using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinPDF.ViewModels;

namespace WinPDF.Controls;

/// <summary>
/// Virtualizing multi-column grid layout for PDF thumbnails that preserves each row's true aspect-ratio height
/// while avoiding native <see cref="UniformGridLayout"/> fixed-row-height clipping and divide-by-zero crashes (microsoft-ui-xaml #10539).
/// </summary>
public sealed class SafeUniformGridLayout : VirtualizingLayout
{
    private const double CaptionAndPaddingHeight = 26.0;
    private const double RealizationBufferDip = 240.0;

    private readonly record struct RealizedItem(int Index, int Row, int Column);

    private readonly List<double> _rowTops = new();
    private readonly List<double> _rowHeights = new();
    private readonly List<RealizedItem> _realizedItems = new();
    private readonly HashSet<int> _activeRealizedIndices = new();
    private readonly HashSet<int> _currentPassIndices = new();
    private double _lastCellWidth = 100.0;

    /// <summary>
    /// Gets or sets the minimum item width in DIPs.
    /// </summary>
    public double MinItemWidth { get; set; } = 50.0;

    /// <summary>
    /// Gets or sets the minimum item height floor in DIPs.
    /// </summary>
    public double MinItemHeight { get; set; } = 40.0;

    /// <summary>
    /// Gets or sets the vertical spacing between rows in DIPs.
    /// </summary>
    public double MinRowSpacing { get; set; } = 8.0;

    /// <summary>
    /// Gets or sets the horizontal spacing between columns in DIPs.
    /// </summary>
    public double MinColumnSpacing { get; set; } = 10.0;

    /// <summary>
    /// Gets or sets the item stretch mode.
    /// </summary>
    public UniformGridLayoutItemsStretch ItemsStretch { get; set; } = UniformGridLayoutItemsStretch.Fill;

    /// <summary>
    /// Gets the maximum number of columns per row.
    /// </summary>
    public int MaximumRowsOrColumns { get; set; } = 2;

    /// <summary>
    /// Gets the total computed content height of all rows including spacing.
    /// </summary>
    public double TotalContentHeight
    {
        get
        {
            if (_rowTops.Count > 0 && _rowHeights.Count == _rowTops.Count)
            {
                int last = _rowTops.Count - 1;
                return _rowTops[last] + _rowHeights[last];
            }
            return 0.0;
        }
    }

    /// <summary>
    /// Pre-computes row vertical positions from the current thumbnail view models and invalidates layout measurement.
    /// </summary>
    /// <param name="items">The collection of thumbnail items.</param>
    public void UpdateRowMetrics(IReadOnlyList<ThumbnailItemViewModel>? items)
    {
        _rowTops.Clear();
        _rowHeights.Clear();

        if (items is { Count: > 0 })
        {
            int cols = Math.Max(1, MaximumRowsOrColumns);
            double rowSpacing = Math.Max(0.0, MinRowSpacing);
            int rowCount = (items.Count + cols - 1) / cols;
            double currentY = 0.0;

            for (int r = 0; r < rowCount; r++)
            {
                int firstIdx = r * cols;
                int lastIdx = Math.Min(items.Count - 1, firstIdx + cols - 1);
                double rowH = Math.Max(36.0, MinItemHeight);

                for (int i = firstIdx; i <= lastIdx; i++)
                {
                    double h = items[i].HeightDip;
                    if (h > 0)
                    {
                        rowH = Math.Max(rowH, Math.Ceiling(h + CaptionAndPaddingHeight));
                    }
                }

                _rowTops.Add(currentY);
                _rowHeights.Add(rowH);
                currentY += rowH + (r < rowCount - 1 ? rowSpacing : 0.0);
            }
        }

        InvalidateMeasure();
    }

    /// <summary>
    /// Invalidates the layout measure pass when thumbnail card dimensions or column counts change.
    /// </summary>
    public void InvalidateLayout() => InvalidateMeasure();

    /// <summary>
    /// Computes the vertical bounds (<c>rowTop</c>, <c>rowHeight</c>, and <c>centerY</c>) for a fractional item index.
    /// </summary>
    /// <param name="fractionalItemIndex">Fractional zero-based page index.</param>
    /// <param name="fallbackRowHeight">Fallback row height if layout has not yet measured.</param>
    /// <param name="centerY">Computed vertical center coordinate in repeater space.</param>
    /// <param name="rowTop">Computed top coordinate of the target row.</param>
    /// <param name="rowHeight">Computed height of the target row.</param>
    public void GetRowVerticalBounds(
        double fractionalItemIndex,
        double fallbackRowHeight,
        out double centerY,
        out double rowTop,
        out double rowHeight)
    {
        int cols = Math.Max(1, MaximumRowsOrColumns);
        double fracRow = Math.Max(0.0, fractionalItemIndex / cols);
        int rowIdx = (int)Math.Floor(fracRow);
        double rowFraction = fracRow - rowIdx;

        if (_rowTops.Count > 0 && _rowHeights.Count == _rowTops.Count)
        {
            int clampedRow = Math.Clamp(rowIdx, 0, _rowTops.Count - 1);
            rowTop = _rowTops[clampedRow];
            rowHeight = _rowHeights[clampedRow];

            double currentCenterY = rowTop + (rowHeight * 0.5);
            double nextCenterY = (clampedRow < _rowTops.Count - 1)
                ? _rowTops[clampedRow + 1] + (_rowHeights[clampedRow + 1] * 0.5)
                : rowTop + rowHeight + MinRowSpacing + (rowHeight * 0.5);

            centerY = currentCenterY + (rowFraction * (nextCenterY - currentCenterY));
            return;
        }

        rowHeight = fallbackRowHeight > 40.0 ? fallbackRowHeight : 180.0;
        rowTop = rowIdx * (rowHeight + MinRowSpacing);
        centerY = rowTop + (rowHeight * 0.5) + (rowFraction * (rowHeight + MinRowSpacing));
    }

    /// <inheritdoc/>
    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args)
    {
        base.OnItemsChangedCore(context, source, args);
        _rowTops.Clear();
        _rowHeights.Clear();
        _activeRealizedIndices.Clear();
        InvalidateMeasure();
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        _rowTops.Clear();
        _rowHeights.Clear();
        _realizedItems.Clear();

        if (context is null || availableSize.Width <= 0 || double.IsNaN(availableSize.Width))
        {
            return new Size(0, 0);
        }

        int itemCount = context.ItemCount;
        if (itemCount <= 0)
        {
            return new Size(0, 0);
        }

        double availW = double.IsInfinity(availableSize.Width) ? 240.0 : availableSize.Width;
        int cols = Math.Max(1, MaximumRowsOrColumns);
        double colSpacing = cols > 1 ? Math.Max(0.0, MinColumnSpacing) : 0.0;
        double rowSpacing = Math.Max(0.0, MinRowSpacing);

        double cellW = Math.Max(32.0, (availW - (colSpacing * (cols - 1))) / cols);
        _lastCellWidth = cellW;

        Rect realizationRect = context.RealizationRect;
        double realizeTop = -RealizationBufferDip;
        double realizeBottom = 1200.0 + RealizationBufferDip;

        if (!realizationRect.IsEmpty && double.IsFinite(realizationRect.Y) && double.IsFinite(realizationRect.Height) && realizationRect.Height > 0)
        {
            realizeTop = realizationRect.Y - RealizationBufferDip;
            realizeBottom = realizationRect.Y + realizationRect.Height + RealizationBufferDip;
        }

        int rowCount = (itemCount + cols - 1) / cols;
        double currentY = 0.0;

        for (int r = 0; r < rowCount; r++)
        {
            int firstIdx = r * cols;
            int lastIdx = Math.Min(itemCount - 1, firstIdx + cols - 1);

            double estimatedRowH = Math.Max(36.0, MinItemHeight);
            for (int i = firstIdx; i <= lastIdx; i++)
            {
                try
                {
                    if (context.GetItemAt(i) is ThumbnailItemViewModel vm && vm.HeightDip > 0)
                    {
                        estimatedRowH = Math.Max(estimatedRowH, Math.Ceiling(vm.HeightDip + CaptionAndPaddingHeight));
                    }
                }
                catch
                {
                }
            }

            bool shouldRealize = (currentY + estimatedRowH >= realizeTop) && (currentY <= realizeBottom);
            double actualRowH = estimatedRowH;

            if (shouldRealize)
            {
                for (int i = firstIdx; i <= lastIdx; i++)
                {
                    try
                    {
                        UIElement element = context.GetOrCreateElementAt(i);
                        if (context.GetItemAt(i) is ThumbnailItemViewModel itemVm && element is FrameworkElement fe)
                        {
                            if (!ReferenceEquals(fe.DataContext, itemVm))
                            {
                                fe.DataContext = itemVm;
                                Microsoft.UI.Xaml.Markup.XamlBindingHelper.GetDataTemplateComponent(fe)?.ProcessBindings(itemVm, i, 0, out _);
                            }

                            if (fe is Panel rootPanel &&
                                rootPanel.Children.Count > 0 &&
                                rootPanel.Children[0] is Border cardBorder &&
                                itemVm.WidthDip > 0 &&
                                itemVm.HeightDip > 0)
                            {
                                cardBorder.Width = itemVm.WidthDip;
                                cardBorder.Height = itemVm.HeightDip;
                            }
                        }

                        element.Measure(new Size(cellW, double.PositiveInfinity));
                        if (double.IsFinite(element.DesiredSize.Height) && element.DesiredSize.Height > 0)
                        {
                            actualRowH = Math.Max(actualRowH, Math.Ceiling(element.DesiredSize.Height));
                        }

                        _realizedItems.Add(new RealizedItem(i, r, i - firstIdx));
                        _currentPassIndices.Add(i);

                        if (context.GetItemAt(i) is ThumbnailItemViewModel realizedVm &&
                            (!realizedVm.IsRealized || (realizedVm.LoadCancellation is null && (realizedVm.Image is null || realizedVm.Image.IsDisposed))))
                        {
                            double scale = element.XamlRoot?.RasterizationScale ?? 1.0;
                            realizedVm.NotifyRealized(scale);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            _rowTops.Add(currentY);
            _rowHeights.Add(actualRowH);

            currentY += actualRowH;
            if (r < rowCount - 1)
            {
                currentY += rowSpacing;
            }
        }

        foreach (int prevIndex in _activeRealizedIndices)
        {
            if (!_currentPassIndices.Contains(prevIndex) && prevIndex >= 0 && prevIndex < itemCount)
            {
                try
                {
                    if (context.GetItemAt(prevIndex) is ThumbnailItemViewModel offscreenVm)
                    {
                        offscreenVm.NotifyRecycled();
                    }
                }
                catch
                {
                }
            }
        }

        _activeRealizedIndices.Clear();
        foreach (int idx in _currentPassIndices)
        {
            _activeRealizedIndices.Add(idx);
        }
        _currentPassIndices.Clear();

        return new Size(availW, Math.Max(0.0, currentY));
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (context is null || finalSize.Width <= 0 || double.IsNaN(finalSize.Width))
        {
            return new Size(0, 0);
        }

        int itemCount = context.ItemCount;
        if (itemCount <= 0 || _rowTops.Count == 0)
        {
            return new Size(finalSize.Width, 0);
        }

        int cols = Math.Max(1, MaximumRowsOrColumns);
        double colSpacing = cols > 1 ? Math.Max(0.0, MinColumnSpacing) : 0.0;
        double cellW = _lastCellWidth > 0
            ? _lastCellWidth
            : Math.Max(32.0, (finalSize.Width - (colSpacing * (cols - 1))) / cols);

        foreach (RealizedItem item in _realizedItems)
        {
            if (item.Index >= 0 && item.Index < itemCount && item.Row >= 0 && item.Row < _rowTops.Count)
            {
                try
                {
                    UIElement element = context.GetOrCreateElementAt(item.Index);
                    double x = item.Column * (cellW + colSpacing);
                    double y = _rowTops[item.Row];
                    double h = _rowHeights[item.Row];
                    element.Arrange(new Rect(x, y, cellW, h));
                }
                catch
                {
                }
            }
        }

        int lastRow = _rowTops.Count - 1;
        double totalHeight = _rowTops[lastRow] + _rowHeights[lastRow];
        return new Size(finalSize.Width, Math.Max(0.0, totalHeight));
    }
}
