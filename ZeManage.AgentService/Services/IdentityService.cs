using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZeManage.AgentService.Models;

namespace ZeManage.AgentService.Services;

/// <summary>One-shot hardware/identity fingerprint, cached after the first successful call.
/// WMI queries all carry an explicit timeout (unlike a bare ManagementObjectSearcher, which can
/// block forever if a provider — e.g. the Storage namespace used for MediaType — stalls) since a
/// hang here would otherwise stall the whole host startup before any monitor gets a chance to
/// run.</summary>
public sealed class IdentityService
{
    private readonly ILogger<IdentityService>? _log;
    private AgentIdentity? _cached;
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(5);

    public IdentityService(ILogger<IdentityService>? log = null) => _log = log;

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
            CpuModel       = ReadSingle("SELECT Name FROM Win32_Processor", "Name"),
            TotalRamGB     = ReadRamGb("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", "TotalPhysicalMemory"),
            UsableRamGB    = ReadRamGb("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem", "TotalVisibleMemorySize", kb: true),
            GpuModel       = ReadGpuModel(),
            StorageTotalGB = storageTotalGB,
            StorageUsedGB  = storageUsedGB,
            StorageType    = ReadStorageType(),
            RamType        = ReadRamType(),
            MacAddress     = ReadMacAddress(),
            IpAddress      = ReadLocalIp(),
            SerialNumber   = ReadSingle("SELECT SerialNumber FROM Win32_BIOS", "SerialNumber"),
            DeviceId       = ReadDeviceId(),
            SystemType     = ReadSystemType(),
            BiosVersion    = ReadSingle("SELECT SMBIOSBIOSVersion FROM Win32_BIOS", "SMBIOSBIOSVersion"),
            MotherboardModel = ReadMotherboardModel(),
            OsVersion      = Environment.OSVersion.VersionString,
            WindowsEdition = ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName"),
            WindowsVersion = ReadRegistryString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion"),
            OsBuild        = ReadOsBuild(),
            TimeZoneId     = ReadIanaTimeZoneId(),
        };
        return _cached;
    }

    private static string GetMachineId()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var machineGuid = key?.GetValue("MachineGuid")?.ToString()?.Trim() ?? "";
            return DeterministicGuid($"{machineGuid}|{Environment.MachineName}|agentservice");
        }
        catch
        {
            return DeterministicGuid($"{Environment.MachineName}|{Environment.UserName}|agentservice");
        }
    }

    private static string DeterministicGuid(string input)
    {
        using var md5 = MD5.Create();
        return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(input))).ToString();
    }

    private string? ReadSingle(string query, string field)
    {
        try
        {
            using var s = new ManagementObjectSearcher(query);
            s.Options.Timeout = WmiTimeout;
            foreach (var o in s.Get())
                return o[field]?.ToString()?.Trim();
        }
        catch (Exception ex) { _log?.LogWarning(ex, "WMI query failed: {Query}", query); }
        return null;
    }

    private double ReadRamGb(string query, string field, bool kb = false)
    {
        try
        {
            using var s = new ManagementObjectSearcher(query);
            s.Options.Timeout = WmiTimeout;
            foreach (var o in s.Get())
            {
                var raw = Convert.ToDouble(o[field]);
                return kb
                    ? Math.Round(raw / 1024.0 / 1024.0, 1)
                    : Math.Round(raw / 1024.0 / 1024.0 / 1024.0, 1);
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "WMI RAM query failed"); }
        return 0;
    }

    private string? ReadGpuModel()
    {
        try
        {
            var gpus = new List<string>();
            using var s = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            s.Options.Timeout = WmiTimeout;
            foreach (var o in s.Get())
            {
                var name = o["Name"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                var ramMB = (int)Math.Round(Convert.ToDouble(o["AdapterRAM"]) / 1024.0 / 1024.0);
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
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT MediaType FROM MSFT_PhysicalDisk");
            s.Options.Timeout = WmiTimeout;
            foreach (var o in s.Get())
            {
                var mediaType = Convert.ToUInt16(o["MediaType"]);
                types.Add(mediaType switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified" });
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
            s.Options.Timeout = WmiTimeout;
            foreach (var o in s.Get())
            {
                var code = o["SMBIOSMemoryType"] != null ? Convert.ToUInt16(o["SMBIOSMemoryType"]) : (ushort)0;
                var label = code switch { 20 => "DDR", 21 => "DDR2", 22 => "DDR2 FB-DIMM", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", _ => (string?)null };
                if (label != null) types.Add(label);
            }
            return types.Count > 0 ? string.Join(", ", types.Distinct()) : null;
        }
        catch (Exception ex) { _log?.LogWarning(ex, "WMI: ReadRamType failed"); }
        return null;
    }

    private static (double totalGB, double usedGB) ReadSystemDisk()
    {
        try
        {
            var sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var di = new DriveInfo(sysDrive);
            if (di.IsReady)
                return (Math.Round(di.TotalSize / 1024.0 / 1024.0 / 1024.0, 1),
                        Math.Round((di.TotalSize - di.AvailableFreeSpace) / 1024.0 / 1024.0 / 1024.0, 1));
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
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
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

    private static string? ReadLocalIp()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        return addr.Address.ToString();
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
        catch { return null; }
    }

    private string? ReadMotherboardModel()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            s.Options.Timeout = WmiTimeout;
            foreach (var o in s.Get())
            {
                var mfr = o["Manufacturer"]?.ToString()?.Trim();
                var product = o["Product"]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(mfr) && !string.IsNullOrEmpty(product)) return $"{mfr} {product}";
                return product ?? mfr;
            }
        }
        catch (Exception ex) { _log?.LogWarning(ex, "WMI: ReadMotherboardModel failed"); }
        return null;
    }

    private static string ReadSystemType()
    {
        var os = Environment.Is64BitOperatingSystem ? "64-bit operating system" : "32-bit operating system";
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
        catch { return null; }
    }

    private static string? ReadOsBuild()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null) return null;
            var build = key.GetValue("CurrentBuildNumber")?.ToString();
            var ubr = key.GetValue("UBR")?.ToString();
            return build is null ? null : (ubr is not null ? $"{build}.{ubr}" : build);
        }
        catch { return null; }
    }

    private static string? ReadIanaTimeZoneId()
    {
        try
        {
            var tz = TimeZoneInfo.Local;
            return TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var ianaId) ? ianaId : tz.Id;
        }
        catch { return null; }
    }
}
