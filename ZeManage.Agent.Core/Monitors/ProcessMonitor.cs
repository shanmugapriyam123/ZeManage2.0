using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Models;
using ZeManage.Agent.Core.Services;

namespace ZeManage.Agent.Core.Monitors;

public sealed class ProcessMonitor : BackgroundService
{
    private readonly LocalStore _store;
    private readonly IdentityService _identity;
    private readonly AgentOptions _opts;
    private readonly ILogger<ProcessMonitor> _log;
    private readonly AgentState _state;

    private readonly Dictionary<(string proc, int pid), Tracking> _running = new();

    private sealed class Tracking
    {
        public required string ProcessName;
        public required string DisplayName;
        public required string Category;
        public required string? Version;
        public DateTime StartedUtc;
    }

    public ProcessMonitor(
        LocalStore store,
        IdentityService identity,
        IOptions<AgentOptions> opts,
        ILogger<ProcessMonitor> log,
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
        var interval = TimeSpan.FromSeconds(_opts.ProcessScanIntervalSeconds);
        var id = _identity.Get();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ScanAndUpdate(id);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Process scan failed");
            }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        foreach (var ((proc, pid), t) in _running.ToList())
            await CloseSessionAsync(t, id, CancellationToken.None);
    }

    private void ScanAndUpdate(AgentIdentity id)
    {
        var current = new HashSet<(string, int)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                bool hasWindow = p.MainWindowHandle != IntPtr.Zero;
                var def = TrackedApplications.ResolveOrGeneric(p.ProcessName, hasWindow);
                if (def is null) continue;
                var key = (p.ProcessName, p.Id);
                current.Add(key);
                if (!_running.ContainsKey(key))
                {
                    string? version = null;
                    try { version = p.MainModule?.FileVersionInfo.FileVersion; } catch { }
                    var t = new Tracking
                    {
                        ProcessName = p.ProcessName,
                        DisplayName = def.DisplayName,
                        Category = def.Category,
                        Version = version,
                        StartedUtc = DateTime.UtcNow
                    };
                    _running[key] = t;
                    _state.PushEvent($"App start: {def.DisplayName}");

                    if (def.IsBim)
                    {
                        _ = _store.AddBimEventAsync(new BimEvent
                        {
                            UserName    = id.UserName,
                            MachineName = id.MachineName,
                            WindowsSid  = id.WindowsSid,
                            Application = def.DisplayName,
                            EventType   = BimEventType.SessionStart,
                            EventTime   = t.StartedUtc,
                            Details     = $"pid={p.Id} ver={version}"
                        });
                    }
                }
            }
            catch { }
            finally { p.Dispose(); }
        }

        foreach (var key in _running.Keys.ToList())
        {
            if (!current.Contains(key))
            {
                var t = _running[key];
                _running.Remove(key);
                _ = CloseSessionAsync(t, id, CancellationToken.None);
            }
        }
    }

    private async Task CloseSessionAsync(Tracking t, AgentIdentity id, CancellationToken ct)
    {
        var end = DateTime.UtcNow;
        var dur = (long)(end - t.StartedUtc).TotalSeconds;
        var usage = new ApplicationUsage
        {
            UserName        = id.UserName,
            MachineName     = id.MachineName,
            WindowsSid      = id.WindowsSid,
            ApplicationName = t.DisplayName,
            ProcessName     = t.ProcessName,
            Version         = t.Version,
            StartTime       = t.StartedUtc,
            EndTime         = end,
            DurationSeconds = dur,
            Category        = t.Category
        };
        await _store.AddApplicationUsageAsync(usage, ct);
        _state.PushEvent($"App stop: {t.DisplayName} ({dur}s)");

        var def = TrackedApplications.Resolve(t.ProcessName);
        if (def?.IsBim == true)
        {
            await _store.AddBimEventAsync(new BimEvent
            {
                UserName    = id.UserName,
                MachineName = id.MachineName,
                WindowsSid  = id.WindowsSid,
                Application = t.DisplayName,
                EventType   = BimEventType.SessionEnd,
                EventTime   = end,
                Details     = $"duration={dur}s"
            }, ct);
        }
    }
}
