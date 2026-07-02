using System;
using System.Windows.Input;
using BIManage.Common.Helpers;

namespace BIManageRevit.BIManage.ViewModels.Bindings
{
    /// <summary>
    /// ViewModel for Pin Selection Dialog
    /// </summary>
    public class PinSelectionViewModel : ViewModelBase
    {
        public int ElementCount { get; }
        public string ElementList { get; }
        public ICommand NormalPinCommand { get; }
        public ICommand ProtectedPinCommand { get; }

        public bool IsProtectedPin { get; private set; }

        public PinSelectionViewModel(int elementCount, string elementList, Action<bool> onResult)
        {
            ElementCount = elementCount;
            ElementList = elementList;
            NormalPinCommand = new RelayCommand(() => onResult(false));
            ProtectedPinCommand = new RelayCommand(() => onResult(true));
        }
    }
}
