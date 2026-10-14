using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using WinPDF.ViewModels;

namespace WinPDF.Views.Sidebar;

/// <summary>
/// Sidebar tab page presenting document page thumbnails with dynamic responsive card sizing and smooth easing synchronization.
/// </summary>
public sealed partial class ThumbnailsTabPage : Page
{
    /// <summary>
    /// Identifies the <see cref="CurrentAnimatedScrollY"/> dependency property used for storyboard vertical offset animations.
    /// </summary>
    public static readonly DependencyProperty CurrentAnimatedScrollYProperty =
        DependencyProperty.Register(
            nameof(CurrentAnimatedScrollY),
            typeof(double),
            typeof(ThumbnailsTabPage),
            new PropertyMetadata(0.0, OnCurrentAnimatedScrollYChanged));

    /// <summary>
    /// Gets or sets the animated vertical scroll offset of the thumbnails scroll viewer.
    /// </summary>
    public double CurrentAnimatedScrollY
    {
        get => (double)GetValue(CurrentAnimatedScrollYProperty);
        set => SetValue(CurrentAnimatedScrollYProperty, value);
    }

    private static void OnCurrentAnimatedScrollYChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThumbnailsTabPage page && page.ThumbnailsScrollViewer is not null)
        {
            try
            {
                page.ThumbnailsScrollViewer.ChangeView(null, (double)e.NewValue, null, disableAnimation: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ChangeView safely caught in animation: {ex.Message}");
            }
        }
    }

    private readonly DispatcherTimer _userScrollDebounceTimer = new() { Interval = TimeSpan.FromSeconds(1.0) };
    private DocumentViewModel? _observedDocument;
    private Storyboard? _storyboard;
    private bool _isUserScrollingSidebar;
    private bool _isPointerOverSidebar;
    private bool _isFollowing;
    private bool _isStoryboardRunning;
    private double _targetThumbScrollY;
    private int _currentColCount = 2;
    private double _currentRowHeight = 182.0;

    /// <summary>
    /// Gets the shell view model.
    /// </summary>
    public ShellViewModel ViewModel { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ThumbnailsTabPage"/> class.
    /// </summary>
    public ThumbnailsTabPage()
    {
        ViewModel = App.CurrentApp.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _userScrollDebounceTimer.Tick += OnUserScrollDebounceTimerTick;
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ShellViewModel vm)
        {
            ViewModel = vm;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SubscribeToDocument(ViewModel.CurrentDocument);

        if (ThumbnailsScrollViewer.ActualWidth > 0)
        {
            UpdateThumbnailSizes(ThumbnailsScrollViewer.ActualWidth);
        }

        UpdateTargetScrollPosition(allowAnimation: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        UnsubscribeFromDocument();
        StopContinuousFollower();
        _storyboard?.Stop();
        _isStoryboardRunning = false;
        _userScrollDebounceTimer.Stop();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentDocument))
        {
            SubscribeToDocument(ViewModel.CurrentDocument);
            if (ThumbnailsScrollViewer.ActualWidth > 0)
            {
                UpdateThumbnailSizes(ThumbnailsScrollViewer.ActualWidth);
            }

            UpdateTargetScrollPosition(allowAnimation: false);
        }
    }

    private void SubscribeToDocument(DocumentViewModel? doc)
    {
        if (ReferenceEquals(_observedDocument, doc))
        {
            return;
        }

        UnsubscribeFromDocument();

        if (doc is not null)
        {
            _observedDocument = doc;
            _observedDocument.PropertyChanged += OnDocumentPropertyChanged;
        }
    }

    private void UnsubscribeFromDocument()
    {
        if (_observedDocument is not null)
        {
            _observedDocument.PropertyChanged -= OnDocumentPropertyChanged;
            _observedDocument = null;
        }
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.FractionalPageIndex) or nameof(DocumentViewModel.CurrentPageIndex))
        {
            // Scrolling the document resets any user scroll lock on sidebar and snaps into synchronization.
            _isUserScrollingSidebar = false;
            _userScrollDebounceTimer.Stop();
            UpdateTargetScrollPosition(allowAnimation: true);
        }
    }

    private double _lastHandledWidth;

    private void OnThumbnailsScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1.0 && e.NewSize.Width > 0)
        {
            UpdateThumbnailSizes(e.NewSize.Width);
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateTargetScrollPosition(allowAnimation: false);
            });
        }
    }

    private void UpdateThumbnailSizes(double scrollViewerWidth)
    {
        if (ViewModel.CurrentDocument is not { } doc || scrollViewerWidth <= 0 || ThumbnailsLayout is null)
        {
            return;
        }

        if (Math.Abs(_lastHandledWidth - scrollViewerWidth) < 2.0)
        {
            return;
        }

        _lastHandledWidth = scrollViewerWidth;

        // Total padding: 12 DIPs on each side = 24 DIPs, plus space for scrollbar ~8 DIPs = 32 DIPs
        double availableW = Math.Max(80.0, scrollViewerWidth - 32.0);

        // Threshold: 2 columns if sidebar width >= 270 DIPs; otherwise 1 large centered column.
        int numCols = scrollViewerWidth >= 270.0 ? 2 : 1;

        if (ThumbnailsLayout.MaximumRowsOrColumns != numCols)
        {
            ThumbnailsLayout.MaximumRowsOrColumns = numCols;
        }

        ThumbnailsLayout.ItemsStretch = UniformGridLayoutItemsStretch.Fill;
        // Keep MinItemWidth safe at 50 to prevent divide-by-zero crash in UniformGridLayout
        ThumbnailsLayout.MinItemWidth = 50.0;
        ThumbnailsLayout.MinItemHeight = 0.0;

        double colWidth = (numCols == 1)
            ? availableW
            : Math.Floor((availableW - 14.0) / 2.0);

        // Add generous horizontal padding to make thumbnails slightly smaller and breathe nicely
        double maxCardW = (numCols == 1)
            ? Math.Clamp(colWidth - 36.0, 50.0, 150.0)
            : Math.Max(50.0, colWidth - 18.0);

        _currentColCount = numCols;
        double maxCalculatedH = 0.0;

        for (int i = 0; i < doc.Thumbnails.Count; i++)
        {
            var thumb = doc.Thumbnails[i];
            double aspect = 0.707;
            if (i < doc.Pages.Count)
            {
                var p = doc.Pages[i];
                if (p.WidthPt > 0 && p.HeightPt > 0)
                {
                    aspect = p.WidthPt / p.HeightPt;
                }
            }

            double w = maxCardW;
            double h = Math.Round(w / aspect);

            if (h > 300.0)
            {
                h = 300.0;
                w = Math.Clamp(Math.Round(h * aspect), 50.0, maxCardW);
            }
            else if (h < 50.0)
            {
                h = 50.0;
                w = Math.Clamp(Math.Round(h * aspect), 50.0, maxCardW);
            }

            thumb.WidthDip = w;
            thumb.HeightDip = h;

            if (h > maxCalculatedH)
            {
                maxCalculatedH = h;
            }
        }

        _currentRowHeight = (maxCalculatedH > 40.0 ? maxCalculatedH : 140.0) + 30.0; // card + spacing(4) + text(18) + rowSpacing(8)
    }

    private void UpdateTargetScrollPosition(bool allowAnimation = false)
    {
        if (_isUserScrollingSidebar || ViewModel.CurrentDocument is not { PageCount: > 0 } doc || ThumbnailsScrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        int cols = Math.Max(1, _currentColCount);
        double rowHeight = _currentRowHeight > 50.0 ? _currentRowHeight : 180.0;

        double rowIndex = doc.FractionalPageIndex / cols;
        double targetCenterY = 8.0 + (rowIndex * rowHeight) + (rowHeight / 2.0);
        double targetScrollY = Math.Clamp(targetCenterY - (ThumbnailsScrollViewer.ViewportHeight / 2.0), 0, Math.Max(0, ThumbnailsScrollViewer.ScrollableHeight));

        _targetThumbScrollY = targetScrollY;

        double curY = ThumbnailsScrollViewer.VerticalOffset;
        double viewportH = ThumbnailsScrollViewer.ViewportHeight;

        if (!allowAnimation)
        {
            StopContinuousFollower();
            _storyboard?.Stop();
            _isStoryboardRunning = false;
            try
            {
                ThumbnailsScrollViewer.ChangeView(null, targetScrollY, null, disableAnimation: true);
            }
            catch { }
            return;
        }

        // Check if the target page is currently visible in the sidebar viewport:
        double cardTop = 8.0 + (rowIndex * rowHeight);
        double cardBottom = cardTop + rowHeight;
        bool isCardVisible = (cardBottom >= curY && cardTop <= curY + viewportH);

        if (!isCardVisible)
        {
            // Target is not visible: rapid animation with ExponentialEase!
            StopContinuousFollower();
            AnimateWithExponentialEase(curY, targetScrollY);
        }
        else
        {
            // Target is visible: use continuous smooth follower
            if (!_isStoryboardRunning)
            {
                StartContinuousFollower();
            }
        }
    }

    private void AnimateWithExponentialEase(double fromY, double toY)
    {
        if (Math.Abs(fromY - toY) < 1.0)
        {
            try
            {
                ThumbnailsScrollViewer.ChangeView(null, toY, null, disableAnimation: true);
            }
            catch { }
            return;
        }

        try
        {
            _storyboard?.Stop();

            CurrentAnimatedScrollY = fromY;

            DoubleAnimation anim = new()
            {
                From = fromY,
                To = toY,
                Duration = new Duration(TimeSpan.FromMilliseconds(280)),
                EnableDependentAnimation = true,
                EasingFunction = new ExponentialEase { Exponent = 4.5, EasingMode = EasingMode.EaseOut }
            };

            Storyboard.SetTarget(anim, this);
            Storyboard.SetTargetProperty(anim, nameof(CurrentAnimatedScrollY));

            Storyboard sb = new();
            sb.Children.Add(anim);
            _storyboard = sb;
            _isStoryboardRunning = true;
            sb.Completed += (_, _) =>
            {
                _isStoryboardRunning = false;
                _storyboard = null;
            };
            sb.Begin();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AnimateWithExponentialEase error: {ex.Message}");
            _isStoryboardRunning = false;
            try
            {
                ThumbnailsScrollViewer.ChangeView(null, toY, null, disableAnimation: true);
            }
            catch { }
        }
    }

    private void StartContinuousFollower()
    {
        if (!_isFollowing)
        {
            _isFollowing = true;
            CompositionTarget.Rendering += OnCompositionTargetRendering;
        }
    }

    private void StopContinuousFollower()
    {
        if (_isFollowing)
        {
            _isFollowing = false;
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
        }
    }

    private void OnCompositionTargetRendering(object? sender, object e)
    {
        if (ThumbnailsScrollViewer is null || _isStoryboardRunning)
        {
            StopContinuousFollower();
            return;
        }

        double curY = ThumbnailsScrollViewer.VerticalOffset;
        double diff = _targetThumbScrollY - curY;

        if (Math.Abs(diff) < 0.5)
        {
            try
            {
                ThumbnailsScrollViewer.ChangeView(null, _targetThumbScrollY, null, disableAnimation: true);
            }
            catch { }
            StopContinuousFollower();
        }
        else
        {
            double nextY = curY + (diff * 0.30);
            try
            {
                ThumbnailsScrollViewer.ChangeView(null, nextY, null, disableAnimation: true);
            }
            catch
            {
                StopContinuousFollower();
            }
        }
    }

    private void OnThumbnailsPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverSidebar = true;
    }

    private void OnThumbnailsPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverSidebar = false;
    }

    private void OnThumbnailsPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        _isUserScrollingSidebar = true;
        _storyboard?.Stop();
        _isStoryboardRunning = false;
        StopContinuousFollower();
        _userScrollDebounceTimer.Stop();
        _userScrollDebounceTimer.Start();
    }

    private void OnThumbnailsScrollViewerViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        if (!_isFollowing && !_isStoryboardRunning && _isPointerOverSidebar)
        {
            _isUserScrollingSidebar = true;
            _userScrollDebounceTimer.Stop();
            _userScrollDebounceTimer.Start();
        }
    }

    private void OnUserScrollDebounceTimerTick(object? sender, object e)
    {
        _userScrollDebounceTimer.Stop();
        _isUserScrollingSidebar = false;
    }

    private void OnThumbnailTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (ViewModel.CurrentDocument is null)
        {
            return;
        }

        ThumbnailItemViewModel? targetItem = null;
        if (sender is FrameworkElement fe)
        {
            targetItem = fe.DataContext as ThumbnailItemViewModel;
            if (targetItem is null)
            {
                int index = ThumbnailsRepeater.GetElementIndex(fe);
                if (index >= 0 && index < ViewModel.CurrentDocument.Thumbnails.Count)
                {
                    targetItem = ViewModel.CurrentDocument.Thumbnails[index];
                }
            }
        }

        if (targetItem is not null)
        {
            _isUserScrollingSidebar = false;
            _userScrollDebounceTimer.Stop();
            ViewModel.CurrentDocument.GoToPage(targetItem.PageIndex);
        }
    }

    private void OnThumbnailsElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (ViewModel.CurrentDocument is not null && args.Index >= 0 && args.Index < ViewModel.CurrentDocument.Thumbnails.Count)
        {
            var thumbnail = ViewModel.CurrentDocument.Thumbnails[args.Index];
            double scale = XamlRoot?.RasterizationScale ?? 1.0;
            thumbnail.NotifyRealized(scale);
        }
    }

    private void OnThumbnailsElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is FrameworkElement { DataContext: ThumbnailItemViewModel thumbnail })
        {
            thumbnail.NotifyRecycled();
        }
        else if (ViewModel.CurrentDocument is not null)
        {
            int index = ThumbnailsRepeater.GetElementIndex(args.Element);
            if (index >= 0 && index < ViewModel.CurrentDocument.Thumbnails.Count)
            {
                ViewModel.CurrentDocument.Thumbnails[index].NotifyRecycled();
            }
        }
    }
}
