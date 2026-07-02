using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Revit.PinProtection;
using BIManage.Revit.PinProtection.Models;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Commands;

namespace BIManage.Revit.Commands.Bindings
{
    /// <summary>
    /// Dedicated binding for Group, Ungroup, and Edit Group commands.
    /// Blocks any group operation that involves pinned or BIManage pin-protected elements.
    ///
    /// "Add" button protection (command-first flow):
    ///   DocumentChanged is always-on. Each modified Group is compared against a per-group
    ///   member snapshot stored in _groupMemberSnapshots. If a newly added member is pinned,
    ///   an Undo is posted immediately (same pattern as ElementBypassDetector) and a dialog
    ///   is shown on the next Idling tick. This covers both the ribbon "Edit Group" path AND
    ///   the double-click entry path — no session flag needed.
    /// </summary>
    public class GroupCommandBinding : CommandBindingBase
    {
        private AddInCommandBinding? _groupBinding;
        private AddInCommandBinding? _ungroupBinding;
        private AddInCommandBinding? _editGroupBinding;

        private readonly IRuleCommandInterceptor? _ruleInterceptor;
        private readonly Func<CommandProtectionBinding?> _commandProtectionGetter;

        private RevitCommandId? _groupCommandId;
        private RevitCommandId? _ungroupCommandId;
        private RevitCommandId? _editGroupCommandId;

        // Per-group member snapshot: groupId → set of member ElementId values.
        // Populated lazily on first DocumentChanged touch; diffs on every subsequent commit.
        private readonly Dictionary<long, HashSet<long>> _groupMemberSnapshots = new Dictionary<long, HashSet<long>>();
        private volatile bool _showGroupAddDialogPending = false;
        private int _pinnedAddedCount = 0;

        public GroupCommandBinding(
            UIApplication uiApp,
            ILogger logger,
            IRuleCommandInterceptor? ruleInterceptor,
            Func<CommandProtectionBinding?>? commandProtectionGetter = null)
            : base(uiApp, logger)
        {
            _ruleInterceptor = ruleInterceptor;
            _commandProtectionGetter = commandProtectionGetter ?? (() => null);
            CommandId = LookupCommandById(PostableCommands.Group);
        }

        public override void RegisterWithBeforeExecute()
        {
            _groupCommandId     = LookupCommandById(PostableCommands.Group);
            _ungroupCommandId   = LookupCommandById(PostableCommands.Ungroup);
            _editGroupCommandId = LookupCommandById(PostableCommands.EditGroup);

            RegisterSingle(ref _groupBinding,     _groupCommandId,     "Group");
            RegisterSingle(ref _ungroupBinding,   _ungroupCommandId,   "Ungroup");
            RegisterSingle(ref _editGroupBinding, _editGroupCommandId, "EditGroup");

            UIApp.Application.DocumentChanged += OnDocumentChanged;
        }

        private void RegisterSingle(ref AddInCommandBinding? field, RevitCommandId? commandId, string name)
        {
            if (commandId == null || !commandId.CanHaveBinding)
            {
                Logger?.LogWarning($"[GroupBinding] {name} command cannot have a binding — skipping");
                return;
            }

            try
            {
                field = UIApp.CreateAddInCommandBinding(commandId);
                field.BeforeExecuted += OnBeforeExecuted;
                MarkAsRegistered(commandId.Name);
                Logger?.LogInfo($"[GroupBinding] Registered: {name} ({commandId.Name})");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[GroupBinding] Failed to register {name}: {ex.Message}", ex);
            }
        }

        // ── BeforeExecuted: select-first path ─────────────────────────────────────

        private void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e)
        {
            try
            {
                var commandProtection = _commandProtectionGetter?.Invoke();
                if (commandProtection != null)
                {
                    commandProtection.ProcessCommandBeforeExecution(e);
                    if (e.Cancel) return;
                }

                _ruleInterceptor?.OnBeforeExecuted(sender, e);
                if (e.Cancel) return;

                var doc = e.ActiveDocument;
                if (doc == null) return;

                var commandId = e.CommandId;
                var operationLabel = GetOperationLabel(commandId);

                var selection = UIApp.ActiveUIDocument?.Selection;
                var selectedIds = selection?.GetElementIds();
                if (selectedIds == null || selectedIds.Count == 0) return;

                // Priority 2: block ANY pinned element (or group with pinned members)
                var pinnedCount = CountPinnedElementsForOperation(doc, commandId, selectedIds);
                if (pinnedCount > 0)
                {
                    ShowPinnedAlert(pinnedCount, operationLabel);
                    if (e.Cancellable)
                    {
                        e.Cancel = true;
                        Logger?.LogWarning($"[GroupBinding] {operationLabel} blocked: {pinnedCount} pinned element(s)");
                    }
                    return;
                }

                // Priority 3: block BIManage pin-protected elements
                var protectedElements = GetProtectedElementsForOperation(doc, commandId, selectedIds);
                if (protectedElements.Count > 0)
                {
                    ShowBlockDialog(protectedElements, operationLabel);
                    if (e.Cancellable)
                    {
                        e.Cancel = true;
                        Logger?.LogWarning($"[GroupBinding] {operationLabel} blocked: {protectedElements.Count} pin-protected element(s)");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[GroupBinding] Error in BeforeExecuted: {ex.Message}", ex);
            }
        }

        // ── DocumentChanged: always-on "Add" monitoring ───────────────────────────

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (e.Operation != UndoOperation.TransactionCommitted) return;

            try
            {
                var doc = e.GetDocument();
                if (doc == null) return;

                var modifiedIds = e.GetModifiedElementIds();
                if (modifiedIds == null || modifiedIds.Count == 0) return;

                foreach (var id in modifiedIds)
                {
                    Element element;
                    try { element = doc.GetElement(id); } catch { continue; }

                    if (!(element is Group group) || !group.IsValidObject) continue;

                    ICollection<ElementId> currentMemberIds;
                    try { currentMemberIds = group.GetMemberIds(); }
                    catch { continue; }

                    long groupKey = id.GetIdValue();

                    if (!_groupMemberSnapshots.TryGetValue(groupKey, out var snapshot))
                    {
                        // First time we see this group — take a baseline snapshot and move on.
                        // We don't block here: we have no "before" state to diff against.
                        _groupMemberSnapshots[groupKey] = new HashSet<long>(
                            currentMemberIds.Select(m => m.GetIdValue()));
                        continue;
                    }

                    // Find members that were not in the snapshot (newly added this transaction)
                    var newMembers = currentMemberIds
                        .Where(memberId => !snapshot.Contains(memberId.GetIdValue()))
                        .Select(memberId =>
                        {
                            Element? el = null;
                            try { el = doc.GetElement(memberId); } catch { }
                            return (id: memberId, el);
                        })
                        .Where(t => t.el != null && t.el.IsValidObject)
                        .ToList();

                    var newlyAddedPinned = newMembers.Where(t => t.el!.Pinned).ToList();

                    if (newlyAddedPinned.Count == 0)
                    {
                        // No violation — update snapshot with all new members
                        foreach (var memberId in currentMemberIds)
                            snapshot.Add(memberId.GetIdValue());
                        continue;
                    }

                    // Violation — update snapshot only with NON-pinned new members.
                    // Pinned ones are being undone, so they must stay absent from the snapshot
                    // so the next attempt is also detected.
                    foreach (var t in newMembers.Where(t => !t.el!.Pinned))
                        snapshot.Add(t.id.GetIdValue());

                    _pinnedAddedCount = newlyAddedPinned.Count;

                    // Post Undo immediately from DocumentChanged — same approach as ElementBypassDetector.
                    try
                    {
                        var undoId = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
                        if (undoId != null)
                            UIApp.PostCommand(undoId);
                        Logger?.LogWarning($"[GroupBinding] {newlyAddedPinned.Count} pinned element(s) added to group — Undo posted");
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogError($"[GroupBinding] Failed to post Undo: {ex.Message}", ex);
                    }

                    // Guard: only register Idling once per undo cycle
                    if (!_showGroupAddDialogPending)
                    {
                        _showGroupAddDialogPending = true;
                        UIApp.Idling += OnIdlingShowGroupAddDialog;
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[GroupBinding] Error in DocumentChanged: {ex.Message}", ex);
            }
        }

        private void OnIdlingShowGroupAddDialog(object sender, IdlingEventArgs e)
        {
            UIApp.Idling -= OnIdlingShowGroupAddDialog;
            if (!_showGroupAddDialogPending) return;

            _showGroupAddDialogPending = false;
            var count = _pinnedAddedCount;
            _pinnedAddedCount = 0;

            try
            {
                ShowPinnedAlert(count, "Group");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[GroupBinding] Error showing group-add dialog: {ex.Message}", ex);
            }
        }

        // ── Label helpers ──────────────────────────────────────────────────────────
        // Use CommandId comparison — Revit's internal name for "Create Group" contains
        // "EDIT", so string-based detection returns the wrong label.

        private string GetOperationLabel(RevitCommandId? commandId)
        {
            if (_ungroupCommandId != null && commandId?.Id == _ungroupCommandId.Id)
                return "Ungroup";
            if (_editGroupCommandId != null && commandId?.Id == _editGroupCommandId.Id)
                return "Edit Group";
            return "Create Group";
        }

        private bool IsUngroupOrEditGroup(RevitCommandId? commandId) =>
            (_ungroupCommandId != null && commandId?.Id == _ungroupCommandId.Id) ||
            (_editGroupCommandId != null && commandId?.Id == _editGroupCommandId.Id);

        // ── Pinned / protected element checks ─────────────────────────────────────

        private int CountPinnedElementsForOperation(Document doc, RevitCommandId? commandId, ICollection<ElementId> selectedIds)
        {
            try
            {
                var elements = selectedIds
                    .Select(id => doc.GetElement(id))
                    .Where(el => el != null && el.IsValidObject)
                    .ToList();

                var pinnedCount = elements.Count(el => el.Pinned);

                if (IsUngroupOrEditGroup(commandId))
                {
                    foreach (var el in elements)
                    {
                        if (!(el is Group group)) continue;
                        ICollection<ElementId> memberIds;
                        try { memberIds = group.GetMemberIds(); } catch { continue; }
                        pinnedCount += memberIds
                            .Select(id => doc.GetElement(id))
                            .Count(m => m != null && m.IsValidObject && m.Pinned);
                    }
                }

                return pinnedCount;
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[GroupBinding] Error counting pinned elements: {ex.Message}", ex);
                return 0;
            }
        }

        private List<ProtectedPinInfo> GetProtectedElementsForOperation(Document doc, RevitCommandId? commandId, ICollection<ElementId> selectedIds)
        {
            var result = new List<ProtectedPinInfo>();
            try
            {
                var elements = selectedIds
                    .Select(id => doc.GetElement(id))
                    .Where(el => el != null && el.IsValidObject)
                    .ToList();

                result.AddRange(PinProtectionStorage.GetProtectedFromSelection(elements));

                if (IsUngroupOrEditGroup(commandId))
                {
                    foreach (var element in elements)
                    {
                        if (!(element is Group group)) continue;
                        ICollection<ElementId> memberIds;
                        try { memberIds = group.GetMemberIds(); } catch { continue; }

                        var members = memberIds
                            .Select(id => doc.GetElement(id))
                            .Where(m => m != null && m.IsValidObject)
                            .ToList();

                        foreach (var p in PinProtectionStorage.GetProtectedFromSelection(members))
                        {
                            if (result.All(r => r.ElementId != p.ElementId))
                                result.Add(p);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[GroupBinding] Error finding protected elements: {ex.Message}", ex);
            }
            return result;
        }

        // ── Dialogs ───────────────────────────────────────────────────────────────

        private static void ShowPinnedAlert(int pinnedCount, string operationLabel)
        {
            var vm = new BIManageRevit.BIManage.ViewModels.Bindings.PinnedElementAlertViewModel(
                pinnedCount,
                $"Pinned elements cannot be used in a {operationLabel}. Unpin them first.",
                () => { },
                headerText: $"Cannot {operationLabel} Pinned Elements",
                subText: $"These elements are pinned and cannot be used in a {operationLabel}.");

            var dialog = new BIManageRevit.BIManage.Views.Bindings.PinnedElementAlertDialog
            {
                DataContext = vm
            };
            BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);
            dialog.ShowDialog();
        }

        private static void ShowBlockDialog(List<ProtectedPinInfo> elements, string operationLabel)
        {
            var elementNames = string.Join(", ", elements.Take(5).Select(el => el.Name ?? "Unknown"));
            if (elements.Count > 5)
                elementNames += $" and {elements.Count - 5} more";

            var message = $"⛔ Cannot {operationLabel} pin-protected elements:\n\n{elementNames}\n\n" +
                          "These elements are protected with PIN PROTECTION.\n" +
                          "Group operations are blocked to preserve design integrity.\n\n" +
                          "Contact your BIM administrator to modify these elements.";

            var blockViewModel = new BIManageRevit.BIManage.ViewModels.Bindings.ProtectedPinBlockViewModel(
                elements.Count, elementNames, operationLabel, message, () => { });

            var blockDialog = new BIManageRevit.BIManage.Views.Bindings.BlockedOperationDialog
            {
                DataContext = blockViewModel
            };
            BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(blockDialog);
            blockDialog.ShowDialog();
        }

        // ── Infrastructure ────────────────────────────────────────────────────────

        private static RevitCommandId? LookupCommandById(int targetId)
        {
            foreach (PostableCommand pc in Enum.GetValues(typeof(PostableCommand)))
            {
                try
                {
                    var id = RevitCommandId.LookupPostableCommandId(pc);
                    if (id != null && (int)id.Id == targetId)
                        return id;
                }
                catch { }
            }
            return null;
        }

        public override void Register() => RegisterWithBeforeExecute();

        public override void Unregister()
        {
            UIApp.Application.DocumentChanged -= OnDocumentChanged;
            UIApp.Idling -= OnIdlingShowGroupAddDialog;

            UnregisterSingle(_groupBinding,     "Group");
            UnregisterSingle(_ungroupBinding,   "Ungroup");
            UnregisterSingle(_editGroupBinding, "EditGroup");
        }

        private void UnregisterSingle(AddInCommandBinding? binding, string name)
        {
            if (binding == null) return;
            try
            {
                binding.BeforeExecuted -= OnBeforeExecuted;
                Logger?.LogDebug($"[GroupBinding] Unregistered: {name}");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[GroupBinding] Error unregistering {name}: {ex.Message}");
            }
        }
    }
}
