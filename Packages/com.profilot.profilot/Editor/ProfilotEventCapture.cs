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
        // Bump whenever the record's fields change, so the CLI can tell an older record (missing
        // fields) from a newer one it does not know yet. 2 = frameGcAllocBytes, likelyCause,
        // markerTreeDepth, capturedFrameTimeMs, frameMatchesTrigger, writerVersion.
        internal const string SchemaVersion = "2";

        // The package version that wrote a record (writerVersion), read once from the package
        // manifest. "unknown" when the package is not resolved through the Package Manager.
        private static string _writerVersion;
        internal static string WriterVersion
        {
            get
            {
                if (_writerVersion == null)
                {
                    var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ProfilotEventCapture).Assembly);
                    _writerVersion = info != null && !string.IsNullOrEmpty(info.version) ? info.version : "unknown";
                }
                return _writerVersion;
            }
        }

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

        // Review decisions set by the user from the window, keyed by eventId across runs:
        //   "fixed"     - muted, but remembers the trigger value it was marked at. If the problem
        //                 comes back RegressionFactor times worse it turns "regressed" and alerts.
        //   "ignored"   - muted for good (noise), until the user reopens it.
        //   "regressed" - a fixed problem that came back worse; alerts like an open one.
        // Remembered so that an ongoing spike of the same problem does not reset it back to
        // "open" when the record is overwritten.
        private sealed class Review
        {
            public string Status;
            public double Value;
        }

        private const double RegressionFactor = 3.0;
        private static readonly Dictionary<string, Review> Reviewed = new Dictionary<string, Review>();

        /// <summary>Called by the window when the user marks an event (SPEC.md JTBD-8). `value`
        /// is the event's trigger value, the reference a "fixed" mark measures a comeback against.</summary>
        internal static void MarkReviewed(string eventId, string status, double value = 0)
        {
            // "open" is the absence of a decision (Reopen) - drop it from the persisted map so
            // notifications resume; any other status is remembered across sessions.
            if (status == "open")
                Reviewed.Remove(eventId);
            else
                Reviewed[eventId] = new Review { Status = status, Value = value };

            SaveReviews();
        }

        static ProfilotEventCapture()
        {
            EditorApplication.update += OnUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            LoadReviews();
        }

        [System.Serializable] private class ReviewRecord { public string id; public string status; public double value; }
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
                            Reviewed[it.id] = new Review { Status = MigrateStatus(it.status), Value = it.value };
            }
            catch { /* a corrupt reviews file should never break capture */ }
        }

        // Before 0.7 the two buttons were "reviewed" and "not_a_real_issue", and both muted
        // forever. They map onto the new pair; an old "reviewed" has no value, so its first
        // comeback sets the reference instead of alerting.
        private static string MigrateStatus(string status)
        {
            switch (status)
            {
                case "reviewed": return "fixed";
                case "not_a_real_issue": return "ignored";
                default: return status;
            }
        }

        private static void SaveReviews()
        {
            var f = new ReviewFile();
            foreach (var kv in Reviewed)
                f.items.Add(new ReviewRecord { id = kv.Key, status = kv.Value.Status, value = kv.Value.Value });
            try { ProfilotEventStore.WriteReviews(JsonUtility.ToJson(f)); }
            catch { /* best effort */ }
        }

        // Fixed and ignored problems are muted: no more notifications until they regress or the
        // user reopens them. Notifications are only for open, regressed or un-triaged problems.
        private static bool IsMuted(string eventId)
        {
            return Reviewed.TryGetValue(eventId, out Review r) && (r.Status == "fixed" || r.Status == "ignored");
        }

        /// <summary>A "fixed" problem caught again at RegressionFactor times the value it was marked
        /// at turns "regressed" (persisted). Returns true on that transition only.</summary>
        private static bool CheckRegression(string eventId, double value)
        {
            if (!Reviewed.TryGetValue(eventId, out Review r) || r.Status != "fixed")
                return false;

            if (r.Value <= 0)
            {
                // Migrated mark with no reference value: this comeback becomes the reference.
                r.Value = value;
                SaveReviews();
                return false;
            }

            if (value < r.Value * RegressionFactor)
                return false;

            r.Status = "regressed";
            SaveReviews();
            return true;
        }

        /// <summary>Live review status for an event (from the persisted, cross-run decision map),
        /// so the window reflects a mark instantly on every run's copy.</summary>
        internal static string ReviewStatusOf(string eventId)
        {
            return Reviewed.TryGetValue(eventId, out Review r) ? r.Status : "open";
        }

        /// <summary>The trigger value a "fixed" mark was made at (0 when unknown or not fixed).</summary>
        internal static double FixedValueOf(string eventId)
        {
            return Reviewed.TryGetValue(eventId, out Review r) && r.Status == "fixed" ? r.Value : 0;
        }

        /// <summary>How many times worse a fixed problem has to come back to alert again.</summary>
        internal static double RegressionMultiple => RegressionFactor;

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
                    ArmRecording(value);
            }
        }

        // The Profiler window's Record state from before Play, so we can hand it back on exit
        // (null = we did not touch it this run).
        private static bool? _driverWasEnabled;

        // Profiler.enabled alone only arms the runtime side. The Editor keeps frames for
        // ProfilerDriver only while ProfilerDriver.enabled (the Profiler window's Record button)
        // is on, and that is off in a fresh project or in batchmode - every capture then found
        // no frame (frameIndex -1, status "error", no markers). It worked in dogfooding only
        // because Record happened to be on there. So arm both.
        private static void ArmRecording(bool on)
        {
            Profiler.enabled = on;
            if (on && !ProfilerDriver.enabled)
            {
                if (_driverWasEnabled == null)
                    _driverWasEnabled = false;
                ProfilerDriver.enabled = true;
            }
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
            {
                // Leave the Profiler window the way the user had it.
                if (_driverWasEnabled == false)
                    ProfilerDriver.enabled = false;
                _driverWasEnabled = null;
                return;
            }

            if (change != PlayModeStateChange.EnteredPlayMode)
                return;

            // Human-readable, path-safe run id: the local date + time the run started. It is
            // both the sessionId shown in the window/CLI and the name of this run's folder, so
            // it must avoid ':' (illegal in a Windows path).
            _sessionId = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            ProfilotEventStore.CurrentRun = _sessionId;

            // Which package and schema write this store, so `profilot status` can tell the user
            // when the CLI, the package and the project's guide have drifted apart.
            try { ProfilotEventStore.WriteMeta(BuildMeta()); }
            catch { /* best effort - capture does not depend on it */ }

            // Age-based retention: drop run folders older than the configured cap (if enabled),
            // so results from long-gone runs do not pile up.
            ProfilotConfig cfg = ProfilotConfigStore.Load();
            if (cfg.pruneEnabled)
                ProfilotEventStore.PruneByAge(cfg.retentionDays);

            // Arm the profiler for marker->code mapping (on by default - it was measured to
            // add negligible per-frame cost). Off only if the user opted into counter-only mode
            // for maximum Editor speed.
            ArmRecording(DeepCapture);

            ProfilotTripChannel.Clear();
            PendingCaptures.Clear();
            Dedup.Clear();
            // Reviewed is NOT cleared - review decisions persist across sessions (loaded from
            // disk in the static ctor, saved on every change), so a muted problem stays muted.
        }

        // A trip waiting for its profiler frame. The Profiler hands frames to the Editor with a
        // lag, so on the update right after a trip the tripping frame is often not there yet - a
        // single 60 ms stall was captured as a neighbouring 2 ms frame. Each pending trip scans
        // the frames that arrived since its last look, keeps the closest one, and is written as
        // soon as a frame matches, or after MaxCaptureWaitUpdates with the closest it saw.
        private sealed class PendingCapture
        {
            public TripSignal Signal;
            public int Updates;
            public int ScannedTo = -1;
            public int Best = -1;
            public double BestDistance = double.PositiveInfinity;
            public bool Matched;
        }

        private const int MaxCaptureWaitUpdates = 10;
        private static readonly List<PendingCapture> PendingCaptures = new List<PendingCapture>();

        private static void OnUpdate()
        {
            if (!EditorApplication.isPlaying)
                return;

            Buffer.Clear();
            ProfilotTripChannel.Drain(Buffer);
            foreach (TripSignal signal in Buffer)
                PendingCaptures.Add(new PendingCapture { Signal = signal });

            if (PendingCaptures.Count == 0)
                return;

            // Only scan frames when deep capture is armed. Otherwise this is the cheap
            // counter-only path: no profiler recording, no marker tree, no per-trip cost - just
            // the counter snapshot the tripwire already sampled.
            bool deep = Profiler.enabled;
            for (int i = 0; i < PendingCaptures.Count; i++)
            {
                PendingCapture p = PendingCaptures[i];
                if (deep)
                {
                    ScanNewFrames(p);
                    if (!p.Matched && ++p.Updates < MaxCaptureWaitUpdates)
                        continue;
                }

                PendingCaptures.RemoveAt(i--);
                Capture(p.Signal, deep && p.Best >= 0 ? p.Best : ProfilerDriver.lastFrameIndex, deep && p.Matched);
            }
        }

        private static void Capture(in TripSignal signal, int frameIndex, bool frameMatches)
        {
            bool deep = Profiler.enabled;
            int requestedFrameIndex = ProfilerDriver.lastFrameIndex;

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

            // Before the record is built, so it already carries "regressed".
            bool regressed = CheckRegression(eventId, signal.Value);

            string likelyCause = LikelyCause(dominantMarker);

            string json;
            try
            {
                json = BuildEventJson(eventId, frameIndex, requestedFrameIndex, signal, status,
                    markerTreeJson, topMarkersJson, cpuMs, frameMs, frameGcBytes, deep && frameMatches,
                    likelyCause, acc);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Profilot] Failed to capture event {eventId}: {e.Message}");
                return;
            }

            ProfilotEventStore.Write(eventId, json, BuildLatestPointer(eventId));

            // Proactive, cheap alert - only the first time a distinct problem is caught this
            // session (repeats already fold into the dedup count), never per frame, no LLM, and
            // never for a problem the user has muted via Fixed / Ignore. A fixed problem that came
            // back worse alerts once more, on the transition.
            if (regressed || (isNewProblem && !IsMuted(eventId)))
                ProfilotNotifier.OnProblemCaught(eventId, signal.Type, dominantMarker, acc.Count, regressed);
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

        // A scanned frame whose primary metric is within this relative distance of the trip's
        // value is taken as the trip's own frame (frameMatchesTrigger in the record).
        private const double FrameMatchTolerance = 0.5;

        // The tripwire's "GC Allocated In Frame" counter also counts what the Editor itself
        // allocates around the player loop (~15 KB a frame), which the captured frame's tree does
        // not include. Differences up to this much are treated as that offset, not a mismatch -
        // otherwise every small leak looked like a different frame.
        private const double GcMatchSlackBytes = 64 * 1024;

        // |a - b| beyond `slack`, relative to the larger of the two: 0 = equal, 1 = far apart.
        private static double RelDistance(double a, double b, double slack = 0.0)
        {
            double m = Math.Max(Math.Abs(a), Math.Abs(b));
            return m < 1e-9 ? 0.0 : Math.Max(0.0, Math.Abs(a - b) - slack) / m;
        }

        /// <summary>
        /// Looks for the frame that tripped among the profiler frames that arrived since the last
        /// look (the first look also covers the FrameSearchWindow before it). Keeps the frame
        /// whose counters are CLOSEST to the trip's snapshot - GC allocation first for gc_spike,
        /// frame time first otherwise, the other as a tiebreaker. This correlates the runtime trip
        /// to its profiler frame despite the editor/player frame-index offset. It used to pick the
        /// LARGEST frame in the window instead, so a 14 ms / 1.2 MB trip came back with the tree
        /// of a neighbouring 480 ms / 121 MB load frame - wrong marker, wrong event id, wrong
        /// dedup key.
        /// </summary>
        private static void ScanNewFrames(PendingCapture p)
        {
            int latest = ProfilerDriver.lastFrameIndex;
            if (latest < 0)
                return;

            int first = ProfilerDriver.firstFrameIndex;
            int from = p.ScannedTo < 0 ? latest - FrameSearchWindow : p.ScannedTo + 1;
            from = Mathf.Max(first, from);
            p.ScannedTo = latest;

            bool byAlloc = p.Signal.Type == "gc_spike";
            for (int f = latest; f >= from; f--)
            {
                HierarchyFrameDataView view = ProfilerDriver.GetHierarchyFrameDataView(
                    f, 0, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnTotalTime, false);
                try
                {
                    if (view == null || !view.valid)
                        continue;

                    double gc = view.GetItemColumnDataAsFloat(view.GetRootItemID(), HierarchyFrameDataView.columnGcMemory);
                    double gcDistance = RelDistance(gc, p.Signal.GcAllocBytes, GcMatchSlackBytes);
                    double timeDistance = RelDistance(view.frameTimeMs, p.Signal.FrameTimeMs);
                    double primary = byAlloc ? gcDistance : timeDistance;
                    double distance = primary + 0.25 * (byAlloc ? timeDistance : gcDistance);
                    if (distance < p.BestDistance)
                    {
                        p.BestDistance = distance;
                        p.Best = f;
                        p.Matched = primary <= FrameMatchTolerance;
                    }
                }
                finally
                {
                    view?.Dispose();
                }
            }
        }

        private static string BuildEventJson(string eventId, int frameIndex, int requestedFrameIndex,
            in TripSignal signal, string status, string markerTreeJson, string topMarkersJson, float cpuMs,
            float capturedFrameMs, float frameGcBytes, bool frameMatches, string likelyCause, Accumulator acc)
        {
            string severity = Severity(signal.Value, signal.Budget);
            string review = ReviewStatusOf(eventId);

            var sb = new StringBuilder(1024);
            sb.Append('{');
            sb.Append("\"schemaVersion\":").Append(Json.Str(SchemaVersion));
            sb.Append(",\"writerVersion\":").Append(Json.Str(WriterVersion));
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

            // The captured frame's own time, and whether the captured frame's counters are close
            // to the trip's (counters.*). false = no frame in the search window looked like the
            // trip, so the tree may describe a neighbouring frame - say so instead of blaming it.
            sb.Append(",\"capturedFrameTimeMs\":").Append(Json.Num(capturedFrameMs));
            sb.Append(",\"frameMatchesTrigger\":").Append(frameMatches ? "true" : "false");

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

            // How deep the stored tree goes. The CLI prints a shallower slice by default and uses
            // this to tell the reader whether a deeper `--depth`/`--focus` request is answerable
            // from the record or needs a re-capture. Nodes cut short also say so in-band
            // ("truncated": "depth" | "budget"), so a cut node never reads as a genuine leaf.
            sb.Append(",\"markerTreeDepth\":").Append(Json.Num(MarkerTreeNormalizer.CaptureDepth));

            // Ranked over the WHOLE frame at any depth - not over the trimmed tree - so the
            // frame's biggest self-time / allocating marker is here even when it sits below the
            // tree's depth cap (which is where hand-placed Profiler.BeginSample markers live).
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

        private static string BuildMeta()
        {
            var sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"schemaVersion\":").Append(Json.Str(SchemaVersion));
            sb.Append(",\"packageVersion\":").Append(Json.Str(WriterVersion));
            sb.Append(",\"unityVersion\":").Append(Json.Str(Application.unityVersion));
            sb.Append(",\"writtenAt\":").Append(Json.Str(DateTime.UtcNow.ToString("o")));
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
