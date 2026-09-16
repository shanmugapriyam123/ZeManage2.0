using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Interop;
using ZeManage.Agent.Core.Models;
using ZeManage.Agent.Core.Services;
using ZeManage.Agent.Core.Sync;

namespace ZeManage.Agent.Core.Monitors;

public sealed class ProcessMonitor : BackgroundService
{
    private readonly LocalStore _store;
    private readonly IdentityService _identity;
    private readonly AgentOptions _opts;
    private readonly ILogger<ProcessMonitor> _log;
    private readonly AgentState _state;
    private readonly IHttpClientFactory _httpFactory;
    private readonly TokenProvider _tokens;
    private readonly AgentHubConnection _hub;

    // Key = processName only — one session per executable, not one per PID
    private readonly Dictionary<string, Tracking> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _registeredBrowserAppIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private string? _lastLiveActivityApp; // dedup — skip if same app still in foreground
    private DateTime _lastLiveSentUtc = DateTime.MinValue; // last successful live send — drives the 3-s periodic freshness ping for an unchanged foreground app
    // Last successfully-reported known app's identifiers — kept so the periodic freshness ping
    // (which doubles as the server's ONLY heartbeat signal, see ReportLiveActivityAsync) can keep
    // firing every ~3s even while `foreground` is momentarily null (desktop/lock screen, no
    // window focused) or unrecognized (an untracked process). Without this, a genuinely-online,
    // genuinely-idle user whose foreground happens to be null/unknown at read time stops sending
    // ANY live ping — and since that ping is the only thing refreshing LastHeartbeatTime, the
    // stale-timeout sweep eventually (wrongly) marks them Offline even though nothing about their
    // machine actually changed.
    private string? _lastKnownAppId;
    private string? _lastKnownProcessName;
    // Was 30s — dropped to 3s so LastHeartbeatTime (this ping is its only source, see
    // ReportLiveActivityAsync) stays fresh enough for the Agent screen's Idle/Inactive status to
    // feel as instant as the app-switch live update, even when SignalR briefly drops: this same
    // ping already falls back to HTTP (AgentHubConnection.TrySendEventAsync →
    // POST /agentdb/live-activity) whenever the hub isn't Connected, so the heartbeat keeps
    // flowing through the outage instead of only resuming once the hub reconnects.
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromSeconds(3);
    // Instant foreground-change detection (Insightful-style): the OS-level
    // SetWinEventHook fires this semaphore the moment focus changes, waking
    // RunActivityTickAsync immediately instead of letting it sleep out the
    // remainder of its poll interval. Max count 1 → rapid bursts coalesce;
    // the woken tick reads the CURRENT foreground so the final app wins.
    private readonly SemaphoreSlim _fgChangedSignal = new(0, 1);
    private ForegroundWatcher? _fgWatcher;
    private string _lastHubState = ""; // tracks the previous hub state so we can force a re-send whenever the hub transitions from any non-Connected state (Reconnecting/Disconnected) back to Connected — otherwise LiveStatusChanged events fired during the disconnect window are lost and the portal's Task column stays stuck on the pre-drop app until the user manually switches to a different app.

    // The single currently-accumulating focus interval (only one window can be OS-foreground at
    // a time) — guarded by _lock, same as _running. Independent of _running/Tracking: this tracks
    // WHEN an app was actually focused (for the Activities Timeline), not when its process was
    // open. See RunActivityTickAsync for how the two are kept in lockstep without either reading
    // or modifying the other's fields.
    private OpenInterval? _openInterval;

    private sealed class Tracking
    {
        public required string  ProcessName;
        public required string  DisplayName;
        public required string  ApplicationType;
        public required string? Version;
        public required string  ApplicationId;  // stable per software (hash of processName)
        public required string  LocalId;        // local PK in SQLite — never changes
        public string?          SessionId;      // null until server POST returns it
        public DateTime StartedUtc;
        public long ActiveSeconds;
        public long FocusSeconds;
        public long IdleSeconds;
    }

    private sealed class OpenInterval
    {
        public required string  LocalId;                  // local PK — never changes
        public required string  Activity;                 // "Active" or "Idle" — drives segment boundaries
        public string?          ProcessName;               // null for Idle segments — no app is meaningfully "current" while idle
        public string?          DisplayName;
        public string?          ApplicationId;             // stable per software (hash of processName); null for Idle
        public string?          ApplicationUsageLocalId;    // parent ApplicationUsage.LocalId, if any (null for browsers/Idle)
        public DateTime StartedUtc;
        public long ActiveSeconds;
        public long FocusSeconds;
        public long IdleSeconds;
    }

    // Case-insensitive scan for any known session-ID field in a JSON response item.
    private static string? ExtractSessionId(JsonElement item)
    {
        foreach (var prop in item.EnumerateObject())
        {
            if (prop.Name.Equals("applicationSessionId",   StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("application_session_id", StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("sessionId",              StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("session_id",             StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("sessionGuid",            StringComparison.OrdinalIgnoreCase) ||
                prop.Name.Equals("id",                     StringComparison.OrdinalIgnoreCase))
            {
                var val = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() : null;
                if (val != null) return val;
            }
        }
        return null;
    }

    // Deterministic ID for a software — same processName always gives same applicationId
    private static string MakeApplicationId(string processName) =>
        new Guid(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(processName.ToLowerInvariant()))).ToString();

    // Extracts the exe icon and returns it as a Base64 PNG string
    private static string? CaptureIconBase64(string? exePath, string processName, ILogger log)
    {
        if (exePath is null) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon is null)
            {
                log.LogDebug("Icon: ExtractAssociatedIcon returned null for {App} ({Path})", processName, exePath);
                return null;
            }
            using var bmp  = icon.ToBitmap();
            using var ms   = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex)
        {
            log.LogDebug("Icon: extraction failed for {App} ({Path}) — {Reason}", processName, exePath, ex.Message);
            return null;
        }
    }

    private static string ResolveApplicationType(string category) => category switch
    {
        "Browser" => "Browser",
        "Collab"  => "Web",
        _         => "Software"
    };

    public ProcessMonitor(
        LocalStore store,
        IdentityService identity,
        IOptions<AgentOptions> opts,
        ILogger<ProcessMonitor> log,
        AgentState state,
        IHttpClientFactory httpFactory,
        TokenProvider tokens,
        AgentHubConnection hub)
    {
        _store       = store;
        _identity    = identity;
        _opts        = opts.Value;
        _log         = log;
        _state       = state;
        _httpFactory = httpFactory;
        _tokens      = tokens;
        _hub         = hub;

        // Instant re-broadcast on hub reconnect — hooks the event exposed by
        // AgentHubConnection. Clearing _lastLiveActivityApp makes the very next
        // scan re-send the current foreground app, so a server restart (or any
        // transient hub drop) closes the portal's "stuck Task column" gap in
        // ~1s instead of waiting the full scan interval.
        _hub.HubConnectedOrReconnected += OnHubConnectedOrReconnected;
    }

    private void OnHubConnectedOrReconnected()
    {
        if (_lastLiveActivityApp != null)
            _log.LogInformation("[Live] Hub reconnected — clearing dedup marker to force resend (was {App})", _lastLiveActivityApp);
        _lastLiveActivityApp = null;
    }

    // Workstation locked (or an unlock/other switch we don't care about) — only Lock reports.
    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
            _ = ReportGoingOfflineAsync("Locked");
    }

    // System entering sleep/hibernate. Best-effort — Windows gives very little time to react
    // before actually suspending, so the heartbeat-timeout sweep remains the guaranteed fallback.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            _ = ReportGoingOfflineAsync("Sleep");
    }

    // Logoff or shutdown/restart — fires before the session actually ends, so there's a real
    // (if short) window to get this out. Never cancels the session (e.Cancel left false).
    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        var reason = e.Reason == SessionEndReasons.SystemShutdown ? "Shutdown" : "Logoff";
        _ = ReportGoingOfflineAsync(reason);
    }

    private async Task ReportGoingOfflineAsync(string reason)
    {
        try
        {
            var sent = await _hub.TrySendEventAsync("ReportAgentOffline", new { reason }, CancellationToken.None);
            _log.LogInformation("[Offline] Reported going-offline signal ({Reason}) — sent={Sent}", reason, sent);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Offline] Failed to report going-offline signal ({Reason})", reason);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scanInterval     = TimeSpan.FromSeconds(_opts.ProcessScanIntervalSeconds);
        var activityInterval = TimeSpan.FromSeconds(_opts.ActivityTickSeconds);
        var id               = _identity.Get();

        // Restore previously-Running rows from DB — prevents duplicates on restart
        try
        {
            var existing = await _store.GetRunningApplicationsAsync(stoppingToken);
            lock (_lock)
            {
                foreach (var app in existing)
                {
                    if (!_running.ContainsKey(app.ProcessName))
                    {
                        _running[app.ProcessName] = new Tracking
                        {
                            ProcessName     = app.ProcessName,
                            DisplayName     = app.ApplicationName,
                            ApplicationType = app.ApplicationType,
                            Version         = app.Version,
                            ApplicationId   = app.ApplicationId,
                            LocalId         = app.LocalId,
                            SessionId       = app.SessionId,
                            StartedUtc      = app.StartTime,
                            ActiveSeconds   = app.ActiveSeconds,
                            FocusSeconds    = app.FocusSeconds,
                            IdleSeconds     = app.IdleSeconds
                        };
                    }
                }
            }
            _log.LogInformation("Restored {Count} running app sessions from DB", existing.Count);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not restore running sessions from DB"); }

        // Any focus interval left with EndTime=null belongs to a previous run that didn't shut
        // down gracefully (a graceful stop closes it explicitly — see this method's tail). We
        // can't know what was actually focused during the downtime, so close it using its own
        // last-known flush time (UpdatedAt) — NOT "now" — so a machine that sat off for hours
        // doesn't get a fake multi-hour interval on the timeline.
        try
        {
            var dangling = await _store.GetOpenActivityIntervalsAsync(stoppingToken);
            foreach (var iv in dangling)
            {
                var bestKnownEnd = iv.UpdatedAt > iv.StartTime ? iv.UpdatedAt : iv.StartTime;
                await _store.CloseActivityIntervalAsync(
                    iv.LocalId, bestKnownEnd, iv.ActiveSeconds, iv.FocusSeconds, iv.IdleSeconds,
                    DateTime.UtcNow, stoppingToken);
            }
            if (dangling.Count > 0)
                _log.LogInformation("Closed {Count} dangling activity interval(s) from a previous run", dangling.Count);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not close dangling activity intervals"); }

        // OS-level instant foreground-change hook — wakes the activity tick the
        // moment focus changes so the live send fires in ~100 ms instead of up
        // to a full poll interval later. See ForegroundWatcher for details.
        _fgWatcher = new ForegroundWatcher(() =>
        {
            try { _fgChangedSignal.Release(); }
            catch (SemaphoreFullException) { /* burst coalescing — a wake is already pending */ }
        });

        // Proactive "going offline" signal — reports a lock/sleep/shutdown/logoff the instant
        // Windows raises it, instead of leaving the portal to notice only via
        // StaleUserHeartbeatDeactivationService's periodic heartbeat-timeout sweep (still the
        // fallback for ungraceful terminations — crash, power loss — which raise none of these).
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;

        var signalRInterval  = TimeSpan.FromSeconds(_opts.HeartbeatIntervalSeconds);
        var activityTask     = RunActivityTickAsync(activityInterval, stoppingToken);
        var signalRTickTask  = RunSignalRTickAsync(signalRInterval, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_state.IsCaptureEnabled)
            {
                try { ScanAndUpdate(id); }
                catch (Exception ex) { _log.LogWarning(ex, "Process scan failed"); }
            }
            try { await Task.Delay(scanInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _fgWatcher?.Dispose();
        await Task.WhenAll(activityTask, signalRTickTask);

        List<Tracking> toClose;
        lock (_lock)
            toClose = _running.Values.ToList();

        foreach (var t in toClose)
            await CloseSessionAsync(t, id, CancellationToken.None);

        if (_openInterval is not null)
            await CloseOpenIntervalAsync(_openInterval, DateTime.UtcNow);
    }

    private async Task RunActivityTickAsync(TimeSpan interval, CancellationToken ct)
    {
        var activeThreshold = TimeSpan.FromSeconds(_opts.ActiveThresholdSeconds);
        var lastTick        = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            if (!_state.IsCaptureEnabled)
            {
                lastTick = DateTime.UtcNow; // avoid a huge elapsed-seconds jump once capture resumes
                try { await Task.Delay(interval, ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                // Re-read each tick so server-updated value takes effect immediately
                var idleThreshold  = TimeSpan.FromMinutes(_state.IdleThresholdMinutes);
                var idle           = Win32Idle.GetIdleDuration();
                var foreground     = Win32Window.GetForegroundProcessName();
                var now            = DateTime.UtcNow;
                var elapsed        = (long)(now - lastTick).TotalSeconds;
                lastTick           = now;

                // Three mutually-exclusive buckets, matching Active/Focused/Idle exactly:
                //   Active — idle < activeThreshold        (currently interacting)
                //   Focus  — activeThreshold <= idle < idleThreshold (in front, no recent input yet)
                //   Idle   — idle >= idleThreshold          (no interaction for the full threshold)
                if (foreground is not null && elapsed > 0)
                {
                    lock (_lock)
                    {
                        if (_running.TryGetValue(foreground, out var tracked))
                        {
                            if (idle < activeThreshold)
                                tracked.ActiveSeconds += elapsed;
                            else if (idle < idleThreshold)
                                tracked.FocusSeconds += elapsed;
                            else
                                tracked.IdleSeconds += elapsed;
                        }
                    }
                }

                // ---- Activity Timeline segment tracking (Activities Timeline feature) ----
                // Independent bookkeeping for WHEN foreground focus AND two-state
                // (Active-vs-Idle) engagement actually changed — a byproduct of the SAME
                // idle/active-threshold decision above, not an independent recomputation (both
                // this and `tracked` above are fed by the same elapsed/idle values, computed once
                // per tick, so the two can never drift apart). Additive only: never reads or
                // writes `tracked`/`_running`, and never touches the hub. A new segment starts on
                // EITHER an app switch (while engaged) OR an Active<->Idle transition (Active here
                // covers both the Active and Focus buckets — the timeline only renders two
                // colors), matching a real employee-monitoring timeline: Chrome 09:00-09:15
                // Active, 09:15-09:20 Idle, Chrome-again 09:20-09:45 Active are three rows, not one.
                OpenInterval? intervalToClose = null;
                OpenInterval? intervalJustOpened = null;

                if (elapsed > 0)
                {
                    bool engaged = idle < idleThreshold; // combines the Active + Focus buckets
                    // Idle time inside a configured Fixed-window break (Break Time Setup) is
                    // reported as "Break" instead of "Idle" so it isn't counted against the
                    // employee — computed once per tick and reused below so the segment-open
                    // branch can't observe a different answer than the state-change check that
                    // decided to open it.
                    bool inBreakWindow = !engaged && _state.IsInActiveBreakWindow(now);
                    string twoState = engaged ? "Active" : (inBreakWindow ? "Break" : "Idle");

                    lock (_lock)
                    {
                        // Deliberately NOT reusing _lastLiveActivityApp — it is nulled out on hub
                        // reconnect purely to force a resend, not a trustworthy "did focus really
                        // change" signal for this purpose.
                        bool stateChanged = _openInterval is null || _openInterval.Activity != twoState;
                        bool appChangedWhileEngaged = engaged && foreground is not null && _openInterval is not null &&
                            !foreground.Equals(_openInterval.ProcessName, StringComparison.OrdinalIgnoreCase);
                        bool dayRolled = _openInterval is not null &&
                            _openInterval.StartedUtc.ToLocalTime().Date < now.ToLocalTime().Date;

                        if (stateChanged || appChangedWhileEngaged || dayRolled)
                        {
                            intervalToClose = _openInterval;
                            _openInterval = null;

                            if (engaged)
                            {
                                // Only track Active segments for apps we recognise — same
                                // "isKnown" bar ReportLiveActivity already applies further down,
                                // so the timeline never picks up noise from unrecognised
                                // background processes. Idle segments have no meaningful app.
                                if (foreground is not null)
                                {
                                    _running.TryGetValue(foreground, out var trackedForInterval);
                                    var knownDef = TrackedApplications.Map.GetValueOrDefault(foreground);
                                    bool isKnown = trackedForInterval is not null || knownDef is not null;

                                    if (isKnown)
                                    {
                                        _openInterval = new OpenInterval
                                        {
                                            LocalId                 = Guid.NewGuid().ToString(),
                                            Activity                = "Active",
                                            ProcessName             = foreground,
                                            ApplicationId           = trackedForInterval?.ApplicationId ?? MakeApplicationId(foreground),
                                            DisplayName             = trackedForInterval?.DisplayName   ?? knownDef?.DisplayName ?? foreground,
                                            ApplicationUsageLocalId = trackedForInterval?.LocalId,
                                            StartedUtc              = now
                                        };
                                    }
                                }
                            }
                            else
                            {
                                _openInterval = new OpenInterval
                                {
                                    LocalId     = Guid.NewGuid().ToString(),
                                    Activity    = inBreakWindow ? "Break" : "Idle",
                                    ProcessName = null,
                                    DisplayName = null,
                                    ApplicationId = null,
                                    StartedUtc  = now
                                };
                            }

                            if (_openInterval is not null)
                                intervalJustOpened = _openInterval;
                        }

                        if (_openInterval is not null)
                        {
                            if (idle < activeThreshold)      _openInterval.ActiveSeconds += elapsed;
                            else if (idle < idleThreshold)   _openInterval.FocusSeconds  += elapsed;
                            else                              _openInterval.IdleSeconds   += elapsed;
                        }
                    }
                }

                // DB writes happen OUTSIDE _lock, fire-and-forget — a slow/locked SQLite write can
                // never delay this tick's ReportLiveActivity send further below.
                if (intervalToClose is not null)
                    _ = CloseOpenIntervalAsync(intervalToClose, now);
                if (intervalJustOpened is not null)
                    _ = _store.AddActivityIntervalAsync(new ActivityInterval
                    {
                        LocalId = intervalJustOpened.LocalId,
                        ApplicationUsageLocalId = intervalJustOpened.ApplicationUsageLocalId,
                        Activity        = intervalJustOpened.Activity,
                        ApplicationId   = intervalJustOpened.ApplicationId ?? "",
                        ApplicationName = intervalJustOpened.DisplayName ?? "",
                        ProcessName     = intervalJustOpened.ProcessName ?? "",
                        StartTime = intervalJustOpened.StartedUtc,
                        EndTime   = null,
                        CreatedAt = now,
                        UpdatedAt = now,
                        Synced    = false
                    }, CancellationToken.None);

                // When the hub reconnects after a drop, clear the dedup marker so
                // the very next scan re-sends the CURRENT foreground app. Without
                // this, if the user switched apps while the hub was Reconnecting,
                // the send was skipped (TrySendEventAsync returns false), _lastLiveActivityApp
                // was never updated, and no further update fires until the user
                // manually switches to yet another app.
                var currentHubState = _state.HubConnectionState ?? "";
                if (_lastHubState != "Connected" && currentHubState == "Connected")
                {
                    if (_lastLiveActivityApp != null)
                        _log.LogInformation("[Live] Hub reconnected — re-sending current app on next scan (was {App})", _lastLiveActivityApp);
                    _lastLiveActivityApp = null;
                }
                _lastHubState = currentHubState;

                // ReportLiveActivity — fire when the foreground app CHANGES, and
                // ALSO as a periodic freshness ping every 3 s for the SAME app.
                // The change-only dedup meant that while the user stayed in one
                // app the server's Redis live cache aged and any freshly loaded
                // dashboard sat on a stale Postgres-derived value until the next
                // switch (potentially minutes). The 3-s refresh keeps the cache
                // and every open dashboard continuously current, so a page
                // refresh can never show old data for more than one ping cycle.
                bool appChanged  = foreground is not null &&
                    !foreground.Equals(_lastLiveActivityApp, StringComparison.OrdinalIgnoreCase);
                bool refreshDue  = foreground is not null && !appChanged &&
                    DateTime.UtcNow - _lastLiveSentUtc >= LiveRefreshInterval;

                if (appChanged || refreshDue)
                {
                    // Look up in _running first; browsers are excluded from _running so fall back
                    // to TrackedApplications for their display name / appId
                    Tracking? t;
                    lock (_lock) { _running.TryGetValue(foreground!, out t); }

                    string? liveAppId      = t?.ApplicationId ?? MakeApplicationId(foreground!);
                    string  liveProcess    = t?.ProcessName   ?? foreground!;
                    var     knownDef       = TrackedApplications.Map.GetValueOrDefault(foreground!);
                    string  liveDisplay    = t?.DisplayName   ?? knownDef?.DisplayName ?? foreground!;

                    // Only report apps we recognise (in _running OR in the known app map)
                    bool isKnown = t is not null || knownDef is not null;

                    _log.LogDebug("[Live] Foreground → {App} | known={Known} | hub={Hub} | running_count={Count}",
                        foreground, isKnown, _state.HubConnectionState, _running.Count);

                    if (isKnown)
                    {
                        var liveStatus = idle < activeThreshold ? "Active"
                                       : idle < idleThreshold   ? "Focus"
                                       : "Idle";

                        if (appChanged)
                            _log.LogInformation("[Live] App switch → {App} | status={Status} | hub={HubState}",
                                liveDisplay, liveStatus, _state.HubConnectionState);
                        else
                            _log.LogDebug("[Live] Periodic refresh → {App} | status={Status}", liveDisplay, liveStatus);

                        var sent = await _hub.TrySendEventAsync("ReportLiveActivity", new
                        {
                            appId       = liveAppId,
                            processName = liveProcess,
                            status      = liveStatus
                        }, ct);

                        if (sent)
                        {
                            _lastLiveActivityApp  = foreground;
                            _lastLiveSentUtc      = DateTime.UtcNow;
                            _lastKnownAppId       = liveAppId;
                            _lastKnownProcessName = liveProcess;
                            if (appChanged)
                                _log.LogInformation("[Live] ReportLiveActivity sent ✓ {App}", liveDisplay);
                        }
                        else if (appChanged)
                        {
                            _log.LogWarning("[Live] ReportLiveActivity NOT sent — hub={HubState}, app={App}",
                                _state.HubConnectionState, liveDisplay);
                        }
                    }
                }
                else if (_lastKnownAppId is not null &&
                         DateTime.UtcNow - _lastLiveSentUtc >= LiveRefreshInterval)
                {
                    // Foreground is currently null (desktop/lock screen, no window focused) or
                    // unrecognized — the branch above never fires in that case, so nothing would
                    // otherwise keep the ~30s ping (the server's only heartbeat signal) flowing.
                    // Re-send using the last KNOWN app's identifiers purely to keep the heartbeat
                    // alive; the Idle/Active/Focus status is still computed fresh from the current
                    // idle timer, so a genuinely idle-but-online machine correctly keeps reporting
                    // "Idle" (not "stuck" on whatever it was doing before) without needing a real
                    // foreground app to hang the ping off of.
                    var liveStatus = idle < activeThreshold ? "Active"
                                   : idle < idleThreshold   ? "Focus"
                                   : "Idle";

                    _log.LogDebug("[Live] Keepalive refresh (no current foreground) → status={Status}", liveStatus);

                    var sent = await _hub.TrySendEventAsync("ReportLiveActivity", new
                    {
                        appId       = _lastKnownAppId,
                        processName = _lastKnownProcessName,
                        status      = liveStatus
                    }, ct);

                    if (sent)
                        _lastLiveSentUtc = DateTime.UtcNow;
                }

            }
            catch { }

            // Wait for either the normal tick interval OR an instant wake from
            // the ForegroundWatcher hook (fires the moment focus changes).
            // WaitAsync(timeout) returning true = focus-change wake → the next
            // iteration reads the new foreground within ~100 ms of the switch;
            // false = ordinary interval tick. Either way the loop continues.
            try { await _fgChangedSignal.WaitAsync(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Called by SyncService after it writes a sessionId to the DB,
    // so the in-memory _running dict stays in sync without waiting for the next scan.
    public void UpdateInMemorySessionId(string localId, string sessionId)
    {
        lock (_lock)
        {
            var entry = _running.Values.FirstOrDefault(t => t.LocalId == localId);
            if (entry is not null)
            {
                entry.SessionId = sessionId;
                _log.LogDebug("In-memory sessionId updated: {App} → {SessionId}", entry.DisplayName, sessionId);
            }
        }
    }

    // Runs every HeartbeatIntervalSeconds.
    // For every running app whose sessionId is already known:
    //   1. Push live stats to server via SignalR
    //   2. Flush current stats to local DB (crash safety)
    // For apps with sessionId=null: peek at DB — SyncService may have populated it since last tick.
    private async Task RunSignalRTickAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }

            if (!_state.IsCaptureEnabled) continue;

            try
            {
                // Refresh in-memory sessionId for any apps that SyncService populated since last tick
                List<Tracking> nullSession;
                lock (_lock)
                    nullSession = _running.Values.Where(t => t.SessionId is null).ToList();

                foreach (var t in nullSession)
                {
                    var sid = await _store.GetSessionIdAsync(t.LocalId, ct);
                    if (sid is not null)
                        lock (_lock) { t.SessionId = sid; }
                }

                var now = DateTime.UtcNow;

                List<(string? sessionId, string localId, string displayName,
                      string processName, string applicationId, DateTime startedUtc,
                      long active, long focus, long idle)> allRunning;

                lock (_lock)
                {
                    allRunning = _running.Values
                        .Select(t => (t.SessionId, t.LocalId, t.DisplayName,
                                      t.ProcessName, t.ApplicationId, t.StartedUtc,
                                      t.ActiveSeconds, t.FocusSeconds, t.IdleSeconds))
                        .ToList();
                }

                var today     = DateTime.Now.Date;
                var snapshots = allRunning
                    .Where(t => t.sessionId is not null)
                    .Where(t => t.startedUtc.ToLocalTime().Date >= today)
                    .ToList();
                var machineId = _identity.Get().MachineId;

                _log.LogDebug("[Heartbeat] Tick: running={Running} withSession={WithSession} hub={Hub}",
                    allRunning.Count, snapshots.Count, _state.HubConnectionState);

                if (snapshots.Count > 0)
                {
                    // Server expects an array of objects — send all running apps in one call
                    var payload = snapshots.Select(s => new
                    {
                        applicationSessionId = s.sessionId,
                        machineId            = machineId,
                        applicationId        = s.applicationId,
                        applicationName      = s.displayName,
                        processName          = s.processName,
                        capturedAt           = now,
                        sessionStartTime     = s.startedUtc,
                        activeSeconds        = s.active,
                        focusSeconds         = s.focus,
                        idleSeconds          = s.idle,
                        durationSeconds      = s.active + s.focus + s.idle,
                        status               = "Running",
                        isApplicationClosed  = false
                    }).ToArray();

                    // Chunk to ≤50 items per send. A single frame carrying every
                    // running app once blew past the server's SignalR
                    // MaximumReceiveMessageSize (32 KB at the time) — the server
                    // aborts the transport on oversized frames ("remote party
                    // closed the WebSocket without completing the close
                    // handshake"), which was a root cause of the recurring hub
                    // drops. The cap is now 256 KB server-side, but chunking
                    // keeps any future config regression from ever killing the
                    // connection again.
                    foreach (var chunk in payload.Chunk(50))
                    {
                        var sent = await _hub.TrySendEventAsync("ReportAgentActivity", chunk, ct);
                        if (sent)
                            _log.LogInformation("[Heartbeat] ReportAgentActivity sent ✓ ({Count} apps)", chunk.Length);
                        else
                            _log.LogWarning("[Heartbeat] ReportAgentActivity NOT sent — hub={Hub} ({Count} apps)",
                                _state.HubConnectionState, chunk.Length);
                    }
                }

                // Flush seconds for ALL running apps — not just those with sessionId.
                // Without this, apps never show accumulated time in DB when Redis is down
                // (no sessionId ever assigned), which makes every row look stuck at 0.
                foreach (var (_, localId, _, _, _, _, active, focus, idle) in allRunning)
                    await _store.UpdateRunningStatsAsync(localId, active, focus, idle, now, ct);

                // Durability flush for the currently-open activity interval, same crash-safety
                // guarantee UpdateRunningStatsAsync already gives ApplicationUsage — bounds
                // crash data-loss for the open interval to this same ~30s cadence. Runs after the
                // SignalR sends above so it can never add latency to them.
                OpenInterval? openSnapshot;
                lock (_lock) { openSnapshot = _openInterval; }
                if (openSnapshot is not null)
                    await _store.UpdateOpenActivityIntervalStatsAsync(
                        openSnapshot.LocalId, openSnapshot.ActiveSeconds,
                        openSnapshot.FocusSeconds, openSnapshot.IdleSeconds, now, ct);

            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "SignalR tick failed");
            }
        }
    }

    private void ScanAndUpdate(AgentIdentity id)
    {
        var current  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Collect (usage, tracking) pairs — DB insert + POST done together in order
        var newBatch = new List<(ApplicationUsage usage, Tracking t)>();
        // Browsers are excluded from _running/time-tracking (BrowserMonitor owns that
        // per-tab), but screenshots still reference a browser's appId hash — without a
        // matching server Application record + icon, the server has nothing to resolve
        // that AppId to and the icon never shows. Register once per browser, per session.
        var browserBatch = new List<(string appId, string displayName, string processName, string? iconB64)>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                bool hasWindow = p.MainWindowHandle != IntPtr.Zero;
                var def = TrackedApplications.ResolveOrGeneric(p.ProcessName, hasWindow);
                if (def is null) continue;

                var key = p.ProcessName;

                // Collab apps (Teams, Zoom, Slack) keep background processes alive after the
                // window is closed/minimized to tray. Only count them as "running" when at
                // least one instance has a visible main window — this triggers the close event
                // correctly when the user closes the window.
                if (def.Category == "Collab" && !hasWindow) continue;

                current.Add(key);

                if (def.Category == "Browser")
                {
                    var browserAppId = MakeApplicationId(p.ProcessName);
                    bool alreadyRegistered;
                    lock (_lock) { alreadyRegistered = _registeredBrowserAppIds.Contains(browserAppId); }
                    if (!alreadyRegistered)
                    {
                        string? exePath = null;
                        try { exePath = p.MainModule?.FileName; } catch { }
                        var iconB64 = CaptureIconBase64(exePath, p.ProcessName, _log);
                        lock (_lock) { _registeredBrowserAppIds.Add(browserAppId); }
                        browserBatch.Add((browserAppId, def.DisplayName, p.ProcessName, iconB64));
                    }
                    continue;
                }

                Tracking? existing;
                lock (_lock) { _running.TryGetValue(key, out existing); }

                // A session that's still running when local midnight passes gets split:
                // close it out as of the day it started, then start a fresh session for
                // today — same treatment as a real close+reopen, just triggered by the
                // calendar day changing instead of the process actually exiting.
                bool rolledOver = existing is not null &&
                    existing.StartedUtc.ToLocalTime().Date < DateTime.Now.Date;

                if (rolledOver)
                {
                    _log.LogInformation("Day rollover: {App} session from {Date} closed, starting new session for today",
                        def.DisplayName, existing!.StartedUtc.ToLocalTime().Date.ToShortDateString());
                    _ = CloseSessionAsync(existing!, id, CancellationToken.None);
                }

                bool isNew = existing is null || rolledOver;

                if (isNew)
                {
                    string? version = null;
                    string? exePath = null;
                    try { version = p.MainModule?.FileVersionInfo.FileVersion; } catch { }
                    try { exePath = p.MainModule?.FileName; }
                    catch (Exception ex) { _log.LogDebug("Icon: exePath unavailable for {App} — {Reason}", p.ProcessName, ex.Message); }

                    var now     = DateTime.UtcNow;
                    var localId = Guid.NewGuid().ToString();
                    var appId   = MakeApplicationId(p.ProcessName);
                    var iconB64 = CaptureIconBase64(exePath, p.ProcessName, _log);
                    if (iconB64 is null)
                        _log.LogDebug("Icon: no icon captured for {App} (exePath={ExePath})", p.ProcessName, exePath ?? "(null)");

                    var usage = new ApplicationUsage
                    {
                        LocalId         = localId,
                        SessionId       = null,
                        ApplicationId   = appId,
                        ApplicationName = def.DisplayName,
                        ProcessName     = p.ProcessName,
                        Version         = version,
                        ApplicationType = ResolveApplicationType(def.Category),
                        Status          = "Running",
                        IconBase64      = iconB64,
                        StartTime       = now,
                        EndTime         = null,
                        ActiveSeconds   = 0,
                        FocusSeconds    = 0,
                        IdleSeconds     = 0,
                        CreatedAt       = now,
                        UpdatedAt       = now,
                        Synced          = false
                    };

                    var t = new Tracking
                    {
                        ProcessName     = p.ProcessName,
                        DisplayName     = def.DisplayName,
                        ApplicationType = ResolveApplicationType(def.Category),
                        Version         = version,
                        ApplicationId   = appId,
                        LocalId         = localId,
                        StartedUtc      = now
                    };
                    lock (_lock) { _running[key] = t; }
                    newBatch.Add((usage, t));
                    _state.PushEvent($"App start: {def.DisplayName}");
                }
            }
            catch { }
            finally { p.Dispose(); }
        }

        // INSERT to local DB first, THEN batch POST — guarantees rows exist before UPDATE
        if (newBatch.Count > 0)
            _ = InsertThenPostBatchAsync(newBatch, CancellationToken.None);

        if (browserBatch.Count > 0)
            _ = RegisterBrowserIconsAsync(browserBatch, CancellationToken.None);

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
                _ = CloseSessionAsync(t, id, CancellationToken.None);
    }

    // Step 1: INSERT all rows to local DB (awaited)
    // Step 2: batch POST to server
    // Step 3: write returned sessionIds back to DB
    // Sequential order guarantees INSERT is committed before UPDATE runs.
    private async Task InsertThenPostBatchAsync(
        List<(ApplicationUsage usage, Tracking t)> batch, CancellationToken ct)
    {
        try
        {
            // INSERT all rows first — must complete before POST so UPDATE has rows to match
            foreach (var (usage, _) in batch)
                await _store.AddApplicationUsageAsync(usage, ct);

            var token = await _tokens.GetAsync(ct);
            if (token is null) return;

            var id     = _identity.Get();
            var client = _httpFactory.CreateClient("backend");
            client.BaseAddress = new Uri(_opts.BackendBaseUrl);
            client.Timeout     = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add("X-Agent-Id", id.MachineId);

            // Only POST apps that are still Running with no sessionId — skip if app closed before POST fires
            List<(ApplicationUsage usage, Tracking t)> toPost;
            lock (_lock)
                toPost = batch.Where(x => _running.ContainsKey(x.t.ProcessName) && x.t.SessionId is null).ToList();

            if (toPost.Count == 0) return;

            var payload = toPost.Select(x => new
            {
                appId           = x.t.ApplicationId,
                applicationName = x.t.DisplayName,
                executableName  = x.t.ProcessName,
                type            = x.t.ApplicationType,
                displayName     = x.t.DisplayName,
                version         = x.t.Version,
                iconUrl         = (string?)null,   // real icon goes via POST /applications/{applicationId}/icon (binary), not this field
                status          = "Running",
                createdAt       = x.t.StartedUtc,
                updatedAt       = x.t.StartedUtc,
                activeSeconds   = 0,
                focusSeconds    = 0,
                idleSeconds     = 0,
                crashCount      = 0
            }).ToArray();

            using var resp = await client.PostAsJsonAsync("/api/v1/agentdb/applications", payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Batch app open POST {Status}: {Body}", (int)resp.StatusCode, errBody);
                return;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            _log.LogWarning("Batch open RAW response: {Json}", json);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Normalize response into a flat list: handles root[], data[], data{}, root{}
            var items = new List<JsonElement>();
            if (root.TryGetProperty("data", out var d))
            {
                if (d.ValueKind == JsonValueKind.Array)
                    foreach (var el in d.EnumerateArray()) items.Add(el);
                else if (d.ValueKind == JsonValueKind.Object)
                    items.Add(d);
            }
            else if (root.ValueKind == JsonValueKind.Array)
                foreach (var el in root.EnumerateArray()) items.Add(el);
            else if (root.ValueKind == JsonValueKind.Object)
                items.Add(root);

            // Build lookup maps: localId / executableName / appId → sessionId + serverApplicationId
            var byLocalId      = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var byExeName      = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var byAppId        = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var appIdByLocal   = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var appIdByExeName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var appIdByAppId   = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var sid    = ExtractSessionId(item);
                var srvAid = item.TryGetProperty("applicationId", out var aidEl) && aidEl.ValueKind == JsonValueKind.String
                    ? aidEl.GetString() : null;

                if (item.TryGetProperty("localId", out var li) && li.GetString() is { } lid)
                {
                    byLocalId[lid]    = sid;
                    appIdByLocal[lid] = srvAid;
                }
                if (item.TryGetProperty("executableName", out var en) && en.GetString() is { } ename)
                {
                    byExeName[ename]      = sid;
                    appIdByExeName[ename] = srvAid;
                }
                if (item.TryGetProperty("appId", out var ai) && ai.GetString() is { } aid)
                {
                    byAppId[aid]      = sid;
                    appIdByAppId[aid] = srvAid;
                }
            }

            for (int i = 0; i < toPost.Count; i++)
            {
                var entry = toPost[i];
                string? sessionId          = null;
                string? serverApplicationId = null;

                // 1. Match by array position — the server always returns exactly one result per
                //    request item, in the same order (see ApplicationService.CreateApplicationsAsync,
                //    which builds its response with one ordered Add() per input item). This must be
                //    the primary match: a batch can contain several launches of the same executable
                //    (e.g. multiple Compil32/setup.exe instances), and the name/appId lookups below
                //    are Dictionaries keyed by those fields — they can only hold one entry per key,
                //    so they'd incorrectly collapse every same-named launch onto a single shared
                //    session if given priority over position.
                if (i < items.Count)
                {
                    sessionId = ExtractSessionId(items[i]);
                    if (items[i].TryGetProperty("applicationId", out var aidEl2) && aidEl2.ValueKind == JsonValueKind.String)
                        serverApplicationId = aidEl2.GetString();
                }
                // 2. Fallback matches — only reached if the response array is a different length
                //    than the request (shouldn't normally happen), so at least attempt a best-effort
                //    match instead of silently dropping the item.
                else if (byLocalId.TryGetValue(entry.t.LocalId, out var s1))
                {
                    sessionId = s1;
                    appIdByLocal.TryGetValue(entry.t.LocalId, out serverApplicationId);
                }
                else if (byExeName.TryGetValue(entry.t.ProcessName, out var s3))
                {
                    sessionId = s3;
                    appIdByExeName.TryGetValue(entry.t.ProcessName, out serverApplicationId);
                }
                else if (byAppId.TryGetValue(entry.t.ApplicationId, out var s2))
                {
                    sessionId = s2;
                    appIdByAppId.TryGetValue(entry.t.ApplicationId, out serverApplicationId);
                }

                if (sessionId is not null)
                {
                    entry.t.SessionId = sessionId;
                    await _store.UpdateSessionIdAsync(entry.t.LocalId, sessionId, ct);
                    _log.LogInformation("sessionId saved: {App} → {SessionId}", entry.t.DisplayName, sessionId);
                }
                else
                {
                    var rawItem = i < items.Count ? items[i].GetRawText() : "(no item at this index)";
                    _log.LogWarning("No sessionId for {App} — server returned: {Item}", entry.t.DisplayName, rawItem);
                }

                if (serverApplicationId is not null && entry.usage.IconBase64 is not null)
                    await UploadIconAsync(client, serverApplicationId, entry.usage.IconBase64, entry.t.DisplayName, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "InsertThenPostBatch failed");
        }
    }

    // Uploads the app's icon as binary via the dedicated endpoint — the main /applications
    // POST's iconUrl field is a plain string (no image handling), so the real icon bytes
    // must go here, keyed by the server's own applicationId (not our client-side appId hash).
    private async Task UploadIconAsync(HttpClient client, string serverApplicationId, string iconBase64, string appName, CancellationToken ct)
    {
        try
        {
            var bytes = Convert.FromBase64String(iconBase64);
            using var content = new MultipartFormDataContent();
            var byteContent = new ByteArrayContent(bytes);
            byteContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(byteContent, "Icon", "icon.png");

            using var resp = await client.PostAsync($"/api/v1/agentdb/applications/{serverApplicationId}/icon", content, ct);
            if (resp.IsSuccessStatusCode)
                _log.LogInformation("Icon uploaded: {App} → {AppId}", appName, serverApplicationId);
            else
                _log.LogWarning("Icon upload failed: {App} → {Status}", appName, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Icon upload failed for {App}", appName);
        }
    }

    // Registers a browser (chrome/msedge) as a minimal Application record purely so its
    // appId hash resolves to something on the server and its icon can be uploaded — no
    // ApplicationUsage row, no _running/time-tracking entry. BrowserMonitor already owns
    // per-tab time tracking; this exists only so screenshots referencing this appId (taken
    // while a browser was in the foreground) show the right icon.
    private async Task RegisterBrowserIconsAsync(
        List<(string appId, string displayName, string processName, string? iconB64)> browsers, CancellationToken ct)
    {
        try
        {
            var token = await _tokens.GetAsync(ct);
            if (token is null) return;

            var client = _httpFactory.CreateClient("backend");
            client.BaseAddress = new Uri(_opts.BackendBaseUrl);
            client.Timeout     = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var now = DateTime.UtcNow;
            var payload = browsers.Select(b => new
            {
                appId           = b.appId,
                applicationName = b.displayName,
                executableName  = b.processName,
                type            = "Browser",
                displayName     = b.displayName,
                version         = (string?)null,
                iconUrl         = (string?)null,
                status          = "Running",
                createdAt       = now,
                updatedAt       = now,
                activeSeconds   = 0,
                focusSeconds    = 0,
                idleSeconds     = 0,
                crashCount      = 0
            }).ToArray();

            using var resp = await client.PostAsJsonAsync("/api/v1/agentdb/applications", payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Browser icon registration POST {Status}: {Body}", (int)resp.StatusCode, errBody);
                return;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var items = new List<JsonElement>();
            if (root.TryGetProperty("data", out var d))
            {
                if (d.ValueKind == JsonValueKind.Array)
                    foreach (var el in d.EnumerateArray()) items.Add(el);
                else if (d.ValueKind == JsonValueKind.Object)
                    items.Add(d);
            }
            else if (root.ValueKind == JsonValueKind.Array)
                foreach (var el in root.EnumerateArray()) items.Add(el);

            var appIdByEchoedAppId = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var srvAid = item.TryGetProperty("applicationId", out var aidEl) && aidEl.ValueKind == JsonValueKind.String
                    ? aidEl.GetString() : null;
                if (item.TryGetProperty("appId", out var ai) && ai.GetString() is { } aid)
                    appIdByEchoedAppId[aid] = srvAid;
            }

            foreach (var b in browsers)
            {
                if (b.iconB64 is null) continue;
                if (appIdByEchoedAppId.TryGetValue(b.appId, out var serverApplicationId) && serverApplicationId is not null)
                    await UploadIconAsync(client, serverApplicationId, b.iconB64, b.displayName, ct);
                else
                    _log.LogWarning("No server applicationId for browser {App} — icon not uploaded", b.displayName);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RegisterBrowserIconsAsync failed");
        }
    }

    // Closes an open activity interval — foreground lost, day rollover, or graceful shutdown.
    // Wrapped in try/catch (unlike CloseSessionAsync's unwrapped fire-and-forget call sites) since
    // this is invoked via `_ = CloseOpenIntervalAsync(...)` and an unobserved exception from a
    // brand-new code path is a risk not worth introducing.
    private async Task CloseOpenIntervalAsync(OpenInterval iv, DateTime end)
    {
        try
        {
            await _store.CloseActivityIntervalAsync(
                iv.LocalId, end, iv.ActiveSeconds, iv.FocusSeconds, iv.IdleSeconds, end, CancellationToken.None);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Failed to close activity interval for {App}", iv.DisplayName ?? iv.Activity); }
    }

    private async Task CloseSessionAsync(Tracking t, AgentIdentity id, CancellationToken ct)
    {
        var end    = DateTime.UtcNow;
        var dur    = (long)(end - t.StartedUtc).TotalSeconds;
        var idle   = t.IdleSeconds;

        // SyncService may have populated sessionId in DB after the app opened — pick it up now
        if (t.SessionId is null)
            t.SessionId = await _store.GetSessionIdAsync(t.LocalId, ct);

        var usage = new ApplicationUsage
        {
            LocalId         = t.LocalId,
            SessionId       = t.SessionId,
            ApplicationId   = t.ApplicationId,
            ApplicationName = t.DisplayName,
            ProcessName     = t.ProcessName,
            Version         = t.Version,
            ApplicationType = t.ApplicationType,
            Status          = "Closed",
            StartTime       = t.StartedUtc,
            EndTime         = end,
            ActiveSeconds   = t.ActiveSeconds,
            FocusSeconds    = t.FocusSeconds,
            IdleSeconds     = idle,
            UpdatedAt       = end
        };

        await _store.UpdateApplicationUsageAsync(usage, ct);
        _state.PushEvent($"App stop: {t.DisplayName} ({dur}s active={t.ActiveSeconds}s focus={t.FocusSeconds}s)");

        if (t.SessionId is not null)
        {
            // Server expects an array — send close event as single-item array
            var sent = await _hub.TrySendEventAsync("ReportAgentActivity", new[]
            {
                new
                {
                    applicationSessionId = t.SessionId,
                    machineId            = id.MachineId,
                    applicationId        = t.ApplicationId,
                    applicationName      = t.DisplayName,
                    processName          = t.ProcessName,
                    capturedAt           = end,
                    sessionStartTime     = t.StartedUtc,
                    sessionEndTime       = end,
                    activeSeconds        = t.ActiveSeconds,
                    focusSeconds         = t.FocusSeconds,
                    idleSeconds          = idle,
                    durationSeconds      = t.ActiveSeconds + t.FocusSeconds + idle,
                    status               = "Closed",
                    isApplicationClosed  = true
                }
            }, ct);

            if (sent)
            {
                await _store.MarkRowSyncedAsync(t.LocalId, ct);
                _log.LogInformation("App closed via SignalR: {App} sessionId={SessionId}", t.DisplayName, t.SessionId);
            }
            else
            {
                _log.LogWarning("[Hub] Not connected for {App} close — SyncService will POST as fallback", t.DisplayName);
            }
        }
        else
        {
            _log.LogWarning("App closed with no sessionId: {App} localId={LocalId} — SyncService will POST open+close", t.DisplayName, t.LocalId);
        }
    }

}
