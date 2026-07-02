using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BIManage.Core.Protection;
using BIManage.ViewModels.Protection;


namespace BIManage.Views.Protection
{
    /// <summary>
    /// Password configuration dialog
    /// </summary>
    public partial class PasswordConfigDialog : Window
    {
        private readonly PasswordConfigViewModel _viewModel;

        public bool Success => _viewModel?.Success ?? false;

        public PasswordConfigDialog(PasswordManager passwordManager)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            _viewModel = new PasswordConfigViewModel(passwordManager);
            DataContext = _viewModel;
        }

        private void OnCurrentPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox passwordBox)
            {
                _viewModel.CurrentPassword = passwordBox.Password;
            }
        }

        private void OnNewPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox passwordBox)
            {
                _viewModel.NewPassword = passwordBox.Password;
            }
        }

        private void OnConfirmPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox passwordBox)
            {
                _viewModel.ConfirmPassword = passwordBox.Password;
            }
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            _viewModel.OnSave();
            
            // Close dialog after successful save (with a small delay to show success message)
            if (_viewModel.Success)
            {
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = System.TimeSpan.FromSeconds(1.5)
                };
                timer.Tick += (s, args) =>
                {
                    timer.Stop();
                    DialogResult = true;
                    Close();
                };
                timer.Start();
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            _viewModel.OnCancel();
            DialogResult = false;
            Close();
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

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            _viewModel.OnCancel();
            DialogResult = false;
            Close();
        }
    }
}
