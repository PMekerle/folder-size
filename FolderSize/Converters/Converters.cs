using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using FolderSize.Models;
using FolderSize.ViewModels;

namespace FolderSize.Converters;

public sealed class MetricValueConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return "";
        if (values[0] is not long val) return "";
        if (values[1] is not Metric metric) return "";

        return metric switch
        {
            Metric.Size => MainViewModel.FormatBytes(val),
            Metric.SizeOnDisk => MainViewModel.FormatBytes(val),
            Metric.FileCount => $"{val:N0} files",
            _ => "",
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool bb && bb;
        if (parameter is string s && s == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class BarFractionToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return 0.0;
        double fraction = values[0] is double f ? f : 0.0;
        double totalWidth = values[1] is double w ? w : 0.0;
        if (double.IsNaN(totalWidth) || totalWidth <= 0) return 0.0;
        var result = totalWidth * Math.Clamp(fraction, 0.0, 1.0);
        return double.IsNaN(result) || result < 0 ? 0.0 : result;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
