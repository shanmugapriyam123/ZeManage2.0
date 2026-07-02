using System;
using System.Security.Cryptography;
using System.Text;
using BIManage.Infrastructure.Logging;
using Microsoft.Win32;

namespace BIManage.Common.Helpers
{
    /// <summary>
    /// Generates a unique machine identifier (GUID) based on hardware characteristics.
    /// Uses Windows MachineGuid (registry) + motherboard serial + processor ID.
    /// WMI is unreliable on .NET 8 (returns empty), so registry is the primary source
    /// with WMI via wmic.exe as supplementary data.
    /// </summary>
    public static class MachineIdentifier
    {
        private static string? _cachedMachineId;
        private static readonly object _lock = new object();

        /// <summary>
        /// Get unique machine ID as GUID string.
        /// Returns a deterministic GUID based on hardware characteristics.
        /// </summary>
        public static string GetMachineId(ILogger? logger = null)
        {
            lock (_lock)
            {
                if (_cachedMachineId != null)
                {
                    return _cachedMachineId;
                }

                try
                {
                    // Primary: Windows MachineGuid from registry — stable, works on all .NET runtimes
                    var machineGuid = GetWindowsMachineGuid();

                    // Supplementary: hardware IDs via wmic.exe (works on both .NET 4.8 and .NET 8)
                    var motherboardSerial = GetWmicValue("baseboard get SerialNumber");
                    var processorId = GetWmicValue("cpu get ProcessorId");

                    // Combine identifiers
                    var combinedId = $"{machineGuid}|{motherboardSerial}|{processorId}";

                    logger?.LogInfo($"Hardware IDs: MachineGuid=[{machineGuid}], MB=[{motherboardSerial}], CPU=[{processorId}]");

                    // Generate deterministic GUID from combined hardware ID
                    _cachedMachineId = GenerateDeterministicGuid(combinedId);

                    logger?.LogInfo($"Machine ID generated: {_cachedMachineId}");
                    return _cachedMachineId;
                }
                catch (Exception ex)
                {
                    logger?.LogError($"Failed to generate machine ID: {ex.Message}", ex);

                    // Fallback: Use computer name + user name as backup
                    var fallbackId = $"{Environment.MachineName}|{Environment.UserName}";
                    _cachedMachineId = GenerateDeterministicGuid(fallbackId);

                    logger?.LogWarning($"Using fallback machine ID based on computer name: {_cachedMachineId}");
                    return _cachedMachineId;
                }
            }
        }

        /// <summary>
        /// Get Windows MachineGuid from registry — unique per Windows installation,
        /// never changes, works identically on .NET 4.8 and .NET 8.
        /// </summary>
        private static string GetWindowsMachineGuid()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    var guid = key?.GetValue("MachineGuid")?.ToString();
                    if (!string.IsNullOrWhiteSpace(guid))
                        return guid.Trim();
                }
            }
            catch
            {
                // Registry access denied or key not found
            }

            return string.Empty;
        }

        /// <summary>
        /// Runs wmic.exe to get a hardware value. Works identically on .NET 4.8 and .NET 8
        /// because it's an external process, not in-process COM/WMI.
        /// </summary>
        private static string GetWmicValue(string wmicArgs)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("wmic", wmicArgs)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    if (process == null) return string.Empty;

                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);

                    // wmic output: first line is header, second line is value
                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length >= 2)
                    {
                        var value = lines[1].Trim();
                        if (!string.IsNullOrWhiteSpace(value) && value != "None")
                            return value;
                    }
                }
            }
            catch
            {
                // wmic not available or access denied
            }

            return string.Empty;
        }

        /// <summary>
        /// Generate deterministic GUID from string input using MD5 hash.
        /// Same input always produces same GUID.
        /// </summary>
        private static string GenerateDeterministicGuid(string input)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
                var guid = new Guid(hash);
                return guid.ToString();
            }
        }

        /// <summary>
        /// Clear cached machine ID (useful for testing)
        /// </summary>
        internal static void ClearCache()
        {
            lock (_lock)
            {
                _cachedMachineId = null;
            }
        }
    }
}
