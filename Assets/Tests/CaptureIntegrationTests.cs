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

        private static string RunsDir =>
            Path.Combine(Directory.GetCurrentDirectory(), "Library", "Profilot", "runs");

        [UnityTest]
        public IEnumerator Tripwire_captures_a_gc_spike_event()
        {
            // Drop earlier events but keep the run folders: entering Play already created this
            // run's folder and run.json, which is part of what is asserted below.
            if (Directory.Exists(RunsDir))
                foreach (string old in Directory.GetFiles(RunsDir, "evt_*.json", SearchOption.AllDirectories))
                    File.Delete(old);

            var go = new GameObject("Allocator");
            go.AddComponent<Allocator>();

            // Past the warm-up (60 frames) plus a trip and the editor-side capture that runs
            // on the next EditorApplication.update.
            for (int i = 0; i < 200; i++)
                yield return null;

            Object.Destroy(go);
            yield return null;

            Assert.IsTrue(Directory.Exists(RunsDir), "run store directory was not created");

            // Events live in per-run folders now (runs/<runId>/evt_*.json).
            string[] files = Directory.GetFiles(RunsDir, "evt_*.json", SearchOption.AllDirectories);
            Assert.IsNotEmpty(files, "no events were captured after allocating every frame");

            // A file existing is not enough: v0.5.1 shipped with every record frameless
            // (frameIndex -1, no markers) and this test still passed. The allocator's gc_spike
            // must come from a real profiler frame and name the allocator.
            string record = null;
            foreach (string file in files)
            {
                string json = File.ReadAllText(file);
                if (json.Contains("\"type\":\"gc_spike\"") && json.Contains("Allocator.Update"))
                {
                    record = json;
                    break;
                }
            }

            Assert.IsNotNull(record, "expected a gc_spike event mapped to Allocator.Update");
            Assert.IsTrue(record.Contains("\"status\":\"ok\""), "the gc_spike record has no profiler frame");
            Assert.IsFalse(record.Contains("\"frameIndex\":-1"), "the gc_spike record has no profiler frame");
            Assert.IsTrue(record.Contains("\"frameMatchesTrigger\":true"), "the captured frame does not match the trip");
            Assert.IsTrue(record.Contains("\"schemaVersion\":\"3\""), "the record does not carry the current schema version");
            Assert.IsTrue(record.Contains("\"peakValue\":"), "the record does not keep the problem's peak");
            Assert.IsFalse(record.Contains("\"stale\""), "stale is decided by the CLI, not stored");
            Assert.IsFalse(record.Contains("\"writerVersion\":\"unknown\""), "the record does not say which package wrote it");

            string meta = Path.Combine(Directory.GetCurrentDirectory(), "Library", "Profilot", "meta.json");
            Assert.IsTrue(File.Exists(meta), "meta.json was not written on entering Play");

            // Every Play leaves a run folder with run.json, so a run that catches nothing is on
            // record and "not seen in the newest run" means a clean run.
            string runDir = Path.GetDirectoryName(files[0]);
            Assert.IsTrue(File.Exists(Path.Combine(runDir, "run.json")), "run.json was not written on entering Play");
        }
    }
}
