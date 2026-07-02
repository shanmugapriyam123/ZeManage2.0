using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Core.Evidence;
using BIManage.Core.Features;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Context;
using BIManage.Revit.Events;
using BIManage.Revit.Productivity;
using BIManage.Core.Protection.Models;
using BIManage.Core.Rules.Models;
using BIManage.Revit.Helpers;
using BIManage.Revit.Protection;
using BIManage.Revit.Commands.Bindings;

namespace BIManage.Revit.Commands
{
    /// <summary>
    ///     Command interception service implementing the full individual-binding pattern (Option B)
    ///     Monitors core commands with dual binding strategy:
    ///
    ///     INDIVIDUAL BINDINGS (specialized handling):
    ///     - Move/Rotate/Mirror: BeforeExecuted ONLY - pre-execution cancellation via rule evaluation
    ///     - Pin/Unpin: Executed event - post-execution protection workflow
    ///       - Pin: Shows admin config dialog, saves protection metadata to ExtensibleStorage
    ///       - Unpin: Evaluates protection modes (Monitor/Guide/Prevent), can re-pin if blocked
    ///     - Delete: BOTH events - rule evaluation in BeforeExecuted, manual deletion in Executed
    ///
    ///     CENTRALIZED HANDLER (generic tracking):
    ///     - All other commands: BeforeExecuted ONLY - rule evaluation and cancellation
    ///     - Revit auto-executes commands if not cancelled via e.Cancel = true
    ///
    ///     Deduplication ensures each command only gets ONE binding via GetIndividuallyBoundCommandIds().
    /// </summary>
    public class CommandInterceptionService : ICommandInterceptionService
    {
        private readonly IFeatureToggleService _featureToggleService;
        private readonly IProductivityTracker _productivityTracker;
        private readonly ILogger? _logger;
        private readonly Dictionary<RevitCommandId, AddInCommandBinding> _commandBindings;
        private volatile IRuleCommandInterceptor? _ruleInterceptor; // Optional rule-based interceptor (volatile for thread safety)

        // Dependencies for Pin/Unpin individual bindings (Option B - full individual-binding pattern)
        private readonly Func<bool> _isAdminCheck;
        private readonly BIManage.Data.SQLite.OtpRepository? _otpRepository;
        private readonly BIManage.Data.SQLite.AuditRepository? _auditRepository;
        private readonly BIManage.Data.SQLite.PinProtectionRepository? _pinProtectionRepository;
        private readonly Func<IScreenshotService?>? _screenshotServiceGetter;
        private readonly Func<IRevitContext?>? _revitContextGetter;
        private readonly BIManage.Revit.PinProtection.UnpinnedElementTracker? _unpinnedElementTracker;
        private readonly Func<CommandProtectionBinding?>? _commandProtectionGetter;
        private readonly Func<EventRegistry.PositionRestorationService?>? _positionRestorationGetter;
        private readonly Func<BIManage.Data.SQLite.RegisteredModelsRepository?>? _registeredModelsRepoGetter;
        private readonly Func<BIManage.Infrastructure.Api.PinProtectionSyncService?>? _pinProtectionSyncGetter;
        private readonly Func<IEventProtectionService?>? _eventProtectionServiceGetter;
        private readonly Func<BIManage.Data.SQLite.EvidenceRepository?>? _evidenceRepositoryGetter;
        private readonly Func<BIManage.Revit.SyncTrafficControl.SyncTrafficControlService?>? _syncTrafficControlGetter;
        private readonly Func<BIManage.Infrastructure.SignalR.Events.ISignalREventBus?>? _signalREventBusGetter;

        // Individual command bindings for Move/Rotate/Mirror
        private MoveCommandBinding? _moveBinding;
        private RotateCommandBinding? _rotateBinding;
        private MirrorCommandBinding? _mirrorPickAxisBinding;
        private MirrorCommandBinding? _mirrorDrawAxisBinding;

        // Individual command bindings for Pin/Unpin (Option B - full individual-binding pattern)
        private BIManage.Revit.Commands.Bindings.PinCommandBinding? _pinBinding;
        private BIManage.Revit.Commands.Bindings.UnpinCommandBinding? _unpinBinding;

        // Individual binding for Delete command (explicit deletion operation)
        private DeleteCommandBinding? _deleteBinding;

        // Individual binding for Cut to Clipboard — ensures rule interceptor fires
        private CutCommandBinding? _cutBinding;

        // Individual binding for Copy — pin protection alert on pinned/protected elements
        private CopyCommandBinding? _copyBinding;

        // Individual binding for Offset — pin protection alert on pinned/protected elements
        private OffsetCommandBinding? _offsetBinding;

        // Individual binding for Group/Ungroup/EditGroup (protect pinned elements)
        private GroupCommandBinding? _groupCommandBinding;

        // Individual binding for Assembly commands (protect pinned elements)
        private AssemblyCommandBinding? _assemblyCommandBinding;

        // Sync traffic control — gates sync upstream via command binding
        private SyncCommandBinding? _syncBinding;

        // Idle-time accumulators on document_sessions: post-open dialog brackets.
        // Worksets / Manage Links binding fields removed — see RegisterIndividualBindings
        // for the disabled-binding note. Repository + sync getter retained for reuse.
        private readonly BIManage.Data.SQLite.SessionRepository? _sessionRepository;
        private readonly Func<BIManage.Infrastructure.Api.ModelSessionSyncService?>? _modelSessionSyncGetter;

        public CommandInterceptionService(
            IFeatureToggleService featureToggleService,
            IProductivityTracker productivityTracker,
            ILogger? logger,
            Func<bool> isAdminCheck,
            BIManage.Data.SQLite.OtpRepository? otpRepository,
            BIManage.Data.SQLite.AuditRepository? auditRepository,
            BIManage.Data.SQLite.PinProtectionRepository? pinProtectionRepository = null,
            Func<IScreenshotService?>? screenshotServiceGetter = null,
            Func<IRevitContext?>? revitContextGetter = null,
            BIManage.Revit.PinProtection.UnpinnedElementTracker? unpinnedElementTracker = null,
            Func<CommandProtectionBinding?>? commandProtectionGetter = null,
            Func<EventRegistry.PositionRestorationService?>? positionRestorationGetter = null,
            Func<BIManage.Data.SQLite.RegisteredModelsRepository?>? registeredModelsRepoGetter = null,
            Func<BIManage.Infrastructure.Api.PinProtectionSyncService?>? pinProtectionSyncGetter = null,
            Func<IEventProtectionService?>? eventProtectionServiceGetter = null,
            Func<BIManage.Data.SQLite.EvidenceRepository?>? evidenceRepositoryGetter = null,
            Func<BIManage.Revit.SyncTrafficControl.SyncTrafficControlService?>? syncTrafficControlGetter = null,
            Func<BIManage.Infrastructure.SignalR.Events.ISignalREventBus?>? signalREventBusGetter = null,
            BIManage.Data.SQLite.SessionRepository? sessionRepository = null,
            Func<BIManage.Infrastructure.Api.ModelSessionSyncService?>? modelSessionSyncGetter = null)
        {
            _featureToggleService = featureToggleService ?? throw new ArgumentNullException(nameof(featureToggleService));
            _productivityTracker = productivityTracker ?? throw new ArgumentNullException(nameof(productivityTracker));
            _logger = logger;
            _isAdminCheck = isAdminCheck ?? throw new ArgumentNullException(nameof(isAdminCheck));
            _otpRepository = otpRepository;
            _auditRepository = auditRepository;
            _pinProtectionRepository = pinProtectionRepository;
            _screenshotServiceGetter = screenshotServiceGetter;
            _revitContextGetter = revitContextGetter;
            _unpinnedElementTracker = unpinnedElementTracker;
            _commandProtectionGetter = commandProtectionGetter;
            _positionRestorationGetter = positionRestorationGetter;
            _registeredModelsRepoGetter = registeredModelsRepoGetter;
            _pinProtectionSyncGetter = pinProtectionSyncGetter;
            _eventProtectionServiceGetter = eventProtectionServiceGetter;
            _evidenceRepositoryGetter = evidenceRepositoryGetter;
            _syncTrafficControlGetter = syncTrafficControlGetter;
            _signalREventBusGetter = signalREventBusGetter;
            _sessionRepository = sessionRepository;
            _modelSessionSyncGetter = modelSessionSyncGetter;
            _commandBindings = new Dictionary<RevitCommandId, AddInCommandBinding>();
        }

        /// <summary>
        /// Sets the rule command interceptor to be called before legacy protection logic
        /// </summary>
        public void SetRuleInterceptor(IRuleCommandInterceptor ruleInterceptor)
        {
            _ruleInterceptor = ruleInterceptor;
            _logger?.LogInfo("Rule-based command interceptor attached");
        }

        public void RegisterCommandBindings(UIApplication uiApplication)
        {
            if (uiApplication == null) throw new ArgumentNullException(nameof(uiApplication));

            try
            {
                // CRITICAL: Force cleanup of any stale bindings from previous DLL loads
                // This prevents old Executed handlers from persisting across rebuilds
                ForceCleanupStaleBindings(uiApplication);

                // Register individual bindings for Move/Rotate/Mirror FIRST
                // These commands need direct rule interception without guard clauses
                RegisterIndividualBindings(uiApplication);

                // Get version-aware core commands from mapper, deduped by .Id.
                // GetCoreCommands intentionally probes the same underlying Revit command via
                // both string-id lookup and PostableCommand lookup as a defensive measure
                // (e.g. ID_EDIT_TYPE + PostableCommand.TypeProperties both resolve to id=32790;
                // ID_EDIT_GROUP + PostableCommand.CreateGroup both resolve to id=33305). Without
                // this dedupe, the loop below calls CreateAddInCommandBinding + `BeforeExecuted +=`
                // twice for the same command id, producing two subscriptions that Revit's
                // AddInManager cannot fully resolve to a single host — surfacing later as
                // "Cannot find the host add-in which added this handler" and the user-visible
                // "Revit encountered a serious error" dialog when a contextual ribbon tab is built.
                var coreCommands = RevitCommandMapper.GetCoreCommands(uiApplication)
                    .GroupBy(c => (int)c.Id)
                    .Select(g => g.First())
                    .ToList();

                // Get IDs of commands with individual bindings to skip them in the loop
                var individuallyBoundCommands = GetIndividuallyBoundCommandIds(uiApplication);

                foreach (var commandId in coreCommands)
                {
                    // Skip commands that have dedicated individual bindings
                    if (individuallyBoundCommands.Contains((int)commandId.Id))
                    {
                        _logger?.LogDebug($"Skipping {commandId.Name} - has individual binding");
                        continue;
                    }

                    try
                    {
                        var binding = uiApplication.CreateAddInCommandBinding(commandId);
                        binding.BeforeExecuted += OnBeforeCommandExecuted;
                        LogSubscriptionEvent("REGISTER-CORE", commandId, uiApplication);
                        // REMOVED: binding.Executed += OnCommandExecuted;
                        // Revit will auto-execute if not cancelled via e.Cancel = true
                        // Individual bindings (Pin, Unpin, Delete) handle Executed event separately

                        _commandBindings[commandId] = binding;

                        var displayName = RevitCommandMapper.GetCommandDisplayName(commandId);
                        _logger?.LogDebug($"Registered command binding: {displayName} ({commandId.Name})");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning($"Failed to register command {commandId.Name}: {ex.Message}");
                    }
                }

                _logger?.LogInfo($"Command interception service registered {_commandBindings.Count} generic bindings + individual bindings for Move/Rotate/Mirror");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to register command bindings: {ex.Message}", ex);
            }
        }

        public void UnregisterCommandBindings()
        {
            try
            {
                // Unregister individual bindings first
                // BeforeExecuted bindings
                _moveBinding?.Unregister();
                _rotateBinding?.Unregister();
                _mirrorPickAxisBinding?.Unregister();
                _mirrorDrawAxisBinding?.Unregister();

                // Executed bindings (Pin Protection - Option B)
                _pinBinding?.Unregister();
                _unpinBinding?.Unregister();

                // Both BeforeExecuted and Executed bindings
                _deleteBinding?.Unregister();
                _cutBinding?.Unregister();
                _copyBinding?.Unregister();
                _offsetBinding?.Unregister();

                // Group command bindings
                _groupCommandBinding?.Unregister();
                _assemblyCommandBinding?.Unregister();

                // Sync traffic control binding
                _syncBinding?.Unregister();

                // Unregister centralized bindings
                foreach (var kvp in _commandBindings)
                {
                    try
                    {
                        kvp.Value.BeforeExecuted -= OnBeforeCommandExecuted;
                        LogSubscriptionEvent("UNREGISTER", kvp.Key);
                        // REMOVED: kvp.Value.Executed -= OnCommandExecuted;
                        // Only BeforeExecuted bound for centralized commands
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning($"Error unregistering command binding: {ex.Message}");
                    }
                }

                _commandBindings.Clear();
                _logger?.LogInfo("Command bindings unregistered (including individual bindings for Move/Rotate/Mirror/Pin/Unpin)");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to unregister command bindings: {ex.Message}", ex);
            }
        }

        public bool IsCommandMonitored(RevitCommandId commandId)
        {
            if (commandId == null) return false;
            var name = commandId.Name;
            foreach (var key in _commandBindings.Keys)
            {
                if (string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void OnBeforeCommandExecuted(object sender, BeforeExecutedEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused)
                    return;

                // Record activity for productivity tracking
                _productivityTracker.RecordActivity();

                var commandId = e.CommandId;
                if (commandId == null)
                    return;

                // Skip edit mode commands that require Revit's interactive UI
                if (IsEditModeCommand(commandId))
                {
                    _logger?.LogDebug($"Skipping edit mode command: {commandId.Name}");
                    return;
                }

                var context = new CommandExecutionContext(commandId, commandId.Name, isBeforeExecution: true);
                _logger?.LogDebug($"BeforeExecuted: {context}");

                // PRIORITY 1: Check CommandProtectionBinding for command-level settings (database-driven).
                // Fires only when the user has registered a Command Protection for this command —
                // shows the "Command Protection" dialog. If no Command Protection exists for this
                // command, ProcessCommandBeforeExecution is a no-op and the priority chain falls
                // through to Priority 3 (Event Protection), which shows "Event Restriction".
                // This preserves the user-intent: dialog title matches whichever protection type
                // was actually configured.
                var commandProtection = _commandProtectionGetter?.Invoke();
                if (commandProtection != null)
                {
                    commandProtection.ProcessCommandBeforeExecution(e);
                    if (e.Cancel)
                    {
                        _logger?.LogDebug("Command cancelled by CommandProtectionBinding");
                        return;
                    }
                }

                // PRIORITY 2: Try rule-based interception (element-aware evaluation)
                if (_ruleInterceptor != null)
                {
                    _ruleInterceptor.OnBeforeExecuted(sender, e);

                    if (e.Cancel)
                    {
                        _logger?.LogDebug("Command cancelled by rule interceptor");
                        return;
                    }
                }

                // PRIORITY 3: Event protection check for command-mapped protections
                if (!_featureToggleService.IsFeatureEnabled("EventProtection"))
                {
                    _logger?.LogInfo($"[EventProtection] Feature toggle 'EventProtection' is OFF — skipping for {commandId.Name}.");
                }
                else
                {
                    var dummyCommandIds = GetEventProtectionDummyCommandIds(commandId.Name);

                    // If this is a Save As command, suppress SyncConflictDialogInterceptor
                    // immediately — before the file picker opens. Revit calls acquireFileLock()
                    // during Save As; if the target file is locked by another user's sync the
                    // interceptor's IsCentralBusyTitle path fires and shows the Sync Queue
                    // dialog, which is wrong context (user is saving, not syncing).
                    // DocumentSaved / OnDocumentSavingAs completion clears the flag.
                    if (IsSaveAsCommand(commandId.Name))
                        SyncTrafficControl.SyncConflictDialogInterceptor.NotifySaveAsStarted();

                    if (dummyCommandIds.Length == 0)
                    {
                        // Command isn't mapped to an event protection — not a bug, just no
                        // applicable rule. Logged at Debug to avoid spam on every command.
                        _logger?.LogDebug($"[EventProtection] {commandId.Name} not mapped to any event protection key.");
                    }
                    else
                    {
                        var protectionService = _eventProtectionServiceGetter?.Invoke();
                        if (protectionService == null)
                        {
                            // Promoted to Info: this is a real silent failure mode that the user
                            // needs to see in the standard log to diagnose "Import CAD doesn't fire".
                            _logger?.LogInfo($"[EventProtection] No protection service available for {commandId.Name} — skipping.");
                        }
                        else if (!protectionService.IsProtectionEnabled)
                        {
                            _logger?.LogInfo($"[EventProtection] Service disabled (IsProtectionEnabled=false) for {commandId.Name} — skipping.");
                        }
                        else
                        {
                            // Defensive: refresh the in-memory cache from DB before reading
                            // the setting. Without this, a freshly-toggled enabled flag may not
                            // be visible if the dialog hadn't yet triggered LoadSettingsFromDatabase.
                            try { protectionService.RefreshSettings(); } catch (Exception refreshEx) { _logger?.LogWarning($"[EventProtection] RefreshSettings failed for {commandId.Name}: {refreshEx.Message}"); }

                            foreach (var dummyCommandId in dummyCommandIds)
                            {
                                var settings = protectionService.GetProtectionByDummyCommandId(dummyCommandId);
                                if (settings == null)
                                {
                                    // Promoted to Info — this is the most common silent skip when the
                                    // local DB row exists but isn't being loaded into the cache (the
                                    // exact bug the v2.10 SQL widening was meant to fix). If you see
                                    // this line for Ze_CADImportProtection while the dialog clearly
                                    // shows the row enabled, the cache-load query is filtering it out.
                                    _logger?.LogInfo($"[EventProtection] No settings found for {dummyCommandId} (cache miss) — skipping. Click checked: {commandId.Name}.");
                                    continue;
                                }
                                else if (!settings.Enabled)
                                {
                                    // Promoted to Info — silent enabled=false on a row the user can
                                    // see toggled on in the dialog points at the wrong row winning the
                                    // scope-precedence dedup (e.g. a stale orphan override outranks
                                    // the just-edited company-level row). Log includes the row's
                                    // identifying fields so we can spot the wrong-row case.
                                    _logger?.LogInfo($"[EventProtection] Settings for {dummyCommandId} are DISABLED (Enabled=false) — skipping. Loaded row: id={settings.Id}, scope={(string.IsNullOrEmpty(settings.ModelGuid) ? (settings.IsCompanyLevel && string.IsNullOrEmpty(settings.ProjectId) ? "Company" : "Project") : "Model")}, projectId={settings.ProjectId ?? "<null>"}, modifiedAt={settings.ModifiedAt:o}.");
                                    continue;
                                }

                                // These commands require an active document to evaluate
                                if (e.ActiveDocument == null)
                                {
                                    _logger?.LogInfo($"[EventProtection] Skipping {commandId.Name}: no active document.");
                                    return;
                                }

                                if (string.Equals(dummyCommandId, "Ze_CADImportProtection", StringComparison.Ordinal) ||
                                    string.Equals(dummyCommandId, "Ze_DocumentExportingProtection", StringComparison.Ordinal) ||
                                    string.Equals(dummyCommandId, "Ze_DocumentPrintingProtection", StringComparison.Ordinal) ||
                                    string.Equals(dummyCommandId, "Ze_TransferProjectStandardsProtection", StringComparison.Ordinal))
                                {
                                    var registeredModelsRepo = _registeredModelsRepoGetter?.Invoke();
                                    if (registeredModelsRepo == null)
                                    {
                                        _logger?.LogInfo($"[EventProtection] {dummyCommandId} skipped — registered models repo unavailable.");
                                        continue;
                                    }
                                    var commandModelGuid = ModelGuidHelper.GetModelGuid(e.ActiveDocument, _logger);
                                    var commandModelIsRegistered = !string.IsNullOrEmpty(commandModelGuid) &&
                                        registeredModelsRepo.IsModelRegisteredAsync(commandModelGuid).GetAwaiter().GetResult();
                                    if (!commandModelIsRegistered)
                                    {
                                        _logger?.LogInfo($"[EventProtection] {dummyCommandId} skipped — '{e.ActiveDocument.Title}' is not a registered model.");
                                        continue;
                                    }
                                }

                                _logger?.LogInfo($"[EventProtection] Triggered for command {commandId.Name}: {dummyCommandId} (mode={settings.Mode}, requireComment={settings.RequireComment})");

                                var handler = new EventInterventionHandler(_logger, _auditRepository, _otpRepository, _isAdminCheck);
                                var eventContext = new EventContext
                                {
                                    Document = e.ActiveDocument,
                                    CurrentRevitVersion = e.ActiveDocument.Application?.VersionNumber
                                };

                                // Generate audit log ID (GUID PK) at the start of the protection flow
                                var auditLogId = Guid.NewGuid().ToString();

                                // Capture before screenshot if configured
                                if (settings.CaptureBeforeScreenshot)
                                {
                                    CaptureCommandEventScreenshot(settings, "before", auditLogId);
                                }

                                var result = handler.ProcessIntervention(settings, eventContext);

                                // Audit logging
                                LogCommandEventAuditEntry(settings, e.ActiveDocument, result, auditLogId);

                                // Capture after screenshot if allowed and configured
                                if (result.Allowed && settings.CaptureAfterScreenshot)
                                {
                                    CaptureCommandEventScreenshot(settings, "after", auditLogId);
                                }

                                if (!result.Allowed)
                                {
                                    if (e.Cancellable)
                                    {
                                        e.Cancel = true;
                                        _logger?.LogWarning($"Command blocked by event protection: {commandId.Name} ({dummyCommandId})");
                                    }
                                    return;
                                }

                                // Allowed-flow follow-up: when this Priority-3 enforcement targeted
                                // the context-menu Explode command, suppress the post-action handler
                                // (CheckCADExplodePostAction). Otherwise the user sees the protection
                                // dialog twice — once here, once on the resulting "Full Explode" /
                                // "Partial Explode" transaction. The ribbon swap path uses the same
                                // gate, so both upstream enforcers route through one mechanism.
                                if (string.Equals(dummyCommandId, "Ze_CADExplodeProtection", StringComparison.Ordinal))
                                {
                                    BIManage.Revit.RibbonInterception.CADExplodeAuditGate.Suppress();
                                }

                                // Same pattern for printing: CommandInterceptionService fires on
                                // BeforeExecuted (Ctrl+P), then Revit fires DocumentPrinting after
                                // the user clicks OK in the print dialog. Suppress the second pass
                                // so the user only sees one protection dialog per print action.
                                if (string.Equals(dummyCommandId, "Ze_DocumentPrintingProtection", StringComparison.Ordinal))
                                {
                                    BIManage.Revit.Protection.PrintProtectionGate.Suppress();
                                }
                            }
                        }
                    }
                }

                // Priority chain: CommandProtectionBinding → RuleCommandInterceptor → EventProtection
                // Same order as individual bindings (Delete, Pin, Unpin)
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnBeforeCommandExecuted handler: {ex.Message}", ex);
            }
        }


        /// <summary>
        /// Force cleanup of any stale bindings from previous DLL loads
        /// This is CRITICAL to prevent old Executed handlers from persisting
        /// </summary>
        private void ForceCleanupStaleBindings(UIApplication uiApplication)
        {
            try
            {
                _logger?.LogWarning("FORCING CLEANUP OF STALE BINDINGS (defensive against DLL reload issues)");

                // Dedupe by .Id — see comment in RegisterCommandBindings for the rationale.
                var coreCommands = RevitCommandMapper.GetCoreCommands(uiApplication)
                    .GroupBy(c => (int)c.Id)
                    .Select(g => g.First())
                    .ToList();
                int cleanedCount = 0;

                foreach (var commandId in coreCommands)
                {
                    try
                    {
                        // Get the binding object (may have stale handlers attached)
                        var binding = uiApplication.CreateAddInCommandBinding(commandId);

                        // FORCE REMOVE any potential stale handlers
                        // These -= operations are defensive - they won't error if handler not attached
                        binding.BeforeExecuted -= OnBeforeCommandExecuted;
                        LogSubscriptionEvent("STALE-CLEANUP", commandId, uiApplication);

                        cleanedCount++;
                    }
                    catch (Exception ex)
                    {
                        // Ignore errors - binding might not exist yet
                        _logger?.LogDebug($"Cleanup attempt for {commandId.Name}: {ex.Message}");
                    }
                }

                _logger?.LogWarning($"Stale binding cleanup complete: attempted cleanup on {cleanedCount} commands");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error during stale binding cleanup: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Register individual bindings for Move/Rotate/Mirror/Pin/Unpin
        /// Full individual-binding pattern (Option B):
        /// - Individual bindings (BeforeExecuted): Move, Rotate, Mirror - pre-execution cancellation
        /// - Individual bindings (Executed): Pin, Unpin - post-execution protection workflow
        ///
        /// Pin/Unpin use individual bindings with Executed event because:
        /// - Pin: Element must be pinned first, then admin configures protection (two-step)
        /// - Unpin: Element attempts unpin, then protection mode evaluated, can re-pin if blocked
        /// </summary>
        private void RegisterIndividualBindings(UIApplication uiApplication)
        {
            // BeforeExecuted bindings (pre-execution cancellation)
            // Move command - with command protection integration
            _moveBinding = new MoveCommandBinding(uiApplication, _logger, _ruleInterceptor, _commandProtectionGetter, _positionRestorationGetter);
            _moveBinding.RegisterWithBeforeExecute();

            // Rotate command - with command protection integration
            _rotateBinding = new RotateCommandBinding(uiApplication, _logger, _ruleInterceptor, _commandProtectionGetter);
            _rotateBinding.RegisterWithBeforeExecute();

            // Mirror Pick Axis - with command protection integration
            _mirrorPickAxisBinding = new MirrorCommandBinding(
                uiApplication,
                _logger,
                _ruleInterceptor,
                PostableCommand.MirrorPickAxis,
                _commandProtectionGetter,
                _eventProtectionServiceGetter,
                () => new BIManage.Revit.Protection.EventInterventionHandler(_logger, _auditRepository, _otpRepository, _isAdminCheck),
                () => _auditRepository,
                _registeredModelsRepoGetter
            );
            _mirrorPickAxisBinding.RegisterWithBeforeExecute();

            // Mirror Draw Axis - with command protection integration
            _mirrorDrawAxisBinding = new MirrorCommandBinding(
                uiApplication,
                _logger,
                _ruleInterceptor,
                PostableCommand.MirrorDrawAxis,
                _commandProtectionGetter,
                _eventProtectionServiceGetter,
                () => new BIManage.Revit.Protection.EventInterventionHandler(_logger, _auditRepository, _otpRepository, _isAdminCheck),
                () => _auditRepository,
                _registeredModelsRepoGetter
            );
            _mirrorDrawAxisBinding.RegisterWithBeforeExecute();

            // Dual-event bindings (BeforeExecuted for command protection, Executed for pin protection)
            // Pin command - shows admin config dialog, stores protection metadata
            _pinBinding = new BIManage.Revit.Commands.Bindings.PinCommandBinding(
                uiApplication,
                _logger,
                _isAdminCheck,
                _pinProtectionRepository,
                _commandProtectionGetter,
                _revitContextGetter,
                _registeredModelsRepoGetter,
                _pinProtectionSyncGetter,
                _auditRepository,
                _screenshotServiceGetter,
                _evidenceRepositoryGetter,
                _ruleInterceptor);  // Model registration check + session_id + API sync + audit + evidence + rule management
            _pinBinding.Register();

            // Unpin command - evaluates protection modes, OTP authorization
            _unpinBinding = new BIManage.Revit.Commands.Bindings.UnpinCommandBinding(
                uiApplication,
                _logger,
                _isAdminCheck,
                _otpRepository,
                _auditRepository,
                _pinProtectionRepository,
                _screenshotServiceGetter,
                _revitContextGetter,
                _unpinnedElementTracker,
                _commandProtectionGetter,
                _pinProtectionSyncGetter,
                _ruleInterceptor);  // Command protection + API sync + rule evaluation
            _unpinBinding.Register();

            // Delete command - Explicit deletion operation (BeforeExecuted for rules, Executed for actual deletion)
            // Also integrates with CommandProtectionBinding for dynamic command protection settings
            _deleteBinding = new DeleteCommandBinding(
                uiApplication,
                _logger,
                _ruleInterceptor,
                _commandProtectionGetter);
            _deleteBinding.Register();

            // Cut to Clipboard — dedicated binding ensures rule interceptor fires for Cut
            _cutBinding = new BIManage.Revit.Commands.Bindings.CutCommandBinding(
                uiApplication,
                _logger,
                _ruleInterceptor,
                _commandProtectionGetter);
            _cutBinding.RegisterWithBeforeExecute();

            // Copy — pin protection alert on pinned/protected elements
            _copyBinding = new CopyCommandBinding(
                uiApplication,
                _logger,
                _ruleInterceptor,
                _commandProtectionGetter);
            _copyBinding.RegisterWithBeforeExecute();

            // Offset — pin protection alert on pinned/protected elements
            _offsetBinding = new OffsetCommandBinding(
                uiApplication,
                _logger,
                _ruleInterceptor,
                _commandProtectionGetter);
            _offsetBinding.RegisterWithBeforeExecute();

            // Group commands — hard block when pinned elements are involved
            _groupCommandBinding = new GroupCommandBinding(
                uiApplication,
                _logger,
                _ruleInterceptor,
                _commandProtectionGetter);
            _groupCommandBinding.RegisterWithBeforeExecute();

            // Assembly commands — hard block when pinned elements are involved
            _assemblyCommandBinding = new AssemblyCommandBinding(
                uiApplication,
                _logger,
                _ruleInterceptor,
                _commandProtectionGetter);
            _assemblyCommandBinding.RegisterWithBeforeExecute();

            // Sync traffic control — gates sync upstream (never cancel mid-sync)
            _syncBinding = new SyncCommandBinding(
                uiApplication,
                _logger,
                _syncTrafficControlGetter,
                _signalREventBusGetter,
                _revitContextGetter,
                _commandProtectionGetter,
                _ruleInterceptor);
            _syncBinding.RegisterWithBeforeExecute();

            // Post-open idle accumulators: time spent on Worksets / Manage Links dialogs.
            // DISABLED — CreateAddInCommandBinding for these modal-dialog PostableCommands
            // interferes with Revit's internal dispatch and prevents the dialog from opening.
            // Binding classes + DB columns + sync endpoints are kept in place; revisit with a
            // different timing mechanism (e.g. DialogBoxShowing event) before re-enabling.

            _logger?.LogInfo("Individual command bindings registered for Move/Rotate/Mirror/Pin/Unpin/Delete/Group/Sync (full individual-binding pattern)");
        }

        /// <summary>
        /// Get command IDs that have individual bindings to avoid duplicate registration
        /// These commands will be SKIPPED in the centralized binding loop
        /// to prevent duplicate binding registration
        /// </summary>
        private HashSet<int> GetIndividuallyBoundCommandIds(UIApplication uiApplication)
        {
            var ids = new HashSet<int>();

            // BeforeExecuted bindings
            var moveId = RevitCommandId.LookupPostableCommandId(PostableCommand.Move);
            var rotateId = RevitCommandId.LookupPostableCommandId(PostableCommand.Rotate);
            var mirrorPickId = RevitCommandId.LookupPostableCommandId(PostableCommand.MirrorPickAxis);
            var mirrorDrawId = RevitCommandId.LookupPostableCommandId(PostableCommand.MirrorDrawAxis);

            if (moveId != null) ids.Add((int)moveId.Id);
            if (rotateId != null) ids.Add((int)rotateId.Id);
            if (mirrorPickId != null) ids.Add((int)mirrorPickId.Id);
            if (mirrorDrawId != null) ids.Add((int)mirrorDrawId.Id);

            // Executed bindings (Pin Protection - Option B)
            var pinId = RevitCommandId.LookupPostableCommandId(PostableCommand.Pin);
            var unpinId = RevitCommandId.LookupPostableCommandId(PostableCommand.Unpin);

            if (pinId != null) ids.Add((int)pinId.Id);
            if (unpinId != null) ids.Add((int)unpinId.Id);

            // Both BeforeExecuted and Executed bindings (Delete - explicit deletion)
            var deleteId = RevitCommandId.LookupPostableCommandId(PostableCommand.Delete);
            if (deleteId != null) ids.Add((int)deleteId.Id);

            // Cut to Clipboard — has dedicated CutCommandBinding (BeforeExecuted only)
            var cutId = RevitCommandId.LookupCommandId("ID_EDIT_CUT")
                     ?? RevitCommandId.LookupPostableCommandId(PostableCommand.CutToClipboard);
            if (cutId != null) ids.Add((int)cutId.Id);

            // Copy — has dedicated CopyCommandBinding (BeforeExecuted only)
            var copyId = RevitCommandId.LookupPostableCommandId(PostableCommand.Copy);
            if (copyId != null) ids.Add((int)copyId.Id);

            // Offset — has dedicated OffsetCommandBinding (BeforeExecuted only)
            var offsetId = RevitCommandId.LookupPostableCommandId(PostableCommand.Offset);
            if (offsetId != null) ids.Add((int)offsetId.Id);

            // Group command bindings (BeforeExecuted — pin protection block)
            // PostableCommand enum does not include Group/Ungroup/EditGroup; use integer IDs directly
            ids.Add(PostableCommands.Group);      // 33305
            ids.Add(PostableCommands.Ungroup);    // 33091
            ids.Add(PostableCommands.EditGroup);  // 32854

            // Assembly command bindings (BeforeExecuted — pin protection block)
            try
            {
                var createAssemblyId = RevitCommandId.LookupPostableCommandId(PostableCommand.CreateAssembly);
                if (createAssemblyId != null) ids.Add((int)createAssemblyId.Id);
            }
            catch { /* unavailable in this Revit version */ }

            // Idle accumulator bindings (Worksets / Manage Links) are currently disabled
            // because the AddInCommandBinding interferes with the modal dialogs. No skip
            // needed — re-add when the bindings are re-enabled with a different mechanism.

            return ids;
        }

        /// <summary>
        /// Checks if command is an edit mode command that requires Revit's interactive UI
        /// These commands cannot be manually executed and must be allowed to run normally
        /// </summary>
        private bool IsEditModeCommand(RevitCommandId commandId)
        {
            var commandName = commandId.Name;

            // Only skip commands that TRULY require interactive editing context
            // Note: Move/Rotate are standard modification commands intercepted for protection -
            // they should NOT be skipped
            return commandName.Equals("ID_EDIT_PROFILE", StringComparison.OrdinalIgnoreCase) ||
                   commandName.Equals("ID_EDIT_BOUNDARY", StringComparison.OrdinalIgnoreCase) ||
                   commandName.Equals("ID_EDIT_PATH", StringComparison.OrdinalIgnoreCase) ||
                   commandName.Equals("ID_EDIT_SCALE", StringComparison.OrdinalIgnoreCase) ||
                   commandName.Equals("ID_EDIT_RESIZE", StringComparison.OrdinalIgnoreCase);

            // REMOVED: ID_EDIT_MOVE, ID_EDIT_ROTATE
            // These are modification commands, not edit mode commands
            // They should reach rule evaluation for protection (Monitor/Guide/Prevent modes)
        }

        /// <summary>
        /// Register additional command bindings for commands referenced in rules
        /// but not already bound by GetCoreCommands() or individual bindings.
        /// Called after rules are finalized to ensure all rule-referenced commands are interceptable.
        /// </summary>
        public void RegisterRuleCommandBindings(UIApplication uiApplication, IEnumerable<(int CommandId, string CommandName)> ruleCommands)
        {
            if (uiApplication == null) return;

            var individuallyBound = GetIndividuallyBoundCommandIds(uiApplication);
            var alreadyBound = new HashSet<int>();
            foreach (var kvp in _commandBindings)
            {
                try { alreadyBound.Add((int)kvp.Key.Id); }
                catch { /* skip if Id access fails */ }
            }

            int addedCount = 0;

            foreach (var (commandId, commandName) in ruleCommands)
            {
                if (commandId <= 0) continue;

                // Skip if already bound
                if (individuallyBound.Contains(commandId) || alreadyBound.Contains(commandId))
                    continue;

                try
                {
                    var revitCommandId = TryLookupCommandId(commandId);
                    if (revitCommandId == null)
                    {
                        _logger?.LogDebug($"Cannot find RevitCommandId for rule command {commandName} ({commandId})");
                        continue;
                    }

                    if (!revitCommandId.CanHaveBinding)
                    {
                        _logger?.LogDebug($"Rule command {commandName} ({commandId}) cannot have binding");
                        continue;
                    }

                    var binding = uiApplication.CreateAddInCommandBinding(revitCommandId);
                    binding.BeforeExecuted += OnBeforeCommandExecuted;
                    LogSubscriptionEvent("REGISTER-RULE", revitCommandId, uiApplication);
                    _commandBindings[revitCommandId] = binding;
                    alreadyBound.Add(commandId);
                    addedCount++;

                    _logger?.LogInfo($"Registered rule command binding: {commandName} ({commandId})");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to register rule command {commandName} ({commandId}): {ex.Message}");
                }
            }

            if (addedCount > 0)
            {
                _logger?.LogInfo($"Registered {addedCount} additional command bindings from rules");
            }
        }

        /// <summary>
        /// Cached mapping of integer command ID → RevitCommandId.
        /// Populated lazily on first miss. Eliminates O(1500) enum scan per lookup.
        /// </summary>
        private static readonly Dictionary<int, RevitCommandId> _commandIdCache = new Dictionary<int, RevitCommandId>();
        private static bool _commandIdCacheBuilt = false;

        /// <summary>
        /// Try to find RevitCommandId for a given integer command ID.
        /// Uses a static cache built once from the PostableCommand enum.
        /// </summary>
        private static RevitCommandId? TryLookupCommandId(int commandId)
        {
            if (_commandIdCache.TryGetValue(commandId, out var cached))
                return cached;

            // Build cache on first miss (one-time O(1500) scan instead of per-call)
            if (!_commandIdCacheBuilt)
            {
                _commandIdCacheBuilt = true;
                foreach (PostableCommand pc in Enum.GetValues(typeof(PostableCommand)))
                {
                    try
                    {
                        var revitId = RevitCommandId.LookupPostableCommandId(pc);
                        if (revitId != null)
                        {
                            var id = (int)revitId.Id;
                            if (!_commandIdCache.ContainsKey(id))
                                _commandIdCache[id] = revitId;
                        }
                    }
                    catch
                    {
                        // Skip unavailable commands
                    }
                }

                // Re-check after building cache
                if (_commandIdCache.TryGetValue(commandId, out cached))
                    return cached;
            }

            return null;
        }

        #region Event Protection Helpers

        /// <summary>
        /// Maps a Revit command ID name to one or more event protection dummy command IDs.
        /// Returns an empty array when the command has no applicable protection.
        /// A command may map to multiple protections (e.g. Export PDF triggers both
        /// Document Exporting and Document Printing protections); each is evaluated in
        /// order and the first block wins.
        /// </summary>
        private static string[] GetEventProtectionDummyCommandIds(string revitCommandName)
        {
            return revitCommandName switch
            {
                // Import CAD: ribbon button is ID_IMPORT_CAD; ID_FILE_IMPORT is the legacy menu entry
                "ID_IMPORT_CAD"               => new[] { "Ze_CADImportProtection" },
                "ID_FILE_IMPORT"              => new[] { "Ze_CADImportProtection" },
                "ID_RVTLINK_IMPORT_CAD"       => new[] { "Ze_CADImportProtection" },
                "ID_IMPORT_INSTANCE_EXPLODE"  => new[] { "Ze_CADExplodeProtection" },
                "ID_IMPORT_INST_PARTIAL_EXPLODE" => new[] { "Ze_CADExplodeProtection" },
                "ID_TRANSFER_PROJECT_STANDARDS"  => new[] { "Ze_TransferProjectStandardsProtection" },
                "ID_FILE_EXPORT"         => new[] { "Ze_DocumentExportingProtection" },
                "ID_EXPORT_IFC"          => new[] { "Ze_DocumentExportingProtection" },
                "ID_EXPORT_DWG"          => new[] { "Ze_DocumentExportingProtection" },
                "ID_EXPORT_DXF"          => new[] { "Ze_DocumentExportingProtection" },
                "ID_EXPORT_DGN"          => new[] { "Ze_DocumentExportingProtection" },
                "ID_EXPORT_FBX"          => new[] { "Ze_DocumentExportingProtection" },
                "ID_EXPORT_PDF"          => new[] { "Ze_DocumentPrintingProtection" },
                "ID_EXPORT_PDF_IN_PRINT" => new[] { "Ze_DocumentPrintingProtection" },
                "ID_EXPORT_STL"          => new[] { "Ze_DocumentExportingProtection" },
                "ID_EXPORT_DWF"          => new[] { "Ze_DocumentExportingProtection" },
                "ID_EXPORT_OBJ"          => new[] { "Ze_DocumentExportingProtection" },
                "ID_EXPORT_NWC"          => new[] { "Ze_DocumentExportingProtection" },
                "ID_EXPORT_SAT"          => new[] { "Ze_DocumentExportingProtection" },
                "ID_EXPORT_GBXML"        => new[] { "Ze_DocumentExportingProtection" },
                "ID_EXPORT_ODBC"         => new[] { "Ze_DocumentExportingProtection" },
                "ID_EXPORT_IMAGES"       => new[] { "Ze_DocumentExportingProtection" },
                "ID_EXPORT_FAMILY_TYPES" => new[] { "Ze_DocumentExportingProtection" },
                "ID_EXPORT_REPORT"       => new[] { "Ze_DocumentExportingProtection" },
                "ID_REVIT_FILE_PRINT"         => new[] { "Ze_DocumentPrintingProtection" },
                "ID_REVIT_FILE_PRINT_SETUP"   => new[] { "Ze_DocumentPrintingProtection" },
                "ID_BATCH_PRINT"              => new[] { "Ze_DocumentPrintingProtection" },
                // Save As variants — ID_FILE_SAVE_AS is what Revit fires for Project Save As (confirmed by journal)
                "ID_FILE_SAVE_AS"                   => new[] { "Ze_DocumentSaveAsProtection" },
                "ID_REVIT_FILE_SAVE_AS"             => new[] { "Ze_DocumentSaveAsProtection" },
                "ID_REVIT_FILE_SAVE_AS_CLOUD_MODEL" => new[] { "Ze_DocumentSaveAsProtection" },
                "ID_REVIT_SAVE_AS_FAMILY"           => new[] { "Ze_DocumentSaveAsProtection" },
                "ID_SAVE_FAMILY_ANY"                => new[] { "Ze_DocumentSaveAsProtection" },
                "ID_REVIT_SAVE_AS_TEMPLATE"         => new[] { "Ze_DocumentSaveAsProtection" },
                "ID_SAVE_GROUP"                     => new[] { "Ze_DocumentSaveAsProtection" },
                "ID_SAVE_VIEWS_TO_FILE"             => new[] { "Ze_DocumentSaveAsProtection" },
                _ => Array.Empty<string>()
            };
        }

        /// <summary>
        /// Returns true when the command name is any Save As variant. Used to
        /// suppress SyncConflictDialogInterceptor before the file picker opens.
        /// </summary>
        private static bool IsSaveAsCommand(string commandName) =>
            commandName == "ID_FILE_SAVE_AS"
            || commandName == "ID_REVIT_FILE_SAVE_AS"
            || commandName == "ID_REVIT_FILE_SAVE_AS_CLOUD_MODEL"
            || commandName == "ID_REVIT_SAVE_AS_FAMILY"
            || commandName == "ID_SAVE_FAMILY_ANY"
            || commandName == "ID_REVIT_SAVE_AS_TEMPLATE"
            || commandName == "ID_SAVE_GROUP"
            || commandName == "ID_SAVE_VIEWS_TO_FILE";

        /// <summary>
        /// Capture screenshot for command-based event protection.
        /// Returns the evidence's own ID if successful, null otherwise.
        /// The auditLogId FK links the evidence record back to the audit_log entry.
        /// </summary>
        private string? CaptureCommandEventScreenshot(EventProtectionSettings settings, string stage, string? auditLogId = null)
        {
            var screenshotService = _screenshotServiceGetter?.Invoke();
            if (screenshotService == null)
            {
                _logger?.LogDebug("Screenshot service not available for command event protection");
                return null;
            }

            try
            {
                var evidenceId = Guid.NewGuid().ToString();
                var revitContext = _revitContextGetter?.Invoke();
                var sessionId = revitContext?.SessionId.ToString() ?? Guid.NewGuid().ToString();

                var imageBase64 = screenshotService.CaptureRevitWindowAsBase64();
                if (!string.IsNullOrEmpty(imageBase64))
                {
                    _logger?.LogInfo($"Command event screenshot captured for {settings.DummyCommandId} ({stage}): {imageBase64.Length} chars");

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
                        var evidence = new BIManage.Data.SQLite.EvidenceRepository.EvidenceCapture
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
                            Metadata = $"{{\"mode\":\"{settings.Mode}\",\"source\":\"CommandBinding\"}}"
                        };

                        _ = evidenceRepository.RecordEvidenceCaptureAsync(evidence);
                    }

                    return evidenceId;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to capture command event screenshot: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Log audit entry for command-based event protection enforcement
        /// </summary>
        private void LogCommandEventAuditEntry(
            EventProtectionSettings settings,
            Document? document,
            EventInterventionResult result,
            string? auditLogId)
        {
            try
            {
                if (_auditRepository == null) return;

                var isAdmin = _isAdminCheck?.Invoke() ?? false;
                var revitContext = _revitContextGetter?.Invoke();
                var sessionId = revitContext?.SessionId.ToString();

                string? modelGuid = null;
                try
                {
                    if (document != null)
                        modelGuid = ModelGuidHelper.GetModelGuid(document, _logger);
                }
                catch { /* Non-critical */ }

                string action = result.Allowed && result.UserOverrode ? "override"
                    : result.Allowed ? "allowed"
                    : (settings.Mode == InterventionMode.Assist ? "cancelled" : "blocked");
                string? overrideMethod = result.UserOverrode ? "AdminPrivilege" : null;

                var auditEntry = new ProtectionAuditEntry
                {
                    Timestamp = DateTime.UtcNow,
                    UserName = document?.Application?.Username ?? Environment.UserName,
                    WasCompanyAdmin = isAdmin,
                    WasProjectAdmin = isAdmin,
                    ModelGuid = modelGuid,
                    ProtectionId = settings.Id,
                    CommandName = settings.ProtectionName,
                    Mode = settings.Mode switch
                    {
                        InterventionMode.Notify => ProtectionMode.Notify,
                        InterventionMode.Assist => ProtectionMode.Assist,
                        InterventionMode.Protect => ProtectionMode.Protect,
                        _ => ProtectionMode.Notify
                    },
                    Action = action switch
                    {
                        "allowed" => ProtectionAction.Allowed,
                        "blocked" => ProtectionAction.Blocked,
                        "cancelled" => ProtectionAction.Cancelled,
                        "override" => ProtectionAction.Override,
                        _ => ProtectionAction.Allowed
                    },
                    ElementIds = "",
                    ElementCount = 0,
                    Reason = result.Reason,
                    OverrideMethod = overrideMethod,
                    EventSource = "Event Restriction",
                    SessionId = sessionId,
                    // Per user request: every audit entry starts at sent_mail=1 (true).
                    SentMail = true,
                    AuditLogId = auditLogId
                };

                _auditRepository.SaveAuditEntry(auditEntry);
                _logger?.LogDebug($"Command event audit entry logged: {settings.DummyCommandId} - {action}");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to log command event audit entry: {ex.Message}");
            }
        }

        #endregion

        // [Fix-1a diagnostic] Logs the subscription lifecycle of every AddInCommandBinding
        // BeforeExecuted/Executed +=/-= with the registering ALC, assembly hash, and the
        // currently-live Application.Instance's assembly hash. Compared against
        // [AsmIdentity:OnStartup] in Application.cs to detect subscriptions registered from
        // a non-live ALC (the suspected cause of AddInManager's "Cannot find the host
        // add-in" warning). Pure logging — no behavior change.
        private void LogSubscriptionEvent(string op, RevitCommandId commandId, UIApplication? uiApplication = null)
        {
            try
            {
                string alcName = "n/a";
                int thisAsmHash = 0;
                int liveAsmHash = 0;
#if NET8_0_OR_GREATER
                var thisAsm = typeof(CommandInterceptionService).Assembly;
                var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(thisAsm);
                alcName = alc?.Name ?? "Default";
                thisAsmHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(thisAsm);
                var liveApp = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                if (liveApp != null)
                    liveAsmHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(liveApp.GetType().Assembly);
#endif
                string addInId = "unknown";
                try { addInId = uiApplication?.ActiveAddInId?.GetGUID().ToString() ?? "unknown"; }
                catch { /* ActiveAddInId may throw if context isn't ready */ }

                bool fromLiveAlc = (thisAsmHash != 0 && thisAsmHash == liveAsmHash);
                // Per-command diagnostic from the cross-ALC debugging campaign — kept as DEBUG
                // so it's still available with verbose logging, but stops flooding INFO at
                // ~100 lines per document open now that the duplicate-load bug is fixed.
                _logger?.LogDebug(
                    $"[SubDiag] {op} cmd={commandId.Name}(id={commandId.Id}) ALC={alcName} thisAsm={thisAsmHash} liveAsm={liveAsmHash} fromLiveAlc={fromLiveAlc} AddInId={addInId}");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[SubDiag] log failed for op={op}: {ex.Message}");
            }
        }
    }
}
