using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;

namespace BIManageRevit.BIManage.Views.Auth
{
    public enum LicenseToastSeverity
    {
        Info,
        Warning,
        Error,
    }

    public partial class LicenseWarningToast : Window
    {
        public volatile bool ShouldDismiss;

        private readonly DispatcherTimer _dismissPoll;
        private readonly DispatcherTimer _autoCloseTimer;

        public LicenseWarningToast(string message, LicenseToastSeverity severity, TimeSpan autoCloseAfter)
        {
            InitializeComponent();

            MessageText.Text = message ?? string.Empty;
            ApplySeverity(severity);
            PositionBottomRight();

            _dismissPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _dismissPoll.Tick += (s, e) =>
            {
                if (ShouldDismiss)
                {
                    _dismissPoll.Stop();
                    Close();
                }
            };
            _dismissPoll.Start();

            if (autoCloseAfter > TimeSpan.Zero)
            {
                _autoCloseTimer = new DispatcherTimer { Interval = autoCloseAfter };
                _autoCloseTimer.Tick += (s, e) =>
                {
                    _autoCloseTimer.Stop();
                    Close();
                };
                _autoCloseTimer.Start();
            }
        }

        private void ApplySeverity(LicenseToastSeverity severity)
        {
            switch (severity)
            {
                case LicenseToastSeverity.Error:
                    IconBackground.Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
                    IconGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
                    IconGlyph.Text = "!";
                    TitleText.Text = "License Issue";
                    TitleText.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
                    break;
                case LicenseToastSeverity.Warning:
                    TitleText.Text = "License Notice";
                    break;
                case LicenseToastSeverity.Info:
                default:
                    IconBackground.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xF2, 0xFE));
                    IconGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0x07, 0x5A, 0x9E));
                    IconGlyph.Text = "i";
                    TitleText.Text = "License";
                    TitleText.Foreground = new SolidColorBrush(Color.FromRgb(0x07, 0x5A, 0x9E));
                    break;
            }
        }

        private void PositionBottomRight()
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 8;
            Top = workArea.Bottom - 110;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape) Close();
        }
    }
}
