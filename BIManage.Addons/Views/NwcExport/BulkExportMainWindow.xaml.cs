using BIManage.Addons.Helpers;
using System.Windows;
using System.Windows.Input;

namespace BIManage.Addons.Views.NwcExport
{
    public partial class BulkExportMainWindow : Window
    {
        public BulkExportMainWindow()
        {
            InitializeComponent();

            IconHelper.SetWindowIcon(this);
            HeaderLogo.Source = IconHelper.GetLogoBitmapImage();

            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };

            this.OwnByRevit();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
        }

        private void TextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
        }
    }
}
