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
    private const double TwoColumnThresholdDip = 300.0;

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
    private bool _isRenderingHooked;
    private bool _isStoryboardRunning;
    private double _targetThumbScrollY;
    private double _currentRowHeight = 182.0;
    private double _lastHandledWidth;
    private long _lastSizedDocumentId = -1;
    private int _lastSizedRotation = -1;

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
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SubscribeToDocument(ViewModel.CurrentDocument);
        RefreshAfterVisibilityRestored();
    }

    /// <summary>
    /// Ensures thumbnail active state, layout metrics, and realized item rasterization are synchronized
    /// when the sidebar pane opens or returns from the settings overlay.
    /// </summary>
    public void RefreshAfterVisibilityRestored()
    {
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        if (ViewModel.CurrentDocument is not null && ViewModel.IsSidebarOpen && !ViewModel.IsSettingsOpen && ViewModel.SidebarTab == Models.SidebarTab.Thumbnails)
        {
            ViewModel.CurrentDocument.SetThumbnailsActive(true, scale);
        }

        double width = ThumbnailsScrollViewer.ActualWidth > 0 ? ThumbnailsScrollViewer.ActualWidth : 260.0;
        UpdateThumbnailSizes(width, force: true);
        UpdateTargetScrollPosition(allowAnimation: false);

        if (ViewModel.CurrentDocument is { } doc && ViewModel.IsSidebarOpen && !ViewModel.IsSettingsOpen && ViewModel.SidebarTab == Models.SidebarTab.Thumbnails)
        {
            for (int i = 0; i < doc.Thumbnails.Count; i++)
            {
                if (ThumbnailsRepeater.TryGetElement(i) is not null)
                {
                    doc.Thumbnails[i].NotifyRealized(scale);
                }
            }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        UnsubscribeFromDocument();
        StopSmoothScrollLoop();
        _storyboard?.Stop();
        _isStoryboardRunning = false;
        _userScrollDebounceTimer.Stop();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentDocument))
        {
            SubscribeToDocument(ViewModel.CurrentDocument);
            double width = ThumbnailsScrollViewer.ActualWidth > 0 ? ThumbnailsScrollViewer.ActualWidth : 260.0;
            UpdateThumbnailSizes(width, force: true);
            UpdateTargetScrollPosition(allowAnimation: false);
        }
        else if (e.PropertyName is nameof(ShellViewModel.IsSidebarOpen) or nameof(ShellViewModel.SidebarTab) or nameof(ShellViewModel.IsSettingsOpen))
        {
            if (ViewModel.IsSidebarOpen && !ViewModel.IsSettingsOpen && ViewModel.SidebarTab == Models.SidebarTab.Thumbnails)
            {
                RefreshAfterVisibilityRestored();
            }
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
        if (!ViewModel.IsSidebarOpen || ViewModel.SidebarTab != Models.SidebarTab.Thumbnails)
        {
            return;
        }

        if (e.PropertyName == nameof(DocumentViewModel.Rotation))
        {
            double width = ThumbnailsScrollViewer.ActualWidth > 0 ? ThumbnailsScrollViewer.ActualWidth : 260.0;
            UpdateThumbnailSizes(width, force: true);
            UpdateTargetScrollPosition(allowAnimation: false);
        }
        else if (e.PropertyName is nameof(DocumentViewModel.FractionalPageIndex) or nameof(DocumentViewModel.CurrentPageIndex))
        {
            // Scrolling the document resets any user scroll lock on sidebar and snaps into synchronization.
            _isUserScrollingSidebar = false;
            _userScrollDebounceTimer.Stop();
            UpdateTargetScrollPosition(allowAnimation: true);
        }
    }

    private void OnThumbnailsScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 50.0)
        {
            UpdateThumbnailSizes(e.NewSize.Width, force: false);
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateTargetScrollPosition(allowAnimation: false);
            });
        }
    }

    private void UpdateThumbnailSizes(double scrollViewerWidth, bool force = false)
    {
        if (ViewModel.CurrentDocument is not { } doc || scrollViewerWidth <= 0 || ThumbnailsLayout is null)
        {
            return;
        }

    bool sameDocAndRot = (_lastSizedDocumentId == doc.DocumentId) && (_lastSizedRotation == doc.Rotation);
    if (!force && sameDocAndRot && Math.Abs(_lastHandledWidth - scrollViewerWidth) < 0.5)
    {
        return;
    }

    _lastHandledWidth = scrollViewerWidth;
    _lastSizedDocumentId = doc.DocumentId;
    _lastSizedRotation = doc.Rotation;

    double horizontalPadding = ThumbnailsScrollViewer.Padding.Left + ThumbnailsScrollViewer.Padding.Right;
    double availableW = Math.Max(80.0, scrollViewerWidth - horizontalPadding - 14.0);

    int numCols = scrollViewerWidth >= TwoColumnThresholdDip ? 2 : 1;

    if (ThumbnailsLayout.MaximumRowsOrColumns != numCols)
    {
        ThumbnailsLayout.MaximumRowsOrColumns = numCols;
    }

    double colSpacing = ThumbnailsLayout.MinColumnSpacing;
    double colWidth = (numCols == 1)
        ? availableW
        : Math.Floor((availableW - colSpacing) / 2.0);

    double maxCardW = (numCols == 1)
        ? Math.Clamp(colWidth - 12.0, 48.0, 190.0)
        : Math.Clamp(colWidth - 8.0, 44.0, 180.0);  

        // Allow portrait pages (like A4 1:1.414 or taller) to maintain their full aspect ratio without clipping.
        double maxCardH = Math.Round(maxCardW * 1.48);

        double maxCalculatedH = 0.0;

        for (int i = 0; i < doc.Thumbnails.Count; i++)
        {
            var thumb = doc.Thumbnails[i];
            double aspect = 1.0 / Math.Sqrt(2.0);
            if (i < doc.Pages.Count)
            {
                aspect = DocumentViewModel.GetPageDisplayAspectRatio(doc.Pages[i], doc.Rotation);
            }

            (double w, double h) = DocumentViewModel.ComputeThumbnailBox(aspect, maxCardW, maxCardH);

            thumb.WidthDip = w;
            thumb.HeightDip = h;

            if (h > maxCalculatedH)
            {
                maxCalculatedH = h;
            }
        }

        _currentRowHeight = (maxCalculatedH > 32.0 ? maxCalculatedH : 140.0) + 26.0 + ThumbnailsLayout.MinRowSpacing;
        ThumbnailsLayout.UpdateRowMetrics(doc.Thumbnails);
    }

    private void UpdateTargetScrollPosition(bool allowAnimation = false)
    {
        if (_isUserScrollingSidebar ||
            !ViewModel.IsSidebarOpen ||
            ViewModel.SidebarTab != Models.SidebarTab.Thumbnails ||
            ViewModel.CurrentDocument is not { PageCount: > 0 } doc ||
            ThumbnailsScrollViewer.ViewportHeight <= 0 ||
            ThumbnailsScrollViewer.ActualWidth < 50.0)
        {
            return;
        }

        ThumbnailsLayout.GetRowVerticalBounds(
            doc.FractionalPageIndex,
            _currentRowHeight,
            out double rowCenterY,
            out double rowTop,
            out double rowHeight);

        double targetCenterY = 8.0 + rowCenterY;
        double targetScrollY = Math.Clamp(targetCenterY - (ThumbnailsScrollViewer.ViewportHeight / 2.0), 0, Math.Max(0, ThumbnailsScrollViewer.ScrollableHeight));

        _targetThumbScrollY = targetScrollY;

        double curY = ThumbnailsScrollViewer.VerticalOffset;
        double viewportH = ThumbnailsScrollViewer.ViewportHeight;

        if (!allowAnimation)
        {
            StopSmoothScrollLoop();
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
        double cardTop = 8.0 + rowTop;
        double cardBottom = cardTop + rowHeight;
        bool isCardVisible = (cardBottom >= curY && cardTop <= curY + viewportH);

        if (!isCardVisible)
        {
            // Target is not visible: rapid animation with ExponentialEase!
            StopSmoothScrollLoop();
            AnimateWithExponentialEase(curY, targetScrollY);
        }
        else
        {
            // Target is visible: use continuous smooth follower
            if (!_isStoryboardRunning)
            {
                StartSmoothScrollLoop();
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

    private void StartSmoothScrollLoop()
    {
        if (!_isRenderingHooked)
        {
            _isRenderingHooked = true;
            CompositionTarget.Rendering += OnCompositionTargetRendering;
        }
    }

    private void StopSmoothScrollLoop()
    {
        if (_isRenderingHooked)
        {
            _isRenderingHooked = false;
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
        }
    }

    private void OnCompositionTargetRendering(object? sender, object e)
    {
        if (ThumbnailsScrollViewer is null ||
            _isStoryboardRunning ||
            _isUserScrollingSidebar ||
            !ViewModel.IsSidebarOpen ||
            ViewModel.SidebarTab != Models.SidebarTab.Thumbnails)
        {
            StopSmoothScrollLoop();
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
            StopSmoothScrollLoop();
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
                StopSmoothScrollLoop();
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
        StopSmoothScrollLoop();
        _userScrollDebounceTimer.Stop();
        _userScrollDebounceTimer.Start();
    }

    private void OnThumbnailsScrollViewerViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        if (!_isRenderingHooked && !_isStoryboardRunning && _isPointerOverSidebar)
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
