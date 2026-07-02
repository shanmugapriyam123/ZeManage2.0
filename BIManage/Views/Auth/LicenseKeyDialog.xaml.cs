using System.Windows;
using System.Windows.Input;


namespace BIManageRevit.BIManage.Views.Auth
{
    public partial class LicenseKeyDialog : Window
    {
        public string? LicenseKey { get; private set; }

        public LicenseKeyDialog(string machineId)
        {
            InitializeComponent();
            MachineIdText.Text = machineId;
            LicenseKeyInput.Focus();
        }

        private void LicenseKeyInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ActivateButton.IsEnabled = !string.IsNullOrWhiteSpace(LicenseKeyInput.Text);
        }

        private void Activate_Click(object sender, RoutedEventArgs e)
        {
            LicenseKey = LicenseKeyInput.Text?.Trim();
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            LicenseKey = null;
            DialogResult = false;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                LicenseKey = null;
                DialogResult = false;
                Close();
            }
            else if (e.Key == Key.Enter && ActivateButton.IsEnabled)
            {
                Activate_Click(sender, e);
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            LicenseKey = null;
            DialogResult = false;
            Close();
        }
    }
}
