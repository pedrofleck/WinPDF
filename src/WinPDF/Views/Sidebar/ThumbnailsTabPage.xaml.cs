using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinPDF.Helpers;
using WinPDF.ViewModels;

namespace WinPDF.Views.Sidebar;

/// <summary>
/// Sidebar tab page presenting document page thumbnails with dynamic responsive card sizing,
/// fluid critically-damped spring synchronization, and uninterrupted parallel touch manipulation.
/// </summary>
public sealed partial class ThumbnailsTabPage : Page
{
    private const double TwoColumnThresholdDip = 300.0;

    private DocumentViewModel? _observedDocument;
    private bool _isPointerDownOnSidebar;
    private bool _isSidebarManuallyScrolled;
    private bool _isProgrammaticThumbScroll;
    private bool _isRenderingHooked;
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

        // Register pointer handlers with handledEventsToo: true so that DirectManipulation touch interactions
        // immediately mark the sidebar as manually controlled and prevent any interference from document scroll inertia.
        ThumbnailsScrollViewer.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnThumbnailsPointerPressed), handledEventsToo: true);
        ThumbnailsScrollViewer.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnThumbnailsPointerReleased), handledEventsToo: true);
        ThumbnailsScrollViewer.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnThumbnailsPointerCanceled), handledEventsToo: true);
        ThumbnailsScrollViewer.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnThumbnailsPointerCaptureLost), handledEventsToo: true);

        TouchScrollBarHelper.Attach(
            ThumbnailsScrollViewer,
            touchHitThicknessDip: 22.0,
            onTouchScrollStarted: ActivateManualSidebarScrollLock);
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

    /// <summary>
    /// Marks the sidebar as actively controlled by the user so leftover document inertia will not pull it back.
    /// </summary>
    public void ActivateManualSidebarScrollLock()
    {
        _isSidebarManuallyScrolled = true;
        StopSmoothScrollLoop();
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
        StopSmoothScrollLoop();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentDocument))
        {
            SubscribeToDocument(ViewModel.CurrentDocument);
            _isPointerDownOnSidebar = false;
            _isSidebarManuallyScrolled = false;
            _lastSizedDocumentId = -1;
            _lastSizedRotation = -1;
            _lastHandledWidth = 0;

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
        // When the user initiates a new scroll directly on the document (and does not have a finger held down on the sidebar),
        // re-enable automatic sidebar synchronization so the sidebar returns to centering the active page.
        if (!_isPointerDownOnSidebar)
        {
            _isSidebarManuallyScrolled = false;
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
            if (_observedDocument?.IsCurrentPageChangeFromScroll == true)
            {
                // Page boundary crossed during document scrolling/inertia:
                // If the user is touching or has manually scrolled the sidebar, keep the sidebar 100% independent!
                if (_isPointerDownOnSidebar || _isSidebarManuallyScrolled)
                {
                    return;
                }

                UpdateTargetScrollPosition(allowAnimation: true, useCurrentPageIndex: false);
                return;
            }

            // Explicit page jump (thumbnail click, outline click, status bar page box, keyboard shortcut):
            if (!_isPointerDownOnSidebar)
            {
                _isSidebarManuallyScrolled = false;
                UpdateTargetScrollPosition(allowAnimation: true, useCurrentPageIndex: true);
            }
        }
        else if (e.PropertyName == nameof(DocumentViewModel.FractionalPageIndex))
        {
            // If the user is touching or has manually scrolled the sidebar during document inertia, never interrupt their sidebar view!
            if (_isPointerDownOnSidebar || _isSidebarManuallyScrolled)
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
                if (!_isPointerDownOnSidebar && !_isSidebarManuallyScrolled)
                {
                    UpdateTargetScrollPosition(allowAnimation: false, useCurrentPageIndex: true);
                }
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
            _isSidebarManuallyScrolled ||
            !ViewModel.IsSidebarOpen ||
            ViewModel.SidebarTab != Models.SidebarTab.Thumbnails ||
            ViewModel.CurrentDocument is not { PageCount: > 0 } doc ||
            ThumbnailsScrollViewer.ViewportHeight <= 0 ||
            ThumbnailsScrollViewer.ActualWidth < 50.0)
        {
            return;
        }

        double pageProgress = useCurrentPageIndex ? doc.CurrentPageIndex : doc.FractionalPageIndex;

        ThumbnailsLayout.GetRowVerticalBounds(
            pageProgress,
            _currentRowHeight,
            out double rowCenterY,
            out double _,
            out double _);

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
            StopSmoothScrollLoop();
            ApplyProgrammaticThumbScroll(targetScrollY);
            return;
        }

        StartSmoothScrollLoop();
    }

    private void ApplyProgrammaticThumbScroll(double targetY)
    {
        _isProgrammaticThumbScroll = true;
        try
        {
            ThumbnailsScrollViewer.ChangeView(null, targetY, null, disableAnimation: true);
        }
        catch
        {
        }
        finally
        {
            _isProgrammaticThumbScroll = false;
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
            _isPointerDownOnSidebar ||
            _isSidebarManuallyScrolled ||
            !ViewModel.IsSidebarOpen ||
            ViewModel.SidebarTab != Models.SidebarTab.Thumbnails)
        {
            StopSmoothScrollLoop();
            return;
        }

        double curY = ThumbnailsScrollViewer.VerticalOffset;
        double diff = _targetThumbScrollY - curY;

        if (Math.Abs(diff) < 0.75)
        {
            ApplyProgrammaticThumbScroll(_targetThumbScrollY);
            StopSmoothScrollLoop();
            return;
        }

        double step = diff * 0.22;
        if (Math.Abs(step) < 1.5)
        {
            step = Math.Sign(diff) * Math.Min(Math.Abs(diff), 1.5);
        }

        double nextY = curY + step;
        ApplyProgrammaticThumbScroll(nextY);
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
        _isSidebarManuallyScrolled = true;
        StopSmoothScrollLoop();
    }

    private void OnThumbnailsPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isPointerDownOnSidebar = false;
    }

    private void OnThumbnailsPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isPointerDownOnSidebar = false;
    }

    private void OnThumbnailsPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isPointerDownOnSidebar = false;
    }

    private void OnThumbnailsPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        _isSidebarManuallyScrolled = true;
        StopSmoothScrollLoop();
    }

    private void OnThumbnailsScrollViewerViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        if (!_isRenderingHooked && !_isProgrammaticThumbScroll)
        {
            _isSidebarManuallyScrolled = true;
        }
    }

    private void OnThumbnailsScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_isRenderingHooked && !_isProgrammaticThumbScroll && e.IsIntermediate)
        {
            _isSidebarManuallyScrolled = true;
        }
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
            _isSidebarManuallyScrolled = false;
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
