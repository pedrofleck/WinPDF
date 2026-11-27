using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using WinPDF.Controls;
using WinPDF.Models;
using WinPDF.Rendering;
using WinPDF.ViewModels;

namespace WinPDF.Views;

/// <summary>
/// Freehand Pen/Ink drawing, Eraser scrubbing, S Pen hover cursor, pressure sensitivity,
/// palm rejection, and simultaneous hand pan/zoom on the dedicated <c>TouchInkDrawOverlay</c>.
/// </summary>
public sealed partial class DocumentView
{
    private readonly record struct EraserSegmentJob(
        int PageIndex,
        Vector2 SegA,
        Vector2 SegB,
        float RadiusPt,
        PdfEraserMode EraserMode,
        long SessionId);

    private const double PalmContactSizeThresholdDip = 28.0;
    private const double PenPalmExclusionRadiusDip = 150.0;
    private const double TouchPanEngageThresholdDip = 6.0;

    private bool _isInkOverlayDrawing;
    private bool _isInkOverlayErasing;
    private uint _inkOverlayDrawPointerId;
    private PointerDeviceType _inkOverlayDrawDeviceType;
    private PdfPageCanvas? _inkOverlayDrawCanvas;
    private Point _inkOverlayLastEraserDip;
    private bool _hasInkOverlayLastEraserDip;
    private PdfPageCanvas? _inkOverlayHoverCanvas;
    private long _currentEraserSessionId;

    private bool _isPenInContact;
    private uint _activePenPointerId;
    private long _lastPenActivityTick;
    private Point _lastPenOverlayPos;
    private bool _hasLastPenOverlayPos;

    private readonly Dictionary<uint, Point> _inkOverlayTouchPointers = new();
    private readonly Dictionary<uint, Point> _inkOverlayTouchPressOrigins = new();
    private readonly HashSet<uint> _inkOverlayRejectedPalms = new();
    private bool _isInkOverlayTouchPanning;
    private bool _inkOverlayPanEngaged;
    private uint _inkOverlayPanPointerId;
    private Point _inkOverlayPanLastPos;
    private double _inkOverlayPanAccumX;
    private double _inkOverlayPanAccumY;
    private bool _isInkOverlayTwoFingerNavigating;
    private Point _inkOverlayPrevMidpoint;
    private double _inkOverlayPrevSpan;
    private bool _isReleasingInkOverlayCapture;

    private readonly Queue<EraserSegmentJob> _eraserSegmentQueue = new();
    private bool _isDrainingEraserQueue;

    private void SyncTouchInkDrawOverlayVisibility()
    {
        if (TouchInkDrawOverlay is null)
        {
            return;
        }

        bool shouldShow = ViewModel?.ActiveTool is AnnotationTool.Pen or AnnotationTool.Eraser &&
                          ViewModel.IsPresentationMode != true &&
                          !_isInlineTextEditorOpen &&
                          ViewModel.CurrentDocument is { Pages.Count: > 0 };

        if (shouldShow)
        {
            if (TouchInkDrawOverlay.Visibility != Visibility.Visible)
            {
                TouchInkDrawOverlay.Visibility = Visibility.Visible;
            }
        }
        else
        {
            ResetTouchInkOverlayState();
            if (TouchInkDrawOverlay.Visibility != Visibility.Collapsed)
            {
                TouchInkDrawOverlay.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ResetTouchInkOverlayState()
    {
        _inkOverlayDrawCanvas?.CancelExternalInkStroke();
        ClearPenHoverCursorOnAllCanvases();
        _isInkOverlayDrawing = false;
        _isInkOverlayErasing = false;
        _inkOverlayDrawPointerId = 0;
        _inkOverlayDrawCanvas = null;
        _hasInkOverlayLastEraserDip = false;
        _inkOverlayHoverCanvas = null;
        _isPenInContact = false;
        _activePenPointerId = 0;
        _isInkOverlayTouchPanning = false;
        _inkOverlayPanEngaged = false;
        _inkOverlayPanPointerId = 0;
        _isInkOverlayTwoFingerNavigating = false;
        _inkOverlayTouchPointers.Clear();
        _inkOverlayTouchPressOrigins.Clear();
        _inkOverlayRejectedPalms.Clear();
        _eraserSegmentQueue.Clear();
    }

    private void ClearPenHoverCursorOnAllCanvases()
    {
        _inkOverlayHoverCanvas?.ClearPenHoverCursor();
        _inkOverlayHoverCanvas = null;
        foreach (PdfPageCanvas canvas in _activeCanvases)
        {
            canvas.ClearPenHoverCursor();
        }
    }

    private static bool IsPalmTouchContact(PointerPoint pt)
    {
        Rect cr = pt.Properties.ContactRect;
        return cr.Width > PalmContactSizeThresholdDip || cr.Height > PalmContactSizeThresholdDip;
    }

    private bool IsTouchNearActivePen(Point overlayPos, long nowTick)
    {
        if (!_hasLastPenOverlayPos)
        {
            return false;
        }

        if (!_isPenInContact && (nowTick - _lastPenActivityTick) > 420)
        {
            return false;
        }

        return double.Hypot(overlayPos.X - _lastPenOverlayPos.X, overlayPos.Y - _lastPenOverlayPos.Y) < PenPalmExclusionRadiusDip;
    }

    private bool IsEffectiveEraserMode(PointerPoint pt)
    {
        if (ViewModel?.ActiveTool == AnnotationTool.Eraser)
        {
            return true;
        }

        return pt.PointerDeviceType == PointerDeviceType.Pen &&
               (pt.Properties.IsEraser || pt.Properties.IsBarrelButtonPressed);
    }

    private void UpdateInkOverlayHoverVisual(Point overlayPos, PointerPoint pt)
    {
        if (TouchInkDrawOverlay is null || ViewModel?.CurrentDocument is not { } doc)
        {
            return;
        }

        bool isEraser = IsEffectiveEraserMode(pt);
        if (TryFindRealizedCanvasAtVisualPoint(TouchInkDrawOverlay, overlayPos, out PdfPageCanvas? hitCanvas, out Point localPt) &&
            hitCanvas is not null)
        {
            if (_inkOverlayHoverCanvas is not null && !ReferenceEquals(_inkOverlayHoverCanvas, hitCanvas))
            {
                _inkOverlayHoverCanvas.ClearPenHoverCursor();
            }

            _inkOverlayHoverCanvas = hitCanvas;
            double? svZoom = ContinuousScrollViewer is { ZoomFactor: > 0.05f } sv ? sv.ZoomFactor : null;
            hitCanvas.UpdatePenHoverCursor(
                localPt,
                isEraser,
                ViewModel.StrokeThicknessPt,
                doc.ActiveAnnotationColor,
                ViewModel.ActiveEraserMode,
                svZoom);
        }
        else if (_inkOverlayHoverCanvas is not null)
        {
            _inkOverlayHoverCanvas.ClearPenHoverCursor();
            _inkOverlayHoverCanvas = null;
        }
    }

    private void CommitInkOverlayNavigation()
    {
        bool wasNav = _isInkOverlayTwoFingerNavigating || _isInkOverlayTouchPanning;
        _isInkOverlayTwoFingerNavigating = false;
        _isInkOverlayTouchPanning = false;
        _inkOverlayPanEngaged = false;
        _inkOverlayPanPointerId = 0;

        if (!wasNav || ViewModel?.CurrentDocument is not { } doc || ContinuousScrollViewer is null)
        {
            return;
        }

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

    private void OnInkOverlayPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (TouchInkDrawOverlay is null || ViewModel?.ActiveTool is not (AnnotationTool.Pen or AnnotationTool.Eraser))
        {
            return;
        }

        PointerPoint pt = e.GetCurrentPoint(TouchInkDrawOverlay);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
        {
            ViewModel.NotifyPenDetected();
            _lastPenActivityTick = Environment.TickCount64;
            _lastPenOverlayPos = pt.Position;
            _hasLastPenOverlayPos = true;
            if (!pt.IsInContact)
            {
                UpdateInkOverlayHoverVisual(pt.Position, pt);
            }
        }
        else if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            UpdateInkOverlayHoverVisual(pt.Position, pt);
        }
    }

    private void OnInkOverlayPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
        {
            if (!_isInkOverlayDrawing && !_isInkOverlayErasing)
            {
                ClearPenHoverCursorOnAllCanvases();
            }
        }
        else if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            if (!_isInkOverlayDrawing && !_isInkOverlayErasing)
            {
                ClearPenHoverCursorOnAllCanvases();
            }
        }
    }

    private void OnInkOverlayPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (TouchInkDrawOverlay is null ||
            ViewModel?.ActiveTool is not (AnnotationTool.Pen or AnnotationTool.Eraser) ||
            ViewModel.CurrentDocument is not { } doc)
        {
            return;
        }

        PointerPoint pt = e.GetCurrentPoint(TouchInkDrawOverlay);
        PointerDeviceType devType = e.Pointer.PointerDeviceType;
        uint pid = e.Pointer.PointerId;
        long nowTick = Environment.TickCount64;

        if (devType == PointerDeviceType.Touch)
        {
            _isLastPointerInputTouch = true;

            if (IsPalmTouchContact(pt))
            {
                _inkOverlayRejectedPalms.Add(pid);
                e.Handled = true;
                return;
            }

            // Reject resting palm near the active pen tip while still allowing the opposite hand (>150 DIPs away) to pan/zoom.
            bool nearPen = IsTouchNearActivePen(pt.Position, nowTick);
            bool penRecentlyActive = _isPenInContact || (nowTick - _lastPenActivityTick) < 350;
            bool allowTouchDraw = (!ViewModel.HasPenDigitizer || ViewModel.IsTouchDrawingEnabled) && !penRecentlyActive && !nearPen;

            if (nearPen && !_isInkOverlayTwoFingerNavigating && _inkOverlayTouchPointers.Count == 0)
            {
                // Single contact resting right beside the active pen nib -> palm rejection (neither draw nor pan).
                _inkOverlayRejectedPalms.Add(pid);
                e.Handled = true;
                return;
            }

            HideAnnotationPopupsImmediate();
            if (_isSlideSwipeActive || _isSlideSwipeAnimating)
            {
                CancelInteractiveSlideSwipeImmediate();
            }

            _inkOverlayTouchPointers[pid] = pt.Position;
            _inkOverlayTouchPressOrigins[pid] = pt.Position;

            try
            {
                _ = TouchInkDrawOverlay.CapturePointer(e.Pointer);
            }
            catch
            {
            }

            if (_inkOverlayTouchPointers.Count == 1)
            {
                if (allowTouchDraw && !_isInkOverlayDrawing && !_isInkOverlayErasing)
                {
                    StartInkOrEraserStrokeOnOverlay(pid, devType, pt, doc, isEraser: ViewModel.ActiveTool == AnnotationTool.Eraser);
                }
                else
                {
                    // Hand navigation mode (IsTouchDrawingEnabled == false, or opposite hand panning while pen is active).
                    _isInkOverlayTouchPanning = true;
                    _inkOverlayPanEngaged = false;
                    _inkOverlayPanPointerId = pid;
                    _inkOverlayPanLastPos = pt.Position;
                    _inkOverlayPanAccumX = 0.0;
                    _inkOverlayPanAccumY = 0.0;
                    EnsureInteractiveViewportStateInitialized(doc);
                }
            }
            else if (_inkOverlayTouchPointers.Count >= 2)
            {
                // Two non-palm fingers: cancel any accidental touch ink/erase stroke and switch smoothly to pan + pinch-zoom.
                if ((_isInkOverlayDrawing || _isInkOverlayErasing) && _inkOverlayDrawDeviceType == PointerDeviceType.Touch)
                {
                    CancelActiveInkOrEraserStroke();
                }

                _isInkOverlayTouchPanning = false;
                _inkOverlayPanEngaged = false;
                _inkOverlayPanPointerId = 0;

                if (_inkOverlayTouchPointers.Count == 2)
                {
                    using var it = _inkOverlayTouchPointers.Values.GetEnumerator();
                    it.MoveNext();
                    Point p1 = it.Current;
                    it.MoveNext();
                    Point p2 = it.Current;
                    _inkOverlayPrevMidpoint = new Point((p1.X + p2.X) * 0.5, (p1.Y + p2.Y) * 0.5);
                    _inkOverlayPrevSpan = Math.Max(10.0, double.Hypot(p2.X - p1.X, p2.Y - p1.Y));
                    _isInkOverlayTwoFingerNavigating = true;
                    EnsureInteractiveViewportStateInitialized(doc);
                }
            }

            e.Handled = true;
            return;
        }

        // Pen or Mouse input
        _isLastPointerInputTouch = false;

        if (devType == PointerDeviceType.Mouse)
        {
            if (pt.Properties.IsRightButtonPressed)
            {
                if (_isInkOverlayDrawing || _isInkOverlayErasing)
                {
                    CancelActiveInkOrEraserStroke();
                }

                e.Handled = true;
                return;
            }

            if (!pt.Properties.IsLeftButtonPressed)
            {
                return;
            }
        }
        else if (devType == PointerDeviceType.Pen)
        {
            ViewModel.NotifyPenDetected();
            _isPenInContact = true;
            _activePenPointerId = pid;
            _lastPenActivityTick = nowTick;
            _lastPenOverlayPos = pt.Position;
            _hasLastPenOverlayPos = true;

            // If a resting touch accidentally began a touch stroke just milliseconds before the pen touched down, cancel it immediately.
            if ((_isInkOverlayDrawing || _isInkOverlayErasing) && _inkOverlayDrawDeviceType == PointerDeviceType.Touch)
            {
                CancelActiveInkOrEraserStroke();
            }

            // Also reject any single touch pan that was resting near the pen tip.
            if (_isInkOverlayTouchPanning &&
                _inkOverlayTouchPointers.TryGetValue(_inkOverlayPanPointerId, out Point panPos) &&
                double.Hypot(panPos.X - pt.Position.X, panPos.Y - pt.Position.Y) < PenPalmExclusionRadiusDip)
            {
                _inkOverlayRejectedPalms.Add(_inkOverlayPanPointerId);
                _inkOverlayTouchPointers.Remove(_inkOverlayPanPointerId);
                _inkOverlayTouchPressOrigins.Remove(_inkOverlayPanPointerId);
                _isInkOverlayTouchPanning = false;
                _inkOverlayPanEngaged = false;
                _inkOverlayPanPointerId = 0;
            }
        }

        HideAnnotationPopupsImmediate();
        try
        {
            _ = TouchInkDrawOverlay.CapturePointer(e.Pointer);
        }
        catch
        {
        }

        bool isEraser = IsEffectiveEraserMode(pt);
        StartInkOrEraserStrokeOnOverlay(pid, devType, pt, doc, isEraser);
        e.Handled = true;
    }

    private void StartInkOrEraserStrokeOnOverlay(
        uint pointerId,
        PointerDeviceType deviceType,
        PointerPoint pt,
        DocumentViewModel doc,
        bool isEraser)
    {
        if (TouchInkDrawOverlay is null)
        {
            return;
        }

        if (!TryFindRealizedCanvasAtVisualPoint(TouchInkDrawOverlay, pt.Position, out PdfPageCanvas? targetCanvas, out Point localPt) ||
            targetCanvas is null)
        {
            return;
        }

        _inkOverlayDrawPointerId = pointerId;
        _inkOverlayDrawDeviceType = deviceType;
        _inkOverlayDrawCanvas = targetCanvas;

        if (isEraser)
        {
            _isInkOverlayErasing = true;
            _isInkOverlayDrawing = false;
            _currentEraserSessionId++;
            _inkOverlayLastEraserDip = localPt;
            _hasInkOverlayLastEraserDip = true;
            _inkOverlayHoverCanvas = targetCanvas;
            double? svZoom = ContinuousScrollViewer is { ZoomFactor: > 0.05f } sv ? sv.ZoomFactor : null;
            targetCanvas.UpdatePenHoverCursor(
                localPt,
                isEraser: true,
                ViewModel.StrokeThicknessPt,
                doc.ActiveAnnotationColor,
                ViewModel.ActiveEraserMode,
                svZoom);
            EnqueueEraserSegment(targetCanvas, localPt, localPt);
        }
        else
        {
            _isInkOverlayDrawing = true;
            _isInkOverlayErasing = false;
            _hasInkOverlayLastEraserDip = false;
            ClearPenHoverCursorOnAllCanvases();

            bool hasPressure = deviceType == PointerDeviceType.Pen;
            float pressure = hasPressure ? pt.Properties.Pressure : 0.5f;
            targetCanvas.BeginExternalInkStroke(
                localPt,
                pressure,
                hasPressure,
                doc.ActiveAnnotationColor,
                (float)ViewModel.StrokeThicknessPt);
        }
    }

    private void OnInkOverlayPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (TouchInkDrawOverlay is null ||
            ViewModel?.ActiveTool is not (AnnotationTool.Pen or AnnotationTool.Eraser) ||
            ViewModel.CurrentDocument is not { } doc)
        {
            return;
        }

        PointerPoint pt = e.GetCurrentPoint(TouchInkDrawOverlay);
        PointerDeviceType devType = e.Pointer.PointerDeviceType;
        uint pid = e.Pointer.PointerId;
        long nowTick = Environment.TickCount64;

        if (devType == PointerDeviceType.Touch)
        {
            if (_inkOverlayRejectedPalms.Contains(pid))
            {
                e.Handled = true;
                return;
            }

            if (IsPalmTouchContact(pt))
            {
                _inkOverlayRejectedPalms.Add(pid);
                _inkOverlayTouchPointers.Remove(pid);
                _inkOverlayTouchPressOrigins.Remove(pid);
                if ((_isInkOverlayDrawing || _isInkOverlayErasing) && pid == _inkOverlayDrawPointerId)
                {
                    CancelActiveInkOrEraserStroke();
                }

                if (_isInkOverlayTouchPanning && pid == _inkOverlayPanPointerId)
                {
                    _isInkOverlayTouchPanning = false;
                    _inkOverlayPanEngaged = false;
                    _inkOverlayPanPointerId = 0;
                }

                e.Handled = true;
                return;
            }

            if (_inkOverlayTouchPointers.ContainsKey(pid))
            {
                _inkOverlayTouchPointers[pid] = pt.Position;
            }

            if (_isInkOverlayTwoFingerNavigating && _inkOverlayTouchPointers.Count >= 2 && ContinuousScrollViewer is not null)
            {
                using var it = _inkOverlayTouchPointers.Values.GetEnumerator();
                it.MoveNext();
                Point p1 = it.Current;
                it.MoveNext();
                Point p2 = it.Current;
                Point mid = new((p1.X + p2.X) * 0.5, (p1.Y + p2.Y) * 0.5);
                double span = Math.Max(10.0, double.Hypot(p2.X - p1.X, p2.Y - p1.Y));
                double dx = mid.X - _inkOverlayPrevMidpoint.X;
                double dy = mid.Y - _inkOverlayPrevMidpoint.Y;
                double scaleDelta = _inkOverlayPrevSpan > 1.0 ? Math.Clamp(span / _inkOverlayPrevSpan, 0.75, 1.35) : 1.0;
                _inkOverlayPrevMidpoint = mid;
                _inkOverlayPrevSpan = span;

                try
                {
                    Point midInSv = TouchInkDrawOverlay.TransformToVisual(ContinuousScrollViewer).TransformPoint(mid);
                    ApplyInteractiveZoomAroundPoint(midInSv, scaleDelta, dx, dy, commitZoomProperty: false);
                }
                catch
                {
                }

                e.Handled = true;
                return;
            }

            if (_isInkOverlayTouchPanning && pid == _inkOverlayPanPointerId)
            {
                double dx = pt.Position.X - _inkOverlayPanLastPos.X;
                double dy = pt.Position.Y - _inkOverlayPanLastPos.Y;
                _inkOverlayPanLastPos = pt.Position;
                _inkOverlayPanAccumX += dx;
                _inkOverlayPanAccumY += dy;

                if (!_inkOverlayPanEngaged)
                {
                    if (_inkOverlayTouchPressOrigins.TryGetValue(pid, out Point origin) &&
                        double.Hypot(pt.Position.X - origin.X, pt.Position.Y - origin.Y) >= TouchPanEngageThresholdDip)
                    {
                        _inkOverlayPanEngaged = true;
                    }
                }

                if (_inkOverlayPanEngaged)
                {
                    PanPagedScrollViewerBy(dx, dy);
                }

                e.Handled = true;
                return;
            }

            if ((_isInkOverlayDrawing || _isInkOverlayErasing) && pid == _inkOverlayDrawPointerId)
            {
                ProcessActiveInkOrEraserMove(e, pt, devType, doc);
                e.Handled = true;
            }

            return;
        }

        // Pen or Mouse hover / active stroke
        if (devType == PointerDeviceType.Pen)
        {
            _lastPenActivityTick = nowTick;
            _lastPenOverlayPos = pt.Position;
            _hasLastPenOverlayPos = true;
        }

        if ((_isInkOverlayDrawing || _isInkOverlayErasing) && pid == _inkOverlayDrawPointerId)
        {
            ProcessActiveInkOrEraserMove(e, pt, devType, doc);
            e.Handled = true;
            return;
        }

        // Hover preview (0% idle GPU — PdfPageCanvas only invalidates if coordinates actually moved)
        if (!pt.IsInContact)
        {
            UpdateInkOverlayHoverVisual(pt.Position, pt);
        }
    }

    private void ProcessActiveInkOrEraserMove(
        PointerRoutedEventArgs e,
        PointerPoint currentPt,
        PointerDeviceType devType,
        DocumentViewModel doc)
    {
        if (TouchInkDrawOverlay is null || _inkOverlayDrawCanvas is not { } canvas)
        {
            return;
        }

        Microsoft.UI.Xaml.Media.GeneralTransform overlayToCanvas;
        try
        {
            overlayToCanvas = TouchInkDrawOverlay.TransformToVisual(canvas);
        }
        catch
        {
            return;
        }

        IList<PointerPoint>? intermediates = null;
        try
        {
            intermediates = e.GetIntermediatePoints(TouchInkDrawOverlay);
        }
        catch
        {
        }

        if (_isInkOverlayErasing)
        {
            // WinUI GetIntermediatePoints returns newest-first; iterate backwards for chronological order.
            if (intermediates is { Count: > 0 })
            {
                for (int i = intermediates.Count - 1; i >= 0; i--)
                {
                    Point dip = overlayToCanvas.TransformPoint(intermediates[i].Position);
                    Point prev = _hasInkOverlayLastEraserDip ? _inkOverlayLastEraserDip : dip;
                    EnqueueEraserSegment(canvas, prev, dip);
                    _inkOverlayLastEraserDip = dip;
                    _hasInkOverlayLastEraserDip = true;
                }
            }
            else
            {
                Point dip = overlayToCanvas.TransformPoint(currentPt.Position);
                Point prev = _hasInkOverlayLastEraserDip ? _inkOverlayLastEraserDip : dip;
                EnqueueEraserSegment(canvas, prev, dip);
                _inkOverlayLastEraserDip = dip;
                _hasInkOverlayLastEraserDip = true;
            }

            double? svZoom = ContinuousScrollViewer is { ZoomFactor: > 0.05f } sv ? sv.ZoomFactor : null;
            canvas.UpdatePenHoverCursor(
                _inkOverlayLastEraserDip,
                isEraser: true,
                ViewModel.StrokeThicknessPt,
                doc.ActiveAnnotationColor,
                ViewModel.ActiveEraserMode,
                svZoom);
            return;
        }

        if (_isInkOverlayDrawing)
        {
            bool hasPressure = devType == PointerDeviceType.Pen;
            List<PdfInkSample> batch;
            if (intermediates is { Count: > 0 })
            {
                batch = new List<PdfInkSample>(intermediates.Count);
                for (int i = intermediates.Count - 1; i >= 0; i--)
                {
                    PointerPoint samplePt = intermediates[i];
                    Point dip = overlayToCanvas.TransformPoint(samplePt.Position);
                    float pressure = hasPressure ? samplePt.Properties.Pressure : 0.5f;
                    batch.Add(new PdfInkSample((float)dip.X, (float)dip.Y, pressure));
                }
            }
            else
            {
                Point dip = overlayToCanvas.TransformPoint(currentPt.Position);
                float pressure = hasPressure ? currentPt.Properties.Pressure : 0.5f;
                batch = [new PdfInkSample((float)dip.X, (float)dip.Y, pressure)];
            }

            canvas.AppendExternalInkSamples(batch);
        }
    }

    private void OnInkOverlayPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (TouchInkDrawOverlay is null)
        {
            return;
        }

        uint pid = e.Pointer.PointerId;
        PointerDeviceType devType = e.Pointer.PointerDeviceType;

        _isReleasingInkOverlayCapture = true;
        try
        {
            TouchInkDrawOverlay.ReleasePointerCapture(e.Pointer);
        }
        catch
        {
        }
        finally
        {
            _isReleasingInkOverlayCapture = false;
        }

        if (devType == PointerDeviceType.Touch)
        {
            _inkOverlayRejectedPalms.Remove(pid);
            _inkOverlayTouchPointers.Remove(pid);
            _inkOverlayTouchPressOrigins.Remove(pid);

            if ((_isInkOverlayDrawing || _isInkOverlayErasing) && pid == _inkOverlayDrawPointerId)
            {
                CompleteActiveInkOrEraserStroke();
            }
            else if (_isInkOverlayTwoFingerNavigating && _inkOverlayTouchPointers.Count < 2)
            {
                CommitInkOverlayNavigation();
            }
            else if (_isInkOverlayTouchPanning && pid == _inkOverlayPanPointerId)
            {
                double accumX = _inkOverlayPanAccumX;
                double accumY = _inkOverlayPanAccumY;
                bool engaged = _inkOverlayPanEngaged;
                CommitInkOverlayNavigation();

                // In Paged mode when not horizontally scrollable, allow clean 1-finger horizontal swipe to flip pages.
                if (engaged &&
                    ViewModel?.CurrentDocument is { IsContinuous: false } pagedDoc &&
                    ContinuousScrollViewer is not null &&
                    ContinuousScrollViewer.ScrollableWidth <= 2.0 &&
                    Math.Abs(accumX) >= 85.0 &&
                    Math.Abs(accumX) > Math.Abs(accumY) * 1.45)
                {
                    if (accumX > 0)
                    {
                        pagedDoc.PreviousPage();
                    }
                    else
                    {
                        pagedDoc.NextPage();
                    }

                    if (_isFittedVertically)
                    {
                        DispatcherQueue.TryEnqueue(() => ExecuteFitVertical());
                    }
                }
            }

            e.Handled = true;
            return;
        }

        if (devType == PointerDeviceType.Pen && pid == _activePenPointerId)
        {
            _isPenInContact = false;
            _activePenPointerId = 0;
            _lastPenActivityTick = Environment.TickCount64;
        }

        if ((_isInkOverlayDrawing || _isInkOverlayErasing) && pid == _inkOverlayDrawPointerId)
        {
            CompleteActiveInkOrEraserStroke();
            PointerPoint pt = e.GetCurrentPoint(TouchInkDrawOverlay);
            UpdateInkOverlayHoverVisual(pt.Position, pt);
            e.Handled = true;
        }
    }

    private void OnInkOverlayPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_isReleasingInkOverlayCapture)
        {
            return;
        }

        HandleInkOverlayPointerInterrupted(e.Pointer.PointerId, e.Pointer.PointerDeviceType);
    }

    private void OnInkOverlayPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isReleasingInkOverlayCapture)
        {
            return;
        }

        HandleInkOverlayPointerInterrupted(e.Pointer.PointerId, e.Pointer.PointerDeviceType);
    }

    private void HandleInkOverlayPointerInterrupted(uint pid, PointerDeviceType devType)
    {
        if (devType == PointerDeviceType.Touch)
        {
            _inkOverlayRejectedPalms.Remove(pid);
            _inkOverlayTouchPointers.Remove(pid);
            _inkOverlayTouchPressOrigins.Remove(pid);

            if ((_isInkOverlayDrawing || _isInkOverlayErasing) && pid == _inkOverlayDrawPointerId)
            {
                CompleteActiveInkOrEraserStroke();
            }
            else if (_isInkOverlayTwoFingerNavigating && _inkOverlayTouchPointers.Count < 2)
            {
                CommitInkOverlayNavigation();
            }
            else if (_isInkOverlayTouchPanning && pid == _inkOverlayPanPointerId)
            {
                CommitInkOverlayNavigation();
            }

            return;
        }

        if (devType == PointerDeviceType.Pen && pid == _activePenPointerId)
        {
            _isPenInContact = false;
            _activePenPointerId = 0;
            _lastPenActivityTick = Environment.TickCount64;
        }

        if ((_isInkOverlayDrawing || _isInkOverlayErasing) && pid == _inkOverlayDrawPointerId)
        {
            CompleteActiveInkOrEraserStroke();
        }
    }

    private void CancelActiveInkOrEraserStroke()
    {
        _inkOverlayDrawCanvas?.CancelExternalInkStroke();
        _isInkOverlayDrawing = false;
        _isInkOverlayErasing = false;
        _inkOverlayDrawPointerId = 0;
        _inkOverlayDrawCanvas = null;
        _hasInkOverlayLastEraserDip = false;
    }

    private void CompleteActiveInkOrEraserStroke()
    {
        PdfPageCanvas? canvas = _inkOverlayDrawCanvas;
        bool wasDrawing = _isInkOverlayDrawing;

        _isInkOverlayDrawing = false;
        _isInkOverlayErasing = false;
        _inkOverlayDrawPointerId = 0;
        _inkOverlayDrawCanvas = null;
        _hasInkOverlayLastEraserDip = false;

        if (!wasDrawing || canvas?.PageInfo is not { } pageInfo || ViewModel?.CurrentDocument is not { } doc)
        {
            return;
        }

        if (!canvas.CompleteExternalInkStrokeToPending(out PdfInkStrokeData? pdfStroke, out int pendingToken) ||
            pdfStroke is null)
        {
            return;
        }

        PdfAnnotationColor color = doc.ActiveAnnotationColor;
        double widthPt = ViewModel.StrokeThicknessPt;
        _ = CommitInkStrokeAsync(doc, canvas, pageInfo.Index, pdfStroke, color, widthPt, pendingToken);
    }

    private async Task CommitInkStrokeAsync(
        DocumentViewModel doc,
        PdfPageCanvas canvas,
        int pageIndex,
        PdfInkStrokeData stroke,
        PdfAnnotationColor color,
        double widthPt,
        int pendingToken)
    {
        try
        {
            _ = await doc.AddInkAsync(pageIndex, [stroke], color, widthPt);
            // Keep the Win2D wet-ink preview visible briefly while PDFium uploads the refreshed page bitmap.
            await Task.Delay(95);
        }
        catch
        {
        }
        finally
        {
            canvas.ReleasePendingInkStroke(pendingToken);
        }
    }

    private void EnqueueEraserSegment(PdfPageCanvas canvas, Point dipA, Point dipB)
    {
        if (canvas.PageInfo is not { } pageInfo || ViewModel is null)
        {
            return;
        }

        PageCoordinateTransform transform = PageCoordinateTransform.FromPage(pageInfo, canvas.RotationAngle);
        Vector2 segA = transform.ToPdf(new Vector2((float)dipA.X, (float)dipA.Y));
        Vector2 segB = transform.ToPdf(new Vector2((float)dipB.X, (float)dipB.Y));
        double zoom = ContinuousScrollViewer is { ZoomFactor: > 0.05f } sv ? sv.ZoomFactor : canvas.ZoomFactor;
        PdfEraserMode mode = ViewModel.ActiveEraserMode;
        float radiusPt = PdfPageCanvas.ComputeEraserRadiusPdfPt(zoom, ViewModel.StrokeThicknessPt, mode);

        _eraserSegmentQueue.Enqueue(new EraserSegmentJob(
            pageInfo.Index,
            segA,
            segB,
            radiusPt,
            mode,
            _currentEraserSessionId));

        if (!_isDrainingEraserQueue)
        {
            _ = DrainEraserQueueAsync();
        }
    }

    private async Task DrainEraserQueueAsync()
    {
        if (_isDrainingEraserQueue)
        {
            return;
        }

        _isDrainingEraserQueue = true;
        try
        {
            while (_eraserSegmentQueue.Count > 0)
            {
                EraserSegmentJob job = _eraserSegmentQueue.Dequeue();
                if (ViewModel?.CurrentDocument is not { } doc)
                {
                    _eraserSegmentQueue.Clear();
                    break;
                }

                if (job.EraserMode == PdfEraserMode.Point)
                {
                    IReadOnlyList<PdfAnnotationInfo> hits = doc.FindAnnotationsHitByEraser(
                        job.PageIndex,
                        job.SegA,
                        job.SegB,
                        job.RadiusPt,
                        PdfEraserMode.Point);

                    for (int i = 0; i < hits.Count; i++)
                    {
                        PdfAnnotationInfo target = hits[i];
                        if (target.Strokes is { Count: > 0 })
                        {
                            List<PdfInkStrokeData> current = InkStrokeProcessor.ExtractInkStrokeData(target);
                            List<PdfInkStrokeData> remaining = InkStrokeProcessor.ErasePointFromStrokes(
                                current,
                                job.SegA,
                                job.SegB,
                                job.RadiusPt,
                                out bool modified);
                            if (!modified)
                            {
                                continue;
                            }

                            if (remaining.Count == 0)
                            {
                                await doc.DeleteAnnotationAsync(target.PageIndex, target.AnnotIndex, recordUndo: true, eraserSessionId: job.SessionId);
                            }
                            else
                            {
                                _ = await doc.ReplaceInkStrokesAsync(
                                    target.PageIndex,
                                    target.AnnotIndex,
                                    remaining,
                                    recordUndo: true,
                                    gestureSessionId: job.SessionId);
                            }
                        }
                        else
                        {
                            await doc.DeleteAnnotationAsync(target.PageIndex, target.AnnotIndex, recordUndo: true, eraserSessionId: job.SessionId);
                        }
                    }
                }
                else
                {
                    // Stroke mode: re-query live annotations before each full deletion so shifted indices never collide.
                    while (true)
                    {
                        IReadOnlyList<PdfAnnotationInfo> hits = doc.FindAnnotationsHitByEraser(
                            job.PageIndex,
                            job.SegA,
                            job.SegB,
                            job.RadiusPt,
                            PdfEraserMode.Stroke);
                        if (hits.Count == 0)
                        {
                            break;
                        }

                        PdfAnnotationInfo target = hits[0];
                        await doc.DeleteAnnotationAsync(target.PageIndex, target.AnnotIndex, recordUndo: true, eraserSessionId: job.SessionId);
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            _isDrainingEraserQueue = false;
        }
    }
}
