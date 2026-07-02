using System;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Helpers;
using BIManageRevit.BIManage.Views;

namespace BIManageRevit.Commands.RibbonCommands
{
    /// <summary>
    /// Command to manually register the current model for BIManage tracking and protection
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class RegisterModelCommand : IExternalCommand
    {
        internal static PushButton? RibbonButton;
        internal static bool _isModelRegistered = false;

        /// <summary>
        /// Updates the Register Model button appearance based on registration status.
        /// Unregistered: "Register\nModel" (default icon)
        /// Registered: "Model\nRegistered" (green tint)
        /// </summary>
        public static void UpdateButtonAppearance(bool isRegistered)
        {
            _isModelRegistered = isRegistered;
            try
            {
                if (RibbonButton == null) return;

                if (isRegistered)
                {
                    RibbonButton.ItemText = "Model\nRegistered";
                    RibbonButton.ToolTip = "This model is registered. Click for registration details.";
                    RibbonButton.LongDescription =
                        "This model is already registered with the server. Protection rules and tracking are active.\n\n" +
                        "Click to view registration details or to re-sync if needed.";
                    // Use green icon if available, fallback to default
                    SetButtonIcon("ModelRegistered", fallback: "RegisterModel");
                }
                else
                {
                    RibbonButton.ItemText = "Register\nModel";
                    RibbonButton.ToolTip = "Register the current model so it can be tracked and protected.";
                    RibbonButton.LongDescription =
                        "Register the model you have open so the server recognises it. After registration, this model receives protection rules, command settings, and has its sessions and metrics tracked.\n\n" +
                        "Most models register automatically on first open. Use this only if registration didn't happen, or to re-register after a model rename.";
                    SetButtonIcon("RegisterModel");
                }
            }
            catch { }
        }

        private static void SetButtonIcon(string iconName, string? fallback = null)
        {
            try
            {
                if (RibbonButton == null) return;

                var iconFolder = "Icons";
                try
                {
                    var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                    var isDark = app?.GetType().GetField("_isDarkTheme",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
                    if (isDark != null && (bool)(isDark.GetValue(app) ?? false))
                        iconFolder = "IconsWhite";
                }
                catch { }

                // Try primary icon, fall back if not found
                var name = iconName;
                try
                {
                    var testUri = new Uri($"pack://application:,,,/BIManageRevit;component/Resources/{iconFolder}/{name}_32.png");
                    var test = new System.Windows.Media.Imaging.BitmapImage(testUri);
                }
                catch
                {
                    name = fallback ?? iconName;
                }

                var icon32 = new System.Windows.Media.Imaging.BitmapImage();
                icon32.BeginInit();
                icon32.UriSource = new Uri($"pack://application:,,,/BIManageRevit;component/Resources/{iconFolder}/{name}_32.png");
                icon32.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                icon32.EndInit();
                icon32.Freeze();

                var icon16 = new System.Windows.Media.Imaging.BitmapImage();
                icon16.BeginInit();
                icon16.UriSource = new Uri($"pack://application:,,,/BIManageRevit;component/Resources/{iconFolder}/{name}_16.png");
                icon16.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                icon16.EndInit();
                icon16.Freeze();

                RibbonButton.LargeImage = icon32;
                RibbonButton.Image = icon16;
            }
            catch { }
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            if (LicenseGuard.IsBlockedByLicense(commandData)) return Result.Succeeded;

            try
            {
                var uiDoc = commandData.Application.ActiveUIDocument;
                var doc = uiDoc?.Document;

                if (doc == null)
                {
                    ModelRegistrationDialog.ShowNoDocument();
                    return Result.Cancelled;
                }

                // Block unsaved documents — PathName is empty until first Save
                if (string.IsNullOrEmpty(doc.PathName))
                {
                    ModelRegistrationDialog.ShowNotSavedWarning();
                    return Result.Cancelled;
                }

                // Block detached workshared models — opened with "Detach from Central", not saved as standalone
                if (doc.IsWorkshared && doc.IsDetached)
                {
                    ModelRegistrationDialog.ShowDetachedWarning();
                    return Result.Cancelled;
                }

                // Get services
                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;

                var logger = services?.GetService<ILogger>();
                var modelRepo = services?.GetService<RegisteredModelsRepository>();
                var modelSyncService = services?.GetService<global::BIManage.Infrastructure.Api.ModelSyncService>();

                if (modelRepo == null)
                {
                    ModelRegistrationDialog.ShowServiceUnavailable();
                    return Result.Failed;
                }

                // Get model information using consistent GUID generation
                var modelGuid = ModelGuidHelper.GetModelGuid(doc, logger);
                if (string.IsNullOrEmpty(modelGuid))
                {
                    ModelRegistrationDialog.ShowNoIdentifier();
                    return Result.Cancelled;
                }

                // Get central model path for secondary duplicate check
                var centralModelPath = GetCentralModelPath(doc);

                // Check if already registered by GUID
                var isRegistered = Task.Run(() => modelRepo.IsModelRegisteredAsync(modelGuid)).GetAwaiter().GetResult();

                // Secondary check: Also check by central model path to prevent duplicates with different GUIDs
                if (!isRegistered && !string.IsNullOrEmpty(centralModelPath))
                {
                    isRegistered = Task.Run(() => modelRepo.IsModelRegisteredByPathAsync(centralModelPath)).GetAwaiter().GetResult();

                    if (isRegistered)
                    {
                        logger?.LogInfo($"Model found by path check: {centralModelPath}");
                    }
                }

                if (isRegistered)
                {
                    ModelRegistrationDialog.ShowAlreadyRegistered(doc.Title, modelGuid, GetModelType(doc));
                    return Result.Succeeded;
                }

                // Gather model information. modelName MUST match the source used by
                // Application.GetModelNameForRegistration (auto-register path), otherwise
                // the two register paths produce different ModelName values for the same
                // physical model (auto: "ELE-MOD.rvt", manual: "ELE-MOD") and create
                // confusingly-different rows on the backend even when the modelGuid
                // matches. For workshared docs we use the central model name; for
                // non-workshared we fall back to doc.Title.
                string registrationModelName = doc.Title;
                if (doc.IsWorkshared)
                {
                    try
                    {
                        var centralName = global::BIManage.Revit.Helpers.DocumentInformationHelper.GetCentralModelName(doc);
                        if (!string.IsNullOrEmpty(centralName))
                            registrationModelName = centralName;
                    }
                    catch { /* fall back to doc.Title */ }
                }

                var modelInfo = new RegisteredModel
                {
                    ModelGuid = modelGuid,
                    ModelName = registrationModelName,
                    CentralModelPath = centralModelPath,
                    ProjectName = doc.ProjectInformation?.Name,
                    CloudProjectId = GetCloudProjectId(doc),
                    ZemanageProjectId = GetCloudProjectId(doc) ?? GetLocalProjectId(doc),
                    RegisteredBy = doc.Application.Username,
                    ModelType = GetModelType(doc),
                    IsLocalCopy = false, // TODO: Detect if local copy
                    IsWorkshared = doc.IsWorkshared,
                    IsFamily = doc.IsFamilyDocument,
                    IsCloudModel = doc.IsModelInCloud,
                    Notes = $"Manually registered on {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                };

                // Register the model locally
                var success = Task.Run(() => modelRepo.RegisterModelAsync(modelInfo)).GetAwaiter().GetResult();

                if (!success)
                {
                    ModelRegistrationDialog.ShowRegistrationFailed();
                    return Result.Failed;
                }

                logger?.LogInfo($"Model registered locally (manual): {modelInfo.ModelName} ({modelGuid}) — awaiting server confirmation");

                // Now push to the server. The "Model Registered" dialog should only
                // appear once the server has actually accepted the POST — previously
                // the manual command showed success on local-SQLite write alone and
                // never attempted server sync, so the model never appeared in the
                // tenant's backend dashboard.
                global::BIManage.Infrastructure.Api.ModelSyncResult? syncResult = null;
                if (modelSyncService != null)
                {
                    try
                    {
                        syncResult = Task.Run(() => modelSyncService.SyncModelAsync(modelGuid)).GetAwaiter().GetResult();
                    }
                    catch (Exception syncEx)
                    {
                        logger?.LogWarning($"Manual registration server sync threw: {syncEx.Message}");
                    }
                }

                if (syncResult != null && syncResult.ServerAccepted)
                {
                    logger?.LogInfo($"Model registered manually and accepted by server: {modelInfo.ModelName} ({modelGuid})");
                    UpdateButtonAppearance(true);
                    ModelRegistrationDialog.ShowRegistered(
                        modelInfo.ModelName, modelGuid, modelInfo.ModelType, modelInfo.ProjectName);
                    return Result.Succeeded;
                }

                if (syncResult != null && syncResult.IsTenantNotProvisioned)
                {
                    logger?.LogWarning($"Manual registration: server rejected — company not provisioned. Model: {modelInfo.ModelName} ({modelGuid})");
                    UpdateButtonAppearance(false);
                    ModelRegistrationDialog.ShowTenantNotProvisioned();
                    // Return Succeeded because the LOCAL side worked; the user now knows what to escalate.
                    return Result.Succeeded;
                }

                if (syncResult != null && syncResult.Rejected)
                {
                    logger?.LogWarning($"Manual registration: server rejected ({syncResult.StatusCode}) — {syncResult.ResponseBody}. Model: {modelInfo.ModelName} ({modelGuid})");
                    UpdateButtonAppearance(false);
                    ModelRegistrationDialog.ShowServerRejected(syncResult.StatusCode, syncResult.ResponseBody);
                    return Result.Succeeded;
                }

                if (syncResult != null && syncResult.WasQueued)
                {
                    logger?.LogInfo($"Manual registration: local save ok, server sync queued for retry. Model: {modelInfo.ModelName} ({modelGuid})");
                    UpdateButtonAppearance(false);
                    ModelRegistrationDialog.ShowRegisteredPendingServer(modelInfo.ModelName);
                    return Result.Succeeded;
                }

                // No transport / service not wired in / other. Keep the old dialog so behaviour
                // mirrors pre-fix in environments where ModelSyncService isn't registered.
                logger?.LogWarning($"Manual registration: local save ok, no server transport available. Model: {modelInfo.ModelName} ({modelGuid})");
                UpdateButtonAppearance(false);
                ModelRegistrationDialog.ShowRegisteredPendingServer(modelInfo.ModelName);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = $"Registration failed: {ex.Message}";
                ModelRegistrationDialog.ShowRegistrationError(ex.Message);
                return Result.Failed;
            }
        }

        private string GetCentralModelPath(Document doc)
        {
            try
            {
                // Cloud models: return user-visible cloud path
                if (doc.IsModelInCloud)
                {
                    var cloudPath = doc.GetCloudModelPath();
                    if (cloudPath != null)
                        return ModelPathUtils.ConvertModelPathToUserVisiblePath(cloudPath);
                    return doc.PathName;
                }

                // Workshared: return central model path
                if (doc.IsWorkshared)
                {
                    var centralPath = doc.GetWorksharingCentralModelPath();
                    if (centralPath != null && !centralPath.Empty)
                    {
                        return ModelPathUtils.ConvertModelPathToUserVisiblePath(centralPath);
                    }
                }

                // Local: return current file path
                return doc.PathName;
            }
            catch
            {
                return doc.PathName;
            }
        }

        private string GetCloudProjectId(Document doc)
        {
            try
            {
                if (doc.IsModelInCloud)
                {
                    var cloudPath = doc.GetCloudModelPath();
                    if (cloudPath != null)
                    {
                        var projectGuid = cloudPath.GetProjectGUID();
                        return projectGuid.ToString();
                    }
                }
            }
            catch { }
            return null;
        }

        private string GetLocalProjectId(Document doc)
        {
            try
            {
                var path = doc.PathName;
                if (string.IsNullOrEmpty(path))
                    return null;

                var folder = System.IO.Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(folder))
                    return null;

                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(folder.ToLowerInvariant()));
                    return new Guid(hash).ToString();
                }
            }
            catch { }
            return null;
        }

        private string GetModelType(Document doc)
        {
            if (doc.IsFamilyDocument)
                return "family";
            if (doc.IsModelInCloud)
                return "cloudmodel";
            if (doc.IsWorkshared)
                return "workshared";
            return "local";
        }
    }
}
