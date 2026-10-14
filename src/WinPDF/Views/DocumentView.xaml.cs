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
    private readonly DispatcherTimer _presentationHideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
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
        ViewModel = App.CurrentApp.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();

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
        _presentationHideTimer.Stop();
        _presentationHideTimer.Tick -= OnPresentationHideTimerTick;

        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
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
        else if (e.PropertyName == nameof(ShellViewModel.IsPresentationMode))
        {
            if (ViewModel.IsPresentationMode)
            {
                ExitPresentationOverlay.Visibility = Visibility.Visible;
                _presentationHideTimer.Stop();
                _presentationHideTimer.Start();
                DispatcherQueue.TryEnqueue(() => ExecuteFitVertical());
            }
            else
            {
                _presentationHideTimer.Stop();
                ExitPresentationOverlay.Visibility = Visibility.Collapsed;
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

    private void OnDocumentModeChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => ExecuteFitVertical());
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

                    double activeZoom = ContinuousScrollViewer.ZoomFactor;
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

    private int CalculateCurrentPageIndexFromScroll()
    {
        if (ViewModel.CurrentDocument is null || ViewModel.CurrentDocument.Pages.Count == 0)
        {
            return 0;
        }

        if (!ViewModel.CurrentDocument.IsContinuous || ContinuousScrollViewer.ViewportHeight <= 0)
        {
            return ViewModel.CurrentDocument.CurrentPageIndex;
        }

        double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
        double centerY = (ContinuousScrollViewer.VerticalOffset + (ContinuousScrollViewer.ViewportHeight / 2.0)) / zoom;

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

    private void ScrollToPage(int pageIndex)
    {
        if (ViewModel.CurrentDocument is null || pageIndex < 0 || pageIndex >= ViewModel.CurrentDocument.Pages.Count)
        {
            return;
        }

        if (ViewModel.CurrentDocument.IsContinuous)
        {
            double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
            double targetY = 24.0;
            var pages = ViewModel.CurrentDocument.Pages;
            int step = ViewModel.CurrentDocument.IsDoublePage ? 2 : 1;
            int targetRow = pageIndex / step;
            for (int r = 0; r < targetRow && r * step < pages.Count; r++)
            {
                targetY += pages[r * step].DisplayHeightDip + 16.0;
            }

            double verticalOffset = targetY * zoom;
            ContinuousScrollViewer.ChangeView(null, verticalOffset, null, disableAnimation: true);
        }
        else
        {
            ContinuousScrollViewer.ChangeView(0, 0, null, disableAnimation: true);
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
            double oldZoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
            double viewportCenterX = (ContinuousScrollViewer.HorizontalOffset + ContinuousScrollViewer.ViewportWidth / 2.0) / oldZoom;
            double viewportCenterY = (ContinuousScrollViewer.VerticalOffset + ContinuousScrollViewer.ViewportHeight / 2.0) / oldZoom;

            double newOffsetX = Math.Max(0, viewportCenterX * newZoom - ContinuousScrollViewer.ViewportWidth / 2.0);
            double newOffsetY = Math.Max(0, viewportCenterY * newZoom - ContinuousScrollViewer.ViewportHeight / 2.0);

            ContinuousScrollViewer.ChangeView(newOffsetX, newOffsetY, newZoom, disableAnimation: true);

            ViewModel.SetZoom(newZoom);
            UpdateViewportPages(newZoom);
        }
        finally
        {
            _isApplyingZoom = false;
        }
    }

    /// <summary>
    /// Executes vertical fitting, adjusting zoom so the current page (or page pair) fits comfortably within the viewport.
    /// </summary>
    public void ExecuteFitVertical()
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        int cur = Math.Clamp(doc.CurrentPageIndex, 0, doc.Pages.Count - 1);
        var page = doc.Pages[cur];
        double availW = Math.Max(100.0, ContinuousScrollViewer.ViewportWidth - 32.0);
        double availH = Math.Max(100.0, ContinuousScrollViewer.ViewportHeight - 32.0);
        double targetW = doc.IsDoublePage ? (page.DisplayWidthDip * 2.0 + 16.0) : page.DisplayWidthDip;
        double targetH = page.DisplayHeightDip;
        double targetZoom = Math.Clamp(Math.Min(availH / targetH, availW / targetW), DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);

        if (doc.IsContinuous)
        {
            double targetY = 24.0;
            int step = doc.IsDoublePage ? 2 : 1;
            int targetRow = cur / step;
            for (int r = 0; r < targetRow && r * step < doc.Pages.Count; r++)
            {
                targetY += doc.Pages[r * step].DisplayHeightDip + 16.0;
            }

            double vOffset = Math.Max(0, (targetY * targetZoom) - Math.Max(0, (ContinuousScrollViewer.ViewportHeight - (targetH * targetZoom)) / 2.0));
            double hOffset = Math.Max(0, ((targetW * targetZoom) - ContinuousScrollViewer.ViewportWidth) / 2.0);
            ContinuousScrollViewer.ChangeView(hOffset, vOffset, (float)targetZoom, disableAnimation: true);
        }
        else
        {
            double hOffset = Math.Max(0, (ContinuousScrollViewer.ExtentWidth - ContinuousScrollViewer.ViewportWidth) / 2.0);
            double vOffset = Math.Max(0, (ContinuousScrollViewer.ExtentHeight - ContinuousScrollViewer.ViewportHeight) / 2.0);
            ContinuousScrollViewer.ChangeView(hOffset, vOffset, (float)targetZoom, disableAnimation: true);
        }

        doc.ZoomFactor = targetZoom;
        UpdateViewportPages(targetZoom);
    }

    /// <summary>
    /// Executes horizontal fitting, adjusting zoom so the current page (or page pair) fits width-wise within the viewport.
    /// </summary>
    public void ExecuteFitHorizontal()
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        int cur = Math.Clamp(doc.CurrentPageIndex, 0, doc.Pages.Count - 1);
        var page = doc.Pages[cur];
        double availW = Math.Max(100.0, ContinuousScrollViewer.ViewportWidth - 32.0);
        double targetW = doc.IsDoublePage ? (page.DisplayWidthDip * 2.0 + 16.0) : page.DisplayWidthDip;
        double targetZoom = Math.Clamp(availW / targetW, DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);

        ContinuousScrollViewer.ChangeView(null, null, (float)targetZoom, disableAnimation: true);
        doc.ZoomFactor = targetZoom;
        UpdateViewportPages(targetZoom);
    }

    private void OnScrollViewerPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is not { } doc)
        {
            return;
        }

        var props = e.GetCurrentPoint(ContinuousScrollViewer).Properties;
        bool isCtrlPressed = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0;

        if (!doc.IsContinuous && !isCtrlPressed)
        {
            int delta = props.MouseWheelDelta;
            if (delta != 0)
            {
                bool atTop = ContinuousScrollViewer.VerticalOffset <= 2.0;
                bool atBottom = ContinuousScrollViewer.VerticalOffset >= ContinuousScrollViewer.ScrollableHeight - 2.0;

                if (delta > 0 && atTop)
                {
                    e.Handled = true;
                    doc.PreviousPage();
                }
                else if (delta < 0 && atBottom)
                {
                    e.Handled = true;
                    doc.NextPage();
                }
            }
        }
    }

    private void OnPrevPageFloatingClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CurrentDocument?.PreviousPage();
    }

    private void OnNextPageFloatingClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CurrentDocument?.NextPage();
    }

    private void OnDocumentPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.IsPresentationMode)
        {
            ExitPresentationOverlay.Visibility = Visibility.Visible;
            _presentationHideTimer.Stop();
            _presentationHideTimer.Start();
        }
    }

    private void OnPresentationHideTimerTick(object? sender, object e)
    {
        _presentationHideTimer.Stop();
        if (ViewModel.IsPresentationMode)
        {
            ExitPresentationOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnEscapeAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsPresentationMode)
        {
            args.Handled = true;
            ViewModel.ExitPresentationCommand.Execute(null);
        }
    }

    private float GetCurrentZoomFactor()
    {
        return (float)(ViewModel.CurrentDocument?.ZoomFactor ?? ContinuousScrollViewer.ZoomFactor);
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
