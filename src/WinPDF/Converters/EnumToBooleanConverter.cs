using Microsoft.UI.Xaml.Data;

namespace WinPDF.Converters;

/// <summary>
/// Converts an enum value to a boolean by comparing it to a string parameter.
/// </summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, string language)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        string checkValue = value.ToString() ?? string.Empty;
        string targetValue = parameter.ToString() ?? string.Empty;
        return string.Equals(checkValue, targetValue, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, string language)
    {
        if (value is true && parameter is string paramString && targetType.IsEnum)
        {
            return Enum.Parse(targetType, paramString);
        }

        return Microsoft.UI.Xaml.DependencyProperty.UnsetValue;
    }
}
