using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZeManage.Agent.Core.Models;

namespace ZeManage.Agent.Core.Services;

public sealed class IdentityService
{
    private readonly ILogger<IdentityService>? _log;
    private AgentIdentity? _cached;

    public IdentityService(ILogger<IdentityService>? log = null)
    {
        _log = log;
    }

    public AgentIdentity Get()
    {
        if (_cached is not null) return _cached;

        string? sid = null;
        try { sid = WindowsIdentity.GetCurrent().User?.Value; } catch { }

        var (storageTotalGB, storageUsedGB) = ReadSystemDisk();

        _cached = new AgentIdentity
        {
            MachineId      = GetMachineId(),
            WindowsSid     = sid,
            MachineName    = Environment.MachineName,
            UserName       = Environment.UserName,
            CpuModel       = ReadCpuModel(),
            TotalRamGB     = ReadTotalRamGB(),
            UsableRamGB    = ReadUsableRamGB(),
            GpuModel       = ReadGpuModel(),
            StorageTotalGB = storageTotalGB,
            StorageUsedGB  = storageUsedGB,
            StorageType    = ReadStorageType(),
            RamType        = ReadRamType(),
            MacAddress     = ReadMacAddress(),
            IpAddress      = ReadLocalIp(),
            SerialNumber   = ReadSerialNumber(),
            DeviceId       = ReadDeviceId(),
            SystemType     = ReadSystemType(),
            BiosVersion    = ReadBiosVersion(),
            MotherboardModel = ReadMotherboardModel(),
            OsVersion      = Environment.OSVersion.VersionString,
            WindowsEdition = ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName"),
            WindowsVersion = ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion"),
            OsBuild        = ReadOsBuild(),
            TimeZoneId     = ReadIanaTimeZoneId(),
        };
        return _cached;
    }

    // REVERTED 2026-08-07: this used to compute MachineId from real Win32_BaseBoard/Win32_Processor
    // WMI values (via ManagementObjectSearcher, matching every other hardware field in this file)
    // instead of shelling out to the now-often-missing wmic.exe. That change is technically more
    // correct, but every ALREADY-DEPLOYED agent has a device record on the server keyed to the id
    // this exact (broken-wmic, motherboard/cpu-less) formula produces — TokenProvider.ValidateDeviceAsync
    // posts the computed MachineId to /api/v1/tenant/device/auth/validate-device, which 401s for any
    // MachineId that doesn't match an existing registered device. Switching to real WMI values changed
    // the computed id and locked the agent out of authenticating entirely (confirmed on GF-S-037:
    // validate-device started returning 401, hub stuck in "Auth Failed", nothing could sync). Reverted
    // to keep already-registered machines working. If this needs revisiting, it has to go through a
    // proper re-registration/migration path (re-POST /api/v1/agent/register under the new id) — not a
    // silent recompute — or every existing install breaks the same way this one did.
    private static string GetMachineId()
    {
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
            {
                var machineGuid = key?.GetValue("MachineGuid")?.ToString()?.Trim() ?? "";
                var mb  = GetWmicValue("baseboard get SerialNumber");
                var cpu = GetWmicValue("cpu get ProcessorId");
                return DeterministicGuid($"{machineGuid}|{mb}|{cpu}");
            }
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

    private string? ReadCpuModel()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            s.Options.Timeout = TimeSpan.FromSeconds(5);
            foreach (var o in s.Get())
                return o["Name"]?.ToString()?.Trim();
        }
        catch (Exception ex) { _log?.LogWarning(ex, "WMI: ReadCpuModel failed"); }
        return null;
    }

    private double ReadTotalRamGB()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            s.Options.Timeout = TimeSpan.FromSeconds(5);
            foreach (var o in s.Get())
                return Math.Round(Convert.ToDouble(o["TotalPhysicalMemory"]) / 1024.0 / 1024.0 / 1024.0, 1);
        }
        catch (Exception ex) { _log?.LogWarning(ex, "WMI: ReadTotalRamGB failed"); }
        return 0;
    }

    private double ReadUsableRamGB()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
            s.Options.Timeout = TimeSpan.FromSeconds(5);
            foreach (var o in s.Get())
                return Math.Round(Convert.ToDouble(o["TotalVisibleMemorySize"]) / 1024.0 / 1024.0, 1);
        }
        catch (Exception ex) { _log?.LogWarning(ex, "WMI: ReadUsableRamGB failed"); }
        return 0;
    }

    private string? ReadGpuModel()
    {
        try
        {
            var gpus = new List<string>();
            using var s = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            s.Options.Timeout = TimeSpan.FromSeconds(5);
            foreach (var o in s.Get())
            {
                var name = o["Name"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                var ramBytes = Convert.ToDouble(o["AdapterRAM"]);
                var ramMB = (int)Math.Round(ramBytes / 1024.0 / 1024.0);
                gpus.Add(ramMB > 0 ? $"{name} ({ramMB} MB)" : name);
            }
            return gpus.Count > 0 ? string.Join("; ", gpus) : null;
        }
        catch (Exception ex) { _log?.LogWarning(ex, "WMI: ReadGpuModel failed"); }
        return null;
    }

    private string? ReadStorageType()
    {
        try
        {
            var types = new List<string>();
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT MediaType FROM MSFT_PhysicalDisk");
            s.Options.Timeout = TimeSpan.FromSeconds(5);
            foreach (var o in s.Get())
            {
                var mediaType = Convert.ToUInt16(o["MediaType"]);
                var label = mediaType switch
                {
                    3 => "HDD",
                    4 => "SSD",
                    5 => "SCM",
                    _ => "Unspecified"
                };
                types.Add(label);
            }
            return types.Count > 0 ? string.Join("; ", types.Distinct()) : null;
        }
        catch (Exception ex) { _log?.LogWarning(ex, "WMI: ReadStorageType failed"); }
        return null;
    }

    private string? ReadRamType()
    {
        try
        {
            var types = new List<string>();
            using var s = new ManagementObjectSearcher("SELECT SMBIOSMemoryType FROM Win32_PhysicalMemory");
            s.Options.Timeout = TimeSpan.FromSeconds(5);
            foreach (var o in s.Get())
            {
                var code = o["SMBIOSMemoryType"] != null ? Convert.ToUInt16(o["SMBIOSMemoryType"]) : (ushort)0;
                var label = MapRamType(code);
                if (label != null) types.Add(label);
            }
            return types.Count > 0 ? string.Join(", ", types.Distinct()) : null;
        }
        catch (Exception ex) { _log?.LogWarning(ex, "WMI: ReadRamType failed"); }
        return null;
    }

    private static string? MapRamType(ushort smbiosMemoryType) => smbiosMemoryType switch
    {
        20 => "DDR",
        21 => "DDR2",
        22 => "DDR2 FB-DIMM",
        24 => "DDR3",
        26 => "DDR4",
        34 => "DDR5",
        _  => null
    };

    private static (double totalGB, double usedGB) ReadSystemDisk()
    {
        try
        {
            var sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var di = new DriveInfo(sysDrive);
            if (di.IsReady)
            {
                var totalGB = Math.Round(di.TotalSize / 1024.0 / 1024.0 / 1024.0, 1);
                var usedGB  = Math.Round((di.TotalSize - di.AvailableFreeSpace) / 1024.0 / 1024.0 / 1024.0, 1);
                return (totalGB, usedGB);
            }
        }
        catch { }
        return (0, 0);
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

    private string? ReadSerialNumber()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
            s.Options.Timeout = TimeSpan.FromSeconds(5);
            foreach (var o in s.Get())
            {
                var sn = o["SerialNumber"]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(sn) && sn != "To Be Filled By O.E.M.")
                    return sn;
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "WMI: ReadSerialNumber failed"); }
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

    private static string? ReadDeviceId()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\SQMClient");
            return key?.GetValue("MachineId")?.ToString()?.Trim('{', '}');
        }
        catch { }
        return null;
    }

    private string? ReadBiosVersion()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
            s.Options.Timeout = TimeSpan.FromSeconds(5);
            foreach (var o in s.Get())
                return o["SMBIOSBIOSVersion"]?.ToString()?.Trim();
        }
        catch (Exception ex) { _log?.LogWarning(ex, "WMI: ReadBiosVersion failed"); }
        return null;
    }

    private string? ReadMotherboardModel()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            s.Options.Timeout = TimeSpan.FromSeconds(5);
            foreach (var o in s.Get())
            {
                var mfr     = o["Manufacturer"]?.ToString()?.Trim();
                var product = o["Product"]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(mfr) && !string.IsNullOrEmpty(product))
                    return $"{mfr} {product}";
                return product ?? mfr;
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "WMI: ReadMotherboardModel failed"); }
        return null;
    }

    private static string ReadSystemType()
    {
        var os  = Environment.Is64BitOperatingSystem ? "64-bit operating system" : "32-bit operating system";
        var cpu = Environment.Is64BitProcess ? "x64-based processor" : "x86-based processor";
        return $"{os}, {cpu}";
    }

    private static string? ReadRegistryString(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(valueName)?.ToString()?.Trim();
        }
        catch { }
        return null;
    }

    private static string? ReadOsBuild()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null) return null;
            var build = key.GetValue("CurrentBuildNumber")?.ToString();
            var ubr   = key.GetValue("UBR")?.ToString();
            if (build is null) return null;
            return ubr is not null ? $"{build}.{ubr}" : build;
        }
        catch { }
        return null;
    }

    private static string? ReadIanaTimeZoneId()
    {
        try
        {
            var tz = TimeZoneInfo.Local;
            // On Windows, Local.Id is a Windows ID (e.g. "India Standard Time").
            // TryConvertWindowsIdToIanaId maps it to IANA (e.g. "Asia/Kolkata").
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var ianaId))
                return ianaId;
            // On Linux/Mac .NET already returns the IANA ID directly.
            return tz.Id;
        }
        catch { }
        return null;
    }
}
