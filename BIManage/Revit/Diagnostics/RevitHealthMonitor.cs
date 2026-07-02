using System;
using System.Reflection;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.Diagnostics
{
    /// <summary>
    ///     Lightweight health tracker for startup/shutdown.
    /// </summary>
    public class RevitHealthMonitor : IRevitHealthMonitor
    {
        private readonly ILogger? _logger;

        public RevitHealthMonitor(ILogger? logger)
        {
            _logger = logger;
        }

        public void RecordStartup()
        {
            _logger?.LogInfo($"Revit add-in started at {DateTime.Now:G}");

            // Log the loaded BIManageRevit assembly identity. Useful for verifying which
            // build is actually loaded (per-user vs all-users deploy, stale cache, etc.)
            // when diagnosing version-mismatch problems like the WPF pack-URI lookup
            // failures that hit on Revit 2025 in 0.2.3.0.
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var asmName = asm.GetName();
                var fileVersion = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "?";
                var infoVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
                string location;
                try { location = asm.Location; }
                catch { location = "(in-memory)"; }
                _logger?.LogInfo(
                    $"[AsmIdentity] Loaded {asmName.Name} " +
                    $"AssemblyVersion={asmName.Version} FileVersion={fileVersion} " +
                    $"InformationalVersion={infoVersion} From={location}");

                // Detect duplicate-load: if more than one BIManageRevit assembly is in the AppDomain,
                // WPF pack-URI resolution and Application.Instance singletons silently break.
                // Caused historically by AppDomain.CurrentDomain.AssemblyResolve pre-loading our
                // own DLL via LoadFrom while Revit had it in a collectible ALC.
                var matches = new System.Collections.Generic.List<Assembly>();
                foreach (var loaded in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (string.Equals(loaded.GetName().Name, asmName.Name, System.StringComparison.OrdinalIgnoreCase))
                            matches.Add(loaded);
                    }
                    catch { }
                }
                if (matches.Count > 1)
                {
                    _logger?.LogWarning(
                        $"[AsmIdentity] DUPLICATE LOAD DETECTED: {matches.Count} copies of {asmName.Name} are in the AppDomain. " +
                        $"This breaks WPF dialogs (XAML resource lookup) and Application.Instance singletons.");
                    for (int i = 0; i < matches.Count; i++)
                    {
                        try
                        {
                            var dup = matches[i];
                            string dupLoc;
                            try { dupLoc = dup.Location; }
                            catch { dupLoc = "(in-memory / no location)"; }
                            string alcName = "?";
                            try
                            {
#if NETCOREAPP || NET
                                var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(dup);
                                alcName = $"{alc?.Name ?? "(default)"} (collectible={alc?.IsCollectible})";
#endif
                            }
                            catch { }
                            _logger?.LogWarning(
                                $"[AsmIdentity]   copy #{i + 1}: HashCode={dup.GetHashCode()} " +
                                $"FullName={dup.FullName} ALC={alcName} From={dupLoc}");
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[AsmIdentity] Failed to read assembly identity: {ex.Message}");
            }
        }

        public void RecordShutdown()
        {
            _logger?.LogInfo($"Revit add-in shut down at {DateTime.Now:G}");
        }
    }
}
