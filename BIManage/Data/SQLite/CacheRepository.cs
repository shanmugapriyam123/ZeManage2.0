using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Repository for managing category_cache and command_cache tables
    /// Used to store category codes and command details fetched from API
    /// </summary>
    public class CacheRepository
    {
        private readonly string _connectionString;
        private readonly ILogger? _logger;
        private bool _tablesEnsured = false;

        public CacheRepository(string databasePath, ILogger? logger = null)
        {
            if (string.IsNullOrEmpty(databasePath))
                throw new ArgumentNullException(nameof(databasePath));

            _connectionString = SqliteConnectionHelper.BuildConnectionString(databasePath);
            _logger = logger;

            // Ensure tables exist on construction
            EnsureTablesExist();
        }

        /// <summary>
        /// Creates the cache tables if they don't exist.
        /// This is a defensive measure in case schema migration didn't run.
        /// </summary>
        private void EnsureTablesExist()
        {
            if (_tablesEnsured) return;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    connection.Open();

                    // Create category_cache table (simple: category_name + category_code_24 only)
                    using (var cmd = new SQLiteCommand(@"
                        CREATE TABLE IF NOT EXISTS category_cache (
                            category_name TEXT PRIMARY KEY NOT NULL,
                            category_code_24 TEXT NULL
                        )", connection))
                    {
                        cmd.ExecuteNonQuery();
                    }

                    // Create command_cache table (command_name + member_name_24 + description + filter flags + revit_command_id)
                    using (var cmd = new SQLiteCommand(@"
                        CREATE TABLE IF NOT EXISTS command_cache (
                            command_name TEXT PRIMARY KEY NOT NULL,
                            member_name_24 TEXT NULL,
                            description TEXT NULL,
                            can_have_binding INTEGER NULL DEFAULT 0,
                            need_binding INTEGER NULL DEFAULT 0,
                            can_work_with_selection INTEGER NULL DEFAULT 0,
                            revit_command_id TEXT NULL
                        )", connection))
                    {
                        cmd.ExecuteNonQuery();
                    }

                    // Add revit_command_id column if missing (for existing databases)
                    try
                    {
                        using (var cmd = new SQLiteCommand(
                            "ALTER TABLE command_cache ADD COLUMN revit_command_id TEXT NULL", connection))
                        {
                            cmd.ExecuteNonQuery();
                        }
                    }
                    catch (SQLiteException) { /* Column already exists */ }

                    _tablesEnsured = true;
                    _logger?.LogInfo("CacheRepository: Tables verified/created successfully");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"CacheRepository: Failed to ensure tables exist: {ex.Message}", ex);
            }
        }

        #region Category Cache

        /// <summary>
        /// Inserts or updates a category code in the cache
        /// Table structure: category_name, category_code_24, description
        /// </summary>
        public async Task<bool> UpsertCategoryCacheAsync(string categoryName, string categoryCode)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // Use INSERT OR IGNORE to skip duplicates (data cached once only)
                    var query = @"
                        INSERT OR IGNORE INTO category_cache (category_name, category_code_24)
                        VALUES (@categoryName, @categoryCode)";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@categoryName", categoryName);
                        command.Parameters.AddWithValue("@categoryCode", categoryCode ?? (object)DBNull.Value);

                        await command.ExecuteNonQueryAsync();
                        _logger?.LogInfo($"Category cache updated: {categoryName} -> {categoryCode}");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error upserting category cache: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Inserts multiple category codes in the cache (skips duplicates)
        /// Uses INSERT OR IGNORE - data is cached once only, subsequent inserts are skipped
        /// </summary>
        public async Task<int> InsertCategoryCacheBatchAsync(List<(string CategoryName, string? CategoryCode)> categories)
        {
            var count = 0;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var transaction = connection.BeginTransaction())
                    {
                        try
                        {
                            var query = @"
                                INSERT OR IGNORE INTO category_cache (category_name, category_code_24)
                                VALUES (@categoryName, @categoryCode)";

                            foreach (var (categoryName, categoryCode) in categories)
                            {
                                using (var command = new SQLiteCommand(query, connection))
                                {
                                    command.Parameters.AddWithValue("@categoryName", categoryName);
                                    command.Parameters.AddWithValue("@categoryCode", categoryCode ?? (object)DBNull.Value);

                                    await command.ExecuteNonQueryAsync();
                                    count++;
                                }
                            }

                            transaction.Commit();
                            _logger?.LogInfo($"Category cache batch inserted: {count} entries");
                        }
                        catch
                        {
                            transaction.Rollback();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error batch inserting category cache: {ex.Message}", ex);
            }

            return count;
        }

        /// <summary>
        /// Legacy method - kept for backwards compatibility
        /// Inserts categories from a dictionary (no duplicates since Dictionary enforces unique keys)
        /// </summary>
        [Obsolete("Use InsertCategoryCacheBatchAsync with List instead to allow duplicates")]
        public async Task<int> UpsertCategoryCacheBatchAsync(Dictionary<string, string> categoryCodes)
        {
            var categories = categoryCodes.Select(kvp => (kvp.Key, (string?)kvp.Value)).ToList();
            return await InsertCategoryCacheBatchAsync(categories);
        }

        /// <summary>
        /// Gets a category code from the cache
        /// </summary>
        public async Task<string?> GetCategoryCacheAsync(string categoryName)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var query = "SELECT category_code_24 FROM category_cache WHERE category_name = @categoryName";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@categoryName", categoryName);
                        var result = await command.ExecuteScalarAsync();
                        return result?.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting category from cache: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Gets all categories from the cache
        /// Returns list of (CategoryName, CategoryCode) tuples
        /// </summary>
        public async Task<List<(string CategoryName, string? CategoryCode)>> GetAllCategoriesFromCacheAsync()
        {
            var categories = new List<(string CategoryName, string? CategoryCode)>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var query = "SELECT category_name, category_code_24 FROM category_cache ORDER BY category_name";

                    using (var command = new SQLiteCommand(query, connection))
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var name = reader["category_name"]?.ToString() ?? "";
                            var code = reader["category_code_24"]?.ToString();
                            if (!string.IsNullOrEmpty(name))
                            {
                                categories.Add((name, code));
                            }
                        }
                    }
                }

                _logger?.LogInfo($"Retrieved {categories.Count} categories from cache");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting all categories from cache: {ex.Message}", ex);
            }

            return categories;
        }

        /// <summary>
        /// Checks if a category exists in the cache
        /// </summary>
        public async Task<bool> CategoryExistsInCacheAsync(string categoryName)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var query = "SELECT COUNT(*) FROM category_cache WHERE category_name = @categoryName";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@categoryName", categoryName);
                        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
                        return count > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error checking category cache: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Gets the count of categories in the cache
        /// Used to check if cache has data before fetching from API
        /// </summary>
        public async Task<int> GetCategoryCacheCountAsync()
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var query = "SELECT COUNT(*) FROM category_cache";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        return Convert.ToInt32(await command.ExecuteScalarAsync());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting category cache count: {ex.Message}", ex);
                return 0;
            }
        }

        #endregion

        #region Command Cache

        /// <summary>
        /// Inserts or updates a command in the cache
        /// Table structure: command_name, member_name_24, description, can_have_binding, need_binding, can_work_with_selection
        /// </summary>
        public async Task<bool> UpsertCommandCacheAsync(string commandName, string memberName, string description, bool canHaveBinding = false, bool needBinding = false, bool canWorkWithSelection = false)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // Use INSERT OR REPLACE to update existing entries with new filter columns
                    var query = @"
                        INSERT OR REPLACE INTO command_cache (command_name, member_name_24, description, can_have_binding, need_binding, can_work_with_selection)
                        VALUES (@commandName, @memberName, @description, @canHaveBinding, @needBinding, @canWorkWithSelection)";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@commandName", commandName);
                        command.Parameters.AddWithValue("@memberName", memberName ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@description", description ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@canHaveBinding", canHaveBinding ? 1 : 0);
                        command.Parameters.AddWithValue("@needBinding", needBinding ? 1 : 0);
                        command.Parameters.AddWithValue("@canWorkWithSelection", canWorkWithSelection ? 1 : 0);

                        await command.ExecuteNonQueryAsync();
                        _logger?.LogInfo($"Command cache updated: {commandName} -> {memberName}");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error upserting command cache: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Inserts multiple commands in the cache with filter flags.
        /// Uses INSERT OR REPLACE to update existing entries with new data from API.
        /// </summary>
        public async Task<int> UpsertCommandCacheBatchAsync(List<(string CommandName, string? MemberName, string? Description, bool CanHaveBinding, bool NeedBinding, bool CanWorkWithSelection, string? Code)> commands)
        {
            var count = 0;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var transaction = connection.BeginTransaction())
                    {
                        try
                        {
                            var query = @"
                                INSERT OR REPLACE INTO command_cache (command_name, member_name_24, description, can_have_binding, need_binding, can_work_with_selection, revit_command_id)
                                VALUES (@commandName, @memberName, @description, @canHaveBinding, @needBinding, @canWorkWithSelection, @revitCommandId)";

                            foreach (var (commandName, memberName, description, canHaveBinding, needBinding, canWorkWithSelection, code) in commands)
                            {
                                using (var command = new SQLiteCommand(query, connection))
                                {
                                    command.Parameters.AddWithValue("@commandName", commandName);
                                    command.Parameters.AddWithValue("@memberName", memberName ?? (object)DBNull.Value);
                                    command.Parameters.AddWithValue("@description", description ?? (object)DBNull.Value);
                                    command.Parameters.AddWithValue("@canHaveBinding", canHaveBinding ? 1 : 0);
                                    command.Parameters.AddWithValue("@needBinding", needBinding ? 1 : 0);
                                    command.Parameters.AddWithValue("@canWorkWithSelection", canWorkWithSelection ? 1 : 0);
                                    command.Parameters.AddWithValue("@revitCommandId", code ?? (object)DBNull.Value);

                                    await command.ExecuteNonQueryAsync();
                                    count++;
                                }
                            }

                            transaction.Commit();
                            _logger?.LogInfo($"Command cache batch updated: {count} entries");
                        }
                        catch
                        {
                            transaction.Rollback();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error batch upserting command cache: {ex.Message}", ex);
            }

            return count;
        }

        /// <summary>
        /// Gets command details from the cache
        /// </summary>
        public async Task<(string? MemberName, string? Description)> GetCommandCacheAsync(string commandName)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var query = "SELECT member_name_24, description FROM command_cache WHERE command_name = @commandName";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@commandName", commandName);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                var memberName = reader["member_name_24"]?.ToString();
                                var description = reader["description"]?.ToString();
                                return (memberName, description);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting command from cache: {ex.Message}", ex);
            }

            return (null, null);
        }

        /// <summary>
        /// Gets all commands from the cache
        /// Returns list of (CommandName, MemberName, Description, CanHaveBinding, NeedBinding, CanWorkWithSelection) tuples
        /// </summary>
        public async Task<List<(string CommandName, string? MemberName, string? Description, bool CanHaveBinding, bool NeedBinding, bool CanWorkWithSelection)>> GetAllCommandsFromCacheAsync()
        {
            var commands = new List<(string CommandName, string? MemberName, string? Description, bool CanHaveBinding, bool NeedBinding, bool CanWorkWithSelection)>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var query = "SELECT command_name, member_name_24, description, can_have_binding, need_binding, can_work_with_selection FROM command_cache ORDER BY command_name";

                    using (var command = new SQLiteCommand(query, connection))
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var name = reader["command_name"]?.ToString() ?? "";
                            var memberName = reader["member_name_24"]?.ToString();
                            var description = reader["description"]?.ToString();
                            var canHaveBinding = Convert.ToInt32(reader["can_have_binding"]) == 1;
                            var needBinding = Convert.ToInt32(reader["need_binding"]) == 1;
                            var canWorkWithSelection = Convert.ToInt32(reader["can_work_with_selection"]) == 1;
                            if (!string.IsNullOrEmpty(name))
                            {
                                commands.Add((name, memberName, description, canHaveBinding, needBinding, canWorkWithSelection));
                            }
                        }
                    }
                }

                _logger?.LogInfo($"Retrieved {commands.Count} commands from cache");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting all commands from cache: {ex.Message}", ex);
            }

            return commands;
        }

        /// <summary>
        /// Checks if a command exists in the cache
        /// </summary>
        public async Task<bool> CommandExistsInCacheAsync(string commandName)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var query = "SELECT COUNT(*) FROM command_cache WHERE command_name = @commandName";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@commandName", commandName);
                        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
                        return count > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error checking command cache: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Gets commands that can have bindings (for Command Protection dialog)
        /// Only returns commands where can_have_binding = 1
        /// </summary>
        public async Task<List<(string CommandName, string? MemberName, string? Description)>> GetCommandsWithBindingAsync()
        {
            var commands = new List<(string CommandName, string? MemberName, string? Description)>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var query = "SELECT command_name, member_name_24, description FROM command_cache WHERE can_have_binding = 1 ORDER BY command_name";

                    using (var command = new SQLiteCommand(query, connection))
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var name = reader["command_name"]?.ToString() ?? "";
                            var memberName = reader["member_name_24"]?.ToString();
                            var description = reader["description"]?.ToString();
                            if (!string.IsNullOrEmpty(name))
                            {
                                commands.Add((name, memberName, description));
                            }
                        }
                    }
                }

                _logger?.LogInfo($"Retrieved {commands.Count} commands with binding capability from cache");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting binding commands from cache: {ex.Message}", ex);
            }

            return commands;
        }

        /// <summary>
        /// Gets the description for a given command name or member name from the cache.
        /// Returns null if not found.
        /// </summary>
        public async Task<string?> GetCommandDescriptionAsync(string? commandNameOrCode)
        {
            if (string.IsNullOrWhiteSpace(commandNameOrCode)) return null;

            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                var query = "SELECT description FROM command_cache WHERE command_name = @key OR member_name_24 = @key LIMIT 1";
                using var cmd = new SQLiteCommand(query, connection);
                cmd.Parameters.AddWithValue("@key", commandNameOrCode);
                var result = await cmd.ExecuteScalarAsync();
                return result?.ToString();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting command description: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Gets commands that need bindings
        /// Only returns commands where need_binding = 1
        /// </summary>
        public async Task<List<(string CommandName, string? MemberName, string? Description)>> GetCommandsWithNeedBindingAsync()
        {
            var commands = new List<(string CommandName, string? MemberName, string? Description)>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var query = "SELECT command_name, member_name_24, description FROM command_cache WHERE need_binding = 1 ORDER BY command_name";

                    using (var command = new SQLiteCommand(query, connection))
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var name = reader["command_name"]?.ToString() ?? "";
                            var memberName = reader["member_name_24"]?.ToString();
                            var description = reader["description"]?.ToString();
                            if (!string.IsNullOrEmpty(name))
                            {
                                commands.Add((name, memberName, description));
                            }
                        }
                    }
                }

                _logger?.LogInfo($"Retrieved {commands.Count} commands with need binding from cache");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting need binding commands from cache: {ex.Message}", ex);
            }

            return commands;
        }

        /// <summary>
        /// Gets commands that can work with selection (for Rules Management dialog)
        /// Only returns commands where can_work_with_selection = 1
        /// </summary>
        public async Task<List<(string CommandName, string? MemberName, string? Description)>> GetCommandsWithSelectionAsync()
        {
            var commands = new List<(string CommandName, string? MemberName, string? Description)>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var query = "SELECT command_name, member_name_24, description FROM command_cache WHERE can_work_with_selection = 1 ORDER BY command_name";

                    using (var command = new SQLiteCommand(query, connection))
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var name = reader["command_name"]?.ToString() ?? "";
                            var memberName = reader["member_name_24"]?.ToString();
                            var description = reader["description"]?.ToString();
                            if (!string.IsNullOrEmpty(name))
                            {
                                commands.Add((name, memberName, description));
                            }
                        }
                    }
                }

                _logger?.LogInfo($"Retrieved {commands.Count} commands with selection capability from cache");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting selection commands from cache: {ex.Message}", ex);
            }

            return commands;
        }

        /// <summary>
        /// Clears all commands from the cache.
        /// Used to force a reload from API with fresh data.
        /// </summary>
        public async Task<int> ClearCommandCacheAsync()
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = new SQLiteCommand("DELETE FROM command_cache", connection))
                    {
                        var deleted = await command.ExecuteNonQueryAsync();
                        _logger?.LogInfo($"Cleared {deleted} commands from cache");
                        return deleted;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error clearing command cache: {ex.Message}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Gets the count of commands in the cache
        /// Used to check if cache has data before fetching from API
        /// </summary>
        public async Task<int> GetCommandCacheCountAsync()
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var query = "SELECT COUNT(*) FROM command_cache";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        return Convert.ToInt32(await command.ExecuteScalarAsync());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting command cache count: {ex.Message}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Gets the Revit command ID string for a given member_name_24.
        /// Synchronous for use in LookupCommandId hot path.
        /// Returns null if not found or no revit_command_id stored.
        /// </summary>
        public string? GetRevitCommandIdByMemberName(string memberName)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    connection.Open();

                    var query = "SELECT revit_command_id FROM command_cache WHERE member_name_24 = @memberName";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@memberName", memberName);
                        var result = command.ExecuteScalar();
                        return result as string;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting revit_command_id for {memberName}: {ex.Message}", ex);
                return null;
            }
        }

        #endregion
    }
}
