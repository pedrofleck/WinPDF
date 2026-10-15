using System;
using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinPDF.Controls;

/// <summary>
/// Hardened UniformGridLayout that prevents native integer divide-by-zero crashes (microsoft-ui-xaml #10539)
/// by ensuring that available sizes passed to the native layout engine are always strictly greater than
/// the minimum item size plus spacing.
/// </summary>
public sealed class SafeUniformGridLayout : UniformGridLayout
{
    /// <inheritdoc/>
    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        if (context is null || availableSize.Width <= 0 || availableSize.Height <= 0)
        {
            return new Size(0, 0);
        }

        // UniformGridLayout has a known bug in WinUI 3 (microsoft/microsoft-ui-xaml issue #10539):
        // itemsPerLine is calculated via integer division:
        //   itemsPerLine = (availableMinor + spacing) / itemSizeWithSpacing;
        //   fullLinesCount = itemsCount / itemsPerLine; // DIVIDE BY ZERO (0x80020012) if itemsPerLine == 0!
        // To guarantee itemsPerLine is ALWAYS >= 1, the available size along the line axis MUST be at least
        // (MinItemWidth + MinColumnSpacing + 1.0) for horizontal orientation, or (MinItemHeight + MinRowSpacing + 1.0)
        // for vertical orientation.
        double minWidth = Math.Max(50.0, MinItemWidth + MinColumnSpacing + 1.0);
        double minHeight = Math.Max(50.0, MinItemHeight + MinRowSpacing + 1.0);

        double w = double.IsNaN(availableSize.Width) || double.IsInfinity(availableSize.Width)
            ? availableSize.Width
            : Math.Max(availableSize.Width, minWidth);

        double h = double.IsNaN(availableSize.Height) || double.IsInfinity(availableSize.Height)
            ? availableSize.Height
            : Math.Max(availableSize.Height, minHeight);

        Size safeSize = new(w, h);

        try
        {
            return base.MeasureOverride(context, safeSize);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SafeUniformGridLayout] MeasureOverride safely suppressed native exception: {ex.Message}");
            return new Size(0, 0);
        }
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (context is null || finalSize.Width <= 0 || finalSize.Height <= 0)
        {
            return new Size(0, 0);
        }

        double minWidth = Math.Max(50.0, MinItemWidth + MinColumnSpacing + 1.0);
        double minHeight = Math.Max(50.0, MinItemHeight + MinRowSpacing + 1.0);

        double w = double.IsNaN(finalSize.Width) || double.IsInfinity(finalSize.Width)
            ? finalSize.Width
            : Math.Max(finalSize.Width, minWidth);

        double h = double.IsNaN(finalSize.Height) || double.IsInfinity(finalSize.Height)
            ? finalSize.Height
            : Math.Max(finalSize.Height, minHeight);

        Size safeSize = new(w, h);

        try
        {
            return base.ArrangeOverride(context, safeSize);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SafeUniformGridLayout] ArrangeOverride safely suppressed native exception: {ex.Message}");
            return new Size(0, 0);
        }
    }
}
