using Unity.Profiling;
using UnityEngine;

namespace Profilot
{
    /// <summary>
    /// Phase 1 (SPEC.md section 17). The low-overhead live tripwire: samples a few cheap
    /// counters every frame through ProfilerRecorder and flags anomalies against a budget.
    /// There is no LLM call and no full-frame capture here - capturing the problem frame
    /// (ProfilerDriver, Editor side) and writing an event record is Phase 2.
    ///
    /// Counters are only meaningful in the Editor and in development builds; in release
    /// players most markers are stripped (SPEC.md NG3), so the tripwire does not boot there.
    /// Hot-path rule (SPEC.md M8): no allocations in the per-frame sampling path.
    /// </summary>
    [DefaultExecutionOrder(int.MaxValue)]
    public sealed class ProfilotTripwire : MonoBehaviour
    {
        // Placeholder budget defaults (SPEC.md decision 5; calibrated in Phase 3).
        public double FrameBudgetMs = 16.6;            // 60 fps
        public long GcAllocBudgetBytes = 0;            // any in-frame allocation is a candidate
        public float DrawCallsBaselineMultiplier = 1.5f;

        private ProfilerRecorder _mainThreadTime;      // nanoseconds
        private ProfilerRecorder _gcAllocated;         // bytes
        private ProfilerRecorder _drawCalls;           // count

        private double _drawCallsBaseline;
        private bool _baselineWarm;

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
            double frameMs = _mainThreadTime.Valid ? _mainThreadTime.LastValue * 1e-6 : 0.0; // ns -> ms
            long gcBytes = _gcAllocated.Valid ? _gcAllocated.LastValue : 0L;
            double draws = _drawCalls.Valid ? _drawCalls.LastValue : 0.0;

            UpdateDrawCallsBaseline(draws);

            // First match wins; richer dedup / cooldown / event records come in Phase 2.
            if (_mainThreadTime.Valid && frameMs > FrameBudgetMs)
                OnTrip("frame_hitch", "frameTimeMs", frameMs, FrameBudgetMs);
            else if (_gcAllocated.Valid && gcBytes > GcAllocBudgetBytes)
                OnTrip("gc_spike", "gcAllocBytes", gcBytes, GcAllocBudgetBytes);
            else if (_baselineWarm && _drawCalls.Valid && draws > _drawCallsBaseline * DrawCallsBaselineMultiplier)
                OnTrip("draw_calls", "drawCalls", draws, _drawCallsBaseline * DrawCallsBaselineMultiplier);
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

        private void OnTrip(string type, string metric, double value, double budget)
        {
            // Phase 1 surfaces the trip to the Console only. Phase 2 will hand the frame
            // index to the Editor layer, which fetches the full frame and writes an event
            // record to the store for the CLI to expose to Claude Code.
            Debug.LogWarning($"[Profilot] {type} caught - {metric} {value:F2} (budget {budget:F2}) at frame {Time.frameCount}.");
        }
    }
}
