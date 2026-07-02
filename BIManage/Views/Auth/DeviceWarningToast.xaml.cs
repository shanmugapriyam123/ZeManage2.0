using System;
using System.Windows;
using System.Windows.Threading;


namespace BIManageRevit.BIManage.Views.Auth
{
    public partial class DeviceWarningToast : Window
    {
        public bool UserClickedRegister { get; private set; }

        /// <summary>Set to true from any thread to dismiss the toast.</summary>
        public volatile bool ShouldDismiss;
        private DispatcherTimer? _dismissTimer;

        public DeviceWarningToast()
        {
            InitializeComponent();
            PositionBottomRight();

            // Poll every 500ms to check if we should dismiss (cross-thread safe)
            _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _dismissTimer.Tick += (s, e) =>
            {
                if (ShouldDismiss)
                {
                    _dismissTimer?.Stop();
                    UserClickedRegister = false;
                    Close();
                }
            };
            _dismissTimer.Start();
        }

        private void PositionBottomRight()
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 8;
            Top = workArea.Bottom - 90;
        }

        private void Toast_Click(object sender, RoutedEventArgs e)
        {
            UserClickedRegister = true;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            UserClickedRegister = false;
            Close();
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                UserClickedRegister = false;
                Close();
            }
        }
    }
}
