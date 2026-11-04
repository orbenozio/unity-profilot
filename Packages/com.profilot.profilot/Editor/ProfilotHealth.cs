using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Profilot.Editor
{
    /// <summary>
    /// Is this project wired up end to end: the package, the `profilot` CLI on the PATH, the
    /// guide copy under .claude/ and its CLAUDE.md import. The window shows it so version drift
    /// is visible where people look, not only in `profilot status`. Checked on demand and cached;
    /// the check runs off the main thread, because finding the CLI starts a login shell.
    /// </summary>
    internal static class ProfilotHealth
    {
        internal const string SetupCommand = "npm install -g profilot && profilot init";
        private const string GuideFile = "profilot-diagnosis-guide.md";
        private static readonly Regex GuideHeader = new Regex(@"^<!--\s*profilot-guide\s+([0-9][0-9.]*)\s*-->");
        private static readonly Regex VersionField = new Regex("\"version\"\\s*:\\s*\"([^\"]+)\"");

        internal sealed class Report
        {
            public string PackageVersion;
            public string CliVersion;      // null = not found on the PATH
            public string GuideState;      // current | outdated | missing
            public string GuideVersion;
            public bool ClaudeMdImports;
            public bool Checking;

            public bool CliFound => CliVersion != null;
            public bool VersionsDrift => CliFound && PackageVersion != "unknown" && CliVersion != PackageVersion;
            public bool Healthy => !Checking && CliFound && !VersionsDrift && GuideState == "current" && ClaudeMdImports;
        }

        private static Report _cached;
        private static Task<Report> _pending;

        /// <summary>The last finished check; while one is running, a report with Checking set.</summary>
        internal static Report Get(bool refresh = false)
        {
            if (_pending != null && _pending.IsCompleted)
            {
                _cached = _pending.Status == TaskStatus.RanToCompletion ? _pending.Result : null;
                _pending = null;
            }
            if ((_cached == null || refresh) && _pending == null)
            {
                // Unity APIs stay on the main thread; files and the shell go to a worker.
                string version = ProfilotEventCapture.WriterVersion;
                string root = Directory.GetCurrentDirectory();
                _pending = Task.Run(() => Check(version, root));
            }
            return _cached ?? new Report { PackageVersion = ProfilotEventCapture.WriterVersion, GuideState = "missing", Checking = true };
        }

        private static Report Check(string packageVersion, string root)
        {
            var r = new Report { PackageVersion = packageVersion };

            string guide = Path.Combine(root, ".claude", GuideFile);
            if (!File.Exists(guide))
            {
                r.GuideState = "missing";
            }
            else
            {
                string first = string.Empty;
                try { using (var sr = new StreamReader(guide)) first = sr.ReadLine() ?? string.Empty; }
                catch { /* unreadable reads as outdated */ }
                Match m = GuideHeader.Match(first);
                r.GuideVersion = m.Success ? m.Groups[1].Value : null;
                r.GuideState = r.GuideVersion != null && r.GuideVersion == r.PackageVersion ? "current" : "outdated";
            }

            string claudeMd = Path.Combine(root, "CLAUDE.md");
            try { r.ClaudeMdImports = File.Exists(claudeMd) && File.ReadAllText(claudeMd).Contains("@.claude/" + GuideFile); }
            catch { r.ClaudeMdImports = false; }

            r.CliVersion = FindCliVersion();
            return r;
        }

        // Unity started from the Hub does not inherit the terminal's PATH (no /opt/homebrew/bin on
        // a Mac), so ask a login shell, the way the user's terminal would.
        private static string FindCliVersion()
        {
            try
            {
                var psi = Path.DirectorySeparatorChar == '\\'
                    ? new ProcessStartInfo("cmd.exe", "/c profilot --version")
                    : new ProcessStartInfo(File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/bash", "-lc \"profilot --version\"");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null)
                        return null;
                    // Read asynchronously: a shell that hangs must not freeze the Editor.
                    var output = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(4000))
                    {
                        try { p.Kill(); } catch { /* already gone */ }
                        return null;
                    }
                    if (!output.Wait(1000))
                        return null;
                    Match m = VersionField.Match(output.Result);
                    return m.Success ? m.Groups[1].Value : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
