using System.Windows.Input;
using BIManage.Core.Protection;
using BIManage.Core.Protection.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BIManage.ViewModels.Protection
{
    /// <summary>
    /// ViewModel for password configuration dialog
    /// </summary>
    public class PasswordConfigViewModel : ObservableObject
    {
        private readonly PasswordManager _passwordManager;
        private string _currentPassword;
        private string _newPassword;
        private string _confirmPassword;
        private string _statusMessage;
        private bool _hasError;
        private bool _success;

        public string CurrentPassword
        {
            get => _currentPassword;
            set
            {
                SetProperty(ref _currentPassword, value);
                ClearStatus();
            }
        }

        public string NewPassword
        {
            get => _newPassword;
            set
            {
                SetProperty(ref _newPassword, value);
                OnPropertyChanged(nameof(PasswordStrength));
                OnPropertyChanged(nameof(PasswordStrengthText));
                OnPropertyChanged(nameof(PasswordStrengthColor));
                ClearStatus();
            }
        }

        public string ConfirmPassword
        {
            get => _confirmPassword;
            set
            {
                SetProperty(ref _confirmPassword, value);
                ClearStatus();
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool HasError
        {
            get => _hasError;
            set => SetProperty(ref _hasError, value);
        }

        public bool Success
        {
            get => _success;
            set => SetProperty(ref _success, value);
        }

        public bool IsPasswordSet => _passwordManager?.IsPasswordSet() ?? false;

        public PasswordStrength PasswordStrength
        {
            get
            {
                var credentials = new AdminCredentials { NewPassword = NewPassword };
                return credentials.GetStrength();
            }
        }

        public string PasswordStrengthText
        {
            get
            {
                return PasswordStrength switch
                {
                    PasswordStrength.None => "",
                    PasswordStrength.Weak => "Weak",
                    PasswordStrength.Medium => "Medium",
                    PasswordStrength.Strong => "Strong",
                    PasswordStrength.VeryStrong => "Very Strong",
                    _ => ""
                };
            }
        }

        public string PasswordStrengthColor
        {
            get
            {
                return PasswordStrength switch
                {
                    PasswordStrength.Weak => "#F44336",
                    PasswordStrength.Medium => "#FF9800",
                    PasswordStrength.Strong => "#4CAF50",
                    PasswordStrength.VeryStrong => "#2196F3",
                    _ => "#BDBDBD"
                };
            }
        }

        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }

        public PasswordConfigViewModel(PasswordManager passwordManager)
        {
            _passwordManager = passwordManager;
            SaveCommand = new RelayCommand(OnSave, CanSave);
            CancelCommand = new RelayCommand(OnCancel);
        }

        private bool CanSave()
        {
            if (string.IsNullOrWhiteSpace(NewPassword))
                return false;

            if (NewPassword != ConfirmPassword)
                return false;

            if (IsPasswordSet && string.IsNullOrWhiteSpace(CurrentPassword))
                return false;

            return true;
        }

        public void OnSave()
        {
            var credentials = new AdminCredentials
            {
                CurrentPassword = CurrentPassword,
                NewPassword = NewPassword,
                ConfirmPassword = ConfirmPassword
            };

            if (!credentials.IsValid())
            {
                StatusMessage = "Password validation failed. Please check your input.";
                HasError = true;
                Success = false;
                return;
            }

            if (NewPassword.Length < 6)
            {
                StatusMessage = "Password must be at least 6 characters long.";
                HasError = true;
                Success = false;
                return;
            }

            if (NewPassword != ConfirmPassword)
            {
                StatusMessage = "Passwords do not match.";
                HasError = true;
                Success = false;
                return;
            }

            var result = _passwordManager.SetPassword(NewPassword, CurrentPassword);

            if (result)
            {
                StatusMessage = "Password updated successfully!";
                HasError = false;
                Success = true;

                // Clear fields after successful save
                CurrentPassword = string.Empty;
                NewPassword = string.Empty;
                ConfirmPassword = string.Empty;
            }
            else
            {
                StatusMessage = "Failed to update password. Check current password is correct.";
                HasError = true;
                Success = false;
            }
        }

        public void OnCancel()
        {
            Success = false;
        }

        private void ClearStatus()
        {
            StatusMessage = string.Empty;
            HasError = false;
            Success = false;
        }
    }
}
