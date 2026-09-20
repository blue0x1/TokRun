using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace TokRun
{
    // Modern Windows/Server console broker.
    //
    // The parent starts this TokRun instance under the selected token in the
    // caller's interactive session. The broker then allocates an ordinary
    // Windows console and starts the requested console client normally. Because
    // the broker already has the target identity and interactive session, cmd.exe
    // or PowerShell inherits both without cross-token console creation.
    internal static class NativeConsoleBroker
    {
        public static int Run(string commandLine, string workDir, string expectedUser)
        {
            string actualUser;
            try
            {
                actualUser = WindowsIdentity.GetCurrent().Name;
            }
            catch (Exception ex)
            {
                return Fail("Could not determine broker identity: " + ex.Message);
            }

            if (!String.IsNullOrWhiteSpace(expectedUser) &&
                !TokenUtils.MatchesUser(actualUser, expectedUser))
            {
                return Fail("Broker identity mismatch. Expected '" + expectedUser +
                    "', actual '" + actualUser + "'.");
            }

            if (String.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
                workDir = Environment.GetFolderPath(Environment.SpecialFolder.System);

            bool consoleAttached = NativeMethods.AllocConsole();
            if (!consoleAttached)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 5)
                    return Fail("AllocConsole: " + error + " (" + new Win32Exception(error).Message + ")");
            }

            NativeMethods.SetConsoleTitleW("TokRun - " + actualUser);

            var pi = new NativeMethods.PROCESS_INFORMATION();
            var si = new NativeMethods.STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(NativeMethods.STARTUPINFO));
            si.lpDesktop = @"winsta0\default";

            try
            {
                var mutableCommand = new StringBuilder(commandLine, commandLine.Length + 64);

                // Do NOT use CREATE_NEW_CONSOLE here. The real shell should inherit
                // the broker console, whether Windows attached it at broker startup
                // or this process created it with AllocConsole.
                if (!NativeMethods.CreateProcessW(
                    null,
                    mutableCommand,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    0,
                    IntPtr.Zero,
                    workDir,
                    ref si,
                    out pi))
                {
                    return FailLastError("CreateProcessW(console client)");
                }

                NativeMethods.CloseHandle(pi.hThread);
                pi.hThread = IntPtr.Zero;

                // Keep the broker alive for the lifetime of the interactive shell.
                // If the broker exited, Windows could tear down the console while
                // the client is still using it.
                NativeMethods.WaitForSingleObject(pi.hProcess, NativeMethods.INFINITE);

                uint exitCode;
                if (NativeMethods.GetExitCodeProcess(pi.hProcess, out exitCode))
                    return unchecked((int)exitCode);

                return 0;
            }
            finally
            {
                if (pi.hThread != IntPtr.Zero)
                    NativeMethods.CloseHandle(pi.hThread);
                if (pi.hProcess != IntPtr.Zero)
                    NativeMethods.CloseHandle(pi.hProcess);
                NativeMethods.FreeConsole();
            }
        }

        private static int FailLastError(string operation)
        {
            int error = Marshal.GetLastWin32Error();
            return Fail(operation + ": " + error + " (" + new Win32Exception(error).Message + ")");
        }

        private static int Fail(string message)
        {
            try
            {
                // This process was intentionally created without a console, so use
                // the Application event log only if available and return a nonzero
                // code. The parent detects the failed broker and prints diagnostics.
                System.Diagnostics.Debug.WriteLine("TokRun broker: " + message);
            }
            catch { }
            return 40;
        }
    }
}
