using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Common.Helpers;
using BIManage.Revit.PinProtection;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.EventRegistry
{
    /// <summary>
    /// Queues and executes revert actions for protected elements affected by bypass gestures
    /// (drag, nudge, Ctrl+drag duplicate, property-palette edit). Uses the Idling event so
    /// reverts run in a valid Revit API context, not inside DocumentChanged.
    ///
    /// Three queues:
    /// • Restoration — restores pin-protected elements to their stored ExtensibleStorage position
    ///   (existing pin-bypass detector behavior).
    /// • Deletion — deletes element IDs that were added by a Copy/Paste/Ctrl+Drag bypass and the
    ///   user blocked.
    /// • Undo — posts PostableCommand.Undo via UIApplication for Move bypasses on non-pin-protected
    ///   elements where we don't have a stored original position. Reverts the last user transaction.
    /// </summary>
    public class PositionRestorationService : IDisposable
    {
        private readonly ConcurrentQueue<RestorationRequest> _restorationQueue;
        private readonly ConcurrentQueue<DeletionRequest> _deletionQueue;
        private readonly ConcurrentQueue<UndoRequest> _undoQueue;
        private readonly ILogger _logger;
        private bool _disposed;

        public PositionRestorationService(ILogger logger)
        {
            _logger = logger;
            _restorationQueue = new ConcurrentQueue<RestorationRequest>();
            _deletionQueue = new ConcurrentQueue<DeletionRequest>();
            _undoQueue = new ConcurrentQueue<UndoRequest>();
        }

        /// <summary>
        /// Queue an element for position restoration
        /// Called from DocumentChanged handler when bypass is detected
        /// </summary>
        public void QueueRestoration(ElementId elementId, Document document, string bypassMethod)
        {
            if (elementId == null || document == null || !document.IsValidObject)
                return;

            var request = new RestorationRequest
            {
                ElementId = elementId,
                DocumentHashCode = document.GetHashCode(),
                BypassMethod = bypassMethod,
                QueuedAt = DateTime.UtcNow
            };

            _restorationQueue.Enqueue(request);
            _logger?.LogDebug($"Queued position restoration for element {elementId.GetIdValue()} (bypass: {bypassMethod})");
        }

        /// <summary>
        /// Queue added element IDs for deletion (Copy/Paste/Ctrl+Drag bypass blocked).
        /// Called from ElementBypassDetector when the user cancelled an Assist dialog or
        /// failed OTP on a Protect-mode copy rule.
        /// </summary>
        public void QueueDeletion(IList<ElementId> addedIds, Document document, string bypassMethod)
        {
            if (addedIds == null || addedIds.Count == 0 || document == null || !document.IsValidObject)
                return;

            var request = new DeletionRequest
            {
                AddedIds = addedIds.ToList(),
                DocumentHashCode = document.GetHashCode(),
                BypassMethod = bypassMethod,
                QueuedAt = DateTime.UtcNow
            };

            _deletionQueue.Enqueue(request);
            _logger?.LogDebug($"Queued deletion of {addedIds.Count} added element(s) (bypass: {bypassMethod})");
        }

        /// <summary>
        /// Queue a PostableCommand.Undo invocation for the next Idling tick.
        /// Used for Move bypasses on non-pin-protected elements where we don't have a stored
        /// original position — undoing the user's last transaction is the only reliable revert.
        /// Caveat: undoes whatever the most recent transaction was; users doing rapid follow-up
        /// edits could see an unrelated step rolled back. Acceptable given Idling fires fast.
        /// </summary>
        public void QueueUndo(Document document, string bypassMethod)
        {
            if (document == null || !document.IsValidObject) return;

            var request = new UndoRequest
            {
                DocumentHashCode = document.GetHashCode(),
                BypassMethod = bypassMethod,
                QueuedAt = DateTime.UtcNow
            };

            _undoQueue.Enqueue(request);
            _logger?.LogDebug($"Queued Undo for bypass: {bypassMethod}");
        }

        /// <summary>
        /// Check if there are pending restorations, deletions, or undos.
        /// </summary>
        public bool HasPendingRestorations =>
            !_restorationQueue.IsEmpty || !_deletionQueue.IsEmpty || !_undoQueue.IsEmpty;

        /// <summary>
        /// Process all queued restorations, deletions, and undos.
        /// Called from Idling event handler.
        /// </summary>
        /// <param name="document">Current active document</param>
        /// <param name="uiApplication">Optional UIApplication required to dispatch PostCommand(Undo)
        /// for queued undo requests. When null, queued undos remain pending.</param>
        public void ProcessQueue(Document document, UIApplication? uiApplication = null)
        {
            if (document == null || !document.IsValidObject)
                return;

            ProcessDeletions(document);
            ProcessUndos(uiApplication);

            var docHashCode = document.GetHashCode();
            var processedCount = 0;
            var failedCount = 0;
            var skippedCount = 0;

            // Track groups already restored — when multiple members of the same group are queued,
            // moving the group once fixes all members, so skip subsequent members.
            var restoredGroupIds = new HashSet<long>();

            // Process only requests for the current document
            var tempQueue = new ConcurrentQueue<RestorationRequest>();

            while (_restorationQueue.TryDequeue(out var request))
            {
                // Skip requests for other documents - re-queue them
                if (request.DocumentHashCode != docHashCode)
                {
                    tempQueue.Enqueue(request);
                    continue;
                }

                // Skip stale requests (older than 30 seconds)
                if ((DateTime.UtcNow - request.QueuedAt).TotalSeconds > 30)
                {
                    _logger?.LogWarning($"Skipping stale restoration request for element {request.ElementId.GetIdValue()}");
                    continue;
                }

                try
                {
                    // Deduplicate group members: if this element's group was already restored, skip
                    var element = document.GetElement(request.ElementId);
                    if (element != null && element.IsValidObject &&
                        element.GroupId != null && element.GroupId != ElementId.InvalidElementId)
                    {
                        var groupIdValue = (long)element.GroupId.GetIdValue();
                        if (restoredGroupIds.Contains(groupIdValue))
                        {
                            _logger?.LogDebug($"Skipping element {request.ElementId.GetIdValue()} — group {groupIdValue} already restored");
                            skippedCount++;
                            continue;
                        }
                    }

                    var success = RestoreElementPosition(request.ElementId, document);
                    if (success)
                    {
                        processedCount++;
                        // Track the group so subsequent members are skipped
                        if (element != null && element.GroupId != null && element.GroupId != ElementId.InvalidElementId)
                            restoredGroupIds.Add((long)element.GroupId.GetIdValue());
                    }
                    else
                        failedCount++;
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to restore element position {request.ElementId.GetIdValue()}: {ex.Message}", ex);
                    failedCount++;
                }
            }

            // Re-queue requests for other documents
            while (tempQueue.TryDequeue(out var request))
            {
                _restorationQueue.Enqueue(request);
            }

            if (processedCount > 0 || failedCount > 0 || skippedCount > 0)
            {
                _logger?.LogInfo($"Position restoration complete: {processedCount} restored, {failedCount} failed, {skippedCount} skipped (group dedup)");
            }
        }

        /// <summary>
        /// Restore element to its original protected position
        /// </summary>
        private bool RestoreElementPosition(ElementId elementId, Document document)
        {
            var element = document.GetElement(elementId);
            if (element == null || !element.IsValidObject)
            {
                _logger?.LogWarning($"Cannot restore position - element {elementId.GetIdValue()} not found");
                return false;
            }

            var storedPosition = PinProtectionStorage.GetStoredPosition(element);
            if (storedPosition == null)
            {
                _logger?.LogWarning($"Cannot restore position - no stored position for element {elementId.GetIdValue()}");
                return false;
            }

            var currentPosition = PinProtectionStorage.GetElementPosition(element);
            if (currentPosition == null)
            {
                _logger?.LogWarning($"Cannot restore position - unable to get current position for element {elementId.GetIdValue()}");
                return false;
            }

            // Calculate movement vector
            var moveVector = storedPosition - currentPosition;

            // Skip if already at position (within tolerance)
            if (moveVector.GetLength() < 0.001)
            {
                _logger?.LogDebug($"Element {elementId.GetIdValue()} already at original position");
                return true;
            }

            // If element is inside a group, move the group instead (can't move individual group members)
            var targetId = elementId;
            if (element.GroupId != null && element.GroupId != ElementId.InvalidElementId)
            {
                targetId = element.GroupId;
                _logger?.LogInfo($"Element {elementId.GetIdValue()} is in group {targetId.GetIdValue()}, moving group to restore position");
            }

            using (var trans = new Transaction(document, "ZeManage: Restore Protected Element Position"))
            {
                trans.Start();

                try
                {
                    // Move element (or its parent group) back to original position
                    ElementTransformUtils.MoveElement(document, targetId, moveVector);

                    // Re-pin the element if it was unpinned
                    if (!element.Pinned)
                    {
                        element.Pinned = true;
                    }

                    trans.Commit();

                    _logger?.LogInfo($"Restored protected element {elementId.GetIdValue()} to original position " +
                                    $"(moved {moveVector.GetLength():F3} feet, target: {targetId.GetIdValue()})");
                    return true;
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    _logger?.LogError($"Failed to move element {elementId.GetIdValue()}: {ex.Message}", ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// Process all queued copy-bypass deletions on the current Idling tick.
        /// </summary>
        private void ProcessDeletions(Document document)
        {
            if (_deletionQueue.IsEmpty) return;

            var docHashCode = document.GetHashCode();
            var tempQueue = new ConcurrentQueue<DeletionRequest>();

            while (_deletionQueue.TryDequeue(out var request))
            {
                if (request.DocumentHashCode != docHashCode)
                {
                    tempQueue.Enqueue(request);
                    continue;
                }
                if ((DateTime.UtcNow - request.QueuedAt).TotalSeconds > 30)
                {
                    _logger?.LogWarning($"Skipping stale deletion request ({request.AddedIds.Count} ids, bypass: {request.BypassMethod})");
                    continue;
                }

                var liveIds = request.AddedIds
                    .Where(id => id != null && id != ElementId.InvalidElementId &&
                                 document.GetElement(id)?.IsValidObject == true)
                    .ToList();

                if (liveIds.Count == 0)
                {
                    _logger?.LogDebug($"No live elements to delete for bypass {request.BypassMethod} — already removed");
                    continue;
                }

                using (var trans = new Transaction(document, "ZeManage: Revert Copy Bypass"))
                {
                    try
                    {
                        trans.Start();
                        document.Delete(liveIds);
                        trans.Commit();
                        _logger?.LogInfo($"Reverted copy bypass: deleted {liveIds.Count} element(s) ({request.BypassMethod})");
                    }
                    catch (Exception ex)
                    {
                        if (trans.HasStarted()) trans.RollBack();
                        _logger?.LogError($"Failed to delete {liveIds.Count} elements for bypass {request.BypassMethod}: {ex.Message}", ex);
                    }
                }
            }

            while (tempQueue.TryDequeue(out var req)) _deletionQueue.Enqueue(req);
        }

        /// <summary>
        /// Drain the undo queue by posting PostableCommand.Undo once per pending request.
        /// Requires UIApplication; if null, requests remain queued.
        /// </summary>
        private void ProcessUndos(UIApplication? uiApplication)
        {
            if (_undoQueue.IsEmpty) return;
            if (uiApplication == null)
            {
                _logger?.LogDebug("Undo queue non-empty but no UIApplication — leaving requests queued");
                return;
            }

            var undoCommandId = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
            if (undoCommandId == null)
            {
                _logger?.LogWarning("Cannot post Undo — PostableCommand.Undo not resolvable");
                while (_undoQueue.TryDequeue(out _)) { }
                return;
            }

            int posted = 0;
            while (_undoQueue.TryDequeue(out var request))
            {
                if ((DateTime.UtcNow - request.QueuedAt).TotalSeconds > 30)
                {
                    _logger?.LogWarning($"Skipping stale undo request (bypass: {request.BypassMethod})");
                    continue;
                }

                try
                {
                    uiApplication.PostCommand(undoCommandId);
                    posted++;
                    _logger?.LogInfo($"Posted Undo for bypass: {request.BypassMethod}");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Failed to post Undo for bypass {request.BypassMethod}: {ex.Message}", ex);
                }

                // Revit collapses consecutive PostCommand(Undo) — one is usually enough per
                // user-perceived gesture; bail after the first successful post.
                break;
            }

            if (posted > 0 && !_undoQueue.IsEmpty)
            {
                _logger?.LogDebug($"Undo queue still has {_undoQueue.Count} entries — will drain on next Idling tick");
            }
        }

        /// <summary>
        /// Clear all pending restorations, deletions, and undos (e.g., on document close).
        /// </summary>
        public void ClearQueue()
        {
            while (_restorationQueue.TryDequeue(out _)) { }
            while (_deletionQueue.TryDequeue(out _)) { }
            while (_undoQueue.TryDequeue(out _)) { }
            _logger?.LogDebug("Cleared position restoration / deletion / undo queues");
        }

        public void Dispose()
        {
            if (_disposed) return;
            ClearQueue();
            _disposed = true;
        }

        /// <summary>
        /// Represents a queued restoration request
        /// </summary>
        private class RestorationRequest
        {
            public ElementId ElementId { get; set; }
            public int DocumentHashCode { get; set; }
            public string BypassMethod { get; set; }
            public DateTime QueuedAt { get; set; }
        }

        private class DeletionRequest
        {
            public List<ElementId> AddedIds { get; set; } = new();
            public int DocumentHashCode { get; set; }
            public string BypassMethod { get; set; } = string.Empty;
            public DateTime QueuedAt { get; set; }
        }

        private class UndoRequest
        {
            public int DocumentHashCode { get; set; }
            public string BypassMethod { get; set; } = string.Empty;
            public DateTime QueuedAt { get; set; }
        }
    }
}
