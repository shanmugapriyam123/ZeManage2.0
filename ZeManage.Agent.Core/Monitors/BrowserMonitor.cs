using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Interop;
using ZeManage.Agent.Core.Models;
using ZeManage.Agent.Core.Services;

namespace ZeManage.Agent.Core.Monitors;

public sealed class BrowserMonitor : BackgroundService
{
    private readonly LocalStore _store;
    private readonly IdentityService _identity;
    private readonly AgentState _state;
    private readonly ILogger<BrowserMonitor> _log;

    private string? _currentBrowser;
    private string? _currentTitle;
    private DateTime _titleSince;
    private DateTime _lastFlush;

    private static readonly TimeSpan PollInterval  = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(2);
    private const int MinDurationSeconds = 5;

    public BrowserMonitor(LocalStore store, IdentityService identity, AgentState state, ILogger<BrowserMonitor> log)
    {
        _store    = store;
        _identity = identity;
        _state    = state;
        _log      = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var id = _identity.Get();
        _lastFlush = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(id, stoppingToken); }
            catch (Exception ex) { _log.LogDebug(ex, "Browser monitor tick error"); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        if (_currentTitle is not null)
            await FlushAsync(id, DateTime.UtcNow, CancellationToken.None);
    }

    private async Task TickAsync(AgentIdentity id, CancellationToken ct)
    {
        var (proc, title) = Win32Window.GetForegroundBrowserInfo();
        var now = DateTime.UtcNow;

        if (proc is null || title is null)
        {
            if (_currentTitle is not null)
            {
                await FlushAsync(id, now, ct);
                _currentBrowser = null;
                _currentTitle   = null;
                _state.CurrentBrowserTitle = null;
                _state.CurrentBrowserName  = null;
                _state.NotifyChanged();
            }
            return;
        }

        var (browser, pageTitle) = ParseTitle(proc, title);
        if (string.IsNullOrWhiteSpace(pageTitle)) return;

        if (browser != _currentBrowser || pageTitle != _currentTitle)
        {
            if (_currentTitle is not null)
                await FlushAsync(id, now, ct);

            _currentBrowser = browser;
            _currentTitle   = pageTitle;
            _titleSince     = now;
            _lastFlush      = now;

            _state.CurrentBrowserTitle = pageTitle;
            _state.CurrentBrowserName  = browser;
            _state.BrowserActivityAt   = now;
            _state.PushEvent($"[{browser}] {pageTitle}");
        }
        else if (now - _lastFlush >= FlushInterval)
        {
            await FlushAsync(id, now, ct);
            _titleSince = now;
            _lastFlush  = now;
        }
    }

    private async Task FlushAsync(AgentIdentity id, DateTime endTime, CancellationToken ct)
    {
        if (_currentTitle is null || _currentBrowser is null) return;

        var duration = (long)(endTime - _titleSince).TotalSeconds;
        if (duration < MinDurationSeconds) return;

        await _store.AddBrowserActivityAsync(new BrowserActivity
        {
            UserName        = id.UserName,
            MachineName     = id.MachineName,
            WindowsSid      = id.WindowsSid,
            Browser         = _currentBrowser,
            PageTitle       = _currentTitle,
            StartTime       = _titleSince,
            EndTime         = endTime,
            DurationSeconds = duration
        }, ct);

        _log.LogDebug("Browser activity saved: {Browser} | {Title} | {Duration}s",
            _currentBrowser, _currentTitle, duration);
    }

    private static (string browser, string pageTitle) ParseTitle(string processName, string windowTitle)
    {
        var browser = processName.Equals("chrome", StringComparison.OrdinalIgnoreCase)
            ? "Google Chrome" : "Microsoft Edge";

        var title = windowTitle;
        if (title.EndsWith(" - Google Chrome", StringComparison.Ordinal))
            title = title[..^" - Google Chrome".Length];
        else if (title.EndsWith(" - Microsoft Edge", StringComparison.Ordinal))
            title = title[..^" - Microsoft Edge".Length];

        return (browser, title.Trim());
    }
}
