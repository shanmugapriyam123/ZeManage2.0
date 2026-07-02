using System;
using System.Data.SQLite;
using System.IO;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManageRevit.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace BIManageRevit.Tests.Integration
{
    public class SchemaMigrationTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly ILogger _logger;

        public SchemaMigrationTests()
        {
            _logger = new ConsoleLogger();
            _dbPath = Path.Combine(Path.GetTempPath(), $"BiManage_Migration_Test_{Guid.NewGuid()}.db");
        }

        public void Dispose()
        {
            TestDatabaseHelper.Cleanup(_dbPath);
        }

        [Fact]
        public void FreshDb_MigrateToLatest_CreatesAllTables()
        {
            var migration = new SchemaMigration(_dbPath, _logger);
            var result = migration.MigrateToLatest();
            result.Should().BeTrue();

            var expectedTables = new[]
            {
                "sessions", "session_heartbeats", "document_sessions",
                "event_log", "offline_queue", "sync_log",
                "audit_log", "rules", "command_settings",
                "event_protection_settings", "pin_protection",
                "model_file_metrics_sync_save", "model_file_metrics_periodic", "model_file_metrics_manual",
                "registered_models", "schema_version",
                "category_cache", "command_cache",
                "unmonitored_user_detections"
            };

            using var conn = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_dbPath));
            conn.Open();

            foreach (var table in expectedTables)
            {
                TableExists(conn, table).Should().BeTrue($"table '{table}' should exist after migration");
            }
        }

        [Fact]
        public void FreshDb_HasCorrectHeartbeatColumns()
        {
            var migration = new SchemaMigration(_dbPath, _logger);
            migration.MigrateToLatest();

            using var conn = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_dbPath));
            conn.Open();

            // Should have _percent columns, NOT _mb
            ColumnExists(conn, "session_heartbeats", "memory_usage_percent").Should().BeTrue();
            ColumnExists(conn, "session_heartbeats", "cpu_usage_percent").Should().BeTrue();
            ColumnExists(conn, "session_heartbeats", "disk_usage_percent").Should().BeTrue();
            ColumnExists(conn, "session_heartbeats", "graphics_usage_percent").Should().BeTrue();
            ColumnExists(conn, "session_heartbeats", "memory_usage_mb").Should().BeFalse("old _mb column should not exist on fresh DB");
        }

        [Fact]
        public void FreshDb_HasHmacColumns()
        {
            var migration = new SchemaMigration(_dbPath, _logger);
            migration.MigrateToLatest();

            using var conn = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_dbPath));
            conn.Open();

            ColumnExists(conn, "command_settings", "row_hmac").Should().BeTrue();
            ColumnExists(conn, "event_protection_settings", "row_hmac").Should().BeTrue();
            ColumnExists(conn, "audit_log", "row_hmac").Should().BeTrue();
        }

        [Fact]
        public void FreshDb_HasCreatedByColumns()
        {
            var migration = new SchemaMigration(_dbPath, _logger);
            migration.MigrateToLatest();

            using var conn = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_dbPath));
            conn.Open();

            ColumnExists(conn, "event_protection_settings", "created_by").Should().BeTrue();
            ColumnExists(conn, "command_settings", "created_by").Should().BeTrue();
        }

        [Fact]
        public void FreshDb_HasSessionCrashColumns()
        {
            var migration = new SchemaMigration(_dbPath, _logger);
            migration.MigrateToLatest();

            using var conn = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_dbPath));
            conn.Open();

            ColumnExists(conn, "sessions", "machine_id").Should().BeTrue();
            ColumnExists(conn, "sessions", "process_id").Should().BeTrue();
            ColumnExists(conn, "sessions", "is_crashed").Should().BeTrue();
        }

        [Fact]
        public void MigrateToLatest_IsIdempotent()
        {
            var migration = new SchemaMigration(_dbPath, _logger);

            migration.MigrateToLatest().Should().BeTrue("first run");
            migration.MigrateToLatest().Should().BeTrue("second run should not fail");
            migration.MigrateToLatest().Should().BeTrue("third run should not fail");
        }

        [Fact]
        public void DefensiveChecks_AddsColumnsToOldSchema()
        {
            // Simulate an old DB with session_heartbeats but missing new columns
            using (var conn = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_dbPath)))
            {
                conn.Open();
                // Create minimal schema_version + session_heartbeats with old column names
                new SQLiteCommand("CREATE TABLE schema_version (version INTEGER, applied_at TEXT, description TEXT)", conn).ExecuteNonQuery();
                new SQLiteCommand("INSERT INTO schema_version (version, applied_at, description) VALUES (27, datetime('now'), 'old schema')", conn).ExecuteNonQuery();
                new SQLiteCommand(@"CREATE TABLE session_heartbeats (
                    id INTEGER PRIMARY KEY, session_id TEXT, timestamp TEXT,
                    active_document_id TEXT, memory_usage_mb INTEGER, cpu_usage_percent REAL,
                    disk_usage_mb INTEGER, graphics_usage_mb INTEGER,
                    created_at TEXT, modified_at TEXT)", conn).ExecuteNonQuery();
                new SQLiteCommand("CREATE TABLE sessions (session_id TEXT PRIMARY KEY, status TEXT, started_at TEXT)", conn).ExecuteNonQuery();
                new SQLiteCommand("CREATE TABLE command_settings (id TEXT PRIMARY KEY)", conn).ExecuteNonQuery();
                new SQLiteCommand("CREATE TABLE event_protection_settings (id TEXT PRIMARY KEY)", conn).ExecuteNonQuery();
                new SQLiteCommand("CREATE TABLE audit_log (id TEXT PRIMARY KEY)", conn).ExecuteNonQuery();
                new SQLiteCommand("CREATE TABLE pin_protection (id TEXT PRIMARY KEY)", conn).ExecuteNonQuery();
                new SQLiteCommand("CREATE TABLE passwords (id TEXT PRIMARY KEY)", conn).ExecuteNonQuery();
                new SQLiteCommand("CREATE TABLE offline_queue (id INTEGER PRIMARY KEY, queue_id TEXT, refer_id TEXT, operation_type TEXT, operation_data TEXT, created_at TEXT, retry_count INTEGER DEFAULT 0, max_retries INTEGER DEFAULT 0, last_retry_at TEXT, last_error TEXT, sync_status TEXT DEFAULT 'pending', synced_at TEXT, priority INTEGER DEFAULT 5)", conn).ExecuteNonQuery();
            }

            // Run migration — should detect v27 and run defensive checks
            var migration = new SchemaMigration(_dbPath, _logger);
            migration.MigrateToLatest().Should().BeTrue();

            using (var conn = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_dbPath)))
            {
                conn.Open();
                // _mb columns should be renamed to _percent
                ColumnExists(conn, "session_heartbeats", "memory_usage_percent").Should().BeTrue("memory_usage_mb should be renamed");
                ColumnExists(conn, "session_heartbeats", "disk_usage_percent").Should().BeTrue("disk_usage_mb should be renamed");

                // HMAC columns should be added
                ColumnExists(conn, "command_settings", "row_hmac").Should().BeTrue();
                ColumnExists(conn, "event_protection_settings", "row_hmac").Should().BeTrue();
                ColumnExists(conn, "event_protection_settings", "created_by").Should().BeTrue();

                // Session columns should be added
                ColumnExists(conn, "sessions", "machine_id").Should().BeTrue();
                ColumnExists(conn, "sessions", "is_crashed").Should().BeTrue();
            }
        }

        private static bool TableExists(SQLiteConnection conn, string table)
        {
            using var cmd = new SQLiteCommand($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'", conn);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        private static bool ColumnExists(SQLiteConnection conn, string table, string column)
        {
            using var cmd = new SQLiteCommand($"PRAGMA table_info({table})", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
