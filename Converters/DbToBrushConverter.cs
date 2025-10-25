using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GoombaCast.Converters
{
    // Maps dBFS level (-90..0) to a brush: green -> yellow -> red
    public sealed class DbToBrushConverter : IValueConverter
    {
        public float MinDb { get; set; } = -90f;
        public float YellowThresholdDb { get; set; } = -18f;
        public float RedThresholdDb { get; set; } = -6f;
        public bool UseGradient { get; set; } = true;

        private static readonly Color Green = Color.FromRgb(0x22, 0xC5, 0x22);
        private static readonly Color Yellow = Color.FromRgb(0xFF, 0xD7, 0x00);
        private static readonly Color Red = Color.FromRgb(0xE0, 0x30, 0x30);

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            float db = value switch
            {
                float f => f,
                double d => (float)d,
                int i => i,
                _ => MinDb
            };

            if (!UseGradient)
            {
                var c = db >= RedThresholdDb ? Red
                      : db >= YellowThresholdDb ? Yellow
                      : Green;
                return new SolidColorBrush(c);
            }

            // Below yellow: interpolate Green -> Yellow
            if (db < YellowThresholdDb)
            {
                double t = InverseLerp(MinDb, YellowThresholdDb, db);
                return new SolidColorBrush(Lerp(Green, Yellow, t));
            }

            // Yellow -> Red
            double t2 = InverseLerp(YellowThresholdDb, RedThresholdDb, db);
            return new SolidColorBrush(Lerp(Yellow, Red, t2));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

        private static double InverseLerp(double a, double b, double v)
        {
            if (Math.Abs(b - a) < double.Epsilon) return 0;
            var t = (v - a) / (b - a);
            return Math.Clamp(t, 0, 1);
        }

        private static Color Lerp(Color a, Color b, double t)
        {
            byte LerpByte(byte x, byte y) => (byte)(x + (y - x) * t);
            return Color.FromRgb(
                LerpByte(a.R, b.R),
                LerpByte(a.G, b.G),
                LerpByte(a.B, b.B));
        }
    }
}