using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using WinPDF.Models;
using WinPDF.Rendering;
using WinPDF.Services;
using WinPDF.ViewModels;

namespace WinPDF.Controls;

/// <summary>
/// Native Win2D canvas rendering a single PDF page with Viewbox-insulated base canvas, high-DPI viewport detail patch,
/// zero-LOH uploads, fallback background blitting during zoom/scroll, and true 0.0% CPU/GPU consumption when stationary.
/// </summary>
public sealed partial class PdfPageCanvas : Grid
{
    private const double DetailZoomThreshold = 1.01;
    private const double MaxDetailPatchDimensionPx = 4096.0;

    [ComImport]
    [Guid("905a0fef-bc53-11df-8c49-001e4fc686da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICanvasBufferByteAccess
    {
        unsafe byte* Buffer { get; }
    }

    /// <summary>
    /// Identifies the <see cref="PageInfo"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty PageInfoProperty =
        DependencyProperty.Register(
            nameof(PageInfo),
            typeof(PdfPageInfo),
            typeof(PdfPageCanvas),
            new PropertyMetadata(null, OnPageInfoPropertyChanged));

    /// <summary>
    /// Identifies the <see cref="DocumentId"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty DocumentIdProperty =
        DependencyProperty.Register(
            nameof(DocumentId),
            typeof(long),
            typeof(PdfPageCanvas),
            new PropertyMetadata(0L, OnDocumentIdPropertyChanged));

    /// <summary>
    /// Identifies the <see cref="ZoomFactor"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty ZoomFactorProperty =
        DependencyProperty.Register(
            nameof(ZoomFactor),
            typeof(double),
            typeof(PdfPageCanvas),
            new PropertyMetadata(1.0, OnZoomFactorPropertyChanged));

    /// <summary>
    /// Identifies the <see cref="IsNightMode"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsNightModeProperty =
        DependencyProperty.Register(
            nameof(IsNightMode),
            typeof(bool),
            typeof(PdfPageCanvas),
            new PropertyMetadata(false, OnIsNightModePropertyChanged));

    /// <summary>
    /// Identifies the <see cref="RotationAngle"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty RotationAngleProperty =
        DependencyProperty.Register(
            nameof(RotationAngle),
            typeof(int),
            typeof(PdfPageCanvas),
            new PropertyMetadata(0, OnRotationAnglePropertyChanged));

    private readonly CanvasControl _baseCanvas;
    private readonly Viewbox _baseViewbox;
    private readonly CanvasControl _detailCanvas;
    private readonly TranslateTransform _detailTranslate;
    private readonly IPdfService _pdfService;
    private readonly TileCache _tileCache;

    private CanvasBitmapEntry? _level0Entry;
    private CanvasBitmap? _level0Bitmap;
    private CanvasBitmap? _detailBitmap;
    private Rect _detailRectDip = Rect.Empty;
    private double _renderedZoom = 1.0;
    private double _renderedPixelScale = 1.0;

    private CancellationTokenSource? _baseCts;
    private CancellationTokenSource? _detailCts;
    private DocumentViewModel? _observedDocument;
    private ScrollViewer? _cachedScrollViewer;
    private int _loadedPageIndex = -1;
    private volatile bool _isUnloaded;
    private bool _isResourcesCreated;
    private bool _needsLevel0Render;

    /// <summary>
    /// Gets or sets the metadata describing the page to render.
    /// </summary>
    public PdfPageInfo? PageInfo
    {
        get => (PdfPageInfo?)GetValue(PageInfoProperty);
        set => SetValue(PageInfoProperty, value);
    }

    /// <summary>
    /// Gets or sets the engine-assigned document identifier.
    /// </summary>
    public long DocumentId
    {
        get => (long)GetValue(DocumentIdProperty);
        set => SetValue(DocumentIdProperty, value);
    }

    /// <summary>
    /// Event raised when a <see cref="PdfPageCanvas"/> is loaded into the visual tree.
    /// </summary>
    public static event Action<PdfPageCanvas>? CanvasLoaded;

    /// <summary>
    /// Event raised when a <see cref="PdfPageCanvas"/> is unloaded from the visual tree.
    /// </summary>
    public static event Action<PdfPageCanvas>? CanvasUnloaded;

    /// <summary>
    /// Gets or sets the current zoom factor applied to the page.
    /// </summary>
    public double ZoomFactor
    {
        get => (double)GetValue(ZoomFactorProperty);
        set => SetValue(ZoomFactorProperty, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether night/dark reading mode is enabled.
    /// </summary>
    public bool IsNightMode
    {
        get => (bool)GetValue(IsNightModeProperty);
        set => SetValue(IsNightModeProperty, value);
    }

    /// <summary>
    /// Gets or sets the view rotation angle in degrees (0, 90, 180, 270).
    /// </summary>
    public int RotationAngle
    {
        get => (int)GetValue(RotationAngleProperty);
        set => SetValue(RotationAngleProperty, value);
    }

    /// <summary>
    /// Gets the effective document id, falling back to the shell's active document if not explicitly set.
    /// </summary>
    private long EffectiveDocumentId
    {
        get
        {
            if (DocumentId > 0)
            {
                return DocumentId;
            }

            if (PageInfo is { DocumentId: > 0 } pageInfo)
            {
                return pageInfo.DocumentId;
            }

            try
            {
                return App.CurrentApp.Services.GetService<ShellViewModel>()?.CurrentDocument?.DocumentId ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Gets the effective zoom factor, looking at parent ScrollViewer, explicit ZoomFactor, or active document ZoomFactor.
    /// </summary>
    private double EffectiveZoom
    {
        get
        {
            var scrollViewer = _cachedScrollViewer ??= FindAncestor<ScrollViewer>(this);
            if (scrollViewer is not null && scrollViewer.ZoomFactor > 0.05 && Math.Abs(scrollViewer.ZoomFactor - 1.0) > 0.001)
            {
                return scrollViewer.ZoomFactor;
            }

            if (ZoomFactor > 0.05 && Math.Abs(ZoomFactor - 1.0) > 0.001)
            {
                return ZoomFactor;
            }

            if (_observedDocument is not null && _observedDocument.ZoomFactor > 0.05)
            {
                return _observedDocument.ZoomFactor;
            }

            if (scrollViewer is not null && scrollViewer.ZoomFactor > 0.05)
            {
                return scrollViewer.ZoomFactor;
            }

            try
            {
                return App.CurrentApp.Services.GetService<ShellViewModel>()?.CurrentDocument?.ZoomFactor ?? 1.0;
            }
            catch
            {
                return 1.0;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PdfPageCanvas"/> class.
    /// </summary>
    public PdfPageCanvas()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;

        _pdfService = App.CurrentApp.Services.GetRequiredService<IPdfService>();
        _tileCache = App.CurrentApp.Services.GetRequiredService<TileCache>();

        // Level 0 base canvas keeps DpiScale fixed at 1.0f forever to bound full-page VRAM.
        _baseCanvas = new CanvasControl
        {
            UseSharedDevice = true,
            ClearColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            DpiScale = 1.0f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _baseCanvas.CreateResources += OnCreateResources;
        _baseCanvas.Draw += OnBaseCanvasDraw;

        _baseViewbox = new Viewbox
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = _baseCanvas,
        };

        _detailTranslate = new TranslateTransform();
        _detailCanvas = new CanvasControl
        {
            UseSharedDevice = true,
            ClearColor = Windows.UI.Color.FromArgb(0, 0, 0, 0),
            DpiScale = 1.0f,
            Width = 1.0,
            Height = 1.0,
            Opacity = 0.0,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            RenderTransform = _detailTranslate,
        };

        _detailCanvas.CreateResources += OnDetailCreateResources;
        _detailCanvas.SizeChanged += OnDetailCanvasSizeChanged;
        _detailCanvas.Draw += OnDetailCanvasDraw;

        Children.Add(_baseViewbox);
        Children.Add(_detailCanvas);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsNaN(availableSize.Width) || double.IsNaN(availableSize.Height) ||
            availableSize.Width <= 0 || availableSize.Height <= 0)
        {
            return new Size(0, 0);
        }

        return base.MeasureOverride(availableSize);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = false;
        if (PageInfo is null)
        {
            return;
        }

        UpdateDimensions();
        SubscribeToActiveDocument();
        if (_observedDocument is not null)
        {
            ZoomFactor = _observedDocument.ZoomFactor;
            IsNightMode = _observedDocument.IsNightMode;
            RotationAngle = _observedDocument.Rotation;
        }
        else
        {
            var scrollViewer = _cachedScrollViewer ??= FindAncestor<ScrollViewer>(this);
            if (scrollViewer is not null && scrollViewer.ZoomFactor > 0.05)
            {
                ZoomFactor = scrollViewer.ZoomFactor;
            }
        }

        TryLoadCachedBitmaps();
        UpdateRender();
        CanvasLoaded?.Invoke(this);

        var sv = FindAncestor<ScrollViewer>(this);
        double activeZoom = sv is not null && sv.ZoomFactor > 0.05 ? sv.ZoomFactor : EffectiveZoom;
        if (activeZoom > DetailZoomThreshold)
        {
            UpdateViewport(activeZoom);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        _cachedScrollViewer = null;
        CanvasUnloaded?.Invoke(this);
        CancelInFlightRenders();
        ReleaseHighResPatch();
        UnsubscribeFromDocument();
        ReleaseBitmaps();
    }

    private void SubscribeToActiveDocument()
    {
        try
        {
            var doc = App.CurrentApp.Services.GetService<ShellViewModel>()?.CurrentDocument;
            if (!ReferenceEquals(_observedDocument, doc))
            {
                UnsubscribeFromDocument();
                if (doc is not null)
                {
                    _observedDocument = doc;
                    _observedDocument.PropertyChanged += OnObservedDocumentPropertyChanged;
                }
            }
        }
        catch
        {
        }
    }

    private void UnsubscribeFromDocument()
    {
        if (_observedDocument is { } doc)
        {
            doc.PropertyChanged -= OnObservedDocumentPropertyChanged;
            _observedDocument = null;
        }
    }

    private void OnObservedDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.ZoomFactor))
        {
            if (_observedDocument is not null)
            {
                ZoomFactor = _observedDocument.ZoomFactor;
            }
        }
        else if (e.PropertyName == nameof(DocumentViewModel.IsNightMode))
        {
            if (_observedDocument is not null)
            {
                IsNightMode = _observedDocument.IsNightMode;
            }
        }
        else if (e.PropertyName == nameof(DocumentViewModel.Rotation))
        {
            if (_observedDocument is not null)
            {
                RotationAngle = _observedDocument.Rotation;
            }
        }
    }

    private static void OnIsNightModePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageCanvas canvas)
        {
            canvas.RefreshNightModeVisuals();
        }
    }

    /// <summary>
    /// Updates the night mode state and immediately invalidates both base and detail Win2D surfaces.
    /// </summary>
    /// <param name="isNightMode">Whether night mode is enabled.</param>
    public void ApplyNightModeImmediate(bool isNightMode)
    {
        if (IsNightMode != isNightMode)
        {
            IsNightMode = isNightMode;
        }

        RefreshNightModeVisuals();
    }

    private void RefreshNightModeVisuals()
    {
        _baseCanvas.ClearColor = IsNightMode
            ? Windows.UI.Color.FromArgb(255, 18, 18, 18)
            : Windows.UI.Color.FromArgb(255, 255, 255, 255);
        _baseCanvas.Invalidate();
        _detailCanvas.Invalidate();
    }

    private static void OnRotationAnglePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageCanvas canvas)
        {
            canvas.ReleaseHighResPatch();
            canvas.UpdateDimensions();
            canvas._baseCanvas.Invalidate();
            if (canvas.EffectiveZoom > DetailZoomThreshold)
            {
                canvas.UpdateViewport(canvas.EffectiveZoom);
            }
        }
    }

    private static void OnPageInfoPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageCanvas canvas)
        {
            canvas.CancelInFlightRenders();
            canvas.ReleaseHighResPatch();
            canvas.ReleaseBitmaps();
            canvas._loadedPageIndex = canvas.PageInfo?.Index ?? -1;
            canvas.UpdateDimensions();
            canvas.SubscribeToActiveDocument();
            if (canvas._observedDocument is not null)
            {
                canvas.IsNightMode = canvas._observedDocument.IsNightMode;
                canvas.RotationAngle = canvas._observedDocument.Rotation;
            }

            if (canvas.PageInfo is not null)
            {
                canvas.TryLoadCachedBitmaps();
                canvas.RefreshNightModeVisuals();
                canvas.UpdateRender();
            }
        }
    }

    private static void OnDocumentIdPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageCanvas canvas)
        {
            canvas.CancelInFlightRenders();
            canvas.ReleaseHighResPatch();
            canvas.ReleaseBitmaps();
            if (canvas.PageInfo is not null)
            {
                canvas.TryLoadCachedBitmaps();
                canvas._baseCanvas.Invalidate();
                canvas.UpdateRender();
            }
        }
    }

    private static void OnZoomFactorPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageCanvas canvas && e.NewValue is double newZoom && newZoom > 0.05)
        {
            canvas.UpdateViewport(newZoom);
        }
    }

    private void UpdateDimensions()
    {
        if (PageInfo is not null)
        {
            bool swap = (RotationAngle % 180 != 0);
            double effW = Math.Max(1.0, swap ? PageInfo.DisplayHeightDip : PageInfo.DisplayWidthDip);
            double effH = Math.Max(1.0, swap ? PageInfo.DisplayWidthDip : PageInfo.DisplayHeightDip);

            Width = effW;
            Height = effH;
            _baseCanvas.Width = effW;
            _baseCanvas.Height = effH;
            Visibility = Visibility.Visible;
        }
        else
        {
            Visibility = Visibility.Collapsed;
        }
    }

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _isResourcesCreated = true;
        if (args.Reason == CanvasCreateResourcesReason.NewDevice)
        {
            _tileCache.HandleDeviceLost();
            _level0Bitmap?.Dispose();
            _level0Bitmap = null;
            ReleaseHighResPatch();
        }

        if (_level0Bitmap is null)
        {
            TryLoadCachedBitmaps();
        }

        if (_level0Bitmap is not null)
        {
            _baseCanvas.Invalidate();
        }
        else if (_needsLevel0Render || PageInfo is not null)
        {
            _needsLevel0Render = false;
            UpdateRender();
        }
    }

    private void OnDetailCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        if (_detailBitmap is not null && _detailCanvas.Visibility == Visibility.Visible)
        {
            _detailCanvas.Invalidate();
        }
    }

    private void OnDetailCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_detailBitmap is not null && _detailCanvas.Visibility == Visibility.Visible && e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            _detailCanvas.Invalidate();
        }
    }

    private void TryLoadCachedBitmaps()
    {
        if (PageInfo is null || EffectiveDocumentId <= 0)
        {
            return;
        }

        double rasterizationScale = XamlRoot?.RasterizationScale ?? 1.0;
        int scalePercent = (int)Math.Round(rasterizationScale * 100);

        TileKey baseKey = new(
            EffectiveDocumentId,
            PageInfo.Index,
            TileKind.FullPage,
            ZoomStep: 0,
            TileX: 0,
            TileY: 0,
            RenderFlags: 0,
            Rotation: 0,
            ScalePercent: scalePercent);

        if (_tileCache.TryGet(in baseKey, out TileCacheEntry? cached))
        {
            if (cached is CanvasBitmapEntry { IsDisposed: false } canvasEntry)
            {
                try
                {
                    if (canvasEntry.Bitmap.Device is not null)
                    {
                        _level0Entry?.Release();
                        _level0Entry = canvasEntry;
                        _level0Bitmap = canvasEntry.Bitmap;
                        return;
                    }
                }
                catch
                {
                    _tileCache.RemovePage(EffectiveDocumentId, PageInfo.Index);
                    _level0Bitmap = null;
                }
            }

            cached?.Release();
        }
    }

    private void OnBaseCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (PageInfo is null)
        {
            return;
        }

        if (_loadedPageIndex != PageInfo.Index)
        {
            CancelInFlightRenders();
            ReleaseHighResPatch();
            ReleaseBitmaps();
            _loadedPageIndex = PageInfo.Index;
            UpdateRender();
            return;
        }

        var ds = args.DrawingSession;
        double w = sender.ActualWidth > 0 ? sender.ActualWidth : sender.Width;
        double h = sender.ActualHeight > 0 ? sender.ActualHeight : sender.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        Rect pageRect = new(0, 0, w, h);

        // 1. Base page background: white in normal mode, dark #121212 in night mode.
        ds.FillRectangle(pageRect, IsNightMode
            ? Windows.UI.Color.FromArgb(255, 18, 18, 18)
            : Windows.UI.Color.FromArgb(255, 255, 255, 255));

        // 2. Centered rotation via Matrix3x2.
        var oldTransform = ds.Transform;
        try
        {
            if (RotationAngle != 0)
            {
                float angleRad = (float)(RotationAngle * Math.PI / 180.0);
                Vector2 center = new((float)w / 2f, (float)h / 2f);
                ds.Transform = Matrix3x2.CreateRotation(angleRad, center) * oldTransform;
            }

            // 3. Draw Level 0 base bitmap.
            if (_level0Bitmap is { } baseBmp)
            {
                try
                {
                    bool swap = (RotationAngle % 180 != 0);
                    double origW = swap ? h : w;
                    double origH = swap ? w : h;
                    Rect baseDest = new((w - origW) / 2.0, (h - origH) / 2.0, origW, origH);
                    DrawBitmapWithNightMode(ds, baseBmp, baseDest, baseBmp.Bounds);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or COMException)
                {
                    _level0Bitmap = null;
                    _tileCache.RemovePage(EffectiveDocumentId, PageInfo.Index);
                    _needsLevel0Render = true;
                    DispatcherQueue.TryEnqueue(() => UpdateRender());
                }
            }
        }
        finally
        {
            ds.Transform = oldTransform;
        }
    }

    private void OnDetailCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_detailBitmap is not { } detailBmp || _detailRectDip.IsEmpty || _detailRectDip.Width <= 0 || _detailRectDip.Height <= 0)
        {
            return;
        }

        var ds = args.DrawingSession;
        double w = sender.ActualWidth > 0 ? sender.ActualWidth : sender.Width;
        double h = sender.ActualHeight > 0 ? sender.ActualHeight : sender.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        Rect destRect = new(0, 0, w, h);
        try
        {
            DrawBitmapWithNightMode(ds, detailBmp, destRect, detailBmp.Bounds);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or COMException)
        {
            _detailBitmap = null;
        }
    }

    private void DrawBitmapWithNightMode(
        CanvasDrawingSession ds,
        CanvasBitmap bitmap,
        Rect destRect,
        Rect srcRect)
    {
        try
        {
            if (IsNightMode)
            {
                using var invert = new InvertEffect { Source = bitmap };
                ds.DrawImage(invert, destRect, invert.GetBounds(ds));
            }
            else
            {
                ds.DrawImage(bitmap, destRect, srcRect);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or COMException)
        {
        }
    }

    /// <summary>
    /// Releases the high-resolution patch and resets the detail canvas surface to minimum footprint.
    /// </summary>
    public void ReleaseHighResPatch()
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;

        if (_detailBitmap is not null || _detailCanvas.Visibility == Visibility.Visible)
        {
            CanvasBitmap? old = _detailBitmap;
            _detailBitmap = null;
            _detailRectDip = Rect.Empty;
            _renderedZoom = 1.0;
            _renderedPixelScale = 1.0;
            _detailCanvas.Opacity = 0.0;
            _detailCanvas.Visibility = Visibility.Collapsed;
            _detailCanvas.Width = 1.0;
            _detailCanvas.Height = 1.0;
            if (Math.Abs(_detailCanvas.DpiScale - 1.0f) > 0.001f)
            {
                _detailCanvas.DpiScale = 1.0f;
            }

            old?.Dispose();
        }
    }

    /// <summary>
    /// Updates the viewport zoom factor for this page canvas and triggers detail rendering if zoomed and visible.
    /// </summary>
    /// <param name="newZoom">The new zoom factor.</param>
    public void UpdateViewportZoom(double newZoom) => UpdateViewport(newZoom);

    /// <summary>
    /// Updates the viewport and triggers detail rendering if zoomed and visible.
    /// Level 0 bitmap is always retained while loaded.
    /// </summary>
    /// <param name="currentZoom">The current zoom factor.</param>
    public void UpdateViewport(double currentZoom)
    {
        if (PageInfo is null || EffectiveDocumentId <= 0 || _isUnloaded)
        {
            return;
        }

        if (Math.Abs(ZoomFactor - currentZoom) > 0.0005)
        {
            SetValue(ZoomFactorProperty, currentZoom);
        }

        // Ensure Level 0 base bitmap is loaded or requested.
        if (_level0Bitmap is null)
        {
            TryLoadCachedBitmaps();
        }

        if (_level0Bitmap is null && !_isUnloaded && _isResourcesCreated)
        {
            _baseCts?.Cancel();
            _baseCts?.Dispose();
            _baseCts = new CancellationTokenSource();
            _ = RenderBaseBitmapAsync(_baseCts.Token);
        }

        Rect visibleDip = GetVisibleRectInPageDips(currentZoom);

        // Case 1 — Page off-screen or unzoomed: release high-res patch immediately.
        if (currentZoom <= DetailZoomThreshold || visibleDip.IsEmpty || visibleDip.Width <= 0 || visibleDip.Height <= 0)
        {
            ReleaseHighResPatch();
            return;
        }

        bool swap = (RotationAngle % 180 != 0);
        double pageW = Math.Max(1.0, swap ? PageInfo.DisplayHeightDip : PageInfo.DisplayWidthDip);
        double pageH = Math.Max(1.0, swap ? PageInfo.DisplayWidthDip : PageInfo.DisplayHeightDip);
        double rasterScale = XamlRoot?.RasterizationScale ?? 1.0;

        (Rect candidatePatchDip, double targetPixelScale) = ComputePatchAndPixelScale(visibleDip, pageW, pageH, currentZoom, rasterScale);

        // Case 2 — Page visible on screen and zoomed: check if current high-res patch already covers viewport at target resolution.
        bool sameZoom = Math.Abs(_renderedZoom - currentZoom) < 0.02;
        bool sameScale = Math.Abs(_renderedPixelScale - targetPixelScale) < 0.02;
        bool coversViewport = _detailBitmap is not null
            && !_detailRectDip.IsEmpty
            && _detailCanvas.Visibility == Visibility.Visible
            && _detailRectDip.X <= visibleDip.X + 0.5
            && _detailRectDip.Y <= visibleDip.Y + 0.5
            && (_detailRectDip.X + _detailRectDip.Width) >= (visibleDip.X + visibleDip.Width - 0.5)
            && (_detailRectDip.Y + _detailRectDip.Height) >= (visibleDip.Y + visibleDip.Height - 0.5);

        if (sameZoom && sameScale && coversViewport)
        {
            return;
        }

        _ = RenderDetailPatchAsync(candidatePatchDip, targetPixelScale, currentZoom, rasterScale);
    }

    private static (Rect PatchDip, double PixelScale) ComputePatchAndPixelScale(
        Rect visibleDip,
        double pageW,
        double pageH,
        double currentZoom,
        double rasterScale)
    {
        double desiredPixelScale = Math.Max(1.0, currentZoom * rasterScale);

        // Bound the patch in DIPs so that the 20% prefetch margin does not force pixelScale below native resolution on 4K displays.
        double maxAllowedPatchW = Math.Max(visibleDip.Width, MaxDetailPatchDimensionPx / desiredPixelScale);
        double maxAllowedPatchH = Math.Max(visibleDip.Height, MaxDetailPatchDimensionPx / desiredPixelScale);

        double marginX = Math.Min(visibleDip.Width * 0.20, Math.Max(0.0, (maxAllowedPatchW - visibleDip.Width) / 2.0));
        double marginY = Math.Min(visibleDip.Height * 0.20, Math.Max(0.0, (maxAllowedPatchH - visibleDip.Height) / 2.0));

        double patchX = Math.Max(0, visibleDip.X - marginX);
        double patchY = Math.Max(0, visibleDip.Y - marginY);
        double patchR = Math.Min(pageW, visibleDip.X + visibleDip.Width + marginX);
        double patchB = Math.Min(pageH, visibleDip.Y + visibleDip.Height + marginY);
        Rect patchDip = new(patchX, patchY, Math.Max(1.0, patchR - patchX), Math.Max(1.0, patchB - patchY));

        double maxScaleW = MaxDetailPatchDimensionPx / Math.Max(1.0, patchDip.Width);
        double maxScaleH = MaxDetailPatchDimensionPx / Math.Max(1.0, patchDip.Height);
        double pixelScale = Math.Min(desiredPixelScale, Math.Min(maxScaleW, maxScaleH));

        return (patchDip, pixelScale);
    }

    private void UpdateRender()
    {
        if (PageInfo is null || EffectiveDocumentId <= 0 || _isUnloaded)
        {
            return;
        }

        if (!_isResourcesCreated)
        {
            _needsLevel0Render = true;
            return;
        }

        if (_level0Bitmap is null)
        {
            TryLoadCachedBitmaps();
        }

        if (_level0Bitmap is null)
        {
            _baseCts?.Cancel();
            _baseCts?.Dispose();
            _baseCts = new CancellationTokenSource();
            _ = RenderBaseBitmapAsync(_baseCts.Token);
        }

        UpdateViewport(EffectiveZoom);
    }

    private async Task RenderBaseBitmapAsync(CancellationToken ct)
    {
        if (PageInfo is not { } pageInfo || EffectiveDocumentId <= 0)
        {
            return;
        }

        int targetPageIndex = pageInfo.Index;
        long targetDocId = EffectiveDocumentId;

        double rasterizationScale = XamlRoot?.RasterizationScale ?? 1.0;
        int scalePercent = (int)Math.Round(rasterizationScale * 100);

        TileKey baseKey = new(
            targetDocId,
            targetPageIndex,
            TileKind.FullPage,
            ZoomStep: 0,
            TileX: 0,
            TileY: 0,
            RenderFlags: 0,
            Rotation: 0,
            ScalePercent: scalePercent);

        if (_tileCache.TryGet(in baseKey, out TileCacheEntry? cached))
        {
            if (cached is CanvasBitmapEntry { IsDisposed: false } canvasEntry)
            {
                bool isValid = false;
                try
                {
                    isValid = canvasEntry.Bitmap.Device is not null;
                }
                catch
                {
                    _tileCache.RemovePage(targetDocId, targetPageIndex);
                }

                if (isValid && PageInfo?.Index == targetPageIndex && EffectiveDocumentId == targetDocId && !_isUnloaded)
                {
                    _level0Entry?.Release();
                    _level0Entry = canvasEntry;
                    _level0Bitmap = canvasEntry.Bitmap;
                    _baseCanvas.Invalidate();

                    if (EffectiveZoom > DetailZoomThreshold)
                    {
                        UpdateViewport(EffectiveZoom);
                    }

                    return;
                }
            }

            cached?.Release();
        }

        (int pixelWidth, int pixelHeight) = RenderGeometry.ComputeFullPagePixelSize(pageInfo, 1.0, rasterizationScale, 0);

        PdfBitmapLease lease;
        try
        {
            lease = await _pdfService.RenderPageAsync(
                targetDocId,
                targetPageIndex,
                pixelWidth,
                pixelHeight,
                highPriority: true,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Base bitmap render failed: {ex.Message}");
            return;
        }

        if (ct.IsCancellationRequested || _isUnloaded)
        {
            lease.Dispose();
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (ct.IsCancellationRequested || _isUnloaded || !IsLoaded || PageInfo?.Index != targetPageIndex || EffectiveDocumentId != targetDocId)
                {
                    lease.Dispose();
                    return;
                }

                if (_baseCanvas.Device is null)
                {
                    _needsLevel0Render = true;
                    lease.Dispose();
                    return;
                }

                float baseDpi = _baseCanvas.Dpi > 0 ? _baseCanvas.Dpi : (float)(96.0 * rasterizationScale);
                CanvasBitmap? newBitmap = CreateCanvasBitmapFromLease(_baseCanvas, lease, baseDpi);
                if (newBitmap is null)
                {
                    return;
                }

                CanvasBitmapEntry cacheEntry = new(newBitmap);
                _tileCache.Add(in baseKey, cacheEntry);

                _level0Entry?.Release();
                _level0Entry = cacheEntry;
                _level0Bitmap = newBitmap;
                _baseCanvas.Invalidate();

                if (EffectiveZoom > DetailZoomThreshold)
                {
                    UpdateViewport(EffectiveZoom);
                }
            }
            catch (Exception ex)
            {
                lease.Dispose();
                Debug.WriteLine($"Base bitmap upload failed: {ex.Message}");
            }
        });
    }

    private async Task RenderDetailPatchAsync(Rect patchDip, double pixelScale, double currentZoom, double rasterScale)
    {
        if (PageInfo is not { } pageInfo || EffectiveDocumentId <= 0 || _isUnloaded)
        {
            return;
        }

        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = new CancellationTokenSource();
        CancellationToken ct = _detailCts.Token;

        int targetPageIndex = pageInfo.Index;
        long targetDocId = EffectiveDocumentId;
        int targetRotation = RotationAngle;

        bool swap = (targetRotation % 180 != 0);
        double pageW = Math.Max(1.0, swap ? pageInfo.DisplayHeightDip : pageInfo.DisplayWidthDip);
        double pageH = Math.Max(1.0, swap ? pageInfo.DisplayWidthDip : pageInfo.DisplayHeightDip);

        int fullW = Math.Max(1, (int)Math.Round(pageW * pixelScale));
        int fullH = Math.Max(1, (int)Math.Round(pageH * pixelScale));
        int regX = Math.Clamp((int)Math.Floor(patchDip.X * pixelScale), 0, fullW - 1);
        int regY = Math.Clamp((int)Math.Floor(patchDip.Y * pixelScale), 0, fullH - 1);
        int regW = Math.Clamp((int)Math.Ceiling(patchDip.Width * pixelScale), 1, fullW - regX);
        int regH = Math.Clamp((int)Math.Ceiling(patchDip.Height * pixelScale), 1, fullH - regY);

        double actualScaleX = fullW / pageW;
        double actualScaleY = fullH / pageH;
        Rect exactPatchDip = new(
            regX / actualScaleX,
            regY / actualScaleY,
            regW / actualScaleX,
            regH / actualScaleY);

        PdfBitmapLease lease;
        try
        {
            PdfRenderRequest request = new(
                targetDocId,
                targetPageIndex,
                regW,
                regH,
                fullW,
                fullH,
                -regX,
                -regY,
                targetRotation,
                PdfRenderFlags.Annotations);

            lease = await _pdfService.RenderAsync(
                request,
                PdfJobPriority.Visible,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Detail patch render failed: {ex.Message}");
            return;
        }

        if (ct.IsCancellationRequested || _isUnloaded)
        {
            lease.Dispose();
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (ct.IsCancellationRequested || _isUnloaded || !IsLoaded ||
                    PageInfo?.Index != targetPageIndex || EffectiveDocumentId != targetDocId || RotationAngle != targetRotation)
                {
                    lease.Dispose();
                    return;
                }

                ICanvasResourceCreator? resourceCreator = _detailCanvas.Device ?? _baseCanvas.Device;
                if (resourceCreator is null)
                {
                    try
                    {
                        resourceCreator = CanvasDevice.GetSharedDevice();
                    }
                    catch
                    {
                        lease.Dispose();
                        return;
                    }
                }

                double activeRasterScale = XamlRoot?.RasterizationScale ?? rasterScale;
                float targetDpiScale = (float)Math.Max(1.0, pixelScale / Math.Max(0.1, activeRasterScale));
                float targetDpi = (float)(96.0 * activeRasterScale * targetDpiScale);

                CanvasBitmap? newBitmap = CreateCanvasBitmapFromLease(resourceCreator, lease, targetDpi);
                if (newBitmap is null)
                {
                    return;
                }

                CanvasBitmap? oldDetail = _detailBitmap;
                _detailBitmap = newBitmap;
                _detailRectDip = exactPatchDip;
                _renderedZoom = currentZoom;
                _renderedPixelScale = pixelScale;

                _detailTranslate.X = exactPatchDip.X;
                _detailTranslate.Y = exactPatchDip.Y;
                _detailCanvas.Width = Math.Max(1.0, exactPatchDip.Width);
                _detailCanvas.Height = Math.Max(1.0, exactPatchDip.Height);

                if (Math.Abs(_detailCanvas.DpiScale - targetDpiScale) > 0.001f)
                {
                    _detailCanvas.DpiScale = targetDpiScale;
                }

                _detailCanvas.Opacity = 1.0;
                _detailCanvas.Visibility = Visibility.Visible;
                _detailCanvas.Invalidate();
                oldDetail?.Dispose();
            }
            catch (Exception ex)
            {
                lease.Dispose();
                Debug.WriteLine($"Detail patch upload failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Computes the visible portion of this page in local page DIPs (0..DisplayWidthDip, 0..DisplayHeightDip).
    /// Returns Rect.Empty if the page does not intersect the current ScrollViewer viewport.
    /// </summary>
    public Rect GetVisibleRectInPageDips() => GetVisibleRectInPageDips(EffectiveZoom);

    /// <summary>
    /// Computes the visible portion of this page in local page DIPs for the specified target zoom factor.
    /// Returns Rect.Empty if the page does not intersect the current ScrollViewer viewport.
    /// </summary>
    /// <param name="targetZoom">The target zoom factor.</param>
    /// <returns>The visible rectangle in page DIPs, or <see cref="Rect.Empty"/> if off-screen.</returns>
    public Rect GetVisibleRectInPageDips(double targetZoom)
    {
        if (PageInfo is null || !IsLoaded)
        {
            return Rect.Empty;
        }

        bool swap = (RotationAngle % 180 != 0);
        double defaultW = swap ? PageInfo.DisplayHeightDip : PageInfo.DisplayWidthDip;
        double defaultH = swap ? PageInfo.DisplayWidthDip : PageInfo.DisplayHeightDip;

        double pageW = ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : defaultW);
        double pageH = ActualHeight > 0 ? ActualHeight : (Height > 0 ? Height : defaultH);
        if (pageW <= 0 || pageH <= 0)
        {
            return Rect.Empty;
        }

        var scrollViewer = FindAncestor<ScrollViewer>(this);
        if (scrollViewer is null || scrollViewer.ViewportWidth <= 0 || scrollViewer.ViewportHeight <= 0)
        {
            return new Rect(0, 0, pageW, pageH);
        }

        Rect pageInSv;
        try
        {
            var pageToSv = TransformToVisual(scrollViewer);
            pageInSv = pageToSv.TransformBounds(new Rect(0, 0, pageW, pageH));
        }
        catch
        {
            return Rect.Empty;
        }

        if (pageInSv.IsEmpty || pageInSv.Width <= 1.0 || pageInSv.Height <= 1.0)
        {
            return Rect.Empty;
        }

        double vw = scrollViewer.ViewportWidth;
        double vh = scrollViewer.ViewportHeight;
        double viewportLeft = 0.0;
        double viewportTop = 0.0;
        double viewportRight = vw;
        double viewportBottom = vh;

        // If ChangeView was just invoked to zoom in, the visual transform may still reflect the previous zoom factor.
        double actualTransformZoom = pageInSv.Width / pageW;
        if (targetZoom > DetailZoomThreshold && actualTransformZoom > 0.05 && Math.Abs(actualTransformZoom - targetZoom) > 0.05 && targetZoom > actualTransformZoom)
        {
            double scaleRatio = actualTransformZoom / targetZoom;
            double effVw = vw * scaleRatio;
            double effVh = vh * scaleRatio;
            viewportLeft = (vw - effVw) / 2.0;
            viewportTop = (vh - effVh) / 2.0;
            viewportRight = viewportLeft + effVw;
            viewportBottom = viewportTop + effVh;
        }

        double intX = Math.Max(viewportLeft, pageInSv.X);
        double intY = Math.Max(viewportTop, pageInSv.Y);
        double intR = Math.Min(viewportRight, pageInSv.X + pageInSv.Width);
        double intB = Math.Min(viewportBottom, pageInSv.Y + pageInSv.Height);

        if (intR <= intX + 1.0 || intB <= intY + 1.0)
        {
            return Rect.Empty;
        }

        Rect visibleInSv = new(intX, intY, intR - intX, intB - intY);

        double relX = (visibleInSv.X - pageInSv.X) / pageInSv.Width;
        double relY = (visibleInSv.Y - pageInSv.Y) / pageInSv.Height;
        double relW = visibleInSv.Width / pageInSv.Width;
        double relH = visibleInSv.Height / pageInSv.Height;

        double x = Math.Clamp(relX * pageW, 0, pageW);
        double y = Math.Clamp(relY * pageH, 0, pageH);
        double w = Math.Clamp(relW * pageW, 1, pageW - x);
        double h = Math.Clamp(relH * pageH, 1, pageH - y);

        return new Rect(x, y, w, h);
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        DependencyObject? parent = VisualTreeHelper.GetParent(current);
        while (parent is not null)
        {
            if (parent is T match)
            {
                return match;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private void CancelInFlightRenders()
    {
        _baseCts?.Cancel();
        _baseCts?.Dispose();
        _baseCts = null;

        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;
    }

    private static unsafe CanvasBitmap? CreateCanvasBitmapFromLease(
        ICanvasResourceCreator resourceCreator,
        PdfBitmapLease lease,
        float dpi)
    {
        try
        {
            if (lease.IsDisposed || lease.Width <= 0 || lease.Height <= 0 || lease.SizeInBytes <= 0 || lease.SizeInBytes > int.MaxValue)
            {
                return null;
            }

            int byteLength = (int)lease.SizeInBytes;
            Windows.Storage.Streams.IBuffer nativeBuffer = new Windows.Storage.Streams.Buffer((uint)byteLength)
            {
                Length = (uint)byteLength
            };

            if (nativeBuffer is ICanvasBufferByteAccess byteAccess)
            {
                byte* dst = byteAccess.Buffer;
                byte* src = (byte*)lease.Scan0;
                System.Buffer.MemoryCopy(src, dst, byteLength, byteLength);
            }
            else
            {
                using var stream = nativeBuffer.AsStream();
                stream.Write(lease.AsSpan());
            }

            return CanvasBitmap.CreateFromBytes(
                resourceCreator,
                nativeBuffer,
                lease.Width,
                lease.Height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                dpi);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or COMException)
        {
            return null;
        }
        finally
        {
            lease.Dispose();
        }
    }

    private void ReleaseBitmaps()
    {
        _level0Bitmap = null;
        CanvasBitmapEntry? oldEntry = _level0Entry;
        _level0Entry = null;
        oldEntry?.Release();

        CanvasBitmap? bd = _detailBitmap;
        _detailBitmap = null;
        bd?.Dispose();
    }
}
