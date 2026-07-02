using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using BIManage.Core.Rules.Models;
using BIManage.ViewModels.Protection;


namespace BIManage.Views.Protection
{
    /// <summary>
    /// Compact guide mode dialog for command protection with image support
    /// </summary>
    public partial class GuideDialog : Window
    {
        private readonly GuideDialogViewModel _viewModel;

        public bool UserAllowed => _viewModel?.UserAllowed ?? false;
        public string UserComment => _viewModel?.UserComment ?? string.Empty;

        /// <summary>OTP code entered via the OTP dialog (Protect mode only)</summary>
        public string OtpCode { get; private set; }

        /// <summary>Override method used: "OTP" or null</summary>
        public string OverrideMethod { get; private set; }

        /// <summary>True if the user successfully provided an override (OTP)</summary>
        public bool OverrideAllowed { get; private set; }

        /// <summary>
        /// Standard constructor for basic guide dialog
        /// </summary>
        public GuideDialog(RuleEvaluationResult evaluationResult, ICollection<ElementId> affectedElements)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            _viewModel = new GuideDialogViewModel();
            _viewModel.Initialize(evaluationResult, affectedElements);

            DataContext = _viewModel;
        }

        /// <summary>
        /// Extended constructor with command name and optional images
        /// </summary>
        public GuideDialog(
            RuleEvaluationResult evaluationResult,
            ICollection<ElementId> affectedElements,
            string commandName,
            string commandDescription = null,
            string beforeImagePath = null,
            string afterImagePath = null)
        {
            InitializeComponent();

            _viewModel = new GuideDialogViewModel();
            _viewModel.Initialize(evaluationResult, affectedElements, commandName, commandDescription, beforeImagePath, afterImagePath);

            DataContext = _viewModel;
        }

        /// <summary>
        /// Set the before screenshot image directly
        /// </summary>
        public void SetBeforeImage(BitmapImage image)
        {
            _viewModel.SetBeforeImage(image);
        }

        /// <summary>
        /// Set the after screenshot image directly
        /// </summary>
        public void SetAfterImage(BitmapImage image)
        {
            _viewModel.SetAfterImage(image);
        }

        /// <summary>
        /// Set the action type explicitly
        /// </summary>
        public void SetActionType(ConfirmationActionType actionType)
        {
            _viewModel.ActionType = actionType;
        }

        /// <summary>
        /// Set the single preview image directly
        /// </summary>
        public void SetPreviewImage(BitmapImage image)
        {
            _viewModel.SetPreviewImage(image);
        }

        /// <summary>
        /// Set custom button text
        /// </summary>
        public void SetButtonText(string proceedText, string cancelText = null)
        {
            if (!string.IsNullOrEmpty(proceedText))
                _viewModel.ProceedButtonText = proceedText;
            if (!string.IsNullOrEmpty(cancelText))
                _viewModel.CancelButtonText = cancelText;
        }

        private void OnProceedClick(object sender, RoutedEventArgs e)
        {
            _viewModel.OnProceed();
            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            _viewModel.OnCancel();
            DialogResult = false;
            Close();
        }

        private void CommentTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ProceedButton.IsEnabled)
            {
                e.Handled = true;
                _viewModel.OnProceed();
                DialogResult = true;
                Close();
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();

        /// <summary>
        /// Configure dialog for Protect mode (red color scheme, OTP button visible, Confirm hidden)
        /// </summary>
        public void SetProtectMode(ProtectionType protectionType, string contentTitle)
        {
            _viewModel.DialogMode = ProtectionDialogMode.Protect;
            _viewModel.ProtectionTypeValue = protectionType;
            _viewModel.ContentTitle = contentTitle;

            // Update window title based on protection type
            Title = protectionType switch
            {
                ProtectionType.Command => "Command Restriction",
                ProtectionType.Event => "Event Restriction",
                ProtectionType.Rule => "Rule Management",
                ProtectionType.Pin => "Pin Protection",
                _ => "Protection"
            };

            if (ContentTitleText != null)
            {
                ContentTitleText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x39, 0x35)); // Red title
                ContentTitleText.Text = contentTitle;
                ContentTitleText.Visibility = string.IsNullOrWhiteSpace(contentTitle) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            }

            // Show OTP button, hide Confirm button
            if (OtpButton != null) OtpButton.Visibility = System.Windows.Visibility.Visible;
            if (ProceedButton != null) ProceedButton.Visibility = System.Windows.Visibility.Collapsed;
        }

        /// <summary>
        /// Configure dialog for Notify mode (green color scheme, read-only — no action required)
        /// </summary>
        public void SetNotifyMode(ProtectionType protectionType, string contentTitle)
        {
            _viewModel.DialogMode = ProtectionDialogMode.Notify;
            _viewModel.ProtectionTypeValue = protectionType;
            _viewModel.ContentTitle = contentTitle;

            Title = protectionType switch
            {
                ProtectionType.Command => "Command Notification",
                ProtectionType.Event => "Event Notification",
                ProtectionType.Rule => "Rule Notification",
                ProtectionType.Pin => "Pin Notification",
                _ => "Notification"
            };

            if (ContentTitleText != null)
            {
                ContentTitleText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x7A, 0x3B));
                ContentTitleText.Text = contentTitle;
                ContentTitleText.Visibility = string.IsNullOrWhiteSpace(contentTitle) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            }

            if (OtpButton != null) OtpButton.Visibility = System.Windows.Visibility.Collapsed;
            if (ProceedButton != null) ProceedButton.Visibility = System.Windows.Visibility.Visible;
        }

        /// <summary>
        /// Configure dialog for Assist mode (blue color scheme, default)
        /// </summary>
        public void SetAssistMode(ProtectionType protectionType, string contentTitle)
        {
            _viewModel.DialogMode = ProtectionDialogMode.Assist;
            _viewModel.ProtectionTypeValue = protectionType;
            _viewModel.ContentTitle = contentTitle;

            // Update window title based on protection type
            Title = protectionType switch
            {
                ProtectionType.Command => "Command Restriction",
                ProtectionType.Event => "Event Restriction",
                ProtectionType.Rule => "Rule Management",
                ProtectionType.Pin => "Pin Protection",
                _ => "Protection"
            };

            // Set content title
            if (ContentTitleText != null)
            {
                ContentTitleText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x52, 0x76)); // Blue title
                ContentTitleText.Text = contentTitle;
                ContentTitleText.Visibility = string.IsNullOrWhiteSpace(contentTitle) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            }

            // Ensure Assist defaults: OTP hidden, Confirm visible
            if (OtpButton != null) OtpButton.Visibility = System.Windows.Visibility.Collapsed;
            if (ProceedButton != null) ProceedButton.Visibility = System.Windows.Visibility.Visible;
        }

        /// <summary>
        /// OTP entry click handler for Protect mode
        /// </summary>
        private void OnEnterOtpClick(object sender, RoutedEventArgs e)
        {
            // Inherit the parent dialog's title (Command Protection / Event Restriction /
            // Rule Management / Pin Protection) so the OTP popup matches the context.
            var otpDialog = new global::BIManageRevit.BIManage.Views.Bindings.OtpInputDialog(Title);
            try
            {
                new WindowInteropHelper(otpDialog) { Owner = new WindowInteropHelper(this).Handle };
            }
            catch { /* non-critical */ }

            var result = otpDialog.ShowDialog();
            if (result == true && !string.IsNullOrWhiteSpace(otpDialog.OtpCode))
            {
                OtpCode = otpDialog.OtpCode;
                OverrideMethod = "OTP";
                OverrideAllowed = true;
                // Also capture user comment before closing
                _viewModel.OnProceed();
                DialogResult = true;
                Close();
            }
        }
    }
}
