using System.Diagnostics;
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
using ZeManage.Agent.Core.Sync;

namespace ZeManage.Agent;

public partial class App : Application
{
    private const string AutoStartKey  = "ZeManageAgent";
    private const string AutoStartPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string MutexName     = "ZeManageAgent-SingleInstance";
    private const string ShowEventName = "ZeManageAgent-ShowWindow";
    private const string ExitEventName = "ZeManageAgent-RequestExit";

    public static IHost? Host { get; private set; }
    private TrayIconManager? _tray;
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _exitEvent;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Prevent duplicate instances — if already running, signal it to show window instead
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, out bool isFirst);
        if (!isFirst)
        {
            _showEvent.Set();   // wake the running instance so it shows its window
            _mutex.Dispose();
            _showEvent.Dispose();
            Shutdown();
            return;
        }

        // Deny PROCESS_TERMINATE to non-admins so a standard user's Task Manager "End Task" fails
        // with Access Denied — only an elevated admin can stop this process. See ProcessProtection.
        ProcessProtection.RestrictTerminationToAdmins();

        // Graceful-exit escape hatch for the installer/updater: since PROCESS_TERMINATE is denied
        // to non-admins (including a non-elevated installer run in per-user mode), taskkill alone
        // can no longer reliably stop this process to replace its files during an upgrade. Self-
        // exiting via Shutdown() needs no handle rights at all, so the installer signals this event
        // first and only falls back to taskkill (which only works when it's itself elevated) if
        // this process doesn't exit in time. See ZeManageInstaller.iss.
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        Task.Run(() =>
        {
            if (_exitEvent?.WaitOne() == true)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Host?.Services.GetService<ILogger<App>>()?.LogInformation(
                        "Graceful exit requested externally (installer/updater) — shutting down.");
                    Shutdown();
                }));
            }
        });

        // Belt-and-suspenders: keep HKCU Run key current (installer is primary, this is backup)
        RegisterAutoStart();

        // Ensure the watchdog companion is running — it relaunches this agent if it's ever
        // terminated (crash, or an elevated admin's End Task) while the employee is still active.
        LaunchWatchdogIfNotRunning();

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BIManageRevit", "Logs", "agent.log");

        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(lb =>
            {
                lb.ClearProviders();
                lb.AddDebug();
                lb.AddProvider(new FileLoggerProvider(logPath));
                // EF Core's own internal diagnostics (connection open/close, every SaveChanges,
                // every command execution) log at Debug and are extremely chatty — combined with
                // FileLoggerProvider's AutoFlush=true, that's a synchronous disk write per line,
                // often several per second. That I/O volume can starve whatever thread is waiting
                // on the shared log lock (including the UI thread) long enough to trip Windows'
                // hang detector, making the window look "Not Responding" even though the process
                // is genuinely still working underneath — confirmed live: a hung-looking instance
                // was mid-flight on real HTTP calls (200/201 responses) in this exact log.
                // Information keeps the agent's own meaningful logs, drops the EF Core firehose.
                lb.SetMinimumLevel(LogLevel.Information);
                // Even at Information, two built-in categories are still extremely chatty:
                // EF Core logs "Executed DbCommand" for every single query (this app runs many
                // small queries per tick across 4+ monitors), and HttpClient's own handler
                // logs 4 lines per HTTP call (start/sending/received/end) — this app makes an
                // HTTP call roughly every few seconds. Both are still synchronous, AutoFlush=true
                // disk writes through the same shared log lock as everything else. Confirmed live
                // that reducing global Debug->Information alone did not stop the periodic
                // "Not Responding" episodes — dropping these two specific firehoses to Warning
                // removes the large majority of remaining log volume while keeping every
                // WARN/ERROR from them (failed queries, failed HTTP calls) fully visible.
                lb.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
                lb.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddZeManageAgent();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        var store = Host.Services.GetRequiredService<LocalStore>();
        await store.EnsureCreatedAsync();

        // Get() runs a chain of WMI queries (CPU/RAM/GPU/storage/BIOS/motherboard/storage-type/
        // RAM-type) that can take several seconds on first call — especially the
        // root\Microsoft\Windows\Storage provider used for storage type. Running it inline here
        // would block this method's caller, the UI thread's message pump, long enough for Windows
        // to flag the process "Not Responding". Task.Run moves it off the UI thread so the pump
        // keeps servicing messages while the WMI calls run in the background.
        var identity = await Task.Run(() => Host.Services.GetRequiredService<IdentityService>().Get());
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

        // Load the last confirmed admin active/inactive state before any monitor's ExecuteAsync
        // runs (Host.StartAsync below is what starts them) — fail-closed: if this machine was
        // last confirmed deactivated, it stays deactivated from the very first tick instead of
        // briefly capturing before TokenProvider's first register/validate call corrects it.
        var earlyState = Host.Services.GetRequiredService<AgentState>();
        try
        {
            earlyState.IsCaptureEnabled = await store.GetCaptureEnabledAsync();
            log.LogInformation("Capture state loaded from cache: {State}", earlyState.IsCaptureEnabled ? "enabled" : "disabled");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to load cached capture state — defaulting to enabled");
        }

        // Restore the last successfully-fetched group settings (break window, screenshot
        // schedule/interval, idle threshold) so a transient empty /my-group response — or simply
        // this process restarting — doesn't run blank until the next live fetch succeeds.
        try
        {
            await Host.Services.GetRequiredService<SyncService>().ApplyPersistedGroupSettingsAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to load cached group settings");
        }

        // Admin deactivated this employee before this launch — per explicit decision, the WFH
        // Agent process itself must stop on deactivate (unlike the Revit plugin, which stays
        // open and just pauses capture, since force-closing Revit would destroy unsaved work).
        // Exit immediately rather than spinning up monitors/SignalR just to shut them down —
        // reactivation requires manually relaunching the agent, since a dead process can't
        // receive a live SignalR push.
        //
        // BUT the cache alone is stale-by-construction: it's only ever written by a successful
        // server response, and every write path that could correct it (TokenProvider,
        // AgentHubConnection) only runs AFTER Host.StartAsync — which this exact branch never
        // reaches. Without the live check below, a deactivated agent that exits here can NEVER
        // start again, even after the admin reactivates it, because it keeps reading the same
        // stale "disabled" cache on every relaunch. Do one live check ONLY in this branch (not
        // on every launch — a normal active launch shouldn't pay a network round-trip just to
        // confirm what the cache already correctly says) so a relaunch after reactivation
        // actually picks it up. If the server is unreachable, GetAsync leaves the cached value
        // as-is — that's the real fail-closed behavior for a genuine offline gap.
        if (!earlyState.IsCaptureEnabled)
        {
            try
            {
                var tokenProvider = Host.Services.GetRequiredService<ZeManage.Agent.Core.Sync.TokenProvider>();
                await tokenProvider.GetAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Pre-flight reactivation check failed — using cached capture state");
            }
        }

        if (!earlyState.IsCaptureEnabled)
        {
            log.LogWarning("Employee is deactivated — agent will not start. Relaunch after reactivation.");
            Shutdown();
            return;
        }

        // Task.Run, not a plain await — Host.StartAsync() kicks off every BackgroundService's
        // ExecuteAsync(), and OnStartup itself runs on the WPF UI thread. Without this, each
        // hosted service's internal `await`s (Task.Delay, HTTP calls, etc.) capture the UI
        // thread's DispatcherSynchronizationContext and resume their continuations back on it
        // instead of a thread-pool thread — confirmed via a live memory dump showing the UI
        // thread itself stuck executing HardwareMonitor's GPU-percent loop, which froze the
        // entire message pump (Windows "Not Responding") for the loop's whole duration, with
        // every OTHER monitor's continuations queued up behind it on the same wedged dispatcher.
        // Task.Run has no ambient SynchronizationContext, so everything started from inside it
        // resumes on the thread pool as intended — a single blocking call in any one monitor can
        // no longer freeze the UI or any other monitor.
        await Task.Run(() => Host.StartAsync());

        var state  = Host.Services.GetRequiredService<AgentState>();
        var window = Host.Services.GetRequiredService<MainWindow>();
        _tray = new TrayIconManager(window, state);

        // If the employee is deactivated WHILE the agent is running (SignalR push or the next
        // TokenProvider register/validate/refresh poll confirms it), stop the process entirely
        // rather than just pausing capture in place — same reasoning as the startup check above.
        state.Changed += () =>
        {
            if (!state.IsCaptureEnabled)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    log.LogWarning("Employee deactivated while running — shutting down agent. Relaunch after reactivation.");
                    Shutdown();
                }));
            }
        };

        // Show window in taskbar on every startup.
        // --minimized flag (from installer / HKCU Run) starts it minimized but still visible in taskbar.
        window.Show();
        if (e.Args.Contains("--minimized"))
        {
            window.WindowState = WindowState.Minimized;
        }
        else
        {
            // Force this to actually reach the foreground — a freshly-launched process isn't the
            // foreground app by default (Windows' focus-stealing prevention), so a plain Show()
            // can leave the window opened-but-behind whatever already had focus.
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        }

        // Background thread: watch for show-window signals from subsequent launches
        // (e.g. user clicks Start Menu shortcut while agent is already running in tray)
        Task.Run(() =>
        {
            while (_showEvent?.WaitOne() == true)
            {
                window.Dispatcher.BeginInvoke(() =>
                {
                    window.Show();
                    window.WindowState = WindowState.Normal;
                    window.Activate();
                    window.Topmost = true;
                    window.Topmost = false;
                    window.Focus();
                });
            }
        });
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
        _showEvent?.Dispose();
        _exitEvent?.Dispose();
        base.OnExit(e);
    }

    // Starts ZeManage.Agent.Watchdog.exe (deployed alongside this exe) if it isn't already running.
    // The watchdog polls for this process and relaunches it if it disappears — the mirror image of
    // this agent's own ProcessProtection hardening: killing just this exe (as an elevated admin,
    // the only account that even CAN) still isn't enough to keep the agent down, since the
    // watchdog brings it back within a few seconds. Best-effort: an install that hasn't shipped
    // the watchdog exe yet (or is mid-install — see the ".installing" marker check inside the
    // watchdog itself) simply runs without this extra layer.
    private static void LaunchWatchdogIfNotRunning()
    {
        try
        {
            if (Process.GetProcessesByName("ZeManage.Agent.Watchdog").Length > 0) return;

            var exeDir = AppContext.BaseDirectory;
            var watchdogExe = Path.Combine(exeDir, "ZeManage.Agent.Watchdog.exe");
            if (!File.Exists(watchdogExe)) return;

            Process.Start(new ProcessStartInfo(watchdogExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = exeDir
            });
        }
        catch { }
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
