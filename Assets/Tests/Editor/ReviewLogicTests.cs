using System.IO;
using NUnit.Framework;
using Profilot.Editor;

namespace Profilot.EditorTests
{
    /// <summary>
    /// EditMode tests for the review decisions (Fixed / Ignore / regressed, seen again after
    /// Fixed) and the store's run naming. Each test runs against a throwaway store folder.
    /// </summary>
    public class ReviewLogicTests
    {
        private string _base;

        [SetUp]
        public void SetUp()
        {
            _base = Path.Combine(Path.GetTempPath(), "profilot-tests-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_base);
            ProfilotEventStore.BaseOverride = _base;
            ProfilotEventCapture.LoadReviews();
        }

        [TearDown]
        public void TearDown()
        {
            ProfilotEventStore.BaseOverride = null;
            ProfilotEventCapture.LoadReviews();
            try { Directory.Delete(_base, true); } catch { /* best effort */ }
        }

        private void WriteEvent(string run, string eventId, double value, double peak)
        {
            Directory.CreateDirectory(ProfilotEventStore.RunDir(run));
            File.WriteAllText(ProfilotEventStore.EventPath(run, eventId),
                "{\"eventId\":\"" + eventId + "\",\"trigger\":{\"value\":" + value + "},\"dedup\":{\"count\":1,\"peakValue\":" + peak + "}}");
        }

        private static TripSignal Signal(double value, int repeats = 0) =>
            new TripSignal("gc_spike", "gcAllocBytes", value, 0, 100, repeats, 1, (long)value, 0);

        [Test]
        public void Fixed_measures_against_the_peak_across_runs_not_the_clicked_card()
        {
            WriteEvent("2026-11-01_10-00-00", "evt_a", 500, 4000);
            WriteEvent("2026-11-02_10-00-00", "evt_a", 900, 900);
            ProfilotEventCapture.MarkReviewed("evt_a", "fixed");
            Assert.AreEqual(4000, ProfilotEventCapture.FixedValueOf("evt_a"));
        }

        [Test]
        public void Regresses_only_at_three_times_the_peak()
        {
            WriteEvent("2026-11-01_10-00-00", "evt_a", 1000, 1000);
            ProfilotEventCapture.MarkReviewed("evt_a", "fixed");
            Assert.IsFalse(ProfilotEventCapture.CheckRegression("evt_a", 2999));
            Assert.AreEqual("fixed", ProfilotEventCapture.ReviewStatusOf("evt_a"));
            Assert.IsTrue(ProfilotEventCapture.CheckRegression("evt_a", 3000));
            Assert.AreEqual("regressed", ProfilotEventCapture.ReviewStatusOf("evt_a"));

            // Persisted: a fresh load still says regressed.
            ProfilotEventCapture.LoadReviews();
            Assert.AreEqual("regressed", ProfilotEventCapture.ReviewStatusOf("evt_a"));
        }

        [Test]
        public void A_migrated_mark_with_no_value_takes_the_first_comeback_as_its_reference()
        {
            File.WriteAllText(ProfilotEventStore.ReviewsPath, "{\"items\":[{\"id\":\"evt_a\",\"status\":\"reviewed\"}]}");
            ProfilotEventCapture.LoadReviews();
            Assert.AreEqual("fixed", ProfilotEventCapture.ReviewStatusOf("evt_a"));
            Assert.IsFalse(ProfilotEventCapture.CheckRegression("evt_a", 700));
            Assert.AreEqual(700, ProfilotEventCapture.FixedValueOf("evt_a"));
        }

        [Test]
        public void Seen_again_below_the_line_is_counted_and_kept_muted()
        {
            WriteEvent("2026-11-01_10-00-00", "evt_a", 1000, 1000);
            ProfilotEventCapture.MarkReviewed("evt_a", "fixed");
            ProfilotEventCapture.NoteSeenAgain("evt_a", Signal(400, repeats: 2));
            ProfilotEventCapture.NoteSeenAgain("evt_a", Signal(600));

            Assert.IsTrue(ProfilotEventCapture.SeenAgainAfterFixed("evt_a", out int count, out _, out double max));
            Assert.AreEqual(4, count);
            Assert.AreEqual(600, max);
            Assert.AreEqual("fixed", ProfilotEventCapture.ReviewStatusOf("evt_a"));

            // Marking Fixed again starts over.
            ProfilotEventCapture.MarkReviewed("evt_a", "fixed");
            Assert.IsFalse(ProfilotEventCapture.SeenAgainAfterFixed("evt_a", out _, out _, out _));
        }

        [Test]
        public void Reopen_removes_the_decision_and_helped_is_remembered()
        {
            ProfilotEventCapture.MarkReviewed("evt_a", "ignored");
            ProfilotEventCapture.MarkHelped("evt_a", false);
            ProfilotEventCapture.LoadReviews();
            Assert.AreEqual("no", ProfilotEventCapture.HelpedOf("evt_a"));
            ProfilotEventCapture.MarkReviewed("evt_a", "open");
            ProfilotEventCapture.LoadReviews();
            Assert.AreEqual("open", ProfilotEventCapture.ReviewStatusOf("evt_a"));
            StringAssert.DoesNotContain("evt_a", File.ReadAllText(ProfilotEventStore.ReviewsPath));
        }

        [Test]
        public void Legacy_statuses_migrate()
        {
            Assert.AreEqual("fixed", ProfilotEventCapture.MigrateStatus("reviewed"));
            Assert.AreEqual("ignored", ProfilotEventCapture.MigrateStatus("not_a_real_issue"));
            Assert.AreEqual("regressed", ProfilotEventCapture.MigrateStatus("regressed"));
        }

        [Test]
        public void Runs_sort_newest_first_and_non_timestamp_folders_last()
        {
            foreach (string r in new[] { "session", "2026-11-01_10-00-00", "2026-11-02_09-00-00" })
                Directory.CreateDirectory(ProfilotEventStore.RunDir(r));
            CollectionAssert.AreEqual(new[] { "2026-11-02_09-00-00", "2026-11-01_10-00-00", "session" }, ProfilotEventStore.ListRuns());
            Assert.IsTrue(ProfilotEventStore.IsRunId("2026-11-02_09-00-00"));
            Assert.IsFalse(ProfilotEventStore.IsRunId("2026-11-02 09-00-00"));
        }

        [Test]
        public void StartRun_writes_run_json_and_keeps_only_the_newest_clean_runs()
        {
            for (int i = 0; i < 25; i++)
                Directory.CreateDirectory(ProfilotEventStore.RunDir($"2026-10-{i + 1:00}_10-00-00"));
            WriteEvent("2026-09-30_10-00-00", "evt_a", 1, 1);
            ProfilotEventStore.StartRun("2026-11-03_10-00-00", "{}");

            Assert.IsTrue(File.Exists(Path.Combine(ProfilotEventStore.RunDir("2026-11-03_10-00-00"), "run.json")));
            var runs = ProfilotEventStore.ListRuns();
            Assert.AreEqual(22, runs.Count, "current + 20 clean + the run with an event");
            Assert.Contains("2026-09-30_10-00-00", runs);
            Assert.IsFalse(runs.Contains("2026-10-01_10-00-00"));
        }

        [Test]
        public void Slug_keeps_the_class_and_method()
        {
            Assert.AreEqual("GarbageGenerator.Update",
                ProfilotEventCapture.Slug("Assembly-CSharp.dll!ProfilotDemo::GarbageGenerator.Update() [Invoke]"));
            Assert.AreEqual("unknown", ProfilotEventCapture.Slug(""));
        }
    }
}
