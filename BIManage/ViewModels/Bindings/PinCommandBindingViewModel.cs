using System;
using System.Windows;
using System.Windows.Input;
using BIManage.Common.Helpers;

namespace BIManageRevit.BIManage.ViewModels.Bindings
{
    /// <summary>
    /// ViewModel for Pin Command Dialog
    /// </summary>
    public class PinCommandBindingViewModel : ViewModelBase
    {
        public string MainTitle { get; }
        public string Instruction { get; }
        public string ElementListText { get; }

        public ICommand NormalPinCommand { get; }
        public ICommand ProtectedPinCommand { get; }
        public ICommand CancelCommand { get; }

        public PinCommandBindingViewModel(
            string title,
            string instruction,
            string elementList,
            Action<bool, bool> onClose)
        {
            MainTitle = title;
            Instruction = instruction;
            ElementListText = elementList;

            NormalPinCommand = new RelayCommand(() => onClose(true, false));
            ProtectedPinCommand = new RelayCommand(() => onClose(true, true));
            CancelCommand = new RelayCommand(() => onClose(false, false));
        }
    }

    /// <summary>
    /// ViewModel for OTP Dialog
    /// </summary>
    public class OTPDialogViewModel : ViewModelBase
    {
        private string _timerText = "Code expires in: 05:00";
        private bool _isVerifyEnabled;
        private System.Windows.Visibility _errorVisibility = System.Windows.Visibility.Collapsed;
        private string _errorText = string.Empty;

        public string TimerText
        {
            get => _timerText;
            set { _timerText = value; OnPropertyChanged(); }
        }

        public bool IsVerifyEnabled
        {
            get => _isVerifyEnabled;
            set { _isVerifyEnabled = value; OnPropertyChanged(); }
        }

        public System.Windows.Visibility ErrorVisibility
        {
            get => _errorVisibility;
            set { _errorVisibility = value; OnPropertyChanged(); }
        }

        public string ErrorText
        {
            get => _errorText;
            set { _errorText = value; OnPropertyChanged(); }
        }

        public ICommand VerifyCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ResendCommand { get; }

        public OTPDialogViewModel(Action<bool> onVerify, Action onResend)
        {
            VerifyCommand = new RelayCommand(() => onVerify(true));
            CancelCommand = new RelayCommand(() => onVerify(false));
            ResendCommand = new RelayCommand(onResend);
        }

        public void ShowError(string message)
        {
            ErrorText = message;
            ErrorVisibility = System.Windows.Visibility.Visible;
        }

        public void HideError()
        {
            ErrorVisibility = System.Windows.Visibility.Collapsed;
        }
    }

    /// <summary>
    /// ViewModel for Protection Success Dialog
    /// </summary>
    public class ProtectionSuccessViewModel : ViewModelBase
    {
        public string Message { get; }
        public string ElementCount { get; }
        public string Username { get; }

        public ICommand OkCommand { get; }

        public ProtectionSuccessViewModel(
            string message,
            int elementCount,
            string username,
            Action onOk)
        {
            Message = message;
            ElementCount = elementCount.ToString();
            Username = username;
            OkCommand = new RelayCommand(onOk);
        }
    }

    /// <summary>
    /// ViewModel for Error Dialog
    /// </summary>
    public class ErrorDialogViewModel : ViewModelBase
    {
        public string Title { get; }
        public string Message { get; }
        public string Details { get; }
        public System.Windows.Visibility DetailsVisibility { get; }

        public ICommand OkCommand { get; }

        public ErrorDialogViewModel(string title, string message, string details = null)
        {
            Title = title;
            Message = message;
            Details = details;
            DetailsVisibility = string.IsNullOrEmpty(details)
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;

            OkCommand = new RelayCommand(() => { });
        }
    }

    /// <summary>
    /// ViewModel for Permission Denied Dialog
    /// </summary>
    public class PermissionDeniedViewModel : ViewModelBase
    {
        public string CurrentUser { get; }
        public ICommand OkCommand { get; }

        public PermissionDeniedViewModel(string currentUser, Action onOk)
        {
            CurrentUser = currentUser;
            OkCommand = new RelayCommand(onOk);
        }
    }

    /// <summary>
    /// Base ViewModel with INotifyPropertyChanged implementation
    /// </summary>
    public abstract class ViewModelBase : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}