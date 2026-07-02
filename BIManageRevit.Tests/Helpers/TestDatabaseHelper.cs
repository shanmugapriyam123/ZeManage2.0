using System;
using System.IO;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;

namespace BIManageRevit.Tests.Helpers
{
    /// <summary>
    /// Creates a temp database with full schema for integration tests.
    /// Call InitializeTestDatabase() in test constructor before creating repositories.
    /// </summary>
    internal static class TestDatabaseHelper
    {
        /// <summary>
        /// Creates a new temp DB path and runs schema migration to create all tables.
        /// </summary>
        public static string CreateTestDatabase(ILogger? logger = null)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"BiManage_Test_{Guid.NewGuid()}.db");
            InitializeSchema(dbPath, logger);
            return dbPath;
        }

        /// <summary>
        /// Runs SchemaMigration on an existing DB path to ensure all tables exist.
        /// </summary>
        public static void InitializeSchema(string dbPath, ILogger? logger = null)
        {
            var migration = new SchemaMigration(dbPath, logger);
            migration.MigrateToLatest();
        }

        /// <summary>
        /// Deletes a temp test database file.
        /// </summary>
        public static void Cleanup(string dbPath)
        {
            try
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
            catch { }
        }
    }
}
