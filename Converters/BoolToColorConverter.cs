using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace GoombaCast.Converters
{
    public class BoolToColorConverter : IValueConverter
    {
        public IBrush TrueValue { get; set; } = Brushes.Red;
        public IBrush FalseValue { get; set; } = Brushes.Green;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is bool boolValue && boolValue ? TrueValue : FalseValue;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}