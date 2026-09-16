using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Models;
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
    private readonly ScreenshotMonitor _screenshotMonitor;
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
        ProcessMonitor processMonitor,
        ScreenshotMonitor screenshotMonitor)
    {
        _store             = store;
        _identity          = identity;
        _opts              = opts.Value;
        _log               = log;
        _state             = state;
        _httpFactory       = httpFactory;
        _tokens            = tokens;
        _processMonitor    = processMonitor;
        _screenshotMonitor = screenshotMonitor;

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
            try { await Task.WhenAny(Task.Delay(interval, stoppingToken), _state.SyncTrigger.WaitAsync(stoppingToken)); }
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

        // Register device identity once per session.
        // Skip if CompanyId not yet loaded from token — retry next cycle rather than send "" and get 400.
        if (!_identityPosted && Guid.TryParse(_tokens.CompanyId, out var companyGuid))
        {
            var now = DateTime.UtcNow;
            var identityPayload = new
            {
                machineId                 = id.MachineId,           // string GUID — backend requires string
                companyId                 = companyGuid,
                companyName               = _tokens.CompanyName ?? "",
                zeUserId                  = _tokens.ZeUserId ?? "",
                timeZone                  = TimeZoneInfo.Local.Id,
                status                    = 1,
                screenshotIntervalSeconds = _opts.ScreenshotIntervalMinutes * 60,
                sid                       = id.WindowsSid ?? "",
                windowsUserName           = id.UserName,
                hostName                  = id.MachineName,
                machineName               = id.MachineName,
                osName                    = id.WindowsEdition,
                agentVersion              = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0",
                installedDate             = now,
                licenseKey                = Guid.TryParse(_opts.LicenseKey, out var lkGuid) ? (Guid?)lkGuid : null,
                capturedAt                = now,
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
                motherboardModel          = id.MotherboardModel,
                timezoneId                = id.TimeZoneId,
                startDate                 = now,
                endDate                   = now,
                isActive                  = true,
                createdAt                 = now,
                updatedAt                 = now
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
                iconUrl         = (string?)null,   // real icon goes via POST /applications/{applicationId}/icon (binary)
                status          = "Running",
                createdAt       = a.CreatedAt,
                updatedAt       = a.UpdatedAt,
                activeSeconds   = (long)0,
                focusSeconds    = (long)0,
                idleSeconds     = (long)0,
                durationSeconds = (long)0,
                crashCount      = 0
            }).ToArray();

            // Matched by array position, not by executableName — a batch can contain multiple
            // launches of the same executable (e.g. several Compil32/setup.exe instances), and
            // the server returns results in the same order it received them. A name-keyed lookup
            // would incorrectly collapse all same-named launches onto a single shared session.
            var results = await TryBatchPostAppOpenAsync(client, "/api/v1/agentdb/applications", payloads, ct);

            for (int i = 0; i < runningNoSession.Count && i < results.Count; i++)
            {
                var app = runningNoSession[i];
                var (sessionId, serverApplicationId) = results[i];
                if (sessionId is not null)
                {
                    await _store.UpdateSessionIdAsync(app.LocalId, sessionId, ct);
                    _processMonitor.UpdateInMemorySessionId(app.LocalId, sessionId);
                    _log.LogInformation("sessionId set: {App} → {SessionId}", app.ApplicationName, sessionId);
                    anyOk = true;
                }

                if (serverApplicationId is not null && app.IconBase64 is not null)
                    await UploadIconAsync(client, serverApplicationId, app.IconBase64!, app.ApplicationName, ct);
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
                iconUrl         = (string?)null,   // real icon goes via POST /applications/{applicationId}/icon (binary)
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

        // Batch POST focus-interval rows for the Activities Timeline — REST-only, no SignalR
        // involvement. Two kinds go in the same batch every cycle:
        //   1. Closed, unsynced segments — final, marked Synced after a successful POST.
        //   2. The CURRENTLY OPEN segment (if any) — re-sent every cycle regardless of Synced,
        //      upserted server-side by localId, so the timeline can always show a "Running" row
        //      for whatever's happening right now instead of only learning about a segment once
        //      it closes (which, for someone who stays on one app/state all day, could otherwise
        //      mean nothing ever appears).
        // A ~60s delay here is fine either way; this feeds a look-back timeline, not a live
        // indicator — the live "Task" column is still ReportLiveActivity over SignalR, untouched.
        var closedIntervals = await _store.GetUnsyncedClosedActivityIntervalsAsync(200, ct);
        var openIntervals   = await _store.GetOpenActivityIntervalsAsync(ct);

        if (closedIntervals.Count > 0 || openIntervals.Count > 0)
        {
            static object ToDto(ActivityInterval iv, string machineId) => new
            {
                localId         = iv.LocalId,
                activity        = iv.Activity,
                applicationId   = string.IsNullOrEmpty(iv.ApplicationId)   ? null : iv.ApplicationId,
                applicationName = string.IsNullOrEmpty(iv.ApplicationName) ? null : iv.ApplicationName,
                processName     = string.IsNullOrEmpty(iv.ProcessName)    ? null : iv.ProcessName,
                machineId       = machineId,
                intervalStart   = iv.StartTime,
                intervalEnd     = iv.EndTime,
                activeSeconds   = iv.ActiveSeconds,
                focusSeconds    = iv.FocusSeconds,
                idleSeconds     = iv.IdleSeconds
            };

            var intervalDtos = closedIntervals.Select(iv => ToDto(iv, machineId))
                .Concat(openIntervals.Select(iv => ToDto(iv, machineId)))
                .ToList();

            if (await TryPostAsync(client, "/api/v1/agentdb/activity-intervals", intervalDtos, ct))
            {
                if (closedIntervals.Count > 0)
                    await _store.MarkSyncedAsync(closedIntervals, ct);
                anyOk = true;
            }
        }

        var net = await _store.GetUnsyncedNetworkAsync(100, ct);
        if (net.Count > 0)
        {
            // Send only the latest snapshot — server stores one-per-capture
            var latest = net.OrderByDescending(n => n.CapturedAt).First();
            var dto = new
            {
                capturedAt        = latest.CapturedAt,
                ipAddress         = id.IpAddress,
                macAddress        = id.MacAddress,
                connectionType    = latest.ConnectionType ?? "Ethernet",
                downloadMbps      = latest.DownloadMbps,
                uploadMbps        = latest.UploadMbps,
                latencyMs         = latest.LatencyMs,
                packetLossPercent = latest.PacketLossPercent,
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

            // Server requires non-empty companyId/zeUserId (GUID format) — skip this cycle
            // rather than send invalid empty strings if tokens haven't loaded yet.
            if (withUrl.Count > 0)
            {
                if (string.IsNullOrEmpty(_tokens.CompanyId) || string.IsNullOrEmpty(_tokens.ZeUserId))
                {
                    _log.LogWarning("Browser sync skipped — CompanyId={CompanyId} ZeUserId={ZeUserId} not yet loaded from token",
                        _tokens.CompanyId ?? "(null)", _tokens.ZeUserId ?? "(null)");
                }
                else
                {
                    // Server column is genuinely varchar(100) (confirmed via a live 22001 "value too
                    // long for type character varying(100)" error, not varchar(500) — clamping to
                    // 500 here let oversized values through, and since this posts a whole batch in
                    // one request, a single over-limit item failed EVERY browser activity in that
                    // sync cycle, not just itself.
                    static string? Clamp(string? s, int max) => s is { } str && str.Length > max ? str[..max] : s;

                    var dtos = withUrl.Select(b => new
                    {
                        companyId       = _tokens.CompanyId,
                        zeUserId        = _tokens.ZeUserId,
                        browserName     = b.Browser,
                        processName     = b.Browser.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ? "chrome" : "msedge",
                        url             = Clamp(b.Url, 100),
                        pageTitle       = Clamp(b.PageTitle, 100),
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
        }

        // There is no "resubmit metadata" endpoint for a screenshot that failed its
        // immediate upload — /api/v1/agentdb/screenshots expects a screenshotId +
        // blobUrl that only exist AFTER a successful upload. So retrying means
        // re-doing the actual multipart upload via ScreenshotMonitor, not a
        // separate lightweight POST.
        var shots = await _store.GetUnsyncedScreenshotsAsync(20, ct);
        foreach (var s in shots)
        {
            if (!File.Exists(s.FilePath))
            {
                _log.LogWarning("Screenshot file missing, giving up on retry: {Path}", s.FilePath);
                await _store.MarkSyncedAsync(new[] { s }, ct);
                continue;
            }
            await _screenshotMonitor.UploadAndNotifyAsync(s, s.FilePath, s.ApplicationId, null, ct);
        }

        totalUnsynced = await GetUnsyncedTotalAsync(ct);
        _state.UnsyncedCount = totalUnsynced;
        _state.LastSyncAt = DateTime.Now;
        _state.LastSyncStatus = anyOk
            ? "OK"
            : totalUnsynced == 0 ? "Up to date" : "Backend reachable but no data sent";
        _state.NotifyChanged();
    }

    // POSTs all apps in one batch. Returns (sessionId, serverApplicationId) keyed by executableName.
    // Matching by executableName is reliable because the server always echoes it back and it
    // equals the agent's ProcessName — no dependency on response order or nullable AppId.
    private async Task<List<(string? sessionId, string? applicationId)>> TryBatchPostAppOpenAsync<T>(
        HttpClient client, string path, T[] payload, CancellationToken ct)
    {
        var results = new List<(string?, string?)>();
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

            // Order matters here — the caller zips this list against the request array by
            // position, since multiple queued launches can share the same executableName and a
            // name-keyed lookup would incorrectly collapse them onto one shared session.
            foreach (var item in items)
            {
                var sid = ExtractSessionId(item);
                var aid = item.TryGetProperty("applicationId", out var aidEl) && aidEl.ValueKind == System.Text.Json.JsonValueKind.String
                    ? aidEl.GetString() : null;

                if (sid is null)
                    _log.LogWarning("No sessionId in batch open response item — raw: {Raw}", item.GetRawText());

                results.Add((sid, aid));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Batch app open POST failed");
        }
        return results;
    }

    // Uploads the app's icon as binary via the dedicated endpoint — mirrors
    // ProcessMonitor.UploadIconAsync for apps whose open-POST only succeeds
    // on this retry path (e.g. the primary attempt hit a transient network error).
    private async Task UploadIconAsync(HttpClient client, string serverApplicationId, string iconBase64, string appName, CancellationToken ct)
    {
        try
        {
            var bytes = Convert.FromBase64String(iconBase64);
            using var content = new MultipartFormDataContent();
            var byteContent = new ByteArrayContent(bytes);
            byteContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(byteContent, "Icon", "icon.png");

            using var resp = await client.PostAsync($"/api/v1/agentdb/applications/{serverApplicationId}/icon", content, ct);
            if (resp.IsSuccessStatusCode)
                _log.LogInformation("Icon uploaded: {App} → {AppId}", appName, serverApplicationId);
            else
                _log.LogWarning("Icon upload failed: {App} → {Status}", appName, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Icon upload failed for {App}", appName);
        }
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

    // Called once at startup, before Host.StartAsync, so a device that has previously synced
    // real group settings keeps enforcing them (break window, screenshot schedule/interval,
    // idle threshold) from the very first tick instead of running blank until the first live
    // /my-group fetch succeeds — mirrors how IsCaptureEnabled is loaded from cache early in
    // App.xaml.cs. A no-op if nothing has ever been persisted (fresh install).
    public async Task ApplyPersistedGroupSettingsAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _store.GetGroupSettingsRawAsync(ct);
            if (string.IsNullOrEmpty(json)) return;

            using var doc = JsonDocument.Parse(json);
            ApplyGroupSettings(doc.RootElement.Clone());
            _log.LogInformation("Group settings restored from local cache");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not apply cached group settings");
        }
    }

    private async Task TryFetchCompanySettingsAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            // Group-scoped settings only — "my-group" resolves the caller's own group from its
            // own JWT/identity, same self-service convention as the other agentdb endpoints.
            // Field-parsing below stays defensive (TryGetProperty per field) either way — an
            // unrecognized/missing field just leaves that one setting at its last-known value
            // instead of failing the whole sync.
            var data = await FetchGroupSettingsDataAsync(client, "/api/v1/agentdb/group-worktime-setup/my-group", ct);

            if (data is { } settings)
            {
                ApplyGroupSettings(settings);
                // Persist on every successful fetch so a later empty/failed response (backend
                // hiccup, transient group-assignment gap) or a process restart doesn't wipe
                // these fields back to blank — see ApplyPersistedGroupSettingsAsync.
                try { await _store.SetGroupSettingsRawAsync(settings.GetRawText(), ct); }
                catch (Exception ex) { _log.LogDebug(ex, "Could not persist group settings"); }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not fetch company settings");
        }
    }

    // Fetches one group-worktime-setup route and returns its unwrapped settings object, or null
    // if the route failed, returned no body, or returned an empty/absent data array.
    private async Task<JsonElement?> FetchGroupSettingsDataAsync(HttpClient client, string path, CancellationToken ct)
    {
        try
        {
            using var resp = await client.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Was a silent `return null` — a sustained failure here (auth glitch, server
                // error) looked identical in the logs to a routine "no group assigned" empty
                // response, with nothing above Debug to tell them apart. Surfacing the actual
                // status is what would have caught the settings-fetch stall that persisted for
                // ~26 hours before a restart happened to clear it.
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Group settings fetch failed for {Path}: {Status} — {Body}", path, resp.StatusCode, errBody);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement data = root;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d))
                data = d;

            // Both routes wrap their settings object in an array ({"data":[...]}) — at most one
            // entry for "my-group" (the caller's own group), possibly more for "default" (only the
            // first/active one matters here).
            if (data.ValueKind == JsonValueKind.Array)
                data = data.EnumerateArray().FirstOrDefault();

            return data.ValueKind == JsonValueKind.Object ? data.Clone() : null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Group settings fetch failed for {Path}", path);
            return null;
        }
    }

    private void ApplyGroupSettings(JsonElement data)
    {
        try
        {
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

            // activeWindowStartTime/EndTime come back as full ISO datetimes (e.g.
            // "0001-01-02T03:30:00+00:00"), the same DateTime?-not-TimeOnly? shape as
            // breakStartTime/breakEndTime below — TimeSpan.TryParse silently fails on this
            // format (it expects plain "HH:mm:ss"), which was leaving ScreenshotStartTime/
            // EndTime stuck at their 9am-8pm client defaults instead of the server's actual
            // configured window. Same RoundtripKind + ToLocalTime() fix as breakStartTime.
            if (data.TryGetProperty("activeWindowStartTime", out var scSt) &&
                DateTime.TryParse(scSt.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var scStDt))
                _state.ScreenshotStartTime = scStDt.ToLocalTime().TimeOfDay;

            if (data.TryGetProperty("activeWindowEndTime", out var scEt) &&
                DateTime.TryParse(scEt.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var scEtDt))
                _state.ScreenshotEndTime = scEtDt.ToLocalTime().TimeOfDay;

            if (data.TryGetProperty("isActiveWindowEnabled", out var actWinEn))
                _state.IsActiveWindowEnabled = actWinEn.ValueKind == JsonValueKind.True;

            // Break Time Setup — breakStartTime/breakEndTime come back as full ISO datetimes
            // (e.g. "2026-08-10T10:24:59.185Z"), not plain "HH:mm"/"HH:mm:ss" like
            // activeWindowStartTime/EndTime above — the server-side field turned out to be
            // DateTime?, not TimeOnly?/TimeSpan (confirmed by the frontend team hitting the same
            // thing). RoundtripKind preserves the 'Z' as UTC so ToLocalTime() correctly converts
            // to this machine's wall-clock time before taking just the time-of-day, matching how
            // the value was originally encoded (a chosen LOCAL time, shifted to UTC on save).
            if (data.TryGetProperty("isBreakTimeEnabled", out var brkEnabled))
                _state.IsBreakTimeEnabled = brkEnabled.ValueKind == JsonValueKind.True;

            if (data.TryGetProperty("flexibleBreak", out var flexBreak))
                _state.IsFlexibleBreak = flexBreak.ValueKind == JsonValueKind.True;

            // Fixed IST offset (AgentState.ToIstTimeOfDay), not .ToLocalTime() — the server encodes
            // these as genuine UTC instants of the admin's IST wall-clock input, so recovering the
            // intended break window must not depend on this agent machine's own OS timezone
            // setting (see AgentState.IsInActiveBreakWindow for the full explanation).
            if (data.TryGetProperty("breakStartTime", out var brkSt) &&
                DateTime.TryParse(brkSt.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var brkStDt))
                _state.BreakStartTime = AgentState.ToIstTimeOfDay(brkStDt.ToUniversalTime());

            if (data.TryGetProperty("breakEndTime", out var brkEt) &&
                DateTime.TryParse(brkEt.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var brkEtDt))
                _state.BreakEndTime = AgentState.ToIstTimeOfDay(brkEtDt.ToUniversalTime());

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
            _log.LogDebug(ex, "Could not apply group settings");
        }
    }
}
