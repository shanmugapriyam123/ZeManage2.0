namespace BIManage.Infrastructure.SignalR.Events
{
    /// <summary>
    /// Published via SignalREventBus when the server forces a client logout.
    /// Triggered by admin-initiated session revocation (access deactivation, user deletion, etc.).
    /// Subscribers should clear UI identity, update ribbon, and show a logout message.
    /// </summary>
    public class ForceLogoutEvent : SignalREvent
    {
        /// <summary>
        /// Human-readable reason provided by the server (e.g. "Access revoked by administrator").
        /// </summary>
        public string Reason { get; set; }
    }
}
