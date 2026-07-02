using System;
using System.Windows;
using System.Windows.Input;
using BIManage.Infrastructure.SignalR.Events;

using WpfVisibility = System.Windows.Visibility;


namespace BIManageRevit.BIManage.Views.SyncTrafficControl
{
    public partial class SyncConflictDialog : Window
    {
        private readonly ISignalREventBus? _eventBus;
        private readonly string _modelGuid;
        private Action<SyncCompletedEvent>? _onSyncCompleted;
        private Action<SyncCancelledEvent>? _onSyncCancelled;

        public string BlockedByUsername { get; }

        /// <summary>
        /// True when the blocking user finished syncing while this dialog was open.
        /// The caller should proceed with sync immediately instead of joining the queue.
        /// </summary>
        public bool SyncReady { get; private set; }

        public SyncConflictDialog(
            string blockedByUsername,
            DateTime? blockedSince,
            string modelName,
            ISignalREventBus? eventBus = null,
            string? modelGuid = null)
        {
            BlockedByUsername = blockedByUsername;
            _eventBus = eventBus;
            _modelGuid = modelGuid ?? "";
            DataContext = this;
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            ModelNameText.Text = modelName;

            if (blockedSince.HasValue)
            {
                var duration = DateTime.UtcNow - blockedSince.Value;
                if (duration.TotalMinutes < 1)
                    DurationText.Text = "Started less than a minute ago";
                else if (duration.TotalMinutes < 60)
                    DurationText.Text = $"Started {(int)duration.TotalMinutes} minute{((int)duration.TotalMinutes != 1 ? "s" : "")} ago";
                else
                    DurationText.Text = $"Started {(int)duration.TotalHours}h {duration.Minutes}m ago";
            }
            else
            {
                DurationText.Text = "Started recently";
            }

            Loaded += (s, e) => SubscribeEvents();
            Closed += (s, e) => UnsubscribeEvents();
        }

        private void SubscribeEvents()
        {
            if (_eventBus == null || string.IsNullOrEmpty(_modelGuid))
                return;

            _onSyncCompleted = e =>
            {
                if (e.ModelGuid == _modelGuid)
                    Dispatcher.Invoke(OnBlockerFinished);
            };

            _onSyncCancelled = e =>
            {
                if (e.ModelGuid == _modelGuid)
                    Dispatcher.Invoke(OnBlockerFinished);
            };

            _eventBus.Subscribe(_onSyncCompleted);
            _eventBus.Subscribe(_onSyncCancelled);
        }

        private void OnBlockerFinished()
        {
            SyncReady = true;

            // Brief visual feedback before auto-close
            ReadyBorder.Visibility = WpfVisibility.Visible;
            ExplanationText.Visibility = WpfVisibility.Collapsed;

            DialogResult = true;
            Close();
        }

        private void UnsubscribeEvents()
        {
            if (_eventBus == null) return;

            if (_onSyncCompleted != null)
            {
                _eventBus.Unsubscribe(_onSyncCompleted);
                _onSyncCompleted = null;
            }
            if (_onSyncCancelled != null)
            {
                _eventBus.Unsubscribe(_onSyncCancelled);
                _onSyncCancelled = null;
            }
        }

        private void JoinQueueButton_Click(object sender, RoutedEventArgs e)
        {
            SyncReady = false;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
    }
}
