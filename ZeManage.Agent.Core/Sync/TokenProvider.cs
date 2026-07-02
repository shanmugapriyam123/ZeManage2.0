using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.Agent.Core.Services;

namespace ZeManage.Agent.Core.Sync;

public sealed class TokenProvider
{
    private readonly IHttpClientFactory _factory;
    private readonly AgentOptions _opts;
    private readonly ILogger<TokenProvider> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTime _expiresAt;

    public TokenProvider(IHttpClientFactory factory, IOptions<AgentOptions> opts, ILogger<TokenProvider> log)
    {
        _factory = factory;
        _opts = opts.Value;
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

            var client = _factory.CreateClient("backend-auth");
            client.BaseAddress = new Uri(_opts.BackendBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(15);

            var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new
            {
                Email = _opts.BackendEmail,
                Password = _opts.BackendPassword
            }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("Login failed: {Status}", resp.StatusCode);
                return null;
            }
            var body = await resp.Content.ReadFromJsonAsync<LoginResp>(ct);
            if (body is null || string.IsNullOrEmpty(body.Token)) return null;

            _token = body.Token;
            _expiresAt = body.ExpiresAt;
            _log.LogInformation("Backend token acquired, expires {Expires:u}", _expiresAt);
            return _token;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Login attempt failed");
            return null;
        }
        finally { _lock.Release(); }
    }

    private sealed class LoginResp
    {
        public string Token { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
    }
}
