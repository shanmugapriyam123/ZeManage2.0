using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
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
    private readonly object _lock = new();
    private string? _lastLiveActivityApp; // dedup — skip if same app still in foreground
    private DateTime _lastLiveSentUtc = DateTime.MinValue; // last successful live send — drives the 30-s periodic freshness ping for an unchanged foreground app
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromSeconds(30);
    // Instant foreground-change detection (Insightful-style): the OS-level
    // SetWinEventHook fires this semaphore the moment focus changes, waking
    // RunActivityTickAsync immediately instead of letting it sleep out the
    // remainder of its poll interval. Max count 1 → rapid bursts coalesce;
    // the woken tick reads the CURRENT foreground so the final app wins.
    private readonly SemaphoreSlim _fgChangedSignal = new(0, 1);
    private ForegroundWatcher? _fgWatcher;
    private string _lastHubState = ""; // tracks the previous hub state so we can force a re-send whenever the hub transitions from any non-Connected state (Reconnecting/Disconnected) back to Connected — otherwise LiveStatusChanged events fired during the disconnect window are lost and the portal's Task column stays stuck on the pre-drop app until the user manually switches to a different app.

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
    private static string? CaptureIconBase64(string? exePath)
    {
        if (exePath is null) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon is null) return null;
            using var bmp  = icon.ToBitmap();
            using var ms   = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
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
                            FocusSeconds    = app.FocusSeconds
                        };
                    }
                }
            }
            _log.LogInformation("Restored {Count} running app sessions from DB", existing.Count);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not restore running sessions from DB"); }

        // OS-level instant foreground-change hook — wakes the activity tick the
        // moment focus changes so the live send fires in ~100 ms instead of up
        // to a full poll interval later. See ForegroundWatcher for details.
        _fgWatcher = new ForegroundWatcher(() =>
        {
            try { _fgChangedSignal.Release(); }
            catch (SemaphoreFullException) { /* burst coalescing — a wake is already pending */ }
        });

        var signalRInterval  = TimeSpan.FromSeconds(_opts.HeartbeatIntervalSeconds);
        var activityTask     = RunActivityTickAsync(activityInterval, stoppingToken);
        var signalRTickTask  = RunSignalRTickAsync(signalRInterval, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { ScanAndUpdate(id); }
            catch (Exception ex) { _log.LogWarning(ex, "Process scan failed"); }
            try { await Task.Delay(scanInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _fgWatcher?.Dispose();
        await Task.WhenAll(activityTask, signalRTickTask);

        List<Tracking> toClose;
        lock (_lock)
            toClose = _running.Values.ToList();

        foreach (var t in toClose)
            await CloseSessionAsync(t, id, CancellationToken.None);
    }

    private async Task RunActivityTickAsync(TimeSpan interval, CancellationToken ct)
    {
        var activeThreshold = TimeSpan.FromSeconds(_opts.ActiveThresholdSeconds);
        var lastTick        = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Re-read each tick so server-updated value takes effect immediately
                var idleThreshold  = TimeSpan.FromMinutes(_state.IdleThresholdMinutes);
                var idle           = Win32Idle.GetIdleDuration();
                var foreground     = Win32Window.GetForegroundProcessName();
                var now            = DateTime.UtcNow;
                var elapsed        = (long)(now - lastTick).TotalSeconds;
                lastTick           = now;

                // Only track when app is in foreground and user hasn't walked away
                if (foreground is not null && elapsed > 0 && idle < idleThreshold)
                {
                    lock (_lock)
                    {
                        if (_running.TryGetValue(foreground, out var tracked))
                        {
                            // Focus: app is the active foreground window
                            tracked.FocusSeconds += elapsed;
                            // Active: subset of focus where mouse/keyboard was recently used
                            if (idle < activeThreshold)
                                tracked.ActiveSeconds += elapsed;
                        }
                    }
                }

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
                // ALSO as a periodic freshness ping every 30 s for the SAME app.
                // The change-only dedup meant that while the user stayed in one
                // app the server's Redis live cache aged and any freshly loaded
                // dashboard sat on a stale Postgres-derived value until the next
                // switch (potentially minutes). The 30-s refresh keeps the cache
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
                            _lastLiveActivityApp = foreground;
                            _lastLiveSentUtc     = DateTime.UtcNow;
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

                List<(string sessionId, string localId, string displayName,
                      string processName, string applicationId, DateTime startedUtc,
                      long active, long focus, long idle)> snapshots;

                lock (_lock)
                {
                    snapshots = _running.Values
                        .Where(t => t.SessionId is not null)
                        .Select(t =>
                        {
                            var idle = Math.Max(0L, t.FocusSeconds - t.ActiveSeconds);
                            return (t.SessionId!, t.LocalId, t.DisplayName,
                                    t.ProcessName, t.ApplicationId, t.StartedUtc,
                                    t.ActiveSeconds, t.FocusSeconds, idle);
                        })
                        .ToList();
                }

                var machineId = _identity.Get().MachineId;

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
                        durationSeconds      = s.active + s.focus,
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
                        await _hub.TrySendEventAsync("ReportAgentActivity", chunk, ct);
                }

                foreach (var (sessionId, localId, displayName, processName, applicationId, startedUtc, active, focus, idle) in snapshots)
                    await _store.UpdateRunningStatsAsync(localId, active, focus, idle, now, ct);

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

                if (def.Category == "Browser") continue;

                bool isNew;
                lock (_lock) { isNew = !_running.ContainsKey(key); }

                if (isNew)
                {
                    string? version = null;
                    string? exePath = null;
                    try { version = p.MainModule?.FileVersionInfo.FileVersion; } catch { }
                    try { exePath = p.MainModule?.FileName; } catch { }

                    var now     = DateTime.UtcNow;
                    var localId = Guid.NewGuid().ToString();
                    var appId   = MakeApplicationId(p.ProcessName);
                    var iconB64 = CaptureIconBase64(exePath);

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
                iconUrl         = x.usage.IconBase64,
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

            // Build lookup maps: localId → sessionId, appId → sessionId (case-insensitive field scan)
            var byLocalId = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var byAppId   = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var sid = ExtractSessionId(item);
                if (item.TryGetProperty("localId", out var li) && li.GetString() is { } lid) byLocalId[lid] = sid;
                if (item.TryGetProperty("appId",   out var ai) && ai.GetString() is { } aid) byAppId[aid]   = sid;
            }

            for (int i = 0; i < toPost.Count; i++)
            {
                var entry = toPost[i];
                string? sessionId = null;

                // 1. Match by localId (server echoed correlation ID — most reliable)
                if (byLocalId.TryGetValue(entry.t.LocalId, out var s1)) sessionId = s1;
                // 2. Match by appId
                else if (byAppId.TryGetValue(entry.t.ApplicationId, out var s2)) sessionId = s2;
                // 3. Fall back to index
                else if (i < items.Count) sessionId = ExtractSessionId(items[i]);

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
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "InsertThenPostBatch failed");
        }
    }

    private async Task CloseSessionAsync(Tracking t, AgentIdentity id, CancellationToken ct)
    {
        var end    = DateTime.UtcNow;
        var dur    = (long)(end - t.StartedUtc).TotalSeconds;
        var idle   = Math.Max(0L, t.FocusSeconds - t.ActiveSeconds);

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
                    durationSeconds      = t.ActiveSeconds + t.FocusSeconds,
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
