using System.Collections.Generic;

namespace BIManage.Revit.Protection
{
    /// <summary>
    /// Service for managing event protection settings
    /// </summary>
    public interface IEventProtectionService
    {
        /// <summary>
        /// Check if event protection is globally enabled
        /// </summary>
        bool IsProtectionEnabled { get; }

        /// <summary>
        /// Check if a specific protection is enabled by dummy command ID
        /// </summary>
        bool IsProtectionEnabledFor(string dummyCommandId);

        /// <summary>
        /// Get all protections for a specific event type
        /// </summary>
        IReadOnlyList<EventProtectionSettings> GetProtectionsForEvent(RevitEventType eventType);

        /// <summary>
        /// Get a specific protection by dummy command ID
        /// </summary>
        EventProtectionSettings? GetProtectionByDummyCommandId(string dummyCommandId);

        /// <summary>
        /// Get a specific protection by dummy command ID, falling back to the DB if the
        /// in-memory cache is not yet populated (e.g. called from OnDocumentOpening before
        /// LoadSettingsFromDatabase has run for this document).
        /// </summary>
        EventProtectionSettings? GetProtectionByDummyCommandIdDirect(string dummyCommandId);

        /// <summary>
        /// Get configuration for a specific protection
        /// </summary>
        T? GetConfiguration<T>(string dummyCommandId) where T : EventProtectionConfig;

        /// <summary>
        /// Load settings from database
        /// </summary>
        void LoadSettingsFromDatabase(int? projectId, string? modelGuid = null);

        /// <summary>
        /// Reload settings from database (call after UI makes changes)
        /// </summary>
        void RefreshSettings();

        /// <summary>
        /// Update a protection setting (runtime)
        /// </summary>
        void UpdateProtection(EventProtectionSettings setting);
    }
}
