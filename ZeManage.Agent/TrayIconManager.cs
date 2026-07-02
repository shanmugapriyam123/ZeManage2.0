using System.Drawing;
using System.Windows;
using Application = System.Windows.Application;
using ZeManage.Agent.Core.Services;
using WinForms = System.Windows.Forms;
using Microsoft.Win32;

namespace ZeManage.Agent;

public sealed class TrayIconManager : IDisposable
{
    private const string AutoStartPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartKey  = "ZeManageAgent";

    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _autoStartItem;
    private readonly MainWindow _window;
    private readonly Application _app;

    public TrayIconManager(MainWindow window, AgentState state, Application app)
    {
        _window = window;
        _app    = app;

        _icon = new WinForms.NotifyIcon
        {
            Icon    = SystemIcons.Application,
            Text    = "ZeManage Agent",
            Visible = true
        };

        _autoStartItem = new WinForms.ToolStripMenuItem
        {
            Text    = AutoStartEnabled() ? "Disable Auto-Start" : "Enable Auto-Start",
            Checked = AutoStartEnabled(),
        };
        _autoStartItem.Click += OnToggleAutoStart;

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open",  null, (_, _) => ShowWindow());
        menu.Items.Add("-");
        menu.Items.Add(_autoStartItem);
        menu.Items.Add("-");
        menu.Items.Add("Exit",  null, (_, _) => _app.Shutdown());

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowWindow();

        _window.Closing += (s, e) =>
        {
            e.Cancel = true;
            _window.Hide();
        };
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnToggleAutoStart(object? sender, EventArgs e)
    {
        if (AutoStartEnabled())
        {
            App.UnregisterAutoStart();
            _autoStartItem.Text    = "Enable Auto-Start";
            _autoStartItem.Checked = false;
        }
        else
        {
            // Re-register
            try
            {
                var exePath = Environment.ProcessPath ?? string.Empty;
                if (!string.IsNullOrEmpty(exePath))
                {
                    using var key = Registry.CurrentUser.OpenSubKey(AutoStartPath, writable: true);
                    key?.SetValue(AutoStartKey, $"\"{exePath}\" --minimized");
                }
            }
            catch { }
            _autoStartItem.Text    = "Disable Auto-Start";
            _autoStartItem.Checked = true;
        }
    }

    private static bool AutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartPath);
            return key?.GetValue(AutoStartKey) is not null;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
