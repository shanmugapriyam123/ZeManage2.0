using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using System.Threading.Tasks;
using BIManage.Common.Helpers;
using BIManage.Core.Features;
using BIManage.Core.Metrics;
using BIManage.Revit.PinProtection;
using BIManage.Revit.PinProtection.Models;
using BIManage.Core.Rules.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Context;
using BIManage.Revit.Events;
using BIManage.Revit.Execution;
using BIManage.Revit.Helpers;
using BIManage.Revit.Productivity;
using BIManage.Revit.Session;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Core.Evidence;
using BIManage.Core.Protection.Models;
using BIManage.Core.Rules;
using BIManage.Revit.Protection;

namespace BIManage.Revit.EventRegistry
{
    /// <summary>
    ///     Service for registering and managing Revit application-level events
    ///     Handles ControlledApplication event subscriptions
    /// </summary>
    public class EventRegistryService : IEventRegistryService
    {
        private readonly ControlledApplication _controlledApplication;
        private readonly IExternalEventOrchestrator _externalEventOrchestrator;
        private readonly IFeatureToggleService _featureToggleService;
        private readonly IProductivityTracker _productivityTracker;
        private readonly IDocumentSessionManager _sessionManager;
        private readonly ILogger? _logger;
        private readonly ExternalEvent? _togglePinExternalEvent;
        private readonly SessionRepository? _sessionRepository;
        private readonly IRevitContext? _revitContext;
        private readonly SyncRepository? _syncRepository;
        private readonly ModelFileMetricsRepository? _metricsRepository;
        private readonly ModelFileMetricsCollectorService? _metricsCollector;
        private readonly IUIApplicationProvider? _uiApplicationProvider;
        private readonly UnpinnedElementTracker? _unpinnedElementTracker;
        private readonly PositionRestorationService? _positionRestorationService;
        private readonly AuditRepository? _auditRepository;
        private readonly Func<RegisteredModelsRepository?>? _registeredModelsRepoGetter;
        private readonly Func<ModelSessionSyncService?>? _modelSessionSyncGetter;
        private readonly Func<MetricsSyncService?>? _metricsSyncGetter;
        private readonly Func<Revit.SyncTrafficControl.SyncTrafficControlService?>? _syncTrafficControlGetter;
        private readonly Func<ISignalREventBus?>? _signalREventBusGetter;
        // Cloud sync gate (Option D) — null when caller didn't wire it; the cloud gate
        // then short-circuits to "Unavailable" and the existing client-side STC fallback
        // runs. Lazy getter avoids construction-order dependency with the SignalR service.
        private readonly Func<Infrastructure.SignalR.Core.ISignalRConnectionManager?>? _signalRConnectionManagerGetter;
        private readonly Func<Revit.BackgroundSync.BackgroundSyncEngine?>? _backgroundSyncEngineGetter;
        private readonly System.Collections.Generic.Dictionary<string, DocumentOpeningTracker> _documentOpeningTrackers;
        private readonly System.Collections.Generic.Dictionary<string, string> _activeSyncOperations;
        // docKey of syncs cancelled by the traffic-control fallback. Revit still fires
        // DocumentSynchronizedWithCentral after e.Cancel() on local-server workshared models,
        // which would otherwise broadcast a bogus SyncCompleted to peers.
        private readonly System.Collections.Generic.HashSet<string> _fallbackCancelledSyncs = new System.Collections.Generic.HashSet<string>();
        // docKey → 5-second timer that updates Revit's status bar to "Waiting for central
        // file lock — retrying…" if sync hasn't completed by then. Lets the user know
        // the delay (SMB lock retry, BIM 360 settle, etc.) is expected, not a hang.
        // Disposed when DocumentSynchronizedWithCentral fires.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.Timer> _syncStatusRetryTimers
            = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.Timer>();
        // Cache modelGuid per-document (populated at DocumentOpened, cleared at DocumentClosing).
        // Reading via doc.GetWorksharingCentralModelPath().GetModelGUID() from inside the
        // DocumentSynchronizingWithCentral handler crashes Revit 2026 with
        // ApplicationException in ADocument::getModelGUID_ (Document.cpp:3551).
        private readonly System.Collections.Generic.Dictionary<int, string> _modelGuidCache;
        private readonly object _modelGuidCacheLock = new object();
        private readonly Func<IEventProtectionService?>? _eventProtectionServiceGetter;
        private readonly Func<IScreenshotService?>? _screenshotServiceGetter;
        private readonly Func<EvidenceRepository?>? _evidenceRepositoryGetter;
        private readonly Func<OtpRepository?>? _otpRepositoryGetter;
        private readonly Func<bool>? _isAdminCheck;
        private readonly Func<IRuleService?>? _ruleServiceGetter;
        private EventInterventionHandler? _eventInterventionHandler;
        private readonly Protection.ElementBypassDetector? _bypassDetector;
        private readonly Protection.DeletionProtectionGuard? _deletionGuard;
        // Phase 1.f — Command Protection enforcement on Sync via the canonical event hook
        // (SyncCommandBinding.OnBeforeExecuted is unreliable for cloud / programmatic / queued paths).
        private readonly Func<BIManage.Revit.Commands.Bindings.CommandProtectionBinding?>? _commandProtectionGetter;
        // Phase 6 — Cloud-gate deferred local save handler (see ctor doc).
        private readonly Func<BIManage.Revit.SyncTrafficControl.LocalSaveBeforeQueueHandler?>? _localSaveBeforeQueueHandlerGetter;
        private readonly Dictionary<string, HashSet<long>> _copyMonitorTrackedElements = new Dictionary<string, HashSet<long>>();
        private readonly ExternalEvent? _pinAfterEventExternalEvent;
        private bool _disposed;

        public EventRegistryService(
            ControlledApplication controlledApplication,
            IExternalEventOrchestrator externalEventOrchestrator,
            IFeatureToggleService featureToggleService,
            IProductivityTracker productivityTracker,
            IDocumentSessionManager sessionManager,
            ILogger? logger,
            ExternalEvent? togglePinExternalEvent = null,
            SessionRepository? sessionRepository = null,
            IRevitContext? revitContext = null,
            SyncRepository? syncRepository = null,
            ModelFileMetricsRepository? metricsRepository = null,
            ModelFileMetricsCollectorService? metricsCollector = null,
            IUIApplicationProvider? uiApplicationProvider = null,
            UnpinnedElementTracker? unpinnedElementTracker = null,
            PositionRestorationService? positionRestorationService = null,
            AuditRepository? auditRepository = null,
            Func<ModelSessionSyncService?>? modelSessionSyncGetter = null,
            Func<MetricsSyncService?>? metricsSyncGetter = null,
            Func<Revit.SyncTrafficControl.SyncTrafficControlService?>? syncTrafficControlGetter = null,
            Func<ISignalREventBus?>? signalREventBusGetter = null,
            Func<Revit.BackgroundSync.BackgroundSyncEngine?>? backgroundSyncEngineGetter = null,
            Func<IEventProtectionService?>? eventProtectionServiceGetter = null,
            Func<IScreenshotService?>? screenshotServiceGetter = null,
            Func<EvidenceRepository?>? evidenceRepositoryGetter = null,
            Func<OtpRepository?>? otpRepositoryGetter = null,
            Func<bool>? isAdminCheck = null,
            ExternalEvent? pinAfterEventExternalEvent = null,
            Func<IRuleService?>? ruleServiceGetter = null,
            Func<RegisteredModelsRepository?>? registeredModelsRepoGetter = null,
            Protection.ElementBypassDetector? bypassDetector = null,
            Func<Infrastructure.SignalR.Core.ISignalRConnectionManager?>? signalRConnectionManagerGetter = null,
            Func<BIManage.Revit.Commands.Bindings.CommandProtectionBinding?>? commandProtectionGetter = null,
            Protection.DeletionProtectionGuard? deletionGuard = null,
            // Phase 6 — Cloud-gate deferred local save. Lazy getter because the
            // handler is constructed in Application.CreateExternalEvents() and
            // registered in DI from there; resolving it at registration time
            // would race the External-Event creation order. Null when the
            // feature is disabled or the handler hasn't been created yet, in
            // which case any callers should short-circuit.
            Func<BIManage.Revit.SyncTrafficControl.LocalSaveBeforeQueueHandler?>? localSaveBeforeQueueHandlerGetter = null)
        {
            _controlledApplication = controlledApplication ?? throw new ArgumentNullException(nameof(controlledApplication));
            _externalEventOrchestrator = externalEventOrchestrator ?? throw new ArgumentNullException(nameof(externalEventOrchestrator));
            _featureToggleService = featureToggleService ?? throw new ArgumentNullException(nameof(featureToggleService));
            _productivityTracker = productivityTracker ?? throw new ArgumentNullException(nameof(productivityTracker));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _logger = logger;
            _togglePinExternalEvent = togglePinExternalEvent;
            _sessionRepository = sessionRepository;
            _revitContext = revitContext;
            _syncRepository = syncRepository;
            _metricsRepository = metricsRepository;
            _metricsCollector = metricsCollector;
            _uiApplicationProvider = uiApplicationProvider;
            _unpinnedElementTracker = unpinnedElementTracker;
            _positionRestorationService = positionRestorationService;
            _auditRepository = auditRepository;
            _modelSessionSyncGetter = modelSessionSyncGetter;
            _metricsSyncGetter = metricsSyncGetter;
            _syncTrafficControlGetter = syncTrafficControlGetter;
            _signalREventBusGetter = signalREventBusGetter;
            _backgroundSyncEngineGetter = backgroundSyncEngineGetter;
            _documentOpeningTrackers = new System.Collections.Generic.Dictionary<string, DocumentOpeningTracker>();
            _activeSyncOperations = new System.Collections.Generic.Dictionary<string, string>();
            _modelGuidCache = new System.Collections.Generic.Dictionary<int, string>();
            _eventProtectionServiceGetter = eventProtectionServiceGetter;
            _screenshotServiceGetter = screenshotServiceGetter;
            _evidenceRepositoryGetter = evidenceRepositoryGetter;
            _otpRepositoryGetter = otpRepositoryGetter;
            _isAdminCheck = isAdminCheck;
            _pinAfterEventExternalEvent = pinAfterEventExternalEvent;
            _ruleServiceGetter = ruleServiceGetter;
            _registeredModelsRepoGetter = registeredModelsRepoGetter;
            _bypassDetector = bypassDetector;
            _commandProtectionGetter = commandProtectionGetter;
            _signalRConnectionManagerGetter = signalRConnectionManagerGetter;
            _deletionGuard = deletionGuard;
            _localSaveBeforeQueueHandlerGetter = localSaveBeforeQueueHandlerGetter;
        }

        /// <summary>
        ///     Register all ControlledApplication events
        /// </summary>
        public void RegisterControlledApplicationEvents()
        {
            if (!_featureToggleService.IsFeatureEnabled("EventMonitoring"))
            {
                _logger?.LogInfo("Event monitoring is disabled, skipping event registration");
                return;
            }

            try
            {
                // Application events
                RegisterApplicationEvents();

                // Document lifecycle events
                if (_featureToggleService.IsFeatureEnabled("DocumentLifecycleEvents"))
                {
                    RegisterDocumentLifecycleEvents();
                }

                // View tracking for productivity (tracked via DocumentChanged events)
                // ChangeSet extraction
                if (_featureToggleService.IsFeatureEnabled("ChangeSetExtraction"))
                {
                    RegisterDocumentChangedEvent();
                }

                _logger?.LogInfo("ControlledApplication events registered successfully");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to register ControlledApplication events: {ex.Message}", ex);
            }
        }

        /// <summary>
        ///     Register document lifecycle events
        /// </summary>
        private void RegisterDocumentLifecycleEvents()
        {
            _controlledApplication.DocumentOpened += OnDocumentOpened;
            _controlledApplication.DocumentOpening += OnDocumentOpening;
            _controlledApplication.DocumentCreated += OnDocumentCreated;
            _controlledApplication.DocumentCreating += OnDocumentCreating;
            _controlledApplication.DocumentClosed += OnDocumentClosed;
            _controlledApplication.DocumentClosing += OnDocumentClosing;
            _controlledApplication.DocumentSaving += OnDocumentSaving;
            _controlledApplication.DocumentSavingAs += OnDocumentSavingAs;

            _controlledApplication.DocumentSaved += OnDocumentSaved;
            _controlledApplication.DocumentSynchronizingWithCentral += OnDocumentSynchronizingWithCentral;
            _controlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynchronizedWithCentral;
            _controlledApplication.FamilyLoadingIntoDocument += OnFamilyLoadingIntoDocument;
            _controlledApplication.DocumentPrinting += OnDocumentPrinting;

            _logger?.LogInfo("Document lifecycle events registered");
        }

        /// <summary>
        ///     Register application-level events
        /// </summary>
        private void RegisterApplicationEvents()
        {
            _controlledApplication.ApplicationInitialized += OnApplicationInitialized;
            _logger?.LogInfo("Application events registered");
        }

        /// <summary>
        ///     Register DocumentChanged event for ChangeSet extraction
        /// </summary>
        private void RegisterDocumentChangedEvent()
        {
            _controlledApplication.DocumentChanged += OnDocumentChanged;
            _logger?.LogInfo("DocumentChanged event registered for ChangeSet extraction");
        }

        /// <summary>
        ///     Unregister all events
        /// </summary>
        public void UnregisterAllEvents()
        {
            try
            {
                // Document lifecycle events
                _controlledApplication.DocumentOpened -= OnDocumentOpened;
                _controlledApplication.DocumentOpening -= OnDocumentOpening;
                _controlledApplication.DocumentCreated -= OnDocumentCreated;
                _controlledApplication.DocumentCreating -= OnDocumentCreating;
                _controlledApplication.DocumentClosed -= OnDocumentClosed;
                _controlledApplication.DocumentClosing -= OnDocumentClosing;
                _controlledApplication.DocumentSaving -= OnDocumentSaving;
                _controlledApplication.DocumentSavingAs -= OnDocumentSavingAs;

                _controlledApplication.DocumentSaved -= OnDocumentSaved;
                _controlledApplication.DocumentSynchronizingWithCentral -= OnDocumentSynchronizingWithCentral;
                _controlledApplication.DocumentSynchronizedWithCentral -= OnDocumentSynchronizedWithCentral;
                _controlledApplication.FamilyLoadingIntoDocument -= OnFamilyLoadingIntoDocument;
                _controlledApplication.DocumentPrinting -= OnDocumentPrinting;

                // Application events
                _controlledApplication.ApplicationInitialized -= OnApplicationInitialized;

                // DocumentChanged event
                _controlledApplication.DocumentChanged -= OnDocumentChanged;

                _logger?.LogInfo("All events unregistered");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error unregistering events: {ex.Message}", ex);
            }
        }

        #region Event Handlers

        private async void OnDocumentOpened(object sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;

                var doc = e.Document;
                if (doc == null || !doc.IsValidObject)
                {
                    _logger?.LogWarning("EventRegistryService: DocumentOpened with null/invalid document — skipping");
                    return;
                }

                // Re-evaluate the CAD Explode ribbon swap. Initialize() ran at app startup
                // when the Modify | Imports contextual tab didn't exist yet, so the swap may
                // not have hooked the right tab. Refreshing here (now that a document and its
                // contextual ribbon are alive) gives the swap a chance to discover the panel
                // and install the shadow buttons.
                try
                {
                    global::BIManageRevit.BIManage.Revit.Applications.Application.Instance?.RefreshCADExplodeSwapState();
                }
                catch (Exception swapEx) { _logger?.LogDebug($"Explode swap refresh on DocumentOpened: {swapEx.Message}"); }

                // Pre-populate the deletion guard's pinned-element cache from ExtensibleStorage
                // so the guard can detect deletions immediately without waiting for a
                // DocumentChanged scan to build the cache from scratch.
                _deletionGuard?.Bootstrap(doc);

                // Create in-memory document session
                var session = _sessionManager.CreateSession(doc);
                _logger?.LogInfo($"Document opened: {doc.Title}");

                // Persist to database if SessionRepository available
                if (_sessionRepository != null && doc != null)
                {
                    var documentId = doc.GetHashCode().ToString();
                    var documentTitle = doc.Title ?? "Untitled";
                    var isWorkshared = DocumentTypeHelper.IsWorkshared(doc);

                    // Get normalized current working path
                    var modelLocation = Helpers.DocumentInformationHelper.GetNormalizedPath(doc);

                    // Extract central model information (workshared only) — needed for guid reconciliation below
                    string centralModelPath = null;
                    string centralModelName = null;

                    if (isWorkshared)
                    {
                        centralModelPath = Helpers.DocumentInformationHelper.GetCentralModelPath(doc);
                        centralModelName = Helpers.DocumentInformationHelper.GetCentralModelName(doc);
                    }

                    // Get persistent model GUID (works for both workshared and non-workshared)
                    var modelGuid = Helpers.ModelGuidHelper.GetModelGuid(doc, _logger);

                    // Reconcile against existing registration. The raw guid from Revit can drift
                    // (e.g. open-as-detached then save-as-local yields a different ModelPath GUID
                    // than the originally registered one). Without this, model_session POSTs use
                    // the raw guid and the server rejects with FK violation against
                    // Revit_Model_Register_Tab → row gets discarded after offline retry.
                    var modelRepo = _registeredModelsRepoGetter?.Invoke();
                    if (modelRepo != null)
                    {
                        try
                        {
                            var modelNameForLookup = isWorkshared && !string.IsNullOrEmpty(centralModelName)
                                ? centralModelName
                                : (doc.Title ?? string.Empty);
                            var existingGuid = await modelRepo.FindExistingModelGuidAsync(modelNameForLookup, centralModelPath);
                            if (!string.IsNullOrEmpty(existingGuid) && existingGuid != modelGuid)
                            {
                                _logger?.LogInfo($"Reconciling model guid for '{modelNameForLookup}': {modelGuid} → {existingGuid}");
                                modelGuid = existingGuid;
                            }
                        }
                        catch (Exception reconcileEx)
                        {
                            _logger?.LogWarning($"Model guid reconciliation failed (using raw guid): {reconcileEx.Message}");
                        }
                    }

                    // Cache so sync handlers don't re-invoke the native getModelGUID_ path (Revit 2026 crash).
                    SetCachedModelGuid(doc, modelGuid);

                    var projectName = Helpers.DocumentInformationHelper.GetProjectName(doc);
                    var cloudProjectName = Helpers.DocumentInformationHelper.GetCloudProjectName(doc);

                    // Get current session ID
                    var sessionId = _revitContext?.SessionId.ToString() ?? System.Guid.NewGuid().ToString();

                    // Count opened worksets (workshared only)
                    int? openedWorksetsCount = null;
                    if (isWorkshared)
                    {
                        openedWorksetsCount = CountOpenedWorksets(doc);
                    }

                    // Calculate opening duration if start time was tracked
                    DateTime? openingStartedAt = null;
                    double? openingDurationSeconds = null;
                    var pathKey = doc.PathName ?? doc.GetHashCode().ToString();

                    if (_documentOpeningTrackers.TryGetValue(pathKey, out var tracker))
                    {
                        openingStartedAt = tracker.OpeningStartedAt;
                        tracker.DocumentOpenedAt = DateTime.UtcNow;
                        openingDurationSeconds = (tracker.DocumentOpenedAt.Value - tracker.OpeningStartedAt).TotalSeconds;

                        _logger?.LogDebug($"Document opened duration: {openingDurationSeconds:F2}s");

                        // Subscribe to ViewActivated for interactive ready timing
                        SubscribeToViewActivatedForDocument(doc, pathKey);

                        // Note: Do NOT remove tracker yet - keep it for ViewActivated handler
                    }

                    // Determine if this is a local copy (workshared models only)
                    var isLocal = Helpers.DocumentInformationHelper.IsLocalCopy(doc);

                    // Protection 17: Duplicate user session (same username on another machine)
                    // Checked here (not in DocumentOpening) because Revit username and async DB
                    // queries are only safely available after the document is fully opened.
                    if (isWorkshared && _featureToggleService.IsFeatureEnabled("EventProtection"))
                    {
                        var revitUser = doc.Application?.Username;
                        if (!await CheckDuplicateUserSessionProtectionAsync(doc, revitUser, centralModelPath ?? modelLocation))
                            return; // Document closed by the check method
                    }

                    // Protection 2: Block direct central file opening
                    // Checked here (not in DocumentOpening) because only after the document is opened
                    // can we reliably distinguish "Open as Central" from "Create New Local".
                    if (isWorkshared && !isLocal && _featureToggleService.IsFeatureEnabled("EventProtection"))
                    {
                        if (!CheckOpenCentralFileProtection(doc))
                            return; // Document will be closed by the check method
                    }

                    var revitUsername = doc.Application?.Username ?? Environment.UserName;

                    await _sessionRepository.CreateDocumentSessionAsync(
                        sessionId: sessionId,
                        documentId: documentId,
                        modelLocation: modelLocation,
                        documentTitle: documentTitle,
                        projectName: projectName,
                        cloudProjectName: cloudProjectName,
                        openingStartedAt: openingStartedAt,
                        openingDurationSeconds: openingDurationSeconds,
                        modelGuid: modelGuid,
                        centralModelPath: centralModelPath,
                        centralModelName: centralModelName,
                        openedWorksetsCount: openedWorksetsCount,
                        isLocal: isLocal,
                        createdBy: revitUsername);

                    // Sync model session to API (fire-and-forget)
                    // All fields must be present (non-null) — server returns 500 if fields are missing
                    SyncModelSessionToApiAsync(new ModelSessionApiRequest
                    {
                        SessionId = sessionId,
                        ModelGuid = modelGuid,
                        CentralModelPath = centralModelPath ?? modelLocation ?? "",
                        CentralModelName = centralModelName ?? documentTitle ?? "",
                        LocalModelLocation = modelLocation ?? "",
                        DocumentTitle = documentTitle ?? "",
                        LocalProjectName = projectName ?? "",
                        CloudProjectName = cloudProjectName ?? "",
                        OpenedAt = DateTime.UtcNow,
                        OpeningStartedAt = openingStartedAt ?? DateTime.UtcNow,
                        ModelOpeningDuration = openingDurationSeconds ?? 0,
                        OpenedWorksetsCount = openedWorksetsCount ?? 0,
                        Status = "Active",
                        TotalModifications = 0,
                        CreatedBy = revitUsername,
                        ModifiedBy = revitUsername,
                        RevitUsername = revitUsername
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnDocumentOpened handler: {ex.Message}", ex);
            }
        }

        private void OnDocumentOpening(object sender, Autodesk.Revit.DB.Events.DocumentOpeningEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;

                // Only track opening time if we have a valid path
                // (new/unsaved documents don't have paths and can't be reliably tracked)
                if (string.IsNullOrEmpty(e.PathName))
                {
                    _logger?.LogDebug("Document opening started without path (new document) - skipping timing");
                    return;
                }

                // === Event Protection Checks (pre-action, cancellable) ===
                // Skip all protection checks for external resource paths (IExternalResourceServer).
                // These are internal Revit operations for loading linked keynotes, shared parameters,
                // assembly codes, etc. Intercepting them blocks the resource resolution pipeline.
                if (_featureToggleService.IsFeatureEnabled("EventProtection") && !IsNonFileSystemPath(e.PathName))
                {
                    // Protection 1: Non-approved location
                    if (!CheckOpenFileFromNonApprovedProtection(e.PathName))
                    {
                        if (e.Cancellable) e.Cancel();
                        return;
                    }

                    // Protection 2: Central file check moved to OnDocumentOpened
                    // (DocumentOpening cannot distinguish "Open as Central" from "Create New Local")

                    // Protection 3: Model upgrade
                    if (!CheckModelUpgradeProtection(e.PathName))
                    {
                        if (e.Cancellable) e.Cancel();
                        return;
                    }

                    // Protection 17: Duplicate user session (pre-open via local GUID lookup + API)
                    if (!CheckDuplicateUserSessionProtectionBeforeOpen(e.PathName))
                    {
                        if (e.Cancellable) e.Cancel();
                        return;
                    }
                    // OnDocumentOpened retains a fallback check for models not yet registered locally.
                }

                // Track opening start time with new tracker
                var pathKey = e.PathName;
                var tracker = new DocumentOpeningTracker
                {
                    OpeningStartedAt = DateTime.UtcNow,
                    PathKey = pathKey,
                    ViewActivationHandled = false
                };

                _documentOpeningTrackers[pathKey] = tracker;
                _logger?.LogDebug($"Document opening started: {e.PathName}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnDocumentOpening handler: {ex.Message}", ex);
            }
        }

        private void OnDocumentCreated(object sender, Autodesk.Revit.DB.Events.DocumentCreatedEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;
                _logger?.LogInfo($"Document created: {e.Document?.Title}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnDocumentCreated handler: {ex.Message}", ex);
            }
        }

        private void OnDocumentCreating(object sender, Autodesk.Revit.DB.Events.DocumentCreatingEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;
                _logger?.LogDebug("Document creating");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnDocumentCreating handler: {ex.Message}", ex);
            }
        }

        private void OnDocumentClosed(object sender, Autodesk.Revit.DB.Events.DocumentClosedEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;
                _logger?.LogInfo("Document closed");

                // Clear the deletion guard's pinned-element cache for the closed document
                _deletionGuard?.ClearCache();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnDocumentClosed handler: {ex.Message}", ex);
            }
        }

        private async void OnDocumentClosing(object sender, Autodesk.Revit.DB.Events.DocumentClosingEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;

                _logger?.LogDebug($"Document closing: {e.Document?.Title}");

                // Clean up opening tracker if document closes before ViewActivated
                if (e.Document != null)
                {
                    var pathKey = e.Document.PathName ?? e.Document.GetHashCode().ToString();
                    if (_documentOpeningTrackers.ContainsKey(pathKey))
                    {
                        _documentOpeningTrackers.Remove(pathKey);
                        UnsubscribeFromViewActivatedForDocument(pathKey);
                        _logger?.LogDebug($"Cleaned up opening tracker for closing document: {pathKey}");
                    }

                    // Close in-memory document session
                    _sessionManager.CloseSession(e.Document);
                }

                // Close database session and sync "Closed" status to API
                if (_sessionRepository != null && e.Document != null)
                {
                    var documentId = e.Document.GetHashCode().ToString();
                    var revitUsername = e.Document.Application?.Username ?? Environment.UserName;
                    await _sessionRepository.CloseDocumentSessionAsync(documentId, revitUsername);

                    // Sync "Closed" status to model sessions API — read from cache to avoid
                    // the native getModelGUID_ path that crashes Revit during DB state transitions.
                    var sessionId = _revitContext?.SessionId.ToString();
                    if (!TryGetCachedModelGuid(e.Document, out var modelGuid))
                    {
                        modelGuid = Helpers.ModelGuidHelper.GetModelGuid(e.Document, _logger);
                    }
                    if (!string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(modelGuid))
                    {
                        SyncModelSessionStatusUpdateAsync(sessionId, modelGuid, "Closed", revitUsername);
                    }
                }

                // Clear cache entry now that the doc is closing.
                ClearCachedModelGuid(e.Document);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnDocumentClosing handler: {ex.Message}", ex);
            }
        }


        private async void OnDocumentSaving(object sender, Autodesk.Revit.DB.Events.DocumentSavingEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;

                var doc = e.Document;
                _logger?.LogDebug($"Document saving: {doc?.Title}");

                // === Event Protection Check: Save Over Earlier Version ===
                if (_featureToggleService.IsFeatureEnabled("EventProtection") && doc != null)
                {
                    if (!CheckSaveOverEarlierVersionProtection(doc))
                    {
                        if (e.Cancellable) e.Cancel();
                        return;
                    }
                }

                // Collect fast metrics before save
                if (_metricsRepository != null && _metricsCollector != null && doc != null)
                {
                    try
                    {
                        var sessionId = _revitContext?.SessionId.ToString() ?? Guid.NewGuid().ToString();
                        // Prefer cached modelGuid to avoid the native getModelGUID_ round-trip
                        // on every save (same reason as the STC handlers).
                        if (!TryGetCachedModelGuid(doc, out var modelGuid))
                        {
                            modelGuid = Helpers.ModelGuidHelper.GetModelGuid(doc, _logger);
                        }
                        var modelPath = Helpers.DocumentInformationHelper.GetNormalizedPath(doc);
                        var modelName = doc.Title;
                        var documentId = doc.GetHashCode().ToString();

                        // Get Revit username from session
                        var savedBy = Environment.UserName;
                        if (_sessionRepository != null && _revitContext != null)
                        {
                            try
                            {
                                var session = await _sessionRepository.GetSessionAsync(sessionId);
                                if (session != null && !string.IsNullOrEmpty(session.RevitUsername))
                                {
                                    savedBy = session.RevitUsername;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning($"Failed to get Revit username from session: {ex.Message}");
                            }
                        }

                        var metrics = _metricsCollector.CollectFastMetrics(doc);

                        var captureId = await _metricsRepository.InsertFastMetricsAsync(
                            sessionId: sessionId,
                            documentId: documentId,
                            modelGuid: modelGuid,
                            modelPath: modelPath,
                            modelName: modelName,
                            capturedBy: savedBy,
                            metrics: metrics,
                            captureTypeValue: "save",
                            syncGuid: null);

                        _logger?.LogDebug("Captured fast metrics before save");

                        // Sync to API (fire-and-forget)
                        SyncSyncSaveMetricsToApiAsync(captureId, sessionId, documentId, modelGuid, modelPath, modelName, "save", Guid.NewGuid().ToString(), savedBy, metrics);
                    }
                    catch (Exception metricsEx)
                    {
                        _logger?.LogWarning($"Failed to capture metrics before save: {metricsEx.Message}");
                        // Don't throw - metrics collection failure shouldn't block save
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnDocumentSaving handler: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// DocumentSavingAs: enforces Ze_DocumentSaveAsProtection for Save As → Project.
        /// BeforeExecuted on ID_REVIT_FILE_SAVE_AS is non-cancellable (confirmed by Revit journal
        /// N++EB(NB) flag), so this event is the only reliable interception point.
        /// Fires after the user selects a file in the native picker but before the file is written.
        /// </summary>
        private void OnDocumentSavingAs(object sender, Autodesk.Revit.DB.Events.DocumentSavingAsEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;

                // Plugin-internal SaveAs (e.g. Deep Analysis writing temp .rfa files to
                // measure family size) sets this scope so the user-facing dialog is skipped.
                if (BIManage.Revit.Protection.EventProtectionSuppression.IsActive) return;

                var doc = e.Document;
                if (doc == null) return;

                if (!_featureToggleService.IsFeatureEnabled("EventProtection"))
                    return;

                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null || !protectionService.IsProtectionEnabled)
                    return;

                var saveAsProtection = protectionService.GetProtectionByDummyCommandId("Ze_DocumentSaveAsProtection");
                if (saveAsProtection == null || !saveAsProtection.Enabled)
                    return;

                var registeredModelsRepo = _registeredModelsRepoGetter?.Invoke();
                if (registeredModelsRepo == null)
                    return;
                var saveAsModelGuid = Helpers.ModelGuidHelper.GetModelGuid(doc, _logger);
                var isRegistered = !string.IsNullOrEmpty(saveAsModelGuid) &&
                    registeredModelsRepo.IsModelRegisteredAsync(saveAsModelGuid).GetAwaiter().GetResult();
                if (!isRegistered)
                {
                    _logger?.LogInfo($"Save As protection skipped — '{doc.Title}' is not a registered model.");
                    return;
                }

                if (!e.Cancellable)
                {
                    _logger?.LogInfo($"DocumentSavingAs: event not cancellable for '{doc.Title}' — cannot block.");
                    return;
                }

                var auditLogId = Guid.NewGuid().ToString();
                var context = new EventContext
                {
                    Document = doc,
                    FilePath = doc.PathName,
                    CurrentRevitVersion = doc.Application?.VersionNumber
                };

                if (saveAsProtection.CaptureBeforeScreenshot)
                    CaptureEventScreenshot(saveAsProtection, context, "before", auditLogId);

                var handler = GetEventInterventionHandler();

                // Suppress SyncConflictDialogInterceptor while the protection dialog is
                // visible. If we cancel the save, Revit shows "File not saved." — identical
                // to a sync-collision popup. Without the flag the interceptor would catch it
                // and show the Sync Queue dialog spuriously.
                // Flag set here stays active past the return of this handler so the
                // interceptor is still suppressed when Revit shows "File not saved."
                // (which appears AFTER OnDocumentSavingAs returns, not during it).
                // Cleared by: WinEventProc on "File not saved." | OnDocumentSaved on success
                //             | 60-second auto-expiry if file picker was cancelled.
                Revit.SyncTrafficControl.SyncConflictDialogInterceptor.NotifySaveAsStarted();

                var result = handler.ProcessIntervention(saveAsProtection, context,
                    string.IsNullOrEmpty(doc.Title) ? "Save As requested" : $"File: {doc.Title}",
                    calledFromRevitEvent: true);

                string action = result.Allowed && result.UserOverrode ? "override"
                    : result.Allowed ? "allowed"
                    : (saveAsProtection.Mode == InterventionMode.Assist ? "cancelled" : "blocked");
                string? overrideMethod = result.UserOverrode ? "AdminPrivilege" : null;
                LogEventAuditEntry(saveAsProtection, context, action, result.Reason,
                    auditLogId: auditLogId, userComment: result.UserComment, overrideMethod: overrideMethod);

                if (result.Allowed && saveAsProtection.CaptureAfterScreenshot)
                    CaptureEventScreenshot(saveAsProtection, context, "after", auditLogId);

                if (!result.Allowed)
                {
                    e.Cancel();
                    _logger?.LogInfo($"Save As blocked: '{doc.Title}' (mode={saveAsProtection.Mode})");
                }
                else
                {
                    _logger?.LogInfo($"Save As allowed: '{doc.Title}' (mode={saveAsProtection.Mode}, override={result.UserOverrode})");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnDocumentSavingAs handler: {ex.Message}", ex);
            }
        }

        private async void OnDocumentSaved(object sender, Autodesk.Revit.DB.Events.DocumentSavedEventArgs e)
        {
            // Save As completed (or plain Save). Clear the Save As suppression flag so
            // SyncConflictDialogInterceptor resumes normal operation.
            Revit.SyncTrafficControl.SyncConflictDialogInterceptor.NotifySaveAsCompleted();

            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;

                var doc = e.Document;
                _logger?.LogInfo($"Document saved: {doc?.Title}");

                // Record document save in session tracking
                // For workshared models, last_saved_at is NOT updated here (only on sync)
                // For local models, last_saved_at IS updated here
                if (_sessionRepository != null && doc != null && _revitContext != null)
                {
                    try
                    {
                        var sessionId = _revitContext.SessionId.ToString();
                        var documentId = doc.GetHashCode().ToString();
                        var isWorkshared = Helpers.DocumentTypeHelper.IsWorkshared(doc);
                        var revitUsername = doc.Application?.Username ?? Environment.UserName;

                        await _sessionRepository.RecordDocumentSaveAsync(sessionId, documentId, isWorkshared, revitUsername);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"Failed to record document save: {ex.Message}", ex);
                        // Don't throw - save tracking failure shouldn't impact the completed save
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnDocumentSaved handler: {ex.Message}", ex);
            }
        }

        private void OnDocumentPrinting(object sender, Autodesk.Revit.DB.Events.DocumentPrintingEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;

                if (_featureToggleService.IsFeatureEnabled("EventProtection") && e.Document != null)
                {
                    var registeredModelsRepo = _registeredModelsRepoGetter?.Invoke();
                    if (registeredModelsRepo == null) return;
                    var printModelGuid = Helpers.ModelGuidHelper.GetModelGuid(e.Document, _logger);
                    var isPrintRegistered = !string.IsNullOrEmpty(printModelGuid) &&
                        registeredModelsRepo.IsModelRegisteredAsync(printModelGuid).GetAwaiter().GetResult();
                    if (!isPrintRegistered)
                    {
                        _logger?.LogInfo($"Document Printing protection skipped — '{e.Document.Title}' is not a registered model.");
                        return;
                    }

                    // If the command interception (BeforeExecuted on ID_REVIT_FILE_PRINT etc.)
                    // already enforced protection and the user was allowed through, skip this
                    // second pass — otherwise the dialog fires twice for one print action.
                    if (BIManage.Revit.Protection.PrintProtectionGate.ConsumeSuppressed())
                    {
                        _logger?.LogDebug("Document Printing protection skipped — already enforced via command binding.");
                        return;
                    }

                    if (!CheckDocumentPrintingProtection(e.Document))
                    {
                        if (e.Cancellable) e.Cancel();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnDocumentPrinting handler: {ex.Message}", ex);
            }
        }

        private void OnFamilyLoadingIntoDocument(object sender, Autodesk.Revit.DB.Events.FamilyLoadingIntoDocumentEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;
                if (!_featureToggleService.IsFeatureEnabled("EventProtection")) return;

                var familyPath = e.FamilyPath;
                var familyName = e.FamilyName;
                var doc = e.Document;

                _logger?.LogInfo($"[FamilyLoad] Intercepted: '{familyName}' from '{familyPath}'");

                // Skip protection checks for external resource paths (IExternalResourceServer).
                // Families loaded via linked external resources are internal Revit operations.
                if (IsNonFileSystemPath(familyPath))
                {
                    _logger?.LogInfo($"[FamilyLoad] Skipped (non-filesystem path): '{familyPath}'");
                    return;
                }

                // Protection 5: Family Library Location
                if (!CheckFamilyLibraryProtection(familyPath, familyName, doc))
                {
                    if (e.Cancellable) e.Cancel();
                    return;
                }

                // Protection 6: Family Overwrite Protection
                if (!CheckFamilyOverwriteProtection(familyPath, familyName, doc))
                {
                    if (e.Cancellable) e.Cancel();
                    return;
                }

                // Protection 7: Family Version Mismatch
                if (!CheckFamilyVersionMismatchProtection(familyPath, familyName, doc))
                {
                    if (e.Cancellable) e.Cancel();
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnFamilyLoadingIntoDocument handler: {ex.Message}", ex);
            }
        }

        private async void OnDocumentSynchronizingWithCentral(object sender, Autodesk.Revit.DB.Events.DocumentSynchronizingWithCentralEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;

                var doc = e.Document;

                // Sync events should only fire for workshared documents
                if (!Helpers.DocumentTypeHelper.IsWorkshared(doc))
                {
                    _logger?.LogWarning($"Sync event fired for non-workshared document: {doc?.Title}");
                    return;
                }

                var docType = Helpers.DocumentTypeHelper.GetDocumentTypeName(doc);
                _logger?.LogInfo($"Document synchronizing with central ({docType}): {doc?.Title}");

                // === Phase 1.f: Command Protection enforcement for user-initiated Sync ===
                // SyncCommandBinding.OnBeforeExecuted is unreliable here: cloud sync routes through
                // Autodesk's CloudWorksharing.UI add-in which bypasses external command bindings;
                // programmatic doc.SynchronizeWithCentral() (BackgroundSync, AutoExit) skips command
                // dispatch entirely. This event handler IS the canonical, reliable hook for every
                // sync path and exposes e.Cancellable + e.Cancel().
                //
                // The check runs BEFORE the cloud gate and STC-fallback recording — a cancelled
                // sync therefore never enters the queue / records a local-sync-start, keeping
                // sync queue and background-sync state machines untouched.
                //
                // SyncProtectionGate skips automated dispatches: background sync's auto-loop,
                // auto-exit-on-shutdown, and the "your turn" queued retry. The user already
                // consented at queue-entry / settings; re-prompting on every retry would
                // retry-storm or surprise the user with a second dialog.
                if (!SyncTrafficControl.SyncProtectionGate.IsInternalSync
                    && _featureToggleService.IsFeatureEnabled("CommandInterception")
                    && doc != null)
                {
                    var commandProtection = _commandProtectionGetter?.Invoke();
                    if (commandProtection != null)
                    {
                        // Canonical IDs from command_cache (what the admin UI saves into
                        // command_settings) come first; PostableCommand-resolved aliases follow
                        // as a defensive fallback for older Revit versions / alternate dispatch.
                        foreach (var syncCmdKey in new[]
                        {
                            "ID_SYNCHRONIZE_NOW",
                            "ID_SYNCHRONIZE_AND_MODIFY_SETTINGS",
                            "ID_FILE_SAVE_TO_CENTRAL_SHORTCUT",
                            "ID_FILE_SAVE_TO_CENTRAL"
                        })
                        {
                            var result = commandProtection.EvaluateCommand(
                                syncCmdKey,
                                doc,
                                new System.Collections.Generic.List<Element>(),
                                bypassSource: "Sync to Central");
                            if (!result.HasProtection) continue;
                            if (!result.Allowed)
                            {
                                _logger?.LogWarning($"Command Protection blocked sync ({syncCmdKey}, mode={result.Mode}, reason={result.Reason})");
                                if (e.Cancellable)
                                {
                                    e.Cancel();
                                    // Suppress the bogus SyncCompleted broadcast that
                                    // DocumentSynchronizedWithCentral would otherwise emit for
                                    // the cancelled local-server sync.
                                    lock (_fallbackCancelledSyncs)
                                    {
                                        _fallbackCancelledSyncs.Add(doc.GetHashCode().ToString());
                                    }
                                }
                                return;
                            }
                            break;
                        }
                    }
                }

                // Status-bar feedback: tell the user sync is in progress + arm a 5s
                // retry-message timer so a stalled sync (SMB lock contention, BIM 360
                // settle window, etc.) doesn't look like a hang. Same channel
                // background-sync uses (prefix "ZeManage: …"). The 5s threshold is
                // tuned to Revit's exponential-backoff lock retry pattern observed
                // in v7 journals (~6s before "give up" surfaces "File not saved").
                try
                {
                    var uiApp = _uiApplicationProvider?.UIApplication;
                    if (uiApp != null && doc != null)
                    {
                        var mainWindow = uiApp.MainWindowHandle;
                        var title = doc.Title;
                        var docKey = doc.GetHashCode().ToString();
                        // Detect cloud vs local-server workshared — the slow-sync
                        // message text is different for each. For cloud (BIM 360 /
                        // Autodesk Docs / ACC), a 30 s+ sync is HTTP upload time, not
                        // SMB lock retry — the "Waiting for central file lock"
                        // message is misleading. For local-server worksharing, the
                        // lock-retry message IS accurate.
                        bool isCloudDoc = false;
                        try { isCloudDoc = Helpers.DocumentTypeHelper.IsCloud(doc); }
                        catch { /* fall through to local-message path */ }

                        Revit.BackgroundSync.RevitInteropHelper.SetStatusText(mainWindow,
                            $"ZeManage: Syncing with central — {title}…");

                        // Retry-warning timer (5s). If sync completes before this fires,
                        // DocumentSynchronizedWithCentral disposes the timer.
                        var retryMessage = isCloudDoc
                            ? $"ZeManage: Cloud sync still in progress — large models may take several minutes ({title})"
                            : $"ZeManage: Waiting for central file lock — retrying… ({title})";
                        var retryTimer = new System.Threading.Timer(_ =>
                        {
                            try
                            {
                                var uiAppNow = _uiApplicationProvider?.UIApplication;
                                if (uiAppNow == null) return;
                                Revit.BackgroundSync.RevitInteropHelper.SetStatusText(
                                    uiAppNow.MainWindowHandle, retryMessage);
                            }
                            catch { /* status bar update is best-effort */ }
                        }, null, TimeSpan.FromSeconds(5), System.Threading.Timeout.InfiniteTimeSpan);

                        // Dispose any prior timer for this doc (re-entrancy safety).
                        if (_syncStatusRetryTimers.TryRemove(docKey, out var prior))
                        {
                            try { prior.Dispose(); } catch { }
                        }
                        _syncStatusRetryTimers[docKey] = retryTimer;
                    }
                }
                catch (Exception statusEx)
                {
                    _logger?.LogDebug($"[SyncStatus] Failed to set status-bar text on sync start: {statusEx.Message}");
                }

                // === Cloud Sync Gate (Option D — server-arbitrated) ===
                // For cloud (BIM 360 / Autodesk Docs) models the upstream BeforeExecuted
                // is bypassed by Revit's CloudWorksharing.UI add-in, so the on-prem queue
                // path NEVER runs. Ask the server for a sync slot here, while we still hold
                // the sync event handler. Backward-compat: an old server that doesn't
                // implement RequestSyncSlot simply doesn't reply → we time out → fall
                // through to the existing client-side STC fallback below (today's behavior
                // is preserved). On-prem is unaffected because the isCloudDoc check skips
                // this block entirely and the upstream BeforeExecuted has already gated.
                // See C:\Users\Admin\.claude\plans\buzzing-spinning-storm.md for the full
                // backward-compatibility matrix.
                bool cloudGateIsCloud = false;
                try { cloudGateIsCloud = Helpers.DocumentTypeHelper.IsCloud(doc); }
                catch { /* fall through to local-server path */ }

                var cloudGateConnMgr = _signalRConnectionManagerGetter?.Invoke();
                var cloudGateStc = _syncTrafficControlGetter?.Invoke();
                // Skip the cloud gate during background sync. BackgroundSyncEngine drives
                // SynchronizeWithCentral programmatically (no user click), so showing a
                // SyncReadyDialog or doing an unattended doc.Save() here would interrupt
                // the headless flow and confuse the engine's state machine. Matches the
                // upstream SyncCommandBinding.OnBeforeExecuted guard (line ~221) which
                // also short-circuits on IsBackgroundSyncActive.
                bool cloudGateIsBackground = cloudGateStc?.IsBackgroundSyncActive == true;

                // Feature-toggle kill switch (A4). SyncQueueControl is registered with
                // default=true in FeatureToggleService.InitializeDefaultFeatures, so the
                // gate fires by default. Ops can flip the toggle off per-tenant (via the
                // standard feature flag mechanism) if a cloud-gate regression surfaces.
                bool cloudGateFeatureEnabled = _featureToggleService?.IsFeatureEnabled("SyncQueueControl") == true;

                // Registered-model guard (A3). Skip the gate entirely for workshared cloud
                // models that aren't registered in the BIManage backend — they have no
                // queue state to coordinate, and a server roundtrip would just waste time.
                // The existing client-side STC fallback below handles the rare collision
                // case on unregistered models. Sync check via Task.Run+GetResult matches
                // the established pattern in SyncCommandBinding / PinCommandBinding.
                bool cloudGateIsRegistered = false;
                try
                {
                    TryGetCachedModelGuid(doc, out var probeGuid);
                    if (!string.IsNullOrEmpty(probeGuid))
                    {
                        var modelRepo = _registeredModelsRepoGetter?.Invoke();
                        if (modelRepo != null)
                        {
                            cloudGateIsRegistered = Task.Run(() => modelRepo.IsModelRegisteredAsync(probeGuid))
                                .GetAwaiter().GetResult();
                        }
                    }
                }
                catch (Exception probeEx)
                {
                    _logger?.LogDebug($"[CloudGate] Registered-model probe failed (skipping gate): {probeEx.Message}");
                }

                if (cloudGateIsCloud && cloudGateConnMgr != null && cloudGateStc != null
                    && !cloudGateIsBackground && cloudGateFeatureEnabled && cloudGateIsRegistered
                    && doc != null)
                {
                    try
                    {
                        // Pre-sync local save (constraint per user: "trigger local save
                        // of the model to ensure any model changes will not be lost at
                        // any cost … shouldn't affect the central model at any reason").
                        // doc.Save() writes local-only; it does NOT push to central until
                        // SynchronizeWithCentral runs. Safe to call from inside a sync
                        // event handler because no transaction is open at this point.
                        try
                        {
                            doc.Save();
                            _logger?.LogInfo($"[CloudGate] Local save committed before queue join — {doc.Title}");
                        }
                        catch (Exception saveEx)
                        {
                            _logger?.LogWarning($"[CloudGate] Local save failed (non-fatal — continuing to queue): {saveEx.Message}");
                        }

                        var cgSessionId = _revitContext?.SessionId.ToString() ?? "";
                        TryGetCachedModelGuid(doc, out var cgModelGuid);
                        var cgRevitUsername = doc.Application?.Username ?? Environment.UserName;
                        var cgWindowsUsername = Environment.UserName;
                        var cgUsername = string.IsNullOrWhiteSpace(cgRevitUsername) ? cgWindowsUsername : cgRevitUsername;

                        if (!string.IsNullOrEmpty(cgModelGuid))
                        {
                            // Same-user repeat short-circuit (user-confirmed in plan): if WE
                            // already hold the active slot in STC, this is a quick re-sync
                            // by the same client. No queue UI, no server call — proceed.
                            var existingSlot = cloudGateStc.GetActiveSyncer(cgModelGuid);
                            var alreadyOurs = existingSlot != null
                                && !string.IsNullOrEmpty(existingSlot.SessionId)
                                && !string.IsNullOrEmpty(cgSessionId)
                                && string.Equals(existingSlot.SessionId, cgSessionId, StringComparison.OrdinalIgnoreCase);

                            if (alreadyOurs)
                            {
                                _logger?.LogInfo($"[CloudGate] Same-user repeat — short-circuit (session {cgSessionId} already holds slot for {cgModelGuid}).");
                            }
                            else
                            {
                                // SYNCHRONOUS BLOCK (v100 fix): we must NOT await here.
                                // DocumentSynchronizingWithCentral is async-void; awaiting yields
                                // control back to Revit's dispatcher, Revit immediately reads
                                // e.Cancel (still false) and proceeds with the cloud upload —
                                // by the time our await continuation runs, the sync is already
                                // started or done and e.Cancel is meaningless. The only way to
                                // make e.Cancel reliable is to block the dispatcher briefly
                                // until the server decides. Matches the established pattern at
                                // SyncCommandBinding.cs:230 (Thread.Sleep for ClaimWaitMs).
                                // 500 ms timeout: most server replies arrive <100 ms; 500 ms is
                                // tolerable as a UI freeze (Revit's own auth check often takes
                                // 1-2 s per the v10-cloud journal). If the server takes longer
                                // or is unreachable, GetResult returns Unavailable and we fall
                                // through to today's client-side STC gate.
                                Infrastructure.SignalR.SyncSlotDecision decision;
                                try
                                {
                                    decision = Task.Run(() => cloudGateConnMgr.RequestSyncSlotAsync(
                                        cgModelGuid,
                                        cgSessionId,
                                        cgUsername,
                                        cgRevitUsername,
                                        DateTime.UtcNow,
                                        isCloud: true,
                                        timeout: TimeSpan.FromMilliseconds(500))).GetAwaiter().GetResult();
                                }
                                catch (Exception cgWaitEx)
                                {
                                    _logger?.LogWarning($"[CloudGate] RequestSyncSlot wait failed: {cgWaitEx.Message} — falling back");
                                    decision = Infrastructure.SignalR.SyncSlotDecision.Unavailable("wait-failed");
                                }

                                switch (decision.Outcome)
                                {
                                    case Infrastructure.SignalR.SyncSlotOutcome.Granted:
                                        cloudGateStc.OnLocalSyncStarting(cgModelGuid, doc.Title, cgSessionId, cgUsername);
                                        _logger?.LogInfo($"[CloudGate] Slot granted by server for {cgModelGuid} — proceeding with cloud sync.");
                                        // Skip the fallback block below; we've already recorded the local sync.
                                        // Fall through to event-protection check + bookkeeping.
                                        goto skipStcFallback;

                                    case Infrastructure.SignalR.SyncSlotOutcome.Denied:
                                        _logger?.LogWarning(
                                            $"[CloudGate] Slot DENIED by server for {cgModelGuid} — pos={decision.QueuePosition}, " +
                                            $"blockingUser={decision.BlockingUsername ?? "<unknown>"}, reason={decision.Reason ?? "<none>"}.");
                                        // KNOWN LIMITATION (documented in plan): we are awaiting an
                                        // async call inside an async-void Revit event handler, so
                                        // this e.Cancel() runs AFTER the handler has already
                                        // yielded back to Revit's event pump. Cancel may be a no-op
                                        // on cloud models (the plan explicitly accepts this — even
                                        // synchronous e.Cancel is "unreliable for cloud" per
                                        // commit dfea62a). If cancel doesn't actually abort the
                                        // upload, the server still recorded us in the queue —
                                        // SyncRequest will fire on the next turn so the queue stays
                                        // consistent. Worst case: this sync collides with the
                                        // current syncer at the cloud layer (same as today's
                                        // unguarded behavior).
                                        if (e.Cancellable) e.Cancel();
                                        // Mark this doc so the subsequent DocumentSynchronizedWithCentral
                                        // doesn't broadcast a bogus SyncCompleted.
                                        lock (_fallbackCancelledSyncs)
                                        {
                                            _fallbackCancelledSyncs.Add(doc.GetHashCode().ToString());
                                        }
                                        // Status-bar feedback so the user understands what
                                        // happened — even if the modal dialog below fails to
                                        // appear (e.g. UI thread unavailable), the user still
                                        // sees that they were queued rather than silently
                                        // stuck.
                                        try
                                        {
                                            var uiAppDenied = _uiApplicationProvider?.UIApplication;
                                            if (uiAppDenied != null)
                                            {
                                                var blockerName = string.IsNullOrEmpty(decision.BlockingUsername)
                                                    ? "another user" : decision.BlockingUsername;
                                                Revit.BackgroundSync.RevitInteropHelper.SetStatusText(
                                                    uiAppDenied.MainWindowHandle,
                                                    $"ZeManage: Sync queued behind {blockerName} (position {decision.QueuePosition}) — waiting for turn…");
                                            }
                                        }
                                        catch { /* status bar is best-effort */ }

                                        // A1: surface the same SyncConflictDialog the on-prem
                                        // fallback uses (ShowFallbackSyncConflictDialog) so cloud
                                        // and on-prem deliver equivalent UX. Dialog auto-closes
                                        // when the blocker's SyncCompleted arrives via the
                                        // event bus; user can also click "Cancel" to leave the
                                        // queue. ExpectedWaitSecs is used as a rough "started
                                        // at" hint (server reports 0 today, so dialog will show
                                        // "syncing for 0s" — acceptable until server gains
                                        // duration tracking).
                                        var blockedSince = DateTime.UtcNow
                                            .AddSeconds(-Math.Max(decision.ExpectedWaitSecs, 0));
                                        ShowFallbackSyncConflictDialog(
                                            cloudGateStc,
                                            decision.BlockingUsername ?? "another user",
                                            blockedSince,
                                            doc.Title,
                                            cgModelGuid,
                                            cgSessionId,
                                            cgUsername);
                                        // SyncReadyDialog surfaces later via the existing
                                        // SyncRequest SignalR message when our turn comes.
                                        return;

                                    case Infrastructure.SignalR.SyncSlotOutcome.Unavailable:
                                    default:
                                        _logger?.LogInfo($"[CloudGate] Server unreachable / no slot reply (reason='{decision.Reason ?? "unknown"}') — falling back to client-side STC gate.");
                                        // Fall through to the existing fallback block below.
                                        break;
                                }
                            }
                        }
                    }
                    catch (Exception cloudGateEx)
                    {
                        _logger?.LogWarning($"[CloudGate] Unexpected error — falling back to client-side STC gate: {cloudGateEx.Message}");
                        // Fall through to existing fallback below.
                    }
                }

                // === Sync Traffic Control: compatibility fallback ===
                // Primary path is SyncCommandBinding.OnBeforeExecuted (upstream). This block
                // only engages when the upstream didn't fire — missing command id, peer on an
                // older add-in version, PostCommand path, etc. Cancel is unreliable for cloud
                // models but reliable for workshared local-server models.
                var stcFallback = _syncTrafficControlGetter?.Invoke();
                if (stcFallback != null && doc != null)
                {
                    try
                    {
                        var stcSessionId = _revitContext?.SessionId.ToString() ?? "";
                        // MUST read from cache here. Calling GetModelGuid live from inside this
                        // event handler re-enters Revit's ADocument::getModelGUID_ while the
                        // DB is mid-STC transition and crashes the process on Revit 2026.
                        TryGetCachedModelGuid(doc, out var stcModelGuid);
                        var stcUsername  = doc.Application?.Username ?? Environment.UserName;

                        if (!string.IsNullOrEmpty(stcModelGuid))
                        {
                            var activeSyncer = stcFallback.GetActiveSyncer(stcModelGuid);
                            var currentSyncer = activeSyncer?.Username;
                            // CRITICAL: identify "is this me?" by SessionId (unique per Revit
                            // instance), NOT by Username. Two engineers can share the same
                            // Revit username (e.g. both "shalini" or both "admin" — observed
                            // 2026-05-12 R24 logs). A username-only check incorrectly treats
                            // the remote sync as "already ours" and skips the block, so Revit
                            // proceeds, hits the central file lock, and shows its native
                            // "Unable to Access the Model" dialog — exactly the failure mode
                            // we are trying to eliminate. SessionId is a Guid per Revit
                            // process, so it never collides.
                            var alreadyOurs = activeSyncer != null
                                && !string.IsNullOrEmpty(activeSyncer.SessionId)
                                && !string.IsNullOrEmpty(stcSessionId)
                                && string.Equals(activeSyncer.SessionId, stcSessionId, StringComparison.OrdinalIgnoreCase);

                            if (!alreadyOurs && activeSyncer != null)
                            {
                                // Fix C (Phase 7 — v5 evidence): before blocking, check
                                // whether the user just clicked Sync Now from a queue
                                // promotion popup. In that case the active syncer's
                                // SyncCompleted may not yet have fanned out to us, so
                                // GetActiveSyncer can still report the OLD peer here —
                                // blocking would put us BACK into queue immediately
                                // after we were promoted out, and the server then
                                // promotes us a second time when the real Completed
                                // arrives, surfacing a second SyncReadyDialog (the
                                // user-reported "Join Queue popup appears again after
                                // completion" bug — see Phase 7 v5 log analysis).
                                if (stcFallback.TryConsumePendingQueueSync(stcModelGuid))
                                {
                                    _logger?.LogInfo(
                                        $"[SyncTrafficControl/fallback] Bypassing block — user just accepted queue promotion for {stcModelGuid}. " +
                                        $"Stale active-syncer {currentSyncer} (session {activeSyncer.SessionId}) treated as already-completing.");
                                    // Fall through to OnLocalSyncStarting below so STC
                                    // sees us as the active syncer. Don't cancel, don't
                                    // show conflict dialog — the user is correctly mid-promotion.
                                }
                                else
                                {
                                    _logger?.LogWarning(
                                        $"[SyncTrafficControl/fallback] Sync BLOCKED mid-event — {currentSyncer} (session {activeSyncer.SessionId}) is syncing {stcModelGuid}. " +
                                        $"Cancelling (upstream SyncCommandBinding did not intercept).");
                                    if (e.Cancellable) e.Cancel();
                                    // Mark this doc so the subsequent DocumentSynchronizedWithCentral
                                    // (which Revit fires even after cancel) doesn't broadcast SyncCompleted.
                                    lock (_fallbackCancelledSyncs)
                                    {
                                        _fallbackCancelledSyncs.Add(doc.GetHashCode().ToString());
                                    }

                                    // Show conflict dialog so the user actually sees the interruption,
                                    // matching the upstream SyncCommandBinding behaviour.
                                    ShowFallbackSyncConflictDialog(stcFallback,
                                        activeSyncer.RevitUsername ?? activeSyncer.Username,
                                        activeSyncer.StartedAtUtc,
                                        doc.Title, stcModelGuid, stcSessionId, stcUsername);
                                    return;
                                }
                            }

                            if (!alreadyOurs)
                            {
                                stcFallback.OnLocalSyncStarting(stcModelGuid, doc.Title, stcSessionId, stcUsername);
                                _logger?.LogInfo($"[SyncTrafficControl/fallback] Recorded local sync start for {stcModelGuid} (upstream did not).");
                            }
                        }
                    }
                    catch (Exception stcEx)
                    {
                        _logger?.LogWarning($"[SyncTrafficControl/fallback] Error in pre-sync check: {stcEx.Message}");
                    }
                }

                // Cloud sync gate (Option D) skips here when the server granted a slot —
                // STC has already been notified via OnLocalSyncStarting so the fallback
                // above would double-record. Continue with event-protection check below.
                skipStcFallback:

                // === Event Protection Check: Sync Conflict Detection ===
                if (_featureToggleService.IsFeatureEnabled("EventProtection") && doc != null)
                {
                    if (!CheckSyncConflictDetection(doc))
                    {
                        if (e.Cancellable) e.Cancel();
                        return;
                    }
                }

                // === Sync Traffic Control ===
                // Traffic gating (block/queue/dialog) is now handled UPSTREAM in SyncCommandBinding.
                // BeforeExecuted on the command binding fires before Revit starts the sync,
                // making e.Cancel reliable (unlike this event where cancel is unreliable for cloud models).
                // This event handler is now bookkeeping-only — no dialogs, no cancel for traffic control.

                // Track sync start if repository available
                if (_syncRepository != null && doc != null)
                {
                    // CRITICAL: arm the DocumentChanged guard SYNCHRONOUSLY before any await.
                    // The guard at OnDocumentChanged checks _activeSyncOperations.ContainsKey(docKey);
                    // without the placeholder, the ~40-50ms gap before the real syncGuid lands
                    // (two awaits below) lets the first wave of merge-phase DocumentChanged events
                    // run the full protection chain on co-user deltas — which is exactly what
                    // the guard exists to prevent. Replace with the real syncGuid after the awaits.
                    var preDocKey = doc.GetHashCode().ToString();
                    _activeSyncOperations[preDocKey] = string.Empty;

                    try
                    {
                        var sessionId = _revitContext?.SessionId.ToString() ?? Guid.NewGuid().ToString();
                        // Cached — see comment in stcFallback block above and class field docs.
                        TryGetCachedModelGuid(doc, out var modelGuid);
                        var modelPath = Helpers.DocumentInformationHelper.GetNormalizedPath(doc);
                        var modelName = doc.Title;

                        // Get Revit username from session (not Windows username)
                        var syncedBy = Environment.UserName;  // Fallback to Windows username
                        if (_sessionRepository != null && _revitContext != null)
                        {
                            try
                            {
                                var session = await _sessionRepository.GetSessionAsync(sessionId);
                                if (session != null && !string.IsNullOrEmpty(session.RevitUsername))
                                {
                                    syncedBy = session.RevitUsername;
                                }
                                else
                                {
                                    _logger?.LogWarning($"Revit username not available for session {sessionId}, using Windows username");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning($"Failed to get Revit username from session: {ex.Message}");
                            }
                        }

                        // Note: IsLocalSaved and IsRelinquished detection would require
                        // additional Revit API exploration or transaction monitoring
                        var isLocalSaved = false;  // TODO: Detect if local save occurred
                        var isRelinquished = false; // TODO: Detect if elements were relinquished

                        var syncGuid = await _syncRepository.StartSyncAsync(
                            sessionId: sessionId,
                            modelGuid: modelGuid,
                            modelPath: modelPath,
                            modelName: modelName,
                            syncedBy: syncedBy,
                            isLocalSaved: isLocalSaved,
                            isRelinquished: isRelinquished);

                        // Store sync GUID to correlate with completion event
                        var docKey = doc.GetHashCode().ToString();
                        _activeSyncOperations[docKey] = syncGuid;

                        _logger?.LogDebug($"Sync tracking started: {syncGuid}");

                        // Fast-metrics snapshot is deliberately collected AFTER the sync finishes
                        // (in OnDocumentSynchronizedWithCentral). Running FilteredWorksetCollector
                        // / FilteredElementCollector on the doc while Revit is preparing STC is
                        // unsafe on Revit 2026 — it can re-enter the same native paths that
                        // crash in ADocument::getModelGUID_.
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"Failed to track sync start: {ex.Message}", ex);
                        // Don't throw - sync tracking failure shouldn't block the sync operation
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnDocumentSynchronizingWithCentral handler: {ex.Message}", ex);
            }
        }

        private async void OnDocumentSynchronizedWithCentral(object sender, Autodesk.Revit.DB.Events.DocumentSynchronizedWithCentralEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;

                var doc = e.Document;

                // Sync events should only fire for workshared documents
                if (!Helpers.DocumentTypeHelper.IsWorkshared(doc))
                {
                    _logger?.LogWarning($"Sync event fired for non-workshared document: {doc?.Title}");
                    return;
                }

                // If the traffic-control fallback cancelled this sync, Revit still fires this
                // completion event on local-server models. Suppress all bookkeeping + broadcast
                // so peers don't see a bogus SyncCompleted.
                if (doc != null)
                {
                    var cancelledKey = doc.GetHashCode().ToString();
                    bool wasCancelled;
                    lock (_fallbackCancelledSyncs)
                    {
                        wasCancelled = _fallbackCancelledSyncs.Remove(cancelledKey);
                    }
                    if (wasCancelled)
                    {
                        _logger?.LogInfo($"[SyncTrafficControl/fallback] Suppressing completion bookkeeping for {doc.Title} — sync was cancelled pre-start.");
                        return;
                    }
                }

                var docType = Helpers.DocumentTypeHelper.GetDocumentTypeName(doc);
                _logger?.LogInfo($"Document synchronized with central ({docType}): {doc?.Title}");

                // Dispose the retry-warning timer (sync completed within the window)
                // and surface a brief "Sync complete" status until the next operation.
                try
                {
                    if (doc != null)
                    {
                        var docKey = doc.GetHashCode().ToString();
                        if (_syncStatusRetryTimers.TryRemove(docKey, out var retryTimer))
                        {
                            try { retryTimer.Dispose(); } catch { }
                        }
                        var uiApp = _uiApplicationProvider?.UIApplication;
                        if (uiApp != null)
                        {
                            Revit.BackgroundSync.RevitInteropHelper.SetStatusText(
                                uiApp.MainWindowHandle,
                                $"ZeManage: Sync complete — {doc.Title}");
                        }
                    }
                }
                catch (Exception statusEx)
                {
                    _logger?.LogDebug($"[SyncStatus] Failed to clear status-bar text on sync end: {statusEx.Message}");
                }

                // Complete sync tracking if repository available
                if (_syncRepository != null && doc != null)
                {
                    try
                    {
                        var docKey = doc.GetHashCode().ToString();

                        // A pre-await placeholder entry has empty syncGuid — treat as "no real
                        // tracking record" and fall through to the fallback-create branch below.
                        // Also clear the placeholder so the DocumentChanged guard for this doc
                        // doesn't keep blocking after sync completion.
                        if (_activeSyncOperations.TryGetValue(docKey, out var syncGuid)
                            && !string.IsNullOrEmpty(syncGuid))
                        {
                            // Assume success if this event fired (Revit API doesn't provide explicit success flag)
                            // Sync failures typically throw exceptions before this event
                            var isSucceeded = true;

                            await _syncRepository.CompleteSyncAsync(
                                syncGuid: syncGuid,
                                isSucceeded: isSucceeded,
                                errorMessage: null);

                            // Clean up tracking dictionary
                            _activeSyncOperations.Remove(docKey);

                            // Fire-and-forget: POST sync record to API
                            var totalWorksets = CountTotalWorksets(doc);
                            SyncModelSyncToApiAsync(syncGuid, totalWorksets);

                            _logger?.LogDebug($"Sync tracking completed: {syncGuid}");

                            // Post-sync fast-metrics snapshot (moved from Synchronizing handler,
                            // which was crashing Revit 2026 due to FilteredElementCollector +
                            // getModelGUID_ re-entry during STC prep).
                            await CaptureSyncMetricsAsync(doc, syncGuid);
                        }
                        else
                        {
                            // Clean up the empty-placeholder if one was left by a failed StartSyncAsync —
                            // otherwise the DocumentChanged guard would silently early-return for this
                            // doc until the process exits.
                            _activeSyncOperations.Remove(docKey);

                            // No matching start record — this happens when background sync was blocked/cancelled
                            // but Revit still fires SynchronizedWithCentral (e.g. manual sync ran alongside it).
                            // Create a start + complete record so the sync appears in history.
                            _logger?.LogWarning($"Sync completed but no tracking record found for document: {doc.Title} — creating fallback record");
                            try
                            {
                                var sessionId = _revitContext?.SessionId.ToString() ?? Guid.NewGuid().ToString();
                                // Use cache — safe even post-sync, avoids another native round-trip.
                                if (!TryGetCachedModelGuid(doc, out var modelGuid))
                                {
                                    modelGuid = Helpers.ModelGuidHelper.GetModelGuid(doc, _logger);
                                }
                                var modelPath = Helpers.DocumentInformationHelper.GetNormalizedPath(doc);
                                var syncedBy = doc.Application?.Username ?? Environment.UserName;

                                var fallbackGuid = await _syncRepository.StartSyncAsync(
                                    sessionId: sessionId,
                                    modelGuid: modelGuid,
                                    modelPath: modelPath,
                                    modelName: doc.Title,
                                    syncedBy: syncedBy,
                                    isLocalSaved: false,
                                    isRelinquished: false);

                                await _syncRepository.CompleteSyncAsync(
                                    syncGuid: fallbackGuid,
                                    isSucceeded: true,
                                    errorMessage: null);

                                var totalWorksets = CountTotalWorksets(doc);
                                SyncModelSyncToApiAsync(fallbackGuid, totalWorksets);

                                _logger?.LogDebug($"Fallback sync tracking created: {fallbackGuid}");

                                await CaptureSyncMetricsAsync(doc, fallbackGuid);
                            }
                            catch (Exception fallbackEx)
                            {
                                _logger?.LogError($"Failed to create fallback sync record for {doc.Title}: {fallbackEx.Message}", fallbackEx);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"Failed to complete sync tracking: {ex.Message}", ex);
                        // Don't throw - tracking failure shouldn't impact the completed sync
                    }
                }

                // === Sync Traffic Control: notify completion ===
                var syncTrafficControl = _syncTrafficControlGetter?.Invoke();
                if (syncTrafficControl != null && doc != null)
                {
                    try
                    {
                        var stcSessionId = _revitContext?.SessionId.ToString() ?? "";
                        // Cached read — avoid native getModelGUID_ round-trip right after STC finishes.
                        if (!TryGetCachedModelGuid(doc, out var stcModelGuid))
                        {
                            stcModelGuid = Helpers.ModelGuidHelper.GetModelGuid(doc, _logger);
                        }
                        var stcUsername = doc.Application?.Username ?? Environment.UserName;
                        if (!string.IsNullOrEmpty(stcModelGuid))
                        {
                            syncTrafficControl.OnLocalSyncCompleted(stcModelGuid, stcSessionId, stcUsername, true);

                            // Notify BackgroundSyncEngine of sync completion for interval tracking
                            var bgSyncEngine = _backgroundSyncEngineGetter?.Invoke();
                            bgSyncEngine?.RecordSyncCompleted(stcModelGuid);
                        }
                    }
                    catch (Exception stcEx)
                    {
                        _logger?.LogWarning($"[SyncTrafficControl] Error in post-sync notify: {stcEx.Message}");
                    }
                }

                // Update document session's last_saved_at on sync for workshared models
                // This is the authoritative "last saved" timestamp for workshared models
                if (_sessionRepository != null && doc != null && _revitContext != null)
                {
                    try
                    {
                        var sessionId = _revitContext.SessionId.ToString();
                        var documentId = doc.GetHashCode().ToString();
                        var revitUsername = doc.Application?.Username ?? Environment.UserName;

                        await _sessionRepository.RecordDocumentSyncAsync(sessionId, documentId, revitUsername);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"Failed to record document sync in session: {ex.Message}", ex);
                        // Don't throw - session tracking failure shouldn't impact the completed sync
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnDocumentSynchronizedWithCentral handler: {ex.Message}", ex);
            }
        }

        private void OnApplicationInitialized(object sender, Autodesk.Revit.DB.Events.ApplicationInitializedEventArgs e)
        {
            try
            {
                _logger?.LogInfo("Application initialized");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnApplicationInitialized handler: {ex.Message}", ex);
            }
        }

        private void OnDocumentChanged(object sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;

                // Record user activity for productivity tracking (DocumentChanged fires on every user action)
                _productivityTracker.RecordActivity();

                // Skip every downstream check while STC is merging central's delta for this doc.
                // During STC:Delayed-Propagation, Revit fires DocumentChanged with freshly-merged
                // element IDs whose referenced resources (linked RVTs, CAD imports, families)
                // are NOT yet resolved. Calling doc.GetElement(id) here — which every downstream
                // check below does — forces Revit to materialize the element synchronously on
                // the sync call stack. If that triggers a linked-file load and the target file
                // is corrupt or unreachable (e.g. an IFC-converted RVT, or a co-user's local
                // path embedded in the central), Revit dies inside STC with no rollback point.
                // Other Revit clients on the same central don't see this because only BIManage
                // calls GetElement at that moment.
                // Correctness: every handler below (Toggle Pin, CAD import prompt, RVT link pin
                // prompt, Copy/Monitor, protected-position bypass, ChangeSet extraction) is a
                // USER-intent feature — none of them should fire on co-user deltas arriving via
                // sync anyway.
                var syncingDoc = e.GetDocument();
                if (syncingDoc != null
                    && _activeSyncOperations.ContainsKey(syncingDoc.GetHashCode().ToString()))
                {
                    return;
                }

                // Check for "Toggle Pin" transaction (floating pushpin icon)
                var transactionNames = e.GetTransactionNames();
                if (transactionNames != null &&
                    transactionNames.Count == 1 &&
                    transactionNames.All(x => string.Equals(x, "Toggle Pin", StringComparison.Ordinal)) &&
                    e.Operation == UndoOperation.TransactionCommitted)
                {
                    _logger?.LogDebug("Detected 'Toggle Pin' transaction from floating pushpin icon");

                    try
                    {
                        var document = e.GetDocument();
                        if (document != null && document.IsValidObject)
                        {
                            // Capture modified elements
                            var modifiedElementIds = e.GetModifiedElementIds();
                            var modifiedElements = modifiedElementIds
                                .Select(id => document.GetElement(id))
                                .Where(el => el != null && el.IsValidObject);

                            // Pass data to external event
                            TogglePinExternalEventInfo.Instance.AllModifiedElements = modifiedElements;
                            TogglePinExternalEventInfo.Instance.Document = document;

                            // Raise external event if not already pending
                            if (_togglePinExternalEvent != null && !_togglePinExternalEvent.IsPending)
                            {
                                _togglePinExternalEvent.Raise();
                                _logger?.LogDebug("Raised TogglePinExternalEvent");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"Error handling Toggle Pin transaction: {ex.Message}", ex);
                    }

                    // Early return - don't process as regular DocumentChanged event
                    return;
                }

                // Protection 13: CAD Import Pin Prompt (post-action)
                if (_featureToggleService.IsFeatureEnabled("EventProtection") &&
                    transactionNames != null &&
                    transactionNames.Count == 1 &&
                    string.Equals(transactionNames[0], "Import Vector Data", StringComparison.Ordinal) &&
                    e.Operation == UndoOperation.TransactionCommitted)
                {
                    CheckCADImportPinPrompt(e);
                }

                // CAD Explode (post-action): catches ribbon Explode buttons that bypass BeforeExecuted.
                // Context menu IDs (ID_IMPORT_INSTANCE_EXPLODE / ID_IMPORT_INST_PARTIAL_EXPLODE) are
                // intercepted by CommandInterceptionService Priority 3; this handler covers ribbon paths.
                // Note: post-action — Notify/Guide modes log/notify but cannot truly block (already exploded).
                if (_featureToggleService.IsFeatureEnabled("EventProtection") &&
                    transactionNames != null &&
                    transactionNames.Count > 0 &&
                    e.Operation == UndoOperation.TransactionCommitted &&
                    transactionNames.Any(n =>
                        string.Equals(n, "Full Explode", StringComparison.Ordinal) ||
                        string.Equals(n, "Partial Explode", StringComparison.Ordinal)))
                {
                    CheckCADExplodePostAction(e);
                }

                // Protection 14: RVT Link Pin Prompt (post-insert)
                if (_featureToggleService.IsFeatureEnabled("EventProtection") &&
                    e.Operation == UndoOperation.TransactionCommitted)
                {
                    CheckRVTLinkPinPrompt(e);
                }

                // Protection 11: Copy/Monitor Pin Protection (post-action, 2-stage)
                if (_featureToggleService.IsFeatureEnabled("EventProtection") &&
                    transactionNames != null && transactionNames.Count > 0 &&
                    e.Operation == UndoOperation.TransactionCommitted)
                {
                    var txName = transactionNames[0];
                    if (string.Equals(txName, "Copy", StringComparison.Ordinal) ||
                        string.Equals(txName, "Copy (Copy Monitor link)", StringComparison.Ordinal))
                    {
                        TrackCopyMonitorElements(e);
                    }
                    else if (string.Equals(txName, "Finish Mode", StringComparison.Ordinal))
                    {
                        CheckCopyMonitorPinProtection(e);
                    }
                }

                // Clear Copy/Monitor tracking on undo
                if (e.Operation == UndoOperation.TransactionUndone || e.Operation == UndoOperation.TransactionGroupRolledBack)
                {
                    ClearCopyMonitorTracking(e.GetDocument());
                }

                // Extract ChangeSet (performance-optimized: only if changes exist)
                var addedIds = e.GetAddedElementIds();
                var modifiedIds = e.GetModifiedElementIds();
                var deletedIds = e.GetDeletedElementIds();

                var totalChanges = addedIds.Count + modifiedIds.Count + deletedIds.Count;
                if (totalChanges == 0) return;

                // Check for pin protection bypass (protected elements that moved)
                if (_featureToggleService.IsFeatureEnabled("PinProtectionBypassDetection"))
                {
                    CheckProtectedElementPositions(e, modifiedIds);
                }

                // Closes the bypass loophole for Move/Copy/Cut on non-pin-protected elements with
                // an active Command Protection or Rule. Drag-to-move, arrow-key nudge, Ctrl+drag
                // duplicate, Ctrl+V paste (when not bound), and property-palette location edits
                // don't fire a Revit command id and would otherwise skip the priority chain.
                // The detector mirrors CommandInterceptionService.OnBeforeCommandExecuted's
                // Priority 1+2 chain, then queues a revert via PositionRestorationService when
                // the user blocks.
                _bypassDetector?.Process(e);

                // Guard against deletion of BIManage pin-protected elements via any path
                // (API, scripts, gestures) that bypasses the DeleteCommandBinding.BeforeExecuted.
                var uiAppForGuard = _uiApplicationProvider?.UIApplication;
                if (uiAppForGuard != null)
                    _deletionGuard?.Process(e, uiAppForGuard);

                // Check if any unpinned elements were modified - capture after screenshot
                if (_unpinnedElementTracker != null && _unpinnedElementTracker.HasTrackedElements)
                {
                    var modifiedLongIds = modifiedIds.Select(id => id.GetIdValue());
                    var deletedLongIds = deletedIds.Select(id => id.GetIdValue());
                    _unpinnedElementTracker.CheckForModifications(modifiedLongIds, deletedLongIds);
                }

                var changeSet = new ChangeSet(e.GetDocument(), addedIds, modifiedIds, deletedIds);
                _logger?.LogDebug($"DocumentChanged: {changeSet}");

                // Add to document session history
                var session = _sessionManager.GetSession(e.GetDocument());
                session?.AddChangeSet(changeSet);

                // TODO: Queue ChangeSet for rule evaluation (Phase 2)
                // TODO: Persist ChangeSet to SQLite (Phase 3)
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnDocumentChanged handler: {ex.Message}", ex);
            }
        }

        #endregion

        #region Workset Tracking

        /// <summary>
        /// Count the number of opened/loaded worksets in a workshared document
        /// </summary>
        private int? CountOpenedWorksets(Document doc)
        {
            try
            {
                if (!doc.IsWorkshared)
                    return null;

                var worksetTable = doc.GetWorksetTable();
                if (worksetTable == null)
                    return null;

                int openedCount = 0;
                var worksetCollector = new FilteredWorksetCollector(doc);
                foreach (Workset workset in worksetCollector)
                {
                    if (workset.Kind == WorksetKind.UserWorkset && workset.IsOpen)
                        openedCount++;
                }

                _logger?.LogDebug($"Document has {openedCount} opened worksets");
                return openedCount;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to count opened worksets: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Document Opening Time Tracking (Interactive Ready Time)

        /// <summary>
        /// Subscribe to ViewActivated event for a specific document to track interactive ready time
        /// </summary>
        private void SubscribeToViewActivatedForDocument(Document doc, string pathKey)
        {
            try
            {
                // Get UIApplication from provider
                var uiApp = _uiApplicationProvider?.UIApplication;
                if (uiApp == null || _uiApplicationProvider?.IsAvailable != true)
                {
                    _logger?.LogWarning("UIApplication not available for ViewActivated subscription");
                    return;
                }

                // Subscribe to ViewActivated event
                uiApp.ViewActivated += OnViewActivatedForTiming;

                _logger?.LogDebug($"Subscribed to ViewActivated for document: {pathKey}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to subscribe to ViewActivated: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Handle ViewActivated event to capture interactive ready time (first activation only)
        /// </summary>
        private async void OnViewActivatedForTiming(object sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;

                var doc = e.Document;
                if (doc == null || !doc.IsValidObject) return;

                var pathKey = doc.PathName ?? doc.GetHashCode().ToString();

                // Check if we have a tracker for this document
                if (!_documentOpeningTrackers.TryGetValue(pathKey, out var tracker))
                    return;

                // Only handle the FIRST ViewActivated event
                if (tracker.ViewActivationHandled)
                    return;

                tracker.ViewActivatedAt = DateTime.UtcNow;
                tracker.ViewActivationHandled = true;

                // Calculate interactive ready time
                var interactiveReadySeconds = (tracker.ViewActivatedAt.Value - tracker.OpeningStartedAt).TotalSeconds;

                _logger?.LogInfo($"Document interactive ready: {doc.Title} ({interactiveReadySeconds:F2}s)");

                // Update database with interactive ready time
                if (_sessionRepository != null)
                {
                    var documentId = doc.GetHashCode().ToString();
                    var revitUsername = doc.Application?.Username ?? Environment.UserName;
                    await _sessionRepository.UpdateDocumentInteractiveReadyTimeAsync(
                        documentId,
                        interactiveReadySeconds,
                        revitUsername);
                }

                // CRITICAL: Unsubscribe immediately to prevent memory leak
                UnsubscribeFromViewActivatedForDocument(pathKey);

                // Clean up tracker
                _documentOpeningTrackers.Remove(pathKey);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnViewActivatedForTiming handler: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Unsubscribe from ViewActivated event for a document
        /// </summary>
        private void UnsubscribeFromViewActivatedForDocument(string pathKey)
        {
            try
            {
                var uiApp = _uiApplicationProvider?.UIApplication;
                if (uiApp != null)
                {
                    uiApp.ViewActivated -= OnViewActivatedForTiming;
                    _logger?.LogDebug($"Unsubscribed from ViewActivated for document: {pathKey}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to unsubscribe from ViewActivated: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Clean up stale document opening trackers (called periodically)
        /// </summary>
        private void CleanupStaleOpeningTrackers()
        {
            try
            {
                var staleThreshold = DateTime.UtcNow.AddMinutes(-5);
                var staleTrackers = _documentOpeningTrackers
                    .Where(kvp => kvp.Value.OpeningStartedAt < staleThreshold)
                    .ToList();

                foreach (var kvp in staleTrackers)
                {
                    _documentOpeningTrackers.Remove(kvp.Key);
                    UnsubscribeFromViewActivatedForDocument(kvp.Key);
                    _logger?.LogWarning($"Cleaned up stale document opening tracker: {kvp.Key}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error cleaning up stale trackers: {ex.Message}", ex);
            }
        }

        #endregion

        #region Pin Protection Bypass Detection

        /// <summary>
        /// Check if any protected pinned elements have been moved via bypass methods
        /// </summary>
        private void CheckProtectedElementPositions(DocumentChangedEventArgs e, ICollection<ElementId> modifiedIds)
        {
            if (modifiedIds == null || modifiedIds.Count == 0)
                return;

            try
            {
                var doc = e.GetDocument();
                if (doc == null || !doc.IsValidObject)
                    return;

                var transactionNames = e.GetTransactionNames();
                var txNames = transactionNames != null ? string.Join(", ", transactionNames) : "(none)";
                _logger?.LogDebug($"[BypassDetection] CheckProtectedElementPositions called. ModifiedIds: {modifiedIds.Count}, Transactions: {txNames}");

                // Expand groups: when a Group is moved, its members don't appear in modifiedIds.
                // Check group members so protected elements inside moved groups are detected.
                var idsToCheck = new HashSet<ElementId>(modifiedIds);
                var groupExpandedIds = new HashSet<ElementId>();
                foreach (var id in modifiedIds)
                {
                    var el = doc.GetElement(id);
                    if (el is Group group)
                    {
                        try
                        {
                            var memberIds = group.GetMemberIds();
                            _logger?.LogDebug($"[BypassDetection] Group {id.GetIdValue()} found with {memberIds.Count} members — expanding");
                            foreach (var memberId in memberIds)
                            {
                                idsToCheck.Add(memberId);
                                groupExpandedIds.Add(memberId);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug($"[BypassDetection] Could not expand group {id.GetIdValue()} members: {ex.Message}");
                        }
                    }
                }

                _logger?.LogDebug($"[BypassDetection] Total IDs to check (after group expansion): {idsToCheck.Count}, group-expanded: {groupExpandedIds.Count}");

                foreach (var id in idsToCheck)
                {
                    var element = doc.GetElement(id);
                    if (element == null || !element.IsValidObject)
                        continue;

                    // Check if element is protected
                    if (!PinProtectionStorage.IsProtected(element))
                        continue;

                    _logger?.LogDebug($"[BypassDetection] Protected element found: {element.Id.GetIdValue()} ({element.Category?.Name} - {element.Name})");

                    // For group-expanded members: skip HasMoved check during DocumentChanged.
                    // When a group is moved via Move command (vs drag), member positions may not
                    // be updated yet in the DocumentChanged handler. Defer position verification
                    // to the PositionRestorationService which runs in Idling context where
                    // positions are guaranteed to be up-to-date.
                    bool wasExpandedFromGroup = groupExpandedIds.Contains(id);
                    if (wasExpandedFromGroup)
                    {
                        if (PinProtectionStorage.GetStoredPosition(element) == null)
                        {
                            _logger?.LogDebug($"[BypassDetection] Group member {element.Id.GetIdValue()} has no stored position — skipping");
                            continue;
                        }
                        _logger?.LogDebug($"[BypassDetection] Group member {element.Id.GetIdValue()} — deferred position check (group was modified)");
                    }
                    else
                    {
                        // For directly modified elements, check position immediately
                        if (!PinProtectionStorage.HasMoved(element))
                        {
                            _logger?.LogDebug($"[BypassDetection] Element {element.Id.GetIdValue()} has NOT moved — skipping");
                            continue;
                        }
                    }

                    _logger?.LogWarning($"[BypassDetection] BYPASS DETECTED: Element {element.Id.GetIdValue()} has moved from stored position!");

                    // Bypass detected! Determine the method
                    var bypassMethod = DetectBypassMethod(e, element, transactionNames);

                    // Skip logging for GroupMemberMove — too noisy, not a real bypass
                    if (bypassMethod == "GroupMemberMove")
                    {
                        _logger?.LogDebug($"[BypassDetection] Skipping GroupMemberMove for element {element.Id.GetIdValue()}");
                    }
                    else
                    {
                        LogPinProtectionViolation(element, bypassMethod, doc);
                    }

                    // Get protection info to check mode
                    var protectionInfo = PinProtectionStorage.GetProtection(element);
                    if (protectionInfo == null)
                        continue;

                    _logger?.LogDebug($"[BypassDetection] Protection mode: {protectionInfo.ProtectionModeType}, HasRestorationService: {_positionRestorationService != null}");

                    // Queue position restoration if in Protect mode
                    if (protectionInfo.ProtectionModeType == Revit.PinProtection.Models.ProtectionMode.Protect)
                    {
                        _positionRestorationService?.QueueRestoration(element.Id, doc, bypassMethod);
                        _logger?.LogInfo($"[BypassDetection] Queued position restoration for element {element.Id.GetIdValue()} (Protect mode, bypass: {bypassMethod})");
                    }
                    else
                    {
                        _logger?.LogInfo($"[BypassDetection] Element {element.Id.GetIdValue()} moved but mode is {protectionInfo.ProtectionModeType} — no restoration");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error checking protected element positions: {ex.Message}", ex);
                // Fail open - don't block user operations
            }
        }

        /// <summary>
        /// Detect the bypass method used to move a protected element
        /// </summary>
        private string DetectBypassMethod(DocumentChangedEventArgs e, Element element, IList<string> transactionNames)
        {
            try
            {
                // Check if transaction name indicates grouping
                if (transactionNames != null)
                {
                    foreach (var txName in transactionNames)
                    {
                        if (string.IsNullOrEmpty(txName))
                            continue;

                        var txLower = txName.ToLowerInvariant();
                        if (txLower.Contains("group") || txLower.Contains("ungroup"))
                            return "GroupWorkaround";

                        if (txLower.Contains("move group"))
                            return "GroupMoveWorkaround";
                    }
                }

                // Check if element is hosted (nested in parent)
                if (element is FamilyInstance fi && fi.Host != null)
                {
                    // Element is nested - check if parent is also protected
                    var host = fi.Host;
                    if (PinProtectionStorage.IsProtected(host))
                        return "NestedComponentMove";
                }

                // Check if element is part of a group
                var groupId = element.GroupId;
                if (groupId != null && groupId != ElementId.InvalidElementId)
                {
                    return "GroupMemberMove";
                }

                return "Unknown";
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Error detecting bypass method: {ex.Message}");
                return "Unknown";
            }
        }

        /// <summary>
        /// Log a pin protection violation to audit log
        /// </summary>
        private void LogPinProtectionViolation(Element element, string bypassMethod, Document doc)
        {
            try
            {
                var username = doc.Application?.Username ?? Environment.UserName;
                var categoryName = element.Category?.Name ?? "Unknown";
                var elementName = element.Name ?? "Unnamed";
                var elementId = element.Id.GetIdValue();

                var message = $"Pin protection bypass detected: {bypassMethod} on {categoryName} - {elementName} (ID: {elementId}) by {username}";
                _logger?.LogWarning(message);

                // Log to audit repository if available
                if (_auditRepository != null)
                {
                    try
                    {
                        var sessionId = _revitContext?.SessionId.ToString() ?? Guid.NewGuid().ToString();

                        // Get model GUID from the element's document
                        string modelGuid = null;
                        try { modelGuid = Helpers.ModelGuidHelper.GetModelGuid(element.Document, _logger); }
                        catch { /* Non-critical */ }

                        // Create audit entry asynchronously (fire and forget)
                        _ = _auditRepository.SaveAuditEntryAsync(new Core.Protection.Models.ProtectionAuditEntry
                        {
                            AuditLogId = Guid.NewGuid().ToString(),
                            // UTC so the server-side mail formatter renders the correct
                            // local time on receipt — DateTime.Now serialised without offset
                            // was the "mail timestamp off by 1 hour" cause.
                            Timestamp = DateTime.UtcNow,
                            UserName = username,
                            ModelGuid = modelGuid,
                            CommandName = $"Pin Protection Bypass: {bypassMethod}",
                            Mode = Core.Rules.Models.ProtectionMode.Notify,
                            Action = Core.Protection.Models.ProtectionAction.Detected,
                            ElementIds = elementId.ToString(),
                            ElementCount = 1,
                            Reason = $"Protected element moved via {bypassMethod}",
                            EventSource = "Pin Protection",
                            SessionId = sessionId
                        });
                    }
                    catch (Exception auditEx)
                    {
                        _logger?.LogWarning($"Failed to log audit entry: {auditEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error logging pin protection violation: {ex.Message}", ex);
            }
        }
        #region API Sync Helpers

        private void SyncModelSessionToApiAsync(ModelSessionApiRequest request)
        {
            var syncService = _modelSessionSyncGetter?.Invoke();
            if (syncService == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await syncService.SyncModelSessionAsync(request);
                    _logger?.LogDebug($"Model session API sync: {(result ? "Success" : "Failed/Queued")}");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Model session API sync error (non-critical): {ex.Message}");
                }
            });
        }

        private void SyncModelSessionStatusUpdateAsync(string sessionId, string modelGuid, string status, string? modifiedBy = null)
        {
            var syncService = _modelSessionSyncGetter?.Invoke();
            if (syncService == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await syncService.UpdateModelSessionStatusAsync(sessionId, modelGuid, status, modifiedBy: modifiedBy);
                    _logger?.LogDebug($"Model session status update ({status}): {(result ? "Success" : "Failed/Queued")}");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Model session status update error (non-critical): {ex.Message}");
                }
            });
        }

        private void SyncSyncSaveMetricsToApiAsync(
            string captureId, string sessionId, string documentId,
            string modelGuid, string modelPath, string modelName,
            string captureType, string syncGuid, string capturedBy,
            FastMetrics metrics)
        {
            // Skip if background sync is disabled
            if (!_featureToggleService.IsFeatureEnabled("BackgroundSync"))
            {
                _logger?.LogDebug("SyncSave metrics sync skipped — BackgroundSync disabled");
                return;
            }

            var syncService = _metricsSyncGetter?.Invoke();
            if (syncService == null) return;

            var request = new SyncSaveMetricsApiRequest
            {
                CaptureId = captureId,
                SessionId = sessionId,
                DocumentId = documentId,
                ModelGuid = modelGuid,
                ModelPath = modelPath,
                ModelName = modelName,
                CaptureType = captureType,
                SyncGuid = syncGuid,
                CapturedAt = DateTime.UtcNow,
                CapturedBy = capturedBy,
                FileSizeBytes = metrics.FileSizeBytes ?? 0,
                LevelsCount = metrics.LevelsCount ?? 0,
                GridsCount = metrics.GridsCount ?? 0,
                DesignOptionsCount = metrics.DesignOptionsCount ?? 0,
                LinkedDwgCount = metrics.LinkedDwgCount ?? 0,
                ImportedDwgCount = metrics.ImportedDwgCount ?? 0,
                LinkedRevitCount = metrics.LinkedRevitCount ?? 0,
                RasterImagesCount = metrics.RasterImagesCount ?? 0,
                WarningsCount = metrics.WarningsCount ?? 0,
                DuplicateElementsCount = metrics.DuplicateElementsCount ?? 0,
                ModelGroupsCount = metrics.ModelGroupsCount ?? 0,
                DetailGroupsCount = metrics.DetailGroupsCount ?? 0,
                TotalViewsCount = metrics.TotalViewsCount ?? 0,
                SheetsCount = metrics.SheetsCount ?? 0,
                TotalFamiliesCount = metrics.TotalFamiliesCount ?? 0,
                TotalWorksetsCount = metrics.TotalWorksetsCount ?? 0,
                SharedCoordNs = metrics.SharedCoordNs,
                SharedCoordEw = metrics.SharedCoordEw,
                SharedCoordElevation = metrics.SharedCoordElevation,
                SharedCoordUnit = metrics.SharedCoordUnit,
                CreatedAt = DateTime.UtcNow
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await syncService.SyncSyncSaveMetricsAsync(request);
                    _logger?.LogDebug($"SyncSave metrics API sync: {(result ? "Success" : "Failed/Queued")}");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"SyncSave metrics API sync error (non-critical): {ex.Message}");
                }
            });
        }

        private void SyncModelSyncToApiAsync(string syncGuid, int? totalWorksetsCount)
        {
            if (_syncRepository == null) return;

            // Skip if background sync is disabled
            if (!_featureToggleService.IsFeatureEnabled("BackgroundSync"))
            {
                _logger?.LogDebug("Model sync API sync skipped — BackgroundSync disabled");
                return;
            }

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var syncRecord = await _syncRepository.GetSyncByGuidAsync(syncGuid);
                    if (syncRecord == null)
                    {
                        _logger?.LogWarning($"Model sync record not found for API sync: {syncGuid}");
                        return;
                    }

                    var request = new ModelSyncApiRequest
                    {
                        SyncGuid = syncRecord.SyncGuid,
                        SessionId = syncRecord.SessionId,
                        ModelGuid = syncRecord.ModelGuid,
                        ModelPath = syncRecord.ModelPath,
                        ModelName = syncRecord.ModelName,
                        SyncedBy = syncRecord.SyncedBy,
                        SyncStartedAt = syncRecord.SyncStartedAt.ToUniversalTime(),
                        SyncEndedAt = syncRecord.SyncEndedAt?.ToUniversalTime(),
                        SyncDurationSeconds = syncRecord.SyncDurationSeconds ?? 0,
                        IsLocalSaved = syncRecord.IsLocalSaved,
                        IsSucceeded = syncRecord.IsSucceeded,
                        IsRelinquished = syncRecord.IsRelinquished,
                        TotalWorksetsCount = totalWorksetsCount ?? 0,
                        ErrorMessage = syncRecord.ErrorMessage,
                        CreatedAt = syncRecord.CreatedAt.ToUniversalTime(),
                        ModifiedAt = syncRecord.ModifiedAt.ToUniversalTime()
                    };

                    var syncService = _metricsSyncGetter?.Invoke();
                    if (syncService == null) return;

                    var result = await syncService.SyncModelSyncAsync(request);
                    _logger?.LogDebug($"Model sync API sync: {(result ? "Success" : "Failed/Queued")}");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Model sync API sync error (non-critical): {ex.Message}");
                }
            });
        }

        private int? CountTotalWorksets(Document doc)
        {
            try
            {
                if (!doc.IsWorkshared)
                    return null;

                int count = 0;
                var collector = new FilteredWorksetCollector(doc);
                foreach (Workset w in collector)
                {
                    if (w.Kind == WorksetKind.UserWorkset)
                        count++;
                }
                return count;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to count total worksets: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Event Protection Enforcement

        /// <summary>
        /// Core enforcement method for event protections.
        /// Returns true if event should be allowed, false if cancelled.
        /// </summary>
        private bool EnforceEventProtection(string dummyCommandId, EventContext context, string? additionalInfo = null)
        {
            if (!_featureToggleService.IsFeatureEnabled("EventProtection")) return true;
            var protectionService = _eventProtectionServiceGetter?.Invoke();
            if (protectionService == null || !protectionService.IsProtectionEnabled) return true;
            var settings = protectionService.GetProtectionByDummyCommandId(dummyCommandId);
            if (settings == null || !settings.Enabled) return true;
            return ExecuteProtectionEnforcement(settings, context, additionalInfo);
        }

        private bool EnforceEventProtection(EventProtectionSettings settings, EventContext context, string? additionalInfo = null)
        {
            if (!_featureToggleService.IsFeatureEnabled("EventProtection")) return true;
            var protectionService = _eventProtectionServiceGetter?.Invoke();
            if (protectionService == null || !protectionService.IsProtectionEnabled) return true;
            if (settings == null || !settings.Enabled) return true;
            return ExecuteProtectionEnforcement(settings, context, additionalInfo);
        }

        private bool ExecuteProtectionEnforcement(EventProtectionSettings settings, EventContext context, string? additionalInfo)
        {
            var dummyCommandId = settings.DummyCommandId;
            try
            {
                _logger?.LogInfo($"Event protection triggered: {dummyCommandId} (Mode: {settings.Mode})");

                // Conditional activation: if RuleIds are specified, only enforce when at least one is active
                if (settings.RuleIds.Count > 0)
                {
                    var ruleService = _ruleServiceGetter?.Invoke();
                    if (ruleService != null)
                    {
                        var matchedRules = ruleService.GetRulesByIds(settings.RuleIds);
                        if (matchedRules.Count == 0)
                        {
                            _logger?.LogInfo($"Event protection '{dummyCommandId}' skipped: none of {settings.RuleIds.Count} linked rules are active");
                            return true;
                        }

                        _logger?.LogInfo($"Event protection '{dummyCommandId}': {matchedRules.Count}/{settings.RuleIds.Count} linked rules active");

                        foreach (var rule in matchedRules)
                        {
                            var ruleMode = (InterventionMode)((int)rule.Mode - 1);
                            if (ruleMode > settings.Mode)
                            {
                                _logger?.LogInfo($"Event protection '{dummyCommandId}' mode escalated from {settings.Mode} to {ruleMode} by rule '{rule.Name}'");
                                settings.Mode = ruleMode;
                            }
                        }

                        foreach (var rule in matchedRules)
                        {
                            if (rule.CaptureBeforeScreenshot) settings.CaptureBeforeScreenshot = true;
                            if (rule.CaptureAfterScreenshot) settings.CaptureAfterScreenshot = true;
                            if (rule.RequireComment) settings.RequireComment = true;
                        }
                    }
                }

                if (settings.Mode == InterventionMode.Notify && settings.RequireComment)
                {
                    settings.RequireComment = false;
                    _logger?.LogDebug($"Event protection '{dummyCommandId}': stripped RequireComment — not applicable in Notify mode");
                }

                var auditLogId = Guid.NewGuid().ToString();

                if (settings.CaptureBeforeScreenshot)
                    CaptureEventScreenshot(settings, context, "before", auditLogId);

                var handler = GetEventInterventionHandler();
                var result = handler.ProcessIntervention(settings, context, additionalInfo);

                // Capture the enforcement decision BEFORE any logging so that an audit-log
                // failure cannot flip "blocked" to "allowed" via the outer catch block.
                bool allowed = result.Allowed;

                try
                {
                    string action = allowed && result.UserOverrode ? "override"
                        : allowed ? "allowed"
                        : (settings.Mode == InterventionMode.Assist ? "cancelled" : "blocked");
                    string? overrideMethod = result.UserOverrode ? "AdminPrivilege" : null;

                    LogEventAuditEntry(settings, context, action, result.Reason,
                        auditLogId: auditLogId, userComment: result.UserComment, overrideMethod: overrideMethod);

                    if (allowed && settings.CaptureAfterScreenshot)
                        CaptureEventScreenshot(settings, context, "after", auditLogId);
                }
                catch (Exception logEx)
                {
                    _logger?.LogError($"Audit/screenshot step failed for '{dummyCommandId}' (enforcement decision preserved): {logEx.Message}", logEx);
                }

                return allowed;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error enforcing event protection {dummyCommandId}: {ex.Message}", ex);
                return true;
            }
        }

        /// <summary>
        /// Capture screenshot for event protection and record to evidence repository.
        /// Links the evidence to the audit entry via auditLogId (evidence → audit FK).
        /// </summary>
        private string? CaptureEventScreenshot(EventProtectionSettings settings, EventContext context, string stage, string? auditLogId = null)
        {
            var screenshotService = _screenshotServiceGetter?.Invoke();
            if (screenshotService == null)
            {
                _logger?.LogDebug("Screenshot service not available for event protection");
                return null;
            }

            try
            {
                var evidenceId = Guid.NewGuid().ToString();
                var sessionId = _revitContext?.SessionId.ToString() ?? Guid.NewGuid().ToString();

                var imageBase64 = screenshotService.CaptureRevitWindowAsBase64();

                if (!string.IsNullOrEmpty(imageBase64))
                {
                    _logger?.LogInfo($"Event screenshot captured for {settings.DummyCommandId} ({stage}): {imageBase64.Length} chars");

                    var savedPath = screenshotService.SaveBase64ToTempFile(imageBase64, sessionId, $"{evidenceId}_{stage}");
                    string? fileHash = null;
                    if (!string.IsNullOrEmpty(savedPath))
                    {
                        try
                        {
                            using (var sha256 = System.Security.Cryptography.SHA256.Create())
                            {
                                var fileBytes = System.IO.File.ReadAllBytes(savedPath);
                                var hashBytes = sha256.ComputeHash(fileBytes);
                                fileHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                            }
                        }
                        catch { /* hash failure is non-fatal */ }
                    }

                    var evidenceRepository = _evidenceRepositoryGetter?.Invoke();
                    if (evidenceRepository != null)
                    {
                        var evidence = new EvidenceRepository.EvidenceCapture
                        {
                            EvidenceId = evidenceId,
                            AuditLogId = auditLogId,
                            SessionId = sessionId,
                            ProtectionType = "event",
                            CaptureType = "screenshot",
                            CaptureStage = stage,
                            CapturedAt = DateTime.UtcNow,
                            CommandId = settings.DummyCommandId,
                            CommandName = settings.ProtectionName,
                            ElementIds = "",
                            ElementCount = 0,
                            FilePath = savedPath,
                            FileSizeBytes = imageBase64.Length * 3 / 4,
                            FileFormat = "png",
                            UploadStatus = "pending",
                            FileHashSha256 = fileHash,
                            Metadata = $"{{\"filePath\":\"{context.FilePath?.Replace("\\", "\\\\")}\",\"mode\":\"{settings.Mode}\"}}"
                        };

                        _ = evidenceRepository.RecordEvidenceCaptureAsync(evidence);
                    }

                    return evidenceId;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to capture event screenshot: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Log audit entry for event protection enforcement.
        /// </summary>
        private void LogEventAuditEntry(
            EventProtectionSettings settings,
            EventContext context,
            string action,
            string? reason,
            string? auditLogId = null,
            string? userComment = null,
            string? overrideMethod = null)
        {
            try
            {
                if (_auditRepository == null)
                {
                    _logger?.LogDebug("Audit repository not available, skipping event audit log");
                    return;
                }

                var isAdmin = _isAdminCheck?.Invoke() ?? false;
                var username = context.Document?.Application?.Username ?? Environment.UserName;
                var sessionId = _revitContext?.SessionId.ToString();

                string? modelGuid = null;
                try
                {
                    if (context.Document != null)
                        modelGuid = ModelGuidHelper.GetModelGuid(context.Document, _logger);
                }
                catch { /* Non-critical */ }

                var auditEntry = new Core.Protection.Models.ProtectionAuditEntry
                {
                    AuditLogId = auditLogId ?? Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow,
                    UserName = username,
                    WasCompanyAdmin = isAdmin,
                    WasProjectAdmin = isAdmin,
                    ModelGuid = modelGuid,
                    ProtectionId = settings.Id,
                    CommandName = settings.ProtectionName,
                    Mode = settings.Mode switch
                    {
                        InterventionMode.Notify => Core.Rules.Models.ProtectionMode.Notify,
                        InterventionMode.Assist => Core.Rules.Models.ProtectionMode.Assist,
                        InterventionMode.Protect => Core.Rules.Models.ProtectionMode.Protect,
                        _ => Core.Rules.Models.ProtectionMode.Notify
                    },
                    Action = action switch
                    {
                        "allowed" => Core.Protection.Models.ProtectionAction.Allowed,
                        "blocked" => Core.Protection.Models.ProtectionAction.Blocked,
                        "cancelled" => Core.Protection.Models.ProtectionAction.Cancelled,
                        "override" => Core.Protection.Models.ProtectionAction.Override,
                        _ => Core.Protection.Models.ProtectionAction.Allowed
                    },
                    ElementIds = "",
                    ElementCount = 0,
                    Reason = reason,
                    UserComment = userComment,
                    OverrideMethod = overrideMethod,
                    EventSource = "Event Restriction",
                    SessionId = sessionId,
                    SentMail = !settings.SendEmail
                };

                _auditRepository.SaveAuditEntry(auditEntry);
                _logger?.LogDebug($"Event audit entry logged: {settings.DummyCommandId} - {action}");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to log event audit entry: {ex.Message}");
            }
        }

        #region DocumentSaving Protections

        /// <summary>
        /// Protection 4: Check if saving a document would overwrite an earlier file version.
        /// Returns true if allowed, false if blocked.
        /// </summary>
        private bool CheckSaveOverEarlierVersionProtection(Document doc)
        {
            try
            {
                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null) return true;

                var settings = protectionService.GetProtectionByDummyCommandId("Ze_SaveOverEarlierFileVersionProtection");
                if (settings == null || !settings.Enabled) return true;

                var filePath = doc.PathName;
                if (string.IsNullOrEmpty(filePath)) return true;

                // Cloud models don't use BasicFileInfo
                if (IsCloudModelPath(filePath)) return true;

                if (!System.IO.File.Exists(filePath)) return true;

                var basicFileInfo = BasicFileInfo.Extract(filePath);
                if (basicFileInfo == null) return true;

                var savedVersion = basicFileInfo.Format;
                var savedYear = ExtractYearFromFormat(savedVersion);
                var currentYear = doc.Application.VersionNumber;

                if (savedYear == null || currentYear == null) return true;

                if (int.TryParse(savedYear, out int savedYearInt) &&
                    int.TryParse(currentYear, out int currentYearInt))
                {
                    if (savedYearInt < currentYearInt)
                    {
                        var context = new EventContext
                        {
                            FilePath = filePath,
                            FileVersion = savedVersion,
                            CurrentRevitVersion = currentYear,
                            Document = doc
                        };

                        return EnforceEventProtection("Ze_SaveOverEarlierFileVersionProtection", context,
                            $"File was saved in Revit {savedYear}, current Revit is {currentYear}.");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error checking save over earlier version protection: {ex.Message}", ex);
                return true; // Fail open
            }
        }

        #endregion

        #region Sync Protections

        /// <summary>
        /// Protection 8: Sync Conflict Detection.
        /// Checks if another user is syncing via SignalR or active sync tracking.
        /// Returns true if allowed, false if blocked.
        /// </summary>
        /// <summary>
        /// Show the SyncConflictDialog from inside the OnDocumentSynchronizingWithCentral fallback
        /// after we've already cancelled the sync. The dialog gives the user the same UX as the
        /// upstream SyncCommandBinding path: see who's syncing, optionally join the queue.
        /// </summary>
        private void ShowFallbackSyncConflictDialog(
            Revit.SyncTrafficControl.SyncTrafficControlService stc,
            string blockedByUsername,
            DateTime blockedSince,
            string modelName,
            string modelGuid,
            string sessionId,
            string username)
        {
            try
            {
                var eventBus = _signalREventBusGetter?.Invoke();
                var dialog = new BIManageRevit.BIManage.Views.SyncTrafficControl.SyncConflictDialog(
                    blockedByUsername, blockedSince, modelName, eventBus, modelGuid);

                BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);

                var result = dialog.ShowDialog();
                if (result == true && !dialog.SyncReady)
                {
                    // User clicked "Join Queue" — register so they get a SyncRequest when their turn arrives.
                    stc.JoinQueue(modelGuid, modelName, sessionId, username);
                    _logger?.LogInfo("[SyncTrafficControl/fallback] User joined queue from conflict dialog");
                }
                else if (result == true && dialog.SyncReady)
                {
                    _logger?.LogInfo("[SyncTrafficControl/fallback] Blocker finished while dialog was open — user can retry sync now");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncTrafficControl/fallback] Failed to show conflict dialog: {ex.Message}");
            }
        }

        private bool CheckSyncConflictDetection(Document doc)
        {
            try
            {
                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null) return true;

                var settings = protectionService.GetProtectionByDummyCommandId("Ze_SyncConflictDetection");
                if (settings == null || !settings.Enabled) return true;

                // Check if there are active sync operations for this model
                var modelGuid = Helpers.ModelGuidHelper.GetModelGuid(doc, _logger);
                if (string.IsNullOrEmpty(modelGuid)) return true;

                // Check _activeSyncOperations for conflicts
                string? conflictingUser = null;
                if (_activeSyncOperations.TryGetValue(modelGuid, out var syncingUser))
                {
                    var currentUser = doc.Application?.Username ?? Environment.UserName;
                    if (!string.Equals(syncingUser, currentUser, StringComparison.OrdinalIgnoreCase))
                    {
                        conflictingUser = syncingUser;
                    }
                }

                // Also check via SyncTrafficControl (SignalR-backed)
                if (conflictingUser == null)
                {
                    var syncTrafficControl = _syncTrafficControlGetter?.Invoke();
                    if (syncTrafficControl != null)
                    {
                        var remoteSyncer = syncTrafficControl.GetActiveSyncerUsername(modelGuid);
                        if (remoteSyncer != null)
                        {
                            var currentUser = doc.Application?.Username ?? Environment.UserName;
                            if (!string.Equals(remoteSyncer, currentUser, StringComparison.OrdinalIgnoreCase))
                            {
                                conflictingUser = remoteSyncer;
                                _logger?.LogInfo($"Sync conflict detected via SignalR: {remoteSyncer} is syncing {modelGuid}");
                            }
                        }
                    }
                }

                if (conflictingUser == null) return true;

                var context = new EventContext
                {
                    FilePath = doc.PathName,
                    IsWorkshared = true,
                    SyncingUser = conflictingUser,
                    Document = doc,
                    CurrentRevitVersion = _controlledApplication.VersionNumber
                };

                return EnforceEventProtection("Ze_SyncConflictDetection", context,
                    $"User '{conflictingUser}' is currently synchronizing with the central model.");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error checking sync conflict detection: {ex.Message}", ex);
                return true;
            }
        }

        #endregion

        #region FamilyLoading Protections

        /// <summary>
        /// Protection 5: Check if family is being loaded from a non-approved library location.
        /// Returns true if allowed, false if blocked.
        /// </summary>
        private bool CheckFamilyLibraryProtection(string familyPath, string familyName, Document doc)
        {
            try
            {
                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null)
                {
                    _logger?.LogInfo($"[FamilyLibraryProtection] SKIP — protectionService is null");
                    return true;
                }

                var settings = protectionService.GetProtectionByDummyCommandId("Ze_FamilyLibrarySettings");
                if (settings == null)
                {
                    _logger?.LogInfo($"[FamilyLibraryProtection] SKIP — settings not found for Ze_FamilyLibrarySettings");
                    return true;
                }
                if (!settings.Enabled)
                {
                    _logger?.LogInfo($"[FamilyLibraryProtection] SKIP — protection is disabled (Enabled=false)");
                    return true;
                }

                var registeredModelsRepo = _registeredModelsRepoGetter?.Invoke();
                if (registeredModelsRepo == null) return true;
                var familyLibModelGuid = Helpers.ModelGuidHelper.GetModelGuid(doc, _logger);
                var isFamilyLibRegistered = !string.IsNullOrEmpty(familyLibModelGuid) &&
                    registeredModelsRepo.IsModelRegisteredAsync(familyLibModelGuid).GetAwaiter().GetResult();
                if (!isFamilyLibRegistered)
                {
                    _logger?.LogInfo($"[FamilyLibraryProtection] SKIP — '{doc?.Title}' is not a registered model.");
                    return true;
                }

                var config = protectionService.GetConfiguration<FamilyLibraryConfig>("Ze_FamilyLibrarySettings")
                             ?? new FamilyLibraryConfig();
                _logger?.LogInfo($"[FamilyLibraryProtection] Config: NonApprovedPaths={config.NonApprovedPaths?.Count ?? 0}, ApprovedPaths={config.ApprovedLibraryPaths?.Count ?? 0}");

                var normalizedPath = familyPath?.Replace('\\', '/').ToLowerInvariant() ?? "";

                var context = new EventContext
                {
                    FilePath = familyPath,
                    FamilyName = familyName,
                    Document = doc,
                    CurrentRevitVersion = _controlledApplication.VersionNumber
                };

                // Blacklist check — NonApprovedPaths (defaults to ["C:\"] when not explicitly configured)
                var effectiveNonApprovedPaths = (config.NonApprovedPaths == null || config.NonApprovedPaths.Count == 0)
                    ? new List<string> { @"C:\" }
                    : config.NonApprovedPaths;

                _logger?.LogInfo($"[FamilyLibraryProtection] Checking '{familyName}' | Path: '{familyPath}' | BlockedPaths: [{string.Join(", ", effectiveNonApprovedPaths)}] | ApprovedPaths: [{string.Join(", ", config.ApprovedLibraryPaths ?? new List<string>())}]");

                if (effectiveNonApprovedPaths.Count > 0)
                {
                    foreach (var blockedPath in effectiveNonApprovedPaths)
                    {
                        if (string.IsNullOrEmpty(blockedPath)) continue;
                        var normalizedBlocked = blockedPath.Replace('\\', '/').ToLowerInvariant();
                        if (normalizedPath.StartsWith(normalizedBlocked))
                        {
                            return EnforceEventProtection("Ze_FamilyLibrarySettings", context,
                                $"Family '{familyName}' is being loaded from a non-approved library location.\nPath: {familyPath}");
                        }
                    }
                }

                // Whitelist check — ApprovedLibraryPaths
                if (config.ApprovedLibraryPaths?.Count == 0) return true;

                foreach (var approvedPath in config.ApprovedLibraryPaths)
                {
                    if (string.IsNullOrEmpty(approvedPath)) continue;
                    var normalizedApproved = approvedPath.Replace('\\', '/').ToLowerInvariant();
                    if (normalizedPath.StartsWith(normalizedApproved))
                        return true;
                }

                return EnforceEventProtection("Ze_FamilyLibrarySettings", context,
                    $"Family '{familyName}' is being loaded from a non-approved library location.\nPath: {familyPath}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error checking family library protection: {ex.Message}", ex);
                return true;
            }
        }

        /// <summary>
        /// Protection 6: Check if an existing family in the document will be overwritten.
        /// Returns true if allowed, false if blocked.
        /// </summary>
        private bool CheckFamilyOverwriteProtection(string familyPath, string familyName, Document doc)
        {
            try
            {
                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null) return true;

                var settings = protectionService.GetProtectionByDummyCommandId("Ze_FamilyLoadingProtection");
                if (settings == null || !settings.Enabled) return true;

                if (doc == null || string.IsNullOrEmpty(familyName)) return true;

                // Check if a family with the same name already exists in the document
                var collector = new FilteredElementCollector(doc);
                var existingFamily = collector
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));

                if (existingFamily == null) return true; // No existing family — no overwrite concern

                var context = new EventContext
                {
                    FilePath = familyPath,
                    FamilyName = familyName,
                    Document = doc,
                    CurrentRevitVersion = _controlledApplication.VersionNumber
                };

                return EnforceEventProtection("Ze_FamilyLoadingProtection", context,
                    $"Family '{familyName}' already exists in the document and will be overwritten.");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error checking family overwrite protection: {ex.Message}", ex);
                return true;
            }
        }

        /// <summary>
        /// Protection 7: Check if family version mismatches current Revit version.
        /// Returns true if allowed, false if blocked.
        /// </summary>
        private bool CheckFamilyVersionMismatchProtection(string familyPath, string familyName, Document doc)
        {
            try
            {
                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null) return true;

                var settings = protectionService.GetProtectionByDummyCommandId("Ze_FamilyVersionMismatch");
                if (settings == null || !settings.Enabled) return true;

                if (string.IsNullOrEmpty(familyPath)) return true;
                if (IsCloudModelPath(familyPath)) return true;
                if (!System.IO.File.Exists(familyPath)) return true;

                var basicFileInfo = BasicFileInfo.Extract(familyPath);
                if (basicFileInfo == null) return true;

                var savedVersion = basicFileInfo.Format;
                var savedYear = ExtractYearFromFormat(savedVersion);
                var currentYear = _controlledApplication.VersionNumber;

                if (savedYear == null || currentYear == null) return true;

                if (int.TryParse(savedYear, out int savedYearInt) &&
                    int.TryParse(currentYear, out int currentYearInt))
                {
                    if (savedYearInt != currentYearInt)
                    {
                        var context = new EventContext
                        {
                            FilePath = familyPath,
                            FamilyName = familyName,
                            FileVersion = savedVersion,
                            CurrentRevitVersion = currentYear,
                            Document = doc
                        };

                        return EnforceEventProtection("Ze_FamilyVersionMismatch", context,
                            $"Family '{familyName}' was created in Revit {savedYear}, current Revit is {currentYear}.");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error checking family version mismatch protection: {ex.Message}", ex);
                return true;
            }
        }

        #endregion

        #region Printing/Exporting/DuplicateSession Protections

        /// <summary>
        /// Protection 17 (pre-open): Checks for a duplicate Revit username via local GUID lookup + API.
        /// Called from OnDocumentOpening (sync) — blocks on async internally.
        /// Returns true if opening is allowed, false if it should be cancelled.
        /// </summary>
        private bool CheckDuplicateUserSessionProtectionBeforeOpen(string pathName)
        {
            try
            {
                _logger?.LogInfo($"[DupCheck] Starting pre-open check for: {pathName}");

                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null) { _logger?.LogInfo("[DupCheck] SKIP: protectionService is null"); return true; }

                var settings = protectionService.GetProtectionByDummyCommandIdDirect("Ze_DuplicateUserSessionProtection");
                if (settings == null) { _logger?.LogInfo("[DupCheck] SKIP: settings not found in cache or DB"); return true; }
                if (!settings.Enabled) { _logger?.LogInfo("[DupCheck] SKIP: protection not enabled"); return true; }

                var currentUsername = _uiApplicationProvider?.UIApplication?.Application?.Username;
                _logger?.LogInfo($"[DupCheck] CurrentUsername='{currentUsername}', MachineName='{Environment.MachineName}'");
                if (string.IsNullOrEmpty(currentUsername)) { _logger?.LogInfo("[DupCheck] SKIP: username is null/empty"); return true; }

                // e.PathName may be a local copy path — use BasicFileInfo to resolve the real central path.
                // We also need IsWorkshared to short-circuit below: a local non-workshared file
                // cannot have duplicate users (only one Revit can have it open at a time), so
                // skipping the API + DB calls here saves ~300ms of UI-thread blocking per open.
                string centralPath = pathName;
                bool isWorksharedFile = false;
                bool isCloudModel = IsCloudModelPath(pathName);
                try
                {
                    if (!isCloudModel && System.IO.File.Exists(pathName))
                    {
                        var bfi = BasicFileInfo.Extract(pathName);
                        if (bfi != null)
                        {
                            isWorksharedFile = bfi.IsWorkshared;
                            if (isWorksharedFile && !string.IsNullOrEmpty(bfi.CentralPath))
                                centralPath = bfi.CentralPath;
                        }
                    }
                }
                catch (Exception bfiEx)
                {
                    _logger?.LogInfo($"[DupCheck] BasicFileInfo fallback to pathName: {bfiEx.Message}");
                }
                _logger?.LogInfo($"[DupCheck] Resolved centralPath='{centralPath}', isWorkshared={isWorksharedFile}, isCloud={isCloudModel}");

                // Short-circuit: a local, non-workshared .rvt cannot have concurrent users.
                // Revit's file lock prevents two processes from opening the same .rvt at once
                // when there's no central. Skip the API + DB roundtrips entirely.
                // Cloud models are still checked — they support multi-user editing.
                if (!isCloudModel && !isWorksharedFile)
                {
                    _logger?.LogDebug("[DupCheck] SKIP: local non-workshared file — no possible duplicates");
                    return true;
                }

                var activeUsers = new List<ModelActiveUser>();

                var config = protectionService.GetConfiguration<DuplicateUserSessionConfig>("Ze_DuplicateUserSessionProtection");
                _logger?.LogInfo($"[DupCheck] config={config?.GetType().Name ?? "null"}, CheckApiForRemoteSessions={config?.CheckApiForRemoteSessions}");

                if (config == null || config.CheckApiForRemoteSessions)
                {
                    var registeredModelsRepo = _registeredModelsRepoGetter?.Invoke();
                    _logger?.LogInfo($"[DupCheck] registeredModelsRepo={registeredModelsRepo?.GetType().Name ?? "null"}");
                    if (registeredModelsRepo != null)
                    {
                        var modelGuid = registeredModelsRepo.GetModelGuidByPathAsync(centralPath)
                            .GetAwaiter().GetResult();
                        _logger?.LogInfo($"[DupCheck] modelGuid='{modelGuid}'");

                        if (!string.IsNullOrEmpty(modelGuid))
                        {
                            var syncService = _modelSessionSyncGetter?.Invoke();
                            _logger?.LogInfo($"[DupCheck] syncService={syncService?.GetType().Name ?? "null"}");
                            if (syncService != null)
                            {
                                // Cap the UI-thread block at 2s. Worst case: slow backend or
                                // intermittent network → user still gets a fast open and the
                                // post-open async check (CheckDuplicateUserSessionProtectionAsync,
                                // wired from OnDocumentOpened at line 387) catches any missed
                                // duplicate and closes the model if the user cancels the dialog.
                                var apiTask = Task.Run(() => syncService.GetModelUsersAsync(modelGuid));
                                var completed = apiTask.Wait(TimeSpan.FromSeconds(2));
                                if (!completed)
                                {
                                    _logger?.LogInfo("[DupCheck] Pre-open API call timed out after 2s — proceeding; post-open async check will retry");
                                }
                                else
                                {
                                    var apiUsers = apiTask.Result;
                                    _logger?.LogInfo($"[DupCheck] API returned {apiUsers.Count} user(s)");

                                    foreach (var u in apiUsers)
                                    {
                                        _logger?.LogInfo($"[DupCheck] API user: RevitUsername='{u.RevitUsername}', ComputerName='{u.ComputerName}'");
                                        if (!string.IsNullOrEmpty(u.RevitUsername))
                                            activeUsers.Add(new ModelActiveUser
                                            {
                                                RevitUsername = u.RevitUsername,
                                                ComputerName = u.ComputerName ?? "",
                                                Username = u.Username ?? ""
                                            });
                                    }
                                }
                            }
                        }
                    }
                }

                if (_sessionRepository != null)
                {
                    var localUsers = _sessionRepository.GetActiveUsersByModelPathAsync(centralPath)
                        .GetAwaiter().GetResult();
                    _logger?.LogInfo($"[DupCheck] Local DB returned {localUsers.Count} user(s)");

                    foreach (var u in localUsers)
                    {
                        bool alreadyKnown = activeUsers.Any(a =>
                            string.Equals(a.RevitUsername, u.RevitUsername, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(a.ComputerName, u.ComputerName, StringComparison.OrdinalIgnoreCase));
                        if (!alreadyKnown) activeUsers.Add(u);
                    }
                }

                _logger?.LogInfo($"[DupCheck] Total merged users: {activeUsers.Count}");
                if (activeUsers.Count > 0)
                {
                    var userSummary = string.Join(", ", activeUsers.Select(u => $"{u.RevitUsername} ({u.ComputerName})"));
                    _logger?.LogInfo($"[DupCheck] Active users in model: {userSummary}");
                }
                if (activeUsers.Count == 0) return true;

                var duplicate = activeUsers.FirstOrDefault(u =>
                    string.Equals(u.RevitUsername, currentUsername, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(u.ComputerName) &&
                    !string.Equals(u.ComputerName, Environment.MachineName, StringComparison.OrdinalIgnoreCase));

                if (duplicate == null) { _logger?.LogInfo($"[DupCheck] No duplicate found for '{currentUsername}' on this machine"); return true; }

                _logger?.LogWarning($"[DupCheck] DUPLICATE: '{currentUsername}' active on '{duplicate.ComputerName}' — blocking open of '{pathName}'");

                var context = new EventContext
                {
                    FilePath = centralPath,
                    CurrentRevitVersion = _controlledApplication.VersionNumber
                };

                return EnforceEventProtection(settings, context,
                    $"User '{currentUsername}' is already active on '{duplicate.ComputerName}' for this model.");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[DupCheck] Exception: {ex.Message}", ex);
                return true;
            }
        }

        /// <summary>
        /// Protection: Check document printing protection.
        /// Returns true if printing is allowed, false if blocked.
        /// </summary>
        private bool CheckDocumentPrintingProtection(Document doc)
        {
            try
            {
                var context = new EventContext
                {
                    FilePath = doc.PathName,
                    Document = doc,
                    CurrentRevitVersion = _controlledApplication.VersionNumber
                };

                return EnforceEventProtection("Ze_DocumentPrintingProtection", context,
                    "Print operation requested");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error checking document printing protection: {ex.Message}", ex);
                return true; // Allow on error
            }
        }

        /// <summary>
        /// Protection 17: Check for duplicate user session on the same model.
        /// Prevents opening a model when the same Revit username is already active
        /// on another machine for the same model.
        /// Called from OnDocumentOpened (async-safe, username guaranteed available).
        /// <para>
        /// This is the safety net for the pre-open check
        /// (<see cref="CheckDuplicateUserSessionProtectionBeforeOpen"/>): if the pre-open
        /// API call timed out (2s cap), failed (network blip), or was skipped because
        /// CheckApiForRemoteSessions was disabled, this method re-queries the API on a
        /// background thread AFTER the model is already open, then shows the protection
        /// dialog and closes the doc if the user cancels.
        /// </para>
        /// If blocked, closes the document.
        /// Returns true if allowed, false if blocked (document closed).
        /// </summary>
        private async Task<bool> CheckDuplicateUserSessionProtectionAsync(Document doc, string currentUsername, string modelPath)
        {
            try
            {
                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null) return true;

                var settings = protectionService.GetProtectionByDummyCommandId("Ze_DuplicateUserSessionProtection");
                if (settings == null || !settings.Enabled) return true;

                if (string.IsNullOrEmpty(modelPath) || string.IsNullOrEmpty(currentUsername))
                    return true;

                // Collect users from BOTH sources (API + local DB), same as the pre-open path.
                // The pre-open check uses a 2-second timeout to keep model-open snappy; if the
                // API was slow or unreachable then, this async path retries it now without any
                // UI-thread pressure (we already have the model open, the dialog runs on the
                // Revit dispatcher when needed).
                var activeUsers = new List<ModelActiveUser>();

                var config = protectionService.GetConfiguration<DuplicateUserSessionConfig>("Ze_DuplicateUserSessionProtection");
                if (config == null || config.CheckApiForRemoteSessions)
                {
                    try
                    {
                        var registeredModelsRepo = _registeredModelsRepoGetter?.Invoke();
                        if (registeredModelsRepo != null)
                        {
                            var modelGuid = await registeredModelsRepo.GetModelGuidByPathAsync(modelPath);
                            if (!string.IsNullOrEmpty(modelGuid))
                            {
                                var syncService = _modelSessionSyncGetter?.Invoke();
                                if (syncService != null)
                                {
                                    // 5s timeout here is fine — we're not on the UI thread.
                                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                                    var apiUsersTask = syncService.GetModelUsersAsync(modelGuid);
                                    var winner = await Task.WhenAny(apiUsersTask, Task.Delay(TimeSpan.FromSeconds(5), cts.Token));
                                    if (winner == apiUsersTask)
                                    {
                                        cts.Cancel();
                                        var apiUsers = await apiUsersTask;
                                        _logger?.LogInfo($"[DupCheck-Async] API returned {apiUsers.Count} user(s) for model '{modelPath}'");
                                        foreach (var u in apiUsers)
                                        {
                                            if (!string.IsNullOrEmpty(u.RevitUsername))
                                                activeUsers.Add(new ModelActiveUser
                                                {
                                                    RevitUsername = u.RevitUsername,
                                                    ComputerName = u.ComputerName ?? "",
                                                    Username = u.Username ?? ""
                                                });
                                        }
                                    }
                                    else
                                    {
                                        _logger?.LogInfo("[DupCheck-Async] API call timed out after 5s — using local DB only");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception apiEx)
                    {
                        _logger?.LogWarning($"[DupCheck-Async] API query failed (non-fatal — local DB will still be checked): {apiEx.Message}");
                    }
                }

                // Local DB query — merge any users we didn't already see from the API.
                if (_sessionRepository != null)
                {
                    var localUsers = await _sessionRepository.GetActiveUsersByModelPathAsync(modelPath);
                    foreach (var u in localUsers)
                    {
                        bool alreadyKnown = activeUsers.Any(a =>
                            string.Equals(a.RevitUsername, u.RevitUsername, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(a.ComputerName, u.ComputerName, StringComparison.OrdinalIgnoreCase));
                        if (!alreadyKnown) activeUsers.Add(u);
                    }
                }

                if (activeUsers.Count == 0) return true;

                // Check if the same Revit username is active on a different machine
                var duplicateSession = activeUsers.FirstOrDefault(u =>
                    string.Equals(u.RevitUsername, currentUsername, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(u.ComputerName) &&
                    !string.Equals(u.ComputerName, Environment.MachineName, StringComparison.OrdinalIgnoreCase));

                if (duplicateSession == null) return true;

                _logger?.LogWarning($"Duplicate user session detected: '{currentUsername}' is already active on '{duplicateSession.ComputerName}' for model '{modelPath}'");

                var context = new EventContext
                {
                    FilePath = modelPath,
                    Document = doc,
                    CurrentRevitVersion = _controlledApplication.VersionNumber
                };

                bool allowed = EnforceEventProtection("Ze_DuplicateUserSessionProtection", context,
                    $"User '{currentUsername}' is already active on '{duplicateSession.ComputerName}' for this model.");

                if (!allowed)
                {
                    // See CheckOpenCentralFileProtection for the why: closing inline from
                    // inside DocumentOpened silently fails. Defer to the next idle tick.
                    bool deferred = false;
                    try
                    {
                        deferred = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance?.TryCloseDocumentDeferred(
                            doc, $"Duplicate user '{currentUsername}' protection — user cancelled") ?? false;
                    }
                    catch (Exception deferEx)
                    {
                        _logger?.LogWarning($"Deferred close request failed: {deferEx.Message}");
                    }
                    if (!deferred)
                    {
                        try
                        {
                            _logger?.LogWarning($"Closing model '{doc.Title}' inline (deferred unavailable) — duplicate username '{currentUsername}' blocked by event protection");
                            doc.Close(false);
                        }
                        catch (Exception closeEx)
                        {
                            _logger?.LogError($"Failed to close blocked duplicate session model: {closeEx.Message}", closeEx);
                        }
                    }
                    else
                    {
                        _logger?.LogWarning($"Model '{doc.Title}' queued for deferred close — duplicate username '{currentUsername}' blocked by event protection");
                    }
                }

                return allowed;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error checking duplicate user session protection: {ex.Message}", ex);
                return true; // Allow on error
            }
        }

        #endregion

        #region DocumentOpening Protections

        /// <summary>
        /// Protection 1: Check if file is being opened from a non-approved location.
        /// Returns true if allowed, false if blocked.
        /// </summary>
        private bool CheckOpenFileFromNonApprovedProtection(string filePath)
        {
            try
            {
                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null) return true;

                var settings = protectionService.GetProtectionByDummyCommandId("Ze_OpenFileFromNonApprovedProtection");
                if (settings == null || !settings.Enabled) return true;

                var config = protectionService.GetConfiguration<FileOpeningLocationConfig>("Ze_OpenFileFromNonApprovedProtection");

                // Check if path is approved
                bool isApproved = false;

                // Cloud models check
                if (IsCloudModelPath(filePath))
                {
                    isApproved = config?.AllowCloudModels ?? true;
                }
                // Local drive check
                else if (IsLocalDrivePath(filePath))
                {
                    isApproved = config?.AllowLocalDrives ?? false;
                }

                // Check against approved paths list
                if (!isApproved && config?.ApprovedPaths != null)
                {
                    var normalizedPath = filePath.Replace('\\', '/').ToLowerInvariant();
                    foreach (var approvedPath in config.ApprovedPaths)
                    {
                        if (string.IsNullOrEmpty(approvedPath)) continue;
                        var normalizedApproved = approvedPath.Replace('\\', '/').ToLowerInvariant();
                        if (normalizedPath.StartsWith(normalizedApproved))
                        {
                            isApproved = true;
                            break;
                        }
                    }
                }

                // If no config set at all (no approved paths, no flags), treat as approved
                if (config == null || (config.ApprovedPaths.Count == 0 && !config.AllowLocalDrives && !config.AllowCloudModels))
                {
                    return true;
                }

                if (isApproved) return true;

                // File is from non-approved location — enforce
                var context = new EventContext
                {
                    FilePath = filePath,
                    CurrentRevitVersion = _controlledApplication.VersionNumber
                };

                return EnforceEventProtection("Ze_OpenFileFromNonApprovedProtection", context,
                    $"Location: {System.IO.Path.GetDirectoryName(filePath)}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error checking non-approved location protection: {ex.Message}", ex);
                return true; // Fail open
            }
        }

        /// <summary>
        /// Protection 2: Check if central file is being opened directly.
        /// Returns true if allowed, false if blocked.
        /// </summary>
        /// <summary>
        /// Protection 2: Block direct central file opening.
        /// Called from OnDocumentOpened (post-open) because only after the document is opened
        /// can we reliably detect whether the user opened central directly vs created a local copy.
        /// If blocked, closes the document programmatically.
        /// Returns true if allowed, false if blocked (document closed).
        /// </summary>
        private bool CheckOpenCentralFileProtection(Document doc)
        {
            try
            {
                // Plugin-internal opens (e.g. Batch NWC Export's detach-and-discard-worksets
                // pass that enumerates 3D views) wrap the OpenDocumentFile call in an
                // EventProtectionSuppression scope. Honour it here — the protection check
                // is enforced at the Revit event boundary BEFORE Revit processes the
                // OpenOptions.DetachFromCentralOption flag, so even a legitimate detach
                // call would otherwise trip the popup. Same pattern already used by
                // DocumentSavingAs for Deep Analysis temp saves.
                if (BIManage.Revit.Protection.EventProtectionSuppression.IsActive)
                {
                    _logger?.LogInfo("[EventProtection] Open-central check skipped — plugin-internal scope active");
                    return true;
                }

                // Detached workshared files (e.g. NWC export, local copy opened via Open dialog
                // with "Detach from Central" ticked) are never the live central model.
                // IsLocalCopy() cannot catch these because GetCentralModelPath() returns null
                // for a detached document, causing the path comparison to short-circuit to false.
                if (doc.IsDetached)
                {
                    _logger?.LogInfo("[EventProtection] Open-central check skipped — document opened as detached (not central)");
                    return true;
                }

                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null) return true;

                var settings = protectionService.GetProtectionByDummyCommandId("Ze_OpenCentralFileProtection");
                if (settings == null || !settings.Enabled) return true;

                if (DocumentTypeHelper.IsCloud(doc)) return true;

                // At this point we know: isWorkshared=true, isLocal=false, not a cloud model
                // This means doc.PathName == centralModelPath — user opened central directly

                var context = new EventContext
                {
                    FilePath = doc.PathName,
                    IsWorkshared = true,
                    IsCentralFile = true,
                    Document = doc,
                    CurrentRevitVersion = _controlledApplication.VersionNumber
                };

                bool allowed = EnforceEventProtection("Ze_OpenCentralFileProtection", context,
                    "You are opening the central model directly. Please create a local copy instead.");

                if (!allowed)
                {
                    // Defer close to the next Revit idle tick via ExternalEvent.
                    // Document.Close(false) throws InvalidOperationException on the active
                    // document — the ExternalEvent handler catches this and falls back to
                    // PostCommand(Close) which IS allowed on the active document.
                    bool deferred = false;
                    try
                    {
                        deferred = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance?.TryCloseDocumentDeferred(
                            doc, "Open Central File Directly protection — user cancelled") ?? false;
                    }
                    catch (Exception deferEx)
                    {
                        _logger?.LogWarning($"Deferred close request failed: {deferEx.Message}");
                    }

                    if (deferred)
                        _logger?.LogWarning($"Central model '{doc.Title}' queued for deferred close — ExternalEvent path active");
                    else
                        _logger?.LogError($"Central model '{doc.Title}' — deferred infrastructure unavailable; model may not close");
                }

                return allowed;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error checking central file protection: {ex.Message}", ex);
                return true; // Fail open
            }
        }

        /// <summary>
        /// Protection 3: Check if model will be upgraded to current Revit version.
        /// Returns true if allowed, false if blocked.
        /// </summary>
        private bool CheckModelUpgradeProtection(string filePath)
        {
            try
            {
                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null) return true;

                var settings = protectionService.GetProtectionByDummyCommandId("Ze_ModelUpgradeProtection");
                if (settings == null || !settings.Enabled) return true;

                // Cloud models don't use BasicFileInfo
                if (IsCloudModelPath(filePath)) return true;

                if (!System.IO.File.Exists(filePath)) return true;

                var config = protectionService.GetConfiguration<ModelUpgradeConfig>("Ze_ModelUpgradeProtection");

                // Check file type filtering
                var extension = System.IO.Path.GetExtension(filePath)?.ToLowerInvariant();
                if (extension == ".rvt" && config != null && !config.ProtectProjectFiles) return true;
                if (extension == ".rfa" && config != null && !config.ProtectFamilyFiles) return true;

                var basicFileInfo = BasicFileInfo.Extract(filePath);
                if (basicFileInfo == null) return true;

                var savedVersion = basicFileInfo.Format;
                var savedYear = ExtractYearFromFormat(savedVersion);
                var currentYear = _controlledApplication.VersionNumber; // e.g., "2025"

                if (savedYear == null || currentYear == null) return true;

                if (int.TryParse(savedYear, out int savedYearInt) &&
                    int.TryParse(currentYear, out int currentYearInt))
                {
                    var versionDiff = currentYearInt - savedYearInt;
                    var minDiff = config?.MinimumVersionDifference ?? 1;

                    if (versionDiff >= minDiff)
                    {
                        var context = new EventContext
                        {
                            FilePath = filePath,
                            FileVersion = savedVersion,
                            CurrentRevitVersion = currentYear
                        };

                        return EnforceEventProtection("Ze_ModelUpgradeProtection", context,
                            $"File version: {savedYear}, Current Revit: {currentYear} (difference: {versionDiff} years)");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error checking model upgrade protection: {ex.Message}", ex);
                return true; // Fail open
            }
        }

        #endregion

        #region DocumentChanged Post-Action Protections

        /// <summary>
        /// Protection 13: CAD Import Pin Prompt.
        /// After "Import Vector Data" transaction, detect ImportInstance elements and prompt to pin.
        /// </summary>
        private void CheckCADImportPinPrompt(DocumentChangedEventArgs e)
        {
            try
            {
                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null) return;

                var settings = protectionService.GetProtectionByDummyCommandId("Ze_CADImportPinPrompt");
                if (settings == null || !settings.Enabled) return;

                var document = e.GetDocument();
                if (document == null || !document.IsValidObject) return;

                var registeredModelsRepo = _registeredModelsRepoGetter?.Invoke();
                if (registeredModelsRepo == null) return;
                var cadImportModelGuid = Helpers.ModelGuidHelper.GetModelGuid(document, _logger);
                var isCadImportRegistered = !string.IsNullOrEmpty(cadImportModelGuid) &&
                    registeredModelsRepo.IsModelRegisteredAsync(cadImportModelGuid).GetAwaiter().GetResult();
                if (!isCadImportRegistered)
                {
                    _logger?.LogInfo($"CAD Import Pin Prompt skipped — '{document.Title}' is not a registered model.");
                    return;
                }

                // Filter added elements for ImportInstance
                var addedIds = e.GetAddedElementIds();
                var importInstanceIds = addedIds
                    .Where(id =>
                    {
                        var el = document.GetElement(id);
                        return el is ImportInstance;
                    })
                    .ToList();

                if (importInstanceIds.Count == 0)
                {
                    _logger?.LogDebug("CAD Import Pin Prompt: No ImportInstance elements found in added elements");
                    return;
                }

                _logger?.LogInfo($"CAD Import Pin Prompt: Detected {importInstanceIds.Count} ImportInstance element(s)");

                // Run enforcement (Monitor/Guide/Prevent)
                var context = new EventContext
                {
                    Document = document,
                    CurrentRevitVersion = document.Application?.VersionNumber
                };

                bool allowed = EnforceEventProtection("Ze_CADImportPinPrompt", context,
                    $"Imported {importInstanceIds.Count} CAD element(s)");

                if (!allowed) return;

                // Get config for auto-pin setting
                var config = protectionService.GetConfiguration<CADImportPinPromptConfig>(settings.DummyCommandId);
                bool autoPin = config?.AutoPinWithoutPrompt ?? false;

                // Queue pin operation via ExternalEvent (cannot modify doc in DocumentChanged)
                var info = PinAfterEventExternalEventInfo.Instance;
                info.ElementIds = importInstanceIds;
                info.Document = document;
                info.SourceProtection = "Ze_CADImportPinPrompt";
                info.AutoPinWithoutPrompt = autoPin;

                if (_pinAfterEventExternalEvent != null && !_pinAfterEventExternalEvent.IsPending)
                {
                    _pinAfterEventExternalEvent.Raise();
                    _logger?.LogDebug("Raised PinAfterEventExternalEvent for CAD Import Pin Prompt");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in CAD Import Pin Prompt: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Post-action handler for ribbon-triggered Full/Partial Explode.
        /// Ribbon Explode buttons cannot be intercepted via AddInCommandBinding (Revit limitation),
        /// so this catches the operation after the fact via the transaction name.
        /// Runs the protection in post-action mode: Notify/Guide log + audit; Prevent cannot truly block
        /// (the explode already happened) but is recorded as an unauthorized action for the audit trail.
        /// </summary>
        private void CheckCADExplodePostAction(DocumentChangedEventArgs e)
        {
            try
            {
                // If the ribbon swap just dispatched the explode itself, it has already shown
                // the dialog, written audit, and (for context-menu IDs) ran Priority-3 enforcement.
                // Skip this post-action pass to avoid double-auditing the same operation.
                if (BIManage.Revit.RibbonInterception.CADExplodeRibbonSwap.ConsumeSuppressedPostAction())
                {
                    _logger?.LogDebug("CAD Explode (post-action): suppressed (just dispatched by ribbon swap).");
                    return;
                }

                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null) return;

                var settings = protectionService.GetProtectionByDummyCommandId("Ze_CADExplodeProtection");
                if (settings == null || !settings.Enabled) return;

                var document = e.GetDocument();
                if (document == null || !document.IsValidObject) return;

                var registeredModelsRepo = _registeredModelsRepoGetter?.Invoke();
                if (registeredModelsRepo == null) return;
                var cadExplodeModelGuid = Helpers.ModelGuidHelper.GetModelGuid(document, _logger);
                var isCadExplodeRegistered = !string.IsNullOrEmpty(cadExplodeModelGuid) &&
                    registeredModelsRepo.IsModelRegisteredAsync(cadExplodeModelGuid).GetAwaiter().GetResult();
                if (!isCadExplodeRegistered)
                {
                    _logger?.LogInfo($"CAD Explode (post-action) skipped — '{document.Title}' is not a registered model.");
                    return;
                }

                var transactionNames = e.GetTransactionNames();
                var explodeType = transactionNames != null && transactionNames.Count > 0 ? transactionNames[0] : "Explode";

                _logger?.LogInfo($"CAD Explode (post-action): Detected ribbon-triggered {explodeType}");

                var context = new EventContext
                {
                    Document = document,
                    CurrentRevitVersion = document.Application?.VersionNumber
                };

                EnforceEventProtection("Ze_CADExplodeProtection", context, $"Ribbon {explodeType} detected");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in CAD Explode post-action: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Protection 14 - After Revit link insertion, detect RevitLinkInstance elements and prompt to pin.
        /// </summary>
        private void CheckRVTLinkPinPrompt(DocumentChangedEventArgs e)
        {
            try
            {
                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null) return;

                var settings = protectionService.GetProtectionByDummyCommandId("Ze_RVTLinkPinPrompt");
                if (settings == null || !settings.Enabled) return;

                var document = e.GetDocument();
                if (document == null || !document.IsValidObject) return;

                var registeredModelsRepo = _registeredModelsRepoGetter?.Invoke();
                if (registeredModelsRepo == null) return;
                var rvtLinkModelGuid = Helpers.ModelGuidHelper.GetModelGuid(document, _logger);
                var isRvtLinkRegistered = !string.IsNullOrEmpty(rvtLinkModelGuid) &&
                    registeredModelsRepo.IsModelRegisteredAsync(rvtLinkModelGuid).GetAwaiter().GetResult();
                if (!isRvtLinkRegistered)
                {
                    _logger?.LogInfo($"RVT Link Pin Prompt skipped — '{document.Title}' is not a registered model.");
                    return;
                }

                // Filter added elements for RevitLinkInstance
                var addedIds = e.GetAddedElementIds();
                var linkInstanceIds = addedIds
                    .Where(id =>
                    {
                        var el = document.GetElement(id);
                        return el is RevitLinkInstance;
                    })
                    .ToList();

                if (linkInstanceIds.Count == 0) return;

                _logger?.LogInfo($"RVT Link Pin Prompt: Detected {linkInstanceIds.Count} RevitLinkInstance element(s)");

                var context = new EventContext
                {
                    Document = document,
                    CurrentRevitVersion = document.Application?.VersionNumber
                };

                bool allowed = EnforceEventProtection(settings, context,
                    $"Inserted {linkInstanceIds.Count} Revit link(s)");

                bool autoPin;
                if (settings.Mode == InterventionMode.Notify)
                {
                    // No Event Protection window shown — keep the "Pin Elements?" dialog
                    if (!allowed) return;
                    autoPin = false;
                }
                else
                {
                    // Assist / Protect: single window drives pin decision
                    // Confirm/OTP → user takes responsibility → do not pin
                    if (allowed) return;
                    // Cancel → enforce pin silently, no second dialog
                    autoPin = true;
                }

                var info = PinAfterEventExternalEventInfo.Instance;
                info.ElementIds = linkInstanceIds;
                info.Document = document;
                info.SourceProtection = "Ze_RVTLinkPinPrompt";
                info.AutoPinWithoutPrompt = autoPin;

                if (_pinAfterEventExternalEvent != null && !_pinAfterEventExternalEvent.IsPending)
                {
                    _pinAfterEventExternalEvent.Raise();
                    _logger?.LogDebug("Raised PinAfterEventExternalEvent for RVT Link Pin Prompt");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in RVT Link Pin Prompt: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Protection 11 - Stage 1: Track elements added during Copy/Monitor "Copy" transaction.
        /// Stores element IDs for later processing in "Finish Mode".
        /// </summary>
        private void TrackCopyMonitorElements(DocumentChangedEventArgs e)
        {
            try
            {
                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null) return;

                var settings = protectionService.GetProtectionByDummyCommandId("Ze_CopyMonitorPinProtection");
                if (settings == null || !settings.Enabled) return;

                var document = e.GetDocument();
                if (document == null || !document.IsValidObject) return;

                var addedIds = e.GetAddedElementIds();
                if (addedIds.Count == 0) return;

                // Store element IDs keyed by document title (project key)
                var projectKey = document.Title ?? "unknown";
                if (!_copyMonitorTrackedElements.ContainsKey(projectKey))
                {
                    _copyMonitorTrackedElements[projectKey] = new HashSet<long>();
                }

                foreach (var id in addedIds)
                {
                    _copyMonitorTrackedElements[projectKey].Add(id.GetIdValue());
                }

                _logger?.LogDebug($"Copy/Monitor tracking: Stored {addedIds.Count} element IDs for project '{projectKey}' (total: {_copyMonitorTrackedElements[projectKey].Count})");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error tracking Copy/Monitor elements: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Protection 11 - Stage 2: On "Finish Mode" transaction, filter tracked elements
        /// for monitored elements and prompt to pin.
        /// </summary>
        private void CheckCopyMonitorPinProtection(DocumentChangedEventArgs e)
        {
            try
            {
                var protectionService = _eventProtectionServiceGetter?.Invoke();
                if (protectionService == null) return;

                var settings = protectionService.GetProtectionByDummyCommandId("Ze_CopyMonitorPinProtection");
                if (settings == null || !settings.Enabled) return;

                var document = e.GetDocument();
                if (document == null || !document.IsValidObject) return;

                var projectKey = document.Title ?? "unknown";
                if (!_copyMonitorTrackedElements.ContainsKey(projectKey) ||
                    _copyMonitorTrackedElements[projectKey].Count == 0)
                {
                    _logger?.LogDebug("Copy/Monitor Pin Protection: No tracked elements for Finish Mode");
                    return;
                }

                var trackedIds = _copyMonitorTrackedElements[projectKey];

                // Filter for elements that are actually copy/monitored
                var monitoredElementIds = trackedIds
                    .Select(idValue =>
                    {
                        try
                        {
                            var elementId = ElementIdExtensions.CreateElementId(idValue);
                            var element = document.GetElement(elementId);
                            if (element != null && element.IsValidObject)
                            {
                                // Check if element has monitored link element IDs
                                var monitoredIds = element.GetMonitoredLinkElementIds();
                                if (monitoredIds != null && monitoredIds.Count > 0)
                                    return elementId;
                            }
                        }
                        catch { /* Element may not exist anymore */ }
                        return null;
                    })
                    .Where(id => id != null)
                    .ToList();

                // Clear tracking data
                _copyMonitorTrackedElements.Remove(projectKey);

                if (monitoredElementIds.Count == 0)
                {
                    _logger?.LogDebug("Copy/Monitor Pin Protection: No monitored elements found after filtering");
                    return;
                }

                _logger?.LogInfo($"Copy/Monitor Pin Protection: Found {monitoredElementIds.Count} monitored element(s)");

                // Run enforcement (Monitor/Guide/Prevent)
                var context = new EventContext
                {
                    Document = document,
                    CurrentRevitVersion = document.Application?.VersionNumber
                };

                bool allowed = EnforceEventProtection("Ze_CopyMonitorPinProtection", context,
                    $"Copy/Monitor completed with {monitoredElementIds.Count} monitored element(s)");

                if (!allowed) return;

                // Get config for auto-pin setting
                var config = protectionService.GetConfiguration<CopyMonitorPinConfig>(settings.DummyCommandId);
                bool autoPin = config?.AutoPinWithoutPrompt ?? false;

                // Queue pin operation via ExternalEvent
                var info = PinAfterEventExternalEventInfo.Instance;
                info.ElementIds = monitoredElementIds;
                info.Document = document;
                info.SourceProtection = "Ze_CopyMonitorPinProtection";
                info.AutoPinWithoutPrompt = autoPin;

                if (_pinAfterEventExternalEvent != null && !_pinAfterEventExternalEvent.IsPending)
                {
                    _pinAfterEventExternalEvent.Raise();
                    _logger?.LogDebug("Raised PinAfterEventExternalEvent for Copy/Monitor Pin Protection");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in Copy/Monitor Pin Protection: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Clear Copy/Monitor tracking data on undo/abort.
        /// </summary>
        private void ClearCopyMonitorTracking(Document? document)
        {
            try
            {
                if (document == null) return;
                var projectKey = document.Title ?? "unknown";
                if (_copyMonitorTrackedElements.Remove(projectKey))
                {
                    _logger?.LogDebug($"Copy/Monitor tracking cleared for project '{projectKey}' (undo/abort)");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Error clearing Copy/Monitor tracking: {ex.Message}");
            }
        }

        #endregion

        /// <summary>
        /// Lazy instantiation of EventInterventionHandler
        /// </summary>
        private EventInterventionHandler GetEventInterventionHandler()
        {
            return _eventInterventionHandler ??= new EventInterventionHandler(
                _logger,
                _auditRepository,
                _otpRepositoryGetter?.Invoke(),
                _isAdminCheck);
        }

        /// <summary>
        /// Extract year from Revit file format string (e.g., "2021" from "2021").
        /// BasicFileInfo.Format returns the file format version (e.g., "2021").
        /// </summary>
        private static string? ExtractYearFromFormat(string format)
        {
            if (string.IsNullOrEmpty(format)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(format, @"\d{4}");
            return match.Success ? match.Value : null;
        }

        /// <summary>
        /// Check if path is a cloud model (BIM 360, ACC, RSN)
        /// </summary>
        private static bool IsCloudModelPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.StartsWith("BIM 360://", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("ACC://", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("A360://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Check if path is a local drive (C:\, D:\, etc.)
        /// </summary>
        private static bool IsLocalDrivePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':';
        }

        /// <summary>
        /// Detects paths from Revit's External Resources system (IExternalResourceServer).
        /// These paths are NOT local files and NOT standard cloud model URIs — they are
        /// internal resource identifiers used by external resource servers (e.g., Autodesk Docs
        /// shared parameters, keynotes, assembly codes linked via "External Resources" panel).
        ///
        /// Such paths must bypass all file-system checks (File.Exists, BasicFileInfo.Extract)
        /// and protection dialogs, as they are loaded internally by Revit and cannot be
        /// intercepted without breaking the resource resolution pipeline.
        /// </summary>
        private static bool IsNonFileSystemPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            // Already handled by IsCloudModelPath — skip here
            if (IsCloudModelPath(path)) return false;

            // Local drive path or UNC network path — these are real files
            if (IsLocalDrivePath(path)) return false;
            if (path.StartsWith("\\\\", StringComparison.Ordinal)) return false;

            // Everything else is a non-file-system path:
            // - External resource server identifiers (GUIDs, server-relative paths)
            // - HTTP/HTTPS URLs from custom resource servers
            // - Revit internal resource references
            // These cannot be checked with File.Exists or BasicFileInfo.Extract.
            return true;
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;

            UnregisterAllEvents();
            _positionRestorationService?.Dispose();
            lock (_modelGuidCacheLock) { _modelGuidCache.Clear(); }
            _disposed = true;
        }

        #region ModelGuid cache

        // Keep reads off the native ADocument::getModelGUID_ path during event handlers that
        // fire while Revit is mid-STC (DocumentSynchronizingWithCentral). See crash report
        // in docs/ and the field definitions above.
        private void SetCachedModelGuid(Document doc, string modelGuid)
        {
            if (doc == null || string.IsNullOrEmpty(modelGuid)) return;
            lock (_modelGuidCacheLock)
            {
                _modelGuidCache[doc.GetHashCode()] = modelGuid;
            }
        }

        private bool TryGetCachedModelGuid(Document doc, out string modelGuid)
        {
            modelGuid = null;
            if (doc == null) return false;
            lock (_modelGuidCacheLock)
            {
                return _modelGuidCache.TryGetValue(doc.GetHashCode(), out modelGuid)
                    && !string.IsNullOrEmpty(modelGuid);
            }
        }

        private void ClearCachedModelGuid(Document doc)
        {
            if (doc == null) return;
            lock (_modelGuidCacheLock)
            {
                _modelGuidCache.Remove(doc.GetHashCode());
            }
        }

        private async Task CaptureSyncMetricsAsync(Document doc, string syncGuid)
        {
            if (_metricsRepository == null || _metricsCollector == null
                || doc == null || string.IsNullOrEmpty(syncGuid))
            {
                return;
            }
            try
            {
                var sessionId = _revitContext?.SessionId.ToString() ?? Guid.NewGuid().ToString();
                if (!TryGetCachedModelGuid(doc, out var modelGuid))
                {
                    modelGuid = Helpers.ModelGuidHelper.GetModelGuid(doc, _logger);
                }
                var modelPath = Helpers.DocumentInformationHelper.GetNormalizedPath(doc);
                var modelName = doc.Title;
                var capturedBy = doc.Application?.Username ?? Environment.UserName;
                var documentId = doc.GetHashCode().ToString();

                var metrics = _metricsCollector.CollectFastMetrics(doc);

                var captureId = await _metricsRepository.InsertFastMetricsAsync(
                    sessionId: sessionId,
                    documentId: documentId,
                    modelGuid: modelGuid,
                    modelPath: modelPath,
                    modelName: modelName,
                    capturedBy: capturedBy,
                    metrics: metrics,
                    captureTypeValue: "sync",
                    syncGuid: syncGuid);

                _logger?.LogDebug("Captured fast metrics after sync");

                SyncSyncSaveMetricsToApiAsync(captureId, sessionId, documentId, modelGuid,
                    modelPath, modelName, "sync", syncGuid, capturedBy, metrics);
            }
            catch (Exception metricsEx)
            {
                _logger?.LogWarning($"Failed to capture metrics after sync: {metricsEx.Message}");
                // Fail-open — metrics failure must not break sync tracking.
            }
        }

        #endregion

        /// <summary>
        /// Tracks document opening lifecycle for timing measurements
        /// </summary>
        private class DocumentOpeningTracker
        {
            public DateTime OpeningStartedAt { get; set; }
            public DateTime? DocumentOpenedAt { get; set; }
            public DateTime? ViewActivatedAt { get; set; }
            public bool ViewActivationHandled { get; set; }
            public string PathKey { get; set; }
        }
    }
}
#endregion