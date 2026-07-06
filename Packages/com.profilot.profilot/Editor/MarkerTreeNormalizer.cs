using System.Collections.Generic;
using System.Text;
using UnityEditor.Profiling;
using UnityEngine;

namespace Profilot.Editor
{
    /// <summary>
    /// Turns a HierarchyFrameDataView into the trimmed markerTree and the flat topMarkers
    /// list of the event record (SPEC.md section 14). Per the Phase 0.5 finding it drills
    /// into the PlayerLoop subtree (the game) and ignores EditorLoop (editor idle overhead),
    /// and applies the trimming rule so a frame with thousands of markers stays small:
    /// keep a child if its self time is over ~1% of the frame or it allocated, then keep the
    /// top-N per level, cap depth, and fold the rest into a synthetic "&lt;other&gt;" node.
    /// </summary>
    internal static class MarkerTreeNormalizer
    {
        private const float SelfTimeKeepFractionOfFrame = 0.01f; // 1%
        private const int TopChildrenPerLevel = 8;
        private const int MaxDepth = 6;
        private const int TopMarkersCount = 12;

        private struct Marker
        {
            public string Name;
            public float SelfMs;
            public float TotalMs;
            public float GcBytes;
            public int Calls;
        }

        // Just the two columns WriteNode needs to rank and collapse a level's children, fetched
        // once per child. Reading a column is a managed->native call into the frame view, so we
        // avoid re-reading self/alloc in both the sort comparator and the collapse loop.
        private struct ChildRow
        {
            public int Id;
            public float SelfMs;
            public float GcBytes;
        }

        public static void Build(HierarchyFrameDataView view, bool rankByAlloc, out string markerTreeJson,
            out string topMarkersJson, out string dominantMarker, out float frameMs, out float cpuMs,
            out float frameGcBytes)
        {
            frameMs = view.frameTimeMs;
            int rootId = view.GetRootItemID();
            int startId = FindPlayerLoop(view, rootId);

            // Whole-frame GC alloc, read at the ROOT (not startId/PlayerLoop) on purpose: it must
            // match the scope of counters.gcAllocBytes, which the tripwire samples from the
            // "GC Allocated In Frame" recorder - a whole-frame total. Reading it at PlayerLoop
            // would systematically undercount versus that counter and show a false mismatch every
            // time. When the two DO disagree, the captured frame and the trip frame differ
            // (frameIndexDelta), or the alloc is diffuse churn no single marker owns - either way
            // the reader must not treat the markerTree total as the frame total.
            frameGcBytes = view.GetItemColumnDataAsFloat(rootId, HierarchyFrameDataView.columnGcMemory);

            // Total main-thread CPU time under PlayerLoop (the game's work). When this is far
            // below the frame time, the main thread spent the frame WAITING - VSync, GPU
            // present, an idle stall - not doing fixable CPU work. The capture uses this to
            // drop off-CPU false-positive hitches, whose off-thread wait markers never appear
            // in this PlayerLoop tree at all (SPEC.md M4, NG5).
            cpuMs = view.GetItemColumnDataAsFloat(startId, HierarchyFrameDataView.columnTotalTime);

            float keepMs = Mathf.Max(0.01f, frameMs * SelfTimeKeepFractionOfFrame);

            var all = new List<Marker>();
            CollectAll(view, startId, all, 0);

            // Rank topMarkers by the trigger's own dimension: a gc_spike is explained by the
            // marker that allocated (often tiny self time), a hitch by the marker that spent
            // the time. Sorting both by self time buried the real allocator (SPEC.md phase 3).
            if (rankByAlloc)
                all.Sort((a, b) => b.GcBytes.CompareTo(a.GcBytes));
            else
                all.Sort((a, b) => b.SelfMs.CompareTo(a.SelfMs));

            dominantMarker = all.Count > 0 ? all[0].Name : string.Empty;

            var top = new StringBuilder();
            top.Append('[');
            int n = Mathf.Min(TopMarkersCount, all.Count);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) top.Append(',');
                WriteMarkerSummary(top, all[i]);
            }
            top.Append(']');
            topMarkersJson = top.ToString();

            var tree = new StringBuilder();
            WriteNode(view, startId, tree, keepMs, 0, rankByAlloc);
            markerTreeJson = tree.ToString();
        }

        private static int FindPlayerLoop(HierarchyFrameDataView view, int rootId)
        {
            var children = new List<int>();
            view.GetItemChildren(rootId, children);
            foreach (int id in children)
            {
                if (view.GetItemName(id) == "PlayerLoop")
                    return id;
            }
            // Fallback (e.g. a build, or a frame without PlayerLoop): normalize the whole
            // frame; EditorLoop is still filtered out per-node below.
            return rootId;
        }

        private static bool IsEditorOverhead(HierarchyFrameDataView view, int id)
        {
            return view.GetItemName(id) == "EditorLoop";
        }

        /// <summary>
        /// Hard noise: not user code AND its subtree is irrelevant, so CollectAll prunes it
        /// entirely (does not descend). Profilot's own markers, the allocation/JIT machinery,
        /// and editor-only work (stripped in a real build anyway, SPEC.md NG3). Calibration
        /// from live dogfooding + the golden harness (SPEC.md section 17, phase 3).
        /// </summary>
        private static bool IsSkipSubtree(string name)
        {
            if (string.IsNullOrEmpty(name))
                return true;
            // Our own assemblies only - must not catch user code in a "Profilot*" namespace
            // (e.g. the ProfilotDemo sample), which is exactly the code we want to surface.
            if (name.Contains("Profilot.Runtime") || name.Contains("Profilot.Editor"))
                return true;
            // Allocation / collection machinery: GC.Collect is the collector pausing the
            // frame, not the code that caused the garbage - the responsible allocator is the
            // marker to surface, so prune the machinery from ranking.
            if (name == "Mono.JIT" || name == "GC.Alloc" || name == "GC.Collect" ||
                name == "LogStringToConsole")
                return true;
            if (name.Contains("StackTraceUtility"))
                return true;
            // Editor-only work (EditorLoop, EditorConnection, EditorResources, AssetDatabase,
            // GUIUtility, ...). Matching "Editor" is broad, but these markers do not exist in a
            // development build.
            if (name.Contains("Editor") || name.Contains("AssetDatabase") || name.Contains("GUIUtility"))
                return true;
            return false;
        }

        /// <summary>
        /// Excluded from ranking but NOT from the walk: CollectAll still descends into these,
        /// because the responsible user marker sits underneath them. Two kinds (golden-harness
        /// findings, SPEC.md phase 3):
        ///   - Generic PlayerLoop phases (UpdateScene, BehaviourUpdate, ...): containers whose
        ///     inclusive time/alloc just bubbles up their children, so ranking on them would
        ///     surface a phase name instead of the user method that actually allocated/stalled.
        ///   - GPU / present / vsync waits: the main thread is idle waiting for the GPU or
        ///     vsync, not doing the developer's work; a frame dominated by these is a false
        ///     hitch (M4), not a CPU stall to fix.
        /// When every rankable marker is filtered out (e.g. a pure GPU-wait frame), the
        /// dominant marker comes back empty and the capture drops the event.
        /// </summary>
        private static bool IsExcludedFromRanking(string name)
        {
            if (string.IsNullOrEmpty(name))
                return true;

            // GPU / present / vsync / semaphore waits - main thread idle, not user CPU work.
            if (name.Contains("WaitForGPU") || name.Contains("WaitForLastPresentation") ||
                name.Contains("WaitForTargetFPS") || name.Contains("Gfx.WaitFor") ||
                name.Contains("GfxDeviceD3D") || name.Contains("Gfx.PresentFrame") ||
                name.Contains("WaitForPresent") || name.Contains("Present.WaitForJobGroupID") ||
                name.Contains("Semaphore.WaitForSignal") || name.Contains("VSync"))
                return true;

            // Generic PlayerLoop structural phases (exact names - a user method shows as
            // "Class.Update", never bare "Update", so these do not catch user code).
            switch (name)
            {
                case "PlayerLoop":
                case "UpdateScene":
                case "BehaviourUpdate":
                case "LateBehaviourUpdate":
                case "FixedBehaviourUpdate":
                case "PreUpdate":
                case "PreLateUpdate":
                case "Update":
                case "FixedUpdate":
                case "Initialization":
                case "EarlyUpdate":
                    return true;
            }
            if (name.Contains("ScriptRunBehaviour") || name.Contains("ScriptRunDelayed") ||
                name.StartsWith("Update.") || name.StartsWith("PreUpdate.") ||
                name.StartsWith("PreLateUpdate.") || name.StartsWith("FixedUpdate.") ||
                name.StartsWith("EarlyUpdate.") || name.StartsWith("Initialization."))
                return true;

            return false;
        }

        private static Marker Read(HierarchyFrameDataView view, int id)
        {
            return new Marker
            {
                Name = view.GetItemName(id),
                SelfMs = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnSelfTime),
                TotalMs = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime),
                GcBytes = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnGcMemory),
                Calls = (int)view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnCalls)
            };
        }

        private static void CollectAll(HierarchyFrameDataView view, int id, List<Marker> acc, int depth)
        {
            if (depth > MaxDepth)
                return;

            var children = new List<int>();
            view.GetItemChildren(id, children);
            foreach (int c in children)
            {
                string name = view.GetItemName(c);

                // Hard noise: prune the whole subtree (nothing useful under it).
                if (IsSkipSubtree(name))
                    continue;

                // Structural phases / GPU waits: do not rank them, but still descend - the
                // responsible user marker lives underneath (SPEC.md phase 3, golden harness).
                if (!IsExcludedFromRanking(name))
                    acc.Add(Read(view, c));

                CollectAll(view, c, acc, depth + 1);
            }
        }

        private static void WriteMarkerSummary(StringBuilder sb, Marker m)
        {
            sb.Append('{');
            sb.Append("\"name\":").Append(Json.Str(m.Name));
            sb.Append(",\"selfTimeMs\":").Append(Json.Num(m.SelfMs));
            sb.Append(",\"totalTimeMs\":").Append(Json.Num(m.TotalMs));
            sb.Append(",\"gcAllocBytes\":").Append(Json.Num(m.GcBytes));
            sb.Append(",\"calls\":").Append(Json.Num(m.Calls));
            sb.Append('}');
        }

        private static void WriteNode(HierarchyFrameDataView view, int id, StringBuilder sb, float keepMs,
            int depth, bool rankByAlloc)
        {
            Marker m = Read(view, id);
            sb.Append('{');
            sb.Append("\"name\":").Append(Json.Str(m.Name));
            sb.Append(",\"selfTimeMs\":").Append(Json.Num(m.SelfMs));
            sb.Append(",\"totalTimeMs\":").Append(Json.Num(m.TotalMs));
            sb.Append(",\"gcAllocBytes\":").Append(Json.Num(m.GcBytes));
            sb.Append(",\"calls\":").Append(Json.Num(m.Calls));

            var children = new List<int>();
            if (depth < MaxDepth)
                view.GetItemChildren(id, children);

            // Read each child's self/alloc ONCE (each read is a native call), dropping editor
            // overhead up front, then rank and collapse over the in-memory rows.
            var rows = new List<ChildRow>(children.Count);
            foreach (int c in children)
            {
                if (IsEditorOverhead(view, c))
                    continue;
                rows.Add(new ChildRow
                {
                    Id = c,
                    SelfMs = view.GetItemColumnDataAsFloat(c, HierarchyFrameDataView.columnSelfTime),
                    GcBytes = view.GetItemColumnDataAsFloat(c, HierarchyFrameDataView.columnGcMemory),
                });
            }

            // Rank children by the trigger's own dimension so the ones that survive the top-N cut
            // are the ones that explain the problem: GC alloc for an alloc-ranked event (else a
            // low-self-time allocator gets pushed past the cap and its bytes vanish from the tree),
            // self time otherwise.
            if (rankByAlloc)
                rows.Sort((a, b) => b.GcBytes.CompareTo(a.GcBytes));
            else
                rows.Sort((a, b) => b.SelfMs.CompareTo(a.SelfMs));

            var kept = new List<int>();
            double cutSelf = 0;
            double cutGc = 0;
            int cutCount = 0;
            foreach (ChildRow r in rows)
            {
                bool interesting = r.SelfMs >= keepMs || r.GcBytes > 0f;

                if (kept.Count < TopChildrenPerLevel && interesting)
                {
                    kept.Add(r.Id);
                }
                else
                {
                    cutSelf += r.SelfMs;
                    cutGc += r.GcBytes;
                    cutCount++;
                }
            }

            if (kept.Count > 0 || cutCount > 0)
            {
                sb.Append(",\"children\":[");
                for (int i = 0; i < kept.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteNode(view, kept[i], sb, keepMs, depth + 1, rankByAlloc);
                }
                if (cutCount > 0)
                {
                    if (kept.Count > 0) sb.Append(',');
                    // Carry the collapsed GC alloc too: dropping it silently under-reported the
                    // frame's allocation and made a diffuse-churn frame look like it barely
                    // allocated. Now the reader sees the bytes that live off the ranked branches.
                    sb.Append("{\"name\":\"<other>\",\"selfTimeMs\":").Append(Json.Num(cutSelf))
                      .Append(",\"gcAllocBytes\":").Append(Json.Num(cutGc))
                      .Append(",\"collapsed\":").Append(Json.Num(cutCount)).Append('}');
                }
                sb.Append(']');
            }

            sb.Append('}');
        }
    }
}
