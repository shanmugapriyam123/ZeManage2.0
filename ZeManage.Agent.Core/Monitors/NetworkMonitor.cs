using System.Net.NetworkInformation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.Agent.Core.Data;
using ZeManage.Agent.Core.Models;
using ZeManage.Agent.Core.Services;
using ZeManage.Agent.Core.Sync;

namespace ZeManage.Agent.Core.Monitors;

public sealed class NetworkMonitor : BackgroundService
{
    private readonly LocalStore _store;
    private readonly IdentityService _identity;
    private readonly AgentOptions _opts;
    private readonly ILogger<NetworkMonitor> _log;
    private readonly AgentState _state;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AgentHubConnection _hub;

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
        IHttpClientFactory httpFactory,
        AgentHubConnection hub)
    {
        _store = store;
        _identity = identity;
        _opts = opts.Value;
        _log = log;
        _state = state;
        _httpFactory = httpFactory;
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_opts.NetworkIntervalSeconds);
        var id = _identity.Get();
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_state.IsCaptureEnabled)
            {
                try { await Task.Delay(interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                var snap = await CollectAsync(stoppingToken);
                await _store.AddNetworkSnapshotAsync(snap, stoppingToken);
                _state.LatestNetwork = snap;
                _state.NotifyChanged();

                await _hub.TrySendEventAsync("ReportNetworkSnapshot", new
                {
                    machineId         = id.MachineId,
                    capturedAt        = snap.CapturedAt,
                    downloadMbps      = snap.DownloadMbps,
                    uploadMbps        = snap.UploadMbps,
                    latencyMs         = snap.LatencyMs,
                    packetLossPercent = snap.PacketLossPercent,
                    vpnConnected      = snap.VpnConnected,
                    activeAdapter     = snap.ActiveAdapter,
                    healthScore       = snap.HealthScore
                }, stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Network snapshot failed");
            }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<NetworkSnapshot> CollectAsync(CancellationToken ct)
    {
        // Run ping and throughput measurement concurrently (throughput needs 1s sample window)
        var pingTask       = PingAsync(ct);
        var throughputTask = MeasureThroughputAsync(ct);

        await Task.WhenAll(pingTask, throughputTask);

        var (latencyMs, lossPct)                           = pingTask.Result;
        var (downMbps, upMbps, adapterName, connType) = throughputTask.Result;
        var vpn = DetectVpn();
        var now = DateTime.UtcNow;
        var snap = new NetworkSnapshot
        {
            CapturedAt        = now,
            DownloadMbps      = Math.Round(downMbps, 2),
            UploadMbps        = Math.Round(upMbps, 2),
            LatencyMs         = Math.Round(latencyMs, 1),
            PacketLossPercent = Math.Round(lossPct, 1),
            VpnConnected      = vpn,
            ActiveAdapter     = adapterName,
            ConnectionType    = connType,
            CreatedAt         = now,
            UpdatedAt         = now
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

            var downloadMbps = rxBytes * 8.0 / 1_000_000.0;
            var uploadMbps   = txBytes * 8.0 / 1_000_000.0;

            var connType = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";

            return (downloadMbps, uploadMbps, ni.Description, connType);
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
