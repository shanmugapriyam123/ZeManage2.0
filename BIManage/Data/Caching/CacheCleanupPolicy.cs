using System;
using System.Threading;
using System.Threading.Tasks;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.Caching
{
    /// <summary>
    /// Cache cleanup policy manager
    /// Implements automated cleanup strategies for cache, events, and offline queue
    /// Configurable retention periods and cleanup schedules
    /// </summary>
    public class CacheCleanupPolicy : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _databasePath;

        // Repositories
        private readonly EventRepository _eventRepository;
        private readonly OfflineQueueRepository _offlineQueueRepository;
        private readonly OtpRepository _otpRepository;
        private readonly AuditRepository _auditRepository;

        // Cleanup configuration
        private readonly CleanupConfiguration _config;

        // Background timer
        private readonly Timer _cleanupTimer;
        private bool _disposed;

        public CacheCleanupPolicy(
            string databasePath,
            ILogger logger,
            CleanupConfiguration config = null)
        {
            _databasePath = databasePath;
            _logger = logger;
            _config = config ?? CleanupConfiguration.Default;

            // Initialize repositories
            _eventRepository = new EventRepository(databasePath, logger);
            _offlineQueueRepository = new OfflineQueueRepository(databasePath, logger);
            _otpRepository = new OtpRepository(logger);
            _auditRepository = new AuditRepository(databasePath, logger);

            // Start cleanup timer
            _cleanupTimer = new Timer(
                callback: async _ => await ExecuteCleanupAsync(),
                state: null,
                dueTime: _config.InitialDelay,
                period: _config.CleanupInterval);

            _logger?.LogInfo($"CacheCleanupPolicy initialized (Interval: {_config.CleanupInterval.TotalHours}hrs)");
        }

        /// <summary>
        /// Execute all cleanup tasks
        /// </summary>
        public async Task ExecuteCleanupAsync()
        {
            try
            {
                _logger?.LogInfo("Starting scheduled cleanup...");

                var summary = new CleanupSummary
                {
                    StartedAt = DateTime.UtcNow
                };

                // 1. Cleanup old events
                if (_config.EnableEventCleanup)
                {
                    summary.EventsDeleted = await _eventRepository.CleanupOldEventsAsync(
                        _config.EventRetentionPeriod);
                }

                // 2. Cleanup completed offline operations
                if (_config.EnableOfflineQueueCleanup)
                {
                    summary.OfflineOperationsDeleted = await _offlineQueueRepository.CleanupCompletedOperationsAsync(
                        _config.OfflineQueueRetentionPeriod);
                }

                // 3. Cleanup expired OTPs
                if (_config.EnableOtpCleanup)
                {
                    summary.OtpsDeleted = await _otpRepository.CleanupOldOtpsAsync(
                        _config.OtpRetentionPeriod);
                }

                // 4. Cleanup old audit logs
                if (_config.EnableAuditCleanup)
                {
                    summary.AuditEntriesDeleted = await CleanupOldAuditLogsAsync(
                        _config.AuditRetentionPeriod);
                }

                // 5. Vacuum database if threshold met
                if (_config.EnableVacuum && summary.TotalDeleted > _config.VacuumThreshold)
                {
                    await VacuumDatabaseAsync();
                    summary.DatabaseVacuumed = true;
                }

                summary.CompletedAt = DateTime.UtcNow;

                _logger?.LogInfo($"Cleanup completed: {summary}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error during cleanup: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Cleanup old audit logs
        /// </summary>
        private async Task<int> CleanupOldAuditLogsAsync(TimeSpan retentionPeriod)
        {
            try
            {
                using (var connection = new System.Data.SQLite.SQLiteConnection(
                    SqliteConnectionHelper.BuildConnectionString(_databasePath)))
                {
                    await connection.OpenAsync();

                    var cutoffDate = DateTime.UtcNow.Subtract(retentionPeriod);

                    var sql = @"
                        DELETE FROM audit_log
                        WHERE datetime(timestamp) < @cutoffDate";

                    using (var command = new System.Data.SQLite.SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@cutoffDate", cutoffDate.ToString("o"));

                        var deleted = await command.ExecuteNonQueryAsync();

                        if (deleted > 0)
                        {
                            _logger?.LogInfo($"Cleaned up {deleted} old audit entries (older than {retentionPeriod.TotalDays} days)");
                        }

                        return deleted;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to cleanup audit logs: {ex.Message}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Vacuum database to reclaim space
        /// </summary>
        private async Task<bool> VacuumDatabaseAsync()
        {
            try
            {
                using (var connection = new System.Data.SQLite.SQLiteConnection(
                    SqliteConnectionHelper.BuildConnectionString(_databasePath)))
                {
                    await connection.OpenAsync();

                    using (var command = new System.Data.SQLite.SQLiteCommand("VACUUM", connection))
                    {
                        await command.ExecuteNonQueryAsync();
                    }
                }

                _logger?.LogInfo("Database vacuumed successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to vacuum database: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Execute immediate cleanup (on-demand)
        /// </summary>
        public async Task<CleanupSummary> ExecuteImmediateCleanupAsync()
        {
            _logger?.LogInfo("Executing immediate cleanup (on-demand)...");
            await ExecuteCleanupAsync();
            return new CleanupSummary(); // TODO: Return actual summary
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _cleanupTimer?.Dispose();
            _disposed = true;

            _logger?.LogInfo("CacheCleanupPolicy disposed");
        }
    }

    #region Configuration

    /// <summary>
    /// Cleanup configuration model
    /// </summary>
    public class CleanupConfiguration
    {
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(24);

        // Retention periods
        public TimeSpan EventRetentionPeriod { get; set; } = TimeSpan.FromDays(90);
        public TimeSpan OfflineQueueRetentionPeriod { get; set; } = TimeSpan.FromDays(7);
        public TimeSpan OtpRetentionPeriod { get; set; } = TimeSpan.FromDays(30);
        public TimeSpan AuditRetentionPeriod { get; set; } = TimeSpan.FromDays(365);

        // Feature flags
        public bool EnableEventCleanup { get; set; } = true;
        public bool EnableOfflineQueueCleanup { get; set; } = true;
        public bool EnableOtpCleanup { get; set; } = true;
        public bool EnableAuditCleanup { get; set; } = true;
        public bool EnableVacuum { get; set; } = true;

        // Vacuum threshold (minimum deleted items to trigger vacuum)
        public int VacuumThreshold { get; set; } = 1000;

        public static CleanupConfiguration Default => new CleanupConfiguration();

        public static CleanupConfiguration Aggressive => new CleanupConfiguration
        {
            CleanupInterval = TimeSpan.FromHours(12),
            EventRetentionPeriod = TimeSpan.FromDays(30),
            OfflineQueueRetentionPeriod = TimeSpan.FromDays(3),
            OtpRetentionPeriod = TimeSpan.FromDays(7),
            AuditRetentionPeriod = TimeSpan.FromDays(180),
            VacuumThreshold = 500
        };

        public static CleanupConfiguration Conservative => new CleanupConfiguration
        {
            CleanupInterval = TimeSpan.FromDays(7),
            EventRetentionPeriod = TimeSpan.FromDays(180),
            OfflineQueueRetentionPeriod = TimeSpan.FromDays(14),
            OtpRetentionPeriod = TimeSpan.FromDays(60),
            AuditRetentionPeriod = TimeSpan.FromDays(730), // 2 years
            VacuumThreshold = 5000
        };
    }

    /// <summary>
    /// Cleanup summary model
    /// </summary>
    public class CleanupSummary
    {
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public int EventsDeleted { get; set; }
        public int OfflineOperationsDeleted { get; set; }
        public int OtpsDeleted { get; set; }
        public int AuditEntriesDeleted { get; set; }
        public bool DatabaseVacuumed { get; set; }

        public int TotalDeleted => EventsDeleted + OfflineOperationsDeleted + OtpsDeleted + AuditEntriesDeleted;
        public TimeSpan Duration => (CompletedAt ?? DateTime.UtcNow) - StartedAt;

        public override string ToString()
        {
            return $"Cleanup: Events={EventsDeleted}, Offline={OfflineOperationsDeleted}, OTP={OtpsDeleted}, Audit={AuditEntriesDeleted}, Vacuum={DatabaseVacuumed}, Duration={Duration.TotalSeconds:F1}s";
        }
    }

    #endregion
}
