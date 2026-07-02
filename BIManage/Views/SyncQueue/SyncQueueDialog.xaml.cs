using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Core;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Revit.SyncTrafficControl;
using BIManage.ViewModels.SyncQueue;
using WpfVisibility = System.Windows.Visibility;
using WpfColor = System.Windows.Media.Color;


namespace BIManageRevit.BIManage.Views.SyncQueue
{
    public partial class SyncQueueDialog : Window
    {
        private readonly SyncQueueViewModel _viewModel;

        // Periodically re-pulls the active syncer set from DB / in-memory snapshot while
        // the dialog is open. Fixes the "sometimes Currently Syncing badge is missing"
        // intermittent bug where the SignalR SyncStarting event arrived BEFORE the
        // sync_queue row was persisted (or was lost entirely), and the dialog was
        // stuck with an empty SyncingUsers list until the user reopened the window.
        private DispatcherTimer? _refreshTimer;

        public SyncQueueDialog(
            ILogger? logger = null,
            ISignalREventBus? eventBus = null,
            string? modelGuid = null,
            ISignalRConnectionManager? connectionManager = null,
            string? sessionId = null,
            SyncRepository? syncRepository = null,
            SyncTrafficControlService? syncTrafficControl = null,
            SessionRepository? sessionRepository = null)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            _viewModel = new SyncQueueViewModel(logger, eventBus, modelGuid, connectionManager, sessionId, syncRepository, syncTrafficControl, sessionRepository);
            DataContext = _viewModel;

            _viewModel.SyncHistory.CollectionChanged += OnSyncHistoryChanged;

            Loaded += async (s, e) =>
            {
                _viewModel.SubscribeSignalREvents();
                _viewModel.RefreshHeaderStatus();
                _viewModel.LoadActiveRemoteSyncers();
                await _viewModel.LoadHistoryAsync();
                UpdateEmptyState();

                // Cheap heartbeat poll — every 5 s ask the VM to re-pull active syncers from
                // DB + in-memory snapshot. SignalR events still drive the live updates; this
                // poll just self-heals when an event was missed or arrived out of order.
                _refreshTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(5)
                };
                _refreshTimer.Tick += (_, _) =>
                {
                    _viewModel.RefreshHeaderStatus();
                    _viewModel.LoadActiveRemoteSyncers();
                    UpdateEmptyState();
                };
                _refreshTimer.Start();
            };

            Closed += (s, e) =>
            {
                if (_refreshTimer != null)
                {
                    _refreshTimer.Stop();
                    _refreshTimer = null;
                }
                _viewModel.UnsubscribeSignalREvents();
                _viewModel.SyncHistory.CollectionChanged -= OnSyncHistoryChanged;
            };
        }

        private void OnSyncHistoryChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() => UpdateEmptyState());
        }

        private void UpdateEmptyState()
        {
            // Empty-state messaging takes priority over grid when:
            //   1) no model is open (NoModel) — explain why the user sees nothing
            //   2) SignalR is disconnected (Disconnected) — explain why no updates flow
            //   3) the model IS in scope + SignalR connected but no rows yet (Live, empty grid)
            // Otherwise the grid is the source of truth and the empty state hides.
            bool nonLiveHeader = !string.Equals(_viewModel.HeaderStatusKind, "Live", StringComparison.OrdinalIgnoreCase);
            bool showEmpty = nonLiveHeader || _viewModel.SyncHistory.Count == 0;
            EmptyState.Visibility = showEmpty ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            GridSyncHistory.Visibility = (!showEmpty) ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ClearHistory();
            UpdateEmptyState();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
    }

    /// <summary>
    /// Converts an integer count to Visibility (> 0 = Visible, 0 = Collapsed).
    /// </summary>
    public class CountToVisibilityConverter : IValueConverter
    {
        public static readonly CountToVisibilityConverter Instance = new CountToVisibilityConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var count = value is int i ? i : 0;
            return count > 0 ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts sync status to background color for badge.
    /// </summary>
    public class SyncStatusToBackgroundConverter : IValueConverter
    {
        public static readonly SyncStatusToBackgroundConverter Instance = new SyncStatusToBackgroundConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var status = value as string ?? "";
            return status switch
            {
                "InQueue" => new SolidColorBrush(WpfColor.FromRgb(254, 243, 199)),    // #FEF3C7 amber
                "Syncing" => new SolidColorBrush(WpfColor.FromRgb(219, 234, 254)),    // #DBEAFE blue
                "Completed" => new SolidColorBrush(WpfColor.FromRgb(220, 252, 231)),   // #DCFCE7 green
                "Cancelled" => new SolidColorBrush(WpfColor.FromRgb(254, 243, 199)),   // #FEF3C7 yellow
                "Failed" => new SolidColorBrush(WpfColor.FromRgb(254, 226, 226)),      // #FEE2E2 red
                _ => new SolidColorBrush(WpfColor.FromRgb(241, 245, 249))              // #F1F5F9 gray
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Per-kind visibility converter for the dialog's empty state. Each static
    /// instance is bound to a specific HeaderStatusKind ("Live" / "Disconnected" /
    /// "NoModel") and returns Visible iff the bound value matches the expected kind.
    /// Used in XAML so the EmptyState border can show different copy depending on
    /// which "why is this empty?" condition is actually true.
    /// </summary>
    public class HeaderStatusKindToVisibilityConverter : IValueConverter
    {
        public static readonly HeaderStatusKindToVisibilityConverter Live = new HeaderStatusKindToVisibilityConverter("Live");
        public static readonly HeaderStatusKindToVisibilityConverter Disconnected = new HeaderStatusKindToVisibilityConverter("Disconnected");
        public static readonly HeaderStatusKindToVisibilityConverter NoModel = new HeaderStatusKindToVisibilityConverter("NoModel");

        private readonly string _expected;
        private HeaderStatusKindToVisibilityConverter(string expected) { _expected = expected; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var kind = value as string ?? "";
            return string.Equals(kind, _expected, StringComparison.OrdinalIgnoreCase)
                ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>
    /// Converts the dialog HeaderStatusKind ("Live" / "Disconnected" / "NoModel") to
    /// a background brush for the small status pill next to the title — green when live,
    /// amber when no model, red when disconnected.
    /// </summary>
    public class HeaderStatusBackgroundConverter : IValueConverter
    {
        public static readonly HeaderStatusBackgroundConverter Instance = new HeaderStatusBackgroundConverter();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var kind = value as string ?? "";
            return kind switch
            {
                "Live"         => new SolidColorBrush(WpfColor.FromRgb(220, 252, 231)), // #DCFCE7
                "Disconnected" => new SolidColorBrush(WpfColor.FromRgb(254, 226, 226)), // #FEE2E2
                "NoModel"      => new SolidColorBrush(WpfColor.FromRgb(254, 243, 199)), // #FEF3C7
                _              => new SolidColorBrush(WpfColor.FromRgb(241, 245, 249))
            };
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>Foreground colour pair for <see cref="HeaderStatusBackgroundConverter"/>.</summary>
    public class HeaderStatusForegroundConverter : IValueConverter
    {
        public static readonly HeaderStatusForegroundConverter Instance = new HeaderStatusForegroundConverter();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var kind = value as string ?? "";
            return kind switch
            {
                "Live"         => new SolidColorBrush(WpfColor.FromRgb(22, 101, 52)),
                "Disconnected" => new SolidColorBrush(WpfColor.FromRgb(153, 27, 27)),
                "NoModel"      => new SolidColorBrush(WpfColor.FromRgb(146, 64, 14)),
                _              => new SolidColorBrush(WpfColor.FromRgb(71, 85, 105))
            };
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>
    /// Converts sync status to foreground color for badge text.
    /// </summary>
    public class SyncStatusToForegroundConverter : IValueConverter
    {
        public static readonly SyncStatusToForegroundConverter Instance = new SyncStatusToForegroundConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var status = value as string ?? "";
            return status switch
            {
                "InQueue" => new SolidColorBrush(WpfColor.FromRgb(146, 64, 14)),     // #92400E amber
                "Syncing" => new SolidColorBrush(WpfColor.FromRgb(30, 64, 175)),     // #1E40AF blue
                "Completed" => new SolidColorBrush(WpfColor.FromRgb(22, 101, 52)),    // #166534 green
                "Cancelled" => new SolidColorBrush(WpfColor.FromRgb(146, 64, 14)),    // #92400E yellow
                "Failed" => new SolidColorBrush(WpfColor.FromRgb(153, 27, 27)),       // #991B1B red
                _ => new SolidColorBrush(WpfColor.FromRgb(71, 85, 105))              // #475569 gray
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
