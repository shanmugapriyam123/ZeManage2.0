using System;
using System.Windows.Input;
using BIManage.Common.Helpers;

namespace BIManageRevit.BIManage.ViewModels.Bindings
{
    /// <summary>
    /// ViewModel for Unpin Warning Dialog (Guide mode)
    /// </summary>
    public class UnpinWarningViewModel : ViewModelBase
    {
        public int ElementCount { get; }
        public string ElementList { get; }
        public ICommand OkCommand { get; }
        public ICommand CancelCommand { get; }

        public UnpinWarningViewModel(int elementCount, string elementList, Action<bool> onResult)
        {
            ElementCount = elementCount;
            ElementList = elementList;
            OkCommand = new RelayCommand(() => onResult(true));
            CancelCommand = new RelayCommand(() => onResult(false));
        }
    }
}
