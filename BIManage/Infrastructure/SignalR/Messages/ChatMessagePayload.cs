using System;

namespace BIManage.Infrastructure.SignalR.Messages
{
    /// <summary>
    /// Payload for real-time chat messages between users via SignalR.
    /// Scope: "model" (all model users), "project" (all project users), "direct" (targeted by username).
    /// Matches the API's ChatMessagePayload exactly.
    /// </summary>
    public class ChatMessagePayload
    {
        public string Scope { get; set; }              // "model", "project", "direct"
        public string ModelGuid { get; set; }
        public string ProjectId { get; set; }
        public string TargetUsername { get; set; }      // For direct scope only
        public string SenderUsername { get; set; }
        public string SenderDisplayName { get; set; }
        public string Text { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
