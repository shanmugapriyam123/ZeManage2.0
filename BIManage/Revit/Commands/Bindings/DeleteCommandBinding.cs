using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Common.Helpers;
using BIManage.Core.Protection;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.PinProtection;
using BIManage.Revit.PinProtection.Models;

namespace BIManage.Revit.Commands.Bindings
{
    /// <summary>
    /// Dedicated binding for Delete command
    /// Uses BOTH BeforeExecuted (for rule evaluation) and Executed (for explicit delete operation)
    /// This ensures deletion actually happens when rules don't block it
    /// Now also integrates with CommandProtectionBinding for dynamic command protection settings
    /// </summary>
    public class DeleteCommandBinding : CommandBindingBase
    {
        private AddInCommandBinding _binding;
        private readonly IRuleCommandInterceptor _ruleInterceptor;
        private readonly Func<CommandProtectionBinding?> _commandProtectionGetter;
        private bool _commandWasCancelled = false;

        public DeleteCommandBinding(
            UIApplication uiApp,
            ILogger logger,
            IRuleCommandInterceptor ruleInterceptor,
            Func<CommandProtectionBinding?>? commandProtectionGetter = null)
            : base(uiApp, logger)
        {
            _ruleInterceptor = ruleInterceptor;
            _commandProtectionGetter = commandProtectionGetter ?? (() => null);

            // Look up by PostableCommand enum
            CommandId = RevitCommandId.LookupPostableCommandId(PostableCommand.Delete);
        }

        public override void RegisterWithBeforeExecute()
        {
            // Delete uses both BeforeExecuted and Executed, so delegate to Register()
            Register();
        }

        public override void Register()
        {
            if (!CanRegister())
                return;

            try
            {
                _binding = UIApp.CreateAddInCommandBinding(CommandId);
                _binding.BeforeExecuted += OnBeforeExecuted;
                _binding.Executed += OnExecuted;

                // Mark as registered in the shared registry to prevent duplicate bindings
                MarkAsRegistered(CommandId.Name);

                Logger?.LogInfo($"Delete command binding registered: {CommandId.Name} (ID: {CommandId.Id})");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to register Delete binding: {ex.Message}", ex);
            }
        }

        private void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e)
        {
            try
            {
                Logger?.LogDebug($"Delete command intercepted (BeforeExecuted): {e.CommandId.Name}");

                // Reset cancellation flag
                _commandWasCancelled = false;

                // PRIORITY 1: Check CommandProtectionBinding for command-specific settings (Notify/Assist/Protect)
                var commandProtection = _commandProtectionGetter?.Invoke();
                if (commandProtection != null)
                {
                    // Delegate to command protection binding - it handles its own settings lookup
                    commandProtection.ProcessCommandBeforeExecution(e);

                    // If cancelled by command protection, don't run rule interceptor
                    if (e.Cancel)
                    {
                        _commandWasCancelled = true;
                        Logger?.LogInfo("Delete command cancelled by command protection");
                        return;
                    }
                }

                // PRIORITY 2: Delegate to rule interceptor for rule-based evaluation
                _ruleInterceptor?.OnBeforeExecuted(sender, e);

                // Track if command was cancelled by rules
                if (e.Cancel)
                {
                    _commandWasCancelled = true;
                    Logger?.LogInfo("Delete command cancelled by rule evaluation");
                    return;
                }

                // PRIORITY 3: Check for any pinned elements → show alert dialog
                if (HasPinnedElements(e.ActiveDocument, out var pinnedCount))
                {
                    Logger?.LogWarning($"Delete blocked: {pinnedCount} pinned elements selected");

                    var pinnedAlertViewModel = new BIManageRevit.BIManage.ViewModels.Bindings.PinnedElementAlertViewModel(
                        pinnedCount,
                        "Pinned elements (or groups containing pinned elements) cannot be deleted. Unpin them first.",
                        () => { },
                        headerText: "Cannot Delete — Contains Pinned Elements",
                        subText: "Selection includes pinned elements or groups whose members are pinned.");

                    var pinnedAlertDialog = new BIManageRevit.BIManage.Views.Bindings.PinnedElementAlertDialog
                    {
                        DataContext = pinnedAlertViewModel
                    };

                    BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(pinnedAlertDialog);
                    pinnedAlertDialog.ShowDialog();

                    if (e.Cancellable)
                    {
                        e.Cancel = true;
                        _commandWasCancelled = true;
                    }
                    return;
                }

                // PRIORITY 4: Check for protected-pin elements → show mode-appropriate dialog
                var doc = e.ActiveDocument;
                if (doc != null)
                {
                    var protectedElements = GetProtectedPinnedElements(doc);
                    if (protectedElements != null && protectedElements.Count > 0)
                    {
                        HandleProtectedPinDelete(protectedElements, e);
                        if (e.Cancel)
                            _commandWasCancelled = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in Delete BeforeExecuted: {ex.Message}", ex);
            }
        }

        private bool HasPinnedElements(Document doc, out int pinnedCount)
        {
            pinnedCount = 0;
            if (doc == null) return false;

            var selection = UIApp.ActiveUIDocument?.Selection;
            if (selection == null) return false;

            var selectedIds = selection.GetElementIds();
            if (selectedIds == null || selectedIds.Count == 0) return false;

            foreach (var id in selectedIds)
            {
                var element = doc.GetElement(id);
                if (element == null || !element.IsValidObject) continue;

                if (element.Pinned)
                {
                    pinnedCount++;
                }
                else if (element is Group group)
                {
                    // A group is not itself pinned, but deleting it destroys all its members.
                    // Use PinProtectionStorage.IsProtected instead of member.Pinned here:
                    // Revit can report Pinned=true on ALL members of a group instance (group-level
                    // locking behaviour), which would make the count equal the total element count.
                    // IsProtected checks BIManage extensible storage and reflects only elements
                    // that were intentionally restricted, giving the correct count.
                    try
                    {
                        foreach (var memberId in group.GetMemberIds())
                        {
                            var member = doc.GetElement(memberId);
                            if (member != null && member.IsValidObject &&
                                BIManage.Revit.PinProtection.PinProtectionStorage.IsProtected(member))
                                pinnedCount++;
                        }
                    }
                    catch { /* non-critical — skip this group */ }
                }
            }

            return pinnedCount > 0;
        }

        private List<ProtectedPinInfo> GetProtectedPinnedElements(Document doc)
        {
            var result = new List<ProtectedPinInfo>();
            try
            {
                var selection = UIApp.ActiveUIDocument?.Selection;
                if (selection == null) return result;

                var selectedIds = selection.GetElementIds();
                if (selectedIds == null || selectedIds.Count == 0) return result;

                var elements = selectedIds
                    .Select(id => doc.GetElement(id))
                    .Where(el => el != null && el.IsValidObject)
                    .ToList();

                result = PinProtectionStorage.GetProtectedFromSelection(elements);
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error getting protected pinned elements: {ex.Message}", ex);
            }
            return result;
        }

        private void HandleProtectedPinDelete(List<ProtectedPinInfo> protectedElements, BeforeExecutedEventArgs e)
        {
            try
            {
                var highestMode = protectedElements
                    .Select(p => (ProtectionMode)p.ProtectionMode)
                    .OrderByDescending(m => (int)m)
                    .FirstOrDefault();

                Logger?.LogInfo($"Delete attempted on {protectedElements.Count} protected pinned elements. Highest mode: {highestMode}");

                var elementNames = string.Join(", ", protectedElements.Take(5).Select(el => el.Name ?? "Unknown"));
                if (protectedElements.Count > 5)
                    elementNames += $", and {protectedElements.Count - 5} more";

                switch (highestMode)
                {
                    case ProtectionMode.Protect:
                    {
                        var message = $"⛔ Cannot delete protected pinned elements:\n\n{elementNames}\n\n" +
                                      $"These elements are pinned with PREVENT mode protection.\n" +
                                      $"Deletion is blocked to preserve design intent.\n\n" +
                                      $"Admin override: Contact your BIM administrator or use OTP authorization.";

                        var blockViewModel = new BIManageRevit.BIManage.ViewModels.Bindings.ProtectedPinBlockViewModel(
                            protectedElements.Count,
                            elementNames,
                            "Delete",
                            message,
                            () => { });

                        var blockDialog = new BIManageRevit.BIManage.Views.Bindings.BlockedOperationDialog
                        {
                            DataContext = blockViewModel
                        };
                        BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(blockDialog);
                        blockDialog.ShowDialog();

                        if (e.Cancellable)
                        {
                            e.Cancel = true;
                            Logger?.LogWarning($"Delete command blocked: {protectedElements.Count} elements have Prevent mode protection");
                        }
                        break;
                    }

                    case ProtectionMode.Assist:
                    {
                        var message = $"⚠️ Warning: You are about to delete protected pinned elements:\n\n{elementNames}\n\n" +
                                      $"These elements are pinned with GUIDE mode protection.\n" +
                                      $"Deleting them may affect design intent.\n\n" +
                                      $"Do you want to proceed?";

                        var warningViewModel = new BIManageRevit.BIManage.ViewModels.Bindings.ProtectedPinWarningViewModel(
                            protectedElements.Count,
                            elementNames,
                            "Delete",
                            message,
                            (result) => { });

                        var warningDialog = new BIManageRevit.BIManage.Views.Bindings.ProtectionWarningDialog
                        {
                            DataContext = warningViewModel
                        };
                        BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(warningDialog);

                        var dialogResult = warningDialog.ShowDialog();

                        if (dialogResult != true)
                        {
                            if (e.Cancellable)
                            {
                                e.Cancel = true;
                                Logger?.LogInfo($"Delete command cancelled by user: {protectedElements.Count} protected elements");
                            }
                        }
                        else
                        {
                            Logger?.LogInfo($"Delete command allowed by user acknowledgment: {protectedElements.Count} protected elements");
                        }
                        break;
                    }

                    case ProtectionMode.Notify:
                        Logger?.LogInfo($"Delete command monitored: {protectedElements.Count} protected pinned elements being deleted (Notify mode)");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error handling protected pin delete: {ex.Message}", ex);
            }
        }

        private void OnExecuted(object sender, ExecutedEventArgs e)
        {
            try
            {
                // If command was cancelled in BeforeExecuted, don't perform deletion
                if (_commandWasCancelled)
                {
                    Logger?.LogDebug("Delete operation skipped - command was cancelled by rules");
                    return;
                }

                Logger?.LogDebug($"Delete command executing (Executed): {e.CommandId.Name}");

                var doc = e.ActiveDocument;
                if (doc == null || doc.IsReadOnly)
                {
                    Logger?.LogWarning("Cannot delete - no active document or document is read-only");
                    return;
                }

                // Get selected elements
                var selection = UIApp.ActiveUIDocument?.Selection;
                if (selection == null)
                {
                    Logger?.LogWarning("Cannot delete - no selection available");
                    return;
                }

                var selectedIds = selection.GetElementIds();
                if (selectedIds == null || selectedIds.Count == 0)
                {
                    Logger?.LogDebug("No elements selected for deletion");
                    return;
                }

                // Filter out elements that cannot be deleted
                var elementsToDelete = new List<ElementId>();
                foreach (var id in selectedIds)
                {
                    var element = doc.GetElement(id);
                    if (element != null && CanDeleteElement(element))
                    {
                        elementsToDelete.Add(id);
                    }
                    else if (element != null)
                    {
                        Logger?.LogDebug($"Cannot delete element {id.GetIdValue()}: {GetDeleteBlockReason(element)}");
                    }
                }

                if (elementsToDelete.Count == 0)
                {
                    Logger?.LogInfo("No deletable elements found in selection");
                    return;
                }

                // Warn user if some elements were filtered out
                var skippedCount = selectedIds.Count - elementsToDelete.Count;
                if (skippedCount > 0)
                {
                    Logger?.LogInfo($"Delete: {skippedCount} of {selectedIds.Count} elements skipped (pinned or owned by other user)");
                }

                // Perform deletion in a transaction
                using (Transaction trans = new Transaction(doc, "Delete Elements"))
                {
                    trans.Start();

                    try
                    {
                        var deleted = doc.Delete(elementsToDelete);
                        trans.Commit();

                        Logger?.LogInfo($"Deleted {deleted.Count} elements (attempted {elementsToDelete.Count})");
                    }
                    catch (Exception ex)
                    {
                        trans.RollBack();
                        Logger?.LogError($"Failed to delete elements: {ex.Message}", ex);

                        // Show error to user
                        var dialog = new BIManageRevit.BIManage.Views.Bindings.ErrorDialog(
                            "Delete Failed",
                            "Could not delete the selected elements.",
                            $"Reason: {ex.Message}");
                        BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);
                        dialog.ShowDialog();
                    }
                }

                // Call rule interceptor for post-execution tracking
                _ruleInterceptor?.OnExecuted(sender, e);
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in Delete Executed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Check if an element can be deleted
        /// </summary>
        private bool CanDeleteElement(Element element)
        {
            if (element == null || !element.IsValidObject)
                return false;

            // Cannot delete elements that are pinned
            if (element.Pinned)
                return false;

            // Check if element is deletable
            var doc = element.Document;
            if (doc == null)
                return false;

            // Check worksharing constraints
            if (doc.IsWorkshared)
            {
                var worksharingEnabled = WorksharingUtils.GetCheckoutStatus(doc, element.Id);
                if (worksharingEnabled == CheckoutStatus.OwnedByOtherUser)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Get reason why element cannot be deleted
        /// </summary>
        private string GetDeleteBlockReason(Element element)
        {
            if (element == null || !element.IsValidObject)
                return "Element is null or invalid";

            if (element.Pinned)
                return "Element is pinned - unpin first";

            var doc = element.Document;
            if (doc == null)
                return "No document";

            if (doc.IsWorkshared)
            {
                var checkoutStatus = WorksharingUtils.GetCheckoutStatus(doc, element.Id);
                if (checkoutStatus == CheckoutStatus.OwnedByOtherUser)
                    return "Owned by another user";
            }

            return "Unknown reason";
        }

        public override void Unregister()
        {
            if (_binding != null)
            {
                _binding.BeforeExecuted -= OnBeforeExecuted;
                _binding.Executed -= OnExecuted;
                Logger?.LogDebug("Delete command binding unregistered");
            }
        }
    }
}
