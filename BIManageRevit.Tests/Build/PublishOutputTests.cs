using System.IO;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace BIManageRevit.Tests.Build
{
    /// <summary>
    /// Verifies that the SQL schema files are embedded resources (not external files).
    /// Also verifies critical embedded resources are accessible at runtime.
    /// </summary>
    public class PublishOutputTests
    {
        [Fact]
        public void SchemaPersistence_IsEmbeddedResource()
        {
            var assembly = typeof(global::BIManage.Data.SQLite.SchemaMigration).Assembly;
            using var stream = assembly.GetManifestResourceStream("BIManage.Data.SQLite.Schema_Persistence.sql");
            stream.Should().NotBeNull("Schema_Persistence.sql must be embedded in the assembly");
            stream!.Length.Should().BeGreaterThan(1000, "SQL file should have substantial content");
        }

        [Fact]
        public void Schema_IsEmbeddedResource()
        {
            var assembly = typeof(global::BIManage.Data.SQLite.SchemaMigration).Assembly;
            using var stream = assembly.GetManifestResourceStream("BIManage.Data.SQLite.Schema.sql");
            stream.Should().NotBeNull("Schema.sql must be embedded in the assembly");
            stream!.Length.Should().BeGreaterThan(500, "SQL file should have substantial content");
        }

        [Fact]
        public void EmbeddedSchema_ContainsSessionsTable()
        {
            var assembly = typeof(global::BIManage.Data.SQLite.SchemaMigration).Assembly;
            using var stream = assembly.GetManifestResourceStream("BIManage.Data.SQLite.Schema_Persistence.sql");
            using var reader = new StreamReader(stream!);
            var sql = reader.ReadToEnd();

            sql.Should().Contain("CREATE TABLE IF NOT EXISTS sessions");
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS session_heartbeats");
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS offline_queue");
        }

        [Fact]
        public void EmbeddedSchema_HasPercentColumns()
        {
            var assembly = typeof(global::BIManage.Data.SQLite.SchemaMigration).Assembly;
            using var stream = assembly.GetManifestResourceStream("BIManage.Data.SQLite.Schema_Persistence.sql");
            using var reader = new StreamReader(stream!);
            var sql = reader.ReadToEnd();

            sql.Should().Contain("memory_usage_percent", "should use _percent not _mb");
            sql.Should().Contain("disk_usage_percent");
            sql.Should().Contain("graphics_usage_percent");
            sql.Should().NotContain("memory_usage_mb", "old _mb columns should not be in fresh schema");
        }
    }
}
