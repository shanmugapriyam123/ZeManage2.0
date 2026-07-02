#nullable enable

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Revit.Context;
using BIManage.Revit.Helpers;

namespace BIManage.Revit.SyncTrafficControl
{
    /// <summary>
    /// Final safety net for the sync queue.
    ///
    /// The upstream <c>SyncCommandBinding.OnBeforeExecuted</c> and the
    /// <c>OnDocumentSynchronizingWithCentral</c> fallback both depend on
    /// <c>SyncTrafficControlService._activeSyncers</c> being populated for the model
    /// the user is about to sync. That dictionary is populated by SignalR
    /// <c>SyncStarting</c> events from peers — so if the event hasn't arrived yet
    /// (network latency, server delay, peer not running the plugin), our gate
    /// allows the sync. Revit then tries to grab the central file lock, finds it
    /// held, and shows its native "Unable to Access the Model" / "central currently
    /// in use" dialog — bypassing our queue UI entirely.
    ///
    /// This interceptor closes that gap. It subscribes to
    /// <c>UIApplication.DialogBoxShowing</c>, detects Revit's central-busy dialog by
    /// DialogId pattern, dismisses it via <c>OverrideResult(Cancel)</c>, and on the
    /// next Idling tick shows our own <see cref="Views.SyncTrafficControl.SyncConflictDialog"/>
    /// so the user can join the queue — same UX as the upstream gate.
    ///
    /// Pure additive: never changes the upstream paths. If they fire correctly,
    /// Revit's dialog never appears and this interceptor stays dormant.
    /// </summary>
    public sealed class SyncConflictDialogInterceptor : IDisposable
    {
        private readonly UIApplication _uiApp;
        private readonly ILogger? _logger;
        private readonly Func<SyncTrafficControlService?> _stcGetter;
        private readonly Func<IRevitContext?> _ctxGetter;
        private readonly Func<ISignalREventBus?> _eventBusGetter;
        private readonly Dispatcher _dispatcher;

        private bool _started;
        private bool _disposed;

        // When DialogBoxShowing intercepts the central-busy dialog, we cannot show
        // our own modal from inside the event handler (would re-enter Revit's UI).
        // Instead we capture the model context and defer to the next Idling tick.
        private string? _pendingModelGuid;
        private string? _pendingModelName;

        // Suppress repeat dialog-intercepts inside a single sync click. Revit may
        // fire DialogBoxShowing more than once for the same central-busy condition.
        private DateTime _lastInterceptAtUtc = DateTime.MinValue;
        private static readonly TimeSpan InterceptDebounce = TimeSpan.FromSeconds(2);

        // Set while a Save As operation is in progress so the interceptor ignores
        // Revit popups caused by us rather than a real sync collision.
        // Cleared by:
        //   (a) WinEventProc when it handles "File not saved." in Save As context
        //   (b) OnDocumentSaved when a Save As completes successfully
        private static volatile bool _saveAsProtectionActive;

        /// <summary>Call before the file picker opens for any Save As command.</summary>
        public static void NotifySaveAsStarted()  => _saveAsProtectionActive = true;

        /// <summary>Call when Save As completes (success or explicit clear).</summary>
        public static void NotifySaveAsCompleted() => _saveAsProtectionActive = false;

        private static bool SaveAsActive() => _saveAsProtectionActive;

        // Set by SyncCommandBinding.OnBeforeExecuted whenever the user clicks any
        // Sync command. Used to disambiguate Revit's "File not saved." popup —
        // that exact dialog fires for BOTH a sync collision AND any other cancelled
        // save (Esc during save, FailuresProcessing aborting, other Event Protections).
        // The Save-As path is already short-circuited by SaveAsActive() above; this
        // covers every other cancelled-save scenario. Only treat the match as a sync
        // collision when a Sync attempt happened within SyncRecencyWindow.
        private static DateTime _lastSyncAttemptAtUtc = DateTime.MinValue;
        // Widened from 45 s to 5 min — in the multi-user queue scenario, a queued user
        // can wait several minutes for their turn. The "File not saved" popup can also
        // appear minutes after the user clicked Sync Now (Revit retries the central-file
        // lock internally a few times before surfacing the error). 5 min covers the
        // worst-case turn duration; any sync attempt within a queue cycle will re-arm
        // the window through OnLocalSyncStarting / JoinQueue / MarkPendingQueueSync.
        private static readonly TimeSpan SyncRecencyWindow = TimeSpan.FromMinutes(5);

        public static void NoteSyncAttempt() => _lastSyncAttemptAtUtc = DateTime.UtcNow;

        /// <summary>
        /// Clears the recent-sync-attempt window. Called by SyncTrafficControlService when
        /// the local user's own sync COMPLETES: once the sync is done there is no pending
        /// attempt, so any follow-up Revit dialog (workset notifications, "file not saved"
        /// from an unrelated cancelled save, etc.) must NOT be misread as a blocked sync and
        /// routed to the Join Queue popup. A fresh Sync click re-arms the window via
        /// SyncCommandBinding.OnBeforeExecuted → NoteSyncAttempt(). Fixes the spurious
        /// "Join Queue" popup that appeared after a user finished their own sync.
        /// </summary>
        public static void ClearSyncAttempt() => _lastSyncAttemptAtUtc = DateTime.MinValue;

        private static bool SyncAttemptedRecently()
            => (DateTime.UtcNow - _lastSyncAttemptAtUtc) < SyncRecencyWindow;

        /// <summary>
        /// Diagnostic: captures Revit document + sync state for context lines.
        /// Helps attribute Revit's "Unable to Access" / "File not saved" dialogs to
        /// a particular phase (mid-sync, post-sync, etc.) without spamming the log.
        /// </summary>
        private string DescribeDocumentSyncState()
        {
            try
            {
                var doc = _uiApp.ActiveUIDocument?.Document;
                if (doc == null) return "doc=null";
                var modelGuid = ModelGuidHelper.GetModelGuid(doc, _logger);
                var stc = _stcGetter();
                bool isLocalSyncing = stc != null && !string.IsNullOrEmpty(modelGuid) && stc.IsLocalSessionSyncingFor(modelGuid);
                bool isFinishedRecently = stc != null && !string.IsNullOrEmpty(modelGuid) && stc.IsOwnSyncFinishedRecentlyFor(modelGuid);
                return $"docState: title='{doc.Title}', workshared={doc.IsWorkshared}, readOnly={doc.IsReadOnly}, modified={doc.IsModified}, modelGuid={modelGuid}, localIsSyncing={isLocalSyncing}, finishedRecently={isFinishedRecently}";
            }
            catch (Exception ex)
            {
                return $"docState(error: {ex.Message})";
            }
        }

        /// <summary>
        /// True when our local session is the active syncer for the currently active
        /// document — meaning we already broadcast SyncStarting and the central-busy /
        /// file-not-saved dialog firing right now is OUR sync failing, not a peer
        /// blocking us. ALSO true when we just COMPLETED our own sync within the last
        /// ~15s — Revit fires follow-up dialogs (workset notifications etc.) shortly
        /// after a successful sync, and our IsCentralBusyMessage matcher catches some
        /// of them; without the cooldown gate, the interceptor would pop the queue
        /// dialog right after the user's sync just succeeded ("another user is
        /// syncing" against themselves, all over again).
        /// </summary>
        private bool IsOwnSyncFailureForActiveDocument()
        {
            try
            {
                var doc = _uiApp.ActiveUIDocument?.Document;
                if (doc == null) return false;
                var modelGuid = ModelGuidHelper.GetModelGuid(doc, _logger);
                if (string.IsNullOrEmpty(modelGuid)) return false;
                var stc = _stcGetter();
                if (stc == null) return false;
                return stc.IsLocalSessionSyncingFor(modelGuid)
                    || stc.IsOwnSyncFinishedRecentlyFor(modelGuid);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[SyncQueueIntercept] IsOwnSyncFailureForActiveDocument lookup failed: {ex.Message} — defaulting to false");
                return false;
            }
        }

        // Win32 OS-level hook. Field log analysis (2026-05-16) confirmed that
        // Revit's "Unable to Access the Model" popup never fires the
        // DialogBoxShowing event — it's raised by Revit's internal native code
        // path (likely a Win32 MessageBox call) and bypasses the API event
        // pipeline entirely. To catch it we hook EVENT_OBJECT_SHOW for the
        // current process via SetWinEventHook; the callback inspects the
        // window title, PostMessages WM_CLOSE if it matches, and triggers the
        // existing queue-dialog flow through the same _pendingModelGuid +
        // OnIdling path that DialogBoxShowing uses.
        private IntPtr _winEventHook = IntPtr.Zero;
        private WinEventDelegate? _winEventCallback; // keep alive so GC doesn't reclaim it
        // Microsoft docs: EVENT_OBJECT_CREATE=0x8000, EVENT_OBJECT_SHOW=0x8002,
        // EVENT_OBJECT_HIDE=0x8003. Previous value 0x8003 was HIDE — that's why the
        // hook never caught the popup appearing.
        private const uint EVENT_OBJECT_CREATE = 0x8000;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint WINEVENT_OUTOFCONTEXT = 0;
        private const int OBJID_WINDOW = 0;
        private const int WM_CLOSE = 0x0010;
        // Single source of truth for the central-busy dialog family. Used by BOTH the
        // Win32 window-title path (IsCentralBusyTitle) and the DialogBoxShowing API path
        // (IsCentralBusyDialog). These two paths previously kept SEPARATE lists that drifted
        // apart — the Win32 list only had "Unable to Access" / "Cannot Access" and missed the
        // R24 variant ("Could Not Access the Model" / TaskDialog_Could_Not_Access_Model),
        // so on Revit 2024 — where this popup is raised natively and bypasses DialogBoxShowing
        // — nothing dismissed it and the native popup leaked to the user. Stored in
        // space-form; MatchesCentralBusy() normalizes the DialogId underscore-form to match.
        private static readonly string[] CentralBusySubstrings = new[]
        {
            "Unable to Access",
            "Could Not Access",
            "Cannot Access",
            "Access Denied",
            "Central Model Access",
            "Currently In Use",
            "Unavailable Resource",
            "Resource For Operation",
            "Model Not Available",
            "Not Currently Available",
        };

        // DialogIds use underscores ("TaskDialog_Could_Not_Access_Model"); native window
        // titles use spaces ("Could Not Access the Model"). Normalize underscores to spaces
        // so the one substring set above matches both forms, case-insensitively.
        private static bool MatchesCentralBusy(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var normalized = text.Replace('_', ' ');
            foreach (var s in CentralBusySubstrings)
            {
                if (normalized.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        // Separate list: Revit's "Sync With Central" progress dialog. The user wants this
        // hidden silently so sync runs in the background — DO NOT WM_CLOSE it (that would
        // cancel the sync). We use ShowWindow(SW_HIDE) instead so the dialog disappears
        // while Revit's sync logic keeps running to completion. No follow-up queue dialog.
        private static readonly string[] _win32SilentHideTitles = new[]
        {
            "Sync With Central",
            "Syncing with central",
        };
        private const int SW_HIDE = 0;

        public SyncConflictDialogInterceptor(
            UIApplication uiApp,
            ILogger? logger,
            Func<SyncTrafficControlService?> stcGetter,
            Func<IRevitContext?>? ctxGetter = null,
            Func<ISignalREventBus?>? eventBusGetter = null)
        {
            _uiApp = uiApp ?? throw new ArgumentNullException(nameof(uiApp));
            _logger = logger;
            _stcGetter = stcGetter ?? (() => null);
            _ctxGetter = ctxGetter ?? (() => null);
            _eventBusGetter = eventBusGetter ?? (() => null);
            _dispatcher = Dispatcher.CurrentDispatcher;
        }

        public void Start()
        {
            if (_started || _disposed) return;
            try
            {
                _uiApp.DialogBoxShowing += OnDialogShowing;
                _uiApp.Idling += OnIdling;

                // Install the OS-level hook in addition to the API event. Revit's
                // "Unable to Access the Model" popup bypasses DialogBoxShowing on
                // some versions; this hook catches the native window directly.
                TryInstallWinEventHook();

                _started = true;
                _logger?.LogInfo("[SyncQueueIntercept] Started — Revit central-busy dialog will be replaced by our queue dialog");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncQueueIntercept] Start failed: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (!_started) return;
            try
            {
                _uiApp.DialogBoxShowing -= OnDialogShowing;
                _uiApp.Idling -= OnIdling;
                TryUninstallWinEventHook();
            }
            catch { }
            _started = false;
        }

        private void OnDialogShowing(object? sender, DialogBoxShowingEventArgs e)
        {
            try
            {
                if (e == null) return;
                var dialogId = e.DialogId ?? string.Empty;

                // Pull the body text out of TaskDialogShowingEventArgs as well so we can
                // match dialogs whose DialogId doesn't fit any of our known substrings
                // (Revit varies the IDs between major versions, but the visible message
                // for "central in use" is much more stable).
                var message = string.Empty;
                if (e is TaskDialogShowingEventArgs tde)
                {
                    try { message = tde.Message ?? string.Empty; } catch { }
                }

                // Always log both fields so a real-world repro can confirm the pattern
                // match in IsCentralBusyDialog covers the user's Revit version.
                _logger?.LogDebug($"[SyncQueueIntercept] DialogBoxShowing: id={dialogId} msg={message}");

                // Sync-related dialog probe — kept at DEBUG so it's available during repros
                // but not noise in production logs. The DialogBoxShowing event already fires
                // for every Revit dialog; the unfiltered DEBUG line above is the canonical
                // diagnostic. This narrowed match used to log at INFO during development of
                // the sync-conflict interceptor; promote back to LogInfo only if a specific
                // sync-collision repro requires it.
                if (IndexOf(dialogId, "save") || IndexOf(dialogId, "sync") || IndexOf(dialogId, "central")
                    || IndexOf(dialogId, "model") || IndexOf(message, "file not saved")
                    || IndexOf(message, "another user"))
                {
                    _logger?.LogDebug($"[SyncQueueIntercept][probe] DialogBoxShowing: id='{dialogId}' msg='{message}'");
                }

                if (SaveAsActive()) return;
                if (!IsCentralBusyDialog(dialogId) && !IsCentralBusyMessage(message)) return;

                // Recency gate: only auto-route to our queue dialog when the user
                // actually clicked Sync recently. Without this, Revit's "Unable to
                // Access the Model" dialog fires during model-open (and other
                // background paths that touch central), and we'd auto-pop the queue
                // dialog for a sync the user never initiated. Same pattern used by
                // the "File not saved" branch in the Win32 hook below.
                if (!SyncAttemptedRecently())
                {
                    _logger?.LogDebug($"[SyncQueueIntercept] Central-busy dialog '{dialogId}' with no recent Sync attempt — letting Revit show it normally.");
                    return;
                }

                // Own-sync-failure gate: if WE are the active syncer for this model
                // (i.e. we already broadcast SyncStarting and the sync is now failing
                // at Revit's layer with "Unable to Access"), don't pop the queue UI —
                // the user is being told "another user is syncing" against themselves.
                // Let Revit's native dialog show so they see the real error and retry.
                //
                // DO NOT override / WM_CLOSE the dialog here. Empirically (v8 logs),
                // dismissing Revit's dialog while it's mid-retry aborts Revit's own
                // exponential-backoff lock-acquisition loop and BREAKS the sync. The
                // dialog appearance is part of Revit's normal retry surface; we must
                // leave it alone and let Revit's internal logic complete.
                if (IsOwnSyncFailureForActiveDocument())
                {
                    _logger?.LogInfo($"[SyncQueueIntercept] Central-busy dialog '{dialogId}' msg='{message}' fired while WE are the active syncer — own sync failed, letting Revit show its native error (no queue re-pop). {DescribeDocumentSyncState()}");
                    return;
                }

                // Debounce — Revit can fire the same dialog twice (e.g. one for the
                // command, one for the post-sync notification). One intercept per click.
                var now = DateTime.UtcNow;
                if ((now - _lastInterceptAtUtc) < InterceptDebounce) return;
                _lastInterceptAtUtc = now;

                _logger?.LogInfo($"[SyncQueueIntercept] Matched Revit central-busy dialog: {dialogId}");

                // Capture the model context BEFORE dismissing the dialog so the deferred
                // Idling handler knows which queue to join.
                string? modelGuid = null;
                string? modelName = null;
                try
                {
                    var doc = _uiApp.ActiveUIDocument?.Document;
                    if (doc != null)
                    {
                        modelName = doc.Title;
                        modelGuid = ModelGuidHelper.GetModelGuid(doc, _logger);
                    }
                }
                catch (Exception docEx)
                {
                    _logger?.LogDebug($"[SyncQueueIntercept] Doc lookup failed: {docEx.Message}");
                }

                // Dismiss Revit's native dialog. Both TaskDialog and MessageBox
                // variants support OverrideResult on the base type.
                try { e.OverrideResult(2 /* IDCANCEL */); }
                catch (Exception ovrEx)
                {
                    _logger?.LogWarning($"[SyncQueueIntercept] OverrideResult failed: {ovrEx.Message}");
                    return;
                }

                if (string.IsNullOrEmpty(modelGuid))
                {
                    _logger?.LogWarning("[SyncQueueIntercept] Suppressed Revit dialog but no model context — skipping queue dialog");
                    return;
                }

                _pendingModelGuid = modelGuid;
                _pendingModelName = modelName;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncQueueIntercept] OnDialogShowing failed: {ex.Message}");
            }
        }

        private void OnIdling(object? sender, IdlingEventArgs e)
        {
            if (_pendingModelGuid == null) return;

            var modelGuid = _pendingModelGuid;
            var modelName = _pendingModelName ?? string.Empty;
            _pendingModelGuid = null;
            _pendingModelName = null;

            // Show on the dispatcher thread so WPF dialog ownership works.
            try
            {
                _dispatcher.BeginInvoke(new Action(() => ShowQueueDialog(modelGuid, modelName)));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncQueueIntercept] Dispatcher invoke failed: {ex.Message}");
            }
        }

        private void ShowQueueDialog(string modelGuid, string modelName)
        {
            try
            {
                var stc = _stcGetter();
                if (stc == null)
                {
                    _logger?.LogWarning("[SyncQueueIntercept] SyncTrafficControlService not available — cannot show queue dialog");
                    return;
                }

                // Best-effort: if we DO know who's syncing (SignalR arrived late but did
                // arrive), surface their name. Otherwise fall back to a generic label.
                string blockerName = "Another user";
                DateTime? blockedSince = null;
                var activeSyncer = stc.GetActiveSyncer(modelGuid);
                if (activeSyncer != null)
                {
                    blockerName = activeSyncer.RevitUsername ?? activeSyncer.Username ?? blockerName;
                    blockedSince = activeSyncer.StartedAtUtc;
                }

                var ctx = _ctxGetter();
                var sessionId = ctx?.SessionId.ToString() ?? string.Empty;
                string username;
                try
                {
                    var revitName = _uiApp.Application?.Username;
                    // Prefer Application.Username when set. If it's empty (sometimes happens
                    // when the Autodesk account info hasn't been refreshed yet), fall back to
                    // the Windows account name, which is guaranteed unique per machine and
                    // matches what other identity layers (RevitContext, SessionRepository)
                    // recorded at session start.
                    username = !string.IsNullOrWhiteSpace(revitName) ? revitName! : Environment.UserName;
                }
                catch { username = Environment.UserName; }
                _logger?.LogInfo($"[SyncQueueIntercept] Queue-join identity resolved: username='{username}', sessionId={sessionId}");

                var dialog = new BIManageRevit.BIManage.Views.SyncTrafficControl.SyncConflictDialog(
                    blockerName,
                    blockedSince,
                    modelName,
                    _eventBusGetter(),
                    modelGuid);

                BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);

                var result = dialog.ShowDialog();

                if (result == true && !dialog.SyncReady)
                {
                    // User chose Join Queue — register so they get a SyncRequest event
                    // when their turn arrives. Same call the upstream + fallback paths use.
                    stc.JoinQueue(modelGuid, modelName, sessionId, username);
                    _logger?.LogInfo($"[SyncQueueIntercept] User joined queue for {modelGuid}");
                }
                else if (result == true && dialog.SyncReady)
                {
                    _logger?.LogInfo($"[SyncQueueIntercept] Blocker finished while dialog open — user can retry sync (model={modelGuid})");
                }
                else
                {
                    _logger?.LogInfo($"[SyncQueueIntercept] User dismissed queue dialog without joining (model={modelGuid})");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncQueueIntercept] ShowQueueDialog failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Matches the DialogId of Revit's central-file-busy dialogs across versions.
        /// Revit doesn't expose a stable enum, and the exact id varies between
        /// 2021..2026 — match on the recognisable substrings instead.
        /// Known IDs seen in the wild include:
        ///   "TaskDialog_Unavailable_Resource_For_Operation"  (R21-R23)
        ///   "TaskDialog_Could_Not_Access_Model"              (R24)
        ///   "TaskDialog_Unable_to_Access_the_Model"          (R25)
        ///   "TaskDialog_Central_Model_Access_Denied"         (variant)
        ///   "TaskDialog_Cannot_Access_Central"               (variant)
        /// </summary>
        private static bool IsCentralBusyDialog(string dialogId)
            => MatchesCentralBusy(dialogId); // shared set; _ ↔ space normalized for DialogId form

        // Fallback when DialogId doesn't match any known substring (Revit varies IDs
        // across versions). The visible message text in "Unable to Access the Model"
        // dialogs is much more stable — the wording "is not currently available" /
        // "operation cannot be completed" appears across all Revit versions we've
        // seen this dialog in. Matching by message catches the cases the DialogId
        // list misses, so the user's sync collision still routes into our queue UI.
        private static bool IsCentralBusyMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;

            // Specific central-busy phrases — only ever appear during sync collisions,
            // so unconditional matching is safe.
            if (IndexOf(message, "is not currently available")
                || IndexOf(message, "operation cannot be completed")
                || IndexOf(message, "currently in use")
                || IndexOf(message, "unable to access")
                || IndexOf(message, "cannot access the model")
                || IndexOf(message, "central file is in use")
                || IndexOf(message, "central model is in use")
                || IndexOf(message, "model is not available"))
                return true;

            // Local-model sync collision: when User B clicks Sync while User A is
            // syncing the same central, Revit shows a tiny "Revit / File not saved."
            // popup. The exact same text is also shown for ANY other cancelled save
            // (Esc, FailuresProcessing abort, other add-ins calling e.Cancel), so
            // only treat it as a sync collision when a Sync command was clicked
            // moments ago. The Save-As specific case is already filtered earlier by
            // SaveAsActive().
            if (IndexOf(message, "file not saved") && SyncAttemptedRecently())
                return true;

            return false;
        }

        private static bool IndexOf(string haystack, string needle)
            => haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        // ── Win32 OS-level dialog hook ──────────────────────────────────────────
        // Installed when DialogBoxShowing won't catch the popup (Revit raises it
        // through its native code path). Catches the window the moment it becomes
        // visible, PostMessages WM_CLOSE to dismiss it, then drops a pending
        // model-guid so the next Idling tick shows our SyncConflictDialog.

        private void TryInstallWinEventHook()
        {
            try
            {
                var pid = (uint)Process.GetCurrentProcess().Id;
                _winEventCallback = WinEventProc; // GC-pinned via instance field
                // Range CREATE..SHOW so we catch the window whether the popup
                // first fires CREATE or SHOW. The callback filters by event type.
                _winEventHook = SetWinEventHook(
                    EVENT_OBJECT_CREATE,
                    EVENT_OBJECT_SHOW,
                    IntPtr.Zero,
                    _winEventCallback,
                    pid,
                    0,
                    WINEVENT_OUTOFCONTEXT);
                if (_winEventHook == IntPtr.Zero)
                {
                    _logger?.LogWarning("[SyncQueueIntercept] Win32 hook install returned NULL — native popup will not be intercepted");
                    return;
                }
                _logger?.LogInfo($"[SyncQueueIntercept] Win32 hook installed (pid={pid}) — listening for native 'Unable to Access the Model' popup");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncQueueIntercept] Win32 hook install failed: {ex.Message}");
            }
        }

        private void TryUninstallWinEventHook()
        {
            if (_winEventHook == IntPtr.Zero) return;
            try { UnhookWinEvent(_winEventHook); } catch { }
            _winEventHook = IntPtr.Zero;
            _winEventCallback = null;
        }

        private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                if (idObject != OBJID_WINDOW) return; // only top-level window events
                if (eventType != EVENT_OBJECT_SHOW && eventType != EVENT_OBJECT_CREATE) return;

                // Get window title
                int len = GetWindowTextLength(hwnd);
                if (len <= 0) return;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                var title = sb.ToString();

                // Sync-collision dialog probe — kept at DEBUG so it's available for repros
                // but never spams production logs. Filter is narrowed to sync-collision
                // titles only (was previously catching the Home page via the broad "revit"
                // matcher, which flooded the log on every launch). If you need to extend
                // the match for a new Revit version, broaden the filter rather than the log
                // level.
                if (title.Length <= 60 &&
                    (IndexOf(title, "sync") || IndexOf(title, "central")
                     || IndexOf(title, "unable to access") || IndexOf(title, "file not saved")))
                {
                    _logger?.LogDebug($"[SyncQueueIntercept][probe] Win32 SHOW/CREATE title='{title}' hwnd=0x{hwnd.ToInt64():X} event={eventType}");
                }

                // Silent-hide path: Revit's "Sync With Central" progress dialog. We hide it
                // ONLY when the BackgroundSync engine is actively driving the sync (i.e.,
                // the user didn't click Sync themselves — our scheduler did). For
                // foreground user-clicked syncs we LET REVIT SHOW its progress dialog so
                // the user gets visual feedback for long-running operations (cloud syncs
                // can take several minutes; without the progress dialog the UI appears
                // frozen).
                //
                // EXCEPTION: when Revit shows this dialog with the inline retry banner
                // ("Another user has completed a sync with central. Revit is retrying your
                // sync with central."), a sync collision just happened. We always handle
                // that case below regardless of foreground/background.
                if (IsSilentHideTitle(title))
                {
                    var stcForHide = _stcGetter();
                    bool isBackgroundSync = stcForHide != null && stcForHide.IsBackgroundSyncActive;

                    if (isBackgroundSync)
                    {
                        try
                        {
                            ShowWindow(hwnd, SW_HIDE);
                            _logger?.LogInfo($"[SyncQueueIntercept] Hid 'Sync With Central' progress dialog silently (background sync): '{title}' (hwnd=0x{hwnd.ToInt64():X}).");
                        }
                        catch (Exception swEx)
                        {
                            _logger?.LogWarning($"[SyncQueueIntercept] ShowWindow(SW_HIDE) failed: {swEx.Message}");
                        }
                    }
                    else
                    {
                        // Foreground sync (user clicked Sync themselves). Leave Revit's
                        // progress dialog VISIBLE — for cloud / long-running syncs the
                        // user needs the visual feedback. Hiding it for foreground syncs
                        // makes Revit appear frozen during multi-minute cloud transfers
                        // (v9 logs showed a 230-second cloud sync with no visible progress
                        // because we hid the dialog).
                        _logger?.LogDebug($"[SyncQueueIntercept] Not hiding 'Sync With Central' progress dialog — foreground sync (user clicked Sync). title='{title}', hwnd=0x{hwnd.ToInt64():X}.");
                    }

                    // Collision-banner detection runs for BOTH foreground and background:
                    // if Revit's progress dialog contains the inline retry banner
                    // ("Another user has completed a sync with central. Revit is retrying
                    // your sync with central."), we route to our Join Queue popup so the
                    // user sees a clear queue prompt instead of either (a) a hidden
                    // dialog with no feedback, or (b) Revit's auto-retry with no
                    // queue position info.
                    try
                    {
                        var bodyText = ReadAllChildText(hwnd);
                        if (!SaveAsActive() && IsCollisionBanner(bodyText))
                        {
                            var now2 = DateTime.UtcNow;
                            if ((now2 - _lastInterceptAtUtc) >= InterceptDebounce)
                            {
                                _lastInterceptAtUtc = now2;
                                _logger?.LogInfo($"[SyncQueueIntercept] Detected sync collision banner inside 'Sync With Central' — routing to Join Queue popup.");
                                try
                                {
                                    var doc = _uiApp.ActiveUIDocument?.Document;
                                    if (doc != null)
                                    {
                                        _pendingModelName = doc.Title;
                                        _pendingModelGuid = ModelGuidHelper.GetModelGuid(doc, _logger);
                                    }
                                }
                                catch (Exception docEx)
                                {
                                    _logger?.LogDebug($"[SyncQueueIntercept] Doc lookup failed for collision-banner path: {docEx.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception scanEx)
                    {
                        _logger?.LogDebug($"[SyncQueueIntercept] Collision-banner scan failed: {scanEx.Message}");
                    }

                    return;
                }

                // Local-model sync collision: Revit posts a tiny dialog titled exactly
                // "Revit" with body "File not saved." after the user clicks Sync while
                // another user is already syncing. The title alone is too generic (the
                // main Revit window title starts with "Autodesk Revit..."), so we require
                // an EXACT title match AND a specific body to be sure it's the sync
                // failure popup and not something else.
                if (IsGenericRevitTitle(title))
                {
                    string bodyText2;
                    try { bodyText2 = ReadAllChildText(hwnd); }
                    catch { bodyText2 = string.Empty; }

                    // EVENT_OBJECT_CREATE often fires before Revit attaches the static-text
                    // controls that carry the "File not saved." body. In that case the read
                    // returns empty and the popup escapes our match — visible to the user.
                    // Re-poll the dialog text a few times over ~250 ms so children get a
                    // chance to materialize. All work happens on the WinEvent thread; we
                    // never block Revit's UI thread.
                    if (string.IsNullOrWhiteSpace(bodyText2) && SyncAttemptedRecently() && !SaveAsActive())
                    {
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            for (int attempt = 0; attempt < 5; attempt++)
                            {
                                try { await System.Threading.Tasks.Task.Delay(50); }
                                catch { return; }

                                string retryText;
                                try { retryText = ReadAllChildText(hwnd); }
                                catch { return; }
                                if (string.IsNullOrWhiteSpace(retryText)) continue;
                                if (!IsFileNotSavedBody(retryText)) return;
                                HandleFileNotSavedPopup(hwnd, "deferred body re-check");
                                return;
                            }
                        });
                    }

                    if (IsFileNotSavedBody(bodyText2))
                    {
                        if (SaveAsActive())
                        {
                            // "File not saved." caused by our own Save As protection blocking
                            // the save via e.Cancel(). Dismiss the popup silently — the user
                            // already saw our protection dialog — and clear the flag so the
                            // interceptor resumes normal operation for real sync collisions.
                            _logger?.LogInfo($"[SyncQueueIntercept] 'File not saved.' from Save As protection — dismissing silently (hwnd=0x{hwnd.ToInt64():X}).");
                            try { PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { }
                            NotifySaveAsCompleted();
                            return;
                        }

                        // "File not saved." is also shown for any other cancelled save
                        // (Esc, FailuresProcessing abort, other Event Protections calling
                        // e.Cancel). Treat as a sync collision only if the user actually
                        // clicked a Sync command moments ago.
                        if (!SyncAttemptedRecently())
                        {
                            _logger?.LogDebug($"[SyncQueueIntercept] 'File not saved.' with no recent Sync attempt — ignoring (hwnd=0x{hwnd.ToInt64():X}).");
                            return;
                        }

                        // Own-sync-failure gate: when WE are the active syncer and Revit
                        // surfaces "File not saved", our own sync just failed at the
                        // native layer (central became unavailable mid-save). Don't pop
                        // the queue UI — that tells the user "another user is syncing"
                        // against themselves and creates a self-trapping loop. Also
                        // don't WM_CLOSE — Revit's sync retry uses these dialogs as
                        // part of its lock-acquisition flow; dismissing them mid-flight
                        // aborts the sync (per v8 evidence).
                        if (IsOwnSyncFailureForActiveDocument())
                        {
                            _logger?.LogInfo($"[SyncQueueIntercept] 'File not saved.' fired while WE are the active syncer — own sync failed, letting Revit show its native error (no queue re-pop).");
                            return;
                        }

                        var now3 = DateTime.UtcNow;
                        if ((now3 - _lastInterceptAtUtc) < InterceptDebounce) return;
                        _lastInterceptAtUtc = now3;

                        _logger?.LogInfo($"[SyncQueueIntercept] Matched local-model sync 'File not saved' popup (hwnd=0x{hwnd.ToInt64():X}). Sending WM_CLOSE and routing to Join Queue.");

                        try { PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
                        catch (Exception pmEx)
                        {
                            _logger?.LogWarning($"[SyncQueueIntercept] PostMessage WM_CLOSE failed (file-not-saved): {pmEx.Message}");
                            return;
                        }

                        try
                        {
                            var doc = _uiApp.ActiveUIDocument?.Document;
                            if (doc != null)
                            {
                                _pendingModelName = doc.Title;
                                _pendingModelGuid = ModelGuidHelper.GetModelGuid(doc, _logger);
                            }
                        }
                        catch (Exception docEx)
                        {
                            _logger?.LogDebug($"[SyncQueueIntercept] Doc lookup failed for file-not-saved path: {docEx.Message}");
                        }
                        return;
                    }
                }

                if (SaveAsActive()) return;
                if (!IsCentralBusyTitle(title)) return;

                // Recency gate: Revit shows "Unable to Access the Model" during model
                // open (and other non-sync background paths) when central is busy. If
                // the user didn't actually click Sync recently, leave Revit's dialog
                // alone — don't auto-pop our queue UI for a sync they never initiated.
                if (!SyncAttemptedRecently())
                {
                    _logger?.LogDebug($"[SyncQueueIntercept] Central-busy window '{title}' with no recent Sync attempt — letting Revit show it normally (hwnd=0x{hwnd.ToInt64():X}).");
                    return;
                }

                // Own-sync-failure gate: same logic as the DialogBoxShowing branch —
                // if we are the active syncer, this dialog is Revit telling us OUR
                // sync just failed. Don't WM_CLOSE it and don't pop the queue UI;
                // let the user see Revit's native error and retry manually. (See
                // detailed comment in OnDialogShowing — WM_CLOSE mid-retry aborts
                // Revit's lock-acquisition backoff and breaks the sync, per v8.)
                if (IsOwnSyncFailureForActiveDocument())
                {
                    _logger?.LogInfo($"[SyncQueueIntercept] Central-busy window '{title}' fired while WE are the active syncer — own sync failed, letting Revit show its native error (no queue re-pop). {DescribeDocumentSyncState()}");
                    return;
                }

                // Debounce so we don't fire twice for the same popup
                var now = DateTime.UtcNow;
                if ((now - _lastInterceptAtUtc) < InterceptDebounce) return;
                _lastInterceptAtUtc = now;

                _logger?.LogInfo($"[SyncQueueIntercept] Matched native central-busy window: '{title}' (hwnd=0x{hwnd.ToInt64():X}). Sending WM_CLOSE.");

                // Dismiss the popup
                try { PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
                catch (Exception pmEx)
                {
                    _logger?.LogWarning($"[SyncQueueIntercept] PostMessage WM_CLOSE failed: {pmEx.Message}");
                    return;
                }

                // Capture model context so the deferred Idling handler can join the queue
                try
                {
                    var doc = _uiApp.ActiveUIDocument?.Document;
                    if (doc != null)
                    {
                        _pendingModelName = doc.Title;
                        _pendingModelGuid = ModelGuidHelper.GetModelGuid(doc, _logger);
                    }
                }
                catch (Exception docEx)
                {
                    _logger?.LogDebug($"[SyncQueueIntercept] Doc lookup failed inside Win32 hook: {docEx.Message}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[SyncQueueIntercept] WinEventProc error: {ex.Message}");
            }
        }

        /// <summary>
        /// Shared dismiss + queue-routing path for the "Revit / File not saved." popup.
        /// Called from both the immediate-match branch in <see cref="WinEventProc"/> and
        /// the deferred body re-check that runs when child text wasn't ready yet on
        /// EVENT_OBJECT_CREATE. Single source of truth keeps the debounce, WM_CLOSE,
        /// and pending-model-guid logic identical between the two callers.
        /// </summary>
        private void HandleFileNotSavedPopup(IntPtr hwnd, string reason)
        {
            try
            {
                var now3 = DateTime.UtcNow;
                if ((now3 - _lastInterceptAtUtc) < InterceptDebounce) return;
                _lastInterceptAtUtc = now3;

                _logger?.LogInfo($"[SyncQueueIntercept] Matched local-model sync 'File not saved' popup ({reason}, hwnd=0x{hwnd.ToInt64():X}). Sending WM_CLOSE and routing to Join Queue.");

                try { PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
                catch (Exception pmEx)
                {
                    _logger?.LogWarning($"[SyncQueueIntercept] PostMessage WM_CLOSE failed ({reason}): {pmEx.Message}");
                    return;
                }

                try
                {
                    var doc = _uiApp.ActiveUIDocument?.Document;
                    if (doc != null)
                    {
                        _pendingModelName = doc.Title;
                        _pendingModelGuid = ModelGuidHelper.GetModelGuid(doc, _logger);
                    }
                }
                catch (Exception docEx)
                {
                    _logger?.LogDebug($"[SyncQueueIntercept] Doc lookup failed for file-not-saved path ({reason}): {docEx.Message}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[SyncQueueIntercept] HandleFileNotSavedPopup failed: {ex.Message}");
            }
        }

        private static bool IsCentralBusyTitle(string title)
            => MatchesCentralBusy(title); // same shared set as IsCentralBusyDialog — no drift

        private static bool IsSilentHideTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            foreach (var pattern in _win32SilentHideTitles)
            {
                if (IndexOf(title, pattern)) return true;
            }
            return false;
        }

        // The inline banner Revit writes into the "Sync With Central" progress dialog
        // when another user finished syncing mid-attempt and Revit is auto-retrying.
        // Matching the banner lets us pop our Join Queue dialog instead of leaving the
        // user staring at a hidden progress window with no feedback.
        private static bool IsCollisionBanner(string bodyText)
        {
            if (string.IsNullOrWhiteSpace(bodyText)) return false;
            return IndexOf(bodyText, "another user has completed a sync")
                || IndexOf(bodyText, "retrying your sync")
                || IndexOf(bodyText, "another user is currently saving")
                || IndexOf(bodyText, "another user has updated");
        }

        // The local-model sync-collision popup has the bare title "Revit". The main
        // Revit application window also contains "Revit" in its title (e.g. "Autodesk
        // Revit 2025 - [filename.rvt]") so we require an EXACT match to avoid catching
        // the main window. Case-insensitive trim handles any stray whitespace.
        private static bool IsGenericRevitTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            return string.Equals(title.Trim(), "Revit", StringComparison.OrdinalIgnoreCase);
        }

        // The exact body Revit shows in the local-model sync-collision popup
        // ("File not saved."). Match conservatively — this title is too generic to
        // trust on title alone.
        private static bool IsFileNotSavedBody(string bodyText)
        {
            if (string.IsNullOrWhiteSpace(bodyText)) return false;
            return IndexOf(bodyText, "file not saved");
        }

        // Concatenates the GetWindowText of every child of hwnd (one level deep).
        // The "Sync With Central" dialog is composed of multiple labels/static controls;
        // the parent's GetWindowText only returns the title bar, so we walk children
        // to find the inline banner text.
        private string ReadAllChildText(IntPtr parent)
        {
            if (parent == IntPtr.Zero) return string.Empty;
            var sb = new StringBuilder(512);
            EnumChildWindows(parent, (childHwnd, _) =>
            {
                try
                {
                    int len = GetWindowTextLength(childHwnd);
                    if (len > 0)
                    {
                        var buf = new StringBuilder(len + 1);
                        GetWindowText(childHwnd, buf, buf.Capacity);
                        if (buf.Length > 0)
                        {
                            sb.Append(buf.ToString());
                            sb.Append(' ');
                        }
                    }
                }
                catch { }
                return true; // keep enumerating
            }, IntPtr.Zero);
            return sb.ToString();
        }

        // P/Invoke
        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
