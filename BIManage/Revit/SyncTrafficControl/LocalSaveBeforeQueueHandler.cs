using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.SyncTrafficControl
{
    /// <summary>
    /// ExternalEventHandler that performs a local save on Revit's next Idle, called from
    /// the cloud-gate's Denied branch in <c>EventRegistryService.OnDocumentSynchronizingWithCentral</c>.
    ///
    /// <para>
    /// Why deferred: inside <c>DocumentSynchronizingWithCentral</c> Revit reports
    /// <c>"Save is temporarily disabled"</c> if we try <c>doc.Save()</c> directly.
    /// After we cancel the sync, the next Idle dispatch lets us call Save outside
    /// the sync-event context where it succeeds normally.
    /// </para>
    /// <para>
    /// Honors the user's "save before joining queue at any cost, local-only" constraint
    /// for cloud models. Mirrors the on-prem flow at
    /// <c>SyncCommandBinding.OnBeforeExecuted</c> (which can call <c>doc.Save()</c>
    /// synchronously because BeforeExecuted runs before the sync-event context).
    /// </para>
    /// </summary>
    public class LocalSaveBeforeQueueHandler : IExternalEventHandler
    {
        /// <summary>Set by caller before raising; cleared on Execute (one-shot).</summary>
        public Document PendingDoc;

        /// <summary>Optional — used to update the bottom-left status bar after save.</summary>
        public UIApplication UiApp;

        public ILogger Logger;

        /// <summary>
        /// The ExternalEvent that fires this handler. Set by the owner (Application)
        /// immediately after <c>ExternalEvent.Create(handler)</c> so callers don't need
        /// a separate event reference. Use <see cref="RaiseFor"/> to schedule a save.
        /// </summary>
        public ExternalEvent Event;

        /// <summary>
        /// Schedule a deferred local save for <paramref name="doc"/>. Safe to call from
        /// inside a Revit event handler — the actual <c>doc.Save()</c> runs on Revit's
        /// next Idle, outside the "Save is temporarily disabled" sync-event context.
        /// </summary>
        public void RaiseFor(Document doc, UIApplication uiApp)
        {
            if (doc == null || Event == null) return;
            // Don't queue if already pending — Revit only fires once per ExternalEvent
            // until Execute completes, and we want THIS doc to be the one that runs.
            if (Event.IsPending)
            {
                Logger?.LogDebug($"[CloudGate] LocalSaveBeforeQueue already pending — replacing target with '{doc.Title}'");
            }
            PendingDoc = doc;
            UiApp = uiApp;
            Event.Raise();
        }

        public void Execute(UIApplication app)
        {
            var doc = PendingDoc;
            var uiApp = UiApp;
            PendingDoc = null;
            UiApp = null; // one-shot — clear so a future raise doesn't reuse stale refs

            if (doc == null || !doc.IsValidObject) return;

            try
            {
                // Same guards SyncCommandBinding uses for the on-prem save path:
                // only save when this is a LOCAL copy of central (not the central
                // itself), the doc is modified, and it's writable.
                if (!IsLocalCopyOfCentral(doc) || !doc.IsModified || doc.IsReadOnly)
                {
                    Logger?.LogDebug("[CloudGate] Deferred save skipped — guards (local-copy/modified/read-only) failed");
                    SetStatus(uiApp, $"ZeManage: Joining queue — {doc.Title}");
                    return;
                }

                doc.Save();
                Logger?.LogInfo($"[CloudGate] Deferred local save committed for '{doc.Title}'");
                SetStatus(uiApp, $"ZeManage: Saved. Joining queue — {doc.Title}");
            }
            catch (Exception ex)
            {
                // Non-fatal — user is already in queue. The failure is logged and
                // the status bar tells the user we proceeded without the save.
                Logger?.LogWarning($"[CloudGate] Deferred local save failed: {ex.Message}");
                SetStatus(uiApp, $"ZeManage: Save failed — queued anyway. {doc?.Title}");
            }
        }

        public string GetName() => "CloudGateLocalSave";

        /// <summary>
        /// True iff <paramref name="doc"/> is a workshared LOCAL COPY (its PathName
        /// differs from its central model path). For a directly-opened central
        /// doc.Save() would touch central — explicitly forbidden by the constraint.
        /// </summary>
        private static bool IsLocalCopyOfCentral(Document doc)
        {
            try
            {
                if (!doc.IsWorkshared || doc.IsDetached || string.IsNullOrEmpty(doc.PathName))
                    return false;
                var centralPath = doc.GetWorksharingCentralModelPath();
                var docPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(doc.PathName);
                return centralPath != null && docPath != null && !centralPath.Equals(docPath);
            }
            catch
            {
                return false;
            }
        }

        private static void SetStatus(UIApplication uiApp, string message)
        {
            if (uiApp == null) return;
            try
            {
                global::BIManage.Revit.BackgroundSync.RevitInteropHelper.SetStatusText(
                    uiApp.MainWindowHandle, message);
            }
            catch
            {
                /* status bar is best-effort */
            }
        }
    }
}
