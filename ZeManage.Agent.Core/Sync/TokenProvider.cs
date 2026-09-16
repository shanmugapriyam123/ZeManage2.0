using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Services;

namespace ZeManage.Agent.Core.Sync;

public sealed class TokenProvider
{
    private readonly IHttpClientFactory _factory;
    private readonly AgentOptions _opts;
    private readonly IdentityService _identity;
    private readonly AgentState _state;
    private readonly LocalStore _store;
    private readonly ILogger<TokenProvider> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private string? _refreshToken;
    private DateTime _expiresAt;

    public long    BackendMachineId   { get; private set; }
    public string? MachineIdString   { get; private set; }  // GUID string from validate-device
    public string? CompanyId         { get; private set; }
    public string? CompanyName       { get; private set; }
    public string? ZeUserId          { get; private set; }

    public TokenProvider(
        IHttpClientFactory factory,
        IOptions<AgentOptions> opts,
        IdentityService identity,
        AgentState state,
        LocalStore store,
        ILogger<TokenProvider> log)
    {
        _factory  = factory;
        _opts     = opts.Value;
        _identity = identity;
        _state    = state;
        _store    = store;
        _log      = log;
    }

    // Register/validate/refresh responses are the HTTP-polling fallback for the admin
    // active/inactive toggle (SignalR EmployeeActiveStatusChanged, wired in AgentHubConnection,
    // is the fast path). Applies the server's answer to AgentState immediately and persists it
    // via LocalStore for fail-closed behavior across restarts when offline.
    private async Task ApplyCaptureStateAsync(bool isActive, CancellationToken ct)
    {
        if (_state.IsCaptureEnabled != isActive)
        {
            _state.IsCaptureEnabled = isActive;
            _state.NotifyChanged();
            _log.LogInformation("Capture state changed: {State}", isActive ? "enabled" : "disabled (admin deactivated)");
        }
        try { await _store.SetCaptureEnabledAsync(isActive, ct); }
        catch (Exception ex) { _log.LogDebug(ex, "Failed to persist capture state"); }
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

            var validated = await ValidateDeviceAsync(ct);
            if (validated is not null)
                return validated;

            // Device unknown to the server yet (never registered) — fall back to first-time
            // registration via the installation license key.
            return await RegisterAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Token acquire failed");
            return null;
        }
        finally { _lock.Release(); }
    }

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private async Task<bool> TryRefreshAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await MakeClient().PostAsJsonAsync(
                "/api/v1/tenant/device/auth/refresh",
                new { refreshToken = _refreshToken },
                ct);

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Token refresh failed: {Status} — {Body}", resp.StatusCode, errBody);
                return false;
            }
            var body = await resp.Content.ReadFromJsonAsync<TokenResp>(_jsonOpts, ct);
            if (body?.AccessToken is null) return false;

            StoreTokens(body);
            await ApplyCaptureStateAsync(body.IsActive, ct);
            _log.LogInformation("Device token refreshed, expires {Expires:u}", _expiresAt);
            return true;
        }
        // Was a silent `catch { return false; }` — a genuine backend outage (server down/
        // unreachable) looked identical in the logs to a routine "no refresh token yet" skip,
        // with nothing above Debug to distinguish them. Surfacing the actual exception (timeout,
        // connection refused, etc.) at Warning is what let a real multi-minute backend outage
        // get diagnosed instead of dismissed as "must be a client issue."
        catch (Exception ex) { _log.LogWarning(ex, "Token refresh threw"); return false; }
    }

    // Single endpoint that both registers a new device and validates/refreshes an already-known
    // one — replaces the old separate register-device + validate-device calls. licenseKey is
    // included when available so a not-yet-registered device gets created in the same call;
    // an already-registered device is validated the same way regardless of whether it's present.
    private async Task<string?> ValidateDeviceAsync(CancellationToken ct)
    {
        try
        {
            var id = _identity.Get();
            using var resp = await MakeClient().PostAsJsonAsync(
                "/api/v1/tenant/device/auth/register-or-validate-device",
                new
                {
                    licenseKey  = _opts.LicenseKey,
                    machineId   = id.MachineId,
                    sid         = id.WindowsSid,
                    machineName = id.MachineName,
                    osVersion   = id.OsVersion
                },
                ct);

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Device validation failed: {Status} — {Body}", resp.StatusCode, errBody);
                return null;
            }

            var body = await resp.Content.ReadFromJsonAsync<TokenResp>(_jsonOpts, ct);
            if (body?.AccessToken is null) return null;

            StoreTokens(body);
            await ApplyCaptureStateAsync(body.IsActive, ct);
            _log.LogInformation("Device validated (machineId={MachineId}), expires {Expires:u}", id.MachineId, _expiresAt);
            return _token;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Device validation attempt failed");
            return null;
        }
    }

    // First-time registration — resolves the company from the installation license key.
    // Unlike validate-device/refresh, /agent/register returns a single device token with no
    // refresh token, so the next cycle re-authenticates via validate-device (or re-registers
    // if that still 401s).
    private async Task<string?> RegisterAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.LicenseKey)) return null;

        try
        {
            var id = _identity.Get();
            using var resp = await MakeClient().PostAsJsonAsync(
                "/api/v1/agent/register",
                new
                {
                    licenseKey                = _opts.LicenseKey,
                    deviceId                  = id.DeviceId,
                    machineName               = id.MachineName,
                    windowsUser               = id.UserName,
                    osVersion                 = id.OsVersion,
                    sid                       = id.WindowsSid,
                    machineGuid               = id.MachineId,
                    hostName                  = id.MachineName,
                    osName                    = id.WindowsEdition,
                    processor                 = id.CpuModel,
                    ramgb                     = (int)Math.Round(id.TotalRamGB),
                    diskGB                    = (int)Math.Round(id.StorageTotalGB),
                    macAddress                = id.MacAddress,
                    ipAddress                 = id.IpAddress,
                    agentVersion              = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0",
                    installedDate             = DateTime.UtcNow,
                    domainName                = Environment.UserDomainName,
                    timeZone                  = TimeZoneInfo.Local.Id,
                    status                    = 1,
                    screenshotIntervalSeconds = _opts.ScreenshotIntervalMinutes * 60,
                    capturedAt                = DateTime.UtcNow,
                    ramUsableGb               = id.UsableRamGB,
                    gpuModel                  = id.GpuModel,
                    biosVersion               = id.BiosVersion,
                    motherboardModel          = id.MotherboardModel,
                    storageUsedGb             = id.StorageUsedGB,
                    serialNumber              = id.SerialNumber,
                    systemType                = id.SystemType,
                    windowsEdition            = id.WindowsEdition,
                    windowsVersion            = id.WindowsVersion,
                    osBuild                   = id.OsBuild,
                    isActive                  = true
                },
                ct);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("Agent registration failed: {Status}", resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadFromJsonAsync<RegisterResp>(_jsonOpts, ct);
            if (body?.Token is null) return null;

            _token     = body.Token;
            _expiresAt = DateTime.UtcNow.AddHours(168); // matches the server's fixed device-token lifetime
            if (body.CompanyId is Guid cid && cid != Guid.Empty)
                CompanyId = cid.ToString();
            await ApplyCaptureStateAsync(body.IsActive, ct);

            _log.LogInformation("Agent registered (deviceId={DeviceId}), expires {Expires:u}", id.DeviceId, _expiresAt);
            return _token;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Agent registration attempt failed");
            return null;
        }
    }

    private sealed class RegisterResp
    {
        public string? Token     { get; set; }
        public Guid?   CompanyId { get; set; }
        public bool    IsActive  { get; set; } = true;
    }

    private void StoreTokens(TokenResp resp)
    {
        _token        = resp.AccessToken!;
        _refreshToken = resp.RefreshToken ?? _refreshToken;
        _expiresAt    = resp.TokenExpiry?.ToUniversalTime() ?? DateTime.UtcNow.AddMinutes(55);
        if (!string.IsNullOrEmpty(resp.MachineId))
        {
            MachineIdString = resp.MachineId;
            if (long.TryParse(resp.MachineId, out var mid) && mid > 0)
                BackendMachineId = mid;
        }
        if (!string.IsNullOrEmpty(resp.CompanyId))  CompanyId   = resp.CompanyId;
        if (resp.CompanyName is not null)            CompanyName = resp.CompanyName;
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
        public string?   AccessToken  { get; set; }
        public string?   RefreshToken { get; set; }
        public DateTime? TokenExpiry  { get; set; }
        public string?   MachineId    { get; set; }  // GUID string from backend
        public string?   CompanyId    { get; set; }  // GUID string
        public string?   CompanyName  { get; set; }
        public string?   ZeUserId     { get; set; }
        public string?   UserId       { get; set; }
        public bool      Success      { get; set; }
        public bool      IsActive     { get; set; } = true;
    }
}
