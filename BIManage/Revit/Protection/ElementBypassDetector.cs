using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using BIManage.Common.Helpers;
using BIManage.Core.Features;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Commands;
using BIManage.Revit.Commands.Bindings;
using BIManage.Revit.EventRegistry;
using BIManage.Revit.PinProtection;

namespace BIManage.Revit.Protection
{
    /// <summary>
    /// Detects bypass gestures that don't fire a Revit command id (and therefore bypass the
    /// command-binding stack): direct drag-to-move, arrow-key nudge, Ctrl+drag duplicate,
    /// Ctrl+V paste-via-non-binding-path, and property-palette location edits.
    ///
    /// Wired into EventRegistryService.OnDocumentChanged AFTER the pin-bypass + CAD-Explode
    /// post-action handlers. For each tracked transaction name, mirrors the central command
    /// binding's Priority 1 + Priority 2 chain (CommandProtection then Rule) and queues a revert
    /// when the user blocks the action:
    ///   • Move-class → PositionRestorationService.QueueUndo (Idling tick posts Undo for
    ///     non-pin-protected elements; pin-protected elements are already handled upstream by
    ///     CheckProtectedElementPositions).
    ///   • Copy-class → PositionRestorationService.QueueDeletion of the added element ids.
    /// </summary>
    public class ElementBypassDetector
    {
        private readonly ILogger? _logger;
        private readonly IFeatureToggleService _featureToggle;
        private readonly Func<CommandProtectionBinding?>? _commandProtectionGetter;
        private readonly Func<IRuleCommandInterceptor?>? _ruleInterceptorGetter;
        private readonly PositionRestorationService? _restorationService;

        public ElementBypassDetector(
            ILogger? logger,
            IFeatureToggleService featureToggle,
            Func<CommandProtectionBinding?>? commandProtectionGetter,
            Func<IRuleCommandInterceptor?>? ruleInterceptorGetter,
            PositionRestorationService? restorationService)
        {
            _logger = logger;
            _featureToggle = featureToggle ?? throw new ArgumentNullException(nameof(featureToggle));
            _commandProtectionGetter = commandProtectionGetter;
            _ruleInterceptorGetter = ruleInterceptorGetter;
            _restorationService = restorationService;
        }

        /// <summary>
        /// Called from EventRegistryService.OnDocumentChanged after pin-bypass + CAD-Explode
        /// handlers. Filters transaction names, runs the Priority 1+2 chain, queues revert on block.
        /// </summary>
        public void Process(DocumentChangedEventArgs e)
        {
            try
            {
                if (_featureToggle.IsGlobalPaused) return;
                if (e.Operation != UndoOperation.TransactionCommitted) return;

                var document = e.GetDocument();
                if (document == null || !document.IsValidObject) return;

                // Map transaction name → (canonical command code, gesture-source label).
                var txNames = e.GetTransactionNames();
                if (txNames == null || txNames.Count == 0) return;

                var mapping = MapTransactionToCommand(txNames);
                if (mapping == null) return;

                // Re-entrancy guard: a ribbon click that already ran the priority chain marked
                // this transaction as already-handled. Consume only AFTER confirming the
                // transaction matches a tracked op (else we'd drain the flag spuriously).
                if (CommandBypassAuditGate.ConsumeSuppressed())
                {
                    _logger?.LogDebug($"[BypassDetector] Skipping {mapping.Value.commandCode} — already handled by command binding");
                    return;
                }

                var (commandCode, gestureSource, isCopy) = mapping.Value;

                var modifiedIds = e.GetModifiedElementIds();
                var addedIds = e.GetAddedElementIds();

                List<ElementId> targetIds;
                if (isCopy)
                {
                    if (addedIds == null || addedIds.Count == 0) return;
                    targetIds = addedIds.ToList();
                }
                else
                {
                    if (modifiedIds == null || modifiedIds.Count == 0) return;
                    targetIds = modifiedIds.ToList();
                }

                var elements = ResolveElements(document, targetIds);
                if (elements.Count == 0)
                {
                    _logger?.LogDebug($"[BypassDetector] {gestureSource}: no eligible elements (pin-protected elements deferred to pin handler)");
                    return;
                }

                _logger?.LogInfo($"[BypassDetector] {gestureSource}: evaluating {elements.Count} element(s) against {commandCode}");

                // Priority 1 — Command Protection.
                var cmdProtection = _commandProtectionGetter?.Invoke();
                if (cmdProtection != null)
                {
                    var p1 = cmdProtection.EvaluateCommand(commandCode, document, elements, gestureSource);
                    if (p1.HasProtection)
                    {
                        if (!p1.Allowed)
                        {
                            _logger?.LogWarning($"[BypassDetector] {gestureSource}: blocked by Command Protection — queuing revert");
                            QueueRevert(isCopy, document, addedIds, gestureSource);
                            return;
                        }
                        // Notify mode only logged — no dialog shown, no user decision made.
                        // Fall through so Priority 2 (Rule) can still evaluate the element.
                        // For Assist/Protect where the user chose Allow, stop here to prevent
                        // a second dialog from the Rule evaluator.
                        if (p1.Mode != InterventionMode.Notify)
                            return;
                    }
                }

                // Priority 2 — Rule.
                var ruleInterceptor = _ruleInterceptorGetter?.Invoke();
                if (ruleInterceptor != null)
                {
                    var commandId = ResolveCommandIdInt(commandCode);
                    var friendlyName = CommandNameResolver.GetFriendlyName(commandCode);
                    var p2 = ruleInterceptor.EvaluateForBypass(commandId, friendlyName, document, elements, gestureSource);
                    if (p2.HasMatch && !p2.Allowed)
                    {
                        _logger?.LogWarning($"[BypassDetector] {gestureSource}: blocked by Rule — queuing revert");
                        QueueRevert(isCopy, document, addedIds, gestureSource);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[BypassDetector] Error in Process: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Filter the modified/added id list to elements we want to evaluate:
        /// • Valid, non-null, exists in doc.
        /// • Not pin-protected (those go through the existing PIN bypass handler in
        ///   EventRegistryService.CheckProtectedElementPositions).
        /// </summary>
        private List<Element> ResolveElements(Document document, IEnumerable<ElementId> ids)
        {
            var list = new List<Element>();
            foreach (var id in ids)
            {
                if (id == null || id == ElementId.InvalidElementId) continue;
                var element = document.GetElement(id);
                if (element == null || !element.IsValidObject) continue;
                // Defer pin-protected elements to the existing pin-bypass handler (it already
                // queued PositionRestorationService.QueueRestoration with the exact stored pos).
                try { if (PinProtectionStorage.IsProtected(element)) continue; }
                catch { /* IsProtected can throw if ExtensibleStorage schema isn't ready — treat as not protected */ }
                list.Add(element);
            }
            return list;
        }

        private void QueueRevert(bool isCopy, Document document, ICollection<ElementId>? addedIds, string gestureSource)
        {
            if (isCopy)
            {
                if (_restorationService == null)
                {
                    _logger?.LogWarning($"[BypassDetector] {gestureSource}: no PositionRestorationService — copy revert skipped");
                    return;
                }
                if (addedIds != null && addedIds.Count > 0)
                    _restorationService.QueueDeletion(addedIds.ToList(), document, gestureSource);
            }
            else
            {
                // Post Undo directly from the current DocumentChanged context — valid per Revit API
                // docs and more reliable than the IdlingService queue: the WPF modal dialog shown by
                // EvaluateForBypass runs a nested message loop that lets Revit fire events before
                // Idling drains the queue, potentially displacing the blocked transaction from the
                // top of the undo stack by the time PostCommand(Undo) eventually fires from Idling.
                try
                {
                    var undoId = Autodesk.Revit.UI.RevitCommandId.LookupPostableCommandId(
                        Autodesk.Revit.UI.PostableCommand.Undo);
                    if (undoId != null)
                    {
                        new Autodesk.Revit.UI.UIApplication(document.Application).PostCommand(undoId);
                        _logger?.LogInfo($"[BypassDetector] {gestureSource}: PostCommand(Undo) posted");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"[BypassDetector] {gestureSource}: direct PostCommand(Undo) failed ({ex.Message}), falling back to IdlingService queue");
                }

                if (_restorationService != null)
                    _restorationService.QueueUndo(document, gestureSource);
                else
                    _logger?.LogWarning($"[BypassDetector] {gestureSource}: no PositionRestorationService — fallback revert skipped");
            }
        }

        /// <summary>
        /// Map Revit transaction names to canonical command codes + gesture-source label.
        /// • Move/Drag/Nudge → ID_OBJECTS_MOVE
        /// • Copy/Paste → ID_EDIT_COPY (Command Protection cache uses the ribbon copy id)
        /// • Modify element parameter → ID_OBJECTS_MOVE (palette location edits surface as move)
        /// Returns null for unknown transactions — we don't try to cover everything, only the
        /// gestures the user explicitly called out (Move, Copy, Cut).
        /// </summary>
        private static (string commandCode, string gestureSource, bool isCopy)? MapTransactionToCommand(IList<string> txNames)
        {
            foreach (var raw in txNames)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                var name = raw.Trim();
                if (string.Equals(name, "Move", StringComparison.Ordinal))
                    return ("ID_OBJECTS_MOVE", "Move bypass", false);
                if (string.Equals(name, "Drag elements", StringComparison.Ordinal) ||
                    string.Equals(name, "Drag Elements", StringComparison.Ordinal))
                    return ("ID_OBJECTS_MOVE", "Drag bypass", false);
                if (string.Equals(name, "Nudge", StringComparison.Ordinal))
                    return ("ID_OBJECTS_MOVE", "Nudge bypass", false);
                if (string.Equals(name, "Copy", StringComparison.Ordinal))
                    return ("ID_EDIT_COPY", "Copy bypass", true);
                if (string.Equals(name, "Paste", StringComparison.Ordinal))
                    return ("ID_EDIT_CLIPBOARD_PASTE", "Paste bypass", true);
                if (name.StartsWith("Modify element", StringComparison.Ordinal))
                    return ("ID_OBJECTS_MOVE", "Property palette", false);
                if (string.Equals(name, "Align", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Align", StringComparison.OrdinalIgnoreCase))
                    return ("ID_ALIGN", "Align bypass", false);
                if (string.Equals(name, "Offset", StringComparison.OrdinalIgnoreCase))
                    return ("ID_OBJECTS_OFFSET", "Offset bypass", false);
            }
            return null;
        }

        /// <summary>
        /// Best-effort int command id for the Rule evaluator. Returns 0 when unresolvable;
        /// the rule evaluator treats 0 as no-match-fail-open, which is correct for the
        /// bypass path (we'd rather under-enforce than mis-attribute).
        /// Uses CommandNameResolver first (same strategy as the ribbon path) so bypass-path
        /// command IDs match what rules store — otherwise Revit API lookup can return 0 for
        /// commands like ID_ALIGN and rule evaluation silently bails out.
        /// </summary>
        private static int ResolveCommandIdInt(string commandCode)
        {
            // Primary: name-based resolution — same dictionary the ribbon path uses.
            // Produces the PostableCommand integer stored in rule.CommandIds.
            var nameBasedId = CommandNameResolver.ResolveNameToId(commandCode);
            if (nameBasedId != 0)
                return nameBasedId;

            // Secondary: Revit API lookup (fallback for commands not in the resolver dict).
            try
            {
                var rcid = Autodesk.Revit.UI.RevitCommandId.LookupCommandId(commandCode);
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
    }
}
