using System.IO;

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
