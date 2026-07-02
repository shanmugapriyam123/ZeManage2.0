using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
#if NET8_0_OR_GREATER
using System.Runtime.Loader;
#endif

namespace BIManageRevit.Shim
{
    /// <summary>
    ///   Tiny IExternalApplication forwarder. Listed in BIManageRevit.addin's
    ///   &lt;FullClassName&gt;. Its sole job is to load BIManageRevit.dll (the
    ///   "Core") into a dedicated AssemblyLoadContext on Revit 2025+ and forward
    ///   OnStartup/OnShutdown to the Core's existing
    ///   <c>BIManageRevit.BIManage.Revit.Applications.Application</c> class.
    ///   <para>
    ///   On Revit 2021-2024 (.NET Framework 4.8) there is no AssemblyLoadContext
    ///   concept; we fall back to <see cref="Assembly.LoadFrom(string)"/>, which
    ///   loads the Core into the default load context. The dual-load pathology
    ///   does not exist on net48 (160+ R24 logs surveyed: zero DUPLICATE LOAD
    ///   warnings), so a single load is sufficient there.
    ///   </para>
    ///   <para>
    ///   By design, this class has NO Revit-API event subscriptions, NO command
    ///   bindings, NO WPF dialogs, NO singletons. The only state it holds is the
    ///   reference to the Core's instance for OnShutdown forwarding. If Revit's
    ///   native AddInManager dual-loads the Shim DLL itself, that is harmless
    ///   because the Shim has no subscriptions for the native scan to mis-attribute.
    ///   </para>
    /// </summary>
    public sealed class ShimApplication : IExternalApplication
    {
        private const string CoreDllFileName = "BIManageRevit.dll";
        private const string CoreApplicationTypeFullName =
            "BIManageRevit.BIManage.Revit.Applications.Application";

        // ALC name "BIManageRevit" matches what historical logs show, so CrossAlcDispatch's
        // name-based ALC matching (see CrossAlcDispatch.cs) finds the live instance
        // without needing changes.
        private const string CoreAlcName = "BIManageRevit";

        private static IExternalApplication? _coreApp;
        private static Assembly? _coreAssembly;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                var shimDir = Path.GetDirectoryName(typeof(ShimApplication).Assembly.Location);
                if (string.IsNullOrEmpty(shimDir))
                {
                    ShimLog("FATAL: Could not determine shim directory (Assembly.Location is empty). Aborting Core load.");
                    return Result.Failed;
                }

                var corePath = Path.Combine(shimDir, CoreDllFileName);
                if (!File.Exists(corePath))
                {
                    ShimLog($"FATAL: Core DLL not found at '{corePath}'. The deployment is missing BIManageRevit.dll alongside the Shim. Reinstall the add-in.");
                    return Result.Failed;
                }

                ShimLog($"Loading Core from '{corePath}'");

#if NET8_0_OR_GREATER
                // LAYER 3 (WPF pack URI fix — EAGER variant). Confirmed required on
                // Revit 2025 in BIManageRevit_20260520_1611.log at 16:12:29.770: even
                // with Application.ResourceAssembly correctly SET both times, WPF still
                // throws "does not have a resource identified by the URI ...". Root cause:
                // when WPF's BAML reader resolves the pack URI, it iterates
                // AppDomain.CurrentDomain.GetAssemblies() and finds our Custom-ALC Core
                // first (it IS visible across ALCs in the assembly list). But on .NET 8
                // the Custom-ALC assembly's embedded resources are NOT accessible to WPF's
                // resource reader running in Default ALC (PresentationFramework lives in
                // Default ALC). Result: assembly found, BAML stream lookup fails.
                //
                // The lazy AssemblyLoadContext.Default.Resolving hook does NOT fire
                // because WPF never gets to "Assembly.Load failed" — the Custom-ALC copy
                // satisfies the iteration first. Eager load is required.
                //
                // Fix: load BIManageRevit.dll INTO the Default ALC at Shim startup,
                // before WPF needs it. This creates a SECOND copy of the Core — used
                // exclusively by WPF's BAML reader — while the Custom-ALC copy remains
                // the live one (Application.Instance, services, event handlers). When
                // WPF iterates GetAssemblies() it finds BOTH; WPF's selection logic
                // prefers the one whose ALC matches PresentationFramework's ALC
                // (Default), so BAML lookup goes against the Default-ALC copy whose
                // embedded resources ARE accessible to WPF.
                //
                // SAFETY: the Default-ALC copy is INERT. The Core's static ctor at
                // Application.cs:66-87 has a guard that detects when running in
                // Default ALC and bails out before RegisterManagedAssemblyResolvers,
                // before ServiceRegistry init, before ANY event subscription. So
                // Revit's native AddInManager scan finds no command bindings registered
                // from the Default-ALC copy, sidestepping the historical
                // AddInManager::commandHasAddinSubscriptions crash class entirely. The
                // Custom-ALC copy remains the only "live" copy.
                // SYNCHRONOUS for now — we need to prove the mechanism works before
                // optimising to async. If this resolves the WPF pack URI errors, we
                // can move it to Task.Run for zero startup-blocking cost (Option β
                // in the implementation plan). Adds ~50-500ms to Shim startup.
                var eagerSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var defaultAlcCopy = AssemblyLoadContext.Default.LoadFromAssemblyPath(corePath);
                    eagerSw.Stop();
                    ShimLog($"[DefaultAlcEager] Pre-loaded BIManageRevit into Default ALC for WPF BAML access ({eagerSw.ElapsedMilliseconds}ms): {defaultAlcCopy.FullName}");

                    // BAML accessibility verification: prove the WPF embedded-resource container is reachable
                    // from the Default-ALC copy and that a representative dialog's BAML entry exists. If
                    // these come back null/missing, eager pre-load did the load but the embedded resources
                    // are NOT accessible — which would explain why dialog construction still throws
                    // "does not have a resource identified by the URI ...".
                    try
                    {
                        var resourceNames = defaultAlcCopy.GetManifestResourceNames();
                        ShimLog($"[DefaultAlcEager][Verify] Manifest resources on Default-ALC copy: count={resourceNames.Length}, names=[{string.Join(", ", resourceNames)}]");

                        using var gRes = defaultAlcCopy.GetManifestResourceStream("BIManageRevit.g.resources");
                        var gResLen = gRes?.Length ?? -1;
                        ShimLog($"[DefaultAlcEager][Verify] BIManageRevit.g.resources stream: len={gResLen} (expect >0)");

                        if (gRes != null)
                        {
                            // Enumerate the .g.resources container — this is a System.Resources.ResourceReader binary.
                            // We need it loaded into Default ALC (System.Resources.Extensions is built-in).
                            using var reader = new System.Resources.ResourceReader(gRes);
                            int bamlCount = 0;
                            bool foundSignIn = false;
                            foreach (System.Collections.DictionaryEntry e in reader)
                            {
                                var key = (e.Key as string) ?? string.Empty;
                                if (key.EndsWith(".baml", StringComparison.OrdinalIgnoreCase))
                                {
                                    bamlCount++;
                                    if (key.IndexOf("signindialog", StringComparison.OrdinalIgnoreCase) >= 0)
                                        foundSignIn = true;
                                }
                            }
                            ShimLog($"[DefaultAlcEager][Verify] BAML entries in g.resources: count={bamlCount}, signindialog.baml present={foundSignIn}");
                        }
                    }
                    catch (Exception vEx)
                    {
                        ShimLog($"[DefaultAlcEager][Verify] EXCEPTION during BAML verification: {vEx.GetType().Name}: {vEx.Message}");
                    }
                }
                catch (Exception ex)
                {
                    eagerSw.Stop();
                    ShimLog($"[DefaultAlcEager] FAILED to pre-load BIManageRevit into Default ALC ({eagerSw.ElapsedMilliseconds}ms): {ex.GetType().Name}: {ex.Message}. WPF dialogs may throw 'does not have a resource' errors.");
                }

                // Belt-and-braces: ALSO register the Resolving event as a fallback in case
                // WPF or some other code asks Default ALC to bind BIManageRevit later
                // (e.g., a deferred reflection lookup that the eager pre-load missed).
                // The eager load above usually satisfies WPF; this Resolving event is the
                // safety net.
                AssemblyLoadContext.Default.Resolving += (alc, asmName) =>
                {
                    if (asmName?.Name == null) return null;
                    if (!string.Equals(asmName.Name, "BIManageRevit", StringComparison.OrdinalIgnoreCase))
                        return null;
                    try
                    {
                        var defaultAlcCopy = alc.LoadFromAssemblyPath(corePath);
                        ShimLog($"[DefaultAlcResolve] (fallback) Loaded BIManageRevit into Default ALC: {defaultAlcCopy.FullName}");
                        return defaultAlcCopy;
                    }
                    catch (Exception ex)
                    {
                        ShimLog($"[DefaultAlcResolve] (fallback) FAILED: {ex.GetType().Name}: {ex.Message}");
                        return null;
                    }
                };

                // Revit 2025+ (.NET 8/10): load the Core into a dedicated, non-collectible
                // AssemblyLoadContext. Non-collectible because the assembly lives for the
                // entire Revit session; collectible would require freeing it on OnShutdown
                // and forbids loading some native-dependent assemblies (System.Data.SQLite).
                var coreAlc = new AssemblyLoadContext(CoreAlcName, isCollectible: false);

                // Resolve the Core's dependencies (private DLLs deployed alongside the Core
                // such as Nice3point.Revit.Toolkit, CommunityToolkit.Mvvm, SQLite, etc.)
                // INTO the same custom ALC. Without this, when the Core code first touches
                // one of those types, the Default ALC's AssemblyResolve handler resolves it,
                // and the dependency ends up loaded into a different ALC than the Core —
                // producing the cross-ALC type-identity mismatches we are trying to avoid.
                coreAlc.Resolving += (alc, asmName) =>
                {
                    if (asmName?.Name == null) return null;

                    // Try a flat probe in the shim/core directory (most NuGet payloads land here).
                    var probe = Path.Combine(shimDir, asmName.Name + ".dll");
                    if (File.Exists(probe))
                    {
                        try { return alc.LoadFromAssemblyPath(probe); }
                        catch (Exception ex) { ShimLog($"WARN: failed to load '{probe}' into Core ALC: {ex.Message}"); }
                    }

                    // Also probe the lib/ subfolder for our isolated private deps (e.g.
                    // Newtonsoft.Json, which is kept out of the flat layout so it doesn't
                    // shadow Revit's own copy). See BIManageRevit.csproj BuildAddons target.
                    var libProbe = Path.Combine(shimDir, "lib", asmName.Name + ".dll");
                    if (File.Exists(libProbe))
                    {
                        try { return alc.LoadFromAssemblyPath(libProbe); }
                        catch (Exception ex) { ShimLog($"WARN: failed to load '{libProbe}' into Core ALC: {ex.Message}"); }
                    }

                    // Not ours — fall back to the Default ALC (handles Revit API assemblies,
                    // framework types, anything Revit itself has loaded).
                    return null;
                };

                _coreAssembly = coreAlc.LoadFromAssemblyPath(corePath);
                ShimLog($"Core loaded into ALC '{CoreAlcName}': {_coreAssembly.FullName}");
#else
                // Revit 2021-2024 (.NET Framework 4.8): no ALC concept. LoadFrom places the
                // Core in the default load context. The dual-load pathology has never been
                // observed on net48 in any of the surveyed logs, so this is sufficient.
                _coreAssembly = Assembly.LoadFrom(corePath);
                ShimLog("Core loaded via Assembly.LoadFrom (net48 path): " + _coreAssembly.FullName);
#endif

                var coreAppType = _coreAssembly.GetType(CoreApplicationTypeFullName, throwOnError: false);
                if (coreAppType == null)
                {
                    ShimLog($"FATAL: Core type '{CoreApplicationTypeFullName}' not found in '{_coreAssembly.FullName}'. The Core DLL may be from a different version. Reinstall the add-in.");
                    return Result.Failed;
                }

                object? instance;
                try
                {
                    instance = Activator.CreateInstance(coreAppType);
                }
                catch (Exception ex)
                {
                    ShimLog($"FATAL: Could not construct Core type '{coreAppType.FullName}': {ex}");
                    return Result.Failed;
                }

                // IExternalApplication and UIControlledApplication both live in RevitAPIUI.dll,
                // which is loaded into the Default ALC by Revit itself. Both the Shim and the
                // Core reference the SAME Revit-loaded RevitAPIUI, so IExternalApplication is
                // the same type from both sides — this cast is safe across the ALC boundary.
                _coreApp = instance as IExternalApplication;
                if (_coreApp == null)
                {
                    ShimLog($"FATAL: '{coreAppType.FullName}' does not implement IExternalApplication (or the interface identity differs across ALCs — should never happen if RevitAPIUI is shared).");
                    return Result.Failed;
                }

                // Forward to the Core's OnStartup. Inside the Core, this initialises the
                // ServiceRegistry, registers Revit event handlers, builds the ribbon, etc.
                // All subscriptions registered during this call are attributed by Revit's
                // native AddInManager to the .addin currently invoking OnStartup, which is
                // the Shim — keeping AddInManager's host-resolution unambiguous.
                var result = _coreApp.OnStartup(application);
                ShimLog($"Core OnStartup returned: {result}");
                return result;
            }
            catch (Exception ex)
            {
                ShimLog($"FATAL: ShimApplication.OnStartup threw: {ex}");
                // Do NOT rethrow — that would propagate into Revit's add-in loader and
                // potentially destabilise other add-ins. Returning Failed deactivates this
                // add-in cleanly and lets Revit start without us.
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            if (_coreApp == null) return Result.Succeeded;

            try
            {
                return _coreApp.OnShutdown(application);
            }
            catch (Exception ex)
            {
                ShimLog($"WARN: ShimApplication.OnShutdown threw: {ex.Message}");
                return Result.Failed;
            }
        }

        /// <summary>
        ///   Fallback logger. The Core's structured Logger is owned by the Core's
        ///   ServiceRegistry, which doesn't exist until the Core successfully loads
        ///   and calls Bootstrapper.Initialize. The Shim therefore writes to a
        ///   separate, append-only file so failures BEFORE the Core comes alive are
        ///   not silent. Path matches the Core's log directory so the user can find
        ///   both in one place when reporting an issue.
        /// </summary>
        private static void ShimLog(string line)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BIManageRevit", "Logs");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "BIManageRevit_Shim.log");
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [Shim] {line}{Environment.NewLine}");
            }
            catch
            {
                // Last-resort: if we can't even open a log file, there's nothing useful to do
                // and we must not throw out of the Shim. Swallow.
            }
        }
    }
}
