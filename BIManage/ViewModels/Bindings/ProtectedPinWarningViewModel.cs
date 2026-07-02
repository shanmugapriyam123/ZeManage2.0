using System;
using System.Windows.Input;
using BIManage.Common.Helpers;

namespace BIManageRevit.BIManage.ViewModels.Bindings
{
    /// <summary>
    /// ViewModel for Protected Pin Warning Dialog (Guide mode)
    /// </summary>
    public class ProtectedPinWarningViewModel : ViewModelBase
    {
        public int ElementCount { get; }
        public string ElementNames { get; }
        public string Mode { get; }
        public string Message { get; }
        public ICommand ProceedCommand { get; }
        public ICommand CancelCommand { get; }

        public ProtectedPinWarningViewModel(
            int elementCount,
            string elementNames,
            string mode,
            string message,
            Action<bool> onResult)
        {
            ElementCount = elementCount;
            ElementNames = elementNames;
            Mode = mode;
            Message = message;
            ProceedCommand = new RelayCommand(() => onResult(true));
            CancelCommand = new RelayCommand(() => onResult(false));
        }
    }
}
