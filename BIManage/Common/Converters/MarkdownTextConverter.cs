using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace BIManage.Common.Converters
{
    /// <summary>
    /// Converts AI markdown responses to a selectable RichTextBox with FlowDocument.
    /// Handles: ### headers, **bold**, * bullets, - bullets, numbered lists, plain text.
    /// Text is fully selectable — users can mouse-select, Ctrl+A, Ctrl+C, or right-click Copy.
    /// </summary>
    public class MarkdownTextConverter : IValueConverter
    {
        private static readonly System.Windows.Media.SolidColorBrush DarkBlue =
            new(System.Windows.Media.Color.FromRgb(0, 0, 0));
        private static readonly System.Windows.Media.SolidColorBrush MidBlue =
            new(System.Windows.Media.Color.FromRgb(0, 0, 0));
        private static readonly System.Windows.Media.SolidColorBrush TextDark =
            new(System.Windows.Media.Color.FromRgb(30, 41, 59));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string text)
                return value;

            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontSize = 13,
                Foreground = TextDark,
                LineHeight = 20
            };

            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd();

                if (string.IsNullOrWhiteSpace(line))
                {
                    doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 2, 0, 2) });
                    continue;
                }

                if (line.StartsWith("### "))
                {
                    doc.Blocks.Add(new Paragraph(new Run(line.Substring(4).Trim()))
                    {
                        FontWeight = FontWeights.Bold,
                        FontSize = 13.5,
                        Foreground = DarkBlue,
                        Margin = new Thickness(0, 6, 0, 2)
                    });
                    continue;
                }

                if (line.StartsWith("## "))
                {
                    doc.Blocks.Add(new Paragraph(new Run(line.Substring(3).Trim()))
                    {
                        FontWeight = FontWeights.Bold,
                        FontSize = 14,
                        Foreground = DarkBlue,
                        Margin = new Thickness(0, 6, 0, 2)
                    });
                    continue;
                }

                if (Regex.IsMatch(line, @"^\s*[\*\-]\s+"))
                {
                    var indent = line.Length - line.TrimStart().Length;
                    var bulletText = Regex.Replace(line.TrimStart(), @"^[\*\-]\s+", "");
                    var p = new Paragraph { Margin = new Thickness(indent * 4 + 16, 1, 0, 1) };
                    p.Inlines.Add(new Run("\u2022  ") { Foreground = MidBlue });
                    AddInlines(p, bulletText);
                    doc.Blocks.Add(p);
                    continue;
                }

                var m = Regex.Match(line, @"^(\s*)(\d+)\.\s+(.+)$");
                if (m.Success)
                {
                    var indent = m.Groups[1].Value.Length;
                    var p = new Paragraph { Margin = new Thickness(indent * 4 + 16, 1, 0, 1) };
                    p.Inlines.Add(new Run($"{m.Groups[2].Value}.  ") { Foreground = MidBlue, FontWeight = FontWeights.SemiBold });
                    AddInlines(p, m.Groups[3].Value);
                    doc.Blocks.Add(p);
                    continue;
                }

                var para = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                AddInlines(para, line);
                doc.Blocks.Add(para);
            }

            var rtb = new RichTextBox
            {
                Document = doc,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(-4, 0, -4, 0),
                Focusable = true,
                IsTabStop = false
            };
            ScrollViewer.SetVerticalScrollBarVisibility(rtb, ScrollBarVisibility.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(rtb, ScrollBarVisibility.Disabled);

            return rtb;
        }

        private static void AddInlines(Paragraph p, string text)
        {
            var lastIndex = 0;
            foreach (Match match in Regex.Matches(text, @"\*\*(.+?)\*\*"))
            {
                if (match.Index > lastIndex)
                    p.Inlines.Add(new Run(text.Substring(lastIndex, match.Index - lastIndex)));
                p.Inlines.Add(new Bold(new Run(match.Groups[1].Value)));
                lastIndex = match.Index + match.Length;
            }
            if (lastIndex < text.Length)
                p.Inlines.Add(new Run(text.Substring(lastIndex)));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
