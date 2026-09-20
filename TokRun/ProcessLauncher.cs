using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace TokRun
{
    internal sealed class LaunchResult
    {
        public bool Success;
        public int ChildPid = -1;
        public int ChildSessionId = -1;
        public string Method;
        public string ErrorSummary;
    }

    internal static class ProcessLauncher
    {
        public static LaunchResult LaunchWithFallbacks(
            TokenInfo candidate,
            string commandLine,
            string workDir,
            bool interactive,
            bool hidden,
            bool newConsole,
            int preferredSession,
            string expectedClientName,
            bool commandShell)
        {
            var result = new LaunchResult();
            IntPtr primaryToken = IntPtr.Zero;

            try
            {
                Logger.Verbose("[*] Duplicating candidate token as a primary token...");
                if (!NativeMethods.DuplicateTokenEx(
                    candidate.Token,
                    NativeMethods.MAXIMUM_ALLOWED,
                    IntPtr.Zero,
                    NativeMethods.SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    NativeMethods.TOKEN_TYPE.TokenPrimary,
                    out primaryToken))
                {
                    result.ErrorSummary = FormatLastError("DuplicateTokenEx");
                    Logger.Verbose("[-] " + result.ErrorSummary);
                    return result;
                }

                int tokenSession = TokenUtils.GetTokenSessionId(primaryToken);
                int callerSession = GetCurrentSessionId();
                Logger.Verbose("[*] Caller session    : {0}", callerSession);
                Logger.Verbose("[*] Token session     : {0}", tokenSession);
                if (interactive)
                    Logger.Verbose("[*] Interactive target: session {0}", preferredSession);

                // Windows Server 2025 and modern 26100+ builds render normal shells
                // through Windows Terminal. Launching cmd.exe directly with a duplicated
                // token may create the old Windows Command Processor surface, which can
                // show as a blank window. Prefer wt.exe when available, then fall back
                // to the native console broker and older Win32 process-creation paths.
                if (interactive && !hidden && newConsole && NeedsModernTerminalHost())
                {
                    string product;
                    int build;
                    GetWindowsProduct(out product, out build);
                    Logger.Verbose("[*] Modern terminal build detected: {0} (build {1}).", product, build);

                    if (IsSystemUser(candidate.UserName))
                    {
                        Logger.Verbose("[*] SYSTEM token detected; trying native visible console broker first...");
                        if (TryLaunchNativeConsoleBroker(
                            primaryToken,
                            commandLine,
                            workDir,
                            candidate.UserName,
                            expectedClientName,
                            result))
                            return result;

                        Logger.Verbose("[*] Native console broker failed; trying Windows Terminal (wt.exe) fallback...");
                        if (TryLaunchWindowsTerminalHost(
                            primaryToken,
                            commandLine,
                            workDir,
                            candidate.UserName,
                            result))
                            return result;
                    }
                    else
                    {
                        Logger.Verbose("[*] Trying Windows Terminal (wt.exe) host first...");
                        if (TryLaunchWindowsTerminalHost(
                            primaryToken,
                            commandLine,
                            workDir,
                            candidate.UserName,
                            result))
                            return result;

                        Logger.Verbose("[*] Windows Terminal path failed/unavailable; trying native console broker fallback...");
                        if (TryLaunchNativeConsoleBroker(
                            primaryToken,
                            commandLine,
                            workDir,
                            candidate.UserName,
                            expectedClientName,
                            result))
                            return result;
                    }
                }

                uint creationFlags = NativeMethods.CREATE_UNICODE_ENVIRONMENT;
                if (hidden)
                    creationFlags |= NativeMethods.CREATE_NO_WINDOW;
                else if (newConsole)
                    creationFlags |= NativeMethods.CREATE_NEW_CONSOLE;

                // Normal path used by Windows 7/10 and older Server releases, and as
                // a fallback if the modern-console broker cannot start.
                if (!interactive || callerSession == preferredSession)
                {
                    string visibleDesktop = interactive ? @"winsta0\default" : null;
                    if (interactive)
                    {
                        if (TryCreateProcessWithToken(primaryToken, commandLine, workDir, creationFlags,
                            NativeMethods.LOGON_WITH_PROFILE, visibleDesktop, false,
                            "CreateProcessWithTokenW(profile/winsta0/default desktop/default env)", result))
                            return result;

                        if (TryCreateProcessWithToken(primaryToken, commandLine, workDir, creationFlags,
                            NativeMethods.LOGON_WITH_PROFILE, visibleDesktop, true,
                            "CreateProcessWithTokenW(profile/winsta0/default desktop/token env)", result))
                            return result;
                    }

                    if (TryCreateProcessWithToken(primaryToken, commandLine, workDir, creationFlags,
                        NativeMethods.LOGON_WITH_PROFILE, null, false,
                        "CreateProcessWithTokenW(profile/inherited desktop/default env)", result))
                        return result;

                    if (TryCreateProcessWithToken(primaryToken, commandLine, workDir, creationFlags,
                        NativeMethods.LOGON_WITH_PROFILE, null, true,
                        "CreateProcessWithTokenW(profile/inherited desktop/token env)", result))
                        return result;

                    if (TryCreateProcessWithToken(primaryToken, commandLine, workDir, creationFlags,
                        0, null, false,
                        "CreateProcessWithTokenW(no-profile/inherited desktop)", result))
                        return result;
                }
                else
                {
                    Logger.Verbose("[*] Skipping caller-session CreateProcessWithTokenW because caller session {0} != requested session {1}.", callerSession, preferredSession);
                }

                tokenSession = TokenUtils.GetTokenSessionId(primaryToken);
                if (!interactive || tokenSession == preferredSession)
                {
                    string visibleDesktop = interactive ? @"winsta0\default" : null;
                    if (interactive &&
                        TryCreateProcessAsUser(primaryToken, commandLine, workDir, creationFlags,
                        visibleDesktop, "CreateProcessAsUserW(existing session/winsta0/default desktop)", result))
                        return result;

                    if (TryCreateProcessAsUser(primaryToken, commandLine, workDir, creationFlags,
                        null, "CreateProcessAsUserW(existing session/inherited desktop)", result))
                        return result;
                }
                else
                {
                    Logger.Verbose("[*] Token is not already in requested interactive session; direct CreateProcessAsUserW would remain in session {0}.", tokenSession);
                }

                if (interactive && preferredSession >= 0 && tokenSession != preferredSession)
                {
                    Logger.Verbose("[*] Trying TokenSessionId reassignment {0} -> {1}...", tokenSession, preferredSession);
                    int sessionError;
                    if (TrySetTokenSession(primaryToken, preferredSession, out sessionError))
                    {
                        Logger.Verbose("[+] Token moved to session {0}.", preferredSession);
                        if (TryCreateProcessAsUser(primaryToken, commandLine, workDir, creationFlags,
                            @"winsta0\default", "SetTokenInformation + CreateProcessAsUserW(winsta0/default desktop)", result))
                            return result;

                        if (TryCreateProcessAsUser(primaryToken, commandLine, workDir, creationFlags,
                            null, "SetTokenInformation + CreateProcessAsUserW(inherited desktop)", result))
                            return result;
                    }
                    else
                    {
                        Logger.Verbose("[-] SetTokenInformation(TokenSessionId): {0} ({1})",
                            sessionError, new Win32Exception(sessionError).Message);
                    }
                }

                if (!interactive)
                {
                    if (TryCreateProcessAsUser(primaryToken, commandLine, workDir, creationFlags,
                        null, "CreateProcessAsUserW(original session)", result))
                        return result;
                }

                result.ErrorSummary = "All compatible process creation methods failed for this token candidate.";
                return result;
            }
            finally
            {
                if (primaryToken != IntPtr.Zero)
                    NativeMethods.CloseHandle(primaryToken);
            }
        }

        private static bool NeedsModernTerminalHost()
        {
            string product;
            int build;
            return WindowsTerminalUtils.IsModernConsoleBuild(out product, out build);
        }

        private static bool IsSystemUser(string userName)
        {
            return TokenUtils.MatchesUser(userName, "SYSTEM");
        }

        private static void GetWindowsProduct(out string product, out int build)
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
        }

        private static bool TryLaunchWindowsTerminalHost(
            IntPtr token,
            string childCommandLine,
            string workDir,
            string expectedUser,
            LaunchResult result)
        {
            var terminals = WindowsTerminalUtils.GetCandidates();
            if (terminals == null || terminals.Count == 0)
            {
                Logger.Verbose("[-] Windows Terminal host: wt.exe was not found.");
                Logger.Verbose("[-] Checked the WindowsApps alias, PATH, and HKCU App Paths.");
                return false;
            }

            string title = "TokRun - " + (expectedUser ?? "token");

            for (int i = 0; i < terminals.Count; i++)
            {
                string wtPath = terminals[i].Path;
                string terminalLine = BuildWindowsTerminalCommandLine(
                    wtPath, childCommandLine, workDir, title);
                string terminalWorkDir = System.IO.Path.GetDirectoryName(wtPath);
                if (String.IsNullOrWhiteSpace(terminalWorkDir))
                    terminalWorkDir = workDir;

                Logger.Verbose("[+] Windows Terminal : {0}", wtPath);
                Logger.Verbose("[+] Detected via     : {0}", terminals[i].Source);
                Logger.Verbose("[+] Terminal command : {0}", terminalLine);

                IntPtr environment = IntPtr.Zero;
                var pi = new NativeMethods.PROCESS_INFORMATION();
                var si = NewStartupInfo(null);

                try
                {
                    TryCreateEnvironment(token, out environment);
                    var cmd = new StringBuilder(terminalLine, terminalLine.Length + 64);
                    uint flags = NativeMethods.CREATE_UNICODE_ENVIRONMENT;
                    string method = "CreateProcessWithTokenW(Windows Terminal: " + terminals[i].Source + ")";

                    Logger.Verbose("[*] Trying {0}...", method);
                    if (!NativeMethods.CreateProcessWithTokenW(
                        token,
                        NativeMethods.LOGON_WITH_PROFILE,
                        null,
                        cmd,
                        flags,
                        environment,
                        terminalWorkDir,
                        ref si,
                        out pi))
                    {
                        PrintLastError(method);
                        continue;
                    }

                    result.Success = true;
                    result.ChildPid = pi.dwProcessId;
                    result.ChildSessionId = GetProcessSessionId(pi.dwProcessId);
                    result.Method = method;
                    Logger.Verbose("[+] Windows Terminal launch request succeeded.");
                    Logger.Verbose("[+] Host PID          : {0}", pi.dwProcessId);
                    Logger.Verbose("[+] Host session      : {0}", result.ChildSessionId);
                    Logger.Verbose("[+] Requested shell   : {0}", childCommandLine);
                    return true;
                }
                finally
                {
                    CloseProcessInfo(ref pi);
                    if (environment != IntPtr.Zero)
                        NativeMethods.DestroyEnvironmentBlock(environment);
                }
            }

            return false;
        }

        private static string BuildWindowsTerminalCommandLine(
            string wtPath,
            string childCommandLine,
            string workDir,
            string title)
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

        private static bool TryLaunchNativeConsoleBroker(
            IntPtr token,
            string commandLine,
            string workDir,
            string expectedUser,
            string expectedClientName,
            LaunchResult result)
        {
            string exePath;
            try
            {
                exePath = Process.GetCurrentProcess().MainModule.FileName;
            }
            catch
            {
                exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            }

            if (String.IsNullOrWhiteSpace(exePath) || !System.IO.File.Exists(exePath))
            {
                Logger.Verbose("[-] Native console broker: could not resolve TokRun executable path.");
                return false;
            }

            string brokerLine = QuoteArgument(exePath) +
                " --_native-console " + EncodeInternalArg(commandLine) +
                " " + EncodeInternalArg(workDir) +
                " " + EncodeInternalArg(expectedUser);

            Logger.Verbose("[*] Starting TokRun native console broker under {0}...", expectedUser);
            IntPtr environment = IntPtr.Zero;
            var pi = new NativeMethods.PROCESS_INFORMATION();
            var si = NewStartupInfo(@"winsta0\default");

            try
            {
                TryCreateEnvironment(token, out environment);
                var cmd = new StringBuilder(brokerLine, brokerLine.Length + 64);
                uint flags = NativeMethods.CREATE_UNICODE_ENVIRONMENT | NativeMethods.CREATE_NEW_CONSOLE;

                if (!NativeMethods.CreateProcessWithTokenW(
                    token,
                    NativeMethods.LOGON_WITH_PROFILE,
                    null,
                    cmd,
                    flags,
                    environment,
                    System.IO.Path.GetDirectoryName(exePath),
                    ref si,
                    out pi))
                {
                    PrintLastError("CreateProcessWithTokenW(native console broker)");
                    return false;
                }

                Logger.Verbose("[+] Broker PID        : {0}", pi.dwProcessId);
                Logger.Verbose("[+] Broker session    : {0}", GetProcessSessionId(pi.dwProcessId));

                int clientPid = WaitForDescendantByName(pi.dwProcessId, expectedClientName, 3500);
                if (clientPid <= 0)
                {
                    uint wait = NativeMethods.WaitForSingleObject(pi.hProcess, 0);
                    if (wait == NativeMethods.WAIT_OBJECT_0)
                    {
                        uint exitCode;
                        if (!NativeMethods.GetExitCodeProcess(pi.hProcess, out exitCode))
                            exitCode = 0xFFFFFFFF;
                        Logger.Verbose("[-] Native console broker exited during startup (exit code {0}).", exitCode);
                    }
                    else
                    {
                        Logger.Verbose("[-] Native console broker started, but client '{0}' was not observed.", expectedClientName ?? "<unknown>");
                    }
                    try { NativeMethods.TerminateProcess(pi.hProcess, 1); } catch { }
                    return false;
                }

                result.Success = true;
                result.ChildPid = clientPid;
                result.ChildSessionId = GetProcessSessionId(clientPid);
                result.Method = "CreateProcessWithTokenW -> AllocConsole broker -> CreateProcessW";
                Logger.Verbose("[+] Real console client started successfully.");
                Logger.Verbose("[+] Client PID        : {0}", clientPid);
                Logger.Verbose("[+] Client session    : {0}", result.ChildSessionId);
                return true;
            }
            finally
            {
                CloseProcessInfo(ref pi);
                if (environment != IntPtr.Zero)
                    NativeMethods.DestroyEnvironmentBlock(environment);
            }
        }

        private static int WaitForDescendantByName(int rootPid, string expectedClientName, int timeoutMs)
        {
            string expected = NormalizeExeName(expectedClientName);
            int elapsed = 0;
            const int interval = 100;

            while (elapsed <= timeoutMs)
            {
                int pid = FindDescendantByName(rootPid, expected);
                if (pid > 0)
                    return pid;

                System.Threading.Thread.Sleep(interval);
                elapsed += interval;
            }
            return -1;
        }

        private static int FindDescendantByName(int rootPid, string expectedExeName)
        {
            IntPtr snap = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1))
                return -1;

            try
            {
                var all = new System.Collections.Generic.List<NativeMethods.PROCESSENTRY32>();
                var pe = new NativeMethods.PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(NativeMethods.PROCESSENTRY32));
                if (!NativeMethods.Process32FirstW(snap, ref pe))
                    return -1;

                do
                {
                    all.Add(pe);
                    pe = new NativeMethods.PROCESSENTRY32();
                    pe.dwSize = (uint)Marshal.SizeOf(typeof(NativeMethods.PROCESSENTRY32));
                }
                while (NativeMethods.Process32NextW(snap, ref pe));

                var frontier = new System.Collections.Generic.Queue<int>();
                var seen = new System.Collections.Generic.HashSet<int>();
                frontier.Enqueue(rootPid);
                seen.Add(rootPid);

                while (frontier.Count > 0)
                {
                    int parent = frontier.Dequeue();
                    for (int i = 0; i < all.Count; i++)
                    {
                        var e = all[i];
                        if ((int)e.th32ParentProcessID != parent)
                            continue;

                        int childPid = (int)e.th32ProcessID;
                        if (seen.Contains(childPid))
                            continue;
                        seen.Add(childPid);

                        string name = NormalizeExeName(e.szExeFile);
                        if (String.IsNullOrWhiteSpace(expectedExeName) ||
                            String.Equals(name, expectedExeName, StringComparison.OrdinalIgnoreCase))
                            return childPid;

                        frontier.Enqueue(childPid);
                    }
                }

                return -1;
            }
            finally
            {
                NativeMethods.CloseHandle(snap);
            }
        }

        private static string NormalizeExeName(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return null;
            try { return System.IO.Path.GetFileName(value.Trim().Trim('"')); }
            catch { return value.Trim().Trim('"'); }
        }

        private static string EncodeInternalArg(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "-";
            return Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
        }

        private static string QuoteArgument(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "\"\"";
            if (value.StartsWith("\"") && value.EndsWith("\""))
                return value;
            return "\"" + value + "\"";
        }

        private static bool TryCreateProcessWithToken(
            IntPtr token,
            string commandLine,
            string workDir,
            uint flags,
            uint logonFlags,
            string desktop,
            bool createTokenEnvironment,
            string methodName,
            LaunchResult result)
        {
            Logger.Verbose("[*] Trying {0}...", methodName);
            IntPtr environment = IntPtr.Zero;
            var pi = new NativeMethods.PROCESS_INFORMATION();
            var si = NewStartupInfo(desktop);

            try
            {
                if (createTokenEnvironment)
                    TryCreateEnvironment(token, out environment);

                var cmd = new StringBuilder(commandLine, commandLine.Length + 32);
                if (!NativeMethods.CreateProcessWithTokenW(
                    token, logonFlags, null, cmd, flags, environment, workDir, ref si, out pi))
                {
                    PrintLastError(methodName);
                    return false;
                }

                SetSuccess(result, pi.dwProcessId, methodName);
                return true;
            }
            finally
            {
                CloseProcessInfo(ref pi);
                if (environment != IntPtr.Zero)
                    NativeMethods.DestroyEnvironmentBlock(environment);
            }
        }

        private static bool TryCreateProcessAsUser(
            IntPtr token,
            string commandLine,
            string workDir,
            uint flags,
            string desktop,
            string methodName,
            LaunchResult result)
        {
            Logger.Verbose("[*] Trying {0}...", methodName);
            IntPtr environment = IntPtr.Zero;
            var pi = new NativeMethods.PROCESS_INFORMATION();
            var si = NewStartupInfo(desktop);

            try
            {
                TryCreateEnvironment(token, out environment);
                var cmd = new StringBuilder(commandLine, commandLine.Length + 32);
                if (!NativeMethods.CreateProcessAsUserW(
                    token, null, cmd, IntPtr.Zero, IntPtr.Zero, false, flags,
                    environment, workDir, ref si, out pi))
                {
                    PrintLastError(methodName);
                    return false;
                }

                SetSuccess(result, pi.dwProcessId, methodName);
                return true;
            }
            finally
            {
                CloseProcessInfo(ref pi);
                if (environment != IntPtr.Zero)
                    NativeMethods.DestroyEnvironmentBlock(environment);
            }
        }

        private static NativeMethods.STARTUPINFO NewStartupInfo(string desktop)
        {
            var si = new NativeMethods.STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(NativeMethods.STARTUPINFO));
            si.lpDesktop = desktop;
            if (!String.IsNullOrEmpty(desktop))
            {
                si.dwFlags |= NativeMethods.STARTF_USESHOWWINDOW;
                si.wShowWindow = NativeMethods.SW_SHOWNORMAL;
            }
            return si;
        }

        private static void TryCreateEnvironment(IntPtr token, out IntPtr environment)
        {
            environment = IntPtr.Zero;
            if (!NativeMethods.CreateEnvironmentBlock(out environment, token, false))
                environment = IntPtr.Zero;
        }

        private static void SetSuccess(LaunchResult result, int pid, string method)
        {
            result.Success = true;
            result.ChildPid = pid;
            result.Method = method;
            result.ChildSessionId = GetProcessSessionId(pid);
            Logger.Verbose("[+] {0} succeeded.", method);
            Logger.Verbose("[+] Child PID         : {0}", pid);
            Logger.Verbose("[+] Child session     : {0}", result.ChildSessionId);
        }

        private static bool TrySetTokenSession(IntPtr token, int sessionId, out int error)
        {
            error = 0;
            PrivilegeUtils.TryEnablePrivilege("SeTcbPrivilege");

            IntPtr pSession = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(pSession, sessionId);
                if (NativeMethods.SetTokenInformation(
                    token,
                    NativeMethods.TOKEN_INFORMATION_CLASS.TokenSessionId,
                    pSession,
                    4))
                    return true;

                error = Marshal.GetLastWin32Error();
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(pSession);
            }
        }

        private static int GetCurrentSessionId()
        {
            try { return Process.GetCurrentProcess().SessionId; }
            catch { return -1; }
        }

        private static int GetProcessSessionId(int pid)
        {
            int session;
            if (NativeMethods.ProcessIdToSessionId(pid, out session))
                return session;
            return -1;
        }

        private static void CloseProcessInfo(ref NativeMethods.PROCESS_INFORMATION pi)
        {
            if (pi.hProcess != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(pi.hProcess);
                pi.hProcess = IntPtr.Zero;
            }
            if (pi.hThread != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(pi.hThread);
                pi.hThread = IntPtr.Zero;
            }
        }

        private static void PrintLastError(string api)
        {
            int err = Marshal.GetLastWin32Error();
            Logger.Verbose("[-] {0}: {1} ({2})", api, err, new Win32Exception(err).Message);
        }

        private static string FormatLastError(string api)
        {
            int err = Marshal.GetLastWin32Error();
            return String.Format("{0}: {1} ({2})", api, err, new Win32Exception(err).Message);
        }
    }
}
