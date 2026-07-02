namespace BIManage.Infrastructure.SignalR.Events
{
    /// <summary>
    /// Published via SignalREventBus when a SyncQueueUpdate message is received.
    /// SyncQueueViewModel subscribes to add the joining user as an "InQueue" row.
    /// User fields are populated from the SignalR payload when the server forwards
    /// the join announcement (SyncQueueJoin → SyncQueueUpdate fan-out).
    /// </summary>
    public class SyncQueueUpdateEvent : SignalREvent
    {
        public string ModelGuid { get; set; }
        public object Payload { get; set; }
        public string SessionId { get; set; }
        public string Username { get; set; }
        public string RevitUsername { get; set; }
        public string ComputerName { get; set; }

        /// <summary>
        /// True when this event is the result of a 1-second post-start collision check
        /// detecting that another user began syncing the same model simultaneously.
        /// Bypasses the normal self-session filter so the local user's history row
        /// can be shown as "InQueue".
        /// </summary>
        public bool SelfCollision { get; set; }

        /// <summary>
        /// Server-stamped UTC timestamp when the hub processed this event.
        /// DateTime.MinValue means "no info" — receivers should accept (backward-compat
        /// with older server builds that don't stamp). Used by SyncQueueViewModel
        /// to reject stale echoes that would otherwise revert a Completed row to Syncing.
        /// </summary>
        public DateTime OccurredAtUtc { get; set; }
    }

    /// <summary>
    /// Published via SignalREventBus when a SyncStarting message is received.
    /// Carries user info so the Sync Queue dialog can show who is currently syncing.
    /// </summary>
    public class SyncStartingEvent : SignalREvent
    {
        public string ModelGuid { get; set; }
        public object Payload { get; set; }
        public string SessionId { get; set; }
        public string Username { get; set; }
        public string RevitUsername { get; set; }
        public string ComputerName { get; set; }

        /// <summary>Server-stamped UTC timestamp; DateTime.MinValue when absent.</summary>
        public DateTime OccurredAtUtc { get; set; }
    }

    /// <summary>
    /// Published via SignalREventBus when a SyncCompleted message is received.
    /// Carries user info so the Sync Queue dialog can label completed syncs even when
    /// the dialog opened after the corresponding SyncStarting event.
    /// </summary>
    public class SyncCompletedEvent : SignalREvent
    {
        public string ModelGuid { get; set; }
        public object Payload { get; set; }
        public string SessionId { get; set; }
        public string Username { get; set; }
        public string RevitUsername { get; set; }
        public string ComputerName { get; set; }

        /// <summary>Server-stamped UTC timestamp; DateTime.MinValue when absent.</summary>
        public DateTime OccurredAtUtc { get; set; }
    }

    /// <summary>
    /// Published via SignalREventBus when a SyncCancelled message is received.
    /// </summary>
    public class SyncCancelledEvent : SignalREvent
    {
        public string ModelGuid { get; set; }
        public string SessionId { get; set; }
        public string Username { get; set; }
        public string RevitUsername { get; set; }
        public string ComputerName { get; set; }

        /// <summary>Server-stamped UTC timestamp; DateTime.MinValue when absent.</summary>
        public DateTime OccurredAtUtc { get; set; }
    }

    /// <summary>
    /// Published via SignalREventBus when a SyncRequest message is received.
    /// Can be used to prompt the local Revit client to initiate a sync.
    /// </summary>
    public class SyncRequestEvent : SignalREvent
    {
        public string ModelGuid { get; set; }
        public string TargetSessionId { get; set; }
        public object Payload { get; set; }
    }

    /// <summary>
    /// Published via SignalREventBus when a SyncStarting message arrives carrying
    /// QueueAction="Claim". The sender is announcing intent to sync; receivers record
    /// the claim and use it during the ~600ms arbitration window in SyncCommandBinding
    /// to decide who actually proceeds when two team members click Sync within the
    /// SignalR round-trip window. Earliest <see cref="ClaimedAtUtc"/> wins; ties broken
    /// by lex compare on SessionId so every client picks the same winner.
    /// </summary>
    public class SyncClaimEvent : SignalREvent
    {
        public string ModelGuid { get; set; }
        public string SessionId { get; set; }
        public string Username { get; set; }
        public string RevitUsername { get; set; }
        public string ComputerName { get; set; }
        public DateTime ClaimedAtUtc { get; set; }
    }

    /// <summary>
    /// Published when the server has granted this client a sync slot in response to a
    /// prior RequestSyncSlot. Cloud sync gate (Option D) — caller awaits this via
    /// SyncSlotResponseCoordinator to know whether to proceed with the sync.
    /// </summary>
    public class SyncSlotGrantedEvent : SignalREvent
    {
        public string ModelGuid { get; set; }
        public string SessionId { get; set; }
    }

    /// <summary>
    /// Published when the server has denied this client a sync slot — another peer
    /// holds the active slot. Carries queue position so the client UX can convey wait.
    /// SyncReadyDialog will surface later when the server emits SyncRequest for our turn.
    /// </summary>
    public class SyncSlotDeniedEvent : SignalREvent
    {
        public string ModelGuid { get; set; }
        public string SessionId { get; set; }
        public string Reason { get; set; }
        public int QueuePosition { get; set; }
        public int ExpectedWaitSecs { get; set; }
        public string BlockingUsername { get; set; }
    }
}
