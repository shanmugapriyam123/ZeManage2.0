using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using ZeManage.Agent.Core.Models;

namespace ZeManage.Agent.Core.Services;

public sealed class IdentityService
{
    private AgentIdentity? _cached;

    public AgentIdentity Get()
    {
        if (_cached is not null) return _cached;

        string? sid = null;
        try { sid = WindowsIdentity.GetCurrent().User?.Value; } catch { }

        var machineId = GetMachineId();

        _cached = new AgentIdentity
        {
            MachineId    = machineId,
            WindowsSid   = sid,
            ZeUserId     = null,
            MachineName  = Environment.MachineName,
            UserName     = Environment.UserName,
            OsVersion    = Environment.OSVersion.VersionString,
            CpuModel     = ReadCpuModel(),
            TotalRamGB   = ReadTotalRamGB(),
            MacAddress   = ReadMacAddress(),
            SerialNumber = ReadSerialNumber(),
            IpAddress    = ReadLocalIp(),
        };
        return _cached;
    }

    public void SetZeUserId(Guid zeUserId)
    {
        if (_cached is null) Get();
        _cached = _cached! with { ZeUserId = zeUserId };
    }

    private static string GetMachineId()
    {
        try
        {
            var machineGuid = "";
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                machineGuid = key?.GetValue("MachineGuid")?.ToString()?.Trim() ?? "";

            var mb  = GetWmicValue("baseboard get SerialNumber");
            var cpu = GetWmicValue("cpu get ProcessorId");
            return DeterministicGuid($"{machineGuid}|{mb}|{cpu}");
        }
        catch
        {
            return DeterministicGuid($"{Environment.MachineName}|{Environment.UserName}");
        }
    }

    private static string GetWmicValue(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("wmic", args)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return string.Empty;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= 2)
            {
                var value = lines[1].Trim();
                if (!string.IsNullOrWhiteSpace(value) && value != "None") return value;
            }
        }
        catch { }
        return string.Empty;
    }

    private static string DeterministicGuid(string input)
    {
        using var md5 = MD5.Create();
        return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(input))).ToString();
    }

    private static string? ReadCpuModel()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var o in s.Get())
                return o["Name"]?.ToString()?.Trim();
        }
        catch { }
        return null;
    }

    private static double ReadTotalRamGB()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (var o in s.Get())
                return Math.Round(Convert.ToDouble(o["TotalVisibleMemorySize"]) / 1024.0 / 1024.0, 1);
        }
        catch { }
        return 0;
    }

    private static string? ReadMacAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    var mac = nic.GetPhysicalAddress().ToString();
                    if (mac.Length == 12)
                        return string.Join(":", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2)));
                }
            }
        }
        catch { }
        return null;
    }

    private static string? ReadSerialNumber()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
            foreach (var o in s.Get())
            {
                var sn = o["SerialNumber"]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(sn) && sn != "To Be Filled By O.E.M.")
                    return sn;
            }
        }
        catch { }
        return null;
    }

    private static string? ReadLocalIp()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        return addr.Address.ToString();
                }
            }
        }
        catch { }
        return null;
    }
}
