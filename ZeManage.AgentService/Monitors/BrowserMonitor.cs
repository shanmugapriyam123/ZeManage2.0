using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeManage.AgentService.Data;
using ZeManage.AgentService.Interop;
using ZeManage.AgentService.Models;

namespace ZeManage.AgentService.Monitors;

/// <summary>Tracks foreground Chrome/Edge tab title (+ best-effort URL) and writes BrowserActivity
/// rows to SQLite. Track-only, same as ProcessMonitor — SyncService owns the actual API call.</summary>
public sealed class BrowserMonitor : BackgroundService
{
    private readonly LocalStore _store;
    private readonly ILogger<BrowserMonitor> _log;

    private string? _currentBrowser;
    private string? _currentTitle;
    private string? _currentUrl;
    private string? _currentApplicationId;
    private DateTime _titleSince;
    private DateTime _lastFlush;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(2);
    private const int MinDurationSeconds = 5;

    public BrowserMonitor(LocalStore store, ILogger<BrowserMonitor> log)
    {
        _store = store;
        _log = log;
    }

    private static string MakeApplicationId(string processName) =>
        new Guid(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(processName.ToLowerInvariant()))).ToString();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastFlush = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogDebug(ex, "Browser monitor tick error"); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        if (_currentTitle is not null)
            await FlushAsync(DateTime.UtcNow, CancellationToken.None);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var (proc, title) = Win32Window.GetForegroundBrowserInfo();
        var now = DateTime.UtcNow;

        if (proc is null || title is null)
        {
            if (_currentTitle is not null)
            {
                await FlushAsync(now, ct);
                _currentBrowser = null;
                _currentTitle = null;
            }
            return;
        }

        var (browser, pageTitle) = ParseTitle(proc, title);
        if (string.IsNullOrWhiteSpace(pageTitle)) return;

        _currentUrl = Win32Window.TryGetBrowserUrl();
        _currentApplicationId = MakeApplicationId(proc);

        if (_currentTitle is not null && _titleSince.ToLocalTime().Date < now.ToLocalTime().Date)
        {
            await FlushAsync(now, ct);
            _titleSince = now;
            _lastFlush = now;
        }

        if (browser != _currentBrowser || pageTitle != _currentTitle)
        {
            if (_currentTitle is not null) await FlushAsync(now, ct);
            _currentBrowser = browser;
            _currentTitle = pageTitle;
            _titleSince = now;
            _lastFlush = now;
        }
        else if (now - _lastFlush >= FlushInterval)
        {
            await FlushAsync(now, ct);
            _titleSince = now;
            _lastFlush = now;
        }
    }

    private async Task FlushAsync(DateTime endTime, CancellationToken ct)
    {
        if (_currentTitle is null || _currentBrowser is null) return;

        var duration = (long)(endTime - _titleSince).TotalSeconds;
        if (duration < MinDurationSeconds) return;

        var now = DateTime.UtcNow;
        var activity = new BrowserActivity
        {
            Browser = _currentBrowser,
            PageTitle = _currentTitle,
            ApplicationId = _currentApplicationId,
            Url = _currentUrl,
            StartTime = _titleSince,
            EndTime = endTime,
            DurationSeconds = duration,
            CreatedAt = now,
            UpdatedAt = now,
            Synced = false
        };
        try { await _store.AddBrowserActivityAsync(activity, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to save browser activity"); }
    }

    private static (string browser, string pageTitle) ParseTitle(string processName, string windowTitle)
    {
        var browser = processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ? "Google Chrome" : "Microsoft Edge";
        var title = windowTitle;
        if (title.EndsWith(" - Google Chrome", StringComparison.Ordinal)) title = title[..^" - Google Chrome".Length];
        else if (title.EndsWith(" - Microsoft Edge", StringComparison.Ordinal)) title = title[..^" - Microsoft Edge".Length];
        return (browser, title.Trim());
    }
}
