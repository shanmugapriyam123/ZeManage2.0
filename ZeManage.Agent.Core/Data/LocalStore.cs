using Microsoft.EntityFrameworkCore;
using ZeManage.Agent.Core.Models;

namespace ZeManage.Agent.Core.Data;

public sealed class LocalStore
{
    private readonly IDbContextFactory<AgentDbContext> _factory;

    public LocalStore(IDbContextFactory<AgentDbContext> factory)
    {
        _factory = factory;
    }

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);

        // Safe upgrade: create any new tables that may not exist in older DBs
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "BrowserActivities" (
                "Id"              INTEGER NOT NULL CONSTRAINT "PK_BrowserActivities" PRIMARY KEY AUTOINCREMENT,
                "UserName"        TEXT NOT NULL,
                "MachineName"     TEXT NOT NULL,
                "Browser"         TEXT NOT NULL,
                "PageTitle"       TEXT NOT NULL,
                "StartTime"       TEXT NOT NULL,
                "EndTime"         TEXT NOT NULL,
                "DurationSeconds" INTEGER NOT NULL,
                "Synced"          INTEGER NOT NULL
            );
            """, ct);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_BrowserActivities_Synced" ON "BrowserActivities" ("Synced");""", ct);

        // Upgrade: add WindowsSid to existing tables (SQLite ignores duplicate column errors)
        foreach (var sql in new[]
        {
            """ALTER TABLE "ApplicationUsages" ADD COLUMN "WindowsSid" TEXT NULL;""",
            """ALTER TABLE "BrowserActivities" ADD COLUMN "WindowsSid" TEXT NULL;""",
            """ALTER TABLE "BimEvents"          ADD COLUMN "WindowsSid" TEXT NULL;""",
        })
        {
            try { await db.Database.ExecuteSqlRawAsync(sql, ct); } catch { /* column already exists */ }
        }

        // Upgrade: Screenshots table (new in V2)
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "Screenshots" (
                    "Id"            INTEGER NOT NULL CONSTRAINT "PK_Screenshots" PRIMARY KEY AUTOINCREMENT,
                    "UserName"      TEXT NOT NULL,
                    "MachineName"   TEXT NOT NULL,
                    "WindowsSid"    TEXT NULL,
                    "CapturedAt"    TEXT NOT NULL,
                    "TriggerEvent"  TEXT NOT NULL,
                    "FilePath"      TEXT NOT NULL,
                    "FileSizeBytes" INTEGER NOT NULL,
                    "Synced"        INTEGER NOT NULL
                );
                """, ct);
            await db.Database.ExecuteSqlRawAsync(
                """CREATE INDEX IF NOT EXISTS "IX_Screenshots_Synced" ON "Screenshots" ("Synced");""", ct);
            await db.Database.ExecuteSqlRawAsync(
                """CREATE INDEX IF NOT EXISTS "IX_Screenshots_CapturedAt" ON "Screenshots" ("CapturedAt");""", ct);
        }
        catch { /* already exists */ }

        // ze_identity: single-row table, written once on first Agent start then updated on each start.
        // Stores machine/user identity captured at system level (before Revit ever opens).
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ze_identity" (
                "id"            INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                "machine_id"    TEXT NOT NULL,
                "sid"           TEXT NULL,
                "username"      TEXT NOT NULL,
                "hostname"      TEXT NOT NULL,
                "ze_user_id"    TEXT NULL,
                "captured_at"   TEXT NULL,
                "cpu_model"     TEXT NULL,
                "ram_gb"        REAL NOT NULL DEFAULT 0,
                "mac_address"   TEXT NULL,
                "ip_address"    TEXT NULL,
                "os_version"    TEXT NULL,
                "serial_number" TEXT NULL
            );
            """, ct);
    }

    public async Task SaveIdentityAsync(AgentIdentity identity, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ze_identity
                (id, machine_id, sid, username, hostname, captured_at,
                 cpu_model, ram_gb, mac_address, ip_address, os_version, serial_number)
            VALUES
                (1, @machineId, @sid, @username, @hostname, @capturedAt,
                 @cpuModel, @ramGb, @macAddress, @ipAddress, @osVersion, @serialNumber)
            ON CONFLICT(id) DO UPDATE SET
                machine_id    = excluded.machine_id,
                sid           = excluded.sid,
                username      = excluded.username,
                hostname      = excluded.hostname,
                captured_at   = COALESCE(ze_identity.captured_at, excluded.captured_at),
                cpu_model     = excluded.cpu_model,
                ram_gb        = excluded.ram_gb,
                mac_address   = excluded.mac_address,
                ip_address    = excluded.ip_address,
                os_version    = excluded.os_version,
                serial_number = excluded.serial_number;
            """;

        void AddParam(string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        AddParam("@machineId",    identity.MachineId);
        AddParam("@sid",          identity.WindowsSid);
        AddParam("@username",     identity.UserName);
        AddParam("@hostname",     identity.MachineName);
        AddParam("@capturedAt",   DateTime.UtcNow.ToString("o"));
        AddParam("@cpuModel",     identity.CpuModel);
        AddParam("@ramGb",        identity.TotalRamGB);
        AddParam("@macAddress",   identity.MacAddress);
        AddParam("@ipAddress",    identity.IpAddress);
        AddParam("@osVersion",    identity.OsVersion);
        AddParam("@serialNumber", identity.SerialNumber);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddAttendanceAsync(AttendanceSession s, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Attendance.Add(s);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAttendanceAsync(AttendanceSession s, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Attendance.Update(s);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddApplicationUsageAsync(ApplicationUsage u, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.ApplicationUsages.Add(u);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddHardwareSnapshotAsync(HardwareSnapshot h, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.HardwareSnapshots.Add(h);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddNetworkSnapshotAsync(NetworkSnapshot n, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.NetworkSnapshots.Add(n);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddBimEventAsync(BimEvent e, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.BimEvents.Add(e);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddBrowserActivityAsync(BrowserActivity b, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.BrowserActivities.Add(b);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<BrowserActivity>> GetUnsyncedBrowserActivitiesAsync(int take = 200, CancellationToken ct = default)
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

    public async Task<List<Screenshot>> GetUnsyncedScreenshotsAsync(int take = 50, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Screenshots.Where(x => !x.Synced).OrderBy(x => x.CapturedAt).Take(take).ToListAsync(ct);
    }

    public async Task<List<AttendanceSession>> GetUnsyncedAttendanceAsync(int take = 100, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Attendance.Where(x => !x.Synced).Take(take).ToListAsync(ct);
    }

    public async Task<List<ApplicationUsage>> GetUnsyncedApplicationsAsync(int take = 100, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.ApplicationUsages.Where(x => !x.Synced).Take(take).ToListAsync(ct);
    }

    public async Task<List<HardwareSnapshot>> GetUnsyncedHardwareAsync(int take = 200, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.HardwareSnapshots.Where(x => !x.Synced).Take(take).ToListAsync(ct);
    }

    public async Task<List<NetworkSnapshot>> GetUnsyncedNetworkAsync(int take = 100, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.NetworkSnapshots.Where(x => !x.Synced).Take(take).ToListAsync(ct);
    }

    public async Task<List<BimEvent>> GetUnsyncedBimEventsAsync(int take = 100, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.BimEvents.Where(x => !x.Synced).Take(take).ToListAsync(ct);
    }

    public async Task MarkSyncedAsync<T>(IEnumerable<T> rows, CancellationToken ct = default) where T : class
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        foreach (var r in rows)
        {
            var prop = typeof(T).GetProperty("Synced");
            prop?.SetValue(r, true);
            db.Update(r);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> CountUnsyncedAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Attendance.CountAsync(x => !x.Synced, ct)
             + await db.ApplicationUsages.CountAsync(x => !x.Synced, ct)
             + await db.HardwareSnapshots.CountAsync(x => !x.Synced, ct)
             + await db.NetworkSnapshots.CountAsync(x => !x.Synced, ct)
             + await db.BimEvents.CountAsync(x => !x.Synced, ct)
             + await db.BrowserActivities.CountAsync(x => !x.Synced, ct)
             + await db.Screenshots.CountAsync(x => !x.Synced, ct);
    }

    public async Task<(int attendance, int apps, int hardware, int network, int bim)> CountsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return (
            await db.Attendance.CountAsync(ct),
            await db.ApplicationUsages.CountAsync(ct),
            await db.HardwareSnapshots.CountAsync(ct),
            await db.NetworkSnapshots.CountAsync(ct),
            await db.BimEvents.CountAsync(ct)
        );
    }
}
