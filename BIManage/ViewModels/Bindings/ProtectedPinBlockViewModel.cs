using System;
using System.Collections.Generic;
using System.Windows.Input;
using BIManage.Common.Helpers;

namespace BIManageRevit.BIManage.ViewModels.Bindings
{
    /// <summary>
    /// ViewModel for Protected Pin Block Dialog (Prevent mode)
    /// </summary>
    public class ProtectedPinBlockViewModel : ViewModelBase
    {
        public int ElementCount { get; }
        public string ElementNames { get; }
        public string Mode { get; }
        public string Message { get; }
        public ICommand CloseCommand { get; }

        public ProtectedPinBlockViewModel(
            int elementCount,
            string elementNames,
            string mode,
            string message,
            Action onClose)
        {
            ElementCount = elementCount;
            ElementNames = elementNames;
            Mode = mode;
            Message = message;
            CloseCommand = new RelayCommand(onClose);
        }
    }
}
