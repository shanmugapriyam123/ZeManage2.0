using System;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Context;
using BIManage.Revit.Helpers;

namespace BIManage.Revit.Idling
{
    /// <summary>
    /// Observation-only timer for post-open Worksets / Manage Links dialogs.
    /// Replaces the disabled command-binding pattern, which interfered with Revit's
    /// modal dispatch and prevented the dialogs from opening at all.
    ///
    /// Detection: Application.DialogBoxShowing — match e.DialogId against known patterns.
    /// Close: First UIApplication.Idling event after a recognized dialog opened. Revit's
    /// main message loop only goes idle once the modal is dismissed, so the next Idling
    /// tick after DialogBoxShowing reliably bookends the dialog's lifetime.
    ///
    /// On close: increments document_sessions.total_workset_open_seconds /
    /// total_link_load_seconds and re-syncs the model session via the existing endpoints.
    ///
    /// Safety contract: never calls e.OverrideResult, never sets e.Cancel anywhere,
    /// never creates an AddInCommandBinding, never blocks the UI thread on DB / HTTP.
    /// </summary>
    public sealed class PostOpenDialogTimingService : IDisposable
    {
        private readonly ILogger? _logger;
        private readonly SessionRepository? _sessionRepository;
        private readonly Func<IRevitContext?> _revitContextGetter;
        private readonly Func<ModelSessionSyncService?> _modelSessionSyncGetter;

        private UIApplication? _uiApp;
        private bool _started;
        private readonly object _stateLock = new object();

        private DialogCategory? _pendingCategory;
        private DateTime _pendingStartedAt;
        private string? _pendingSessionId;
        private string? _pendingModelGuid;
        private string? _pendingDialogId;

        // Cap measured deltas so a never-flushed pending state can't bleed huge values
        // into the session totals (e.g. user leaves Worksets dialog open overnight).
        private static readonly TimeSpan MaxMeasurableInterval = TimeSpan.FromMinutes(60);

        // Ignore deltas under this threshold — Revit fires DialogBoxShowing for fleeting
        // sub-dialogs (e.g. progress popups during workset enumeration) that the next
        // Idling tick would catch as a sub-second flicker.
        private static readonly TimeSpan MinMeasurableInterval = TimeSpan.FromMilliseconds(250);

        public PostOpenDialogTimingService(
            ILogger? logger,
            SessionRepository? sessionRepository,
            Func<IRevitContext?>? revitContextGetter,
            Func<ModelSessionSyncService?>? modelSessionSyncGetter)
        {
            _logger = logger;
            _sessionRepository = sessionRepository;
            _revitContextGetter = revitContextGetter ?? (() => null);
            _modelSessionSyncGetter = modelSessionSyncGetter ?? (() => null);
        }

        public void Start(UIApplication uiApp)
        {
            if (_started) return;
            if (uiApp == null) return;

            _uiApp = uiApp;

            try
            {
                _uiApp.DialogBoxShowing += OnDialogShowing;
                _uiApp.Idling += OnIdling;
                _started = true;
                _logger?.LogInfo("PostOpenDialogTimingService started (Worksets / Manage Links observers)");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"PostOpenDialogTimingService.Start failed: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (!_started) return;
            try
            {
                if (_uiApp != null)
                {
                    _uiApp.DialogBoxShowing -= OnDialogShowing;
                    _uiApp.Idling -= OnIdling;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"PostOpenDialogTimingService.Stop: {ex.Message}");
            }
            _started = false;
        }

        public void Dispose() => Stop();

        private enum DialogCategory { Worksets, ManageLinks }

        private static DialogCategory? ClassifyDialog(string? dialogId)
        {
            if (string.IsNullOrWhiteSpace(dialogId)) return null;

            // Worksets dialog ids vary by Revit version: "WorksetsDialog",
            // "Dialog_Revit_Worksets", "Dialog_Revit_TaskDialog_Worksets". The Reload
            // Latest "WorksharingLink*" dialog also matches "Workset" loosely, so guard
            // it out below.
            var hasWorkset = dialogId.IndexOf("Workset", StringComparison.OrdinalIgnoreCase) >= 0;
            var hasWorksharingLink = dialogId.IndexOf("Worksharing", StringComparison.OrdinalIgnoreCase) >= 0
                                  && dialogId.IndexOf("Link", StringComparison.OrdinalIgnoreCase) >= 0;

            if (hasWorkset && !hasWorksharingLink)
                return DialogCategory.Worksets;

            // Manage Links / RevitLinkType dialogs: "LinkRevit",
            // "Dialog_Revit_LinkManagement", "ManageLinks". Skip the "Worksharing*" set.
            var hasLinkRevit = dialogId.IndexOf("LinkRevit", StringComparison.OrdinalIgnoreCase) >= 0;
            var hasLinkManagement = dialogId.IndexOf("LinkManagement", StringComparison.OrdinalIgnoreCase) >= 0;
            var hasManageLinks = dialogId.IndexOf("ManageLinks", StringComparison.OrdinalIgnoreCase) >= 0;

            if ((hasLinkRevit || hasLinkManagement || hasManageLinks) && !hasWorksharingLink)
                return DialogCategory.ManageLinks;

            return null;
        }

        private void OnDialogShowing(object sender, DialogBoxShowingEventArgs e)
        {
            try
            {
                var dialogId = e?.DialogId;
                var category = ClassifyDialog(dialogId);
                if (category == null) return;

                var sessionId = _revitContextGetter()?.SessionId.ToString();
                if (string.IsNullOrEmpty(sessionId)) return;

                string? modelGuid = null;
                try
                {
                    var doc = _uiApp?.ActiveUIDocument?.Document;
                    if (doc != null) modelGuid = ModelGuidHelper.GetModelGuid(doc, _logger);
                }
                catch (Exception guidEx)
                {
                    _logger?.LogDebug($"PostOpenDialogTiming: modelGuid read failed for dialogId={dialogId}: {guidEx.Message}");
                }

                if (string.IsNullOrEmpty(modelGuid)) return;

                lock (_stateLock)
                {
                    // Last-in-wins. If a previous recognized dialog was still pending
                    // (nested case), drop it — the parent's measurement is lost rather
                    // than incorrectly attributed to the nested dialog's lifetime.
                    _pendingCategory = category;
                    _pendingStartedAt = DateTime.UtcNow;
                    _pendingSessionId = sessionId;
                    _pendingModelGuid = modelGuid;
                    _pendingDialogId = dialogId;
                }
            }
            catch (Exception ex)
            {
                // Never let an exception escape into Revit's dialog flow.
                _logger?.LogDebug($"PostOpenDialogTiming.OnDialogShowing: {ex.Message}");
            }
        }

        private void OnIdling(object sender, IdlingEventArgs e)
        {
            try
            {
                FlushIfPending();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"PostOpenDialogTiming.OnIdling: {ex.Message}");
            }
        }

        private void FlushIfPending()
        {
            DialogCategory? category;
            DateTime startedAt;
            string? sessionId;
            string? modelGuid;
            string? dialogId;
            DateTime now = DateTime.UtcNow;

            lock (_stateLock)
            {
                if (_pendingCategory == null) return;
                category = _pendingCategory;
                startedAt = _pendingStartedAt;
                sessionId = _pendingSessionId;
                modelGuid = _pendingModelGuid;
                dialogId = _pendingDialogId;

                _pendingCategory = null;
                _pendingStartedAt = default;
                _pendingSessionId = null;
                _pendingModelGuid = null;
                _pendingDialogId = null;
            }

            var deltaSeconds = (now - startedAt).TotalSeconds;
            if (deltaSeconds < MinMeasurableInterval.TotalSeconds) return;
            if (deltaSeconds > MaxMeasurableInterval.TotalSeconds)
            {
                _logger?.LogDebug($"PostOpenDialogTiming: dropping {category} delta {deltaSeconds:F1}s > {MaxMeasurableInterval.TotalMinutes:F0}m cap (dialogId={dialogId ?? "(unknown)"})");
                return;
            }
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(modelGuid)) return;
            if (_sessionRepository == null) return;

            var capturedCategory = category!.Value;
            var capturedSessionId = sessionId!;
            var capturedModelGuid = modelGuid!;
            var capturedDelta = deltaSeconds;
            var capturedDialogId = dialogId ?? "(unknown)";
            var revitUsername = SafeGetRevitUsername();

            // Fire-and-forget — UI thread never waits on DB or HTTP.
            _ = Task.Run(async () =>
            {
                try
                {
                    double? newTotal;
                    if (capturedCategory == DialogCategory.Worksets)
                    {
                        newTotal = await _sessionRepository.IncrementWorksetOpenTimeAsync(
                            capturedSessionId, capturedModelGuid, capturedDelta, revitUsername);
                    }
                    else
                    {
                        newTotal = await _sessionRepository.IncrementLinkLoadTimeAsync(
                            capturedSessionId, capturedModelGuid, capturedDelta, revitUsername);
                    }

                    if (newTotal == null) return;

                    _logger?.LogInfo($"[Idle] {capturedCategory} dialog: +{capturedDelta:F2}s → total {newTotal.Value:F2}s (model={capturedModelGuid}, dialogId={capturedDialogId})");

                    var sync = _modelSessionSyncGetter();
                    if (sync == null) return;

                    if (capturedCategory == DialogCategory.Worksets)
                        await sync.SyncWorksetOpeningDurationAsync(capturedSessionId, capturedModelGuid, newTotal.Value, revitUsername);
                    else
                        await sync.SyncLinkLoadingDurationAsync(capturedSessionId, capturedModelGuid, newTotal.Value, revitUsername);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"PostOpenDialogTiming async path failed: {ex.Message}");
                }
            });
        }

        private string SafeGetRevitUsername()
        {
            try
            {
                return _uiApp?.Application?.Username ?? Environment.UserName;
            }
            catch
            {
                return Environment.UserName;
            }
        }
    }
}
