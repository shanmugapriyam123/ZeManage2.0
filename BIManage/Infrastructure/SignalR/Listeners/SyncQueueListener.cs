using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BIManage.Core.Identity;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.SignalR.Listeners
{
    /// <summary>
    /// Listens for server-pushed sync coordination messages and publishes them
    /// to the internal SignalREventBus so SyncQueueViewModel can update in real time.
    /// Also persists remote sync starts/completes/cancels to local SQLite via SyncRepository,
    /// independently of the dialog being open — so that when the user reopens the Sync
    /// Activity Monitor, currently-syncing remote users are visible (otherwise the only
    /// place the events were captured was inside the dialog's own subscription).
    ///
    /// Handled methods:
    ///   SyncQueueUpdate  → SyncQueueUpdateEvent
    ///   SyncStarting     → SyncStartingEvent + persist starting row
    ///   SyncCompleted    → SyncCompletedEvent + persist completion
    ///   SyncCancelled    → SyncCancelledEvent + persist completion (failed)
    ///   SyncRequest      → SyncRequestEvent
    /// </summary>
    public class SyncQueueListener : SignalListenerBase
    {
        private readonly ISignalREventBus _eventBus;
        private readonly SyncRepository? _syncRepository;
        private readonly SessionRepository? _sessionRepository;
        private readonly string? _ownSessionId;
        // Optional — only set when server-arbitrated cloud sync gate (Option D) is wired.
        // The listener forwards SyncSlotGranted / SyncSlotDenied replies to this coordinator
        // so the awaiting RequestSyncSlotAsync caller can resolve. Null on legacy bootstrap
        // paths (e.g. unit tests) — slot messages are still published to the event bus.
        private readonly BIManage.Infrastructure.SignalR.SyncSlotResponseCoordinator? _slotCoordinator;

        // Per-method per-(session+model) timestamp of FIRST processed event.
        // Used to swallow duplicate SignalR deliveries (echo + multi-binding fan-out,
        // server replay on reconnect) within a short fixed-length window. Without this,
        // every duplicate SyncStarting/SyncCompleted inserts a new row in model_sync
        // and the Sync Activity Monitor shows the same sync as 2+ rows.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _recentEvents
            = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // 3-second fixed window: real duplicates (multi-binding echo, server replay on
        // reconnect) arrive within ~500 ms; 3 s is generous margin. Importantly, the
        // window is fixed-length from first-seen (not refreshed on each suppressed dupe)
        // so a legitimate re-sync at T+4 s is NOT swallowed. Previously this was 60 s
        // with timestamp-refresh-on-hit, which extended the window indefinitely under a
        // trickle of echoes and silently dropped re-syncs by the same user within a
        // minute. Listener registration is now de-duplicated at the bootstrap level,
        // so the original "4 events at 15 s intervals" repro no longer reproduces.
        private const int DuplicateWindowSeconds = 3;

        public override string Name => "SyncQueue";

        public override IEnumerable<string> SupportedMethods => new[]
        {
            SignalRMethods.SyncQueueUpdate,
            // Some server fan-out paths forward the original SyncQueueJoin method name
            // without converting it to SyncQueueUpdate. Without these two entries the
            // queued-user broadcast is silently dropped on the receiving client because
            // no listener is registered for that method, and no peer ever sees the
            // joiner appear as "In Queue".
            SignalRMethods.SyncQueueJoin,
            SignalRMethods.SyncQueueLeave,
            SignalRMethods.SyncStarting,
            SignalRMethods.SyncCompleted,
            SignalRMethods.SyncCancelled,
            SignalRMethods.SyncRequest,
            // Server-arbitrated cloud sync gate (Option D). Replies to RequestSyncSlot.
            SignalRMethods.SyncSlotGranted,
            SignalRMethods.SyncSlotDenied
        };

        public SyncQueueListener(ILogger logger, ISignalREventBus eventBus,
                                 SyncRepository? syncRepository = null,
                                 string? ownSessionId = null,
                                 SessionRepository? sessionRepository = null,
                                 BIManage.Infrastructure.SignalR.SyncSlotResponseCoordinator? slotCoordinator = null)
            : base(logger)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _syncRepository = syncRepository;
            _ownSessionId = ownSessionId;
            _sessionRepository = sessionRepository;
            _slotCoordinator = slotCoordinator;
        }

        protected override async Task ProcessMessageAsync(SignalRMessageInfo message)
        {
            LogMessageReceived(message);

            // ModelGuid is carried at the top-level SignalRMessageInfo field
            var modelGuid = message.ModelGuid;

            switch (message.Method)
            {
                case SignalRMethods.SyncQueueUpdate:
                case SignalRMethods.SyncQueueJoin:
                case SignalRMethods.SyncQueueLeave:
                {
                    // Server forwards a user payload identifying who joined/left the queue
                    // (same shape as SyncStarting). Extract it so the Sync Queue dialog can
                    // add the joining user as an "InQueue" row in real time.
                    //
                    // All three method names route through the same handler because some
                    // server fan-out paths forward the original SyncQueueJoin/SyncQueueLeave
                    // method name without converting to SyncQueueUpdate. Treating them
                    // identically on the client side means the queued user appears in every
                    // peer's monitor regardless of which name the server uses.
                    var queueUser = GetPayloadSafe<UserPresencePayload>(message);
                    var queueSessionId = queueUser?.SessionId ?? message.SenderSessionId;
                    // Cross-method dedup: if Join + Update arrive back-to-back for the same
                    // (session, model), they describe the same event — keep only the first.
                    // Use a constant pseudo-method key so all three names share one bucket.
                    if (IsRecentDuplicate("SyncQueue*", queueSessionId, modelGuid))
                    {
                        _logger?.LogDebug($"[SyncQueue] Suppressing duplicate {message.Method} for session {queueSessionId} on model {modelGuid} (within {DuplicateWindowSeconds}s)");
                        break;
                    }
                    // Resolve via the central UsernameResolver — falls back to the local
                    // sessions table when the SignalR payload doesn't carry a name, so
                    // the UI never has to stamp "Unknown user".
                    var queueDisplayUser = await UsernameResolver.ResolveAsync(
                        queueSessionId,
                        queueUser?.RevitUsername,
                        queueUser?.Username ?? message.SenderUsername,
                        queueUser?.ComputerName,
                        _sessionRepository).ConfigureAwait(false);
                    _logger?.LogInfo($"[SyncQueue] {message.Method} for model {modelGuid} — user: {queueDisplayUser ?? "<unresolved>"}");
                    _eventBus.Publish(new SyncQueueUpdateEvent
                    {
                        ModelGuid     = modelGuid,
                        SessionId     = queueSessionId,
                        Username      = queueDisplayUser ?? queueUser?.Username ?? message.SenderUsername,
                        RevitUsername = queueDisplayUser ?? queueUser?.RevitUsername,
                        ComputerName  = queueUser?.ComputerName,
                        OccurredAtUtc = TicksToUtc(queueUser?.OccurredAtUtcTicks ?? 0),
                        Source        = Name
                    });
                    break;
                }

                case SignalRMethods.SyncStarting:
                {
                    var user = GetPayloadSafe<UserPresencePayload>(message);
                    var sessionId = user?.SessionId ?? message.SenderSessionId;

                    // QUEUE PIGGYBACK: if this SyncStarting carries a QueueAction marker,
                    // it's not a real sync start — it's a queue/claim announcement sent on
                    // the reliable SyncStarting channel (the SyncQueueUpdate / SyncQueueJoin
                    // server fan-out has proven unreliable). Route based on action and DO
                    // NOT publish SyncStartingEvent or touch _activeSyncers — the user is
                    // queued/claiming, not yet syncing.
                    var queueAction = user?.QueueAction;
                    if (!string.IsNullOrWhiteSpace(queueAction))
                    {
                        // Claim has its own dedup window (3s) and timestamp arbitration;
                        // do NOT route it through the 60s "SyncQueue*" dedup bucket — that
                        // would suppress a legitimate second claim from the same session
                        // for the same model after the first one was resolved.
                        if (string.Equals(queueAction, "Claim", StringComparison.OrdinalIgnoreCase))
                        {
                            var claimDisplay = await UsernameResolver.ResolveAsync(
                                sessionId, user?.RevitUsername, user?.Username ?? message.SenderUsername,
                                user?.ComputerName, _sessionRepository).ConfigureAwait(false);
                            var claimedAt = user!.ClaimedAtUtcTicks > 0
                                ? new DateTime(user.ClaimedAtUtcTicks, DateTimeKind.Utc)
                                : DateTime.UtcNow;
                            _logger?.LogInfo($"[SyncClaim] recv session={sessionId} user={claimDisplay ?? "<unresolved>"} ts={claimedAt:HH:mm:ss.fff} model={modelGuid}");
                            _eventBus.Publish(new SyncClaimEvent
                            {
                                ModelGuid     = modelGuid,
                                SessionId     = sessionId,
                                Username      = claimDisplay ?? user?.Username ?? message.SenderUsername,
                                RevitUsername = claimDisplay ?? user?.RevitUsername,
                                ComputerName  = user?.ComputerName,
                                ClaimedAtUtc  = claimedAt,
                                Source        = Name + "/Piggyback-Claim"
                            });
                            break;
                        }

                        if (IsRecentDuplicate("SyncQueue*", sessionId, modelGuid))
                        {
                            _logger?.LogDebug($"[SyncQueue] Suppressing duplicate piggyback {queueAction} for session {sessionId} on model {modelGuid}");
                            break;
                        }
                        var piggybackDisplay = await UsernameResolver.ResolveAsync(
                            sessionId, user?.RevitUsername, user?.Username ?? message.SenderUsername,
                            user?.ComputerName, _sessionRepository).ConfigureAwait(false);
                        _logger?.LogInfo($"[SyncQueue] SyncStarting carrying QueueAction='{queueAction}' for model {modelGuid} — user: {piggybackDisplay ?? "<unresolved>"} — routing as queue event");
                        _eventBus.Publish(new SyncQueueUpdateEvent
                        {
                            ModelGuid     = modelGuid,
                            SessionId     = sessionId,
                            Username      = piggybackDisplay ?? user?.Username ?? message.SenderUsername,
                            RevitUsername = piggybackDisplay ?? user?.RevitUsername,
                            ComputerName  = user?.ComputerName,
                            OccurredAtUtc = TicksToUtc(user?.OccurredAtUtcTicks ?? 0),
                            Source        = Name + "/Piggyback-" + queueAction
                        });
                        break;
                    }

                    if (IsRecentDuplicate(SignalRMethods.SyncStarting, sessionId, modelGuid))
                    {
                        _logger?.LogDebug($"[SyncQueue] Suppressing duplicate SyncStarting for session {sessionId} on model {modelGuid} (within {DuplicateWindowSeconds}s)");
                        break;
                    }
                    var displayUser = await UsernameResolver.ResolveAsync(
                        sessionId,
                        user?.RevitUsername,
                        user?.Username ?? message.SenderUsername,
                        user?.ComputerName,
                        _sessionRepository).ConfigureAwait(false);
                    _logger?.LogInfo($"[SyncQueue] SyncStarting for model {modelGuid} — user: {displayUser ?? "<unresolved>"}");
                    _eventBus.Publish(new SyncStartingEvent
                    {
                        ModelGuid     = modelGuid,
                        SessionId     = sessionId,
                        Username      = displayUser ?? user?.Username ?? message.SenderUsername,
                        RevitUsername = displayUser ?? user?.RevitUsername,
                        ComputerName  = user?.ComputerName,
                        OccurredAtUtc = TicksToUtc(user?.OccurredAtUtcTicks ?? 0),
                        Source        = Name
                    });
                    // Skip persistence when no name resolved at all — a NULL row would
                    // surface as "Unknown user" on dialog reload. The next SignalR event
                    // for the same session (heartbeat, sync-complete) will carry the name
                    // once the sessions table catches up.
                    if (!string.IsNullOrWhiteSpace(displayUser))
                        PersistRemoteSyncStartFireAndForget(sessionId, modelGuid, displayUser!, user?.ComputerName);
                    else
                        _logger?.LogDebug($"[SyncQueue] Deferring persistence — no display name resolved for session {sessionId}");
                    break;
                }

                case SignalRMethods.SyncCompleted:
                {
                    var user = GetPayloadSafe<UserPresencePayload>(message);
                    var sessionId = user?.SessionId ?? message.SenderSessionId;
                    if (IsRecentDuplicate(SignalRMethods.SyncCompleted, sessionId, modelGuid))
                    {
                        _logger?.LogDebug($"[SyncQueue] Suppressing duplicate SyncCompleted for session {sessionId} on model {modelGuid} (within {DuplicateWindowSeconds}s)");
                        break;
                    }
                    var completedDisplayUser = await UsernameResolver.ResolveAsync(
                        sessionId,
                        user?.RevitUsername,
                        user?.Username ?? message.SenderUsername,
                        user?.ComputerName,
                        _sessionRepository).ConfigureAwait(false);
                    _logger?.LogInfo($"[SyncQueue] SyncCompleted for model {modelGuid} — user: {completedDisplayUser ?? "<unresolved>"}");
                    _eventBus.Publish(new SyncCompletedEvent
                    {
                        ModelGuid     = modelGuid,
                        SessionId     = sessionId,
                        Username      = completedDisplayUser ?? user?.Username ?? message.SenderUsername,
                        RevitUsername = completedDisplayUser ?? user?.RevitUsername,
                        ComputerName  = user?.ComputerName,
                        OccurredAtUtc = TicksToUtc(user?.OccurredAtUtcTicks ?? 0),
                        Source        = Name
                    });
                    PersistRemoteSyncCompleteFireAndForget(sessionId, modelGuid, succeeded: true, syncedBy: completedDisplayUser, computerName: user?.ComputerName);
                    break;
                }

                case SignalRMethods.SyncCancelled:
                {
                    var user = GetPayloadSafe<UserPresencePayload>(message);
                    var sessionId = user?.SessionId ?? message.SenderSessionId;
                    if (IsRecentDuplicate(SignalRMethods.SyncCancelled, sessionId, modelGuid))
                    {
                        _logger?.LogDebug($"[SyncQueue] Suppressing duplicate SyncCancelled for session {sessionId} on model {modelGuid} (within {DuplicateWindowSeconds}s)");
                        break;
                    }
                    var cancelledDisplayUser = await UsernameResolver.ResolveAsync(
                        sessionId,
                        user?.RevitUsername,
                        user?.Username ?? message.SenderUsername,
                        user?.ComputerName,
                        _sessionRepository).ConfigureAwait(false);
                    _logger?.LogInfo($"[SyncQueue] SyncCancelled for model {modelGuid} — user: {cancelledDisplayUser ?? "<unresolved>"}");
                    _eventBus.Publish(new SyncCancelledEvent
                    {
                        ModelGuid     = modelGuid,
                        SessionId     = sessionId,
                        Username      = cancelledDisplayUser ?? user?.Username ?? message.SenderUsername,
                        RevitUsername = cancelledDisplayUser ?? user?.RevitUsername,
                        ComputerName  = user?.ComputerName,
                        OccurredAtUtc = TicksToUtc(user?.OccurredAtUtcTicks ?? 0),
                        Source        = Name
                    });
                    PersistRemoteSyncCompleteFireAndForget(sessionId, modelGuid, succeeded: false, syncedBy: cancelledDisplayUser, computerName: user?.ComputerName);
                    break;
                }

                case SignalRMethods.SyncRequest:
                {
                    var reqPayload = GetPayloadSafe<SyncRequestPayload>(message);
                    _logger?.LogInfo($"[SyncQueue] SyncRequest for model {modelGuid}, target session: {reqPayload?.TargetSessionId ?? "all"}");
                    _eventBus.Publish(new SyncRequestEvent
                    {
                        ModelGuid       = modelGuid,
                        TargetSessionId = reqPayload?.TargetSessionId,
                        Source          = Name
                    });
                    break;
                }

                case SignalRMethods.SyncSlotGranted:
                {
                    var grantedPayload = GetPayloadSafe<SyncSlotGrantedPayload>(message);
                    var grantedSessionId = grantedPayload?.SessionId ?? message.SenderSessionId;
                    var grantedModelGuid = grantedPayload?.ModelGuid ?? modelGuid;
                    _logger?.LogInfo($"[SyncQueue] SyncSlotGranted for model {grantedModelGuid} session {grantedSessionId}");
                    _eventBus.Publish(new SyncSlotGrantedEvent
                    {
                        ModelGuid = grantedModelGuid,
                        SessionId = grantedSessionId,
                        Source    = Name
                    });
                    _slotCoordinator?.CompleteGranted(grantedSessionId, grantedModelGuid);
                    break;
                }

                case SignalRMethods.SyncSlotDenied:
                {
                    var deniedPayload = GetPayloadSafe<SyncSlotDeniedPayload>(message);
                    var deniedSessionId = deniedPayload?.SessionId ?? message.SenderSessionId;
                    var deniedModelGuid = deniedPayload?.ModelGuid ?? modelGuid;
                    _logger?.LogInfo($"[SyncQueue] SyncSlotDenied for model {deniedModelGuid} session {deniedSessionId} — pos={deniedPayload?.QueuePosition ?? 0}, blockingUser={deniedPayload?.BlockingUsername ?? "<unknown>"}");
                    _eventBus.Publish(new SyncSlotDeniedEvent
                    {
                        ModelGuid        = deniedModelGuid,
                        SessionId        = deniedSessionId,
                        Reason           = deniedPayload?.Reason,
                        QueuePosition    = deniedPayload?.QueuePosition ?? 0,
                        ExpectedWaitSecs = deniedPayload?.ExpectedWaitSecs ?? 0,
                        BlockingUsername = deniedPayload?.BlockingUsername,
                        Source           = Name
                    });
                    _slotCoordinator?.CompleteDenied(
                        deniedSessionId,
                        deniedModelGuid,
                        deniedPayload?.Reason,
                        deniedPayload?.QueuePosition ?? 0,
                        deniedPayload?.ExpectedWaitSecs ?? 0,
                        deniedPayload?.BlockingUsername);
                    break;
                }
            }
        }

        /// <summary>
        /// Converts the server-stamped UTC tick count to a DateTime, or DateTime.MinValue
        /// when ticks &lt;= 0 (older server build that doesn't stamp). Receivers treat
        /// MinValue as "no info — accept the event" for backward compat. Clamp out-of-range
        /// values so an upstream bug can't crash the listener.
        /// </summary>
        private static DateTime TicksToUtc(long ticks)
        {
            if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return DateTime.MinValue;
            return new DateTime(ticks, DateTimeKind.Utc);
        }

        /// <summary>
        /// Returns true if a (method, sessionId, modelGuid) event was already processed
        /// within the last <see cref="DuplicateWindowSeconds"/> seconds. Used to swallow
        /// duplicate SignalR deliveries (echo + multi-binding fan-out, server replay on
        /// reconnect) so we don't persist or republish the same sync twice. First call
        /// for a key returns false AND records the timestamp; subsequent calls within
        /// the window return true WITHOUT updating the stored timestamp — so the window
        /// is fixed-length from first-seen, and a legitimate re-sync just after the
        /// window expires is processed normally. Also expires stale entries (>5 min)
        /// opportunistically.
        /// </summary>
        private bool IsRecentDuplicate(string method, string? sessionId, string? modelGuid)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(modelGuid)) return false;
            var key = method + "|" + sessionId + "|" + modelGuid;
            var now = DateTime.UtcNow;
            if (_recentEvents.TryGetValue(key, out var lastSeen)
                && (now - lastSeen).TotalSeconds < DuplicateWindowSeconds)
            {
                // Intentionally NOT refreshing the stored timestamp — the window is
                // anchored to the first-seen event, so a trickle of echoes can't slide
                // it forward and starve a real re-sync.
                return true;
            }
            _recentEvents[key] = now;

            // Opportunistic cleanup so the dictionary doesn't grow without bound across
            // long sessions. Anything older than 5 minutes is irrelevant for dedup.
            if (_recentEvents.Count > 64)
            {
                var cutoff = now.AddMinutes(-5);
                foreach (var kvp in _recentEvents)
                {
                    if (kvp.Value < cutoff)
                        _recentEvents.TryRemove(kvp.Key, out _);
                }
            }
            return false;
        }

        private void PersistRemoteSyncStartFireAndForget(string? sessionId, string? modelGuid, string syncedBy, string? computerName)
        {
            if (_syncRepository == null) return;
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(modelGuid)) return;
            // Don't record our own session as a "remote" sync.
            if (!string.IsNullOrEmpty(_ownSessionId)
                && string.Equals(sessionId, _ownSessionId, StringComparison.OrdinalIgnoreCase)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    // Always insert a fresh row — every remote sync is its own history entry.
                    await _syncRepository.InsertRemoteSyncStartAsync(
                        sessionId: sessionId!,
                        modelGuid: modelGuid!,
                        modelName: null,
                        syncedBy: syncedBy,
                        computerName: computerName,
                        startedAtUtc: DateTime.UtcNow);
                }
                catch (Exception ex) { _logger?.LogDebug($"[SyncQueue] persist start failed: {ex.Message}"); }
            });
        }

        private void PersistRemoteSyncCompleteFireAndForget(string? sessionId, string? modelGuid, bool succeeded, string? syncedBy = null, string? computerName = null)
        {
            if (_syncRepository == null) return;
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(modelGuid)) return;
            if (!string.IsNullOrEmpty(_ownSessionId)
                && string.Equals(sessionId, _ownSessionId, StringComparison.OrdinalIgnoreCase)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    // Closes the most recent open row for (sessionId, modelGuid). Falls back
                    // to a synthetic closed row if no open row exists.
                    await _syncRepository.CompleteRemoteSyncAsync(
                        sessionId: sessionId!,
                        modelGuid: modelGuid!,
                        succeeded: succeeded,
                        syncedBy: syncedBy,
                        endedAtUtc: DateTime.UtcNow);
                }
                catch (Exception ex) { _logger?.LogDebug($"[SyncQueue] persist complete failed: {ex.Message}"); }
            });
        }
    }
}
