namespace BIManage.Revit.EventRegistry
{
    /// <summary>
    ///     Interface for centralized toggle checks for Revit event subscriptions.
    /// </summary>
    public interface IEventTogglePolicy
    {
        /// <summary>
        ///     Checks if document lifecycle events should be monitored.
        /// </summary>
        /// <returns>True if document monitoring is enabled, false otherwise</returns>
        bool ShouldMonitorDocuments();

        /// <summary>
        ///     Checks if command events should be monitored.
        /// </summary>
        /// <returns>True if command monitoring is enabled, false otherwise</returns>
        bool ShouldMonitorCommands();

        /// <summary>
        ///     Checks if idling events should be monitored.
        /// </summary>
        /// <returns>True if idling monitoring is enabled, false otherwise</returns>
        bool ShouldMonitorIdling();

        /// <summary>
        ///     Checks if UI tracking (view and selection changes) should be monitored.
        /// </summary>
        /// <returns>True if UI tracking is enabled, false otherwise</returns>
        bool ShouldMonitorUITracking();
    }
}
