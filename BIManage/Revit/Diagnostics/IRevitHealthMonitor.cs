namespace BIManage.Revit.Diagnostics
{
    /// <summary>
    ///     Interface for lightweight health tracker for startup/shutdown.
    /// </summary>
    public interface IRevitHealthMonitor
    {
        /// <summary>
        ///     Records the add-in startup event.
        /// </summary>
        void RecordStartup();

        /// <summary>
        ///     Records the add-in shutdown event.
        /// </summary>
        void RecordShutdown();
    }
}
