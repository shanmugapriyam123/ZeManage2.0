using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using BIManage.Core.Rules.Models;

namespace BIManageRevit.BIManage.Views.Converters
{
    /// <summary>
    /// Converts ProtectionMode enum to color for mode badge
    /// </summary>
    public class ModeToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ProtectionMode mode)
            {
                return mode switch
                {
                    ProtectionMode.Notify => new SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246)),  // #3B82F6 - Blue (Notify)
                    ProtectionMode.Assist => new SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 146, 60)),   // #FB923C - Orange (Assist)
                    ProtectionMode.Protect => new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68)),  // #EF4444 - Red (Protect)
                    _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(107, 114, 128)) // #6B7280 - Gray (Default)
                };
            }
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(107, 114, 128));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
