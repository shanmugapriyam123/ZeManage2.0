using System;
using System.Data.SQLite;
using System.IO;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    /// <summary>
    ///     Handles lightweight database initialization and migrations.
    /// </summary>
    public class MigrationRunner
    {
        private readonly string _databasePath;
        private readonly ILogger? _logger;

        public MigrationRunner(string databasePath, ILogger? logger)
        {
            _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
            _logger = logger;
        }

        public void EnsureDatabase()
        {
            if (File.Exists(_databasePath)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath) ?? ".");
            File.WriteAllBytes(_databasePath, Array.Empty<byte>());
            _logger?.LogInfo($"Created SQLite database at {_databasePath}");
        }

        public void ApplyMigrations(string schemaFilePath)
        {
            _logger?.LogDebug($"Attempting to apply schema from: {schemaFilePath}");

            if (!File.Exists(schemaFilePath))
            {
                _logger?.LogError($"CRITICAL: Schema file not found at {schemaFilePath}. Database tables will not be created!");
                return;
            }

            try
            {
                using var connection = new SQLiteConnection(SqliteConnectionHelper.BuildConnectionString(_databasePath));
                connection.Open();

                string schemaSql = File.ReadAllText(schemaFilePath);
                using var command = new SQLiteCommand(schemaSql, connection);
                command.ExecuteNonQuery();

                _logger?.LogInfo($"Database schema applied successfully from {schemaFilePath}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to apply schema: {ex.Message}", ex);
                throw;
            }
        }
    }
}
