using BIManage.Addons.Helpers;
using System.Windows;
using System.Windows.Input;

namespace BIManage.Addons.Views.NwcExport
{
    public partial class BulkExportFilePickerWindow : Window
    {
        public BulkExportFilePickerWindow()
        {
            InitializeComponent();

            IconHelper.SetWindowIcon(this);
            HeaderLogo.Source = IconHelper.GetLogoBitmapImage();

            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };

            this.OwnByRevit();
        }

        public static readonly DependencyProperty ShowBrowseButtonProperty =
            DependencyProperty.Register(
                nameof(ShowBrowseButton),
                typeof(bool),
                typeof(BulkExportFilePickerWindow),
                new PropertyMetadata(true));

        public bool ShowBrowseButton
        {
            get => (bool)GetValue(ShowBrowseButtonProperty);
            set => SetValue(ShowBrowseButtonProperty, value);
        }

        public static readonly DependencyProperty ShowImportSourceButtonsProperty =
            DependencyProperty.Register(
                nameof(ShowImportSourceButtons),
                typeof(bool),
                typeof(BulkExportFilePickerWindow),
                new PropertyMetadata(false));

        public bool ShowImportSourceButtons
        {
            get => (bool)GetValue(ShowImportSourceButtonsProperty);
            set => SetValue(ShowImportSourceButtonsProperty, value);
        }
    }
}
