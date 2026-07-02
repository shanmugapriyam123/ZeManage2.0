#nullable enable

using System.Windows;
using System.Windows.Input;

namespace BIManage.Views.Common
{
    /// <summary>
    /// Result of a <see cref="ZeConfirmDialog"/> interaction.
    /// </summary>
    public enum ZeConfirmResult
    {
        /// <summary>The user clicked the secondary button (Cancel by default), pressed Esc, or closed the window.</summary>
        Cancel,
        /// <summary>The user clicked the primary action button.</summary>
        Primary
    }

    /// <summary>
    /// Zestine-themed confirmation dialog — drop-in replacement for Revit's
    /// <c>TaskDialog</c> when a Yes/No or Action/Cancel decision is needed.
    /// Use <see cref="ZeMessageBox"/> for single-OK info dialogs.
    ///
    /// Default labels: primary="OK", secondary="Cancel". Override either via the
    /// <see cref="ConfirmShow"/> parameters. The dialog returns
    /// <see cref="ZeConfirmResult.Cancel"/> for Esc / window-close so callers don't
    /// need to special-case those paths.
    /// </summary>
    public partial class ZeConfirmDialog : Window
    {
        private ZeConfirmResult _result = ZeConfirmResult.Cancel;

        public ZeConfirmDialog()
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);
        }

        /// <summary>
        /// Show a modal confirmation dialog and return which button the user clicked.
        /// </summary>
        /// <param name="title">Header title (shown next to the BIManage logo).</param>
        /// <param name="message">Body message — supports multiline, wraps automatically.</param>
        /// <param name="primaryText">Primary action button label (e.g. "Sign in again").</param>
        /// <param name="secondaryText">Secondary / cancel button label (e.g. "Not now").</param>
        /// <param name="headline">Optional bold sub-title above the message — used for "headline + body" layouts. Leave null for plain message.</param>
        /// <param name="ownerHandle">Optional owner-window handle so the dialog centers correctly when the host is Revit (which is not a WPF window).</param>
        public static ZeConfirmResult ConfirmShow(
            string title,
            string message,
            string primaryText = "OK",
            string secondaryText = "Cancel",
            string? headline = null,
            System.IntPtr? ownerHandle = null)
        {
            var dialog = new ZeConfirmDialog();
            dialog.TitleText.Text = title ?? string.Empty;
            dialog.MessageText.Text = message ?? string.Empty;
            dialog.PrimaryButton.Content = primaryText;
            dialog.SecondaryButton.Content = secondaryText;
            if (!string.IsNullOrWhiteSpace(headline))
            {
                dialog.HeadlineText.Text = headline;
                dialog.HeadlineText.Visibility = System.Windows.Visibility.Visible;
            }

            // Use the supplied handle if provided (Revit is not a WPF window so
            // Application.Current.MainWindow won't help center us). Fall back to
            // the current process's main window handle as a best-effort default.
            var handle = ownerHandle ?? System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = handle };

            dialog.ShowDialog();
            return dialog._result;
        }

        private void Primary_Click(object sender, RoutedEventArgs e)
        {
            _result = ZeConfirmResult.Primary;
            DialogResult = true;
            Close();
        }

        private void Secondary_Click(object sender, RoutedEventArgs e)
        {
            _result = ZeConfirmResult.Cancel;
            DialogResult = false;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _result = ZeConfirmResult.Cancel;
            DialogResult = false;
            Close();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }
    }
}
