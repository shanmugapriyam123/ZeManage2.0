using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Revit.BackgroundSync;
using BIManage.Revit.Context;
using BIManage.Revit.Helpers;
using BIManage.Revit.SyncTrafficControl;
using BIManage.Revit.SyncTrafficControl.Models;

namespace BIManage.Revit.Commands.Bindings
{
    /// <summary>
    /// Intercepts SynchronizeWithCentral and SynchronizeNow commands BEFORE Revit processes them.
    /// Replaces the unreliable e.Cancel() approach in DocumentSynchronizingWithCentral event handler.
    ///
    /// Gates sync upstream via command binding, never cancels mid-sync.
    /// BeforeExecuted on a command binding fires BEFORE Revit starts the sync, so e.Cancel is reliable.
    /// </summary>
    public class SyncCommandBinding : CommandBindingBase
    {
        // Holds every AddInCommandBinding we attach to. Previously the class kept only
        // two refs (_bindingSyncCentral, _bindingSyncNow) which silently dropped any
        // third+ binding during registration and left those handlers attached on
        // Unregister(). With cloud sync ids added we may bind 3-6 commands depending on
        // Revit version, so storage has to be unbounded.
        private readonly List<AddInCommandBinding> _bindings = new();
        private readonly Func<SyncTrafficControlService?> _syncTrafficControlGetter;
        private readonly Func<ISignalREventBus?> _signalREventBusGetter;
        private readonly Func<IRevitContext?> _revitContextGetter;
        private readonly Func<CommandProtectionBinding?> _commandProtectionGetter;
        private readonly IRuleCommandInterceptor? _ruleInterceptor;

        public SyncCommandBinding(
            UIApplication uiApp,
            ILogger logger,
            Func<SyncTrafficControlService?> syncTrafficControlGetter,
            Func<ISignalREventBus?>? signalREventBusGetter = null,
            Func<IRevitContext?>? revitContextGetter = null,
            Func<CommandProtectionBinding?>? commandProtectionGetter = null,
            IRuleCommandInterceptor? ruleInterceptor = null)
            : base(uiApp, logger)
        {
            _syncTrafficControlGetter = syncTrafficControlGetter ?? (() => null);
            _signalREventBusGetter = signalREventBusGetter ?? (() => null);
            _revitContextGetter = revitContextGetter ?? (() => null);
            _commandProtectionGetter = commandProtectionGetter ?? (() => null);
            _ruleInterceptor = ruleInterceptor;
        }

        public override void Register()
        {
            RegisterWithBeforeExecute();
        }

        public override void RegisterWithBeforeExecute()
        {
            try
            {
                // Bind every sync command id we know about. The first two cover the
                // on-prem "Sync with Central" / shortcut flow (works for local workshared
                // models). The remaining ids are cloud-sync variants Revit may emit for
                // Autodesk Docs / BIM 360 / ACC models — without these the binding never
                // fires for cloud sync and the plugin's queue dialog is bypassed entirely
                // (Revit's native popup runs instead). LookupCommandId returns null for
                // ids that don't exist in the current Revit version — we skip those
                // gracefully so this list is forward/backward compatible across R21..R26.
                var syncIds = new[]
                {
                    // Canonical Revit command IDs (per local DB's command_cache table).
                    // These are the ids fired by BeforeExecuted on Sync Now / Sync with Modified
                    // Settings ribbon clicks. Without registering these specifically, BeforeExecuted
                    // never reaches us — which is exactly the issue the Phase 1.f event-handler
                    // fallback was added to work around. Register them here so Phase 1.e dispatches
                    // correctly and Phase 1.g's ArmNextSync correctly arms the gate.
                    "ID_SYNCHRONIZE_NOW",
                    "ID_SYNCHRONIZE_AND_MODIFY_SETTINGS",
                    // Legacy / PostableCommand-resolved aliases. PostableCommand.SynchronizeNow
                    // resolves to ID_FILE_SAVE_TO_CENTRAL_SHORTCUT in Revit 2024; keep these
                    // bindings for cross-version compatibility (older Revit, alternate dispatch).
                    "ID_FILE_SAVE_TO_CENTRAL",
                    "ID_FILE_SAVE_TO_CENTRAL_SHORTCUT",
                    // Cloud / Autodesk Docs / BIM 360 sync variants.
                    "ID_REVIT_FILE_SYNCHRONIZE_NOW",
                    "ID_REVIT_FILE_SYNCHRONIZE",
                    "ID_REVIT_BIM_360_SYNC",
                    "ID_FILE_BIM_360_SYNC",
                    "ID_REVIT_SYNCHRONIZE_AND_MODIFY_SETTINGS",
                };
                foreach (var idStr in syncIds)
                {
                    try
                    {
                        var cmdId = RevitCommandId.LookupCommandId(idStr);
                        if (cmdId == null)
                        {
                            Logger?.LogWarning($"SyncCommandBinding: {idStr} not registered — LookupCommandId returned null");
                            continue;
                        }
                        if (!cmdId.CanHaveBinding)
                        {
                            Logger?.LogWarning($"SyncCommandBinding: {idStr} not registered — CanHaveBinding=false");
                            continue;
                        }
                        if (HasIndividualBinding(cmdId.Name))
                        {
                            Logger?.LogInfo($"SyncCommandBinding: {idStr} skipped — already bound as {cmdId.Name}");
                            continue;
                        }
                        var binding = UIApp.CreateAddInCommandBinding(cmdId);
                        binding.BeforeExecuted += OnBeforeExecuted;
                        MarkAsRegistered(cmdId.Name);
                        _bindings.Add(binding);
                        Logger?.LogInfo($"SyncCommandBinding: Registered {cmdId.Name}");
                    }
                    catch (Exception idEx)
                    {
                        Logger?.LogWarning($"SyncCommandBinding: {idStr} not registered — {idEx.Message}");
                    }
                }

                // Also bind PostableCommand sync variants (SynchronizeNow, SynchronizeAndModifySettings).
                // Different Revit versions / sync entry points map to different command ids; bind every
                // variant the API exposes so we maximise the chance the BeforeExecuted handler fires.
                TryRegisterPostableSync(PostableCommand.SynchronizeNow);
                TryRegisterPostableSync(PostableCommand.SynchronizeAndModifySettings);
            }
            catch (Exception ex)
            {
                Logger?.LogError($"SyncCommandBinding: Registration failed: {ex.Message}", ex);
            }
        }

        private void TryRegisterPostableSync(PostableCommand command)
        {
            try
            {
                var cmdId = RevitCommandId.LookupPostableCommandId(command);
                if (cmdId == null)
                {
                    Logger?.LogWarning($"SyncCommandBinding: {command} not registered — LookupPostableCommandId returned null");
                    return;
                }
                if (!cmdId.CanHaveBinding)
                {
                    Logger?.LogWarning($"SyncCommandBinding: {cmdId.Name} not registered — CanHaveBinding=false");
                    return;
                }
                if (HasIndividualBinding(cmdId.Name))
                {
                    Logger?.LogInfo($"SyncCommandBinding: {command} skipped — already bound as {cmdId.Name}");
                    return;
                }
                var binding = UIApp.CreateAddInCommandBinding(cmdId);
                binding.BeforeExecuted += OnBeforeExecuted;
                MarkAsRegistered(cmdId.Name);
                _bindings.Add(binding);
                Logger?.LogInfo($"SyncCommandBinding: Registered {cmdId.Name} (from {command})");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"SyncCommandBinding: {command} not registered — {ex.Message}");
            }
        }

        public override void Unregister()
        {
            try
            {
                foreach (var binding in _bindings)
                {
                    try { binding.BeforeExecuted -= OnBeforeExecuted; }
                    catch (Exception detachEx)
                    {
                        Logger?.LogWarning($"SyncCommandBinding: Detach failed for one binding — {detachEx.Message}");
                    }
                }
                _bindings.Clear();
                Logger?.LogInfo("SyncCommandBinding: Unregistered");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"SyncCommandBinding: Unregister failed: {ex.Message}", ex);
            }
        }

        private void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e)
        {
            // Record the click immediately so SyncConflictDialogInterceptor can
            // disambiguate Revit's shared "File not saved." popup (used both for
            // real sync collisions and for any other cancelled save). Must run
            // before any early return below — covers all sync command ids (on-prem,
            // cloud, postable) registered by this binding.
            SyncConflictDialogInterceptor.NoteSyncAttempt();

            try
            {
                Logger?.LogInfo($"[SyncBinding] OnBeforeExecuted fired for CommandId={e.CommandId?.Name ?? "null"}");

                // Skip Command Protection / Rule evaluation when this sync is internal
                // (BackgroundSync auto-loop, AutoExit on shutdown, or queued-retry via
                // TriggerSync's PostCommand). The user already consented at queue-entry or
                // settings; re-prompting here would retry-storm the auto-loop or surprise
                // the user with a second dialog.
                if (BIManage.Revit.SyncTrafficControl.SyncProtectionGate.IsInternalSync)
                {
                    Logger?.LogDebug("Sync command: skipping Command Protection / Rule (internal sync)");
                }
                else
                {
                    // PRIORITY 0: Check command_settings-driven Command Protection.
                    // Sync is in the individual-binding set (this class owns the Revit binding),
                    // so CommandProtectionBinding only cached the setting and never registered
                    // its own handler. We must dispatch ProcessCommandBeforeExecution here for
                    // the Notify/Assist/Protect dialog to fire. Same pattern as PinCommandBinding.
                    var commandProtection = _commandProtectionGetter?.Invoke();
                    if (commandProtection != null)
                    {
                        commandProtection.ProcessCommandBeforeExecution(e);
                        if (e.Cancel)
                        {
                            Logger?.LogInfo("Sync command cancelled by command protection");
                            return;
                        }
                    }

                    // PRIORITY 1: Rule Management evaluation.
                    _ruleInterceptor?.OnBeforeExecuted(sender, e);
                    if (e.Cancel)
                    {
                        Logger?.LogInfo("Sync command cancelled by rule evaluation");
                        return;
                    }

                    // Phase 1.g — Phase 1.e just evaluated Command Protection / Rule against
                    // the specific clicked command id (e.CommandId.Name). Arm the gate so the
                    // Phase 1.f broad two-key evaluation in OnDocumentSynchronizingWithCentral
                    // doesn't fire a duplicate or wrong-key dialog for this same sync.
                    BIManage.Revit.SyncTrafficControl.SyncProtectionGate.ArmNextSync();
                }

                var syncControl = _syncTrafficControlGetter?.Invoke();
                if (syncControl == null) return; // No traffic control — allow sync

                var uiApp = sender as UIApplication;
                var doc = uiApp?.ActiveUIDocument?.Document;
                if (doc == null || !DocumentTypeHelper.IsWorkshared(doc)) return;

                var modelGuid = ModelGuidHelper.GetModelGuid(doc, Logger);
                if (string.IsNullOrEmpty(modelGuid)) return;

                var sessionId = _revitContextGetter?.Invoke()?.SessionId.ToString() ?? "";
                // Prefer Application.Username (Revit identity) but fall back to Windows account
                // name when blank — Windows is unique per machine, so two clients sharing one
                // Autodesk login still produce distinct queue-broadcast names.
                var revitName = doc.Application?.Username;
                var username = !string.IsNullOrWhiteSpace(revitName) ? revitName! : Environment.UserName;
                var modelName = doc.Title;
                Logger?.LogDebug($"[SyncBinding] OnBeforeExecuted identity: username='{username}', sessionId={sessionId}, model='{modelName}'");

                // === CLAIM PHASE (race-condition shrinker) ===
                // Without this, two clients clicking inside the SignalR round-trip window
                // (~100-500ms) both see _activeSyncers empty in EvaluateSync and both proceed
                // — the "multiple users sync at once" bug. We broadcast our intent first,
                // wait briefly for peer claims, then arbitrate by earliest timestamp (lex
                // tiebreak on sessionId). Skip if the user already accepted their turn via
                // SyncReadyDialog — the _pendingQueueSync entry bypasses claim arbitration
                // because the queue already picked them as next.
                // Debounce: Revit can fire BeforeExecuted twice for one click when multiple
                // sync command-ids are bound (ID_FILE_SAVE_TO_CENTRAL + SynchronizeNow).
                // TryBeginClaimPhase returns false on the second call within 2s for the
                // same model — we skip the broadcast and the synchronous sleep (avoiding
                // 1.2s of UI freeze) but still arbitrate against whatever the first call
                // already collected in _pendingClaims.
                var claimedAtUtc = DateTime.UtcNow;
                if (!syncControl.IsBackgroundSyncActive
                    && !syncControl.IsQueuedForModel(modelGuid)
                    && syncControl.TryBeginClaimPhase(modelGuid))
                {
                    try
                    {
                        // Fire-and-forget broadcast — wait synchronously below so peers' claims
                        // have time to reach us.
                        _ = syncControl.BroadcastClaimAsync(modelGuid, sessionId, username, claimedAtUtc);
                        System.Threading.Thread.Sleep(syncControl.ClaimWaitMs);
                    }
                    catch (Exception claimEx)
                    {
                        Logger?.LogWarning($"[SyncBinding] Claim broadcast/wait failed: {claimEx.Message}");
                    }
                }
                else
                {
                    Logger?.LogDebug($"[SyncBinding] Claim phase skipped (background/queued/debounced) for {modelName}");
                }

                // EvaluateSync checks _pendingQueueSync for queue-approved syncs (auto-bypass)
                var decision = syncControl.EvaluateSync(modelGuid, modelName, sessionId, username);

                // ARBITRATION: even if EvaluateSync said Allow, a peer's claim may have
                // arrived during the wait window. Pick the earliest claim; ties broken by
                // ordinal sessionId compare so every machine reaches the same verdict.
                if (decision.Action != SyncAction.Blocked
                    && !syncControl.IsBackgroundSyncActive
                    && !syncControl.IsQueuedForModel(modelGuid))
                {
                    var competing = syncControl.GetCompetingClaims(modelGuid, claimedAtUtc.AddSeconds(-1));
                    SyncTrafficControl.SyncTrafficControlService.ClaimEntry winner = null;
                    foreach (var c in competing)
                    {
                        bool peerWins = c.ClaimedAtUtc < claimedAtUtc
                            || (c.ClaimedAtUtc == claimedAtUtc
                                && string.CompareOrdinal(c.SessionId, sessionId) < 0);
                        if (!peerWins) continue;
                        if (winner == null
                            || c.ClaimedAtUtc < winner.ClaimedAtUtc
                            || (c.ClaimedAtUtc == winner.ClaimedAtUtc
                                && string.CompareOrdinal(c.SessionId, winner.SessionId) < 0))
                        {
                            winner = c;
                        }
                    }
                    if (winner != null)
                    {
                        Logger?.LogInfo($"[SyncBinding] Claim lost to {winner.Username} (peer ts={winner.ClaimedAtUtc:HH:mm:ss.fff}, ours={claimedAtUtc:HH:mm:ss.fff}) — re-routing to JoinQueue");
                        decision = SyncDecision.Blocked(
                            string.IsNullOrWhiteSpace(winner.Username) ? "Another user" : winner.Username,
                            winner.ClaimedAtUtc);
                    }
                    else if (competing.Count > 0)
                    {
                        Logger?.LogInfo($"[SyncBinding] Claim won (ours={claimedAtUtc:HH:mm:ss.fff}, {competing.Count} peer claim(s) all later or higher sessionId)");
                    }
                }

                if (decision.Action != SyncAction.Blocked)
                {
                    // Won arbitration — clear stale peer claims so the next sync click on
                    // this model starts with a clean window.
                    try { syncControl.ClearClaims(modelGuid); } catch { }
                    // Allowed — record sync start for traffic coordination
                    syncControl.OnLocalSyncStarting(modelGuid, modelName, sessionId, username);
                    Logger?.LogInfo($"[SyncBinding] Sync allowed for {modelName}");

                    // 1-second delayed collision check — pre-arbitration legacy safety net
                    // for the rare case where claim arbitration (~600ms window) failed to
                    // serialize two simultaneous clicks. GetActiveSyncer returns the EARLIEST
                    // claimant from _activeSyncers (multi-slot). We only demote our own row
                    // if a peer was genuinely earlier — if WE are the earliest, we won the
                    // race and must NOT mark ourselves as InQueue (that caused the visible
                    // bug where Zestine's chip stayed in "Currently Syncing" while the grid
                    // row said "In Queue" — the collision check was firing even though
                    // Zestine had won).
                    var collisionEventBus = _signalREventBusGetter?.Invoke();
                    if (collisionEventBus != null)
                    {
                        var ourStartedAt = DateTime.UtcNow;
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            try
                            {
                                await System.Threading.Tasks.Task.Delay(1000);
                                var remote = syncControl.GetActiveSyncer(modelGuid);
                                // No remote, or remote IS us → we are the active syncer, do nothing.
                                if (remote == null
                                    || string.Equals(remote.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                                {
                                    return;
                                }
                                // Remote is someone else, but they started AFTER us → we are
                                // still the earliest claimant; demoting ourselves to InQueue
                                // would be wrong. The remote peer is the one Revit's central
                                // lock will queue behind us.
                                if (remote.StartedAtUtc >= ourStartedAt)
                                {
                                    Logger?.LogDebug($"[SyncBinding] Collision check: peer {remote.Username} started after us ({remote.StartedAtUtc:HH:mm:ss.fff} vs ours {ourStartedAt:HH:mm:ss.fff}) — not demoting");
                                    return;
                                }
                                Logger?.LogInfo($"[SyncBinding] Collision detected — {remote.Username} started EARLIER ({remote.StartedAtUtc:HH:mm:ss.fff} vs ours {ourStartedAt:HH:mm:ss.fff}); marking own row as InQueue");
                                collisionEventBus.Publish(new SyncQueueUpdateEvent
                                {
                                    ModelGuid = modelGuid,
                                    SessionId = sessionId,
                                    Username = username,
                                    RevitUsername = username,
                                    ComputerName = Environment.MachineName,
                                    SelfCollision = true,
                                    Source = "SyncBinding"
                                });
                            }
                            catch (Exception delayEx)
                            {
                                Logger?.LogWarning($"[SyncBinding] Delayed collision check failed: {delayEx.Message}");
                            }
                        });
                    }
                    return; // Let Revit proceed
                }

                // === BLOCKED ===
                Logger?.LogInfo($"[SyncBinding] Sync BLOCKED — {decision.BlockedByUsername} is syncing");

                // Save locally before blocking so the user's work is preserved EVEN IF the
                // queue join fails. Show progress in Revit's bottom-left status bar (same
                // channel background-sync uses, prefix "ZeManage: …"). If the save FAILS,
                // we abort the queue join entirely — better the user retry than risk
                // losing changes in memory while they wait for a queue slot.
                //
                // EXPLICIT LOCAL-ONLY GUARANTEE: doc.Save() writes to doc.PathName. For a
                // workshared LOCAL COPY (the standard Revit workflow), PathName is the
                // local .rvt file — no central interaction. For a directly-opened central
                // (rare advanced workflow), PathName IS the central — Save() would touch
                // central, which we explicitly promised never to do. Guard with
                // WorksharingUtils.GetCentralModelPath comparison so the save only runs
                // when we can prove this is a local copy.
                var mainWindow = uiApp.MainWindowHandle;
                bool isLocalCopyOfCentral = false;
                try
                {
                    if (doc.IsWorkshared && !doc.IsDetached && !string.IsNullOrEmpty(doc.PathName))
                    {
                        var centralPath = doc.GetWorksharingCentralModelPath();
                        var docPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(doc.PathName);
                        isLocalCopyOfCentral = centralPath != null && docPath != null && !centralPath.Equals(docPath);
                    }
                }
                catch (Exception pathEx)
                {
                    Logger?.LogDebug($"[SyncBinding] Local-copy check threw — defaulting to skip save: {pathEx.Message}");
                    isLocalCopyOfCentral = false;
                }

                if (isLocalCopyOfCentral && doc.IsModified && !doc.IsReadOnly)
                {
                    try
                    {
                        RevitInteropHelper.SetStatusText(mainWindow,
                            $"ZeManage: Saving local copy before joining queue — {doc.Title}…");
                        doc.Save();   // safe — writes to local .rvt, never touches central
                        RevitInteropHelper.SetStatusText(mainWindow,
                            $"ZeManage: Saved. Joining queue — {doc.Title}");
                        Logger?.LogInfo("[SyncBinding] Local save completed before sync queue");
                    }
                    catch (Exception saveEx)
                    {
                        Logger?.LogWarning($"[SyncBinding] Local save failed: {saveEx.Message}");
                        RevitInteropHelper.ClearStatusText(mainWindow);

                        // Don't show the modal during background-sync; just abort.
                        if (!syncControl.IsBackgroundSyncActive)
                        {
                            BIManage.Views.Common.ZeConfirmDialog.ConfirmShow(
                                title: "Couldn't save your work",
                                message: "Saving the local copy failed, so we did not join the sync queue. " +
                                         "Please save manually, then try to sync again.\n\n" +
                                         "Details: " + saveEx.Message,
                                primaryText: "OK",
                                secondaryText: "Close",
                                ownerHandle: mainWindow);
                        }

                        if (e.Cancellable) e.Cancel = true;
                        return; // DO NOT call JoinQueue — user's unsaved work must not be put at further risk.
                    }
                }
                else
                {
                    Logger?.LogInfo($"[SyncBinding] Skipping pre-queue save — isLocalCopyOfCentral={isLocalCopyOfCentral}, IsModified={doc.IsModified}, IsReadOnly={doc.IsReadOnly}");
                }

                // Background sync: auto-queue without dialog
                if (syncControl.IsBackgroundSyncActive)
                {
                    Logger?.LogInfo("[SyncBinding] Background sync — auto-joining queue");
                    syncControl.JoinQueue(modelGuid, modelName, sessionId, username);
                    if (e.Cancellable) e.Cancel = true;
                    return;
                }

                // Suppress the conflict dialog when this user JUST completed a local sync
                // (within 60 s). A peer's SyncStarting that fans-out moments after our
                // completion otherwise triggers this dialog on a stale Revit command click
                // (e.g. an auto-save retry). Without this, the user sees "Join Sync Queue"
                // after they've already finished syncing — and clicking Join creates a
                // ghost "------" queue entry.
                if (syncControl.JustCompletedLocalSync(modelGuid))
                {
                    Logger?.LogInfo($"[SyncBinding] Skipping conflict dialog — local sync for {modelGuid} completed within the last 60 s. Letting Revit cancel silently.");
                    if (e.Cancellable) e.Cancel = true;
                    return;
                }

                // Show conflict dialog — safe in BeforeExecuted (designed for UI interaction)
                var eventBus = _signalREventBusGetter?.Invoke();
                var dialog = new BIManageRevit.BIManage.Views.SyncTrafficControl.SyncConflictDialog(
                    decision.BlockedByUsername, decision.BlockedSince, modelName,
                    eventBus, modelGuid);
                BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);

                var dialogResult = dialog.ShowDialog();

                if (dialogResult == true && dialog.SyncReady)
                {
                    // Blocker finished while dialog was open — proceed with sync
                    Logger?.LogInfo("[SyncBinding] Blocker finished — proceeding with sync");
                    syncControl.OnLocalSyncStarting(modelGuid, modelName, sessionId, username);
                    return; // Don't cancel — let Revit sync
                }

                if (dialogResult == true)
                {
                    // User chose "Join Queue"
                    syncControl.JoinQueue(modelGuid, modelName, sessionId, username);
                    Logger?.LogInfo("[SyncBinding] User joined queue");
                }

                // Cancel the sync command — safe because BeforeExecuted fires before Revit commits
                if (e.Cancellable)
                {
                    e.Cancel = true;
                    Logger?.LogInfo("[SyncBinding] Sync command cancelled (blocked/queued)");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[SyncBinding] Error (fail-open): {ex.Message}", ex);
                // Fail-open: don't block sync on errors
            }
        }
    }
}
