using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Metrics;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.DependencyInjection;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Context;
using BIManage.Revit.Helpers;
using BIManageRevit.BIManage.Views.Metrics;

namespace BIManageRevit.Commands.RibbonCommands
{
    /// <summary>
    /// Command to perform manual model metrics analysis.
    /// Runs analysis directly in the command context (foreground) with a non-modal progress dialog.
    /// Collects both expensive (manual) and medium (periodic) metrics.
    /// </summary>
    // TransactionMode.Manual (not ReadOnly): EditFamily is rejected by Revit
    // when the host doc is held in the ReadOnly-transaction state, with the
    // exact error "The document is currently in read-only state. EditFamily
    // may not be executed." Manual mode opens no ambient transaction, doesn't
    // impose that state, and this command never modifies the document anyway.
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AnalyzeModelMetricsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            if (LicenseGuard.IsBlockedByLicense(commandData)) return Result.Succeeded;

            var uiApp = commandData.Application;
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc?.Document;

            if (doc == null || doc.IsFamilyDocument)
            {
                TaskDialog.Show("Model Metrics Analysis",
                    "No active project document found.\n\n" +
                    "Please open a Revit project (.rvt) to analyze model metrics.");
                return Result.Cancelled;
            }

            var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
            var logger = app?.Logger;
            var services = app?.Services;

            if (services == null)
            {
                TaskDialog.Show("Model Metrics Analysis",
                    "Analysis service not available.\n\nPlease restart Revit and try again.");
                return Result.Failed;
            }

            // Deep analysis is purely local \u2014 it doesn't talk to the server during the run,
            // so blocking unregistered models with a warning popup was punishing the user
            // for an unrelated registration step. Just run the analysis; the registration
            // gate is enforced separately at sync/upload time.
            try
            {
                var modelGuid = ModelGuidHelper.GetModelGuid(doc);
                var modelRepo = services.GetService<RegisteredModelsRepository>();
                if (modelRepo != null && !string.IsNullOrEmpty(modelGuid))
                {
                    var regModel = Task.Run(() => modelRepo.GetModelAsync(modelGuid)).GetAwaiter().GetResult();
                    if (regModel == null)
                        logger?.LogInfo($"[AnalyzeModelMetrics] Running deep analysis on unregistered model {modelGuid}; results will live locally until the model is registered.");
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Model registration check failed: {ex.Message}");
            }

            try
            {
                // Show confirmation dialog (modal — blocks until user decides)
                var confirmDialog = new AnalyzeModelDialog(doc.Title);
                new System.Windows.Interop.WindowInteropHelper(confirmDialog) { Owner = uiApp.MainWindowHandle };
                var dialogResult = confirmDialog.ShowDialog();

                if (dialogResult != true || !confirmDialog.UserConfirmed)
                {
                    return Result.Cancelled;
                }

                // Run analysis directly in command context
                return RunAnalysis(uiApp, doc, services, logger, confirmDialog.GenerateDetailedReport);
            }
            catch (Exception ex)
            {
                logger?.LogError($"Error launching model metrics analysis: {ex.Message}", ex);
                message = $"Failed to start model metrics analysis: {ex.Message}";
                return Result.Failed;
            }
        }

        private Result RunAnalysis(UIApplication uiApp, Document doc, ServiceRegistry services, ILogger? logger, bool generateDetailedReport = false)
        {
            var metricsRepository = services.GetService<ModelFileMetricsRepository>();
            var metricsCollector = services.GetService<ModelFileMetricsCollectorService>();
            var sessionRepository = services.GetService<SessionRepository>();
            var revitContext = services.GetService<IRevitContext>();

            if (metricsRepository == null || metricsCollector == null)
            {
                TaskDialog.Show("Model Metrics Analysis",
                    "Metrics services not available.\n\nPlease restart Revit and try again.");
                return Result.Failed;
            }

            // Show non-modal progress dialog (foreground)
            var progressDialog = new AnalyzeModelDialog(doc.Title, startInLoadingMode: true);
            new System.Windows.Interop.WindowInteropHelper(progressDialog) { Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle };
            progressDialog.Show();
            ForceRender(progressDialog.Dispatcher);
            System.Threading.Thread.Sleep(100);

            // Dialog suppression so the analysis runs silently in the
            // background — covers BOTH the Phase 2 family-size scan AND the
            // optional Phase 5 detailed-report family scan. Both phases call
            // doc.EditFamily(), which can raise the hard "Constraints not
            // satisfied" failure dialog (offers "Remove Constraints" / Cancel)
            // and/or the soft "Constraints between geometry... can behave
            // unpredictably" TaskDialog. Hoisted to the outermost analysis
            // scope (before the try) so the handler stays attached across
            // every phase; detached in the matching finally below regardless
            // of success or failure.
            //
            // Smart-split policy (per user requirement):
            //   - Hard "Constraints not satisfied" dialog → Cancel (2). The
            //     family is skipped (recorded as 0 KB) but its constraints
            //     are NEVER touched, even in a temp in-memory copy.
            //   - Soft warnings and everything else → first action (1001).
            //     EditFamily proceeds so the family's real file size can be
            //     measured.
            //
            // We pair DialogBoxShowing with a FailuresProcessing handler. The
            // FailuresProcessing one catches Revit's accumulated-warnings dialog
            // (the one with the "<< 1 of 2 >>" pager and "Remove Constraints"
            // button) which is shown via the failures pipeline, NOT through
            // DialogBoxShowing — so a DialogBoxShowing-only setup leaked it to
            // the user mid-analysis. The handler uses DeleteWarning + Continue,
            // which discards the warning marker without touching the family's
            // constraints (proven safe by ChunkedMetricsAnalyzer which uses the
            // same pattern). A previous attempt used ResolveFailure +
            // ProceedWithCommit and broke EditFamily with
            // "InvalidOperationException: Loaded Family Editing failed" — DO NOT
            // revert to that combination.
            void AutoAckFamilyWarning(object sender, Autodesk.Revit.UI.Events.DialogBoxShowingEventArgs e)
            {
                string dialogId = "";
                string message = "";
                try { dialogId = e.DialogId ?? ""; } catch { }
                if (e is Autodesk.Revit.UI.Events.TaskDialogShowingEventArgs tde)
                {
                    try { message = tde.Message ?? ""; } catch { }
                }

                // Hard constraint-failure dialog signature — covers both the
                // English DialogId and the visible message text so a Revit
                // localisation or a re-id in a future release still matches.
                bool isHardConstraintFailure =
                    dialogId.IndexOf("Unsatisfied", StringComparison.OrdinalIgnoreCase) >= 0
                    || dialogId.IndexOf("RemoveConstraint", StringComparison.OrdinalIgnoreCase) >= 0
                    || dialogId.IndexOf("CannotBeIgnored", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("not satisfied", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("Remove Constraints", StringComparison.OrdinalIgnoreCase) >= 0;

                // Soft family-editing warnings we WANT to auto-acknowledge so the
                // size scan proceeds without the user mashing buttons. Restricted
                // to known family-related dialog signatures. Previously this branch
                // also fired OverrideResult(1001) on EVERY OTHER dialog the handler
                // saw — which is how generic "save?" / "close?" / "document changed"
                // dialogs Revit raised mid-scan were being auto-clicked with result
                // 1001 (CommandLink1). On some of those dialogs id 1001 maps to
                // "Don't save and close" → the host model was getting closed out
                // from under the user the moment Deep Analysis ran. By restricting
                // the soft branch to known signatures and SKIPPING everything else,
                // unrelated Revit dialogs are left to their default behaviour.
                bool isSoftFamilyWarning =
                    dialogId.IndexOf("Constraint",      StringComparison.OrdinalIgnoreCase) >= 0
                    || dialogId.IndexOf("Family",       StringComparison.OrdinalIgnoreCase) >= 0
                    || dialogId.IndexOf("EditFamily",   StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("behave unpredictably", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("Constraints between",  StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("family",        StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isHardConstraintFailure && !isSoftFamilyWarning)
                {
                    // Not a family-editing dialog — leave Revit's default flow alone.
                    // Critically prevents the "model auto-closes mid-analysis" bug
                    // when an unrelated dialog (save prompt, close prompt, etc.)
                    // happens to fire while the suppression handler is attached.
                    logger?.LogDebug($"[AutoAckFamilyWarning] Ignored unrelated dialog: id='{dialogId}'");
                    return;
                }

                int result = isHardConstraintFailure ? 2 /* IDCANCEL → skip family */
                                                    : 1001 /* CommandLink1 / OK → proceed */;

                try { e.OverrideResult(result); } catch { }

                // Diagnostic so we can verify the routing on a real run and
                // refine the signature list if a dialog falls through wrong.
                logger?.LogInfo($"[AutoAckFamilyWarning] DialogId='{dialogId}' Hard={isHardConstraintFailure} Soft={isSoftFamilyWarning} → OverrideResult({result})");
            }

            uiApp.DialogBoxShowing += AutoAckFamilyWarning;

            // Failures-pipeline handler: silently drops WARNING-severity messages so
            // Revit never opens its accumulated-warnings dialog mid-scan. Leaves
            // ERROR-severity alone — those signal real corruption and should not be
            // swallowed. Mirrors ChunkedMetricsAnalyzer.OnFailuresProcessing.
            void AutoDeleteFamilyWarningsAccumulated(object? sender, Autodesk.Revit.DB.Events.FailuresProcessingEventArgs e)
            {
                try
                {
                    var fa = e.GetFailuresAccessor();
                    var messages = fa.GetFailureMessages();
                    bool deletedAny = false;
                    foreach (var msg in messages)
                    {
                        if (msg.GetSeverity() == Autodesk.Revit.DB.FailureSeverity.Warning)
                        {
                            fa.DeleteWarning(msg);
                            deletedAny = true;
                        }
                    }
                    if (deletedAny)
                        e.SetProcessingResult(Autodesk.Revit.DB.FailureProcessingResult.Continue);
                }
                catch { /* never throw from a Revit event handler */ }
            }

            uiApp.Application.FailuresProcessing += AutoDeleteFamilyWarningsAccumulated;

            try
            {
                progressDialog.UpdateProgress("Reading your model...", 0);
                ForceRender(progressDialog.Dispatcher);
                System.Threading.Thread.Sleep(50);

                // Get context information
                var sessionId = revitContext?.SessionId.ToString() ?? Guid.NewGuid().ToString();
                var modelGuid = ModelGuidHelper.GetModelGuid(doc);
                var modelPath = DocumentInformationHelper.GetNormalizedPath(doc);
                var modelName = doc.Title;
                var documentId = doc.GetHashCode().ToString();

                // Get Revit username from session
                var capturedBy = Environment.UserName;
                if (sessionRepository != null && revitContext != null)
                {
                    try
                    {
                        var session = Task.Run(() => sessionRepository.GetSessionAsync(sessionId))
                            .GetAwaiter().GetResult();
                        if (session != null && !string.IsNullOrEmpty(session.RevitUsername))
                        {
                            capturedBy = session.RevitUsername;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning($"Failed to get Revit username from session: {ex.Message}");
                    }
                }

                // Cooperative cancellation: the Cancel button on the loading dialog and the
                // window-close X both set IsCancelled to true. We poll it between phases AND
                // pass a Func<bool> into the heavy collectors so their inner loops bail too.
                // Without this the synchronous loop kept running for the full 2-3 minutes
                // after a cancel and then briefly flashed a results dialog on top of the
                // already-closed window.
                Func<bool> isCancelled = () => progressDialog.IsCancelled;

                if (isCancelled())
                {
                    logger?.LogInfo("Model analysis cancelled before fast metrics phase");
                    progressDialog.Dispatcher.Invoke(() => { try { progressDialog.Close(); } catch { } });
                    return Result.Cancelled;
                }

                // Phase 1: Collect fast (sync/save) metrics (0% - 10%)
                progressDialog.UpdateProgress("Reviewing file size, views, and warnings...", 2);
                ForceRender(progressDialog.Dispatcher);

                var fastMetrics = metricsCollector.CollectFastMetrics(doc);
                logger?.LogInfo("Fast (sync/save) metrics collected during manual analysis");

                if (isCancelled())
                {
                    logger?.LogInfo("Model analysis cancelled after fast metrics phase");
                    progressDialog.Dispatcher.Invoke(() => { try { progressDialog.Close(); } catch { } });
                    return Result.Cancelled;
                }

                // Phase 2: Collect expensive metrics with live progress (10% - 80%).
                // Dialog suppression is attached at the method's outer scope
                // (see AutoAckFamilyWarning above the outer try) so it stays
                // active for Phase 2 AND the optional Phase 5 detailed-report
                // scan, which also calls EditFamily.
                ExpensiveMetrics expensiveMetrics;
                {
                    var dialog = progressDialog;
                    expensiveMetrics = metricsCollector.CollectExpensiveMetrics(
                        doc,
                        (stepMessage, stepPercent) =>
                        {
                            // Scale expensive metrics progress to 10-80% range
                            var scaledPercent = 10 + (int)(stepPercent * 0.70);
                            dialog.UpdateProgress(stepMessage, scaledPercent);
                            ForceRender(dialog.Dispatcher);
                        },
                        isCancelled);
                }

                if (isCancelled())
                {
                    logger?.LogInfo("Model analysis cancelled after expensive metrics phase");
                    progressDialog.Dispatcher.Invoke(() => { try { progressDialog.Close(); } catch { } });
                    return Result.Cancelled;
                }

                // Phase 3: Collect medium (periodic) metrics (80% - 88%)
                progressDialog.UpdateProgress("Counting elements and checking connections...", 81);
                ForceRender(progressDialog.Dispatcher);

                var mediumMetrics = metricsCollector.CollectMediumMetrics(doc);
                logger?.LogInfo("Medium (periodic) metrics collected during manual analysis");

                if (isCancelled())
                {
                    logger?.LogInfo("Model analysis cancelled after medium metrics phase (skipping save + sync)");
                    progressDialog.Dispatcher.Invoke(() => { try { progressDialog.Close(); } catch { } });
                    return Result.Cancelled;
                }

                // Phase 4: Save all three to database (88% - 98%)
                progressDialog.UpdateProgress("Saving results...", 89);
                ForceRender(progressDialog.Dispatcher);

                // Insert fast (sync/save) metrics
                var fastCaptureId = Task.Run(() => metricsRepository.InsertFastMetricsAsync(
                    sessionId: sessionId,
                    documentId: documentId,
                    modelGuid: modelGuid,
                    modelPath: modelPath,
                    modelName: modelName,
                    capturedBy: capturedBy,
                    metrics: fastMetrics,
                    captureTypeValue: "manual")).GetAwaiter().GetResult();

                // Insert expensive metrics
                var captureId = Task.Run(() => metricsRepository.InsertExpensiveMetricsAsync(
                    sessionId: sessionId,
                    documentId: documentId,
                    modelGuid: modelGuid,
                    modelPath: modelPath,
                    modelName: modelName,
                    capturedBy: capturedBy,
                    metrics: expensiveMetrics,
                    commandSource: "ribbon",
                    captureReason: "User-initiated analysis")).GetAwaiter().GetResult();

                // Insert medium (periodic) metrics
                var periodicCaptureId = Task.Run(() => metricsRepository.InsertMediumMetricsAsync(
                    sessionId: sessionId,
                    documentId: documentId,
                    modelGuid: modelGuid,
                    modelPath: modelPath,
                    modelName: modelName,
                    capturedBy: capturedBy,
                    metrics: mediumMetrics,
                    isManualTrigger: true,
                    captureIntervalHours: 0)).GetAwaiter().GetResult();

                progressDialog.UpdateProgress("Complete!", 100);
                ForceRender(progressDialog.Dispatcher);

                logger?.LogInfo($"Model analysis completed (fast capture_id: {fastCaptureId}, expensive capture_id: {captureId}, periodic capture_id: {periodicCaptureId})");

                // Sync all three to API (fire-and-forget)
                SyncToApi(services, logger, fastCaptureId, captureId, periodicCaptureId,
                    sessionId, documentId, modelGuid, modelPath, modelName,
                    capturedBy, fastMetrics, expensiveMetrics, mediumMetrics);

                // Show results (auto-closes after 30 seconds)
                progressDialog.ShowResults(
                    expensiveMetrics.FamiliesOver5MbCount ?? 0,
                    expensiveMetrics.PurgeableElementsCount ?? 0,
                    captureId.ToString());

                // Generate detailed report if requested. Bail early when the user cancelled
                // mid-analysis via the dialog Cancel button or by closing the window —
                // without this the synchronous report-generation loop kept running even
                // after the dialog disappeared, and the HTML/PDF was still written.
                if (generateDetailedReport && !progressDialog.IsCancelled)
                {
                    try
                    {
                        progressDialog.UpdateProgress("Collecting details for report...", 98);
                        ForceRender(progressDialog.Dispatcher);

                        var detailedMetrics = metricsCollector.CollectDetailedMetrics(doc, (msg, pct2) =>
                        {
                            progressDialog.UpdateProgress(msg, 98);
                            ForceRender(progressDialog.Dispatcher);
                        });

                        // Match the Model Health Dashboard thresholds: fetch admin-configured
                        // goals so the report's pass/fail badges and "Goal: < N" subtitles
                        // use the same values the dashboard shows for this model.
                        HealthMonitorProtection? healthGoals = null;
                        try
                        {
                            var healthSvc = services.GetService<HealthMonitorProtectionSyncService>();
                            if (healthSvc != null && !string.IsNullOrEmpty(modelGuid))
                                healthGoals = Task.Run(() => healthSvc.FetchProtectionAsync(modelGuid)).GetAwaiter().GetResult();
                        }
                        catch (Exception goalEx) { logger?.LogDebug($"Health goals fetch failed: {goalEx.Message}"); }

                        if (progressDialog.IsCancelled)
                        {
                            logger?.LogInfo("Detailed report generation cancelled by user before render");
                        }
                        else
                        {
                            DetailedReportGenerator.GenerateAndOpen(
                                modelName, modelPath, modelGuid, capturedBy,
                                fastMetrics, mediumMetrics, expensiveMetrics, detailedMetrics,
                                healthGoals);
                            logger?.LogInfo("Detailed report generated and opened");
                        }
                    }
                    catch (Exception reportEx)
                    {
                        logger?.LogWarning($"Failed to generate detailed report: {reportEx.Message}");
                    }
                }
                else if (generateDetailedReport)
                {
                    logger?.LogInfo("Detailed report skipped — user cancelled");
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                logger?.LogError($"Error during model metrics analysis: {ex.Message}", ex);
                progressDialog.ShowError(ex.Message);
                return Result.Failed;
            }
            finally
            {
                // Detach the suppression handler attached above the outer
                // try. Runs whether the analysis succeeded, threw, or was
                // cancelled — guarantees we never leave Revit-app-level
                // event subscriptions hanging after the command returns.
                uiApp.DialogBoxShowing -= AutoAckFamilyWarning;
                try { uiApp.Application.FailuresProcessing -= AutoDeleteFamilyWarningsAccumulated; } catch { }
            }
        }

        private void SyncToApi(
            ServiceRegistry services, ILogger? logger,
            string fastCaptureId, string captureId, string periodicCaptureId,
            string sessionId, string documentId, string modelGuid,
            string modelPath, string modelName, string capturedBy,
            FastMetrics fastMetrics, ExpensiveMetrics expensiveMetrics, MediumMetrics mediumMetrics)
        {
            var metricsSyncService = services.GetService<MetricsSyncService>();
            if (metricsSyncService == null) return;

            // Sync fast (sync/save) metrics
            _ = Task.Run(async () =>
            {
                try
                {
                    var apiRequest = new SyncSaveMetricsApiRequest
                    {
                        CaptureId = fastCaptureId,
                        SessionId = sessionId,
                        DocumentId = documentId,
                        ModelGuid = modelGuid,
                        ModelPath = modelPath,
                        ModelName = modelName,
                        CaptureType = "manual",
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
                    await metricsSyncService.SyncSyncSaveMetricsAsync(apiRequest);
                    logger?.LogDebug("SyncSave metrics synced to API (from manual analysis)");
                }
                catch (Exception ex)
                {
                    logger?.LogWarning($"SyncSave metrics API sync failed (non-critical): {ex.Message}");
                }
            });

            // Sync expensive (manual) metrics
            _ = Task.Run(async () =>
            {
                try
                {
                    var apiRequest = new ManualMetricsApiRequest
                    {
                        CaptureId = captureId,
                        SessionId = sessionId,
                        DocumentId = documentId,
                        ModelGuid = modelGuid,
                        ModelPath = modelPath,
                        ModelName = modelName,
                        CapturedAt = DateTime.UtcNow,
                        CapturedBy = capturedBy,
                        CommandSource = "ribbon",
                        CaptureReason = "User-initiated analysis",
                        FamiliesOver5mbCount = expensiveMetrics.FamiliesOver5MbCount ?? 0,
                        PurgeableElementsCount = expensiveMetrics.PurgeableElementsCount ?? 0,
                        CreatedAt = DateTime.UtcNow
                    };
                    await metricsSyncService.SyncManualMetricsAsync(apiRequest);
                    logger?.LogDebug("Manual metrics synced to API");
                }
                catch (Exception ex)
                {
                    logger?.LogWarning($"Manual metrics API sync failed (non-critical): {ex.Message}");
                }
            });

            // Sync medium (periodic) metrics
            _ = Task.Run(async () =>
            {
                try
                {
                    var apiRequest = new PeriodicMetricsApiRequest
                    {
                        CaptureId = periodicCaptureId,
                        SessionId = sessionId,
                        DocumentId = documentId,
                        ModelGuid = modelGuid,
                        ModelPath = modelPath,
                        ModelName = modelName,
                        CapturedAt = DateTime.UtcNow,
                        CapturedBy = capturedBy,
                        CaptureIntervalHours = 0,
                        IsManualTrigger = true,
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
                    await metricsSyncService.SyncPeriodicMetricsAsync(apiRequest);
                    logger?.LogDebug("Periodic metrics synced to API (from manual analysis)");
                }
                catch (Exception ex)
                {
                    logger?.LogWarning($"Periodic metrics API sync failed (non-critical): {ex.Message}");
                }
            });
        }

        private static void ForceRender(Dispatcher dispatcher)
        {
            dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
    }
}
