using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MobileEssControl.Converters;

public sealed class PowerSegmentBrushConverter : IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        double powerKw = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0
        };

        int segmentIndex = 0;

        if (parameter is string text)
        {
            int.TryParse(text, out segmentIndex);
        }

        powerKw = Math.Clamp(powerKw, 0, 44);

        int activeCount = (int)Math.Ceiling(powerKw / 44.0 * 10.0);

        if (segmentIndex <= activeCount)
        {
            return new SolidColorBrush(Color.Parse("#2563EB"));
        }

        return new SolidColorBrush(Color.Parse("#DBEAFE"));
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}