using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Profilot.PlayTests
{
    /// <summary>
    /// PlayMode integration test for the live capture pipeline (SPEC.md sections 1-2): enter
    /// Play, allocate garbage every frame, and assert the tripwire + editor capture wrote a
    /// gc_spike event to the store. Runs headless via `-runTests -testPlatform PlayMode`.
    /// Asserts the robust end of the pipeline (an event was captured and classified); the
    /// marker tree itself is exercised live and is not asserted here, since profiler hierarchy
    /// detail can differ under -nographics.
    /// </summary>
    public class CaptureIntegrationTests
    {
        private sealed class Allocator : MonoBehaviour
        {
            public int AllocationsPerFrame = 500;
            private int _sink;

            private void Update()
            {
                var junk = new List<int>();
                for (int i = 0; i < AllocationsPerFrame; i++)
                    junk.Add(i);
                _sink += junk[junk.Count - 1];
            }
        }

        private static string EventsDir =>
            Path.Combine(Directory.GetCurrentDirectory(), "Library", "Profilot", "events");

        [UnityTest]
        public IEnumerator Tripwire_captures_a_gc_spike_event()
        {
            if (Directory.Exists(EventsDir))
                Directory.Delete(EventsDir, true);

            var go = new GameObject("Allocator");
            go.AddComponent<Allocator>();

            // Past the warm-up (60 frames) plus a trip and the editor-side capture that runs
            // on the next EditorApplication.update.
            for (int i = 0; i < 200; i++)
                yield return null;

            Object.Destroy(go);
            yield return null;

            Assert.IsTrue(Directory.Exists(EventsDir), "event store directory was not created");

            string[] files = Directory.GetFiles(EventsDir, "evt_*.json");
            Assert.IsNotEmpty(files, "no events were captured after allocating every frame");

            bool gcSpike = false;
            foreach (string file in files)
            {
                if (File.ReadAllText(file).Contains("\"type\":\"gc_spike\""))
                {
                    gcSpike = true;
                    break;
                }
            }

            Assert.IsTrue(gcSpike, "expected a gc_spike event from per-frame allocation");
        }
    }
}
