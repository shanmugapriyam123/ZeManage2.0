using System;
using System.Collections.Generic;
using System.Linq;

namespace BIManage.Core.Evidence
{
    /// <summary>
    /// Stores event data including before/after screenshots for compliance logging.
    /// </summary>
    public class UserEventData
    {
        // Identity
        public string AuditLogId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string DocumentPath { get; set; } = string.Empty;

        // User context
        public string Username { get; set; } = string.Empty;
        public bool IsAdmin { get; set; }

        // Event details
        public string EventType { get; set; } = string.Empty;  // e.g., "pin_protection"
        public string CommandName { get; set; } = string.Empty; // e.g., "Unpin"
        public List<long> ElementIds { get; set; } = new();
        public int ElementCount => ElementIds.Count;

        // Timestamps
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }

        // Screenshots (Base64-encoded PNG)
        public string? BeforeImageBytes { get; set; }
        public string? AfterImageBytes { get; set; }

        // Status
        public string Status { get; set; } = "Pending"; // Pending, Completed, Cancelled
        public string? UserComment { get; set; }
        public string? OtpCodeUsed { get; set; }

        /// <summary>
        /// Factory method for pin protection events
        /// </summary>
        public static UserEventData CreateForPinProtection(
            string auditLogId,
            string sessionId,
            string documentPath,
            string username,
            bool isAdmin,
            IEnumerable<long> elementIds)
        {
            return new UserEventData
            {
                AuditLogId = auditLogId,
                SessionId = sessionId,
                DocumentPath = documentPath,
                Username = username,
                IsAdmin = isAdmin,
                EventType = "pin_protection",
                CommandName = "Unpin",
                ElementIds = elementIds.ToList()
            };
        }
    }
}
