using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.Agent.Core.Services;

namespace ZeManage.Agent.Core.Sync;

public sealed class AgentHubConnection : BackgroundService
{
    private readonly IdentityService _identity;
    private readonly AgentState _state;
    private readonly AgentOptions _opts;
    private readonly TokenProvider _tokens;
    private readonly ILogger<AgentHubConnection> _log;

    public AgentHubConnection(
        IdentityService identity,
        AgentState state,
        IOptions<AgentOptions> opts,
        TokenProvider tokens,
        ILogger<AgentHubConnection> log)
    {
        _identity = identity;
        _state    = state;
        _opts     = opts.Value;
        _tokens   = tokens;
        _log      = log;
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

                var hubUrl = $"{_opts.BackendBaseUrl.TrimEnd('/')}/hubs/telemetry";

                var hub = new HubConnectionBuilder()
                    .WithUrl(hubUrl, o =>
                    {
                        o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                        if (_opts.AllowInsecureSsl)
                        {
                            o.HttpMessageHandlerFactory = _ => new HttpClientHandler
                            {
                                ServerCertificateCustomValidationCallback =
                                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                            };
                        }
                    })
                    .WithAutomaticReconnect(new[]
                    {
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(15),
                        TimeSpan.FromSeconds(30),
                        TimeSpan.FromSeconds(60)
                    })
                    .Build();

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

                hub.Reconnecting += _ =>
                {
                    _state.IsHubConnected    = false;
                    _state.HubConnectionState = "Reconnecting";
                    _state.NotifyChanged();
                    return Task.CompletedTask;
                };

                hub.Reconnected += connId =>
                {
                    _state.IsHubConnected    = true;
                    _state.HubConnectionState = "Connected";
                    _state.NotifyChanged();
                    _ = hub.InvokeAsync("RegisterAgent", id.ZeUserId?.ToString() ?? id.MachineId, stoppingToken);
                    return Task.CompletedTask;
                };

                hub.Closed += _ =>
                {
                    _state.IsHubConnected    = false;
                    _state.HubConnectionState = "Disconnected";
                    _state.NotifyChanged();
                    return Task.CompletedTask;
                };

                await hub.StartAsync(stoppingToken);
                await hub.InvokeAsync("RegisterAgent", id.ZeUserId?.ToString() ?? id.MachineId, stoppingToken);

                _state.IsHubConnected    = true;
                _state.HubConnectionState = "Connected";
                _state.NotifyChanged();
                _state.PushEvent("[Hub] Connected to backend");
                _log.LogInformation("[Hub] Connected to {Url}", hubUrl);

                var heartbeatInterval = TimeSpan.FromSeconds(_opts.HeartbeatIntervalSeconds);
                while (!stoppingToken.IsCancellationRequested &&
                       hub.State == HubConnectionState.Connected)
                {
                    try
                    {
                        await hub.InvokeAsync("AgentHeartbeat", new
                        {
                            AgentId     = id.ZeUserId?.ToString() ?? id.MachineId,
                            MachineName = id.MachineName,
                            UserName    = id.UserName,
                            WindowsSid  = id.WindowsSid,
                            Status      = "Active",
                            Timestamp   = DateTime.UtcNow
                        }, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "[Hub] Heartbeat failed");
                    }

                    await SafeDelay((int)heartbeatInterval.TotalSeconds, stoppingToken);
                }

                await hub.StopAsync(CancellationToken.None);
                await hub.DisposeAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _state.IsHubConnected    = false;
                _state.HubConnectionState = "Error";
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
