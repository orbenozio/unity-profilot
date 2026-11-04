using System;
using System.Globalization;

namespace Profilot.Editor
{
    /// <summary>Plain-language formatting shared by the window and the notifications.</summary>
    internal static class ProfilotText
    {
        // The raw dominant marker is assembly-qualified and decorated
        // (e.g. "NeonRunner.Runtime.dll!NeonRunner.UI::GameHud.OnGUI() [Invoke]"). Trim to the
        // readable "Class.Method" tail.
        internal static string CleanMarker(string marker)
        {
            if (string.IsNullOrEmpty(marker))
                return "(no marker)";

            string s = marker.Replace("() [Invoke]", string.Empty).Replace("[Invoke]", string.Empty).Replace("()", string.Empty);
            int colons = s.LastIndexOf("::", StringComparison.Ordinal);
            if (colons >= 0) s = s.Substring(colons + 2);
            int bang = s.LastIndexOf('!');
            if (bang >= 0) s = s.Substring(bang + 1);
            return s.Trim();
        }

        // A trigger value in the unit of its metric: "36.2 ms", "1.2 MB", "312 draw calls".
        internal static string FormatValue(string metric, double v)
        {
            switch (metric)
            {
                case "frameTimeMs": return v.ToString("0.#", CultureInfo.InvariantCulture) + " ms";
                case "gcAllocBytes":
                    if (v >= 1024 * 1024) return (v / (1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
                    if (v >= 1024) return (v / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
                    return v.ToString("0", CultureInfo.InvariantCulture) + " B";
                case "drawCalls": return v.ToString("0", CultureInfo.InvariantCulture) + " draw calls";
                default: return v.ToString("0.##", CultureInfo.InvariantCulture);
            }
        }

        // What happened, in a sentence: "Frame took 48 ms (trips above 18 ms)".
        internal static string Describe(string type, string metric, double value, double budget)
        {
            switch (type)
            {
                case "frame_hitch": return $"Frame took {FormatValue(metric, value)} (trips above {FormatValue(metric, budget)})";
                case "gc_spike": return $"Allocated {FormatValue(metric, value)} in one frame";
                case "draw_calls": return $"{FormatValue(metric, value)} in one frame (trips above {budget:0})";
                default: return $"{metric} {FormatValue(metric, value)} (budget {FormatValue(metric, budget)})";
            }
        }

        internal static string TypeLabel(string type)
        {
            switch (type)
            {
                case "frame_hitch": return "Frame hitch";
                case "gc_spike": return "GC allocation";
                case "draw_calls": return "Draw-call spike";
                default: return type ?? "?";
            }
        }

        // "2026-11-04_14-32-05" -> "Nov 4, 14:32:05"; anything else as is.
        internal static string RunLabel(string run)
        {
            if (DateTime.TryParseExact(run, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t))
                return t.ToString("MMM d, HH:mm:ss", CultureInfo.InvariantCulture);
            return run ?? "?";
        }
    }
}
