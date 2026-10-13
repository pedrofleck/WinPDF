using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace WinPDF.Converters;

/// <summary>
/// Converts a boolean value to a <see cref="Visibility"/> value with optional inversion.
/// </summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// Gets or sets a value indicating whether the output visibility should be inverted.
    /// </summary>
    public bool IsInverted { get; set; }

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, string language)
    {
        bool flag = value is true;
        if (IsInverted)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, string language)
    {
        bool isVisible = value is Visibility.Visible;
        return IsInverted ? !isVisible : isVisible;
    }
}
