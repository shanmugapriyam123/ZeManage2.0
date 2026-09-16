using Microsoft.Data.Sqlite;

var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "BIManageRevit", "Logs", "agent.db");

if (!File.Exists(dbPath)) { Console.WriteLine($"DB not found: {dbPath}"); return; }

Console.WriteLine($"Clearing all data from: {dbPath}");
using var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();

foreach (var t in new[] { "ApplicationUsages","BrowserActivities","NetworkSnapshots","Screenshots","ActivityTimeline","machine_info" })
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"DELETE FROM \"{t}\"";
    try { var n = cmd.ExecuteNonQuery(); Console.WriteLine($"  {t}: {n} rows deleted"); }
    catch (Exception ex) { Console.WriteLine($"  {t}: skip ({ex.Message})"); }
}
Console.WriteLine("Done — all local data cleared.");
return;

var statements = new[]
{
    """
    CREATE TABLE "ApplicationUsages" (
        "application_id"  TEXT    NOT NULL PRIMARY KEY,
        "ApplicationId"   TEXT    NOT NULL DEFAULT '',
        "ApplicationName" TEXT    NOT NULL,
        "IconBase64"      TEXT    NULL,
        "ProcessName"     TEXT    NOT NULL,
        "Version"         TEXT    NULL,
        "ApplicationType" TEXT    NOT NULL DEFAULT 'Software',
        "StartTime"       TEXT    NOT NULL,
        "EndTime"         TEXT    NULL,
        "ActiveSeconds"   INTEGER NOT NULL DEFAULT 0,
        "FocusSeconds"    INTEGER NOT NULL DEFAULT 0,
        "IdleSeconds"     INTEGER NOT NULL DEFAULT 0,
        "CrashCount"      INTEGER NOT NULL DEFAULT 0,
        "Status"          TEXT    NOT NULL DEFAULT 'Running',
        "Synced"          INTEGER NOT NULL DEFAULT 0,
        "CreatedAt"       TEXT    NOT NULL DEFAULT '',
        "UpdatedAt"       TEXT    NOT NULL DEFAULT ''
    )
    """,
    """CREATE INDEX "IX_ApplicationUsages_Synced" ON "ApplicationUsages" ("Synced")""",

    """
    CREATE TABLE "NetworkSnapshots" (
        "network_snapshot_id" INTEGER NOT NULL CONSTRAINT "PK_NetworkSnapshots" PRIMARY KEY AUTOINCREMENT,
        "CapturedAt"          TEXT    NOT NULL,
        "DownloadMbps"        REAL    NOT NULL,
        "UploadMbps"          REAL    NOT NULL,
        "LatencyMs"           REAL    NOT NULL,
        "PacketLossPercent"   REAL    NOT NULL,
        "VpnConnected"        INTEGER NOT NULL,
        "ActiveAdapter"       TEXT    NULL,
        "HealthScore"         INTEGER NOT NULL,
        "Synced"              INTEGER NOT NULL,
        "CreatedAt"           TEXT    NOT NULL,
        "UpdatedAt"           TEXT    NOT NULL
    )
    """,
    """CREATE INDEX "IX_NetworkSnapshots_Synced" ON "NetworkSnapshots" ("Synced")""",

    """
    CREATE TABLE "BrowserActivities" (
        "browser_activity_id" INTEGER NOT NULL CONSTRAINT "PK_BrowserActivities" PRIMARY KEY AUTOINCREMENT,
        "Browser"             TEXT    NOT NULL,
        "PageTitle"           TEXT    NOT NULL,
        "StartTime"           TEXT    NOT NULL,
        "EndTime"             TEXT    NOT NULL,
        "DurationSeconds"     INTEGER NOT NULL,
        "Synced"              INTEGER NOT NULL,
        "CreatedAt"           TEXT    NOT NULL,
        "UpdatedAt"           TEXT    NOT NULL
    )
    """,
    """CREATE INDEX "IX_BrowserActivities_Synced" ON "BrowserActivities" ("Synced")""",

    """
    CREATE TABLE "Screenshots" (
        "screenshot_id" INTEGER NOT NULL CONSTRAINT "PK_Screenshots" PRIMARY KEY AUTOINCREMENT,
        "CapturedAt"    TEXT    NOT NULL,
        "TriggerEvent"  TEXT    NOT NULL,
        "FilePath"      TEXT    NOT NULL,
        "FileSizeBytes" INTEGER NOT NULL,
        "Synced"        INTEGER NOT NULL,
        "CreatedAt"     TEXT    NOT NULL,
        "UpdatedAt"     TEXT    NOT NULL
    )
    """,
    """CREATE INDEX "IX_Screenshots_Synced"     ON "Screenshots" ("Synced")""",
    """CREATE INDEX "IX_Screenshots_CapturedAt" ON "Screenshots" ("CapturedAt")""",

    """
    CREATE TABLE "machine_info" (
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
        "mac_address"      TEXT NULL,
        "ip_address"       TEXT NULL,
        "serial_number"    TEXT NULL,
        "device_id"        TEXT NULL,

        "system_type"      TEXT NULL,
        "os_version"       TEXT NULL,
        "windows_edition"  TEXT NULL,
        "windows_version"  TEXT NULL,
        "os_build"         TEXT NULL
    )
    """
};

foreach (var sql in statements)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}

// Verify
Console.WriteLine($"Schema written to: {dbPath}");
Console.WriteLine("\nTables and columns:");
using var tblCmd = conn.CreateCommand();
tblCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
using var tblReader = tblCmd.ExecuteReader();
var tables = new List<string>();
while (tblReader.Read()) tables.Add(tblReader.GetString(0));
tblReader.Close();

foreach (var tbl in tables)
{
    Console.WriteLine($"\n  [{tbl}]");
    using var colCmd = conn.CreateCommand();
    colCmd.CommandText = $"PRAGMA table_info(\"{tbl}\")";
    using var colReader = colCmd.ExecuteReader();
    while (colReader.Read())
        Console.WriteLine($"    {colReader.GetString(1),22}  {colReader.GetString(2),-8}  {(colReader.GetBoolean(3) ? "NOT NULL" : "NULL    ")}");
}
Console.WriteLine();
Console.WriteLine($"Stop ZeManage.Agent then run:");
Console.WriteLine($"  Copy-Item \"{dbPath}\" -Force");
