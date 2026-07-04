using UnityEditor;
using UnityEngine;

namespace Profilot.Editor
{
    /// <summary>
    /// Proactive, cheap notifications when a NEW problem is first caught in a Play session
    /// (SPEC.md section 9, screen 2). Fires once per distinct problem - the dedup already folds
    /// repeats - so it never spams. No LLM is touched (all data is already in the record);
    /// diagnosis stays on-demand (SPEC.md G5/M7). Each channel is individually toggleable and
    /// persisted per-user in EditorPrefs. Every channel is best-effort and wrapped so a
    /// notification failure can never break the capture pipeline.
    /// </summary>
    internal static class ProfilotNotifier
    {
        private const string Prefix = "Profilot.Notify.";

        // Channel toggles (persisted per-user). Console / Game View toast / window flash are on
        // by default; sound is opt-in so a beep on every new problem is never a surprise.
        public static bool Console { get => EditorPrefs.GetBool(Prefix + "Console", true); set => EditorPrefs.SetBool(Prefix + "Console", value); }
        public static bool Toast { get => EditorPrefs.GetBool(Prefix + "Toast", true); set => EditorPrefs.SetBool(Prefix + "Toast", value); }
        public static bool WindowFlash { get => EditorPrefs.GetBool(Prefix + "WindowFlash", true); set => EditorPrefs.SetBool(Prefix + "WindowFlash", value); }
        public static bool Sound { get => EditorPrefs.GetBool(Prefix + "Sound", false); set => EditorPrefs.SetBool(Prefix + "Sound", value); }

        public static void OnProblemCaught(string eventId, string type, string marker, int count)
        {
            string headline = $"{type} · {CleanMarker(marker)} ×{count}";

            if (Console) TryConsole(headline, eventId);
            if (Toast) TryToast(headline);
            if (WindowFlash) TryWindowFlash(headline);
            if (Sound) TrySound();
        }

        // The raw dominant marker is assembly-qualified and decorated
        // (e.g. "NeonRunner.Runtime.dll!NeonRunner.UI::GameHud.OnGUI() [Invoke]"). Trim to the
        // readable "Class.Method" tail for the notification headline.
        private static string CleanMarker(string marker)
        {
            if (string.IsNullOrEmpty(marker))
                return "(no marker)";

            string s = marker.Replace("() [Invoke]", string.Empty).Replace("[Invoke]", string.Empty).Replace("()", string.Empty);
            int colons = s.LastIndexOf("::", System.StringComparison.Ordinal);
            if (colons >= 0) s = s.Substring(colons + 2);
            int bang = s.LastIndexOf('!');
            if (bang >= 0) s = s.Substring(bang + 1);
            return s.Trim();
        }

        private static void TryConsole(string headline, string eventId)
        {
            // Editor-side (once per new problem), so there is no runtime hot-path allocation
            // cost - the diagnose command is in the text to copy straight into Claude Code.
            try { Debug.LogWarning($"[Profilot] caught {headline}. Diagnose: profilot diagnose --id {eventId}"); }
            catch { /* best effort */ }
        }

        private static void TryToast(string headline)
        {
            // A fading notification on the Game View (where you are looking during Play). Found
            // without stealing focus or creating a view; skipped cleanly if none is open.
            try
            {
                var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameViewType == null) return;
                var content = new GUIContent($"⚠ Profilot: {headline}");
                foreach (var obj in Resources.FindObjectsOfTypeAll(gameViewType))
                {
                    if (obj is EditorWindow gv)
                    {
                        gv.ShowNotification(content, 4.0);
                        gv.Repaint();
                    }
                }
            }
            catch { /* best effort */ }
        }

        private static void TryWindowFlash(string headline)
        {
            // Bring the Profilot window forward (opening it if needed) so the caught-issue list
            // and its Copy-diagnose buttons are right there.
            try
            {
                var w = EditorWindow.GetWindow<ProfilotWindow>(false, "Profilot", true);
                w.ShowNotification(new GUIContent($"⚠ {headline}"), 4.0);
                w.Repaint();
            }
            catch { /* best effort */ }
        }

        private static void TrySound()
        {
            try { System.Console.Beep(); }
            catch { /* best effort - not available on every platform */ }
        }
    }
}
