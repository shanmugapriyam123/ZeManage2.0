using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BIManage.Common.Converters
{
    /// <summary>
    /// Converts a FeedbackRating (int?) to a foreground color.
    /// ConverterParameter indicates which button this is for (1 = thumbs up, -1 = thumbs down).
    /// Active = colored (#0288D1 for up, #E53935 for down), Inactive = gray (#CBD5E1).
    /// </summary>
    public class FeedbackColorConverter : IValueConverter
    {
        private static readonly Brush ActiveUp = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString("#0288D1"));
        private static readonly Brush ActiveDown = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString("#E53935"));
        private static readonly Brush Inactive = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString("#CBD5E1"));

        static FeedbackColorConverter()
        {
            ActiveUp.Freeze();
            ActiveDown.Freeze();
            Inactive.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var rating = value as int?;
            var buttonType = 0;
            if (parameter is string s && int.TryParse(s, out var parsed))
                buttonType = parsed;

            if (rating == null || rating == 0)
                return Inactive;

            if (rating == buttonType)
                return buttonType == 1 ? ActiveUp : ActiveDown;

            return Inactive;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
