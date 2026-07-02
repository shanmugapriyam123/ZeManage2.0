using System;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Context;
using BIManage.Revit.Helpers;

namespace BIManage.Core.Metrics
{
    /// <summary>
    /// Service for collecting periodic model file metrics snapshots
    /// Runs on a configurable schedule (default: daily at 2 AM local time)
    /// Collects fast + medium metrics and syncs to API
    /// </summary>
    public class PeriodicMetricsSnapshotService : IDisposable
    {
        private readonly ILogger? _logger;
        private readonly ModelFileMetricsRepository? _metricsRepository;
        private readonly ModelFileMetricsCollectorService? _metricsCollector;
        private readonly SessionRepository? _sessionRepository;
        private readonly IRevitContext? _revitContext;
        private readonly UIApplication? _uiApp;
        private readonly Func<MetricsSyncService?>? _metricsSyncGetter;
        private Timer? _timer;
        private bool _disposed;

        // Configuration
        private readonly TimeSpan _interval;
        private readonly TimeSpan _startTime;

        /// <summary>
        /// Creates a new periodic metrics snapshot service
        /// </summary>
        /// <param name="intervalHours">Interval in hours between snapshots (default: 24 for daily)</param>
        /// <param name="startHourLocal">Local hour to start snapshots (default: 2 AM local time)</param>
        public PeriodicMetricsSnapshotService(
            ILogger? logger,
            ModelFileMetricsRepository? metricsRepository,
            ModelFileMetricsCollectorService? metricsCollector,
            SessionRepository? sessionRepository,
            IRevitContext? revitContext,
            UIApplication? uiApp,
            Func<MetricsSyncService?>? metricsSyncGetter = null,
            int intervalHours = 24,
            int startHourLocal = 2)
        {
            _logger = logger;
            _metricsRepository = metricsRepository;
            _metricsCollector = metricsCollector;
            _sessionRepository = sessionRepository;
            _revitContext = revitContext;
            _uiApp = uiApp;
            _metricsSyncGetter = metricsSyncGetter;

            _interval = TimeSpan.FromHours(intervalHours);
            _startTime = TimeSpan.FromHours(startHourLocal);
        }

        /// <summary>
        /// Start the periodic snapshot service
        /// </summary>
        public void Start()
        {
            if (_metricsRepository == null || _metricsCollector == null)
            {
                _logger?.LogWarning("Periodic metrics service not started - missing dependencies");
                return;
            }

            try
            {
                // Calculate time until next scheduled snapshot (using local time)
                var now = DateTime.Now;
                var today = now.Date;
                var nextRun = today + _startTime;

                // If we've already passed today's scheduled time, schedule for tomorrow
                if (now > nextRun)
                {
                    nextRun = nextRun.AddDays(1);
                }

                var dueTime = nextRun - now;

                _logger?.LogInfo($"Periodic metrics service starting - first snapshot in {dueTime.TotalHours:F1} hours at {nextRun:yyyy-MM-dd HH:mm} local time");

                // Create timer with initial due time and periodic interval
                _timer = new Timer(
                    callback: OnTimerElapsed,
                    state: null,
                    dueTime: dueTime,
                    period: _interval);

                _logger?.LogInfo($"Periodic metrics service started (interval: {_interval.TotalHours}h, start time: {_startTime.Hours:00}:00 local)");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to start periodic metrics service: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Stop the periodic snapshot service
        /// </summary>
        public void Stop()
        {
            try
            {
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                    _logger?.LogInfo("Periodic metrics service stopped");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error stopping periodic metrics service: {ex.Message}", ex);
            }
        }

        private void OnTimerElapsed(object? state)
        {
            try
            {
                _logger?.LogInfo("Periodic metrics snapshot triggered");

                // Check if Revit is in a valid state for metric collection
                if (_uiApp == null)
                {
                    _logger?.LogWarning("UIApplication not available - skipping periodic snapshot");
                    return;
                }

                // Get active document (if any)
                var doc = _uiApp.ActiveUIDocument?.Document;
                if (doc == null || doc.IsFamilyDocument)
                {
                    _logger?.LogDebug("No active project document - skipping periodic snapshot");
                    return;
                }

                // Don't collect metrics if document is in a transaction or read-only mode
                if (doc.IsReadOnly)
                {
                    _logger?.LogDebug("Document is read-only - skipping periodic snapshot");
                    return;
                }

                _logger?.LogInfo($"Collecting periodic metrics for: {doc.Title}");

                // Get context information
                var sessionId = _revitContext?.SessionId.ToString() ?? Guid.NewGuid().ToString();
                var modelGuid = ModelGuidHelper.GetModelGuid(doc);
                var modelPath = DocumentInformationHelper.GetNormalizedPath(doc);
                var modelName = doc.Title;
                var documentId = doc.GetHashCode().ToString();

                // Get Revit username from session
                var capturedBy = Environment.UserName;
                if (_sessionRepository != null && _revitContext != null)
                {
                    try
                    {
                        var session = Task.Run(() => _sessionRepository.GetSessionAsync(sessionId)).GetAwaiter().GetResult();
                        if (session != null && !string.IsNullOrEmpty(session.RevitUsername))
                        {
                            capturedBy = session.RevitUsername;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning($"Failed to get Revit username from session: {ex.Message}");
                    }
                }

                // Phase 1: Collect fast metrics (file size, warnings, views, etc.)
                var fastMetrics = _metricsCollector.CollectFastMetrics(doc);
                _logger?.LogDebug("Fast metrics collected during periodic snapshot");

                var fastCaptureId = _metricsRepository.InsertFastMetricsAsync(
                    sessionId: sessionId,
                    documentId: documentId,
                    modelGuid: modelGuid,
                    modelPath: modelPath,
                    modelName: modelName,
                    capturedBy: capturedBy,
                    metrics: fastMetrics,
                    captureTypeValue: "periodic").Result;

                _logger?.LogInfo($"Fast metrics saved (capture_id: {fastCaptureId})");

                // Phase 2: Collect medium metrics (element counts, rooms, connectivity)
                var mediumMetrics = _metricsCollector.CollectMediumMetrics(doc);

                var periodicCaptureId = _metricsRepository.InsertMediumMetricsAsync(
                    sessionId: sessionId,
                    documentId: documentId,
                    modelGuid: modelGuid,
                    modelPath: modelPath,
                    modelName: modelName,
                    capturedBy: capturedBy,
                    metrics: mediumMetrics,
                    isManualTrigger: false,
                    captureIntervalHours: 24).Result;

                _logger?.LogInfo($"Periodic snapshot completed (fast: {fastCaptureId}, periodic: {periodicCaptureId})");

                // Phase 3: Sync both to API (fire-and-forget)
                SyncToApi(fastCaptureId, periodicCaptureId,
                    sessionId, documentId, modelGuid, modelPath, modelName,
                    capturedBy, fastMetrics, mediumMetrics);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error during periodic metrics snapshot: {ex.Message}", ex);
            }
        }

        private void SyncToApi(
            string fastCaptureId, string periodicCaptureId,
            string sessionId, string documentId, string modelGuid,
            string modelPath, string modelName, string capturedBy,
            FastMetrics fastMetrics, MediumMetrics mediumMetrics)
        {
            var syncService = _metricsSyncGetter?.Invoke();
            if (syncService == null) return;

            // Sync fast (sync/save) metrics
            _ = Task.Run(async () =>
            {
                try
                {
                    var request = new SyncSaveMetricsApiRequest
                    {
                        CaptureId = fastCaptureId,
                        SessionId = sessionId,
                        DocumentId = documentId,
                        ModelGuid = modelGuid,
                        ModelPath = modelPath,
                        ModelName = modelName,
                        CaptureType = "periodic",
                        CapturedAt = DateTime.UtcNow,
                        CapturedBy = capturedBy,
                        FileSizeBytes = fastMetrics.FileSizeBytes ?? 0,
                        LevelsCount = fastMetrics.LevelsCount ?? 0,
                        GridsCount = fastMetrics.GridsCount ?? 0,
                        DesignOptionsCount = fastMetrics.DesignOptionsCount ?? 0,
                        LinkedDwgCount = fastMetrics.LinkedDwgCount ?? 0,
                        ImportedDwgCount = fastMetrics.ImportedDwgCount ?? 0,
                        LinkedRevitCount = fastMetrics.LinkedRevitCount ?? 0,
                        RasterImagesCount = fastMetrics.RasterImagesCount ?? 0,
                        WarningsCount = fastMetrics.WarningsCount ?? 0,
                        DuplicateElementsCount = fastMetrics.DuplicateElementsCount ?? 0,
                        ModelGroupsCount = fastMetrics.ModelGroupsCount ?? 0,
                        DetailGroupsCount = fastMetrics.DetailGroupsCount ?? 0,
                        TotalViewsCount = fastMetrics.TotalViewsCount ?? 0,
                        SheetsCount = fastMetrics.SheetsCount ?? 0,
                        TotalFamiliesCount = fastMetrics.TotalFamiliesCount ?? 0,
                        TotalWorksetsCount = fastMetrics.TotalWorksetsCount,
                        NonNativeObjectStylesCount = fastMetrics.NonNativeObjectStylesCount,
                        ViewTemplatesCount = fastMetrics.ViewTemplatesCount,
                        SharedCoordNs = fastMetrics.SharedCoordNs,
                        SharedCoordEw = fastMetrics.SharedCoordEw,
                        SharedCoordElevation = fastMetrics.SharedCoordElevation,
                        SharedCoordUnit = fastMetrics.SharedCoordUnit,
                        CreatedAt = DateTime.UtcNow
                    };
                    await syncService.SyncSyncSaveMetricsAsync(request);
                    _logger?.LogDebug("Periodic fast metrics synced to API");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Periodic fast metrics API sync failed (non-critical): {ex.Message}");
                }
            });

            // Sync medium (periodic) metrics
            _ = Task.Run(async () =>
            {
                try
                {
                    var request = new PeriodicMetricsApiRequest
                    {
                        CaptureId = periodicCaptureId,
                        SessionId = sessionId,
                        DocumentId = documentId,
                        ModelGuid = modelGuid,
                        ModelPath = modelPath,
                        ModelName = modelName,
                        CapturedAt = DateTime.UtcNow,
                        CapturedBy = capturedBy,
                        CaptureIntervalHours = 24,
                        IsManualTrigger = false,
                        TotalElementsCount = mediumMetrics.TotalElementsCount ?? 0,
                        ModelElementsCount = mediumMetrics.ModelElementsCount ?? 0,
                        AnnotativeElementsCount = mediumMetrics.AnnotativeElementsCount ?? 0,
                        InplaceFamiliesCount = mediumMetrics.InplaceFamiliesCount ?? 0,
                        UnplacedRoomsCount = mediumMetrics.UnplacedRoomsCount ?? 0,
                        ViewsNotOnSheetsCount = mediumMetrics.ViewsNotOnSheetsCount ?? 0,
                        UnenclosedRoomsCount = mediumMetrics.UnenclosedRoomsCount ?? 0,
                        WallsNotConnectedCount = mediumMetrics.WallsNotConnectedCount ?? 0,
                        PipesNotConnectedCount = mediumMetrics.PipesNotConnectedCount ?? 0,
                        DuctsNotConnectedCount = mediumMetrics.DuctsNotConnectedCount ?? 0,
                        CreatedAt = DateTime.UtcNow
                    };
                    await syncService.SyncPeriodicMetricsAsync(request);
                    _logger?.LogDebug("Periodic medium metrics synced to API");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Periodic medium metrics API sync failed (non-critical): {ex.Message}");
                }
            });
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _disposed = true;
            }
        }
    }
}
