using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Helpers;

namespace BIManage.Revit.BackgroundSync
{
    /// <summary>
    /// ExternalEvent handler that executes SynchronizeWithCentral programmatically on Revit's main thread.
    /// Provides full control over sync options (compact, relinquish) without opening Revit's sync dialog.
    /// Honours the user's "Open Views" sync settings:
    ///   - mode 0: keep all views open (no-op)
    ///   - mode 1: close all views except one low-memory view
    ///   - mode 2: snapshot+close (except one), sync, then reopen the captured views
    /// Also enforces the "prevent sync when more than N views are open" guard. When the guard
    /// trips, the sync is skipped and SyncSkipped is fired so the engine does NOT reset the timer.
    /// </summary>
    public class BackgroundSyncExternalEventHandler : IExternalEventHandler
    {
        private readonly ILogger _logger;
        private readonly SyncRepository _syncRepository;

        /// <summary>Model GUID to sync — set before calling Raise().</summary>
        public string PendingModelGuid { get; set; }

        /// <summary>Whether to compact the model during this sync.</summary>
        public bool Compact { get; set; }

        /// <summary>Whether to sync even if the document has no pending changes.</summary>
        public bool SyncEvenIfNoChanges { get; set; }

        /// <summary>Fired after sync completes (success or failure). Args: modelGuid, succeeded.</summary>
        public event Action<string, bool> SyncCompleted;

        /// <summary>
        /// Fired when sync was skipped (e.g. too many views open). The engine should treat
        /// this as "tick did not actually run sync" — do NOT reset the per-document timer.
        /// </summary>
        public event Action<string> SyncSkipped;

        public BackgroundSyncExternalEventHandler(ILogger logger, SyncRepository syncRepository = null)
        {
            _logger = logger;
            _syncRepository = syncRepository;
        }

        public void Execute(UIApplication app)
        {
            if (string.IsNullOrEmpty(PendingModelGuid))
                return;

            var modelGuid = PendingModelGuid;
            var compact = Compact;
            var syncEvenIfNoChanges = SyncEvenIfNoChanges;

            // Reset for next use
            PendingModelGuid = null;
            Compact = false;
            SyncEvenIfNoChanges = false;

            Document doc = null;
            try
            {
                doc = FindWorksharedDoc(app, modelGuid);
                if (doc == null)
                {
                    _logger?.LogWarning($"[BackgroundSync] Document not found for model {modelGuid}");
                    return;
                }

                // Check if document has changes (unless forced)
                if (!doc.IsModified && !syncEvenIfNoChanges)
                {
                    _logger?.LogDebug($"[BackgroundSync] Skipping sync for {doc.Title} — no changes and SyncEvenIfNoChanges=false");
                    SyncCompleted?.Invoke(modelGuid, true);
                    return;
                }

                // Wait for background calculation to complete
                var waitCount = 0;
                while (doc.IsBackgroundCalculationInProgress())
                {
                    Thread.Sleep(100);
                    waitCount++;
                    if (waitCount > 300) // 30-second timeout
                    {
                        _logger?.LogWarning($"[BackgroundSync] Background calculation timeout for {doc.Title}");
                        return;
                    }
                }

                // Resolve the active UIDocument that matches this Document so we can manage views.
                var uidoc = ResolveUIDocument(app, doc);

                // Load latest sync settings (cheap one-row read). Falls back to defaults if missing.
                BackgroundSyncSettings settings = null;
                if (_syncRepository != null)
                {
                    try { settings = _syncRepository.GetBackgroundSyncSettingsAsync().GetAwaiter().GetResult(); }
                    catch (Exception sEx) { _logger?.LogWarning($"[BackgroundSync] Failed to load sync settings: {sEx.Message}"); }
                }
                int openViewsMode = settings?.OpenViewsOnSyncMode ?? 0;
                int preventOver = settings != null
                    ? Math.Max(2, settings.PreventSyncWhenViewsOpenedOver)
                    : 10;

                // === Pre-sync guard: too many views open? ===
                int openViewCount = CountOpenViews(uidoc);
                if (openViewCount > preventOver)
                {
                    _logger?.LogInfo($"[BackgroundSync] Sync SKIPPED for {doc.Title} — {openViewCount} views open (limit {preventOver}). Timer will NOT be reset.");
                    RevitInteropHelper.SetStatusText(app.MainWindowHandle, $"ZeManage: Sync skipped — too many views open ({openViewCount} > {preventOver})");
                    SyncSkipped?.Invoke(modelGuid);
                    return;
                }

                _logger?.LogInfo($"[BackgroundSync] Starting sync for {doc.Title} (compact: {compact}, openViewsMode: {openViewsMode}, openViews: {openViewCount})");

                // === Pre-sync view handling (mode 1 / mode 2) ===
                List<ElementId> snapshot = null;     // Mode 2 only — list of views to reopen
                ElementId originalActiveViewId = null;
                ElementId keptViewId = null;

                try
                {
                    if (uidoc != null && (openViewsMode == 1 || openViewsMode == 2))
                    {
                        if (openViewsMode == 2)
                        {
                            snapshot = CaptureOpenViewIds(uidoc);
                            originalActiveViewId = uidoc.ActiveView?.Id;
                        }
                        keptViewId = PickKeepView(doc, uidoc);
                        if (keptViewId != null)
                            CloseAllViewsExcept(uidoc, keptViewId);
                    }
                }
                catch (Exception viewEx)
                {
                    _logger?.LogWarning($"[BackgroundSync] Pre-sync view handling failed: {viewEx.Message}");
                    // Fail open — keep going with the sync regardless
                }

                var mainWindow = app.MainWindowHandle;
                RevitInteropHelper.SetStatusText(mainWindow, $"ZeManage: Synchronizing {doc.Title}...");

                var failuresHandler = new BackgroundFailuresHandler(_logger);
                failuresHandler.Attach(app.Application);
                bool syncSucceeded = false;
                try
                {
                    var syncOpts = new SynchronizeWithCentralOptions();
                    var relinquishOpts = new RelinquishOptions(true)
                    {
                        UserWorksets = true,
                        FamilyWorksets = false,
                        CheckedOutElements = true
                    };
                    syncOpts.SetRelinquishOptions(relinquishOpts);
                    syncOpts.Compact = compact;
                    syncOpts.Comment = "ZeManage background sync";

                    // SyncProtectionGate marks this as an automated sync so the Command Protection
                    // enforcer in EventRegistryService.OnDocumentSynchronizingWithCentral skips it.
                    // Background sync = prior user consent (settings toggle); a dialog here would
                    // retry-storm the auto-loop.
                    using (BIManage.Revit.SyncTrafficControl.SyncProtectionGate.EnterInternalSync())
                    {
                        doc.SynchronizeWithCentral(new TransactWithCentralOptions(), syncOpts);
                    }
                    syncSucceeded = true;

                    _logger?.LogInfo($"[BackgroundSync] Sync completed for {doc.Title}");
                    RevitInteropHelper.SetStatusText(mainWindow, $"ZeManage: Sync completed — {doc.Title}");
                }
                finally
                {
                    failuresHandler.Detach(app.Application);

                    // === Post-sync view restore (mode 2) ===
                    // Always run inside finally so a sync failure still reopens captured views.
                    if (uidoc != null && openViewsMode == 2 && snapshot != null && snapshot.Count > 0)
                    {
                        try { ReopenViews(uidoc, doc, snapshot, originalActiveViewId, keptViewId); }
                        catch (Exception reopenEx) { _logger?.LogWarning($"[BackgroundSync] Post-sync view restore failed: {reopenEx.Message}"); }
                    }

                    SyncCompleted?.Invoke(modelGuid, syncSucceeded);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[BackgroundSync] Sync failed for {doc?.Title ?? modelGuid}: {ex.Message}", ex);
                RevitInteropHelper.ClearStatusText(app.MainWindowHandle);
                SyncCompleted?.Invoke(modelGuid, false);
            }
        }

        private Document FindWorksharedDoc(UIApplication app, string modelGuid)
        {
            try
            {
                foreach (Document doc in app.Application.Documents)
                {
                    if (doc.IsLinked || doc.IsFamilyDocument) continue;
                    if (!DocumentTypeHelper.IsWorkshared(doc)) continue;

                    var guid = ModelGuidHelper.GetModelGuid(doc);
                    if (string.Equals(guid, modelGuid, StringComparison.OrdinalIgnoreCase))
                        return doc;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[BackgroundSync] Error finding document: {ex.Message}", ex);
            }
            return null;
        }

        public string GetName() => "BIManage Background Sync";

        // ───────────────────────── View-handling helpers ─────────────────────────

        /// <summary>Find the UIDocument that wraps the given Document.</summary>
        private static UIDocument ResolveUIDocument(UIApplication app, Document doc)
        {
            try
            {
                if (app?.ActiveUIDocument?.Document?.Equals(doc) == true) return app.ActiveUIDocument;
            }
            catch { /* fall through */ }
            return null; // Background sync of a non-active doc — view manipulation is skipped.
        }

        /// <summary>Counts UIView instances currently open in the editor (sheets included).</summary>
        private int CountOpenViews(UIDocument uidoc)
        {
            if (uidoc == null) return 0;
            try { return uidoc.GetOpenUIViews()?.Count ?? 0; } catch { return 0; }
        }

        /// <summary>Captures every open UIView's view-id in display order.</summary>
        private List<ElementId> CaptureOpenViewIds(UIDocument uidoc)
        {
            var list = new List<ElementId>();
            if (uidoc == null) return list;
            try
            {
                foreach (var uiv in uidoc.GetOpenUIViews())
                {
                    var id = uiv.ViewId;
                    if (id != null && id != ElementId.InvalidElementId)
                        list.Add(id);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[BackgroundSync] CaptureOpenViewIds failed: {ex.Message}");
            }
            return list;
        }

        /// <summary>
        /// Picks one low-memory view to keep open during sync.
        /// Priority: drafting → legend → section → floor plan → fallback to currently active view.
        /// </summary>
        private ElementId PickKeepView(Document doc, UIDocument uidoc)
        {
            if (doc == null) return null;
            try
            {
                // 1. Drafting view
                var drafting = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewDrafting))
                    .Cast<ViewDrafting>()
                    .FirstOrDefault(v => !v.IsTemplate);
                if (drafting != null) return drafting.Id;

                // 2. Legend (no concrete class — match by ViewType)
                var legend = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.Legend);
                if (legend != null) return legend.Id;

                // 3. Section
                var section = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSection))
                    .Cast<ViewSection>()
                    .FirstOrDefault(v => !v.IsTemplate);
                if (section != null) return section.Id;

                // 4. Floor plan
                var floorPlan = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewPlan))
                    .Cast<ViewPlan>()
                    .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan);
                if (floorPlan != null) return floorPlan.Id;

                // 5. Fallback: keep whatever view is currently active so we never leave Revit with zero open views.
                return uidoc?.ActiveView?.Id;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[BackgroundSync] PickKeepView failed: {ex.Message}");
                return uidoc?.ActiveView?.Id;
            }
        }

        /// <summary>
        /// Closes every open UIView except the one with id <paramref name="keepViewId"/>.
        /// Activates the keep view first so closing the previously-active one is allowed.
        /// </summary>
        private void CloseAllViewsExcept(UIDocument uidoc, ElementId keepViewId)
        {
            if (uidoc == null || keepViewId == null) return;
            try
            {
                // Activate the keep view first — Revit refuses to close the last/active view
                // unless another view is set active.
                var keepView = uidoc.Document.GetElement(keepViewId) as View;
                if (keepView != null)
                {
                    try { uidoc.ActiveView = keepView; } catch { /* may throw if already active */ }
                }

                var openViews = uidoc.GetOpenUIViews();
                foreach (var uiv in openViews)
                {
                    try
                    {
                        if (uiv.ViewId == keepViewId) continue;
                        uiv.Close();
                    }
                    catch (Exception closeEx)
                    {
                        _logger?.LogDebug($"[BackgroundSync] Could not close view {uiv.ViewId}: {closeEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[BackgroundSync] CloseAllViewsExcept failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reopens views captured in the snapshot (Mode 2 post-sync).
        /// Skips views that were deleted during sync. Restores the originally active view last.
        /// </summary>
        private void ReopenViews(UIDocument uidoc, Document doc, List<ElementId> snapshot, ElementId originalActiveViewId, ElementId keepViewId)
        {
            if (uidoc == null || snapshot == null) return;
            foreach (var id in snapshot)
            {
                try
                {
                    if (keepViewId != null && id == keepViewId) continue; // already open
                    var view = doc?.GetElement(id) as View;
                    if (view == null) continue; // deleted during sync — skip silently
                    uidoc.RequestViewChange(view);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"[BackgroundSync] Could not reopen view {id}: {ex.Message}");
                }
            }

            // Restore the focused view if it survived sync.
            try
            {
                if (originalActiveViewId != null)
                {
                    var activeView = doc?.GetElement(originalActiveViewId) as View;
                    if (activeView != null) uidoc.ActiveView = activeView;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[BackgroundSync] Restore active view failed: {ex.Message}");
            }
        }
    }
}
