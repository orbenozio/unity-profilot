using Unity.Profiling;
using UnityEngine;

namespace Profilot
{
    /// <summary>
    /// Phase 1 + 2 (SPEC.md section 17). The low-overhead live tripwire: samples a few cheap
    /// counters every frame through ProfilerRecorder and flags anomalies against a budget.
    /// On a trip it publishes a <see cref="TripSignal"/> to <see cref="ProfilotTripChannel"/>;
    /// the editor capture service then fetches the full problem frame and writes an event
    /// record. There is no LLM call here.
    ///
    /// Counters are only meaningful in the Editor and in development builds; in release
    /// players most markers are stripped (SPEC.md NG3), so the tripwire does not boot there.
    /// Hot-path rule (SPEC.md M8): no allocations in the per-frame sampling path.
    /// </summary>
    [DefaultExecutionOrder(int.MaxValue)]
    public sealed class ProfilotTripwire : MonoBehaviour
    {
        private enum TripKind { FrameHitch = 0, GcSpike = 1, DrawCalls = 2 }
        private const int TripKindCount = 3;

        // Spec trigger.type strings (SPEC.md section 14), indexed by TripKind.
        private static readonly string[] TripTypes = { "frame_hitch", "gc_spike", "draw_calls" };
        private static readonly string[] TripMetrics = { "frameTimeMs", "gcAllocBytes", "drawCalls" };

        // Placeholder budget defaults (SPEC.md decision 5; calibrated in Phase 3).
        public double FrameBudgetMs = 16.6;            // 60 fps
        public long GcAllocBudgetBytes = 0;            // any in-frame allocation is a candidate
        public float DrawCallsBaselineMultiplier = 1.5f;

        // A trigger type emits at most one signal per cooldown window; repeats inside the
        // window are counted and folded into the next signal as dedup.count (SPEC.md
        // section 15 rate-limit / dedup). Without it the Editor's constant baseline
        // allocation would re-fire gc_spike every single frame.
        public double CooldownSeconds = 2.0;

        // Ignore the first frames after entering Play Mode: scene init and first-time JIT
        // compilation always blow the frame budget and would fire a meaningless startup
        // hitch (false positive, hurts SPEC.md M4). Counted in frames, not seconds, on
        // purpose - during the JIT storm a single frame can take hundreds of ms, so a
        // wall-clock warmup expires mid-storm. Counters still warm during this window.
        public int WarmupFrames = 60;

        // Convenience for development; the real surface is the event store, not the Console.
        public bool LogToConsole = true;

        private ProfilerRecorder _mainThreadTime;      // nanoseconds
        private ProfilerRecorder _gcAllocated;         // bytes
        private ProfilerRecorder _drawCalls;           // count

        private int _enabledFrame;
        private double _drawCallsBaseline;
        private bool _baselineWarm;

        private readonly double[] _lastReportTime = new double[TripKindCount];
        private readonly int[] _suppressedSinceReport = new int[TripKindCount];

        // Last per-frame snapshot, so a trip can carry the full counter set.
        private double _frameMs;
        private long _gcBytes;
        private double _draws;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // Only where profiler data exists and is trustworthy.
            if (!Application.isEditor && !Debug.isDebugBuild)
                return;

            var host = new GameObject("[Profilot] Tripwire")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            host.AddComponent<ProfilotTripwire>();
            DontDestroyOnLoad(host);
        }

        private void OnEnable()
        {
            for (int i = 0; i < TripKindCount; i++)
            {
                _lastReportTime[i] = double.NegativeInfinity;
                _suppressedSinceReport[i] = 0;
            }

            _enabledFrame = Time.frameCount;

            // Recorders are allocated once here, never in the per-frame path.
            _mainThreadTime = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
            _gcAllocated = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count", 1);
        }

        private void OnDisable()
        {
            _mainThreadTime.Dispose();
            _gcAllocated.Dispose();
            _drawCalls.Dispose();
        }

        private void LateUpdate()
        {
            // Read last sampled values only. A counter that is not available on this
            // Unity version / platform reports Valid == false and is simply skipped
            // (SPEC.md section 14: GetAvailable / Valid before reading).
            _frameMs = _mainThreadTime.Valid ? _mainThreadTime.LastValue * 1e-6 : 0.0; // ns -> ms
            _gcBytes = _gcAllocated.Valid ? _gcAllocated.LastValue : 0L;
            _draws = _drawCalls.Valid ? _drawCalls.LastValue : 0.0;

            UpdateDrawCallsBaseline(_draws);

            // Let the first frames after Play settle (scene init + JIT) before flagging.
            if (Time.frameCount - _enabledFrame < WarmupFrames)
                return;

            // First match wins; a full multi-event model per frame comes later.
            if (_mainThreadTime.Valid && _frameMs > FrameBudgetMs)
                OnTrip(TripKind.FrameHitch, _frameMs, FrameBudgetMs);
            else if (_gcAllocated.Valid && _gcBytes > GcAllocBudgetBytes)
                OnTrip(TripKind.GcSpike, _gcBytes, GcAllocBudgetBytes);
            else if (_baselineWarm && _drawCalls.Valid && _draws > _drawCallsBaseline * DrawCallsBaselineMultiplier)
                OnTrip(TripKind.DrawCalls, _draws, _drawCallsBaseline * DrawCallsBaselineMultiplier);
        }

        private void UpdateDrawCallsBaseline(double draws)
        {
            // Exponential moving average as the relative baseline (absolute draw-call
            // counts vary too much between scenes to use a fixed threshold).
            const double alpha = 0.05;
            if (!_baselineWarm)
            {
                _drawCallsBaseline = draws;
                _baselineWarm = true;
                return;
            }

            _drawCallsBaseline = (1.0 - alpha) * _drawCallsBaseline + alpha * draws;
        }

        private void OnTrip(TripKind kind, double value, double budget)
        {
            int i = (int)kind;

            // Inside the cooldown window: count the repeat, stay silent (this also keeps the
            // tool from generating its own GC by emitting every frame).
            double now = Time.unscaledTimeAsDouble;
            if (now - _lastReportTime[i] < CooldownSeconds)
            {
                _suppressedSinceReport[i]++;
                return;
            }

            int repeats = _suppressedSinceReport[i];
            _suppressedSinceReport[i] = 0;
            _lastReportTime[i] = now;

            var signal = new TripSignal(
                TripTypes[i], TripMetrics[i], value, budget, Time.frameCount, repeats,
                _frameMs, _gcBytes, _draws);
            ProfilotTripChannel.Publish(signal);

            if (LogToConsole)
            {
                string repeatNote = repeats > 0 ? $" (+{repeats} more in the last {CooldownSeconds:F0}s)" : string.Empty;
                Debug.LogWarning($"[Profilot] {TripTypes[i]} caught - {TripMetrics[i]} {value:F2} (budget {budget:F2}) at frame {Time.frameCount}{repeatNote}.");
            }
        }
    }
}
