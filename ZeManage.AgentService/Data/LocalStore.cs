using Microsoft.EntityFrameworkCore;
using ZeManage.AgentService.Models;

namespace ZeManage.AgentService.Data;

/// <summary>All local SQLite reads/writes for the service. Monitors call the Add*/Update* methods
/// only — they never talk to the API. SyncService is the only consumer of the GetUnsynced*/
/// MarkSynced* methods, which form the offline sync queue: any row written here with Synced=false
/// stays queued until a sync cycle successfully POSTs it, so an API outage just means the queue
/// grows instead of losing data.</summary>
public sealed class LocalStore
{
    private readonly IDbContextFactory<AgentServiceDbContext> _factory;

    public LocalStore(IDbContextFactory<AgentServiceDbContext> factory) => _factory = factory;

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
    }

    public async Task<long> SaveIdentityAsync(AgentIdentity id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.MachineInfos.FirstOrDefaultAsync(x => x.MachineId == id.MachineId, ct);
        var now = DateTime.UtcNow;

        if (existing is null)
        {
            db.MachineInfos.Add(new MachineInfo
            {
                MachineId = id.MachineId,
                Sid = id.WindowsSid,
                UserName = id.UserName,
                HostName = id.MachineName,
                CapturedAt = now,
                CpuModel = id.CpuModel,
                RamGb = id.TotalRamGB,
                RamUsableGb = id.UsableRamGB,
                GpuModel = id.GpuModel,
                StorageTotalGb = id.StorageTotalGB,
                StorageUsedGb = id.StorageUsedGB,
                StorageType = id.StorageType,
                RamType = id.RamType,
                MacAddress = id.MacAddress,
                IpAddress = id.IpAddress,
                SerialNumber = id.SerialNumber,
                DeviceId = id.DeviceId,
                SystemType = id.SystemType,
                OsVersion = id.OsVersion,
                WindowsEdition = id.WindowsEdition,
                WindowsVersion = id.WindowsVersion,
                OsBuild = id.OsBuild,
                BiosVersion = id.BiosVersion,
                MotherboardModel = id.MotherboardModel,
                TimezoneId = id.TimeZoneId
            });
        }
        else
        {
            existing.Sid = id.WindowsSid;
            existing.UserName = id.UserName;
            existing.HostName = id.MachineName;
            existing.CpuModel = id.CpuModel;
            existing.RamGb = id.TotalRamGB;
            existing.RamUsableGb = id.UsableRamGB;
            existing.GpuModel = id.GpuModel;
            existing.StorageTotalGb = id.StorageTotalGB;
            existing.StorageUsedGb = id.StorageUsedGB;
            existing.StorageType = id.StorageType;
            existing.RamType = id.RamType;
            existing.MacAddress = id.MacAddress;
            existing.IpAddress = id.IpAddress;
        }

        await db.SaveChangesAsync(ct);
        return 0;
    }

    public async Task AddApplicationUsageAsync(ApplicationUsage u, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.ApplicationUsages.Add(u);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ApplicationUsage>> GetRunningApplicationsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.ApplicationUsages.Where(x => x.Status == "Running").ToListAsync(ct);
    }

    public async Task CloseApplicationUsageAsync(ApplicationUsage u, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.ApplicationUsages.FirstOrDefaultAsync(x => x.LocalId == u.LocalId, ct);
        if (row is null) return;
        row.EndTime = u.EndTime;
        row.ActiveSeconds = u.ActiveSeconds;
        row.FocusSeconds = u.FocusSeconds;
        row.IdleSeconds = u.IdleSeconds;
        row.Status = "Closed";
        row.UpdatedAt = u.UpdatedAt;
        row.Synced = false;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateRunningStatsAsync(string localId, long active, long focus, long idle, DateTime updatedAt, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.ApplicationUsages.FirstOrDefaultAsync(x => x.LocalId == localId && x.Status == "Running", ct);
        if (row is null) return;
        row.ActiveSeconds = active;
        row.FocusSeconds = focus;
        row.IdleSeconds = idle;
        row.UpdatedAt = updatedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateSessionIdAsync(string localId, string sessionId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.ApplicationUsages.FirstOrDefaultAsync(x => x.LocalId == localId, ct);
        if (row is null) return;
        row.SessionId = sessionId;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ApplicationUsage>> GetOpenWithoutSessionIdAsync(int take, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.ApplicationUsages.Where(x => x.Status == "Running" && x.SessionId == null).Take(take).ToListAsync(ct);
    }

    public async Task<List<ApplicationUsage>> GetUnsyncedClosedApplicationsAsync(int take, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.ApplicationUsages.Where(x => !x.Synced && x.Status == "Closed").Take(take).ToListAsync(ct);
    }

    public async Task AddActivityIntervalAsync(ActivityInterval iv, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.ActivityIntervals.Add(iv);
        await db.SaveChangesAsync(ct);
    }

    public async Task CloseActivityIntervalAsync(string localId, DateTime endTime, long active, long focus, long idle, DateTime updatedAt, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.ActivityIntervals.FirstOrDefaultAsync(x => x.LocalId == localId, ct);
        if (row is null) return;
        row.EndTime = endTime;
        row.ActiveSeconds = active;
        row.FocusSeconds = focus;
        row.IdleSeconds = idle;
        row.UpdatedAt = updatedAt;
        row.Synced = false;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateOpenActivityIntervalStatsAsync(string localId, long active, long focus, long idle, DateTime updatedAt, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.ActivityIntervals.FirstOrDefaultAsync(x => x.LocalId == localId && x.EndTime == null, ct);
        if (row is null) return;
        row.ActiveSeconds = active;
        row.FocusSeconds = focus;
        row.IdleSeconds = idle;
        row.UpdatedAt = updatedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ActivityInterval>> GetOpenActivityIntervalsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.ActivityIntervals.Where(x => x.EndTime == null).ToListAsync(ct);
    }

    public async Task<List<ActivityInterval>> GetUnsyncedClosedActivityIntervalsAsync(int take, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.ActivityIntervals.Where(x => !x.Synced && x.EndTime != null).Take(take).ToListAsync(ct);
    }

    public async Task AddNetworkSnapshotAsync(NetworkSnapshot n, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.NetworkSnapshots.Add(n);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<NetworkSnapshot>> GetUnsyncedNetworkAsync(int take, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.NetworkSnapshots.Where(x => !x.Synced).OrderByDescending(x => x.CapturedAt).Take(take).ToListAsync(ct);
    }

    public async Task AddBrowserActivityAsync(BrowserActivity b, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.BrowserActivities.Add(b);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<BrowserActivity>> GetUnsyncedBrowserActivitiesAsync(int take, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.BrowserActivities.Where(x => !x.Synced).Take(take).ToListAsync(ct);
    }

    public async Task AddScreenshotAsync(Screenshot s, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Screenshots.Add(s);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<Screenshot>> GetUnsyncedScreenshotsAsync(int take, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Screenshots.Where(x => !x.Synced).OrderBy(x => x.CapturedAt).Take(take).ToListAsync(ct);
    }

    public async Task MarkSyncedAsync<T>(IEnumerable<T> rows, CancellationToken ct = default) where T : class
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        foreach (var r in rows)
        {
            typeof(T).GetProperty("Synced")?.SetValue(r, true);
            db.Update(r);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> CountUnsyncedAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.ApplicationUsages.CountAsync(x => !x.Synced, ct)
             + await db.NetworkSnapshots.CountAsync(x => !x.Synced, ct)
             + await db.BrowserActivities.CountAsync(x => !x.Synced, ct)
             + await db.Screenshots.CountAsync(x => !x.Synced, ct)
             + await db.ActivityIntervals.CountAsync(x => !x.Synced, ct);
    }
}
