using System.Diagnostics;
using System.Threading;
using Microsoft.Data.Sqlite;
using ZeManage.Agent.Core.Services;

// Single-instance guard — mirrors ZeManage.Agent's own pattern (App.xaml.cs).
using var mutex = new Mutex(true, "ZeManageAgentWatchdog-SingleInstance", out var isFirst);
if (!isFirst) return;

// Same tamper-resistance as the agent itself — otherwise a user could defeat the whole
// protection by just killing the watchdog first, then the agent.
ProcessProtection.RestrictTerminationToAdmins();

// Graceful-exit escape hatch for the installer/updater — mirrors ZeManage.Agent's own
// ExitEventName handling (see App.xaml.cs). PROCESS_TERMINATE is denied to non-admins, including
// a non-elevated installer running in per-user mode, so taskkill alone can't reliably stop this
// process to replace its own exe during an upgrade. Self-exiting needs no handle rights at all.
var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "ZeManageAgentWatchdog-RequestExit");
new Thread(() =>
{
    exitEvent.WaitOne();
    Environment.Exit(0);
}) { IsBackground = true }.Start();

var exeDir       = AppContext.BaseDirectory;
var agentExePath = Path.Combine(exeDir, "ZeManage.Agent.exe");
var installingMarker = Path.Combine(exeDir, ".installing");

// Same DB path convention as AgentBootstrap.AddZeManageAgent's default (ZeManage.Agent.Core).
var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "BIManageRevit", "Logs", "agent.db");

const int pollSeconds = 5;

// A hung (deadlocked UI thread) agent still satisfies "process exists" — the plain existence
// check above never restarts it. Track consecutive not-responding polls so a single transient
// blip (e.g. a GC pause) doesn't trigger a false recovery; two in a row (~10s of unresponsiveness)
// does. Recovery is signal-only — this watchdog runs as the same standard user as the agent, so
// ProcessProtection's DACL denies it PROCESS_TERMINATE too (same as it denies Task Manager); the
// exit-request event's listener thread is a separate background thread from the stuck UI thread,
// so it can often still get through and self-terminate the process even while the UI is wedged.
// If it can't (the hang is severe enough to also stop the threadpool from servicing that thread),
// this is a known, accepted limitation of "only an admin can stop this process" — there is no
// force-kill fallback available to a non-elevated watchdog by design.
var consecutiveHangPolls = 0;

while (true)
{
    try
    {
        var processes = Process.GetProcessesByName("ZeManage.Agent");
        var alreadyRunning = processes.Length > 0;
        var installInProgress = File.Exists(installingMarker);

        if (alreadyRunning)
        {
            var hung = processes.All(p => { try { return !p.Responding; } catch { return false; } });
            if (hung)
            {
                consecutiveHangPolls++;
                if (consecutiveHangPolls >= 2)
                {
                    SignalGracefulExit("ZeManageAgent-RequestExit");
                    consecutiveHangPolls = 0;
                }
            }
            else
            {
                consecutiveHangPolls = 0;
            }
        }
        else
        {
            consecutiveHangPolls = 0;
        }

        if (!alreadyRunning && !installInProgress && File.Exists(agentExePath) && IsCaptureEnabled(dbPath))
        {
            Process.Start(new ProcessStartInfo(agentExePath, "--minimized")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = exeDir
            });
        }
    }
    catch
    {
        // Best-effort — never let a transient failure (locked DB, exe temporarily missing during
        // an upgrade, etc.) kill the watchdog's own loop.
    }

    await Task.Delay(TimeSpan.FromSeconds(pollSeconds));
}

static void SignalGracefulExit(string name)
{
    try
    {
        using var evt = EventWaitHandle.OpenExisting(name);
        evt.Set();
    }
    catch
    {
        // Event doesn't exist (agent build predates this feature) — nothing more this watchdog
        // can do without elevation.
    }
}

// Respects the same admin deactivate/reactivate kill-switch the agent itself honors
// (AgentState.IsCaptureEnabled / machine_info.is_capture_enabled) — a deactivated employee's
// agent must stay down until reactivated, not get relaunched every 5 seconds by the watchdog.
// Defaults to "enabled" (restart) when the DB or row doesn't exist yet, matching the agent's own
// fail-closed default for a fresh install with no confirmed state.
static bool IsCaptureEnabled(string dbPath)
{
    if (!File.Exists(dbPath)) return true;
    try
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_capture_enabled FROM machine_info LIMIT 1";
        var result = cmd.ExecuteScalar();
        return result is null || Convert.ToInt64(result) != 0;
    }
    catch
    {
        return true;
    }
}
