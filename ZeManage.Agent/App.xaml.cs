using System.IO;
using System.Threading;
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
    private const string MutexName     = "ZeManageAgent-SingleInstance";

    public static IHost? Host { get; private set; }
    private TrayIconManager? _tray;
    private Mutex? _mutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Prevent duplicate instances (HKCU Run + Task Scheduler both fire at login)
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, out bool isFirst);
        if (!isFirst)
        {
            _mutex.Dispose();
            Shutdown();
            return;
        }

        // Belt-and-suspenders: keep HKCU Run key current (installer is primary, this is backup)
        RegisterAutoStart();

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BIManageRevit", "Logs", "agent.log");

        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(lb =>
            {
                lb.ClearProviders();
                lb.AddDebug();
                lb.AddProvider(new FileLoggerProvider(logPath));
                lb.SetMinimumLevel(LogLevel.Debug);
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddZeManageAgent();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        var store = Host.Services.GetRequiredService<LocalStore>();
        await store.EnsureCreatedAsync();

        var identity = Host.Services.GetRequiredService<IdentityService>().Get();
        var log = Host.Services.GetRequiredService<ILogger<App>>();
        try
        {
            var localId = await store.SaveIdentityAsync(identity);
            log.LogInformation("machine_info saved: {MachineId} (local_id={LocalId})", identity.MachineId, localId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SaveIdentityAsync failed — machine_info not written");
        }

        await Host.StartAsync();

        var state  = Host.Services.GetRequiredService<AgentState>();
        var window = Host.Services.GetRequiredService<MainWindow>();
        _tray = new TrayIconManager(window, state);

        // Always start minimized (tray only). User opens window via tray double-click.
        // --minimized flag is passed by both HKCU Run key and Task Scheduler.
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
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

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

            if (!string.Equals(existing, desired, StringComparison.OrdinalIgnoreCase))
                key.SetValue(AutoStartKey, desired);
        }
        catch { }
    }
}
