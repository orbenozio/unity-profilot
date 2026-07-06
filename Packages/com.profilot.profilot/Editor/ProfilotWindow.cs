using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Profilot.Editor
{
    /// <summary>
    /// The Profilot editor window (SPEC.md section 9). A view over the per-run event store plus
    /// the settings surface: colored section headers, collapsible groups, tooltip-annotated
    /// parameters, and icon buttons for compact actions. Navigate runs, act on caught issues
    /// (copy diagnose command, reviewed / not-an-issue / reopen), and tune thresholds/retention.
    /// </summary>
    internal sealed class ProfilotWindow : EditorWindow
    {
        [Serializable] private class EventSummary
        {
            public string eventId;
            public string status;
            public string reviewStatus;
            public string sessionId;
            public string capturedAt;
            public Trigger trigger;
            public Dedup dedup;
            public Marker[] topMarkers;
        }

        [Serializable] private class Trigger { public string type; public string severity; public string metric; public double value; public double budget; }
        [Serializable] private class Dedup { public int count; }
        [Serializable] private class Marker { public string name; }

        private Vector2 _scroll;
        private double _lastRepaint;
        private string _selectedRun;
        private ProfilotConfig _config;

        // Accent colors for section headers / states.
        private static readonly Color Blue = new Color(0.40f, 0.65f, 0.95f);
        private static readonly Color Orange = new Color(0.95f, 0.7f, 0.3f);
        private static readonly Color Green = new Color(0.45f, 0.82f, 0.45f);
        private static readonly Color Purple = new Color(0.72f, 0.6f, 0.95f);
        private static readonly Color Dim = new Color(0.6f, 0.6f, 0.6f);

        [MenuItem("Tools/Profilot/Window")]
        private static void Open()
        {
            ProfilotWindow w = GetWindow<ProfilotWindow>("Profilot");
            w.minSize = new Vector2(430, 340);
        }

        private void OnEnable()
        {
            _config = ProfilotConfigStore.Load();
            EditorApplication.update += Tick;
        }

        private void OnDisable() => EditorApplication.update -= Tick;

        private void Tick()
        {
            if (EditorApplication.timeSinceStartup - _lastRepaint > 0.5)
            {
                _lastRepaint = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        private void OnGUI()
        {
            DrawHeader();
            DrawCaptureSection();
            DrawSettingsSection();
            DrawRunsSection();
        }

        // ---- reusable UI helpers ------------------------------------------------------------

        // A colored header strip: a tinted bar with the title in the accent color.
        private static void HeaderBar(string title, Color accent)
        {
            EditorGUILayout.Space(4);
            Rect r = EditorGUILayout.GetControlRect(false, 18);
            EditorGUI.DrawRect(r, new Color(accent.r, accent.g, accent.b, 0.14f));
            var style = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = accent } };
            GUI.Label(new Rect(r.x + 5, r.y, r.width - 5, r.height), title, style);
        }

        // A collapsible colored header. Foldout state is persisted per key.
        private static bool FoldoutBar(string key, string title, Color accent, bool def = true)
        {
            bool cur = EditorPrefs.GetBool(key, def);
            Rect r = EditorGUILayout.GetControlRect(false, 18);
            EditorGUI.DrawRect(r, new Color(accent.r, accent.g, accent.b, 0.14f));
            var style = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            style.normal.textColor = style.onNormal.textColor = accent;
            style.focused.textColor = style.onFocused.textColor = accent;
            style.active.textColor = style.onActive.textColor = accent;
            bool now = EditorGUI.Foldout(new Rect(r.x + 2, r.y, r.width - 2, r.height), cur, title, true, style);
            if (now != cur) EditorPrefs.SetBool(key, now);
            return now;
        }

        // Icon button (built-in editor icon, theme-aware, crisp). Falls back to text if the
        // named icon is unavailable on this Unity version.
        private static bool IconButton(string iconName, string tooltip, float width = 30f)
        {
            GUIContent c = null;
            try { c = new GUIContent(EditorGUIUtility.IconContent(iconName)) { tooltip = tooltip }; }
            catch { /* fall through */ }
            if (c == null || c.image == null)
                c = new GUIContent(tooltip, tooltip);
            return GUILayout.Button(c, GUILayout.Width(width), GUILayout.Height(20));
        }

        // ---- header + capture ---------------------------------------------------------------

        private void DrawHeader()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Profilot", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            Color prev = GUI.color;
            GUI.color = EditorApplication.isPlaying ? Green : Dim;
            EditorGUILayout.LabelField(EditorApplication.isPlaying ? "● monitoring" : "○ not playing",
                EditorStyles.miniLabel, GUILayout.Width(100));
            GUI.color = prev;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawCaptureSection()
        {
            HeaderBar("Capture", Blue);
            bool deep = ProfilotEventCapture.DeepCapture;
            bool now = EditorGUILayout.ToggleLeft(
                new GUIContent("Deep capture  (map problems to code)",
                    "ON (default): keeps the Unity Profiler recording so a caught problem maps to " +
                    "the exact file and line. Measured to add negligible per-frame cost.\n\n" +
                    "OFF: counter-only events (type, size, frequency) with no marker tree - maximum " +
                    "Editor speed, but you lose code mapping."),
                deep);
            if (now != deep)
                ProfilotEventCapture.DeepCapture = now;
            EditorGUILayout.LabelField(
                now ? "Problems map to the exact file and line." : "Counters only - no code mapping.",
                EditorStyles.miniLabel);
        }

        // ---- settings -----------------------------------------------------------------------

        private void DrawSettingsSection()
        {
            bool open = FoldoutBar("Profilot.UI.Settings", "Settings", Purple, false);
            if (!open)
                return;

            EditorGUI.indentLevel++;

            if (FoldoutBar("Profilot.UI.Thresholds", "Thresholds", Purple))
            {
                EditorGUI.indentLevel++;
                EditorGUI.BeginChangeCheck();
                _config.frameHitchMultiplier = EditorGUILayout.FloatField(
                    new GUIContent("Frame hitch  (x baseline)",
                        "A frame is a hitch when it takes at least this many times the project's own " +
                        "rolling average frame time.\nUnit: multiplier (2 = twice the baseline).\n" +
                        "Higher = less sensitive (only bigger stutters trip)."),
                    _config.frameHitchMultiplier);
                _config.frameHitchFloorMs = EditorGUILayout.FloatField(
                    new GUIContent("Frame hitch floor  (ms)",
                        "...and the frame must also be at least this long, so tiny relative spikes on a " +
                        "fast scene are ignored.\nUnit: milliseconds.\nHigher = fewer, larger hitches."),
                    _config.frameHitchFloorMs);
                _config.gcAllocBudgetBytes = EditorGUILayout.LongField(
                    new GUIContent("GC budget  (bytes/frame)",
                        "In-frame allocation above this trips a gc_spike.\nUnit: bytes per frame.\n" +
                        "0 = flag any per-frame allocation (recommended)."),
                    _config.gcAllocBudgetBytes);
                _config.drawCallsBaselineMultiplier = EditorGUILayout.FloatField(
                    new GUIContent("Draw calls  (x baseline)",
                        "Draw calls above this many times the rolling average trip a draw_calls event.\n" +
                        "Unit: multiplier.\nHigher = less sensitive."),
                    _config.drawCallsBaselineMultiplier);
                _config.cooldownSeconds = EditorGUILayout.DoubleField(
                    new GUIContent("Cooldown  (s)",
                        "At most one alert per problem type within this window, so a recurring spike does " +
                        "not flood you.\nUnit: seconds.\nHigher = quieter."),
                    _config.cooldownSeconds);
                _config.warmupFrames = EditorGUILayout.IntField(
                    new GUIContent("Warm-up  (frames)",
                        "Ignore this many frames right after entering Play - scene load and JIT are always " +
                        "slow and would false-trigger.\nUnit: frames."),
                    _config.warmupFrames);
                if (EditorGUI.EndChangeCheck())
                    ProfilotConfigStore.Save(_config);
                EditorGUILayout.LabelField("Applies on the next Play.", EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }

            if (FoldoutBar("Profilot.UI.Retention", "Retention", Purple))
            {
                EditorGUI.indentLevel++;
                EditorGUI.BeginChangeCheck();
                _config.pruneEnabled = EditorGUILayout.ToggleLeft(
                    new GUIContent("Auto-delete old runs",
                        "When on, run folders older than the age below are deleted on each Play start."),
                    _config.pruneEnabled);
                using (new EditorGUI.DisabledScope(!_config.pruneEnabled))
                    _config.retentionDays = EditorGUILayout.IntField(
                        new GUIContent("Older than  (days)", "Runs older than this are auto-deleted.\nUnit: days."),
                        _config.retentionDays);
                if (EditorGUI.EndChangeCheck())
                    ProfilotConfigStore.Save(_config);
                EditorGUI.indentLevel--;
            }

            if (FoldoutBar("Profilot.UI.Notifications", "Notifications", Purple))
            {
                EditorGUI.indentLevel++;
                ProfilotNotifier.Console = EditorGUILayout.ToggleLeft(
                    new GUIContent("Console warning", "Log a Console warning (with the ready diagnose command) on a new problem."),
                    ProfilotNotifier.Console);
                ProfilotNotifier.Toast = EditorGUILayout.ToggleLeft(
                    new GUIContent("Game View toast", "Show a fading banner in the Game View, where you are looking during Play."),
                    ProfilotNotifier.Toast);
                ProfilotNotifier.WindowFlash = EditorGUILayout.ToggleLeft(
                    new GUIContent("Flash this window", "Flash the Profilot window if it is already open (never steals focus)."),
                    ProfilotNotifier.WindowFlash);
                ProfilotNotifier.Sound = EditorGUILayout.ToggleLeft(
                    new GUIContent("Sound", "Play a short beep on a new problem (off by default)."),
                    ProfilotNotifier.Sound);
                EditorGUILayout.LabelField("Fires once per new problem, per run. No LLM cost.", EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(2);
            if (IconButtonWide("Refresh", "Reset all settings to defaults", "Reset to defaults"))
            {
                _config = new ProfilotConfig();
                ProfilotConfigStore.Save(_config);
            }
            EditorGUI.indentLevel--;
        }

        private static bool IconButtonWide(string iconName, string tooltip, string text)
        {
            GUIContent c;
            try { c = new GUIContent(text, EditorGUIUtility.IconContent(iconName).image, tooltip); }
            catch { c = new GUIContent(text, tooltip); }
            return GUILayout.Button(c, GUILayout.Height(20));
        }

        // ---- runs + events ------------------------------------------------------------------

        private void DrawRunsSection()
        {
            List<string> runs = ProfilotEventStore.ListRuns();
            HeaderBar("Runs", Orange);

            if (runs.Count == 0)
            {
                if (EditorApplication.isPlaying)
                    DrawQuiet();
                else
                    DrawOnboarding();
                return;
            }

            if (string.IsNullOrEmpty(_selectedRun) || !runs.Contains(_selectedRun))
                _selectedRun = runs[0];

            DrawRunPicker(runs);

            List<EventSummary> events = LoadEvents(_selectedRun);
            bool isCurrent = _selectedRun == ProfilotEventCapture.CurrentSessionId;

            EditorGUILayout.Space(2);
            if (events.Count == 0)
            {
                if (isCurrent && EditorApplication.isPlaying)
                    DrawQuiet();
                else
                    EditorGUILayout.LabelField("No results in this run.", EditorStyles.miniLabel);
            }
            else
            {
                Color prev = GUI.color;
                GUI.color = Orange;
                EditorGUILayout.LabelField($"⚠  {events.Count} issue(s) in this run", EditorStyles.miniBoldLabel);
                GUI.color = prev;

                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                for (int i = 0; i < events.Count; i++)
                    DrawEventCard(events[i]);
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawRunPicker(List<string> runs)
        {
            var labels = new string[runs.Count];
            string current = ProfilotEventCapture.CurrentSessionId;
            for (int i = 0; i < runs.Count; i++)
                labels[i] = runs[i].Replace('_', ' ') + (runs[i] == current ? "   (current)" : string.Empty);

            int idx = Mathf.Max(0, runs.IndexOf(_selectedRun));
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(new GUIContent("Run", "Each Play session is a run, named by its start time. Pick one to view its results."), GUILayout.Width(30));
            int newIdx = EditorGUILayout.Popup(idx, labels);
            if (newIdx != idx)
                _selectedRun = runs[newIdx];

            if (IconButton("Folder Icon", "Open the events folder in the file browser"))
                OpenFolder();
            if (IconButton("TreeEditor.Trash", "Delete this run's results"))
            {
                if (EditorUtility.DisplayDialog("Profilot", $"Delete run {_selectedRun}?", "Delete", "Cancel"))
                {
                    ProfilotEventStore.ClearRun(_selectedRun);
                    _selectedRun = null;
                    Repaint();
                }
            }
            if (GUILayout.Button(new GUIContent("Clear all", "Delete ALL captured runs"), GUILayout.Width(66), GUILayout.Height(20)))
            {
                if (EditorUtility.DisplayDialog("Profilot", "Delete ALL captured runs?", "Delete", "Cancel"))
                {
                    ProfilotEventStore.ClearAll();
                    _selectedRun = null;
                    Repaint();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawEventCard(EventSummary e)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            Trigger t = e.trigger ?? new Trigger();
            int count = e.dedup != null ? e.dedup.count : 1;

            Color prev = GUI.color;
            GUI.color = SeverityColor(t.severity);
            EditorGUILayout.LabelField(new GUIContent($"{t.type}   ·   {t.severity}   ·   x{count}",
                    "Trigger type, severity, and how many times it was seen this run (dedup count)."),
                EditorStyles.boldLabel);
            GUI.color = prev;

            string marker;
            if (e.topMarkers != null && e.topMarkers.Length > 0)
                marker = e.topMarkers[0].name;
            else if (e.status == "counters_only")
                marker = "(no marker - deep capture off; enable to map to code)";
            else
                marker = "(no marker - data may be partial)";
            EditorGUILayout.LabelField(new GUIContent($"top: {marker}", "The dominant marker - the code Profilot blames."), EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"{t.metric} {t.value:0.##} (budget {t.budget:0.##})", EditorStyles.miniLabel);

            if (GUILayout.Button(new GUIContent("Copy diagnose command", "Copies 'profilot diagnose --id ...' - paste it to Claude Code to get the fix."), GUILayout.Height(22)))
            {
                EditorGUIUtility.systemCopyBuffer = $"profilot diagnose --id {e.eventId}";
                ShowNotification(new GUIContent("Copied - paste into Claude Code"));
            }

            string review = ProfilotEventCapture.ReviewStatusOf(e.eventId);
            bool marked = review == "reviewed" || review == "not_a_real_issue";

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Reviewed", "Mark handled - mutes further notifications for this problem."), GUILayout.Height(20)))
                Mark(e.eventId, "reviewed");
            if (GUILayout.Button(new GUIContent("Not an issue", "Mute this problem as noise - no more notifications until reopened."), GUILayout.Height(20)))
                Mark(e.eventId, "not_a_real_issue");
            using (new EditorGUI.DisabledScope(!marked))
            {
                if (GUILayout.Button(new GUIContent("Reopen", "Return to open and resume notifications."), GUILayout.Height(20), GUILayout.Width(70)))
                    Mark(e.eventId, "open");
            }
            EditorGUILayout.EndHorizontal();

            if (marked)
            {
                EditorGUILayout.LabelField(
                    review == "not_a_real_issue" ? "not an issue - muted (Reopen to resume)"
                                                 : "reviewed - muted (Reopen to resume)",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        private void DrawQuiet()
        {
            EditorGUILayout.Space(6);
            Color prev = GUI.color;
            GUI.color = Green;
            EditorGUILayout.LabelField("● Active - monitoring", EditorStyles.miniBoldLabel);
            GUI.color = prev;
            EditorGUILayout.LabelField("No issues caught. Everything is within budget.");
        }

        private void DrawOnboarding()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "No runs captured yet. Enter Play Mode and Profilot will watch for performance " +
                "spikes on its own. Results are grouped by run.",
                MessageType.Info);
            if (GUILayout.Button("Enter Play Mode", GUILayout.Height(26)))
                EditorApplication.isPlaying = true;
        }

        private void Mark(string eventId, string status)
        {
            ProfilotEventCapture.MarkReviewed(eventId, status);
            Repaint();
        }

        private static void OpenFolder()
        {
            string dir = ProfilotEventStore.RunsRoot;
            Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

        private static Color SeverityColor(string severity)
        {
            switch (severity)
            {
                case "high": return new Color(0.95f, 0.4f, 0.4f);
                case "medium": return Orange;
                default: return new Color(0.7f, 0.7f, 0.7f);
            }
        }

        private static List<EventSummary> LoadEvents(string runId)
        {
            var result = new List<EventSummary>();
            foreach (string path in ProfilotEventStore.EventFiles(runId))
            {
                try
                {
                    EventSummary e = JsonUtility.FromJson<EventSummary>(File.ReadAllText(path));
                    if (e != null && !string.IsNullOrEmpty(e.eventId))
                        result.Add(e);
                }
                catch { /* skip a file caught mid-write */ }
            }
            result.Sort((a, b) => string.CompareOrdinal(b.capturedAt, a.capturedAt));
            return result;
        }
    }
}
