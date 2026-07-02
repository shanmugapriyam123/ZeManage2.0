using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Common.Helpers;
using BIManage.Core.Evidence;
using BIManage.Revit.PinProtection.Models;
using BIManage.Core.Protection.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Commands;
using BIManage.Revit.Commands.Bindings;
using BIManage.Revit.Context;
using BIManageRevit.BIManage.Views.Bindings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BIManage.Revit.PinProtection
{
    /// <summary>
    /// External event handler for floating pushpin icon protection.
    /// Processes pin/unpin actions that bypass standard command bindings.
    /// </summary>
    public class TogglePinExternalEventHandler : IExternalEventHandler
    {
        private readonly ILogger _logger;
        private readonly Func<bool> _isAdminCheck;
        private readonly OtpRepository? _otpRepository;
        private readonly PinProtectionRepository? _pinProtectionRepository;
        private readonly AuditRepository? _auditRepository;
        private readonly Func<IRevitContext?>? _revitContextGetter;
        private readonly Func<IScreenshotService?>? _screenshotServiceGetter;
        private readonly Func<EvidenceRepository?>? _evidenceRepositoryGetter;
        private readonly UnpinnedElementTracker? _unpinnedElementTracker;
        private readonly Func<CommandProtectionBinding?>? _commandProtectionGetter;
        private readonly Func<IRuleCommandInterceptor?>? _ruleInterceptorGetter;

        public TogglePinExternalEventHandler(
            ILogger logger,
            Func<bool> isAdminCheck,
            OtpRepository? otpRepository = null,
            PinProtectionRepository? pinProtectionRepository = null,
            AuditRepository? auditRepository = null,
            Func<IRevitContext?>? revitContextGetter = null,
            Func<IScreenshotService?>? screenshotServiceGetter = null,
            Func<EvidenceRepository?>? evidenceRepositoryGetter = null,
            UnpinnedElementTracker? unpinnedElementTracker = null,
            Func<CommandProtectionBinding?>? commandProtectionGetter = null,
            Func<IRuleCommandInterceptor?>? ruleInterceptorGetter = null)
        {
            _logger = logger;
            _isAdminCheck = isAdminCheck ?? throw new ArgumentNullException(nameof(isAdminCheck));
            _otpRepository = otpRepository;
            _pinProtectionRepository = pinProtectionRepository;
            _auditRepository = auditRepository;
            _revitContextGetter = revitContextGetter;
            _screenshotServiceGetter = screenshotServiceGetter;
            _evidenceRepositoryGetter = evidenceRepositoryGetter;
            _unpinnedElementTracker = unpinnedElementTracker;
            _commandProtectionGetter = commandProtectionGetter;
            _ruleInterceptorGetter = ruleInterceptorGetter;
        }

        public void Execute(UIApplication app)
        {
            try
            {
                // Get captured data from DocumentChanged event
                var info = TogglePinExternalEventInfo.Instance;
                var document = info.Document;
                var allModifiedElements = info.AllModifiedElements;

                if (document == null || !document.IsValidObject || allModifiedElements == null || !allModifiedElements.Any())
                {
                    _logger?.LogDebug("TogglePinExternalEvent: No valid elements to process");
                    info.Clear();
                    return;
                }

                _logger?.LogDebug($"TogglePinExternalEvent: Processing {allModifiedElements.Count()} elements");

                // Materialize once — used by both the new Command/Rule check and the existing pin-protection flow.
                var allValidElements = allModifiedElements
                    .Where(x => x != null && x.IsValidObject)
                    .ToList();
                if (allValidElements.Count == 0)
                {
                    _logger?.LogDebug("TogglePinExternalEvent: No valid modified elements");
                    info.Clear();
                    return;
                }

                // Each element that appears in a "Toggle Pin" transaction had its pinned state
                // flipped, so the *new* Pinned value tells us which direction was applied to it.
                // Split into two subsets so multi-selection with mixed pre-states (some pinned,
                // some unpinned) gets evaluated against the correct command key for each subset.
                var justPinned = allValidElements.Where(e => e.Pinned).ToList();
                var justUnpinned = allValidElements.Where(e => !e.Pinned).ToList();

                _logger?.LogInfo($"Toggle pin detected: {justPinned.Count} pinned + {justUnpinned.Count} unpinned");

                // PRIORITY 1+2: Command Protection / Rule evaluation on the Pin command for the
                // just-pinned subset, and on the Unpin command for the just-unpinned subset.
                // A protection on Unpin only fires when its subset is non-empty; a protection on
                // Pin only fires for the pin subset. Identical to how the ribbon bindings split
                // — each command id has its own dialog flow.
                //
                // Mixed-selection example: user selects 3 unpinned + 2 pinned elements and clicks
                // the ribbon pushpin → Revit pins the 3 (justPinned=3) and unpins the 2
                // (justUnpinned=2). Both subsets evaluate independently, so an Unpin-only Command
                // Protection blocks ONLY the 2 unpinned (we re-pin them) while the 3 newly-pinned
                // stay pinned (the Pin command had no protection).
                bool pinSubsetAllowed = true;
                bool unpinSubsetAllowed = true;

                if (justPinned.Count > 0)
                {
                    pinSubsetAllowed = EvaluateCommandProtectionAndRule(
                        "ID_LOCK_ELEMENTS", document, justPinned, "Floating pushpin (pin)");
                }
                if (justUnpinned.Count > 0)
                {
                    unpinSubsetAllowed = EvaluateCommandProtectionAndRule(
                        "ID_UNLOCK_ELEMENTS", document, justUnpinned, "Floating pushpin (unpin)");
                }

                // Revert only the subset(s) that got blocked. Independent so a blocked-Unpin
                // doesn't drag an allowed-Pin back with it.
                if (!pinSubsetAllowed)
                {
                    _logger?.LogWarning($"Command protection/rule blocked pin subset ({justPinned.Count}) — reverting to unpinned");
                    RevertToggle(document, justPinned, wasPinGesture: true);
                }
                if (!unpinSubsetAllowed)
                {
                    _logger?.LogWarning($"Command protection/rule blocked unpin subset ({justUnpinned.Count}) — reverting to pinned");
                    RevertToggle(document, justUnpinned, wasPinGesture: false);
                }

                // If any subset was blocked, the surviving elements still need a Pin-Protection
                // check (only on the now-unpinned ones that carry the metadata). Recompute
                // allValidElements snapshot — reverted elements have flipped back, so the filter
                // below picks them up correctly.

                // Existing flow: pin-protection metadata check (only meaningful for unpin direction
                // on elements that carry pin-protection ExtensibleStorage).
                var relevantElements = allValidElements
                    .Where(x => x.Pinned || IsElementProtected(x))
                    .ToList();

                if (!relevantElements.Any())
                {
                    _logger?.LogDebug("TogglePinExternalEvent: No pin-protected/pinned elements — command check passed, no pin-protection to evaluate");
                    info.Clear();
                    return;
                }

                // Get the primary element (from selection if multiple)
                var toggleElement = GetTogglePinElement(app, relevantElements);
                if (toggleElement == null || !toggleElement.IsValidObject)
                {
                    _logger?.LogDebug("TogglePinExternalEvent: Could not identify toggle element");
                    info.Clear();
                    return;
                }

                bool currentlyPinned = toggleElement.Pinned;
                bool allowAction = true;

                // SCENARIO 1: User just UNPINNED a pin-protected element
                if (!currentlyPinned && IsElementProtected(toggleElement))
                {
                    _logger?.LogDebug("Processing unpin via floating pushpin icon");

                    var protectedElements = PinProtectionStorage.GetProtectedFromSelection(relevantElements);
                    if (protectedElements.Any())
                    {
                        allowAction = ProcessProtectedUnpin(app, document, protectedElements);

                        if (!allowAction)
                        {
                            _logger?.LogInfo($"Unpin denied for element {toggleElement.Id}, requesting undo");
                        }
                    }
                }

                // If pin-protection denied, re-pin to revert.
                if (!allowAction)
                {
                    RequestUndo(document);
                }

                info.Clear();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in TogglePinExternalEvent: {ex.Message}", ex);
                TogglePinExternalEventInfo.Instance.Clear();
            }
        }

        public string GetName() => "BIManage Toggle Pin Event";

        /// <summary>
        /// Mirror CommandInterceptionService.OnBeforeCommandExecuted Priority 1 + Priority 2
        /// for the Pin or Unpin command. Identical machinery the ribbon path uses, so a Command
        /// Protection or Rule that triggers on Pin/Unpin fires from the floating pushpin path too.
        /// Returns true if both priorities allow, false if either blocked.
        /// </summary>
        private bool EvaluateCommandProtectionAndRule(
            string commandKey,
            Document document,
            List<Element> elements,
            string gestureSource)
        {
            try
            {
                // Priority 1 — Command Protection (command_settings table).
                var cmdProtection = _commandProtectionGetter?.Invoke();
                if (cmdProtection != null)
                {
                    var p1 = cmdProtection.EvaluateCommand(commandKey, document, elements, gestureSource);
                    if (p1.HasProtection)
                    {
                        if (!p1.Allowed)
                        {
                            _logger?.LogWarning($"[{gestureSource}] Command Protection blocked ({p1.Mode}: {p1.Reason})");
                            return false;
                        }
                        // Command Protection handled this command; don't double-evaluate the rule.
                        return true;
                    }
                }

                // Priority 2 — Rule evaluation.
                var ruleInterceptor = _ruleInterceptorGetter?.Invoke();
                if (ruleInterceptor != null)
                {
                    var commandIdInt = ResolveCommandIdInt(commandKey);
                    var p2 = ruleInterceptor.EvaluateForBypass(commandIdInt, commandKey, document, elements, gestureSource);
                    if (p2.HasMatch && !p2.Allowed)
                    {
                        _logger?.LogWarning($"[{gestureSource}] Rule blocked (mode: {p2.Mode})");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[{gestureSource}] EvaluateCommandProtectionAndRule error: {ex.Message}", ex);
                // Fail open — never block on an internal error.
            }
            return true;
        }

        /// <summary>
        /// Reverts the just-applied Toggle Pin direction. Symmetric counterpart to RequestUndo:
        ///  • Pin gesture blocked → unpin the elements that were just pinned.
        ///  • Unpin gesture blocked → pin the elements that were just unpinned.
        /// </summary>
        private void RevertToggle(Document document, List<Element> elements, bool wasPinGesture)
        {
            try
            {
                using (var trans = new Transaction(document, wasPinGesture ? "ZeManage Revert Pin" : "ZeManage Revert Unpin"))
                {
                    trans.Start();
                    foreach (var element in elements)
                    {
                        if (element == null || !element.IsValidObject) continue;
                        // Toggle back to the opposite of what the user did.
                        if (wasPinGesture && element.Pinned)
                        {
                            element.Pinned = false;
                        }
                        else if (!wasPinGesture && !element.Pinned)
                        {
                            element.Pinned = true;
                        }
                    }
                    trans.Commit();
                }
                _logger?.LogInfo($"Reverted floating-pushpin {(wasPinGesture ? "pin" : "unpin")} on {elements.Count} element(s)");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to revert floating-pushpin toggle: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Best-effort int command id for the Rule evaluator. Returns 0 when unresolvable.
        /// </summary>
        private static int ResolveCommandIdInt(string commandKey)
        {
            try
            {
                var rcid = RevitCommandId.LookupCommandId(commandKey);
                if (rcid != null)
                {
                    try { return (int)rcid.Id; }
                    catch
                    {
                        try { return Convert.ToInt32(rcid.Id); } catch { /* fall through */ }
                    }
                }
            }
            catch { /* fall through */ }
            return 0;
        }

        /// <summary>
        /// Check if element has protection metadata
        /// </summary>
        private bool IsElementProtected(Element element)
        {
            try
            {
                var protection = PinProtectionStorage.GetProtection(element);
                return protection != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Process protected elements according to their protection mode
        /// Returns true if unpin should be allowed, false to block
        /// </summary>
        private bool ProcessProtectedUnpin(UIApplication app, Document doc, List<ProtectedPinInfo> protectedElements)
        {
            // Group by protection mode
            var byMode = protectedElements.GroupBy(p => p.ProtectionModeType);

            // Get highest protection mode (Prevent > Guide > Monitor)
            ProtectionMode highestMode = byMode.Max(g => g.Key);
            var elementsAtHighestMode = byMode.First(g => g.Key == highestMode).ToList();

            bool isAdmin = _isAdminCheck();
            string revitUsername = GetRevitUsername(app);

            switch (highestMode)
            {
                case ProtectionMode.Notify:
                    return HandleMonitorMode(doc, elementsAtHighestMode, revitUsername);

                case ProtectionMode.Assist:
                    return HandleGuideMode(doc, elementsAtHighestMode, revitUsername, isAdmin);

                case ProtectionMode.Protect:
                    return HandlePreventMode(doc, elementsAtHighestMode, revitUsername, isAdmin);

                default:
                    _logger?.LogWarning($"Unknown protection mode: {highestMode}");
                    return true; // Fail open
            }
        }

        /// <summary>
        /// Handle Monitor mode: Log but allow unpin
        /// </summary>
        private bool HandleMonitorMode(Document doc, List<ProtectedPinInfo> elements, string username)
        {
            _logger?.LogInfo($"Monitor mode (floating pushpin): Allowing unpin of {elements.Count} elements by {username}");

            // Log action
            LogUnpinAction(elements, username, allowed: true, mode: ProtectionMode.Notify, doc: doc, source: "floating_pushpin");

            // Remove protection metadata (element is being unpinned)
            using (Transaction trans = new Transaction(doc, "Clear Pin Protection"))
            {
                trans.Start();
                PinProtectionStorage.ClearProtection(doc, elements.Select(p => p.RevitElement!));
                trans.Commit();
            }

            // Deactivate in database
            DeactivateProtectionInDatabase(doc, elements, username, "Monitor mode - unpin allowed via floating pushpin");

            return true; // Allow unpin
        }

        /// <summary>
        /// Handle Guide mode: Show warning, require acknowledgment.
        /// Uses the same unified GuideDialog as UnpinCommandBinding.HandleGuideMode
        /// so both paths (ribbon Unpin and floating pushpin icon) show the same UI.
        /// </summary>
        private bool HandleGuideMode(Document doc, List<ProtectedPinInfo> elements, string username, bool isAdmin)
        {
            // Build element list
            string elementList = string.Join("\n", elements.Take(5).Select(p =>
                $"  • {p.Name}\n    Protected by: {p.ProtectedBy}\n    Reason: {p.AdminComment}"));

            if (elements.Count > 5)
                elementList += $"\n  ... and {elements.Count - 5} more";

            var evalResult = new BIManage.Core.Rules.Models.RuleEvaluationResult
            {
                FinalMode = BIManage.Core.Rules.Models.ProtectionMode.Assist,
                CombinedMessage = $"You are about to unpin {elements.Count} protected element(s).\n\n{elementList}",
                RequireComment = true
            };
            var pinElementIds = elements
                .Where(e => e.RevitElement != null)
                .Select(e => e.RevitElement!.Id)
                .ToList();
            var dialog = new BIManage.Views.Protection.GuideDialog(evalResult, pinElementIds);
            dialog.SetProtectMode(BIManage.ViewModels.Protection.ProtectionType.Pin, "Unpin Protected Element");
            BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);
            bool? result = dialog.ShowDialog();
            var userAllows = result == true && dialog.UserAllowed;

            if (!userAllows)
            {
                _logger?.LogInfo($"Guide mode (floating pushpin): User {username} cancelled unpin of {elements.Count} elements");
                LogUnpinAction(elements, username, allowed: false, mode: ProtectionMode.Assist,
                    doc: doc, comment: string.IsNullOrWhiteSpace(dialog.UserComment) ? "User cancelled" : $"User cancelled | Comment: {dialog.UserComment}",
                    source: "floating_pushpin");

                return false; // Block unpin - will trigger undo (re-pin)
            }

            // User confirmed - allow unpin
            _logger?.LogInfo($"Guide mode (floating pushpin): User {username} confirmed unpin of {elements.Count} elements");
            LogUnpinAction(elements, username, allowed: true, mode: ProtectionMode.Assist,
                doc: doc, comment: string.IsNullOrWhiteSpace(dialog.UserComment) ? "User confirmed" : $"User confirmed | Comment: {dialog.UserComment}",
                source: "floating_pushpin");

            // Remove protection metadata
            using (Transaction trans = new Transaction(doc, "Clear Pin Protection"))
            {
                trans.Start();
                PinProtectionStorage.ClearProtection(doc, elements.Select(p => p.RevitElement!));
                trans.Commit();
            }

            // Deactivate in database
            DeactivateProtectionInDatabase(doc, elements, username, "Guide mode - user confirmed via floating pushpin");

            return true; // Allow unpin
        }

        /// <summary>
        /// Handle Prevent mode: Block unless OTP override.
        /// Uses the same unified GuideDialog in Protect mode as UnpinCommandBinding.HandlePreventMode
        /// so both paths (ribbon Unpin and floating pushpin icon) show the same UI.
        /// </summary>
        private bool HandlePreventMode(Document doc, List<ProtectedPinInfo> elements, string username, bool isAdmin)
        {
            // Build element list
            string elementList = string.Join("\n", elements.Take(5).Select(p =>
                $"  • {p.Name}\n    Protected by: {p.ProtectedBy}\n    Reason: {p.AdminComment}"));

            if (elements.Count > 5)
                elementList += $"\n  ... and {elements.Count - 5} more";

            var evalResult = new BIManage.Core.Rules.Models.RuleEvaluationResult
            {
                FinalMode = BIManage.Core.Rules.Models.ProtectionMode.Protect,
                CombinedMessage = $"These {elements.Count} element(s) are protected and require authorization to unpin.\n\n{elementList}",
                RequireComment = true
            };
            var pinElementIds = elements
                .Where(e => e.RevitElement != null)
                .Select(e => e.RevitElement!.Id)
                .ToList();
            var dialog = new BIManage.Views.Protection.GuideDialog(evalResult, pinElementIds);
            dialog.SetProtectMode(BIManage.ViewModels.Protection.ProtectionType.Pin, "Unpin Protected Element");
            BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);
            bool? dialogResult = dialog.ShowDialog();

            if (dialogResult != true || !dialog.OverrideAllowed)
            {
                _logger?.LogInfo($"Prevent mode (floating pushpin): User {username} cancelled OTP entry");
                var cancelComment = dialog.UserComment;
                LogUnpinAction(elements, username, allowed: false, mode: ProtectionMode.Protect,
                    doc: doc, comment: !string.IsNullOrWhiteSpace(cancelComment) ? $"User cancelled | Comment: {cancelComment}" : "User cancelled",
                    source: "floating_pushpin");
                return false; // Block unpin - will trigger undo
            }

            string? otpCode = dialog.OtpCode;

            // Validate OTP
            bool otpValid = ValidateOtp(otpCode, username, elements);

            if (!otpValid)
            {
                var errorDialog = new ErrorDialog(
                    windowTitle: "Pin Protection",
                    headerText: "Invalid OTP",
                    message: "The OTP code you entered is invalid or has expired.",
                    details: "Please contact your BIM administrator for a new code.");
                BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(errorDialog);
                errorDialog.ShowDialog();

                _logger?.LogWarning($"Prevent mode (floating pushpin): Invalid OTP '{otpCode}' entered by {username}");
                LogUnpinAction(elements, username, allowed: false, mode: ProtectionMode.Protect,
                    doc: doc, overrideType: "otp", comment: $"Invalid OTP: {otpCode} | Comment: {dialog.UserComment}",
                    source: "floating_pushpin");
                return false; // Block unpin - will trigger undo
            }

            // OTP valid - allow unpin
            var userComment = dialog.UserComment;
            if (!string.IsNullOrWhiteSpace(userComment))
            {
                _logger?.LogInfo($"User comment: {userComment}");
            }
            _logger?.LogInfo($"Prevent mode (floating pushpin): User {username} authorized via OTP for {elements.Count} elements");

            var unpinAuditLogId = Guid.NewGuid().ToString();
            LogUnpinAction(elements, username, allowed: true, mode: ProtectionMode.Protect,
                doc: doc, overrideType: "otp", overrideGrantedBy: "OTP System", comment: $"OTP: {otpCode} | Comment: {userComment}",
                source: "floating_pushpin", auditLogId: unpinAuditLogId);

            // Build UserEventData for evidence capture (mirrors UnpinCommandBinding.HandlePreventMode)
            var revitContext = _revitContextGetter?.Invoke();
            var sessionId = revitContext?.SessionId.ToString() ?? Guid.NewGuid().ToString();
            var elementIds = elements
                .Where(e => e.RevitElement != null)
                .Select(e => e.RevitElement!.Id.GetIdValue())
                .ToList();

            var userEventData = UserEventData.CreateForPinProtection(
                unpinAuditLogId, sessionId, doc.PathName ?? doc.Title,
                username, _isAdminCheck(), elementIds);
            userEventData.OtpCodeUsed = otpCode;

            // Capture before screenshot
            var screenshotService = _screenshotServiceGetter?.Invoke();
            if (screenshotService != null)
            {
                try
                {
                    userEventData.BeforeImageBytes = screenshotService.CaptureRevitWindowAsBase64();
                    if (userEventData.BeforeImageBytes != null)
                        _logger?.LogInfo($"[TogglePin] Captured before screenshot ({userEventData.BeforeImageBytes.Length} chars)");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"[TogglePin] Failed to capture before screenshot: {ex.Message}");
                }
            }

            // Remove protection metadata (unpin already happened via "Toggle Pin" transaction)
            using (Transaction trans = new Transaction(doc, "Clear Pin Protection"))
            {
                trans.Start();
                PinProtectionStorage.ClearProtection(doc, elements.Select(p => p.RevitElement!));
                trans.Commit();
            }

            // Register elements for after-screenshot tracking
            if (_unpinnedElementTracker != null)
                _unpinnedElementTracker.TrackElements(elementIds, userEventData);

            // Deactivate in database
            DeactivateProtectionInDatabase(doc, elements, username, "Prevent mode - OTP authorized via floating pushpin", otpCode);

            var successDialog = new UnpinSuccessDialog(
                elements.Count,
                $"OTP verified successfully.\n\n{elements.Count} element(s) have been unpinned.\nThis action has been logged for audit purposes.");
            BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(successDialog);
            successDialog.ShowDialog();

            return true; // Allow unpin
        }

        /// <summary>
        /// Get the primary element from multiple modified elements (uses selection)
        /// </summary>
        private Element? GetTogglePinElement(UIApplication app, IEnumerable<Element> allModifiedElements)
        {
            if (allModifiedElements == null || !allModifiedElements.Any())
                return null;

            // If only one element, return it
            if (allModifiedElements.Count() == 1)
                return allModifiedElements.First();

            // If multiple elements, return the one currently selected
            try
            {
                var selectedIds = app.ActiveUIDocument?.Selection?.GetElementIds();
                if (selectedIds != null && selectedIds.Count > 0)
                {
                    return allModifiedElements.FirstOrDefault(x => selectedIds.Contains(x.Id));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Could not get selected element: {ex.Message}");
            }

            // Fallback: return first element
            return allModifiedElements.First();
        }

        /// <summary>
        /// Re-pin elements to revert the unpin action
        /// Creates a new transaction to restore pin state
        /// </summary>
        private void RequestUndo(Document document)
        {
            try
            {
                // Get the elements that were just unpinned from our captured data
                var info = TogglePinExternalEventInfo.Instance;
                var elementsToRePin = info.AllModifiedElements?
                    .Where(el => el != null && el.IsValidObject && !el.Pinned);

                if (elementsToRePin != null && elementsToRePin.Any())
                {
                    using (Transaction trans = new Transaction(document, "ZeManage Restore Pin"))
                    {
                        trans.Start();
                        foreach (var element in elementsToRePin)
                        {
                            element.Pinned = true;
                        }
                        trans.Commit();
                    }
                    _logger?.LogInfo($"Successfully re-pinned {elementsToRePin.Count()} element(s)");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to re-pin elements: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get current Revit username (not Windows username)
        /// </summary>
        private string GetRevitUsername(UIApplication app)
        {
            try
            {
                return app?.Application?.Username ?? Environment.UserName;
            }
            catch
            {
                return Environment.UserName;
            }
        }

        /// <summary>
        /// Show OTP input dialog using custom XAML dialog
        /// </summary>
        private string? ShowOtpInputDialog()
        {
            try
            {
                var dialog = new BIManageRevit.BIManage.Views.Bindings.OtpInputDialog();
                BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);
                var result = dialog.ShowDialog();

                if (result == true && !string.IsNullOrWhiteSpace(dialog.OtpCode))
                {
                    return dialog.OtpCode.Trim();
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to show OTP input dialog: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Validate OTP code against the database
        /// </summary>
        private bool ValidateOtp(string otpCode, string username, List<ProtectedPinInfo> elements)
        {
            if (_otpRepository == null)
            {
                _logger?.LogWarning("OTP repository not available - OTP validation disabled");
                return false;
            }

            try
            {
                var otp = Task.Run(() => _otpRepository.ValidateAndConsumeOtpAsync(
                    otpCode,
                    username,
                    ruleId: null,
                    commandId: "pin_protection")).GetAwaiter().GetResult();

                if (otp != null)
                {
                    _logger?.LogInfo($"OTP validated: Code={otpCode}, GeneratedBy={otp.GeneratedBy}, Reason={otp.Reason}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"OTP validation error: {ex.Message}", ex);
            }

            return false;
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
            string? overrideGrantedBy = null,
            string? comment = null,
            string? source = null,
            string? auditLogId = null)
        {
            string action = allowed ? "allowed" : "blocked";
            string overrideInfo = overrideType != null ? $", override: {overrideType} by {overrideGrantedBy}" : "";
            string sourceInfo = source != null ? $", source: {source}" : "";

            foreach (var elem in elements)
            {
                _logger?.LogInfo($"Pin Protection Unpin {action}: Element {elem.ElementId} ({elem.Name}), " +
                              $"Mode: {mode}, User: {username}, Protected by: {elem.ProtectedBy}{overrideInfo}{sourceInfo}, " +
                              $"Comment: {comment ?? "none"}");
            }

            // Save to audit repository
            if (_auditRepository != null)
            {
                try
                {
                    var elementIds = string.Join(",", elements.Select(e => e.ElementId));
                    var sessionId = _revitContextGetter?.Invoke()?.SessionId.ToString();

                    string? modelGuid = null;
                    if (doc != null)
                    {
                        try { modelGuid = BIManage.Revit.Helpers.ModelGuidHelper.GetModelGuid(doc, _logger); }
                        catch { /* Non-critical */ }
                    }

                    var firstPin = elements.FirstOrDefault();
                    var protectionId = firstPin?.ElementGuid;

                    var rulesMode = mode switch
                    {
                        ProtectionMode.Notify => Core.Rules.Models.ProtectionMode.Notify,
                        ProtectionMode.Assist => Core.Rules.Models.ProtectionMode.Assist,
                        ProtectionMode.Protect => Core.Rules.Models.ProtectionMode.Protect,
                        _ => Core.Rules.Models.ProtectionMode.Notify
                    };

                    var auditEntry = new ProtectionAuditEntry
                    {
                        AuditLogId = auditLogId,
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
                        Reason = comment,
                        OverrideMethod = overrideType,
                        EventSource = "Pin Protection",
                        SessionId = sessionId
                    };

                    _auditRepository.SaveAuditEntry(auditEntry);
                    _logger?.LogDebug($"Audit entry saved for floating pushpin Unpin: {action} (EventSource: PinProtection)");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to save audit entry for floating pushpin: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Deactivate protection records in database (fire and forget)
        /// </summary>
        private async void DeactivateProtectionInDatabase(
            Document doc,
            List<ProtectedPinInfo> elements,
            string deactivatedBy,
            string? reason = null,
            string? otpCodeUsed = null)
        {
            if (_pinProtectionRepository == null)
                return;

            try
            {
                var modelGuid = BIManage.Revit.Helpers.ModelGuidHelper.GetModelGuid(doc, _logger);

                foreach (var element in elements)
                {
                    await _pinProtectionRepository.DeactivateProtectionAsync(
                        modelGuid,
                        element.ElementGuid,
                        deactivatedBy,
                        reason,
                        otpCodeUsed,
                        null);

                    _logger?.LogDebug($"Deactivated protection in database for element {element.ElementGuid}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to deactivate protection in database: {ex.Message}", ex);
            }
        }
    }
}
