using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Profilot.PlayTests
{
    /// <summary>
    /// Golden-scenario calibration harness (SPEC.md section 17 phase 3, metrics M3/M5). Each
    /// scenario has a KNOWN responsible method, so every case asserts two things:
    ///   - M5 (recall): the tripwire caught an event of the expected type;
    ///   - M3 (mapping): the captured record names the responsible method, so Claude can map
    ///     the marker back to the offending file.
    /// The record's markers are surfaced both in topMarkers and in the eventId slug
    /// (evt_&lt;type&gt;_&lt;dominantMarker&gt;), so a substring check on the JSON is enough.
    /// Runs live in the editor (full graphics) via the agent bridge, not headless -nographics.
    /// </summary>
    public class GoldenScenarioTests
    {
        private static string RunsDir =>
            Path.Combine(Directory.GetCurrentDirectory(), "Library", "Profilot", "runs");

        private static void ClearStore()
        {
            if (Directory.Exists(RunsDir))
                Directory.Delete(RunsDir, true);
        }

        // Events now live in per-run folders (runs/<runId>/evt_*.json), so scan them all.
        private static string[] AllEventFiles()
        {
            return Directory.Exists(RunsDir)
                ? Directory.GetFiles(RunsDir, "evt_*.json", SearchOption.AllDirectories)
                : System.Array.Empty<string>();
        }

        // All PlayMode tests share ONE play session, so the tripwire (a DontDestroyOnLoad
        // singleton) keeps its per-type cooldown across tests. Under the test runner's very
        // high frame rate a single 2s cooldown spans ~1400 frames - long enough to shadow a
        // whole later test - and the runner's own yield machinery (CoroutinesDelayedCalls)
        // trips gc_spike every frame, keeping that cooldown consumed. Zeroing the cooldown
        // makes each scenario's trip fire on its own frames, independent of the others.
        private static void ArmProfilot()
        {
            // Deep capture is off by default (the profiler is a heavy per-frame Editor cost).
            // These scenarios assert marker -> code mapping, which needs the marker tree, so
            // turn the profiler on for the test. (The capture keys off Profiler.enabled.)
            UnityEngine.Profiling.Profiler.enabled = true;

            // The tripwire GameObject is HideFlags.HideAndDontSave, which FindObjectOfType
            // silently skips - use Resources.FindObjectsOfTypeAll, which includes hidden
            // objects, or the cooldown is never actually zeroed.
            foreach (var t in Resources.FindObjectsOfTypeAll<Profilot.ProfilotTripwire>())
                t.CooldownSeconds = 0.0;
        }

        // True if the store holds an event whose DOMINANT marker (the one that names the
        // record, evt_<type>_<dominant>) is the responsible method, under any of the accepted
        // trigger types. Asserting the dominant marker - not mere presence in the tree - makes
        // this a real M3 precision check: the headline Claude sees must point at the offending
        // code. Accepting more than one type reflects product reality: a heavy per-frame
        // allocator surfaces as the GC-pause frame_hitch, not always a gc_spike (the light-
        // allocation gc_spike path is covered by CaptureIntegrationTests).
        private static bool CaughtAndMapped(string responsibleMarker, params string[] acceptedTypes)
        {
            foreach (string file in AllEventFiles())
            {
                string json = File.ReadAllText(file);
                foreach (string type in acceptedTypes)
                {
                    if (json.Contains($"\"eventId\":\"evt_{type}_{responsibleMarker}"))
                        return true;
                }
            }
            return false;
        }

        [UnityTest]
        public IEnumerator GcSpike_is_caught_and_maps_to_the_allocating_method()
        {
            ClearStore();
            ArmProfilot();

            var go = new GameObject("gc-spike-scenario");
            go.AddComponent<GcSpikeScenario>().bytesPerFrame = 1_000_000;

            // Past warm-up (60 frames) + a trip + the editor-side capture on the next update.
            for (int i = 0; i < 200; i++)
                yield return null;

            Object.Destroy(go);
            yield return null;

            Assert.IsTrue(CaughtAndMapped("GcSpikeScenario.Update", "gc_spike", "frame_hitch"),
                "expected a caught event whose dominant marker is GcSpikeScenario.Update (M5 recall + M3 mapping)");
        }

        [UnityTest]
        public IEnumerator FrameHitch_is_caught_and_maps_to_the_stalling_method()
        {
            ClearStore();
            ArmProfilot();

            var go = new GameObject("frame-hitch-scenario");
            var s = go.AddComponent<SyncHitchScenario>();
            s.startAfterFrames = 75;
            s.stallMs = 40.0;

            // Warm-up (60) + baseline seed on normal frames + a sustained stall stretch + the
            // editor-side capture on the next update.
            for (int i = 0; i < 200; i++)
                yield return null;

            Object.Destroy(go);
            yield return null;

            Assert.IsTrue(CaughtAndMapped("SyncHitchScenario.Update", "frame_hitch"),
                "expected a frame_hitch event whose dominant marker is SyncHitchScenario.Update (M5 recall + M3 mapping)");
        }

        // The 0.8 match rule as an invariant over every captured record: a record that claims
        // its frame matches the trip must hold a frame that breaches the trip's own threshold.
        private static void AssertMatchedHitchesBreach()
        {
            foreach (string file in AllEventFiles())
            {
                string json = File.ReadAllText(file);
                if (!json.Contains("\"type\":\"frame_hitch\"") || !json.Contains("\"frameMatchesTrigger\":true"))
                    continue;
                double captured = NumberAfter(json, "\"capturedFrameTimeMs\":");
                double budget = NumberAfter(json, "\"budget\":");
                Assert.GreaterOrEqual(captured, budget,
                    $"{Path.GetFileName(file)} claims frameMatchesTrigger but its frame ({captured} ms) is under the trip threshold ({budget} ms)");
            }
        }

        private static double NumberAfter(string json, string key)
        {
            int i = json.IndexOf(key, System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(i, 0, $"{key} missing");
            i += key.Length;
            int j = i;
            while (j < json.Length && "-+.0123456789eE".IndexOf(json[j]) >= 0)
                j++;
            return double.Parse(json.Substring(i, j - i), System.Globalization.CultureInfo.InvariantCulture);
        }

        [UnityTest]
        public IEnumerator MildHitch_is_captured_on_its_own_frame()
        {
            ClearStore();
            ArmProfilot();

            var go = new GameObject("mild-hitch-scenario");
            go.AddComponent<MildHitchScenario>();

            for (int i = 0; i < 260; i++)
                yield return null;

            Object.Destroy(go);
            yield return null;

            Assert.IsTrue(CaughtAndMapped("MildHitchScenario.Update", "frame_hitch"),
                "expected a frame_hitch event whose dominant marker is MildHitchScenario.Update");

            bool anyMatched = false;
            foreach (string file in AllEventFiles())
            {
                string json = File.ReadAllText(file);
                if (json.Contains("\"eventId\":\"evt_frame_hitch_MildHitchScenario.Update") && json.Contains("\"frameMatchesTrigger\":true"))
                    anyMatched = true;
            }
            Assert.IsTrue(anyMatched, "the mild hitch was captured, but never on a frame that matches the trip");
            AssertMatchedHitchesBreach();
        }

        [UnityTest]
        public IEnumerator DrawCallSpike_is_caught_and_matched_on_the_draw_counter()
        {
            ClearStore();
            ArmProfilot();

            var go = new GameObject("draw-call-scenario");
            go.AddComponent<DrawCallSpikeScenario>();

            var draws = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Draw Calls Count", 1);
            double maxDraws = 0;
            for (int i = 0; i < 200; i++)
            {
                yield return null;
                if (draws.Valid)
                    maxDraws = System.Math.Max(maxDraws, draws.LastValue);
            }
            draws.Dispose();

            Object.Destroy(go);
            yield return null;

            if (maxDraws < 100)
                Assert.Inconclusive($"This run did not render (max {maxDraws} draw calls a frame), so a draw-call spike cannot happen. Run it in the Editor with graphics.");

            string record = null;
            foreach (string file in AllEventFiles())
            {
                string json = File.ReadAllText(file);
                if (json.Contains("\"type\":\"draw_calls\""))
                    record = json;
            }
            Assert.IsNotNull(record, $"no draw_calls event (max {maxDraws} draw calls a frame)");
            Assert.IsTrue(record.Contains("\"frameMatchesTrigger\":true"), "the draw_calls event was not matched to its frame");
        }
    }
}
