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
/// Sidebar tab page presenting document page thumbnails with dynamic responsive card sizing,
/// fluid critically-damped spring synchronization, and uninterrupted touch manipulation.
/// </summary>
public sealed partial class ThumbnailsTabPage : Page
{
    private const double TwoColumnThresholdDip = 300.0;

    private readonly DispatcherTimer _userScrollDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private DocumentViewModel? _observedDocument;
    private bool _isPointerDownOnSidebar;
    private bool _isUserInteractingWithSidebar;
    private bool _isRenderingHooked;
    private bool _isCatchingUp;
    private double _springVelocity;
    private long _lastSpringTimestamp;
    private double _targetThumbScrollY;
    private double _currentRowHeight = 182.0;
    private double _lastHandledWidth;
    private double _lastProgrammaticScrollY = -1;
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

        // Register pointer handlers with handledEventsToo: true so that DirectManipulation touch interactions
        // immediately set _isUserInteractingWithSidebar and prevent any interference from document scroll inertia.
        ThumbnailsScrollViewer.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnThumbnailsPointerPressed), handledEventsToo: true);
        ThumbnailsScrollViewer.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnThumbnailsPointerReleased), handledEventsToo: true);
        ThumbnailsScrollViewer.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnThumbnailsPointerCanceled), handledEventsToo: true);
        ThumbnailsScrollViewer.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnThumbnailsPointerCaptureLost), handledEventsToo: true);
        ThumbnailsScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnThumbnailsPointerWheelChanged), handledEventsToo: true);
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
        UpdateTargetScrollPosition(allowAnimation: false, useCurrentPageIndex: true);

        if (ViewModel.CurrentDocument is { } doc && ViewModel.IsSidebarOpen && !ViewModel.IsSettingsOpen && ViewModel.SidebarTab == Models.SidebarTab.Thumbnails)
        {
            for (int i = 0; i < doc.Thumbnails.Count; i++)
            {
                if (ThumbnailsRepeater.TryGetElement(i) is FrameworkElement realizedEl)
                {
                    realizedEl.DataContext = doc.Thumbnails[i];
                    BindPresenterInElement(realizedEl, doc.Thumbnails[i]);
                    doc.Thumbnails[i].NotifyRealized(scale);
                }
            }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        UnsubscribeFromDocument();
        _springVelocity = 0.0;
        _isCatchingUp = false;
        StopSmoothScrollLoop();
        _userScrollDebounceTimer.Stop();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentDocument))
        {
            SubscribeToDocument(ViewModel.CurrentDocument);
            _springVelocity = 0.0;
            _isCatchingUp = false;
            _isUserInteractingWithSidebar = false;
            _userScrollDebounceTimer.Stop();
            _lastSizedDocumentId = -1;
            _lastSizedRotation = -1;
            _lastHandledWidth = 0;

            // Force ItemsRepeater to recycle elements from the previous document and bind fresh items
            ThumbnailsRepeater.ItemsSource = null;
            ThumbnailsRepeater.ItemsSource = ViewModel.CurrentDocument?.Thumbnails;

            RefreshAfterVisibilityRestored();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RefreshAfterVisibilityRestored);
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
            _observedDocument.UserDocumentScrollInitiated += OnUserDocumentScrollInitiated;
        }
    }

    private void UnsubscribeFromDocument()
    {
        if (_observedDocument is not null)
        {
            _observedDocument.PropertyChanged -= OnDocumentPropertyChanged;
            _observedDocument.UserDocumentScrollInitiated -= OnUserDocumentScrollInitiated;
            _observedDocument = null;
        }
    }

    private void OnUserDocumentScrollInitiated()
    {
        if (!_isPointerDownOnSidebar)
        {
            _isUserInteractingWithSidebar = false;
            _userScrollDebounceTimer.Stop();
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
            UpdateTargetScrollPosition(allowAnimation: false, useCurrentPageIndex: true);
        }
        else if (e.PropertyName == nameof(DocumentViewModel.CurrentPageIndex))
        {
            // Selected page changed. In non-continuous mode (e.g. paged or presentation mode), center on the chosen page.
            // In continuous mode, FractionalPageIndex drives smooth, uninterrupted scrolling without boundary stutters.
            if (_observedDocument is not { IsContinuous: true })
            {
                _isUserInteractingWithSidebar = false;
                _userScrollDebounceTimer.Stop();
                UpdateTargetScrollPosition(allowAnimation: true, useCurrentPageIndex: true);
            }
        }
        else if (e.PropertyName == nameof(DocumentViewModel.FractionalPageIndex))
        {
            // If the user is actively touching or scrolling the sidebar, do not interrupt or fight their gesture!
            if (_isUserInteractingWithSidebar)
            {
                return;
            }

            UpdateTargetScrollPosition(allowAnimation: true, useCurrentPageIndex: false);
        }
    }

    private void OnThumbnailsScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 50.0)
        {
            UpdateThumbnailSizes(e.NewSize.Width, force: false);
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateTargetScrollPosition(allowAnimation: false, useCurrentPageIndex: true);
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

        // Allow portrait pages to maintain their full aspect ratio without clipping.
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

            if (ThumbnailsRepeater.TryGetElement(i) is FrameworkElement realizedEl)
            {
                realizedEl.DataContext = thumb;
                BindPresenterInElement(realizedEl, thumb);
            }

            if (h > maxCalculatedH)
            {
                maxCalculatedH = h;
            }
        }

        _currentRowHeight = (maxCalculatedH > 32.0 ? maxCalculatedH : 140.0) + 26.0 + ThumbnailsLayout.MinRowSpacing;
        ThumbnailsLayout.UpdateRowMetrics(doc.Thumbnails);
    }

    private void UpdateTargetScrollPosition(bool allowAnimation = false, bool useCurrentPageIndex = false)
    {
        if (_isPointerDownOnSidebar ||
            _isUserInteractingWithSidebar ||
            !ViewModel.IsSidebarOpen ||
            ViewModel.SidebarTab != Models.SidebarTab.Thumbnails ||
            ViewModel.CurrentDocument is not { PageCount: > 0 } doc ||
            ThumbnailsScrollViewer.ViewportHeight <= 0 ||
            ThumbnailsScrollViewer.ActualWidth < 50.0)
        {
            return;
        }

        double pageProgress = useCurrentPageIndex ? (doc.CurrentPageIndex + 0.5) : doc.FractionalPageIndex;

        ThumbnailsLayout.GetRowVerticalBounds(
            pageProgress,
            _currentRowHeight,
            out double rowCenterY,
            out double rowTop,
            out double rowHeight);

        // 8.0 DIPs is the top padding of ThumbnailsScrollViewer (Padding="16,8,16,8")
        double targetCenterY = 8.0 + rowCenterY;
        double totalH = ThumbnailsLayout.TotalContentHeight;
        double maxScrollY = Math.Max(0.0, totalH > 0
            ? (totalH + 16.0 - ThumbnailsScrollViewer.ViewportHeight)
            : (ThumbnailsScrollViewer.ExtentHeight > 0
                ? (ThumbnailsScrollViewer.ExtentHeight - ThumbnailsScrollViewer.ViewportHeight)
                : ThumbnailsScrollViewer.ScrollableHeight));

        double targetScrollY = Math.Clamp(targetCenterY - (ThumbnailsScrollViewer.ViewportHeight / 2.0), 0, maxScrollY);

        _targetThumbScrollY = targetScrollY;

        if (!allowAnimation)
        {
            _springVelocity = 0.0;
            _isCatchingUp = false;
            StopSmoothScrollLoop();
            try
            {
                ThumbnailsScrollViewer.ChangeView(null, targetScrollY, null, disableAnimation: true);
            }
            catch { }
            return;
        }

        double curY = ThumbnailsScrollViewer.VerticalOffset;
        double viewportH = ThumbnailsScrollViewer.ViewportHeight;

        double cardTop = 8.0 + rowTop;
        double cardBottom = cardTop + rowHeight;
        bool isCardVisible = (cardBottom >= curY && cardTop <= curY + viewportH);
        double scrollDistance = Math.Abs(targetScrollY - curY);

        if (!isCardVisible || scrollDistance > 50.0)
        {
            _isCatchingUp = true;
        }

        StartSmoothScrollLoop();
    }

    private void StartSmoothScrollLoop()
    {
        if (!_isRenderingHooked)
        {
            _isRenderingHooked = true;
            _lastSpringTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
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
            _isPointerDownOnSidebar ||
            _isUserInteractingWithSidebar ||
            !ViewModel.IsSidebarOpen ||
            ViewModel.SidebarTab != Models.SidebarTab.Thumbnails)
        {
            _springVelocity = 0.0;
            _isCatchingUp = false;
            StopSmoothScrollLoop();
            return;
        }

        long currentTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = (double)(currentTimestamp - _lastSpringTimestamp) / System.Diagnostics.Stopwatch.Frequency;
        _lastSpringTimestamp = currentTimestamp;
        dt = Math.Clamp(dt, 0.001, 0.050);

        double curY = ThumbnailsScrollViewer.VerticalOffset;
        double diff = _targetThumbScrollY - curY;

        // When within snapping threshold, place at target and unhook rendering to guarantee 0.0% idle CPU and PSR
        if (Math.Abs(diff) < 0.75 && Math.Abs(_springVelocity) < 2.0)
        {
            _springVelocity = 0.0;
            _isCatchingUp = false;
            try
            {
                ThumbnailsScrollViewer.ChangeView(null, _targetThumbScrollY, null, disableAnimation: true);
            }
            catch { }
            StopSmoothScrollLoop();
            return;
        }

        // Underdamped spring with subtle bounce (~4.6% overshoot) when catching up from distance (~410ms duration);
        // critically damped when continuously following during normal document reading.
        double omega = _isCatchingUp ? 13.8 : 19.0;
        double zeta = _isCatchingUp ? 0.70 : 1.0;

        double accel = (omega * omega * diff) - (2.0 * zeta * omega * _springVelocity);
        _springVelocity += accel * dt;
        _springVelocity = Math.Clamp(_springVelocity, -8000.0, 8000.0);

        double nextY = curY + (_springVelocity * dt);

        if (_isCatchingUp && Math.Abs(diff) < 1.0 && Math.Abs(_springVelocity) < 5.0)
        {
            _isCatchingUp = false;
        }

        double totalH = ThumbnailsLayout?.TotalContentHeight ?? 0.0;
        double maxScroll = Math.Max(ThumbnailsScrollViewer.ScrollableHeight,
            totalH > 0 ? (totalH + 16.0 - ThumbnailsScrollViewer.ViewportHeight) : 0.0);
        double clampedY = maxScroll > 0 ? Math.Clamp(nextY, 0.0, maxScroll) : Math.Max(0.0, nextY);

        _lastProgrammaticScrollY = clampedY;

        try
        {
            ThumbnailsScrollViewer.ChangeView(null, clampedY, null, disableAnimation: true);
        }
        catch
        {
            StopSmoothScrollLoop();
        }
    }

    private void OnThumbnailsPointerEntered(object sender, PointerRoutedEventArgs e)
    {
    }

    private void OnThumbnailsPointerExited(object sender, PointerRoutedEventArgs e)
    {
    }

    private void OnThumbnailsPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isPointerDownOnSidebar = true;
        _isUserInteractingWithSidebar = true;
        _springVelocity = 0.0;
        _isCatchingUp = false;
        _userScrollDebounceTimer.Stop();
        StopSmoothScrollLoop();
    }

    private void OnThumbnailsPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isPointerDownOnSidebar = false;
        if (_isUserInteractingWithSidebar)
        {
            _userScrollDebounceTimer.Stop();
            _userScrollDebounceTimer.Start();
        }
    }

    private void OnThumbnailsPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isPointerDownOnSidebar = false;
        if (_isUserInteractingWithSidebar)
        {
            _userScrollDebounceTimer.Stop();
            _userScrollDebounceTimer.Start();
        }
    }

    private void OnThumbnailsPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isPointerDownOnSidebar = false;
        if (_isUserInteractingWithSidebar)
        {
            _userScrollDebounceTimer.Stop();
            _userScrollDebounceTimer.Start();
        }
    }

    private void OnThumbnailsPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        _isUserInteractingWithSidebar = true;
        _springVelocity = 0.0;
        _isCatchingUp = false;
        StopSmoothScrollLoop();
        _userScrollDebounceTimer.Stop();
        _userScrollDebounceTimer.Start();
    }

    private void OnThumbnailsScrollViewerViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        // If the new offset differs from what we programmatically asked for, the user is scrolling the sidebar directly
        if (!_isRenderingHooked || Math.Abs(e.NextView.VerticalOffset - _lastProgrammaticScrollY) > 1.5)
        {
            _isUserInteractingWithSidebar = true;
            _springVelocity = 0.0;
            _isCatchingUp = false;
            StopSmoothScrollLoop();
            _userScrollDebounceTimer.Stop();
            _userScrollDebounceTimer.Start();
        }
    }

    private void OnThumbnailsScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_isRenderingHooked)
        {
            if (e.IsIntermediate)
            {
                _isUserInteractingWithSidebar = true;
                _userScrollDebounceTimer.Stop();
                _userScrollDebounceTimer.Start();
            }
            else
            {
                _userScrollDebounceTimer.Stop();
                _userScrollDebounceTimer.Start();
            }
        }
    }

    private void OnUserScrollDebounceTimerTick(object? sender, object e)
    {
        _userScrollDebounceTimer.Stop();
        _isUserInteractingWithSidebar = false;
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
            _isPointerDownOnSidebar = false;
            _isUserInteractingWithSidebar = false;
            _springVelocity = 0.0;
            _isCatchingUp = false;
            _userScrollDebounceTimer.Stop();
            StopSmoothScrollLoop();
            ViewModel.CurrentDocument.GoToPage(targetItem.PageIndex);
            UpdateTargetScrollPosition(allowAnimation: true, useCurrentPageIndex: true);
        }
    }

    private void OnThumbnailsElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (ViewModel.CurrentDocument is not null && args.Index >= 0 && args.Index < ViewModel.CurrentDocument.Thumbnails.Count)
        {
            var thumbnail = ViewModel.CurrentDocument.Thumbnails[args.Index];
            if (args.Element is FrameworkElement fe)
            {
                fe.DataContext = thumbnail;
                BindPresenterInElement(fe, thumbnail);
            }

            double scale = XamlRoot?.RasterizationScale ?? 1.0;
            thumbnail.NotifyRealized(scale);
        }
    }

    private static void BindPresenterInElement(DependencyObject parent, ThumbnailItemViewModel thumbnail)
    {
        if (parent is FrameworkElement rootFe)
        {
            Microsoft.UI.Xaml.Markup.XamlBindingHelper.GetDataTemplateComponent(rootFe)?.ProcessBindings(thumbnail, thumbnail.PageIndex, 0, out _);
            if (rootFe is Panel rootPanel && rootPanel.Children.Count > 0 && rootPanel.Children[0] is Border cardBorder)
            {
                cardBorder.Width = thumbnail.WidthDip;
                cardBorder.Height = thumbnail.HeightDip;
            }
        }

        BindPresenterRecursive(parent, thumbnail);
    }

    private static void BindPresenterRecursive(DependencyObject parent, ThumbnailItemViewModel thumbnail)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is WinPDF.Controls.ThumbnailPresenter presenter)
            {
                if (!ReferenceEquals(presenter.Item, thumbnail))
                {
                    presenter.Item = thumbnail;
                }

                presenter.IsNightMode = thumbnail.IsNightMode;
                presenter.SyncCardDimensions();
                return;
            }

            BindPresenterRecursive(child, thumbnail);
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
