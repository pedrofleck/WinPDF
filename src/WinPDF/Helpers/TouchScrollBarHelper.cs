using Windows.Foundation;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace WinPDF.Helpers;

/// <summary>
/// Enhances native WinUI 3 <see cref="ScrollViewer"/> scrollbars for touchscreens by expanding
/// the touch/pen hit zone (without changing the visual 14 DIP scrollbar appearance or mouse behavior),
/// keeping the interactive thumb available on touch contact, and providing smooth thumb scrubbing.
/// </summary>
public sealed class TouchScrollBarHelper
{
    private const double VisualScrollBarThicknessDip = 14.0;
    private const double ArrowButtonLengthDip = 16.0;
    private const double ThumbTouchHaloDip = 42.0;

    private readonly ScrollViewer _scrollViewer;
    private readonly double _touchHitThicknessDip;
    private readonly Action? _onTouchScrollStarted;

    private ScrollBar? _verticalScrollBar;
    private ScrollBar? _horizontalScrollBar;

    private uint? _vTouchPointerId;
    private double _vDragStartY;
    private double _vDragStartOffset;
    private double _vUsableTrackLength = 1.0;

    private uint? _hTouchPointerId;
    private double _hDragStartX;
    private double _hDragStartOffset;
    private double _hUsableTrackLength = 1.0;

    private TouchScrollBarHelper(ScrollViewer scrollViewer, double touchHitThicknessDip, Action? onTouchScrollStarted)
    {
        _scrollViewer = scrollViewer;
        _touchHitThicknessDip = Math.Max(VisualScrollBarThicknessDip, touchHitThicknessDip);
        _onTouchScrollStarted = onTouchScrollStarted;

        _scrollViewer.Loaded += OnScrollViewerLoaded;
        _scrollViewer.SizeChanged += OnScrollViewerSizeChanged;
        TryHookScrollBars();
    }

    /// <summary>
    /// Attaches touch-ergonomic scrollbar hit targets and scrubbing behavior to the specified <see cref="ScrollViewer"/>.
    /// </summary>
    /// <param name="scrollViewer">The target scroll viewer.</param>
    /// <param name="touchHitThicknessDip">Total hit-test width/height in DIPs for touch/pen input.</param>
    /// <param name="onTouchScrollStarted">Optional callback invoked when a user initiates a touch scrollbar drag.</param>
    /// <returns>The attached helper instance.</returns>
    public static TouchScrollBarHelper Attach(
        ScrollViewer scrollViewer,
        double touchHitThicknessDip = 28.0,
        Action? onTouchScrollStarted = null)
    {
        return new TouchScrollBarHelper(scrollViewer, touchHitThicknessDip, onTouchScrollStarted);
    }

    private void OnScrollViewerLoaded(object sender, RoutedEventArgs e)
    {
        TryHookScrollBars();
    }

    private void OnScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        TryHookScrollBars();
    }

    private void TryHookScrollBars()
    {
        _scrollViewer.ApplyTemplate();

        if (_verticalScrollBar is null)
        {
            _verticalScrollBar = FindDescendant<ScrollBar>(_scrollViewer, "VerticalScrollBar");
            if (_verticalScrollBar is not null)
            {
                ConfigureVerticalScrollBar(_verticalScrollBar);
            }
        }
        else
        {
            ApplyVerticalScrollBarTemplateMetrics(_verticalScrollBar);
        }

        if (_horizontalScrollBar is null)
        {
            _horizontalScrollBar = FindDescendant<ScrollBar>(_scrollViewer, "HorizontalScrollBar");
            if (_horizontalScrollBar is not null)
            {
                ConfigureHorizontalScrollBar(_horizontalScrollBar);
            }
        }
        else
        {
            ApplyHorizontalScrollBarTemplateMetrics(_horizontalScrollBar);
        }
    }

    private void ConfigureVerticalScrollBar(ScrollBar bar)
    {
        bar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
        bar.Width = _touchHitThicknessDip;
        bar.MinWidth = _touchHitThicknessDip;
        bar.Loaded += (_, _) => ApplyVerticalScrollBarTemplateMetrics(bar);
        bar.SizeChanged += (_, _) => ApplyVerticalScrollBarTemplateMetrics(bar);
        bar.LayoutUpdated += (_, _) =>
        {
            if (bar.IndicatorMode != ScrollingIndicatorMode.MouseIndicator)
            {
                bar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
            }
        };

        bar.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnVerticalBarPointerPressed), handledEventsToo: true);
        bar.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnVerticalBarPointerMoved), handledEventsToo: true);
        bar.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnVerticalBarPointerReleased), handledEventsToo: true);
        bar.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnVerticalBarPointerReleased), handledEventsToo: true);
        bar.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnVerticalBarPointerReleased), handledEventsToo: true);

        ApplyVerticalScrollBarTemplateMetrics(bar);
    }

    private void ApplyVerticalScrollBarTemplateMetrics(ScrollBar bar)
    {
        bar.ApplyTemplate();
        bar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
        bar.Width = _touchHitThicknessDip;
        bar.MinWidth = _touchHitThicknessDip;

        if (FindDescendant<Grid>(bar, "Root") is { } rootGrid)
        {
            rootGrid.Background = new SolidColorBrush(Colors.Transparent);
            rootGrid.Width = _touchHitThicknessDip;
            rootGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        if (FindDescendant<FrameworkElement>(bar, "VerticalRoot") is { } verticalRoot)
        {
            verticalRoot.Width = VisualScrollBarThicknessDip;
            verticalRoot.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private void ConfigureHorizontalScrollBar(ScrollBar bar)
    {
        bar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
        bar.Height = _touchHitThicknessDip;
        bar.MinHeight = _touchHitThicknessDip;
        bar.Loaded += (_, _) => ApplyHorizontalScrollBarTemplateMetrics(bar);
        bar.SizeChanged += (_, _) => ApplyHorizontalScrollBarTemplateMetrics(bar);
        bar.LayoutUpdated += (_, _) =>
        {
            if (bar.IndicatorMode != ScrollingIndicatorMode.MouseIndicator)
            {
                bar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
            }
        };

        bar.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnHorizontalBarPointerPressed), handledEventsToo: true);
        bar.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnHorizontalBarPointerMoved), handledEventsToo: true);
        bar.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnHorizontalBarPointerReleased), handledEventsToo: true);
        bar.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnHorizontalBarPointerReleased), handledEventsToo: true);
        bar.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnHorizontalBarPointerReleased), handledEventsToo: true);

        ApplyHorizontalScrollBarTemplateMetrics(bar);
    }

    private void ApplyHorizontalScrollBarTemplateMetrics(ScrollBar bar)
    {
        bar.ApplyTemplate();
        bar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
        bar.Height = _touchHitThicknessDip;
        bar.MinHeight = _touchHitThicknessDip;

        if (FindDescendant<Grid>(bar, "Root") is { } rootGrid)
        {
            rootGrid.Background = new SolidColorBrush(Colors.Transparent);
            rootGrid.Height = _touchHitThicknessDip;
            rootGrid.VerticalAlignment = VerticalAlignment.Stretch;
        }

        if (FindDescendant<FrameworkElement>(bar, "HorizontalRoot") is { } horizontalRoot)
        {
            horizontalRoot.Height = VisualScrollBarThicknessDip;
            horizontalRoot.VerticalAlignment = VerticalAlignment.Bottom;
        }
    }

    private void OnVerticalBarPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse ||
            _verticalScrollBar is null ||
            _scrollViewer.ScrollableHeight <= 0.5)
        {
            return;
        }

        _onTouchScrollStarted?.Invoke();
        _verticalScrollBar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
        VisualStateManager.GoToState(_scrollViewer, "MouseIndicator", true);
        VisualStateManager.GoToState(_verticalScrollBar, "Expanded", true);

        Point pt = e.GetCurrentPoint(_verticalScrollBar).Position;
        double barHeight = Math.Max(48.0, _verticalScrollBar.ActualHeight);
        double trackTop = ArrowButtonLengthDip;
        double trackBottom = Math.Max(trackTop + 24.0, barHeight - ArrowButtonLengthDip);
        double trackHeight = trackBottom - trackTop;

        double viewportRatio = _scrollViewer.ExtentHeight > 0
            ? Math.Clamp(_scrollViewer.ViewportHeight / _scrollViewer.ExtentHeight, 0.05, 1.0)
            : 0.2;
        double thumbHeight = Math.Clamp(trackHeight * viewportRatio, 18.0, trackHeight);

        if (FindDescendant<FrameworkElement>(_verticalScrollBar, "VerticalThumb") is { ActualHeight: > 8.0 } actualThumb)
        {
            thumbHeight = actualThumb.ActualHeight;
        }

        _vUsableTrackLength = Math.Max(1.0, trackHeight - thumbHeight);
        double currentRatio = Math.Clamp(_scrollViewer.VerticalOffset / Math.Max(1.0, _scrollViewer.ScrollableHeight), 0.0, 1.0);
        double thumbTop = trackTop + (currentRatio * _vUsableTrackLength);
        double thumbBottom = thumbTop + thumbHeight;

        _vTouchPointerId = e.Pointer.PointerId;
        _verticalScrollBar.CapturePointer(e.Pointer);
        e.Handled = true;

        if (pt.Y >= thumbTop - ThumbTouchHaloDip && pt.Y <= thumbBottom + ThumbTouchHaloDip)
        {
            // Finger landed on or near the thumb: grab it in-place with zero jump
            _vDragStartY = pt.Y;
            _vDragStartOffset = _scrollViewer.VerticalOffset;
        }
        else
        {
            // Finger tapped the scrollbar track away from the thumb: center thumb at finger and scrub immediately
            double targetRatio = Math.Clamp((pt.Y - trackTop - (thumbHeight / 2.0)) / _vUsableTrackLength, 0.0, 1.0);
            double targetOffset = targetRatio * _scrollViewer.ScrollableHeight;
            _vDragStartY = pt.Y;
            _vDragStartOffset = targetOffset;
            _scrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);
        }
    }

    private void OnVerticalBarPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_vTouchPointerId != e.Pointer.PointerId || _verticalScrollBar is null || _scrollViewer.ScrollableHeight <= 0.5)
        {
            return;
        }

        Point pt = e.GetCurrentPoint(_verticalScrollBar).Position;
        double deltaY = pt.Y - _vDragStartY;
        double deltaOffset = (deltaY / Math.Max(1.0, _vUsableTrackLength)) * _scrollViewer.ScrollableHeight;
        double targetOffset = Math.Clamp(_vDragStartOffset + deltaOffset, 0.0, _scrollViewer.ScrollableHeight);

        _scrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);
        e.Handled = true;
    }

    private void OnVerticalBarPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_vTouchPointerId == e.Pointer.PointerId)
        {
            _vTouchPointerId = null;
            _verticalScrollBar?.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnHorizontalBarPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse ||
            _horizontalScrollBar is null ||
            _scrollViewer.ScrollableWidth <= 0.5)
        {
            return;
        }

        _onTouchScrollStarted?.Invoke();
        _horizontalScrollBar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
        VisualStateManager.GoToState(_scrollViewer, "MouseIndicator", true);
        VisualStateManager.GoToState(_horizontalScrollBar, "Expanded", true);

        Point pt = e.GetCurrentPoint(_horizontalScrollBar).Position;
        double barWidth = Math.Max(48.0, _horizontalScrollBar.ActualWidth);
        double trackLeft = ArrowButtonLengthDip;
        double trackRight = Math.Max(trackLeft + 24.0, barWidth - ArrowButtonLengthDip);
        double trackWidth = trackRight - trackLeft;

        double viewportRatio = _scrollViewer.ExtentWidth > 0
            ? Math.Clamp(_scrollViewer.ViewportWidth / _scrollViewer.ExtentWidth, 0.05, 1.0)
            : 0.2;
        double thumbWidth = Math.Clamp(trackWidth * viewportRatio, 18.0, trackWidth);

        if (FindDescendant<FrameworkElement>(_horizontalScrollBar, "HorizontalThumb") is { ActualWidth: > 8.0 } actualThumb)
        {
            thumbWidth = actualThumb.ActualWidth;
        }

        _hUsableTrackLength = Math.Max(1.0, trackWidth - thumbWidth);
        double currentRatio = Math.Clamp(_scrollViewer.HorizontalOffset / Math.Max(1.0, _scrollViewer.ScrollableWidth), 0.0, 1.0);
        double thumbLeft = trackLeft + (currentRatio * _hUsableTrackLength);
        double thumbRight = thumbLeft + thumbWidth;

        _hTouchPointerId = e.Pointer.PointerId;
        _horizontalScrollBar.CapturePointer(e.Pointer);
        e.Handled = true;

        if (pt.X >= thumbLeft - ThumbTouchHaloDip && pt.X <= thumbRight + ThumbTouchHaloDip)
        {
            _hDragStartX = pt.X;
            _hDragStartOffset = _scrollViewer.HorizontalOffset;
        }
        else
        {
            double targetRatio = Math.Clamp((pt.X - trackLeft - (thumbWidth / 2.0)) / _hUsableTrackLength, 0.0, 1.0);
            double targetOffset = targetRatio * _scrollViewer.ScrollableWidth;
            _hDragStartX = pt.X;
            _hDragStartOffset = targetOffset;
            _scrollViewer.ChangeView(targetOffset, null, null, disableAnimation: true);
        }
    }

    private void OnHorizontalBarPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_hTouchPointerId != e.Pointer.PointerId || _horizontalScrollBar is null || _scrollViewer.ScrollableWidth <= 0.5)
        {
            return;
        }

        Point pt = e.GetCurrentPoint(_horizontalScrollBar).Position;
        double deltaX = pt.X - _hDragStartX;
        double deltaOffset = (deltaX / Math.Max(1.0, _hUsableTrackLength)) * _scrollViewer.ScrollableWidth;
        double targetOffset = Math.Clamp(_hDragStartOffset + deltaOffset, 0.0, _scrollViewer.ScrollableWidth);

        _scrollViewer.ChangeView(targetOffset, null, null, disableAnimation: true);
        e.Handled = true;
    }

    private void OnHorizontalBarPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_hTouchPointerId == e.Pointer.PointerId)
        {
            _hTouchPointerId = null;
            _horizontalScrollBar?.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name)
            {
                return match;
            }

            T? nested = FindDescendant<T>(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
