using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;


namespace BIManage.Views.Common
{
    public enum ZeMessageType
    {
        Info,
        Warning,
        Error,
        Success
    }

    public partial class ZeMessageBox : Window
    {
        private DispatcherTimer _autoCloseTimer;

        public ZeMessageBox()
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);
        }

        public static void Show(string title, string message, ZeMessageType type = ZeMessageType.Info, int autoCloseSeconds = 0)
        {
            var dialog = new ZeMessageBox();
            dialog.TitleText.Text = title;
            dialog.MessageText.Text = message;

            if (autoCloseSeconds > 0)
            {
                dialog.StartAutoClose(autoCloseSeconds);
            }
            else if (type == ZeMessageType.Success)
            {
                dialog.StartAutoClose(30);
            }

            // Prefer the currently-active WPF window as the owner so the message box
            // stacks above the dialog that triggered it AND centers on screen properly.
            // Parenting only through WindowInteropHelper to Revit's HWND caused the popup
            // to appear behind / at (0,0) of the calling dialog instead of centered.
            // Topmost is a safety net for cases where no active WPF owner can be found.
            Window activeOwner = null;
            if (Application.Current != null)
            {
                foreach (Window w in Application.Current.Windows)
                {
                    if (w != null && w.IsActive && w != dialog)
                    {
                        activeOwner = w;
                        break;
                    }
                }
            }

            if (activeOwner != null)
            {
                dialog.Owner = activeOwner;
            }
            else
            {
                new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle };
            }
            dialog.Topmost = true;
            dialog.ShowDialog();
        }

        private void StartAutoClose(int seconds)
        {
            _autoCloseTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(seconds)
            };
            _autoCloseTimer.Tick += (s, e) =>
            {
                _autoCloseTimer.Stop();
                DialogResult = true;
                Close();
            };
            _autoCloseTimer.Start();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            _autoCloseTimer?.Stop();
            DialogResult = true;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _autoCloseTimer?.Stop();
            DialogResult = false;
            Close();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }
    }
}
