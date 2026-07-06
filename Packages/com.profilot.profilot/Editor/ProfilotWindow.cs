using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Profilot.Editor
{
    /// <summary>
    /// The Profilot editor window (SPEC.md section 9). A view over the per-run event store, plus
    /// the settings surface. It shows the live states, a picker to navigate between runs, the
    /// caught-issue list for the selected run with per-event actions (copy diagnose command,
    /// reviewed / not-an-issue / reopen), and a Settings foldout for thresholds, retention, and
    /// notifications.
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
        private bool _showSettings;
        private string _selectedRun;
        private ProfilotConfig _config;

        private static readonly Color Orange = new Color(0.95f, 0.7f, 0.3f);
        private static readonly Color Green = new Color(0.45f, 0.8f, 0.45f);
        private static readonly Color Dim = new Color(0.6f, 0.6f, 0.6f);

        [MenuItem("Tools/Profilot/Window")]
        private static void Open()
        {
            ProfilotWindow w = GetWindow<ProfilotWindow>("Profilot");
            w.minSize = new Vector2(420, 320);
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
            DrawDeepCapture();
            DrawSettings();
            DrawSeparator();
            DrawRuns();
        }

        // ---- header + top-level controls ----------------------------------------------------

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
            DrawSeparator();
        }

        private void DrawDeepCapture()
        {
            bool deep = ProfilotEventCapture.DeepCapture;
            bool now = EditorGUILayout.ToggleLeft(
                new GUIContent("Deep capture  (map problems to code)",
                    "On (default): keeps the Unity Profiler recording so a caught problem maps to " +
                    "the exact code. Adds negligible per-frame cost. Turn off only for maximum " +
                    "Editor speed, giving up marker->code mapping (counter-only events)."),
                deep);
            if (now != deep)
                ProfilotEventCapture.DeepCapture = now;
            EditorGUILayout.LabelField(
                now ? "Problems map to the exact file and line." : "Counters only - no code mapping.",
                EditorStyles.miniLabel);
        }

        // ---- settings -----------------------------------------------------------------------

        private void DrawSettings()
        {
            _showSettings = EditorGUILayout.Foldout(_showSettings, "Settings", true);
            if (!_showSettings)
                return;

            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Thresholds", EditorStyles.boldLabel);
            _config.frameHitchMultiplier = EditorGUILayout.FloatField(
                new GUIContent("Frame hitch x baseline", "A frame this many times its rolling baseline is a hitch."),
                _config.frameHitchMultiplier);
            _config.frameHitchFloorMs = EditorGUILayout.FloatField(
                new GUIContent("Frame hitch floor (ms)", "...and at least this long, so tiny relative spikes are ignored."),
                _config.frameHitchFloorMs);
            _config.gcAllocBudgetBytes = EditorGUILayout.LongField(
                new GUIContent("GC budget (bytes/frame)", "In-frame allocation above this trips gc_spike (0 = any allocation)."),
                _config.gcAllocBudgetBytes);
            _config.drawCallsBaselineMultiplier = EditorGUILayout.FloatField(
                new GUIContent("Draw calls x baseline", "Draw calls this many times the rolling baseline trips draw_calls."),
                _config.drawCallsBaselineMultiplier);
            _config.cooldownSeconds = EditorGUILayout.DoubleField(
                new GUIContent("Cooldown (s)", "At most one signal per type per this window."),
                _config.cooldownSeconds);
            _config.warmupFrames = EditorGUILayout.IntField(
                new GUIContent("Warm-up (frames)", "Ignore the first frames after Play (scene init / JIT)."),
                _config.warmupFrames);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Retention", EditorStyles.boldLabel);
            _config.pruneEnabled = EditorGUILayout.ToggleLeft("Auto-delete old runs", _config.pruneEnabled);
            using (new EditorGUI.DisabledScope(!_config.pruneEnabled))
                _config.retentionDays = EditorGUILayout.IntField("Older than (days)", _config.retentionDays);

            if (EditorGUI.EndChangeCheck())
                ProfilotConfigStore.Save(_config);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Notifications  (on a new problem)", EditorStyles.boldLabel);
            ProfilotNotifier.Console = EditorGUILayout.ToggleLeft("Console warning", ProfilotNotifier.Console);
            ProfilotNotifier.Toast = EditorGUILayout.ToggleLeft("Game View toast", ProfilotNotifier.Toast);
            ProfilotNotifier.WindowFlash = EditorGUILayout.ToggleLeft("Flash this window", ProfilotNotifier.WindowFlash);
            ProfilotNotifier.Sound = EditorGUILayout.ToggleLeft("Sound", ProfilotNotifier.Sound);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Threshold changes apply on the next Play.", EditorStyles.miniLabel);
            if (GUILayout.Button("Reset to defaults", GUILayout.Height(20)))
            {
                _config = new ProfilotConfig();
                ProfilotConfigStore.Save(_config);
            }
            EditorGUI.indentLevel--;
        }

        // ---- runs + events ------------------------------------------------------------------

        private void DrawRuns()
        {
            List<string> runs = ProfilotEventStore.ListRuns();

            if (runs.Count == 0)
            {
                if (EditorApplication.isPlaying)
                    DrawQuiet();
                else
                    DrawOnboarding();
                return;
            }

            // Keep a valid selection (default to newest).
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
                    DrawEventCard(events[i], isCurrent);
                EditorGUILayout.EndScrollView();
            }

            DrawRunToolbar();
        }

        private void DrawRunPicker(List<string> runs)
        {
            var labels = new string[runs.Count];
            string current = ProfilotEventCapture.CurrentSessionId;
            for (int i = 0; i < runs.Count; i++)
                labels[i] = runs[i].Replace('_', ' ') + (runs[i] == current ? "   (current)" : string.Empty);

            int idx = Mathf.Max(0, runs.IndexOf(_selectedRun));
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Run", GUILayout.Width(30));
            int newIdx = EditorGUILayout.Popup(idx, labels);
            if (newIdx != idx)
                _selectedRun = runs[newIdx];
            if (GUILayout.Button("Open folder", GUILayout.Width(90), GUILayout.Height(18)))
                OpenFolder();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawRunToolbar()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Delete this run", GUILayout.Height(20)))
            {
                if (EditorUtility.DisplayDialog("Profilot", $"Delete run {_selectedRun}?", "Delete", "Cancel"))
                {
                    ProfilotEventStore.ClearRun(_selectedRun);
                    _selectedRun = null;
                    Repaint();
                }
            }
            if (GUILayout.Button("Clear all runs", GUILayout.Height(20)))
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

        private void DrawEventCard(EventSummary e, bool isCurrentRun)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            Trigger t = e.trigger ?? new Trigger();
            int count = e.dedup != null ? e.dedup.count : 1;

            Color prev = GUI.color;
            GUI.color = SeverityColor(t.severity);
            EditorGUILayout.LabelField($"{t.type}   ·   {t.severity}   ·   x{count}", EditorStyles.boldLabel);
            GUI.color = prev;

            string marker;
            if (e.topMarkers != null && e.topMarkers.Length > 0)
                marker = e.topMarkers[0].name;
            else if (e.status == "counters_only")
                marker = "(no marker - deep capture off; enable to map to code)";
            else
                marker = "(no marker - data may be partial)";
            EditorGUILayout.LabelField($"top: {marker}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"{t.metric} {t.value:0.##} (budget {t.budget:0.##})", EditorStyles.miniLabel);

            if (GUILayout.Button("Copy diagnose command", GUILayout.Height(22)))
            {
                EditorGUIUtility.systemCopyBuffer = $"profilot diagnose --id {e.eventId}";
                ShowNotification(new GUIContent("Copied - paste into Claude Code"));
            }

            string review = ProfilotEventCapture.ReviewStatusOf(e.eventId);
            bool marked = review == "reviewed" || review == "not_a_real_issue";

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Reviewed", GUILayout.Height(20)))
                Mark(e.eventId, "reviewed");
            if (GUILayout.Button("Not an issue", GUILayout.Height(20)))
                Mark(e.eventId, "not_a_real_issue");
            using (new EditorGUI.DisabledScope(!marked))
            {
                if (GUILayout.Button("Reopen", GUILayout.Height(20), GUILayout.Width(70)))
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

        private static void DrawSeparator()
        {
            Rect r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.2f));
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
