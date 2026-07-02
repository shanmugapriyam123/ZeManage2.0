using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Models;
using ZeManage.Agent.Core.Services;

namespace ZeManage.Agent.Core.Monitors;

public sealed class NetworkMonitor : BackgroundService
{
    private readonly LocalStore _store;
    private readonly IdentityService _identity;
    private readonly AgentOptions _opts;
    private readonly ILogger<NetworkMonitor> _log;
    private readonly AgentState _state;
    private readonly IHttpClientFactory _httpFactory;

    private static readonly string[] PingTargets =
    {
        "8.8.8.8", "1.1.1.1", "www.microsoft.com"
    };

    public NetworkMonitor(
        LocalStore store,
        IdentityService identity,
        IOptions<AgentOptions> opts,
        ILogger<NetworkMonitor> log,
        AgentState state,
        IHttpClientFactory httpFactory)
    {
        _store = store;
        _identity = identity;
        _opts = opts.Value;
        _log = log;
        _state = state;
        _httpFactory = httpFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_opts.NetworkIntervalSeconds);
        var id = _identity.Get();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snap = await CollectAsync(id.MachineName, stoppingToken);
                await _store.AddNetworkSnapshotAsync(snap, stoppingToken);
                _state.LatestNetwork = snap;
                _state.NotifyChanged();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Network snapshot failed");
            }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<NetworkSnapshot> CollectAsync(string machineName, CancellationToken ct)
    {
        var (latencyMs, lossPct) = await PingAsync(ct);
        var (downMbps, activeAdapter) = ReadAdapterStats();
        var vpn = DetectVpn();
        var snap = new NetworkSnapshot
        {
            MachineName = machineName,
            CapturedAt = DateTime.UtcNow,
            DownloadMbps = Math.Round(downMbps, 2),
            UploadMbps = 0,
            LatencyMs = Math.Round(latencyMs, 1),
            PacketLossPercent = Math.Round(lossPct, 1),
            VpnConnected = vpn,
            ActiveAdapter = activeAdapter
        };

        if (_opts.EnableSpeedTest)
        {
            try
            {
                var mbps = await SimpleDownloadProbeAsync(ct);
                if (mbps > 0) snap.DownloadMbps = Math.Round(mbps, 2);
            }
            catch (Exception ex) { _log.LogDebug(ex, "Speed probe failed"); }
        }

        snap.HealthScore = ComputeHealthScore(snap);
        return snap;
    }

    private static async Task<(double avgMs, double lossPct)> PingAsync(CancellationToken ct)
    {
        using var ping = new Ping();
        int sent = 0, ok = 0;
        double sum = 0;
        foreach (var t in PingTargets)
        {
            sent++;
            try
            {
                var r = await ping.SendPingAsync(t, TimeSpan.FromSeconds(2));
                if (r.Status == IPStatus.Success)
                {
                    ok++;
                    sum += r.RoundtripTime;
                }
            }
            catch { }
            if (ct.IsCancellationRequested) break;
        }
        var avg = ok > 0 ? sum / ok : 0;
        var loss = sent > 0 ? (sent - ok) / (double)sent * 100.0 : 0;
        return (avg, loss);
    }

    private static (double mbps, string? name) ReadAdapterStats()
    {
        try
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel);
            if (ni is null) return (0, null);
            var speed = ni.Speed / 1_000_000.0;
            return (speed, ni.Name);
        }
        catch { return (0, null); }
    }

    private static bool DetectVpn()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var n = ni.Name.ToLowerInvariant();
                var d = ni.Description.ToLowerInvariant();
                if (n.Contains("vpn") || d.Contains("vpn") ||
                    d.Contains("tap-windows") || d.Contains("wintun") ||
                    d.Contains("openvpn") || d.Contains("wireguard") ||
                    d.Contains("cisco anyconnect") || d.Contains("fortinet"))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private async Task<double> SimpleDownloadProbeAsync(CancellationToken ct)
    {
        const string url = "https://speed.cloudflare.com/__down?bytes=1000000";
        var client = _httpFactory.CreateClient("speedtest");
        client.Timeout = TimeSpan.FromSeconds(10);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bytes = await client.GetByteArrayAsync(url, ct);
        sw.Stop();
        var mbps = bytes.Length * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000.0;
        return mbps;
    }

    private static int ComputeHealthScore(NetworkSnapshot s)
    {
        var score = 100;
        if (s.LatencyMs > 50) score -= 10;
        if (s.LatencyMs > 100) score -= 15;
        if (s.LatencyMs > 200) score -= 25;
        if (s.PacketLossPercent > 1) score -= 10;
        if (s.PacketLossPercent > 5) score -= 20;
        if (s.LatencyMs == 0 && s.PacketLossPercent >= 100) score = 0;
        return Math.Max(0, Math.Min(100, score));
    }
}
