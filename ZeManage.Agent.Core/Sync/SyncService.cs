using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Monitors;
using ZeManage.Agent.Core.Services;

namespace ZeManage.Agent.Core.Sync;

public sealed class SyncService : BackgroundService
{
    private readonly LocalStore _store;
    private readonly IdentityService _identity;
    private readonly AgentOptions _opts;
    private readonly ILogger<SyncService> _log;
    private readonly AgentState _state;
    private readonly IHttpClientFactory _httpFactory;
    private readonly TokenProvider _tokens;
    private readonly ProcessMonitor _processMonitor;
    private readonly ResiliencePipeline _retry;
    private bool _identityPosted;

    public SyncService(
        LocalStore store,
        IdentityService identity,
        IOptions<AgentOptions> opts,
        ILogger<SyncService> log,
        AgentState state,
        IHttpClientFactory httpFactory,
        TokenProvider tokens,
        ProcessMonitor processMonitor)
    {
        _store          = store;
        _identity       = identity;
        _opts           = opts.Value;
        _log            = log;
        _state          = state;
        _httpFactory    = httpFactory;
        _tokens         = tokens;
        _processMonitor = processMonitor;

        _retry = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential
            })
            .Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_opts.SyncIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _state.LastSyncStatus = $"Error: {ex.Message}";
                _log.LogWarning(ex, "Sync cycle failed");
                _state.NotifyChanged();
            }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var id        = _identity.Get();
        var machineId = id.MachineId;
        var totalUnsynced = await GetUnsyncedTotalAsync(ct);
        _state.UnsyncedCount = totalUnsynced;

        var token = await _tokens.GetAsync(ct);
        if (token is null)
        {
            _state.LastSyncStatus = "Backend unreachable / login failed (cached locally)";
            _state.NotifyChanged();
            return;
        }

        var client = _httpFactory.CreateClient("backend");
        client.BaseAddress = new Uri(_opts.BackendBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Agent-Id",        machineId);
        client.DefaultRequestHeaders.Add("X-Agent-OsVersion", id.OsVersion ?? "");
        client.DefaultRequestHeaders.Add("X-Agent-Sid",       id.WindowsSid ?? "");
        if (!string.IsNullOrEmpty(id.CpuModel))    client.DefaultRequestHeaders.Add("X-Agent-CpuModel",     id.CpuModel);
        if (id.TotalRamGB > 0)                      client.DefaultRequestHeaders.Add("X-Agent-RamGB",        id.TotalRamGB.ToString("F1"));
        if (!string.IsNullOrEmpty(id.MacAddress))   client.DefaultRequestHeaders.Add("X-Agent-MacAddress",   id.MacAddress);
        if (!string.IsNullOrEmpty(id.SerialNumber)) client.DefaultRequestHeaders.Add("X-Agent-SerialNumber", id.SerialNumber);
        if (!string.IsNullOrEmpty(id.IpAddress))    client.DefaultRequestHeaders.Add("X-Agent-IpAddress",    id.IpAddress);

        // Fetch company settings to get server-configured idle threshold
        await TryFetchCompanySettingsAsync(client, ct);

        // Register device identity once per session
        if (!_identityPosted)
        {
            var identityPayload = new
            {
                machineId                 = id.MachineId,           // string GUID — backend requires string
                companyId                 = _tokens.CompanyId ?? "",
                companyName               = _tokens.CompanyName ?? "",
                zeUserId                  = _tokens.ZeUserId ?? "",
                timeZone                  = TimeZoneInfo.Local.Id,
                status                    = 1,
                screenshotIntervalSeconds = _opts.ScreenshotIntervalMinutes * 60,
                sid                       = id.WindowsSid ?? "",
                windowsUserName           = id.UserName,
                hostName                  = id.MachineName,
                capturedAt                = DateTime.UtcNow,
                cpuModel                  = id.CpuModel,
                ramGb                     = id.TotalRamGB,
                ramUsableGb               = id.UsableRamGB,
                gpuModel                  = id.GpuModel,
                storageTotalGb            = id.StorageTotalGB,
                storageUsedGb             = id.StorageUsedGB,
                macAddress                = id.MacAddress,
                ipAddress                 = id.IpAddress,
                serialNumber              = id.SerialNumber,
                deviceId                  = id.DeviceId,
                systemType                = id.SystemType,
                osVersion                 = id.OsVersion,
                windowsEdition            = id.WindowsEdition,
                windowsVersion            = id.WindowsVersion,
                osBuild                   = id.OsBuild,
                biosVersion               = id.BiosVersion,
                motherboardModel          = id.MotherboardModel
            };
            if (await TryPostSingleAsync(client, "/api/v1/agentdb/identity", identityPayload, ct))
            {
                _identityPosted = true;
                _log.LogInformation("Device identity registered: {MachineId}", machineId);
            }
        }

        var anyOk = false;

        // Batch POST all running apps whose session_id is still null — ALL at once, not one by one
        var runningNoSession = await _store.GetRunningWithoutSessionIdAsync(200, ct);
        if (runningNoSession.Count > 0)
        {
            _log.LogInformation("Batch open POST: sending {Count} null-session apps", runningNoSession.Count);
            var payloads = runningNoSession.Select(a => new
            {
                appId           = a.ApplicationId,
                applicationName = a.ApplicationName,
                executableName  = a.ProcessName,
                type            = a.ApplicationType,
                displayName     = a.ApplicationName,
                version         = a.Version,
                iconUrl         = a.IconBase64,
                status          = "Running",
                createdAt       = a.CreatedAt,
                updatedAt       = a.UpdatedAt,
                activeSeconds   = (long)0,
                focusSeconds    = (long)0,
                idleSeconds     = (long)0,
                durationSeconds = (long)0,
                crashCount      = 0
            }).ToArray();

            // Returns sessionIds indexed same as payloads
            var sessionIds = await TryBatchPostAppOpenAsync(client, "/api/v1/agentdb/applications", payloads, ct);

            for (int i = 0; i < runningNoSession.Count && i < sessionIds.Count; i++)
            {
                if (sessionIds[i] is not null)
                {
                    var localId   = runningNoSession[i].LocalId;
                    var sessionId = sessionIds[i]!;
                    await _store.UpdateSessionIdAsync(localId, sessionId, ct);
                    _processMonitor.UpdateInMemorySessionId(localId, sessionId);
                    _log.LogInformation("sessionId set: {App} → {SessionId}", runningNoSession[i].ApplicationName, sessionId);
                    anyOk = true;
                }
            }

        }

        // Sync Closed apps — session_id=null + status=Closed → skip POST, just mark synced
        var apps = await _store.GetUnsyncedApplicationsAsync(200, ct);
        var closedApps        = apps.Where(a => a.Status == "Closed").ToList();
        var closedWithSession = closedApps.Where(a => a.SessionId is not null).ToList();
        var closedNoSession   = closedApps.Where(a => a.SessionId is null).ToList();

        if (closedWithSession.Count > 0)
        {
            var dtos = closedWithSession.Select(a => new
            {
                appId           = a.ApplicationId,
                applicationName = a.ApplicationName,
                executableName  = a.ProcessName,
                type            = a.ApplicationType,
                displayName     = a.ApplicationName,
                version         = a.Version,
                iconUrl         = a.IconBase64,
                createdAt       = a.CreatedAt,
                updatedAt       = a.UpdatedAt,
                activeSeconds   = a.ActiveSeconds,
                focusSeconds    = a.FocusSeconds,
                idleSeconds     = a.IdleSeconds,
                durationSeconds = (long)(a.UpdatedAt - a.CreatedAt).TotalSeconds,
                crashCount      = a.CrashCount,
                status          = a.Status
            }).ToList();
            if (await TryPostAsync(client, "/api/v1/agentdb/applications", dtos, ct))
            {
                await _store.MarkSyncedAsync(closedWithSession, ct);
                anyOk = true;
            }
        }

        // session_id=null + Closed → server never opened a session for these, mark synced to stop retrying
        if (closedNoSession.Count > 0)
        {
            await _store.MarkSyncedAsync(closedNoSession, ct);
            _log.LogWarning("Skipped POST for {Count} closed apps with no session_id", closedNoSession.Count);
        }

        var net = await _store.GetUnsyncedNetworkAsync(100, ct);
        if (net.Count > 0)
        {
            var latest = net.OrderByDescending(n => n.CapturedAt).First();
            var dto = new
            {
                companyId         = _tokens.CompanyId,
                zeUserId          = _tokens.ZeUserId,
                capturedAt        = latest.CapturedAt,
                downloadMbps      = (float)latest.DownloadMbps,
                uploadMbps        = (float)latest.UploadMbps,
                latencyMs         = (float)latest.LatencyMs,
                packetLossPercent = (float)latest.PacketLossPercent,
                vpnConnected      = latest.VpnConnected,
                adapterName       = latest.ActiveAdapter,
                healthScore       = latest.HealthScore
            };
            if (await TryPostSingleAsync(client, "/api/v1/agentdb/network-snapshots", dto, ct))
            {
                await _store.MarkSyncedAsync(net, ct);
                anyOk = true;
            }
        }

        var browser = await _store.GetUnsyncedBrowserActivitiesAsync(200, ct);
        if (browser.Count > 0)
        {
            // Only send records that have a URL — server requires Url field
            var withUrl    = browser.Where(b => !string.IsNullOrEmpty(b.Url)).ToList();
            var withoutUrl = browser.Where(b => string.IsNullOrEmpty(b.Url)).ToList();

            // Mark no-URL records synced so they don't keep blocking the queue
            if (withoutUrl.Count > 0)
                await _store.MarkSyncedAsync(withoutUrl, ct);

            if (withUrl.Count > 0)
            {
                var dtos = withUrl.Select(b => new
                {
                    zeUserId        = _tokens.ZeUserId ?? "",
                    companyId       = _tokens.CompanyId ?? "",
                    browserName     = b.Browser,
                    processName     = b.Browser == "Google Chrome" ? "chrome" : "msedge",
                    pageTitle       = b.PageTitle,
                    url             = b.Url,
                    applicationId   = b.ApplicationId,
                    startTime       = b.StartTime,
                    endTime         = b.EndTime,
                    durationSeconds = b.DurationSeconds
                }).ToList();
                if (await TryPostAsync(client, "/api/v1/agentdb/browser-activities", dtos, ct))
                {
                    await _store.MarkSyncedAsync(withUrl, ct);
                    anyOk = true;
                }
            }
        }

        var shots = await _store.GetUnsyncedScreenshotsAsync(20, ct);
        if (shots.Count > 0)
        {
            var dtos = shots.Select(s => new
            {
                capturedAt    = s.CapturedAt,
                triggerEvent  = s.TriggerEvent,
                filePath      = s.FilePath,
                fileSizeBytes = s.FileSizeBytes
            }).ToList();
            if (await TryPostAsync(client, "/api/v1/agentdb/screenshots", dtos, ct))
            {
                await _store.MarkSyncedAsync(shots, ct);
                anyOk = true;
            }
        }

        totalUnsynced = await GetUnsyncedTotalAsync(ct);
        _state.UnsyncedCount = totalUnsynced;
        _state.LastSyncAt = DateTime.Now;
        _state.LastSyncStatus = anyOk
            ? "OK"
            : totalUnsynced == 0 ? "Up to date" : "Backend reachable but no data sent";
        _state.NotifyChanged();
    }

    // POSTs all apps in one batch. Returns sessionIds indexed same as input array.
    // Null entry = server didn't return a sessionId for that position.
    private async Task<List<string?>> TryBatchPostAppOpenAsync<T>(
        HttpClient client, string path, T[] payload, CancellationToken ct)
    {
        var results = new List<string?>(payload.Length);
        try
        {
            using var resp = await client.PostAsJsonAsync(path, payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Batch app open POST {Status}: {Body}", (int)resp.StatusCode, err);
                for (int i = 0; i < payload.Length; i++) results.Add(null);
                return results;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            _log.LogWarning("SyncService batch open RAW response: {Json}", json);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var items = new List<System.Text.Json.JsonElement>();
            if (root.TryGetProperty("data", out var d))
            {
                if (d.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var el in d.EnumerateArray()) items.Add(el);
                else if (d.ValueKind == System.Text.Json.JsonValueKind.Object)
                    items.Add(d);
            }
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var el in root.EnumerateArray()) items.Add(el);
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                items.Add(root);

            for (int i = 0; i < payload.Length; i++)
            {
                string? sid = i < items.Count ? ExtractSessionId(items[i]) : null;
                if (sid is null)
                    _log.LogWarning("No sessionId in response at index {I} — raw: {Raw}",
                        i, i < items.Count ? items[i].GetRawText() : "(missing)");
                results.Add(sid);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Batch app open POST failed");
            while (results.Count < payload.Length) results.Add(null);
        }
        return results;
    }

    // Case-insensitive scan for any known session-ID field in a JSON response item.
    // Handles camelCase, PascalCase, snake_case from any server implementation.
    private static string? ExtractSessionId(System.Text.Json.JsonElement item)
    {
        foreach (var prop in item.EnumerateObject())
        {
            if (prop.Name.Equals("applicationSessionId",  StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("application_session_id", StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("sessionId",             StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("session_id",            StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("sessionGuid",           StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("id",                    StringComparison.OrdinalIgnoreCase))
            {
                var val = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? prop.Value.GetString() : null;
                if (val != null) return val;
            }
        }
        return null;
    }

    private async Task<bool> TryPostAsync<T>(HttpClient client, string path, IReadOnlyList<T> payload, CancellationToken ct)
    {
        string? errorBody = null;
        try
        {
            await _retry.ExecuteAsync(async token =>
            {
                using var resp = await client.PostAsJsonAsync(path, payload, token);
                if (!resp.IsSuccessStatusCode)
                {
                    errorBody = await resp.Content.ReadAsStringAsync(token);
                    throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
                }
            }, ct);
            _log.LogInformation("Synced {Count} rows to {Path}", payload.Count, path);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Post to {Path} failed — body: {Body}", path, errorBody ?? "(no body)");
            return false;
        }
    }

    private async Task<bool> TryPostSingleAsync<T>(HttpClient client, string path, T payload, CancellationToken ct)
    {
        string? errorBody = null;
        try
        {
            await _retry.ExecuteAsync(async token =>
            {
                using var resp = await client.PostAsJsonAsync(path, payload, token);
                if (!resp.IsSuccessStatusCode)
                {
                    errorBody = await resp.Content.ReadAsStringAsync(token);
                    throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
                }
            }, ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Post to {Path} failed — body: {Body}", path, errorBody ?? "(no body)");
            return false;
        }
    }

    private Task<int> GetUnsyncedTotalAsync(CancellationToken ct) => _store.CountUnsyncedAsync(ct);

    private async Task TryFetchCompanySettingsAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            using var resp = await client.GetAsync("/api/v1/agentdb/company-settings", ct);
            if (!resp.IsSuccessStatusCode) return;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement data = root;
            if (root.TryGetProperty("data", out var d)) data = d;

            if (data.TryGetProperty("isIdleTrackingEnabled", out var idleEnabled))
                _state.IsIdleTrackingEnabled = idleEnabled.GetBoolean();

            if (data.TryGetProperty("idleThresholdMinutes", out var prop) &&
                prop.ValueKind == JsonValueKind.Number &&
                prop.TryGetInt32(out var minutes) && minutes > 0)
            {
                _state.IdleThresholdMinutes = minutes;
                _log.LogDebug("IdleThreshold updated from server: {Minutes} min", minutes);
            }

            if (data.TryGetProperty("isScreenshotEnabled", out var scEn))
                _state.IsScreenshotEnabled = scEn.GetBoolean();

            if (data.TryGetProperty("screenshotIntervalMinutes", out var scInt) &&
                scInt.ValueKind == JsonValueKind.Number && scInt.TryGetInt32(out var scM) && scM > 0)
                _state.ScreenshotIntervalMinutes = scM;

            if (data.TryGetProperty("screenshotStartTime", out var scSt) &&
                TimeSpan.TryParse(scSt.GetString(), out var stTs))
                _state.ScreenshotStartTime = stTs;

            if (data.TryGetProperty("screenshotEndTime", out var scEt) &&
                TimeSpan.TryParse(scEt.GetString(), out var etTs))
                _state.ScreenshotEndTime = etTs;

            if (data.TryGetProperty("screenshotMonday",    out var d1)) _state.ScreenshotMonday    = d1.GetBoolean();
            if (data.TryGetProperty("screenshotTuesday",   out var d2)) _state.ScreenshotTuesday   = d2.GetBoolean();
            if (data.TryGetProperty("screenshotWednesday", out var d3)) _state.ScreenshotWednesday = d3.GetBoolean();
            if (data.TryGetProperty("screenshotThursday",  out var d4)) _state.ScreenshotThursday  = d4.GetBoolean();
            if (data.TryGetProperty("screenshotFriday",    out var d5)) _state.ScreenshotFriday    = d5.GetBoolean();
            if (data.TryGetProperty("screenshotSaturday",  out var d6)) _state.ScreenshotSaturday  = d6.GetBoolean();
            if (data.TryGetProperty("screenshotSunday",    out var d7)) _state.ScreenshotSunday    = d7.GetBoolean();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not fetch company settings");
        }
    }
}
