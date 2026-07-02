using System;
using System.Collections.Concurrent;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.Protection
{
    /// <summary>
    /// Closes a Revit document on the main thread when Revit is in an idle state.
    ///
    /// Why this exists: calling <see cref="Document.Close(bool)"/> from INSIDE Revit's
    /// own DocumentOpened event handler silently fails — Revit still holds internal
    /// locks on the document it just finished opening, so the close request is
    /// effectively ignored. The visible symptom was the "Action Restricted — Open
    /// Central File Directly" popup dismissing while the central model stayed open.
    ///
    /// Solution: queue the close request and raise an ExternalEvent from a background
    /// thread (Revit forbids Raise() on the main thread while inside an event callback).
    /// Revit fires Execute() when it's quiescent. Inside Execute(), we first try
    /// Document.Close(false). If Revit refuses (common on just-opened central/workshared
    /// models where internal worksharing state is still active), we fall back to
    /// PostCommand(Close), which routes through Revit's own close pipeline — identical
    /// to the user pressing Ctrl+W — and handles all edge cases the direct API cannot.
    /// </summary>
    public class CloseDocumentExternalEventHandler : IExternalEventHandler
    {
        private readonly ILogger? _logger;
        private readonly ConcurrentQueue<CloseRequest> _pending = new ConcurrentQueue<CloseRequest>();
        private ExternalEvent? _externalEvent;

        public CloseDocumentExternalEventHandler(ILogger? logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Bind the ExternalEvent that raises this handler. Must be called once after
        /// the handler is constructed and BEFORE any Raise() calls.
        /// </summary>
        public void SetExternalEvent(ExternalEvent externalEvent)
        {
            _externalEvent = externalEvent ?? throw new ArgumentNullException(nameof(externalEvent));
        }

        /// <summary>
        /// Queue a document for deferred close and raise the ExternalEvent. The actual
        /// close runs on the next Revit idle tick. <paramref name="reason"/> is logged
        /// for diagnostics and shown nowhere user-visible.
        /// </summary>
        public void RequestClose(Document doc, string reason)
        {
            if (doc == null) return;
            if (_externalEvent == null)
            {
                _logger?.LogError("[CloseDocument] RequestClose called before SetExternalEvent — close ignored");
                return;
            }
            // Capture identity now in case the Document object becomes invalid before
            // the handler runs. PathName is used to re-resolve the live Document via
            // UIApplication.Application.Documents in Execute().
            _pending.Enqueue(new CloseRequest
            {
                PathName = doc.PathName ?? "",
                Title = doc.Title ?? "",
                Reason = reason ?? ""
            });
            _logger?.LogInfo($"[CloseDocument] Queued close for '{doc.Title}' ({reason})");

            // Raise from a background thread. Revit forbids ExternalEvent.Raise() on
            // the main thread while inside (or in the async continuation of) a Revit
            // event callback — it throws or silently does nothing. Task.Run ensures
            // the call comes from a non-event thread regardless of the caller context.
            var ev = _externalEvent;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { ev.Raise(); }
                catch (Exception ex)
                {
                    _logger?.LogError($"[CloseDocument] ExternalEvent.Raise (background) failed: {ex.Message}", ex);
                }
            });
        }

        public void Execute(UIApplication app)
        {
            // Drain everything queued — multiple closes can pile up if several models
            // are opened directly in quick succession.
            while (_pending.TryDequeue(out var req))
            {
                try
                {
                    var live = FindLiveDocument(app, req);
                    if (live == null)
                    {
                        _logger?.LogInfo($"[CloseDocument] Document '{req.Title}' already gone — nothing to close");
                        continue;
                    }

                    // Try switching to another doc first — avoids the active-doc restriction.
                    // If no other doc exists this is a no-op and we fall through to PostCommand.
                    TrySwitchAwayFromActive(app, live);

                    // doc.Close(false) throws InvalidOperationException: "The active document
                    // may not be closed from the API." when no other doc was available to
                    // switch to. Isolate it so the exception falls through to PostCommand
                    // rather than being swallowed by the outer catch and giving up entirely.
                    bool closed = false;
                    try { closed = live.Close(false); }
                    catch (Exception closeEx)
                    {
                        _logger?.LogWarning($"[CloseDocument] Close() threw for '{req.Title}' — falling back to PostCommand: {closeEx.Message}");
                    }

                    if (closed)
                    {
                        _logger?.LogWarning($"[CloseDocument] Closed '{req.Title}' — {req.Reason}");
                    }
                    else
                    {
                        // PostCommand(Close) works on the active document from within Execute()
                        // even when Document.Close() is forbidden (active-doc restriction).
                        var closeCmd = RevitCommandId.LookupPostableCommandId(PostableCommand.Close);
                        if (app.CanPostCommand(closeCmd))
                        {
                            // Suppress any "Save changes before closing?" dialog automatically.
                            bool dialogFired = false;
                            EventHandler<Autodesk.Revit.UI.Events.DialogBoxShowingEventArgs> dismissSave = null;
                            dismissSave = (s, e) =>
                            {
                                if (dialogFired) return;
                                dialogFired = true;
                                app.DialogBoxShowing -= dismissSave;
                                if (e is Autodesk.Revit.UI.Events.TaskDialogShowingEventArgs td)
                                {
                                    td.OverrideResult(7); // 7 = No = "Don't Save / Abandon"
                                    _logger?.LogWarning($"[CloseDocument] Auto-dismissed save dialog for '{req.Title}'");
                                }
                            };
                            app.DialogBoxShowing += dismissSave;
                            app.PostCommand(closeCmd);
                            _logger?.LogWarning($"[CloseDocument] PostCommand(Close) posted for '{req.Title}'");
                        }
                        else
                        {
                            _logger?.LogError($"[CloseDocument] Both Close() and PostCommand unavailable for '{req.Title}'");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"[CloseDocument] Failed to close '{req.Title}': {ex.Message}", ex);
                }
            }
        }

        public string GetName() => "CloseDocumentExternalEvent";

        private static Document? FindLiveDocument(UIApplication app, CloseRequest req)
        {
            foreach (Document d in app.Application.Documents)
            {
                if (d == null) continue;
                // Prefer pathname match — same model can be opened twice with different
                // titles in some edge cases; pathname is the unique identity.
                if (!string.IsNullOrEmpty(req.PathName)
                    && string.Equals(d.PathName, req.PathName, StringComparison.OrdinalIgnoreCase))
                    return d;
                if (!string.IsNullOrEmpty(req.Title)
                    && string.Equals(d.Title, req.Title, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        private void TrySwitchAwayFromActive(UIApplication app, Document docBeingClosed)
        {
            try
            {
                var active = app.ActiveUIDocument?.Document;
                if (active == null || !ReferenceEquals(active, docBeingClosed)) return;
                foreach (Document other in app.Application.Documents)
                {
                    if (other == null || other.IsFamilyDocument) continue;
                    if (ReferenceEquals(other, docBeingClosed)) continue;
                    if (!string.IsNullOrEmpty(other.PathName))
                    {
                        try
                        {
                            app.OpenAndActivateDocument(other.PathName);
                            return;
                        }
                        catch { /* try next */ }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[CloseDocument] TrySwitchAwayFromActive: {ex.Message}");
            }
        }

        private class CloseRequest
        {
            public string PathName { get; set; } = "";
            public string Title { get; set; } = "";
            public string Reason { get; set; } = "";
        }
    }
}
