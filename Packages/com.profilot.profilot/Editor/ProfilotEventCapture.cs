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

        // How many recent profiler frames to scan when correlating a trip to its frame.
        // ProfilerDriver counts editor frames too, so its lastFrameIndex is not the frame
        // that tripped; we drain on the next editor update, so the real frame is only a few
        // back. We pick by signal (the frame whose GC alloc / time matches the trigger),
        // not by index, which is robust to that offset (SPEC.md section 15, ring buffer).
        private const int FrameSearchWindow = 30;

        private static readonly List<TripSignal> Buffer = new List<TripSignal>();
        private static string _sessionId = "editor";

        static ProfilotEventCapture()
        {
            EditorApplication.update += OnUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredPlayMode)
                return;

            _sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);

            // The full-frame fetch needs the profiler to be recording. The cheap tripwire
            // (counters) does not, but ProfilerDriver only has frames if profiling is on.
            // In the Editor dev scenario this overhead is acceptable (SPEC.md NG3).
            Profiler.enabled = true;

            ProfilotTripChannel.Clear();
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
            int requestedFrameIndex = ProfilerDriver.lastFrameIndex;
            int frameIndex = PickBestFrame(signal, requestedFrameIndex);
            string eventId = $"evt_{frameIndex}_{signal.Type}";

            string json;
            try
            {
                json = BuildEventJson(eventId, frameIndex, requestedFrameIndex, signal);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Profilot] Failed to capture event {eventId}: {e.Message}");
                return;
            }

            ProfilotEventStore.Write(eventId, json, BuildLatestPointer(eventId));
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

        private static string BuildEventJson(string eventId, int frameIndex, int requestedFrameIndex, in TripSignal signal)
        {
            string markerTreeJson = "null";
            string topMarkersJson = "[]";
            string status = "ok";

            HierarchyFrameDataView view = null;
            if (frameIndex >= 0)
            {
                view = ProfilerDriver.GetHierarchyFrameDataView(
                    frameIndex, 0,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnTotalTime, false);
            }

            try
            {
                if (view != null && view.valid)
                {
                    MarkerTreeNormalizer.Build(view, out markerTreeJson, out topMarkersJson, out _);
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

            string severity = Severity(signal.Value, signal.Budget);
            int count = signal.RepeatCount + 1;

            var sb = new StringBuilder(1024);
            sb.Append('{');
            sb.Append("\"schemaVersion\":").Append(Json.Str(SchemaVersion));
            sb.Append(",\"eventId\":").Append(Json.Str(eventId));
            sb.Append(",\"status\":").Append(Json.Str(status));
            sb.Append(",\"reviewStatus\":").Append(Json.Str("open"));
            sb.Append(",\"stale\":false");
            sb.Append(",\"capturedAt\":").Append(Json.Str(DateTime.UtcNow.ToString("o")));
            sb.Append(",\"sessionId\":").Append(Json.Str(_sessionId));
            sb.Append(",\"unityVersion\":").Append(Json.Str(Application.unityVersion));
            sb.Append(",\"frameIndex\":").Append(Json.Num(frameIndex));
            sb.Append(",\"requestedFrameIndex\":").Append(Json.Num(requestedFrameIndex));
            sb.Append(",\"frameIndexDelta\":").Append(Json.Num(requestedFrameIndex - frameIndex));

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
            sb.Append("\"count\":").Append(Json.Num(count));
            sb.Append(",\"firstSeenFrame\":").Append(Json.Num(signal.FrameCount));
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
