using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace BIManage.Addons.ViewModels.LinkRemapper
{
    public class RevitLinkItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        internal Action<bool> _onSelectionChanged;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
                _onSelectionChanged?.Invoke(value);
            }
        }

        public string LinkName { get; set; }
        public string CurrentFilePath { get; set; }

        public ObservableCollection<string> NewFilePaths { get; } = new();

        public string NewFilePath => NewFilePaths.LastOrDefault() ?? string.Empty;

        public string Name { get; internal set; }
        public string ViewName { get; internal set; }

        public RevitLinkItem(Action<bool> onSelectionChanged = null)
        {
            _onSelectionChanged = onSelectionChanged;

            NewFilePaths.CollectionChanged += (s, e) =>
                OnPropertyChanged(nameof(NewFilePath));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string n) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
