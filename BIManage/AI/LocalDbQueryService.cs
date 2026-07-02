using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;

namespace BIManage.AI
{
    /// <summary>
    /// Enables the AI assistant to query the local SQLite database directly.
    /// Hybrid approach: pre-built templates for common questions (fast, single AI call)
    /// + AI-generated SQL fallback for anything else (flexible, two AI calls).
    /// Role-based table access restricts what normal users can query.
    /// </summary>
    public class LocalDbQueryService
    {
        private readonly string _connectionString;
        private readonly ILogger? _logger;
        private const int MaxRows = 50;
        private const int QueryTimeoutSeconds = 5;

        // ── Dangerous SQL keywords (blocklist) ──
        private static readonly string[] DangerousKeywords =
        {
            "DROP", "DELETE", "INSERT", "UPDATE", "ALTER", "CREATE",
            "ATTACH", "DETACH", "VACUUM", "REINDEX", "REPLACE",
            "GRANT", "REVOKE", "EXEC", "EXECUTE"
        };

        // Allow PRAGMA only for reads
        private static readonly string[] DangerousPragmas =
        {
            "PRAGMA WRITABLE_SCHEMA", "PRAGMA JOURNAL_MODE", "PRAGMA WAL_CHECKPOINT"
        };

        // ── Role-based table access ──

        private static readonly HashSet<string> UserTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sessions", "document_sessions", "model_sync", "registered_models",
            "model_file_metrics_sync_save", "model_file_metrics_periodic", "model_file_metrics_manual",
            "background_sync_settings", "session_heartbeats",
            "chat_sessions", "chat_messages",
            "event_log", "sync_queue", "sync_log",
            "schema_version"
        };

        private static readonly HashSet<string> AdminOnlyTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "audit_log", "pin_protection", "rules", "rule_parameters",
            "rule_builtin_parameters", "rule_commands", "rule_conflicts",
            "command_settings", "event_protection_settings", "protection_settings",
            "evidence_capture", "protection_overrides", "protection_passwords",
            "unmonitored_user_detections"
        };

        // ── SQL tag pattern ──
        private static readonly Regex SqlTagPattern = new Regex(
            @"\[SQL\](.*?)\[/SQL\]",
            RegexOptions.Singleline | RegexOptions.Compiled);

        // ── Pre-built query templates ──
        private static readonly List<QueryTemplate> Templates = new List<QueryTemplate>
        {
            // Per-model sync summary — for explicit per-model / grouped phrasings.
            // Must come BEFORE the broad history template so specific intent wins.
            new QueryTemplate(
                new[] {
                    @"(syncs?|synced).*(per|by|each|across)\s+(model|all)",
                    @"how many model.*sync",
                    @"different model.*sync",
                    @"summary.*sync",
                    @"sync summary",
                    @"list of model.*sync",
                    @"models (i\s|i'?ve\s|i have\s)(synced|sync)",
                    @"what models.*sync"
                },
                "SELECT model_name, COUNT(*) AS total_syncs, MAX(sync_started_at) AS last_synced_at, MIN(sync_started_at) AS first_synced_at, SUM(CASE WHEN is_succeeded = 1 THEN 1 ELSE 0 END) AS successful_syncs, SUM(CASE WHEN is_succeeded = 0 THEN 1 ELSE 0 END) AS failed_syncs, ROUND(AVG(sync_duration_seconds), 1) AS avg_duration_seconds FROM model_sync GROUP BY model_name ORDER BY last_synced_at DESC LIMIT 15",
                false),

            // Latest / most-recent sync — top 5 chronological.
            // Includes "when ... sync(ed)" patterns to catch phrasings like "when my model synced"
            // (and tolerates typos elsewhere in the question — only "when" and "sync" need to land).
            new QueryTemplate(
                new[] {
                    "last sync", "latest sync", "most recent sync", "when was it synced",
                    @"when did.*sync", @"last time.*sync",
                    @"when.*sync(ed)?",
                    @"when.*model.*sync(ed)?"
                },
                "SELECT model_name, synced_by, sync_started_at, sync_ended_at, sync_duration_seconds, is_succeeded FROM model_sync ORDER BY sync_started_at DESC LIMIT 5",
                false),

            // Full sync history across all models — catch-all for generic sync-history phrasings.
            new QueryTemplate(
                new[] {
                    "sync history", "sync log", "sync logs", "sync record",
                    "synced times", "sync times", "synced time",
                    "synced model", "model synced", "models synced",
                    "history of sync",
                    @"all (my |the )?syncs?",
                    @"list (of |my |the |all )?syncs?",
                    "sync count", "how many sync",
                    @"tell me.*sync(ed|s)",
                    @"show.*sync(ed|s| log| history)",
                    @"syncs i (have |'?ve |did )"
                },
                "SELECT model_name, synced_by, sync_started_at, sync_ended_at, sync_duration_seconds, is_succeeded, is_relinquished FROM model_sync ORDER BY sync_started_at DESC LIMIT 25",
                false),

            new QueryTemplate(
                new[] { "sessions today", "how many sessions today" },
                "SELECT COUNT(*) as session_count, username FROM sessions WHERE date(started_at) = date('now','localtime') GROUP BY username",
                false),

            new QueryTemplate(
                new[] { "active session", "current session", "who is online" },
                "SELECT session_id, username, computer_name, revit_version, started_at, last_heartbeat FROM sessions WHERE is_active = 1",
                false),

            new QueryTemplate(
                new[] { "crash", "how many crash", "crash history", "crash count" },
                "SELECT session_id, username, computer_name, started_at, ended_at FROM sessions WHERE crash_detected = 1 OR is_crashed = 1 ORDER BY started_at DESC LIMIT 10",
                false),

            new QueryTemplate(
                new[] { "audit log", "protection log", "recent audit", "audit history" },
                "SELECT timestamp, user_name, command_name, mode, action, element_count, element_category FROM audit_log ORDER BY timestamp DESC LIMIT 15",
                true),

            new QueryTemplate(
                new[] { "pinned element", "how many pin", "active pin", "protected element" },
                "SELECT element_name, element_category, element_type, protected_by, protected_at FROM pin_protection WHERE is_active = 1 ORDER BY protected_at DESC LIMIT 20",
                true),

            new QueryTemplate(
                new[] { "metrics history", "warning trend", "file size history", "warning history" },
                "SELECT captured_at, captured_by, warnings_count, file_size_bytes, duplicate_elements_count, total_families_count FROM model_file_metrics_sync_save ORDER BY captured_at DESC LIMIT 15",
                false),

            new QueryTemplate(
                new[] { "registered model", "model list", "what model" },
                "SELECT model_name, model_guid, registered_by, registered_at, is_active FROM registered_models ORDER BY registered_at DESC LIMIT 10",
                false),

            new QueryTemplate(
                new[] { "sync setting", "sync interval", "sync config", "background sync" },
                "SELECT is_enabled, sync_interval_minutes, relinquish_interval_minutes, enable_idle_sync, idle_timeout_minutes, enable_relinquish, enable_schedule, schedule_start_time, schedule_end_time, compact_model_once_a_day, exit_revit_on_idle, exit_revit_after_minutes FROM background_sync_settings LIMIT 1",
                false),

            new QueryTemplate(
                new[] { "who opened", "model opened by", "document history", "open history" },
                "SELECT username, document_title, central_model_name, opened_at, closed_at, is_active FROM document_sessions ORDER BY opened_at DESC LIMIT 10",
                false),

            new QueryTemplate(
                new[] { "rule list", "active rule", "protection rule", "how many rule" },
                "SELECT name, description, category_name, mode, priority, is_enabled FROM rules WHERE is_enabled = 1 ORDER BY priority DESC LIMIT 15",
                true),

            new QueryTemplate(
                new[] { "evidence", "screenshot", "capture history" },
                "SELECT evidence_id, protection_type, capture_type, capture_stage, captured_at, element_count, upload_status FROM evidence_capture ORDER BY captured_at DESC LIMIT 10",
                true),

            new QueryTemplate(
                new[] { "heartbeat", "cpu usage", "memory usage", "system resource", "resource usage" },
                "SELECT session_id, timestamp, memory_usage_percent, cpu_usage_percent, disk_usage_percent, graphics_usage_percent FROM session_heartbeats ORDER BY timestamp DESC LIMIT 15",
                false)
        };

        public LocalDbQueryService(string databasePath, ILogger? logger = null)
        {
            _connectionString = $"Data Source={databasePath};Version=3;Journal Mode=WAL;Read Only=True;BusyTimeout=3000;";
            _logger = logger;
        }

        // ──────────────────────────────────────────────────────────────────
        // Fast path: pre-built template queries
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Tries to match the user message against pre-built query templates.
        /// Returns formatted results if matched, null if no template fits.
        /// </summary>
        public string? TryRunTemplateQuery(string userMessage, bool isAdmin)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return null;
            var lower = userMessage.ToLowerInvariant();

            foreach (var template in Templates)
            {
                if (!template.Keywords.Any(k => Regex.IsMatch(lower, k)))
                    continue;

                // Role check — friendly message with alternatives
                if (template.AdminOnly && !isAdmin)
                    return GetAccessRestrictedMessage(template.Keywords);

                try
                {
                    return ExecuteQuery(template.Sql);
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"[LocalDbQuery] Template query failed: {ex.Message}", ex);
                    return null;
                }
            }

            return null;
        }

        private static string GetAccessRestrictedMessage(string[] keywords)
        {
            // Detect what the user was trying to access and suggest alternatives
            var joined = string.Join(" ", keywords).ToLowerInvariant();

            string topic;
            string suggestion;

            if (joined.Contains("audit") || joined.Contains("protection log"))
            {
                topic = "audit logs and protection activity";
                suggestion = "You can ask about your own session history, sync status, or model health metrics instead.";
            }
            else if (joined.Contains("pin") || joined.Contains("protected element"))
            {
                topic = "pin protection details";
                suggestion = "You can check your model's health score, sync history, or ask general questions about how pin protection works.";
            }
            else if (joined.Contains("rule"))
            {
                topic = "protection rules configuration";
                suggestion = "If a rule is blocking your work, you can request an OTP from your administrator to proceed.";
            }
            else if (joined.Contains("evidence") || joined.Contains("screenshot") || joined.Contains("capture"))
            {
                topic = "evidence and screenshot captures";
                suggestion = "You can ask about your session activity, sync history, or model metrics instead.";
            }
            else
            {
                topic = "this information";
                suggestion = "You can ask about sessions, sync history, model metrics, crash history, or sync settings instead.";
            }

            return $"The {topic} is managed by your administrators and is not accessible from here." +
                   $"{suggestion}\n\nIf you need access, please contact your Project or Company Administrator.";
        }

        // ──────────────────────────────────────────────────────────────────
        // Flexible path: AI-generated SQL
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts SQL from AI response wrapped in [SQL]...[/SQL] tags.
        /// Returns null if no SQL tags found.
        /// </summary>
        public static string? ExtractSqlFromResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return null;
            var match = SqlTagPattern.Match(response);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        /// <summary>
        /// Validates and executes an AI-generated SQL query.
        /// Returns formatted results or an error/access message.
        /// </summary>
        public string ExecuteReadOnlyQuery(string sql, bool isAdmin)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return "No query provided.";

            if (!IsSafeQuery(sql))
                return "Query rejected: only SELECT statements are allowed.";

            if (!IsTableAccessible(sql, isAdmin))
                return "I don't have access to some of the data needed for this query — it's managed by your project or company administrators. " +
                       "I can still help you with session history, sync status, model metrics, health data, and sync settings. Try asking about those instead!";

            try
            {
                return ExecuteQuery(sql);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[LocalDbQuery] AI-generated query failed: {ex.Message}", ex);
                return "Query execution failed. Please try rephrasing your question.";
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Schema description (role-filtered)
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a compact schema description suitable for AI prompt injection.
        /// Admin-only tables are included only when isAdmin is true.
        /// </summary>
        public string GetSchemaDescription(bool isAdmin)
        {
            var sb = new StringBuilder();
            sb.AppendLine("LOCAL DATABASE SCHEMA — you can generate SELECT queries against these tables:");
            sb.AppendLine("Wrap your SQL in [SQL]...[/SQL] tags. Only SELECT. Always include LIMIT.");
            sb.AppendLine("All timestamps are ISO 8601 strings. Use datetime() for date math.");
            sb.AppendLine();
            sb.AppendLine("-- Sessions & Activity --");
            sb.AppendLine("sessions(session_id, username, computer_name, revit_version, started_at, ended_at, status, is_active, crash_detected, is_crashed, total_commands, total_events, last_heartbeat)");
            sb.AppendLine("document_sessions(session_id, document_title, model_guid, central_model_path, central_model_name, opened_at, closed_at, is_active, total_modifications)");
            sb.AppendLine("model_sync(sync_guid, session_id, model_guid, model_name, synced_by, sync_started_at, sync_ended_at, sync_duration_seconds, is_succeeded, is_relinquished)");
            sb.AppendLine("session_heartbeats(session_id, timestamp, memory_usage_percent, cpu_usage_percent, disk_usage_percent, graphics_usage_percent)");
            sb.AppendLine("event_log(session_id, timestamp, event_type, event_category, event_name, element_id, success, duration_ms)");
            sb.AppendLine();
            sb.AppendLine("-- Models & Metrics --");
            sb.AppendLine("registered_models(model_guid, model_name, central_model_path, is_active, registered_by, registered_at, last_opened_at, is_workshared, is_cloudmodel)");
            sb.AppendLine("model_file_metrics_sync_save(model_guid, model_name, capture_type, captured_at, captured_by, file_size_bytes, warnings_count, duplicate_elements_count, levels_count, grids_count, total_views_count, total_families_count, sheets_count)");
            sb.AppendLine("model_file_metrics_periodic(model_guid, captured_at, total_elements_count, model_elements_count, annotative_elements_count, inplace_families_count, unenclosed_rooms_count, views_not_on_sheets_count, walls_not_connected_count, pipes_not_connected_count, ducts_not_connected_count)");
            sb.AppendLine("model_file_metrics_manual(model_guid, captured_at, families_over_5mb_count, purgeable_elements_count)");
            sb.AppendLine();
            sb.AppendLine("-- Settings --");
            sb.AppendLine("background_sync_settings(is_enabled, sync_interval_minutes, relinquish_interval_minutes, idle_timeout_minutes, enable_schedule, schedule_start_time, schedule_end_time, compact_model_once_a_day, exit_revit_on_idle)");
            sb.AppendLine();
            sb.AppendLine("-- Chat History --");
            sb.AppendLine("chat_sessions(chat_session_id, model_name, user_name, started_at, ended_at, message_count, title)");
            sb.AppendLine("chat_messages(chat_session_id, role, content, timestamp, feedback_rating)");

            if (isAdmin)
            {
                sb.AppendLine();
                sb.AppendLine("-- Protection & Audit (Admin Only) --");
                sb.AppendLine("audit_log(audit_log_id, timestamp, user_name, command_name, mode, action, element_ids, element_count, element_category, element_family_type, element_name, override_method, event_source, session_id)");
                sb.AppendLine("pin_protection(id, model_guid, element_id, element_name, element_category, element_type, protection_mode, protected_by, protected_at, is_active, deactivated_at, deactivated_by)");
                sb.AppendLine("rules(rule_id, name, description, rule_scope, category_name, type_name, family_name, mode, priority, is_enabled, message)");
                sb.AppendLine("command_settings(id, command_code, is_enabled, intervention_mode, scope, custom_message)");
                sb.AppendLine("evidence_capture(evidence_id, protection_type, capture_type, capture_stage, captured_at, element_count, upload_status, file_size_bytes)");
            }

            return sb.ToString();
        }

        // ──────────────────────────────────────────────────────────────────
        // Safety validation
        // ──────────────────────────────────────────────────────────────────

        private bool IsSafeQuery(string sql)
        {
            var trimmed = sql.Trim();
            var upper = trimmed.ToUpperInvariant();

            // Must start with SELECT
            if (!upper.StartsWith("SELECT"))
            {
                _logger?.LogWarning($"[LocalDbQuery] BLOCKED — does not start with SELECT: {sql.Substring(0, Math.Min(50, sql.Length))}");
                return false;
            }

            // Check for dangerous keywords (as standalone words)
            foreach (var keyword in DangerousKeywords)
            {
                // Match as a whole word to avoid false positives (e.g., "UPDATED_AT" shouldn't match "UPDATE")
                if (Regex.IsMatch(upper, $@"\b{keyword}\b"))
                {
                    _logger?.LogWarning($"[LocalDbQuery] BLOCKED — contains dangerous keyword '{keyword}': {sql.Substring(0, Math.Min(80, sql.Length))}");
                    return false;
                }
            }

            // Block dangerous PRAGMAs
            foreach (var pragma in DangerousPragmas)
            {
                if (upper.Contains(pragma))
                {
                    _logger?.LogWarning($"[LocalDbQuery] BLOCKED — contains dangerous PRAGMA: {sql.Substring(0, Math.Min(80, sql.Length))}");
                    return false;
                }
            }

            // Block semicolons (prevent multi-statement injection)
            if (trimmed.TrimEnd(';').Contains(';'))
            {
                _logger?.LogWarning("[LocalDbQuery] BLOCKED — contains multiple statements (semicolon)");
                return false;
            }

            return true;
        }

        private bool IsTableAccessible(string sql, bool isAdmin)
        {
            // If admin, all tables are accessible
            if (isAdmin) return true;

            var upper = sql.ToUpperInvariant();

            // Check if any admin-only table is referenced
            foreach (var table in AdminOnlyTables)
            {
                if (Regex.IsMatch(upper, $@"\b{table.ToUpperInvariant()}\b"))
                {
                    _logger?.LogWarning($"[LocalDbQuery] ACCESS DENIED — non-admin querying admin table '{table}'");
                    return false;
                }
            }

            return true;
        }

        // ──────────────────────────────────────────────────────────────────
        // Query execution
        // ──────────────────────────────────────────────────────────────────

        private string ExecuteQuery(string sql)
        {
            // Enforce LIMIT if not present
            var upper = sql.ToUpperInvariant();
            if (!upper.Contains("LIMIT"))
                sql = sql.TrimEnd().TrimEnd(';') + $" LIMIT {MaxRows}";

            var sb = new StringBuilder();
            var columns = new List<string>();
            var rows = new List<List<string>>();

            using var conn = new SQLiteConnection(_connectionString);
            conn.Open();

            using var cmd = new SQLiteCommand(sql, conn);
            cmd.CommandTimeout = QueryTimeoutSeconds;

            using var reader = cmd.ExecuteReader();

            // Collect column names
            for (int i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));

            // Collect rows
            int rowCount = 0;
            while (reader.Read() && rowCount < MaxRows)
            {
                var row = new List<string>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? "NULL";
                    row.Add(value);
                }
                rows.Add(row);
                rowCount++;
            }

            if (rows.Count == 0)
            {
                sb.AppendLine("Query returned 0 rows.");
                sb.AppendLine($"Query: {sql}");
                return sb.ToString();
            }

            // Format as markdown table
            sb.AppendLine($"Query returned {rows.Count} row(s):");
            sb.AppendLine();

            // Header
            sb.AppendLine("| " + string.Join(" | ", columns) + " |");
            sb.AppendLine("| " + string.Join(" | ", columns.Select(_ => "---")) + " |");

            // Rows
            foreach (var row in rows)
            {
                var cells = row.Select(v =>
                {
                    // Truncate long values for readability
                    if (v.Length > 80) return v.Substring(0, 77) + "...";
                    return v.Replace("|", "\\|"); // escape pipes
                });
                sb.AppendLine("| " + string.Join(" | ", cells) + " |");
            }

            _logger?.LogDebug($"[LocalDbQuery] Executed: {rows.Count} rows returned");

            return sb.ToString();
        }

        // ──────────────────────────────────────────────────────────────────
        // Template definition
        // ──────────────────────────────────────────────────────────────────

        private class QueryTemplate
        {
            public string[] Keywords { get; }
            public string Sql { get; }
            public bool AdminOnly { get; }

            public QueryTemplate(string[] keywords, string sql, bool adminOnly)
            {
                Keywords = keywords;
                Sql = sql;
                AdminOnly = adminOnly;
            }
        }
    }
}
