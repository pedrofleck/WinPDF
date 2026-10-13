using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace WinPDF.Converters;

/// <summary>
/// Converts an enum value to a <see cref="Visibility"/> by comparing it to a parameter.
/// </summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, string language)
    {
        if (value is null || parameter is null)
        {
            return Visibility.Collapsed;
        }

        string checkValue = value.ToString() ?? string.Empty;
        string targetValue = parameter.ToString() ?? string.Empty;
        return string.Equals(checkValue, targetValue, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, string language)
    {
        return Microsoft.UI.Xaml.DependencyProperty.UnsetValue;
    }
}
