using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BIManageRevit.BIManage.Views.Converters
{
    /// <summary>
    /// Converts boolean (IsEnabled) to color for status indicator
    /// </summary>
    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isEnabled)
            {
                // Green for enabled, Gray for disabled
                return isEnabled
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94))   // #22C55E - Green
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(156, 163, 175)); // #9CA3AF - Gray
            }
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(156, 163, 175)); // Default gray
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
