using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using ZeManage.AgentService.Data;
using ZeManage.AgentService.Services;

namespace ZeManage.AgentService.Sync;

/// <summary>The ONLY component in this service that talks to the backend API. Every monitor writes
/// exclusively to LocalStore (SQLite) with Synced=false; this service periodically reads whatever
/// is unsynced and POSTs it, with exponential-backoff retry per request and a Polly pipeline for
/// transient failures. If the API is unreachable, unsynced rows simply accumulate in SQLite and get
/// retried on the next cycle — that queue-and-retry behavior IS the offline sync queue, there is no
/// separate queue table. This method also doubles as the heartbeat: a successful cycle (even one
/// with nothing new to send) re-confirms the device is alive by re-posting its identity.</summary>
public sealed class SyncService : BackgroundService
{
    private readonly LocalStore _store;
    private readonly IdentityService _identity;
    private readonly AgentServiceOptions _opts;
    private readonly ILogger<SyncService> _log;
    private readonly AgentHealthState _health;
    private readonly IHttpClientFactory _httpFactory;
    private readonly TokenProvider _tokens;
    private readonly AgentSettings _settings;
    private readonly ResiliencePipeline _retry;
    private DateTime _lastIdentityPostUtc = DateTime.MinValue;
    private static readonly TimeSpan IdentityRepostInterval = TimeSpan.FromMinutes(10);

    public SyncService(
        LocalStore store, IdentityService identity, IOptions<AgentServiceOptions> opts,
        ILogger<SyncService> log, AgentHealthState health, IHttpClientFactory httpFactory, TokenProvider tokens,
        AgentSettings settings)
    {
        _store = store;
        _identity = identity;
        _opts = opts.Value;
        _log = log;
        _health = health;
        _httpFactory = httpFactory;
        _tokens = tokens;
        _settings = settings;

        _retry = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = Math.Max(1, _opts.MaxSyncRetryAttempts),
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
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex)
            {
                _health.LastSyncStatus = $"Error: {ex.Message}";
                _health.IsBackendReachable = false;
                _log.LogWarning(ex, "Sync cycle failed");
            }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var id = _identity.Get();
        _health.UnsyncedCount = await _store.CountUnsyncedAsync(ct);

        var token = await _tokens.GetAsync(ct);
        if (token is null)
        {
            _health.IsBackendReachable = false;
            _health.LastSyncStatus = "Backend unreachable / auth failed (cached locally)";
            _log.LogWarning("Sync skipped — no auth token available");
            return;
        }
        _health.IsBackendReachable = true;

        var client = _httpFactory.CreateClient("backend");
        client.BaseAddress = new Uri(_opts.BackendBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Agent-Id", id.MachineId);

        // Server-configured idle threshold + screenshot enabled/interval/schedule — same
        // endpoint/shape the reference Agent's SyncService.TryFetchCompanySettingsAsync already
        // confirmed working live. Fetched every cycle (cheap GET) so an admin change takes effect
        // within one SyncIntervalSeconds, same as the reference Agent.
        await TryFetchGroupSettingsAsync(client, ct);

        var anyOk = false;

        // Heartbeat: re-POST device identity on a fixed cadence. The endpoint upserts
        // machine_info and refreshes its captured_at, which is what the server-side liveness
        // check reads — this doubles as "Agent heartbeat/health status" without needing a
        // separate endpoint contract that isn't confirmed to exist.
        if (DateTime.UtcNow - _lastIdentityPostUtc >= IdentityRepostInterval)
        {
            var now = DateTime.UtcNow;
            var identityPayload = new
            {
                machineId = id.MachineId,
                sid = id.WindowsSid ?? "",
                windowsUserName = id.UserName,
                hostName = id.MachineName,
                machineName = id.MachineName,
                osName = id.WindowsEdition,
                agentVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0",
                capturedAt = now,
                cpuModel = id.CpuModel,
                ramGb = id.TotalRamGB,
                ramUsableGb = id.UsableRamGB,
                gpuModel = id.GpuModel,
                storageTotalGb = id.StorageTotalGB,
                storageUsedGb = id.StorageUsedGB,
                macAddress = id.MacAddress,
                ipAddress = id.IpAddress,
                serialNumber = id.SerialNumber,
                deviceId = id.DeviceId,
                systemType = id.SystemType,
                osVersion = id.OsVersion,
                windowsEdition = id.WindowsEdition,
                windowsVersion = id.WindowsVersion,
                osBuild = id.OsBuild,
                biosVersion = id.BiosVersion,
                motherboardModel = id.MotherboardModel,
                timezoneId = id.TimeZoneId,
                isActive = true,
                updatedAt = now
            };
            if (await TryPostSingleAsync(client, "/api/v1/agentdb/identity", identityPayload, ct))
            {
                _lastIdentityPostUtc = DateTime.UtcNow;
                _health.LastHeartbeatAtUtc = _lastIdentityPostUtc;
                _log.LogInformation("Heartbeat: identity re-posted for {MachineId}", id.MachineId);
            }
        }

        // Open apps still missing a server sessionId — batch POST
        var openNoSession = await _store.GetOpenWithoutSessionIdAsync(200, ct);
        if (openNoSession.Count > 0)
        {
            var payloads = openNoSession.Select(a => new
            {
                appId = a.ApplicationId,
                applicationName = a.ApplicationName,
                executableName = a.ProcessName,
                type = a.ApplicationType,
                displayName = a.ApplicationName,
                version = a.Version,
                status = "Running",
                createdAt = a.CreatedAt,
                updatedAt = a.UpdatedAt,
                activeSeconds = 0L,
                focusSeconds = 0L,
                idleSeconds = 0L,
                durationSeconds = 0L,
                crashCount = 0
            }).ToArray();

            var results = await TryBatchPostAppOpenAsync(client, "/api/v1/agentdb/applications", payloads, ct);
            for (int i = 0; i < openNoSession.Count && i < results.Count; i++)
            {
                if (results[i] is { } sessionId)
                {
                    await _store.UpdateSessionIdAsync(openNoSession[i].LocalId, sessionId, ct);
                    anyOk = true;
                }
            }
        }

        // Closed apps
        var closedApps = await _store.GetUnsyncedClosedApplicationsAsync(200, ct);
        var closedWithSession = closedApps.Where(a => a.SessionId is not null).ToList();
        var closedNoSession = closedApps.Where(a => a.SessionId is null).ToList();

        if (closedWithSession.Count > 0)
        {
            var dtos = closedWithSession.Select(a => new
            {
                appId = a.ApplicationId,
                applicationName = a.ApplicationName,
                executableName = a.ProcessName,
                type = a.ApplicationType,
                displayName = a.ApplicationName,
                version = a.Version,
                createdAt = a.CreatedAt,
                updatedAt = a.UpdatedAt,
                activeSeconds = a.ActiveSeconds,
                focusSeconds = a.FocusSeconds,
                idleSeconds = a.IdleSeconds,
                durationSeconds = (long)(a.UpdatedAt - a.CreatedAt).TotalSeconds,
                status = a.Status
            }).ToList();
            if (await TryPostAsync(client, "/api/v1/agentdb/applications", dtos, ct))
            {
                await _store.MarkSyncedAsync(closedWithSession, ct);
                anyOk = true;
            }
        }
        if (closedNoSession.Count > 0)
        {
            // Server never opened a session for these (e.g. it closed before the open POST
            // succeeded) — nothing to update server-side, mark synced so the queue doesn't churn.
            await _store.MarkSyncedAsync(closedNoSession, ct);
        }

        // Activity intervals — closed (unsynced) + currently-open (always resent, upserted by localId)
        var closedIntervals = await _store.GetUnsyncedClosedActivityIntervalsAsync(200, ct);
        var openIntervals = await _store.GetOpenActivityIntervalsAsync(ct);
        if (closedIntervals.Count > 0 || openIntervals.Count > 0)
        {
            object ToDto(Models.ActivityInterval iv) => new
            {
                localId = iv.LocalId,
                activity = iv.Activity,
                applicationId = string.IsNullOrEmpty(iv.ApplicationId) ? null : iv.ApplicationId,
                applicationName = iv.ApplicationName,
                processName = iv.ProcessName,
                machineId = id.MachineId,
                intervalStart = iv.StartTime,
                intervalEnd = iv.EndTime,
                activeSeconds = iv.ActiveSeconds,
                focusSeconds = iv.FocusSeconds,
                idleSeconds = iv.IdleSeconds
            };
            var dtos = closedIntervals.Select(ToDto).Concat(openIntervals.Select(ToDto)).ToList();
            if (await TryPostAsync(client, "/api/v1/agentdb/activity-intervals", dtos, ct))
            {
                if (closedIntervals.Count > 0) await _store.MarkSyncedAsync(closedIntervals, ct);
                anyOk = true;
            }
        }

        // Network — send only the latest unsynced snapshot (server stores one-per-capture)
        var net = await _store.GetUnsyncedNetworkAsync(100, ct);
        if (net.Count > 0)
        {
            var latest = net[0]; // already ordered by CapturedAt desc
            var dto = new
            {
                capturedAt = latest.CapturedAt,
                ipAddress = id.IpAddress,
                macAddress = id.MacAddress,
                connectionType = latest.ConnectionType ?? "Ethernet",
                downloadMbps = latest.DownloadMbps,
                uploadMbps = latest.UploadMbps,
                latencyMs = latest.LatencyMs,
                packetLossPercent = latest.PacketLossPercent,
                vpnConnected = latest.VpnConnected,
                adapterName = latest.ActiveAdapter,
                healthScore = latest.HealthScore
            };
            if (await TryPostSingleAsync(client, "/api/v1/agentdb/network-snapshots", dto, ct))
            {
                await _store.MarkSyncedAsync(net, ct);
                anyOk = true;
            }
        }

        // Browser activity — server requires a non-empty Url; records without one are dropped
        // from the queue (marked synced) rather than retried forever.
        var browser = await _store.GetUnsyncedBrowserActivitiesAsync(200, ct);
        if (browser.Count > 0)
        {
            var withUrl = browser.Where(b => !string.IsNullOrEmpty(b.Url)).ToList();
            var withoutUrl = browser.Where(b => string.IsNullOrEmpty(b.Url)).ToList();
            if (withoutUrl.Count > 0) await _store.MarkSyncedAsync(withoutUrl, ct);

            if (withUrl.Count > 0 && !string.IsNullOrEmpty(_tokens.CompanyId) && !string.IsNullOrEmpty(_tokens.ZeUserId))
            {
                static string? Clamp(string? s, int max) => s is { } str && str.Length > max ? str[..max] : s;
                var dtos = withUrl.Select(b => new
                {
                    companyId = _tokens.CompanyId,
                    zeUserId = _tokens.ZeUserId,
                    browserName = b.Browser,
                    processName = b.Browser.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ? "chrome" : "msedge",
                    url = Clamp(b.Url, 500),
                    pageTitle = Clamp(b.PageTitle, 500),
                    startTime = b.StartTime,
                    endTime = b.EndTime,
                    durationSeconds = b.DurationSeconds
                }).ToList();
                if (await TryPostAsync(client, "/api/v1/agentdb/browser-activities", dtos, ct))
                {
                    await _store.MarkSyncedAsync(withUrl, ct);
                    anyOk = true;
                }
            }
        }

        // Screenshots — multipart upload, one at a time (each needs its own file stream)
        var shots = await _store.GetUnsyncedScreenshotsAsync(20, ct);
        foreach (var s in shots)
        {
            if (!File.Exists(s.FilePath))
            {
                _log.LogWarning("Screenshot file missing, giving up on retry: {Path}", s.FilePath);
                await _store.MarkSyncedAsync(new[] { s }, ct);
                continue;
            }
            if (await TryUploadScreenshotAsync(client, s, ct))
                anyOk = true;
        }

        _health.UnsyncedCount = await _store.CountUnsyncedAsync(ct);
        _health.LastSyncAtUtc = DateTime.UtcNow;
        _health.LastSyncStatus = anyOk ? "OK" : _health.UnsyncedCount == 0 ? "Up to date" : "Backend reachable but no data sent this cycle";
        _log.LogInformation("Sync cycle: {Status} — unsynced={Unsynced}", _health.LastSyncStatus, _health.UnsyncedCount);
    }

    private async Task<bool> TryUploadScreenshotAsync(HttpClient client, Models.Screenshot s, CancellationToken ct)
    {
        try
        {
            await using var fileStream = File.OpenRead(s.FilePath);
            using var content = new MultipartFormDataContent();
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Add(fileContent, "image", Path.GetFileName(s.FilePath));
            content.Add(new StringContent(s.CapturedAt.ToString("o")), "capturedAt");
            content.Add(new StringContent(s.TriggerEvent ?? ""), "triggerEvent");
            content.Add(new StringContent(s.ProcessName ?? ""), "processName");

            using var resp = await client.PostAsync("/api/v1/agentdb/screenshots/upload", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Screenshot upload failed: {Status} — {Body}", (int)resp.StatusCode, body);
                return false;
            }
            await _store.MarkSyncedAsync(new[] { s }, ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Screenshot upload failed for {Path}", s.FilePath);
            return false;
        }
    }

    private async Task<List<string?>> TryBatchPostAppOpenAsync<T>(HttpClient client, string path, T[] payload, CancellationToken ct)
    {
        var results = new List<string?>();
        try
        {
            using var resp = await client.PostAsJsonAsync(path, payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Batch app open POST {Status}: {Body}", (int)resp.StatusCode, err);
                return results;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var items = FlattenItems(doc.RootElement);
            foreach (var item in items)
                results.Add(ExtractSessionId(item));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Batch app open POST failed"); }
        return results;
    }

    private static List<JsonElement> FlattenItems(JsonElement root)
    {
        var items = new List<JsonElement>();
        if (root.TryGetProperty("data", out var d))
        {
            if (d.ValueKind == JsonValueKind.Array) items.AddRange(d.EnumerateArray());
            else if (d.ValueKind == JsonValueKind.Object) items.Add(d);
        }
        else if (root.ValueKind == JsonValueKind.Array) items.AddRange(root.EnumerateArray());
        else if (root.ValueKind == JsonValueKind.Object) items.Add(root);
        return items;
    }

    private static string? ExtractSessionId(JsonElement item)
    {
        foreach (var prop in item.EnumerateObject())
        {
            if (prop.Name.Equals("applicationSessionId", StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("sessionId", StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("session_id", StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.ValueKind == JsonValueKind.String) return prop.Value.GetString();
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
            _log.LogWarning(ex, "Post to {Path} failed after retries — body: {Body}", path, errorBody ?? "(no body)");
            return false;
        }
    }

    // Group-scoped settings — "my-group" resolves the caller's own group from its own JWT/
    // identity. Response wraps the settings object in an array ({"data":[...]}) — an employee not
    // assigned to any group gets back {"data":[]}, a legitimate "nothing to apply" response, not
    // an error, handled by FirstOrDefault() + the ValueKind check below. Every field read is
    // defensive (TryGetProperty) so an unrecognized/missing field just leaves the previous value
    // in place instead of throwing or failing the whole sync cycle.
    private async Task TryFetchGroupSettingsAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            using var resp = await client.GetAsync("/api/v1/agentdb/group-worktime-setup/my-group", ct);
            if (!resp.IsSuccessStatusCode) return;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement data = root;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d))
                data = d;
            if (data.ValueKind == JsonValueKind.Array)
                data = data.EnumerateArray().FirstOrDefault();
            if (data.ValueKind != JsonValueKind.Object) return;

            if (data.TryGetProperty("isIdleTrackingEnabled", out var idleEnabled))
                _settings.IsIdleTrackingEnabled = idleEnabled.GetBoolean();

            if (data.TryGetProperty("idleThresholdMinutes", out var idleMin) &&
                idleMin.ValueKind == JsonValueKind.Number && idleMin.TryGetInt32(out var minutes) && minutes > 0)
                _settings.IdleThresholdMinutes = minutes;

            if (data.TryGetProperty("isScreenshotEnabled", out var scEn))
                _settings.IsScreenshotEnabled = scEn.GetBoolean();

            if (data.TryGetProperty("screenshotIntervalMinutes", out var scInt) &&
                scInt.ValueKind == JsonValueKind.Number && scInt.TryGetInt32(out var scM) && scM > 0)
                _settings.ScreenshotIntervalMinutes = scM;

            if (data.TryGetProperty("activeWindowStartTime", out var scSt) &&
                TimeSpan.TryParse(scSt.GetString(), out var stTs))
                _settings.ScreenshotStartTime = stTs;

            if (data.TryGetProperty("activeWindowEndTime", out var scEt) &&
                TimeSpan.TryParse(scEt.GetString(), out var etTs))
                _settings.ScreenshotEndTime = etTs;

            if (data.TryGetProperty("isActiveWindowEnabled", out var actWinEn))
                _settings.IsActiveWindowEnabled = actWinEn.ValueKind == JsonValueKind.True;

            if (data.TryGetProperty("screenshotMonday", out var d1)) _settings.ScreenshotMonday = d1.GetBoolean();
            if (data.TryGetProperty("screenshotTuesday", out var d2)) _settings.ScreenshotTuesday = d2.GetBoolean();
            if (data.TryGetProperty("screenshotWednesday", out var d3)) _settings.ScreenshotWednesday = d3.GetBoolean();
            if (data.TryGetProperty("screenshotThursday", out var d4)) _settings.ScreenshotThursday = d4.GetBoolean();
            if (data.TryGetProperty("screenshotFriday", out var d5)) _settings.ScreenshotFriday = d5.GetBoolean();
            if (data.TryGetProperty("screenshotSaturday", out var d6)) _settings.ScreenshotSaturday = d6.GetBoolean();
            if (data.TryGetProperty("screenshotSunday", out var d7)) _settings.ScreenshotSunday = d7.GetBoolean();

            _settings.LastFetchedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not fetch group worktime settings");
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
            _log.LogWarning(ex, "Post to {Path} failed after retries — body: {Body}", path, errorBody ?? "(no body)");
            return false;
        }
    }
}
