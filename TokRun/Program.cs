// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 Chokri Hammedi
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace TokRun
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Internal native-console broker mode for Windows Server 2025 / build 26100+.
            // The parent launches this process under the selected token in the interactive
            // session. This broker allocates a standard console and starts the real client.
            if (args != null && args.Length >= 4 && String.Equals(args[0], "--_native-console", StringComparison.Ordinal))
            {
                try
                {
                    string brokerCommand = DecodeInternalArg(args[1]);
                    string brokerWorkDir = DecodeInternalArg(args[2]);
                    string brokerUser = DecodeInternalArg(args[3]);
                    return NativeConsoleBroker.Run(brokerCommand, brokerWorkDir, brokerUser);
                }
                catch
                {
                    return 41;
                }
            }

            Options opt;
            try
            {
                opt = Options.Parse(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[-] Argument error: " + ex.Message);
                Options.PrintHelp();
                return 1;
            }

            if (opt.Help)
            {
                Options.PrintHelp();
                return 0;
            }

            Logger.VerboseEnabled = opt.Verbose;

            PrivilegeUtils.TryEnableCommonPrivileges();

            if (opt.List)
            {
                TokenUtils.ListTokens();
                return 0;
            }

            if (opt.Pid <= 0 && String.IsNullOrWhiteSpace(opt.User))
            {
                Console.Error.WriteLine("[-] Select a token with --user <DOMAIN\\user> or --pid <pid>. Use --list first.");
                return 1;
            }

            if (!opt.Cmd && String.IsNullOrWhiteSpace(opt.RunPath))
            {
                Console.Error.WriteLine("[-] Select what to launch with --cmd or --run <exe path>.");
                return 1;
            }

            string commandLine;
            string workDir = opt.WorkDir;
            string resolvedTarget = null;
            string expectedClientName = null;

            if (opt.Cmd)
            {
                string cmdPath = Options.GetDefaultCmdPathUnquoted();
                // /D disables AutoRun commands and /K keeps the shell alive.  COLOR 07
                // prevents an inherited/target console palette from making the prompt
                // effectively black-on-black.
                commandLine = Options.Quote(cmdPath) + " /D /K \"color 07\"";
                expectedClientName = Path.GetFileName(cmdPath);
                if (String.IsNullOrWhiteSpace(workDir))
                    workDir = Path.GetDirectoryName(cmdPath);
            }
            else
            {
                if (ExecutableUtils.IsRemoteOrUrlPath(opt.RunPath))
                {
                    Console.Error.WriteLine("[-] Remote or URL executable paths are not supported.");
                    Console.Error.WriteLine("[-] Copy the executable to a local disk and run it from a local path.");
                    return 1;
                }

                resolvedTarget = ExecutableUtils.ResolveExecutablePath(opt.RunPath);
                string launchPath = !String.IsNullOrWhiteSpace(resolvedTarget) ? resolvedTarget : opt.RunPath;
                string launchArgs = opt.RunArgs;

                // Interactive shells should remain open when launched without explicit args.
                string fileName = Path.GetFileName(launchPath);
                expectedClientName = fileName;
                if (!opt.Hidden && String.IsNullOrWhiteSpace(launchArgs))
                {
                    if (String.Equals(fileName, "powershell.exe", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(fileName, "pwsh.exe", StringComparison.OrdinalIgnoreCase))
                        launchArgs = "-NoExit";
                    else if (String.Equals(fileName, "cmd.exe", StringComparison.OrdinalIgnoreCase))
                        launchArgs = "/D /K \"color 07\"";
                }

                commandLine = Options.BuildRunCommandLine(launchPath, launchArgs);

                // v0.7 used Path.GetFullPath("powershell.exe"), which incorrectly made the
                // caller's TokRun folder the working directory for relative executable names.
                if (String.IsNullOrWhiteSpace(workDir) && !String.IsNullOrWhiteSpace(resolvedTarget))
                    workDir = Path.GetDirectoryName(resolvedTarget);
            }

            bool interactive = !opt.Hidden;
            bool newConsole = opt.Cmd;
            if (!opt.Cmd && !opt.Hidden)
            {
                if (String.IsNullOrWhiteSpace(resolvedTarget))
                    resolvedTarget = ExecutableUtils.ResolveExecutablePath(opt.RunPath);
                if (!String.IsNullOrWhiteSpace(resolvedTarget))
                    newConsole = ExecutableUtils.IsConsoleApplication(resolvedTarget);
            }

            int callerSession = GetCurrentSessionId();
            int activeSession = NativeMethods.WTSGetActiveConsoleSessionId();
            int preferredSession = opt.TargetSessionId >= 0
                ? opt.TargetSessionId
                : (callerSession > 0 ? callerSession : activeSession);

            Logger.Verbose("[+] Launch target     : {0}", commandLine);
            if (!String.IsNullOrWhiteSpace(workDir))
                Logger.Verbose("[+] Working directory : {0}", workDir);
            if (interactive)
            {
                Logger.Verbose(opt.Cmd ? "[+] Visible --cmd requested." : "[+] Interactive target requested.");
                Logger.Verbose("[+] Caller session    : {0}", callerSession);
                Logger.Verbose("[+] Active session    : {0}", activeSession);
                Logger.Verbose("[+] Preferred session : {0}", preferredSession);
            }

            List<TokenInfo> candidates = new List<TokenInfo>();
            try
            {
                bool pidRequested = opt.Pid > 0;
                bool userConstraint = !String.IsNullOrWhiteSpace(opt.User);

                if (pidRequested)
                {
                    TokenInfo direct = TokenUtils.OpenTokenFromPid(opt.Pid, NativeMethods.TOKEN_LAUNCH_ACCESS);
                    if (direct == null || direct.Token == IntPtr.Zero)
                    {
                        Console.Error.WriteLine("[-] PID {0} was not accessible or did not expose a usable token.", opt.Pid);
                        Console.Error.WriteLine("[-] Some protected/system processes deny token access even from an elevated shell.");
                        Console.Error.WriteLine("[-] Use --list and choose an accessible PID, or use --user <name> to let TokRun choose automatically.");
                        return 2;
                    }

                    // Production-safe semantics:
                    //   --pid <pid> uses that PID token.
                    //   --pid <pid> --user <name> validates that the PID token belongs to <name>.
                    // It must never silently continue with a different identity.
                    if (userConstraint && !TokenUtils.MatchesUser(direct.UserName, opt.User))
                    {
                        Console.Error.WriteLine("[-] PID {0} belongs to {1}, not requested user {2}.", opt.Pid, direct.UserName, opt.User);
                        Console.Error.WriteLine("[-] Refusing to launch with a mismatched token.");
                        TokenUtils.CloseToken(direct);
                        return 2;
                    }

                    candidates.Add(direct);
                    TokenInfo linked = TokenUtils.TryOpenLinkedToken(direct);
                    if (linked != null)
                        candidates.Add(linked);
                }
                else
                {
                    candidates = TokenUtils.FindTokensByUser(opt.User, opt.SessionId, NativeMethods.TOKEN_LAUNCH_ACCESS, preferredSession);
                }

                if (candidates == null || candidates.Count == 0)
                {
                    Console.Error.WriteLine("[-] Matching token was not found or was not accessible.");
                    Console.Error.WriteLine("[-] The target user must already have a token on this host.");
                    return 2;
                }

                Logger.Verbose("[+] Found {0} candidate token(s).", candidates.Count);

                for (int i = 0; i < candidates.Count; i++)
                {
                    TokenInfo selected = candidates[i];
                    Logger.Verbose();
                    Logger.Verbose("[*] Candidate #{0}/{1}", i + 1, candidates.Count);
                    Logger.Verbose("[+] Token user        : {0}", selected.UserName);
                    Logger.Verbose("[+] Source process    : PID {0} / Session {1} / {2}", selected.Pid, selected.SessionId, selected.ProcessName);
                    Logger.Verbose("[+] Token type        : {0}{1}", selected.TokenType, selected.IsLinkedToken ? " (linked token)" : "");

                    LaunchResult result = ProcessLauncher.LaunchWithFallbacks(
                        selected,
                        commandLine,
                        workDir,
                        interactive,
                        opt.Hidden,
                        newConsole,
                        preferredSession,
                        expectedClientName,
                        opt.Cmd);

                    if (result.Success)
                    {
                        if (Logger.VerboseEnabled)
                        {
                            Logger.Verbose();
                            Logger.Verbose("[+] Process launch succeeded.");
                            Logger.Verbose("[+] Method            : {0}", result.Method);
                            Logger.Verbose("[+] Started PID       : {0}", result.ChildPid);
                            Logger.Verbose("[+] Started session   : {0}", result.ChildSessionId);

                            if (interactive && result.ChildSessionId != preferredSession)
                            {
                                Logger.Verbose("[!] The process started, but not in the preferred interactive session.");
                                Logger.Verbose("[!] Continuing to other candidates would create duplicate shells, so TokRun stops here.");
                            }
                        }
                        else
                        {
                            Logger.Info("[+] Selected token : {0}", selected.UserName);
                            Logger.Info("[+] Launch target  : {0}", opt.Cmd ? "cmd.exe" : commandLine);
                            Logger.Info("[+] Started PID    : {0}", result.ChildPid);
                            Logger.Info("[+] Session        : {0}", result.ChildSessionId);
                        }

                        return 0;
                    }

                    Logger.Verbose("[-] Candidate #{0} exhausted: {1}", i + 1, result.ErrorSummary ?? "launch failed");
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine("[-] All token candidates and compatible launch methods failed.");
                if (interactive)
                {
                    Console.Error.WriteLine("[-] No visible interactive process could be created for the selected identity.");
                    Console.Error.WriteLine("[-] Re-run with --verbose to see each fallback and Win32 error.");
                }
                return 3;
            }
            finally
            {
                if (candidates != null)
                {
                    for (int i = 0; i < candidates.Count; i++)
                        TokenUtils.CloseToken(candidates[i]);
                }
            }
        }

        private static string DecodeInternalArg(string value)
        {
            if (String.IsNullOrEmpty(value) || value == "-")
                return null;
            byte[] raw = Convert.FromBase64String(value);
            return System.Text.Encoding.Unicode.GetString(raw);
        }

        private static int GetCurrentSessionId()
        {
            try { return Process.GetCurrentProcess().SessionId; }
            catch { return -1; }
        }
    }
}
