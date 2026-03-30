using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SqlAgMonitor.Converters;

/// <summary>
/// Converts a UTC DateTimeOffset to a local-time formatted string.
/// Pass the desired format string as the converter parameter.
/// </summary>
public class UtcToLocalConverter : IValueConverter
{
    public static readonly UtcToLocalConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var format = parameter as string ?? "yyyy-MM-dd HH:mm:ss";

        return value switch
        {
            DateTimeOffset dto => dto.ToLocalTime().ToString(format, CultureInfo.CurrentCulture),
            DateTime dt => dt.Kind == DateTimeKind.Utc
                ? dt.ToLocalTime().ToString(format, CultureInfo.CurrentCulture)
                : dt.ToString(format, CultureInfo.CurrentCulture),
            _ => value?.ToString()
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
