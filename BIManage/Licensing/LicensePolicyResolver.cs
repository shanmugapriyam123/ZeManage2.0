using System;

namespace BIManage.Licensing
{
    /// <summary>
    /// Resolves how the application should behave based on license state.
    /// Module-aware: determines which features are allowed per license mode.
    /// </summary>
    public class LicensePolicyResolver
    {
        /// <summary>
        /// Returns true if the application should run at all (Licensed or Passive or GracePeriod).
        /// False only for Breached mode (expired/revoked/invalid).
        /// </summary>
        public bool AllowExecution(LicenseStatus status)
        {
            return status == LicenseStatus.Valid || status == LicenseStatus.GracePeriod;
        }

        /// <summary>
        /// Returns true if the application should run (any non-Breached mode).
        /// </summary>
        public bool AllowExecution(LicenseInfo info)
        {
            return info.Mode != LicenseMode.Breached;
        }

        /// <summary>
        /// Checks if a specific module is allowed under the current license.
        /// </summary>
        public bool IsModuleAllowed(LicenseInfo info, LicenseModule module)
        {
            return info.IsModuleEnabled(module);
        }

        /// <summary>
        /// Returns true if the user should see a warning about their license state.
        /// </summary>
        public bool ShouldWarnUser(LicenseStatus status)
        {
            return status == LicenseStatus.Expired
                || status == LicenseStatus.Revoked
                || status == LicenseStatus.Invalid
                || status == LicenseStatus.GracePeriod;
        }

        /// <summary>
        /// Returns true if the user should see a warning (mode-aware).
        /// </summary>
        public bool ShouldWarnUser(LicenseInfo info)
        {
            return info.Mode == LicenseMode.Passive
                || info.Mode == LicenseMode.Breached
                || info.Status == LicenseStatus.GracePeriod;
        }

        /// <summary>
        /// Returns true specifically for grace period warning display.
        /// </summary>
        public bool ShouldShowGracePeriodWarning(LicenseInfo info)
        {
            return info.Status == LicenseStatus.GracePeriod;
        }

        /// <summary>
        /// Returns a user-facing message describing the current license state.
        /// </summary>
        public string GetUserMessage(LicenseInfo info, TimeSpan? gracePeriodRemaining = null)
        {
            switch (info.Mode)
            {
                case LicenseMode.Licensed:
                    if (info.Status == LicenseStatus.GracePeriod && gracePeriodRemaining.HasValue)
                        return $"Server unreachable. Running in grace period — {gracePeriodRemaining.Value.TotalHours:F0} hours remaining.";
                    return "License is active.";

                case LicenseMode.Passive:
                    return $"License seats exceeded ({info.SeatUsageDisplay}). " +
                           "Running in passive mode — only Protection and Activity Tracking are active.";

                case LicenseMode.Breached:
                    if (info.Status == LicenseStatus.Expired)
                        return "Your license has expired. Please contact your administrator to renew.";
                    if (info.Status == LicenseStatus.Revoked)
                        return "Your license has been revoked. Please contact your administrator.";
                    return "No valid license found. Please register your device or contact your administrator.";

                default:
                    return "License status unknown.";
            }
        }
    }
}
