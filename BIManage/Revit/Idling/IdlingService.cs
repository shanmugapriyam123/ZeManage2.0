using System;
using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using BIManage.Common.Helpers;
using BIManage.Core.Detection;
using BIManage.Core.Features;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.DependencyInjection;
using BIManage.Infrastructure.Logging;
using BIManage.Licensing;
using BIManage.Revit.Context;
using BIManage.Revit.EventRegistry;

namespace BIManage.Revit.Idling
{
    /// <summary>
    ///     Idling event service with hybrid throttle (time + work-based)
    ///     Executes background work only if:
    ///     1. Work exists in queue AND
    ///     2. Throttle interval has elapsed
    /// </summary>
    public class IdlingService : IIdlingService
    {
        private UIApplication? _uiApplication;
        private readonly IUIApplicationProvider _uiApplicationProvider;
        private readonly IFeatureToggleService _featureToggleService;
        private readonly ILogger? _logger;
        private readonly SessionRepository? _sessionRepository;
        private readonly BIManage.Data.SQLite.SyncRepository? _syncRepository;
        private readonly IRevitContext? _revitContext;
        private readonly PositionRestorationService? _positionRestorationService;
        private BIManage.Infrastructure.Api.SessionSyncService? _sessionSyncService;
        private readonly UnmonitoredUserDetectionService? _detectionService;
        private readonly LicenseValidator? _licenseValidator;
        private readonly BootstrapHealthTracker? _healthTracker;
        private readonly ConcurrentQueue<Action> _workQueue;
        private readonly TimeSpan _throttleInterval;

        private DateTime _lastExecutionTime;
        private bool _isRegistered;

        // Throttle metrics for observability
        private int _throttleRejectionsCount;
        private int _totalExecutionsCount;
        private DateTime? _lastLogTime;

        // [Fix-4 diagnostic] Idling instrumentation for the first 5 minutes of a session.
        // Aggregates stats IN MEMORY (no file I/O per tick) and emits ONLY:
        //   - one LogInfo summary every ~30 s
        //   - one LogWarning on any inter-tick gap > 1000 ms (the user-facing freeze signal)
        //   - one final LogInfo summary when the 5-min window closes
        // Per-tick overhead is sub-microsecond (a few arithmetic ops). No per-tick file writes,
        // no string allocations except when a summary or warning is actually emitted. Safe on
        // AV-active customer machines because we don't poke the disk on every tick.
        // Captures whether Revit's Idling event fires continuously (normal) or stops for
        // seconds-to-minutes at a time (UI thread busy elsewhere). Single-threaded — OnIdling
        // runs on Revit's UI thread, so no synchronization needed on these fields.
        private DateTime _firstIdlingObservedAt = DateTime.MinValue;
        private DateTime _lastIdlingTickAt = DateTime.MinValue;
        private DateTime _idleTraceLastSummaryAt = DateTime.MinValue;
        private long _idleTraceTickCount;
        private double _idleTraceMaxGapMs;
        private double _idleTraceSumGapMs;
        private int _idleTraceLongGapCount;
        private bool _idleTraceFinalSummaryEmitted;

        public IdlingService(
            IUIApplicationProvider uiApplicationProvider,
            IFeatureToggleService featureToggleService,
            ILogger? logger,
            SessionRepository? sessionRepository = null,
            BIManage.Data.SQLite.SyncRepository? syncRepository = null,
            IRevitContext? revitContext = null,
            TimeSpan? throttleInterval = null,
            PositionRestorationService? positionRestorationService = null,
            BIManage.Infrastructure.Api.SessionSyncService? sessionSyncService = null,
            UnmonitoredUserDetectionService? detectionService = null,
            LicenseValidator? licenseValidator = null,
            BootstrapHealthTracker? healthTracker = null)
        {
            _uiApplicationProvider = uiApplicationProvider ?? throw new ArgumentNullException(nameof(uiApplicationProvider));
            _featureToggleService = featureToggleService ?? throw new ArgumentNullException(nameof(featureToggleService));
            _logger = logger;
            _sessionRepository = sessionRepository;
            _syncRepository = syncRepository;
            _revitContext = revitContext;
            _positionRestorationService = positionRestorationService;
            _sessionSyncService = sessionSyncService;
            _detectionService = detectionService;
            _licenseValidator = licenseValidator;
            _healthTracker = healthTracker;
            _workQueue = new ConcurrentQueue<Action>();
            _throttleInterval = throttleInterval ?? TimeSpan.FromSeconds(1); // Default: 1 second throttle
            _lastExecutionTime = DateTime.MinValue;
        }

        /// <summary>
        /// Wires in the SessionSyncService after it is registered in Phase 2 bootstrap.
        /// Called from RevitBootstrapper once SessionSyncService is available.
        /// </summary>
        public void SetSessionSyncService(BIManage.Infrastructure.Api.SessionSyncService svc)
            => _sessionSyncService = svc;

        public void RegisterIdlingEvent()
        {
            if (_isRegistered) return;

            // Get UIApplication from provider when registering
            _uiApplication = _uiApplicationProvider.UIApplication;
            if (_uiApplication == null)
            {
                _logger?.LogWarning("Cannot register Idling event - UIApplication not yet available");
                return;
            }

            try
            {
                _uiApplication.Idling += OnIdling;
                _isRegistered = true;
                _logger?.LogInfo($"Idling event registered with {_throttleInterval.TotalSeconds:F1}s throttle");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to register Idling event: {ex.Message}", ex);
            }
        }

        public void UnregisterIdlingEvent()
        {
            if (!_isRegistered || _uiApplication == null) return;

            try
            {
                _uiApplication.Idling -= OnIdling;
                _isRegistered = false;

                // Clear work queue (ConcurrentQueue doesn't have Clear())
                while (_workQueue.TryDequeue(out _)) { }

                _logger?.LogInfo("Idling event unregistered");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to unregister Idling event: {ex.Message}", ex);
            }
        }

        public void QueueWork(Action workItem)
        {
            if (workItem == null) throw new ArgumentNullException(nameof(workItem));
            _workQueue.Enqueue(workItem);
            _logger?.LogDebug($"Work queued for idling execution (queue size: {_workQueue.Count})");
        }

        public bool HasPendingWork => !_workQueue.IsEmpty;

        public TimeSpan TimeSinceLastExecution => DateTime.UtcNow - _lastExecutionTime;

        // Watchdog threshold for a single OnIdling tick on the UI thread. Idling fires
        // many times per second when Revit is at rest; a single handler that takes >5s
        // is a clear sign someone has called a blocking API on the UI thread (e.g. cold
        // PerformanceCounter enumeration, synchronous HTTP, or a blocking DB query). This
        // is the watchdog that would have caught the 2026-05-19 GetGraphicsUsagePercent
        // deadlock in real time instead of via post-mortem log analysis.
        private const long IdlingWatchdogWarnThresholdMs = 5000;
        private long _idlingTickCount;
        private long _idlingWarningCount;

        private void OnIdling(object sender, EventArgs e)
        {
            // CRITICAL: nothing in OnIdling may call a blocking API on the UI thread.
            // No synchronous HTTP, no PerformanceCounter category enumeration, no
            // synchronous DB queries that aren't bounded by milliseconds. Long-running
            // work goes into the QueueWork lambdas (each work item is itself synchronous
            // but bounded by maxWorkItemsPerCycle below) or into Task.Run from inside an
            // async lambda. If you must add something here, profile it on a cold Windows
            // perf-counter cache before merging.
            var watchdog = System.Diagnostics.Stopwatch.StartNew();
            long entryTickIndex = System.Threading.Interlocked.Increment(ref _idlingTickCount);
            try
            {
                // [Fix-4 diagnostic] Idling trace — first 5 min, no per-tick I/O.
                // Tracks gap-since-last-tick in memory; emits a summary every 30 s and an
                // immediate warning on any gap > 1 s. Tells us whether Revit's Idling event
                // is firing continuously during the user-reported post-ribbon freeze.
                try
                {
                    var nowUtc = DateTime.UtcNow;
                    if (_firstIdlingObservedAt == DateTime.MinValue)
                    {
                        _firstIdlingObservedAt = nowUtc;
                        _idleTraceLastSummaryAt = nowUtc;
                        _lastIdlingTickAt = nowUtc;
                        // First-ever tick — no prior gap to measure. Skip accounting.
                    }
                    else
                    {
                        var elapsedSinceStart = (nowUtc - _firstIdlingObservedAt).TotalSeconds;
                        if (elapsedSinceStart <= 300)
                        {
                            var gapMs = (nowUtc - _lastIdlingTickAt).TotalMilliseconds;
                            _idleTraceTickCount++;
                            _idleTraceSumGapMs += gapMs;
                            if (gapMs > _idleTraceMaxGapMs) _idleTraceMaxGapMs = gapMs;
                            if (gapMs > 1000)
                            {
                                _idleTraceLongGapCount++;
                                _logger?.LogWarning(
                                    $"[IdleTrace] LONG-GAP Idling after {gapMs:F0} ms idle " +
                                    $"(t={elapsedSinceStart:F1}s, pendingWork={_workQueue.Count}) — Revit UI was busy");
                            }
                            if ((nowUtc - _idleTraceLastSummaryAt).TotalSeconds >= 30)
                            {
                                _idleTraceLastSummaryAt = nowUtc;
                                var avgGap = _idleTraceTickCount > 0 ? _idleTraceSumGapMs / _idleTraceTickCount : 0;
                                _logger?.LogInfo(
                                    $"[IdleTrace] summary t={elapsedSinceStart:F0}s " +
                                    $"ticks={_idleTraceTickCount} " +
                                    $"avgGapMs={avgGap:F0} " +
                                    $"maxGapMs={_idleTraceMaxGapMs:F0} " +
                                    $"longGaps={_idleTraceLongGapCount} " +
                                    $"pendingWork={_workQueue.Count}");
                            }
                        }
                        else if (!_idleTraceFinalSummaryEmitted)
                        {
                            _idleTraceFinalSummaryEmitted = true;
                            var avgGap = _idleTraceTickCount > 0 ? _idleTraceSumGapMs / _idleTraceTickCount : 0;
                            _logger?.LogInfo(
                                $"[IdleTrace] final (5-min window closed) " +
                                $"ticks={_idleTraceTickCount} " +
                                $"avgGapMs={avgGap:F0} " +
                                $"maxGapMs={_idleTraceMaxGapMs:F0} " +
                                $"longGaps={_idleTraceLongGapCount}");
                        }
                        _lastIdlingTickAt = nowUtc;
                    }
                }
                catch { /* diagnostic must never throw into OnIdling */ }

                // [Fix-1a diagnostic] Every 5 minutes, log the live-ALC identity so we can
                // detect if Application.Instance changes/disappears during the session
                // (would indicate state corruption between subscription time and use time).
                LogPeriodicLiveAlcHealth();

                // Track when Revit was last the foreground window. Cheap (~0.1 µs Win32 call).
                // Used by the heartbeat gate to distinguish "user is in Revit" from "user is
                // somewhere else on the system" — alt-tabbed-to-browser should NOT count as
                // active even though system-wide input is fresh.
                if (_revitMainHwnd == IntPtr.Zero && _uiApplication != null)
                    _revitMainHwnd = _uiApplication.MainWindowHandle;
                if (_revitMainHwnd != IntPtr.Zero
                    && BIManage.Revit.BackgroundSync.RevitInteropHelper.IsRevitForeground(_revitMainHwnd))
                {
                    _lastRevitForegroundTime = DateTime.UtcNow;
                }

                // Idling fires only when no dialogs are open — notify dialog tracker
                BIManage.Revit.Timing.StartupDialogTracker.OnDialogDismissed();

                // Recovery runs BEFORE global pause check — if bootstrap failed and set
                // IsGlobalPaused=true (Breached), we still need recovery to fix it
                ExecuteBootstrapRecovery();

                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;

                // Process position restoration queue BEFORE throttle gate — must run immediately
                // regardless of work queue state to reverse pin protection bypass moves
                ProcessPositionRestorations();

                // Heartbeat runs before throttle gate — must fire every 60s regardless of work queue state
                ExecutePeriodicHeartbeat();

                // Unmonitored user detection — self-throttles to every 5 minutes
                ExecuteUnmonitoredUserDetection();

                // License re-validation — every 4 hours
                ExecuteLicenseRevalidation();

                // Hybrid throttle: Execute only if work exists AND throttle interval elapsed
                var timeSinceLastExecution = DateTime.UtcNow - _lastExecutionTime;
                var hasPendingWork = HasPendingWork;
                var shouldExecute = hasPendingWork && timeSinceLastExecution >= _throttleInterval;

                if (!shouldExecute)
                {
                    // Log throttle rejections for observability
                    if (hasPendingWork)
                    {
                        _throttleRejectionsCount++;

                        // Log throttle stats every 10 seconds
                        var now = DateTime.UtcNow;
                        if (!_lastLogTime.HasValue || (now - _lastLogTime.Value).TotalSeconds >= 10)
                        {
                            _logger?.LogDebug($"Idling throttled: {_throttleRejectionsCount} rejections, queue depth: {_workQueue.Count}");
                            _lastLogTime = now;
                            _throttleRejectionsCount = 0; // Reset counter
                        }
                    }
                    return;
                }

                _lastExecutionTime = DateTime.UtcNow;
                _totalExecutionsCount++;

                // Warn if queue is backing up
                const int QUEUE_DEPTH_WARNING_THRESHOLD = 50;
                if (_workQueue.Count > QUEUE_DEPTH_WARNING_THRESHOLD)
                {
                    _logger?.LogWarning($"Idling queue backlog: {_workQueue.Count} items (threshold: {QUEUE_DEPTH_WARNING_THRESHOLD})");
                }

                // Process work queue (limit to avoid UI freeze)
                const int maxWorkItemsPerCycle = 10;
                var processedCount = 0;

                while (processedCount < maxWorkItemsPerCycle && _workQueue.TryDequeue(out var workItem))
                {
                    try
                    {
                        workItem();
                        processedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"Error executing idling work item: {ex.Message}", ex);
                    }
                }

                if (processedCount > 0)
                {
                    _logger?.LogDebug($"Idling: Processed {processedCount} work items (remaining: {_workQueue.Count})");
                }

                // Process position restoration queue after work items (elements may have moved)
                ProcessPositionRestorations();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnIdling handler: {ex.Message}", ex);
            }
            finally
            {
                watchdog.Stop();
                if (watchdog.ElapsedMilliseconds >= IdlingWatchdogWarnThresholdMs)
                {
                    // Throttle the warning so we don't spam the log if the same slow code
                    // path fires on every tick. Always log the first one, then every 10th.
                    long warningIndex = System.Threading.Interlocked.Increment(ref _idlingWarningCount);
                    if (warningIndex == 1 || warningIndex % 10 == 0)
                    {
                        _logger?.LogWarning(
                            $"[Watchdog] OnIdling tick #{entryTickIndex} took {watchdog.ElapsedMilliseconds}ms " +
                            $"on the Revit UI thread (threshold: {IdlingWatchdogWarnThresholdMs}ms). " +
                            $"Something in OnIdling is calling a blocking API on the UI thread — " +
                            $"check ExecutePeriodicHeartbeat / ExecuteUnmonitoredUserDetection / " +
                            $"ExecuteLicenseRevalidation / position-restoration handlers. " +
                            $"(Warning #{warningIndex}; logging every 10th occurrence)");
                    }
                }
            }
        }

        /// <summary>
        /// Process queued position restorations for protected elements that were moved via bypass
        /// </summary>
        private void ProcessPositionRestorations()
        {
            try
            {
                if (_positionRestorationService == null || !_positionRestorationService.HasPendingRestorations)
                    return;

                // Get active document from UIApplication
                var activeDoc = _uiApplication?.ActiveUIDocument?.Document;
                if (activeDoc == null || !activeDoc.IsValidObject)
                    return;

                _positionRestorationService.ProcessQueue(activeDoc, _uiApplication);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error processing position restorations: {ex.Message}", ex);
            }
        }

        private DateTime _lastRecoveryAttempt = DateTime.MinValue;
        private static readonly TimeSpan RecoveryInterval = TimeSpan.FromMinutes(5);
        private bool _recoveryLoggedOnce;

        /// <summary>
        /// Attempts to recover services that failed during bootstrap.
        /// Runs every 5 minutes until healthy or max attempts reached.
        /// Runs BEFORE IsGlobalPaused check so it can fix Breached state.
        /// </summary>
        private void ExecuteBootstrapRecovery()
        {
            try
            {
                if (_healthTracker == null || !_healthTracker.ShouldAttemptRecovery) return;

                if (DateTime.UtcNow - _lastRecoveryAttempt < RecoveryInterval) return;
                _lastRecoveryAttempt = DateTime.UtcNow;

                _healthTracker.RecoveryAttempts++;

                // The most common reason this fires at startup is a benign race: the first
                // Idling event arrives before the license heartbeat has landed, so
                // LicenseValidated is still false even though everything is actually fine.
                // The license validator will be retried below; once the heartbeat returns,
                // LicenseValidated flips true and recovery stops on its own. Log the first
                // few attempts at INFO so we don't pollute the log with WARN for a routine
                // race, but escalate to WARN if recovery is still incomplete after 3 cycles
                // (~15 min) — at that point something is genuinely broken.
                if (!_recoveryLoggedOnce)
                {
                    _recoveryLoggedOnce = true;
                    _logger?.LogInfo($"[Recovery] Bootstrap not yet fully healthy — attempting recovery (status: {_healthTracker.GetStatusSummary()}, attempt {_healthTracker.RecoveryAttempts}/{BootstrapHealthTracker.MaxRecoveryAttempts})");
                }
                else if (_healthTracker.RecoveryAttempts == 3)
                {
                    _logger?.LogWarning($"[Recovery] Still incomplete after 3 attempts (~15 min) — investigation needed (status: {_healthTracker.GetStatusSummary()})");
                }

                // If license not validated but license validator is available, retry
                if (!_healthTracker.LicenseValidated && _licenseValidator != null)
                {
                    QueueWork(async () =>
                    {
                        try
                        {
                            var info = await _licenseValidator.ValidateAsync();
                            if (info.Status == LicenseStatus.Valid || info.Status == LicenseStatus.GracePeriod)
                            {
                                _healthTracker.LicenseValidated = true;
                                // Unpause if we were breached
                                _featureToggleService.IsGlobalPaused = false;
                                _logger?.LogInfo("[Recovery] License validated successfully — features unpaused");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug($"[Recovery] License validation retry failed: {ex.Message}");
                        }
                    });
                }

                // If session not synced but repositories are available, retry
                if (!_healthTracker.SessionSynced && _sessionRepository != null && _revitContext != null)
                {
                    QueueWork(async () =>
                    {
                        try
                        {
                            var sessionId = _revitContext.SessionId.ToString();
                            await _sessionRepository.RecordHeartbeatAsync(sessionId);
                            _healthTracker.SessionSynced = true;
                            _logger?.LogInfo("[Recovery] Session heartbeat recovered");
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug($"[Recovery] Session sync retry failed: {ex.Message}");
                        }
                    });
                }

                if (_healthTracker.RecoveryAttempts >= BootstrapHealthTracker.MaxRecoveryAttempts)
                {
                    _logger?.LogWarning($"[Recovery] Max attempts ({BootstrapHealthTracker.MaxRecoveryAttempts}) reached — stopping recovery. Status: {_healthTracker.GetStatusSummary()}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in ExecuteBootstrapRecovery: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Periodic license re-validation — queries the server every 4 hours
        /// to detect seat changes, passive mode transitions, or license expiration.
        /// </summary>
        private void ExecuteLicenseRevalidation()
        {
            try
            {
                if (_licenseValidator == null || !_licenseValidator.NeedsRevalidation()) return;

                QueueWork(async () =>
                {
                    try
                    {
                        await _licenseValidator.RevalidateAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning($"License re-validation failed: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in ExecuteLicenseRevalidation: {ex.Message}", ex);
            }
        }

        private DateTime _lastDetectionQueueTime = DateTime.MinValue;
        private static readonly TimeSpan DetectionQueueInterval = TimeSpan.FromMinutes(4);

        /// <summary>
        /// Detect Revit users working on workshared models without BIManage installed.
        /// Queues scan every ~4 minutes; the detection service self-throttles to 5 minutes internally.
        /// Must be called from the Revit main thread (OnIdling) since TryScanAsync accesses Document API.
        /// </summary>
        private void ExecuteUnmonitoredUserDetection()
        {
            try
            {
                if (_detectionService == null) return;

                // Avoid queuing work items every idle tick — only queue periodically
                if (DateTime.UtcNow - _lastDetectionQueueTime < DetectionQueueInterval) return;
                _lastDetectionQueueTime = DateTime.UtcNow;

                var doc = _uiApplication?.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsValidObject) return;

                QueueWork(async () =>
                {
                    try
                    {
                        await _detectionService.TryScanAsync(doc);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"Unmonitored user detection failed: {ex.Message}", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in ExecuteUnmonitoredUserDetection: {ex.Message}", ex);
            }
        }

        private DateTime _lastHeartbeatTime = DateTime.MinValue;
        private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(60);
        private bool _heartbeatNullLogged;

        // When the user has been idle (no system-wide mouse/keyboard input) for at
        // least this long, skip the API heartbeat so the dashboard correctly transitions
        // the session to Crashed/Offline via the server's existing 5-min crash window.
        // Important on Modern Standby laptops where Revit's process keeps running
        // (and Idling keeps firing) even with the lid closed — without this gate,
        // the dashboard would show "Online" all night.
        // Local SQLite heartbeat is still recorded unconditionally so this Revit
        // instance doesn't appear locally-crashed to itself.
        private static readonly TimeSpan _idleSkipThreshold = TimeSpan.FromMinutes(2);
        private bool _idleSkipLogged;

        // Tracks when continuous idle started for company-settings based idle reporting.
        // Set to (UtcNow - currentIdleDuration) the first tick idle >= company threshold;
        // reset to null the moment the user becomes active again.
        private DateTime? _idleStartTime;

        // Tracks the last UTC time we observed Revit as the foreground window.
        // Updated on every OnIdling tick when Revit holds focus; consumed by the
        // heartbeat gate so that working in another application (browser, email)
        // does NOT count as "actively using Revit" — the dashboard should go Offline
        // when the user has alt-tabbed away even if mouse/keyboard input is fresh
        // in the other app. Seeded to construction time so the heartbeat is not
        // falsely paused during the first 5 min of a new session (loading splash,
        // model open, etc., before Idling observes Revit-foreground for the first time).
        private DateTime _lastRevitForegroundTime = DateTime.UtcNow;
        private IntPtr _revitMainHwnd = IntPtr.Zero;

        /// <summary>
        /// Send periodic heartbeat to maintain session health status
        /// </summary>
        private void ExecutePeriodicHeartbeat()
        {
            try
            {
                // Only send heartbeats if SessionRepository and RevitContext are available
                if (_sessionRepository == null || _revitContext == null)
                {
                    if (!_heartbeatNullLogged)
                    {
                        _logger?.LogWarning("Heartbeat skipped — SessionRepository or RevitContext not available");
                        _heartbeatNullLogged = true;
                    }
                    return;
                }

                var timeSinceLastHeartbeat = DateTime.UtcNow - _lastHeartbeatTime;
                if (timeSinceLastHeartbeat < _heartbeatInterval)
                    return;

                _lastHeartbeatTime = DateTime.UtcNow;

                var heartbeatTime = DateTime.UtcNow;

                // Active document ID and model GUID — accessing Document API requires the
                // Revit UI thread, so this MUST stay on the calling thread (OnIdling thread).
                string activeDocId = null;
                string activeModelGuid = null;
                var doc = _uiApplication?.ActiveUIDocument?.Document;
                if (doc != null)
                {
                    activeModelGuid = BIManage.Revit.Helpers.ModelGuidHelper.GetModelGuid(doc, _logger);
                    activeDocId = !string.IsNullOrEmpty(activeModelGuid)
                        ? activeModelGuid
                        : System.IO.Path.GetFileName(doc.PathName ?? doc.Title);
                }

                // Queue heartbeat work (async operation).
                // CRITICAL: metric collection (ProcessHelper.Get*Percent) MUST run inside the
                // lambda on a background thread, NOT on the OnIdling/UI thread. Historical bug
                // (root-cause-confirmed 2026-05-19): GetGraphicsUsagePercent on cold start
                // enumerates Windows "GPU Engine" performance counters, which on Windows 10/11
                // can block for SEVERAL MINUTES the first time (slow perf-counter service
                // handshake + 50–200 instances each requiring a sample). Pinning that to the
                // UI thread froze Revit's cursor for 5–6 minutes after first idle. Reproduced
                // in BIManageRevit_20260519_2229.log: 5-minute silent gap between the last
                // "License module gating applied" line and the next log entry.
                QueueWork(async () =>
                {
                    var sessionId = _revitContext.SessionId.ToString();

                    // Collect metrics on a thread-pool thread. PerformanceCounter calls inside
                    // GetGraphicsUsagePercent / GetCpuUsagePercent can be slow on cold Windows
                    // perf-counter cache — keeping them off the UI thread is non-negotiable.
                    double? sysMemoryPct = null, diskPct = null, revitMemoryPct = null, cpuPct = null, gpuPct = null;
                    try
                    {
                        await System.Threading.Tasks.Task.Run(() =>
                        {
                            sysMemoryPct   = ProcessHelper.GetMemoryUsagePercent();
                            diskPct        = ProcessHelper.GetDiskUsagePercent();
                            revitMemoryPct = ProcessHelper.GetProcessMemoryPercent();
                            cpuPct         = ProcessHelper.GetCpuUsagePercent();
                            gpuPct         = ProcessHelper.GetGraphicsUsagePercent();
                        });
                    }
                    catch (Exception metricEx)
                    {
                        _logger?.LogWarning($"Heartbeat metric collection failed (non-fatal): {metricEx.Message}");
                    }

                    // 1. Local SQLite heartbeat — isolated so schema errors don't block the API call
                    try
                    {
                        await _sessionRepository.RecordHeartbeatAsync(
                            sessionId,
                            activeDocumentId: activeDocId,
                            memoryUsagePercent: sysMemoryPct,
                            cpuUsagePercent: cpuPct,
                            diskUsagePercent: diskPct,
                            graphicsUsagePercent: gpuPct);
                        _logger?.LogDebug($"Heartbeat recorded (sysMem={sysMemoryPct}% revitMem={revitMemoryPct}% cpu={cpuPct}% disk={diskPct}% gpu={gpuPct}%)");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"Local heartbeat failed: {ex.Message}", ex);
                    }

                    // 2. API heartbeat — gated by user-activity idle detection.
                    // If the user hasn't touched mouse/keyboard for >= _idleSkipThreshold,
                    // skip the PATCH; the server's 5-min crash detection will then mark
                    // the session Crashed/Offline as the right dashboard outcome.
                    if (_sessionSyncService != null)
                    {
                        // Heartbeat is gated by BOTH:
                        //   (a) system-wide last-input idle duration, AND
                        //   (b) time since Revit was last the foreground window.
                        // EITHER condition exceeding the threshold marks the session idle
                        // and pauses the heartbeat. So working in another app (Chrome,
                        // Outlook) with fresh input still pauses Revit's heartbeat after
                        // 5 min of Revit being backgrounded — the dashboard correctly
                        // reflects "user is not in Revit", not just "user is at the desk".
                        var idleDuration   = BIManage.Revit.BackgroundSync.RevitInteropHelper.GetTimeSinceLastInput();
                        var timeNotInRevit = DateTime.UtcNow - _lastRevitForegroundTime;
                        var isIdle = idleDuration >= _idleSkipThreshold
                                  || timeNotInRevit >= _idleSkipThreshold;

                        if (isIdle)
                        {
                            if (!_idleSkipLogged)
                            {
                                var reason = (idleDuration >= _idleSkipThreshold)
                                    ? $"no system input for {idleDuration.TotalMinutes:F0} min"
                                    : $"Revit not in foreground for {timeNotInRevit.TotalMinutes:F0} min";
                                _logger?.LogInfo(
                                    $"Heartbeat paused — {reason} " +
                                    $"(threshold {_idleSkipThreshold.TotalMinutes:F0} min). Will resume when user interacts with Revit.");
                                _idleSkipLogged = true;
                            }
                        }
                        else
                        {
                            if (_idleSkipLogged)
                            {
                                _logger?.LogInfo(
                                    $"Heartbeat resumed — user input detected " +
                                    $"(input idle {idleDuration.TotalSeconds:F0} s, Revit-bg {timeNotInRevit.TotalSeconds:F0} s).");
                                _idleSkipLogged = false;
                            }

                            try
                            {
                                // API fields named "MB" but we send Revit-specific percentages
                                // (server treats them as numeric values regardless of unit)
                                await _sessionSyncService.SendHeartbeatAsync(
                                    sessionId,
                                    lastHeartbeat: heartbeatTime,
                                    memoryUsageMb: revitMemoryPct,   // Revit process memory % of total RAM
                                    cpuUsagePercent: cpuPct,          // Revit process CPU %
                                    diskUsageMb: diskPct,             // System disk usage %
                                    graphicsUsageMb: gpuPct,          // Revit GPU utilization %
                                    modelGuid: activeModelGuid);

                                // Update license cache immediately from response (no 4-hour delay)
                                var heartbeatResponse = _sessionSyncService.LastHeartbeatResponse;
                                if (heartbeatResponse != null)
                                    _licenseValidator?.UpdateFromHeartbeatResponse(heartbeatResponse);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError($"API heartbeat failed: {ex.Message}", ex);
                            }
                        }
                    }

                    // Company-settings idle tracking via SignalR.
                    // Fetches idleThresholdMinutes from GET /api/v1/agentdb/company-settings
                    // (cached 1h). Once the user has been idle >= that threshold, calculates
                    // accumulated idle seconds and sends them via SignalR so the portal can
                    // record productivity loss in real time without polling.
                    // Completely independent of the existing _idleSkipThreshold heartbeat gate above.
                    if (_sessionSyncService != null)
                    {
                        try
                        {
                            var companySettings = await _sessionSyncService.GetCompanySettingsAsync();
                            if (companySettings?.IsIdleTrackingEnabled == true && companySettings.IdleThresholdMinutes > 0)
                            {
                                var companyIdleThreshold = TimeSpan.FromMinutes(companySettings.IdleThresholdMinutes);
                                var sysIdleDuration = BIManage.Revit.BackgroundSync.RevitInteropHelper.GetTimeSinceLastInput();
                                if (sysIdleDuration >= companyIdleThreshold)
                                {
                                    if (_idleStartTime == null)
                                    {
                                        // Anchor to when idle actually started, but never before
                                        // this Revit session began (_firstIdlingObservedAt).
                                        // This prevents pre-session system idle (e.g. the user
                                        // left the PC idle, then launched Revit) from inflating
                                        // the durationSeconds on the very first tick.
                                        var rawStart = DateTime.UtcNow - sysIdleDuration;
                                        var sessionFloor = _firstIdlingObservedAt != DateTime.MinValue
                                            ? _firstIdlingObservedAt
                                            : DateTime.UtcNow;
                                        _idleStartTime = rawStart < sessionFloor ? sessionFloor : rawStart;
                                    }

                                    var durationSeconds = (int)(DateTime.UtcNow - _idleStartTime.Value).TotalSeconds;
                                    await _sessionSyncService.SendIdleTimeViaSignalRAsync(sessionId, durationSeconds);
                                }
                                else
                                {
                                    // User is active again — reset the accumulator.
                                    _idleStartTime = null;
                                }
                            }
                        }
                        catch (Exception idleTrackEx)
                        {
                            _logger?.LogDebug($"Company idle tracking (non-critical): {idleTrackEx.Message}");
                        }
                    }

                    // 3. Crashed/unknown session detection (process dead but session still Active)
                    try
                    {
                        var crashed = await _sessionRepository.DetectCrashedSessionsAsync(heartbeatTimeoutMinutes: 5);
                        if (crashed.Count > 0)
                            _logger?.LogInfo($"Detected {crashed.Count} crashed/unknown session(s) during periodic check");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug($"Crashed session detection failed: {ex.Message}");
                    }

                    // 4. Inactive session detection (heartbeat silent for 4+ hours)
                    try
                    {
                        await _sessionRepository.DetectInactiveSessionsAsync(inactiveTimeoutHours: 4);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning($"Inactive session detection failed: {ex.Message}");
                    }

                    // 5. Mark stale syncs as failed (started but never completed within 5 minutes)
                    try
                    {
                        if (_syncRepository != null)
                            await _syncRepository.MarkStaleSyncsAsFailedAsync(timeoutMinutes: 5);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug($"Stale sync detection failed: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in ExecutePeriodicHeartbeat: {ex.Message}", ex);
            }
        }

        private DateTime _lastAlcHealthLogTime = DateTime.MinValue;
        private static readonly TimeSpan AlcHealthLogInterval = TimeSpan.FromMinutes(5);

        // [Fix-1a diagnostic] Once every 5 minutes, log the live-ALC identity and the
        // currently-running thread's ALC. If Application.Instance ever becomes null or
        // changes its assembly hash mid-session, this catches *when* it happened —
        // pinpointing the event sequence that orphaned BIManage's subscriptions.
        // Pure logging — no behavior change.
        private void LogPeriodicLiveAlcHealth()
        {
            try
            {
                var now = DateTime.UtcNow;
                if (now - _lastAlcHealthLogTime < AlcHealthLogInterval) return;
                _lastAlcHealthLogTime = now;

                string thisAlc = "n/a";
                int thisAsmHash = 0;
                int liveAsmHash = 0;
                bool liveInstanceNonNull = false;
                bool liveServicesNonNull = false;
#if NET8_0_OR_GREATER
                var thisAsm = typeof(IdlingService).Assembly;
                var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(thisAsm);
                thisAlc = alc?.Name ?? "Default";
                thisAsmHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(thisAsm);
                var liveApp = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                if (liveApp != null)
                {
                    liveInstanceNonNull = true;
                    liveAsmHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(liveApp.GetType().Assembly);
                    liveServicesNonNull = liveApp.Services != null;
                }
#endif
                bool fromLiveAlc = (thisAsmHash != 0 && thisAsmHash == liveAsmHash);
                // ALC-health heartbeat from the cross-ALC debugging campaign — kept as DEBUG.
                _logger?.LogDebug(
                    $"[SubDiag-Health] tick ALC={thisAlc} thisAsm={thisAsmHash} liveAsm={liveAsmHash} liveInstance={liveInstanceNonNull} liveServices={liveServicesNonNull} fromLiveAlc={fromLiveAlc}");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[SubDiag-Health] log failed: {ex.Message}");
            }
        }
    }
}
