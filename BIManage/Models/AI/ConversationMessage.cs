using System;

namespace BIManage.Models.AI
{
    /// <summary>
    /// Represents a single message in the conversation history.
    /// </summary>
    public class ConversationMessage
    {
        public string Role { get; set; } // "user" or "assistant"
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }

        public ConversationMessage(string role, string content)
        {
            Role = role;
            Content = content;
            Timestamp = DateTime.Now;
        }
    }
}
