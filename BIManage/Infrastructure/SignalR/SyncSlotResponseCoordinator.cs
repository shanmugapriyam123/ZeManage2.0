using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;

namespace BIManage.Infrastructure.SignalR
{
    /// <summary>
    /// Correlates RequestSyncSlot (client → server) with SyncSlotGranted / SyncSlotDenied
    /// (server → client) replies. Our SignalR transport is a custom raw WebSocket without
    /// hub-style InvokeAsync — so we send a request fire-and-forget, then await a TCS
    /// completed by the listener when the matching reply arrives.
    ///
    /// Keyed by (sessionId, modelGuid). If no reply arrives within the caller's timeout
    /// the awaiter cancels and returns Unavailable, letting EventRegistryService fall
    /// back to the client-side STC gate. This makes the feature backward-compatible:
    /// an old server that doesn't recognize RequestSyncSlot simply doesn't reply, the
    /// client times out, and today's behavior is preserved (per backward-compatibility
    /// guarantee in C:\Users\Admin\.claude\plans\buzzing-spinning-storm.md).
    /// </summary>
    public sealed class SyncSlotResponseCoordinator
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<SyncSlotDecision>> _pending
            = new ConcurrentDictionary<string, TaskCompletionSource<SyncSlotDecision>>(StringComparer.OrdinalIgnoreCase);

        public SyncSlotResponseCoordinator(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Awaits a server reply for (sessionId, modelGuid). Times out after
        /// <paramref name="timeout"/> and returns <see cref="SyncSlotDecision.Unavailable"/>
        /// so the caller falls back to the client-side gate.
        /// </summary>
        public async Task<SyncSlotDecision> AwaitDecisionAsync(
            string sessionId, string modelGuid, TimeSpan timeout, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(modelGuid))
                return SyncSlotDecision.Unavailable("invalid-key");

            var key = MakeKey(sessionId, modelGuid);
            var tcs = new TaskCompletionSource<SyncSlotDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Replace any prior pending awaiter for the same key — if a previous call
            // hadn't completed (timed out and is GC-pending), let the new caller take
            // over. Cancelling the orphan TCS prevents a leaked Unavailable from racing
            // into a later request.
            if (_pending.TryGetValue(key, out var prior))
            {
                try { prior.TrySetResult(SyncSlotDecision.Unavailable("superseded")); } catch { }
            }
            _pending[key] = tcs;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                using (timeoutCts.Token.Register(() =>
                       {
                           if (tcs.TrySetResult(SyncSlotDecision.Unavailable("timeout")))
                               _logger?.LogDebug($"[SyncSlot] await timed out for {key} after {timeout.TotalMilliseconds:F0}ms");
                       }))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                // Best-effort: only remove if we're still the registered awaiter.
                if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, tcs))
                    _pending.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Called by SyncQueueListener when a SyncSlotGranted message arrives. Completes
        /// the matching awaiter if one exists; otherwise the message is silently dropped
        /// (e.g. server emitted a stale reply after our caller already timed out).
        /// </summary>
        public void CompleteGranted(string sessionId, string modelGuid)
        {
            var key = MakeKey(sessionId, modelGuid);
            if (_pending.TryRemove(key, out var tcs))
            {
                if (tcs.TrySetResult(SyncSlotDecision.Granted))
                    _logger?.LogDebug($"[SyncSlot] Granted reply matched for {key}");
            }
            else
            {
                _logger?.LogDebug($"[SyncSlot] Granted reply ignored — no awaiter for {key}");
            }
        }

        /// <summary>
        /// Called by SyncQueueListener when a SyncSlotDenied message arrives.
        /// </summary>
        public void CompleteDenied(string sessionId, string modelGuid,
            string reason, int queuePosition, int expectedWaitSecs, string blockingUsername)
        {
            var key = MakeKey(sessionId, modelGuid);
            if (_pending.TryRemove(key, out var tcs))
            {
                var decision = SyncSlotDecision.Denied(reason, queuePosition, expectedWaitSecs, blockingUsername);
                if (tcs.TrySetResult(decision))
                    _logger?.LogDebug($"[SyncSlot] Denied reply matched for {key} (pos={queuePosition})");
            }
            else
            {
                _logger?.LogDebug($"[SyncSlot] Denied reply ignored — no awaiter for {key}");
            }
        }

        private static string MakeKey(string sessionId, string modelGuid)
            => sessionId + "|" + modelGuid;
    }

    public enum SyncSlotOutcome
    {
        Granted,
        Denied,
        Unavailable
    }

    public sealed class SyncSlotDecision
    {
        public SyncSlotOutcome Outcome { get; }
        public string Reason { get; }
        public int QueuePosition { get; }
        public int ExpectedWaitSecs { get; }
        public string BlockingUsername { get; }

        private SyncSlotDecision(SyncSlotOutcome outcome, string reason,
            int queuePosition, int expectedWaitSecs, string blockingUsername)
        {
            Outcome = outcome;
            Reason = reason;
            QueuePosition = queuePosition;
            ExpectedWaitSecs = expectedWaitSecs;
            BlockingUsername = blockingUsername;
        }

        public static readonly SyncSlotDecision Granted
            = new SyncSlotDecision(SyncSlotOutcome.Granted, null, 0, 0, null);

        public static SyncSlotDecision Denied(string reason, int queuePosition,
            int expectedWaitSecs, string blockingUsername)
            => new SyncSlotDecision(SyncSlotOutcome.Denied, reason, queuePosition, expectedWaitSecs, blockingUsername);

        public static SyncSlotDecision Unavailable(string reason)
            => new SyncSlotDecision(SyncSlotOutcome.Unavailable, reason, 0, 0, null);
    }
}
