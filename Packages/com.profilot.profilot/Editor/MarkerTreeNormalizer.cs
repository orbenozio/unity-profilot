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

        public static void Build(HierarchyFrameDataView view, out string markerTreeJson,
            out string topMarkersJson, out float frameMs)
        {
            frameMs = view.frameTimeMs;
            int rootId = view.GetRootItemID();
            int startId = FindPlayerLoop(view, rootId);
            float keepMs = Mathf.Max(0.01f, frameMs * SelfTimeKeepFractionOfFrame);

            var all = new List<Marker>();
            CollectAll(view, startId, all, 0);
            all.Sort((a, b) => b.SelfMs.CompareTo(a.SelfMs));

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
            WriteNode(view, startId, tree, keepMs, 0);
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
                if (IsEditorOverhead(view, c))
                    continue;
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

        private static void WriteNode(HierarchyFrameDataView view, int id, StringBuilder sb, float keepMs, int depth)
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

            // Rank children by self time so the heaviest survive the top-N cut.
            children.Sort((a, b) =>
                view.GetItemColumnDataAsFloat(b, HierarchyFrameDataView.columnSelfTime)
                    .CompareTo(view.GetItemColumnDataAsFloat(a, HierarchyFrameDataView.columnSelfTime)));

            var kept = new List<int>();
            double cutSelf = 0;
            int cutCount = 0;
            foreach (int c in children)
            {
                if (IsEditorOverhead(view, c))
                    continue;

                float cSelf = view.GetItemColumnDataAsFloat(c, HierarchyFrameDataView.columnSelfTime);
                float cGc = view.GetItemColumnDataAsFloat(c, HierarchyFrameDataView.columnGcMemory);
                bool interesting = cSelf >= keepMs || cGc > 0f;

                if (kept.Count < TopChildrenPerLevel && interesting)
                {
                    kept.Add(c);
                }
                else
                {
                    cutSelf += cSelf;
                    cutCount++;
                }
            }

            if (kept.Count > 0 || cutCount > 0)
            {
                sb.Append(",\"children\":[");
                for (int i = 0; i < kept.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteNode(view, kept[i], sb, keepMs, depth + 1);
                }
                if (cutCount > 0)
                {
                    if (kept.Count > 0) sb.Append(',');
                    sb.Append("{\"name\":\"<other>\",\"selfTimeMs\":").Append(Json.Num(cutSelf))
                      .Append(",\"collapsed\":").Append(Json.Num(cutCount)).Append('}');
                }
                sb.Append(']');
            }

            sb.Append('}');
        }
    }
}
