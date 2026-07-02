using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Monitors;
using ZeManage.Agent.Core.Services;
using ZeManage.Agent.Core.Sync;

namespace ZeManage.Agent.Core;

public static class AgentBootstrap
{
    public static IServiceCollection AddZeManageAgent(this IServiceCollection services, Action<AgentOptions>? configure = null)
    {
        services.AddOptions<AgentOptions>().Configure(opts =>
        {
            if (string.IsNullOrWhiteSpace(opts.DatabasePath))
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BIManageRevit", "Logs");
                Directory.CreateDirectory(dir);
                opts.DatabasePath = Path.Combine(dir, "agent.db");
            }

            // Config file written by the installer next to the exe. Loaded from the
            // exe's own directory (AppContext.BaseDirectory), not the working directory,
            // so it resolves correctly when launched from the autostart Run key.
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(configPath))
            {
                new ConfigurationBuilder()
                    .AddJsonFile(configPath, optional: true)
                    .Build()
                    .GetSection("Agent")
                    .Bind(opts);
            }

            // Environment variables override the config file.
            var envUrl   = Environment.GetEnvironmentVariable("ZEMANAGE_BACKEND_URL");
            var envEmail = Environment.GetEnvironmentVariable("ZEMANAGE_AGENT_EMAIL");
            var envPwd   = Environment.GetEnvironmentVariable("ZEMANAGE_AGENT_PASSWORD");
            if (!string.IsNullOrWhiteSpace(envUrl))   opts.BackendBaseUrl  = envUrl;
            if (!string.IsNullOrWhiteSpace(envEmail)) opts.BackendEmail    = envEmail;
            if (!string.IsNullOrWhiteSpace(envPwd))   opts.BackendPassword = envPwd;

            configure?.Invoke(opts);
        });

        services.AddSingleton<IdentityService>();
        services.AddSingleton<AgentState>();
        services.AddSingleton<LocalStore>();
        services.AddSingleton<Sync.TokenProvider>();

        services.AddDbContextFactory<AgentDbContext>((sp, options) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
            options.UseSqlite($"Data Source={opts.DatabasePath}");
        });

        services.AddHttpClient("backend").ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
            return new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = o.AllowInsecureSsl
                    ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    : null
            };
        });
        services.AddHttpClient("backend-auth").ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
            return new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = o.AllowInsecureSsl
                    ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    : null
            };
        });
        services.AddHttpClient("speedtest");
        services.AddHttpClient();

        services.AddSingleton<ScreenshotMonitor>();
        services.AddHostedService<AttendanceMonitor>();
        services.AddHostedService<ProcessMonitor>();
        services.AddHostedService<HardwareMonitor>();
        services.AddHostedService<NetworkMonitor>();
        services.AddHostedService<BrowserMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<ScreenshotMonitor>());
        services.AddHostedService<AgentHubConnection>();
        services.AddHostedService<SyncService>();

        return services;
    }
}
