using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Models;
using ZeManage.Agent.Core.Services;

namespace ZeManage.Agent.Core.Monitors;

public sealed class ScreenshotMonitor : BackgroundService
{
    private readonly LocalStore _store;
    private readonly IdentityService _identity;
    private readonly AgentState _state;
    private readonly AgentOptions _opts;
    private readonly ILogger<ScreenshotMonitor> _log;
    private readonly string _screenshotDir;

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
        ILogger<ScreenshotMonitor> log)
    {
        _store    = store;
        _identity = identity;
        _state    = state;
        _opts     = opts.Value;
        _log      = log;
        _screenshotDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZeManage", "screenshots");
        Directory.CreateDirectory(_screenshotDir);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.EnableScreenshots) return;

        var id = _identity.Get();

        // Capture on start if configured
        if (_opts.ScreenshotOnAttendanceStart)
        {
            try { await CaptureAsync(id, "SessionStart", stoppingToken); } catch { }
        }

        var interval = TimeSpan.FromMinutes(_opts.ScreenshotIntervalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try { await CaptureAsync(id, "Timer", stoppingToken); }
            catch (Exception ex) { _log.LogDebug(ex, "Screenshot capture failed"); }
        }
    }

    public async Task CaptureAsync(AgentIdentity id, string trigger, CancellationToken ct)
    {
        var now    = DateTime.Now;
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
        await _store.AddScreenshotAsync(new Screenshot
        {
            UserName      = id.UserName,
            MachineName   = id.MachineName,
            WindowsSid    = id.WindowsSid,
            CapturedAt    = now.ToUniversalTime(),
            TriggerEvent  = trigger,
            FilePath      = filePath,
            FileSizeBytes = fileSize
        }, ct);

        _state.LatestScreenshotAt = now;
        _state.ScreenshotCount++;
        _state.NotifyChanged();
        _log.LogDebug("Screenshot: {Trigger} → {Path} ({Bytes}b)", trigger, filePath, fileSize);
    }
}
