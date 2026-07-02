using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BIManage.Revit.SyncTrafficControl.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Core;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Revit.SyncTrafficControl
{
    public class SyncTrafficControlService : IDisposable
    {
        private readonly ISignalREventBus _eventBus;
        private readonly ISignalRConnectionManager _connectionManager;
        private readonly SyncRepository _syncRepository;
        private readonly ILogger _logger;
        private readonly Dispatcher _dispatcher;
        private readonly string _localSessionId;

        // modelGuid → (sessionId → SyncerInfo) for ALL sessions that have broadcast a
        // SyncStarting we haven't seen a SyncCompleted/SyncCancelled for. Multi-slot
        // because the claim-arbitration window (~600 ms) doesn't always elect a single
        // winner: when SignalR fan-out latency exceeds the window, multiple peers can
        // broadcast SyncStarting concurrently and only Revit's native central lock
        // serializes them. With a single-slot dictionary the last echo on the wire
        // overwrites the earliest claimant, EvaluateSync points at the wrong user, and
        // the popup names the wrong person. Semantics:
        //   - Earliest StartedAtUtc = the active syncer (the one who has — or will get —
        //     Revit's central lock first). Read via GetActiveSyncer / IsBusy.
        //   - All others = blocked at Revit's native layer; they'll become active in
        //     start-time order as each SyncCompleted removes its own entry.
        // The inner dict is keyed by sessionId so SyncCompleted's per-session removal
        // never wipes a peer's still-live entry.
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SyncerInfo>> _activeSyncers
            = new ConcurrentDictionary<string, ConcurrentDictionary<string, SyncerInfo>>(StringComparer.OrdinalIgnoreCase);

        // modelGuid → local queue ID (models this user is queued for)
        private readonly ConcurrentDictionary<string, string> _queuedModels = new ConcurrentDictionary<string, string>();

        // modelGuid → ordered list of (sessionId, username, joinedAtUtc) for ALL users
        // queued on that model (own + every peer we've observed via SignalR). The list is
        // kept FIFO by joinedAtUtc so the head element is the next user to sync.
        //
        // Why a list, not _queuedModels: _queuedModels only tracks OWN session ("am I
        // queued for this model?"). To do client-side queue promotion (deciding "is it
        // my turn now?" when the syncer completes) and to populate the "Waiting in Queue"
        // panel for every observer, every client needs the full ordered list.
        private readonly ConcurrentDictionary<string, List<QueueEntry>> _modelQueues
            = new ConcurrentDictionary<string, List<QueueEntry>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>One entry in a model's shared queue.</summary>
        public class QueueEntry
        {
            public string SessionId { get; set; } = "";
            public string Username { get; set; } = "";
            public DateTime JoinedAtUtc { get; set; }
        }

        // local queue ID for ongoing sync (to update status on completion)
        private readonly ConcurrentDictionary<string, string> _localSyncQueueIds = new ConcurrentDictionary<string, string>();

        // modelGuid → timestamp: queue-triggered syncs that should bypass EvaluateSync (30s TTL)
        private readonly ConcurrentDictionary<string, DateTime> _pendingQueueSync = new ConcurrentDictionary<string, DateTime>();

        // modelGuid → UTC timestamp of THIS user's most recent local sync completion.
        // Used to suppress the "Join Sync Queue" dialog for a short window AFTER we
        // just finished syncing — without this, a peer's SyncStarting that arrives
        // 1-2 s after our completion wrongly pops "Join Queue" even though we have
        // nothing to sync. User-reported (4-user concurrent test, 2026-05-27):
        // "users received Join Sync Queue option even after their sync was completed,
        //  if they clicked Join they were added as ------ with status In Queue".
        // Window: 60 s — generous enough to cover slow SignalR fan-out, short enough
        // that a real second sync attempt by the same user still routes through the
        // normal flow.
        private readonly ConcurrentDictionary<string, DateTime> _recentLocalCompletions = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RecentCompletionWindow = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Returns true if THIS user's last sync on the given model completed within
        /// the suppression window. Used by the Join-Queue dialog and queue-add paths
        /// to avoid spurious entries when a peer's SyncStarting arrives moments
        /// after the local sync completes.
        /// </summary>
        public bool JustCompletedLocalSync(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return false;
            if (!_recentLocalCompletions.TryGetValue(modelGuid, out var ts)) return false;
            return (DateTime.UtcNow - ts) < RecentCompletionWindow;
        }

        // modelGuid → marker: a Sync Now popup has already been dispatched for the
        // current turn. Survives re-queue cycles (BackgroundSyncEngine periodic retry,
        // SignalR replay after reconnect) — the _queuedModels TryRemove gate is a
        // one-shot delete that re-arms when JoinQueue runs again, so without this
        // second-line guard the user sees the popup multiple times for the same turn.
        // Cleared on SyncCompleted, SyncCancelled, LeaveQueue, and explicit decline.
        private readonly ConcurrentDictionary<string, byte> _activePopupGuard = new ConcurrentDictionary<string, byte>();

        // modelGuid → list of in-flight claims received from peers in the last ~3s.
        // Populated by OnSyncClaim and consumed by GetCompetingClaims during the
        // ~600ms arbitration window inside SyncCommandBinding.OnBeforeExecuted. Pruned
        // by CleanupStaleEntries (3s TTL — claims are only interesting within the
        // arbitration window; older entries are noise).
        private readonly ConcurrentDictionary<string, List<ClaimEntry>> _pendingClaims
            = new ConcurrentDictionary<string, List<ClaimEntry>>(StringComparer.OrdinalIgnoreCase);

        // modelGuid → timestamp of the last claim broadcast we started locally. Used to
        // debounce the claim phase when Revit fires BeforeExecuted multiple times for
        // one user click (multiple bound command-ids — ID_FILE_SAVE_TO_CENTRAL and
        // ID_FILE_SAVE_TO_CENTRAL_SHORTCUT both register, and SynchronizeNow can wrap
        // them). Without this, one click costs 2× the wait (1.2s UI freeze) plus 2
        // wire broadcasts. The first call wins; subsequent calls within the window
        // reuse the prior wait's _pendingClaims results.
        private readonly ConcurrentDictionary<string, DateTime> _lastClaimBroadcastAt
            = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>One peer's "I want to sync" announcement, captured for arbitration.</summary>
        public class ClaimEntry
        {
            public string SessionId { get; set; } = "";
            public string Username { get; set; } = "";
            public DateTime ClaimedAtUtc { get; set; }
        }

        private Timer _staleCleanupTimer;
        private bool _disposed;

        /// <summary>
        /// When true, sync queue operations are handled automatically without UI dialogs.
        /// Set by the background sync engine; cleared when background sync completes.
        /// NOTE: this is a GLOBAL flag — true whenever the engine is driving ANY model's
        /// sync. Use <see cref="IsBackgroundSyncActiveFor"/> to gate per-model decisions
        /// (e.g. whether a queue-turn handoff should auto-accept) so a background sync of
        /// one model doesn't suppress a real user's "Ready to Sync" popup for another.
        /// </summary>
        public bool IsBackgroundSyncActive { get; set; }

        // Per-model background-sync tracking. The global flag above can't distinguish
        // "engine is syncing model X" from "engine is syncing model Y", which made the
        // queue-turn handler auto-sync (no popup) whenever the engine happened to be busy
        // on any model. This set records exactly which models the engine is driving.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _backgroundSyncModels
            = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when the background engine is currently driving a sync for this specific model.</summary>
        public bool IsBackgroundSyncActiveFor(string modelGuid)
            => !string.IsNullOrEmpty(modelGuid) && _backgroundSyncModels.ContainsKey(modelGuid);

        /// <summary>Mark/unmark a model as currently being background-synced (called by BackgroundSyncEngine).</summary>
        public void SetBackgroundSyncActive(string modelGuid, bool active)
        {
            if (string.IsNullOrEmpty(modelGuid)) return;
            if (active) _backgroundSyncModels[modelGuid] = 1;
            else _backgroundSyncModels.TryRemove(modelGuid, out _);
        }

        // Cached App.config flags — read once on first access. Defaults match the new
        // race-safe behavior; existing installs without these keys get the new behavior
        // automatically. Set the keys in BIManage/App.config to override.
        private bool? _failClosedOnDisconnectCached;
        private bool FailClosedOnDisconnect
        {
            get
            {
                if (_failClosedOnDisconnectCached.HasValue) return _failClosedOnDisconnectCached.Value;
                var raw = Common.Helpers.AppConfigReader.Read("SyncQueueControl:FailClosedOnDisconnect");
                // Default true. Only "false" (case-insensitive) disables.
                _failClosedOnDisconnectCached = !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
                return _failClosedOnDisconnectCached.Value;
            }
        }

        private int? _claimWaitMsCached;
        /// <summary>
        /// Milliseconds SyncCommandBinding waits between broadcasting its claim and
        /// arbitrating against peer claims. Default 600 ms — enough to cover typical
        /// SignalR fan-out round-trip on a WAN. Tunable via App.config key
        /// SyncQueueControl:ClaimWaitMs. Clamped to [100, 3000].
        /// </summary>
        public int ClaimWaitMs
        {
            get
            {
                if (_claimWaitMsCached.HasValue) return _claimWaitMsCached.Value;
                var raw = Common.Helpers.AppConfigReader.Read("SyncQueueControl:ClaimWaitMs");
                if (!int.TryParse(raw, out var ms)) ms = 600;
                if (ms < 100) ms = 100;
                else if (ms > 3000) ms = 3000;
                _claimWaitMsCached = ms;
                return ms;
            }
        }

        // Event raised when SyncRequest targets this session (UI thread)
        public event Action<string, string> SyncRequestReceived; // modelGuid, modelName

        // Delegates for event bus (stored for unsubscribe)
        private readonly Action<SyncStartingEvent> _onSyncStarting;
        private readonly Action<SyncCompletedEvent> _onSyncCompleted;
        private readonly Action<SyncCancelledEvent> _onSyncCancelled;
        private readonly Action<SyncRequestEvent> _onSyncRequest;
        private readonly Action<SyncQueueUpdateEvent> _onSyncQueueUpdate;
        private readonly Action<SyncClaimEvent> _onSyncClaim;
        private readonly Action<BIManage.Infrastructure.SignalR.Events.UserLeftEvent> _onUserLeft;

        public SyncTrafficControlService(
            ISignalREventBus eventBus,
            ISignalRConnectionManager connectionManager,
            SyncRepository syncRepository,
            string localSessionId,
            ILogger logger,
            Dispatcher dispatcher)
        {
            _eventBus = eventBus;
            _connectionManager = connectionManager;
            _syncRepository = syncRepository;
            _localSessionId = localSessionId;
            _logger = logger;
            _dispatcher = dispatcher;

            _onSyncStarting = OnSyncStarting;
            _onSyncCompleted = OnSyncCompleted;
            _onSyncCancelled = OnSyncCancelled;
            _onSyncRequest = OnSyncRequest;
            _onSyncQueueUpdate = OnSyncQueueUpdate;
            _onSyncClaim = OnSyncClaim;
            _onUserLeft = OnUserLeft;
        }

        public void Initialize()
        {
            _eventBus.Subscribe(_onSyncStarting);
            _eventBus.Subscribe(_onSyncCompleted);
            _eventBus.Subscribe(_onUserLeft);
            _eventBus.Subscribe(_onSyncCancelled);
            _eventBus.Subscribe(_onSyncRequest);
            _eventBus.Subscribe(_onSyncQueueUpdate);
            _eventBus.Subscribe(_onSyncClaim);

            // Stale entry cleanup every 2 minutes
            _staleCleanupTimer = new Timer(_ => CleanupStaleEntries(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));

            _logger?.LogInfo("[SyncTrafficControl] Initialized");
        }

        /// <summary>
        /// Synchronous check — called from OnDocumentSynchronizingWithCentral (Revit main thread).
        /// O(1) dictionary lookup, no async needed.
        /// </summary>
        public SyncDecision EvaluateSync(string modelGuid, string modelName, string sessionId, string username)
        {
            // Disconnect policy — gated by App.config key SyncQueueControl:FailClosedOnDisconnect
            // (default true). When SignalR is down we can't see peer SyncStarting broadcasts,
            // so allowing sync would let every client on a flaky network race independently
            // — the exact "multiple users sync at once" bug. Default to BLOCKED with a synthetic
            // blocker so the user retries after the connection restores. Single-user installs
            // with no SignalR can opt out by setting the key to false.
            if (!_connectionManager.IsConnected)
            {
                if (FailClosedOnDisconnect)
                {
                    _logger?.LogWarning("[SyncTrafficControl] SignalR disconnected — blocking sync (fail-closed). Set SyncQueueControl:FailClosedOnDisconnect=false in App.config to disable.");
                    return SyncDecision.Blocked("Connectivity lost — try again", DateTime.UtcNow);
                }
                _logger?.LogDebug("[SyncTrafficControl] SignalR disconnected — allowing sync (fail-open, opted out via config)");
                return SyncDecision.Allow();
            }

            // Queue-triggered sync bypass: user already accepted their turn via SyncReadyDialog
            if (_pendingQueueSync.TryRemove(modelGuid, out var markedAt))
            {
                if ((DateTime.UtcNow - markedAt).TotalSeconds < 30)
                {
                    // FIRST-CLICKER-WINS race guard: if another user already claimed the
                    // turn in the ms between our Sync Now click and now, _activeSyncers
                    // will have their entry. Block us so we re-queue cleanly instead of
                    // starting a concurrent sync. The existing SyncCommandBinding path
                    // takes a Blocked decision and routes to JoinQueue automatically.
                    var racer = GetActiveSyncer(modelGuid);
                    if (racer != null
                        && !string.Equals(racer.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.LogInfo($"[SyncTrafficControl] Queue bypass race: {racer.Username} won the Sync Now race ({(DateTime.UtcNow - racer.StartedAtUtc).TotalMilliseconds:F0}ms ago) — blocking us so we re-join the queue");
                        return SyncDecision.Blocked(racer.Username, racer.StartedAtUtc);
                    }
                    _logger?.LogInfo($"[SyncTrafficControl] Queue bypass for {modelGuid} — user accepted turn");
                    return SyncDecision.Allow();
                }
            }

            // Earliest claimant is the authoritative "active" syncer. If multiple peers
            // raced past arbitration, the rest are tracked but reported via
            // GetQueuedSyncers — for the block decision we only care about the earliest.
            var primary = GetActiveSyncer(modelGuid);
            if (primary == null)
                return SyncDecision.Allow();

            // Same-session stale check: if we're the earliest claimant (e.g. after crash
            // with no SyncCompleted broadcast), clear our entry locally AND tell peers
            // their _activeSyncers entry for us is stale. Reference design's "kill
            // server-side" — we emit it via the existing SyncCancelled SignalR channel
            // (peers' OnSyncCancelled handler removes the matching session entry). This
            // breaks any phantom block immediately instead of letting peers wait for
            // the 10-minute stale TTL.
            if (string.Equals(primary.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning($"[SyncTrafficControl] Clearing stale self-entry for {modelGuid} and broadcasting SyncCancelled so peers self-heal");
                RemoveActiveSyncer(modelGuid, sessionId);
                _ = BroadcastSyncCancelledAsync(modelGuid, sessionId, username);
                primary = GetActiveSyncer(modelGuid);
                if (primary == null) return SyncDecision.Allow();
            }

            // Timeout check: if the active syncer has been running for more than 5
            // minutes, the entry is likely stale (their SyncCompleted was missed). Clear
            // that specific entry and re-evaluate so a queued peer can take over.
            var syncAge = DateTime.UtcNow - primary.StartedAtUtc;
            if (syncAge.TotalMinutes > 5)
            {
                _logger?.LogWarning($"[SyncTrafficControl] Clearing stale remote sync entry for {modelGuid} — {primary.Username} started {syncAge.TotalMinutes:F0}m ago (likely missed SyncCompleted)");
                RemoveActiveSyncer(modelGuid, primary.SessionId);
                primary = GetActiveSyncer(modelGuid);
                if (primary == null) return SyncDecision.Allow();
            }

            _logger?.LogInfo($"[SyncTrafficControl] Sync BLOCKED for {modelGuid} — {primary.Username} is syncing since {primary.StartedAtUtc:HH:mm:ss}");
            return SyncDecision.Blocked(primary.Username, primary.StartedAtUtc);
        }

        /// <summary>
        /// Called after EvaluateSync returns Allow and we proceed with sync.
        /// Records locally and broadcasts SyncStarting via SignalR.
        /// <para>
        /// Debounce: SyncCommandBinding registers ~7 sync command-ids (on-prem +
        /// cloud variants). For one user click Revit can fire BeforeExecuted for
        /// MORE THAN ONE of those ids (e.g. SynchronizeNow wraps ID_FILE_SAVE_TO_CENTRAL
        /// internally). Without a debounce we'd broadcast SyncStarting 2-3× per click,
        /// every receiver would persist 2-3 rows in model_sync, and the Sync Activity
        /// Monitor would show the same sync as duplicate rows. We swallow any call
        /// arriving within 10 seconds for the same (modelGuid, sessionId).
        /// </para>
        /// </summary>
        public void OnLocalSyncStarting(string modelGuid, string modelName, string sessionId, string username)
        {
            // Duplicate-broadcast suppression (see method remarks). Look up our own
            // session's entry directly — peer entries for the same model don't count.
            if (_activeSyncers.TryGetValue(modelGuid, out var inner)
                && inner.TryGetValue(sessionId, out var existing)
                && (DateTime.UtcNow - existing.StartedAtUtc).TotalSeconds < 60)
            {
                _logger?.LogDebug($"[SyncTrafficControl] Skipping duplicate OnLocalSyncStarting for {modelGuid} (within 60s window)");
                return;
            }

            // Stamp the start time ONCE and reuse it for both the local insert and the
            // broadcast, so our own machine and every peer order this sync by the SAME
            // (originator's) timestamp. Previously the broadcast carried no timestamp and
            // each receiver stamped its own local clock — which made every client think
            // ITSELF was the earliest/active syncer (per-user inconsistent view bug).
            var startedAtUtc = DateTime.UtcNow;
            var info = new SyncerInfo
            {
                SessionId = sessionId,
                Username = username,
                RevitUsername = username,
                StartedAtUtc = startedAtUtc
            };

            // Upsert into our own slot — a stale own-entry (from a prior session) is
            // safe to overwrite because StartedAtUtc is being refreshed to "now".
            GetOrAddInner(modelGuid)[sessionId] = info;

            // Arm the native-popup interceptor (long-window recency check) every time
            // a real sync starts on this machine — covers the rare case where Revit
            // surfaces "File not saved" mid-sync due to a sub-second retry.
            try { SyncConflictDialogInterceptor.NoteSyncAttempt(); } catch { }

            // A session that's transitioning from queue → syncing must leave BOTH the
            // shared queue list (_modelQueues, used by the Sync Activity Monitor) and
            // the local own-queue tracker (_queuedModels). Without this, our own machine
            // continues to advertise us as "In Queue" even though we're actually syncing
            // now — because the cleanup in OnSyncStarting only fires when the SignalR
            // echo of our own broadcast comes back, which the server doesn't always do.
            RemoveFromModelQueue(modelGuid, sessionId);
            _queuedModels.TryRemove(modelGuid, out _);

            // Fire-and-forget: broadcast to other clients, carrying our start timestamp so
            // every peer records us with the same StartedAtUtc we used locally.
            _ = BroadcastSyncStartingAsync(modelGuid, sessionId, username, startedAtUtc);

            // Fire-and-forget: record in local DB
            _ = RecordSyncQueueAsync(modelGuid, modelName, sessionId, username, "syncing");
        }

        /// <summary>
        /// Get the username of the user currently syncing a model (if any). Returns the
        /// earliest claimant's name — see <see cref="GetActiveSyncer"/> for semantics.
        /// Used by EventRegistryService.CheckSyncConflictDetection for cross-system
        /// conflict detection.
        /// </summary>
        public string? GetActiveSyncerUsername(string modelGuid)
            => GetActiveSyncer(modelGuid)?.Username;

        /// <summary>
        /// Get the SyncerInfo for the *active* syncer of a model — defined as the
        /// earliest claimant by <see cref="SyncerInfo.StartedAtUtc"/>. Returns null
        /// when no sync is in progress. When multiple peers have a live SyncStarting
        /// (claim arbitration failed), only the earliest is reported here; the rest
        /// are available via <see cref="GetQueuedSyncers"/>.
        /// </summary>
        public SyncerInfo? GetActiveSyncer(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return null;
            if (!_activeSyncers.TryGetValue(modelGuid, out var inner) || inner.IsEmpty) return null;
            SyncerInfo? earliest = null;
            foreach (var s in inner.Values)
            {
                if (earliest == null
                    || s.StartedAtUtc < earliest.StartedAtUtc
                    // Deterministic tiebreak on equal timestamps so every client elects
                    // the SAME active syncer (ordinal SessionId compare).
                    || (s.StartedAtUtc == earliest.StartedAtUtc
                        && string.CompareOrdinal(s.SessionId, earliest.SessionId) < 0))
                    earliest = s;
            }
            return earliest;
        }

        /// <summary>
        /// True when at least one syncer is currently tracked for the model.
        /// </summary>
        public bool IsBusy(string modelGuid)
            => !string.IsNullOrEmpty(modelGuid)
               && _activeSyncers.TryGetValue(modelGuid, out var inner)
               && !inner.IsEmpty;

        /// <summary>
        /// Returns the non-active syncers (everyone but the earliest claimant) ordered
        /// by <see cref="SyncerInfo.StartedAtUtc"/>. Empty when at most one syncer is
        /// tracked. Used to render late SyncStarting attempts as "queued" rows in the
        /// Sync Activity Monitor and to defer client-side queue promotion until ALL
        /// concurrent syncers have completed.
        /// </summary>
        public IReadOnlyList<SyncerInfo> GetQueuedSyncers(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return Array.Empty<SyncerInfo>();
            if (!_activeSyncers.TryGetValue(modelGuid, out var inner) || inner.Count <= 1)
                return Array.Empty<SyncerInfo>();
            var all = new SyncerInfo[inner.Count];
            int i = 0;
            foreach (var v in inner.Values) all[i++] = v;
            Array.Sort(all, (a, b) => a.StartedAtUtc.CompareTo(b.StartedAtUtc));
            var rest = new SyncerInfo[all.Length - 1];
            Array.Copy(all, 1, rest, 0, rest.Length);
            return rest;
        }

        /// <summary>
        /// True when the LOCAL session has an entry in <see cref="_activeSyncers"/> for
        /// this model — i.e., we already broadcast <c>SyncStarting</c> and haven't yet
        /// fired <c>SyncCompleted</c>/<c>SyncCancelled</c>. Used by
        /// <c>SyncConflictDialogInterceptor</c> to distinguish "central is busy because
        /// someone else is syncing" (peer is the blocker → route to queue dialog) from
        /// "central is busy and our own sync just failed at Revit's layer" (no point
        /// queueing — let Revit's native error show through so the user can retry).
        /// </summary>
        public bool IsLocalSessionSyncingFor(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid) || string.IsNullOrEmpty(_localSessionId)) return false;
            return _activeSyncers.TryGetValue(modelGuid, out var inner)
                   && inner.ContainsKey(_localSessionId);
        }

        // Timestamps of our most recent local-sync transition per model. Revit fires
        // post-sync follow-up dialogs (workset notifications, save-confirm-style
        // popups, etc.) within a few hundred ms of a successful sync completion; some
        // of those carry text our IsCentralBusyMessage matcher catches. By that point
        // _activeSyncers no longer has our entry (OnLocalSyncCompleted cleared it),
        // so IsLocalSessionSyncingFor returns false and the interceptor incorrectly
        // pops the queue dialog telling the user "another user is syncing" right
        // after THEIR OWN sync just succeeded. Track the completion timestamp so the
        // interceptor can suppress queue popups inside a short cooldown window.
        private readonly ConcurrentDictionary<string, DateTime> _lastOwnSyncFinishedAtUtc
            = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // Widened 15 → 30 s: Revit can surface follow-up dialogs (workset notifications,
        // "file not saved") up to ~20-25 s after a successful sync on large/cloud models.
        // The shorter window let those post-sync dialogs slip past the own-sync gate and
        // re-pop the Join Queue dialog right after the user finished syncing.
        private const int OwnSyncFinishedCooldownSeconds = 30;

        /// <summary>
        /// True when this client just completed (or cancelled) a local sync within the
        /// last <see cref="OwnSyncFinishedCooldownSeconds"/>. Used by the dialog
        /// interceptor to suppress queue-dialog re-pops for follow-up Revit dialogs
        /// (workset notifications, etc.) that arrive in the brief window AFTER
        /// _activeSyncers was cleared by OnLocalSyncCompleted.
        /// </summary>
        public bool IsOwnSyncFinishedRecentlyFor(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return false;
            if (!_lastOwnSyncFinishedAtUtc.TryGetValue(modelGuid, out var finishedAt)) return false;
            return (DateTime.UtcNow - finishedAt).TotalSeconds < OwnSyncFinishedCooldownSeconds;
        }

        /// <summary>
        /// Count of all syncers tracked for a model (active + concurrent peers).
        /// </summary>
        public int GetActiveSyncerCount(string modelGuid)
            => !string.IsNullOrEmpty(modelGuid)
               && _activeSyncers.TryGetValue(modelGuid, out var inner)
                ? inner.Count : 0;

        /// <summary>
        /// Atomically (best-effort) remove a session's entry from a model's active-syncer
        /// set, pruning the outer entry when the inner becomes empty. Returns whether
        /// the inner removal succeeded.
        /// </summary>
        private bool RemoveActiveSyncer(string modelGuid, string sessionId)
        {
            if (string.IsNullOrEmpty(modelGuid) || string.IsNullOrEmpty(sessionId)) return false;
            if (!_activeSyncers.TryGetValue(modelGuid, out var inner)) return false;
            var removed = inner.TryRemove(sessionId, out _);
            // Best-effort outer prune. Race window between IsEmpty and TryRemove is benign
            // — worst case an empty inner lingers until the next CleanupStaleEntries pass.
            if (inner.IsEmpty)
                _activeSyncers.TryRemove(modelGuid, out _);
            return removed;
        }

        /// <summary>
        /// GetOrAdd the inner per-session dictionary for a model. Inner dict uses
        /// case-insensitive sessionId comparison to match the rest of the SignalR/DB
        /// stack.
        /// </summary>
        private ConcurrentDictionary<string, SyncerInfo> GetOrAddInner(string modelGuid)
            => _activeSyncers.GetOrAdd(modelGuid,
                _ => new ConcurrentDictionary<string, SyncerInfo>(StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// Called when local sync completes (DocumentSynchronizedWithCentral).
        /// Clears local state immediately and broadcasts SyncCompleted. The previous
        /// 10s cooldown was removed at user request so the popup never shows a stale
        /// "X is syncing" message after their sync has already finished.
        /// </summary>
        public void OnLocalSyncCompleted(string modelGuid, string sessionId, string username, bool succeeded)
        {
            // Stamp the "just completed local sync" timestamp FIRST so any SyncStarting
            // events that arrive during this method (peers reacting to our completion)
            // are correctly suppressed by JustCompletedLocalSync. Fixes the "Join Sync
            // Queue dialog appears after own sync done" bug.
            if (!string.IsNullOrEmpty(modelGuid))
                _recentLocalCompletions[modelGuid] = DateTime.UtcNow;

            // Capture the original sync start time BEFORE removing the active-syncer slot,
            // so the persisted history row records the real duration (not a "1s" placeholder
            // derived from now-AddSeconds(-1)). Look up our own session's entry directly.
            DateTime syncStartedAt = DateTime.UtcNow.AddSeconds(-1);
            if (_activeSyncers.TryGetValue(modelGuid, out var innerForStart)
                && innerForStart.TryGetValue(sessionId, out var info))
                syncStartedAt = info.StartedAtUtc;

            // Fire-and-forget: broadcast completion (other clients see sync is done),
            // carrying our completion time so peers' out-of-order guard can reject any
            // late SyncStarting echo for this episode.
            _ = BroadcastSyncCompletedAsync(modelGuid, sessionId, username, DateTime.UtcNow);

            // Fire-and-forget: update local DB
            if (_localSyncQueueIds.TryRemove(modelGuid, out var queueId))
            {
                _ = _syncRepository.UpdateSyncQueueStatusAsync(queueId, succeeded ? "completed" : "failed");
            }

            // Fire-and-forget: persist the local user's own sync to model_sync, keyed by
            // sessionId, so the Sync Activity Monitor's LoadHistoryAsync picks it up after
            // the dialog is closed and reopened. Without this, the originator only sees the
            // row live in-memory and it disappears on next open because the server doesn't
            // echo SyncCompleted back to the sender.
            _ = _syncRepository.UpsertRemoteSyncAsync(
                sessionId: sessionId,
                modelGuid: modelGuid,
                modelName: null,
                syncedBy: username,
                computerName: Environment.MachineName,
                startedAtUtc: syncStartedAt,
                endedAtUtc: DateTime.UtcNow,
                isSucceeded: succeeded);

            RemoveActiveSyncer(modelGuid, sessionId);

            // Stamp the post-sync cooldown window — see _lastOwnSyncFinishedAtUtc /
            // IsOwnSyncFinishedRecentlyFor doc. Suppresses queue-dialog re-pops for
            // follow-up Revit dialogs that arrive within ~15s of our successful sync.
            _lastOwnSyncFinishedAtUtc[modelGuid] = DateTime.UtcNow;

            // Belt-and-suspenders queue cleanup at the local-sync-completed boundary.
            // OnLocalSyncStarting already removed us from _modelQueues / _queuedModels, but
            // a missed echo can leave stale entries. Clearing again here guarantees that
            // after our sync completes our own machine never shows us as "In Queue" — which
            // was the user-reported bug: "Even the sync of user 1 is completed, it's still
            // shows he is in queue."
            RemoveFromModelQueue(modelGuid, sessionId);
            _queuedModels.TryRemove(modelGuid, out _);

            // Our sync is finished — there is no longer a pending sync attempt. Clear the
            // interceptor's recency window so any follow-up Revit dialog ("file not saved",
            // workset notifications) is NOT misread as a blocked sync and routed to the
            // Join Queue popup. A fresh Sync click re-arms it via SyncCommandBinding. The
            // IsOwnSyncFinishedRecentlyFor cooldown still covers the immediate aftermath.
            try { SyncConflictDialogInterceptor.ClearSyncAttempt(); } catch { }

            _logger?.LogDebug($"[SyncTrafficControl] Local sync completed for {modelGuid} — slot cleared immediately");
        }

        /// <summary>
        /// User chose "Join Queue" in the SyncConflictDialog.
        /// </summary>
        public void JoinQueue(string modelGuid, string modelName, string sessionId, string username)
        {
            // GUARDRAIL: reject join attempts with missing identity. These produced
            // "------" rows in the Sync Activity Monitor (user-reported 2026-05-27)
            // because the queue entry's username never resolved. Empty sessionId is
            // also dropped — the entry would be unmatched on every subsequent
            // SyncCompleted lookup and stay orphaned forever.
            if (string.IsNullOrWhiteSpace(modelGuid) || string.IsNullOrWhiteSpace(sessionId))
            {
                _logger?.LogWarning($"[SyncTrafficControl] JoinQueue refused — missing modelGuid='{modelGuid}' or sessionId='{sessionId}'. Skipping to prevent ghost queue entries.");
                return;
            }
            if (string.IsNullOrWhiteSpace(username))
            {
                username = !string.IsNullOrWhiteSpace(Environment.UserName) ? Environment.UserName : "unknown";
                _logger?.LogWarning($"[SyncTrafficControl] JoinQueue called with empty username — substituting '{username}' so the queue row renders something readable instead of '------'.");
            }
            // Suppress join when this user JUST completed a sync. Otherwise a peer's
            // SyncStarting fanned out during the brief post-completion window can
            // route here via SyncCommandBinding's blocked-decision path and create
            // a self-queue entry the user never intended.
            if (JustCompletedLocalSync(modelGuid))
            {
                _logger?.LogInfo($"[SyncTrafficControl] JoinQueue refused — local sync for {modelGuid} completed within the last 60 s. Skipping to prevent ghost self-queue.");
                return;
            }

            _logger?.LogInfo($"[SyncTrafficControl] Joining queue for {modelGuid} (sessionId={sessionId}, username='{username}', windowsUser='{Environment.UserName}', machine='{Environment.MachineName}')");

            _queuedModels[modelGuid] = sessionId;

            // Stamp our join time ONCE and reuse it for every emit path (server, both
            // broadcasts, and the local self-event) so our queue position is computed
            // from the same value on every machine — consistent FIFO order everywhere.
            var joinedAtUtc = DateTime.UtcNow;

            // Arm the recency window for SyncConflictDialogInterceptor so Revit's native
            // "File not saved" popup that might appear afterward gets dismissed instead
            // of leaking through to the user. Per the spec, ONLY Join Queue and Sync Now
            // popups should ever appear during the sync queue scenario.
            try { SyncConflictDialogInterceptor.NoteSyncAttempt(); } catch { }

            // Fire-and-forget: tell server to add us to the queue
            _ = SendJoinQueueAsync(modelGuid, sessionId, username);

            // Fire-and-forget: also broadcast SyncQueueUpdate so peers' Sync Activity
            // Monitor dialogs immediately render us as "In Queue" without depending on
            // the server fanning out SyncQueueJoin as SyncQueueUpdate.
            _ = BroadcastSyncQueueUpdateAsync(modelGuid, sessionId, username, joinedAtUtc);

            // PRIMARY queue-announce channel: piggyback on SyncStarting because the
            // server reliably fans SyncStarting out to all model peers. SyncQueueUpdate
            // / SyncQueueJoin have proven unreliable in the field. The QueueAction
            // marker on the payload tells the receiver's SyncQueueListener to route
            // this as a queue event rather than a real sync start.
            _ = BroadcastQueueAnnouncementAsync(modelGuid, sessionId, username, "JoinQueue", joinedAtUtc);

            // Publish a LOCAL SyncQueueUpdate event with SelfCollision=true so OUR OWN
            // Sync Activity Monitor immediately renders us as "In Queue" too. Without
            // this, the SignalR echo of our broadcast is correctly skipped by the
            // dialog's self-filter — so the queued user would see only the syncing
            // user in their history, never themselves. SelfCollision=true tells the
            // ViewModel to bypass the self-filter for this one event so the joining
            // user's own row appears alongside the active syncer.
            try
            {
                _eventBus.Publish(new SyncQueueUpdateEvent
                {
                    ModelGuid = modelGuid,
                    SessionId = sessionId,
                    Username = username,
                    RevitUsername = username,
                    ComputerName = Environment.MachineName,
                    SelfCollision = true,
                    OccurredAtUtc = joinedAtUtc,
                    Source = "JoinQueue-Local"
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncTrafficControl] Failed to publish local JoinQueue event: {ex.Message}");
            }

            // Fire-and-forget: record locally
            _ = RecordSyncQueueAsync(modelGuid, modelName, sessionId, username, "waiting");
        }

        /// <summary>
        /// User cancelled from SyncReadyDialog or wants to leave queue.
        /// </summary>
        public void LeaveQueue(string modelGuid)
        {
            if (_queuedModels.TryRemove(modelGuid, out var sessionId))
            {
                _logger?.LogInfo($"[SyncTrafficControl] Leaving queue for {modelGuid}");
                _ = SendLeaveQueueAsync(modelGuid, sessionId);

                // Piggyback the leave announcement on SyncStarting (reliable channel).
                // Resolve the username from the local session record so peers can render
                // the leave correctly. Empty username is acceptable — receivers only need
                // sessionId + QueueAction to drop the entry.
                _ = BroadcastQueueAnnouncementAsync(modelGuid, sessionId, "", "LeaveQueue", DateTime.UtcNow);

                // Also clean the shared queue list on our own machine; peers will do the
                // same when they receive the SyncStarting-piggybacked LeaveQueue event.
                RemoveFromModelQueue(modelGuid, sessionId);
            }
            // Leaving the queue ends the current "turn" expectation, so the popup guard
            // for this model must reset; otherwise rejoining and getting another turn
            // would be suppressed.
            _activePopupGuard.TryRemove(modelGuid, out _);
        }

        public bool IsQueuedForModel(string modelGuid) => _queuedModels.ContainsKey(modelGuid);

        /// <summary>
        /// Marks a model as having a pending queue-triggered sync.
        /// Called when the user accepts their turn (SyncReadyDialog → "Sync Now").
        /// EvaluateSync will bypass the blocked check for this model within 30 seconds.
        /// </summary>
        public void MarkPendingQueueSync(string modelGuid)
        {
            _pendingQueueSync[modelGuid] = DateTime.UtcNow;
            // Arm the recency window: when user clicks Sync Now, Revit re-fires the sync
            // command which may briefly surface a native "File not saved" popup before
            // our binding catches it. Arming the window ensures the interceptor dismisses
            // that popup instead of leaking it to the user.
            try { SyncConflictDialogInterceptor.NoteSyncAttempt(); } catch { }
            _logger?.LogInfo($"[SyncTrafficControl] Marked pending queue sync for {modelGuid}");
        }

        /// <summary>
        /// Atomic peek-and-consume of the pending queue sync flag. Returns true if the
        /// user clicked Sync Now from the queue popup within the last 30 seconds and
        /// this consumption clears the flag; false if no fresh pending flag exists.
        ///
        /// Used by <c>EventRegistryService.OnDocumentSynchronizingWithCentral</c>'s
        /// fallback path to detect "we're entering the sync handler because the user
        /// just accepted their queue turn" — the fallback should NOT block-and-rejoin
        /// in that case, even if other peers still register as active syncers in the
        /// brief race window (peer's SyncCompleted may not have arrived yet on this
        /// client). The upstream <c>SyncCommandBinding.OnBeforeExecuted</c> normally
        /// consumes this flag via <see cref="EvaluateSync"/>, but in Revit dispatch
        /// paths where BeforeExecuted doesn't fire (observed empirically in v5 test
        /// logs), the flag survives to this fallback as the only signal that the
        /// user has been promoted.
        /// </summary>
        public bool TryConsumePendingQueueSync(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return false;
            if (!_pendingQueueSync.TryRemove(modelGuid, out var markedAt)) return false;
            var age = DateTime.UtcNow - markedAt;
            if (age.TotalSeconds >= 30)
            {
                _logger?.LogDebug($"[SyncTrafficControl] Pending queue sync flag for {modelGuid} was stale ({age.TotalSeconds:F1}s old) — discarding");
                return false;
            }
            _logger?.LogInfo($"[SyncTrafficControl] Consumed pending queue sync flag for {modelGuid} ({age.TotalSeconds:F1}s old) — fallback will allow sync without re-queue");
            return true;
        }

        /// <summary>
        /// Clears the "Sync Now popup already shown for this turn" guard for a model.
        /// Call this from the dialog-decline path so a user who said No can be re-queued
        /// and receive a fresh popup when their next turn arrives.
        /// </summary>
        public void ClearPopupGuard(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return;
            if (_activePopupGuard.TryRemove(modelGuid, out _))
                _logger?.LogDebug($"[SyncTrafficControl] Popup guard cleared for {modelGuid}");
        }

        #region SignalR Event Handlers

        private void OnSyncStarting(SyncStartingEvent e)
        {
            if (string.IsNullOrEmpty(e.ModelGuid)) return;

            // A session moving from queue → syncing must leave the shared queue list.
            // (Applies to BOTH own and remote sessions — keeps every client's queue
            // model in sync with reality so subsequent promotion picks the right user.)
            RemoveFromModelQueue(e.ModelGuid, e.SessionId);

            // Don't overwrite if it's our own broadcast echoed back
            if (e.SessionId == _localSessionId) return;

            // TryAdd, not assignment — when a duplicate echo of this peer's SyncStarting
            // arrives (replay, multi-binding fan-out), we keep the FIRST-seen
            // StartedAtUtc so the "earliest claimant = active" ordering is stable across
            // every client. Different peers each get their own slot keyed by sessionId.
            // Use the SENDER's start timestamp (carried in OccurredAtUtc) so this peer is
            // ordered identically on every client. Fall back to the receiver clock only
            // for older payloads that don't carry it (OccurredAtUtc == MinValue). Stamping
            // the receiver clock here was the cause of the per-user inconsistent view.
            var remoteStartedAt = e.OccurredAtUtc == DateTime.MinValue ? DateTime.UtcNow : e.OccurredAtUtc;
            GetOrAddInner(e.ModelGuid).TryAdd(e.SessionId, new SyncerInfo
            {
                SessionId = e.SessionId,
                Username = e.Username ?? e.RevitUsername,
                RevitUsername = e.RevitUsername,
                StartedAtUtc = remoteStartedAt
            });

            _logger?.LogInfo($"[SyncTrafficControl] Remote sync starting: {e.Username} on {e.ModelGuid}");
            // Persistence is handled upstream by SyncQueueListener (single writer for
            // remote-sync history rows). This service only maintains in-memory state.
        }

        private void OnSyncCompleted(SyncCompletedEvent e)
        {
            if (string.IsNullOrEmpty(e.ModelGuid)) return;

            _logger?.LogInfo($"[SyncTrafficControl] Remote sync completed: {e.ModelGuid} — clearing session {e.SessionId}");

            // Per-session removal: peers that haven't completed yet keep their slots so
            // GetActiveSyncer correctly reports the next-earliest as the new active
            // syncer. Wiping the whole modelGuid (old behavior) would falsely free the
            // slot while other peers are still syncing.
            RemoveActiveSyncer(e.ModelGuid, e.SessionId);
            // Sync turn ended — re-arm the popup guard so the NEXT queued user's
            // SyncRequest produces a fresh Sync Now popup.
            _activePopupGuard.TryRemove(e.ModelGuid, out _);

            // Defensive: also remove this session from the shared queue list. The cleanup
            // already happened in OnSyncStarting when the user moved from queue → syncing,
            // but if that event was missed (server didn't fan it out), the entry would
            // otherwise linger as "still queued" even after their sync finished.
            RemoveFromModelQueue(e.ModelGuid, e.SessionId);

            // CLIENT-SIDE PROMOTION FALLBACK: if I'm the oldest queued user for this model,
            // open my own Sync Now popup. The server is expected to send a targeted
            // SignalRMethods.SyncRequest after a sync completes, but that path has proven
            // unreliable in this environment. Self-triggering here keeps the queue moving
            // even when the server-side push doesn't fire.
            TryPromoteSelfIfNext(e.ModelGuid);
        }

        private void OnSyncCancelled(SyncCancelledEvent e)
        {
            if (string.IsNullOrEmpty(e.ModelGuid)) return;

            // Same per-session removal as OnSyncCompleted: cancelled syncs free only
            // that session's slot; concurrent peer entries remain so GetActiveSyncer
            // can promote the next-earliest.
            RemoveActiveSyncer(e.ModelGuid, e.SessionId);
            _activePopupGuard.TryRemove(e.ModelGuid, out _);
            // Defensive queue cleanup — see OnSyncCompleted comment.
            RemoveFromModelQueue(e.ModelGuid, e.SessionId);
            _logger?.LogInfo($"[SyncTrafficControl] Remote sync cancelled: {e.ModelGuid}");

            TryPromoteSelfIfNext(e.ModelGuid);
        }

        /// <summary>
        /// SignalR <c>UserLeft</c> — fires when a peer's session ends (Revit close,
        /// crash, network drop past server-side disconnect timeout). Evict any
        /// _activeSyncers / _modelQueues entries belonging to that session so a sync
        /// the dead session never completed (no SyncCompleted broadcast) doesn't
        /// linger as a phantom blocker. Without this, a closed Revit instance keeps
        /// blocking every other client until the 10-minute stale TTL fires.
        /// </summary>
        private void OnUserLeft(BIManage.Infrastructure.SignalR.Events.UserLeftEvent e)
        {
            if (string.IsNullOrEmpty(e.ModelGuid) || string.IsNullOrEmpty(e.SessionId)) return;
            // Skip our own session — UserLeft for the local session means we're
            // shutting down; the in-memory state is about to be torn down anyway.
            if (string.Equals(e.SessionId, _localSessionId, StringComparison.OrdinalIgnoreCase))
                return;

            bool removedActive = RemoveActiveSyncer(e.ModelGuid, e.SessionId);
            RemoveFromModelQueue(e.ModelGuid, e.SessionId);

            if (removedActive)
            {
                _logger?.LogInfo($"[SyncTrafficControl] Evicted phantom syncer for departed session {e.SessionId} on {e.ModelGuid} (UserLeft)");
                // The next-earliest claimant (if any) becomes active automatically
                // via GetActiveSyncer. Also try to self-promote in case we were the
                // queued-next user waiting on this phantom to clear.
                _activePopupGuard.TryRemove(e.ModelGuid, out _);
                TryPromoteSelfIfNext(e.ModelGuid);
            }
        }

        /// <summary>
        /// SignalR feed for queue join/update/leave broadcasts. Maintains the per-model
        /// shared queue list (<see cref="_modelQueues"/>) used by both the UI's "Waiting
        /// in Queue" panel and the client-side promotion fallback in <see cref="OnSyncCompleted"/>.
        /// </summary>
        private void OnSyncQueueUpdate(SyncQueueUpdateEvent e)
        {
            if (string.IsNullOrEmpty(e.ModelGuid) || string.IsNullOrEmpty(e.SessionId)) return;

            // Skip the SelfCollision local-publish path — that's UI-only, the real
            // SignalR broadcast that follows carries the same session and will populate
            // the queue list for everyone (including ourselves via the echo).
            // Use the joiner's own join timestamp (carried in OccurredAtUtc) so the FIFO
            // queue order is identical on every client. Stamping the receiver clock made
            // each machine reconstruct a different order (per-user inconsistent view).
            var joinedAtUtc = e.OccurredAtUtc == DateTime.MinValue ? DateTime.UtcNow : e.OccurredAtUtc;

            if (e.SelfCollision && string.Equals(e.SessionId, _localSessionId, StringComparison.OrdinalIgnoreCase))
            {
                AddToModelQueue(e.ModelGuid, e.SessionId, e.RevitUsername ?? e.Username, joinedAtUtc);
                return;
            }

            AddToModelQueue(e.ModelGuid, e.SessionId, e.RevitUsername ?? e.Username, joinedAtUtc);
        }

        private void AddToModelQueue(string modelGuid, string sessionId, string username, DateTime joinedAtUtc)
        {
            var list = _modelQueues.GetOrAdd(modelGuid, _ => new List<QueueEntry>());
            lock (list)
            {
                foreach (var entry in list)
                {
                    if (string.Equals(entry.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                        return; // already queued
                }
                list.Add(new QueueEntry
                {
                    SessionId = sessionId,
                    Username = username ?? "",
                    JoinedAtUtc = joinedAtUtc == DateTime.MinValue ? DateTime.UtcNow : joinedAtUtc
                });
                _logger?.LogInfo($"[SyncTrafficControl] Queue add: session={sessionId}, user={username}, model={modelGuid}, queueSize={list.Count}");
            }
        }

        private void RemoveFromModelQueue(string modelGuid, string sessionId)
        {
            if (!_modelQueues.TryGetValue(modelGuid, out var list)) return;
            lock (list)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(list[i].SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        list.RemoveAt(i);
                        _logger?.LogDebug($"[SyncTrafficControl] Queue remove: session={sessionId}, model={modelGuid}, queueSize={list.Count}");
                    }
                }
            }
        }

        /// <summary>
        /// Returns the current ordered queue for a model (FIFO by join time). Used by
        /// SyncQueueViewModel to populate the "Waiting in Queue" panel on dialog open
        /// and to render every queued user across all clients.
        /// </summary>
        public IReadOnlyList<QueueEntry> GetQueuedSessions(string modelGuid)
        {
            if (!_modelQueues.TryGetValue(modelGuid, out var list))
                return Array.Empty<QueueEntry>();
            lock (list)
            {
                // Sort by JoinedAtUtc ascending so every client renders the queue in the
                // same relative order in the "Waiting in Queue" panel — the position
                // numbers (#1, #2, …) stay consistent across machines even when SignalR
                // events arrive in different orders. Cosmetic only; first-clicker-wins
                // is the actual sync-order decider.
                var sorted = new QueueEntry[list.Count];
                list.CopyTo(sorted);
                Array.Sort(sorted, (a, b) =>
                {
                    int byTime = a.JoinedAtUtc.CompareTo(b.JoinedAtUtc);
                    // Deterministic tiebreak on equal join times so every client renders
                    // the identical order (ordinal SessionId compare).
                    return byTime != 0 ? byTime : string.CompareOrdinal(a.SessionId, b.SessionId);
                });
                return sorted;
            }
        }

        /// <summary>
        /// If the local user is anywhere in the queue for this model, raise the same
        /// SyncRequestReceived event that a server-pushed SyncRequest would. The wired
        /// handler in Application.WireSyncTrafficControlEvents then opens SyncReadyDialog.
        /// <para>
        /// FIRST-CLICKER-WINS model: when the syncer completes, EVERY queued user's
        /// machine gets a Sync Now popup. Whoever clicks first wins the race; the others'
        /// popups auto-close on the winner's SyncStarting broadcast (handled by
        /// SyncReadyDialog's SyncStartingEvent subscription). The race itself is guarded
        /// in EvaluateSync — if two users click within the same millisecond, only one
        /// passes through; the loser re-joins the queue automatically.
        /// </para>
        /// </summary>
        private void TryPromoteSelfIfNext(string modelGuid)
        {
            if (!_modelQueues.TryGetValue(modelGuid, out var list)) return;
            bool meInQueue;
            lock (list)
            {
                meInQueue = false;
                foreach (var e in list)
                {
                    if (string.Equals(e.SessionId, _localSessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        meInQueue = true;
                        break;
                    }
                }
            }
            if (!meInQueue) return;

            // Multi-syncer guard: if other peers still have live SyncStarting entries
            // (claim arbitration didn't fully serialize them), defer promotion. The
            // next SyncCompleted/SyncCancelled will retry. Without this, a JoinQueue
            // user could be promoted while concurrent peers are still finishing.
            if (_activeSyncers.TryGetValue(modelGuid, out var stillActive) && !stillActive.IsEmpty)
            {
                _logger?.LogDebug($"[SyncTrafficControl] Client-side promotion deferred: {stillActive.Count} syncer(s) still active for {modelGuid}");
                return;
            }

            // Popup guard — atomic TryAdd as the gate. Two concurrent promotion
            // paths (OnSyncCompleted from the SignalR thread + OnSyncCancelled from
            // another race, or a server-pushed SyncRequest piggybacking) could each
            // pass a non-atomic `ContainsKey` then `TryAdd` even though only one
            // succeeds, and both end up invoking SyncRequestReceived → two stacked
            // SyncReadyDialogs. By using TryAdd's return as the gate, exactly ONE
            // call ever proceeds to raise the event.
            if (!_activePopupGuard.TryAdd(modelGuid, 0))
            {
                _logger?.LogDebug($"[SyncTrafficControl] Client-side promotion: popup already shown for {modelGuid}, skipping");
                return;
            }

            _logger?.LogInfo($"[SyncTrafficControl] Client-side promotion: I am queued for {modelGuid} — raising SyncRequestReceived (first-clicker-wins race begins)");
            try
            {
                // Marshal to the UI dispatcher so SyncReadyDialog (a WPF dialog) can be
                // constructed on an STA thread. TryPromoteSelfIfNext is called from
                // OnSyncCompleted / OnSyncCancelled which run on the SignalR thread
                // (MTA) — invoking the event directly threw "The calling thread must
                // be STA" and the popup never appeared, leaving the queued user stuck.
                // Mirrors the dispatcher.Invoke already used by OnSyncRequest below.
                _dispatcher?.Invoke(() => SyncRequestReceived?.Invoke(modelGuid, ""));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncTrafficControl] Client-side promotion raise failed: {ex.Message}");
                // Eviction: if the dispatcher invocation itself failed, remove the
                // popup guard so the next promotion attempt isn't suppressed.
                _activePopupGuard.TryRemove(modelGuid, out _);
            }
        }

        /// <summary>
        /// Records a peer's claim into <see cref="_pendingClaims"/> for the ~600ms
        /// arbitration window. SyncCommandBinding consults <see cref="GetCompetingClaims"/>
        /// after broadcasting its own claim and waiting; if any peer claim has an earlier
        /// timestamp (or lower sessionId on tie), the local user backs off and joins the
        /// queue. Claims are scoped per-model and pruned by CleanupStaleEntries.
        /// </summary>
        private void OnSyncClaim(SyncClaimEvent e)
        {
            if (string.IsNullOrEmpty(e.ModelGuid) || string.IsNullOrEmpty(e.SessionId)) return;
            // Skip our own echo — own claim is tracked separately by the caller via its
            // local timestamp; including it here would force GetCompetingClaims to filter
            // by sessionId on every call.
            if (string.Equals(e.SessionId, _localSessionId, StringComparison.OrdinalIgnoreCase))
                return;

            var list = _pendingClaims.GetOrAdd(e.ModelGuid, _ => new List<ClaimEntry>());
            lock (list)
            {
                // Dedup: if we already have a claim from this session within the window,
                // keep the EARLIER timestamp — a slower-arriving duplicate must not move
                // the arbitration anchor forward.
                ClaimEntry existing = null;
                foreach (var c in list)
                {
                    if (string.Equals(c.SessionId, e.SessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        existing = c; break;
                    }
                }
                if (existing != null)
                {
                    if (e.ClaimedAtUtc < existing.ClaimedAtUtc)
                        existing.ClaimedAtUtc = e.ClaimedAtUtc;
                    return;
                }
                list.Add(new ClaimEntry
                {
                    SessionId = e.SessionId,
                    Username = e.Username ?? "",
                    ClaimedAtUtc = e.ClaimedAtUtc
                });
            }
            _logger?.LogDebug($"[SyncClaim] recorded peer claim: session={e.SessionId} user={e.Username} ts={e.ClaimedAtUtc:HH:mm:ss.fff} model={e.ModelGuid}");
        }

        /// <summary>
        /// Atomically reserves the claim phase for one user click. Returns true the
        /// first time it's called within a 2-second window for a model; subsequent
        /// calls return false so the caller can skip the broadcast and the synchronous
        /// wait. Prevents duplicate claims when Revit fires BeforeExecuted multiple
        /// times for one user click (e.g. ID_FILE_SAVE_TO_CENTRAL + SynchronizeNow
        /// both bound). The caller still goes through EvaluateSync + GetCompetingClaims
        /// when false — only the broadcast and the 600ms sleep are skipped.
        /// </summary>
        public bool TryBeginClaimPhase(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return false;
            var now = DateTime.UtcNow;
            // AddOrUpdate atomically writes the new timestamp only if the old one is
            // stale (>2s). Returns the value written.
            DateTime written = _lastClaimBroadcastAt.AddOrUpdate(
                modelGuid,
                _ => now,
                (_, prev) => (now - prev).TotalSeconds >= 2.0 ? now : prev);
            return written == now;
        }

        /// <summary>
        /// Broadcasts the local user's intent to sync over the reliable SyncStarting
        /// channel piggyback (QueueAction="Claim"). Peers' SyncQueueListener routes this
        /// to SyncClaimEvent → OnSyncClaim → _pendingClaims. The caller (SyncCommandBinding)
        /// then waits ~600ms and calls GetCompetingClaims to arbitrate.
        /// </summary>
        public async System.Threading.Tasks.Task BroadcastClaimAsync(string modelGuid, string sessionId, string username, DateTime claimedAtUtc)
        {
            if (!_connectionManager.IsConnected) return;
            try
            {
                await _connectionManager.SendAsync(SignalRMethods.SyncStarting, new
                {
                    ModelGuid = modelGuid,
                    SessionId = sessionId,
                    Username = username,
                    RevitUsername = username,
                    ComputerName = Environment.MachineName,
                    QueueAction = "Claim",
                    ClaimedAtUtcTicks = claimedAtUtc.Ticks
                });
                _logger?.LogInfo($"[SyncClaim] sent session={sessionId} ts={claimedAtUtc:HH:mm:ss.fff} model={modelGuid}");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncClaim] Failed to broadcast claim: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns peer claims for <paramref name="modelGuid"/> that arrived since
        /// <paramref name="sinceUtc"/>. Used by SyncCommandBinding to decide whether the
        /// local sync should proceed or back off into the queue. Excludes the local
        /// session (already filtered on insert).
        /// </summary>
        public IReadOnlyList<ClaimEntry> GetCompetingClaims(string modelGuid, DateTime sinceUtc)
        {
            if (!_pendingClaims.TryGetValue(modelGuid, out var list))
                return Array.Empty<ClaimEntry>();
            lock (list)
            {
                var result = new List<ClaimEntry>(list.Count);
                foreach (var c in list)
                {
                    if (c.ClaimedAtUtc >= sinceUtc)
                        result.Add(new ClaimEntry { SessionId = c.SessionId, Username = c.Username, ClaimedAtUtc = c.ClaimedAtUtc });
                }
                return result;
            }
        }

        /// <summary>
        /// Clears all recorded claims for a model. Called after the local user wins the
        /// arbitration and is about to call OnLocalSyncStarting — any stale peer claims
        /// would otherwise linger until CleanupStaleEntries' 3s TTL.
        /// </summary>
        public void ClearClaims(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return;
            _pendingClaims.TryRemove(modelGuid, out _);
        }

        private void OnSyncRequest(SyncRequestEvent e)
        {
            if (string.IsNullOrEmpty(e.ModelGuid)) return;

            // Check TargetSessionId BEFORE consuming the queue entry — a SyncRequest
            // targeted at a different session must not affect our queue state.
            if (!string.IsNullOrEmpty(e.TargetSessionId) && e.TargetSessionId != _localSessionId)
                return;

            // Atomic consume: the previous ContainsKey + TryRemove pattern was a TOCTOU
            // race — two concurrent SyncRequest deliveries (server fan-out + targeted
            // send, or a SignalR reconnect replaying the same message) could both pass
            // ContainsKey, both call TryRemove, and both raise SyncRequestReceived. That
            // was the "popup appears twice and sync runs twice" bug: the second
            // delivery's TryRemove returned false, but its return value was discarded.
            // Using TryRemove itself as the gate guarantees exactly one delivery wins.
            if (!_queuedModels.TryRemove(e.ModelGuid, out _))
            {
                _logger?.LogDebug($"[SyncTrafficControl] Duplicate SyncRequest for {e.ModelGuid} suppressed — queue entry already consumed.");
                return;
            }

            // Second-line guard: even when JoinQueue has re-armed _queuedModels
            // (BackgroundSyncEngine periodic retry, SignalR replay, explicit rejoin),
            // a Sync Now popup must only appear ONCE per turn. The guard is cleared by
            // SyncCompleted/SyncCancelled/LeaveQueue/ClearPopupGuard when the turn ends.
            if (!_activePopupGuard.TryAdd(e.ModelGuid, 0))
            {
                _logger?.LogDebug($"[SyncTrafficControl] SyncRequest for {e.ModelGuid} suppressed — popup already shown for current turn.");
                return;
            }

            _logger?.LogInfo($"[SyncTrafficControl] SyncRequest received — it's our turn for {e.ModelGuid}!");

            // Raise event on UI thread so the dialog can be shown
            _dispatcher?.Invoke(() => SyncRequestReceived?.Invoke(e.ModelGuid, e.ModelGuid));
        }

        #endregion

        #region SignalR Sends

        private async System.Threading.Tasks.Task BroadcastSyncStartingAsync(string modelGuid, string sessionId, string username, DateTime startedAtUtc)
        {
            if (!_connectionManager.IsConnected) return;
            try
            {
                await _connectionManager.SendAsync(SignalRMethods.SyncStarting, new
                {
                    ModelGuid = modelGuid,
                    SessionId = sessionId,
                    Username = username,
                    RevitUsername = username,
                    ComputerName = Environment.MachineName,
                    // Originator's start time — peers order all syncers by this single
                    // value (not their own arrival clock) so every client agrees on the
                    // active syncer. Relay hub forwards it verbatim.
                    OccurredAtUtcTicks = startedAtUtc.Ticks
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncTrafficControl] Failed to broadcast SyncStarting: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a queue announcement piggybacked on the SyncStarting SignalR method,
        /// because the server has been observed to reliably fan SyncStarting out to all
        /// model peers while SyncQueueJoin / SyncQueueUpdate broadcasts are inconsistent.
        /// The QueueAction field on the payload tells receivers' SyncQueueListener to
        /// route this message to SyncQueueUpdateEvent (not SyncStartingEvent), so the
        /// queuer is NOT recorded as the active syncer on peer machines.
        /// </summary>
        private async System.Threading.Tasks.Task BroadcastQueueAnnouncementAsync(string modelGuid, string sessionId, string username, string queueAction, DateTime joinedAtUtc)
        {
            if (!_connectionManager.IsConnected) return;
            try
            {
                await _connectionManager.SendAsync(SignalRMethods.SyncStarting, new
                {
                    ModelGuid = modelGuid,
                    SessionId = sessionId,
                    Username = username,
                    RevitUsername = username,
                    ComputerName = Environment.MachineName,
                    QueueAction = queueAction,
                    // Joiner's own join time — peers order the queue (FIFO) by this value
                    // so every client renders the same position numbers.
                    OccurredAtUtcTicks = joinedAtUtc.Ticks
                });
                _logger?.LogInfo($"[SyncTrafficControl] Broadcast queue announcement via SyncStarting channel: action={queueAction}, user={username}, model={modelGuid}");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncTrafficControl] Failed to broadcast queue announcement: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task BroadcastSyncCompletedAsync(string modelGuid, string sessionId, string username, DateTime completedAtUtc)
        {
            if (!_connectionManager.IsConnected) return;
            try
            {
                await _connectionManager.SendAsync(SignalRMethods.SyncCompleted, new
                {
                    ModelGuid = modelGuid,
                    SessionId = sessionId,
                    Username = username,
                    RevitUsername = username,
                    ComputerName = Environment.MachineName,
                    // Originator's completion time — lets every receiver's out-of-order
                    // guard reject a late SyncStarting echo that would otherwise flip a
                    // Completed row back to Syncing (status-revert bug).
                    OccurredAtUtcTicks = completedAtUtc.Ticks
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncTrafficControl] Failed to broadcast SyncCompleted: {ex.Message}");
            }
        }

        /// <summary>
        /// Broadcasts a SyncCancelled event for this session — used when EvaluateSync
        /// detects that OUR session is the active syncer in <c>_activeSyncers</c> but we
        /// have no actual sync in progress (typically: prior Revit instance crashed
        /// before its own SyncCompleted, leaving a phantom entry on every peer for the
        /// full 10-min stale TTL). Peers' <c>OnSyncCancelled</c> handler removes the
        /// matching session entry from their own <c>_activeSyncers</c>, breaking the
        /// phantom block immediately instead of waiting for the TTL.
        /// </summary>
        private async System.Threading.Tasks.Task BroadcastSyncCancelledAsync(string modelGuid, string sessionId, string username)
        {
            if (!_connectionManager.IsConnected) return;
            try
            {
                await _connectionManager.SendAsync(SignalRMethods.SyncCancelled, new
                {
                    ModelGuid = modelGuid,
                    SessionId = sessionId,
                    Username = username,
                    RevitUsername = username,
                    ComputerName = Environment.MachineName
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncTrafficControl] Failed to broadcast SyncCancelled: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task SendJoinQueueAsync(string modelGuid, string sessionId, string username)
        {
            if (!_connectionManager.IsConnected) return;
            try
            {
                await _connectionManager.SendAsync(SignalRMethods.SyncQueueJoin, new
                {
                    ModelGuid = modelGuid,
                    SessionId = sessionId,
                    Username = username,
                    RevitUsername = username
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncTrafficControl] Failed to send SyncQueueJoin: {ex.Message}");
            }
        }

        /// <summary>
        /// Broadcasts SyncQueueUpdate so peers' Sync Activity Monitor dialogs immediately
        /// render this user as "In Queue". Sent in addition to SyncQueueJoin because the
        /// server's fan-out of SyncQueueJoin into SyncQueueUpdate is not reliable across
        /// all model groups — broadcasting directly removes that dependency.
        /// </summary>
        private async System.Threading.Tasks.Task BroadcastSyncQueueUpdateAsync(string modelGuid, string sessionId, string username, DateTime joinedAtUtc)
        {
            if (!_connectionManager.IsConnected) return;
            try
            {
                await _connectionManager.SendAsync(SignalRMethods.SyncQueueUpdate, new
                {
                    ModelGuid = modelGuid,
                    SessionId = sessionId,
                    Username = username,
                    RevitUsername = username,
                    ComputerName = Environment.MachineName,
                    SelfCollision = false,
                    OccurredAtUtcTicks = joinedAtUtc.Ticks
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncTrafficControl] Failed to broadcast SyncQueueUpdate: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task SendLeaveQueueAsync(string modelGuid, string sessionId)
        {
            if (!_connectionManager.IsConnected) return;
            try
            {
                await _connectionManager.SendAsync(SignalRMethods.SyncQueueLeave, new
                {
                    ModelGuid = modelGuid,
                    SessionId = sessionId
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncTrafficControl] Failed to send SyncQueueLeave: {ex.Message}");
            }
        }

        #endregion

        #region Local DB

        private async System.Threading.Tasks.Task RecordSyncQueueAsync(string modelGuid, string modelName, string sessionId, string username, string status)
        {
            try
            {
                var queueId = await _syncRepository.AddToSyncQueueAsync(modelGuid, modelName, sessionId, username, status);
                if (status == "syncing")
                {
                    _localSyncQueueIds[modelGuid] = queueId;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncTrafficControl] Failed to record sync queue: {ex.Message}");
            }
        }

        #endregion

        #region Stale Cleanup

        private void CleanupStaleEntries()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            foreach (var outerKvp in _activeSyncers)
            {
                var inner = outerKvp.Value;
                foreach (var innerKvp in inner)
                {
                    if (innerKvp.Value.StartedAtUtc < cutoff)
                    {
                        if (inner.TryRemove(innerKvp.Key, out _))
                        {
                            _logger?.LogWarning($"[SyncTrafficControl] Cleaned stale sync entry: {innerKvp.Value.Username} on {outerKvp.Key}");
                        }
                    }
                }
                // Best-effort outer prune when the inner empties out.
                if (inner.IsEmpty)
                    _activeSyncers.TryRemove(outerKvp.Key, out _);
            }

            // Clean up expired pending queue syncs (30-second TTL)
            var pendingCutoff = DateTime.UtcNow.AddSeconds(-30);
            foreach (var kvp in _pendingQueueSync)
            {
                if (kvp.Value < pendingCutoff)
                    _pendingQueueSync.TryRemove(kvp.Key, out _);
            }

            // Prune _lastOwnSyncFinishedAtUtc — entries older than 4x the cooldown
            // can never satisfy IsOwnSyncFinishedRecentlyFor, so they're dead weight.
            // Without periodic pruning, a user who touches many models in a session
            // accumulates one stale DateTime per modelGuid forever. Microscopic in
            // practice but worth keeping clean.
            var finishedCutoff = DateTime.UtcNow.AddSeconds(-OwnSyncFinishedCooldownSeconds * 4);
            foreach (var kvp in _lastOwnSyncFinishedAtUtc)
            {
                if (kvp.Value < finishedCutoff)
                    _lastOwnSyncFinishedAtUtc.TryRemove(kvp.Key, out _);
            }

            // Prune stale claims (3-second TTL). The arbitration window is ~600ms;
            // anything older than 3s is past-relevance and only takes memory.
            var claimCutoff = DateTime.UtcNow.AddSeconds(-3);
            foreach (var kvp in _pendingClaims)
            {
                var list = kvp.Value;
                lock (list)
                {
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i].ClaimedAtUtc < claimCutoff)
                            list.RemoveAt(i);
                    }
                    if (list.Count == 0)
                        _pendingClaims.TryRemove(kvp.Key, out _);
                }
            }
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _staleCleanupTimer?.Dispose();

            _eventBus.Unsubscribe(_onSyncStarting);
            _eventBus.Unsubscribe(_onSyncCompleted);
            _eventBus.Unsubscribe(_onSyncCancelled);
            _eventBus.Unsubscribe(_onSyncRequest);
            _eventBus.Unsubscribe(_onSyncQueueUpdate);
            _eventBus.Unsubscribe(_onSyncClaim);
            _eventBus.Unsubscribe(_onUserLeft);

            // Leave any pending queues
            foreach (var kvp in _queuedModels)
            {
                try { _ = SendLeaveQueueAsync(kvp.Key, kvp.Value); } catch { }
            }

            _logger?.LogInfo("[SyncTrafficControl] Disposed");
        }
    }
}
