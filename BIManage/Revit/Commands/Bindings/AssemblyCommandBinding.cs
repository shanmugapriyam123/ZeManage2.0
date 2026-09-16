using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Commands;

namespace BIManage.Revit.Commands.Bindings
{
    /// <summary>
    /// Dedicated binding for the Create Assembly command and assembly member protection.
    ///
    /// Three protection paths:
    ///   1. Select-first → Create Assembly: BeforeExecuted checks selection and cancels.
    ///   2. Create Assembly with pinned members (any path): DocumentChanged detects a new
    ///      AssemblyInstance in addedIds whose members include a pinned element → Undo + dialog.
    ///   3. Edit Assembly "Add" button (select-first or command-first): DocumentChanged detects
    ///      a new pinned member added to an existing AssemblyInstance in modifiedIds → Undo + dialog.
    ///
    /// Paths 2 and 3 use an always-on per-assembly snapshot dictionary — no session flag,
    /// no external call needed (mirrors the GroupCommandBinding pattern).
    /// </summary>
    public class AssemblyCommandBinding : CommandBindingBase
    {
        private AddInCommandBinding? _createBinding;
        private RevitCommandId? _createCommandId;

        private readonly IRuleCommandInterceptor? _ruleInterceptor;
        private readonly Func<CommandProtectionBinding?> _commandProtectionGetter;
        private readonly BIManage.Core.Features.IFeatureToggleService? _featureToggleService;

        // Per-assembly member snapshot: assemblyId → set of member ElementId values.
        // Populated lazily on first DocumentChanged touch; diffs on every subsequent commit.
        private readonly Dictionary<long, HashSet<long>> _assemblyMemberSnapshots = new Dictionary<long, HashSet<long>>();
        private volatile bool _showAssemblyDialogPending = false;
        private int _pinnedCount = 0;

        public AssemblyCommandBinding(
            UIApplication uiApp,
            ILogger logger,
            IRuleCommandInterceptor? ruleInterceptor,
            Func<CommandProtectionBinding?>? commandProtectionGetter = null,
            BIManage.Core.Features.IFeatureToggleService? featureToggleService = null)
            : base(uiApp, logger)
        {
            _ruleInterceptor = ruleInterceptor;
            _commandProtectionGetter = commandProtectionGetter ?? (() => null);
            _featureToggleService = featureToggleService;
            CommandId = TryLookup(PostableCommand.CreateAssembly);
        }

        public override void RegisterWithBeforeExecute()
        {
            _createCommandId = TryLookup(PostableCommand.CreateAssembly);
            RegisterSingle(ref _createBinding, _createCommandId, "CreateAssembly");
            UIApp.Application.DocumentChanged += OnDocumentChanged;
        }

        private void RegisterSingle(ref AddInCommandBinding? field, RevitCommandId? commandId, string name)
        {
            if (commandId == null || !commandId.CanHaveBinding)
            {
                Logger?.LogWarning($"[AssemblyBinding] {name} command cannot have a binding — skipping");
                return;
            }
            try
            {
                field = UIApp.CreateAddInCommandBinding(commandId);
                field.BeforeExecuted += OnBeforeExecuted;
                MarkAsRegistered(commandId.Name);
                Logger?.LogInfo($"[AssemblyBinding] Registered: {name} ({commandId.Name})");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[AssemblyBinding] Failed to register {name}: {ex.Message}", ex);
            }
        }

        // ── BeforeExecuted: select-first create path ──────────────────────────────

        private void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e)
        {
            try
            {
                if (_featureToggleService?.IsGlobalPaused == true || _featureToggleService?.IsEmployeeCaptureDisabled == true)
                    return;

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

                var selectedIds = UIApp.ActiveUIDocument?.Selection?.GetElementIds();
                if (selectedIds == null || selectedIds.Count == 0) return;

                var pinnedCount = selectedIds
                    .Select(id => doc.GetElement(id))
                    .Count(el => el != null && el.IsValidObject && el.Pinned);

                if (pinnedCount > 0)
                {
                    ShowPinnedAlert(pinnedCount, "Create Assembly");
                    if (e.Cancellable)
                    {
                        e.Cancel = true;
                        Logger?.LogWarning($"[AssemblyBinding] Create Assembly blocked: {pinnedCount} pinned element(s)");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[AssemblyBinding] Error in BeforeExecuted: {ex.Message}", ex);
            }
        }

        // ── DocumentChanged: always-on assembly monitoring ────────────────────────

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (e.Operation != UndoOperation.TransactionCommitted) return;
            if (_featureToggleService?.IsGlobalPaused == true || _featureToggleService?.IsEmployeeCaptureDisabled == true) return;

            try
            {
                var doc = e.GetDocument();
                if (doc == null) return;

                // Path 1 — newly CREATED assembly: check if any member is pinned
                var addedIds = e.GetAddedElementIds();
                if (addedIds != null)
                {
                    foreach (var id in addedIds)
                    {
                        AssemblyInstance? assembly;
                        try { assembly = doc.GetElement(id) as AssemblyInstance; } catch { continue; }
                        if (assembly == null || !assembly.IsValidObject) continue;

                        ICollection<ElementId> members;
                        try { members = assembly.GetMemberIds(); } catch { continue; }

                        var pinnedMembers = members
                            .Select(m => { try { return doc.GetElement(m); } catch { return null; } })
                            .Where(m => m != null && m.IsValidObject && m.Pinned)
                            .ToList();

                        // Record snapshot regardless so modifiedIds path has a baseline
                        _assemblyMemberSnapshots[id.GetIdValue()] = new HashSet<long>(
                            members.Select(m => m.GetIdValue()));

                        if (pinnedMembers.Count == 0) continue;

                        _pinnedCount = pinnedMembers.Count;
                        PostUndo();
                        if (!_showAssemblyDialogPending)
                        {
                            _showAssemblyDialogPending = true;
                            UIApp.Idling += OnIdlingShowDialog;
                        }
                        Logger?.LogWarning($"[AssemblyBinding] Assembly created with {pinnedMembers.Count} pinned member(s) — Undo posted");
                        return;
                    }
                }

                // Path 2 — MODIFIED assembly: diff members against snapshot
                var modifiedIds = e.GetModifiedElementIds();
                if (modifiedIds == null || modifiedIds.Count == 0) return;

                foreach (var id in modifiedIds)
                {
                    AssemblyInstance? assembly;
                    try { assembly = doc.GetElement(id) as AssemblyInstance; } catch { continue; }
                    if (assembly == null || !assembly.IsValidObject) continue;

                    ICollection<ElementId> currentMembers;
                    try { currentMembers = assembly.GetMemberIds(); } catch { continue; }

                    long key = id.GetIdValue();

                    if (!_assemblyMemberSnapshots.TryGetValue(key, out var snapshot))
                    {
                        // First time seeing this assembly — baseline snapshot, no block
                        _assemblyMemberSnapshots[key] = new HashSet<long>(currentMembers.Select(m => m.GetIdValue()));
                        continue;
                    }

                    var newMembers = currentMembers
                        .Where(m => !snapshot.Contains(m.GetIdValue()))
                        .Select(m =>
                        {
                            Element? el = null;
                            try { el = doc.GetElement(m); } catch { }
                            return (id: m, el);
                        })
                        .Where(t => t.el != null && t.el.IsValidObject)
                        .ToList();

                    var newlyAddedPinned = newMembers.Where(t => t.el!.Pinned).ToList();

                    if (newlyAddedPinned.Count == 0)
                    {
                        // No violation — update snapshot with all new members
                        foreach (var m in currentMembers)
                            snapshot.Add(m.GetIdValue());
                        continue;
                    }

                    // Violation — update snapshot only with NON-pinned new members.
                    // Pinned ones are being undone, so they must stay absent from the snapshot
                    // so the next attempt is also detected.
                    foreach (var t in newMembers.Where(t => !t.el!.Pinned))
                        snapshot.Add(t.id.GetIdValue());

                    _pinnedCount = newlyAddedPinned.Count;
                    PostUndo();
                    if (!_showAssemblyDialogPending)
                    {
                        _showAssemblyDialogPending = true;
                        UIApp.Idling += OnIdlingShowDialog;
                    }
                    Logger?.LogWarning($"[AssemblyBinding] {newlyAddedPinned.Count} pinned element(s) added to assembly — Undo posted");
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[AssemblyBinding] Error in DocumentChanged: {ex.Message}", ex);
            }
        }

        private void OnIdlingShowDialog(object sender, IdlingEventArgs e)
        {
            UIApp.Idling -= OnIdlingShowDialog;
            if (!_showAssemblyDialogPending) return;

            _showAssemblyDialogPending = false;
            var count = _pinnedCount;
            _pinnedCount = 0;

            try
            {
                ShowPinnedAlert(count, "Assembly");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[AssemblyBinding] Error showing dialog: {ex.Message}", ex);
            }
        }

        private void PostUndo()
        {
            try
            {
                var undoId = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
                if (undoId != null)
                    UIApp.PostCommand(undoId);
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[AssemblyBinding] Failed to post Undo: {ex.Message}", ex);
            }
        }

        private static void ShowPinnedAlert(int pinnedCount, string operationLabel)
        {
            var vm = new BIManageRevit.BIManage.ViewModels.Bindings.PinnedElementAlertViewModel(
                pinnedCount,
                "Pinned elements cannot be added to an Assembly. Unpin them first.",
                () => { },
                headerText: $"Cannot {operationLabel} with Pinned Elements",
                subText: "These elements are pinned and cannot be added to an Assembly.");

            var dialog = new BIManageRevit.BIManage.Views.Bindings.PinnedElementAlertDialog
            {
                DataContext = vm
            };
            BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);
            dialog.ShowDialog();
        }

        private static RevitCommandId? TryLookup(PostableCommand command)
        {
            try { return RevitCommandId.LookupPostableCommandId(command); }
            catch { return null; }
        }

        public override void Register() => RegisterWithBeforeExecute();

        public override void Unregister()
        {
            UIApp.Application.DocumentChanged -= OnDocumentChanged;
            UIApp.Idling -= OnIdlingShowDialog;
            UnregisterSingle(_createBinding, "CreateAssembly");
        }

        private void UnregisterSingle(AddInCommandBinding? binding, string name)
        {
            if (binding == null) return;
            try
            {
                binding.BeforeExecuted -= OnBeforeExecuted;
                Logger?.LogDebug($"[AssemblyBinding] Unregistered: {name}");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[AssemblyBinding] Error unregistering {name}: {ex.Message}");
            }
        }
    }
}
