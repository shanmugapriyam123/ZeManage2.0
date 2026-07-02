using System.Windows;
using System.Windows.Input;


namespace BIManageRevit.BIManage.Views.Bindings
{
    /// <summary>
    /// Interaction logic for PinSelectionDialog.xaml
    /// </summary>
    public partial class PinSelectionDialog : Window
    {
        public bool IsProtectedPin { get; private set; }

        public PinSelectionDialog()
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);
        }

        private void NormalPinButton_Click(object sender, RoutedEventArgs e)
        {
            IsProtectedPin = false;
            DialogResult = true;
            Close();
        }

        private void ProtectedPinButton_Click(object sender, RoutedEventArgs e)
        {
            IsProtectedPin = true;
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
