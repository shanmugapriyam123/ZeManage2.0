using System.Windows;
using Application = System.Windows.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZeManage.Agent.Core;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Services;

namespace ZeManage.Agent;

public partial class App : Application
{
    private const string AutoStartKey  = "ZeManageAgent";
    private const string AutoStartPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static IHost? Host { get; private set; }
    private TrayIconManager? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Register in Windows startup so the agent runs on every boot (minimized in tray).
        RegisterAutoStart();

        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(lb =>
            {
                lb.ClearProviders();
                lb.AddDebug();
                lb.AddConsole();
                lb.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddZeManageAgent();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        // Ensure both the agent DB schema and the BrowserActivities upgrade table exist
        // before any monitor tries to write to them.
        var store = Host.Services.GetRequiredService<LocalStore>();
        await store.EnsureCreatedAsync();

        // Capture machine/user identity into agent.db on every startup.
        // captured_at is preserved from the first run (COALESCE in UPSERT).
        var identity = Host.Services.GetRequiredService<IdentityService>().Get();
        _ = store.SaveIdentityAsync(identity).ContinueWith(t =>
        {
            if (t.IsFaulted)
                System.Diagnostics.Debug.WriteLine($"[ZeManage] SaveIdentity failed: {t.Exception?.InnerException?.Message}");
        });

        await Host.StartAsync();

        var state = Host.Services.GetRequiredService<AgentState>();
        var window = Host.Services.GetRequiredService<MainWindow>();
        _tray = new TrayIconManager(window, state, this);

        // Start minimized when launched by Windows startup (--minimized flag)
        if (!e.Args.Contains("--minimized"))
            window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        if (Host is not null)
        {
            await Host.StopAsync(TimeSpan.FromSeconds(5));
            Host.Dispose();
        }
        base.OnExit(e);
    }

    /// <summary>
    /// Writes the exe path + --minimized to HKCU Run so Windows starts the agent on every login.
    /// Uses HKCU (per-user) so no elevation is required.
    /// </summary>
    private static void RegisterAutoStart()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            using var key = Registry.CurrentUser.OpenSubKey(AutoStartPath, writable: true);
            if (key is null) return;

            var existing = key.GetValue(AutoStartKey) as string;
            var desired  = $"\"{exePath}\" --minimized";

            // Only write if the value is missing or the path changed (handles reinstall/move).
            if (!string.Equals(existing, desired, StringComparison.OrdinalIgnoreCase))
                key.SetValue(AutoStartKey, desired);
        }
        catch
        {
            // Non-fatal: agent still works without auto-start if registry write fails.
        }
    }

    /// <summary>
    /// Removes the auto-start entry. Call from settings UI or tray "Disable Auto-Start" menu item.
    /// </summary>
    public static void UnregisterAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartPath, writable: true);
            key?.DeleteValue(AutoStartKey, throwOnMissingValue: false);
        }
        catch { }
    }
}
