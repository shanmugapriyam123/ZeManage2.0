using System;
using System.Windows.Input;
using BIManage.Common.Helpers;

namespace BIManageRevit.BIManage.ViewModels.Bindings
{
    /// <summary>
    /// ViewModel for Pinned Element Alert Dialog
    /// </summary>
    public class PinnedElementAlertViewModel : ViewModelBase
    {
        public int PinnedCount { get; }
        public string Message { get; }
        public string HeaderText { get; }
        public string SubText { get; }
        public ICommand OkCommand { get; }

        public PinnedElementAlertViewModel(int pinnedCount, string message, Action onOk,
            string headerText = "Cannot Move Pinned Elements",
            string subText = "These elements are protected and cannot be moved.")
        {
            PinnedCount = pinnedCount;
            Message = message;
            HeaderText = headerText;
            SubText = subText;
            OkCommand = new RelayCommand(onOk);
        }
    }
}
