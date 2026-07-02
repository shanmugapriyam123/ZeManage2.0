using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BIManage.Core.Rules.Models;

// Resolve ambiguous types with Revit API
using WpfColor = System.Windows.Media.Color;
using WpfVisibility = System.Windows.Visibility;

namespace BIManageRevit.BIManage.Views.Rules
{
    /// <summary>
    /// Converts ProtectionMode to a brush for badge display, also handles bool for icon colors
    /// </summary>
    public class ModeToBrushConverter : IValueConverter
    {
        // Regular colors (for text)
        private static readonly SolidColorBrush NotifyBrush = new SolidColorBrush(WpfColor.FromRgb(59, 130, 246));
        private static readonly SolidColorBrush AssistBrush = new SolidColorBrush(WpfColor.FromRgb(245, 158, 11));
        private static readonly SolidColorBrush ProtectBrush = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68));

        // Light colors (for badge backgrounds)
        private static readonly SolidColorBrush NotifyLightBrush = new SolidColorBrush(WpfColor.FromRgb(239, 246, 255)); // #EFF6FF
        private static readonly SolidColorBrush AssistLightBrush = new SolidColorBrush(WpfColor.FromRgb(255, 251, 235)); // #FFFBEB
        private static readonly SolidColorBrush ProtectLightBrush = new SolidColorBrush(WpfColor.FromRgb(254, 242, 242)); // #FEF2F2

        private static readonly SolidColorBrush ActiveBrush = new SolidColorBrush(WpfColor.FromRgb(34, 197, 94));
        private static readonly SolidColorBrush DisabledBrush = new SolidColorBrush(WpfColor.FromRgb(203, 213, 225));

        // Status background colors
        private static readonly SolidColorBrush EnabledBgBrush = new SolidColorBrush(WpfColor.FromRgb(220, 252, 231)); // #DCFCE7 light green
        private static readonly SolidColorBrush DisabledBgBrush = new SolidColorBrush(WpfColor.FromRgb(241, 245, 249)); // #F1F5F9 light gray

        // Toggle switch colors
        private static readonly SolidColorBrush ToggleOnBrush = new SolidColorBrush(WpfColor.FromRgb(34, 197, 94)); // #22C55E green
        private static readonly SolidColorBrush ToggleOffBrush = new SolidColorBrush(WpfColor.FromRgb(203, 213, 225)); // #CBD5E1 gray

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var paramStr = parameter?.ToString() ?? "";

            // Handle bool for icon coloring
            if (paramStr == "bool")
            {
                if (value is bool boolValue)
                {
                    return boolValue ? ActiveBrush : DisabledBrush;
                }
                return DisabledBrush;
            }

            // Handle status text color
            if (paramStr == "status")
            {
                if (value is bool isEnabled)
                {
                    return isEnabled ? ActiveBrush : DisabledBrush;
                }
                return DisabledBrush;
            }

            // Handle status background color
            if (paramStr == "statusBg")
            {
                if (value is bool isEnabled)
                {
                    return isEnabled ? EnabledBgBrush : DisabledBgBrush;
                }
                return DisabledBgBrush;
            }

            // Handle toggle switch background color
            if (paramStr == "toggleBg")
            {
                if (value is bool isEnabled)
                {
                    return isEnabled ? ToggleOnBrush : ToggleOffBrush;
                }
                return ToggleOffBrush;
            }

            // Handle radio button fill (black filled for true, transparent for false)
            if (paramStr == "radio")
            {
                if (value is bool boolValue)
                {
                    return boolValue
                        ? new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#000000"))
                        : new SolidColorBrush(Colors.Transparent);
                }
                return new SolidColorBrush(Colors.Transparent);
            }

            // Handle radio button stroke (black for both true and false — outline always visible)
            if (paramStr == "radioStroke")
            {
                if (value is bool boolValue)
                {
                    return boolValue
                        ? new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#000000"))
                        : new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#000000"));
                }
                return new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#000000"));
            }

            // Handle light colors for badge backgrounds
            if (paramStr == "light" && value is ProtectionMode lightMode)
            {
                return lightMode switch
                {
                    ProtectionMode.Notify => NotifyLightBrush,
                    ProtectionMode.Assist => AssistLightBrush,
                    ProtectionMode.Protect => ProtectLightBrush,
                    _ => NotifyLightBrush
                };
            }

            if (value is ProtectionMode mode)
            {
                return mode switch
                {
                    ProtectionMode.Notify => NotifyBrush,
                    ProtectionMode.Assist => AssistBrush,
                    ProtectionMode.Protect => ProtectBrush,
                    _ => NotifyBrush
                };
            }

            return NotifyBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts bool to Visibility
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool boolValue = value is bool b && b;

            if (parameter?.ToString() == "inverse")
            {
                boolValue = !boolValue;
            }

            return boolValue ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is WpfVisibility visibility)
            {
                bool result = visibility == WpfVisibility.Visible;

                if (parameter?.ToString() == "inverse")
                {
                    result = !result;
                }

                return result;
            }

            return false;
        }
    }

    /// <summary>
    /// Converts bool to lightweight icon (filled/empty circle)
    /// </summary>
    public class BoolToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                // Use filled circle for true, empty circle for false
                return boolValue ? "\u25CF" : "\u25CB";
            }
            return "\u25CB";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts bool to HorizontalAlignment for toggle switch thumb position
    /// </summary>
    public class BoolToAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left;
            }
            return System.Windows.HorizontalAlignment.Left;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
