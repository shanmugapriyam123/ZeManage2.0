using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Auth;
using BIManage.Revit.Helpers;
using BIManageRevit.BIManage.Views.Metrics;

namespace BIManageRevit.Commands.RibbonCommands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class ModelHealthCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            if (LicenseGuard.IsBlockedByLicense(commandData)) return Result.Succeeded;

            var uiApp = commandData.Application;
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc?.Document;

            // Get services from application singleton
            var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
            var logger = app?.Logger;
            var services = app?.GetType().GetField("_services",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;

            if (services == null)
            {
                TaskDialog.Show("Model Health Dashboard",
                    "Unable to access application services.\n\n" +
                    "Please restart Revit and try again.");
                return Result.Failed;
            }

            var metricsRepository = services.GetService<ModelFileMetricsRepository>();

            if (metricsRepository == null)
            {
                TaskDialog.Show("Model Health Dashboard",
                    "Metrics repository not available.\n\n" +
                    "Please check the log file for errors.");
                return Result.Failed;
            }

            try
            {
                // Get model info if document is open
                string? modelGuid = null;
                string modelName = "All Models";
                string? revitProjectName = null;
                bool isRegistered = true;

                if (doc != null && !doc.IsFamilyDocument)
                {
                    modelGuid = ModelGuidHelper.GetModelGuid(doc);
                    modelName = doc.Title;

                    // Authoritative project name for the dashboard's "Project Name" field \u2014
                    // this is what the user typed into Revit's Project Properties, not the
                    // file name the API repeats into both project_name / model_name columns.
                    try { revitProjectName = doc.ProjectInformation?.Name; }
                    catch { /* ProjectInformation can be null in unusual docs */ }

                    // Check if model is registered. Do NOT abort if it isn't \u2014 the dashboard
                    // renders an inline "register now" banner so the user can still see
                    // locally-computed metrics. Aborting with a popup hides everything for no
                    // good reason.
                    try
                    {
                        var modelRepo = services.GetService<global::BIManage.Data.SQLite.RegisteredModelsRepository>();
                        if (modelRepo != null && !string.IsNullOrEmpty(modelGuid))
                        {
                            var regModel = System.Threading.Tasks.Task.Run(() => modelRepo.GetModelAsync(modelGuid)).GetAwaiter().GetResult();
                            if (regModel == null)
                            {
                                isRegistered = false;
                                logger?.LogInfo($"[ModelHealth] Opening dashboard for unregistered model {modelGuid}; inline banner will prompt for registration.");
                            }
                            else if (!string.IsNullOrEmpty(regModel.ProjectName))
                            {
                                modelName = regModel.ProjectName + " | " + (regModel.ModelName ?? doc.Title);
                            }
                        }
                    }
                    catch { /* Use doc.Title as fallback */ }
                }

                // Get authenticated HTTP client for API goals
                var httpClient = services.GetService<AuthenticatedHttpClient>();

                // Get metrics sync service for fetching latest from API
                var metricsSyncService = services.GetService<MetricsSyncService>();

                // Show the dashboard
                var dashboard = new ModelHealthDashboard(metricsRepository, logger, modelGuid, modelName, httpClient, metricsSyncService, revitProjectName, isRegistered);
                new System.Windows.Interop.WindowInteropHelper(dashboard) { Owner = uiApp.MainWindowHandle };
                dashboard.ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                logger?.LogError($"Error opening Model Health Dashboard: {ex.Message}", ex);
                message = $"Failed to open Model Health Dashboard: {ex.Message}";
                return Result.Failed;
            }
        }
    }
}
