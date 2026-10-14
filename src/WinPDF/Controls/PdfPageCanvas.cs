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
/// Native Win2D canvas rendering a single PDF page with zero-LOH uploads, on-demand high-resolution
/// virtual tiles, fallback background blitting during zoom, and true 0.0% CPU/GPU consumption when stationary.
/// </summary>
public sealed partial class PdfPageCanvas : Grid
{
    private const int MaxTilesPerPass = 36;

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

    private readonly CanvasControl _baseCanvas;
    private readonly IPdfService _pdfService;
    private readonly TileCache _tileCache;

    private CanvasVirtualControl? _virtualCanvas;
    private CanvasBitmapEntry? _baseBitmapEntry;
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

        // PROIBIÇÃO ABSOLUTA: DpiScale do _baseCanvas fixo em 1.0f para sempre.
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

        Children.Add(_baseCanvas);

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
        DestroyVirtualCanvas();
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
            DispatcherQueue.TryEnqueue(UpdateRender);
        }
    }

    private static void OnPageInfoPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageCanvas canvas)
        {
            canvas.CancelInFlightRenders();
            canvas.DestroyVirtualCanvas();
            canvas.ReleaseBitmaps();
            canvas._loadedPageIndex = canvas.PageInfo?.Index ?? -1;
            canvas.UpdateDimensions();
            canvas.TryLoadCachedBitmaps();
            canvas._baseCanvas.Invalidate();
            canvas.UpdateRender();
        }
    }

    private static void OnDocumentIdPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageCanvas canvas)
        {
            canvas.CancelInFlightRenders();
            canvas.DestroyVirtualCanvas();
            canvas.ReleaseBitmaps();
            canvas.TryLoadCachedBitmaps();
            canvas._baseCanvas.Invalidate();
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
            _baseCanvas.Width = PageInfo.DisplayWidthDip;
            _baseCanvas.Height = PageInfo.DisplayHeightDip;

            if (_virtualCanvas is not null)
            {
                _virtualCanvas.Width = PageInfo.DisplayWidthDip;
                _virtualCanvas.Height = PageInfo.DisplayHeightDip;
            }
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
            DestroyVirtualCanvas();
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

    private void OnBaseCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (PageInfo is null)
        {
            return;
        }

        if (_loadedPageIndex != PageInfo.Index)
        {
            CancelInFlightRenders();
            DestroyVirtualCanvas();
            ReleaseBitmaps();
            _loadedPageIndex = PageInfo.Index;
            UpdateRender();
            return;
        }

        var ds = args.DrawingSession;
        Rect pageRect = new(0, 0, PageInfo.DisplayWidthDip, PageInfo.DisplayHeightDip);

        // 1. Folha branca base
        ds.FillRectangle(pageRect, Windows.UI.Color.FromArgb(255, 255, 255, 255));

        // 2. Bitmap base Nível 0 (Zq = 1.0f) como pré-visualização contínua e suave durante gestos
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
    }

    /// <summary>
    /// Destroys the virtual canvas overlay and releases 100% of its DirectX VRAM immediately.
    /// </summary>
    public void DestroyVirtualCanvas()
    {
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = null;

        if (_virtualCanvas is not null)
        {
            _virtualCanvas.RegionsInvalidated -= OnVirtualCanvasRegionsInvalidated;
            _virtualCanvas.RemoveFromVisualTree();
            Children.Remove(_virtualCanvas);
            _virtualCanvas = null;
        }
    }

    private void EnsureVirtualCanvasCreated(double Zq)
    {
        if (PageInfo is null)
        {
            return;
        }

        float targetDpiScale = (float)Math.Clamp(Zq, 1.0, 8.0);

        if (_virtualCanvas is null)
        {
            _virtualCanvas = new CanvasVirtualControl
            {
                UseSharedDevice = true,
                ClearColor = Windows.UI.Color.FromArgb(0, 0, 0, 0),
                DpiScale = targetDpiScale,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Width = PageInfo.DisplayWidthDip,
                Height = PageInfo.DisplayHeightDip,
            };

            _virtualCanvas.RegionsInvalidated += OnVirtualCanvasRegionsInvalidated;
            Children.Add(_virtualCanvas);
        }
        else
        {
            if (Math.Abs(_virtualCanvas.DpiScale - targetDpiScale) > 0.01f)
            {
                _virtualCanvas.DpiScale = targetDpiScale;
            }
            _virtualCanvas.Invalidate();
        }
    }

    private void OnVirtualCanvasRegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        if (PageInfo is null || EffectiveDocumentId <= 0)
        {
            return;
        }

        double rasterizationScale = XamlRoot?.RasterizationScale ?? 1.0;
        int scalePercent = (int)Math.Round(rasterizationScale * 100);
        ZoomLevel level = ZoomQuantizer.Resolve(EffectiveZoom);
        double Zq = level.Factor;
        int step = level.Step;
        float effectiveScale = (float)(Zq * rasterizationScale);

        (int pageWidth, int pageHeight) = RenderGeometry.GetPagePixelSize(PageInfo, 0, Zq, rasterizationScale);
        (int cols, int rows) = RenderGeometry.GetTileGrid(pageWidth, pageHeight);

        foreach (Rect region in args.InvalidatedRegions)
        {
            using CanvasDrawingSession ds = sender.CreateDrawingSession(region);

            int startTileX = Math.Max(0, (int)Math.Floor(region.X * effectiveScale / RenderGeometry.TileSizePx));
            int startTileY = Math.Max(0, (int)Math.Floor(region.Y * effectiveScale / RenderGeometry.TileSizePx));
            int endTileX = Math.Min(cols - 1, (int)Math.Floor(Math.Max(region.X, region.X + region.Width - 0.001) * effectiveScale / RenderGeometry.TileSizePx));
            int endTileY = Math.Min(rows - 1, (int)Math.Floor(Math.Max(region.Y, region.Y + region.Height - 0.001) * effectiveScale / RenderGeometry.TileSizePx));

            for (int ty = startTileY; ty <= endTileY; ty++)
            {
                for (int tx = startTileX; tx <= endTileX; tx++)
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

        // 1. Garantir que o bitmap base Nível 0 esteja carregado ou requisitado
        if (_baseBitmapEntry is null)
        {
            TryLoadCachedBitmaps();
        }

        if (_baseBitmapEntry is null)
        {
            _baseCts?.Cancel();
            _baseCts?.Dispose();
            _baseCts = new CancellationTokenSource();
            _ = RenderBaseBitmapAsync(_baseCts.Token);
        }

        double zoom = EffectiveZoom;

        // 2. Trava de Visibilidade e Zoom:
        Rect visibleDip = GetVisibleRectInPageDips();
        if (zoom <= 1.05 || visibleDip.IsEmpty)
        {
            DestroyVirtualCanvas();
            return;
        }

        // 3. Página visível e em zoom alto: ativa CanvasVirtualControl sob demanda
        ZoomLevel level = ZoomQuantizer.Resolve(zoom);
        double Zq = level.Factor;

        EnsureVirtualCanvasCreated(Zq);

        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;

        _ = RenderTilesThrottledAsync(level, visibleDip, ct);
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
                    _baseCanvas.Invalidate();
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
                if (_isUnloaded || PageInfo?.Index != targetPageIndex || EffectiveDocumentId != targetDocId)
                {
                    return;
                }

                try
                {
                    CanvasDevice? device = CanvasDevice.GetSharedDevice();
                    if (device is null)
                    {
                        return;
                    }

                    var bitmap = CanvasBitmap.CreateFromBytes(
                        device,
                        pixelData,
                        width,
                        height,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized);

                    CanvasBitmapEntry entry = new(bitmap);
                    _tileCache.Add(in baseKey, entry);

                    if (!ct.IsCancellationRequested)
                    {
                        _baseBitmapEntry?.Release();
                        _baseBitmapEntry = entry;
                        _baseCanvas.Invalidate();
                    }
                    else
                    {
                        entry.Release();
                    }
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

    private async Task RenderTilesThrottledAsync(ZoomLevel level, Rect visibleDip, CancellationToken ct)
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
        float effectiveScale = (float)(Zq * rasterizationScale);

        (int pageWidth, int pageHeight) = RenderGeometry.GetPagePixelSize(pageInfo, 0, Zq, rasterizationScale);
        (int cols, int rows) = RenderGeometry.GetTileGrid(pageWidth, pageHeight);

        // 1. Margem de pré-carregamento (1 tile ao redor do viewport: ~256/Zq DIPs)
        double marginDip = 256.0 / Zq;
        double prefetchLeft = Math.Max(0, visibleDip.X - marginDip);
        double prefetchTop = Math.Max(0, visibleDip.Y - marginDip);
        double prefetchRight = Math.Min(pageInfo.DisplayWidthDip, visibleDip.X + visibleDip.Width + marginDip);
        double prefetchBottom = Math.Min(pageInfo.DisplayHeightDip, visibleDip.Y + visibleDip.Height + marginDip);

        int startTileX = Math.Max(0, (int)Math.Floor(prefetchLeft * effectiveScale / RenderGeometry.TileSizePx));
        int startTileY = Math.Max(0, (int)Math.Floor(prefetchTop * effectiveScale / RenderGeometry.TileSizePx));
        int endTileX = Math.Min(cols - 1, (int)Math.Floor(prefetchRight * effectiveScale / RenderGeometry.TileSizePx));
        int endTileY = Math.Min(rows - 1, (int)Math.Floor(prefetchBottom * effectiveScale / RenderGeometry.TileSizePx));

        // 2. Trava física rígida de no máximo 36 tiles por passe
        if ((endTileX - startTileX + 1) * (endTileY - startTileY + 1) > MaxTilesPerPass)
        {
            int centerTileX = (int)Math.Floor((visibleDip.X + visibleDip.Width / 2.0) * effectiveScale / RenderGeometry.TileSizePx);
            int centerTileY = (int)Math.Floor((visibleDip.Y + visibleDip.Height / 2.0) * effectiveScale / RenderGeometry.TileSizePx);
            startTileX = Math.Max(0, centerTileX - 3);
            endTileX = Math.Min(cols - 1, startTileX + 5);
            startTileY = Math.Max(0, centerTileY - 3);
            endTileY = Math.Min(rows - 1, startTileY + 5);
        }

        // 3. Separar em tiles visíveis estritos (prioridade 1) e tiles do anel de prefetch (prioridade 2)
        int visStartX = Math.Max(0, (int)Math.Floor(visibleDip.X * effectiveScale / RenderGeometry.TileSizePx));
        int visStartY = Math.Max(0, (int)Math.Floor(visibleDip.Y * effectiveScale / RenderGeometry.TileSizePx));
        int visEndX = Math.Min(cols - 1, (int)Math.Floor(Math.Max(visibleDip.X, visibleDip.X + visibleDip.Width - 0.001) * effectiveScale / RenderGeometry.TileSizePx));
        int visEndY = Math.Min(rows - 1, (int)Math.Floor(Math.Max(visibleDip.Y, visibleDip.Y + visibleDip.Height - 0.001) * effectiveScale / RenderGeometry.TileSizePx));

        List<(int tx, int ty)> orderedTiles = [];
        List<(int tx, int ty)> prefetchList = [];

        for (int ty = startTileY; ty <= endTileY; ty++)
        {
            for (int tx = startTileX; tx <= endTileX; tx++)
            {
                if (tx >= visStartX && tx <= visEndX && ty >= visStartY && ty <= visEndY)
                {
                    orderedTiles.Add((tx, ty));
                }
                else
                {
                    prefetchList.Add((tx, ty));
                }
            }
        }

        orderedTiles.AddRange(prefetchList);

        // 4. Renderização controlada e sequencial, checando cancelamento a cada tile
        foreach ((int tx, int ty) in orderedTiles)
        {
            if (ct.IsCancellationRequested || _isUnloaded || _virtualCanvas is null)
            {
                return;
            }

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

            (int leftPx, int topPx, int widthPx, int heightPx) = RenderGeometry.GetTileRect(pageWidth, pageHeight, tx, ty);

            PdfBitmapLease lease;
            try
            {
                lease = await _pdfService.RenderRegionAsync(
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
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Tile render failed: {ex.Message}");
                continue;
            }

            if (ct.IsCancellationRequested || _isUnloaded || _virtualCanvas is null)
            {
                lease.Dispose();
                return;
            }

            byte[] pixelData = lease.ToPixelArray();
            int leaseW = lease.Width;
            int leaseH = lease.Height;
            lease.Dispose();

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isUnloaded || PageInfo?.Index != targetPageIndex || EffectiveDocumentId != targetDocId)
                {
                    return;
                }

                try
                {
                    CanvasDevice? device = CanvasDevice.GetSharedDevice();
                    if (device is null)
                    {
                        return;
                    }

                    var cb = CanvasBitmap.CreateFromBytes(
                        device,
                        pixelData,
                        leaseW,
                        leaseH,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized);

                    CanvasBitmapEntry entry = new(cb);
                    _tileCache.Add(in key, entry);
                    entry.Release();

                    if (!ct.IsCancellationRequested && _virtualCanvas is not null)
                    {
                        Rect tileRectDip = new(
                            leftPx / effectiveScale,
                            topPx / effectiveScale,
                            widthPx / effectiveScale,
                            heightPx / effectiveScale);

                        _virtualCanvas.Invalidate(tileRectDip);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Tile upload failed: {ex.Message}");
                }
            });
        }
    }

    /// <summary>
    /// Calculates the visible portion of this page in page DIPs intersecting the parent ScrollViewer viewport.
    /// Returns <see cref="Rect.Empty"/> if off-screen.
    /// </summary>
    public Rect GetVisibleRectInPageDips()
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
            Point pageP0 = transform.TransformPoint(new Point(0, 0));
            Point pageP1 = transform.TransformPoint(new Point(PageInfo.DisplayWidthDip, PageInfo.DisplayHeightDip));

            double pageLeftInViewport = Math.Min(pageP0.X, pageP1.X);
            double pageTopInViewport = Math.Min(pageP0.Y, pageP1.Y);
            double pageRightInViewport = Math.Max(pageP0.X, pageP1.X);
            double pageBottomInViewport = Math.Max(pageP0.Y, pageP1.Y);

            double pageWidthInViewport = pageRightInViewport - pageLeftInViewport;
            double pageHeightInViewport = pageBottomInViewport - pageTopInViewport;

            if (pageWidthInViewport <= 0.001 || pageHeightInViewport <= 0.001)
            {
                return Rect.Empty;
            }

            double viewportWidth = scrollViewer.ViewportWidth > 0 ? scrollViewer.ViewportWidth : scrollViewer.ActualWidth;
            double viewportHeight = scrollViewer.ViewportHeight > 0 ? scrollViewer.ViewportHeight : scrollViewer.ActualHeight;

            if (viewportWidth <= 0.001 || viewportHeight <= 0.001)
            {
                return Rect.Empty;
            }

            double intersectLeft = Math.Max(0, pageLeftInViewport);
            double intersectTop = Math.Max(0, pageTopInViewport);
            double intersectRight = Math.Min(viewportWidth, pageRightInViewport);
            double intersectBottom = Math.Min(viewportHeight, pageBottomInViewport);

            double intersectW = intersectRight - intersectLeft;
            double intersectH = intersectBottom - intersectTop;

            if (intersectW <= 1.0 || intersectH <= 1.0)
            {
                return Rect.Empty;
            }

            double visLeft = (intersectLeft - pageLeftInViewport) / pageWidthInViewport * PageInfo.DisplayWidthDip;
            double visTop = (intersectTop - pageTopInViewport) / pageHeightInViewport * PageInfo.DisplayHeightDip;
            double visWidth = intersectW / pageWidthInViewport * PageInfo.DisplayWidthDip;
            double visHeight = intersectH / pageHeightInViewport * PageInfo.DisplayHeightDip;

            visLeft = Math.Clamp(visLeft, 0, PageInfo.DisplayWidthDip);
            visTop = Math.Clamp(visTop, 0, PageInfo.DisplayHeightDip);
            visWidth = Math.Clamp(visWidth, 0, PageInfo.DisplayWidthDip - visLeft);
            visHeight = Math.Clamp(visHeight, 0, PageInfo.DisplayHeightDip - visTop);

            if (visWidth <= 1.0 || visHeight <= 1.0)
            {
                return Rect.Empty;
            }

            return new Rect(visLeft, visTop, visWidth, visHeight);
        }
        catch
        {
            return Rect.Empty;
        }
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
    }
}
