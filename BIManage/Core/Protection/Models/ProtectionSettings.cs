using System;
using BIManage.Core.Rules.Models;

namespace BIManage.Core.Protection.Models
{
    /// <summary>
    /// Represents protection settings including password credentials
    /// </summary>
    public class ProtectionSettings
    {
        public int Id { get; set; }
        public string PasswordHash { get; set; }
        public string Salt { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime UpdatedDate { get; set; }
        public bool IsPasswordSet => !string.IsNullOrEmpty(PasswordHash);
    }

    /// <summary>
    /// Represents an audit log entry for protection actions
    /// Includes role tracking for RBAC compliance
    /// </summary>
    public class ProtectionAuditEntry
    {
        // GUID primary key — generated client-side, matches backend auditLogId
        public string AuditLogId { get; set; }
        public DateTime Timestamp { get; set; }
        public string UserName { get; set; }
        public bool WasCompanyAdmin { get; set; }
        public bool WasProjectAdmin { get; set; }

        // Model context — identifies which model the action occurred on
        public string ModelGuid { get; set; }

        // Protection source — stores the rule_id, command_settings id, event_protection id, or pin_protection id
        public string ProtectionId { get; set; }
        public string CommandName { get; set; }
        public ProtectionMode Mode { get; set; }
        public ProtectionAction Action { get; set; }
        public string ElementIds { get; set; }
        public int ElementCount { get; set; }

        // Element metadata — describes the primary element involved in the protection event
        public string? ElementCategory { get; set; }
        public string? ElementFamilyType { get; set; }
        public string? ElementName { get; set; }

        public string Reason { get; set; }
        public string UserComment { get; set; }

        // Event source tracking — identifies which protection system logged the event.
        // Values map 1:1 to the four user-facing ribbon panels (display strings, not enum
        // names): "Pin Protection", "Command Protection", "Event Restriction", "Rule Management".
        // Older rows may still hold the legacy single-word values (CommandProtection, etc.);
        // SchemaMigration.NormalizeAuditEventSource normalises them in place on next launch.
        public string EventSource { get; set; }

        // Override tracking — "OTP", "AdminPassword", or null (replaces admin_overridden + password_override_used)
        public string OverrideMethod { get; set; }

        // Session tracking — links events to Revit session
        public string SessionId { get; set; }

        // Mail dispatch — server polls sent_mail=0 rows to compose and send email notifications
        // false (0) = pending dispatch (default); true (1) = dispatched or no email needed
        // Mail is triggered by the plugin AFTER all evidence (screenshots) are uploaded,
        // via POST /api/v1/Revit/audit-logs/{id}/send-mail
        public bool SentMail { get; set; } = false;
        // When this entry was marked ready for server mail pickup
        public DateTime? MailQueuedAt { get; set; }

        // Server sync tracking — false (0) = pending upload, true (1) = synced
        public bool Synced { get; set; } = false;
    }

    /// <summary>
    /// Represents the action taken during protection enforcement
    /// </summary>
    public enum ProtectionAction
    {
        Allowed,
        Blocked,
        Cancelled,
        Override,
        /// <summary>
        /// Action was detected but not blocked (e.g., bypass detection in Monitor mode)
        /// </summary>
        Detected
    }

    /// <summary>
    /// Admin credentials for password management
    /// </summary>
    public class AdminCredentials
    {
        public string CurrentPassword { get; set; }
        public string NewPassword { get; set; }
        public string ConfirmPassword { get; set; }

        public bool IsValid()
        {
            if (string.IsNullOrWhiteSpace(NewPassword))
                return false;

            if (NewPassword != ConfirmPassword)
                return false;

            if (NewPassword.Length < 6)
                return false;

            return true;
        }

        public PasswordStrength GetStrength()
        {
            if (string.IsNullOrWhiteSpace(NewPassword))
                return PasswordStrength.None;

            var length = NewPassword.Length;
            var hasUpper = false;
            var hasLower = false;
            var hasDigit = false;
            var hasSpecial = false;

            foreach (var c in NewPassword)
            {
                if (char.IsUpper(c)) hasUpper = true;
                if (char.IsLower(c)) hasLower = true;
                if (char.IsDigit(c)) hasDigit = true;
                if (!char.IsLetterOrDigit(c)) hasSpecial = true;
            }

            var score = 0;
            if (length >= 6) score++;
            if (length >= 8) score++;
            if (length >= 12) score++;
            if (hasUpper) score++;
            if (hasLower) score++;
            if (hasDigit) score++;
            if (hasSpecial) score++;

            if (score <= 2) return PasswordStrength.Weak;
            if (score <= 4) return PasswordStrength.Medium;
            if (score <= 6) return PasswordStrength.Strong;
            return PasswordStrength.VeryStrong;
        }
    }

    public enum PasswordStrength
    {
        None,
        Weak,
        Medium,
        Strong,
        VeryStrong
    }

    /// <summary>
    /// Represents a structured admin override with metadata and audit trail
    /// Supports scope-based, time-bound overrides for granular control
    /// </summary>
    public class ProtectionOverride
    {
        public int Id { get; set; }
        public string OverrideId { get; set; }
        public string RuleId { get; set; }
        public string CommandId { get; set; }
        public string DocumentId { get; set; }
        public OverrideScope Scope { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string ApproverEmail { get; set; }
        public string ApproverName { get; set; }
        public string Reason { get; set; }
        public bool IsActive { get; set; }
        public DateTime? RevokedAt { get; set; }
        public string RevokedBy { get; set; }

        /// <summary>
        /// Check if this override is currently valid
        /// </summary>
        public bool IsValid()
        {
            if (!IsActive)
                return false;

            if (ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value)
                return false;

            return true;
        }

        /// <summary>
        /// Check if this override applies to a specific context
        /// </summary>
        public bool AppliesTo(string ruleId, string commandId, string documentId)
        {
            switch (Scope)
            {
                case OverrideScope.Global:
                    return true;

                case OverrideScope.Rule:
                    return RuleId == ruleId;

                case OverrideScope.Command:
                    return CommandId == commandId;

                case OverrideScope.Document:
                    return DocumentId == documentId;

                case OverrideScope.RuleAndCommand:
                    return RuleId == ruleId && CommandId == commandId;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Create a new override with standard defaults
        /// </summary>
        public static ProtectionOverride Create(
            string ruleId,
            string commandId,
            OverrideScope scope,
            string approverEmail,
            string approverName,
            string reason,
            TimeSpan? duration = null)
        {
            var overrideId = Guid.NewGuid().ToString();
            var now = DateTime.UtcNow;

            return new ProtectionOverride
            {
                OverrideId = overrideId,
                RuleId = ruleId,
                CommandId = commandId,
                Scope = scope,
                CreatedAt = now,
                ExpiresAt = duration.HasValue ? now.Add(duration.Value) : (DateTime?)null,
                ApproverEmail = approverEmail,
                ApproverName = approverName,
                Reason = reason,
                IsActive = true
            };
        }
    }

    /// <summary>
    /// Defines the scope of a protection override
    /// </summary>
    public enum OverrideScope
    {
        /// <summary>
        /// Override applies globally to all commands and rules
        /// </summary>
        Global = 0,

        /// <summary>
        /// Override applies to a specific rule only
        /// </summary>
        Rule = 1,

        /// <summary>
        /// Override applies to a specific command only
        /// </summary>
        Command = 2,

        /// <summary>
        /// Override applies to a specific document only
        /// </summary>
        Document = 3,

        /// <summary>
        /// Override applies to a specific rule and command combination
        /// </summary>
        RuleAndCommand = 4
    }
}
