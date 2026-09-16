using System.IO;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeManage.AgentService.Data;
using ZeManage.AgentService.Logging;
using ZeManage.AgentService.Monitors;
using ZeManage.AgentService.Services;
using ZeManage.AgentService.Sync;

// Single-instance guard, own mutex name — independent of the reference Agent's
// "ZeManageAgent-SingleInstance" mutex, so both can run on the same machine at once. Mainly
// matters when this is launched as a console app for testing; the Windows SCM already guarantees
// only one instance of an installed service runs.
using var mutex = new Mutex(true, "ZeManageAgentService-SingleInstance", out var isFirst);
if (!isFirst)
{
    Console.WriteLine("ZeManage Agent Service is already running.");
    return;
}

var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ZeManageAgentService");
var logPath = Path.Combine(dataDir, "Logs", "agentservice.log");

var builder = Host.CreateApplicationBuilder(args);

// Windows Background Service support — when installed via `sc.exe create` / New-Service and
// started under the Service Control Manager, this makes the host behave as a real Windows
// service (responds to Stop/Pause, logs to the Application event log on failure to start,
// honors the SCM's start/stop timeouts). Running via `dotnet run` or the built exe directly
// still works as a normal console app — UseWindowsService() is a no-op outside an actual
// service context.
builder.Services.AddWindowsService(o => o.ServiceName = "ZeManageAgentService");

builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(logPath));
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddOptions<AgentServiceOptions>().Configure(opts =>
{
    if (string.IsNullOrWhiteSpace(opts.DatabasePath))
    {
        var dbDir = Path.Combine(dataDir, "Data");
        Directory.CreateDirectory(dbDir);
        opts.DatabasePath = Path.Combine(dbDir, "agentservice.db");
    }

    // appsettings.json next to the exe (own file, own "AgentService" section — never reads the
    // reference Agent's appsettings.json or its "Agent" section).
    var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    if (File.Exists(configPath))
    {
        new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: true)
            .Build()
            .GetSection("AgentService")
            .Bind(opts);
    }

    // Environment variables override the config file — own prefix, distinct from the reference
    // Agent's ZEMANAGE_* variables.
    var envUrl = Environment.GetEnvironmentVariable("ZEMANAGE_AGENTSVC_BACKEND_URL");
    var envKey = Environment.GetEnvironmentVariable("ZEMANAGE_AGENTSVC_LICENSE_KEY");
    if (!string.IsNullOrWhiteSpace(envUrl)) opts.BackendBaseUrl = envUrl;
    if (!string.IsNullOrWhiteSpace(envKey)) opts.LicenseKey = envKey;
});

builder.Services.AddSingleton<IdentityService>();
builder.Services.AddSingleton<AgentHealthState>();
builder.Services.AddSingleton<AgentSettings>();
builder.Services.AddSingleton<LocalStore>();
builder.Services.AddSingleton<TokenProvider>();

builder.Services.AddDbContextFactory<AgentServiceDbContext>((sp, options) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentServiceOptions>>().Value;
    options.UseSqlite($"Data Source={opts.DatabasePath}");
});

builder.Services.AddHttpClient("backend").ConfigurePrimaryHttpMessageHandler(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentServiceOptions>>().Value;
    return new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = o.AllowInsecureSsl
            ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            : null
    };
});
builder.Services.AddHttpClient("backend-auth").ConfigurePrimaryHttpMessageHandler(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentServiceOptions>>().Value;
    return new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = o.AllowInsecureSsl
            ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            : null
    };
});

builder.Services.AddHostedService<ProcessMonitor>();
builder.Services.AddHostedService<BrowserMonitor>();
builder.Services.AddHostedService<NetworkMonitor>();
builder.Services.AddHostedService<ScreenshotMonitor>();
builder.Services.AddHostedService<SyncService>();
builder.Services.AddHostedService<HealthLoggerService>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<LocalStore>();
    await store.EnsureCreatedAsync();

    var identity = scope.ServiceProvider.GetRequiredService<IdentityService>();
    var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var id = identity.Get();
        await store.SaveIdentityAsync(id);
        log.LogInformation("Machine identity captured and saved: {MachineId}", id.MachineId);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Failed to capture/save machine identity");
    }
}

await host.RunAsync();
