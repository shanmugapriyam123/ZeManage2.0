namespace BIManage.Core.Sessions
{
    /// <summary>Integer status codes for the sessions.status_code column.</summary>
    public enum SessionStatusCode
    {
        Closed   = 0,   // Normal shutdown — OnShutdown fired
        Active   = 1,   // Currently running
        Crashed  = 2,   // Process dead + definitive crash evidence in journal
        Inactive = 3,   // Heartbeat silent 4+ hours (process may still be running)
        Unknown  = 4    // Process dead + no crash evidence (force-kill, power loss)
    }

    /// <summary>Integer status codes for the document_sessions.status_code column.</summary>
    public enum DocumentSessionStatusCode
    {
        Closed  = 0,   // Closed normally, or session was Unknown/Inactive
        Active  = 1,   // Currently open
        Crashed = 2    // Was the active model at last heartbeat when session crashed
    }
}
