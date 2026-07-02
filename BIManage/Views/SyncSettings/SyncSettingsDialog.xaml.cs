using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BIManage.Core.Features;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.ViewModels.SyncSettings;


namespace BIManageRevit.BIManage.Views.SyncSettings
{
    public partial class SyncSettingsDialog : Window
    {
        private readonly SyncSettingsViewModel _viewModel;
        private readonly ILogger? _logger;

        public SyncSettingsDialog(
            SyncRepository syncRepository,
            ILogger? logger = null,
            IFeatureToggleService? featureToggleService = null)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            _logger = logger;
            _viewModel = new SyncSettingsViewModel(syncRepository, logger, featureToggleService);
            DataContext = _viewModel;

            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.LoadAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SyncSettingsDialog.OnLoaded failed: {ex.Message}", ex);
            }
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var success = await _viewModel.SaveBackgroundSyncSettingsAsync();
                if (success)
                {
                    ShowSuccessPopup("Settings Saved", "Background sync settings updated successfully.");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Save failed: {ex.Message}", ex);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var success = await _viewModel.ExportToFileAsync();
                if (success)
                {
                    ShowSuccessPopup("Exported", "Sync settings exported successfully.");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Export failed: {ex.Message}", ex);
            }
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var success = await _viewModel.ImportFromFileAsync();
                if (success)
                {
                    ShowSuccessPopup("Imported", "Settings loaded — click Save to persist.");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Import failed: {ex.Message}", ex);
            }
        }

        private void PreventSyncViewsTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Only restrict to digits while typing — do NOT block "0" or "1" mid-keystroke,
            // otherwise the user can never type any multi-digit number that begins with 1
            // (10, 11, 12, 20 — typing 2 first works for 20, but 10/11/etc. need a leading 1).
            // Minimum-value enforcement (>= 2) happens on LostFocus / Save instead.
            foreach (var ch in e.Text)
            {
                if (!char.IsDigit(ch))
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        private void PreventSyncViewsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Strip any non-digit characters that may have been pasted in. Do not coerce
            // values < 2 here — that would interrupt the user mid-typing (e.g. erase the
            // "1" before they type the "0" of "10"). The LostFocus handler enforces the
            // minimum once the user is done editing.
            if (sender is not TextBox tb) return;
            if (string.IsNullOrEmpty(tb.Text)) return;

            var digitsOnly = new string(tb.Text.Where(char.IsDigit).ToArray());
            if (digitsOnly != tb.Text)
            {
                var caret = tb.SelectionStart;
                tb.Text = digitsOnly;
                tb.SelectionStart = Math.Min(caret, digitsOnly.Length);
            }
        }

        private void PreventSyncViewsTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // Final commit validation: any value below the minimum (2) is reset to the
            // minimum so saved settings always honor the documented rule.
            if (sender is not TextBox tb) return;
            if (!int.TryParse(tb.Text, out var value) || value < 2)
            {
                tb.Text = "2";
                _viewModel.PreventSyncWhenViewsOpenedOver = 2;
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }

        private void ShowSuccessPopup(string title, string message)
        {
            SuccessTitle.Text = title;
            SuccessMessage.Text = message;
            SuccessPopup.Visibility = System.Windows.Visibility.Visible;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                SuccessPopup.Visibility = System.Windows.Visibility.Collapsed;
            };
            timer.Start();
        }
    }
}
