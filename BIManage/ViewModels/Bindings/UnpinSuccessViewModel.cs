using System;
using System.Windows.Input;
using BIManage.Common.Helpers;

namespace BIManageRevit.BIManage.ViewModels.Bindings
{
    /// <summary>
    /// ViewModel for Unpin Success Dialog
    /// </summary>
    public class UnpinSuccessViewModel : ViewModelBase
    {
        public int ElementCount { get; }
        public string Message { get; }
        public ICommand OkCommand { get; }

        public UnpinSuccessViewModel(int elementCount, string message, Action onOk)
        {
            ElementCount = elementCount;
            Message = message;
            OkCommand = new RelayCommand(onOk);
        }
    }
}
