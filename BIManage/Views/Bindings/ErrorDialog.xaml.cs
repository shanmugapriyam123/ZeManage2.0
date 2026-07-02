using System.Windows;
using System.Windows.Input;


namespace BIManageRevit.BIManage.Views.Bindings
{
    /// <summary>
    /// Interaction logic for ErrorDialog.xaml
    /// </summary>
    public partial class ErrorDialog : Window
    {
        public string HeaderText { get; }
        public string Message { get; }
        public string Details { get; }
        public System.Windows.Visibility DetailsVisibility => string.IsNullOrEmpty(Details) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        /// <summary>
        /// Creates the error dialog. The window title bar uses <paramref name="title"/> too,
        /// overriding the XAML default. To set a context-specific window title independently,
        /// use the four-argument constructor.
        /// </summary>
        public ErrorDialog(string title, string message, string details = "")
            : this(title, title, message, details)
        {
        }

        /// <summary>
        /// Creates the error dialog with an explicit window title (for the title bar) and a
        /// header (shown above the message). Useful when the surrounding context is "Command
        /// Protection" / "Event Restriction" / "Rule Management" / "Pin Protection" but the
        /// header is something specific like "Invalid OTP".
        /// </summary>
        public ErrorDialog(string windowTitle, string headerText, string message, string details = "")
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            if (!string.IsNullOrWhiteSpace(windowTitle))
                Title = windowTitle;
            HeaderText = headerText ?? string.Empty;
            Message = message;
            Details = details;

            // Set DataContext for binding
            DataContext = this;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
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
