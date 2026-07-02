using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BIManage.Addons.ViewModels.NwcExport
{
    public static class NumericTextBoxBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached("IsEnabled", typeof(bool),
            typeof(NumericTextBoxBehavior), new PropertyMetadata(false, OnIsEnabledChanged));

        public static readonly DependencyProperty MinimumValueProperty =
            DependencyProperty.RegisterAttached("MinimumValue", typeof(int),
            typeof(NumericTextBoxBehavior), new PropertyMetadata(1));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);
        public static int GetMinimumValue(DependencyObject obj) => (int)obj.GetValue(MinimumValueProperty);
        public static void SetMinimumValue(DependencyObject obj, int value) => obj.SetValue(MinimumValueProperty, value);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBox textBox)
            {
                if ((bool)e.NewValue)
                {
                    textBox.PreviewTextInput += OnPreviewTextInput;
                    textBox.LostFocus += OnLostFocus;
                    DataObject.AddPastingHandler(textBox, OnPasting);
                }
                else
                {
                    textBox.PreviewTextInput -= OnPreviewTextInput;
                    textBox.LostFocus -= OnLostFocus;
                    DataObject.RemovePastingHandler(textBox, OnPasting);
                }
            }
        }

        private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            Regex regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private static void OnLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                int minimumValue = GetMinimumValue(textBox);
                if (string.IsNullOrEmpty(textBox.Text) || !int.TryParse(textBox.Text, out int value) || value < minimumValue)
                {
                    textBox.Text = minimumValue.ToString();
                    var bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);
                    bindingExpression?.UpdateSource();
                }
            }
        }

        private static void OnPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string text = (string)e.DataObject.GetData(typeof(string));
                Regex regex = new Regex("^[0-9]+$");
                if (!regex.IsMatch(text))
                {
                    e.CancelCommand();
                }
            }
        }
    }
}
