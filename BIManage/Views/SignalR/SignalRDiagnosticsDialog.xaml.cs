using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;
using System.Windows.Threading;
using BIManage.Infrastructure.SignalR.Core;


namespace BIManageRevit.BIManage.Views.SignalR
{
    public partial class SignalRDiagnosticsDialog : Window
    {
        private readonly ISignalRService _signalRService;
        private readonly ISignalRConnectionManager _connectionManager;
        private readonly DispatcherTimer _refreshTimer;

        public SignalRDiagnosticsDialog(ISignalRService signalRService, ISignalRConnectionManager connectionManager)
        {
            InitializeComponent();
            _signalRService = signalRService;
            _connectionManager = connectionManager;

            // Subscribe to real-time state changes
            if (_signalRService != null)
                _signalRService.ConnectionStateChanged += OnConnectionStateChanged;

            // Refresh dynamic fields every 2 seconds
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _refreshTimer.Tick += (s, e) => RefreshDisplay();
            _refreshTimer.Start();

            RefreshDisplay();
        }

        private void OnConnectionStateChanged(object sender, SignalRConnectionStateChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(RefreshDisplay));
        }

        private void RefreshDisplay()
        {
            if (_signalRService == null)
            {
                StatusTitle.Text = "SignalR Not Configured";
                StatusCircle.Background = new SolidColorBrush(WpfColor.FromRgb(148, 163, 184)); // gray
                StatusIcon.Text = "?";
                return;
            }

            var state = _signalRService.State;

            // Status indicator
            switch (state)
            {
                case SignalRConnectionState.Connected:
                    StatusCircle.Background = new SolidColorBrush(WpfColor.FromRgb(34, 197, 94));  // green
                    StatusIcon.Text = "\u2713"; // checkmark
                    StatusTitle.Text = "Connected";
                    break;
                case SignalRConnectionState.Connecting:
                case SignalRConnectionState.Reconnecting:
                    StatusCircle.Background = new SolidColorBrush(WpfColor.FromRgb(245, 158, 11)); // amber
                    StatusIcon.Text = "\u2022\u2022\u2022"; // dots
                    StatusTitle.Text = state.ToString();
                    break;
                case SignalRConnectionState.Disconnecting:
                    StatusCircle.Background = new SolidColorBrush(WpfColor.FromRgb(245, 158, 11)); // amber
                    StatusIcon.Text = "\u2193"; // down arrow
                    StatusTitle.Text = "Disconnecting";
                    break;
                default:
                    StatusCircle.Background = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68));  // red
                    StatusIcon.Text = "\u2717"; // X
                    StatusTitle.Text = "Disconnected";
                    break;
            }

            // Connection info
            StateText.Text = state.ToString();
            ConnectionIdText.Text = _signalRService.ConnectionId ?? "(none)";
            HubUrlText.Text = _signalRService.HubUrl ?? "(not set)";

            var lastConnected = _signalRService.LastConnectedAt;
            LastConnectedText.Text = lastConnected.HasValue
                ? lastConnected.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "Never";

            ReconnectAttemptsText.Text = _signalRService.ReconnectAttemptCount.ToString();

            var rateLimitUntil = _signalRService.RateLimitedUntil;
            if (rateLimitUntil > DateTime.UtcNow)
            {
                var remaining = (rateLimitUntil - DateTime.UtcNow).TotalSeconds;
                RateLimitText.Text = $"Yes ({remaining:F0}s remaining)";
                RateLimitText.Foreground = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68));
            }
            else
            {
                RateLimitText.Text = "No";
                RateLimitText.Foreground = new SolidColorBrush(WpfColor.FromRgb(71, 85, 105));
            }

            // Groups from connection manager (includes document-level groups)
            IReadOnlyList<string> groups = _connectionManager?.ActiveGroups ?? _signalRService.JoinedGroups;
            ActiveGroupsText.Text = groups.Count > 0 ? string.Join("\n", groups) : "(none)";

            ListenerCountText.Text = _connectionManager?.ListenerCount.ToString() ?? "N/A";

            // Last error
            var lastError = _signalRService.LastError;
            if (!string.IsNullOrEmpty(lastError))
            {
                ErrorRow.Visibility = System.Windows.Visibility.Visible;
                LastErrorText.Text = lastError;
            }
            else
            {
                ErrorRow.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            if (_signalRService == null) return;

            TestConnectionBtn.IsEnabled = false;
            ActionStatusText.Text = "Testing connection...";
            ActionStatusText.Visibility = System.Windows.Visibility.Visible;

            try
            {
                await _signalRService.ConnectAsync();
                ActionStatusText.Text = _signalRService.IsConnected
                    ? "Connection successful!"
                    : "Connection attempt completed — check state above.";
            }
            catch (Exception ex)
            {
                ActionStatusText.Text = $"Connection failed: {ex.Message}";
            }
            finally
            {
                TestConnectionBtn.IsEnabled = true;
                RefreshDisplay();
            }
        }

        private async void ForceReconnect_Click(object sender, RoutedEventArgs e)
        {
            if (_signalRService == null) return;

            ForceReconnectBtn.IsEnabled = false;
            ActionStatusText.Text = "Disconnecting...";
            ActionStatusText.Visibility = System.Windows.Visibility.Visible;

            try
            {
                await _signalRService.DisconnectAsync();
                RefreshDisplay();

                ActionStatusText.Text = "Reconnecting...";
                _signalRService.ResetReconnectAttempts();
                await _signalRService.ConnectAsync();

                ActionStatusText.Text = _signalRService.IsConnected
                    ? "Reconnection successful!"
                    : "Reconnection attempt completed — check state above.";
            }
            catch (Exception ex)
            {
                ActionStatusText.Text = $"Reconnection failed: {ex.Message}";
            }
            finally
            {
                ForceReconnectBtn.IsEnabled = true;
                RefreshDisplay();
            }
        }

        private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== SignalR Diagnostics ===");
            sb.AppendLine($"Timestamp:          {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"State:              {_signalRService?.State}");
            sb.AppendLine($"Connection ID:      {_signalRService?.ConnectionId ?? "(none)"}");
            sb.AppendLine($"Hub URL:            {_signalRService?.HubUrl ?? "(not set)"}");

            var lastConnected = _signalRService?.LastConnectedAt;
            sb.AppendLine($"Last Connected:     {(lastConnected.HasValue ? lastConnected.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "Never")}");
            sb.AppendLine($"Reconnect Attempts: {_signalRService?.ReconnectAttemptCount}");

            var rateLimitUntil = _signalRService?.RateLimitedUntil ?? DateTime.MinValue;
            sb.AppendLine($"Rate Limited:       {(rateLimitUntil > DateTime.UtcNow ? $"Yes ({(rateLimitUntil - DateTime.UtcNow).TotalSeconds:F0}s)" : "No")}");

            IReadOnlyList<string> groups = _connectionManager?.ActiveGroups ?? _signalRService?.JoinedGroups;
            sb.AppendLine($"Active Groups:      {(groups != null && groups.Count > 0 ? string.Join(", ", groups) : "(none)")}");
            sb.AppendLine($"Listeners:          {_connectionManager?.ListenerCount.ToString() ?? "N/A"}");

            var lastError = _signalRService?.LastError;
            if (!string.IsNullOrEmpty(lastError))
                sb.AppendLine($"Last Error:         {lastError}");

            Clipboard.SetText(sb.ToString());
            ActionStatusText.Text = "Diagnostics copied to clipboard!";
            ActionStatusText.Visibility = System.Windows.Visibility.Visible;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _refreshTimer?.Stop();
            if (_signalRService != null)
                _signalRService.ConnectionStateChanged -= OnConnectionStateChanged;
        }
    }
}
