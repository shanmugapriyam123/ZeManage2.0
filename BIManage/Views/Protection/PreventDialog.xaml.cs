using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using BIManage.Core.Protection;
using BIManage.Core.Rules.Models;
using BIManage.ViewModels.Protection;


namespace BIManage.Views.Protection
{
    /// <summary>
    /// Prevent mode dialog with password protection
    /// </summary>
    public partial class PreventDialog : Window
    {
        private readonly PreventDialogViewModel _viewModel;

        public bool OverrideAllowed => _viewModel?.OverrideAllowed ?? false;
        public string OverrideMethod => _viewModel?.OverrideMethod;
        public string OtpCode => _viewModel?.OtpCode;

        public PreventDialog(RuleEvaluationResult evaluationResult, ICollection<ElementId> affectedElements, PasswordManager passwordManager)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            _viewModel = new PreventDialogViewModel(passwordManager);
            _viewModel.Initialize(evaluationResult, affectedElements);
            
            DataContext = _viewModel;
        }

        private void OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox passwordBox)
            {
                _viewModel.Password = passwordBox.Password;
            }
        }

        private void OnOverrideClick(object sender, RoutedEventArgs e)
        {
            _viewModel.OnOverride();
            
            if (_viewModel.OverrideAllowed)
            {
                DialogResult = true;
                Close();
            }
        }

        private void OnEnterOtpClick(object sender, RoutedEventArgs e)
        {
            // PreventDialog is invoked from RuleCommandInterceptor (Rule Protection),
            // so the OTP popup launched here must show "Rule Protection" as its header.
            var otpDialog = new BIManageRevit.BIManage.Views.Bindings.OtpInputDialog("Rule Protection");

            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(otpDialog)
                {
                    Owner = new System.Windows.Interop.WindowInteropHelper(this).Handle
                };
            }
            catch { /* non-critical */ }

            bool? result = otpDialog.ShowDialog();

            if (result == true && !string.IsNullOrWhiteSpace(otpDialog.OtpCode))
            {
                _viewModel.OtpCode = otpDialog.OtpCode.Trim();
                _viewModel.OverrideMethod = "OTP";
                DialogResult = true;
                Close();
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
