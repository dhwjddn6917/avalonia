using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MobileEssControl.Converters;

public sealed class WidthFractionConverter : IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        double width = value switch
        {
            double d => d,
            _ => 0
        };

        double fraction = 1.0;

        if (parameter is string text &&
            double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsedFraction))
        {
            fraction = parsedFraction;
        }

        return width * fraction;
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
