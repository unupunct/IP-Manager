using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using IPManager.Core;

namespace IPManager.App.Infrastructure;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visibleWhenNull = string.Equals(parameter?.ToString(), "invert", StringComparison.OrdinalIgnoreCase);
        var isNull = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        return isNull == visibleWhenNull ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = value is null || (value is string s && string.IsNullOrWhiteSpace(s)) || (value is int i && i == 0);
        var invert = string.Equals(parameter?.ToString(), "invert", StringComparison.OrdinalIgnoreCase);
        if (invert) empty = !empty;
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var parts = (parameter?.ToString() ?? "Yes|No").Split('|');
        return value is bool b && b ? parts[0] : parts.Length > 1 ? parts[1] : "";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class EnumTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return "";
        var text = value.ToString() ?? "";
        if (value is DeviceCategory category) return DeviceClassifier.DisplayName(category);
        return string.Concat(text.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class UtcToLocalConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DateTime dt)
        {
            var local = dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
            return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        }
        if (value is DateTimeOffset dto) return dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        return "";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is AlertSeverity severity ? Brush(severity) : Brush(AlertSeverity.Info);

    private static SolidColorBrush Brush(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        AlertSeverity.Warning => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        _ => new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8))
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class IpStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Brush(value is IpStatus s ? s : IpStatus.Unknown);

    public static SolidColorBrush Brush(IpStatus status) => status switch
    {
        IpStatus.Assigned => new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
        IpStatus.Reserved => new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)),
        IpStatus.Excluded => new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
        IpStatus.Gateway => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
        IpStatus.Conflict => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        IpStatus.Available => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
        _ => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DeviceStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value is DeviceStatus s ? s : DeviceStatus.Unknown;
        return new SolidColorBrush(status switch
        {
            DeviceStatus.Online => Color.FromRgb(0x22, 0xC5, 0x5E),
            DeviceStatus.Offline => Color.FromRgb(0xEF, 0x44, 0x44),
            _ => Color.FromRgb(0x94, 0xA3, 0xB8)
        });
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ConflictStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = value is ConflictState s ? s : ConflictState.Suspected;
        return new SolidColorBrush(state switch
        {
            ConflictState.Confirmed => Color.FromRgb(0xEF, 0x44, 0x44),
            ConflictState.Resolved => Color.FromRgb(0x22, 0xC5, 0x5E),
            _ => Color.FromRgb(0xF5, 0x9E, 0x0B)
        });
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ConfidenceBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var confidence = value is int i ? i : 0;
        return new SolidColorBrush(confidence switch
        {
            >= 90 => Color.FromRgb(0xEF, 0x44, 0x44),
            >= 60 => Color.FromRgb(0xF5, 0x9E, 0x0B),
            _ => Color.FromRgb(0x94, 0xA3, 0xB8)
        });
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (string.Equals(parameter?.ToString(), "invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
