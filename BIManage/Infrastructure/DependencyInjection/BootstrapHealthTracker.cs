using System;

namespace BIManage.Infrastructure.DependencyInjection
{
    /// <summary>
    /// Tracks which service groups initialized successfully during bootstrap.
    /// Enables deferred recovery for services that failed (e.g., API unavailable at startup).
    /// Registered as singleton — accessible from IdlingService for periodic recovery attempts.
    /// </summary>
    public class BootstrapHealthTracker
    {
        public bool PersistenceReady { get; set; }
        public bool ApiServicesReady { get; set; }
        public bool SignalRReady { get; set; }
        public bool LicenseValidated { get; set; }
        public bool SessionSynced { get; set; }
        public string? LastError { get; set; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;

        /// <summary>True when all critical services are initialized.
        /// Must include every flag <see cref="NeedsRecovery"/> checks — otherwise the
        /// recovery code can fire (because a flag is false) while <see cref="GetStatusSummary"/>
        /// simultaneously reports "All services healthy", producing the contradictory log
        /// line "[Recovery] Bootstrap incomplete — attempting recovery (status: All services healthy)"
        /// observed on every fresh startup.</summary>
        public bool IsFullyHealthy => PersistenceReady && ApiServicesReady && SessionSynced && LicenseValidated;

        /// <summary>True when deferred recovery should be attempted.</summary>
        public bool NeedsRecovery => !ApiServicesReady || !SessionSynced || !LicenseValidated;

        /// <summary>Number of recovery attempts made so far.</summary>
        public int RecoveryAttempts { get; set; }

        /// <summary>Max recovery attempts before giving up (stop wasting cycles).</summary>
        public const int MaxRecoveryAttempts = 12; // 12 × 5min = 1 hour

        public bool ShouldAttemptRecovery => NeedsRecovery && RecoveryAttempts < MaxRecoveryAttempts;

        public string GetStatusSummary()
        {
            if (IsFullyHealthy) return "All services healthy";

            var issues = new System.Text.StringBuilder();
            if (!PersistenceReady) issues.Append("Database not ready. ");
            if (!ApiServicesReady) issues.Append("API services not available. ");
            if (!LicenseValidated) issues.Append("License not validated. ");
            if (!SessionSynced) issues.Append("Session not synced. ");
            if (!SignalRReady) issues.Append("SignalR not connected. ");
            if (!string.IsNullOrEmpty(LastError)) issues.Append($"Last error: {LastError}");
            return issues.ToString().TrimEnd();
        }
    }
}
