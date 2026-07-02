using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using BIManage.Core.Protection.Models;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Repository for managing protection overrides in SQLite database
    /// Supports CRUD operations with scope-based filtering and expiration handling
    /// </summary>
    public class OverrideRepository
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;

        public OverrideRepository(string databasePath, ILogger logger)
        {
            _connectionString = SqliteConnectionHelper.BuildConnectionString(databasePath);
            _logger = logger;

            EnsureTableExists();
        }

        private void EnsureTableExists()
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                connection.Open();

                var sql = @"
                    CREATE TABLE IF NOT EXISTS protection_overrides (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        override_id TEXT NOT NULL UNIQUE,
                        rule_id TEXT NULL,
                        command_id TEXT NULL,
                        document_id TEXT NULL,
                        scope INTEGER NOT NULL DEFAULT 0,
                        created_at TEXT NOT NULL,
                        expires_at TEXT NULL,
                        approver_email TEXT NOT NULL,
                        approver_name TEXT NOT NULL,
                        reason TEXT NOT NULL,
                        is_active INTEGER NOT NULL DEFAULT 1,
                        revoked_at TEXT NULL,
                        revoked_by TEXT NULL
                    )";

                using var command = new SQLiteCommand(sql, connection);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to create protection_overrides table: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save a new override to the database
        /// </summary>
        public async Task<bool> SaveOverrideAsync(ProtectionOverride protectionOverride)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    INSERT INTO protection_overrides (
                        override_id, rule_id, command_id, document_id, scope,
                        created_at, expires_at, approver_email, approver_name,
                        reason, is_active
                    ) VALUES (
                        @overrideId, @ruleId, @commandId, @documentId, @scope,
                        @createdAt, @expiresAt, @approverEmail, @approverName,
                        @reason, @isActive
                    )
                ";

                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@overrideId", protectionOverride.OverrideId);
                command.Parameters.AddWithValue("@ruleId", protectionOverride.RuleId ?? string.Empty);
                command.Parameters.AddWithValue("@commandId", protectionOverride.CommandId ?? string.Empty);
                command.Parameters.AddWithValue("@documentId", protectionOverride.DocumentId ?? string.Empty);
                command.Parameters.AddWithValue("@scope", (int)protectionOverride.Scope);
                command.Parameters.AddWithValue("@createdAt", protectionOverride.CreatedAt.ToString("O"));
                command.Parameters.AddWithValue("@expiresAt", protectionOverride.ExpiresAt?.ToString("O") ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@approverEmail", protectionOverride.ApproverEmail);
                command.Parameters.AddWithValue("@approverName", protectionOverride.ApproverName);
                command.Parameters.AddWithValue("@reason", protectionOverride.Reason);
                command.Parameters.AddWithValue("@isActive", protectionOverride.IsActive ? 1 : 0);

                await command.ExecuteNonQueryAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to save override: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Get all active overrides (non-revoked, non-expired)
        /// </summary>
        public async Task<List<ProtectionOverride>> GetActiveOverridesAsync()
        {
            var overrides = new List<ProtectionOverride>();

            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    SELECT * FROM protection_overrides
                    WHERE is_active = 1
                    AND (expires_at IS NULL OR expires_at > @now)
                    ORDER BY created_at DESC
                ";

                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    overrides.Add(ReadOverride((SQLiteDataReader)reader));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get active overrides: {ex.Message}", ex);
            }

            return overrides;
        }

        /// <summary>
        /// Get overrides that apply to a specific context
        /// </summary>
        public async Task<List<ProtectionOverride>> GetApplicableOverridesAsync(string ruleId, string commandId, string documentId)
        {
            var allActive = await GetActiveOverridesAsync();
            return allActive.Where(o => o.AppliesTo(ruleId, commandId, documentId)).ToList();
        }

        /// <summary>
        /// Revoke an override by ID
        /// </summary>
        public async Task<bool> RevokeOverrideAsync(string overrideId, string revokedBy)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    UPDATE protection_overrides
                    SET is_active = 0,
                        revoked_at = @revokedAt,
                        revoked_by = @revokedBy
                    WHERE override_id = @overrideId
                ";

                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@overrideId", overrideId);
                command.Parameters.AddWithValue("@revokedAt", DateTime.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("@revokedBy", revokedBy);

                var rowsAffected = await command.ExecuteNonQueryAsync();
                return rowsAffected > 0;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to revoke override {overrideId}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Get override by ID
        /// </summary>
        public async Task<ProtectionOverride> GetOverrideByIdAsync(string overrideId)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = "SELECT * FROM protection_overrides WHERE override_id = @overrideId";

                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@overrideId", overrideId);

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    return ReadOverride((SQLiteDataReader)reader);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get override {overrideId}: {ex.Message}", ex);
            }

            return null;
        }

        /// <summary>
        /// Clean up expired overrides (mark as inactive)
        /// </summary>
        public async Task<int> CleanupExpiredOverridesAsync()
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                    UPDATE protection_overrides
                    SET is_active = 0
                    WHERE is_active = 1
                    AND expires_at IS NOT NULL
                    AND expires_at < @now
                ";

                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));

                return await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to cleanup expired overrides: {ex.Message}", ex);
                return 0;
            }
        }

        private ProtectionOverride ReadOverride(SQLiteDataReader reader)
        {
            return new ProtectionOverride
            {
                Id = reader.GetInt32(0),
                OverrideId = reader.GetString(1),
                RuleId = reader.IsDBNull(2) ? null : reader.GetString(2),
                CommandId = reader.IsDBNull(3) ? null : reader.GetString(3),
                DocumentId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Scope = (OverrideScope)reader.GetInt32(5),
                CreatedAt = DateTime.Parse(reader.GetString(6)),
                ExpiresAt = reader.IsDBNull(7) ? (DateTime?)null : DateTime.Parse(reader.GetString(7)),
                ApproverEmail = reader.GetString(8),
                ApproverName = reader.GetString(9),
                Reason = reader.GetString(10),
                IsActive = reader.GetInt32(11) == 1,
                RevokedAt = reader.IsDBNull(12) ? (DateTime?)null : DateTime.Parse(reader.GetString(12)),
                RevokedBy = reader.IsDBNull(13) ? null : reader.GetString(13)
            };
        }
    }
}
