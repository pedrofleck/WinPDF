using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinPDF.Controls;
using WinPDF.Helpers;
using WinPDF.Models;
using WinPDF.Rendering;
using WinPDF.ViewModels;

namespace WinPDF.Views;

public sealed partial class DocumentView
{
    private const double TouchTapZoomMultiplier = 2.4;
    private const long TwoFingerTapMaxDurationMs = 300;
    private const double TwoFingerTapMaxMoveDip = 22.0;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct WIN32_POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public WIN32_POINT ptPixelLocation;
        public WIN32_POINT ptHimetricLocation;
        public WIN32_POINT ptPixelLocationRaw;
        public WIN32_POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerInfo(uint pointerId, out POINTER_INFO pointerInfo);

    private readonly Dictionary<uint, (long PressTick, Point PressPos)> _activeTouchPointers = new();
    private bool _isTouchDoubleTapZoomedIn;
    private double _zoomBeforeTouchDoubleTap = 1.0;
    private bool _wasFittedBeforeTouchDoubleTap;
    private bool _restoreFitAfterSmoothZoom;
    private bool _isTouchToggleZoomAnimation;

    private PdfPageCanvas? _activeTouchSelectingCanvas;
    private PdfPageCanvas? _activeTouchShapeCanvas;
    private uint _touchShapePointerId;
    private static readonly InputSystemCursor s_shapeOverlayCrossCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    private readonly Dictionary<uint, Point> _shapeOverlayTouchPointers = new();
    private bool _isShapeOverlayTouchDrawing;
    private uint _shapeOverlayDrawPointerId;
    private PdfPageCanvas? _shapeOverlayDrawCanvas;
    private PdfPageCanvas? _shapeOverlayMouseCanvas;
    private bool _isShapeOverlayTwoFingerNavigating;
    private Point _shapeOverlayPrevMidpoint;
    private double _shapeOverlayPrevSpan;
    private bool _isReleasingShapeOverlayCapture;
    private PdfPageCanvas? _activeTouchDraggingAnnotCanvas;
    private uint _touchDragAnnotPointerId;
    private uint _touchAnnotPanPointerId;
    private WIN32_POINT _touchAnnotPanLastScreenPx;
    private bool _hasTouchAnnotPanLastScreenPx;
    private bool _isTouchAnnotPanPollingSubscribed;
    private Point _touchAnnotPanLastRootPos;
    private bool _hasTouchAnnotPanLastRootPos;
    private bool _isTouchSelectionFlyoutOpen;
    private bool _isLastPointerInputTouch;
    private long _lastTouchSelectionCompletedTick;
    private bool _isTouchSelectionHandlesActive;
    private bool _isDraggingSelectionHandle;
    private bool _isDraggingStartSelectionHandle;
    private uint _handleDragPointerId;
    private bool _isDraggingAnnotationMoveHandle;
    private uint _annotMoveHandlePointerId;
    private PdfPageCanvas? _annotMoveTargetCanvas;
    private Point _touchMoveLastRootPos;
    private bool _hasTouchMoveHandleMoved;
    private bool _isDraggingAnnotationResizeHandle;
    private uint _annotResizeHandlePointerId;
    private UIElement? _annotResizeHandleElement;
    private PdfPageCanvas? _annotResizeTargetCanvas;
    private Point _touchResizeLastRootPos;
    private bool _isReleasingOverlayCapture;
    private int _touchQuickBarBoundAnnotPage = -1;
    private int _touchQuickBarBoundAnnotIndex = -1;
    private PdfPageCanvas? _handleDragTargetCanvas;
    private UIElement? _handleDragElement;
    private Point _handleGrabOffsetRootDip;
    private Point _lastHandleTargetRootDip;

    // Stage 3: Touch-Exclusive Assisted Vertical Scroll Scrubber state
    private const double TouchScrollTrackTopMarginDip = 16.0;
    private const double TouchScrollTrackBottomMarginDip = 16.0;
    private const double TouchScrollThumbHeightDip = 64.0;
    private readonly DispatcherTimer _touchScrollHideTimer = new() { Interval = TimeSpan.FromMilliseconds(2200) };
    private readonly DispatcherTimer _touchScrollCollapseTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private bool _isTouchScrollScrubberVisible;
    private bool _isDraggingTouchScrollScrubber;
    private uint _touchScrollScrubberPointerId;
    private double _touchScrollGrabOffsetY = 32.0;
    private long _lastScrubberScrollUpdateTick;
    private double _lastScrubberTargetVOffset = -1.0;

    private bool _isTouchEmptyHoldFlyoutOpen;
    private PdfPageCanvas? _touchEmptyHoldCanvas;
    private Vector2 _touchEmptyHoldPdfPt;
    private Point _touchEmptyHoldDipPt;

    private void ToggleSmoothTouchZoom(Point pivotInViewport)
    {
        if (ViewModel?.CurrentDocument is not { Pages.Count: > 0 } doc || ViewModel.IsPresentationMode)
        {
            return;
        }

        _viewportDebounceTimer.Stop();
        if (!_isSmoothZoomRenderingSubscribed)
        {
            double liveZoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor);
            SetInteractiveViewportState(liveZoom, ContinuousScrollViewer.HorizontalOffset, ContinuousScrollViewer.VerticalOffset);
        }
        else
        {
            EnsureInteractiveViewportStateInitialized(doc);
        }

        double currentZoom = _interactiveZoomFactor;
        double targetZoom;

        if (_isTouchDoubleTapZoomedIn)
        {
            _isTouchDoubleTapZoomedIn = false;
            _restoreFitAfterSmoothZoom = _wasFittedBeforeTouchDoubleTap && !doc.IsContinuous;
            if (_restoreFitAfterSmoothZoom)
            {
                targetZoom = CalculateFitVerticalZoom(doc);
            }
            else
            {
                targetZoom = Math.Clamp(
                    _zoomBeforeTouchDoubleTap > 0.05 ? _zoomBeforeTouchDoubleTap : 1.0,
                    DocumentViewModel.MinZoom,
                    DocumentViewModel.MaxZoom);
                if (Math.Abs(targetZoom - currentZoom) < 0.05)
                {
                    targetZoom = Math.Clamp(currentZoom / TouchTapZoomMultiplier, DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);
                }
            }
        }
        else
        {
            _zoomBeforeTouchDoubleTap = currentZoom;
            _wasFittedBeforeTouchDoubleTap = _isFittedVertically;
            _isTouchDoubleTapZoomedIn = true;
            _restoreFitAfterSmoothZoom = false;
            targetZoom = Math.Clamp(
                Math.Max(currentZoom * TouchTapZoomMultiplier, currentZoom + 1.10),
                DocumentViewModel.MinZoom,
                DocumentViewModel.MaxZoom);
        }

        EnsureScrollModesEnabledForViewChange();

        double vw = ContinuousScrollViewer.ViewportWidth > 0 ? ContinuousScrollViewer.ViewportWidth : ContinuousScrollViewer.ActualWidth;
        double vh = ContinuousScrollViewer.ViewportHeight > 0 ? ContinuousScrollViewer.ViewportHeight : ContinuousScrollViewer.ActualHeight;
        double pivotX = vw > 0 ? Math.Clamp(pivotInViewport.X, 0.0, vw) : 0.0;
        double pivotY = vh > 0 ? Math.Clamp(pivotInViewport.Y, 0.0, vh) : 0.0;

        double unzoomedW = ScrollViewerInnerGrid is { ActualWidth: > 1.0 }
            ? ScrollViewerInnerGrid.ActualWidth
            : (ContinuousScrollViewer.ExtentWidth > 1.0 && currentZoom > 0.05
                ? ContinuousScrollViewer.ExtentWidth / currentZoom
                : GetUnzoomedScrollContentWidthDip(doc, currentZoom, vw));
        double unzoomedH = ScrollViewerInnerGrid is { ActualHeight: > 1.0 }
            ? ScrollViewerInnerGrid.ActualHeight
            : (ContinuousScrollViewer.ExtentHeight > 1.0 && currentZoom > 0.05
                ? ContinuousScrollViewer.ExtentHeight / currentZoom
                : GetUnzoomedScrollContentHeightDip(doc, currentZoom, vh));

        double docOffsetDip = GetTargetDocumentOffsetDip(currentZoom);
        double oldPadX = Math.Max(0.0, (vw - (unzoomedW * currentZoom)) * 0.5);
        double oldPadY = Math.Max(0.0, (vh - (unzoomedH * currentZoom)) * 0.5);

        _smoothZoomStartFactor = currentZoom;
        _smoothZoomCurrentFactor = currentZoom;
        _smoothZoomTargetFactor = targetZoom;
        _smoothZoomStartOffsetX = _interactiveOffsetX;
        _smoothZoomStartOffsetY = _interactiveOffsetY;
        _smoothZoomUnzoomedW = unzoomedW;
        _smoothZoomUnzoomedH = unzoomedH;
        _smoothZoomViewportW = vw;
        _smoothZoomViewportH = vh;
        _smoothZoomDocOffsetDip = docOffsetDip;
        _smoothZoomPivotPoint = new Point(pivotX, pivotY);
        _smoothZoomContentBaseX = (_smoothZoomStartOffsetX + pivotX - oldPadX - docOffsetDip) / currentZoom;
        _smoothZoomContentBaseY = (_smoothZoomStartOffsetY + pivotY - oldPadY) / currentZoom;

        ComputePivotLockedZoomOffsets(targetZoom, out _smoothZoomTargetOffsetX, out _smoothZoomTargetOffsetY);
        if (_restoreFitAfterSmoothZoom && !doc.IsContinuous)
        {
            if (unzoomedW * targetZoom <= vw + 1.0)
            {
                _smoothZoomTargetOffsetX = 0.0;
            }
            if (unzoomedH * targetZoom <= vh + 1.0)
            {
                _smoothZoomTargetOffsetY = 0.0;
            }
        }

        _isTouchToggleZoomAnimation = true;
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _smoothZoomAnimStartTicks = nowTicks;
        _smoothZoomAnimDurationSec = 0.25;
        _lastSmoothZoomRenderTicks = nowTicks;

        if (!_isSmoothZoomRenderingSubscribed)
        {
            CompositionTarget.Rendering += OnSmoothZoomRendering;
            _isSmoothZoomRenderingSubscribed = true;
        }
    }

    private void OnCanvasTouchDoubleTapZoomRequested(PdfPageCanvas canvas, Point tapPosInCanvas)
    {
        if (ContinuousScrollViewer is null || ViewModel?.IsPresentationMode == true)
        {
            return;
        }

        try
        {
            Point pivotInViewport = canvas.TransformToVisual(ContinuousScrollViewer).TransformPoint(tapPosInCanvas);
            ToggleSmoothTouchZoom(pivotInViewport);
        }
        catch
        {
        }
    }

    private void OnCanvasTouchShapeDrawStarted(PdfPageCanvas canvas, uint touchPointerId)
    {
        _isLastPointerInputTouch = true;
        _activeTouchShapeCanvas = canvas;
        _touchShapePointerId = touchPointerId;
        _isTouchSwipeTracking = false;
        _isTouchSwipeEngaged = false;
        if (_isSlideSwipeActive || _isSlideSwipeAnimating)
        {
            CancelInteractiveSlideSwipeImmediate();
        }

        HideAnnotationPopupsImmediate();
        LockScrollViewerForTouchAnnotationDrag();
        try
        {
            _ = ContinuousScrollViewer?.CancelDirectManipulations();
        }
        catch
        {
        }
    }

    private void OnCanvasTouchShapeDrawEnded(PdfPageCanvas canvas)
    {
        if (ReferenceEquals(_activeTouchShapeCanvas, canvas))
        {
            _activeTouchShapeCanvas = null;
            _touchShapePointerId = 0;
        }

        _isTouchSwipeTracking = false;
        _isTouchSwipeEngaged = false;
        SyncPagedScrollViewerModes();
    }

    private void SyncTouchShapeDrawOverlayVisibility()
    {
        if (TouchShapeDrawOverlay is null)
        {
            return;
        }

        bool shouldShow = ViewModel?.ActiveTool == AnnotationTool.Shape &&
                          ViewModel.IsPresentationMode != true &&
                          !_isInlineTextEditorOpen &&
                          ViewModel.CurrentDocument is { Pages.Count: > 0 };

        if (shouldShow)
        {
            if (TouchShapeDrawOverlay.Visibility != Visibility.Visible)
            {
                TouchShapeDrawOverlay.Visibility = Visibility.Visible;
            }
        }
        else
        {
            ResetTouchShapeOverlayState();
            if (TouchShapeDrawOverlay.Visibility != Visibility.Collapsed)
            {
                TouchShapeDrawOverlay.Visibility = Visibility.Collapsed;
            }

            if (ProtectedCursor is not null)
            {
                ProtectedCursor = null;
            }
        }
    }

    private void ResetTouchShapeOverlayState()
    {
        _shapeOverlayDrawCanvas?.CancelShapeDrawing();
        _shapeOverlayMouseCanvas?.CancelShapeDrawing();
        _isShapeOverlayTouchDrawing = false;
        _shapeOverlayDrawPointerId = 0;
        _shapeOverlayDrawCanvas = null;
        _shapeOverlayMouseCanvas = null;
        _isShapeOverlayTwoFingerNavigating = false;
        _shapeOverlayTouchPointers.Clear();
    }

    private bool TryFindRealizedCanvasAtOverlayPoint(Point overlayPt, out PdfPageCanvas? targetCanvas, out Point localPt)
    {
        targetCanvas = null;
        localPt = default;
        if (TouchShapeDrawOverlay is null)
        {
            return false;
        }

        PdfPageCanvas? bestNearest = null;
        Point bestLocal = default;
        double bestDistSq = double.MaxValue;

        foreach (PdfPageCanvas canvas in _activeCanvases)
        {
            if (canvas.IsSwipePreview || !canvas.IsLoaded || canvas.PageInfo is null)
            {
                continue;
            }

            double cw = canvas.ActualWidth > 1.0 ? canvas.ActualWidth : canvas.Width;
            double ch = canvas.ActualHeight > 1.0 ? canvas.ActualHeight : canvas.Height;
            if (cw <= 1.0 || ch <= 1.0)
            {
                continue;
            }

            try
            {
                Point pt = TouchShapeDrawOverlay.TransformToVisual(canvas).TransformPoint(overlayPt);
                if (pt.X >= 0.0 && pt.X <= cw && pt.Y >= 0.0 && pt.Y <= ch)
                {
                    targetCanvas = canvas;
                    localPt = pt;
                    return true;
                }

                double cx = Math.Clamp(pt.X, 0.0, cw);
                double cy = Math.Clamp(pt.Y, 0.0, ch);
                double dx = pt.X - cx;
                double dy = pt.Y - cy;
                double d2 = (dx * dx) + (dy * dy);
                if (d2 <= (24.0 * 24.0) && d2 < bestDistSq)
                {
                    bestDistSq = d2;
                    bestNearest = canvas;
                    bestLocal = new Point(cx, cy);
                }
            }
            catch
            {
            }
        }

        if (bestNearest is not null)
        {
            targetCanvas = bestNearest;
            localPt = bestLocal;
            return true;
        }

        return false;
    }

    private void CommitShapeOverlayTwoFingerNavigation()
    {
        if (!_isShapeOverlayTwoFingerNavigating)
        {
            return;
        }

        _isShapeOverlayTwoFingerNavigating = false;
        if (ViewModel?.CurrentDocument is { } doc && ContinuousScrollViewer is not null)
        {
            FlushInteractiveViewportNow();
            double finalZoom = _hasInteractiveViewportState && _interactiveZoomFactor > 0.05
                ? _interactiveZoomFactor
                : ContinuousScrollViewer.ZoomFactor;
            if (finalZoom > 0.05)
            {
                if (Math.Abs(doc.ZoomFactor - finalZoom) > 0.002)
                {
                    ViewModel.SetZoom((float)finalZoom);
                }

                UpdateViewportPages(finalZoom);
                ScheduleViewportUpdate();
            }
        }
    }

    private void OnShapeOverlayPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
        {
            ProtectedCursor = s_shapeOverlayCrossCursor;
        }
    }

    private void OnShapeOverlayPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isShapeOverlayTouchDrawing && ProtectedCursor is not null)
        {
            ProtectedCursor = null;
        }
    }

    private void OnShapeOverlayPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (TouchShapeDrawOverlay is null || ViewModel?.ActiveTool != AnnotationTool.Shape)
        {
            return;
        }

        PointerPoint pt = e.GetCurrentPoint(TouchShapeDrawOverlay);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            _isLastPointerInputTouch = true;
            HideAnnotationPopupsImmediate();
            if (_isSlideSwipeActive || _isSlideSwipeAnimating)
            {
                CancelInteractiveSlideSwipeImmediate();
            }

            uint pid = e.Pointer.PointerId;
            _shapeOverlayTouchPointers[pid] = pt.Position;

            try
            {
                _ = TouchShapeDrawOverlay.CapturePointer(e.Pointer);
            }
            catch
            {
            }

            if (_shapeOverlayTouchPointers.Count == 1)
            {
                if (TryFindRealizedCanvasAtOverlayPoint(pt.Position, out PdfPageCanvas? targetCanvas, out Point localPt) &&
                    targetCanvas is not null)
                {
                    targetCanvas.BeginExternalTouchShapeDraw(ViewModel.ActiveShapeKind, localPt);
                    _isShapeOverlayTouchDrawing = true;
                    _shapeOverlayDrawPointerId = pid;
                    _shapeOverlayDrawCanvas = targetCanvas;
                }
            }
            else
            {
                if (_isShapeOverlayTouchDrawing || _shapeOverlayDrawCanvas is not null)
                {
                    _shapeOverlayDrawCanvas?.CancelShapeDrawing();
                    _isShapeOverlayTouchDrawing = false;
                    _shapeOverlayDrawPointerId = 0;
                    _shapeOverlayDrawCanvas = null;
                }

                if (_shapeOverlayTouchPointers.Count == 2 && ViewModel.CurrentDocument is { } doc)
                {
                    using var it = _shapeOverlayTouchPointers.Values.GetEnumerator();
                    it.MoveNext();
                    Point p1 = it.Current;
                    it.MoveNext();
                    Point p2 = it.Current;
                    _shapeOverlayPrevMidpoint = new Point((p1.X + p2.X) * 0.5, (p1.Y + p2.Y) * 0.5);
                    _shapeOverlayPrevSpan = Math.Max(10.0, double.Hypot(p2.X - p1.X, p2.Y - p1.Y));
                    _isShapeOverlayTwoFingerNavigating = true;
                    EnsureInteractiveViewportStateInitialized(doc);
                }
            }

            e.Handled = true;
            return;
        }

        _isLastPointerInputTouch = false;
        ProtectedCursor = s_shapeOverlayCrossCursor;

        if (pt.Properties.IsRightButtonPressed)
        {
            if (_shapeOverlayMouseCanvas is not null)
            {
                _shapeOverlayMouseCanvas.CancelShapeDrawing();
                _shapeOverlayMouseCanvas = null;
            }

            e.Handled = true;
            return;
        }

        if (pt.Properties.IsLeftButtonPressed)
        {
            HideAnnotationPopupsImmediate();
            if (_shapeOverlayMouseCanvas is { IsDrawingShape: true } activeCanvas)
            {
                try
                {
                    Point ptInCanvas = TouchShapeDrawOverlay.TransformToVisual(activeCanvas).TransformPoint(pt.Position);
                    bool stillDrawing = activeCanvas.HandleExternalMouseShapeClick(ViewModel.ActiveShapeKind, ptInCanvas);
                    if (!stillDrawing)
                    {
                        _shapeOverlayMouseCanvas = null;
                    }
                }
                catch
                {
                    _shapeOverlayMouseCanvas = null;
                }
            }
            else if (TryFindRealizedCanvasAtOverlayPoint(pt.Position, out PdfPageCanvas? targetCanvas, out Point localPt) &&
                     targetCanvas is not null)
            {
                bool started = targetCanvas.HandleExternalMouseShapeClick(ViewModel.ActiveShapeKind, localPt);
                _shapeOverlayMouseCanvas = started ? targetCanvas : null;
            }

            e.Handled = true;
        }
    }

    private void OnShapeOverlayPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (TouchShapeDrawOverlay is null || ViewModel?.ActiveTool != AnnotationTool.Shape)
        {
            return;
        }

        PointerPoint pt = e.GetCurrentPoint(TouchShapeDrawOverlay);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            uint pid = e.Pointer.PointerId;
            if (_shapeOverlayTouchPointers.ContainsKey(pid))
            {
                _shapeOverlayTouchPointers[pid] = pt.Position;
            }

            if (_isShapeOverlayTwoFingerNavigating && _shapeOverlayTouchPointers.Count >= 2 && ContinuousScrollViewer is not null)
            {
                using var it = _shapeOverlayTouchPointers.Values.GetEnumerator();
                it.MoveNext();
                Point p1 = it.Current;
                it.MoveNext();
                Point p2 = it.Current;
                Point mid = new((p1.X + p2.X) * 0.5, (p1.Y + p2.Y) * 0.5);
                double span = Math.Max(10.0, double.Hypot(p2.X - p1.X, p2.Y - p1.Y));
                double dx = mid.X - _shapeOverlayPrevMidpoint.X;
                double dy = mid.Y - _shapeOverlayPrevMidpoint.Y;
                double scaleDelta = _shapeOverlayPrevSpan > 1.0 ? Math.Clamp(span / _shapeOverlayPrevSpan, 0.75, 1.35) : 1.0;
                _shapeOverlayPrevMidpoint = mid;
                _shapeOverlayPrevSpan = span;

                try
                {
                    Point midInSv = TouchShapeDrawOverlay.TransformToVisual(ContinuousScrollViewer).TransformPoint(mid);
                    ApplyInteractiveZoomAroundPoint(midInSv, scaleDelta, dx, dy, commitZoomProperty: false);
                }
                catch
                {
                }
            }
            else if (_isShapeOverlayTouchDrawing && pid == _shapeOverlayDrawPointerId && _shapeOverlayDrawCanvas is { } canvas)
            {
                try
                {
                    Point ptInCanvas = TouchShapeDrawOverlay.TransformToVisual(canvas).TransformPoint(pt.Position);
                    canvas.UpdateTouchShapeDrawPoint(ptInCanvas);
                }
                catch
                {
                }
            }

            e.Handled = true;
            return;
        }

        if (_shapeOverlayMouseCanvas is { IsDrawingShape: true } activeCanvas)
        {
            try
            {
                Point ptInCanvas = TouchShapeDrawOverlay.TransformToVisual(activeCanvas).TransformPoint(pt.Position);
                activeCanvas.UpdateExternalMouseShapePreview(ptInCanvas);
            }
            catch
            {
            }

            e.Handled = true;
        }
    }

    private void OnShapeOverlayPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (TouchShapeDrawOverlay is null)
        {
            return;
        }

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            _isReleasingShapeOverlayCapture = true;
            try
            {
                TouchShapeDrawOverlay.ReleasePointerCapture(e.Pointer);
            }
            catch
            {
            }
            finally
            {
                _isReleasingShapeOverlayCapture = false;
            }

            uint pid = e.Pointer.PointerId;
            _shapeOverlayTouchPointers.Remove(pid);

            if (_isShapeOverlayTouchDrawing && pid == _shapeOverlayDrawPointerId)
            {
                PdfPageCanvas? canvas = _shapeOverlayDrawCanvas;
                _isShapeOverlayTouchDrawing = false;
                _shapeOverlayDrawPointerId = 0;
                _shapeOverlayDrawCanvas = null;

                if (canvas is not null)
                {
                    try
                    {
                        Point ptInCanvas = e.GetCurrentPoint(canvas).Position;
                        canvas.CompleteExternalTouchShapeDraw(ptInCanvas, commitIfValid: true);
                    }
                    catch
                    {
                        canvas.CompleteExternalTouchShapeDraw(null, commitIfValid: true);
                    }
                }
            }
            else if (_isShapeOverlayTwoFingerNavigating && _shapeOverlayTouchPointers.Count < 2)
            {
                CommitShapeOverlayTwoFingerNavigation();
            }

            e.Handled = true;
        }
    }

    private void OnShapeOverlayPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_isReleasingShapeOverlayCapture || e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
        {
            return;
        }

        uint pid = e.Pointer.PointerId;
        _shapeOverlayTouchPointers.Remove(pid);

        if (_isShapeOverlayTouchDrawing && pid == _shapeOverlayDrawPointerId)
        {
            PdfPageCanvas? canvas = _shapeOverlayDrawCanvas;
            _isShapeOverlayTouchDrawing = false;
            _shapeOverlayDrawPointerId = 0;
            _shapeOverlayDrawCanvas = null;
            canvas?.CompleteExternalTouchShapeDraw(null, commitIfValid: true);
        }
        else if (_isShapeOverlayTwoFingerNavigating && _shapeOverlayTouchPointers.Count < 2)
        {
            CommitShapeOverlayTwoFingerNavigation();
        }
    }

    private void OnShapeOverlayPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isReleasingShapeOverlayCapture || e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
        {
            return;
        }

        uint pid = e.Pointer.PointerId;
        _shapeOverlayTouchPointers.Remove(pid);

        if (_isShapeOverlayTouchDrawing && pid == _shapeOverlayDrawPointerId)
        {
            PdfPageCanvas? canvas = _shapeOverlayDrawCanvas;
            _isShapeOverlayTouchDrawing = false;
            _shapeOverlayDrawPointerId = 0;
            _shapeOverlayDrawCanvas = null;
            canvas?.CompleteExternalTouchShapeDraw(null, commitIfValid: true);
        }
        else if (_isShapeOverlayTwoFingerNavigating && _shapeOverlayTouchPointers.Count < 2)
        {
            CommitShapeOverlayTwoFingerNavigation();
        }
    }

    private void OnCanvasTouchEmptyHoldRequested(PdfPageCanvas canvas, Vector2 pdfPt, Point canvasDipPt)
    {
        if (_copiedAnnotation is null ||
            TouchEmptyHoldCommandBarFlyout is null ||
            ScrollViewerContentRoot is null ||
            canvas.PageInfo is null ||
            ViewModel.IsPresentationMode)
        {
            return;
        }

        HideTouchSelectionFlyout();
        if (AnnotationEditorFlyout?.IsOpen == true)
        {
            AnnotationEditorFlyout.Hide();
        }

        if (AnnotationQuickBarFlyout?.IsOpen == true)
        {
            _pendingAfterQuickBarClosed = null;
            AnnotationQuickBarFlyout.Hide();
        }

        ViewModel.CurrentDocument?.ClearSelection();
        if (ViewModel.CurrentDocument is not null)
        {
            ViewModel.CurrentDocument.SelectedAnnotation = null;
        }

        _touchEmptyHoldCanvas = canvas;
        _touchEmptyHoldPdfPt = pdfPt;
        _touchEmptyHoldDipPt = canvasDipPt;
        _lastMouseClickPageIndex = canvas.PageInfo.Index;
        _lastMouseClickPdfPt = pdfPt;
        _hasLastMouseClickPdfPt = true;
        SetVisible(TouchEmptyPasteButton, _copiedAnnotation is not null);

        try
        {
            Point rootPt = canvas.TransformToVisual(ScrollViewerContentRoot).TransformPoint(canvasDipPt);
            double rootW = Math.Max(48.0, ScrollViewerContentRoot.ActualWidth);
            double rootH = Math.Max(48.0, ScrollViewerContentRoot.ActualHeight);
            double clampedX = Math.Clamp(rootPt.X, 24.0, rootW - 24.0);
            double clampedY = Math.Clamp(rootPt.Y, 24.0, rootH - 24.0);
            bool placeBelow = clampedY < 72.0;

            if (TouchEmptyHoldCommandBarFlyout.IsOpen)
            {
                TouchEmptyHoldCommandBarFlyout.Hide();
            }

            var options = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = new Point(clampedX, clampedY),
                Placement = placeBelow
                    ? Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom
                    : Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top,
                ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Transient,
            };
            TouchEmptyHoldCommandBarFlyout.ShowAt(ScrollViewerContentRoot, options);
            _isTouchEmptyHoldFlyoutOpen = true;
        }
        catch
        {
        }
    }

    private void OnTouchEmptyHoldFlyoutClosed(object? sender, object e)
    {
        _isTouchEmptyHoldFlyoutOpen = false;
    }

    private void OnTouchEmptyPasteClick(object sender, RoutedEventArgs e)
    {
        PdfPageCanvas? canvas = _touchEmptyHoldCanvas;
        Vector2 pdfPt = _touchEmptyHoldPdfPt;
        HideAnnotationPopupsImmediate();
        if (canvas?.PageInfo is { } pageInfo && ViewModel.CurrentDocument is { } doc)
        {
            _ = PasteCopiedAnnotationAtAsync(doc, pageInfo.Index, pdfPt.X, pdfPt.Y);
        }
    }

    private void OnTouchSelectionHighlightButtonClick(object sender, RoutedEventArgs e)
    {
        DocumentViewModel? doc = ViewModel.CurrentDocument;
        if (doc?.ActiveSelection is { HasContent: true } sel)
        {
            _ = doc.AddMarkupForSelectionAsync(sel, PdfAnnotationSubtype.Highlight, doc.ActiveAnnotationColor);
        }

        HideTouchSelectionFlyout();
    }

    private void OnTouchSelectionCommentButtonClick(object sender, RoutedEventArgs e)
    {
        DocumentViewModel? doc = ViewModel.CurrentDocument;
        if (doc?.ActiveSelection is not { HasContent: true } sel)
        {
            HideTouchSelectionFlyout();
            return;
        }

        PdfPageCanvas? anchorCanvas = FindRealizedCanvasForPage(sel.StartPageIndex);
        Point anchorDip = ComputeSelectionAnchorCanvasDip(sel, anchorCanvas);
        HideTouchSelectionFlyout();
        OpenCreateAnnotationEditor(
            AnnotationEditorMode.CreateMarkupWithComment,
            sel.StartPageIndex,
            default,
            anchorCanvas,
            anchorDip,
            sel);
    }

    private void OnTouchSelectionPasteButtonClick(object sender, RoutedEventArgs e)
    {
        DocumentViewModel? doc = ViewModel.CurrentDocument;
        HideTouchSelectionFlyout();
        if (doc is null || _copiedAnnotation is null)
        {
            return;
        }

        doc.ClearSelection();
        if (_hasLastMouseClickPdfPt &&
            _lastMouseClickPageIndex >= 0 &&
            _lastMouseClickPageIndex < doc.Pages.Count)
        {
            _ = PasteCopiedAnnotationAtAsync(doc, _lastMouseClickPageIndex, _lastMouseClickPdfPt.X, _lastMouseClickPdfPt.Y);
        }
        else
        {
            _ = PasteCopiedAnnotationFromKeyboardAsync(doc);
        }
    }

    private void OnTouchSelectionSubtypeClick(object sender, RoutedEventArgs e)
    {
        DocumentViewModel? doc = ViewModel.CurrentDocument;
        if (doc?.ActiveSelection is { HasContent: true } sel && sender is FrameworkElement fe)
        {
            PdfAnnotationSubtype subtype = ParseMarkupSubtypeTag(fe.Tag as string, PdfAnnotationSubtype.Underline);
            _ = doc.AddMarkupForSelectionAsync(sel, subtype, doc.ActiveAnnotationColor);
        }

        HideTouchSelectionFlyout();
    }

    private void OnTouchSelectionColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            PdfAnnotationColor current = ViewModel.CurrentDocument?.ActiveAnnotationColor ?? ViewModel.DefaultAnnotationColor;
            PdfAnnotationColor color = ParseAnnotationColorTag(fe.Tag as string, current);
            SetDefaultAnnotationColor(color);
        }
    }

    private void OnTouchScrollScrubberRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnTouchScrollScrubberContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        args.Handled = true;
    }

    private bool IsDescendantOfTouchScrubber(DependencyObject? element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, TouchScrollThumbHitTarget) ||
                ReferenceEquals(element, TouchScrollScrubberContainer))
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void OnTouchSelectionCopyButtonClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CopySelectedText();
        HideTouchSelectionFlyout();
    }

    private void OnTouchSelectionFlyoutClosed(object? sender, object e)
    {
        _isTouchSelectionFlyoutOpen = false;
    }

    private void HideTouchSelectionFlyout()
    {
        if (TouchSelectionCommandBarFlyout is null)
        {
            _isTouchSelectionFlyoutOpen = false;
            return;
        }

        try
        {
            if (TouchSelectionCommandBarFlyout.IsOpen)
            {
                TouchSelectionCommandBarFlyout.Hide();
            }
        }
        catch
        {
        }
        finally
        {
            _isTouchSelectionFlyoutOpen = false;
        }
    }

    private void OnCanvasTouchSelectionDismissRequested()
    {
        if (!_isDraggingSelectionHandle)
        {
            HideTouchSelectionFlyout();
        }

        if (AnnotationQuickBarFlyout?.IsOpen == true)
        {
            try
            {
                _quickBarClosedProgrammatically = true;
                AnnotationQuickBarFlyout.Hide();
            }
            catch
            {
            }
        }

        if (_isTouchEmptyHoldFlyoutOpen)
        {
            try
            {
                if (TouchEmptyHoldCommandBarFlyout?.IsOpen == true)
                {
                    TouchEmptyHoldCommandBarFlyout.Hide();
                }
            }
            catch
            {
            }
            finally
            {
                _isTouchEmptyHoldFlyoutOpen = false;
            }
        }
    }

    private void OnCanvasTouchSelectionStarted(PdfPageCanvas canvas)
    {
        _isLastPointerInputTouch = true;
        _activeTouchSelectingCanvas = canvas;
        _isTouchSelectionHandlesActive = true;
        _isTouchSwipeTracking = false;
        _isTouchSwipeEngaged = false;
        if (_isSlideSwipeActive || _isSlideSwipeAnimating)
        {
            CancelInteractiveSlideSwipeImmediate();
        }

        HideTouchSelectionFlyout();

        if (ContinuousScrollViewer is not null)
        {
            if (ContinuousScrollViewer.HorizontalScrollMode != ScrollMode.Disabled)
            {
                ContinuousScrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
            }

            if (ContinuousScrollViewer.VerticalScrollMode != ScrollMode.Disabled)
            {
                ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
            }

            try
            {
                _ = ContinuousScrollViewer.CancelDirectManipulations();
            }
            catch
            {
            }
        }

        UpdateTouchSelectionHandlesLayout();
    }

    private void OnCanvasTouchSelectionEnded(PdfPageCanvas canvas)
    {
        if (ReferenceEquals(_activeTouchSelectingCanvas, canvas))
        {
            _activeTouchSelectingCanvas = null;
        }

        _isTouchSwipeTracking = false;
        _isTouchSwipeEngaged = false;
        SyncPagedScrollViewerModes();
        UpdateTouchSelectionHandlesLayout();
    }

    private void OnCanvasTouchSelectionCompleted(PdfPageCanvas canvas, PdfTextSelection selection)
    {
        _isTouchSelectionHandlesActive = true;
        UpdateTouchSelectionHandlesLayout();
        ShowTouchSelectionCopyFlyout(selection, canvas);
    }

    private void OnCanvasTouchAnnotationDragStarted(PdfPageCanvas canvas, uint dragPointerId)
    {
        _isLastPointerInputTouch = true;
        _activeTouchDraggingAnnotCanvas = canvas;
        _touchDragAnnotPointerId = dragPointerId;
        StopTouchAnnotPanPolling();
        _touchAnnotPanPointerId = 0;

        _isTouchSwipeTracking = false;
        _isTouchSwipeEngaged = false;
        if (_isSlideSwipeActive || _isSlideSwipeAnimating)
        {
            CancelInteractiveSlideSwipeImmediate();
        }

        HideAnnotationPopupsImmediate();

        if (ContinuousScrollViewer is not null)
        {
            if (ContinuousScrollViewer.ZoomFactor > 0.05)
            {
                SetInteractiveViewportState(
                    ContinuousScrollViewer.ZoomFactor,
                    ContinuousScrollViewer.HorizontalOffset,
                    ContinuousScrollViewer.VerticalOffset);
            }

            if (ContinuousScrollViewer.HorizontalScrollMode != ScrollMode.Disabled)
            {
                ContinuousScrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
            }

            if (ContinuousScrollViewer.VerticalScrollMode != ScrollMode.Disabled)
            {
                ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
            }

            try
            {
                _ = ContinuousScrollViewer.CancelDirectManipulations();
            }
            catch
            {
            }
        }
    }

    private void OnCanvasTouchAnnotationDragEnded(PdfPageCanvas canvas)
    {
        StopTouchAnnotPanPolling();
        if (ReferenceEquals(_activeTouchDraggingAnnotCanvas, canvas))
        {
            _activeTouchDraggingAnnotCanvas = null;
            _touchDragAnnotPointerId = 0;
        }

        _touchAnnotPanPointerId = 0;
        _isTouchSwipeTracking = false;
        _isTouchSwipeEngaged = false;
        SyncPagedScrollViewerModes();
    }

    private void StartTouchAnnotPanPolling()
    {
        if (!_isTouchAnnotPanPollingSubscribed)
        {
            CompositionTarget.Rendering += OnTouchAnnotPanPollingRendering;
            _isTouchAnnotPanPollingSubscribed = true;
        }
    }

    private void StopTouchAnnotPanPolling()
    {
        _hasTouchAnnotPanLastScreenPx = false;
        if (_isTouchAnnotPanPollingSubscribed)
        {
            CompositionTarget.Rendering -= OnTouchAnnotPanPollingRendering;
            _isTouchAnnotPanPollingSubscribed = false;
        }
    }

    private void LockScrollViewerForTouchAnnotationDrag()
    {
        if (ContinuousScrollViewer is null)
        {
            return;
        }

        if (ContinuousScrollViewer.HorizontalScrollMode != ScrollMode.Disabled)
        {
            ContinuousScrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
        }

        if (ContinuousScrollViewer.VerticalScrollMode != ScrollMode.Disabled)
        {
            ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
        }
    }

    private void OnTouchAnnotPanPollingRendering(object? sender, object e)
    {
        if (_activeTouchDraggingAnnotCanvas is not { IsTouchDraggingAnnotation: true } ||
            _touchAnnotPanPointerId == 0)
        {
            _touchAnnotPanPointerId = 0;
            StopTouchAnnotPanPolling();
            return;
        }

        bool ok = GetPointerInfo(_touchAnnotPanPointerId, out POINTER_INFO pi);
        if (!ok ||
            (pi.pointerFlags & POINTER_FLAG_INCONTACT) == 0 ||
            (pi.pointerFlags & 0x00040000u) != 0)
        {
            _activeTouchPointers.Remove(_touchAnnotPanPointerId);
            _touchAnnotPanPointerId = 0;
            StopTouchAnnotPanPolling();
            LockScrollViewerForTouchAnnotationDrag();
            return;
        }

        if (!_hasTouchAnnotPanLastScreenPx)
        {
            _touchAnnotPanLastScreenPx = pi.ptPixelLocation;
            _hasTouchAnnotPanLastScreenPx = true;
            return;
        }

        double scale = XamlRoot?.RasterizationScale > 0.01 ? XamlRoot.RasterizationScale : 1.0;
        double panDxDip = (pi.ptPixelLocation.X - _touchAnnotPanLastScreenPx.X) / scale;
        double panDyDip = (pi.ptPixelLocation.Y - _touchAnnotPanLastScreenPx.Y) / scale;
        _touchAnnotPanLastScreenPx = pi.ptPixelLocation;

        if (Math.Abs(panDxDip) > 0.25 || Math.Abs(panDyDip) > 0.25)
        {
            PanPagedScrollViewerBy(panDxDip, panDyDip);
        }
    }

    private PdfPageCanvas? FindCanvasWaitingOnMovableAnnotation()
    {
        foreach (PdfPageCanvas canvas in _activeCanvases)
        {
            if (canvas.IsTouchWaitingOnMovableAnnotation)
            {
                return canvas;
            }
        }

        return null;
    }

    private bool IsDescendantOfTouchResizeHandle(DependencyObject? source)
    {
        for (DependencyObject? cur = source; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (ReferenceEquals(cur, TouchAnnotationResizeHandlesCanvas) ||
                ReferenceEquals(cur, TouchAnnotationMoveBodyHandle) ||
                ReferenceEquals(cur, TouchAnnotationQuickBarBorder) ||
                ReferenceEquals(cur, TouchResizeTopLeftHandle) ||
                ReferenceEquals(cur, TouchResizeTopRightHandle) ||
                ReferenceEquals(cur, TouchResizeBottomLeftHandle) ||
                ReferenceEquals(cur, TouchResizeBottomRightHandle))
            {
                return true;
            }
        }

        return false;
    }

    private void SyncTouchQuickBarForAnnotation(PdfAnnotationInfo annot, PdfPageCanvas canvas, Point anchorCanvasDipPt)
    {
        _quickBarAnnotation = annot;
        _quickBarCanvas = canvas;
        _quickBarAnchorDipPt = anchorCanvasDipPt;

        bool changedTarget = _touchQuickBarBoundAnnotPage != annot.PageIndex ||
                             _touchQuickBarBoundAnnotIndex != annot.AnnotIndex;
        if (!changedTarget)
        {
            return;
        }

        _touchQuickBarBoundAnnotPage = annot.PageIndex;
        _touchQuickBarBoundAnnotIndex = annot.AnnotIndex;

        SetVisible(TouchQuickBarColorButton, annot.SupportsColorChange);
        SetVisible(TouchQuickBarCopyButton, annot.CanCopyOrCut);
        SetVisible(TouchQuickBarCutButton, annot.CanCopyOrCut);
        if (TouchQuickBarAddCommentButton?.Content is FontIcon quickCommentIcon)
        {
            quickCommentIcon.Glyph = annot.IsTextBox ? "\uE70F" : "\uE90A";
        }

        SetToolTip(
            TouchQuickBarAddCommentButton,
            annot.IsTextBox ? ResourceKeys.AnnotEditTextToolTip : ResourceKeys.AnnotAddCommentToolTip);
        SetVisible(TouchQuickBarColorRow, false);
        ApplySwatchSelectedState(TouchQuickColorYellowButton, IsSameColor(annot.Color, PdfAnnotationColor.Yellow));
        ApplySwatchSelectedState(TouchQuickColorGreenButton, IsSameColor(annot.Color, PdfAnnotationColor.Green));
        ApplySwatchSelectedState(TouchQuickColorBlueButton, IsSameColor(annot.Color, PdfAnnotationColor.Blue));
        ApplySwatchSelectedState(TouchQuickColorPinkButton, IsSameColor(annot.Color, PdfAnnotationColor.Pink));
        ApplySwatchSelectedState(TouchQuickColorOrangeButton, IsSameColor(annot.Color, PdfAnnotationColor.Orange));
        ApplySwatchSelectedState(TouchQuickColorRedButton, IsSameColor(annot.Color, PdfAnnotationColor.Red));
    }

    private void UpdateTouchAnnotationResizeHandlesLayout(Rect? livePreviewCanvasRect = null, PdfPageCanvas? liveCanvas = null)
    {
        if (TouchAnnotationResizeHandlesCanvas is null ||
            TouchAnnotationMoveBodyHandle is null ||
            TouchResizeTopLeftHandle is null ||
            TouchResizeTopRightHandle is null ||
            TouchResizeBottomLeftHandle is null ||
            TouchResizeBottomRightHandle is null ||
            ScrollViewerContentRoot is null)
        {
            return;
        }

        if ((!_isLastPointerInputTouch && !_isDraggingAnnotationMoveHandle && !_isDraggingAnnotationResizeHandle) ||
            ViewModel?.IsPresentationMode == true ||
            ViewModel?.ActiveTool != AnnotationTool.Select ||
            ViewModel?.CurrentDocument?.SelectedAnnotation is not { } selAnnot ||
            selAnnot.IsSignatureWidget ||
            selAnnot.Bounds.IsEmpty)
        {
            _touchQuickBarBoundAnnotPage = -1;
            _touchQuickBarBoundAnnotIndex = -1;
            if (TouchAnnotationQuickBarBorder is not null)
            {
                TouchAnnotationQuickBarBorder.Visibility = Visibility.Collapsed;
            }

            SetVisible(TouchQuickBarColorRow, false);
            if (TouchAnnotationResizeHandlesCanvas.Visibility != Visibility.Collapsed)
            {
                TouchAnnotationResizeHandlesCanvas.Visibility = Visibility.Collapsed;
            }

            return;
        }

        PdfPageCanvas? canvas = liveCanvas ?? FindRealizedCanvasForPage(selAnnot.PageIndex);
        if (canvas?.PageInfo is not { } pageInfo)
        {
            TouchAnnotationResizeHandlesCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            Rect pageRectDip = livePreviewCanvasRect ??
                PageCoordinateTransform.FromPage(pageInfo, canvas.RotationAngle).ToPageRect(selAnnot.Bounds);
            if (pageRectDip.Width < 1.0 || pageRectDip.Height < 1.0)
            {
                TouchAnnotationResizeHandlesCanvas.Visibility = Visibility.Collapsed;
                return;
            }

            GeneralTransform toRoot = canvas.TransformToVisual(ScrollViewerContentRoot);
            Rect rootRect = toRoot.TransformBounds(pageRectDip);

            // 1. Movable body hit target (expanded to at least 52x52 DIP centered on small annotations like comments/text)
            if (selAnnot.IsMovable && !_isInlineTextEditorOpen)
            {
                const double minMoveHitDip = 52.0;
                double padX = rootRect.Width < 72.0 ? Math.Max(8.0, (minMoveHitDip - rootRect.Width) * 0.5) : 0.0;
                double padY = rootRect.Height < 72.0 ? Math.Max(8.0, (minMoveHitDip - rootRect.Height) * 0.5) : 0.0;
                TouchAnnotationMoveBodyHandle.Width = Math.Max(minMoveHitDip, rootRect.Width + (padX * 2.0));
                TouchAnnotationMoveBodyHandle.Height = Math.Max(minMoveHitDip, rootRect.Height + (padY * 2.0));
                Canvas.SetLeft(TouchAnnotationMoveBodyHandle, rootRect.Left - padX);
                Canvas.SetTop(TouchAnnotationMoveBodyHandle, rootRect.Top - padY);
                TouchAnnotationMoveBodyHandle.Visibility = Visibility.Visible;
            }
            else
            {
                TouchAnnotationMoveBodyHandle.Visibility = Visibility.Collapsed;
            }

            // 2. Corner resize handles (for resizable stamps/images), shifted slightly outward on small images
            // so the center interior remains wide open for drag-to-move.
            if (selAnnot.IsResizable && !_isInlineTextEditorOpen)
            {
                double minSide = Math.Min(rootRect.Width, rootRect.Height);
                double cornerSize = minSide < 84.0 ? 36.0 : 40.0;
                double halfCorner = cornerSize * 0.5;
                double outward = minSide < 96.0 ? Math.Clamp((96.0 - minSide) * 0.22, 0.0, 14.0) : 0.0;

                TouchResizeTopLeftHandle.Width = cornerSize;
                TouchResizeTopLeftHandle.Height = cornerSize;
                TouchResizeTopRightHandle.Width = cornerSize;
                TouchResizeTopRightHandle.Height = cornerSize;
                TouchResizeBottomLeftHandle.Width = cornerSize;
                TouchResizeBottomLeftHandle.Height = cornerSize;
                TouchResizeBottomRightHandle.Width = cornerSize;
                TouchResizeBottomRightHandle.Height = cornerSize;

                Canvas.SetLeft(TouchResizeTopLeftHandle, rootRect.Left - outward - halfCorner);
                Canvas.SetTop(TouchResizeTopLeftHandle, rootRect.Top - outward - halfCorner);

                Canvas.SetLeft(TouchResizeTopRightHandle, rootRect.Right + outward - halfCorner);
                Canvas.SetTop(TouchResizeTopRightHandle, rootRect.Top - outward - halfCorner);

                Canvas.SetLeft(TouchResizeBottomLeftHandle, rootRect.Left - outward - halfCorner);
                Canvas.SetTop(TouchResizeBottomLeftHandle, rootRect.Bottom + outward - halfCorner);

                Canvas.SetLeft(TouchResizeBottomRightHandle, rootRect.Right + outward - halfCorner);
                Canvas.SetTop(TouchResizeBottomRightHandle, rootRect.Bottom + outward - halfCorner);

                TouchResizeTopLeftHandle.Visibility = Visibility.Visible;
                TouchResizeTopRightHandle.Visibility = Visibility.Visible;
                TouchResizeBottomLeftHandle.Visibility = Visibility.Visible;
                TouchResizeBottomRightHandle.Visibility = Visibility.Visible;
            }
            else
            {
                TouchResizeTopLeftHandle.Visibility = Visibility.Collapsed;
                TouchResizeTopRightHandle.Visibility = Visibility.Collapsed;
                TouchResizeBottomLeftHandle.Visibility = Visibility.Collapsed;
                TouchResizeBottomRightHandle.Visibility = Visibility.Collapsed;
            }

            // 3. Non-modal floating Acrylic QuickBar (no WinUI Flyout light-dismiss wall to steal drag/resize touches)
            if (TouchAnnotationQuickBarBorder is not null)
            {
                bool isActivelyDragging = _hasTouchMoveHandleMoved ||
                                          _isDraggingAnnotationResizeHandle ||
                                          (_activeTouchDraggingAnnotCanvas is { IsTouchDraggingAnnotation: true } && !_isDraggingAnnotationMoveHandle);
                bool showTouchQuickBar = _isLastPointerInputTouch &&
                                         !isActivelyDragging &&
                                         !_isInlineTextEditorOpen &&
                                         AnnotationEditorFlyout?.IsOpen != true;
                if (showTouchQuickBar)
                {
                    Point centerCanvasDip = new(pageRectDip.Left + (pageRectDip.Width * 0.5), pageRectDip.Top + (pageRectDip.Height * 0.5));
                    SyncTouchQuickBarForAnnotation(selAnnot, canvas, centerCanvasDip);
                    bool wasCollapsed = TouchAnnotationQuickBarBorder.Visibility != Visibility.Visible;
                    if (wasCollapsed)
                    {
                        TouchAnnotationQuickBarBorder.Visibility = Visibility.Visible;
                        _quickBarOpenedTick = Environment.TickCount64;
                    }

                    TouchAnnotationQuickBarBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double barW = TouchAnnotationQuickBarBorder.ActualWidth > 1.0
                        ? TouchAnnotationQuickBarBorder.ActualWidth
                        : Math.Max(140.0, TouchAnnotationQuickBarBorder.DesiredSize.Width);
                    double barH = TouchAnnotationQuickBarBorder.ActualHeight > 1.0
                        ? TouchAnnotationQuickBarBorder.ActualHeight
                        : Math.Max(44.0, TouchAnnotationQuickBarBorder.DesiredSize.Height);

                    double rootW = Math.Max(64.0, ScrollViewerContentRoot.ActualWidth);
                    double rootH = Math.Max(64.0, ScrollViewerContentRoot.ActualHeight);
                    double topEdge = selAnnot.IsMovable && rootRect.Height < 52.0
                        ? rootRect.Top - ((52.0 - rootRect.Height) * 0.5)
                        : rootRect.Top;
                    double bottomEdge = selAnnot.IsMovable && rootRect.Height < 52.0
                        ? rootRect.Bottom + ((52.0 - rootRect.Height) * 0.5)
                        : rootRect.Bottom;

                    double barLeft = Math.Clamp((rootRect.Left + (rootRect.Width * 0.5)) - (barW * 0.5), 12.0, Math.Max(12.0, rootW - barW - 12.0));
                    bool placeBelow = (topEdge - barH - 18.0) < 12.0;
                    double barTop = placeBelow
                        ? Math.Clamp(bottomEdge + 18.0, 12.0, Math.Max(12.0, rootH - barH - 12.0))
                        : Math.Clamp(topEdge - barH - 18.0, 12.0, Math.Max(12.0, rootH - barH - 12.0));

                    Canvas.SetLeft(TouchAnnotationQuickBarBorder, barLeft);
                    Canvas.SetTop(TouchAnnotationQuickBarBorder, barTop);
                }
                else
                {
                    TouchAnnotationQuickBarBorder.Visibility = Visibility.Collapsed;
                }
            }

            if (TouchAnnotationResizeHandlesCanvas.Visibility != Visibility.Visible)
            {
                TouchAnnotationResizeHandlesCanvas.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            TouchAnnotationResizeHandlesCanvas.Visibility = Visibility.Collapsed;
        }
    }

    private void SyncActiveTouchOverlayDragAfterViewportPan()
    {
        if (ScrollViewerContentRoot is null)
        {
            return;
        }

        if (_isDraggingAnnotationMoveHandle && _annotMoveTargetCanvas is { } moveCanvas)
        {
            try
            {
                Point ptInCanvas = ScrollViewerContentRoot.TransformToVisual(moveCanvas).TransformPoint(_touchMoveLastRootPos);
                Rect previewDip = moveCanvas.UpdateExternalTouchAnnotationMove(ptInCanvas, out bool hasMoved);
                if (hasMoved)
                {
                    _hasTouchMoveHandleMoved = true;
                }

                UpdateTouchAnnotationResizeHandlesLayout(previewDip, moveCanvas);
            }
            catch
            {
            }

            return;
        }

        if (_isDraggingAnnotationResizeHandle && _annotResizeTargetCanvas is { } resizeCanvas)
        {
            try
            {
                Point ptInCanvas = ScrollViewerContentRoot.TransformToVisual(resizeCanvas).TransformPoint(_touchResizeLastRootPos);
                Rect previewDip = resizeCanvas.UpdateExternalTouchAnnotationResize(ptInCanvas);
                UpdateTouchAnnotationResizeHandlesLayout(previewDip, resizeCanvas);
            }
            catch
            {
            }
        }
    }

    private void OnTouchMoveHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ScrollViewerContentRoot is null ||
            TouchAnnotationMoveBodyHandle is null ||
            ViewModel?.CurrentDocument?.SelectedAnnotation is not { IsMovable: true } selAnnot ||
            FindRealizedCanvasForPage(selAnnot.PageIndex) is not { } canvas)
        {
            return;
        }

        if (_isDraggingAnnotationMoveHandle || _isDraggingAnnotationResizeHandle)
        {
            return;
        }

        Point ptInCanvas = e.GetCurrentPoint(canvas).Position;
        if (!canvas.BeginExternalTouchAnnotationMove(selAnnot, ptInCanvas, out Rect origRectDip))
        {
            return;
        }

        e.Handled = true;
        _isLastPointerInputTouch = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch;
        _isDraggingAnnotationMoveHandle = true;
        _annotMoveHandlePointerId = e.Pointer.PointerId;
        _annotMoveTargetCanvas = canvas;
        _activeTouchDraggingAnnotCanvas = canvas;
        _touchDragAnnotPointerId = e.Pointer.PointerId;
        _touchMoveLastRootPos = e.GetCurrentPoint(ScrollViewerContentRoot).Position;
        _hasTouchMoveHandleMoved = false;

        LockScrollViewerForTouchAnnotationDrag();
        try
        {
            _ = TouchAnnotationMoveBodyHandle.CapturePointer(e.Pointer);
        }
        catch
        {
        }

        UpdateTouchAnnotationResizeHandlesLayout(origRectDip, canvas);
    }

    private void OnTouchMoveHandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingAnnotationMoveHandle ||
            e.Pointer.PointerId != _annotMoveHandlePointerId ||
            _annotMoveTargetCanvas is not { } canvas ||
            ScrollViewerContentRoot is null)
        {
            return;
        }

        e.Handled = true;
        try
        {
            _touchMoveLastRootPos = e.GetCurrentPoint(ScrollViewerContentRoot).Position;
            Point ptInCanvas = e.GetCurrentPoint(canvas).Position;
            Rect previewDip = canvas.UpdateExternalTouchAnnotationMove(ptInCanvas, out bool hasMoved);
            if (hasMoved)
            {
                _hasTouchMoveHandleMoved = true;
            }

            UpdateTouchAnnotationResizeHandlesLayout(previewDip, canvas);
        }
        catch
        {
        }
    }

    private void OnTouchMoveHandlePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingAnnotationMoveHandle || e.Pointer.PointerId != _annotMoveHandlePointerId)
        {
            return;
        }

        e.Handled = true;
        Point? finalInCanvas = null;
        if (_annotMoveTargetCanvas is { } canvas)
        {
            try
            {
                finalInCanvas = e.GetCurrentPoint(canvas).Position;
            }
            catch
            {
            }
        }

        FinishTouchAnnotationMoveHandleDrag(e.Pointer, commit: true, finalInCanvas);
    }

    private void OnTouchMoveHandlePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingAnnotationMoveHandle && e.Pointer.PointerId == _annotMoveHandlePointerId)
        {
            FinishTouchAnnotationMoveHandleDrag(e.Pointer, commit: true, finalInCanvas: null);
        }
    }

    private void OnTouchMoveHandlePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isReleasingOverlayCapture)
        {
            return;
        }

        if (_isDraggingAnnotationMoveHandle && e.Pointer.PointerId == _annotMoveHandlePointerId)
        {
            FinishTouchAnnotationMoveHandleDrag(null, commit: true, finalInCanvas: null);
        }
    }

    private void CancelTouchAnnotationMoveHandleDragImmediate()
    {
        if (_isDraggingAnnotationMoveHandle)
        {
            FinishTouchAnnotationMoveHandleDrag(null, commit: false, finalInCanvas: null);
        }
    }

    private void FinishTouchAnnotationMoveHandleDrag(Pointer? pointerToRelease, bool commit, Point? finalInCanvas)
    {
        if (!_isDraggingAnnotationMoveHandle)
        {
            return;
        }

        PdfPageCanvas? canvas = _annotMoveTargetCanvas;
        bool hadMoved = _hasTouchMoveHandleMoved;
        PdfAnnotationInfo? selAnnot = ViewModel?.CurrentDocument?.SelectedAnnotation;

        _isDraggingAnnotationMoveHandle = false;
        _annotMoveHandlePointerId = 0;
        _annotMoveTargetCanvas = null;
        _hasTouchMoveHandleMoved = false;
        StopTouchAnnotPanPolling();
        _touchAnnotPanPointerId = 0;
        _hasTouchAnnotPanLastRootPos = false;
        if (ReferenceEquals(_activeTouchDraggingAnnotCanvas, canvas))
        {
            _activeTouchDraggingAnnotCanvas = null;
            _touchDragAnnotPointerId = 0;
        }

        if (pointerToRelease is not null && TouchAnnotationMoveBodyHandle is not null)
        {
            _isReleasingOverlayCapture = true;
            try
            {
                TouchAnnotationMoveBodyHandle.ReleasePointerCapture(pointerToRelease);
            }
            catch
            {
            }
            finally
            {
                _isReleasingOverlayCapture = false;
            }
        }

        bool didMove = false;
        if (canvas is not null)
        {
            if (commit)
            {
                didMove = canvas.CompleteExternalTouchAnnotationMove(finalInCanvas);
            }
            else
            {
                canvas.CancelExternalTouchAnnotationMove();
            }
        }

        SyncPagedScrollViewerModes();

        if (commit && !didMove && !hadMoved && selAnnot is not null && canvas is not null)
        {
            // Clean stationary tap on an already-selected TextBox or Comment opens its editor directly.
            if (selAnnot.IsTextBox)
            {
                HideAnnotationPopupsImmediate();
                OpenInlineTextEditorForExisting(selAnnot, canvas);
                return;
            }

            if (selAnnot.HasComment)
            {
                HideAnnotationPopupsImmediate();
                OpenExistingAnnotationEditor(selAnnot, canvas, finalInCanvas ?? default);
                return;
            }
        }

        UpdateTouchAnnotationResizeHandlesLayout();
    }

    private void OnTouchResizeHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement handleElem ||
            ScrollViewerContentRoot is null ||
            ViewModel?.CurrentDocument?.SelectedAnnotation is not { IsResizable: true } selAnnot ||
            FindRealizedCanvasForPage(selAnnot.PageIndex) is not { } canvas)
        {
            return;
        }

        if (_isDraggingAnnotationMoveHandle || _isDraggingAnnotationResizeHandle)
        {
            return;
        }

        int cornerIndex = int.TryParse(handleElem.Tag as string, out int parsed) ? parsed : 3;
        if (!canvas.BeginExternalTouchAnnotationResize(selAnnot, cornerIndex, out Rect initialPreviewDip))
        {
            return;
        }

        e.Handled = true;
        _isLastPointerInputTouch = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch;
        _isDraggingAnnotationResizeHandle = true;
        _annotResizeHandlePointerId = e.Pointer.PointerId;
        _annotResizeHandleElement = handleElem;
        _annotResizeTargetCanvas = canvas;
        _activeTouchDraggingAnnotCanvas = canvas;
        _touchDragAnnotPointerId = e.Pointer.PointerId;
        _touchResizeLastRootPos = e.GetCurrentPoint(ScrollViewerContentRoot).Position;

        HideAnnotationPopupsImmediate();
        LockScrollViewerForTouchAnnotationDrag();
        try
        {
            _ = handleElem.CapturePointer(e.Pointer);
        }
        catch
        {
        }

        UpdateTouchAnnotationResizeHandlesLayout(initialPreviewDip, canvas);
    }

    private void OnTouchResizeHandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingAnnotationResizeHandle ||
            e.Pointer.PointerId != _annotResizeHandlePointerId ||
            _annotResizeTargetCanvas is not { } canvas)
        {
            return;
        }

        e.Handled = true;
        try
        {
            if (ScrollViewerContentRoot is not null)
            {
                _touchResizeLastRootPos = e.GetCurrentPoint(ScrollViewerContentRoot).Position;
            }

            Point ptInCanvas = e.GetCurrentPoint(canvas).Position;
            Rect previewDip = canvas.UpdateExternalTouchAnnotationResize(ptInCanvas);
            UpdateTouchAnnotationResizeHandlesLayout(previewDip, canvas);
        }
        catch
        {
        }
    }

    private void OnTouchResizeHandlePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingAnnotationResizeHandle || e.Pointer.PointerId != _annotResizeHandlePointerId)
        {
            return;
        }

        e.Handled = true;
        Point? finalInCanvas = null;
        if (_annotResizeTargetCanvas is { } canvas)
        {
            try
            {
                finalInCanvas = e.GetCurrentPoint(canvas).Position;
            }
            catch
            {
            }
        }

        FinishTouchAnnotationResizeHandleDrag(e.Pointer, commit: true, finalInCanvas);
    }

    private void OnTouchResizeHandlePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingAnnotationResizeHandle && e.Pointer.PointerId == _annotResizeHandlePointerId)
        {
            FinishTouchAnnotationResizeHandleDrag(e.Pointer, commit: true, finalInCanvas: null);
        }
    }

    private void OnTouchResizeHandlePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isReleasingOverlayCapture)
        {
            return;
        }

        if (_isDraggingAnnotationResizeHandle && e.Pointer.PointerId == _annotResizeHandlePointerId)
        {
            FinishTouchAnnotationResizeHandleDrag(null, commit: true, finalInCanvas: null);
        }
    }

    private void CancelTouchAnnotationResizeHandleDragImmediate()
    {
        if (_isDraggingAnnotationResizeHandle)
        {
            FinishTouchAnnotationResizeHandleDrag(null, commit: false, finalInCanvas: null);
        }
    }

    private void FinishTouchAnnotationResizeHandleDrag(Pointer? pointerToRelease, bool commit, Point? finalInCanvas)
    {
        if (!_isDraggingAnnotationResizeHandle)
        {
            return;
        }

        PdfPageCanvas? canvas = _annotResizeTargetCanvas;
        UIElement? handleElem = _annotResizeHandleElement;

        _isDraggingAnnotationResizeHandle = false;
        _annotResizeHandlePointerId = 0;
        _annotResizeHandleElement = null;
        _annotResizeTargetCanvas = null;
        StopTouchAnnotPanPolling();
        _touchAnnotPanPointerId = 0;
        _hasTouchAnnotPanLastRootPos = false;
        if (ReferenceEquals(_activeTouchDraggingAnnotCanvas, canvas))
        {
            _activeTouchDraggingAnnotCanvas = null;
            _touchDragAnnotPointerId = 0;
        }

        if (pointerToRelease is not null && handleElem is not null)
        {
            _isReleasingOverlayCapture = true;
            try
            {
                handleElem.ReleasePointerCapture(pointerToRelease);
            }
            catch
            {
            }
            finally
            {
                _isReleasingOverlayCapture = false;
            }
        }

        if (canvas is not null)
        {
            if (commit)
            {
                canvas.CompleteExternalTouchAnnotationResize(finalInCanvas);
            }
            else
            {
                canvas.CancelExternalTouchAnnotationResize();
            }
        }

        SyncPagedScrollViewerModes();
        UpdateTouchAnnotationResizeHandlesLayout();
    }

    private PdfPageCanvas? FindRealizedCanvasForPage(int pageIndex)
    {
        foreach (PdfPageCanvas canvas in _activeCanvases)
        {
            if (canvas.PageInfo?.Index == pageIndex && !canvas.IsSwipePreview)
            {
                return canvas;
            }
        }

        if (ContinuousPagesRepeater is not null)
        {
            PdfPageCanvas? found = FindCanvasForPageInVisualTree(ContinuousPagesRepeater, pageIndex);
            if (found is not null)
            {
                _activeCanvases.Add(found);
                return found;
            }
        }

        return null;
    }

    private static PdfPageCanvas? FindCanvasForPageInVisualTree(DependencyObject? parent, int pageIndex)
    {
        if (parent is null)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is PdfPageCanvas canvas && !canvas.IsSwipePreview && canvas.PageInfo?.Index == pageIndex)
            {
                return canvas;
            }

            PdfPageCanvas? nested = FindCanvasForPageInVisualTree(child, pageIndex);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void UpdateTouchSelectionHandlesLayout()
    {
        if (TouchSelectionHandlesCanvas is null ||
            TouchSelectionStartHandle is null ||
            TouchSelectionEndHandle is null ||
            TouchSelectionStartStem is null ||
            TouchSelectionEndStem is null ||
            ScrollViewerContentRoot is null)
        {
            return;
        }

        if (!_isTouchSelectionHandlesActive ||
            ViewModel?.IsPresentationMode == true ||
            ViewModel?.CurrentDocument?.ActiveSelection is not { HasContent: true } selection)
        {
            if (TouchSelectionHandlesCanvas.Visibility != Visibility.Collapsed)
            {
                TouchSelectionHandlesCanvas.Visibility = Visibility.Collapsed;
            }

            return;
        }

        PdfRect? firstPdf = selection.GetFirstRect(out int startPageIndex);
        PdfRect? lastPdf = selection.GetLastRect(out int endPageIndex);
        if (firstPdf is not { } firstPdfRect || lastPdf is not { } lastPdfRect)
        {
            TouchSelectionHandlesCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        double rootW = ScrollViewerContentRoot.ActualWidth > 0
            ? ScrollViewerContentRoot.ActualWidth
            : ContinuousScrollViewer.ActualWidth;
        double rootH = ScrollViewerContentRoot.ActualHeight > 0
            ? ScrollViewerContentRoot.ActualHeight
            : ContinuousScrollViewer.ActualHeight;

        bool startVisible = false;
        bool endVisible = false;

        try
        {
            if (FindRealizedCanvasForPage(startPageIndex) is { PageInfo: { } startPageInfo } startCanvas)
            {
                PageCoordinateTransform startTransform = PageCoordinateTransform.FromPage(startPageInfo, startCanvas.RotationAngle);
                GeneralTransform startToRoot = startCanvas.TransformToVisual(ScrollViewerContentRoot);
                Rect firstPageRect = startTransform.ToPageRect(firstPdfRect);
                Rect firstRootRect = startToRoot.TransformBounds(firstPageRect);

                bool inBounds = rootW <= 0 || rootH <= 0 ||
                    (firstRootRect.Right >= -8.0 && firstRootRect.Left <= rootW + 8.0 &&
                     firstRootRect.Bottom >= -8.0 && firstRootRect.Top <= rootH + 8.0);
                if (inBounds || (_isDraggingSelectionHandle && _isDraggingStartSelectionHandle))
                {
                    double startStemH = Math.Clamp(firstRootRect.Height, 10.0, 72.0);
                    TouchSelectionStartStem.Height = startStemH;
                    Canvas.SetLeft(TouchSelectionStartHandle, firstRootRect.Left - 32.0);
                    Canvas.SetTop(TouchSelectionStartHandle, firstRootRect.Bottom - startStemH);
                    startVisible = true;
                }
            }

            if (FindRealizedCanvasForPage(endPageIndex) is { PageInfo: { } endPageInfo } endCanvas)
            {
                PageCoordinateTransform endTransform = PageCoordinateTransform.FromPage(endPageInfo, endCanvas.RotationAngle);
                GeneralTransform endToRoot = endCanvas.TransformToVisual(ScrollViewerContentRoot);
                Rect lastPageRect = endTransform.ToPageRect(lastPdfRect);
                Rect lastRootRect = endToRoot.TransformBounds(lastPageRect);

                bool inBounds = rootW <= 0 || rootH <= 0 ||
                    (lastRootRect.Right >= -8.0 && lastRootRect.Left <= rootW + 8.0 &&
                     lastRootRect.Bottom >= -8.0 && lastRootRect.Top <= rootH + 8.0);
                if (inBounds || (_isDraggingSelectionHandle && !_isDraggingStartSelectionHandle))
                {
                    double endStemH = Math.Clamp(lastRootRect.Height, 10.0, 72.0);
                    TouchSelectionEndStem.Height = endStemH;
                    Canvas.SetLeft(TouchSelectionEndHandle, lastRootRect.Right - 2.0);
                    Canvas.SetTop(TouchSelectionEndHandle, lastRootRect.Bottom - endStemH);
                    endVisible = true;
                }
            }

            TouchSelectionStartHandle.Visibility = startVisible ? Visibility.Visible : Visibility.Collapsed;
            TouchSelectionEndHandle.Visibility = endVisible ? Visibility.Visible : Visibility.Collapsed;
            TouchSelectionHandlesCanvas.Visibility = (startVisible || endVisible)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch
        {
            TouchSelectionHandlesCanvas.Visibility = Visibility.Collapsed;
        }
    }

    private bool IsDescendantOfTouchSelectionHandle(DependencyObject? element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, TouchSelectionStartHandle) ||
                ReferenceEquals(element, TouchSelectionEndHandle) ||
                ReferenceEquals(element, TouchScrollThumbHitTarget) ||
                ReferenceEquals(element, TouchScrollScrubberContainer))
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private bool CanShowTouchScrollScrubber()
    {
        if (!_isLastPointerInputTouch ||
            ViewModel?.IsPresentationMode == true ||
            ViewModel?.CurrentDocument is not { IsContinuous: true, Pages.Count: > 0 } ||
            ContinuousScrollViewer is null)
        {
            return false;
        }

        double viewportH = ContinuousScrollViewer.ViewportHeight > 0
            ? ContinuousScrollViewer.ViewportHeight
            : ContinuousScrollViewer.ActualHeight;
        return ContinuousScrollViewer.ScrollableHeight > Math.Max(80.0, viewportH * 0.5);
    }

    private void ShowAndSyncTouchScrollScrubber(bool resetAutoHideTimer)
    {
        if (TouchScrollScrubberContainer is null || !CanShowTouchScrollScrubber())
        {
            return;
        }

        _touchScrollCollapseTimer.Stop();
        UpdateTouchScrollScrubberPosition();

        if (!_isTouchScrollScrubberVisible || TouchScrollScrubberContainer.Visibility != Visibility.Visible)
        {
            _isTouchScrollScrubberVisible = true;
            TouchScrollScrubberContainer.Visibility = Visibility.Visible;
            TouchScrollScrubberContainer.Opacity = 1.0;
        }
        else if (TouchScrollScrubberContainer.Opacity < 0.99)
        {
            TouchScrollScrubberContainer.Opacity = 1.0;
        }

        if (resetAutoHideTimer && !_isDraggingTouchScrollScrubber)
        {
            _touchScrollHideTimer.Stop();
            _touchScrollHideTimer.Start();
        }
    }

    private void HideTouchScrollScrubberWithFade()
    {
        _touchScrollHideTimer.Stop();
        if (_isDraggingTouchScrollScrubber || TouchScrollScrubberContainer is null)
        {
            return;
        }

        _isTouchScrollScrubberVisible = false;
        TouchScrollScrubberContainer.Opacity = 0.0;
        if (TouchScrollPageBubble is not null)
        {
            TouchScrollPageBubble.Visibility = Visibility.Collapsed;
        }

        _touchScrollCollapseTimer.Stop();
        _touchScrollCollapseTimer.Start();
    }

    private void HideTouchScrollScrubberImmediate()
    {
        _touchScrollHideTimer.Stop();
        _touchScrollCollapseTimer.Stop();
        if (_isDraggingTouchScrollScrubber)
        {
            FinishTouchScrollScrubberDrag(null);
        }

        _isTouchScrollScrubberVisible = false;
        if (TouchScrollPageBubble is not null)
        {
            TouchScrollPageBubble.Visibility = Visibility.Collapsed;
        }

        if (TouchScrollScrubberContainer is not null)
        {
            TouchScrollScrubberContainer.Opacity = 0.0;
            TouchScrollScrubberContainer.Visibility = Visibility.Collapsed;
        }
    }

    private void OnTouchScrollHideTimerTick(object? sender, object e)
    {
        _touchScrollHideTimer.Stop();
        if (!_isDraggingTouchScrollScrubber)
        {
            HideTouchScrollScrubberWithFade();
        }
    }

    private void OnTouchScrollCollapseTimerTick(object? sender, object e)
    {
        _touchScrollCollapseTimer.Stop();
        if (!_isTouchScrollScrubberVisible && !_isDraggingTouchScrollScrubber && TouchScrollScrubberContainer is not null)
        {
            TouchScrollScrubberContainer.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateTouchScrollScrubberPosition()
    {
        if (TouchScrollScrubberTranslate is null || ScrollViewerContentRoot is null || ContinuousScrollViewer is null)
        {
            return;
        }

        if (_isDraggingTouchScrollScrubber)
        {
            UpdateTouchScrollPageBubbleText();
            return;
        }

        double viewerH = ScrollViewerContentRoot.ActualHeight > 0
            ? ScrollViewerContentRoot.ActualHeight
            : ContinuousScrollViewer.ActualHeight;
        if (viewerH <= 0)
        {
            return;
        }

        double maxThumbY = Math.Max(TouchScrollTrackTopMarginDip, viewerH - TouchScrollTrackBottomMarginDip - TouchScrollThumbHeightDip);
        double trackRange = Math.Max(1.0, maxThumbY - TouchScrollTrackTopMarginDip);
        double ratio = ContinuousScrollViewer.ScrollableHeight > 0.5
            ? Math.Clamp(ContinuousScrollViewer.VerticalOffset / ContinuousScrollViewer.ScrollableHeight, 0.0, 1.0)
            : 0.0;

        TouchScrollScrubberTranslate.TranslateY = TouchScrollTrackTopMarginDip + (ratio * trackRange);
    }

    private void UpdateTouchScrollPageBubbleText()
    {
        if (TouchScrollPageBubbleText is not null && ViewModel?.CurrentDocument is { Pages.Count: > 0 } doc)
        {
            TouchScrollPageBubbleText.Text = $"{doc.CurrentPageNumber} / {doc.PageCount}";
        }
    }

    private void OnTouchScrollThumbPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (TouchScrollThumbHitTarget is null ||
            ScrollViewerContentRoot is null ||
            ContinuousScrollViewer is null ||
            ViewModel?.CurrentDocument is not { IsContinuous: true, Pages.Count: > 0 })
        {
            return;
        }

        e.Handled = true;
        _isLastPointerInputTouch = true;
        _isDraggingTouchScrollScrubber = true;
        PdfPageCanvas.IsTouchScrubberDragging = true;
        _touchScrollScrubberPointerId = e.Pointer.PointerId;
        _lastScrubberScrollUpdateTick = 0;
        _lastScrubberTargetVOffset = -1.0;
        _touchScrollGrabOffsetY = Math.Clamp(
            e.GetCurrentPoint(TouchScrollThumbHitTarget).Position.Y,
            8.0,
            TouchScrollThumbHeightDip - 8.0);

        _touchScrollHideTimer.Stop();
        _touchScrollCollapseTimer.Stop();
        CancelTouchHoldOnAllCanvases();
        HideTouchSelectionFlyout();
        StopSmoothWheelZoom();

        try
        {
            _ = ContinuousScrollViewer.CancelDirectManipulations();
        }
        catch
        {
        }

        try
        {
            _ = TouchScrollThumbHitTarget.CapturePointer(e.Pointer);
        }
        catch
        {
        }

        if (TouchScrollPillScaleTransform is not null)
        {
            TouchScrollPillScaleTransform.ScaleX = 1.08;
            TouchScrollPillScaleTransform.ScaleY = 1.08;
        }

        UpdateTouchScrollPageBubbleText();
        if (TouchScrollPageBubble is not null)
        {
            TouchScrollPageBubble.Visibility = Visibility.Visible;
        }

        ShowAndSyncTouchScrollScrubber(resetAutoHideTimer: false);
    }

    private void OnTouchScrollThumbPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingTouchScrollScrubber ||
            e.Pointer.PointerId != _touchScrollScrubberPointerId ||
            ScrollViewerContentRoot is null ||
            ContinuousScrollViewer is null ||
            ViewModel?.CurrentDocument is not { IsContinuous: true, Pages.Count: > 0 } doc)
        {
            return;
        }

        e.Handled = true;
        _isLastPointerInputTouch = true;
        double fingerY = e.GetCurrentPoint(ScrollViewerContentRoot).Position.Y;
        double viewerH = ScrollViewerContentRoot.ActualHeight > 0
            ? ScrollViewerContentRoot.ActualHeight
            : ContinuousScrollViewer.ActualHeight;
        double maxThumbY = Math.Max(TouchScrollTrackTopMarginDip, viewerH - TouchScrollTrackBottomMarginDip - TouchScrollThumbHeightDip);
        double trackScrollableHeight = Math.Max(1.0, maxThumbY - TouchScrollTrackTopMarginDip);

        double thumbY = Math.Clamp(fingerY - _touchScrollGrabOffsetY, TouchScrollTrackTopMarginDip, maxThumbY);
        if (TouchScrollScrubberTranslate is not null)
        {
            TouchScrollScrubberTranslate.TranslateY = thumbY;
        }

        double normalized = Math.Clamp((thumbY - TouchScrollTrackTopMarginDip) / trackScrollableHeight, 0.0, 1.0);
        if (ContinuousScrollViewer.ScrollableHeight > 0.5)
        {
            double targetVOffset = normalized * ContinuousScrollViewer.ScrollableHeight;
            _lastScrubberTargetVOffset = targetVOffset;

            if (TouchScrollPageBubbleText is not null)
            {
                int targetPageIdx = CalculateCurrentPageIndexFromScroll(targetVOffset);
                TouchScrollPageBubbleText.Text = $"{targetPageIdx + 1} / {doc.PageCount}";
            }

            long nowTick = Environment.TickCount64;
            if (nowTick - _lastScrubberScrollUpdateTick >= 15 ||
                normalized <= 0.001 ||
                normalized >= 0.999)
            {
                _lastScrubberScrollUpdateTick = nowTick;
                EnsureScrollModesEnabledForViewChange();
                ContinuousScrollViewer.ChangeView(null, targetVOffset, null, disableAnimation: true);
                doc.LastVerticalOffset = targetVOffset;
                SyncScrollMetricsFromVerticalOffset(targetVOffset);
                UpdateTouchScrollPageBubbleText();
                ScheduleViewportUpdate();
            }
        }
    }

    private void OnTouchScrollThumbPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingTouchScrollScrubber || e.Pointer.PointerId != _touchScrollScrubberPointerId)
        {
            return;
        }

        e.Handled = true;
        FinishTouchScrollScrubberDrag(e.Pointer);
    }

    private void OnTouchScrollThumbPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingTouchScrollScrubber && e.Pointer.PointerId == _touchScrollScrubberPointerId)
        {
            FinishTouchScrollScrubberDrag(e.Pointer);
        }
    }

    private void OnTouchScrollThumbPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingTouchScrollScrubber && e.Pointer.PointerId == _touchScrollScrubberPointerId)
        {
            FinishTouchScrollScrubberDrag(null);
        }
    }

    private void FinishTouchScrollScrubberDrag(Pointer? pointerToRelease)
    {
        if (!_isDraggingTouchScrollScrubber)
        {
            PdfPageCanvas.IsTouchScrubberDragging = false;
            return;
        }

        _isDraggingTouchScrollScrubber = false;
        PdfPageCanvas.IsTouchScrubberDragging = false;

        if (_lastScrubberTargetVOffset >= 0.0 &&
            ContinuousScrollViewer is not null &&
            ContinuousScrollViewer.ScrollableHeight > 0.5 &&
            ViewModel?.CurrentDocument is { IsContinuous: true, Pages.Count: > 0 } doc)
        {
            double finalVOffset = Math.Clamp(_lastScrubberTargetVOffset, 0.0, ContinuousScrollViewer.ScrollableHeight);
            _lastScrubberTargetVOffset = -1.0;
            if (Math.Abs(ContinuousScrollViewer.VerticalOffset - finalVOffset) > 0.5)
            {
                EnsureScrollModesEnabledForViewChange();
                ContinuousScrollViewer.ChangeView(null, finalVOffset, null, disableAnimation: true);
                doc.LastVerticalOffset = finalVOffset;
                SyncScrollMetricsFromVerticalOffset(finalVOffset);
            }
        }
        else
        {
            _lastScrubberTargetVOffset = -1.0;
        }

        if (TouchScrollThumbHitTarget is not null && pointerToRelease is not null)
        {
            try
            {
                TouchScrollThumbHitTarget.ReleasePointerCapture(pointerToRelease);
            }
            catch
            {
            }
        }

        if (TouchScrollPillScaleTransform is not null)
        {
            TouchScrollPillScaleTransform.ScaleX = 1.0;
            TouchScrollPillScaleTransform.ScaleY = 1.0;
        }

        if (TouchScrollPageBubble is not null)
        {
            TouchScrollPageBubble.Visibility = Visibility.Collapsed;
        }

        SyncPagedScrollViewerModes();
        ScheduleViewportUpdate();
        _touchScrollHideTimer.Stop();
        _touchScrollHideTimer.Start();
    }

    private void OnTouchSelectionStartHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        BeginTouchSelectionHandleDrag(TouchSelectionStartHandle, isStartHandle: true, e);
    }

    private void OnTouchSelectionEndHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        BeginTouchSelectionHandleDrag(TouchSelectionEndHandle, isStartHandle: false, e);
    }

    private void BeginTouchSelectionHandleDrag(UIElement? handleElement, bool isStartHandle, PointerRoutedEventArgs e)
    {
        if (handleElement is null ||
            ScrollViewerContentRoot is null ||
            ViewModel?.CurrentDocument?.ActiveSelection is not { HasContent: true } selection)
        {
            return;
        }

        PdfRect? firstPdf = selection.GetFirstRect(out int startPageIndex);
        PdfRect? lastPdf = selection.GetLastRect(out int endPageIndex);
        if (firstPdf is not { } firstPdfRect || lastPdf is not { } lastPdfRect)
        {
            return;
        }

        int grabbedPageIndex = isStartHandle ? startPageIndex : endPageIndex;
        if (FindRealizedCanvasForPage(grabbedPageIndex) is not { PageInfo: { } grabbedPageInfo } grabbedCanvas)
        {
            return;
        }

        e.Handled = true;
        _isLastPointerInputTouch = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch;
        _isTouchSelectionHandlesActive = true;
        _isDraggingSelectionHandle = true;
        _isDraggingStartSelectionHandle = isStartHandle;
        _handleDragPointerId = e.Pointer.PointerId;
        _handleDragTargetCanvas = grabbedCanvas;
        _handleDragElement = handleElement;
        _isTouchSwipeTracking = false;
        _isTouchSwipeEngaged = false;

        CancelTouchHoldOnAllCanvases();
        HideTouchSelectionFlyout();

        if (ContinuousScrollViewer is not null)
        {
            ContinuousScrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
            ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
            ContinuousScrollViewer.ZoomMode = ZoomMode.Disabled;
        }

        try
        {
            _ = handleElement.CapturePointer(e.Pointer);
        }
        catch
        {
        }

        int fixedAnchorPageIndex;
        int fixedAnchorCharIndex;
        System.Numerics.Vector2 fixedAnchorPdfPt;
        PdfSelectionAnchorClamp clampMode;

        if (isStartHandle)
        {
            fixedAnchorPageIndex = selection.LastPageIndex;
            fixedAnchorCharIndex = selection.LastPageEndCharIndex;
            fixedAnchorPdfPt = new System.Numerics.Vector2(
                (lastPdfRect.Left + lastPdfRect.Right) * 0.5f,
                (lastPdfRect.Bottom + lastPdfRect.Top) * 0.5f);
            clampMode = PdfSelectionAnchorClamp.AtOrBeforeAnchor;
        }
        else
        {
            fixedAnchorPageIndex = selection.StartPageIndex;
            fixedAnchorCharIndex = selection.FirstPageStartCharIndex;
            fixedAnchorPdfPt = new System.Numerics.Vector2(
                (firstPdfRect.Left + firstPdfRect.Right) * 0.5f,
                (firstPdfRect.Bottom + firstPdfRect.Top) * 0.5f);
            clampMode = PdfSelectionAnchorClamp.AtOrAfterAnchor;
        }

        PageCoordinateTransform grabbedTransform = PageCoordinateTransform.FromPage(grabbedPageInfo, grabbedCanvas.RotationAngle);
        Rect grabbedPageDipRect = grabbedTransform.ToPageRect(isStartHandle ? firstPdfRect : lastPdfRect);
        Point grabbedCharCenterDip = new(
            isStartHandle
                ? (grabbedPageDipRect.Left + Math.Min(4.0, grabbedPageDipRect.Width * 0.5))
                : (grabbedPageDipRect.Right - Math.Min(4.0, grabbedPageDipRect.Width * 0.5)),
            (grabbedPageDipRect.Top + grabbedPageDipRect.Bottom) * 0.5);

        Point grabbedCharCenterRoot;
        try
        {
            grabbedCharCenterRoot = grabbedCanvas.TransformToVisual(ScrollViewerContentRoot).TransformPoint(grabbedCharCenterDip);
        }
        catch
        {
            grabbedCharCenterRoot = e.GetCurrentPoint(ScrollViewerContentRoot).Position;
        }

        Point pointerRoot = e.GetCurrentPoint(ScrollViewerContentRoot).Position;
        _handleGrabOffsetRootDip = new Point(
            grabbedCharCenterRoot.X - pointerRoot.X,
            grabbedCharCenterRoot.Y - pointerRoot.Y);
        _lastHandleTargetRootDip = grabbedCharCenterRoot;

        _crossPageSelectionSessionId++;
        _isCrossPageSelectionInFlight = false;
        _hasPendingCrossPageSelection = false;
        _selDragSourceCanvas = grabbedCanvas;
        _selDragSourceSessionId = 0;
        _selDragAnchorPageIndex = fixedAnchorPageIndex;
        _selDragAnchorCharIndex = fixedAnchorCharIndex;
        _selDragAnchorPdfPt = fixedAnchorPdfPt;
        _selDragClampMode = clampMode;
        _selDragInitWordPageIndex = -1;
        _selDragInitWordStart = -1;
        _selDragInitWordEnd = -1;
        _selDragLastActivePageIndex = grabbedPageIndex;
        _selDragLastActiveCharIndex = isStartHandle ? selection.FirstPageStartCharIndex : selection.LastPageEndCharIndex;
        _selDragLastRootPoint = grabbedCharCenterRoot;
        _selDragIsTouchMode = true;
    }

    private void OnTouchSelectionHandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSelectionHandle || e.Pointer.PointerId != _handleDragPointerId || ScrollViewerContentRoot is null)
        {
            return;
        }

        e.Handled = true;
        Point pointerRoot = e.GetCurrentPoint(ScrollViewerContentRoot).Position;
        Point targetRoot = new(
            pointerRoot.X + _handleGrabOffsetRootDip.X,
            pointerRoot.Y + _handleGrabOffsetRootDip.Y);
        _lastHandleTargetRootDip = targetRoot;
        UpdateSelectionEdgeAutoScroll(pointerRoot);
        RequestCrossPageSelectionUpdate(targetRoot, force: false);
    }

    private void OnTouchSelectionHandlePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSelectionHandle || e.Pointer.PointerId != _handleDragPointerId)
        {
            return;
        }

        e.Handled = true;
        if (ScrollViewerContentRoot is not null)
        {
            Point pointerRoot = e.GetCurrentPoint(ScrollViewerContentRoot).Position;
            _lastHandleTargetRootDip = new Point(
                pointerRoot.X + _handleGrabOffsetRootDip.X,
                pointerRoot.Y + _handleGrabOffsetRootDip.Y);
        }

        FinishTouchSelectionHandleDrag(e.Pointer);
    }

    private void OnTouchSelectionHandlePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingSelectionHandle && e.Pointer.PointerId == _handleDragPointerId)
        {
            FinishTouchSelectionHandleDrag(e.Pointer);
        }
    }

    private void OnTouchSelectionHandlePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingSelectionHandle && e.Pointer.PointerId == _handleDragPointerId)
        {
            FinishTouchSelectionHandleDrag(null);
        }
    }

    private void FinishTouchSelectionHandleDrag(Pointer? pointerToRelease)
    {
        if (!_isDraggingSelectionHandle)
        {
            return;
        }

        StopSelectionEdgeAutoScroll();

        UIElement? handleElem = _handleDragElement;
        Point finalTargetRoot = _lastHandleTargetRootDip;
        long activeSession = _crossPageSelectionSessionId;

        _isDraggingSelectionHandle = false;
        _handleDragTargetCanvas = null;
        _handleDragElement = null;
        _selDragSourceCanvas = null;
        _selDragSourceSessionId = 0;

        if (handleElem is not null && pointerToRelease is not null)
        {
            try
            {
                handleElem.ReleasePointerCapture(pointerToRelease);
            }
            catch
            {
            }
        }

        SyncPagedScrollViewerModes();
        UpdateTouchSelectionHandlesLayout();

        _ = FinalizeCrossPageSelectionAsync(finalTargetRoot, activeSession, showTouchFlyout: true);
    }

    private void CancelTouchSelectionHandleDragImmediate()
    {
        StopSelectionEdgeAutoScroll();
        _crossPageSelectionSessionId++;
        _isCrossPageSelectionInFlight = false;
        _hasPendingCrossPageSelection = false;
        _isDraggingSelectionHandle = false;
        _isMouseCrossPageDragActive = false;
        _handleDragTargetCanvas = null;
        _handleDragElement = null;
        _selDragSourceCanvas = null;
        _selDragSourceSessionId = 0;
        _isTouchSelectionHandlesActive = false;
        HideTouchSelectionFlyout();
        if (TouchSelectionHandlesCanvas is not null)
        {
            TouchSelectionHandlesCanvas.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowTouchSelectionCopyFlyout(PdfTextSelection selection, PdfPageCanvas? fallbackCanvas = null)
    {
        if (TouchSelectionCommandBarFlyout is null ||
            ScrollViewerContentRoot is null ||
            !selection.HasContent ||
            ViewModel?.IsPresentationMode == true)
        {
            return;
        }

        try
        {
            double rootW = ScrollViewerContentRoot.ActualWidth;
            double rootH = ScrollViewerContentRoot.ActualHeight;
            if (rootW <= 0 || rootH <= 0)
            {
                return;
            }

            double visMinX = double.MaxValue;
            double visMaxX = double.MinValue;
            double visMinY = double.MaxValue;
            double visMaxY = double.MinValue;
            bool hasVisibleRect = false;

            double allMinX = double.MaxValue;
            double allMaxX = double.MinValue;
            double allMinY = double.MaxValue;
            double allMaxY = double.MinValue;

            int pageCount = selection.Pages is { Count: > 0 } ? selection.Pages.Count : 1;
            for (int p = 0; p < pageCount; p++)
            {
                int pageIndex = selection.Pages is { Count: > 0 } ? selection.Pages[p].PageIndex : selection.PageIndex;
                IReadOnlyList<PdfRect> rects = selection.Pages is { Count: > 0 } ? selection.Pages[p].Rects : selection.Rects;
                PdfPageCanvas? canvas = FindRealizedCanvasForPage(pageIndex) ??
                    (fallbackCanvas?.PageInfo?.Index == pageIndex ? fallbackCanvas : null);
                if (canvas?.PageInfo is not { } pageInfo)
                {
                    continue;
                }

                PageCoordinateTransform pageTransform = PageCoordinateTransform.FromPage(pageInfo, canvas.RotationAngle);
                GeneralTransform toRoot = canvas.TransformToVisual(ScrollViewerContentRoot);

                for (int i = 0; i < rects.Count; i++)
                {
                    Rect pageDipRect = pageTransform.ToPageRect(rects[i]);
                    Rect rootRect = toRoot.TransformBounds(pageDipRect);

                    allMinX = Math.Min(allMinX, rootRect.Left);
                    allMaxX = Math.Max(allMaxX, rootRect.Right);
                    allMinY = Math.Min(allMinY, rootRect.Top);
                    allMaxY = Math.Max(allMaxY, rootRect.Bottom);

                    if (rootRect.Right < 0 || rootRect.Left > rootW || rootRect.Bottom < 0 || rootRect.Top > rootH)
                    {
                        continue;
                    }

                    hasVisibleRect = true;
                    visMinX = Math.Min(visMinX, rootRect.Left);
                    visMaxX = Math.Max(visMaxX, rootRect.Right);
                    visMinY = Math.Min(visMinY, rootRect.Top);
                    visMaxY = Math.Max(visMaxY, rootRect.Bottom);
                }
            }

            double minX = hasVisibleRect ? visMinX : allMinX;
            double maxX = hasVisibleRect ? visMaxX : allMaxX;
            double minY = hasVisibleRect ? visMinY : allMinY;
            double maxY = hasVisibleRect ? visMaxY : allMaxY;

            if (minX > maxX || minY > maxY)
            {
                return;
            }

            double centerX = Math.Clamp((minX + maxX) / 2.0, 32.0, Math.Max(32.0, rootW - 32.0));
            bool placeBelow = minY < 56.0 && (maxY + 56.0 <= rootH);
            double anchorY = placeBelow
                ? Math.Clamp(maxY + 8.0, 12.0, Math.Max(12.0, rootH - 12.0))
                : Math.Clamp(minY - 8.0, 12.0, Math.Max(12.0, rootH - 12.0));

            if (TouchSelectionCopyButton is not null)
            {
                string? copyLabel = ContextCopyMenuItem?.Text;
                if (string.IsNullOrWhiteSpace(copyLabel))
                {
                    copyLabel = ResourceStrings.Get("Context_Copy/Text");
                    if (string.IsNullOrWhiteSpace(copyLabel) || copyLabel.StartsWith('['))
                    {
                        copyLabel = "Copy";
                    }
                }

                ToolTipService.SetToolTip(TouchSelectionCopyButton, copyLabel);
            }

            SetVisible(TouchSelectionPasteButton, _copiedAnnotation is not null);
            _lastTouchSelectionCompletedTick = Environment.TickCount64;
            HideTouchSelectionFlyout();

            var showOptions = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = new Point(centerX, anchorY),
                Placement = placeBelow
                    ? Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom
                    : Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top,
                ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Transient,
            };
            TouchSelectionCommandBarFlyout.ShowAt(ScrollViewerContentRoot, showOptions);
            _isTouchSelectionFlyoutOpen = true;
        }
        catch
        {
        }
    }

    private PdfPageCanvas? FindCanvasWaitingForTouchHold()
    {
        foreach (PdfPageCanvas canvas in _activeCanvases)
        {
            if (canvas.IsTouchWaitingForHold)
            {
                return canvas;
            }
        }

        return null;
    }

    private void CancelTouchHoldOnAllCanvases()
    {
        foreach (PdfPageCanvas canvas in _activeCanvases)
        {
            if (canvas.IsTouchWaitingForHold)
            {
                canvas.CancelPendingTouchHold();
            }
        }
    }

    private void PruneStaleTouchPointers(long nowTick)
    {
        if (_activeTouchPointers.Count == 0)
        {
            return;
        }

        List<uint>? stale = null;
        foreach (var kvp in _activeTouchPointers)
        {
            bool stillDown = GetPointerInfo(kvp.Key, out POINTER_INFO pi) &&
                             (pi.pointerFlags & POINTER_FLAG_INCONTACT) != 0 &&
                             (pi.pointerFlags & 0x00040000u) == 0;
            if (!stillDown)
            {
                (stale ??= new List<uint>()).Add(kvp.Key);
            }
        }

        if (stale is not null)
        {
            foreach (uint id in stale)
            {
                _activeTouchPointers.Remove(id);
            }
        }
    }

    private bool IsAnyTouchPointerStillInContact()
    {
        foreach (var kvp in _activeTouchPointers)
        {
            if (GetPointerInfo(kvp.Key, out POINTER_INFO pi) &&
                (pi.pointerFlags & POINTER_FLAG_INCONTACT) != 0 &&
                (pi.pointerFlags & 0x00040000u) == 0)
            {
                return true;
            }
        }

        return false;
    }

}
