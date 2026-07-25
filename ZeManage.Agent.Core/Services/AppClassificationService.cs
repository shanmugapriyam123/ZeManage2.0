using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.Agent.Core.Sync;

namespace ZeManage.Agent.Core.Services;

/// <summary>
/// Fetches productive/unproductive app classifications from backend and caches in memory.
/// Classifications are set by admins in the web portal and refreshed every hour.
/// Returns null for unclassified apps (never set by admin).
/// </summary>
public sealed class AppClassificationService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly AgentOptions _opts;
    private readonly TokenProvider _tokens;
    private readonly ILogger<AppClassificationService> _log;

    private Dictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRefresh = DateTime.MinValue;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AppClassificationService(
        IHttpClientFactory httpFactory,
        IOptions<AgentOptions> opts,
        TokenProvider tokens,
        ILogger<AppClassificationService> log)
    {
        _httpFactory = httpFactory;
        _opts        = opts.Value;
        _tokens      = tokens;
        _log         = log;
    }

    /// <summary>Returns true/false if classified by admin; null if not configured.</summary>
    public bool? IsProductive(string processName)
    {
        if (_cache.TryGetValue(processName, out var val)) return val;
        return null;
    }

    public async Task RefreshIfStaleAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastRefresh < RefreshInterval) return;

        await _lock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _lastRefresh < RefreshInterval) return;
            await FetchAsync(ct);
        }
        finally { _lock.Release(); }
    }

    private async Task FetchAsync(CancellationToken ct)
    {
        try
        {
            var token = await _tokens.GetAsync(ct);
            if (token is null) return;

            var client = _httpFactory.CreateClient("backend");
            client.BaseAddress = new Uri(_opts.BackendBaseUrl);
            client.Timeout     = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await client.GetAsync("/api/v1/agent/app-classifications", ct);
            if (!resp.IsSuccessStatusCode) return;

            var items = await resp.Content.ReadFromJsonAsync<List<ClassificationDto>>(ct);
            if (items is null) return;

            var newCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
                if (!string.IsNullOrEmpty(item.ProcessName))
                    newCache[item.ProcessName] = item.IsProductive;

            _cache        = newCache;
            _lastRefresh  = DateTime.UtcNow;
            _log.LogInformation("App classifications refreshed: {Count} entries", newCache.Count);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to fetch app classifications from backend");
        }
    }

    private sealed class ClassificationDto
    {
        public string? ProcessName { get; set; }
        public bool    IsProductive { get; set; }
    }
}
