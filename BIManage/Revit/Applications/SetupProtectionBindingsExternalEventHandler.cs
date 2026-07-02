using System;
using System.Diagnostics;
using Autodesk.Revit.UI;
using BIManage.Core.Evidence;
using BIManage.Revit.PinProtection;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.DependencyInjection;
using BIManage.Infrastructure.Logging;
using BIManage.Core.Rules;
using BIManage.Revit.Commands;
using BIManage.Revit.Commands.Bindings;
using BIManage.Revit.Context;
using BIManage.Revit.EventRegistry;

namespace BIManage.Revit.Applications
{
    /// <summary>
    /// Handles deferred command binding registration via ExternalEvent
    /// Ensures bindings are registered with document context on main thread
    /// </summary>
    public class SetupProtectionBindingsExternalEventHandler : IExternalEventHandler
    {
        private readonly ServiceRegistry _services;
        private readonly ILogger _logger;

        public SetupProtectionBindingsExternalEventHandler(
            ServiceRegistry services,
            ILogger logger)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _logger = logger;
        }

        public void Execute(UIApplication app)
        {
            var totalSw = Stopwatch.StartNew();
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    _logger?.LogWarning("No active document - skipping protection binding setup");
                    return;
                }

                _logger?.LogInfo($"[TIMING] Setting up protection bindings for document: {doc.Title}");

                // 1. Trigger lazy initialization of event registries
                var registrySw = Stopwatch.StartNew();
                var commandRegistry = _services?.GetService<ICommandEventRegistry>();
                commandRegistry?.TryInitialize();

                var idlingRegistry = _services?.GetService<IIdlingEventRegistry>();
                idlingRegistry?.TryInitialize();

                var uiTrackingRegistry = _services?.GetService<IUITrackingEventRegistry>();
                uiTrackingRegistry?.TryInitialize();
                registrySw.Stop();
                _logger?.LogInfo($"[TIMING] Event registries initialization: {registrySw.ElapsedMilliseconds}ms");

                // 1.5. Register additional command bindings from rules
                var ruleBindingSw = Stopwatch.StartNew();
                RegisterRuleCommandBindings(app);
                ruleBindingSw.Stop();
                _logger?.LogInfo($"[TIMING] Rule command binding registration: {ruleBindingSw.ElapsedMilliseconds}ms");

                // 2. Enable Pin Protection
                var pinSw = Stopwatch.StartNew();
                var pinProtectionService = _services?.GetService<PinProtectionService>();
                if (pinProtectionService != null && !pinProtectionService.IsEnabled)
                {
                    pinProtectionService.Enable(app);
                    _logger?.LogInfo("Pin Protection enabled");
                }
                pinSw.Stop();
                _logger?.LogInfo($"[TIMING] Pin Protection setup: {pinSw.ElapsedMilliseconds}ms");

                // 3. Initialize Evidence Capture (requires UIApplication)
                var evidenceSw = Stopwatch.StartNew();
                InitializeEvidenceCapture(app);
                evidenceSw.Stop();
                _logger?.LogInfo($"[TIMING] Evidence Capture initialization: {evidenceSw.ElapsedMilliseconds}ms");

                // 4. Initialize CommandProtectionBinding (database-driven command protection)
                var cmdProtSw = Stopwatch.StartNew();
                InitializeCommandProtectionBinding(app);
                cmdProtSw.Stop();
                _logger?.LogInfo($"[TIMING] CommandProtectionBinding initialization: {cmdProtSw.ElapsedMilliseconds}ms");

                // 5. Load Event Protection settings from database
                var eventProtSw = Stopwatch.StartNew();
                LoadEventProtectionSettings();
                eventProtSw.Stop();
                _logger?.LogInfo($"[TIMING] Event protection settings load: {eventProtSw.ElapsedMilliseconds}ms");

                totalSw.Stop();
                _logger?.LogInfo($"[TIMING] Protection bindings setup complete for document: {doc.Title} (TOTAL: {totalSw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                totalSw.Stop();
                _logger?.LogError($"Failed to setup protection bindings ({totalSw.ElapsedMilliseconds}ms): {ex.Message}", ex);
            }
        }

        public string GetName() => "SetupProtectionBindingsExternalEvent";

        private void InitializeEvidenceCapture(UIApplication uiApp)
        {
            try
            {
                var evidenceRepository = _services?.GetService<global::BIManage.Data.SQLite.EvidenceRepository>();
                if (evidenceRepository == null)
                {
                    _logger?.LogWarning("EvidenceRepository not registered, skipping evidence capture initialization");
                    return;
                }

                // Check if already registered to avoid duplicate registration
                var existingScreenshotService = _services?.GetService<global::BIManage.Core.Evidence.IScreenshotService>();
                if (existingScreenshotService != null)
                {
                    _logger?.LogDebug("Evidence capture services already registered, skipping");
                    return;
                }

                // Create and register ScreenshotService
                var screenshotService = new global::BIManage.Core.Evidence.ScreenshotService(uiApp, _logger);
                _services?.RegisterSingleton<global::BIManage.Core.Evidence.IScreenshotService>(screenshotService);

                // Create and register EvidenceUploadQueue (uses AuthenticatedHttpClient for JWT auth)
                var authenticatedHttpClient = _services?.GetService<global::BIManage.Infrastructure.Auth.AuthenticatedHttpClient>();
                if (authenticatedHttpClient != null)
                {
                    var auditLogSyncService = _services?.GetService<global::BIManage.Infrastructure.Api.AuditLogSyncService>();
                    var offlineQueueRepo = _services?.GetService<global::BIManage.Data.SQLite.OfflineQueueRepository>();
                    var mailDispatchService = _services?.GetService<global::BIManage.Infrastructure.Api.AuditLogMailDispatchService>();
                    var auditRepo = _services?.GetService<global::BIManage.Data.SQLite.AuditRepository>();
                    var evidenceUploadQueue = new global::BIManage.Core.Evidence.EvidenceUploadQueue(
                        evidenceRepository,
                        screenshotService,
                        authenticatedHttpClient,
                        _logger,
                        auditLogSyncService,
                        offlineQueueRepo,
                        mailDispatchService,
                        auditRepo);
                    _services?.RegisterSingleton<global::BIManage.Core.Evidence.EvidenceUploadQueue>(evidenceUploadQueue);
                }
                else
                {
                    _logger?.LogWarning("AuthenticatedHttpClient not available, EvidenceUploadQueue will not be initialized");
                }

                _logger?.LogInfo("Evidence capture services initialized (ScreenshotService, EvidenceUploadQueue)");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to initialize evidence capture: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Register additional command bindings for commands referenced in rules
        /// but not already bound by GetCoreCommands() or individual bindings.
        /// </summary>
        private void RegisterRuleCommandBindings(UIApplication uiApp)
        {
            try
            {
                var ruleService = _services?.GetService<IRuleService>();
                var commandService = _services?.GetService<ICommandInterceptionService>();

                if (ruleService == null || commandService == null)
                {
                    _logger?.LogDebug("RuleService or CommandInterceptionService not available, skipping rule command bindings");
                    return;
                }

                var ruleCommands = ruleService.GetDistinctRuleCommandIds();
                if (ruleCommands.Count == 0)
                {
                    _logger?.LogDebug("No rule commands to register additional bindings for");
                    return;
                }

                commandService.RegisterRuleCommandBindings(uiApp, ruleCommands);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to register rule command bindings: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Load event protection settings from database into EventProtectionService cache
        /// </summary>
        private void LoadEventProtectionSettings()
        {
            try
            {
                var eventProtectionService = _services?.GetService<global::BIManage.Revit.Protection.IEventProtectionService>();
                if (eventProtectionService == null)
                {
                    _logger?.LogDebug("EventProtectionService not registered, skipping event protection settings load");
                    return;
                }

                eventProtectionService.LoadSettingsFromDatabase(projectId: null);
                _logger?.LogInfo("Event protection settings loaded from database");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load event protection settings: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Initialize CommandProtectionBinding for database-driven command protection
        /// Uses BeforeExecuted event only, loads settings from command_settings table
        /// </summary>
        private void InitializeCommandProtectionBinding(UIApplication uiApp)
        {
            try
            {
                // Check if already registered to avoid duplicate registration
                var existingBinding = _services?.GetService<CommandProtectionBinding>();
                if (existingBinding != null)
                {
                    // Already registered - just refresh commands (settings may have changed via SignalR)
                    existingBinding.LoadAndRegisterCommands();
                    _logger?.LogDebug("CommandProtectionBinding already registered - refreshed commands");
                    return;
                }

                // Get required dependencies
                var commandProtectionRepository = _services?.GetService<CommandProtectionRepository>();
                if (commandProtectionRepository == null)
                {
                    _logger?.LogWarning("CommandProtectionRepository not registered, skipping CommandProtectionBinding initialization");
                    return;
                }

                var userService = _services?.GetService<global::BIManage.Core.Identity.IUserService>();
                var otpRepository = _services?.GetService<OtpRepository>();
                var auditRepository = _services?.GetService<AuditRepository>();
                var evidenceRepository = _services?.GetService<EvidenceRepository>();

                // Create getter functions for services that may be initialized later
                Func<IScreenshotService?> screenshotServiceGetter = () => _services?.GetService<IScreenshotService>();
                Func<IRevitContext?> revitContextGetter = () => _services?.GetService<IRevitContext>();
                var cacheRepository = _services?.GetService<CacheRepository>();
                var commandInterceptionService = _services?.GetService<ICommandInterceptionService>();

                // Create and register CommandProtectionBinding
                var commandProtectionBinding = new CommandProtectionBinding(
                    uiApp,
                    _logger,
                    commandProtectionRepository,
                    isAdminCheck: () => userService?.HasAdminPrivileges ?? false,
                    otpRepository,
                    auditRepository,
                    evidenceRepository,
                    screenshotServiceGetter,
                    revitContextGetter,
                    cacheRepository,
                    commandInterceptionService);

                _services?.RegisterSingleton(commandProtectionBinding);

                // Load and register enabled commands from database
                commandProtectionBinding.LoadAndRegisterCommands();

                _logger?.LogInfo($"CommandProtectionBinding initialized with {commandProtectionBinding.ActiveBindingCount} active command bindings");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to initialize CommandProtectionBinding: {ex.Message}", ex);
            }
        }
    }
}
