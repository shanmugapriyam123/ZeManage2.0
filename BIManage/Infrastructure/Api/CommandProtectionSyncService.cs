using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BIManage.Core.Identity;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Protection;
using BIManageRevit.BIManage.ViewModels.Protection;

namespace BIManage.Infrastructure.Api
{
    /// <summary>
    /// Syncs command protection settings to the backend via HTTP POST/PATCH/DELETE.
    /// Queues to offline_queue if API is unavailable.
    /// </summary>
    public class CommandProtectionSyncService
    {
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly OfflineQueueRepository? _offlineQueue;
        private readonly ILogger? _logger;
        private const string Endpoint = "/api/v1/Revit/command-protections";
        private readonly CommandProtectionRepository? _repository;
        private readonly IUserService? _userService;

        public CommandProtectionSyncService(
            AuthenticatedHttpClient? httpClient = null,
            OfflineQueueRepository? offlineQueue = null,
            ILogger? logger = null,
            CommandProtectionRepository? repository = null,
            IUserService? userService = null)
        {
            _httpClient = httpClient;
            _offlineQueue = offlineQueue;
            _logger = logger;
            _repository = repository;
            _userService = userService;
            _logger?.LogInfo($"CommandProtectionSyncService initialized (HTTP: {(_httpClient != null ? "enabled" : "disabled")}, OfflineQueue: {(_offlineQueue != null ? "enabled" : "disabled")}, UserService: {(_userService != null ? "enabled" : "disabled")})");
        }

        /// <summary>
        /// Returns the currently signed-in user's company id, or null if unauthenticated
        /// / not yet resolved. Used to scope fetches so cross-tenant rows (left over from
        /// a previous sign-in or leaked by the server) are filtered out before they reach
        /// local SQLite.
        /// </summary>
        private string? GetCurrentCompanyId()
        {
            var id = _userService?.CurrentUser?.CompanyId;
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }

        /// <summary>
        /// Syncs a new command protection setting to the backend (POST).
        /// </summary>
        public async Task<bool> SyncNewCommandAsync(CommandProtectionApiRequest request)
        {
            try
            {
                _logger?.LogInfo($"Syncing new command protection: {request.CommandCode}");

                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    return await SyncViaHttpPostAsync(request);
                }

                // No HTTP client or not authenticated - queue for later
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "POST", OfflineApiWrapper.OperationTypes.CommandProtection);
                    return true;
                }

                _logger?.LogWarning("No sync transport available for command protection");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Command protection sync failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Syncs an updated command protection setting to the backend (PUT).
        /// Note: API does not support PATCH — uses PUT for updates.
        /// <paramref name="lookupModelGuid"/>: current document modelGuid for server-id resolution.
        /// </summary>
        public async Task<bool> SyncUpdateCommandAsync(string commandId, CommandProtectionApiRequest request, string? lookupModelGuid = null)
        {
            try
            {
                _logger?.LogInfo($"Syncing updated command protection: {request.CommandCode} (id: {commandId})");

                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    return await SyncViaHttpPutAsync(commandId, request, lookupModelGuid);
                }

                // No HTTP client or not authenticated - queue for later
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "PUT", OfflineApiWrapper.OperationTypes.CommandProtectionUpdate, commandId);
                    return true;
                }

                _logger?.LogWarning("No sync transport available for command protection update");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Command protection update sync failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Syncs an updated command protection setting to the backend (PUT - full replacement).
        /// <paramref name="lookupModelGuid"/> is the CURRENT document's modelGuid used only to
        /// resolve the server's real commandProtectionId via the by-model GET endpoint — it is
        /// NOT included in the PUT payload. Required for Project/Company scope edits where
        /// request.ModelGuid is null but the row was originally fetched from a by-model query.
        /// </summary>
        public async Task<bool> SyncPutCommandAsync(string commandId, CommandProtectionApiRequest request, string? lookupModelGuid = null)
        {
            try
            {
                _logger?.LogInfo($"Syncing PUT command protection: {request.CommandCode} (id: {commandId}, lookupModel: {lookupModelGuid ?? "(none)"})");

                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    return await SyncViaHttpPutAsync(commandId, request, lookupModelGuid);
                }

                // No HTTP client or not authenticated - queue for later
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "PUT", OfflineApiWrapper.OperationTypes.CommandProtectionUpdate, commandId);
                    return true;
                }

                _logger?.LogWarning("No sync transport available for command protection PUT");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Command protection PUT sync failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Syncs a command protection deletion to the backend (DELETE).
        /// </summary>
        public async Task<bool> SyncDeleteCommandAsync(string commandId, string commandCode, string? modelGuid = null)
        {
            try
            {
                _logger?.LogInfo($"Syncing delete command protection: {commandCode} (id: {commandId}, modelGuid: {modelGuid ?? "(none)"})");

                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    // Robust flow: the server assigns its own commandProtectionId which may drift from
                    // the client-generated one. To delete reliably we first GET the current server state
                    // (via the working by-model endpoint), find every record matching the commandCode,
                    // and DELETE each by its real server id.
                    bool deletedAny = false;

                    if (!string.IsNullOrEmpty(modelGuid))
                    {
                        try
                        {
                            var lookupUrl = $"/api/v1/Revit/command-protections/by-model/{modelGuid}";
                            var serverResponse = await _httpClient.GetAsync(lookupUrl);
                            if (serverResponse.IsSuccessStatusCode)
                            {
                                var json = await serverResponse.Content.ReadAsStringAsync();
                                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                                List<CommandProtectionApiRequest>? serverCommands = null;

                                using var doc = JsonDocument.Parse(json);
                                var root = doc.RootElement;
                                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                                    serverCommands = JsonSerializer.Deserialize<List<CommandProtectionApiRequest>>(dataEl.GetRawText(), jsonOptions);
                                else if (root.ValueKind == JsonValueKind.Array)
                                    serverCommands = JsonSerializer.Deserialize<List<CommandProtectionApiRequest>>(json, jsonOptions);

                                if (serverCommands != null)
                                {
                                    var matches = serverCommands
                                        .Where(c => string.Equals(c.CommandCode, commandCode, StringComparison.OrdinalIgnoreCase)
                                                    && !string.IsNullOrEmpty(c.CommandProtectionId))
                                        .ToList();

                                    _logger?.LogInfo($"Delete sync: server has {matches.Count} record(s) with commandCode='{commandCode}' for model {modelGuid}");

                                    foreach (var m in matches)
                                    {
                                        var delEndpoint = $"{Endpoint}/{m.CommandProtectionId}";
                                        var delResp = await _httpClient.DeleteAsync(delEndpoint, requireAdminToken: true);
                                        _logger?.LogInfo($"Delete sync: DELETE {delEndpoint} → {delResp.StatusCode}");
                                        if (delResp.IsSuccessStatusCode) deletedAny = true;
                                    }
                                }
                            }
                            else
                            {
                                _logger?.LogWarning($"Delete sync: by-model GET failed ({serverResponse.StatusCode}). Falling back to plain-id delete.");
                            }
                        }
                        catch (Exception lookupEx)
                        {
                            _logger?.LogWarning($"Delete sync: by-model lookup threw: {lookupEx.Message}. Falling back to plain-id delete.");
                        }
                    }

                    if (deletedAny) return true;

                    // Fallback: try the plain id-based DELETE (may 404 if id has drifted)
                    return await SyncViaHttpDeleteAsync(commandId, commandCode);
                }

                // No HTTP client or not authenticated - queue for later
                if (_offlineQueue != null)
                {
                    var request = new CommandProtectionApiRequest { CommandCode = commandCode };
                    await QueueForOfflineSyncAsync(request, "DELETE", OfflineApiWrapper.OperationTypes.CommandProtectionDelete, commandId);
                    return true;
                }

                _logger?.LogWarning("No sync transport available for command protection delete");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Command protection delete sync failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Delete a command protection from local DB only (no server call).
        /// Used when processing SignalR Deleted notifications — the server already removed it.
        /// </summary>
        public async Task<bool> DeleteLocalRecordAsync(string commandId)
        {
            if (_repository == null) return false;
            try
            {
                return await _repository.DeleteCommandSettingAsync(commandId);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to delete local command protection {commandId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Fetches all command settings from the backend API (GET).
        /// Saves fetched commands to local SQLite database.
        /// </summary>
        public async Task<List<CommandSettingViewModel>> FetchAllFromApiAsync(string? fallbackProfileId = null)
        {
            try
            {
                _logger?.LogInfo("Fetching all command settings from API...");

                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP client not available or not authenticated for fetching command settings");
                    return new List<CommandSettingViewModel>();
                }

                var response = await _httpClient.GetAsync(Endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
                        _logger?.LogInfo($"Fetch command settings: endpoint does not support GET (405) — skipping initial fetch");
                    else
                        _logger?.LogWarning($"Fetch command settings failed: {response.StatusCode} - {responseBody}");
                    return new List<CommandSettingViewModel>();
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"Fetch command settings response:\n{json}");

                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                List<CommandProtectionApiRequest>? apiCommands = null;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        apiCommands = JsonSerializer.Deserialize<List<CommandProtectionApiRequest>>(dataElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("$values", out var valuesElement))
                    {
                        apiCommands = JsonSerializer.Deserialize<List<CommandProtectionApiRequest>>(valuesElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Array)
                    {
                        apiCommands = JsonSerializer.Deserialize<List<CommandProtectionApiRequest>>(json, jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Object)
                    {
                        var single = JsonSerializer.Deserialize<CommandProtectionApiRequest>(root.GetRawText(), jsonOptions);
                        if (single != null)
                            apiCommands = new List<CommandProtectionApiRequest> { single };
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to deserialize command protections API response: {ex.Message}", ex);
                    return new List<CommandSettingViewModel>();
                }

                if (apiCommands == null || apiCommands.Count == 0)
                {
                    _logger?.LogWarning("No command settings returned from API");
                    return new List<CommandSettingViewModel>();
                }

                var commands = new List<CommandSettingViewModel>();
                foreach (var apiCmd in apiCommands)
                {
                    var cmd = MapFromApiResponse(apiCmd);

                    // Same as the by-model branch above: never fall back to the current
                    // viewer's profile ID when the API omits modifiedBy / createdBy. Doing
                    // so makes every fetch rewrite the local DB with the viewer's identity,
                    // so the column flips to whoever opened the dialog most recently (the
                    // exact bug Sujith ↔ Pavish role-switch surfaced). Leave the field
                    // blank when the server hasn't supplied a real value.

                    commands.Add(cmd);
                }

                // Persist fetched commands to local database via upsert (matches event protection pattern)
                if (_repository != null)
                {
                    var savedCount = 0;
                    foreach (var cmd in commands)
                    {
                        try
                        {
                            var id = await _repository.SaveOrUpdateCommandSettingAsync(cmd, null);
                            if (!string.IsNullOrEmpty(id))
                            {
                                cmd.Id = id;
                                savedCount++;
                            }
                        }
                        catch (Exception saveEx)
                        {
                            _logger?.LogWarning($"Failed to upsert command '{cmd.CommandCode}' to local DB: {saveEx.Message}");
                        }
                    }
                    _logger?.LogInfo($"Fetched {commands.Count} command settings from API, saved {savedCount} to local database");

                    // Remove local rows whose command_code is not on the server (handles deletions).
                    // Codes with pending offline-queue operations are preserved — they were just created
                    // locally and haven't been accepted by the server yet.
                    try
                    {
                        var pendingCodes = await GetPendingCommandCodesAsync();
                        var keepCodes = commands
                            .Where(c => !string.IsNullOrEmpty(c.CommandCode))
                            .Select(c => c.CommandCode!)
                            .Concat(pendingCodes)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        if (pendingCodes.Count > 0)
                            _logger?.LogInfo($"Orphan cleanup: preserving {pendingCodes.Count} unsynced code(s): {string.Join(", ", pendingCodes)}");
                        await _repository.DeleteCommandSettingsNotInCodesAsync(keepCodes);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger?.LogWarning($"Orphan cleanup failed: {cleanupEx.Message}");
                    }
                }
                else
                {
                    _logger?.LogInfo($"Fetched {commands.Count} command settings from API (no repository for local save)");
                }

                return commands;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch command settings from API: {ex.Message}", ex);
                return new List<CommandSettingViewModel>();
            }
        }

        /// <summary>
        /// Fetches a single command setting from the backend API by ID (GET).
        /// </summary>
        public async Task<CommandSettingViewModel?> FetchByIdFromApiAsync(string commandSettingsId)
        {
            try
            {
                _logger?.LogInfo($"Fetching command setting from API: {commandSettingsId}");

                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP client not available or not authenticated for fetching command setting");
                    return null;
                }

                var endpoint = $"{Endpoint}/{commandSettingsId}";
                var response = await _httpClient.GetAsync(endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Fetch command setting failed: {response.StatusCode} - {responseBody}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                CommandProtectionApiRequest? apiCommand = null;

                try
                {
                    apiCommand = JsonSerializer.Deserialize<CommandProtectionApiRequest>(json, jsonOptions);
                }
                catch (JsonException)
                {
                    // Try wrapper object (e.g. {"success":true,"data":{...}})
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("data", out var dataElement))
                        {
                            apiCommand = JsonSerializer.Deserialize<CommandProtectionApiRequest>(dataElement.GetRawText(), jsonOptions);
                        }
                    }
                    catch { }
                }

                if (apiCommand == null)
                {
                    _logger?.LogWarning($"Command setting not found on API: {commandSettingsId}");
                    return null;
                }

                _logger?.LogInfo($"Fetched command setting from API: {commandSettingsId}");
                return MapFromApiResponse(apiCommand);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch command setting from API: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Fetches command protection settings for a specific model from the backend API.
        /// Returns company-wide and project-level rules applicable to the given model.
        /// Saves fetched commands to local SQLite via upsert (update or insert).
        /// </summary>
        public async Task<List<CommandSettingViewModel>> FetchByModelGuidFromApiAsync(string modelGuid, string? fallbackProfileId = null)
        {
            try
            {
                _logger?.LogInfo($"Fetching command protections for model: {modelGuid}");

                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP client not available or not authenticated for fetching model command protections");
                    return new List<CommandSettingViewModel>();
                }

                var endpoint = $"/api/v1/Revit/command-protections/by-model/{modelGuid}";
                var response = await _httpClient.GetAsync(endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Fetch model command protections failed: {response.StatusCode} - {responseBody}");
                    return new List<CommandSettingViewModel>();
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"Fetch model command protections response (model {modelGuid}):\n{json}");

                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                List<CommandProtectionApiRequest>? apiCommands = null;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        apiCommands = JsonSerializer.Deserialize<List<CommandProtectionApiRequest>>(dataElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("$values", out var valuesElement))
                    {
                        apiCommands = JsonSerializer.Deserialize<List<CommandProtectionApiRequest>>(valuesElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Array)
                    {
                        apiCommands = JsonSerializer.Deserialize<List<CommandProtectionApiRequest>>(json, jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Object)
                    {
                        var single = JsonSerializer.Deserialize<CommandProtectionApiRequest>(root.GetRawText(), jsonOptions);
                        if (single != null)
                            apiCommands = new List<CommandProtectionApiRequest> { single };
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to deserialize model command protections API response: {ex.Message}", ex);
                    return new List<CommandSettingViewModel>();
                }

                // Tenant-scope filter: drop API rows whose companyId doesn't match the
                // signed-in user. Defense-in-depth against the server's by-model endpoint
                // returning a model's protections without verifying the caller's company —
                // prevents a previous session's data (e.g. Zestine) from leaking into the
                // current session's view (e.g. Conserve). Run BEFORE the empty-server
                // early-return so a fully cross-tenant payload still triggers the cleanup.
                var currentCompanyId = GetCurrentCompanyId();
                if (apiCommands != null && currentCompanyId != null)
                {
                    var preFilter = apiCommands.Count;
                    apiCommands = apiCommands
                        .Where(c => string.IsNullOrWhiteSpace(c.CompanyId)
                                    || string.Equals(c.CompanyId, currentCompanyId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (apiCommands.Count != preFilter)
                        _logger?.LogInfo($"Tenant filter: dropped {preFilter - apiCommands.Count} cross-company command(s) from API response (currentCompany={currentCompanyId})");
                }

                // Cross-tenant local cleanup runs in both the empty-server and populated-server
                // paths below. Performed up here once so both branches benefit without duplication.
                if (_repository != null && currentCompanyId != null)
                {
                    try
                    {
                        var crossTenantDeleted = await _repository.DeleteCommandSettingsByMismatchedCompanyAsync(currentCompanyId);
                        if (crossTenantDeleted > 0)
                            _logger?.LogInfo($"Tenant cleanup: removed {crossTenantDeleted} local command setting(s) from other companies (currentCompany={currentCompanyId})");
                    }
                    catch (Exception tenantEx)
                    {
                        _logger?.LogDebug($"Tenant cleanup (commands) failed: {tenantEx.Message}");
                    }
                }

                if (apiCommands == null || apiCommands.Count == 0)
                {
                    _logger?.LogInfo($"No command protections returned from API for model {modelGuid}");
                    // Server returned empty — reconcile across the full /by-model response
                    // scope (model + company + project rows for the current tenant), not
                    // just rows whose model_guid matches. The narrower model-only sweep
                    // missed company-scoped and project-scoped orphans, so after a web-side
                    // delete those rows stayed stale forever and reappeared in the Revit
                    // dialog even though the server had zero records. Preserve any code
                    // whose POST is still pending in the offline queue.
                    if (_repository != null)
                    {
                        try
                        {
                            var pendingCodes = await GetPendingCommandCodesAsync();
                            var localRecords = await _repository.GetCommandSettingIdsForFetchScopeAsync(modelGuid, currentCompanyId);
                            var orphanCount = 0;
                            foreach (var (localId, localCode) in localRecords)
                            {
                                if (string.IsNullOrEmpty(localCode)) continue;
                                if (pendingCodes.Contains(localCode))
                                {
                                    _logger?.LogInfo($"Orphan cleanup (empty server): preserving unsynced code '{localCode}' (id: {localId})");
                                    continue;
                                }
                                await _repository.DeleteCommandSettingAsync(localId);
                                orphanCount++;
                            }
                            if (orphanCount > 0)
                                _logger?.LogInfo($"Orphan cleanup (empty server): removed {orphanCount} record(s) across model/project/company scope for model {modelGuid}");
                        }
                        catch (Exception cleanupEx) { _logger?.LogWarning($"Orphan cleanup (empty server) failed: {cleanupEx.Message}"); }
                    }
                    return new List<CommandSettingViewModel>();
                }

                var commands = new List<CommandSettingViewModel>();
                foreach (var apiCmd in apiCommands)
                {
                    var vm = MapFromApiResponse(apiCmd);
                    vm.ModelGuid = modelGuid;

                    // Do NOT fall back to the current viewer's profile ID when the API
                    // omits modifiedBy / createdBy. Earlier versions did, which silently
                    // rewrote the local DB on every fetch with the CURRENT user's identity
                    // — so when Sujith opened the dialog every row's "Modified By" became
                    // "Sujith Kumar", and when Pavish opened it the same rows became
                    // "Pavish S". Falsely attributes authorship to whoever happens to be
                    // viewing. Leave the field blank when the server hasn't given us a
                    // real value; the UI shows an empty cell instead of impersonating
                    // whoever last opened the dialog.

                    commands.Add(vm);
                }

                // Persist via upsert to avoid duplicates on repeated document opens
                if (_repository != null)
                {
                    var savedCount = 0;
                    foreach (var cmd in commands)
                    {
                        try
                        {
                            var id = await _repository.SaveOrUpdateCommandSettingAsync(cmd, null);
                            if (!string.IsNullOrEmpty(id))
                            {
                                cmd.Id = id;
                                savedCount++;
                            }
                        }
                        catch (Exception saveEx)
                        {
                            _logger?.LogWarning($"Failed to upsert command '{cmd.CommandCode}' for model {modelGuid}: {saveEx.Message}");
                        }
                    }
                    _logger?.LogInfo($"Fetched {commands.Count} command protections for model {modelGuid}, saved {savedCount} to local DB");

                    // Reconcile across the full /by-model response scope: model + company +
                    // project rows for the current tenant. The narrower model-only sweep
                    // missed company-scoped and project-scoped orphans (those have
                    // model_guid IS NULL) so a row deleted on the web kept reappearing in
                    // Revit. Preserve any code whose create/update is still pending in the
                    // offline queue — those were authored locally and haven't reached the
                    // server yet.
                    try
                    {
                        var serverCodes = commands
                            .Where(c => !string.IsNullOrEmpty(c.CommandCode))
                            .Select(c => c.CommandCode)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var pendingCodes = await GetPendingCommandCodesAsync();
                        var localRecords = await _repository.GetCommandSettingIdsForFetchScopeAsync(modelGuid, currentCompanyId);
                        var orphanCount = 0;
                        foreach (var (localId, localCode) in localRecords)
                        {
                            if (string.IsNullOrEmpty(localCode)) continue;
                            if (serverCodes.Contains(localCode)) continue;
                            if (pendingCodes.Contains(localCode))
                            {
                                _logger?.LogInfo($"Reconciliation: preserving unsynced command protection '{localCode}' (id: {localId})");
                                continue;
                            }
                            await _repository.DeleteCommandSettingAsync(localId);
                            orphanCount++;
                            _logger?.LogInfo($"Reconciled: deleted orphaned command protection '{localCode}' (id: {localId})");
                        }
                        if (orphanCount > 0)
                            _logger?.LogInfo($"Command protection reconciliation: removed {orphanCount} orphaned record(s) across model/project/company scope for model {modelGuid}");
                    }
                    catch (Exception reconcileEx)
                    {
                        _logger?.LogDebug($"Command protection reconciliation failed: {reconcileEx.Message}");
                    }
                }
                else
                {
                    _logger?.LogInfo($"Fetched {commands.Count} command protections for model {modelGuid} (no repository)");
                }

                return commands;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch command protections for model {modelGuid}: {ex.Message}", ex);
                return new List<CommandSettingViewModel>();
            }
        }

        /// <summary>
        /// Returns the set of command codes whose POST/PUT is still pending in the offline queue.
        /// Used to protect locally-created rows that haven't reached the server yet from being
        /// wiped by orphan reconciliation when the server-side fetch returns nothing for them.
        /// </summary>
        private async Task<HashSet<string>> GetPendingCommandCodesAsync()
        {
            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_offlineQueue == null) return codes;

            try
            {
                var pending = await _offlineQueue.GetPendingOperationsAsync(500);
                foreach (var op in pending)
                {
                    if (op.OperationType != OfflineApiWrapper.OperationTypes.CommandProtection
                        && op.OperationType != OfflineApiWrapper.OperationTypes.CommandProtectionUpdate)
                        continue;

                    if (string.IsNullOrWhiteSpace(op.OperationData)) continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(op.OperationData);
                        if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                        if (!doc.RootElement.TryGetProperty("payload", out var payloadEl)
                            || payloadEl.ValueKind != JsonValueKind.Object)
                            continue;

                        if (payloadEl.TryGetProperty("commandCode", out var codeEl)
                            && codeEl.ValueKind == JsonValueKind.String)
                        {
                            var code = codeEl.GetString();
                            if (!string.IsNullOrWhiteSpace(code)) codes.Add(code!);
                        }
                    }
                    catch { /* skip malformed entries */ }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"GetPendingCommandCodesAsync failed: {ex.Message}");
            }

            return codes;
        }

        /// <summary>
        /// Maps an API response DTO back to a CommandSettingViewModel domain model.
        /// If the API returns a modifiedByDisplayName, caches it for future lookups.
        /// </summary>
        private CommandSettingViewModel MapFromApiResponse(CommandProtectionApiRequest apiResponse)
        {
            // Wire format is 1-indexed to match the web dashboard's display:
            //   server: Notify=1, Assist=2, Protect=3
            //   local enum (InterventionMode): Notify=0, Assist=1, Protect=2
            // So a server value of N maps to enum (N - 1).
            // Tolerant fallback: a row written by an older client may have arrived as 0-indexed
            // (0, 1, 2). Accept it directly so we don't visibly mis-render legacy data.
            var mode = InterventionMode.Notify;
            if (apiResponse.InterventionMode >= 1 && apiResponse.InterventionMode <= 3)
            {
                mode = (InterventionMode)(apiResponse.InterventionMode - 1);
            }
            else if (apiResponse.InterventionMode == 0)
            {
                // Legacy 0-indexed wire from a pre-fix client → 0 means Notify.
                mode = InterventionMode.Notify;
            }
            else if (!string.IsNullOrEmpty(apiResponse.Mode))
            {
                if (!Enum.TryParse<InterventionMode>(apiResponse.Mode, true, out mode))
                {
                    _logger?.LogWarning($"Unknown mode '{apiResponse.Mode}' for command '{apiResponse.CommandCode}', defaulting to Notify");
                    mode = InterventionMode.Notify;
                }
            }

            // Cache display names from API response so UserDisplayNameCache resolves them later
            if (!string.IsNullOrWhiteSpace(apiResponse.ModifiedBy) &&
                !string.IsNullOrWhiteSpace(apiResponse.ModifiedByDisplayName))
            {
                Core.Identity.UserDisplayNameCache.Set(apiResponse.ModifiedBy, apiResponse.ModifiedByDisplayName);
            }
            if (!string.IsNullOrWhiteSpace(apiResponse.CreatedBy) &&
                !string.IsNullOrWhiteSpace(apiResponse.CreatedByDisplayName))
            {
                Core.Identity.UserDisplayNameCache.Set(apiResponse.CreatedBy, apiResponse.CreatedByDisplayName);
            }

            return new CommandSettingViewModel
            {
                Id = apiResponse.CommandProtectionId,
                CommandCode = apiResponse.CommandCode ?? "",
                CommandName = apiResponse.CommandName ?? "",
                Mode = mode,
                IsEnabled = apiResponse.IsEnabled,
                IsCompanyLevel = apiResponse.LevelScope == 1, // Server scheme: 1 = Company, 2 = Project, 3 = Model
                CustomMessage = apiResponse.CustomMessage,
                CaptureBeforeScreenshot = apiResponse.CaptureBeforeScreenshot,
                CaptureAfterScreenshot = apiResponse.CaptureAfterScreenshot,
                RequireComment = apiResponse.RequireComment,
                AllowAdminOverride = apiResponse.AllowAdminOverride,
                // Store profile ID in DB; frontend resolves to display name via UserDisplayNameCache.
                // When the server returns ModifiedBy = "System" (server-side automation /
                // seed write with no user-context) but CreatedBy is a real user, store
                // CreatedBy as ModifiedBy so the local DB never holds the "System"
                // sentinel. The web side shows the author's name in this case; matching
                // that here keeps the Modified By column meaningful. The display-layer
                // fallback (CommandProtectionViewModel.LoadCommands) is the second line
                // of defence for rows already in the DB.
                CreatedBy = apiResponse.CreatedBy,
                ModifiedBy =
                    (string.Equals(apiResponse.ModifiedBy, "System", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(apiResponse.CreatedBy)
                     && !string.Equals(apiResponse.CreatedBy, "System", StringComparison.OrdinalIgnoreCase))
                        ? apiResponse.CreatedBy
                        : apiResponse.ModifiedBy,
                ModifiedAt = apiResponse.ModifiedAt,
                SendEmail = apiResponse.SendEmail,
                CustomMessageImagePath = apiResponse.CustomMessageImagePath,
                ModelGuid = apiResponse.ModelGuid,
                CompanyId = apiResponse.CompanyId
            };
        }

        private async Task<bool> SyncViaHttpPostAsync(CommandProtectionApiRequest request)
        {
            try
            {
                System.Net.Http.HttpResponseMessage response;
                // Mutate just around the serialize + send so the wire carries 1-indexed values
                // (server: Notify=1, Assist=2, Protect=3) while in-memory stays 0-indexed
                // (InterventionMode: Notify=0, Assist=1, Protect=2). Finally restores so catch
                // blocks and QueueForOfflineSyncAsync see the original value (no double-offset
                // when the queue's PUT branch also applies +1).
                request.InterventionMode = request.InterventionMode + 1;
                try
                {
                    var jsonPayload = JsonSerializer.Serialize(request, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    });
                    _logger?.LogInfo($"Command protection POST payload:\n{jsonPayload}");
                    response = await _httpClient!.PostAsync(Endpoint, request, requireAdminToken: true);
                }
                finally
                {
                    request.InterventionMode = request.InterventionMode - 1;
                }

                if (response.IsSuccessStatusCode)
                {
                    // Parse server response to extract the server-assigned commandProtectionId.
                    // If the server assigned a different ID than what we sent, update the local DB
                    // so DELETE later uses the correct ID (otherwise delete hits 404 and record
                    // reappears on next refresh).
                    try
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            using var doc = JsonDocument.Parse(body);
                            var root = doc.RootElement;
                            JsonElement dataEl = root;
                            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d))
                                dataEl = d;
                            if (dataEl.ValueKind == JsonValueKind.Object && dataEl.TryGetProperty("commandProtectionId", out var idEl))
                            {
                                var serverId = idEl.GetString();
                                if (!string.IsNullOrWhiteSpace(serverId) && !string.Equals(serverId, request.CommandProtectionId, StringComparison.OrdinalIgnoreCase))
                                {
                                    _logger?.LogInfo($"Server assigned different commandProtectionId: client={request.CommandProtectionId}, server={serverId}. Updating local DB.");
                                    if (_repository != null && !string.IsNullOrEmpty(request.CommandCode))
                                    {
                                        await _repository.UpdateCommandIdByCodeAsync(request.CommandCode!, serverId!);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception parseEx)
                    {
                        _logger?.LogDebug($"Could not parse POST response for server ID: {parseEx.Message}");
                    }

                    _logger?.LogInfo($"Command protection synced via HTTP POST: {request.CommandCode} (Status: {response.StatusCode})");
                    return true;
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"HTTP POST command protection failed: {request.CommandCode} (Status: {response.StatusCode}, Body: {responseBody})");

                    // Queue ALL failed responses for retry
                    if (_offlineQueue != null)
                    {
                        await QueueForOfflineSyncAsync(request, "POST", OfflineApiWrapper.OperationTypes.CommandProtection);
                        return true;
                    }
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError($"Command protection POST HTTP error: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "POST", OfflineApiWrapper.OperationTypes.CommandProtection);
                    return true;
                }
                return false;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Command protection POST timeout: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "POST", OfflineApiWrapper.OperationTypes.CommandProtection);
                    return true;
                }
                return false;
            }
        }

        private async Task<bool> SyncViaHttpPutAsync(string commandId, CommandProtectionApiRequest request, string? lookupModelGuid = null)
        {
            try
            {
                // PUT uses /for-Model/{id} with the trimmed DTO — exactly what was verified
                // to return 200 + "Command protection updated successfully" in the user's
                // Swagger "Try it out" test.
                //
                // Why not the generic /{id} endpoint (despite POST/DELETE working there)?
                // The server's UpdateCommandProtectionRequest schema for that endpoint has
                // additionalProperties=false. Sending the full CommandProtectionApiRequest
                // (with commandCode, commandName, levelScope, projectId, …) triggers a 400
                // because those extra fields are not in the schema. The /for-Model/ endpoint
                // accepts the trimmed UpdateCommandProtectionForModelRequest below — same
                // 12 fields the Swagger working call used.
                //
                // ID DRIFT FIX (same as SyncDeleteCommandAsync): the client-side commandId
                // can differ from the server's real commandProtectionId (server overrides
                // the client-supplied GUID at POST time). Without resolving the real id,
                // PUT targets a non-existent record and silently no-ops — which is exactly
                // what was making edits "revert" on Refresh. Resolve via by-model GET first.
                // Use request.ModelGuid first (Model-scope); fall back to lookupModelGuid
                // (current document) for Project/Company-scope edits whose payload omits modelGuid
                // but whose record was originally fetched via a by-model query.
                var lookupGuid = !string.IsNullOrEmpty(request.ModelGuid) ? request.ModelGuid : lookupModelGuid;
                var resolvedId = commandId;
                if (!string.IsNullOrEmpty(lookupGuid))
                {
                    try
                    {
                        var lookupUrl = $"/api/v1/Revit/command-protections/by-model/{lookupGuid}";
                        var lookup = await _httpClient!.GetAsync(lookupUrl);
                        if (lookup.IsSuccessStatusCode)
                        {
                            var lookupBody = await lookup.Content.ReadAsStringAsync();
                            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                            List<CommandProtectionApiRequest>? serverList = null;
                            using var jdoc = JsonDocument.Parse(lookupBody);
                            var root = jdoc.RootElement;
                            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                                serverList = JsonSerializer.Deserialize<List<CommandProtectionApiRequest>>(dataEl.GetRawText(), opts);
                            else if (root.ValueKind == JsonValueKind.Array)
                                serverList = JsonSerializer.Deserialize<List<CommandProtectionApiRequest>>(lookupBody, opts);

                            var match = serverList?.FirstOrDefault(c =>
                                string.Equals(c.CommandCode, request.CommandCode, StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrEmpty(c.CommandProtectionId));
                            if (match != null && !string.IsNullOrEmpty(match.CommandProtectionId))
                            {
                                if (!string.Equals(resolvedId, match.CommandProtectionId, StringComparison.OrdinalIgnoreCase))
                                    _logger?.LogInfo($"PUT id resolved: local={commandId} → server={match.CommandProtectionId} (commandCode={request.CommandCode})");
                                resolvedId = match.CommandProtectionId!;
                            }
                            else
                            {
                                _logger?.LogWarning($"PUT id lookup: no server record found for commandCode={request.CommandCode} on model {lookupGuid}. Using local id (may 404).");
                            }
                        }
                        else
                        {
                            _logger?.LogWarning($"PUT id lookup: by-model GET failed ({lookup.StatusCode}). Using local id.");
                        }
                    }
                    catch (Exception lookupEx)
                    {
                        _logger?.LogWarning($"PUT id lookup threw: {lookupEx.Message}. Using local id.");
                    }
                }

                var endpoint = $"{Endpoint}/for-Model/{resolvedId}";

                // Per server contract: only Project (levelScope=2) and Model (levelScope=3)
                // send modelGuid in the PUT body. Company (levelScope=1) must OMIT modelGuid
                // entirely — the server stores it as null at that scope, and including it
                // causes the update to misfire. Null + WhenWritingNull strips it from JSON.
                string? payloadModelGuid = null;
                if (request.LevelScope == 2 || request.LevelScope == 3)
                {
                    payloadModelGuid = !string.IsNullOrEmpty(request.ModelGuid)
                        ? request.ModelGuid
                        : lookupModelGuid;
                }

                var payload = new UpdateCommandProtectionForModelRequest
                {
                    ModelGuid = payloadModelGuid,
                    LevelScope = request.LevelScope,
                    IsEnabled = request.IsEnabled,
                    // Convert local 0-indexed enum to 1-indexed wire (Notify=1, Assist=2, Protect=3).
                    InterventionMode = request.InterventionMode + 1,
                    CustomMessage = request.CustomMessage,
                    CaptureBeforeScreenshot = request.CaptureBeforeScreenshot,
                    CaptureAfterScreenshot = request.CaptureAfterScreenshot,
                    RequireComment = request.RequireComment,
                    AllowAdminOverride = request.AllowAdminOverride,
                    SendEmail = request.SendEmail,
                    CustomMessageImagePath = request.CustomMessageImagePath,
                    UpdatedAt = request.ModifiedAt ?? DateTime.UtcNow
                };

                var jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
                _logger?.LogInfo($"Command protection PUT → {endpoint}\n{jsonPayload}");

                var response = await _httpClient!.PutAsync(endpoint, payload, requireAdminToken: true);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Command protection updated via HTTP PUT: {request.CommandCode} (Status: {response.StatusCode}, Body: {responseBody})");
                    return true;
                }

                _logger?.LogWarning($"HTTP PUT command protection failed: {request.CommandCode} (Status: {response.StatusCode}, Body: {responseBody})");

                // 404 → record doesn't exist on server; fall back to POST (create)
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger?.LogInfo($"Command protection not found on server (404) — falling back to POST create: {request.CommandCode}");
                    try
                    {
                        var postResponse = await _httpClient!.PostAsync(Endpoint, request, requireAdminToken: true);
                        if (postResponse.IsSuccessStatusCode)
                        {
                            _logger?.LogInfo($"Command protection created via POST fallback: {request.CommandCode}");
                            return true;
                        }
                        var postBody = await postResponse.Content.ReadAsStringAsync();
                        _logger?.LogWarning($"Command protection POST fallback also failed: {postResponse.StatusCode} - {postBody}");
                    }
                    catch (Exception postEx)
                    {
                        _logger?.LogError($"Command protection POST fallback error: {postEx.Message}", postEx);
                    }

                    // POST also failed — queue as POST for retry
                    if (_offlineQueue != null)
                    {
                        await QueueForOfflineSyncAsync(request, "POST", OfflineApiWrapper.OperationTypes.CommandProtection);
                        return true;
                    }
                    return false;
                }

                // Other non-success — queue as PUT for retry
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "PUT", OfflineApiWrapper.OperationTypes.CommandProtectionUpdate, commandId);
                    return true;
                }
                return false;
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError($"Command protection PUT HTTP error: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "PUT", OfflineApiWrapper.OperationTypes.CommandProtectionUpdate, commandId);
                    return true;
                }
                return false;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Command protection PUT timeout: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "PUT", OfflineApiWrapper.OperationTypes.CommandProtectionUpdate, commandId);
                    return true;
                }
                return false;
            }
        }

        private async Task<bool> SyncViaHttpPatchAsync(string commandId, CommandProtectionApiRequest request)
        {
            try
            {
                var endpoint = $"{Endpoint}/{commandId}";
                var jsonPayload = JsonSerializer.Serialize(request, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
                _logger?.LogInfo($"Command protection PATCH payload:\n{jsonPayload}");

                var response = await _httpClient!.PatchAsync(endpoint, request, requireAdminToken: true);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Command protection updated via HTTP PATCH: {request.CommandCode} (Status: {response.StatusCode})");
                    return true;
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"HTTP PATCH command protection failed: {request.CommandCode} (Status: {response.StatusCode}, Body: {responseBody})");

                    if (_offlineQueue != null)
                    {
                        await QueueForOfflineSyncAsync(request, "PATCH", OfflineApiWrapper.OperationTypes.CommandProtectionUpdate, commandId);
                        return true;
                    }
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogError($"Command protection PATCH HTTP error: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "PATCH", OfflineApiWrapper.OperationTypes.CommandProtectionUpdate, commandId);
                    return true;
                }
                return false;
            }
            catch (TaskCanceledException ex)
            {
                _logger?.LogError($"Command protection PATCH timeout: {ex.Message}", ex);
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(request, "PATCH", OfflineApiWrapper.OperationTypes.CommandProtectionUpdate, commandId);
                    return true;
                }
                return false;
            }
        }

        private async Task<bool> SyncViaHttpDeleteAsync(string commandId, string? commandCode = null)
        {
            try
            {
                var endpoint = $"{Endpoint}/{commandId}";
                var response = await _httpClient!.DeleteAsync(endpoint, requireAdminToken: true);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Command protection deleted via HTTP DELETE: id {commandId} (Status: {response.StatusCode})");
                    return true;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // 404 means the server has no record with THIS id. If we know the command code,
                    // check whether any server record matches and delete it by its real id.
                    _logger?.LogWarning($"HTTP DELETE returned 404 for id {commandId}. Trying to find matching server record by code='{commandCode}'...");
                    if (!string.IsNullOrWhiteSpace(commandCode))
                    {
                        await DeleteByCommandCodeAsync(commandCode);
                    }
                    return true;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning($"HTTP DELETE command protection failed: id {commandId} (Status: {response.StatusCode}, Body: {responseBody})");

                // Queue the DELETE for retry — without this fallback, transient 5xx, 401, or
                // 403 responses silently lose the delete server-side. Symptom: row deleted
                // locally but reappears on the next fetch because the server still has it.
                // Mirrors the EventProtection DELETE retry pattern.
                if (_offlineQueue != null)
                {
                    _logger?.LogWarning($"Command protection HTTP DELETE failed for {commandId} — queueing for retry");
                    var request = new CommandProtectionApiRequest { CommandCode = commandCode };
                    await QueueForOfflineSyncAsync(request, "DELETE", OfflineApiWrapper.OperationTypes.CommandProtectionDelete, commandId);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Command protection DELETE error: {ex.Message}", ex);
                // Network exception too — queue for retry rather than dropping the delete.
                if (_offlineQueue != null)
                {
                    try
                    {
                        var request = new CommandProtectionApiRequest { CommandCode = commandCode };
                        await QueueForOfflineSyncAsync(request, "DELETE", OfflineApiWrapper.OperationTypes.CommandProtectionDelete, commandId);
                        _logger?.LogWarning($"Command protection DELETE error for {commandId} — queued for retry");
                        return true;
                    }
                    catch { /* queue itself failed — fall through */ }
                }
                return false;
            }
        }

        /// <summary>
        /// Fallback when DELETE by id returns 404: fetch all commands from server WITHOUT
        /// upserting them into the local DB (pure read), find any matching the given commandCode,
        /// and delete each by its real server id.
        /// </summary>
        private async Task DeleteByCommandCodeAsync(string commandCode)
        {
            try
            {
                var serverResponse = await _httpClient!.GetAsync(Endpoint);
                if (!serverResponse.IsSuccessStatusCode) return;

                var json = await serverResponse.Content.ReadAsStringAsync();
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                List<CommandProtectionApiRequest>? serverCommands = null;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                        serverCommands = JsonSerializer.Deserialize<List<CommandProtectionApiRequest>>(dataEl.GetRawText(), jsonOptions);
                    else if (root.ValueKind == JsonValueKind.Array)
                        serverCommands = JsonSerializer.Deserialize<List<CommandProtectionApiRequest>>(json, jsonOptions);
                }
                catch { return; }

                if (serverCommands == null) return;

                var matches = serverCommands
                    .Where(c => string.Equals(c.CommandCode, commandCode, StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrEmpty(c.CommandProtectionId))
                    .ToList();

                foreach (var match in matches)
                {
                    var endpoint = $"{Endpoint}/{match.CommandProtectionId}";
                    var response = await _httpClient!.DeleteAsync(endpoint, requireAdminToken: true);
                    _logger?.LogInfo($"Fallback DELETE by code: {commandCode} → server id {match.CommandProtectionId} (Status: {response.StatusCode})");

                    // Also clean local DB rows that match this code (cleanup any stale ids)
                    if (response.IsSuccessStatusCode && _repository != null)
                    {
                        try { await _repository.DeleteCommandSettingAsync(match.CommandProtectionId!); } catch { }
                    }
                }

                // Last resort: also remove any local rows matching this code so Refresh doesn't resurrect them
                if (_repository != null)
                {
                    try { await _repository.DeleteCommandSettingsByCodeAsync(commandCode); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"DeleteByCommandCodeAsync failed for '{commandCode}': {ex.Message}");
            }
        }

        private async Task QueueForOfflineSyncAsync(CommandProtectionApiRequest request, string httpMethod, string operationType, string? commandId = null)
        {
            try
            {
                string endpoint;
                object payload;

                // Must mirror the live HTTP paths so retries hit a 200, not a 400.
                // PUT  → /for-Model/{id} + trimmed UpdateCommandProtectionForModelRequest
                //        (generic /{id} rejects extras via additionalProperties=false).
                // POST → generic endpoint + full CommandProtectionApiRequest (create).
                // DELETE → /{id} (no body needed but kept consistent).
                if (string.Equals(httpMethod, "PUT", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(commandId))
                {
                    endpoint = $"{Endpoint}/for-Model/{commandId}";
                    // Company (1) omits modelGuid; Project (2) and Model (3) include it.
                    string? offlineModelGuid = (request.LevelScope == 2 || request.LevelScope == 3)
                        ? request.ModelGuid
                        : null;
                    payload = new UpdateCommandProtectionForModelRequest
                    {
                        ModelGuid = offlineModelGuid,
                        LevelScope = request.LevelScope,
                        IsEnabled = request.IsEnabled,
                        // Convert local 0-indexed enum to 1-indexed wire (Notify=1, Assist=2, Protect=3).
                        InterventionMode = request.InterventionMode + 1,
                        CustomMessage = request.CustomMessage,
                        CaptureBeforeScreenshot = request.CaptureBeforeScreenshot,
                        CaptureAfterScreenshot = request.CaptureAfterScreenshot,
                        RequireComment = request.RequireComment,
                        AllowAdminOverride = request.AllowAdminOverride,
                        SendEmail = request.SendEmail,
                        CustomMessageImagePath = request.CustomMessageImagePath,
                        UpdatedAt = request.ModifiedAt ?? DateTime.UtcNow
                    };
                }
                else
                {
                    endpoint = !string.IsNullOrEmpty(commandId) ? $"{Endpoint}/{commandId}" : Endpoint;
                    payload = request;
                }

                var operationData = new OfflineOperationData
                {
                    Endpoint = endpoint,
                    HttpMethod = httpMethod,
                    Payload = payload,
                    CreatedAtUtc = DateTime.UtcNow
                };

                // For the POST/DELETE branch (payload == request), bake the 1-indexed wire value
                // into the serialized JSON so the offline replay sends the same value the live POST
                // would send. The PUT branch above already applied + 1 when constructing its DTO,
                // so this only matters when payload is the raw request object.
                bool mutatedRequest = ReferenceEquals(payload, request);
                if (mutatedRequest) request.InterventionMode = request.InterventionMode + 1;
                string jsonData;
                try
                {
                    jsonData = JsonSerializer.Serialize(operationData, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });
                }
                finally
                {
                    if (mutatedRequest) request.InterventionMode = request.InterventionMode - 1;
                }

                var operation = OfflineOperation.Create(
                    sessionId: request.CommandCode ?? "unknown",
                    operationType: operationType,
                    operationData: jsonData,
                    priority: 3); // Medium priority

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued offline operation: {operationType} for command {request.CommandCode}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue offline command protection operation: {ex.Message}", ex);
            }
        }
    }

    #region Command Protection API Request DTO

    /// <summary>
    /// Request body for command protection API.
    /// POST /api/v1/Revit/command-protections (create)
    /// PUT  /api/v1/Revit/command-protections/for-Model/{commandProtectionId} — see <see cref="UpdateCommandProtectionForModelRequest"/>.
    /// Also used for GET response deserialization and offline-queue payload.
    /// </summary>
    public class CommandProtectionApiRequest
    {
        // --- Fields matching CreateCommandProtectionRequest spec ---

        [JsonPropertyName("commandProtectionId")]
        public string? CommandProtectionId { get; set; }

        [JsonPropertyName("commandCode")]
        public string? CommandCode { get; set; }

        [JsonPropertyName("interventionMode")]
        public int InterventionMode { get; set; }

        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; set; }

        [JsonPropertyName("levelScope")]
        public int LevelScope { get; set; }

        [JsonPropertyName("projectId")]
        public string? ProjectId { get; set; }

        // Present on GET responses; ignored on writes (server scopes by JWT). Used by
        // FetchByModelGuidFromApiAsync to filter out cross-tenant rows that the server
        // may have leaked (e.g. by-model endpoint returning a model's rules without
        // checking the caller's company).
        [JsonPropertyName("companyId")]
        public string? CompanyId { get; set; }

        [JsonPropertyName("modelGuid")]
        public string? ModelGuid { get; set; }

        [JsonPropertyName("customMessage")]
        public string? CustomMessage { get; set; }

        [JsonPropertyName("captureBeforeScreenshot")]
        public bool CaptureBeforeScreenshot { get; set; }

        [JsonPropertyName("captureAfterScreenshot")]
        public bool CaptureAfterScreenshot { get; set; }

        [JsonPropertyName("requireComment")]
        public bool RequireComment { get; set; }

        [JsonPropertyName("allowAdminOverride")]
        public bool AllowAdminOverride { get; set; }

        [JsonPropertyName("sendEmail")]
        public bool SendEmail { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime? CreatedAt { get; set; }

        [JsonPropertyName("createdBy")]
        public string? CreatedBy { get; set; }

        [JsonPropertyName("createdByDisplayName")]
        public string? CreatedByDisplayName { get; set; }

        [JsonPropertyName("customMessageImagePath")]
        public string? CustomMessageImagePath { get; set; }

        // --- Extra fields from GET response (not in POST spec, kept for deserialization) ---

        [JsonPropertyName("commandName")]
        public string? CommandName { get; set; }

        /// <summary>Mode as string from GET response (e.g. "Notify", "Guide", "Prevent")</summary>
        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("modifiedBy")]
        public string? ModifiedBy { get; set; }

        [JsonPropertyName("modifiedByDisplayName")]
        public string? ModifiedByDisplayName { get; set; }

        [JsonPropertyName("modifiedAt")]
        public DateTime? ModifiedAt { get; set; }
    }

    /// <summary>
    /// Request body for PUT /api/v1/Revit/command-protections/for-Model/{commandProtectionId}.
    /// Mirrors the backend's UpdateCommandProtectionForModelRequest schema exactly
    /// (Swagger: 11 fields, no levelScope, no projectId — model scope is implied by the URL).
    /// </summary>
    internal class UpdateCommandProtectionForModelRequest
    {
        // Nullable so Company-scope (levelScope=1) edits omit modelGuid from the JSON
        // via JsonIgnoreCondition.WhenWritingNull. Project (2) and Model (3) scopes
        // always send a real GUID.
        [JsonPropertyName("modelGuid")] public string? ModelGuid { get; set; }
        // levelScope is NOT in the published OpenAPI schema for this endpoint, but the
        // server's live PUT handler accepts and uses it to know which scope the update
        // targets (1=Company, 2=Project, 3=Model). Verified via the Swagger "Try it out"
        // round-trip: sending levelScope=1 with modelGuid=<contextModel> returns
        // 200 + "Command protection updated successfully" and the change persists.
        // Without this field every update reverts on refresh because the server
        // defaults to a scope that doesn't match the caller's row.
        [JsonPropertyName("levelScope")] public int LevelScope { get; set; }
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; }
        [JsonPropertyName("interventionMode")] public int InterventionMode { get; set; }
        [JsonPropertyName("customMessage")] public string? CustomMessage { get; set; }
        [JsonPropertyName("captureBeforeScreenshot")] public bool CaptureBeforeScreenshot { get; set; }
        [JsonPropertyName("captureAfterScreenshot")] public bool CaptureAfterScreenshot { get; set; }
        [JsonPropertyName("requireComment")] public bool RequireComment { get; set; }
        [JsonPropertyName("allowAdminOverride")] public bool AllowAdminOverride { get; set; }
        [JsonPropertyName("sendEmail")] public bool SendEmail { get; set; }
        [JsonPropertyName("customMessageImagePath")] public string? CustomMessageImagePath { get; set; }
        [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; set; }
    }

    /// <summary>
    /// Request body for PUT /api/v1/Revit/command-protections/for-Project/{commandProtectionId}.
    /// Server schema has additionalProperties=false, so only these exact fields are accepted.
    /// </summary>
    internal class UpdateCommandProtectionForProjectRequest
    {
        [JsonPropertyName("projectId")] public string ProjectId { get; set; } = "";
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; }
        [JsonPropertyName("interventionMode")] public int InterventionMode { get; set; }
        [JsonPropertyName("customMessage")] public string? CustomMessage { get; set; }
        [JsonPropertyName("captureBeforeScreenshot")] public bool CaptureBeforeScreenshot { get; set; }
        [JsonPropertyName("captureAfterScreenshot")] public bool CaptureAfterScreenshot { get; set; }
        [JsonPropertyName("requireComment")] public bool RequireComment { get; set; }
        [JsonPropertyName("allowAdminOverride")] public bool AllowAdminOverride { get; set; }
        [JsonPropertyName("sendEmail")] public bool SendEmail { get; set; }
        [JsonPropertyName("customMessageImagePath")] public string? CustomMessageImagePath { get; set; }
        [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; set; }
    }

    /// <summary>
    /// Request body for PUT /api/v1/Revit/command-protections/{commandProtectionId}
    /// (generic — kept for backward compatibility, not used by current routing logic).
    /// </summary>
    internal class UpdateCommandProtectionRequest
    {
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; }
        [JsonPropertyName("interventionMode")] public int InterventionMode { get; set; }
        [JsonPropertyName("customMessage")] public string? CustomMessage { get; set; }
        [JsonPropertyName("captureBeforeScreenshot")] public bool CaptureBeforeScreenshot { get; set; }
        [JsonPropertyName("captureAfterScreenshot")] public bool CaptureAfterScreenshot { get; set; }
        [JsonPropertyName("requireComment")] public bool RequireComment { get; set; }
        [JsonPropertyName("allowAdminOverride")] public bool AllowAdminOverride { get; set; }
        [JsonPropertyName("sendEmail")] public bool SendEmail { get; set; }
        [JsonPropertyName("customMessageImagePath")] public string? CustomMessageImagePath { get; set; }
        [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; set; }
    }

    /// <summary>
    /// Request body for PATCH /api/v1/Revit/company-defaults/company/command-protections/{commandProtectionId}
    /// (Company-scope updates). additionalProperties=false on the server, and there is no
    /// updatedAt field in this schema (unlike the for-Project / for-Model variants).
    /// </summary>
    internal class UpdateDefaultCommandProtectionRequest
    {
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; }
        [JsonPropertyName("interventionMode")] public int InterventionMode { get; set; }
        [JsonPropertyName("customMessage")] public string? CustomMessage { get; set; }
        [JsonPropertyName("captureBeforeScreenshot")] public bool CaptureBeforeScreenshot { get; set; }
        [JsonPropertyName("captureAfterScreenshot")] public bool CaptureAfterScreenshot { get; set; }
        [JsonPropertyName("requireComment")] public bool RequireComment { get; set; }
        [JsonPropertyName("allowAdminOverride")] public bool AllowAdminOverride { get; set; }
        [JsonPropertyName("sendEmail")] public bool SendEmail { get; set; }
        [JsonPropertyName("customMessageImagePath")] public string? CustomMessageImagePath { get; set; }
    }

    #endregion
}
