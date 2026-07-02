using System.ComponentModel;

namespace BIManage.Addons.ViewModels.NwcExport
{
    public class FileViewPair : INotifyPropertyChanged
    {
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public string ViewName { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string n)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
