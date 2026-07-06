using System.IO;
using UnityEngine;

namespace Profilot
{
    /// <summary>
    /// User-tunable Profilot settings, shared by the runtime tripwire (thresholds) and the
    /// editor (retention). Persisted as JSON at ProjectSettings/ProfilotConfig.json so it
    /// survives a Library wipe and can be committed / shared. The runtime only reads it; the
    /// editor settings UI writes it (<see cref="ProfilotConfigStore.Save"/>).
    /// </summary>
    [System.Serializable]
    public class ProfilotConfig
    {
        // Detection thresholds - read by the tripwire at OnEnable.
        public float frameHitchMultiplier = 2.0f;   // a frame N times its rolling baseline is a hitch
        public float frameHitchFloorMs = 12.0f;      // ...and at least this long in absolute terms
        public long gcAllocBudgetBytes = 0;          // any in-frame allocation above this is a candidate
        public float drawCallsBaselineMultiplier = 1.5f;
        public double cooldownSeconds = 2.0;         // one signal per type per this window
        public int warmupFrames = 60;                // ignore the first frames after Play

        // Retention - applied by the editor on play start.
        public bool pruneEnabled = true;
        public int retentionDays = 30;               // run folders older than this are dropped
    }

    /// <summary>Loads / saves <see cref="ProfilotConfig"/>. Runtime-safe (reads via File IO).</summary>
    public static class ProfilotConfigStore
    {
        public static string Path
        {
            get { return System.IO.Path.Combine(Directory.GetCurrentDirectory(), "ProjectSettings", "ProfilotConfig.json"); }
        }

        public static ProfilotConfig Load()
        {
            try
            {
                if (File.Exists(Path))
                    return JsonUtility.FromJson<ProfilotConfig>(File.ReadAllText(Path)) ?? new ProfilotConfig();
            }
            catch { /* a corrupt config should never break Play - fall back to defaults */ }
            return new ProfilotConfig();
        }

        public static void Save(ProfilotConfig config)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Path);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(Path, JsonUtility.ToJson(config, true));
            }
            catch { /* best effort */ }
        }
    }
}
