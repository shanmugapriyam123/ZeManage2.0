using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.AgentService.Data;
using ZeManage.AgentService.Interop;
using ZeManage.AgentService.Models;
using ZeManage.AgentService.Services;

namespace ZeManage.AgentService.Monitors;

/// <summary>Tracks running/foreground processes and writes ApplicationUsage + ActivityInterval
/// rows to SQLite. Track-only — this monitor never calls the API; SyncService picks up whatever
/// it writes (Synced=false) on its own schedule. This is a deliberate simplification versus the
/// reference Agent's ProcessMonitor, which POSTs immediately over SignalR/HTTP and only falls back
/// to the periodic sync on failure — here there is exactly one sync path, matching the requested
/// "Data Tracking -> Local SQLite DB -> Sync Service -> API" flow literally.</summary>
public sealed class ProcessMonitor : BackgroundService
{
    private readonly LocalStore _store;
    private readonly AgentServiceOptions _opts;
    private readonly AgentSettings _settings;
    private readonly ILogger<ProcessMonitor> _log;

    private readonly Dictionary<string, Tracking> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private OpenInterval? _openInterval;

    private sealed class Tracking
    {
        public required string ProcessName;
        public required string DisplayName;
        public required string ApplicationType;
        public required string ApplicationId;
        public required string LocalId;
        public DateTime StartedUtc;
        public long ActiveSeconds;
        public long FocusSeconds;
        public long IdleSeconds;
    }

    private sealed class OpenInterval
    {
        public required string LocalId;
        public required string Activity;
        public string? ProcessName;
        public string? DisplayName;
        public string? ApplicationId;
        public DateTime StartedUtc;
        public long ActiveSeconds;
        public long FocusSeconds;
        public long IdleSeconds;
    }

    public ProcessMonitor(LocalStore store, IOptions<AgentServiceOptions> opts, AgentSettings settings, ILogger<ProcessMonitor> log)
    {
        _store = store;
        _opts = opts.Value;
        _settings = settings;
        _log = log;
    }

    private static string MakeApplicationId(string processName) =>
        new Guid(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(processName.ToLowerInvariant()))).ToString();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scanInterval = TimeSpan.FromSeconds(_opts.ProcessScanIntervalSeconds);
        var tickInterval = TimeSpan.FromSeconds(_opts.ActivityTickSeconds);

        try
        {
            var existing = await _store.GetRunningApplicationsAsync(stoppingToken);
            lock (_lock)
            {
                foreach (var app in existing.Where(a => !_running.ContainsKey(a.ProcessName)))
                {
                    _running[app.ProcessName] = new Tracking
                    {
                        ProcessName = app.ProcessName,
                        DisplayName = app.ApplicationName,
                        ApplicationType = app.ApplicationType,
                        ApplicationId = app.ApplicationId,
                        LocalId = app.LocalId,
                        StartedUtc = app.StartTime,
                        ActiveSeconds = app.ActiveSeconds,
                        FocusSeconds = app.FocusSeconds,
                        IdleSeconds = app.IdleSeconds
                    };
                }
            }
            _log.LogInformation("Restored {Count} running app sessions from DB", existing.Count);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not restore running sessions from DB"); }

        try
        {
            foreach (var iv in await _store.GetOpenActivityIntervalsAsync(stoppingToken))
                await _store.CloseActivityIntervalAsync(iv.LocalId, iv.UpdatedAt, iv.ActiveSeconds, iv.FocusSeconds, iv.IdleSeconds, DateTime.UtcNow, stoppingToken);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not close dangling activity intervals"); }

        var tickTask = RunActivityTickAsync(tickInterval, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { ScanAndUpdate(); }
            catch (Exception ex) { _log.LogWarning(ex, "Process scan failed"); }
            try { await Task.Delay(scanInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        await tickTask;

        List<Tracking> toClose;
        lock (_lock) toClose = _running.Values.ToList();
        foreach (var t in toClose)
            await CloseSessionAsync(t, CancellationToken.None);

        if (_openInterval is { } open)
            await CloseOpenIntervalAsync(open, DateTime.UtcNow);
    }

    private async Task RunActivityTickAsync(TimeSpan interval, CancellationToken ct)
    {
        var activeThreshold = TimeSpan.FromSeconds(_opts.ActiveThresholdSeconds);
        var lastTick = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }

            try
            {
                // Re-read every tick so a server-side idle-threshold change (via SyncService's
                // group-worktime-setup fetch) takes effect immediately, not just on restart.
                var idleThreshold = _settings.IdleThresholdMinutes is { } mins
                    ? TimeSpan.FromMinutes(mins)
                    : TimeSpan.FromSeconds(_opts.IdleThresholdSeconds);
                var idle = Win32Idle.GetIdleDuration();
                var foreground = Win32Window.GetForegroundProcessName();
                var now = DateTime.UtcNow;
                var elapsed = (long)(now - lastTick).TotalSeconds;
                lastTick = now;
                if (elapsed <= 0) continue;

                if (foreground is not null)
                {
                    lock (_lock)
                    {
                        if (_running.TryGetValue(foreground, out var tracked))
                        {
                            if (idle < activeThreshold) tracked.ActiveSeconds += elapsed;
                            else if (idle < idleThreshold) tracked.FocusSeconds += elapsed;
                            else tracked.IdleSeconds += elapsed;
                        }
                    }
                }

                bool engaged = idle < idleThreshold;
                string twoState = engaged ? "Active" : "Idle";
                OpenInterval? toClose = null;
                OpenInterval? justOpened = null;

                lock (_lock)
                {
                    bool stateChanged = _openInterval is null || _openInterval.Activity != twoState;
                    bool appChanged = engaged && foreground is not null && _openInterval is not null &&
                        !foreground.Equals(_openInterval.ProcessName, StringComparison.OrdinalIgnoreCase);

                    if (stateChanged || appChanged)
                    {
                        toClose = _openInterval;
                        _openInterval = null;

                        if (engaged && foreground is not null)
                        {
                            _running.TryGetValue(foreground, out var t);
                            var known = TrackedApplications.ResolveOrGeneric(foreground, true);
                            if (t is not null || known is not null)
                            {
                                _openInterval = new OpenInterval
                                {
                                    LocalId = Guid.NewGuid().ToString(),
                                    Activity = "Active",
                                    ProcessName = foreground,
                                    ApplicationId = t?.ApplicationId ?? MakeApplicationId(foreground),
                                    DisplayName = t?.DisplayName ?? known?.DisplayName ?? foreground,
                                    StartedUtc = now
                                };
                            }
                        }
                        else if (!engaged)
                        {
                            _openInterval = new OpenInterval { LocalId = Guid.NewGuid().ToString(), Activity = "Idle", StartedUtc = now };
                        }

                        if (_openInterval is not null) justOpened = _openInterval;
                    }

                    if (_openInterval is not null)
                    {
                        if (idle < activeThreshold) _openInterval.ActiveSeconds += elapsed;
                        else if (idle < idleThreshold) _openInterval.FocusSeconds += elapsed;
                        else _openInterval.IdleSeconds += elapsed;
                    }
                }

                if (toClose is not null) _ = CloseOpenIntervalAsync(toClose, now);
                if (justOpened is not null)
                    _ = _store.AddActivityIntervalAsync(new ActivityInterval
                    {
                        LocalId = justOpened.LocalId,
                        Activity = justOpened.Activity,
                        ApplicationId = justOpened.ApplicationId ?? "",
                        ApplicationName = justOpened.DisplayName,
                        ProcessName = justOpened.ProcessName,
                        StartTime = justOpened.StartedUtc,
                        EndTime = null,
                        CreatedAt = now,
                        UpdatedAt = now,
                        Synced = false
                    }, CancellationToken.None);
            }
            catch (Exception ex) { _log.LogDebug(ex, "Activity tick failed"); }
        }
    }

    private void ScanAndUpdate()
    {
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newBatch = new List<ApplicationUsage>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                bool hasWindow = p.MainWindowHandle != IntPtr.Zero;
                var def = TrackedApplications.ResolveOrGeneric(p.ProcessName, hasWindow);
                if (def is null) continue;
                if (def.Category == "Collab" && !hasWindow) continue;

                var key = p.ProcessName;
                current.Add(key);

                Tracking? existing;
                lock (_lock) { _running.TryGetValue(key, out existing); }

                bool rolledOver = existing is not null && existing.StartedUtc.ToLocalTime().Date < DateTime.Now.Date;
                if (rolledOver)
                    _ = CloseSessionAsync(existing!, CancellationToken.None);

                if (existing is null || rolledOver)
                {
                    string? version = null;
                    try { version = p.MainModule?.FileVersionInfo.FileVersion; } catch { }

                    var now = DateTime.UtcNow;
                    var localId = Guid.NewGuid().ToString();
                    var appId = MakeApplicationId(p.ProcessName);
                    var appType = def.Category == "Browser" ? "Browser" : "Software";

                    var usage = new ApplicationUsage
                    {
                        LocalId = localId,
                        ApplicationId = appId,
                        ApplicationName = def.DisplayName,
                        ProcessName = p.ProcessName,
                        Version = version,
                        ApplicationType = appType,
                        Status = "Running",
                        StartTime = now,
                        CreatedAt = now,
                        UpdatedAt = now,
                        Synced = false
                    };

                    var t = new Tracking
                    {
                        ProcessName = p.ProcessName,
                        DisplayName = def.DisplayName,
                        ApplicationType = appType,
                        ApplicationId = appId,
                        LocalId = localId,
                        StartedUtc = now
                    };
                    lock (_lock) { _running[key] = t; }
                    newBatch.Add(usage);
                }
            }
            catch { }
            finally { p.Dispose(); }
        }

        if (newBatch.Count > 0)
            _ = InsertBatchAsync(newBatch);

        List<Tracking>? ended = null;
        lock (_lock)
        {
            foreach (var key in _running.Keys.ToList())
            {
                if (!current.Contains(key))
                {
                    ended ??= new();
                    ended.Add(_running[key]);
                    _running.Remove(key);
                }
            }
        }
        if (ended is not null)
            foreach (var t in ended)
                _ = CloseSessionAsync(t, CancellationToken.None);
    }

    private async Task InsertBatchAsync(List<ApplicationUsage> batch)
    {
        foreach (var u in batch)
        {
            try { await _store.AddApplicationUsageAsync(u, CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to insert application usage {App}", u.ApplicationName); }
        }
    }

    private async Task CloseOpenIntervalAsync(OpenInterval iv, DateTime end)
    {
        try { await _store.CloseActivityIntervalAsync(iv.LocalId, end, iv.ActiveSeconds, iv.FocusSeconds, iv.IdleSeconds, end, CancellationToken.None); }
        catch (Exception ex) { _log.LogDebug(ex, "Failed to close activity interval"); }
    }

    private async Task CloseSessionAsync(Tracking t, CancellationToken ct)
    {
        var end = DateTime.UtcNow;
        var usage = new ApplicationUsage
        {
            LocalId = t.LocalId,
            ApplicationId = t.ApplicationId,
            ApplicationName = t.DisplayName,
            ProcessName = t.ProcessName,
            ApplicationType = t.ApplicationType,
            Status = "Closed",
            StartTime = t.StartedUtc,
            EndTime = end,
            ActiveSeconds = t.ActiveSeconds,
            FocusSeconds = t.FocusSeconds,
            IdleSeconds = t.IdleSeconds,
            UpdatedAt = end
        };
        try { await _store.CloseApplicationUsageAsync(usage, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to close application usage {App}", t.DisplayName); }
    }
}
