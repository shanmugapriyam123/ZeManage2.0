using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Interop;
using ZeManage.Agent.Core.Models;
using ZeManage.Agent.Core.Services;
using ZeManage.Agent.Core.Sync;

namespace ZeManage.Agent.Core.Monitors;

public sealed class BrowserMonitor : BackgroundService
{
    private readonly LocalStore _store;
    private readonly IdentityService _identity;
    private readonly AgentState _state;
    private readonly ILogger<BrowserMonitor> _log;
    private readonly AgentHubConnection _hub;
    private readonly TokenProvider _tokens;

    private string? _currentBrowser;
    private string? _currentTitle;
    private string? _currentUrl;
    private string? _currentApplicationId;
    private DateTime _titleSince;
    private DateTime _lastFlush;

    private static readonly TimeSpan PollInterval  = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(2);
    private const int MinDurationSeconds = 5;

    public BrowserMonitor(LocalStore store, IdentityService identity, AgentState state,
        ILogger<BrowserMonitor> log, AgentHubConnection hub, TokenProvider tokens)
    {
        _store    = store;
        _identity = identity;
        _state    = state;
        _log      = log;
        _hub      = hub;
        _tokens   = tokens;
    }

    private static string MakeApplicationId(string processName) =>
        new Guid(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(processName.ToLowerInvariant()))).ToString();

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

        _currentUrl           = Win32Window.TryGetBrowserUrl();
        _currentApplicationId = MakeApplicationId(proc);

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

        var now = DateTime.UtcNow;
        var activity = new BrowserActivity
        {
            Browser         = _currentBrowser,
            PageTitle       = _currentTitle,
            ApplicationId   = _currentApplicationId,
            Url             = _currentUrl,
            StartTime       = _titleSince,
            EndTime         = endTime,
            DurationSeconds = duration,
            CreatedAt       = now,
            UpdatedAt       = now
        };
        await _store.AddBrowserActivityAsync(activity, ct);

        _log.LogDebug("Browser activity saved: {Browser} | {Title} | {Url} | {Duration}s",
            _currentBrowser, _currentTitle, _currentUrl, duration);

        await _hub.TrySendEventAsync("ReportBrowserActivity", new
        {
            zeUserId        = _tokens.ZeUserId ?? "",
            companyId       = _tokens.CompanyId ?? "",
            browserName     = activity.Browser,
            processName     = activity.Browser == "Google Chrome" ? "chrome" : "msedge",
            pageTitle       = activity.PageTitle,
            url             = activity.Url ?? "",
            applicationId   = activity.ApplicationId,
            startTime       = activity.StartTime,
            endTime         = activity.EndTime,
            durationSeconds = activity.DurationSeconds
        }, ct);
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
