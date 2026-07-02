namespace BIManage.Licensing
{
    /// <summary>
    /// The 5 individually licensable product modules.
    /// Values match the server API response for enabled module identification.
    /// </summary>
    public enum LicenseModule
    {
        Protection = 1,
        ActivityTracker = 2,
        HealthMonitor = 3,
        SyncControl = 4,
        AI = 5
    }
}
