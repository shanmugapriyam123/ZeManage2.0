namespace BIManage.Infrastructure.SignalR.Events
{
    /// <summary>
    /// Published via SignalREventBus when the server requests a token refresh.
    /// Triggered by role/permission changes — the client gets a fresh JWT with updated claims.
    /// Subscribers can react to permission changes (e.g. update ribbon availability).
    /// </summary>
    public class ForceTokenRefreshEvent : SignalREvent
    {
        /// <summary>
        /// Human-readable reason provided by the server (e.g. "Role updated").
        /// </summary>
        public string Reason { get; set; }

        /// <summary>
        /// True if the token refresh succeeded and a new JWT was obtained.
        /// </summary>
        public bool TokenRefreshed { get; set; }
    }
}
