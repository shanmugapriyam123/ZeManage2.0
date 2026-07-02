using System.Windows;
using BIManage.ViewModels.Protection;


namespace BIManage.Views.Protection
{
    /// <summary>
    /// Base class for all protection dialogs
    /// </summary>
    public partial class ProtectionDialogBase : Window
    {
        protected ProtectionDialogBase()
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);
        }

        protected void SetViewModel(ProtectionDialogBaseViewModel viewModel)
        {
            DataContext = viewModel;

            if (viewModel != null)
            {
                TitleText.Text = viewModel.DialogTitle;
                MessageText.Text = viewModel.DialogMessage;
                ElementSummaryText.Text = viewModel.ElementSummary;
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
    }
}
