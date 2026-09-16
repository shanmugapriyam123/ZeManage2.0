using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Interop;
using ZeManage.Agent.Core.Models;
using ZeManage.Agent.Core.Services;
using ZeManage.Agent.Core.Sync;

namespace ZeManage.Agent.Core.Monitors;

public sealed class ScreenshotMonitor : BackgroundService
{
    private readonly LocalStore _store;
    private readonly IdentityService _identity;
    private readonly AgentState _state;
    private readonly AgentOptions _opts;
    private readonly ILogger<ScreenshotMonitor> _log;
    private readonly string _screenshotDir;
    private readonly AgentHubConnection _hub;
    private readonly IHttpClientFactory _httpFactory;
    private readonly TokenProvider _tokens;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int n);
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SM_XVIRTUALSCREEN  = 76;
    private const int SM_YVIRTUALSCREEN  = 77;

    // Deterministic ID for a software — same processName always gives same applicationId
    private static string MakeApplicationId(string processName) =>
        new Guid(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(processName.ToLowerInvariant()))).ToString();

    // CaptureAsync is called from four independent, unsynchronized loops (SessionStart above,
    // the Timer loop, WatchIdleReturnAsync, WatchOnDemandCaptureAsync) — with nothing guarding
    // it, two of them coinciding (e.g. the Timer interval elapsing right as the idle-return
    // watcher detects an idle→active transition, or an admin's "Capture Now" landing at the
    // same moment) produces two screenshots at effectively the same CapturedAt timestamp, same
    // foreground app. _captureLock keeps the actual capture+upload from ever running twice at
    // once; _minGapBetweenCaptures then makes the second of two near-simultaneous triggers a
    // deliberate no-op instead of a genuine duplicate screenshot.
    private readonly SemaphoreSlim _captureLock = new(1, 1);
    private static readonly TimeSpan _minGapBetweenCaptures = TimeSpan.FromSeconds(5);

    public ScreenshotMonitor(
        LocalStore store,
        IdentityService identity,
        AgentState state,
        IOptions<AgentOptions> opts,
        ILogger<ScreenshotMonitor> log,
        AgentHubConnection hub,
        IHttpClientFactory httpFactory,
        TokenProvider tokens)
    {
        _store       = store;
        _identity    = identity;
        _state       = state;
        _opts        = opts.Value;
        _log         = log;
        _hub         = hub;
        _httpFactory = httpFactory;
        _tokens      = tokens;
        _screenshotDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZeManage", "screenshots");
        Directory.CreateDirectory(_screenshotDir);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.EnableScreenshots) return;

        var id = _identity.Get();

        var startIdleThreshold = TimeSpan.FromMinutes(_state.IdleThresholdMinutes > 0 ? _state.IdleThresholdMinutes : 5);
        if (_state.IsCaptureEnabled && _opts.ScreenshotOnAttendanceStart && _state.IsScreenshotEnabled && IsWithinSchedule()
            && Win32Idle.GetIdleDuration() < startIdleThreshold)
        {
            try { await CaptureAsync(id, "SessionStart", stoppingToken); } catch { }
        }

        // Run idle-return watcher alongside the timer loop
        _ = WatchIdleReturnAsync(id, stoppingToken);
        _ = WatchOnDemandCaptureAsync(id, stoppingToken);

        // Polls every 15s and re-reads ScreenshotIntervalMinutes on every tick, rather than
        // sleeping for one long Task.Delay(interval) — a single long delay locks in whatever
        // the interval was AT THE MOMENT it started, so an admin lowering the interval mid-wait
        // (or a fresh process start racing SyncService's first settings fetch) wouldn't take
        // effect until the current, possibly-stale wait finished. Checking a rolling checkpoint
        // against the live interval value every 15s means a config change (or the first real
        // fetch after startup) applies within one poll tick instead of up to a full stale cycle.
        var lastCheckpoint = DateTime.UtcNow;
        var pollInterval = TimeSpan.FromSeconds(15);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(pollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            var interval = TimeSpan.FromMinutes(
                _state.ScreenshotIntervalMinutes > 0
                    ? _state.ScreenshotIntervalMinutes
                    : _opts.ScreenshotIntervalMinutes);

            if (DateTime.UtcNow - lastCheckpoint < interval) continue;
            lastCheckpoint = DateTime.UtcNow;

            if (!_state.IsCaptureEnabled || !_state.IsScreenshotEnabled || !IsWithinSchedule()) continue;

            var idleThreshold = TimeSpan.FromMinutes(_state.IdleThresholdMinutes > 0 ? _state.IdleThresholdMinutes : 5);
            if (Win32Idle.GetIdleDuration() >= idleThreshold)
            {
                _log.LogDebug("Screenshot skipped — machine idle/locked");
                continue;
            }

            try { await CaptureAsync(id, "Timer", stoppingToken); }
            catch (Exception ex) { _log.LogDebug(ex, "Screenshot capture failed"); }
        }
    }

    private async Task WatchIdleReturnAsync(AgentIdentity id, CancellationToken ct)
    {
        var wasIdle = false;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { break; }

            if (!_state.IsCaptureEnabled || !_state.IsIdleTrackingEnabled || !_state.IsScreenshotEnabled)
            {
                wasIdle = false;
                continue;
            }

            var threshold  = TimeSpan.FromMinutes(_state.IdleThresholdMinutes > 0 ? _state.IdleThresholdMinutes : 5);
            var currentIdle = Win32Idle.GetIdleDuration();
            var isIdle      = currentIdle >= threshold;

            // Transition: idle → active
            if (wasIdle && !isIdle && IsWithinSchedule())
            {
                try { await CaptureAsync(id, "IdleReturn", ct); }
                catch (Exception ex) { _log.LogDebug(ex, "IdleReturn screenshot failed"); }
            }

            wasIdle = isIdle;
        }
    }

    // Polls AgentState.CaptureScreenshotNowRequested — set by AgentHubConnection's
    // "CaptureScreenshotNow" hub handler — instead of being invoked directly from there, since
    // ScreenshotMonitor already depends on AgentHubConnection (see UploadAndNotifyAsync's
    // ReportScreenshot notify) and the reverse dependency would be a circular constructor
    // reference. 2s poll so an on-demand request feels instant without a dedicated event/signal.
    private async Task WatchOnDemandCaptureAsync(AgentIdentity id, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { break; }

            if (!_state.CaptureScreenshotNowRequested) continue;
            _state.CaptureScreenshotNowRequested = false;

            if (!_opts.EnableScreenshots || !_state.IsCaptureEnabled) continue;

            try { await CaptureAsync(id, "OnDemand", ct); }
            catch (Exception ex) { _log.LogDebug(ex, "On-demand screenshot capture failed"); }
        }
    }

    public async Task CaptureAsync(AgentIdentity id, string trigger, CancellationToken ct)
    {
        await _captureLock.WaitAsync(ct);
        try
        {
            // Re-check right after acquiring the lock, not before — the whole point is to
            // catch the case where another trigger was already mid-capture when this one
            // queued up behind it and only just finished.
            var lastCapture = _state.LatestScreenshotAt;
            if (lastCapture is not null)
            {
                var sinceLast = DateTime.Now - lastCapture.Value;
                if (sinceLast < _minGapBetweenCaptures)
                {
                    _log.LogDebug("Screenshot skipped — {Trigger} arrived {Gap:F1}s after the last capture (< {Window}s window)",
                        trigger, sinceLast.TotalSeconds, _minGapBetweenCaptures.TotalSeconds);
                    return;
                }
            }

            await CaptureCoreAsync(id, trigger, ct);
        }
        finally
        {
            _captureLock.Release();
        }
    }

    private async Task CaptureCoreAsync(AgentIdentity id, string trigger, CancellationToken ct)
    {
        var now     = DateTime.Now;
        var dateDir = Path.Combine(_screenshotDir, now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dateDir);
        var filePath = Path.Combine(dateDir, $"{now:HH-mm-ss}_{trigger}.jpg");

        int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (w <= 0) w = 1920;
        if (h <= 0) h = 1080;

        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)_opts.ScreenshotJpegQuality);
        var jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        bmp.Save(filePath, jpegCodec, encoderParams);

        var fileSize = new FileInfo(filePath).Length;
        var utcNow   = now.ToUniversalTime();

        var foregroundProcess = Win32Window.GetForegroundProcessName();
        var applicationId     = foregroundProcess is not null ? MakeApplicationId(foregroundProcess) : null;

        var screenshot = new Screenshot
        {
            CapturedAt    = utcNow,
            TriggerEvent  = trigger,
            FilePath      = filePath,
            FileSizeBytes = fileSize,
            CreatedAt     = utcNow,
            UpdatedAt     = utcNow,
            ApplicationId = applicationId
        };
        await _store.AddScreenshotAsync(screenshot, ct);

        _state.LatestScreenshotAt = now;
        _state.ScreenshotCount++;
        _state.NotifyChanged();
        _log.LogDebug("Screenshot: {Trigger} → {Path} ({Bytes}b)", trigger, filePath, fileSize);

        await UploadAndNotifyAsync(screenshot, filePath, applicationId, foregroundProcess, ct);
    }

    // Public so SyncService can retry a screenshot whose immediate upload attempt
    // didn't complete (e.g. transient network error) — there is no separate
    // "resubmit metadata" endpoint on the server, so retrying means re-doing
    // the actual multipart upload.
    public async Task UploadAndNotifyAsync(Screenshot screenshot, string filePath, string? applicationId, string? processName, CancellationToken ct)
    {
        try
        {
            var token = await _tokens.GetAsync(ct);
            if (token is null) return;

            var client = _httpFactory.CreateClient("backend");
            client.BaseAddress = new Uri(_opts.BackendBaseUrl);
            client.Timeout     = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            await using var fileStream = File.OpenRead(filePath);
            using var content    = new MultipartFormDataContent();
            var fileContent      = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Add(fileContent, "image", Path.GetFileName(filePath));
            content.Add(new StringContent(screenshot.CapturedAt.ToString("o")), "capturedAt");
            content.Add(new StringContent(screenshot.TriggerEvent ?? ""), "triggerEvent");
            content.Add(new StringContent(processName ?? ""), "processName");

            using var resp = await client.PostAsync("/api/v1/agentdb/screenshots/upload", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Screenshot upload failed: {Status} — {Body}", (int)resp.StatusCode, errBody);
                return;
            }

            string? screenshotId = null;
            string? blobUrl      = null;
            try
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                var data = root.TryGetProperty("data", out var d) ? d : root;
                if (data.TryGetProperty("screenshotId", out var sid) ||
                    data.TryGetProperty("id",            out sid))
                    screenshotId = sid.GetString();
                if (data.TryGetProperty("blobUrl", out var bu))
                    blobUrl = bu.GetString();
            }
            catch { }

            await _store.MarkSyncedAsync(new[] { screenshot }, ct);
            _log.LogInformation("Screenshot uploaded: {ScreenshotId}", screenshotId ?? "(no id)");

            // The server's ReportScreenshot hub method binds screenshotId to a non-nullable
            // Guid — sending JSON null there fails during SignalR's own argument deserialization
            // (before the hub method's try/catch even runs), which kills the WebSocket outright
            // ("Websocket closed with error: InternalServerError") instead of just failing this
            // one call. The upload itself already succeeded and is marked Synced above; this
            // notify is a best-effort real-time ping only, so skipping it when there's no id is
            // strictly safer than risking the whole connection over a cosmetic notification.
            if (screenshotId is not null)
            {
                await _hub.TrySendEventAsync("ReportScreenshot", new
                {
                    screenshotId = screenshotId,
                    blobUrl      = blobUrl,
                    capturedAt   = screenshot.CapturedAt,
                    triggerEvent = screenshot.TriggerEvent
                }, ct);
            }
            else
            {
                _log.LogWarning("Skipped ReportScreenshot notify — server response had no screenshotId (would crash the hub's non-nullable Guid binding)");
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Screenshot upload/notify failed");
        }
    }

    private bool IsWithinSchedule()
    {
        var now  = DateTime.Now;
        var time = now.TimeOfDay;

        var dayEnabled = now.DayOfWeek switch
        {
            DayOfWeek.Monday    => _state.ScreenshotMonday,
            DayOfWeek.Tuesday   => _state.ScreenshotTuesday,
            DayOfWeek.Wednesday => _state.ScreenshotWednesday,
            DayOfWeek.Thursday  => _state.ScreenshotThursday,
            DayOfWeek.Friday    => _state.ScreenshotFriday,
            DayOfWeek.Saturday  => _state.ScreenshotSaturday,
            DayOfWeek.Sunday    => _state.ScreenshotSunday,
            _                   => false
        };

        // "Active time window" toggle governs the start/end TIME restriction specifically —
        // active days (above) stay in effect regardless of it, matching the web portal's
        // "Active time window" section being just the Start/End time fields, separate from
        // the "Active days" section.
        if (!_state.IsActiveWindowEnabled) return dayEnabled;

        return dayEnabled
            && time >= _state.ScreenshotStartTime
            && time <= _state.ScreenshotEndTime;
    }
}
