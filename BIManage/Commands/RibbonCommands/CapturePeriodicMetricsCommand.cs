using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Threading.Tasks;
using BIManage.Core.Metrics;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Context;
using BIManage.Revit.Helpers;

namespace BIManageRevit.Commands.RibbonCommands
{
    /// <summary>
    /// Command to manually trigger periodic metrics collection
    /// Collects medium-cost metrics (10 metrics) on demand
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class CapturePeriodicMetricsCommand : IExternalCommand
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
                TaskDialog.Show("Capture Periodic Metrics",
                    "No active project document found.\n\n" +
                    "Please open a Revit project (.rvt) to capture periodic metrics.");
                return Result.Cancelled;
            }

            // Get services from application singleton
            var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
            var logger = app?.Logger;
            var services = app?.GetType().GetField("_services",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;

            if (services == null)
            {
                TaskDialog.Show("Capture Periodic Metrics",
                    "Unable to access application services.\n\n" +
                    "Please restart Revit and try again.");
                return Result.Failed;
            }

            var metricsRepository = services.GetService<ModelFileMetricsRepository>();
            var metricsCollector = services.GetService<ModelFileMetricsCollectorService>();
            var sessionRepository = services.GetService<SessionRepository>();
            var revitContext = services.GetService<IRevitContext>();

            if (metricsRepository == null || metricsCollector == null)
            {
                TaskDialog.Show("Capture Periodic Metrics",
                    "Metrics service not available.\n\n" +
                    "Please check the log file for errors.");
                return Result.Failed;
            }

            try
            {
                // Show confirmation dialog
                var dialogResult = TaskDialog.Show(
                    "Capture Periodic Metrics",
                    $"This will capture periodic metrics for:\n" +
                    $"• {doc.Title}\n\n" +
                    $"The following metrics will be collected:\n" +
                    $"  • Total elements count\n" +
                    $"  • Model elements count\n" +
                    $"  • Annotative elements count\n" +
                    $"  • In-place families count\n" +
                    $"  • Unplaced rooms count\n" +
                    $"  • Views not on sheets count\n" +
                    $"  • Unenclosed rooms count\n" +
                    $"  • Walls not connected count (from warnings)\n" +
                    $"  • Pipes not connected count (from warnings)\n" +
                    $"  • Ducts not connected count (from warnings)\n\n" +
                    $"This should complete in a few seconds.\n\n" +
                    $"Do you want to continue?",
                    TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No);

                if (dialogResult != TaskDialogResult.Yes)
                {
                    return Result.Cancelled;
                }

                // Collect metrics
                logger?.LogInfo($"Manual periodic metrics capture started for: {doc.Title}");

                var metrics = metricsCollector.CollectMediumMetrics(doc);

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
                        var session = Task.Run(() => sessionRepository.GetSessionAsync(sessionId)).GetAwaiter().GetResult();
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

                // Insert metrics (manual trigger = true)
                var captureId = Task.Run(() => metricsRepository.InsertMediumMetricsAsync(
                    sessionId: sessionId,
                    documentId: documentId,
                    modelGuid: modelGuid,
                    modelPath: modelPath,
                    modelName: modelName,
                    capturedBy: capturedBy,
                    metrics: metrics,
                    isManualTrigger: true,
                    captureIntervalHours: 24)).GetAwaiter().GetResult();

                logger?.LogInfo($"Manual periodic metrics capture completed (capture_id: {captureId})");

                // Sync to API (fire-and-forget)
                var metricsSyncService = services.GetService<MetricsSyncService>();
                if (metricsSyncService != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var apiRequest = new PeriodicMetricsApiRequest
                            {
                                CaptureId = captureId,
                                SessionId = sessionId,
                                DocumentId = documentId,
                                ModelGuid = modelGuid,
                                ModelPath = modelPath,
                                ModelName = modelName,
                                CapturedAt = DateTime.UtcNow,
                                CapturedBy = capturedBy,
                                CaptureIntervalHours = 24,
                                IsManualTrigger = true,
                                TotalElementsCount = metrics.TotalElementsCount ?? 0,
                                ModelElementsCount = metrics.ModelElementsCount ?? 0,
                                AnnotativeElementsCount = metrics.AnnotativeElementsCount ?? 0,
                                InplaceFamiliesCount = metrics.InplaceFamiliesCount ?? 0,
                                UnplacedRoomsCount = metrics.UnplacedRoomsCount ?? 0,
                                ViewsNotOnSheetsCount = metrics.ViewsNotOnSheetsCount ?? 0,
                                UnenclosedRoomsCount = metrics.UnenclosedRoomsCount ?? 0,
                                WallsNotConnectedCount = metrics.WallsNotConnectedCount ?? 0,
                                PipesNotConnectedCount = metrics.PipesNotConnectedCount ?? 0,
                                DuctsNotConnectedCount = metrics.DuctsNotConnectedCount ?? 0,
                                CreatedAt = DateTime.UtcNow
                            };
                            await metricsSyncService.SyncPeriodicMetricsAsync(apiRequest);
                            logger?.LogDebug("Periodic metrics synced to API");
                        }
                        catch (Exception syncEx)
                        {
                            logger?.LogWarning($"Periodic metrics API sync failed (non-critical): {syncEx.Message}");
                        }
                    });
                }

                // Show results summary
                var resultsMessage = "Periodic Metrics Captured\n\n";
                resultsMessage += $"Model: {modelName}\n";
                resultsMessage += $"Captured: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
                resultsMessage += $"Capture ID: {captureId}\n\n";
                resultsMessage += "Metrics Collected:\n";
                resultsMessage += $"  • Total Elements: {metrics.TotalElementsCount ?? 0}\n";
                resultsMessage += $"  • Model Elements: {metrics.ModelElementsCount ?? 0}\n";
                resultsMessage += $"  • Annotative Elements: {metrics.AnnotativeElementsCount ?? 0}\n";
                resultsMessage += $"  • In-place Families: {metrics.InplaceFamiliesCount ?? 0}\n";
                resultsMessage += $"  • Unplaced Rooms: {metrics.UnplacedRoomsCount ?? 0}\n";
                resultsMessage += $"  • Views Not on Sheets: {metrics.ViewsNotOnSheetsCount ?? 0}\n";
                resultsMessage += $"  • Unenclosed Rooms: {metrics.UnenclosedRoomsCount ?? 0}\n";
                resultsMessage += $"  • Walls Not Connected: {metrics.WallsNotConnectedCount ?? 0}\n";
                resultsMessage += $"  • Pipes Not Connected: {metrics.PipesNotConnectedCount ?? 0}\n";
                resultsMessage += $"  • Ducts Not Connected: {metrics.DuctsNotConnectedCount ?? 0}\n";
                resultsMessage += $"\n✓ Results saved to database";

                TaskDialog.Show("Capture Periodic Metrics", resultsMessage);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                logger?.LogError($"Error during manual periodic metrics capture: {ex.Message}", ex);

                message = $"Failed to capture periodic metrics: {ex.Message}";
                TaskDialog.Show("Error", $"Periodic Metrics Capture Failed\n\n{ex.Message}");

                return Result.Failed;
            }
        }
    }
}
