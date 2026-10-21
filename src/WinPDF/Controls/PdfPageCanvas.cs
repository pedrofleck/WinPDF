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
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
/// GPU-accelerated search and text selection highlights, interactive pointer hit-testing, zero-LOH uploads,
/// and true 0.0% CPU/GPU consumption when stationary.
/// </summary>
public sealed partial class PdfPageCanvas : Grid
{
    private const double DetailZoomThreshold = 1.01;
    private const double MaxDetailPatchDimensionPx = 4096.0;
    private const double DragStartThresholdDip = 3.0;
    private const double HoverMoveMinDeltaDip = 2.0;
    private const double DragMoveMinDeltaDip = 1.0;
    private const long MultiClickMaxIntervalMs = 450;
    private const double MultiClickMaxDistanceDip = 8.0;

    private static readonly InputSystemCursor s_arrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private static readonly InputSystemCursor s_iBeamCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    private static readonly InputSystemCursor s_handCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

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

    private static readonly object s_slideBufferLock = new();
    private static readonly Dictionary<int, CanvasBitmap> s_slideBuffer = new();
    private static long s_slideBufferDocId = -1;
    private static int s_slideBufferRotation = -1;
    private static double s_slideBufferPixelScale = 1.0;

    private CancellationTokenSource? _baseCts;
    private CancellationTokenSource? _detailCts;
    private CancellationTokenSource? _detailDebounceCts;
    private CancellationTokenSource? _prefetchCts;
    private DocumentViewModel? _observedDocument;
    private ShellViewModel? _shellViewModel;
    private ScrollViewer? _cachedScrollViewer;
    private int _loadedPageIndex = -1;
    private volatile bool _isUnloaded;
    private bool _isResourcesCreated;
    private bool _needsLevel0Render;

    // Interactive text selection & link hit-testing state (zero idle overhead)
    private InputSystemCursorShape _currentCursorShape = InputSystemCursorShape.Arrow;
    private bool _isPointerDown;
    private bool _hasDraggedSelection;
    private bool _isReleasingCapture;
    private long _selectionSessionId;
    private Point _pressDipPoint;
    private Vector2 _pressPdfPoint;
    private Point _lastHoverDipPoint = new(-1000, -1000);
    private Point _lastDragDipPoint = new(-1000, -1000);
    private Point _pendingHoverDipPoint;
    private Point _pendingDragDipPoint;
    private bool _hasPendingHoverPoint;
    private bool _hasPendingDragPoint;
    private bool _isHoverHitTestInFlight;
    private bool _isDragSelectionInFlight;
    private int _selectionAnchorCharIndex = -1;
    private int _lastDragEndCharIndex = -2;
    private PdfLinkTarget _pressedLink = PdfLinkTarget.None;
    private int _clickCount;
    private long _lastClickTick;
    private Point _lastClickDipPoint = new(-1000, -1000);
    private long _lastDoubleTapTick;
    private long _lastTripleTapTick;

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
                return (_shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>())?.CurrentDocument?.DocumentId ?? 0;
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
                return (_shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>())?.CurrentDocument?.ZoomFactor ?? 1.0;
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
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        _pdfService = App.CurrentApp.Services.GetRequiredService<IPdfService>();
        _tileCache = App.CurrentApp.Services.GetRequiredService<TileCache>();
        _shellViewModel = App.CurrentApp.Services.GetService<ShellViewModel>();

        // Level 0 base canvas keeps DpiScale fixed at 1.0f forever to bound full-page VRAM.
        _baseCanvas = new CanvasControl
        {
            UseSharedDevice = true,
            ClearColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            DpiScale = 1.0f,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
        };

        _baseCanvas.CreateResources += OnCreateResources;
        _baseCanvas.Draw += OnBaseCanvasDraw;

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

        Children.Add(_baseCanvas);
        Children.Add(_detailCanvas);

        PointerPressed += OnCanvasPointerPressed;
        PointerMoved += OnCanvasPointerMoved;
        PointerReleased += OnCanvasPointerReleased;
        PointerCanceled += OnCanvasPointerCanceled;
        PointerCaptureLost += OnCanvasPointerCaptureLost;
        PointerExited += OnCanvasPointerExited;
        DoubleTapped += OnCanvasDoubleTapped;

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
        ResetPointerSelectionState();
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
            var doc = (_shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>())?.CurrentDocument;
            if (!ReferenceEquals(_observedDocument, doc))
            {
                UnsubscribeFromDocument();
                if (doc is not null)
                {
                    _observedDocument = doc;
                    _observedDocument.PropertyChanged += OnObservedDocumentPropertyChanged;
                    _observedDocument.PageOverlaysInvalidated += OnPageOverlaysInvalidated;
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
            doc.PageOverlaysInvalidated -= OnPageOverlaysInvalidated;
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

    private void OnPageOverlaysInvalidated(int targetPageIndex)
    {
        if (PageInfo is not null && (targetPageIndex < 0 || targetPageIndex == PageInfo.Index))
        {
            InvalidateOverlays();
        }
    }

    /// <summary>
    /// Invalidates the Win2D surfaces to redraw GPU search and selection highlights without re-rasterizing the PDF page.
    /// </summary>
    public void InvalidateOverlays()
    {
        if (_isUnloaded || PageInfo is null)
        {
            return;
        }

        _baseCanvas.Invalidate();
        if (_detailCanvas.Visibility == Visibility.Visible)
        {
            _detailCanvas.Invalidate();
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
            canvas.ResetPointerSelectionState();
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
            canvas.ResetPointerSelectionState();
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

    // ------------------------------------------------------------------------------------------
    // Interactive Text Selection & Link Hit-Testing (Mouse / Pen / Multi-Tap in Select Mode)
    // ------------------------------------------------------------------------------------------

    private bool IsSelectInteractionAllowed()
    {
        if (_isUnloaded || PageInfo is null || EffectiveDocumentId <= 0)
        {
            return false;
        }

        ShellViewModel? shell = _shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>();
        if (shell is null || shell.IsPresentationMode)
        {
            return false;
        }

        return shell.ActiveTool == AnnotationTool.Select;
    }

    private void FocusParentViewerIfNeeded()
    {
        try
        {
            var sv = _cachedScrollViewer ??= FindAncestor<ScrollViewer>(this);
            sv?.Focus(FocusState.Programmatic);
        }
        catch
        {
        }
    }

    private void SetCursorShape(InputSystemCursorShape shape)
    {
        if (_currentCursorShape == shape)
        {
            return;
        }

        _currentCursorShape = shape;
        ProtectedCursor = shape switch
        {
            InputSystemCursorShape.IBeam => s_iBeamCursor,
            InputSystemCursorShape.Hand => s_handCursor,
            _ => s_arrowCursor,
        };
    }

    private Vector2 ToPdfPoint(Point pageDipPoint)
    {
        if (PageInfo is null)
        {
            return Vector2.Zero;
        }

        PageCoordinateTransform transform = PageCoordinateTransform.FromPage(PageInfo, RotationAngle);
        return transform.ToPdf(new Vector2((float)pageDipPoint.X, (float)pageDipPoint.Y));
    }

    private void ResetPointerSelectionState()
    {
        _selectionSessionId++;
        _isPointerDown = false;
        _hasDraggedSelection = false;
        _isReleasingCapture = false;
        _selectionAnchorCharIndex = -1;
        _lastDragEndCharIndex = -2;
        _pressedLink = PdfLinkTarget.None;
        _hasPendingHoverPoint = false;
        _hasPendingDragPoint = false;
        SetCursorShape(InputSystemCursorShape.Arrow);
    }

    private bool IsWithinMultiClickDistance(Point p1, Point p2)
    {
        double dx = p1.X - p2.X;
        double dy = p1.Y - p2.Y;
        return ((dx * dx) + (dy * dy)) <= (MultiClickMaxDistanceDip * MultiClickMaxDistanceDip);
    }

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsSelectInteractionAllowed() || PageInfo is null)
        {
            return;
        }

        PointerPoint pt = e.GetCurrentPoint(this);
        bool isTouch = e.Pointer.PointerDeviceType == PointerDeviceType.Touch;

        if (!isTouch && !pt.Properties.IsLeftButtonPressed)
        {
            // Right-click opens the context menu while preserving any existing text selection for "Copy".
            return;
        }

        SubscribeToActiveDocument();
        long now = Environment.TickCount64;
        bool sameSpot = IsWithinMultiClickDistance(pt.Position, _lastClickDipPoint);

        // Detect Triple-Click (3rd click shortly after DoubleTapped or 2 rapid clicks): select full sentence.
        if (sameSpot &&
            ((now - _lastDoubleTapTick <= MultiClickMaxIntervalMs) ||
             (_clickCount >= 2 && now - _lastClickTick <= MultiClickMaxIntervalMs)))
        {
            _clickCount = 0;
            _lastClickTick = now;
            _lastTripleTapTick = now;
            _isPointerDown = false;
            _hasDraggedSelection = false;
            _pressedLink = PdfLinkTarget.None;
            e.Handled = true;

            long sessionId = ++_selectionSessionId;
            Vector2 triplePdfPt = ToPdfPoint(pt.Position);
            _ = SelectSentenceOnTripleClickAsync(EffectiveDocumentId, PageInfo.Index, triplePdfPt, sessionId);
            return;
        }

        if (sameSpot && (now - _lastClickTick <= MultiClickMaxIntervalMs))
        {
            _clickCount++;
        }
        else
        {
            _clickCount = 1;
        }

        _lastClickTick = now;
        _lastClickDipPoint = pt.Position;

        // For Touch contacts, leave single-finger press/move unhandled so ScrollViewer DirectManipulation
        // performs smooth hardware-accelerated pan and pinch-zoom. Double-tap and triple-tap work on Touch too.
        if (isTouch)
        {
            return;
        }

        long dragSession = ++_selectionSessionId;
        _isPointerDown = true;
        _hasDraggedSelection = false;
        _pressDipPoint = pt.Position;
        _pressPdfPoint = ToPdfPoint(pt.Position);
        _lastDragDipPoint = pt.Position;
        _selectionAnchorCharIndex = -1;
        _lastDragEndCharIndex = -2;
        _pressedLink = PdfLinkTarget.None;

        // Capture the pointer immediately on PointerPressed so ScrollViewer does not steal the drag gesture.
        try
        {
            _ = CapturePointer(e.Pointer);
        }
        catch
        {
        }

        e.Handled = true;
        _ = ResolvePressAnchorAsync(EffectiveDocumentId, PageInfo.Index, _pressPdfPoint, dragSession);
    }

    private async Task SelectSentenceOnTripleClickAsync(long docId, int pageIndex, Vector2 pdfPt, long sessionId)
    {
        try
        {
            PdfTextSelection? sentenceSelection = await _pdfService.SelectSentenceAtPointAsync(
                docId,
                pageIndex,
                pdfPt.X,
                pdfPt.Y,
                tolerancePt: 12.0);

            if (!_isUnloaded &&
                _selectionSessionId == sessionId &&
                PageInfo?.Index == pageIndex &&
                EffectiveDocumentId == docId)
            {
                _observedDocument?.SetSelection(sentenceSelection);
                if (sentenceSelection is { HasContent: true })
                {
                    SetCursorShape(InputSystemCursorShape.IBeam);
                    FocusParentViewerIfNeeded();
                }
            }
        }
        catch
        {
        }
    }

    private async Task ResolvePressAnchorAsync(long docId, int pageIndex, Vector2 pdfPt, long sessionId)
    {
        try
        {
            PdfPointerHitResult hit = await _pdfService.HitTestPagePointAsync(
                docId,
                pageIndex,
                pdfPt.X,
                pdfPt.Y,
                tolerancePt: 8.0);

            if (_isUnloaded ||
                _selectionSessionId != sessionId ||
                PageInfo?.Index != pageIndex ||
                EffectiveDocumentId != docId)
            {
                return;
            }

            if (hit.CharIndex >= 0)
            {
                _selectionAnchorCharIndex = hit.CharIndex;
            }

            if (!_hasDraggedSelection && _isPointerDown)
            {
                _pressedLink = hit.Link;
                if (hit.Link.IsValid)
                {
                    SetCursorShape(InputSystemCursorShape.Hand);
                }
                else if (hit.CharIndex >= 0)
                {
                    SetCursorShape(InputSystemCursorShape.IBeam);
                }
            }
            else if (_hasDraggedSelection)
            {
                RequestDragSelectionUpdate(_lastDragDipPoint, force: true, sessionId);
            }
        }
        catch
        {
        }
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsSelectInteractionAllowed() || e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            SetCursorShape(InputSystemCursorShape.Arrow);
            return;
        }

        Point posDip = e.GetCurrentPoint(this).Position;

        if (_isPointerDown)
        {
            double dx = posDip.X - _pressDipPoint.X;
            double dy = posDip.Y - _pressDipPoint.Y;
            if (!_hasDraggedSelection && ((dx * dx) + (dy * dy)) >= (DragStartThresholdDip * DragStartThresholdDip))
            {
                _hasDraggedSelection = true;
                _pressedLink = PdfLinkTarget.None;
            }

            if (_hasDraggedSelection)
            {
                e.Handled = true;
                SetCursorShape(InputSystemCursorShape.IBeam);
                RequestDragSelectionUpdate(posDip, force: false, _selectionSessionId);
            }

            return;
        }

        RequestHoverHitTest(posDip);
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerDown)
        {
            return;
        }

        // Capture local state BEFORE calling ReleasePointerCapture, because ReleasePointerCapture
        // synchronously raises OnCanvasPointerCaptureLost in WinUI 3!
        bool wasDragged = _hasDraggedSelection;
        PdfLinkTarget pressedLink = _pressedLink;
        long activeSession = _selectionSessionId;
        Point releasePosDip = e.GetCurrentPoint(this).Position;

        _isPointerDown = false;
        _pressedLink = PdfLinkTarget.None;

        _isReleasingCapture = true;
        try
        {
            ReleasePointerCapture(e.Pointer);
        }
        catch
        {
        }
        finally
        {
            _isReleasingCapture = false;
        }

        long now = Environment.TickCount64;
        if (now - _lastDoubleTapTick <= MultiClickMaxIntervalMs ||
            now - _lastTripleTapTick <= MultiClickMaxIntervalMs)
        {
            return;
        }

        if (wasDragged)
        {
            // Ensure the final release coordinate is processed and keep the selection active on screen.
            e.Handled = true;
            FocusParentViewerIfNeeded();
            RequestDragSelectionUpdate(releasePosDip, force: true, activeSession);
            return;
        }

        if (pressedLink.IsValid)
        {
            e.Handled = true;
            ShellViewModel? shell = _shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>();
            if (shell is not null)
            {
                _ = shell.HandleLinkClickedAsync(pressedLink);
            }
        }
        else
        {
            _observedDocument?.ClearSelection();
        }
    }

    private void OnCanvasPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isPointerDown = false;
        _pressedLink = PdfLinkTarget.None;
    }

    private void OnCanvasPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isReleasingCapture)
        {
            return;
        }

        // Do NOT clear _hasDraggedSelection or _observedDocument.ActiveSelection here so any selection
        // already made by the user remains intact.
        _isPointerDown = false;
    }

    private void OnCanvasPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerDown)
        {
            SetCursorShape(InputSystemCursorShape.Arrow);
        }
    }

    private async void OnCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!IsSelectInteractionAllowed() || PageInfo is null)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now - _lastTripleTapTick <= MultiClickMaxIntervalMs)
        {
            return;
        }

        _lastDoubleTapTick = now;
        _lastClickTick = now;
        _clickCount = 2;
        _isPointerDown = false;
        _hasDraggedSelection = false;
        _pressedLink = PdfLinkTarget.None;
        e.Handled = true;

        SubscribeToActiveDocument();
        Point posDip = e.GetPosition(this);
        _lastClickDipPoint = posDip;
        Vector2 pdfPt = ToPdfPoint(posDip);
        long docId = EffectiveDocumentId;
        int pageIndex = PageInfo.Index;
        long sessionId = ++_selectionSessionId;

        try
        {
            PdfTextSelection? wordSelection = await _pdfService.SelectWordAtPointAsync(
                docId,
                pageIndex,
                pdfPt.X,
                pdfPt.Y,
                tolerancePt: 10.0);

            if (!_isUnloaded &&
                _selectionSessionId == sessionId &&
                PageInfo?.Index == pageIndex &&
                EffectiveDocumentId == docId)
            {
                _observedDocument?.SetSelection(wordSelection);
                if (wordSelection is { HasContent: true })
                {
                    SetCursorShape(InputSystemCursorShape.IBeam);
                    FocusParentViewerIfNeeded();
                }
            }
        }
        catch
        {
        }
    }

    private void RequestHoverHitTest(Point posDip)
    {
        double dx = posDip.X - _lastHoverDipPoint.X;
        double dy = posDip.Y - _lastHoverDipPoint.Y;
        if (((dx * dx) + (dy * dy)) < (HoverMoveMinDeltaDip * HoverMoveMinDeltaDip))
        {
            return;
        }

        _lastHoverDipPoint = posDip;
        if (_isHoverHitTestInFlight)
        {
            _pendingHoverDipPoint = posDip;
            _hasPendingHoverPoint = true;
            return;
        }

        _ = RunHoverHitTestLoopAsync(posDip);
    }

    private async Task RunHoverHitTestLoopAsync(Point initialPointDip)
    {
        _isHoverHitTestInFlight = true;
        Point currentDip = initialPointDip;

        try
        {
            while (!_isUnloaded && !_isPointerDown && PageInfo is { } pageInfo && EffectiveDocumentId > 0)
            {
                long docId = EffectiveDocumentId;
                int pageIndex = pageInfo.Index;
                Vector2 pdfPt = ToPdfPoint(currentDip);

                PdfPointerHitResult hit = await _pdfService.HitTestPagePointAsync(
                    docId,
                    pageIndex,
                    pdfPt.X,
                    pdfPt.Y,
                    tolerancePt: 4.5);

                if (_isUnloaded || _isPointerDown || PageInfo?.Index != pageIndex || EffectiveDocumentId != docId)
                {
                    break;
                }

                if (hit.Link.IsValid)
                {
                    SetCursorShape(InputSystemCursorShape.Hand);
                }
                else if (hit.CharIndex >= 0)
                {
                    SetCursorShape(InputSystemCursorShape.IBeam);
                }
                else
                {
                    SetCursorShape(InputSystemCursorShape.Arrow);
                }

                if (_hasPendingHoverPoint)
                {
                    currentDip = _pendingHoverDipPoint;
                    _hasPendingHoverPoint = false;
                }
                else
                {
                    break;
                }
            }
        }
        catch
        {
        }
        finally
        {
            _isHoverHitTestInFlight = false;
            _hasPendingHoverPoint = false;
        }
    }

    private void RequestDragSelectionUpdate(Point posDip, bool force, long sessionId)
    {
        double clampedX = Math.Clamp(posDip.X, 0.0, Math.Max(1.0, ActualWidth > 0 ? ActualWidth : Width));
        double clampedY = Math.Clamp(posDip.Y, 0.0, Math.Max(1.0, ActualHeight > 0 ? ActualHeight : Height));
        Point clampedDip = new(clampedX, clampedY);

        if (!force)
        {
            double dx = clampedDip.X - _lastDragDipPoint.X;
            double dy = clampedDip.Y - _lastDragDipPoint.Y;
            if (((dx * dx) + (dy * dy)) < (DragMoveMinDeltaDip * DragMoveMinDeltaDip))
            {
                return;
            }
        }

        _lastDragDipPoint = clampedDip;

        if (_isDragSelectionInFlight)
        {
            _pendingDragDipPoint = clampedDip;
            _hasPendingDragPoint = true;
            return;
        }

        _ = RunDragSelectionLoopAsync(clampedDip, sessionId);
    }

    private async Task RunDragSelectionLoopAsync(Point initialPointDip, long sessionId)
    {
        _isDragSelectionInFlight = true;
        Point currentDip = initialPointDip;

        try
        {
            while (!_isUnloaded &&
                   _selectionSessionId == sessionId &&
                   _hasDraggedSelection &&
                   PageInfo is { } pageInfo &&
                   EffectiveDocumentId > 0)
            {
                long docId = EffectiveDocumentId;
                int pageIndex = pageInfo.Index;
                int anchor = _selectionAnchorCharIndex;
                Vector2 anchorPdf = _pressPdfPoint;
                Vector2 pdfPt = ToPdfPoint(currentDip);

                PdfTextSelection? updated = await _pdfService.SelectTextByPointAsync(
                    docId,
                    pageIndex,
                    anchor,
                    anchorPdf.X,
                    anchorPdf.Y,
                    pdfPt.X,
                    pdfPt.Y,
                    _lastDragEndCharIndex);

                if (_isUnloaded ||
                    _selectionSessionId != sessionId ||
                    PageInfo?.Index != pageIndex ||
                    EffectiveDocumentId != docId)
                {
                    break;
                }

                if (updated is { HasContent: true })
                {
                    if (updated.AnchorCharIndex >= 0)
                    {
                        _selectionAnchorCharIndex = updated.AnchorCharIndex;
                    }

                    _lastDragEndCharIndex = updated.ActiveCharIndex >= 0
                        ? updated.ActiveCharIndex
                        : updated.EndCharIndex;
                    _observedDocument?.SetSelection(updated);
                }

                if (_hasPendingDragPoint && _selectionSessionId == sessionId)
                {
                    currentDip = _pendingDragDipPoint;
                    _hasPendingDragPoint = false;
                }
                else
                {
                    break;
                }
            }
        }
        catch
        {
        }
        finally
        {
            _isDragSelectionInFlight = false;
            _hasPendingDragPoint = false;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Layout & Win2D Rendering (Base + High-Res Detail Patch + GPU Highlights)
    // ------------------------------------------------------------------------------------------

    private void UpdateDimensions()
    {
        if (PageInfo is not null)
        {
            bool swap = (RotationAngle % 180 != 0);
            double defaultW = swap ? PageInfo.DisplayHeightDip : PageInfo.DisplayWidthDip;
            double defaultH = swap ? PageInfo.DisplayWidthDip : PageInfo.DisplayHeightDip;
            double rasterScale = XamlRoot?.RasterizationScale ?? 1.0;
            double effW = Math.Max(1.0, Math.Round(defaultW * rasterScale) / rasterScale);
            double effH = Math.Max(1.0, Math.Round(defaultH * rasterScale) / rasterScale);

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
            ClearSlideBuffer();
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
        if (PageInfo is not { } pageInfo || EffectiveDocumentId <= 0 || _isUnloaded)
        {
            return;
        }

        double rasterScale = XamlRoot?.RasterizationScale ?? 1.0;
        int scalePercent = (int)Math.Round(rasterScale * 100);

        TileKey baseKey = new(
            EffectiveDocumentId,
            pageInfo.Index,
            TileKind.FullPage,
            ZoomStep: 0,
            TileX: 0,
            TileY: 0,
            RenderFlags: (int)(PdfRenderFlags.Annotations | PdfRenderFlags.LcdText),
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
                    }
                    else
                    {
                        cached.Release();
                    }
                }
                catch
                {
                    _tileCache.RemovePage(EffectiveDocumentId, pageInfo.Index);
                    _level0Bitmap = null;
                    cached.Release();
                }
            }
            else
            {
                cached?.Release();
            }
        }

        double activeZoom = EffectiveZoom;
        if (activeZoom > DetailZoomThreshold)
        {
            double desiredPixelScale = Math.Max(1.0, activeZoom * rasterScale);

            if (TryGetSlideFromBuffer(EffectiveDocumentId, pageInfo.Index, RotationAngle, desiredPixelScale, out CanvasBitmap? bufferedSlide) && bufferedSlide is not null)
            {
                _detailBitmap = bufferedSlide;

                bool swap = (RotationAngle % 180 != 0);
                double pageW = Math.Max(1.0, swap ? pageInfo.DisplayHeightDip : pageInfo.DisplayWidthDip);
                double pageH = Math.Max(1.0, swap ? pageInfo.DisplayWidthDip : pageInfo.DisplayHeightDip);

                _detailRectDip = new Rect(0, 0, pageW, pageH);
                _renderedZoom = activeZoom;
                _renderedPixelScale = desiredPixelScale;
                _detailTranslate.X = 0;
                _detailTranslate.Y = 0;
                _detailCanvas.Width = bufferedSlide.Bounds.Width;
                _detailCanvas.Height = bufferedSlide.Bounds.Height;

                float targetDpiScale = (float)Math.Max(1.0, desiredPixelScale / Math.Max(0.1, rasterScale));
                if (Math.Abs(_detailCanvas.DpiScale - targetDpiScale) > 0.001f)
                {
                    _detailCanvas.DpiScale = targetDpiScale;
                }

                _detailCanvas.Opacity = 1.0;
                _detailCanvas.Visibility = Visibility.Visible;
                _detailCanvas.Invalidate();

                PrefetchAdjacentPages(EffectiveDocumentId, pageInfo.Index, RotationAngle, activeZoom, desiredPixelScale, rasterScale);
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
                    Rect bmpBounds = baseBmp.Bounds;
                    double scale = XamlRoot?.RasterizationScale ?? 1.0;
                    double destX = Math.Round(((w - bmpBounds.Width) / 2.0) * scale) / scale;
                    double destY = Math.Round(((h - bmpBounds.Height) / 2.0) * scale) / scale;
                    Rect baseDest = new(destX, destY, bmpBounds.Width, bmpBounds.Height);
                    CanvasImageInterpolation baseInterpolation = (EffectiveZoom >= 0.99 && EffectiveZoom <= 1.01)
                        ? CanvasImageInterpolation.NearestNeighbor
                        : CanvasImageInterpolation.Linear;
                    DrawBitmapWithNightMode(ds, baseBmp, baseDest, bmpBounds, baseInterpolation);
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

        // 4. Draw GPU vector highlights (search occurrences and interactive text selection) in effective Page DIPs.
        DrawTextOverlays(ds, offsetX: 0.0, offsetY: 0.0);
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

        Rect bmpBounds = detailBmp.Bounds;
        Rect destRect = new(0, 0, bmpBounds.Width, bmpBounds.Height);
        try
        {
            DrawBitmapWithNightMode(ds, detailBmp, destRect, bmpBounds, CanvasImageInterpolation.Linear);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or COMException)
        {
            _detailBitmap = null;
            return;
        }

        // Draw GPU vector highlights translated into the high-DPI detail patch coordinate space.
        DrawTextOverlays(ds, offsetX: -_detailRectDip.X, offsetY: -_detailRectDip.Y);
    }

    private void DrawTextOverlays(CanvasDrawingSession ds, double offsetX, double offsetY)
    {
        if (PageInfo is not { } pageInfo)
        {
            return;
        }

        DocumentViewModel? doc = _observedDocument;
        if (doc is null)
        {
            return;
        }

        int pageIndex = pageInfo.Index;
        IReadOnlyList<PdfSearchMatch> pageMatches = doc.GetPageSearchMatches(pageIndex);
        PdfTextSelection? activeSelection = doc.ActiveSelection;
        bool hasSelectionOnPage = activeSelection is { HasContent: true } && activeSelection.PageIndex == pageIndex;

        if (pageMatches.Count == 0 && !hasSelectionOnPage)
        {
            return;
        }

        PageCoordinateTransform transform = PageCoordinateTransform.FromPage(pageInfo, RotationAngle);

        // 1. Search match highlights (translucent yellow for page matches, vibrant orange + border for active match)
        if (pageMatches.Count > 0)
        {
            PdfSearchMatch? activeMatch = doc.ActiveSearchMatch;
            Windows.UI.Color passiveFill = IsNightMode
                ? Windows.UI.Color.FromArgb(120, 255, 215, 0)
                : Windows.UI.Color.FromArgb(115, 255, 230, 0);
            Windows.UI.Color activeFill = Windows.UI.Color.FromArgb(175, 255, 136, 0);
            Windows.UI.Color activeBorder = Windows.UI.Color.FromArgb(240, 220, 80, 0);

            for (int m = 0; m < pageMatches.Count; m++)
            {
                PdfSearchMatch match = pageMatches[m];
                bool isActive = ReferenceEquals(match, activeMatch) ||
                    (activeMatch is not null &&
                     activeMatch.PageIndex == match.PageIndex &&
                     activeMatch.MatchIndexOnPage == match.MatchIndexOnPage);

                IReadOnlyList<PdfRect> rects = match.Rects;
                for (int r = 0; r < rects.Count; r++)
                {
                    Rect dipRect = transform.ToPageRect(rects[r]);
                    if (dipRect.Width <= 0.1 || dipRect.Height <= 0.1)
                    {
                        continue;
                    }

                    // Expand slightly by 1 DIP vertically for comfortable visual breathing room around glyphs.
                    Rect drawRect = new(
                        dipRect.X + offsetX - 0.5,
                        dipRect.Y + offsetY - 1.0,
                        dipRect.Width + 1.0,
                        dipRect.Height + 2.0);

                    if (isActive)
                    {
                        ds.FillRoundedRectangle(drawRect, 2.5f, 2.5f, activeFill);
                        ds.DrawRoundedRectangle(drawRect, 2.5f, 2.5f, activeBorder, 1.25f);
                    }
                    else
                    {
                        ds.FillRoundedRectangle(drawRect, 2.0f, 2.0f, passiveFill);
                    }
                }
            }
        }

        // 2. Interactive text selection highlight (translucent blue with soft rounded corners)
        if (hasSelectionOnPage && activeSelection is not null)
        {
            Windows.UI.Color selectionFill = IsNightMode
                ? Windows.UI.Color.FromArgb(115, 76, 166, 255)
                : Windows.UI.Color.FromArgb(92, 0, 120, 215);

            IReadOnlyList<PdfRect> rects = activeSelection.Rects;
            for (int r = 0; r < rects.Count; r++)
            {
                Rect dipRect = transform.ToPageRect(rects[r]);
                if (dipRect.Width <= 0.1 || dipRect.Height <= 0.1)
                {
                    continue;
                }

                Rect drawRect = new(
                    dipRect.X + offsetX - 0.5,
                    dipRect.Y + offsetY - 0.75,
                    dipRect.Width + 1.0,
                    dipRect.Height + 1.5);

                ds.FillRoundedRectangle(drawRect, 2.5f, 2.5f, selectionFill);
            }
        }
    }

    private void DrawBitmapWithNightMode(
        CanvasDrawingSession ds,
        CanvasBitmap bitmap,
        Rect destRect,
        Rect srcRect,
        CanvasImageInterpolation interpolation = CanvasImageInterpolation.Linear)
    {
        try
        {
            if (IsNightMode)
            {
                using var invert = new InvertEffect { Source = bitmap };
                ds.DrawImage(invert, destRect, invert.GetBounds(ds), 1.0f, interpolation);
            }
            else
            {
                ds.DrawImage(bitmap, destRect, srcRect, 1.0f, interpolation);
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
        _prefetchCts?.Cancel();
        _prefetchCts?.Dispose();
        _prefetchCts = null;

        _detailDebounceCts?.Cancel();
        _detailDebounceCts?.Dispose();
        _detailDebounceCts = null;

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

            if (old is not null && !IsInSlideBuffer(old))
            {
                old.Dispose();
            }
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
            _detailDebounceCts?.Cancel();
            _detailDebounceCts?.Dispose();
            _detailDebounceCts = null;
            return;
        }

        bool shouldDebounce = _detailBitmap is not null && _detailCanvas.Visibility == Visibility.Visible;
        ScheduleDetailPatch(candidatePatchDip, targetPixelScale, currentZoom, rasterScale, shouldDebounce);
    }

    private void ScheduleDetailPatch(Rect candidatePatchDip, double targetPixelScale, double currentZoom, double rasterScale, bool debounce = true)
    {
        _detailDebounceCts?.Cancel();
        _detailDebounceCts?.Dispose();
        _detailDebounceCts = new CancellationTokenSource();
        CancellationToken ct = _detailDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            if (debounce)
            {
                try
                {
                    await Task.Delay(85, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (ct.IsCancellationRequested || _isUnloaded)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (ct.IsCancellationRequested || _isUnloaded || !IsLoaded)
                {
                    return;
                }

                _ = RenderDetailPatchAsync(candidatePatchDip, targetPixelScale, currentZoom, rasterScale);
            });
        }, ct);
    }

    private static (Rect PatchDip, double PixelScale) ComputePatchAndPixelScale(
        Rect visibleDip,
        double pageW,
        double pageH,
        double currentZoom,
        double rasterScale)
    {
        double desiredPixelScale = Math.Max(1.0, currentZoom * rasterScale);

        // Bound the patch in DIPs so that the prefetch margin does not force pixelScale below native resolution on 4K displays.
        double maxAllowedPatchW = Math.Max(visibleDip.Width, MaxDetailPatchDimensionPx / desiredPixelScale);
        double maxAllowedPatchH = Math.Max(visibleDip.Height, MaxDetailPatchDimensionPx / desiredPixelScale);

        // Expand the horizontal prefetch margin generously (up to 75% of viewport width) and vertical margin (up to 40% of viewport height)
        // so that scrolling/panning laterally across high-resolution pages maintains crisp detail without exposing the low-res base bitmap,
        // while strictly bounding within maxAllowedPatch to keep memory controlled and prevent downsampling below native pixel scale.
        double marginX = Math.Min(visibleDip.Width * 0.75, Math.Max(0.0, (maxAllowedPatchW - visibleDip.Width) / 2.0));
        double marginY = Math.Min(visibleDip.Height * 0.40, Math.Max(0.0, (maxAllowedPatchH - visibleDip.Height) / 2.0));

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
            RenderFlags: (int)(PdfRenderFlags.Annotations | PdfRenderFlags.LcdText),
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

                float baseDpi = (float)(96.0 * rasterizationScale);
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
                PdfRenderFlags.Annotations | PdfRenderFlags.LcdText);

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

                Rect patchBmpBounds = newBitmap.Bounds;
                double snapTransX = Math.Round(exactPatchDip.X * activeRasterScale) / activeRasterScale;
                double snapTransY = Math.Round(exactPatchDip.Y * activeRasterScale) / activeRasterScale;
                _detailTranslate.X = snapTransX;
                _detailTranslate.Y = snapTransY;
                _detailCanvas.Width = patchBmpBounds.Width;
                _detailCanvas.Height = patchBmpBounds.Height;

                if (Math.Abs(_detailCanvas.DpiScale - targetDpiScale) > 0.001f)
                {
                    _detailCanvas.DpiScale = targetDpiScale;
                }

                _detailCanvas.Opacity = 1.0;
                _detailCanvas.Visibility = Visibility.Visible;
                _detailCanvas.Invalidate();

                bool isFullPagePatch = Math.Abs(exactPatchDip.X) <= 1.0
                    && Math.Abs(exactPatchDip.Y) <= 1.0
                    && exactPatchDip.Width >= (pageW - 1.0)
                    && exactPatchDip.Height >= (pageH - 1.0);

                ShellViewModel? shell = _shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>();
                DocumentViewModel? doc = _observedDocument ??= shell?.CurrentDocument;
                bool isSlideMode = shell?.IsPresentationMode == true || doc?.IsContinuous == false;

                if (isFullPagePatch && isSlideMode)
                {
                    StoreInSlideBuffer(targetDocId, targetPageIndex, targetPageIndex, targetRotation, pixelScale, newBitmap);
                }

                if (oldDetail is not null && !IsInSlideBuffer(oldDetail))
                {
                    oldDetail.Dispose();
                }

                if (isSlideMode)
                {
                    PrefetchAdjacentPages(targetDocId, targetPageIndex, targetRotation, currentZoom, pixelScale, activeRasterScale);
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
        _prefetchCts?.Cancel();
        _prefetchCts?.Dispose();
        _prefetchCts = null;

        _detailDebounceCts?.Cancel();
        _detailDebounceCts?.Dispose();
        _detailDebounceCts = null;

        _baseCts?.Cancel();
        _baseCts?.Dispose();
        _baseCts = null;

        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;
    }

    private void PrefetchAdjacentPages(
        long targetDocId,
        int currentIdx,
        int targetRotation,
        double currentZoom,
        double pixelScale,
        double activeRasterScale)
    {
        ShellViewModel? shell = _shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>();
        DocumentViewModel? doc = _observedDocument ??= shell?.CurrentDocument;
        if (doc is null || doc.DocumentId != targetDocId || doc.Pages.Count <= 1 || _isUnloaded)
        {
            return;
        }

        bool isPresentation = shell?.IsPresentationMode == true;
        bool isSinglePage = !doc.IsContinuous;
        if (!isPresentation && !isSinglePage)
        {
            return;
        }

        _prefetchCts?.Cancel();
        _prefetchCts?.Dispose();
        _prefetchCts = new CancellationTokenSource();
        CancellationToken ct = _prefetchCts.Token;

        int totalPages = doc.Pages.Count;
        int zoomStep = Math.Max(1, ZoomQuantizer.ToStep(pixelScale));
        int scalePercent = (int)Math.Round(activeRasterScale * 100);

        List<int> pagesToPrefetch = new(2);
        if (currentIdx + 1 < totalPages)
        {
            lock (s_slideBufferLock)
            {
                if (!s_slideBuffer.ContainsKey(currentIdx + 1))
                {
                    pagesToPrefetch.Add(currentIdx + 1);
                }
            }
        }

        if (currentIdx - 1 >= 0)
        {
            lock (s_slideBufferLock)
            {
                if (!s_slideBuffer.ContainsKey(currentIdx - 1))
                {
                    pagesToPrefetch.Add(currentIdx - 1);
                }
            }
        }

        if (pagesToPrefetch.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (int pageIndex in pagesToPrefetch)
            {
                if (ct.IsCancellationRequested || _isUnloaded)
                {
                    break;
                }

                lock (s_slideBufferLock)
                {
                    if (s_slideBuffer.ContainsKey(pageIndex))
                    {
                        continue;
                    }
                }

                if (pageIndex >= doc.Pages.Count)
                {
                    continue;
                }

                PdfPageInfo? candidateInfo = doc.Pages[pageIndex];
                if (candidateInfo is null)
                {
                    continue;
                }

                bool swap = (targetRotation % 180 != 0);
                double pW = Math.Max(1.0, swap ? candidateInfo.DisplayHeightDip : candidateInfo.DisplayWidthDip);
                double pH = Math.Max(1.0, swap ? candidateInfo.DisplayWidthDip : candidateInfo.DisplayHeightDip);
                int fullW = Math.Max(1, (int)Math.Round(pW * pixelScale));
                int fullH = Math.Max(1, (int)Math.Round(pH * pixelScale));

                PdfRenderRequest request = new(
                    targetDocId,
                    pageIndex,
                    fullW,
                    fullH,
                    fullW,
                    fullH,
                    0,
                    0,
                    targetRotation,
                    PdfRenderFlags.Annotations | PdfRenderFlags.LcdText);

                PdfBitmapLease lease;
                try
                {
                    lease = await _pdfService.RenderAsync(request, PdfJobPriority.Prefetch, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Prefetch render failed for page {pageIndex}: {ex.Message}");
                    continue;
                }

                if (ct.IsCancellationRequested || _isUnloaded)
                {
                    lease.Dispose();
                    break;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (ct.IsCancellationRequested || _isUnloaded)
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

                        float targetDpiScale = (float)Math.Max(1.0, pixelScale / Math.Max(0.1, activeRasterScale));
                        float targetDpi = (float)(96.0 * activeRasterScale * targetDpiScale);

                        CanvasBitmap? prefetchedBitmap = CreateCanvasBitmapFromLease(resourceCreator, lease, targetDpi);
                        if (prefetchedBitmap is not null)
                        {
                            StoreInSlideBuffer(targetDocId, currentIdx, pageIndex, targetRotation, pixelScale, prefetchedBitmap);
                        }
                    }
                    catch (Exception ex)
                    {
                        lease.Dispose();
                        Debug.WriteLine($"Prefetch upload failed for page {pageIndex}: {ex.Message}");
                    }
                });
            }
        }, ct);
    }

    [ThreadStatic]
    private static Windows.Storage.Streams.Buffer? s_reusableBuffer;

    /// <summary>
    /// Explicitly releases the thread-static staging buffer to reclaim unmanaged memory.
    /// </summary>
    public static void ReleaseReusableBuffer()
    {
        s_reusableBuffer = null;
    }

    /// <summary>
    /// Explicitly disposes all pre-rendered slide bitmaps and resets the presentation slide buffer.
    /// Call when exiting presentation mode or closing the document.
    /// </summary>
    public static void ClearSlideBuffer()
    {
        lock (s_slideBufferLock)
        {
            foreach (var kvp in s_slideBuffer)
            {
                try
                {
                    kvp.Value?.Dispose();
                }
                catch
                {
                }
            }
            s_slideBuffer.Clear();
            s_slideBufferDocId = -1;
            s_slideBufferRotation = -1;
            s_slideBufferPixelScale = 1.0;
        }
    }

    private static bool IsInSlideBuffer(CanvasBitmap? bitmap)
    {
        if (bitmap is null)
        {
            return false;
        }

        lock (s_slideBufferLock)
        {
            foreach (var bmp in s_slideBuffer.Values)
            {
                if (ReferenceEquals(bmp, bitmap))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void StoreInSlideBuffer(long docId, int activePageIndex, int storedPageIndex, int rotation, double pixelScale, CanvasBitmap bitmap)
    {
        lock (s_slideBufferLock)
        {
            if (s_slideBufferDocId != docId || s_slideBufferRotation != rotation || Math.Abs(s_slideBufferPixelScale - pixelScale) > 0.1)
            {
                foreach (var kvp in s_slideBuffer)
                {
                    try { kvp.Value?.Dispose(); } catch { }
                }
                s_slideBuffer.Clear();
                s_slideBufferDocId = docId;
                s_slideBufferRotation = rotation;
                s_slideBufferPixelScale = pixelScale;
            }

            if (s_slideBuffer.TryGetValue(storedPageIndex, out CanvasBitmap? existing))
            {
                if (!ReferenceEquals(existing, bitmap))
                {
                    try { existing.Dispose(); } catch { }
                }
            }

            s_slideBuffer[storedPageIndex] = bitmap;
            TrimSlideBufferLocked(activePageIndex);
        }
    }

    private static void TrimSlideBufferLocked(int activePageIndex)
    {
        List<int>? toRemove = null;
        foreach (var key in s_slideBuffer.Keys)
        {
            if (key < activePageIndex - 1 || key > activePageIndex + 1)
            {
                toRemove ??= new List<int>();
                toRemove.Add(key);
            }
        }

        if (toRemove is not null)
        {
            foreach (int key in toRemove)
            {
                if (s_slideBuffer.Remove(key, out CanvasBitmap? deadBmp))
                {
                    try { deadBmp?.Dispose(); } catch { }
                }
            }
        }
    }

    private static bool TryGetSlideFromBuffer(long docId, int pageIndex, int rotation, double pixelScale, out CanvasBitmap? bitmap)
    {
        lock (s_slideBufferLock)
        {
            if (s_slideBufferDocId == docId &&
                s_slideBufferRotation == rotation &&
                Math.Abs(s_slideBufferPixelScale - pixelScale) <= 0.1 &&
                s_slideBuffer.TryGetValue(pageIndex, out CanvasBitmap? candidate))
            {
                try
                {
                    if (candidate.Device is not null)
                    {
                        bitmap = candidate;
                        return true;
                    }
                }
                catch
                {
                }

                s_slideBuffer.Remove(pageIndex);
                try { candidate?.Dispose(); } catch { }
            }

            bitmap = null;
            return false;
        }
    }

    private static Windows.Storage.Streams.Buffer GetOrCreateReusableBuffer(int requiredCapacity)
    {
        if (s_reusableBuffer is null || s_reusableBuffer.Capacity < requiredCapacity)
        {
            uint newCap = (uint)Math.Max(requiredCapacity, (int)((s_reusableBuffer?.Capacity ?? 0) * 1.25));
            s_reusableBuffer = new Windows.Storage.Streams.Buffer(newCap);
        }

        s_reusableBuffer.Length = (uint)requiredCapacity;
        return s_reusableBuffer;
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
            Windows.Storage.Streams.IBuffer nativeBuffer = GetOrCreateReusableBuffer(byteLength);

            if ((object)nativeBuffer is ICanvasBufferByteAccess byteAccess)
            {
                byte* dst = byteAccess.Buffer;
                byte* src = (byte*)lease.Scan0;
                System.Buffer.MemoryCopy(src, dst, byteLength, byteLength);
            }
            else
            {
                using var stream = nativeBuffer.AsStream();
                stream.Position = 0;
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

        if (bd is not null && !IsInSlideBuffer(bd))
        {
            bd.Dispose();
        }
    }
}
