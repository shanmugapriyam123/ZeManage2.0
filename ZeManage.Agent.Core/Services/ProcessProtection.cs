using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Extensions.Logging;

namespace ZeManage.Agent.Core.Services;

/// <summary>Tamper-resistance: denies PROCESS_TERMINATE on the calling process to everyone except
/// Administrators/SYSTEM, so a standard (non-elevated) user's Task Manager "End Task" / taskkill
/// fails with Access Denied. A genuinely elevated admin (SeDebugPrivilege enabled) still bypasses
/// the DACL entirely and can stop it; a non-elevated admin session is UAC-filtered the same as a
/// standard user and is denied too — which matches "admin only" in practice (must elevate).</summary>
public static class ProcessProtection
{
    private const int DACL_SECURITY_INFORMATION = 0x4;
    private const int PROTECTED_DACL_SECURITY_INFORMATION = unchecked((int)0x80000000);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(IntPtr handle, int securityInformation, byte[] securityDescriptor);

    // BA = Builtin Administrators, SY = Local System, WD = Everyone.
    // Administrators/SYSTEM keep full access (0x1FFFFF = PROCESS_ALL_ACCESS); everyone else keeps
    // only PROCESS_QUERY_LIMITED_INFORMATION (0x1000) + SYNCHRONIZE (0x100000) — enough for Task
    // Manager to still see/query the process — but PROCESS_TERMINATE (0x0001) is withheld.
    // "D:P" (protected) makes this the process's only DACL — no inherited ACE can restore access.
    private const string RestrictedDaclSddl =
        "D:P(A;;0x1FFFFF;;;BA)(A;;0x1FFFFF;;;SY)(A;;0x101000;;;WD)";

    public static void RestrictTerminationToAdmins(ILogger? log = null)
    {
        try
        {
            var sd = new RawSecurityDescriptor(RestrictedDaclSddl);
            var bytes = new byte[sd.BinaryLength];
            sd.GetBinaryForm(bytes, 0);

            var handle = Process.GetCurrentProcess().Handle;
            var ok = SetKernelObjectSecurity(handle, DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION, bytes);
            if (!ok)
                log?.LogWarning("ProcessProtection: SetKernelObjectSecurity failed (Win32 error {Error})", Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "ProcessProtection: failed to restrict process termination");
        }
    }
}
