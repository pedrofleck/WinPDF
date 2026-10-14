using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas;
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
/// Native Win2D canvas rendering a single PDF page with zero-LOH uploads, hybrid full-page / 512×512 tiles,
/// fallback background blitting during zoom, and true 0.0% CPU/GPU consumption when stationary.
/// </summary>
public sealed class PdfPageCanvas : Grid
{
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

    private readonly CanvasControl _canvasControl;
    private readonly IPdfService _pdfService;
    private readonly TileCache _tileCache;

    private CanvasBitmapEntry? _baseBitmapEntry;
    private CanvasBitmapEntry? _activeFullPageEntry;
    private CancellationTokenSource? _baseCts;
    private CancellationTokenSource? _renderCts;
    private DocumentViewModel? _observedDocument;
    private int _loadedPageIndex = -1;
    private volatile bool _isUnloaded;
    private bool _isResourcesCreated;
    private bool _pendingRender;

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
    /// Updates the viewport zoom factor for this page canvas and triggers re-rendering if the quantized zoom changed.
    /// </summary>
    /// <param name="newZoom">The new zoom factor.</param>
    public void UpdateViewportZoom(double newZoom)
    {
        ZoomFactor = newZoom;
        UpdateRender();
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
    /// Gets the effective zoom factor, looking at explicit ZoomFactor, active document ZoomFactor, or parent ScrollViewer.
    /// </summary>
    private double EffectiveZoom
    {
        get
        {
            if (ZoomFactor > 0.05 && Math.Abs(ZoomFactor - 1.0) > 0.001)
            {
                return ZoomFactor;
            }

            if (_observedDocument is not null && _observedDocument.ZoomFactor > 0.05)
            {
                return _observedDocument.ZoomFactor;
            }

            var scrollViewer = FindAncestor<ScrollViewer>(this);
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

        _canvasControl = new CanvasControl
        {
            UseSharedDevice = true,
            ClearColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            DpiScale = 1.0f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _canvasControl.CreateResources += OnCreateResources;
        _canvasControl.Draw += OnDraw;

        Children.Add(_canvasControl);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = false;
        UpdateDimensions();
        SubscribeToActiveDocument();
        if (_observedDocument is not null)
        {
            ZoomFactor = _observedDocument.ZoomFactor;
        }
        else
        {
            var scrollViewer = FindAncestor<ScrollViewer>(this);
            if (scrollViewer is not null && scrollViewer.ZoomFactor > 0.05)
            {
                ZoomFactor = scrollViewer.ZoomFactor;
            }
        }
        TryLoadCachedBitmaps();
        UpdateRender();
        CanvasLoaded?.Invoke(this);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        CanvasUnloaded?.Invoke(this);
        CancelInFlightRenders();
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
        if (_observedDocument is not null)
        {
            _observedDocument.PropertyChanged -= OnObservedDocumentPropertyChanged;
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
            DispatcherQueue.TryEnqueue(UpdateRender);
        }
    }

    private static void OnPageInfoPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageCanvas canvas)
        {
            canvas.CancelInFlightRenders();
            canvas.ReleaseBitmaps();
            canvas._loadedPageIndex = canvas.PageInfo?.Index ?? -1;
            canvas.UpdateDimensions();
            canvas.TryLoadCachedBitmaps();
            canvas._canvasControl.Invalidate();
            canvas.UpdateRender();
        }
    }

    private static void OnDocumentIdPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageCanvas canvas)
        {
            canvas.CancelInFlightRenders();
            canvas.ReleaseBitmaps();
            canvas.TryLoadCachedBitmaps();
            canvas._canvasControl.Invalidate();
            canvas.UpdateRender();
        }
    }

    private static void OnZoomFactorPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageCanvas canvas)
        {
            canvas.UpdateRender();
        }
    }

    private void UpdateDimensions()
    {
        if (PageInfo is not null)
        {
            Width = PageInfo.DisplayWidthDip;
            Height = PageInfo.DisplayHeightDip;
            _canvasControl.Width = PageInfo.DisplayWidthDip;
            _canvasControl.Height = PageInfo.DisplayHeightDip;
        }
    }

    private void OnCreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        _isResourcesCreated = true;
        if (args.Reason == Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesReason.NewDevice)
        {
            _tileCache.HandleDeviceLost();
            _baseBitmapEntry?.Release();
            _baseBitmapEntry = null;
            _activeFullPageEntry?.Release();
            _activeFullPageEntry = null;
        }

        if (_pendingRender || _baseBitmapEntry is null)
        {
            _pendingRender = false;
            UpdateRender();
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
            if (cached is CanvasBitmapEntry canvasEntry)
            {
                _baseBitmapEntry?.Release();
                _baseBitmapEntry = canvasEntry;
            }
            else
            {
                cached?.Release();
            }
        }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (PageInfo is null)
        {
            return;
        }

        if (_loadedPageIndex != PageInfo.Index)
        {
            CancelInFlightRenders();
            ReleaseBitmaps();
            _loadedPageIndex = PageInfo.Index;
            UpdateRender();
            return;
        }

        var ds = args.DrawingSession;
        Rect pageRect = new(0, 0, PageInfo.DisplayWidthDip, PageInfo.DisplayHeightDip);

        // 1. Draw base white card
        ds.FillRectangle(pageRect, Windows.UI.Color.FromArgb(255, 255, 255, 255));

        // 2. Draw Level 0 base bitmap as fallback (never flicker, always smooth preview)
        if (_baseBitmapEntry is { IsDisposed: false, Bitmap: { } baseBmp })
        {
            try
            {
                ds.DrawImage(baseBmp, pageRect);
            }
            catch (ObjectDisposedException)
            {
                _baseBitmapEntry?.Release();
                _baseBitmapEntry = null;
                UpdateRender();
            }
        }

        ZoomLevel level = ZoomQuantizer.Resolve(EffectiveZoom);

        if (level.Mode == PageRenderMode.FullPage)
        {
            if (!level.IsBaseLevel && _activeFullPageEntry is { IsDisposed: false, Bitmap: { } fullBmp })
            {
                try
                {
                    ds.DrawImage(fullBmp, pageRect);
                }
                catch (ObjectDisposedException)
                {
                    _activeFullPageEntry?.Release();
                    _activeFullPageEntry = null;
                    UpdateRender();
                }
            }
        }
        else
        {
            // High magnification: draw all cached tiles on top
            DrawCachedTiles(ds, level);
        }
    }

    private void DrawCachedTiles(CanvasDrawingSession ds, ZoomLevel level)
    {
        if (PageInfo is null || EffectiveDocumentId <= 0)
        {
            return;
        }

        double rasterizationScale = XamlRoot?.RasterizationScale ?? 1.0;
        int scalePercent = (int)Math.Round(rasterizationScale * 100);
        double Zq = level.Factor;
        int step = level.Step;
        float effectiveScale = (float)(Zq * rasterizationScale);

        (int pageWidth, int pageHeight) = RenderGeometry.GetPagePixelSize(PageInfo, 0, Zq, rasterizationScale);
        (int cols, int rows) = RenderGeometry.GetTileGrid(pageWidth, pageHeight);

        for (int ty = 0; ty < rows; ty++)
        {
            for (int tx = 0; tx < cols; tx++)
            {
                TileKey key = new(
                    EffectiveDocumentId,
                    PageInfo.Index,
                    TileKind.Tile,
                    step,
                    tx,
                    ty,
                    0,
                    0,
                    scalePercent);

                if (_tileCache.TryGet(in key, out TileCacheEntry? entry))
                {
                    if (entry is CanvasBitmapEntry { IsDisposed: false, Bitmap: { } tileBmp })
                    {
                        (int leftPx, int topPx, int widthPx, int heightPx) = RenderGeometry.GetTileRect(pageWidth, pageHeight, tx, ty);
                        Rect tileRectDip = new(
                            leftPx / effectiveScale,
                            topPx / effectiveScale,
                            widthPx / effectiveScale,
                            heightPx / effectiveScale);

                        try
                        {
                            ds.DrawImage(tileBmp, tileRectDip);
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    }
                    entry?.Release();
                }
            }
        }
    }

    private void UpdateRender()
    {
        if (PageInfo is null || EffectiveDocumentId <= 0 || _isUnloaded)
        {
            return;
        }

        if (!_isResourcesCreated)
        {
            _pendingRender = true;
            return;
        }

        _pendingRender = false;

        // Try load from cache first
        if (_baseBitmapEntry is null)
        {
            TryLoadCachedBitmaps();
        }

        // Ensure Level 0 base bitmap is requested or loaded
        if (_baseBitmapEntry is null)
        {
            _baseCts?.Cancel();
            _baseCts?.Dispose();
            _baseCts = new CancellationTokenSource();
            _ = RenderBaseBitmapAsync(_baseCts.Token);
        }

        double rasterizationScale = XamlRoot?.RasterizationScale ?? 1.0;
        ZoomLevel level = ZoomQuantizer.Resolve(EffectiveZoom);
        double Zq = level.Factor;

        // Ensure CanvasControl backing surface resolution matches quantized zoom Zq
        float maxDim = (float)Math.Max(PageInfo.DisplayWidthDip, PageInfo.DisplayHeightDip);
        float maxSafeDpiScale = (maxDim > 0 && rasterizationScale > 0)
            ? (float)(16384.0 / (maxDim * rasterizationScale))
            : 8.0f;
        float targetDpiScale = (float)Math.Clamp(Zq, 0.25, Math.Min(8.0, maxSafeDpiScale));

        if (Math.Abs(_canvasControl.DpiScale - targetDpiScale) > 0.01f)
        {
            _canvasControl.DpiScale = targetDpiScale;
        }

        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;

        if (level.Mode == PageRenderMode.FullPage)
        {
            if (level.IsBaseLevel)
            {
                // Level 0 is already the sharp base representation, zero CPU work needed
                _activeFullPageEntry?.Release();
                _activeFullPageEntry = null;
                _canvasControl.Invalidate();
            }
            else
            {
                _ = RenderFullPageAsync(level, ct);
            }
        }
        else
        {
            _ = RenderTilesAsync(level, ct);
        }
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
            if (cached is CanvasBitmapEntry canvasEntry)
            {
                if (PageInfo?.Index == targetPageIndex && EffectiveDocumentId == targetDocId && !_isUnloaded)
                {
                    _baseBitmapEntry?.Release();
                    _baseBitmapEntry = canvasEntry;
                    _canvasControl.Invalidate();
                    return;
                }
            }
            cached?.Release();
        }

        (int pixelWidth, int pixelHeight) = RenderGeometry.ComputeFullPagePixelSize(pageInfo, 1.0, rasterizationScale, 0);

        try
        {
            PdfBitmapLease lease = await _pdfService.RenderPageAsync(
                targetDocId,
                targetPageIndex,
                pixelWidth,
                pixelHeight,
                highPriority: true,
                ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                lease.Dispose();
                return;
            }

            byte[] pixelData = lease.ToPixelArray();
            int width = lease.Width;
            int height = lease.Height;
            lease.Dispose();

            DispatcherQueue.TryEnqueue(() =>
            {
                if (ct.IsCancellationRequested || _isUnloaded || PageInfo?.Index != targetPageIndex || EffectiveDocumentId != targetDocId)
                {
                    return;
                }

                CanvasDevice? device = _canvasControl.Device ?? CanvasDevice.GetSharedDevice();
                if (device is null)
                {
                    return;
                }

                try
                {
                    CanvasBitmap bitmap = CanvasBitmap.CreateFromBytes(
                        device,
                        pixelData,
                        width,
                        height,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized);

                    CanvasBitmapEntry entry = new(bitmap);
                    _tileCache.Add(in baseKey, entry);

                    _baseBitmapEntry?.Release();
                    _baseBitmapEntry = entry;
                    _canvasControl.Invalidate();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Base bitmap upload failed: {ex.Message}");
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Base bitmap render failed: {ex.Message}");
        }
    }

    private async Task RenderFullPageAsync(ZoomLevel level, CancellationToken ct)
    {
        if (PageInfo is not { } pageInfo || EffectiveDocumentId <= 0)
        {
            return;
        }

        int targetPageIndex = pageInfo.Index;
        long targetDocId = EffectiveDocumentId;

        double rasterizationScale = XamlRoot?.RasterizationScale ?? 1.0;
        int scalePercent = (int)Math.Round(rasterizationScale * 100);
        double Zq = level.Factor;
        int step = level.Step;

        TileKey key = new(
            targetDocId,
            targetPageIndex,
            TileKind.FullPage,
            ZoomStep: step,
            TileX: 0,
            TileY: 0,
            RenderFlags: 0,
            Rotation: 0,
            ScalePercent: scalePercent);

        if (_tileCache.TryGet(in key, out TileCacheEntry? cached))
        {
            if (cached is CanvasBitmapEntry canvasEntry)
            {
                if (PageInfo?.Index == targetPageIndex && EffectiveDocumentId == targetDocId && !_isUnloaded)
                {
                    _activeFullPageEntry?.Release();
                    _activeFullPageEntry = canvasEntry;
                    _canvasControl.Invalidate();
                    return;
                }
            }
            cached?.Release();
        }

        (int pixelWidth, int pixelHeight) = RenderGeometry.ComputeFullPagePixelSize(pageInfo, Zq, rasterizationScale, 0);

        try
        {
            PdfBitmapLease lease = await _pdfService.RenderPageAsync(
                targetDocId,
                targetPageIndex,
                pixelWidth,
                pixelHeight,
                highPriority: true,
                ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                lease.Dispose();
                return;
            }

            byte[] pixelData = lease.ToPixelArray();
            int width = lease.Width;
            int height = lease.Height;
            lease.Dispose();

            DispatcherQueue.TryEnqueue(() =>
            {
                if (ct.IsCancellationRequested || _isUnloaded || PageInfo?.Index != targetPageIndex || EffectiveDocumentId != targetDocId)
                {
                    return;
                }

                CanvasDevice? device = _canvasControl.Device ?? CanvasDevice.GetSharedDevice();
                if (device is null)
                {
                    return;
                }

                try
                {
                    CanvasBitmap bitmap = CanvasBitmap.CreateFromBytes(
                        device,
                        pixelData,
                        width,
                        height,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized);

                    CanvasBitmapEntry entry = new(bitmap);
                    _tileCache.Add(in key, entry);

                    _activeFullPageEntry?.Release();
                    _activeFullPageEntry = entry;
                    _canvasControl.Invalidate();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Full page upload failed: {ex.Message}");
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Full page render failed: {ex.Message}");
        }
    }

    private async Task RenderTilesAsync(ZoomLevel level, CancellationToken ct)
    {
        if (PageInfo is not { } pageInfo || EffectiveDocumentId <= 0)
        {
            return;
        }

        int targetPageIndex = pageInfo.Index;
        long targetDocId = EffectiveDocumentId;

        double rasterizationScale = XamlRoot?.RasterizationScale ?? 1.0;
        int scalePercent = (int)Math.Round(rasterizationScale * 100);
        double Zq = level.Factor;
        int step = level.Step;

        (int pageWidth, int pageHeight) = RenderGeometry.GetPagePixelSize(pageInfo, 0, Zq, rasterizationScale);
        (int cols, int rows) = RenderGeometry.GetTileGrid(pageWidth, pageHeight);

        float effectiveScale = (float)(Zq * rasterizationScale);
        Rect visibleRect = GetVisibleRectInPageDips();
        if (visibleRect.IsEmpty || visibleRect.Width <= 0 || visibleRect.Height <= 0)
        {
            visibleRect = new Rect(0, 0, pageInfo.DisplayWidthDip, pageInfo.DisplayHeightDip);
        }

        int startTileX = Math.Max(0, (int)Math.Floor(visibleRect.X * effectiveScale / RenderGeometry.TileSizePx));
        int startTileY = Math.Max(0, (int)Math.Floor(visibleRect.Y * effectiveScale / RenderGeometry.TileSizePx));
        int endTileX = Math.Min(cols - 1, (int)Math.Floor((visibleRect.X + visibleRect.Width) * effectiveScale / RenderGeometry.TileSizePx));
        int endTileY = Math.Min(rows - 1, (int)Math.Floor((visibleRect.Y + visibleRect.Height) * effectiveScale / RenderGeometry.TileSizePx));

        for (int ty = startTileY; ty <= endTileY; ty++)
        {
            for (int tx = startTileX; tx <= endTileX; tx++)
            {
                if (ct.IsCancellationRequested || _isUnloaded)
                {
                    return;
                }

                (int leftPx, int topPx, int widthPx, int heightPx) = RenderGeometry.GetTileRect(pageWidth, pageHeight, tx, ty);

                TileKey key = new(
                    targetDocId,
                    targetPageIndex,
                    TileKind.Tile,
                    step,
                    tx,
                    ty,
                    0,
                    0,
                    scalePercent);

                if (_tileCache.TryGet(in key, out TileCacheEntry? existing))
                {
                    existing?.Release();
                    continue;
                }

                try
                {
                    PdfBitmapLease lease = await _pdfService.RenderRegionAsync(
                        targetDocId,
                        targetPageIndex,
                        startXPx: leftPx,
                        startYPx: topPx,
                        widthPx: widthPx,
                        heightPx: heightPx,
                        totalWidthPx: pageWidth,
                        totalHeightPx: pageHeight,
                        highPriority: true,
                        ct).ConfigureAwait(false);

                    if (ct.IsCancellationRequested)
                    {
                        lease.Dispose();
                        return;
                    }

                    byte[] pixelData = lease.ToPixelArray();
                    int leaseWidth = lease.Width;
                    int leaseHeight = lease.Height;
                    lease.Dispose();

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (ct.IsCancellationRequested || _isUnloaded || PageInfo?.Index != targetPageIndex || EffectiveDocumentId != targetDocId)
                        {
                            return;
                        }

                        CanvasDevice? device = _canvasControl.Device ?? CanvasDevice.GetSharedDevice();
                        if (device is null)
                        {
                            return;
                        }

                        try
                        {
                            CanvasBitmap cb = CanvasBitmap.CreateFromBytes(
                                device,
                                pixelData,
                                leaseWidth,
                                leaseHeight,
                                DirectXPixelFormat.B8G8R8A8UIntNormalized);

                            CanvasBitmapEntry entry = new(cb);
                            _tileCache.Add(in key, entry);
                            entry.Release();

                            _canvasControl.Invalidate();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Tile upload failed: {ex.Message}");
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Tile render failed: {ex.Message}");
                }
            }
        }
    }

    private Rect GetVisibleRectInPageDips()
    {
        if (PageInfo is null)
        {
            return Rect.Empty;
        }

        try
        {
            var scrollViewer = FindAncestor<ScrollViewer>(this);
            if (scrollViewer is null)
            {
                return new Rect(0, 0, PageInfo.DisplayWidthDip, PageInfo.DisplayHeightDip);
            }

            var transform = TransformToVisual(scrollViewer);
            Point pageTopLeftInScrollViewer = transform.TransformPoint(new Point(0, 0));
            double zoom = Math.Max(0.1, scrollViewer.ZoomFactor);

            double viewLeft = scrollViewer.HorizontalOffset;
            double viewTop = scrollViewer.VerticalOffset;
            double viewWidth = scrollViewer.ViewportWidth;
            double viewHeight = scrollViewer.ViewportHeight;

            double visibleLeft = Math.Max(0, (viewLeft - pageTopLeftInScrollViewer.X) / zoom);
            double visibleTop = Math.Max(0, (viewTop - pageTopLeftInScrollViewer.Y) / zoom);
            double visibleRight = Math.Min(PageInfo.DisplayWidthDip, (viewLeft + viewWidth - pageTopLeftInScrollViewer.X) / zoom);
            double visibleBottom = Math.Min(PageInfo.DisplayHeightDip, (viewTop + viewHeight - pageTopLeftInScrollViewer.Y) / zoom);

            if (visibleRight <= visibleLeft || visibleBottom <= visibleTop)
            {
                return Rect.Empty;
            }

            return new Rect(visibleLeft, visibleTop, visibleRight - visibleLeft, visibleBottom - visibleTop);
        }
        catch
        {
            return new Rect(0, 0, PageInfo.DisplayWidthDip, PageInfo.DisplayHeightDip);
        }
    }

    private static bool RectIntersects(Rect a, Rect b)
    {
        return a.X < b.X + b.Width &&
               a.X + a.Width > b.X &&
               a.Y < b.Y + b.Height &&
               a.Y + a.Height > b.Y;
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

        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = null;
    }

    private void ReleaseBitmaps()
    {
        _baseBitmapEntry?.Release();
        _baseBitmapEntry = null;

        _activeFullPageEntry?.Release();
        _activeFullPageEntry = null;
    }
}
