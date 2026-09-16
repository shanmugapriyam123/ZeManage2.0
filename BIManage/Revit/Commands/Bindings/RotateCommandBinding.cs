using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Revit.PinProtection;
using BIManage.Revit.PinProtection.Models;
using BIManage.Core.Protection;
using BIManage.Common.Helpers;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.Commands.Bindings
{
    /// <summary>
    /// Dedicated binding for Rotate command - with Pin Protection
    /// Uses BeforeExecuted event for rule-based interception and pin protection
    /// Uses Executed event for pin state restoration
    /// </summary>
    public class RotateCommandBinding : CommandBindingBase
    {
        private AddInCommandBinding _binding;
        private readonly IRuleCommandInterceptor _ruleInterceptor;
        private readonly Func<CommandProtectionBinding?> _commandProtectionGetter;
        private readonly BIManage.Core.Features.IFeatureToggleService? _featureToggleService;

        public RotateCommandBinding(
            UIApplication uiApp,
            ILogger logger,
            IRuleCommandInterceptor ruleInterceptor,
            Func<CommandProtectionBinding?>? commandProtectionGetter = null,
            BIManage.Core.Features.IFeatureToggleService? featureToggleService = null)
            : base(uiApp, logger)
        {
            _ruleInterceptor = ruleInterceptor;
            _commandProtectionGetter = commandProtectionGetter ?? (() => null);
            _featureToggleService = featureToggleService;
            CommandId = RevitCommandId.LookupPostableCommandId(PostableCommand.Rotate);
        }

        public override void RegisterWithBeforeExecute()
        {
            if (!CanRegister())
                return;

            try
            {
                _binding = UIApp.CreateAddInCommandBinding(CommandId);
                _binding.BeforeExecuted += OnBeforeExecuted;
                // NOTE: Do NOT attach Executed handler - it prevents native Rotate command from running
                // With only BeforeExecuted, Revit will proceed with native Rotate after our handler completes

                // Mark as registered in the shared registry to prevent duplicate bindings
                MarkAsRegistered(CommandId.Name);

                Logger?.LogInfo($"Rotate command binding registered: {CommandId.Name} (ID: {CommandId.Id})");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to register Rotate binding: {ex.Message}", ex);
            }
        }

        private void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e)
        {
            try
            {
                if (_featureToggleService?.IsGlobalPaused == true || _featureToggleService?.IsEmployeeCaptureDisabled == true)
                    return;

                Logger?.LogDebug($"Rotate command intercepted: {e.CommandId.Name}");

                // PRIORITY 0: Check CommandProtectionBinding for Notify/Assist/Protect settings
                var commandProtection = _commandProtectionGetter?.Invoke();
                if (commandProtection != null)
                {
                    commandProtection.ProcessCommandBeforeExecution(e);
                    if (e.Cancel)
                    {
                        Logger?.LogInfo("Rotate command cancelled by command protection");
                        return;
                    }
                }

                // Step 1: Check database rules (rule-based protection must run before pin check
                // so category/parameter rules are evaluated regardless of pin state)
                _ruleInterceptor?.OnBeforeExecuted(sender, e);
                if (e.Cancel)
                {
                    Logger?.LogInfo("Rotate command cancelled by rule evaluation");
                    return;
                }

                // Step 2: Check if any selected elements are pinned (native Revit behavior)
                if (HasPinnedElements(e.ActiveDocument, out var pinnedCount))
                {
                    Logger?.LogWarning($"Rotate blocked: {pinnedCount} pinned elements selected");

                    // Show warning dialog
                    var pinnedAlertViewModel = new BIManageRevit.BIManage.ViewModels.Bindings.PinnedElementAlertViewModel(
                        pinnedCount,
                        "Pinned elements cannot be rotated. Unpin them first.",
                        () => { },
                        headerText: "Cannot Rotate Pinned Elements",
                        subText: "These elements are protected and cannot be rotated.");

                    var pinnedAlertDialog = new BIManageRevit.BIManage.Views.Bindings.PinnedElementAlertDialog
                    {
                        DataContext = pinnedAlertViewModel
                    };

                    BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(pinnedAlertDialog);
                    pinnedAlertDialog.ShowDialog();

                    // Block command
                    if (e.Cancellable)
                    {
                        e.Cancel = true;
                        return;
                    }
                }

                // Step 3: Check if any selected elements have protected pins
                var doc = e.ActiveDocument;
                if (doc != null)
                {
                    var protectedElements = GetProtectedPinnedElements(doc);
                    if (protectedElements != null && protectedElements.Count > 0)
                    {
                        HandleProtectedPinRotate(protectedElements, e);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in Rotate BeforeExecuted: {ex.Message}", ex);
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
                {
                    pinnedCount++;
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

                // Get elements and check for protection
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

        private void HandleProtectedPinRotate(List<ProtectedPinInfo> protectedElements, BeforeExecutedEventArgs e)
        {
            try
            {
                // Get highest protection mode
                var highestMode = protectedElements
                    .Select(p => (ProtectionMode)p.ProtectionMode)
                    .OrderByDescending(m => (int)m)
                    .FirstOrDefault();

                Logger?.LogInfo($"Rotate attempted on {protectedElements.Count} protected pinned elements. Highest mode: {highestMode}");

                switch (highestMode)
                {
                    case ProtectionMode.Protect:
                        HandlePreventMode(protectedElements, e);
                        break;

                    case ProtectionMode.Assist:
                        HandleGuideMode(protectedElements, e);
                        break;

                    case ProtectionMode.Notify:
                        HandleMonitorMode(protectedElements);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error handling protected pin rotate: {ex.Message}", ex);
            }
        }

        private void HandlePreventMode(List<ProtectedPinInfo> elements, BeforeExecutedEventArgs e)
        {
            // Build warning message
            var elementNames = string.Join(", ", elements.Take(5).Select(el => el.Name ?? "Unknown"));
            if (elements.Count > 5)
                elementNames += $", and {elements.Count - 5} more";

            var message = $"⛔ Cannot rotate protected pinned elements:\n\n{elementNames}\n\n" +
                          $"These elements are pinned with PREVENT mode protection.\n" +
                          $"Rotating is blocked to preserve design intent.\n\n" +
                          $"Admin override: Contact your BIM administrator or use OTP authorization.";

            // Show blocking dialog
            var blockViewModel = new BIManageRevit.BIManage.ViewModels.Bindings.ProtectedPinBlockViewModel(
                elements.Count,
                elementNames,
                "Rotate",
                message,
                () => { });

            var blockDialog = new BIManageRevit.BIManage.Views.Bindings.BlockedOperationDialog
            {
                DataContext = blockViewModel
            };
            BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(blockDialog);

            blockDialog.ShowDialog();

            // Block the rotate command
            if (e.Cancellable)
            {
                e.Cancel = true;
                Logger?.LogWarning($"Rotate command blocked: {elements.Count} elements have Prevent mode protection");
            }
        }

        private void HandleGuideMode(List<ProtectedPinInfo> elements, BeforeExecutedEventArgs e)
        {
            var elementNames = string.Join(", ", elements.Take(5).Select(el => el.Name ?? "Unknown"));
            if (elements.Count > 5)
                elementNames += $", and {elements.Count - 5} more";

            var message = $"⚠️ Warning: You are about to rotate protected pinned elements:\n\n{elementNames}\n\n" +
                          $"These elements are pinned with GUIDE mode protection.\n" +
                          $"Rotating them may affect design intent.\n\n" +
                          $"Do you want to proceed?";

            var warningViewModel = new BIManageRevit.BIManage.ViewModels.Bindings.ProtectedPinWarningViewModel(
                elements.Count,
                elementNames,
                "Rotate",
                message,
                (result) => { });

            var warningDialog = new BIManageRevit.BIManage.Views.Bindings.ProtectionWarningDialog
            {
                DataContext = warningViewModel
            };
            BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(warningDialog);

            var result = warningDialog.ShowDialog();

            if (result != true)
            {
                // User cancelled
                if (e.Cancellable)
                {
                    e.Cancel = true;
                    Logger?.LogInfo($"Rotate command cancelled by user: {elements.Count} protected elements");
                }
            }
            else
            {
                Logger?.LogInfo($"Rotate command allowed by user acknowledgment: {elements.Count} protected elements");
            }
        }

        private void HandleMonitorMode(List<ProtectedPinInfo> elements)
        {
            // Log the rotate but don't block
            Logger?.LogInfo($"Rotate command monitored: {elements.Count} protected pinned elements being rotated (Monitor mode)");
        }

        public override void Register()
        {
            // For Rotate, we use RegisterWithBeforeExecute (BeforeExecuted only - no Executed handler)
            RegisterWithBeforeExecute();
        }

        public override void Unregister()
        {
            if (_binding != null)
            {
                _binding.BeforeExecuted -= OnBeforeExecuted;
                // NOTE: Executed handler is not attached, so no need to unsubscribe
                Logger?.LogDebug("Rotate command binding unregistered");
            }
        }
    }
}
