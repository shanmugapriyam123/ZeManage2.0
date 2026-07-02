using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using BIManage.Core.Evidence;
using BIManage.Core.Features;
using BIManage.Core.Metrics;
using BIManage.Revit.PinProtection;
using BIManage.Core.Protection;
using BIManage.Core.Rules;
using BIManage.Data.Caching;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.DependencyInjection;
using BIManage.Infrastructure.Logging; 
using BIManage.Revit.Commands;
using BIManage.Revit.Commands.Bindings;
using BIManage.Revit.Context;
using RevitContext = BIManage.Revit.Context.RevitContext;
using BIManage.Revit.Diagnostics;
using BIManage.Revit.Execution;
using BIManage.Revit.EventRegistry;
using BIManage.Revit.Guards;
using BIManage.Revit.Idling;
using BIManage.Revit.Productivity;
using BIManage.Revit.Protection;
using BIManage.Revit.Services;
using BIManage.Revit.Session;
using BIManage.Revit.UITracking;
using BIManage.Infrastructure.SignalR.Core;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Infrastructure.SignalR.Groups;
using BIManage.Infrastructure.SignalR.Listeners;
using BIManage.Infrastructure.Threading;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Api;
using BIManage.AI.Interfaces;
using BIManage.AI.Knowledge;
using BIManage.AI.Providers;

namespace BIManage.Revit.Applications
{
    /// <summary>
    ///     Handles application bootstrap responsibilities and wires core services.
    /// </summary>
    public class RevitBootstrapper
    {
        public InitResult Initialize(InitContext context)
        {
            if (!BootstrapGuards.Validate(context, out var guardFailure))
            {
                return InitResult.Failure(guardFailure ?? "Bootstrap validation failed.");
            }

            // Enable TLS 1.2 for all HTTP connections before any API calls.
            // .NET Framework 4.8 defaults to TLS 1.0/1.1; modern servers (including our API
            // at http://10.10.40.220) require TLS 1.2+.
            System.Net.ServicePointManager.SecurityProtocol |=
                System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;

            // SSL validation is configured per-service via SslValidationPolicy (registered in RegisterCore).
            // ServicePointManager global is set there after the feature toggle service is available.

            var services = new ServiceRegistry();

            try
            {
                Directory.CreateDirectory(context.LogDirectory);

                // Bootstrap health tracker — tracks which service groups initialized
                var healthTracker = new Infrastructure.DependencyInjection.BootstrapHealthTracker();
                services.RegisterSingleton<Infrastructure.DependencyInjection.BootstrapHealthTracker>(healthTracker);

                // Register services in dependency order. Each phase is timed so a slow
                // bootstrap step shows up directly in the log as [Timing] Bootstrap.<phase>.
                // Auth services MUST be registered before SignalR so the connection manager
                // can resolve AuthTokenManager and authenticate the hub connection with a JWT.
                var phaseSw = new System.Diagnostics.Stopwatch();
                void Phase(string name, Action register)
                {
                    phaseSw.Restart();
                    register();
                    phaseSw.Stop();
                    services.GetService<ILogger>()?.LogInfo($"[Timing] Bootstrap.{name} dur={phaseSw.ElapsedMilliseconds}ms");
                }

                Phase("RegisterLogging", () => RegisterLogging(services, context));
                Phase("RegisterCore", () => RegisterCore(services));
                Phase("RegisterIdentity", () => RegisterIdentity(services));
                Phase("RegisterPersistence", () => RegisterPersistence(services, context));
                Phase("RegisterProtection", () => RegisterProtection(services, context));
                Phase("RegisterRuleEngine", () => RegisterRuleEngine(services, context));
                Phase("RegisterProductivity", () => RegisterProductivity(services));
                Phase("RegisterExecution", () => RegisterExecution(services));
                Phase("RegisterContext", () => RegisterContext(services, context));
                Phase("RegisterSession", () => RegisterSession(services));
                Phase("RegisterUITracking", () => RegisterUITracking(services));
                Phase("RegisterCommandAndIdling", () => RegisterCommandAndIdling(services, context));
                Phase("RegisterEventSystem", () => RegisterEventSystem(services, context));
                Phase("RegisterDiagnostics", () => RegisterDiagnostics(services));
                Phase("RegisterApiServices", () => RegisterApiServices(services));
                Phase("RegisterSignalR", () => RegisterSignalR(services, context));
                Phase("RegisterAI", () => RegisterAI(services));

                // Initialize events and health
                phaseSw.Restart();
                var eventRegistry = services.GetRequiredService<IEventRegistry>();
                eventRegistry.RegisterAll();
                phaseSw.Stop();
                services.GetService<ILogger>()?.LogInfo($"[Timing] Bootstrap.EventRegistry.RegisterAll dur={phaseSw.ElapsedMilliseconds}ms");

                phaseSw.Restart();
                var healthMonitor = services.GetRequiredService<IRevitHealthMonitor>();
                healthMonitor.RecordStartup();
                phaseSw.Stop();
                services.GetService<ILogger>()?.LogInfo($"[Timing] Bootstrap.HealthMonitor.RecordStartup dur={phaseSw.ElapsedMilliseconds}ms");

                var logger = services.GetRequiredService<ILogger>();
                var revitContext = services.GetRequiredService<IRevitContext>();
                logger?.LogInfo($"Revit session initialized with SessionId: {revitContext.SessionId}");

                // Initialize persistence layer session tracking (non-blocking to prevent Revit freeze)
                TaskRunner.FireAndForget(
                    InitializePersistenceSession(services, context),
                    logger,
                    "InitializePersistenceSession");

                // Note: Opening completion time will be updated in OnApplicationInitialized
                // when Revit is ready for user input (not here during bootstrap)

                return InitResult.Success(services);
            }
            catch (Exception ex)
            {
                var logger = services.GetService<ILogger>();
                logger?.LogError($"Bootstrap failed: {ex.Message}", ex);
                services.Dispose();
                return InitResult.Failure($"Failed to initialize BIManageRevit: {ex.Message}", ex);
            }
        }

        private void RegisterLogging(ServiceRegistry services, InitContext context)
        {
            var logger = new FileLogger(context.LogDirectory);
            services.RegisterSingleton<ILogger>(logger);
        }

        private void RegisterCore(ServiceRegistry services)
        {
            services.RegisterSingleton<IFeatureToggleService>(
                new FeatureToggleService(services.GetRequiredService<ILogger>()));

            services.RegisterSingleton<IRevitSafetyGuards>(
                new RevitSafetyGuards(services.GetRequiredService<ILogger>()));

            // Centralized SSL validation policy — off by default (dev server uses self-signed certs).
            // Enable "StrictSslValidation" feature flag when a production TLS certificate is in place.
            var sslPolicy = new BIManage.Infrastructure.Network.SslValidationPolicy(
                services.GetRequiredService<IFeatureToggleService>());
            services.RegisterSingleton<BIManage.Infrastructure.Network.SslValidationPolicy>(sslPolicy);

            // Intentionally NOT assigning ServicePointManager.ServerCertificateValidationCallback.
            // That property is a single-delegate slot, not an event: `=` overwrites whatever
            // callback was installed by other add-ins (Autodesk Collaborate/Skyscraper cloud
            // worksharing, Desktop Connector, etc.), and BIManage loads after them in Revit's
            // add-in order. Overwriting their callback broke cloud-hosted linked-file loading
            // because their process-wide cert validation/pinning was lost.
            // Every BIManage HttpClient sets its own HttpClientHandler.ServerCertificateCustomValidationCallback
            // per-instance (see AuthApiService, AuthenticatedHttpClient, RevitApiService,
            // SignalRService.CreateHttpClient), so the global callback is not needed for us.
        }

        private void RegisterIdentity(ServiceRegistry services)
        {
            var logger = services.GetRequiredService<ILogger>();

            // Register UserService for identity and role management
            var userService = new BIManage.Core.Identity.UserService(logger);
            services.RegisterSingleton<BIManage.Core.Identity.IUserService>(userService);

            logger.LogInfo("Identity services registered (UserService)");
        }

        private void RegisterPersistence(ServiceRegistry services, InitContext context)
        {
            var logger = services.GetRequiredService<ILogger>();
            var databasePath = Path.Combine(context.LogDirectory, "bimanage.db");

            // Schema-migration fast path: when the previous successful migration left
            // a marker file proving this exact plugin version already verified the DB at
            // the current target schema version, skip the whole migration. SchemaMigration
            // sets SchemaReady=true synchronously and we never open SQLite during bootstrap
            // — the cold-start cost is pushed off the OnStartup critical path to the
            // first real DB operation later. Saves ~350 ms on every warm launch where the
            // schema is already at target and the plugin hasn't been upgraded.
            //
            // On a cache miss (first run, plugin upgrade, DB reset, marker corruption),
            // falls back to the original background-thread KickOff exactly as before.
            var pluginInfoVersion = System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion
                ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "unknown";

            if (SchemaMigration.TryFastPath(databasePath, pluginInfoVersion, logger))
            {
                logger.LogInfo("Schema fast path hit — skipping background migration");
            }
            else
            {
                logger.LogInfo("Kicking off schema migration on background thread...");
                SchemaMigration.KickOff(databasePath, logger);
            }

            // Register persistence repositories
            var sessionRepository = new SessionRepository(databasePath, logger);
            services.RegisterSingleton<SessionRepository>(sessionRepository);

            var syncRepository = new SyncRepository(databasePath, logger);
            services.RegisterSingleton<SyncRepository>(syncRepository);

            var eventRepository = new EventRepository(databasePath, logger);
            services.RegisterSingleton<EventRepository>(eventRepository);

            var offlineQueueRepository = new OfflineQueueRepository(databasePath, logger);
            services.RegisterSingleton<OfflineQueueRepository>(offlineQueueRepository);

            // Cleanup stale offline operations from previous sessions (older than 48 hours) — non-blocking
            _ = Task.Run(async () =>
            {
                try { await offlineQueueRepository.CleanupStaleOperationsAsync(TimeSpan.FromHours(48)); }
                catch (Exception ex) { logger?.LogDebug($"Offline queue cleanup: {ex.Message}"); }
            });

            var otpRepository = new OtpRepository(logger);
            services.RegisterSingleton<OtpRepository>(otpRepository);

            // Register Model File Metrics Repository and Collector
            var metricsRepository = new ModelFileMetricsRepository(databasePath, logger);
            services.RegisterSingleton<ModelFileMetricsRepository>(metricsRepository);

            var metricsCollector = new ModelFileMetricsCollectorService(logger);
            services.RegisterSingleton<ModelFileMetricsCollectorService>(metricsCollector);

            // Register Hybrid Cache Manager (in-memory + SQLite)
            var cacheManager = new HybridCacheManager(
                databasePath,
                logger,
                maxInMemoryItems: 1000,
                defaultTtl: TimeSpan.FromHours(1));
            services.RegisterSingleton<HybridCacheManager>(cacheManager);

            // Register Cache Cleanup Policy (automated background cleanup)
            var cleanupConfig = CleanupConfiguration.Default; // Can be configured per environment
            var cleanupPolicy = new CacheCleanupPolicy(databasePath, logger, cleanupConfig);
            services.RegisterSingleton<CacheCleanupPolicy>(cleanupPolicy);

            // Register Model Registration Repository (master authorization table)
            var modelRepository = new RegisteredModelsRepository(databasePath, logger);
            services.RegisterSingleton<RegisteredModelsRepository>(modelRepository);

            // Register Cache Repository (category_cache + command_cache tables)
            var cacheRepository = new CacheRepository(databasePath, logger);
            services.RegisterSingleton<CacheRepository>(cacheRepository);

            // Register ZeIdentity Repository — saves MachineId + SID to bimanage.db at startup
            var zeIdentityRepository = new BIManage.Data.SQLite.ZeIdentityRepository(databasePath, logger);
            services.RegisterSingleton<BIManage.Data.SQLite.ZeIdentityRepository>(zeIdentityRepository);
            _ = Task.Run(() =>
            {
                try
                {
                    string? sid = null;
                    try { sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value; } catch { }
                    zeIdentityRepository.SaveIdentity(
                        Common.Helpers.MachineIdentifier.GetMachineId(logger),
                        sid,
                        Environment.UserName,
                        Environment.MachineName);
                    logger.LogInfo($"ZeIdentity saved: machine={Environment.MachineName} user={Environment.UserName}");
                }
                catch (Exception ex) { logger?.LogDebug($"ZeIdentity save failed: {ex.Message}"); }
            });

            logger.LogInfo("Persistence layer registered");

            var healthTracker = services.GetService<Infrastructure.DependencyInjection.BootstrapHealthTracker>();
            if (healthTracker != null) healthTracker.PersistenceReady = true;
        }

        private void RegisterProtection(ServiceRegistry services, InitContext context)
        {
            var logger = services.GetRequiredService<ILogger>();

            // Determine database path
            var databasePath = Path.Combine(context.LogDirectory, "bimanage.db");

            // Initialize database integrity service (HMAC tamper detection)
            var integrityService = new DatabaseIntegrityService(logger);
            integrityService.InitializeKey();
            services.RegisterSingleton<DatabaseIntegrityService>(integrityService);

            // Register AuditRepository
            var auditRepository = new AuditRepository(databasePath, logger, integrityService);
            services.RegisterSingleton<AuditRepository>(auditRepository);

            // Register AsyncAuditQueue (wraps AuditRepository for async writes)
            var asyncAuditQueue = new AsyncAuditQueue(auditRepository, logger);
            services.RegisterSingleton<AsyncAuditQueue>(asyncAuditQueue);

            // Register PasswordManager
            var passwordManager = new PasswordManager(databasePath, logger, integrityService);
            services.RegisterSingleton<PasswordManager>(passwordManager);

            // Register AuditService
            var auditService = new AuditService(auditRepository, logger);
            services.RegisterSingleton<AuditService>(auditService);

            // Register Evidence Capture services
            var evidenceRepository = new EvidenceRepository(databasePath, logger);
            services.RegisterSingleton<EvidenceRepository>(evidenceRepository);

            // Register Pin Protection Service
            // Note: Activation deferred until DocumentOpened
            var userService = services.GetService<BIManage.Core.Identity.IUserService>();
            var pinProtectionService = new BIManage.Revit.PinProtection.PinProtectionService(
                logger,
                isAdminCheck: () => userService?.HasAdminPrivileges ?? false,
                getCurrentUsername: () => Environment.UserName,
                auditRepository: auditRepository);
            services.RegisterSingleton<BIManage.Revit.PinProtection.PinProtectionService>(pinProtectionService);

            // Note: ScreenshotService, EvidenceUploadQueue, and PinProtection will be initialized per-document
            // via ExternalEvent triggered from DocumentOpened

            logger.LogInfo("Protection services registered (AuditRepository, AsyncAuditQueue, PasswordManager, AuditService, EvidenceRepository, PinProtectionService)");
            logger.LogInfo("PinProtectionService registered - will activate per-document via DocumentOpened event");
        }

        private void RegisterRuleEngine(ServiceRegistry services, InitContext context)
        {
            var logger = services.GetRequiredService<ILogger>();

            // Use the same database path as persistence layer
            var databasePath = Path.Combine(context.LogDirectory, "bimanage.db");

            // Register all rule engine services (evaluators, cache, repository, RuleService)
            services.RegisterRuleServices(logger, databasePath);

            // Initialize the rule service (load rules from database/samples)
            services.InitializeRuleService(logger);

            logger.LogInfo("Rule Engine registered and initialized");
        }

        private void RegisterProductivity(ServiceRegistry services)
        {
            services.RegisterSingleton<IProductivityTracker>(
                new ProductivityTracker(services.GetRequiredService<ILogger>()));
        }

        private void RegisterExecution(ServiceRegistry services)
        {
            services.RegisterSingleton<IExternalEventOrchestrator>(
                new ExternalEventOrchestrator(services.GetRequiredService<ILogger>()));

            services.RegisterSingleton<IRevitExecutionService>(
                new RevitExecutionService(
                    services.GetRequiredService<IExternalEventOrchestrator>(),
                    services.GetRequiredService<ILogger>()));

            services.RegisterSingleton<IRevitTransactionService>(
                new RevitTransactionService(services.GetRequiredService<ILogger>()));
        }

        private void RegisterContext(ServiceRegistry services, InitContext context)
        {
            var sessionId = Guid.NewGuid();
            var revitContext = new RevitContext(context.ControlledApplication, sessionId);
            services.RegisterSingleton<IRevitContext>(revitContext);

            // Register UIApplicationProvider for lazy UIApplication access
            services.RegisterSingleton<IUIApplicationProvider>(
                new UIApplicationProvider(services.GetRequiredService<ILogger>()));
        }

        private void RegisterSession(ServiceRegistry services)
        {
            services.RegisterSingleton<IDocumentSessionManager>(
                new DocumentSessionManager(services.GetRequiredService<ILogger>()));
        }

        private void RegisterUITracking(ServiceRegistry services)
        {
            // Register view tracking service
            services.RegisterSingleton<IViewTrackingService>(
                new ViewTrackingService(
                    services.GetRequiredService<IFeatureToggleService>(),
                    services.GetRequiredService<IProductivityTracker>(),
                    services.GetRequiredService<ILogger>()));

            // Register selection tracking service
            services.RegisterSingleton<ISelectionTrackingService>(
                new SelectionTrackingService(
                    services.GetRequiredService<IFeatureToggleService>(),
                    services.GetRequiredService<IProductivityTracker>(),
                    services.GetRequiredService<ILogger>()));
        }

        private void RegisterCommandAndIdling(ServiceRegistry services, InitContext context)
        {
            // Get dependencies for Pin/Unpin individual bindings (Option B - full individual-binding pattern)
            var userService = services.GetService<BIManage.Core.Identity.IUserService>();
            var otpRepository = services.GetService<OtpRepository>();
            var auditRepository = services.GetService<AuditRepository>();
            var logger = services.GetRequiredService<ILogger>();

            // Get database path for PinProtectionRepository
            var databasePath = Path.Combine(context.LogDirectory, "bimanage.db");

            // Create PinProtectionRepository for hybrid storage (ExtensibleStorage + SQLite)
            var pinProtectionRepository = new PinProtectionRepository(databasePath, logger, services.GetService<DatabaseIntegrityService>());
            services.RegisterSingleton(pinProtectionRepository);

            // IScreenshotService is initialized per-document via DocumentOpened event,
            // so we use getter functions to retrieve them dynamically when needed
            Func<IScreenshotService?> screenshotServiceGetter = () => services.GetService<IScreenshotService>();
            Func<IRevitContext?> revitContextGetter = () => services.GetService<IRevitContext>();
            Func<EvidenceRepository?> evidenceRepositoryGetter = () => services.GetService<EvidenceRepository>();

            // Create UnpinnedElementTracker for after-screenshot capture on element modification
            var unpinnedElementTracker = new UnpinnedElementTracker(logger, screenshotServiceGetter, evidenceRepositoryGetter);
            services.RegisterSingleton(unpinnedElementTracker);

            // Getter for CommandProtectionBinding (initialized later via ExternalEvent)
            Func<CommandProtectionBinding?> commandProtectionGetter = () => services.GetService<CommandProtectionBinding>();

            services.RegisterSingleton<ICommandInterceptionService>(
                new CommandInterceptionService(
                    services.GetRequiredService<IFeatureToggleService>(),
                    services.GetRequiredService<IProductivityTracker>(),
                    logger,
                    isAdminCheck: () => userService?.HasAdminPrivileges ?? false,
                    otpRepository,
                    auditRepository,
                    pinProtectionRepository,
                    screenshotServiceGetter,
                    revitContextGetter,
                    unpinnedElementTracker,
                    commandProtectionGetter,
                    positionRestorationGetter: () => services.GetService<PositionRestorationService>(),
                    registeredModelsRepoGetter: () => services.GetService<RegisteredModelsRepository>(),
                    pinProtectionSyncGetter: () => services.GetService<PinProtectionSyncService>(),
                    eventProtectionServiceGetter: () => services.GetService<IEventProtectionService>(),
                    evidenceRepositoryGetter: () => services.GetService<EvidenceRepository>(),
                    syncTrafficControlGetter: () => services.GetService<SyncTrafficControl.SyncTrafficControlService>(),
                    signalREventBusGetter: () => services.GetService<Infrastructure.SignalR.Events.ISignalREventBus>(),
                    sessionRepository: services.GetService<BIManage.Data.SQLite.SessionRepository>(),
                    modelSessionSyncGetter: () => services.GetService<BIManage.Infrastructure.Api.ModelSessionSyncService>()));

            logger.LogInfo("Command interception service registered with Pin/Unpin bindings, OTP support, and pin protection database");

            // Register CommandProtectionRepository for database-driven command protection
            // CommandProtectionBinding will be created in SetupProtectionBindingsExternalEventHandler
            // when UIApplication is available
            var commandProtectionRepository = new CommandProtectionRepository(databasePath, logger, services.GetService<DatabaseIntegrityService>());
            services.RegisterSingleton<CommandProtectionRepository>(commandProtectionRepository);

            logger.LogInfo("CommandProtectionRepository registered (CommandProtectionBinding will be initialized on DocumentOpened)");

            // Register EventProtectionRepository and EventProtectionService for event-based protections
            var eventProtectionRepository = new EventProtectionRepository(databasePath, logger, services.GetService<DatabaseIntegrityService>());
            services.RegisterSingleton<EventProtectionRepository>(eventProtectionRepository);

            var eventProtectionService = new EventProtectionService(logger, eventProtectionRepository);
            services.RegisterSingleton<IEventProtectionService>(eventProtectionService);
            services.RegisterSingleton<EventProtectionService>(eventProtectionService);

            logger.LogInfo("EventProtectionRepository and EventProtectionService registered");

            // Create PositionRestorationService for pin protection bypass prevention
            var positionRestorationService = new PositionRestorationService(logger);
            services.RegisterSingleton<PositionRestorationService>(positionRestorationService);

            // Create UnmonitoredUserDetectionService for detecting users without BIManage
            var detectionService = new Core.Detection.UnmonitoredUserDetectionService(
                services.GetRequiredService<SessionRepository>(), databasePath, logger);
            services.RegisterSingleton<Core.Detection.UnmonitoredUserDetectionService>(detectionService);

            services.RegisterSingleton<IIdlingService>(
                new IdlingService(
                    services.GetRequiredService<IUIApplicationProvider>(),
                    services.GetRequiredService<IFeatureToggleService>(),
                    services.GetRequiredService<ILogger>(),
                    services.GetService<SessionRepository>(),
                    syncRepository: services.GetService<Data.SQLite.SyncRepository>(),
                    revitContext: services.GetRequiredService<IRevitContext>(),
                    positionRestorationService: positionRestorationService,
                    sessionSyncService: services.GetService<SessionSyncService>(),
                    detectionService: detectionService,
                    licenseValidator: services.GetService<Licensing.LicenseValidator>(),
                    healthTracker: services.GetService<Infrastructure.DependencyInjection.BootstrapHealthTracker>()));

            // Post-open Worksets / Manage Links idle-time observer. Replaces the disabled
            // command-binding approach. Pure observer — no command-dispatch interference.
            // Started later from Application.OnApplicationInitialized once UIApplication is up.
            services.RegisterSingleton<Revit.Idling.PostOpenDialogTimingService>(
                new Revit.Idling.PostOpenDialogTimingService(
                    services.GetService<ILogger>(),
                    services.GetService<SessionRepository>(),
                    () => services.GetService<IRevitContext>(),
                    () => services.GetService<Infrastructure.Api.ModelSessionSyncService>()));

            // Register Rule Command Interceptor (database-driven rule evaluation)
            var ruleService = services.GetService<IRuleService>();
            // Note: auditRepository, userService, and logger already retrieved above for CommandInterceptionService
            var asyncAuditQueue = services.GetService<AsyncAuditQueue>();
            var passwordManager = services.GetService<PasswordManager>();

            if (ruleService != null && asyncAuditQueue != null && passwordManager != null)
            {
                // Get session ID from RevitContext
                var revitContext = services.GetRequiredService<IRevitContext>();
                var sessionId = revitContext.SessionId.ToString();

                var ruleInterceptor = new RuleCommandInterceptor(
                    services.GetRequiredService<ICommandInterceptionService>(),
                    ruleService,
                    services.GetRequiredService<IFeatureToggleService>(),
                    logger,
                    sessionId,                 // Session ID for evidence capture
                    auditRepository,           // Legacy sync (fallback)
                    asyncAuditQueue,           // Preferred async queue
                    passwordManager,
                    userService,
                    otpRepository: otpRepository);

                services.RegisterSingleton<IRuleCommandInterceptor>(ruleInterceptor);

                // Wire up the rule interceptor to the command interception service
                var commandService = services.GetRequiredService<ICommandInterceptionService>();
                commandService.SetRuleInterceptor(ruleInterceptor);

                logger.LogInfo("Rule command interceptor registered with AsyncAuditQueue and wired to command service");
            }
            else
            {
                logger.LogWarning("Rule command interceptor not registered - missing dependencies");
            }
        }

        private void RegisterEventSystem(ServiceRegistry services, InitContext context)
        {
            services.RegisterSingleton<IEventTogglePolicy>(
                new EventTogglePolicy(services.GetRequiredService<IFeatureToggleService>()));

            services.RegisterSingleton<IDocumentEventRegistry>(
                new DocumentEventRegistry(
                    context.ControlledApplication,
                    services.GetRequiredService<IEventTogglePolicy>(),
                    services.GetRequiredService<ILogger>()));

            // Command and Idling registries with lazy UIApplication initialization
            services.RegisterSingleton<ICommandEventRegistry>(
                new CommandEventRegistry(
                    context.ControlledApplication,
                    services.GetRequiredService<IEventTogglePolicy>(),
                    services.GetRequiredService<IUIApplicationProvider>(),
                    services.GetRequiredService<ICommandInterceptionService>(),
                    services.GetRequiredService<ILogger>()));

            services.RegisterSingleton<IIdlingEventRegistry>(
                new IdlingEventRegistry(
                    context.ControlledApplication,
                    services.GetRequiredService<IEventTogglePolicy>(),
                    services.GetRequiredService<IUIApplicationProvider>(),
                    services.GetRequiredService<IIdlingService>(),
                    services.GetRequiredService<ILogger>()));

            // UI Tracking registry (view and selection) with lazy UIApplication initialization
            services.RegisterSingleton<IUITrackingEventRegistry>(
                new UITrackingEventRegistry(
                    context.ControlledApplication,
                    services.GetRequiredService<IEventTogglePolicy>(),
                    services.GetRequiredService<IUIApplicationProvider>(),
                    services.GetRequiredService<IViewTrackingService>(),
                    services.GetRequiredService<ISelectionTrackingService>(),
                    services.GetRequiredService<ILogger>()));

            // Create TogglePinExternalEvent for floating pushpin icon protection
            var logger = services.GetRequiredService<ILogger>();
            var userService = services.GetService<BIManage.Core.Identity.IUserService>();
            var otpRepository = services.GetService<OtpRepository>();
            var pinProtectionRepository = services.GetService<PinProtectionRepository>();

            ExternalEvent? togglePinExternalEvent = null;
            if (otpRepository != null && pinProtectionRepository != null)
            {
                var auditRepo = services.GetService<AuditRepository>();
                Func<IRevitContext?> revitCtxGetter = () => services.GetService<IRevitContext>();
                var togglePinHandler = new TogglePinExternalEventHandler(
                    logger,
                    isAdminCheck: () => userService?.HasAdminPrivileges ?? false,
                    otpRepository,
                    pinProtectionRepository,
                    auditRepo,
                    revitCtxGetter,
                    screenshotServiceGetter: () => services.GetService<IScreenshotService>(),
                    evidenceRepositoryGetter: () => services.GetService<EvidenceRepository>(),
                    unpinnedElementTracker: services.GetService<UnpinnedElementTracker>(),
                    commandProtectionGetter: () => services.GetService<CommandProtectionBinding>(),
                    ruleInterceptorGetter: () => services.GetService<IRuleCommandInterceptor>());
                togglePinExternalEvent = ExternalEvent.Create(togglePinHandler);
                logger?.LogInfo("TogglePinExternalEvent created for floating pushpin protection");
            }
            else
            {
                logger?.LogWarning("OTP or PinProtection repository not available - floating pushpin protection disabled");
            }

            // Create PinAfterEventExternalEvent for CAD Import Pin Prompt and Copy/Monitor Pin Protection
            var pinAfterEventHandler = new PinAfterEventExternalEventHandler(logger);
            var pinAfterEventExternalEvent = ExternalEvent.Create(pinAfterEventHandler);
            logger?.LogInfo("PinAfterEventExternalEvent created for post-action pin protections");

            // Closes the bypass loophole for Move/Copy/Cut on non-pin-protected elements with
            // an active Command Protection or Rule (drag, nudge, Ctrl+drag, Ctrl+V, palette edit).
            // Mirrors CommandInterceptionService.OnBeforeCommandExecuted's Priority 1+2 chain
            // post-action via DocumentChanged.
            var bypassDetector = new BIManage.Revit.Protection.ElementBypassDetector(
                services.GetRequiredService<ILogger>(),
                services.GetRequiredService<IFeatureToggleService>(),
                () => services.GetService<CommandProtectionBinding>(),
                () => services.GetService<IRuleCommandInterceptor>(),
                services.GetService<PositionRestorationService>());
            services.RegisterSingleton<BIManage.Revit.Protection.ElementBypassDetector>(bypassDetector);

            var deletionGuard = new BIManage.Revit.Protection.DeletionProtectionGuard(
                services.GetService<ILogger>());
            services.RegisterSingleton<BIManage.Revit.Protection.DeletionProtectionGuard>(deletionGuard);

            services.RegisterSingleton<IEventRegistryService>(
                new EventRegistryService(
                    context.ControlledApplication,
                    services.GetRequiredService<IExternalEventOrchestrator>(),
                    services.GetRequiredService<IFeatureToggleService>(),
                    services.GetRequiredService<IProductivityTracker>(),
                    services.GetRequiredService<IDocumentSessionManager>(),
                    services.GetRequiredService<ILogger>(),
                    togglePinExternalEvent,
                    services.GetService<SessionRepository>(),
                    services.GetRequiredService<IRevitContext>(),
                    services.GetService<SyncRepository>(),
                    services.GetService<ModelFileMetricsRepository>(),
                    services.GetService<ModelFileMetricsCollectorService>(),
                    services.GetRequiredService<IUIApplicationProvider>(),
                    services.GetService<UnpinnedElementTracker>(),
                    services.GetService<PositionRestorationService>(),
                    services.GetService<AuditRepository>(),
                    modelSessionSyncGetter: () => services.GetService<Infrastructure.Api.ModelSessionSyncService>(),
                    metricsSyncGetter: () => services.GetService<Infrastructure.Api.MetricsSyncService>(),
                    syncTrafficControlGetter: () => services.GetService<Revit.SyncTrafficControl.SyncTrafficControlService>(),
                    signalREventBusGetter: () => services.GetService<Infrastructure.SignalR.Events.ISignalREventBus>(),
                    backgroundSyncEngineGetter: () => services.GetService<Revit.BackgroundSync.BackgroundSyncEngine>(),
                    eventProtectionServiceGetter: () => services.GetService<IEventProtectionService>(),
                    screenshotServiceGetter: () => services.GetService<IScreenshotService>(),
                    evidenceRepositoryGetter: () => services.GetService<EvidenceRepository>(),
                    otpRepositoryGetter: () => services.GetService<OtpRepository>(),
                    isAdminCheck: () => userService?.HasAdminPrivileges ?? false,
                    pinAfterEventExternalEvent: pinAfterEventExternalEvent,
                    ruleServiceGetter: () => services.GetService<IRuleService>(),
                    registeredModelsRepoGetter: () => services.GetService<RegisteredModelsRepository>(),
                    bypassDetector: bypassDetector,
                    deletionGuard: deletionGuard,
                    // Wire the SignalR connection manager getter for the cloud sync gate
                    // (Option D). Lazy because SignalR is registered in a later bootstrap
                    // phase (RegisterSignalR) — direct injection here would resolve to null
                    // at construction time. Returns null if SignalR is disabled or the
                    // bootstrap phase hasn't run yet, in which case the cloud gate
                    // short-circuits to Unavailable and the existing client-side STC
                    // fallback runs.
                    signalRConnectionManagerGetter: () => services.GetService<Infrastructure.SignalR.Core.ISignalRConnectionManager>(),
                    // Phase 1.f — Command Protection enforcement on Sync via the canonical
                    // DocumentSynchronizingWithCentral hook. Lazy because CommandProtectionBinding
                    // is registered later (in SetupProtectionBindings on document open).
                    commandProtectionGetter: () => services.GetService<CommandProtectionBinding>(),
                    // Phase 6 — Cloud-gate deferred local save. Handler is created
                    // in Application.CreateExternalEvents() and registered in DI from
                    // there; lazy getter avoids the construction-order dependency.
                    localSaveBeforeQueueHandlerGetter: () => services.GetService<global::BIManage.Revit.SyncTrafficControl.LocalSaveBeforeQueueHandler>()));

            services.RegisterSingleton<IEventRegistry>(
                new BIManage.Revit.EventRegistry.EventRegistry(
                    services.GetRequiredService<IEventRegistryService>(),
                    services.GetRequiredService<IDocumentEventRegistry>(),
                    services.GetRequiredService<ICommandEventRegistry>(),
                    services.GetRequiredService<IIdlingEventRegistry>(),
                    services.GetRequiredService<IUITrackingEventRegistry>(),
                    services.GetRequiredService<ILogger>()));
        }

        private void RegisterDiagnostics(ServiceRegistry services)
        {
            services.RegisterSingleton<IRevitHealthMonitor>(
                new RevitHealthMonitor(services.GetRequiredService<ILogger>()));
        }

        private void RegisterSignalR(ServiceRegistry services, InitContext context)
        {
            var logger = services.GetRequiredService<ILogger>();
            var featureService = services.GetService<IFeatureToggleService>();

            // Check if SignalR is enabled (disabled by default for gradual rollout)
            if (featureService?.IsFeatureEnabled("SignalR") == false)
            {
                logger?.LogInfo("SignalR: Disabled by feature toggle");
                return;
            }

            try
            {
                // Get hub URL from DLL's own config file
                var hubUrl = ReadDllAppSetting("SignalR:HubUrl");
                if (string.IsNullOrEmpty(hubUrl))
                {
                    logger?.LogInfo("SignalR: Not configured (HubUrl not specified)");
                    return;
                }

                // Create configuration with MachineId as device identifier
                // CompanyId is set later after device authentication
                var machineId = Common.Helpers.MachineIdentifier.GetMachineId(logger);
                var config = new SignalRConfiguration
                {
                    HubUrl = hubUrl,
                    AutoReconnect = true,
                    MachineId = machineId,
                    Username = Environment.UserName
                };

                services.RegisterSingleton(config);

                // Register group helper (stateless utility)
                services.RegisterSingleton<ISignalRGroupHelper>(new SignalRGroupHelper());

                // Register event bus for internal message distribution
                var eventBus = new SignalREventBus(logger);
                services.RegisterSingleton<ISignalREventBus>(eventBus);

                // Resolve AuthTokenManager so SignalR can fetch a JWT for the hub connection.
                // RegisterApiServices runs before RegisterSignalR (see RegisterServices ordering),
                // so this is non-null in normal startup. May be null only in unusual configs where
                // ApiBaseUrl is unset — in that case SignalR connects anonymously (logged warn).
                var tokenManager = services.GetService<AuthTokenManager>();
                if (tokenManager == null)
                    logger?.LogWarning("SignalR: AuthTokenManager not registered — hub connection will be anonymous.");

                // Register main SignalR service (WebSocket-based, no NuGet dependency).
                // Token is wired via AccessTokenProvider — the service invokes it on every
                // (re)connect to fetch a fresh JWT from AuthTokenManager.
                var signalRService = new SignalRService(config, logger);
                if (tokenManager != null)
                {
                    signalRService.AccessTokenProvider = () => tokenManager.GetAccessTokenAsync();
                    // Wire SignalR's persistent-negotiate-401 escalation to AuthTokenManager
                    // so 2 consecutive blacklisted-token rejections fire force-logout in
                    // ~5 s instead of looping for ~30 s on the slow-reconnect cadence.
                    // wasAdminAttempt=false — negotiate uses the device-scope token.
                    signalRService.OnAuthExhausted = () => tokenManager.NotifyAuthExhausted(wasAdminAttempt: false);

                    // When OfflineSyncProcessor's periodic device-revalidation succeeds
                    // after a force-logout, AuthTokenManager fires TokensReissuedAfterExhaustion.
                    // Without this hook SignalR stays Disconnected (the "Live updates paused"
                    // banner persists) until the user manually re-signs-in or restarts Revit,
                    // even though valid tokens are now available. Kick off a fresh ConnectAsync
                    // so real-time events resume automatically.
                    tokenManager.TokensReissuedAfterExhaustion += () =>
                    {
                        try
                        {
                            logger?.LogInfo("SignalR: TokensReissuedAfterExhaustion — attempting automatic reconnect.");
                            _ = signalRService.ConnectAsync();
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning($"SignalR: automatic reconnect after token re-issue threw: {ex.Message}");
                        }
                    };
                }
                services.RegisterSingleton<ISignalRService>(signalRService);

                // SyncSlotResponseCoordinator — correlates RequestSyncSlot (client → server)
                // with SyncSlotGranted / SyncSlotDenied (server → client) for the cloud sync
                // gate. Registered before the connection manager + sync queue listener so
                // both can wire it in. Safe to register even if the server doesn't yet
                // implement RequestSyncSlot: the awaiter simply times out, the cloud gate
                // returns Unavailable, and EventRegistryService falls back to today's
                // client-side STC gate.
                var slotCoordinator = new BIManage.Infrastructure.SignalR.SyncSlotResponseCoordinator(logger);
                services.RegisterSingleton<BIManage.Infrastructure.SignalR.SyncSlotResponseCoordinator>(slotCoordinator);

                // Register connection manager
                var connectionManager = new SignalRConnectionManager(
                    signalRService,
                    services.GetRequiredService<ISignalRGroupHelper>(),
                    logger,
                    slotCoordinator);
                services.RegisterSingleton<ISignalRConnectionManager>(connectionManager);

                // Register listeners
                var modelRepository = services.GetService<RegisteredModelsRepository>();
                if (modelRepository != null)
                {
                    var modelRegistrationListener = new ModelRegistrationListener(modelRepository, eventBus, logger);
                    connectionManager.RegisterListener(modelRegistrationListener);
                    logger?.LogInfo("SignalR: ModelRegistrationListener registered");
                }

                // ProtectionChangeListener is registered later in RegisterApiServices,
                // where its dependent sync services (CommandProtectionSyncService, etc.) are available.

                // SessionActivityListener — publishes presence events to ISignalREventBus for ViewModels
                var sessionListener = new SessionActivityListener(logger, eventBus);
                connectionManager.RegisterListener(sessionListener);
                logger?.LogInfo("SessionActivityListener registered for real-time user presence updates");

                // PresenceCache — listens from app startup so late-opening Model Activities dialogs
                // can see users who joined the model BEFORE the dialog was opened. Compensates for
                // the server's RequestActiveUsers returning an incomplete roster.
                var presenceCache = new BIManage.Infrastructure.SignalR.PresenceCache(eventBus, logger);
                services.RegisterSingleton<BIManage.Infrastructure.SignalR.PresenceCache>(presenceCache);
                logger?.LogInfo("PresenceCache registered — caching presence events across dialog lifetimes");

                // SyncQueueListener — publishes sync events AND persists remote sync starts/completes
                // to SQLite even when the SyncQueue dialog is closed, so reopening it while remote
                // users are still syncing shows their in-flight status.
                var syncRepoForListener = services.GetService<SyncRepository>();
                var ownSessionIdForListener = services.GetService<IRevitContext>()?.SessionId.ToString();
                var sessionRepoForListener = services.GetService<BIManage.Data.SQLite.SessionRepository>();
                var syncQueueListener = new SyncQueueListener(
                    logger,
                    eventBus,
                    syncRepoForListener,
                    ownSessionIdForListener,
                    sessionRepoForListener,
                    slotCoordinator);
                connectionManager.RegisterListener(syncQueueListener);
                logger?.LogInfo("SyncQueueListener registered for real-time sync coordination events");

                // ChatListener — publishes chat events for ModelActivitiesViewModel
                var chatListener = new ChatListener(logger, eventBus);
                connectionManager.RegisterListener(chatListener);
                logger?.LogInfo("ChatListener registered for real-time chat messaging");

                // ProtectionChangeListener — refreshes protections on real-time SignalR push.
                // Sync services come from RegisterApiServices, which runs before this phase.
                var cmdSync = services.GetService<CommandProtectionSyncService>();
                var evtSync = services.GetService<EventProtectionSyncService>();
                var rlSync = services.GetService<RulesSyncService>();
                var pinSync = services.GetService<PinProtectionSyncService>();
                var evtProt = services.GetService<IEventProtectionService>();
                var ruleSvc = services.GetService<IRuleService>();
                Func<CommandProtectionBinding?> cmdProtBindingGetter = () => services.GetService<CommandProtectionBinding>();

                var registeredModelsRepoForListener = services.GetService<BIManage.Data.SQLite.RegisteredModelsRepository>();
                var protectionChangeListener = new ProtectionChangeListener(
                    eventBus,
                    connectionManager,
                    cmdSync, evtSync, rlSync, pinSync,
                    cmdProtBindingGetter, evtProt, ruleSvc,
                    logger,
                    registeredModelsRepoForListener);
                connectionManager.RegisterListener(protectionChangeListener);
                logger?.LogInfo("ProtectionChangeListener registered for real-time protection refresh");

                // ForceLogout + ForceTokenRefresh — handle server-initiated auth events
                var authTokenMgr = services.GetService<BIManage.Infrastructure.Auth.AuthTokenManager>();
                var userService = services.GetService<BIManage.Core.Identity.IUserService>();
                if (authTokenMgr != null && userService != null)
                {
                    var forceLogoutListener = new BIManage.Infrastructure.SignalR.Listeners.ForceLogoutListener(
                        authTokenMgr, userService, eventBus, logger);
                    connectionManager.RegisterListener(forceLogoutListener);
                    logger?.LogInfo("ForceLogoutListener registered for server-initiated logout");

                    var forceRefreshListener = new BIManage.Infrastructure.SignalR.Listeners.ForceTokenRefreshListener(
                        authTokenMgr, userService, eventBus, logger,
                        services.GetService<BIManage.Infrastructure.Auth.SecureTokenStorage>());
                    connectionManager.RegisterListener(forceRefreshListener);
                    logger?.LogInfo("ForceTokenRefreshListener registered for server-initiated token refresh");
                }
                else
                {
                    logger?.LogWarning("ForceLogout/ForceTokenRefresh listeners not registered — AuthTokenManager or IUserService not available");
                }

                // SyncTrafficControlService — coordinates sync queue blocking via SignalR
                var revitContext = services.GetService<IRevitContext>();
                var syncRepo = services.GetService<SyncRepository>();
                if (revitContext != null && syncRepo != null)
                {
                    var syncTrafficControl = new Revit.SyncTrafficControl.SyncTrafficControlService(
                        eventBus,
                        connectionManager,
                        syncRepo,
                        revitContext.SessionId.ToString(),
                        logger,
                        System.Windows.Threading.Dispatcher.CurrentDispatcher);
                    syncTrafficControl.Initialize();
                    services.RegisterSingleton(syncTrafficControl);
                    logger?.LogInfo("SyncTrafficControlService registered for sync queue coordination");
                }

                logger?.LogInfo($"SignalR: Infrastructure registered (Hub: {hubUrl})");

                // Late-bind SignalR into sync services that were constructed during
                // RegisterApiServices (which runs BEFORE RegisterSignalR). Without this,
                // _signalRService stays null on RulesSyncService and "+ New Rule" can
                // never take the SignalR-first path → other clients never get the
                // real-time push and only see the rule on next manual refresh.
                services.GetService<BIManage.Infrastructure.Api.RulesSyncService>()?.AttachSignalR(signalRService);

                var htSr = services.GetService<Infrastructure.DependencyInjection.BootstrapHealthTracker>();
                if (htSr != null) htSr.SignalRReady = true;
            }
            catch (Exception ex)
            {
                logger?.LogError($"SignalR: Failed to register - {ex.Message}", ex);
                // Don't throw - SignalR is optional functionality
            }
        }

        private void RegisterApiServices(ServiceRegistry services)
        {
            var logger = services.GetRequiredService<ILogger>();

            try
            {
                // Read API base URL from the DLL's own config file
                // Note: ConfigurationManager.AppSettings reads from the HOST app (Revit.exe) config,
                // not from BIManageRevit.dll.config. We must explicitly open the DLL's config.
                var baseUrl = ReadDllAppSetting("ApiBaseUrl");
                if (string.IsNullOrEmpty(baseUrl))
                {
                    logger.LogWarning("ApiBaseUrl not configured in App.config - API services disabled");
                    var ht = services.GetService<Infrastructure.DependencyInjection.BootstrapHealthTracker>();
                    if (ht != null) { ht.ApiServicesReady = false; ht.LastError = "ApiBaseUrl not configured"; }
                    return;
                }

                // Auth services
                var sslPolicy = services.GetService<BIManage.Infrastructure.Network.SslValidationPolicy>();
                var secureStorage = new SecureTokenStorage(logger);
                var authApi = new AuthApiService(baseUrl, logger, sslPolicy);
                var tokenManager = new AuthTokenManager(secureStorage, authApi, logger);
                var authenticatedClient = new AuthenticatedHttpClient(tokenManager, baseUrl, logger, sslPolicy);

                // Restore admin session flag IMMEDIATELY so the correct refresh endpoint is used
                // if SignalR reconnect triggers a token refresh before AutoAuthenticateDeviceAsync runs.
                //
                // Treat the user as admin if EITHER ProfileId is present OR the stored role flags say
                // so. Older builds could wipe ProfileId during a device-refresh cycle, leaving the
                // user stuck on /device-refresh forever even though the admin refresh token in
                // admin-auth.dat was intact. RestoreAdminSessionFlag still validates that an admin
                // refresh token exists internally, so this only widens recovery, never bypasses safety.
                if (secureStorage.HasStoredIdentity)
                {
                    var storedIdentity = secureStorage.LoadUserIdentity();
                    var looksLikeAdmin = storedIdentity != null
                        && (!string.IsNullOrEmpty(storedIdentity.ProfileId)
                            || storedIdentity.IsCompanyAdmin
                            || storedIdentity.IsProjectAdmin);
                    if (looksLikeAdmin)
                    {
                        tokenManager.RestoreAdminSessionFlag(true);
                    }
                }

                services.RegisterSingleton<SecureTokenStorage>(secureStorage);
                services.RegisterSingleton<AuthApiService>(authApi);
                services.RegisterSingleton<AuthTokenManager>(tokenManager);
                services.RegisterSingleton<AuthenticatedHttpClient>(authenticatedClient);

                // License validation — per-module licensing, passive mode, offline grace period
                var licenseCache    = new BIManage.Licensing.LicenseCache(logger);
                var policyResolver  = new BIManage.Licensing.LicensePolicyResolver();
                var licenseValidator = new BIManage.Licensing.LicenseValidator(
                    licenseCache, policyResolver, logger, tokenManager, authenticatedClient);
                services.RegisterSingleton<BIManage.Licensing.LicenseCache>(licenseCache);
                services.RegisterSingleton<BIManage.Licensing.LicensePolicyResolver>(policyResolver);
                services.RegisterSingleton<BIManage.Licensing.LicenseValidator>(licenseValidator);

                // Re-apply module gating whenever the license mode transitions.
                // Without this subscription, ApplyModuleGating only runs ONCE at startup —
                // and if the initial validation runs before the first heartbeat arrives
                // (common race: heartbeat is only sent after session sync completes),
                // features stay disabled for the whole session even when the server
                // reports licenseMode=1 (Licensed). UpdateFromHeartbeatResponse fires
                // LicenseStatusChanged on every mode transition; this handler re-applies
                // feature gating so tools re-enable as soon as the license is confirmed.
                licenseValidator.LicenseStatusChanged += (sender, e) =>
                {
                    try
                    {
                        logger?.LogInfo($"[License] Mode transition {e.OldMode} → {e.NewMode} — re-applying module gating");
                        ApplyModuleGating(services, licenseValidator.CurrentInfo, logger);

                        // Update Status button color to reflect new mode
                        try
                        {
                            if (e.NewMode == Licensing.LicenseMode.Licensed)
                                BIManageRevit.Commands.RibbonCommands.DeviceStatusCommand.UpdateButtonAppearance(true);
                            else if (e.NewMode == Licensing.LicenseMode.Passive)
                                BIManageRevit.Commands.RibbonCommands.DeviceStatusCommand.SetButtonYellow();
                            else if (e.NewMode == Licensing.LicenseMode.Breached)
                                BIManageRevit.Commands.RibbonCommands.DeviceStatusCommand.UpdateButtonAppearance(false);
                        }
                        catch { }
                    }
                    catch (Exception gex)
                    {
                        logger?.LogWarning($"[License] Re-apply gating failed: {gex.Message}");
                    }
                };

                // Auto-logout when client has no path forward (no stored refresh token AND a
                // 401 just came back). Triggered ONLY for the definitively-broken case;
                // transient failures (network/5xx) preserve the refresh token and don't fire
                // this. Throttled inside the token manager (one event per 2 min cooldown) so
                // a burst of failed dialog fetches can't queue multiple logout dialogs.
                tokenManager.AuthExhausted += (wasAdminAttempt) =>
                {
                    try
                    {
                        var msg = wasAdminAttempt
                            ? "Your admin session has expired. Please sign in again to continue managing protections."
                            : "Your session has expired. Please sign in again.";
                        logger?.LogWarning($"[AuthExhausted] Forcing logout — wasAdminAttempt={wasAdminAttempt}");

                        secureStorage.ClearUserIdentity();
                        var userServiceForExhausted = services.GetService<Core.Identity.IUserService>();
                        userServiceForExhausted?.Logout();
                        tokenManager.ClearTokens();

                        // Popup suppressed per user request — only log to file.
                        // The auto-logout (ClearTokens / ClearUserIdentity) above still runs.
                        logger?.LogInfo($"[AuthExhausted] {msg}");
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning($"[AuthExhausted] Handler error: {ex.Message}");
                    }
                };

                // Auto-logout when server rejects refresh token (expired/revoked)
                tokenManager.TokenRejected += (wasAdminSession) =>
                {
                    try
                    {
                        if (wasAdminSession)
                        {
                            // Admin session: clear identity, logout, prompt to sign in again
                            logger?.LogWarning("Admin token rejected — triggering auto-logout");
                            secureStorage.ClearUserIdentity();
                            var userService = services.GetService<Core.Identity.IUserService>();
                            userService?.Logout();

                            // Popup suppressed per user request — only log to file.
                            // The auto-logout (ClearUserIdentity / Logout) above still runs.
                            logger?.LogInfo("[TokenRejected] Admin session expired — silent logout (popup suppressed)");
                        }
                        else
                        {
                            // Device session: silently attempt re-authentication via ValidateDevice
                            logger?.LogInfo("Device token rejected — attempting silent re-authentication");
                            var machineId = Common.Helpers.MachineIdentifier.GetMachineId(logger);
                            System.Threading.Tasks.Task.Run(async () =>
                            {
                                try
                                {
                                    var response = await authApi.ValidateDeviceAsync(machineId);
                                    if (response?.AccessToken != null)
                                    {
                                        tokenManager.SetTokensFromDevice(response);
                                        logger?.LogInfo("Device re-authenticated silently after token rejection");
                                    }
                                    else
                                    {
                                        logger?.LogWarning("Device silent re-auth returned no tokens — device may need re-registration");
                                    }
                                }
                                catch (Exception reAuthEx)
                                {
                                    logger?.LogWarning($"Device silent re-auth failed: {reAuthEx.Message}");
                                }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning($"Auto-logout handler error: {ex.Message}");
                    }
                };

                // Inject HTTP client into OtpRepository for API-based OTP validation
                var otpRepository = services.GetService<OtpRepository>();
                if (otpRepository != null)
                {
                    otpRepository.SetHttpClient(authenticatedClient);
                }

                // Wire JWT token provider into SignalR so auto-connect and reconnects
                // can obtain a fresh access token from AuthTokenManager.
                // RegisterSignalR runs before RegisterApiServices, so SignalR may already
                // be attempting to connect with a null token — this fixes the 401.
                var signalRForAuth = services.GetService<ISignalRService>();
                if (signalRForAuth != null)
                {
                    signalRForAuth.AccessTokenProvider = async () =>
                        await tokenManager.GetAccessTokenAsync() ?? string.Empty;
                    logger.LogInfo("SignalR: AccessTokenProvider wired to AuthTokenManager");
                }

                // Auto-authenticate device on startup (fire-and-forget to avoid blocking)
                // Use short timeout (5s) to prevent long waits if server is unreachable
                // Then validate license and apply per-module feature gating
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                        var authTask = EnsureAuthenticatedOnceAsync(services, tokenManager, authApi, logger);
                        var completedTask = await Task.WhenAny(authTask, Task.Delay(5000, cts.Token));

                        if (completedTask != authTask)
                        {
                            logger?.LogWarning("Device validation timed out after 5 seconds (non-blocking)");
                        }
                        else
                        {
                            await authTask; // Propagate any exceptions
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Timeout - expected, not an error
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning($"Auto-authentication failed (non-critical): {ex.Message}");
                    }

                    // Reconnect SignalR if auth succeeded but SignalR is still disconnected (401 race condition)
                    if (tokenManager.IsAuthenticated)
                    {
                        try
                        {
                            var signalR = services.GetService<Infrastructure.SignalR.Core.ISignalRService>();
                            if (signalR != null && !signalR.IsConnected)
                            {
                                logger?.LogInfo("SignalR disconnected after auth — triggering reconnect");
                                _ = signalR.ConnectAsync();
                            }
                        }
                        catch (Exception srEx)
                        {
                            logger?.LogDebug($"SignalR post-auth reconnect failed: {srEx.Message}");
                        }
                    }

                    // Validate license after auth attempt and apply module gating
                    try
                    {
                        var licenseInfo = await licenseValidator.ValidateAsync();
                        ApplyModuleGating(services, licenseInfo, logger);

                        var htLic = services.GetService<Infrastructure.DependencyInjection.BootstrapHealthTracker>();
                        if (htLic != null) htLic.LicenseValidated = true;

                        // Update Status button color based on license mode
                        if (licenseInfo.Mode == Licensing.LicenseMode.Passive)
                            BIManageRevit.Commands.RibbonCommands.DeviceStatusCommand.SetButtonYellow();
                        else if (licenseInfo.Mode == Licensing.LicenseMode.Breached)
                            BIManageRevit.Commands.RibbonCommands.DeviceStatusCommand.UpdateButtonAppearance(false);

                        // Show startup warning if license is not fully valid.
                        // Field log BIManageRevit_20260520_2204.log proved that MessageBox.Show
                        // here ran modal on the WPF dispatcher and (without an Owner) hid behind
                        // Revit's main window, freezing the UI thread for ~80s until the user
                        // happened to find and dismiss it. Replaced with a non-blocking toast on
                        // its own STA thread, scheduled via IIdlingService so it only appears
                        // after Revit fires its first Idling tick (= main window responsive).
                        var (shouldWarn, daysRemaining) = licenseValidator.CheckExpirationWarning();
                        // Toast-worthy = real, user-actionable state. Mode=Licensed with
                        // status=GracePeriod is NOT actionable — it just means we're using the
                        // offline cache while the network heartbeat catches up, and surfaces as
                        // a useless "License is active" toast. Skip those.
                        var toastWorthy = shouldWarn
                            || licenseInfo.Mode == Licensing.LicenseMode.Passive
                            || licenseInfo.Mode == Licensing.LicenseMode.Breached;
                        if (shouldWarn || policyResolver.ShouldWarnUser(licenseInfo))
                        {
                            var message = policyResolver.GetUserMessage(licenseInfo);
                            if (shouldWarn && daysRemaining > 0)
                                message = $"License expires in {daysRemaining} day{(daysRemaining != 1 ? "s" : "")}. {message}";

                            if (licenseInfo.Mode == Licensing.LicenseMode.Licensed)
                                logger?.LogInfo($"License status: {message}");
                            else
                                logger?.LogWarning($"License warning: {message}");

                            if (toastWorthy)
                            {
                                var severity = licenseInfo.Mode == Licensing.LicenseMode.Breached
                                    ? BIManageRevit.BIManage.Views.Auth.LicenseToastSeverity.Error
                                    : BIManageRevit.BIManage.Views.Auth.LicenseToastSeverity.Warning;
                                var toastMessage = message;
                                var idling = services.GetService<IIdlingService>();
                                if (idling != null)
                                    idling.QueueWork(() => ShowLicenseWarningToast(toastMessage, severity, logger));
                                else
                                    ShowLicenseWarningToast(toastMessage, severity, logger);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning($"License validation failed (non-critical): {ex.Message}");
                    }
                });

                // Session sync service - SignalR primary, HTTP fallback, offline queue
                var sessionRepository = services.GetService<SessionRepository>();
                var signalRService = services.GetService<ISignalRService>();
                var offlineQueueRepository = services.GetService<OfflineQueueRepository>();
                if (sessionRepository != null)
                {
                    var sessionSync = new SessionSyncService(
                        sessionRepository,
                        signalRService: signalRService,
                        httpClient: authenticatedClient,
                        logger: logger,
                        offlineQueue: offlineQueueRepository);
                    services.RegisterSingleton<SessionSyncService>(sessionSync);

                    // Wire sync service into detection service for API reporting
                    var detectionSvc = services.GetService<Core.Detection.UnmonitoredUserDetectionService>();
                    detectionSvc?.SetSyncService(sessionSync);

                    // Wire session sync into idling service so heartbeat API calls are sent
                    (services.GetService<IIdlingService>() as IdlingService)?.SetSessionSyncService(sessionSync);

                    // Wire session sync into license validator for heartbeat-based license data
                    var licenseVal = services.GetService<Licensing.LicenseValidator>();
                    licenseVal?.SetSessionSyncService(sessionSync);

                    // Wire session sync into metrics service and offline processor for FK validation
                    var metricsSvc = services.GetService<MetricsSyncService>();
                    metricsSvc?.SetSessionSyncService(sessionSync);

                    var offlineProcessor = services.GetService<OfflineSyncProcessor>();
                    offlineProcessor?.SetSessionSyncService(sessionSync);
                }

                // Model sync service - SignalR primary, HTTP fallback, offline queue
                var modelRepository = services.GetService<RegisteredModelsRepository>();
                if (modelRepository != null)
                {
                    var modelSync = new ModelSyncService(
                        modelRepository,
                        signalRService: signalRService,
                        httpClient: authenticatedClient,
                        logger: logger,
                        offlineQueue: offlineQueueRepository);
                    services.RegisterSingleton<ModelSyncService>(modelSync);

                    // Auto-authenticate then fetch all data from API (single task, no retries)
                    // Auth completes -> fetch runs immediately with valid token
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Step 1: Authenticate device (joins the in-flight auth task
                            // started by the earlier RegisterApiServices block — single-flight gate
                            // prevents the two boot paths from running parallel auth + parallel SignalR connects)
                            await EnsureAuthenticatedOnceAsync(services, tokenManager, authApi, logger);

                            // Step 2: Fetch all data right after auth (token is guaranteed ready)
                            if (tokenManager.IsAuthenticated)
                            {
                                logger?.LogInfo("Auto-fetch: auth successful, fetching data from API...");

                                // Models are checked per-document on open, not bulk-fetched on startup

                                // Sessions are created locally and pushed to server — no need to fetch from API
                                // var sessionSync = services.GetService<SessionSyncService>();
                                // if (sessionSync != null)
                                // {
                                //     var sessionsInserted = await sessionSync.FetchAndStoreSessionsFromApiAsync();
                                //     if (sessionsInserted > 0)
                                //         logger?.LogInfo($"Auto-fetched {sessionsInserted} new session(s) from API");
                                // }

                                // Model sessions are created locally and pushed to server — no need to fetch from API
                                // var modelSessionSyncSvc = services.GetService<ModelSessionSyncService>();
                                // if (modelSessionSyncSvc != null)
                                // {
                                //     var modelSessionsInserted = await modelSessionSyncSvc.FetchAndStoreModelSessionsFromApiAsync();
                                //     if (modelSessionsInserted > 0)
                                //         logger?.LogInfo($"Auto-fetched {modelSessionsInserted} new model session(s) from API");
                                // }

                                // Metrics are fetched per-model on document open, not bulk on startup

                                // Fetch command protection settings
                                var cmdProtSync = services.GetService<CommandProtectionSyncService>();
                                if (cmdProtSync != null)
                                {
                                    var cmdSettings = await cmdProtSync.FetchAllFromApiAsync();
                                    if (cmdSettings.Count > 0)
                                        logger?.LogInfo($"Auto-fetched {cmdSettings.Count} command protection setting(s) from API");
                                }

                                // Fetch rules
                                var rulesSyncSvc = services.GetService<RulesSyncService>();
                                if (rulesSyncSvc != null)
                                {
                                    var rules = await rulesSyncSvc.FetchAllRulesFromApiAsync();
                                    if (rules.Count > 0)
                                        logger?.LogInfo($"Auto-fetched {rules.Count} rule(s) from API");
                                }

                                // Upload pending audit logs to backend
                                var auditLogSyncSvc = services.GetService<AuditLogSyncService>();
                                if (auditLogSyncSvc != null)
                                {
                                    var auditLogsSynced = await auditLogSyncSvc.UploadPendingAuditLogsAsync();
                                    if (auditLogsSynced > 0)
                                        logger?.LogInfo($"Auto-synced {auditLogsSynced} audit log(s) to API");
                                }

                                logger?.LogInfo("Auto-fetch complete: all API data synced to local DB");
                            }
                            else
                            {
                                logger?.LogWarning("Auto-fetch: skipped - device not authenticated");
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning($"Auto-auth/fetch failed (non-critical): {ex.Message}");
                        }
                    });
                }

                // Metrics sync service - SignalR primary, HTTP fallback, offline queue
                var metricsRepository = services.GetService<ModelFileMetricsRepository>();
                var metricsSync = new MetricsSyncService(
                    signalRService: signalRService,
                    httpClient: authenticatedClient,
                    logger: logger,
                    offlineQueue: offlineQueueRepository,
                    metricsRepository: metricsRepository);
                services.RegisterSingleton<MetricsSyncService>(metricsSync);

                // Model session sync service - SignalR primary, HTTP fallback, offline queue
                var modelSessionSync = new ModelSessionSyncService(
                    signalRService: signalRService,
                    httpClient: authenticatedClient,
                    logger: logger,
                    offlineQueue: offlineQueueRepository,
                    sessionRepository: sessionRepository);
                // Wire SessionSyncService so model_session sync can defer until the parent
                // session is confirmed on the server. Without this, opening a model directly
                // from File Explorer races: Revit's DocumentOpening fires before our session
                // POST roundtrips, model_session POST then hits a non-existent SessionId FK,
                // and the server rejects with 400/500.
                var sessionSyncForModel = services.GetService<SessionSyncService>();
                if (sessionSyncForModel != null)
                    modelSessionSync.SetSessionSyncService(sessionSyncForModel);
                services.RegisterSingleton<ModelSessionSyncService>(modelSessionSync);

                // Model admins sync service - SignalR primary, HTTP fallback, offline queue
                var modelAdminsSync = new ModelAdminsSyncService(
                    signalRService: signalRService,
                    httpClient: authenticatedClient,
                    logger: logger,
                    offlineQueue: offlineQueueRepository);
                services.RegisterSingleton<ModelAdminsSyncService>(modelAdminsSync);

                // Resolve once — both sync services use it to drop cross-tenant rows when
                // the user switches companies on the same device. Optional: a missing
                // service leaves filtering inactive (fails open).
                var userServiceForTenantFilter = services.GetService<global::BIManage.Core.Identity.IUserService>();

                // Command protection sync service - HTTP with offline queue fallback
                var commandProtectionRepository = services.GetService<CommandProtectionRepository>();
                var commandProtectionSync = new CommandProtectionSyncService(
                    httpClient: authenticatedClient,
                    offlineQueue: offlineQueueRepository,
                    logger: logger,
                    repository: commandProtectionRepository,
                    userService: userServiceForTenantFilter);
                services.RegisterSingleton<CommandProtectionSyncService>(commandProtectionSync);

                // Rules sync service - SignalR primary, HTTP fallback, offline queue
                var ruleRepository = services.GetService<RuleRepository>();
                if (ruleRepository != null)
                {
                    var rulesSync = new RulesSyncService(
                        ruleRepository,
                        signalRService: signalRService,
                        httpClient: authenticatedClient,
                        logger: logger,
                        offlineQueue: offlineQueueRepository,
                        userService: userServiceForTenantFilter);
                    services.RegisterSingleton<RulesSyncService>(rulesSync);
                }

                // Pin protection sync service - HTTP + offline queue
                var pinProtectionRepo = services.GetService<PinProtectionRepository>();
                if (pinProtectionRepo != null)
                {
                    var pinProtectionSync = new PinProtectionSyncService(
                        pinProtectionRepo,
                        httpClient: authenticatedClient,
                        logger: logger,
                        offlineQueue: offlineQueueRepository);
                    services.RegisterSingleton<PinProtectionSyncService>(pinProtectionSync);
                }

                // Event protection sync service - HTTP + offline queue (fetches & pushes event protections)
                var eventProtectionRepo = services.GetService<EventProtectionRepository>();
                var eventProtectionSync = new EventProtectionSyncService(
                    httpClient: authenticatedClient,
                    repository: eventProtectionRepo,
                    logger: logger,
                    offlineQueue: offlineQueueRepository);
                services.RegisterSingleton<EventProtectionSyncService>(eventProtectionSync);

                // Health monitor protection sync — fetches thresholds for UI display (read-only)
                var healthMonitorSync = new HealthMonitorProtectionSyncService(
                    httpClient: authenticatedClient,
                    logger: logger);
                services.RegisterSingleton<HealthMonitorProtectionSyncService>(healthMonitorSync);

                // Audit log sync service - uploads unsynced audit entries to backend
                var auditRepository = services.GetService<AuditRepository>();
                if (auditRepository != null)
                {
                    var sessionSyncSvc = services.GetService<SessionSyncService>();
                    var auditLogSync = new AuditLogSyncService(
                        httpClient: authenticatedClient,
                        auditRepository: auditRepository,
                        offlineQueue: offlineQueueRepository,
                        logger: logger,
                        sessionSyncService: sessionSyncSvc);
                    services.RegisterSingleton<AuditLogSyncService>(auditLogSync);

                    // Periodic audit log re-sync: every 60s, attempt to upload any audit rows
                    // written during the session. The startup sync (in the auto-fetch task above)
                    // runs ONCE; without this timer, any audit row created during the live
                    // session sits unsynced in local SQLite until the next Revit restart.
                    // Fire-and-forget; UploadPendingAuditLogsAsync has its own auth/FK guards
                    // and is a no-op when nothing is pending or auth isn't ready.
                    var auditSyncTimer = new System.Threading.Timer(_ =>
                    {
                        try
                        {
                            _ = auditLogSync.UploadPendingAuditLogsAsync();
                        }
                        catch (Exception ex)
                        {
                            logger?.LogDebug($"Periodic audit log sync iteration failed: {ex.Message}");
                        }
                    },
                    state: null,
                    dueTime: TimeSpan.FromSeconds(60),
                    period: TimeSpan.FromSeconds(60));
                    services.RegisterSingleton<System.Threading.Timer>(auditSyncTimer);
                    logger.LogInfo("Periodic audit log sync timer started (60s interval)");

                    // Audit log mail dispatch — notifies server to send email for flagged audit entries
                    var mailDispatch = new AuditLogMailDispatchService(
                        httpClient: authenticatedClient,
                        auditRepository: auditRepository,
                        logger: logger);
                    services.RegisterSingleton<AuditLogMailDispatchService>(mailDispatch);
                    logger.LogInfo("AuditLogMailDispatchService initialized");
                }

                // Offline sync processor - processes queued operations every 30 seconds
                // Pass token manager + authApi to enable token refresh AND device re-validation
                // Device validation fallback enables recovery when server was unreachable at startup
                if (offlineQueueRepository != null)
                {
                    var featureToggleSvc = services.GetService<IFeatureToggleService>();
                    var offlineSyncProcessor = new OfflineSyncProcessor(
                        offlineQueueRepository,
                        authenticatedClient,
                        logger,
                        autoStart: true,
                        tokenManager: tokenManager,
                        authApi: authApi,
                        featureToggleService: featureToggleSvc);
                    services.RegisterSingleton<OfflineSyncProcessor>(offlineSyncProcessor);
                    logger.LogInfo("OfflineSyncProcessor started (30-second sync interval, token refresh + device validation enabled)");
                }

                // Pre-load background sync settings (seed defaults if not exists) and apply to feature toggles
                var syncSettingsRepo = services.GetService<SyncRepository>();
                var featureToggles = services.GetService<IFeatureToggleService>();
                if (syncSettingsRepo != null && featureToggles != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var settings = await syncSettingsRepo.EnsureDefaultSettingsAsync();
                            featureToggles.SetFeatureEnabled("BackgroundSync", settings.IsEnabled);
                            featureToggles.SetFeatureEnabled("BackgroundRelinquish", settings.EnableRelinquish);
                            featureToggles.SetFeatureEnabled("IdleSync", settings.EnableIdleSync);
                            // SyncQueueControl is NOT coupled to background-sync settings — it
                            // governs the multi-user manual-sync coordination required by the
                            // team flow (claim arbitration, queue, Sync Now popups). Tying it
                            // to settings.IsEnabled created an ~11s window at startup where the
                            // background-sync default of `false` disabled the queue gate, then
                            // license validation re-enabled it — during that window two clients
                            // could click Sync and race uncoordinated. License gating in
                            // ApplyLicenseGating() remains the only authoritative on/off.
                            logger.LogInfo($"Background sync settings loaded from DB (Enabled: {settings.IsEnabled}, Relinquish: {settings.EnableRelinquish}, IdleSync: {settings.EnableIdleSync})");
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning($"Failed to load background sync settings: {ex.Message}");
                        }
                    });
                }

                // Note: SessionActivity / SyncQueue / Chat / ProtectionChange / ForceLogout /
                // ForceTokenRefresh listeners and SyncTrafficControlService are registered in
                // RegisterSignalR (which runs AFTER RegisterApiServices). Putting them here
                // silently no-op'd because ISignalREventBus didn't exist yet.

                // Register RevitApiService (no auth needed - read-only reference data)
                var revitApiService = new RevitApiService(baseUrl, logger, services.GetService<BIManage.Infrastructure.Network.SslValidationPolicy>());
                services.RegisterSingleton<RevitApiService>(revitApiService);

                // Auto-load command cache from API (fire-and-forget, non-blocking)
                var cacheRepository = services.GetService<CacheRepository>();
                if (cacheRepository != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await AutoLoadCommandCacheAsync(revitApiService, cacheRepository, logger);

                            // Extend CommandNameResolver with all commands from cache
                            var loaded = await Commands.CommandNameResolver.LoadFromCacheAsync(cacheRepository);
                            if (loaded > 0)
                                logger?.LogInfo($"CommandNameResolver: Loaded {loaded} additional commands from cache");
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning($"Auto-load command cache failed (non-critical): {ex.Message}");
                        }
                    });
                }

                logger.LogInfo($"API services registered (BaseUrl: {baseUrl}, SignalR: {(signalRService != null ? "enabled" : "disabled")}, OfflineSync: 30s interval)");

                var ht2 = services.GetService<Infrastructure.DependencyInjection.BootstrapHealthTracker>();
                if (ht2 != null) ht2.ApiServicesReady = true;
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to register API services: {ex.Message}", ex);
                var ht3 = services.GetService<Infrastructure.DependencyInjection.BootstrapHealthTracker>();
                if (ht3 != null) { ht3.ApiServicesReady = false; ht3.LastError = ex.Message; }
            }
        }

        /// <summary>
        /// Reads an AppSetting from the DLL's own config file (BIManageRevit.dll.config).
        /// Delegates to <see cref="BIManage.Common.Helpers.AppConfigReader"/> — kept here as a
        /// thin wrapper so existing call sites in this file don't need to change.
        /// </summary>
        private static string ReadDllAppSetting(string key)
            => BIManage.Common.Helpers.AppConfigReader.Read(key);

        // Single-flight gate for AutoAuthenticateDeviceAsync (2026-05-25). Two
        // bootstrap call sites (RegisterApiServices license-validation block +
        // ModelSync registration auto-fetch block) used to fire-and-forget two
        // parallel auth tasks. Each one independently refreshed the token and
        // triggered SignalR.ConnectAsync, producing two connections that both
        // joined every group twice and double-delivered every broadcast — the
        // root cause of "Rule/Command/Event sometimes works, sometimes doesn't".
        // This field ensures the second caller awaits the first instead of
        // running auth in parallel.
        private System.Threading.Tasks.Task? _activeAuthTask;
        private readonly object _authTaskLock = new object();

        /// <summary>
        /// Idempotent wrapper around <see cref="AutoAuthenticateDeviceAsync"/> — guarantees
        /// only ONE auth attempt is in flight at a time. Concurrent callers receive the
        /// same Task and observe the same result. Returns CompletedTask if already
        /// authenticated. This is the public entry point both bootstrap call sites use.
        /// </summary>
        private System.Threading.Tasks.Task EnsureAuthenticatedOnceAsync(
            ServiceRegistry services,
            AuthTokenManager tokenManager,
            AuthApiService authApi,
            ILogger logger)
        {
            lock (_authTaskLock)
            {
                if (tokenManager.IsAuthenticated)
                    return System.Threading.Tasks.Task.CompletedTask;

                if (_activeAuthTask != null && !_activeAuthTask.IsCompleted)
                {
                    logger?.LogDebug("Auth already in flight — joining existing task instead of starting a parallel one");
                    return _activeAuthTask;
                }

                _activeAuthTask = AutoAuthenticateDeviceAsync(services, tokenManager, authApi, logger);
                return _activeAuthTask;
            }
        }

        /// <summary>
        /// Attempts to authenticate the device automatically on startup.
        /// Priority: 1) Use stored refresh token, 2) Try ValidateDevice API with machine ID.
        /// If both fail, device needs manual registration with license key.
        /// </summary>
        private async System.Threading.Tasks.Task AutoAuthenticateDeviceAsync(
            ServiceRegistry services,
            AuthTokenManager tokenManager,
            AuthApiService authApi,
            ILogger logger)
        {
            // Restore admin session state before refreshing tokens
            // so the correct refresh endpoint (admin-refresh vs device-refresh) is used.
            // See the matching block in RegisterApiServices for why role flags are also accepted.
            var secureStorage = services.GetService<SecureTokenStorage>();
            if (secureStorage?.HasStoredIdentity == true)
            {
                var stored = secureStorage.LoadUserIdentity();
                var looksLikeAdmin = stored != null
                    && (!string.IsNullOrEmpty(stored.ProfileId)
                        || stored.IsCompanyAdmin
                        || stored.IsProjectAdmin);
                if (looksLikeAdmin)
                {
                    tokenManager.RestoreAdminSessionFlag(true);
                }
            }

            // Step 1: Check if we have a stored refresh token (previously authenticated)
            if (tokenManager.HasStoredRefreshToken)
            {
                logger?.LogInfo("Attempting authentication using stored refresh token...");
                var token = await tokenManager.GetAccessTokenAsync();
                if (token != null)
                {
                    logger?.LogInfo("Device authenticated via stored refresh token");
                    RestoreUserIdentityFromStorage(services, logger);
                    TriggerSignalRConnect(services, token, logger);
                    return;
                }
                logger?.LogWarning("Stored refresh token expired or invalid");
            }

            // Step 2: Try to validate device by machine ID (device might be registered server-side)
            var machineId = Common.Helpers.MachineIdentifier.GetMachineId(logger);
            logger?.LogInfo($"Attempting device validation for machine: {machineId}");

            try
            {
                var response = await authApi.ValidateDeviceAsync(machineId);
                if (response?.AccessToken != null)
                {
                    tokenManager.SetTokensFromDevice(response);

                    // Update SignalR config with CompanyId from device auth response
                    if (!string.IsNullOrEmpty(response.CompanyId))
                    {
                        var signalRConfig = services.GetService<SignalRConfiguration>();
                        if (signalRConfig != null)
                        {
                            signalRConfig.CompanyId = response.CompanyId;
                            logger?.LogInfo($"SignalR: CompanyId set from device auth: {response.CompanyId}");
                        }
                    }

                    RestoreUserIdentityFromStorage(services, logger);
                    BIManageRevit.Commands.RibbonCommands.DeviceStatusCommand.UpdateButtonAppearance(true);
                    logger?.LogInfo($"Device validated and authenticated: {machineId}");
                    TriggerSignalRConnect(services, response.AccessToken, logger);
                    return;
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Device validation failed: {ex.Message}");
            }

            // Step 3: Device is not authenticated - set red icon (no toast)
            logger?.LogWarning($"Device not authenticated. Machine ID: {machineId}");
            BIManageRevit.Commands.RibbonCommands.DeviceStatusCommand.UpdateButtonAppearance(false);
        }

        /// <summary>
        /// Non-blocking license warning toast. Runs on its own STA thread with its own
        /// Dispatcher so Revit's UI thread is never blocked (replaces the modal MessageBox
        /// that caused the ~80s startup freeze documented in
        /// BIManageRevit_20260520_2204.log).
        /// </summary>
        private static void ShowLicenseWarningToast(
            string message,
            BIManageRevit.BIManage.Views.Auth.LicenseToastSeverity severity,
            ILogger logger)
        {
            try
            {
                var staThread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        // Errors / Breached license stay visible until dismissed; informational
                        // grace-period / passive notices auto-close after 12s so they don't pile
                        // up across heartbeat re-validations.
                        var autoClose = severity == BIManageRevit.BIManage.Views.Auth.LicenseToastSeverity.Error
                            ? TimeSpan.Zero
                            : TimeSpan.FromSeconds(12);
                        var toast = new BIManageRevit.BIManage.Views.Auth.LicenseWarningToast(message, severity, autoClose);
                        toast.Closed += (s, args) =>
                        {
                            try { System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
                            catch { }
                        };
                        toast.Show();
                        System.Windows.Threading.Dispatcher.Run();
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning($"License warning toast thread failed: {ex.Message}");
                    }
                });
                staThread.SetApartmentState(System.Threading.ApartmentState.STA);
                staThread.IsBackground = true;
                staThread.Start();
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"License warning toast launch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows a non-blocking toast warning in the bottom-right corner of the screen.
        /// User can click the toast to open the license key dialog, or close it to dismiss.
        /// </summary>
        private void ShowDeviceRegistrationWarning(
            ServiceRegistry services,
            AuthApiService authApi,
            AuthTokenManager tokenManager,
            string machineId,
            ILogger? logger)
        {
            try
            {
                // Run toast on a dedicated STA thread (Revit has no WPF Application.Current)
                var staThread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        var toast = new BIManageRevit.BIManage.Views.Auth.DeviceWarningToast();
                        BIManageRevit.Commands.RibbonCommands.DeviceStatusCommand.ActiveToast = toast;
                        toast.Show();

                        toast.Closed += (s, args) =>
                        {
                            if (!toast.UserClickedRegister)
                            {
                                logger?.LogInfo("Device registration warning dismissed by user");
                                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
                                return;
                            }

                            // User clicked the toast - show license key dialog
                            var dialog = new BIManageRevit.BIManage.Views.Auth.LicenseKeyDialog(machineId);
                            var dialogResult = dialog.ShowDialog();

                            if (dialogResult != true || string.IsNullOrWhiteSpace(dialog.LicenseKey))
                            {
                                logger?.LogInfo("Device registration cancelled");
                                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
                                return;
                            }

                            var licenseKey = dialog.LicenseKey.Trim();
                            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);

                            // Register device on background thread
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    logger?.LogInfo("Registering device with license key...");
                                    var regResponse = await authApi.RegisterDeviceAsync(licenseKey, machineId);

                                    if (regResponse?.AccessToken != null)
                                    {
                                        tokenManager.SetTokensFromDevice(regResponse);

                                        if (!string.IsNullOrEmpty(regResponse.CompanyId))
                                        {
                                            var signalRConfig = services.GetService<SignalRConfiguration>();
                                            if (signalRConfig != null)
                                                signalRConfig.CompanyId = regResponse.CompanyId;
                                        }

                                        RestoreUserIdentityFromStorage(services, logger);
                                        BIManageRevit.Commands.RibbonCommands.DeviceStatusCommand.UpdateButtonAppearance(true);
                                        logger?.LogInfo($"Device registered successfully: {machineId}");
                                    }
                                    else
                                    {
                                        logger?.LogWarning("Device registration failed - invalid response");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger?.LogWarning($"Device registration failed: {ex.Message}");
                                }
                            });
                        };

                        System.Windows.Threading.Dispatcher.Run();
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning($"Device warning toast thread failed: {ex.Message}");
                    }
                });
                staThread.SetApartmentState(System.Threading.ApartmentState.STA);
                staThread.IsBackground = true;
                staThread.Start();
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Device registration warning failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores user identity from secure storage after successful token authentication.
        /// This sets the user role in UserService, which triggers RibbonVisibilityManager
        /// to show/hide buttons based on the restored role.
        /// </summary>
        private void RestoreUserIdentityFromStorage(ServiceRegistry services, ILogger logger)
        {
            try
            {
                var secureStorage = services.GetService<SecureTokenStorage>();
                var userService = services.GetService<BIManage.Core.Identity.IUserService>();

                if (secureStorage == null || userService == null) return;
                if (!secureStorage.HasStoredIdentity) return;

                var stored = secureStorage.LoadUserIdentity();
                if (stored == null) return;

                var identity = new BIManage.Core.Identity.UserIdentity
                {
                    UserId = stored.UserId ?? Environment.UserName,
                    UserName = stored.UserName ?? Environment.UserName,
                    Email = stored.Email,
                    RevitUserName = Environment.UserName,
                    CompanyId = stored.CompanyId,
                    CompanyName = stored.CompanyName,
                    ProfileId = stored.ProfileId,
                    IsCompanyAdmin = stored.IsCompanyAdmin,
                    IsProjectAdmin = stored.IsProjectAdmin,
                    IsAuthorized = true,
                    LastUpdated = DateTime.UtcNow
                };

                userService.SetCurrentUser(identity);
                logger?.LogInfo($"User identity restored from storage: {identity.Email ?? identity.UserName} (Role: {identity.CurrentRole})");
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Failed to restore user identity: {ex.Message}");
            }
        }

        /// <summary>
        /// Kicks off a SignalR connect with the just-acquired access token. Listener registration
        /// (which races with auth on startup) skips its own auto-connect when no token is yet
        /// available; this method handles the retry once auth succeeds. Fire-and-forget — failures
        /// fall back to the connection manager's normal reconnect logic.
        /// </summary>
        private void TriggerSignalRConnect(ServiceRegistry services, string accessToken, ILogger logger)
        {
            if (string.IsNullOrEmpty(accessToken))
                return;

            var connectionManager = services.GetService<ISignalRConnectionManager>();
            if (connectionManager == null)
                return;

            // Already connected (auto-connect won the race) → nothing to do.
            if (connectionManager.IsConnected)
                return;

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await connectionManager.ConnectAsync(accessToken);
                    logger?.LogInfo("SignalR: Connected after authentication completed.");
                }
                catch (Exception ex)
                {
                    logger?.LogWarning($"SignalR: Post-auth connect failed - {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Auto-loads command cache from API at startup if cache is empty.
        /// Fetches all commands with canHaveBinding and canWorkWithSelection flags.
        /// Non-blocking, fire-and-forget.
        /// </summary>
        private async System.Threading.Tasks.Task AutoLoadCommandCacheAsync(
            RevitApiService revitApiService,
            CacheRepository cacheRepository,
            ILogger logger)
        {
            try
            {
                var cacheCount = await cacheRepository.GetCommandCacheCountAsync();
                if (cacheCount > 0)
                {
                    logger?.LogDebug($"Command cache already has {cacheCount} entries, skipping auto-load");
                    return;
                }

                logger?.LogInfo("Command cache is empty, auto-loading from API...");
                var commands = await revitApiService.GetCommandsAsync();

                if (commands.Count == 0)
                {
                    logger?.LogWarning("API returned 0 commands, cache remains empty");
                    return;
                }

                // Clear existing data and reload with fresh API data
                await cacheRepository.ClearCommandCacheAsync();

                var commandList = commands
                    .Where(c => !string.IsNullOrEmpty(c.CommandName))
                    .Select(c => (c.CommandName, c.MemberName, c.Description, c.CanHaveBinding, c.NeedBinding, c.CanWorkWithSelection, c.Code))
                    .ToList();

                var cachedCount = await cacheRepository.UpsertCommandCacheBatchAsync(commandList);
                logger?.LogInfo($"Auto-loaded {cachedCount} commands to cache (binding: {commands.Count(c => c.CanHaveBinding)}, needBinding: {commands.Count(c => c.NeedBinding)}, selection: {commands.Count(c => c.CanWorkWithSelection)})");
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Auto-load command cache failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies per-module feature gating based on the current license state.
        /// - Licensed: all purchased modules enabled via FeatureToggleService flags.
        /// - Passive: only Protection + ActivityTracker; others disabled.
        /// - Breached: all features paused via IsGlobalPaused.
        /// </summary>
        private static void ApplyModuleGating(ServiceRegistry services, Licensing.LicenseInfo info, ILogger? logger)
        {
            var featureService = services.GetService<IFeatureToggleService>();
            if (featureService == null || info == null) return;

            // Protection module
            var protectionEnabled = info.IsModuleEnabled(Licensing.LicenseModule.Protection);
            featureService.SetFeatureEnabled("CommandInterception", protectionEnabled);
            featureService.SetFeatureEnabled("RuleEvaluation", protectionEnabled);
            featureService.SetFeatureEnabled("EventProtection", protectionEnabled);

            // Health Monitor module
            featureService.SetFeatureEnabled("HealthMonitor", info.IsModuleEnabled(Licensing.LicenseModule.HealthMonitor));

            // Sync Control module
            var syncEnabled = info.IsModuleEnabled(Licensing.LicenseModule.SyncControl);
            featureService.SetFeatureEnabled("BackgroundSync", syncEnabled);
            featureService.SetFeatureEnabled("SyncQueueControl", syncEnabled);
            featureService.SetFeatureEnabled("BackgroundRelinquish", syncEnabled);

            // AI module
            featureService.SetFeatureEnabled("AI", info.IsModuleEnabled(Licensing.LicenseModule.AI));

            // If Breached, pause everything as a kill switch
            if (info.Mode == Licensing.LicenseMode.Breached)
            {
                featureService.IsGlobalPaused = true;
                logger?.LogWarning("License breached — all features paused via IsGlobalPaused");
            }
            else
            {
                featureService.IsGlobalPaused = false;
            }

            logger?.LogInfo($"License module gating applied: mode={info.Mode}, " +
                            $"protection={protectionEnabled}, health={info.IsModuleEnabled(Licensing.LicenseModule.HealthMonitor)}, " +
                            $"sync={syncEnabled}, ai={info.IsModuleEnabled(Licensing.LicenseModule.AI)}");
        }

        private void RegisterAI(ServiceRegistry services)
        {
            var logger = services.GetRequiredService<ILogger>();

            try
            {
                // AI:UseBackendProxy gates whether chat requests go through the ZeManage
                // backend (which holds the OpenAI key in Azure Key Vault) instead of being
                // called directly from the plugin. Default false so behaviour doesn't change
                // until the backend's /api/v1/ai/chat endpoint is live and the flag is flipped.
                var useBackendProxyRaw = ReadDllAppSetting("AI:UseBackendProxy") ?? "false";
                var useBackendProxy = string.Equals(useBackendProxyRaw, "true",
                    StringComparison.OrdinalIgnoreCase);

                var aiConfig = new AIProviderConfig
                {
                    Provider = "openai",
                    ApiKey = ReadDllAppSetting("AI:ApiKey")
                             ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
                    Model = ReadDllAppSetting("AI:Model"),
                    UseBackendProxy = useBackendProxy
                };

                var jsonFallback = new JsonKnowledgeProvider(logger);
                var httpClient = services.GetService<AuthenticatedHttpClient>();

                IKnowledgeProvider knowledgeProvider;
                if (httpClient != null)
                {
                    knowledgeProvider = new ApiKnowledgeProvider(httpClient, jsonFallback, logger);
                    logger.LogInfo("AI knowledge: ApiKnowledgeProvider (with JSON fallback)");
                }
                else
                {
                    knowledgeProvider = jsonFallback;
                    logger.LogInfo("AI knowledge: JsonKnowledgeProvider (no HTTP client)");
                }

                services.RegisterSingleton<IKnowledgeProvider>(knowledgeProvider);
                services.RegisterSingleton<JsonKnowledgeProvider>(jsonFallback);

                // Always register the provider — it handles missing API keys gracefully
                // by returning an error message in the chat instead of blocking startup.
                // Pass httpClient through so proxy mode has a JWT-bearing transport — when
                // useBackendProxy=false the provider just ignores it and calls OpenAI directly.
                var aiProvider = AIProviderFactory.Create(aiConfig, knowledgeProvider, logger, httpClient);
                services.RegisterSingleton<IAIProvider>(aiProvider);

                // Register ChatRepository for persistent chat history
                try
                {
                    var logDir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BIManageRevit", "Logs");
                    var dbPath = System.IO.Path.Combine(logDir, "bimanage.db");
                    var chatRepo = new global::BIManage.Data.SQLite.ChatRepository(dbPath, logger);
                    services.RegisterSingleton<global::BIManage.Data.SQLite.ChatRepository>(chatRepo);
                    logger.LogInfo("ChatRepository registered for AI chat history persistence");
                }
                catch (Exception chatEx)
                {
                    logger.LogWarning($"Failed to register ChatRepository (non-critical): {chatEx.Message}");
                }

                // Register LocalDbQueryService for AI database queries
                try
                {
                    var logDir2 = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BIManageRevit", "Logs");
                    var dbPath2 = System.IO.Path.Combine(logDir2, "bimanage.db");
                    var dbQuerySvc = new global::BIManage.AI.LocalDbQueryService(dbPath2, logger);
                    services.RegisterSingleton<global::BIManage.AI.LocalDbQueryService>(dbQuerySvc);
                    logger.LogInfo("LocalDbQueryService registered for AI database queries");
                }
                catch (Exception dbEx)
                {
                    logger.LogWarning($"Failed to register LocalDbQueryService (non-critical): {dbEx.Message}");
                }

                if (!string.IsNullOrEmpty(aiConfig.ApiKey))
                {
                    logger.LogInfo($"AI services registered (Provider: {aiConfig.Provider}, Model: {aiConfig.Model ?? "default"})");
                }
                else
                {
                    logger.LogWarning("AI:ApiKey not configured - AI chat will prompt user to set API key. Set AI:ApiKey in App.config or OPENAI_API_KEY env var.");
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to register AI services: {ex.Message}", ex);
            }
        }

        private async System.Threading.Tasks.Task InitializePersistenceSession(ServiceRegistry services, InitContext context)
        {
            var logger = services.GetRequiredService<ILogger>();
            var sessionRepository = services.GetService<SessionRepository>();
            var revitContext = services.GetRequiredService<IRevitContext>();

            if (sessionRepository == null)
            {
                logger?.LogWarning("SessionRepository not available - session tracking disabled");
                return;
            }

            try
            {
                // Detect crashed sessions from previous runs
                var crashedSessionIds = await sessionRepository.DetectCrashedSessionsAsync(heartbeatTimeoutMinutes: 5);
                if (crashedSessionIds.Count > 0)
                {
                    logger?.LogWarning($"Marked {crashedSessionIds.Count} orphaned session(s) as crashed");

                    // PATCH each crashed session to the server
                    var sessionSync = services.GetService<SessionSyncService>();
                    var modelSessionSync = services.GetService<Infrastructure.Api.ModelSessionSyncService>();
                    foreach (var crashedId in crashedSessionIds)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // Update session status
                                if (sessionSync != null)
                                    await sessionSync.UpdateSessionAsync(crashedId, "Crashed", isActive: false, crashDetected: true);

                                // Update model session status for each document in the crashed session
                                if (modelSessionSync != null)
                                {
                                    var modelGuids = await sessionRepository.GetDocumentSessionModelGuidsAsync(crashedId);
                                    foreach (var modelGuid in modelGuids)
                                    {
                                        await modelSessionSync.UpdateModelSessionStatusAsync(crashedId, modelGuid, "Crashed");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                logger?.LogWarning($"Failed to PATCH crashed session {crashedId}: {ex.Message}");
                            }
                        });
                    }
                }

                // Get Revit version info
                var revitVersion = context.ControlledApplication.VersionNumber;
                var revitBuild = context.ControlledApplication.VersionBuild;

                // Get user info
                var username = Environment.UserName; // Windows username
                var computerName = Environment.MachineName;

                // Get Revit username (different from Windows username)
                // Note: Application.Username requires UIApplication, which isn't available yet
                // This will remain null during bootstrap and can be updated later if needed
                string revitUsername = null;

                // Start session in database
                var session = await sessionRepository.StartSessionAsync(
                    sessionId: revitContext.SessionId.ToString(),
                    revitVersion: revitVersion,
                    revitBuild: revitBuild,
                    username: username,
                    revitUsername: revitUsername,
                    userEmail: null, // Will be populated when user authenticates
                    computerName: computerName);

                logger?.LogInfo($"Persistence session started: {session.SessionId} (Windows: {username}, Revit: {revitUsername ?? "N/A"}, {computerName}, Revit {revitVersion})");

                // Record initial heartbeat
                await sessionRepository.RecordHeartbeatAsync(session.SessionId);

                var htSession = services.GetService<Infrastructure.DependencyInjection.BootstrapHealthTracker>();
                if (htSession != null) htSession.SessionSynced = true;
            }
            catch (Exception ex)
            {
                logger?.LogError($"Failed to initialize persistence session: {ex.Message}", ex);
            }
        }
    }
}
