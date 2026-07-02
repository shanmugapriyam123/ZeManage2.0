using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;


namespace BIManageRevit.BIManage.Views.Bindings
{
    /// <summary>
    /// Interaction logic for UnpinAuthorizationDialog.xaml
    /// </summary>
    public partial class UnpinAuthorizationDialog : Window
    {
        public int ElementCount { get; }
        public string ElementList { get; }
        public string UserName { get; }
        public string? OtpCode { get; private set; }

        /// <summary>
        /// User comment entered in the dialog
        /// </summary>
        public string? UserComment => string.IsNullOrWhiteSpace(CommentTextBox?.Text) ? null : CommentTextBox.Text.Trim();

        public UnpinAuthorizationDialog(int elementCount, string elementList, string userName, string? commandName = null, string? pinMode = null, string? contextLabel = null)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            ElementCount = elementCount;
            ElementList = elementList;
            UserName = userName;

            // Show command name in alert banner and command card
            if (!string.IsNullOrWhiteSpace(commandName))
            {
                AlertCommandName.Text = commandName;
                CommandNameText.Text = commandName;
                CommandNameSection.Visibility = System.Windows.Visibility.Visible;

                // Switch to command protection mode titles
                Title = "Command Restriction";
                TitleText.Text = "Protected Command - Authorization Required";
                PinModeLabel.Text = "Protected Command Mode: ";
            }

            // contextLabel overrides the Title so callers from Event Restriction or
            // Rule Protection contexts see the correct header in this dialog AND in
            // the OTP popup it launches (the OTP dialog inherits this.Title).
            //   • "Command Restriction" — from Command Protection / Move / Delete bindings
            //   • "Event Restriction"   — from EventRestriction bindings
            //   • "Rule Protection"     — from RuleCommandInterceptor
            // Pass null to keep the XAML default ("Pin Protection").
            if (!string.IsNullOrWhiteSpace(contextLabel))
            {
                Title = contextLabel;
            }

            // Show pin mode in the Protected Pin Mode card
            if (!string.IsNullOrWhiteSpace(pinMode))
            {
                PinModeText.Text = pinMode;
            }

            // Set DataContext to this instance for binding
            DataContext = this;
        }

        /// <summary>
        /// Set a preview image to display in the dialog
        /// </summary>
        public void SetPreviewImage(BitmapImage image)
        {
            if (image != null)
            {
                PreviewImage.Source = image;
                ImageSection.Visibility = System.Windows.Visibility.Visible;
            }
            else
            {
                ImageSection.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private void CommentTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter && EnterOtpButton.IsEnabled)
            {
                e.Handled = true;
                EnterOtpButton_Click(EnterOtpButton, new RoutedEventArgs());
            }
        }

        private void CommentTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var hasComment = !string.IsNullOrWhiteSpace(CommentTextBox.Text);
            EnterOtpButton.IsEnabled = hasComment;
            CommentValidation.Visibility = hasComment ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        }

        private void EnterOtpButton_Click(object sender, RoutedEventArgs e)
        {
            // Show OTP input dialog — inherit our title (e.g. "Command Protection" or
            // "Pin Protection") so the OTP popup matches the calling context.
            var otpDialog = new OtpInputDialog(Title);
            global::BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(otpDialog);
            bool? result = otpDialog.ShowDialog();

            if (result == true && !string.IsNullOrWhiteSpace(otpDialog.OtpCode))
            {
                OtpCode = otpDialog.OtpCode.Trim();
                DialogResult = true;
                Close();
            }
            // If empty or cancelled, do nothing (stay on dialog)
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            OtpCode = null;
            DialogResult = false;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                OtpCode = null;
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
