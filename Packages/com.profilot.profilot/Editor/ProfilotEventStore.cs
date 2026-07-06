using System;
using System.Collections.Generic;
using System.IO;

namespace Profilot.Editor
{
    /// <summary>
    /// Per-run file store (SPEC.md section 12 / 14). Each Play run gets its own folder,
    /// Library/Profilot/runs/&lt;runId&gt;/evt_*.json, so results can be distinguished, navigated,
    /// and deleted by run. A top-level latest.json points at the most recent event, and
    /// reviews.json (top-level, keyed by eventId - a decision is about the problem, not the run)
    /// holds the user's review decisions. Lives under Library/ so it stays out of the Assets
    /// and version control.
    /// </summary>
    internal static class ProfilotEventStore
    {
        public static string Base => Path.Combine(Directory.GetCurrentDirectory(), "Library", "Profilot");
        public static string RunsRoot => Path.Combine(Base, "runs");
        public static string LatestPath => Path.Combine(Base, "latest.json");
        public static string ReviewsPath => Path.Combine(Base, "reviews.json");

        // The run currently being written; set by the capture service on play start.
        public static string CurrentRun = "session";

        // Cap per run so one pathological run cannot grow without bound; dedup already keeps
        // this to the number of DISTINCT problems in the run.
        private const int MaxEventsPerRun = 200;

        public static string RunDir(string runId) => Path.Combine(RunsRoot, runId);
        public static string EventPath(string runId, string eventId) => Path.Combine(RunDir(runId), eventId + ".json");

        /// <summary>Writes the event into the current run's folder, then updates the top-level
        /// latest.json pointer (last, so it never references a half-written event).</summary>
        public static void Write(string eventId, string eventJson, string latestPointerJson)
        {
            Directory.CreateDirectory(RunDir(CurrentRun));
            WriteAtomic(EventPath(CurrentRun, eventId), eventJson);
            Directory.CreateDirectory(Base);
            WriteAtomic(LatestPath, latestPointerJson);
            PruneRun(CurrentRun);
        }

        /// <summary>Run ids, newest first (folder names are yyyy-MM-dd_HH-mm-ss, so an ordinal
        /// sort is chronological).</summary>
        public static List<string> ListRuns()
        {
            var runs = new List<string>();
            if (!Directory.Exists(RunsRoot))
                return runs;
            foreach (string d in Directory.GetDirectories(RunsRoot))
                runs.Add(Path.GetFileName(d));
            runs.Sort((a, b) => string.CompareOrdinal(b, a));
            return runs;
        }

        public static List<string> EventFiles(string runId)
        {
            var list = new List<string>();
            string dir = RunDir(runId);
            if (Directory.Exists(dir))
                list.AddRange(Directory.GetFiles(dir, "evt_*.json"));
            return list;
        }

        public static void ClearAll()
        {
            try { if (Directory.Exists(RunsRoot)) Directory.Delete(RunsRoot, true); } catch { /* ignore */ }
            try { if (File.Exists(LatestPath)) File.Delete(LatestPath); } catch { /* ignore */ }
        }

        public static void ClearRun(string runId)
        {
            try { string d = RunDir(runId); if (Directory.Exists(d)) Directory.Delete(d, true); }
            catch { /* ignore */ }
        }

        public static void ClearEarlierRuns(string keepRunId)
        {
            if (!Directory.Exists(RunsRoot))
                return;
            foreach (string d in Directory.GetDirectories(RunsRoot))
            {
                if (Path.GetFileName(d) != keepRunId)
                {
                    try { Directory.Delete(d, true); } catch { /* ignore */ }
                }
            }
        }

        /// <summary>Age-based retention: drop whole run folders older than maxDays.</summary>
        public static void PruneByAge(int maxDays)
        {
            if (!Directory.Exists(RunsRoot))
                return;
            DateTime cutoff = DateTime.UtcNow.AddDays(-maxDays);
            foreach (string d in Directory.GetDirectories(RunsRoot))
            {
                try { if (Directory.GetLastWriteTimeUtc(d) < cutoff) Directory.Delete(d, true); }
                catch { /* ignore */ }
            }
        }

        private static void PruneRun(string runId)
        {
            string[] files = Directory.GetFiles(RunDir(runId), "evt_*.json");
            if (files.Length <= MaxEventsPerRun)
                return;
            Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)));
            for (int i = 0; i < files.Length - MaxEventsPerRun; i++)
            {
                try { File.Delete(files[i]); } catch { /* ignore */ }
            }
        }

        // Persisted review decisions (eventId -> status), top-level so a decision applies across
        // runs. Read raw JSON or null; the capture service owns parsing.
        public static string ReadReviews()
        {
            return File.Exists(ReviewsPath) ? File.ReadAllText(ReviewsPath) : null;
        }

        public static void WriteReviews(string json)
        {
            Directory.CreateDirectory(Base);
            WriteAtomic(ReviewsPath, json);
        }

        private static void WriteAtomic(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
