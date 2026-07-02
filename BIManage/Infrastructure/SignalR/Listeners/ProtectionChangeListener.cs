using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BIManage.Core.Rules;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Core;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Infrastructure.SignalR.Messages;
using BIManage.Revit.Commands.Bindings;
using BIManage.Revit.Protection;

namespace BIManage.Infrastructure.SignalR.Listeners
{
    /// <summary>
    /// Listens for protection change notifications from the backend (via SignalR).
    /// When protections are edited at any level (Company, Project, Model), this listener:
    ///   1. Resolves which open models are affected
    ///   2. Fetches updated protections from the API for each affected model
    ///   3. Persists to local SQLite and refreshes in-memory caches
    ///   4. Publishes ProtectionChangedEvent for UI components
    /// </summary>
    public class ProtectionChangeListener : SignalListenerBase
    {
        private readonly ISignalREventBus _eventBus;
        private readonly ISignalRConnectionManager _connectionManager;
        private readonly CommandProtectionSyncService? _commandSync;
        private readonly EventProtectionSyncService? _eventSync;
        private readonly RulesSyncService? _rulesSync;
        private readonly PinProtectionSyncService? _pinSync;
        private readonly Func<CommandProtectionBinding?>? _commandProtectionBindingGetter;
        private readonly IEventProtectionService? _eventProtection;
        private readonly IRuleService? _ruleService;
        private readonly RegisteredModelsRepository? _registeredModels;

        public override string Name => "ProtectionChange";

        public override IEnumerable<string> SupportedMethods => new[]
        {
            SignalRMethods.ProtectionSettingsChange,
            SignalRMethods.RuleUpdate,
            SignalRMethods.PinProtectionChange
        };

        public ProtectionChangeListener(
            ISignalREventBus eventBus,
            ISignalRConnectionManager connectionManager,
            CommandProtectionSyncService? commandSync,
            EventProtectionSyncService? eventSync,
            RulesSyncService? rulesSync,
            PinProtectionSyncService? pinSync,
            Func<CommandProtectionBinding?>? commandProtectionBindingGetter,
            IEventProtectionService? eventProtection,
            IRuleService? ruleService,
            ILogger logger,
            RegisteredModelsRepository? registeredModels = null) : base(logger)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
            _commandSync = commandSync;
            _eventSync = eventSync;
            _rulesSync = rulesSync;
            _pinSync = pinSync;
            _commandProtectionBindingGetter = commandProtectionBindingGetter;
            _eventProtection = eventProtection;
            _ruleService = ruleService;
            _registeredModels = registeredModels;
        }

        /// <summary>
        /// Fallback for company-scope broadcasts that arrive while no Revit document is open
        /// (e.g. boot-time reconnect bursts, or the admin edits company-wide protections while
        /// the user is between models). Returns up to N registered model GUIDs we can run the
        /// per-model fetch against — the per-model endpoint already returns ALL applicable
        /// protections (model + project + company), so a single fallback fetch refreshes the
        /// local DB with the latest company-level values. Without this, the listener would
        /// just reload the stale local DB, leaving the Revit dialog showing pre-edit data
        /// until the user opens a model and triggers the doc-open fetch.
        /// </summary>
        private async Task<List<string>> GetFallbackModelGuidsAsync(int max = 1)
        {
            if (_registeredModels == null) return new List<string>();
            try
            {
                var active = await _registeredModels.GetActiveModelsAsync();
                return active
                    .Where(m => !string.IsNullOrEmpty(m.ModelGuid))
                    .OrderByDescending(m => m.LastOpenedAt ?? DateTime.MinValue)
                    .Take(max)
                    .Select(m => m.ModelGuid)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[ProtectionChange] Fallback model lookup failed: {ex.Message}");
                return new List<string>();
            }
        }

        protected override async Task ProcessMessageAsync(SignalRMessageInfo message)
        {
            LogMessageReceived(message);

            var payload = GetPayloadSafe<ProtectionChangePayload>(message);

            // Log raw payload for diagnostics
            _logger?.LogInfo($"[ProtectionChange] Raw payload: {message.Payload ?? "null"}");
            _logger?.LogInfo($"[ProtectionChange] Parsed: ProtectionType={payload?.ProtectionType ?? "null"}, ChangeType={payload?.ChangeType ?? "null"}, LevelScope={payload?.LevelScope}, ModelGuid={payload?.ModelGuid ?? "null"}, ProjectId={payload?.ProjectId ?? "null"}");

            // Resolve which open models need a model-scoped REST refresh.
            // Empty result is NOT a reason to bail — it just means no open Revit document
            // matches the broadcast's scope (e.g. a company-level change while no doc is
            // open, or a model-level change for a model the user hasn't opened). We must
            // still publish ProtectionChangedEvent so any open Command/Event/Rule dialog
            // can refresh itself via its own subscription handler (each dialog knows its
            // own model GUID and can fetch fresh data without depending on the SignalR
            // connection manager's open-docs list). Skipping the publish here is what was
            // causing Web → Revit updates to silently drop.
            var modelsToRefresh = ResolveAffectedModels(payload, message);
            if (modelsToRefresh.Count == 0)
            {
                _logger?.LogInfo("[ProtectionChange] No open Revit document matched the broadcast scope; skipping per-model REST fetch but still publishing event for open dialogs to refresh. Open docs: " +
                    string.Join(", ", _connectionManager.GetOpenDocuments().Select(d => d.ModelGuid)));
            }
            else
            {
                _logger?.LogInfo($"[ProtectionChange] {message.Method} affects {modelsToRefresh.Count} open model(s): {string.Join(", ", modelsToRefresh)}");
            }

            switch (message.Method)
            {
                case SignalRMethods.ProtectionSettingsChange:
                    await HandleProtectionSettingsChangeAsync(payload, modelsToRefresh);
                    break;

                case SignalRMethods.RuleUpdate:
                    await HandleRuleUpdateAsync(modelsToRefresh);
                    break;

                case SignalRMethods.PinProtectionChange:
                    await HandlePinProtectionChangeAsync(modelsToRefresh);
                    break;
            }
        }

        /// <summary>
        /// Resolves which open models are affected by a protection change based on scope.
        /// Model-level: only the specific model.
        /// Project-level: all open models in that project.
        /// Company-level: all open models.
        /// </summary>
        private List<string> ResolveAffectedModels(ProtectionChangePayload? payload, SignalRMessageInfo message)
        {
            var openDocs = _connectionManager.GetOpenDocuments();
            if (openDocs.Count == 0)
                return new List<string>();

            // Model-level: refresh only the specific model
            var modelGuid = payload?.ModelGuid ?? message?.ModelGuid;
            if (!string.IsNullOrEmpty(modelGuid))
            {
                return openDocs
                    .Where(d => string.Equals(d.ModelGuid, modelGuid, StringComparison.OrdinalIgnoreCase))
                    .Select(d => d.ModelGuid)
                    .ToList();
            }

            // Project-level: refresh all open models in that project
            var projectId = payload?.ProjectId ?? message?.ProjectId;
            if (!string.IsNullOrEmpty(projectId))
            {
                return openDocs
                    .Where(d => string.Equals(d.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
                    .Select(d => d.ModelGuid)
                    .ToList();
            }

            // Company-level: refresh ALL open models
            return openDocs.Select(d => d.ModelGuid).ToList();
        }

        /// <summary>
        /// Handles ProtectionSettingsChange — dispatches to Command, Event, or Rule based on
        /// ProtectionType. Server uses this single SignalR method for all three types, so the
        /// listener has to fan out to the right sync service. Without the explicit "rule" branch
        /// the plugin used to fetch command protections in response to rule edits, leaving the
        /// rule list stale until manual refresh.
        /// </summary>
        private async Task HandleProtectionSettingsChangeAsync(ProtectionChangePayload? payload, List<string> models)
        {
            var protType = payload?.ProtectionType?.ToLowerInvariant() ?? "command";
            var changeType = payload?.ChangeType?.ToLowerInvariant() ?? "updated";

            // For deletions with a known EntityId, just remove locally — don't re-fetch from server.
            // Re-fetching would re-insert inherited protections from higher scopes (company/project),
            // undoing the user's delete.
            if (changeType == "deleted" && !string.IsNullOrEmpty(payload?.EntityId))
            {
                if (protType == "event")
                {
                    await HandleEventProtectionDeletedAsync(payload.EntityId);
                }
                else if (protType == "rule")
                {
                    // Rule deletions: refresh from server so inherited rules from higher scopes
                    // re-populate (the rule engine already handles override semantics).
                    await HandleRuleUpdateAsync(models);
                }
                else
                {
                    await HandleCommandProtectionDeletedAsync(payload.EntityId);
                }
            }
            else
            {
                // Stale-data fix (2026-05-25): if a company/project-scope broadcast arrives
                // while no open Revit document matches the scope, the per-model fetch loop
                // below would be a no-op and the listener would just reload the (stale) local
                // DB. That's the cause of "Web shows new mode/toggle, Revit shows old" —
                // observed when admin changes company-wide Event/Command/Rule settings between
                // sessions or before any model is opened. Fall back to ANY registered model
                // GUID so the per-model fetch endpoint refreshes the local DB. The endpoint
                // returns all applicable protections (model + project + company), so one
                // fallback fetch updates company-level rows even though the GUID is just a
                // routing key, not a filter on the response.
                var effectiveModels = models;
                if (effectiveModels.Count == 0)
                {
                    var fallback = await GetFallbackModelGuidsAsync();
                    if (fallback.Count > 0)
                    {
                        _logger?.LogInfo($"[ProtectionChange] No open doc matched broadcast scope — using fallback registered model {fallback[0]} so company/project-level changes refresh the local DB");
                        effectiveModels = fallback;
                    }
                }

                if (protType == "event")
                {
                    await RefreshEventProtectionsAsync(effectiveModels);
                }
                else if (protType == "rule")
                {
                    await HandleRuleUpdateAsync(effectiveModels);
                }
                else
                {
                    // Default to command protection
                    await RefreshCommandProtectionsAsync(effectiveModels);
                }
            }

            _eventBus.Publish(new ProtectionChangedEvent
            {
                ProtectionType = payload?.ProtectionType ?? "Command",
                ChangeType = payload?.ChangeType ?? "Updated",
                AffectedModelGuids = models,
                Source = Name
            });

            // If the change touched Ze_CADExplodeProtection, ask the ribbon swap to re-evaluate
            // its visibility state. The protection toggle controls whether the native ribbon
            // Explode buttons are hidden behind shadow versions — without this refresh, admins
            // would have to restart Revit for a toggle change to affect ribbon UI.
            if (protType == "event")
            {
                try { global::BIManageRevit.BIManage.Revit.Applications.Application.Instance?.RefreshCADExplodeSwapState(); }
                catch (Exception ex) { _logger?.LogWarning($"[ProtectionChange] Explode swap refresh failed: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Handle a command protection deleted via SignalR — remove from local DB and refresh bindings.
        /// Does NOT re-fetch from server (which would re-insert inherited protections from higher scopes).
        /// </summary>
        private async Task HandleCommandProtectionDeletedAsync(string entityId)
        {
            try
            {
                if (_commandSync != null)
                {
                    var deleted = await _commandSync.DeleteLocalRecordAsync(entityId);
                    _logger?.LogInfo($"[ProtectionChange] Deleted command protection locally: {entityId} (found={deleted})");
                }

                // Refresh in-memory cache
                var binding = _commandProtectionBindingGetter?.Invoke();
                binding?.LoadAndRegisterCommands();
                _logger?.LogInfo("[ProtectionChange] CommandProtectionBinding refreshed after delete");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ProtectionChange] Failed to handle command deletion {entityId}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Handle an event protection deleted via SignalR — remove from local DB and refresh cache.
        /// Does NOT re-fetch from server (which would re-insert inherited protections from higher scopes).
        /// </summary>
        private async Task HandleEventProtectionDeletedAsync(string entityId)
        {
            try
            {
                if (_eventSync != null)
                {
                    var deleted = await _eventSync.DeleteLocalRecordAsync(entityId);
                    _logger?.LogInfo($"[ProtectionChange] Deleted event protection locally: {entityId} (found={deleted})");
                }

                // Refresh in-memory cache
                _eventProtection?.RefreshSettings();
                _logger?.LogInfo("[ProtectionChange] EventProtectionService refreshed after delete");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ProtectionChange] Failed to handle event deletion {entityId}: {ex.Message}", ex);
            }
        }

        private async Task RefreshCommandProtectionsAsync(List<string> models)
        {
            if (_commandSync == null)
            {
                _logger?.LogWarning("[ProtectionChange] CommandProtectionSyncService not available");
                return;
            }

            foreach (var modelGuid in models)
            {
                try
                {
                    var commands = await _commandSync.FetchByModelGuidFromApiAsync(modelGuid);
                    _logger?.LogInfo($"[ProtectionChange] Fetched {commands.Count} command protections for model {modelGuid}");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"[ProtectionChange] Failed to fetch command protections for model {modelGuid}: {ex.Message}", ex);
                }
            }

            // Refresh CommandProtectionBinding in-memory cache (reloads from database)
            var binding = _commandProtectionBindingGetter?.Invoke();
            binding?.LoadAndRegisterCommands();
            _logger?.LogInfo("[ProtectionChange] CommandProtectionBinding refreshed");
        }

        private async Task RefreshEventProtectionsAsync(List<string> models)
        {
            if (_eventSync == null)
            {
                _logger?.LogWarning("[ProtectionChange] EventProtectionSyncService not available");
                return;
            }

            foreach (var modelGuid in models)
            {
                try
                {
                    var settings = await _eventSync.FetchByModelGuidFromApiAsync(modelGuid);
                    _logger?.LogInfo($"[ProtectionChange] Fetched {settings.Count} event protections for model {modelGuid}");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"[ProtectionChange] Failed to fetch event protections for model {modelGuid}: {ex.Message}", ex);
                }
            }

            // Refresh in-memory cache once after all fetches
            _eventProtection?.RefreshSettings();
            _logger?.LogInfo("[ProtectionChange] EventProtectionService refreshed");
        }

        /// <summary>
        /// Handles RuleUpdate — fetches rules for each affected model and refreshes the rule engine.
        /// </summary>
        private async Task HandleRuleUpdateAsync(List<string> models)
        {
            if (_rulesSync == null)
            {
                _logger?.LogWarning("[ProtectionChange] RulesSyncService not available");
                return;
            }

            foreach (var modelGuid in models)
            {
                try
                {
                    var rules = await _rulesSync.FetchRuleProtectionsByModelAsync(modelGuid);
                    _logger?.LogInfo($"[ProtectionChange] Fetched {rules.Count} rule protections for model {modelGuid}");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"[ProtectionChange] Failed to fetch rule protections for model {modelGuid}: {ex.Message}", ex);
                }
            }

            // Refresh in-memory rule cache
            if (_ruleService != null)
            {
                try
                {
                    var count = await _ruleService.RefreshRules();
                    _logger?.LogInfo($"[ProtectionChange] RuleService refreshed ({count} rules)");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"[ProtectionChange] Failed to refresh RuleService: {ex.Message}", ex);
                }
            }

            _eventBus.Publish(new ProtectionChangedEvent
            {
                ProtectionType = "Rule",
                ChangeType = "Updated",
                AffectedModelGuids = models,
                Source = Name
            });
        }

        /// <summary>
        /// Handles PinProtectionChange — fetches pin protections for each affected model.
        /// </summary>
        private async Task HandlePinProtectionChangeAsync(List<string> models)
        {
            if (_pinSync == null)
            {
                _logger?.LogWarning("[ProtectionChange] PinProtectionSyncService not available");
                return;
            }

            foreach (var modelGuid in models)
            {
                try
                {
                    var pins = await _pinSync.FetchPinProtectionsByModelAsync(modelGuid);
                    _logger?.LogInfo($"[ProtectionChange] Fetched {pins.Count} pin protections for model {modelGuid}");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"[ProtectionChange] Failed to fetch pin protections for model {modelGuid}: {ex.Message}", ex);
                }
            }

            _eventBus.Publish(new ProtectionChangedEvent
            {
                ProtectionType = "Pin",
                ChangeType = "Updated",
                AffectedModelGuids = models,
                Source = Name
            });
        }

        /// <summary>
        /// On reconnect, do a full catch-up refresh of all protection types so any
        /// broadcasts missed while the WebSocket was down get reconciled. Always
        /// publishes a <see cref="ProtectionChangedEvent"/> for each protection type
        /// at the end so that any open Command / Event / Rule dialog refreshes its
        /// view through the same event-bus path live broadcasts use.
        ///
        /// Without these final publishes, after a transient disconnect dialogs would
        /// keep showing stale data even though the local SQLite was correctly
        /// refreshed — the exact "web → revit works sometimes, sometimes doesn't"
        /// symptom: works during stable connection, fails after any reconnect.
        /// </summary>
        public override void OnReconnected()
        {
            base.OnReconnected();

            Task.Run(async () =>
            {
                try
                {
                    var openDocs = _connectionManager.GetOpenDocuments();
                    var models = openDocs.Select(d => d.ModelGuid).ToList();

                    if (models.Count == 0)
                    {
                        // No tracked Revit docs — skip the per-model REST fetches (they'd
                        // be no-ops anyway). Still publish events so any open dialog can
                        // re-fetch via its own model context.
                        _logger?.LogInfo("[ProtectionChange] Reconnected with no tracked Revit docs — firing dialog refresh events without REST fetch");
                    }
                    else
                    {
                        _logger?.LogInfo($"[ProtectionChange] Reconnected — refreshing all protections for {models.Count} open model(s)");
                        await RefreshCommandProtectionsAsync(models);
                        await RefreshEventProtectionsAsync(models);
                        await HandleRuleUpdateAsync(models);
                        await HandlePinProtectionChangeAsync(models);
                    }

                    // Publish a ProtectionChangedEvent for each type so every open
                    // dialog (Command / Event / Rule / Pin) refreshes via its existing
                    // ProtectionChangedEvent subscription handler — same code path as
                    // live broadcasts use, which keeps reconnect behaviour symmetric
                    // with normal operation. HandlePinProtectionChangeAsync already
                    // publishes Pin internally, but the other three Refresh methods
                    // intentionally don't (they're shared with HandleProtectionSettingsChangeAsync
                    // which does the publish itself in the live-broadcast path).
                    foreach (var type in new[] { "Command", "Event", "Rule" })
                    {
                        _eventBus.Publish(new ProtectionChangedEvent
                        {
                            ProtectionType = type,
                            ChangeType = "Updated",
                            AffectedModelGuids = models,
                            Source = Name
                        });
                    }

                    _logger?.LogInfo("[ProtectionChange] Full protection refresh + event publish completed after reconnect");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"[ProtectionChange] Reconnect refresh failed: {ex.Message}", ex);
                }
            });
        }
    }
}
