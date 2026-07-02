using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Persists AI chat sessions and messages to SQLite.
    /// Follows the same repository pattern as SessionRepository and EventRepository.
    /// </summary>
    public class ChatRepository : IDisposable
    {
        private readonly string _connectionString;
        private readonly ILogger? _logger;
        private bool _disposed;

        public ChatRepository(string databasePath, ILogger? logger = null)
        {
            _connectionString = SqliteConnectionHelper.BuildConnectionString(databasePath);
            _logger = logger;
        }

        // ──────────────────────────────────────────────────────────────────
        // Session CRUD
        // ──────────────────────────────────────────────────────────────────

        public async Task<string> CreateSessionAsync(
            string? revitSessionId, string? modelGuid, string? modelName, string? userName)
        {
            var chatSessionId = Guid.NewGuid().ToString("N");

            const string sql = @"
                INSERT INTO chat_sessions
                    (chat_session_id, session_id, model_guid, model_name, user_name, started_at, message_count)
                VALUES
                    (@id, @sessionId, @modelGuid, @modelName, @userName, @startedAt, 0)";

            try
            {
                using var conn = new SQLiteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new SQLiteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@id", chatSessionId);
                cmd.Parameters.AddWithValue("@sessionId", (object?)revitSessionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@modelGuid", (object?)modelGuid ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@modelName", (object?)modelName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@userName", (object?)userName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@startedAt", DateTime.Now.ToString("o"));
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ChatRepository] CreateSessionAsync failed: {ex.Message}", ex);
            }

            return chatSessionId;
        }

        public async Task EndSessionAsync(string chatSessionId)
        {
            const string sql = @"
                UPDATE chat_sessions
                SET ended_at = @endedAt,
                    message_count = (SELECT COUNT(*) FROM chat_messages WHERE chat_session_id = @id)
                WHERE chat_session_id = @id";

            try
            {
                using var conn = new SQLiteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new SQLiteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@id", chatSessionId);
                cmd.Parameters.AddWithValue("@endedAt", DateTime.Now.ToString("o"));
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ChatRepository] EndSessionAsync failed: {ex.Message}", ex);
            }
        }

        public async Task UpdateSessionTitleAsync(string chatSessionId, string title)
        {
            const string sql = "UPDATE chat_sessions SET title = @title WHERE chat_session_id = @id";

            try
            {
                using var conn = new SQLiteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new SQLiteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@id", chatSessionId);
                cmd.Parameters.AddWithValue("@title", title);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ChatRepository] UpdateSessionTitleAsync failed: {ex.Message}", ex);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Message CRUD
        // ──────────────────────────────────────────────────────────────────

        public async Task<string> SaveMessageAsync(
            string chatSessionId, string role, string content, int? responseTimeMs = null)
        {
            var messageId = Guid.NewGuid().ToString("N");
            var charCount = content?.Length ?? 0;
            var estimatedTokens = charCount / 4;

            const string sql = @"
                INSERT INTO chat_messages
                    (chat_message_id, chat_session_id, role, content, timestamp,
                     character_count, estimated_tokens, response_time_ms)
                VALUES
                    (@id, @sessionId, @role, @content, @timestamp,
                     @charCount, @tokens, @responseMs)";

            try
            {
                using var conn = new SQLiteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new SQLiteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@id", messageId);
                cmd.Parameters.AddWithValue("@sessionId", chatSessionId);
                cmd.Parameters.AddWithValue("@role", role);
                cmd.Parameters.AddWithValue("@content", content ?? string.Empty);
                cmd.Parameters.AddWithValue("@timestamp", DateTime.Now.ToString("o"));
                cmd.Parameters.AddWithValue("@charCount", charCount);
                cmd.Parameters.AddWithValue("@tokens", estimatedTokens);
                cmd.Parameters.AddWithValue("@responseMs", (object?)responseTimeMs ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ChatRepository] SaveMessageAsync failed: {ex.Message}", ex);
            }

            return messageId;
        }

        public async Task UpdateFeedbackAsync(string chatMessageId, int rating)
        {
            const string sql = "UPDATE chat_messages SET feedback_rating = @rating WHERE chat_message_id = @id";

            try
            {
                using var conn = new SQLiteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new SQLiteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@id", chatMessageId);
                cmd.Parameters.AddWithValue("@rating", rating);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ChatRepository] UpdateFeedbackAsync failed: {ex.Message}", ex);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Queries
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the most recent chat sessions for a specific Revit model (by GUID).
        /// Sessions with zero messages are excluded so empty/abandoned sessions don't crowd the
        /// history list. Pass null/empty <paramref name="modelGuid"/> to fall back to all sessions.
        /// </summary>
        public async Task<List<ChatSessionSummary>> GetRecentSessionsForModelAsync(string? modelGuid, int limit = 5)
        {
            var sessions = new List<ChatSessionSummary>();

            // The message_count column is only updated on EndSessionAsync, so for sessions that
            // are still in-progress (or ended without proper cleanup) we count chat_messages
            // directly via a sub-select. This also lets us filter out empty sessions reliably.
            const string sql = @"
                SELECT s.chat_session_id, s.model_name, s.user_name, s.started_at, s.ended_at,
                       (SELECT COUNT(*) FROM chat_messages m WHERE m.chat_session_id = s.chat_session_id) AS msg_count,
                       s.title
                FROM chat_sessions s
                WHERE (@modelGuid IS NULL OR s.model_guid = @modelGuid)
                  AND (SELECT COUNT(*) FROM chat_messages m WHERE m.chat_session_id = s.chat_session_id) > 0
                ORDER BY s.started_at DESC
                LIMIT @limit";

            try
            {
                using var conn = new SQLiteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new SQLiteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@modelGuid",
                    string.IsNullOrEmpty(modelGuid) ? (object)DBNull.Value : modelGuid);
                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    sessions.Add(new ChatSessionSummary
                    {
                        ChatSessionId = reader.GetString(0),
                        ModelName = reader.IsDBNull(1) ? null : reader.GetString(1),
                        UserName = reader.IsDBNull(2) ? null : reader.GetString(2),
                        StartedAt = DateTime.Parse(reader.GetString(3)),
                        EndedAt = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
                        MessageCount = reader.GetInt32(5),
                        Title = reader.IsDBNull(6) ? null : reader.GetString(6)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ChatRepository] GetRecentSessionsForModelAsync failed: {ex.Message}", ex);
            }

            return sessions;
        }

        public async Task<List<ChatSessionSummary>> GetRecentSessionsAsync(int limit = 20)
        {
            var sessions = new List<ChatSessionSummary>();

            const string sql = @"
                SELECT chat_session_id, model_name, user_name, started_at, ended_at, message_count, title
                FROM chat_sessions
                ORDER BY started_at DESC
                LIMIT @limit";

            try
            {
                using var conn = new SQLiteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new SQLiteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    sessions.Add(new ChatSessionSummary
                    {
                        ChatSessionId = reader.GetString(0),
                        ModelName = reader.IsDBNull(1) ? null : reader.GetString(1),
                        UserName = reader.IsDBNull(2) ? null : reader.GetString(2),
                        StartedAt = DateTime.Parse(reader.GetString(3)),
                        EndedAt = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
                        MessageCount = reader.GetInt32(5),
                        Title = reader.IsDBNull(6) ? null : reader.GetString(6)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ChatRepository] GetRecentSessionsAsync failed: {ex.Message}", ex);
            }

            return sessions;
        }

        public async Task<List<ChatMessageRecord>> GetSessionMessagesAsync(string chatSessionId)
        {
            var messages = new List<ChatMessageRecord>();

            const string sql = @"
                SELECT chat_message_id, role, content, timestamp, response_time_ms, feedback_rating
                FROM chat_messages
                WHERE chat_session_id = @sessionId
                ORDER BY timestamp ASC";

            try
            {
                using var conn = new SQLiteConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new SQLiteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@sessionId", chatSessionId);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    messages.Add(new ChatMessageRecord
                    {
                        ChatMessageId = reader.GetString(0),
                        Role = reader.GetString(1),
                        Content = reader.GetString(2),
                        Timestamp = DateTime.Parse(reader.GetString(3)),
                        ResponseTimeMs = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        FeedbackRating = reader.IsDBNull(5) ? null : reader.GetInt32(5)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ChatRepository] GetSessionMessagesAsync failed: {ex.Message}", ex);
            }

            return messages;
        }

        public async Task DeleteSessionAsync(string chatSessionId)
        {
            try
            {
                using var conn = new SQLiteConnection(_connectionString);
                await conn.OpenAsync();

                using var delMsgs = new SQLiteCommand(
                    "DELETE FROM chat_messages WHERE chat_session_id = @id", conn);
                delMsgs.Parameters.AddWithValue("@id", chatSessionId);
                await delMsgs.ExecuteNonQueryAsync();

                using var delSession = new SQLiteCommand(
                    "DELETE FROM chat_sessions WHERE chat_session_id = @id", conn);
                delSession.Parameters.AddWithValue("@id", chatSessionId);
                await delSession.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ChatRepository] DeleteSessionAsync failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Deletes ALL chat sessions and messages for a given model GUID (or every session
        /// when modelGuid is null/empty). Used by the "Clear all chat history" UI option.
        /// </summary>
        public async Task<int> DeleteAllSessionsForModelAsync(string? modelGuid)
        {
            try
            {
                using var conn = new SQLiteConnection(_connectionString);
                await conn.OpenAsync();

                // Delete child messages first (FK-safe even though there's no real FK here).
                using var delMsgs = new SQLiteCommand(
                    @"DELETE FROM chat_messages
                      WHERE chat_session_id IN (
                          SELECT chat_session_id FROM chat_sessions
                          WHERE (@modelGuid IS NULL OR model_guid = @modelGuid))", conn);
                delMsgs.Parameters.AddWithValue("@modelGuid",
                    string.IsNullOrEmpty(modelGuid) ? (object)DBNull.Value : modelGuid);
                await delMsgs.ExecuteNonQueryAsync();

                using var delSessions = new SQLiteCommand(
                    "DELETE FROM chat_sessions WHERE (@modelGuid IS NULL OR model_guid = @modelGuid)", conn);
                delSessions.Parameters.AddWithValue("@modelGuid",
                    string.IsNullOrEmpty(modelGuid) ? (object)DBNull.Value : modelGuid);
                return await delSessions.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ChatRepository] DeleteAllSessionsForModelAsync failed: {ex.Message}", ex);
                return 0;
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // IDisposable
        // ──────────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // DTOs
    // ──────────────────────────────────────────────────────────────────

    public class ChatSessionSummary
    {
        public string ChatSessionId { get; set; } = string.Empty;
        public string? ModelName { get; set; }
        public string? UserName { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public int MessageCount { get; set; }
        public string? Title { get; set; }
    }

    public class ChatMessageRecord
    {
        public string ChatMessageId { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public int? ResponseTimeMs { get; set; }
        public int? FeedbackRating { get; set; }
    }
}
