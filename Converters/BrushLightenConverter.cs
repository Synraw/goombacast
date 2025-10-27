using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace GoombaCast.Converters
{
    public class BrushLightenConverter : IValueConverter
    {
        public static readonly BrushLightenConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not ISolidColorBrush brush)
                return value;

            var color = brush.Color;
            
            // Lighten the color by 20%
            return new SolidColorBrush(new Color(
                color.A,
                (byte)Math.Min(255, color.R + (255 - color.R) * 0.2),
                (byte)Math.Min(255, color.G + (255 - color.G) * 0.2),
                (byte)Math.Min(255, color.B + (255 - color.B) * 0.2)
            ));
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}