// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 Chokri Hammedi
using System;
using System.Text;

namespace TokRun
{
    internal sealed class Options
    {
        public bool Help;
        public bool List;
        public bool Cmd;
        public bool Hidden;
        public bool Verbose;
        public int TargetSessionId = -1;
        public string User;
        public int Pid = -1;
        public int SessionId = -1;
        public string RunPath;
        public string RunArgs;
        public string WorkDir;

        public static Options Parse(string[] args)
        {
            var o = new Options();

            if (args == null || args.Length == 0)
            {
                o.Help = true;
                return o;
            }

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a.ToLowerInvariant())
                {
                    case "-h":
                    case "--help":
                    case "/?":
                        o.Help = true;
                        break;
                    case "--list":
                        o.List = true;
                        break;
                    case "--cmd":
                        o.Cmd = true;
                        break;
                    case "--hidden":
                        o.Hidden = true;
                        break;
                    case "--verbose":
                        o.Verbose = true;
                        break;
                    case "--target-session":
                        o.TargetSessionId = Int32.Parse(NeedValue(args, ref i, a));
                        break;
                    case "--user":
                        o.User = NeedValue(args, ref i, a);
                        break;
                    case "--pid":
                        o.Pid = Int32.Parse(NeedValue(args, ref i, a));
                        break;
                    case "--session":
                        o.SessionId = Int32.Parse(NeedValue(args, ref i, a));
                        break;
                    case "--run":
                        o.RunPath = NeedValue(args, ref i, a);
                        break;
                    case "--args":
                        o.RunArgs = NeedValue(args, ref i, a);
                        break;
                    case "--workdir":
                        o.WorkDir = NeedValue(args, ref i, a);
                        break;
                    default:
                        throw new ArgumentException("Unknown option: " + a);
                }
            }

            if (o.Cmd && !String.IsNullOrWhiteSpace(o.RunPath))
                throw new ArgumentException("Use either --cmd or --run, not both.");

            return o;
        }

        private static string NeedValue(string[] args, ref int index, string opt)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException(opt + " requires a value");
            return args[++index];
        }

        public static string GetDefaultCmdPath()
        {
            return Quote(GetDefaultCmdPathUnquoted());
        }

        public static string GetDefaultCmdPathUnquoted()
        {
            string windir = Environment.GetEnvironmentVariable("WINDIR");
            if (String.IsNullOrWhiteSpace(windir)) windir = @"C:\Windows";
            return windir.TrimEnd('\\') + @"\System32\cmd.exe";
        }

        public static string BuildRunCommandLine(string path, string arguments)
        {
            var sb = new StringBuilder();
            sb.Append(Quote(path));
            if (!String.IsNullOrWhiteSpace(arguments))
            {
                sb.Append(' ');
                sb.Append(arguments);
            }
            return sb.ToString();
        }

        public static string Quote(string s)
        {
            if (String.IsNullOrEmpty(s)) return s;
            if (s.StartsWith("\"") && s.EndsWith("\"")) return s;
            return "\"" + s + "\"";
        }

        public static void PrintHelp()
        {
            Console.WriteLine(@"TokRun v1.0 - token-based process launcher for authorized Windows administration/lab use

Usage:
  TokRun.exe --list
  TokRun.exe --user DOMAIN\user --cmd
  TokRun.exe --user user --cmd
  TokRun.exe --user DOMAIN\user --run C:\Path\program.exe
  TokRun.exe --pid 1234 --run C:\Path\program.exe --args ""arg1 arg2""
  TokRun.exe --pid 1234 --cmd
  TokRun.exe --pid 1234 --user SYSTEM --cmd   (validates PID belongs to SYSTEM)
  TokRun.exe --user DOMAIN\user --target-session 2 --cmd
  TokRun.exe --user SYSTEM --hidden --run C:\Path\agent.exe

Options:
  --list                 List accessible process tokens
  --user <name>          Match DOMAIN\user, .\user, or user suffix
  --pid <pid>            Use the token from a specific process. If --user is also supplied, the PID token must match that user
  --session <id>         Filter source tokens by session ID
  --cmd                  Launch a visible cmd.exe; fallback selection is automatic
  --run <path>           Launch a local target executable; console programs get a new console automatically
  --args <args>          Arguments passed to the target executable
  --workdir <path>       Working directory for the child process
  --target-session <id>  Explicit interactive session for --cmd
  --hidden               Launch without a visible window
  --verbose              Show all token candidates, fallbacks, and Win32 errors

Default output is clean. Use --verbose to show the complete automatic fallback trace.

Automatic interactive-launch fallback order:
  1. Rank all matching tokens and prefer interactive-session tokens.
  2. On modern Windows builds, detect Windows Terminal (wt.exe) and use it
     as the visible host for cmd.exe/PowerShell.
  3. Try the WindowsApps alias first, then PATH, then the HKCU App Paths entry.
  4. If Windows Terminal is unavailable or fails, use the native console broker.
  5. Fall back to direct CreateProcessWithTokenW / CreateProcessAsUserW paths.
  6. If permitted, move a duplicated token to the requested session and retry.
  7. Try linked-token and remaining matching-token candidates.

Notes:
  Run elevated. The target user must already have a token on this host.
  --run accepts local executable paths only; URL and UNC/share paths are rejected.
  --interactive and --active-session were removed in v0.6 because --cmd now handles
  interactive-session selection automatically.");
        }
    }
}
