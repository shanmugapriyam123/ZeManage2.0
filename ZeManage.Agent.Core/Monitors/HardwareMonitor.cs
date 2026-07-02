using System.Diagnostics;
using System.IO;
using System.Management;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Models;
using ZeManage.Agent.Core.Services;

namespace ZeManage.Agent.Core.Monitors;

public sealed class HardwareMonitor : BackgroundService
{
    private readonly LocalStore _store;
    private readonly IdentityService _identity;
    private readonly AgentOptions _opts;
    private readonly ILogger<HardwareMonitor> _log;
    private readonly AgentState _state;

    private PerformanceCounter? _cpu;
    private double _ramTotalGB;

    public HardwareMonitor(
        LocalStore store,
        IdentityService identity,
        IOptions<AgentOptions> opts,
        ILogger<HardwareMonitor> log,
        AgentState state)
    {
        _store = store;
        _identity = identity;
        _opts = opts.Value;
        _log = log;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Initialize();
        var interval = TimeSpan.FromSeconds(_opts.HardwareIntervalSeconds);
        var id = _identity.Get();

        // first read of CPU counter is always 0 — prime it
        try { _cpu?.NextValue(); await Task.Delay(1000, stoppingToken); } catch { }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snap = CollectSnapshot(id.MachineName);
                await _store.AddHardwareSnapshotAsync(snap, stoppingToken);
                _state.LatestHardware = snap;
                _state.NotifyChanged();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Hardware snapshot failed");
            }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Initialize()
    {
        try
        {
            _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "CPU counter init failed");
        }

        try
        {
            using var search = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (var o in search.Get())
            {
                var kb = Convert.ToDouble(o["TotalVisibleMemorySize"]);
                _ramTotalGB = Math.Round(kb / 1024.0 / 1024.0, 2);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RAM total query failed");
        }
    }

    private HardwareSnapshot CollectSnapshot(string machineName)
    {
        var cpuPct = SafeRead(_cpu);
        var (ramFreeGB, ramUsedGB, ramTotalGB) = ReadRam();
        var (diskFree, diskTotal) = ReadSystemDisk();
        var diskUsedPct = diskTotal > 0 ? Math.Round((diskTotal - diskFree) / diskTotal * 100.0, 1) : 0;
        var gpuPct = ReadGpuPercent();
        var (batPct, batStatus) = ReadBattery();

        return new HardwareSnapshot
        {
            MachineName = machineName,
            CapturedAt = DateTime.UtcNow,
            CpuUsagePercent = Math.Round(cpuPct, 1),
            RamUsedGB = ramUsedGB,
            RamTotalGB = ramTotalGB,
            RamUsagePercent = ramTotalGB > 0 ? Math.Round(ramUsedGB / ramTotalGB * 100.0, 1) : 0,
            GpuUsagePercent = Math.Round(gpuPct, 1),
            DiskFreeGB = Math.Round(diskFree, 1),
            DiskTotalGB = Math.Round(diskTotal, 1),
            DiskUsagePercent = diskUsedPct,
            BatteryPercent = batPct,
            BatteryStatus = batStatus
        };
    }

    private static double SafeRead(PerformanceCounter? c)
    {
        try { return c?.NextValue() ?? 0; } catch { return 0; }
    }

    private (double free, double used, double total) ReadRam()
    {
        try
        {
            using var search = new ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (var o in search.Get())
            {
                var freeKb = Convert.ToDouble(o["FreePhysicalMemory"]);
                var freeGb = Math.Round(freeKb / 1024.0 / 1024.0, 2);
                var usedGb = Math.Round(_ramTotalGB - freeGb, 2);
                return (freeGb, usedGb, _ramTotalGB);
            }
        }
        catch { }
        return (0, 0, _ramTotalGB);
    }

    private static (double free, double total) ReadSystemDisk()
    {
        try
        {
            var sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var di = new DriveInfo(sysDrive);
            if (di.IsReady)
            {
                var freeGb = di.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
                var totalGb = di.TotalSize / 1024.0 / 1024.0 / 1024.0;
                return (freeGb, totalGb);
            }
        }
        catch { }
        return (0, 0);
    }

    private static double ReadGpuPercent()
    {
        try
        {
            var cat = new PerformanceCounterCategory("GPU Engine");
            var names = cat.GetInstanceNames().Where(n => n.EndsWith("engtype_3D")).ToArray();
            double total = 0;
            foreach (var n in names)
            {
                using var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", n);
                try { c.NextValue(); } catch { }
                Thread.Sleep(100);
                total += c.NextValue();
            }
            return names.Length > 0 ? Math.Min(100, total / names.Length) : 0;
        }
        catch { return 0; }
    }

    private static (int? percent, string? status) ReadBattery()
    {
        try
        {
            using var search = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
            foreach (var o in search.Get())
            {
                int pct = Convert.ToInt32(o["EstimatedChargeRemaining"]);
                var raw = Convert.ToInt32(o["BatteryStatus"]);
                var status = raw switch
                {
                    1 => "Discharging",
                    2 => "AC",
                    3 => "Fully Charged",
                    4 => "Low",
                    5 => "Critical",
                    6 => "Charging",
                    _ => "Unknown"
                };
                return (pct, status);
            }
        }
        catch { }
        return (null, null);
    }
}
