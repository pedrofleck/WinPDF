using System.ComponentModel;
using System.Globalization;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using WinPDF.Controls;
using WinPDF.Helpers;
using WinPDF.Models;
using WinPDF.ViewModels;

namespace WinPDF.Views;

/// <summary>
/// Page hosting the responsive document canvas, unified scroll viewer, and floating overlays.
/// </summary>
public sealed partial class DocumentView : Page
{
    private readonly HashSet<PdfPageCanvas> _activeCanvases = new();
    private readonly DispatcherTimer _presentationHideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private CancellationTokenSource? _zoomDebounceCts;
    private DocumentViewModel? _observedDocument;
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
    private UIElement? _hookedScrollTemplateRoot;
    private PointerEventHandler? _wheelHandler;
    private Storyboard? _transitionStoryboard;
    private Storyboard? _searchBarStoryboard;

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
        InitializeComponent();

        _wheelHandler = new PointerEventHandler(OnScrollViewerPointerWheelChanged);
        ScrollViewerContentRoot.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        ContinuousPagesRepeater.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        PrevPageZone.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        NextPageZone.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        ExitPresentationTopZone.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        ContinuousScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        ContinuousScrollViewer.SizeChanged += OnScrollViewerSizeChanged;

        _presentationHideTimer.Tick += OnPresentationHideTimerTick;

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
        SubscribeToDocument(ViewModel.CurrentDocument);

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

        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.SearchFocusRequested -= OnSearchFocusRequested;
        _transitionStoryboard?.Stop();
        _searchBarStoryboard?.Stop();
        UnsubscribeFromDocument();

        _zoomDebounceCts?.Cancel();
        _zoomDebounceCts?.Dispose();
        _zoomDebounceCts = null;
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
            SubscribeToDocument(ViewModel.CurrentDocument);
            if (ViewModel.CurrentDocument is not null)
            {
                ScrollToPage(ViewModel.CurrentDocument.CurrentPageIndex);
                PlayDocumentOpenTransitionAnimation();
                DispatcherQueue.TryEnqueue(ReturnFocusToViewer);
            }
            else
            {
                PlayEmptyStateTransitionAnimation();
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsSearchOpen))
        {
            if (ViewModel.IsSearchOpen)
            {
                PlaySearchBarOpenAnimation();
                FocusSearchBox();
            }
            else
            {
                ReturnFocusToViewer();
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
            _observedDocument = null;
        }
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
        _zoomDebounceCts?.Cancel();
        if (_isSwitchingMode || _isRestoringFromPresentation || _isApplyingFitZoom)
        {
            return;
        }

        if (ViewModel.CurrentDocument is { IsContinuous: true } && Math.Abs(e.NextView.ZoomFactor - ContinuousScrollViewer.ZoomFactor) < 0.001)
        {
            UpdateCurrentPageIndexFromViewport();
            ViewModel.CurrentDocument.FractionalPageIndex = CalculateFractionalPageIndex();
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

            if (!_isApplyingFitZoom && !_isApplyingZoom)
            {
                UpdateCurrentPageIndexFromViewport();
                ViewModel.CurrentDocument.FractionalPageIndex = CalculateFractionalPageIndex();
            }
        }

        ScheduleViewportUpdate();
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
            DispatcherQueue.TryEnqueue(() => ExecuteFitVertical(e.NewSize.Width, e.NewSize.Height, force: true));
        }
        else if (ViewModel?.CurrentDocument is { ZoomFactor: > 1.01 })
        {
            ScheduleViewportUpdate();
        }
    }

    private void ScheduleViewportUpdate()
    {
        _zoomDebounceCts?.Cancel();
        _zoomDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _zoomDebounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(100).ConfigureAwait(false);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (cts.IsCancellationRequested || ViewModel.CurrentDocument is null)
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
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void UpdateCurrentPageIndexFromViewport()
    {
        if (ViewModel.CurrentDocument is null || !ViewModel.CurrentDocument.IsContinuous || ContinuousScrollViewer.ViewportHeight <= 0)
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
                double fraction = (centerY - currentY) / Math.Max(1.0, pageHeight);
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
            doc.FractionalPageIndex = CalculateFractionalPageIndex(verticalOffset);
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
            double viewportCenterX = (ContinuousScrollViewer.HorizontalOffset + ContinuousScrollViewer.ViewportWidth / 2.0) / oldZoom;
            double viewportCenterY = (ContinuousScrollViewer.VerticalOffset + ContinuousScrollViewer.ViewportHeight / 2.0) / oldZoom;

            double newOffsetX = Math.Max(0, viewportCenterX * newZoom - ContinuousScrollViewer.ViewportWidth / 2.0);
            double newOffsetY = Math.Max(0, viewportCenterY * newZoom - ContinuousScrollViewer.ViewportHeight / 2.0);

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

            double vWidth = (viewportW.HasValue && viewportW.Value > 0)
                ? viewportW.Value
                : (ContinuousScrollViewer.ActualWidth > 0
                    ? ContinuousScrollViewer.ActualWidth
                    : (_lastHandledScrollViewerSize.Width > 0 ? _lastHandledScrollViewerSize.Width : ContinuousScrollViewer.ViewportWidth));

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

            double vWidth = (viewportW.HasValue && viewportW.Value > 0)
                ? viewportW.Value
                : (ContinuousScrollViewer.ActualWidth > 0
                    ? ContinuousScrollViewer.ActualWidth
                    : (_lastHandledScrollViewerSize.Width > 0 ? _lastHandledScrollViewerSize.Width : ContinuousScrollViewer.ViewportWidth));

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
        if (e.Handled || ViewModel.CurrentDocument is not { } doc)
        {
            return;
        }

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
                case Windows.System.VirtualKey.G:
                    e.Handled = true;
                    StatusBarPageBox.Focus(FocusState.Programmatic);
                    StatusBarPageBox.SelectAll();
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

    private void OnDocumentSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.CloseSearch();
            ReturnFocusToViewer();
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
        }
    }

    private void OnSearchPrevClick(object sender, RoutedEventArgs e)
    {
        DocumentSearchBox?.Focus(FocusState.Programmatic);
    }

    private void OnSearchNextClick(object sender, RoutedEventArgs e)
    {
        DocumentSearchBox?.Focus(FocusState.Programmatic);
    }

    private void OnSearchCloseClick(object sender, RoutedEventArgs e)
    {
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

    private void PlaySearchBarOpenAnimation()
    {
        if (FloatingSearchBar is null || FloatingSearchBarTransform is null)
        {
            return;
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

    private void OnPageBoxAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        StatusBarPageBox.Focus(FocusState.Programmatic);
        StatusBarPageBox.SelectAll();
    }

    private void ReturnFocusToViewer()
    {
        ContinuousScrollViewer.Focus(FocusState.Programmatic);
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
