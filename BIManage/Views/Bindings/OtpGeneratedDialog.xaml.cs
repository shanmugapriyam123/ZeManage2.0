using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;


namespace BIManageRevit.BIManage.Views.Bindings
{
    public partial class OtpGeneratedDialog : Window
    {
        public string OtpCode { get; private set; }
        public bool Generated { get; private set; }

        private readonly string _username;
        private readonly DateTime _expiresAt;

        public OtpGeneratedDialog(string otpCode, string username, DateTime expiresAt)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            OtpCode = otpCode;
            _username = username;
            _expiresAt = expiresAt;

            TxtOtpCode.Text = otpCode;
            TxtValidFor.Text = "24 hours";
            TxtExpires.Text = expiresAt.ToLocalTime().ToString("dd-MM-yyyy HH:mm");
            TxtGeneratedBy.Text = username;
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            // OTP is already saved on the server via POST /api/v1/Revit/otp/generate
            // No local save needed
            Generated = true;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Generated = false;
            DialogResult = false;
            Close();
        }

        private void CopyCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(OtpCode);
                CopiedBanner.Visibility = System.Windows.Visibility.Visible;

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (s, args) =>
                {
                    CopiedBanner.Visibility = System.Windows.Visibility.Collapsed;
                    timer.Stop();
                };
                timer.Start();
            }
            catch
            {
                // Clipboard access can fail in some environments
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                OK_Click(sender, new RoutedEventArgs());
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
    }
}
