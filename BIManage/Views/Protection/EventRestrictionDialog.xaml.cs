using System.Windows;
using System.Windows.Input;


namespace BIManageRevit.BIManage.Views.Protection
{
    public enum EventRestrictionAction
    {
        Cancel,
        EnterOtp,
        AdminOverride
    }

    public partial class EventRestrictionDialog : Window
    {
        public EventRestrictionAction UserAction { get; private set; } = EventRestrictionAction.Cancel;

        /// <summary>
        /// Comment entered by the user when RequireComment is enabled. Empty string if the
        /// dialog wasn't configured to collect comments.
        /// </summary>
        public string UserComment { get; private set; } = string.Empty;

        private bool _requireComment;

        public EventRestrictionDialog(string protectionName, string message, bool showAdminOverride = false, bool requireComment = false)
        {
            InitializeComponent();

            HeaderTitle.Text = "Action Restricted";
            HeaderSubtitle.Text = protectionName;
            Title = $"ZeManage - {protectionName}";
            ProtectionMessage.Text = message;

            if (showAdminOverride)
                AdminOverrideButton.Visibility = System.Windows.Visibility.Visible;

            _requireComment = requireComment;
            CommentLabel.Text = requireComment ? "Reason / Comment (required)" : "Reason / Comment (optional)";
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { UserAction = EventRestrictionAction.Cancel; Close(); }
        }

        private bool TryCaptureComment()
        {
            var text = CommentBox?.Text?.Trim() ?? string.Empty;
            if (_requireComment && string.IsNullOrWhiteSpace(text))
            {
                System.Windows.MessageBox.Show(
                    "A reason / comment is required before proceeding.",
                    "Comment Required",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                CommentBox?.Focus();
                return false;
            }
            UserComment = text;
            return true;
        }

        private void EnterOtp_Click(object sender, RoutedEventArgs e)
        {
            if (!TryCaptureComment()) return;
            UserAction = EventRestrictionAction.EnterOtp;
            Close();
        }

        private void AdminOverride_Click(object sender, RoutedEventArgs e)
        {
            if (!TryCaptureComment()) return;
            UserAction = EventRestrictionAction.AdminOverride;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            // Cancel always allowed without comment.
            UserAction = EventRestrictionAction.Cancel;
            UserComment = CommentBox?.Text?.Trim() ?? string.Empty;
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            UserAction = EventRestrictionAction.Cancel;
            UserComment = CommentBox?.Text?.Trim() ?? string.Empty;
            Close();
        }
    }
}
