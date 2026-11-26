using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using WinPDF.Models;
using WinPDF.Rendering;
using WinPDF.ViewModels;

namespace WinPDF.Controls;

public sealed partial class PdfPageCanvas
{
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

        return shell.ActiveTool is AnnotationTool.Select or AnnotationTool.Highlighter or AnnotationTool.TextNote or AnnotationTool.InsertText or AnnotationTool.InsertImage or AnnotationTool.Shape;
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
            InputSystemCursorShape.SizeAll => s_sizeAllCursor,
            InputSystemCursorShape.Cross => s_crossCursor,
            InputSystemCursorShape.SizeNorthwestSoutheast => s_sizeNwSeCursor,
            InputSystemCursorShape.SizeNortheastSouthwest => s_sizeNeSwCursor,
            _ => s_arrowCursor,
        };
    }

    /// <summary>
    /// Converts a point in local page DIPs to PDF user-space coordinates (points).
    /// </summary>
    /// <param name="pageDipPoint">Point in local canvas DIPs.</param>
    /// <returns>The corresponding point in PDF user space.</returns>
    internal Vector2 ToPdfPoint(Point pageDipPoint)
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
        bool wasSelecting = _hasDraggedSelection || _isTouchSelecting;
        _selectionSessionId++;
        _isPointerDown = false;
        _hasDraggedSelection = false;
        _isReleasingCapture = false;
        _selectionAnchorCharIndex = -1;
        _lastDragEndCharIndex = -2;
        _pressedLink = PdfLinkTarget.None;
        _hasPendingHoverPoint = false;
        _hasPendingDragPoint = false;
        _isAwaitingInitialTouchWord = false;
        _touchHoldReleasedBeforeWordResolved = false;
        CancelTouchSelectionAndHold();
        _activeCanvasTouchPointerTicks.Clear();
        SetCursorShape(InputSystemCursorShape.Arrow);
        if (wasSelecting)
        {
            SelectionDragCanceled?.Invoke(this);
        }
    }


    private static bool IsShiftKeyDown() =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) &
         Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private Point ClampToCanvasDip(Point posDip)
    {
        if (PageInfo is null)
        {
            return posDip;
        }

        bool swap = PageCoordinateTransform.NormalizeRotation(PageInfo.Rotation + RotationAngle) is 90 or 270;
        double pageW = ActualWidth > 1.0 ? ActualWidth : (swap ? PageInfo.HeightPt : PageInfo.WidthPt) * PageCoordinateTransform.DipsPerPoint;
        double pageH = ActualHeight > 1.0 ? ActualHeight : (swap ? PageInfo.WidthPt : PageInfo.HeightPt) * PageCoordinateTransform.DipsPerPoint;
        return new Point(
            Math.Clamp(posDip.X, 0.0, Math.Max(1.0, pageW)),
            Math.Clamp(posDip.Y, 0.0, Math.Max(1.0, pageH)));
    }

    private Point ConstrainShapeEndPointDip(
        PdfAnnotationShapeKind kind,
        Point startDip,
        Point rawEndDip,
        bool constrain)
    {
        if (!constrain || PageInfo is null)
        {
            return ClampToCanvasDip(rawEndDip);
        }

        bool swap = PageCoordinateTransform.NormalizeRotation(PageInfo.Rotation + RotationAngle) is 90 or 270;
        double pageW = ActualWidth > 1.0 ? ActualWidth : (swap ? PageInfo.HeightPt : PageInfo.WidthPt) * PageCoordinateTransform.DipsPerPoint;
        double pageH = ActualHeight > 1.0 ? ActualHeight : (swap ? PageInfo.WidthPt : PageInfo.HeightPt) * PageCoordinateTransform.DipsPerPoint;
        double dx = rawEndDip.X - startDip.X;
        double dy = rawEndDip.Y - startDip.Y;

        if (kind is PdfAnnotationShapeKind.Line or PdfAnnotationShapeKind.Arrow)
        {
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            if (len < 0.5)
            {
                return startDip;
            }

            double snappedRad = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4.0)) * (Math.PI / 4.0);
            double ux = Math.Cos(snappedRad);
            double uy = Math.Sin(snappedRad);
            if (Math.Abs(ux) < 1e-6)
            {
                ux = 0.0;
            }
            if (Math.Abs(uy) < 1e-6)
            {
                uy = 0.0;
            }

            double maxLen = len;
            if (ux > 0.0)
            {
                maxLen = Math.Min(maxLen, Math.Max(0.0, (pageW - startDip.X) / ux));
            }
            else if (ux < 0.0)
            {
                maxLen = Math.Min(maxLen, Math.Max(0.0, startDip.X / (-ux)));
            }

            if (uy > 0.0)
            {
                maxLen = Math.Min(maxLen, Math.Max(0.0, (pageH - startDip.Y) / uy));
            }
            else if (uy < 0.0)
            {
                maxLen = Math.Min(maxLen, Math.Max(0.0, startDip.Y / (-uy)));
            }

            return new Point(startDip.X + (ux * maxLen), startDip.Y + (uy * maxLen));
        }

        double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        double sx = dx >= 0.0 ? 1.0 : -1.0;
        double sy = dy >= 0.0 ? 1.0 : -1.0;
        double maxSideX = sx > 0.0 ? Math.Max(0.0, pageW - startDip.X) : startDip.X;
        double maxSideY = sy > 0.0 ? Math.Max(0.0, pageH - startDip.Y) : startDip.Y;
        side = Math.Min(side, Math.Min(maxSideX, maxSideY));
        return new Point(startDip.X + (sx * side), startDip.Y + (sy * side));
    }

    internal void RefreshShapePreviewConstraint()
    {
        if (!_isDrawingShape || PageInfo is null)
        {
            return;
        }

        Point updated = ConstrainShapeEndPointDip(_shapeDrawKind, _shapeDrawStartDip, _shapeDrawRawEndDip, IsShiftKeyDown());
        if (Math.Abs(updated.X - _shapeDrawEndDip.X) > 0.2 || Math.Abs(updated.Y - _shapeDrawEndDip.Y) > 0.2)
        {
            _shapeDrawEndDip = updated;
            InvalidateOverlays();
        }
    }


    internal void CancelShapeDrawing()
    {
        if (!_isDrawingShape)
        {
            return;
        }

        bool wasTouch = _shapeDrawIsTouch;
        _isDrawingShape = false;
        _shapeDrawIsTouch = false;
        _shapeTouchPointerId = 0;
        if (wasTouch)
        {
            StopTouchSelectionPolling();
            if (_touchPointerRef is not null)
            {
                _isReleasingCapture = true;
                try
                {
                    ReleasePointerCapture(_touchPointerRef);
                }
                catch
                {
                }
                finally
                {
                    _isReleasingCapture = false;
                    _touchPointerRef = null;
                }
            }
        }

        InvalidateOverlays();
        if (wasTouch)
        {
            TouchShapeDrawEnded?.Invoke(this);
        }
    }


    private bool IsWithinMultiClickDistance(Point p1, Point p2)
    {
        double dx = p1.X - p2.X;
        double dy = p1.Y - p2.Y;
        return ((dx * dx) + (dy * dy)) <= (MultiClickMaxDistanceDip * MultiClickMaxDistanceDip);
    }


    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (PageInfo is not null)
        {
            PointerPoint rawPt = e.GetCurrentPoint(this);
            PointerDeviceType devType = e.Pointer.PointerDeviceType;
            if (devType == PointerDeviceType.Mouse)
            {
                if (rawPt.Properties.IsLeftButtonPressed || rawPt.Properties.IsRightButtonPressed)
                {
                    MouseClickedOnPage?.Invoke(this, ToPdfPoint(rawPt.Position));
                }
            }
            else if (devType == PointerDeviceType.Pen)
            {
                MouseClickedOnPage?.Invoke(this, ToPdfPoint(rawPt.Position));
            }
            else if (devType == PointerDeviceType.Touch &&
                     !_isTouchDraggingAnnotation &&
                     !_isTouchResizingAnnotation &&
                     _activeCanvasTouchPointerTicks.Count == 0)
            {
                MouseClickedOnPage?.Invoke(this, ToPdfPoint(rawPt.Position));
            }
        }

        if (!IsSelectInteractionAllowed() || PageInfo is null)
        {
            return;
        }

        PointerPoint pt = e.GetCurrentPoint(this);
        bool isTouch = e.Pointer.PointerDeviceType == PointerDeviceType.Touch;

        if (isTouch && (_isTouchDraggingAnnotation || _isTouchResizingAnnotation))
        {
            // Keep Finger 1's active annotation drag/resize intact while Finger 2 pans the viewport in DocumentView.
            return;
        }

        ShellViewModel? activeShell = _shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>();
        if (activeShell?.ActiveTool == AnnotationTool.Shape)
        {
            if (isTouch)
            {
                long shapeNow = Environment.TickCount64;
                PruneStaleCanvasTouchPointers(shapeNow);
                _activeCanvasTouchPointerTicks[e.Pointer.PointerId] = shapeNow;
                if (_activeCanvasTouchPointerTicks.Count > 1)
                {
                    CancelShapeDrawing();
                    return;
                }

                SubscribeToActiveDocument();
                TouchSelectionDismissRequested?.Invoke();
                _observedDocument?.ClearSelection();
                if (_observedDocument is not null)
                {
                    _observedDocument.SelectedAnnotation = null;
                }

                Point clampedStart = ClampToCanvasDip(pt.Position);
                _isDrawingShape = true;
                _shapeDrawIsTouch = true;
                _shapeDrawKind = activeShell.ActiveShapeKind;
                _shapeDrawStartDip = clampedStart;
                _shapeDrawRawEndDip = clampedStart;
                _shapeDrawEndDip = clampedStart;
                _shapeTouchPointerId = e.Pointer.PointerId;
                _touchPointerId = e.Pointer.PointerId;
                _touchPointerRef = e.Pointer;
                _touchPressRootDipPoint = XamlRoot?.Content is { } sRootElem
                    ? e.GetCurrentPoint(sRootElem).Position
                    : clampedStart;
                _hasTouchStartScreenPx = GetPointerInfo(_shapeTouchPointerId, out POINTER_INFO shapeStartPi);
                _touchStartScreenPx = _hasTouchStartScreenPx ? shapeStartPi.ptPixelLocation : default;
                _touchPollingMissingInfoCount = 0;

                TouchShapeDrawStarted?.Invoke(this, _shapeTouchPointerId);
                try
                {
                    _ = CapturePointer(e.Pointer);
                }
                catch
                {
                }

                StartTouchSelectionPolling();
                InvalidateOverlays();
                e.Handled = true;
                return;
            }

            if (pt.Properties.IsRightButtonPressed)
            {
                if (_isDrawingShape)
                {
                    CancelShapeDrawing();
                    e.Handled = true;
                    return;
                }

                return;
            }

            if (pt.Properties.IsLeftButtonPressed)
            {
                SubscribeToActiveDocument();
                _observedDocument?.ClearSelection();
                if (_observedDocument is not null)
                {
                    _observedDocument.SelectedAnnotation = null;
                }

                Point clampedPt = ClampToCanvasDip(pt.Position);
                if (!_isDrawingShape || _shapeDrawIsTouch)
                {
                    // Click 1: anchor start point; mouse move renders live Win2D preview until Click 2.
                    _isDrawingShape = true;
                    _shapeDrawIsTouch = false;
                    _shapeDrawKind = activeShell.ActiveShapeKind;
                    _shapeDrawStartDip = clampedPt;
                    _shapeDrawRawEndDip = clampedPt;
                    _shapeDrawEndDip = clampedPt;
                    SetCursorShape(InputSystemCursorShape.Cross);
                    InvalidateOverlays();
                    e.Handled = true;
                    return;
                }

                // Click 2: constrain end point if Shift held and commit the shape.
                _shapeDrawRawEndDip = clampedPt;
                Point finalEndDip = ConstrainShapeEndPointDip(_shapeDrawKind, _shapeDrawStartDip, clampedPt, IsShiftKeyDown());
                PdfAnnotationShapeKind commitKind = _shapeDrawKind;
                Point commitStart = _shapeDrawStartDip;
                _isDrawingShape = false;
                _shapeDrawIsTouch = false;
                InvalidateOverlays();

                double dist = double.Hypot(finalEndDip.X - commitStart.X, finalEndDip.Y - commitStart.Y);
                if (dist >= 4.0)
                {
                    ShapeCompleted?.Invoke(this, commitKind, ToPdfPoint(commitStart), ToPdfPoint(finalEndDip));
                }

                e.Handled = true;
                return;
            }
        }

        if (!isTouch && !pt.Properties.IsLeftButtonPressed)
        {
            // Right-click opens the context menu while preserving any existing text selection for "Copy".
            return;
        }

        SubscribeToActiveDocument();
        TouchSelectionDismissRequested?.Invoke();

        // Immediately clear any existing text selection when touching or left-clicking on the page outside the selection handles
        if (_observedDocument?.ActiveSelection is { HasContent: true })
        {
            _observedDocument.ClearSelection();
        }

        if (isTouch && _observedDocument?.SelectedAnnotation is { } currentSelected)
        {
            bool hitResizeCorner = currentSelected.IsResizable &&
                                   currentSelected.PageIndex == PageInfo.Index &&
                                   HitTestResizeHandle(currentSelected, pt.Position, out _, hitRadiusDip: 24.0) != AnnotationResizeHandle.None;
            if (!hitResizeCorner)
            {
                Vector2 touchPdf = ToPdfPoint(pt.Position);
                PdfAnnotationInfo? hitAtTouch = _observedDocument.HitTestAnnotation(PageInfo.Index, touchPdf.X, touchPdf.Y, tolerancePt: GetTouchAnnotationTolerancePt());
                if (hitAtTouch is null ||
                    hitAtTouch.PageIndex != currentSelected.PageIndex ||
                    hitAtTouch.AnnotIndex != currentSelected.AnnotIndex)
                {
                    _observedDocument.SelectedAnnotation = null;
                }
            }
        }

        if (!isTouch &&
            (_shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>())?.ActiveTool is AnnotationTool.TextNote or AnnotationTool.InsertText or AnnotationTool.InsertImage)
        {
            AnnotationTool activeTool = _shellViewModel!.ActiveTool;
            Vector2 notePdfPt = ToPdfPoint(pt.Position);
            if (activeTool == AnnotationTool.InsertImage)
            {
                PlaceImageRequested?.Invoke(this, notePdfPt, pt.Position);
                e.Handled = true;
                return;
            }

            if (activeTool == AnnotationTool.InsertText)
            {
                PlaceTextBoxRequested?.Invoke(this, notePdfPt, pt.Position);
                e.Handled = true;
                return;
            }

            PdfAnnotationInfo? hitAnnot = _observedDocument?.HitTestAnnotation(PageInfo.Index, notePdfPt.X, notePdfPt.Y, tolerancePt: 6.0);
            if (hitAnnot is not null)
            {
                if (hitAnnot.IsSignatureWidget && _observedDocument?.GetSignatureByIndex(hitAnnot.SignatureIndex) is { } sig)
                {
                    SignatureClicked?.Invoke(this, sig, pt.Position);
                }
                else
                {
                    if (_observedDocument is not null)
                    {
                        _observedDocument.SelectedAnnotation = hitAnnot;
                    }

                    AnnotationClicked?.Invoke(this, hitAnnot, pt.Position);
                }
            }
            else
            {
                PlaceTextNoteRequested?.Invoke(this, notePdfPt, pt.Position);
            }

            e.Handled = true;
            return;
        }

        if (!isTouch &&
            (_shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>())?.ActiveTool == AnnotationTool.Select &&
            _observedDocument is not null)
        {
            if (_observedDocument.SelectedAnnotation is { IsResizable: true } selectedResizable &&
                selectedResizable.PageIndex == PageInfo.Index)
            {
                AnnotationResizeHandle handle = HitTestResizeHandle(selectedResizable, pt.Position, out Rect origRectDip);
                if (handle != AnnotationResizeHandle.None)
                {
                    _resizingAnnotation = selectedResizable;
                    _activeResizeHandle = handle;
                    _isResizingAnnotation = true;
                    _resizeOrigRectDip = origRectDip;
                    _annotationResizePreviewDip = origRectDip;
                    _isPointerDown = true;
                    _hasDraggedSelection = false;
                    _pressedLink = PdfLinkTarget.None;
                    _pressDipPoint = pt.Position;
                    try
                    {
                        _ = CapturePointer(e.Pointer);
                    }
                    catch
                    {
                    }

                    SetCursorShape(GetResizeCursorShape(handle));
                    InvalidateOverlays();
                    e.Handled = true;
                    return;
                }
            }

            Vector2 pressPdf = ToPdfPoint(pt.Position);
            PdfAnnotationInfo? hitMovable = _observedDocument.HitTestAnnotation(PageInfo.Index, pressPdf.X, pressPdf.Y, tolerancePt: 4.0);
            if (hitMovable is { IsMovable: true })
            {
                _pressedMovableAnnotation = hitMovable;
                _isDraggingAnnotation = false;
                _annotationDragDeltaDip = default;
                _isPointerDown = true;
                _hasDraggedSelection = false;
                _pressedLink = PdfLinkTarget.None;
                _pressDipPoint = pt.Position;
                _pressPdfPoint = pressPdf;
                try
                {
                    _ = CapturePointer(e.Pointer);
                }
                catch
                {
                }

                e.Handled = true;
                return;
            }
        }

        long now = Environment.TickCount64;
        bool sameSpot = IsWithinMultiClickDistance(pt.Position, _lastClickDipPoint);

        if (isTouch)
        {
            PruneStaleCanvasTouchPointers(now);
            _activeCanvasTouchPointerTicks[e.Pointer.PointerId] = now;
            if (_activeCanvasTouchPointerTicks.Count > 1)
            {
                // 2+ fingers on canvas (2-finger tap to zoom or pinch-to-zoom): immediately cancel touch selection/hold
                CancelTouchSelectionAndHold();
                return;
            }
        }

        // Detect Triple-Click (Mouse/Pen only; Touch uses 300ms hold + drag for selection and double-tap/2-finger tap for zoom)
        if (!isTouch &&
            sameSpot &&
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

        if (isTouch)
        {
            Point rootPt = XamlRoot?.Content is { } rootElem
                ? e.GetCurrentPoint(rootElem).Position
                : pt.Position;

            if (!IsTouchSelectInteractionAllowed())
            {
                return;
            }

            StopTouchSelectionPolling();
            _touchHoldTimer.Stop();
            _touchHoldSessionId = ++_selectionSessionId;
            _touchPointerId = e.Pointer.PointerId;
            _touchPointerRef = e.Pointer;
            _touchPressTick = now;
            _touchPressDipPoint = pt.Position;
            _touchCurrentDipPoint = pt.Position;
            _touchHoldActivatedDipPoint = pt.Position;
            _touchPressPdfPoint = ToPdfPoint(pt.Position);
            _touchPressRootDipPoint = rootPt;
            _hasTouchStartScreenPx = GetPointerInfo(_touchPointerId, out POINTER_INFO startPi);
            _touchStartScreenPx = _hasTouchStartScreenPx ? startPi.ptPixelLocation : default;
            _isTouchWaitingForHold = true;
            _isTouchWaitingForCanceledTap = false;
            _pendingTouchHoldOpenAnnot = null;
            _isTouchSelecting = false;
            _hasTouchDraggedAfterHold = false;
            _touchInitialWordStartCharIndex = -1;
            _touchInitialWordEndCharIndex = -1;
            _touchHoldHitResult = default;
            _hasTouchHoldHitResult = false;
            _pendingTouchReleaseSelection = null;
            _isAwaitingInitialTouchWord = false;
            _touchHoldReleasedBeforeWordResolved = false;

            AnnotationTool touchTool = (_shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>())?.ActiveTool ?? AnnotationTool.Select;
            bool isTouchPlacementTool = touchTool is AnnotationTool.TextNote or AnnotationTool.InsertText or AnnotationTool.InsertImage;
            if (!isTouchPlacementTool)
            {
                _touchHoldMovableCandidate = _observedDocument?.HitTestAnnotation(PageInfo.Index, _touchPressPdfPoint.X, _touchPressPdfPoint.Y, tolerancePt: GetTouchAnnotationTolerancePt()) is { IsMovable: true } cand
                    ? cand
                    : null;
                _touchHoldTimer.Start();
                _ = ResolveTouchHoldCandidateAsync(EffectiveDocumentId, PageInfo.Index, _touchPressPdfPoint, _touchHoldSessionId);
            }
            else
            {
                _touchHoldMovableCandidate = null;
            }

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

        // Capture the pointer immediately on PointerPressed so ScrollViewer does not steal the mouse drag gesture.
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
                AnnotationTool? pressTool = (_shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>())?.ActiveTool;
                if (pressTool is AnnotationTool.TextNote or AnnotationTool.InsertImage)
                {
                    SetCursorShape(InputSystemCursorShape.Cross);
                }
                else if (pressTool == AnnotationTool.InsertText)
                {
                    SetCursorShape(InputSystemCursorShape.IBeam);
                }
                else if (hit.Link.IsValid)
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
                if (SelectionDragUpdated is not null)
                {
                    SelectionDragUpdated.Invoke(
                        this,
                        sessionId,
                        _selectionAnchorCharIndex,
                        _pressPdfPoint,
                        _lastDragDipPoint,
                        false,
                        -1,
                        -1,
                        -1);
                }
                else
                {
                    RequestDragSelectionUpdate(_lastDragDipPoint, force: true, sessionId);
                }
            }
        }
        catch
        {
        }
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsSelectInteractionAllowed())
        {
            SetCursorShape(InputSystemCursorShape.Arrow);
            return;
        }

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            if (_isDrawingShape && _shapeDrawIsTouch && e.Pointer.PointerId == _shapeTouchPointerId)
            {
                e.Handled = true;
                UpdateTouchShapeDrawPoint(e.GetCurrentPoint(this).Position);
                return;
            }

            if (e.Pointer.PointerId == _touchPointerId)
            {
                Point touchPosDip = e.GetCurrentPoint(this).Position;
                if (_isTouchDraggingAnnotation || _isTouchResizingAnnotation)
                {
                    e.Handled = true;
                    UpdateTouchAnnotationDragPoint(touchPosDip);
                    return;
                }

                if (_isTouchSelecting)
                {
                    e.Handled = true;
                }

                UpdateTouchSelectionDragPoint(touchPosDip);
            }

            return;
        }

        Point posDip = e.GetCurrentPoint(this).Position;

        if ((_shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>())?.ActiveTool == AnnotationTool.Shape)
        {
            SetCursorShape(InputSystemCursorShape.Cross);
            if (_isDrawingShape && !_shapeDrawIsTouch)
            {
                _shapeDrawRawEndDip = ClampToCanvasDip(posDip);
                _shapeDrawEndDip = ConstrainShapeEndPointDip(_shapeDrawKind, _shapeDrawStartDip, _shapeDrawRawEndDip, IsShiftKeyDown());
                InvalidateOverlays();
                e.Handled = true;
            }

            return;
        }

        if (_isPointerDown && _isResizingAnnotation && _resizingAnnotation is not null && PageInfo is not null)
        {
            UpdateAnnotationResizePreview(posDip);
            SetCursorShape(GetResizeCursorShape(_activeResizeHandle));
            InvalidateOverlays();
            e.Handled = true;
            return;
        }

        if (_isPointerDown && _pressedMovableAnnotation is not null)
        {
            double adx = posDip.X - _pressDipPoint.X;
            double ady = posDip.Y - _pressDipPoint.Y;
            if (!_isDraggingAnnotation && ((adx * adx) + (ady * ady)) >= (DragStartThresholdDip * DragStartThresholdDip))
            {
                _isDraggingAnnotation = true;
            }

            if (_isDraggingAnnotation)
            {
                _annotationDragDeltaDip = new Point(adx, ady);
                SetCursorShape(InputSystemCursorShape.SizeAll);
                InvalidateOverlays();
            }

            e.Handled = true;
            return;
        }

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
                if (SelectionDragUpdated is not null)
                {
                    _lastDragDipPoint = posDip;
                    SelectionDragUpdated.Invoke(
                        this,
                        _selectionSessionId,
                        _selectionAnchorCharIndex,
                        _pressPdfPoint,
                        posDip,
                        false,
                        -1,
                        -1,
                        -1);
                }
                else
                {
                    RequestDragSelectionUpdate(posDip, force: false, _selectionSessionId);
                }
            }

            return;
        }

        if (PageInfo is not null &&
            _observedDocument?.SelectedAnnotation is { IsResizable: true } hoverResizable &&
            hoverResizable.PageIndex == PageInfo.Index)
        {
            AnnotationResizeHandle hoverHandle = HitTestResizeHandle(hoverResizable, posDip, out _);
            if (hoverHandle != AnnotationResizeHandle.None)
            {
                SetCursorShape(GetResizeCursorShape(hoverHandle));
                return;
            }
        }

        RequestHoverHitTest(posDip);
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            _activeCanvasTouchPointerTicks.Remove(e.Pointer.PointerId);
            if (_isDrawingShape && _shapeDrawIsTouch && e.Pointer.PointerId == _shapeTouchPointerId)
            {
                e.Handled = true;
                CompleteTouchShapeDrawSession(e.GetCurrentPoint(this).Position);
                return;
            }

            if (e.Pointer.PointerId == _touchPointerId)
            {
                Point touchReleaseDip = e.GetCurrentPoint(this).Position;
                _touchCurrentDipPoint = touchReleaseDip;

                if (_isTouchDraggingAnnotation || _isTouchResizingAnnotation)
                {
                    e.Handled = true;
                    CompleteTouchAnnotationDragSession(touchReleaseDip);
                    return;
                }

                if (_pendingTouchHoldOpenAnnot is { } pendingAnnot)
                {
                    _pendingTouchHoldOpenAnnot = null;
                    StopTouchSelectionPolling();
                    e.Handled = true;
                    if (pendingAnnot.IsSignatureWidget && _observedDocument?.GetSignatureByIndex(pendingAnnot.SignatureIndex) is { } sig)
                    {
                        SignatureClicked?.Invoke(this, sig, touchReleaseDip);
                    }
                    else
                    {
                        if (_observedDocument is not null)
                        {
                            _observedDocument.SelectedAnnotation = pendingAnnot;
                        }

                        AnnotationClicked?.Invoke(this, pendingAnnot, touchReleaseDip);
                    }

                    return;
                }

                if (_isTouchSelecting)
                {
                    e.Handled = true;
                    CompleteTouchSelectionSession(touchReleaseDip);
                    return;
                }

                if (HandleTouchQuickRelease(touchReleaseDip))
                {
                    e.Handled = true;
                }
            }

            return;
        }

        if (!_isPointerDown)
        {
            return;
        }

        // Capture local state BEFORE calling ReleasePointerCapture, because ReleasePointerCapture
        // synchronously raises OnCanvasPointerCaptureLost in WinUI 3!
        bool wasDragged = _hasDraggedSelection;
        bool wasAnnotDragged = _isDraggingAnnotation;
        bool wasResizing = _isResizingAnnotation;
        PdfAnnotationInfo? resizedAnnot = _resizingAnnotation;
        Rect resizePreviewDip = _annotationResizePreviewDip;
        PdfAnnotationInfo? pressedMovableAnnot = _pressedMovableAnnotation;
        PdfLinkTarget pressedLink = _pressedLink;
        long activeSession = _selectionSessionId;
        int anchorChar = _selectionAnchorCharIndex;
        Vector2 anchorPdf = _pressPdfPoint;
        Point releasePosDip = e.GetCurrentPoint(this).Position;

        _isPointerDown = false;
        _hasDraggedSelection = false;
        _isDraggingAnnotation = false;
        _pressedMovableAnnotation = null;
        _annotationDragDeltaDip = default;
        _isResizingAnnotation = false;
        _resizingAnnotation = null;
        _activeResizeHandle = AnnotationResizeHandle.None;
        _annotationResizePreviewDip = default;
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

        if (wasResizing && resizedAnnot is not null && PageInfo is not null)
        {
            e.Handled = true;
            SetCursorShape(InputSystemCursorShape.Arrow);
            InvalidateOverlays();

            PageCoordinateTransform transform = PageCoordinateTransform.FromPage(PageInfo, RotationAngle);
            PdfRect newPdfBounds = transform.ToPdfRect(resizePreviewDip);
            if (Math.Abs(newPdfBounds.Width - resizedAnnot.Bounds.Width) > 0.5f ||
                Math.Abs(newPdfBounds.Height - resizedAnnot.Bounds.Height) > 0.5f ||
                Math.Abs(newPdfBounds.Left - resizedAnnot.Bounds.Left) > 0.5f ||
                Math.Abs(newPdfBounds.Bottom - resizedAnnot.Bounds.Bottom) > 0.5f)
            {
                AnnotationResized?.Invoke(this, resizedAnnot, newPdfBounds);
            }

            return;
        }

        if (pressedMovableAnnot is not null)
        {
            e.Handled = true;
            SetCursorShape(InputSystemCursorShape.Arrow);
            InvalidateOverlays();

            if (wasAnnotDragged)
            {
                Vector2 releasePdf = ToPdfPoint(releasePosDip);
                Vector2 deltaPdf = releasePdf - anchorPdf;
                if (MathF.Abs(deltaPdf.X) > 0.5f || MathF.Abs(deltaPdf.Y) > 0.5f)
                {
                    AnnotationMoved?.Invoke(this, pressedMovableAnnot, deltaPdf);
                }
            }
            else if (_observedDocument is { } clickedAnnotDoc)
            {
                clickedAnnotDoc.ClearSelection();
                clickedAnnotDoc.SelectedAnnotation = pressedMovableAnnot;
                AnnotationClicked?.Invoke(this, pressedMovableAnnot, releasePosDip);
            }

            return;
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
            if (SelectionDragCompleted is not null)
            {
                SelectionDragCompleted.Invoke(
                    this,
                    activeSession,
                    anchorChar,
                    anchorPdf,
                    releasePosDip,
                    false,
                    -1,
                    -1,
                    -1);
            }
            else
            {
                RequestDragSelectionUpdate(releasePosDip, force: true, activeSession);
            }

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
        else if (PageInfo is not null && _observedDocument is { } clickedDoc)
        {
            PdfAnnotationInfo? hitAnnot = clickedDoc.HitTestAnnotation(PageInfo.Index, anchorPdf.X, anchorPdf.Y, tolerancePt: 6.0);
            if (hitAnnot is not null)
            {
                e.Handled = true;
                clickedDoc.ClearSelection();
                if (hitAnnot.IsSignatureWidget && clickedDoc.GetSignatureByIndex(hitAnnot.SignatureIndex) is { } sig)
                {
                    SignatureClicked?.Invoke(this, sig, releasePosDip);
                }
                else
                {
                    clickedDoc.SelectedAnnotation = hitAnnot;
                    AnnotationClicked?.Invoke(this, hitAnnot, releasePosDip);
                }
            }
            else
            {
                clickedDoc.ClearSelection();
                clickedDoc.SelectedAnnotation = null;
            }
        }
        else
        {
            _observedDocument?.ClearSelection();
        }
    }

    private void OnCanvasPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            uint canceledId = e.Pointer.PointerId;
            if (_isDrawingShape && _shapeDrawIsTouch && canceledId == _shapeTouchPointerId)
            {
                if (GetPointerInfo(_shapeTouchPointerId, out POINTER_INFO shapePi) &&
                    (shapePi.pointerFlags & POINTER_FLAG_INCONTACT) != 0 &&
                    (shapePi.pointerFlags & POINTER_FLAG_UP) == 0)
                {
                    StartTouchSelectionPolling();
                    return;
                }

                _activeCanvasTouchPointerTicks.Remove(canceledId);
                CompleteTouchShapeDrawSession(_shapeDrawRawEndDip);
                return;
            }

            bool isDraggingAnnot = _isTouchDraggingAnnotation || _isTouchResizingAnnotation;
            if (isDraggingAnnot && canceledId != _touchPointerId)
            {
                // Ignore Finger 2 cancel while Finger 1 drags/resizes an annotation.
                return;
            }

            bool isTrackedActiveTouch = canceledId == _touchPointerId &&
                (_isTouchSelecting || _isTouchWaitingForHold || isDraggingAnnot || _pendingTouchHoldOpenAnnot is not null) &&
                (isDraggingAnnot || _activeCanvasTouchPointerTicks.Count <= 1);
            if (isTrackedActiveTouch)
            {
                // If the finger is still physically in contact with the touchscreen (DirectManipulation or
                // CancelDirectManipulations merely changed pointer capture), keep the hold timer / active drag polling alive!
                if (GetPointerInfo(_touchPointerId, out POINTER_INFO pi) &&
                    (pi.pointerFlags & POINTER_FLAG_INCONTACT) != 0 &&
                    (pi.pointerFlags & POINTER_FLAG_UP) == 0)
                {
                    if (_isTouchWaitingForHold && !_isTouchSelecting && !isDraggingAnnot && _pendingTouchHoldOpenAnnot is null)
                    {
                        // DirectManipulation took capture while waiting for a quick tap:
                        // poll GetPointerInfo so lifting the finger cleanly still fires HandleTouchQuickRelease!
                        _isTouchWaitingForCanceledTap = true;
                        StartTouchSelectionPolling();
                    }

                    return;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isUnloaded || _touchPointerId != canceledId)
                    {
                        return;
                    }

                    if (!(_isTouchDraggingAnnotation || _isTouchResizingAnnotation) &&
                        IsSingleTouchViewerManipulating?.Invoke() == true)
                    {
                        return;
                    }

                    if (GetPointerInfo(canceledId, out POINTER_INFO delayedPi) &&
                        (delayedPi.pointerFlags & POINTER_FLAG_INCONTACT) != 0 &&
                        (delayedPi.pointerFlags & POINTER_FLAG_UP) == 0)
                    {
                        return;
                    }

                    _activeCanvasTouchPointerTicks.Remove(canceledId);
                    if (_isTouchDraggingAnnotation || _isTouchResizingAnnotation)
                    {
                        CompleteTouchAnnotationDragSession(_lastTouchAnnotDragCanvasDip);
                    }
                    else if (_pendingTouchHoldOpenAnnot is { } delayedAnnot)
                    {
                        _pendingTouchHoldOpenAnnot = null;
                        StopTouchSelectionPolling();
                        if (delayedAnnot.IsSignatureWidget && _observedDocument?.GetSignatureByIndex(delayedAnnot.SignatureIndex) is { } sig)
                        {
                            SignatureClicked?.Invoke(this, sig, _touchCurrentDipPoint);
                        }
                        else
                        {
                            if (_observedDocument is not null)
                            {
                                _observedDocument.SelectedAnnotation = delayedAnnot;
                            }

                            AnnotationClicked?.Invoke(this, delayedAnnot, _touchCurrentDipPoint);
                        }
                    }
                    else if (_isTouchSelecting)
                    {
                        CompleteTouchSelectionSession(_touchCurrentDipPoint);
                    }
                    else if (_isTouchWaitingForHold)
                    {
                        _ = HandleTouchQuickRelease(_touchCurrentDipPoint);
                    }
                });
                return;
            }

            _activeCanvasTouchPointerTicks.Remove(canceledId);
            if (canceledId == _touchPointerId)
            {
                CancelTouchSelectionAndHold();
            }

            return;
        }

        bool hadMouseDrag = _hasDraggedSelection;
        _isPointerDown = false;
        _hasDraggedSelection = false;
        _pressedLink = PdfLinkTarget.None;
        if (hadMouseDrag)
        {
            SelectionDragCanceled?.Invoke(this);
        }
    }

    private void OnCanvasPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isReleasingCapture)
        {
            return;
        }

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            // Do NOT remove the touch pointer on PointerCaptureLost while the finger is still pressed down
            // (XAML Manipulation or ScrollViewer takes capture a few ms after PointerPressed).
            return;
        }

        // Do NOT clear _hasDraggedSelection or _observedDocument.ActiveSelection here so any selection
        // already made by the user remains intact.
        _isPointerDown = false;
        if (_pressedMovableAnnotation is not null || _isDraggingAnnotation || _isResizingAnnotation || _resizingAnnotation is not null)
        {
            _pressedMovableAnnotation = null;
            _isDraggingAnnotation = false;
            _annotationDragDeltaDip = default;
            _resizingAnnotation = null;
            _isResizingAnnotation = false;
            _activeResizeHandle = AnnotationResizeHandle.None;
            _annotationResizePreviewDip = default;
            SetCursorShape(InputSystemCursorShape.Arrow);
            InvalidateOverlays();
        }
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

        bool isTouch = e.PointerDeviceType == PointerDeviceType.Touch;
        _lastClickTick = now;
        _clickCount = 2;
        _isPointerDown = false;
        _hasDraggedSelection = false;
        _pressedLink = PdfLinkTarget.None;

        if (isTouch)
        {
            // Touch double-tap zoom is handled deterministically on clean stationary tap releases in HandleTouchQuickRelease
            e.Handled = true;
            return;
        }

        _lastDoubleTapTick = now;

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

                AnnotationTool? hoverTool = (_shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>())?.ActiveTool;
                if (hoverTool is AnnotationTool.TextNote or AnnotationTool.InsertImage)
                {
                    SetCursorShape(InputSystemCursorShape.Cross);
                }
                else if (hoverTool == AnnotationTool.InsertText)
                {
                    SetCursorShape(InputSystemCursorShape.IBeam);
                }
                else if (hit.Link.IsValid)
                {
                    SetCursorShape(InputSystemCursorShape.Hand);
                }
                else if (_observedDocument?.HitTestAnnotation(pageIndex, pdfPt.X, pdfPt.Y, tolerancePt: 4.0) is { } hoverAnnot &&
                         (hoverAnnot.IsSignatureWidget ||
                          hoverAnnot.IsMovable ||
                          hoverAnnot.HasComment))
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
                int prevEndChar = _lastDragEndCharIndex;

                int initWordPage = _isTouchSelecting ? pageIndex : -1;
                int initWordStart = _isTouchSelecting ? _touchInitialWordStartCharIndex : -1;
                int initWordEnd = _isTouchSelecting ? _touchInitialWordEndCharIndex : -1;

                PdfTextSelection? updated = await _pdfService.SelectTextAcrossPagesAsync(
                    docId,
                    pageIndex,
                    anchor,
                    anchorPdf.X,
                    anchorPdf.Y,
                    pageIndex,
                    pdfPt.X,
                    pdfPt.Y,
                    PdfSelectionAnchorClamp.None,
                    initWordPage,
                    initWordStart,
                    initWordEnd,
                    pageIndex,
                    prevEndChar);

                if (_isUnloaded ||
                    _selectionSessionId != sessionId ||
                    PageInfo?.Index != pageIndex ||
                    EffectiveDocumentId != docId)
                {
                    break;
                }

                if (updated is { HasContent: true })
                {
                    if (!_isTouchSelecting && updated.AnchorCharIndex >= 0)
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

}
