using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace TokRun
{
    internal sealed class WindowsTerminalCandidate
    {
        public string Path;
        public string Source;
    }

    internal static class WindowsTerminalUtils
    {
        public static bool IsModernConsoleBuild(out string product, out int build)
        {
            product = "Windows";
            build = 0;

            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        object p = key.GetValue("ProductName");
                        if (p != null && !String.IsNullOrWhiteSpace(p.ToString()))
                            product = p.ToString();

                        object raw = key.GetValue("CurrentBuildNumber") ?? key.GetValue("CurrentBuild");
                        if (raw != null)
                            Int32.TryParse(raw.ToString(), out build);
                    }
                }
            }
            catch { }

            if (build <= 0)
            {
                try { build = Environment.OSVersion.Version.Build; }
                catch { build = 0; }
            }

            // Windows 11 starts at 22000. Windows Server 2025 is 26100.
            return build >= 22000;
        }

        public static List<WindowsTerminalCandidate> GetCandidates()
        {
            var list = new List<WindowsTerminalCandidate>();

            // Normal per-user App Execution Alias, equivalent to `where wt.exe`.
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!String.IsNullOrWhiteSpace(local))
                {
                    string alias = Path.Combine(local, "Microsoft", "WindowsApps", "wt.exe");
                    AddIfUsable(list, alias, "%LOCALAPPDATA%\\Microsoft\\WindowsApps\\wt.exe");
                }
            }
            catch { }

            // PATH / SearchPath lookup.
            try
            {
                var buffer = new StringBuilder(32768);
                IntPtr filePart;
                uint len = NativeMethods.SearchPath(null, "wt.exe", null, buffer.Capacity, buffer, out filePart);
                if (len > 0 && len < buffer.Capacity)
                    AddIfUsable(list, buffer.ToString(), "SearchPath/PATH");
            }
            catch { }

            AddAppPathCandidate(list, Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\App Paths\wt.exe",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\wt.exe");

            AddAppPathCandidate(list, Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\wt.exe",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\wt.exe");

            // Physical packaged executable fallback. This can be unreadable on
            // some hosts; failure is expected and ignored.
            try
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string apps = Path.Combine(programFiles, "WindowsApps");
                if (Directory.Exists(apps))
                {
                    string[] dirs = Directory.GetDirectories(apps, "Microsoft.WindowsTerminal_*__8wekyb3d8bbwe");
                    Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                    Array.Reverse(dirs);
                    for (int i = 0; i < dirs.Length; i++)
                    {
                        string candidate = Path.Combine(dirs[i], "wt.exe");
                        AddIfUsable(list, candidate, "Program Files\\WindowsApps package");
                    }
                }
            }
            catch { }

            return list;
        }

        // Backward-compatible helpers used by earlier ProcessLauncher revisions.
        public static string FindWindowsTerminalPath()
        {
            List<WindowsTerminalCandidate> candidates = GetCandidates();
            if (candidates == null || candidates.Count == 0)
                return null;
            return candidates[0].Path;
        }

        public static string BuildTerminalCommandLine(string wtPath, string childCommandLine, string workDir, string title)
        {
            var sb = new StringBuilder();
            sb.Append(QuoteArgument(wtPath));
            sb.Append(" -w -1 new-tab");

            if (!String.IsNullOrWhiteSpace(workDir))
            {
                sb.Append(" -d ");
                sb.Append(QuoteArgument(workDir));
            }

            if (!String.IsNullOrWhiteSpace(title))
            {
                sb.Append(" --title ");
                sb.Append(QuoteArgument(title));
            }

            sb.Append(' ');
            sb.Append(childCommandLine);
            return sb.ToString();
        }

        private static void AddAppPathCandidate(List<WindowsTerminalCandidate> list, RegistryKey root, string keyPath, string source)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(keyPath))
                {
                    if (key == null)
                        return;
                    object raw = key.GetValue(null);
                    if (raw == null)
                        return;
                    string candidate = Environment.ExpandEnvironmentVariables(raw.ToString().Trim().Trim('"'));
                    AddIfUsable(list, candidate, source);
                }
            }
            catch { }
        }

        private static string QuoteArgument(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "\"\"";

            var sb = new StringBuilder();
            sb.Append('"');
            int slashCount = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\')
                {
                    slashCount++;
                    continue;
                }

                if (c == '"')
                {
                    sb.Append('\\', slashCount * 2 + 1);
                    sb.Append('"');
                    slashCount = 0;
                    continue;
                }

                if (slashCount > 0)
                {
                    sb.Append('\\', slashCount);
                    slashCount = 0;
                }
                sb.Append(c);
            }

            if (slashCount > 0)
                sb.Append('\\', slashCount * 2);
            sb.Append('"');
            return sb.ToString();
        }

        private static void AddIfUsable(List<WindowsTerminalCandidate> list, string path, string source)
        {
            if (String.IsNullOrWhiteSpace(path))
                return;

            try { path = Path.GetFullPath(path); }
            catch { }

            if (!File.Exists(path))
                return;

            for (int i = 0; i < list.Count; i++)
            {
                if (String.Equals(list[i].Path, path, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            list.Add(new WindowsTerminalCandidate { Path = path, Source = source });
        }
    }
}
