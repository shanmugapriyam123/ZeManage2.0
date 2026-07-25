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

        if (_opts.ScreenshotOnAttendanceStart && _state.IsScreenshotEnabled && IsWithinSchedule())
        {
            try { await CaptureAsync(id, "SessionStart", stoppingToken); } catch { }
        }

        // Run idle-return watcher alongside the timer loop
        _ = WatchIdleReturnAsync(id, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMinutes(
                _state.ScreenshotIntervalMinutes > 0
                    ? _state.ScreenshotIntervalMinutes
                    : _opts.ScreenshotIntervalMinutes);

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            if (!_state.IsScreenshotEnabled || !IsWithinSchedule()) continue;

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

            if (!_state.IsIdleTrackingEnabled || !_state.IsScreenshotEnabled)
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

    public async Task CaptureAsync(AgentIdentity id, string trigger, CancellationToken ct)
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
        var screenshot = new Screenshot
        {
            CapturedAt    = utcNow,
            TriggerEvent  = trigger,
            FilePath      = filePath,
            FileSizeBytes = fileSize,
            CreatedAt     = utcNow,
            UpdatedAt     = utcNow
        };
        await _store.AddScreenshotAsync(screenshot, ct);

        _state.LatestScreenshotAt = now;
        _state.ScreenshotCount++;
        _state.NotifyChanged();
        _log.LogDebug("Screenshot: {Trigger} → {Path} ({Bytes}b)", trigger, filePath, fileSize);

        await UploadAndNotifyAsync(screenshot, filePath, ct);
    }

    private async Task UploadAndNotifyAsync(Screenshot screenshot, string filePath, CancellationToken ct)
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

            await _hub.TrySendEventAsync("ReportScreenshot", new
            {
                screenshotId = screenshotId,
                blobUrl      = blobUrl,
                capturedAt   = screenshot.CapturedAt,
                triggerEvent = screenshot.TriggerEvent
            }, ct);
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

        return dayEnabled
            && time >= _state.ScreenshotStartTime
            && time <= _state.ScreenshotEndTime;
    }
}
