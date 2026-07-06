using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

namespace Profilot.Editor
{
    /// <summary>
    /// Phase 2 editor capture service (SPEC.md sections 12, 14, 15). Drains the in-memory
    /// trip channel on the very next EditorApplication.update after a trip, fetches the full
    /// problem frame via ProfilerDriver (time-sensitive, to beat the ring-buffer race),
    /// normalizes it, and writes an event record to the file-based store the CLI reads.
    /// No LLM call - interpretation lives in Claude Code.
    /// </summary>
    [InitializeOnLoad]
    internal static class ProfilotEventCapture
    {
        private const string SchemaVersion = "1";

        // A frame_hitch whose PlayerLoop CPU work explains less than this fraction of the
        // frame time was spent waiting (VSync / GPU present / idle), not doing fixable CPU
        // work - dropped as an off-CPU false positive (SPEC.md M4/M6, NG5).
        private const float OffCpuFraction = 0.5f;

        // How many recent profiler frames to scan when correlating a trip to its frame.
        // ProfilerDriver counts editor frames too, so its lastFrameIndex is not the frame
        // that tripped; we drain on the next editor update, so the real frame is only a few
        // back. We pick by signal (the frame whose GC alloc / time matches the trigger),
        // not by index, which is robust to that offset (SPEC.md section 15, ring buffer).
        // Each scanned frame builds a HierarchyFrameDataView, so this is the per-trip cost -
        // kept small (the observed frameIndexDelta is well under this) so a trip is cheap and
        // does not produce a periodic capture hitch.
        private const int FrameSearchWindow = 10;

        private static readonly List<TripSignal> Buffer = new List<TripSignal>();
        private static string _sessionId = "editor";

        /// <summary>The id of the current Play run, so the window can mark which events are from
        /// this run versus an earlier one.</summary>
        internal static string CurrentSessionId => _sessionId;

        // Session dedup (SPEC.md section 15): repeats of the same problem (type + dominant
        // marker) fold into one rolling record with a growing count, instead of one file per
        // frame. Kept in memory so we never have to parse the JSON back; the file is just
        // overwritten with the running total. Reset each time Play Mode is entered.
        private sealed class Accumulator
        {
            public int Count;
            public int FirstSeenFrame;
        }

        private static readonly Dictionary<string, Accumulator> Dedup = new Dictionary<string, Accumulator>();

        // reviewStatus set by the user from the window. Remembered so that an ongoing spike
        // of the same problem does not reset it back to "open" when the record is overwritten.
        private static readonly Dictionary<string, string> Reviewed = new Dictionary<string, string>();

        /// <summary>Called by the window when the user marks an event (SPEC.md JTBD-8).</summary>
        internal static void MarkReviewed(string eventId, string status)
        {
            // "open" is the absence of a decision (Reopen) - drop it from the persisted map so
            // notifications resume; any other status is remembered across sessions.
            if (status == "open")
                Reviewed.Remove(eventId);
            else
                Reviewed[eventId] = status;

            SaveReviews();
        }

        static ProfilotEventCapture()
        {
            EditorApplication.update += OnUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            LoadReviews();
        }

        [System.Serializable] private class ReviewRecord { public string id; public string status; }
        [System.Serializable] private class ReviewFile { public List<ReviewRecord> items = new List<ReviewRecord>(); }

        private static void LoadReviews()
        {
            Reviewed.Clear();
            string json = ProfilotEventStore.ReadReviews();
            if (string.IsNullOrEmpty(json))
                return;
            try
            {
                ReviewFile f = JsonUtility.FromJson<ReviewFile>(json);
                if (f?.items != null)
                    foreach (ReviewRecord it in f.items)
                        if (!string.IsNullOrEmpty(it.id) && !string.IsNullOrEmpty(it.status))
                            Reviewed[it.id] = it.status;
            }
            catch { /* a corrupt reviews file should never break capture */ }
        }

        private static void SaveReviews()
        {
            var f = new ReviewFile();
            foreach (var kv in Reviewed)
                f.items.Add(new ReviewRecord { id = kv.Key, status = kv.Value });
            try { ProfilotEventStore.WriteReviews(JsonUtility.ToJson(f)); }
            catch { /* best effort */ }
        }

        // A problem the user has given feedback on (reviewed or not-an-issue) is muted: no more
        // notifications until they Reopen it. Notifications are only for open, un-triaged
        // problems.
        private static bool IsMuted(string eventId)
        {
            return Reviewed.TryGetValue(eventId, out string s) && s != "open";
        }

        /// <summary>Live review status for an event (from the persisted, cross-run decision map),
        /// so the window reflects a mark instantly on every run's copy.</summary>
        internal static string ReviewStatusOf(string eventId)
        {
            return Reviewed.TryGetValue(eventId, out string s) ? s : "open";
        }

        private const string DeepCaptureKey = "Profilot.DeepCapture";

        /// <summary>
        /// Deep capture keeps the Unity Profiler recording during Play so a trip can fetch the
        /// full marker tree and map the problem to code - the core value of the tool. ON by
        /// default: turning the profiler on was measured to add negligible per-frame cost
        /// (~0.1ms in a marker-rich scene), so it is NOT the source of the Editor slowdown that
        /// mattered - that was the per-trip capture cost and event accumulation, fixed
        /// separately. Turn it OFF only if you want maximum Editor speed and are willing to give
        /// up marker->code mapping (you still get counter-only events). Persisted per-user.
        /// </summary>
        public static bool DeepCapture
        {
            get { return EditorPrefs.GetBool(DeepCaptureKey, true); }
            set
            {
                EditorPrefs.SetBool(DeepCaptureKey, value);
                // Apply immediately so toggling mid-Play takes effect from now on.
                if (EditorApplication.isPlaying)
                    Profiler.enabled = value;
            }
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredPlayMode)
                return;

            // Human-readable, path-safe run id: the local date + time the run started. It is
            // both the sessionId shown in the window/CLI and the name of this run's folder, so
            // it must avoid ':' (illegal in a Windows path).
            _sessionId = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            ProfilotEventStore.CurrentRun = _sessionId;

            // Age-based retention: drop run folders older than the configured cap (if enabled),
            // so results from long-gone runs do not pile up.
            ProfilotConfig cfg = ProfilotConfigStore.Load();
            if (cfg.pruneEnabled)
                ProfilotEventStore.PruneByAge(cfg.retentionDays);

            // Arm the profiler for marker->code mapping (on by default - it was measured to
            // add negligible per-frame cost). Off only if the user opted into counter-only mode
            // for maximum Editor speed.
            Profiler.enabled = DeepCapture;

            ProfilotTripChannel.Clear();
            Dedup.Clear();
            // Reviewed is NOT cleared - review decisions persist across sessions (loaded from
            // disk in the static ctor, saved on every change), so a muted problem stays muted.
        }

        private static void OnUpdate()
        {
            if (!EditorApplication.isPlaying)
                return;

            Buffer.Clear();
            if (ProfilotTripChannel.Drain(Buffer) == 0)
                return;

            foreach (TripSignal signal in Buffer)
                Capture(signal);
        }

        private static void Capture(in TripSignal signal)
        {
            // Only do the expensive frame fetch + 30-frame scan when deep capture is armed.
            // Otherwise this is the cheap counter-only path: no profiler recording, no marker
            // tree, no per-trip cost - just the counter snapshot the tripwire already sampled.
            bool deep = Profiler.enabled;

            int requestedFrameIndex = ProfilerDriver.lastFrameIndex;
            int frameIndex = deep ? PickBestFrame(signal, requestedFrameIndex) : requestedFrameIndex;

            // Fetch + normalize first: the dominant marker is part of the dedup key.
            string markerTreeJson = "null";
            string topMarkersJson = "[]";
            string dominantMarker = string.Empty;
            string status = deep ? "ok" : "counters_only";
            float frameMs = 0f;
            float cpuMs = 0f;
            float frameGcBytes = 0f;

            if (deep)
            {
                HierarchyFrameDataView view = frameIndex >= 0
                    ? ProfilerDriver.GetHierarchyFrameDataView(frameIndex, 0,
                        HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                        HierarchyFrameDataView.columnTotalTime, false)
                    : null;
                try
                {
                    if (view != null && view.valid)
                    {
                        MarkerTreeNormalizer.Build(view, signal.Type == "gc_spike",
                            out markerTreeJson, out topMarkersJson, out dominantMarker, out frameMs, out cpuMs,
                            out frameGcBytes);
                    }
                    else
                    {
                        // The frame was pushed out of the profiler ring buffer before we could
                        // read it (SPEC.md section 15). The counter snapshot still describes it.
                        status = "error";
                    }
                }
                finally
                {
                    view?.Dispose();
                }
            }

            // Drop off-CPU false-positive hitches (only when the frame was read cleanly - an
            // error frame keeps its counter snapshot). Two shapes of the same non-issue:
            //   - no user marker to blame at all (only waits / structural phases survived), or
            //   - the main thread's PlayerLoop CPU work explains less than half the frame,
            //     so the rest was spent waiting on VSync / the GPU / an idle stall.
            // Neither is a CPU stall to fix (SPEC.md M4/M6, NG5). The off-thread wait markers
            // never appear in the PlayerLoop tree, so this ratio - not a marker match - is what
            // catches the common VSync / GPU-present false positive.
            if (signal.Type == "frame_hitch" && status == "ok" &&
                (string.IsNullOrEmpty(dominantMarker) || (frameMs > 0f && cpuMs < frameMs * OffCpuFraction)))
                return;

            // Stable id per problem (type + dominant marker) so repeats overwrite one file.
            string eventId = $"evt_{signal.Type}_{Slug(dominantMarker)}";

            bool isNewProblem = !Dedup.TryGetValue(eventId, out Accumulator acc);
            if (isNewProblem)
            {
                acc = new Accumulator { Count = 0, FirstSeenFrame = signal.FrameCount };
                Dedup[eventId] = acc;
            }
            acc.Count += signal.RepeatCount + 1;

            string likelyCause = LikelyCause(dominantMarker);

            string json;
            try
            {
                json = BuildEventJson(eventId, frameIndex, requestedFrameIndex, signal, status,
                    markerTreeJson, topMarkersJson, cpuMs, frameGcBytes, likelyCause, acc);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Profilot] Failed to capture event {eventId}: {e.Message}");
                return;
            }

            ProfilotEventStore.Write(eventId, json, BuildLatestPointer(eventId));

            // Proactive, cheap alert - only the first time a distinct problem is caught this
            // session (repeats already fold into the dedup count), never per frame, no LLM, and
            // never for a problem the user has muted via Reviewed / Not-an-issue.
            if (isNewProblem && !IsMuted(eventId))
                ProfilotNotifier.OnProblemCaught(eventId, signal.Type, dominantMarker, acc.Count);
        }

        /// <summary>
        /// Filename-safe key from the dominant marker: drops the assembly prefix and the
        /// "() [Invoke]" decoration, keeps the method-ish tail, so the dedup id is stable
        /// and readable (e.g. "GarbageGenerator.Update").
        /// </summary>
        private static string Slug(string marker)
        {
            if (string.IsNullOrEmpty(marker))
                return "unknown";

            string s = marker.Replace("() [Invoke]", string.Empty).Replace("[Invoke]", string.Empty).Replace("()", string.Empty);
            int bang = s.LastIndexOf('!');
            if (bang >= 0) s = s.Substring(bang + 1);
            int colons = s.LastIndexOf("::", StringComparison.Ordinal);
            if (colons >= 0) s = s.Substring(colons + 2);

            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '.' || c == '_')
                    sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_')
                    sb.Append('_');
            }

            string slug = sb.ToString().Trim('_', '.');
            return slug.Length > 0 ? slug : "unknown";
        }

        /// <summary>
        /// A coarse cause hint for the diagnosis layer, derived from the dominant marker. A frame
        /// whose dominant marker is the garbage collector pausing the frame
        /// (GarbageCollector.CollectIncremental / GC.Collect) is the SYMPTOM of sustained
        /// allocation churn, not a distinct bug - the fixable code is whatever allocated, which a
        /// gc_spike event (ranked by alloc) surfaces. Tagging it "gc_pressure" lets the diagnosis
        /// point the user at the gc_spike instead of trying to map the collector itself. Empty
        /// when there is no confident hint.
        /// </summary>
        private static string LikelyCause(string dominantMarker)
        {
            if (string.IsNullOrEmpty(dominantMarker))
                return string.Empty;

            if (dominantMarker.StartsWith("GarbageCollector", StringComparison.Ordinal) ||
                dominantMarker.IndexOf("CollectIncremental", StringComparison.Ordinal) >= 0 ||
                dominantMarker == "GC.Collect")
                return "gc_pressure";

            return string.Empty;
        }

        /// <summary>
        /// Finds the frame that actually carries the anomaly. Scans recent profiler frames
        /// and picks the one whose signal is strongest: GC allocation for gc_spike, frame
        /// time otherwise. This correlates the runtime trip to the right profiler frame
        /// despite the editor/player frame-index offset, so the offending marker is in the
        /// captured tree rather than a later idle frame.
        /// </summary>
        private static int PickBestFrame(in TripSignal signal, int latest)
        {
            if (latest < 0)
                return latest;

            bool byAlloc = signal.Type == "gc_spike";
            int first = ProfilerDriver.firstFrameIndex;
            int from = Mathf.Max(first, latest - FrameSearchWindow);

            int best = latest;
            double bestScore = double.NegativeInfinity;
            for (int f = latest; f >= from; f--)
            {
                HierarchyFrameDataView view = ProfilerDriver.GetHierarchyFrameDataView(
                    f, 0, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnTotalTime, false);
                try
                {
                    if (view == null || !view.valid)
                        continue;

                    double score = byAlloc
                        ? view.GetItemColumnDataAsFloat(view.GetRootItemID(), HierarchyFrameDataView.columnGcMemory)
                        : view.frameTimeMs;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = f;
                    }
                }
                finally
                {
                    view?.Dispose();
                }
            }

            return best;
        }

        private static string BuildEventJson(string eventId, int frameIndex, int requestedFrameIndex,
            in TripSignal signal, string status, string markerTreeJson, string topMarkersJson, float cpuMs,
            float frameGcBytes, string likelyCause, Accumulator acc)
        {
            string severity = Severity(signal.Value, signal.Budget);
            string review = Reviewed.TryGetValue(eventId, out string r) ? r : "open";

            var sb = new StringBuilder(1024);
            sb.Append('{');
            sb.Append("\"schemaVersion\":").Append(Json.Str(SchemaVersion));
            sb.Append(",\"eventId\":").Append(Json.Str(eventId));
            sb.Append(",\"status\":").Append(Json.Str(status));
            sb.Append(",\"reviewStatus\":").Append(Json.Str(review));
            sb.Append(",\"stale\":false");
            sb.Append(",\"capturedAt\":").Append(Json.Str(DateTime.UtcNow.ToString("o")));
            sb.Append(",\"sessionId\":").Append(Json.Str(_sessionId));
            sb.Append(",\"unityVersion\":").Append(Json.Str(Application.unityVersion));
            sb.Append(",\"frameIndex\":").Append(Json.Num(frameIndex));
            sb.Append(",\"requestedFrameIndex\":").Append(Json.Num(requestedFrameIndex));
            sb.Append(",\"frameIndexDelta\":").Append(Json.Num(requestedFrameIndex - frameIndex));

            // Main-thread PlayerLoop CPU time for this frame. Compare against counters.frameTimeMs:
            // when cpuTimeMs is far below the frame time, the frame was spent waiting off-CPU
            // (VSync / GPU present / idle), not in fixable code (SPEC.md M4, NG5).
            sb.Append(",\"cpuTimeMs\":").Append(Json.Num(cpuMs));

            // Whole-frame GC alloc of the CAPTURED frame (from the marker tree root). counters
            // .gcAllocBytes is the tripwire's snapshot at trip time; when the two disagree the
            // captured frame and the trip frame differ (frameIndexDelta), so the markerTree total
            // must not be read as the trip's allocation.
            sb.Append(",\"frameGcAllocBytes\":").Append(Json.Num(frameGcBytes));

            // Coarse cause hint (may be empty). "gc_pressure" = the frame's dominant marker is the
            // collector pausing the frame, i.e. a symptom of allocation churn - map the allocator
            // (a gc_spike event), not the collector.
            sb.Append(",\"likelyCause\":").Append(Json.Str(likelyCause));

            sb.Append(",\"trigger\":{");
            sb.Append("\"type\":").Append(Json.Str(signal.Type));
            sb.Append(",\"severity\":").Append(Json.Str(severity));
            sb.Append(",\"metric\":").Append(Json.Str(signal.Metric));
            sb.Append(",\"value\":").Append(Json.Num(signal.Value));
            sb.Append(",\"budget\":").Append(Json.Num(signal.Budget));
            sb.Append('}');

            sb.Append(",\"counters\":{");
            sb.Append("\"frameTimeMs\":").Append(Json.Num(signal.FrameTimeMs));
            sb.Append(",\"gcAllocBytes\":").Append(Json.Num(signal.GcAllocBytes));
            sb.Append(",\"drawCalls\":").Append(Json.Num(signal.DrawCalls));
            sb.Append('}');

            sb.Append(",\"markerTree\":").Append(markerTreeJson);
            sb.Append(",\"topMarkers\":").Append(topMarkersJson);

            sb.Append(",\"dedup\":{");
            sb.Append("\"count\":").Append(Json.Num(acc.Count));
            sb.Append(",\"firstSeenFrame\":").Append(Json.Num(acc.FirstSeenFrame));
            sb.Append(",\"lastSeenFrame\":").Append(Json.Num(signal.FrameCount));
            sb.Append('}');

            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildLatestPointer(string eventId)
        {
            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"schemaVersion\":").Append(Json.Str(SchemaVersion));
            sb.Append(",\"eventId\":").Append(Json.Str(eventId));
            sb.Append(",\"run\":").Append(Json.Str(_sessionId));
            sb.Append(",\"file\":").Append(Json.Str(eventId + ".json"));
            sb.Append(",\"capturedAt\":").Append(Json.Str(DateTime.UtcNow.ToString("o")));
            sb.Append('}');
            return sb.ToString();
        }

        private static string Severity(double value, double budget)
        {
            if (budget > 0)
            {
                double ratio = value / budget;
                if (ratio >= 3.0) return "high";
                if (ratio >= 1.5) return "medium";
                return "low";
            }

            // gc_spike against a budget of 0: any allocation trips it, so rank by absolute
            // size (placeholder thresholds, calibrated in Phase 3).
            if (value >= 1_000_000) return "high";
            if (value >= 100_000) return "medium";
            return "low";
        }
    }
}
