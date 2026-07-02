using System;
using System.Collections.Generic;

namespace BIManage.Infrastructure.SignalR.Messages
{
    /// <summary>
    /// Revit-side DTO matching the API's UserPresence payload.
    /// Deserialized from UserJoined / UserLeft / ActiveUsersUpdate SignalR messages.
    /// </summary>
    public class UserPresencePayload
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public string Username { get; set; }
        public string RevitUsername { get; set; }
        public string UserEmail { get; set; }
        public string ComputerName { get; set; }
        public string ModelGuid { get; set; }
        public string ProjectId { get; set; }
        public DateTime JoinedAt { get; set; }

        /// <summary>
        /// Optional marker used by the queue piggyback protocol. When set to "JoinQueue",
        /// "LeaveQueue", or "Claim" on a SyncStarting payload, the message is NOT a real
        /// sync start but a piggybacked announcement on the (reliably fanned-out)
        /// SyncStarting channel. The listener routes such messages to SyncQueueUpdateEvent
        /// or SyncClaimEvent based on the value, so receivers don't mistakenly treat the
        /// sender as the active syncer.
        /// </summary>
        public string QueueAction { get; set; }

        /// <summary>
        /// Sender's local UTC ticks at the moment they pressed Sync. Populated only on
        /// payloads with QueueAction="Claim". Used by peers to arbitrate concurrent claims
        /// — earliest ticks wins; lex compare on SessionId breaks ties. Arrival time is
        /// not safe here because network latency varies per peer, so two peers would
        /// otherwise disagree on the winner. Trust each machine's NTP-synced clock.
        /// </summary>
        public long ClaimedAtUtcTicks { get; set; }

        /// <summary>
        /// Server-stamped UTC tick count for when the hub processed this event.
        /// Set on SyncStarting / SyncCompleted / SyncCancelled before fan-out so clients
        /// can reject stale/out-of-order broadcasts (Phase 7 Issue 1 — a late SyncStarting
        /// echo arriving after its matching SyncCompleted would otherwise flip a row from
        /// Completed back to Syncing). 0 means "no info" (older server build) — clients
        /// treat 0 as "accept" so the change is backward-compat.
        /// </summary>
        public long OccurredAtUtcTicks { get; set; }
    }

    /// <summary>
    /// Bulk active-users snapshot for a model.
    /// Deserialized from the ActiveUsersUpdate SignalR message.
    /// </summary>
    public class ActiveUsersPayload
    {
        public string ModelGuid { get; set; }
        public List<UserPresencePayload> Users { get; set; } = new List<UserPresencePayload>();
    }

    /// <summary>
    /// Minimal payload for UserLeft — only SessionId and ModelGuid are required.
    /// </summary>
    public class UserLeftPayload
    {
        public string SessionId { get; set; }
        public string ModelGuid { get; set; }
    }

    /// <summary>
    /// Payload for joining the sync queue (client → server).
    /// </summary>
    public class SyncQueueJoinPayload
    {
        public string ModelGuid { get; set; }
        public string SessionId { get; set; }
        public string Username { get; set; }
        public string RevitUsername { get; set; }
    }

    /// <summary>
    /// Payload for leaving the sync queue (client → server).
    /// </summary>
    public class SyncQueueLeavePayload
    {
        public string ModelGuid { get; set; }
        public string SessionId { get; set; }
    }

    /// <summary>
    /// Payload for SyncRequest (server → client). Targets a specific session.
    /// </summary>
    public class SyncRequestPayload
    {
        public string ModelGuid { get; set; }
        public string TargetSessionId { get; set; }
    }

    /// <summary>
    /// Payload for RequestSyncSlot (client → server). The server-arbitrated cloud
    /// sync gate (Option D): client asks for a sync slot before letting Revit's
    /// CloudWorksharing.UI pipeline start. Server replies with SyncSlotGranted or
    /// SyncSlotDenied, keyed by SessionId + ModelGuid.
    /// </summary>
    public class RequestSyncSlotPayload
    {
        public string ModelGuid { get; set; }
        public string SessionId { get; set; }
        public string Username { get; set; }
        public string RevitUsername { get; set; }
        // Set to Environment.MachineName by the client. Server uses (RevitUsername,
        // ComputerName) to detect "same user crashed Revit and reopened with a new
        // SessionId" — that case evicts the stale slot and grants the new requester
        // immediately instead of waiting for the 2-minute EvictAfter threshold.
        public string ComputerName { get; set; }
        public long ClaimedAtUtcTicks { get; set; }
        public bool IsCloud { get; set; }
    }

    /// <summary>
    /// Payload for SyncSlotGranted (server → client). Sent when the server has
    /// accepted the requester's sync slot — client proceeds with sync.
    /// </summary>
    public class SyncSlotGrantedPayload
    {
        public string ModelGuid { get; set; }
        public string SessionId { get; set; }
    }

    /// <summary>
    /// Payload for SyncSlotDenied (server → client). Sent when another peer holds
    /// the active slot. Includes queue position so the client can show waiting UX.
    /// </summary>
    public class SyncSlotDeniedPayload
    {
        public string ModelGuid { get; set; }
        public string SessionId { get; set; }
        public string Reason { get; set; }
        public int QueuePosition { get; set; }
        public int ExpectedWaitSecs { get; set; }
        public string BlockingUsername { get; set; }
    }
}
