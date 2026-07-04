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
        private static string EventsDir =>
            Path.Combine(Directory.GetCurrentDirectory(), "Library", "Profilot", "events");

        private static void ClearStore()
        {
            if (Directory.Exists(EventsDir))
                Directory.Delete(EventsDir, true);
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
            if (!Directory.Exists(EventsDir))
                return false;

            foreach (string file in Directory.GetFiles(EventsDir, "evt_*.json"))
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
    }
}
