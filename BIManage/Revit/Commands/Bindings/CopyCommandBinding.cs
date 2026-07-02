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
    /// Dedicated binding for Copy command - with Pin Protection.
    /// Uses BeforeExecuted only; Revit handles the actual copy natively.
    /// </summary>
    public class CopyCommandBinding : CommandBindingBase
    {
        private AddInCommandBinding _binding;
        private readonly IRuleCommandInterceptor _ruleInterceptor;
        private readonly Func<CommandProtectionBinding?> _commandProtectionGetter;

        public CopyCommandBinding(
            UIApplication uiApp,
            ILogger logger,
            IRuleCommandInterceptor ruleInterceptor,
            Func<CommandProtectionBinding?>? commandProtectionGetter = null)
            : base(uiApp, logger)
        {
            _ruleInterceptor = ruleInterceptor;
            _commandProtectionGetter = commandProtectionGetter ?? (() => null);
            CommandId = RevitCommandId.LookupPostableCommandId(PostableCommand.Copy);
        }

        public override void RegisterWithBeforeExecute()
        {
            if (!CanRegister())
                return;

            try
            {
                _binding = UIApp.CreateAddInCommandBinding(CommandId);
                _binding.BeforeExecuted += OnBeforeExecuted;
                MarkAsRegistered(CommandId.Name);
                Logger?.LogInfo($"Copy command binding registered: {CommandId.Name} (ID: {CommandId.Id})");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to register Copy binding: {ex.Message}", ex);
            }
        }

        private void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e)
        {
            try
            {
                Logger?.LogDebug($"Copy command intercepted: {e.CommandId.Name}");

                // PRIORITY 0: Check CommandProtectionBinding for Notify/Assist/Protect settings
                var commandProtection = _commandProtectionGetter?.Invoke();
                if (commandProtection != null)
                {
                    commandProtection.ProcessCommandBeforeExecution(e);
                    if (e.Cancel)
                    {
                        Logger?.LogInfo("Copy command cancelled by command protection");
                        return;
                    }
                }

                // PRIORITY 1: Rule-based evaluation
                _ruleInterceptor?.OnBeforeExecuted(sender, e);
                if (e.Cancel)
                {
                    Logger?.LogInfo("Copy command cancelled by rule evaluation");
                    return;
                }

                // PRIORITY 2: Check for any pinned elements → show alert dialog
                if (HasPinnedElements(e.ActiveDocument, out var pinnedCount))
                {
                    Logger?.LogWarning($"Copy blocked: {pinnedCount} pinned elements selected");

                    var pinnedAlertViewModel = new BIManageRevit.BIManage.ViewModels.Bindings.PinnedElementAlertViewModel(
                        pinnedCount,
                        "Pinned elements cannot be copied. Unpin them first.",
                        () => { },
                        headerText: "Cannot Copy Pinned Elements",
                        subText: "These elements are protected and cannot be copied.");

                    var pinnedAlertDialog = new BIManageRevit.BIManage.Views.Bindings.PinnedElementAlertDialog
                    {
                        DataContext = pinnedAlertViewModel
                    };

                    BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(pinnedAlertDialog);
                    pinnedAlertDialog.ShowDialog();

                    if (e.Cancellable)
                    {
                        e.Cancel = true;
                        return;
                    }
                }

                // PRIORITY 3: Check for protected-pin elements → show mode-appropriate dialog
                var doc = e.ActiveDocument;
                if (doc != null)
                {
                    var protectedElements = GetProtectedPinnedElements(doc);
                    if (protectedElements != null && protectedElements.Count > 0)
                    {
                        HandleProtectedPinCopy(protectedElements, e);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in Copy BeforeExecuted: {ex.Message}", ex);
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
                if (element != null && element.IsValidObject && element.Pinned)
                    pinnedCount++;
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

        private void HandleProtectedPinCopy(List<ProtectedPinInfo> protectedElements, BeforeExecutedEventArgs e)
        {
            try
            {
                var highestMode = protectedElements
                    .Select(p => (ProtectionMode)p.ProtectionMode)
                    .OrderByDescending(m => (int)m)
                    .FirstOrDefault();

                Logger?.LogInfo($"Copy attempted on {protectedElements.Count} protected pinned elements. Highest mode: {highestMode}");

                var elementNames = string.Join(", ", protectedElements.Take(5).Select(el => el.Name ?? "Unknown"));
                if (protectedElements.Count > 5)
                    elementNames += $", and {protectedElements.Count - 5} more";

                switch (highestMode)
                {
                    case ProtectionMode.Protect:
                    {
                        var message = $"⛔ Cannot copy protected pinned elements:\n\n{elementNames}\n\n" +
                                      $"These elements are pinned with PREVENT mode protection.\n" +
                                      $"Copying is blocked to preserve design intent.\n\n" +
                                      $"Admin override: Contact your BIM administrator or use OTP authorization.";

                        var blockViewModel = new BIManageRevit.BIManage.ViewModels.Bindings.ProtectedPinBlockViewModel(
                            protectedElements.Count,
                            elementNames,
                            "Copy",
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
                            Logger?.LogWarning($"Copy command blocked: {protectedElements.Count} elements have Prevent mode protection");
                        }
                        break;
                    }

                    case ProtectionMode.Assist:
                    {
                        var message = $"⚠️ Warning: You are about to copy protected pinned elements:\n\n{elementNames}\n\n" +
                                      $"These elements are pinned with GUIDE mode protection.\n" +
                                      $"Copying them may affect design intent.\n\n" +
                                      $"Do you want to proceed?";

                        var warningViewModel = new BIManageRevit.BIManage.ViewModels.Bindings.ProtectedPinWarningViewModel(
                            protectedElements.Count,
                            elementNames,
                            "Copy",
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
                                Logger?.LogInfo($"Copy command cancelled by user: {protectedElements.Count} protected elements");
                            }
                        }
                        else
                        {
                            Logger?.LogInfo($"Copy command allowed by user acknowledgment: {protectedElements.Count} protected elements");
                        }
                        break;
                    }

                    case ProtectionMode.Notify:
                        Logger?.LogInfo($"Copy command monitored: {protectedElements.Count} protected pinned elements being copied (Notify mode)");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error handling protected pin copy: {ex.Message}", ex);
            }
        }

        public override void Register() => RegisterWithBeforeExecute();

        public override void Unregister()
        {
            if (_binding != null)
            {
                _binding.BeforeExecuted -= OnBeforeExecuted;
                Logger?.LogDebug("Copy command binding unregistered");
            }
        }
    }
}
