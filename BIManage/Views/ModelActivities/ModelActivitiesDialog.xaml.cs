using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Core;
using BIManage.Infrastructure.SignalR.Events;
using BIManageRevit.BIManage.ViewModels.ModelActivities;
using WpfVisibility = System.Windows.Visibility;


namespace BIManageRevit.BIManage.Views.ModelActivities
{
    public partial class ModelActivitiesDialog : Window
    {
        private readonly ModelActivitiesViewModel _viewModel;
        private readonly ILogger? _logger;
        private readonly string _currentUsername;
        private readonly string? _projectName;
        private readonly string? _projectId;
        private readonly ModelAdminsSyncService? _modelAdminsService;
        private string? _chatScope;  // "model", "project", "direct"
        private string? _chatTarget; // null = group chat, username = private chat

        public ModelActivitiesDialog(
            string modelGuid,
            string modelName,
            string? centralModelPath,
            string? modelType,
            string currentUsername,
            string? projectName = null,
            string? projectId = null,
            SessionRepository? sessionRepository = null,
            ILogger? logger = null,
            ISignalREventBus? eventBus = null,
            ISignalRConnectionManager? connectionManager = null,
            global::BIManage.Core.Detection.UnmonitoredUserDetectionService? unmonitoredDetectionService = null,
            global::BIManage.Infrastructure.SignalR.PresenceCache? presenceCache = null,
            ModelAdminsSyncService? modelAdminsService = null)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            _logger = logger;
            _currentUsername = currentUsername;
            _projectName = projectName;
            _projectId = projectId;
            _modelAdminsService = modelAdminsService;
            _viewModel = new ModelActivitiesViewModel(sessionRepository, logger, eventBus, connectionManager, Dispatcher, unmonitoredDetectionService, presenceCache);
            _viewModel.SetModelInfo(modelGuid, modelName, centralModelPath, modelType);
            _viewModel.SetChatContext(currentUsername, projectId);
            DataContext = _viewModel;

            // Set header info
            HeaderModelName.Text = modelName;
            HeaderModelType.Text = modelType ?? "Unknown";
            if (!string.IsNullOrEmpty(projectName))
            {
                HeaderProjectName.Text = projectName;
                ProjectBadge.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EFF6FF"));
                ProjectIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2563EB"));
                HeaderProjectName.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E40AF"));
            }
            else
            {
                // User policy 28 May 2026: show em-dash when the backend has no project
                // name registered for this model — never derive a placeholder from Revit /
                // the model file name. "No Project" was the previous wording; "—" matches
                // the same placeholder used by the Session Information → Open Documents
                // Project column so the two surfaces look consistent.
                HeaderProjectName.Text = "—";
                ProjectBadge.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F1F5F9"));
                ProjectIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#94A3B8"));
                HeaderProjectName.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#94A3B8"));
            }
            HeaderModelPath.Text = centralModelPath ?? "";

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.LoadActiveUsersAsync();
                _viewModel.SubscribeSignalREvents();
                _viewModel.ChatMessages.CollectionChanged += OnChatMessagesChanged;
                UpdateUI();
                _ = LoadProjectAdminsAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load Model Activities: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Fetches the project admins from the API and displays them in the header.
        /// </summary>
        private async System.Threading.Tasks.Task LoadProjectAdminsAsync()
        {
            try
            {
                if (_modelAdminsService == null || string.IsNullOrWhiteSpace(_projectId))
                    return;

                var admins = await _modelAdminsService.GetProjectAdminsAsync(_projectId);
                if (admins == null || admins.Count == 0)
                    return;

                var names = admins
                    .Where(a => a.IsActive != false)
                    .Select(a => a.BestDisplayName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (names.Count == 0) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    HeaderProjectAdminNames.Text = string.Join(", ", names);
                    ProjectAdminPanel.Visibility = WpfVisibility.Visible;
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"LoadProjectAdminsAsync failed: {ex.Message}");
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _viewModel.ChatMessages.CollectionChanged -= OnChatMessagesChanged;
            _viewModel.UnsubscribeSignalREvents();
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            // Stop the 30s tick that keeps SessionDuration fresh — leaving it running
            // after close is a slow dispatcher-handle leak across many open/close cycles.
            _viewModel.StopDurationTimer();
        }

        private void OnChatMessagesChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                Dispatcher.BeginInvoke(new Action(() => ChatScrollViewer.ScrollToEnd()));
        }

        private void OnViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ModelActivitiesViewModel.ActiveSessionCount))
            {
                Dispatcher.Invoke(() => UpdateUI());
            }
        }

        #region Event Handlers

        private void ChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ActiveUserItem user)
            {
                OpenChatPanel(user.UsernameDisplay, $"Private chat with {user.UsernameDisplay}");
                _viewModel.SetChatScope("direct", user.UsernameDisplay);
            }
        }

        private void ChatModelAllButton_Click(object sender, RoutedEventArgs e)
        {
            OpenChatPanel("Model Chat", $"All users on {_viewModel.ModelName}");
            _viewModel.SetChatScope("model", null);
        }

        private void ChatProjectAllButton_Click(object sender, RoutedEventArgs e)
        {
            var projectDisplay = !string.IsNullOrEmpty(_projectName) ? _projectName : "this project";
            OpenChatPanel("Project Chat", $"All users on {projectDisplay}");
            _viewModel.SetChatScope("project", null);
        }

        private void CloseChatPanel_Click(object sender, RoutedEventArgs e)
        {
            CloseChatPanel();
        }

        private void SendMessage_Click(object sender, RoutedEventArgs e)
        {
            SendChatMessage();
        }

        private void ChatInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SendChatMessage();
                e.Handled = true;
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (ChatPanel.Visibility == WpfVisibility.Visible)
                    CloseChatPanel();
                else
                    Close();
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();

        #endregion

        #region Chat Panel

        private void OpenChatPanel(string title, string subtitle)
        {
            ChatHeaderTitle.Text = title;
            ChatHeaderSubtitle.Text = subtitle;

            // Clear previous messages when switching chats
            _viewModel.ChatMessages.Clear();

            // Show welcome message
            _viewModel.AddChatMessage("System", $"Chat connected. Messages are local to this session.", false);

            ChatPanel.Visibility = WpfVisibility.Visible;
            ChatColumnDef.Width = new GridLength(350);
            ChatInput.Focus();
        }

        private void CloseChatPanel()
        {
            ChatPanel.Visibility = WpfVisibility.Collapsed;
            ChatColumnDef.Width = new GridLength(0);
            _viewModel.SetChatScope(null, null);
        }

        private async void SendChatMessage()
        {
            var text = ChatInput.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            // Add locally immediately so user sees their message
            _viewModel.AddChatMessage(_currentUsername, text, isCurrentUser: true);
            ChatInput.Text = "";
            ChatInput.Focus();
            ChatScrollViewer.ScrollToEnd();

            // Send to other users via SignalR
            try
            {
                await _viewModel.SendChatMessageAsync(text);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[Chat] Failed to send: {ex.Message}");
            }
        }

        #endregion

        #region UI Helpers

        private void UpdateUI()
        {
            var count = _viewModel.ActiveSessionCount;
            HeaderSessionCount.Text = count == 0
                ? "No active sessions"
                : $"{count} active session{(count != 1 ? "s" : "")}";

            var userCount = _viewModel.ActiveUsers.Count;
            UserCountRun.Text = userCount > 0 ? $" ({userCount})" : "";

            StatusMessage.Text = $"{_viewModel.ModelName} - {userCount} active user{(userCount != 1 ? "s" : "")}";
        }

        private void ShowSuccessPopup(string title, string message)
        {
            SuccessTitle.Text = title;
            SuccessMessage.Text = message;
            SuccessPopup.Visibility = WpfVisibility.Visible;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (s, e) =>
            {
                SuccessPopup.Visibility = WpfVisibility.Collapsed;
                timer.Stop();
            };
            timer.Start();
        }

        #endregion
    }

    #region Converters

    public class BoolToVisibilityConverter : IValueConverter
    {
        public bool IsInverse { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool boolValue;

            if (value is bool b)
                boolValue = b;
            else if (value is int intVal)
                boolValue = intVal > 0;
            else
                boolValue = value != null;

            if (IsInverse)
                boolValue = !boolValue;

            return boolValue ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    #endregion
}
