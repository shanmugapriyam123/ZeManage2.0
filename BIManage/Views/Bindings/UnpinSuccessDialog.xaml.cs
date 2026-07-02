using System.Windows;
using System.Windows.Input;


namespace BIManageRevit.BIManage.Views.Bindings
{
    /// <summary>
    /// Interaction logic for UnpinSuccessDialog.xaml
    /// </summary>
    public partial class UnpinSuccessDialog : Window
    {
        public int ElementCount { get; }
        public string Message { get; }

        public UnpinSuccessDialog(int elementCount, string message)
            : this(elementCount, message, null, null)
        {
        }

        /// <summary>
        /// Creates the success dialog with optional context-specific window title and header.
        /// Pass null to keep XAML defaults ("Pin Protection" / "Unpin Successful").
        /// </summary>
        public UnpinSuccessDialog(int elementCount, string message, string? windowTitle, string? headerText)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            ElementCount = elementCount;
            Message = message;

            if (!string.IsNullOrWhiteSpace(windowTitle))
                Title = windowTitle;
            if (!string.IsNullOrWhiteSpace(headerText) && HeaderText != null)
                HeaderText.Text = headerText;

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
