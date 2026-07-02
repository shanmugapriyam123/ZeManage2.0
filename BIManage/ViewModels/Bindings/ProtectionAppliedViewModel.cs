using System;
using System.Windows.Input;
using BIManage.Common.Helpers;

namespace BIManageRevit.BIManage.ViewModels.Bindings
{
    /// <summary>
    /// ViewModel for Protection Applied Dialog
    /// </summary>
    public class ProtectionAppliedViewModel : ViewModelBase
    {
        public int ElementCount { get; }
        public string ProtectedBy { get; }
        public string Message { get; }
        public ICommand OkCommand { get; }

        public ProtectionAppliedViewModel(int elementCount, string protectedBy, string message, Action onOk)
        {
            ElementCount = elementCount;
            ProtectedBy = protectedBy;
            Message = message;
            OkCommand = new RelayCommand(onOk);
        }
    }
}
