#if REVIT2027
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeManage.Agent.Core;
using ZeManage.Agent.Core.Data;
using BIManage.Common.Helpers;
using BIManageLogger = BIManage.Infrastructure.Logging.ILogger;

namespace BIManageRevit.BIManage.Revit.Applications;

/// <summary>
/// Manages the ZeManage Desktop Agent IHost lifecycle inside the Revit process.
/// Runs only on R27 (net10.0-windows). Keeps its own SQLite at %LocalAppData%\ZeManage\agent.db —
/// completely separate from BIManage's SQLite at %LocalAppData%\BIManageRevit\.
/// </summary>
internal sealed class ZeManageIntegration : IDisposable
{
    private IHost? _host;
    private readonly BIManageLogger? _logger;

    public ZeManageIntegration(BIManageLogger? logger) => _logger = logger;

    public void Start()
    {
        try
        {
            // Use AppConfigReader (reads BIManageRevit.dll.config, not Revit.exe.config).
            var backendUrl = AppConfigReader.Read("ZeManageAgent:BackendUrl")
                ?? "http://10.10.40.75:5000";
            var email    = AppConfigReader.Read("ZeManageAgent:Email")    ?? "";
            var password = AppConfigReader.Read("ZeManageAgent:Password") ?? "";

            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ApplicationName = "ZeManageAgent",
                // Revit has no console — suppress all default console/event-log providers.
                DisableDefaults = true,
            });

            // Route agent logs to VS debug output only (non-intrusive inside Revit).
            builder.Logging.AddDebug().SetMinimumLevel(LogLevel.Information);

            builder.Services.AddZeManageAgent(opts =>
            {
                opts.BackendBaseUrl  = backendUrl;
                opts.BackendEmail    = email;
                opts.BackendPassword = password;
            });

            _host = builder.Build();

            // Ensure both the agent DB schema and the BrowserActivities upgrade table exist
            // before any monitor IHostedService starts writing. Run synchronously (we're already
            // on a background OnApplicationInitialized path) with a short timeout guard.
            var store = _host.Services.GetRequiredService<LocalStore>();
            using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10)))
                store.EnsureCreatedAsync(cts.Token).GetAwaiter().GetResult();

            // Non-blocking start — OnApplicationInitialized must return promptly to Revit.
            _ = _host.StartAsync();

            _logger?.LogInfo("[ZeManageIntegration] Agent host started (background)");
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[ZeManageIntegration] Failed to start: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        if (_host == null) return;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            _host.StopAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"[ZeManageIntegration] Stop error: {ex.Message}");
        }
        finally
        {
            _host.Dispose();
            _host = null;
        }
    }
}
#endif
