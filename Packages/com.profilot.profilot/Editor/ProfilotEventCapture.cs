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
        // 3 = dedup.peakValue, run.json per run, no stored "stale" (the CLI computes it).
        internal const string SchemaVersion = "3";

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
        // back. We pick by signal (the frame whose counters match the trigger), not by index,
        // which is robust to that offset (SPEC.md section 15, ring buffer). Scanning reads
        // counters through a RawFrameDataView, cached per frame and shared by every pending
        // trip; the expensive hierarchy view is built once, for the chosen frame only.
        private const int FrameSearchWindow = 10;

        private static readonly List<TripSignal> Buffer = new List<TripSignal>();
        private static string _sessionId = "editor";
        private static string _runStartedAt = string.Empty;

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
            public double Peak;
        }

        private static readonly Dictionary<string, Accumulator> Dedup = new Dictionary<string, Accumulator>();

        // Review decisions set by the user from the window, keyed by eventId across runs:
        //   "fixed"     - muted, but remembers the trigger value it was marked at. If the problem
        //                 comes back RegressionFactor times worse it turns "regressed" and alerts.
        //   "ignored"   - muted for good (noise), until the user reopens it.
        //   "regressed" - a fixed problem that came back worse; alerts like an open one.
        // Remembered so that an ongoing spike of the same problem does not reset it back to
        // "open" when the record is overwritten.
        // A fixed problem that comes back below the regression line stays muted but is not
        // invisible: SeenAgain* say how often, in which run and how big, so "did my fix work?"
        // has an answer. Helped is the user's yes/no on whether the diagnosis helped.
        private sealed class Review
        {
            public string Status;
            public double Value;
            public string MarkedAt = string.Empty;
            public int SeenAgainCount;
            public string SeenAgainRun = string.Empty;
            public double SeenAgainValue;
            public string Helped = string.Empty;
        }

        private const double RegressionFactor = 3.0;
        private static readonly Dictionary<string, Review> Reviewed = new Dictionary<string, Review>();

        /// <summary>Called by the window when the user marks an event (SPEC.md JTBD-8). A "fixed"
        /// mark measures a comeback against the problem's PEAK across every run, not the value on
        /// the card that was clicked: a quiet repeat as the reference raised false "regressed"
        /// alerts, and an older run's card set the bar too low.</summary>
        internal static void MarkReviewed(string eventId, string status)
        {
            // "open" is the absence of a decision (Reopen) - drop it from the persisted map so
            // notifications resume; any other status is remembered across sessions.
            if (status == "open")
                Reviewed.Remove(eventId);
            else
                Reviewed[eventId] = new Review
                {
                    Status = status,
                    Value = status == "fixed" ? PeakAcrossRuns(eventId) : 0,
                    MarkedAt = DateTime.UtcNow.ToString("o"),
                };

            SaveReviews();
        }

        /// <summary>The user's answer to "did the diagnosis help?" (feeds SPEC.md M2).</summary>
        internal static void MarkHelped(string eventId, bool helped)
        {
            if (!Reviewed.TryGetValue(eventId, out Review r))
                return;
            r.Helped = helped ? "yes" : "no";
            SaveReviews();
        }

        [System.Serializable] private class PeakRecord { public PeakTrigger trigger; public PeakDedup dedup; }
        [System.Serializable] private class PeakTrigger { public double value; }
        [System.Serializable] private class PeakDedup { public double peakValue; }

        /// <summary>The largest value this problem reached in any stored run (the peak a record
        /// carries, or its trigger value for a record from before peaks were kept).</summary>
        internal static double PeakAcrossRuns(string eventId)
        {
            double peak = 0;
            foreach (string run in ProfilotEventStore.ListRuns())
            {
                string file = ProfilotEventStore.EventPath(run, eventId);
                if (!System.IO.File.Exists(file))
                    continue;
                try
                {
                    PeakRecord r = JsonUtility.FromJson<PeakRecord>(System.IO.File.ReadAllText(file));
                    if (r == null)
                        continue;
                    if (r.dedup != null) peak = Math.Max(peak, r.dedup.peakValue);
                    if (r.trigger != null) peak = Math.Max(peak, r.trigger.value);
                }
                catch { /* a file caught mid-write just does not count */ }
            }
            return peak;
        }

        // The run id survives a domain reload. With "Recompile And Continue Playing" the Editor
        // reloads mid-Play and EnteredPlayMode does not fire again, so the id used to fall back to
        // a default and the rest of the run was written into a folder that sorts as the oldest.
        private const string RunIdKey = "Profilot.RunId";
        private const string RunStartedKey = "Profilot.RunStartedAt";

        static ProfilotEventCapture()
        {
            EditorApplication.update += OnUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            LoadReviews();

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                string run = SessionState.GetString(RunIdKey, string.Empty);
                if (ProfilotEventStore.IsRunId(run))
                {
                    _sessionId = run;
                    ProfilotEventStore.CurrentRun = run;
                    _runStartedAt = SessionState.GetString(RunStartedKey, string.Empty);
                }
            }
        }

        [System.Serializable] private class ReviewRecord
        {
            public string id;
            public string status;
            public double value;
            public string markedAt;
            public int seenAgainCount;
            public string seenAgainRun;
            public double seenAgainValue;
            public string helped;
        }
        [System.Serializable] private class ReviewFile { public List<ReviewRecord> items = new List<ReviewRecord>(); }

        internal static void LoadReviews()
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
                            Reviewed[it.id] = new Review
                            {
                                Status = MigrateStatus(it.status),
                                Value = it.value,
                                MarkedAt = it.markedAt ?? string.Empty,
                                SeenAgainCount = it.seenAgainCount,
                                SeenAgainRun = it.seenAgainRun ?? string.Empty,
                                SeenAgainValue = it.seenAgainValue,
                                Helped = it.helped ?? string.Empty,
                            };
            }
            catch { /* a corrupt reviews file should never break capture */ }
        }

        // Before 0.7 the two buttons were "reviewed" and "not_a_real_issue", and both muted
        // forever. They map onto the new pair; an old "reviewed" has no value, so its first
        // comeback sets the reference instead of alerting.
        internal static string MigrateStatus(string status)
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
            {
                Review r = kv.Value;
                f.items.Add(new ReviewRecord
                {
                    id = kv.Key, status = r.Status, value = r.Value, markedAt = r.MarkedAt,
                    seenAgainCount = r.SeenAgainCount, seenAgainRun = r.SeenAgainRun,
                    seenAgainValue = r.SeenAgainValue, helped = r.Helped,
                });
            }
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
        internal static bool CheckRegression(string eventId, double value)
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

        /// <summary>The peak a "fixed" mark was made at (0 when unknown or not fixed).</summary>
        internal static double FixedValueOf(string eventId)
        {
            return Reviewed.TryGetValue(eventId, out Review r) && r.Status == "fixed" ? r.Value : 0;
        }

        /// <summary>A fixed problem caught again below the regression line: how often, the run it
        /// was last seen in, and its largest value since the mark. False when not seen again.</summary>
        internal static bool SeenAgainAfterFixed(string eventId, out int count, out string run, out double value)
        {
            count = 0; run = null; value = 0;
            if (!Reviewed.TryGetValue(eventId, out Review r) || r.Status != "fixed" || r.SeenAgainCount <= 0)
                return false;
            count = r.SeenAgainCount; run = r.SeenAgainRun; value = r.SeenAgainValue;
            return true;
        }

        internal static string HelpedOf(string eventId)
        {
            return Reviewed.TryGetValue(eventId, out Review r) ? r.Helped : string.Empty;
        }

        // Fixed problems already counted as seen again this run, so reviews.json is written once
        // per problem per run (the rest of the count is saved when Play ends).
        private static readonly HashSet<string> SeenAgainThisRun = new HashSet<string>();
        private static bool _reviewsDirty;

        internal static void NoteSeenAgain(string eventId, in TripSignal signal)
        {
            if (!Reviewed.TryGetValue(eventId, out Review r) || r.Status != "fixed")
                return;
            // One per capture: signal.RepeatCount folds in every repeat of the TYPE during the
            // cooldown, most of them other problems', so it would inflate this count.
            r.SeenAgainCount++;
            r.SeenAgainRun = _sessionId;
            r.SeenAgainValue = Math.Max(r.SeenAgainValue, signal.Value);
            if (SeenAgainThisRun.Add(eventId))
                SaveReviews();
            else
                _reviewsDirty = true;
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
                // A trip from the last moments of the run is still waiting for its frame: write
                // it with the best frame seen so far instead of dropping it.
                bool deepNow = Profiler.enabled;
                Buffer.Clear();
                ProfilotTripChannel.Drain(Buffer);
                foreach (TripSignal signal in Buffer)
                    PendingCaptures.Add(new PendingCapture { Signal = signal, RequestedFrameIndex = ProfilerDriver.lastFrameIndex });
                foreach (PendingCapture p in PendingCaptures)
                {
                    try { Flush(p, deepNow); }
                    catch (Exception e) { Debug.LogWarning($"[Profilot] Could not write a pending event on exit: {e.Message}"); }
                }
                PendingCaptures.Clear();
                FrameCache.Clear();

                if (_reviewsDirty)
                {
                    SaveReviews();
                    _reviewsDirty = false;
                }

                try { ProfilotEventStore.WriteRunInfo(_sessionId, BuildRunInfo(true)); }
                catch { /* best effort */ }
                SessionState.EraseString(RunIdKey);
                SessionState.EraseString(RunStartedKey);

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
            _runStartedAt = DateTime.UtcNow.ToString("o");
            SessionState.SetString(RunIdKey, _sessionId);
            SessionState.SetString(RunStartedKey, _runStartedAt);
            SeenAgainThisRun.Clear();
            _reviewsDirty = false;

            // Which package and schema write this store, so `profilot status` can tell the user
            // when the CLI, the package and the project's guide have drifted apart.
            try { ProfilotEventStore.WriteMeta(BuildMeta()); }
            catch { /* best effort - capture does not depend on it */ }

            // Age-based retention: drop run folders older than the configured cap (if enabled),
            // so results from long-gone runs do not pile up.
            ProfilotConfig cfg = ProfilotConfigStore.Load();
            if (cfg.pruneEnabled)
                ProfilotEventStore.PruneByAge(cfg.retentionDays);

            // The run exists from its first frame, so a run that catches nothing is on record.
            try { ProfilotEventStore.StartRun(_sessionId, BuildRunInfo(false)); }
            catch { /* best effort - capture does not depend on it */ }

            // Arm the profiler for marker->code mapping (on by default - it was measured to
            // add negligible per-frame cost). Off only if the user opted into counter-only mode
            // for maximum Editor speed.
            ArmRecording(DeepCapture);

            ProfilotTripChannel.Clear();
            PendingCaptures.Clear();
            FrameCache.Clear();
            _lastNotifiedFrame = -1;
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
            public int RequestedFrameIndex;
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
                PendingCaptures.Add(new PendingCapture { Signal = signal, RequestedFrameIndex = ProfilerDriver.lastFrameIndex });

            if (PendingCaptures.Count == 0)
            {
                FrameCache.Clear();
                return;
            }

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
                Flush(p, deep);
            }
        }

        private static void Flush(PendingCapture p, bool deep)
        {
            Capture(p.Signal, deep && p.Best >= 0 ? p.Best : ProfilerDriver.lastFrameIndex, deep && p.Matched,
                p.RequestedFrameIndex);
        }

        // One bad frame can trip several types (a load frame is both a hitch and a GC spike).
        // Each still gets its record, but the user is told once.
        private static int _lastNotifiedFrame = -1;

        private static void Capture(in TripSignal signal, int frameIndex, bool frameMatches, int requestedFrameIndex)
        {
            bool deep = Profiler.enabled;

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

            // Only a capture whose frame matched the trip is attributed to its marker: an unmatched
            // one is named after a neighbouring frame, so it must not change a decision ("regressed",
            // seen again), raise a problem's peak, or - for a problem the user already decided on -
            // be written under that problem's id, where the CLI would read it as "still happening".
            bool attributed = !deep || frameMatches;

            // Stable id per problem (type + dominant marker) so repeats overwrite one file.
            string eventId = $"evt_{signal.Type}_{Slug(dominantMarker)}";
            if (!attributed && Reviewed.ContainsKey(eventId))
                eventId = $"evt_{signal.Type}_unmatched";

            bool isNewProblem = !Dedup.TryGetValue(eventId, out Accumulator acc);
            if (isNewProblem)
            {
                acc = new Accumulator { Count = 0, FirstSeenFrame = signal.FrameCount };
                Dedup[eventId] = acc;
            }
            acc.Count += signal.RepeatCount + 1;
            if (attributed)
                acc.Peak = Math.Max(acc.Peak, signal.Value);

            // Before the record is built, so it already carries "regressed".
            bool regressed = attributed && CheckRegression(eventId, signal.Value);
            if (attributed && !regressed)
                NoteSeenAgain(eventId, signal);

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
            // A regression always alerts: it fires only on the transition, so suppressing it as
            // "same frame as another problem" would lose it for good.
            if (regressed || (isNewProblem && !IsMuted(eventId) && signal.FrameCount != _lastNotifiedFrame))
            {
                _lastNotifiedFrame = signal.FrameCount;
                ProfilotNotifier.OnProblemCaught(eventId, _sessionId, signal.Type, dominantMarker, acc.Count, regressed,
                    matched: attributed);
            }
        }

        /// <summary>
        /// Filename-safe key from the dominant marker: drops the assembly prefix and the
        /// "() [Invoke]" decoration, keeps the method-ish tail, so the dedup id is stable
        /// and readable (e.g. "GarbageGenerator.Update").
        /// </summary>
        internal static string Slug(string marker)
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

        // A scanned frame is the trip's own frame (frameMatchesTrigger) only when it breaches
        // the trip's own rule AND its primary metric is within this relative distance of the
        // trip's value. Distance alone was not enough: at 0.5 a 15 ms trip "matched" a normal
        // 8 ms neighbour, and draw-call trips matched on frame time, which a draw-call spike
        // barely moves.
        internal const double FrameMatchTolerance = 0.25;

        // When the GC counter is not readable from the profiler frame, the hierarchy root's GC
        // column is the fallback. The tripwire's counter also counts what the Editor allocates
        // around the player loop (~15 KB a frame), which the tree does not include, so that
        // much difference is allowed - but never more than half the trip itself, or every small
        // leak "matched" whichever frame came first.
        private const double GcMatchSlackBytes = 64 * 1024;

        // |a - b| beyond `slack`, relative to the larger of the two: 0 = equal, 1 = far apart.
        internal static double RelDistance(double a, double b, double slack = 0.0)
        {
            double m = Math.Max(Math.Abs(a), Math.Abs(b));
            return m < 1e-9 ? 0.0 : Math.Max(0.0, Math.Abs(a - b) - slack) / m;
        }

        internal static double GcSlack(double tripBytes) => Math.Min(GcMatchSlackBytes, 0.5 * Math.Abs(tripBytes));

        /// <summary>The counters of one profiler frame. Gc / Draws are -1 when unreadable;
        /// GcFromCounter says whether Gc is the same counter the tripwire reads (exact) or
        /// the hierarchy root (needs slack).</summary>
        internal struct FrameStats
        {
            public bool Valid;
            public double FrameMs;
            public double Gc;
            public bool GcFromCounter;
            public double Draws;
        }

        /// <summary>
        /// Whether a frame is the one that tripped: it must breach the trip's own rule, and its
        /// primary metric must be close to the trip's value. `distance` ranks candidates (primary
        /// plus a quarter of the other metric). Pure, so it is unit-tested.
        /// </summary>
        internal static bool MatchFrame(in TripSignal sig, in FrameStats st, out double distance)
        {
            double timeDistance = RelDistance(st.FrameMs, sig.FrameTimeMs);
            double gcDistance = st.Gc < 0
                ? 1.0
                : RelDistance(st.Gc, sig.GcAllocBytes, st.GcFromCounter ? 0.0 : GcSlack(sig.GcAllocBytes));

            double primary;
            bool breach;
            switch (sig.Type)
            {
                case "gc_spike":
                    primary = gcDistance;
                    breach = st.Gc > sig.Budget;
                    distance = primary + 0.25 * timeDistance;
                    break;
                case "draw_calls":
                    primary = st.Draws < 0 ? 1.0 : RelDistance(st.Draws, sig.DrawCalls);
                    breach = st.Draws > sig.Budget;
                    distance = primary + 0.25 * timeDistance;
                    break;
                default: // frame_hitch
                    primary = timeDistance;
                    breach = st.FrameMs >= sig.Budget;
                    distance = primary + 0.25 * gcDistance;
                    break;
            }
            return st.Valid && breach && primary <= FrameMatchTolerance;
        }

        // Counters per profiler frame, shared by every pending trip, cleared when nothing waits.
        private static readonly Dictionary<int, FrameStats> FrameCache = new Dictionary<int, FrameStats>();

        private static FrameStats StatsOf(int frame)
        {
            if (FrameCache.TryGetValue(frame, out FrameStats st))
                return st;

            st = new FrameStats { Gc = -1, Draws = -1 };
            using (RawFrameDataView raw = ProfilerDriver.GetRawFrameDataView(frame, 0))
            {
                if (raw != null && raw.valid)
                {
                    st.Valid = true;
                    st.FrameMs = raw.frameTimeMs;
                    // A counter registered but not sampled in this frame has no value; reading it
                    // would give a false 0 and skip the hierarchy fallback.
                    int gcId = raw.GetMarkerId("GC Allocated In Frame");
                    if (gcId != FrameDataView.invalidMarkerId && raw.HasCounterValue(gcId))
                    {
                        st.Gc = raw.GetCounterValueAsLong(gcId);
                        st.GcFromCounter = true;
                    }
                    int drawId = raw.GetMarkerId("Draw Calls Count");
                    if (drawId != FrameDataView.invalidMarkerId && raw.HasCounterValue(drawId))
                        st.Draws = raw.GetCounterValueAsInt(drawId);
                }
            }

            if (st.Valid && st.Gc < 0)
            {
                using (HierarchyFrameDataView view = ProfilerDriver.GetHierarchyFrameDataView(
                           frame, 0, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                           HierarchyFrameDataView.columnTotalTime, false))
                {
                    if (view != null && view.valid)
                        st.Gc = view.GetItemColumnDataAsFloat(view.GetRootItemID(), HierarchyFrameDataView.columnGcMemory);
                }
            }

            FrameCache[frame] = st;
            return st;
        }

        /// <summary>
        /// Looks for the frame that tripped among the profiler frames that arrived since the last
        /// look (the first look also covers the FrameSearchWindow before it). A frame that matches
        /// (see MatchFrame) always beats one that does not; among equals the closest wins. When
        /// nothing matches by the deadline the closest frame is written with
        /// frameMatchesTrigger false, so the record says the tree may be a neighbour's.
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

            for (int f = latest; f >= from; f--)
            {
                FrameStats st = StatsOf(f);
                if (!st.Valid)
                    continue;
                bool matches = MatchFrame(p.Signal, st, out double distance);
                bool better = matches
                    ? !p.Matched || distance < p.BestDistance
                    : !p.Matched && distance < p.BestDistance;
                if (better)
                {
                    p.Best = f;
                    p.BestDistance = distance;
                    p.Matched = matches;
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
            sb.Append(",\"peakValue\":").Append(Json.Num(acc.Peak));
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

        private static string BuildRunInfo(bool ended)
        {
            var sb = new StringBuilder(200);
            sb.Append('{');
            sb.Append("\"schemaVersion\":").Append(Json.Str(SchemaVersion));
            sb.Append(",\"run\":").Append(Json.Str(_sessionId));
            sb.Append(",\"packageVersion\":").Append(Json.Str(WriterVersion));
            sb.Append(",\"startedAt\":").Append(Json.Str(_runStartedAt));
            if (ended)
                sb.Append(",\"endedAt\":").Append(Json.Str(DateTime.UtcNow.ToString("o")));
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
