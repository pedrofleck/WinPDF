using System.Runtime.InteropServices;
using Windows.Foundation;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinPDF.Controls;
using WinPDF.Models;
using WinPDF.ViewModels;

namespace WinPDF.Views;

public sealed partial class DocumentView
{
    // Interactive horizontal slide swipe state (1-finger Touch & 2-finger Precision Touchpad in Paged/Presentation mode)
    private enum PagedTouchGestureAxis
    {
        Undecided = 0,
        HorizontalSwipe = 1,
        PanOrZoom = 2,
    }

    private const ManipulationModes PagedCustomManipulationMode =
        ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.Scale;
    private const double SlideSwipeDirectionLockDip = 6.0;
    private const double SlideSwipeMinCommitRatio = 0.20;
    private const double SlideSwipeMinFlickVelocityDipPerSec = 480.0;
    private const double SlideSwipeMinFlickDistanceDip = 24.0;
    private const double SlideSwipeRubberBandFactor = 0.26;
    private const double TouchpadWheelToDipScale = 1.10;
    private const uint WM_ACTIVATE = 0x0006;
    private const uint WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIM_TYPEHID = 2;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const ushort HID_USAGE_PAGE_DIGITIZER = 0x000D;
    private const ushort HID_USAGE_DIGITIZER_TOUCH_PAD = 0x0005;
    private const ushort HID_USAGE_DIGITIZER_TIP_SWITCH = 0x0042;
    private const ushort HID_USAGE_DIGITIZER_CONTACT_ID = 0x0051;
    private const ushort HID_USAGE_DIGITIZER_CONTACT_COUNT = 0x0054;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private static readonly UIntPtr TouchpadRawInputSubclassId = new(0x57505450);

    private delegate IntPtr TouchpadSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    private sealed class TouchpadHidDeviceInfo
    {
        public required byte[] PreparsedData { get; init; }
        public required ushort MaxLinkCollection { get; init; }
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, IntPtr pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(in RAWINPUTDEVICE pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr PreparsedData, out HIDP_CAPS Capabilities);

    [DllImport("hid.dll")]
    private static extern unsafe int HidP_GetUsages(
        int ReportType,
        ushort UsagePage,
        ushort LinkCollection,
        ushort* UsageList,
        ref uint UsageLength,
        IntPtr PreparsedData,
        IntPtr Report,
        uint ReportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(
        int ReportType,
        ushort UsagePage,
        ushort LinkCollection,
        ushort Usage,
        out uint UsageValue,
        IntPtr PreparsedData,
        IntPtr Report,
        uint ReportLength);

    private readonly DispatcherTimer _touchpadSwipeEndTimer = new() { Interval = TimeSpan.FromMilliseconds(115) };

    private readonly Dictionary<IntPtr, TouchpadHidDeviceInfo?> _touchpadHidDevices = new();
    private readonly Dictionary<uint, long> _activeHybridTouchpadContacts = new();
    private readonly ushort[] _hidUsageBuffer = new ushort[32];
    private byte[] _rawInputBuffer = new byte[512];
    private IntPtr _touchpadRawInputHwnd;
    private TouchpadSubclassProc? _touchpadSubclassDelegate;
    private IntPtr _touchpadSubclassPtr;
    private bool _hasReceivedTouchpadRawInput;
    private int _activeTouchpadFingerCount;
    private long _lastTouchpadRawInputTick;
    private bool _touchpadSwipeCompletedSinceLastTwoFingerDown;
    private int _tpFrameExpectedContacts;
    private int _tpFrameSlotsSeen;
    private int _tpFrameActiveTips;

    private Pointer? _firstTouchPointer;
    private PointerEventHandler? _slideSwipePointerPressedHandler;
    private PointerEventHandler? _slideSwipePointerMovedHandler;
    private PointerEventHandler? _slideSwipePointerReleasedHandler;
    private PointerEventHandler? _slideSwipePointerCanceledHandler;
    private PointerEventHandler? _slideSwipePointerCaptureLostHandler;
    private Storyboard? _slideSwipeStoryboard;
    private PagedTouchGestureAxis _touchGestureAxis;
    private double _manipCumulativeDx;
    private double _manipCumulativeDy;
    private bool _isSlideSwipeActive;
    private bool _isSlideSwipeAnimating;
    private bool _isSlideSwipeAnimatingCommit;
    private bool _isCommittingSlideSwipe;
    private bool _isManipulatingSlideSwipe;
    private bool _isTouchSwipeTracking;
    private bool _isTouchSwipeEngaged;
    private bool _isTouchpadWheelSwipeActive;
    private bool _isTouchpadSmoothingSubscribed;
    private double _touchpadTargetDeltaX;
    private double _touchpadVisualVelocityX;
    private bool _isTouchpadGlideAnimating;
    private long _touchpadGlideStartTicks;
    private double _touchpadGlideDurationSec;
    private double _touchpadGlideStartX;
    private double _touchpadGlideTargetX;
    private double _touchpadGlideStartVx;
    private bool _touchpadGlideCommit;
    private int _touchpadGlideTargetPageIndex = -1;
    private long _lastTouchpadSmoothingRenderTicks;
    private bool _isDrainingTouchpadInertia;
    private int _committedTouchpadWheelSign;
    private long _lastTouchpadInertiaDrainTick;
    private long _touchpadInertiaDrainStartTick;
    private long _lastTouchpadHorizontalPacketTick;
    private double _touchpadDrainSmoothedAbsDelta;
    private int _touchpadFreshSurgeCount;

    private uint _touchSwipePointerId;
    private Point _touchSwipeStartPoint;
    private Point _touchSwipeLastPoint;
    private long _touchSwipeLastTimestamp;
    private long _touchpadLastWheelTimestamp;
    private int _lastTouchpadWheelAbsDelta;
    private int _touchpadDecayingFrameCount;
    private double _touchpadVerticalWheelAccum;
    private double _touchpadHorizontalWheelAccum;
    private long _lastTouchpadVerticalPacketTick;
    private long _ignoreTouchpadVerticalFlipUntilTick;
    private long _lastSlideSwipeCompletedTick;
    private double _swipeRawDeltaX;
    private double _swipeVisualDeltaX;
    private double _swipeVelocityX;
    private int _swipeTargetPageIndex = -1;
    private int _swipeBoundTargetPageIndex = -1;
    private double _swipeAdjTotalWidthDip;
    private double _swipeAdjTotalHeightDip;
    private double _swipeAdjScaleRatio = 1.0;

    // =========================================================================
    // Interactive Horizontal Slide Swipe (1-Finger Touch & 2-Finger Precision Touchpad)
    // =========================================================================

    private bool CanUseInteractiveSlideSwipe()
    {
        if (ViewModel?.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return false;
        }

        if (_isRestoringFromPresentation || _isAutoScrolling)
        {
            return false;
        }

        return !doc.IsContinuous || ViewModel.IsPresentationMode;
    }

    private bool CanPanHorizontallyInPagedMode()
    {
        if (ViewModel?.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return false;
        }

        if (doc.IsContinuous && !ViewModel.IsPresentationMode)
        {
            return true;
        }

        if (_isFittedVertically)
        {
            return false;
        }

        if (ContinuousScrollViewer.ScrollableWidth > 1.5)
        {
            return true;
        }

        double zoom = _hasInteractiveViewportState && _interactiveZoomFactor > 0.05
            ? _interactiveZoomFactor
            : (ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor);
        double rowWidthDip = GetDisplayedRowContentWidthDip(doc, doc.CurrentPageIndex);
        double rawViewerW = ContinuousScrollViewer.ActualWidth > 0 ? ContinuousScrollViewer.ActualWidth : ContinuousScrollViewer.ViewportWidth;
        double availW = GetAvailableDocumentWidthDip(rawViewerW);
        return (rowWidthDip * zoom) > (availW + 4.0);
    }

    private bool CanPanVerticallyInPagedMode()
    {
        if (ViewModel?.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return false;
        }

        if (doc.IsContinuous && !ViewModel.IsPresentationMode)
        {
            return true;
        }

        if (_isFittedVertically)
        {
            return false;
        }

        if (ContinuousScrollViewer.ScrollableHeight > 1.5)
        {
            return true;
        }

        double zoom = _hasInteractiveViewportState && _interactiveZoomFactor > 0.05
            ? _interactiveZoomFactor
            : (ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor);
        double rowHeightDip = GetDisplayedRowContentHeightDip(doc, doc.CurrentPageIndex);
        double rawViewerH = ContinuousScrollViewer.ActualHeight > 0 ? ContinuousScrollViewer.ActualHeight : ContinuousScrollViewer.ViewportHeight;
        return (rowHeightDip * zoom) > (rawViewerH + 4.0);
    }

    private bool CanStartSwipeInDirection(bool swipingRight)
    {
        if (!CanUseInteractiveSlideSwipe())
        {
            return false;
        }

        if (!CanPanHorizontallyInPagedMode())
        {
            return true;
        }

        double curH = _hasInteractiveViewportState ? _interactiveOffsetX : ContinuousScrollViewer.HorizontalOffset;

        // When zoomed horizontally in Paged / Presentation mode, allow starting a slide swipe only when already at the horizontal scroll edge
        if (swipingRight)
        {
            return curH <= 4.0;
        }

        double maxScrollX = ContinuousScrollViewer.ScrollableWidth;
        if (_hasInteractiveViewportState && ViewModel?.CurrentDocument is { Pages.Count: > 0 } doc)
        {
            double vw = ContinuousScrollViewer.ViewportWidth > 0 ? ContinuousScrollViewer.ViewportWidth : ContinuousScrollViewer.ActualWidth;
            double unzoomedW = GetUnzoomedScrollContentWidthDip(doc, _interactiveZoomFactor, vw);
            maxScrollX = Math.Max(maxScrollX, (unzoomedW * _interactiveZoomFactor) - vw);
        }

        return curH >= maxScrollX - 4.0;
    }

    private void ApplyManipulationModeToViewerContent(bool useCustomSwipeManip)
    {
        // IMPORTANT: In Continuous mode (useCustomSwipeManip == false), ScrollViewerInnerGrid, ContinuousPagesRepeater,
        // and all PdfPageCanvas instances strictly retain ManipulationModes.System so ContinuousScrollViewer
        // DirectManipulation never detaches or blanks pages during vertical scrolling.
        ManipulationModes targetMode = useCustomSwipeManip
            ? PagedCustomManipulationMode
            : ManipulationModes.System;

        SetElementManipulationMode(ScrollViewerInnerGrid, targetMode);
        SetElementManipulationMode(ContinuousPagesRepeater, targetMode);
        SetElementManipulationMode(PrevPageZone, targetMode);
        SetElementManipulationMode(NextPageZone, targetMode);

        foreach (PdfPageCanvas canvas in _activeCanvases)
        {
            canvas.SyncTouchManipulationMode(useCustomSwipeManip);
        }

        if (_activeCanvases.Count == 0)
        {
            SyncCanvasesManipulationInTree(ContinuousPagesRepeater, useCustomSwipeManip);
        }
    }

    private static void SyncCanvasesManipulationInTree(DependencyObject? parent, bool useCustomSwipeManip, bool includeInertia = false)
    {
        if (parent is null)
        {
            return;
        }

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is PdfPageCanvas canvas)
            {
                canvas.SyncTouchManipulationMode(useCustomSwipeManip, includeInertia);
            }
            else
            {
                SyncCanvasesManipulationInTree(child, useCustomSwipeManip, includeInertia);
            }
        }
    }

    private static void SetElementManipulationMode(UIElement? element, ManipulationModes mode)
    {
        if (element is not null && element.ManipulationMode != mode)
        {
            element.ManipulationMode = mode;
        }
    }

    private static bool IsDescendantOfPageCanvas(DependencyObject? source)
    {
        for (DependencyObject? cur = source; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is PdfPageCanvas)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsInkOrShapeOverlayActiveForEvent(object? originalSource)
        => ReferenceEquals(originalSource, TouchShapeDrawOverlay) ||
           ReferenceEquals(originalSource, TouchInkDrawOverlay) ||
           _isShapeOverlayTouchDrawing ||
           _isShapeOverlayTwoFingerNavigating ||
           _isInkOverlayDrawing ||
           _isInkOverlayErasing ||
           _isInkOverlayTouchPanning ||
           _isInkOverlayTwoFingerNavigating;

    private void OnSlideSwipePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsInkOrShapeOverlayActiveForEvent(e.OriginalSource) ||
            _isDraggingSelectionHandle ||
            _isDraggingTouchScrollScrubber ||
            IsDescendantOfTouchSelectionHandle(e.OriginalSource as DependencyObject) ||
            IsDescendantOfTouchResizeHandle(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var pt = e.GetCurrentPoint(ScrollViewerContentRoot);

        // Native Finger 2 multitouch viewport panning while Finger 1 drags or resizes a selected annotation overlay handle.
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch &&
            (_isDraggingAnnotationMoveHandle || _isDraggingAnnotationResizeHandle))
        {
            if (e.Pointer.PointerId != _touchDragAnnotPointerId)
            {
                _touchAnnotPanPointerId = e.Pointer.PointerId;
                _touchAnnotPanLastRootPos = pt.Position;
                _hasTouchAnnotPanLastRootPos = true;
                if (ContinuousScrollViewer is not null && ContinuousScrollViewer.ZoomFactor > 0.05)
                {
                    SetInteractiveViewportState(
                        ContinuousScrollViewer.ZoomFactor,
                        ContinuousScrollViewer.HorizontalOffset,
                        ContinuousScrollViewer.VerticalOffset);
                }
            }

            return;
        }

        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            _isLastPointerInputTouch = false;
            UpdateTouchAnnotationResizeHandlesLayout();
            _isMouseButtonDown = pt.Properties.IsLeftButtonPressed || pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed;
            _lastMousePointerPosition = pt.Position;
            _hasLastMousePointerPosition = true;
            if (pt.Properties.IsLeftButtonPressed && ViewModel.CurrentDocument is { } mouseDoc)
            {
                if (mouseDoc.ActiveSelection is { HasContent: true })
                {
                    mouseDoc.ClearSelection();
                }

                if (!IsDescendantOfPageCanvas(e.OriginalSource as DependencyObject) &&
                    mouseDoc.SelectedAnnotation is not null)
                {
                    mouseDoc.SelectedAnnotation = null;
                }

                HideTouchSelectionFlyout();
            }
            else if (_isTouchSelectionFlyoutOpen)
            {
                HideTouchSelectionFlyout();
            }

            if (_isTouchpadWheelSwipeActive && _isSlideSwipeActive && !_isSlideSwipeAnimating)
            {
                _activeTouchpadFingerCount = 0;
                CompleteInteractiveSlideSwipe(fromTouchpad: true);
            }

            return;
        }

        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            _isLastPointerInputTouch = false;
            UpdateTouchAnnotationResizeHandlesLayout();
            return;
        }

        long nowTick = Environment.TickCount64;
        _isLastPointerInputTouch = true;
        _activeTouchpadFingerCount = 0;
        _isMouseButtonDown = false;
        PruneStaleTouchPointers(nowTick);
        _activeTouchPointers[e.Pointer.PointerId] = (nowTick, pt.Position);

        // Record single-finger touch target on PDF page so Cut/Copy + Paste always lands at the touched spot.
        if (_activeTouchPointers.Count == 1 &&
            _activeTouchDraggingAnnotCanvas is not { IsTouchDraggingAnnotation: true } &&
            TryFindCanvasAtRootPoint(pt.Position, out PdfPageCanvas? touchedCanvas, out Point touchedCanvasDip) &&
            touchedCanvas?.PageInfo is { } touchedPageInfo)
        {
            _lastMouseClickPageIndex = touchedPageInfo.Index;
            _lastMouseClickPdfPt = touchedCanvas.ToPdfPoint(touchedCanvasDip);
            _hasLastMouseClickPdfPt = true;
        }

        // Multitouch 2nd-finger viewport panning while Finger 1 holds/drags an annotation
        if (_activeTouchDraggingAnnotCanvas is { IsTouchDraggingAnnotation: true })
        {
            if (e.Pointer.PointerId != _touchDragAnnotPointerId)
            {
                try
                {
                    _ = ContinuousScrollViewer?.CancelDirectManipulations();
                }
                catch
                {
                }

                _touchAnnotPanPointerId = e.Pointer.PointerId;
                _touchAnnotPanLastRootPos = pt.Position;
                _hasTouchAnnotPanLastRootPos = true;
                _hasTouchAnnotPanLastScreenPx = GetPointerInfo(_touchAnnotPanPointerId, out POINTER_INFO panStartPi);
                _touchAnnotPanLastScreenPx = _hasTouchAnnotPanLastScreenPx ? panStartPi.ptPixelLocation : default;
                if (ContinuousScrollViewer is not null && ContinuousScrollViewer.ZoomFactor > 0.05)
                {
                    SetInteractiveViewportState(
                        ContinuousScrollViewer.ZoomFactor,
                        ContinuousScrollViewer.HorizontalOffset,
                        ContinuousScrollViewer.VerticalOffset);
                }

                StartTouchAnnotPanPolling();
            }

            return;
        }

        // Immediately clear any existing text selection when touching anywhere outside the selection handles
        if (ViewModel.CurrentDocument is { } touchDoc)
        {
            if (touchDoc.ActiveSelection is { HasContent: true })
            {
                touchDoc.ClearSelection();
            }

            if (!IsDescendantOfPageCanvas(e.OriginalSource as DependencyObject) &&
                touchDoc.SelectedAnnotation is not null)
            {
                touchDoc.SelectedAnnotation = null;
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
            }
        }

        if (CanShowTouchScrollScrubber())
        {
            ShowAndSyncTouchScrollScrubber(resetAutoHideTimer: true);
        }

        if (_isTouchSelectionFlyoutOpen)
        {
            HideTouchSelectionFlyout();
        }

        // If a 2nd finger touches down (2-finger pinch-to-zoom or 2-finger pan), immediately cancel any
        // single-finger touch hold or text selection and track the 2-finger gesture.
        if (_activeTouchPointers.Count > 1)
        {
            CancelTouchHoldOnAllCanvases();
            if (_activeTouchSelectingCanvas is { } activeSelCanvas)
            {
                activeSelCanvas.CancelTouchSelectionAndHold();
                _activeTouchSelectingCanvas = null;
            }

            _isTouchSwipeTracking = false;
            _isTouchSwipeEngaged = false;
            _touchGestureAxis = PagedTouchGestureAxis.PanOrZoom;
            if (_isSlideSwipeActive || _isSlideSwipeAnimating)
            {
                CancelInteractiveSlideSwipeImmediate(stopSmoothZoom: true, resetManipulationSession: false);
            }

            EnsureScrollModesEnabledForViewChange();
            return;
        }

        _firstTouchPointer = e.Pointer;

        if (!CanUseInteractiveSlideSwipe())
        {
            EnsureScrollModesEnabledForViewChange();
            _touchSwipePointerId = e.Pointer.PointerId;
            _touchSwipeStartPoint = pt.Position;
            _touchSwipeLastPoint = pt.Position;
            _touchSwipeLastTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            _isTouchSwipeTracking = false;
            _isTouchSwipeEngaged = false;
            _touchGestureAxis = PagedTouchGestureAxis.Undecided;
            return;
        }

        if (_isSlideSwipeAnimating)
        {
            CompleteSlideSwipeAnimationImmediate();
        }

        SyncPagedScrollViewerModes();

        _touchSwipePointerId = e.Pointer.PointerId;
        _touchSwipeStartPoint = pt.Position;
        _touchSwipeLastPoint = pt.Position;
        _touchSwipeLastTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        _isTouchSwipeTracking = true;
        _isTouchSwipeEngaged = false;
        _touchGestureAxis = PagedTouchGestureAxis.Undecided;
    }

    private void OnSlideSwipePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (IsInkOrShapeOverlayActiveForEvent(e.OriginalSource) ||
            _isDraggingSelectionHandle ||
            _isDraggingTouchScrollScrubber ||
            IsDescendantOfTouchScrubber(e.OriginalSource as DependencyObject))
        {
            return;
        }

        // Native Finger 2 multitouch viewport panning while Finger 1 drags/resizes a selected annotation overlay handle.
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch &&
            (_isDraggingAnnotationMoveHandle || _isDraggingAnnotationResizeHandle))
        {
            if (e.Pointer.PointerId == _touchAnnotPanPointerId &&
                _hasTouchAnnotPanLastRootPos &&
                ScrollViewerContentRoot is not null)
            {
                Point curPanRoot = e.GetCurrentPoint(ScrollViewerContentRoot).Position;
                double panDx = curPanRoot.X - _touchAnnotPanLastRootPos.X;
                double panDy = curPanRoot.Y - _touchAnnotPanLastRootPos.Y;
                _touchAnnotPanLastRootPos = curPanRoot;
                if (Math.Abs(panDx) > 0.2 || Math.Abs(panDy) > 0.2)
                {
                    PanPagedScrollViewerBy(panDx, panDy);
                }
            }

            return;
        }

        if (IsDescendantOfTouchResizeHandle(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            var mousePt = e.GetCurrentPoint(ScrollViewerContentRoot);
            _isMouseButtonDown = mousePt.Properties.IsLeftButtonPressed || mousePt.Properties.IsRightButtonPressed || mousePt.Properties.IsMiddleButtonPressed;
            Point curMousePos = mousePt.Position;
            if (_hasLastMousePointerPosition)
            {
                double mdx = curMousePos.X - _lastMousePointerPosition.X;
                double mdy = curMousePos.Y - _lastMousePointerPosition.Y;
                double distSq = (mdx * mdx) + (mdy * mdy);

                // Only exit touch mode on genuine mouse motion (> 3 DIPs) or button press,
                // ignoring 0px synthetic cursor hover events fired by Windows touch / popup transitions.
                if (_isLastPointerInputTouch && (distSq > 9.0 || _isMouseButtonDown))
                {
                    _isLastPointerInputTouch = false;
                    UpdateTouchAnnotationResizeHandlesLayout();
                    if (_isTouchScrollScrubberVisible && !_isDraggingTouchScrollScrubber)
                    {
                        HideTouchScrollScrubberImmediate();
                    }
                }

                if (distSq > 3.0 &&
                    _isTouchpadWheelSwipeActive &&
                    _isSlideSwipeActive &&
                    !_isSlideSwipeAnimating &&
                    (Environment.TickCount64 - _lastTouchpadHorizontalPacketTick > 45))
                {
                    _lastMousePointerPosition = curMousePos;
                    _activeTouchpadFingerCount = 0;
                    if (Math.Abs(_touchpadTargetDeltaX) > Math.Abs(_swipeRawDeltaX))
                    {
                        UpdateInteractiveSlideSwipeDelta(_touchpadTargetDeltaX);
                    }

                    CompleteInteractiveSlideSwipe(fromTouchpad: true);
                    return;
                }
            }

            _lastMousePointerPosition = curMousePos;
            _hasLastMousePointerPosition = true;
            return;
        }

        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            return;
        }

        if (_activeTouchDraggingAnnotCanvas is { IsTouchDraggingAnnotation: true } dragAnnotCanvas)
        {
            if (e.Pointer.PointerId == _touchAnnotPanPointerId &&
                _hasTouchAnnotPanLastRootPos &&
                ScrollViewerContentRoot is not null)
            {
                Point curPanRoot = e.GetCurrentPoint(ScrollViewerContentRoot).Position;
                double panDx = curPanRoot.X - _touchAnnotPanLastRootPos.X;
                double panDy = curPanRoot.Y - _touchAnnotPanLastRootPos.Y;
                _touchAnnotPanLastRootPos = curPanRoot;
                if (Math.Abs(panDx) > 0.2 || Math.Abs(panDy) > 0.2)
                {
                    PanPagedScrollViewerBy(panDx, panDy);
                }

                return;
            }

            if (e.Pointer.PointerId == _touchDragAnnotPointerId)
            {
                try
                {
                    Point ptInCanvas = e.GetCurrentPoint(dragAnnotCanvas).Position;
                    dragAnnotCanvas.UpdateTouchAnnotationDragPoint(ptInCanvas);
                }
                catch
                {
                }
            }

            return;
        }

        if (_activeTouchShapeCanvas is { IsTouchDrawingShape: true } shapeCanvas)
        {
            if (e.Pointer.PointerId == _touchShapePointerId)
            {
                try
                {
                    Point ptInCanvas = e.GetCurrentPoint(shapeCanvas).Position;
                    shapeCanvas.UpdateTouchShapeDrawPoint(ptInCanvas);
                }
                catch
                {
                }
            }

            return;
        }

        if (_activeTouchSelectingCanvas is { IsTouchSelecting: true } selectingCanvas)
        {
            try
            {
                Point ptInCanvas = e.GetCurrentPoint(selectingCanvas).Position;
                selectingCanvas.UpdateTouchSelectionDragPoint(ptInCanvas);
            }
            catch
            {
            }

            return;
        }

        PruneStaleTouchPointers(Environment.TickCount64);

        if (e.Pointer.PointerId != _touchSwipePointerId ||
            !_isTouchSwipeTracking ||
            _isManipulatingSlideSwipe ||
            _activeTouchPointers.Count > 1)
        {
            return;
        }

        Point pos = e.GetCurrentPoint(ScrollViewerContentRoot).Position;
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = (double)(nowTicks - _touchSwipeLastTimestamp) / System.Diagnostics.Stopwatch.Frequency;
        double stepDx = pos.X - _touchSwipeLastPoint.X;
        double stepDy = pos.Y - _touchSwipeLastPoint.Y;
        _touchSwipeLastPoint = pos;
        _touchSwipeLastTimestamp = nowTicks;

        double totalDx = pos.X - _touchSwipeStartPoint.X;
        double totalDy = pos.Y - _touchSwipeStartPoint.Y;

        if (((totalDx * totalDx) + (totalDy * totalDy)) > 256.0)
        {
            CancelTouchHoldOnAllCanvases();
        }

        if (!CanUseInteractiveSlideSwipe())
        {
            return;
        }

        if (!_isTouchSwipeEngaged)
        {
            bool canPanH = CanPanHorizontallyInPagedMode();
            bool canPanV = CanPanVerticallyInPagedMode();
            if (canPanH || canPanV)
            {
                if (CanStartSwipeInDirection(swipingRight: totalDx > 0) &&
                    Math.Abs(totalDx) >= 16.0 &&
                    Math.Abs(totalDx) > Math.Abs(totalDy) * 2.2)
                {
                    _touchGestureAxis = PagedTouchGestureAxis.HorizontalSwipe;
                    _isTouchSwipeEngaged = true;
                    BeginInteractiveSlideSwipe();
                    stepDx = totalDx;
                }
                else if (((totalDx * totalDx) + (totalDy * totalDy)) >= 25.0)
                {
                    _touchGestureAxis = PagedTouchGestureAxis.PanOrZoom;
                    PanPagedScrollViewerBy(stepDx, stepDy);
                    _isTouchSwipeTracking = false;
                    return;
                }
                else
                {
                    return;
                }
            }
            else if (Math.Abs(totalDx) >= SlideSwipeDirectionLockDip && Math.Abs(totalDx) >= Math.Abs(totalDy) * 1.15)
            {
                _touchGestureAxis = PagedTouchGestureAxis.HorizontalSwipe;
                _isTouchSwipeEngaged = true;
                BeginInteractiveSlideSwipe();
                stepDx = totalDx;
            }
            else
            {
                return;
            }
        }

        if (dt > 0.0005 && dt < 0.25)
        {
            double instantVx = stepDx / dt;
            _swipeVelocityX = Math.Abs(_swipeVelocityX) < 1.0
                ? instantVx
                : (0.65 * instantVx) + (0.35 * _swipeVelocityX);
        }

        UpdateInteractiveSlideSwipeDelta(_swipeRawDeltaX + stepDx);
    }

    private void OnSlideSwipePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch &&
            (_isDraggingAnnotationMoveHandle || _isDraggingAnnotationResizeHandle) &&
            e.Pointer.PointerId == _touchAnnotPanPointerId)
        {
            _activeTouchPointers.Remove(e.Pointer.PointerId);
            _touchAnnotPanPointerId = 0;
            _hasTouchAnnotPanLastRootPos = false;
            StopTouchAnnotPanPolling();
            LockScrollViewerForTouchAnnotationDrag();
            return;
        }

        if (_isDraggingSelectionHandle ||
            _isDraggingAnnotationMoveHandle ||
            _isDraggingAnnotationResizeHandle ||
            _isDraggingTouchScrollScrubber ||
            IsDescendantOfTouchSelectionHandle(e.OriginalSource as DependencyObject) ||
            IsDescendantOfTouchResizeHandle(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            _isMouseButtonDown = false;
            return;
        }

        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            return;
        }

        if (_activeTouchDraggingAnnotCanvas is { IsTouchDraggingAnnotation: true } dragAnnotCanvas)
        {
            uint releasedId = e.Pointer.PointerId;
            _activeTouchPointers.Remove(releasedId);
            if (releasedId == _touchAnnotPanPointerId)
            {
                _touchAnnotPanPointerId = 0;
                StopTouchAnnotPanPolling();
                LockScrollViewerForTouchAnnotationDrag();
                return;
            }

            if (releasedId == _touchDragAnnotPointerId)
            {
                try
                {
                    Point ptInCanvas = e.GetCurrentPoint(dragAnnotCanvas).Position;
                    dragAnnotCanvas.CompleteTouchAnnotationDragSession(ptInCanvas);
                }
                catch
                {
                    dragAnnotCanvas.CompleteTouchAnnotationDragSession();
                }

                if (_activeTouchPointers.Count == 0)
                {
                    _firstTouchPointer = null;
                }
            }

            return;
        }

        if (_activeTouchShapeCanvas is { IsTouchDrawingShape: true } shapeCanvas)
        {
            uint releasedId = e.Pointer.PointerId;
            _activeTouchPointers.Remove(releasedId);
            if (releasedId == _touchShapePointerId)
            {
                try
                {
                    Point ptInCanvas = e.GetCurrentPoint(shapeCanvas).Position;
                    shapeCanvas.CompleteTouchShapeDrawSession(ptInCanvas);
                }
                catch
                {
                    shapeCanvas.CompleteTouchShapeDrawSession(null);
                }

                if (_activeTouchPointers.Count == 0)
                {
                    _firstTouchPointer = null;
                }
            }

            return;
        }

        if (_activeTouchSelectingCanvas is { IsTouchSelecting: true } selectingCanvas)
        {
            try
            {
                Point ptInCanvas = e.GetCurrentPoint(selectingCanvas).Position;
                selectingCanvas.CompleteTouchSelectionSession(ptInCanvas);
            }
            catch
            {
                selectingCanvas.CompleteTouchSelectionSession();
            }

            _activeTouchSelectingCanvas = null;
            _activeTouchPointers.Remove(e.Pointer.PointerId);
            if (_activeTouchPointers.Count == 0)
            {
                _firstTouchPointer = null;
            }

            SyncPagedScrollViewerModes();
            return;
        }

        _activeTouchPointers.Remove(e.Pointer.PointerId);
        if (_activeTouchPointers.Count == 0)
        {
            _firstTouchPointer = null;
        }

        if (e.Pointer.PointerId == _touchSwipePointerId)
        {
            _isTouchSwipeTracking = false;
            if (_isTouchSwipeEngaged && !_isManipulatingSlideSwipe)
            {
                _isTouchSwipeEngaged = false;
                CompleteInteractiveSlideSwipe(fromTouchpad: false);
            }
        }

        if (_activeTouchPointers.Count == 0 && !_isSlideSwipeActive && !_isSlideSwipeAnimating)
        {
            SyncPagedScrollViewerModes();
        }
    }

    private void OnSlideSwipePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            uint canceledId = e.Pointer.PointerId;
            if ((_isDraggingAnnotationMoveHandle || _isDraggingAnnotationResizeHandle) &&
                canceledId == _touchAnnotPanPointerId)
            {
                _activeTouchPointers.Remove(canceledId);
                _touchAnnotPanPointerId = 0;
                _hasTouchAnnotPanLastRootPos = false;
                StopTouchAnnotPanPolling();
                LockScrollViewerForTouchAnnotationDrag();
                return;
            }

            if (_activeTouchSelectingCanvas is { IsTouchSelecting: true } ||
                _activeTouchShapeCanvas is { IsTouchDrawingShape: true } ||
                _activeTouchDraggingAnnotCanvas is { IsTouchDraggingAnnotation: true } ||
                _isDraggingAnnotationMoveHandle ||
                _isDraggingAnnotationResizeHandle)
            {
                return;
            }

            if (canceledId == _touchSwipePointerId)
            {
                _isTouchSwipeTracking = false;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isManipulatingSlideSwipe)
                {
                    _activeTouchPointers.Remove(canceledId);
                    if (_activeTouchPointers.Count == 0)
                    {
                        _firstTouchPointer = null;
                    }

                    if (canceledId == _touchSwipePointerId && _isTouchSwipeEngaged)
                    {
                        _isTouchSwipeEngaged = false;
                        CompleteInteractiveSlideSwipe(fromTouchpad: false);
                    }
                }
            });
        }
        else if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            _isMouseButtonDown = false;
        }
    }

    private void OnSlideSwipePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // Do NOT remove active touch pointers on PointerCaptureLost: XAML Manipulation or ScrollViewer
        // claims pointer capture while the user's fingers are still physically on the screen.
    }

    private void OnSlideSwipeManipulationStarting(object sender, ManipulationStartingRoutedEventArgs e)
    {
        if (IsInkOrShapeOverlayActiveForEvent(e.OriginalSource) ||
            _isDraggingSelectionHandle ||
            _isDraggingAnnotationMoveHandle ||
            _isDraggingAnnotationResizeHandle ||
            _isDraggingTouchScrollScrubber)
        {
            e.Mode = ManipulationModes.None;
            return;
        }

        bool waitingOrDraggingMovableAnnot = _activeTouchDraggingAnnotCanvas is not null ||
                                             FindCanvasWaitingOnMovableAnnotation() is not null;
        bool isTouchDrawingShape = _activeTouchShapeCanvas is { IsTouchDrawingShape: true } ||
                                   ViewModel?.ActiveTool == AnnotationTool.Shape;

        // In Continuous Mode (when not touching/dragging a movable annotation, touch text selection, or shape drawing),
        // leave e.Mode untouched as ManipulationModes.System so ContinuousScrollViewer DirectManipulation handles
        // vertical scrolling, 2D zoomed panning, and pinch-to-zoom natively on the compositor thread.
        if (!CanUseInteractiveSlideSwipe() && _activeTouchSelectingCanvas is null && !waitingOrDraggingMovableAnnot && !isTouchDrawingShape)
        {
            return;
        }

        if (ScrollViewerContentRoot is not null)
        {
            e.Container = ScrollViewerContentRoot;
            bool allowPanInertia = CanPanHorizontallyInPagedMode() || CanPanVerticallyInPagedMode();
            e.Mode = allowPanInertia && _activeTouchSelectingCanvas is null && !waitingOrDraggingMovableAnnot && !isTouchDrawingShape
                ? (PagedCustomManipulationMode | ManipulationModes.TranslateInertia)
                : PagedCustomManipulationMode;
        }
    }

    private void OnSlideSwipeManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        if (IsInkOrShapeOverlayActiveForEvent(e.OriginalSource) ||
            _isMouseButtonDown ||
            _isDraggingSelectionHandle ||
            _isDraggingAnnotationMoveHandle ||
            _isDraggingAnnotationResizeHandle ||
            _isDraggingTouchScrollScrubber)
        {
            return;
        }

        bool waitingOrDraggingMovableAnnot = _activeTouchDraggingAnnotCanvas is not null ||
                                             FindCanvasWaitingOnMovableAnnotation() is not null;
        bool isTouchDrawingShape = _activeTouchShapeCanvas is { IsTouchDrawingShape: true } ||
                                   ViewModel?.ActiveTool == AnnotationTool.Shape;
        if (!CanUseInteractiveSlideSwipe() && _activeTouchSelectingCanvas is null && !waitingOrDraggingMovableAnnot && !isTouchDrawingShape)
        {
            return;
        }

        PruneStaleTouchPointers(Environment.TickCount64);

        if (_isSlideSwipeAnimating)
        {
            CompleteSlideSwipeAnimationImmediate();
        }

        if (ViewModel?.CurrentDocument is { Pages.Count: > 0 } doc &&
            !_isSmoothZoomRenderingSubscribed &&
            !_isInteractiveViewportFlushSubscribed &&
            ContinuousScrollViewer.ZoomFactor > 0.05)
        {
            EnsureInteractiveViewportStateInitialized(doc);
        }

        _isManipulatingSlideSwipe = true;
        _isTouchSwipeTracking = false;
        _isTouchSwipeEngaged = false;
        _manipCumulativeDx = _isSlideSwipeActive ? _swipeRawDeltaX : 0.0;
        _manipCumulativeDy = 0.0;

        if (_activeTouchPointers.Count > 1 || !CanUseInteractiveSlideSwipe())
        {
            _touchGestureAxis = PagedTouchGestureAxis.PanOrZoom;
            if (_isSlideSwipeActive)
            {
                CancelInteractiveSlideSwipeImmediate(stopSmoothZoom: true, resetManipulationSession: false);
            }
        }
        else if (_isSlideSwipeActive)
        {
            _touchGestureAxis = PagedTouchGestureAxis.HorizontalSwipe;
        }
        else
        {
            _touchGestureAxis = PagedTouchGestureAxis.Undecided;
        }
    }

    private void OnSlideSwipeManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (!_isManipulatingSlideSwipe ||
            IsInkOrShapeOverlayActiveForEvent(e.OriginalSource) ||
            _isDraggingSelectionHandle ||
            _isDraggingAnnotationMoveHandle ||
            _isDraggingAnnotationResizeHandle ||
            _isDraggingTouchScrollScrubber)
        {
            return;
        }

        if (_activeTouchDraggingAnnotCanvas is { IsTouchDraggingAnnotation: true } dragAnnotCanvas)
        {
            if (e.IsInertial || _touchAnnotPanPointerId != 0)
            {
                return;
            }

            try
            {
                Point ptInCanvas = ScrollViewerContentRoot.TransformToVisual(dragAnnotCanvas).TransformPoint(e.Position);
                dragAnnotCanvas.UpdateTouchAnnotationDragPoint(ptInCanvas);
            }
            catch
            {
            }

            return;
        }

        if (_activeTouchShapeCanvas is { IsTouchDrawingShape: true } shapeCanvas)
        {
            if (e.IsInertial)
            {
                return;
            }

            if (_activeTouchPointers.Count > 1 || Math.Abs(e.Delta.Scale - 1.0) > 0.0001)
            {
                shapeCanvas.CancelShapeDrawing();
                _touchGestureAxis = PagedTouchGestureAxis.PanOrZoom;
                ApplyInteractiveZoomAroundPoint(e.Position, e.Delta.Scale, e.Delta.Translation.X, e.Delta.Translation.Y, commitZoomProperty: false);
                return;
            }

            try
            {
                Point ptInCanvas = ScrollViewerContentRoot.TransformToVisual(shapeCanvas).TransformPoint(e.Position);
                shapeCanvas.UpdateTouchShapeDrawPoint(ptInCanvas);
            }
            catch
            {
            }

            return;
        }

        if (_activeTouchSelectingCanvas is { IsTouchSelecting: true } selectingCanvas)
        {
            if (e.IsInertial)
            {
                return;
            }

            try
            {
                Point ptInCanvas = ScrollViewerContentRoot.TransformToVisual(selectingCanvas).TransformPoint(e.Position);
                selectingCanvas.UpdateTouchSelectionDragPoint(ptInCanvas);
            }
            catch
            {
            }

            return;
        }

        PruneStaleTouchPointers(Environment.TickCount64);

        double deltaX = e.Delta.Translation.X;
        double deltaY = e.Delta.Translation.Y;
        double scaleDelta = e.Delta.Scale;

        // 2-finger pinch-to-zoom or 2-finger pan
        if (_activeTouchPointers.Count > 1 || Math.Abs(scaleDelta - 1.0) > 0.0001)
        {
            CancelTouchHoldOnAllCanvases();
            _touchGestureAxis = PagedTouchGestureAxis.PanOrZoom;
            if (_isSlideSwipeActive)
            {
                CancelInteractiveSlideSwipeImmediate(stopSmoothZoom: true, resetManipulationSession: false);
            }

            ApplyInteractiveZoomAroundPoint(e.Position, scaleDelta, deltaX, deltaY, commitZoomProperty: false);
            return;
        }

        _manipCumulativeDx += deltaX;
        _manipCumulativeDy += deltaY;
        double totalDistSq = (_manipCumulativeDx * _manipCumulativeDx) + (_manipCumulativeDy * _manipCumulativeDy);

        if (!CanUseInteractiveSlideSwipe())
        {
            if (totalDistSq > 256.0)
            {
                CancelTouchHoldOnAllCanvases();
                PanPagedScrollViewerBy(deltaX, deltaY);
            }

            return;
        }

        if (totalDistSq > 256.0)
        {
            CancelTouchHoldOnAllCanvases();
        }

        if (_touchGestureAxis == PagedTouchGestureAxis.Undecided)
        {
            bool canPanH = CanPanHorizontallyInPagedMode();
            bool canPanV = CanPanVerticallyInPagedMode();

            if (canPanH || canPanV)
            {
                // When zoomed in in Paged Mode, prioritize smooth 2D panning in all directions;
                // only start a horizontal slide-swipe if already at the horizontal scroll edge
                // and the user makes a distinctly horizontal swipe.
                if (CanStartSwipeInDirection(swipingRight: _manipCumulativeDx > 0) &&
                    Math.Abs(_manipCumulativeDx) >= 16.0 &&
                    Math.Abs(_manipCumulativeDx) > Math.Abs(_manipCumulativeDy) * 2.2)
                {
                    CancelTouchHoldOnAllCanvases();
                    _touchGestureAxis = PagedTouchGestureAxis.HorizontalSwipe;
                    if (!_isSlideSwipeActive)
                    {
                        BeginInteractiveSlideSwipe();
                    }

                    deltaX = _manipCumulativeDx;
                }
                else if (totalDistSq >= 256.0)
                {
                    CancelTouchHoldOnAllCanvases();
                    _touchGestureAxis = PagedTouchGestureAxis.PanOrZoom;
                    PanPagedScrollViewerBy(_manipCumulativeDx, _manipCumulativeDy);
                    return;
                }
                else
                {
                    return;
                }
            }
            else if (Math.Abs(_manipCumulativeDx) >= 15.0 &&
                     Math.Abs(_manipCumulativeDx) >= Math.Abs(_manipCumulativeDy) * 0.9)
            {
                CancelTouchHoldOnAllCanvases();
                _touchGestureAxis = PagedTouchGestureAxis.HorizontalSwipe;
                if (!_isSlideSwipeActive)
                {
                    BeginInteractiveSlideSwipe();
                }

                deltaX = _manipCumulativeDx;
            }
            else
            {
                return;
            }
        }

        if (_touchGestureAxis == PagedTouchGestureAxis.PanOrZoom)
        {
            PanPagedScrollViewerBy(deltaX, deltaY);
            return;
        }

        double velocityDipPerSec = e.Velocities.Linear.X * 1000.0;
        if (Math.Abs(velocityDipPerSec) > 1.0)
        {
            _swipeVelocityX = velocityDipPerSec;
        }

        UpdateInteractiveSlideSwipeDelta(_swipeRawDeltaX + deltaX);
    }

    private void OnSlideSwipeManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        if (!_isManipulatingSlideSwipe)
        {
            return;
        }

        _isManipulatingSlideSwipe = false;

        if (_activeTouchDraggingAnnotCanvas is { IsTouchDraggingAnnotation: true } dragAnnotCanvas)
        {
            if (IsAnyTouchPointerStillInContact())
            {
                return;
            }

            _activeTouchPointers.Clear();
            _firstTouchPointer = null;
            try
            {
                Point ptInCanvas = ScrollViewerContentRoot.TransformToVisual(dragAnnotCanvas).TransformPoint(e.Position);
                dragAnnotCanvas.CompleteTouchAnnotationDragSession(ptInCanvas);
            }
            catch
            {
                dragAnnotCanvas.CompleteTouchAnnotationDragSession();
            }

            return;
        }

        if (_activeTouchShapeCanvas is { IsTouchDrawingShape: true } shapeCanvas)
        {
            if (IsAnyTouchPointerStillInContact())
            {
                return;
            }

            _activeTouchPointers.Clear();
            _firstTouchPointer = null;
            try
            {
                Point ptInCanvas = ScrollViewerContentRoot.TransformToVisual(shapeCanvas).TransformPoint(e.Position);
                shapeCanvas.CompleteTouchShapeDrawSession(ptInCanvas);
            }
            catch
            {
                shapeCanvas.CompleteTouchShapeDrawSession(null);
            }

            return;
        }

        if (_activeTouchSelectingCanvas is { IsTouchSelecting: true } selectingCanvas)
        {
            // If the user's finger is still physically in contact on the screen (e.g., ManipulationCompleted fired
            // because CancelDirectManipulations or pointer capture was invoked at the 300ms hold threshold),
            // do NOT terminate the active touch selection drag!
            if (IsAnyTouchPointerStillInContact())
            {
                return;
            }

            _activeTouchPointers.Clear();
            _firstTouchPointer = null;
            try
            {
                Point ptInCanvas = ScrollViewerContentRoot.TransformToVisual(selectingCanvas).TransformPoint(e.Position);
                selectingCanvas.CompleteTouchSelectionSession(ptInCanvas);
            }
            catch
            {
                selectingCanvas.CompleteTouchSelectionSession();
            }

            _activeTouchSelectingCanvas = null;
            _touchGestureAxis = PagedTouchGestureAxis.Undecided;
            SyncPagedScrollViewerModes();
            return;
        }

        _activeTouchPointers.Clear();
        _firstTouchPointer = null;

        if (FindCanvasWaitingForTouchHold() is { } waitingCanvas)
        {
            try
            {
                Point ptInCanvas = ScrollViewerContentRoot.TransformToVisual(waitingCanvas).TransformPoint(e.Position);
                waitingCanvas.HandleTouchQuickRelease(ptInCanvas);
            }
            catch
            {
                waitingCanvas.CancelPendingTouchHold();
            }
        }

        if (_touchGestureAxis == PagedTouchGestureAxis.HorizontalSwipe && _isSlideSwipeActive)
        {
            double finalVelocityDipPerSec = e.Velocities.Linear.X * 1000.0;
            if (Math.Abs(finalVelocityDipPerSec) > Math.Abs(_swipeVelocityX))
            {
                _swipeVelocityX = finalVelocityDipPerSec;
            }

            CompleteInteractiveSlideSwipe(fromTouchpad: false);
        }
        else
        {
            if (_touchGestureAxis == PagedTouchGestureAxis.PanOrZoom && ViewModel?.CurrentDocument is { } doc)
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

            SyncPagedScrollViewerModes();
        }

        _touchGestureAxis = PagedTouchGestureAxis.Undecided;
    }

    private void EnsureTouchpadRawInputHook()
    {
        if (_touchpadRawInputHwnd != IntPtr.Zero)
        {
            return;
        }

        try
        {
            IntPtr hwnd = _windowContext.WindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            _touchpadRawInputHwnd = hwnd;
            _touchpadSubclassDelegate = TouchpadRawInputSubclassProc;
            _touchpadSubclassPtr = Marshal.GetFunctionPointerForDelegate(_touchpadSubclassDelegate);
            _ = SetWindowSubclass(hwnd, _touchpadSubclassPtr, TouchpadRawInputSubclassId, UIntPtr.Zero);

            RAWINPUTDEVICE rid = new()
            {
                usUsagePage = HID_USAGE_PAGE_DIGITIZER,
                usUsage = HID_USAGE_DIGITIZER_TOUCH_PAD,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = hwnd,
            };
            _ = RegisterRawInputDevices(in rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch
        {
        }
    }

    private IntPtr TouchpadRawInputSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == WM_INPUT && lParam != IntPtr.Zero && IsLoaded)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == hWnd || (fg != IntPtr.Zero && IsChild(hWnd, fg)))
            {
                try
                {
                    ProcessTouchpadRawInput(lParam);
                }
                catch
                {
                }
            }
        }
        else if (uMsg == WM_ACTIVATE)
        {
            uint activateState = (uint)(wParam.ToUInt64() & 0xFFFF);
            if (activateState == 0)
            {
                _activeTouchpadFingerCount = 0;
                _tpFrameExpectedContacts = 0;
                _activeHybridTouchpadContacts.Clear();
                if (_isTouchpadWheelSwipeActive && _isSlideSwipeActive && !_isSlideSwipeAnimating)
                {
                    CompleteInteractiveSlideSwipe(fromTouchpad: true);
                }
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private bool TryGetTouchpadPreparsedInfo(IntPtr hDevice, out TouchpadHidDeviceInfo? info)
    {
        if (_touchpadHidDevices.TryGetValue(hDevice, out info))
        {
            return info is not null;
        }

        info = null;
        uint preparsedSize = 0;
        if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref preparsedSize) != 0 ||
            preparsedSize == 0 ||
            preparsedSize > 65536)
        {
            _touchpadHidDevices[hDevice] = null;
            return false;
        }

        byte[] preparsedBytes = new byte[preparsedSize];
        unsafe
        {
            fixed (byte* pBytes = preparsedBytes)
            {
                uint copied = GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, (IntPtr)pBytes, ref preparsedSize);
                if (copied == unchecked((uint)-1) || copied == 0)
                {
                    _touchpadHidDevices[hDevice] = null;
                    return false;
                }

                if (HidP_GetCaps((IntPtr)pBytes, out HIDP_CAPS caps) != HIDP_STATUS_SUCCESS)
                {
                    _touchpadHidDevices[hDevice] = null;
                    return false;
                }

                ushort maxLc = (ushort)Math.Clamp((int)caps.NumberLinkCollectionNodes, 1, 16);
                info = new TouchpadHidDeviceInfo
                {
                    PreparsedData = preparsedBytes,
                    MaxLinkCollection = maxLc,
                };
                _touchpadHidDevices[hDevice] = info;
                return true;
            }
        }
    }

    private void ProcessTouchpadRawInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint dataSize = 0;
        if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref dataSize, headerSize) != 0 ||
            dataSize <= headerSize + 8 ||
            dataSize > 65536)
        {
            return;
        }

        if (_rawInputBuffer.Length < dataSize)
        {
            _rawInputBuffer = new byte[Math.Max((int)dataSize, 512)];
        }

        long nowTick = Environment.TickCount64;

        unsafe
        {
            fixed (byte* bufPtr = _rawInputBuffer)
            {
                uint copied = GetRawInputData(hRawInput, RID_INPUT, (IntPtr)bufPtr, ref dataSize, headerSize);
                if (copied == unchecked((uint)-1) || copied <= headerSize + 8)
                {
                    return;
                }

                RAWINPUTHEADER* hdr = (RAWINPUTHEADER*)bufPtr;
                if (hdr->dwType != RIM_TYPEHID || hdr->hDevice == IntPtr.Zero)
                {
                    return;
                }

                uint dwSizeHid = *(uint*)(bufPtr + headerSize);
                uint dwCount = *(uint*)(bufPtr + headerSize + 4);
                if (dwSizeHid == 0 || dwCount == 0 || headerSize + 8 + (dwSizeHid * dwCount) > copied)
                {
                    return;
                }

                if (!TryGetTouchpadPreparsedInfo(hdr->hDevice, out TouchpadHidDeviceInfo? devInfo) || devInfo is null)
                {
                    return;
                }

                fixed (byte* preparsedPtr = devInfo.PreparsedData)
                fixed (ushort* usageBufPtr = _hidUsageBuffer)
                {
                    IntPtr preparsedIntPtr = (IntPtr)preparsedPtr;
                    byte* reportsBase = bufPtr + headerSize + 8;
                    ushort maxLc = devInfo.MaxLinkCollection;

                    for (uint r = 0; r < dwCount; r++)
                    {
                        IntPtr reportPtr = (IntPtr)(reportsBase + (r * dwSizeHid));
                        int validContactCollections = 0;
                        int activeTipsInReport = 0;
                        uint lastContactId = 0;
                        bool hasContactId = false;

                        for (ushort lc = 1; lc <= maxLc; lc++)
                        {
                            uint usageLen = (uint)_hidUsageBuffer.Length;
                            int status = HidP_GetUsages(
                                0,
                                HID_USAGE_PAGE_DIGITIZER,
                                lc,
                                usageBufPtr,
                                ref usageLen,
                                preparsedIntPtr,
                                reportPtr,
                                dwSizeHid);

                            if (status == HIDP_STATUS_SUCCESS)
                            {
                                validContactCollections++;
                                bool tipSwitch = false;
                                for (uint u = 0; u < usageLen; u++)
                                {
                                    if (usageBufPtr[u] == HID_USAGE_DIGITIZER_TIP_SWITCH)
                                    {
                                        tipSwitch = true;
                                        break;
                                    }
                                }

                                if (tipSwitch)
                                {
                                    activeTipsInReport++;
                                }

                                if (HidP_GetUsageValue(
                                        0,
                                        HID_USAGE_PAGE_DIGITIZER,
                                        lc,
                                        HID_USAGE_DIGITIZER_CONTACT_ID,
                                        out uint contactId,
                                        preparsedIntPtr,
                                        reportPtr,
                                        dwSizeHid) == HIDP_STATUS_SUCCESS)
                                {
                                    lastContactId = contactId;
                                    hasContactId = true;
                                }
                            }
                        }

                        if (validContactCollections == 0)
                        {
                            continue;
                        }

                        int ccStatus = HidP_GetUsageValue(
                            0,
                            HID_USAGE_PAGE_DIGITIZER,
                            0,
                            HID_USAGE_DIGITIZER_CONTACT_COUNT,
                            out uint reportedContactCount,
                            preparsedIntPtr,
                            reportPtr,
                            dwSizeHid);

                        if (validContactCollections >= 2)
                        {
                            // Parallel report mode: all contact slots are present in this single report
                            _activeHybridTouchpadContacts.Clear();
                            if (ccStatus == HIDP_STATUS_SUCCESS && reportedContactCount > (uint)validContactCollections)
                            {
                                _tpFrameExpectedContacts = (int)reportedContactCount;
                                _tpFrameSlotsSeen = validContactCollections;
                                _tpFrameActiveTips = activeTipsInReport;
                            }
                            else if (ccStatus == HIDP_STATUS_SUCCESS && reportedContactCount == 0 &&
                                     _tpFrameExpectedContacts > 0 && _tpFrameSlotsSeen < _tpFrameExpectedContacts)
                            {
                                _tpFrameSlotsSeen += validContactCollections;
                                _tpFrameActiveTips += activeTipsInReport;
                                if (_tpFrameSlotsSeen >= _tpFrameExpectedContacts)
                                {
                                    int totalActive = _tpFrameActiveTips;
                                    _tpFrameExpectedContacts = 0;
                                    UpdateTouchpadActiveFingerCount(totalActive, nowTick);
                                }
                            }
                            else
                            {
                                _tpFrameExpectedContacts = 0;
                                UpdateTouchpadActiveFingerCount(activeTipsInReport, nowTick);
                            }
                        }
                        else
                        {
                            // Single-slot / Hybrid report mode: track per-contactId tip state across packets in the frame
                            if (hasContactId)
                            {
                                if (activeTipsInReport > 0)
                                {
                                    _activeHybridTouchpadContacts[lastContactId] = nowTick;
                                }
                                else
                                {
                                    _activeHybridTouchpadContacts.Remove(lastContactId);
                                }
                            }

                            if (ccStatus == HIDP_STATUS_SUCCESS && reportedContactCount > 1)
                            {
                                _tpFrameExpectedContacts = (int)reportedContactCount;
                                _tpFrameSlotsSeen = 1;
                                _tpFrameActiveTips = activeTipsInReport;
                            }
                            else if (_tpFrameExpectedContacts > 0 && _tpFrameSlotsSeen < _tpFrameExpectedContacts)
                            {
                                _tpFrameSlotsSeen++;
                                _tpFrameActiveTips += activeTipsInReport;
                                if (_tpFrameSlotsSeen >= _tpFrameExpectedContacts)
                                {
                                    int totalActive = hasContactId ? _activeHybridTouchpadContacts.Count : _tpFrameActiveTips;
                                    _tpFrameExpectedContacts = 0;
                                    UpdateTouchpadActiveFingerCount(totalActive, nowTick);
                                }
                            }
                            else
                            {
                                _tpFrameExpectedContacts = 0;
                                int totalActive = hasContactId ? _activeHybridTouchpadContacts.Count : activeTipsInReport;
                                UpdateTouchpadActiveFingerCount(totalActive, nowTick);
                            }
                        }
                    }
                }
            }
        }
    }

    private void UpdateTouchpadActiveFingerCount(int fingerCount, long nowTick)
    {
        _hasReceivedTouchpadRawInput = true;
        _lastTouchpadRawInputTick = nowTick;

        bool wasTwoOrMore = _activeTouchpadFingerCount >= 2;
        bool isTwoOrMore = fingerCount >= 2;
        _activeTouchpadFingerCount = fingerCount;

        if (isTwoOrMore)
        {
            if (!wasTwoOrMore)
            {
                // User just placed 2+ fingers on the touchpad: unlock for a fresh gesture
                _touchpadSwipeCompletedSinceLastTwoFingerDown = false;
                _isDrainingTouchpadInertia = false;
                _touchpadFreshSurgeCount = 0;
            }

            // While 2+ fingers are physically touching the touchpad, keep the slide held and never let the release timer fire
            if (_isTouchpadWheelSwipeActive && _isSlideSwipeActive && !_isSlideSwipeAnimating)
            {
                _touchpadSwipeEndTimer.Stop();
            }
        }
        else if (wasTwoOrMore)
        {
            // User just lifted their fingers off the touchpad (< 2 fingers remaining)!
            if (_isTouchpadWheelSwipeActive && _isSlideSwipeActive && !_isSlideSwipeAnimating)
            {
                if (Math.Abs(_touchpadTargetDeltaX) >= 20.0 || (nowTick - _lastTouchpadHorizontalPacketTick) > 55)
                {
                    // Finger drag distance is already established (or user held the slide still and then lifted):
                    // launch the momentum glide directly from the current rendered position and velocity without any snap/jump.
                    _touchpadSwipeEndTimer.Stop();
                    CompleteInteractiveSlideSwipe(fromTouchpad: true);
                }
                else
                {
                    // Ultra-fast micro-flick where < 20 DIPs arrived before finger lift: wait 16ms once for in-flight flick packets
                    _touchpadSwipeEndTimer.Stop();
                    _touchpadSwipeEndTimer.Interval = TimeSpan.FromMilliseconds(16);
                    _touchpadSwipeEndTimer.Start();
                }
            }
        }
    }

    private void HandleTouchpadHorizontalSlideSwipe(int wheelDelta)
    {
        if (_isManipulatingSlideSwipe || !CanUseInteractiveSlideSwipe() || ViewModel?.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        long nowTick = Environment.TickCount64;
        int absDelta = Math.Abs(wheelDelta);
        int wheelSign = Math.Sign(wheelDelta);

        if (nowTick - _lastTouchpadVerticalPacketTick > 140)
        {
            _touchpadVerticalWheelAccum = 0.0;
            _touchpadHorizontalWheelAccum = 0.0;
        }

        _lastTouchpadVerticalPacketTick = nowTick;
        _touchpadHorizontalWheelAccum += wheelDelta;

        bool isPrecisionTouchpadSession = _hasReceivedTouchpadRawInput &&
            (_activeTouchpadFingerCount >= 2 || (nowTick - _lastTouchpadRawInputTick < 600));

        // If the Precision Touchpad reports that < 2 fingers are on the pad and we already completed the swipe
        // for the previous 2-finger touch, any incoming WM_MOUSEHWHEEL packet is 100% OS inertia and must be ignored.
        if (isPrecisionTouchpadSession && _activeTouchpadFingerCount < 2 && _touchpadSwipeCompletedSinceLastTwoFingerDown)
        {
            _lastTouchpadInertiaDrainTick = nowTick;
            return;
        }

        // 1. If a slide transition animation is currently playing:
        if (_isSlideSwipeAnimating)
        {
            if (_isSlideSwipeAnimatingCommit)
            {
                // If user physically put 2 fingers back down on the touchpad for a brand-new swipe, finish current commit immediately
                if (isPrecisionTouchpadSession && _activeTouchpadFingerCount >= 2 && !_touchpadSwipeCompletedSinceLastTwoFingerDown)
                {
                    CompleteSlideSwipeAnimationImmediate();
                }
                else
                {
                    _lastTouchpadInertiaDrainTick = nowTick;
                    _touchpadDrainSmoothedAbsDelta = (0.75 * _touchpadDrainSmoothedAbsDelta) + (0.25 * absDelta);
                    return;
                }
            }
            else
            {
                // A bounce-back animation is playing: only catch it in-flight if 2 fingers are actually on the touchpad!
                if (isPrecisionTouchpadSession && _activeTouchpadFingerCount < 2)
                {
                    return;
                }

                double catchZoom = Math.Max(0.05, ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor);
                double catchBaseOffsetDip = GetTargetDocumentOffsetDip(catchZoom);
                double inFlightScreenDeltaX = PagesRepeaterPushTransform is not null
                    ? ((PagesRepeaterPushTransform.TranslateX * catchZoom) - catchBaseOffsetDip)
                    : _swipeVisualDeltaX;
                double caughtVelocityX = _isTouchpadGlideAnimating ? _touchpadVisualVelocityX : 0.0;

                _slideSwipeStoryboard?.Stop();
                _slideSwipeStoryboard = null;
                _isTouchpadGlideAnimating = false;
                _isSlideSwipeAnimating = false;
                _isSlideSwipeActive = true;
                _isTouchpadWheelSwipeActive = true;
                _swipeRawDeltaX = inFlightScreenDeltaX;
                _swipeVisualDeltaX = inFlightScreenDeltaX;
                _touchpadTargetDeltaX = inFlightScreenDeltaX;
                _touchpadVisualVelocityX = caughtVelocityX;
            }
        }

        // 2. Drain OS touchpad inertia tail after a completed swipe when fingers are not on the pad
        if (_isDrainingTouchpadInertia)
        {
            if (isPrecisionTouchpadSession && _activeTouchpadFingerCount >= 2)
            {
                _isDrainingTouchpadInertia = false;
                _touchpadFreshSurgeCount = 0;
            }
            else
            {
                bool sameDirection = wheelSign == _committedTouchpadWheelSign;
                long gapSinceLastPacket = nowTick - _lastTouchpadInertiaDrainTick;
                long elapsedSinceDrainStart = nowTick - _touchpadInertiaDrainStartTick;

                if (sameDirection && gapSinceLastPacket < 155)
                {
                    if (!isPrecisionTouchpadSession && elapsedSinceDrainStart > 360 && _touchpadDrainSmoothedAbsDelta <= 4.5 && absDelta >= 18)
                    {
                        _touchpadFreshSurgeCount++;
                        if (_touchpadFreshSurgeCount >= 2)
                        {
                            _isDrainingTouchpadInertia = false;
                            _touchpadFreshSurgeCount = 0;
                        }
                        else
                        {
                            _lastTouchpadInertiaDrainTick = nowTick;
                            return;
                        }
                    }
                    else
                    {
                        _touchpadFreshSurgeCount = 0;
                        _touchpadDrainSmoothedAbsDelta = (0.75 * _touchpadDrainSmoothedAbsDelta) + (0.25 * absDelta);
                        _lastTouchpadInertiaDrainTick = nowTick;
                        return;
                    }
                }
                else
                {
                    _isDrainingTouchpadInertia = false;
                    _touchpadFreshSurgeCount = 0;
                }
            }
        }

        _lastTouchpadHorizontalPacketTick = nowTick;

        long nowPerf = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = _touchpadLastWheelTimestamp > 0
            ? (double)(nowPerf - _touchpadLastWheelTimestamp) / System.Diagnostics.Stopwatch.Frequency
            : 0.016;
        _touchpadLastWheelTimestamp = nowPerf;
        dt = Math.Clamp(dt, 0.004, 0.080);

        if (!_isSlideSwipeActive)
        {
            bool swipingContentRight = wheelDelta < 0;
            if (!CanStartSwipeInDirection(swipingRight: swipingContentRight))
            {
                return;
            }

            _isTouchpadWheelSwipeActive = true;
            _touchpadDecayingFrameCount = 0;
            _lastTouchpadWheelAbsDelta = absDelta;
            _touchpadDrainSmoothedAbsDelta = absDelta;
            _touchpadFreshSurgeCount = 0;
            BeginInteractiveSlideSwipe();
            _touchpadTargetDeltaX = 0.0;

            // Eagerly bind the incoming adjacent preview on the very first touchpad packet to avoid first-frame pop
            int curPage = doc.CurrentPageIndex;
            int step = (!doc.IsContinuous && doc.IsDoublePage && !ViewModel.IsPresentationMode) ? 2 : 1;
            int eagerTarget = swipingContentRight ? (curPage - step) : (curPage + step);
            if (eagerTarget >= 0 && eagerTarget < doc.Pages.Count)
            {
                double eagerZoom = Math.Max(0.05, ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor);
                _swipeTargetPageIndex = eagerTarget;
                EnsureAdjacentSlidePreviewBound(doc, eagerTarget, eagerZoom);
            }
        }
        else
        {
            if (absDelta < _lastTouchpadWheelAbsDelta)
            {
                _touchpadDecayingFrameCount++;
            }
            else if (absDelta > _lastTouchpadWheelAbsDelta + 2)
            {
                _touchpadDecayingFrameCount = 0;
            }

            _lastTouchpadWheelAbsDelta = absDelta;
            _touchpadDrainSmoothedAbsDelta = (0.70 * _touchpadDrainSmoothedAbsDelta) + (0.30 * absDelta);
        }

        // In Windows WM_MOUSEHWHEEL (IsHorizontalMouseWheel == true), positive wheelDelta (> 0) means scroll right
        // (content moves left -> swiping 2 fingers left -> Next page), and negative wheelDelta (< 0) means scroll left
        // (content moves right -> swiping 2 fingers right -> Previous page).
        double stepDip = -wheelDelta * TouchpadWheelToDipScale;
        double instantVx = stepDip / dt;
        _swipeVelocityX = Math.Abs(_swipeVelocityX) < 1.0
            ? instantVx
            : (0.55 * instantVx) + (0.45 * _swipeVelocityX);

        double rawW = ContinuousScrollViewer.ActualWidth > 0 ? ContinuousScrollViewer.ActualWidth : ContinuousScrollViewer.ViewportWidth;
        double availW = Math.Max(200.0, GetAvailableDocumentWidthDip(rawW));
        double curZoom = Math.Max(0.05, ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor);
        double maxStrideDip = ComputeSlideSwipeStrideScreenDip(doc, curZoom, availW);

        _touchpadTargetDeltaX = Math.Clamp(_touchpadTargetDeltaX + stepDip, -maxStrideDip, maxStrideDip);
        StartTouchpadSwipeSmoothingLoop();

        if (isPrecisionTouchpadSession)
        {
            if (_activeTouchpadFingerCount >= 2)
            {
                // 2+ fingers are physically touching the touchpad: hold the page under the fingers and DO NOT start a release timer!
                _touchpadSwipeEndTimer.Stop();
            }
            else if (!_touchpadSwipeEndTimer.IsEnabled)
            {
                // Fingers already lifted during an ultra-fast flick: start a single 18ms completion timer without resetting it on inertia
                _touchpadSwipeEndTimer.Interval = TimeSpan.FromMilliseconds(18);
                _touchpadSwipeEndTimer.Start();
            }
        }
        else
        {
            // Fallback when raw input is unavailable: use 115ms only when OS inertia decay is detected, and 600ms when holding fingers still
            int fallbackMs = _touchpadDecayingFrameCount >= 3 ? 115 : 600;
            _touchpadSwipeEndTimer.Stop();
            _touchpadSwipeEndTimer.Interval = TimeSpan.FromMilliseconds(fallbackMs);
            _touchpadSwipeEndTimer.Start();
        }
    }

    private void StartTouchpadSwipeSmoothingLoop()
    {
        _lastTouchpadSmoothingRenderTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_isTouchpadSmoothingSubscribed)
        {
            CompositionTarget.Rendering += OnTouchpadSwipeSmoothingRendering;
            _isTouchpadSmoothingSubscribed = true;
        }
    }

    private void StopTouchpadSwipeSmoothingLoop()
    {
        if (_isTouchpadSmoothingSubscribed)
        {
            CompositionTarget.Rendering -= OnTouchpadSwipeSmoothingRendering;
            _isTouchpadSmoothingSubscribed = false;
        }
    }

    private void OnTouchpadSwipeSmoothingRendering(object? sender, object e)
    {
        if (_isTouchpadGlideAnimating)
        {
            if (!_isSlideSwipeActive || !_isSlideSwipeAnimating || ViewModel?.CurrentDocument is not { Pages.Count: > 0 })
            {
                _isTouchpadGlideAnimating = false;
                _touchpadVisualVelocityX = 0.0;
                StopTouchpadSwipeSmoothingLoop();
                return;
            }

            long glideNowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            double elapsedSec = (double)(glideNowTicks - _touchpadGlideStartTicks) / System.Diagnostics.Stopwatch.Frequency;
            double u = _touchpadGlideDurationSec > 0.001
                ? Math.Clamp(elapsedSec / _touchpadGlideDurationSec, 0.0, 1.0)
                : 1.0;

            if (u >= 1.0)
            {
                bool glideCommit = _touchpadGlideCommit;
                int glideTargetPage = _touchpadGlideTargetPageIndex;
                _isTouchpadGlideAnimating = false;
                _touchpadVisualVelocityX = 0.0;
                StopTouchpadSwipeSmoothingLoop();
                FinalizeSlideSwipeAnimation(glideCommit, glideTargetPage);
                return;
            }

            double u2 = u * u;
            double u3 = u2 * u;
            double h10 = u3 - (2.0 * u2) + u;
            double h01 = (-2.0 * u3) + (3.0 * u2);
            double dh10 = (3.0 * u2) - (4.0 * u) + 1.0;
            double dh01 = (6.0 * u) - (6.0 * u2);

            double dx = _touchpadGlideTargetX - _touchpadGlideStartX;
            double m0 = _touchpadGlideStartVx * _touchpadGlideDurationSec;
            double nextX = _touchpadGlideStartX + (h10 * m0) + (h01 * dx);
            _touchpadVisualVelocityX = ((dh10 * m0) + (dh01 * dx)) / _touchpadGlideDurationSec;
            ApplyTouchpadGlidePosition(nextX, _touchpadGlideTargetPageIndex);
            return;
        }

        if (!_isTouchpadWheelSwipeActive || !_isSlideSwipeActive || _isSlideSwipeAnimating)
        {
            _touchpadVisualVelocityX = 0.0;
            StopTouchpadSwipeSmoothingLoop();
            return;
        }

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = (double)(nowTicks - _lastTouchpadSmoothingRenderTicks) / System.Diagnostics.Stopwatch.Frequency;
        _lastTouchpadSmoothingRenderTicks = nowTicks;
        dt = Math.Clamp(dt, 0.002, 0.050);

        // If the user is holding their 2 fingers still on the touchpad (> 90ms without wheel packets), decay velocity to 0
        if (Environment.TickCount64 - _lastTouchpadHorizontalPacketTick > 90)
        {
            _swipeVelocityX *= Math.Exp(-25.0 * dt);
            if (Math.Abs(_swipeVelocityX) < 5.0)
            {
                _swipeVelocityX = 0.0;
            }
        }

        double diff = _touchpadTargetDeltaX - _swipeRawDeltaX;
        if (Math.Abs(diff) <= 0.15 && Math.Abs(_touchpadVisualVelocityX) < 4.0)
        {
            _touchpadVisualVelocityX = 0.0;
            UpdateInteractiveSlideSwipeDelta(_touchpadTargetDeltaX);
            if (Environment.TickCount64 - _lastTouchpadHorizontalPacketTick > 90 && Math.Abs(_swipeVelocityX) < 5.0)
            {
                // Unsubscribe while fingers are held completely motionless so idle CPU is 0.0%
                StopTouchpadSwipeSmoothingLoop();
            }

            return;
        }

        // Exact closed-form critically damped spring for C1-smooth tracking and velocity continuity across release
        const double omega = 36.0;
        double springError = _swipeRawDeltaX - _touchpadTargetDeltaX;
        double expTerm = Math.Exp(-omega * dt);
        double c2 = _touchpadVisualVelocityX + (omega * springError);
        double term = springError + (c2 * dt);
        double nextRawX = _touchpadTargetDeltaX + (term * expTerm);
        _touchpadVisualVelocityX = (c2 - (omega * term)) * expTerm;
        UpdateInteractiveSlideSwipeDelta(nextRawX);
    }

    private void OnTouchpadSwipeEndTimerTick(object? sender, object e)
    {
        if (_hasReceivedTouchpadRawInput && _activeTouchpadFingerCount >= 2)
        {
            // Never release while 2+ fingers are still physically touching the touchpad
            _touchpadSwipeEndTimer.Stop();
            return;
        }

        _touchpadSwipeEndTimer.Stop();
        if (_isTouchpadWheelSwipeActive && _isSlideSwipeActive && !_isSlideSwipeAnimating)
        {
            CompleteInteractiveSlideSwipe(fromTouchpad: true);
        }
    }

    private void BeginInteractiveSlideSwipe()
    {
        _slideSwipeStoryboard?.Stop();
        _slideSwipeStoryboard = null;
        _horizontalPushStoryboard?.Stop();
        _isHorizontalPushAnimating = false;

        _isTouchpadGlideAnimating = false;
        _touchpadGlideTargetPageIndex = -1;
        _touchpadVisualVelocityX = 0.0;
        _isSlideSwipeActive = true;
        _isSlideSwipeAnimating = false;
        _swipeRawDeltaX = 0.0;
        _swipeVisualDeltaX = 0.0;
        _swipeVelocityX = 0.0;
        _swipeTargetPageIndex = -1;
        _swipeBoundTargetPageIndex = -1;
        _swipeAdjScaleRatio = 1.0;
    }

    private void GetZoomedViewportCenterShiftScreenDip(out double shiftX, out double shiftY)
    {
        shiftX = ContinuousScrollViewer is not null && ContinuousScrollViewer.ScrollableWidth > 0.5
            ? (ContinuousScrollViewer.HorizontalOffset - (ContinuousScrollViewer.ScrollableWidth / 2.0))
            : 0.0;
        shiftY = ContinuousScrollViewer is not null && ContinuousScrollViewer.ScrollableHeight > 0.5
            ? (ContinuousScrollViewer.VerticalOffset - (ContinuousScrollViewer.ScrollableHeight / 2.0))
            : 0.0;
    }

    private void UpdateInteractiveSlideSwipeDelta(double newRawDeltaX)
    {
        if (!_isSlideSwipeActive || ViewModel?.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        _swipeRawDeltaX = newRawDeltaX;

        int curPage = doc.CurrentPageIndex;
        int step = (!doc.IsContinuous && doc.IsDoublePage && !ViewModel.IsPresentationMode) ? 2 : 1;

        int candidateTarget = -1;
        if (_swipeRawDeltaX < -0.5)
        {
            // Dragging left -> Next slide enters from the right
            if (curPage + step < doc.Pages.Count)
            {
                candidateTarget = curPage + step;
            }
        }
        else if (_swipeRawDeltaX > 0.5)
        {
            // Dragging right -> Previous slide enters from the left
            if (curPage - step >= 0)
            {
                candidateTarget = curPage - step;
            }
        }

        _swipeTargetPageIndex = candidateTarget;

        double rawViewerW = ContinuousScrollViewer.ActualWidth > 0 ? ContinuousScrollViewer.ActualWidth : ContinuousScrollViewer.ViewportWidth;
        double availW = Math.Max(200.0, GetAvailableDocumentWidthDip(rawViewerW));
        double curZoom = Math.Max(0.05, ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor);
        double baseDocOffsetDip = GetTargetDocumentOffsetDip(curZoom);

        if (candidateTarget < 0)
        {
            // At the first or last slide boundary: apply elastic rubber-band resistance and hide adjacent preview
            double maxRubberDip = availW * 0.18;
            double sign = Math.Sign(_swipeRawDeltaX);
            double absRaw = Math.Abs(_swipeRawDeltaX);
            _swipeVisualDeltaX = sign * Math.Min(maxRubberDip, absRaw * SlideSwipeRubberBandFactor);

            if (AdjacentSlideOverlayCanvas is not null && AdjacentSlideOverlayCanvas.Visibility != Visibility.Collapsed)
            {
                AdjacentSlideOverlayCanvas.Visibility = Visibility.Collapsed;
            }
            _swipeBoundTargetPageIndex = -1;

            if (PagesRepeaterPushTransform is not null)
            {
                PagesRepeaterPushTransform.TranslateX = (baseDocOffsetDip + _swipeVisualDeltaX) / curZoom;
            }

            return;
        }

        // Clamp visual displacement to 1 full slide stride
        EnsureAdjacentSlidePreviewBound(doc, candidateTarget, curZoom);
        double strideScreenDip = ComputeSlideSwipeStrideScreenDip(doc, curZoom, availW);
        _swipeVisualDeltaX = Math.Clamp(_swipeRawDeltaX, -strideScreenDip, strideScreenDip);

        if (PagesRepeaterPushTransform is not null)
        {
            PagesRepeaterPushTransform.TranslateX = (baseDocOffsetDip + _swipeVisualDeltaX) / curZoom;
        }

        if (AdjacentSlideTransform is not null)
        {
            GetZoomedViewportCenterShiftScreenDip(out double viewportShiftX, out double viewportShiftY);
            double adjScreenOffsetDip = _swipeRawDeltaX < 0
                ? (_swipeVisualDeltaX + strideScreenDip)
                : (_swipeVisualDeltaX - strideScreenDip);
            AdjacentSlideTransform.ScaleX = _swipeAdjScaleRatio;
            AdjacentSlideTransform.ScaleY = _swipeAdjScaleRatio;
            AdjacentSlideTransform.TranslateX = (baseDocOffsetDip + viewportShiftX + adjScreenOffsetDip) / curZoom;
            AdjacentSlideTransform.TranslateY = viewportShiftY / curZoom;
        }

        if (AdjacentSlideOverlayCanvas is not null && AdjacentSlideOverlayCanvas.Visibility != Visibility.Visible)
        {
            AdjacentSlideOverlayCanvas.Visibility = Visibility.Visible;
        }
    }

    private void EnsureAdjacentSlidePreviewBound(DocumentViewModel doc, int targetPageIndex, double curZoom)
    {
        if (AdjacentSlidePreviewContainer is null || AdjacentLeftPageCanvas is null || AdjacentRightPageCanvas is null ||
            AdjacentLeftPageBorder is null || AdjacentRightPageBorder is null)
        {
            return;
        }

        if (_swipeBoundTargetPageIndex == targetPageIndex &&
            AdjacentLeftPageCanvas.PageInfo?.Index == targetPageIndex)
        {
            return;
        }

        _swipeBoundTargetPageIndex = targetPageIndex;

        PdfPageInfo leftPage = doc.Pages[targetPageIndex];
        bool hasRightPage = !doc.IsContinuous && doc.IsDoublePage && !ViewModel.IsPresentationMode && (targetPageIndex + 1 < doc.Pages.Count);
        PdfPageInfo? rightPage = hasRightPage ? doc.Pages[targetPageIndex + 1] : null;

        _swipeAdjTotalWidthDip = GetDisplayedRowContentWidthDip(doc, targetPageIndex);
        _swipeAdjTotalHeightDip = GetDisplayedRowContentHeightDip(doc, targetPageIndex);

        AdjacentSlidePreviewContainer.Width = _swipeAdjTotalWidthDip;
        AdjacentSlidePreviewContainer.Height = _swipeAdjTotalHeightDip;
        Canvas.SetLeft(AdjacentSlidePreviewContainer, -_swipeAdjTotalWidthDip / 2.0);
        Canvas.SetTop(AdjacentSlidePreviewContainer, -_swipeAdjTotalHeightDip / 2.0);

        // Always compute the exact Fit-Vertical scale ratio when in Presentation Mode or Fitted mode,
        // even if the current slide was temporarily zoomed in by touch, so the incoming slide enters at
        // its exact target fit size and never jumps when FinalizeSlideSwipeAnimation runs ExecuteFitVertical.
        _swipeAdjScaleRatio = 1.0;
        if (_isFittedVertically || ViewModel.IsPresentationMode)
        {
            double adjW = Math.Max(1.0, _swipeAdjTotalWidthDip);
            double adjH = Math.Max(1.0, _swipeAdjTotalHeightDip);
            double rawW = ContinuousScrollViewer.ActualWidth > 0 ? ContinuousScrollViewer.ActualWidth : ContinuousScrollViewer.ViewportWidth;
            double rawH = ContinuousScrollViewer.ActualHeight > 0 ? ContinuousScrollViewer.ActualHeight : ContinuousScrollViewer.ViewportHeight;
            if (ViewModel.IsPresentationMode)
            {
                var (fsW, fsH) = GetFullScreenDimensionsDip();
                if (fsW > 0 && fsH > 0)
                {
                    rawW = Math.Max(rawW, fsW);
                    rawH = Math.Max(rawH, fsH);
                }
            }

            double availW = Math.Max(100.0, GetAvailableDocumentWidthDip(rawW) - (ViewModel.IsPresentationMode ? 0.0 : 32.0));
            double availH = Math.Max(100.0, rawH - (ViewModel.IsPresentationMode ? 0.0 : 32.0));
            double adjTargetZoom = Math.Clamp(Math.Min(availH / adjH, availW / adjW), DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);
            _swipeAdjScaleRatio = adjTargetZoom / Math.Max(0.05, curZoom);
        }

        AdjacentLeftPageCanvas.IsNightMode = doc.IsNightMode;
        AdjacentLeftPageCanvas.RotationAngle = doc.Rotation;
        AdjacentLeftPageCanvas.ZoomFactor = curZoom * _swipeAdjScaleRatio;
        AdjacentLeftPageCanvas.PageInfo = leftPage;

        if (hasRightPage && rightPage is not null)
        {
            AdjacentRightPageBorder.Visibility = Visibility.Visible;
            AdjacentRightPageCanvas.Visibility = Visibility.Visible;
            AdjacentRightPageCanvas.IsNightMode = doc.IsNightMode;
            AdjacentRightPageCanvas.RotationAngle = doc.Rotation;
            AdjacentRightPageCanvas.ZoomFactor = curZoom * _swipeAdjScaleRatio;
            AdjacentRightPageCanvas.PageInfo = rightPage;
        }
        else
        {
            AdjacentRightPageCanvas.PageInfo = null;
            AdjacentRightPageCanvas.Visibility = Visibility.Collapsed;
            AdjacentRightPageBorder.Visibility = Visibility.Collapsed;
        }
    }

    private double ComputeSlideSwipeStrideScreenDip(DocumentViewModel doc, double curZoom, double availViewportWidthDip)
    {
        // Cap effective slide screen widths to availViewportWidthDip so that even if a slide was zoomed in,
        // the incoming slide starts right outside the visible screen edge instead of multiple screens away.
        double curSlideScreenW = Math.Min(availViewportWidthDip, GetDisplayedRowContentWidthDip(doc, doc.CurrentPageIndex) * curZoom);
        double adjSlideScreenW = Math.Min(availViewportWidthDip, (_swipeAdjTotalWidthDip > 0 ? _swipeAdjTotalWidthDip : curSlideScreenW / curZoom) * curZoom * _swipeAdjScaleRatio);

        double minSlideSeparation = (curSlideScreenW / 2.0) + (adjSlideScreenW / 2.0) + 32.0;
        double viewportEdgeSeparation = (availViewportWidthDip / 2.0) + (Math.Max(curSlideScreenW, adjSlideScreenW) / 2.0) + 16.0;

        return Math.Max(minSlideSeparation, viewportEdgeSeparation);
    }

    private void CompleteInteractiveSlideSwipe(bool fromTouchpad)
    {
        _touchpadSwipeEndTimer.Stop();
        _isTouchpadWheelSwipeActive = false;

        if (!_isSlideSwipeActive || ViewModel?.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            CancelInteractiveSlideSwipeImmediate();
            return;
        }

        long nowTick = Environment.TickCount64;
        if (fromTouchpad && (nowTick - _lastTouchpadHorizontalPacketTick > 95))
        {
            // User held their fingers still on the touchpad before lifting: release velocity is zero
            _swipeVelocityX = 0.0;
            _touchpadVisualVelocityX = 0.0;
        }

        double rawViewerW = ContinuousScrollViewer.ActualWidth > 0 ? ContinuousScrollViewer.ActualWidth : ContinuousScrollViewer.ViewportWidth;
        double availW = Math.Max(200.0, GetAvailableDocumentWidthDip(rawViewerW));

        double evalDeltaX = fromTouchpad ? _touchpadTargetDeltaX : _swipeRawDeltaX;
        int evalTargetPageIndex = _swipeTargetPageIndex;
        if (fromTouchpad && Math.Abs(evalDeltaX) > 0.5)
        {
            int curPage = doc.CurrentPageIndex;
            int step = (!doc.IsContinuous && doc.IsDoublePage && !ViewModel.IsPresentationMode) ? 2 : 1;
            evalTargetPageIndex = evalDeltaX < -0.5
                ? (curPage + step < doc.Pages.Count ? curPage + step : -1)
                : (curPage - step >= 0 ? curPage - step : -1);
        }

        double absDx = Math.Abs(evalDeltaX);
        double absVx = Math.Abs(_swipeVelocityX);
        bool sameVelocityDirection = Math.Abs(_swipeVelocityX) < 40.0 || (Math.Sign(_swipeVelocityX) == Math.Sign(evalDeltaX));

        bool distanceCommitted = absDx >= (availW * SlideSwipeMinCommitRatio);
        bool flickCommitted = absVx >= SlideSwipeMinFlickVelocityDipPerSec &&
                              absDx >= SlideSwipeMinFlickDistanceDip &&
                              sameVelocityDirection;

        // If the user dragged in one direction and then reversed direction strongly at the end, bounce back
        bool reversedAtEnd = absVx >= 240.0 && (Math.Sign(_swipeVelocityX) != Math.Sign(evalDeltaX));

        bool shouldCommit = evalTargetPageIndex >= 0 &&
                            evalTargetPageIndex < doc.Pages.Count &&
                            !reversedAtEnd &&
                            (distanceCommitted || flickCommitted);

        if (fromTouchpad)
        {
            _touchpadSwipeCompletedSinceLastTwoFingerDown = true;
            _isDrainingTouchpadInertia = true;
            _committedTouchpadWheelSign = evalDeltaX < 0 ? 1 : -1;
            _lastTouchpadInertiaDrainTick = nowTick;
            _touchpadInertiaDrainStartTick = nowTick;
            _touchpadFreshSurgeCount = 0;

            StartTouchpadSlideGlideAnimation(shouldCommit, evalTargetPageIndex);
            return;
        }

        StopTouchpadSwipeSmoothingLoop();
        AnimateSlideSwipeToCompletion(shouldCommit, _swipeTargetPageIndex);
    }

    private void StartTouchpadSlideGlideAnimation(bool commit, int targetPageIndex)
    {
        if (ViewModel?.CurrentDocument is not { Pages.Count: > 0 } doc || PagesRepeaterPushTransform is null)
        {
            CancelInteractiveSlideSwipeImmediate();
            return;
        }

        double curZoom = Math.Max(0.05, ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor);
        double baseDocOffsetDip = GetTargetDocumentOffsetDip(curZoom);
        double startX = ((PagesRepeaterPushTransform.TranslateX * curZoom) - baseDocOffsetDip);

        double rawViewerW = ContinuousScrollViewer.ActualWidth > 0 ? ContinuousScrollViewer.ActualWidth : ContinuousScrollViewer.ViewportWidth;
        double availW = Math.Max(200.0, GetAvailableDocumentWidthDip(rawViewerW));
        double strideScreenDip = ComputeSlideSwipeStrideScreenDip(doc, curZoom, availW);

        double targetX = 0.0;
        if (commit && targetPageIndex >= 0 && targetPageIndex < doc.Pages.Count)
        {
            EnsureAdjacentSlidePreviewBound(doc, targetPageIndex, curZoom);
            _swipeTargetPageIndex = targetPageIndex;
            bool goingToNext = targetPageIndex > doc.CurrentPageIndex;
            targetX = goingToNext ? -strideScreenDip : strideScreenDip;
            startX = Math.Clamp(startX, -strideScreenDip, strideScreenDip);
        }
        else
        {
            commit = false;
            targetPageIndex = targetPageIndex >= 0 && targetPageIndex < doc.Pages.Count && Math.Abs(startX) > 0.5
                ? targetPageIndex
                : -1;
            if (targetPageIndex >= 0)
            {
                EnsureAdjacentSlidePreviewBound(doc, targetPageIndex, curZoom);
                startX = Math.Clamp(startX, -strideScreenDip, strideScreenDip);
            }
            _swipeTargetPageIndex = targetPageIndex;
        }

        double rem = Math.Abs(targetX - startX);
        if (rem < 1.0)
        {
            _isTouchpadGlideAnimating = false;
            _touchpadVisualVelocityX = 0.0;
            StopTouchpadSwipeSmoothingLoop();
            FinalizeSlideSwipeAnimation(commit, targetPageIndex);
            return;
        }

        double dx = targetX - startX;
        double inwardSpeed = Math.Abs(dx) > 0.5
            ? Math.Max(0.0, Math.Sign(dx) * _touchpadVisualVelocityX)
            : 0.0;
        double minCommitSec = rem < 100.0 ? 0.05 : 0.12;
        double durationSec = commit
            ? Math.Clamp((0.17 * (rem / Math.Max(200.0, strideScreenDip))) + 0.12 - (Math.Clamp(inwardSpeed / 3200.0, 0.0, 1.0) * 0.07), minCommitSec, 0.26)
            : Math.Clamp(0.16 + (Math.Min(1.0, rem / Math.Max(200.0, availW * 0.5)) * 0.08), 0.16, 0.25);

        double startVx = _touchpadVisualVelocityX;
        if (commit && startVx * dx < 0.0)
        {
            startVx = 0.0;
        }

        double maxStartVx = (2.8 * rem) / durationSec;
        if (commit || startVx * dx > 0.0)
        {
            if (Math.Abs(startVx) > maxStartVx)
            {
                startVx = Math.Sign(dx) * maxStartVx;
            }
        }
        else
        {
            double maxCoastVx = (availW * 0.45) / durationSec;
            if (Math.Abs(startVx) > maxCoastVx)
            {
                startVx = Math.Sign(startVx) * maxCoastVx;
            }
        }

        _slideSwipeStoryboard?.Stop();
        _slideSwipeStoryboard = null;
        _isSlideSwipeAnimating = true;
        _isSlideSwipeAnimatingCommit = commit;
        _isTouchpadGlideAnimating = true;
        _touchpadGlideStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _touchpadGlideDurationSec = durationSec;
        _touchpadGlideStartX = startX;
        _touchpadGlideTargetX = targetX;
        _touchpadGlideStartVx = startVx;
        _touchpadGlideCommit = commit;
        _touchpadGlideTargetPageIndex = targetPageIndex;

        ApplyTouchpadGlidePosition(startX, targetPageIndex);
        StartTouchpadSwipeSmoothingLoop();
    }

    private void ApplyTouchpadGlidePosition(double rawDeltaX, int targetPageIndex)
    {
        if (!_isSlideSwipeActive || ViewModel?.CurrentDocument is not { Pages.Count: > 0 } doc || PagesRepeaterPushTransform is null)
        {
            return;
        }

        _swipeRawDeltaX = rawDeltaX;
        _swipeTargetPageIndex = targetPageIndex;

        double rawViewerW = ContinuousScrollViewer.ActualWidth > 0 ? ContinuousScrollViewer.ActualWidth : ContinuousScrollViewer.ViewportWidth;
        double availW = Math.Max(200.0, GetAvailableDocumentWidthDip(rawViewerW));
        double curZoom = Math.Max(0.05, ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor);
        double baseDocOffsetDip = GetTargetDocumentOffsetDip(curZoom);

        if (targetPageIndex < 0 || targetPageIndex >= doc.Pages.Count)
        {
            double maxRubberDip = availW * 0.18;
            _swipeVisualDeltaX = Math.Clamp(rawDeltaX, -maxRubberDip, maxRubberDip);

            if (AdjacentSlideOverlayCanvas is not null && AdjacentSlideOverlayCanvas.Visibility != Visibility.Collapsed)
            {
                AdjacentSlideOverlayCanvas.Visibility = Visibility.Collapsed;
            }

            _swipeBoundTargetPageIndex = -1;
            PagesRepeaterPushTransform.TranslateX = (baseDocOffsetDip + _swipeVisualDeltaX) / curZoom;
            return;
        }

        EnsureAdjacentSlidePreviewBound(doc, targetPageIndex, curZoom);
        double strideScreenDip = ComputeSlideSwipeStrideScreenDip(doc, curZoom, availW);
        _swipeVisualDeltaX = Math.Clamp(rawDeltaX, -strideScreenDip, strideScreenDip);
        PagesRepeaterPushTransform.TranslateX = (baseDocOffsetDip + _swipeVisualDeltaX) / curZoom;

        if (AdjacentSlideTransform is not null)
        {
            GetZoomedViewportCenterShiftScreenDip(out double viewportShiftX, out double viewportShiftY);
            bool previewIsNext = targetPageIndex > doc.CurrentPageIndex;
            double adjScreenOffsetDip = previewIsNext
                ? (_swipeVisualDeltaX + strideScreenDip)
                : (_swipeVisualDeltaX - strideScreenDip);
            AdjacentSlideTransform.ScaleX = _swipeAdjScaleRatio;
            AdjacentSlideTransform.ScaleY = _swipeAdjScaleRatio;
            AdjacentSlideTransform.TranslateX = (baseDocOffsetDip + viewportShiftX + adjScreenOffsetDip) / curZoom;
            AdjacentSlideTransform.TranslateY = viewportShiftY / curZoom;
        }

        if (AdjacentSlideOverlayCanvas is not null && AdjacentSlideOverlayCanvas.Visibility != Visibility.Visible)
        {
            AdjacentSlideOverlayCanvas.Visibility = Visibility.Visible;
        }
    }

    private void AnimateSlideSwipeToCompletion(bool commit, int targetPageIndex)
    {
        if (ViewModel?.CurrentDocument is not { Pages.Count: > 0 } doc || PagesRepeaterPushTransform is null)
        {
            CancelInteractiveSlideSwipeImmediate();
            return;
        }

        double curZoom = Math.Max(0.05, ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor);
        double baseDocOffsetDip = GetTargetDocumentOffsetDip(curZoom);
        double baseRepeaterX = baseDocOffsetDip / curZoom;

        double rawViewerW = ContinuousScrollViewer.ActualWidth > 0 ? ContinuousScrollViewer.ActualWidth : ContinuousScrollViewer.ViewportWidth;
        double availW = Math.Max(200.0, GetAvailableDocumentWidthDip(rawViewerW));
        double strideScreenDip = ComputeSlideSwipeStrideScreenDip(doc, curZoom, availW);
        GetZoomedViewportCenterShiftScreenDip(out double viewportShiftX, out _);
        double baseAdjCenterX = (baseDocOffsetDip + viewportShiftX) / curZoom;

        double startRepeaterX = PagesRepeaterPushTransform.TranslateX;
        double targetRepeaterX = commit
            ? ((baseDocOffsetDip + (_swipeRawDeltaX < 0 ? -strideScreenDip : strideScreenDip)) / curZoom)
            : baseRepeaterX;

        double startAdjX = AdjacentSlideTransform?.TranslateX ?? startRepeaterX;
        double targetAdjX = commit
            ? baseAdjCenterX
            : ((baseDocOffsetDip + viewportShiftX + (_swipeRawDeltaX < 0 ? strideScreenDip : -strideScreenDip)) / curZoom);

        double remainingScreenDip = Math.Abs(targetRepeaterX - startRepeaterX) * curZoom;
        double speedFactor = Math.Clamp(Math.Abs(_swipeVelocityX) / 1800.0, 0.0, 1.0);
        double durationMs = commit
            ? Math.Clamp(195.0 - (speedFactor * 55.0), 130.0, 210.0)
            : Math.Clamp(200.0, 160.0, 220.0);

        if (remainingScreenDip < 2.0)
        {
            durationMs = 15.0;
        }

        _slideSwipeStoryboard?.Stop();
        _isSlideSwipeAnimating = true;
        _isSlideSwipeAnimatingCommit = commit;

        var sb = new Storyboard();
        var duration = new Duration(TimeSpan.FromMilliseconds(durationMs));
        EasingFunctionBase ease = commit
            ? new ExponentialEase { Exponent = 4.5, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut };

        var repeaterAnim = new DoubleAnimation
        {
            From = startRepeaterX,
            To = targetRepeaterX,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(repeaterAnim, PagesRepeaterPushTransform);
        Storyboard.SetTargetProperty(repeaterAnim, "TranslateX");
        sb.Children.Add(repeaterAnim);

        if (AdjacentSlideTransform is not null && AdjacentSlideOverlayCanvas?.Visibility == Visibility.Visible)
        {
            var adjAnim = new DoubleAnimation
            {
                From = startAdjX,
                To = targetAdjX,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(adjAnim, AdjacentSlideTransform);
            Storyboard.SetTargetProperty(adjAnim, "TranslateX");
            sb.Children.Add(adjAnim);
        }

        sb.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_slideSwipeStoryboard, sb))
            {
                return;
            }

            FinalizeSlideSwipeAnimation(commit, targetPageIndex);
        };

        _slideSwipeStoryboard = sb;
        sb.Begin();
    }

    private void CompleteSlideSwipeAnimationImmediate()
    {
        if (!_isSlideSwipeAnimating)
        {
            return;
        }

        bool commit = _swipeTargetPageIndex >= 0 &&
                      ViewModel?.CurrentDocument is { } doc &&
                      _swipeTargetPageIndex < doc.Pages.Count &&
                      (_isTouchpadGlideAnimating ? _touchpadGlideCommit : Math.Abs(_swipeRawDeltaX) >= 32.0);
        int targetPage = _swipeTargetPageIndex;

        _slideSwipeStoryboard?.Stop();
        _slideSwipeStoryboard = null;
        _isTouchpadGlideAnimating = false;
        _touchpadVisualVelocityX = 0.0;
        StopTouchpadSwipeSmoothingLoop();
        FinalizeSlideSwipeAnimation(commit, targetPage);
    }

    private void FinalizeSlideSwipeAnimation(bool committed, int targetPageIndex)
    {
        Storyboard? completedSb = _slideSwipeStoryboard;
        _slideSwipeStoryboard = null;
        completedSb?.Stop();
        _isTouchpadGlideAnimating = false;
        _touchpadGlideTargetPageIndex = -1;
        _touchpadVisualVelocityX = 0.0;
        StopTouchpadSwipeSmoothingLoop();
        _isSlideSwipeAnimating = false;
        _isSlideSwipeAnimatingCommit = false;
        _isSlideSwipeActive = false;
        _touchpadTargetDeltaX = 0.0;
        _swipeRawDeltaX = 0.0;
        _swipeVisualDeltaX = 0.0;
        _swipeVelocityX = 0.0;
        _swipeTargetPageIndex = -1;
        _lastSlideSwipeCompletedTick = Environment.TickCount64;

        if (committed && ViewModel?.CurrentDocument is { Pages.Count: > 0 } doc &&
            targetPageIndex >= 0 && targetPageIndex < doc.Pages.Count)
        {
            _isCommittingSlideSwipe = true;
            try
            {
                doc.GoToPage(targetPageIndex);

                if (!_isPageBoxEditing && StatusBarPageBox is not null)
                {
                    StatusBarPageBox.Text = doc.CurrentPageNumber.ToString();
                }

                if (_isFittedVertically || ViewModel.IsPresentationMode)
                {
                    _isFittedVertically = true;
                    ExecuteFitVertical(force: true);
                }
                else
                {
                    EnsureScrollModesEnabledForViewChange();
                    ContinuousScrollViewer.ChangeView(0, 0, null, disableAnimation: true);
                    SetInteractiveViewportState(
                        _hasInteractiveViewportState && _interactiveZoomFactor > 0.05 ? _interactiveZoomFactor : doc.ZoomFactor,
                        0.0,
                        0.0);
                }
            }
            finally
            {
                _isCommittingSlideSwipe = false;
            }

            double newZoom = Math.Max(0.05, doc.ZoomFactor > 0.05 ? doc.ZoomFactor : ContinuousScrollViewer.ZoomFactor);
            double newBaseRepeaterX = GetTargetDocumentOffsetDip(newZoom) / newZoom;

            if (PagesRepeaterPushTransform is not null)
            {
                PagesRepeaterPushTransform.TranslateX = newBaseRepeaterX;
            }

            // Keep AdjacentSlideOverlayCanvas centered for 1 Low-priority dispatcher turn while ContinuousPagesRepeater
            // draws the newly bound page underneath, eliminating any single-frame Win2D swap flicker.
            if (AdjacentSlideTransform is not null)
            {
                AdjacentSlideTransform.ScaleX = 1.0;
                AdjacentSlideTransform.ScaleY = 1.0;
                AdjacentSlideTransform.TranslateX = newBaseRepeaterX;
                AdjacentSlideTransform.TranslateY = 0.0;
            }

            UpdateViewportPages(newZoom);
            SyncPagedScrollViewerModes();

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_isSlideSwipeActive && !_isSlideSwipeAnimating)
                {
                    HideAndClearAdjacentSlidePreview();
                }
            });
        }
        else
        {
            double curZoom = Math.Max(0.05, ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : (ViewModel?.CurrentDocument?.ZoomFactor ?? 1.0));
            if (PagesRepeaterPushTransform is not null)
            {
                PagesRepeaterPushTransform.TranslateX = GetTargetDocumentOffsetDip(curZoom) / curZoom;
            }

            HideAndClearAdjacentSlidePreview();
            SyncPagedScrollViewerModes();
        }
    }

    private void CancelInteractiveSlideSwipeImmediate(bool stopSmoothZoom = true, bool resetManipulationSession = true)
    {
        if (stopSmoothZoom)
        {
            StopSmoothWheelZoom();
            StopInteractiveViewportFlush();
        }

        StopTouchpadSwipeSmoothingLoop();
        _touchpadSwipeEndTimer.Stop();
        _slideSwipeStoryboard?.Stop();
        _slideSwipeStoryboard = null;
        _isTouchpadGlideAnimating = false;
        _touchpadGlideTargetPageIndex = -1;
        _touchpadVisualVelocityX = 0.0;
        _isSlideSwipeAnimating = false;
        _isSlideSwipeAnimatingCommit = false;
        _isSlideSwipeActive = false;
        _touchpadTargetDeltaX = 0.0;
        if (resetManipulationSession)
        {
            _isManipulatingSlideSwipe = false;
            _touchGestureAxis = PagedTouchGestureAxis.Undecided;
        }

        _isTouchSwipeTracking = false;
        _isTouchSwipeEngaged = false;
        _isTouchpadWheelSwipeActive = false;
        _manipCumulativeDx = 0.0;
        _manipCumulativeDy = 0.0;
        _swipeRawDeltaX = 0.0;
        _swipeVisualDeltaX = 0.0;
        _swipeVelocityX = 0.0;
        _swipeTargetPageIndex = -1;

        double curZoom = _hasInteractiveViewportState && _interactiveZoomFactor > 0.05
            ? _interactiveZoomFactor
            : Math.Max(0.05, ContinuousScrollViewer?.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : (ViewModel?.CurrentDocument?.ZoomFactor ?? 1.0));
        if (!_isHorizontalPushAnimating && PagesRepeaterPushTransform is not null)
        {
            PagesRepeaterPushTransform.TranslateX = GetTargetDocumentOffsetDip(curZoom) / curZoom;
        }

        HideAndClearAdjacentSlidePreview();
    }

    private void HideAndClearAdjacentSlidePreview()
    {
        _swipeBoundTargetPageIndex = -1;
        if (AdjacentSlideOverlayCanvas is not null)
        {
            AdjacentSlideOverlayCanvas.Visibility = Visibility.Collapsed;
        }

        if (AdjacentLeftPageCanvas is not null)
        {
            AdjacentLeftPageCanvas.PageInfo = null;
        }

        if (AdjacentRightPageCanvas is not null)
        {
            AdjacentRightPageCanvas.PageInfo = null;
            AdjacentRightPageCanvas.Visibility = Visibility.Collapsed;
        }

        if (AdjacentRightPageBorder is not null)
        {
            AdjacentRightPageBorder.Visibility = Visibility.Collapsed;
        }
    }

}
