using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.AgentService.Data;
using ZeManage.AgentService.Interop;
using ZeManage.AgentService.Models;
using ZeManage.AgentService.Services;

namespace ZeManage.AgentService.Monitors;

/// <summary>Periodic full-virtual-screen capture, saved as JPEG under its own screenshots folder
/// and recorded in SQLite. Track-only — SyncService performs the actual multipart upload.</summary>
public sealed class ScreenshotMonitor : BackgroundService
{
    private readonly LocalStore _store;
    private readonly AgentServiceOptions _opts;
    private readonly AgentSettings _settings;
    private readonly ILogger<ScreenshotMonitor> _log;
    private readonly string _screenshotDir;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int n);
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;

    public ScreenshotMonitor(LocalStore store, IOptions<AgentServiceOptions> opts, AgentSettings settings, ILogger<ScreenshotMonitor> log)
    {
        _store = store;
        _opts = opts.Value;
        _settings = settings;
        _log = log;
        _screenshotDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZeManageAgentService", "Screenshots");
        Directory.CreateDirectory(_screenshotDir);
    }

    private static string MakeApplicationId(string processName) =>
        new Guid(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(processName.ToLowerInvariant()))).ToString();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.EnableScreenshots) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalMinutes = _settings.ScreenshotIntervalMinutes ?? _opts.ScreenshotIntervalMinutes;
            try { await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken); }
            catch (OperationCanceledException) { break; }

            if (!_settings.IsScreenshotEnabled)
            {
                _log.LogDebug("Screenshot skipped — disabled by server (group worktime settings)");
                continue;
            }
            if (!_settings.IsWithinSchedule(DateTime.Now))
            {
                _log.LogDebug("Screenshot skipped — outside configured active window/day");
                continue;
            }

            var idleThresholdMinutes = _settings.IdleThresholdMinutes ?? (_opts.IdleThresholdSeconds / 60.0);
            if (Win32Idle.GetIdleDuration() >= TimeSpan.FromMinutes(idleThresholdMinutes))
            {
                _log.LogDebug("Screenshot skipped — machine idle/locked");
                continue;
            }

            try { await CaptureAsync("Timer", stoppingToken); }
            catch (Exception ex) { _log.LogDebug(ex, "Screenshot capture failed"); }
        }
    }

    private async Task CaptureAsync(string trigger, CancellationToken ct)
    {
        var now = DateTime.Now;
        var dateDir = Path.Combine(_screenshotDir, now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dateDir);
        var filePath = Path.Combine(dateDir, $"{now:HH-mm-ss}_{trigger}.jpg");

        int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (w <= 0) w = 1920;
        if (h <= 0) h = 1080;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)_opts.ScreenshotJpegQuality);
        var jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        bmp.Save(filePath, jpegCodec, encoderParams);

        var fileSize = new FileInfo(filePath).Length;
        var utcNow = now.ToUniversalTime();
        var foregroundProcess = Win32Window.GetForegroundProcessName();

        var screenshot = new Screenshot
        {
            CapturedAt = utcNow,
            TriggerEvent = trigger,
            FilePath = filePath,
            FileSizeBytes = fileSize,
            ApplicationId = foregroundProcess is not null ? MakeApplicationId(foregroundProcess) : null,
            ProcessName = foregroundProcess,
            CreatedAt = utcNow,
            UpdatedAt = utcNow,
            Synced = false
        };
        await _store.AddScreenshotAsync(screenshot, ct);
        _log.LogDebug("Screenshot: {Trigger} -> {Path} ({Bytes}b)", trigger, filePath, fileSize);
    }
}
