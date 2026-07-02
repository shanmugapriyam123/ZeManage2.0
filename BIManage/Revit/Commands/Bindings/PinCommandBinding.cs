

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Common.Helpers;
using BIManage.Core.Evidence;
using BIManage.Core.Protection.Models;
using BIManage.Revit.PinProtection;
using BIManage.Revit.PinProtection.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Context;
using BIManage.Revit.Helpers;

namespace BIManage.Revit.Commands.Bindings
{
    /// <summary>
    /// Handles Pin command interception
    /// Shows dialog after pinning to allow admin to configure protection
    /// Uses the Executed event
    /// </summary>
    public class PinCommandBinding : CommandBindingBase
    {
        private readonly Func<bool> _isAdminCheck;
        private readonly PinProtectionRepository? _pinProtectionRepository;
        private readonly AuditRepository? _auditRepository;
        private readonly Func<CommandProtectionBinding?> _commandProtectionGetter;
        private readonly Func<IRevitContext?>? _revitContextGetter;
        private readonly Func<RegisteredModelsRepository?>? _registeredModelsRepoGetter;
        private readonly Func<PinProtectionSyncService?>? _pinProtectionSyncGetter;
        private readonly Func<IScreenshotService?>? _screenshotServiceGetter;
        private readonly Func<EvidenceRepository?>? _evidenceRepositoryGetter;
        private readonly IRuleCommandInterceptor? _ruleInterceptor;
        private AddInCommandBinding? _binding;

        public PinCommandBinding(
            UIApplication uiApp,
            ILogger logger,
            Func<bool> isAdminCheck,
            PinProtectionRepository? pinProtectionRepository = null,
            Func<CommandProtectionBinding?>? commandProtectionGetter = null,
            Func<IRevitContext?>? revitContextGetter = null,
            Func<RegisteredModelsRepository?>? registeredModelsRepoGetter = null,
            Func<PinProtectionSyncService?>? pinProtectionSyncGetter = null,
            AuditRepository? auditRepository = null,
            Func<IScreenshotService?>? screenshotServiceGetter = null,
            Func<EvidenceRepository?>? evidenceRepositoryGetter = null,
            IRuleCommandInterceptor? ruleInterceptor = null)
            : base(uiApp, logger)
        {
            _isAdminCheck = isAdminCheck ?? throw new ArgumentNullException(nameof(isAdminCheck));
            _pinProtectionRepository = pinProtectionRepository;
            _auditRepository = auditRepository;
            _commandProtectionGetter = commandProtectionGetter ?? (() => null);
            _revitContextGetter = revitContextGetter;
            _registeredModelsRepoGetter = registeredModelsRepoGetter;
            _pinProtectionSyncGetter = pinProtectionSyncGetter;
            _screenshotServiceGetter = screenshotServiceGetter;
            _evidenceRepositoryGetter = evidenceRepositoryGetter;
            _ruleInterceptor = ruleInterceptor;

            // Pin command ID: 32997
            CommandId = RevitCommandId.LookupPostableCommandId(PostableCommand.Pin);
        }

        /// <summary>
        /// Get current Revit username (not Windows username)
        /// </summary>
        private string GetRevitUsername()
        {
            try
            {
                return UIApp?.Application?.Username ?? Environment.UserName;
            }
            catch
            {
                return Environment.UserName;
            }
        }

        /// <summary>
        /// Not used for Pin command directly (we use Register() which handles both events)
        /// </summary>
        public override void RegisterWithBeforeExecute()
        {
            // Pin uses both BeforeExecuted (command protection) and Executed (pin protection dialog)
            Register();
        }

        /// <summary>
        /// Register with both BeforeExecuted (for command protection) and Executed (for pin protection dialog)
        /// </summary>
        public override void Register()
        {
            if (!CanRegister())
                return;

            try
            {
                _binding = UIApp.CreateAddInCommandBinding(CommandId);
                _binding.BeforeExecuted += OnBeforeExecuted;  // Command protection check
                _binding.Executed += OnPinExecuted;            // Pin protection dialog

                // Mark as registered in the shared registry to prevent duplicate bindings
                MarkAsRegistered(CommandId.Name);

                Logger?.LogInfo("Pin command binding registered (BeforeExecuted + Executed events)");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to register Pin command binding: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Unregister event handlers
        /// </summary>
        public override void Unregister()
        {
            if (_binding != null)
            {
                _binding.BeforeExecuted -= OnBeforeExecuted;
                _binding.Executed -= OnPinExecuted;
                _binding = null;
                Logger?.LogInfo("Pin command binding unregistered");
            }
        }

        /// <summary>
        /// Handle BeforeExecuted event for command protection check
        /// This runs BEFORE Revit pins the elements
        /// </summary>
        private void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e)
        {
            try
            {
                // PRIORITY 0: Check CommandProtectionBinding for Notify/Assist/Protect settings
                var commandProtection = _commandProtectionGetter?.Invoke();
                if (commandProtection != null)
                {
                    commandProtection.ProcessCommandBeforeExecution(e);
                    if (e.Cancel)
                    {
                        Logger?.LogInfo("Pin command cancelled by command protection");
                        return;
                    }
                }

                // PRIORITY 1: Check Rule Management rules
                _ruleInterceptor?.OnBeforeExecuted(sender, e);
                if (e.Cancel)
                {
                    Logger?.LogInfo("Pin command cancelled by rule evaluation");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in Pin BeforeExecuted: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Handle Pin command execution
        /// Called AFTER Revit has attempted to pin the elements
        /// Note: Revit's pin may not always work, so we explicitly pin elements
        /// </summary>
        private void OnPinExecuted(object sender, EventArgs e)
        {
            try
            {
                UIDocument uidoc = (sender as UIApplication)?.ActiveUIDocument;
                if (uidoc == null)
                {
                    Logger?.LogWarning("No active document for Pin command");
                    return;
                }

                Document doc = uidoc.Document;
                var selection = uidoc.Selection.GetElementIds();

                if (selection.Count == 0)
                {
                    Logger?.LogDebug("Pin command executed but no elements selected");
                    return;
                }

                // Get selected elements
                var elements = selection
                    .Select(id => doc.GetElement(id))
                    .Where(el => el != null && el.IsValidObject)
                    .ToList();

                if (!elements.Any())
                    return;

                // Skip protection dialog if model is not registered — just do normal pin
                var modelGuid = ModelGuidHelper.GetModelGuid(doc, Logger);
                bool isRegistered = false;
                if (!string.IsNullOrEmpty(modelGuid))
                {
                    var modelRepo = _registeredModelsRepoGetter?.Invoke();
                    if (modelRepo != null)
                    {
                        try { isRegistered = Task.Run(() => modelRepo.IsModelRegisteredAsync(modelGuid)).GetAwaiter().GetResult(); }
                        catch { }

                        // GUID reconciliation. Workshared / open-as-detached-then-save-as-local
                        // mints a different ModelPath GUID locally than what the model was
                        // originally registered under server-side. EventRegistryService.OnDocumentOpened
                        // (around line 316-343) reconciles this for model_session POSTs by looking up
                        // by (modelName, centralPath); without the same step here, the Pin dialog
                        // silently skipped for registered-but-renumbered models — observed in
                        // BIManageRevit_20260523_1621.log:319 where local guid 8110e446...
                        // was reconciled to registered guid 014c63f6... but PinCommandBinding
                        // still checked IsModelRegisteredAsync against the local guid.
                        if (!isRegistered)
                        {
                            try
                            {
                                var lookupName = doc.Title ?? string.Empty;
                                var centralPath = DocumentInformationHelper.GetNormalizedPath(doc);
                                var reconciledGuid = Task.Run(() => modelRepo.FindExistingModelGuidAsync(lookupName, centralPath)).GetAwaiter().GetResult();
                                if (!string.IsNullOrEmpty(reconciledGuid) && reconciledGuid != modelGuid)
                                {
                                    Logger?.LogInfo($"Pin: reconciled model guid '{modelGuid}' -> '{reconciledGuid}' for '{lookupName}'");
                                    modelGuid = reconciledGuid;
                                    isRegistered = Task.Run(() => modelRepo.IsModelRegisteredAsync(modelGuid)).GetAwaiter().GetResult();
                                }
                            }
                            catch (Exception reconcileEx)
                            {
                                Logger?.LogWarning($"Pin: model guid reconciliation failed: {reconcileEx.Message}");
                            }
                        }
                    }
                }

                if (!isRegistered)
                {
                    // Surface the reason at INFO level — was DEBUG and invisible in the default
                    // log filter, which made the "Pin dialog never opens" symptom impossible to
                    // diagnose without raising the log level.
                    Logger?.LogInfo("Pin: model not registered with server - applying normal pin without protection dialog (register the model first to enable protection)");
                    // AddInCommandBinding replaces Revit's native command, so we must pin explicitly
                    using (var trans = new Transaction(doc, "Pin Elements"))
                    {
                        trans.Start();
                        foreach (var el in elements)
                        {
                            if (!el.Pinned)
                                el.Pinned = true;
                        }
                        trans.Commit();
                    }
                    return;
                }

                // Show simplified protection dialog (Pin vs Protected Pin)
                bool result = ShowProtectionDialog(doc, elements);

                Logger?.LogInfo($"Pin command executed: {elements.Count} elements, protection configured: {result}");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in Pin command handler: {ex.Message}", ex);
                TaskDialog.Show("Pin Protection Error",
                    $"Failed to configure pin protection:\n\n{ex.Message}");
            }
        }

        /// <summary>
        /// Show simplified dialog: Pin (normal) vs Protected Pin (Prevent mode)
        /// Returns true if protection was configured, false for normal pin
        /// </summary>
        private bool ShowProtectionDialog(Document doc, List<Element> elements)
        {
            // Build element list for display
            string elementList = string.Join("\n", elements.Take(10).Select(e =>
                $"  • {e.Name} ({e.Id.GetIdValue()})"));

            if (elements.Count > 10)
                elementList += $"\n  ... and {elements.Count - 10} more";

            // Create XAML dialog with 2 options
            var pinDialog = new BIManageRevit.BIManage.Views.Bindings.PinSelectionDialog
            {
                DataContext = new BIManageRevit.BIManage.ViewModels.Bindings.PinSelectionViewModel(
                    elements.Count,
                    elementList,
                    (isProtected) => { })
            };

            BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(pinDialog);
            bool? pinDialogResult = pinDialog.ShowDialog();
            if (pinDialogResult != true)
            {
                // User cancelled
                return false;
            }

            bool isProtectedPin = pinDialog.IsProtectedPin;

            // First, ensure elements are pinned (in a transaction)
            using (Transaction pinTrans = new Transaction(doc, "Pin Elements"))
            {
                pinTrans.Start();
                try
                {
                    foreach (var element in elements)
                    {
                        if (element != null && element.IsValidObject && !element.Pinned)
                        {
                            element.Pinned = true;
                        }
                    }
                    pinTrans.Commit();
                    Logger?.LogDebug($"Explicitly pinned {elements.Count} elements");
                }
                catch (Exception ex)
                {
                    pinTrans.RollBack();
                    Logger?.LogError($"Failed to pin elements: {ex.Message}", ex);

                    var errorDialog = new BIManageRevit.BIManage.Views.Bindings.ErrorDialog(
                        windowTitle: "Pin Protection",
                        headerText: "Error",
                        message: "Failed to pin elements.",
                        details: ex.Message);
                    BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(errorDialog);
                    errorDialog.ShowDialog();
                    return false;
                }
            }

            // If user chose normal pin, we're done
            if (!isProtectedPin)
            {
                Logger?.LogDebug("User chose normal pin without protection");
                return false;
            }

            // User chose Protected Pin — check model registration first
            var modelGuid = ModelGuidHelper.GetModelGuid(doc, Logger);
            if (!string.IsNullOrEmpty(modelGuid))
            {
                var modelRepo = _registeredModelsRepoGetter?.Invoke();
                if (modelRepo != null)
                {
                    bool isRegistered = Task.Run(() => modelRepo.IsModelRegisteredAsync(modelGuid)).GetAwaiter().GetResult();
                    if (!isRegistered)
                    {
                        var regDialog = new BIManageRevit.BIManage.Views.Bindings.ErrorDialog(
                            windowTitle: "Pin Protection",
                            headerText: "Model Not Registered",
                            message: "Pin protection requires the model to be registered first.",
                            details: "Please register this model before applying protected pins.");
                        BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(regDialog);
                        regDialog.ShowDialog();
                        Logger?.LogWarning($"Pin protection blocked — model not registered: {modelGuid}");
                        return false;
                    }
                }
            }

            string comment = "Protected element - admin authorization required to unpin";

            using (Transaction protectTrans = new Transaction(doc, "Apply Pin Protection"))
            {
                protectTrans.Start();

                try
                {
                    var protectionInfo = ProtectedPinInfo.Create(
                        elements[0], // Template (actual info created per element)
                        GetRevitUsername(),
                        comment,
                        ProtectionMode.Protect,
                        requireComment: false,
                        notifyOnUnpin: true);

                    PinProtectionStorage.SetProtection(doc, protectionInfo, elements);

                    protectTrans.Commit();

                    Logger?.LogInfo($"Applied Protected Pin to {elements.Count} elements");

                    // Audit log + evidence capture for Protected Pin applied
                    CaptureProtectionApplied(doc, elements);

                    // Sync to database (async, non-blocking)
                    if (_pinProtectionRepository != null)
                    {
                        SyncProtectionToDatabase(doc, elements);
                    }

                    // Show success dialog
                    var successDialog = new BIManageRevit.BIManage.Views.Bindings.ProtectionAppliedDialog
                    {
                        DataContext = new BIManageRevit.BIManage.ViewModels.Bindings.ProtectionAppliedViewModel(
                            elements.Count,
                            GetRevitUsername(),
                            "Protected Pin has been applied.",
                            () => { })
                    };
                    BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(successDialog);
                    successDialog.ShowDialog();

                    return true;
                }
                catch (Exception ex)
                {
                    protectTrans.RollBack();
                    Logger?.LogError($"Failed to apply pin protection: {ex.Message}", ex);

                    var errorDialog = new BIManageRevit.BIManage.Views.Bindings.ErrorDialog(
                        windowTitle: "Pin Protection",
                        headerText: "Error",
                        message: "Failed to apply protection.",
                        details: ex.Message);
                    BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(errorDialog);
                    errorDialog.ShowDialog();
                    return false;
                }
            }
        }

        /// <summary>
        /// Log audit entry and capture screenshot when a Protected Pin is applied.
        /// </summary>
        private void CaptureProtectionApplied(Document doc, List<Element> elements)
        {
            try
            {
                var auditLogId = Guid.NewGuid().ToString();
                var username = GetRevitUsername();
                var sessionId = _revitContextGetter?.Invoke()?.SessionId.ToString();
                var modelGuid = ModelGuidHelper.GetModelGuid(doc, Logger);
                var firstElement = elements.FirstOrDefault();
                var elementIds = string.Join(",", elements.Select(e => e.Id.GetIdValue()));

                // Audit log
                if (_auditRepository != null)
                {
                    try
                    {
                        var auditEntry = new ProtectionAuditEntry
                        {
                            AuditLogId = auditLogId,
                            Timestamp = DateTime.UtcNow,
                            UserName = username,
                            ModelGuid = modelGuid,
                            CommandName = "Pin",
                            Mode = BIManage.Core.Rules.Models.ProtectionMode.Protect,
                            Action = BIManage.Core.Protection.Models.ProtectionAction.Allowed,
                            ElementIds = elementIds,
                            ElementCount = elements.Count,
                            ElementName = firstElement?.Name,
                            Reason = "Protected Pin applied",
                            EventSource = "Pin Protection",
                            SessionId = sessionId
                        };
                        _auditRepository.SaveAuditEntry(auditEntry);
                        Logger?.LogDebug($"Audit entry saved for Protected Pin applied (auditLogId: {auditLogId})");
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning($"Failed to save audit entry for Pin: {ex.Message}");
                    }
                }

                // Screenshot + evidence capture
                var screenshotService = _screenshotServiceGetter?.Invoke();
                var evidenceRepository = _evidenceRepositoryGetter?.Invoke();
                if (screenshotService != null && evidenceRepository != null)
                {
                    try
                    {
                        var imageBase64 = screenshotService.CaptureRevitWindowAsBase64();
                        if (!string.IsNullOrEmpty(imageBase64))
                        {
                            Logger?.LogInfo($"[PinBinding] Captured screenshot for Protected Pin ({imageBase64.Length} chars)");

                            string? savedPath = screenshotService.SaveBase64ToTempFile(imageBase64, sessionId ?? "unknown", $"{auditLogId}_applied");
                            string? fileHash = null;
                            if (!string.IsNullOrEmpty(savedPath))
                            {
                                try
                                {
                                    using var sha = System.Security.Cryptography.SHA256.Create();
                                    var bytes = System.IO.File.ReadAllBytes(savedPath);
                                    fileHash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                                }
                                catch { }
                            }

                            var evidence = new EvidenceRepository.EvidenceCapture
                            {
                                EvidenceId = Guid.NewGuid().ToString(),
                                AuditLogId = auditLogId,
                                SessionId = sessionId,
                                ProtectionType = "pin",
                                CaptureType = "screenshot",
                                CaptureStage = "applied",
                                CapturedAt = DateTime.UtcNow,
                                CommandId = "pin_protection",
                                CommandName = "Pin",
                                ElementIds = elementIds,
                                ElementCount = elements.Count,
                                FilePath = savedPath,
                                FileSizeBytes = (long?)(imageBase64.Length * 3 / 4),
                                FileFormat = "png",
                                FileHashSha256 = fileHash,
                                UploadStatus = "pending",
                                Metadata = $"{{\"username\":\"{username}\",\"isAdmin\":true,\"action\":\"pin_applied\"}}"
                            };

                            _ = evidenceRepository.RecordEvidenceCaptureAsync(evidence).ContinueWith(t =>
                            {
                                if (t.IsFaulted)
                                    Logger?.LogWarning($"Failed to record pin evidence: {t.Exception?.InnerException?.Message}");
                                else
                                    Logger?.LogInfo($"Recorded pin evidence capture: {evidence.EvidenceId}");
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning($"[PinBinding] Failed to capture evidence: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[PinBinding] CaptureProtectionApplied failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sync protection metadata to local database and backend API (fire and forget)
        /// </summary>
        private async void SyncProtectionToDatabase(Document doc, List<Element> elements)
        {
            if (_pinProtectionRepository == null)
                return;

            try
            {
                var modelGuid = ModelGuidHelper.GetModelGuid(doc, Logger);
                var projectName = DocumentInformationHelper.GetProjectName(doc);
                var sessionId = _revitContextGetter?.Invoke()?.SessionId.ToString();
                var protections = new List<ProtectedPinInfo>();

                foreach (var element in elements)
                {
                    if (element == null || !element.IsValidObject)
                        continue;

                    var protection = PinProtectionStorage.GetProtection(element);
                    if (protection != null)
                    {
                        await _pinProtectionRepository.SyncProtectionAsync(
                            modelGuid,
                            projectName,
                            protection,
                            "PinCommandBinding",
                            sessionId);

                        protections.Add(protection);
                        Logger?.LogDebug($"Synced protection to database for element {element.Id.GetIdValue()}");
                    }
                }

                // Sync to backend API
                var syncService = _pinProtectionSyncGetter?.Invoke();
                if (syncService != null && protections.Count > 0)
                {
                    await syncService.SyncPinProtectionsToServerAsync(
                        modelGuid, protections, sessionId, projectName, "PinCommandBinding");
                    Logger?.LogInfo($"Synced {protections.Count} pin protections to server for model {modelGuid}");
                }
            }
            catch (Exception ex)
            {
                // Don't fail the UI operation if sync fails
                Logger?.LogError($"Failed to sync protection: {ex.Message}", ex);
            }
        }

    }
}
