namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Shared helper for building SQLite connection strings with WAL mode and BusyTimeout.
    /// WAL (Write-Ahead Logging) allows concurrent reads during writes.
    /// BusyTimeout prevents immediate "database is locked" errors under contention.
    /// </summary>
    public static class SqliteConnectionHelper
    {
        public static string BuildConnectionString(string databasePath)
        {
            return $"Data Source={databasePath};Version=3;Journal Mode=WAL;BusyTimeout=3000;";
        }
    }
}
