using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinPDF.Models;
using WinPDF.Rendering;
using WinPDF.ViewModels;

namespace WinPDF.Controls;

public sealed partial class PdfPageCanvas
{
    private const int TouchHoldDurationMs = 240;
    private const double TouchHoldMaxMoveDip = 16.0;
    private const double TouchDragStartThresholdDip = 2.0;
    private const long TouchSingleTapMaxDurationMs = 340;
    private const double TouchSingleTapMaxMoveDip = 12.0;
    private const double TouchDoubleTapMaxDistanceDip = 18.0;
    private const long TouchDoubleTapMaxIntervalMs = 270;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint POINTER_FLAG_UP = 0x00040000;

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

    /// <summary>
    /// Gets or sets a value indicating whether the user is actively dragging the touch vertical scroll scrubber.
    /// When true, uncached Level 0 page renders apply a short micro-debounce so pages flying past the viewport do not saturate PDFium.
    /// </summary>
    public static bool IsTouchScrubberDragging { get; set; }

    // Touch long-press + drag text selection state (Stage 2)
    private readonly DispatcherTimer _touchHoldTimer = new() { Interval = TimeSpan.FromMilliseconds(TouchHoldDurationMs) };
    private readonly Dictionary<uint, long> _activeCanvasTouchPointerTicks = new();
    private uint _touchPointerId;
    private Pointer? _touchPointerRef;
    private long _touchPressTick;
    private Point _touchPressDipPoint;
    private Point _touchCurrentDipPoint;
    private Point _touchHoldActivatedDipPoint;
    private Point _touchPressRootDipPoint;
    private WIN32_POINT _touchStartScreenPx;
    private bool _hasTouchStartScreenPx;
    private bool _isTouchSelectionPollingSubscribed;
    private long _lastTouchTapTick;
    private Point _lastTouchTapRootPoint = new(-1000, -1000);
    private Vector2 _touchPressPdfPoint;
    private long _touchHoldSessionId;
    private int _touchInitialWordStartCharIndex = -1;
    private int _touchInitialWordEndCharIndex = -1;
    private bool _isTouchWaitingForHold;
    private bool _isTouchSelecting;
    private bool _hasTouchDraggedAfterHold;
    private PdfPointerHitResult _touchHoldHitResult;
    private bool _hasTouchHoldHitResult;
    private PdfTextSelection? _pendingTouchReleaseSelection;
    private int _touchPollingMissingInfoCount;
    private WIN32_POINT _lastPolledTouchScreenPx;
    private bool _hasLastPolledTouchScreenPx;
    private bool _isAwaitingInitialTouchWord;
    private bool _touchHoldReleasedBeforeWordResolved;
    private bool _isTouchDraggingAnnotation;
    private bool _isTouchResizingAnnotation;
    private bool _hasTouchMovedAnnotation;
    private bool _isTouchWaitingForCanceledTap;
    private Point _lastTouchAnnotDragCanvasDip;
    private PdfAnnotationInfo? _pendingTouchHoldOpenAnnot;
    private PdfAnnotationInfo? _touchHoldMovableCandidate;

    /// <summary>
    /// Gets a value indicating whether this canvas is currently waiting for a touch hold threshold.
    /// </summary>
    public bool IsTouchWaitingForHold => _isTouchWaitingForHold;

    /// <summary>
    /// Gets a value indicating whether a touch press on this canvas landed on a movable annotation and is waiting for the hold threshold.
    /// </summary>
    public bool IsTouchWaitingOnMovableAnnotation => _isTouchWaitingForHold && _touchHoldMovableCandidate is not null;

    /// <summary>
    /// Gets a value indicating whether an interactive touch text selection drag is currently active on this canvas.
    /// </summary>
    public bool IsTouchSelecting => _isTouchSelecting;

    /// <summary>
    /// Gets a value indicating whether an interactive touch annotation drag or corner-resize session is currently active on this canvas.
    /// </summary>
    public bool IsTouchDraggingAnnotation => _isTouchDraggingAnnotation || _isTouchResizingAnnotation;

    /// <summary>
    /// Gets a value indicating whether a touch-drag geometric shape drawing gesture is currently active on this canvas.
    /// </summary>
    public bool IsTouchDrawingShape => _isDrawingShape && _shapeDrawIsTouch;

    /// <summary>
    /// Event raised when a touch shape-draw gesture starts on a page canvas, passing the canvas and touch pointer ID.
    /// </summary>
    public static event Action<PdfPageCanvas, uint>? TouchShapeDrawStarted;

    /// <summary>
    /// Event raised when a touch shape-draw gesture completes or is canceled.
    /// </summary>
    public static event Action<PdfPageCanvas>? TouchShapeDrawEnded;

    /// <summary>
    /// Event raised when a 300ms touch hold over a movable annotation (or selected resize handle) starts touch dragging/resizing.
    /// Passes the canvas and the active dragging touch pointer ID so the parent viewer can allow 2nd-finger multitouch panning.
    /// </summary>
    public static event Action<PdfPageCanvas, uint>? TouchAnnotationDragStarted;

    /// <summary>
    /// Event raised when an active touch annotation drag or corner-resize session finishes or is canceled.
    /// </summary>
    public static event Action<PdfPageCanvas>? TouchAnnotationDragEnded;

    /// <summary>
    /// Event raised when a 300ms touch hold over selectable text activates interactive touch text selection.
    /// </summary>
    public static event Action<PdfPageCanvas>? TouchSelectionStarted;

    /// <summary>
    /// Event raised when an active touch text selection gesture finishes or is canceled.
    /// </summary>
    public static event Action<PdfPageCanvas>? TouchSelectionEnded;

    /// <summary>
    /// Event raised when the user releases their finger after completing a touch text selection (hold or hold + drag).
    /// </summary>
    public static event Action<PdfPageCanvas, PdfTextSelection>? TouchSelectionCompleted;

    /// <summary>
    /// Event raised when a touch double-tap occurs on a page canvas, requesting a smooth zoom-in around the tapped local canvas point.
    /// </summary>
    public static event Action<PdfPageCanvas, Point>? TouchDoubleTapZoomRequested;

    /// <summary>
    /// Event raised when a touch interaction starts or taps outside, requesting that the touch selection flyout be hidden.
    /// </summary>
    public static event Action? TouchSelectionDismissRequested;

    /// <summary>
    /// Event raised when the user holds touch on an empty page area (no text and no annotation) for 300 ms.
    /// </summary>
    public static event Action<PdfPageCanvas, Vector2, Point>? TouchEmptyHoldRequested;

    /// <summary>
    /// Optional callback queried to check whether the parent viewer is currently tracking a single-finger XAML manipulation.
    /// </summary>
    public static Func<bool>? IsSingleTouchViewerManipulating { get; set; }

    /// <summary>
    /// Synchronizes the XAML touch manipulation mode of this canvas and its row wrapper elements
    /// so that unzoomed non-continuous and presentation modes emit UI-thread horizontal swipe manipulations
    /// while preserving native <see cref="ScrollViewer"/> DirectManipulation for pinch-to-zoom and zoomed panning.
    /// </summary>
    /// <param name="forceCustomSwipeManip">Optional explicit override for whether custom swipe manipulation should be active.</param>
    /// <param name="includeInertia">Whether to include translation inertia in custom manipulation mode.</param>
    public void SyncTouchManipulationMode(bool? forceCustomSwipeManip = null, bool includeInertia = false)
    {
        if (IsSwipePreview)
        {
            if (ManipulationMode != ManipulationModes.None)
            {
                ManipulationMode = ManipulationModes.None;
            }

            return;
        }

        bool usePagedTouchManip;
        if (forceCustomSwipeManip.HasValue)
        {
            usePagedTouchManip = forceCustomSwipeManip.Value;
        }
        else
        {
            ShellViewModel? shell = _shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>();
            DocumentViewModel? doc = _observedDocument ?? shell?.CurrentDocument;
            usePagedTouchManip = shell?.IsPresentationMode == true ||
                                 (doc is not null && !doc.IsContinuous);
        }

        ManipulationModes customFlags = includeInertia
            ? (ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.TranslateInertia | ManipulationModes.Scale)
            : (ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.Scale);

        ManipulationModes targetMode = usePagedTouchManip
            ? customFlags
            : ManipulationModes.System;

        SetHierarchyManipulationMode(targetMode);
    }

    private void SetHierarchyManipulationMode(ManipulationModes targetMode)
    {
        DependencyObject? curr = this;
        for (int hop = 0; hop < 6 && curr is not null; hop++)
        {
            if (curr is ScrollViewer or ScrollContentPresenter)
            {
                break;
            }

            if (curr is UIElement ui && ui.ManipulationMode != targetMode)
            {
                ui.ManipulationMode = targetMode;
            }

            curr = VisualTreeHelper.GetParent(curr);
        }
    }

    private bool IsTouchSelectInteractionAllowed()
    {
        return IsSelectInteractionAllowed() && !IsSwipePreview;
    }

    private void PruneStaleCanvasTouchPointers(long nowTick)
    {
        if (_activeCanvasTouchPointerTicks.Count == 0)
        {
            return;
        }

        List<uint>? stale = null;
        foreach (var kvp in _activeCanvasTouchPointerTicks)
        {
            bool stillInContact = GetPointerInfo(kvp.Key, out POINTER_INFO pi) &&
                                  (pi.pointerFlags & POINTER_FLAG_INCONTACT) != 0 &&
                                  (pi.pointerFlags & POINTER_FLAG_UP) == 0;
            bool isActiveDragPointer = (_isTouchSelecting || _isTouchDraggingAnnotation || _isTouchResizingAnnotation) &&
                                       kvp.Key == _touchPointerId;
            if (!stillInContact || (nowTick - kvp.Value > 600 && !isActiveDragPointer))
            {
                (stale ??= new List<uint>()).Add(kvp.Key);
            }
        }

        if (stale is not null)
        {
            foreach (uint id in stale)
            {
                _activeCanvasTouchPointerTicks.Remove(id);
            }
        }
    }

    private void StartTouchSelectionPolling()
    {
        _touchPollingMissingInfoCount = 0;
        _hasLastPolledTouchScreenPx = false;
        if (!_isTouchSelectionPollingSubscribed)
        {
            CompositionTarget.Rendering += OnTouchSelectionPollingRendering;
            _isTouchSelectionPollingSubscribed = true;
        }
    }

    private void StopTouchSelectionPolling()
    {
        _touchPollingMissingInfoCount = 0;
        _hasLastPolledTouchScreenPx = false;
        if (_isTouchSelectionPollingSubscribed)
        {
            CompositionTarget.Rendering -= OnTouchSelectionPollingRendering;
            _isTouchSelectionPollingSubscribed = false;
        }
    }

    private void OnTouchSelectionPollingRendering(object? sender, object e)
    {
        bool isDraggingOrResizingAnnot = _isTouchDraggingAnnotation || _isTouchResizingAnnotation;
        bool isTouchDrawingShape = _isDrawingShape && _shapeDrawIsTouch;
        bool needsPolling = _isTouchSelecting ||
                            isDraggingOrResizingAnnot ||
                            isTouchDrawingShape ||
                            _isTouchWaitingForCanceledTap ||
                            _pendingTouchHoldOpenAnnot is not null;

        if (!needsPolling || _isUnloaded || PageInfo is null)
        {
            StopTouchSelectionPolling();
            return;
        }

        // Mode 1: DirectManipulation canceled PointerReleased during a quick touch tap; watch for lift or scroll movement.
        if (_isTouchWaitingForCanceledTap && !_isTouchSelecting && !isDraggingOrResizingAnnot && _pendingTouchHoldOpenAnnot is null)
        {
            if (!_isTouchWaitingForHold || IsSingleTouchViewerManipulating?.Invoke() == true)
            {
                _isTouchWaitingForCanceledTap = false;
                StopTouchSelectionPolling();
                return;
            }

            if (!GetPointerInfo(_touchPointerId, out POINTER_INFO tapPi))
            {
                _isTouchWaitingForCanceledTap = false;
                StopTouchSelectionPolling();
                _ = HandleTouchQuickRelease(_touchCurrentDipPoint);
                return;
            }

            Point latestTapCanvasDip = _touchCurrentDipPoint;
            if (_hasTouchStartScreenPx && XamlRoot?.Content is { } tapRoot)
            {
                double scale = XamlRoot.RasterizationScale > 0 ? XamlRoot.RasterizationScale : 1.0;
                double dxDip = (tapPi.ptPixelLocation.X - _touchStartScreenPx.X) / scale;
                double dyDip = (tapPi.ptPixelLocation.Y - _touchStartScreenPx.Y) / scale;
                if (((dxDip * dxDip) + (dyDip * dyDip)) > (TouchHoldMaxMoveDip * TouchHoldMaxMoveDip))
                {
                    _isTouchWaitingForCanceledTap = false;
                    StopTouchSelectionPolling();
                    CancelPendingTouchHold();
                    return;
                }

                try
                {
                    Point curRootDip = new(_touchPressRootDipPoint.X + dxDip, _touchPressRootDipPoint.Y + dyDip);
                    latestTapCanvasDip = tapRoot.TransformToVisual(this).TransformPoint(curRootDip);
                    _touchCurrentDipPoint = latestTapCanvasDip;
                }
                catch
                {
                }
            }

            if ((tapPi.pointerFlags & POINTER_FLAG_INCONTACT) == 0 ||
                (tapPi.pointerFlags & POINTER_FLAG_UP) != 0)
            {
                _isTouchWaitingForCanceledTap = false;
                StopTouchSelectionPolling();
                _ = HandleTouchQuickRelease(latestTapCanvasDip);
            }

            return;
        }

        // Mode 2: Hold on a non-movable annotation/signature finished 300ms; wait for finger lift to open its popup cleanly.
        if (_pendingTouchHoldOpenAnnot is { } pendingAnnot && !_isTouchSelecting && !isDraggingOrResizingAnnot)
        {
            bool lifted = !GetPointerInfo(_touchPointerId, out POINTER_INFO holdOpenPi) ||
                          (holdOpenPi.pointerFlags & POINTER_FLAG_INCONTACT) == 0 ||
                          (holdOpenPi.pointerFlags & POINTER_FLAG_UP) != 0;
            if (lifted)
            {
                _pendingTouchHoldOpenAnnot = null;
                StopTouchSelectionPolling();
                if (pendingAnnot.IsSignatureWidget && _observedDocument?.GetSignatureByIndex(pendingAnnot.SignatureIndex) is { } sig)
                {
                    SignatureClicked?.Invoke(this, sig, _touchCurrentDipPoint);
                }
                else
                {
                    if (_observedDocument is not null)
                    {
                        _observedDocument.SelectedAnnotation = pendingAnnot;
                    }

                    AnnotationClicked?.Invoke(this, pendingAnnot, _touchCurrentDipPoint);
                }
            }

            return;
        }

        // Mode 2.5: Touch drag shape drawing (touch down -> drag -> lift)
        if (isTouchDrawingShape)
        {
            if (!GetPointerInfo(_shapeTouchPointerId, out POINTER_INFO shapePi))
            {
                _touchPollingMissingInfoCount++;
                if (_touchPollingMissingInfoCount >= 6)
                {
                    StopTouchSelectionPolling();
                    CompleteTouchShapeDrawSession(_shapeDrawRawEndDip);
                }

                return;
            }

            _touchPollingMissingInfoCount = 0;

            if ((shapePi.pointerFlags & POINTER_FLAG_INCONTACT) == 0 ||
                (shapePi.pointerFlags & POINTER_FLAG_UP) != 0)
            {
                StopTouchSelectionPolling();
                CompleteTouchShapeDrawSession(_shapeDrawRawEndDip);
                return;
            }

            if (_hasTouchStartScreenPx && XamlRoot?.Content is { } shapeRoot)
            {
                double scale = XamlRoot.RasterizationScale > 0 ? XamlRoot.RasterizationScale : 1.0;
                double dxDip = (shapePi.ptPixelLocation.X - _touchStartScreenPx.X) / scale;
                double dyDip = (shapePi.ptPixelLocation.Y - _touchStartScreenPx.Y) / scale;
                Point curRootDip = new(_touchPressRootDipPoint.X + dxDip, _touchPressRootDipPoint.Y + dyDip);
                try
                {
                    Point curCanvasDip = shapeRoot.TransformToVisual(this).TransformPoint(curRootDip);
                    if (Math.Abs(curCanvasDip.X - _shapeDrawRawEndDip.X) > 0.25 ||
                        Math.Abs(curCanvasDip.Y - _shapeDrawRawEndDip.Y) > 0.25)
                    {
                        UpdateTouchShapeDrawPoint(curCanvasDip);
                    }
                }
                catch
                {
                }
            }

            return;
        }

        // Mode 3: Touch drag-to-move / corner-resize annotation (with multitouch 2nd-finger viewport pan support)
        if (isDraggingOrResizingAnnot)
        {
            if (!GetPointerInfo(_touchPointerId, out POINTER_INFO dragPi))
            {
                _touchPollingMissingInfoCount++;
                if (_touchPollingMissingInfoCount >= 12)
                {
                    StopTouchSelectionPolling();
                    CompleteTouchAnnotationDragSession(_lastTouchAnnotDragCanvasDip);
                }

                return;
            }

            _touchPollingMissingInfoCount = 0;

            if ((dragPi.pointerFlags & POINTER_FLAG_INCONTACT) == 0 ||
                (dragPi.pointerFlags & POINTER_FLAG_UP) != 0)
            {
                StopTouchSelectionPolling();
                CompleteTouchAnnotationDragSession(_lastTouchAnnotDragCanvasDip);
                return;
            }

            if (!_hasTouchStartScreenPx)
            {
                _touchStartScreenPx = dragPi.ptPixelLocation;
                _hasTouchStartScreenPx = true;
            }

            // Always transform through rootContent -> this even if Finger 1 stayed still on glass,
            // because Finger 2 may be panning the ScrollViewer underneath Finger 1!
            if (XamlRoot?.Content is { } dragRoot)
            {
                double scale = XamlRoot.RasterizationScale > 0 ? XamlRoot.RasterizationScale : 1.0;
                double dxDip = (dragPi.ptPixelLocation.X - _touchStartScreenPx.X) / scale;
                double dyDip = (dragPi.ptPixelLocation.Y - _touchStartScreenPx.Y) / scale;
                Point curRootDip = new(_touchPressRootDipPoint.X + dxDip, _touchPressRootDipPoint.Y + dyDip);
                try
                {
                    Point curCanvasDip = dragRoot.TransformToVisual(this).TransformPoint(curRootDip);
                    if (Math.Abs(curCanvasDip.X - _lastTouchAnnotDragCanvasDip.X) > 0.25 ||
                        Math.Abs(curCanvasDip.Y - _lastTouchAnnotDragCanvasDip.Y) > 0.25)
                    {
                        UpdateTouchAnnotationDragPoint(curCanvasDip);
                    }
                }
                catch
                {
                }
            }

            return;
        }

        // Mode 4: Active touch text selection drag
        // Note: Do NOT check POINTER_FLAG_CANCELED here because CancelDirectManipulations sets POINTER_FLAG_CANCELED
        // while the finger remains physically in contact (POINTER_FLAG_INCONTACT != 0) and continues streaming coordinates.
        if (!GetPointerInfo(_touchPointerId, out POINTER_INFO pi))
        {
            _touchPollingMissingInfoCount++;
            if (_touchPollingMissingInfoCount >= 2)
            {
                StopTouchSelectionPolling();
                CompleteTouchSelectionSession(_touchCurrentDipPoint);
            }

            return;
        }

        _touchPollingMissingInfoCount = 0;

        if ((pi.pointerFlags & POINTER_FLAG_INCONTACT) == 0 ||
            (pi.pointerFlags & POINTER_FLAG_UP) != 0)
        {
            StopTouchSelectionPolling();
            CompleteTouchSelectionSession(_touchCurrentDipPoint);
            return;
        }

        if (!_hasTouchStartScreenPx)
        {
            _touchStartScreenPx = pi.ptPixelLocation;
            _hasTouchStartScreenPx = true;
        }

        if (_hasLastPolledTouchScreenPx &&
            pi.ptPixelLocation.X == _lastPolledTouchScreenPx.X &&
            pi.ptPixelLocation.Y == _lastPolledTouchScreenPx.Y)
        {
            return;
        }

        _lastPolledTouchScreenPx = pi.ptPixelLocation;
        _hasLastPolledTouchScreenPx = true;

        if (XamlRoot?.Content is { } rootContent)
        {
            double scale = XamlRoot.RasterizationScale > 0 ? XamlRoot.RasterizationScale : 1.0;
            double dxDip = (pi.ptPixelLocation.X - _touchStartScreenPx.X) / scale;
            double dyDip = (pi.ptPixelLocation.Y - _touchStartScreenPx.Y) / scale;
            Point curRootDip = new(_touchPressRootDipPoint.X + dxDip, _touchPressRootDipPoint.Y + dyDip);
            try
            {
                Point curCanvasDip = rootContent.TransformToVisual(this).TransformPoint(curRootDip);
                UpdateTouchSelectionDragPoint(curCanvasDip);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Cancels any pending 300ms touch hold timer without disturbing active scrolling or slide swiping.
    /// </summary>
    /// <param name="preserveTapHistory">Whether to preserve the previous tap timestamp for double-tap detection.</param>
    internal void CancelPendingTouchHold(bool preserveTapHistory = false)
    {
        _touchHoldTimer.Stop();
        _isTouchWaitingForHold = false;
        _isTouchWaitingForCanceledTap = false;
        _pendingTouchHoldOpenAnnot = null;
        _touchHoldMovableCandidate = null;
        _touchHoldHitResult = default;
        _hasTouchHoldHitResult = false;
        if (!_isTouchSelecting && !_isTouchDraggingAnnotation && !_isTouchResizingAnnotation && !(_isDrawingShape && _shapeDrawIsTouch))
        {
            StopTouchSelectionPolling();
        }
        if (!preserveTapHistory)
        {
            _lastTouchTapTick = 0;
        }
    }

    internal void BeginExternalTouchShapeDraw(PdfAnnotationShapeKind kind, Point startInCanvasDip)
    {
        if (_isUnloaded || PageInfo is null)
        {
            return;
        }

        SubscribeToActiveDocument();
        TouchSelectionDismissRequested?.Invoke();
        _observedDocument?.ClearSelection();
        if (_observedDocument is not null)
        {
            _observedDocument.SelectedAnnotation = null;
        }

        Point clampedStart = ClampToCanvasDip(startInCanvasDip);
        _isDrawingShape = true;
        _shapeDrawIsTouch = true;
        _shapeDrawKind = kind;
        _shapeDrawStartDip = clampedStart;
        _shapeDrawRawEndDip = clampedStart;
        _shapeDrawEndDip = clampedStart;
        _shapeTouchPointerId = 0;
        InvalidateOverlays();
    }

    internal void CompleteExternalTouchShapeDraw(Point? finalInCanvasDip, bool commitIfValid = true)
    {
        if (!_isDrawingShape || !_shapeDrawIsTouch)
        {
            return;
        }

        if (finalInCanvasDip is { } finalPt && PageInfo is not null)
        {
            _shapeDrawRawEndDip = ClampToCanvasDip(finalPt);
        }

        Point finalEndDip = ConstrainShapeEndPointDip(_shapeDrawKind, _shapeDrawStartDip, _shapeDrawRawEndDip, IsShiftKeyDown());
        PdfAnnotationShapeKind commitKind = _shapeDrawKind;
        Point commitStart = _shapeDrawStartDip;

        _isDrawingShape = false;
        _shapeDrawIsTouch = false;
        _shapeTouchPointerId = 0;
        InvalidateOverlays();

        if (commitIfValid &&
            PageInfo is not null &&
            double.Hypot(finalEndDip.X - commitStart.X, finalEndDip.Y - commitStart.Y) >= 5.0)
        {
            ShapeCompleted?.Invoke(this, commitKind, ToPdfPoint(commitStart), ToPdfPoint(finalEndDip));
        }
    }

    internal void UpdateTouchShapeDrawPoint(Point posInCanvasDip)
    {
        if (!_isDrawingShape || !_shapeDrawIsTouch || _isUnloaded || PageInfo is null)
        {
            return;
        }

        _shapeDrawRawEndDip = ClampToCanvasDip(posInCanvasDip);
        _shapeDrawEndDip = ConstrainShapeEndPointDip(_shapeDrawKind, _shapeDrawStartDip, _shapeDrawRawEndDip, IsShiftKeyDown());
        InvalidateOverlays();
    }

    internal void CompleteTouchShapeDrawSession(Point? finalInCanvasDip)
    {
        StopTouchSelectionPolling();
        if (!_isDrawingShape || !_shapeDrawIsTouch)
        {
            return;
        }

        if (finalInCanvasDip is { } finalPt && PageInfo is not null)
        {
            _shapeDrawRawEndDip = ClampToCanvasDip(finalPt);
        }

        Point finalEndDip = ConstrainShapeEndPointDip(_shapeDrawKind, _shapeDrawStartDip, _shapeDrawRawEndDip, IsShiftKeyDown());
        PdfAnnotationShapeKind commitKind = _shapeDrawKind;
        Point commitStart = _shapeDrawStartDip;

        _isDrawingShape = false;
        _shapeDrawIsTouch = false;
        _shapeTouchPointerId = 0;
        _activeCanvasTouchPointerTicks.Clear();

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

        InvalidateOverlays();
        TouchShapeDrawEnded?.Invoke(this);

        if (PageInfo is not null && double.Hypot(finalEndDip.X - commitStart.X, finalEndDip.Y - commitStart.Y) >= 5.0)
        {
            ShapeCompleted?.Invoke(this, commitKind, ToPdfPoint(commitStart), ToPdfPoint(finalEndDip));
        }
    }

    /// <summary>
    /// Cancels any pending touch hold, active touch text selection drag, or active touch annotation drag/resize.
    /// </summary>
    internal void CancelTouchSelectionAndHold()
    {
        CancelShapeDrawing();
        StopTouchSelectionPolling();
        _touchHoldTimer.Stop();
        _isTouchWaitingForHold = false;
        _isTouchWaitingForCanceledTap = false;
        _pendingTouchHoldOpenAnnot = null;
        _touchHoldMovableCandidate = null;
        _touchHoldHitResult = default;
        _hasTouchHoldHitResult = false;
        _pendingTouchReleaseSelection = null;
        _isAwaitingInitialTouchWord = false;
        _touchHoldReleasedBeforeWordResolved = false;
        _touchInitialWordStartCharIndex = -1;
        _touchInitialWordEndCharIndex = -1;
        _lastTouchTapTick = 0;

        if (_isTouchDraggingAnnotation || _isTouchResizingAnnotation)
        {
            _isTouchDraggingAnnotation = false;
            _isTouchResizingAnnotation = false;
            _hasTouchMovedAnnotation = false;
            _isDraggingAnnotation = false;
            _pressedMovableAnnotation = null;
            _annotationDragDeltaDip = default;
            _isResizingAnnotation = false;
            _resizingAnnotation = null;
            _activeResizeHandle = AnnotationResizeHandle.None;
            _annotationResizePreviewDip = default;

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

            InvalidateOverlays();
            TouchAnnotationDragEnded?.Invoke(this);
        }

        if (_isTouchSelecting)
        {
            _selectionSessionId++;
            _isTouchSelecting = false;
            _hasTouchDraggedAfterHold = false;
            _hasDraggedSelection = false;
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

            SelectionDragCanceled?.Invoke(this);
            TouchSelectionEnded?.Invoke(this);
        }
        else
        {
            _touchPointerRef = null;
        }
    }

    private void OnCanvasRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.PointerDeviceType == PointerDeviceType.Touch)
        {
            // Suppress the desktop right-click MenuFlyout on touch long-press so touch uses native text selection + CommandBarFlyout
            e.Handled = true;
        }
    }

    private double GetTouchAnnotationTolerancePt(double minScreenDip = 24.0)
    {
        double zoom = _observedDocument?.ZoomFactor > 0.05 ? _observedDocument.ZoomFactor : 1.0;
        return Math.Clamp(minScreenDip / (PageCoordinateTransform.DipsPerPoint * zoom), 14.0, 56.0);
    }

    private async Task ResolveTouchHoldCandidateAsync(long docId, int pageIndex, Vector2 pdfPt, long sessionId)
    {
        try
        {
            PdfPointerHitResult hit = await _pdfService.HitTestPagePointAsync(
                docId,
                pageIndex,
                pdfPt.X,
                pdfPt.Y,
                tolerancePt: 12.0);

            if (_isUnloaded ||
                _selectionSessionId != sessionId ||
                PageInfo?.Index != pageIndex ||
                EffectiveDocumentId != docId)
            {
                return;
            }

            _touchHoldHitResult = hit;
            _hasTouchHoldHitResult = true;
        }
        catch
        {
        }
    }

    private async void OnTouchHoldTimerTick(object? sender, object e)
    {
        _touchHoldTimer.Stop();
        PruneStaleCanvasTouchPointers(Environment.TickCount64);
        if ((_shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>())?.ActiveTool is AnnotationTool.TextNote or AnnotationTool.InsertText or AnnotationTool.InsertImage)
        {
            _isTouchWaitingForHold = false;
            return;
        }

        if (!_isTouchWaitingForHold ||
            _isUnloaded ||
            PageInfo is not { } pageInfo ||
            _activeCanvasTouchPointerTicks.Count > 1 ||
            !IsTouchSelectInteractionAllowed())
        {
            _isTouchWaitingForHold = false;
            return;
        }

        if (GetPointerInfo(_touchPointerId, out POINTER_INFO holdPi))
        {
            if ((holdPi.pointerFlags & POINTER_FLAG_INCONTACT) == 0 ||
                (holdPi.pointerFlags & POINTER_FLAG_UP) != 0)
            {
                _isTouchWaitingForHold = false;
                return;
            }

            if (_hasTouchStartScreenPx)
            {
                double scale = XamlRoot?.RasterizationScale > 0 ? XamlRoot.RasterizationScale : 1.0;
                double sdx = (holdPi.ptPixelLocation.X - _touchStartScreenPx.X) / scale;
                double sdy = (holdPi.ptPixelLocation.Y - _touchStartScreenPx.Y) / scale;
                if (((sdx * sdx) + (sdy * sdy)) > (TouchHoldMaxMoveDip * TouchHoldMaxMoveDip))
                {
                    _isTouchWaitingForHold = false;
                    return;
                }
            }
        }
        else
        {
            double moveDx = _touchCurrentDipPoint.X - _touchPressDipPoint.X;
            double moveDy = _touchCurrentDipPoint.Y - _touchPressDipPoint.Y;
            if (((moveDx * moveDx) + (moveDy * moveDy)) > (TouchHoldMaxMoveDip * TouchHoldMaxMoveDip))
            {
                _isTouchWaitingForHold = false;
                return;
            }
        }

        long sessionId = _touchHoldSessionId;
        long docId = EffectiveDocumentId;
        int pageIndex = pageInfo.Index;
        Vector2 pdfPt = _touchPressPdfPoint;

        // Priority 1: Hold on a corner handle of an already-selected resizable annotation (e.g., inserted Image) -> touch resize
        if (_observedDocument?.SelectedAnnotation is { IsResizable: true } selResizable &&
            selResizable.PageIndex == pageIndex)
        {
            AnnotationResizeHandle handle = HitTestResizeHandle(selResizable, _touchPressDipPoint, out Rect origRectDip, hitRadiusDip: 22.0);
            if (handle != AnnotationResizeHandle.None)
            {
                ActivateTouchAnnotationResizeFromHold(selResizable, handle, origRectDip);
                return;
            }
        }

        // Priority 2: Hold on ANY movable annotation (Comment, TextBox, Image, Shape, Ink) -> select & touch drag-to-move
        PdfAnnotationInfo? hitMovable = _touchHoldMovableCandidate ??
            (_observedDocument?.HitTestAnnotation(pageIndex, pdfPt.X, pdfPt.Y, tolerancePt: GetTouchAnnotationTolerancePt()) is { IsMovable: true } m ? m : null);
        _touchHoldMovableCandidate = null;
        if (hitMovable is not null)
        {
            ActivateTouchAnnotationMoveFromHold(hitMovable);
            return;
        }

        if (!_hasTouchHoldHitResult)
        {
            try
            {
                PdfPointerHitResult hit = await _pdfService.HitTestPagePointAsync(
                    docId,
                    pageIndex,
                    pdfPt.X,
                    pdfPt.Y,
                    tolerancePt: 12.0);

                if (!_isTouchWaitingForHold ||
                    _isUnloaded ||
                    _selectionSessionId != sessionId ||
                    PageInfo?.Index != pageIndex ||
                    EffectiveDocumentId != docId ||
                    _activeCanvasTouchPointerTicks.Count > 1)
                {
                    _isTouchWaitingForHold = false;
                    return;
                }

                _touchHoldHitResult = hit;
                _hasTouchHoldHitResult = true;
            }
            catch
            {
                _isTouchWaitingForHold = false;
                return;
            }
        }

        if (_touchHoldHitResult.CharIndex < 0)
        {
            _isTouchWaitingForHold = false;
            _isTouchWaitingForCanceledTap = false;
            double moveDx = _touchCurrentDipPoint.X - _touchPressDipPoint.X;
            double moveDy = _touchCurrentDipPoint.Y - _touchPressDipPoint.Y;
            if (((moveDx * moveDx) + (moveDy * moveDy)) > (TouchSingleTapMaxMoveDip * TouchSingleTapMaxMoveDip) ||
                _activeCanvasTouchPointerTicks.Count > 1 ||
                IsSingleTouchViewerManipulating?.Invoke() == true)
            {
                return;
            }

            PdfAnnotationInfo? hitAnnot = _observedDocument?.HitTestAnnotation(
                PageInfo.Index,
                _touchPressPdfPoint.X,
                _touchPressPdfPoint.Y,
                tolerancePt: GetTouchAnnotationTolerancePt());
            if (hitAnnot is not null)
            {
                // Defer opening the modal annotation flyout until the finger lifts so touch-up never immediately light-dismisses it.
                if (!hitAnnot.IsSignatureWidget && _observedDocument is not null)
                {
                    _observedDocument.SelectedAnnotation = hitAnnot;
                }

                _pendingTouchHoldOpenAnnot = hitAnnot;
                StartTouchSelectionPolling();
            }
            else
            {
                TouchEmptyHoldRequested?.Invoke(this, _touchPressPdfPoint, _touchCurrentDipPoint);
            }

            return;
        }

        // Activate interactive touch text selection at 300ms: select the initial word under the finger,
        // and as the user drags their finger, continuously select the text range passed over by the finger.
        _isTouchWaitingForHold = false;
        _isTouchWaitingForCanceledTap = false;
        _isTouchSelecting = true;
        _hasTouchDraggedAfterHold = false;
        _hasDraggedSelection = false;
        _touchHoldActivatedDipPoint = _touchCurrentDipPoint;
        _pressDipPoint = _touchPressDipPoint;
        _pressPdfPoint = _touchPressPdfPoint;
        _lastDragDipPoint = _touchCurrentDipPoint;
        _selectionAnchorCharIndex = _touchHoldHitResult.CharIndex;
        _touchInitialWordStartCharIndex = _touchHoldHitResult.CharIndex;
        _touchInitialWordEndCharIndex = _touchHoldHitResult.CharIndex;
        _lastDragEndCharIndex = -2;
        _pendingTouchReleaseSelection = null;

        SubscribeToActiveDocument();
        TouchSelectionStarted?.Invoke(this);
        if (_touchPointerRef is not null && IsSingleTouchViewerManipulating?.Invoke() != true)
        {
            try
            {
                _ = CapturePointer(_touchPointerRef);
            }
            catch
            {
            }
        }

        StartTouchSelectionPolling();
        FocusParentViewerIfNeeded();

        _isAwaitingInitialTouchWord = true;
        _touchHoldReleasedBeforeWordResolved = false;
        try
        {
            PdfTextSelection? initialWord = await _pdfService.SelectWordAtPointAsync(
                docId,
                pageIndex,
                pdfPt.X,
                pdfPt.Y,
                tolerancePt: 12.0);

            if (!_isUnloaded &&
                _selectionSessionId == sessionId &&
                PageInfo?.Index == pageIndex &&
                EffectiveDocumentId == docId &&
                initialWord is { HasContent: true })
            {
                if (_isTouchSelecting)
                {
                    _touchInitialWordStartCharIndex = initialWord.StartCharIndex;
                    _touchInitialWordEndCharIndex = initialWord.EndCharIndex;
                    _selectionAnchorCharIndex = initialWord.StartCharIndex;
                    if (!_hasTouchDraggedAfterHold)
                    {
                        _observedDocument?.SetSelection(initialWord);
                        _pendingTouchReleaseSelection = initialWord;
                    }
                    else if (SelectionDragUpdated is not null)
                    {
                        SelectionDragUpdated.Invoke(
                            this,
                            sessionId,
                            _selectionAnchorCharIndex,
                            _pressPdfPoint,
                            _touchCurrentDipPoint,
                            true,
                            pageIndex,
                            _touchInitialWordStartCharIndex,
                            _touchInitialWordEndCharIndex);
                    }
                    else
                    {
                        RequestDragSelectionUpdate(_touchCurrentDipPoint, force: true, sessionId);
                    }
                }
                else if (_touchHoldReleasedBeforeWordResolved)
                {
                    _touchHoldReleasedBeforeWordResolved = false;
                    _observedDocument?.SetSelection(initialWord);
                    TouchSelectionCompleted?.Invoke(this, initialWord);
                }
            }
        }
        catch
        {
        }
        finally
        {
            _isAwaitingInitialTouchWord = false;
        }
    }

    /// <summary>
    /// Updates the current finger position in local canvas DIPs during a touch hold or active touch drag selection.
    /// </summary>
    /// <param name="posDip">Finger position in local canvas DIPs.</param>
    internal void UpdateTouchSelectionDragPoint(Point posDip)
    {
        if (_isUnloaded || PageInfo is null)
        {
            return;
        }

        _touchCurrentDipPoint = posDip;

        if (_isTouchWaitingForHold)
        {
            double hdx = posDip.X - _touchPressDipPoint.X;
            double hdy = posDip.Y - _touchPressDipPoint.Y;
            if (((hdx * hdx) + (hdy * hdy)) > (TouchHoldMaxMoveDip * TouchHoldMaxMoveDip))
            {
                CancelPendingTouchHold();
            }

            return;
        }

        if (!_isTouchSelecting)
        {
            return;
        }

        double dx = posDip.X - _touchHoldActivatedDipPoint.X;
        double dy = posDip.Y - _touchHoldActivatedDipPoint.Y;
        bool isFirstDragFrame = false;
        if (!_hasTouchDraggedAfterHold && ((dx * dx) + (dy * dy)) >= (TouchDragStartThresholdDip * TouchDragStartThresholdDip))
        {
            _hasTouchDraggedAfterHold = true;
            _hasDraggedSelection = true;
            _pendingTouchReleaseSelection = null;
            isFirstDragFrame = true;
        }

        if (_hasTouchDraggedAfterHold)
        {
            if (SelectionDragUpdated is not null)
            {
                SelectionDragUpdated.Invoke(
                    this,
                    _selectionSessionId,
                    _selectionAnchorCharIndex,
                    _pressPdfPoint,
                    posDip,
                    true,
                    PageInfo.Index,
                    _touchInitialWordStartCharIndex,
                    _touchInitialWordEndCharIndex);
            }
            else
            {
                RequestDragSelectionUpdate(posDip, force: isFirstDragFrame, _selectionSessionId);
            }
        }
    }

    /// <summary>
    /// Completes an active touch text selection session when the user lifts their finger and raises <see cref="TouchSelectionCompleted"/>.
    /// </summary>
    /// <param name="finalDipPoint">Optional final finger position in local canvas DIPs.</param>
    internal void CompleteTouchSelectionSession(Point? finalDipPoint = null)
    {
        StopTouchSelectionPolling();
        if (!_isTouchSelecting)
        {
            if (_isTouchWaitingForHold)
            {
                HandleTouchQuickRelease(finalDipPoint ?? _touchCurrentDipPoint);
            }

            return;
        }

        bool hadDragged = _hasTouchDraggedAfterHold;
        long activeSession = _selectionSessionId;
        int anchorChar = _selectionAnchorCharIndex;
        Vector2 anchorPdf = _pressPdfPoint;
        int pageIndex = PageInfo?.Index ?? -1;
        int initialWordStart = _touchInitialWordStartCharIndex;
        int initialWordEnd = _touchInitialWordEndCharIndex;
        Point endPointDip = finalDipPoint ?? _touchCurrentDipPoint;

        _isTouchSelecting = false;
        _isTouchWaitingForHold = false;
        _hasTouchDraggedAfterHold = false;
        _hasDraggedSelection = false;
        _touchInitialWordStartCharIndex = -1;
        _touchInitialWordEndCharIndex = -1;
        _touchHoldTimer.Stop();
        _activeCanvasTouchPointerTicks.Clear();

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

        TouchSelectionEnded?.Invoke(this);
        FocusParentViewerIfNeeded();

        if (hadDragged)
        {
            if (SelectionDragCompleted is not null)
            {
                SelectionDragCompleted.Invoke(
                    this,
                    activeSession,
                    anchorChar,
                    anchorPdf,
                    endPointDip,
                    true,
                    pageIndex,
                    initialWordStart,
                    initialWordEnd);
            }
            else
            {
                _ = FinalizeTouchDragSelectionAsync(endPointDip, activeSession, pageIndex, initialWordStart, initialWordEnd);
            }
        }
        else
        {
            PdfTextSelection? activeSel = _observedDocument?.ActiveSelection ?? _pendingTouchReleaseSelection;
            _pendingTouchReleaseSelection = null;
            if (activeSel is { HasContent: true })
            {
                TouchSelectionCompleted?.Invoke(this, activeSel);
            }
            else if (_isAwaitingInitialTouchWord)
            {
                _touchHoldReleasedBeforeWordResolved = true;
            }
        }
    }

    /// <summary>
    /// Begins an interactive selection boundary drag from one of the Edge-style touch selection handles.
    /// </summary>
    /// <param name="fixedAnchorCharIndex">The character index of the opposite (fixed) end of the selection.</param>
    /// <param name="fixedAnchorPdfPoint">The PDF coordinates of the opposite (fixed) end of the selection.</param>
    internal void BeginHandleSelectionDrag(int fixedAnchorCharIndex, Vector2 fixedAnchorPdfPoint)
    {
        _selectionSessionId++;
        _selectionAnchorCharIndex = fixedAnchorCharIndex;
        _pressPdfPoint = fixedAnchorPdfPoint;
        _touchInitialWordStartCharIndex = -1;
        _touchInitialWordEndCharIndex = -1;
        _lastDragEndCharIndex = -2;
        _lastDragDipPoint = new Point(-1000, -1000);
        _hasDraggedSelection = true;
    }

    /// <summary>
    /// Updates an active selection handle drag to the specified target character position in local canvas DIPs.
    /// </summary>
    /// <param name="targetCharDipPoint">Target character position in local canvas DIPs.</param>
    /// <param name="force">Whether to bypass the minimum movement delta filter.</param>
    internal void UpdateHandleSelectionDrag(Point targetCharDipPoint, bool force = false)
    {
        if (_isUnloaded || PageInfo is null)
        {
            return;
        }

        _hasDraggedSelection = true;
        RequestDragSelectionUpdate(targetCharDipPoint, force, _selectionSessionId);
    }

    /// <summary>
    /// Completes an active selection handle drag and raises <see cref="TouchSelectionCompleted"/>.
    /// </summary>
    /// <param name="finalTargetCharDipPoint">Final target character position in local canvas DIPs.</param>
    internal void EndHandleSelectionDrag(Point finalTargetCharDipPoint)
    {
        if (_isUnloaded || PageInfo is null)
        {
            _hasDraggedSelection = false;
            return;
        }

        long activeSession = _selectionSessionId;
        _hasDraggedSelection = false;
        _ = FinalizeTouchDragSelectionAsync(finalTargetCharDipPoint, activeSession, -1, -1, -1);
    }

    internal bool BeginExternalTouchAnnotationResize(PdfAnnotationInfo annot, int cornerIndex, out Rect currentPreviewDip)
    {
        currentPreviewDip = default;
        if (!annot.IsResizable || PageInfo is null || annot.PageIndex != PageInfo.Index || annot.Bounds.IsEmpty)
        {
            return false;
        }

        AnnotationResizeHandle handle = cornerIndex switch
        {
            0 => AnnotationResizeHandle.TopLeft,
            1 => AnnotationResizeHandle.TopRight,
            2 => AnnotationResizeHandle.BottomLeft,
            _ => AnnotationResizeHandle.BottomRight,
        };

        PageCoordinateTransform transform = PageCoordinateTransform.FromPage(PageInfo, RotationAngle);
        Rect origRectDip = transform.ToPageRect(annot.Bounds);
        if (origRectDip.Width < 4.0 || origRectDip.Height < 4.0)
        {
            return false;
        }

        CancelPendingTouchHold();
        _isTouchResizingAnnotation = true;
        _isResizingAnnotation = true;
        _resizingAnnotation = annot;
        _activeResizeHandle = handle;
        _resizeOrigRectDip = origRectDip;
        _annotationResizePreviewDip = origRectDip;
        currentPreviewDip = origRectDip;
        InvalidateOverlays();
        return true;
    }

    internal Rect UpdateExternalTouchAnnotationResize(Point posInCanvasDip)
    {
        if (!_isTouchResizingAnnotation || _resizingAnnotation is null || PageInfo is null)
        {
            return _annotationResizePreviewDip;
        }

        UpdateAnnotationResizePreview(posInCanvasDip);
        InvalidateOverlays();
        return _annotationResizePreviewDip;
    }

    internal void CompleteExternalTouchAnnotationResize(Point? finalInCanvasDip)
    {
        if (!_isTouchResizingAnnotation)
        {
            return;
        }

        if (finalInCanvasDip is { } finalPt && _resizingAnnotation is not null && PageInfo is not null)
        {
            UpdateAnnotationResizePreview(finalPt);
        }

        PdfAnnotationInfo? resizedAnnot = _resizingAnnotation;
        Rect resizePreviewDip = _annotationResizePreviewDip;

        _isTouchResizingAnnotation = false;
        _isResizingAnnotation = false;
        _resizingAnnotation = null;
        _activeResizeHandle = AnnotationResizeHandle.None;
        _annotationResizePreviewDip = default;
        InvalidateOverlays();

        if (resizedAnnot is not null && PageInfo is not null && resizePreviewDip.Width > 1.0 && resizePreviewDip.Height > 1.0)
        {
            PageCoordinateTransform transform = PageCoordinateTransform.FromPage(PageInfo, RotationAngle);
            PdfRect newPdfBounds = transform.ToPdfRect(resizePreviewDip);
            if (Math.Abs(newPdfBounds.Width - resizedAnnot.Bounds.Width) > 0.5f ||
                Math.Abs(newPdfBounds.Height - resizedAnnot.Bounds.Height) > 0.5f ||
                Math.Abs(newPdfBounds.Left - resizedAnnot.Bounds.Left) > 0.5f ||
                Math.Abs(newPdfBounds.Bottom - resizedAnnot.Bounds.Bottom) > 0.5f)
            {
                AnnotationResized?.Invoke(this, resizedAnnot, newPdfBounds);
            }
        }
    }

    internal void CancelExternalTouchAnnotationResize()
    {
        if (!_isTouchResizingAnnotation && !_isResizingAnnotation)
        {
            return;
        }

        _isTouchResizingAnnotation = false;
        _isResizingAnnotation = false;
        _resizingAnnotation = null;
        _activeResizeHandle = AnnotationResizeHandle.None;
        _annotationResizePreviewDip = default;
        InvalidateOverlays();
    }

    internal bool BeginExternalTouchAnnotationMove(PdfAnnotationInfo annot, Point pressInCanvasDip, out Rect origRectDip)
    {
        origRectDip = default;
        if (!annot.IsMovable || PageInfo is null || annot.PageIndex != PageInfo.Index || annot.Bounds.IsEmpty)
        {
            return false;
        }

        PageCoordinateTransform transform = PageCoordinateTransform.FromPage(PageInfo, RotationAngle);
        origRectDip = transform.ToPageRect(annot.Bounds);
        if (origRectDip.Width < 1.0 || origRectDip.Height < 1.0)
        {
            return false;
        }

        CancelPendingTouchHold();
        _isTouchDraggingAnnotation = true;
        _hasTouchMovedAnnotation = false;
        _isDraggingAnnotation = true;
        _pressedMovableAnnotation = annot;
        _annotationDragDeltaDip = default;
        _pressDipPoint = pressInCanvasDip;
        _pressPdfPoint = ToPdfPoint(pressInCanvasDip);
        _lastTouchAnnotDragCanvasDip = pressInCanvasDip;
        InvalidateOverlays();
        return true;
    }

    internal Rect UpdateExternalTouchAnnotationMove(Point posInCanvasDip, out bool hasMoved)
    {
        hasMoved = _hasTouchMovedAnnotation;
        if (!_isTouchDraggingAnnotation || _pressedMovableAnnotation is not { } dragged || PageInfo is null)
        {
            return default;
        }

        _lastTouchAnnotDragCanvasDip = posInCanvasDip;
        double dx = posInCanvasDip.X - _pressDipPoint.X;
        double dy = posInCanvasDip.Y - _pressDipPoint.Y;

        PageCoordinateTransform transform = PageCoordinateTransform.FromPage(PageInfo, RotationAngle);
        Rect origDip = transform.ToPageRect(dragged.Bounds);
        bool swap = PageCoordinateTransform.NormalizeRotation(PageInfo.Rotation + RotationAngle) is 90 or 270;
        double pageW = ActualWidth > 1.0 ? ActualWidth : (swap ? PageInfo.HeightPt : PageInfo.WidthPt) * PageCoordinateTransform.DipsPerPoint;
        double pageH = ActualHeight > 1.0 ? ActualHeight : (swap ? PageInfo.WidthPt : PageInfo.HeightPt) * PageCoordinateTransform.DipsPerPoint;
        double minDx = -origDip.Left;
        double maxDx = Math.Max(minDx, pageW - origDip.Right);
        double minDy = -origDip.Top;
        double maxDy = Math.Max(minDy, pageH - origDip.Bottom);
        dx = Math.Clamp(dx, minDx, maxDx);
        dy = Math.Clamp(dy, minDy, maxDy);

        if (!_hasTouchMovedAnnotation && ((dx * dx) + (dy * dy)) >= 16.0)
        {
            _hasTouchMovedAnnotation = true;
        }

        hasMoved = _hasTouchMovedAnnotation;
        _annotationDragDeltaDip = new Point(dx, dy);
        InvalidateOverlays();
        return new Rect(origDip.Left + dx, origDip.Top + dy, origDip.Width, origDip.Height);
    }

    internal bool CompleteExternalTouchAnnotationMove(Point? finalInCanvasDip)
    {
        if (!_isTouchDraggingAnnotation)
        {
            return false;
        }

        if (finalInCanvasDip is { } finalPt && _pressedMovableAnnotation is not null && PageInfo is not null)
        {
            _ = UpdateExternalTouchAnnotationMove(finalPt, out _);
        }

        PdfAnnotationInfo? draggedAnnot = _pressedMovableAnnotation;
        bool hadMoved = _hasTouchMovedAnnotation;
        Point dragDeltaDip = _annotationDragDeltaDip;
        Point pressDip = _pressDipPoint;
        Vector2 pressPdf = _pressPdfPoint;

        _isTouchDraggingAnnotation = false;
        _hasTouchMovedAnnotation = false;
        _isDraggingAnnotation = false;
        _pressedMovableAnnotation = null;
        _annotationDragDeltaDip = default;
        InvalidateOverlays();

        if (hadMoved && draggedAnnot is not null && PageInfo is not null)
        {
            Vector2 targetPdf = ToPdfPoint(new Point(pressDip.X + dragDeltaDip.X, pressDip.Y + dragDeltaDip.Y));
            Vector2 deltaPdf = targetPdf - pressPdf;
            if (MathF.Abs(deltaPdf.X) > 0.5f || MathF.Abs(deltaPdf.Y) > 0.5f)
            {
                AnnotationMoved?.Invoke(this, draggedAnnot, deltaPdf);
                return true;
            }
        }

        return false;
    }

    internal void CancelExternalTouchAnnotationMove()
    {
        if (!_isTouchDraggingAnnotation && !_isDraggingAnnotation)
        {
            return;
        }

        _isTouchDraggingAnnotation = false;
        _hasTouchMovedAnnotation = false;
        _isDraggingAnnotation = false;
        _pressedMovableAnnotation = null;
        _annotationDragDeltaDip = default;
        InvalidateOverlays();
    }

    private void ActivateTouchAnnotationMoveFromHold(PdfAnnotationInfo hitMovable)
    {
        _isTouchWaitingForHold = false;
        _isTouchWaitingForCanceledTap = false;
        _pendingTouchHoldOpenAnnot = null;
        _isTouchDraggingAnnotation = true;
        _hasTouchMovedAnnotation = false;
        _pressedMovableAnnotation = hitMovable;
        _isDraggingAnnotation = true;
        _annotationDragDeltaDip = default;
        _pressDipPoint = _touchPressDipPoint;
        _pressPdfPoint = _touchPressPdfPoint;
        _lastTouchAnnotDragCanvasDip = _touchCurrentDipPoint;

        SubscribeToActiveDocument();
        _observedDocument?.ClearSelection();
        if (_observedDocument is not null)
        {
            _observedDocument.SelectedAnnotation = hitMovable;
        }

        TouchAnnotationDragStarted?.Invoke(this, _touchPointerId);
        if (_touchPointerRef is not null)
        {
            try
            {
                _ = CapturePointer(_touchPointerRef);
            }
            catch
            {
            }
        }

        StartTouchSelectionPolling();
        InvalidateOverlays();
    }

    private void ActivateTouchAnnotationResizeFromHold(
        PdfAnnotationInfo selResizable,
        AnnotationResizeHandle handle,
        Rect origRectDip)
    {
        _isTouchWaitingForHold = false;
        _isTouchWaitingForCanceledTap = false;
        _pendingTouchHoldOpenAnnot = null;
        _isTouchResizingAnnotation = true;
        _isResizingAnnotation = true;
        _resizingAnnotation = selResizable;
        _activeResizeHandle = handle;
        _resizeOrigRectDip = origRectDip;
        _annotationResizePreviewDip = origRectDip;
        _pressDipPoint = _touchPressDipPoint;
        _pressPdfPoint = _touchPressPdfPoint;
        _lastTouchAnnotDragCanvasDip = _touchCurrentDipPoint;

        SubscribeToActiveDocument();
        TouchAnnotationDragStarted?.Invoke(this, _touchPointerId);
        if (_touchPointerRef is not null)
        {
            try
            {
                _ = CapturePointer(_touchPointerRef);
            }
            catch
            {
            }
        }

        StartTouchSelectionPolling();
        InvalidateOverlays();
    }

    /// <summary>
    /// Updates the live preview position or corner-resize bounds during an active touch annotation drag session.
    /// </summary>
    /// <param name="posDip">Finger position in local canvas DIPs.</param>
    internal void UpdateTouchAnnotationDragPoint(Point posDip)
    {
        if (_isUnloaded || PageInfo is null)
        {
            return;
        }

        _lastTouchAnnotDragCanvasDip = posDip;
        _touchCurrentDipPoint = posDip;

        if (_isTouchResizingAnnotation && _resizingAnnotation is not null)
        {
            UpdateAnnotationResizePreview(posDip);
            InvalidateOverlays();
            return;
        }

        if (_isTouchDraggingAnnotation && _pressedMovableAnnotation is { } dragged)
        {
            double dx = posDip.X - _pressDipPoint.X;
            double dy = posDip.Y - _pressDipPoint.Y;

            if (!dragged.Bounds.IsEmpty)
            {
                PageCoordinateTransform transform = PageCoordinateTransform.FromPage(PageInfo, RotationAngle);
                Rect origDip = transform.ToPageRect(dragged.Bounds);
                bool swap = PageCoordinateTransform.NormalizeRotation(PageInfo.Rotation + RotationAngle) is 90 or 270;
                double pageW = ActualWidth > 1.0 ? ActualWidth : (swap ? PageInfo.HeightPt : PageInfo.WidthPt) * PageCoordinateTransform.DipsPerPoint;
                double pageH = ActualHeight > 1.0 ? ActualHeight : (swap ? PageInfo.WidthPt : PageInfo.HeightPt) * PageCoordinateTransform.DipsPerPoint;
                double minDx = -origDip.Left;
                double maxDx = Math.Max(minDx, pageW - origDip.Right);
                double minDy = -origDip.Top;
                double maxDy = Math.Max(minDy, pageH - origDip.Bottom);
                dx = Math.Clamp(dx, minDx, maxDx);
                dy = Math.Clamp(dy, minDy, maxDy);
            }

            if (!_hasTouchMovedAnnotation && ((dx * dx) + (dy * dy)) >= 9.0)
            {
                _hasTouchMovedAnnotation = true;
            }

            _annotationDragDeltaDip = new Point(dx, dy);
            InvalidateOverlays();
        }
    }

    /// <summary>
    /// Completes an active touch annotation drag-to-move or corner-resize session when the dragging finger lifts.
    /// </summary>
    /// <param name="finalDipPoint">Optional final finger position in local canvas DIPs.</param>
    internal void CompleteTouchAnnotationDragSession(Point? finalDipPoint = null)
    {
        StopTouchSelectionPolling();
        if (!_isTouchDraggingAnnotation && !_isTouchResizingAnnotation)
        {
            return;
        }

        if (finalDipPoint is { } finalPt)
        {
            UpdateTouchAnnotationDragPoint(finalPt);
        }

        bool wasResizing = _isTouchResizingAnnotation;
        PdfAnnotationInfo? resizedAnnot = _resizingAnnotation;
        Rect resizePreviewDip = _annotationResizePreviewDip;
        PdfAnnotationInfo? draggedAnnot = _pressedMovableAnnotation;
        bool hadMoved = _hasTouchMovedAnnotation;
        Point dragDeltaDip = _annotationDragDeltaDip;
        Point pressDip = _pressDipPoint;
        Vector2 pressPdf = _pressPdfPoint;
        Point releaseDip = finalDipPoint ?? _lastTouchAnnotDragCanvasDip;

        _isTouchDraggingAnnotation = false;
        _isTouchResizingAnnotation = false;
        _hasTouchMovedAnnotation = false;
        _isTouchWaitingForHold = false;
        _isTouchWaitingForCanceledTap = false;
        _isDraggingAnnotation = false;
        _pressedMovableAnnotation = null;
        _annotationDragDeltaDip = default;
        _isResizingAnnotation = false;
        _resizingAnnotation = null;
        _activeResizeHandle = AnnotationResizeHandle.None;
        _annotationResizePreviewDip = default;
        _touchHoldTimer.Stop();
        _activeCanvasTouchPointerTicks.Clear();

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

        InvalidateOverlays();
        TouchAnnotationDragEnded?.Invoke(this);
        FocusParentViewerIfNeeded();

        if (wasResizing && resizedAnnot is not null && PageInfo is not null)
        {
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

        if (draggedAnnot is not null && PageInfo is not null)
        {
            if (hadMoved)
            {
                Vector2 targetPdf = ToPdfPoint(new Point(pressDip.X + dragDeltaDip.X, pressDip.Y + dragDeltaDip.Y));
                Vector2 deltaPdf = targetPdf - pressPdf;
                if (MathF.Abs(deltaPdf.X) > 0.5f || MathF.Abs(deltaPdf.Y) > 0.5f)
                {
                    AnnotationMoved?.Invoke(this, draggedAnnot, deltaPdf);
                }
            }
            else if (_observedDocument is not null)
            {
                _observedDocument.SelectedAnnotation = draggedAnnot;
                AnnotationClicked?.Invoke(this, draggedAnnot, releaseDip);
            }
        }
    }

    /// <summary>
    /// Handles a quick touch release (before the 300ms hold timer fired) to activate links, select annotations, toggle double-tap zoom, or clear any existing selection.
    /// </summary>
    /// <param name="releaseDip">Finger release position in local canvas DIPs.</param>
    /// <returns><see langword="true"/> if an annotation, signature, link, placement tool, or double-tap zoom was activated.</returns>
    internal bool HandleTouchQuickRelease(Point releaseDip)
    {
        bool wasWaitingForHold = _isTouchWaitingForHold;
        PdfPointerHitResult holdHit = _touchHoldHitResult;
        bool hasHoldHit = _hasTouchHoldHitResult;
        long prevTapTick = _lastTouchTapTick;
        Point prevTapRootPt = _lastTouchTapRootPoint;
        CancelPendingTouchHold(preserveTapHistory: true);

        if (_pendingTouchReleaseSelection is { HasContent: true } pendingSel)
        {
            _pendingTouchReleaseSelection = null;
            _lastTouchTapTick = 0;
            TouchSelectionCompleted?.Invoke(this, pendingSel);
            return true;
        }

        long nowTouch = Environment.TickCount64;
        if (nowTouch - _lastDoubleTapTick <= 300 ||
            nowTouch - _lastTripleTapTick <= MultiClickMaxIntervalMs)
        {
            _lastTouchTapTick = 0;
            return false;
        }

        if (!wasWaitingForHold)
        {
            _lastTouchTapTick = 0;
            return false;
        }

        AnnotationTool activeTool = (_shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>())?.ActiveTool ?? AnnotationTool.Select;
        bool isPlacementTool = activeTool is AnnotationTool.TextNote or AnnotationTool.InsertText or AnnotationTool.InsertImage;
        long maxTapDurationMs = isPlacementTool ? 550 : TouchSingleTapMaxDurationMs;
        double maxTapMoveDip = isPlacementTool ? 14.0 : TouchSingleTapMaxMoveDip;

        long pressDurationMs = nowTouch - _touchPressTick;
        double tdx = releaseDip.X - _touchPressDipPoint.X;
        double tdy = releaseDip.Y - _touchPressDipPoint.Y;
        bool isCleanStationaryTap = pressDurationMs >= 0 &&
                                    pressDurationMs <= maxTapDurationMs &&
                                    ((tdx * tdx) + (tdy * tdy)) <= (maxTapMoveDip * maxTapMoveDip);

        if (isCleanStationaryTap && _hasTouchStartScreenPx && GetPointerInfo(_touchPointerId, out POINTER_INFO relPi))
        {
            double scale = XamlRoot?.RasterizationScale > 0 ? XamlRoot.RasterizationScale : 1.0;
            double sdx = (relPi.ptPixelLocation.X - _touchStartScreenPx.X) / scale;
            double sdy = (relPi.ptPixelLocation.Y - _touchStartScreenPx.Y) / scale;
            if (((sdx * sdx) + (sdy * sdy)) > (maxTapMoveDip * maxTapMoveDip))
            {
                isCleanStationaryTap = false;
            }
        }

        if (!isCleanStationaryTap)
        {
            _lastTouchTapTick = 0;
            return false;
        }

        if (hasHoldHit && holdHit.Link.IsValid)
        {
            _lastTouchTapTick = 0;
            _observedDocument?.ClearSelection();
            if (_observedDocument is not null)
            {
                _observedDocument.SelectedAnnotation = null;
            }
            ShellViewModel? shell = _shellViewModel ??= App.CurrentApp.Services.GetService<ShellViewModel>();
            if (shell is not null)
            {
                _ = shell.HandleLinkClickedAsync(holdHit.Link);
            }

            return true;
        }

        if (PageInfo is not null && _observedDocument is { } tappedDoc)
        {
            if (activeTool == AnnotationTool.InsertImage)
            {
                _lastTouchTapTick = 0;
                tappedDoc.ClearSelection();
                tappedDoc.SelectedAnnotation = null;
                PlaceImageRequested?.Invoke(this, _touchPressPdfPoint, releaseDip);
                return true;
            }

            if (activeTool == AnnotationTool.InsertText)
            {
                _lastTouchTapTick = 0;
                tappedDoc.ClearSelection();
                tappedDoc.SelectedAnnotation = null;
                PlaceTextBoxRequested?.Invoke(this, _touchPressPdfPoint, releaseDip);
                return true;
            }

            PdfAnnotationInfo? hitAnnot = tappedDoc.HitTestAnnotation(
                PageInfo.Index,
                _touchPressPdfPoint.X,
                _touchPressPdfPoint.Y,
                tolerancePt: GetTouchAnnotationTolerancePt());
            if (hitAnnot is not null)
            {
                if (hitAnnot.IsSignatureWidget && tappedDoc.GetSignatureByIndex(hitAnnot.SignatureIndex) is { } sig)
                {
                    _lastTouchTapTick = 0;
                    tappedDoc.ClearSelection();
                    tappedDoc.SelectedAnnotation = null;
                    SignatureClicked?.Invoke(this, sig, releaseDip);
                    return true;
                }

                if (!hitAnnot.IsSignatureWidget)
                {
                    _lastTouchTapTick = 0;
                    tappedDoc.ClearSelection();
                    tappedDoc.SelectedAnnotation = hitAnnot;
                    AnnotationClicked?.Invoke(this, hitAnnot, releaseDip);
                    return true;
                }
            }

            if (activeTool == AnnotationTool.TextNote)
            {
                _lastTouchTapTick = 0;
                tappedDoc.ClearSelection();
                tappedDoc.SelectedAnnotation = null;
                PlaceTextNoteRequested?.Invoke(this, _touchPressPdfPoint, releaseDip);
                return true;
            }

            // Record clean touch tap location on page so Cut/Copy + Paste (Ctrl+V) anchors right where the user touched.
            MouseClickedOnPage?.Invoke(this, _touchPressPdfPoint);
        }

        Point curRootPt = new(_touchPressRootDipPoint.X + tdx, _touchPressRootDipPoint.Y + tdy);
        double tapDistX = curRootPt.X - prevTapRootPt.X;
        double tapDistY = curRootPt.Y - prevTapRootPt.Y;
        bool sameTapSpot = ((tapDistX * tapDistX) + (tapDistY * tapDistY)) <= (TouchDoubleTapMaxDistanceDip * TouchDoubleTapMaxDistanceDip);

        if (prevTapTick > 0 &&
            sameTapSpot &&
            (nowTouch - prevTapTick <= TouchDoubleTapMaxIntervalMs) &&
            (nowTouch - _lastDoubleTapTick > 300))
        {
            _lastDoubleTapTick = nowTouch;
            _lastTouchTapTick = 0;
            _clickCount = 0;
            _observedDocument?.ClearSelection();
            if (_observedDocument is not null)
            {
                _observedDocument.SelectedAnnotation = null;
            }
            TouchSelectionDismissRequested?.Invoke();
            TouchDoubleTapZoomRequested?.Invoke(this, releaseDip);
            return true;
        }

        _lastTouchTapTick = nowTouch;
        _lastTouchTapRootPoint = curRootPt;
        _observedDocument?.ClearSelection();
        if (_observedDocument is not null)
        {
            _observedDocument.SelectedAnnotation = null;
        }
        TouchSelectionDismissRequested?.Invoke();
        return false;
    }

    private async Task FinalizeTouchDragSelectionAsync(
        Point endPointDip,
        long sessionId,
        int initialWordPageIndex,
        int initialWordStart,
        int initialWordEnd)
    {
        try
        {
            if (_isUnloaded || PageInfo is not { } pageInfo || EffectiveDocumentId <= 0)
            {
                return;
            }

            double clampedX = Math.Clamp(endPointDip.X, 0.0, Math.Max(1.0, ActualWidth > 0 ? ActualWidth : Width));
            double clampedY = Math.Clamp(endPointDip.Y, 0.0, Math.Max(1.0, ActualHeight > 0 ? ActualHeight : Height));
            Point clampedDip = new(clampedX, clampedY);

            long docId = EffectiveDocumentId;
            int pageIndex = pageInfo.Index;
            int anchor = _selectionAnchorCharIndex;
            Vector2 anchorPdf = _pressPdfPoint;
            Vector2 pdfPt = ToPdfPoint(clampedDip);

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
                initialWordPageIndex,
                initialWordStart,
                initialWordEnd,
                -1,
                -2);

            if (_isUnloaded || _selectionSessionId != sessionId || PageInfo?.Index != pageIndex || EffectiveDocumentId != docId)
            {
                return;
            }

            if (updated is { HasContent: true })
            {
                _observedDocument?.SetSelection(updated);
                TouchSelectionCompleted?.Invoke(this, updated);
            }
            else if (_observedDocument?.ActiveSelection is { HasContent: true } existingSel &&
                     (existingSel.PageIndex == pageIndex || existingSel.GetRectsForPage(pageIndex).Count > 0))
            {
                TouchSelectionCompleted?.Invoke(this, existingSel);
            }
        }
        catch
        {
        }
    }

}
