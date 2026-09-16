using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Common.Helpers;
using BIManage.Core.Evidence;
using BIManage.Revit.PinProtection;
using BIManage.Revit.PinProtection.Models;
using BIManage.Core.Protection.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Context;

namespace BIManage.Revit.Commands.Bindings
{
    /// <summary>
    /// Handles Unpin command interception
    /// Checks for pin protection and enforces according to mode
    /// Uses the Executed event
    /// </summary>
    public class UnpinCommandBinding : CommandBindingBase
    {
        private readonly Func<bool> _isAdminCheck;
        private readonly OtpRepository? _otpRepository;
        private readonly AuditRepository? _auditRepository;
        private readonly PinProtectionRepository? _pinProtectionRepository;
        private readonly Func<IScreenshotService?>? _screenshotServiceGetter;
        private readonly Func<IRevitContext?>? _revitContextGetter;
        private readonly UnpinnedElementTracker? _unpinnedElementTracker;
        private readonly Func<CommandProtectionBinding?> _commandProtectionGetter;
        private readonly Func<PinProtectionSyncService?>? _pinProtectionSyncGetter;
        private readonly IRuleCommandInterceptor? _ruleInterceptor;
        private readonly BIManage.Core.Features.IFeatureToggleService? _featureToggleService;
        private AddInCommandBinding? _binding;

        public UnpinCommandBinding(
            UIApplication uiApp,
            ILogger logger,
            Func<bool> isAdminCheck,
            OtpRepository? otpRepository = null,
            AuditRepository? auditRepository = null,
            PinProtectionRepository? pinProtectionRepository = null,
            Func<IScreenshotService?>? screenshotServiceGetter = null,
            Func<IRevitContext?>? revitContextGetter = null,
            UnpinnedElementTracker? unpinnedElementTracker = null,
            Func<CommandProtectionBinding?>? commandProtectionGetter = null,
            Func<PinProtectionSyncService?>? pinProtectionSyncGetter = null,
            IRuleCommandInterceptor? ruleInterceptor = null,
            BIManage.Core.Features.IFeatureToggleService? featureToggleService = null)
            : base(uiApp, logger)
        {
            _isAdminCheck = isAdminCheck ?? throw new ArgumentNullException(nameof(isAdminCheck));
            _otpRepository = otpRepository;
            _auditRepository = auditRepository;
            _pinProtectionRepository = pinProtectionRepository;
            _screenshotServiceGetter = screenshotServiceGetter;
            _revitContextGetter = revitContextGetter;
            _unpinnedElementTracker = unpinnedElementTracker;
            _commandProtectionGetter = commandProtectionGetter ?? (() => null);
            _pinProtectionSyncGetter = pinProtectionSyncGetter;
            _ruleInterceptor = ruleInterceptor;
            _featureToggleService = featureToggleService;

            // Unpin command ID: 33001
            CommandId = RevitCommandId.LookupPostableCommandId(PostableCommand.Unpin);
        }

        /// <summary>
        /// Not used for Unpin command directly (we use Register() which handles both events)
        /// </summary>
        public override void RegisterWithBeforeExecute()
        {
            // Unpin uses both BeforeExecuted (command protection) and Executed (pin protection)
            Register();
        }

        /// <summary>
        /// Register with both BeforeExecuted (for command protection) and Executed (for pin protection)
        /// </summary>
        public override void Register()
        {
            if (!CanRegister())
                return;

            try
            {
                _binding = UIApp.CreateAddInCommandBinding(CommandId);
                _binding.BeforeExecuted += OnBeforeExecuted;  // Command protection check
                _binding.Executed += OnUnpinExecuted;          // Pin protection logic

                // Mark as registered in the shared registry to prevent duplicate bindings
                MarkAsRegistered(CommandId.Name);

                Logger?.LogInfo("Unpin command binding registered (BeforeExecuted + Executed events)");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to register Unpin command binding: {ex.Message}", ex);
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
                _binding.Executed -= OnUnpinExecuted;
                _binding = null;
                Logger?.LogInfo("Unpin command binding unregistered");
            }
        }

        /// <summary>
        /// Handle BeforeExecuted event for command protection check
        /// This runs BEFORE Revit unpins the elements
        /// </summary>
        private void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e)
        {
            try
            {
                if (_featureToggleService?.IsGlobalPaused == true || _featureToggleService?.IsEmployeeCaptureDisabled == true)
                    return;

                // PRIORITY 0: Check command protection settings from command_settings table
                var commandProtection = _commandProtectionGetter?.Invoke();
                if (commandProtection != null)
                {
                    commandProtection.ProcessCommandBeforeExecution(e);
                    if (e.Cancel)
                    {
                        Logger?.LogInfo("Unpin command cancelled by command protection");
                        return;
                    }
                }

                // PRIORITY 1: Check Rule Management rules
                _ruleInterceptor?.OnBeforeExecuted(sender, e);
                if (e.Cancel)
                {
                    Logger?.LogInfo("Unpin command cancelled by rule evaluation");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in Unpin BeforeExecuted: {ex.Message}", ex);
                // Fail open - don't block on error
            }
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
        /// Handle Unpin command execution
        /// Called AFTER Revit has attempted to unpin the elements
        /// </summary>
        private void OnUnpinExecuted(object sender, EventArgs e)
        {
            try
            {
                if (_featureToggleService?.IsGlobalPaused == true || _featureToggleService?.IsEmployeeCaptureDisabled == true)
                    return;

                UIDocument uidoc = (sender as UIApplication)?.ActiveUIDocument;
                if (uidoc == null)
                {
                    Logger?.LogWarning("No active document for Unpin command");
                    return;
                }

                Document doc = uidoc.Document;
                var selection = uidoc.Selection.GetElementIds();

                if (selection.Count == 0)
                {
                    Logger?.LogDebug("Unpin command executed but no elements selected");
                    return;
                }

                // Get selected elements
                var elements = selection
                    .Select(id => doc.GetElement(id))
                    .Where(el => el != null && el.IsValidObject)
                    .ToList();

                if (!elements.Any())
                    return;

                // Check which elements are protected
                var protectedElements = PinProtectionStorage.GetProtectedFromSelection(elements);

                if (!protectedElements.Any())
                {
                    // No protected elements - just perform normal unpin
                    Logger?.LogDebug($"Unpin command: {elements.Count} normal elements (no protection)");
                    PerformNormalUnpin(doc, elements);
                    return;
                }

                // Separate protected and unprotected elements
                var protectedElementIds = new HashSet<long>(protectedElements.Select(p => p.ElementId));
                var unprotectedElements = elements.Where(e => !protectedElementIds.Contains(e.Id.GetIdValue())).ToList();

                // Unpin any unprotected elements first
                if (unprotectedElements.Any())
                {
                    PerformNormalUnpin(doc, unprotectedElements);
                    Logger?.LogDebug($"Unpinned {unprotectedElements.Count} unprotected elements");
                }

                // Process protected elements
                bool allowUnpin = ProcessProtectedElements(doc, protectedElements);

                Logger?.LogInfo($"Unpin command executed: {protectedElements.Count} protected elements, allowed: {allowUnpin}");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in Unpin command handler: {ex.Message}", ex);
                var errorDialog = new BIManageRevit.BIManage.Views.Bindings.ErrorDialog(
                    windowTitle: "Pin Protection",
                    headerText: "Unpin Protection Error",
                    message: "Failed to process unpin protection:",
                    details: ex.Message);
                BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(errorDialog);
                errorDialog.ShowDialog();
            }
        }

        /// <summary>
        /// Perform normal unpin for unprotected elements
        /// </summary>
        private void PerformNormalUnpin(Document doc, List<Element> elements)
        {
            using (Transaction trans = new Transaction(doc, "Unpin Elements"))
            {
                trans.Start();
                try
                {
                    foreach (var element in elements)
                    {
                        if (element != null && element.IsValidObject && element.Pinned)
                        {
                            element.Pinned = false;
                        }
                    }
                    trans.Commit();
                    Logger?.LogDebug($"Unpinned {elements.Count} elements");
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    Logger?.LogError($"Failed to unpin elements: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Process protected elements according to their protection mode
        /// Returns true if unpin should be allowed, false to block
        /// </summary>
        private bool ProcessProtectedElements(Document doc, List<ProtectedPinInfo> protectedElements)
        {
            // Group by protection mode
            var byMode = protectedElements.GroupBy(p => p.ProtectionModeType);

            // Get highest protection mode (Prevent > Guide > Monitor)
            ProtectionMode highestMode = byMode.Max(g => g.Key);
            var elementsAtHighestMode = byMode.First(g => g.Key == highestMode).ToList();

            bool isAdmin = _isAdminCheck();
            string revitUsername = GetRevitUsername();

            switch (highestMode)
            {
                case ProtectionMode.Notify:
                    return HandleMonitorMode(doc, elementsAtHighestMode, revitUsername);

                case ProtectionMode.Assist:
                    return HandleGuideMode(doc, elementsAtHighestMode, revitUsername, isAdmin);

                case ProtectionMode.Protect:
                    return HandlePreventMode(doc, elementsAtHighestMode, revitUsername, isAdmin);

                default:
                    Logger?.LogWarning($"Unknown protection mode: {highestMode}");
                    return true; // Fail open
            }
        }

        /// <summary>
        /// Handle Monitor mode: Log but allow unpin
        /// </summary>
        private bool HandleMonitorMode(Document doc, List<ProtectedPinInfo> elements, string username)
        {
            Logger?.LogInfo($"Monitor mode: Allowing unpin of {elements.Count} elements by {username}");

            // Log to audit repository
            LogUnpinAction(elements, username, allowed: true, mode: ProtectionMode.Notify, doc: doc,
                reason: "Monitor mode - unpin allowed");

            // Remove protection metadata (element is being unpinned)
            using (Transaction trans = new Transaction(doc, "Clear Pin Protection"))
            {
                trans.Start();
                PinProtectionStorage.ClearProtection(doc, elements.Select(p => p.RevitElement!));
                trans.Commit();
            }

            // Deactivate in database
            DeactivateProtectionInDatabase(doc, elements, username, "Monitor mode - unpin allowed");

            return true; // Allow unpin
        }

        /// <summary>
        /// Handle Guide mode: Show warning, require acknowledgment
        /// </summary>
        private bool HandleGuideMode(Document doc, List<ProtectedPinInfo> elements, string username, bool isAdmin)
        {
            // Build element list
            string elementList = string.Join("\n", elements.Take(5).Select(p =>
                $"  • {p.Name}\n    Protected by: {p.ProtectedBy}\n    Reason: {p.AdminComment}"));

            if (elements.Count > 5)
                elementList += $"\n  ... and {elements.Count - 5} more";

            // Show unified Assist dialog for pin protection
            var evalResult = new BIManage.Core.Rules.Models.RuleEvaluationResult
            {
                FinalMode = BIManage.Core.Rules.Models.ProtectionMode.Assist,
                CombinedMessage = $"You are about to unpin {elements.Count} protected element(s).\n\n{elementList}",
                RequireComment = true
            };
            var pinElementIds = elements.Where(e => e.RevitElement != null).Select(e => e.RevitElement!.Id).ToList();
            var dialog = new BIManage.Views.Protection.GuideDialog(evalResult, pinElementIds);
            dialog.SetProtectMode(BIManage.ViewModels.Protection.ProtectionType.Pin, "Unpin Protected Element");
            BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);
            bool? result = dialog.ShowDialog();
            var userAllows = result == true && dialog.UserAllowed;

            if (!userAllows)
            {
                Logger?.LogInfo($"Guide mode: User {username} cancelled unpin of {elements.Count} elements");
                LogUnpinAction(elements, username, allowed: false, mode: ProtectionMode.Assist, doc: doc,
                    reason: "User cancelled", userComment: dialog.UserComment);

                // Re-pin elements that were unpinned
                using (Transaction trans = new Transaction(doc, "Restore Pin"))
                {
                    trans.Start();
                    foreach (var elem in elements)
                    {
                        if (elem.RevitElement != null && !elem.RevitElement.Pinned)
                        {
                            elem.RevitElement.Pinned = true;
                        }
                    }
                    trans.Commit();
                }

                return false; // Block unpin
            }

            // User confirmed - allow unpin
            Logger?.LogInfo($"Guide mode: User {username} confirmed unpin of {elements.Count} elements");
            LogUnpinAction(elements, username, allowed: true, mode: ProtectionMode.Assist, doc: doc,
                reason: "User confirmed");

            // Remove protection metadata
            using (Transaction trans = new Transaction(doc, "Clear Pin Protection"))
            {
                trans.Start();
                PinProtectionStorage.ClearProtection(doc, elements.Select(p => p.RevitElement!));
                trans.Commit();
            }

            // Deactivate in database
            DeactivateProtectionInDatabase(doc, elements, username, "Guide mode - user confirmed");

            return true; // Allow unpin
        }

        /// <summary>
        /// Handle Prevent mode: Block unless OTP override
        /// Shows OTP input dialog for authorization
        /// </summary>
        private bool HandlePreventMode(Document doc, List<ProtectedPinInfo> elements, string username, bool isAdmin)
        {
            // Build element list
            string elementList = string.Join("\n", elements.Take(5).Select(p =>
                $"  • {p.Name}\n    Protected by: {p.ProtectedBy}\n    Reason: {p.AdminComment}"));

            if (elements.Count > 5)
                elementList += $"\n  ... and {elements.Count - 5} more";

            // Require a comment for Pin Protection unpin (always, and especially when any
            // protected element was saved with IsRequireCommentForUnpin = true).
            bool requireComment = true;

            // Show unified Protect dialog for pin protection with OTP
            var evalResult = new BIManage.Core.Rules.Models.RuleEvaluationResult
            {
                FinalMode = BIManage.Core.Rules.Models.ProtectionMode.Protect,
                CombinedMessage = $"These {elements.Count} element(s) are protected and require authorization to unpin.\n\n{elementList}",
                RequireComment = requireComment
            };
            var pinElementIds = elements.Where(e => e.RevitElement != null).Select(e => e.RevitElement!.Id).ToList();
            var dialog = new BIManage.Views.Protection.GuideDialog(evalResult, pinElementIds);
            dialog.SetProtectMode(BIManage.ViewModels.Protection.ProtectionType.Pin, "Unpin Protected Element");
            BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);
            bool? dialogResult = dialog.ShowDialog();

            if (dialogResult != true || !dialog.OverrideAllowed)
            {
                Logger?.LogInfo($"Prevent mode: User {username} cancelled OTP entry");
                LogUnpinAction(elements, username, allowed: false, mode: ProtectionMode.Protect, doc: doc,
                    reason: "User cancelled", userComment: dialog.UserComment);
                RestorePins(doc, elements);
                return false;
            }

            // Get OTP code from dialog
            string? otpCode = dialog.OtpCode;

            // Validate OTP
            bool otpValid = ValidateOtp(otpCode, username, elements);

            if (!otpValid)
            {
                var errorDialog = new BIManageRevit.BIManage.Views.Bindings.ErrorDialog(
                    windowTitle: "Pin Protection",
                    headerText: "Invalid OTP",
                    message: "The OTP code you entered is invalid or has expired.",
                    details: "Please contact your BIM administrator for a new code.");
                BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(errorDialog);
                errorDialog.ShowDialog();

                Logger?.LogWarning($"Prevent mode: Invalid OTP '{otpCode}' entered by {username}");
                LogUnpinAction(elements, username, allowed: false, mode: ProtectionMode.Protect, doc: doc,
                    overrideType: "OTP", reason: $"Invalid OTP: {otpCode}", userComment: dialog.UserComment);
                RestorePins(doc, elements);
                return false;
            }

            // OTP valid - allow unpin
            var userComment = dialog.UserComment;
            if (!string.IsNullOrWhiteSpace(userComment))
            {
                Logger?.LogInfo($"User comment: {userComment}");
            }
            Logger?.LogInfo($"Prevent mode: User {username} authorized via OTP for {elements.Count} elements");

            // Generate audit log ID (GUID) before logging so we can link evidence back
            var auditLogId = Guid.NewGuid().ToString();

            LogUnpinAction(elements, username, allowed: true, mode: ProtectionMode.Protect, doc: doc,
                overrideType: "OTP", reason: $"OTP authorized: {otpCode}", userComment: userComment,
                auditLogId: auditLogId);
            var revitContext = _revitContextGetter?.Invoke();
            var sessionId = revitContext?.SessionId.ToString() ?? Guid.NewGuid().ToString();
            var documentPath = doc.PathName ?? doc.Title;

            // Get element IDs for tracking
            var elementIds = elements
                .Where(e => e.RevitElement != null)
                .Select(e => e.RevitElement!.Id.GetIdValue())
                .ToList();

            // Create UserEventData to hold screenshots and metadata
            var userEventData = UserEventData.CreateForPinProtection(
                auditLogId, sessionId, documentPath,
                username, isAdmin, elementIds);
            userEventData.OtpCodeUsed = otpCode;

            // Capture before screenshot as Base64 (Win32 PrintWindow → PNG → Base64)
            var screenshotService = _screenshotServiceGetter?.Invoke();
            if (screenshotService != null)
            {
                try
                {
                    userEventData.BeforeImageBytes = screenshotService.CaptureRevitWindowAsBase64();
                    if (userEventData.BeforeImageBytes != null)
                    {
                        Logger?.LogInfo($"Pin protection: Captured before screenshot as Base64 ({userEventData.BeforeImageBytes.Length} chars)");
                    }
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"Failed to capture screenshot: {ex.Message}");
                }
            }

            // Unpin elements and remove protection metadata
            using (Transaction trans = new Transaction(doc, "Unpin Protected Elements"))
            {
                trans.Start();
                foreach (var elem in elements)
                {
                    if (elem.RevitElement != null && elem.RevitElement.Pinned)
                    {
                        elem.RevitElement.Pinned = false;
                    }
                }
                PinProtectionStorage.ClearProtection(doc, elements.Select(p => p.RevitElement!));
                trans.Commit();
            }

            // Register unpinned elements for after-screenshot tracking
            // When these elements are modified, an "after" screenshot will be captured
            if (_unpinnedElementTracker != null)
            {
                _unpinnedElementTracker.TrackElements(elementIds, userEventData);
            }

            // Deactivate in database with OTP tracking
            DeactivateProtectionInDatabase(doc, elements, username, "Prevent mode - OTP authorized", otpCode,
                userEventData.BeforeImageBytes);

            var successDialog = new BIManageRevit.BIManage.Views.Bindings.UnpinSuccessDialog(
                elements.Count,
                "OTP verified successfully.");
            BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(successDialog);
            successDialog.ShowDialog();

            return true;
        }


        /// <summary>
        /// Validate OTP code against the database
        /// </summary>
        private bool ValidateOtp(string otpCode, string username, List<ProtectedPinInfo> elements)
        {
            if (_otpRepository == null)
            {
                Logger?.LogWarning("OTP repository not available - OTP validation disabled");
                return false;
            }

            try
            {
                // Validate OTP - using "pin_protection" as command_id for this feature
                var otp = Task.Run(() => _otpRepository.ValidateAndConsumeOtpAsync(
                    otpCode,
                    username,
                    ruleId: null,
                    commandId: "pin_protection")).GetAwaiter().GetResult();

                if (otp != null)
                {
                    Logger?.LogInfo($"OTP validated: Code={otpCode}, GeneratedBy={otp.GeneratedBy}, Reason={otp.Reason}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"OTP validation error: {ex.Message}", ex);
            }

            return false;
        }

        /// <summary>
        /// Restore pins on elements (re-pin after cancellation)
        /// </summary>
        private void RestorePins(Document doc, List<ProtectedPinInfo> elements)
        {
            using (Transaction trans = new Transaction(doc, "Restore Pin"))
            {
                trans.Start();
                foreach (var elem in elements)
                {
                    if (elem.RevitElement != null && !elem.RevitElement.Pinned)
                    {
                        elem.RevitElement.Pinned = true;
                    }
                }
                trans.Commit();
            }
        }

        /// <summary>
        /// Extract family type name from a pin protection element.
        /// Resolves via RevitElement or doc.GetElement fallback.
        /// </summary>
        private static string? GetPinFamilyType(ProtectedPinInfo? pin, Document? doc)
        {
            try
            {
                if (pin == null || doc == null) return null;
#if REVIT2021 || REVIT2022 || REVIT2023
                var element = pin.RevitElement ?? doc.GetElement(new ElementId((int)pin.ElementId));
#else
                var element = pin.RevitElement ?? doc.GetElement(new ElementId(pin.ElementId));
#endif
                if (element is FamilyInstance fi)
                    return $"{fi.Symbol.Family.Name}: {fi.Symbol.Name}";
                return element?.GetType().Name;
            }
            catch { return null; }
        }

        /// <summary>
        /// Log unpin action to logger and audit repository
        /// </summary>
        private void LogUnpinAction(
            List<ProtectedPinInfo> elements,
            string username,
            bool allowed,
            ProtectionMode mode,
            Document? doc = null,
            string? overrideType = null,
            string? reason = null,
            string? userComment = null,
            string? auditLogId = null)
        {
            // Log to file logger
            string action = allowed ? "allowed" : "blocked";
            string overrideInfo = overrideType != null ? $", override: {overrideType}" : "";

            foreach (var elem in elements)
            {
                Logger?.LogInfo($"Pin Protection Unpin {action}: Element {elem.ElementId} ({elem.Name}), " +
                              $"Mode: {mode}, User: {username}, Protected by: {elem.ProtectedBy}{overrideInfo}, " +
                              $"Reason: {reason ?? "none"}");
            }

            // Log to audit repository
            if (_auditRepository != null)
            {
                try
                {
                    var elementIds = string.Join(",", elements.Select(e => e.ElementId));
                    var sessionId = _revitContextGetter?.Invoke()?.SessionId.ToString();

                    // Get model GUID from document
                    string modelGuid = null;
                    if (doc != null)
                    {
                        try { modelGuid = BIManage.Revit.Helpers.ModelGuidHelper.GetModelGuid(doc, Logger); }
                        catch { /* Non-critical */ }
                    }

                    // Use first element's ElementGuid as protection_id (links to pin_protection table)
                    var firstPin = elements.FirstOrDefault();
                    var protectionId = firstPin?.ElementGuid;

                    // Convert PinProtection.Models.ProtectionMode to Rules.Models.ProtectionMode
                    var rulesMode = mode switch
                    {
                        ProtectionMode.Notify => Core.Rules.Models.ProtectionMode.Notify,
                        ProtectionMode.Assist => Core.Rules.Models.ProtectionMode.Assist,
                        ProtectionMode.Protect => Core.Rules.Models.ProtectionMode.Protect,
                        _ => Core.Rules.Models.ProtectionMode.Notify
                    };

                    var auditEntry = new ProtectionAuditEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        UserName = username,
                        ModelGuid = modelGuid,
                        ProtectionId = protectionId,
                        CommandName = "Unpin",
                        Mode = rulesMode,
                        Action = allowed ? ProtectionAction.Allowed : ProtectionAction.Blocked,
                        ElementIds = elementIds,
                        ElementCount = elements.Count,
                        ElementCategory = firstPin?.Category,
                        ElementFamilyType = GetPinFamilyType(firstPin, doc),
                        ElementName = firstPin?.Name,
                        Reason = reason,
                        UserComment = userComment,
                        OverrideMethod = overrideType,
                        EventSource = "Pin Protection",
                        SessionId = sessionId,
                        AuditLogId = auditLogId
                    };

                    _auditRepository.SaveAuditEntry(auditEntry);
                    Logger?.LogDebug($"Audit entry logged for Unpin: {action} (EventSource: PinProtection)");
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"Failed to log audit entry: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Deactivate protection records in database by setting is_active = 0.
        /// Also notifies the backend API via DELETE for each deactivated protection.
        /// Called when protection is revoked via unpin operation.
        /// Fire and forget - does not block UI operation if sync fails.
        /// </summary>
        private async void DeactivateProtectionInDatabase(
            Document doc,
            List<ProtectedPinInfo> elements,
            string deactivatedBy,
            string reason = null,
            string otpCodeUsed = null,
            string? beforeImageBase64 = null)
        {
            if (_pinProtectionRepository == null)
                return;

            try
            {
                var modelGuid = BIManage.Revit.Helpers.ModelGuidHelper.GetModelGuid(doc, Logger);

                foreach (var element in elements)
                {
                    // Build user comment with screenshot indicator
                    var userComment = !string.IsNullOrEmpty(beforeImageBase64)
                        ? $"Screenshot captured ({beforeImageBase64.Length} chars Base64)"
                        : null;

                    await _pinProtectionRepository.DeactivateProtectionAsync(
                        modelGuid,
                        element.ElementGuid,
                        deactivatedBy,
                        reason,
                        otpCodeUsed,
                        userComment);

                    Logger?.LogDebug($"Deactivated protection in database for element {element.ElementGuid}");

                    // Notify backend API
                    var syncService = _pinProtectionSyncGetter?.Invoke();
                    if (syncService != null)
                    {
                        await syncService.DeletePinProtectionAsync(modelGuid, element.ElementGuid);
                    }
                }
            }
            catch (Exception ex)
            {
                // Don't fail the UI operation if sync fails
                Logger?.LogError($"Failed to deactivate protection: {ex.Message}", ex);
            }
        }

    }
}
