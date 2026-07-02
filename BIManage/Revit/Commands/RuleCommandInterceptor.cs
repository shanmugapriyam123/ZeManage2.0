using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Common.Helpers;
using BIManage.Core.Features;
using BIManage.Core.Protection;
using BIManage.Core.Rules;
using BIManage.Core.Rules.Models;
using BIManage.Core.Protection.Models;
using BIManage.Core.Evidence;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Helpers;
using BIManage.Revit.Protection;
using BIManage.ViewModels.Protection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Interop;

namespace BIManage.Revit.Commands
{
    /// <summary>
    /// Integrates rule evaluation with command interception
    /// Wraps CommandInterceptionService to add rule-based protection
    /// </summary>
    public class RuleCommandInterceptor : IRuleCommandInterceptor
    {
        private readonly ICommandInterceptionService _commandService;
        private readonly IRuleService _ruleService;
        private readonly IFeatureToggleService _featureToggleService;
        private readonly ILogger _logger;
        private readonly AuditRepository _auditRepository;
        private readonly AsyncAuditQueue _asyncAuditQueue;
        private readonly PasswordManager _passwordManager;
        private readonly BIManage.Core.Identity.IUserService _userService;
        private readonly ModelessDialogManager _modelessDialogManager;
        private readonly IScreenshotService _screenshotService;
        private readonly EvidenceRepository _evidenceRepository;
        private readonly OtpRepository _otpRepository;
        private readonly string _sessionId;
        private readonly ScreenshotThrottleCache _screenshotThrottle;

        // Track current evaluation context
        private RuleEvaluationResult? _currentEvaluation;
        private List<Element>? _currentElements;
        private int _currentCommandId;
        private string _currentCommandName;
        private string? _currentAuditLogId;

        // Feature flags
        private bool _enableModelessGuidance = false; // Set to true to enable modeless mode

        public RuleCommandInterceptor(
            ICommandInterceptionService commandService,
            IRuleService ruleService,
            IFeatureToggleService featureToggleService,
            ILogger logger,
            string sessionId,
            AuditRepository auditRepository = null,
            AsyncAuditQueue asyncAuditQueue = null,
            PasswordManager passwordManager = null,
            BIManage.Core.Identity.IUserService userService = null,
            ModelessDialogManager modelessDialogManager = null,
            IScreenshotService screenshotService = null,
            EvidenceRepository evidenceRepository = null,
            OtpRepository otpRepository = null)
        {
            _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
            _ruleService = ruleService ?? throw new ArgumentNullException(nameof(ruleService));
            _featureToggleService = featureToggleService ?? throw new ArgumentNullException(nameof(featureToggleService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _auditRepository = auditRepository; // Optional for Phase 2 (legacy sync)
            _asyncAuditQueue = asyncAuditQueue; // Preferred async queue for performance
            _passwordManager = passwordManager; // Optional for Phase 4
            _userService = userService; // Optional for RBAC tracking
            _modelessDialogManager = modelessDialogManager; // Optional for modeless guidance
            _screenshotService = screenshotService; // Optional for evidence capture
            _evidenceRepository = evidenceRepository; // Optional for evidence tracking
            _otpRepository = otpRepository; // Optional for OTP override in Prevent mode
            _screenshotThrottle = new ScreenshotThrottleCache(throttleWindowSeconds: 5); // 5-second throttle window
        }

        /// <summary>
        /// Result of pure-logic rule evaluation. Used by the bypass detector
        /// (DocumentChanged path) which has no BeforeExecutedEventArgs to cancel.
        /// </summary>
        public sealed class RuleBypassResult
        {
            public bool HasMatch { get; init; }
            public bool Allowed { get; init; } = true;
            public ProtectionMode Mode { get; init; }

            public static RuleBypassResult NoMatch { get; } = new() { HasMatch = false, Allowed = true };
        }

        /// <summary>
        /// Pure-logic rule evaluation for the bypass detector path (drag, nudge,
        /// Ctrl+drag, property palette, etc.). Mirrors OnBeforeExecuted's logic but
        /// accepts the element list from DocumentChanged and a bypass-source label
        /// that prefixes audit Reason. Returns a result instead of mutating
        /// BeforeExecutedEventArgs (which cannot be fabricated from DocumentChanged).
        /// </summary>
        public RuleBypassResult EvaluateForBypass(
            int commandId,
            string commandName,
            Document document,
            IList<Element> elements,
            string bypassSource)
        {
            return EvaluateCore(commandId, commandName, document, elements, bypassSource);
        }

        /// <summary>
        /// Handle BeforeExecuted event with rule evaluation.
        /// Thin wrapper around <see cref="EvaluateCore"/> that applies the result to e.Cancel.
        /// </summary>
        public void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e)
        {
            try
            {
                var document = e.ActiveDocument;
                if (document == null)
                {
                    _logger?.LogWarning("No active document in BeforeExecuted");
                    return;
                }

                var commandId = GetCommandIdAsInt(e.CommandId);
                if (commandId == 0)
                {
                    _logger?.LogWarning($"Could not convert command ID: {e.CommandId?.Name}");
                    return;
                }

                var commandName = CommandNameResolver.GetFriendlyName(e.CommandId?.Name) ?? "Unknown";
                var elements = GetSelectedElements(document);

                var result = EvaluateCore(commandId, commandName, document, elements, bypassSource: null);
                if (!result.Allowed)
                {
                    if (e.Cancellable)
                    {
                        e.Cancel = true;
                        _logger?.LogInfo($"Command cancelled by rule eval (mode: {result.Mode})");
                    }
                    else
                    {
                        _logger?.LogWarning("Command not cancellable, cannot enforce rule");
                    }
                }

                // Suppress bypass detector only when the user was shown a dialog and made a
                // decision (Guide/Prevent modes). For Monitor (Notify), the ribbon path only
                // logged — it showed no dialog — so the bypass detector must still run for the
                // actual operation (e.g. the element picked during the Align tool's pick loop).
                // Without this guard, a Monitor-mode rule matching leftover pre-selection when
                // Align is clicked sets the suppress flag, and the bypass detector then skips
                // the DocumentChanged event for the element that actually gets aligned.
                if (result.HasMatch && !e.Cancel && result.Mode != ProtectionMode.Notify)
                {
                    BIManage.Revit.Protection.CommandBypassAuditGate.Suppress();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnBeforeExecuted wrapper: {ex.Message}", ex);
                // Don't block command on error
            }
        }

        /// <summary>
        /// Shared pure-logic evaluation core. Runs feature-toggle guards, rule eval,
        /// and dispatches to the mode handlers. Returns a result the caller can act on
        /// (set e.Cancel for ribbon path; queue restoration/deletion for bypass path).
        /// </summary>
        private RuleBypassResult EvaluateCore(
            int commandId,
            string commandName,
            Document document,
            IList<Element>? elementsInput,
            string? bypassSource)
        {
            try
            {
                if (!_featureToggleService.IsFeatureEnabled("RuleEvaluation"))
                {
                    _logger?.LogDebug("Rule evaluation disabled by feature toggle");
                    return RuleBypassResult.NoMatch;
                }

                if (_featureToggleService.IsGlobalPaused)
                {
                    _logger?.LogDebug("Rule evaluation paused globally");
                    return RuleBypassResult.NoMatch;
                }

                if (document == null) return RuleBypassResult.NoMatch;
                if (commandId == 0) return RuleBypassResult.NoMatch;

                _currentCommandId = commandId;
                _currentCommandName = commandName;

                var elements = elementsInput?.ToList() ?? new List<Element>();
                if (elements.Count == 0)
                {
                    _logger?.LogDebug($"No elements to evaluate for command {commandId}");
                    return RuleBypassResult.NoMatch;
                }

                _logger?.LogDebug($"Evaluating rules for {elements.Count} elements, command {commandId} ({commandName}) bypassSource={bypassSource ?? "<ribbon>"}");

                var result = _ruleService.EvaluateRulesBatch(elements, commandId);

                if (result == null)
                {
                    _logger?.LogError("Rule evaluation returned null result - allowing (fail-open)");
                    return RuleBypassResult.NoMatch;
                }

                if (result.HasMatch && (result.MatchedRules == null || result.MatchedRules.Count == 0))
                {
                    _logger?.LogError("Inconsistent evaluation result: HasMatch=true but no matched rules - allowing (fail-open)");
                    return RuleBypassResult.NoMatch;
                }

                _currentEvaluation = result;
                _currentElements = elements;

                if (!result.HasMatch)
                {
                    _logger?.LogDebug($"No rules matched for command {commandId}");
                    return RuleBypassResult.NoMatch;
                }

                _logger?.LogInfo($"Rules matched: {result.MatchedRules.Count}, Final mode: {result.FinalMode}");

                if (result.ShouldBlock)
                {
                    bool allowed = HandlePreventMode(result, bypassSource);
                    return new RuleBypassResult { HasMatch = true, Allowed = allowed, Mode = ProtectionMode.Protect };
                }
                if (result.ShouldGuide)
                {
                    bool allowed = HandleGuideMode(result, bypassSource);
                    return new RuleBypassResult { HasMatch = true, Allowed = allowed, Mode = ProtectionMode.Assist };
                }
                HandleMonitorMode(result, bypassSource);
                return new RuleBypassResult { HasMatch = true, Allowed = true, Mode = ProtectionMode.Notify };
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in EvaluateCore: {ex.Message}", ex);
                return RuleBypassResult.NoMatch; // fail-open
            }
        }

        /// <summary>
        /// Handle Executed event (for after-state capture)
        /// </summary>
        public void OnExecuted(object sender, ExecutedEventArgs e)
        {
            try
            {
                // Only process if we have a current evaluation
                if (_currentEvaluation == null || !_currentEvaluation.HasMatch)
                    return;

                var document = e.ActiveDocument;
                if (document == null)
                    return;

                // Capture after screenshot if needed, linking to the same auditLogId
                if (_currentEvaluation.CaptureAfterScreenshot)
                {
                    _logger?.LogDebug("Capturing after screenshot");
                    CaptureScreenshot("after", _currentEvaluation, _currentAuditLogId);
                }

                // Log completion
                _logger?.LogInfo($"Command completed: {_currentEvaluation.MatchedRules.Count} rules matched");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in Executed handler: {ex.Message}", ex);
            }
            finally
            {
                // Clear context
                _currentEvaluation = null;
                _currentElements = null;
                _currentCommandId = 0;
                _currentCommandName = null;
                _currentAuditLogId = null;
            }
        }

        /// <summary>
        /// Handle Prevent mode - block command, show blocking dialog.
        /// Returns true if allowed (override granted), false if blocked.
        /// </summary>
        private bool HandlePreventMode(RuleEvaluationResult result, string? bypassSource = null)
        {
            _logger?.LogWarning($"Command blocked by Prevent mode: {result.CombinedMessage}");

            // Generate auditLogId upfront — used as PK for audit entry and FK on evidence
            var auditLogId = Guid.NewGuid().ToString();
            _currentAuditLogId = auditLogId;

            // Capture before screenshot, linking evidence to the audit entry via auditLogId
            if (result.CaptureBeforeScreenshot)
            {
                _logger?.LogDebug("Capturing before screenshot (Prevent mode)");
                CaptureScreenshot("before", result, auditLogId);
            }

            var overrideAllowed = ShowPreventDialog(result, auditLogId, bypassSource);

            if (overrideAllowed)
                _logger?.LogWarning("Command allowed via override");
            else
                _logger?.LogInfo("Prevent mode: command not allowed");

            return overrideAllowed;
        }

        /// <summary>
        /// Handle Guide mode - show warning dialog (modal or modeless), user decides.
        /// Returns true if user allowed, false if user cancelled.
        /// </summary>
        private bool HandleGuideMode(RuleEvaluationResult result, string? bypassSource = null)
        {
            _logger?.LogInfo($"Guide mode triggered: {result.CombinedMessage}");

            // Generate auditLogId upfront — used as PK for audit entry and FK on evidence
            var auditLogId = Guid.NewGuid().ToString();
            _currentAuditLogId = auditLogId;

            // Capture before screenshot, linking evidence to the audit entry via auditLogId
            if (result.CaptureBeforeScreenshot)
            {
                _logger?.LogDebug("Capturing before screenshot (Guide mode)");
                CaptureScreenshot("before", result, auditLogId);
            }

            // Check if modeless guidance is enabled
            if (_enableModelessGuidance && _modelessDialogManager != null)
            {
                // Show modeless (non-blocking) guidance
                var elementIds = _currentElements?.Select(el => el.Id).ToList() ?? new List<ElementId>();
                var dialogId = _modelessDialogManager.ShowModelessGuidance(result, elementIds);

                _logger?.LogInfo($"Modeless guidance shown: {dialogId}");

                // Log as monitored action (command proceeds without blocking)
                LogGuideAction(result, _currentElements, userAllowed: true, userComment: "Modeless guidance shown", auditLogId: auditLogId, bypassSource: bypassSource);

                // Command proceeds without user intervention
                return true;
            }

            // Show modal guide dialog - user chooses to proceed or cancel
            var userAllows = ShowGuideDialog(result, auditLogId, bypassSource);

            if (!userAllows)
            {
                _logger?.LogInfo("Guide mode: user cancelled");
                LogGuideAction(result, _currentElements, userAllowed: false, userComment: null, auditLogId: auditLogId, bypassSource: bypassSource);
            }
            else
            {
                _logger?.LogInfo("User allowed command in Guide mode");
                // After-state will be captured in Executed handler (ribbon path only)
            }
            return userAllows;
        }

        /// <summary>
        /// Handle Monitor mode - passive tracking, no user intervention.
        /// </summary>
        private void HandleMonitorMode(RuleEvaluationResult result, string? bypassSource = null)
        {
            _logger?.LogInfo($"Monitor mode: tracking command execution");

            // Generate auditLogId upfront — used as PK for audit entry and FK on evidence
            var auditLogId = Guid.NewGuid().ToString();
            _currentAuditLogId = auditLogId;

            // Capture before screenshot, linking evidence to the audit entry via auditLogId
            if (result.CaptureBeforeScreenshot)
            {
                _logger?.LogDebug("Capturing before screenshot (Monitor mode)");
                CaptureScreenshot("before", result, auditLogId);
            }

            // Log to audit repository with auditLogId as PK
            LogMonitorAction(result, _currentElements, auditLogId, bypassSource);

            // No user intervention - command proceeds
            // After-state will be captured in Executed handler (ribbon path only)
        }

        /// <summary>
        /// Log Monitor mode action to audit repository (async, non-blocking)
        /// </summary>
        private void LogMonitorAction(RuleEvaluationResult result, List<Element> elements, string? auditLogId, string? bypassSource = null)
        {
            if (_asyncAuditQueue == null && _auditRepository == null)
            {
                _logger?.LogDebug("No audit logging mechanism available, skipping audit log");
                return;
            }

            try
            {
                var elementIds = string.Join(",", elements.Select(e => e.Id.GetIdValue()));
                var firstRule = result.MatchedRules.FirstOrDefault();
                var currentUser = _userService?.CurrentUser;
                var firstElement = elements.FirstOrDefault();

                var auditEntry = new ProtectionAuditEntry
                {
                    AuditLogId = auditLogId,
                    Timestamp = DateTime.UtcNow,
                    UserName = firstElement?.Document?.Application?.Username ?? Environment.UserName,
                    WasCompanyAdmin = currentUser?.IsCompanyAdmin ?? false,
                    WasProjectAdmin = currentUser?.IsProjectAdmin ?? false,
                    ModelGuid = GetActiveModelGuid(elements),
                    ProtectionId = firstRule?.RuleId,
                    CommandName = _currentCommandName,
                    Mode = ProtectionMode.Notify,
                    Action = ProtectionAction.Allowed,
                    ElementIds = elementIds,
                    ElementCount = elements.Count,
                    ElementCategory = firstElement?.Category?.Name,
                    ElementFamilyType = GetFamilyTypeName(firstElement),
                    ElementName = firstElement?.Name,
                    Reason = string.IsNullOrEmpty(bypassSource)
                        ? result.CombinedMessage
                        : $"{bypassSource}: {result.CombinedMessage}",
                    EventSource = "Rule Management",
                    SessionId = _sessionId,
                    SentMail = !(firstRule?.SendEmail ?? false)
                };

                if (_asyncAuditQueue != null)
                {
                    _asyncAuditQueue.Enqueue(auditEntry);
                    _logger?.LogDebug($"Monitor action enqueued (async) for {elements.Count} elements");
                }
                else
                {
                    _auditRepository.SaveAuditEntry(auditEntry);
                    _logger?.LogDebug($"Monitor action logged (sync) for {elements.Count} elements");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to log monitor action: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Show prevent dialog using EventRestrictionDialog — same UX as EventRestriction.
        /// Shows OTP button always; shows Admin Override button when user is admin and the
        /// matched rule has AllowAdminOverride = true.
        /// </summary>
        private bool ShowPreventDialog(RuleEvaluationResult result, string? auditLogId, string? bypassSource = null)
        {
            try
            {
                var currentUser = _userService?.CurrentUser;
                var isAdmin = currentUser?.IsCompanyAdmin == true || currentUser?.IsProjectAdmin == true;
                var adminOverrideEnabled = result.MatchedRules?.Any(r => r.AllowAdminOverride) == true;
                var ruleName = result.MatchedRules?.FirstOrDefault()?.Name ?? "Protected Action";
                var username = currentUser?.UserName ?? Environment.UserName;
                var message = !string.IsNullOrWhiteSpace(result.CombinedMessage)
                    ? result.CombinedMessage
                    : "This action is restricted by a protection rule.";

                var dialog = new BIManageRevit.BIManage.Views.Protection.EventRestrictionDialog(
                    protectionName: ruleName,
                    message: message,
                    showAdminOverride: isAdmin && adminOverrideEnabled,
                    requireComment: result.RequireComment);

                BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);
                dialog.ShowDialog();

                switch (dialog.UserAction)
                {
                    case BIManageRevit.BIManage.Views.Protection.EventRestrictionAction.AdminOverride:
                        _logger?.LogInfo($"Rule Prevent: Admin override confirmed by '{username}' for rule '{ruleName}'");
                        LogPreventAction(result, _currentElements, overrideUsed: true, auditLogId, "AdminPrivilege", bypassSource);
                        return true;

                    case BIManageRevit.BIManage.Views.Protection.EventRestrictionAction.EnterOtp:
                        var otpDialog = new BIManageRevit.BIManage.Views.Bindings.OtpInputDialog("Rule Management");
                        BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(otpDialog);
                        if (otpDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(otpDialog.OtpCode))
                        {
                            LogPreventAction(result, _currentElements, overrideUsed: false, auditLogId, null, bypassSource);
                            return false;
                        }
                        var otpAllowed = ValidateOtpCode(otpDialog.OtpCode, result);
                        if (otpAllowed)
                        {
                            try
                            {
                                var successDialog = new BIManageRevit.BIManage.Views.Bindings.UnpinSuccessDialog(
                                    _currentElements?.Count ?? 0,
                                    "OTP verified successfully.",
                                    windowTitle: "Rule Management",
                                    headerText: "Authorization Successful");
                                BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(successDialog);
                                successDialog.ShowDialog();
                            }
                            catch (Exception popupEx)
                            {
                                _logger?.LogDebug($"Rule OTP success popup failed: {popupEx.Message}");
                            }
                            LogPreventAction(result, _currentElements, overrideUsed: true, auditLogId, "OTP", bypassSource);
                        }
                        else
                        {
                            _logger?.LogWarning("OTP validation failed for rule override");
                            new TaskDialog("Rule Management")
                            {
                                MainInstruction = "Invalid OTP",
                                MainContent = "The OTP code is invalid or expired. Please request a new OTP from your administrator.",
                                CommonButtons = TaskDialogCommonButtons.Ok
                            }.Show();
                            LogPreventAction(result, _currentElements, overrideUsed: false, auditLogId, null, bypassSource);
                        }
                        return otpAllowed;

                    default: // Cancel
                        _logger?.LogInfo($"Rule Prevent: User '{username}' cancelled for rule '{ruleName}'");
                        LogPreventAction(result, _currentElements, overrideUsed: false, auditLogId, null, bypassSource);
                        return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error showing Prevent dialog: {ex.Message}", ex);
                new TaskDialog("Action Prevented")
                {
                    MainInstruction = "This action is not permitted",
                    MainContent = result.CombinedMessage,
                    CommonButtons = TaskDialogCommonButtons.Ok
                }.Show();
                return false;
            }
        }

        /// <summary>
        /// Validate OTP code via OtpRepository API
        /// </summary>
        private bool ValidateOtpCode(string otpCode, RuleEvaluationResult result)
        {
            if (_otpRepository == null)
            {
                _logger?.LogWarning("OTP repository not available - OTP validation disabled");
                return false;
            }

            try
            {
                var username = Environment.UserName;
                var ruleId = result.MatchedRules.FirstOrDefault()?.RuleId;

                var otp = System.Threading.Tasks.Task.Run(() => _otpRepository.ValidateAndConsumeOtpAsync(
                    otpCode,
                    username,
                    ruleId: ruleId,
                    commandId: _currentCommandName)).GetAwaiter().GetResult();

                if (otp != null)
                {
                    _logger?.LogInfo($"OTP validated successfully for user {username}, rule {ruleId}");
                    return true;
                }

                _logger?.LogWarning($"OTP validation failed for user {username}");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"OTP validation error: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Log Prevent mode action to audit repository (async, non-blocking)
        /// </summary>
        private void LogPreventAction(RuleEvaluationResult result, List<Element> elements, bool overrideUsed, string? auditLogId, string overrideMethod = null, string? bypassSource = null)
        {
            if (_asyncAuditQueue == null && _auditRepository == null)
            {
                _logger?.LogDebug("No audit logging mechanism available, skipping audit log");
                return;
            }

            try
            {
                var elementIds = string.Join(",", elements.Select(e => e.Id.GetIdValue()));
                var firstRule = result.MatchedRules.FirstOrDefault();
                var currentUser = _userService?.CurrentUser;
                var firstElement = elements.FirstOrDefault();

                var auditEntry = new ProtectionAuditEntry
                {
                    AuditLogId = auditLogId,
                    Timestamp = DateTime.UtcNow,
                    UserName = firstElement?.Document?.Application?.Username ?? Environment.UserName,
                    WasCompanyAdmin = currentUser?.IsCompanyAdmin ?? false,
                    WasProjectAdmin = currentUser?.IsProjectAdmin ?? false,
                    ModelGuid = GetActiveModelGuid(elements),
                    ProtectionId = firstRule?.RuleId,
                    CommandName = _currentCommandName,
                    Mode = ProtectionMode.Protect,
                    Action = overrideUsed ? ProtectionAction.Override : ProtectionAction.Blocked,
                    ElementIds = elementIds,
                    ElementCount = elements.Count,
                    ElementCategory = firstElement?.Category?.Name,
                    ElementFamilyType = GetFamilyTypeName(firstElement),
                    ElementName = firstElement?.Name,
                    Reason = string.IsNullOrEmpty(bypassSource)
                        ? result.CombinedMessage
                        : $"{bypassSource}: {result.CombinedMessage}",
                    OverrideMethod = overrideUsed ? (overrideMethod ?? "AdminPassword") : null,
                    EventSource = "Rule Management",
                    SessionId = _sessionId,
                    SentMail = !(firstRule?.SendEmail ?? false)
                };

                if (_asyncAuditQueue != null)
                {
                    _asyncAuditQueue.Enqueue(auditEntry);
                    _logger?.LogDebug($"Prevent action enqueued (async): {auditEntry.Action}");
                }
                else
                {
                    _auditRepository.SaveAuditEntry(auditEntry);
                    _logger?.LogDebug($"Prevent action logged (sync): {auditEntry.Action}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to log prevent action: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Show guide dialog (user chooses)
        /// </summary>
        private bool ShowGuideDialog(RuleEvaluationResult result, string? auditLogId, string? bypassSource = null)
        {
            try
            {
                // Use WPF dialog for Guide mode
                var elementIds = _currentElements?.Select(e => e.Id).ToList() ?? new List<ElementId>();
                var commandName = _currentCommandName ?? "Unknown";
                var dialog = new BIManage.Views.Protection.GuideDialog(
                    result, elementIds,
                    commandName: commandName,
                    commandDescription: elementIds.Count > 0 ? $"Affecting {elementIds.Count} element(s)" : null);

                // Configure as Assist mode with Rule protection type
                var contentTitle = result.MatchedRules?.FirstOrDefault()?.Name ?? commandName;
                dialog.SetAssistMode(ProtectionType.Rule, contentTitle);

                // Set action type based on command name
                var actionType = DetermineActionType(commandName);
                dialog.SetActionType(actionType);

                // Set Revit as owner so dialog appears in front
                BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);

                var dialogResult = dialog.ShowDialog();
                var userAllows = dialogResult == true && dialog.UserAllowed;

                // Log the action with user comment if provided
                if (userAllows && !string.IsNullOrWhiteSpace(dialog.UserComment))
                {
                    _logger?.LogInfo($"User comment: {dialog.UserComment}");
                }

                _logger?.LogDebug($"Guide dialog result: {dialogResult}, User allows: {userAllows}");

                // Log to audit repository
                LogGuideAction(result, _currentElements, userAllows, dialog.UserComment, auditLogId, bypassSource);

                return userAllows;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error showing Guide dialog: {ex.Message}", ex);
                
                // Fallback to TaskDialog if WPF fails
                var fallbackDialog = new TaskDialog("Confirmation Required")
                {
                    MainInstruction = "Please confirm this action",
                    MainContent = result.CombinedMessage,
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No
                };

                var fallbackResult = fallbackDialog.Show();
                return fallbackResult == TaskDialogResult.Yes;
            }
        }

        /// <summary>
        /// Determine action type based on command name for button styling.
        /// </summary>
        private static ConfirmationActionType DetermineActionType(string commandName)
        {
            var name = commandName?.ToLowerInvariant() ?? "";

            if (name.Contains("delete") || name.Contains("remove") || name.Contains("demolish") ||
                name.Contains("unpin") || name.Contains("unlock"))
                return ConfirmationActionType.Delete;

            if (name.Contains("move") || name.Contains("rotate") || name.Contains("mirror") ||
                name.Contains("align") || name.Contains("edit") || name.Contains("modify") || name.Contains("change"))
                return ConfirmationActionType.Modify;

            return ConfirmationActionType.Continue;
        }

        /// <summary>
        /// Log Guide mode action to audit repository (async, non-blocking)
        /// </summary>
        private void LogGuideAction(RuleEvaluationResult result, List<Element> elements, bool userAllowed, string userComment, string? auditLogId = null, string? bypassSource = null)
        {
            if (_asyncAuditQueue == null && _auditRepository == null)
            {
                _logger?.LogDebug("No audit logging mechanism available, skipping audit log");
                return;
            }

            try
            {
                var elementIds = string.Join(",", elements.Select(e => e.Id.GetIdValue()));
                var firstRule = result.MatchedRules.FirstOrDefault();
                var currentUser = _userService?.CurrentUser;
                var firstElement = elements.FirstOrDefault();

                var auditEntry = new ProtectionAuditEntry
                {
                    AuditLogId = auditLogId,
                    Timestamp = DateTime.UtcNow,
                    UserName = firstElement?.Document?.Application?.Username ?? Environment.UserName,
                    WasCompanyAdmin = currentUser?.IsCompanyAdmin ?? false,
                    WasProjectAdmin = currentUser?.IsProjectAdmin ?? false,
                    ModelGuid = GetActiveModelGuid(elements),
                    ProtectionId = firstRule?.RuleId,
                    CommandName = _currentCommandName,
                    Mode = ProtectionMode.Assist,
                    Action = userAllowed ? ProtectionAction.Allowed : ProtectionAction.Cancelled,
                    ElementIds = elementIds,
                    ElementCount = elements.Count,
                    ElementCategory = firstElement?.Category?.Name,
                    ElementFamilyType = GetFamilyTypeName(firstElement),
                    ElementName = firstElement?.Name,
                    Reason = string.IsNullOrEmpty(bypassSource)
                        ? result.CombinedMessage
                        : $"{bypassSource}: {result.CombinedMessage}",
                    UserComment = userComment,
                    EventSource = "Rule Management",
                    SessionId = _sessionId,
                    SentMail = !(firstRule?.SendEmail ?? false)
                };

                if (_asyncAuditQueue != null)
                {
                    _asyncAuditQueue.Enqueue(auditEntry);
                    _logger?.LogDebug($"Guide action enqueued (async): {auditEntry.Action}");
                }
                else
                {
                    _auditRepository.SaveAuditEntry(auditEntry);
                    _logger?.LogDebug($"Guide action logged (sync): {auditEntry.Action}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to log guide action: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Extract family type name from a Revit element.
        /// For FamilyInstance: "FamilyName: TypeName". Otherwise: Revit class name.
        /// </summary>
        private static string? GetFamilyTypeName(Element element)
        {
            try
            {
                if (element == null) return null;
                if (element is FamilyInstance fi)
                    return $"{fi.Symbol.Family.Name}: {fi.Symbol.Name}";
                return element.GetType().Name;
            }
            catch { return null; }
        }

        /// <summary>
        /// Get model GUID from the document that owns the elements being evaluated
        /// </summary>
        private string GetActiveModelGuid(List<Element> elements)
        {
            try
            {
                var doc = elements?.FirstOrDefault()?.Document;
                return doc != null ? ModelGuidHelper.GetModelGuid(doc, _logger) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Get selected elements from document
        /// </summary>
        private List<Element> GetSelectedElements(Document document)
        {
            try
            {
                var uiDocument = new UIDocument(document);
                var selection = uiDocument.Selection;
                var elementIds = selection.GetElementIds();

                if (elementIds.Count == 0)
                    return new List<Element>();

                var elements = elementIds
                    .Select(id => document.GetElement(id))
                    .Where(e => e != null)
                    .ToList();

                return elements;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting selected elements: {ex.Message}", ex);
                return new List<Element>();
            }
        }

        /// <summary>
        /// Convert RevitCommandId to integer for rule matching.
        /// Name-based lookup is the primary strategy because it maps directly to the same
        /// PostableCommand integers that rules store (via CommandNameResolver), matching how
        /// CommandProtectionBinding resolves commands. Int extraction is the fallback for
        /// commands whose names are not in the resolver dictionary.
        /// </summary>
        private int GetCommandIdAsInt(RevitCommandId? commandId)
        {
            if (commandId == null)
                return 0;

            // Primary: name-based mapping — produces PostableCommand integers that match
            // what ParseAndResolveCommands stores in rule.CommandIds.
            // This is the same mechanism used by CommandProtectionBinding (which works correctly).
            var nameBasedId = MapCommandNameToId(commandId.Name);
            if (nameBasedId != 0)
                return nameBasedId;

            // Secondary: direct cast — only accept positive values because PostableCommand
            // integers are always positive; negative values indicate a wrong type (e.g. ElementId)
            try
            {
                var id = (int)commandId.Id;
                if (id > 0)
                    return id;
            }
            catch { /* fall through to next strategy */ }

            // Tertiary: Convert.ToInt32 handles long, short, and IConvertible types
            try
            {
                var id = Convert.ToInt32(commandId.Id);
                if (id > 0)
                    return id;
            }
            catch { /* fall through */ }

            // Quaternary: reflection for API types that wrap the integer
            try
            {
                var commandIdObj = commandId.Id;
                var type = commandIdObj.GetType();

                // Try IntegerValue property (Revit 2021-2024 API)
                var intValueProp = type.GetProperty("IntegerValue");
                if (intValueProp != null)
                {
                    var id = Convert.ToInt32(intValueProp.GetValue(commandIdObj));
                    if (id > 0) return id;
                }

                // Try Value property (Revit 2025+ API, returns long)
                var valueProp = type.GetProperty("Value");
                if (valueProp != null)
                {
                    var id = Convert.ToInt32(valueProp.GetValue(commandIdObj));
                    if (id > 0) return id;
                }

                // Try ToString() parsing
                var commandIdString = commandIdObj.ToString();
                if (int.TryParse(commandIdString, out int parsedId) && parsedId > 0)
                    return parsedId;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Reflection extraction failed for {commandId.Name}: {ex.Message}");
            }

            _logger?.LogWarning($"Cannot resolve command ID for: {commandId.Name}");
            return 0;
        }

        /// <summary>
        /// Map command name to PostableCommand ID.
        /// Delegates to shared CommandNameResolver utility.
        /// </summary>
        private int MapCommandNameToId(string? commandName)
        {
            if (string.IsNullOrEmpty(commandName))
                return 0;

            return CommandNameResolver.ResolveNameToId(commandName);
        }

        /// <summary>
        /// Capture screenshot and record evidence metadata.
        /// The auditLogId links evidence back to the audit log entry (FK on evidence → PK on audit).
        /// </summary>
        private void CaptureScreenshot(string stage, RuleEvaluationResult result, string? auditLogId = null)
        {
            // Skip if screenshot service not available
            if (_screenshotService == null || _evidenceRepository == null)
            {
                _logger?.LogDebug("Screenshot capture skipped: services not available");
                return;
            }

            try
            {
                // Check throttle - prevent duplicate screenshots within time window
                var ruleId = result.MatchedRules.FirstOrDefault()?.RuleId ?? "0";
                var elementIds = _currentElements != null ? string.Join(",", _currentElements.Select(e => e.Id.GetIdValue())) : string.Empty;

                if (!_screenshotThrottle.ShouldCapture(ruleId, _currentCommandId, elementIds, stage))
                {
                    _logger?.LogDebug($"Screenshot throttled for rule {ruleId}, command {_currentCommandId}, stage {stage}");
                    return;
                }

                // Generate unique evidence ID
                var evidenceId = Guid.NewGuid().ToString("N");

                // Capture screenshot with integrity hash asynchronously (fire and forget)
                var captureTask = _screenshotService.CaptureRevitWindowWithHashAsync(evidenceId, _sessionId, stage);

                captureTask.ContinueWith(async task =>
                {
                    try
                    {
                        var captureResult = await task;
                        if (captureResult == null)
                        {
                            _logger?.LogWarning($"Screenshot capture failed for evidence {evidenceId}");
                            return;
                        }

                        var (filePath, sha256Hash) = captureResult.Value;

                        // Get file info
                        var fileInfo = new FileInfo(filePath);

                        // Record evidence metadata with hash — AuditLogId links to the audit entry
                        var evidence = new EvidenceRepository.EvidenceCapture
                        {
                            EvidenceId = evidenceId,
                            SessionId = _sessionId,
                            AuditLogId = auditLogId,
                            ProtectionType = "rule",
                            CaptureType = "screenshot",
                            CaptureStage = stage,
                            CapturedAt = DateTime.Now,
                            RuleId = result.MatchedRules.FirstOrDefault()?.RuleId,
                            RuleName = result.MatchedRules.FirstOrDefault()?.Name,
                            CommandId = _currentCommandId.ToString(),
                            CommandName = _currentCommandName,
                            ElementIds = _currentElements != null ? string.Join(",", _currentElements.Select(e => e.Id.GetIdValue())) : null,
                            ElementCount = _currentElements?.Count ?? 0,
                            FilePath = filePath,
                            FileSizeBytes = fileInfo.Length,
                            FileFormat = "png",
                            FileHashSha256 = sha256Hash,
                            UploadStatus = "pending",
                            Metadata = $"{{\"revit_window\":true,\"stage\":\"{stage}\",\"protection_mode\":\"{result.FinalMode}\"}}"
                        };

                        await _evidenceRepository.RecordEvidenceCaptureAsync(evidence);
                        _logger?.LogInfo($"Evidence recorded: {evidenceId} ({stage}, {fileInfo.Length / 1024} KB)");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"Failed to record evidence metadata: {ex.Message}", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to initiate screenshot capture: {ex.Message}", ex);
            }
        }
    }
}