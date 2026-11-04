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
    /// (copy a prompt or the diagnose command, fixed / ignore / reopen), and tune thresholds and
    /// retention.
    /// </summary>
    internal sealed class ProfilotWindow : EditorWindow
    {
        [Serializable] private class EventSummary
        {
            public string schemaVersion;
            public string eventId;
            public string status;
            public bool frameMatchesTrigger;
            public string reviewStatus;
            public string sessionId;
            public string capturedAt;
            public Trigger trigger;
            public Dedup dedup;
            public Marker[] topMarkers;
        }

        [Serializable] private class Trigger { public string type; public string severity; public string metric; public double value; public double budget; }
        [Serializable] private class Dedup { public int count; public double peakValue; }
        [Serializable] private class Marker { public string name; }

        private Vector2 _scroll;
        private double _lastRepaint;
        private readonly Dictionary<string, int> _runCounts = new Dictionary<string, int>();
        private double _runCountsAt = -10;
        private string _selectedRun;
        private ProfilotConfig _config;

        // Parsed event cache. OnGUI runs on every repaint (and the window repaints twice a second
        // on its own), while LoadEvents reads and JSON-parses every event file in the run - so
        // without this the whole store is re-parsed several times a second for a list that only
        // changes when a trip lands. The records also carry a deep marker tree now, which makes
        // that re-parse meaningfully more expensive. Keyed on a cheap fingerprint of the run's
        // files, so a new or rewritten event still shows up on the next repaint.
        private string _cachedRun;
        private string _cachedStamp;
        private List<EventSummary> _cachedEvents;

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

        // One scroll for the whole window, issues first: the caught problems used to sit below
        // Capture and Settings, and with Settings open the Runs list fell off the bottom.
        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawHeader();
            DrawSetupWarning();
            DrawRunsSection();
            DrawCaptureSection();
            DrawSettingsSection();
            DrawSetupSection();
            EditorGUILayout.EndScrollView();
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
        private static bool IconButton(string iconName, string tooltip, string fallbackText, float width = 30f)
        {
            GUIContent c = null;
            try { c = new GUIContent(EditorGUIUtility.IconContent(iconName)) { tooltip = tooltip }; }
            catch { /* fall through */ }
            if (c == null || c.image == null)
                c = new GUIContent(fallbackText, tooltip);
            return GUILayout.Button(c, GUILayout.MinWidth(width), GUILayout.Height(20));
        }

        // ---- header + capture ---------------------------------------------------------------

        private void DrawHeader()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            Color prev = GUI.color;
            string state = MonitorState(out Color color, out _);
            GUI.color = color;
            GUILayout.Label(state, EditorStyles.boldLabel);
            GUI.color = prev;
            GUILayout.FlexibleSpace();
            GUILayout.Label("v" + ProfilotEventCapture.WriterVersion, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// What the tripwire is actually doing right now, never more than that: it used to say
        /// "monitoring" during the warm-up, before any threshold existed, and whenever Play was on.
        /// `detail` is the longer explanation for the empty-run state.
        /// </summary>
        private static string MonitorState(out Color color, out string detail)
        {
            if (!EditorApplication.isPlaying)
            {
                color = Dim;
                detail = "Enter Play Mode to start watching.";
                return "○ not playing";
            }

            ProfilotTripwire tw = FindTripwire();
            if (tw == null || !tw.isActiveAndEnabled)
            {
                color = Orange;
                detail = "The Profilot tripwire is not in this Play session, so nothing is being checked. " +
                         "Exit and re-enter Play Mode; if it stays like this, reimport the Profilot package.";
                return "○ not running";
            }

            if (tw.WarmingUp || !tw.FrameBaselineReady)
            {
                color = Dim;
                detail = $"Warming up (frame {tw.WarmupFramesDone}/{tw.WarmupFrames}) - the first frames after " +
                         "Play are always slow, so nothing is checked until the baseline forms.";
                return $"○ warming up {tw.WarmupFramesDone}/{tw.WarmupFrames}";
            }

            color = Green;
            string mode = ProfilotEventCapture.DeepCapture ? string.Empty : ", counters only";
            detail = "Nothing caught yet in this run.";
            return $"● watching - baseline {tw.FrameBaselineMs:0.0} ms{mode}";
        }

        private void DrawCaptureSection()
        {
            HeaderBar("Capture", Blue);
            bool deep = ProfilotEventCapture.DeepCapture;
            bool now = EditorGUILayout.ToggleLeft(
                new GUIContent("Deep capture  (map problems to code)",
                    "ON (default): keeps the Unity Profiler recording so a caught problem names the " +
                    "method the profiler blames, which Claude finds in your code. Measured to add " +
                    "negligible per-frame cost.\n\n" +
                    "OFF: counter-only events (type, size, frequency) with no marker tree - maximum " +
                    "Editor speed, but you lose code mapping."),
                deep);
            if (now != deep)
                ProfilotEventCapture.DeepCapture = now;
            EditorGUILayout.LabelField(
                now ? "Problems name the method the profiler blames." : "Counters only - no code mapping.",
                EditorStyles.miniLabel);
        }

        // ---- settings -----------------------------------------------------------------------

        private void DrawSettingsSection()
        {
            bool open = FoldoutBar("Profilot.UI.Settings", "Settings", Purple, false);
            if (!open)
                return;

            EditorGUI.indentLevel++;

            // Widen the label column so the parameter names (with their units) are not clipped.
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 210f;

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
                DrawLiveBaseline();
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
                EditorGUILayout.Space(2);
                if (GUILayout.Button(new GUIContent("Delete all runs...", "Deletes every captured run (asks first)."), GUILayout.Height(20)))
                {
                    if (EditorUtility.DisplayDialog("Profilot", "Delete ALL captured runs?", "Delete", "Cancel"))
                    {
                        ProfilotEventStore.ClearAll();
                        _selectedRun = null;
                        Repaint();
                    }
                }
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

            EditorGUIUtility.labelWidth = prevLabelWidth;

            EditorGUILayout.Space(2);
            if (IconButtonWide("Refresh", "Reset all settings to defaults (asks first)", "Reset to defaults..."))
            {
                if (EditorUtility.DisplayDialog("Profilot", "Reset all Profilot settings to their defaults?", "Reset", "Cancel"))
                {
                    _config = new ProfilotConfig();
                    ProfilotConfigStore.Save(_config);
                }
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
                    EditorGUILayout.LabelField("Clean run - nothing tripped.", EditorStyles.miniLabel);
            }
            else
            {
                // Fixed and ignored problems go into a collapsed group, so a mute is never
                // invisible and never crowds out what still needs a look.
                var active = new List<EventSummary>();
                var muted = new List<EventSummary>();
                foreach (EventSummary e in events)
                {
                    string review = ProfilotEventCapture.ReviewStatusOf(e.eventId);
                    (review == "fixed" || review == "ignored" ? muted : active).Add(e);
                }

                // What needs a look first: a fix that came back worse, then the most severe.
                active.Sort((a, b) =>
                {
                    int ra = ProfilotEventCapture.ReviewStatusOf(a.eventId) == "regressed" ? 1 : 0;
                    int rb = ProfilotEventCapture.ReviewStatusOf(b.eventId) == "regressed" ? 1 : 0;
                    if (ra != rb) return rb - ra;
                    int sa = SeverityRank(a.trigger?.severity), sb = SeverityRank(b.trigger?.severity);
                    if (sa != sb) return sb - sa;
                    return string.CompareOrdinal(b.capturedAt, a.capturedAt);
                });

                Color prev = GUI.color;
                GUI.color = active.Count > 0 ? Orange : Green;
                EditorGUILayout.LabelField(active.Count > 0
                        ? $"⚠  {active.Count} issue(s) in this run"
                        : "No open issues in this run", EditorStyles.miniBoldLabel);
                GUI.color = prev;

                foreach (EventSummary e in active)
                    DrawEventCard(e, _selectedRun);
                if (muted.Count > 0 && FoldoutBar("Profilot.UI.Muted", $"Muted ({muted.Count})", Dim, false))
                {
                    foreach (EventSummary e in muted)
                        DrawEventCard(e, _selectedRun);
                }
            }
        }

        private void DrawRunPicker(List<string> runs)
        {
            var labels = new string[runs.Count];
            string current = ProfilotEventCapture.CurrentSessionId;
            // Counting means listing every run folder, so it is refreshed every few seconds,
            // not on every repaint.
            if (EditorApplication.timeSinceStartup - _runCountsAt > 3)
            {
                _runCounts.Clear();
                _runCountsAt = EditorApplication.timeSinceStartup;
            }
            for (int i = 0; i < runs.Count; i++)
            {
                if (!_runCounts.TryGetValue(runs[i], out int n))
                    _runCounts[runs[i]] = n = ProfilotEventStore.EventFiles(runs[i]).Count;
                string count = n == 0 ? "clean" : $"{n} event{(n == 1 ? "" : "s")}";
                labels[i] = $"{ProfilotText.RunLabel(runs[i])}   ·   {count}" + (runs[i] == current ? "   (current)" : string.Empty);
            }

            int idx = Mathf.Max(0, runs.IndexOf(_selectedRun));
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(new GUIContent("Run", "Each Play session is a run, named by its start time. Pick one to view its results."), GUILayout.Width(30));
            int newIdx = EditorGUILayout.Popup(idx, labels);
            if (newIdx != idx)
                _selectedRun = runs[newIdx];

            if (IconButton("Folder Icon", "Open the events folder in the file browser", "Open"))
                OpenFolder();
            if (IconButton("TreeEditor.Trash", "Delete this run's results", "Delete"))
            {
                if (EditorUtility.DisplayDialog("Profilot", $"Delete run {ProfilotText.RunLabel(_selectedRun)}?", "Delete", "Cancel"))
                {
                    ProfilotEventStore.ClearRun(_selectedRun);
                    _selectedRun = null;
                    Repaint();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawEventCard(EventSummary e, string run)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            Trigger t = e.trigger ?? new Trigger();
            int count = e.dedup != null ? e.dedup.count : 1;

            Color prev = GUI.color;
            GUI.color = SeverityColor(t.severity);
            EditorGUILayout.LabelField(new GUIContent(
                    $"{ProfilotText.TypeLabel(t.type)}   ·   {t.severity}   ·   seen {count} time{(count == 1 ? "" : "s")}",
                    $"{t.type}, severity {t.severity}, caught {count} time(s) in this run."),
                EditorStyles.boldLabel);
            GUI.color = prev;

            // Schema 1 records have no frameMatchesTrigger, so its absence is not "unmatched".
            bool unmatched = e.status == "ok" && !e.frameMatchesTrigger && e.schemaVersion != null && e.schemaVersion != "1";
            if (e.topMarkers != null && e.topMarkers.Length > 0 && unmatched)
                EditorGUILayout.LabelField(new GUIContent($"near {ProfilotText.CleanMarker(e.topMarkers[0].name)} - frame not matched",
                    "No profiler frame matched this trip in time, so the tree (and this name) may belong to a " +
                    $"neighbouring frame.\n{e.topMarkers[0].name}"), EditorStyles.miniLabel);
            else if (e.topMarkers != null && e.topMarkers.Length > 0)
                EditorGUILayout.LabelField(new GUIContent($"in {ProfilotText.CleanMarker(e.topMarkers[0].name)}",
                    $"The marker the profiler blames:\n{e.topMarkers[0].name}"), EditorStyles.miniBoldLabel);
            else if (e.status == "counters_only")
                EditorGUILayout.LabelField("no marker - deep capture is off; turn it on to map this to code", EditorStyles.miniLabel);
            else
                EditorGUILayout.LabelField("no marker - the profiler frame was not available", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(ProfilotText.Describe(t.type, t.metric, t.value, t.budget), EditorStyles.miniLabel);

            // The same event id exists in every run that hit the problem, and the CLI resolves a
            // bare --id to the newest run containing it. Always pin the run the card belongs to,
            // or picking an older run here would diagnose a different record.
            string command = $"profilot diagnose --id {e.eventId} --run {run}";
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Copy prompt for Claude",
                    "Copies a ready prompt that tells Claude Code to run the diagnose command for this event " +
                    "and run, find the code, and propose a fix."), GUILayout.Height(22)))
            {
                EditorGUIUtility.systemCopyBuffer =
                    $"Diagnose Profilot event {e.eventId} (run {run}): run `{command}`, find the code, and propose a fix.";
                ShowNotification(new GUIContent("Copied - paste into Claude Code"));
            }
            if (GUILayout.Button(new GUIContent("Command", $"Copies just the command:\n{command}"),
                    GUILayout.Height(22), GUILayout.Width(80)))
            {
                EditorGUIUtility.systemCopyBuffer = command;
                ShowNotification(new GUIContent("Command copied"));
            }
            EditorGUILayout.EndHorizontal();

            string review = ProfilotEventCapture.ReviewStatusOf(e.eventId);
            bool marked = review != "open";

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Fixed",
                    "Mark it after you applied a fix and re-ran Play. Mutes it; if it comes back " +
                    $"{ProfilotEventCapture.RegressionMultiple:0}x worse than its peak, it alerts again, marked regressed."),
                    GUILayout.Height(20)))
                Mark(e.eventId, "fixed");
            if (GUILayout.Button(new GUIContent("Ignore", "Mutes it for good, as noise. Reopen to bring it back."), GUILayout.Height(20)))
                Mark(e.eventId, "ignored");
            using (new EditorGUI.DisabledScope(!marked))
            {
                if (GUILayout.Button(new GUIContent("Reopen", "Return to open and resume notifications."), GUILayout.Height(20), GUILayout.Width(70)))
                    Mark(e.eventId, "open");
            }
            EditorGUILayout.EndHorizontal();

            switch (review)
            {
                case "fixed":
                    double at = ProfilotEventCapture.FixedValueOf(e.eventId);
                    EditorGUILayout.LabelField(at > 0
                            ? $"fixed at {ProfilotText.FormatValue(t.metric, at)} - muted, alerts again at " +
                              ProfilotText.FormatValue(t.metric, at * ProfilotEventCapture.RegressionMultiple)
                            : "fixed - muted, alerts again if it comes back much worse",
                        EditorStyles.miniLabel);
                    if (ProfilotEventCapture.SeenAgainAfterFixed(e.eventId, out int againCount, out string againRun, out double againValue))
                    {
                        prev = GUI.color;
                        GUI.color = Orange;
                        EditorGUILayout.LabelField(
                            $"caught again after Fixed: {againCount} time{(againCount == 1 ? "" : "s")}, last in run {ProfilotText.RunLabel(againRun)} " +
                            $"(up to {ProfilotText.FormatValue(t.metric, againValue)})",
                            EditorStyles.miniBoldLabel);
                        GUI.color = prev;
                    }
                    DrawHelped(e.eventId);
                    break;
                case "ignored":
                    EditorGUILayout.LabelField("ignored - muted for good (Reopen to resume)", EditorStyles.miniLabel);
                    break;
                case "regressed":
                    prev = GUI.color;
                    GUI.color = SeverityColor("high");
                    EditorGUILayout.LabelField("REGRESSED - marked fixed, came back worse", EditorStyles.miniBoldLabel);
                    GUI.color = prev;
                    DrawHelped(e.eventId);
                    break;
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        private void DrawQuiet()
        {
            EditorGUILayout.Space(6);
            string state = MonitorState(out Color color, out string detail);
            Color prev = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField(state, EditorStyles.miniBoldLabel);
            GUI.color = prev;
            EditorGUILayout.LabelField(detail, EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawOnboarding()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "No runs yet. Enter Play Mode and Profilot watches for performance spikes on its own. " +
                "Then ask Claude Code to \"diagnose the last Profilot event\".",
                MessageType.Info);
            DrawChecklist(ProfilotHealth.Get());
            EditorGUILayout.Space(4);
            if (GUILayout.Button("Enter Play Mode", GUILayout.Height(26)))
                EditorApplication.isPlaying = true;
        }

        // ---- setup / health -----------------------------------------------------------------

        // Only when something is wrong, and above the issues, so drift is seen where people look.
        private void DrawSetupWarning()
        {
            ProfilotHealth.Report h = ProfilotHealth.Get();
            if (h.Healthy || h.Checking || ProfilotEventStore.ListRuns().Count == 0)
                return;
            string what = !h.CliFound ? "The profilot CLI was not found."
                : h.VersionsDrift ? $"CLI {h.CliVersion} and package {h.PackageVersion} differ."
                : h.GuideState != "current" ? $"The guide for Claude Code is {h.GuideState}."
                : "CLAUDE.md does not import the Profilot guide.";
            EditorGUILayout.HelpBox(what + " See Setup at the bottom of this window.", MessageType.Warning);
        }

        private void DrawSetupSection()
        {
            ProfilotHealth.Report h = ProfilotHealth.Get();
            if (!FoldoutBar("Profilot.UI.Setup", h.Healthy ? "Setup" : "Setup  (needs attention)", h.Healthy ? Dim : Orange, !h.Healthy))
                return;
            EditorGUI.indentLevel++;
            DrawChecklist(h);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Re-check", "Look for the CLI and the guide again."), GUILayout.Height(20)))
                ProfilotHealth.Get(true);
            EditorGUILayout.EndHorizontal();
            EditorGUI.indentLevel--;
        }

        private void DrawChecklist(ProfilotHealth.Report h)
        {
            if (h.Checking)
            {
                EditorGUILayout.LabelField("Checking the CLI and the guide...", EditorStyles.miniLabel);
                return;
            }
            Line(true, $"Unity package {h.PackageVersion}");
            Line(h.CliFound, h.CliFound ? $"profilot CLI {h.CliVersion}" : "profilot CLI not found on the PATH");
            if (h.VersionsDrift)
                Line(false, $"CLI {h.CliVersion} and package {h.PackageVersion} differ - update the older one");
            Line(h.GuideState == "current", h.GuideState == "current" ? "Guide for Claude Code in .claude/"
                : h.GuideState == "missing" ? "No guide in .claude/ - run profilot init"
                : h.GuideState == "newer" ? "The guide in .claude/ is newer than the CLI - update the CLI (npm install -g profilot)"
                : "The guide in .claude/ is outdated - run profilot init");
            Line(h.ClaudeMdImports, h.ClaudeMdImports ? "CLAUDE.md imports the guide" : "CLAUDE.md does not import the guide - run profilot init");

            if (!h.Healthy)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.SelectableLabel(ProfilotHealth.SetupCommand, EditorStyles.textField, GUILayout.Height(18));
                if (GUILayout.Button(new GUIContent("Copy", "Run it in a terminal, in this project's folder."), GUILayout.Width(52), GUILayout.Height(18)))
                {
                    EditorGUIUtility.systemCopyBuffer = ProfilotHealth.SetupCommand;
                    ShowNotification(new GUIContent("Setup command copied"));
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private static void Line(bool ok, string text)
        {
            Color prev = GUI.color;
            GUI.color = ok ? Green : Orange;
            EditorGUILayout.LabelField((ok ? "✓  " : "✗  ") + text, EditorStyles.miniLabel);
            GUI.color = prev;
        }

        private void Mark(string eventId, string status)
        {
            ProfilotEventCapture.MarkReviewed(eventId, status);
            Repaint();
        }

        // One click on whether the diagnosis helped (SPEC.md M2), asked once a fix is marked.
        private void DrawHelped(string eventId)
        {
            string helped = ProfilotEventCapture.HelpedOf(eventId);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(helped == "" ? "Did the diagnosis help?" : $"Diagnosis helped: {helped}", EditorStyles.miniLabel);
            if (helped == "")
            {
                if (GUILayout.Button("Yes", EditorStyles.miniButtonLeft, GUILayout.Width(44)))
                    ProfilotEventCapture.MarkHelped(eventId, true);
                if (GUILayout.Button("No", EditorStyles.miniButtonRight, GUILayout.Width(44)))
                    ProfilotEventCapture.MarkHelped(eventId, false);
            }
            EditorGUILayout.EndHorizontal();
        }

        private static int SeverityRank(string severity)
        {
            switch (severity)
            {
                case "high": return 3;
                case "medium": return 2;
                case "low": return 1;
                default: return 0;
            }
        }

        private static void OpenFolder()
        {
            string dir = ProfilotEventStore.RunsRoot;
            Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

        private static ProfilotTripwire FindTripwire()
        {
            // The tripwire GameObject is HideFlags.HideAndDontSave, so FindObjectsOfTypeAll
            // (which includes hidden objects) is the way to reach it.
            var all = Resources.FindObjectsOfTypeAll<ProfilotTripwire>();
            return all.Length > 0 ? all[0] : null;
        }

        // Detailed baseline readout with the derived trip thresholds, shown under Thresholds.
        private void DrawLiveBaseline()
        {
            ProfilotTripwire tw = EditorApplication.isPlaying ? FindTripwire() : null;
            if (tw != null && tw.FrameBaselineReady)
            {
                double hitch = Math.Max(tw.FrameBaselineMs * _config.frameHitchMultiplier, _config.frameHitchFloorMs);
                string dc = tw.DrawCallsBaselineReady
                    ? $"\ndraw calls baseline {tw.DrawCallsBaseline:0}  ->  trips at {tw.DrawCallsBaseline * _config.drawCallsBaselineMultiplier:0}"
                    : string.Empty;
                EditorGUILayout.HelpBox(
                    $"Live baseline (this run): frame {tw.FrameBaselineMs:0.0} ms  ->  hitch at {hitch:0.0} ms{dc}",
                    MessageType.None);
            }
            else
            {
                EditorGUILayout.LabelField("Baseline forms live during Play (relative to this project).", EditorStyles.miniLabel);
            }
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

        private List<EventSummary> LoadEvents(string runId)
        {
            string stamp = RunStamp(runId);
            if (_cachedEvents != null && _cachedRun == runId && _cachedStamp == stamp)
                return _cachedEvents;

            _cachedRun = runId;
            _cachedStamp = stamp;
            _cachedEvents = ReadEvents(runId);
            return _cachedEvents;
        }

        /// <summary>
        /// Cheap fingerprint of a run's event files: how many there are and the newest write time.
        /// A trip either adds a file or rewrites one (dedup overwrites), so both cases move the
        /// stamp - without opening or parsing anything.
        /// </summary>
        private static string RunStamp(string runId)
        {
            List<string> files = ProfilotEventStore.EventFiles(runId);
            long newest = 0;
            foreach (string path in files)
            {
                try
                {
                    long t = File.GetLastWriteTimeUtc(path).Ticks;
                    if (t > newest) newest = t;
                }
                catch { /* a file deleted between the listing and the stat just does not count */ }
            }
            return files.Count + ":" + newest;
        }

        private static List<EventSummary> ReadEvents(string runId)
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
