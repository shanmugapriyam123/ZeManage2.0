using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BIManage.Models.AI;
using BIManage.ViewModels.AI;
using BIManage.Infrastructure.Logging;


namespace BIManageRevit.BIManage.Views.AI
{
    public sealed partial class ZestAiDialog : Window
    {
        private readonly ZestAiViewModel _viewModel;
        private readonly ILogger? _logger;

        private static readonly System.Windows.Media.SolidColorBrush FocusBorderBrush =
            new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A3C8E8"));
        private static readonly System.Windows.Media.SolidColorBrush DefaultBorderBrush =
            new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DDE4EE"));
        private static readonly System.Windows.Media.SolidColorBrush FocusBackground =
            new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFF"));
        private static readonly System.Windows.Media.SolidColorBrush DefaultBackground =
            new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFF"));

        public ZestAiDialog(ZestAiViewModel viewModel, ILogger? logger = null)
        {
            _viewModel = viewModel;
            _logger = logger;
            DataContext = viewModel;
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            Closing += (s, e) =>
            {
                try { _viewModel.SaveSessionToJson(); }
                catch (Exception ex) { _logger?.LogError("Failed to save AI session", ex); }
                _viewModel.EndChatSessionAsync();
            };

            Loaded += (s, e) =>
            {
                MessageScrollViewer.ScrollToTop();
                InputBox.Focus();
            };

            _viewModel.Messages.CollectionChanged += OnMessagesChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Hide the "New reply" pill automatically once the user scrolls to (or past) the
            // bottom — no point showing it if they're already looking at the latest message.
            MessageScrollViewer.ScrollChanged += (_, args) =>
            {
                if (NewReplyButton.Visibility != System.Windows.Visibility.Visible) return;
                var atBottom = MessageScrollViewer.ScrollableHeight - MessageScrollViewer.VerticalOffset < 40;
                if (atBottom)
                    NewReplyButton.Visibility = System.Windows.Visibility.Collapsed;
            };
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Final scroll when response completes, then hands control back to user.
            // Returning focus to InputBox here matches Slack/Teams/ChatGPT behaviour: the user
            // never has to click back into the textbox to continue the conversation. The textbox
            // loses focus during IsSending because it's bound to IsEnabled=!IsSending; WPF moves
            // keyboard focus off any control that gets disabled, and doesn't restore it on
            // re-enable. Dispatcher.BeginInvoke is used so the focus call runs AFTER WPF has
            // finished re-enabling the control on this frame.
            if (e.PropertyName == nameof(ZestAiViewModel.IsSending) && !_viewModel.IsSending)
            {
                ScrollToBottom();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    InputBox.Focus();
                    Keyboard.Focus(InputBox);
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Hide welcome panel once conversation starts (more than the initial greeting)
            if (_viewModel.Messages.Count > 1)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    WelcomePanel.Visibility = System.Windows.Visibility.Collapsed;
                }));
            }

            // Scroll handling on add:
            //   • If user is at the bottom: stick to bottom (auto-scroll).
            //   • If user has scrolled up AND the new message is an AI reply: surface the
            //     "New reply" pill so they know to look. We don't show it for user-sent
            //     messages because the user just typed those — they know about them.
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                var addedIsAi = e.NewItems != null
                    && e.NewItems.Count > 0
                    && e.NewItems[0] is global::BIManage.Models.AI.ChatMessage cm
                    && !cm.IsUser;
                ScrollToBottom(notifyOnMissedAiReply: addedIsAi);
            }
        }

        private void ScrollToBottom(bool notifyOnMissedAiReply = false)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Sticky-bottom behaviour: only auto-scroll if the user is already near the
                // bottom. If they've scrolled up to re-read something, leave them alone — the
                // "content jumping" tester complaint was caused by us always yanking the view
                // to the latest message during streaming.
                //
                // Threshold of 80 px tolerates small layout shifts (token-by-token streaming
                // grows the last bubble by a few px per chunk) without losing stickiness.
                var atBottom = MessageScrollViewer.ScrollableHeight - MessageScrollViewer.VerticalOffset < 80;
                if (atBottom)
                {
                    MessageScrollViewer.ScrollToEnd();
                }
                else if (notifyOnMissedAiReply)
                {
                    // User is scrolled up and a reply just arrived they can't see. Show pill.
                    NewReplyButton.Visibility = System.Windows.Visibility.Visible;
                }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void NewReplyButton_Click(object sender, RoutedEventArgs e)
        {
            MessageScrollViewer.ScrollToEnd();
            NewReplyButton.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void SuggestionChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string prompt)
            {
                _viewModel.UserMessage = prompt;
                if (_viewModel.SendMessageCommand.CanExecute(null))
                {
                    _viewModel.SendMessageCommand.Execute(null);
                }
            }
        }

        private async void NewChat_Click(object sender, RoutedEventArgs e)
        {
            // Close the in-progress session (so it shows up in the history panel from
            // now on) and start a fresh one. Without this the previous conversation
            // is invisible in History until the dialog is closed and reopened —
            // tester complaint logged 2026-05-25.
            await _viewModel.StartNewChatSessionAsync();

            _viewModel.Messages.Clear();
            _viewModel.Messages.Add(new global::BIManage.Models.AI.ChatMessage
            {
                Text = "Hello! I'm your Zesto Assistant. Ask me anything about Revit, BIM workflows, or your current project!",
                IsUser = false,
                Timestamp = DateTime.Now
            });
            _viewModel.RefreshSuggestionChips();
            WelcomePanel.Visibility = System.Windows.Visibility.Visible;
        }

        private async void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.RefreshRecentSessionsAsync();
            UpdateHistoryEmptyState();
            HistoryPopup.IsOpen = true;
        }

        private async void HistoryItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string sessionId
                && !string.IsNullOrEmpty(sessionId))
            {
                HistoryPopup.IsOpen = false;
                await _viewModel.ResumeSessionAsync(sessionId);
                // Hide the welcome panel since we now have past conversation content.
                WelcomePanel.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private async void HistoryItemDelete_Click(object sender, RoutedEventArgs e)
        {
            // Per-row delete (×). Mark handled so the click doesn't bubble up and resume
            // the very session we're trying to delete.
            e.Handled = true;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string sessionId
                && !string.IsNullOrEmpty(sessionId))
            {
                await _viewModel.DeleteSessionAsync(sessionId);
                await _viewModel.RefreshRecentSessionsAsync();
                UpdateHistoryEmptyState();
            }
        }

        private async void ClearAllHistory_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(
                "Delete ALL chat history for this model? This cannot be undone.",
                "Clear chat history",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);
            if (result != System.Windows.MessageBoxResult.Yes) return;

            await _viewModel.ClearAllHistoryForCurrentModelAsync();
            await _viewModel.RefreshRecentSessionsAsync();
            UpdateHistoryEmptyState();
        }

        private void UpdateHistoryEmptyState()
        {
            var hasItems = _viewModel.RecentSessions.Count > 0;
            HistoryEmptyText.Visibility = hasItems
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
            ClearAllRow.Visibility = hasItems
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                if (e.ClickCount == 2)
                    ToggleMaximize();
                else
                    DragMove();
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }

        private void FollowUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is string question)
            {
                _viewModel.UserMessage = question;
                InputBox.Focus();
                InputBox.CaretIndex = InputBox.Text.Length;
            }
        }

        private void InputBox_GotFocus(object sender, RoutedEventArgs e)
        {
            InputBorder.BorderBrush = FocusBorderBrush;
            InputBorder.Background = FocusBackground;
        }

        private void InputBox_LostFocus(object sender, RoutedEventArgs e)
        {
            InputBorder.BorderBrush = DefaultBorderBrush;
            InputBorder.Background = DefaultBackground;
        }

        private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            PlaceholderText.Visibility = string.IsNullOrEmpty(InputBox.Text)
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }
    }
}
