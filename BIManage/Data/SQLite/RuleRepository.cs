using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using BIManage.Core.Rules.Models;
using BIManage.Infrastructure.Logging;
using Rule = BIManage.Core.Rules.Models.Rule;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Repository for rule persistence in SQLite
    /// </summary>
    public class RuleRepository
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;
        private readonly DatabaseIntegrityService? _integrityService;

        public RuleRepository(string databasePath, ILogger logger, DatabaseIntegrityService? integrityService = null)
        {
            if (string.IsNullOrEmpty(databasePath))
                throw new ArgumentNullException(nameof(databasePath));

            _connectionString = SqliteConnectionHelper.BuildConnectionString(databasePath);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _integrityService = integrityService;
            EnsureSchemaUpToDate();
        }

        private void EnsureSchemaUpToDate()
        {
            if (SchemaMigration.SchemaReady) return;
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                connection.Open();
                AddColumnIfNotExists(connection, "rules", "send_email", "INTEGER DEFAULT 0");
                CreateTableIfNotExists(connection, "rule_builtin_parameters", @"
                    CREATE TABLE IF NOT EXISTS rule_builtin_parameters (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        rule_id TEXT NOT NULL,
                        builtin_parameter_id INTEGER NOT NULL,
                        operator INTEGER NOT NULL DEFAULT 0,
                        value TEXT NOT NULL,
                        ignore_case INTEGER NOT NULL DEFAULT 1,
                        FOREIGN KEY (rule_id) REFERENCES rules(rule_id) ON DELETE CASCADE
                    )");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Schema migration check failed: {ex.Message}");
            }
        }

        private void CreateTableIfNotExists(SQLiteConnection connection, string tableName, string createSql)
        {
            using var checkCmd = new SQLiteCommand(
                $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'", connection);
            var exists = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;
            if (!exists)
            {
                using var createCmd = new SQLiteCommand(createSql, connection);
                createCmd.ExecuteNonQuery();
                _logger.LogInfo($"Created table: {tableName}");
            }
        }

        private void AddColumnIfNotExists(SQLiteConnection connection, string tableName, string columnName, string columnDef)
        {
            using var cmd = new SQLiteCommand($"PRAGMA table_info({tableName})", connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader["name"].ToString() == columnName)
                    return;
            }
            reader.Close();
            using var alterCmd = new SQLiteCommand($"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDef}", connection);
            alterCmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Get all rules from database
        /// </summary>
        public async Task<List<Rule>> GetAllRulesAsync()
        {
            var rules = new List<Rule>();

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // Get all rules in database order (by rowid)
                    var rulesQuery = @"
                        SELECT * FROM rules
                        ORDER BY rowid";

                    var hmacMap = new Dictionary<string, string?>();
                    using (var command = new SQLiteCommand(rulesQuery, connection))
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var rule = MapRule(reader);
                            hmacMap[rule.RuleId] = SafeReadString(reader, "row_hmac");
                            rules.Add(rule);
                        }
                    }

                    // Load parameters, built-in parameters, and commands for each rule
                    foreach (var rule in rules)
                    {
                        await LoadRuleParametersAsync(connection, rule);
                        await LoadRuleBuiltInParametersAsync(connection, rule);
                        await LoadRuleCommandsAsync(connection, rule);
                    }

                    // Verify HMAC after children are loaded — log mismatches but don't exclude
                    // (race conditions with SignalR updates can cause temporary HMAC mismatches)
                    if (_integrityService?.IsIntegrityAvailable == true)
                    {
                        foreach (var rule in rules)
                        {
                            hmacMap.TryGetValue(rule.RuleId, out var storedHmac);
                            if (!VerifyRuleHmac(rule, storedHmac))
                                _logger?.LogDebug($"HMAC mismatch for rules.{rule.RuleId} — may be mid-update, including anyway");
                        }
                    }
                }

                _logger?.LogInfo($"Loaded {rules.Count} rules from database");
                return rules;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load rules from database: {ex.Message}", ex);
                return rules;
            }
        }

        /// <summary>
        /// Get a single rule by ID
        /// </summary>
        public async Task<Rule?> GetRuleByIdAsync(string ruleId)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var query = "SELECT * FROM rules WHERE rule_id = @ruleId";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@ruleId", ruleId);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                var rule = MapRule(reader);
                                var storedHmac = SafeReadString(reader, "row_hmac");
                                await LoadRuleParametersAsync(connection, rule);
                                await LoadRuleBuiltInParametersAsync(connection, rule);
                                await LoadRuleCommandsAsync(connection, rule);

                                if (!VerifyRuleHmac(rule, storedHmac))
                                {
                                    _logger?.LogWarning($"HMAC verification FAILED for rules.{ruleId} — row tampered");
                                    return null;
                                }

                                return rule;
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load rule {ruleId}: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Save or update a rule
        /// </summary>
        public async Task<bool> SaveRuleAsync(Rule rule)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var transaction = connection.BeginTransaction())
                    {
                        try
                        {
                            // Check if rule exists
                            var exists = await RuleExistsAsync(connection, rule.RuleId);

                            if (exists)
                            {
                                // Update existing rule
                                await UpdateRuleAsync(connection, rule);
                            }
                            else
                            {
                                // Insert new rule
                                await InsertRuleAsync(connection, rule);
                            }

                            // Delete old parameters, built-in parameters, and commands
                            await DeleteRuleParametersAsync(connection, rule.RuleId);
                            await DeleteRuleBuiltInParametersAsync(connection, rule.RuleId);
                            await DeleteRuleCommandsAsync(connection, rule.RuleId);

                            // Insert new parameters, built-in parameters, and commands
                            await InsertRuleParametersAsync(connection, rule);
                            await InsertRuleBuiltInParametersAsync(connection, rule);
                            await InsertRuleCommandsAsync(connection, rule);

                            // Compute and store HMAC covering rule + child records
                            await UpdateRuleHmacAsync(connection, rule);

                            transaction.Commit();
                            _logger?.LogInfo($"Rule saved: {rule.RuleId}");
                            return true;
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
                _logger?.LogError($"Failed to save rule {rule.RuleId}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Gets all rule IDs for a specific model (for reconciliation).
        /// </summary>
        public async Task<List<(string RuleId, string Name)>> GetRuleIdsByModelGuidAsync(string? modelGuid)
        {
            var results = new List<(string, string)>();
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                var sql = modelGuid != null
                    ? "SELECT rule_id, name FROM rules WHERE model_guid = @modelGuid"
                    : "SELECT rule_id, name FROM rules WHERE model_guid IS NULL";
                using var cmd = new SQLiteCommand(sql, connection);
                if (modelGuid != null)
                    cmd.Parameters.AddWithValue("@modelGuid", modelGuid);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var id = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var name = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (!string.IsNullOrEmpty(id))
                        results.Add((id, name ?? ""));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Failed to get rules by model: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Returns every locally-cached <c>rules</c> row that the server's
        /// <c>/
        /// </c> response is expected to include
        /// — model-level for the current model, project-level for the project the model
        /// belongs to, plus company-level rows for the current company. Used by
        /// <c>FetchRuleProtectionsByModelAsync</c>'s reconciliation block to find
        /// orphans precisely without sweeping rules that belong to OTHER projects of the
        /// same company (which the old "GetRuleIdsByModelGuidAsync(null)" call would do).
        /// </summary>
        public async Task<List<(string RuleId, string Name)>> GetRuleIdsForFetchScopeAsync(string currentModelGuid, string? companyId)
        {
            var results = new List<(string, string)>();
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                const string sql = @"
                    SELECT rule_id, name FROM rules
                    WHERE (@companyId IS NULL OR company_id IS NULL OR company_id = @companyId COLLATE NOCASE)
                      AND (
                          model_guid = @currentModelGuid
                          OR (model_guid IS NULL AND project_id IS NULL)
                          OR (model_guid IS NULL AND project_id IS NOT NULL AND project_id = (
                              SELECT zemanage_project_id FROM registered_models WHERE model_guid = @currentModelGuid LIMIT 1
                          ))
                      )";

                using var cmd = new SQLiteCommand(sql, connection);
                cmd.Parameters.AddWithValue("@currentModelGuid", currentModelGuid ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@companyId", string.IsNullOrWhiteSpace(companyId) ? (object)DBNull.Value : companyId);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var id = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var name = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (!string.IsNullOrEmpty(id))
                        results.Add((id, name ?? ""));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Failed to get rules for fetch scope: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Returns the minimal set of fields needed for the scope-orphan sweep in
        /// <c>RulesSyncService.FetchRuleProtectionsByModelAsync</c>: every local rule
        /// for the current company (or all rows when <paramref name="companyId"/> is
        /// null), as a lightweight projection of (RuleId, Name, CategoryCode,
        /// RuleScope). Avoids the heavy child-load (parameters / built-in params /
        /// commands + HMAC verify) that <c>GetAllRulesAsync</c> performs — we only
        /// need the identity columns to decide whether to delete a stale row.
        /// </summary>
        public async Task<List<(string RuleId, string Name, string? CategoryCode, int RuleScope)>>
            GetAllRulesForCompanyAsync(string? companyId)
        {
            var rules = new List<(string, string, string?, int)>();
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                const string sql = @"
                    SELECT rule_id, name, category_code, rule_scope
                    FROM rules
                    WHERE @companyId IS NULL
                       OR company_id IS NULL
                       OR company_id = @companyId COLLATE NOCASE";

                using var cmd = new SQLiteCommand(sql, connection);
                cmd.Parameters.AddWithValue("@companyId",
                    string.IsNullOrWhiteSpace(companyId) ? (object)DBNull.Value : companyId);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var id   = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var cat  = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var scope = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3));
                    if (!string.IsNullOrEmpty(id))
                        rules.Add((id, name, cat, scope));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"GetAllRulesForCompanyAsync failed: {ex.Message}");
            }
            return rules;
        }

        /// <summary>
        /// Deletes every locally-cached rule whose <c>company_id</c> is set and does not
        /// match the currently signed-in user's company. Used by RulesSyncService at fetch
        /// time to evict rules left over from a previous session (e.g. signed in to a
        /// different company on the same device). Rows with <c>company_id IS NULL</c> are
        /// preserved — they are system defaults / model overrides without explicit tenancy.
        /// Returns the number of rows deleted.
        /// </summary>
        public async Task<int> DeleteRulesByMismatchedCompanyAsync(string currentCompanyId)
        {
            if (string.IsNullOrWhiteSpace(currentCompanyId)) return 0;
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                // CASCADE on rule_parameters / rule_builtin_parameters / rule_commands fires
                // from the FK on rules(rule_id), so the children clear automatically.
                const string sql = "DELETE FROM rules WHERE company_id IS NOT NULL AND company_id != @companyId COLLATE NOCASE";
                using var cmd = new SQLiteCommand(sql, connection);
                cmd.Parameters.AddWithValue("@companyId", currentCompanyId);
                return await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"DeleteRulesByMismatchedCompanyAsync failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Delete a rule
        /// </summary>
        public async Task<bool> DeleteRuleAsync(string ruleId)
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    // CASCADE will handle parameters and commands
                    var query = "DELETE FROM rules WHERE rule_id = @ruleId";

                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@ruleId", ruleId);
                        var rowsAffected = await command.ExecuteNonQueryAsync();

                        if (rowsAffected > 0)
                        {
                            _logger?.LogInfo($"Rule deleted: {ruleId}");
                            return true;
                        }

                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to delete rule {ruleId}: {ex.Message}", ex);
                return false;
            }
        }

        // Private helper methods

        private Rule MapRule(IDataReader reader)
        {
            return new Rule
            {
                RuleId = reader["rule_id"].ToString()!,
                Name = reader["name"].ToString()!,
                Description = reader["description"]?.ToString() ?? string.Empty,
                Mode = (ProtectionMode)SafeGetInt(reader, "mode", 1),
                Priority = SafeGetInt(reader, "priority", 1),
                IsEnabled = Convert.ToBoolean(reader["is_enabled"]),
                ProjectId = reader["project_id"]?.ToString(),
                CompanyId = reader["company_id"]?.ToString(),
                CategoryId = reader["category_id"] == DBNull.Value ? null : (int?)SafeGetInt(reader, "category_id", 0),
                CategoryName = reader["category_name"]?.ToString(),
                TypeName = reader["type_name"]?.ToString(),
                FamilyName = reader["family_name"]?.ToString(),
                Message = reader["message"]?.ToString() ?? string.Empty,
                CaptureBeforeScreenshot = Convert.ToBoolean(reader["capture_before_screenshot"]),
                CaptureAfterScreenshot = Convert.ToBoolean(reader["capture_after_screenshot"]),
                RequireComment = Convert.ToBoolean(reader["require_comment"]),
                AllowAdminOverride = Convert.ToBoolean(reader["allow_admin_override"]),
                SendEmail = reader["send_email"] != DBNull.Value && Convert.ToBoolean(reader["send_email"]),
                CreatedAt = DateTime.Parse(reader["created_at"].ToString()!),
                ModifiedAt = DateTime.Parse(reader["modified_at"].ToString()!),
                CreatedBy = reader["created_by"]?.ToString(),
                ModifiedBy = reader["modified_by"]?.ToString(),
                ModelGuid = reader["model_guid"]?.ToString(),
                RuleScope = reader["rule_scope"] != DBNull.Value ? SafeGetInt(reader, "rule_scope", (int)RuleScopeType.CompanyWide) : (int)RuleScopeType.CompanyWide,
                CategoryCode = reader["category_code"]?.ToString(),
                Version = SafeGetInt(reader, "version", 1)
            };
        }

        private static string? SafeReadString(IDataReader reader, string column)
        {
            try
            {
                var ordinal = reader.GetOrdinal(column);
                return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            }
            catch (IndexOutOfRangeException) { return null; }
        }

        private static int SafeGetInt(IDataReader reader, string column, int defaultValue)
        {
            var val = reader[column];
            if (val == null || val == DBNull.Value) return defaultValue;
            if (val is int i) return i;
            if (val is long l) return (int)l;
            if (int.TryParse(val.ToString(), out var parsed)) return parsed;
            return defaultValue;
        }

        private async Task LoadRuleParametersAsync(SQLiteConnection connection, Rule rule)
        {
            var query = @"
                SELECT parameter_name, operator, value, ignore_case 
                FROM rule_parameters 
                WHERE rule_id = @ruleId";

            using (var command = new SQLiteCommand(query, connection))
            {
                command.Parameters.AddWithValue("@ruleId", rule.RuleId);

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var paramName = reader["parameter_name"].ToString()!;
                        var condition = new RuleCondition
                        {
                            Value = reader["value"].ToString()!,
                            Operator = (ComparisonOperator)Convert.ToInt32(reader["operator"]),
                            IgnoreCase = Convert.ToBoolean(reader["ignore_case"])
                        };

                        rule.Parameters[paramName] = condition;
                    }
                }
            }
        }

        private async Task LoadRuleBuiltInParametersAsync(SQLiteConnection connection, Rule rule)
        {
            var query = @"
                SELECT builtin_parameter_id, operator, value, ignore_case
                FROM rule_builtin_parameters
                WHERE rule_id = @ruleId";

            using (var command = new SQLiteCommand(query, connection))
            {
                command.Parameters.AddWithValue("@ruleId", rule.RuleId);

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var builtInParamId = (BuiltInParameter)Convert.ToInt32(reader["builtin_parameter_id"]);
                        var condition = new RuleCondition
                        {
                            Value = reader["value"].ToString()!,
                            Operator = (ComparisonOperator)Convert.ToInt32(reader["operator"]),
                            IgnoreCase = Convert.ToBoolean(reader["ignore_case"])
                        };

                        rule.BuiltInParameters[builtInParamId] = condition;
                    }
                }
            }
        }

        private async Task LoadRuleCommandsAsync(SQLiteConnection connection, Rule rule)
        {
            var query = @"
                SELECT command_id, command_name 
                FROM rule_commands 
                WHERE rule_id = @ruleId";

            using (var command = new SQLiteCommand(query, connection))
            {
                command.Parameters.AddWithValue("@ruleId", rule.RuleId);

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        rule.CommandIds.Add(Convert.ToInt32(reader["command_id"]));

                        var commandName = reader["command_name"]?.ToString();
                        if (!string.IsNullOrEmpty(commandName))
                        {
                            rule.CommandNames.Add(commandName);
                        }
                    }
                }
            }
        }

        private async Task<bool> RuleExistsAsync(SQLiteConnection connection, string ruleId)
        {
            var query = "SELECT COUNT(*) FROM rules WHERE rule_id = @ruleId";

            using (var command = new SQLiteCommand(query, connection))
            {
                command.Parameters.AddWithValue("@ruleId", ruleId);
                var count = Convert.ToInt32(await command.ExecuteScalarAsync());
                return count > 0;
            }
        }

        private async Task InsertRuleAsync(SQLiteConnection connection, Rule rule)
        {
            var query = @"
                INSERT INTO rules (
                    rule_id, name, description, mode, priority, is_enabled,
                    project_id, company_id, category_id, category_name,
                    type_name, family_name, message,
                    capture_before_screenshot, capture_after_screenshot,
                    require_comment, allow_admin_override, send_email,
                    created_at, modified_at, created_by, modified_by,
                    model_guid, rule_scope, category_code, version, command_name
                ) VALUES (
                    @ruleId, @name, @description, @mode, @priority, @isEnabled,
                    @projectId, @companyId, @categoryId, @categoryName,
                    @typeName, @familyName, @message,
                    @captureBeforeScreenshot, @captureAfterScreenshot,
                    @requireComment, @allowAdminOverride, @sendEmail,
                    @createdAt, @modifiedAt, @createdBy, @modifiedBy,
                    @modelGuid, @ruleScope, @categoryCode, @version, @commandName
                )";

            using (var command = new SQLiteCommand(query, connection))
            {
                AddRuleParameters(command, rule);
                await command.ExecuteNonQueryAsync();
            }
        }

        private async Task UpdateRuleAsync(SQLiteConnection connection, Rule rule)
        {
            var query = @"
                UPDATE rules SET
                    name = @name,
                    description = @description,
                    mode = @mode,
                    priority = @priority,
                    is_enabled = @isEnabled,
                    project_id = @projectId,
                    company_id = @companyId,
                    category_id = @categoryId,
                    category_name = @categoryName,
                    type_name = @typeName,
                    family_name = @familyName,
                    message = @message,
                    capture_before_screenshot = @captureBeforeScreenshot,
                    capture_after_screenshot = @captureAfterScreenshot,
                    require_comment = @requireComment,
                    allow_admin_override = @allowAdminOverride,
                    send_email = @sendEmail,
                    modified_at = @modifiedAt,
                    modified_by = @modifiedBy,
                    model_guid = @modelGuid,
                    rule_scope = @ruleScope,
                    category_code = @categoryCode,
                    version = @version,
                    command_name = @commandName
                WHERE rule_id = @ruleId";

            using (var command = new SQLiteCommand(query, connection))
            {
                AddRuleParameters(command, rule);
                await command.ExecuteNonQueryAsync();
            }
        }

        private void AddRuleParameters(SQLiteCommand command, Rule rule)
        {
            command.Parameters.AddWithValue("@ruleId", rule.RuleId);
            command.Parameters.AddWithValue("@name", rule.Name);
            command.Parameters.AddWithValue("@description", rule.Description ?? string.Empty);
            command.Parameters.AddWithValue("@mode", (int)rule.Mode);
            command.Parameters.AddWithValue("@priority", rule.Priority);
            command.Parameters.AddWithValue("@isEnabled", rule.IsEnabled);
            command.Parameters.AddWithValue("@projectId", (object?)rule.ProjectId ?? DBNull.Value);
            command.Parameters.AddWithValue("@companyId", (object?)rule.CompanyId ?? DBNull.Value);
            command.Parameters.AddWithValue("@categoryId", (object?)rule.CategoryId ?? DBNull.Value);
            command.Parameters.AddWithValue("@categoryName", (object?)rule.CategoryName ?? DBNull.Value);
            command.Parameters.AddWithValue("@typeName", (object?)rule.TypeName ?? DBNull.Value);
            command.Parameters.AddWithValue("@familyName", (object?)rule.FamilyName ?? DBNull.Value);
            command.Parameters.AddWithValue("@message", rule.Message);
            command.Parameters.AddWithValue("@captureBeforeScreenshot", rule.CaptureBeforeScreenshot);
            command.Parameters.AddWithValue("@captureAfterScreenshot", rule.CaptureAfterScreenshot);
            command.Parameters.AddWithValue("@requireComment", rule.RequireComment);
            command.Parameters.AddWithValue("@allowAdminOverride", rule.AllowAdminOverride);
            command.Parameters.AddWithValue("@sendEmail", rule.SendEmail);
            // Always persist UTC wall-clock so the round-trip (save → SQLite TEXT → load as
            // Kind=Unspecified → recency-window compare) stays consistent. Without the
            // Kind=Local → UTC conversion, a value parsed from a server response with
            // Kind=Local would be stored as LOCAL wall-clock (e.g. "15:41:22" IST instead
            // of "10:11:22" UTC), and the fetch recency check would see every row as
            // 5:30 h ahead of server. Fixed 2026-05-25 after the IST/UTC mismatch was
            // observed in production.
            static string ToUtcStorage(DateTime dt) => dt.Kind switch
            {
                DateTimeKind.Utc => dt.ToString("yyyy-MM-dd HH:mm:ss"),
                DateTimeKind.Local => dt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                _ => dt.ToString("yyyy-MM-dd HH:mm:ss"), // Unspecified: assume already UTC
            };
            command.Parameters.AddWithValue("@createdAt", ToUtcStorage(rule.CreatedAt));
            command.Parameters.AddWithValue("@modifiedAt", ToUtcStorage(rule.ModifiedAt));
            command.Parameters.AddWithValue("@createdBy", (object?)rule.CreatedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("@modifiedBy", (object?)rule.ModifiedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("@modelGuid", (object?)rule.ModelGuid ?? DBNull.Value);
            command.Parameters.AddWithValue("@ruleScope", rule.RuleScope);
            command.Parameters.AddWithValue("@categoryCode", (object?)rule.CategoryCode ?? DBNull.Value);
            command.Parameters.AddWithValue("@version", rule.Version);
            // Command name - stored in command_name column (get first command from list)
            var commandName = rule.CommandNames != null && rule.CommandNames.Count > 0
                ? rule.CommandNames[0]
                : null;
            command.Parameters.AddWithValue("@commandName", (object?)commandName ?? DBNull.Value);
        }

        private async Task DeleteRuleParametersAsync(SQLiteConnection connection, string ruleId)

        {
            var query = "DELETE FROM rule_parameters WHERE rule_id = @ruleId";
            using (var command = new SQLiteCommand(query, connection))
            {
                command.Parameters.AddWithValue("@ruleId", ruleId);
                await command.ExecuteNonQueryAsync();
            }
        }

        private async Task DeleteRuleBuiltInParametersAsync(SQLiteConnection connection, string ruleId)
        {
            var query = "DELETE FROM rule_builtin_parameters WHERE rule_id = @ruleId";
            using (var command = new SQLiteCommand(query, connection))
            {
                command.Parameters.AddWithValue("@ruleId", ruleId);
                await command.ExecuteNonQueryAsync();
            }
        }

        private async Task DeleteRuleCommandsAsync(SQLiteConnection connection, string ruleId)
        {
            var query = "DELETE FROM rule_commands WHERE rule_id = @ruleId";
            using (var command = new SQLiteCommand(query, connection))
            {
                command.Parameters.AddWithValue("@ruleId", ruleId);
                await command.ExecuteNonQueryAsync();
            }
        }

        private async Task InsertRuleParametersAsync(SQLiteConnection connection, Rule rule)
        {
            if (rule.Parameters == null || rule.Parameters.Count == 0)
                return;

            var query = @"
                INSERT INTO rule_parameters (rule_id, parameter_name, operator, value, ignore_case)
                VALUES (@ruleId, @parameterName, @operator, @value, @ignoreCase)";

            foreach (var param in rule.Parameters)
            {
                using (var command = new SQLiteCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@ruleId", rule.RuleId);
                    command.Parameters.AddWithValue("@parameterName", param.Key);
                    command.Parameters.AddWithValue("@operator", (int)param.Value.Operator);
                    command.Parameters.AddWithValue("@value", param.Value.Value);
                    command.Parameters.AddWithValue("@ignoreCase", param.Value.IgnoreCase);
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        private async Task InsertRuleBuiltInParametersAsync(SQLiteConnection connection, Rule rule)
        {
            if (rule.BuiltInParameters == null || rule.BuiltInParameters.Count == 0)
                return;

            var query = @"
                INSERT INTO rule_builtin_parameters (rule_id, builtin_parameter_id, operator, value, ignore_case)
                VALUES (@ruleId, @builtinParameterId, @operator, @value, @ignoreCase)";

            foreach (var param in rule.BuiltInParameters)
            {
                using (var command = new SQLiteCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@ruleId", rule.RuleId);
                    command.Parameters.AddWithValue("@builtinParameterId", (int)param.Key);
                    command.Parameters.AddWithValue("@operator", (int)param.Value.Operator);
                    command.Parameters.AddWithValue("@value", param.Value.Value);
                    command.Parameters.AddWithValue("@ignoreCase", param.Value.IgnoreCase);
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        private async Task InsertRuleCommandsAsync(SQLiteConnection connection, Rule rule)
        {
            // Check if we have command names to insert (API provides names only, no IDs)
            var hasCommandIds = rule.CommandIds != null && rule.CommandIds.Count > 0;
            var hasCommandNames = rule.CommandNames != null && rule.CommandNames.Count > 0;

            if (!hasCommandIds && !hasCommandNames)
                return;

            var query = @"
                INSERT INTO rule_commands (rule_id, command_id, command_name)
                VALUES (@ruleId, @commandId, @commandName)";

            // If we have command IDs, use those as the loop counter
            if (hasCommandIds)
            {
                for (int i = 0; i < rule.CommandIds.Count; i++)
                {
                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@ruleId", rule.RuleId);
                        command.Parameters.AddWithValue("@commandId", rule.CommandIds[i]);
                        command.Parameters.AddWithValue("@commandName",
                            i < rule.CommandNames.Count ? rule.CommandNames[i] : DBNull.Value);
                        await command.ExecuteNonQueryAsync();
                    }
                }
            }
            // If we only have command names (no IDs from API), use command_id = 0
            else if (hasCommandNames)
            {
                for (int i = 0; i < rule.CommandNames.Count; i++)
                {
                    using (var command = new SQLiteCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@ruleId", rule.RuleId);
                        command.Parameters.AddWithValue("@commandId", 0); // No ID from API
                        command.Parameters.AddWithValue("@commandName", rule.CommandNames[i]);
                        await command.ExecuteNonQueryAsync();
                    }
                }
            }
        }

        /// <summary>
        /// Computes and stores HMAC for a rule row, including a hash of child records.
        /// Called within the SaveRuleAsync transaction after all children are written.
        /// </summary>
        private async Task UpdateRuleHmacAsync(SQLiteConnection connection, Rule rule)
        {
            if (_integrityService == null || !_integrityService.IsIntegrityAvailable) return;

            try
            {
                var childHash = ComputeRuleChildHash(rule);
                var hmac = _integrityService.ComputeHmac("rules", rule.RuleId,
                    rule.Name,
                    ((int)rule.Mode).ToString(),
                    rule.Priority.ToString(),
                    rule.IsEnabled ? "1" : "0",
                    rule.CategoryId?.ToString(),
                    rule.CategoryName,
                    rule.TypeName,
                    rule.FamilyName,
                    rule.Message,
                    rule.CaptureBeforeScreenshot ? "1" : "0",
                    rule.CaptureAfterScreenshot ? "1" : "0",
                    rule.RequireComment ? "1" : "0",
                    rule.AllowAdminOverride ? "1" : "0",
                    childHash
                );

                if (!string.IsNullOrEmpty(hmac))
                {
                    using var cmd = new SQLiteCommand(
                        "UPDATE rules SET row_hmac = @hmac WHERE rule_id = @ruleId", connection);
                    cmd.Parameters.AddWithValue("@hmac", hmac);
                    cmd.Parameters.AddWithValue("@ruleId", rule.RuleId);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to update HMAC for rule {rule.RuleId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Computes a SHA-256 hash over sorted child records (parameters, builtin params, commands).
        /// </summary>
        private string ComputeRuleChildHash(Rule rule)
        {
            if (_integrityService == null) return string.Empty;

            var parts = new List<string>();

            // Parameters (sorted by key)
            if (rule.Parameters != null)
            {
                foreach (var p in rule.Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    parts.Add($"P:{p.Key}:{(int)p.Value.Operator}:{p.Value.Value}:{(p.Value.IgnoreCase ? 1 : 0)}");
            }

            // Built-in parameters (sorted by key)
            if (rule.BuiltInParameters != null)
            {
                foreach (var p in rule.BuiltInParameters.OrderBy(kv => (int)kv.Key))
                    parts.Add($"B:{(int)p.Key}:{(int)p.Value.Operator}:{p.Value.Value}:{(p.Value.IgnoreCase ? 1 : 0)}");
            }

            // Commands (sorted by name)
            if (rule.CommandNames != null)
            {
                foreach (var cmd in rule.CommandNames.OrderBy(n => n, StringComparer.Ordinal))
                    parts.Add($"C:{cmd}");
            }

            return _integrityService.ComputeChildHash(parts.ToArray());
        }

        /// <summary>
        /// Verifies HMAC integrity for a rule and its child records. Returns false if tampered.
        /// </summary>
        private bool VerifyRuleHmac(Rule rule, string? storedHmac)
        {
            if (_integrityService == null) return true;

            var childHash = ComputeRuleChildHash(rule);
            return _integrityService.VerifyHmac("rules", rule.RuleId,
                storedHmac,
                rule.Name,
                ((int)rule.Mode).ToString(),
                rule.Priority.ToString(),
                rule.IsEnabled ? "1" : "0",
                rule.CategoryId?.ToString(),
                rule.CategoryName,
                rule.TypeName,
                rule.FamilyName,
                rule.Message,
                rule.CaptureBeforeScreenshot ? "1" : "0",
                rule.CaptureAfterScreenshot ? "1" : "0",
                rule.RequireComment ? "1" : "0",
                rule.AllowAdminOverride ? "1" : "0",
                childHash
            );
        }

        // ============================================
        // CONFLICT CRUD METHODS
        // ============================================

        /// <summary>
        /// Clear all unresolved conflicts (called before re-detection)
        /// </summary>
        public async Task ClearUnresolvedConflictsAsync()
        {
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    var query = "DELETE FROM rule_conflicts WHERE resolved = 0";
                    using (var command = new SQLiteCommand(query, connection))
                    {
                        var deleted = await command.ExecuteNonQueryAsync();
                        _logger.LogDebug($"Cleared {deleted} unresolved conflicts");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error clearing unresolved conflicts: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Persist detected conflicts to the rule_conflicts table
        /// </summary>
        public async Task SaveConflictsAsync(List<RuleConflict> conflicts)
        {
            if (conflicts == null || conflicts.Count == 0)
                return;

            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    using (var transaction = connection.BeginTransaction())
                    {
                        var query = @"
                            INSERT INTO rule_conflicts (rule_id_1, rule_id_2, conflict_type, description, detected_at, resolved)
                            VALUES (@ruleId1, @ruleId2, @conflictType, @description, @detectedAt, @resolved)";

                        foreach (var conflict in conflicts)
                        {
                            using (var command = new SQLiteCommand(query, connection))
                            {
                                command.Parameters.AddWithValue("@ruleId1", conflict.RuleId1);
                                command.Parameters.AddWithValue("@ruleId2", conflict.RuleId2);
                                command.Parameters.AddWithValue("@conflictType", conflict.ConflictType);
                                command.Parameters.AddWithValue("@description", conflict.Description);
                                command.Parameters.AddWithValue("@detectedAt", conflict.DetectedAt.ToString("o"));
                                command.Parameters.AddWithValue("@resolved", conflict.Resolved ? 1 : 0);
                                await command.ExecuteNonQueryAsync();
                            }
                        }

                        transaction.Commit();
                        _logger.LogInfo($"Saved {conflicts.Count} conflicts to rule_conflicts table");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error saving conflicts: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get all unresolved conflicts from the database
        /// </summary>
        public async Task<List<RuleConflict>> GetUnresolvedConflictsAsync()
        {
            var conflicts = new List<RuleConflict>();
            try
            {
                using (var connection = new SQLiteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    var query = "SELECT id, rule_id_1, rule_id_2, conflict_type, description, detected_at, resolved FROM rule_conflicts WHERE resolved = 0";
                    using (var command = new SQLiteCommand(query, connection))
                    {
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                conflicts.Add(new RuleConflict
                                {
                                    Id = Convert.ToInt32(reader["id"]),
                                    RuleId1 = reader["rule_id_1"].ToString()!,
                                    RuleId2 = reader["rule_id_2"].ToString()!,
                                    ConflictType = reader["conflict_type"].ToString()!,
                                    Description = reader["description"].ToString()!,
                                    DetectedAt = DateTime.TryParse(reader["detected_at"].ToString(), out var dt) ? dt : DateTime.UtcNow,
                                    Resolved = Convert.ToInt32(reader["resolved"]) == 1
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting unresolved conflicts: {ex.Message}", ex);
            }

            return conflicts;
        }
    }
}