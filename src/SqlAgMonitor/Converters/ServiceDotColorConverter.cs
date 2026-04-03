using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SqlAgMonitor.Converters;

public class ServiceDotColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var connected = value is true;
        return connected
            ? new SolidColorBrush(Color.Parse("#FF4CAF50"))
            : new SolidColorBrush(Color.Parse("#FFFF5252"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
