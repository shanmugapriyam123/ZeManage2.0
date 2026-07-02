using BIManage.Addons.Helpers;
using BIManage.Addons.ViewModels.LinkRemapper;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BIManage.Addons.Views.LinkRemapper
{
    public partial class LinkRemapperWindow : Window
    {
        public LinkRemapperWindow()
        {
            InitializeComponent();

            IconHelper.SetWindowIcon(this);
            HeaderLogo.Source = IconHelper.GetLogoBitmapImage();

            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };

            this.OwnByRevit();

            this.DataContextChanged += (s, e) =>
            {
                if (e.NewValue is RevitLinksViewModel vm)
                {
                    vm.PropertyChanged += (sender, args) =>
                    {
                        if (args.PropertyName == nameof(vm.DialogResult)
                         && vm.DialogResult == true)
                        {
                            LinksDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                            LinksDataGrid.CommitEdit(DataGridEditingUnit.Row, true);

                            this.DialogResult = true;
                        }
                    };
                }
            };
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
        }
    }
}
