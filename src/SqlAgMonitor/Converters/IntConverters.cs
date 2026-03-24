using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace SqlAgMonitor;

public class IntEqualsConverter : IValueConverter
{
    private readonly int _target;

    public IntEqualsConverter(int target) => _target = target;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && i == _target;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class IntComparisonConverter : IValueConverter
{
    private readonly Func<int, bool> _predicate;

    public IntComparisonConverter(Func<int, bool> predicate) => _predicate = predicate;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && _predicate(i);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public static class IntEqualsZeroConverter
{
    public static readonly IValueConverter Instance = new IntEqualsConverter(0);
}

public static class IntEqualsOneConverter
{
    public static readonly IValueConverter Instance = new IntEqualsConverter(1);
}

public static class IntEqualsTwoConverter
{
    public static readonly IValueConverter Instance = new IntEqualsConverter(2);
}

public static class IntGreaterThanZeroConverter
{
    public static readonly IValueConverter Instance = new IntComparisonConverter(i => i > 0);
}

public static class IntLessThanTwoConverter
{
    public static readonly IValueConverter Instance = new IntComparisonConverter(i => i < 2);
}
