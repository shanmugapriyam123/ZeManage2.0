using System.Drawing;
using System.Windows;
using ZeManage.Agent.Core.Services;
using WinForms = System.Windows.Forms;

namespace ZeManage.Agent;

public sealed class TrayIconManager : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly MainWindow _window;

    public TrayIconManager(MainWindow window, AgentState state)
    {
        _window = window;

        _icon = new WinForms.NotifyIcon
        {
            Icon    = LoadTrayIcon(),
            Text    = "ZeManage Agent",
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open ZeManage Agent", null, (_, _) => ShowWindow());

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowWindow();
        // Left-click alone should also open it — right-click still opens the ContextMenuStrip
        // above (WinForms handles that automatically, no extra wiring needed for it).
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                ShowWindow();
        };

        _window.Closing += (s, e) =>
        {
            e.Cancel = true;
            _window.Hide();
        };
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/BIManage.ico");
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info?.Stream is not null)
                return new Icon(info.Stream);
        }
        catch { }
        return SystemIcons.Application;
    }

    // Topmost toggle forces Windows to actually raise the window above whatever else has focus —
    // Activate() alone is unreliable when the caller (tray icon click) isn't already the
    // foreground process, which is exactly the case here.
    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
