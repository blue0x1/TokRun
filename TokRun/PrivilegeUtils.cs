// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 Chokri Hammedi
using System;
using System.Runtime.InteropServices;

namespace TokRun
{
    internal static class PrivilegeUtils
    {
        public static void TryEnableCommonPrivileges()
        {
            TryEnablePrivilege("SeDebugPrivilege");
            TryEnablePrivilege("SeImpersonatePrivilege");
            TryEnablePrivilege("SeAssignPrimaryTokenPrivilege");
            TryEnablePrivilege("SeIncreaseQuotaPrivilege");
            // Required to change TokenSessionId when moving a duplicated token
            // from Session 0 to an interactive desktop session.
            TryEnablePrivilege("SeTcbPrivilege");
        }

        public static bool TryEnablePrivilege(string privilegeName)
        {
            IntPtr token;
            if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY, out token))
                return false;

            try
            {
                NativeMethods.LUID luid;
                if (!NativeMethods.LookupPrivilegeValue(null, privilegeName, out luid))
                    return false;

                var tp = new NativeMethods.TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = NativeMethods.SE_PRIVILEGE_ENABLED
                };

                NativeMethods.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                return Marshal.GetLastWin32Error() == 0;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
    }
}
