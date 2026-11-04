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
    /// holds the user's review decisions, and meta.json says which package version writes the
    /// store. Lives under Library/ so it stays out of the Assets
    /// and version control.
    /// </summary>
    internal static class ProfilotEventStore
    {
        public static string Base => BaseOverride ?? Path.Combine(Directory.GetCurrentDirectory(), "Library", "Profilot");

        // Tests point the store at a temp folder so they never touch the project's real one.
        internal static string BaseOverride;
        public static string RunsRoot => Path.Combine(Base, "runs");
        public static string LatestPath => Path.Combine(Base, "latest.json");
        public static string ReviewsPath => Path.Combine(Base, "reviews.json");
        public static string MetaPath => Path.Combine(Base, "meta.json");
        public const string RunInfoFile = "run.json";

        // The run currently being written; set by the capture service on play start.
        public static string CurrentRun = "editor";

        // Cap per run so one pathological run cannot grow without bound; dedup already keeps
        // this to the number of DISTINCT problems in the run.
        private const int MaxEventsPerRun = 200;

        // Every Play now leaves a run folder, so clean runs pile up; the newest few are enough to
        // show that a problem stopped coming back.
        private const int MaxCleanRuns = 20;

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

        /// <summary>Creates the run's folder and run.json when Play starts. A run that catches
        /// nothing still exists, so "not seen in the newest run" can mean "clean run" instead of
        /// the newest run silently being an older one.</summary>
        public static void StartRun(string runId, string runInfoJson)
        {
            Directory.CreateDirectory(RunDir(runId));
            WriteAtomic(Path.Combine(RunDir(runId), RunInfoFile), runInfoJson);
            PruneCleanRuns(runId);
        }

        public static void WriteRunInfo(string runId, string runInfoJson)
        {
            if (Directory.Exists(RunDir(runId)))
                WriteAtomic(Path.Combine(RunDir(runId), RunInfoFile), runInfoJson);
        }

        // Keeps the newest MaxCleanRuns runs that caught nothing (the current one always stays).
        private static void PruneCleanRuns(string keepRunId)
        {
            int clean = 0;
            foreach (string run in ListRuns())
            {
                if (run == keepRunId || EventFiles(run).Count > 0)
                    continue;
                if (++clean > MaxCleanRuns)
                    ClearRun(run);
            }
        }

        /// <summary>Run ids, newest first (folder names are yyyy-MM-dd_HH-mm-ss, so an ordinal
        /// sort is chronological). Any other folder name goes last, never first.</summary>
        public static List<string> ListRuns()
        {
            var runs = new List<string>();
            if (!Directory.Exists(RunsRoot))
                return runs;
            foreach (string d in Directory.GetDirectories(RunsRoot))
                runs.Add(Path.GetFileName(d));
            runs.Sort((a, b) =>
            {
                bool ta = IsRunId(a), tb = IsRunId(b);
                return ta != tb ? (ta ? -1 : 1) : string.CompareOrdinal(b, a);
            });
            return runs;
        }

        // yyyy-MM-dd_HH-mm-ss
        internal static bool IsRunId(string s)
        {
            if (s == null || s.Length != 19)
                return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool sep = i == 4 || i == 7 || i == 13 || i == 16;
                if (sep ? c != '-' : i == 10 ? c != '_' : !char.IsDigit(c))
                    return false;
            }
            return true;
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

        // Which package version and schema write this store (read by `profilot status`).
        public static void WriteMeta(string json)
        {
            Directory.CreateDirectory(Base);
            WriteAtomic(MetaPath, json);
        }

        private static void WriteAtomic(string path, string content)
        {
            // Replace in one step when the target exists: delete-then-move left a gap in which the
            // CLI could find no reviews.json and fall back to the statuses baked into records.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }
    }
}
