using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Options;
using ZeManage.Agent.Core.Services;

namespace ZeManage.Agent;

public partial class MainWindow : Window
{
    private readonly AgentState _state;
    private readonly IdentityService _identity;
    private readonly AgentOptions _opts;
    private readonly DispatcherTimer _timer;

    public MainWindow(AgentState state, IdentityService identity, IOptions<AgentOptions> opts)
    {
        InitializeComponent();
        _state = state;
        _identity = identity;
        _opts = opts.Value;

        var id = _identity.Get();
        UserText.Text    = id.UserName;
        MachineText.Text = id.MachineName;
        AgentIdText.Text = (id.ZeUserId?.ToString() ?? id.MachineId)[..8] + "…";
        OsText.Text      = id.OsVersion;
        VersionText.Text = "v0.1.0";
        DbPathText.Text  = "DB: " + _opts.DatabasePath;

        CpuModelText.Text = id.CpuModel ?? "--";
        TotalRamText.Text = id.TotalRamGB > 0 ? $"{id.TotalRamGB:F1} GB" : "--";
        IpText.Text       = id.IpAddress ?? "--";
        MacText.Text      = id.MacAddress ?? "--";
        SidText.Text      = id.WindowsSid ?? "--";

        _state.Changed += OnStateChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    private void OnStateChanged()
    {
        Dispatcher.BeginInvoke(new Action(Refresh));
    }

    private void Refresh()
    {
        var att = _state.CurrentAttendance;
        if (att is not null)
        {
            ActiveText.Text = FormatDuration(att.ActiveSeconds);
            IdleText.Text = FormatDuration(att.IdleSeconds);
            LoginText.Text = att.LoginTime.ToLocalTime().ToString("HH:mm");
        }

        var hw = _state.LatestHardware;
        if (hw is not null)
        {
            CpuText.Text = $"{hw.CpuUsagePercent:0}%";
            RamText.Text = $"{hw.RamUsagePercent:0}%";
            GpuText.Text = $"{hw.GpuUsagePercent:0}%";
            DiskText.Text = $"{hw.DiskUsagePercent:0}%";
        }

        var net = _state.LatestNetwork;
        if (net is not null)
        {
            LatencyText.Text = $"{net.LatencyMs:0} ms";
            LossText.Text = $"{net.PacketLossPercent:0}%";
            HealthText.Text = net.HealthScore.ToString();
            VpnText.Text = net.VpnConnected ? "Yes" : "No";
        }

        SyncStatusText.Text = _state.LastSyncStatus;
        LastSyncText.Text = _state.LastSyncAt?.ToString("HH:mm:ss") ?? "Never";
        UnsyncedText.Text = _state.UnsyncedCount.ToString();
        HubStatusText.Text      = _state.HubConnectionState;
        ScreenshotCountText.Text = _state.ScreenshotCount.ToString();

        if (_state.CurrentBrowserTitle is not null)
        {
            BrowserNameText.Text  = _state.CurrentBrowserName  ?? "--";
            BrowserTitleText.Text = _state.CurrentBrowserTitle;
            BrowserSinceText.Text = _state.BrowserActivityAt?.ToLocalTime().ToString("HH:mm") ?? "--:--";
        }
        else
        {
            BrowserNameText.Text  = "--";
            BrowserTitleText.Text = "Not tracking";
            BrowserSinceText.Text = "--:--";
        }

        EventsList.ItemsSource = null;
        lock (_state.RecentEvents)
            EventsList.ItemsSource = _state.RecentEvents.ToList();
    }

    private static string FormatDuration(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{t.Minutes}m {t.Seconds}s";
    }
}
