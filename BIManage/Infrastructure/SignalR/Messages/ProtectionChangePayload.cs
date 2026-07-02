using System;

namespace BIManage.Infrastructure.SignalR.Messages
{
    /// <summary>
    /// Payload received via SignalR when a protection setting changes on the backend.
    /// Carries scope info so the client knows which open models need refreshing.
    /// </summary>
    public class ProtectionChangePayload
    {
        /// <summary>
        /// "Command", "Event", "Rule", or "Pin"
        /// </summary>
        public string ProtectionType { get; set; }

        /// <summary>
        /// "Created", "Updated", or "Deleted"
        /// </summary>
        public string ChangeType { get; set; }

        /// <summary>
        /// 1 = Company-wide, 2 = Project-level, 3 = Model-level
        /// </summary>
        public int LevelScope { get; set; }

        /// <summary>
        /// Populated when LevelScope is project-level (2)
        /// </summary>
        public string ProjectId { get; set; }

        /// <summary>
        /// Populated when LevelScope is model-level (3)
        /// </summary>
        public string ModelGuid { get; set; }

        /// <summary>
        /// The specific protection entity ID that changed
        /// </summary>
        public string EntityId { get; set; }

        public DateTime Timestamp { get; set; }
    }
}
