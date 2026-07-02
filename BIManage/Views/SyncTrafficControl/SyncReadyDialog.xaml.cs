using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Events;


namespace BIManageRevit.BIManage.Views.SyncTrafficControl
{
    public partial class SyncReadyDialog : Window
    {
        private readonly DispatcherTimer _countdownTimer;
        private int _remainingSeconds = 60;

        // First-clicker-wins auto-close: when another peer broadcasts SyncStarting for
        // the same model, our Sync Now popup must dismiss itself so the user doesn't
        // click "Sync Now" on a stale dialog and try to sync on top of someone who
        // already won the race.
        private readonly ISignalREventBus? _eventBus;
        private readonly string? _modelGuid;
        private readonly string? _localSessionId;
        private readonly ILogger? _logger;
        private Action<SyncStartingEvent>? _onSyncStarting;

        // Diagnostic — last close reason. Set by every code path that calls Close()
        // so we can attribute the dialog's lifecycle in the log. Without this, when
        // the v6 "User declined sync (or peer won race)" message fires we have no
        // way to know which of these actually happened.
        private string _closeReason = "unknown";

        /// <summary>
        /// True = user clicked "Sync Now", False/null = cancelled or timed out or
        /// auto-dismissed because another peer started syncing first.
        /// </summary>
        public bool SyncRequested { get; private set; }

        /// <summary>
        /// Diagnostic accessor — returns the reason this dialog closed (e.g.
        /// "sync-now-click", "cancel-click", "esc-key", "window-close",
        /// "peer-syncstarting", "countdown-zero", "unknown"). Used by the caller
        /// to attribute lifecycle in the log.
        /// </summary>
        public string CloseReason => _closeReason;

        public SyncReadyDialog(string modelName)
            : this(modelName, eventBus: null, modelGuid: null, localSessionId: null, logger: null)
        {
        }

        public SyncReadyDialog(string modelName, ISignalREventBus? eventBus, string? modelGuid, string? localSessionId)
            : this(modelName, eventBus, modelGuid, localSessionId, logger: null)
        {
        }

        public SyncReadyDialog(string modelName, ISignalREventBus? eventBus, string? modelGuid, string? localSessionId, ILogger? logger)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);
            ModelNameText.Text = modelName;

            _eventBus = eventBus;
            _modelGuid = modelGuid;
            _localSessionId = localSessionId;
            _logger = logger;

            _logger?.LogInfo($"[SyncReadyDialog] Constructed for model={modelGuid}, localSession={localSessionId}");

            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdownTimer.Tick += OnCountdownTick;
            // Defer starting the countdown until the window is actually painted on
            // screen. Starting in the constructor lets seconds tick away while WPF +
            // Revit's dispatcher renders the dialog, which on a busy UI thread can
            // burn 5-10s of the user's 60s window before they even see the buttons —
            // user reported "the window loads too long, I can't click Sync Now".
            // ContentRendered fires once after the first paint; Activate() pulls
            // focus so Sync Now is one click away (otherwise Revit may still own
            // the foreground and the first click goes to Revit).
            ContentRendered += (_, _) =>
            {
                _logger?.LogInfo($"[SyncReadyDialog] ContentRendered fired — countdown starting, calling Activate (model={_modelGuid})");
                _countdownTimer.Start();
                try { Activate(); } catch (Exception activateEx) { _logger?.LogDebug($"[SyncReadyDialog] Activate threw: {activateEx.Message}"); }
            };

            // Subscribe to peer-started-syncing events so we can auto-close when another
            // user wins the first-clicker-wins race. Same pattern that SyncConflictDialog
            // uses to auto-close when the blocking user finishes.
            if (_eventBus != null && !string.IsNullOrEmpty(_modelGuid))
            {
                _onSyncStarting = e =>
                {
                    if (string.IsNullOrEmpty(e?.ModelGuid)) return;
                    if (!string.Equals(e.ModelGuid, _modelGuid, StringComparison.OrdinalIgnoreCase)) return;
                    // Skip queue-piggyback events — only a REAL SyncStarting (no QueueAction)
                    // means someone actually started syncing. The listener already routes
                    // piggyback events to SyncQueueUpdateEvent, so we shouldn't see those
                    // here — but defense in depth: check session is not our own.
                    if (!string.IsNullOrEmpty(_localSessionId)
                        && string.Equals(e.SessionId, _localSessionId, StringComparison.OrdinalIgnoreCase))
                        return; // it's our own sync — don't close on our own broadcast
                    _logger?.LogInfo($"[SyncReadyDialog] Auto-close: peer SyncStarting fired (peer session={e.SessionId}, model={_modelGuid}) — closing dialog");
                    // A peer claimed the turn. Close this popup so the user doesn't try
                    // to race against an already-started sync.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            _countdownTimer.Stop();
                            _closeReason = "peer-syncstarting";
                            SyncRequested = false;
                            Close();
                        }
                        catch (Exception closeEx) { _logger?.LogDebug($"[SyncReadyDialog] auto-close threw: {closeEx.Message}"); }
                    }));
                };
                try { _eventBus.Subscribe(_onSyncStarting); } catch (Exception subEx) { _logger?.LogWarning($"[SyncReadyDialog] event-bus subscribe failed: {subEx.Message}"); }
            }
        }

        private void OnCountdownTick(object sender, EventArgs e)
        {
            _remainingSeconds--;
            // When the user's turn comes and they don't react, the queue should
            // continue moving instead of stalling — so the countdown auto-syncs
            // (same effect as clicking Sync Now) rather than auto-cancelling.
            CountdownText.Text = $"Auto-syncing in {_remainingSeconds} seconds...";

            if (_remainingSeconds <= 0)
            {
                _logger?.LogInfo($"[SyncReadyDialog] Countdown reached 0 — auto-accepting sync (model={_modelGuid})");
                _countdownTimer.Stop();
                _closeReason = "countdown-zero";
                SyncRequested = true; // auto-accept the turn
                Close();
            }
        }

        private void SyncNowButton_Click(object sender, RoutedEventArgs e)
        {
            _logger?.LogInfo($"[SyncReadyDialog] User clicked Sync Now (model={_modelGuid})");
            _countdownTimer.Stop();
            _closeReason = "sync-now-click";
            SyncRequested = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _logger?.LogInfo($"[SyncReadyDialog] User clicked Cancel (model={_modelGuid})");
            _countdownTimer.Stop();
            _closeReason = "cancel-click";
            SyncRequested = false;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _logger?.LogInfo($"[SyncReadyDialog] User pressed Escape (model={_modelGuid})");
                _countdownTimer.Stop();
                _closeReason = "esc-key";
                SyncRequested = false;
                Close();
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // If we got here without a known reason set, the window was closed via X /
            // Alt-F4 / external Close() — log so we can distinguish from cancel/esc.
            if (_closeReason == "unknown")
                _logger?.LogWarning($"[SyncReadyDialog] OnClosing fired with no known close reason (X / Alt-F4 / external Close) for model={_modelGuid}");
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _logger?.LogInfo($"[SyncReadyDialog] Closed — reason='{_closeReason}', SyncRequested={SyncRequested} (model={_modelGuid})");
            _countdownTimer.Stop();
            // Unsubscribe from the peer-started-syncing event so the dialog can be GC'd.
            if (_eventBus != null && _onSyncStarting != null)
            {
                try { _eventBus.Unsubscribe(_onSyncStarting); } catch { }
                _onSyncStarting = null;
            }
            base.OnClosed(e);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
    }
}
