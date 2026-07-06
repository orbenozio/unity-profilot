using System.IO;
using System.Text.RegularExpressions;

namespace Profilot.Editor
{
    /// <summary>
    /// The file-based event store the CLI reads (SPEC.md section 12 / 14). One atomic JSON
    /// file per event under Library/Profilot/events/, plus a small latest.json pointer that
    /// is written last so a reader never sees it point at a half-written event. Lives under
    /// Library/ so it stays out of version control and out of the user's Assets.
    /// </summary>
    internal static class ProfilotEventStore
    {
        public static string Root
        {
            get
            {
                // Directory.GetCurrentDirectory() is the Unity project root in the Editor.
                return Path.Combine(Directory.GetCurrentDirectory(), "Library", "Profilot", "events");
            }
        }

        public static string LatestPath
        {
            get { return Path.Combine(Root, "latest.json"); }
        }

        public static string EventPath(string eventId)
        {
            return Path.Combine(Root, eventId + ".json");
        }

        // Persisted user review decisions (eventId -> status). Kept next to the events dir (not
        // inside it, so the evt_*.json glob never picks it up) so that "reviewed" and
        // "not_a_real_issue" survive Play sessions and editor restarts, instead of resetting to
        // "open" every session (SPEC.md JTBD-8).
        public static string ReviewsPath
        {
            get { return Path.Combine(Directory.GetCurrentDirectory(), "Library", "Profilot", "reviews.json"); }
        }

        public static string ReadReviews()
        {
            return File.Exists(ReviewsPath) ? File.ReadAllText(ReviewsPath) : null;
        }

        public static void WriteReviews(string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReviewsPath));
            WriteAtomic(ReviewsPath, json);
        }

        /// <summary>
        /// Writes the event file atomically, then updates the latest.json pointer atomically.
        /// Order matters: the pointer must never reference an event that is not fully on disk.
        /// </summary>
        // Bound on the number of event files. Dedup already collapses repeats of the same
        // problem into one file, so this only bites when there are many DISTINCT problems - a
        // long deep-capture session whose false hitches map to ever-changing markers. Keeps the
        // store (and the window's load-all-and-parse per repaint) from growing without limit.
        private const int MaxEvents = 200;

        public static void Write(string eventId, string eventJson, string latestPointerJson)
        {
            Directory.CreateDirectory(Root);
            WriteAtomic(EventPath(eventId), eventJson);
            WriteAtomic(LatestPath, latestPointerJson);
            Prune();
        }

        private static void Prune()
        {
            string[] files = Directory.GetFiles(Root, "evt_*.json");
            if (files.Length <= MaxEvents)
                return;

            // Oldest first (by last write), then delete the excess so the newest MaxEvents stay.
            System.Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)));
            int toDelete = files.Length - MaxEvents;
            for (int i = 0; i < toDelete; i++)
            {
                try { File.Delete(files[i]); }
                catch { /* another reader may hold it; it will be pruned next time */ }
            }
        }

        /// <summary>
        /// Rewrites only the reviewStatus field of an existing event file (the user marking
        /// it from the window, SPEC.md JTBD-8 / decision 2). A targeted replace so the rest
        /// of the record - markerTree and all - is preserved without re-serializing it.
        /// </summary>
        public static void SetReviewStatus(string eventId, string status)
        {
            string path = EventPath(eventId);
            if (!File.Exists(path))
                return;

            string json = File.ReadAllText(path);
            json = Regex.Replace(json, "\"reviewStatus\":\"[a-z_]+\"", $"\"reviewStatus\":\"{status}\"");
            WriteAtomic(path, json);
        }

        /// <summary>
        /// Marks every event currently in the store as stale (SPEC.md section 15). Called
        /// when a new Play session starts: anything already on disk is from a previous run,
        /// so its frame indices no longer match the live profiler. Events that recur this
        /// session are overwritten fresh (stale:false) by the capture service.
        /// </summary>
        public static void MarkAllStale()
        {
            if (!Directory.Exists(Root))
                return;

            foreach (string path in Directory.GetFiles(Root, "evt_*.json"))
            {
                string json = File.ReadAllText(path);
                if (json.Contains("\"stale\":false"))
                    WriteAtomic(path, json.Replace("\"stale\":false", "\"stale\":true"));
            }
        }

        /// <summary>Delete every captured event and the latest pointer (the user clearing the
        /// store from the window). Local dev data under Library/, always regenerable.</summary>
        public static void ClearAll()
        {
            if (!Directory.Exists(Root))
                return;
            foreach (string f in Directory.GetFiles(Root, "evt_*.json"))
            {
                try { File.Delete(f); } catch { /* ignore */ }
            }
            try { if (File.Exists(LatestPath)) File.Delete(LatestPath); } catch { /* ignore */ }
        }

        /// <summary>Delete only stale events - the leftovers from earlier runs that did not
        /// recur this run - keeping the current run's results.</summary>
        public static void ClearStale()
        {
            if (!Directory.Exists(Root))
                return;
            foreach (string f in Directory.GetFiles(Root, "evt_*.json"))
            {
                try { if (File.ReadAllText(f).Contains("\"stale\":true")) File.Delete(f); }
                catch { /* ignore */ }
            }
        }

        /// <summary>Age-based retention: drop event files older than maxDays (by last write),
        /// so results from long-gone runs do not pile up. Called on each play start.</summary>
        public static void PruneByAge(int maxDays)
        {
            if (!Directory.Exists(Root))
                return;
            System.DateTime cutoff = System.DateTime.UtcNow.AddDays(-maxDays);
            foreach (string f in Directory.GetFiles(Root, "evt_*.json"))
            {
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); }
                catch { /* ignore */ }
            }
        }

        private static void WriteAtomic(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);

            // File.Move does not overwrite on .NET Framework, so clear the target first.
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
