using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.AgentService.Data;
using ZeManage.AgentService.Models;
using ZeManage.AgentService.Services;

namespace ZeManage.AgentService.Monitors;

/// <summary>Periodic network health snapshot (latency, loss, throughput, VPN detection) written to
/// SQLite. Track-only — no API call here.</summary>
public sealed class NetworkMonitor : BackgroundService
{
    private readonly LocalStore _store;
    private readonly AgentServiceOptions _opts;
    private readonly ILogger<NetworkMonitor> _log;

    private static readonly string[] PingTargets = { "8.8.8.8", "1.1.1.1", "www.microsoft.com" };

    public NetworkMonitor(LocalStore store, IOptions<AgentServiceOptions> opts, ILogger<NetworkMonitor> log)
    {
        _store = store;
        _opts = opts.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_opts.NetworkIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snap = await CollectAsync(stoppingToken);
                await _store.AddNetworkSnapshotAsync(snap, stoppingToken);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Network snapshot failed"); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<NetworkSnapshot> CollectAsync(CancellationToken ct)
    {
        var pingTask = PingAsync(ct);
        var throughputTask = MeasureThroughputAsync(ct);
        await Task.WhenAll(pingTask, throughputTask);

        var (latencyMs, lossPct) = pingTask.Result;
        var (downMbps, upMbps, adapterName, connType) = throughputTask.Result;
        var now = DateTime.UtcNow;

        var snap = new NetworkSnapshot
        {
            CapturedAt = now,
            DownloadMbps = Math.Round(downMbps, 2),
            UploadMbps = Math.Round(upMbps, 2),
            LatencyMs = Math.Round(latencyMs, 1),
            PacketLossPercent = Math.Round(lossPct, 1),
            VpnConnected = DetectVpn(),
            ActiveAdapter = adapterName,
            ConnectionType = connType,
            CreatedAt = now,
            UpdatedAt = now,
            Synced = false
        };
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
                if (r.Status == IPStatus.Success) { ok++; sum += r.RoundtripTime; }
            }
            catch { }
            if (ct.IsCancellationRequested) break;
        }
        var avg = ok > 0 ? sum / ok : 0;
        var loss = sent > 0 ? (sent - ok) / (double)sent * 100.0 : 0;
        return (avg, loss);
    }

    private static async Task<(double downloadMbps, double uploadMbps, string? adapterName, string connectionType)> MeasureThroughputAsync(CancellationToken ct)
    {
        try
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel);
            if (ni is null) return (0, 0, null, "Unknown");

            var s1 = ni.GetIPv4Statistics();
            await Task.Delay(1000, ct);
            var s2 = ni.GetIPv4Statistics();

            var rxBytes = Math.Max(0, s2.BytesReceived - s1.BytesReceived);
            var txBytes = Math.Max(0, s2.BytesSent - s1.BytesSent);
            var connType = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
            return (rxBytes * 8.0 / 1_000_000.0, txBytes * 8.0 / 1_000_000.0, ni.Description, connType);
        }
        catch { return (0, 0, null, "Unknown"); }
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
                if (n.Contains("vpn") || d.Contains("vpn") || d.Contains("tap-windows") || d.Contains("wintun") ||
                    d.Contains("openvpn") || d.Contains("wireguard") || d.Contains("cisco anyconnect") || d.Contains("fortinet"))
                    return true;
            }
        }
        catch { }
        return false;
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
