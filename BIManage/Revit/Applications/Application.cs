using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.UI;
using ExternalEvent = Autodesk.Revit.UI.ExternalEvent;
using BIManage.Revit.BackgroundSync;
using BIManage.Core.Features;
using BIManage.Core.Metrics;
using BIManage.Core.Rules;
using BIManage.Revit.PinProtection;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.DependencyInjection;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Context;
using BIManage.Revit.Diagnostics;
using BIManage.Revit.EventRegistry;
using BIManage.Revit.Execution;
using BIManage.Revit.Helpers;
using BIManage.Revit.Protection;
// using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Threading;
using BIManage.Revit.Commands.Bindings;
using BIManage.Revit.Crash;
using BIManage.Licensing;
using BIManageRevit.Commands;
using Nice3point.Revit.Toolkit.External;
using RibbonVisibilityManager = BIManage.Revit.Applications.RibbonVisibilityManager;
using UserRole = BIManage.Core.Identity.UserRole;

namespace BIManageRevit.BIManage.Revit.Applications
{
    /// <summary>
    ///     Application entry point - BIManage Add-in
    ///     Handles OnStartup/OnShutdown lifecycle and initializes core services
    /// </summary>
    public class Application : ExternalApplication
    {
        private static Application? _instance;

        /// <summary>
        ///     Static constructor to register assembly resolvers before any other code runs.
        ///     This prevents load failures when dependencies are in a subfolder.
        ///
        ///     Phase C.1 POC (2026-05-14): Skip ALL work if this copy of the assembly is being
        ///     loaded into the Default ALC on R25. Background: Revit 2025 loads our DLL twice
        ///     (Default ALC + custom "BIManageRevit" ALC) — the BIManageRevit ALC copy is the
        ///     one Revit instantiates and calls OnStartup on. The Default ALC copy is loaded
        ///     by Revit's manifest scanner and stays inert EXCEPT for whatever its static
        ///     initializers run. Today that includes RegisterManagedAssemblyResolvers, which
        ///     installs an AppDomain-wide AssemblyResolve handler from the inert ALC — those
        ///     handlers can later resolve assemblies into the wrong context. Bailing out of
        ///     the static ctor when running in Default ALC keeps the inert copy fully inert.
        ///
        ///     Hypothesis being tested: this reduces the native "Cannot find host add-in"
        ///     crash class observed on R25 by ensuring no Revit-event subscription, no
        ///     resolver, no anything is registered from the inert ALC's perspective.
        ///
        ///     The check is gated on net8.0+ because AssemblyLoadContext doesn't exist on
        ///     net48 (R21-R24), where the dual-load is physically impossible anyway.
        ///     If this guard fails the POC test on R25 (no improvement), we move to C.2.
        /// </summary>
        static Application()
        {
            _earlyDiagnostics.Add($"[Timing] static-ctor entered t={_startupStopwatch.ElapsedMilliseconds}ms");
#if NET8_0_OR_GREATER
            try
            {
                var thisAsm = typeof(Application).Assembly;
                var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(thisAsm);
                if (alc != null && alc == System.Runtime.Loader.AssemblyLoadContext.Default)
                {
                    // We're the inert Default-ALC copy. Skip all initialization. The live
                    // copy (in the custom "BIManageRevit" ALC) will run its own static ctor
                    // independently and do the real work.
                    _earlyDiagnostics.Add($"[AsmIdentity:DefaultALC-Guard] Static ctor skipped — running in Default ALC (inert dead-ALC copy on R25). HashCode={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(thisAsm)}");
                    _earlyDiagnostics.Add($"[Timing] static-ctor exit (Default-ALC inert) t={_startupStopwatch.ElapsedMilliseconds}ms");
                    return;
                }
            }
            catch
            {
                // Don't let the guard itself crash startup. Fall through to normal init.
            }
#endif
            _earlyDiagnostics.Add($"[Timing] before RegisterManagedAssemblyResolvers t={_startupStopwatch.ElapsedMilliseconds}ms");
            RegisterManagedAssemblyResolvers();
            _earlyDiagnostics.Add($"[Timing] after RegisterManagedAssemblyResolvers t={_startupStopwatch.ElapsedMilliseconds}ms");
        }
        private static readonly Stopwatch _startupStopwatch = Stopwatch.StartNew();
        private static readonly System.Collections.Generic.List<string> _earlyDiagnostics = new System.Collections.Generic.List<string>();
        private static readonly HashSet<string> _selfAddinKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BIManage", "ZeManage", "BIManageRevit", "ZeManageRevit"
        };
        private ServiceRegistry? _services;
        private IFeatureToggleService? _featureToggleService;
        private ExternalEvent? _setupProtectionBindingsEvent;
        private ExternalEvent? _analyzeModelEvent;
        private AnalyzeModelExternalEventHandler? _analyzeModelHandler;
        private ExternalEvent? _triggerSyncEvent;
        private global::BIManage.Revit.SyncTrafficControl.TriggerSyncExternalEventHandler? _triggerSyncHandler;

        // Cloud-gate deferred local-save (Phase 6). When RequestSyncSlot is denied
        // for a cloud doc, we e.Cancel() the sync (so the central is untouched) and
        // raise this ExternalEvent so doc.Save() can run on Revit's next Idle —
        // outside the sync-event handler's "Save is temporarily disabled" context.
        // Honors the "save before queue at any cost, local-only" constraint without
        // letting Revit's STC sequence attempt a cloud upload.
        private ExternalEvent? _localSaveBeforeQueueEvent;
        private global::BIManage.Revit.SyncTrafficControl.LocalSaveBeforeQueueHandler? _localSaveBeforeQueueHandler;
        private PeriodicMetricsSnapshotService? _periodicMetricsService;
        private RibbonVisibilityManager? _ribbonVisibilityManager;

        // Theme-aware ribbon icon management
        private readonly System.Collections.Generic.List<(PushButton Button, string IconName)> _ribbonButtons = new System.Collections.Generic.List<(PushButton, string)>();
        private bool _isDarkTheme;

        // Background sync engine (ExternalEvents created in OnStartup, engine started in OnApplicationInitialized)
        private ExternalEvent? _backgroundSyncEvent;
        private BackgroundSyncExternalEventHandler? _backgroundSyncHandler;
        private ExternalEvent? _backgroundRelinquishEvent;
        private BackgroundRelinquishExternalEventHandler? _backgroundRelinquishHandler;

        // CAD Explode ribbon button swap — replaces native ribbon Explode buttons with shadow
        // versions so Ze_CADExplodeProtection can intercept clicks. Optional: if AdWindows isn't
        // reachable on the running Revit version, the swap silently no-ops and the post-action
        // handler keeps audit working.
        private global::BIManage.Revit.RibbonInterception.CADExplodeRibbonSwap? _cadExplodeRibbonSwap;

        /// <summary>
        /// Re-evaluate the CAD Explode ribbon button visibility against the current
        /// Ze_CADExplodeProtection enabled state. Called from the SignalR ProtectionChange
        /// listener after an admin toggles the protection in the cloud panel so the ribbon UI
        /// reflects the new state without a Revit restart. No-op if the swap was never
        /// initialized (e.g. AdWindows unavailable).
        /// </summary>
        public void RefreshCADExplodeSwapState()
        {
            try { _cadExplodeRibbonSwap?.RefreshSwapState(); }
            catch (Exception ex) { Logger?.LogWarning($"RefreshCADExplodeSwapState: {ex.Message}"); }
        }
        private ExternalEvent? _autoExitEvent;
        private AutoExitExternalEventHandler? _autoExitHandler;
        private BackgroundSyncEngine? _backgroundSyncEngine;
#if REVIT2027
        private ZeManageIntegration? _zeManageIntegration;
#endif

        // Defers Document.Close() to the next Revit idle tick. Required because closing
        // a document from INSIDE Revit's DocumentOpened event handler silently fails
        // (Revit still holds locks on the just-opened doc). Used by event protections
        // that need to refuse access after the open: Ze_OpenCentralFileProtection and
        // Ze_DuplicateUserSessionProtection. Exposed via TryCloseDocumentDeferred()
        // so any code path can request a close without touching the handler directly.
        private ExternalEvent? _closeDocumentEvent;
        private global::BIManage.Revit.Protection.CloseDocumentExternalEventHandler? _closeDocumentHandler;

        /// <summary>
        /// Queue a document for close on the next Revit idle tick. Returns true if the
        /// request was queued, false if the close infrastructure isn't ready yet
        /// (caller can fall back to inline doc.Close which may or may not work).
        /// </summary>
        public bool TryCloseDocumentDeferred(Autodesk.Revit.DB.Document doc, string reason)
        {
            if (doc == null) return false;
            if (_closeDocumentHandler == null || _closeDocumentEvent == null)
            {
                Logger?.LogWarning("[CloseDocument] Deferred close requested but infrastructure not ready");
                return false;
            }
            _closeDocumentHandler.RequestClose(doc, reason);
            return true;
        }

        /// <summary>
        ///     Singleton instance of the application
        /// </summary>
        public static Application? Instance => _instance;

        /// <summary>
        ///     Service registry for dependency resolution
        /// </summary>
        public ServiceRegistry? Services => _services;

        /// <summary>
        ///     Logger instance for application-wide logging
        /// </summary>
        public ILogger? Logger => _services?.GetService<ILogger>();

        /// <summary>
        ///     Feature toggle service for enabling/disabling features
        /// </summary>
        public IFeatureToggleService? FeatureToggleService => _featureToggleService ?? _services?.GetService<IFeatureToggleService>();

        /// <summary>
        ///     Event registry service for managing Revit event subscriptions
        /// </summary>
        public EventRegistryService? EventRegistryService => _services?.GetService<IEventRegistry>()?.LegacyRegistry;

        /// <summary>
        ///     External event orchestrator for safe Revit API calls
        /// </summary>
        public IExternalEventOrchestrator? ExternalEventOrchestrator => _services?.GetService<IExternalEventOrchestrator>();

        /// <summary>
        ///     Raises the background model analysis external event.
        ///     Returns true if the event was raised successfully.
        /// </summary>
        public bool RaiseAnalyzeModelEvent()
        {
            if (_analyzeModelEvent == null || _analyzeModelHandler == null)
                return false;

            _analyzeModelHandler.IsRequested = true;
            return _analyzeModelEvent.Raise() == ExternalEventRequest.Accepted;
        }

        /// <summary>
        ///     Cross-ALC entry point for IExternalCommand re-dispatch. Used by
        ///     <see cref="BIManage.Common.Helpers.CrossAlcDispatch"/> via reflection
        ///     when a command Execute lands in a non-live ALC on Revit 2025+.
        ///
        ///     Instantiates the named command type from THIS Application's assembly
        ///     (the live ALC's BIManageRevit.dll) and runs Execute locally — so the
        ///     command observes a non-null Application.Instance and a populated
        ///     ServiceRegistry. The Revit API types in the signature are all from
        ///     RevitAPIUI which is shared across ALCs, so reflection.Invoke
        ///     across the ALC boundary is safe.
        /// </summary>
        public Autodesk.Revit.UI.Result RunCommandByTypeName(
            string commandTypeFullName,
            Autodesk.Revit.UI.ExternalCommandData commandData,
            Autodesk.Revit.DB.ElementSet elements,
            ref string message)
        {
            if (string.IsNullOrEmpty(commandTypeFullName))
            {
                message = "RunCommandByTypeName: empty type name";
                return Autodesk.Revit.UI.Result.Failed;
            }

            var asm = typeof(Application).Assembly;
            var cmdType = asm.GetType(commandTypeFullName, throwOnError: false);
            if (cmdType == null)
            {
                message = $"RunCommandByTypeName: type not found in live ALC: {commandTypeFullName}";
                return Autodesk.Revit.UI.Result.Failed;
            }

            object? cmdInstance;
            try { cmdInstance = Activator.CreateInstance(cmdType); }
            catch (Exception ex)
            {
                message = $"RunCommandByTypeName: instantiation failed for {commandTypeFullName}: {ex.Message}";
                return Autodesk.Revit.UI.Result.Failed;
            }

            if (cmdInstance is not Autodesk.Revit.UI.IExternalCommand cmd)
            {
                message = $"RunCommandByTypeName: {commandTypeFullName} does not implement IExternalCommand";
                return Autodesk.Revit.UI.Result.Failed;
            }

            return cmd.Execute(commandData, ref message, elements);
        }

        /// <summary>
        ///     Called when Revit starts up and loads the add-in
        /// </summary>
        public override void OnStartup()
        {
            try
            {
                _instance = this;
                _earlyDiagnostics.Add($"[Timing] OnStartup entered t={_startupStopwatch.ElapsedMilliseconds}ms");

                // [Fix-1a diagnostic] Anchor log identifying which ALC owns the live
                // Application._instance for this session. Compared against subscription-time
                // ALC values logged from CommandInterceptionService / CommandProtectionBinding
                // to locate any subscription registered from a non-live ALC (the suspected
                // cause of AddInManager's "Cannot find the host add-in" warning).
#if NET8_0_OR_GREATER
                try
                {
                    var thisAsm = typeof(Application).Assembly;
                    var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(thisAsm);
                    var asmHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(thisAsm);
                    string addInId = "unknown";
                    try { addInId = base.Application.ActiveAddInId?.GetGUID().ToString() ?? "unknown"; }
                    catch { /* ActiveAddInId may not be available at this point */ }
                    _earlyDiagnostics.Add(
                        $"[AsmIdentity:OnStartup] LiveALC={alc?.Name ?? "Default"}, instanceHash={asmHash}, AddInId={addInId}");
                }
                catch (Exception anchorEx)
                {
                    _earlyDiagnostics.Add($"[AsmIdentity:OnStartup] anchor log failed: {anchorEx.Message}");
                }
#endif

                // CRASH GUARD — must be the FIRST runtime hook installed. Catches unhandled
                // exceptions on background threads (AppDomain), unobserved Tasks, and WPF
                // dispatcher dispatch failures. Without this, ANY plugin-side exception
                // outside an IExternalCommand.Execute can take down Revit and the user's
                // unsaved work. Re-initialised later in this method (after Logger and
                // services are available) to wire up the help-ticket pre-fill providers.
                global::BIManage.Infrastructure.Diagnostics.CrashGuard.Initialize(
                    logger: null,
                    revitMainWindowHandle: IntPtr.Zero);

#if REVIT2027
                // .NET 10 disables BinaryFormatter by default. WPF uses it internally for BAML.
                AppContext.SetSwitch("System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization", true);
#endif

                // Pin WPF pack URI resolution to THIS copy of BIManageRevit.
                // Revit 2025/2026 inherently loads our DLL twice (Default ALC scanner + isolated
                // "BIManageRevit" ALC instantiator). When a XAML dialog calls InitializeComponent,
                // WPF's resource lookup iterates AppDomain.CurrentDomain.GetAssemblies() and picks
                // an assembly by simple-name match — with two matches, it can pick the wrong one
                // and throw "does not have a resource identified by the URI". Application.ResourceAssembly
                // is a static property in PresentationFramework.dll (single instance across all ALCs)
                // that overrides this auto-discovery and forces WPF to use the assembly we specify.
                // Setting it from OnStartup (running in the instantiator copy that has live state)
                // makes ALL subsequent pack URI lookups go to THIS copy, where the BAML resources
                // are reachable from the same Type identity that owns the dialog instance.
                // Set ResourceAssembly — first attempt. May fail if WPF's Application
                // hasn't been initialised yet by Revit at this point in the addin load
                // sequence (observed on R25 with the Shim path). We retry in
                // OnApplicationInitialized below as a second-chance.
                _earlyDiagnostics.Add(TrySetResourceAssembly("OnStartup"));

                // Pre-load SQLite native DLL FIRST, before any managed assembly loading.
                // Must happen before RegisterManagedAssemblyResolvers() which loads all DLLs
                // including System.Data.SQLite.dll — that assembly may trigger P/Invoke calls
                // to SQLite.Interop.dll via static initializers.
                _earlyDiagnostics.Add($"[Timing] before EnsureNativeDependencies t={_startupStopwatch.ElapsedMilliseconds}ms");
                EnsureNativeDependencies();
                _earlyDiagnostics.Add($"[Timing] after EnsureNativeDependencies t={_startupStopwatch.ElapsedMilliseconds}ms");

                // Redirect managed assembly resolution before any SignalR types load.
                // NOTE: Handled by static constructor for earlier execution.

                _earlyDiagnostics.Add($"[Timing] before bootstrapper.Initialize t={_startupStopwatch.ElapsedMilliseconds}ms");
                var bootstrapper = new global::BIManage.Revit.Applications.RevitBootstrapper();
                var initResult = bootstrapper.Initialize(global::BIManage.Revit.Applications.InitContext.CreateDefault(base.Application.ControlledApplication));
                _earlyDiagnostics.Add($"[Timing] after bootstrapper.Initialize t={_startupStopwatch.ElapsedMilliseconds}ms");

                if (!initResult.IsSuccessful)
                {
                    var message = initResult.ErrorMessage ?? "Unknown bootstrap failure";

                    TaskDialog.Show("ZeManage",
                        $"Failed to initialize ZeManage Add-in.\n\nError: {message}\n\nPlease check the log file for details.");
                    return;
                }

                _services = initResult.Services;

                // Cache FeatureToggleService for early access (before full service resolution)
                _featureToggleService = _services?.GetService<IFeatureToggleService>();
                Logger?.LogInfo($"FeatureToggleService cached: {(_featureToggleService != null ? "Available" : "NULL")}");

                // CRASH GUARD — re-initialise now that Logger and services are ready so the
                // help-ticket pre-fill has the proper context (current model, session, auth
                // client) when an exception fires. The handlers themselves are already wired
                // from the early Initialize call at the top of OnStartup. UIApplication is
                // not yet available at this point (it lands in OnApplicationInitialized via
                // IUIApplicationProvider) so the providers below resolve everything LAZILY
                // through that provider — by the time a crash fires, the provider's been set.
                try
                {
                    global::BIManage.Infrastructure.Diagnostics.CrashGuard.Initialize(
                        logger: Logger,
                        revitMainWindowHandle: base.Application?.MainWindowHandle ?? IntPtr.Zero,
                        // Revit version/build/username are read lazily via the provider below.
                        // We don't capture them here because UIApplication isn't created yet.
                        sessionIdProvider: () =>
                        {
                            try { return _services?.GetService<global::BIManage.Revit.Context.IRevitContext>()?.SessionId.ToString(); }
                            catch { return null; }
                        },
                        modelNameProvider: () =>
                        {
                            try
                            {
                                var uiApp = _services?.GetService<global::BIManage.Revit.Context.IUIApplicationProvider>()?.UIApplication;
                                return uiApp?.ActiveUIDocument?.Document?.Title;
                            }
                            catch { return null; }
                        },
                        modelPathProvider: () =>
                        {
                            try
                            {
                                var uiApp = _services?.GetService<global::BIManage.Revit.Context.IUIApplicationProvider>()?.UIApplication;
                                return uiApp?.ActiveUIDocument?.Document?.PathName;
                            }
                            catch { return null; }
                        },
                        httpClientProvider: () =>
                        {
                            try { return _services?.GetService<global::BIManage.Infrastructure.Auth.AuthenticatedHttpClient>(); }
                            catch { return null; }
                        });
                }
                catch (Exception cgEx)
                {
                    Logger?.LogWarning($"[CrashGuard] Late-init failed (handlers still active from early init): {cgEx.Message}");
                }

                // Flush early diagnostics collected before Logger was available
                foreach (var msg in _earlyDiagnostics)
                    Logger?.LogInfo(msg);
                _earlyDiagnostics.Clear();

                // Track dialog time during startup for accurate opening duration
                base.Application.DialogBoxShowing += OnStartupDialogBoxShowing;

                // Hook ApplicationInitialized to set UIApplication when available
                base.Application.ControlledApplication.ApplicationInitialized += OnApplicationInitialized;

                // Hook DocumentOpened for protection activation
                base.Application.ControlledApplication.DocumentOpened += OnDocumentOpened;

                // Hook DocumentCreated for NEW documents
                base.Application.ControlledApplication.DocumentCreated += OnDocumentCreated;

                // Hook DocumentClosing to leave SignalR model groups
                base.Application.ControlledApplication.DocumentClosing += OnDocumentClosing;

                // Hook DocumentSynchronizedWithCentral so cloud models that failed to
                // resolve a stable modelGuid at open-time (cloud-API not yet ready) can
                // be re-checked once Autodesk Cloud has populated the URN-based GUID.
                // Without this retry, the first user to open a cloud model right after
                // it's added to Autodesk Docs gets a permanent "no modelGuid" state
                // until they reopen Revit. See ModelGuidHelper.GetModelGuid cloud branch.
                base.Application.ControlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynchronizedRetryRegistration;

                // Create ExternalEvents for deferred initialization
                CreateExternalEvents();

                // Wire SyncTrafficControl SyncRequest → SyncReadyDialog → TriggerSync
                WireSyncTrafficControlEvents();

                // Detect Revit's current ribbon theme BEFORE building the ribbon so each
                // PushButton is created with icons from the right folder (Icons vs IconsWhite).
                // Without this the buttons are constructed assuming light, then never refreshed
                // because UpdateAllButtonIcons used to be unreachable — Revit Dark users saw
                // light icons until restart. Pre-R24 has no UIThemeManager so we always default
                // to light there (matches Revit's own behaviour on those versions).
#if REVIT2024_OR_GREATER
                _isDarkTheme = UIThemeManager.CurrentTheme == UITheme.Dark;
#endif

                // Create ribbon UI
                Logger?.LogInfo($"[Timing] before CreateRibbon t={_startupStopwatch.ElapsedMilliseconds}ms");
                CreateRibbon();
                Logger?.LogInfo($"[Timing] after CreateRibbon t={_startupStopwatch.ElapsedMilliseconds}ms");

#if REVIT2024_OR_GREATER
                // Subscribe to live theme switches (user toggles UI theme mid-session). The
                // event fires for both UI and canvas changes — we only care about UI/ribbon.
                try
                {
                    this.Application.ThemeChanged += OnRevitThemeChanged;
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"Failed to subscribe to ThemeChanged: {ex.Message}");
                }
#endif

                Logger?.LogInfo("BIManageRevit Add-in started successfully");
                Logger?.LogInfo($"[Timing] OnStartup returning t={_startupStopwatch.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                // Log error and show user-friendly message
                Logger?.LogError($"Failed to start BIManageRevit Add-in: {ex.Message}", ex);

                TaskDialog.Show("ZeManage",
                    $"Failed to initialize ZeManage Add-in.\n\nError: {ex.Message}\n\nPlease check the log file for details.");
            }
        }

        /// <summary>
        ///     Pin WPF's pack-URI resource resolution to THIS copy of BIManageRevit.
        ///     Revit 2025+ loads the Core into a custom <c>AssemblyLoadContext</c> via the
        ///     Shim. WPF's <c>PresentationFramework</c> lives in the Default ALC and its
        ///     <c>Application.LoadComponent(this, uri)</c> uses default-ALC binding to find
        ///     <c>BIManageRevit, V1.0.0.0</c> — which fails because the Core is in the custom
        ///     ALC and invisible to Default-ALC binding. Setting
        ///     <c>System.Windows.Application.ResourceAssembly</c> to the Core's
        ///     <see cref="Assembly"/> instance directly bypasses that binding — WPF holds a
        ///     reference to the exact assembly object regardless of ALC.
        ///     <para>
        ///     The setter can throw if <c>Application.Current</c> isn't yet initialised by
        ///     Revit's WPF host. The Shim's <c>OnStartup</c> runs early in Revit's add-in
        ///     load sequence (BEFORE Revit creates its WPF UI), so the first attempt from
        ///     <c>OnStartup</c> sometimes silently fails. Retrying from
        ///     <c>OnApplicationInitialized</c> (when WPF is guaranteed up) catches that case.
        ///     </para>
        ///     Returns a single diagnostic line (success or failure) for the
        ///     <c>_earlyDiagnostics</c> buffer / <c>Logger</c>.
        /// </summary>
        private static string TrySetResourceAssembly(string callSite)
        {
            try
            {
                var asm = typeof(Application).Assembly;
                System.Windows.Application.ResourceAssembly = asm;
                // Verify the assignment took (it can be ignored without throwing in some
                // hosted-WPF scenarios). Read it back and compare reference identity.
                var actual = System.Windows.Application.ResourceAssembly;
                if (ReferenceEquals(actual, asm))
                    return $"[ResourceAssembly] SET from {callSite}: {asm.GetName().Name} v{asm.GetName().Version}";
                return $"[ResourceAssembly] WARN from {callSite}: setter accepted but readback returned {(actual == null ? "<null>" : actual.GetName().Name)} — WPF pack URI lookups may still fail";
            }
            catch (Exception ex)
            {
                return $"[ResourceAssembly] FAILED from {callSite}: {ex.GetType().Name}: {ex.Message} — WPF pack URI lookups WILL fail until retried";
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        /// <summary>
        ///     Pre-loads all add-in DLLs and registers an AppDomain AssemblyResolve handler
        ///     that redirects any version request to the loaded instance.  Required because
        ///     Revit.exe owns the binding-redirect config (Revit.exe.config) and our
        ///     BIManageRevit.dll.config binding redirects are ignored for assembly resolution.
        ///
        ///     NOTE: SignalR no longer uses Microsoft.AspNetCore.SignalR.Client — it uses a raw
        ///     WebSocket implementation, so there are no Microsoft.Extensions.* assembly conflicts.
        /// </summary>
        private static void RegisterManagedAssemblyResolvers()
        {
            var addinDir = Path.GetDirectoryName(typeof(Application).Assembly.Location) ?? string.Empty;
            if (string.IsNullOrEmpty(addinDir)) return;

            var libDir = Path.Combine(addinDir, "lib");

            _earlyDiagnostics.Add($"[AsmResolver] addinDir={addinDir}");
            _earlyDiagnostics.Add($"[AsmResolver] libDir={libDir} (exists: {Directory.Exists(libDir)})");

            // Register AssemblyResolve handler FIRST — catches:
            //  - Version mismatches (e.g., 6.0.0.0 requested but 6.0.1.0 deployed)
            //  - On-demand lib/ probing for private deps (Newtonsoft.Json) that we INTENTIONALLY
            //    do NOT pre-load to avoid type-identity conflicts with Revit's own copy.
            // Cache our own assembly identity once for the short-circuit below.
            var selfAssembly = typeof(Application).Assembly;
            var selfName = selfAssembly.GetName().Name;
            // Also short-circuit for the partner assembly if it loaded alongside us.
            const string addonsName = "BIManage.Addons";

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var requestedName = new System.Reflection.AssemblyName(args.Name);

                // CRITICAL: short-circuit for OUR own assemblies. If anyone (Revit's add-in
                // loader, WPF's BAML reader, type-resolution by FullClassName, etc.) asks
                // the AppDomain to resolve "BIManageRevit" or "BIManage.Addons", we MUST
                // return the existing instance — never call Assembly.LoadFrom on our own
                // DLL. On Revit 2025+ with .NET 8, our DLL may be in a collectible ALC
                // where .Location is empty or doesn't match the addinDir filter, in which
                // case the loop below would fall through to step (2)'s LoadFrom and produce
                // a SECOND copy in the default ALC. Two copies = WPF pack URI lookup picks
                // the wrong one (XAML "does not have a resource at URI"), and
                // Application.Instance is null on the wrong copy ("Unable to access
                // application services"). Returning the executing copy here keeps both
                // resolution paths pointing at the same Type identity.
                if (string.Equals(requestedName.Name, selfName, StringComparison.OrdinalIgnoreCase))
                    return selfAssembly;

                if (string.Equals(requestedName.Name, addonsName, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var existing in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            if (string.Equals(existing.GetName().Name, addonsName, StringComparison.OrdinalIgnoreCase))
                                return existing;
                        }
                        catch { }
                    }
                    // Not yet loaded — fall through to the on-disk loader below.
                }

                // 1) Check already-loaded assemblies — match by name (ignore version)
                System.Reflection.Assembly fallback = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!string.Equals(a.GetName().Name, requestedName.Name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        var loc = a.Location;
                        if (!string.IsNullOrEmpty(loc) &&
                            loc.StartsWith(addinDir, StringComparison.OrdinalIgnoreCase))
                        {
                            return a;
                        }
                    }
                    catch { }

                    if (fallback == null) fallback = a;
                }

                // 2) Try loading from add-in directory (flat deployment)
                var dllPath = Path.Combine(addinDir, requestedName.Name + ".dll");
                if (File.Exists(dllPath))
                {
                    try
                    {
                        return System.Reflection.Assembly.LoadFrom(dllPath);
                    }
                    catch { }
                }

                // 3) Try loading from lib/ subfolder (private deps that conflict with Revit's own copies)
                //    Only triggers when OUR code references these types — Revit's own code uses its own copy.
                if (Directory.Exists(libDir))
                {
                    var libDllPath = Path.Combine(libDir, requestedName.Name + ".dll");
                    if (File.Exists(libDllPath))
                    {
                        try
                        {
                            _earlyDiagnostics.Add($"[AsmResolver] lib/ resolved: {requestedName.Name}");
                            return System.Reflection.Assembly.LoadFrom(libDllPath);
                        }
                        catch { }
                    }
                }

                // 4) Fall back to any loaded version as last resort
                if (fallback != null)
                    return fallback;

                _earlyDiagnostics.Add($"[AsmResolver] UNRESOLVED: {args.Name}");
                return null;
            };

            _earlyDiagnostics.Add("[AsmResolver] AssemblyResolve handler registered");
            _earlyDiagnostics.Add($"[Timing] AssemblyResolve handler registered t={_startupStopwatch.ElapsedMilliseconds}ms");

            // Pre-load was previously eager: a foreach over Directory.GetFiles(addinDir,"*.dll")
            // that called Assembly.LoadFrom on each flat-deployed DLL except the three skipped
            // below. Timing data captured on 2026-05-19 showed this loop costing ~1.3s on the
            // UI thread (worse on AV-active machines) for zero correctness benefit — the
            // AssemblyResolve handler registered above already faults each DLL in on demand the
            // first time a type from it is referenced.
            //
            // CRITICAL — the dual-ALC skip list (kept here as historical context):
            //   BIManageRevit.dll itself AND BIManage.Addons.dll MUST NEVER be Assembly.LoadFrom'd
            //   from this method on Revit 2025+. Revit's add-in loader puts our add-in into a
            //   *collectible* AssemblyLoadContext; a LoadFrom call from here uses the *default*
            //   ALC and would produce a SECOND copy of the assembly (separate static state →
            //   Application.Instance null on the wrong copy → WPF pack-URI breakage). The
            //   AssemblyResolve handler above short-circuits requests for these two names to the
            //   already-loaded instances, so even if a downstream caller asks for them by name
            //   they go to the live ALC's copy. This is why removing the eager loop is safe:
            //   the handler enforces the same skip behaviour through name-based resolution.
            //
            // lib/ assemblies (e.g. Newtonsoft.Json) were always lazy-loaded via the handler so
            // Revit's own Newtonsoft.Json could occupy the default context for Cloud Collaboration.
            // That arrangement is unchanged.
            _earlyDiagnostics.Add("[AsmResolver] Pre-load deferred: 0 loaded (all DLLs now resolve on-demand via AssemblyResolve handler). BIManageRevit.dll + BIManage.Addons.dll + SQLite.Interop.dll still excluded from any future eager path by the handler short-circuit.");
            _earlyDiagnostics.Add($"[Timing] pre-load complete t={_startupStopwatch.ElapsedMilliseconds}ms totalLoadFromMs=0 count=0+0 (deferred)");

            // [Fix-5] Pre-warm OS file cache for our flat-deployed dep DLLs on a background
            // thread. We DO NOT call Assembly.LoadFrom here — that would re-introduce the
            // dual-ALC class of bugs the original eager pre-load (removed 2026-05-19) carried.
            // We only read the files as bytes so the OS page cache + AV/EDR cache are warm
            // when the CLR later loads them via the on-demand AssemblyResolve path. On a fast
            // SSD machine without active AV this is a few extra ms of background I/O that
            // nobody notices. On the slow-launch customer machine (~1.3 s per-DLL AV-scan
            // cost measured during Revit's doStartupWarnings), this transfers the per-DLL
            // scan time out of the UI thread (where the user perceives "Not Responding") and
            // into a background thread before the user clicks anything. Skip list mirrors the
            // AssemblyResolve handler's short-circuit so we never touch our own assemblies.
            // Capture addinDir into a local for the lambda (the field is per-instance/static
            // and we want the value at this point).
            var addinDirForWarmer = addinDir;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var skip = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "BIManageRevit.dll",
                        "BIManageRevit.Shim.dll",
                        "BIManage.Addons.dll",
                        "SQLite.Interop.dll",
                    };
                    var warmed = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    foreach (var dll in Directory.EnumerateFiles(addinDirForWarmer, "*.dll"))
                    {
                        var name = Path.GetFileName(dll);
                        if (skip.Contains(name)) continue;
                        try
                        {
                            // Discard the byte[] — we only want the OS to scan + cache the file.
                            _ = File.ReadAllBytes(dll);
                            warmed++;
                        }
                        catch { /* per-file failure is fine — try the next one */ }
                    }
                    sw.Stop();
                    _earlyDiagnostics.Add(
                        $"[AsmResolver] Background pre-warm complete: {warmed} files read into OS cache ({sw.ElapsedMilliseconds} ms, background thread)");
                }
                catch { /* never let the background warmer take down OnStartup */ }
            });
        }


        /// <summary>
        ///     Pre-loads native DLLs from the add-in directory so P/Invoke calls succeed.
        ///     Must be called before any managed wrapper (System.Data.SQLite) is used.
        /// </summary>
        private static void EnsureNativeDependencies()
        {
            var location = typeof(Application).Assembly.Location;
            var assemblyDir = string.IsNullOrEmpty(location)
                ? null
                : Path.GetDirectoryName(location);

            // Fallback: use CodeBase (URI) when Location is empty (shadow-copy scenarios)
            if (string.IsNullOrEmpty(assemblyDir))
            {
                try
                {
                    var codeBase = typeof(Application).Assembly.CodeBase;
                    if (!string.IsNullOrEmpty(codeBase))
                    {
                        var uri = new Uri(codeBase);
                        assemblyDir = Path.GetDirectoryName(uri.LocalPath);
                    }
                }
                catch
                {
                    // ignored
                }
            }

            if (string.IsNullOrEmpty(assemblyDir))
            {
                _earlyDiagnostics.Add("[SQLite] ERROR: Could not determine assembly directory");
                return;
            }

            _earlyDiagnostics.Add($"[SQLite] Assembly directory: {assemblyDir}");

            // Try multiple known locations for SQLite.Interop.dll
            var candidates = new[]
            {
                Path.Combine(assemblyDir, "SQLite.Interop.dll"),
                Path.Combine(assemblyDir, "runtimes", "win-x64", "native", "SQLite.Interop.dll"),
                Path.Combine(assemblyDir, "x64", "SQLite.Interop.dll")
            };

            foreach (var path in candidates)
            {
                var exists = File.Exists(path);
                if (exists)
                {
                    var handle = LoadLibrary(path);
                    if (handle != IntPtr.Zero)
                    {
                        _earlyDiagnostics.Add($"[SQLite] Loaded native DLL from: {path}");
                        return;
                    }
                    var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    _earlyDiagnostics.Add($"[SQLite] LoadLibrary FAILED for: {path} (Win32 error: {err})");
                }
                else
                {
                    _earlyDiagnostics.Add($"[SQLite] Not found: {path}");
                }
            }

            _earlyDiagnostics.Add("[SQLite] WARNING: Could not load SQLite.Interop.dll from any location");
        }

        /// <summary>
        ///     Create ExternalEvents for deferred initialization
        /// </summary>
        private void CreateExternalEvents()
        {
            try
            {
                // Create handler that will execute on Revit's main thread
                var handler = new global::BIManage.Revit.Applications.SetupProtectionBindingsExternalEventHandler(_services, Logger);
                _setupProtectionBindingsEvent = ExternalEvent.Create(handler);

                Logger?.LogInfo("Protection binding ExternalEvent created");

                // Create handler for background model metrics analysis
                _analyzeModelHandler = new AnalyzeModelExternalEventHandler(_services, Logger);
                _analyzeModelEvent = ExternalEvent.Create(_analyzeModelHandler);
                Logger?.LogInfo("AnalyzeModel ExternalEvent created for background analysis");

                // Create handler for triggering sync from queue (SyncTrafficControl)
                _triggerSyncHandler = new global::BIManage.Revit.SyncTrafficControl.TriggerSyncExternalEventHandler(Logger);
                _triggerSyncEvent = ExternalEvent.Create(_triggerSyncHandler);
                Logger?.LogInfo("TriggerSync ExternalEvent created for sync queue coordination");

                // Phase 6 — Create handler for cloud-gate deferred local save.
                // EventRegistryService raises this from the gate's Denied branch so
                // doc.Save() runs on the next Idle (outside the "Save is temporarily
                // disabled" sync-event context). See plan file Phase 6 for rationale.
                _localSaveBeforeQueueHandler = new global::BIManage.Revit.SyncTrafficControl.LocalSaveBeforeQueueHandler { Logger = Logger };
                _localSaveBeforeQueueEvent = ExternalEvent.Create(_localSaveBeforeQueueHandler);
                // Wire the event back into the handler so callers (EventRegistryService)
                // only need the handler ref — they call handler.RaiseFor(doc, uiApp).
                _localSaveBeforeQueueHandler.Event = _localSaveBeforeQueueEvent;
                // Register the handler in DI so EventRegistryService's lazy getter can
                // resolve it. (The event is owned by the handler; no need to register
                // it separately and risk collisions with other ExternalEvent singletons.)
                _services?.RegisterSingleton(_localSaveBeforeQueueHandler);
                Logger?.LogInfo("LocalSaveBeforeQueue ExternalEvent created for cloud-gate Denied path");

                // Create handlers for background sync engine (programmatic sync + relinquish).
                // SyncRepository is passed in so the handler can read the user's "Open Views"
                // sync settings (close-mode + max-open-views guard) at the moment of sync.
                _backgroundSyncHandler = new BackgroundSyncExternalEventHandler(Logger, _services?.GetService<SyncRepository>());
                _backgroundSyncEvent = ExternalEvent.Create(_backgroundSyncHandler);
                _backgroundRelinquishHandler = new BackgroundRelinquishExternalEventHandler(Logger);
                _backgroundRelinquishEvent = ExternalEvent.Create(_backgroundRelinquishHandler);
                _autoExitHandler = new AutoExitExternalEventHandler(Logger);
                _autoExitEvent = ExternalEvent.Create(_autoExitHandler);
                Logger?.LogInfo("Background sync/relinquish/auto-exit ExternalEvents created");

            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to create ExternalEvents: {ex.Message}", ex);
            }

            // Isolated from the block above so that a failure in any earlier handler cannot
            // prevent this one from being created. Ze_OpenCentralFileProtection and
            // Ze_DuplicateUserSessionProtection both depend on TryCloseDocumentDeferred()
            // to close the just-opened model on the next idle tick — if this handler is
            // null the only fallback is doc.Close(false) inline inside DocumentOpened,
            // which silently fails (Revit still holds the open lock at that point).
            try
            {
                _closeDocumentHandler = new global::BIManage.Revit.Protection.CloseDocumentExternalEventHandler(Logger);
                _closeDocumentEvent = ExternalEvent.Create(_closeDocumentHandler);
                _closeDocumentHandler.SetExternalEvent(_closeDocumentEvent);
                Logger?.LogInfo("CloseDocument ExternalEvent created (deferred close for blocked event protections)");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to create CloseDocument ExternalEvent — Cancel-closes will fall back to inline doc.Close (may silently fail): {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Wires SyncTrafficControlService.SyncRequestReceived to show SyncReadyDialog
        /// and trigger sync via ExternalEvent when user clicks "Sync Now".
        /// </summary>
        // Per-model dialog dedup: only one SyncReadyDialog at a time per model on this
        // machine. With the new "fire popup for all queued users" semantics, the server's
        // targeted SyncRequest can race with TryPromoteSelfIfNext's client-side fallback
        // and try to open two dialogs for the same model. This dictionary blocks the
        // duplicate; ConcurrentDictionary works fine here because all writes go through
        // the dispatcher thread but defensive locking is cheap.
        // Cloud docs whose modelGuid couldn't be resolved at open-time (cloud sync
        // metadata not yet populated). Keyed by Document.GetHashCode() since we can't
        // hold a Document reference safely across event boundaries. Value is the doc
        // title for diagnostic logging. Drained by OnDocumentSynchronizedRetryRegistration.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _pendingCloudRegistrations
            = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BIManageRevit.BIManage.Views.SyncTrafficControl.SyncReadyDialog> _openSyncReadyDialogs
            = new System.Collections.Concurrent.ConcurrentDictionary<string, BIManageRevit.BIManage.Views.SyncTrafficControl.SyncReadyDialog>(StringComparer.OrdinalIgnoreCase);

        private void WireSyncTrafficControlEvents()
        {
            try
            {
                var syncTrafficControl = _services?.GetService<global::BIManage.Revit.SyncTrafficControl.SyncTrafficControlService>();
                if (syncTrafficControl == null || _triggerSyncEvent == null || _triggerSyncHandler == null)
                {
                    Logger?.LogDebug("[SyncTrafficControl] Not available — skipping event wiring");
                    return;
                }

                syncTrafficControl.SyncRequestReceived += (modelGuid, modelName) =>
                {
                    try
                    {
                        // Background sync: auto-proceed without showing dialog — but ONLY when
                        // THIS model's sync is the one the engine is driving. Using the global
                        // flag here let a background sync of a different model swallow a real
                        // user's "Ready to Sync" popup for this model (turn auto-synced silently).
                        if (syncTrafficControl.IsBackgroundSyncActiveFor(modelGuid))
                        {
                            Logger?.LogInfo($"[SyncTrafficControl] Background sync auto-accepting turn for {modelGuid}");
                            syncTrafficControl.MarkPendingQueueSync(modelGuid);
                            _triggerSyncHandler.PendingModelGuid = modelGuid;
                            _triggerSyncEvent.Raise();
                            return;
                        }

                        // Per-model dialog dedup: if a Sync Now popup is already open for
                        // this model on this machine, skip the second open. Prevents the
                        // case where TryPromoteSelfIfNext fires the event AND a server-
                        // pushed SyncRequest also arrives — we don't want two stacked dialogs.
                        if (_openSyncReadyDialogs.ContainsKey(modelGuid))
                        {
                            Logger?.LogInfo($"[SyncTrafficControl] SyncReadyDialog already open for {modelGuid} — skipping duplicate");
                            return;
                        }

                        // Inject event bus + local session so the dialog auto-closes when
                        // a peer wins the first-clicker-wins race (their SyncStarting
                        // broadcast arrives → dialog detects it's not us → closes itself).
                        var eventBus = _services?.GetService<global::BIManage.Infrastructure.SignalR.Events.ISignalREventBus>();
                        string? localSessionId = null;
                        try { localSessionId = _services?.GetService<global::BIManage.Revit.Context.IRevitContext>()?.SessionId.ToString(); } catch { }

                        var dialog = new BIManageRevit.BIManage.Views.SyncTrafficControl.SyncReadyDialog(modelName, eventBus, modelGuid, localSessionId, Logger);
                        _openSyncReadyDialogs[modelGuid] = dialog;
                        global::BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);
                        try
                        {
                            dialog.ShowDialog();
                        }
                        finally
                        {
                            _openSyncReadyDialogs.TryRemove(modelGuid, out _);
                        }

                        if (dialog.SyncRequested)
                        {
                            Logger?.LogInfo($"[SyncTrafficControl] User accepted sync for {modelGuid} — raising ExternalEvent");
                            syncTrafficControl.MarkPendingQueueSync(modelGuid);
                            _triggerSyncHandler.PendingModelGuid = modelGuid;
                            _triggerSyncEvent.Raise();
                        }
                        else
                        {
                            Logger?.LogInfo($"[SyncTrafficControl] SyncReadyDialog closed without accept for {modelGuid} — closeReason='{dialog.CloseReason}', SyncRequested={dialog.SyncRequested}");
                            // Don't call LeaveQueue when the dialog auto-closed because a peer
                            // won the race — we should STAY in the queue waiting for the next
                            // turn. Only an explicit Cancel/Esc/timeout removes us. We can't
                            // distinguish those cases from SyncRequested=false alone without
                            // a new flag, but the simple heuristic: if _activeSyncers has a
                            // peer right now, a peer won the race → keep us in queue.
                            // Otherwise (no active syncer), user explicitly declined → leave.
                            var peerSyncing = syncTrafficControl.GetActiveSyncer(modelGuid);
                            if (peerSyncing != null && !string.Equals(peerSyncing.SessionId, localSessionId, StringComparison.OrdinalIgnoreCase))
                            {
                                Logger?.LogInfo($"[SyncTrafficControl] Peer {peerSyncing.Username} won the race — keeping us in queue, clearing popup guard so next turn re-opens dialog");
                                syncTrafficControl.ClearPopupGuard(modelGuid);
                            }
                            else
                            {
                                syncTrafficControl.LeaveQueue(modelGuid);
                                syncTrafficControl.ClearPopupGuard(modelGuid);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogError($"[SyncTrafficControl] Error handling SyncRequest: {ex.Message}", ex);
                    }
                };

                Logger?.LogInfo("[SyncTrafficControl] SyncRequest event wired to SyncReadyDialog + TriggerSync");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[SyncTrafficControl] Failed to wire events: {ex.Message}");
            }
        }

        /// <summary>
        ///     Called when Revit application is fully initialized and UIApplication is available
        ///     Note: The sender is an Application object (not UIApplication)
        /// </summary>
        private void OnStartupDialogBoxShowing(object sender, Autodesk.Revit.UI.Events.DialogBoxShowingEventArgs e)
        {
            global::BIManage.Revit.Timing.StartupDialogTracker.OnDialogShowing();
        }

        private void OnApplicationInitialized(object sender, Autodesk.Revit.DB.Events.ApplicationInitializedEventArgs e)
        {
            try
            {
                Logger?.LogInfo($"[Timing] OnApplicationInitialized entered t={_startupStopwatch.ElapsedMilliseconds}ms");

                // Second-chance ResourceAssembly assignment. The first attempt in OnStartup
                // (Application.cs:288) sometimes silently fails on Revit 2025 because
                // System.Windows.Application.Current isn't yet initialised when our addin
                // loads. By the time OnApplicationInitialized fires, Revit has fully
                // bootstrapped WPF — the setter is guaranteed to succeed here. Without
                // this, any WPF dialog from the Core's custom ALC throws:
                //   "The component '...' does not have a resource identified by the URI
                //    '/BIManageRevit;V1.0.0.0;component/...'"
                // on its very first open click (observed on R25 in
                // BIManageRevit_20260520_1506.log at 15:16:58).
                Logger?.LogInfo(TrySetResourceAssembly("OnApplicationInitialized"));

                // Stop dialog tracking — startup is complete
                global::BIManage.Revit.Timing.StartupDialogTracker.StopTracking();
                base.Application.DialogBoxShowing -= OnStartupDialogBoxShowing;
                var startupDialogTime = global::BIManage.Revit.Timing.StartupDialogTracker.TotalDialogSeconds;
                Logger?.LogDebug($">>> OnApplicationInitialized event fired (dialog time during startup: {startupDialogTime:F1}s)");

                // The sender is Autodesk.Revit.ApplicationServices.Application
                // We need to create UIApplication from it
                var revitApp = sender as Autodesk.Revit.ApplicationServices.Application;
                if (revitApp != null)
                {
                    Logger?.LogDebug(">>> Creating UIApplication from sender");

                    // Create UIApplication - there should be at least one active window
                    var uiApp = new UIApplication(revitApp);

                    // Cache the canonical Revit main HWND for all WPF dialog Owner assignments.
                    // Must run BEFORE any code that might show a dialog so SetOwner always has a
                    // valid handle. See RevitWindowHelper for the root-cause analysis (the WPF
                    // "Invalid window handle" failure that bypassed protection in field logs).
                    global::BIManage.Revit.Helpers.RevitWindowHelper.Initialize(uiApp, Logger);

                    // Store UIApplication in provider (allows lazy initialization)
                    var uiAppProvider = _services?.GetService<IUIApplicationProvider>();
                    uiAppProvider?.SetUIApplication(uiApp);

                    Logger?.LogInfo("UIApplication captured and stored in provider");

                    // Initialize idling event now that UIApplication is available
                    // (TryInitialize was called during bootstrap but UIApplication wasn't ready yet)
                    var idlingRegistry = _services?.GetService<IIdlingEventRegistry>();
                    idlingRegistry?.TryInitialize();

                    // Start the post-open dialog timing observer (Worksets / Manage Links).
                    // Subscribes to Application.DialogBoxShowing + UIApplication.Idling — pure
                    // observer, never affects command dispatch.
                    try
                    {
                        var dialogTimer = _services?.GetService<global::BIManage.Revit.Idling.PostOpenDialogTimingService>();
                        dialogTimer?.Start(uiApp);
                    }
                    catch (Exception dtEx)
                    {
                        Logger?.LogWarning($"Failed to start PostOpenDialogTimingService: {dtEx.Message}");
                    }

                    // Start the SyncConflict dialog interceptor — final safety net for the
                    // sync queue. If SignalR's SyncStarting event arrives too late (or never),
                    // Revit would otherwise show its native "Unable to Access the Model"
                    // dialog. This interceptor catches that dialog, dismisses it, and shows
                    // our SyncConflictDialog with a Join Queue option instead.
                    try
                    {
                        var syncInterceptor = new global::BIManage.Revit.SyncTrafficControl.SyncConflictDialogInterceptor(
                            uiApp,
                            Logger,
                            () => _services?.GetService<global::BIManage.Revit.SyncTrafficControl.SyncTrafficControlService>(),
                            () => _services?.GetService<global::BIManage.Revit.Context.IRevitContext>(),
                            () => _services?.GetService<global::BIManage.Infrastructure.SignalR.Events.ISignalREventBus>());
                        syncInterceptor.Start();
                        _services?.RegisterSingleton(syncInterceptor);
                    }
                    catch (Exception siEx)
                    {
                        Logger?.LogWarning($"Failed to start SyncConflictDialogInterceptor: {siEx.Message}");
                    }

                    // Disable Drag Elements on Selection (company policy)
                    try
                    {
                        var selectionOpts = Autodesk.Revit.UI.SelectionUIOptions.GetSelectionUIOptions();
                        selectionOpts.DragOnSelection = false;
                        Logger?.LogInfo("Drag Elements on Selection disabled");
                    }
                    catch (Exception selEx)
                    {
                        Logger?.LogWarning($"Failed to disable Drag on Selection: {selEx.Message}");
                    }

                    // Update session with revit_username and opening completion time (non-blocking)
                    Logger?.LogDebug(">>> Calling UpdateSessionOnRevitReady (async)");
                    TaskRunner.FireAndForget(
                        UpdateSessionOnRevitReady(uiApp),
                        Logger,
                        "UpdateSessionOnRevitReady");

                    // Start periodic metrics snapshot service
                    StartPeriodicMetricsService(uiApp);

                    // Refresh CrashGuard with Revit version/build/username now that UIApplication
                    // (and its underlying Autodesk.Revit.ApplicationServices.Application) is real.
                    // The handlers themselves are already wired from OnStartup; this call just
                    // backfills the static fields the help-ticket pre-fill reads.
                    try
                    {
                        global::BIManage.Infrastructure.Diagnostics.CrashGuard.Initialize(
                            logger: Logger,
                            revitMainWindowHandle: uiApp.MainWindowHandle,
                            revitVersion: revitApp.VersionNumber,
                            revitBuild: revitApp.VersionBuild,
                            revitUsername: revitApp.Username);
                    }
                    catch (Exception cgEx)
                    {
                        Logger?.LogDebug($"[CrashGuard] Version backfill skipped: {cgEx.Message}");
                    }

                    // Start background sync engine (needs UIApplication for document iteration)
                    StartBackgroundSyncEngine(uiApp);

#if REVIT2027
                    // Start ZeManage Desktop Agent (R27 only).
                    // Runs its own IHost with separate SQLite at %LocalAppData%\ZeManage\agent.db.
                    try
                    {
                        _zeManageIntegration = new ZeManageIntegration(Logger);
                        _zeManageIntegration.Start();
                    }
                    catch (Exception zeEx)
                    {
                        Logger?.LogWarning($"[ZeManageIntegration] Non-fatal start failure: {zeEx.Message}");
                    }
#endif

                    // Install the CAD Explode ribbon button swap. Wrapped in try/catch on top of
                    // the service's own internal try/catch so a hostile Revit-version mismatch
                    // (e.g. Autodesk renames AdWindows controls in a future build) cannot break
                    // the rest of init — protection enforcement degrades to post-action audit.
                    try
                    {
                        _cadExplodeRibbonSwap = new global::BIManage.Revit.RibbonInterception.CADExplodeRibbonSwap(
                            Logger,
                            eventProtectionGetter: () => _services?.GetService<IEventProtectionService>(),
                            interventionHandlerFactory: () => new EventInterventionHandler(
                                Logger,
                                _services?.GetService<global::BIManage.Data.SQLite.AuditRepository>(),
                                _services?.GetService<global::BIManage.Data.SQLite.OtpRepository>(),
                                () => _services?.GetService<global::BIManage.Core.Identity.IUserService>()?.HasAdminPrivileges ?? false),
                            auditRepoGetter: () => _services?.GetService<global::BIManage.Data.SQLite.AuditRepository>(),
                            registeredModelsRepoGetter: () => _services?.GetService<global::BIManage.Data.SQLite.RegisteredModelsRepository>());
                        _cadExplodeRibbonSwap.Initialize(uiApp);
                    }
                    catch (Exception swapEx)
                    {
                        Logger?.LogWarning($"CADExplodeRibbonSwap init failed (non-fatal): {swapEx.Message}");
                    }

                    // Diagnostic: one-shot first-Idling marker + settled marker.
                    // Pinpoints when Revit's UI thread first becomes idle after our OnStartup
                    // returns, and how long after that we observe a sustained 5s idle window.
                    // This is the truest measure of "user-perceived ready" — distinct from our
                    // self-reported Opening duration which only times our own OnStartup.
                    try
                    {
                        long firstIdleAtMs = -1;
                        bool settledLogged = false;
                        EventHandler<Autodesk.Revit.UI.Events.IdlingEventArgs> timingHandler = null!;
                        timingHandler = (s, args) =>
                        {
                            try
                            {
                                if (firstIdleAtMs < 0)
                                {
                                    firstIdleAtMs = _startupStopwatch.ElapsedMilliseconds;
                                    Logger?.LogInfo($"[Timing] First Idling event fired t={firstIdleAtMs}ms");
                                }
                                else if (!settledLogged && _startupStopwatch.ElapsedMilliseconds - firstIdleAtMs >= 5000)
                                {
                                    settledLogged = true;
                                    Logger?.LogInfo($"[Timing] Settled t={_startupStopwatch.ElapsedMilliseconds}ms — 5s of idle observed");
                                    uiApp.Idling -= timingHandler;
                                }
                            }
                            catch { /* never throw from an Idling handler */ }
                        };
                        uiApp.Idling += timingHandler;
                    }
                    catch (Exception timingEx)
                    {
                        Logger?.LogDebug($"[Timing] Failed to install first-Idling marker: {timingEx.Message}");
                    }

                    Logger?.LogInfo($"[Timing] OnApplicationInitialized handler done t={_startupStopwatch.ElapsedMilliseconds}ms");

                    // ❌ DO NOT ENABLE PROTECTION HERE - wait for DocumentOpened
                    // ❌ DO NOT REGISTER COMMAND BINDINGS HERE - wait for DocumentOpened
                }
                else
                {
                    Logger?.LogWarning("ApplicationInitialized event sender is not Application - UIApplication will be captured on first command execution");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in OnApplicationInitialized: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Update session when Revit is ready for user input (ApplicationInitialized)
        /// </summary>
        private async System.Threading.Tasks.Task UpdateSessionOnRevitReady(UIApplication uiApp)
        {
            try
            {
                Logger?.LogDebug(">>> UpdateSessionOnRevitReady: Start");

                var sessionRepository = _services?.GetService<SessionRepository>();
                var revitContext = _services?.GetService<IRevitContext>();

                Logger?.LogDebug($">>> SessionRepository: {(sessionRepository != null ? "Available" : "NULL")}");
                Logger?.LogDebug($">>> RevitContext: {(revitContext != null ? "Available" : "NULL")}");

                if (sessionRepository == null || revitContext == null)
                {
                    Logger?.LogWarning("SessionRepository or RevitContext not available - skipping session update");
                    return;
                }

                Logger?.LogDebug($">>> Current SessionId: {revitContext.SessionId}");

                // Get Revit username (now available via UIApplication)
                string revitUsername = null;
                try
                {
                    revitUsername = uiApp.Application.Username;
                    Logger?.LogDebug($">>> Retrieved Revit username: {revitUsername ?? "NULL"}");

                    // Update SignalR config with Revit username (replaces Environment.UserName fallback)
                    if (!string.IsNullOrEmpty(revitUsername))
                    {
                        var signalRConfig = _services?.GetService<global::BIManage.Infrastructure.SignalR.Core.SignalRConfiguration>();
                        if (signalRConfig != null)
                        {
                            signalRConfig.Username = revitUsername;
                            Logger?.LogDebug($"SignalR: Username updated to Revit username: {revitUsername}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"Could not get Revit username: {ex.Message}");
                }

                // Get number of loaded plugins (split into Stock Autodesk vs External/User-installed)
                int? pluginCount = null;
                int? autodeskAddins = null;
                int? externalAddins = null;
                List<string> externalAddinNamesList = null;
                try
                {
                    var (stockCount, externalCount, pyRevitExtensionCount, namesList) = CountLoadedAddins(uiApp);

                    // External count includes pyRevit extensions (counted individually)
                    int totalExternal = externalCount + pyRevitExtensionCount;

                    pluginCount = stockCount + totalExternal;
                    autodeskAddins = stockCount;
                    externalAddins = totalExternal;
                    externalAddinNamesList = namesList;

                    Logger?.LogDebug($">>> Counted {pluginCount} loaded plugins (Stock Autodesk: {stockCount}, External: {externalCount}, pyRevit Extensions: {pyRevitExtensionCount})");
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"Could not count loaded plugins: {ex.Message}");
                }

                string externalAddinNamesJson = null;
                if (externalAddinNamesList != null && externalAddinNamesList.Count > 0)
                    externalAddinNamesJson = System.Text.Json.JsonSerializer.Serialize(externalAddinNamesList);

                // Get journal file name
                string journalFileName = null;
                try
                {
                    journalFileName = uiApp.Application.RecordingJournalFilename;
                    Logger?.LogDebug($">>> Retrieved journal file: {journalFileName ?? "NULL"}");
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"Could not get journal file name: {ex.Message}");
                }

                // Update session with additional info
                Logger?.LogDebug($">>> Calling UpdateSessionReadyAsync with SessionId={revitContext.SessionId}, Username={revitUsername ?? "NULL"}, PluginCount={pluginCount?.ToString() ?? "NULL"}, AutodeskAddins={autodeskAddins?.ToString() ?? "NULL"}, ExternalAddins={externalAddins?.ToString() ?? "NULL"}, JournalFile={journalFileName ?? "NULL"}");

                var updateResult = await sessionRepository.UpdateSessionReadyAsync(
                    revitContext.SessionId.ToString(),
                    revitUsername,
                    pluginCount,
                    journalFileName,
                    autodeskAddins,
                    externalAddins,
                    externalAddinNamesJson);

                Logger?.LogDebug($">>> UpdateSessionReadyAsync returned: {updateResult}");

                // Update session with BIManage and Desktop Connector versions
                try
                {
                    // Read FileVersion (product release identifier) instead of AssemblyVersion.
                    // AssemblyVersion is pinned to 1.0.0.0 to keep WPF pack URIs stable across
                    // product-version bumps (see comment in BIManageRevit.csproj).
                    var bimanageVersion = System.Reflection.Assembly.GetExecutingAssembly()
                        .GetCustomAttribute<System.Reflection.AssemblyFileVersionAttribute>()?.Version
                        ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                        ?? "Unknown";
                    var desktopConnectorVersion = GetDesktopConnectorVersion();

                    var versionResult = await sessionRepository.UpdateSessionVersionsAsync(
                        revitContext.SessionId.ToString(),
                        bimanageVersion,
                        desktopConnectorVersion);

                    Logger?.LogDebug($">>> UpdateSessionVersionsAsync returned: {versionResult} (BIManage: {bimanageVersion}, Desktop Connector: {desktopConnectorVersion})");
                }
                catch (Exception versionEx)
                {
                    Logger?.LogWarning($"Failed to update session versions: {versionEx.Message}");
                }

                Logger?.LogInfo($"Session updated with Revit username: {revitUsername ?? "N/A"}, Plugin count: {pluginCount?.ToString() ?? "N/A"} (Autodesk: {autodeskAddins?.ToString() ?? "N/A"}, External: {externalAddins?.ToString() ?? "N/A"}), Journal file: {System.IO.Path.GetFileName(journalFileName) ?? "N/A"}");

                // Auto-sync session data to backend API (fire-and-forget to avoid blocking UI)
                var sessionSyncService = _services?.GetService<SessionSyncService>();
                if (sessionSyncService != null)
                {
                    var sessionIdToSync = revitContext.SessionId.ToString();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var syncResult = await sessionSyncService.SyncSessionAsync(sessionIdToSync);
                            Logger?.LogInfo($"Session API sync: {(syncResult ? "Success" : "Failed")}");
                        }
                        catch (Exception syncEx)
                        {
                            Logger?.LogWarning($"Session API sync failed (non-critical): {syncEx.Message}");
                        }
                    });
                }
                else
                {
                    Logger?.LogDebug("SessionSyncService not registered - skipping API sync");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to update session on Revit ready: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Count loaded add-ins by parsing .addin manifest files
        /// Distinguishes stock Autodesk add-ins from external/user-installed ones
        /// </summary>
        /// <returns>Tuple of (stockAutodeskCount, externalCount, pyRevitExtensionCount)</returns>
        private (int stockCount, int externalCount, int pyRevitExtensionCount, List<string> externalNames) CountLoadedAddins(UIApplication uiApp)
        {
            int stockCount = 0;
            int externalCount = 0;
            int pyRevitExtensionCount = 0;
            bool pyRevitLoaderFound = false;
            var allExternalNames = new List<string>();

            try
            {
                var revitVersion = uiApp.Application.VersionNumber;

                // Add-in folder locations
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                var appDataRoaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                // Stock add-ins folder (comes with Revit installation)
                var stockAddinFolder = System.IO.Path.Combine(programFiles, "Autodesk", $"Revit {revitVersion}", "AddIns");

                // External/user add-in folders
                var programDataAddinFolder = System.IO.Path.Combine(programData, "Autodesk", "Revit", "Addins", revitVersion);
                var userAddinFolder = System.IO.Path.Combine(appDataRoaming, "Autodesk", "Revit", "Addins", revitVersion);

                // Known stock Autodesk add-in names (whitelist for fallback)
                var stockAddinNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Collaboration for Revit", "C4R", "eTransmit", "FormIt Converter",
                    "Model Review", "Worksharing Monitor", "Revit Interoperability",
                    "IFC Exporter", "IFC", "Structural Analysis", "Energy Analysis",
                    "Insight", "Dynamo", "DynamoRevit", "Revit DB Link", "Site Designer",
                    "Structural Precast", "Steel Connections", "Fabrication MEP",
                    "Revit Server", "BIM 360", "Cloud Models", "Autodesk Rendering",
                    "A360 Collaboration", "Generative Design", "System Analysis",
                    "Model Checker", "Batch Print", "Shared Views", "PDF Export",
                    "Revit LT", "Revit Architecture", "Revit Structure", "Revit MEP"
                };

                // Known Autodesk vendor identifiers
                var autodeskVendors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "ADSK", "Autodesk", "Autodesk, Inc.", "Autodesk Inc"
                };

                // Track processed add-ins to avoid duplicates
                var processedAddins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Process stock add-in folder first
                if (System.IO.Directory.Exists(stockAddinFolder))
                {
                    var (stock, external, pyRevit, names) = ProcessAddinFolder(
                        stockAddinFolder, true, autodeskVendors, stockAddinNames, processedAddins);
                    stockCount += stock;
                    externalCount += external;
                    allExternalNames.AddRange(names);
                    if (pyRevit) pyRevitLoaderFound = true;
                }

                // Process ProgramData add-in folder (company/machine-level installs)
                if (System.IO.Directory.Exists(programDataAddinFolder))
                {
                    var (stock, external, pyRevit, names) = ProcessAddinFolder(
                        programDataAddinFolder, false, autodeskVendors, stockAddinNames, processedAddins);
                    stockCount += stock;
                    externalCount += external;
                    allExternalNames.AddRange(names);
                    if (pyRevit) pyRevitLoaderFound = true;
                }

                // Process user AppData add-in folder (user-level installs)
                if (System.IO.Directory.Exists(userAddinFolder))
                {
                    var (stock, external, pyRevit, names) = ProcessAddinFolder(
                        userAddinFolder, false, autodeskVendors, stockAddinNames, processedAddins);
                    stockCount += stock;
                    externalCount += external;
                    allExternalNames.AddRange(names);
                    if (pyRevit) pyRevitLoaderFound = true;
                }

                Logger?.LogInfo($"Add-in manifest scan complete: Stock={stockCount}, External={externalCount}, pyRevitFound={pyRevitLoaderFound}, ExternalNames={allExternalNames.Count}");

                // Count pyRevit extensions if pyRevit loader was found
                if (pyRevitLoaderFound)
                {
                    pyRevitExtensionCount = CountPyRevitExtensions();
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Error counting add-ins: {ex.Message}");
            }

            return (stockCount, externalCount, pyRevitExtensionCount, allExternalNames);
        }

        /// <summary>
        /// Process a single add-in folder and count add-ins from .addin manifest files
        /// </summary>
        private (int stockCount, int externalCount, bool pyRevitFound, List<string> names) ProcessAddinFolder(
            string folderPath,
            bool isStockFolder,
            HashSet<string> autodeskVendors,
            HashSet<string> stockAddinNames,
            HashSet<string> processedAddins)
        {
            int stockCount = 0;
            int externalCount = 0;
            bool pyRevitFound = false;
            var names = new List<string>();

            try
            {
                var addinFiles = System.IO.Directory.GetFiles(folderPath, "*.addin", System.IO.SearchOption.TopDirectoryOnly);

                foreach (var addinFile in addinFiles)
                {
                    try
                    {
                        var (addins, hasPyRevit) = ParseAddinManifest(addinFile, isStockFolder, autodeskVendors, stockAddinNames, processedAddins);

                        foreach (var (isStock, name) in addins)
                        {
                            if (isStock)
                            {
                                stockCount++;
                            }
                            else
                            {
                                externalCount++;
                                if (!string.IsNullOrWhiteSpace(name) &&
                                    !_selfAddinKeywords.Any(k => name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                                    names.Add(name);
                            }
                        }

                        if (hasPyRevit)
                            pyRevitFound = true;
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogDebug($"Could not parse addin file {System.IO.Path.GetFileName(addinFile)}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogDebug($"Error processing addin folder {folderPath}: {ex.Message}");
            }

            return (stockCount, externalCount, pyRevitFound, names);
        }

        /// <summary>
        /// Parse a single .addin manifest file and extract add-in information
        /// </summary>
        private (List<(bool isStock, string name)> addins, bool hasPyRevit) ParseAddinManifest(
            string addinFilePath,
            bool isStockFolder,
            HashSet<string> autodeskVendors,
            HashSet<string> stockAddinNames,
            HashSet<string> processedAddins)
        {
            var addins = new List<(bool isStock, string name)>();
            bool hasPyRevit = false;

            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.Load(addinFilePath);

                // Find all AddIn elements (can have multiple per file)
                var addinNodes = doc.SelectNodes("//AddIn");
                if (addinNodes == null) return (addins, hasPyRevit);

                foreach (System.Xml.XmlNode addinNode in addinNodes)
                {
                    try
                    {
                        var vendorId = addinNode.SelectSingleNode("VendorId")?.InnerText?.Trim() ?? "";
                        var name = addinNode.SelectSingleNode("Name")?.InnerText?.Trim() ?? "";
                        var assembly = addinNode.SelectSingleNode("Assembly")?.InnerText?.Trim() ?? "";
                        var addinId = addinNode.SelectSingleNode("AddInId")?.InnerText?.Trim() ?? "";

                        // Use AddInId or Name as unique identifier
                        var uniqueKey = !string.IsNullOrEmpty(addinId) ? addinId : name;
                        if (string.IsNullOrEmpty(uniqueKey) || processedAddins.Contains(uniqueKey))
                            continue;

                        processedAddins.Add(uniqueKey);

                        // Check for pyRevit
                        if (name.IndexOf("pyRevit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            assembly.IndexOf("pyRevit", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hasPyRevit = true;
                            Logger?.LogDebug($"pyRevit loader detected in manifest: {name}");
                            continue; // Don't count pyRevit loader, extensions counted separately
                        }

                        // Determine if this is a stock add-in
                        bool isAutodeskVendor = autodeskVendors.Any(v =>
                            vendorId.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0);

                        bool isStockByName = stockAddinNames.Any(n =>
                            name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);

                        // Stock if: (in stock folder AND Autodesk vendor) OR matches whitelist name
                        bool isStock = (isStockFolder && isAutodeskVendor) || isStockByName;

                        addins.Add((isStock, name));

                        if (isStock)
                            Logger?.LogDebug($"Stock add-in: {name} (Vendor: {vendorId}, File: {System.IO.Path.GetFileName(addinFilePath)})");
                        else
                            Logger?.LogDebug($"External add-in: {name} (Vendor: {vendorId}, File: {System.IO.Path.GetFileName(addinFilePath)})");
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogDebug($"Error parsing AddIn node: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogDebug($"Error loading addin manifest {addinFilePath}: {ex.Message}");
            }

            return (addins, hasPyRevit);
        }

        /// <summary>
        /// Count pyRevit extensions by scanning loaded assemblies from pyRevit folders
        /// </summary>
        private int CountPyRevitExtensions()
        {
            int count = 0;
            try
            {
                // Get all loaded assemblies
                var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();

                // pyRevit extension paths (standard and custom)
                var pyRevitPaths = new List<string>();

                // Standard pyRevit extensions folder
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var standardPyRevitPath = System.IO.Path.Combine(appDataPath, "pyRevit", "Extensions");
                if (System.IO.Directory.Exists(standardPyRevitPath))
                {
                    pyRevitPaths.Add(standardPyRevitPath);
                }

                // Also check ProgramData
                var programDataPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                var programDataPyRevitPath = System.IO.Path.Combine(programDataPath, "pyRevit", "Extensions");
                if (System.IO.Directory.Exists(programDataPyRevitPath))
                {
                    pyRevitPaths.Add(programDataPyRevitPath);
                }

                // Check user's pyRevit custom extensions folder (from pyRevit config)
                var pyRevitConfigPath = System.IO.Path.Combine(appDataPath, "pyRevit", "pyRevit_config.ini");
                if (System.IO.File.Exists(pyRevitConfigPath))
                {
                    try
                    {
                        var configContent = System.IO.File.ReadAllText(pyRevitConfigPath);
                        // Look for custom extension paths in config
                        var lines = configContent.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            if (line.TrimStart().StartsWith("userextensions", StringComparison.OrdinalIgnoreCase) ||
                                line.TrimStart().StartsWith("extensions", StringComparison.OrdinalIgnoreCase))
                            {
                                var parts = line.Split('=');
                                if (parts.Length > 1)
                                {
                                    var paths = parts[1].Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
                                    foreach (var p in paths)
                                    {
                                        var trimmedPath = p.Trim().Trim('"');
                                        if (System.IO.Directory.Exists(trimmedPath))
                                        {
                                            pyRevitPaths.Add(trimmedPath);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogDebug($"Could not parse pyRevit config: {ex.Message}");
                    }
                }

                // Count assemblies loaded from pyRevit paths
                var countedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var assembly in loadedAssemblies)
                {
                    try
                    {
                        if (assembly.IsDynamic)
                            continue;

                        var location = assembly.Location;
                        if (string.IsNullOrEmpty(location))
                            continue;

                        // Check if assembly is from any pyRevit path
                        foreach (var pyRevitPath in pyRevitPaths)
                        {
                            if (location.StartsWith(pyRevitPath, StringComparison.OrdinalIgnoreCase))
                            {
                                // Extract extension name from path
                                var relativePath = location.Substring(pyRevitPath.Length).TrimStart('\\', '/');
                                var extensionFolder = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

                                if (!string.IsNullOrEmpty(extensionFolder) && !countedAssemblies.Contains(extensionFolder))
                                {
                                    countedAssemblies.Add(extensionFolder);
                                    count++;
                                    Logger?.LogDebug($"pyRevit extension: {extensionFolder} ({System.IO.Path.GetFileName(location)})");
                                }
                                break;
                            }
                        }

                        // Also check for assemblies with "pyRevit" in their path but not the loader itself
                        if (location.IndexOf("pyRevit", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            location.IndexOf("pyRevitLoader", StringComparison.OrdinalIgnoreCase) < 0 &&
                            location.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            var assemblyName = System.IO.Path.GetFileNameWithoutExtension(location);
                            if (!countedAssemblies.Contains(assemblyName) &&
                                !assemblyName.StartsWith("pyRevit", StringComparison.OrdinalIgnoreCase))
                            {
                                // This might be an extension DLL
                                var parentFolder = System.IO.Path.GetDirectoryName(location);
                                var extensionName = System.IO.Path.GetFileName(parentFolder);

                                if (!string.IsNullOrEmpty(extensionName) &&
                                    extensionName.EndsWith(".extension", StringComparison.OrdinalIgnoreCase) &&
                                    !countedAssemblies.Contains(extensionName))
                                {
                                    countedAssemblies.Add(extensionName);
                                    count++;
                                    Logger?.LogDebug($"pyRevit extension (by folder): {extensionName}");
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Skip assemblies that can't be inspected
                    }
                }

                Logger?.LogInfo($"Counted {count} pyRevit extensions");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Error counting pyRevit extensions: {ex.Message}");
            }

            return count;
        }

        /// <summary>
        /// Get Desktop Connector version if installed
        /// </summary>
        private string GetDesktopConnectorVersion()
        {
            try
            {
                var dcBasePath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Autodesk",
                    "Desktop Connector");

                // Check if Desktop Connector folder exists
                if (!System.IO.Directory.Exists(dcBasePath))
                {
                    Logger?.LogDebug("Desktop Connector folder not found");
                    return "Not Installed";
                }

                // Try multiple possible executable names and locations
                var possibleExePaths = new[]
                {
                    System.IO.Path.Combine(dcBasePath, "DesktopConnector.Applications.Tray.exe"),
                    System.IO.Path.Combine(dcBasePath, "DesktopConnector.exe"),
                    System.IO.Path.Combine(dcBasePath, "Autodesk.DesktopConnector.exe"),
                    System.IO.Path.Combine(dcBasePath, "bin", "DesktopConnector.exe"),
                    System.IO.Path.Combine(dcBasePath, "bin", "Autodesk.DesktopConnector.exe"),
                    System.IO.Path.Combine(dcBasePath, "Desktop Connector.exe"),
                };

                foreach (var exePath in possibleExePaths)
                {
                    if (System.IO.File.Exists(exePath))
                    {
                        var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                        var version = versionInfo.FileVersion ?? versionInfo.ProductVersion;
                        if (!string.IsNullOrEmpty(version))
                        {
                            Logger?.LogDebug($"Desktop Connector version from {System.IO.Path.GetFileName(exePath)}: {version}");
                            return version;
                        }
                    }
                }

                // Fallback: Try to find any .exe file in the folder and get its version
                var exeFiles = System.IO.Directory.GetFiles(dcBasePath, "*.exe", System.IO.SearchOption.AllDirectories);
                foreach (var exeFile in exeFiles)
                {
                    try
                    {
                        var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(exeFile);
                        // Look for files with "Desktop" or "Connector" in name or company name contains "Autodesk"
                        if (versionInfo.CompanyName?.Contains("Autodesk") == true ||
                            System.IO.Path.GetFileName(exeFile).IndexOf("Connector", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var version = versionInfo.FileVersion ?? versionInfo.ProductVersion;
                            if (!string.IsNullOrEmpty(version))
                            {
                                Logger?.LogDebug($"Desktop Connector version from {System.IO.Path.GetFileName(exeFile)}: {version}");
                                return version;
                            }
                        }
                    }
                    catch
                    {
                        // Skip files that can't be read
                    }
                }

                // Fallback: Try registry lookup
                var registryVersion = GetDesktopConnectorVersionFromRegistry();
                if (!string.IsNullOrEmpty(registryVersion))
                {
                    return registryVersion;
                }

                // Folder exists but couldn't determine version
                Logger?.LogWarning("Desktop Connector folder exists but version could not be determined");
                return "Installed (version unknown)";
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Failed to get Desktop Connector version: {ex.Message}");
                return "Unknown";
            }
        }

        /// <summary>
        /// Try to get Desktop Connector version from Windows registry
        /// </summary>
        private string GetDesktopConnectorVersionFromRegistry()
        {
            try
            {
                // Try Autodesk's own registry key
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Autodesk\Desktop Connector"))
                {
                    if (key != null)
                    {
                        var version = key.GetValue("Version") as string;
                        if (!string.IsNullOrEmpty(version))
                        {
                            Logger?.LogDebug($"Desktop Connector version from registry: {version}");
                            return version;
                        }
                    }
                }

                // Try Windows Uninstall registry (search for Desktop Connector)
                var uninstallKeys = new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };

                foreach (var uninstallPath in uninstallKeys)
                {
                    using (var uninstallKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(uninstallPath))
                    {
                        if (uninstallKey == null) continue;

                        foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                        {
                            try
                            {
                                using (var subKey = uninstallKey.OpenSubKey(subKeyName))
                                {
                                    if (subKey == null) continue;

                                    var displayName = subKey.GetValue("DisplayName") as string;
                                    if (displayName != null &&
                                        displayName.IndexOf("Desktop Connector", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        var version = subKey.GetValue("DisplayVersion") as string;
                                        if (!string.IsNullOrEmpty(version))
                                        {
                                            Logger?.LogDebug($"Desktop Connector version from uninstall registry: {version}");
                                            return version;
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Skip subkeys that can't be read
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger?.LogDebug($"Registry lookup for Desktop Connector failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        ///     Internal method to set UIApplication (called from commands)
        /// </summary>
        public void SetUIApplication(UIApplication uiApplication)
        {
            try
            {
                var uiApplicationProvider = _services?.GetService<IUIApplicationProvider>();
                if (uiApplicationProvider != null && !uiApplicationProvider.IsAvailable)
                {
                    uiApplicationProvider.SetUIApplication(uiApplication);
                    Logger?.LogInfo("UIApplication set in provider (deferred binding until DocumentOpened)");

                    // ❌ DO NOT register command bindings here - wait for DocumentOpened
                    // ❌ DO NOT enable protection here - wait for DocumentOpened
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in SetUIApplication: {ex.Message}", ex);
            }
        }

        /// <summary>
        ///     Called when a document is opened from disk
        /// </summary>
        private void OnDocumentOpened(object sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
        {
            var totalSw = Stopwatch.StartNew();
            try
            {
                var doc = e.Document;
                if (doc == null || !doc.IsValidObject)
                {
                    Logger?.LogWarning("Invalid document in DocumentOpened");
                    return;
                }

                Logger?.LogInfo($"[TIMING] Document opened: {doc.Title}");

                // Check model registration in background (non-blocking to prevent Revit freeze)
                // Always initialize protection - API registration happens asynchronously
                TaskRunner.FireAndForget(
                    CheckAndRegisterModel(doc),
                    Logger,
                    "CheckAndRegisterModel");

                // Fetch command protection rules from backend for this model (non-blocking)
                TaskRunner.FireAndForget(
                    FetchCommandProtectionsForModel(doc),
                    Logger,
                    "FetchCommandProtectionsForModel");

                // Fetch all remaining protection types from backend (non-blocking)
                TaskRunner.FireAndForget(FetchEventProtectionsForModel(doc), Logger, "FetchEventProtectionsForModel");
                TaskRunner.FireAndForget(FetchRuleProtectionsForModel(doc), Logger, "FetchRuleProtectionsForModel");
                TaskRunner.FireAndForget(FetchPinProtectionsForModel(doc), Logger, "FetchPinProtectionsForModel");
                TaskRunner.FireAndForget(FetchHealthMonitorProtectionForModel(doc), Logger, "FetchHealthMonitorProtectionForModel");

                // Check license before enabling protection features
                var licenseValidator = _services?.GetService<LicenseValidator>();
                var policyResolver   = _services?.GetService<LicensePolicyResolver>();
                if (licenseValidator != null && policyResolver != null)
                {
                    var licenseStatus = licenseValidator.Validate();
                    if (!policyResolver.AllowExecution(licenseStatus))
                    {
                        Logger?.LogWarning($"License not valid ({licenseStatus}) — protection features will not be activated for: {doc.Title}");
                        totalSw.Stop();
                        return;
                    }
                }

                // Initialize protection bindings
                var initSw = Stopwatch.StartNew();
                InitializeDocumentProtection(doc);
                initSw.Stop();
                Logger?.LogInfo($"[TIMING] InitializeDocumentProtection: {initSw.ElapsedMilliseconds}ms");

                totalSw.Stop();
                Logger?.LogInfo($"[TIMING] OnDocumentOpened TOTAL: {totalSw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                totalSw.Stop();
                Logger?.LogError($"Error in DocumentOpened ({totalSw.ElapsedMilliseconds}ms): {ex.Message}", ex);
            }
        }

        /// <summary>
        ///     Called when a NEW document is created
        ///     DocumentCreated fires for new documents, DocumentOpened fires for existing files
        /// </summary>
        private void OnDocumentCreated(object sender, Autodesk.Revit.DB.Events.DocumentCreatedEventArgs e)
        {
            try
            {
                var doc = e.Document;
                if (doc == null || !doc.IsValidObject)
                {
                    Logger?.LogWarning("Invalid document in DocumentCreated");
                    return;
                }

                Logger?.LogInfo($"Document created: {doc.Title}");

                // Check model registration in background (non-blocking to prevent Revit freeze)
                // Always initialize protection - API registration happens asynchronously
                TaskRunner.FireAndForget(
                    CheckAndRegisterModel(doc),
                    Logger,
                    "CheckAndRegisterModel");

                // Fetch command protection rules from backend for this model (non-blocking)
                TaskRunner.FireAndForget(
                    FetchCommandProtectionsForModel(doc),
                    Logger,
                    "FetchCommandProtectionsForModel");

                // Fetch all remaining protection types from backend (non-blocking)
                TaskRunner.FireAndForget(FetchEventProtectionsForModel(doc), Logger, "FetchEventProtectionsForModel");
                TaskRunner.FireAndForget(FetchRuleProtectionsForModel(doc), Logger, "FetchRuleProtectionsForModel");
                TaskRunner.FireAndForget(FetchPinProtectionsForModel(doc), Logger, "FetchPinProtectionsForModel");
                TaskRunner.FireAndForget(FetchHealthMonitorProtectionForModel(doc), Logger, "FetchHealthMonitorProtectionForModel");

                // Initialize protection bindings regardless of API registration status
                InitializeDocumentProtection(doc);
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in DocumentCreated: {ex.Message}", ex);
            }
        }

        /// <summary>
        ///     When a cloud sync completes, retry registration for any doc whose modelGuid
        ///     wasn't resolvable at open-time. Drains <see cref="_pendingCloudRegistrations"/>.
        ///     Cloud-API quirk: doc.GetCloudModelPath().GetModelGUID() can return Guid.Empty
        ///     immediately after DocumentOpened — by the time the user has performed a sync,
        ///     the URN-derived GUID is populated and this retry can complete the registration.
        /// </summary>
        private async void OnDocumentSynchronizedRetryRegistration(object sender, Autodesk.Revit.DB.Events.DocumentSynchronizedWithCentralEventArgs e)
        {
            try
            {
                var doc = e?.Document;
                if (doc == null) return;
                if (!doc.IsModelInCloud) return; // Only cloud docs queue for retry.

                var key = doc.GetHashCode();
                if (!_pendingCloudRegistrations.TryRemove(key, out var title)) return;

                Logger?.LogInfo($"[ModelGuid] Retrying registration for cloud doc '{doc.Title}' after first sync (was pending since open).");
                await CheckAndRegisterModel(doc);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[ModelGuid] Retry-registration failed: {ex.Message}");
            }
        }

        /// <summary>
        ///     Leaves SignalR model groups when a document is closing.
        /// </summary>
        private async void OnDocumentClosing(object sender, Autodesk.Revit.DB.Events.DocumentClosingEventArgs e)
        {
            try
            {
                var doc = e.Document;
                if (doc == null || !doc.IsValidObject)
                    return;

                var modelGuid = GetModelGuid(doc);
                if (string.IsNullOrEmpty(modelGuid))
                    return;

                var connectionManager = _services?.GetService<global::BIManage.Infrastructure.SignalR.Core.ISignalRConnectionManager>();
                if (connectionManager != null)
                {
                    // Announce leave before leaving the model group
                    var revitContext = _services?.GetService<IRevitContext>();
                    var sessionId = revitContext?.SessionId.ToString() ?? "";
                    await connectionManager.AnnounceLeaveAsync(modelGuid, sessionId);
                    await connectionManager.OnDocumentClosedAsync(modelGuid);
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in OnDocumentClosing (SignalR leave): {ex.Message}", ex);
            }
        }

        /// <summary>
        ///     Shared initialization logic for both DocumentOpened and DocumentCreated
        /// </summary>
        private void InitializeDocumentProtection(Autodesk.Revit.DB.Document doc)
        {
            // 1. Get/Create Project Cookie (ExtensibleStorage GUID)
            var cookieSw = Stopwatch.StartNew();
            string projectGuid = GetOrCreateProjectCookie(doc);
            cookieSw.Stop();
            Logger?.LogDebug($"[TIMING] GetOrCreateProjectCookie: {cookieSw.ElapsedMilliseconds}ms");

            // 2. Load project-specific settings
            var loadSw = Stopwatch.StartNew();
            LoadProjectSettings(doc, projectGuid);
            loadSw.Stop();
            Logger?.LogInfo($"[TIMING] LoadProjectSettings: {loadSw.ElapsedMilliseconds}ms");

            // 3. Trigger ExternalEvent to register command bindings
            var raiseSw = Stopwatch.StartNew();
            _setupProtectionBindingsEvent?.Raise();
            raiseSw.Stop();
            Logger?.LogDebug($"[TIMING] ExternalEvent.Raise(): {raiseSw.ElapsedMilliseconds}ms");

            Logger?.LogInfo($"Protection binding setup triggered for project {projectGuid}");
        }

        /// <summary>
        ///     Fetches command protection rules from the backend API for the given model.
        ///     Called asynchronously (fire-and-forget) on document open/create.
        ///     After fetch, reloads the in-memory CommandProtectionBinding so new rules take effect.
        /// </summary>
        private async System.Threading.Tasks.Task FetchCommandProtectionsForModel(Autodesk.Revit.DB.Document doc)
        {
            try
            {
                var modelGuid = GetModelGuid(doc);
                if (string.IsNullOrEmpty(modelGuid))
                {
                    Logger?.LogDebug("FetchCommandProtectionsForModel: no model GUID, skipping");
                    return;
                }

                var syncService = _services?.GetService<CommandProtectionSyncService>();
                if (syncService == null)
                {
                    Logger?.LogDebug("FetchCommandProtectionsForModel: sync service not available");
                    return;
                }

                var commands = await syncService.FetchByModelGuidFromApiAsync(modelGuid);
                if (commands.Count > 0)
                {
                    // Reload CommandProtectionBinding from DB (if available)
                    var binding = _services?.GetService<CommandProtectionBinding>();
                    binding?.LoadAndRegisterCommands();
                    Logger?.LogInfo($"Command protection rules refreshed from API for model {modelGuid} ({commands.Count} rules)");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"FetchCommandProtectionsForModel failed (non-critical): {ex.Message}");
            }
        }

        /// <summary>
        ///     Fetches event protection settings from the backend API for the given model.
        ///     Called asynchronously (fire-and-forget) on document open/create.
        /// </summary>
        private async System.Threading.Tasks.Task FetchEventProtectionsForModel(Autodesk.Revit.DB.Document doc)
        {
            try
            {
                var modelGuid = GetModelGuid(doc);
                if (string.IsNullOrEmpty(modelGuid)) return;
                var syncService = _services?.GetService<EventProtectionSyncService>();
                if (syncService == null) return;
                Logger?.LogInfo($"Fetching event protections for model: {modelGuid}");
                var settings = await syncService.FetchByModelGuidFromApiAsync(modelGuid);
                var eventProtection = _services?.GetService<IEventProtectionService>();
                eventProtection?.LoadSettingsFromDatabase(projectId: null, modelGuid: modelGuid);
                Logger?.LogInfo($"Fetched {settings.Count} event protections for model {modelGuid}");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"FetchEventProtectionsForModel failed (non-critical): {ex.Message}");
            }
        }

        /// <summary>
        ///     Fetches rule protections from the backend API for the given model.
        ///     Called asynchronously (fire-and-forget) on document open/create.
        /// </summary>
        private async System.Threading.Tasks.Task FetchRuleProtectionsForModel(Autodesk.Revit.DB.Document doc)
        {
            try
            {
                var modelGuid = GetModelGuid(doc);
                if (string.IsNullOrEmpty(modelGuid)) return;
                var syncService = _services?.GetService<RulesSyncService>();
                if (syncService == null) return;
                Logger?.LogInfo($"Fetching rule protections for model: {modelGuid}");
                var rules = await syncService.FetchRuleProtectionsByModelAsync(modelGuid);
                var ruleService = _services?.GetService<IRuleService>();
                if (ruleService != null) await ruleService.RefreshRules();
                Logger?.LogInfo($"Fetched {rules.Count} rule protections for model {modelGuid}");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"FetchRuleProtectionsForModel failed (non-critical): {ex.Message}");
            }
        }

        /// <summary>
        ///     Fetches pin protections from the backend API for the given model.
        ///     Called asynchronously (fire-and-forget) on document open/create.
        /// </summary>
        private async System.Threading.Tasks.Task FetchPinProtectionsForModel(Autodesk.Revit.DB.Document doc)
        {
            try
            {
                var modelGuid = GetModelGuid(doc);
                if (string.IsNullOrEmpty(modelGuid)) return;
                var syncService = _services?.GetService<PinProtectionSyncService>();
                if (syncService == null) return;
                Logger?.LogInfo($"Fetching pin protections for model: {modelGuid}");
                var pins = await syncService.FetchPinProtectionsByModelAsync(modelGuid);
                Logger?.LogInfo($"Fetched {pins.Count} pin protections for model {modelGuid}");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"FetchPinProtectionsForModel failed (non-critical): {ex.Message}");
            }
        }

        /// <summary>
        ///     Fetches health monitor protection thresholds from the backend API for UI display.
        ///     Called asynchronously (fire-and-forget) on document open/create.
        ///     Thresholds are UI/visual only — do not affect protection logic.
        /// </summary>
        private async System.Threading.Tasks.Task FetchHealthMonitorProtectionForModel(Autodesk.Revit.DB.Document doc)
        {
            try
            {
                var modelGuid = GetModelGuid(doc);
                if (string.IsNullOrEmpty(modelGuid)) return;
                var syncService = _services?.GetService<HealthMonitorProtectionSyncService>();
                if (syncService == null) return;
                await syncService.FetchProtectionAsync(modelGuid);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"FetchHealthMonitorProtectionForModel failed (non-critical): {ex.Message}");
            }
        }

        /// <summary>
        ///     Check if model is registered and auto-register if enabled
        ///     Returns true if model should be initialized (registered), false otherwise
        /// </summary>
        private async System.Threading.Tasks.Task<bool> CheckAndRegisterModel(Autodesk.Revit.DB.Document doc)
        {
            try
            {
                var modelRepo = _services?.GetService<RegisteredModelsRepository>();
                if (modelRepo == null)
                {
                    Logger?.LogWarning("RegisteredModelsRepository not available - allowing initialization by default");
                    return true; // Fail open - allow initialization if service missing
                }

                // Block unsaved documents — PathName is empty until first Save
                if (string.IsNullOrEmpty(doc.PathName))
                {
                    Logger?.LogInfo($"Skipping registration — document not yet saved to disk: {doc.Title}");
                    return false;
                }

                // Block documents without a real file path on disk (except cloud models which use cloud:// paths)
                if (!doc.IsModelInCloud && !System.IO.File.Exists(doc.PathName))
                {
                    Logger?.LogInfo($"Skipping registration — file path does not exist on disk: {doc.PathName} ({doc.Title})");
                    return false;
                }

                // Block detached workshared models — opened with "Detach from Central", not saved as standalone
                if (doc.IsWorkshared && doc.IsDetached)
                {
                    Logger?.LogInfo($"Skipping registration — detached workshared model (use Save As to register): {doc.Title}");
                    return false;
                }

                // Get model info for duplicate prevention
                var modelName = GetModelNameForRegistration(doc);
                var centralModelPath = GetCentralModelPath(doc);

                // Get model GUID - first check if existing model with same name/path exists
                var modelGuid = GetModelGuid(doc);
                if (string.IsNullOrEmpty(modelGuid))
                {
                    // Most common cause for cloud models: ModelGuidHelper.GetModelGuid
                    // returned null because doc.GetCloudModelPath().GetModelGUID()
                    // returned Guid.Empty at open-time — cloud sync metadata hadn't
                    // been populated yet. Mark the doc for retry on the next sync
                    // event so we don't permanently miss registration for new cloud
                    // models. The retry is harmless for non-cloud docs because the
                    // sync event also has to fire there for the retry to trigger.
                    if (doc.IsModelInCloud)
                    {
                        try { _pendingCloudRegistrations[doc.GetHashCode()] = doc.Title ?? "<untitled>"; } catch { }
                        Logger?.LogInfo($"[ModelGuid] Cloud modelGuid not yet available for '{doc.Title}' — queued for retry on next DocumentSynchronizedWithCentral.");
                    }
                    else
                    {
                        Logger?.LogWarning($"Unable to determine model GUID for {doc.Title} - allowing initialization by default");
                    }
                    return true; // Fail open
                }

                // DUPLICATE PREVENTION: Check if model already exists by name and central path
                // This handles cases where GUID differs (e.g., first open as detached, then as local copy)
                var existingGuid = await modelRepo.FindExistingModelGuidAsync(modelName, centralModelPath);
                if (!string.IsNullOrEmpty(existingGuid) && existingGuid != modelGuid)
                {
                    Logger?.LogInfo($"Found existing registration for '{modelName}' - using existing GUID: {existingGuid} (instead of {modelGuid})");
                    modelGuid = existingGuid;

                    // Update central path if we have one now but the existing record doesn't
                    if (!string.IsNullOrEmpty(centralModelPath))
                    {
                        await modelRepo.UpdateCentralModelPathAsync(modelGuid, centralModelPath);
                    }
                }

                // Check registration: API first (source of truth), then local DB fallback
                var (exists, isActive) = await modelRepo.GetModelRegistrationStatusAsync(modelGuid);

                // Try API check — updates local DB with server state
                string? resolvedProjectId = null;
                var modelSyncService = _services?.GetService<ModelSyncService>();
                if (modelSyncService != null)
                {
                    try
                    {
                        var apiModel = await modelSyncService.FetchModelByGuidAsync(modelGuid);
                        if (apiModel != null)
                        {
                            // Model exists on server — upsert into local DB
                            exists = true;
                            isActive = apiModel.IsActive;
                            // Server returns project GUID under either "zemanageProjectId" or
                            // "projectId" depending on endpoint — capture whichever is populated
                            // so we can join the SignalR project group below.
                            resolvedProjectId = !string.IsNullOrWhiteSpace(apiModel.ZemanageProjectId)
                                ? apiModel.ZemanageProjectId
                                : apiModel.ProjectIdAlternate;

                            await modelRepo.RegisterOrUpdateModelAsync(new RegisteredModel
                            {
                                ModelGuid = apiModel.ModelGuid ?? modelGuid,
                                ModelName = apiModel.ModelName ?? modelName,
                                CentralModelPath = apiModel.CentralModelPath ?? centralModelPath,
                                ProjectName = apiModel.LocalProjectName,
                                ZemanageProjectId = resolvedProjectId,
                                IsActive = apiModel.IsActive,
                                RegisteredBy = apiModel.RegisteredBy ?? Environment.UserName,
                                IsWorkshared = apiModel.IsWorkshared,
                                IsFamily = apiModel.IsFamily,
                                IsCloudModel = apiModel.IsCloudModel,
                                Notes = apiModel.Notes
                            });

                            Logger?.LogInfo($"Model registration synced from API: {modelGuid} (active={apiModel.IsActive}, project={resolvedProjectId ?? "<none>"})");
                        }
                        else if (!exists)
                        {
                            // Not on server AND not in local DB — unregistered
                            Logger?.LogDebug($"Model not found on API or local DB: {modelGuid}");
                        }
                    }
                    catch (Exception apiEx)
                    {
                        Logger?.LogDebug($"API model check failed (using local DB): {apiEx.Message}");
                        // Fall through to local DB state
                    }
                }

                // If the API didn't give us a project ID (offline / fetch failed), fall back
                // to local DB so we still join the project SignalR group.
                if (string.IsNullOrEmpty(resolvedProjectId))
                {
                    try
                    {
                        var localModel = await modelRepo.GetModelAsync(modelGuid);
                        resolvedProjectId = localModel?.ZemanageProjectId;
                    }
                    catch (Exception lookupEx)
                    {
                        Logger?.LogDebug($"Local project ID lookup failed: {lookupEx.Message}");
                    }
                }

                if (exists)
                {
                    // Always join SignalR groups — even for deactivated models — so live updates
                    // (reactivation, protection changes) are received while the document is open.
                    // Pass projectId so the client joins the project: SignalR group — without it,
                    // project-level ProtectionSettingsChange pushes (which the server routes to
                    // project:{projectId}) never reach this client.
                    var connectionManager = _services?.GetService<global::BIManage.Infrastructure.SignalR.Core.ISignalRConnectionManager>();
                    if (connectionManager != null)
                    {
                        try
                        {
                            await connectionManager.OnDocumentOpenedAsync(modelGuid, resolvedProjectId);
                            await connectionManager.AnnouncePresenceAsync(BuildPresencePayload(modelGuid, resolvedProjectId, doc));
                            Logger?.LogInfo($"SignalR: Joined groups for model {modelGuid} (active={isActive}, project={resolvedProjectId ?? "<none>"})");
                        }
                        catch (Exception srEx)
                        {
                            Logger?.LogDebug($"SignalR group join failed: {srEx.Message}");
                        }
                    }

                    if (!isActive)
                    {
                        // Model deactivated - skip protection initialization but keep SignalR connected
                        Logger?.LogInfo($"Model deactivated (is_active=0) - skipping protections: {doc.Title} ({modelGuid})");
                        BIManageRevit.Commands.RibbonCommands.RegisterModelCommand.UpdateButtonAppearance(true);
                        return false;
                    }

                    Logger?.LogInfo($"Model registered and active: {doc.Title} ({modelGuid})");
                    BIManageRevit.Commands.RibbonCommands.RegisterModelCommand.UpdateButtonAppearance(true);

                    // Update last opened timestamp
                    var username = doc.Application.Username ?? Environment.UserName;
                    await modelRepo.UpdateLastOpenedAsync(modelGuid, username);

                    // Sync model to backend API
                    await SyncModelToApiAsync(modelGuid);

                    return true; // Model registered and active - proceed with initialization
                }

                // Model not in database
                BIManageRevit.Commands.RibbonCommands.RegisterModelCommand.UpdateButtonAppearance(false);

                // Check auto-register setting
                var autoRegister = await modelRepo.GetAutoRegisterSettingAsync();

                if (autoRegister)
                {
                    // modelName and centralModelPath already computed above for duplicate prevention
                    var isLocalCopy = DocumentInformationHelper.IsLocalCopy(doc);

                    Logger?.LogInfo($"Auto-registering: {modelName} (IsWorkshared: {doc.IsWorkshared}, IsLocalCopy: {isLocalCopy}, GUID: {modelGuid})");

                    // Get username for registration
                    var username = doc.Application.Username ?? Environment.UserName;

                    // Auto-register the central model (not local copy)
                    var cloudProjectId = GetCloudProjectId(doc);
                    var modelInfo = new RegisteredModel
                    {
                        ModelGuid = modelGuid,
                        ModelName = modelName, // Use central model name for workshared
                        CentralModelPath = centralModelPath, // Use already-computed path
                        ProjectName = GetProjectName(doc),
                        CloudProjectId = cloudProjectId,
                        ZemanageProjectId = cloudProjectId ?? GetLocalProjectId(doc), // Use cloud project ID or generate from folder
                        RegisteredBy = username, // Use actual username instead of hardcoded value
                        ModelType = GetModelType(doc),
                        IsLocalCopy = false, // Always false - we register central models only
                        IsWorkshared = doc.IsWorkshared,
                        IsFamily = doc.IsFamilyDocument,
                        IsCloudModel = doc.IsModelInCloud,
                        Notes = $"Auto-registered on {DateTime.Now:yyyy-MM-dd HH:mm:ss} by {username}" +
                                (isLocalCopy ? " (via local copy)" : "")
                    };

                    var success = await modelRepo.RegisterModelAsync(modelInfo);

                    if (success)
                    {
                        Logger?.LogInfo($"Model auto-registered locally: {modelName} ({modelGuid}) — awaiting server confirmation");

                        // Update last_opened immediately after local registration
                        await modelRepo.UpdateLastOpenedAsync(modelGuid, username);

                        // Sync to backend API BEFORE flipping the UI — the ribbon and
                        // "registered successfully" signal are misleading if the server
                        // rejects the POST. Common case in staging: company is not yet
                        // provisioned with a default project and the server returns 400
                        // "No default project found for this company" — previously the
                        // user saw the ribbon turn green and a success notification, but
                        // no row ever landed on the server, and every downstream sync
                        // (model-sessions, metrics, model-syncs) cascaded-failed with
                        // FK constraint violations against the missing parent row.
                        var serverResult = await SyncModelToApiAsync(modelGuid);

                        if (serverResult != null && serverResult.ServerAccepted)
                        {
                            Logger?.LogInfo($"Model auto-registered successfully: {modelName} ({modelGuid})");
                            BIManageRevit.Commands.RibbonCommands.RegisterModelCommand.UpdateButtonAppearance(true);
                        }
                        else if (serverResult != null && serverResult.IsTenantNotProvisioned)
                        {
                            Logger?.LogWarning(
                                $"Model registered locally, but the server rejected registration because this company has no default project configured. " +
                                $"Ask your BIManage administrator to provision the company — until then, model/session/metrics sync cannot land on the server. " +
                                $"Model: {modelName} ({modelGuid}).");
                            BIManageRevit.Commands.RibbonCommands.RegisterModelCommand.UpdateButtonAppearance(false);
                        }
                        else if (serverResult != null && serverResult.Rejected)
                        {
                            Logger?.LogWarning(
                                $"Model registered locally, but the server rejected registration " +
                                $"(Status: {serverResult.StatusCode}, Body: {serverResult.ResponseBody}). " +
                                $"Ribbon will stay un-green until the server accepts on a future re-open. Model: {modelName} ({modelGuid}).");
                            BIManageRevit.Commands.RibbonCommands.RegisterModelCommand.UpdateButtonAppearance(false);
                        }
                        else if (serverResult != null && serverResult.WasQueued)
                        {
                            Logger?.LogInfo(
                                $"Model registered locally; server sync queued for retry (transient failure). " +
                                $"Ribbon will flip green automatically after a successful retry on next model open. Model: {modelName} ({modelGuid}).");
                            BIManageRevit.Commands.RibbonCommands.RegisterModelCommand.UpdateButtonAppearance(false);
                        }
                        else
                        {
                            // serverResult == null (ModelSyncService not registered) OR NoTransport outcome:
                            // no authenticated path to the server is available. Keep ribbon un-green so the
                            // state is honest — auto-register will try again on the next document open.
                            Logger?.LogWarning(
                                $"Model registered locally but server sync was not attempted or had no transport " +
                                $"({serverResult?.ErrorMessage ?? "service not registered"}). " +
                                $"Ribbon will stay un-green. Model: {modelName} ({modelGuid}).");
                            BIManageRevit.Commands.RibbonCommands.RegisterModelCommand.UpdateButtonAppearance(false);
                        }

                        // Join SignalR groups for this model
                        var connectionManager = _services?.GetService<global::BIManage.Infrastructure.SignalR.Core.ISignalRConnectionManager>();
                        if (connectionManager != null)
                        {
                            var projectId = cloudProjectId ?? modelInfo.ZemanageProjectId;
                            await connectionManager.OnDocumentOpenedAsync(modelGuid, projectId);
                            // Announce presence so other users see us in their Model Activities
                            Logger?.LogInfo($"SignalR: Sending presence announcement for model {modelGuid}");
                            await connectionManager.AnnouncePresenceAsync(BuildPresencePayload(modelGuid, projectId, doc));
                            Logger?.LogInfo($"SignalR: Presence announcement completed for model {modelGuid}");
                        }

                        return true; // Auto-registration successful - proceed with initialization
                    }
                    else
                    {
                        Logger?.LogError($"Failed to auto-register model: {modelName} ({modelGuid}) - skipping initialization");
                        return false; // Registration failed - skip initialization
                    }
                }
                else
                {
                    Logger?.LogInfo($"Model not registered and auto-registration disabled - skipping initialization: {doc.Title} ({modelGuid})");
                    return false; // Auto-registration disabled - skip initialization
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error checking model registration: {ex.Message}", ex);
                return true; // Fail open - allow initialization on error to avoid blocking users
            }
        }

        /// <summary>
        /// Builds a UserPresencePayload for announcing this user's presence on a model via SignalR.
        /// </summary>
        private global::BIManage.Infrastructure.SignalR.Messages.UserPresencePayload BuildPresencePayload(
            string modelGuid, string projectId, Autodesk.Revit.DB.Document doc)
        {
            var revitContext = _services?.GetService<IRevitContext>();
            var userIdentity = _services?.GetService<global::BIManage.Core.Identity.UserIdentity>();

            // Accessing doc.Application.Username from a background thread (FireAndForget)
            // may throw on some Revit versions — safeguard against thread-safety issues
            string revitUsername = null;
            try { revitUsername = doc?.Application?.Username; }
            catch (Exception ex) { Logger?.LogWarning($"BuildPresencePayload: Could not read Revit username - {ex.Message}"); }

            return new global::BIManage.Infrastructure.SignalR.Messages.UserPresencePayload
            {
                SessionId = revitContext?.SessionId.ToString() ?? Guid.NewGuid().ToString(),
                UserId = userIdentity?.UserId ?? Environment.UserName,
                Username = Environment.UserName,
                RevitUsername = revitUsername ?? Environment.UserName,
                UserEmail = userIdentity?.Email,
                ComputerName = Environment.MachineName,
                ModelGuid = modelGuid,
                ProjectId = projectId,
                JoinedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        ///     Get model GUID for identification
        ///     Uses ModelGuidHelper for robust fallback chain
        /// </summary>
        private string GetModelGuid(Autodesk.Revit.DB.Document doc)
        {
            return ModelGuidHelper.GetModelGuid(doc, Logger);
        }

        /// <summary>
        ///     Get central model path (or file path for local models)
        ///     Workshared: central model path
        ///     Cloud: cloud model path string
        ///     Local: current file path
        /// </summary>
        private string GetCentralModelPath(Autodesk.Revit.DB.Document doc)
        {
            try
            {
                // Cloud models: return cloud path
                if (doc.IsModelInCloud)
                {
                    var cloudPath = doc.GetCloudModelPath();
                    if (cloudPath != null)
                    {
                        return Autodesk.Revit.DB.ModelPathUtils.ConvertModelPathToUserVisiblePath(cloudPath);
                    }
                    return doc.PathName;
                }

                // Workshared: return central model path
                if (doc.IsWorkshared)
                {
                    var centralPath = doc.GetWorksharingCentralModelPath();
                    return Autodesk.Revit.DB.ModelPathUtils.ConvertModelPathToUserVisiblePath(centralPath);
                }

                // Local: return current file path
                return doc.PathName;
            }
            catch { }
            return doc.PathName; // Fallback to current path
        }

        /// <summary>
        ///     Get project name
        /// </summary>
        private string GetProjectName(Autodesk.Revit.DB.Document doc)
        {
            try
            {
                return doc.ProjectInformation?.Name;
            }
            catch { }
            return null;
        }

        /// <summary>
        ///     Get cloud project ID
        /// </summary>
        private string GetCloudProjectId(Autodesk.Revit.DB.Document doc)
        {
            try
            {
                if (doc.IsModelInCloud)
                {
                    var cloudPath = doc.GetCloudModelPath();
                    if (cloudPath != null)
                    {
                        var projectGuid = cloudPath.GetProjectGUID(); // Guid is non-nullable struct
                        return projectGuid.ToString();
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        ///     Generate deterministic project ID for local models
        ///     Uses parent folder path to group related models
        /// </summary>
        private string GetLocalProjectId(Autodesk.Revit.DB.Document doc)
        {
            try
            {
                var path = doc.PathName;
                if (string.IsNullOrEmpty(path))
                    return null;

                // Use parent folder as project identifier
                var folder = System.IO.Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(folder))
                    return null;

                // Generate deterministic GUID from folder path
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(folder.ToLowerInvariant()));
                    return new Guid(hash).ToString();
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        ///     Get model type
        /// </summary>
        private string GetModelType(Autodesk.Revit.DB.Document doc)
        {
            if (doc.IsFamilyDocument)
                return "family";
            if (doc.IsModelInCloud)
                return "cloudmodel";
            if (doc.IsWorkshared)
                return "workshared";
            return "local";
        }

        /// <summary>
        /// Sync model registration data to backend API.
        /// Returns the structured outcome so callers can decide UI state
        /// (ribbon button green vs warning) based on whether the server
        /// actually accepted the POST — previously this was fire-and-forget
        /// and the UI flipped to "registered" on local SQLite success alone,
        /// even when the server rejected with 400 "No default project found for this company".
        /// Returns null if ModelSyncService is not registered.
        /// </summary>
        private async Task<ModelSyncResult?> SyncModelToApiAsync(string modelGuid)
        {
            try
            {
                var modelSyncService = _services?.GetService<ModelSyncService>();
                if (modelSyncService == null)
                {
                    Logger?.LogDebug("ModelSyncService not registered - skipping model API sync");
                    return null;
                }

                try
                {
                    var result = await modelSyncService.SyncModelAsync(modelGuid);
                    Logger?.LogInfo($"Model API sync: {result.Outcome}");
                    return result;
                }
                catch (Exception syncEx)
                {
                    Logger?.LogWarning($"Model API sync failed (non-critical): {syncEx.Message}");
                    return ModelSyncResult.NoTransport(syncEx.Message);
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Model sync setup failed: {ex.Message}");
                return ModelSyncResult.NoTransport(ex.Message);
            }
        }

        /// <summary>
        ///     Get model name for registration
        ///     For workshared: returns central model name
        ///     For non-workshared: returns document title
        /// </summary>
        private string GetModelNameForRegistration(Autodesk.Revit.DB.Document doc)
        {
            if (doc.IsWorkshared)
            {
                var centralName = DocumentInformationHelper.GetCentralModelName(doc);
                if (!string.IsNullOrEmpty(centralName))
                {
                    return centralName;
                }
            }
            return doc.Title;
        }

        /// <summary>
        ///     Get or create project cookie (GUID) from ExtensibleStorage
        ///     Unique identifier for per-project settings
        /// </summary>
        private string GetOrCreateProjectCookie(Autodesk.Revit.DB.Document doc)
        {
            try
            {
                // TODO: Implement ExtensibleStorage cookie pattern
                // For now, use document GUID or create deterministic GUID from path
                var docGuid = doc.GetHashCode().ToString();
                Logger?.LogDebug($"Project cookie generated: {docGuid} (TODO: Implement ExtensibleStorage persistence)");
                return docGuid;
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to get project cookie: {ex.Message}", ex);
                return Guid.NewGuid().ToString(); // Fallback to random GUID
            }
        }

        /// <summary>
        ///     Load project-specific protection settings from database
        /// </summary>
        private void LoadProjectSettings(Autodesk.Revit.DB.Document doc, string projectGuid)
        {
            // Command protection is now handled by CommandProtectionBinding (database-driven)
            // which is initialized in SetupProtectionBindingsExternalEventHandler on DocumentOpened.
            // No legacy CommandProtectionService loading needed.
            Logger?.LogDebug($"LoadProjectSettings called for project {projectGuid} (handled by CommandProtectionBinding)");
        }

        /// <summary>
        /// Start periodic metrics snapshot service
        /// </summary>
        /// <summary>
        /// Starts the background sync engine that orchestrates automatic sync, relinquish,
        /// compact, and auto-exit operations based on configured settings and idle state.
        /// </summary>
        private void StartBackgroundSyncEngine(UIApplication uiApp)
        {
            try
            {
                if (_backgroundSyncEvent == null || _backgroundSyncHandler == null ||
                    _backgroundRelinquishEvent == null || _backgroundRelinquishHandler == null ||
                    _autoExitEvent == null || _autoExitHandler == null)
                {
                    Logger?.LogDebug("[BackgroundSyncEngine] ExternalEvents not created — skipping");
                    return;
                }

                var featureToggle = _services?.GetService<IFeatureToggleService>();
                var productivityTracker = _services?.GetService<global::BIManage.Revit.Productivity.IProductivityTracker>();
                var syncRepository = _services?.GetService<SyncRepository>();
                var syncTrafficControl = _services?.GetService<global::BIManage.Revit.SyncTrafficControl.SyncTrafficControlService>();

                if (featureToggle == null || productivityTracker == null || syncRepository == null)
                {
                    Logger?.LogWarning("[BackgroundSyncEngine] Missing dependencies — skipping");
                    return;
                }

                // UIApplication getter — captures the reference for the engine timer to use
                var capturedUiApp = uiApp;

                _backgroundSyncEngine = new BackgroundSyncEngine(
                    featureToggle,
                    productivityTracker,
                    syncRepository,
                    syncTrafficControl,
                    Logger,
                    _backgroundSyncEvent,
                    _backgroundSyncHandler,
                    _backgroundRelinquishEvent,
                    _backgroundRelinquishHandler,
                    _autoExitEvent,
                    _autoExitHandler,
                    () => capturedUiApp);

                _backgroundSyncEngine.Start();
                _services?.RegisterSingleton(_backgroundSyncEngine);

                Logger?.LogInfo("[BackgroundSyncEngine] Started successfully");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[BackgroundSyncEngine] Failed to start: {ex.Message}", ex);
            }
        }

        private void StartPeriodicMetricsService(UIApplication uiApp)
        {
            try
            {
                var metricsRepository = _services?.GetService<ModelFileMetricsRepository>();
                var metricsCollector = _services?.GetService<ModelFileMetricsCollectorService>();
                var sessionRepository = _services?.GetService<SessionRepository>();
                var revitContext = _services?.GetService<IRevitContext>();

                if (metricsRepository == null || metricsCollector == null)
                {
                    Logger?.LogDebug("Periodic metrics service not started - missing dependencies");
                    return;
                }

                // Create and start periodic service (daily snapshots at 2 AM local time)
                _periodicMetricsService = new PeriodicMetricsSnapshotService(
                    Logger,
                    metricsRepository,
                    metricsCollector,
                    sessionRepository,
                    revitContext,
                    uiApp,
                    metricsSyncGetter: () => _services?.GetService<MetricsSyncService>(),
                    intervalHours: 24,
                    startHourLocal: 2);

                _periodicMetricsService.Start();

                Logger?.LogInfo("Periodic metrics snapshot service started");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to start periodic metrics service: {ex.Message}", ex);
            }
        }

        /// <summary>
        ///     Called when Revit shuts down or unloads the add-in
        /// </summary>
        public override void OnShutdown()
        {
            try
            {
                Logger?.LogInfo("BIManageRevit Add-in shutting down...");


#if REVIT2027
                // Stop ZeManage Desktop Agent (flushes remaining sync batch before Revit exits)
                try { _zeManageIntegration?.Dispose(); }
                catch (Exception zeEx) { Logger?.LogWarning($"[ZeManageIntegration] Dispose: {zeEx.Message}"); }
#endif

                // Stop background sync engine
                _backgroundSyncEngine?.Dispose();

                // Stop periodic metrics service
                _periodicMetricsService?.Stop();
                _periodicMetricsService?.Dispose();

                // Detach the Worksets / Manage Links observer
                try { _services?.GetService<global::BIManage.Revit.Idling.PostOpenDialogTimingService>()?.Stop(); }
                catch (Exception dtEx) { Logger?.LogDebug($"PostOpenDialogTimingService stop: {dtEx.Message}"); }

                // Detach the SyncConflict dialog interceptor
                try { _services?.GetService<global::BIManage.Revit.SyncTrafficControl.SyncConflictDialogInterceptor>()?.Dispose(); }
                catch (Exception siEx) { Logger?.LogDebug($"SyncConflictDialogInterceptor dispose: {siEx.Message}"); }

                // Tear down the CAD Explode ribbon swap (unsubscribes UIElementActivated +
                // CollectionChanged hooks). Defensive — swap may be null if init failed or
                // AdWindows wasn't reachable at startup.
                try { _cadExplodeRibbonSwap?.Dispose(); }
                catch (Exception swapEx) { Logger?.LogDebug($"CADExplodeRibbonSwap dispose: {swapEx.Message}"); }

                // Record shutdown before disposal
                _services?.GetService<IRevitHealthMonitor>()?.RecordShutdown();

                // Check journal for crash evidence BEFORE closing session.
                // Revit's crash handler often calls OnShutdown before the process dies,
                // so the journal may already contain crash keywords at this point.
                var sessionRepository = _services?.GetService<SessionRepository>();
                bool isCrashShutdown = false;
                try
                {
                    if (sessionRepository != null)
                    {
                        var (journalFile, revitVer) = sessionRepository.GetCurrentSessionJournalInfo();
                        var evidence = RevitJournalCrashDetector.Analyze(journalFile, revitVer);
                        if (evidence.IsDefinitiveCrash)
                        {
                            isCrashShutdown = true;
                            Logger?.LogWarning($"[OnShutdown] Journal shows crash evidence (keyword: {evidence.KeywordFound}) — will mark as Crashed");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger?.LogDebug($"[OnShutdown] Journal crash check failed: {ex.Message}");
                }

                // Update session via API PATCH
                try
                {
                    Logger?.LogInfo("Attempting session API PATCH update...");
                    var revitContext = _services?.GetService<IRevitContext>();
                    var sessionSyncService = _services?.GetService<SessionSyncService>();

                    if (revitContext == null)
                    {
                        Logger?.LogWarning("Session PATCH skipped - IRevitContext is null");
                    }
                    else if (sessionSyncService == null)
                    {
                        Logger?.LogWarning("Session PATCH skipped - SessionSyncService is null");
                    }
                    else
                    {
                        var sessionId = revitContext.SessionId.ToString();
                        var status = isCrashShutdown ? "Crashed" : "Closed";
                        Logger?.LogInfo($"Updating session via API PATCH: {sessionId} (Status: {status})");
                        var patchTask = sessionSyncService.UpdateSessionAsync(sessionId, status, false, isCrashShutdown);
                        try { patchTask.Wait(TimeSpan.FromSeconds(3)); }
                        catch { } // Timeout is acceptable — best-effort close
                    }
                }
                catch (Exception ex)
                {
                    Logger?.LogError($"Session API PATCH failed: {ex.Message}", ex);
                }

                // Gracefully shutdown SessionRepository BEFORE disposal
                // ShutdownAsync also checks journal internally (double-check)
                if (sessionRepository != null)
                {
                    try { sessionRepository.ShutdownAsync(crashDetected: isCrashShutdown).Wait(TimeSpan.FromSeconds(2)); }
                    catch { } // Best-effort
                }

                // ServiceRegistry handles all disposal in correct order
                _services?.Dispose();
            }
            catch
            {
                // Can't log at this point - logger may be disposed
                // Could write to Windows Event Log if critical
            }
            finally
            {
                _instance = null;
            }
        }

        #region Theme-Aware Ribbon Icons

        /// <summary>
        /// Returns the icon resource folder based on current theme.
        /// </summary>
        private string GetIconFolder() => _isDarkTheme ? "IconsWhite" : "Icons";


#if REVIT2024_OR_GREATER
        /// <summary>
        /// Live theme-switch handler. Re-reads UIThemeManager.CurrentTheme and refreshes every
        /// tracked ribbon button so icons follow the user toggling Revit's UI theme without a
        /// restart. Filters out canvas-only theme changes (those don't affect ribbon icons).
        /// </summary>
        private void OnRevitThemeChanged(object sender, Autodesk.Revit.UI.Events.ThemeChangedEventArgs e)
        {
            try
            {
#if REVIT2025_OR_GREATER
                // R25+ split the event into UITheme vs CanvasTheme. Only UI/ribbon changes
                // affect our icons — skip canvas-only events. R24 fires a single event for
                // both, no discriminator on the args; we accept the extra (cheap) refresh.
                if (e.ThemeChangedType != ThemeType.UITheme) return;
#endif

                var newIsDark = UIThemeManager.CurrentTheme == UITheme.Dark;
                if (newIsDark == _isDarkTheme) return;

                _isDarkTheme = newIsDark;
                Logger?.LogInfo($"Revit UI theme changed → {(_isDarkTheme ? "Dark" : "Light")}; refreshing ribbon icons");
                UpdateAllButtonIcons();
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"OnRevitThemeChanged failed: {ex.Message}");
            }
        }
#endif

        /// <summary>
        /// Updates all tracked ribbon button icons to match the current theme.
        /// </summary>
        private void UpdateAllButtonIcons()
        {
            var iconFolder = GetIconFolder();
            foreach (var (button, iconName) in _ribbonButtons)
            {
                try
                {
                    // For Device Status button, use current state icon (not the initial iconName)
                    var actualIconName = iconName;
                    if (button == BIManageRevit.Commands.RibbonCommands.DeviceStatusCommand.RibbonButton)
                    {
                        actualIconName = BIManageRevit.Commands.RibbonCommands.DeviceStatusCommand._isDeviceRegistered
                            ? "DeviceGreen" : "DeviceRed";
                    }

                    var icon16 = string.IsNullOrEmpty(actualIconName) ? "RibbonIcon_16" : $"{actualIconName}_16";
                    var icon32 = string.IsNullOrEmpty(actualIconName) ? "RibbonIcon_32" : $"{actualIconName}_32";

                    button.Image = new System.Windows.Media.Imaging.BitmapImage(
                        new Uri($"pack://application:,,,/BIManageRevit;component/Resources/{iconFolder}/{icon16}.png"));
                    button.LargeImage = new System.Windows.Media.Imaging.BitmapImage(
                        new Uri($"pack://application:,,,/BIManageRevit;component/Resources/{iconFolder}/{icon32}.png"));
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"Failed to update icon for button '{iconName}': {ex.Message}");
                }
            }
        }

        #endregion

        /// <summary>
        ///     Create ribbon UI for the add-in
        /// </summary>
        private void CreateRibbon()
        {
            try
            {
                // Get user role at startup for visibility control
                var userService = _services?.GetService<global::BIManage.Core.Identity.IUserService>();
                var isCompanyAdmin = userService?.IsCompanyAdmin ?? false;
                var hasAdminPrivileges = userService?.HasAdminPrivileges ?? false;

                Logger?.LogInfo($"Creating ribbon - IsCompanyAdmin: {isCompanyAdmin}, HasAdminPrivileges: {hasAdminPrivileges}");

                // Initialize RibbonVisibilityManager for button-level hiding within panels
                _ribbonVisibilityManager = new RibbonVisibilityManager(Logger, userService);
                _ribbonVisibilityManager.Initialize();

                // Get or create "ZeManage" ribbon tab
                const string tabName = "ZeManage";
                try
                {
                    this.Application.CreateRibbonTab(tabName);
                }
                catch
                {
                    // Tab already exists, ignore
                }


                // Availability class names for enabled/disabled state (grays out button)
                const string AdminOnly = "BIManage.Revit.Commands.Availability.AvailableOnlyToAdmins";
                const string CompanyAdminOnly = "BIManage.Revit.Commands.Availability.AvailableOnlyToCompanyAdmins";
                const string AdminWithDocument = "BIManage.Revit.Commands.Availability.AvailableToAdminsWithActiveDocument";
                const string WithActiveDocument = "BIManage.Revit.Commands.Availability.AvailableWithActiveDocument";
                const string AlwaysAvailable = "BIManage.Revit.Commands.Availability.AlwaysAvailable";

                // ===================================================================
                // PANEL 1: Protection
                // Pin Protection available to all users, others admin only
                // ===================================================================
                var protectionPanel = this.Application.CreateRibbonPanel(tabName, "Protection");

                // Pin Protection - available to ALL users, requires open document
                CreatePushButton(protectionPanel,
                    "PinProtectionButton",
                    "Pin\nProtection",
                    typeof(BIManageRevit.Commands.RibbonCommands.PinProtectionCommand),
                    "Lock selected elements so they can't be moved or unpinned without approval.",
                    "Apply a protected pin to the elements you currently have selected. Once locked, they can't be moved, rotated or unpinned by anyone until an admin approves it.\n\n" +
                    "Click again on a locked element to request unlock — an admin issues a one-time code.\n\n" +
                    "Requires an open model.",
                    iconName: "PinProtection",
                    availabilityClassName: WithActiveDocument);

                // Command Protection - Admin only, requires open document
                CreatePushButton(protectionPanel,
                    "CommandProtectionButton",
                    "Command\nRestriction",
                    typeof(BIManageRevit.Commands.RibbonCommands.CommandProtectionCommand),
                    "Decide what happens when users run specific Revit commands.",
                    "Set how Revit commands (Delete, Move, Copy, Sync, Export, etc.) behave for users on this model.\n\n" +
                    "For each command, choose one mode:\n" +
                    "• Notify — silently records the action\n" +
                    "• Assist — warns the user before it runs\n" +
                    "• Protect — blocks the command\n\n" +
                    "Settings sync to the cloud and apply to all users on this model.\n\n" +
                    "Admin only.",
                    iconName: "CommandProtection",
                    availabilityClassName: AdminWithDocument,
                    visibilityRole: UserRole.ProjectAdministrator);

                // Event Restriction - Admin only, requires open document
                CreatePushButton(protectionPanel,
                    "EventRestrictionButton",
                    "Event\nRestriction",
                    typeof(BIManageRevit.Commands.RibbonCommands.EventRestrictionCommand),
                    "Restrict specific Revit events — like opening central files or printing.",
                    "Set rules for specific Revit events that ZeManage tracks — for example \"Open Central File Directly\" or \"Document Exporting\".\n\n" +
                    "For each event, choose Notify, Assist, or Protect.\n\n" +
                    "Admin only.",
                    iconName: "EventRestriction",
                    availabilityClassName: AdminWithDocument,
                    visibilityRole: UserRole.ProjectAdministrator);

                // Rule Management - Admin only, requires open document
                CreatePushButton(protectionPanel,
                    "RuleManagementButton",
                    "Rule\nManagement",
                    typeof(BIManageRevit.Commands.RibbonCommands.RulesManagementCommand),
                    "Apply command protection to specific element categories.",
                    "Create rules that combine an element category with one or more Revit commands and a protection mode.\n\n" +
                    "Use this to scope Command Protection more precisely — for example, Protect \"Delete\" only when the element is in the \"Walls\" category.\n\n" +
                    "Admin only.",
                    iconName: "RuleManagement",
                    availabilityClassName: AdminWithDocument,
                    visibilityRole: UserRole.ProjectAdministrator);

                // ===================================================================
                // PANEL 2: Activity Tracker (All users)
                // ===================================================================
                var activityPanel = this.Application.CreateRibbonPanel(tabName, "Activity Tracker");

                CreatePushButton(activityPanel,
                    "SessionInfoButton",
                    "Session\nInfo",
                    typeof(BIManageRevit.Commands.RibbonCommands.SessionInformationCommand),
                    "View details about your current Revit session.",
                    "Shows information about the Revit session you're in right now — when it started, how long it has been running, the model you have open, and whether the cloud connection is healthy.\n\n" +
                    "Useful to confirm everything is being tracked correctly.",
                    iconName: "SessionInfo",
                    availabilityClassName: AlwaysAvailable);

                CreatePushButton(activityPanel,
                    "CrashRegisterButton",
                    "Crash\nRegister",
                    typeof(BIManageRevit.Commands.RibbonCommands.CrashRegisterCommand),
                    "Browse Revit sessions on this device that ended unexpectedly.",
                    "Lists previous sessions on this device where Revit crashed, was force-closed, or stopped responding.\n\n" +
                    "For each entry you can see when it happened and the model that was open at the time.",
                    iconName: "CrashRegister",
                    availabilityClassName: AlwaysAvailable);

                // Renamed from "Sync Activities" to "Model Activities"
                CreatePushButton(activityPanel,
                    "ModelActivitiesButton",
                    "Model\nActivities",
                    typeof(BIManageRevit.Commands.RibbonCommands.SyncActivitiesCommand),
                    "See who is working on this model and the recent activity log.",
                    "Shows the users currently active on this model and recent events on it.\n\n" +
                    "Filter by user, date range, or event type.",
                    iconName: "ModelActivities");

                // ===================================================================
                // PANEL 3: Health Monitor (All users)
                // ===================================================================
                var healthPanel = this.Application.CreateRibbonPanel(tabName, "Health Monitor");

                CreatePushButton(healthPanel,
                    "AnalyzeModelButton",
                    "Deep\nAnalysis",
                    typeof(BIManageRevit.Commands.RibbonCommands.AnalyzeModelMetricsCommand),
                    "Run a one-time, in-depth health check on the current model.",
                    "Performs a one-time analysis of the open model and updates the Model Health dashboard with the latest figures (file size, element counts, warnings, large families, purgeable items, views not on sheets).\n\n" +
                    "May take several minutes on large models. You'll be asked before the family-size scan starts (the slowest part).",
                    iconName: "AnalyzeModel",
                    availabilityClassName: WithActiveDocument);

                CreatePushButton(healthPanel,
                    "ModelHealthButton",
                    "Model\nHealth",
                    typeof(BIManageRevit.Commands.RibbonCommands.ModelHealthCommand),
                    "Open the health dashboard for this model.",
                    "Opens a dashboard summarising the latest health metrics for this model — file size, element counts, warnings, sync and crash history, heavy families and views.\n\n" +
                    "Run Deep Analysis from the same panel to refresh the figures on demand. Otherwise they update on the daily background scan.",
                    iconName: "ModelHealth",
                    availabilityClassName: WithActiveDocument);

                // ===================================================================
                // PANEL 4: Sync Control (All users)
                // ===================================================================
                var syncPanel = this.Application.CreateRibbonPanel(tabName, "Sync Control");

                CreatePushButton(syncPanel,
                    "SyncSettingsButton",
                    "Sync\nSettings",
                    typeof(BIManageRevit.Commands.RibbonCommands.SyncSettingsCommand),
                    "Configure how Revit synchronises with central — open views and sync limits.",
                    "Adjust how Revit handles synchronisation with the central model.\n\n" +
                    "Options include which views to keep open during sync and a limit on the number of open views allowed before sync is blocked.\n\n" +
                    "Settings are saved per device.",
                    iconName: "SyncSettings");

                CreatePushButton(syncPanel,
                    "SyncQueueButton",
                    "Sync\nQueue",
                    typeof(BIManageRevit.Commands.RibbonCommands.SyncQueueCommand),
                    "See which users are syncing this model to central, and recent sync history.",
                    "Shows real-time activity for synchronising this model with the central file.\n\n" +
                    "You can see who is syncing right now, the queue of users waiting their turn, and recent completed or failed syncs.",
                    iconName: "SyncQueue");

                // ===================================================================
                // PANEL 5: Manage Projects (Admin only - renamed from Manage Models)
                // ===================================================================
                var manageProjectsPanel = this.Application.CreateRibbonPanel(tabName, "Manage Projects");

                var registerBtn = CreatePushButton(manageProjectsPanel,
                    "RegisterModelButton",
                    "Register\nModel",
                    typeof(BIManageRevit.Commands.RibbonCommands.RegisterModelCommand),
                    "Register the current model so it can be tracked and protected.",
                    "Register the model you have open so the server recognises it. After registration, this model receives protection rules, command settings, and has its sessions and metrics tracked.\n\n" +
                    "Most models register automatically on first open. Use this only if registration didn't happen, or to re-register after a model rename.",
                    iconName: "RegisterModel",
                    availabilityClassName: WithActiveDocument); // Available to all users
                BIManageRevit.Commands.RibbonCommands.RegisterModelCommand.RibbonButton = registerBtn;

                CreatePushButton(manageProjectsPanel,
                    "GenerateOtpButton",
                    "Generate\nOTP",
                    typeof(BIManageRevit.Commands.RibbonCommands.GenerateOtpCommand),
                    "Issue a one-time code that lets a user bypass a protection.",
                    "Create a short-lived one-time code so a user can override a specific protection — for example, unpin a locked element or run a command that's normally prevented.\n\n" +
                    "Every use of the code is recorded with a screenshot.\n\n" +
                    "Admin only.",
                    iconName: "GenerateOTP",
                    availabilityClassName: AdminOnly,
                    visibilityRole: UserRole.ProjectAdministrator);

                CreatePushButton(manageProjectsPanel,
                    "DashboardButton",
                    "Dashboard",
                    typeof(BIManageRevit.Commands.RibbonCommands.DashboardCommand),
                    "Open the web dashboard in your browser.",
                    "Launches the web dashboard in your default browser, where you can manage projects, users, rules and review audit history across all your models.\n\n" +
                    "Admin only — requires you to be signed in.",
                    iconName: "Dashboard",
                    availabilityClassName: AdminOnly,
                    visibilityRole: UserRole.ProjectAdministrator);

                // ===================================================================
                // PANEL 6: Addons (optional — only if BIManage.Addons.dll is present)
                // ===================================================================
                var addonDllPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Application).Assembly.Location) ?? "",
                    "BIManage.Addons.dll");

                bool includeAddons = System.IO.File.Exists(addonDllPath);
                if (includeAddons)
                {
                    var addonsPanel = this.Application.CreateRibbonPanel(tabName, "Addons");

                    CreatePushButton(addonsPanel,
                        "NwcExportButton", "NWC\nExport",
                        addonDllPath, "BIManage.Addons.Commands.NwcExportCommand",
                        "Export 3D views to Navisworks (.nwc) in batch.",
                        "Export one or several 3D views from this model to Navisworks Cache (.nwc) files.\n\n" +
                        "Pick the views, choose the output folder and naming pattern, and use the Navisworks export settings already configured in this model.\n\n" +
                        "Requires at least one 3D view in the open model.",
                        iconName: "NwcExport",
                        availabilityClassName: AlwaysAvailable);

                    CreatePushButton(addonsPanel,
                        "LinkRemapperButton", "Link\nRemapper",
                        addonDllPath, "BIManage.Addons.Commands.LinkRemapperCommand",
                        "Repair and reload Revit links in bulk.",
                        "Inspect every linked RVT in this model, see its current path status, and remap broken paths to a new folder or to BIM 360 / Autodesk Docs.\n\n" +
                        "Reload links without leaving the session.",
                        iconName: "LinkRemapper",
                        availabilityClassName: WithActiveDocument);

                    Logger?.LogInfo("Addons panel created (BIManage.Addons.dll found)");
                }
                else
                {
                    Logger?.LogInfo("Addons panel skipped (BIManage.Addons.dll not found)");
                }

                // ===================================================================
                // PANEL 7: AI
                // ===================================================================
                var aiPanel = this.Application.CreateRibbonPanel(tabName, "AI");

                CreatePushButton(aiPanel,
                    "ZeAiButton",
                    "Ze AI",
                    typeof(BIManageRevit.Commands.RibbonCommands.ZestAiCommand),
                    "Ask plain-language questions about your current model.",
                    "Open a chat assistant that can answer questions about the model you have open.\n\n" +
                    "Examples:\n" +
                    "• \"How many ducts are in this view?\"\n" +
                    "• \"List the families larger than 5 MB.\"\n" +
                    "• \"Which worksets does the current selection use?\"\n\n" +
                    "Answers are based on the current model only.",
                    iconName: "ZedAi");

                // ===================================================================
                // PANEL 8: Support (order: Device, Account, Support)
                // ===================================================================
                var supportPanel = this.Application.CreateRibbonPanel(tabName, "Support");

                var deviceBtn = CreatePushButton(supportPanel,
                    "DeviceStatusButton",
                    "Status",
                    typeof(BIManageRevit.Commands.RibbonCommands.DeviceStatusCommand),
                    "Check whether this device is registered and connected.",
                    "Shows whether this device is registered with the server and connected.\n\n" +
                    "• Green — registered and active\n" +
                    "• Yellow — registered, license in passive mode (limited features)\n" +
                    "• Red — not registered, or license breached\n\n" +
                    "Click to see the full status, copy your device ID, or enter a license key.",
                    iconName: "DeviceRed",
                    availabilityClassName: AlwaysAvailable);
                // Store button reference for dynamic icon swap
                if (deviceBtn != null)
                    BIManageRevit.Commands.RibbonCommands.DeviceStatusCommand.RibbonButton = deviceBtn;

                CreatePushButton(supportPanel,
                    "SignInButton",
                    "Account",
                    typeof(BIManageRevit.Commands.RibbonCommands.SignInCommand),
                    "Sign in to your account.",
                    "Sign in with your work email and password.\n\n" +
                    "You need to be signed in to use admin-only features (rules, OTP, dashboard) and to sync activity to the cloud.",
                    iconName: "SignIn",
                    availabilityClassName: AlwaysAvailable);

                CreatePushButton(supportPanel,
                    "SupportButton",
                    "Support",
                    typeof(BIManageRevit.Commands.RibbonCommands.SupportCommand),
                    "Contact support or open documentation.",
                    "Open a support ticket — diagnostics are attached automatically — or browse documentation, FAQs and release notes.",
                    iconName: "Support",
                    availabilityClassName: AlwaysAvailable);


                // ===================================================================
                // DEVELOPER PANEL (Controlled by FeatureToggleService)
                // All buttons visible to all users when panel is enabled
                // ===================================================================
                var devMode = Environment.GetEnvironmentVariable("BIMANAGE_DEV_MODE");
                var isDeveloper = devMode == "1" || devMode?.ToLowerInvariant() == "true";

                Logger?.LogInfo($"Checking developer mode: BIMANAGE_DEV_MODE = '{devMode ?? "(null)"}', isDeveloper = {isDeveloper}");

                if (isDeveloper)
                {
                    Logger?.LogInfo("Developer Mode Enabled - Developer Panel will be Active");
                    var devPanel = this.Application.CreateRibbonPanel(tabName, "Developer");

                    CreatePushButton(devPanel,
                        "ExecuteButton",
                        "Execute",
                        typeof(StartupCommand),
                        "Execute startup command",
                        "Manually trigger the BIManage startup / bootstrap sequence.\n\n" +
                        "Useful for developers testing:\n" +
                        "• Service initialization order\n" +
                        "• Deferred DI registrations\n" +
                        "• Re-wiring event handlers without restarting Revit\n\n" +
                        "Company Administrator only.",
                        iconName: "Execute",
                        availabilityClassName: CompanyAdminOnly,
                        visibilityRole: UserRole.CompanyAdministrator); // SuperAdmin only

                    CreatePushButton(devPanel,
                        "DeviceRegistrationButton",
                        "Register\nDevice",
                        typeof(BIManageRevit.Commands.RibbonCommands.DeviceRegistrationCommand),
                        "Register this device with a license key to enable API sync",
                        "Registers this device with the BIManage API server.\n\n" +
                        "Required for:\n" +
                        "• Session sync to cloud\n" +
                        "• Model metrics sync\n" +
                        "• Real-time collaboration\n\n" +
                        "You will need a valid license key.",
                        iconName: "Execute",
                        availabilityClassName: AlwaysAvailable);

                    CreatePushButton(devPanel,
                        "TestRulesButton",
                        "Test\nRules",
                        typeof(BIManageRevit.Commands.RibbonCommands.TestRulesCommand),
                        "Test rule evaluation on selected elements",
                        "Run the rule evaluation engine against the current selection and display the result.\n\n" +
                        "• Lists each matching rule and mode (Notify/Guide/Prevent)\n" +
                        "• Shows cache source (memory / SQLite / API)\n" +
                        "• Reports evaluation time per rule\n\n" +
                        "Used to validate that authored rules behave as expected.",
                        iconName: "TestRules",
                        availabilityClassName: AlwaysAvailable);

                    CreatePushButton(devPanel,
                        "TestScreenshotButton",
                        "Test\nScreenshot",
                        typeof(BIManageRevit.Commands.RibbonCommands.TestScreenshotCommand),
                        "Test screenshot capture functionality",
                        "Capture a screenshot using the evidence pipeline and show the result.\n\n" +
                        "Verifies:\n" +
                        "• Win32 GDI capture of the Revit window\n" +
                        "• SHA-256 hashing and dedup\n" +
                        "• Base64 encoding and temp-file persistence\n" +
                        "• Queued upload to the evidence API\n\n" +
                        "Useful for diagnosing evidence upload issues.",
                        iconName: "TestScreenshot",
                        availabilityClassName: AlwaysAvailable);

                    CreatePushButton(devPanel,
                        "CapturePeriodicButton",
                        "Capture\nPeriodic",
                        typeof(BIManageRevit.Commands.RibbonCommands.CapturePeriodicMetricsCommand),
                        "Manually capture periodic model metrics",
                        "Captures periodic model metrics on demand (normally collected daily at 2 AM):\n• Total elements count\n• Model/annotative elements\n• In-place families\n• Unplaced/unenclosed rooms\n• Views not on sheets\n• Walls/pipes/ducts not connected (from warnings)\n\nThis should complete in a few seconds.",
                        iconName: "CapturePeriodic",
                        availabilityClassName: AlwaysAvailable);

                    CreatePushButton(devPanel,
                        "SignalRDiagnosticsButton",
                        "SignalR\nDiag",
                        typeof(BIManageRevit.Commands.RibbonCommands.SignalRDiagnosticsCommand),
                        "SignalR connection diagnostics",
                        "View SignalR connection state, groups, listeners, and perform connectivity tests.\n\n" +
                        "Actions:\n" +
                        "• Test Connection — attempt to connect\n" +
                        "• Force Reconnect — disconnect and reconnect\n" +
                        "• Copy Info — copy diagnostics to clipboard",
                        iconName: "Execute",
                        availabilityClassName: AlwaysAvailable);

                    // AI Terminal smoke tests (Phase 3a/3d). Visible only in dev mode.
                    CreatePushButton(devPanel,
                        "TestSayHelloButton",
                        "Test\nSayHello",
                        typeof(BIManageRevit.Commands.RibbonCommands.TestSayHelloCommand),
                        "AI Terminal — Phase 3a smoke test (SayHello tool through registry + dispatcher).",
                        "AI Terminal Phase 3a / 3c smoke test.\n\n" +
                        "Click to invoke the SayHello tool through the full pipeline:\n" +
                        "• ToolRegistry.DiscoverAndCreate (reflection scan)\n" +
                        "• ToolSchemaBuilder.Build (OpenAI function definitions)\n" +
                        "• ToolDispatcher.Dispatch (JSON args → tool execution → JSON result)\n" +
                        "• ExternalEventCommandBase.IsCalledFromConstructionThread routing\n\n" +
                        "Expected: two TaskDialogs in sequence — the greeting from SayHelloEventHandler, " +
                        "then a result summary confirming the foundation works end-to-end.",
                        iconName: "ZedAi",
                        availabilityClassName: AlwaysAvailable);

                    CreatePushButton(devPanel,
                        "TestSendCodeButton",
                        "Test\nSendCode",
                        typeof(BIManageRevit.Commands.RibbonCommands.TestSendCodeCommand),
                        "AI Terminal — Phase 3d safety smoke test (safe + unsafe snippets).",
                        "AI Terminal Phase 3d safety smoke test.\n\n" +
                        "Runs two hand-crafted snippets through the full send_code_to_revit pipeline:\n" +
                        "  1. SAFE: counts walls and returns a JSON object.\n" +
                        "  2. UNSAFE: tries to create a Transaction and delete an element.\n\n" +
                        "Expected: Test 1 returns success with a wall count; Test 2 is rejected by " +
                        "the static analyzer (SAFE020_NoTransaction) before any compile or execution.\n\n" +
                        "Audit entries for both attempts are written to " +
                        "%LOCALAPPDATA%\\BIManageRevit\\AI\\Logs\\ai_script_executions_YYYYMMDD.log.",
                        iconName: "ZedAi",
                        availabilityClassName: AlwaysAvailable);

                    CreatePushButton(devPanel,
                        "AdversarialSafetyTestButton",
                        "Test\nSafety",
                        typeof(BIManageRevit.Commands.RibbonCommands.AdversarialSafetyTestCommand),
                        "AI Terminal — runs ~20 adversarial snippets through the safety analyzer.",
                        "AI Terminal Phase 3d adversarial test suite.\n\n" +
                        "Runs roughly 20 hand-crafted snippets against the safety analyzer covering:\n" +
                        "  • Direct mutation (Transaction, Delete, Parameter.Set)\n" +
                        "  • Reflection bypasses (Assembly.Load, Type.GetType, Activator)\n" +
                        "  • File/network/process attempts\n" +
                        "  • Language-feature bypasses (unsafe)\n" +
                        "  • Legitimate code that MUST still pass\n\n" +
                        "Reports per-test pass/fail with the rule id that fired. Failures indicate " +
                        "either a security gap or an over-restriction — both worth triaging.",
                        iconName: "ZedAi",
                        availabilityClassName: AlwaysAvailable);

                    Logger?.LogInfo("Ribbon UI created with 9 panels (including Developer)");
                }
                else
                {
                    Logger?.LogInfo("Ribbon UI created with 8 panels (Developer panel disabled)");
                }

                // Set initial button visibility based on current user role
                _ribbonVisibilityManager?.RefreshVisibility();
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to create ribbon UI: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Helper method to create push buttons with consistent styling
        /// </summary>
        /// <param name="availabilityClassName">Full class name of IExternalCommandAvailability implementation for enabled/disabled state</param>
        /// <param name="visibilityRole">Minimum role required to SEE the button (null = visible to all)</param>
        private PushButton CreatePushButton(
            RibbonPanel panel,
            string buttonName,
            string buttonText,
            Type commandType,
            string toolTip,
            string longDescription = null,
            string iconName = null,
            string availabilityClassName = null,
            UserRole? visibilityRole = null)
        {
            try
            {
                var buttonData = new PushButtonData(
                    buttonName,
                    buttonText,
                    commandType.Assembly.Location,
                    commandType.FullName);

                // Set availability class for enabled/disabled state (role-based)
                // Revit automatically polls these classes on Idling events to update button state
                if (!string.IsNullOrEmpty(availabilityClassName))
                {
                    buttonData.AvailabilityClassName = availabilityClassName;
                }

                var button = panel.AddItem(buttonData) as PushButton;
                if (button != null)
                {
                    // Use specific icon if provided, otherwise fall back to default RibbonIcon
                    var iconFolder = GetIconFolder();
                    var icon16 = string.IsNullOrEmpty(iconName) ? "RibbonIcon_16" : $"{iconName}_16";
                    var icon32 = string.IsNullOrEmpty(iconName) ? "RibbonIcon_32" : $"{iconName}_32";

                    // Try to load theme-aware icons with fallback
                    try
                    {
                        button.Image = new System.Windows.Media.Imaging.BitmapImage(
                            new Uri($"pack://application:,,,/BIManageRevit;component/Resources/{iconFolder}/{icon16}.png"));
                        button.LargeImage = new System.Windows.Media.Imaging.BitmapImage(
                            new Uri($"pack://application:,,,/BIManageRevit;component/Resources/{iconFolder}/{icon32}.png"));
                    }
                    catch (Exception iconEx)
                    {
                        Logger?.LogWarning($"Failed to load icon '{iconName}' from {iconFolder}: {iconEx.Message}. Trying default folder.");
                        try
                        {
                            // Fallback to Icons folder (light theme)
                            button.Image = new System.Windows.Media.Imaging.BitmapImage(
                                new Uri($"pack://application:,,,/BIManageRevit;component/Resources/Icons/{icon16}.png"));
                            button.LargeImage = new System.Windows.Media.Imaging.BitmapImage(
                                new Uri($"pack://application:,,,/BIManageRevit;component/Resources/Icons/{icon32}.png"));
                        }
                        catch
                        {
                            Logger?.LogWarning($"Default icon also failed for button '{buttonName}'. Button will have no icon.");
                        }
                    }

                    // Track button for theme-change icon updates
                    _ribbonButtons.Add((button, iconName));

                    button.ToolTip = toolTip;

                    if (!string.IsNullOrEmpty(longDescription))
                    {
                        button.LongDescription = longDescription;
                    }

                    // Register with visibility manager for role-based hiding
                    if (visibilityRole.HasValue && _ribbonVisibilityManager != null)
                    {
                        _ribbonVisibilityManager.RegisterButton(button, visibilityRole.Value);

                        // Initially hide until role is verified
                        button.Visible = false;
                    }
                }

                return button;
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to create button '{buttonName}': {ex.Message}", ex);
                return null; // Return null but don't throw - allow other buttons to load
            }
        }

        /// <summary>
        /// String-based overload for creating push buttons from external assemblies (e.g. BIManage.Addons.dll).
        /// No compile-time reference required — uses assembly path + class name strings.
        /// </summary>
        private PushButton CreatePushButton(
            RibbonPanel panel,
            string buttonName,
            string buttonText,
            string assemblyPath,
            string className,
            string toolTip,
            string longDescription = null,
            string iconName = null,
            string availabilityClassName = null,
            UserRole? visibilityRole = null)
        {
            try
            {
                var buttonData = new PushButtonData(
                    buttonName,
                    buttonText,
                    assemblyPath,
                    className);

                if (!string.IsNullOrEmpty(availabilityClassName))
                {
                    buttonData.AvailabilityClassName = availabilityClassName;
                }

                var button = panel.AddItem(buttonData) as PushButton;
                if (button != null)
                {
                    var iconFolder = GetIconFolder();
                    var icon16 = string.IsNullOrEmpty(iconName) ? "RibbonIcon_16" : $"{iconName}_16";
                    var icon32 = string.IsNullOrEmpty(iconName) ? "RibbonIcon_32" : $"{iconName}_32";

                    try
                    {
                        button.Image = new System.Windows.Media.Imaging.BitmapImage(
                            new Uri($"pack://application:,,,/BIManageRevit;component/Resources/{iconFolder}/{icon16}.png"));
                        button.LargeImage = new System.Windows.Media.Imaging.BitmapImage(
                            new Uri($"pack://application:,,,/BIManageRevit;component/Resources/{iconFolder}/{icon32}.png"));
                    }
                    catch (Exception iconEx)
                    {
                        Logger?.LogWarning($"Failed to load icon '{iconName}' from {iconFolder}: {iconEx.Message}. Trying default folder.");
                        try
                        {
                            button.Image = new System.Windows.Media.Imaging.BitmapImage(
                                new Uri($"pack://application:,,,/BIManageRevit;component/Resources/Icons/{icon16}.png"));
                            button.LargeImage = new System.Windows.Media.Imaging.BitmapImage(
                                new Uri($"pack://application:,,,/BIManageRevit;component/Resources/Icons/{icon32}.png"));
                        }
                        catch
                        {
                            Logger?.LogWarning($"Default icon also failed for button '{buttonName}'. Button will have no icon.");
                        }
                    }

                    // Track button for theme-change icon updates
                    _ribbonButtons.Add((button, iconName));

                    button.ToolTip = toolTip;

                    if (!string.IsNullOrEmpty(longDescription))
                    {
                        button.LongDescription = longDescription;
                    }

                    if (visibilityRole.HasValue && _ribbonVisibilityManager != null)
                    {
                        _ribbonVisibilityManager.RegisterButton(button, visibilityRole.Value);
                        button.Visible = false;
                    }
                }

                return button;
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to create addon button '{buttonName}': {ex.Message}", ex);
                return null;
            }
        }
    }
}
