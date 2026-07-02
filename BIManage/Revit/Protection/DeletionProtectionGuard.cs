using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Helpers;
using BIManage.Revit.PinProtection;

namespace BIManage.Revit.Protection
{
    /// <summary>
    /// Guards against deletion of BIManage pin-protected elements via any code path —
    /// command bindings, API calls, scripts, or gestures that bypass BeforeExecuted.
    ///
    /// Why a live cache instead of a SQLite query on deletion:
    ///   After an element is deleted, doc.GetElement(id) returns null — ExtensibleStorage is
    ///   gone with the element. We must capture pinned state BEFORE deletion. We also cannot
    ///   rely on the SQLite ElementId column because element IDs can change after central sync.
    ///
    /// Cache strategy:
    ///   On every DocumentChanged commit, AddedElementIds and ModifiedElementIds are scanned;
    ///   elements whose element.Pinned == true are added to _pinnedIds, others are removed.
    ///   When DeletedElementIds arrive, we check against _pinnedIds — the pinned state was
    ///   captured while the element still existed.
    ///   Bootstrap: on document open, all protected elements are loaded from ExtensibleStorage
    ///   via PinProtectionStorage and added to the cache so the guard works from the first event.
    ///
    /// Undo mechanism:
    ///   PostCommand(PostableCommand.Undo) is posted directly from DocumentChanged — same
    ///   proven pattern as ElementBypassDetector.QueueRevert. The dialog is shown on the
    ///   next Idling tick, by which time the element has been restored.
    /// </summary>
    public class DeletionProtectionGuard
    {
        private readonly ILogger? _logger;

        // ElementId values (long) of currently-pinned elements, kept current on every
        // DocumentChanged commit. Checked against deletedIds before element is gone.
        private readonly HashSet<long> _pinnedIds = new HashSet<long>();

        // No Idling state needed — dialog is shown directly from DocumentChanged.

        public DeletionProtectionGuard(ILogger? logger)
        {
            _logger = logger;
        }

        // ── Bootstrap ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Pre-populate the cache at document open from ExtensibleStorage so the guard
        /// works immediately without waiting for the first DocumentChanged scan.
        /// Call this once after the document is fully loaded.
        /// </summary>
        public void Bootstrap(Document doc)
        {
            if (doc == null || !doc.IsValidObject) return;
            try
            {
                // GetAllProtectedElements returns ProtectedPinInfo records (data model),
                // not Revit Element objects. Add each element's integer ID to the cache —
                // all returned entries are by definition protected and pinned.
                var protectedInfos = PinProtectionStorage.GetAllProtectedElements(doc);
                foreach (var info in protectedInfos)
                    _pinnedIds.Add((long)info.ElementId);

                _logger?.LogDebug($"[DeletionGuard] Bootstrap: {_pinnedIds.Count} protected element(s) cached");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[DeletionGuard] Bootstrap failed: {ex.Message}", ex);
            }
        }

        /// <summary>Clear the cache when a document is closed.</summary>
        public void ClearCache() => _pinnedIds.Clear();

        // ── Main entry point ──────────────────────────────────────────────────────

        /// <summary>
        /// Called from EventRegistryService.OnDocumentChanged for every committed transaction.
        /// Updates the pinned-element cache from added/modified elements, then checks deleted
        /// elements against the cache.
        /// </summary>
        public void Process(DocumentChangedEventArgs e, UIApplication uiApp)
        {
            if (e.Operation != UndoOperation.TransactionCommitted) return;

            try
            {
                var doc = e.GetDocument();
                if (doc == null || !doc.IsValidObject) return;

                // Step 1 — Update cache from elements that still exist in the document.
                UpdateCacheFromIds(doc, e.GetAddedElementIds());
                UpdateCacheFromIds(doc, e.GetModifiedElementIds());

                // Step 2 — Check deleted elements against the cache.
                var deletedIds = e.GetDeletedElementIds();
                if (deletedIds == null || deletedIds.Count == 0) return;

                bool found = false;
                foreach (var id in deletedIds)
                {
                    long idValue = id.GetIdValue();
                    bool wasPinned = _pinnedIds.Contains(idValue);

                    if (!wasPinned)
                    {
                        // Not a protected element — clean it out of the cache
                        _pinnedIds.Remove(idValue);
                        continue;
                    }

                    // Pinned element deleted — do NOT remove from cache.
                    // The Undo we are about to post will restore the element,
                    // so its ID must remain tracked for any subsequent attempts.
                    found = true;
                    _logger?.LogWarning($"[DeletionGuard] Pinned element {idValue} deleted — blocking");
                    break;
                }

                if (!found) return;

                // ── ElementBypassDetector pattern ──────────────────────────────────
                // Show dialog FIRST (from DocumentChanged, same thread).
                // The modal dialog runs a nested WPF message loop — while it is open no
                // new Revit transactions can commit, so the deletion stays at the top of
                // the undo stack. PostCommand(Undo) is posted AFTER the dialog closes,
                // guaranteeing it undoes the correct transaction every time.
                ShowBlockDialog();

                try
                {
                    var undoId = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
                    if (undoId != null)
                        uiApp.PostCommand(undoId);
                    _logger?.LogInfo("[DeletionGuard] Undo posted after dialog — element will be restored");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"[DeletionGuard] Failed to post Undo: {ex.Message}", ex);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[DeletionGuard] Error in Process: {ex.Message}", ex);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private void UpdateCacheFromIds(Document doc, ICollection<ElementId> ids)
        {
            if (ids == null || ids.Count == 0) return;
            foreach (var id in ids)
            {
                try
                {
                    var el = doc.GetElement(id);
                    if (el == null || !el.IsValidObject)
                    {
                        _pinnedIds.Remove(id.GetIdValue());
                        continue;
                    }

                    // Track only BIManage protected pins (not normal Revit pins), so the
                    // guard scope matches Bootstrap (which loads GetAllProtectedElements).
                    if (PinProtectionStorage.IsProtected(el))
                        _pinnedIds.Add(id.GetIdValue());
                    else
                        _pinnedIds.Remove(id.GetIdValue());
                }
                catch { /* non-critical — skip this element */ }
            }
        }

        private void ShowBlockDialog()
        {
            try
            {
                var vm = new BIManageRevit.BIManage.ViewModels.Bindings.PinnedElementAlertViewModel(
                    1,
                    "Pinned elements cannot be deleted. Unpin the element first if you need to delete it.",
                    () => { },
                    headerText: "Cannot Delete Pinned Element",
                    subText: "The deletion will be automatically reversed.");

                var dialog = new BIManageRevit.BIManage.Views.Bindings.PinnedElementAlertDialog
                {
                    DataContext = vm
                };
                BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[DeletionGuard] Error showing dialog: {ex.Message}", ex);
            }
        }
    }
}
