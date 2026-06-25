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

        /// <summary>
        /// Writes the event file atomically, then updates the latest.json pointer atomically.
        /// Order matters: the pointer must never reference an event that is not fully on disk.
        /// </summary>
        public static void Write(string eventId, string eventJson, string latestPointerJson)
        {
            Directory.CreateDirectory(Root);
            WriteAtomic(EventPath(eventId), eventJson);
            WriteAtomic(LatestPath, latestPointerJson);
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
