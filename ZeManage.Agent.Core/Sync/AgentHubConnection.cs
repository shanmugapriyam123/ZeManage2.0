using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ZeManage.Agent.Core.Services;

namespace ZeManage.Agent.Core.Sync;

public sealed class AgentHubConnection : BackgroundService
{
    private readonly IdentityService _identity;
    private readonly AgentState _state;
    private readonly AgentOptions _opts;
    private readonly TokenProvider _tokens;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AgentHubConnection> _log;
    private HubConnection? _hub;

    /// <summary>Raised as soon as the hub finishes an initial or automatic
    /// reconnect and is Connected again. Consumers (ProcessMonitor) subscribe
    /// so they can immediately re-broadcast the current foreground app instead
    /// of waiting for the next 5-second scan tick — cuts the perceived
    /// dashboard freeze after a server restart from ~5-10s down to ~1s.</summary>
    public event Action? HubConnectedOrReconnected;

    // Returns true if the event was actually delivered — via SignalR when it is
    // healthy, otherwise via an HTTP fallback so a transient hub drop can never
    // silently lose an app-switch event. This is the piece that turns the
    // portal's live view from "works only for a few seconds after every server
    // restart" into a genuinely stable Insightful-style stream.
    public async Task<bool> TrySendEventAsync(string method, object? arg, CancellationToken ct = default)
    {
        var hub = _hub;
        if (hub?.State == HubConnectionState.Connected)
        {
            // Guard every invocation with a 20-s client-side timeout. Without
            // it, a stalled server call (Postgres/Redis spike, telemetry
            // queue backpressure) blocks the tick loop indefinitely and the
            // server's 300-s ClientTimeoutInterval eventually fires — that's
            // the recurring 5–6 min drop cascade. 20 s is long enough for
            // normal turnarounds and short enough that reconnect kicks in
            // before the server declares us dead.
            using var invokeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            invokeCts.CancelAfter(TimeSpan.FromSeconds(20));

            try
            {
                // ReportAgentActivity returns a per-item result list nobody
                // reads on the client — using SendAsync (fire-and-forget)
                // avoids blocking the tick loop on that return round-trip.
                // ReportLiveActivity is a lightweight ping we DO want to
                // observe for logging, so still InvokeAsync.
                if (method == "ReportAgentActivity")
                    await hub.SendAsync(method, arg, invokeCts.Token).ConfigureAwait(false);
                else
                    await hub.InvokeAsync(method, arg, invokeCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (invokeCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _log.LogWarning("[Hub] Send {Method} timed out after 20 s — falling back to HTTP", method);
            }
            catch (InvalidOperationException)
            {
                // Race with hub.Closed setting _hub = null while we captured
                // the old reference. Fall through to HTTP fallback.
                _log.LogDebug("[Hub] Send {Method} raced a Closed transition — falling back to HTTP", method);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[Hub] Send {Method} failed via SignalR — will try HTTP fallback", method);
            }
        }
        else
        {
            _log.LogDebug("[Hub] SignalR unavailable (state={State}) — using HTTP fallback for {Method}",
                hub?.State.ToString() ?? "null", method);
        }

        // HTTP fallback so the event still reaches the server while SignalR is
        // in Reconnecting / Disconnected. Server processes these calls through
        // the same AgentActivityService pipeline that the hub methods delegate
        // to, so LiveStatusChanged still fires and the portal Task column keeps
        // updating even during a hub dead window.
        return await TryHttpFallbackAsync(method, arg, ct);
    }

    private async Task<bool> TryHttpFallbackAsync(string method, object? arg, CancellationToken ct)
    {
        try
        {
            var path = MapMethodToHttpPath(method);
            if (path is null)
            {
                _log.LogDebug("[Hub] HTTP fallback has no route for method {Method} — dropping", method);
                return false;
            }

            var token = await _tokens.GetAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
            {
                _log.LogDebug("[Hub] HTTP fallback skipped — no token available");
                return false;
            }

            var client = _httpFactory.CreateClient("backend-agent");
            client.BaseAddress = new Uri(_opts.BackendBaseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // 30 s, not 10 — the fallback fires exactly when the server is
            // momentarily busy (the same stall that dropped SignalR); a 10-s
            // budget was observed timing out in that window, losing the very
            // events this path exists to save.
            client.Timeout = TimeSpan.FromSeconds(30);

            using var resp = await client.PostAsJsonAsync(path, arg, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                _log.LogInformation("[Hub] HTTP fallback sent {Method} → {Path}", method, path);
                return true;
            }

            _log.LogWarning("[Hub] HTTP fallback for {Method} returned {Status}", method, (int)resp.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Hub] HTTP fallback for {Method} threw", method);
            return false;
        }
    }

    private static string? MapMethodToHttpPath(string method) => method switch
    {
        // The lightweight foreground-change ping — this is the one that keeps
        // the portal's Task column live-updating. When the SignalR hub is
        // Reconnecting, hitting the REST endpoint delivers the same payload to
        // the same server-side handler (AgentActivityService.ReportLiveActivityAsync),
        // which persists to Redis and broadcasts the LiveStatusChanged event
        // to the admin group.
        "ReportLiveActivity"  => "/api/v1/agentdb/live-activity",
        "ReportAgentActivity" => "/api/v1/agentdb/agent-activities",
        _                     => null,
    };

    public AgentHubConnection(
        IdentityService identity,
        AgentState state,
        IOptions<AgentOptions> opts,
        TokenProvider tokens,
        IHttpClientFactory httpFactory,
        ILogger<AgentHubConnection> log)
    {
        _identity    = identity;
        _state       = state;
        _opts        = opts.Value;
        _tokens      = tokens;
        _httpFactory = httpFactory;
        _log         = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.EnableSignalR) return;

        var id = _identity.Get();
        _state.HubConnectionState = "Connecting";

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var token = await _tokens.GetAsync(stoppingToken);
                if (token is null)
                {
                    _state.HubConnectionState = "Auth Failed";
                    _state.NotifyChanged();
                    await SafeDelay(30, stoppingToken);
                    continue;
                }

                var hubUrl = $"{_opts.BackendBaseUrl.TrimEnd('/')}/hubs/notifications";

                var hub = new HubConnectionBuilder()
                    .WithUrl(hubUrl, o =>
                    {
                        // Refresh the token on EVERY request instead of baking in
                        // the value captured at connection start. Long-lived hub
                        // connections outlive a single token's ~5 min lifetime;
                        // freezing the value here meant every negotiate/poll/reconnect
                        // used a stale JWT after the first expiry, the server
                        // dropped the connection with 401, WithAutomaticReconnect
                        // fired repeatedly with the same dead token, and no live
                        // events could reach the portal until either the process
                        // restarted or the user manually toggled the API. Calling
                        // _tokens.GetAsync() here is cheap: TokenProvider caches
                        // and only refreshes ~2 min before the actual expiry.
                        //
                        // NOTE: return null on failure — do NOT fall back to the
                        // captured `token` local. That original JWT expired hours
                        // ago; using it would produce a 401 that WithAutomaticReconnect
                        // would then retry with the SAME dead token forever.
                        // Returning null lets SignalR treat this as an auth failure
                        // and enter a fresh Reconnecting cycle that re-invokes this
                        // provider — TokenProvider will retry validate-device on
                        // the next call and hand back a live token.
                        o.AccessTokenProvider = async () =>
                            await _tokens.GetAsync(CancellationToken.None).ConfigureAwait(false);
                        if (_opts.AllowInsecureSsl)
                        {
                            o.HttpMessageHandlerFactory = _ => new HttpClientHandler
                            {
                                ServerCertificateCustomValidationCallback =
                                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                            };
                        }
                    })
                    // Aggressive retry pattern — fires reconnect attempts at 1s,
                    // 2s, 5s, 10s, 20s, 30s, then every minute up to 20 attempts.
                    // The default [5,15,30,60] leaves the hub Reconnecting for up
                    // to 45 seconds after a drop, during which every ReportLiveActivity
                    // send is skipped and the portal's Task column freezes until
                    // the hub recovers. This tight schedule shrinks the average
                    // dead window to ~1-2 seconds so live updates feel unbroken.
                    .WithAutomaticReconnect(new[]
                    {
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(10),
                        TimeSpan.FromSeconds(20),
                        TimeSpan.FromSeconds(30),
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromMinutes(1),
                    })
                    .Build();

                // Client-side liveness tuning: match the server's SignalR defaults
                // (KeepAliveInterval=30s, ClientTimeoutInterval=120s in
                // CachingExtensions.AddCachingAndSignalR) so a transient TCP stall
                // isn't misread as a dead connection. HandshakeTimeout is bumped
                // so the initial negotiate survives brief server jitters during
                // deploys or Redis backplane hiccups.
                hub.ServerTimeout      = TimeSpan.FromSeconds(120);
                hub.KeepAliveInterval  = TimeSpan.FromSeconds(15);
                hub.HandshakeTimeout   = TimeSpan.FromSeconds(30);

                hub.On<string>("ForceLogout", machine =>
                {
                    if (machine == id.MachineName || machine == "*")
                    {
                        _log.LogWarning("[Hub] Server forced logout");
                        _state.ForceLogoutRequested = true;
                        _state.PushEvent("[Server] Force logout command received");
                        _state.NotifyChanged();
                    }
                });

                hub.On<string>("Shutdown", machine =>
                {
                    if (machine == id.MachineName || machine == "*")
                    {
                        _log.LogWarning("[Hub] Server requested shutdown");
                        _state.ShutdownRequested = true;
                        _state.PushEvent("[Server] Shutdown command received");
                        _state.NotifyChanged();
                    }
                });

                hub.On<string>("RefreshConfig", _ =>
                {
                    _state.PushEvent("[Server] Config refresh requested");
                });

                hub.Reconnecting += ex =>
                {
                    _state.IsHubConnected    = false;
                    _state.HubConnectionState = "Reconnecting";
                    _state.NotifyChanged();
                    // Log the underlying exception so we can see WHY the connection
                    // dropped — the default handler swallowed it, leaving the log
                    // with only "Reconnecting" and no hint at the root cause
                    // (auth failure vs poll timeout vs server-side kill).
                    _log.LogWarning(ex, "[Hub] Connection dropped — starting automatic reconnect. Reason: {Reason}",
                        ex?.Message ?? "no exception provided");
                    return Task.CompletedTask;
                };

                hub.Reconnected += connId =>
                {
                    _state.IsHubConnected    = true;
                    _state.HubConnectionState = "Connected";
                    _state.NotifyChanged();
                    // RegisterAgent intentionally removed — the server-side
                    // NotificationHub doesn't define this method, and every
                    // invocation returned HubException("Failed to invoke
                    // 'RegisterAgent'…"), polluting the client's error stream
                    // and (under some transports) racing against subsequent
                    // sends. Signal subscribers directly instead.
                    try { HubConnectedOrReconnected?.Invoke(); }
                    catch (Exception ex) { _log.LogWarning(ex, "[Hub] HubConnectedOrReconnected handler threw"); }
                    return Task.CompletedTask;
                };

                hub.Closed += _ =>
                {
                    _hub = null;
                    _state.IsHubConnected    = false;
                    _state.HubConnectionState = "Disconnected";
                    _state.NotifyChanged();
                    return Task.CompletedTask;
                };

                await hub.StartAsync(stoppingToken);
                _hub = hub;

                // Previously we called `InvokeAsync("RegisterAgent", …)` here as
                // an optional handshake. The server-side NotificationHub does
                // NOT expose that method, so every attempt returned
                // HubException("Failed to invoke 'RegisterAgent' due to an
                // error on the server"). Under LongPolling that exception
                // showed up on the connection's error stream and could
                // destabilise the next few sends. Removed entirely — SignalR
                // group membership is already handled server-side by
                // NotificationHub.OnConnectedAsync via JWT claims.

                _state.IsHubConnected    = true;
                _state.HubConnectionState = "Connected";
                _state.NotifyChanged();
                _state.PushEvent("[Hub] Connected to backend");
                _log.LogInformation("[Hub] Connected to {Url}", hubUrl);

                // Fire subscribers (ProcessMonitor) so they can immediately push the
                // current foreground app on this connection instead of waiting for
                // the next scan tick — matters most when the outer loop restarts the
                // hub from scratch after a fatal drop or a fresh startup.
                try { HubConnectedOrReconnected?.Invoke(); }
                catch (Exception ex) { _log.LogWarning(ex, "[Hub] HubConnectedOrReconnected handler threw"); }

                var heartbeatInterval = TimeSpan.FromSeconds(_opts.HeartbeatIntervalSeconds);
                // Stay in this loop while Connected OR Reconnecting.
                // WithAutomaticReconnect handles reconnect; we only exit when hub gives up (Disconnected).
                while (!stoppingToken.IsCancellationRequested &&
                       hub.State != HubConnectionState.Disconnected)
                {
                    await SafeDelay((int)heartbeatInterval.TotalSeconds, stoppingToken);
                }

                _hub = null;
                // Hub is already Disconnected — StopAsync returns immediately
                using (var stopCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    await hub.StopAsync(stopCts.Token).ConfigureAwait(false);
                await hub.DisposeAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _state.IsHubConnected    = false;
                _state.HubConnectionState = "Error";
                _state.PushEvent($"[Hub] Error: {ex.GetType().Name}: {ex.Message}");
                _state.NotifyChanged();
                _log.LogWarning(ex, "[Hub] Connection error, retry in 30s");
                await SafeDelay(30, stoppingToken);
            }
        }

        _state.IsHubConnected    = false;
        _state.HubConnectionState = "Stopped";
        _state.NotifyChanged();
    }

    private static async Task SafeDelay(int seconds, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); }
        catch (OperationCanceledException) { }
    }
}
