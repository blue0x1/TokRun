using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace TokRun
{
    internal static class TokenUtils
    {
        public static void ListTokens()
        {
            Console.WriteLine("PID\tSess\tType\tImpersonation\tUser\t\t\tProcess");
            foreach (TokenInfo t in EnumerateTokens(NativeMethods.TOKEN_LIST_ACCESS))
            {
                try
                {
                    Console.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}",
                        t.Pid,
                        t.SessionId,
                        t.TokenType,
                        t.ImpersonationLevel,
                        t.UserName,
                        t.ProcessName);
                }
                finally
                {
                    CloseToken(t);
                }
            }
        }

        public static List<TokenInfo> FindTokensByUser(string wantedUser, int sessionFilter, uint desiredAccess, int preferredSession)
        {
            var matches = new List<TokenInfo>();
            var seenPids = new HashSet<int>();

            foreach (TokenInfo t in EnumerateTokens(NativeMethods.TOKEN_LIST_ACCESS))
            {
                bool userMatch = MatchesUser(t.UserName, wantedUser);
                bool sessionMatch = sessionFilter < 0 || t.SessionId == sessionFilter;
                int pid = t.Pid;
                CloseToken(t);

                if (!userMatch || !sessionMatch || seenPids.Contains(pid))
                    continue;

                seenPids.Add(pid);
                TokenInfo reopened = OpenTokenFromPidBestEffort(pid, desiredAccess, false);
                if (reopened != null && reopened.Token != IntPtr.Zero)
                {
                    matches.Add(reopened);

                    TokenInfo linked = TryOpenLinkedToken(reopened);
                    if (linked != null)
                        matches.Add(linked);
                }
            }

            matches.Sort(delegate(TokenInfo a, TokenInfo b)
            {
                return b.Score(preferredSession).CompareTo(a.Score(preferredSession));
            });

            return matches;
        }

        public static TokenInfo OpenTokenFromPid(int pid, uint desiredAccess)
        {
            return OpenTokenFromPidBestEffort(pid, desiredAccess, true);
        }

        public static TokenInfo TryOpenLinkedToken(TokenInfo source)
        {
            if (source == null || source.Token == IntPtr.Zero)
                return null;

            int size = IntPtr.Size;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int returned;
                if (!NativeMethods.GetTokenInformation(
                    source.Token,
                    NativeMethods.TOKEN_INFORMATION_CLASS.TokenLinkedToken,
                    buffer,
                    size,
                    out returned))
                    return null;

                IntPtr linkedHandle = Marshal.ReadIntPtr(buffer);
                if (linkedHandle == IntPtr.Zero)
                    return null;

                return new TokenInfo
                {
                    Token = linkedHandle,
                    UserName = GetTokenUser(linkedHandle),
                    Pid = source.Pid,
                    ProcessName = source.ProcessName + " [linked]",
                    SessionId = GetTokenSessionId(linkedHandle),
                    TokenType = GetTokenType(linkedHandle),
                    ImpersonationLevel = GetTokenImpersonationLevel(linkedHandle),
                    IsLinkedToken = true
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static TokenInfo OpenTokenFromPidBestEffort(int pid, uint desiredAccess, bool printErrors)
        {
            uint[] attempts = new uint[]
            {
                desiredAccess,
                NativeMethods.TOKEN_QUERY | NativeMethods.TOKEN_DUPLICATE | NativeMethods.TOKEN_ASSIGN_PRIMARY | NativeMethods.TOKEN_IMPERSONATE | NativeMethods.TOKEN_ADJUST_DEFAULT | NativeMethods.TOKEN_ADJUST_SESSIONID,
                NativeMethods.TOKEN_QUERY | NativeMethods.TOKEN_DUPLICATE | NativeMethods.TOKEN_ASSIGN_PRIMARY | NativeMethods.TOKEN_IMPERSONATE | NativeMethods.TOKEN_ADJUST_DEFAULT,
                NativeMethods.TOKEN_QUERY | NativeMethods.TOKEN_DUPLICATE | NativeMethods.TOKEN_ASSIGN_PRIMARY | NativeMethods.TOKEN_IMPERSONATE,
                NativeMethods.TOKEN_QUERY | NativeMethods.TOKEN_DUPLICATE | NativeMethods.TOKEN_ASSIGN_PRIMARY,
                NativeMethods.TOKEN_QUERY | NativeMethods.TOKEN_DUPLICATE
            };

            for (int i = 0; i < attempts.Length; i++)
            {
                TokenInfo info = TryOpenTokenFromPid(pid, attempts[i], false);
                if (info != null && info.Token != IntPtr.Zero)
                    return info;
            }

            if (printErrors)
                PrintLastError("OpenProcessToken", pid);

            return null;
        }

        private static TokenInfo TryOpenTokenFromPid(int pid, uint desiredAccess, bool printErrors)
        {
            IntPtr process = IntPtr.Zero;
            IntPtr token = IntPtr.Zero;

            try
            {
                process = OpenProcessForQuery(pid);
                if (process == IntPtr.Zero)
                {
                    if (printErrors)
                        PrintLastError("OpenProcess", pid);
                    return null;
                }

                if (!NativeMethods.OpenProcessToken(process, desiredAccess, out token))
                {
                    if (printErrors)
                        PrintLastError("OpenProcessToken", pid);
                    return null;
                }

                Process p = null;
                try { p = Process.GetProcessById(pid); } catch { }

                TokenInfo info = BuildTokenInfo(token, pid, SafeProcessName(p));
                token = IntPtr.Zero;
                return info;
            }
            finally
            {
                if (process != IntPtr.Zero)
                    NativeMethods.CloseHandle(process);
                if (token != IntPtr.Zero)
                    NativeMethods.CloseHandle(token);
            }
        }

        private static IEnumerable<TokenInfo> EnumerateTokens(uint desiredAccess)
        {
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { yield break; }

            foreach (Process p in processes)
            {
                IntPtr process = IntPtr.Zero;
                IntPtr token = IntPtr.Zero;

                try
                {
                    process = OpenProcessForQuery(p.Id);
                    if (process == IntPtr.Zero)
                        continue;

                    if (!NativeMethods.OpenProcessToken(process, desiredAccess, out token))
                        continue;

                    TokenInfo info = BuildTokenInfo(token, p.Id, SafeProcessName(p));
                    token = IntPtr.Zero;
                    yield return info;
                }
                finally
                {
                    if (process != IntPtr.Zero)
                        NativeMethods.CloseHandle(process);
                    if (token != IntPtr.Zero)
                        NativeMethods.CloseHandle(token);
                }
            }
        }

        private static IntPtr OpenProcessForQuery(int pid)
        {
            IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
                h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION, false, pid);
            return h;
        }

        private static TokenInfo BuildTokenInfo(IntPtr token, int pid, string processName)
        {
            return new TokenInfo
            {
                Token = token,
                UserName = GetTokenUser(token),
                Pid = pid,
                ProcessName = processName,
                SessionId = GetTokenSessionId(token),
                TokenType = GetTokenType(token),
                ImpersonationLevel = GetTokenImpersonationLevel(token),
                IsLinkedToken = false
            };
        }

        public static bool MatchesUser(string actual, string wanted)
        {
            if (String.IsNullOrWhiteSpace(actual) || String.IsNullOrWhiteSpace(wanted))
                return false;

            string a = NormalizeUser(actual);
            string w = NormalizeUser(wanted);

            if (a.Equals(w, StringComparison.OrdinalIgnoreCase))
                return true;

            string actualUserOnly = UserOnly(a);
            string wantedUserOnly = UserOnly(w);

            if (actualUserOnly.Equals(w, StringComparison.OrdinalIgnoreCase))
                return true;

            if (actualUserOnly.Equals(wantedUserOnly, StringComparison.OrdinalIgnoreCase))
                return true;

            if (actualUserOnly.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) &&
                (w.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                 w.Equals("AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                 w.Equals("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase)))
                return true;

            return false;
        }

        private static string NormalizeUser(string name)
        {
            string n = name.Trim().Trim('"').Replace('/', '\\');
            while (n.Contains("\\\\"))
                n = n.Replace("\\\\", "\\");
            if (n.StartsWith(".\\"))
                n = n.Substring(2);
            return n;
        }

        private static string UserOnly(string name)
        {
            int idx = name.LastIndexOf('\\');
            if (idx >= 0 && idx + 1 < name.Length)
                return name.Substring(idx + 1);
            return name;
        }

        public static string GetTokenUser(IntPtr token)
        {
            try
            {
                using (var id = new WindowsIdentity(token))
                    return id.Name;
            }
            catch
            {
                return "<unknown>";
            }
        }

        public static int GetTokenSessionId(IntPtr token)
        {
            int value;
            if (TryReadTokenInt(token, NativeMethods.TOKEN_INFORMATION_CLASS.TokenSessionId, out value))
                return value;
            return -1;
        }

        private static string GetTokenType(IntPtr token)
        {
            int value;
            if (!TryReadTokenInt(token, NativeMethods.TOKEN_INFORMATION_CLASS.TokenType, out value))
                return "Unknown";

            if (value == (int)NativeMethods.TOKEN_TYPE.TokenPrimary)
                return "Primary";
            if (value == (int)NativeMethods.TOKEN_TYPE.TokenImpersonation)
                return "Impersonation";
            return value.ToString();
        }

        private static string GetTokenImpersonationLevel(IntPtr token)
        {
            int tokenType;
            if (TryReadTokenInt(token, NativeMethods.TOKEN_INFORMATION_CLASS.TokenType, out tokenType) && tokenType == (int)NativeMethods.TOKEN_TYPE.TokenPrimary)
                return "N/A";

            int value;
            if (!TryReadTokenInt(token, NativeMethods.TOKEN_INFORMATION_CLASS.TokenImpersonationLevel, out value))
                return "N/A";

            if (Enum.IsDefined(typeof(NativeMethods.SECURITY_IMPERSONATION_LEVEL), value))
                return ((NativeMethods.SECURITY_IMPERSONATION_LEVEL)value).ToString();
            return value.ToString();
        }

        private static bool TryReadTokenInt(IntPtr token, NativeMethods.TOKEN_INFORMATION_CLASS infoClass, out int value)
        {
            value = -1;
            IntPtr buffer = Marshal.AllocHGlobal(4);
            try
            {
                int len;
                if (!NativeMethods.GetTokenInformation(token, infoClass, buffer, 4, out len))
                    return false;
                value = Marshal.ReadInt32(buffer);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string SafeProcessName(Process p)
        {
            if (p == null) return "<unknown>";
            try { return p.ProcessName; }
            catch { return "<unknown>"; }
        }

        public static void CloseToken(TokenInfo t)
        {
            if (t != null && t.Token != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(t.Token);
                t.Token = IntPtr.Zero;
            }
        }

        private static void PrintLastError(string api, int pid)
        {
            int err = Marshal.GetLastWin32Error();
            Console.Error.WriteLine("[-] {0} failed for PID {1}: {2} ({3})", api, pid, err, new Win32Exception(err).Message);
        }
    }
}
