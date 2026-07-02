using System;
using System.Globalization;
using System.Windows.Data;

namespace BIManage.Common.Converters
{
    /// <summary>
    /// Converts a boolean to Visibility (inverted): True -> Collapsed, False -> Visible.
    /// </summary>
    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
                return boolValue ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            return System.Windows.Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is System.Windows.Visibility visibility)
                return visibility != System.Windows.Visibility.Visible;
            return true;
        }
    }
}
