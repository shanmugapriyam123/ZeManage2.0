namespace BIManage.Revit.Protection
{
    /// <summary>
    /// Result of event intervention
    /// </summary>
    public class EventInterventionResult
    {
        private EventInterventionResult(bool allowed, string? reason, bool userOverrode, string? userComment = null)
        {
            Allowed = allowed;
            Reason = reason;
            UserOverrode = userOverrode;
            UserComment = userComment;
        }

        /// <summary>
        /// Whether the event should be allowed
        /// </summary>
        public bool Allowed { get; }

        /// <summary>
        /// Reason for blocking or allowing (for audit)
        /// </summary>
        public string? Reason { get; }

        /// <summary>
        /// Whether user used admin override (OTP/password)
        /// </summary>
        public bool UserOverrode { get; }

        /// <summary>
        /// User-provided comment captured when RequireComment is true.
        /// </summary>
        public string? UserComment { get; }

        /// <summary>
        /// Allow event to proceed
        /// </summary>
        public static EventInterventionResult Allow(string? reason = null, string? userComment = null)
        {
            return new EventInterventionResult(true, reason, false, userComment);
        }

        /// <summary>
        /// Allow event with user override (admin approved)
        /// </summary>
        public static EventInterventionResult AllowWithOverride(string reason, string? userComment = null)
        {
            return new EventInterventionResult(true, reason, true, userComment);
        }

        /// <summary>
        /// Cancel/block event
        /// </summary>
        public static EventInterventionResult Cancel(string? reason = null, string? userComment = null)
        {
            return new EventInterventionResult(false, reason, false, userComment);
        }
    }
}
