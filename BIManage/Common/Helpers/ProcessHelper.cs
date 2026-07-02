using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace BIManage.Common.Helpers
{
    /// <summary>
    /// Helper methods for process verification, management, and resource metrics.
    /// All hardware metrics return usage percentages (0–100) as used/total ratio.
    /// </summary>
    public static class ProcessHelper
    {
        // CPU sampling state — tracks previous measurement for delta calculation
        private static DateTime _lastCpuSampleTime = DateTime.MinValue;
        private static TimeSpan _lastTotalProcessorTime = TimeSpan.Zero;
        private static readonly object _cpuLock = new object();
        private static bool _cpuBaselineSeeded;

        // GPU VRAM total — cached after first WMI query (expensive call)
        private static long _cachedTotalVram = -1;
        private static readonly object _vramLock = new object();

        // Win32 GlobalMemoryStatusEx — reliable cross-framework memory query (no WMI)
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;      // already 0–100 %
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        /// <summary>
        /// Checks if a process with the given ID is currently running
        /// </summary>
        public static bool IsProcessRunning(int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Gets the current process ID
        /// </summary>
        public static int GetCurrentProcessId()
        {
            return Process.GetCurrentProcess().Id;
        }

        /// <summary>
        /// Seeds the CPU baseline so the first real call returns a value instead of null.
        /// Call this early during startup (e.g., in RevitBootstrapper).
        /// </summary>
        public static void SeedCpuBaseline()
        {
            if (_cpuBaselineSeeded) return;
            lock (_cpuLock)
            {
                if (_cpuBaselineSeeded) return;
                var process = Process.GetCurrentProcess();
                process.Refresh();
                _lastCpuSampleTime = DateTime.UtcNow;
                _lastTotalProcessorTime = process.TotalProcessorTime;
                _cpuBaselineSeeded = true;
            }
        }

        /// <summary>
        /// Returns system-wide memory usage as a percentage (0–100).
        /// Uses Win32 GlobalMemoryStatusEx — reliable on net48 and net8.0-windows without WMI.
        /// </summary>
        public static double? GetMemoryUsagePercent()
        {
            try
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref status))
                    return Math.Round((double)status.dwMemoryLoad, 1);
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Returns the CPU usage percentage for the current process since the last call (0–100).
        /// Uses delta-based sampling: (cpuTimeDelta / wallTimeDelta × cores) × 100.
        /// Returns null on the very first call if baseline was not seeded, or on error.
        /// Thread-safe.
        /// <para>
        /// <b>⚠️ DO NOT CALL FROM THE REVIT UI THREAD.</b> Uses <see cref="Process.Refresh"/>
        /// which on some Windows configurations can block while WMI / perf-counter caches
        /// warm. Wrap in <c>Task.Run</c> if called from <c>OnIdling</c> or any other UI-bound
        /// callback. See the heartbeat handler in <c>IdlingService.cs</c> for the pattern.
        /// </para>
        /// </summary>
        public static double? GetCpuUsagePercent()
        {
            try
            {
                var process = Process.GetCurrentProcess();
                process.Refresh();

                var now = DateTime.UtcNow;
                var currentCpuTime = process.TotalProcessorTime;

                lock (_cpuLock)
                {
                    if (_lastCpuSampleTime == DateTime.MinValue)
                    {
                        // First call — record baseline, no result yet
                        _lastCpuSampleTime = now;
                        _lastTotalProcessorTime = currentCpuTime;
                        _cpuBaselineSeeded = true;
                        return null;
                    }

                    var wallElapsed = (now - _lastCpuSampleTime).TotalSeconds;
                    if (wallElapsed < 1.0) return null; // Too soon for accurate sample

                    var cpuElapsed = (currentCpuTime - _lastTotalProcessorTime).TotalSeconds;
                    _lastCpuSampleTime = now;
                    _lastTotalProcessorTime = currentCpuTime;

                    var cores = Environment.ProcessorCount;
                    var percent = (cpuElapsed / (wallElapsed * cores)) * 100.0;
                    return Math.Round(Math.Max(0.0, Math.Min(percent, 100.0)), 1);
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// Returns disk usage as a percentage (0–100) for the drive containing the given path.
        /// Falls back to the OS drive if path is null.
        /// <para>
        /// <b>⚠️ DO NOT CALL FROM THE REVIT UI THREAD.</b> <see cref="System.IO.DriveInfo"/>
        /// queries can block if the target path resolves to a network drive that is slow or
        /// disconnected. Always wrap in <c>Task.Run</c> when called from UI-bound callbacks.
        /// </para>
        /// </summary>
        public static double? GetDiskUsagePercent(string drivePath = null)
        {
            try
            {
                var root = drivePath != null
                    ? System.IO.Path.GetPathRoot(drivePath)
                    : System.IO.Path.GetPathRoot(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

                if (string.IsNullOrEmpty(root)) return null;

                var drive = new System.IO.DriveInfo(root);
                if (!drive.IsReady) return null;

                var usedBytes = drive.TotalSize - drive.TotalFreeSpace;
                return Math.Round((usedBytes / (double)drive.TotalSize) * 100.0, 1);
            }
            catch { return null; }
        }

        /// <summary>
        /// Returns this process's GPU utilization as a percentage (0–100).
        /// Primary: "GPU Engine / Utilization Percentage" perf counter (Windows 10+, no WMI).
        /// Fallback: "GPU Process Memory / Dedicated Usage" as % of total VRAM via WMI.
        /// Returns null if neither counter is available.
        /// <para>
        /// <b>⚠️ NEVER CALL FROM THE REVIT UI THREAD.</b> Confirmed root cause of a 7-minute
        /// Revit UI deadlock on 2026-05-19 (see <c>BIManageRevit_20260519_2229.log</c>):
        /// <see cref="PerformanceCounterCategory.Exists"/> and
        /// <see cref="PerformanceCounterCategory.GetInstanceNames"/> on a cold Windows
        /// perf-counter cache enumerate <i>all</i> performance counters from the registry,
        /// which routinely takes 30+ seconds and has been observed taking 7+ minutes on
        /// machines with corrupted perf-counter registries. Each subsequent
        /// <c>PerformanceCounter.NextValue()</c> on a fresh instance can take ~1s.
        /// This method <b>must</b> be invoked from a thread-pool thread — see the heartbeat
        /// handler in <c>IdlingService.cs</c> for the <c>Task.Run</c> pattern.
        /// </para>
        /// </summary>
        public static double? GetGraphicsUsagePercent()
        {
            try
            {
                var pid = Process.GetCurrentProcess().Id;
                var pidTag = $"pid_{pid}_";

                // Primary: GPU Engine utilization % — no WMI, works on net48 and net8.0-windows
                try
                {
                    if (PerformanceCounterCategory.Exists("GPU Engine"))
                    {
                        var engineCategory = new PerformanceCounterCategory("GPU Engine");
                        var engineInstances = engineCategory.GetInstanceNames()
                            .Where(i => i.IndexOf(pidTag, StringComparison.OrdinalIgnoreCase) >= 0)
                            .ToArray();

                        if (engineInstances.Length > 0)
                        {
                            double maxUtil = 0;
                            foreach (var instance in engineInstances)
                            {
                                using var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                                var val = counter.NextValue();
                                if (val > maxUtil) maxUtil = val;
                            }
                            return Math.Round(Math.Min(maxUtil, 100.0), 1);
                        }
                    }
                }
                catch { /* fall through to VRAM fallback */ }

                // Fallback: VRAM usage % (requires WMI for total VRAM)
                var totalVram = GetTotalGpuVram();
                if (totalVram <= 0) return null;

                if (!PerformanceCounterCategory.Exists("GPU Process Memory")) return null;

                var memCategory = new PerformanceCounterCategory("GPU Process Memory");
                var matchingInstances = memCategory.GetInstanceNames()
                    .Where(i => i.IndexOf(pidTag, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToArray();

                if (matchingInstances.Length == 0) return 0.0;

                long usedBytes = 0;
                foreach (var instance in matchingInstances)
                {
                    using var counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instance, readOnly: true);
                    usedBytes += (long)counter.NextValue();
                }

                return usedBytes > 0
                    ? Math.Round((usedBytes / (double)totalVram) * 100.0, 1)
                    : 0.0;
            }
            catch { return null; }
        }

        /// <summary>
        /// Gets the total dedicated GPU VRAM in bytes via WMI. Cached after first call.
        /// Uses the largest GPU if multiple are present.
        /// </summary>
        private static long GetTotalGpuVram()
        {
            lock (_vramLock)
            {
                if (_cachedTotalVram >= 0) return _cachedTotalVram;

                try
                {
                    long maxVram = 0;
                    using var searcher = new System.Management.ManagementObjectSearcher(
                        "SELECT AdapterRAM FROM Win32_VideoController");
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        var ram = Convert.ToInt64(obj["AdapterRAM"]);
                        if (ram > maxVram) maxVram = ram;
                    }
                    _cachedTotalVram = maxVram;
                    return maxVram;
                }
                catch
                {
                    _cachedTotalVram = 0;
                    return 0;
                }
            }
        }

        // ---- MB methods used by the API heartbeat payload ----

        /// <summary>
        /// Returns the current Revit process working set (physical RAM) in MB.
        /// Used by PATCH /api/v1/Revit/session/{id}/heartbeat → memoryUsageMB.
        /// </summary>
        public static int? GetMemoryUsageMb()
        {
            try
            {
                var mb = Process.GetCurrentProcess().WorkingSet64 / (1024L * 1024L);
                return (int)mb;
            }
            catch { return null; }
        }

        /// <summary>
        /// Returns the Revit process memory usage as a percentage of total system RAM (0–100).
        /// </summary>
        public static double? GetProcessMemoryPercent()
        {
            try
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (!GlobalMemoryStatusEx(ref status)) return null;
                var totalBytes = (double)status.ullTotalPhys;
                if (totalBytes <= 0) return null;
                var processBytes = (double)Process.GetCurrentProcess().WorkingSet64;
                return Math.Round((processBytes / totalBytes) * 100.0, 1);
            }
            catch { return null; }
        }

        /// <summary>
        /// Returns disk space used on the drive containing drivePath (or OS drive) in MB.
        /// Used by PATCH /api/v1/Revit/session/{id}/heartbeat → diskUsageMB.
        /// </summary>
        public static int? GetDiskUsageMb(string drivePath = null)
        {
            try
            {
                var root = drivePath != null
                    ? System.IO.Path.GetPathRoot(drivePath)
                    : System.IO.Path.GetPathRoot(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                if (string.IsNullOrEmpty(root)) return null;
                var drive = new System.IO.DriveInfo(root);
                if (!drive.IsReady) return null;
                var usedBytes = drive.TotalSize - drive.TotalFreeSpace;
                return (int)(usedBytes / (1024L * 1024L));
            }
            catch { return null; }
        }

        /// <summary>
        /// Returns GPU dedicated VRAM used by the current process in MB.
        /// Used by PATCH /api/v1/Revit/session/{id}/heartbeat → graphicsUsageMB.
        /// </summary>
        public static int? GetGraphicsUsageMb()
        {
            try
            {
                var pid = Process.GetCurrentProcess().Id;
                var category = new PerformanceCounterCategory("GPU Process Memory");
                var instances = category.GetInstanceNames();
                var pidTag = $"pid_{pid}_";
                var matchingInstances = instances
                    .Where(i => i.IndexOf(pidTag, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToArray();
                if (matchingInstances.Length == 0) return null;
                long totalBytes = 0;
                foreach (var instance in matchingInstances)
                {
                    using var counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instance, readOnly: true);
                    totalBytes += (long)counter.NextValue();
                }
                return totalBytes > 0 ? (int)(totalBytes / (1024L * 1024L)) : null;
            }
            catch { return null; }
        }
    }
}
