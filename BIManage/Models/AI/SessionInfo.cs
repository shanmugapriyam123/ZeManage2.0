using System;
using System.Collections.Generic;
using System.Linq;

namespace BIManage.Models.AI
{
    /// <summary>
    /// Tracks session information for analytics and logging.
    /// </summary>
    public class SessionInfo
    {
        public string SessionId { get; set; }
        public string UserName { get; set; }
        public DateTime SessionStart { get; set; }
        public DateTime? SessionEnd { get; set; }
        public int UserPromptCount { get; set; }
        public int AIResponseCount { get; set; }
        public List<MessageMetadata> Messages { get; set; }

        public int TotalInputTokens => Messages
            .Where(m => m.Role == "user")
            .Sum(m => m.EstimatedTokens);

        public int TotalOutputTokens => Messages
            .Where(m => m.Role == "assistant")
            .Sum(m => m.EstimatedTokens);

        public TimeSpan SessionDuration => SessionEnd.HasValue
            ? SessionEnd.Value - SessionStart
            : DateTime.Now - SessionStart;

        public SessionInfo()
        {
            SessionId = Guid.NewGuid().ToString();
            UserName = Environment.UserName;
            SessionStart = DateTime.Now;
            UserPromptCount = 0;
            AIResponseCount = 0;
            Messages = new List<MessageMetadata>();
        }

        public void EndSession()
        {
            SessionEnd = DateTime.Now;
        }
    }

    /// <summary>
    /// Metadata for individual messages.
    /// </summary>
    public class MessageMetadata
    {
        public DateTime Timestamp { get; set; }
        public string Role { get; set; } // "user" or "assistant"
        public int CharacterCount { get; set; }
        public int EstimatedTokens { get; set; }
        public double ResponseTimeMs { get; set; }

        public MessageMetadata(string role, string content, double responseTimeMs = 0)
        {
            Timestamp = DateTime.Now;
            Role = role;
            CharacterCount = content?.Length ?? 0;
            EstimatedTokens = CharacterCount / 4;
            ResponseTimeMs = responseTimeMs;
        }
    }
}
