namespace BIManage.Licensing
{
    public enum LicenseStatus
    {
        Unknown = 0,
        Valid = 1,
        Expired = 2,
        Revoked = 3,
        Invalid = 4,
        GracePeriod = 5,
        /// <summary>
        /// Authenticated but first heartbeat not yet received. Transient at startup.
        /// Features remain gated (as if Breached) until UpdateFromHeartbeatResponse
        /// resolves the mode to Licensed/Passive/Breached.
        /// </summary>
        Pending = 6
    }
}
