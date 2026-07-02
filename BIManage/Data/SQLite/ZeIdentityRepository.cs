using System;
using System.Data.SQLite;
using BIManage.Common.Helpers;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    public class ZeIdentityRepository : IDisposable
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;
        private bool _disposed;

        public ZeIdentityRepository(string databasePath, ILogger logger)
        {
            _connectionString = SqliteConnectionHelper.BuildConnectionString(databasePath);
            _logger = logger;
        }

        public ZeIdentityRecord? Load()
        {
            try
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand(
                        "SELECT machine_id, sid, username, hostname, ze_user_id, captured_at, registered_at FROM ze_identity WHERE id = 1",
                        conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read()) return null;
                        return new ZeIdentityRecord
                        {
                            MachineId    = reader.GetString(0),
                            Sid          = reader.IsDBNull(1) ? null : reader.GetString(1),
                            Username     = reader.GetString(2),
                            Hostname     = reader.GetString(3),
                            ZeUserId     = reader.IsDBNull(4) ? null : reader.GetString(4),
                            CapturedAt   = reader.IsDBNull(5) ? null : reader.GetString(5),
                            RegisteredAt = reader.IsDBNull(6) ? null : reader.GetString(6),
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Failed to load ze_identity", ex);
                return null;
            }
        }

        public void SaveIdentity(string machineId, string? sid, string username, string hostname)
        {
            try
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand(conn))
                    {
                        cmd.CommandText = @"
                            INSERT INTO ze_identity (id, machine_id, sid, username, hostname, captured_at)
                            VALUES (1, @machineId, @sid, @username, @hostname, @capturedAt)
                            ON CONFLICT(id) DO UPDATE SET
                                machine_id  = @machineId,
                                sid         = @sid,
                                username    = @username,
                                hostname    = @hostname,
                                captured_at = COALESCE(ze_identity.captured_at, @capturedAt);";
                        cmd.Parameters.AddWithValue("@machineId",  machineId);
                        cmd.Parameters.AddWithValue("@sid",        (object?)sid ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@username",   username);
                        cmd.Parameters.AddWithValue("@hostname",   hostname);
                        cmd.Parameters.AddWithValue("@capturedAt", DateTime.UtcNow.ToString("o"));
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Failed to save ze_identity", ex);
            }
        }

        public void SaveZeUserId(string zeUserId)
        {
            try
            {
                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = new SQLiteCommand(conn))
                    {
                        cmd.CommandText = @"
                            UPDATE ze_identity
                            SET ze_user_id = @zeUserId, registered_at = @now
                            WHERE id = 1;";
                        cmd.Parameters.AddWithValue("@zeUserId", zeUserId);
                        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Failed to save ZeUserId", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    public class ZeIdentityRecord
    {
        public string  MachineId    { get; set; } = "";
        public string? Sid          { get; set; }
        public string  Username     { get; set; } = "";
        public string  Hostname     { get; set; } = "";
        public string? ZeUserId     { get; set; }
        public string? CapturedAt   { get; set; }
        public string? RegisteredAt { get; set; }
    }
}
