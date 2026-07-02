using System;
using System.Windows.Input;
using BIManage.Common.Helpers;

namespace BIManageRevit.BIManage.ViewModels.Bindings
{
    /// <summary>
    /// ViewModel for OTP Input Dialog
    /// </summary>
    public class OtpInputViewModel : ViewModelBase
    {
        private string _otpCode;
        private string _validationError;
        private System.Windows.Visibility _showError;

        public string OtpCode
        {
            get => _otpCode;
            set => SetProperty(ref _otpCode, value);
        }

        public string ValidationError
        {
            get => _validationError;
            set => SetProperty(ref _validationError, value);
        }

        public System.Windows.Visibility ShowError
        {
            get => _showError;
            set => SetProperty(ref _showError, value);
        }

        public ICommand SubmitCommand { get; }
        public ICommand CancelCommand { get; }

        public OtpInputViewModel(Action<bool> onResult)
        {
            _otpCode = string.Empty;
            _validationError = string.Empty;
            _showError = System.Windows.Visibility.Collapsed;

            SubmitCommand = new RelayCommand(() => onResult(true));
            CancelCommand = new RelayCommand(() => onResult(false));
        }

        public void ShowValidationError(string error)
        {
            ValidationError = error;
            ShowError = System.Windows.Visibility.Visible;
        }

        public void HideValidationError()
        {
            ShowError = System.Windows.Visibility.Collapsed;
        }
    }
}
