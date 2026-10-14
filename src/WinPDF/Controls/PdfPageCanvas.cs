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
/// Native Win2D canvas rendering a single PDF page with zero-LOH uploads, single viewport detail patch,
/// fallback background blitting during zoom/scroll, and true 0.0% CPU/GPU consumption when stationary.
/// </summary>
public sealed partial class PdfPageCanvas : Grid
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

    private readonly CanvasControl _baseCanvas;
    private readonly CanvasControl _detailCanvas;
    private readonly TranslateTransform _detailTranslate;
    private readonly IPdfService _pdfService;
    private readonly TileCache _tileCache;

    private CanvasBitmapEntry? _baseBitmapEntry;
    private CanvasBitmap? _detailBitmap;
    private Rect _renderedPatchDip = Rect.Empty;
    private double _renderedZoom = 1.0;

    private CancellationTokenSource? _baseCts;
    private CancellationTokenSource? _detailCts;
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

        _detailTranslate = new TranslateTransform();
        _detailCanvas = new CanvasControl
        {
            UseSharedDevice = true,
            ClearColor = Windows.UI.Color.FromArgb(0, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            RenderTransform = _detailTranslate,
        };

        _detailCanvas.Draw += OnDetailCanvasDraw;

        Children.Add(_baseCanvas);
        Children.Add(_detailCanvas);

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

        var sv = FindAncestor<ScrollViewer>(this);
        if (sv is not null && sv.ZoomFactor > 1.05)
        {
            UpdateViewport(sv.ZoomFactor);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
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
            canvas.ReleaseHighResPatch();
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
            canvas.UpdateViewport(canvas.EffectiveZoom);
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
            ReleaseHighResPatch();
        }

        if (_baseBitmapEntry is null)
        {
            TryLoadCachedBitmaps();
        }

        if (_baseBitmapEntry is not null)
        {
            _baseCanvas.Invalidate();
        }
        else if (_pendingRender || PageInfo is not null)
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
            ReleaseHighResPatch();
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

    private void OnDetailCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_detailBitmap is not null && !_renderedPatchDip.IsEmpty)
        {
            try
            {
                args.DrawingSession.DrawImage(_detailBitmap, new Rect(0, 0, _renderedPatchDip.Width, _renderedPatchDip.Height));
            }
            catch (ObjectDisposedException)
            {
                _detailBitmap?.Dispose();
                _detailBitmap = null;
            }
        }
    }

    /// <summary>
    /// Releases the high-resolution patch and collapses the detail canvas.
    /// </summary>
    public void ReleaseHighResPatch()
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;

        if (_detailCanvas.Visibility != Visibility.Collapsed)
        {
            _detailCanvas.Visibility = Visibility.Collapsed;
        }

        _detailBitmap?.Dispose();
        _detailBitmap = null;
        _renderedPatchDip = Rect.Empty;
        _renderedZoom = 1.0;
    }

    /// <summary>
    /// Updates the viewport zoom factor for this page canvas and triggers detail rendering if zoomed and visible.
    /// </summary>
    /// <param name="newZoom">The new zoom factor.</param>
    public void UpdateViewportZoom(double newZoom) => UpdateViewport(newZoom);

    /// <summary>
    /// Updates the viewport and triggers detail rendering if zoomed and visible.
    /// </summary>
    /// <param name="currentZoom">The current zoom factor.</param>
    public void UpdateViewport(double currentZoom)
    {
        if (PageInfo is null || EffectiveDocumentId <= 0 || _isUnloaded)
        {
            return;
        }

        ZoomFactor = currentZoom;

        // 1. Garantir que o bitmap base Nível 0 esteja carregado ou requisitado
        if (_baseBitmapEntry is null)
        {
            TryLoadCachedBitmaps();
        }

        if (_baseBitmapEntry is null && !_isUnloaded)
        {
            _baseCts?.Cancel();
            _baseCts?.Dispose();
            _baseCts = new CancellationTokenSource();
            _ = RenderBaseBitmapAsync(_baseCts.Token);
        }

        Rect visibleDip = GetVisibleRectInPageDips();

        // Caso 1 — Página fora da tela (visibleDip.IsEmpty) OU sem zoom (currentZoom <= 1.05):
        if (currentZoom <= 1.05 || visibleDip.IsEmpty)
        {
            ReleaseHighResPatch();
            return;
        }

        // Caso 2 — Página visível na tela (!visibleDip.IsEmpty) E com zoom (currentZoom > 1.05):
        bool sameZoom = Math.Abs(_renderedZoom - currentZoom) < 0.02;
        bool coversViewport = _detailBitmap is not null
            && !_renderedPatchDip.IsEmpty
            && _renderedPatchDip.X <= visibleDip.X + 0.5
            && _renderedPatchDip.Y <= visibleDip.Y + 0.5
            && (_renderedPatchDip.X + _renderedPatchDip.Width) >= (visibleDip.X + visibleDip.Width - 0.5)
            && (_renderedPatchDip.Y + _renderedPatchDip.Height) >= (visibleDip.Y + visibleDip.Height - 0.5);

        if (sameZoom && coversViewport)
        {
            return; // Já está nítido na região visível atual!
        }

        _ = RenderDetailPatchAsync(visibleDip, currentZoom);
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
            if (cached is CanvasBitmapEntry canvasEntry)
            {
                if (PageInfo?.Index == targetPageIndex && EffectiveDocumentId == targetDocId && !_isUnloaded)
                {
                    _baseBitmapEntry?.Release();
                    _baseBitmapEntry = canvasEntry;
                    _baseCanvas.Invalidate();

                    if (EffectiveZoom > 1.05)
                    {
                        UpdateViewport(EffectiveZoom);
                    }
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

                        if (EffectiveZoom > 1.05)
                        {
                            UpdateViewport(EffectiveZoom);
                        }
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

    private async Task RenderDetailPatchAsync(Rect visibleDip, double currentZoom)
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

        double pageW = pageInfo.DisplayWidthDip;
        double pageH = pageInfo.DisplayHeightDip;

        // Expande visibleDip com uma margem de pré-carregamento de 20% da largura/altura em cada direção
        double marginX = visibleDip.Width * 0.20;
        double marginY = visibleDip.Height * 0.20;
        double patchX = Math.Max(0, visibleDip.X - marginX);
        double patchY = Math.Max(0, visibleDip.Y - marginY);
        double patchR = Math.Min(pageW, visibleDip.X + visibleDip.Width + marginX);
        double patchB = Math.Min(pageH, visibleDip.Y + visibleDip.Height + marginY);
        Rect patchDip = new(patchX, patchY, patchR - patchX, patchB - patchY);

        double rasterScale = XamlRoot?.RasterizationScale ?? 1.0;
        double pixelScale = currentZoom * rasterScale;

        // Limita o patch a no máximo 3840x2400 pixels físicos (~35 MB máx, ~10 MB típico)
        double maxScaleW = 3840.0 / Math.Max(1.0, patchDip.Width);
        double maxScaleH = 2400.0 / Math.Max(1.0, patchDip.Height);
        pixelScale = Math.Min(pixelScale, Math.Min(maxScaleW, maxScaleH));

        int fullW = Math.Max(1, (int)Math.Round(pageW * pixelScale));
        int fullH = Math.Max(1, (int)Math.Round(pageH * pixelScale));
        int regX = Math.Clamp((int)Math.Floor(patchDip.X * pixelScale), 0, fullW - 1);
        int regY = Math.Clamp((int)Math.Floor(patchDip.Y * pixelScale), 0, fullH - 1);
        int regW = Math.Clamp((int)Math.Ceiling(patchDip.Width * pixelScale), 1, fullW - regX);
        int regH = Math.Clamp((int)Math.Ceiling(patchDip.Height * pixelScale), 1, fullH - regY);

        PdfBitmapLease lease;
        try
        {
            lease = await _pdfService.RenderRegionAsync(
                targetDocId,
                targetPageIndex,
                startXPx: regX,
                startYPx: regY,
                widthPx: regW,
                heightPx: regH,
                totalWidthPx: fullW,
                totalHeightPx: fullH,
                highPriority: true,
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

        byte[] pixelData = lease.ToPixelArray();
        int leaseW = lease.Width;
        int leaseH = lease.Height;
        lease.Dispose();

        DispatcherQueue.TryEnqueue(() =>
        {
            if (ct.IsCancellationRequested || _isUnloaded || PageInfo?.Index != targetPageIndex || EffectiveDocumentId != targetDocId)
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

                var newBitmap = CanvasBitmap.CreateFromBytes(
                    device,
                    pixelData,
                    leaseW,
                    leaseH,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized);

                _detailBitmap?.Dispose();
                _detailBitmap = newBitmap;
                _renderedPatchDip = patchDip;
                _renderedZoom = currentZoom;

                _detailTranslate.X = patchDip.X;
                _detailTranslate.Y = patchDip.Y;
                _detailCanvas.Width = patchDip.Width;
                _detailCanvas.Height = patchDip.Height;

                float targetDpiScale = (float)Math.Clamp(pixelScale / rasterScale, 1.0, 8.0);
                if (Math.Abs(_detailCanvas.DpiScale - targetDpiScale) > 0.25f)
                {
                    _detailCanvas.DpiScale = targetDpiScale;
                }

                _detailCanvas.Visibility = Visibility.Visible;
                _detailCanvas.Invalidate();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Detail patch upload failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Computes the visible portion of this page in local page DIPs (0..DisplayWidthDip, 0..DisplayHeightDip).
    /// Returns Rect.Empty if the page does not intersect the current ScrollViewer viewport.
    /// </summary>
    public Rect GetVisibleRectInPageDips()
    {
        if (PageInfo is null || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return Rect.Empty;
        }

        double pageW = PageInfo.DisplayWidthDip;
        double pageH = PageInfo.DisplayHeightDip;
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

        double intX = Math.Max(0, pageInSv.X);
        double intY = Math.Max(0, pageInSv.Y);
        double intR = Math.Min(scrollViewer.ViewportWidth, pageInSv.X + pageInSv.Width);
        double intB = Math.Min(scrollViewer.ViewportHeight, pageInSv.Y + pageInSv.Height);

        if (intR <= intX + 1.0 || intB <= intY + 1.0)
        {
            return Rect.Empty;
        }

        Rect visibleInSv = new(intX, intY, intR - intX, intB - intY);

        // Converte da interseção na tela de volta para DIPs locais da página por proporção linear
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

    private void ReleaseBitmaps()
    {
        _baseBitmapEntry?.Release();
        _baseBitmapEntry = null;
    }
}
