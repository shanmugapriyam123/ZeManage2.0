using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
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
    /// Dedicated binding for Offset command - with Pin Protection.
    ///
    /// Handles two workflows:
    ///   Select-first → Offset: BeforeExecuted checks current selection and cancels before the command runs.
    ///   Offset-first → pick element: BeforeExecuted sees an empty selection, so we set _offsetActive and
    ///     monitor DocumentChanged. If a pinned/protected element is modified by the offset we undo via
    ///     the next Idling tick (the only place Document.Undo() is safe to call from an event handler).
    /// </summary>
    public class OffsetCommandBinding : CommandBindingBase
    {
        private AddInCommandBinding _binding;
        private readonly IRuleCommandInterceptor _ruleInterceptor;
        private readonly Func<CommandProtectionBinding?> _commandProtectionGetter;
        private readonly BIManage.Core.Features.IFeatureToggleService? _featureToggleService;

        // True while the Offset command is in its interactive pick session (set in BeforeExecuted,
        // cleared only on the next BeforeExecuted so multiple picks in the same session are covered).
        private volatile bool _offsetActive = false;
        // Guards the dialog queue — only one dialog per undo cycle; reset in OnIdlingShowDialog.
        private volatile bool _showDialogPending = false;
        // Throttle: don't post more than one Undo per 500 ms to avoid flooding if the Offset
        // command re-applies on the same element immediately after each undo.
        private DateTime _lastUndoPostedAt = DateTime.MinValue;
        private List<ProtectedPinInfo> _blockedProtectedElements = new List<ProtectedPinInfo>();
        private int _pinnedElementCount = 0;

        public OffsetCommandBinding(
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
            CommandId = RevitCommandId.LookupPostableCommandId(PostableCommand.Offset);
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

                // Also watch DocumentChanged to catch command-first → pick-element workflow
                UIApp.Application.DocumentChanged += OnDocumentChanged;

                Logger?.LogInfo($"Offset command binding registered: {CommandId.Name} (ID: {CommandId.Id})");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to register Offset binding: {ex.Message}", ex);
            }
        }

        private void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e)
        {
            try
            {
                Logger?.LogDebug($"Offset command intercepted: {e.CommandId.Name}");

                // Reset ALL tracking on every new invocation
                _offsetActive = false;
                _showDialogPending = false;
                _lastUndoPostedAt = DateTime.MinValue;
                UIApp.Idling -= OnIdlingShowDialog;

                if (_featureToggleService?.IsGlobalPaused == true || _featureToggleService?.IsEmployeeCaptureDisabled == true)
                    return;

                // PRIORITY 0: Command protection
                var commandProtection = _commandProtectionGetter?.Invoke();
                if (commandProtection != null)
                {
                    commandProtection.ProcessCommandBeforeExecution(e);
                    if (e.Cancel)
                    {
                        Logger?.LogInfo("Offset command cancelled by command protection");
                        return;
                    }
                }

                // PRIORITY 1: Rule-based evaluation
                _ruleInterceptor?.OnBeforeExecuted(sender, e);
                if (e.Cancel)
                {
                    Logger?.LogInfo("Offset command cancelled by rule evaluation");
                    return;
                }

                var doc = e.ActiveDocument;
                var selection = UIApp.ActiveUIDocument?.Selection;
                var selectedIds = selection?.GetElementIds();
                bool hasSelection = selectedIds != null && selectedIds.Count > 0;

                if (hasSelection)
                {
                    // ── Select-first workflow: check and cancel inline ──────────────────
                    if (HasPinnedElements(doc, selectedIds, out var pinnedCount))
                    {
                        Logger?.LogWarning($"Offset blocked: {pinnedCount} pinned elements selected");
                        ShowPinnedAlert(pinnedCount);

                        if (e.Cancellable)
                            e.Cancel = true;
                        return;
                    }

                    var protectedElements = GetProtectedPinnedElements(doc, selectedIds);
                    if (protectedElements.Count > 0)
                    {
                        HandleProtectedPinOffset(protectedElements, e);
                    }
                }
                else
                {
                    // ── Command-first workflow: no selection yet ───────────────────────
                    // The user will pick an element interactively. Set flag so DocumentChanged
                    // can catch any pinned/protected element that ends up being modified.
                    _offsetActive = true;
                    Logger?.LogDebug("Offset command-first mode: monitoring DocumentChanged for pinned element modification");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in Offset BeforeExecuted: {ex.Message}", ex);
            }
        }

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (!_offsetActive) return;
            if (e.Operation != UndoOperation.TransactionCommitted) return;

            try
            {
                // Only act on actual Offset transactions — ignore other commands that fire while
                // the Offset pick session is still open (e.g. parameter updates, snap events).
                var txNames = e.GetTransactionNames();
                bool isOffsetTx = txNames != null && txNames.Any(n =>
                    string.Equals(n?.Trim(), "Offset", StringComparison.OrdinalIgnoreCase));
                if (!isOffsetTx) return;

                var doc = e.GetDocument();
                if (doc == null) return;

                var modifiedIds = e.GetModifiedElementIds();
                if (modifiedIds == null || modifiedIds.Count == 0) return;

                var pinnedModified = modifiedIds
                    .Select(id => { try { return doc.GetElement(id); } catch { return null; } })
                    .Where(el => el != null && el.IsValidObject && el.Pinned)
                    .ToList();

                if (pinnedModified.Count == 0) return;

                _pinnedElementCount = pinnedModified.Count;

                // Post Undo directly from DocumentChanged — same approach as ElementBypassDetector.
                // Throttled to 500 ms: if the Offset command re-applies on the same element right
                // after each undo (pick-loop behaviour), every re-apply is also reversed without
                // flooding the undo stack.
                var now = DateTime.UtcNow;
                if ((now - _lastUndoPostedAt).TotalMilliseconds > 500)
                {
                    _lastUndoPostedAt = now;
                    try
                    {
                        var undoId = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
                        if (undoId != null)
                            UIApp.PostCommand(undoId);
                        Logger?.LogWarning($"[OffsetBinding] Offset on {pinnedModified.Count} pinned element(s) — Undo posted");
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogError($"[OffsetBinding] Failed to post Undo: {ex.Message}", ex);
                    }
                }

                // Queue the dialog only once per undo cycle; reset in OnIdlingShowDialog so
                // subsequent picks in the same session can trigger a fresh dialog.
                if (!_showDialogPending)
                {
                    _showDialogPending = true;
                    UIApp.Idling += OnIdlingShowDialog;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[OffsetBinding] Error in DocumentChanged: {ex.Message}", ex);
            }
        }

        private void OnIdlingShowDialog(object sender, IdlingEventArgs e)
        {
            UIApp.Idling -= OnIdlingShowDialog;

            if (!_showDialogPending) return;

            // Reset dialog flag BEFORE showing so that if the Offset command re-applies during
            // the modal dialog's nested message loop, OnDocumentChanged can detect it and queue
            // a fresh dialog rather than being silently blocked.
            _showDialogPending = false;
            var count = _pinnedElementCount;
            _pinnedElementCount = 0;
            _blockedProtectedElements = new List<ProtectedPinInfo>();

            // _offsetActive intentionally NOT reset — the Offset pick session may still be open.

            try
            {
                ShowPinnedAlert(count);
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[OffsetBinding] Error showing pin alert dialog: {ex.Message}", ex);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private bool HasPinnedElements(Document doc, ICollection<ElementId> ids, out int pinnedCount)
        {
            pinnedCount = 0;
            if (doc == null || ids == null) return false;

            foreach (var id in ids)
            {
                var element = doc.GetElement(id);
                if (element != null && element.IsValidObject && element.Pinned)
                    pinnedCount++;
            }

            return pinnedCount > 0;
        }

        private List<ProtectedPinInfo> GetProtectedPinnedElements(Document doc, ICollection<ElementId> ids)
        {
            var result = new List<ProtectedPinInfo>();
            try
            {
                var elements = ids
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

        private void ShowPinnedAlert(int pinnedCount)
        {
            var vm = new BIManageRevit.BIManage.ViewModels.Bindings.PinnedElementAlertViewModel(
                pinnedCount,
                "Pinned elements cannot be offset. Unpin them first.",
                () => { },
                headerText: "Cannot Offset Pinned Elements",
                subText: "These elements are protected and cannot be offset.");

            var dialog = new BIManageRevit.BIManage.Views.Bindings.PinnedElementAlertDialog
            {
                DataContext = vm
            };
            BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);
            dialog.ShowDialog();
        }

        private void HandleProtectedPinOffset(List<ProtectedPinInfo> protectedElements, BeforeExecutedEventArgs e)
        {
            try
            {
                var highestMode = protectedElements
                    .Select(p => (ProtectionMode)p.ProtectionMode)
                    .OrderByDescending(m => (int)m)
                    .FirstOrDefault();

                Logger?.LogInfo($"Offset attempted on {protectedElements.Count} protected pinned elements. Highest mode: {highestMode}");

                var elementNames = string.Join(", ", protectedElements.Take(5).Select(el => el.Name ?? "Unknown"));
                if (protectedElements.Count > 5)
                    elementNames += $", and {protectedElements.Count - 5} more";

                switch (highestMode)
                {
                    case ProtectionMode.Protect:
                    {
                        var message = $"⛔ Cannot offset protected pinned elements:\n\n{elementNames}\n\n" +
                                      "These elements are pinned with PREVENT mode protection.\n" +
                                      "Offset is blocked to preserve design intent.\n\n" +
                                      "Admin override: Contact your BIM administrator or use OTP authorization.";

                        var blockViewModel = new BIManageRevit.BIManage.ViewModels.Bindings.ProtectedPinBlockViewModel(
                            protectedElements.Count, elementNames, "Offset", message, () => { });

                        var blockDialog = new BIManageRevit.BIManage.Views.Bindings.BlockedOperationDialog
                        {
                            DataContext = blockViewModel
                        };
                        BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(blockDialog);
                        blockDialog.ShowDialog();

                        if (e.Cancellable)
                        {
                            e.Cancel = true;
                            Logger?.LogWarning($"Offset command blocked: {protectedElements.Count} elements have Prevent mode protection");
                        }
                        break;
                    }

                    case ProtectionMode.Assist:
                    {
                        var message = $"⚠️ Warning: You are about to offset protected pinned elements:\n\n{elementNames}\n\n" +
                                      "These elements are pinned with GUIDE mode protection.\n" +
                                      "Offsetting them may affect design intent.\n\n" +
                                      "Do you want to proceed?";

                        var warningViewModel = new BIManageRevit.BIManage.ViewModels.Bindings.ProtectedPinWarningViewModel(
                            protectedElements.Count, elementNames, "Offset", message, (result) => { });

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
                                Logger?.LogInfo($"Offset cancelled by user: {protectedElements.Count} protected elements");
                            }
                        }
                        else
                        {
                            Logger?.LogInfo($"Offset allowed by user acknowledgment: {protectedElements.Count} protected elements");
                        }
                        break;
                    }

                    case ProtectionMode.Notify:
                        Logger?.LogInfo($"Offset monitored: {protectedElements.Count} protected pinned elements (Notify mode)");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error handling protected pin offset: {ex.Message}", ex);
            }
        }

        public override void Register() => RegisterWithBeforeExecute();

        public override void Unregister()
        {
            if (_binding != null)
                _binding.BeforeExecuted -= OnBeforeExecuted;

            UIApp.Application.DocumentChanged -= OnDocumentChanged;
            UIApp.Idling -= OnIdlingShowDialog;

            Logger?.LogDebug("Offset command binding unregistered");
        }
    }
}
