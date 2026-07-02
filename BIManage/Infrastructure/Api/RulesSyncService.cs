using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BIManage.Core.Identity;
using BIManage.Core.Rules.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Core;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.Api
{
    /// <summary>
    /// Syncs local rule protections to the backend API.
    /// POST/PUT/DELETE → /api/v1/Revit/rule-protections.
    /// GET (fetch) → /api/v1/Revit/command-rules.
    /// Primary: SignalR two-way communication.
    /// Fallback: HTTP, then offline queue.
    /// </summary>
    public class RulesSyncService
    {
        private readonly RuleRepository _ruleRepository;
        // Not readonly: AttachSignalR() injects this after construction. The bootstrapper
        // can only resolve ISignalRService AFTER RegisterSignalR has run, which is later
        // than the RegisterApiServices phase that constructs this service.
        private ISignalRService? _signalRService;
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly OfflineQueueRepository? _offlineQueue;
        private readonly IUserService? _userService;
        private readonly ILogger? _logger;
        private const string Endpoint = "/api/v1/Revit/rule-protections";
        private const string RuleProtectionEndpoint = "/api/v1/Revit/rule-protections";
        private const string HubMethod = "SendRuleData";

        public RulesSyncService(
            RuleRepository ruleRepository,
            ISignalRService? signalRService = null,
            AuthenticatedHttpClient? httpClient = null,
            ILogger? logger = null,
            OfflineQueueRepository? offlineQueue = null,
            IUserService? userService = null)
        {
            _ruleRepository = ruleRepository ?? throw new ArgumentNullException(nameof(ruleRepository));
            _signalRService = signalRService;
            _httpClient = httpClient;
            _offlineQueue = offlineQueue;
            _userService = userService;
            _logger = logger;
            _logger?.LogInfo($"RulesSyncService initialized (SignalR: {(_signalRService != null ? "enabled" : "disabled")}, HTTP fallback: {(_httpClient != null ? "enabled" : "disabled")}, OfflineQueue: {(_offlineQueue != null ? "enabled" : "disabled")}, UserService: {(_userService != null ? "enabled" : "disabled")})");
        }

        /// <summary>
        /// Attaches the SignalR service after construction. Called by the bootstrapper
        /// once RegisterSignalR has run — RegisterApiServices (which constructs this
        /// service) runs BEFORE RegisterSignalR and would otherwise hand us null,
        /// permanently disabling the SignalR-first path for rule sync.
        /// </summary>
        public void AttachSignalR(ISignalRService? signalRService)
        {
            _signalRService = signalRService;
            _logger?.LogInfo($"RulesSyncService SignalR attached post-init (SignalR: {(_signalRService != null ? "enabled" : "disabled")})");
        }

        /// <summary>
        /// Returns the currently signed-in user's company id, or null if unauthenticated /
        /// not yet resolved. Used to scope fetches to the active tenant so a previous
        /// session's rules (e.g. signed in to Zestine, then signed in to Conserve) don't
        /// leak into the current company's view.
        /// </summary>
        private string? GetCurrentCompanyId()
        {
            var id = _userService?.CurrentUser?.CompanyId;
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }

        /// <summary>
        /// Syncs a single rule to the backend via POST /api/v1/Revit/rule-protections.
        /// </summary>
        public async Task<bool> SyncRuleAsync(string ruleId)
        {
            try
            {
                _logger?.LogInfo($"Syncing rule: {ruleId}");

                var rule = await _ruleRepository.GetRuleByIdAsync(ruleId);
                if (rule == null)
                {
                    _logger?.LogWarning($"Rule not found in database: {ruleId}");
                    return false;
                }

                var postRequest = MapToRuleProtectionPostRequest(rule);
                return await SyncRuleRequestAsync(postRequest);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Rule sync failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Syncs a rule object directly (without database lookup).
        /// </summary>
        public async Task<bool> SyncRuleAsync(Rule rule)
        {
            try
            {
                _logger?.LogInfo($"Syncing rule: {rule.RuleId} ({rule.Name})");
                var postRequest = MapToRuleProtectionPostRequest(rule);
                return await SyncRuleRequestAsync(postRequest);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Rule sync failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Syncs all rules to the backend.
        /// </summary>
        public async Task<(int succeeded, int failed)> SyncAllRulesAsync()
        {
            int succeeded = 0;
            int failed = 0;

            try
            {
                var rules = await _ruleRepository.GetAllRulesAsync();
                _logger?.LogInfo($"Syncing {rules.Count} rules to backend...");

                foreach (var rule in rules)
                {
                    var result = await SyncRuleAsync(rule);
                    if (result)
                        succeeded++;
                    else
                        failed++;
                }

                _logger?.LogInfo($"Rules sync completed: {succeeded} succeeded, {failed} failed");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to sync all rules: {ex.Message}", ex);
            }

            return (succeeded, failed);
        }

        /// <summary>
        /// Updates an existing rule via PUT /api/v1/Revit/rule-protections/for-Model/{ruleProtectionId}.
        /// Falls back to POST (create) if PUT returns 404 (rule doesn't exist on server yet).
        /// </summary>
        public async Task<bool> UpdateRuleAsync(Rule rule)
        {
            try
            {
                _logger?.LogInfo($"Updating rule via API: {rule.RuleId} ({rule.Name})");
                var updateRequest = MapToUpdateRequest(rule);

                // Try HTTP PUT
                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    var endpoint = $"{RuleProtectionEndpoint}/for-Model/{rule.RuleId}";
                    var response = await _httpClient.PutAsync(endpoint, updateRequest, requireAdminToken: true);

                    if (response.IsSuccessStatusCode)
                    {
                        _logger?.LogInfo($"Rule updated via HTTP PUT: {rule.RuleId} (Status: {response.StatusCode})");
                        return true;
                    }

                    // 404 = rule doesn't exist on server yet — fall back to POST (create)
                    if ((int)response.StatusCode == 404)
                    {
                        _logger?.LogInfo($"Rule {rule.RuleId} not found on server (404), falling back to POST");
                        return await SyncRuleAsync(rule);
                    }

                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Rule PUT failed: {response.StatusCode} - {responseBody}");

                    // Queue non-404 failures for retry
                    if (_offlineQueue != null)
                    {
                        await QueueForOfflineUpdateAsync(rule.RuleId, updateRequest);
                        return true;
                    }

                    return false;
                }

                _logger?.LogWarning("HTTP client not available or not authenticated for rule update");

                // Queue for later
                if (_offlineQueue != null)
                {
                    await QueueForOfflineUpdateAsync(rule.RuleId, updateRequest);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Rule update failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Deletes a rule via DELETE /api/v1/Revit/command-rules/{ruleId}
        /// </summary>
        public async Task<bool> DeleteRuleAsync(string ruleId)
        {
            try
            {
                _logger?.LogInfo($"Deleting rule via API: {ruleId}");

                // Try HTTP DELETE
                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    return await DeleteViaHttpAsync(ruleId);
                }

                _logger?.LogWarning("HTTP client not available or not authenticated for rule delete");

                // Queue for later
                if (_offlineQueue != null)
                {
                    await QueueForOfflineDeleteAsync(ruleId);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Rule delete failed: {ex.Message}", ex);
                return false;
            }
        }

        private async Task<bool> DeleteViaHttpAsync(string ruleId)
        {
            try
            {
                var endpoint = $"{RuleProtectionEndpoint}/{ruleId}";
                _logger?.LogInfo($"Rule DELETE request to {endpoint}");

                var response = await _httpClient!.DeleteAsync(endpoint, requireAdminToken: true);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Rule deleted via HTTP DELETE: {ruleId} (Status: {response.StatusCode})");
                    return true;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning($"Rule DELETE failed: {response.StatusCode} - {responseBody}");

                // Queue ALL failed responses for retry
                if (_offlineQueue != null)
                {
                    await QueueForOfflineDeleteAsync(ruleId);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"HTTP DELETE rule failed: {ex.Message}", ex);

                if (_offlineQueue != null)
                {
                    await QueueForOfflineDeleteAsync(ruleId);
                    return true;
                }

                return false;
            }
        }

        private async Task QueueForOfflineUpdateAsync(string ruleId, RuleUpdateApiRequest updateRequest)
        {
            try
            {
                var operationData = new OfflineOperationData
                {
                    Endpoint = $"{RuleProtectionEndpoint}/for-Model/{ruleId}",
                    HttpMethod = "PUT",
                    Payload = updateRequest,
                    CreatedAtUtc = DateTime.UtcNow
                };

                var jsonData = JsonSerializer.Serialize(operationData, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var operation = OfflineOperation.Create(
                    sessionId: ruleId,
                    operationType: OfflineApiWrapper.OperationTypes.RuleSync,
                    operationData: jsonData,
                    priority: 3);

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued offline PUT operation for rule {ruleId}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue offline PUT: {ex.Message}", ex);
            }
        }

        private async Task QueueForOfflineDeleteAsync(string ruleId)
        {
            try
            {
                var operationData = new OfflineOperationData
                {
                    Endpoint = $"{RuleProtectionEndpoint}/{ruleId}",
                    HttpMethod = "DELETE",
                    Payload = null,
                    CreatedAtUtc = DateTime.UtcNow
                };

                var jsonData = JsonSerializer.Serialize(operationData, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var operation = OfflineOperation.Create(
                    sessionId: ruleId,
                    operationType: OfflineApiWrapper.OperationTypes.RuleSync,
                    operationData: jsonData,
                    priority: 3);

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued offline DELETE operation for rule {ruleId}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue offline DELETE: {ex.Message}", ex);
            }
        }

        private async Task<bool> SyncRuleRequestAsync(RuleProtectionPostRequest postRequest)
        {
            // HTTP-FIRST (2026-05-24): Mirror the working Command/Event Protection
            // pattern — those services never send via SignalR from Revit and the
            // server reliably broadcasts a RuleUpdate to web subscribers on the HTTP
            // write. The previous SignalR-first path routed POSTs through the hub
            // method `SendRuleData`, which the server does NOT translate into a
            // RuleUpdate broadcast, so web clients never saw the new rule until a
            // manual refresh. Going HTTP-first restores Revit→Web real-time updates
            // without touching any of the (frozen) Command/Event/SignalR code.
            if (_httpClient != null && _httpClient.IsAuthenticated)
            {
                return await SyncViaHttpAsync(postRequest);
            }

            if (_httpClient != null && !_httpClient.IsAuthenticated)
            {
                _logger?.LogWarning("HTTP send skipped - no auth tokens available (register device first)");
            }

            // Queue for later if no authenticated client
            if (_offlineQueue != null)
            {
                await QueueForOfflineSyncAsync(postRequest);
                _logger?.LogInfo($"Queued rule for later sync: {postRequest.RuleProtectionId}");
                return true;
            }

            _logger?.LogWarning("No sync transport available for rule");
            return false;
        }

        private async Task<bool> SyncViaSignalRAsync(RuleProtectionPostRequest postRequest)
        {
            try
            {
                var message = SignalRMessageInfo.Create(SignalRMethods.RuleUpdate, postRequest);
                message.SenderSessionId = postRequest.RuleProtectionId;

                await _signalRService!.SendAsync(HubMethod, message);

                _logger?.LogInfo($"Rule synced via SignalR: {postRequest.RuleProtectionId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR rule sync failed: {ex.Message}", ex);

                // Fallback to HTTP
                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    _logger?.LogInfo("SignalR failed - falling back to HTTP POST");
                    return await SyncViaHttpAsync(postRequest);
                }

                return false;
            }
        }

        private async Task<bool> SyncViaHttpAsync(RuleProtectionPostRequest postRequest)
        {
            try
            {
                var jsonPayload = JsonSerializer.Serialize(postRequest, GetJsonOptions());
                _logger?.LogInfo($"Rule POST to {RuleProtectionEndpoint}:\n{jsonPayload}");

                var response = await _httpClient!.PostAsync(RuleProtectionEndpoint, postRequest, requireAdminToken: true);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Rule synced via HTTP POST: {postRequest.RuleProtectionId} (Status: {response.StatusCode})");
                    return true;
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Rule HTTP POST failed: {response.StatusCode} - {responseBody}");

                    // Queue ALL failed responses for retry
                    if (_offlineQueue != null)
                    {
                        await QueueForOfflineSyncAsync(postRequest);
                        _logger?.LogInfo($"Queued rule for retry: {postRequest.RuleProtectionId}");
                        return true;
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"HTTP rule POST failed: {ex.Message}", ex);

                // Queue for retry on exception
                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(postRequest);
                    _logger?.LogInfo($"Queued rule for retry after error: {postRequest.RuleProtectionId}");
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Returns the set of rule ids whose latest user edit is still pending in the
        /// offline queue. Used by FetchRuleProtectionsByModelAsync to avoid overwriting
        /// or deleting a row whose user edit hasn't been pushed to the server yet —
        /// otherwise a fresh fetch would erase the local change (e.g. mode flip,
        /// enable/disable toggle) before the queued operation had a chance to run.
        /// </summary>
        private async Task<HashSet<string>> GetPendingRuleIdsAsync()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_offlineQueue == null) return ids;

            try
            {
                var pending = await _offlineQueue.GetPendingOperationsAsync(500);
                foreach (var op in pending)
                {
                    if (op.OperationType != OfflineApiWrapper.OperationTypes.RuleSync) continue;

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

                        if (payloadEl.TryGetProperty("ruleProtectionId", out var idEl)
                            && idEl.ValueKind == JsonValueKind.String)
                        {
                            var idStr = idEl.GetString();
                            if (!string.IsNullOrWhiteSpace(idStr)) ids.Add(idStr!);
                        }
                        if (payloadEl.TryGetProperty("ruleId", out var ridEl)
                            && ridEl.ValueKind == JsonValueKind.String)
                        {
                            var ridStr = ridEl.GetString();
                            if (!string.IsNullOrWhiteSpace(ridStr)) ids.Add(ridStr!);
                        }
                    }
                    catch { /* malformed payload — skip */ }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"GetPendingRuleIdsAsync failed: {ex.Message}");
            }
            return ids;
        }

        private async Task QueueForOfflineSyncAsync(RuleProtectionPostRequest postRequest)
        {
            try
            {
                var operationData = new OfflineOperationData
                {
                    Endpoint = RuleProtectionEndpoint,
                    HttpMethod = "POST",
                    Payload = postRequest,
                    CreatedAtUtc = DateTime.UtcNow
                };

                var jsonData = JsonSerializer.Serialize(operationData, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var operation = OfflineOperation.Create(
                    sessionId: postRequest.RuleProtectionId ?? Guid.NewGuid().ToString(),
                    operationType: OfflineApiWrapper.OperationTypes.RuleSync,
                    operationData: jsonData,
                    priority: 3);

                await _offlineQueue!.EnqueueAsync(operation);
                _logger?.LogInfo($"Queued offline POST for rule {postRequest.RuleProtectionId}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to queue offline operation: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Creates a project-level override rule via POST /api/v1/Revit/rule-protections.
        /// Used when a project admin modifies or deletes a company-wide rule.
        /// </summary>
        public async Task<bool> PostRuleProtectionOverrideAsync(Rule rule)
        {
            try
            {
                _logger?.LogInfo($"Posting rule protection override: {rule.RuleId} ({rule.Name})");
                var postRequest = MapToRuleProtectionPostRequest(rule);

                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP client not available or not authenticated for rule protection override");

                    if (_offlineQueue != null)
                    {
                        await QueueForOfflineSyncAsync(postRequest);
                        _logger?.LogInfo($"Queued offline POST rule-protection override: {rule.RuleId}");
                        return true;
                    }

                    return false;
                }

                var response = await _httpClient.PostAsync(RuleProtectionEndpoint, postRequest, requireAdminToken: true);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"Rule protection override posted: {rule.RuleId} (Status: {response.StatusCode})");
                    return true;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning($"Rule protection override POST failed: {response.StatusCode} - {responseBody}");

                if (_offlineQueue != null)
                {
                    await QueueForOfflineSyncAsync(postRequest);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Rule protection override failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Fetches rule protections for a specific model via GET /api/v1/Revit/rule-protections/by-model/{modelGuid}.
        /// Returns company-wide + project + model-specific rules that apply to this model.
        /// Persists fetched rules to local database.
        /// </summary>
        public async Task<List<Rule>> FetchRuleProtectionsByModelAsync(string modelGuid)
        {
            try
            {
                // Snapshot local rule count BEFORE the fetch so we can detect any drop
                // caused by anything in this method (overwrite, tenant cleanup, etc.).
                var preFetchLocal = await _ruleRepository.GetAllRulesAsync();
                _logger?.LogInfo($"Rule fetch START: local DB has {preFetchLocal.Count} rule(s) BEFORE fetch (model={modelGuid})");

                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP client not available or not authenticated for fetching rule protections");
                    return new List<Rule>();
                }

                var endpoint = $"{RuleProtectionEndpoint}/by-model/{modelGuid}";
                var response = await _httpClient.GetAsync(endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Fetch rule protections failed: {response.StatusCode} - {responseBody}");
                    return new List<Rule>();
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"Fetch rule protections response:\n{json}");

                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                List<RuleProtectionApiResponse>? apiRules = null;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // Standard wrapper: { "success": true, "data": [...] }
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        apiRules = JsonSerializer.Deserialize<List<RuleProtectionApiResponse>>(dataElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Array)
                    {
                        apiRules = JsonSerializer.Deserialize<List<RuleProtectionApiResponse>>(json, jsonOptions);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to deserialize rule protections response: {ex.Message}", ex);
                    return new List<Rule>();
                }

                // Do NOT short-circuit when the API returns an empty list — that's the
                // signal that the admin deleted all rules on the web side, and the
                // reconciliation block below MUST still run so the corresponding orphan
                // rows in local SQLite get cleared. Returning early here was the cause
                // of "rules deleted on web still showing in Revit".
                if (apiRules == null) apiRules = new List<RuleProtectionApiResponse>();
                if (apiRules.Count == 0)
                    _logger?.LogInfo("No rule protections returned from API — running reconciliation against empty server set (deletes local orphans)");

                // Tenant-scope filter: drop any API rows whose companyId doesn't match the
                // currently signed-in user. The server SHOULD already scope by JWT claims,
                // but a defense-in-depth filter here prevents a cross-tenant response (e.g.
                // a model registered under a different company) from polluting this user's
                // local cache. Rows with no companyId are kept (system defaults / pre-scope data).
                var currentCompanyId = GetCurrentCompanyId();
                if (currentCompanyId != null)
                {
                    var preFilter = apiRules.Count;
                    apiRules = apiRules
                        .Where(r => string.IsNullOrWhiteSpace(r.CompanyId)
                                    || string.Equals(r.CompanyId, currentCompanyId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (apiRules.Count != preFilter)
                        _logger?.LogInfo($"Tenant filter: dropped {preFilter - apiRules.Count} cross-company rule(s) from API response (currentCompany={currentCompanyId})");
                }

                // Dedupe the API response BEFORE mapping. Two duplication sources are
                // guarded here — both lead to "same rule shown 3× in Revit" when the
                // user edits on the web:
                //   (a) Missing RuleProtectionId on the wire. MapFromRuleProtectionResponse
                //       fell back to Guid.NewGuid() in that case, producing a brand-new
                //       phantom id on EVERY fetch — that row could never reconcile
                //       against itself next time, so it accumulated.
                //   (b) Duplicate RuleProtectionId rows in the same response payload
                //       (e.g. server returns both the pre-edit and post-edit version
                //       of one rule). Saving both leaves the cache with two rows that
                //       reconciliation can't tell apart from legitimate distinct rules.
                // Skipping both cases here is safer than mutating the save/reconcile
                // logic downstream.
                var seenRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var rules = new List<Rule>();
                foreach (var apiRule in apiRules)
                {
                    if (string.IsNullOrWhiteSpace(apiRule.RuleProtectionId))
                    {
                        _logger?.LogWarning(
                            $"Fetch: skipping API rule with missing RuleProtectionId " +
                            $"(category={apiRule.CategoryCode}, mode={apiRule.Mode}). " +
                            "Generating a fake GUID here would create a phantom row that " +
                            "duplicates the rule on every fetch.");
                        continue;
                    }
                    if (!seenRuleIds.Add(apiRule.RuleProtectionId))
                    {
                        _logger?.LogInfo(
                            $"Fetch: skipping duplicate RuleProtectionId {apiRule.RuleProtectionId} " +
                            $"in API response (category={apiRule.CategoryCode}).");
                        continue;
                    }
                    rules.Add(MapFromRuleProtectionResponse(apiRule));
                }

                // Build the set of rule ids whose latest user edit is still pending in the
                // offline queue. We must NOT overwrite their local rows with the older
                // server values during this fetch — the queued PUT/POST will push them
                // to the server later, and a fresh fetch should not erase them in the
                // meantime (e.g. mode Protect → Assist edit would otherwise revert).
                var pendingRuleIds = await GetPendingRuleIdsAsync();

                // Build a lookup of local rules by id so the recency + timestamp preservation
                // checks below run in O(1) per server row instead of N hits to SQLite.
                var localById = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
                foreach (var lr in preFetchLocal)
                {
                    if (!string.IsNullOrEmpty(lr.RuleId))
                        localById[lr.RuleId] = lr;
                }

                // Mirror the Event Protection freshness-window pattern (the reference
                // implementation other sync services should follow). If the local row's
                // ModifiedAt is within the last 60 s, treat it as a fresh user edit and
                // SKIP the server overwrite. Covers the case where a PUT returned 2xx
                // (so the row isn't queued anywhere) but the server didn't actually persist
                // the change — without this guard, every dialog reopen would revert the
                // edit because the server's stale payload would win.
                var freshEditCutoff = DateTime.UtcNow.AddSeconds(-60);

                // Persist fetched rules to local database
                var savedCount = 0;
                var preservedByRecency = 0;
                var preservedByTimestamp = 0;
                foreach (var rule in rules)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(rule.RuleId) && pendingRuleIds.Contains(rule.RuleId))
                        {
                            _logger?.LogInfo($"Fetch: preserving pending local edit for rule '{rule.RuleId}' (queued PUT/POST not yet pushed to server)");
                            continue;
                        }

                        Rule? localRow = null;
                        if (!string.IsNullOrEmpty(rule.RuleId))
                            localById.TryGetValue(rule.RuleId, out localRow);

                        // Defensive normalization: SQLite roundtrip yields Kind=Unspecified
                        // even when we wrote UTC, so coerce both sides to UTC before
                        // comparing. Without this, the comparison can be off by the local
                        // TZ offset and the windows misfire.
                        if (localRow != null)
                        {
                            var localUtc = localRow.ModifiedAt.Kind switch
                            {
                                DateTimeKind.Utc => localRow.ModifiedAt,
                                DateTimeKind.Local => localRow.ModifiedAt.ToUniversalTime(),
                                _ => DateTime.SpecifyKind(localRow.ModifiedAt, DateTimeKind.Utc),
                            };
                            var serverUtc = rule.ModifiedAt.Kind switch
                            {
                                DateTimeKind.Utc => rule.ModifiedAt,
                                DateTimeKind.Local => rule.ModifiedAt.ToUniversalTime(),
                                _ => DateTime.SpecifyKind(rule.ModifiedAt, DateTimeKind.Utc),
                            };

                            // Sanity bound: if local appears more than 15 minutes newer than
                            // server, it's almost certainly a stored-as-local-wall-clock
                            // roundtrip rather than a real edit (legitimate stuck edits
                            // retry every 30 s via OfflineSyncProcessor, so they don't get
                            // that far ahead). Skip both preservation paths and let the
                            // server's value win — that also self-heals the corrupted
                            // timestamp on disk via the SaveRuleAsync UTC-storage fix.
                            var aheadByMinutes = (localUtc - serverUtc).TotalMinutes;
                            if (aheadByMinutes > 15)
                            {
                                _logger?.LogInfo($"Fetch: local row for '{rule.Name}' appears {aheadByMinutes:F0} min ahead of server (likely legacy local-time storage). Letting server overwrite to normalize timestamp.");
                                // fall through to SaveRuleAsync below
                            }
                            else if (localUtc >= freshEditCutoff && localUtc >= serverUtc.AddSeconds(-2))
                            {
                                // Recency window: preserve local ONLY when it's also at least
                                // as new as the server (within 2 s clock slack). Pairing
                                // recency with "local >= server" prevents preserving a stale
                                // local row that was set by a previous fetch (so local ts
                                // matches server ts, both recent) when ANOTHER user has since
                                // edited on web.
                                _logger?.LogInfo($"Fetch: preserving recent local edit for rule '{rule.Name}' (id={rule.RuleId}, local={localUtc:o} ≥ server={serverUtc:o}, within 60 s window)");
                                preservedByRecency++;
                                continue;
                            }
                            else if (localUtc > serverUtc.AddSeconds(2))
                            {
                                // Timestamp wins outside the recency window: local edit is
                                // older than 60 s but still newer than server (within the
                                // 15-min sanity bound above) → server's PUT didn't persist
                                // or replication lag. Keep local.
                                _logger?.LogWarning($"Fetch: preserving local edit for rule '{rule.Name}' (id={rule.RuleId}, local={localUtc:o} > server={serverUtc:o}). Server may not have persisted the last PUT.");
                                preservedByTimestamp++;
                                continue;
                            }
                        }

                        var saved = await _ruleRepository.SaveRuleAsync(rule);
                        if (saved) savedCount++;
                    }
                    catch (Exception saveEx)
                    {
                        _logger?.LogWarning($"Failed to save rule '{rule.RuleId}' to local DB: {saveEx.Message}");
                    }
                }

                _logger?.LogInfo($"Fetched {rules.Count} rule protections for model {modelGuid}, saved {savedCount} (preserved {pendingRuleIds.Count} queued + {preservedByRecency} fresh-local + {preservedByTimestamp} newer-local)");

                // CROSS-TENANT CLEANUP DISABLED (2026-05-24). Despite preserving NULL
                // company_id rows and matching company_id rows, this DELETE path was the
                // last remaining fetch-time delete that could plausibly nuke a freshly
                // POSTed local rule (e.g. when the row is mid-write or its company_id
                // was momentarily not set). Both reconciliation and tenant-cleanup are
                // now off — the only DELETE that runs is the explicit user delete from
                // the dialog. Trade-off: rules cached from a previous tenant remain
                // visible until manual cleanup — far smaller cost than silently losing
                // a user-authored rule.
                var preserveCount = (await _ruleRepository.GetAllRulesAsync()).Count;
                _logger?.LogInfo($"Tenant cleanup DISABLED — preserving all {preserveCount} local rule(s) after fetch (was {preFetchLocal.Count} before)");

                // Reconcile: delete local rules not returned by server. Pending-edit rules
                // are preserved here — they were just authored locally and haven't reached
                // the server yet. Comparison is case-insensitive so a server-canonical
                // lowercase id matches a locally-stored uppercase one. Both case-variants
                // of an id are removed when only one is the canonical server form.
                //
                // SAFETY: skip the entire reconcile loop when the server returned ZERO rules.
                // A 0-rule response cannot reliably distinguish between "admin truly wiped all
                // rules on the web" and "client just authored a rule whose POST hasn't reached
                // the server yet (or its POST got marked non-retriable and dropped from the
                // offline queue)". Wiping a user's brand-new rule because the server didn't
                // return it yet is far more harmful than briefly retaining a rule the admin
                // deleted — the next non-empty fetch will reconcile correctly. The user's POST
                // retry from OfflineSyncProcessor (every 30 s) eventually pushes the rule
                // server-side; from then on, the server's non-empty response includes it and
                // normal reconciliation resumes for any actually-deleted rules.
                // RECONCILIATION RE-ENABLED (2026-05-26) with hardened safeguards. The
                // previous disable caused the inverse bug — local stale rules from a
                // previous tenant or deleted-on-web rules accumulated forever, so Revit
                // showed 10 rules while web showed 1 (user-confirmed via DBeaver / web).
                //
                // Safety guards (ALL must pass before deleting a local rule):
                //   1. Server response is non-empty — `rules.Count > 0`. A 0-count
                //      response could mean "auth failed and fetch silently returned []",
                //      not "admin really deleted all rules". The early return below
                //      handles this.
                //   2. Local rule is NOT in the pending offline queue — that POST is
                //      still in flight.
                //   3. Local rule's modified_at OR created_at is older than 600 s. The
                //      previous 120 s window was too tight — close + slow open cycles
                //      tripped it. 10 min is generous enough to cover even sluggish
                //      Revit sessions while still catching genuine stale rules on the
                //      NEXT fetch after that window.
                //   4. We check BOTH ModifiedAt AND CreatedAt — earlier code only
                //      checked the most recent of the two, which let "ancient created,
                //      just modified" rules slip through correctly, but missed "just
                //      created, never modified" → covered now.
                if (rules.Count == 0)
                {
                    _logger?.LogInfo("Rule reconciliation skipped: server returned 0 rules (could be auth/network blip — preserving all local rules).");
                    return rules;
                }

                try
                {
                    var serverRuleIds = rules
                        .Where(r => !string.IsNullOrEmpty(r.RuleId))
                        .Select(r => r.RuleId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var allLocal = await _ruleRepository.GetRuleIdsForFetchScopeAsync(modelGuid, currentCompanyId);

                    var orphanCount = 0;
                    var deletedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    const double freshnessWindowSeconds = 600.0; // 10-minute safety
                    foreach (var (localId, localName) in allLocal)
                    {
                        if (string.IsNullOrEmpty(localId)) continue;
                        if (deletedIds.Contains(localId)) continue;

                        // Case-variant duplicate cleanup (server has this id case-insensitively,
                        // but the local row's case differs from the canonical server form).
                        if (serverRuleIds.Contains(localId))
                        {
                            var exactMatch = rules.Any(r =>
                                string.Equals(r.RuleId, localId, StringComparison.Ordinal));
                            if (exactMatch) continue;

                            _logger?.LogInfo($"Reconciled: deleting case-variant duplicate '{localName}' ({localId})");
                            await _ruleRepository.DeleteRuleAsync(localId);
                            deletedIds.Add(localId);
                            orphanCount++;
                            continue;
                        }

                        // Pending offline-queue check — POST hasn't reached server yet.
                        if (pendingRuleIds.Contains(localId))
                        {
                            _logger?.LogInfo($"Reconciliation: preserving pending local rule '{localName}' ({localId})");
                            continue;
                        }

                        // Freshness check — preserve any local rule modified OR created in
                        // the last 10 minutes. SQLite returns DateTime with Kind=Unspecified;
                        // raw subtraction by Ticks works regardless of Kind, so do NOT call
                        // .ToUniversalTime() (it would treat unspecified as local and shift
                        // by the local offset, making every rule look hours old).
                        try
                        {
                            var localRule = await _ruleRepository.GetRuleByIdAsync(localId);
                            if (localRule != null)
                            {
                                var modAge = localRule.ModifiedAt != default
                                    ? (DateTime.UtcNow - localRule.ModifiedAt).TotalSeconds
                                    : double.MaxValue;
                                var crtAge = localRule.CreatedAt != default
                                    ? (DateTime.UtcNow - localRule.CreatedAt).TotalSeconds
                                    : double.MaxValue;
                                var youngest = Math.Min(modAge, crtAge);
                                if (youngest < freshnessWindowSeconds && youngest > -300)
                                {
                                    _logger?.LogInfo($"Reconciliation: preserving recent local rule '{localName}' ({localId}) — youngest of modified/created = {youngest:F0}s ago (window={freshnessWindowSeconds}s).");
                                    continue;
                                }
                            }
                        }
                        catch (Exception freshEx)
                        {
                            _logger?.LogDebug($"Freshness check failed for rule '{localId}': {freshEx.Message}");
                            // On freshness-check error, err on the side of PRESERVING the
                            // rule — same trade-off the rest of the safeguards apply.
                            continue;
                        }

                        await _ruleRepository.DeleteRuleAsync(localId);
                        deletedIds.Add(localId);
                        orphanCount++;
                        _logger?.LogInfo($"Reconciled: deleted orphaned rule '{localName}' ({localId})");
                    }
                    if (orphanCount > 0)
                        _logger?.LogInfo($"Rule reconciliation: removed {orphanCount} orphaned/duplicate record(s)");
                    else
                        _logger?.LogInfo("Rule reconciliation: 0 orphans found — local matches server.");
                }
                catch (Exception reconcileEx)
                {
                    _logger?.LogWarning($"Rule reconciliation failed: {reconcileEx.Message}");
                }

                // ── Scope-orphan sweep ────────────────────────────────────────────────
                // The freshness window in the reconcile loop above intentionally preserves
                // any local rule touched in the last 10 minutes — that's there to protect
                // in-flight offline edits. But it also preserves "scope-orphan" rows:
                // rules whose scope was changed on the web (e.g. Company → Model). When
                // an admin saves that edit, the server creates a NEW ruleId at the new
                // scope and the OLD row at the old scope is no longer returned by the
                // by-model endpoint — yet the local row gets its ModifiedAt refreshed
                // every time the dialog opens, so the freshness window protects it
                // forever. Net effect: Revit shows 4 rows while the web shows 2.
                //
                // Targeted cleanup: a local rule is a scope-orphan IFF the server
                // returned a different-id row for the same business identity
                // (CategoryCode + RuleScope is the closest stable pair). When that
                // condition holds, the local row is guaranteed NOT to be an in-flight
                // authored rule (server already has a row for that exact bucket with
                // a different id), so the freshness window is bypassed safely.
                try
                {
                    // Rebuild lookups here — the ones in the reconcile loop above are
                    // scoped to that inner try{}.
                    //   * serverRuleIdSet — every RuleId the server returned.
                    //   * serverCategorySet — every CategoryCode the server returned.
                    //
                    // The orphan detection uses CategoryCode (not CategoryCode + Scope):
                    // scope-CHANGE orphans (Company → Model edited on web) have a
                    // DIFFERENT scope from the server's current row, so a (Category, Scope)
                    // pair would never match them. Pairing on Category alone says "the
                    // server has spoken authoritatively for this category — any local row
                    // for the same category with a different RuleId must be a leftover
                    // from a previous mode/scope value." A legitimate local-only category
                    // (server returned nothing for it) is left alone.
                    var serverRuleIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var serverCategorySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in rules)
                    {
                        if (!string.IsNullOrEmpty(s.RuleId))
                            serverRuleIdSet.Add(s.RuleId);
                        if (!string.IsNullOrWhiteSpace(s.CategoryCode))
                            serverCategorySet.Add(s.CategoryCode.Trim());
                    }

                    var localAll = await _ruleRepository.GetAllRulesForCompanyAsync(currentCompanyId);
                    var scopeOrphanCount = 0;
                    foreach (var (localId, localName, localCat, localScope) in localAll)
                    {
                        if (string.IsNullOrEmpty(localId)) continue;
                        if (serverRuleIdSet.Contains(localId)) continue;     // RuleId matched server — kept.
                        if (pendingRuleIds.Contains(localId)) continue;      // Offline queue still in-flight.
                        if (string.IsNullOrWhiteSpace(localCat)) continue;   // No category to compare → leave alone.
                        if (!serverCategorySet.Contains(localCat.Trim())) continue; // Server didn't speak for this category → leave alone.

                        // Server returned at least one row for this CategoryCode with
                        // a DIFFERENT RuleId than the local row → guaranteed scope/mode-
                        // edit orphan. Safe to delete regardless of freshness window.
                        await _ruleRepository.DeleteRuleAsync(localId);
                        scopeOrphanCount++;
                        _logger?.LogInfo(
                            $"Scope-orphan sweep: deleted local rule '{localName}' " +
                            $"(id={localId}, category={localCat}, scope={localScope}) — " +
                            "server returned a different ruleId for the same category.");
                    }
                    if (scopeOrphanCount > 0)
                        _logger?.LogInfo($"Scope-orphan sweep: removed {scopeOrphanCount} stale local rule(s).");
                }
                catch (Exception sweepEx)
                {
                    _logger?.LogDebug($"Scope-orphan sweep failed: {sweepEx.Message}");
                }

                return rules;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch rule protections: {ex.Message}", ex);
                return new List<Rule>();
            }
        }

        /// <summary>
        /// Backward-compatible wrapper — fetches rules from the old /command-rules endpoint.
        /// Prefer FetchRuleProtectionsByModelAsync when a modelGuid is available.
        /// </summary>
        public async Task<List<Rule>> FetchAllRulesFromApiAsync()
        {
            // Delegate to by-model fetch if we can't use the old endpoint
            _logger?.LogInfo("FetchAllRulesFromApiAsync called (legacy). Consider using FetchRuleProtectionsByModelAsync with modelGuid.");

            try
            {
                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("HTTP client not available or not authenticated for fetching rules");
                    return new List<Rule>();
                }

                var response = await _httpClient.GetAsync(Endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
                        _logger?.LogInfo($"Fetch rules: endpoint does not support GET (405) — skipping legacy fetch");
                    else
                        _logger?.LogWarning($"Fetch rules failed: {response.StatusCode} - {responseBody}");
                    return new List<Rule>();
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"Fetch rules response:\n{json}");

                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                List<RuleProtectionApiResponse>? apiRules = null;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        apiRules = JsonSerializer.Deserialize<List<RuleProtectionApiResponse>>(dataElement.GetRawText(), jsonOptions);
                    }
                    else if (root.ValueKind == JsonValueKind.Array)
                    {
                        apiRules = JsonSerializer.Deserialize<List<RuleProtectionApiResponse>>(json, jsonOptions);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to deserialize rules API response: {ex.Message}", ex);
                    return new List<Rule>();
                }

                if (apiRules == null || apiRules.Count == 0)
                {
                    _logger?.LogWarning("No rules returned from API");
                    return new List<Rule>();
                }

                // Tenant-scope filter — same intent as the by-model fetch path. Drop any
                // API rows whose companyId doesn't match the signed-in user before mapping.
                var currentCompanyId = GetCurrentCompanyId();
                if (currentCompanyId != null)
                {
                    var preFilter = apiRules.Count;
                    apiRules = apiRules
                        .Where(r => string.IsNullOrWhiteSpace(r.CompanyId)
                                    || string.Equals(r.CompanyId, currentCompanyId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (apiRules.Count != preFilter)
                        _logger?.LogInfo($"Tenant filter (legacy): dropped {preFilter - apiRules.Count} cross-company rule(s) from API response (currentCompany={currentCompanyId})");
                }

                // Same dedup as FetchRuleProtectionsByModelAsync — guards against
                // missing-id phantom GUIDs and duplicate ids in the same payload that
                // cause "edit-on-web duplicates row in Revit".
                var seenLegacyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var rules = new List<Rule>();
                foreach (var apiRule in apiRules)
                {
                    if (string.IsNullOrWhiteSpace(apiRule.RuleProtectionId))
                    {
                        _logger?.LogWarning($"Fetch (legacy): skipping API rule with missing RuleProtectionId (category={apiRule.CategoryCode}, mode={apiRule.Mode}).");
                        continue;
                    }
                    if (!seenLegacyIds.Add(apiRule.RuleProtectionId))
                    {
                        _logger?.LogInfo($"Fetch (legacy): skipping duplicate RuleProtectionId {apiRule.RuleProtectionId} (category={apiRule.CategoryCode}).");
                        continue;
                    }
                    rules.Add(MapFromRuleProtectionResponse(apiRule));
                }

                // CROSS-TENANT CLEANUP (legacy path) DISABLED (2026-05-24) — same
                // reasoning as the by-model fetch above. The only DELETE that runs
                // against the rules table is the explicit user delete from the dialog.
                _logger?.LogInfo("Tenant cleanup (legacy) DISABLED — preserving all local rules");

                // Persist fetched rules to local database
                var savedCount = 0;
                foreach (var rule in rules)
                {
                    try
                    {
                        var saved = await _ruleRepository.SaveRuleAsync(rule);
                        if (saved) savedCount++;
                    }
                    catch (Exception saveEx)
                    {
                        _logger?.LogWarning($"Failed to save rule '{rule.RuleId}' to local DB: {saveEx.Message}");
                    }
                }

                _logger?.LogInfo($"Fetched {rules.Count} rules from API, saved {savedCount} to local database");
                return rules;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to fetch rules from API: {ex.Message}", ex);
                return new List<Rule>();
            }
        }

        /// <summary>
        /// Maps the rule-protections GET response to internal Rule model.
        /// </summary>
        private Rule MapFromRuleProtectionResponse(RuleProtectionApiResponse apiResponse)
        {
            // Populate the global profileId → displayName cache so the UI can render
            // "Modified By" as the user's name instead of their raw GUID. The same
            // pattern lives in EventProtectionSyncService.MapFromApiResponse — keeping
            // both in sync ensures every API entry-point feeds the same cache, so a
            // user resolves consistently across Rule / Event / Command dialogs.
            var updatedByDisplayName = !string.IsNullOrWhiteSpace(apiResponse.UpdatedByDisplayName)
                ? apiResponse.UpdatedByDisplayName
                : apiResponse.ModifiedByDisplayName;
            if (!string.IsNullOrWhiteSpace(apiResponse.UpdatedBy)
                && !string.IsNullOrWhiteSpace(updatedByDisplayName))
            {
                Core.Identity.UserDisplayNameCache.Set(apiResponse.UpdatedBy!, updatedByDisplayName!);
            }
            if (!string.IsNullOrWhiteSpace(apiResponse.CreatedBy)
                && !string.IsNullOrWhiteSpace(apiResponse.CreatedByDisplayName))
            {
                Core.Identity.UserDisplayNameCache.Set(apiResponse.CreatedBy!, apiResponse.CreatedByDisplayName!);
            }

            var rule = new Rule
            {
                // RuleProtectionId is the canonical server-side primary key. Callers
                // MUST filter out rows missing this id BEFORE invoking the mapper —
                // the previous fallback to Guid.NewGuid() was the root cause of
                // "edit-on-web creates duplicate row in Revit" (every fetch generated
                // a new id for the same rule, so reconciliation could never collapse
                // them). FetchRuleProtectionsByModelAsync now enforces this upstream.
                RuleId = apiResponse.RuleProtectionId ?? Guid.NewGuid().ToString(),
                Mode = (ProtectionMode)apiResponse.Mode,
                Priority = apiResponse.Priority,
                IsEnabled = apiResponse.IsEnabled,
                ProjectId = apiResponse.ProjectId,
                CompanyId = apiResponse.CompanyId,
                ModelGuid = apiResponse.ModelGuid,
                CategoryId = apiResponse.CategoryId,
                CategoryCode = apiResponse.CategoryCode,
                CategoryName = apiResponse.CategoryName ?? string.Empty,
                TypeName = apiResponse.TypeName,
                FamilyName = apiResponse.FamilyName,
                Message = apiResponse.Message ?? string.Empty,
                CaptureBeforeScreenshot = apiResponse.CaptureBeforeScreenshot,
                CaptureAfterScreenshot = apiResponse.CaptureAfterScreenshot,
                RequireComment = apiResponse.RequireComment,
                AllowAdminOverride = apiResponse.AllowAdminOverride,
                SendEmail = apiResponse.SendEmail,
                CreatedBy = apiResponse.CreatedBy,
                // When the server returns UpdatedBy = "System" (server-side automation
                // / seed write with no user-context) but CreatedBy is a real user,
                // store CreatedBy as ModifiedBy so the local DB never holds the
                // "System" sentinel. The web side shows the author's name in this
                // case; matching that here keeps the Modified By column meaningful.
                // The display-layer fallback (RulesManagementViewModel.LoadRulesAsync)
                // is the second line of defence for rows already in the DB.
                ModifiedBy =
                    (string.Equals(apiResponse.UpdatedBy, "System", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(apiResponse.CreatedBy)
                     && !string.Equals(apiResponse.CreatedBy, "System", StringComparison.OrdinalIgnoreCase))
                        ? apiResponse.CreatedBy
                        : apiResponse.UpdatedBy,
                RuleScope = apiResponse.LevelScope > 0 ? apiResponse.LevelScope : (int)RuleScopeType.CompanyWide,
                Version = apiResponse.Version
            };

            // Use categoryName as the rule name if available, otherwise generate from categoryCode
            rule.Name = !string.IsNullOrEmpty(apiResponse.CategoryName)
                ? $"{apiResponse.CategoryName} Protection"
                : $"Rule {rule.RuleId.Substring(0, Math.Min(8, rule.RuleId.Length))}";

            // Parse CreatedAt/UpdatedAt from ISO strings.
            //
            // CRITICAL (2026-05-25): use AssumeUniversal | AdjustToUniversal so the parsed
            // DateTime has Kind=Utc. Default DateTime.TryParse on a "Z"-suffixed ISO string
            // silently converts to LOCAL time (Kind=Local). When SaveRuleAsync then writes
            // the value using "yyyy-MM-dd HH:mm:ss" (no TZ marker), the LOCAL wall-clock
            // (e.g. "15:41:22" IST) gets persisted as if it were UTC. On the next fetch,
            // the recency window compares local (IST stored as UTC) vs server (real UTC)
            // and sees a 5:30 h offset, so every legitimately-synced rule looks "newer
            // locally" — server updates never reach Revit. Forcing UTC here makes the
            // round-trip preserve real UTC wall-clocks.
            var dtStyles = System.Globalization.DateTimeStyles.AssumeUniversal
                         | System.Globalization.DateTimeStyles.AdjustToUniversal;
            if (DateTime.TryParse(apiResponse.CreatedAt, System.Globalization.CultureInfo.InvariantCulture, dtStyles, out var createdAt))
                rule.CreatedAt = createdAt;
            if (DateTime.TryParse(apiResponse.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture, dtStyles, out var updatedAt))
                rule.ModifiedAt = updatedAt;

            // Parse command names from commentName (comma-separated)
            if (!string.IsNullOrEmpty(apiResponse.CommentName))
            {
                foreach (var name in apiResponse.CommentName.Split(','))
                {
                    var trimmed = name.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        rule.CommandNames.Add(trimmed);
                }
            }

            return rule;
        }

        private RuleUpdateApiRequest MapToUpdateRequest(Rule rule)
        {
            string? NormalizeGuid(string? s) =>
                !string.IsNullOrWhiteSpace(s) && Guid.TryParse(s, out _) ? s : null;

            // Wire-format rule (matches the working Event Protection PUT pattern):
            //   Company-level row → ModelGuid=null, ProjectId=null, LevelScope=1
            //   Project-level row → ModelGuid=null, ProjectId=<guid>, LevelScope=2
            //   Model-level row   → ModelGuid=<guid>, ProjectId=<guid>, LevelScope=3
            // The DTO's JsonIgnoreCondition.WhenWritingNull drops null fields so the
            // server sees only the values that apply to the row's scope. Without
            // LevelScope+ProjectId in the body, the server's PUT handler updates the
            // DB row but cannot determine which SignalR group to broadcast the
            // RuleUpdate event to, so web clients never see the change in real time.
            // The previous body sent ModelGuid=string.Empty for company/project rows,
            // which the server treated as an invalid GUID — same root cause as the
            // Event toggle bug that I already fixed by routing through PUT.
            var scope = rule.RuleScope;
            string? modelGuid = null;
            string? projectId = null;
            switch (scope)
            {
                case (int)RuleScopeType.CompanyWide:
                    // Both null — company-wide rule applies across all projects/models
                    break;
                case (int)RuleScopeType.ProjectWide:
                    projectId = NormalizeGuid(rule.ProjectId);
                    break;
                case (int)RuleScopeType.ModelSpecific:
                default:
                    projectId = NormalizeGuid(rule.ProjectId);
                    modelGuid = NormalizeGuid(rule.ModelGuid);
                    break;
            }

            return new RuleUpdateApiRequest
            {
                LevelScope = scope,
                ProjectId = projectId,
                ModelGuid = modelGuid,
                Mode = (int)rule.Mode,
                Priority = rule.Priority,
                IsEnabled = rule.IsEnabled,
                Message = rule.Message,
                CaptureBeforeScreenshot = rule.CaptureBeforeScreenshot,
                CaptureAfterScreenshot = rule.CaptureAfterScreenshot,
                RequireComment = rule.RequireComment,
                AllowAdminOverride = rule.AllowAdminOverride,
                CommentName = rule.CommandNames.Count > 0 ? string.Join(",", rule.CommandNames) : null,
                SendEmail = rule.SendEmail,
                UpdatedAt = DateTime.UtcNow.ToString("o")
            };
        }

        private RuleProtectionPostRequest MapToRuleProtectionPostRequest(Rule rule)
        {
            // Server expects projectId/modelGuid as Nullable<Guid>. Empty strings or
            // non-GUID values cause a 400 ("could not be converted to Nullable[Guid]"),
            // so normalize them to null here.
            string? NormalizeGuid(string? s) =>
                !string.IsNullOrWhiteSpace(s) && Guid.TryParse(s, out _) ? s : null;

            // Scope-aware nulling — same shape as MapToUpdateRequest (the working PUT path).
            // A new rule object can inherit stale ProjectId/ModelGuid from the open document context;
            // sending those for a CompanyWide row makes the server reject the POST. The PUT path
            // already learned this lesson (see comment in MapToUpdateRequest) — POST needs the same.
            //   Company-level new rule  → ModelGuid=null, ProjectId=null, LevelScope=1
            //   Project-level new rule  → ModelGuid=null, ProjectId=<guid>, LevelScope=2
            //   Model-level new rule    → ModelGuid=<guid>, ProjectId=<guid>, LevelScope=3
            var scope = rule.RuleScope;
            string? projectId = null;
            string? modelGuid = null;
            switch (scope)
            {
                case (int)RuleScopeType.CompanyWide:
                    break;
                case (int)RuleScopeType.ProjectWide:
                    projectId = NormalizeGuid(rule.ProjectId);
                    break;
                case (int)RuleScopeType.ModelSpecific:
                default:
                    projectId = NormalizeGuid(rule.ProjectId);
                    modelGuid = NormalizeGuid(rule.ModelGuid);
                    break;
            }

            return new RuleProtectionPostRequest
            {
                RuleProtectionId = rule.RuleId,
                Mode = (int)rule.Mode,
                Priority = rule.Priority,
                IsEnabled = rule.IsEnabled,
                LevelScope = scope,
                ProjectId = projectId,
                ModelGuid = modelGuid,
                CategoryId = rule.CategoryId,
                CategoryCode = rule.CategoryCode,
                CategoryName = rule.CategoryName,
                TypeName = rule.TypeName,
                FamilyName = rule.FamilyName,
                Message = rule.Message,
                CaptureBeforeScreenshot = rule.CaptureBeforeScreenshot,
                CaptureAfterScreenshot = rule.CaptureAfterScreenshot,
                RequireComment = rule.RequireComment,
                AllowAdminOverride = rule.AllowAdminOverride,
                CommentName = rule.CommandNames.Count > 0 ? string.Join(",", rule.CommandNames) : null,
                SendEmail = rule.SendEmail,
                CreatedAt = rule.CreatedAt.ToString("o")
            };
        }

        private static JsonSerializerOptions GetJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = true
            };
        }
    }

    #region API Request DTOs

    /// <summary>
    /// DTO for POST /api/v1/Revit/rule-protections
    /// Matches the backend contract exactly.
    /// </summary>
    public class RuleProtectionPostRequest
    {
        [JsonPropertyName("ruleProtectionId")]
        public string? RuleProtectionId { get; set; }

        [JsonPropertyName("mode")]
        public int Mode { get; set; }

        [JsonPropertyName("priority")]
        public int Priority { get; set; }

        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; set; }

        [JsonPropertyName("levelScope")]
        public int LevelScope { get; set; }

        [JsonPropertyName("projectId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProjectId { get; set; }

        [JsonPropertyName("modelGuid")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ModelGuid { get; set; }

        [JsonPropertyName("categoryId")]
        public int? CategoryId { get; set; }

        [JsonPropertyName("categoryCode")]
        public string? CategoryCode { get; set; }

        [JsonPropertyName("categoryName")]
        public string? CategoryName { get; set; }

        [JsonPropertyName("typeName")]
        public string? TypeName { get; set; }

        [JsonPropertyName("familyName")]
        public string? FamilyName { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("captureBeforeScreenshot")]
        public bool CaptureBeforeScreenshot { get; set; }

        [JsonPropertyName("captureAfterScreenshot")]
        public bool CaptureAfterScreenshot { get; set; }

        [JsonPropertyName("requireComment")]
        public bool RequireComment { get; set; }

        [JsonPropertyName("allowAdminOverride")]
        public bool AllowAdminOverride { get; set; }

        [JsonPropertyName("commentName")]
        public string? CommentName { get; set; }

        [JsonPropertyName("sendEmail")]
        public bool SendEmail { get; set; }

        [JsonPropertyName("createdAt")]
        public string? CreatedAt { get; set; }
    }

    /// <summary>
    /// DTO for GET /api/v1/Revit/rule-protections/by-model/{modelGuid} response.
    /// Matches the actual backend response schema.
    /// </summary>
    public class RuleProtectionApiResponse
    {
        [JsonPropertyName("ruleProtectionId")]
        public string? RuleProtectionId { get; set; }

        [JsonPropertyName("mode")]
        public int Mode { get; set; }

        [JsonPropertyName("priority")]
        public int Priority { get; set; }

        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; set; }

        [JsonPropertyName("projectId")]
        public string? ProjectId { get; set; }

        [JsonPropertyName("companyId")]
        public string? CompanyId { get; set; }

        [JsonPropertyName("modelGuid")]
        public string? ModelGuid { get; set; }

        [JsonPropertyName("categoryId")]
        public int? CategoryId { get; set; }

        [JsonPropertyName("categoryCode")]
        public string? CategoryCode { get; set; }

        [JsonPropertyName("categoryName")]
        public string? CategoryName { get; set; }

        [JsonPropertyName("typeName")]
        public string? TypeName { get; set; }

        [JsonPropertyName("familyName")]
        public string? FamilyName { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("captureBeforeScreenshot")]
        public bool CaptureBeforeScreenshot { get; set; }

        [JsonPropertyName("captureAfterScreenshot")]
        public bool CaptureAfterScreenshot { get; set; }

        [JsonPropertyName("requireComment")]
        public bool RequireComment { get; set; }

        [JsonPropertyName("allowAdminOverride")]
        public bool AllowAdminOverride { get; set; }

        [JsonPropertyName("createdAt")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("createdBy")]
        public string? CreatedBy { get; set; }

        // Display-name companion fields — server already populates these for the rules
        // GET response (verified against the event-protection DTO which deserialises the
        // same shape successfully). Without these here, the plugin only stored the raw
        // profileId, the local UserDisplayNameCache stayed empty for that GUID, and the
        // UI fell back to showing the raw GUID in the "Modified By" column for any user
        // whose mapping wasn't populated through another endpoint.
        [JsonPropertyName("createdByDisplayName")]
        public string? CreatedByDisplayName { get; set; }

        [JsonPropertyName("updatedAt")]
        public string? UpdatedAt { get; set; }

        [JsonPropertyName("updatedBy")]
        public string? UpdatedBy { get; set; }

        [JsonPropertyName("updatedByDisplayName")]
        public string? UpdatedByDisplayName { get; set; }

        // Some server endpoints use "modifiedByDisplayName" instead — accept both spellings
        // so we don't depend on a single property name landing on this DTO.
        [JsonPropertyName("modifiedByDisplayName")]
        public string? ModifiedByDisplayName { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("commentName")]
        public string? CommentName { get; set; }

        [JsonPropertyName("sendEmail")]
        public bool SendEmail { get; set; }

        [JsonPropertyName("isDefault")]
        public bool IsDefault { get; set; }

        [JsonPropertyName("levelScope")]
        public int LevelScope { get; set; }
    }

    /// <summary>
    /// DTO for PUT /api/v1/Revit/rule-protections/for-Model/{ruleProtectionId}
    /// Matches UpdateRuleProtectionForModelRequest API schema.
    ///
    /// LevelScope + ProjectId + (nullable) ModelGuid are required so the server can
    /// determine which SignalR group to broadcast the resulting RuleUpdate event to
    /// (company / project / model). Without these, the PUT succeeds against the DB
    /// but no broadcast is emitted — that's the cause of "Rule update on Revit
    /// doesn't propagate to web in real time". Wire-format rules:
    ///   Company-level row → ModelGuid omitted, ProjectId omitted, LevelScope=1
    ///   Project-level row → ModelGuid omitted, ProjectId set,     LevelScope=2
    ///   Model-level row   → ModelGuid set,     ProjectId optional, LevelScope=3
    /// JsonIgnoreCondition.WhenWritingNull on the optional fields drops them from
    /// the wire body for scopes that don't apply.
    /// </summary>
    public class RuleUpdateApiRequest
    {
        [JsonPropertyName("levelScope")]
        public int LevelScope { get; set; }

        [JsonPropertyName("projectId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProjectId { get; set; }

        [JsonPropertyName("modelGuid")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ModelGuid { get; set; }

        [JsonPropertyName("mode")]
        public int Mode { get; set; }

        [JsonPropertyName("priority")]
        public int Priority { get; set; }

        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("captureBeforeScreenshot")]
        public bool CaptureBeforeScreenshot { get; set; }

        [JsonPropertyName("captureAfterScreenshot")]
        public bool CaptureAfterScreenshot { get; set; }

        [JsonPropertyName("requireComment")]
        public bool RequireComment { get; set; }

        [JsonPropertyName("allowAdminOverride")]
        public bool AllowAdminOverride { get; set; }

        [JsonPropertyName("commentName")]
        public string? CommentName { get; set; }

        [JsonPropertyName("sendEmail")]
        public bool SendEmail { get; set; }

        [JsonPropertyName("updatedAt")]
        public string? UpdatedAt { get; set; }
    }

    #endregion
}
