using BIManage.ViewModels;


namespace BIManage.Views
{
    public sealed partial class BIManageView
    {
        public BIManageView(BIManageViewModel viewModel)
        {
            DataContext = viewModel;
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);
        }
    }
}