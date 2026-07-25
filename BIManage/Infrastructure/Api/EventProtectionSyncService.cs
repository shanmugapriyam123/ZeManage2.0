using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Protection;

namespace BIManage.Infrastructure.Api
{
    /// <summary>
    /// Outcome of a single event-protection toggle attempt against the server.
    /// Lets callers distinguish "toggle stored on server", "queued for retry",
    /// "rejected because user lacks permission for the row's scope" (caller can
    /// fall back to a project-level override), and "fatal — give up".
    /// </summary>
    public enum EventToggleResult
    {
        Success,
        Queued,
        PermissionDenied,
        UnrecoverableFailure
    }

    /// <summary>
    /// Fetches event protection settings from the backend API and persists to local SQLite.
    /// Endpoint: GET /api/v1/Revit/event-protections/by-model/{modelGuid}
    /// </summary>
    public class EventProtectionSyncService
    {
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly EventProtectionRepository? _repository;
        private readonly OfflineQueueRepository? _offlineQueue;
        private readonly ILogger? _logger;

        private const string FetchEndpoint = "/api/v1/Revit/event-protections/by-model";
        private const string Endpoint = "/api/v1/Revit/event-protections";

        /// <summary>
        /// Known dummyCommandIds that this plugin version can enforce.
        /// Protections from the server with IDs not in this set are saved to DB
        /// but flagged as unsupported — they will not be enforced locally.
        /// </summary>
        private static readonly HashSet<string> KnownProtectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Ze_DuplicateUserSessionProtection",
            "Ze_OpenCentralFileProtection",
            "Ze_ModelUpgradeProtection",
            "Ze_SaveOverEarlierFileVersionProtection",
            "Ze_DocumentSaveAsProtection",
            "Ze_DocumentPrintingProtection",
            "Ze_DocumentExportingProtection",
            "Ze_TransferProjectStandardsProtection",
            "Ze_FamilyLibrarySettings",
            "Ze_CADImportProtection",
            "Ze_CADExplodeProtection",
            "Ze_RVTLinkPinPrompt",
            "Ze_EquipmentMirrorProtection",
        };

        /// <summary>
        /// Known eventType values that this plugin version supports.
        /// </summary>
        private static readonly HashSet<int> KnownEventTypes = new HashSet<int> { 1, 2, 3, 4, 5, 6 };

        // Per-row recovery-PUT state. Without this, when the server consistently rejects
        // the PUT (e.g. the PostgreSQL DateTime Kind=Unspecified bug observed
        // 2026-05-07), the fetch cycle fires a recovery PUT every ~30 s for the SAME row
        // forever — 147 PUTs in one minute in the field, generating cascading offline-
        // queue retries. After _recoveryMaxAttempts consecutive failures for a row, that
        // row is marked abandoned for the session. A successful PUT clears the entry.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RecoveryState> _recoveryStateById
            = new System.Collections.Concurrent.ConcurrentDictionary<string, RecoveryState>(StringComparer.OrdinalIgnoreCase);
        private const int _recoveryMaxAttempts = 3;
        private sealed class RecoveryState
        {
            public int FailedAttempts;
            public bool Abandoned;
        }

        // Per-row dedup of "Fetch: preserving local edit" WARN. The condition that triggers
        // the WARN (local timestamp > server timestamp) persists every fetch cycle until
        // the server actually accepts an update — which it can't if the server has a
        // persistent bug. Field log showed 49 identical WARNs in one minute for 4 rows.
        // Log once per row per session; the count surfaces in the message itself.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _preservingWarnedById
            = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        public EventProtectionSyncService(
            AuthenticatedHttpClient? httpClient = null,
            EventProtectionRepository? repository = null,
            ILogger? logger = null,
            OfflineQueueRepository? offlineQueue = null)
        {
            _httpClient = httpClient;
            _repository = repository;
            _offlineQueue = offlineQueue;
            _logger = logger;
            _logger?.LogInfo($"EventProtectionSyncService initialized (HTTP: {(_httpClient != null ? "enabled" : "disabled")}, Repository: {(_repository != null ? "enabled" : "disabled")}, OfflineQueue: {(_offlineQueue != null ? "enabled" : "disabled")})");
        }

        /// <summary>
        /// Fetches event protections for a model from the API.
        /// Returns company-wide, project-level, and model-level protections applicable to the given model.
        /// Persists fetched settings to local SQLite via upsert.
        /// </summary>
        public async Task<List<EventProtectionSettings>> FetchByModelGuidFromApiAsync(string modelGuid, string? fallbackProfileId = null)
        {
            try
            {
                _logger?.LogInfo($"Fetching event protections for model: {modelGuid}");

                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP client not available or not authenticated for fetching event protections");
                    return new List<EventProtectionSettings>();
                }

                var endpoint = $"{FetchEndpoint}/{modelGuid}";
                var response = await _httpClient.GetAsync(endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Fetch event protections failed: {response.StatusCode} - {responseBody}");
                    return new List<EventProtectionSettings>();
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"Fetch event protections response (model {modelGuid}):\n{json}");

                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                List<EventProtectionApiResponse>? apiItems = null;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        apiItems = JsonSerializer.Deserialize<List<EventProtectionApiResponse>>(dataElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("$values", out var valuesElement))
                    {
                        apiItems = JsonSerializer.Deserialize<List<EventProtectionApiResponse>>(valuesElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Array)
                    {
                        apiItems = JsonSerializer.Deserialize<List<EventProtectionApiResponse>>(json, jsonOptions);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to deserialize event protections API response: {ex.Message}", ex);
                    return new List<EventProtectionSettings>();
                }

                // Do NOT short-circuit when the API returns an empty list — that's the
                // signal that an admin deleted all event protections on the web side, and
                // the reconciliation block below MUST still run so corresponding orphan
                // rows in local SQLite get cleared. Returning early here was the cause of
                // "event protection deleted on web still showing in Revit".
                if (apiItems == null) apiItems = new List<EventProtectionApiResponse>();
                if (apiItems.Count == 0)
                    _logger?.LogInfo($"No event protections returned from API for model {modelGuid} — running reconciliation against empty server set (deletes local orphans)");

                var settings = new List<EventProtectionSettings>();

                foreach (var apiItem in apiItems)
                {
                    // Warn (but still include) protections whose dummyCommandId this plugin version
                    // doesn't know how to enforce. User has asked to surface ALL records returned by
                    // the API in the UI — enforcement-layer handles unknown ids gracefully.
                    if (!string.IsNullOrEmpty(apiItem.DummyCommandId) && !KnownProtectionIds.Contains(apiItem.DummyCommandId))
                    {
                        _logger?.LogWarning($"Event protection '{apiItem.DummyCommandId}' (eventType={apiItem.EventType}, " +
                            $"name='{apiItem.ProtectionName}') is not in the known-ids list. Displaying in UI but enforcement may require a plugin update.");
                    }

                    if (!KnownEventTypes.Contains(apiItem.EventType))
                    {
                        _logger?.LogWarning($"Event protection '{apiItem.DummyCommandId}' has unknown eventType={apiItem.EventType}. " +
                            "Saving to DB but enforcement may not work as expected.");
                    }

                    // Diagnostic: what did the server actually return for this row's IsEnabled +
                    // InterventionMode + LevelScope? The log line "Loaded N event protections (0 enabled)"
                    // points at every row coming back as Enabled=false even when the UI shows toggles ON.
                    // This per-row log lets us tell whether the server is returning IsEnabled=false
                    // (server-side persistence bug) or whether the JSON field name doesn't map to our
                    // C# property (deserialization bug).
                    _logger?.LogDebug($"[ApiMap] {apiItem.DummyCommandId}: IsEnabled={apiItem.IsEnabled}, InterventionMode={apiItem.InterventionMode}, LevelScope={apiItem.LevelScope}, EventProtectionId={apiItem.EventProtectionId ?? "<null>"}");

                    var setting = MapFromApiResponse(apiItem);

                    // Fallback: if API didn't return modifiedBy/createdBy, use current user's profile ID
                    if (string.IsNullOrWhiteSpace(setting.ModifiedBy) && !string.IsNullOrWhiteSpace(fallbackProfileId))
                        setting.ModifiedBy = fallbackProfileId;
                    if (string.IsNullOrWhiteSpace(setting.CreatedBy) && !string.IsNullOrWhiteSpace(fallbackProfileId))
                        setting.CreatedBy = fallbackProfileId;

                    settings.Add(setting);
                }

                // Ensure all settings have the model_guid set
                foreach (var setting in settings)
                {
                    if (string.IsNullOrEmpty(setting.ModelGuid))
                        setting.ModelGuid = modelGuid;
                }

                // Persist via upsert with two safeguards against losing user edits:
                //   1. Pending-queue check: if a POST/PUT for this row is still in the
                //      offline queue, the local copy is the newer truth — skip overwrite.
                //   2. Timestamp check: even when nothing is queued (e.g. the PUT got a
                //      2xx but the server didn't actually persist, or replication lag is
                //      returning stale data), if the local row's updated_at is newer than
                //      the server's modifiedAt, the local copy wins. Without this, a Save
                //      followed by Refresh silently reverts the user's edit.
                if (_repository != null)
                {
                    var pendingIds = await GetPendingEventProtectionIdsAsync();
                    var localRows = _repository.GetEventProtectionRowsByModel(modelGuid);
                    // 60-second guard window. Any local row whose updated_at is within this
                    // window from "now" is treated as a fresh user edit that the user just
                    // made via the dialog (toggle / mode change / scope change) and is
                    // preserved against the server's response — even when the server
                    // returns a 200 from the PATCH/PUT but doesn't actually persist the
                    // change (e.g. the JWT-profileId server-side bug we've seen).
                    var freshEditCutoff = DateTime.UtcNow.AddSeconds(-60);
                    var savedCount = 0;
                    var preservedByTimestamp = 0;
                    var preservedByRecency = 0;
                    for (int i = 0; i < settings.Count; i++)
                    {
                        var setting = settings[i];
                        try
                        {
                            bool isLocallyDirty =
                                (!string.IsNullOrEmpty(setting.Id) && pendingIds.Contains(setting.Id))
                                || (!string.IsNullOrEmpty(setting.DummyCommandId) && pendingIds.Contains(setting.DummyCommandId));
                            if (isLocallyDirty)
                            {
                                if (!string.IsNullOrEmpty(setting.Id) && localRows.TryGetValue(setting.Id, out var localQueuedRow))
                                    settings[i] = localQueuedRow; // UI sees the queued edit, not the stale server payload
                                _logger?.LogInfo($"Fetch: preserving pending local edit for event protection '{setting.DummyCommandId}' (queued PUT/POST/PATCH not yet pushed to server)");
                                continue;
                            }

                            // Look up the local row once for the recency + timestamp checks below.
                            EventProtectionSettings? localRow = null;
                            if (!string.IsNullOrEmpty(setting.Id))
                                localRows.TryGetValue(setting.Id, out localRow);

                            // Recency-based preservation. If the local row's updated_at is
                            // within the last 60 s AND is at least as new as the server's
                            // copy, treat it as a fresh user edit and keep it. Covers the
                            // case where the toggle/edit endpoint returned 200 (so nothing
                            // is queued) but the server didn't actually persist the change —
                            // without this guard, refresh would revert the user's edit
                            // because the server's stale payload would win.
                            //
                            // REGRESSION FIX (2026-05-25): the previous check used
                            // "localRow.ModifiedAt.Value >= freshEditCutoff" alone, which
                            // also preserved local rows whose timestamp had been set from
                            // a PREVIOUS server fetch (so local == server timestamp, both
                            // recent). If another user then edited the same row on web,
                            // the next Revit fetch would preserve the stale local copy
                            // because local fell inside the 60 s window — even though the
                            // server now had a newer payload. Symptom: "Web shows new
                            // Modified By, Revit shows old Modified By" right after a
                            // collaborator's edit. 2 s slack absorbs client/server clock skew.
                            if (localRow != null
                                && localRow.ModifiedAt.HasValue
                                && localRow.ModifiedAt.Value >= freshEditCutoff
                                && (!setting.ModifiedAt.HasValue
                                    || localRow.ModifiedAt.Value >= setting.ModifiedAt.Value.AddSeconds(-2)))
                            {
                                _logger?.LogInfo($"Fetch: preserving recent local edit for event protection '{setting.DummyCommandId}' " +
                                    $"(local updated_at={localRow.ModifiedAt:o} ≥ server={setting.ModifiedAt:o}, within 60 s window).");
                                settings[i] = localRow;
                                preservedByRecency++;
                                continue;
                            }

                            // Timestamp-based conflict resolution. If the server's row is older
                            // than what we have locally (e.g. PUT returned 2xx but the server
                            // didn't actually persist the change, or replication lag is serving
                            // stale data), keep the local copy AND swap it into the in-memory
                            // list so the UI shows the local edit, not the stale server data.
                            //
                            // RECOVERY: when the local edit is older than the recency window (60s)
                            // but still newer than the server, the in-flight PUT/POST clearly
                            // didn't land (network drop, app exited mid-sync, server 5xx that
                            // was logged-only without queueing). Re-push the local row to the
                            // server so the user's edit isn't permanently stuck. Without this
                            // recovery, the row is preserved every fetch but never reaches the
                            // server, and the warning fires forever.
                            if (localRow != null
                                && localRow.ModifiedAt.HasValue)
                            {
                                var serverTs = setting.ModifiedAt;
                                var localTs = localRow.ModifiedAt.Value;
                                // 2 s slack to absorb clock skew between client and server.
                                if (serverTs.HasValue && localTs > serverTs.Value.AddSeconds(2))
                                {
                                    // Dedup the WARN — log once per row per session. The condition
                                    // is sticky (server hasn't persisted) so re-warning every cycle
                                    // just spams the log. The recovery-PUT path below also tracks
                                    // its own per-row state, so abandonment is logged separately.
                                    if (!string.IsNullOrEmpty(localRow.Id) && _preservingWarnedById.TryAdd(localRow.Id, 0))
                                    {
                                        _logger?.LogWarning($"Fetch: preserving local edit for event protection '{setting.DummyCommandId}' " +
                                            $"(local updated_at={localTs:o} > server modifiedAt={serverTs:o}). " +
                                            "Server may not have persisted the last PUT yet. " +
                                            "Subsequent occurrences for this row will be silenced this session.");
                                    }
                                    settings[i] = localRow;
                                    preservedByTimestamp++;

                                    // Recovery: outside the recency window, treat this as a
                                    // stuck unsynced edit and re-push it. Inside the window the
                                    // recency branch above already handled it (don't double-fire).
                                    // Per-row throttle: skip if abandoned (consistently failing
                                    // for this row this session) so a server-side bug doesn't
                                    // turn into an infinite client-side retry loop.
                                    if (localTs < freshEditCutoff && !string.IsNullOrEmpty(localRow.Id))
                                    {
                                        var state = _recoveryStateById.GetOrAdd(localRow.Id, _ => new RecoveryState());
                                        if (state.Abandoned)
                                        {
                                            // Silent skip — already logged the abandonment when it tripped.
                                            continue;
                                        }

                                        _logger?.LogInfo($"Fetch: attempting recovery PUT for stuck local edit '{localRow.DummyCommandId}' (id: {localRow.Id}, attempt {state.FailedAttempts + 1}/{_recoveryMaxAttempts})");
                                        _ = Task.Run(async () =>
                                        {
                                            try
                                            {
                                                // Pass the model being fetched as the fallback modelGuid so the
                                                // recovery PUT body always carries a valid uuid even when the
                                                // local row is company/project-scope (its own ModelGuid is null).
                                                var ok = await UpdateEventProtectionAsync(localRow.Id, localRow, modelGuid);
                                                if (ok)
                                                {
                                                    state.FailedAttempts = 0;
                                                    _logger?.LogInfo($"Recovery PUT succeeded for '{localRow.DummyCommandId}' (id: {localRow.Id})");
                                                }
                                                else
                                                {
                                                    var attempts = System.Threading.Interlocked.Increment(ref state.FailedAttempts);
                                                    if (attempts >= _recoveryMaxAttempts)
                                                    {
                                                        state.Abandoned = true;
                                                        _logger?.LogWarning($"Recovery PUT ABANDONED for '{localRow.DummyCommandId}' (id: {localRow.Id}) after {attempts} consecutive failures. " +
                                                            "Will not re-attempt this session — restart Revit or fix the server-side issue to retry.");
                                                    }
                                                    else
                                                    {
                                                        _logger?.LogWarning($"Recovery PUT returned false for '{localRow.DummyCommandId}' (id: {localRow.Id}) — attempt {attempts}/{_recoveryMaxAttempts}.");
                                                    }
                                                }
                                            }
                                            catch (Exception recoverEx)
                                            {
                                                var attempts = System.Threading.Interlocked.Increment(ref state.FailedAttempts);
                                                if (attempts >= _recoveryMaxAttempts)
                                                {
                                                    state.Abandoned = true;
                                                    _logger?.LogWarning($"Recovery PUT ABANDONED for '{localRow.DummyCommandId}' after {attempts} attempts (last error: {recoverEx.Message}).");
                                                }
                                                else
                                                {
                                                    _logger?.LogWarning($"Recovery PUT threw for '{localRow.DummyCommandId}' (attempt {attempts}/{_recoveryMaxAttempts}): {recoverEx.Message}");
                                                }
                                            }
                                        });
                                    }
                                    continue;
                                }
                            }

                            // Pass the server's modifiedAt as the explicit updated_at so the
                            // local row's timestamp matches the server's. Without this, every
                            // fetch bumps updated_at to NOW, then on the NEXT fetch the
                            // "preserve local edit" logic fires (local > server timestamp) and
                            // discards real server-side updates indefinitely.
                            var id = await _repository.SaveEventProtectionAsync(setting, null, setting.ModifiedAt);
                            if (!string.IsNullOrEmpty(id))
                            {
                                setting.Id = id;
                                savedCount++;
                            }
                        }
                        catch (Exception saveEx)
                        {
                            _logger?.LogWarning($"Failed to upsert event protection '{setting.DummyCommandId}' for model {modelGuid}: {saveEx.Message}");
                        }
                    }
                    _logger?.LogInfo($"Fetched {settings.Count} event protections for model {modelGuid}, saved {savedCount} (preserved {pendingIds.Count} queued + {preservedByRecency} fresh-local + {preservedByTimestamp} newer-local)");

                    // Reconcile: delete local rows the server no longer returns. Without
                    // this, an event protection deleted on the web stays in Revit's local
                    // cache forever (the row is never re-fetched and never overwritten,
                    // because INSERT/UPDATE alone never sees the absence). Mirrors the
                    // RulesSyncService pattern with the pending-queue safeguard so a row
                    // the user just authored locally (still in the offline queue) is
                    // preserved instead of being deleted as an "orphan".
                    try
                    {
                        var serverIds = settings
                            .Where(s => !string.IsNullOrEmpty(s.Id))
                            .Select(s => s.Id!)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        // Precise tenant-scoped sweep: model-level for current model +
                        // project-level for current model's project + company-level rows.
                        // The previous "(null)" call swept ALL model_guid-NULL rows
                        // including project-level events belonging to OTHER projects in
                        // the same company, which could wrongly delete them.
                        // companyId passed as null: EventProtectionSyncService doesn't
                        // hold an IUserService dependency — model/project scoping is the
                        // main protection here; cross-tenant cleanup happens via the
                        // company filter applied to the API response itself.
                        var allLocal = await _repository.GetEventProtectionIdsForFetchScopeAsync(modelGuid, null);

                        var orphanCount = 0;
                        var deletedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (localId, dummyCommandId) in allLocal)
                        {
                            if (string.IsNullOrEmpty(localId)) continue;
                            if (deletedIds.Contains(localId)) continue;
                            if (serverIds.Contains(localId)) continue;

                            // Preserve any row the user just authored that hasn't pushed yet.
                            if (pendingIds.Contains(localId)
                                || (!string.IsNullOrEmpty(dummyCommandId) && pendingIds.Contains(dummyCommandId)))
                            {
                                _logger?.LogInfo($"Reconciliation: preserving pending local event protection '{dummyCommandId}' ({localId})");
                                continue;
                            }

                            await _repository.DeleteEventProtectionAsync(localId);
                            deletedIds.Add(localId);
                            orphanCount++;
                            _logger?.LogInfo($"Reconciled: deleted orphaned event protection '{dummyCommandId}' ({localId})");
                        }
                        if (orphanCount > 0)
                            _logger?.LogInfo($"Event protection reconciliation: removed {orphanCount} orphaned record(s)");
                    }
                    catch (Exception reconcileEx)
                    {
                        _logger?.LogDebug($"Event protection reconciliation failed: {reconcileEx.Message}");
                    }
                }
                else
                {
                    _logger?.LogInfo($"Fetched {settings.Count} event protections for model {modelGuid} (no repository)");
                }

                return settings;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch event protections for model {modelGuid}: {ex.Message}", ex);
                return new List<EventProtectionSettings>();
            }
        }

        #region Push Sync Methods (Plugin → Server)

        /// <summary>
        /// Creates a new event protection on the backend (POST).
        /// On failure: queues to offline queue for later retry.
        /// </summary>
        public async Task<bool> CreateEventProtectionAsync(EventProtectionSettings setting)
        {
            try
            {
                var request = MapToCreateRequest(setting);
                _logger?.LogInfo($"Syncing new event protection: {setting.DummyCommandId}");

                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    return await SyncViaHttpPostAsync(request, setting.DummyCommandId);
                }

                // No HTTP client or not authenticated - queue for later
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "POST", OfflineApiWrapper.OperationTypes.EventProtection, setting.DummyCommandId);
                    return true;
                }

                _logger?.LogWarning("No sync transport available for event protection create");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Event protection create sync failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Updates an existing event protection on the backend (PUT).
        /// On failure: queues to offline queue for later retry.
        /// </summary>
        public async Task<bool> UpdateEventProtectionAsync(string eventProtectionId, EventProtectionSettings setting, string? currentModelGuid = null)
        {
            try
            {
                _logger?.LogInfo($"Syncing updated event protection: {setting.DummyCommandId} (id: {eventProtectionId})");

                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    return await SyncViaHttpPutAsync(eventProtectionId, setting, currentModelGuid);
                }

                if (_offlineQueue != null)
                {
                    // Single endpoint: PUT /event-protections/for-Model/{id} with for-Model DTO.
                    var forModelEndpoint = $"{Endpoint}/for-Model/{eventProtectionId}";
                    var forModelPayload = MapToUpdateForModelRequest(setting, currentModelGuid);
                    await QueueForOfflineSyncAsync(forModelPayload, "PUT", OfflineApiWrapper.OperationTypes.EventProtectionUpdate, setting.DummyCommandId, eventProtectionId, overrideEndpoint: forModelEndpoint);
                    return true;
                }

                _logger?.LogWarning("No sync transport available for event protection update");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Event protection update sync failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Deletes an event protection on the backend (DELETE).
        /// On failure: queues to offline queue for later retry.
        /// </summary>
        /// <summary>
        /// Delete an event protection from local DB only (no server call).
        /// Used when processing SignalR Deleted notifications — the server already removed it.
        /// </summary>
        public async Task<bool> DeleteLocalRecordAsync(string eventProtectionId)
        {
            if (_repository == null) return false;
            try
            {
                return await _repository.DeleteEventProtectionAsync(eventProtectionId);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to delete local event protection {eventProtectionId}: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> DeleteEventProtectionAsync(string eventProtectionId, string? dummyCommandId = null)
        {
            try
            {
                _logger?.LogInfo($"Syncing delete event protection: {dummyCommandId ?? "unknown"} (id: {eventProtectionId})");

                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    var ok = await SyncViaHttpDeleteAsync(eventProtectionId);
                    if (ok) return true;

                    // HTTP DELETE returned a non-2xx / non-404 (e.g. transient 5xx, 401,
                    // 403, network glitch). Without a queue fallback the delete would be
                    // lost on the server and the row would reappear on the next fetch —
                    // which is exactly the "delete option not working" symptom. Queue it
                    // so OfflineSyncProcessor retries with a fresh token on its next tick.
                    if (_offlineQueue != null)
                    {
                        _logger?.LogWarning($"Event protection HTTP DELETE failed for {eventProtectionId} — queueing for retry");
                        var request = new CreateEventProtectionRequest { DummyCommandId = dummyCommandId ?? "" };
                        await QueueForOfflineSyncAsync(request, "DELETE", OfflineApiWrapper.OperationTypes.EventProtectionDelete, dummyCommandId ?? "unknown", eventProtectionId);
                        return true;
                    }
                    return false;
                }

                if (_offlineQueue != null)
                {
                    var request = new CreateEventProtectionRequest { DummyCommandId = dummyCommandId ?? "" };
                    await QueueForOfflineSyncAsync(request, "DELETE", OfflineApiWrapper.OperationTypes.EventProtectionDelete, dummyCommandId ?? "unknown", eventProtectionId);
                    return true;
                }

                _logger?.LogWarning("No sync transport available for event protection delete");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Event protection delete sync failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Toggles the enabled state of an event protection on the backend (PATCH).
        /// Endpoint: PATCH /api/v1/Revit/event-protections/for-Model/{eventProtectionId}/is-enabled
        /// On failure: queues to offline queue for later retry.
        /// </summary>
        public async Task<bool> ToggleEventProtectionAsync(string eventProtectionId, bool isEnabled, string? modelGuid)
        {
            var detailed = await ToggleEventProtectionWithResultAsync(eventProtectionId, isEnabled, modelGuid);
            return detailed != EventToggleResult.UnrecoverableFailure;
        }

        /// <summary>
        /// Toggle path that uses the full PUT endpoint (/for-Model/{id}) instead of the
        /// PATCH /is-enabled sub-resource. The backend only broadcasts ProtectionSettingsChange
        /// SignalR events on the PUT handler — the PATCH /is-enabled handler updates the DB
        /// but does NOT push to subscribed clients, leaving the web UI stale after a toggle
        /// from Revit. Routing the toggle through PUT keeps the web in sync in real time.
        /// Caller passes the full EventProtectionSettings (with IsEnabled already flipped).
        /// Returns the same EventToggleResult as the PATCH path so the ViewModel can detect
        /// PermissionDenied and fall back to creating a project-level override.
        /// </summary>
        public async Task<EventToggleResult> UpdateEventProtectionWithResultAsync(
            string eventProtectionId, EventProtectionSettings setting, string? currentModelGuid)
        {
            try
            {
                _logger?.LogInfo($"Syncing toggle (via PUT for SignalR broadcast): {setting.DummyCommandId} (id: {eventProtectionId})");

                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    if (_offlineQueue != null)
                    {
                        var forModelEp = $"{Endpoint}/for-Model/{eventProtectionId}";
                        var forModelPayload = MapToUpdateForModelRequest(setting, currentModelGuid);
                        await QueueForOfflineSyncAsync(forModelPayload, "PUT", OfflineApiWrapper.OperationTypes.EventProtectionUpdate, setting.DummyCommandId, eventProtectionId, overrideEndpoint: forModelEp);
                        return EventToggleResult.Queued;
                    }
                    return EventToggleResult.UnrecoverableFailure;
                }

                var endpoint = $"{Endpoint}/for-Model/{eventProtectionId}";
                var scopedRequest = MapToUpdateForModelRequest(setting, currentModelGuid);
                var response = await _httpClient.PutAsync(endpoint, scopedRequest, requireAdminToken: true);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Event protection toggle PUT succeeded: id {eventProtectionId} (Status: {response.StatusCode})");
                    return EventToggleResult.Success;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning($"Event protection toggle PUT failed: id {eventProtectionId} (Status: {response.StatusCode}, Body: {responseBody})");

                if (IsPermissionDenied(response.StatusCode, responseBody))
                {
                    return EventToggleResult.PermissionDenied;
                }

                if (_offlineQueue != null)
                {
                    var forModelEp = $"{Endpoint}/for-Model/{eventProtectionId}";
                    await QueueForOfflineSyncAsync(scopedRequest, "PUT", OfflineApiWrapper.OperationTypes.EventProtectionUpdate, setting.DummyCommandId, eventProtectionId, overrideEndpoint: forModelEp);
                    return EventToggleResult.Queued;
                }
                return EventToggleResult.UnrecoverableFailure;
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError($"Event protection toggle PUT HTTP error: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    var forModelEp = $"{Endpoint}/for-Model/{eventProtectionId}";
                    var forModelPayload = MapToUpdateForModelRequest(setting, currentModelGuid);
                    await QueueForOfflineSyncAsync(forModelPayload, "PUT", OfflineApiWrapper.OperationTypes.EventProtectionUpdate, setting.DummyCommandId, eventProtectionId, overrideEndpoint: forModelEp);
                    return EventToggleResult.Queued;
                }
                return EventToggleResult.UnrecoverableFailure;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Event protection toggle PUT timeout: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    var forModelEp = $"{Endpoint}/for-Model/{eventProtectionId}";
                    var forModelPayload = MapToUpdateForModelRequest(setting, currentModelGuid);
                    await QueueForOfflineSyncAsync(forModelPayload, "PUT", OfflineApiWrapper.OperationTypes.EventProtectionUpdate, setting.DummyCommandId, eventProtectionId, overrideEndpoint: forModelEp);
                    return EventToggleResult.Queued;
                }
                return EventToggleResult.UnrecoverableFailure;
            }
        }

        /// <summary>
        /// Same as <see cref="ToggleEventProtectionAsync"/> but returns a richer result so the
        /// caller can distinguish a server-confirmed PATCH from a permission failure (server
        /// rejected because the user isn't allowed to toggle the company-level row). Used by
        /// the ViewModel to fall back to creating a project-level override when the direct
        /// PATCH is denied — without this, a Project Admin's toggle would only update local
        /// DB but never reach the server, and the company-wide row stays out of sync.
        /// </summary>
        public async Task<EventToggleResult> ToggleEventProtectionWithResultAsync(string eventProtectionId, bool isEnabled, string? modelGuid)
        {
            try
            {
                // Preserve null when modelGuid is null/empty/not-a-uuid so the DTO's
                // JsonIgnoreCondition.WhenWritingNull drops the field from the wire body.
                // Company-level toggles arrive with modelGuid=null and ship body { isEnabled }.
                // Project/Model toggles arrive with a real uuid and ship body { isEnabled, modelGuid }.
                var normalizedGuid = !string.IsNullOrWhiteSpace(modelGuid) && Guid.TryParse(modelGuid, out _) ? modelGuid : null;
                var request = new SetProtectionIsEnabledRequest { IsEnabled = isEnabled, ModelGuid = normalizedGuid };
                _logger?.LogInfo($"Syncing toggle event protection: id {eventProtectionId}, isEnabled={isEnabled}, modelGuid={normalizedGuid ?? "<omitted>"}");

                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    return await SyncViaHttpPatchToggleWithResultAsync(eventProtectionId, request);
                }

                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "PATCH", OfflineApiWrapper.OperationTypes.EventProtectionToggle, eventProtectionId, eventProtectionId);
                    return EventToggleResult.Queued;
                }

                _logger?.LogWarning("No sync transport available for event protection toggle");
                return EventToggleResult.UnrecoverableFailure;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Event protection toggle sync failed: {ex.Message}", ex);
                return EventToggleResult.UnrecoverableFailure;
            }
        }

        #endregion

        #region HTTP Transport Methods

        private async Task<bool> SyncViaHttpPostAsync(CreateEventProtectionRequest request, string dummyCommandId)
        {
            try
            {
                var response = await _httpClient!.PostAsync(Endpoint, request, requireAdminToken: true);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Event protection synced via HTTP POST: {dummyCommandId} (Status: {response.StatusCode})");
                    return true;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning($"HTTP POST event protection failed: {dummyCommandId} (Status: {response.StatusCode}, Body: {responseBody})");

                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "POST", OfflineApiWrapper.OperationTypes.EventProtection, dummyCommandId);
                    return true;
                }
                return false;
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError($"Event protection POST HTTP error: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "POST", OfflineApiWrapper.OperationTypes.EventProtection, dummyCommandId);
                    return true;
                }
                return false;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Event protection POST timeout: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "POST", OfflineApiWrapper.OperationTypes.EventProtection, dummyCommandId);
                    return true;
                }
                return false;
            }
        }

        private async Task<bool> SyncViaHttpPutAsync(string eventProtectionId, EventProtectionSettings originalSetting, string? currentModelGuid = null)
        {
            var dummyCommandId = originalSetting.DummyCommandId;
            try
            {
                // All event-protection updates (regardless of original scope) go through
                // PUT /api/v1/Revit/event-protections/for-Model/{eventProtectionId}.
                // Wire-format rule (handled inside MapToUpdateForModelRequest):
                //   Company-level row → body omits modelGuid (stays company-scoped).
                //   Project / Model-level row → body includes the row's own GUID, or
                //   falls back to currentModelGuid (the open Revit model) if missing.
                var endpoint = $"{Endpoint}/for-Model/{eventProtectionId}";
                var scopedRequest = MapToUpdateForModelRequest(originalSetting, currentModelGuid);
                var response = await _httpClient!.PutAsync(endpoint, scopedRequest, requireAdminToken: true);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Event protection updated via HTTP PUT: {dummyCommandId} (Status: {response.StatusCode})");
                    return true;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning($"HTTP PUT event protection failed: {dummyCommandId} (Status: {response.StatusCode}, Body: {responseBody})");

                // 404 → record doesn't exist on server; fall back to POST (create)
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger?.LogInfo($"Event protection not found on server (404) — falling back to POST create: {dummyCommandId}");
                    // Build a fresh create-request from the original EventProtectionSettings,
                    // since the strict update DTO no longer carries scope/identity fields.
                    var createRequest = MapToCreateRequest(originalSetting);
                    try
                    {
                        var postResponse = await _httpClient!.PostAsync(Endpoint, createRequest, requireAdminToken: true);
                        if (postResponse.IsSuccessStatusCode)
                        {
                            _logger?.LogInfo($"Event protection created via POST fallback: {dummyCommandId}");
                            return true;
                        }
                        var postBody = await postResponse.Content.ReadAsStringAsync();
                        _logger?.LogWarning($"Event protection POST fallback also failed: {postResponse.StatusCode} - {postBody}");
                    }
                    catch (Exception postEx)
                    {
                        _logger?.LogError($"Event protection POST fallback error: {postEx.Message}", postEx);
                    }

                    if (_offlineQueue != null)
                    {
                        await QueueForOfflineSyncAsync(createRequest, "POST", OfflineApiWrapper.OperationTypes.EventProtection, dummyCommandId);
                        return true;
                    }
                    return false;
                }

                if (_offlineQueue != null)
                {
                    var forModelEp = $"{Endpoint}/for-Model/{eventProtectionId}";
                    var forModelPayload = MapToUpdateForModelRequest(originalSetting, currentModelGuid);
                    await QueueForOfflineSyncAsync(forModelPayload, "PUT", OfflineApiWrapper.OperationTypes.EventProtectionUpdate, dummyCommandId, eventProtectionId, overrideEndpoint: forModelEp);
                    return true;
                }
                return false;
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError($"Event protection PUT HTTP error: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    var forModelEp = $"{Endpoint}/for-Model/{eventProtectionId}";
                    var forModelPayload = MapToUpdateForModelRequest(originalSetting, currentModelGuid);
                    await QueueForOfflineSyncAsync(forModelPayload, "PUT", OfflineApiWrapper.OperationTypes.EventProtectionUpdate, dummyCommandId, eventProtectionId, overrideEndpoint: forModelEp);
                    return true;
                }
                return false;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Event protection PUT timeout: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    var forModelEp = $"{Endpoint}/for-Model/{eventProtectionId}";
                    var forModelPayload = MapToUpdateForModelRequest(originalSetting, currentModelGuid);
                    await QueueForOfflineSyncAsync(forModelPayload, "PUT", OfflineApiWrapper.OperationTypes.EventProtectionUpdate, dummyCommandId, eventProtectionId, overrideEndpoint: forModelEp);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                // Catch-all: any other exception (SocketException, IOException, JsonException
                // wrapping the request body, AggregateException from a fault-on-write Stream,
                // etc.) MUST still queue for retry — otherwise the user's edit is silently
                // lost: the local DB has it, the dialog says "saved", but the server never
                // hears about it and subsequent fetches will discard the local row by the
                // newer-than-server preservation guard.
                _logger?.LogError($"Event protection PUT unexpected error ({ex.GetType().Name}): {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    try
                    {
                        var forModelEp = $"{Endpoint}/for-Model/{eventProtectionId}";
                    var forModelPayload = MapToUpdateForModelRequest(originalSetting, currentModelGuid);
                    await QueueForOfflineSyncAsync(forModelPayload, "PUT", OfflineApiWrapper.OperationTypes.EventProtectionUpdate, dummyCommandId, eventProtectionId, overrideEndpoint: forModelEp);
                        _logger?.LogInfo($"Event protection PUT queued for offline retry: {dummyCommandId} (id: {eventProtectionId})");
                        return true;
                    }
                    catch (Exception queueEx)
                    {
                        _logger?.LogError($"Failed to queue event protection PUT for offline retry: {queueEx.Message}", queueEx);
                    }
                }
                return false;
            }
        }

        private async Task<bool> SyncViaHttpDeleteAsync(string eventProtectionId)
        {
            try
            {
                var endpoint = $"{Endpoint}/{eventProtectionId}";
                var response = await _httpClient!.DeleteAsync(endpoint, requireAdminToken: true);

                if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger?.LogDebug($"Event protection deleted via HTTP DELETE: id {eventProtectionId} (Status: {response.StatusCode})");
                    return true;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning($"HTTP DELETE event protection failed: id {eventProtectionId} (Status: {response.StatusCode}, Body: {responseBody})");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Event protection DELETE error: {ex.Message}", ex);
                return false;
            }
        }

        private async Task<bool> SyncViaHttpPatchToggleAsync(string eventProtectionId, SetProtectionIsEnabledRequest request)
        {
            var result = await SyncViaHttpPatchToggleWithResultAsync(eventProtectionId, request);
            return result != EventToggleResult.UnrecoverableFailure;
        }

        private async Task<EventToggleResult> SyncViaHttpPatchToggleWithResultAsync(string eventProtectionId, SetProtectionIsEnabledRequest request)
        {
            try
            {
                var endpoint = $"{Endpoint}/for-Model/{eventProtectionId}/is-enabled";
                var response = await _httpClient!.PatchAsync(endpoint, request, requireAdminToken: true);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Event protection toggled via HTTP PATCH: id {eventProtectionId} (Status: {response.StatusCode})");
                    return EventToggleResult.Success;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning($"HTTP PATCH event protection toggle failed: id {eventProtectionId} (Status: {response.StatusCode}, Body: {responseBody})");

                // Detect permission denial — server tells us only company admins can toggle
                // the company-level row. Plugin caller can react to this by creating a
                // project-level override instead, so the user's intent isn't lost just
                // because their cached role classification doesn't match the server's.
                if (IsPermissionDenied(response.StatusCode, responseBody))
                {
                    return EventToggleResult.PermissionDenied;
                }

                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "PATCH", OfflineApiWrapper.OperationTypes.EventProtectionToggle, eventProtectionId, eventProtectionId);
                    return EventToggleResult.Queued;
                }
                return EventToggleResult.UnrecoverableFailure;
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError($"Event protection PATCH toggle HTTP error: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "PATCH", OfflineApiWrapper.OperationTypes.EventProtectionToggle, eventProtectionId, eventProtectionId);
                    return EventToggleResult.Queued;
                }
                return EventToggleResult.UnrecoverableFailure;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Event protection PATCH toggle timeout: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "PATCH", OfflineApiWrapper.OperationTypes.EventProtectionToggle, eventProtectionId, eventProtectionId);
                    return EventToggleResult.Queued;
                }
                return EventToggleResult.UnrecoverableFailure;
            }
        }

        /// <summary>
        /// Returns true when the server's response indicates the toggle was rejected because
        /// the user isn't an admin for the row's scope (e.g. Project Admin trying to toggle
        /// a company-level row). Server signals this with HTTP 403 OR HTTP 404 + a body
        /// containing the role-error keyword "company admin" / "RLID001".
        /// </summary>
        private static bool IsPermissionDenied(System.Net.HttpStatusCode status, string? body)
        {
            if (status == System.Net.HttpStatusCode.Forbidden) return true;
            if (status != System.Net.HttpStatusCode.NotFound) return false;
            if (string.IsNullOrEmpty(body)) return false;
            return body.IndexOf("company admin", StringComparison.OrdinalIgnoreCase) >= 0
                || body.IndexOf("RLID001", StringComparison.OrdinalIgnoreCase) >= 0
                || body.IndexOf("only company", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        #endregion

        #region Offline Queue

        /// <summary>
        /// Returns the set of event-protection identifiers (server id + dummyCommandId)
        /// for any operation still pending in the offline queue. Used by FetchByModelGuid
        /// to avoid overwriting a row whose user edit hasn't been pushed to the server
        /// yet — a fresh server fetch would otherwise return the older server value and
        /// erase the local change.
        /// </summary>
        private async Task<HashSet<string>> GetPendingEventProtectionIdsAsync()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_offlineQueue == null) return ids;

            try
            {
                var pending = await _offlineQueue.GetPendingOperationsAsync(500);
                foreach (var op in pending)
                {
                    if (op.OperationType != OfflineApiWrapper.OperationTypes.EventProtection
                        && op.OperationType != OfflineApiWrapper.OperationTypes.EventProtectionUpdate
                        && op.OperationType != OfflineApiWrapper.OperationTypes.EventProtectionToggle)
                        continue;

                    // refer_id is the dummyCommandId or eventProtectionId we passed when enqueueing.
                    if (!string.IsNullOrWhiteSpace(op.SessionId))
                        ids.Add(op.SessionId);

                    if (string.IsNullOrWhiteSpace(op.OperationData)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(op.OperationData);
                        if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                        if (!doc.RootElement.TryGetProperty("payload", out var payloadEl)
                            || payloadEl.ValueKind != JsonValueKind.Object)
                            continue;

                        if (payloadEl.TryGetProperty("eventProtectionId", out var idEl)
                            && idEl.ValueKind == JsonValueKind.String)
                        {
                            var idStr = idEl.GetString();
                            if (!string.IsNullOrWhiteSpace(idStr)) ids.Add(idStr!);
                        }
                        if (payloadEl.TryGetProperty("dummyCommandId", out var dceEl)
                            && dceEl.ValueKind == JsonValueKind.String)
                        {
                            var dc = dceEl.GetString();
                            if (!string.IsNullOrWhiteSpace(dc)) ids.Add(dc!);
                        }
                    }
                    catch { /* malformed payload — skip */ }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"GetPendingEventProtectionIdsAsync failed: {ex.Message}");
            }
            return ids;
        }

        private async Task QueueForOfflineSyncAsync(object request, string httpMethod, string operationType, string identifier, string? entityId = null, string? overrideEndpoint = null)
        {
            try
            {
                string endpoint;
                if (!string.IsNullOrEmpty(overrideEndpoint))
                {
                    // Caller pre-built the for-Model URL. Use it verbatim — do NOT fall back to the
                    // legacy unscoped PUT URL which the server no longer accepts.
                    endpoint = overrideEndpoint!;
                }
                else
                {
                    endpoint = !string.IsNullOrEmpty(entityId) ? $"{Endpoint}/{entityId}" : Endpoint;
                    if (operationType == OfflineApiWrapper.OperationTypes.EventProtectionToggle && !string.IsNullOrEmpty(entityId))
                        endpoint = $"{Endpoint}/for-Model/{entityId}/is-enabled";
                }

                var operationData = new OfflineOperationData
                {
                    Endpoint = endpoint,
                    HttpMethod = httpMethod,
                    Payload = request,
                    CreatedAtUtc = DateTime.UtcNow
                };

                var jsonData = JsonSerializer.Serialize(operationData, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var operation = OfflineOperation.Create(
                    sessionId: identifier,
                    operationType: operationType,
                    operationData: jsonData,
                    priority: 3); // Medium priority

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued offline operation: {operationType} for event protection {identifier}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue offline event protection operation: {ex.Message}", ex);
            }
        }

        #endregion

        #region Mapping Helpers

        // Server expects projectId/modelGuid as Nullable<Guid>. Empty strings or
        // non-GUID values cause a 400, so normalize them to null. Shared by the
        // Create + ForProject + ForModel mappers.
        private static string? NormalizeGuid(string? s) =>
            !string.IsNullOrWhiteSpace(s) && Guid.TryParse(s, out _) ? s : null;

        private static CreateEventProtectionRequest MapToCreateRequest(EventProtectionSettings setting)
        {
            return new CreateEventProtectionRequest
            {
                EventProtectionId = string.IsNullOrEmpty(setting.Id) ? null : setting.Id,
                EventType = (int)setting.EventType,
                DummyCommandId = setting.DummyCommandId,
                ProtectionName = setting.ProtectionName,
                IsEnabled = setting.Enabled,
                // Wire format: 1=Notify, 2=Assist, 3=Protect (matches web MODE_MAP).
                // Local enum is 0-indexed → add 1.
                InterventionMode = (int)setting.Mode + 1,
                CustomMessage = setting.CustomMessage,
                ConfigurationJson = setting.ConfigurationJson,
                CaptureBeforeScreenshot = setting.CaptureBeforeScreenshot,
                CaptureAfterScreenshot = setting.CaptureAfterScreenshot,
                RequireComment = setting.RequireComment,
                AllowAdminOverride = setting.AllowAdminOverride,
                SendEmail = setting.SendEmail,
                // System has only 2 scopes: 1 = Company, 2 = Project. Never 3 — the old
                // mapping emitted 3 when ModelGuid was set, which polluted the server
                // with Model-scope rows that don't match the data model.
                LevelScope = setting.IsCompanyLevel ? 1 : 2,
                ProjectId = NormalizeGuid(setting.ProjectId),
                ModelGuid = NormalizeGuid(setting.ModelGuid)
            };
        }

        private static UpdateEventProtectionForModelRequest MapToUpdateForModelRequest(EventProtectionSettings setting, string? fallbackModelGuid = null)
        {
            // Wire-format rule (server schema):
            //   Company-level row → omit modelGuid (JsonIgnoreCondition.WhenWritingNull drops
            //   it from the body) so the server keeps the row company-scoped.
            //   Project / Model-level row → include the row's own GUID, falling back to the
            //   currently-open Revit model's GUID when the local row lacks one.
            // Sending currentModelGuid for company rows would silently convert them to
            // model-scope on the server; after refresh the company view no longer found
            // them and the edit looked "lost".
            var modelGuid = setting.IsCompanyLevel
                ? null
                : (NormalizeGuid(setting.ModelGuid) ?? NormalizeGuid(fallbackModelGuid));
            return new UpdateEventProtectionForModelRequest
            {
                ModelGuid = modelGuid,
                // 1 = Company, 2 = Project (system has only these two scopes).
                LevelScope = setting.IsCompanyLevel ? 1 : 2,
                EventType = (int)setting.EventType,
                ProtectionName = setting.ProtectionName,
                ToolTips = setting.ToolTips,
                IsEnabled = setting.Enabled,
                InterventionMode = (int)setting.Mode + 1,
                CustomMessage = setting.CustomMessage,
                ConfigurationJson = setting.ConfigurationJson,
                CaptureBeforeScreenshot = setting.CaptureBeforeScreenshot,
                CaptureAfterScreenshot = setting.CaptureAfterScreenshot,
                RequireComment = setting.RequireComment,
                AllowAdminOverride = setting.AllowAdminOverride,
                SendEmail = setting.SendEmail,
                UpdatedAt = NormalizeToUtc(setting.ModifiedAt)
            };
        }

        /// <summary>
        /// Normalises a DateTime to UTC Kind so System.Text.Json serialises it with a Z
        /// suffix (Server's Npgsql driver requires Kind=Utc for 'timestamp with time zone'
        /// columns; Kind=Unspecified is rejected with a 500). Local DB DateTimes load as
        /// Unspecified because SQLite stores ISO 8601 without timezone marker, but the
        /// SQLite values themselves come from datetime('now') which is documented UTC.
        /// </summary>
        private static DateTime? NormalizeToUtc(DateTime? value)
        {
            if (!value.HasValue) return null;
            return value.Value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.Value.ToUniversalTime(),
                // Unspecified — assume the value is already UTC (matches our SQLite write
                // path which uses datetime('now') = UTC) and just stamp the Kind so the
                // serializer emits the Z suffix.
                _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
            };
        }

        #endregion

        private static EventProtectionSettings MapFromApiResponse(EventProtectionApiResponse api)
        {
            // Wire format (matches web MODE_MAP): 1=Notify, 2=Assist, 3=Protect.
            // Local InterventionMode enum is 0-indexed → subtract 1.
            var mode = InterventionMode.Notify;
            if (api.InterventionMode >= 1 && api.InterventionMode <= 3)
                mode = (InterventionMode)(api.InterventionMode - 1);

            // Cache display names from API response
            if (!string.IsNullOrWhiteSpace(api.ModifiedBy) &&
                !string.IsNullOrWhiteSpace(api.ModifiedByDisplayName))
            {
                Core.Identity.UserDisplayNameCache.Set(api.ModifiedBy, api.ModifiedByDisplayName);
            }
            if (!string.IsNullOrWhiteSpace(api.CreatedBy) &&
                !string.IsNullOrWhiteSpace(api.CreatedByDisplayName))
            {
                Core.Identity.UserDisplayNameCache.Set(api.CreatedBy, api.CreatedByDisplayName);
            }

            return new EventProtectionSettings
            {
                Id = api.EventProtectionId ?? string.Empty,
                EventType = (RevitEventType)api.EventType,
                DummyCommandId = api.DummyCommandId ?? string.Empty,
                ProtectionName = api.ProtectionName ?? string.Empty,
                Mode = mode,
                Enabled = api.IsEnabled,
                CustomMessage = api.CustomMessage,
                ConfigurationJson = api.ConfigurationJson,
                CaptureBeforeScreenshot = api.CaptureBeforeScreenshot,
                CaptureAfterScreenshot = api.CaptureAfterScreenshot,
                RequireComment = api.RequireComment,
                AllowAdminOverride = api.AllowAdminOverride,
                SendEmail = api.SendEmail,
                CustomMessageImagePath = api.CustomMessageImagePath,
                IsCompanyLevel = api.LevelScope == 1, // Server scheme: 1 = Company, 2 = Project, 3 = Model
                ProjectId = api.ProjectId,
                ModelGuid = api.ModelGuid,
                // Store display name: try displayName → updatedBy → profileId
                CreatedBy = !string.IsNullOrWhiteSpace(api.CreatedByDisplayName) ? api.CreatedByDisplayName
                           : !string.IsNullOrWhiteSpace(api.CreatedBy) ? api.CreatedBy : null,
                ModifiedBy = !string.IsNullOrWhiteSpace(api.ModifiedByDisplayName) ? api.ModifiedByDisplayName
                            : !string.IsNullOrWhiteSpace(api.UpdatedBy) ? api.UpdatedBy
                            : !string.IsNullOrWhiteSpace(api.ModifiedBy) ? api.ModifiedBy : null,
                ModifiedAt = api.UpdatedAt,
                ToolTips = api.ToolTips,
                HasCompanyScope = api.HasCompanyScope
            };
        }
    }

    #region Event Protection API Response DTO

    /// <summary>
    /// Maps the JSON response from GET /api/v1/Revit/event-protections/by-model/{modelGuid}.
    /// Field names match the backend's RevitEventProtection entity (camelCase via ASP.NET default serialization).
    /// </summary>
    internal class EventProtectionApiResponse
    {
        public string? EventProtectionId { get; set; }
        public int EventType { get; set; }
        public string? DummyCommandId { get; set; }
        public string? ProtectionName { get; set; }
        public bool IsEnabled { get; set; }
        public int InterventionMode { get; set; }
        public string? CustomMessage { get; set; }
        public string? ConfigurationJson { get; set; }
        public bool CaptureBeforeScreenshot { get; set; }
        public bool CaptureAfterScreenshot { get; set; }
        public bool RequireComment { get; set; }
        public bool AllowAdminOverride { get; set; }
        public bool SendEmail { get; set; }
        public string? CustomMessageImagePath { get; set; }
        public int LevelScope { get; set; }
        public string? ProjectId { get; set; }
        public string? ModelGuid { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public string? CreatedByDisplayName { get; set; }
        public string? ModifiedBy { get; set; }
        public string? ModifiedByDisplayName { get; set; }

        [JsonPropertyName("updatedBy")]
        public string? UpdatedBy { get; set; }

        [JsonPropertyName("toolTips")]
        public string? ToolTips { get; set; }

        /// <summary>
        /// Server-computed flag indicating whether a Company-scoped row exists for
        /// this protection. When true, Project Admins are not allowed to disable
        /// the row from Revit (only Company Admins can). The dialog's toggle
        /// handler enforces this and shows a ZeMessageBox if a Project Admin tries.
        /// </summary>
        [JsonPropertyName("hasCompanyScope")]
        public bool HasCompanyScope { get; set; }
    }

    #endregion

    #region Event Protection Push Request DTOs

    /// <summary>
    /// Request body for POST /api/v1/Revit/event-protections (create).
    /// Field names match the backend's CreateEventProtectionRequest schema.
    /// </summary>
    internal class CreateEventProtectionRequest
    {
        [JsonPropertyName("eventProtectionId")] public string? EventProtectionId { get; set; }
        [JsonPropertyName("eventType")] public int EventType { get; set; }
        [JsonPropertyName("dummyCommandId")] public string DummyCommandId { get; set; } = "";
        [JsonPropertyName("protectionName")] public string ProtectionName { get; set; } = "";
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; }
        [JsonPropertyName("interventionMode")] public int InterventionMode { get; set; }
        [JsonPropertyName("customMessage")] public string? CustomMessage { get; set; }
        [JsonPropertyName("configurationJson")] public string? ConfigurationJson { get; set; }
        [JsonPropertyName("captureBeforeScreenshot")] public bool CaptureBeforeScreenshot { get; set; }
        [JsonPropertyName("captureAfterScreenshot")] public bool CaptureAfterScreenshot { get; set; }
        [JsonPropertyName("requireComment")] public bool RequireComment { get; set; }
        [JsonPropertyName("allowAdminOverride")] public bool AllowAdminOverride { get; set; }
        [JsonPropertyName("sendEmail")] public bool SendEmail { get; set; }
        [JsonPropertyName("levelScope")] public int LevelScope { get; set; }
        [JsonPropertyName("projectId")] public string? ProjectId { get; set; }
        [JsonPropertyName("modelGuid")] public string? ModelGuid { get; set; }
    }

    /// <summary>
    /// Request body for PUT /api/v1/Revit/event-protections/for-Model/{eventProtectionId}.
    /// Mirrors the backend's UpdateEventProtectionForModelRequest schema exactly
    /// (verified against staging Swagger 2026-05-13). additionalProperties=false on
    /// the server, so we cannot include projectId here. Required fields: modelGuid,
    /// protectionName.
    /// </summary>
    internal class UpdateEventProtectionForModelRequest
    {
        // Nullable + JsonIgnore-when-null so company-level rows can omit modelGuid entirely
        // from the wire body. Project-scope rows populate this with the open Revit model's
        // GUID; company-scope rows leave it null and the field is dropped from the
        // serialized JSON (matches the wire format shown in Swagger: company → no
        // modelGuid; project → modelGuid present).
        [JsonPropertyName("modelGuid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ModelGuid { get; set; }
        // Required by the server to know which scope the row belongs to. Without it,
        // the PUT body has no scope information and the server can't apply the update
        // to the correct row — symptom: company-level edits "don't stick" after refresh.
        // Values: 1 = Company, 2 = Project.
        [JsonPropertyName("levelScope")] public int LevelScope { get; set; }
        [JsonPropertyName("eventType")] public int EventType { get; set; }
        [JsonPropertyName("protectionName")] public string ProtectionName { get; set; } = "";
        [JsonPropertyName("toolTips")] public string? ToolTips { get; set; }
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; }
        [JsonPropertyName("interventionMode")] public int InterventionMode { get; set; }
        [JsonPropertyName("customMessage")] public string? CustomMessage { get; set; }
        [JsonPropertyName("configurationJson")] public string? ConfigurationJson { get; set; }
        [JsonPropertyName("captureBeforeScreenshot")] public bool CaptureBeforeScreenshot { get; set; }
        [JsonPropertyName("captureAfterScreenshot")] public bool CaptureAfterScreenshot { get; set; }
        [JsonPropertyName("requireComment")] public bool RequireComment { get; set; }
        [JsonPropertyName("allowAdminOverride")] public bool AllowAdminOverride { get; set; }
        [JsonPropertyName("sendEmail")] public bool SendEmail { get; set; }
        [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; set; }
    }

    /// <summary>
    /// Request body for PATCH /api/v1/Revit/event-protections/for-Model/{id}/is-enabled (toggle).
    /// Matches the backend's SetEventProtectionIsEnabledForModelRequest schema
    /// (additionalProperties=false, both fields required).
    /// </summary>
    internal class SetProtectionIsEnabledRequest
    {
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; }
        // Same wire-format rule as the update DTO:
        //   Company-level row → omit modelGuid (the field is dropped from the JSON body).
        //   Project / Model-level row → include the currently-open Revit model's GUID.
        [JsonPropertyName("modelGuid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ModelGuid { get; set; }
    }

    #endregion
}
