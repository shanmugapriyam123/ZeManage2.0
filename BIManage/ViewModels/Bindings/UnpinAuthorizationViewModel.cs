using System;
using System.Windows.Input;
using BIManage.Common.Helpers;

namespace BIManageRevit.BIManage.ViewModels.Bindings
{
    /// <summary>
    /// ViewModel for Unpin Authorization Dialog (Prevent mode OTP menu)
    /// </summary>
    public class UnpinAuthorizationViewModel : ViewModelBase
    {
        public int ElementCount { get; }
        public string ElementList { get; }
        public string UserName { get; }
        public ICommand EnterOtpCommand { get; }
        public ICommand CancelCommand { get; }

        public UnpinAuthorizationViewModel(int elementCount, string elementList, string userName, Action<bool> onResult)
        {
            ElementCount = elementCount;
            ElementList = elementList;
            UserName = userName;
            EnterOtpCommand = new RelayCommand(() => onResult(true));
            CancelCommand = new RelayCommand(() => onResult(false));
        }
    }
}
