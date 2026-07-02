using System;
using System.Collections.Generic;

namespace BIManage.Revit.Protection
{
    /// <summary>
    /// Protection settings for a specific Revit event
    /// Maps to event_protection_settings table in SQLite
    /// Uses "Dummy Command IDs" pattern for consistent logging/tracking
    /// </summary>
    public class EventProtectionSettings
    {
        /// <summary>
        /// Database ID (GUID)
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Custom message image path
        /// </summary>
        public string? CustomMessageImagePath { get; set; }

        /// <summary>
        /// Revit event being protected (e.g., DocumentOpening, FamilyLoadingIntoDocument)
        /// </summary>
        public RevitEventType EventType { get; set; }

        /// <summary>
        /// Dummy Command ID for logging/tracking
        /// e.g., "Ze_OpenCentralFileProtection"
        /// </summary>
        public string DummyCommandId { get; set; } = string.Empty;

        /// <summary>
        /// Protection name for display (e.g., "Open Central File Protection")
        /// </summary>
        public string ProtectionName { get; set; } = string.Empty;

        /// <summary>
        /// Intervention mode (Monitor/Guide/Prevent)
        /// </summary>
        public InterventionMode Mode { get; set; } = InterventionMode.Notify;

        /// <summary>
        /// Whether protection is enabled
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Custom message for intervention dialog
        /// </summary>
        public string? CustomMessage { get; set; }

        /// <summary>
        /// Company-level setting (applies to all projects)
        /// </summary>
        public bool IsCompanyLevel { get; set; } = true;

        /// <summary>
        /// Configuration data (JSON) - event-specific settings
        /// e.g., approved locations list, protected file types, etc.
        /// </summary>
        public string? ConfigurationJson { get; set; }

        /// <summary>
        /// Capture screenshot before event
        /// </summary>
        public bool CaptureBeforeScreenshot { get; set; }

        /// <summary>
        /// Capture screenshot after event
        /// </summary>
        public bool CaptureAfterScreenshot { get; set; }

        /// <summary>
        /// Require user comment before proceeding
        /// </summary>
        public bool RequireComment { get; set; }

        /// <summary>
        /// Allow admin override without OTP
        /// </summary>
        public bool AllowAdminOverride { get; set; } = false;

        /// <summary>
        /// Send email notification when triggered
        /// </summary>
        public bool SendEmail { get; set; }

        /// <summary>
        /// Project ID for project-level assignment
        /// </summary>
        public string? ProjectId { get; set; }

        /// <summary>
        /// Company ID for company-level assignment
        /// </summary>
        public string? CompanyId { get; set; }

        /// <summary>
        /// Model GUID for model-specific assignment
        /// </summary>
        public string? ModelGuid { get; set; }

        /// <summary>
        /// Who created this setting
        /// </summary>
        public string? CreatedBy { get; set; }

        /// <summary>
        /// Who last modified this setting
        /// </summary>
        public string? ModifiedBy { get; set; }

        /// <summary>
        /// When this setting was last modified
        /// </summary>
        public DateTime? ModifiedAt { get; set; }

        /// <summary>
        /// Rule IDs for conditional activation — protection only triggers if at least one
        /// of these rules is active. Empty list means unconditional (always applies).
        /// </summary>
        public List<string> RuleIds { get; set; } = new List<string>();

        /// <summary>
        /// Tooltip/description text for this protection, shown on hover in the UI.
        /// Populated from the API's `toolTips` field.
        /// </summary>
        public string? ToolTips { get; set; }

        /// <summary>
        /// Server-computed: true when a Company-scoped row exists for this protection.
        /// Project Admins are not allowed to disable such rows from Revit (Company
        /// Admins manage company-scope policy). The dialog enforces this and shows
        /// a ZeMessageBox if a Project Admin tries to toggle off. In-memory only —
        /// not persisted to local SQLite; recomputed each fetch.
        /// </summary>
        public bool HasCompanyScope { get; set; }

        /// <summary>
        /// Create default settings (Monitor mode, disabled)
        /// </summary>
        public static EventProtectionSettings CreateDefault(
            RevitEventType eventType,
            string dummyCommandId,
            string protectionName)
        {
            return new EventProtectionSettings
            {
                EventType = eventType,
                DummyCommandId = dummyCommandId,
                ProtectionName = protectionName,
                Mode = InterventionMode.Notify,
                Enabled = false
            };
        }

        /// <summary>
        /// Create Monitor mode settings (enabled)
        /// </summary>
        public static EventProtectionSettings CreateMonitor(
            RevitEventType eventType,
            string dummyCommandId,
            string protectionName)
        {
            return new EventProtectionSettings
            {
                EventType = eventType,
                DummyCommandId = dummyCommandId,
                ProtectionName = protectionName,
                Mode = InterventionMode.Notify,
                Enabled = true
            };
        }

        /// <summary>
        /// Create Guide mode settings
        /// </summary>
        public static EventProtectionSettings CreateGuide(
            RevitEventType eventType,
            string dummyCommandId,
            string protectionName,
            string? message = null)
        {
            return new EventProtectionSettings
            {
                EventType = eventType,
                DummyCommandId = dummyCommandId,
                ProtectionName = protectionName,
                Mode = InterventionMode.Assist,
                Enabled = true,
                CustomMessage = message ?? "This action requires confirmation to proceed."
            };
        }

        /// <summary>
        /// Create Prevent mode settings
        /// </summary>
        public static EventProtectionSettings CreatePrevent(
            RevitEventType eventType,
            string dummyCommandId,
            string protectionName,
            string? message = null)
        {
            return new EventProtectionSettings
            {
                EventType = eventType,
                DummyCommandId = dummyCommandId,
                ProtectionName = protectionName,
                Mode = InterventionMode.Protect,
                Enabled = true,
                CustomMessage = message ?? "This action is restricted and requires authorization."
            };
        }
    }
}
