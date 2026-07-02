using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Interop;
using ZeManage.Agent.Core.Models;
using ZeManage.Agent.Core.Services;

namespace ZeManage.Agent.Core.Monitors;

public sealed class AttendanceMonitor : BackgroundService
{
    private readonly LocalStore _store;
    private readonly IdentityService _identity;
    private readonly AgentOptions _opts;
    private readonly ILogger<AttendanceMonitor> _log;
    private readonly AgentState _state;

    public AttendanceMonitor(
        LocalStore store,
        IdentityService identity,
        IOptions<AgentOptions> opts,
        ILogger<AttendanceMonitor> log,
        AgentState state)
    {
        _store = store;
        _identity = identity;
        _opts = opts.Value;
        _log = log;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var id = _identity.Get();
        var session = new AttendanceSession
        {
            UserName = id.UserName,
            MachineName = id.MachineName,
            WindowsSid = id.WindowsSid,
            LoginTime = DateTime.UtcNow
        };
        await _store.AddAttendanceAsync(session, stoppingToken);
        _state.CurrentAttendance = session;
        _log.LogInformation("Attendance session opened for {User}@{Machine}", id.UserName, id.MachineName);

        try
        {
            var tick = TimeSpan.FromSeconds(_opts.ActivityTickSeconds);
            var idleThreshold = TimeSpan.FromSeconds(_opts.IdleThresholdSeconds);
            var lastTick = DateTime.UtcNow;
            while (!stoppingToken.IsCancellationRequested)
            {
                var idle = Win32Idle.GetIdleDuration();
                var now = DateTime.UtcNow;
                var elapsed = (long)(now - lastTick).TotalSeconds;
                lastTick = now;
                if (idle < idleThreshold)
                    session.ActiveSeconds += elapsed;
                else
                    session.IdleSeconds += elapsed;

                await _store.UpdateAttendanceAsync(session, stoppingToken);
                await Task.Delay(tick, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            session.LogoutTime = DateTime.UtcNow;
            await _store.UpdateAttendanceAsync(session, CancellationToken.None);
            _log.LogInformation("Attendance session closed. Active={Active}s Idle={Idle}s",
                session.ActiveSeconds, session.IdleSeconds);
        }
    }
}
