using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Rules.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.ViewModels.Protection;
using BIManage.Views.Protection;

namespace BIManage.Revit.Protection
{
    /// <summary>
    /// Handles event intervention logic
    /// Shows dialogs and determines if event should be blocked
    /// </summary>
    public class EventInterventionHandler
    {
        private readonly ILogger? _logger;
        private readonly AuditRepository? _auditRepository;
        private readonly OtpRepository? _otpRepository;
        private readonly Func<bool>? _isAdminCheck;

        public EventInterventionHandler(
            ILogger? logger,
            AuditRepository? auditRepository = null,
            OtpRepository? otpRepository = null,
            Func<bool>? isAdminCheck = null)
        {
            _logger = logger;
            _auditRepository = auditRepository;
            _otpRepository = otpRepository;
            _isAdminCheck = isAdminCheck;
        }

        /// <summary>
        /// Process event intervention based on settings.
        /// </summary>
        public EventInterventionResult ProcessIntervention(
            EventProtectionSettings settings,
            EventContext context)
        {
            return ProcessIntervention(settings, context, null, calledFromRevitEvent: false);
        }

        /// <summary>
        /// Process event intervention with custom message.
        /// </summary>
        public EventInterventionResult ProcessIntervention(
            EventProtectionSettings settings,
            EventContext context,
            string? additionalInfo)
        {
            return ProcessIntervention(settings, context, additionalInfo, calledFromRevitEvent: false);
        }

        /// <summary>
        /// Process event intervention called from a Revit document event (e.g. DocumentSavingAs).
        /// Revit holds internal document locks during these events, so any WPF ShowDialog() call
        /// that pumps the message loop on the UI thread risks a deadlock (Revit WndProc tries to
        /// acquire the same lock). Passing calledFromRevitEvent=true routes all dialog display
        /// through a dedicated STA thread so the calling (Revit) thread simply blocks with no
        /// message pumping, eliminating the deadlock.
        /// </summary>
        public EventInterventionResult ProcessIntervention(
            EventProtectionSettings settings,
            EventContext context,
            string? additionalInfo,
            bool calledFromRevitEvent)
        {
            if (settings == null || !settings.Enabled)
            {
                return EventInterventionResult.Allow();
            }

            switch (settings.Mode)
            {
                case InterventionMode.Notify:
                    return ProcessMonitorMode(settings, context);

                case InterventionMode.Assist:
                    return ProcessGuideMode(settings, context, additionalInfo, calledFromRevitEvent);

                case InterventionMode.Protect:
                    return ProcessPreventMode(settings, context, additionalInfo, calledFromRevitEvent);

                default:
                    _logger?.LogWarning($"Unknown intervention mode: {settings.Mode}");
                    return EventInterventionResult.Allow();
            }
        }

        /// <summary>
        /// Runs a dialog factory on a new STA thread and blocks the caller until the dialog closes.
        /// Use this when calling from a Revit document event to avoid deadlock: Revit holds
        /// internal locks during events, and ShowDialog()'s nested message pump can cause Revit's
        /// WndProc to re-enter those locks. A separate STA thread with its own message pump
        /// avoids this — Revit's main thread just blocks on Join() with no message processing.
        /// </summary>
        private static T RunOnStaThread<T>(Func<T> dialogAction)
        {
            T result = default!;
            var thread = new System.Threading.Thread(() =>
            {
                result = dialogAction();
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            return result;
        }

        /// <summary>
        /// Monitor mode - just log, allow event
        /// </summary>
        private EventInterventionResult ProcessMonitorMode(
            EventProtectionSettings settings,
            EventContext context)
        {
            _logger?.LogInfo($"Monitor: Event protection '{settings.ProtectionName}' triggered " +
                $"(File: {context.FilePath ?? "N/A"})");

            // Audit logging happens in the caller (EventRegistryService)
            return EventInterventionResult.Allow("Logged for monitoring");
        }

        /// <summary>
        /// Guide mode - show confirmation dialog with Proceed/Cancel.
        /// When RequireComment is true, swap the simple TaskDialog for the WPF
        /// EventRestrictionDialog so the user can enter a reason that lands in the audit log.
        /// </summary>
        private EventInterventionResult ProcessGuideMode(
            EventProtectionSettings settings,
            EventContext context,
            string? additionalInfo,
            bool calledFromRevitEvent = false)
        {
            try
            {
                var message = BuildMessage(settings, context, additionalInfo);

                (bool userAllows, string userComment) ShowGuideDialog()
                {
                    var evalResult = new RuleEvaluationResult
                    {
                        FinalMode = ProtectionMode.Assist,
                        CombinedMessage = message,
                        RequireComment = settings.RequireComment
                    };
                    evalResult.MatchedRules.Add(new Rule
                    {
                        Name = settings.ProtectionName,
                        Description = settings.DummyCommandId,
                        Mode = ProtectionMode.Assist,
                        Message = message
                    });

                    var dialog = new GuideDialog(
                        evalResult,
                        new List<ElementId>(),
                        commandName: settings.ProtectionName,
                        commandDescription: "This action will be logged for audit purposes.");
                    dialog.SetAssistMode(ProtectionType.Event, settings.ProtectionName);

                    if (!calledFromRevitEvent)
                    {
                        BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);
                    }

                    var result = dialog.ShowDialog();
                    return (result == true && dialog.UserAllowed, dialog.UserComment ?? string.Empty);
                }

                bool userAllows;
                string userComment;

                if (calledFromRevitEvent)
                    (userAllows, userComment) = RunOnStaThread(ShowGuideDialog);
                else
                    (userAllows, userComment) = ShowGuideDialog();

                if (userAllows)
                {
                    _logger?.LogInfo($"Guide: User approved '{settings.ProtectionName}'{(string.IsNullOrEmpty(userComment) ? "" : " with comment")}");
                    return EventInterventionResult.Allow("User confirmed", userComment);
                }

                _logger?.LogInfo($"Guide: User cancelled '{settings.ProtectionName}'");
                return EventInterventionResult.Cancel("User cancelled", userComment);
            }
            catch (Exception ex)
            {
                // Fail-CLOSED. Previously this catch returned Allow(...) which silently
                // bypassed the protection if the WPF dialog threw (e.g. the
                // "Invalid window handle" failure documented in
                // BIManageRevit_20260522_1034.log). For Guide mode (Assist), block the
                // event and notify the user so they can retry — matches the
                // CommandProtectionBinding.HandleGuideMode shape.
                string diag = "diag=n/a";
                try
                {
                    var procHwnd = Process.GetCurrentProcess().MainWindowHandle;
                    var cachedHwnd = BIManage.Revit.Helpers.RevitWindowHelper.GetOwnerHwnd();
                    var tid = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    var apt = System.Threading.Thread.CurrentThread.GetApartmentState();
                    diag = $"procHwnd=0x{procHwnd.ToInt64():X} cachedHwnd=0x{cachedHwnd.ToInt64():X} tid={tid} apt={apt}";
                }
                catch { }
                try
                {
                    _logger?.LogError(
                        $"EventInterventionHandler: Guide dialog threw for '{settings.ProtectionName}' — blocking event (fail-closed): {ex.Message} | {diag}",
                        ex);
                }
                catch { }
                try
                {
                    Autodesk.Revit.UI.TaskDialog.Show(
                        "ZeManage",
                        $"The confirmation dialog for '{settings.ProtectionName}' could not open. The action has been cancelled — please try again.");
                }
                catch { }
                return EventInterventionResult.Cancel($"Blocked (Assist: dialog error, fail-closed): {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// Prevent mode - block unless admin override or OTP
        /// </summary>
        private EventInterventionResult ProcessPreventMode(
            EventProtectionSettings settings,
            EventContext context,
            string? additionalInfo,
            bool calledFromRevitEvent = false)
        {
            try
            {
                var isAdmin = _isAdminCheck?.Invoke() ?? false;
                var username = Environment.UserName;

                // Admin override check
                if (isAdmin && settings.AllowAdminOverride)
                {
                    _logger?.LogInfo($"Prevent: Admin override available for '{settings.ProtectionName}'");

                    var adminMessage = BuildMessage(settings, context, additionalInfo);

                    (BIManageRevit.BIManage.Views.Protection.EventRestrictionAction action, string? comment) ShowAdminDialog()
                    {
                        var d = new BIManageRevit.BIManage.Views.Protection.EventRestrictionDialog(
                            settings.ProtectionName, adminMessage, showAdminOverride: true, requireComment: settings.RequireComment);
                        d.ShowDialog();
                        return (d.UserAction, d.UserComment);
                    }

                    var (adminAction, adminComment) = calledFromRevitEvent
                        ? RunOnStaThread(ShowAdminDialog)
                        : ShowAdminDialog();

                    if (adminAction == BIManageRevit.BIManage.Views.Protection.EventRestrictionAction.AdminOverride)
                    {
                        _logger?.LogInfo($"Prevent: Admin {username} approved '{settings.ProtectionName}'");
                        return EventInterventionResult.AllowWithOverride("Admin override confirmed", adminComment);
                    }
                    else if (adminAction == BIManageRevit.BIManage.Views.Protection.EventRestrictionAction.EnterOtp)
                    {
                        var otpResult = calledFromRevitEvent
                            ? RunOnStaThread(() => ShowOtpDialogAndValidate(username, settings.DummyCommandId))
                            : ShowOtpDialogAndValidate(username, settings.DummyCommandId);
                        if (otpResult)
                        {
                            _logger?.LogInfo($"Prevent: Admin {username} authorized via OTP for '{settings.ProtectionName}'");
                            return EventInterventionResult.AllowWithOverride("OTP override confirmed", adminComment);
                        }
                        _logger?.LogWarning($"Prevent: Admin {username} OTP failed for '{settings.ProtectionName}'");
                        return EventInterventionResult.Cancel("OTP validation failed", adminComment);
                    }
                    else
                    {
                        _logger?.LogInfo($"Prevent: Admin {username} cancelled '{settings.ProtectionName}'");
                        return EventInterventionResult.Cancel("Admin cancelled override", adminComment);
                    }
                }

                // Non-admin: Show WPF restricted dialog with OTP option
                var blockMessage = BuildMessage(settings, context, additionalInfo);

                (BIManageRevit.BIManage.Views.Protection.EventRestrictionAction action, string? comment) ShowRestrictDialog()
                {
                    var d = new BIManageRevit.BIManage.Views.Protection.EventRestrictionDialog(
                        settings.ProtectionName, blockMessage, showAdminOverride: false, requireComment: settings.RequireComment);
                    d.ShowDialog();
                    return (d.UserAction, d.UserComment);
                }

                var (restrictAction, restrictComment) = calledFromRevitEvent
                    ? RunOnStaThread(ShowRestrictDialog)
                    : ShowRestrictDialog();

                if (restrictAction == BIManageRevit.BIManage.Views.Protection.EventRestrictionAction.EnterOtp)
                {
                    var otpResult = calledFromRevitEvent
                        ? RunOnStaThread(() => ShowOtpDialogAndValidate(username, settings.DummyCommandId))
                        : ShowOtpDialogAndValidate(username, settings.DummyCommandId);
                    if (otpResult)
                    {
                        _logger?.LogInfo($"Prevent: User {username} authorized via OTP for '{settings.ProtectionName}'");
                        return EventInterventionResult.AllowWithOverride("OTP override confirmed", restrictComment);
                    }
                    else
                    {
                        _logger?.LogWarning($"Prevent: User {username} OTP failed for '{settings.ProtectionName}'");
                        return EventInterventionResult.Cancel("OTP validation failed", restrictComment);
                    }
                }

                _logger?.LogWarning($"Prevent: User {username} blocked from '{settings.ProtectionName}'");
                return EventInterventionResult.Cancel("Action restricted - user cancelled", restrictComment);
            }
            catch (Exception ex)
            {
                // Fail-CLOSED. The previous version of this catch returned
                // EventInterventionResult.Allow(...) with a "fail open" comment, which
                // silently bypassed every event protection (CAD Import, CAD Explode,
                // Print, Save-As, Transfer Standards, etc.) whenever the WPF dialog
                // threw — the same "Invalid window handle" class of failure documented
                // in BIManageRevit_20260522_1034.log. Protection integrity must NOT
                // depend on the WPF dialog opening successfully.
                string diag = "diag=n/a";
                try
                {
                    var procHwnd = Process.GetCurrentProcess().MainWindowHandle;
                    var cachedHwnd = BIManage.Revit.Helpers.RevitWindowHelper.GetOwnerHwnd();
                    var tid = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    var apt = System.Threading.Thread.CurrentThread.GetApartmentState();
                    diag = $"procHwnd=0x{procHwnd.ToInt64():X} cachedHwnd=0x{cachedHwnd.ToInt64():X} tid={tid} apt={apt}";
                }
                catch { }
                try
                {
                    _logger?.LogError(
                        $"EventInterventionHandler: Prevent dialog threw for '{settings.ProtectionName}' — blocking event (fail-closed): {ex.Message} | {diag}",
                        ex);
                }
                catch { }
                try
                {
                    Autodesk.Revit.UI.TaskDialog.Show(
                        "ZeManage",
                        $"The authorization dialog for '{settings.ProtectionName}' could not open. The action has been cancelled — please try again.");
                }
                catch { }
                return EventInterventionResult.Cancel($"Blocked (Prevent: dialog error, fail-closed): {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// Show OTP input dialog and validate the code
        /// </summary>
        private bool ShowOtpDialogAndValidate(string username, string? commandId)
        {
            if (_otpRepository == null)
            {
                _logger?.LogWarning("OTP repository not available for event protection");
                return false;
            }

            try
            {
                var otpDialog = new BIManageRevit.BIManage.Views.Bindings.OtpInputDialog("Event Restriction");

                BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(otpDialog);

                bool? dialogResult = otpDialog.ShowDialog();

                if (dialogResult != true || string.IsNullOrWhiteSpace(otpDialog.OtpCode))
                {
                    return false;
                }

                var otpCode = otpDialog.OtpCode.Trim();

                // Validate OTP via repository (blocking call - we're on UI thread)
                var otp = Task.Run(() => _otpRepository.ValidateAndConsumeOtpAsync(
                    otpCode, username, ruleId: null, commandId: commandId))
                    .GetAwaiter().GetResult();

                if (otp != null)
                {
                    _logger?.LogInfo($"OTP validated for user {username}, command {commandId}");
                    ShowEventOtpSuccessPopup();
                    return true;
                }

                // OTP invalid - show error and allow retry
                otpDialog = new BIManageRevit.BIManage.Views.Bindings.OtpInputDialog("Event Restriction");
                otpDialog.ShowError("Invalid or expired OTP code. Please try again.");

                BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(otpDialog);

                dialogResult = otpDialog.ShowDialog();

                if (dialogResult != true || string.IsNullOrWhiteSpace(otpDialog.OtpCode))
                {
                    return false;
                }

                otpCode = otpDialog.OtpCode.Trim();

                var retryOtp = Task.Run(() => _otpRepository.ValidateAndConsumeOtpAsync(
                    otpCode, username, ruleId: null, commandId: commandId))
                    .GetAwaiter().GetResult();

                if (retryOtp != null)
                {
                    _logger?.LogInfo($"OTP validated (retry) for user {username}, command {commandId}");
                    ShowEventOtpSuccessPopup();
                    return true;
                }

                _logger?.LogWarning($"OTP validation failed (retry) for user {username}");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error during OTP validation: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Show success popup after a valid OTP for an event-restriction OTP override.
        /// Mirrors the success-popup behaviour shown by CommandProtectionBinding so the
        /// user gets the same confirmation feedback regardless of protection type.
        /// </summary>
        private void ShowEventOtpSuccessPopup()
        {
            try
            {
                var successDialog = new BIManageRevit.BIManage.Views.Bindings.UnpinSuccessDialog(
                    elementCount: 0,
                    message: "OTP verified successfully.",
                    windowTitle: "Event Restriction",
                    headerText: "Authorization Successful");
                BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(successDialog);
                successDialog.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Event OTP success popup failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Build intervention message
        /// </summary>
        private string BuildMessage(
            EventProtectionSettings settings,
            EventContext context,
            string? additionalInfo)
        {
            var message = settings.CustomMessage ?? GetDefaultMessage(settings, context);

            if (!string.IsNullOrEmpty(additionalInfo))
            {
                message += "\n\n" + additionalInfo;
            }

            // Add context details if available
            if (!string.IsNullOrEmpty(context.FilePath))
            {
                message += $"\n\nFile: {System.IO.Path.GetFileName(context.FilePath)}";
            }

            if (!string.IsNullOrEmpty(context.FileVersion))
            {
                message += $"\nFile Version: {context.FileVersion}";
            }

            return message;
        }

        /// <summary>
        /// Get default message for a protection
        /// </summary>
        private string GetDefaultMessage(EventProtectionSettings settings, EventContext context)
        {
            return settings.DummyCommandId switch
            {
                "Ze_OpenFileFromNonApprovedProtection" =>
                    "This file is being opened from a non-approved location.\n" +
                    "Please ensure you have permission to access files from this location.",

                "Ze_OpenCentralFileProtection" =>
                    "You are attempting to open the central file directly.\n" +
                    "It is recommended to create a local copy instead.\n\n" +
                    "Opening the central file directly may cause synchronization issues.",

                "Ze_ModelUpgradeProtection" =>
                    "This file will be upgraded to the current Revit version.\n" +
                    "This operation cannot be undone.\n\n" +
                    "Are you sure you want to continue?",

                "Ze_SaveOverEarlierFileVersionProtection" =>
                    "You are saving to an earlier file version.\n" +
                    "Some data may be lost or modified during this operation.\n\n" +
                    "Are you sure you want to continue?",

                "Ze_FamilyLibrarySettings" =>
                    "This family is being loaded from a non-approved library location.\n" +
                    "Please ensure you are using the approved company family library.",

                "Ze_FamilyLoadingProtection" =>
                    "You are about to overwrite a protected family.\n" +
                    "This family has been marked as protected by an administrator.",

                "Ze_FamilyVersionMismatch" =>
                    "This family was created in a different Revit version.\n" +
                    "Loading it may cause compatibility issues.",

                "Ze_SyncConflictDetection" =>
                    "Another user is currently synchronizing with the central model.\n" +
                    "Proceeding now may cause conflicts.",

                "Ze_CADImportProtection" =>
                    "You are attempting to import a CAD file.\n" +
                    "Importing CAD files may introduce non-native geometry and impact model performance.\n\n" +
                    "Consider linking the CAD file instead, or consult your BIM manager.",

                "Ze_CADExplodeProtection" =>
                    "You are attempting to explode an imported CAD instance.\n" +
                    "Exploding CAD files creates native Revit elements that cannot be easily updated.\n\n" +
                    "This action is restricted by your organization's BIM standards.",

                "Ze_EquipmentMirrorProtection" =>
                    "Mirroring equipment can invert flow/port directions and break MEP connections.\n" +
                    "Deselect equipment elements or use Copy instead.",

                "Ze_CopyMonitorPinProtection" =>
                    "You have completed a Copy/Monitor operation.\n" +
                    "Copy/Monitored elements should be pinned to prevent accidental modifications.\n\n" +
                    "Would you like to pin and protect these elements?",

                "Ze_TransferProjectStandardsProtection" =>
                    "You have transferred project standards from another file.\n" +
                    "This operation may have modified project registration settings.\n\n" +
                    "Project settings will be verified and updated if necessary.",

                "Ze_CADImportPinPrompt" =>
                    "You have imported CAD geometry into the model.\n" +
                    "Imported CAD should be pinned to prevent accidental movement.\n\n" +
                    "Would you like to pin and protect the imported CAD?",

                "Ze_RVTLinkPinPrompt" =>
                    "You have inserted a Revit link into the model.\n" +
                    "Revit links should be pinned to prevent accidental movement.\n\n" +
                    "Would you like to pin and protect the inserted Revit link?",

                // NOTE: Sync Control messages (SyncQueueControl, BackgroundSync, BackgroundRelinquish, IdleSync)
                // are NOT handled here. They are part of the Sync Control Module.

                _ => $"{settings.ProtectionName}\n\nDo you want to proceed?"
            };
        }
    }

    /// <summary>
    /// Context information for event protection evaluation
    /// </summary>
    public class EventContext
    {
        /// <summary>
        /// File path being opened/saved
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// File version (from BasicFileInfo)
        /// </summary>
        public string? FileVersion { get; set; }

        /// <summary>
        /// Current Revit version
        /// </summary>
        public string? CurrentRevitVersion { get; set; }

        /// <summary>
        /// Whether the file is workshared
        /// </summary>
        public bool IsWorkshared { get; set; }

        /// <summary>
        /// Whether this is the central file
        /// </summary>
        public bool IsCentralFile { get; set; }

        /// <summary>
        /// Family name (for family loading events)
        /// </summary>
        public string? FamilyName { get; set; }

        /// <summary>
        /// Family category (for family loading events)
        /// </summary>
        public string? FamilyCategory { get; set; }

        /// <summary>
        /// Active document
        /// </summary>
        public Document? Document { get; set; }

        /// <summary>
        /// Syncing user name (for sync conflict detection)
        /// </summary>
        public string? SyncingUser { get; set; }

        /// <summary>
        /// Additional custom data
        /// </summary>
        public object? CustomData { get; set; }
    }
}
