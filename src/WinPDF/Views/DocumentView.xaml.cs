using System.ComponentModel;
using System.Globalization;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinPDF.Controls;
using WinPDF.Helpers;
using WinPDF.Models;
using WinPDF.ViewModels;

namespace WinPDF.Views;

/// <summary>
/// Page hosting the responsive document canvas, scroll viewer, flip view, and floating overlays.
/// </summary>
public sealed partial class DocumentView : Page
{
    private readonly HashSet<PdfPageCanvas> _activeCanvases = new();
    private CancellationTokenSource? _zoomDebounceCts;
    private DocumentViewModel? _observedDocument;
    private bool _isUpdatingPageFromScroll;
    private bool _isApplyingZoom;
    private bool _isUpdatingZoomFromScrollViewer;
    private bool _isUpdatingSliderFromZoom;
    private bool _isPageBoxEditing;

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

    /// <summary>
    /// Gets the shell view model.
    /// </summary>
    public ShellViewModel ViewModel { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentView"/> class.
    /// </summary>
    public DocumentView()
    {
        ViewModel = App.CurrentApp.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();

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
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SubscribeToDocument(ViewModel.CurrentDocument);

        if (ViewModel.CurrentDocument is not null)
        {
            UpdateViewportPages(ViewModel.CurrentDocument.ZoomFactor);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        UnsubscribeFromDocument();

        _zoomDebounceCts?.Cancel();
        _zoomDebounceCts?.Dispose();
        _zoomDebounceCts = null;
    }

    private void OnCanvasLoaded(PdfPageCanvas canvas)
    {
        _activeCanvases.Add(canvas);
        double targetZoom = ViewModel.CurrentDocument?.ZoomFactor
            ?? (ViewModel.ViewMode == ViewMode.Continuous ? ContinuousScrollViewer.ZoomFactor : 1.0);
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
            FindAndApplyCanvases(PagedFlipView, currentZoom);
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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentDocument))
        {
            SubscribeToDocument(ViewModel.CurrentDocument);
            if (ViewModel.CurrentDocument is not null)
            {
                ScrollToPage(ViewModel.CurrentDocument.CurrentPageIndex);
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.ViewMode))
        {
            if (ViewModel.CurrentDocument is not null)
            {
                ScrollToPage(ViewModel.CurrentDocument.CurrentPageIndex);
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
            _observedDocument = null;
        }
    }

    private void OnFitPageRequested()
    {
        OnFitPageClick(this, new RoutedEventArgs());
    }

    private void OnFitWidthRequested()
    {
        OnFitWidthClick(this, new RoutedEventArgs());
    }

    private void OnCurrentDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.CurrentPageIndex))
        {
            if (!_isUpdatingPageFromScroll && _observedDocument is not null)
            {
                ScrollToPage(_observedDocument.CurrentPageIndex);
            }

            if (!_isPageBoxEditing && StatusBarPageBox is not null && _observedDocument is not null)
            {
                StatusBarPageBox.Text = _observedDocument.CurrentPageNumber.ToString();
            }
        }
        else if (e.PropertyName == nameof(DocumentViewModel.ZoomFactor))
        {
            if (_observedDocument is not null)
            {
                UpdateZoomSlider(_observedDocument.ZoomFactor);
            }

            if (_isApplyingZoom || _isUpdatingZoomFromScrollViewer)
            {
                return;
            }

            if (_observedDocument is not null)
            {
                if (Math.Abs(ContinuousScrollViewer.ZoomFactor - _observedDocument.ZoomFactor) > 0.01)
                {
                    ApplyZoomAroundCenter((float)_observedDocument.ZoomFactor);
                }
                UpdateViewportPages(_observedDocument.ZoomFactor);
            }
        }
    }

    private void OnScrollViewerViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        _zoomDebounceCts?.Cancel();
    }

    private void OnScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate)
        {
            _zoomDebounceCts?.Cancel();
            return;
        }

        UpdateCurrentPageIndexFromViewport();
        ScheduleViewportUpdate();
    }

    private void OnPagedScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate || sender is not ScrollViewer sv)
        {
            _zoomDebounceCts?.Cancel();
            return;
        }

        if (ViewModel.CurrentDocument is not null && Math.Abs(ViewModel.CurrentDocument.ZoomFactor - sv.ZoomFactor) > 0.01)
        {
            _isUpdatingZoomFromScrollViewer = true;
            try
            {
                ViewModel.CurrentDocument.ZoomFactor = sv.ZoomFactor;
                UpdateZoomSlider(sv.ZoomFactor);
            }
            finally
            {
                _isUpdatingZoomFromScrollViewer = false;
            }
        }

        ScheduleViewportUpdate();
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
                await Task.Delay(120).ConfigureAwait(false);
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

                    double activeZoom = ViewModel.ViewMode == ViewMode.Continuous
                        ? ContinuousScrollViewer.ZoomFactor
                        : (GetActivePagedScrollViewer()?.ZoomFactor ?? ViewModel.CurrentDocument.ZoomFactor);

                    if (Math.Abs(ViewModel.CurrentDocument.ZoomFactor - activeZoom) > 0.01)
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
        if (ViewModel.ViewMode != ViewMode.Continuous || ContinuousScrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        if (ViewModel.CurrentDocument is null || ViewModel.CurrentDocument.Pages.Count == 0)
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

    private int CalculateCurrentPageIndexFromScroll()
    {
        if (ViewModel.CurrentDocument is null || ViewModel.CurrentDocument.Pages.Count == 0)
        {
            return 0;
        }

        if (ViewModel.ViewMode != ViewMode.Continuous || ContinuousScrollViewer.ViewportHeight <= 0)
        {
            return ViewModel.CurrentDocument.CurrentPageIndex;
        }

        double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
        double centerY = (ContinuousScrollViewer.VerticalOffset + (ContinuousScrollViewer.ViewportHeight / 2.0)) / zoom;

        double currentY = 24.0; // Margin.Top
        var pages = ViewModel.CurrentDocument.Pages;
        for (int i = 0; i < pages.Count; i++)
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

    private void ScrollToPage(int pageIndex)
    {
        if (ViewModel.CurrentDocument is null || pageIndex < 0 || pageIndex >= ViewModel.CurrentDocument.Pages.Count)
        {
            return;
        }

        if (ViewModel.ViewMode == ViewMode.Continuous)
        {
            double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
            double targetY = 24.0;
            var pages = ViewModel.CurrentDocument.Pages;
            for (int i = 0; i < pageIndex; i++)
            {
                targetY += pages[i].DisplayHeightDip + 16.0;
            }

            double verticalOffset = targetY * zoom;
            ContinuousScrollViewer.ChangeView(null, verticalOffset, null, disableAnimation: false);
        }
        else
        {
            if (PagedFlipView.SelectedIndex != pageIndex)
            {
                _isUpdatingPageFromScroll = true;
                try
                {
                    PagedFlipView.SelectedIndex = pageIndex;
                }
                finally
                {
                    _isUpdatingPageFromScroll = false;
                }
            }
        }
    }

    private void OnPagedFlipViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingPageFromScroll || ViewModel.CurrentDocument is null)
        {
            return;
        }

        int newIndex = PagedFlipView.SelectedIndex;
        if (newIndex >= 0 && newIndex < ViewModel.CurrentDocument.Pages.Count && ViewModel.CurrentDocument.CurrentPageIndex != newIndex)
        {
            _isUpdatingPageFromScroll = true;
            try
            {
                ViewModel.CurrentDocument.CurrentPageIndex = newIndex;
            }
            finally
            {
                _isUpdatingPageFromScroll = false;
            }

            ApplyCurrentZoomToActivePagedScrollViewer();
            UpdateViewportPages(ViewModel.CurrentDocument.ZoomFactor);
        }
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
            if (ViewModel.ViewMode == ViewMode.Continuous)
            {
                double oldZoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
                double viewportCenterY = (ContinuousScrollViewer.VerticalOffset + ContinuousScrollViewer.ViewportHeight / 2.0) / oldZoom;
                double newOffsetY = Math.Max(0, viewportCenterY * newZoom - ContinuousScrollViewer.ViewportHeight / 2.0);

                double contentWidth = 0;
                if (ContinuousPagesRepeater.ActualWidth > 0)
                {
                    contentWidth = ContinuousPagesRepeater.ActualWidth;
                }
                else if (ViewModel.CurrentDocument is { Pages.Count: > 0 } doc)
                {
                    contentWidth = doc.Pages[0].DisplayWidthDip + 48.0;
                }

                double renderedWidth = contentWidth * newZoom;
                double newOffsetX = 0;
                if (renderedWidth > ContinuousScrollViewer.ViewportWidth)
                {
                    newOffsetX = (renderedWidth - ContinuousScrollViewer.ViewportWidth) / 2.0;
                }

                ContinuousScrollViewer.ChangeView(newOffsetX, newOffsetY, newZoom, disableAnimation: true);
            }
            else
            {
                var innerScrollViewer = GetActivePagedScrollViewer();
                if (innerScrollViewer is not null)
                {
                    innerScrollViewer.ChangeView(null, null, newZoom, disableAnimation: true);
                }
            }

            ViewModel.SetZoom(newZoom);
            UpdateViewportPages(newZoom);
        }
        finally
        {
            _isApplyingZoom = false;
        }
    }

    private ScrollViewer? GetActivePagedScrollViewer()
    {
        if (PagedFlipView.SelectedIndex < 0)
        {
            return null;
        }

        var container = PagedFlipView.ContainerFromIndex(PagedFlipView.SelectedIndex);
        if (container is null)
        {
            return null;
        }

        return FindDescendant<ScrollViewer>(container);
    }

    private void ApplyCurrentZoomToActivePagedScrollViewer()
    {
        if (ViewModel.CurrentDocument is null)
        {
            return;
        }

        var sv = GetActivePagedScrollViewer();
        if (sv is not null)
        {
            float targetZoom = (float)ViewModel.CurrentDocument.ZoomFactor;
            if (targetZoom > 0.1f && Math.Abs(sv.ZoomFactor - targetZoom) > 0.01f)
            {
                sv.ChangeView(null, null, targetZoom, disableAnimation: true);
            }
        }
    }

    private void OnPagedScrollViewerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer sv && ViewModel.CurrentDocument is not null)
        {
            float targetZoom = (float)ViewModel.CurrentDocument.ZoomFactor;
            if (targetZoom > 0.1f && Math.Abs(sv.ZoomFactor - targetZoom) > 0.01f)
            {
                sv.ChangeView(null, null, targetZoom, disableAnimation: true);
            }
        }
    }

    private void OnPagedFlipViewPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        HandlePagedPointerWheel(e);
    }

    private void OnPagedScrollViewerPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        HandlePagedPointerWheel(e);
    }

    private void HandlePagedPointerWheel(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (ViewModel.ViewMode == ViewMode.Continuous || ViewModel.CurrentDocument is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(PagedFlipView);
        if ((e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0)
        {
            int delta = point.Properties.MouseWheelDelta;
            if (delta > 0)
            {
                OnZoomInClick(this, new RoutedEventArgs());
            }
            else if (delta < 0)
            {
                OnZoomOutClick(this, new RoutedEventArgs());
            }

            e.Handled = true;
            return;
        }

        int mouseDelta = point.Properties.MouseWheelDelta;
        if (mouseDelta < 0)
        {
            if (PagedFlipView.SelectedIndex < ViewModel.CurrentDocument.Pages.Count - 1)
            {
                PagedFlipView.SelectedIndex++;
                e.Handled = true;
            }
        }
        else if (mouseDelta > 0)
        {
            if (PagedFlipView.SelectedIndex > 0)
            {
                PagedFlipView.SelectedIndex--;
                e.Handled = true;
            }
        }
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            T? descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private float GetCurrentZoomFactor()
    {
        if (ViewModel.ViewMode == ViewMode.Continuous)
        {
            return (float)ContinuousScrollViewer.ZoomFactor;
        }

        var inner = GetActivePagedScrollViewer();
        if (inner is not null)
        {
            return (float)inner.ZoomFactor;
        }

        return (float)(ViewModel.CurrentDocument?.ZoomFactor ?? 1.0);
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        float currentZoom = GetCurrentZoomFactor();
        float newZoom = Math.Clamp(currentZoom * 1.25f, 0.25f, 8.0f);
        ApplyZoomAroundCenter(newZoom);
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        float currentZoom = GetCurrentZoomFactor();
        float newZoom = Math.Clamp(currentZoom / 1.25f, 0.25f, 8.0f);
        ApplyZoomAroundCenter(newZoom);
    }

    private void OnZoomPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string tagStr &&
            double.TryParse(tagStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoomFactor))
        {
            ApplyZoomAroundCenter((float)zoomFactor);
        }
    }

    private void OnFitWidthClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is null || ViewModel.CurrentDocument.Pages.Count == 0)
        {
            return;
        }

        int pageIdx = ViewModel.CurrentDocument.CurrentPageIndex;
        var page = ViewModel.CurrentDocument.Pages[pageIdx];
        double viewportW = ViewModel.ViewMode == ViewMode.Continuous
            ? ContinuousScrollViewer.ViewportWidth
            : PagedFlipView.ActualWidth;
        double availableWidth = viewportW - 48; // accounting for 24 DIP margins on each side
        if (availableWidth > 100 && page.DisplayWidthDip > 0)
        {
            float fitZoom = (float)Math.Clamp(availableWidth / page.DisplayWidthDip, 0.25, 8.0);
            ApplyZoomAroundCenter(fitZoom);
        }
    }

    private void OnFitPageClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is null || ViewModel.CurrentDocument.Pages.Count == 0)
        {
            return;
        }

        int pageIdx = ViewModel.CurrentDocument.CurrentPageIndex;
        var page = ViewModel.CurrentDocument.Pages[pageIdx];
        double viewportW = ViewModel.ViewMode == ViewMode.Continuous
            ? ContinuousScrollViewer.ViewportWidth
            : PagedFlipView.ActualWidth;
        double viewportH = ViewModel.ViewMode == ViewMode.Continuous
            ? ContinuousScrollViewer.ViewportHeight
            : PagedFlipView.ActualHeight;

        double availableWidth = viewportW - 48;
        double availableHeight = viewportH - 48;
        if (availableWidth > 100 && availableHeight > 100 && page.DisplayWidthDip > 0 && page.DisplayHeightDip > 0)
        {
            float zoomW = (float)(availableWidth / page.DisplayWidthDip);
            float zoomH = (float)(availableHeight / page.DisplayHeightDip);
            float fitZoom = (float)Math.Clamp(Math.Min(zoomW, zoomH), 0.25, 8.0);
            ApplyZoomAroundCenter(fitZoom);
        }
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

    private void OnStatusBarPageBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
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

    private void OnPageBoxAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        StatusBarPageBox.Focus(FocusState.Programmatic);
        StatusBarPageBox.SelectAll();
    }

    private void ReturnFocusToViewer()
    {
        if (ViewModel.ViewMode == ViewMode.Continuous)
        {
            ContinuousScrollViewer.Focus(FocusState.Programmatic);
        }
        else
        {
            PagedFlipView.Focus(FocusState.Programmatic);
        }
    }

    private void OnStatusBarZoomSliderValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingSliderFromZoom || ViewModel.CurrentDocument is null)
        {
            return;
        }

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
