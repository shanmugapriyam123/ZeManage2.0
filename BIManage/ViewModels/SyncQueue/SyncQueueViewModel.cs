using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using BIManage.Core.Identity;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Core;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Infrastructure.SignalR.Messages;
using BIManage.Revit.SyncTrafficControl;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BIManage.ViewModels.SyncQueue
{
    public class SyncQueueViewModel : ObservableObject
    {
        private readonly ILogger? _logger;
        private readonly ISignalREventBus? _eventBus;
        private readonly ISignalRConnectionManager? _connectionManager;
        private readonly SyncRepository? _syncRepository;
        private readonly SessionRepository? _sessionRepository;
        private readonly SyncTrafficControlService? _syncTrafficControl;
        private readonly string? _currentModelGuid;
        private readonly string? _currentSessionId;
        private readonly Dispatcher _dispatcher;

        // Periodic cleanup for stale "Syncing" rows whose SyncCompleted event was missed.
        // Without this, a syncer that finished but whose completion didn't reach this client
        // would appear as "Syncing" forever, blocking the queue's perceived progress.
        private DispatcherTimer? _staleCleanupTimer;
        private static readonly TimeSpan StaleSyncCutoff = TimeSpan.FromMinutes(5);

        // Stored delegate references required for unsubscription
        private Action<SyncStartingEvent>? _onSyncStarting;
        private Action<SyncCompletedEvent>? _onSyncCompleted;
        private Action<SyncCancelledEvent>? _onSyncCancelled;
        private Action<SyncQueueUpdateEvent>? _onSyncQueueUpdate;

        private int _syncingNowCount;
        private int _completedCount;
        private int _totalCount;
        private int _queuedCount;
        private string _statusMessage = "Listening for sync activity...";

        public int SyncingNowCount
        {
            get => _syncingNowCount;
            set => SetProperty(ref _syncingNowCount, value);
        }

        public int CompletedCount
        {
            get => _completedCount;
            set => SetProperty(ref _completedCount, value);
        }

        public int TotalCount
        {
            get => _totalCount;
            set => SetProperty(ref _totalCount, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        // Header status pill shown next to the "Sync Activity Monitor" title — answers the
        // three "why am I not seeing live updates?" questions at a glance:
        //   - "No model open"      → user opened dialog without a workshared model active
        //   - "Disconnected"       → SignalR is down; events neither sent nor received
        //   - "Live"               → connected + model in scope; events should flow
        // Updated on dialog open and by the 5-second refresh timer.
        private string _headerStatusText = "";
        public string HeaderStatusText
        {
            get => _headerStatusText;
            set => SetProperty(ref _headerStatusText, value);
        }

        private string _headerStatusKind = "Live"; // "Live" | "Disconnected" | "NoModel"
        public string HeaderStatusKind
        {
            get => _headerStatusKind;
            set => SetProperty(ref _headerStatusKind, value);
        }

        /// <summary>
        /// Recomputes <see cref="HeaderStatusText"/> + <see cref="HeaderStatusKind"/>
        /// from current model + SignalR connection state. Call on dialog open and on
        /// each refresh-timer tick.
        /// </summary>
        public void RefreshHeaderStatus()
        {
            if (string.IsNullOrEmpty(_currentModelGuid))
            {
                HeaderStatusKind = "NoModel";
                HeaderStatusText = "No workshared model open";
                return;
            }
            if (_connectionManager == null || !_connectionManager.IsConnected)
            {
                HeaderStatusKind = "Disconnected";
                HeaderStatusText = "SignalR disconnected — live updates paused";
                return;
            }
            HeaderStatusKind = "Live";
            HeaderStatusText = "Live";
        }

        public int QueuedCount
        {
            get => _queuedCount;
            set => SetProperty(ref _queuedCount, value);
        }

        public ObservableCollection<SyncActivityItem> SyncHistory { get; } = new ObservableCollection<SyncActivityItem>();
        public ObservableCollection<SyncingUserDisplay> SyncingUsers { get; } = new ObservableCollection<SyncingUserDisplay>();

        // Users waiting in line behind the active syncer. Mirrors the "InQueue" rows in
        // SyncHistory in arrival order so the dialog can render a "Waiting in Queue"
        // panel with positions (1., 2., 3.). The grid already showed an "In Queue"
        // badge but users couldn't tell the ORDER from it — this collection makes
        // the queue ordering explicit.
        public ObservableCollection<QueuedUserDisplay> QueuedUsers { get; } = new ObservableCollection<QueuedUserDisplay>();

        public SyncQueueViewModel(
            ILogger? logger = null,
            ISignalREventBus? eventBus = null,
            string? modelGuid = null,
            ISignalRConnectionManager? connectionManager = null,
            string? sessionId = null,
            SyncRepository? syncRepository = null,
            SyncTrafficControlService? syncTrafficControl = null,
            SessionRepository? sessionRepository = null)
        {
            _logger = logger;
            _eventBus = eventBus;
            _currentModelGuid = modelGuid;
            _connectionManager = connectionManager;
            _currentSessionId = sessionId;
            _syncRepository = syncRepository;
            _syncTrafficControl = syncTrafficControl;
            _sessionRepository = sessionRepository;
            _dispatcher = Dispatcher.CurrentDispatcher;
        }

        /// <summary>
        /// Pre-populate SyncingUsers when the dialog opens. Pulls every active syncer for
        /// the current model from the persisted sync_queue table (not just the one slot
        /// in SyncTrafficControlService._activeSyncers, which only tracks the most recent),
        /// then falls back to the in-memory traffic-control snapshot if the DB is empty.
        /// Without this, when 4+ users are syncing concurrently the dialog only showed the
        /// one slot from the in-memory dictionary and missed the others.
        /// </summary>
        public async void LoadActiveRemoteSyncers()
        {
            if (string.IsNullOrEmpty(_currentModelGuid)) return;
            try
            {
                int loadedFromDb = 0;
                var seenSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_syncRepository != null)
                {
                    var activeEntries = await _syncRepository.GetActiveSyncersForModelAsync(_currentModelGuid!);
                    // Also pull in-flight rows from model_sync — these are the remote-user
                    // sync starts persisted by SyncQueueListener even when the dialog was
                    // closed when their SignalR event arrived.
                    var inFlightRemote = await _syncRepository.GetActiveRemoteSyncsFromModelSyncAsync(_currentModelGuid!);

                    // STALE EVICTION: build the set of sessionIds whose model_sync row has a
                    // sync_ended_at — those syncs DID complete; any matching sync_queue row
                    // with status='syncing' is stale and must NOT be loaded as currently syncing.
                    // GetActiveRemoteSyncsFromModelSyncAsync only returns IN-FLIGHT rows
                    // (sync_ended_at IS NULL), so a sessionId that appears in activeEntries but
                    // NOT in inFlightRemote indicates the completion happened on the model_sync
                    // side but the sync_queue side wasn't updated. Skip those.
                    var inFlightSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in inFlightRemote)
                    {
                        if (!string.IsNullOrWhiteSpace(r.SessionId))
                            inFlightSessionIds.Add(r.SessionId);
                    }

                    RunOnUiThread(() =>
                    {
                        foreach (var entry in activeEntries)
                        {
                            // Skip our own session — we don't list ourselves as a remote syncer
                            if (!string.IsNullOrEmpty(_currentSessionId)
                                && string.Equals(entry.SessionId, _currentSessionId, StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (!seenSessionIds.Add(entry.SessionId)) continue;

                            // Stale: if model_sync has no in-flight row for this session, the
                            // sync_queue 'syncing' row is leftover from a missed completion.
                            // Don't show it as Syncing — let it appear in history (if present)
                            // via LoadHistoryAsync, but skip the live SyncingUsers panel entry.
                            if (!inFlightSessionIds.Contains(entry.SessionId))
                            {
                                _logger?.LogInfo($"[SyncQueue] Skipping stale sync_queue 'syncing' row for session {entry.SessionId} — no matching in-flight model_sync row");
                                continue;
                            }

                            var entryName = ResolveDisplaySync(entry.SessionId, entry.Username, computerName: null);
                            // Don't seed the panel with synthetic placeholders ("User-abcd1234"
                            // or "—"). A "Syncing" row with no real username surfaces as a
                            // blank "—" in the grid and a useless chip; the SignalR event
                            // for this session will arrive shortly with the resolved name
                            // and populate the row properly. Skip until then.
                            if (IsSyntheticDisplayName(entryName))
                            {
                                _logger?.LogDebug($"[SyncQueue] Skipping DB-load row for session {entry.SessionId} — name unresolved (would render as placeholder)");
                                ResolveAndPatchUsername(entry.SessionId, entry.Username, computerName: null);
                                continue;
                            }
                            AddSyncingUser(entry.SessionId, entryName, entryName, computerName: "");
                            AddSyncActivity(entry.SessionId, entryName, computerName: "", status: "Syncing");
                            ResolveAndPatchUsername(entry.SessionId, entry.Username, computerName: null);
                            loadedFromDb++;
                        }
                        foreach (var row in inFlightRemote)
                        {
                            if (!string.IsNullOrEmpty(_currentSessionId)
                                && string.Equals(row.SessionId, _currentSessionId, StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (!seenSessionIds.Add(row.SessionId)) continue;

                            var name = ResolveDisplaySync(row.SessionId, row.SyncedBy, computerName: null);
                            if (IsSyntheticDisplayName(name))
                            {
                                _logger?.LogDebug($"[SyncQueue] Skipping DB-load row for session {row.SessionId} — name unresolved (would render as placeholder)");
                                ResolveAndPatchUsername(row.SessionId, row.SyncedBy, computerName: null);
                                continue;
                            }
                            AddSyncingUser(row.SessionId, name, name, computerName: "");
                            AddSyncActivity(row.SessionId, name, computerName: "", status: "Syncing");
                            ResolveAndPatchUsername(row.SessionId, row.SyncedBy, computerName: null);
                            loadedFromDb++;
                        }

                        // QUEUE REHYDRATION: pull the shared queue list from the traffic-control
                        // service. This list is populated by SignalR `SyncQueueUpdate` events the
                        // service receives even when the dialog is CLOSED — so opening the dialog
                        // mid-scenario shows every currently-queued peer, not just whoever joined
                        // while the dialog was open.
                        if (_syncTrafficControl != null)
                        {
                            var queueEntries = _syncTrafficControl.GetQueuedSessions(_currentModelGuid!);
                            foreach (var q in queueEntries)
                            {
                                if (string.IsNullOrEmpty(q.SessionId)) continue;
                                // Skip if already represented as syncing — the cross-list dedup
                                // in AddSyncActivity / AddQueuedUser will catch it again as a
                                // safety net, but skipping early keeps the log cleaner.
                                bool alreadySyncing = false;
                                foreach (var s in SyncingUsers)
                                {
                                    if (string.Equals(s.SessionId, q.SessionId, StringComparison.OrdinalIgnoreCase))
                                    {
                                        alreadySyncing = true;
                                        break;
                                    }
                                }
                                if (alreadySyncing) continue;

                                var qName = ResolveDisplaySync(q.SessionId, q.Username, computerName: null);
                                if (IsSyntheticDisplayName(qName))
                                {
                                    _logger?.LogDebug($"[SyncQueue] Skipping queue-rehydration row for session {q.SessionId} — name unresolved");
                                    ResolveAndPatchUsername(q.SessionId, q.Username, computerName: null);
                                    continue;
                                }
                                AddSyncActivity(q.SessionId, qName, computerName: "", status: "InQueue", eventTimestampUtc: q.JoinedAtUtc);
                                ResolveAndPatchUsername(q.SessionId, q.Username, computerName: null);
                            }
                            if (queueEntries.Count > 0)
                                _logger?.LogInfo($"[SyncQueue] Rehydrated {queueEntries.Count} queue entr(y/ies) from in-memory shared queue for model {_currentModelGuid}");
                        }

                        // Final reconciliation: ensure SyncingUsers + QueuedUsers exactly
                        // mirror what the grid says is Syncing / InQueue. This is the heartbeat
                        // that self-heals any chip-vs-grid drift accumulated since the dialog
                        // last refreshed (missed SignalR events, stale rows, etc.).
                        ReconcilePanelsToGrid();
                        // UpdateCounts sets StatusMessage based on actual SyncingUsers /
                        // QueuedUsers counts — no need for an explicit "N users are syncing"
                        // override here, which previously got stuck on initial load.
                        UpdateCounts();
                    });
                    _logger?.LogDebug($"[SyncQueue] Pre-populated {loadedFromDb} active remote syncer(s) from DB (sync_queue + model_sync in-flight)");
                }

                // Fallback to the in-memory traffic-control snapshot if the DB returned nothing
                // (e.g. fresh install with no rows yet, or DB was just cleared).
                if (loadedFromDb == 0 && _syncTrafficControl != null)
                {
                    var info = _syncTrafficControl.GetActiveSyncer(_currentModelGuid!);
                    if (info == null) return;
                    if (!string.IsNullOrEmpty(_currentSessionId)
                        && string.Equals(info.SessionId, _currentSessionId, StringComparison.OrdinalIgnoreCase))
                        return;
                    RunOnUiThread(() =>
                    {
                        var infoName = ResolveDisplaySync(info.SessionId, info.RevitUsername ?? info.Username, computerName: null);
                        if (IsSyntheticDisplayName(infoName))
                        {
                            _logger?.LogDebug($"[SyncQueue] Skipping in-memory snapshot row for session {info.SessionId} — name unresolved");
                            ResolveAndPatchUsername(info.SessionId, info.RevitUsername ?? info.Username, computerName: null);
                            return;
                        }
                        AddSyncingUser(info.SessionId, info.Username ?? infoName, info.RevitUsername ?? infoName, computerName: "");
                        AddSyncActivity(info.SessionId, infoName, computerName: "", status: "Syncing", eventTimestampUtc: info.StartedAtUtc);
                        ResolveAndPatchUsername(info.SessionId, info.RevitUsername ?? info.Username, computerName: null);
                        ReconcilePanelsToGrid();
                        UpdateCounts();
                        StatusMessage = $"{infoName} is syncing";
                    });
                    _logger?.LogDebug($"[SyncQueue] Pre-populated active remote syncer from in-memory snapshot: {info.RevitUsername ?? info.Username}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[SyncQueue] LoadActiveRemoteSyncers failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Subscribe to SignalR sync events for real-time updates.
        /// </summary>
        public void SubscribeSignalREvents()
        {
            if (_eventBus == null)
                return;

            _onSyncStarting = e =>
            {
                if (!string.IsNullOrEmpty(_currentModelGuid) && e.ModelGuid != _currentModelGuid)
                    return;
                // ResolveDisplaySync walks payload → cache → computer-name → short-session-id label.
                // Never returns "Unknown user".
                var providedName = !string.IsNullOrWhiteSpace(e.RevitUsername) ? e.RevitUsername : e.Username;
                var displayUser = ResolveDisplaySync(e.SessionId, providedName, e.ComputerName);
                // Don't seed the grid with synthetic placeholders ("—" or "User-XXXXXXXX").
                // Kick off the async lookup but skip the row insert until a real name is
                // available. The next event for this session (heartbeat update, next
                // sync attempt) will arrive with the resolved name and render properly.
                if (IsSyntheticDisplayName(displayUser))
                {
                    _logger?.LogDebug($"[SyncQueue] Skipping SyncStarting for session {e.SessionId} — name unresolved (would render as placeholder)");
                    ResolveAndPatchUsername(e.SessionId, providedName, e.ComputerName);
                    return;
                }
                RunOnUiThread(() =>
                {
                    AddSyncingUser(e.SessionId, e.Username ?? displayUser, e.RevitUsername ?? displayUser, e.ComputerName);
                    AddSyncActivity(e.SessionId, displayUser, e.ComputerName, "Syncing", e.OccurredAtUtc);
                    // Panels must mirror the grid AFTER the grid mutation — otherwise a
                    // stale chip from a missed prior SyncCompleted blocks the new syncer
                    // from appearing in "Currently Syncing".
                    ReconcilePanelsToGrid();
                    UpdateCounts();
                    StatusMessage = $"{displayUser} started syncing";
                });
                // If we couldn't resolve a real name synchronously, kick off the async DB lookup
                // and patch the row in-place when the sessions table catches up.
                ResolveAndPatchUsername(e.SessionId, providedName, e.ComputerName);
                // Persistence handled by SyncQueueListener (single writer for remote-sync rows).
            };

            _onSyncCompleted = e =>
            {
                if (!string.IsNullOrEmpty(_currentModelGuid) && e.ModelGuid != _currentModelGuid)
                    return;
                RunOnUiThread(() =>
                {
                    var user = RemoveSyncingUser(e.SessionId);
                    var providedName = user?.DisplayName
                        ?? (!string.IsNullOrWhiteSpace(e.RevitUsername) ? e.RevitUsername : e.Username);
                    var displayName = ResolveDisplaySync(e.SessionId, providedName, user?.ComputerName ?? e.ComputerName);
                    var computerName = user?.ComputerName ?? e.ComputerName ?? "";
                    // If the completion event lacks a usable identity, don't overwrite
                    // the existing row's Username with "—" or "User-XXXX". Pass null so
                    // CompleteSyncActivity keeps the prior real name (set when the row
                    // was originally inserted by SyncStarting).
                    var nameForRow = IsSyntheticDisplayName(displayName) ? null : displayName;
                    CompleteSyncActivity(e.SessionId, user, nameForRow, computerName, e.OccurredAtUtc);
                    ReconcilePanelsToGrid();
                    UpdateCounts();
                    if (!IsSyntheticDisplayName(displayName))
                        StatusMessage = $"{displayName} completed sync";
                });
                // Persistence handled by SyncQueueListener (single writer for remote-sync rows).
            };

            _onSyncCancelled = e =>
            {
                if (!string.IsNullOrEmpty(_currentModelGuid) && e.ModelGuid != _currentModelGuid)
                    return;
                RunOnUiThread(() =>
                {
                    var user = RemoveSyncingUser(e.SessionId);
                    var providedName = user?.DisplayName
                        ?? (!string.IsNullOrWhiteSpace(e.RevitUsername) ? e.RevitUsername : e.Username);
                    var displayName = ResolveDisplaySync(e.SessionId, providedName, user?.ComputerName ?? e.ComputerName);
                    var computerName = user?.ComputerName ?? e.ComputerName ?? "";
                    // Same as _onSyncCompleted — don't clobber a real name with a placeholder.
                    var nameForRow = IsSyntheticDisplayName(displayName) ? null : displayName;
                    CancelSyncActivity(e.SessionId, user, nameForRow, computerName, e.OccurredAtUtc);
                    ReconcilePanelsToGrid();
                    UpdateCounts();
                    if (!IsSyntheticDisplayName(displayName))
                        StatusMessage = $"{displayName} cancelled sync";
                });
                // Persistence handled by SyncQueueListener (single writer for remote-sync rows).
            };

            _onSyncQueueUpdate = e =>
            {
                if (!string.IsNullOrEmpty(_currentModelGuid) && e.ModelGuid != _currentModelGuid)
                    return;
                // Skip our own session — joining the queue locally is normally handled by the
                // SyncConflictDialog flow; the dialog only needs to reflect REMOTE joiners.
                // EXCEPTION: SelfCollision events come from the 1-second post-start race-condition
                // check (two clients started syncing simultaneously and neither saw the other in
                // EvaluateSync). In that case we DO want to show our own row as InQueue.
                if (!e.SelfCollision
                    && !string.IsNullOrEmpty(_currentSessionId)
                    && string.Equals(e.SessionId, _currentSessionId, StringComparison.OrdinalIgnoreCase))
                    return;
                var providedName = !string.IsNullOrWhiteSpace(e.RevitUsername) ? e.RevitUsername : e.Username;
                var displayUser = ResolveDisplaySync(e.SessionId, providedName, e.ComputerName);
                // Same gate as _onSyncStarting — don't seed an "In Queue" row with a
                // synthetic placeholder. ResolveAndPatchUsername still fires so the
                // resolver gets a chance to learn this session's real name; the next
                // event for it (heartbeat, real SyncStarting echo) inserts properly.
                if (IsSyntheticDisplayName(displayUser))
                {
                    _logger?.LogDebug($"[SyncQueue] Skipping SyncQueueUpdate for session {e.SessionId} — name unresolved (would render as placeholder)");
                    ResolveAndPatchUsername(e.SessionId, providedName, e.ComputerName);
                    return;
                }
                RunOnUiThread(() =>
                {
                    AddSyncActivity(e.SessionId, displayUser, e.ComputerName, "InQueue", e.OccurredAtUtc);
                    ReconcilePanelsToGrid();
                    UpdateCounts();
                    StatusMessage = $"{displayUser} joined the queue";
                });
                ResolveAndPatchUsername(e.SessionId, providedName, e.ComputerName);
            };

            _eventBus.Subscribe(_onSyncStarting);
            _eventBus.Subscribe(_onSyncCompleted);
            _eventBus.Subscribe(_onSyncCancelled);
            _eventBus.Subscribe(_onSyncQueueUpdate);

            // Start the stale-Syncing janitor. Fires every 30 seconds and closes any
            // "Syncing" row whose StartedAt is older than StaleSyncCutoff (5 min).
            _staleCleanupTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _staleCleanupTimer.Tick += CleanupStaleSyncingRows;
            _staleCleanupTimer.Start();

            _logger?.LogDebug($"[SyncQueue] Subscribed to SignalR sync events (model: {_currentModelGuid ?? "any"})");
            StatusMessage = "Listening for sync activity...";
        }

        /// <summary>
        /// Janitor: marks "Syncing" history rows older than <see cref="StaleSyncCutoff"/> as
        /// "Completed" so the UI doesn't show ghost-syncers when the SyncCompleted event was
        /// lost in transit. Runs on the dispatcher thread (no marshalling needed).
        /// </summary>
        private void CleanupStaleSyncingRows(object? sender, EventArgs e)
        {
            try
            {
                var cutoff = DateTime.Now - StaleSyncCutoff;
                int closed = 0;
                for (int i = SyncHistory.Count - 1; i >= 0; i--)
                {
                    var item = SyncHistory[i];
                    if (item.Status != "Syncing") continue;
                    if (item.StartedAt > cutoff) continue;

                    item.Status = "Completed";
                    item.CompletedAt = DateTime.Now;
                    item.StatusDisplay = "Completed";
                    item.Duration = FormatDuration(item.StartedAt, item.CompletedAt.Value);
                    RemoveSyncingUser(item.SessionId);
                    RemoveQueuedUser(item.SessionId);
                    closed++;
                }
                if (closed > 0)
                {
                    ReconcilePanelsToGrid();
                    UpdateCounts();
                    _logger?.LogInfo($"[SyncQueue] Stale cleanup: marked {closed} Syncing row(s) older than {StaleSyncCutoff.TotalMinutes} min as Completed");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[SyncQueue] CleanupStaleSyncingRows failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Unsubscribe from SignalR sync events. Call when the dialog closes.
        /// </summary>
        public void UnsubscribeSignalREvents()
        {
            if (_eventBus == null)
                return;

            if (_onSyncStarting != null)  { _eventBus.Unsubscribe(_onSyncStarting);  _onSyncStarting = null; }
            if (_onSyncCompleted != null)  { _eventBus.Unsubscribe(_onSyncCompleted); _onSyncCompleted = null; }
            if (_onSyncCancelled != null)  { _eventBus.Unsubscribe(_onSyncCancelled); _onSyncCancelled = null; }
            if (_onSyncQueueUpdate != null) { _eventBus.Unsubscribe(_onSyncQueueUpdate); _onSyncQueueUpdate = null; }

            if (_staleCleanupTimer != null)
            {
                _staleCleanupTimer.Stop();
                _staleCleanupTimer.Tick -= CleanupStaleSyncingRows;
                _staleCleanupTimer = null;
            }

            _logger?.LogDebug("[SyncQueue] Unsubscribed from SignalR sync events");
        }

        /// <summary>
        /// Clears the in-memory history list AND deletes the underlying completed
        /// sync_history rows from SQLite. Without the DB delete, LoadHistoryAsync
        /// (called every time the dialog opens) re-hydrates the list from storage,
        /// making the cleared entries reappear on next open.
        /// </summary>
        public void ClearHistory()
        {
            SyncHistory.Clear();
            CompletedCount = 0;
            TotalCount = SyncingUsers.Count;
            StatusMessage = "History cleared";

            // Persist the clear so it survives reopening the dialog. Active (in-flight)
            // syncs are preserved by the repository — only completed rows are removed.
            if (_syncRepository != null && !string.IsNullOrEmpty(_currentModelGuid))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var deleted = await _syncRepository.DeleteModelSyncHistoryAsync(_currentModelGuid!);
                        _logger?.LogInfo($"[SyncQueue] Persisted Clear History for model {_currentModelGuid} ({deleted} rows)");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning($"[SyncQueue] Failed to persist clear history: {ex.Message}");
                    }
                });
            }
        }

        private void AddSyncingUser(string sessionId, string username, string revitUsername, string computerName)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;

            var nameKey = NormalizeUsername(revitUsername ?? username);

            // Dedup pass first: bail if the same session OR the same human (via another
            // session) is already in "Currently Syncing". While iterating, also detect
            // whether ANY different user is already syncing — used by the multi-slot
            // guard below.
            bool anotherDifferentUserSyncing = false;
            foreach (var existing in SyncingUsers)
            {
                if (existing.SessionId == sessionId)
                    return;
                if (!string.IsNullOrEmpty(nameKey)
                    && string.Equals(NormalizeUsername(existing.Username), nameKey, StringComparison.OrdinalIgnoreCase))
                    return; // same human, different session — don't double-add
                anotherDifferentUserSyncing = true;
            }

            // Multi-slot guard: when another user is already shown as syncing, this
            // incoming SyncStarting belongs to a peer who's blocked at Revit's native
            // central-lock layer (claim arbitration didn't fully serialize them). Route
            // it to the queued panel instead of stacking another chip in "Currently
            // Syncing" — keeps the chip panel consistent with the per-row demotion
            // already done by AddSyncActivity → IsActiveSyncingByDifferentUser.
            if (anotherDifferentUserSyncing)
            {
                var resolvedForQueue = ResolveDisplaySync(sessionId, username, computerName);
                AddQueuedUser(sessionId, resolvedForQueue, computerName ?? "");
                return;
            }

            // Original transition path: this user becomes the active syncer.
            //
            // A session that's transitioning from queue → syncing must leave the
            // "Waiting in Queue" panel. Without this, the SAME user can appear in
            // BOTH "Currently Syncing" AND "Waiting in Queue" simultaneously, which
            // is logically impossible and was the visible bug in the screenshot.
            RemoveQueuedUser(sessionId);

            // ALSO remove any queued entries whose Username matches the user being added.
            // Covers the case where the same human has two different sessionIds (stale
            // session from a previous Revit run + new live session) and would otherwise
            // appear in both lists. SessionId-only dedup doesn't catch this.
            if (!string.IsNullOrEmpty(nameKey))
            {
                for (int i = QueuedUsers.Count - 1; i >= 0; i--)
                {
                    var q = QueuedUsers[i];
                    if (string.Equals(NormalizeUsername(q.DisplayName), nameKey, StringComparison.OrdinalIgnoreCase))
                    {
                        QueuedUsers.RemoveAt(i);
                    }
                }
                RenumberQueue();
                QueuedCount = QueuedUsers.Count;
            }

            // Resolve a real display name synchronously (cache → computer-name → short
            // session-id label) so the chip is never blank and never "Unknown user".
            // ResolveAndPatchUsername kicks off the async sessions-table lookup that
            // upgrades the chip in place once the real name resolves.
            var resolved = ResolveDisplaySync(sessionId, username, computerName);
            SyncingUsers.Add(new SyncingUserDisplay
            {
                SessionId = sessionId,
                Username = resolved,
                RevitUsername = !string.IsNullOrWhiteSpace(revitUsername) ? revitUsername : null,
                ComputerName = computerName ?? "",
                SyncStartedAt = DateTime.UtcNow
            });
            SyncingNowCount = SyncingUsers.Count;
            _logger?.LogDebug($"[SyncQueue] Added syncing user: {revitUsername ?? username} (session: {sessionId})");
        }

        private SyncingUserDisplay? RemoveSyncingUser(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return null;

            SyncingUserDisplay? removed = null;
            for (int i = SyncingUsers.Count - 1; i >= 0; i--)
            {
                if (SyncingUsers[i].SessionId == sessionId)
                {
                    removed = SyncingUsers[i];
                    SyncingUsers.RemoveAt(i);
                    break;
                }
            }
            SyncingNowCount = SyncingUsers.Count;
            return removed;
        }

        /// <summary>
        /// The wall-clock time to DISPLAY for an event's Started/Completed column. Uses the
        /// originator's sender-stamped UTC timestamp (converted to local time) so every
        /// machine shows the SAME time for the same sync — the history grid was previously
        /// stamping each receiver's own clock, which made the times (and Duration) differ
        /// per machine. Falls back to local now for legacy un-stamped events (MinValue).
        /// </summary>
        private static DateTime DisplayTime(DateTime eventTimestampUtc)
            => (eventTimestampUtc != default && eventTimestampUtc != DateTime.MinValue)
                ? eventTimestampUtc.ToLocalTime()
                : DateTime.Now;

        private void AddSyncActivity(string sessionId, string username, string computerName, string status,
                                     DateTime eventTimestampUtc = default)
        {
            // Queue rule: only ONE sync per dialog can be in "Syncing" state at a time.
            // If a Syncing row already exists for a DIFFERENT user, the incoming sync
            // is queued behind it and is promoted to Syncing by CompleteSyncActivity
            // when its turn comes. CRITICAL: if the active syncer is the SAME user as
            // the incoming event (late echo of own SyncStarting, or a duplicate), do
            // NOT demote — that would wrongly flip the user's own Syncing row to
            // InQueue. The visible bug was Giri appearing as "Syncing" in the panel
            // but "In Queue" in the history table on the same event.
            var effectiveStatus = status;
            var effectiveDisplay = status == "Syncing" ? "Syncing..."
                                 : status == "InQueue" ? "In Queue"
                                 : status;
            var incomingNameKeyForDemote = NormalizeUsername(username);
            if (status == "Syncing" && IsActiveSyncingByDifferentUser(incomingNameKeyForDemote))
            {
                effectiveStatus = "InQueue";
                effectiveDisplay = "In Queue";
            }

            // ONE ROW PER USER (per the user's explicit spec). When the same human
            // syncs again — even hours later, even with a different sessionId — we
            // UPDATE their existing row in place rather than inserting a new one.
            // This is what kills the "rd.2K9VAR shown 5 times in 90s" pattern: every
            // subsequent sync rewrites the same row with the latest start time and
            // duration. The dialog's history table becomes a "who synced last and
            // when" view, NOT a per-event log.
            var userNameKey = NormalizeUsername(username);
            if (!string.IsNullOrEmpty(userNameKey))
            {
                for (int i = 0; i < SyncHistory.Count; i++)
                {
                    var existing = SyncHistory[i];
                    if (!string.Equals(NormalizeUsername(existing.Username), userNameKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Don't downgrade an active "Syncing" row to "InQueue" via a stale or
                    // late SyncQueueUpdate (or a piggyback JoinQueue echo that arrives
                    // after the user already promoted to active). The active syncer's
                    // status is authoritative until their own SyncCompleted/Cancelled
                    // fires. Without this guard, a late InQueue event flips the row to
                    // "In Queue" while the chip in "Currently Syncing" still points at
                    // them — exactly the visible bug.
                    if (effectiveStatus == "InQueue" && existing.Status == "Syncing")
                    {
                        _logger?.LogDebug($"[SyncQueue] Skipping InQueue update for actively-Syncing user '{username}' (session {existing.SessionId})");
                        return;
                    }

                    // Server-stamped timestamp guard (Phase 7 Fix B).
                    // If the incoming event carries a server timestamp AND the row already
                    // has a more recent server timestamp, the incoming event is out-of-order
                    // and must be ignored regardless of status — covers cases the
                    // CompletedGuardSeconds TTL guard below can't (e.g. echo arrives just
                    // after the 10 s window, late InQueue for a row that already advanced
                    // to Syncing, etc.). MinValue means "no info" — accept; backward-compat
                    // with older server builds that don't stamp.
                    if (eventTimestampUtc != default
                        && eventTimestampUtc != DateTime.MinValue
                        && existing.LastEventAtUtc != DateTime.MinValue
                        && eventTimestampUtc < existing.LastEventAtUtc)
                    {
                        _logger?.LogDebug($"[SyncQueue] Ignoring stale event for '{username}' ts={eventTimestampUtc:HH:mm:ss.fff} < row ts={existing.LastEventAtUtc:HH:mm:ss.fff} (status={effectiveStatus})");
                        return;
                    }

                    // Don't downgrade an already-Completed row back to Syncing or InQueue
                    // via a stale/echoed event. Out-of-order SignalR delivery (server
                    // fan-out delay, reconnect replay, dup-suppression-window expiry,
                    // or piggyback echo) can deliver a SyncStarting AFTER the matching
                    // SyncCompleted — the visible bug where the row briefly shows
                    // "Completed" then flips back to "Sync In Progress."
                    //
                    // A Completed row is TERMINAL for that sync episode. A later
                    // Syncing/InQueue event is only legitimate when it's a genuinely NEW
                    // sync — which carries a strictly newer (sender-stamped) timestamp. A
                    // stale/echoed SyncStarting carries the original start time, which always
                    // precedes the completion, so it must never flip the row back to
                    // "Sync In Progress" (the status-revert bug).
                    //
                    // Now that broadcasts carry the originator's OccurredAtUtcTicks
                    // (SyncTrafficControlService.Broadcast*), the timestamp comparison is the
                    // primary guard. The 10 s TTL is kept ONLY as a fallback for legacy
                    // un-stamped events (eventTimestampUtc == MinValue) from older clients.
                    const double CompletedGuardSeconds = 10.0;
                    if (existing.Status == "Completed"
                        && (effectiveStatus == "Syncing" || effectiveStatus == "InQueue"))
                    {
                        bool stamped = eventTimestampUtc != default && eventTimestampUtc != DateTime.MinValue;
                        bool incomingIsNewer = stamped
                            && (existing.LastEventAtUtc == DateTime.MinValue || eventTimestampUtc > existing.LastEventAtUtc);

                        bool staleEcho;
                        if (incomingIsNewer)
                            staleEcho = false;                 // genuine re-sync — allow overwrite
                        else if (stamped)
                            staleEcho = true;                  // stamped but not newer → definitively out of order
                        else                                    // legacy un-stamped → fall back to TTL
                            staleEcho = existing.CompletedAt.HasValue
                                && (DateTime.Now - existing.CompletedAt.Value).TotalSeconds < CompletedGuardSeconds;

                        if (staleEcho)
                        {
                            _logger?.LogDebug($"[SyncQueue] Skipping {effectiveStatus} update for Completed user '{username}' " +
                                $"(stamped={stamped}, eventTs={eventTimestampUtc:HH:mm:ss.fff}, rowTs={existing.LastEventAtUtc:HH:mm:ss.fff}) — stale/out-of-order, Completed is terminal");
                            return;
                        }
                    }

                    // Same human. Update the existing row in place with the new sync's
                    // data. SessionId is rewritten too so subsequent SyncCompleted events
                    // for the new session can still find this row via SessionId match.
                    existing.SessionId = sessionId ?? existing.SessionId;
                    existing.Username = username ?? existing.Username;
                    if (!string.IsNullOrEmpty(computerName))
                        existing.ComputerName = computerName;
                    existing.StartedAt = DisplayTime(eventTimestampUtc);
                    existing.CompletedAt = null;
                    existing.Status = effectiveStatus;
                    existing.StatusDisplay = effectiveDisplay;
                    existing.Duration = "";
                    if (eventTimestampUtc != default && eventTimestampUtc != DateTime.MinValue)
                        existing.LastEventAtUtc = eventTimestampUtc;

                    // Keep panels consistent for the new state of this user. CRITICAL:
                    // when flipping to InQueue we MUST also remove from SyncingUsers,
                    // otherwise the chip lingers in "Currently Syncing" while the grid
                    // row says "In Queue" — the visible bug where Zestine appeared in
                    // the syncing-chip panel but the grid row said "In Queue" after a
                    // collision-detector SelfCollision event arrived.
                    if (effectiveStatus == "Syncing")
                    {
                        RemoveQueuedUser(existing.SessionId);
                    }
                    else if (effectiveStatus == "InQueue")
                    {
                        RemoveSyncingUser(existing.SessionId);
                        AddQueuedUser(existing.SessionId, username ?? existing.Username, computerName ?? "");
                    }
                    _logger?.LogDebug($"[SyncQueue] Updated existing row in place for user '{username}' (one-row-per-user policy) → status={effectiveStatus}");
                    return;
                }
            }

            if (!string.IsNullOrEmpty(sessionId))
            {
                // Gate on effectiveStatus, not the raw status param. When the incoming
                // event was demoted to InQueue (because another user is already syncing),
                // we MUST NOT enter the displacement branch — doing so wrongly closes the
                // active syncer's row to Completed even though we just decided to queue
                // behind them, leaving the chip showing them as Syncing while the history
                // table shows them as Completed/InQueue. Only enter when we're actually
                // inserting a Syncing row.
                if (effectiveStatus == "Syncing")
                {
                    // Two scenarios reach here with effectiveStatus=="Syncing" and an EXISTING open row
                    // for the same sessionId:
                    //   (a) DUPLICATE: SignalR delivered the same SyncStarting twice (echo +
                    //       rebroadcast, or two bound sync commandIds firing for one user
                    //       click). The existing row was added <~3s ago. Dedup → skip.
                    //   (b) GENUINE re-sync: previous sync's SyncCompleted was missed, and a
                    //       new sync is starting later (existing row is older than 3s). Close
                    //       the stale row, then insert the new one as a distinct entry.
                    // The 10-second cutoff absorbs SignalR reconnect replays, slow echo
                    // round-trips, and the case where a sender's broadcast is fanned out to
                    // the same client twice (e.g. multiple bound sync command-ids firing for
                    // one user click). A real Revit sync takes >10s for any non-trivial model,
                    // so legitimate re-syncs by the same user are still treated as distinct.
                    var now = DateTime.Now;
                    bool foundRecentDuplicate = false;
                    bool upgradedInQueueToSyncing = false;

                    // Cross-session displacement: a model can only have ONE active syncer at a
                    // time. When a Syncing event arrives for a NEW session, any existing Syncing
                    // row for a DIFFERENT session in the same dialog (already filtered to the
                    // current model by _onSyncStarting) must be stale — its SyncCompleted event
                    // was missed. Close it so the UI doesn't show ghost-syncers forever.
                    //
                    // ALSO close any "Syncing" row whose Username matches the incoming user but
                    // whose session differs (same human, two sessionIds across Revit restarts).
                    // This fixes the bug where priya's old stale Syncing row sits alongside her
                    // current InQueue row in the same dialog.
                    var incomingNameKey = NormalizeUsername(username);
                    for (int j = SyncHistory.Count - 1; j >= 0; j--)
                    {
                        var other = SyncHistory[j];
                        if (other.SessionId == sessionId) continue;
                        if (other.Status != "Syncing") continue;
                        other.Status = "Completed";
                        other.CompletedAt = now;
                        other.StatusDisplay = "Completed";
                        other.Duration = FormatDuration(other.StartedAt, other.CompletedAt.Value);
                        RemoveSyncingUser(other.SessionId);
                        _logger?.LogDebug($"[SyncQueue] Cross-session displacement: closed stale Syncing for session {other.SessionId} because session {sessionId} started syncing");
                    }
                    // Also close any Syncing row for the same Username (different session).
                    if (!string.IsNullOrEmpty(incomingNameKey))
                    {
                        for (int j = SyncHistory.Count - 1; j >= 0; j--)
                        {
                            var other = SyncHistory[j];
                            if (other.SessionId == sessionId) continue;
                            if (other.Status != "Syncing") continue;
                            if (!string.Equals(NormalizeUsername(other.Username), incomingNameKey, StringComparison.OrdinalIgnoreCase))
                                continue;
                            other.Status = "Completed";
                            other.CompletedAt = now;
                            other.StatusDisplay = "Completed";
                            other.Duration = FormatDuration(other.StartedAt, other.CompletedAt.Value);
                            RemoveSyncingUser(other.SessionId);
                            _logger?.LogDebug($"[SyncQueue] Username-based displacement: closed stale Syncing for {other.Username} (session {other.SessionId}) because the same user started a new sync (session {sessionId})");
                        }
                    }

                    for (int i = SyncHistory.Count - 1; i >= 0; i--)
                    {
                        var item = SyncHistory[i];
                        if (item.SessionId != sessionId) continue;

                        // Existing InQueue row for this session → upgrade to Syncing in place.
                        // This produces a smooth "In Queue → Syncing → Completed" lifecycle
                        // on ONE row instead of leaving a stale InQueue + adding a new Syncing.
                        if (item.Status == "InQueue")
                        {
                            item.Status = "Syncing";
                            item.StatusDisplay = "Syncing...";
                            item.StartedAt = now; // restart timer at the actual sync-start moment
                            RemoveQueuedUser(item.SessionId);
                            upgradedInQueueToSyncing = true;
                            continue;
                        }

                        // Existing Syncing row for the same session.
                        if (item.Status == "Syncing")
                        {
                            // Widened from 10s to 60s. Field reports showed a single Revit
                            // sync producing 4 rows at 15s intervals (echo + multi-binding
                            // fan-out + server replays). 60 s collapses these into one row;
                            // a legitimate re-sync 60+ s later still lands as a distinct row.
                            if ((now - item.StartedAt).TotalSeconds < 60)
                            {
                                foundRecentDuplicate = true;
                                continue;
                            }
                            // Old Syncing row whose Completed event was missed → close it.
                            item.Status = "Completed";
                            item.CompletedAt = now;
                            item.StatusDisplay = "Completed";
                            item.Duration = FormatDuration(item.StartedAt, item.CompletedAt.Value);
                            RemoveQueuedUser(item.SessionId);
                        }
                    }
                    if (upgradedInQueueToSyncing)
                    {
                        // Row already exists at Syncing — don't insert a new one.
                        _logger?.LogDebug($"[SyncQueue] Upgraded InQueue → Syncing in place for session {sessionId}");
                        return;
                    }
                    if (foundRecentDuplicate)
                    {
                        _logger?.LogDebug($"[SyncQueue] Dedup'd duplicate SyncStarting for session {sessionId} (within 10s window)");
                        return;
                    }
                }
                else
                {
                    // For "InQueue" arrivals, keep the original dedup: avoid duplicate
                    // queue rows when SyncQueueUpdate is rebroadcast or arrives after a
                    // pre-populated row. Completed/Cancelled rows do NOT block.
                    foreach (var existing in SyncHistory)
                    {
                        if (existing.SessionId == sessionId
                            && (existing.Status == "Syncing" || existing.Status == "InQueue"))
                            return;
                    }
                }
            }

            var resolvedName = ResolveDisplaySync(sessionId, username, computerName);
            SyncHistory.Insert(0, new SyncActivityItem
            {
                RowNumber = SyncHistory.Count + 1,
                SessionId = sessionId,
                Username = resolvedName,
                ComputerName = computerName ?? "",
                StartedAt = DisplayTime(eventTimestampUtc),
                Status = effectiveStatus,
                StatusDisplay = effectiveDisplay,
                LastEventAtUtc = (eventTimestampUtc != default && eventTimestampUtc != DateTime.MinValue)
                    ? eventTimestampUtc
                    : DateTime.MinValue
            });
            RenumberHistory();

            // Mirror InQueue rows into the queue-order panel.
            if (effectiveStatus == "InQueue")
            {
                AddQueuedUser(sessionId, resolvedName, computerName ?? "");
            }
        }

        private void AddQueuedUser(string sessionId, string displayName, string computerName)
        {
            if (string.IsNullOrEmpty(sessionId)) return;

            // Don't add a session that's already shown as currently syncing — a session
            // is either syncing OR queued, never both. Check by sessionId first, then by
            // Username (case-insensitive) — covers the case where the same human has two
            // different sessionIds (e.g. stale session from a previous Revit run + new
            // live session). SessionId-only dedup misses this scenario.
            var nameKey = NormalizeUsername(displayName);
            foreach (var s in SyncingUsers)
            {
                if (s.SessionId == sessionId) return;
                if (!string.IsNullOrEmpty(nameKey)
                    && string.Equals(NormalizeUsername(s.Username), nameKey, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            foreach (var existing in QueuedUsers)
            {
                if (existing.SessionId == sessionId) return;
                if (!string.IsNullOrEmpty(nameKey)
                    && string.Equals(NormalizeUsername(existing.DisplayName), nameKey, StringComparison.OrdinalIgnoreCase))
                    return; // same human, different session — don't double-queue
            }
            QueuedUsers.Add(new QueuedUserDisplay
            {
                SessionId = sessionId,
                DisplayName = displayName,
                ComputerName = computerName,
                QueuedAt = DateTime.UtcNow,
                Position = QueuedUsers.Count + 1
            });
            QueuedCount = QueuedUsers.Count;
        }

        private void RemoveQueuedUser(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            for (int i = QueuedUsers.Count - 1; i >= 0; i--)
            {
                if (QueuedUsers[i].SessionId == sessionId)
                {
                    QueuedUsers.RemoveAt(i);
                    break;
                }
            }
            RenumberQueue();
            QueuedCount = QueuedUsers.Count;
        }

        private void RenumberQueue()
        {
            for (int i = 0; i < QueuedUsers.Count; i++)
                QueuedUsers[i].Position = i + 1;
        }

        private bool HasActiveSyncing()
        {
            foreach (var item in SyncHistory)
            {
                if (item.Status == "Syncing") return true;
            }
            return false;
        }

        /// <summary>
        /// True iff at least one row is "Syncing" AND none of those Syncing rows belong
        /// to the user identified by <paramref name="incomingNameKey"/>. Used by
        /// AddSyncActivity to decide whether a new "Syncing" event should be demoted to
        /// InQueue (someone else is syncing) or left as Syncing (the user's own row is
        /// already active; a late echo must not flip it to InQueue).
        /// </summary>
        private bool IsActiveSyncingByDifferentUser(string incomingNameKey)
        {
            bool anySyncing = false;
            bool sameUserSyncing = false;
            foreach (var item in SyncHistory)
            {
                if (item.Status != "Syncing") continue;
                anySyncing = true;
                if (!string.IsNullOrEmpty(incomingNameKey)
                    && string.Equals(NormalizeUsername(item.Username), incomingNameKey, StringComparison.OrdinalIgnoreCase))
                {
                    sameUserSyncing = true;
                }
            }
            return anySyncing && !sameUserSyncing;
        }

        /// <summary>
        /// Finds the oldest InQueue entry and promotes it to Syncing.
        /// Called after a Syncing entry completes/cancels so the next queued user takes over.
        /// </summary>
        private void PromoteNextQueued()
        {
            SyncActivityItem? oldest = null;
            foreach (var item in SyncHistory)
            {
                if (item.Status != "InQueue") continue;
                if (oldest == null || item.StartedAt < oldest.StartedAt)
                    oldest = item;
            }
            if (oldest != null)
            {
                oldest.Status = "Syncing";
                oldest.StatusDisplay = "Syncing...";
                oldest.StartedAt = DateTime.Now; // restart timer so Duration reflects the active wait→sync transition
                // The promoted user is no longer waiting — remove them from the queue-order panel.
                RemoveQueuedUser(oldest.SessionId);

                // Also mirror the promotion into the "Currently Syncing" chip panel.
                // When AddSyncingUser's multi-slot guard routed this user into QueuedUsers
                // instead of SyncingUsers (because another user was already active at the
                // time), there's no chip for them yet. Without adding one here, the chip
                // panel goes empty between the prior user's completion and this user's
                // own SyncCompleted, even though the history row correctly shows them as
                // "Syncing".
                bool alreadyShownAsSyncing = false;
                foreach (var s in SyncingUsers)
                {
                    if (string.Equals(s.SessionId, oldest.SessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyShownAsSyncing = true;
                        break;
                    }
                }
                if (!alreadyShownAsSyncing)
                {
                    SyncingUsers.Add(new SyncingUserDisplay
                    {
                        SessionId = oldest.SessionId,
                        Username = oldest.Username,
                        ComputerName = oldest.ComputerName ?? "",
                        SyncStartedAt = oldest.StartedAt
                    });
                    SyncingNowCount = SyncingUsers.Count;
                }
            }
            // After mutating the grid, reconcile both panels so SyncingUsers picks up the
            // promoted row and any stale chips drop out.
            ReconcilePanelsToGrid();
        }

        /// <summary>
        /// Forces both panels (SyncingUsers chip panel, QueuedUsers position panel) to
        /// match the authoritative state in <see cref="SyncHistory"/> (the grid).
        ///
        /// Why this exists: the chip panel was getting out of sync with the grid in the
        /// real multi-user scenario — a missed peer SyncCompleted leaves a stale chip in
        /// SyncingUsers, the multi-slot guard then routes the actual new syncer into
        /// QueuedUsers, and the user sees the wrong person (or nobody) as "Currently
        /// Syncing" while the grid below shows the truth. This method makes the grid the
        /// single source of truth: every row in SyncHistory with Status=="Syncing" gets
        /// a SyncingUsers chip; every Status=="InQueue" gets a QueuedUsers entry; any
        /// panel entry whose SessionId is not in the grid gets removed.
        ///
        /// Called after every grid mutation in the event handlers so drift can't accumulate.
        /// </summary>
        private void ReconcilePanelsToGrid()
        {
            var gridSyncingBySession = new Dictionary<string, SyncActivityItem>(StringComparer.OrdinalIgnoreCase);
            var gridQueuedBySession = new Dictionary<string, SyncActivityItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in SyncHistory)
            {
                if (string.IsNullOrEmpty(item.SessionId)) continue;
                if (item.Status == "Syncing") gridSyncingBySession[item.SessionId] = item;
                else if (item.Status == "InQueue") gridQueuedBySession[item.SessionId] = item;
            }

            // Drop chips that no longer correspond to a Syncing grid row.
            for (int i = SyncingUsers.Count - 1; i >= 0; i--)
            {
                var s = SyncingUsers[i];
                if (string.IsNullOrEmpty(s.SessionId) || !gridSyncingBySession.ContainsKey(s.SessionId))
                    SyncingUsers.RemoveAt(i);
            }
            // Drop queue entries that no longer correspond to an InQueue grid row.
            for (int i = QueuedUsers.Count - 1; i >= 0; i--)
            {
                var q = QueuedUsers[i];
                if (string.IsNullOrEmpty(q.SessionId) || !gridQueuedBySession.ContainsKey(q.SessionId))
                    QueuedUsers.RemoveAt(i);
            }

            // Add chips for any Syncing grid row that doesn't have one yet.
            foreach (var kvp in gridSyncingBySession)
            {
                bool found = false;
                foreach (var s in SyncingUsers)
                {
                    if (string.Equals(s.SessionId, kvp.Key, StringComparison.OrdinalIgnoreCase))
                    { found = true; break; }
                }
                if (!found)
                {
                    var item = kvp.Value;
                    SyncingUsers.Add(new SyncingUserDisplay
                    {
                        SessionId = item.SessionId,
                        Username = item.Username,
                        RevitUsername = item.Username,
                        ComputerName = item.ComputerName ?? "",
                        SyncStartedAt = item.StartedAt.ToUniversalTime()
                    });
                }
            }
            // Add queue entries for any InQueue grid row that doesn't have one yet.
            foreach (var kvp in gridQueuedBySession)
            {
                bool found = false;
                foreach (var q in QueuedUsers)
                {
                    if (string.Equals(q.SessionId, kvp.Key, StringComparison.OrdinalIgnoreCase))
                    { found = true; break; }
                }
                if (!found)
                {
                    var item = kvp.Value;
                    AddQueuedUser(item.SessionId, item.Username, item.ComputerName ?? "");
                }
            }

            RenumberQueue();
            SyncingNowCount = SyncingUsers.Count;
            QueuedCount = QueuedUsers.Count;
        }

        private void CompleteSyncActivity(string sessionId, SyncingUserDisplay? user, string? fallbackDisplayName = null, string? fallbackComputerName = null,
                                          DateTime eventTimestampUtc = default)
        {
            var displayName = user?.DisplayName ?? fallbackDisplayName;
            var computerName = user?.ComputerName ?? fallbackComputerName ?? "";
            var nameKey = NormalizeUsername(displayName);

            // First pass: match by SessionId AND status. The expected happy path: the
            // Syncing/InQueue row was inserted earlier by the corresponding SyncStarting,
            // and the SyncCompleted closes that exact row.
            foreach (var item in SyncHistory)
            {
                if (item.SessionId == sessionId && (item.Status == "Syncing" || item.Status == "InQueue"))
                {
                    bool wasActive = item.Status == "Syncing";
                    item.Status = "Completed";
                    item.CompletedAt = DisplayTime(eventTimestampUtc);
                    item.StatusDisplay = "Completed";
                    item.Duration = FormatDuration(item.StartedAt, item.CompletedAt.Value);
                    if (!string.IsNullOrEmpty(displayName))
                    {
                        item.Username = displayName;
                        item.ComputerName = computerName;
                    }
                    if (eventTimestampUtc != default && eventTimestampUtc != DateTime.MinValue)
                        item.LastEventAtUtc = eventTimestampUtc;
                    if (!wasActive) RemoveQueuedUser(sessionId);
                    if (wasActive) PromoteNextQueued();
                    return;
                }
            }

            // ONE ROW PER USER fallback: SessionId didn't match (e.g. the user restarted
            // Revit between sync start and complete, or the row's sessionId got rewritten
            // by AddSyncActivity's update-in-place path). Find the user's row by Username
            // and close it there instead of inserting a new Completed row. Keeps one row
            // per user even across session changes.
            if (!string.IsNullOrEmpty(nameKey))
            {
                foreach (var item in SyncHistory)
                {
                    if (!string.Equals(NormalizeUsername(item.Username), nameKey, StringComparison.OrdinalIgnoreCase))
                        continue;
                    item.SessionId = sessionId ?? item.SessionId;
                    item.Status = "Completed";
                    item.CompletedAt = DisplayTime(eventTimestampUtc);
                    item.StatusDisplay = "Completed";
                    if (item.StartedAt > item.CompletedAt.Value) item.StartedAt = item.CompletedAt.Value;
                    item.Duration = FormatDuration(item.StartedAt, item.CompletedAt.Value);
                    if (!string.IsNullOrEmpty(displayName)) item.Username = displayName;
                    if (!string.IsNullOrEmpty(computerName)) item.ComputerName = computerName;
                    if (eventTimestampUtc != default && eventTimestampUtc != DateTime.MinValue)
                        item.LastEventAtUtc = eventTimestampUtc;
                    RemoveQueuedUser(item.SessionId);
                    PromoteNextQueued();
                    _logger?.LogDebug($"[SyncQueue] CompleteSyncActivity username-fallback: closed '{displayName}' row that lost SessionId match");
                    return;
                }
            }

            // No row found by SessionId or Username — insert a fresh Completed record.
            SyncHistory.Insert(0, new SyncActivityItem
            {
                RowNumber = 1,
                SessionId = sessionId,
                Username = ResolveDisplaySync(sessionId, displayName, computerName),
                ComputerName = computerName,
                StartedAt = DisplayTime(eventTimestampUtc),
                CompletedAt = DisplayTime(eventTimestampUtc),
                Status = "Completed",
                StatusDisplay = "Completed",
                Duration = "-"
            });
            RenumberHistory();
        }

        private void CancelSyncActivity(string sessionId, SyncingUserDisplay? user, string? fallbackDisplayName = null, string? fallbackComputerName = null,
                                        DateTime eventTimestampUtc = default)
        {
            var displayName = user?.DisplayName ?? fallbackDisplayName;
            var computerName = user?.ComputerName ?? fallbackComputerName ?? "";

            foreach (var item in SyncHistory)
            {
                if (item.SessionId == sessionId && (item.Status == "Syncing" || item.Status == "InQueue"))
                {
                    bool wasActive = item.Status == "Syncing";
                    item.Status = "Cancelled";
                    item.CompletedAt = DisplayTime(eventTimestampUtc);
                    item.StatusDisplay = "Cancelled";
                    item.Duration = FormatDuration(item.StartedAt, item.CompletedAt.Value);
                    if (!string.IsNullOrEmpty(displayName))
                    {
                        item.Username = displayName;
                        item.ComputerName = computerName;
                    }
                    if (eventTimestampUtc != default && eventTimestampUtc != DateTime.MinValue)
                        item.LastEventAtUtc = eventTimestampUtc;
                    // Cancelled-while-queued → drop from queue panel; cancelled-while-active → promote next.
                    if (!wasActive) RemoveQueuedUser(sessionId);
                    if (wasActive) PromoteNextQueued();
                    return;
                }
            }
        }

        private void RenumberHistory()
        {
            for (int i = 0; i < SyncHistory.Count; i++)
                SyncHistory[i].RowNumber = i + 1;
        }

        private void UpdateCounts()
        {
            SyncingNowCount = SyncingUsers.Count;
            int completed = 0;
            foreach (var item in SyncHistory)
            {
                if (item.Status == "Completed")
                    completed++;
            }
            CompletedCount = completed;
            TotalCount = SyncHistory.Count;

            // Refresh the footer status so it always matches the current chip counts.
            // Event handlers may overwrite StatusMessage with a one-shot custom string
            // ("Zestine started syncing") AFTER UpdateCounts returns — that's fine;
            // this default kicks in between events and during idle so the footer never
            // shows a stale "3 users are syncing" hours after sync activity ended.
            int syncing = SyncingUsers.Count;
            int queued = QueuedUsers.Count;
            if (syncing == 0 && queued == 0)
                StatusMessage = "No active syncs";
            else if (syncing == 0 && queued > 0)
                StatusMessage = queued == 1 ? "1 user waiting in queue" : $"{queued} users waiting in queue";
            else if (queued == 0)
                StatusMessage = syncing == 1 ? "1 user is syncing" : $"{syncing} users are syncing";
            else
                StatusMessage = $"{syncing} syncing, {queued} in queue";
        }

        private static string FormatDuration(DateTime start, DateTime end)
        {
            var ts = end - start;
            if (ts.TotalSeconds < 60)
                return $"{Math.Max(1, (int)ts.TotalSeconds)}s";
            if (ts.TotalMinutes < 60)
                return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        }

        /// <summary>
        /// Load the last 15 syncs for the current model from SQLite and populate SyncHistory.
        /// Called every time the dialog opens; live SignalR events are prepended on top.
        ///
        /// Re-entrancy / reopen safety:
        ///  * Existing rows added by an earlier <c>LoadActiveRemoteSyncers</c> pass are kept
        ///    only when their SessionId matches a fresh DB record — that protects truly
        ///    in-flight syncs from being temporarily blanked while we re-fetch. Everything
        ///    else (especially "Completed" rows from a previous open of this VM) is wiped
        ///    so the reload is authoritative: whatever is in model_sync NOW is what shows.
        ///  * After the records are appended, SyncHistory is re-sorted by StartedAt DESC
        ///    so live in-flight rows (inserted at the top with NOW timestamps) can't visually
        ///    push older completed rows above a newer completion.
        /// </summary>
        public async Task LoadHistoryAsync()
        {
            if (_syncRepository == null || string.IsNullOrEmpty(_currentModelGuid))
                return;

            try
            {
                var records = await _syncRepository.GetModelSyncHistoryAsync(_currentModelGuid, 15);

                RunOnUiThread(() =>
                {
                    // Drop any non-live rows so the next paint reflects the DB authoritatively.
                    // Live "Syncing" AND "InQueue" rows added by LoadActiveRemoteSyncers stay
                    // — they represent remote syncs/queue entries we know are in flight via
                    // SignalR / in-memory snapshot, and we don't want them to flicker out
                    // while the DB re-read runs. Without preserving InQueue here, the queue
                    // rehydration done in LoadActiveRemoteSyncers (which populates the
                    // "Waiting in Queue" panel from _modelQueues) would wipe the matching
                    // InQueue history rows, leaving the panel populated but the table empty.
                    //
                    // AGE GATE: a "Syncing"/"InQueue" row older than 5 minutes is almost
                    // certainly stale (peer's SyncCompleted was lost, app was restarted
                    // between, etc.). Without this gate, when the dialog is reopened 30 min
                    // after the user closed it during a peer's long sync, a ghost
                    // "Syncing" chip would persist forever (until the next live event).
                    var stalenessCutoff = DateTime.Now.AddMinutes(-5);
                    for (int i = SyncHistory.Count - 1; i >= 0; i--)
                    {
                        var row = SyncHistory[i];
                        var s = row.Status;
                        if (s != "Syncing" && s != "InQueue")
                        {
                            SyncHistory.RemoveAt(i);
                            continue;
                        }
                        // Live-status row, but it's been "live" for longer than a real
                        // sync usually takes — close it as Completed so it gets re-loaded
                        // from DB (or dropped if no DB row backs it).
                        if (row.StartedAt < stalenessCutoff)
                        {
                            _logger?.LogDebug($"[SyncQueue] LoadHistoryAsync: closing stale {s} row (user={row.Username}, started={row.StartedAt:HH:mm:ss}) — older than 5 min");
                            SyncHistory.RemoveAt(i);
                        }
                    }

                    // ONE ROW PER USER on DB reload: walk records newest-first and keep
                    // only the most recent sync per Username. The DB may have hundreds of
                    // rows from many sync events per user; the user explicitly wants one
                    // row per user in the UI.
                    var seenUsernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var orderedRecords = records.OrderByDescending(r => r.SyncStartedAt).ToList();

                    foreach (var sync in orderedRecords)
                    {
                        // Skip if this sync is already in the list as a live event (matched by SyncGuid)
                        bool alreadyPresent = false;
                        foreach (var existing in SyncHistory)
                        {
                            if (existing.SessionId == sync.SyncGuid)
                            { alreadyPresent = true; break; }
                        }
                        if (alreadyPresent) continue;

                        var rowName = ResolveDisplaySync(sync.SessionId, sync.SyncedBy, computerName: null);

                        // Don't render historical rows whose name resolved to a synthetic
                        // placeholder ("—" or "User-XXXXXXXX") — they surface as ugly blank
                        // rows in the grid. The next time this user actually syncs, their
                        // real name comes through the SignalR payload and a fresh row
                        // appears properly.
                        if (IsSyntheticDisplayName(rowName))
                        {
                            _logger?.LogDebug($"[SyncQueue] Skipping history row for sync {sync.SyncGuid} — name unresolved (would render as placeholder)");
                            continue;
                        }

                        var nameKey = NormalizeUsername(rowName);

                        // One-row-per-user dedup: if we've already loaded the most recent
                        // sync for this Username, skip older rows.
                        if (!string.IsNullOrEmpty(nameKey) && !seenUsernames.Add(nameKey))
                            continue;

                        // Also skip if any live-event row in SyncHistory already represents
                        // this human (different SessionId, same Username).
                        bool liveRowForUser = false;
                        if (!string.IsNullOrEmpty(nameKey))
                        {
                            foreach (var existing in SyncHistory)
                            {
                                if (string.Equals(NormalizeUsername(existing.Username), nameKey, StringComparison.OrdinalIgnoreCase))
                                { liveRowForUser = true; break; }
                            }
                        }
                        if (liveRowForUser) continue;

                        var status = !sync.SyncEndedAt.HasValue ? "Syncing"
                            : sync.IsSucceeded ? "Completed"
                            : "Failed";

                        string duration = "-";
                        if (sync.SyncDurationSeconds.HasValue)
                            duration = FormatDurationSeconds(sync.SyncDurationSeconds.Value);
                        else if (sync.SyncEndedAt.HasValue)
                            duration = FormatDuration(sync.SyncStartedAt.ToLocalTime(), sync.SyncEndedAt.Value.ToLocalTime());

                        SyncHistory.Add(new SyncActivityItem
                        {
                            RowNumber = SyncHistory.Count + 1,
                            SessionId = sync.SyncGuid,
                            Username = rowName,
                            ComputerName = Environment.MachineName,
                            StartedAt = sync.SyncStartedAt.ToLocalTime(),
                            CompletedAt = sync.SyncEndedAt?.ToLocalTime(),
                            Status = status,
                            StatusDisplay = status,
                            Duration = duration
                        });
                        // Fire the async sessions-table lookup so legacy rows upgrade to the
                        // real Revit username once it resolves.
                        ResolveAndPatchUsernameForLoadedRow(sync.SyncGuid, sync.SessionId, sync.SyncedBy);
                    }

                    // Defensive re-sort: live "Syncing" rows were inserted at position 0
                    // with NOW timestamps regardless of when the sync actually started, so
                    // a stale active row (whose SyncCompleted SignalR event was missed) could
                    // otherwise sit at the top above truly-newest completions. Sorting by
                    // StartedAt DESC guarantees newest-first regardless of insertion order.
                    SortHistoryByStartedAtDesc();

                    RenumberHistory();
                    // LoadHistoryAsync can add Syncing/InQueue rows from DB that the chip
                    // and queue panels don't know about yet — reconcile so the panels
                    // mirror the grid. Without this, the dialog can show a "Syncing" row
                    // in the grid with no matching chip in "Currently Syncing".
                    ReconcilePanelsToGrid();
                    UpdateCounts();
                });

                _logger?.LogDebug($"[SyncQueue] Loaded {records.Count} historical sync records for model {_currentModelGuid}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[SyncQueue] Failed to load sync history: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// In-place stable sort of SyncHistory by StartedAt descending (newest first).
        /// Used after LoadHistoryAsync to guarantee the visible order matches the
        /// "most recent at the top" expectation, regardless of how items were added
        /// (Insert(0,...) by SignalR live events vs Add() by historical reload).
        /// </summary>
        private void SortHistoryByStartedAtDesc()
        {
            if (SyncHistory.Count < 2) return;
            var sorted = SyncHistory.OrderByDescending(s => s.StartedAt).ToList();
            for (int target = 0; target < sorted.Count; target++)
            {
                var item = sorted[target];
                var current = SyncHistory.IndexOf(item);
                if (current != target && current >= 0)
                    SyncHistory.Move(current, target);
            }
        }

        private static string FormatDurationSeconds(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalSeconds < 60)
                return $"{Math.Max(1, (int)ts.TotalSeconds)}s";
            if (ts.TotalMinutes < 60)
                return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        }

        /// <summary>
        /// Returns the best display name we have RIGHT NOW for a session — provided name
        /// → in-memory cache → computer name → short session-id label. NEVER "Unknown user".
        /// Use the async <see cref="ResolveAndPatchUsername"/> companion to upgrade rows
        /// once a real name is resolved from the sessions table.
        /// </summary>
        /// <summary>
        /// Canonicalizes a username for cross-list dedup. Trims whitespace and lowercases.
        /// Returns empty for placeholders like "User-...", "Unknown user", or "—" so they
        /// don't accidentally collapse distinct real users together.
        /// </summary>
        private static string NormalizeUsername(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var trimmed = name.Trim();
            if (trimmed.StartsWith("User-", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            if (string.Equals(trimmed, "Unknown user", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            if (trimmed == "—" || trimmed == "-") return string.Empty;
            return trimmed.ToLowerInvariant();
        }

        private static string ResolveDisplaySync(string? sessionId, string? providedName, string? computerName)
        {
            if (!string.IsNullOrWhiteSpace(providedName)) return providedName!;
            var cached = UsernameResolver.TryGetCached(sessionId);
            if (!string.IsNullOrWhiteSpace(cached)) return cached!;
            if (!string.IsNullOrWhiteSpace(computerName)) return computerName!;
            if (!string.IsNullOrWhiteSpace(sessionId))
                return $"User-{sessionId!.Substring(0, Math.Min(8, sessionId.Length))}";
            return "—";
        }

        /// <summary>
        /// True when <see cref="ResolveDisplaySync"/> fell through to its synthetic-label
        /// fallback ("—" or the "User-XXXXXXXX" session-id-prefix label). DB-load and
        /// in-memory-snapshot paths should skip rows whose name resolves to these
        /// placeholders — inserting them surfaces as blank-looking rows in the grid and
        /// useless chips in "Currently Syncing". The matching ResolveAndPatchUsername
        /// call still kicks off the async lookup; the row populates properly once a
        /// real SignalR event arrives or the sessions table catches up.
        /// </summary>
        private static bool IsSyntheticDisplayName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            if (name == "—") return true;
            return name!.StartsWith("User-", StringComparison.Ordinal);
        }

        /// <summary>
        /// Fire-and-forget DB lookup for a session's real Revit username. When it resolves,
        /// rewrites the matching SyncHistory rows' Username in place. Lets the row appear
        /// IMMEDIATELY with a stable placeholder, and silently upgrades to the real name
        /// once the sessions table has it — no "Unknown user", no UI flicker.
        /// </summary>
        private void ResolveAndPatchUsername(string? sessionId, string? providedName, string? computerName)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            if (!string.IsNullOrWhiteSpace(providedName)) return; // already have a real name
            if (UsernameResolver.TryGetCached(sessionId) != null) return; // cache already has it

            _ = Task.Run(async () =>
            {
                try
                {
                    var resolved = await UsernameResolver.ResolveAsync(
                        sessionId, null, null, computerName, _sessionRepository).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(resolved)) return;
                    RunOnUiThread(() =>
                    {
                        foreach (var item in SyncHistory)
                        {
                            if (string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                                && (item.Username.StartsWith("User-", StringComparison.Ordinal)
                                    || string.Equals(item.Username, computerName, StringComparison.OrdinalIgnoreCase)
                                    || item.Username == "—"))
                            {
                                item.Username = resolved!;
                            }
                        }
                        foreach (var u in SyncingUsers)
                        {
                            if (string.Equals(u.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                                && string.IsNullOrWhiteSpace(u.RevitUsername))
                            {
                                u.RevitUsername = resolved;
                            }
                        }
                        foreach (var q in QueuedUsers)
                        {
                            if (string.Equals(q.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                                && (q.DisplayName.StartsWith("User-", StringComparison.Ordinal)
                                    || string.Equals(q.DisplayName, computerName, StringComparison.OrdinalIgnoreCase)
                                    || q.DisplayName == "—"))
                            {
                                q.DisplayName = resolved!;
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"[SyncQueue] ResolveAndPatchUsername failed for {sessionId}: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Specialised companion to <see cref="ResolveAndPatchUsername"/> for rows loaded
        /// from <c>model_sync</c> by <see cref="LoadHistoryAsync"/>. Those rows use the
        /// row's <c>sync_guid</c> as their VM-side SessionId (so each historical sync is
        /// independently de-duplicated), but the cache/sessions table are keyed by the
        /// actual <c>session_id</c>. Resolves via the real sessionId and patches the row
        /// matched by its sync_guid.
        /// </summary>
        private void ResolveAndPatchUsernameForLoadedRow(string syncGuidRowKey, string? realSessionId, string? providedName)
        {
            if (string.IsNullOrWhiteSpace(syncGuidRowKey)) return;
            if (!string.IsNullOrWhiteSpace(providedName)
                && !providedName!.StartsWith("User-", StringComparison.Ordinal))
                return; // already has a real name

            _ = Task.Run(async () =>
            {
                try
                {
                    var resolved = await UsernameResolver.ResolveAsync(
                        realSessionId, null, null, null, _sessionRepository).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(resolved)) return;
                    RunOnUiThread(() =>
                    {
                        foreach (var item in SyncHistory)
                        {
                            if (string.Equals(item.SessionId, syncGuidRowKey, StringComparison.OrdinalIgnoreCase))
                            {
                                if (item.Username.StartsWith("User-", StringComparison.Ordinal)
                                    || string.IsNullOrWhiteSpace(item.Username)
                                    || item.Username == "—")
                                {
                                    item.Username = resolved!;
                                }
                                break;
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"[SyncQueue] ResolveAndPatchUsernameForLoadedRow failed for {realSessionId}: {ex.Message}");
                }
            });
        }

        private void RunOnUiThread(Action action)
        {
            if (_dispatcher.CheckAccess())
                action();
            else
                _dispatcher.Invoke(action);
        }

    }

    public class SyncActivityItem : ObservableObject
    {
        private int _rowNumber;
        private string _status = "";
        private string _statusDisplay = "";
        private string _duration = "";
        private string _username = "";
        private string _computerName = "";
        private DateTime _startedAt;
        private DateTime? _completedAt;

        public int RowNumber
        {
            get => _rowNumber;
            set => SetProperty(ref _rowNumber, value);
        }

        public string SessionId { get; set; } = "";

        public string Username
        {
            get => _username;
            set => SetProperty(ref _username, value);
        }

        public string ComputerName
        {
            get => _computerName;
            set => SetProperty(ref _computerName, value);
        }

        public DateTime StartedAt
        {
            get => _startedAt;
            set
            {
                if (SetProperty(ref _startedAt, value))
                {
                    OnPropertyChanged(nameof(StartedAtDisplay));
                    OnPropertyChanged(nameof(Tooltip));
                }
            }
        }

        public DateTime? CompletedAt
        {
            get => _completedAt;
            set
            {
                if (SetProperty(ref _completedAt, value))
                    OnPropertyChanged(nameof(Tooltip));
            }
        }

        /// <summary>
        /// Server-stamped UTC timestamp of the most recent event that touched this row.
        /// DateTime.MinValue when the server doesn't supply a timestamp (older builds).
        /// Used to reject stale/out-of-order SyncStarting / SyncQueueUpdate echoes that
        /// arrive AFTER a newer SyncCompleted for the same session (Phase 7 Issue 1 —
        /// row briefly shows Completed then reverts to Sync In Progress). Not bindable;
        /// internal bookkeeping only.
        /// </summary>
        public DateTime LastEventAtUtc { get; set; } = DateTime.MinValue;

        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public string StatusDisplay
        {
            get => _statusDisplay;
            set => SetProperty(ref _statusDisplay, value);
        }

        public string Duration
        {
            get => _duration;
            set => SetProperty(ref _duration, value);
        }

        public string StartedAtDisplay => StartedAt.Date == DateTime.Today
            ? StartedAt.ToString("HH:mm:ss")
            : StartedAt.ToString("dd MMM HH:mm");
        public string Tooltip => $"Session: {SessionId}\nStarted: {StartedAt:HH:mm:ss}\n{(CompletedAt.HasValue ? $"Finished: {CompletedAt:HH:mm:ss}" : "In progress...")}";
    }

    /// <summary>
    /// Render-only model for one user waiting in the queue behind the active syncer.
    /// Position updates in place as users ahead complete/cancel, so the WPF binding
    /// re-renders the order numbers without rebuilding the panel.
    /// </summary>
    public class QueuedUserDisplay : ObservableObject
    {
        private int _position;
        private string _displayName = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string DisplayName
        {
            get => _displayName;
            set => SetProperty(ref _displayName, value);
        }
        public string ComputerName { get; set; } = string.Empty;
        public DateTime QueuedAt { get; set; }

        public int Position
        {
            get => _position;
            set
            {
                if (SetProperty(ref _position, value))
                    OnPropertyChanged(nameof(PositionLabel));
            }
        }

        public string PositionLabel => $"#{Position}";
    }

    public class SyncingUserDisplay : ObservableObject
    {
        private string? _revitUsername;
        public string SessionId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string? RevitUsername
        {
            get => _revitUsername;
            set
            {
                if (SetProperty(ref _revitUsername, value))
                    OnPropertyChanged(nameof(DisplayName));
            }
        }
        public string ComputerName { get; set; } = string.Empty;
        public DateTime SyncStartedAt { get; set; }

        public string DisplayName => !string.IsNullOrEmpty(RevitUsername) ? RevitUsername : Username;

        public string Duration
        {
            get
            {
                var ts = DateTime.UtcNow - SyncStartedAt;
                if (ts.TotalMinutes < 1) return "<1m";
                return $"{(int)ts.TotalMinutes}m";
            }
        }
    }
}
