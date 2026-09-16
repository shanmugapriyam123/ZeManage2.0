using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Options;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Services;
using ZeManage.Agent.Core.Sync;

namespace ZeManage.Agent;

public partial class MainWindow : Window
{
    private readonly AgentState _state;
    private readonly IdentityService _identity;
    private readonly AgentOptions _opts;
    private readonly LocalStore _store;
    private readonly AttendanceApiService _attendanceApi;
    private readonly DispatcherTimer _timer;
    private bool _attendanceRefreshInFlight;

    public MainWindow(
        AgentState state, IdentityService identity, IOptions<AgentOptions> opts,
        LocalStore store, AttendanceApiService attendanceApi)
    {
        InitializeComponent();
        _state = state;
        _identity = identity;
        _opts = opts.Value;
        _store = store;
        _attendanceApi = attendanceApi;

        var id = _identity.Get();
        UserText.Text     = id.UserName;
        MachineText.Text  = id.MachineName;
        InitialsText.Text = GetInitials(id.UserName);

        _state.Changed += OnStateChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => { Refresh(); _ = RefreshAttendanceAsync(); };
        _timer.Start();

        Refresh();
        _ = RefreshAttendanceAsync();
    }

    private void OnStateChanged()
    {
        Dispatcher.BeginInvoke(new Action(Refresh));
    }

    // Today's Active/Idle/Break/Total-Work-Time card — real server data, via AttendanceApiService
    // (GET /api/v1/agentdb/employee-machine-dashboard/today-summary), the SAME numbers the admin's
    // own Employee Machine Dashboard shows for this employee's today. Falls back to the local
    // SQLite copy (LocalStore.GetTodayActivitySummaryAsync) only when the API call fails — offline,
    // backend unreachable, not logged in yet — so the card still shows something instead of going
    // blank. Guarded by _attendanceRefreshInFlight so the 2-second timer can't stack overlapping
    // calls if one run takes long.
    private async Task RefreshAttendanceAsync()
    {
        if (_attendanceRefreshInFlight) return;
        _attendanceRefreshInFlight = true;
        try
        {
            long active, idle, brk, totalWork;
            var apiSummary = await _attendanceApi.GetTodaySummaryAsync();
            if (apiSummary is { } s)
            {
                (active, idle, brk, totalWork) = s;
            }
            else
            {
                var local = await _store.GetTodayActivitySummaryAsync();
                active    = local.ActiveSeconds;
                idle      = local.IdleSeconds;
                brk       = local.BreakSeconds;
                totalWork = active + idle + brk;
            }

            ActiveText.Text    = FormatDuration(active);
            IdleText.Text      = FormatDuration(idle);
            BreakText.Text     = FormatDuration(brk);
            TotalWorkText.Text = FormatDuration(totalWork);
        }
        catch { }
        finally { _attendanceRefreshInFlight = false; }
    }

    // Small connectivity dot + label — green/"Synced" while syncing normally, amber/"Connecting"
    // while waiting for the first sync, red/"Offline" on error. Click retries a sync, same as the
    // old status banner did.
    private void Refresh()
    {
        var isRunning = _state.LastSyncAt != null &&
            (_state.LastSyncStatus == "OK" || _state.LastSyncStatus == "Up to date");
        var isError = _state.LastSyncStatus?.StartsWith("Error:") == true ||
            _state.LastSyncStatus == "Backend unreachable / login failed (cached locally)";

        StatusDot.Fill = new SolidColorBrush(
            isRunning ? System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A) :
            isError   ? System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26) :
                        System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06));
        StatusLabel.Text = isRunning ? "Synced" : isError ? "Offline" : "Connecting";
    }

    private void AgentStatusBanner_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _state.RequestImmediateSync();
    }

    private static string FormatDuration(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{t.Minutes}m {t.Seconds}s";
    }

    // Two-letter avatar badge — first letter of the first two name segments (space/dot/underscore/
    // hyphen delimited, matching Windows username conventions like "GF_S_037" or "Priya.Kumar").
    private static string GetInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}";
        return name.Length >= 2 ? name[..2].ToUpperInvariant() : name.ToUpperInvariant();
    }
}
