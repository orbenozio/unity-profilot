using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Profilot.Editor
{
    /// <summary>
    /// The Profilot editor window (SPEC.md section 9). A thin view over the event store: it
    /// shows the live states - not playing / quiet / events caught - and, for each captured
    /// problem, a one-line summary plus a button that copies the exact Claude Code command to
    /// diagnose it. Read-only for now; writing reviewStatus back is the next iteration.
    /// </summary>
    internal sealed class ProfilotWindow : EditorWindow
    {
        [Serializable] private class EventSummary
        {
            public string eventId;
            public string status;
            public string reviewStatus;
            public bool stale;
            public string capturedAt;
            public Trigger trigger;
            public Dedup dedup;
            public Marker[] topMarkers;
        }

        [Serializable] private class Trigger
        {
            public string type;
            public string severity;
            public string metric;
            public double value;
            public double budget;
        }

        [Serializable] private class Dedup { public int count; }

        [Serializable] private class Marker { public string name; }

        private Vector2 _scroll;
        private double _lastRepaint;
        private bool _showSettings;

        [MenuItem("Tools/Profilot/Window")]
        private static void Open()
        {
            ProfilotWindow w = GetWindow<ProfilotWindow>("Profilot");
            w.minSize = new Vector2(380, 260);
        }

        private void OnEnable() => EditorApplication.update += Tick;
        private void OnDisable() => EditorApplication.update -= Tick;

        private void Tick()
        {
            // Light auto-refresh while the window is open (the store changes underneath us).
            if (EditorApplication.timeSinceStartup - _lastRepaint > 0.5)
            {
                _lastRepaint = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        private void OnGUI()
        {
            DrawTitle();
            DrawCaptureMode();
            DrawNotificationSettings();

            if (!EditorApplication.isPlaying)
            {
                DrawNoData();
                return;
            }

            List<EventSummary> events = LoadEvents();
            if (events.Count == 0)
                DrawQuiet();
            else
                DrawEvents(events);
        }

        private void DrawCaptureMode()
        {
            bool deep = ProfilotEventCapture.DeepCapture;
            bool now = EditorGUILayout.ToggleLeft(
                new GUIContent("Deep capture (map to code, slower)",
                    "On: keeps the Unity Profiler recording so a caught problem maps to the exact " +
                    "code - at a real per-frame Editor cost. Off (default): cheap always-on " +
                    "catching with counters only, full Editor speed. Arm it when you want to diagnose."),
                deep);
            if (now != deep)
                ProfilotEventCapture.DeepCapture = now;

            EditorGUILayout.LabelField(
                deep ? "Deep: profiler on - maps to code, slower Editor Play." :
                       "Cheap: counters only, full speed. Enable to map problems to code.",
                EditorStyles.miniLabel);
            DrawSeparator();
        }

        private void DrawNotificationSettings()
        {
            _showSettings = EditorGUILayout.Foldout(_showSettings, "Notifications", true);
            if (!_showSettings)
                return;

            EditorGUI.indentLevel++;
            ProfilotNotifier.Console = EditorGUILayout.ToggleLeft("Console warning", ProfilotNotifier.Console);
            ProfilotNotifier.Toast = EditorGUILayout.ToggleLeft("Game View toast", ProfilotNotifier.Toast);
            ProfilotNotifier.WindowFlash = EditorGUILayout.ToggleLeft("Flash this window", ProfilotNotifier.WindowFlash);
            ProfilotNotifier.Sound = EditorGUILayout.ToggleLeft("Sound", ProfilotNotifier.Sound);
            EditorGUILayout.LabelField("Fires once per new problem, per Play session. No LLM cost.", EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
            DrawSeparator();
        }

        private void DrawTitle()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Profilot", EditorStyles.boldLabel);
            DrawSeparator();
        }

        private void DrawNoData()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("○  Not monitoring", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Profiler data is only available in Play Mode (in the Editor or a development build). " +
                "Enter Play Mode to start catching performance spikes.",
                MessageType.Info);
            if (GUILayout.Button("Enter Play Mode", GUILayout.Height(26)))
                EditorApplication.isPlaying = true;
        }

        private void DrawQuiet()
        {
            EditorGUILayout.Space(8);
            Color prev = GUI.color;
            GUI.color = new Color(0.45f, 0.8f, 0.45f);
            EditorGUILayout.LabelField("●  Active - monitoring", EditorStyles.miniBoldLabel);
            GUI.color = prev;
            EditorGUILayout.LabelField("No issues caught. Everything is within budget.");
        }

        private void DrawEvents(List<EventSummary> events)
        {
            EditorGUILayout.Space(4);
            Color prev = GUI.color;
            GUI.color = new Color(0.95f, 0.7f, 0.3f);
            EditorGUILayout.LabelField($"⚠  {events.Count} issue(s) caught", EditorStyles.miniBoldLabel);
            GUI.color = prev;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < events.Count; i++)
                DrawEventCard(events[i], isLatest: i == 0);
            EditorGUILayout.EndScrollView();
        }

        private void DrawEventCard(EventSummary e, bool isLatest)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            Trigger t = e.trigger ?? new Trigger();
            int count = e.dedup != null ? e.dedup.count : 1;
            string headline = $"{t.type}  ·  {t.severity}  ·  ×{count}";
            if (isLatest)
                headline += "   (latest)";

            Color prev = GUI.color;
            GUI.color = SeverityColor(t.severity);
            EditorGUILayout.LabelField(headline, EditorStyles.boldLabel);
            GUI.color = prev;

            if (e.stale)
                EditorGUILayout.LabelField("(from a previous session - frame may no longer match)", EditorStyles.miniLabel);

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

            bool marked = e.reviewStatus == "reviewed" || e.reviewStatus == "not_a_real_issue";

            // All actions stay available so a mark is never a dead end: switch between the two,
            // or Reopen back to open (which resumes notifications). SPEC.md JTBD-8.
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Reviewed", GUILayout.Height(22)))
                Mark(e.eventId, "reviewed");
            if (GUILayout.Button("Not an issue", GUILayout.Height(22)))
                Mark(e.eventId, "not_a_real_issue");
            using (new EditorGUI.DisabledScope(!marked))
            {
                if (GUILayout.Button("Reopen", GUILayout.Height(22), GUILayout.Width(70)))
                    Mark(e.eventId, "open");
            }
            EditorGUILayout.EndHorizontal();

            if (marked)
            {
                string note = e.reviewStatus == "not_a_real_issue"
                    ? "not an issue - muted, won't notify (Reopen to resume)"
                    : "reviewed - muted, won't notify (Reopen to resume)";
                EditorGUILayout.LabelField(note, EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        private void Mark(string eventId, string status)
        {
            // The editor owns the store write; the CLI stays read-only (SPEC.md decision 2).
            // Routed through the capture service so an ongoing spike keeps the status instead
            // of resetting it to "open" on the next overwrite.
            ProfilotEventCapture.MarkReviewed(eventId, status);
            Repaint();
        }

        private static Color SeverityColor(string severity)
        {
            switch (severity)
            {
                case "high": return new Color(0.95f, 0.4f, 0.4f);
                case "medium": return new Color(0.95f, 0.7f, 0.3f);
                default: return new Color(0.7f, 0.7f, 0.7f);
            }
        }

        private static void DrawSeparator()
        {
            Rect r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.2f));
        }

        private static List<EventSummary> LoadEvents()
        {
            var result = new List<EventSummary>();
            string dir = ProfilotEventStore.Root;
            if (!Directory.Exists(dir))
                return result;

            foreach (string path in Directory.GetFiles(dir, "evt_*.json"))
            {
                try
                {
                    EventSummary e = JsonUtility.FromJson<EventSummary>(File.ReadAllText(path));
                    if (e != null && !string.IsNullOrEmpty(e.eventId))
                        result.Add(e);
                }
                catch
                {
                    // Skip a file caught mid-write; it will read cleanly on the next repaint.
                }
            }

            // Newest first (capturedAt is ISO 8601, so a string sort is chronological).
            result.Sort((a, b) => string.CompareOrdinal(b.capturedAt, a.capturedAt));
            return result;
        }
    }
}
