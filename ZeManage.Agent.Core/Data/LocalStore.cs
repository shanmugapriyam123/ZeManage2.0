using Microsoft.EntityFrameworkCore;
using ZeManage.Agent.Core.Models;

namespace ZeManage.Agent.Core.Data;

public sealed class LocalStore
{
    private readonly IDbContextFactory<AgentDbContext> _factory;
    private long _localMachineId;

    public long LocalMachineId => _localMachineId;

    public LocalStore(IDbContextFactory<AgentDbContext> factory)
    {
        _factory = factory;
    }

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        // Phase 1: if tables have old INTEGER PKs, rename them inside SQLite so EF can recreate
        // with TEXT/GUID PKs. Table-rename is metadata-only — works even with DBeaver connected.
        {
            await using var db1 = await _factory.CreateDbContextAsync(ct);
            var conn = db1.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            // WAL mode: allows DBeaver + Agent to read/write simultaneously without locking
            using var wal = conn.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL";
            await wal.ExecuteNonQueryAsync(ct);

            // Give other readers up to 10s before failing on lock
            using var bt = conn.CreateCommand();
            bt.CommandText = "PRAGMA busy_timeout = 10000";
            await bt.ExecuteNonQueryAsync(ct);

            using var chk = conn.CreateCommand();
            chk.CommandText = "SELECT type FROM pragma_table_info('ApplicationUsages') WHERE pk = 1";
            var pkType = await chk.ExecuteScalarAsync(ct) as string;

            if (pkType == "INTEGER")
            {
                foreach (var tbl in new[] { "ApplicationUsages", "BrowserActivities", "NetworkSnapshots", "Screenshots" })
                {
                    // Drop any leftover _old table, then rename current → _old
                    using var dropOld = conn.CreateCommand();
                    dropOld.CommandText = $"DROP TABLE IF EXISTS \"{tbl}_old\"";
                    try { await dropOld.ExecuteNonQueryAsync(ct); } catch { }

                    using var rename = conn.CreateCommand();
                    rename.CommandText = $"ALTER TABLE \"{tbl}\" RENAME TO \"{tbl}_old\"";
                    try { await rename.ExecuteNonQueryAsync(ct); } catch { }
                }
            }
        } // db1 disposed

        // Phase 2: fresh context — EF creates tables with TEXT/GUID PKs
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);

        // Belt-and-suspenders: EF's EnsureCreatedAsync skips CREATE TABLE when the DB file
        // already exists with some tables (e.g. machine_info). Explicit DDL guarantees all
        // four EF tables are present regardless.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ApplicationUsages" (
                "local_id"        TEXT NOT NULL PRIMARY KEY,
                "session_id"      TEXT NULL,
                "ApplicationId"   TEXT NOT NULL DEFAULT '',
                "ApplicationName" TEXT NOT NULL,
                "IconBase64"      TEXT,
                "ProcessName"     TEXT NOT NULL,
                "Version"         TEXT,
                "ApplicationType" TEXT NOT NULL DEFAULT 'Software',
                "Status"          TEXT NOT NULL DEFAULT 'Running',
                "StartTime"       TEXT NOT NULL,
                "EndTime"         TEXT,
                "ActiveSeconds"   INTEGER NOT NULL DEFAULT 0,
                "FocusSeconds"    INTEGER NOT NULL DEFAULT 0,
                "IdleSeconds"     INTEGER NOT NULL DEFAULT 0,
                "CrashCount"      INTEGER NOT NULL DEFAULT 0,
                "Synced"          INTEGER NOT NULL DEFAULT 0,
                "CreatedAt"       TEXT NOT NULL DEFAULT '',
                "UpdatedAt"       TEXT NOT NULL DEFAULT ''
            )
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "BrowserActivities" (
                "browser_activity_id" TEXT NOT NULL,
                "Browser"             TEXT NOT NULL,
                "PageTitle"           TEXT NOT NULL,
                "ApplicationId"       TEXT NULL,
                "Url"                 TEXT NULL,
                "StartTime"           TEXT NOT NULL,
                "EndTime"             TEXT NOT NULL,
                "DurationSeconds"     INTEGER NOT NULL DEFAULT 0,
                "Synced"              INTEGER NOT NULL DEFAULT 0,
                "CreatedAt"           TEXT NOT NULL DEFAULT '',
                "UpdatedAt"           TEXT NOT NULL DEFAULT '',
                PRIMARY KEY ("browser_activity_id")
            )
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "NetworkSnapshots" (
                "network_snapshot_id" TEXT NOT NULL,
                "CapturedAt"          TEXT NOT NULL,
                "DownloadMbps"        REAL NOT NULL DEFAULT 0,
                "UploadMbps"          REAL NOT NULL DEFAULT 0,
                "LatencyMs"           REAL NOT NULL DEFAULT 0,
                "PacketLossPercent"   REAL NOT NULL DEFAULT 0,
                "VpnConnected"        INTEGER NOT NULL DEFAULT 0,
                "ActiveAdapter"       TEXT,
                "ConnectionType"      TEXT,
                "HealthScore"         INTEGER NOT NULL DEFAULT 0,
                "Synced"              INTEGER NOT NULL DEFAULT 0,
                "CreatedAt"           TEXT NOT NULL DEFAULT '',
                "UpdatedAt"           TEXT NOT NULL DEFAULT '',
                PRIMARY KEY ("network_snapshot_id")
            )
            """, ct);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Screenshots" (
                "screenshot_id" TEXT NOT NULL,
                "CapturedAt"    TEXT NOT NULL,
                "TriggerEvent"  TEXT NOT NULL DEFAULT 'Timer',
                "FilePath"      TEXT NOT NULL,
                "FileSizeBytes" INTEGER NOT NULL DEFAULT 0,
                "Synced"        INTEGER NOT NULL DEFAULT 0,
                "CreatedAt"     TEXT NOT NULL DEFAULT '',
                "UpdatedAt"     TEXT NOT NULL DEFAULT '',
                "ApplicationId" TEXT NULL,
                PRIMARY KEY ("screenshot_id")
            )
            """, ct);

        // One row per continuous timeline segment — a span during which the two-state Activity
        // (Active/Idle) AND the foreground application both stayed constant (see
        // Models/ActivityInterval.cs). A genuinely separate table from ApplicationUsages (process
        // open→close) — not an ALTER of any existing table, since the segment model (breaks on
        // Active<->Idle transitions too, not just app switches) is a different shape entirely.
        // Brand-new table, so it never had an INTEGER PK — created with its final TEXT-PK shape
        // from day one, no rename/migration loop needed.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ActivityTimeline" (
                "local_id"               TEXT NOT NULL PRIMARY KEY,
                "application_local_id"   TEXT NULL,
                "ApplicationId"          TEXT NOT NULL DEFAULT '',
                "Activity"               TEXT NOT NULL DEFAULT 'Active',
                "ApplicationName"        TEXT NOT NULL DEFAULT '',
                "ProcessName"            TEXT NOT NULL DEFAULT '',
                "StartTime"              TEXT NOT NULL,
                "EndTime"                TEXT,
                "ActiveSeconds"          INTEGER NOT NULL DEFAULT 0,
                "FocusSeconds"           INTEGER NOT NULL DEFAULT 0,
                "IdleSeconds"            INTEGER NOT NULL DEFAULT 0,
                "Synced"                 INTEGER NOT NULL DEFAULT 0,
                "CreatedAt"              TEXT NOT NULL DEFAULT '',
                "UpdatedAt"              TEXT NOT NULL DEFAULT ''
            )
            """, ct);

        // Indexes used by GetUnsynced queries
        foreach (var idx in new[]
        {
            """CREATE INDEX IF NOT EXISTS "IX_ApplicationUsages_Synced"  ON "ApplicationUsages"  ("Synced")""",
            """CREATE INDEX IF NOT EXISTS "IX_BrowserActivities_Synced"  ON "BrowserActivities"  ("Synced")""",
            """CREATE INDEX IF NOT EXISTS "IX_NetworkSnapshots_Synced"   ON "NetworkSnapshots"   ("Synced")""",
            """CREATE INDEX IF NOT EXISTS "IX_Screenshots_Synced"        ON "Screenshots"        ("Synced")""",
            """CREATE INDEX IF NOT EXISTS "IX_Screenshots_CapturedAt"    ON "Screenshots"        ("CapturedAt")""",
            """CREATE INDEX IF NOT EXISTS "IX_ActivityTimeline_Synced"  ON "ActivityTimeline"  ("Synced")""",
            """CREATE INDEX IF NOT EXISTS "IX_ActivityTimeline_EndTime" ON "ActivityTimeline"  ("EndTime")""",
        })
        try { await db.Database.ExecuteSqlRawAsync(idx, ct); } catch { }

        // Clean up any leftover _old tables from the INTEGER→GUID migration
        foreach (var tbl in new[] { "ApplicationUsages", "BrowserActivities", "NetworkSnapshots", "Screenshots" })
            try { await db.Database.ExecuteSqlRawAsync($"DROP TABLE IF EXISTS \"{tbl}_old\"", ct); } catch { }

        // machine_info: single-row machine identity table (PK = machine_id TEXT UUID)
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "machine_info" (
                "machine_id"       TEXT NOT NULL PRIMARY KEY,
                "sid"              TEXT NULL,
                "username"         TEXT NOT NULL,
                "hostname"         TEXT NOT NULL,
                "captured_at"      TEXT NULL,
                "cpu_model"        TEXT NULL,
                "ram_gb"           REAL NOT NULL DEFAULT 0,
                "ram_usable_gb"    REAL NULL,
                "gpu_model"        TEXT NULL,
                "storage_total_gb" REAL NULL,
                "storage_used_gb"  REAL NULL,
                "storage_type"     TEXT NULL,
                "ram_type"         TEXT NULL,
                "mac_address"      TEXT NULL,
                "ip_address"       TEXT NULL,
                "serial_number"    TEXT NULL,
                "device_id"        TEXT NULL,
                "system_type"      TEXT NULL,
                "os_version"       TEXT NULL,
                "windows_edition"  TEXT NULL,
                "windows_version"  TEXT NULL,
                "os_build"         TEXT NULL,
                "timezone_id"      TEXT NULL
            );
            """, ct);

        // Upgrade: add columns to existing tables (safe — ignored if already exist)
        foreach (var sql in new[]
        {
            // V3: per-app time tracking
            """ALTER TABLE "ApplicationUsages" ADD COLUMN "ActiveSeconds"    INTEGER NOT NULL DEFAULT 0;""",
            """ALTER TABLE "ApplicationUsages" ADD COLUMN "FocusSeconds"     INTEGER NOT NULL DEFAULT 0;""",
            """ALTER TABLE "ApplicationUsages" ADD COLUMN "IdleSeconds"      INTEGER NOT NULL DEFAULT 0;""",
            // V4: audit timestamps
            """ALTER TABLE "ApplicationUsages" ADD COLUMN "CreatedAt"        TEXT NOT NULL DEFAULT '';""",
            """ALTER TABLE "ApplicationUsages" ADD COLUMN "UpdatedAt"        TEXT NOT NULL DEFAULT '';""",
            """ALTER TABLE "NetworkSnapshots"  ADD COLUMN "CreatedAt"        TEXT NOT NULL DEFAULT '';""",
            """ALTER TABLE "NetworkSnapshots"  ADD COLUMN "UpdatedAt"        TEXT NOT NULL DEFAULT '';""",
            """ALTER TABLE "BrowserActivities" ADD COLUMN "CreatedAt"        TEXT NOT NULL DEFAULT '';""",
            """ALTER TABLE "BrowserActivities" ADD COLUMN "UpdatedAt"        TEXT NOT NULL DEFAULT '';""",
            """ALTER TABLE "Screenshots"       ADD COLUMN "CreatedAt"        TEXT NOT NULL DEFAULT '';""",
            """ALTER TABLE "Screenshots"       ADD COLUMN "UpdatedAt"        TEXT NOT NULL DEFAULT '';""",
            """ALTER TABLE "Screenshots"       ADD COLUMN "ApplicationId"    TEXT NULL;""",
            // V5: application type
            """ALTER TABLE "ApplicationUsages" ADD COLUMN "ApplicationType"  TEXT NOT NULL DEFAULT 'Software';""",
            // V9: applicationId (stable per software) + status + icon
            """ALTER TABLE "ApplicationUsages" ADD COLUMN "ApplicationId"    TEXT NOT NULL DEFAULT '';""",
            """ALTER TABLE "ApplicationUsages" ADD COLUMN "Status"           TEXT NOT NULL DEFAULT 'Running';""",
            """ALTER TABLE "ApplicationUsages" ADD COLUMN "IconBase64"       TEXT NULL;""",
            // V10: rename application_id → local_id, add session_id (nullable, filled after server POST)
            """ALTER TABLE "ApplicationUsages" RENAME COLUMN "application_id" TO "local_id";""",
            """ALTER TABLE "ApplicationUsages" ADD COLUMN "session_id" TEXT NULL;""",
            // V6: machine_info new hardware columns
            """ALTER TABLE "machine_info" ADD COLUMN "captured_at"      TEXT NULL;""",
            """ALTER TABLE "machine_info" ADD COLUMN "ram_usable_gb"    REAL NULL;""",
            """ALTER TABLE "machine_info" ADD COLUMN "gpu_model"        TEXT NULL;""",
            """ALTER TABLE "machine_info" ADD COLUMN "storage_total_gb" REAL NULL;""",
            """ALTER TABLE "machine_info" ADD COLUMN "storage_used_gb"  REAL NULL;""",
            """ALTER TABLE "machine_info" ADD COLUMN "device_id"        TEXT NULL;""",
            """ALTER TABLE "machine_info" ADD COLUMN "system_type"      TEXT NULL;""",
            """ALTER TABLE "machine_info" ADD COLUMN "windows_edition"  TEXT NULL;""",
            """ALTER TABLE "machine_info" ADD COLUMN "windows_version"  TEXT NULL;""",
            """ALTER TABLE "machine_info" ADD COLUMN "os_build"         TEXT NULL;""",
            // V11: browser activity — applicationId + url
            """ALTER TABLE "BrowserActivities" ADD COLUMN "ApplicationId" TEXT NULL;""",
            """ALTER TABLE "BrowserActivities" ADD COLUMN "Url"           TEXT NULL;""",
            // V12: network connection type
            """ALTER TABLE "NetworkSnapshots"  ADD COLUMN "ConnectionType" TEXT NULL;""",
            // V7: BIOS + motherboard
            """ALTER TABLE "machine_info" ADD COLUMN "bios_version"      TEXT NULL;""",
            """ALTER TABLE "machine_info" ADD COLUMN "motherboard_model" TEXT NULL;""",
            // V8: local integer ID (maps to SQLite rowid)
            """ALTER TABLE "machine_info" ADD COLUMN "local_id"          INTEGER NULL;""",
            // V9: IANA timezone
            """ALTER TABLE "machine_info" ADD COLUMN "timezone_id"       TEXT NULL;""",
            // V13: fail-closed cache for the admin active/inactive capture kill-switch — see
            // AgentState.IsCaptureEnabled. Defaults to enabled (1) so a fresh install with no
            // confirmed state yet doesn't start out gated; capture_state_confirmed_at is null
            // until the first successful register/validate/refresh/heartbeat response or
            // SignalR push actually confirms a real state from the server.
            """ALTER TABLE "machine_info" ADD COLUMN "is_capture_enabled" INTEGER NOT NULL DEFAULT 1;""",
            """ALTER TABLE "machine_info" ADD COLUMN "capture_state_confirmed_at" TEXT NULL;""",
            // V14: storage media type (SSD/HDD) + RAM type (DDR3/DDR4/DDR5)
            """ALTER TABLE "machine_info" ADD COLUMN "storage_type"     TEXT NULL;""",
            """ALTER TABLE "machine_info" ADD COLUMN "ram_type"         TEXT NULL;""",
            // V15: last-known-good group-worktime-setup response, so a transient empty/failed
            // /my-group fetch (or simply a process restart racing the first fetch) doesn't wipe
            // BreakStartTime/EndTime/ScreenshotIntervalMinutes/etc. back to blank — those fields
            // have no other persistence, unlike is_capture_enabled above. Raw JSON, re-parsed
            // through the same ApplyGroupSettings path used for a live fetch.
            """ALTER TABLE "machine_info" ADD COLUMN "group_settings_json" TEXT NULL;""",
        })
        {
            try { await db.Database.ExecuteSqlRawAsync(sql, ct); } catch { /* column already exists */ }
        }

        // Backfill local_id for any existing rows that don't have it yet
        await db.Database.ExecuteSqlRawAsync(
            """UPDATE "machine_info" SET "local_id" = rowid WHERE "local_id" IS NULL;""", ct);
    }

    public async Task<long> SaveIdentityAsync(AgentIdentity identity, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO machine_info
                (machine_id, sid, username, hostname, captured_at,
                 cpu_model, ram_gb, ram_usable_gb, gpu_model,
                 storage_total_gb, storage_used_gb, storage_type, ram_type,
                 mac_address, ip_address, os_version, serial_number,
                 device_id, system_type, bios_version, motherboard_model,
                 windows_edition, windows_version, os_build, timezone_id)
            VALUES
                (@machineId, @sid, @username, @hostname, @capturedAt,
                 @cpuModel, @ramGb, @ramUsableGb, @gpuModel,
                 @storageTotalGb, @storageUsedGb, @storageType, @ramType,
                 @macAddress, @ipAddress, @osVersion, @serialNumber,
                 @deviceId, @systemType, @biosVersion, @motherboardModel,
                 @windowsEdition, @windowsVersion, @osBuild, @timezoneId)
            ON CONFLICT(machine_id) DO UPDATE SET
                sid              = excluded.sid,
                username         = excluded.username,
                hostname         = excluded.hostname,
                captured_at      = COALESCE(machine_info.captured_at, excluded.captured_at),
                cpu_model        = excluded.cpu_model,
                ram_gb           = excluded.ram_gb,
                ram_usable_gb    = excluded.ram_usable_gb,
                gpu_model        = excluded.gpu_model,
                storage_total_gb = excluded.storage_total_gb,
                storage_used_gb  = excluded.storage_used_gb,
                storage_type     = excluded.storage_type,
                ram_type         = excluded.ram_type,
                mac_address      = excluded.mac_address,
                ip_address       = excluded.ip_address,
                os_version       = excluded.os_version,
                serial_number    = excluded.serial_number,
                device_id        = excluded.device_id,
                system_type      = excluded.system_type,
                bios_version     = excluded.bios_version,
                motherboard_model = excluded.motherboard_model,
                windows_edition  = excluded.windows_edition,
                windows_version  = excluded.windows_version,
                os_build         = excluded.os_build,
                timezone_id      = excluded.timezone_id;
            """;

        void AddParam(string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        AddParam("@machineId",      identity.MachineId);
        AddParam("@sid",            identity.WindowsSid);
        AddParam("@username",       identity.UserName);
        AddParam("@hostname",       identity.MachineName);
        AddParam("@capturedAt",     DateTime.UtcNow.ToString("o"));
        AddParam("@cpuModel",       identity.CpuModel);
        AddParam("@ramGb",          identity.TotalRamGB);
        AddParam("@ramUsableGb",    identity.UsableRamGB);
        AddParam("@gpuModel",       identity.GpuModel);
        AddParam("@storageTotalGb", identity.StorageTotalGB);
        AddParam("@storageUsedGb",  identity.StorageUsedGB);
        AddParam("@storageType",    identity.StorageType);
        AddParam("@ramType",        identity.RamType);
        AddParam("@macAddress",     identity.MacAddress);
        AddParam("@ipAddress",      identity.IpAddress);
        AddParam("@osVersion",      identity.OsVersion);
        AddParam("@serialNumber",   identity.SerialNumber);
        AddParam("@deviceId",        identity.DeviceId);
        AddParam("@systemType",      identity.SystemType);
        AddParam("@biosVersion",     identity.BiosVersion);
        AddParam("@motherboardModel", identity.MotherboardModel);
        AddParam("@windowsEdition",  identity.WindowsEdition);
        AddParam("@windowsVersion", identity.WindowsVersion);
        AddParam("@osBuild",        identity.OsBuild);
        AddParam("@timezoneId",     identity.TimeZoneId);

        await cmd.ExecuteNonQueryAsync(ct);

        // Assign local_id = rowid on first write; leave unchanged on subsequent updates
        using var assignCmd = conn.CreateCommand();
        assignCmd.CommandText =
            "UPDATE machine_info SET local_id = rowid WHERE machine_id = @mid AND local_id IS NULL";
        var ap = assignCmd.CreateParameter(); ap.ParameterName = "@mid"; ap.Value = identity.MachineId;
        assignCmd.Parameters.Add(ap);
        await assignCmd.ExecuteNonQueryAsync(ct);

        // Read back the stable local_id
        using var readCmd = conn.CreateCommand();
        readCmd.CommandText = "SELECT local_id FROM machine_info WHERE machine_id = @mid2";
        var rp = readCmd.CreateParameter(); rp.ParameterName = "@mid2"; rp.Value = identity.MachineId;
        readCmd.Parameters.Add(rp);
        var scalar = await readCmd.ExecuteScalarAsync(ct);
        _localMachineId = scalar is long l ? l : Convert.ToInt64(scalar ?? 0L);
        return _localMachineId;
    }

    public async Task AddApplicationUsageAsync(ApplicationUsage u, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.ApplicationUsages.Add(u);
        await db.SaveChangesAsync(ct);
    }

    // Called when app closes — updates the existing "Running" row with final values
    public async Task UpdateApplicationUsageAsync(ApplicationUsage u, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "ApplicationUsages" SET
                "EndTime"       = @endTime,
                "ActiveSeconds" = @activeSeconds,
                "FocusSeconds"  = @focusSeconds,
                "IdleSeconds"   = @idleSeconds,
                "Status"        = @status,
                "UpdatedAt"     = @updatedAt,
                "Synced"        = 0
            WHERE "local_id" = @id
            """;

        void P(string name, object? val) {
            var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = val ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        P("@id",            u.LocalId);
        P("@endTime",       u.EndTime?.ToString("o"));
        P("@activeSeconds", u.ActiveSeconds);
        P("@focusSeconds",  u.FocusSeconds);
        P("@idleSeconds",   u.IdleSeconds);
        P("@status",        u.Status);
        P("@updatedAt",     u.UpdatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Called after server POST returns a sessionId — updates the row PK
    // Replaces the local temp sessionId with the server-returned sessionId
    public async Task UpdateSessionIdAsync(string localSessionId, string serverSessionId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "ApplicationUsages" SET "session_id" = @sessionId
            WHERE "local_id" = @localId
            """;
        var p1 = cmd.CreateParameter(); p1.ParameterName = "@sessionId"; p1.Value = serverSessionId; cmd.Parameters.Add(p1);
        var p2 = cmd.CreateParameter(); p2.ParameterName = "@localId";   p2.Value = localSessionId;  cmd.Parameters.Add(p2);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddNetworkSnapshotAsync(NetworkSnapshot n, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.NetworkSnapshots.Add(n);
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

    public async Task<List<ApplicationUsage>> GetUnsyncedApplicationsAsync(int take = 100, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.ApplicationUsages.Where(x => !x.Synced).Take(take).ToListAsync(ct);
    }

    // All currently Running apps — loaded on startup to restore tracking state
    public async Task<List<ApplicationUsage>> GetRunningApplicationsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.ApplicationUsages
            .Where(x => x.Status == "Running")
            .ToListAsync(ct);
    }

    // Mark a single closed row as synced — called after SignalR close event fires successfully
    public async Task MarkRowSyncedAsync(string localId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "ApplicationUsages" SET "Synced" = 1 WHERE "local_id" = @id
            """;
        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = localId;
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Periodic flush of running stats while app is still open (SignalR tick + crash safety)
    public async Task UpdateRunningStatsAsync(
        string localId, long activeSeconds, long focusSeconds, long idleSeconds,
        DateTime updatedAt, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "ApplicationUsages" SET
                "ActiveSeconds" = @active,
                "FocusSeconds"  = @focus,
                "IdleSeconds"   = @idle,
                "UpdatedAt"     = @updatedAt
            WHERE "local_id" = @id AND "Status" = 'Running'
            """;
        void P(string n, object? v) {
            var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = v ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        P("@id",        localId);
        P("@active",    activeSeconds);
        P("@focus",     focusSeconds);
        P("@idle",      idleSeconds);
        P("@updatedAt", updatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Fetch just the session_id for a single row (used by CloseSessionAsync and SignalR tick)
    // Fail-closed cache for the admin active/inactive capture kill-switch — see
    // AgentState.IsCaptureEnabled. machine_info is a singleton row (PK on machine_id, one row
    // per install), so these update every row unconditionally rather than filtering by id.

    public async Task SetCaptureEnabledAsync(bool isEnabled, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """UPDATE "machine_info" SET "is_capture_enabled" = @v, "capture_state_confirmed_at" = @now""";
        var pv = cmd.CreateParameter(); pv.ParameterName = "@v"; pv.Value = isEnabled ? 1 : 0;
        cmd.Parameters.Add(pv);
        var pn = cmd.CreateParameter(); pn.ParameterName = "@now"; pn.Value = DateTime.UtcNow.ToString("o");
        cmd.Parameters.Add(pn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Returns the last confirmed capture-enabled state, or true if no state has ever
    /// been confirmed yet (fresh install — fail-open until the first real confirmation).</summary>
    public async Task<bool> GetCaptureEnabledAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "is_capture_enabled" FROM "machine_info" WHERE "capture_state_confirmed_at" IS NOT NULL LIMIT 1""";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull || result is null || Convert.ToInt64(result) != 0;
    }

    // Last-known-good /my-group response — see V15 migration comment above. Persisted so a
    // transient empty fetch (backend hiccup, group-assignment gap, or a process restart racing
    // the first sync cycle) doesn't reset BreakStartTime/EndTime/interval/etc. to blank; the
    // caller re-applies this through the same field-parsing path as a live fetch.
    public async Task SetGroupSettingsRawAsync(string json, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """UPDATE "machine_info" SET "group_settings_json" = @v""";
        var p = cmd.CreateParameter(); p.ParameterName = "@v"; p.Value = json;
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetGroupSettingsRawAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "group_settings_json" FROM "machine_info" WHERE "group_settings_json" IS NOT NULL LIMIT 1""";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull || result is null ? null : (string)result;
    }

    public async Task<string?> GetSessionIdAsync(string localId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "session_id" FROM "ApplicationUsages" WHERE "local_id" = @id""";
        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = localId;
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull || result is null ? null : result.ToString();
    }

    // Running apps whose initial open POST failed (session_id still null) — need retry
    public async Task<List<ApplicationUsage>> GetRunningWithoutSessionIdAsync(int take = 50, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.ApplicationUsages
            .Where(x => x.Status == "Running" && x.SessionId == null)
            .Take(take).ToListAsync(ct);
    }

    public async Task<List<NetworkSnapshot>> GetUnsyncedNetworkAsync(int take = 100, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.NetworkSnapshots.Where(x => !x.Synced).Take(take).ToListAsync(ct);
    }

    public async Task MarkSyncedAsync<T>(IEnumerable<T> rows, CancellationToken ct = default) where T : class
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        foreach (var r in rows)
        {
            typeof(T).GetProperty("Synced")?.SetValue(r, true);
            typeof(T).GetProperty("UpdatedAt")?.SetValue(r, DateTime.UtcNow);
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

    public async Task AddActivityIntervalAsync(ActivityInterval interval, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.ActivityIntervals.Add(interval);
        await db.SaveChangesAsync(ct);
    }

    // Closes an interval — foreground lost, day rollover, or startup crash-recovery cleanup.
    public async Task CloseActivityIntervalAsync(
        string localId, DateTime endTime, long activeSeconds, long focusSeconds, long idleSeconds,
        DateTime updatedAt, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "ActivityTimeline" SET
                "EndTime"       = @endTime,
                "ActiveSeconds" = @activeSeconds,
                "FocusSeconds"  = @focusSeconds,
                "IdleSeconds"   = @idleSeconds,
                "UpdatedAt"     = @updatedAt,
                "Synced"        = 0
            WHERE "local_id" = @id
            """;
        void P(string n, object? v) {
            var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = v ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        P("@id",            localId);
        P("@endTime",       endTime.ToString("o"));
        P("@activeSeconds", activeSeconds);
        P("@focusSeconds",  focusSeconds);
        P("@idleSeconds",   idleSeconds);
        P("@updatedAt",     updatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Periodic durability flush while still open — mirrors UpdateRunningStatsAsync.
    public async Task UpdateOpenActivityIntervalStatsAsync(
        string localId, long activeSeconds, long focusSeconds, long idleSeconds,
        DateTime updatedAt, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "ActivityTimeline" SET
                "ActiveSeconds" = @active,
                "FocusSeconds"  = @focus,
                "IdleSeconds"   = @idle,
                "UpdatedAt"     = @updatedAt
            WHERE "local_id" = @id AND "EndTime" IS NULL
            """;
        void P(string n, object? v) {
            var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = v ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        P("@id",        localId);
        P("@active",    activeSeconds);
        P("@focus",     focusSeconds);
        P("@idle",      idleSeconds);
        P("@updatedAt", updatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Startup crash-recovery: intervals left open by an ungraceful previous shutdown.
    public async Task<List<ActivityInterval>> GetOpenActivityIntervalsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.ActivityIntervals.Where(x => x.EndTime == null).ToListAsync(ct);
    }

    public async Task<List<ActivityInterval>> GetUnsyncedClosedActivityIntervalsAsync(int take = 200, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.ActivityIntervals.Where(x => !x.Synced && x.EndTime != null).Take(take).ToListAsync(ct);
    }

    // Attendance summary for the "Today's Session" UI card — Active/Break/Idle totals from this
    // machine's own local timeline rows only (each install has exactly one machine_info row, so
    // there is no other employee's data in this DB to mix in), plus the earliest segment start
    // today as "login" time. Bounded to a 2-day cutoff so the query never scans the full historical
    // timeline table, then filtered to the exact local calendar day in memory (matches the
    // ToLocalTime().Date day-rollover check already used elsewhere in ProcessMonitor).
    public async Task<(long ActiveSeconds, long BreakSeconds, long IdleSeconds, DateTime? LoginUtc)> GetTodayActivitySummaryAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var cutoffUtc = DateTime.UtcNow.AddDays(-2);
        var rows = await db.ActivityIntervals
            .Where(x => x.EndTime == null || x.StartTime >= cutoffUtc)
            .ToListAsync(ct);

        var todayLocal = DateTime.Now.Date;
        long active = 0, brk = 0, idle = 0;
        DateTime? loginUtc = null;

        foreach (var r in rows)
        {
            if (r.StartTime.ToLocalTime().Date != todayLocal) continue;

            var segSeconds = r.ActiveSeconds + r.FocusSeconds + r.IdleSeconds;
            switch (r.Activity)
            {
                case "Active": active += segSeconds; break;
                case "Break":  brk    += segSeconds; break;
                default:       idle   += segSeconds; break; // "Idle"
            }

            if (loginUtc is null || r.StartTime < loginUtc) loginUtc = r.StartTime;
        }

        return (active, brk, idle, loginUtc);
    }
}
