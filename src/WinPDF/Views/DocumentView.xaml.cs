using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Printing;
using Windows.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Printing;
using WinPDF.Controls;
using WinPDF.Helpers;
using WinPDF.Models;
using WinPDF.Rendering;
using WinPDF.Services;
using WinPDF.ViewModels;

namespace WinPDF.Views;

/// <summary>
/// Page hosting the responsive document canvas, unified scroll viewer, and floating overlays.
/// </summary>
public sealed partial class DocumentView : Page
{

    [ComImport]
    [Guid("905a0fef-bc53-11df-8c49-001e4fc686da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPrintBufferByteAccess
    {
        unsafe byte* Buffer { get; }
    }

    private readonly HashSet<PdfPageCanvas> _activeCanvases = new();
    private readonly DispatcherTimer _presentationHideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _topNotificationTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _viewportDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(85) };
    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly IPdfService _pdfService;
    private readonly IWindowContext _windowContext;
    private DocumentViewModel? _observedDocument;
    private bool _isProgrammaticPageScroll;
    private bool _isUpdatingPageFromScroll;
    private bool _isApplyingZoom;
    private bool _isUpdatingZoomFromScrollViewer;
    private bool _isUpdatingSliderFromZoom;
    private bool _isPageBoxEditing;
    private bool _isFittedVertically;
    private Size _lastHandledScrollViewerSize;
    private bool _isApplyingFitZoom;
    private bool _isSwitchingMode;
    private bool _isRestoringFromPresentation;
    private bool _wasInPresentationMode;
    private bool _prePresentationFittedVertically;
    private double _prePresentationZoom = 1.0;
    private double _currentSidebarWidthDip = 260.0;
    private bool _isHorizontalPushAnimating;
    private UIElement? _hookedScrollTemplateRoot;
    private PointerEventHandler? _wheelHandler;
    private Storyboard? _transitionStoryboard;
    private Storyboard? _searchBarStoryboard;
    private Storyboard? _horizontalPushStoryboard;
    private Storyboard? _statusBarCompactStoryboard;
    private double _statusBarMetadataExpandedWidth = 148.0;
    private double _statusBarPresentationExpandedWidth = 68.0;
    private bool _isAutoScrolling;
    private bool _autoScrollMiddleButtonDown;
    private long _autoScrollStartTimestamp;
    private long _lastAutoScrollRenderTicks;
    private long _lastAutoScrollPageFlipTicks;
    private Point _autoScrollAnchorPoint;
    private Point _autoScrollCurrentPoint;
    private double _autoScrollAccumulatedX;
    private double _autoScrollAccumulatedY;
    private bool _isAutoScrollRenderingSubscribed;
    private PointerEventHandler? _autoScrollPointerPressedHandler;
    private PointerEventHandler? _autoScrollPointerMovedHandler;
    private PointerEventHandler? _autoScrollPointerReleasedHandler;
    private PrintManager? _registeredPrintManager;
    private nint _registeredPrintHwnd;
    private PrintDocument? _printDocument;
    private IPrintDocumentSource? _printDocumentSource;
    private DocumentViewModel? _activePrintDocumentVm;
    private PrintTaskOptions? _currentPrintTaskOptions;

    /// <summary>Gets the localized page prefix text.</summary>
    public string PagePrefixText => ResourceStrings.Get(ResourceKeys.StatusBarPagePrefix);

    /// <summary>Gets the localized page box tooltip.</summary>
    public string PageBoxToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarPageBoxToolTip);

    /// <summary>Gets the localized format tooltip.</summary>
    public string FormatToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarFormatToolTip);

    /// <summary>Gets the localized document properties tooltip.</summary>
    public string PropertiesToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarPropertiesToolTip);

    /// <summary>Gets the localized two-page toggle tooltip.</summary>
    public string DoublePageToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarDoublePageToolTip);

    /// <summary>Gets the localized continuous scroll toggle tooltip.</summary>
    public string ContinuousToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarContinuousToolTip);

    /// <summary>Gets the localized fit vertical tooltip.</summary>
    public string FitVerticalToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarFitVerticalToolTip);

    /// <summary>Gets the localized fit horizontal tooltip.</summary>
    public string FitHorizontalToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarFitHorizontalToolTip);

    /// <summary>Gets the localized zoom preset tooltip.</summary>
    public string ZoomPresetToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarZoomPresetToolTip);

    /// <summary>Gets the localized zoom out tooltip.</summary>
    public string ZoomOutToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarZoomOutToolTip);

    /// <summary>Gets the localized zoom slider tooltip.</summary>
    public string ZoomSliderToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarZoomSliderToolTip);

    /// <summary>Gets the localized zoom in tooltip.</summary>
    public string ZoomInToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarZoomInToolTip);

    /// <summary>Gets the localized slideshow presentation tooltip.</summary>
    public string SlideshowToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarSlideshowToolTip);

    /// <summary>Gets the localized full screen toggle tooltip.</summary>
    public string FullScreenToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarFullScreenToolTip);

    /// <summary>Gets the localized exit presentation button text.</summary>
    public string ExitPresentationText => ResourceStrings.Get(ResourceKeys.PresentationExitButton);

    /// <summary>
    /// Gets the shell view model.
    /// </summary>
    public ShellViewModel ViewModel { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentView"/> class.
    /// </summary>
    public DocumentView()
    {
        NavigationCacheMode = NavigationCacheMode.Required;
        ViewModel = App.CurrentApp.Services.GetRequiredService<ShellViewModel>();
        _pdfService = App.CurrentApp.Services.GetRequiredService<IPdfService>();
        _windowContext = App.CurrentApp.Services.GetRequiredService<IWindowContext>();
        _currentSidebarWidthDip = Math.Clamp(ViewModel.SettingsService.Settings.SidebarWidth, 230.0, 480.0);
        InitializeComponent();

        _wheelHandler = new PointerEventHandler(OnScrollViewerPointerWheelChanged);
        _autoScrollPointerPressedHandler = new PointerEventHandler(OnViewerAutoScrollPointerPressed);
        _autoScrollPointerMovedHandler = new PointerEventHandler(OnViewerAutoScrollPointerMoved);
        _autoScrollPointerReleasedHandler = new PointerEventHandler(OnViewerAutoScrollPointerReleased);
        ScrollViewerContentRoot.AddHandler(UIElement.PointerPressedEvent, _autoScrollPointerPressedHandler, true);
        ScrollViewerContentRoot.AddHandler(UIElement.PointerMovedEvent, _autoScrollPointerMovedHandler, true);
        ScrollViewerContentRoot.AddHandler(UIElement.PointerReleasedEvent, _autoScrollPointerReleasedHandler, true);
        ScrollViewerContentRoot.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        ContinuousPagesRepeater.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        PrevPageZone.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        NextPageZone.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        ExitPresentationTopZone.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        ContinuousScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        ContinuousScrollViewer.SizeChanged += OnScrollViewerSizeChanged;

        _presentationHideTimer.Tick += OnPresentationHideTimerTick;
        _topNotificationTimer.Tick += OnTopNotificationTimerTick;
        _viewportDebounceTimer.Tick += OnViewportDebounceTimerTick;
        _searchDebounceTimer.Tick += OnSearchDebounceTimerTick;

        PdfPageCanvas.CanvasLoaded += OnCanvasLoaded;
        PdfPageCanvas.CanvasUnloaded += OnCanvasUnloaded;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ShellViewModel vm)
        {
            ViewModel = vm;
        }

        if (ViewModel.CurrentDocument is not null && (ViewModel.CurrentDocument.LastVerticalOffset > 0 || ViewModel.CurrentDocument.LastHorizontalOffset > 0))
        {
            ContinuousScrollViewer.ChangeView(
                ViewModel.CurrentDocument.LastHorizontalOffset,
                ViewModel.CurrentDocument.LastVerticalOffset,
                (float)ViewModel.CurrentDocument.ZoomFactor,
                disableAnimation: true);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureScrollViewerWheelHook();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.SearchFocusRequested -= OnSearchFocusRequested;
        ViewModel.SearchFocusRequested += OnSearchFocusRequested;
        ViewModel.NotificationRequested -= OnNotificationRequested;
        ViewModel.NotificationRequested += OnNotificationRequested;
        ViewModel.DefaultZoomPreferenceChanged -= OnDefaultZoomPreferenceChanged;
        ViewModel.DefaultZoomPreferenceChanged += OnDefaultZoomPreferenceChanged;
        ViewModel.PrintRequested -= OnPrintRequestedAsync;
        ViewModel.PrintRequested += OnPrintRequestedAsync;
        SubscribeToDocument(ViewModel.CurrentDocument);

        if (ViewModel.TryConsumePendingNotification(out string title, out string message))
        {
            ShowTopNotification(title, message);
        }

        SyncPaneOffsetsImmediate();
        ApplyStatusBarCompactStateImmediate(ViewModel.IsCompactChrome);

        if (ViewModel.CurrentDocument is not null)
        {
            UpdateViewportPages(ViewModel.CurrentDocument.ZoomFactor);
        }
    }

    private void EnsureScrollViewerWheelHook()
    {
        if (_wheelHandler is null)
        {
            return;
        }

        ContinuousScrollViewer.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(ContinuousScrollViewer) > 0 &&
            VisualTreeHelper.GetChild(ContinuousScrollViewer, 0) is UIElement templateRoot &&
            !ReferenceEquals(_hookedScrollTemplateRoot, templateRoot))
        {
            if (_hookedScrollTemplateRoot is not null)
            {
                _hookedScrollTemplateRoot.RemoveHandler(UIElement.PointerWheelChangedEvent, _wheelHandler);
            }

            _hookedScrollTemplateRoot = templateRoot;
            _hookedScrollTemplateRoot.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _presentationHideTimer.Stop();
        _presentationHideTimer.Tick -= OnPresentationHideTimerTick;
        _topNotificationTimer.Stop();
        _topNotificationTimer.Tick -= OnTopNotificationTimerTick;
        _viewportDebounceTimer.Stop();
        _viewportDebounceTimer.Tick -= OnViewportDebounceTimerTick;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Tick -= OnSearchDebounceTimerTick;

        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.SearchFocusRequested -= OnSearchFocusRequested;
        ViewModel.NotificationRequested -= OnNotificationRequested;
        ViewModel.DefaultZoomPreferenceChanged -= OnDefaultZoomPreferenceChanged;
        ViewModel.PrintRequested -= OnPrintRequestedAsync;
        StopAutoScroll();
        _transitionStoryboard?.Stop();
        _searchBarStoryboard?.Stop();
        _horizontalPushStoryboard?.Stop();
        _statusBarCompactStoryboard?.Stop();
        _isHorizontalPushAnimating = false;
        UnsubscribeFromDocument();
    }

    private void OnCanvasLoaded(PdfPageCanvas canvas)
    {
        _activeCanvases.Add(canvas);
        double targetZoom = ViewModel.CurrentDocument?.ZoomFactor ?? ContinuousScrollViewer.ZoomFactor;
        if (targetZoom > 0.05)
        {
            ApplyViewportToCanvas(canvas, targetZoom);
        }
    }

    private void OnCanvasUnloaded(PdfPageCanvas canvas)
    {
        _activeCanvases.Remove(canvas);
    }

    private void UpdateViewportPages(double currentZoom)
    {
        if (_activeCanvases.Count > 0)
        {
            foreach (var canvas in _activeCanvases.ToArray())
            {
                ApplyViewportToCanvas(canvas, currentZoom);
            }
        }
        else
        {
            FindAndApplyCanvases(ContinuousPagesRepeater, currentZoom);
        }
    }

    private static void ApplyViewportToCanvas(PdfPageCanvas canvas, double currentZoom)
    {
        canvas.UpdateViewport(currentZoom);
    }

    private static void FindAndApplyCanvases(DependencyObject? parent, double currentZoom)
    {
        if (parent is null)
        {
            return;
        }

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is PdfPageCanvas canvas)
            {
                ApplyViewportToCanvas(canvas, currentZoom);
            }
            else
            {
                FindAndApplyCanvases(child, currentZoom);
            }
        }
    }

    /// <summary>
    /// Refreshes active canvases and restores focus when returning from the Settings overlay.
    /// </summary>
    public void OnReturnedFromSettings()
    {
        EnsureScrollViewerWheelHook();
        if (ViewModel.CurrentDocument is { } doc)
        {
            ApplyNightModeToAllCanvases(doc.IsNightMode);
            double activeZoom = ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor;
            UpdateViewportPages(activeZoom);
            DispatcherQueue.TryEnqueue(ReturnFocusToViewer);
        }
    }

    private void ApplyNightModeToAllCanvases(bool isNightMode)
    {
        foreach (var canvas in _activeCanvases.ToArray())
        {
            canvas.ApplyNightModeImmediate(isNightMode);
        }

        FindAndApplyNightMode(ContinuousPagesRepeater, isNightMode);
    }

    private static void FindAndApplyNightMode(DependencyObject? parent, bool isNightMode)
    {
        if (parent is null)
        {
            return;
        }

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is PdfPageCanvas canvas)
            {
                canvas.ApplyNightModeImmediate(isNightMode);
            }
            else
            {
                FindAndApplyNightMode(child, isNightMode);
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentDocument))
        {
            StopAutoScroll();
            SubscribeToDocument(ViewModel.CurrentDocument);
            if (ViewModel.CurrentDocument is not null)
            {
                SyncPaneOffsetsImmediate();
                ScrollToPage(ViewModel.CurrentDocument.CurrentPageIndex, allowUnclampedOffset: true);
                PlayDocumentOpenTransitionAnimation();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (ViewModel.CurrentDocument is { } activeDoc)
                    {
                        Models.SavedDocumentState? savedState = null;
                        bool hasCustomRestoredZoom = ViewModel.SettingsService.Settings.SaveDocumentViewSettings &&
                            !string.IsNullOrWhiteSpace(activeDoc.FilePath) &&
                            ViewModel.SettingsService.TryGetDocumentState(activeDoc.FilePath, out savedState) &&
                            savedState is { HasCustomViewSettings: true, ZoomFactor: { } z } &&
                            z >= ViewModels.DocumentViewModel.MinZoom && z <= ViewModels.DocumentViewModel.MaxZoom;

                        if (hasCustomRestoredZoom && savedState?.ZoomFactor is { } restoredZoom)
                        {
                            ApplyCustomRestoredZoom(activeDoc, restoredZoom);
                        }
                        else
                        {
                            ApplyZoomPreferenceByIndex(ViewModel.DefaultZoomIndex);
                        }
                    }

                    ReturnFocusToViewer();
                });
            }
            else
            {
                PlayEmptyStateTransitionAnimation();
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsSearchOpen))
        {
            if (!_isHorizontalPushAnimating && FloatingTopTransform is not null)
            {
                FloatingTopTransform.TranslateX = GetTargetFloatingTopOffsetDip();
            }

            if (ViewModel.IsSearchOpen)
            {
                PlaySearchBarOpenAnimation();
                FocusSearchBox();
            }
            else
            {
                _searchDebounceTimer.Stop();
                ReturnFocusToViewer();
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsAnnotationToolbarOpen))
        {
            if (!_isHorizontalPushAnimating && FloatingTopTransform is not null)
            {
                FloatingTopTransform.TranslateX = GetTargetFloatingTopOffsetDip();
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsNightMode))
        {
            ApplyNightModeToAllCanvases(ViewModel.IsNightMode);
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsPresentationMode))
        {
            if (ViewModel.IsPresentationMode)
            {
                if (!_wasInPresentationMode)
                {
                    _wasInPresentationMode = true;
                    _prePresentationFittedVertically = _isFittedVertically;
                    _prePresentationZoom = ViewModel.ZoomBeforePresentation > 0.05
                        ? ViewModel.ZoomBeforePresentation
                        : (ViewModel.CurrentDocument?.ZoomFactor ?? ContinuousScrollViewer.ZoomFactor);
                }

                _isRestoringFromPresentation = false;
                FullScreenInfoBar.IsOpen = true;
                _presentationHideTimer.Stop();
                _presentationHideTimer.Start();

                ExecuteFitVertical(force: true);
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (ViewModel.IsPresentationMode)
                    {
                        ExecuteFitVertical(force: true);
                    }
                });
                _ = Task.Run(async () =>
                {
                    await Task.Delay(120).ConfigureAwait(false);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (ViewModel.IsPresentationMode)
                        {
                            ExecuteFitVertical(force: true);
                        }
                    });
                });
            }
            else if (_wasInPresentationMode)
            {
                _wasInPresentationMode = false;
                _isRestoringFromPresentation = true;
                _isFittedVertically = _prePresentationFittedVertically;

                if (!ViewModel.IsFullScreen)
                {
                    _presentationHideTimer.Stop();
                    FullScreenInfoBar.IsOpen = false;
                }

                RestoreAfterPresentationExit();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RestoreAfterPresentationExit);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(140).ConfigureAwait(false);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        RestoreAfterPresentationExit();
                        _isRestoringFromPresentation = false;
                    });
                });
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsFullScreen))
        {
            if (ViewModel.IsFullScreen || ViewModel.IsPresentationMode)
            {
                FullScreenInfoBar.IsOpen = true;
                _presentationHideTimer.Stop();
                _presentationHideTimer.Start();
            }
            else
            {
                _presentationHideTimer.Stop();
                FullScreenInfoBar.IsOpen = false;
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsCompactChrome))
        {
            AnimateStatusBarCompactMode(ViewModel.IsCompactChrome);
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
            _observedDocument.PropertyChanged += OnCurrentDocumentPropertyChanged;
            _observedDocument.FitVerticalRequested += OnFitPageRequested;
            _observedDocument.FitHorizontalRequested += OnFitWidthRequested;
            _observedDocument.ModeChanged += OnDocumentModeChanged;
            _observedDocument.ActiveSearchMatchScrollRequested += OnActiveSearchMatchScrollRequested;
            UpdateZoomSlider(doc.ZoomFactor);
            if (StatusBarPageBox is not null)
            {
                StatusBarPageBox.Text = doc.CurrentPageNumber.ToString();
            }
        }
    }

    private void UnsubscribeFromDocument()
    {
        if (_observedDocument is not null)
        {
            _observedDocument.PropertyChanged -= OnCurrentDocumentPropertyChanged;
            _observedDocument.FitVerticalRequested -= OnFitPageRequested;
            _observedDocument.FitHorizontalRequested -= OnFitWidthRequested;
            _observedDocument.ModeChanged -= OnDocumentModeChanged;
            _observedDocument.ActiveSearchMatchScrollRequested -= OnActiveSearchMatchScrollRequested;
            _observedDocument = null;
        }
    }

    private void OnActiveSearchMatchScrollRequested(PdfSearchMatch match)
    {
        ScrollToSearchMatch(match);
    }

    /// <summary>
    /// Automatically scrolls the active page and viewport so that <paramref name="match"/> is centered in the viewer.
    /// </summary>
    /// <param name="match">The search match to scroll into view.</param>
    private void ScrollToSearchMatch(PdfSearchMatch match)
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc ||
            match.PageIndex < 0 || match.PageIndex >= doc.Pages.Count)
        {
            return;
        }

        PdfPageInfo pageInfo = doc.Pages[match.PageIndex];
        PageCoordinateTransform transform = PageCoordinateTransform.FromPage(pageInfo, doc.Rotation);

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;

        for (int i = 0; i < match.Rects.Count; i++)
        {
            Rect r = transform.ToPageRect(match.Rects[i]);
            minX = Math.Min(minX, r.X);
            minY = Math.Min(minY, r.Y);
            maxX = Math.Max(maxX, r.X + r.Width);
            maxY = Math.Max(maxY, r.Y + r.Height);
        }

        double pageW = GetDisplayedPageWidthDip(pageInfo, doc.Rotation);
        double pageH = GetDisplayedPageHeightDip(pageInfo, doc.Rotation);
        double matchCenterX = minX <= maxX ? (minX + maxX) / 2.0 : pageW / 2.0;
        double matchCenterY = minY <= maxY ? (minY + maxY) / 2.0 : pageH / 2.0;

        _isProgrammaticPageScroll = true;

        if (doc.IsContinuous)
        {
            if (doc.CurrentPageIndex != match.PageIndex)
            {
                _isUpdatingPageFromScroll = true;
                try
                {
                    doc.CurrentPageIndex = match.PageIndex;
                }
                finally
                {
                    _isUpdatingPageFromScroll = false;
                }
            }

            if ( StatusBarPageBox is not null && !_isPageBoxEditing)
            {
                StatusBarPageBox.Text = doc.CurrentPageNumber.ToString();
            }

            double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
            double rowTopY = 24.0;
            int step = doc.IsDoublePage ? 2 : 1;
            int targetRow = match.PageIndex / step;
            for (int r = 0; r < targetRow && r * step < doc.Pages.Count; r++)
            {
                int leftIdx = r * step;
                double rowH = GetDisplayedPageHeightDip(doc.Pages[leftIdx], doc.Rotation);
                if (step == 2 && leftIdx + 1 < doc.Pages.Count)
                {
                    rowH = Math.Max(rowH, GetDisplayedPageHeightDip(doc.Pages[leftIdx + 1], doc.Rotation));
                }

                rowTopY += rowH + 16.0;
            }

            double contentMatchY = rowTopY + matchCenterY;
            double viewportH = ContinuousScrollViewer.ViewportHeight > 0 ? ContinuousScrollViewer.ViewportHeight : ContinuousScrollViewer.ActualHeight;
            double rawVOffset = Math.Max(0.0, (contentMatchY * zoom) - (viewportH / 2.0));
            double vOffset = ContinuousScrollViewer.ScrollableHeight > 0.5
                ? Math.Clamp(rawVOffset, 0.0, ContinuousScrollViewer.ScrollableHeight)
                : rawVOffset;

            double? hOffset = null;
            if (ContinuousScrollViewer.ScrollableWidth > 4.0)
            {
                double viewportW = ContinuousScrollViewer.ViewportWidth > 0 ? ContinuousScrollViewer.ViewportWidth : ContinuousScrollViewer.ActualWidth;
                double rawHOffset = Math.Max(0.0, ((24.0 + matchCenterX) * zoom) - (viewportW / 2.0));
                hOffset = Math.Clamp(rawHOffset, 0.0, ContinuousScrollViewer.ScrollableWidth);
            }

            ContinuousScrollViewer.ChangeView(hOffset, vOffset, null, disableAnimation: true);
            doc.LastVerticalOffset = vOffset;
            if (hOffset.HasValue)
            {
                doc.LastHorizontalOffset = hOffset.Value;
            }

            doc.FractionalPageIndex = match.PageIndex + Math.Clamp(matchCenterY / Math.Max(1.0, pageH), 0.0, 1.0);
        }
        else
        {
            if (doc.CurrentPageIndex != match.PageIndex)
            {
                doc.GoToPage(match.PageIndex);
            }

            double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
            double? vOffset = 0.0;
            double? hOffset = 0.0;

            if (ContinuousScrollViewer.ScrollableHeight > 4.0)
            {
                double viewportH = ContinuousScrollViewer.ViewportHeight > 0 ? ContinuousScrollViewer.ViewportHeight : ContinuousScrollViewer.ActualHeight;
                vOffset = Math.Clamp((matchCenterY * zoom) - (viewportH / 2.0), 0.0, ContinuousScrollViewer.ScrollableHeight);
            }

            if (ContinuousScrollViewer.ScrollableWidth > 4.0)
            {
                double viewportW = ContinuousScrollViewer.ViewportWidth > 0 ? ContinuousScrollViewer.ViewportWidth : ContinuousScrollViewer.ActualWidth;
                hOffset = Math.Clamp((matchCenterX * zoom) - (viewportW / 2.0), 0.0, ContinuousScrollViewer.ScrollableWidth);
            }

            ContinuousScrollViewer.ChangeView(hOffset, vOffset, null, disableAnimation: true);
            doc.LastVerticalOffset = vOffset ?? 0.0;
            doc.FractionalPageIndex = match.PageIndex;
        }

        ScheduleViewportUpdate();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _isProgrammaticPageScroll = false;
        });
    }

    private static double GetDisplayedPageWidthDip(PdfPageInfo page, int userRotationDegrees)
    {
        int effRot = PageCoordinateTransform.NormalizeRotation(page.Rotation + userRotationDegrees);
        return (effRot is 90 or 270 ? page.HeightPt : page.WidthPt) * (96.0 / 72.0);
    }

    private static double GetDisplayedPageHeightDip(PdfPageInfo page, int userRotationDegrees)
    {
        int effRot = PageCoordinateTransform.NormalizeRotation(page.Rotation + userRotationDegrees);
        return (effRot is 90 or 270 ? page.WidthPt : page.HeightPt) * (96.0 / 72.0);
    }

    private void RestoreAfterPresentationExit()
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc || ViewModel.IsPresentationMode)
        {
            return;
        }

        int targetPage = Math.Clamp(doc.CurrentPageIndex, 0, doc.Pages.Count - 1);
        double restoreZoom = Math.Clamp(_prePresentationZoom > 0.05 ? _prePresentationZoom : ViewModel.ZoomBeforePresentation, DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);

        _isSwitchingMode = true;
        _isApplyingZoom = true;
        try
        {
            ContinuousScrollViewer.UpdateLayout();
            doc.ZoomFactor = restoreZoom;
            UpdateZoomSlider(restoreZoom);

            if (doc.IsContinuous)
            {
                double targetY = 24.0;
                int step = doc.IsDoublePage ? 2 : 1;
                int targetRow = targetPage / step;
                for (int r = 0; r < targetRow && r * step < doc.Pages.Count; r++)
                {
                    targetY += doc.Pages[r * step].DisplayHeightDip + 16.0;
                }

                double vOffset = targetY * restoreZoom;
                ContinuousScrollViewer.ChangeView(0, vOffset, (float)restoreZoom, disableAnimation: true);
                doc.LastVerticalOffset = vOffset;
                doc.FractionalPageIndex = targetPage;
            }
            else
            {
                ContinuousScrollViewer.ChangeView(0, 0, (float)restoreZoom, disableAnimation: true);
                doc.LastVerticalOffset = 0;
                doc.FractionalPageIndex = targetPage;
            }

            UpdateViewportPages(restoreZoom);
            ScheduleViewportUpdate();
        }
        finally
        {
            _isApplyingZoom = false;
            _isSwitchingMode = false;
        }
    }

    private void OnDocumentModeChanged(object? sender, EventArgs e)
    {
        if (_isRestoringFromPresentation || ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        int targetPage = Math.Clamp(doc.CurrentPageIndex, 0, doc.PageCount - 1);
        _isSwitchingMode = true;

        if (!doc.IsContinuous)
        {
            _isFittedVertically = true;
            _isApplyingFitZoom = true;

            if (doc.CurrentPageIndex != targetPage)
            {
                doc.GoToPage(targetPage);
            }

            doc.FractionalPageIndex = targetPage;
            ContinuousScrollViewer.ChangeView(0, 0, null, disableAnimation: true);
            ExecuteFitVertical(force: true);

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    if (doc.CurrentPageIndex != targetPage)
                    {
                        doc.GoToPage(targetPage);
                    }

                    ExecuteFitVertical(force: true);
                    UpdateViewportPages(doc.ZoomFactor);
                }
                finally
                {
                    _isSwitchingMode = false;
                }
            });
        }
        else
        {
            ContinuousScrollViewer.UpdateLayout();
            ScrollToPage(targetPage, allowUnclampedOffset: true);

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    ContinuousScrollViewer.UpdateLayout();
                    if (doc.CurrentPageIndex != targetPage)
                    {
                        doc.GoToPage(targetPage);
                    }

                    ScrollToPage(targetPage, allowUnclampedOffset: true);
                    UpdateViewportPages(doc.ZoomFactor);
                }
                finally
                {
                    _isSwitchingMode = false;
                    _isApplyingFitZoom = false;
                }
            });
        }
    }

    private void OnFitPageRequested()
    {
        ExecuteFitVertical();
    }

    private void OnFitWidthRequested()
    {
        ExecuteFitHorizontal();
    }

    private void OnCurrentDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.CurrentPageIndex))
        {
            if (!_isUpdatingPageFromScroll && !_isApplyingFitZoom && _observedDocument is not null)
            {
                ScrollToPage(_observedDocument.CurrentPageIndex);
            }

            if (!_isPageBoxEditing && StatusBarPageBox is not null && _observedDocument is not null)
            {
                StatusBarPageBox.Text = _observedDocument.CurrentPageNumber.ToString();
            }

            if (_observedDocument is { IsContinuous: false })
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    UpdateViewportPages(ViewModel.CurrentDocument?.ZoomFactor ?? ContinuousScrollViewer.ZoomFactor);
                });
            }
        }
        else if (e.PropertyName == nameof(DocumentViewModel.IsNightMode))
        {
            if (_observedDocument is not null)
            {
                ApplyNightModeToAllCanvases(_observedDocument.IsNightMode);
            }
        }
        else if (e.PropertyName is nameof(DocumentViewModel.IsContinuous) or nameof(DocumentViewModel.IsDoublePage))
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                UpdateViewportPages(ViewModel.CurrentDocument?.ZoomFactor ?? ContinuousScrollViewer.ZoomFactor);
            });
        }
        else if (e.PropertyName == nameof(DocumentViewModel.ZoomFactor))
        {
            if (_observedDocument is not null)
            {
                UpdateZoomSlider(_observedDocument.ZoomFactor);
            }

            if (_isApplyingZoom || _isUpdatingZoomFromScrollViewer || _isApplyingFitZoom)
            {
                return;
            }

            if (_observedDocument is not null)
            {
                if (Math.Abs(ContinuousScrollViewer.ZoomFactor - _observedDocument.ZoomFactor) > 0.01)
                {
                    ApplyZoomAroundCenter((float)_observedDocument.ZoomFactor);
                }
                else
                {
                    UpdateViewportPages(_observedDocument.ZoomFactor);
                    ScheduleViewportUpdate();
                }
            }
        }
    }

    private void OnScrollViewerViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        _viewportDebounceTimer.Stop();
        if (_isSwitchingMode || _isRestoringFromPresentation || _isApplyingFitZoom)
        {
            return;
        }

        if (!_isHorizontalPushAnimating && PagesRepeaterPushTransform is not null && e.NextView.ZoomFactor > 0.05)
        {
            PagesRepeaterPushTransform.TranslateX = GetTargetDocumentOffsetDip(e.NextView.ZoomFactor) / e.NextView.ZoomFactor;
        }

        if (!_isHorizontalPushAnimating && FloatingTopTransform is not null)
        {
            FloatingTopTransform.TranslateX = GetTargetFloatingTopOffsetDip();
        }

        if (!_isProgrammaticPageScroll && ViewModel.CurrentDocument is { IsContinuous: true })
        {
            ViewModel.CurrentDocument.NotifyUserDocumentScrollInitiated();
            if (Math.Abs(e.NextView.ZoomFactor - ContinuousScrollViewer.ZoomFactor) < 0.001)
            {
                UpdatePageProgressFromScroll(e.NextView.VerticalOffset);
            }
        }
    }

    private void OnScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isSwitchingMode || _isRestoringFromPresentation)
        {
            ScheduleViewportUpdate();
            return;
        }

        if (ViewModel.CurrentDocument is not null)
        {
            ViewModel.CurrentDocument.LastHorizontalOffset = ContinuousScrollViewer.HorizontalOffset;
            ViewModel.CurrentDocument.LastVerticalOffset = ContinuousScrollViewer.VerticalOffset;

            double activeZoom = ContinuousScrollViewer.ZoomFactor;
            if (!_isHorizontalPushAnimating && PagesRepeaterPushTransform is not null && activeZoom > 0.05)
            {
                PagesRepeaterPushTransform.TranslateX = GetTargetDocumentOffsetDip(activeZoom) / activeZoom;
            }

            if (!_isHorizontalPushAnimating && FloatingTopTransform is not null)
            {
                FloatingTopTransform.TranslateX = GetTargetFloatingTopOffsetDip();
            }

            if (_isApplyingFitZoom)
            {
                if (Math.Abs(activeZoom - ViewModel.CurrentDocument.ZoomFactor) < 0.01)
                {
                    _isApplyingFitZoom = false;
                }
            }
            else if (!_isApplyingZoom && activeZoom > 0.05 && Math.Abs(ViewModel.CurrentDocument.ZoomFactor - activeZoom) > 0.005)
            {
                _isFittedVertically = false;
                _isUpdatingZoomFromScrollViewer = true;
                try
                {
                    ViewModel.CurrentDocument.ZoomFactor = activeZoom;
                }
                finally
                {
                    _isUpdatingZoomFromScrollViewer = false;
                }
            }

            if (!_isApplyingFitZoom && !_isApplyingZoom && !e.IsIntermediate)
            {
                UpdatePageProgressFromScroll();
            }
        }

        if (!e.IsIntermediate)
        {
            ScheduleViewportUpdate();
        }
    }

    private void OnScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsureScrollViewerWheelHook();
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            return;
        }

        if (Math.Abs(e.NewSize.Width - _lastHandledScrollViewerSize.Width) < 1.0 &&
            Math.Abs(e.NewSize.Height - _lastHandledScrollViewerSize.Height) < 1.0)
        {
            return;
        }

        _lastHandledScrollViewerSize = e.NewSize;

        if (_isRestoringFromPresentation)
        {
            DispatcherQueue.TryEnqueue(RestoreAfterPresentationExit);
            return;
        }

        if (ViewModel?.IsPresentationMode == true || (_isFittedVertically && ViewModel?.CurrentDocument is { IsContinuous: false }))
        {
            DispatcherQueue.TryEnqueue(() => ExecuteFitVertical(GetAvailableDocumentWidthDip(e.NewSize.Width), e.NewSize.Height, force: true));
        }
        else if (ViewModel?.CurrentDocument is { ZoomFactor: > 1.01 })
        {
            ScheduleViewportUpdate();
        }
    }

    private void ScheduleViewportUpdate()
    {
        _viewportDebounceTimer.Stop();
        _viewportDebounceTimer.Start();
    }

    private void OnViewportDebounceTimerTick(object? sender, object e)
    {
        _viewportDebounceTimer.Stop();
        if (ViewModel.CurrentDocument is null)
        {
            return;
        }

        double activeZoom = ContinuousScrollViewer.ZoomFactor;
        if (activeZoom <= 0.05)
        {
            activeZoom = ViewModel.CurrentDocument.ZoomFactor;
        }

        if (!_isApplyingZoom && !_isApplyingFitZoom && Math.Abs(ViewModel.CurrentDocument.ZoomFactor - activeZoom) > 0.01)
        {
            _isUpdatingZoomFromScrollViewer = true;
            try
            {
                ViewModel.CurrentDocument.ZoomFactor = activeZoom;
            }
            finally
            {
                _isUpdatingZoomFromScrollViewer = false;
            }
        }

        UpdateViewportPages(activeZoom);
    }

    private void UpdatePageProgressFromScroll(double? explicitVerticalOffset = null)
    {
        if (_isProgrammaticPageScroll || ViewModel.CurrentDocument is not { IsContinuous: true, Pages.Count: > 0 } doc || ContinuousScrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        double vOffset = explicitVerticalOffset ?? ContinuousScrollViewer.VerticalOffset;
        double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
        double centerY = (vOffset + (ContinuousScrollViewer.ViewportHeight / 2.0)) / zoom;

        double currentY = 24.0;
        var pages = doc.Pages;
        int step = doc.IsDoublePage ? 2 : 1;
        int targetIndex = pages.Count - 1;
        double fractionalIndex = pages.Count - 1;

        for (int i = 0; i < pages.Count; i += step)
        {
            double pageHeight = pages[i].DisplayHeightDip;
            if (centerY < currentY)
            {
                targetIndex = i;
                fractionalIndex = i;
                break;
            }

            if (centerY <= currentY + pageHeight + 8.0)
            {
                targetIndex = i;
                double fraction = Math.Clamp((centerY - currentY) / Math.Max(1.0, pageHeight), 0.0, 1.0);
                fractionalIndex = i + (fraction * step);
                break;
            }

            currentY += pageHeight + 16.0;
        }

        if (doc.CurrentPageIndex != targetIndex)
        {
            _isUpdatingPageFromScroll = true;
            try
            {
                doc.CurrentPageIndex = targetIndex;
            }
            finally
            {
                _isUpdatingPageFromScroll = false;
            }
        }

        if (Math.Abs(doc.FractionalPageIndex - fractionalIndex) > 0.0005)
        {
            doc.FractionalPageIndex = fractionalIndex;
        }
    }

    private void UpdateCurrentPageIndexFromViewport()
    {
        if (_isProgrammaticPageScroll || ViewModel.CurrentDocument is null || !ViewModel.CurrentDocument.IsContinuous || ContinuousScrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        if (ViewModel.CurrentDocument.Pages.Count == 0)
        {
            return;
        }

        int targetIndex = CalculateCurrentPageIndexFromScroll();
        if (ViewModel.CurrentDocument.CurrentPageIndex != targetIndex)
        {
            _isUpdatingPageFromScroll = true;
            try
            {
                ViewModel.CurrentDocument.CurrentPageIndex = targetIndex;
            }
            finally
            {
                _isUpdatingPageFromScroll = false;
            }
        }
    }

    private int CalculateCurrentPageIndexFromScroll(double? explicitVerticalOffset = null)
    {
        if (ViewModel.CurrentDocument is null || ViewModel.CurrentDocument.Pages.Count == 0)
        {
            return 0;
        }

        if (!ViewModel.CurrentDocument.IsContinuous || ContinuousScrollViewer.ViewportHeight <= 0)
        {
            return ViewModel.CurrentDocument.CurrentPageIndex;
        }

        double vOffset = explicitVerticalOffset ?? ContinuousScrollViewer.VerticalOffset;
        double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
        double centerY = (vOffset + (ContinuousScrollViewer.ViewportHeight / 2.0)) / zoom;

        double currentY = 24.0; // Margin.Top
        var pages = ViewModel.CurrentDocument.Pages;
        int step = ViewModel.CurrentDocument.IsDoublePage ? 2 : 1;
        for (int i = 0; i < pages.Count; i += step)
        {
            double pageHeight = pages[i].DisplayHeightDip;
            if (centerY <= currentY + pageHeight + 8.0)
            {
                return i;
            }
            currentY += pageHeight + 16.0; // Spacing = 16
        }

        return pages.Count - 1;
    }

    private double CalculateFractionalPageIndex(double? explicitVerticalOffset = null)
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return 0.0;
        }

        if (!doc.IsContinuous || ContinuousScrollViewer.ViewportHeight <= 0)
        {
            return doc.CurrentPageIndex;
        }

        double vOffset = explicitVerticalOffset ?? ContinuousScrollViewer.VerticalOffset;
        double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
        double centerY = (vOffset + (ContinuousScrollViewer.ViewportHeight / 2.0)) / zoom;

        double currentY = 24.0;
        int step = doc.IsDoublePage ? 2 : 1;
        for (int i = 0; i < doc.Pages.Count; i += step)
        {
            double pageHeight = doc.Pages[i].DisplayHeightDip;
            if (centerY < currentY)
            {
                return i;
            }

            if (centerY <= currentY + pageHeight + 8.0)
            {
                double fraction = Math.Clamp((centerY - currentY) / Math.Max(1.0, pageHeight), 0.0, 1.0);
                return i + (fraction * step);
            }

            currentY += pageHeight + 16.0;
        }

        return doc.Pages.Count - 1;
    }

    private void ScrollToPage(int pageIndex, bool allowUnclampedOffset = false)
    {
        if (ViewModel.CurrentDocument is not { } doc || pageIndex < 0 || pageIndex >= doc.Pages.Count)
        {
            return;
        }

        _isProgrammaticPageScroll = true;

        if (ContinuousScrollViewer.VerticalScrollMode != ScrollMode.Enabled)
        {
            ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
        }

        if (doc.IsContinuous)
        {
            double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
            double targetY = 24.0;
            var pages = doc.Pages;
            int step = doc.IsDoublePage ? 2 : 1;
            int targetRow = pageIndex / step;
            for (int r = 0; r < targetRow && r * step < pages.Count; r++)
            {
                targetY += pages[r * step].DisplayHeightDip + 16.0;
            }

            double rawOffset = Math.Max(0.0, targetY * zoom);
            double verticalOffset = (allowUnclampedOffset || ContinuousScrollViewer.ScrollableHeight <= 0.5)
                ? rawOffset
                : Math.Clamp(rawOffset, 0.0, ContinuousScrollViewer.ScrollableHeight);
            ContinuousScrollViewer.ChangeView(null, verticalOffset, null, disableAnimation: true);
            doc.LastVerticalOffset = verticalOffset;
            doc.FractionalPageIndex = pageIndex;
        }
        else
        {
            ContinuousScrollViewer.ChangeView(0, 0, null, disableAnimation: true);
            doc.LastVerticalOffset = 0;
            doc.FractionalPageIndex = pageIndex;
            if (_isFittedVertically || ViewModel.IsPresentationMode)
            {
                DispatcherQueue.TryEnqueue(() => ExecuteFitVertical(force: true));
            }
        }

        ScheduleViewportUpdate();

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _isProgrammaticPageScroll = false;
        });
    }

    private void ApplyZoomAroundCenter(float newZoom)
    {
        if (_isApplyingZoom)
        {
            return;
        }

        _isApplyingZoom = true;
        try
        {
            double oldZoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
            double halfViewportW = ContinuousScrollViewer.ViewportWidth / 2.0;
            double halfViewportH = ContinuousScrollViewer.ViewportHeight / 2.0;

            double newOffsetX;
            if (ContinuousScrollViewer.ScrollableWidth > 0.5)
            {
                double viewportCenterX = (ContinuousScrollViewer.HorizontalOffset + halfViewportW) / oldZoom;
                newOffsetX = Math.Max(0, viewportCenterX * newZoom - halfViewportW);
            }
            else
            {
                double unzoomedW = ContinuousPagesRepeater.ActualWidth + ContinuousPagesRepeater.Margin.Left + ContinuousPagesRepeater.Margin.Right;
                newOffsetX = Math.Max(0, (unzoomedW * newZoom - ContinuousScrollViewer.ViewportWidth) / 2.0);
            }

            double viewportCenterY = (ContinuousScrollViewer.VerticalOffset + halfViewportH) / oldZoom;
            double newOffsetY = Math.Max(0, viewportCenterY * newZoom - halfViewportH);

            if (!_isHorizontalPushAnimating && PagesRepeaterPushTransform is not null && newZoom > 0.05f)
            {
                PagesRepeaterPushTransform.TranslateX = GetTargetDocumentOffsetDip(newZoom) / newZoom;
            }

            if (!_isHorizontalPushAnimating && FloatingTopTransform is not null)
            {
                FloatingTopTransform.TranslateX = GetTargetFloatingTopOffsetDip();
            }

            ContinuousScrollViewer.ChangeView(newOffsetX, newOffsetY, newZoom, disableAnimation: true);

            ViewModel.SetZoom(newZoom);
            UpdateViewportPages(newZoom);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                UpdateViewportPages(newZoom);
            });
            ScheduleViewportUpdate();
        }
        finally
        {
            _isApplyingZoom = false;
        }
    }

    /// <summary>
    /// Executes vertical fitting, adjusting zoom so the current page (or page pair) fits comfortably within the viewport.
    /// </summary>
    /// <param name="viewportW">Optional explicit viewport width.</param>
    /// <param name="viewportH">Optional explicit viewport height.</param>
    /// <param name="force">If true, overrides any in-flight fit operation.</param>
    public void ExecuteFitVertical(double? viewportW = null, double? viewportH = null, bool force = false)
    {
        if ((_isApplyingFitZoom && !force) || ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        _isApplyingFitZoom = true;
        try
        {
            int cur = Math.Clamp(doc.CurrentPageIndex, 0, doc.Pages.Count - 1);
            var page = doc.Pages[cur];

            bool isPres = ViewModel.IsPresentationMode;
            // RepeaterMargin in continuous mode is 24 on left and 24 on right (48 total).
            // Add safety margin so page fits comfortably with clean padding and never triggers horizontal scrollbar.
            double marginW = isPres ? 0.0 : (!doc.IsContinuous ? 32.0 : 64.0);
            double marginH = isPres ? 0.0 : (!doc.IsContinuous ? 32.0 : 48.0);

            double rawWidth = ContinuousScrollViewer.ActualWidth > 0
                ? ContinuousScrollViewer.ActualWidth
                : (_lastHandledScrollViewerSize.Width > 0 ? _lastHandledScrollViewerSize.Width : ContinuousScrollViewer.ViewportWidth);

            double vWidth = (viewportW.HasValue && viewportW.Value > 0)
                ? viewportW.Value
                : GetAvailableDocumentWidthDip(rawWidth);

            double vHeight = (viewportH.HasValue && viewportH.Value > 0)
                ? viewportH.Value
                : (ContinuousScrollViewer.ActualHeight > 0
                    ? ContinuousScrollViewer.ActualHeight
                    : (_lastHandledScrollViewerSize.Height > 0 ? _lastHandledScrollViewerSize.Height : ContinuousScrollViewer.ViewportHeight));

            double availW = Math.Max(100.0, vWidth - marginW);
            double availH = Math.Max(100.0, vHeight - marginH);
            double targetW = doc.IsDoublePage ? (page.DisplayWidthDip * 2.0 + 16.0) : page.DisplayWidthDip;
            double targetH = page.DisplayHeightDip;
            double targetZoom = Math.Clamp(Math.Min(availH / targetH, availW / targetW), DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);

            double hOffset = 0.0;
            double vOffset = 0.0;

            if (doc.IsContinuous)
            {
                double targetY = 24.0;
                int step = doc.IsDoublePage ? 2 : 1;
                int targetRow = cur / step;
                for (int r = 0; r < targetRow && r * step < doc.Pages.Count; r++)
                {
                    targetY += doc.Pages[r * step].DisplayHeightDip + 16.0;
                }

                double remainingY = Math.Max(0, vHeight - (targetH * targetZoom));
                vOffset = Math.Max(0, (targetY * targetZoom) - (remainingY / 2.0));
                hOffset = Math.Max(0, ((targetW * targetZoom) - vWidth) / 2.0);
                ContinuousScrollViewer.ChangeView(hOffset, vOffset, (float)targetZoom, disableAnimation: true);
            }
            else
            {
                ContinuousScrollViewer.ChangeView(0, 0, (float)targetZoom, disableAnimation: true);
            }

            if (!_isHorizontalPushAnimating && PagesRepeaterPushTransform is not null && targetZoom > 0.05)
            {
                PagesRepeaterPushTransform.TranslateX = GetTargetDocumentOffsetDip() / targetZoom;
            }

            _isFittedVertically = true;
            doc.ZoomFactor = targetZoom;
            UpdateViewportPages(targetZoom);
            ScheduleViewportUpdate();

            bool enqueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    if (doc.IsContinuous)
                    {
                        ContinuousScrollViewer.ChangeView(hOffset, vOffset, null, disableAnimation: true);
                    }
                    else
                    {
                        ContinuousScrollViewer.ChangeView(0, 0, (float)targetZoom, disableAnimation: true);
                    }
                }
                finally
                {
                    if (Math.Abs(ContinuousScrollViewer.ZoomFactor - targetZoom) < 0.01)
                    {
                        _isApplyingFitZoom = false;
                    }
                }
            });

            if (!enqueued)
            {
                _isApplyingFitZoom = false;
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _isApplyingFitZoom = false;
                    });
                });
            }
        }
        catch
        {
            _isApplyingFitZoom = false;
            throw;
        }
    }

    /// <summary>
    /// Executes horizontal fitting, adjusting zoom so the current page (or page pair) fits width-wise within the viewport.
    /// </summary>
    /// <param name="viewportW">Optional explicit viewport width.</param>
    /// <param name="force">If true, overrides any in-flight fit operation.</param>
    public void ExecuteFitHorizontal(double? viewportW = null, bool force = false)
    {
        if ((_isApplyingFitZoom && !force) || ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        _isApplyingFitZoom = true;
        try
        {
            int cur = Math.Clamp(doc.CurrentPageIndex, 0, doc.Pages.Count - 1);
            var page = doc.Pages[cur];

            bool isPres = ViewModel.IsPresentationMode;
            // Continuous mode has RepeaterMargin of 24 on left and 24 on right = 48 DIPs.
            // We include the margin in base content width plus extra breathing room so horizontal scrollbar never appears.
            double baseMargin = (isPres || !doc.IsContinuous) ? 0.0 : 48.0;
            double targetW = (doc.IsDoublePage ? (page.DisplayWidthDip * 2.0 + 16.0) : page.DisplayWidthDip) + baseMargin;

            // Extra safety margin (at least 32 DIPs total: 16 on each side) to guarantee ExtentWidth < ViewportWidth
            double safetyPadding = 32.0;

            double rawWidth = ContinuousScrollViewer.ActualWidth > 0
                ? ContinuousScrollViewer.ActualWidth
                : (_lastHandledScrollViewerSize.Width > 0 ? _lastHandledScrollViewerSize.Width : ContinuousScrollViewer.ViewportWidth);

            double vWidth = (viewportW.HasValue && viewportW.Value > 0)
                ? viewportW.Value
                : GetAvailableDocumentWidthDip(rawWidth);

            double availW = Math.Max(100.0, vWidth - safetyPadding);
            double targetZoom = Math.Clamp(availW / targetW, DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);

            double? targetVOffset = null;
            if (doc.IsContinuous)
            {
                double targetY = 24.0;
                int step = doc.IsDoublePage ? 2 : 1;
                int targetRow = cur / step;
                for (int r = 0; r < targetRow && r * step < doc.Pages.Count; r++)
                {
                    targetY += doc.Pages[r * step].DisplayHeightDip + 16.0;
                }
                targetVOffset = targetY * targetZoom;
            }
            else
            {
                targetVOffset = 0;
            }

            if (!_isHorizontalPushAnimating && PagesRepeaterPushTransform is not null && targetZoom > 0.05)
            {
                PagesRepeaterPushTransform.TranslateX = GetTargetDocumentOffsetDip() / targetZoom;
            }

            _isFittedVertically = false;
            ContinuousScrollViewer.ChangeView(0, targetVOffset, (float)targetZoom, disableAnimation: true);
            doc.ZoomFactor = targetZoom;
            UpdateViewportPages(targetZoom);
            ScheduleViewportUpdate();

            bool enqueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    if (doc.IsContinuous)
                    {
                        ContinuousScrollViewer.ChangeView(0, targetVOffset, null, disableAnimation: true);
                    }
                    else
                    {
                        ContinuousScrollViewer.ChangeView(0, targetVOffset, (float)targetZoom, disableAnimation: true);
                    }
                }
                finally
                {
                    if (Math.Abs(ContinuousScrollViewer.ZoomFactor - targetZoom) < 0.01)
                    {
                        _isApplyingFitZoom = false;
                    }
                }
            });

            if (!enqueued)
            {
                _isApplyingFitZoom = false;
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _isApplyingFitZoom = false;
                    });
                });
            }
        }
        catch
        {
            _isApplyingFitZoom = false;
            throw;
        }
    }

    private static bool IsKeyDown(Windows.System.VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    private void OnScrollViewerPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_isAutoScrolling)
        {
            StopAutoScroll();
        }

        if (e.Handled || ViewModel.CurrentDocument is not { } doc)
        {
            return;
        }

        doc.NotifyUserDocumentScrollInitiated();

        var props = e.GetCurrentPoint(ContinuousScrollViewer).Properties;
        bool isCtrlPressed = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0
            || IsKeyDown(Windows.System.VirtualKey.Control)
            || IsKeyDown(Windows.System.VirtualKey.LeftControl)
            || IsKeyDown(Windows.System.VirtualKey.RightControl);

        bool isShiftDown = IsKeyDown(Windows.System.VirtualKey.Shift)
            || IsKeyDown(Windows.System.VirtualKey.LeftShift)
            || IsKeyDown(Windows.System.VirtualKey.RightShift);
        bool isShiftPressed = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0
            || isShiftDown
            || props.IsHorizontalMouseWheel;

        if (!isShiftPressed || isCtrlPressed)
        {
            if (ContinuousScrollViewer.VerticalScrollMode != ScrollMode.Enabled)
            {
                ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
            }
        }

        if (isCtrlPressed)
        {
            _isFittedVertically = false;
            return;
        }

        if (isShiftPressed)
        {
            e.Handled = true;
            int delta = props.MouseWheelDelta;
            if (delta != 0)
            {
                _isFittedVertically = false;
                double lockedVerticalOffset = ContinuousScrollViewer.VerticalOffset;

                if (ContinuousScrollViewer.VerticalScrollMode != ScrollMode.Disabled)
                {
                    ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
                }

                if (ContinuousScrollViewer.ScrollableWidth > 1.0)
                {
                    double scrollDelta = delta > 0 ? -120.0 : 120.0;
                    double newOffset = Math.Clamp(
                        ContinuousScrollViewer.HorizontalOffset + scrollDelta,
                        0,
                        ContinuousScrollViewer.ScrollableWidth);

                    ContinuousScrollViewer.ChangeView(newOffset, lockedVerticalOffset, null, disableAnimation: true);
                }
                else if (!doc.IsContinuous)
                {
                    if (delta > 0)
                    {
                        doc.PreviousPage();
                    }
                    else
                    {
                        doc.NextPage();
                    }
                }
                else
                {
                    ContinuousScrollViewer.ChangeView(null, lockedVerticalOffset, null, disableAnimation: true);
                }

                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (ContinuousScrollViewer.VerticalScrollMode != ScrollMode.Enabled)
                    {
                        ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
                    }
                });
            }
            return;
        }

        if (!doc.IsContinuous)
        {
            int delta = props.MouseWheelDelta;
            if (delta != 0)
            {
                bool canScrollVertically = ContinuousScrollViewer.ScrollableHeight > 4.0 && !_isFittedVertically;
                bool atTop = ContinuousScrollViewer.VerticalOffset <= 4.0;
                bool atBottom = ContinuousScrollViewer.VerticalOffset >= ContinuousScrollViewer.ScrollableHeight - 4.0;

                if (!canScrollVertically || (delta > 0 && atTop) || (delta < 0 && atBottom))
                {
                    e.Handled = true;
                    if (delta > 0)
                    {
                        doc.PreviousPage();
                    }
                    else
                    {
                        doc.NextPage();
                    }

                    if (_isFittedVertically)
                    {
                        DispatcherQueue.TryEnqueue(() => ExecuteFitVertical());
                    }
                }
            }
        }
    }

    private void OnPrevZonePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        PrevPageArrowPill.Opacity = 1.0;
    }

    private void OnPrevZonePointerExited(object sender, PointerRoutedEventArgs e)
    {
        PrevPageArrowPill.Opacity = 0.0;
    }

    private void OnPrevZoneTapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.CurrentDocument?.PreviousPage();
        if (_isFittedVertically)
        {
            DispatcherQueue.TryEnqueue(() => ExecuteFitVertical());
        }
    }

    private void OnNextZonePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        NextPageArrowPill.Opacity = 1.0;
    }

    private void OnNextZonePointerExited(object sender, PointerRoutedEventArgs e)
    {
        NextPageArrowPill.Opacity = 0.0;
    }

    private void OnNextZoneTapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.CurrentDocument?.NextPage();
        if (_isFittedVertically)
        {
            DispatcherQueue.TryEnqueue(() => ExecuteFitVertical());
        }
    }

    private void OnExitPresentationZonePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ExitPresentationCloseBtnPill.Opacity = 1.0;
    }

    private void OnExitPresentationZonePointerExited(object sender, PointerRoutedEventArgs e)
    {
        ExitPresentationCloseBtnPill.Opacity = 0.0;
    }

    private void OnExitPresentationZoneTapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.ExitPresentationCommand.Execute(null);
    }

    private void OnDocumentPointerMoved(object sender, PointerRoutedEventArgs e)
    {
    }

    private void ApplyInitialZoomForDocument()
    {
        if (ViewModel.CurrentDocument is null)
        {
            return;
        }

        ApplyZoomPreferenceByIndex(ViewModel.DefaultZoomIndex);
    }

    private void OnDefaultZoomPreferenceChanged(int zoomIndex)
    {
        if (ViewModel.CurrentDocument is not null && !ViewModel.IsPresentationMode)
        {
            ApplyZoomPreferenceByIndex(zoomIndex);
        }
    }

    private void ApplyCustomRestoredZoom(DocumentViewModel doc, double restoredZoom)
    {
        _isFittedVertically = false;
        double? targetVOffset = null;
        if (doc.IsContinuous)
        {
            double targetY = 24.0;
            var pages = doc.Pages;
            int step = doc.IsDoublePage ? 2 : 1;
            int targetRow = doc.CurrentPageIndex / step;
            for (int r = 0; r < targetRow && r * step < pages.Count; r++)
            {
                targetY += pages[r * step].DisplayHeightDip + 16.0;
            }
            targetVOffset = targetY * restoredZoom;
        }
        else
        {
            targetVOffset = 0;
        }

        ContinuousScrollViewer.ChangeView(0, targetVOffset, (float)restoredZoom, disableAnimation: true);
        doc.ZoomFactor = restoredZoom;
        UpdateZoomSlider(restoredZoom);
        UpdateViewportPages(restoredZoom);
        ScheduleViewportUpdate();
    }

    private void ApplyZoomPreferenceByIndex(int zoomIndex)
    {
        switch (zoomIndex)
        {
            case 0:
                ExecuteFitHorizontal(force: true);
                break;
            case 1:
                ExecuteFitVertical(force: true);
                break;
            default:
                _isFittedVertically = false;
                ApplyZoomAroundCenter(1.0f);
                if (ViewModel.CurrentDocument is { } doc)
                {
                    ScrollToPage(doc.CurrentPageIndex, allowUnclampedOffset: true);
                }
                break;
        }
    }

    private void OnNotificationRequested(string title, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel.TryConsumePendingNotification(out string consumedTitle, out string consumedMessage))
            {
                ShowTopNotification(consumedTitle, consumedMessage);
            }
            else
            {
                ShowTopNotification(title, message);
            }
        });
    }

    private void ShowTopNotification(string title, string message)
    {
        if (TopNotificationInfoBar is null)
        {
            return;
        }

        _topNotificationTimer.Stop();
        TopNotificationInfoBar.Title = title;
        TopNotificationInfoBar.Message = message;
        TopNotificationInfoBar.IsOpen = true;
        _topNotificationTimer.Start();
    }

    private void OnTopNotificationTimerTick(object? sender, object e)
    {
        _topNotificationTimer.Stop();
        if (TopNotificationInfoBar is not null)
        {
            TopNotificationInfoBar.IsOpen = false;
        }
    }

    private void OnTopNotificationInfoBarCloseButtonClick(InfoBar sender, object args)
    {
        _topNotificationTimer.Stop();
        TopNotificationInfoBar.IsOpen = false;
    }

    private void OnPresentationHideTimerTick(object? sender, object e)
    {
        _presentationHideTimer.Stop();
        FullScreenInfoBar.IsOpen = false;
    }

    private void OnFullScreenInfoBarCloseButtonClick(InfoBar sender, object args)
    {
        _presentationHideTimer.Stop();
        FullScreenInfoBar.IsOpen = false;
    }

    private void OnAnnotationNoticeCloseButtonClick(InfoBar sender, object args)
    {
        ViewModel.IsAnnotationNoticeOpen = false;
    }

    private void OnEscapeAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsSearchOpen)
        {
            args.Handled = true;
            ViewModel.CloseSearch();
            ReturnFocusToViewer();
        }
        else if (ViewModel.IsPresentationMode)
        {
            args.Handled = true;
            ViewModel.ExitPresentationCommand.Execute(null);
        }
        else if (ViewModel.IsFullScreen)
        {
            args.Handled = true;
            ViewModel.ToggleFullScreenCommand.Execute(null);
        }
    }

    /// <summary>
    /// Handles window-level tunneling key events for document navigation and classic keyboard shortcuts.
    /// </summary>
    /// <param name="e">Key event arguments.</param>
    public void HandleWindowPreviewKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (_isAutoScrolling)
        {
            StopAutoScroll();
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                return;
            }
        }

        object? focusedObj = XamlRoot is not null ? FocusManager.GetFocusedElement(XamlRoot) : null;
        if (IsInsideContentDialog(focusedObj as DependencyObject))
        {
            return;
        }

        bool isCtrl = IsKeyDown(Windows.System.VirtualKey.Control)
            || IsKeyDown(Windows.System.VirtualKey.LeftControl)
            || IsKeyDown(Windows.System.VirtualKey.RightControl);
        bool isShift = IsKeyDown(Windows.System.VirtualKey.Shift)
            || IsKeyDown(Windows.System.VirtualKey.LeftShift)
            || IsKeyDown(Windows.System.VirtualKey.RightShift);
        bool isAlt = IsKeyDown(Windows.System.VirtualKey.Menu)
            || IsKeyDown(Windows.System.VirtualKey.LeftMenu)
            || IsKeyDown(Windows.System.VirtualKey.RightMenu);

        // Global shortcuts that work even when the search box or page box is focused:
        if (isAlt && !isCtrl && e.Key == Windows.System.VirtualKey.Enter)
        {
            if (ViewModel.HasDocument)
            {
                e.Handled = true;
                _ = ViewModel.TogglePropertiesCommand.ExecuteAsync(null);
                return;
            }
        }

        if (isCtrl && !isAlt)
        {
            if (e.Key == Windows.System.VirtualKey.W || e.Key == Windows.System.VirtualKey.F4)
            {
                if (ViewModel.HasDocument)
                {
                    e.Handled = true;
                    ViewModel.CloseDocument();
                }
                return;
            }

            if (e.Key == Windows.System.VirtualKey.O && !isShift)
            {
                e.Handled = true;
                _ = ViewModel.OpenFileCommand.ExecuteAsync(null);
                return;
            }

            if (e.Key == Windows.System.VirtualKey.F && !isShift)
            {
                if (ViewModel.HasDocument)
                {
                    e.Handled = true;
                    ViewModel.OpenSearch();
                }
                return;
            }
        }

        if (!isCtrl && !isAlt)
        {
            if (e.Key == Windows.System.VirtualKey.F3 && ViewModel.HasDocument)
            {
                e.Handled = true;
                if (!ViewModel.IsSearchOpen)
                {
                    ViewModel.OpenSearch();
                }
                else if (isShift)
                {
                    _ = ViewModel.FindPreviousMatchAsync();
                }
                else
                {
                    _ = ViewModel.FindNextMatchAsync();
                }
                return;
            }

            if (e.Key == Windows.System.VirtualKey.F5 && ViewModel.HasDocument)
            {
                e.Handled = true;
                ViewModel.StartPresentation();
                return;
            }

            if (e.Key == Windows.System.VirtualKey.F11)
            {
                e.Handled = true;
                ViewModel.ToggleFullScreen();
                return;
            }
        }

        if (isCtrl && !isAlt && !isShift && e.Key == Windows.System.VirtualKey.C && ViewModel.HasTextSelection)
        {
            if (focusedObj is not TextBox focusedTextBox || focusedTextBox.SelectionLength == 0)
            {
                e.Handled = true;
                ViewModel.CopySelectedText();
                return;
            }
        }

        // If a TextBox or PasswordBox has focus, let it handle its own text editing and caret navigation.
        if (focusedObj is TextBox or PasswordBox)
        {
            return;
        }

        if (!isCtrl && !isAlt && e.Key == Windows.System.VirtualKey.Escape)
        {
            if (ViewModel.IsSearchOpen)
            {
                e.Handled = true;
                ViewModel.CloseSearch();
                ReturnFocusToViewer();
                return;
            }

            if (ViewModel.HasTextSelection)
            {
                e.Handled = true;
                ViewModel.CurrentDocument?.ClearSelection();
                return;
            }

            if (ViewModel.IsPresentationMode)
            {
                e.Handled = true;
                ViewModel.ExitPresentation();
                return;
            }

            if (ViewModel.IsFullScreen)
            {
                e.Handled = true;
                ViewModel.ToggleFullScreen();
                return;
            }

            if (ViewModel.IsAnnotationToolbarOpen)
            {
                e.Handled = true;
                ViewModel.IsAnnotationToolbarOpen = false;
                ViewModel.IsAnnotationNoticeOpen = false;
                return;
            }
        }

        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        if (isCtrl && !isAlt)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.C when !isShift:
                    if (ViewModel.HasTextSelection)
                    {
                        e.Handled = true;
                        ViewModel.CopySelectedText();
                    }
                    return;

                case Windows.System.VirtualKey.S:
                    e.Handled = true;
                    _ = ViewModel.SaveAsCommand.ExecuteAsync(null);
                    return;

                case Windows.System.VirtualKey.P:
                    e.Handled = true;
                    _ = ViewModel.PrintAsync();
                    return;

                case Windows.System.VirtualKey.N when isShift:
                    e.Handled = true;
                    _ = ViewModel.OpenCopyCommand.ExecuteAsync(null);
                    return;

                case Windows.System.VirtualKey.O when isShift:
                    e.Handled = true;
                    _ = ViewModel.OpenWithCommand.ExecuteAsync(null);
                    return;

                case Windows.System.VirtualKey.B:
                    e.Handled = true;
                    ViewModel.ToggleSidebar();
                    return;

                case Windows.System.VirtualKey.I:
                    e.Handled = true;
                    ViewModel.ToggleNightMode();
                    return;

                case Windows.System.VirtualKey.R:
                    e.Handled = true;
                    if (isShift)
                    {
                        ViewModel.RotateCounterClockwise();
                    }
                    else
                    {
                        ViewModel.RotateClockwise();
                    }
                    return;

                case Windows.System.VirtualKey.Right:
                    e.Handled = true;
                    ViewModel.RotateClockwise();
                    return;

                case Windows.System.VirtualKey.Left:
                    e.Handled = true;
                    ViewModel.RotateCounterClockwise();
                    return;

                case Windows.System.VirtualKey.Add:
                case (Windows.System.VirtualKey)187:
                    e.Handled = true;
                    OnZoomInClick(this, new RoutedEventArgs());
                    return;

                case Windows.System.VirtualKey.Subtract:
                case (Windows.System.VirtualKey)189:
                    e.Handled = true;
                    OnZoomOutClick(this, new RoutedEventArgs());
                    return;

                case Windows.System.VirtualKey.Number0:
                case Windows.System.VirtualKey.NumberPad0:
                    e.Handled = true;
                    ExecuteFitVertical(force: true);
                    return;

                case Windows.System.VirtualKey.Number1:
                case Windows.System.VirtualKey.NumberPad1:
                    e.Handled = true;
                    _isFittedVertically = false;
                    ApplyZoomAroundCenter(1.0f);
                    return;

                case Windows.System.VirtualKey.Number2:
                case Windows.System.VirtualKey.NumberPad2:
                    e.Handled = true;
                    ExecuteFitHorizontal(force: true);
                    return;

                case Windows.System.VirtualKey.Home:
                    e.Handled = true;
                    NavigateToDocumentStart();
                    return;

                case Windows.System.VirtualKey.End:
                    e.Handled = true;
                    NavigateToDocumentEnd();
                    return;
            }

            return;
        }

        if (isAlt && !isCtrl && e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = ViewModel.ShowPropertiesCommand.ExecuteAsync(null);
            return;
        }

        if (!isCtrl && !isAlt)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.F9:
                    e.Handled = true;
                    ViewModel.ToggleSidebar();
                    return;

                case Windows.System.VirtualKey.Down:
                    e.Handled = true;
                    HandleDirectionalVertical(down: true, isPageStep: false);
                    return;

                case Windows.System.VirtualKey.Up:
                    e.Handled = true;
                    HandleDirectionalVertical(down: false, isPageStep: false);
                    return;

                case Windows.System.VirtualKey.PageDown:
                    e.Handled = true;
                    HandleDirectionalVertical(down: true, isPageStep: true);
                    return;

                case Windows.System.VirtualKey.PageUp:
                    e.Handled = true;
                    HandleDirectionalVertical(down: false, isPageStep: true);
                    return;

                case Windows.System.VirtualKey.Space:
                    e.Handled = true;
                    HandleDirectionalVertical(down: !isShift, isPageStep: true);
                    return;

                case Windows.System.VirtualKey.Right:
                    e.Handled = true;
                    HandleDirectionalHorizontal(right: true);
                    return;

                case Windows.System.VirtualKey.Left:
                    e.Handled = true;
                    HandleDirectionalHorizontal(right: false);
                    return;

                case Windows.System.VirtualKey.Home:
                    e.Handled = true;
                    NavigateToDocumentStart();
                    return;

                case Windows.System.VirtualKey.End:
                    e.Handled = true;
                    NavigateToDocumentEnd();
                    return;
            }
        }
    }

    private static bool IsInsideContentDialog(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ContentDialog)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void HandleDirectionalVertical(bool down, bool isPageStep)
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        if (doc.IsContinuous && !ViewModel.IsPresentationMode)
        {
            double step = isPageStep
                ? Math.Max(160.0, ContinuousScrollViewer.ViewportHeight * 0.85)
                : 84.0;
            ScrollVerticallyBy(down ? step : -step);
            return;
        }

        // Non-continuous mode or Presentation mode:
        if (!isPageStep && !ViewModel.IsPresentationMode && !_isFittedVertically && ContinuousScrollViewer.ScrollableHeight > 4.0)
        {
            bool atTop = ContinuousScrollViewer.VerticalOffset <= 4.0;
            bool atBottom = ContinuousScrollViewer.VerticalOffset >= ContinuousScrollViewer.ScrollableHeight - 4.0;
            if ((down && !atBottom) || (!down && !atTop))
            {
                double newV = Math.Clamp(
                    ContinuousScrollViewer.VerticalOffset + (down ? 84.0 : -84.0),
                    0.0,
                    ContinuousScrollViewer.ScrollableHeight);
                ContinuousScrollViewer.ChangeView(null, newV, null, disableAnimation: true);
                doc.LastVerticalOffset = newV;
                ScheduleViewportUpdate();
                return;
            }
        }

        FlipPagedSlide(forward: down);
    }

    private void HandleDirectionalHorizontal(bool right)
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        if (!doc.IsContinuous || ViewModel.IsPresentationMode)
        {
            FlipPagedSlide(forward: right);
            return;
        }

        if (ContinuousScrollViewer.ScrollableWidth > 4.0)
        {
            double newH = Math.Clamp(
                ContinuousScrollViewer.HorizontalOffset + (right ? 84.0 : -84.0),
                0.0,
                ContinuousScrollViewer.ScrollableWidth);
            ContinuousScrollViewer.ChangeView(newH, ContinuousScrollViewer.VerticalOffset, null, disableAnimation: true);
            doc.LastHorizontalOffset = newH;
            ScheduleViewportUpdate();
        }
        else
        {
            if (right)
            {
                doc.NextPage();
            }
            else
            {
                doc.PreviousPage();
            }
        }
    }

    private void ScrollVerticallyBy(double deltaY)
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        if (ContinuousScrollViewer.VerticalScrollMode != ScrollMode.Enabled)
        {
            ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
        }

        double maxScroll = Math.Max(0.0, ContinuousScrollViewer.ScrollableHeight);
        double newOffset = Math.Clamp(ContinuousScrollViewer.VerticalOffset + deltaY, 0.0, maxScroll);

        ContinuousScrollViewer.ChangeView(null, newOffset, null, disableAnimation: true);
        SyncScrollMetricsFromVerticalOffset(newOffset);
        ScheduleViewportUpdate();
    }

    private void SyncScrollMetricsFromVerticalOffset(double verticalOffset)
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        doc.LastVerticalOffset = verticalOffset;
        doc.LastHorizontalOffset = ContinuousScrollViewer.HorizontalOffset;

        if (doc.IsContinuous && ContinuousScrollViewer.ViewportHeight > 0)
        {
            int targetIndex = CalculateCurrentPageIndexFromScroll(verticalOffset);
            if (doc.CurrentPageIndex != targetIndex)
            {
                _isUpdatingPageFromScroll = true;
                try
                {
                    doc.CurrentPageIndex = targetIndex;
                }
                finally
                {
                    _isUpdatingPageFromScroll = false;
                }
            }

            doc.FractionalPageIndex = CalculateFractionalPageIndex(verticalOffset);
        }
    }

    private void FlipPagedSlide(bool forward)
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        if (forward)
        {
            doc.NextPage();
        }
        else
        {
            doc.PreviousPage();
        }

        if (_isFittedVertically || ViewModel.IsPresentationMode)
        {
            DispatcherQueue.TryEnqueue(() => ExecuteFitVertical(force: true));
        }
        else
        {
            ContinuousScrollViewer.ChangeView(0, 0, null, disableAnimation: true);
            ScheduleViewportUpdate();
        }
    }

    private void NavigateToDocumentStart()
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        if (doc.IsContinuous && !ViewModel.IsPresentationMode)
        {
            _isUpdatingPageFromScroll = true;
            try
            {
                doc.GoToPage(0);
            }
            finally
            {
                _isUpdatingPageFromScroll = false;
            }

            ContinuousScrollViewer.ChangeView(null, 0.0, null, disableAnimation: true);
            doc.LastVerticalOffset = 0.0;
            doc.FractionalPageIndex = 0.0;
            ScheduleViewportUpdate();
        }
        else
        {
            doc.GoToPage(0);
            if (_isFittedVertically || ViewModel.IsPresentationMode)
            {
                DispatcherQueue.TryEnqueue(() => ExecuteFitVertical(force: true));
            }
            else
            {
                ContinuousScrollViewer.ChangeView(0, 0, null, disableAnimation: true);
                ScheduleViewportUpdate();
            }
        }
    }

    private void NavigateToDocumentEnd()
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        int lastIndex = doc.Pages.Count - 1;
        if (doc.IsContinuous && !ViewModel.IsPresentationMode)
        {
            _isUpdatingPageFromScroll = true;
            try
            {
                doc.GoToPage(lastIndex);
            }
            finally
            {
                _isUpdatingPageFromScroll = false;
            }

            double maxScroll = Math.Max(0.0, ContinuousScrollViewer.ScrollableHeight);
            ContinuousScrollViewer.ChangeView(null, maxScroll, null, disableAnimation: true);
            doc.LastVerticalOffset = maxScroll;
            doc.FractionalPageIndex = lastIndex;
            ScheduleViewportUpdate();
        }
        else
        {
            doc.GoToPage(lastIndex);
            if (_isFittedVertically || ViewModel.IsPresentationMode)
            {
                DispatcherQueue.TryEnqueue(() => ExecuteFitVertical(force: true));
            }
            else
            {
                ContinuousScrollViewer.ChangeView(0, 0, null, disableAnimation: true);
                ScheduleViewportUpdate();
            }
        }
    }

    private void OnSearchFocusRequested()
    {
        FocusSearchBox();
    }

    private void FocusSearchBox()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel.IsSearchOpen && DocumentSearchBox is not null)
            {
                DocumentSearchBox.Focus(FocusState.Programmatic);
                DocumentSearchBox.SelectAll();
            }
        });
    }

    private void OnDocumentSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!ViewModel.IsSearchOpen || ViewModel.CurrentDocument is null)
        {
            return;
        }

        string text = DocumentSearchBox?.Text ?? string.Empty;
        ViewModel.SearchQuery = text;

        _searchDebounceTimer.Stop();
        if (string.IsNullOrEmpty(text))
        {
            ViewModel.CurrentDocument.ClearSearchResults();
            return;
        }

        _searchDebounceTimer.Start();
    }

    private void OnSearchDebounceTimerTick(object? sender, object e)
    {
        _searchDebounceTimer.Stop();
        if (!ViewModel.IsSearchOpen || ViewModel.CurrentDocument is not { } doc)
        {
            return;
        }

        string query = DocumentSearchBox?.Text ?? ViewModel.SearchQuery;
        _ = doc.ExecuteSearchAsync(query, ViewModel.IsSearchMatchCase, jumpToNearestMatch: true);
    }

    private void OnDocumentSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            _searchDebounceTimer.Stop();
            ViewModel.CloseSearch();
            ReturnFocusToViewer();
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _searchDebounceTimer.Stop();
            bool isShift = IsKeyDown(Windows.System.VirtualKey.Shift)
                || IsKeyDown(Windows.System.VirtualKey.LeftShift)
                || IsKeyDown(Windows.System.VirtualKey.RightShift);

            if (isShift)
            {
                _ = ViewModel.FindPreviousMatchAsync();
            }
            else
            {
                _ = ViewModel.FindNextMatchAsync();
            }
        }
        else if (e.Key == Windows.System.VirtualKey.F3)
        {
            e.Handled = true;
            _searchDebounceTimer.Stop();
            bool isShift = IsKeyDown(Windows.System.VirtualKey.Shift)
                || IsKeyDown(Windows.System.VirtualKey.LeftShift)
                || IsKeyDown(Windows.System.VirtualKey.RightShift);

            if (isShift)
            {
                _ = ViewModel.FindPreviousMatchAsync();
            }
            else
            {
                _ = ViewModel.FindNextMatchAsync();
            }
        }
    }

    private void OnSearchPrevClick(object sender, RoutedEventArgs e)
    {
        _searchDebounceTimer.Stop();
        _ = ViewModel.FindPreviousMatchAsync();
        DocumentSearchBox?.Focus(FocusState.Programmatic);
    }

    private void OnSearchNextClick(object sender, RoutedEventArgs e)
    {
        _searchDebounceTimer.Stop();
        _ = ViewModel.FindNextMatchAsync();
        DocumentSearchBox?.Focus(FocusState.Programmatic);
    }

    private void OnSearchMatchCaseToggleClick(object sender, RoutedEventArgs e)
    {
        _searchDebounceTimer.Stop();
        if (ViewModel.CurrentDocument is { } doc && !string.IsNullOrEmpty(ViewModel.SearchQuery))
        {
            _ = doc.ExecuteSearchAsync(ViewModel.SearchQuery, ViewModel.IsSearchMatchCase, jumpToNearestMatch: true);
        }

        DocumentSearchBox?.Focus(FocusState.Programmatic);
    }

    private void OnSearchCloseClick(object sender, RoutedEventArgs e)
    {
        _searchDebounceTimer.Stop();
        ViewModel.CloseSearch();
        ReturnFocusToViewer();
    }

    private void OnContextPrintClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.PrintAsync();
    }

    private void PlayEmptyStateTransitionAnimation()
    {
        if (EmptyStatePanel is null || EmptyStateTransform is null)
        {
            return;
        }

        _transitionStoryboard?.Stop();
        EmptyStatePanel.Opacity = 0.0;
        EmptyStateTransform.TranslateY = 16.0;
        EmptyStateTransform.ScaleX = 0.97;
        EmptyStateTransform.ScaleY = 0.97;

        var sb = new Storyboard();
        var ease = new ExponentialEase { Exponent = 4.5, EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(260));

        var opacityAnim = new DoubleAnimation { From = 0.0, To = 1.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(opacityAnim, EmptyStatePanel);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        sb.Children.Add(opacityAnim);

        var translateAnim = new DoubleAnimation { From = 16.0, To = 0.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(translateAnim, EmptyStateTransform);
        Storyboard.SetTargetProperty(translateAnim, "TranslateY");
        sb.Children.Add(translateAnim);

        var scaleXAnim = new DoubleAnimation { From = 0.97, To = 1.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(scaleXAnim, EmptyStateTransform);
        Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");
        sb.Children.Add(scaleXAnim);

        var scaleYAnim = new DoubleAnimation { From = 0.97, To = 1.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(scaleYAnim, EmptyStateTransform);
        Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");
        sb.Children.Add(scaleYAnim);

        _transitionStoryboard = sb;
        sb.Begin();
    }

    private void PlayDocumentOpenTransitionAnimation()
    {
        if (DocumentAreaGrid is null || DocumentAreaTransform is null)
        {
            return;
        }

        _transitionStoryboard?.Stop();
        DocumentAreaGrid.Opacity = 0.0;
        DocumentAreaTransform.TranslateY = 10.0;

        var sb = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(200));

        var opacityAnim = new DoubleAnimation { From = 0.0, To = 1.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(opacityAnim, DocumentAreaGrid);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        sb.Children.Add(opacityAnim);

        var translateAnim = new DoubleAnimation { From = 10.0, To = 0.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(translateAnim, DocumentAreaTransform);
        Storyboard.SetTargetProperty(translateAnim, "TranslateY");
        sb.Children.Add(translateAnim);

        _transitionStoryboard = sb;
        sb.Begin();
    }

    private double GetAvailableDocumentWidthDip(double rawViewerWidth)
    {
        if (ViewModel.IsPresentationMode)
        {
            return Math.Max(100.0, rawViewerWidth);
        }

        double leftW = (ViewModel.HasDocument && ViewModel.IsSidebarOpen) ? _currentSidebarWidthDip : 0.0;
        double appliedMarginLeft = ContinuousScrollViewer?.Margin.Left ?? 0.0;
        double unsubtractedLeftW = Math.Max(0.0, leftW - appliedMarginLeft);
        return Math.Max(120.0, rawViewerWidth - unsubtractedLeftW);
    }

    private void OnNightModeMenuItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject root)
        {
            AlignMenuFlyoutAcceleratorRight(root);
        }
    }

    private static void AlignMenuFlyoutAcceleratorRight(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock tb && tb.Name == "KeyboardAcceleratorTextBlock")
            {
                tb.TextAlignment = TextAlignment.Right;
                tb.HorizontalAlignment = HorizontalAlignment.Right;
                return;
            }

            AlignMenuFlyoutAcceleratorRight(child);
        }
    }

    private double GetTargetDocumentOffsetDip(double? explicitZoom = null)
    {
        if (ViewModel is null || !ViewModel.HasDocument || ViewModel.IsPresentationMode)
        {
            return 0.0;
        }

        double leftW = (ViewModel.IsSidebarOpen && !ViewModel.IsPresentationMode) ? _currentSidebarWidthDip : 0.0;
        return leftW / 2.0;
    }

    private double GetTargetFloatingTopOffsetDip()
    {
        if (ViewModel is null || !ViewModel.HasDocument || ViewModel.IsPresentationMode)
        {
            return 0.0;
        }

        double leftW = (ViewModel.IsSidebarOpen && !ViewModel.IsPresentationMode) ? _currentSidebarWidthDip : 0.0;
        return leftW / 2.0;
    }

    /// <summary>
    /// Synchronizes all pane-dependent transforms immediately without animation (used on startup, document switch, or splitter drag).
    /// </summary>
    /// <param name="sidebarOpen">Whether the left sidebar is open.</param>
    /// <param name="sidebarWidth">Current width of the left sidebar in DIPs.</param>
    public void SetSidebarStateImmediate(bool sidebarOpen, double sidebarWidth)
    {
        if (sidebarWidth > 0)
        {
            _currentSidebarWidthDip = sidebarWidth;
        }

        SyncPaneOffsetsImmediate();
    }

    private void SyncPaneOffsetsImmediate()
    {
        _horizontalPushStoryboard?.Stop();
        _isHorizontalPushAnimating = false;

        // Keep ContinuousScrollViewer.Margin permanently at 0 so the ScrollViewer never shifts or triggers a second layout push.
        if (ContinuousScrollViewer is not null && ContinuousScrollViewer.Margin.Left != 0.0)
        {
            ContinuousScrollViewer.Margin = new Thickness(0);
        }

        double leftW = (ViewModel.HasDocument && ViewModel.IsSidebarOpen && !ViewModel.IsPresentationMode) ? _currentSidebarWidthDip : 0.0;
        double zoom = ContinuousScrollViewer?.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : (ViewModel.CurrentDocument?.ZoomFactor ?? 1.0);
        double targetDocOffsetDip = GetTargetDocumentOffsetDip(zoom);

        if (PagesRepeaterPushTransform is not null)
        {
            PagesRepeaterPushTransform.TranslateX = targetDocOffsetDip / Math.Max(0.05, zoom);
        }

        if (FloatingTopTransform is not null)
        {
            FloatingTopTransform.TranslateX = GetTargetFloatingTopOffsetDip();
        }

        if (StatusBarBackdropTransform is not null)
        {
            StatusBarBackdropTransform.TranslateX = leftW;
        }

        if (StatusBarLeftTransform is not null)
        {
            StatusBarLeftTransform.TranslateX = leftW;
        }

        if (PrevPageZoneTransform is not null)
        {
            PrevPageZoneTransform.TranslateX = leftW;
        }

        if (NextPageZoneTransform is not null)
        {
            NextPageZoneTransform.TranslateX = 0.0;
        }
    }

    /// <summary>
    /// Smoothly animates the document, status bar, and navigation zones when the left sidebar opens or closes,
    /// pushing the document once by exactly half of the sidebar width simultaneously with the sidebar animation.
    /// </summary>
    /// <param name="sidebarOpen">Whether the left sidebar is opening.</param>
    /// <param name="sidebarWidth">Width of the left sidebar in DIPs.</param>
    public void AnimateSidebarTransition(bool sidebarOpen, double sidebarWidth)
    {
        if (sidebarWidth > 0)
        {
            _currentSidebarWidthDip = sidebarWidth;
        }

        AnimateDocumentAndChromeToTargetOffsets();
    }

    private void AnimateDocumentAndChromeToTargetOffsets()
    {
        if (PagesRepeaterPushTransform is null)
        {
            return;
        }

        double startRepeaterX = PagesRepeaterPushTransform.TranslateX;
        double startTopX = FloatingTopTransform?.TranslateX ?? 0.0;
        double startBackdropX = StatusBarBackdropTransform?.TranslateX ?? 0.0;
        double startLeftX = StatusBarLeftTransform?.TranslateX ?? 0.0;
        double startPrevX = PrevPageZoneTransform?.TranslateX ?? 0.0;

        _horizontalPushStoryboard?.Stop();
        _isHorizontalPushAnimating = true;

        if (ContinuousScrollViewer is not null && ContinuousScrollViewer.Margin.Left != 0.0)
        {
            ContinuousScrollViewer.Margin = new Thickness(0);
        }

        double leftW = (ViewModel.HasDocument && ViewModel.IsSidebarOpen && !ViewModel.IsPresentationMode) ? _currentSidebarWidthDip : 0.0;
        double zoom = ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : (ViewModel.CurrentDocument?.ZoomFactor ?? 1.0);

        double targetDocOffsetDip = GetTargetDocumentOffsetDip(zoom);
        double targetRepeaterX = targetDocOffsetDip / Math.Max(0.05, zoom);
        double targetTopOffsetDip = GetTargetFloatingTopOffsetDip();

        PagesRepeaterPushTransform.TranslateX = startRepeaterX;

        var sb = new Storyboard();
        var ease = new ExponentialEase { Exponent = 4.5, EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(250));

        var docPush = new DoubleAnimation
        {
            From = startRepeaterX,
            To = targetRepeaterX,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(docPush, PagesRepeaterPushTransform);
        Storyboard.SetTargetProperty(docPush, "TranslateX");
        sb.Children.Add(docPush);

        if (FloatingTopTransform is not null)
        {
            FloatingTopTransform.TranslateX = startTopX;
            var topAnim = new DoubleAnimation
            {
                From = startTopX,
                To = targetTopOffsetDip,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(topAnim, FloatingTopTransform);
            Storyboard.SetTargetProperty(topAnim, "TranslateX");
            sb.Children.Add(topAnim);
        }

        if (StatusBarBackdropTransform is not null)
        {
            StatusBarBackdropTransform.TranslateX = startBackdropX;
            var sbBackdropAnim = new DoubleAnimation
            {
                From = startBackdropX,
                To = leftW,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(sbBackdropAnim, StatusBarBackdropTransform);
            Storyboard.SetTargetProperty(sbBackdropAnim, "TranslateX");
            sb.Children.Add(sbBackdropAnim);
        }

        if (StatusBarLeftTransform is not null)
        {
            StatusBarLeftTransform.TranslateX = startLeftX;
            var sbLeftAnim = new DoubleAnimation
            {
                From = startLeftX,
                To = leftW,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(sbLeftAnim, StatusBarLeftTransform);
            Storyboard.SetTargetProperty(sbLeftAnim, "TranslateX");
            sb.Children.Add(sbLeftAnim);
        }

        if (PrevPageZoneTransform is not null)
        {
            PrevPageZoneTransform.TranslateX = startPrevX;
            var prevAnim = new DoubleAnimation
            {
                From = startPrevX,
                To = leftW,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(prevAnim, PrevPageZoneTransform);
            Storyboard.SetTargetProperty(prevAnim, "TranslateX");
            sb.Children.Add(prevAnim);
        }

        if (NextPageZoneTransform is not null)
        {
            var nextAnim = new DoubleAnimation
            {
                From = NextPageZoneTransform.TranslateX,
                To = 0.0,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(nextAnim, NextPageZoneTransform);
            Storyboard.SetTargetProperty(nextAnim, "TranslateX");
            sb.Children.Add(nextAnim);
        }

        sb.Completed += (_, _) =>
        {
            SyncPaneOffsetsImmediate();
            ScheduleViewportUpdate();
        };

        _horizontalPushStoryboard = sb;
        sb.Begin();
    }

    // =========================================================================
    // Native Windows 11 Print Preview & High-DPI Printing (PrintManagerInterop)
    // =========================================================================

    private async Task OnPrintRequestedAsync()
    {
        if (ViewModel.CurrentDocument is not { PageCount: > 0 } doc)
        {
            return;
        }

        nint hwnd = _windowContext.WindowHandle;
        if (hwnd == nint.Zero)
        {
            return;
        }

        try
        {
            PreparePrintDocumentForWindow(hwnd, doc);
            await PrintManagerInterop.ShowPrintUIForWindowAsync(hwnd);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ShowPrintUIForWindowAsync failed: {ex.Message}");
        }
    }

    private void PreparePrintDocumentForWindow(nint hwnd, DocumentViewModel doc)
    {
        _activePrintDocumentVm = doc;

        if (_registeredPrintManager is null || _registeredPrintHwnd != hwnd)
        {
            if (_registeredPrintManager is not null)
            {
                _registeredPrintManager.PrintTaskRequested -= OnPrintTaskRequested;
            }

            _registeredPrintManager = PrintManagerInterop.GetForWindow(hwnd);
            _registeredPrintHwnd = hwnd;
            _registeredPrintManager.PrintTaskRequested += OnPrintTaskRequested;
        }

        CleanupPrintDocumentInstance();

        _printDocument = new PrintDocument();
        _printDocumentSource = _printDocument.DocumentSource;
        _printDocument.Paginate += OnPrintDocumentPaginate;
        _printDocument.GetPreviewPage += OnPrintDocumentGetPreviewPage;
        _printDocument.AddPages += OnPrintDocumentAddPages;
    }

    private void CleanupPrintDocumentInstance()
    {
        if (_printDocument is not null)
        {
            _printDocument.Paginate -= OnPrintDocumentPaginate;
            _printDocument.GetPreviewPage -= OnPrintDocumentGetPreviewPage;
            _printDocument.AddPages -= OnPrintDocumentAddPages;
            _printDocument = null;
        }

        _printDocumentSource = null;
        _currentPrintTaskOptions = null;
        PrintStagingCanvas?.Children.Clear();
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        DocumentViewModel? doc = _activePrintDocumentVm ?? ViewModel.CurrentDocument;
        IPrintDocumentSource? source = _printDocumentSource;
        if (doc is null || source is null)
        {
            return;
        }

        string title = string.IsNullOrWhiteSpace(doc.FileName) ? "WinPDF Document" : doc.FileName;

        PrintTask printTask = args.Request.CreatePrintTask(title, sourceRequestedArgs =>
        {
            PrintTaskSourceRequestedDeferral deferral = sourceRequestedArgs.GetDeferral();
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    sourceRequestedArgs.SetSource(source);
                }
                finally
                {
                    deferral.Complete();
                }
            });
        });

        try
        {
            PrintPageRangeOptions rangeOptions = printTask.Options.PageRangeOptions;
            rangeOptions.AllowAllPages = true;
            rangeOptions.AllowCurrentPage = true;
            rangeOptions.AllowCustomSetOfPages = true;
        }
        catch
        {
        }

        try
        {
            if (doc.Pages.Count > 0)
            {
                int curIdx = Math.Clamp(doc.CurrentPageIndex, 0, doc.Pages.Count - 1);
                double aspect = DocumentViewModel.GetPageDisplayAspectRatio(doc.Pages[curIdx], doc.Rotation);
                printTask.Options.Orientation = aspect > 1.05
                    ? PrintOrientation.Landscape
                    : PrintOrientation.Portrait;
            }
        }
        catch
        {
        }

        printTask.Completed += OnPrintTaskCompleted;
    }

    private void OnPrintTaskCompleted(PrintTask sender, PrintTaskCompletedEventArgs args)
    {
        sender.Completed -= OnPrintTaskCompleted;
        DispatcherQueue.TryEnqueue(() =>
        {
            PrintStagingCanvas?.Children.Clear();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: true);
        });
    }

    private void OnPrintDocumentPaginate(object sender, PaginateEventArgs e)
    {
        DocumentViewModel? doc = _activePrintDocumentVm ?? ViewModel.CurrentDocument;
        if (_printDocument is null || doc is null)
        {
            return;
        }

        _currentPrintTaskOptions = e.PrintTaskOptions;
        _printDocument.SetPreviewPageCount(doc.PageCount, PreviewPageCountType.Final);
    }

    private void OnPrintDocumentGetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        DocumentViewModel? doc = _activePrintDocumentVm ?? ViewModel.CurrentDocument;
        if (_printDocument is null || doc is null || e.PageNumber < 1 || e.PageNumber > doc.PageCount)
        {
            return;
        }

        try
        {
            UIElement pageVisual = BuildPrintPageElement(doc, e.PageNumber - 1, _currentPrintTaskOptions, targetDpi: 150.0);
            _printDocument.SetPreviewPage(e.PageNumber, pageVisual);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetPreviewPage failed for page {e.PageNumber}: {ex.Message}");
        }
    }

    private void OnPrintDocumentAddPages(object sender, AddPagesEventArgs e)
    {
        DocumentViewModel? doc = _activePrintDocumentVm ?? ViewModel.CurrentDocument;
        if (_printDocument is null || doc is null)
        {
            _printDocument?.AddPagesComplete();
            return;
        }

        try
        {
            PrintTaskOptions options = e.PrintTaskOptions ?? _currentPrintTaskOptions!;
            double targetDpi = doc.PageCount <= 30 ? 220.0 : 160.0;
            for (int i = 0; i < doc.PageCount; i++)
            {
                UIElement pageVisual = BuildPrintPageElement(doc, i, options, targetDpi);
                _printDocument.AddPage(pageVisual);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AddPages failed: {ex.Message}");
        }
        finally
        {
            _printDocument.AddPagesComplete();
        }
    }

    private UIElement BuildPrintPageElement(
        DocumentViewModel doc,
        int pageIndex,
        PrintTaskOptions? printOptions,
        double targetDpi)
    {
        PdfPageInfo pageInfo = doc.Pages[pageIndex];
        int effectiveRot = PageCoordinateTransform.NormalizeRotation(pageInfo.Rotation + doc.Rotation);
        bool swap = effectiveRot is 90 or 270;
        double pageWidthPt = Math.Max(1.0, swap ? pageInfo.HeightPt : pageInfo.WidthPt);
        double pageHeightPt = Math.Max(1.0, swap ? pageInfo.WidthPt : pageInfo.HeightPt);

        double paperWidthDip = pageWidthPt * (96.0 / 72.0);
        double paperHeightDip = pageHeightPt * (96.0 / 72.0);
        Rect imageableRect = new(0, 0, paperWidthDip, paperHeightDip);

        if (printOptions is not null)
        {
            try
            {
                PrintPageDescription desc = printOptions.GetPageDescription((uint)(pageIndex + 1));
                if (desc.PageSize.Width > 0 && desc.PageSize.Height > 0)
                {
                    paperWidthDip = desc.PageSize.Width;
                    paperHeightDip = desc.PageSize.Height;
                    imageableRect = (desc.ImageableRect.Width > 0 && desc.ImageableRect.Height > 0)
                        ? desc.ImageableRect
                        : new Rect(0, 0, paperWidthDip, paperHeightDip);
                }
            }
            catch
            {
            }
        }

        double fitScaleDip = Math.Min(imageableRect.Width / pageWidthPt, imageableRect.Height / pageHeightPt);
        double drawWidthDip = Math.Max(1.0, pageWidthPt * fitScaleDip);
        double drawHeightDip = Math.Max(1.0, pageHeightPt * fitScaleDip);
        double offsetLeftDip = imageableRect.X + ((imageableRect.Width - drawWidthDip) / 2.0);
        double offsetTopDip = imageableRect.Y + ((imageableRect.Height - drawHeightDip) / 2.0);

        double dpiScale = Math.Max(1.0, targetDpi / 96.0);
        int renderWidthPx = Math.Max(1, (int)Math.Round(drawWidthDip * dpiScale));
        int renderHeightPx = Math.Max(1, (int)Math.Round(drawHeightDip * dpiScale));

        const int MaxPrintDimensionPx = 2800;
        if (renderWidthPx > MaxPrintDimensionPx || renderHeightPx > MaxPrintDimensionPx)
        {
            double clamp = Math.Min((double)MaxPrintDimensionPx / renderWidthPx, (double)MaxPrintDimensionPx / renderHeightPx);
            renderWidthPx = Math.Max(1, (int)Math.Round(renderWidthPx * clamp));
            renderHeightPx = Math.Max(1, (int)Math.Round(renderHeightPx * clamp));
        }

        int userRot = PageCoordinateTransform.NormalizeRotation(doc.Rotation);
        PdfRenderRequest request = new(
            doc.DocumentId,
            pageIndex,
            renderWidthPx,
            renderHeightPx,
            renderWidthPx,
            renderHeightPx,
            0,
            0,
            userRot,
            PdfRenderFlags.Annotations | PdfRenderFlags.Printing | PdfRenderFlags.LimitedImageCache);

        using PdfBitmapLease lease = _pdfService.RenderAsync(request, PdfJobPriority.Critical).GetAwaiter().GetResult();
        WriteableBitmap? bitmap = CreatePrintWriteableBitmap(lease);

        Canvas pageCanvas = new()
        {
            Width = paperWidthDip,
            Height = paperHeightDip,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White)
        };

        if (bitmap is not null)
        {
            Image pageImage = new()
            {
                Width = drawWidthDip,
                Height = drawHeightDip,
                Stretch = Stretch.Fill,
                Source = bitmap
            };
            Canvas.SetLeft(pageImage, offsetLeftDip);
            Canvas.SetTop(pageImage, offsetTopDip);
            pageCanvas.Children.Add(pageImage);
        }

        if (PrintStagingCanvas is not null)
        {
            PrintStagingCanvas.Children.Add(pageCanvas);
            pageCanvas.Measure(new Size(paperWidthDip, paperHeightDip));
            pageCanvas.Arrange(new Rect(0, 0, paperWidthDip, paperHeightDip));
            pageCanvas.UpdateLayout();
            PrintStagingCanvas.Children.Remove(pageCanvas);
        }
        else
        {
            pageCanvas.Measure(new Size(paperWidthDip, paperHeightDip));
            pageCanvas.Arrange(new Rect(0, 0, paperWidthDip, paperHeightDip));
            pageCanvas.UpdateLayout();
        }

        return pageCanvas;
    }

    private static unsafe WriteableBitmap? CreatePrintWriteableBitmap(PdfBitmapLease lease)
    {
        if (lease.IsDisposed || lease.Width <= 0 || lease.Height <= 0 || lease.SizeInBytes <= 0)
        {
            return null;
        }

        try
        {
            WriteableBitmap bitmap = new(lease.Width, lease.Height);
            if (bitmap.PixelBuffer is IPrintBufferByteAccess byteAccess)
            {
                byte* dest = byteAccess.Buffer;
                byte* src = (byte*)lease.Scan0;
                long bytesToCopy = Math.Min(lease.SizeInBytes, (long)lease.Stride * lease.Height);
                Buffer.MemoryCopy(src, dest, bytesToCopy, bytesToCopy);
            }
            else
            {
                using Stream stream = bitmap.PixelBuffer.AsStream();
                stream.Write(lease.AsSpan());
            }

            bitmap.Invalidate();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void PlaySearchBarOpenAnimation()
    {
        if (FloatingSearchBar is null || FloatingSearchBarTransform is null)
        {
            return;
        }

        if (!_isHorizontalPushAnimating && FloatingTopTransform is not null)
        {
            FloatingTopTransform.TranslateX = GetTargetFloatingTopOffsetDip();
        }

        _searchBarStoryboard?.Stop();
        FloatingSearchBar.Opacity = 0.0;
        FloatingSearchBarTransform.TranslateY = -10.0;

        var sb = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(160));

        var opacityAnim = new DoubleAnimation { From = 0.0, To = 1.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(opacityAnim, FloatingSearchBar);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        sb.Children.Add(opacityAnim);

        var translateAnim = new DoubleAnimation { From = -10.0, To = 0.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(translateAnim, FloatingSearchBarTransform);
        Storyboard.SetTargetProperty(translateAnim, "TranslateY");
        sb.Children.Add(translateAnim);

        _searchBarStoryboard = sb;
        sb.Begin();
    }

    private float GetCurrentZoomFactor()
    {
        return (float)(ViewModel.CurrentDocument?.ZoomFactor ?? ContinuousScrollViewer.ZoomFactor);
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        _isFittedVertically = false;
        float currentZoom = GetCurrentZoomFactor();
        float newZoom = Math.Clamp(currentZoom * 1.25f, 0.25f, 8.0f);
        ApplyZoomAroundCenter(newZoom);
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        _isFittedVertically = false;
        float currentZoom = GetCurrentZoomFactor();
        float newZoom = Math.Clamp(currentZoom / 1.25f, 0.25f, 8.0f);
        ApplyZoomAroundCenter(newZoom);
    }

    private void OnZoomPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string tagStr &&
            double.TryParse(tagStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoomFactor))
        {
            _isFittedVertically = false;
            ApplyZoomAroundCenter((float)zoomFactor);
        }
    }

    private void OnFitWidthClick(object sender, RoutedEventArgs e)
    {
        ExecuteFitHorizontal();
    }

    private void OnFitPageClick(object sender, RoutedEventArgs e)
    {
        ExecuteFitVertical();
    }

    private void OnToolSelectClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetTool(AnnotationTool.Select);
    }

    private void OnToolPenClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetTool(AnnotationTool.Pen);
    }

    private void OnToolHighlighterClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetTool(AnnotationTool.Highlighter);
    }

    private void OnToolEraserClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetTool(AnnotationTool.Eraser);
    }

    private void OnToolTextNoteClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetTool(AnnotationTool.TextNote);
    }

    private void OnDocumentDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = ResourceStrings.Get(ResourceKeys.OpenFile_DragCaption);
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            e.Handled = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void OnDocumentDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var pdfFile = items.OfType<StorageFile>()
                .FirstOrDefault(f => string.Equals(f.FileType, ".pdf", StringComparison.OrdinalIgnoreCase));

            if (pdfFile is not null)
            {
                e.Handled = true;
                await ViewModel.OpenFilePathAsync(pdfFile.Path);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open dropped PDF file: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnStatusBarPageBoxGotFocus(object sender, RoutedEventArgs e)
    {
        _isPageBoxEditing = true;
        StatusBarPageBox.SelectAll();
    }

    private void OnStatusBarPageBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _isPageBoxEditing = false;
            if (int.TryParse(StatusBarPageBox.Text.Trim(), out int pageNumber) && ViewModel.CurrentDocument is not null)
            {
                int targetIndex = Math.Clamp(pageNumber - 1, 0, ViewModel.CurrentDocument.PageCount - 1);
                ViewModel.CurrentDocument.GoToPage(targetIndex);
                ScrollToPage(targetIndex);
                StatusBarPageBox.Text = (targetIndex + 1).ToString();
            }
            else if (ViewModel.CurrentDocument is not null)
            {
                StatusBarPageBox.Text = ViewModel.CurrentDocument.CurrentPageNumber.ToString();
            }

            ReturnFocusToViewer();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            _isPageBoxEditing = false;
            if (ViewModel.CurrentDocument is not null)
            {
                StatusBarPageBox.Text = ViewModel.CurrentDocument.CurrentPageNumber.ToString();
            }

            ReturnFocusToViewer();
        }
    }

    private void OnStatusBarPageBoxLostFocus(object sender, RoutedEventArgs e)
    {
        _isPageBoxEditing = false;
        if (ViewModel.CurrentDocument is not null)
        {
            StatusBarPageBox.Text = ViewModel.CurrentDocument.CurrentPageNumber.ToString();
        }
    }

    private void ReturnFocusToViewer()
    {
        ContinuousScrollViewer.Focus(FocusState.Programmatic);
    }

    // =========================================================================
    // Native Browser-Style Middle-Click Free Autoscroll
    // =========================================================================

    private void OnViewerAutoScrollPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is null)
        {
            return;
        }

        ViewModel.CurrentDocument.NotifyUserDocumentScrollInitiated();

        var point = e.GetCurrentPoint(ScrollViewerContentRoot);
        var props = point.Properties;

        if (_isAutoScrolling)
        {
            StopAutoScroll();
            e.Handled = true;
            return;
        }

        if (point.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && props.IsMiddleButtonPressed)
        {
            StartAutoScroll(point.Position, e.Pointer);
            e.Handled = true;
        }
    }

    private void OnViewerAutoScrollPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isAutoScrolling)
        {
            return;
        }

        _autoScrollCurrentPoint = e.GetCurrentPoint(ScrollViewerContentRoot).Position;
    }

    private void OnViewerAutoScrollPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isAutoScrolling || !_autoScrollMiddleButtonDown)
        {
            return;
        }

        var point = e.GetCurrentPoint(ScrollViewerContentRoot);
        var props = point.Properties;
        if (props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased || !props.IsMiddleButtonPressed)
        {
            _autoScrollMiddleButtonDown = false;
            _autoScrollCurrentPoint = point.Position;

            double dx = _autoScrollCurrentPoint.X - _autoScrollAnchorPoint.X;
            double dy = _autoScrollCurrentPoint.Y - _autoScrollAnchorPoint.Y;
            double distance = Math.Sqrt((dx * dx) + (dy * dy));
            long elapsedMs = Environment.TickCount64 - _autoScrollStartTimestamp;

            // If user held and dragged the middle mouse button, stop on release; if it was a quick click, stay latched.
            if (distance > 10.0 || (elapsedMs > 260 && distance > 4.0))
            {
                StopAutoScroll();
            }

            e.Handled = true;
        }
    }

    private void StartAutoScroll(Point anchorPosition, Pointer pointer)
    {
        if (ViewModel.CurrentDocument is null || AutoScrollOverlayCanvas is null || AutoScrollAnchorBadge is null)
        {
            return;
        }

        _isAutoScrolling = true;
        _autoScrollMiddleButtonDown = true;
        _autoScrollStartTimestamp = Environment.TickCount64;
        _lastAutoScrollRenderTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastAutoScrollPageFlipTicks = _lastAutoScrollRenderTicks;
        _autoScrollAnchorPoint = anchorPosition;
        _autoScrollCurrentPoint = anchorPosition;
        _autoScrollAccumulatedX = ContinuousScrollViewer.HorizontalOffset;
        _autoScrollAccumulatedY = ContinuousScrollViewer.VerticalOffset;

        Canvas.SetLeft(AutoScrollAnchorBadge, anchorPosition.X - 14.0);
        Canvas.SetTop(AutoScrollAnchorBadge, anchorPosition.Y - 14.0);
        AutoScrollOverlayCanvas.Visibility = Visibility.Visible;

        try
        {
            ScrollViewerContentRoot.CapturePointer(pointer);
        }
        catch
        {
        }

        if (!_isAutoScrollRenderingSubscribed)
        {
            CompositionTarget.Rendering += OnAutoScrollRendering;
            _isAutoScrollRenderingSubscribed = true;
        }
    }

    private void StopAutoScroll()
    {
        if (_isAutoScrollRenderingSubscribed)
        {
            CompositionTarget.Rendering -= OnAutoScrollRendering;
            _isAutoScrollRenderingSubscribed = false;
        }

        if (!_isAutoScrolling)
        {
            return;
        }

        _isAutoScrolling = false;
        _autoScrollMiddleButtonDown = false;

        if (AutoScrollOverlayCanvas is not null)
        {
            AutoScrollOverlayCanvas.Visibility = Visibility.Collapsed;
        }

        try
        {
            ScrollViewerContentRoot?.ReleasePointerCaptures();
        }
        catch
        {
        }

        ScheduleViewportUpdate();
    }

    private void OnAutoScrollRendering(object? sender, object e)
    {
        if (!_isAutoScrolling || ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            StopAutoScroll();
            return;
        }

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = (double)(nowTicks - _lastAutoScrollRenderTicks) / System.Diagnostics.Stopwatch.Frequency;
        _lastAutoScrollRenderTicks = nowTicks;
        dt = Math.Clamp(dt, 0.001, 0.05);

        double dx = _autoScrollCurrentPoint.X - _autoScrollAnchorPoint.X;
        double dy = _autoScrollCurrentPoint.Y - _autoScrollAnchorPoint.Y;

        const double DeadZoneDip = 8.0;
        double absX = Math.Abs(dx);
        double absY = Math.Abs(dy);

        if (absX <= DeadZoneDip && absY <= DeadZoneDip)
        {
            _autoScrollAccumulatedX = ContinuousScrollViewer.HorizontalOffset;
            _autoScrollAccumulatedY = ContinuousScrollViewer.VerticalOffset;
            return;
        }

        // Smooth browser-style exponential velocity curve (DIPs/second)
        double velX = absX > DeadZoneDip
            ? Math.Sign(dx) * Math.Min(4200.0, Math.Pow(absX - DeadZoneDip, 1.35) * 4.2)
            : 0.0;
        double velY = absY > DeadZoneDip
            ? Math.Sign(dy) * Math.Min(4200.0, Math.Pow(absY - DeadZoneDip, 1.35) * 4.2)
            : 0.0;

        bool canScrollH = ContinuousScrollViewer.ScrollableWidth > 1.0;
        bool canScrollV = ContinuousScrollViewer.ScrollableHeight > 1.0 && (doc.IsContinuous || !_isFittedVertically);

        if (!doc.IsContinuous && !canScrollV && absY > 28.0)
        {
            double elapsedFlipSec = (double)(nowTicks - _lastAutoScrollPageFlipTicks) / System.Diagnostics.Stopwatch.Frequency;
            double flipInterval = Math.Clamp(0.45 - (Math.Min(220.0, absY - 28.0) * 0.0014), 0.12, 0.45);
            if (elapsedFlipSec >= flipInterval)
            {
                _lastAutoScrollPageFlipTicks = nowTicks;
                FlipPagedSlide(forward: dy > 0);
            }
            return;
        }

        double? targetH = null;
        double? targetV = null;

        if (canScrollH && Math.Abs(velX) > 0.01)
        {
            if (Math.Abs(_autoScrollAccumulatedX - ContinuousScrollViewer.HorizontalOffset) > 48.0)
            {
                _autoScrollAccumulatedX = ContinuousScrollViewer.HorizontalOffset;
            }

            _autoScrollAccumulatedX = Math.Clamp(
                _autoScrollAccumulatedX + (velX * dt),
                0.0,
                ContinuousScrollViewer.ScrollableWidth);
            targetH = _autoScrollAccumulatedX;
        }
        else
        {
            _autoScrollAccumulatedX = ContinuousScrollViewer.HorizontalOffset;
        }

        if (canScrollV && Math.Abs(velY) > 0.01)
        {
            if (ContinuousScrollViewer.VerticalScrollMode != ScrollMode.Enabled)
            {
                ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
            }

            if (Math.Abs(_autoScrollAccumulatedY - ContinuousScrollViewer.VerticalOffset) > 48.0)
            {
                _autoScrollAccumulatedY = ContinuousScrollViewer.VerticalOffset;
            }

            _autoScrollAccumulatedY = Math.Clamp(
                _autoScrollAccumulatedY + (velY * dt),
                0.0,
                ContinuousScrollViewer.ScrollableHeight);
            targetV = _autoScrollAccumulatedY;
        }
        else
        {
            _autoScrollAccumulatedY = ContinuousScrollViewer.VerticalOffset;
        }

        if (targetH.HasValue || targetV.HasValue)
        {
            ContinuousScrollViewer.ChangeView(targetH, targetV, null, disableAnimation: true);
            if (targetH.HasValue)
            {
                doc.LastHorizontalOffset = targetH.Value;
            }

            if (targetV.HasValue)
            {
                doc.LastVerticalOffset = targetV.Value;
            }

            if (doc.IsContinuous)
            {
                UpdatePageProgressFromScroll();
            }

            ScheduleViewportUpdate();
        }
    }

    // =========================================================================
    // Compact Chrome (< 750 DIPs) Status Bar "Swallowed" Animation
    // =========================================================================

    private void ApplyStatusBarCompactStateImmediate(bool compact)
    {
        _statusBarCompactStoryboard?.Stop();

        if (StatusBarMetadataClipContainer is not null &&
            StatusBarMetadataInnerPanel is not null &&
            StatusBarMetadataTransform is not null &&
            StatusBarMetadataClip is not null)
        {
            if (compact)
            {
                if (StatusBarMetadataInnerPanel.ActualWidth > 1.0)
                {
                    _statusBarMetadataExpandedWidth = StatusBarMetadataInnerPanel.ActualWidth;
                }

                StatusBarMetadataClipContainer.Width = 0.0;
                StatusBarMetadataClipContainer.Opacity = 0.0;
                StatusBarMetadataClipContainer.Visibility = Visibility.Collapsed;
                StatusBarMetadataTransform.TranslateX = -_statusBarMetadataExpandedWidth;
            }
            else
            {
                StatusBarMetadataClipContainer.Visibility = Visibility.Visible;
                StatusBarMetadataClipContainer.Width = double.NaN;
                StatusBarMetadataClipContainer.Opacity = 1.0;
                StatusBarMetadataTransform.TranslateX = 0.0;
                StatusBarMetadataClip.Rect = new Rect(0, 0, 400, 32);
            }
        }

        if (StatusBarPresentationClipContainer is not null &&
            StatusBarPresentationInnerPanel is not null &&
            StatusBarPresentationTransform is not null &&
            StatusBarPresentationClip is not null)
        {
            if (compact)
            {
                if (StatusBarPresentationInnerPanel.ActualWidth > 1.0)
                {
                    _statusBarPresentationExpandedWidth = StatusBarPresentationInnerPanel.ActualWidth;
                }

                StatusBarPresentationClipContainer.Width = 0.0;
                StatusBarPresentationClipContainer.Opacity = 0.0;
                StatusBarPresentationClipContainer.Visibility = Visibility.Collapsed;
                StatusBarPresentationTransform.TranslateX = -_statusBarPresentationExpandedWidth;
            }
            else
            {
                StatusBarPresentationClipContainer.Visibility = Visibility.Visible;
                StatusBarPresentationClipContainer.Width = double.NaN;
                StatusBarPresentationClipContainer.Opacity = 1.0;
                StatusBarPresentationTransform.TranslateX = 0.0;
                StatusBarPresentationClip.Rect = new Rect(0, 0, 200, 32);
            }
        }
    }

    private void AnimateStatusBarCompactMode(bool collapse)
    {
        if (StatusBarMetadataClipContainer is null ||
            StatusBarMetadataInnerPanel is null ||
            StatusBarMetadataTransform is null ||
            StatusBarMetadataClip is null ||
            StatusBarPresentationClipContainer is null ||
            StatusBarPresentationInnerPanel is null ||
            StatusBarPresentationTransform is null ||
            StatusBarPresentationClip is null)
        {
            return;
        }

        _statusBarCompactStoryboard?.Stop();

        if (!collapse)
        {
            StatusBarMetadataClipContainer.Visibility = Visibility.Visible;
            StatusBarPresentationClipContainer.Visibility = Visibility.Visible;
        }

        StatusBarMetadataInnerPanel.Measure(new Size(double.PositiveInfinity, 32));
        if (StatusBarMetadataInnerPanel.DesiredSize.Width > 1.0)
        {
            _statusBarMetadataExpandedWidth = StatusBarMetadataInnerPanel.DesiredSize.Width;
        }
        else if (StatusBarMetadataInnerPanel.ActualWidth > 1.0)
        {
            _statusBarMetadataExpandedWidth = StatusBarMetadataInnerPanel.ActualWidth;
        }

        StatusBarPresentationInnerPanel.Measure(new Size(double.PositiveInfinity, 32));
        if (StatusBarPresentationInnerPanel.DesiredSize.Width > 1.0)
        {
            _statusBarPresentationExpandedWidth = StatusBarPresentationInnerPanel.DesiredSize.Width;
        }
        else if (StatusBarPresentationInnerPanel.ActualWidth > 1.0)
        {
            _statusBarPresentationExpandedWidth = StatusBarPresentationInnerPanel.ActualWidth;
        }

        StatusBarMetadataClip.Rect = new Rect(0, 0, Math.Max(400.0, _statusBarMetadataExpandedWidth + 32.0), 32);
        StatusBarPresentationClip.Rect = new Rect(0, 0, Math.Max(200.0, _statusBarPresentationExpandedWidth + 32.0), 32);

        double fromMetaW = collapse
            ? (StatusBarMetadataClipContainer.ActualWidth > 0.5 ? StatusBarMetadataClipContainer.ActualWidth : _statusBarMetadataExpandedWidth)
            : (double.IsNaN(StatusBarMetadataClipContainer.Width) ? 0.0 : StatusBarMetadataClipContainer.Width);
        double toMetaW = collapse ? 0.0 : _statusBarMetadataExpandedWidth;

        double fromPresW = collapse
            ? (StatusBarPresentationClipContainer.ActualWidth > 0.5 ? StatusBarPresentationClipContainer.ActualWidth : _statusBarPresentationExpandedWidth)
            : (double.IsNaN(StatusBarPresentationClipContainer.Width) ? 0.0 : StatusBarPresentationClipContainer.Width);
        double toPresW = collapse ? 0.0 : _statusBarPresentationExpandedWidth;

        var sb = new Storyboard();
        var ease = new ExponentialEase { Exponent = 4.5, EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(220));

        var metaWidthAnim = new DoubleAnimation
        {
            From = fromMetaW,
            To = toMetaW,
            Duration = duration,
            EasingFunction = ease,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(metaWidthAnim, StatusBarMetadataClipContainer);
        Storyboard.SetTargetProperty(metaWidthAnim, "Width");
        sb.Children.Add(metaWidthAnim);

        var metaTranslateAnim = new DoubleAnimation
        {
            From = collapse ? StatusBarMetadataTransform.TranslateX : -_statusBarMetadataExpandedWidth,
            To = collapse ? -_statusBarMetadataExpandedWidth : 0.0,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(metaTranslateAnim, StatusBarMetadataTransform);
        Storyboard.SetTargetProperty(metaTranslateAnim, "TranslateX");
        sb.Children.Add(metaTranslateAnim);

        var metaOpacityAnim = new DoubleAnimation
        {
            From = collapse ? StatusBarMetadataClipContainer.Opacity : 0.0,
            To = collapse ? 0.0 : 1.0,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(metaOpacityAnim, StatusBarMetadataClipContainer);
        Storyboard.SetTargetProperty(metaOpacityAnim, "Opacity");
        sb.Children.Add(metaOpacityAnim);

        var presWidthAnim = new DoubleAnimation
        {
            From = fromPresW,
            To = toPresW,
            Duration = duration,
            EasingFunction = ease,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(presWidthAnim, StatusBarPresentationClipContainer);
        Storyboard.SetTargetProperty(presWidthAnim, "Width");
        sb.Children.Add(presWidthAnim);

        var presTranslateAnim = new DoubleAnimation
        {
            From = collapse ? StatusBarPresentationTransform.TranslateX : -_statusBarPresentationExpandedWidth,
            To = collapse ? -_statusBarPresentationExpandedWidth : 0.0,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(presTranslateAnim, StatusBarPresentationTransform);
        Storyboard.SetTargetProperty(presTranslateAnim, "TranslateX");
        sb.Children.Add(presTranslateAnim);

        var presOpacityAnim = new DoubleAnimation
        {
            From = collapse ? StatusBarPresentationClipContainer.Opacity : 0.0,
            To = collapse ? 0.0 : 1.0,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(presOpacityAnim, StatusBarPresentationClipContainer);
        Storyboard.SetTargetProperty(presOpacityAnim, "Opacity");
        sb.Children.Add(presOpacityAnim);

        sb.Completed += (_, _) =>
        {
            if (ViewModel.IsCompactChrome)
            {
                StatusBarMetadataClipContainer.Visibility = Visibility.Collapsed;
                StatusBarPresentationClipContainer.Visibility = Visibility.Collapsed;
            }
            else
            {
                StatusBarMetadataClipContainer.Width = double.NaN;
                StatusBarMetadataClipContainer.Opacity = 1.0;
                StatusBarMetadataTransform.TranslateX = 0.0;
                StatusBarPresentationClipContainer.Width = double.NaN;
                StatusBarPresentationClipContainer.Opacity = 1.0;
                StatusBarPresentationTransform.TranslateX = 0.0;
            }
        };

        _statusBarCompactStoryboard = sb;
        sb.Begin();
    }

    private void OnStatusBarZoomSliderValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingSliderFromZoom || ViewModel.CurrentDocument is null)
        {
            return;
        }

        _isFittedVertically = false;
        double newFactor = Math.Clamp(e.NewValue / 100.0, 0.25, 5.0);
        if (Math.Abs(ViewModel.CurrentDocument.ZoomFactor - newFactor) > 0.01)
        {
            ApplyZoomAroundCenter((float)newFactor);
        }
    }

    private void UpdateZoomSlider(double zoomFactor)
    {
        if (StatusBarZoomSlider is null)
        {
            return;
        }

        _isUpdatingSliderFromZoom = true;
        try
        {
            double percentage = Math.Clamp(zoomFactor * 100.0, StatusBarZoomSlider.Minimum, StatusBarZoomSlider.Maximum);
            StatusBarZoomSlider.Value = percentage;
        }
        finally
        {
            _isUpdatingSliderFromZoom = false;
        }
    }
}