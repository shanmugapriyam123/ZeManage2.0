using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Features;
using BIManage.Revit.SyncTrafficControl;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Helpers;
using BIManage.Revit.Productivity;

namespace BIManage.Revit.BackgroundSync
{
    /// <summary>
    /// Background sync orchestration engine.
    /// Monitors timer intervals, idle state, and schedule windows to automatically
    /// trigger sync/relinquish/exit operations on open Revit documents.
    ///
    /// Sync engine supports two modes:
    ///   - "Always On" (SyncAllTheTime=true):  sync based on timer interval only
    ///   - "When Paused" (SyncAllTheTime=false): sync only when user is idle
    /// </summary>
    public class BackgroundSyncEngine : IDisposable
    {
        private readonly IFeatureToggleService _featureToggleService;
        private readonly IProductivityTracker _productivityTracker;
        private readonly SyncRepository _syncRepository;
        private readonly SyncTrafficControlService _syncTrafficControl;
        private readonly ILogger _logger;

        // ExternalEvent handles for Revit main thread operations
        private readonly ExternalEvent _syncEvent;
        private readonly BackgroundSyncExternalEventHandler _syncHandler;
        private readonly ExternalEvent _relinquishEvent;
        private readonly BackgroundRelinquishExternalEventHandler _relinquishHandler;
        private readonly ExternalEvent _autoExitEvent;
        private readonly AutoExitExternalEventHandler _autoExitHandler;
        // Scan event: runs document iteration on the Revit main thread to avoid
        // AccessViolationException when Revit closes a document mid-scan.
        private readonly ExternalEvent _scanEvent;
        private readonly BackgroundScanExternalEventHandler _scanHandler;

        // Timer
        private readonly System.Timers.Timer _timer;
        private readonly TimeSpan _tickInterval = TimeSpan.FromSeconds(30);

        // Concurrency
        private readonly object _tickLock = new object();
        private bool _isBusy;
        private bool _disposed;

        // Per-document tracking
        private readonly ConcurrentDictionary<string, DocumentSyncState> _documentStates
            = new ConcurrentDictionary<string, DocumentSyncState>();

        // Compact model tracking (once-a-day)
        private readonly HashSet<string> _compactedToday = new HashSet<string>();
        private int _lastCompactDay = -1;

        // Auto-exit guard (fire only once)
        private bool _exitRequested;

        // Cached settings (refreshed each tick from DB)
        private BackgroundSyncSettings _cachedSettings;

        // Func to get UIApplication — avoids storing UIApplication directly (thread safety)
        private readonly Func<UIApplication> _uiAppGetter;

        // Revit main window handle for WM_NULL poke
        private IntPtr _mainWindowHandle;

        public BackgroundSyncEngine(
            IFeatureToggleService featureToggleService,
            IProductivityTracker productivityTracker,
            SyncRepository syncRepository,
            SyncTrafficControlService syncTrafficControl,
            ILogger logger,
            ExternalEvent syncEvent,
            BackgroundSyncExternalEventHandler syncHandler,
            ExternalEvent relinquishEvent,
            BackgroundRelinquishExternalEventHandler relinquishHandler,
            ExternalEvent autoExitEvent,
            AutoExitExternalEventHandler autoExitHandler,
            Func<UIApplication> uiAppGetter)
        {
            _featureToggleService = featureToggleService ?? throw new ArgumentNullException(nameof(featureToggleService));
            _productivityTracker = productivityTracker ?? throw new ArgumentNullException(nameof(productivityTracker));
            _syncRepository = syncRepository ?? throw new ArgumentNullException(nameof(syncRepository));
            _syncTrafficControl = syncTrafficControl;
            _logger = logger;
            _syncEvent = syncEvent ?? throw new ArgumentNullException(nameof(syncEvent));
            _syncHandler = syncHandler ?? throw new ArgumentNullException(nameof(syncHandler));
            _relinquishEvent = relinquishEvent ?? throw new ArgumentNullException(nameof(relinquishEvent));
            _relinquishHandler = relinquishHandler ?? throw new ArgumentNullException(nameof(relinquishHandler));
            _autoExitEvent = autoExitEvent ?? throw new ArgumentNullException(nameof(autoExitEvent));
            _autoExitHandler = autoExitHandler ?? throw new ArgumentNullException(nameof(autoExitHandler));
            _uiAppGetter = uiAppGetter ?? throw new ArgumentNullException(nameof(uiAppGetter));

            // Create scan event for running document iteration on the Revit main thread.
            _scanHandler = new BackgroundScanExternalEventHandler(logger);
            _scanEvent = ExternalEvent.Create(_scanHandler);

            _timer = new System.Timers.Timer(_tickInterval.TotalMilliseconds);
            _timer.Elapsed += (s, e) => OnTimerTick();
            _timer.AutoReset = true;

            // Subscribe to completion events for tracking
            _syncHandler.SyncCompleted += OnSyncCompleted;
            _syncHandler.SyncSkipped += OnSyncSkipped;
            _relinquishHandler.RelinquishCompleted += OnRelinquishCompleted;

            // Reset exit guard if user cancels the countdown — allows re-triggering next tick
            _autoExitHandler.ExitCancelled += () => _exitRequested = false;
        }

        /// <summary>Start the background sync engine.</summary>
        public void Start()
        {
            _timer.Start();
            _logger?.LogInfo($"[BackgroundSyncEngine] Started (tick interval: {_tickInterval.TotalSeconds}s)");
        }

        /// <summary>Stop the background sync engine.</summary>
        public void Stop()
        {
            _timer.Stop();
            _logger?.LogInfo("[BackgroundSyncEngine] Stopped");
        }

        /// <summary>Record that a sync completed for a document (called from EventRegistryService).</summary>
        public void RecordSyncCompleted(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return;
            var state = GetOrCreateState(modelGuid);
            state.LastSyncedUtc = DateTime.UtcNow;
        }

        /// <summary>Record that a relinquish completed for a document.</summary>
        public void RecordRelinquishCompleted(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return;
            var state = GetOrCreateState(modelGuid);
            state.LastRelinquishedUtc = DateTime.UtcNow;
        }

        private void OnTimerTick()
        {
            if (_disposed) return;

            // Gate: prevent concurrent ticks
            lock (_tickLock)
            {
                if (_isBusy) return;
                _isBusy = true;
            }

            try
            {
                // Load settings from DB (runs on ThreadPool to avoid deadlock on timer thread).
                // Settings must be loaded before the master gate so Auto-Close (ExitRevitOnIdle)
                // can keep the tick alive on its own — even when Background Sync and Relinquish
                // checkboxes are off and their feature toggles are flipped off accordingly.
                // Ticket: BC20B6E4.
                _cachedSettings = Task.Run(() => _syncRepository.GetBackgroundSyncSettingsAsync()).GetAwaiter().GetResult();

                if (_cachedSettings == null)
                {
                    _logger?.LogDebug("[BackgroundSyncEngine] No settings loaded");
                    return;
                }

                // Master gate: tick proceeds if ANY of the three automations is wanted.
                // Sync and Relinquish still respect their feature toggles inside ShouldSync /
                // ShouldRelinquish (admin kill-switch). Auto-Close is purely settings-driven so
                // it runs standalone — when its countdown completes, AutoExitExternalEventHandler
                // performs Synchronize-with-Central + full Relinquish (CheckedOutElements=true)
                // before CloseMainWindow, independent of the sync/relinquish toggles.
                if (!_cachedSettings.IsEnabled && !_cachedSettings.EnableRelinquish && !_cachedSettings.ExitRevitOnIdle)
                {
                    _logger?.LogDebug("[BackgroundSyncEngine] Sync, relinquish and auto-exit all disabled in settings");
                    return;
                }

                // Gate: schedule window
                if (!IsWithinSchedule(_cachedSettings))
                {
                    _logger?.LogDebug("[BackgroundSyncEngine] Outside schedule window — skipping tick");
                    return;
                }

                // Use Win32 GetLastInputInfo for system-level idle detection.
                // Idle = no keyboard/mouse input for IdleTimeoutMinutes (no keyboard/mouse input detected).
                // Foreground check intentionally omitted — being in another app does not mean idle.
                var systemIdleTime = RevitInteropHelper.GetTimeSinceLastInput();
                var isIdle = systemIdleTime >= TimeSpan.FromMinutes(_cachedSettings.IdleTimeoutMinutes);

                // Only raise the document scan when sync or relinquish is actually wanted.
                // When only Auto-Close is on, we skip the scan to avoid touching documents
                // unnecessarily — Auto-Close just needs the idle timer and CheckAutoExit.
                if (_cachedSettings.IsEnabled || _cachedSettings.EnableRelinquish)
                {
                    var capturedSettings = _cachedSettings;
                    _scanHandler.ScanAction = (uiApp) => ProcessOpenDocuments(uiApp, capturedSettings, isIdle, systemIdleTime);
                    _scanEvent.Raise();
                }

                // Auto-exit check (independent of sync/relinquish processing)
                CheckAutoExit(_cachedSettings);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[BackgroundSyncEngine] Tick error: {ex.Message}", ex);
            }
            finally
            {
                lock (_tickLock)
                {
                    _isBusy = false;
                }
            }
        }

        /// <summary>
        /// Iterates open documents and decides whether to sync/relinquish.
        /// MUST be called on the Revit main thread (via BackgroundScanExternalEventHandler).
        /// Direct calls from the timer thread cause AccessViolationException because the
        /// Documents collection is backed by Revit native objects that may be freed concurrently.
        /// </summary>
        private void ProcessOpenDocuments(UIApplication uiApp, BackgroundSyncSettings settings, bool isIdle, TimeSpan systemIdleTime)
        {
            if (uiApp == null) return;

            try
            {
                // Cache Revit main window handle for WM_NULL poke and foreground checks
                if (_mainWindowHandle == IntPtr.Zero)
                    _mainWindowHandle = uiApp.MainWindowHandle;

                foreach (Document doc in uiApp.Application.Documents)
                {
                    try
                    {
                        // Skip linked, family, non-workshared docs
                        if (doc.IsLinked || doc.IsFamilyDocument) continue;
                        if (!DocumentTypeHelper.IsWorkshared(doc)) continue;
                        if (doc.IsDetached) continue;

                        var modelGuid = ModelGuidHelper.GetModelGuid(doc, _logger);
                        if (string.IsNullOrEmpty(modelGuid)) continue;

                        var state = GetOrCreateState(modelGuid);

                        // === SYNC CHECK ===
                        if (ShouldSync(settings, state, isIdle))
                        {
                            TriggerSync(settings, modelGuid, state);
                        }

                        // === RELINQUISH CHECK ===
                        // Pass systemIdleTime so relinquish can apply its own idle threshold
                        // 
                        if (ShouldRelinquish(settings, state, systemIdleTime))
                        {
                            TriggerRelinquish(modelGuid, state);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning($"[BackgroundSyncEngine] Error processing document: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[BackgroundSyncEngine] Error iterating documents: {ex.Message}", ex);
            }
        }

        // ── Sync ──────────────────────────────────────────────────────────────────

        private bool ShouldSync(BackgroundSyncSettings settings, DocumentSyncState state, bool isIdle)
        {
            // Per-action gate — ShouldRelinquish has the equivalent for its toggle. Without this,
            // a tick that fired only because relinquish is on would still run sync.
            if (!settings.IsEnabled) return false;
            if (!_featureToggleService.IsFeatureEnabled("BackgroundSync")) return false;

            // Mode check: "Always On" skips idle check, "When Paused" requires idle
            if (!settings.SyncAllTheTime)
            {
                if (!isIdle) return false;
            }

            // Interval check: has enough time elapsed since last sync?
            var elapsed = DateTime.UtcNow - state.LastSyncedUtc;
            if (elapsed < TimeSpan.FromMinutes(settings.SyncIntervalMinutes))
                return false;

            return true;
        }

        private void TriggerSync(BackgroundSyncSettings settings, string modelGuid, DocumentSyncState state)
        {
            // Check sync traffic control — another user may be syncing
            if (_syncTrafficControl != null)
            {
                var sessionId = state.SessionId ?? "";
                var decision = _syncTrafficControl.EvaluateSync(modelGuid, modelGuid, sessionId, Environment.UserName);
                if (decision.Action == Revit.SyncTrafficControl.Models.SyncAction.Blocked)
                {
                    _logger?.LogInfo($"[BackgroundSyncEngine] Sync blocked for {modelGuid} — {decision.BlockedByUsername} is syncing, auto-queuing");
                    _syncTrafficControl.JoinQueue(modelGuid, modelGuid, sessionId, Environment.UserName);
                    return;
                }
            }

            // Determine compact flag
            var compact = ShouldCompact(settings, modelGuid);

            _logger?.LogInfo($"[BackgroundSyncEngine] Triggering sync for {modelGuid} (compact: {compact}, interval: {settings.SyncIntervalMinutes}min)");

            // Set handler properties and raise ExternalEvent
            _syncHandler.PendingModelGuid = modelGuid;
            _syncHandler.Compact = compact;
            _syncHandler.SyncEvenIfNoChanges = settings.SyncEvenIfNoChanges;

            // Mark as background-initiated sync so SyncTrafficControl suppresses the conflict dialog
            if (_syncTrafficControl != null)
            {
                _syncTrafficControl.IsBackgroundSyncActive = true;
                _syncTrafficControl.SetBackgroundSyncActive(modelGuid, true);
            }

            _syncEvent.Raise();

            // Send WM_NULL to wake Revit's message pump — ensures the ExternalEvent
            // is processed promptly even when Revit is in the background
            RevitInteropHelper.PokeRevit(_mainWindowHandle);

            // Optimistically mark compacted (handler will actually check)
            if (compact)
                _compactedToday.Add(modelGuid);

            // Remember the previous timestamp so SyncSkipped (e.g. too many views open) can
            // revert it — that scenario must NOT reset the timer.
            state.PreviousLastSyncedUtc = state.LastSyncedUtc;

            // Update last sync time (will be updated again on completion via RecordSyncCompleted)
            state.LastSyncedUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Called when the sync handler aborted before SynchronizeWithCentral ran (currently:
        /// open-views over the configured limit). Revert the optimistic timestamp so the next
        /// tick still considers this document due — i.e. the sync timer does NOT get reset.
        /// </summary>
        private void OnSyncSkipped(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return;
            if (_documentStates.TryGetValue(modelGuid, out var state))
            {
                state.LastSyncedUtc = state.PreviousLastSyncedUtc;
                _logger?.LogInfo($"[BackgroundSyncEngine] Sync skipped for {modelGuid} — timer NOT reset (LastSyncedUtc reverted to {state.LastSyncedUtc:o})");
            }

            // Clear the background-sync flag so user-initiated syncs aren't suppressed.
            if (_syncTrafficControl != null)
            {
                _syncTrafficControl.IsBackgroundSyncActive = false;
                _syncTrafficControl.SetBackgroundSyncActive(modelGuid, false);
            }
        }

        // ── Relinquish ────────────────────────────────────────────────────────────

        private bool ShouldRelinquish(BackgroundSyncSettings settings, DocumentSyncState state, TimeSpan systemIdleTime)
        {
            if (!settings.EnableRelinquish) return false;
            if (!_featureToggleService.IsFeatureEnabled("BackgroundRelinquish")) return false;

            // Standalone relinquish (safety net for elements not relinquished during sync):
            // Use a 5-minute minimum idle threshold, or RelinquishIntervalMinutes if set lower.
            // The primary relinquish now happens during sync (CheckedOutElements = true),
            // so this standalone check is a fallback for elements checked out outside sync.
            var minIdleMinutes = Math.Min(settings.RelinquishIntervalMinutes, 5);
            if (systemIdleTime < TimeSpan.FromMinutes(minIdleMinutes)) return false;

            // Interval check: at least RelinquishIntervalMinutes since last standalone relinquish
            var elapsed = DateTime.UtcNow - state.LastRelinquishedUtc;
            if (elapsed < TimeSpan.FromMinutes(settings.RelinquishIntervalMinutes)) return false;

            return true;
        }

        private void TriggerRelinquish(string modelGuid, DocumentSyncState state)
        {
            _logger?.LogInfo($"[BackgroundSyncEngine] Triggering relinquish for {modelGuid}");

            _relinquishHandler.PendingModelGuid = modelGuid;
            _relinquishEvent.Raise();

            // Send WM_NULL to wake Revit's message pump
            RevitInteropHelper.PokeRevit(_mainWindowHandle);

            state.LastRelinquishedUtc = DateTime.UtcNow;
        }

        // ── Compact Model ─────────────────────────────────────────────────────────

        private bool ShouldCompact(BackgroundSyncSettings settings, string modelKey)
        {
            if (!settings.CompactModelOnceADay) return false;

            // Reset tracking at midnight
            var today = DateTime.Today.DayOfYear;
            if (today != _lastCompactDay)
            {
                _compactedToday.Clear();
                _lastCompactDay = today;
            }

            // Already compacted today?
            if (_compactedToday.Contains(modelKey)) return false;

            // Night-only restriction: 00:00–05:59
            if (settings.CompactAtNightOnly && DateTime.Now.Hour >= 6) return false;

            return true;
        }

        // ── Schedule Window ───────────────────────────────────────────────────────

        private bool IsWithinSchedule(BackgroundSyncSettings settings)
        {
            if (!settings.EnableSchedule) return true; // No schedule = always active

            var now = DateTime.Now.TimeOfDay;

            if (!TimeSpan.TryParse(settings.ScheduleStartTime, out var start))
                start = new TimeSpan(21, 0, 0); // Default 21:00

            if (!TimeSpan.TryParse(settings.ScheduleEndTime, out var end))
                end = new TimeSpan(9, 0, 0); // Default 09:00

            if (start < end)
            {
                // Same-day window: e.g. 09:00–17:00
                return now >= start && now <= end;
            }
            else
            {
                // Overnight window: e.g. 21:00–09:00 (crosses midnight)
                return now >= start || now <= end;
            }
        }

        // ── Auto-Exit ─────────────────────────────────────────────────────────────

        private void CheckAutoExit(BackgroundSyncSettings settings)
        {
            if (!settings.ExitRevitOnIdle) return;
            if (_exitRequested) return;

            // Use Win32 GetLastInputInfo for accurate system-level idle duration
            var systemIdleTime = RevitInteropHelper.GetTimeSinceLastInput();
            if (systemIdleTime < TimeSpan.FromMinutes(settings.ExitRevitAfterMinutes))
                return;

            _exitRequested = true;
            _logger?.LogWarning($"[BackgroundSyncEngine] User idle for {systemIdleTime.TotalMinutes:F0} min — triggering auto-exit (threshold: {settings.ExitRevitAfterMinutes} min)");

            try
            {
                // Fire ExternalEvent on Revit's main thread so it can sync all open documents
                // before closing. AutoExitExternalEventHandler handles the sync + CloseMainWindow.
                _autoExitEvent.Raise();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[BackgroundSyncEngine] Failed to raise auto-exit event: {ex.Message}", ex);
                _exitRequested = false; // Allow retry
            }
        }

        // ── State Management ──────────────────────────────────────────────────────

        private DocumentSyncState GetOrCreateState(string modelGuid)
        {
            return _documentStates.GetOrAdd(modelGuid, _ => new DocumentSyncState
            {
                LastSyncedUtc = DateTime.UtcNow, // Don't sync immediately on first discovery
                LastRelinquishedUtc = DateTime.UtcNow
            });
        }

        // ── Event Handlers ────────────────────────────────────────────────────────

        private void OnSyncCompleted(string modelGuid, bool succeeded)
        {
            // Reset flag — ExternalEvent execution is over, manual syncs can show conflict dialog again
            if (_syncTrafficControl != null)
            {
                _syncTrafficControl.IsBackgroundSyncActive = false;
                _syncTrafficControl.SetBackgroundSyncActive(modelGuid, false);
            }

            if (succeeded)
            {
                RecordSyncCompleted(modelGuid);
                _logger?.LogDebug($"[BackgroundSyncEngine] Sync state updated for {modelGuid}");
            }
        }

        private void OnRelinquishCompleted(string modelGuid, bool succeeded)
        {
            if (succeeded)
            {
                RecordRelinquishCompleted(modelGuid);
                _logger?.LogDebug($"[BackgroundSyncEngine] Relinquish state updated for {modelGuid}");
            }
        }

        // ── Dispose ───────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _timer?.Dispose();

            _syncHandler.SyncCompleted -= OnSyncCompleted;
            _syncHandler.SyncSkipped -= OnSyncSkipped;
            _relinquishHandler.RelinquishCompleted -= OnRelinquishCompleted;

            _logger?.LogInfo("[BackgroundSyncEngine] Disposed");
        }
    }

    /// <summary>
    /// Tracks per-document sync/relinquish timestamps for interval-based scheduling.
    /// </summary>
    internal class DocumentSyncState
    {
        public DateTime LastSyncedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Snapshot of LastSyncedUtc taken just before the optimistic update in TriggerSync.
        /// Used by OnSyncSkipped to revert when sync is aborted (e.g. too many views open).
        /// </summary>
        public DateTime PreviousLastSyncedUtc { get; set; } = DateTime.UtcNow;

        public DateTime LastRelinquishedUtc { get; set; } = DateTime.UtcNow;
        public string SessionId { get; set; }
    }
}
