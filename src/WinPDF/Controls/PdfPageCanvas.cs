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
/// Native Win2D canvas rendering a single PDF page with zero-LOH uploads, unified single-SwapChain architecture,
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

    private readonly CanvasControl _canvas;
    private readonly IPdfService _pdfService;
    private readonly TileCache _tileCache;

    private CanvasBitmap? _level0Bitmap;
    private CanvasBitmap? _detailBitmap;
    private Rect _detailRectDip = Rect.Empty;
    private double _renderedZoom = 1.0;

    private CancellationTokenSource? _baseCts;
    private CancellationTokenSource? _detailCts;
    private DocumentViewModel? _observedDocument;
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
    /// Initializes a new instance of the <see cref="PdfPageCanvas"/> class with a single unified CanvasControl.
    /// </summary>
    public PdfPageCanvas()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;

        _pdfService = App.CurrentApp.Services.GetRequiredService<IPdfService>();
        _tileCache = App.CurrentApp.Services.GetRequiredService<TileCache>();

        _canvas = new CanvasControl
        {
            UseSharedDevice = true,
            ClearColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            DpiScale = 1.0f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _canvas.CreateResources += OnCreateResources;
        _canvas.Draw += OnCanvasDraw;

        Children.Add(_canvas);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
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
            canvas._canvas.ClearColor = canvas.IsNightMode
                ? Windows.UI.Color.FromArgb(255, 18, 18, 18)
                : Windows.UI.Color.FromArgb(255, 255, 255, 255);
            canvas._canvas.Invalidate();
        }
    }

    private static void OnRotationAnglePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageCanvas canvas)
        {
            canvas.ReleaseHighResPatch();
            canvas.UpdateDimensions();
            canvas._canvas.Invalidate();
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
            if (canvas.PageInfo is not null)
            {
                canvas.TryLoadCachedBitmaps();
                canvas._canvas.Invalidate();
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
                canvas._canvas.Invalidate();
                canvas.UpdateRender();
            }
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
            bool swap = (RotationAngle % 180 != 0);
            double effW = swap ? PageInfo.DisplayHeightDip : PageInfo.DisplayWidthDip;
            double effH = swap ? PageInfo.DisplayWidthDip : PageInfo.DisplayHeightDip;

            Width = effW;
            Height = effH;
            _canvas.Width = effW;
            _canvas.Height = effH;
        }
        else
        {
            Width = 0;
            Height = 0;
            _canvas.Width = 0;
            _canvas.Height = 0;
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
            _canvas.Invalidate();
        }
        else if (_needsLevel0Render || PageInfo is not null)
        {
            _needsLevel0Render = false;
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
            if (cached is CanvasBitmapEntry { IsDisposed: false } canvasEntry)
            {
                _level0Bitmap = canvasEntry.Bitmap;
            }

            cached?.Release();
        }
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
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
        double w = sender.ActualWidth > 0 ? sender.ActualWidth : Width;
        double h = sender.ActualHeight > 0 ? sender.ActualHeight : Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        Rect pageRect = new(0, 0, w, h);

        // 1. Folha base: branca no modo normal, OLED escuro #121212 no modo noturno
        ds.FillRectangle(pageRect, IsNightMode
            ? Windows.UI.Color.FromArgb(255, 18, 18, 18)
            : Windows.UI.Color.FromArgb(255, 255, 255, 255));

        // 2. Rotação centrada via Matrix3x2
        var oldTransform = ds.Transform;
        try
        {
            if (RotationAngle != 0)
            {
                float angleRad = (float)(RotationAngle * Math.PI / 180.0);
                Vector2 center = new((float)w / 2f, (float)h / 2f);
                ds.Transform = Matrix3x2.CreateRotation(angleRad, center) * oldTransform;
            }

            // 3. Desenhar Nível 0 base
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
                }
            }

            // 4. Desenhar Patch de alta resolução sobre a região visível (apenas sem rotação do usuário)
            if (RotationAngle == 0 && _detailBitmap is { } detailBmp && !_detailRectDip.IsEmpty && _detailRectDip.Width > 0 && _detailRectDip.Height > 0)
            {
                try
                {
                    DrawBitmapWithNightMode(ds, detailBmp, _detailRectDip, detailBmp.Bounds);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or COMException)
                {
                    _detailBitmap = null;
                }
            }
        }
        finally
        {
            ds.Transform = oldTransform;
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
                ds.DrawImage(invert, destRect, srcRect);
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
    /// Releases the high-resolution patch.
    /// </summary>
    public void ReleaseHighResPatch()
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;

        if (_detailBitmap is not null)
        {
            CanvasBitmap? old = _detailBitmap;
            _detailBitmap = null;
            _detailRectDip = Rect.Empty;
            _renderedZoom = 1.0;
            _canvas.Invalidate();
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

        ZoomFactor = currentZoom;

        // Garantir que o bitmap base Nível 0 esteja carregado ou requisitado
        if (_level0Bitmap is null)
        {
            TryLoadCachedBitmaps();
        }

        if (_level0Bitmap is null && !_isUnloaded)
        {
            _baseCts?.Cancel();
            _baseCts?.Dispose();
            _baseCts = new CancellationTokenSource();
            _ = RenderBaseBitmapAsync(_baseCts.Token);
        }

        Rect visibleDip = GetVisibleRectInPageDips();

        // Caso 1 — Página fora da tela (visibleDip.IsEmpty ou dimensão <= 0) OU sem zoom (currentZoom <= 1.05) OU com rotação:
        if (currentZoom <= 1.05 || visibleDip.IsEmpty || visibleDip.Width <= 0 || visibleDip.Height <= 0 || RotationAngle != 0)
        {
            ReleaseHighResPatch();
            return;
        }

        // Caso 2 — Página visível na tela (!visibleDip.IsEmpty) E com zoom (currentZoom > 1.05):
        bool sameZoom = Math.Abs(_renderedZoom - currentZoom) < 0.02;
        bool coversViewport = _detailBitmap is not null
            && !_detailRectDip.IsEmpty
            && _detailRectDip.X <= visibleDip.X + 0.5
            && _detailRectDip.Y <= visibleDip.Y + 0.5
            && (_detailRectDip.X + _detailRectDip.Width) >= (visibleDip.X + visibleDip.Width - 0.5)
            && (_detailRectDip.Y + _detailRectDip.Height) >= (visibleDip.Y + visibleDip.Height - 0.5);

        if (sameZoom && coversViewport)
        {
            return;
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
                if (PageInfo?.Index == targetPageIndex && EffectiveDocumentId == targetDocId && !_isUnloaded)
                {
                    _level0Bitmap = canvasEntry.Bitmap;
                    _canvas.Invalidate();

                    if (EffectiveZoom > 1.05)
                    {
                        UpdateViewport(EffectiveZoom);
                    }

                    cached?.Release();
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

                if (_canvas.Device is null)
                {
                    _needsLevel0Render = true;
                    lease.Dispose();
                    return;
                }

                using (var entry = new PdfBitmapEntry(lease))
                {
                    lease.Dispose();

                    var buffer = entry.Pixels.AsBuffer(0, entry.ByteLength);
                    CanvasBitmap newBitmap;
                    try
                    {
                        newBitmap = CanvasBitmap.CreateFromBytes(
                            _canvas,
                            buffer,
                            entry.Width,
                            entry.Height,
                            DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or COMException)
                    {
                        return;
                    }

                    CanvasBitmapEntry cacheEntry = new(newBitmap);
                    _tileCache.Add(in baseKey, cacheEntry);

                    CanvasBitmap? oldBitmap = _level0Bitmap;
                    _level0Bitmap = newBitmap;
                    _canvas.Invalidate();
                    oldBitmap?.Dispose();

                    if (EffectiveZoom > 1.05)
                    {
                        UpdateViewport(EffectiveZoom);
                    }
                }
            }
            catch (Exception ex)
            {
                lease.Dispose();
                Debug.WriteLine($"Base bitmap upload failed: {ex.Message}");
            }
        });
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

        double marginX = visibleDip.Width * 0.20;
        double marginY = visibleDip.Height * 0.20;
        double patchX = Math.Max(0, visibleDip.X - marginX);
        double patchY = Math.Max(0, visibleDip.Y - marginY);
        double patchR = Math.Min(pageW, visibleDip.X + visibleDip.Width + marginX);
        double patchB = Math.Min(pageH, visibleDip.Y + visibleDip.Height + marginY);
        Rect patchDip = new(patchX, patchY, patchR - patchX, patchB - patchY);

        double rasterScale = XamlRoot?.RasterizationScale ?? 1.0;
        double pixelScale = currentZoom * rasterScale;

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

        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (ct.IsCancellationRequested || _isUnloaded || !IsLoaded || PageInfo?.Index != targetPageIndex || EffectiveDocumentId != targetDocId)
                {
                    lease.Dispose();
                    return;
                }

                if (_canvas.Device is null)
                {
                    lease.Dispose();
                    return;
                }

                using (var entry = new PdfBitmapEntry(lease))
                {
                    lease.Dispose();

                    var buffer = entry.Pixels.AsBuffer(0, entry.ByteLength);
                    CanvasBitmap newBitmap;
                    try
                    {
                        newBitmap = CanvasBitmap.CreateFromBytes(
                            _canvas,
                            buffer,
                            entry.Width,
                            entry.Height,
                            DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or COMException)
                    {
                        return;
                    }

                    CanvasBitmap? oldDetail = _detailBitmap;
                    _detailBitmap = newBitmap;
                    _detailRectDip = patchDip;
                    _renderedZoom = currentZoom;
                    _canvas.Invalidate();
                    oldDetail?.Dispose();
                }
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
    public Rect GetVisibleRectInPageDips()
    {
        if (PageInfo is null || !IsLoaded)
        {
            return Rect.Empty;
        }

        double pageW = ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : PageInfo.DisplayWidthDip);
        double pageH = ActualHeight > 0 ? ActualHeight : (Height > 0 ? Height : PageInfo.DisplayHeightDip);
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
        CanvasBitmap? b0 = _level0Bitmap;
        _level0Bitmap = null;
        b0?.Dispose();

        CanvasBitmap? bd = _detailBitmap;
        _detailBitmap = null;
        bd?.Dispose();
    }
}
