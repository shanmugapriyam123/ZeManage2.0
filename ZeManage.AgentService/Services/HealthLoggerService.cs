using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZeManage.AgentService.Services;

/// <summary>Logs a periodic health-status line — uptime, last sync result, unsynced queue depth,
/// backend reachability. This is the locally-visible half of "Agent heartbeat/health status"; the
/// server-visible half is SyncService's periodic identity re-POST.</summary>
public sealed class HealthLoggerService : BackgroundService
{
    private readonly AgentHealthState _health;
    private readonly ILogger<HealthLoggerService> _log;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public HealthLoggerService(AgentHealthState health, ILogger<HealthLoggerService> log)
    {
        _health = health;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _log.LogInformation(
                "Health check: uptime={Uptime} reachable={Reachable} lastSync={LastSync} lastSyncStatus={Status} unsynced={Unsynced}",
                _health.Uptime, _health.IsBackendReachable, _health.LastSyncAtUtc, _health.LastSyncStatus, _health.UnsyncedCount);

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
