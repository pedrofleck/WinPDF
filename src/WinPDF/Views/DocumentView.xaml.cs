using System.ComponentModel;
using System.Globalization;
using Windows.ApplicationModel.DataTransfer;
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
            UpdateAllCanvasesZoom(ViewModel.CurrentDocument.ZoomFactor);
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
            canvas.UpdateViewportZoom(targetZoom);
        }
    }

    private void OnCanvasUnloaded(PdfPageCanvas canvas)
    {
        _activeCanvases.Remove(canvas);
    }

    private void UpdateAllCanvasesZoom(double newZoom)
    {
        foreach (var canvas in _activeCanvases)
        {
            canvas.UpdateViewportZoom(newZoom);
        }

        FindAndApplyCanvases(ContinuousPagesRepeater, newZoom);
        FindAndApplyCanvases(PagedFlipView, newZoom);
    }

    private static void FindAndApplyCanvases(DependencyObject? parent, double newZoom)
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
                canvas.UpdateViewportZoom(newZoom);
            }
            else
            {
                FindAndApplyCanvases(child, newZoom);
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
        }
    }

    private void UnsubscribeFromDocument()
    {
        if (_observedDocument is not null)
        {
            _observedDocument.PropertyChanged -= OnCurrentDocumentPropertyChanged;
            _observedDocument = null;
        }
    }

    private void OnCurrentDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.CurrentPageIndex))
        {
            if (!_isUpdatingPageFromScroll && _observedDocument is not null)
            {
                ScrollToPage(_observedDocument.CurrentPageIndex);
            }
        }
        else if (e.PropertyName == nameof(DocumentViewModel.ZoomFactor))
        {
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
                UpdateAllCanvasesZoom(_observedDocument.ZoomFactor);
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

        // Update current page index from center of viewport
        UpdateCurrentPageIndexFromViewport();

        // 150 ms gesture debounce before applying quantized zoom to visible pages
        _zoomDebounceCts?.Cancel();
        _zoomDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _zoomDebounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150, cts.Token);
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

                    double newZoom = ContinuousScrollViewer.ZoomFactor;
                    if (Math.Abs(ViewModel.CurrentDocument.ZoomFactor - newZoom) > 0.01)
                    {
                        _isUpdatingZoomFromScrollViewer = true;
                        try
                        {
                            ViewModel.CurrentDocument.ZoomFactor = newZoom;
                        }
                        finally
                        {
                            _isUpdatingZoomFromScrollViewer = false;
                        }
                    }

                    UpdateAllCanvasesZoom(newZoom);
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void UpdateCurrentPageIndexFromViewport()
    {
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
            UpdateAllCanvasesZoom(newZoom);
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
}
