using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.DependencyInjection;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Context;
using BIManage.Revit.Helpers;
using BIManage.Revit.Protection;
using BIManageRevit.BIManage.Views.Metrics;

namespace BIManage.Core.Metrics
{
    /// <summary>
    /// Collects all 3 metric tiers (fast + medium + expensive) across multiple Idling callbacks
    /// so the Revit UI stays responsive. Each tick processes a small chunk, then yields.
    ///
    /// Phase order: Fast → Medium → Purgeable → SaveAndSync →
    ///              ConfirmFamilyScan → FamilySize (chunked) → FinalSave → Done
    ///
    /// Family size scanning is the last and slowest phase. The user is prompted to confirm
    /// before it starts, and can cancel mid-way. Results without family data are still saved.
    /// </summary>
    public class ChunkedMetricsAnalyzer
    {
        private readonly UIApplication _uiApp;
        private readonly ServiceRegistry _services;
        private readonly ILogger? _logger;
        private readonly AnalyzeModelDialog _dialog;
        private readonly Document _doc;

        // Collection state
        private AnalysisPhase _phase = AnalysisPhase.CollectFast;
        private FastMetrics? _fastMetrics;
        private MediumMetrics? _mediumMetrics;
        private ExpensiveMetrics _expensiveMetrics = new ExpensiveMetrics();

        // Family size scanning (last, optional, cancellable)
        private List<Family>? _families;
        private int _familyIndex;
        // One family per tick keeps Cancel responsive: the _cancelled flag is checked
        // at the top of every OnIdling tick, so worst-case cancel latency is the time
        // of a single EditFamily/SaveAs (which Revit's API can't interrupt mid-call).
        private const int FamiliesPerTick = 1;
        private long _familySizeThresholdBytes = 5L * 1024 * 1024;
        private bool _familyScanConfirmed;

        // Dialog suppression during EditFamily — Revit pops "Constraints between geometry…"
        // and similar modal warnings when opening certain families; auto-cancel so the
        // analyzer doesn't block on a dialog the user can't reach.
        private EventHandler<DialogBoxShowingEventArgs>? _dialogHandler;

        // Purgeable types chunking
        private static readonly Type[] PurgeableTypes =
        {
            typeof(FamilySymbol), typeof(WallType), typeof(FloorType),
            typeof(RoofType), typeof(TextNoteType), typeof(DimensionType),
            typeof(ViewFamilyType), typeof(LinePatternElement), typeof(FillPatternElement)
        };
        private static readonly string[] PurgeableTypeNames =
        {
            "Family Symbols", "Walls", "Floors", "Roofs", "Text Notes",
            "Dimensions", "View Types", "Line Patterns", "Fill Patterns"
        };
        private int _purgeableTypeIndex;

        // Context
        private string _sessionId = "";
        private string _documentId = "";
        private string _modelGuid = "";
        private string _modelPath = "";
        private string _modelName = "";
        private string _capturedBy = "";

        private bool _isRunning;
        private volatile bool _cancelled;

        // Suppression handlers active only during the family SaveAs loop. EditFamily + SaveAs
        // on a family with pre-existing constraint warnings ("Constraints between geometry in
        // the family can behave unpredictably...") pops Revit's modal FailuresDialog once per
        // family, blocking the whole scan. Subscribed in ResumeFamilyScan(true), torn down in
        // CleanupFamilyTempDir / Cleanup.
        private bool _failureHandlersAttached;
        private EventHandler<Autodesk.Revit.DB.Events.FailuresProcessingEventArgs>? _failuresProcessingHandler;
        private EventHandler<Autodesk.Revit.UI.Events.DialogBoxShowingEventArgs>? _dialogBoxShowingHandler;

        // Saved capture IDs from the initial save (before family scan)
        private string? _savedFastCaptureId;
        private string? _savedMediumCaptureId;
        private string _savedExpensiveCaptureId = "";

        private enum AnalysisPhase
        {
            CollectFast,
            CollectMedium,
            PrepareExpensive,
            ExpensivePurgeable,
            SaveAndSync,
            ConfirmFamilyScan,
            WaitingForConfirmation,
            FamilySizeScanning,
            FinalSave,
            Done
        }

        public ChunkedMetricsAnalyzer(
            UIApplication uiApp,
            ServiceRegistry services,
            AnalyzeModelDialog dialog,
            Document doc,
            ILogger? logger)
        {
            _uiApp = uiApp ?? throw new ArgumentNullException(nameof(uiApp));
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _logger = logger;
        }

        /// <summary>Start the chunked analysis by subscribing to the Idling event.</summary>
        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            // Wire up cancel routing so the dialog's persistent Cancel button can call
            // back into Cancel() even during phases before ShowFamilyScanConfirmation/
            // ShowFamilyScanProgress (which set the analyzer reference internally).
            _dialog.AttachAnalyzer(this);

            // Resolve context once
            var revitContext = _services.GetService<IRevitContext>();
            _sessionId = revitContext?.SessionId.ToString() ?? Guid.NewGuid().ToString();
            _modelGuid = ModelGuidHelper.GetModelGuid(_doc);
            _modelPath = DocumentInformationHelper.GetNormalizedPath(_doc);
            _modelName = _doc.Title;
            _documentId = _doc.GetHashCode().ToString();
            _capturedBy = Environment.UserName;

            // Try to get Revit username from session
            var sessionRepo = _services.GetService<SessionRepository>();
            if (sessionRepo != null)
            {
                try
                {
                    var session = Task.Run(() => sessionRepo.GetSessionAsync(_sessionId))
                        .GetAwaiter().GetResult();
                    if (session != null && !string.IsNullOrEmpty(session.RevitUsername))
                        _capturedBy = session.RevitUsername;
                }
                catch { }
            }

            _phase = AnalysisPhase.CollectFast;
            _uiApp.Idling += OnIdling;

            _logger?.LogInfo("ChunkedMetricsAnalyzer: started (Idling event subscribed)");
        }

        /// <summary>
        /// Cancel the analysis. Safe to call from any thread (e.g., dialog Cancel button).
        /// If family scanning is in progress, it stops after the current chunk.
        /// Results collected so far are preserved.
        /// </summary>
        public void Cancel()
        {
            _cancelled = true;
            _logger?.LogInfo("ChunkedMetricsAnalyzer: cancellation requested by user");
        }

        /// <summary>
        /// Called by the dialog when the user responds to the family scan confirmation.
        /// </summary>
        public void OnFamilyScanConfirmation(bool confirmed)
        {
            _familyScanConfirmed = confirmed;
            if (!confirmed)
                _logger?.LogInfo("ChunkedMetricsAnalyzer: user declined family size scan");
        }

        /// <summary>Called each time Revit becomes idle. Processes one chunk then returns.</summary>
        private void OnIdling(object sender, IdlingEventArgs e)
        {
            try
            {
                // Check for cancellation
                if (_cancelled)
                {
                    _logger?.LogInfo($"ChunkedMetricsAnalyzer: cancelled in phase {_phase}");
                    CleanupFamilyTempDir();

                    // Show results with what we have so far
                    _dialog.ShowAllResults(
                        _modelName, _fastMetrics, _mediumMetrics, _expensiveMetrics,
                        _savedExpensiveCaptureId);

                    Cleanup();
                    return;
                }

                // Request another callback so we keep getting ticks
                e.SetRaiseWithoutDelay();

                switch (_phase)
                {
                    case AnalysisPhase.CollectFast:
                        RunCollectFast();
                        break;

                    case AnalysisPhase.CollectMedium:
                        RunCollectMedium();
                        break;

                    case AnalysisPhase.PrepareExpensive:
                        RunPrepareExpensive();
                        break;

                    case AnalysisPhase.ExpensivePurgeable:
                        RunExpensivePurgeableChunk();
                        break;

                    case AnalysisPhase.SaveAndSync:
                        RunSaveAndSync();
                        break;

                    case AnalysisPhase.ConfirmFamilyScan:
                        RunConfirmFamilyScan();
                        break;

                    case AnalysisPhase.WaitingForConfirmation:
                        // Do nothing — wait for OnFamilyScanConfirmation callback
                        break;

                    case AnalysisPhase.FamilySizeScanning:
                        RunFamilySizeScanChunk();
                        break;

                    case AnalysisPhase.FinalSave:
                        RunFinalSave();
                        break;

                    case AnalysisPhase.Done:
                        Cleanup();
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"ChunkedMetricsAnalyzer error in phase {_phase}: {ex.Message}", ex);
                _dialog.ShowError(ex.Message);
                Cleanup();
            }
        }

        // ── Phase: Fast Metrics (one tick) ──────────────────────────────────────

        private void RunCollectFast()
        {
            _dialog.UpdateProgress("Collecting model information...", 2);

            var collector = _services.GetService<ModelFileMetricsCollectorService>();
            _fastMetrics = collector?.CollectFastMetrics(_doc) ?? new FastMetrics();

            _logger?.LogInfo("ChunkedMetricsAnalyzer: fast metrics collected");
            _phase = AnalysisPhase.CollectMedium;
        }

        // ── Phase: Medium Metrics (one tick) ────────────────────────────────────

        private void RunCollectMedium()
        {
            _dialog.UpdateProgress("Collecting element statistics...", 10);

            var collector = _services.GetService<ModelFileMetricsCollectorService>();
            _mediumMetrics = collector?.CollectMediumMetrics(_doc) ?? new MediumMetrics();

            _logger?.LogInfo("ChunkedMetricsAnalyzer: medium metrics collected");
            _phase = AnalysisPhase.PrepareExpensive;
        }

        // ── Phase: Prepare Expensive (one tick) ─────────────────────────────────

        private void RunPrepareExpensive()
        {
            _dialog.UpdateProgress("Preparing analysis...", 18);

            _expensiveMetrics.FamiliesOver5MbCount = 0;
            _expensiveMetrics.PurgeableElementsCount = 0;

            // Read threshold from health monitor settings if available
            try
            {
                var metricsService = _services?.GetService<ModelFileMetricsCollectorService>();
                if (metricsService != null)
                    _familySizeThresholdBytes = metricsService.GetFamilySizeThresholdBytes();
            }
            catch { }

            _purgeableTypeIndex = 0;

            _phase = AnalysisPhase.ExpensivePurgeable;
        }

        // ── Phase: Purgeable Types (one type category per tick) ─────────────────

        private void RunExpensivePurgeableChunk()
        {
            if (_purgeableTypeIndex >= PurgeableTypes.Length)
            {
                _phase = AnalysisPhase.SaveAndSync;
                return;
            }

            var typeName = PurgeableTypeNames[_purgeableTypeIndex];
            int pct = 20 + (int)(50.0 * _purgeableTypeIndex / PurgeableTypes.Length);
            _dialog.UpdateProgress($"Checking {typeName}...", pct);

            try
            {
                var types = new FilteredElementCollector(_doc)
                    .OfClass(PurgeableTypes[_purgeableTypeIndex])
                    .Cast<ElementType>()
                    .ToList();

                foreach (var type in types)
                {
                    try
                    {
                        var instances = new FilteredElementCollector(_doc)
                            .WhereElementIsNotElementType()
                            .Where(e => e.GetTypeId() == type.Id)
                            .ToList();

                        if (instances.Count == 0)
                            _expensiveMetrics.PurgeableElementsCount++;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Purgeable check failed for {typeName}: {ex.Message}");
            }

            _purgeableTypeIndex++;

            if (_purgeableTypeIndex >= PurgeableTypes.Length)
                _phase = AnalysisPhase.SaveAndSync;
        }

        // ── Phase: Save to DB + API Sync (initial — without family sizes) ──────

        private void RunSaveAndSync()
        {
            _dialog.UpdateProgress("Saving metrics...", 75);

            var metricsRepo = _services.GetService<ModelFileMetricsRepository>();
            if (metricsRepo == null)
            {
                _dialog.ShowError("Metrics repository not available");
                _phase = AnalysisPhase.Done;
                return;
            }

            try
            {
                // Save fast metrics
                if (_fastMetrics != null)
                {
                    _savedFastCaptureId = Task.Run(() => metricsRepo.InsertFastMetricsAsync(
                        sessionId: _sessionId, documentId: _documentId,
                        modelGuid: _modelGuid, modelPath: _modelPath,
                        modelName: _modelName, capturedBy: _capturedBy,
                        metrics: _fastMetrics, captureTypeValue: "manual"))
                        .GetAwaiter().GetResult();
                }

                // Save medium metrics
                if (_mediumMetrics != null)
                {
                    _savedMediumCaptureId = Task.Run(() => metricsRepo.InsertMediumMetricsAsync(
                        sessionId: _sessionId, documentId: _documentId,
                        modelGuid: _modelGuid, modelPath: _modelPath,
                        modelName: _modelName, capturedBy: _capturedBy,
                        metrics: _mediumMetrics, isManualTrigger: true))
                        .GetAwaiter().GetResult();
                }

                // Save expensive metrics (purgeable count, family count = 0 for now)
                _savedExpensiveCaptureId = Task.Run(() => metricsRepo.InsertExpensiveMetricsAsync(
                    sessionId: _sessionId, documentId: _documentId,
                    modelGuid: _modelGuid, modelPath: _modelPath,
                    modelName: _modelName, capturedBy: _capturedBy,
                    metrics: _expensiveMetrics, commandSource: "ribbon",
                    captureReason: "User-initiated analysis"))
                    .GetAwaiter().GetResult();

                _logger?.LogInfo($"ChunkedMetricsAnalyzer: initial save complete (fast={_savedFastCaptureId}, medium={_savedMediumCaptureId}, expensive={_savedExpensiveCaptureId})");

                // Fire-and-forget API sync
                SyncAllToApi(_savedFastCaptureId, _savedMediumCaptureId, _savedExpensiveCaptureId);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"ChunkedMetricsAnalyzer save failed: {ex.Message}", ex);
            }

            _phase = AnalysisPhase.ConfirmFamilyScan;
        }

        // ── Phase: Confirm Family Scan (prompt user) ───────────────────────────

        private void RunConfirmFamilyScan()
        {
            // Skip family scanning on read-only documents
            if (_doc.IsReadOnly)
            {
                _logger?.LogInfo("ChunkedMetricsAnalyzer: document is read-only — skipping family size scan");
                ShowFinalResults();
                _phase = AnalysisPhase.Done;
                return;
            }

            // Count editable families to show in the prompt
            var editableFamilyCount = new FilteredElementCollector(_doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Count(f => !f.IsInPlace && f.IsEditable);

            if (editableFamilyCount == 0)
            {
                _logger?.LogInfo("ChunkedMetricsAnalyzer: no editable families — skipping family size scan");
                ShowFinalResults();
                _phase = AnalysisPhase.Done;
                return;
            }

            // Ask the dialog to show the confirmation prompt
            _familyScanConfirmed = false;
            _dialog.ShowFamilyScanConfirmation(editableFamilyCount, this);
            _phase = AnalysisPhase.WaitingForConfirmation;
        }

        /// <summary>
        /// Called by the dialog after the user responds. Resumes the analysis.
        /// </summary>
        internal void ResumeFamilyScan(bool confirmed)
        {
            _familyScanConfirmed = confirmed;

            if (!confirmed)
            {
                _logger?.LogInfo("ChunkedMetricsAnalyzer: user skipped family size scan");
                ShowFinalResults();
                _phase = AnalysisPhase.Done;
                return;
            }

            // Prepare family list
            _families = new FilteredElementCollector(_doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Where(f => !f.IsInPlace && f.IsEditable)
                .ToList();

            _familyIndex = 0;

            _logger?.LogInfo($"ChunkedMetricsAnalyzer: starting family size scan ({_families.Count} families, threshold: {_familySizeThresholdBytes / (1024.0 * 1024.0):F0} MB)");

            // Suppress family warning dialogs (e.g. "Constraints between geometry...") raised
            // during EditFamily+SaveAs. Must attach BEFORE the first RunFamilySizeScanChunk tick.
            AttachFailureHandlers();

            // Show cancellable progress in dialog
            _dialog.ShowFamilyScanProgress(_families.Count, this);

            AttachDialogSuppression();
            _phase = AnalysisPhase.FamilySizeScanning;
        }

        // ── Phase: Family Size Scanning (chunked, cancellable) ─────────────────

        private void RunFamilySizeScanChunk()
        {
            if (_families == null || _familyIndex >= _families.Count)
            {
                CleanupFamilyTempDir();
                _phase = AnalysisPhase.FinalSave;
                return;
            }

            var end = Math.Min(_familyIndex + FamiliesPerTick, _families.Count);

            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BIManage_FamilySize");
            if (!System.IO.Directory.Exists(tempDir))
                System.IO.Directory.CreateDirectory(tempDir);

            for (int i = _familyIndex; i < end; i++)
            {
                if (_cancelled) break;

                try
                {
                    var family = _families[i];
                    var familyDoc = _doc.EditFamily(family);
                    if (familyDoc != null)
                    {
                        try
                        {
                            var tempPath = System.IO.Path.Combine(tempDir, $"{Guid.NewGuid()}.rfa");
                            var saveOpts = new SaveAsOptions { OverwriteExistingFile = true, Compact = false };
                            using (EventProtectionSuppression.BeginScope())
                            {
                                familyDoc.SaveAs(tempPath, saveOpts);
                            }

                            var fileSize = new System.IO.FileInfo(tempPath).Length;
                            if (fileSize > _familySizeThresholdBytes)
                            {
                                _expensiveMetrics.FamiliesOver5MbCount++;
                                _logger?.LogDebug($"Large family: {family.Name} ({fileSize / (1024.0 * 1024.0):F1} MB)");
                            }

                            try { System.IO.File.Delete(tempPath); } catch { }
                        }
                        finally
                        {
                            familyDoc.Close(false);
                        }
                    }
                }
                catch { }
            }

            _familyIndex = end;

            // Update dialog progress
            int pct = (int)(100.0 * _familyIndex / _families.Count);
            _dialog.UpdateFamilyScanProgress(_familyIndex, _families.Count, pct);

            if (_familyIndex >= _families.Count)
            {
                CleanupFamilyTempDir();
                _phase = AnalysisPhase.FinalSave;
            }
        }

        // ── Phase: Final Save (update expensive metrics with family count) ─────

        private void RunFinalSave()
        {
            try
            {
                // Update the expensive metrics record with the family count
                var metricsRepo = _services.GetService<ModelFileMetricsRepository>();
                if (metricsRepo != null && !string.IsNullOrEmpty(_savedExpensiveCaptureId))
                {
                    Task.Run(() => metricsRepo.UpdateExpensiveMetricsFamilyCountAsync(
                        _savedExpensiveCaptureId,
                        _expensiveMetrics.FamiliesOver5MbCount ?? 0))
                        .GetAwaiter().GetResult();

                    _logger?.LogInfo($"ChunkedMetricsAnalyzer: updated family count to {_expensiveMetrics.FamiliesOver5MbCount} (captureId: {_savedExpensiveCaptureId})");

                    // Re-sync expensive metrics to API with updated family count
                    SyncExpensiveToApi(_savedExpensiveCaptureId);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"ChunkedMetricsAnalyzer: failed to update family count: {ex.Message}");
            }

            ShowFinalResults();
            _phase = AnalysisPhase.Done;
        }

        private void ShowFinalResults()
        {
            _dialog.ShowAllResults(
                _modelName, _fastMetrics, _mediumMetrics, _expensiveMetrics,
                _savedExpensiveCaptureId);
        }

        // ── API Sync helpers ───────────────────────────────────────────────────

        private void SyncAllToApi(string? fastCaptureId, string? mediumCaptureId, string expensiveCaptureId)
        {
            var syncService = _services.GetService<MetricsSyncService>();
            if (syncService == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (fastCaptureId != null && _fastMetrics != null)
                    {
                        await syncService.SyncSyncSaveMetricsAsync(new SyncSaveMetricsApiRequest
                        {
                            CaptureId = fastCaptureId,
                            SessionId = _sessionId,
                            DocumentId = _documentId,
                            ModelGuid = _modelGuid,
                            ModelPath = _modelPath,
                            ModelName = _modelName,
                            CaptureType = "manual",
                            CapturedAt = DateTime.UtcNow,
                            CapturedBy = _capturedBy,
                            FileSizeBytes = _fastMetrics.FileSizeBytes ?? 0,
                            LevelsCount = _fastMetrics.LevelsCount ?? 0,
                            GridsCount = _fastMetrics.GridsCount ?? 0,
                            DesignOptionsCount = _fastMetrics.DesignOptionsCount ?? 0,
                            LinkedDwgCount = _fastMetrics.LinkedDwgCount ?? 0,
                            ImportedDwgCount = _fastMetrics.ImportedDwgCount ?? 0,
                            LinkedRevitCount = _fastMetrics.LinkedRevitCount ?? 0,
                            RasterImagesCount = _fastMetrics.RasterImagesCount ?? 0,
                            WarningsCount = _fastMetrics.WarningsCount ?? 0,
                            DuplicateElementsCount = _fastMetrics.DuplicateElementsCount ?? 0,
                            ModelGroupsCount = _fastMetrics.ModelGroupsCount ?? 0,
                            DetailGroupsCount = _fastMetrics.DetailGroupsCount ?? 0,
                            TotalViewsCount = _fastMetrics.TotalViewsCount ?? 0,
                            SheetsCount = _fastMetrics.SheetsCount ?? 0,
                            TotalFamiliesCount = _fastMetrics.TotalFamiliesCount ?? 0,
                            TotalWorksetsCount = _fastMetrics.TotalWorksetsCount,
                            NonNativeObjectStylesCount = _fastMetrics.NonNativeObjectStylesCount,
                            ViewTemplatesCount = _fastMetrics.ViewTemplatesCount,
                            SharedCoordNs = _fastMetrics.SharedCoordNs,
                            SharedCoordEw = _fastMetrics.SharedCoordEw,
                            SharedCoordElevation = _fastMetrics.SharedCoordElevation,
                            SharedCoordUnit = _fastMetrics.SharedCoordUnit,
                            CreatedAt = DateTime.UtcNow
                        });
                    }

                    if (mediumCaptureId != null && _mediumMetrics != null)
                    {
                        await syncService.SyncPeriodicMetricsAsync(new PeriodicMetricsApiRequest
                        {
                            CaptureId = mediumCaptureId,
                            SessionId = _sessionId,
                            DocumentId = _documentId,
                            ModelGuid = _modelGuid,
                            ModelPath = _modelPath,
                            ModelName = _modelName,
                            CapturedAt = DateTime.UtcNow,
                            CapturedBy = _capturedBy,
                            IsManualTrigger = true,
                            CaptureIntervalHours = 0,
                            TotalElementsCount = _mediumMetrics.TotalElementsCount ?? 0,
                            ModelElementsCount = _mediumMetrics.ModelElementsCount ?? 0,
                            AnnotativeElementsCount = _mediumMetrics.AnnotativeElementsCount ?? 0,
                            InplaceFamiliesCount = _mediumMetrics.InplaceFamiliesCount ?? 0,
                            UnplacedRoomsCount = _mediumMetrics.UnplacedRoomsCount ?? 0,
                            ViewsNotOnSheetsCount = _mediumMetrics.ViewsNotOnSheetsCount ?? 0,
                            UnenclosedRoomsCount = _mediumMetrics.UnenclosedRoomsCount ?? 0,
                            WallsNotConnectedCount = _mediumMetrics.WallsNotConnectedCount ?? 0,
                            PipesNotConnectedCount = _mediumMetrics.PipesNotConnectedCount ?? 0,
                            DuctsNotConnectedCount = _mediumMetrics.DuctsNotConnectedCount ?? 0,
                            CreatedAt = DateTime.UtcNow
                        });
                    }

                    await syncService.SyncManualMetricsAsync(new ManualMetricsApiRequest
                    {
                        CaptureId = expensiveCaptureId,
                        SessionId = _sessionId,
                        DocumentId = _documentId,
                        ModelGuid = _modelGuid,
                        ModelPath = _modelPath,
                        ModelName = _modelName,
                        CapturedAt = DateTime.UtcNow,
                        CapturedBy = _capturedBy,
                        CommandSource = "ribbon",
                        CaptureReason = "User-initiated analysis",
                        FamiliesOver5mbCount = _expensiveMetrics.FamiliesOver5MbCount ?? 0,
                        PurgeableElementsCount = _expensiveMetrics.PurgeableElementsCount ?? 0,
                        CreatedAt = DateTime.UtcNow
                    });

                    _logger?.LogDebug("ChunkedMetricsAnalyzer: all tiers synced to API");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"ChunkedMetricsAnalyzer API sync failed (non-critical): {ex.Message}");
                }
            });
        }

        private void SyncExpensiveToApi(string captureId)
        {
            var syncService = _services.GetService<MetricsSyncService>();
            if (syncService == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await syncService.SyncManualMetricsAsync(new ManualMetricsApiRequest
                    {
                        CaptureId = captureId,
                        SessionId = _sessionId,
                        DocumentId = _documentId,
                        ModelGuid = _modelGuid,
                        ModelPath = _modelPath,
                        ModelName = _modelName,
                        CapturedAt = DateTime.UtcNow,
                        CapturedBy = _capturedBy,
                        CommandSource = "ribbon",
                        CaptureReason = "User-initiated analysis (family scan update)",
                        FamiliesOver5mbCount = _expensiveMetrics.FamiliesOver5MbCount ?? 0,
                        PurgeableElementsCount = _expensiveMetrics.PurgeableElementsCount ?? 0,
                        CreatedAt = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"ChunkedMetricsAnalyzer: family count API sync failed: {ex.Message}");
                }
            });
        }

        // ── Cleanup helpers ────────────────────────────────────────────────────

        private void CleanupFamilyTempDir()
        {
            DetachDialogSuppression();
            DetachFailureHandlers();
            try
            {
                var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BIManage_FamilySize");
                if (System.IO.Directory.Exists(tempDir))
                    System.IO.Directory.Delete(tempDir, true);
            }
            catch { }
        }

        private void Cleanup()
        {
            DetachDialogSuppression();
            DetachFailureHandlers();
            _uiApp.Idling -= OnIdling;
            _isRunning = false;
            _families = null;
            _logger?.LogInfo("ChunkedMetricsAnalyzer: cleanup done (Idling event unsubscribed)");
        }

        private void AttachDialogSuppression()
        {
            if (_dialogHandler != null) return;
            // DialogBoxShowing subscription requires a Revit API context that isn't always
            // available from inside Idling-driven phase transitions; catch the
            // InvalidOperationException and degrade gracefully — the user will see the
            // dialog and have to dismiss it once, but the analyzer won't crash.
            var handler = new EventHandler<DialogBoxShowingEventArgs>(OnDialogShowingDuringFamilyScan);
            try
            {
                _uiApp.DialogBoxShowing += handler;
                _dialogHandler = handler;
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                _logger?.LogWarning($"[FamilyScan] Could not attach dialog suppression (no API context): {ex.Message}");
                _dialogHandler = null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[FamilyScan] Could not attach dialog suppression: {ex.Message}");
                _dialogHandler = null;
            }
        }

        private void DetachDialogSuppression()
        {
            if (_dialogHandler == null) return;
            try { _uiApp.DialogBoxShowing -= _dialogHandler; }
            catch { /* same API-context restriction can apply on detach */ }
            _dialogHandler = null;
        }

        // During EditFamily, Revit can pop the "Constraints between geometry in the family
        // can behave unpredictably…" warning (and similar modal messages). The analyzer is
        // running on a background-style chunked loop and the user has no way to dismiss
        // these dialogs without breaking the scan, so override the result to cancel and
        // skip that family. EditFamily then returns null and the existing null-check at
        // each call site silently advances to the next family.
        private void OnDialogShowingDuringFamilyScan(object sender, DialogBoxShowingEventArgs e)
        {
            try
            {
                // 2 == IDCANCEL for legacy modal dialogs; for TaskDialogs this maps to the
                // "common-cancel" close behaviour. Either way the family is skipped.
                e.OverrideResult(2);
                _logger?.LogDebug($"[FamilyScan] Suppressed dialog: {e.DialogId}");
            }
            catch { /* best-effort suppression */ }
        }

        // ── Family-warning suppression (active only during family SaveAs loop) ────

        private void AttachFailureHandlers()
        {
            if (_failureHandlersAttached) return;

            _failuresProcessingHandler = OnFailuresProcessing;
            _dialogBoxShowingHandler = OnDialogBoxShowing;

            try { _uiApp.Application.FailuresProcessing += _failuresProcessingHandler; } catch { }
            try { _uiApp.DialogBoxShowing += _dialogBoxShowingHandler; } catch { }

            _failureHandlersAttached = true;
            _logger?.LogDebug("Family scan: warning suppression attached");
        }

        private void DetachFailureHandlers()
        {
            if (!_failureHandlersAttached) return;

            try { if (_failuresProcessingHandler != null) _uiApp.Application.FailuresProcessing -= _failuresProcessingHandler; } catch { }
            try { if (_dialogBoxShowingHandler != null) _uiApp.DialogBoxShowing -= _dialogBoxShowingHandler; } catch { }

            _failuresProcessingHandler = null;
            _dialogBoxShowingHandler = null;
            _failureHandlersAttached = false;
            _logger?.LogDebug("Family scan: warning suppression detached");
        }

        /// <summary>
        /// Closes Revit's FailuresDialog during the family-scan window without mutating any
        /// document. We deliberately do NOT call DeleteWarning or ResolveFailure:
        ///   1) DeleteWarning permanently strips the message from that document's
        ///      Review-Warnings list — we never want the analyzer to silently
        ///      modify a real project's warning collection.
        ///   2) This handler is attached at the application level, so it would
        ///      fire for the host project too if anything triggers FailuresProcessing
        ///      on it (autosave, background regen, sync) during the scan.
        /// If unresolved errors block the temp-family SaveAs, the surrounding
        /// try/catch in RunFamilySizeScanChunk skips that family.
        /// </summary>
        private void OnFailuresProcessing(object? sender, Autodesk.Revit.DB.Events.FailuresProcessingEventArgs e)
        {
            try
            {
                // ProceedWithRollBack (not Continue): Continue lets Revit still display
                // the FailuresDialog for unresolved ERRORS (e.g. "Constraints are not
                // satisfied", "Extrusion is too thin") which then blocks the UI thread
                // for tens of seconds — also blocking the analyzer's Cancel button.
                // RollBack tells Revit to abandon the family's transaction silently:
                // no dialog, no document mutation, SaveAs throws → caught by the per-
                // family try/catch in RunFamilySizeScanChunk → next family.
                e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
            }
            catch { /* never throw from a Revit event handler */ }
        }

        /// <summary>
        /// Fallback for any modal dialog (TaskDialog or legacy) that bypasses the failures pipeline.
        /// OverrideResult(1) = OK / first button — safe because the SaveAs writes to a throwaway
        /// temp file, never to the original family.
        /// </summary>
        private void OnDialogBoxShowing(object? sender, Autodesk.Revit.UI.Events.DialogBoxShowingEventArgs e)
        {
            try { e.OverrideResult(1); } catch { }
        }
    }
}
