namespace BIManage.Licensing
{
    /// <summary>
    /// The 3 operational license modes displayed via ribbon traffic-light icon.
    /// </summary>
    public enum LicenseMode
    {
        /// <summary>Green icon — all purchased modules active, within seat limit.</summary>
        Licensed,

        /// <summary>Yellow icon — seat limit exceeded, only Protection + Activity Tracker active.</summary>
        Passive,

        /// <summary>Red icon — license expired/revoked/invalid, no modules active.</summary>
        Breached
    }
}
