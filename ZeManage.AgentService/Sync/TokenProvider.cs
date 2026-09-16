using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.AgentService.Services;

namespace ZeManage.AgentService.Sync;

/// <summary>Device-JWT acquisition against the same backend contract the reference Agent uses
/// (POST /api/v1/tenant/device/auth/register-or-validate-device, then /refresh) — this single
/// endpoint both registers a never-seen-before device and validates/refreshes a known one.</summary>
public sealed class TokenProvider
{
    private readonly IHttpClientFactory _factory;
    private readonly AgentServiceOptions _opts;
    private readonly IdentityService _identity;
    private readonly ILogger<TokenProvider> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _token;
    private string? _refreshToken;
    private DateTime _expiresAt;

    public string? CompanyId { get; private set; }
    public string? CompanyName { get; private set; }
    public string? ZeUserId { get; private set; }

    public TokenProvider(IHttpClientFactory factory, IOptions<AgentServiceOptions> opts, IdentityService identity, ILogger<TokenProvider> log)
    {
        _factory = factory;
        _opts = opts.Value;
        _identity = identity;
        _log = log;
    }

    public async Task<string?> GetAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_token) && _expiresAt - TimeSpan.FromMinutes(2) > DateTime.UtcNow)
            return _token;

        await _lock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(_token) && _expiresAt - TimeSpan.FromMinutes(2) > DateTime.UtcNow)
                return _token;

            if (!string.IsNullOrEmpty(_refreshToken) && await TryRefreshAsync(ct))
                return _token;

            return await ValidateOrRegisterAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Token acquire failed");
            return null;
        }
        finally { _lock.Release(); }
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private async Task<bool> TryRefreshAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await MakeClient().PostAsJsonAsync("/api/v1/tenant/device/auth/refresh", new { refreshToken = _refreshToken }, ct);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadFromJsonAsync<TokenResp>(JsonOpts, ct);
            if (body?.AccessToken is null) return false;
            StoreTokens(body);
            _log.LogInformation("Device token refreshed, expires {Expires:u}", _expiresAt);
            return true;
        }
        catch { return false; }
    }

    private async Task<string?> ValidateOrRegisterAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.LicenseKey))
        {
            _log.LogWarning("No LicenseKey configured — cannot authenticate with backend");
            return null;
        }

        try
        {
            var id = _identity.Get();
            using var resp = await MakeClient().PostAsJsonAsync(
                "/api/v1/tenant/device/auth/register-or-validate-device",
                new { licenseKey = _opts.LicenseKey, machineId = id.MachineId, sid = id.WindowsSid, machineName = id.MachineName, osVersion = id.OsVersion },
                ct);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("Device validation failed: {Status}", resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadFromJsonAsync<TokenResp>(JsonOpts, ct);
            if (body?.AccessToken is null) return null;

            StoreTokens(body);
            _log.LogInformation("Device validated (machineId={MachineId}), expires {Expires:u}", id.MachineId, _expiresAt);
            return _token;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Device validation attempt failed");
            return null;
        }
    }

    private void StoreTokens(TokenResp resp)
    {
        _token = resp.AccessToken!;
        _refreshToken = resp.RefreshToken ?? _refreshToken;
        _expiresAt = resp.TokenExpiry?.ToUniversalTime() ?? DateTime.UtcNow.AddMinutes(55);
        if (!string.IsNullOrEmpty(resp.CompanyId)) CompanyId = resp.CompanyId;
        if (resp.CompanyName is not null) CompanyName = resp.CompanyName;
        var uid = resp.ZeUserId ?? resp.UserId;
        if (uid is not null) ZeUserId = uid;
    }

    private HttpClient MakeClient()
    {
        var client = _factory.CreateClient("backend-auth");
        client.BaseAddress = new Uri(_opts.BackendBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    private sealed class TokenResp
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public DateTime? TokenExpiry { get; set; }
        public string? CompanyId { get; set; }
        public string? CompanyName { get; set; }
        public string? ZeUserId { get; set; }
        public string? UserId { get; set; }
    }
}
