using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;


namespace BIManageRevit.BIManage.Views.Bindings
{
    /// <summary>
    /// Interaction logic for OtpInputDialog.xaml
    /// </summary>
    public partial class OtpInputDialog : Window
    {
        public string OtpCode { get; private set; }

        public OtpInputDialog() : this(null)
        {
        }

        /// <summary>
        /// Creates the OTP dialog with a custom window title (e.g. "Command Protection",
        /// "Pin Protection", "Event Restriction"). Pass null to keep the XAML default.
        /// </summary>
        public OtpInputDialog(string? windowTitle)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);
            // Window sizing/positioning is now driven by XAML (SizeToContent="WidthAndHeight"
            // + WindowStartupLocation="CenterScreen"). The previous code that explicitly
            // sized to SystemParameters.VirtualScreen* fought against the XAML and broke
            // Z-ordering when other apps (Teams, browsers, VS Code) were already in front.
            Loaded += (s, e) =>
            {
                // Bring the dialog to the front of its OWNER (Revit) — but not above
                // other applications. Callers always parent the dialog to Revit's
                // main HWND via RevitWindowHelper.SetOwner before ShowDialog, so the
                // owner relationship is what keeps the dialog above Revit. We
                // intentionally do NOT set Topmost = true here: that flag floats the
                // dialog over every window in the system, including unrelated apps
                // like Visual Studio / browsers, which is what the user reported.
                Activate();
                Focus();
                Otp1.Focus();
            };
            OtpCode = string.Empty;

            if (!string.IsNullOrWhiteSpace(windowTitle))
                Title = windowTitle;
        }

        private void OtpBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null) return;

            // Hide error when user starts typing
            ErrorBorder.Visibility = System.Windows.Visibility.Collapsed;

            // Move to next box if digit entered
            if (!string.IsNullOrEmpty(textBox.Text))
            {
                if (textBox == Otp1) Otp2.Focus();
                else if (textBox == Otp2) Otp3.Focus();
                else if (textBox == Otp3) Otp4.Focus();
                else if (textBox == Otp4) Otp5.Focus();
                else if (textBox == Otp5) Otp6.Focus();
                else if (textBox == Otp6) SubmitButton_Click(null, null);
            }
        }

        private void OtpBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null) return;

            // Handle backspace - move to previous box
            if (e.Key == Key.Back && string.IsNullOrEmpty(textBox.Text))
            {
                if (textBox == Otp6) { Otp5.Focus(); Otp5.SelectAll(); }
                else if (textBox == Otp5) { Otp4.Focus(); Otp4.SelectAll(); }
                else if (textBox == Otp4) { Otp3.Focus(); Otp3.SelectAll(); }
                else if (textBox == Otp3) { Otp2.Focus(); Otp2.SelectAll(); }
                else if (textBox == Otp2) { Otp1.Focus(); Otp1.SelectAll(); }
                e.Handled = true;
            }
            // Handle paste
            else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                HandlePaste();
                e.Handled = true;
            }
        }

        private void HandlePaste()
        {
            if (Clipboard.ContainsText())
            {
                string pastedText = Clipboard.GetText().Trim().ToUpper();
                // Remove any non-alphanumeric characters
                pastedText = new string(System.Array.FindAll(pastedText.ToCharArray(), c => char.IsLetterOrDigit(c)));

                if (pastedText.Length >= 6)
                {
                    Otp1.Text = pastedText[0].ToString();
                    Otp2.Text = pastedText[1].ToString();
                    Otp3.Text = pastedText[2].ToString();
                    Otp4.Text = pastedText[3].ToString();
                    Otp5.Text = pastedText[4].ToString();
                    Otp6.Text = pastedText[5].ToString();
                    Otp6.Focus();
                }
            }
        }

        private void SubmitButton_Click(object sender, RoutedEventArgs e)
        {
            // Collect OTP from all 6 boxes
            OtpCode = $"{Otp1.Text}{Otp2.Text}{Otp3.Text}{Otp4.Text}{Otp5.Text}{Otp6.Text}";

            // Validate: must be 6 characters
            if (OtpCode.Length != 6)
            {
                ShowError("Please enter all 6 digits of the OTP code.");
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            OtpCode = string.Empty;
            DialogResult = false;
            Close();
        }

        public void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorBorder.Visibility = System.Windows.Visibility.Visible;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                OtpCode = string.Empty;
                DialogResult = false;
                Close();
            }
        }

        // Lets the user drag the centered dialog box (the surrounding window is maximized,
        // so OS chrome can't be used). No-op when maximized — DragMove throws.
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left && WindowState == System.Windows.WindowState.Normal)
            {
                try { DragMove(); } catch { }
            }
        }

        // X button in the custom title bar — same semantics as Escape (cancel).
        private void TitleBarClose_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            OtpCode = string.Empty;
            DialogResult = false;
            Close();
        }
    }
}
