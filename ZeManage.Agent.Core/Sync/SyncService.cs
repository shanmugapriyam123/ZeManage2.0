using System.IO;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using ZeManage.Agent.Core.Data;
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
    private readonly ResiliencePipeline _retry;

    public SyncService(
        LocalStore store,
        IdentityService identity,
        IOptions<AgentOptions> opts,
        ILogger<SyncService> log,
        AgentState state,
        IHttpClientFactory httpFactory,
        TokenProvider tokens)
    {
        _store = store;
        _identity = identity;
        _opts = opts.Value;
        _log = log;
        _state = state;
        _httpFactory = httpFactory;
        _tokens = tokens;

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
        var id = _identity.Get();
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
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (id.ZeUserId.HasValue) client.DefaultRequestHeaders.Add("X-Ze-User-Id", id.ZeUserId.Value.ToString());
        client.DefaultRequestHeaders.Add("X-Machine-Id",      id.MachineId);
        client.DefaultRequestHeaders.Add("X-Agent-OsVersion", id.OsVersion ?? "");
        client.DefaultRequestHeaders.Add("X-Agent-Sid",       id.WindowsSid ?? "");
        if (!string.IsNullOrEmpty(id.CpuModel))     client.DefaultRequestHeaders.Add("X-Agent-CpuModel",     id.CpuModel);
        if (id.TotalRamGB > 0)                       client.DefaultRequestHeaders.Add("X-Agent-RamGB",        id.TotalRamGB.ToString("F1"));
        if (!string.IsNullOrEmpty(id.MacAddress))    client.DefaultRequestHeaders.Add("X-Agent-MacAddress",   id.MacAddress);
        if (!string.IsNullOrEmpty(id.SerialNumber))  client.DefaultRequestHeaders.Add("X-Agent-SerialNumber", id.SerialNumber);
        if (!string.IsNullOrEmpty(id.IpAddress))     client.DefaultRequestHeaders.Add("X-Agent-IpAddress",    id.IpAddress);

        var anyOk = false;

        var att = await _store.GetUnsyncedAttendanceAsync(100, ct);
        if (att.Count > 0 && await TryPostAsync(client, "/api/v1/attendance", att, ct))
        {
            await _store.MarkSyncedAsync(att, ct);
            anyOk = true;
        }

        var apps = await _store.GetUnsyncedApplicationsAsync(200, ct);
        if (apps.Count > 0 && await TryPostAsync(client, "/api/v1/applications", apps, ct))
        {
            await _store.MarkSyncedAsync(apps, ct);
            anyOk = true;
        }

        var hw = await _store.GetUnsyncedHardwareAsync(300, ct);
        if (hw.Count > 0 && await TryPostAsync(client, "/api/v1/hardware", hw, ct))
        {
            await _store.MarkSyncedAsync(hw, ct);
            anyOk = true;
        }

        var net = await _store.GetUnsyncedNetworkAsync(100, ct);
        if (net.Count > 0 && await TryPostAsync(client, "/api/v1/network", net, ct))
        {
            await _store.MarkSyncedAsync(net, ct);
            anyOk = true;
        }

        var bim = await _store.GetUnsyncedBimEventsAsync(100, ct);
        if (bim.Count > 0 && await TryPostAsync(client, "/api/v1/bim-events", bim, ct))
        {
            await _store.MarkSyncedAsync(bim, ct);
            anyOk = true;
        }

        var browser = await _store.GetUnsyncedBrowserActivitiesAsync(200, ct);
        if (browser.Count > 0 && await TryPostAsync(client, "/api/v1/browser", browser, ct))
        {
            await _store.MarkSyncedAsync(browser, ct);
            anyOk = true;
        }

        var shots = await _store.GetUnsyncedScreenshotsAsync(20, ct);
        if (shots.Count > 0)
        {
            var shotDtos = new List<ZeManage.Backend.Contracts.ScreenshotDto>();
            foreach (var s in shots)
            {
                string base64 = "";
                try
                {
                    if (File.Exists(s.FilePath))
                        base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(s.FilePath, ct));
                }
                catch { }
                shotDtos.Add(new ZeManage.Backend.Contracts.ScreenshotDto
                {
                    UserName      = s.UserName,
                    MachineName   = s.MachineName,
                    WindowsSid    = s.WindowsSid,
                    CapturedAt    = s.CapturedAt,
                    TriggerEvent  = s.TriggerEvent,
                    Base64Image   = base64,
                    FileSizeBytes = s.FileSizeBytes
                });
            }
            if (await TryPostAsync(client, "/api/v1/screenshots", shotDtos, ct))
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

    private async Task<bool> TryPostAsync<T>(HttpClient client, string path, IReadOnlyList<T> payload, CancellationToken ct)
    {
        try
        {
            await _retry.ExecuteAsync(async token =>
            {
                using var resp = await client.PostAsJsonAsync(path, payload, token);
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}");
            }, ct);
            _log.LogInformation("Synced {Count} rows to {Path}", payload.Count, path);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Post to {Path} failed", path);
            return false;
        }
    }

    private Task<int> GetUnsyncedTotalAsync(CancellationToken ct) => _store.CountUnsyncedAsync(ct);
}
