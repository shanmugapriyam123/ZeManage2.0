using System;
using System.Collections.Generic;
using System.Windows.Input;
using Autodesk.Revit.DB;
using BIManage.Core.Protection;
using BIManage.Core.Rules.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BIManage.ViewModels.Protection
{
    /// <summary>
    /// ViewModel for Prevent mode dialog with password protection
    /// </summary>
    public class PreventDialogViewModel : ProtectionDialogBaseViewModel
    {
        private readonly PasswordManager _passwordManager;
        private string _password;
        private bool _overrideAllowed;
        private string _statusMessage;
        private bool _hasError;

        public string Password
        {
            get => _password;
            set
            {
                SetProperty(ref _password, value);
                StatusMessage = string.Empty;
                HasError = false;
            }
        }

        public bool OverrideAllowed
        {
            get => _overrideAllowed;
            private set => SetProperty(ref _overrideAllowed, value);
        }

        /// <summary>
        /// Override method used: "AdminPassword" or "OTP"
        /// </summary>
        public string OverrideMethod { get; set; }

        /// <summary>
        /// OTP code entered by user (when OTP override is used)
        /// </summary>
        public string OtpCode { get; set; }

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

        public bool IsPasswordSet => _passwordManager?.IsPasswordSet() ?? false;

        public ICommand OverrideCommand { get; }
        public ICommand CancelCommand { get; }

        public PreventDialogViewModel(PasswordManager passwordManager)
        {
            _passwordManager = passwordManager;
            OverrideCommand = new RelayCommand(OnOverride, CanOverride);
            CancelCommand = new RelayCommand(OnCancel);
        }

        public override void Initialize(RuleEvaluationResult result, ICollection<ElementId> elements)
        {
            base.Initialize(result, elements);

            DialogTitle = "Action Prevented";
            DialogMessage = result?.CombinedMessage ?? "This action is not permitted.";
            OverrideAllowed = false;
            
            if (!IsPasswordSet)
            {
                StatusMessage = "No override password configured. Contact your administrator.";
                HasError = true;
            }
        }

        private bool CanOverride()
        {
            return IsPasswordSet && !string.IsNullOrWhiteSpace(Password);
        }

        public void OnOverride()
        {
            if (!IsPasswordSet)
            {
                StatusMessage = "No password configured. Override not possible.";
                HasError = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(Password))
            {
                StatusMessage = "Please enter a password.";
                HasError = true;
                return;
            }

            var isValid = _passwordManager.ValidatePassword(Password);
            
            if (isValid)
            {
                OverrideAllowed = true;
                OverrideMethod = "AdminPassword";
                StatusMessage = "Password verified. Override granted.";
                HasError = false;
            }
            else
            {
                OverrideAllowed = false;
                StatusMessage = "Invalid password. Access denied.";
                HasError = true;
                Password = string.Empty; // Clear invalid password
            }
        }

        public void OnCancel()
        {
            OverrideAllowed = false;
        }
    }
}
