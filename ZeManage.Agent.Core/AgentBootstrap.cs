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
        services.AddSingleton<Sync.AttendanceApiService>();
        services.AddSingleton<Services.AppClassificationService>();

        services.AddDbContextFactory<AgentDbContext>((sp, options) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
            options.UseSqlite($"Data Source={opts.DatabasePath}");
        });

        // SocketsHttpHandler (not HttpClientHandler) specifically so PooledConnectionLifetime is
        // available. Confirmed live: requests were failing with SocketException(995, "operation
        // aborted") on plain HTTP calls (reproduced even with SignalR disabled entirely, via the
        // "backend-agent" fallback client) — the classic symptom of a proxy/gateway/load-balancer
        // between this machine and the backend silently closing idle keep-alive connections that
        // this process's connection pool doesn't know are dead yet, so the next request reused on
        // that pooled connection gets aborted mid-flight instead of cleanly reconnecting. Forcing
        // every pooled connection to retire after 30s (well under any typical proxy idle timeout,
        // which is usually 60-120s) makes the client always establish a fresh connection instead
        // of gambling on a possibly-already-dead pooled one.
        static SocketsHttpHandler MakeHandler(AgentOptions o) => new()
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            SslOptions = o.AllowInsecureSsl
                ? new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }
                : new System.Net.Security.SslClientAuthenticationOptions()
        };

        services.AddHttpClient("backend").ConfigurePrimaryHttpMessageHandler(sp =>
            MakeHandler(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value));
        services.AddHttpClient("backend-auth").ConfigurePrimaryHttpMessageHandler(sp =>
            MakeHandler(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value));
        // "backend-agent" — HTTP fallback channel used by AgentHubConnection
        // when SignalR is Reconnecting/Disconnected. Same SSL policy as the
        // other backend clients so a self-signed cert on the staging backend
        // doesn't silently sink the fallback POST.
        services.AddHttpClient("backend-agent").ConfigurePrimaryHttpMessageHandler(sp =>
            MakeHandler(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value));
        services.AddHttpClient("speedtest");
        services.AddHttpClient();

        services.AddSingleton<ScreenshotMonitor>();
        services.AddSingleton<ProcessMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<ProcessMonitor>());
        services.AddHostedService<HardwareMonitor>();
        services.AddHostedService<NetworkMonitor>();
        services.AddHostedService<BrowserMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<ScreenshotMonitor>());
        services.AddSingleton<AgentHubConnection>();
        services.AddHostedService(sp => sp.GetRequiredService<AgentHubConnection>());
        services.AddSingleton<SyncService>();
        services.AddHostedService(sp => sp.GetRequiredService<SyncService>());

        return services;
    }
}
