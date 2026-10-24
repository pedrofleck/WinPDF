using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Printing;
using Windows.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Printing;
using WinPDF.Controls;
using WinPDF.Helpers;
using WinPDF.Models;
using WinPDF.Rendering;
using WinPDF.Services;
using WinPDF.ViewModels;

namespace WinPDF.Views;

/// <summary>
/// Page hosting the responsive document canvas, unified scroll viewer, and floating overlays.
/// </summary>
public sealed partial class DocumentView : Page
{

    [ComImport]
    [Guid("905a0fef-bc53-11df-8c49-001e4fc686da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPrintBufferByteAccess
    {
        unsafe byte* Buffer { get; }
    }

    private readonly HashSet<PdfPageCanvas> _activeCanvases = new();
    private readonly DispatcherTimer _presentationHideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _topNotificationTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _viewportDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(85) };
    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly IPdfService _pdfService;
    private readonly IWindowContext _windowContext;
    private DocumentViewModel? _observedDocument;
    private bool _isProgrammaticPageScroll;
    private bool _isUpdatingPageFromScroll;
    private bool _isApplyingZoom;
    private bool _isUpdatingZoomFromScrollViewer;
    private bool _isUpdatingSliderFromZoom;
    private bool _isPageBoxEditing;
    private bool _isFittedVertically;
    private Size _lastHandledScrollViewerSize;
    private bool _isApplyingFitZoom;
    private bool _isSwitchingMode;
    private bool _isRestoringFromPresentation;
    private bool _wasInPresentationMode;
    private bool _prePresentationFittedVertically;
    private double _prePresentationZoom = 1.0;
    private double _currentSidebarWidthDip = 260.0;
    private bool _isHorizontalPushAnimating;
    private UIElement? _hookedScrollTemplateRoot;
    private PointerEventHandler? _wheelHandler;
    private Storyboard? _transitionStoryboard;
    private Storyboard? _searchBarStoryboard;
    private Storyboard? _horizontalPushStoryboard;
    private Storyboard? _statusBarCompactStoryboard;
    private double _statusBarMetadataExpandedWidth = 148.0;
    private double _statusBarPresentationExpandedWidth = 68.0;
    private bool _isAutoScrolling;
    private bool _autoScrollMiddleButtonDown;
    private long _autoScrollStartTimestamp;
    private long _lastAutoScrollRenderTicks;
    private long _lastAutoScrollPageFlipTicks;
    private Point _autoScrollAnchorPoint;
    private Point _autoScrollCurrentPoint;
    private double _autoScrollAccumulatedX;
    private double _autoScrollAccumulatedY;
    private bool _isAutoScrollRenderingSubscribed;
    private PointerEventHandler? _autoScrollPointerPressedHandler;
    private PointerEventHandler? _autoScrollPointerMovedHandler;
    private PointerEventHandler? _autoScrollPointerReleasedHandler;

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

    private readonly DispatcherTimer _touchpadSwipeEndTimer = new() { Interval = TimeSpan.FromMilliseconds(115) };
    private readonly Dictionary<uint, (long PressTick, Point PressPos)> _activeTouchPointers = new();
    private bool _isTwoFingerTapPollingSubscribed;
    private bool _isTouchDoubleTapZoomedIn;
    private double _zoomBeforeTouchDoubleTap = 1.0;
    private bool _wasFittedBeforeTouchDoubleTap;
    private bool _restoreFitAfterSmoothZoom;
    private bool _isTouchToggleZoomAnimation;
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
    private Point _lastMousePointerPosition;
    private bool _hasLastMousePointerPosition;
    private PointerRoutedEventArgs? _lastProcessedWheelEventArgs;
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
    private long _lastTouchpadSmoothingRenderTicks;
    private bool _isDrainingTouchpadInertia;
    private int _committedTouchpadWheelSign;
    private long _lastTouchpadInertiaDrainTick;
    private long _touchpadInertiaDrainStartTick;
    private long _lastTouchpadHorizontalPacketTick;
    private double _touchpadDrainSmoothedAbsDelta;
    private int _touchpadFreshSurgeCount;
    private bool _isSmoothZoomRenderingSubscribed;
    private double _smoothZoomStartFactor = 1.0;
    private double _smoothZoomCurrentFactor = 1.0;
    private double _smoothZoomTargetFactor;
    private double _smoothZoomStartOffsetX;
    private double _smoothZoomStartOffsetY;
    private double _smoothZoomTargetOffsetX;
    private double _smoothZoomTargetOffsetY;
    private Point _smoothZoomPivotPoint;
    private long _lastSmoothZoomRenderTicks;
    private bool _isMouseButtonDown;
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
    private PdfPageCanvas? _activeTouchSelectingCanvas;
    private bool _isTouchSelectionFlyoutOpen;
    private bool _isLastPointerInputTouch;
    private long _lastTouchSelectionCompletedTick;
    private bool _isTouchSelectionHandlesActive;
    private bool _isDraggingSelectionHandle;
    private uint _handleDragPointerId;
    private PdfPageCanvas? _handleDragTargetCanvas;
    private UIElement? _handleDragElement;
    private Point _handleGrabOffsetCanvasDip;
    private Point _lastHandleTargetCanvasDip;

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
    private PrintManager? _registeredPrintManager;
    private nint _registeredPrintHwnd;
    private PrintDocument? _printDocument;
    private IPrintDocumentSource? _printDocumentSource;
    private DocumentViewModel? _activePrintDocumentVm;
    private PrintTaskOptions? _currentPrintTaskOptions;

    /// <summary>Gets the localized page prefix text.</summary>
    public string PagePrefixText => ResourceStrings.Get(ResourceKeys.StatusBarPagePrefix);

    /// <summary>Gets the localized page box tooltip.</summary>
    public string PageBoxToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarPageBoxToolTip);

    /// <summary>Gets the localized format tooltip.</summary>
    public string FormatToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarFormatToolTip);

    /// <summary>Gets the localized document properties tooltip.</summary>
    public string PropertiesToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarPropertiesToolTip);

    /// <summary>Gets the localized two-page toggle tooltip.</summary>
    public string DoublePageToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarDoublePageToolTip);

    /// <summary>Gets the localized continuous scroll toggle tooltip.</summary>
    public string ContinuousToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarContinuousToolTip);

    /// <summary>Gets the localized fit vertical tooltip.</summary>
    public string FitVerticalToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarFitVerticalToolTip);

    /// <summary>Gets the localized fit horizontal tooltip.</summary>
    public string FitHorizontalToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarFitHorizontalToolTip);

    /// <summary>Gets the localized zoom preset tooltip.</summary>
    public string ZoomPresetToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarZoomPresetToolTip);

    /// <summary>Gets the localized zoom out tooltip.</summary>
    public string ZoomOutToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarZoomOutToolTip);

    /// <summary>Gets the localized zoom slider tooltip.</summary>
    public string ZoomSliderToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarZoomSliderToolTip);

    /// <summary>Gets the localized zoom in tooltip.</summary>
    public string ZoomInToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarZoomInToolTip);

    /// <summary>Gets the localized slideshow presentation tooltip.</summary>
    public string SlideshowToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarSlideshowToolTip);

    /// <summary>Gets the localized full screen toggle tooltip.</summary>
    public string FullScreenToolTipText => ResourceStrings.Get(ResourceKeys.StatusBarFullScreenToolTip);

    /// <summary>Gets the localized exit presentation button text.</summary>
    public string ExitPresentationText => ResourceStrings.Get(ResourceKeys.PresentationExitButton);

    /// <summary>
    /// Gets the shell view model.
    /// </summary>
    public ShellViewModel ViewModel { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentView"/> class.
    /// </summary>
    public DocumentView()
    {
        NavigationCacheMode = NavigationCacheMode.Required;
        ViewModel = App.CurrentApp.Services.GetRequiredService<ShellViewModel>();
        _pdfService = App.CurrentApp.Services.GetRequiredService<IPdfService>();
        _windowContext = App.CurrentApp.Services.GetRequiredService<IWindowContext>();
        _currentSidebarWidthDip = Math.Clamp(ViewModel.SettingsService.Settings.SidebarWidth, 230.0, 480.0);
        InitializeComponent();

        _wheelHandler = new PointerEventHandler(OnScrollViewerPointerWheelChanged);
        _autoScrollPointerPressedHandler = new PointerEventHandler(OnViewerAutoScrollPointerPressed);
        _autoScrollPointerMovedHandler = new PointerEventHandler(OnViewerAutoScrollPointerMoved);
        _autoScrollPointerReleasedHandler = new PointerEventHandler(OnViewerAutoScrollPointerReleased);
        _slideSwipePointerPressedHandler = new PointerEventHandler(OnSlideSwipePointerPressed);
        _slideSwipePointerMovedHandler = new PointerEventHandler(OnSlideSwipePointerMoved);
        _slideSwipePointerReleasedHandler = new PointerEventHandler(OnSlideSwipePointerReleased);
        _slideSwipePointerCanceledHandler = new PointerEventHandler(OnSlideSwipePointerCanceled);
        _slideSwipePointerCaptureLostHandler = new PointerEventHandler(OnSlideSwipePointerCaptureLost);
        ScrollViewerContentRoot.AddHandler(UIElement.PointerPressedEvent, _autoScrollPointerPressedHandler, true);
        ScrollViewerContentRoot.AddHandler(UIElement.PointerMovedEvent, _autoScrollPointerMovedHandler, true);
        ScrollViewerContentRoot.AddHandler(UIElement.PointerReleasedEvent, _autoScrollPointerReleasedHandler, true);
        ScrollViewerContentRoot.AddHandler(UIElement.PointerPressedEvent, _slideSwipePointerPressedHandler, true);
        ScrollViewerContentRoot.AddHandler(UIElement.PointerMovedEvent, _slideSwipePointerMovedHandler, true);
        ScrollViewerContentRoot.AddHandler(UIElement.PointerReleasedEvent, _slideSwipePointerReleasedHandler, true);
        ScrollViewerContentRoot.AddHandler(UIElement.PointerCanceledEvent, _slideSwipePointerCanceledHandler, true);
        ScrollViewerContentRoot.AddHandler(UIElement.PointerCaptureLostEvent, _slideSwipePointerCaptureLostHandler, true);
        ScrollViewerContentRoot.AddHandler(UIElement.ManipulationStartingEvent, new ManipulationStartingEventHandler(OnSlideSwipeManipulationStarting), true);
        ScrollViewerContentRoot.AddHandler(UIElement.ManipulationStartedEvent, new ManipulationStartedEventHandler(OnSlideSwipeManipulationStarted), true);
        ScrollViewerContentRoot.AddHandler(UIElement.ManipulationDeltaEvent, new ManipulationDeltaEventHandler(OnSlideSwipeManipulationDelta), true);
        ScrollViewerContentRoot.AddHandler(UIElement.ManipulationCompletedEvent, new ManipulationCompletedEventHandler(OnSlideSwipeManipulationCompleted), true);
        ScrollViewerContentRoot.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        ScrollViewerInnerGrid.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        ContinuousPagesRepeater.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        PrevPageZone.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        NextPageZone.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        ExitPresentationTopZone.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        ContinuousScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        ContinuousScrollViewer.SizeChanged += OnScrollViewerSizeChanged;

        _presentationHideTimer.Tick += OnPresentationHideTimerTick;
        _topNotificationTimer.Tick += OnTopNotificationTimerTick;
        _viewportDebounceTimer.Tick += OnViewportDebounceTimerTick;
        _searchDebounceTimer.Tick += OnSearchDebounceTimerTick;
        _touchpadSwipeEndTimer.Tick += OnTouchpadSwipeEndTimerTick;
        _touchScrollHideTimer.Tick += OnTouchScrollHideTimerTick;
        _touchScrollCollapseTimer.Tick += OnTouchScrollCollapseTimerTick;

        PdfPageCanvas.CanvasLoaded += OnCanvasLoaded;
        PdfPageCanvas.CanvasUnloaded += OnCanvasUnloaded;
        PdfPageCanvas.TouchSelectionStarted += OnCanvasTouchSelectionStarted;
        PdfPageCanvas.TouchSelectionEnded += OnCanvasTouchSelectionEnded;
        PdfPageCanvas.TouchSelectionCompleted += OnCanvasTouchSelectionCompleted;
        PdfPageCanvas.TouchDoubleTapZoomRequested += OnCanvasTouchDoubleTapZoomRequested;
        PdfPageCanvas.TouchSelectionDismissRequested += OnCanvasTouchSelectionDismissRequested;
        PdfPageCanvas.IsSingleTouchViewerManipulating = () => _isManipulatingSlideSwipe && _activeTouchPointers.Count <= 1;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ShellViewModel vm)
        {
            ViewModel = vm;
        }

        if (ViewModel.CurrentDocument is not null && (ViewModel.CurrentDocument.LastVerticalOffset > 0 || ViewModel.CurrentDocument.LastHorizontalOffset > 0))
        {
            ContinuousScrollViewer.ChangeView(
                ViewModel.CurrentDocument.LastHorizontalOffset,
                ViewModel.CurrentDocument.LastVerticalOffset,
                (float)ViewModel.CurrentDocument.ZoomFactor,
                disableAnimation: true);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureScrollViewerWheelHook();
        EnsureTouchpadRawInputHook();
        PdfPageCanvas.TouchSelectionStarted -= OnCanvasTouchSelectionStarted;
        PdfPageCanvas.TouchSelectionStarted += OnCanvasTouchSelectionStarted;
        PdfPageCanvas.TouchSelectionEnded -= OnCanvasTouchSelectionEnded;
        PdfPageCanvas.TouchSelectionEnded += OnCanvasTouchSelectionEnded;
        PdfPageCanvas.TouchSelectionCompleted -= OnCanvasTouchSelectionCompleted;
        PdfPageCanvas.TouchSelectionCompleted += OnCanvasTouchSelectionCompleted;
        PdfPageCanvas.TouchDoubleTapZoomRequested -= OnCanvasTouchDoubleTapZoomRequested;
        PdfPageCanvas.TouchDoubleTapZoomRequested += OnCanvasTouchDoubleTapZoomRequested;
        PdfPageCanvas.TouchSelectionDismissRequested -= OnCanvasTouchSelectionDismissRequested;
        PdfPageCanvas.TouchSelectionDismissRequested += OnCanvasTouchSelectionDismissRequested;
        PdfPageCanvas.IsSingleTouchViewerManipulating = () => _isManipulatingSlideSwipe && _activeTouchPointers.Count <= 1;
        _presentationHideTimer.Tick -= OnPresentationHideTimerTick;
        _presentationHideTimer.Tick += OnPresentationHideTimerTick;
        _topNotificationTimer.Tick -= OnTopNotificationTimerTick;
        _topNotificationTimer.Tick += OnTopNotificationTimerTick;
        _viewportDebounceTimer.Tick -= OnViewportDebounceTimerTick;
        _viewportDebounceTimer.Tick += OnViewportDebounceTimerTick;
        _searchDebounceTimer.Tick -= OnSearchDebounceTimerTick;
        _searchDebounceTimer.Tick += OnSearchDebounceTimerTick;
        _touchpadSwipeEndTimer.Tick -= OnTouchpadSwipeEndTimerTick;
        _touchpadSwipeEndTimer.Tick += OnTouchpadSwipeEndTimerTick;
        _touchScrollHideTimer.Tick -= OnTouchScrollHideTimerTick;
        _touchScrollHideTimer.Tick += OnTouchScrollHideTimerTick;
        _touchScrollCollapseTimer.Tick -= OnTouchScrollCollapseTimerTick;
        _touchScrollCollapseTimer.Tick += OnTouchScrollCollapseTimerTick;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.SearchFocusRequested -= OnSearchFocusRequested;
        ViewModel.SearchFocusRequested += OnSearchFocusRequested;
        ViewModel.NotificationRequested -= OnNotificationRequested;
        ViewModel.NotificationRequested += OnNotificationRequested;
        ViewModel.DefaultZoomPreferenceChanged -= OnDefaultZoomPreferenceChanged;
        ViewModel.DefaultZoomPreferenceChanged += OnDefaultZoomPreferenceChanged;
        ViewModel.PrintRequested -= OnPrintRequestedAsync;
        ViewModel.PrintRequested += OnPrintRequestedAsync;
        ViewModel.BackgroundTrimRequested -= OnBackgroundTrimRequested;
        ViewModel.BackgroundTrimRequested += OnBackgroundTrimRequested;
        SubscribeToDocument(ViewModel.CurrentDocument);

        if (ViewModel.TryConsumePendingNotification(out string title, out string message))
        {
            ShowTopNotification(title, message);
        }

        SyncPaneOffsetsImmediate();
        ApplyStatusBarCompactStateImmediate(ViewModel.IsCompactChrome);

        if (ViewModel.CurrentDocument is not null)
        {
            SyncPagedScrollViewerModes();
            UpdateViewportPages(ViewModel.CurrentDocument.ZoomFactor);
        }
    }

    private void EnsureScrollViewerWheelHook()
    {
        if (_wheelHandler is null)
        {
            return;
        }

        ContinuousScrollViewer.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(ContinuousScrollViewer) > 0 &&
            VisualTreeHelper.GetChild(ContinuousScrollViewer, 0) is UIElement templateRoot &&
            !ReferenceEquals(_hookedScrollTemplateRoot, templateRoot))
        {
            if (_hookedScrollTemplateRoot is not null)
            {
                _hookedScrollTemplateRoot.RemoveHandler(UIElement.PointerWheelChangedEvent, _wheelHandler);
            }

            _hookedScrollTemplateRoot = templateRoot;
            _hookedScrollTemplateRoot.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _presentationHideTimer.Stop();
        _presentationHideTimer.Tick -= OnPresentationHideTimerTick;
        _topNotificationTimer.Stop();
        _topNotificationTimer.Tick -= OnTopNotificationTimerTick;
        _viewportDebounceTimer.Stop();
        _viewportDebounceTimer.Tick -= OnViewportDebounceTimerTick;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Tick -= OnSearchDebounceTimerTick;
        _touchpadSwipeEndTimer.Stop();
        _touchpadSwipeEndTimer.Tick -= OnTouchpadSwipeEndTimerTick;
        _touchScrollHideTimer.Stop();
        _touchScrollHideTimer.Tick -= OnTouchScrollHideTimerTick;
        _touchScrollCollapseTimer.Stop();
        _touchScrollCollapseTimer.Tick -= OnTouchScrollCollapseTimerTick;
        HideTouchScrollScrubberImmediate();
        HideTouchSelectionFlyout();
        CancelInteractiveSlideSwipeImmediate();
        PdfPageCanvas.TouchSelectionStarted -= OnCanvasTouchSelectionStarted;
        PdfPageCanvas.TouchSelectionEnded -= OnCanvasTouchSelectionEnded;
        PdfPageCanvas.TouchSelectionCompleted -= OnCanvasTouchSelectionCompleted;
        PdfPageCanvas.TouchDoubleTapZoomRequested -= OnCanvasTouchDoubleTapZoomRequested;
        PdfPageCanvas.TouchSelectionDismissRequested -= OnCanvasTouchSelectionDismissRequested;

        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.SearchFocusRequested -= OnSearchFocusRequested;
        ViewModel.NotificationRequested -= OnNotificationRequested;
        ViewModel.DefaultZoomPreferenceChanged -= OnDefaultZoomPreferenceChanged;
        ViewModel.PrintRequested -= OnPrintRequestedAsync;
        ViewModel.BackgroundTrimRequested -= OnBackgroundTrimRequested;
        StopAutoScroll();
        _transitionStoryboard?.Stop();
        _searchBarStoryboard?.Stop();
        _horizontalPushStoryboard?.Stop();
        _statusBarCompactStoryboard?.Stop();
        _isHorizontalPushAnimating = false;
        UnsubscribeFromDocument();
    }

    /// <summary>
    /// Pauses active timers, auto-scroll loops, and in-flight background prefetches when the window is minimized.
    /// Does not stop HoldEnd transition storyboards or wipe realized viewport pages.
    /// </summary>
    public void OnWindowMinimized()
    {
        _activeTouchpadFingerCount = 0;
        _tpFrameExpectedContacts = 0;
        _activeHybridTouchpadContacts.Clear();
        HideTouchScrollScrubberImmediate();
        CancelInteractiveSlideSwipeImmediate();
        StopAutoScroll();
        _viewportDebounceTimer.Stop();
        _searchDebounceTimer.Stop();
        _presentationHideTimer.Stop();
        _topNotificationTimer.Stop();

        foreach (PdfPageCanvas canvas in _activeCanvases.ToArray())
        {
            canvas.SuspendForBackground();
        }
    }

    /// <summary>
    /// Resumes viewport rendering when the window is restored from the taskbar.
    /// </summary>
    public void OnWindowRestored()
    {
        if (DocumentAreaGrid is not null && ViewModel.HasDocument)
        {
            DocumentAreaGrid.Opacity = 1.0;
            if (DocumentAreaTransform is not null)
            {
                DocumentAreaTransform.TranslateY = 0.0;
            }
        }

        if (ViewModel.CurrentDocument is { } doc)
        {
            double activeZoom = ContinuousScrollViewer.ZoomFactor > 0.05
                ? ContinuousScrollViewer.ZoomFactor
                : doc.ZoomFactor;
            UpdateViewportPages(activeZoom);
        }
    }

    private void OnBackgroundTrimRequested()
    {
        int curPage = ViewModel.CurrentDocument?.CurrentPageIndex ?? -1;

        // Trim only non-current prefetched slides in the presentation slide buffer so the active slide is never disposed
        PdfPageCanvas.TrimSlideBuffer(curPage);
        PdfPageCanvas.ReleaseReusableBuffer();

        if (curPage >= 0 && _activeCanvases.Count > 0)
        {
            foreach (PdfPageCanvas canvas in _activeCanvases.ToArray())
            {
                if (canvas.PageInfo is { } info)
                {
                    int distance = Math.Abs(info.Index - curPage);
                    bool isVisibleInViewport = !canvas.GetVisibleRectInPageDips().IsEmpty;
                    if (!isVisibleInViewport && distance > 1)
                    {
                        // Keep Level 0 base bitmap on all realized canvases in the visual tree so pages never turn blank,
                        // and never trim any page currently visible in the viewport (e.g. Dual-Page or multi-page zoom);
                        // only release off-screen high-resolution detail patches.
                        canvas.TrimForBackground(keepBaseBitmap: true);
                    }
                }
            }
        }
    }

    private void OnCanvasLoaded(PdfPageCanvas canvas)
    {
        _activeCanvases.Add(canvas);
        canvas.SyncTouchManipulationMode();
        double targetZoom = ViewModel.CurrentDocument?.ZoomFactor ?? ContinuousScrollViewer.ZoomFactor;
        if (targetZoom > 0.05)
        {
            ApplyViewportToCanvas(canvas, targetZoom);
        }
    }

    private void OnCanvasUnloaded(PdfPageCanvas canvas)
    {
        _activeCanvases.Remove(canvas);
    }

    private void UpdateViewportPages(double currentZoom)
    {
        if (_activeCanvases.Count > 0)
        {
            foreach (var canvas in _activeCanvases.ToArray())
            {
                ApplyViewportToCanvas(canvas, currentZoom);
            }
        }
        else
        {
            FindAndApplyCanvases(ContinuousPagesRepeater, currentZoom);
        }
    }

    private static void ApplyViewportToCanvas(PdfPageCanvas canvas, double currentZoom)
    {
        canvas.UpdateViewport(currentZoom);
    }

    private static void FindAndApplyCanvases(DependencyObject? parent, double currentZoom)
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
                ApplyViewportToCanvas(canvas, currentZoom);
            }
            else
            {
                FindAndApplyCanvases(child, currentZoom);
            }
        }
    }

    /// <summary>
    /// Refreshes active canvases and restores focus when returning from the Settings overlay.
    /// </summary>
    public void OnReturnedFromSettings()
    {
        EnsureScrollViewerWheelHook();
        if (ViewModel.CurrentDocument is { } doc)
        {
            ApplyNightModeToAllCanvases(doc.IsNightMode);
            double activeZoom = ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor;
            UpdateViewportPages(activeZoom);
            DispatcherQueue.TryEnqueue(ReturnFocusToViewer);
        }
    }

    private void ApplyNightModeToAllCanvases(bool isNightMode)
    {
        foreach (var canvas in _activeCanvases.ToArray())
        {
            canvas.ApplyNightModeImmediate(isNightMode);
        }

        FindAndApplyNightMode(ContinuousPagesRepeater, isNightMode);
        AdjacentLeftPageCanvas?.ApplyNightModeImmediate(isNightMode);
        AdjacentRightPageCanvas?.ApplyNightModeImmediate(isNightMode);
    }

    private static void FindAndApplyNightMode(DependencyObject? parent, bool isNightMode)
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
                canvas.ApplyNightModeImmediate(isNightMode);
            }
            else
            {
                FindAndApplyNightMode(child, isNightMode);
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentDocument))
        {
            CancelInteractiveSlideSwipeImmediate();
            StopAutoScroll();
            SubscribeToDocument(ViewModel.CurrentDocument);
            if (ViewModel.CurrentDocument is not null)
            {
                SyncPaneOffsetsImmediate();
                ScrollToPage(ViewModel.CurrentDocument.CurrentPageIndex, allowUnclampedOffset: true);
                PlayDocumentOpenTransitionAnimation();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (ViewModel.CurrentDocument is { } activeDoc)
                    {
                        Models.SavedDocumentState? savedState = null;
                        bool hasCustomRestoredZoom = ViewModel.SettingsService.Settings.SaveDocumentViewSettings &&
                            !string.IsNullOrWhiteSpace(activeDoc.FilePath) &&
                            ViewModel.SettingsService.TryGetDocumentState(activeDoc.FilePath, out savedState) &&
                            savedState is { HasCustomViewSettings: true, ZoomFactor: { } z } &&
                            z >= ViewModels.DocumentViewModel.MinZoom && z <= ViewModels.DocumentViewModel.MaxZoom;

                        if (hasCustomRestoredZoom && savedState?.ZoomFactor is { } restoredZoom)
                        {
                            ApplyCustomRestoredZoom(activeDoc, restoredZoom);
                        }
                        else
                        {
                            ApplyZoomPreferenceByIndex(ViewModel.DefaultZoomIndex);
                        }
                    }

                    ReturnFocusToViewer();
                });
            }
            else
            {
                PlayEmptyStateTransitionAnimation();
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsSearchOpen))
        {
            if (!_isHorizontalPushAnimating && FloatingTopTransform is not null)
            {
                FloatingTopTransform.TranslateX = GetTargetFloatingTopOffsetDip();
            }

            if (ViewModel.IsSearchOpen)
            {
                PlaySearchBarOpenAnimation();
                FocusSearchBox();
            }
            else
            {
                _searchDebounceTimer.Stop();
                ReturnFocusToViewer();
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsAnnotationToolbarOpen))
        {
            if (!_isHorizontalPushAnimating && FloatingTopTransform is not null)
            {
                FloatingTopTransform.TranslateX = GetTargetFloatingTopOffsetDip();
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsNightMode))
        {
            ApplyNightModeToAllCanvases(ViewModel.IsNightMode);
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsPresentationMode))
        {
            HideTouchScrollScrubberImmediate();
            CancelInteractiveSlideSwipeImmediate();
            if (ViewModel.IsPresentationMode)
            {
                if (!_wasInPresentationMode)
                {
                    _wasInPresentationMode = true;
                    _prePresentationFittedVertically = _isFittedVertically;
                    _prePresentationZoom = ViewModel.ZoomBeforePresentation > 0.05
                        ? ViewModel.ZoomBeforePresentation
                        : (ViewModel.CurrentDocument?.ZoomFactor ?? ContinuousScrollViewer.ZoomFactor);
                }

                _isRestoringFromPresentation = false;
                FullScreenInfoBar.IsOpen = true;
                _presentationHideTimer.Stop();
                _presentationHideTimer.Start();

                var (fsWidth, fsHeight) = GetFullScreenDimensionsDip();
                ExecuteFitVertical(fsWidth > 0 ? fsWidth : null, fsHeight > 0 ? fsHeight : null, force: true);
            }
            else if (_wasInPresentationMode)
            {
                _wasInPresentationMode = false;
                _isRestoringFromPresentation = true;
                _isFittedVertically = _prePresentationFittedVertically;

                if (!ViewModel.IsFullScreen)
                {
                    _presentationHideTimer.Stop();
                    FullScreenInfoBar.IsOpen = false;
                }

                PdfPageCanvas.ClearSlideBuffer();
                PdfPageCanvas.ReleaseReusableBuffer();

                RestoreAfterPresentationExit();
                SyncPagedScrollViewerModes();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    _isRestoringFromPresentation = false;
                    RestoreAfterPresentationExit();
                    SyncPagedScrollViewerModes();
                });
                _ = Task.Run(async () =>
                {
                    await Task.Delay(85).ConfigureAwait(false);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!ViewModel.IsPresentationMode && ViewModel.CurrentDocument is { } activeDoc)
                        {
                            _isRestoringFromPresentation = false;
                            if (!activeDoc.IsContinuous && _isFittedVertically)
                            {
                                ExecuteFitVertical(force: true);
                            }

                            SyncPagedScrollViewerModes();
                        }
                    });
                });
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsFullScreen))
        {
            if (ViewModel.IsFullScreen || ViewModel.IsPresentationMode)
            {
                FullScreenInfoBar.IsOpen = true;
                _presentationHideTimer.Stop();
                _presentationHideTimer.Start();
            }
            else
            {
                _presentationHideTimer.Stop();
                FullScreenInfoBar.IsOpen = false;
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsCompactChrome))
        {
            AnimateStatusBarCompactMode(ViewModel.IsCompactChrome);
        }
    }

    private void SubscribeToDocument(DocumentViewModel? doc)
    {
        if (ReferenceEquals(_observedDocument, doc))
        {
            return;
        }

        UnsubscribeFromDocument();

        if (doc is not null)
        {
            _observedDocument = doc;
            _observedDocument.PropertyChanged += OnCurrentDocumentPropertyChanged;
            _observedDocument.FitVerticalRequested += OnFitPageRequested;
            _observedDocument.FitHorizontalRequested += OnFitWidthRequested;
            _observedDocument.ModeChanged += OnDocumentModeChanged;
            _observedDocument.ActiveSearchMatchScrollRequested += OnActiveSearchMatchScrollRequested;
            UpdateZoomSlider(doc.ZoomFactor);
            if (StatusBarPageBox is not null)
            {
                StatusBarPageBox.Text = doc.CurrentPageNumber.ToString();
            }
        }
    }

    private void UnsubscribeFromDocument()
    {
        if (_observedDocument is not null)
        {
            _observedDocument.PropertyChanged -= OnCurrentDocumentPropertyChanged;
            _observedDocument.FitVerticalRequested -= OnFitPageRequested;
            _observedDocument.FitHorizontalRequested -= OnFitWidthRequested;
            _observedDocument.ModeChanged -= OnDocumentModeChanged;
            _observedDocument.ActiveSearchMatchScrollRequested -= OnActiveSearchMatchScrollRequested;
            _observedDocument = null;
        }
    }

    private void OnActiveSearchMatchScrollRequested(PdfSearchMatch match)
    {
        ScrollToSearchMatch(match);
    }

    /// <summary>
    /// Automatically scrolls the active page and viewport so that <paramref name="match"/> is centered in the viewer.
    /// </summary>
    /// <param name="match">The search match to scroll into view.</param>
    private void ScrollToSearchMatch(PdfSearchMatch match)
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc ||
            match.PageIndex < 0 || match.PageIndex >= doc.Pages.Count)
        {
            return;
        }

        PdfPageInfo pageInfo = doc.Pages[match.PageIndex];
        PageCoordinateTransform transform = PageCoordinateTransform.FromPage(pageInfo, doc.Rotation);

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;

        for (int i = 0; i < match.Rects.Count; i++)
        {
            Rect r = transform.ToPageRect(match.Rects[i]);
            minX = Math.Min(minX, r.X);
            minY = Math.Min(minY, r.Y);
            maxX = Math.Max(maxX, r.X + r.Width);
            maxY = Math.Max(maxY, r.Y + r.Height);
        }

        double pageW = GetDisplayedPageWidthDip(pageInfo, doc.Rotation);
        double pageH = GetDisplayedPageHeightDip(pageInfo, doc.Rotation);
        double matchCenterX = minX <= maxX ? (minX + maxX) / 2.0 : pageW / 2.0;
        double matchCenterY = minY <= maxY ? (minY + maxY) / 2.0 : pageH / 2.0;

        _isProgrammaticPageScroll = true;

        if (doc.IsContinuous)
        {
            if (doc.CurrentPageIndex != match.PageIndex)
            {
                _isUpdatingPageFromScroll = true;
                try
                {
                    doc.CurrentPageIndex = match.PageIndex;
                }
                finally
                {
                    _isUpdatingPageFromScroll = false;
                }
            }

            if ( StatusBarPageBox is not null && !_isPageBoxEditing)
            {
                StatusBarPageBox.Text = doc.CurrentPageNumber.ToString();
            }

            double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
            double rowTopY = 24.0;
            int step = doc.IsDoublePage ? 2 : 1;
            int targetRow = match.PageIndex / step;
            for (int r = 0; r < targetRow && r * step < doc.Pages.Count; r++)
            {
                int leftIdx = r * step;
                double rowH = GetDisplayedPageHeightDip(doc.Pages[leftIdx], doc.Rotation);
                if (step == 2 && leftIdx + 1 < doc.Pages.Count)
                {
                    rowH = Math.Max(rowH, GetDisplayedPageHeightDip(doc.Pages[leftIdx + 1], doc.Rotation));
                }

                rowTopY += rowH + 16.0;
            }

            double contentMatchY = rowTopY + matchCenterY;
            double viewportH = ContinuousScrollViewer.ViewportHeight > 0 ? ContinuousScrollViewer.ViewportHeight : ContinuousScrollViewer.ActualHeight;
            double rawVOffset = Math.Max(0.0, (contentMatchY * zoom) - (viewportH / 2.0));
            double vOffset = ContinuousScrollViewer.ScrollableHeight > 0.5
                ? Math.Clamp(rawVOffset, 0.0, ContinuousScrollViewer.ScrollableHeight)
                : rawVOffset;

            double? hOffset = null;
            if (ContinuousScrollViewer.ScrollableWidth > 4.0)
            {
                double viewportW = ContinuousScrollViewer.ViewportWidth > 0 ? ContinuousScrollViewer.ViewportWidth : ContinuousScrollViewer.ActualWidth;
                double rawHOffset = Math.Max(0.0, ((24.0 + matchCenterX) * zoom) - (viewportW / 2.0));
                hOffset = Math.Clamp(rawHOffset, 0.0, ContinuousScrollViewer.ScrollableWidth);
            }

            ContinuousScrollViewer.ChangeView(hOffset, vOffset, null, disableAnimation: true);
            doc.LastVerticalOffset = vOffset;
            if (hOffset.HasValue)
            {
                doc.LastHorizontalOffset = hOffset.Value;
            }

            doc.FractionalPageIndex = match.PageIndex + Math.Clamp(matchCenterY / Math.Max(1.0, pageH), 0.0, 1.0);
        }
        else
        {
            if (doc.CurrentPageIndex != match.PageIndex)
            {
                doc.GoToPage(match.PageIndex);
            }

            double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
            double? vOffset = 0.0;
            double? hOffset = 0.0;

            if (ContinuousScrollViewer.ScrollableHeight > 4.0)
            {
                double viewportH = ContinuousScrollViewer.ViewportHeight > 0 ? ContinuousScrollViewer.ViewportHeight : ContinuousScrollViewer.ActualHeight;
                vOffset = Math.Clamp((matchCenterY * zoom) - (viewportH / 2.0), 0.0, ContinuousScrollViewer.ScrollableHeight);
            }

            if (ContinuousScrollViewer.ScrollableWidth > 4.0)
            {
                double viewportW = ContinuousScrollViewer.ViewportWidth > 0 ? ContinuousScrollViewer.ViewportWidth : ContinuousScrollViewer.ActualWidth;
                hOffset = Math.Clamp((matchCenterX * zoom) - (viewportW / 2.0), 0.0, ContinuousScrollViewer.ScrollableWidth);
            }

            ContinuousScrollViewer.ChangeView(hOffset, vOffset, null, disableAnimation: true);
            doc.LastVerticalOffset = vOffset ?? 0.0;
            doc.FractionalPageIndex = match.PageIndex;
        }

        ScheduleViewportUpdate();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _isProgrammaticPageScroll = false;
        });
    }

    private static double GetDisplayedPageWidthDip(PdfPageInfo page, int userRotationDegrees)
    {
        int effRot = PageCoordinateTransform.NormalizeRotation(page.Rotation + userRotationDegrees);
        return (effRot is 90 or 270 ? page.HeightPt : page.WidthPt) * (96.0 / 72.0);
    }

    private static double GetDisplayedPageHeightDip(PdfPageInfo page, int userRotationDegrees)
    {
        int effRot = PageCoordinateTransform.NormalizeRotation(page.Rotation + userRotationDegrees);
        return (effRot is 90 or 270 ? page.WidthPt : page.HeightPt) * (96.0 / 72.0);
    }

    private void RestoreAfterPresentationExit()
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc || ViewModel.IsPresentationMode)
        {
            return;
        }

        int targetPage = Math.Clamp(doc.CurrentPageIndex, 0, doc.Pages.Count - 1);
        double restoreZoom = Math.Clamp(_prePresentationZoom > 0.05 ? _prePresentationZoom : ViewModel.ZoomBeforePresentation, DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);

        _isSwitchingMode = true;
        _isApplyingZoom = true;
        try
        {
            EnsureScrollModesEnabledForViewChange();
            ContinuousScrollViewer.UpdateLayout();
            doc.ZoomFactor = restoreZoom;
            UpdateZoomSlider(restoreZoom);
            SyncPaneOffsetsImmediate();

            if (doc.IsContinuous)
            {
                double targetY = 24.0;
                int step = doc.IsDoublePage ? 2 : 1;
                int targetRow = targetPage / step;
                for (int r = 0; r < targetRow && r * step < doc.Pages.Count; r++)
                {
                    targetY += doc.Pages[r * step].DisplayHeightDip + 16.0;
                }

                double vOffset = targetY * restoreZoom;
                ContinuousScrollViewer.ChangeView(0, vOffset, (float)restoreZoom, disableAnimation: true);
                doc.LastVerticalOffset = vOffset;
                doc.FractionalPageIndex = targetPage;
            }
            else
            {
                ContinuousScrollViewer.ChangeView(0, 0, (float)restoreZoom, disableAnimation: true);
                doc.LastVerticalOffset = 0;
                doc.FractionalPageIndex = targetPage;
            }

            SyncPaneOffsetsImmediate();
            UpdateViewportPages(restoreZoom);
            ScheduleViewportUpdate();
        }
        finally
        {
            _isApplyingZoom = false;
            _isSwitchingMode = false;
        }

        if (!doc.IsContinuous && _isFittedVertically)
        {
            ExecuteFitVertical(force: true);
        }

        SyncPagedScrollViewerModes();
    }

    private void OnDocumentModeChanged(object? sender, EventArgs e)
    {
        _isTouchDoubleTapZoomedIn = false;
        _restoreFitAfterSmoothZoom = false;
        CancelInteractiveSlideSwipeImmediate();
        if (_isRestoringFromPresentation || ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        int targetPage = Math.Clamp(doc.CurrentPageIndex, 0, doc.PageCount - 1);
        _isSwitchingMode = true;

        if (!doc.IsContinuous)
        {
            _isFittedVertically = true;
            _isApplyingFitZoom = true;

            if (doc.CurrentPageIndex != targetPage)
            {
                doc.GoToPage(targetPage);
            }

            doc.FractionalPageIndex = targetPage;
            ContinuousScrollViewer.ChangeView(0, 0, null, disableAnimation: true);
            ExecuteFitVertical(force: true);

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    if (doc.CurrentPageIndex != targetPage)
                    {
                        doc.GoToPage(targetPage);
                    }

                    ExecuteFitVertical(force: true);
                    UpdateViewportPages(doc.ZoomFactor);
                }
                finally
                {
                    _isSwitchingMode = false;
                    SyncPagedScrollViewerModes();
                }
            });
        }
        else
        {
            ContinuousScrollViewer.UpdateLayout();
            ScrollToPage(targetPage, allowUnclampedOffset: true);

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    ContinuousScrollViewer.UpdateLayout();
                    if (doc.CurrentPageIndex != targetPage)
                    {
                        doc.GoToPage(targetPage);
                    }

                    ScrollToPage(targetPage, allowUnclampedOffset: true);
                    UpdateViewportPages(doc.ZoomFactor);
                }
                finally
                {
                    _isSwitchingMode = false;
                    _isApplyingFitZoom = false;
                }
            });
        }
    }

    private void OnFitPageRequested()
    {
        _isTouchDoubleTapZoomedIn = false;
        _restoreFitAfterSmoothZoom = false;
        ExecuteFitVertical();
    }

    private void OnFitWidthRequested()
    {
        _isTouchDoubleTapZoomedIn = false;
        _restoreFitAfterSmoothZoom = false;
        ExecuteFitHorizontal();
    }

    private void OnCurrentDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.CurrentPageIndex))
        {
            if (!_isCommittingSlideSwipe && (_isSlideSwipeActive || _isSlideSwipeAnimating))
            {
                CancelInteractiveSlideSwipeImmediate();
            }

            if (!_isUpdatingPageFromScroll && !_isApplyingFitZoom && !_isCommittingSlideSwipe && _observedDocument is not null)
            {
                ScrollToPage(_observedDocument.CurrentPageIndex);
            }

            if (!_isPageBoxEditing && StatusBarPageBox is not null && _observedDocument is not null)
            {
                StatusBarPageBox.Text = _observedDocument.CurrentPageNumber.ToString();
            }

            if (_observedDocument is { IsContinuous: false })
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    UpdateViewportPages(ViewModel.CurrentDocument?.ZoomFactor ?? ContinuousScrollViewer.ZoomFactor);
                });
            }
        }
        else if (e.PropertyName == nameof(DocumentViewModel.ActiveSelection))
        {
            if (_observedDocument?.ActiveSelection is { HasContent: true })
            {
                if (_isLastPointerInputTouch || _activeTouchSelectingCanvas is not null || _isDraggingSelectionHandle || _isTouchSelectionHandlesActive)
                {
                    _isTouchSelectionHandlesActive = true;
                }

                UpdateTouchSelectionHandlesLayout();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateTouchSelectionHandlesLayout);
            }
            else
            {
                _isTouchSelectionHandlesActive = false;
                UpdateTouchSelectionHandlesLayout();
                HideTouchSelectionFlyout();
            }
        }
        else if (e.PropertyName == nameof(DocumentViewModel.IsNightMode))
        {
            if (_observedDocument is not null)
            {
                ApplyNightModeToAllCanvases(_observedDocument.IsNightMode);
            }
        }
        else if (e.PropertyName is nameof(DocumentViewModel.IsContinuous) or nameof(DocumentViewModel.IsDoublePage))
        {
            if (_observedDocument?.IsContinuous != true)
            {
                HideTouchScrollScrubberImmediate();
            }

            SyncPagedScrollViewerModes();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                SyncPagedScrollViewerModes();
                UpdateViewportPages(ViewModel.CurrentDocument?.ZoomFactor ?? ContinuousScrollViewer.ZoomFactor);
            });
        }
        else if (e.PropertyName == nameof(DocumentViewModel.ZoomFactor))
        {
            if (_observedDocument is not null)
            {
                UpdateZoomSlider(_observedDocument.ZoomFactor);
            }

            if (_isApplyingZoom || _isUpdatingZoomFromScrollViewer || _isApplyingFitZoom || _isRestoringFromPresentation || (_wasInPresentationMode && !ViewModel.IsPresentationMode))
            {
                return;
            }

            if (_observedDocument is not null)
            {
                if (Math.Abs(ContinuousScrollViewer.ZoomFactor - _observedDocument.ZoomFactor) > 0.01)
                {
                    ApplyZoomAroundCenter((float)_observedDocument.ZoomFactor);
                }
                else
                {
                    UpdateViewportPages(_observedDocument.ZoomFactor);
                    ScheduleViewportUpdate();
                }
            }
        }
    }

    private void OnScrollViewerViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        _viewportDebounceTimer.Stop();
        if (_isTouchSelectionFlyoutOpen)
        {
            HideTouchSelectionFlyout();
        }

        if (!_isProgrammaticPageScroll && _activeTouchSelectingCanvas is null && !_isDraggingSelectionHandle)
        {
            double dV = Math.Abs(e.NextView.VerticalOffset - ContinuousScrollViewer.VerticalOffset);
            double dH = Math.Abs(e.NextView.HorizontalOffset - ContinuousScrollViewer.HorizontalOffset);
            double dZ = Math.Abs(e.NextView.ZoomFactor - ContinuousScrollViewer.ZoomFactor);
            if (dV > 5.0 || dH > 5.0 || dZ > 0.008)
            {
                CancelTouchHoldOnAllCanvases();
            }
        }

        if (_isTouchSelectionHandlesActive)
        {
            UpdateTouchSelectionHandlesLayout();
        }

        if (_isLastPointerInputTouch && !_isProgrammaticPageScroll && CanShowTouchScrollScrubber())
        {
            ShowAndSyncTouchScrollScrubber(resetAutoHideTimer: true);
        }
        else if (_isTouchScrollScrubberVisible)
        {
            UpdateTouchScrollScrubberPosition();
        }

        if (_isSwitchingMode || _isRestoringFromPresentation || _isApplyingFitZoom)
        {
            return;
        }

        if (!_isHorizontalPushAnimating && !_isSlideSwipeActive && !_isSlideSwipeAnimating && PagesRepeaterPushTransform is not null && e.NextView.ZoomFactor > 0.05)
        {
            PagesRepeaterPushTransform.TranslateX = GetTargetDocumentOffsetDip(e.NextView.ZoomFactor) / e.NextView.ZoomFactor;
        }

        if (!_isHorizontalPushAnimating && FloatingTopTransform is not null)
        {
            FloatingTopTransform.TranslateX = GetTargetFloatingTopOffsetDip();
        }

        if (!_isProgrammaticPageScroll && ViewModel.CurrentDocument is { IsContinuous: true })
        {
            ViewModel.CurrentDocument.NotifyUserDocumentScrollInitiated();
            if (Math.Abs(e.NextView.ZoomFactor - ContinuousScrollViewer.ZoomFactor) < 0.001)
            {
                UpdatePageProgressFromScroll(e.NextView.VerticalOffset);
            }
        }
    }

    private void OnScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isSwitchingMode || _isRestoringFromPresentation)
        {
            if (!e.IsIntermediate)
            {
                SyncPagedScrollViewerModes();
            }

            ScheduleViewportUpdate();
            return;
        }

        if (ViewModel.CurrentDocument is not null)
        {
            ViewModel.CurrentDocument.LastHorizontalOffset = ContinuousScrollViewer.HorizontalOffset;
            ViewModel.CurrentDocument.LastVerticalOffset = ContinuousScrollViewer.VerticalOffset;

            double activeZoom = ContinuousScrollViewer.ZoomFactor;
            if (!_isHorizontalPushAnimating && !_isSlideSwipeActive && !_isSlideSwipeAnimating && PagesRepeaterPushTransform is not null && activeZoom > 0.05)
            {
                PagesRepeaterPushTransform.TranslateX = GetTargetDocumentOffsetDip(activeZoom) / activeZoom;
            }

            if (!_isHorizontalPushAnimating && FloatingTopTransform is not null)
            {
                FloatingTopTransform.TranslateX = GetTargetFloatingTopOffsetDip();
            }

            if (_isApplyingFitZoom)
            {
                if (Math.Abs(activeZoom - ViewModel.CurrentDocument.ZoomFactor) < 0.01)
                {
                    _isApplyingFitZoom = false;
                }
            }
            else if (!_isApplyingZoom && activeZoom > 0.05 && Math.Abs(ViewModel.CurrentDocument.ZoomFactor - activeZoom) > 0.005)
            {
                _isFittedVertically = false;
                _isUpdatingZoomFromScrollViewer = true;
                try
                {
                    ViewModel.CurrentDocument.ZoomFactor = activeZoom;
                }
                finally
                {
                    _isUpdatingZoomFromScrollViewer = false;
                }
            }

            if (!_isApplyingFitZoom && !_isApplyingZoom && !e.IsIntermediate)
            {
                UpdatePageProgressFromScroll();
            }
        }

        if (_isTouchSelectionHandlesActive)
        {
            UpdateTouchSelectionHandlesLayout();
        }

        if (_isTouchScrollScrubberVisible)
        {
            UpdateTouchScrollScrubberPosition();
            if (!e.IsIntermediate && !_isDraggingTouchScrollScrubber)
            {
                _touchScrollHideTimer.Stop();
                _touchScrollHideTimer.Start();
            }
        }

        if (!e.IsIntermediate)
        {
            if (!_isDraggingTouchScrollScrubber)
            {
                SyncPagedScrollViewerModes();
            }

            ScheduleViewportUpdate();
        }
    }

    private void OnScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsureScrollViewerWheelHook();
        EnsureTouchpadRawInputHook();
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            return;
        }

        if (Math.Abs(e.NewSize.Width - _lastHandledScrollViewerSize.Width) < 1.0 &&
            Math.Abs(e.NewSize.Height - _lastHandledScrollViewerSize.Height) < 1.0)
        {
            return;
        }

        _lastHandledScrollViewerSize = e.NewSize;

        if (_isRestoringFromPresentation)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _isRestoringFromPresentation = false;
                RestoreAfterPresentationExit();
                SyncPagedScrollViewerModes();
            });
            return;
        }

        if (ViewModel?.IsPresentationMode == true || (_isFittedVertically && ViewModel?.CurrentDocument is { IsContinuous: false }))
        {
            DispatcherQueue.TryEnqueue(() => ExecuteFitVertical(GetAvailableDocumentWidthDip(e.NewSize.Width), e.NewSize.Height, force: true));
        }
        else if (ViewModel?.CurrentDocument is { ZoomFactor: > 1.01 })
        {
            ScheduleViewportUpdate();
        }
    }

    private void ScheduleViewportUpdate()
    {
        _viewportDebounceTimer.Stop();
        _viewportDebounceTimer.Start();
    }

    private void OnViewportDebounceTimerTick(object? sender, object e)
    {
        _viewportDebounceTimer.Stop();
        if (ViewModel.CurrentDocument is null)
        {
            return;
        }

        double activeZoom = ContinuousScrollViewer.ZoomFactor;
        if (activeZoom <= 0.05)
        {
            activeZoom = ViewModel.CurrentDocument.ZoomFactor;
        }

        if (!_isApplyingZoom && !_isApplyingFitZoom && Math.Abs(ViewModel.CurrentDocument.ZoomFactor - activeZoom) > 0.01)
        {
            _isUpdatingZoomFromScrollViewer = true;
            try
            {
                ViewModel.CurrentDocument.ZoomFactor = activeZoom;
            }
            finally
            {
                _isUpdatingZoomFromScrollViewer = false;
            }
        }

        UpdateViewportPages(activeZoom);
    }

    private void UpdatePageProgressFromScroll(double? explicitVerticalOffset = null)
    {
        if (_isProgrammaticPageScroll || ViewModel.CurrentDocument is not { IsContinuous: true, Pages.Count: > 0 } doc || ContinuousScrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        double vOffset = explicitVerticalOffset ?? ContinuousScrollViewer.VerticalOffset;
        double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
        double centerY = (vOffset + (ContinuousScrollViewer.ViewportHeight / 2.0)) / zoom;

        double currentY = 24.0;
        var pages = doc.Pages;
        int step = doc.IsDoublePage ? 2 : 1;
        int targetIndex = pages.Count - 1;
        double fractionalIndex = pages.Count - 1;

        for (int i = 0; i < pages.Count; i += step)
        {
            double pageHeight = pages[i].DisplayHeightDip;
            if (centerY < currentY)
            {
                targetIndex = i;
                fractionalIndex = i;
                break;
            }

            if (centerY <= currentY + pageHeight + 8.0)
            {
                targetIndex = i;
                double fraction = Math.Clamp((centerY - currentY) / Math.Max(1.0, pageHeight), 0.0, 1.0);
                fractionalIndex = i + (fraction * step);
                break;
            }

            currentY += pageHeight + 16.0;
        }

        if (doc.CurrentPageIndex != targetIndex)
        {
            _isUpdatingPageFromScroll = true;
            try
            {
                doc.CurrentPageIndex = targetIndex;
            }
            finally
            {
                _isUpdatingPageFromScroll = false;
            }
        }

        if (Math.Abs(doc.FractionalPageIndex - fractionalIndex) > 0.0005)
        {
            doc.FractionalPageIndex = fractionalIndex;
        }
    }

    private void UpdateCurrentPageIndexFromViewport()
    {
        if (_isProgrammaticPageScroll || ViewModel.CurrentDocument is null || !ViewModel.CurrentDocument.IsContinuous || ContinuousScrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        if (ViewModel.CurrentDocument.Pages.Count == 0)
        {
            return;
        }

        int targetIndex = CalculateCurrentPageIndexFromScroll();
        if (ViewModel.CurrentDocument.CurrentPageIndex != targetIndex)
        {
            _isUpdatingPageFromScroll = true;
            try
            {
                ViewModel.CurrentDocument.CurrentPageIndex = targetIndex;
            }
            finally
            {
                _isUpdatingPageFromScroll = false;
            }
        }
    }

    private int CalculateCurrentPageIndexFromScroll(double? explicitVerticalOffset = null)
    {
        if (ViewModel.CurrentDocument is null || ViewModel.CurrentDocument.Pages.Count == 0)
        {
            return 0;
        }

        if (!ViewModel.CurrentDocument.IsContinuous || ContinuousScrollViewer.ViewportHeight <= 0)
        {
            return ViewModel.CurrentDocument.CurrentPageIndex;
        }

        double vOffset = explicitVerticalOffset ?? ContinuousScrollViewer.VerticalOffset;
        double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
        double centerY = (vOffset + (ContinuousScrollViewer.ViewportHeight / 2.0)) / zoom;

        double currentY = 24.0; // Margin.Top
        var pages = ViewModel.CurrentDocument.Pages;
        int step = ViewModel.CurrentDocument.IsDoublePage ? 2 : 1;
        for (int i = 0; i < pages.Count; i += step)
        {
            double pageHeight = pages[i].DisplayHeightDip;
            if (centerY <= currentY + pageHeight + 8.0)
            {
                return i;
            }
            currentY += pageHeight + 16.0; // Spacing = 16
        }

        return pages.Count - 1;
    }

    private double CalculateFractionalPageIndex(double? explicitVerticalOffset = null)
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return 0.0;
        }

        if (!doc.IsContinuous || ContinuousScrollViewer.ViewportHeight <= 0)
        {
            return doc.CurrentPageIndex;
        }

        double vOffset = explicitVerticalOffset ?? ContinuousScrollViewer.VerticalOffset;
        double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
        double centerY = (vOffset + (ContinuousScrollViewer.ViewportHeight / 2.0)) / zoom;

        double currentY = 24.0;
        int step = doc.IsDoublePage ? 2 : 1;
        for (int i = 0; i < doc.Pages.Count; i += step)
        {
            double pageHeight = doc.Pages[i].DisplayHeightDip;
            if (centerY < currentY)
            {
                return i;
            }

            if (centerY <= currentY + pageHeight + 8.0)
            {
                double fraction = Math.Clamp((centerY - currentY) / Math.Max(1.0, pageHeight), 0.0, 1.0);
                return i + (fraction * step);
            }

            currentY += pageHeight + 16.0;
        }

        return doc.Pages.Count - 1;
    }

    private void ScrollToPage(int pageIndex, bool allowUnclampedOffset = false)
    {
        if (ViewModel.CurrentDocument is not { } doc || pageIndex < 0 || pageIndex >= doc.Pages.Count)
        {
            return;
        }

        _isProgrammaticPageScroll = true;

        if (ContinuousScrollViewer.VerticalScrollMode != ScrollMode.Enabled)
        {
            ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
        }

        if (doc.IsContinuous)
        {
            double zoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
            double targetY = 24.0;
            var pages = doc.Pages;
            int step = doc.IsDoublePage ? 2 : 1;
            int targetRow = pageIndex / step;
            for (int r = 0; r < targetRow && r * step < pages.Count; r++)
            {
                targetY += pages[r * step].DisplayHeightDip + 16.0;
            }

            double rawOffset = Math.Max(0.0, targetY * zoom);
            double verticalOffset = (allowUnclampedOffset || ContinuousScrollViewer.ScrollableHeight <= 0.5)
                ? rawOffset
                : Math.Clamp(rawOffset, 0.0, ContinuousScrollViewer.ScrollableHeight);
            ContinuousScrollViewer.ChangeView(null, verticalOffset, null, disableAnimation: true);
            doc.LastVerticalOffset = verticalOffset;
            doc.FractionalPageIndex = pageIndex;
        }
        else
        {
            EnsureScrollModesEnabledForViewChange();
            ContinuousScrollViewer.ChangeView(0, 0, null, disableAnimation: true);
            doc.LastVerticalOffset = 0;
            doc.FractionalPageIndex = pageIndex;
            if (_isFittedVertically || ViewModel.IsPresentationMode)
            {
                DispatcherQueue.TryEnqueue(() => ExecuteFitVertical(force: true));
            }
            else
            {
                SyncPagedScrollViewerModes();
            }
        }

        ScheduleViewportUpdate();

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _isProgrammaticPageScroll = false;
        });
    }

    private void ApplyZoomAroundCenter(float newZoom)
    {
        if (_isApplyingZoom)
        {
            return;
        }

        _isApplyingZoom = true;
        try
        {
            double oldZoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor);
            double halfViewportW = ContinuousScrollViewer.ViewportWidth / 2.0;
            double halfViewportH = ContinuousScrollViewer.ViewportHeight / 2.0;

            double newOffsetX;
            if (ContinuousScrollViewer.ScrollableWidth > 0.5)
            {
                double viewportCenterX = (ContinuousScrollViewer.HorizontalOffset + halfViewportW) / oldZoom;
                newOffsetX = Math.Max(0, viewportCenterX * newZoom - halfViewportW);
            }
            else
            {
                double unzoomedW = ContinuousPagesRepeater.ActualWidth + ContinuousPagesRepeater.Margin.Left + ContinuousPagesRepeater.Margin.Right;
                newOffsetX = Math.Max(0, (unzoomedW * newZoom - ContinuousScrollViewer.ViewportWidth) / 2.0);
            }

            double viewportCenterY = (ContinuousScrollViewer.VerticalOffset + halfViewportH) / oldZoom;
            double newOffsetY = Math.Max(0, viewportCenterY * newZoom - halfViewportH);

            if (!_isHorizontalPushAnimating && PagesRepeaterPushTransform is not null && newZoom > 0.05f)
            {
                PagesRepeaterPushTransform.TranslateX = GetTargetDocumentOffsetDip(newZoom) / newZoom;
            }

            if (!_isHorizontalPushAnimating && FloatingTopTransform is not null)
            {
                FloatingTopTransform.TranslateX = GetTargetFloatingTopOffsetDip();
            }

            EnsureScrollModesEnabledForViewChange();
            ContinuousScrollViewer.ChangeView(newOffsetX, newOffsetY, newZoom, disableAnimation: true);

            ViewModel.SetZoom(newZoom);
            SyncPagedScrollViewerModes();
            UpdateViewportPages(newZoom);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                SyncPagedScrollViewerModes();
                UpdateViewportPages(newZoom);
            });
            ScheduleViewportUpdate();
        }
        finally
        {
            _isApplyingZoom = false;
        }
    }

    /// <summary>
    /// Executes vertical fitting, adjusting zoom so the current page (or page pair) fits comfortably within the viewport.
    /// </summary>
    /// <param name="viewportW">Optional explicit viewport width.</param>
    /// <param name="viewportH">Optional explicit viewport height.</param>
    /// <param name="force">If true, overrides any in-flight fit operation.</param>
    public void ExecuteFitVertical(double? viewportW = null, double? viewportH = null, bool force = false)
    {
        if ((_isApplyingFitZoom && !force) || ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        _isApplyingFitZoom = true;
        try
        {
            EnsureScrollModesEnabledForViewChange();
            int cur = Math.Clamp(doc.CurrentPageIndex, 0, doc.Pages.Count - 1);
            var page = doc.Pages[cur];

            bool isPres = ViewModel.IsPresentationMode;
            bool isContinuous = doc.IsContinuous && !isPres;
            bool isDoublePage = doc.IsDoublePage && !isPres;

            // RepeaterMargin in continuous mode is 24 on left and 24 on right (48 total).
            // Add safety margin so page fits comfortably with clean padding and never triggers horizontal scrollbar.
            double marginW = isPres ? 0.0 : (!isContinuous ? 32.0 : 64.0);
            double marginH = isPres ? 0.0 : (!isContinuous ? 32.0 : 48.0);

            double rawWidth = ContinuousScrollViewer.ActualWidth > 0
                ? ContinuousScrollViewer.ActualWidth
                : (_lastHandledScrollViewerSize.Width > 0 ? _lastHandledScrollViewerSize.Width : ContinuousScrollViewer.ViewportWidth);

            double vWidth = (viewportW.HasValue && viewportW.Value > 0)
                ? viewportW.Value
                : GetAvailableDocumentWidthDip(rawWidth);

            double vHeight = (viewportH.HasValue && viewportH.Value > 0)
                ? viewportH.Value
                : (ContinuousScrollViewer.ActualHeight > 0
                    ? ContinuousScrollViewer.ActualHeight
                    : (_lastHandledScrollViewerSize.Height > 0 ? _lastHandledScrollViewerSize.Height : ContinuousScrollViewer.ViewportHeight));

            if (isPres)
            {
                var (fsWidth, fsHeight) = GetFullScreenDimensionsDip();
                if (fsWidth > 0 && fsHeight > 0)
                {
                    if (!viewportW.HasValue || viewportW.Value <= 0 || vWidth < fsWidth)
                    {
                        vWidth = fsWidth;
                    }
                    if (!viewportH.HasValue || viewportH.Value <= 0 || vHeight < fsHeight)
                    {
                        vHeight = fsHeight;
                    }
                }
            }

            double availW = Math.Max(100.0, vWidth - marginW);
            double availH = Math.Max(100.0, vHeight - marginH);
            double targetW = isDoublePage ? (page.DisplayWidthDip * 2.0 + 16.0) : page.DisplayWidthDip;
            double targetH = page.DisplayHeightDip;
            double targetZoom = Math.Clamp(Math.Min(availH / targetH, availW / targetW), DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);

            double hOffset = 0.0;
            double vOffset = 0.0;

            if (isContinuous)
            {
                double targetY = 24.0;
                int step = isDoublePage ? 2 : 1;
                int targetRow = cur / step;
                for (int r = 0; r < targetRow && r * step < doc.Pages.Count; r++)
                {
                    targetY += doc.Pages[r * step].DisplayHeightDip + 16.0;
                }

                double remainingY = Math.Max(0, vHeight - (targetH * targetZoom));
                vOffset = Math.Max(0, (targetY * targetZoom) - (remainingY / 2.0));
                hOffset = Math.Max(0, ((targetW * targetZoom) - vWidth) / 2.0);
                ContinuousScrollViewer.ChangeView(hOffset, vOffset, (float)targetZoom, disableAnimation: true);
            }
            else
            {
                ContinuousScrollViewer.ChangeView(0, 0, (float)targetZoom, disableAnimation: true);
            }

            if (!_isHorizontalPushAnimating && !_isSlideSwipeActive && !_isSlideSwipeAnimating && PagesRepeaterPushTransform is not null && targetZoom > 0.05)
            {
                PagesRepeaterPushTransform.TranslateX = GetTargetDocumentOffsetDip() / targetZoom;
            }

            _isFittedVertically = true;
            doc.ZoomFactor = targetZoom;
            SyncPagedScrollViewerModes();
            UpdateViewportPages(targetZoom);
            ScheduleViewportUpdate();

            bool enqueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    if (isContinuous)
                    {
                        ContinuousScrollViewer.ChangeView(hOffset, vOffset, null, disableAnimation: true);
                    }
                    else
                    {
                        ContinuousScrollViewer.ChangeView(0, 0, (float)targetZoom, disableAnimation: true);
                    }
                }
                finally
                {
                    if (Math.Abs(ContinuousScrollViewer.ZoomFactor - targetZoom) < 0.01)
                    {
                        _isApplyingFitZoom = false;
                    }
                }
            });

            if (!enqueued)
            {
                _isApplyingFitZoom = false;
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _isApplyingFitZoom = false;
                    });
                });
            }
        }
        catch
        {
            _isApplyingFitZoom = false;
            throw;
        }
    }

    /// <summary>
    /// Executes horizontal fitting, adjusting zoom so the current page (or page pair) fits width-wise within the viewport.
    /// </summary>
    /// <param name="viewportW">Optional explicit viewport width.</param>
    /// <param name="force">If true, overrides any in-flight fit operation.</param>
    public void ExecuteFitHorizontal(double? viewportW = null, bool force = false)
    {
        if ((_isApplyingFitZoom && !force) || ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        _isApplyingFitZoom = true;
        try
        {
            EnsureScrollModesEnabledForViewChange();
            int cur = Math.Clamp(doc.CurrentPageIndex, 0, doc.Pages.Count - 1);
            var page = doc.Pages[cur];

            bool isPres = ViewModel.IsPresentationMode;
            bool isContinuous = doc.IsContinuous && !isPres;
            bool isDoublePage = doc.IsDoublePage && !isPres;

            // Continuous mode has RepeaterMargin of 24 on left and 24 on right = 48 DIPs.
            // We include the margin in base content width plus extra breathing room so horizontal scrollbar never appears.
            double baseMargin = (isPres || !isContinuous) ? 0.0 : 48.0;
            double targetW = (isDoublePage ? (page.DisplayWidthDip * 2.0 + 16.0) : page.DisplayWidthDip) + baseMargin;

            // Extra safety margin (at least 32 DIPs total: 16 on each side) to guarantee ExtentWidth < ViewportWidth
            double safetyPadding = isPres ? 0.0 : 32.0;

            double rawWidth = ContinuousScrollViewer.ActualWidth > 0
                ? ContinuousScrollViewer.ActualWidth
                : (_lastHandledScrollViewerSize.Width > 0 ? _lastHandledScrollViewerSize.Width : ContinuousScrollViewer.ViewportWidth);

            double vWidth = (viewportW.HasValue && viewportW.Value > 0)
                ? viewportW.Value
                : GetAvailableDocumentWidthDip(rawWidth);

            if (isPres)
            {
                var (fsWidth, _) = GetFullScreenDimensionsDip();
                if (fsWidth > 0 && (!viewportW.HasValue || viewportW.Value <= 0 || vWidth < fsWidth))
                {
                    vWidth = fsWidth;
                }
            }

            double availW = Math.Max(100.0, vWidth - safetyPadding);
            double targetZoom = Math.Clamp(availW / targetW, DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);

            double? targetVOffset = null;
            if (isContinuous)
            {
                double targetY = 24.0;
                int step = doc.IsDoublePage ? 2 : 1;
                int targetRow = cur / step;
                for (int r = 0; r < targetRow && r * step < doc.Pages.Count; r++)
                {
                    targetY += doc.Pages[r * step].DisplayHeightDip + 16.0;
                }
                targetVOffset = targetY * targetZoom;
            }
            else
            {
                targetVOffset = 0;
            }

            if (!_isHorizontalPushAnimating && PagesRepeaterPushTransform is not null && targetZoom > 0.05)
            {
                PagesRepeaterPushTransform.TranslateX = GetTargetDocumentOffsetDip() / targetZoom;
            }

            _isFittedVertically = false;
            ContinuousScrollViewer.ChangeView(0, targetVOffset, (float)targetZoom, disableAnimation: true);
            doc.ZoomFactor = targetZoom;
            SyncPagedScrollViewerModes();
            UpdateViewportPages(targetZoom);
            ScheduleViewportUpdate();

            bool enqueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    if (doc.IsContinuous)
                    {
                        ContinuousScrollViewer.ChangeView(0, targetVOffset, null, disableAnimation: true);
                    }
                    else
                    {
                        ContinuousScrollViewer.ChangeView(0, targetVOffset, (float)targetZoom, disableAnimation: true);
                    }
                }
                finally
                {
                    if (Math.Abs(ContinuousScrollViewer.ZoomFactor - targetZoom) < 0.01)
                    {
                        _isApplyingFitZoom = false;
                    }
                }
            });

            if (!enqueued)
            {
                _isApplyingFitZoom = false;
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _isApplyingFitZoom = false;
                    });
                });
            }
        }
        catch
        {
            _isApplyingFitZoom = false;
            throw;
        }
    }

    private static bool IsKeyDown(Windows.System.VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    private void OnScrollViewerPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        EnsureTouchpadRawInputHook();
        if (_isAutoScrolling)
        {
            StopAutoScroll();
        }

        if (ReferenceEquals(e, _lastProcessedWheelEventArgs) || ViewModel.CurrentDocument is not { } doc)
        {
            return;
        }

        _lastProcessedWheelEventArgs = e;
        _isLastPointerInputTouch = false;
        if (_isTouchScrollScrubberVisible && !_isDraggingTouchScrollScrubber)
        {
            HideTouchScrollScrubberImmediate();
        }

        doc.NotifyUserDocumentScrollInitiated();

        var currentPt = e.GetCurrentPoint(ContinuousScrollViewer);
        var props = currentPt.Properties;
        bool isCtrlPressed = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0
            || IsKeyDown(Windows.System.VirtualKey.Control)
            || IsKeyDown(Windows.System.VirtualKey.LeftControl)
            || IsKeyDown(Windows.System.VirtualKey.RightControl);

        bool isShiftDown = IsKeyDown(Windows.System.VirtualKey.Shift)
            || IsKeyDown(Windows.System.VirtualKey.LeftShift)
            || IsKeyDown(Windows.System.VirtualKey.RightShift);
        bool isShiftKeyHeld = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0
            || isShiftDown;
        bool isTouchpadHorizontalWheel = props.IsHorizontalMouseWheel && !isShiftKeyHeld;
        bool isShiftPressed = isShiftKeyHeld || props.IsHorizontalMouseWheel;

        if (!isShiftPressed || isCtrlPressed)
        {
            if (doc.IsContinuous && !ViewModel.IsPresentationMode && ContinuousScrollViewer.VerticalScrollMode != ScrollMode.Enabled)
            {
                ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
            }
        }

        if (isCtrlPressed)
        {
            CancelInteractiveSlideSwipeImmediate();
            EnsureScrollModesEnabledForViewChange();
            _isFittedVertically = false;
            if (!doc.IsContinuous || ViewModel.IsPresentationMode)
            {
                e.Handled = true;
                int zoomDelta = props.MouseWheelDelta;
                if (zoomDelta != 0)
                {
                    StartOrUpdateSmoothWheelZoom(currentPt.Position, zoomDelta);
                }

                return;
            }

            return;
        }

        // Precision Touchpad 2-finger horizontal swipe in Paged / Presentation mode:
        // In Windows (WM_MOUSEHWHEEL), MouseWheelDelta > 0 means scroll right (content moves left -> swiping 2 fingers left -> Next slide),
        // and MouseWheelDelta < 0 means scroll left (content moves right -> swiping 2 fingers right -> Previous slide).
        if (isTouchpadHorizontalWheel && CanUseInteractiveSlideSwipe())
        {
            int tpDelta = props.MouseWheelDelta;
            if (tpDelta != 0)
            {
                bool swipingContentRight = tpDelta < 0;
                if (_isSlideSwipeActive || _isSlideSwipeAnimating || _isDrainingTouchpadInertia || CanStartSwipeInDirection(swipingRight: swipingContentRight))
                {
                    e.Handled = true;
                    HandleTouchpadHorizontalSlideSwipe(tpDelta);
                    return;
                }
            }
        }

        if (isShiftPressed)
        {
            e.Handled = true;
            int delta = props.MouseWheelDelta;
            if (delta != 0)
            {
                CancelInteractiveSlideSwipeImmediate();
                _isFittedVertically = false;
                double lockedVerticalOffset = ContinuousScrollViewer.VerticalOffset;

                if (ContinuousScrollViewer.VerticalScrollMode != ScrollMode.Disabled)
                {
                    ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
                }

                if (ContinuousScrollViewer.ScrollableWidth > 1.0)
                {
                    EnsureScrollModesEnabledForViewChange();
                    double scrollDelta = props.IsHorizontalMouseWheel
                        ? (delta * 0.9)
                        : (delta > 0 ? -120.0 : 120.0);
                    double newOffset = Math.Clamp(
                        ContinuousScrollViewer.HorizontalOffset + scrollDelta,
                        0,
                        ContinuousScrollViewer.ScrollableWidth);

                    ContinuousScrollViewer.ChangeView(newOffset, lockedVerticalOffset, null, disableAnimation: true);
                }
                else if (!doc.IsContinuous || ViewModel.IsPresentationMode)
                {
                    if (delta > 0)
                    {
                        doc.PreviousPage();
                    }
                    else
                    {
                        doc.NextPage();
                    }
                }
                else
                {
                    ContinuousScrollViewer.ChangeView(null, lockedVerticalOffset, null, disableAnimation: true);
                }

                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    SyncPagedScrollViewerModes();
                });
            }
            return;
        }

        if (!doc.IsContinuous || ViewModel.IsPresentationMode)
        {
            // If an interactive horizontal touchpad swipe or its inertia drain is active, ignore stray vertical wheel jitter
            if (_isSlideSwipeActive || _isSlideSwipeAnimating || _isDrainingTouchpadInertia)
            {
                e.Handled = true;
                return;
            }

            int delta = props.MouseWheelDelta;
            if (delta != 0)
            {
                bool canScrollVertically = ContinuousScrollViewer.ScrollableHeight > 4.0 && !_isFittedVertically && !ViewModel.IsPresentationMode;
                bool atTop = ContinuousScrollViewer.VerticalOffset <= 4.0;
                bool atBottom = ContinuousScrollViewer.VerticalOffset >= ContinuousScrollViewer.ScrollableHeight - 4.0;

                if (!canScrollVertically || (delta > 0 && atTop) || (delta < 0 && atBottom))
                {
                    e.Handled = true;

                    // Distinguish discrete physical mouse wheel detents (multiples of 120) from continuous Precision Touchpad
                    // sub-detent packets so diagonal 2-finger horizontal touchpad swipes are never hijacked by tiny vertical drift.
                    bool isDiscreteMouseWheel = Math.Abs(delta) >= 120 && (Math.Abs(delta) % 120 == 0);
                    if (!isDiscreteMouseWheel)
                    {
                        long nowTick = Environment.TickCount64;
                        if (nowTick < _ignoreTouchpadVerticalFlipUntilTick || (nowTick - _lastTouchpadHorizontalPacketTick < 360))
                        {
                            return;
                        }

                        if (nowTick - _lastTouchpadVerticalPacketTick > 140)
                        {
                            _touchpadVerticalWheelAccum = 0.0;
                            _touchpadHorizontalWheelAccum = 0.0;
                        }

                        _lastTouchpadVerticalPacketTick = nowTick;
                        _touchpadVerticalWheelAccum += delta;

                        if (Math.Abs(_touchpadVerticalWheelAccum) < 105.0 ||
                            Math.Abs(_touchpadHorizontalWheelAccum) >= Math.Abs(_touchpadVerticalWheelAccum))
                        {
                            return;
                        }

                        _touchpadVerticalWheelAccum = 0.0;
                        _ignoreTouchpadVerticalFlipUntilTick = nowTick + 240;
                    }

                    CancelInteractiveSlideSwipeImmediate();
                    if (delta > 0)
                    {
                        doc.PreviousPage();
                    }
                    else
                    {
                        doc.NextPage();
                    }

                    if (_isFittedVertically || ViewModel.IsPresentationMode)
                    {
                        DispatcherQueue.TryEnqueue(() => ExecuteFitVertical());
                    }
                }
                else
                {
                    e.Handled = true;
                    EnsureScrollModesEnabledForViewChange();
                    double stepV = -delta * 0.85;
                    double targetV = Math.Clamp(ContinuousScrollViewer.VerticalOffset + stepV, 0.0, ContinuousScrollViewer.ScrollableHeight);
                    ContinuousScrollViewer.ChangeView(null, targetV, null, disableAnimation: true);
                    doc.LastVerticalOffset = targetV;
                    ScheduleViewportUpdate();
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, SyncPagedScrollViewerModes);
                }
            }
        }
    }

    private void OnPrevZonePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        PrevPageArrowPill.Opacity = 1.0;
    }

    private void OnPrevZonePointerExited(object sender, PointerRoutedEventArgs e)
    {
        PrevPageArrowPill.Opacity = 0.0;
    }

    private void OnPrevZoneTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_isSlideSwipeActive || _isSlideSwipeAnimating || (Environment.TickCount64 - _lastSlideSwipeCompletedTick < 180))
        {
            return;
        }

        CancelInteractiveSlideSwipeImmediate();
        ViewModel.CurrentDocument?.PreviousPage();
        if (_isFittedVertically || ViewModel.IsPresentationMode)
        {
            DispatcherQueue.TryEnqueue(() => ExecuteFitVertical());
        }
    }

    private void OnNextZonePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        NextPageArrowPill.Opacity = 1.0;
    }

    private void OnNextZonePointerExited(object sender, PointerRoutedEventArgs e)
    {
        NextPageArrowPill.Opacity = 0.0;
    }

    private void OnNextZoneTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_isSlideSwipeActive || _isSlideSwipeAnimating || (Environment.TickCount64 - _lastSlideSwipeCompletedTick < 180))
        {
            return;
        }

        CancelInteractiveSlideSwipeImmediate();
        ViewModel.CurrentDocument?.NextPage();
        if (_isFittedVertically || ViewModel.IsPresentationMode)
        {
            DispatcherQueue.TryEnqueue(() => ExecuteFitVertical());
        }
    }

    private void OnExitPresentationZonePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ExitPresentationCloseBtnPill.Opacity = 1.0;
    }

    private void OnExitPresentationZonePointerExited(object sender, PointerRoutedEventArgs e)
    {
        ExitPresentationCloseBtnPill.Opacity = 0.0;
    }

    private void OnExitPresentationZoneTapped(object sender, TappedRoutedEventArgs e)
    {
        ViewModel.ExitPresentationCommand.Execute(null);
    }

    private void OnDocumentPointerMoved(object sender, PointerRoutedEventArgs e)
    {
    }

    private void ApplyInitialZoomForDocument()
    {
        if (ViewModel.CurrentDocument is null)
        {
            return;
        }

        ApplyZoomPreferenceByIndex(ViewModel.DefaultZoomIndex);
    }

    private void OnDefaultZoomPreferenceChanged(int zoomIndex)
    {
        if (ViewModel.CurrentDocument is not null && !ViewModel.IsPresentationMode)
        {
            ApplyZoomPreferenceByIndex(zoomIndex);
        }
    }

    private void ApplyCustomRestoredZoom(DocumentViewModel doc, double restoredZoom)
    {
        _isFittedVertically = false;
        double? targetVOffset = null;
        if (doc.IsContinuous)
        {
            double targetY = 24.0;
            var pages = doc.Pages;
            int step = doc.IsDoublePage ? 2 : 1;
            int targetRow = doc.CurrentPageIndex / step;
            for (int r = 0; r < targetRow && r * step < pages.Count; r++)
            {
                targetY += pages[r * step].DisplayHeightDip + 16.0;
            }
            targetVOffset = targetY * restoredZoom;
        }
        else
        {
            targetVOffset = 0;
        }

        EnsureScrollModesEnabledForViewChange();
        ContinuousScrollViewer.ChangeView(0, targetVOffset, (float)restoredZoom, disableAnimation: true);
        doc.ZoomFactor = restoredZoom;
        UpdateZoomSlider(restoredZoom);
        SyncPagedScrollViewerModes();
        UpdateViewportPages(restoredZoom);
        ScheduleViewportUpdate();
    }

    private void ApplyZoomPreferenceByIndex(int zoomIndex)
    {
        switch (zoomIndex)
        {
            case 0:
                ExecuteFitHorizontal(force: true);
                break;
            case 1:
                ExecuteFitVertical(force: true);
                break;
            default:
                _isFittedVertically = false;
                ApplyZoomAroundCenter(1.0f);
                if (ViewModel.CurrentDocument is { } doc)
                {
                    ScrollToPage(doc.CurrentPageIndex, allowUnclampedOffset: true);
                }
                break;
        }
    }

    private void OnNotificationRequested(string title, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel.TryConsumePendingNotification(out string consumedTitle, out string consumedMessage))
            {
                ShowTopNotification(consumedTitle, consumedMessage);
            }
            else
            {
                ShowTopNotification(title, message);
            }
        });
    }

    private void ShowTopNotification(string title, string message)
    {
        if (TopNotificationInfoBar is null)
        {
            return;
        }

        _topNotificationTimer.Stop();
        TopNotificationInfoBar.Title = title;
        TopNotificationInfoBar.Message = message;
        TopNotificationInfoBar.IsOpen = true;
        _topNotificationTimer.Start();
    }

    private void OnTopNotificationTimerTick(object? sender, object e)
    {
        _topNotificationTimer.Stop();
        if (TopNotificationInfoBar is not null)
        {
            TopNotificationInfoBar.IsOpen = false;
        }
    }

    private void OnTopNotificationInfoBarCloseButtonClick(InfoBar sender, object args)
    {
        _topNotificationTimer.Stop();
        TopNotificationInfoBar.IsOpen = false;
    }

    private void OnPresentationHideTimerTick(object? sender, object e)
    {
        _presentationHideTimer.Stop();
        FullScreenInfoBar.IsOpen = false;
    }

    private void OnFullScreenInfoBarCloseButtonClick(InfoBar sender, object args)
    {
        _presentationHideTimer.Stop();
        FullScreenInfoBar.IsOpen = false;
    }

    private void OnAnnotationNoticeCloseButtonClick(InfoBar sender, object args)
    {
        ViewModel.IsAnnotationNoticeOpen = false;
    }

    private void OnEscapeAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsSearchOpen)
        {
            args.Handled = true;
            ViewModel.CloseSearch();
            ReturnFocusToViewer();
        }
        else if (ViewModel.IsPresentationMode)
        {
            args.Handled = true;
            ViewModel.ExitPresentationCommand.Execute(null);
        }
        else if (ViewModel.IsFullScreen)
        {
            args.Handled = true;
            ViewModel.ToggleFullScreenCommand.Execute(null);
        }
    }

    /// <summary>
    /// Handles window-level tunneling key events for document navigation and classic keyboard shortcuts.
    /// </summary>
    /// <param name="e">Key event arguments.</param>
    public void HandleWindowPreviewKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (_isAutoScrolling)
        {
            StopAutoScroll();
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                return;
            }
        }

        object? focusedObj = XamlRoot is not null ? FocusManager.GetFocusedElement(XamlRoot) : null;
        if (IsInsideContentDialog(focusedObj as DependencyObject))
        {
            return;
        }

        bool isCtrl = IsKeyDown(Windows.System.VirtualKey.Control)
            || IsKeyDown(Windows.System.VirtualKey.LeftControl)
            || IsKeyDown(Windows.System.VirtualKey.RightControl);
        bool isShift = IsKeyDown(Windows.System.VirtualKey.Shift)
            || IsKeyDown(Windows.System.VirtualKey.LeftShift)
            || IsKeyDown(Windows.System.VirtualKey.RightShift);
        bool isAlt = IsKeyDown(Windows.System.VirtualKey.Menu)
            || IsKeyDown(Windows.System.VirtualKey.LeftMenu)
            || IsKeyDown(Windows.System.VirtualKey.RightMenu);

        // Global shortcuts that work even when the search box or page box is focused:
        if (isAlt && !isCtrl && e.Key == Windows.System.VirtualKey.Enter)
        {
            if (ViewModel.HasDocument)
            {
                e.Handled = true;
                _ = ViewModel.TogglePropertiesCommand.ExecuteAsync(null);
                return;
            }
        }

        if (isCtrl && !isAlt)
        {
            if (e.Key == Windows.System.VirtualKey.W || e.Key == Windows.System.VirtualKey.F4)
            {
                if (ViewModel.HasDocument)
                {
                    e.Handled = true;
                    ViewModel.CloseDocument();
                }
                return;
            }

            if (e.Key == Windows.System.VirtualKey.O && !isShift)
            {
                e.Handled = true;
                _ = ViewModel.OpenFileCommand.ExecuteAsync(null);
                return;
            }

            if (e.Key == Windows.System.VirtualKey.F && !isShift)
            {
                if (ViewModel.HasDocument)
                {
                    e.Handled = true;
                    ViewModel.OpenSearch();
                }
                return;
            }
        }

        if (!isCtrl && !isAlt)
        {
            if (e.Key == Windows.System.VirtualKey.F3 && ViewModel.HasDocument)
            {
                e.Handled = true;
                if (!ViewModel.IsSearchOpen)
                {
                    ViewModel.OpenSearch();
                }
                else if (isShift)
                {
                    _ = ViewModel.FindPreviousMatchAsync();
                }
                else
                {
                    _ = ViewModel.FindNextMatchAsync();
                }
                return;
            }

            if (e.Key == Windows.System.VirtualKey.F5 && ViewModel.HasDocument)
            {
                e.Handled = true;
                ViewModel.StartPresentation();
                return;
            }

            if (e.Key == Windows.System.VirtualKey.F11)
            {
                e.Handled = true;
                ViewModel.ToggleFullScreen();
                return;
            }
        }

        if (isCtrl && !isAlt && !isShift && e.Key == Windows.System.VirtualKey.C && ViewModel.HasTextSelection)
        {
            if (focusedObj is not TextBox focusedTextBox || focusedTextBox.SelectionLength == 0)
            {
                e.Handled = true;
                ViewModel.CopySelectedText();
                return;
            }
        }

        // If a TextBox or PasswordBox has focus, let it handle its own text editing and caret navigation.
        if (focusedObj is TextBox or PasswordBox)
        {
            return;
        }

        if (!isCtrl && !isAlt && e.Key == Windows.System.VirtualKey.Escape)
        {
            if (ViewModel.IsSearchOpen)
            {
                e.Handled = true;
                ViewModel.CloseSearch();
                ReturnFocusToViewer();
                return;
            }

            if (ViewModel.HasTextSelection)
            {
                e.Handled = true;
                ViewModel.CurrentDocument?.ClearSelection();
                return;
            }

            if (ViewModel.IsPresentationMode)
            {
                e.Handled = true;
                ViewModel.ExitPresentation();
                return;
            }

            if (ViewModel.IsFullScreen)
            {
                e.Handled = true;
                ViewModel.ToggleFullScreen();
                return;
            }

            if (ViewModel.IsAnnotationToolbarOpen)
            {
                e.Handled = true;
                ViewModel.IsAnnotationToolbarOpen = false;
                ViewModel.IsAnnotationNoticeOpen = false;
                return;
            }
        }

        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        if (isCtrl && !isAlt)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.C when !isShift:
                    if (ViewModel.HasTextSelection)
                    {
                        e.Handled = true;
                        ViewModel.CopySelectedText();
                    }
                    return;

                case Windows.System.VirtualKey.S:
                    e.Handled = true;
                    _ = ViewModel.SaveAsCommand.ExecuteAsync(null);
                    return;

                case Windows.System.VirtualKey.P:
                    e.Handled = true;
                    _ = ViewModel.PrintAsync();
                    return;

                case Windows.System.VirtualKey.N when isShift:
                    e.Handled = true;
                    _ = ViewModel.OpenCopyCommand.ExecuteAsync(null);
                    return;

                case Windows.System.VirtualKey.O when isShift:
                    e.Handled = true;
                    _ = ViewModel.OpenWithCommand.ExecuteAsync(null);
                    return;

                case Windows.System.VirtualKey.B:
                    e.Handled = true;
                    ViewModel.ToggleSidebar();
                    return;

                case Windows.System.VirtualKey.I:
                    e.Handled = true;
                    ViewModel.ToggleNightMode();
                    return;

                case Windows.System.VirtualKey.R:
                    e.Handled = true;
                    if (isShift)
                    {
                        ViewModel.RotateCounterClockwise();
                    }
                    else
                    {
                        ViewModel.RotateClockwise();
                    }
                    return;

                case Windows.System.VirtualKey.Right:
                    e.Handled = true;
                    ViewModel.RotateClockwise();
                    return;

                case Windows.System.VirtualKey.Left:
                    e.Handled = true;
                    ViewModel.RotateCounterClockwise();
                    return;

                case Windows.System.VirtualKey.Add:
                case (Windows.System.VirtualKey)187:
                    e.Handled = true;
                    OnZoomInClick(this, new RoutedEventArgs());
                    return;

                case Windows.System.VirtualKey.Subtract:
                case (Windows.System.VirtualKey)189:
                    e.Handled = true;
                    OnZoomOutClick(this, new RoutedEventArgs());
                    return;

                case Windows.System.VirtualKey.Number0:
                case Windows.System.VirtualKey.NumberPad0:
                    e.Handled = true;
                    ExecuteFitVertical(force: true);
                    return;

                case Windows.System.VirtualKey.Number1:
                case Windows.System.VirtualKey.NumberPad1:
                    e.Handled = true;
                    _isFittedVertically = false;
                    ApplyZoomAroundCenter(1.0f);
                    return;

                case Windows.System.VirtualKey.Number2:
                case Windows.System.VirtualKey.NumberPad2:
                    e.Handled = true;
                    ExecuteFitHorizontal(force: true);
                    return;

                case Windows.System.VirtualKey.Home:
                    e.Handled = true;
                    NavigateToDocumentStart();
                    return;

                case Windows.System.VirtualKey.End:
                    e.Handled = true;
                    NavigateToDocumentEnd();
                    return;
            }

            return;
        }

        if (isAlt && !isCtrl && e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = ViewModel.ShowPropertiesCommand.ExecuteAsync(null);
            return;
        }

        if (!isCtrl && !isAlt)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.F9:
                    e.Handled = true;
                    ViewModel.ToggleSidebar();
                    return;

                case Windows.System.VirtualKey.Down:
                    e.Handled = true;
                    HandleDirectionalVertical(down: true, isPageStep: false);
                    return;

                case Windows.System.VirtualKey.Up:
                    e.Handled = true;
                    HandleDirectionalVertical(down: false, isPageStep: false);
                    return;

                case Windows.System.VirtualKey.PageDown:
                    e.Handled = true;
                    HandleDirectionalVertical(down: true, isPageStep: true);
                    return;

                case Windows.System.VirtualKey.PageUp:
                    e.Handled = true;
                    HandleDirectionalVertical(down: false, isPageStep: true);
                    return;

                case Windows.System.VirtualKey.Space:
                    e.Handled = true;
                    HandleDirectionalVertical(down: !isShift, isPageStep: true);
                    return;

                case Windows.System.VirtualKey.Right:
                    e.Handled = true;
                    HandleDirectionalHorizontal(right: true);
                    return;

                case Windows.System.VirtualKey.Left:
                    e.Handled = true;
                    HandleDirectionalHorizontal(right: false);
                    return;

                case Windows.System.VirtualKey.Home:
                    e.Handled = true;
                    NavigateToDocumentStart();
                    return;

                case Windows.System.VirtualKey.End:
                    e.Handled = true;
                    NavigateToDocumentEnd();
                    return;
            }
        }
    }

    private static bool IsInsideContentDialog(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ContentDialog)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void HandleDirectionalVertical(bool down, bool isPageStep)
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        if (doc.IsContinuous && !ViewModel.IsPresentationMode)
        {
            double step = isPageStep
                ? Math.Max(160.0, ContinuousScrollViewer.ViewportHeight * 0.85)
                : 84.0;
            ScrollVerticallyBy(down ? step : -step);
            return;
        }

        // Non-continuous mode or Presentation mode:
        if (!isPageStep && !ViewModel.IsPresentationMode && !_isFittedVertically && ContinuousScrollViewer.ScrollableHeight > 4.0)
        {
            bool atTop = ContinuousScrollViewer.VerticalOffset <= 4.0;
            bool atBottom = ContinuousScrollViewer.VerticalOffset >= ContinuousScrollViewer.ScrollableHeight - 4.0;
            if ((down && !atBottom) || (!down && !atTop))
            {
                double newV = Math.Clamp(
                    ContinuousScrollViewer.VerticalOffset + (down ? 84.0 : -84.0),
                    0.0,
                    ContinuousScrollViewer.ScrollableHeight);
                ContinuousScrollViewer.ChangeView(null, newV, null, disableAnimation: true);
                doc.LastVerticalOffset = newV;
                ScheduleViewportUpdate();
                return;
            }
        }

        FlipPagedSlide(forward: down);
    }

    private void HandleDirectionalHorizontal(bool right)
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        if (!doc.IsContinuous || ViewModel.IsPresentationMode)
        {
            FlipPagedSlide(forward: right);
            return;
        }

        if (ContinuousScrollViewer.ScrollableWidth > 4.0)
        {
            double newH = Math.Clamp(
                ContinuousScrollViewer.HorizontalOffset + (right ? 84.0 : -84.0),
                0.0,
                ContinuousScrollViewer.ScrollableWidth);
            ContinuousScrollViewer.ChangeView(newH, ContinuousScrollViewer.VerticalOffset, null, disableAnimation: true);
            doc.LastHorizontalOffset = newH;
            ScheduleViewportUpdate();
        }
        else
        {
            if (right)
            {
                doc.NextPage();
            }
            else
            {
                doc.PreviousPage();
            }
        }
    }

    private void ScrollVerticallyBy(double deltaY)
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        if (ContinuousScrollViewer.VerticalScrollMode != ScrollMode.Enabled)
        {
            ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
        }

        double maxScroll = Math.Max(0.0, ContinuousScrollViewer.ScrollableHeight);
        double newOffset = Math.Clamp(ContinuousScrollViewer.VerticalOffset + deltaY, 0.0, maxScroll);

        ContinuousScrollViewer.ChangeView(null, newOffset, null, disableAnimation: true);
        SyncScrollMetricsFromVerticalOffset(newOffset);
        ScheduleViewportUpdate();
    }

    private void SyncScrollMetricsFromVerticalOffset(double verticalOffset)
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        doc.LastVerticalOffset = verticalOffset;
        doc.LastHorizontalOffset = ContinuousScrollViewer.HorizontalOffset;

        if (doc.IsContinuous && ContinuousScrollViewer.ViewportHeight > 0)
        {
            int targetIndex = CalculateCurrentPageIndexFromScroll(verticalOffset);
            if (doc.CurrentPageIndex != targetIndex)
            {
                _isUpdatingPageFromScroll = true;
                try
                {
                    doc.CurrentPageIndex = targetIndex;
                }
                finally
                {
                    _isUpdatingPageFromScroll = false;
                }
            }

            doc.FractionalPageIndex = CalculateFractionalPageIndex(verticalOffset);
        }
    }

    private void FlipPagedSlide(bool forward)
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        CancelInteractiveSlideSwipeImmediate();

        if (forward)
        {
            doc.NextPage();
        }
        else
        {
            doc.PreviousPage();
        }

        if (_isFittedVertically || ViewModel.IsPresentationMode)
        {
            DispatcherQueue.TryEnqueue(() => ExecuteFitVertical(force: true));
        }
        else
        {
            ContinuousScrollViewer.ChangeView(0, 0, null, disableAnimation: true);
            ScheduleViewportUpdate();
        }
    }

    private void NavigateToDocumentStart()
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        if (doc.IsContinuous && !ViewModel.IsPresentationMode)
        {
            _isUpdatingPageFromScroll = true;
            try
            {
                doc.GoToPage(0);
            }
            finally
            {
                _isUpdatingPageFromScroll = false;
            }

            ContinuousScrollViewer.ChangeView(null, 0.0, null, disableAnimation: true);
            doc.LastVerticalOffset = 0.0;
            doc.FractionalPageIndex = 0.0;
            ScheduleViewportUpdate();
        }
        else
        {
            doc.GoToPage(0);
            if (_isFittedVertically || ViewModel.IsPresentationMode)
            {
                DispatcherQueue.TryEnqueue(() => ExecuteFitVertical(force: true));
            }
            else
            {
                ContinuousScrollViewer.ChangeView(0, 0, null, disableAnimation: true);
                ScheduleViewportUpdate();
            }
        }
    }

    private void NavigateToDocumentEnd()
    {
        if (ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        int lastIndex = doc.Pages.Count - 1;
        if (doc.IsContinuous && !ViewModel.IsPresentationMode)
        {
            _isUpdatingPageFromScroll = true;
            try
            {
                doc.GoToPage(lastIndex);
            }
            finally
            {
                _isUpdatingPageFromScroll = false;
            }

            double maxScroll = Math.Max(0.0, ContinuousScrollViewer.ScrollableHeight);
            ContinuousScrollViewer.ChangeView(null, maxScroll, null, disableAnimation: true);
            doc.LastVerticalOffset = maxScroll;
            doc.FractionalPageIndex = lastIndex;
            ScheduleViewportUpdate();
        }
        else
        {
            doc.GoToPage(lastIndex);
            if (_isFittedVertically || ViewModel.IsPresentationMode)
            {
                DispatcherQueue.TryEnqueue(() => ExecuteFitVertical(force: true));
            }
            else
            {
                ContinuousScrollViewer.ChangeView(0, 0, null, disableAnimation: true);
                ScheduleViewportUpdate();
            }
        }
    }

    private void OnSearchFocusRequested()
    {
        FocusSearchBox();
    }

    private void FocusSearchBox()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel.IsSearchOpen && DocumentSearchBox is not null)
            {
                DocumentSearchBox.Focus(FocusState.Programmatic);
                DocumentSearchBox.SelectAll();
            }
        });
    }

    private void OnDocumentSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!ViewModel.IsSearchOpen || ViewModel.CurrentDocument is null)
        {
            return;
        }

        string text = DocumentSearchBox?.Text ?? string.Empty;
        ViewModel.SearchQuery = text;

        _searchDebounceTimer.Stop();
        if (string.IsNullOrEmpty(text))
        {
            ViewModel.CurrentDocument.ClearSearchResults();
            return;
        }

        _searchDebounceTimer.Start();
    }

    private void OnSearchDebounceTimerTick(object? sender, object e)
    {
        _searchDebounceTimer.Stop();
        if (!ViewModel.IsSearchOpen || ViewModel.CurrentDocument is not { } doc)
        {
            return;
        }

        string query = DocumentSearchBox?.Text ?? ViewModel.SearchQuery;
        _ = doc.ExecuteSearchAsync(query, ViewModel.IsSearchMatchCase, jumpToNearestMatch: true);
    }

    private void OnDocumentSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            _searchDebounceTimer.Stop();
            ViewModel.CloseSearch();
            ReturnFocusToViewer();
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _searchDebounceTimer.Stop();
            bool isShift = IsKeyDown(Windows.System.VirtualKey.Shift)
                || IsKeyDown(Windows.System.VirtualKey.LeftShift)
                || IsKeyDown(Windows.System.VirtualKey.RightShift);

            if (isShift)
            {
                _ = ViewModel.FindPreviousMatchAsync();
            }
            else
            {
                _ = ViewModel.FindNextMatchAsync();
            }
        }
        else if (e.Key == Windows.System.VirtualKey.F3)
        {
            e.Handled = true;
            _searchDebounceTimer.Stop();
            bool isShift = IsKeyDown(Windows.System.VirtualKey.Shift)
                || IsKeyDown(Windows.System.VirtualKey.LeftShift)
                || IsKeyDown(Windows.System.VirtualKey.RightShift);

            if (isShift)
            {
                _ = ViewModel.FindPreviousMatchAsync();
            }
            else
            {
                _ = ViewModel.FindNextMatchAsync();
            }
        }
    }

    private void OnSearchPrevClick(object sender, RoutedEventArgs e)
    {
        _searchDebounceTimer.Stop();
        _ = ViewModel.FindPreviousMatchAsync();
        DocumentSearchBox?.Focus(FocusState.Programmatic);
    }

    private void OnSearchNextClick(object sender, RoutedEventArgs e)
    {
        _searchDebounceTimer.Stop();
        _ = ViewModel.FindNextMatchAsync();
        DocumentSearchBox?.Focus(FocusState.Programmatic);
    }

    private void OnSearchMatchCaseToggleClick(object sender, RoutedEventArgs e)
    {
        _searchDebounceTimer.Stop();
        if (ViewModel.CurrentDocument is { } doc && !string.IsNullOrEmpty(ViewModel.SearchQuery))
        {
            _ = doc.ExecuteSearchAsync(ViewModel.SearchQuery, ViewModel.IsSearchMatchCase, jumpToNearestMatch: true);
        }

        DocumentSearchBox?.Focus(FocusState.Programmatic);
    }

    private void OnSearchCloseClick(object sender, RoutedEventArgs e)
    {
        _searchDebounceTimer.Stop();
        ViewModel.CloseSearch();
        ReturnFocusToViewer();
    }

    private void OnContextPrintClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.PrintAsync();
    }

    private void PlayEmptyStateTransitionAnimation()
    {
        if (EmptyStatePanel is null || EmptyStateTransform is null)
        {
            return;
        }

        _transitionStoryboard?.Stop();
        EmptyStatePanel.Opacity = 0.0;
        EmptyStateTransform.TranslateY = 16.0;
        EmptyStateTransform.ScaleX = 0.97;
        EmptyStateTransform.ScaleY = 0.97;

        var sb = new Storyboard();
        var ease = new ExponentialEase { Exponent = 4.5, EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(260));

        var opacityAnim = new DoubleAnimation { From = 0.0, To = 1.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(opacityAnim, EmptyStatePanel);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        sb.Children.Add(opacityAnim);

        var translateAnim = new DoubleAnimation { From = 16.0, To = 0.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(translateAnim, EmptyStateTransform);
        Storyboard.SetTargetProperty(translateAnim, "TranslateY");
        sb.Children.Add(translateAnim);

        var scaleXAnim = new DoubleAnimation { From = 0.97, To = 1.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(scaleXAnim, EmptyStateTransform);
        Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");
        sb.Children.Add(scaleXAnim);

        var scaleYAnim = new DoubleAnimation { From = 0.97, To = 1.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(scaleYAnim, EmptyStateTransform);
        Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");
        sb.Children.Add(scaleYAnim);

        sb.Completed += (_, _) =>
        {
            if (EmptyStatePanel is not null)
            {
                EmptyStatePanel.Opacity = 1.0;
            }
            if (EmptyStateTransform is not null)
            {
                EmptyStateTransform.TranslateY = 0.0;
                EmptyStateTransform.ScaleX = 1.0;
                EmptyStateTransform.ScaleY = 1.0;
            }
        };

        _transitionStoryboard = sb;
        sb.Begin();
    }

    private void PlayDocumentOpenTransitionAnimation()
    {
        if (DocumentAreaGrid is null || DocumentAreaTransform is null)
        {
            return;
        }

        _transitionStoryboard?.Stop();
        DocumentAreaGrid.Opacity = 0.0;
        DocumentAreaTransform.TranslateY = 10.0;

        var sb = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(200));

        var opacityAnim = new DoubleAnimation { From = 0.0, To = 1.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(opacityAnim, DocumentAreaGrid);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        sb.Children.Add(opacityAnim);

        var translateAnim = new DoubleAnimation { From = 10.0, To = 0.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(translateAnim, DocumentAreaTransform);
        Storyboard.SetTargetProperty(translateAnim, "TranslateY");
        sb.Children.Add(translateAnim);

        sb.Completed += (_, _) =>
        {
            if (DocumentAreaGrid is not null)
            {
                DocumentAreaGrid.Opacity = 1.0;
            }
            if (DocumentAreaTransform is not null)
            {
                DocumentAreaTransform.TranslateY = 0.0;
            }
        };

        _transitionStoryboard = sb;
        sb.Begin();
    }

    private double GetAvailableDocumentWidthDip(double rawViewerWidth)
    {
        if (ViewModel.IsPresentationMode)
        {
            return Math.Max(100.0, rawViewerWidth);
        }

        double leftW = (ViewModel.HasDocument && ViewModel.IsSidebarOpen) ? _currentSidebarWidthDip : 0.0;
        double appliedMarginLeft = ContinuousScrollViewer?.Margin.Left ?? 0.0;
        double unsubtractedLeftW = Math.Max(0.0, leftW - appliedMarginLeft);
        return Math.Max(120.0, rawViewerWidth - unsubtractedLeftW);
    }

    /// <summary>
    /// Gets the display dimensions in DIPs for the monitor hosting the active window.
    /// Used in presentation mode to eliminate initial render size discrepancy and prevent flicker.
    /// </summary>
    private (double WidthDip, double HeightDip) GetFullScreenDimensionsDip()
    {
        try
        {
            var windowId = _windowContext.WindowId;
            if (windowId.Value == 0 && _windowContext.WindowHandle != 0)
            {
                windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowContext.WindowHandle);
            }

            if (windowId.Value != 0)
            {
                var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                if (displayArea is not null)
                {
                    double scale = (XamlRoot?.RasterizationScale > 0)
                        ? XamlRoot.RasterizationScale
                        : ((_windowContext.XamlRoot?.RasterizationScale > 0) ? _windowContext.XamlRoot.RasterizationScale : 1.0);

                    if (scale <= 0.0)
                    {
                        scale = 1.0;
                    }

                    return (displayArea.OuterBounds.Width / scale, displayArea.OuterBounds.Height / scale);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DocumentView.GetFullScreenDimensionsDip: Failed to query display bounds: {ex.Message}");
        }

        return (0.0, 0.0);
    }

    private void OnNightModeMenuItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject root)
        {
            AlignMenuFlyoutAcceleratorRight(root);
        }
    }

    private static void AlignMenuFlyoutAcceleratorRight(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock tb && tb.Name == "KeyboardAcceleratorTextBlock")
            {
                tb.TextAlignment = TextAlignment.Right;
                tb.HorizontalAlignment = HorizontalAlignment.Right;
                return;
            }

            AlignMenuFlyoutAcceleratorRight(child);
        }
    }

    private double GetTargetDocumentOffsetDip(double? explicitZoom = null)
    {
        if (ViewModel is null || !ViewModel.HasDocument || ViewModel.IsPresentationMode)
        {
            return 0.0;
        }

        double leftW = (ViewModel.IsSidebarOpen && !ViewModel.IsPresentationMode) ? _currentSidebarWidthDip : 0.0;
        return leftW / 2.0;
    }

    private double GetTargetFloatingTopOffsetDip()
    {
        if (ViewModel is null || !ViewModel.HasDocument || ViewModel.IsPresentationMode)
        {
            return 0.0;
        }

        double leftW = (ViewModel.IsSidebarOpen && !ViewModel.IsPresentationMode) ? _currentSidebarWidthDip : 0.0;
        return leftW / 2.0;
    }

    /// <summary>
    /// Synchronizes all pane-dependent transforms immediately without animation (used on startup, document switch, or splitter drag).
    /// </summary>
    /// <param name="sidebarOpen">Whether the left sidebar is open.</param>
    /// <param name="sidebarWidth">Current width of the left sidebar in DIPs.</param>
    public void SetSidebarStateImmediate(bool sidebarOpen, double sidebarWidth)
    {
        if (sidebarWidth > 0)
        {
            _currentSidebarWidthDip = sidebarWidth;
        }

        SyncPaneOffsetsImmediate();
    }

    private void SyncPaneOffsetsImmediate()
    {
        _horizontalPushStoryboard?.Stop();
        _isHorizontalPushAnimating = false;

        // Keep ContinuousScrollViewer.Margin permanently at 0 so the ScrollViewer never shifts or triggers a second layout push.
        if (ContinuousScrollViewer is not null && ContinuousScrollViewer.Margin.Left != 0.0)
        {
            ContinuousScrollViewer.Margin = new Thickness(0);
        }

        double leftW = (ViewModel.HasDocument && ViewModel.IsSidebarOpen && !ViewModel.IsPresentationMode) ? _currentSidebarWidthDip : 0.0;
        double zoom = ContinuousScrollViewer?.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : (ViewModel.CurrentDocument?.ZoomFactor ?? 1.0);
        double targetDocOffsetDip = GetTargetDocumentOffsetDip(zoom);

        if (PagesRepeaterPushTransform is not null)
        {
            PagesRepeaterPushTransform.TranslateX = targetDocOffsetDip / Math.Max(0.05, zoom);
        }

        if (FloatingTopTransform is not null)
        {
            FloatingTopTransform.TranslateX = GetTargetFloatingTopOffsetDip();
        }

        if (StatusBarBackdropTransform is not null)
        {
            StatusBarBackdropTransform.TranslateX = leftW;
        }

        if (StatusBarLeftTransform is not null)
        {
            StatusBarLeftTransform.TranslateX = leftW;
        }

        if (PrevPageZoneTransform is not null)
        {
            PrevPageZoneTransform.TranslateX = leftW;
        }

        if (NextPageZoneTransform is not null)
        {
            NextPageZoneTransform.TranslateX = 0.0;
        }
    }

    /// <summary>
    /// Smoothly animates the document, status bar, and navigation zones when the left sidebar opens or closes,
    /// pushing the document once by exactly half of the sidebar width simultaneously with the sidebar animation.
    /// </summary>
    /// <param name="sidebarOpen">Whether the left sidebar is opening.</param>
    /// <param name="sidebarWidth">Width of the left sidebar in DIPs.</param>
    public void AnimateSidebarTransition(bool sidebarOpen, double sidebarWidth)
    {
        if (sidebarWidth > 0)
        {
            _currentSidebarWidthDip = sidebarWidth;
        }

        AnimateDocumentAndChromeToTargetOffsets();
    }

    private void AnimateDocumentAndChromeToTargetOffsets()
    {
        if (PagesRepeaterPushTransform is null)
        {
            return;
        }

        double startRepeaterX = PagesRepeaterPushTransform.TranslateX;
        double startTopX = FloatingTopTransform?.TranslateX ?? 0.0;
        double startBackdropX = StatusBarBackdropTransform?.TranslateX ?? 0.0;
        double startLeftX = StatusBarLeftTransform?.TranslateX ?? 0.0;
        double startPrevX = PrevPageZoneTransform?.TranslateX ?? 0.0;

        _horizontalPushStoryboard?.Stop();
        _isHorizontalPushAnimating = true;

        if (ContinuousScrollViewer is not null && ContinuousScrollViewer.Margin.Left != 0.0)
        {
            ContinuousScrollViewer.Margin = new Thickness(0);
        }

        double leftW = (ViewModel.HasDocument && ViewModel.IsSidebarOpen && !ViewModel.IsPresentationMode) ? _currentSidebarWidthDip : 0.0;
        double zoom = (ContinuousScrollViewer?.ZoomFactor > 0.05) ? ContinuousScrollViewer.ZoomFactor : (ViewModel.CurrentDocument?.ZoomFactor ?? 1.0);

        double targetDocOffsetDip = GetTargetDocumentOffsetDip(zoom);
        double targetRepeaterX = targetDocOffsetDip / Math.Max(0.05, zoom);
        double targetTopOffsetDip = GetTargetFloatingTopOffsetDip();

        PagesRepeaterPushTransform.TranslateX = startRepeaterX;

        var sb = new Storyboard();
        var ease = new ExponentialEase { Exponent = 4.5, EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(250));

        var docPush = new DoubleAnimation
        {
            From = startRepeaterX,
            To = targetRepeaterX,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(docPush, PagesRepeaterPushTransform);
        Storyboard.SetTargetProperty(docPush, "TranslateX");
        sb.Children.Add(docPush);

        if (FloatingTopTransform is not null)
        {
            FloatingTopTransform.TranslateX = startTopX;
            var topAnim = new DoubleAnimation
            {
                From = startTopX,
                To = targetTopOffsetDip,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(topAnim, FloatingTopTransform);
            Storyboard.SetTargetProperty(topAnim, "TranslateX");
            sb.Children.Add(topAnim);
        }

        if (StatusBarBackdropTransform is not null)
        {
            StatusBarBackdropTransform.TranslateX = startBackdropX;
            var sbBackdropAnim = new DoubleAnimation
            {
                From = startBackdropX,
                To = leftW,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(sbBackdropAnim, StatusBarBackdropTransform);
            Storyboard.SetTargetProperty(sbBackdropAnim, "TranslateX");
            sb.Children.Add(sbBackdropAnim);
        }

        if (StatusBarLeftTransform is not null)
        {
            StatusBarLeftTransform.TranslateX = startLeftX;
            var sbLeftAnim = new DoubleAnimation
            {
                From = startLeftX,
                To = leftW,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(sbLeftAnim, StatusBarLeftTransform);
            Storyboard.SetTargetProperty(sbLeftAnim, "TranslateX");
            sb.Children.Add(sbLeftAnim);
        }

        if (PrevPageZoneTransform is not null)
        {
            PrevPageZoneTransform.TranslateX = startPrevX;
            var prevAnim = new DoubleAnimation
            {
                From = startPrevX,
                To = leftW,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(prevAnim, PrevPageZoneTransform);
            Storyboard.SetTargetProperty(prevAnim, "TranslateX");
            sb.Children.Add(prevAnim);
        }

        if (NextPageZoneTransform is not null)
        {
            var nextAnim = new DoubleAnimation
            {
                From = NextPageZoneTransform.TranslateX,
                To = 0.0,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(nextAnim, NextPageZoneTransform);
            Storyboard.SetTargetProperty(nextAnim, "TranslateX");
            sb.Children.Add(nextAnim);
        }

        sb.Completed += (_, _) =>
        {
            SyncPaneOffsetsImmediate();
            ScheduleViewportUpdate();
        };

        _horizontalPushStoryboard = sb;
        sb.Begin();
    }

    // =========================================================================
    // Native Windows 11 Print Preview & High-DPI Printing (PrintManagerInterop)
    // =========================================================================

    private async Task OnPrintRequestedAsync()
    {
        if (ViewModel.CurrentDocument is not { PageCount: > 0 } doc)
        {
            return;
        }

        nint hwnd = _windowContext.WindowHandle;
        if (hwnd == nint.Zero)
        {
            return;
        }

        try
        {
            PreparePrintDocumentForWindow(hwnd, doc);
            await PrintManagerInterop.ShowPrintUIForWindowAsync(hwnd);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ShowPrintUIForWindowAsync failed: {ex.Message}");
        }
    }

    private void PreparePrintDocumentForWindow(nint hwnd, DocumentViewModel doc)
    {
        _activePrintDocumentVm = doc;

        if (_registeredPrintManager is null || _registeredPrintHwnd != hwnd)
        {
            if (_registeredPrintManager is not null)
            {
                _registeredPrintManager.PrintTaskRequested -= OnPrintTaskRequested;
            }

            _registeredPrintManager = PrintManagerInterop.GetForWindow(hwnd);
            _registeredPrintHwnd = hwnd;
            _registeredPrintManager.PrintTaskRequested += OnPrintTaskRequested;
        }

        CleanupPrintDocumentInstance();

        _printDocument = new PrintDocument();
        _printDocumentSource = _printDocument.DocumentSource;
        _printDocument.Paginate += OnPrintDocumentPaginate;
        _printDocument.GetPreviewPage += OnPrintDocumentGetPreviewPage;
        _printDocument.AddPages += OnPrintDocumentAddPages;
    }

    private void CleanupPrintDocumentInstance()
    {
        if (_printDocument is not null)
        {
            _printDocument.Paginate -= OnPrintDocumentPaginate;
            _printDocument.GetPreviewPage -= OnPrintDocumentGetPreviewPage;
            _printDocument.AddPages -= OnPrintDocumentAddPages;
            _printDocument = null;
        }

        _printDocumentSource = null;
        _currentPrintTaskOptions = null;
        PrintStagingCanvas?.Children.Clear();
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        DocumentViewModel? doc = _activePrintDocumentVm ?? ViewModel.CurrentDocument;
        IPrintDocumentSource? source = _printDocumentSource;
        if (doc is null || source is null)
        {
            return;
        }

        string title = string.IsNullOrWhiteSpace(doc.FileName) ? "WinPDF Document" : doc.FileName;

        PrintTask printTask = args.Request.CreatePrintTask(title, sourceRequestedArgs =>
        {
            PrintTaskSourceRequestedDeferral deferral = sourceRequestedArgs.GetDeferral();
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    sourceRequestedArgs.SetSource(source);
                }
                finally
                {
                    deferral.Complete();
                }
            });
        });

        try
        {
            PrintPageRangeOptions rangeOptions = printTask.Options.PageRangeOptions;
            rangeOptions.AllowAllPages = true;
            rangeOptions.AllowCurrentPage = true;
            rangeOptions.AllowCustomSetOfPages = true;
        }
        catch
        {
        }

        try
        {
            if (doc.Pages.Count > 0)
            {
                int curIdx = Math.Clamp(doc.CurrentPageIndex, 0, doc.Pages.Count - 1);
                double aspect = DocumentViewModel.GetPageDisplayAspectRatio(doc.Pages[curIdx], doc.Rotation);
                printTask.Options.Orientation = aspect > 1.05
                    ? PrintOrientation.Landscape
                    : PrintOrientation.Portrait;
            }
        }
        catch
        {
        }

        printTask.Completed += OnPrintTaskCompleted;
    }

    private void OnPrintTaskCompleted(PrintTask sender, PrintTaskCompletedEventArgs args)
    {
        sender.Completed -= OnPrintTaskCompleted;
        DispatcherQueue.TryEnqueue(() =>
        {
            PrintStagingCanvas?.Children.Clear();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: true);
        });
    }

    private void OnPrintDocumentPaginate(object sender, PaginateEventArgs e)
    {
        DocumentViewModel? doc = _activePrintDocumentVm ?? ViewModel.CurrentDocument;
        if (_printDocument is null || doc is null)
        {
            return;
        }

        _currentPrintTaskOptions = e.PrintTaskOptions;
        _printDocument.SetPreviewPageCount(doc.PageCount, PreviewPageCountType.Final);
    }

    private void OnPrintDocumentGetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        DocumentViewModel? doc = _activePrintDocumentVm ?? ViewModel.CurrentDocument;
        if (_printDocument is null || doc is null || e.PageNumber < 1 || e.PageNumber > doc.PageCount)
        {
            return;
        }

        try
        {
            UIElement pageVisual = BuildPrintPageElement(doc, e.PageNumber - 1, _currentPrintTaskOptions, targetDpi: 150.0);
            _printDocument.SetPreviewPage(e.PageNumber, pageVisual);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetPreviewPage failed for page {e.PageNumber}: {ex.Message}");
        }
    }

    private void OnPrintDocumentAddPages(object sender, AddPagesEventArgs e)
    {
        DocumentViewModel? doc = _activePrintDocumentVm ?? ViewModel.CurrentDocument;
        if (_printDocument is null || doc is null)
        {
            _printDocument?.AddPagesComplete();
            return;
        }

        try
        {
            PrintTaskOptions options = e.PrintTaskOptions ?? _currentPrintTaskOptions!;
            double targetDpi = doc.PageCount <= 30 ? 220.0 : 160.0;
            for (int i = 0; i < doc.PageCount; i++)
            {
                UIElement pageVisual = BuildPrintPageElement(doc, i, options, targetDpi);
                _printDocument.AddPage(pageVisual);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AddPages failed: {ex.Message}");
        }
        finally
        {
            _printDocument.AddPagesComplete();
        }
    }

    private UIElement BuildPrintPageElement(
        DocumentViewModel doc,
        int pageIndex,
        PrintTaskOptions? printOptions,
        double targetDpi)
    {
        PdfPageInfo pageInfo = doc.Pages[pageIndex];
        int effectiveRot = PageCoordinateTransform.NormalizeRotation(pageInfo.Rotation + doc.Rotation);
        bool swap = effectiveRot is 90 or 270;
        double pageWidthPt = Math.Max(1.0, swap ? pageInfo.HeightPt : pageInfo.WidthPt);
        double pageHeightPt = Math.Max(1.0, swap ? pageInfo.WidthPt : pageInfo.HeightPt);

        double paperWidthDip = pageWidthPt * (96.0 / 72.0);
        double paperHeightDip = pageHeightPt * (96.0 / 72.0);
        Rect imageableRect = new(0, 0, paperWidthDip, paperHeightDip);

        if (printOptions is not null)
        {
            try
            {
                PrintPageDescription desc = printOptions.GetPageDescription((uint)(pageIndex + 1));
                if (desc.PageSize.Width > 0 && desc.PageSize.Height > 0)
                {
                    paperWidthDip = desc.PageSize.Width;
                    paperHeightDip = desc.PageSize.Height;
                    imageableRect = (desc.ImageableRect.Width > 0 && desc.ImageableRect.Height > 0)
                        ? desc.ImageableRect
                        : new Rect(0, 0, paperWidthDip, paperHeightDip);
                }
            }
            catch
            {
            }
        }

        double fitScaleDip = Math.Min(imageableRect.Width / pageWidthPt, imageableRect.Height / pageHeightPt);
        double drawWidthDip = Math.Max(1.0, pageWidthPt * fitScaleDip);
        double drawHeightDip = Math.Max(1.0, pageHeightPt * fitScaleDip);
        double offsetLeftDip = imageableRect.X + ((imageableRect.Width - drawWidthDip) / 2.0);
        double offsetTopDip = imageableRect.Y + ((imageableRect.Height - drawHeightDip) / 2.0);

        double dpiScale = Math.Max(1.0, targetDpi / 96.0);
        int renderWidthPx = Math.Max(1, (int)Math.Round(drawWidthDip * dpiScale));
        int renderHeightPx = Math.Max(1, (int)Math.Round(drawHeightDip * dpiScale));

        const int MaxPrintDimensionPx = 2800;
        if (renderWidthPx > MaxPrintDimensionPx || renderHeightPx > MaxPrintDimensionPx)
        {
            double clamp = Math.Min((double)MaxPrintDimensionPx / renderWidthPx, (double)MaxPrintDimensionPx / renderHeightPx);
            renderWidthPx = Math.Max(1, (int)Math.Round(renderWidthPx * clamp));
            renderHeightPx = Math.Max(1, (int)Math.Round(renderHeightPx * clamp));
        }

        int userRot = PageCoordinateTransform.NormalizeRotation(doc.Rotation);
        PdfRenderRequest request = new(
            doc.DocumentId,
            pageIndex,
            renderWidthPx,
            renderHeightPx,
            renderWidthPx,
            renderHeightPx,
            0,
            0,
            userRot,
            PdfRenderFlags.Annotations | PdfRenderFlags.Printing | PdfRenderFlags.LimitedImageCache);

        using PdfBitmapLease lease = _pdfService.RenderAsync(request, PdfJobPriority.Critical).GetAwaiter().GetResult();
        WriteableBitmap? bitmap = CreatePrintWriteableBitmap(lease);

        Canvas pageCanvas = new()
        {
            Width = paperWidthDip,
            Height = paperHeightDip,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White)
        };

        if (bitmap is not null)
        {
            Image pageImage = new()
            {
                Width = drawWidthDip,
                Height = drawHeightDip,
                Stretch = Stretch.Fill,
                Source = bitmap
            };
            Canvas.SetLeft(pageImage, offsetLeftDip);
            Canvas.SetTop(pageImage, offsetTopDip);
            pageCanvas.Children.Add(pageImage);
        }

        if (PrintStagingCanvas is not null)
        {
            PrintStagingCanvas.Children.Add(pageCanvas);
            pageCanvas.Measure(new Size(paperWidthDip, paperHeightDip));
            pageCanvas.Arrange(new Rect(0, 0, paperWidthDip, paperHeightDip));
            pageCanvas.UpdateLayout();
            PrintStagingCanvas.Children.Remove(pageCanvas);
        }
        else
        {
            pageCanvas.Measure(new Size(paperWidthDip, paperHeightDip));
            pageCanvas.Arrange(new Rect(0, 0, paperWidthDip, paperHeightDip));
            pageCanvas.UpdateLayout();
        }

        return pageCanvas;
    }

    private static unsafe WriteableBitmap? CreatePrintWriteableBitmap(PdfBitmapLease lease)
    {
        if (lease.IsDisposed || lease.Width <= 0 || lease.Height <= 0 || lease.SizeInBytes <= 0)
        {
            return null;
        }

        try
        {
            WriteableBitmap bitmap = new(lease.Width, lease.Height);
            if (bitmap.PixelBuffer is IPrintBufferByteAccess byteAccess)
            {
                byte* dest = byteAccess.Buffer;
                byte* src = (byte*)lease.Scan0;
                long bytesToCopy = Math.Min(lease.SizeInBytes, (long)lease.Stride * lease.Height);
                Buffer.MemoryCopy(src, dest, bytesToCopy, bytesToCopy);
            }
            else
            {
                using Stream stream = bitmap.PixelBuffer.AsStream();
                stream.Write(lease.AsSpan());
            }

            bitmap.Invalidate();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void PlaySearchBarOpenAnimation()
    {
        if (FloatingSearchBar is null || FloatingSearchBarTransform is null)
        {
            return;
        }

        if (!_isHorizontalPushAnimating && FloatingTopTransform is not null)
        {
            FloatingTopTransform.TranslateX = GetTargetFloatingTopOffsetDip();
        }

        _searchBarStoryboard?.Stop();
        FloatingSearchBar.Opacity = 0.0;
        FloatingSearchBarTransform.TranslateY = -10.0;

        var sb = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(160));

        var opacityAnim = new DoubleAnimation { From = 0.0, To = 1.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(opacityAnim, FloatingSearchBar);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        sb.Children.Add(opacityAnim);

        var translateAnim = new DoubleAnimation { From = -10.0, To = 0.0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(translateAnim, FloatingSearchBarTransform);
        Storyboard.SetTargetProperty(translateAnim, "TranslateY");
        sb.Children.Add(translateAnim);

        sb.Completed += (_, _) =>
        {
            if (FloatingSearchBar is not null)
            {
                FloatingSearchBar.Opacity = 1.0;
            }
            if (FloatingSearchBarTransform is not null)
            {
                FloatingSearchBarTransform.TranslateY = 0.0;
            }
        };

        _searchBarStoryboard = sb;
        sb.Begin();
    }

    private float GetCurrentZoomFactor()
    {
        return (float)(ViewModel.CurrentDocument?.ZoomFactor ?? ContinuousScrollViewer.ZoomFactor);
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        _isFittedVertically = false;
        float currentZoom = GetCurrentZoomFactor();
        float newZoom = Math.Clamp(currentZoom * 1.25f, 0.25f, 8.0f);
        ApplyZoomAroundCenter(newZoom);
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        _isFittedVertically = false;
        float currentZoom = GetCurrentZoomFactor();
        float newZoom = Math.Clamp(currentZoom / 1.25f, 0.25f, 8.0f);
        ApplyZoomAroundCenter(newZoom);
    }

    private void OnZoomPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string tagStr &&
            double.TryParse(tagStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoomFactor))
        {
            _isFittedVertically = false;
            ApplyZoomAroundCenter((float)zoomFactor);
        }
    }

    private void OnFitWidthClick(object sender, RoutedEventArgs e)
    {
        ExecuteFitHorizontal();
    }

    private void OnFitPageClick(object sender, RoutedEventArgs e)
    {
        ExecuteFitVertical();
    }

    private void OnToolSelectClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetTool(AnnotationTool.Select);
    }

    private void OnToolPenClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetTool(AnnotationTool.Pen);
    }

    private void OnToolHighlighterClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetTool(AnnotationTool.Highlighter);
    }

    private void OnToolEraserClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetTool(AnnotationTool.Eraser);
    }

    private void OnToolTextNoteClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetTool(AnnotationTool.TextNote);
    }

    private void OnDocumentDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = ResourceStrings.Get(ResourceKeys.OpenFile_DragCaption);
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            e.Handled = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void OnDocumentDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var pdfFile = items.OfType<StorageFile>()
                .FirstOrDefault(f => string.Equals(f.FileType, ".pdf", StringComparison.OrdinalIgnoreCase));

            if (pdfFile is not null)
            {
                e.Handled = true;
                await ViewModel.OpenFilePathAsync(pdfFile.Path);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open dropped PDF file: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnStatusBarPageBoxGotFocus(object sender, RoutedEventArgs e)
    {
        _isPageBoxEditing = true;
        StatusBarPageBox.SelectAll();
    }

    private void OnStatusBarPageBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _isPageBoxEditing = false;
            if (int.TryParse(StatusBarPageBox.Text.Trim(), out int pageNumber) && ViewModel.CurrentDocument is not null)
            {
                int targetIndex = Math.Clamp(pageNumber - 1, 0, ViewModel.CurrentDocument.PageCount - 1);
                ViewModel.CurrentDocument.GoToPage(targetIndex);
                ScrollToPage(targetIndex);
                StatusBarPageBox.Text = (targetIndex + 1).ToString();
            }
            else if (ViewModel.CurrentDocument is not null)
            {
                StatusBarPageBox.Text = ViewModel.CurrentDocument.CurrentPageNumber.ToString();
            }

            ReturnFocusToViewer();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            _isPageBoxEditing = false;
            if (ViewModel.CurrentDocument is not null)
            {
                StatusBarPageBox.Text = ViewModel.CurrentDocument.CurrentPageNumber.ToString();
            }

            ReturnFocusToViewer();
        }
    }

    private void OnStatusBarPageBoxLostFocus(object sender, RoutedEventArgs e)
    {
        _isPageBoxEditing = false;
        if (ViewModel.CurrentDocument is not null)
        {
            StatusBarPageBox.Text = ViewModel.CurrentDocument.CurrentPageNumber.ToString();
        }
    }

    private void ReturnFocusToViewer()
    {
        ContinuousScrollViewer.Focus(FocusState.Programmatic);
    }

    // =========================================================================
    // Native Browser-Style Middle-Click Free Autoscroll
    // =========================================================================

    private void OnViewerAutoScrollPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is null)
        {
            return;
        }

        ViewModel.CurrentDocument.NotifyUserDocumentScrollInitiated();

        var point = e.GetCurrentPoint(ScrollViewerContentRoot);
        var props = point.Properties;

        if (_isAutoScrolling)
        {
            StopAutoScroll();
            e.Handled = true;
            return;
        }

        if (point.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && props.IsMiddleButtonPressed)
        {
            StartAutoScroll(point.Position, e.Pointer);
            e.Handled = true;
        }
    }

    private void OnViewerAutoScrollPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isAutoScrolling)
        {
            return;
        }

        _autoScrollCurrentPoint = e.GetCurrentPoint(ScrollViewerContentRoot).Position;
    }

    private void OnViewerAutoScrollPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isAutoScrolling || !_autoScrollMiddleButtonDown)
        {
            return;
        }

        var point = e.GetCurrentPoint(ScrollViewerContentRoot);
        var props = point.Properties;
        if (props.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased || !props.IsMiddleButtonPressed)
        {
            _autoScrollMiddleButtonDown = false;
            _autoScrollCurrentPoint = point.Position;

            double dx = _autoScrollCurrentPoint.X - _autoScrollAnchorPoint.X;
            double dy = _autoScrollCurrentPoint.Y - _autoScrollAnchorPoint.Y;
            double distance = Math.Sqrt((dx * dx) + (dy * dy));
            long elapsedMs = Environment.TickCount64 - _autoScrollStartTimestamp;

            // If user held and dragged the middle mouse button, stop on release; if it was a quick click, stay latched.
            if (distance > 10.0 || (elapsedMs > 260 && distance > 4.0))
            {
                StopAutoScroll();
            }

            e.Handled = true;
        }
    }

    private void StartAutoScroll(Point anchorPosition, Pointer pointer)
    {
        if (ViewModel.CurrentDocument is null || AutoScrollOverlayCanvas is null || AutoScrollAnchorBadge is null)
        {
            return;
        }

        _isAutoScrolling = true;
        _autoScrollMiddleButtonDown = true;
        _autoScrollStartTimestamp = Environment.TickCount64;
        _lastAutoScrollRenderTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastAutoScrollPageFlipTicks = _lastAutoScrollRenderTicks;
        _autoScrollAnchorPoint = anchorPosition;
        _autoScrollCurrentPoint = anchorPosition;
        _autoScrollAccumulatedX = ContinuousScrollViewer.HorizontalOffset;
        _autoScrollAccumulatedY = ContinuousScrollViewer.VerticalOffset;

        Canvas.SetLeft(AutoScrollAnchorBadge, anchorPosition.X - 14.0);
        Canvas.SetTop(AutoScrollAnchorBadge, anchorPosition.Y - 14.0);
        AutoScrollOverlayCanvas.Visibility = Visibility.Visible;

        try
        {
            ScrollViewerContentRoot.CapturePointer(pointer);
        }
        catch
        {
        }

        if (!_isAutoScrollRenderingSubscribed)
        {
            CompositionTarget.Rendering += OnAutoScrollRendering;
            _isAutoScrollRenderingSubscribed = true;
        }
    }

    private void StopAutoScroll()
    {
        if (_isAutoScrollRenderingSubscribed)
        {
            CompositionTarget.Rendering -= OnAutoScrollRendering;
            _isAutoScrollRenderingSubscribed = false;
        }

        if (!_isAutoScrolling)
        {
            return;
        }

        _isAutoScrolling = false;
        _autoScrollMiddleButtonDown = false;

        if (AutoScrollOverlayCanvas is not null)
        {
            AutoScrollOverlayCanvas.Visibility = Visibility.Collapsed;
        }

        try
        {
            ScrollViewerContentRoot?.ReleasePointerCaptures();
        }
        catch
        {
        }

        ScheduleViewportUpdate();
    }

    private void OnAutoScrollRendering(object? sender, object e)
    {
        if (!_isAutoScrolling || ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            StopAutoScroll();
            return;
        }

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = (double)(nowTicks - _lastAutoScrollRenderTicks) / System.Diagnostics.Stopwatch.Frequency;
        _lastAutoScrollRenderTicks = nowTicks;
        dt = Math.Clamp(dt, 0.001, 0.05);

        double dx = _autoScrollCurrentPoint.X - _autoScrollAnchorPoint.X;
        double dy = _autoScrollCurrentPoint.Y - _autoScrollAnchorPoint.Y;

        const double DeadZoneDip = 8.0;
        double absX = Math.Abs(dx);
        double absY = Math.Abs(dy);

        if (absX <= DeadZoneDip && absY <= DeadZoneDip)
        {
            _autoScrollAccumulatedX = ContinuousScrollViewer.HorizontalOffset;
            _autoScrollAccumulatedY = ContinuousScrollViewer.VerticalOffset;
            return;
        }

        // Smooth browser-style exponential velocity curve (DIPs/second)
        double velX = absX > DeadZoneDip
            ? Math.Sign(dx) * Math.Min(4200.0, Math.Pow(absX - DeadZoneDip, 1.35) * 4.2)
            : 0.0;
        double velY = absY > DeadZoneDip
            ? Math.Sign(dy) * Math.Min(4200.0, Math.Pow(absY - DeadZoneDip, 1.35) * 4.2)
            : 0.0;

        bool canScrollH = ContinuousScrollViewer.ScrollableWidth > 1.0;
        bool canScrollV = ContinuousScrollViewer.ScrollableHeight > 1.0 && (doc.IsContinuous || !_isFittedVertically);

        if (!doc.IsContinuous && !canScrollV && absY > 28.0)
        {
            double elapsedFlipSec = (double)(nowTicks - _lastAutoScrollPageFlipTicks) / System.Diagnostics.Stopwatch.Frequency;
            double flipInterval = Math.Clamp(0.45 - (Math.Min(220.0, absY - 28.0) * 0.0014), 0.12, 0.45);
            if (elapsedFlipSec >= flipInterval)
            {
                _lastAutoScrollPageFlipTicks = nowTicks;
                FlipPagedSlide(forward: dy > 0);
            }
            return;
        }

        double? targetH = null;
        double? targetV = null;

        if (canScrollH && Math.Abs(velX) > 0.01)
        {
            if (Math.Abs(_autoScrollAccumulatedX - ContinuousScrollViewer.HorizontalOffset) > 48.0)
            {
                _autoScrollAccumulatedX = ContinuousScrollViewer.HorizontalOffset;
            }

            _autoScrollAccumulatedX = Math.Clamp(
                _autoScrollAccumulatedX + (velX * dt),
                0.0,
                ContinuousScrollViewer.ScrollableWidth);
            targetH = _autoScrollAccumulatedX;
        }
        else
        {
            _autoScrollAccumulatedX = ContinuousScrollViewer.HorizontalOffset;
        }

        if (canScrollV && Math.Abs(velY) > 0.01)
        {
            if (ContinuousScrollViewer.VerticalScrollMode != ScrollMode.Enabled)
            {
                ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
            }

            if (Math.Abs(_autoScrollAccumulatedY - ContinuousScrollViewer.VerticalOffset) > 48.0)
            {
                _autoScrollAccumulatedY = ContinuousScrollViewer.VerticalOffset;
            }

            _autoScrollAccumulatedY = Math.Clamp(
                _autoScrollAccumulatedY + (velY * dt),
                0.0,
                ContinuousScrollViewer.ScrollableHeight);
            targetV = _autoScrollAccumulatedY;
        }
        else
        {
            _autoScrollAccumulatedY = ContinuousScrollViewer.VerticalOffset;
        }

        if (targetH.HasValue || targetV.HasValue)
        {
            ContinuousScrollViewer.ChangeView(targetH, targetV, null, disableAnimation: true);
            if (targetH.HasValue)
            {
                doc.LastHorizontalOffset = targetH.Value;
            }

            if (targetV.HasValue)
            {
                doc.LastVerticalOffset = targetV.Value;
            }

            if (doc.IsContinuous)
            {
                UpdatePageProgressFromScroll();
            }

            ScheduleViewportUpdate();
        }
    }

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

        double zoom = ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor;
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

        double zoom = ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor;
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

        // When zoomed horizontally in Paged / Presentation mode, allow starting a slide swipe only when already at the horizontal scroll edge
        if (swipingRight)
        {
            return ContinuousScrollViewer.HorizontalOffset <= 4.0;
        }

        return ContinuousScrollViewer.HorizontalOffset >= ContinuousScrollViewer.ScrollableWidth - 4.0;
    }

    private void EnsureScrollModesEnabledForViewChange()
    {
        if (ContinuousScrollViewer is null)
        {
            return;
        }

        if (ContinuousScrollViewer.HorizontalScrollMode != ScrollMode.Enabled)
        {
            ContinuousScrollViewer.HorizontalScrollMode = ScrollMode.Enabled;
        }

        if (ContinuousScrollViewer.VerticalScrollMode != ScrollMode.Enabled)
        {
            ContinuousScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
        }

        if (ContinuousScrollViewer.ZoomMode != ZoomMode.Enabled)
        {
            ContinuousScrollViewer.ZoomMode = ZoomMode.Enabled;
        }
    }

    private void SyncPagedScrollViewerModes()
    {
        if (ContinuousScrollViewer is null || ScrollViewerContentRoot is null)
        {
            return;
        }

        if (_activeTouchSelectingCanvas is { IsTouchSelecting: true } || _isDraggingSelectionHandle)
        {
            return;
        }

        // Always keep ScrollViewer ScrollModes and ZoomMode Enabled so WinUI 3 ScrollContentPresenter
        // always divides its arrange slot by ZoomFactor (preserving pixel-perfect centering at all zoom levels)
        // and native DirectManipulation pinch-to-zoom and continuous touch scrolling work at 120Hz.
        EnsureScrollModesEnabledForViewChange();

        bool isPres = ViewModel?.IsPresentationMode == true;
        ScrollBarVisibility targetVBar = isPres ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Visible;
        ScrollBarVisibility targetHBar = isPres ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;

        if (ContinuousScrollViewer.VerticalScrollBarVisibility != targetVBar)
        {
            ContinuousScrollViewer.VerticalScrollBarVisibility = targetVBar;
        }

        if (ContinuousScrollViewer.HorizontalScrollBarVisibility != targetHBar)
        {
            ContinuousScrollViewer.HorizontalScrollBarVisibility = targetHBar;
        }

        bool isPagedOrPres = ViewModel?.CurrentDocument is { Pages.Count: > 0 } doc &&
                             (!doc.IsContinuous || ViewModel.IsPresentationMode);
        if (isPagedOrPres && !_isApplyingFitZoom && !_isApplyingZoom &&
            ContinuousScrollViewer.ScrollableWidth <= 4.0 && ContinuousScrollViewer.ScrollableHeight <= 4.0 &&
            isPres)
        {
            _isFittedVertically = true;
        }

        bool useCustomSwipeManip = isPagedOrPres;
        ApplyManipulationModeToViewerContent(useCustomSwipeManip);
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

        foreach (PdfPageCanvas canvas in _activeCanvases.ToArray())
        {
            canvas.SyncTouchManipulationMode(useCustomSwipeManip);
        }

        SyncCanvasesManipulationInTree(ContinuousPagesRepeater, useCustomSwipeManip);
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

    private void StartOrUpdateSmoothWheelZoom(Point pivotInViewport, int wheelDelta)
    {
        if (ViewModel?.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        _isTouchToggleZoomAnimation = false;
        _restoreFitAfterSmoothZoom = false;
        double currentZoom = ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor;
        if (!_isSmoothZoomRenderingSubscribed || _smoothZoomTargetFactor <= 0.05)
        {
            _smoothZoomTargetFactor = currentZoom;
        }

        double factorStep = Math.Pow(1.13, wheelDelta / 120.0);
        _smoothZoomTargetFactor = Math.Clamp(_smoothZoomTargetFactor * factorStep, DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);
        _smoothZoomPivotPoint = pivotInViewport;
        _lastSmoothZoomRenderTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        if (!_isSmoothZoomRenderingSubscribed)
        {
            CompositionTarget.Rendering += OnSmoothZoomRendering;
            _isSmoothZoomRenderingSubscribed = true;
        }
    }

    private double CalculateFitVerticalZoom(DocumentViewModel doc)
    {
        int cur = Math.Clamp(doc.CurrentPageIndex, 0, doc.Pages.Count - 1);
        var page = doc.Pages[cur];
        bool isPres = ViewModel.IsPresentationMode;
        bool isContinuous = doc.IsContinuous && !isPres;
        bool isDoublePage = doc.IsDoublePage && !isPres;

        double marginW = isPres ? 0.0 : (!isContinuous ? 32.0 : 64.0);
        double marginH = isPres ? 0.0 : (!isContinuous ? 32.0 : 48.0);

        double rawWidth = ContinuousScrollViewer.ActualWidth > 0
            ? ContinuousScrollViewer.ActualWidth
            : (_lastHandledScrollViewerSize.Width > 0 ? _lastHandledScrollViewerSize.Width : ContinuousScrollViewer.ViewportWidth);
        double vWidth = GetAvailableDocumentWidthDip(rawWidth);
        double vHeight = ContinuousScrollViewer.ActualHeight > 0
            ? ContinuousScrollViewer.ActualHeight
            : (_lastHandledScrollViewerSize.Height > 0 ? _lastHandledScrollViewerSize.Height : ContinuousScrollViewer.ViewportHeight);

        double availW = Math.Max(100.0, vWidth - marginW);
        double availH = Math.Max(100.0, vHeight - marginH);
        double targetW = isDoublePage ? (page.DisplayWidthDip * 2.0 + 16.0) : page.DisplayWidthDip;
        double targetH = page.DisplayHeightDip;
        return Math.Clamp(Math.Min(availH / targetH, availW / targetW), DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);
    }

    private void ToggleSmoothTouchZoom(Point pivotInViewport)
    {
        if (ViewModel?.CurrentDocument is not { Pages.Count: > 0 } doc || ViewModel.IsPresentationMode)
        {
            return;
        }

        double currentZoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor);
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

        _smoothZoomStartFactor = currentZoom;
        _smoothZoomCurrentFactor = currentZoom;
        _smoothZoomTargetFactor = targetZoom;
        _smoothZoomStartOffsetX = ContinuousScrollViewer.HorizontalOffset;
        _smoothZoomStartOffsetY = ContinuousScrollViewer.VerticalOffset;

        double contentX = (_smoothZoomStartOffsetX + pivotX) / currentZoom;
        double contentY = (_smoothZoomStartOffsetY + pivotY) / currentZoom;

        if (_restoreFitAfterSmoothZoom && !doc.IsContinuous)
        {
            _smoothZoomTargetOffsetX = 0.0;
            _smoothZoomTargetOffsetY = 0.0;
        }
        else if (targetZoom < currentZoom)
        {
            double unzoomedW = Math.Max(vw / currentZoom, ContinuousScrollViewer.ExtentWidth / currentZoom);
            double unzoomedH = Math.Max(vh / currentZoom, ContinuousScrollViewer.ExtentHeight / currentZoom);
            double maxTargetScrollX = Math.Max(0.0, (unzoomedW * targetZoom) - vw);
            double maxTargetScrollY = Math.Max(0.0, (unzoomedH * targetZoom) - vh);
            _smoothZoomTargetOffsetX = Math.Clamp((contentX * targetZoom) - pivotX, 0.0, maxTargetScrollX);
            _smoothZoomTargetOffsetY = Math.Clamp((contentY * targetZoom) - pivotY, 0.0, maxTargetScrollY);
        }
        else
        {
            _smoothZoomTargetOffsetX = Math.Max(0.0, (contentX * targetZoom) - pivotX);
            _smoothZoomTargetOffsetY = Math.Max(0.0, (contentY * targetZoom) - pivotY);
        }

        _isTouchToggleZoomAnimation = true;
        _smoothZoomPivotPoint = pivotInViewport;
        _lastSmoothZoomRenderTicks = System.Diagnostics.Stopwatch.GetTimestamp();

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

    private void StopSmoothWheelZoom()
    {
        if (_isSmoothZoomRenderingSubscribed)
        {
            CompositionTarget.Rendering -= OnSmoothZoomRendering;
            _isSmoothZoomRenderingSubscribed = false;
        }
    }

    private void OnSmoothZoomRendering(object? sender, object e)
    {
        if (ViewModel?.CurrentDocument is not { Pages.Count: > 0 } doc || _isSwitchingMode || _isRestoringFromPresentation)
        {
            StopSmoothWheelZoom();
            return;
        }

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = (double)(nowTicks - _lastSmoothZoomRenderTicks) / System.Diagnostics.Stopwatch.Frequency;
        _lastSmoothZoomRenderTicks = nowTicks;
        dt = Math.Clamp(dt, 0.002, 0.050);

        if (_isTouchToggleZoomAnimation)
        {
            double diff = _smoothZoomTargetFactor - _smoothZoomCurrentFactor;
            if (Math.Abs(diff) < 0.003 || Math.Abs(_smoothZoomTargetFactor - _smoothZoomStartFactor) < 0.001)
            {
                bool restoreFit = _restoreFitAfterSmoothZoom;
                _restoreFitAfterSmoothZoom = false;
                _isTouchToggleZoomAnimation = false;
                StopSmoothWheelZoom();

                _isApplyingZoom = true;
                try
                {
                    _isFittedVertically = restoreFit && !doc.IsContinuous;
                    EnsureScrollModesEnabledForViewChange();
                    if (!_isHorizontalPushAnimating && !_isSlideSwipeActive && !_isSlideSwipeAnimating && PagesRepeaterPushTransform is not null && _smoothZoomTargetFactor > 0.05)
                    {
                        PagesRepeaterPushTransform.TranslateX = GetTargetDocumentOffsetDip(_smoothZoomTargetFactor) / _smoothZoomTargetFactor;
                    }

                    ContinuousScrollViewer.ChangeView(_smoothZoomTargetOffsetX, _smoothZoomTargetOffsetY, (float)_smoothZoomTargetFactor, disableAnimation: true);
                    doc.LastHorizontalOffset = _smoothZoomTargetOffsetX;
                    doc.LastVerticalOffset = _smoothZoomTargetOffsetY;
                    ViewModel.SetZoom((float)_smoothZoomTargetFactor);
                    SyncPagedScrollViewerModes();
                    UpdateViewportPages(_smoothZoomTargetFactor);
                    ScheduleViewportUpdate();
                }
                finally
                {
                    _isApplyingZoom = false;
                }

                return;
            }

            double alpha = 1.0 - Math.Exp(-18.0 * dt);
            _smoothZoomCurrentFactor += diff * alpha;
            double totalSpan = _smoothZoomTargetFactor - _smoothZoomStartFactor;
            double progress = Math.Abs(totalSpan) > 0.0001
                ? Math.Clamp((_smoothZoomCurrentFactor - _smoothZoomStartFactor) / totalSpan, 0.0, 1.0)
                : 1.0;

            double nextOffsetX = _smoothZoomStartOffsetX + ((_smoothZoomTargetOffsetX - _smoothZoomStartOffsetX) * progress);
            double nextOffsetY = _smoothZoomStartOffsetY + ((_smoothZoomTargetOffsetY - _smoothZoomStartOffsetY) * progress);

            _isApplyingZoom = true;
            try
            {
                _isFittedVertically = false;
                EnsureScrollModesEnabledForViewChange();
                if (!_isHorizontalPushAnimating && !_isSlideSwipeActive && !_isSlideSwipeAnimating && PagesRepeaterPushTransform is not null && _smoothZoomCurrentFactor > 0.05)
                {
                    PagesRepeaterPushTransform.TranslateX = GetTargetDocumentOffsetDip(_smoothZoomCurrentFactor) / _smoothZoomCurrentFactor;
                }

                ContinuousScrollViewer.ChangeView(nextOffsetX, nextOffsetY, (float)_smoothZoomCurrentFactor, disableAnimation: true);
                ScheduleViewportUpdate();
            }
            finally
            {
                _isApplyingZoom = false;
            }

            return;
        }

        double currentZoom = Math.Max(0.05, ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor);
        double wheelDiff = _smoothZoomTargetFactor - currentZoom;
        if (Math.Abs(wheelDiff) < 0.002)
        {
            StopSmoothWheelZoom();
            ApplyInteractiveZoomAroundPoint(_smoothZoomPivotPoint, _smoothZoomTargetFactor / currentZoom, 0.0, 0.0);
            SyncPagedScrollViewerModes();
            return;
        }

        double wheelAlpha = 1.0 - Math.Exp(-26.0 * dt);
        double nextWheelZoom = currentZoom + (wheelDiff * wheelAlpha);
        ApplyInteractiveZoomAroundPoint(_smoothZoomPivotPoint, nextWheelZoom / currentZoom, 0.0, 0.0);
    }

    private void ApplyInteractiveZoomAroundPoint(Point viewportPoint, double zoomMultiplier, double panDx, double panDy)
    {
        if (_isApplyingZoom || ViewModel?.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        _isApplyingZoom = true;
        try
        {
            _isFittedVertically = false;
            EnsureScrollModesEnabledForViewChange();

            double oldZoom = Math.Max(0.1, ContinuousScrollViewer.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : doc.ZoomFactor);
            double newZoom = Math.Clamp(oldZoom * zoomMultiplier, DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);

            double vw = ContinuousScrollViewer.ViewportWidth > 0 ? ContinuousScrollViewer.ViewportWidth : ContinuousScrollViewer.ActualWidth;
            double vh = ContinuousScrollViewer.ViewportHeight > 0 ? ContinuousScrollViewer.ViewportHeight : ContinuousScrollViewer.ActualHeight;
            double pivotX = vw > 0 ? Math.Clamp(viewportPoint.X, 0.0, vw) : 0.0;
            double pivotY = vh > 0 ? Math.Clamp(viewportPoint.Y, 0.0, vh) : 0.0;

            double contentX = (ContinuousScrollViewer.HorizontalOffset + pivotX) / oldZoom;
            double contentY = (ContinuousScrollViewer.VerticalOffset + pivotY) / oldZoom;

            double newOffsetX = Math.Max(0.0, (contentX * newZoom) - pivotX - panDx);
            double newOffsetY = Math.Max(0.0, (contentY * newZoom) - pivotY - panDy);

            if (!_isHorizontalPushAnimating && !_isSlideSwipeActive && !_isSlideSwipeAnimating && PagesRepeaterPushTransform is not null && newZoom > 0.05)
            {
                PagesRepeaterPushTransform.TranslateX = GetTargetDocumentOffsetDip(newZoom) / newZoom;
            }

            ContinuousScrollViewer.ChangeView(newOffsetX, newOffsetY, (float)newZoom, disableAnimation: true);
            ViewModel.SetZoom((float)newZoom);
            UpdateViewportPages(newZoom);
            ScheduleViewportUpdate();
        }
        finally
        {
            _isApplyingZoom = false;
        }
    }

    private void PanPagedScrollViewerBy(double deltaX, double deltaY)
    {
        if (ViewModel?.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        bool isContinuousMode = doc.IsContinuous && !ViewModel.IsPresentationMode;
        bool canH = isContinuousMode
            ? ContinuousScrollViewer.ScrollableWidth > 1.0
            : CanPanHorizontallyInPagedMode();
        bool canV = isContinuousMode
            ? ContinuousScrollViewer.ScrollableHeight > 1.0
            : CanPanVerticallyInPagedMode();
        if (!canH && !canV)
        {
            return;
        }

        EnsureScrollModesEnabledForViewChange();
        double? targetH = null;
        double? targetV = null;

        if (canH && Math.Abs(deltaX) > 0.01 && ContinuousScrollViewer.ScrollableWidth > 0.5)
        {
            targetH = Math.Clamp(ContinuousScrollViewer.HorizontalOffset - deltaX, 0.0, ContinuousScrollViewer.ScrollableWidth);
            doc.LastHorizontalOffset = targetH.Value;
        }

        if (canV && Math.Abs(deltaY) > 0.01 && ContinuousScrollViewer.ScrollableHeight > 0.5)
        {
            targetV = Math.Clamp(ContinuousScrollViewer.VerticalOffset - deltaY, 0.0, ContinuousScrollViewer.ScrollableHeight);
            doc.LastVerticalOffset = targetV.Value;
        }

        if (targetH.HasValue || targetV.HasValue)
        {
            ContinuousScrollViewer.ChangeView(targetH, targetV, null, disableAnimation: true);
            if (isContinuousMode && targetV.HasValue)
            {
                SyncScrollMetricsFromVerticalOffset(targetV.Value);
            }

            ScheduleViewportUpdate();
        }
    }

    private double GetDisplayedRowContentWidthDip(DocumentViewModel doc, int leftPageIndex)
    {
        if (leftPageIndex < 0 || leftPageIndex >= doc.Pages.Count)
        {
            return 612.0;
        }

        double borderAdd = ViewModel.IsPresentationMode ? 0.0 : 2.0;
        double leftW = GetDisplayedPageWidthDip(doc.Pages[leftPageIndex], doc.Rotation) + borderAdd;
        bool isDouble = doc.IsDoublePage && !ViewModel.IsPresentationMode && (leftPageIndex + 1 < doc.Pages.Count);
        if (!isDouble)
        {
            return leftW;
        }

        double rightW = GetDisplayedPageWidthDip(doc.Pages[leftPageIndex + 1], doc.Rotation) + borderAdd;
        return leftW + 16.0 + rightW;
    }

    private double GetDisplayedRowContentHeightDip(DocumentViewModel doc, int leftPageIndex)
    {
        if (leftPageIndex < 0 || leftPageIndex >= doc.Pages.Count)
        {
            return 792.0;
        }

        double borderAdd = ViewModel.IsPresentationMode ? 0.0 : 2.0;
        double leftH = GetDisplayedPageHeightDip(doc.Pages[leftPageIndex], doc.Rotation) + borderAdd;
        bool isDouble = doc.IsDoublePage && !ViewModel.IsPresentationMode && (leftPageIndex + 1 < doc.Pages.Count);
        if (!isDouble)
        {
            return leftH;
        }

        double rightH = GetDisplayedPageHeightDip(doc.Pages[leftPageIndex + 1], doc.Rotation) + borderAdd;
        return Math.Max(leftH, rightH);
    }

    private void OnScrollViewerContentRootContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (_isLastPointerInputTouch || _isDraggingTouchScrollScrubber || IsDescendantOfTouchScrubber(args.OriginalSource as DependencyObject))
        {
            // Suppress the desktop right-click MenuFlyout on touch long-press so touch uses native text selection + CommandBarFlyout
            args.Handled = true;
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
        ShowTouchSelectionCopyFlyout(canvas, selection);
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
            ViewModel?.CurrentDocument?.ActiveSelection is not { HasContent: true } selection ||
            FindRealizedCanvasForPage(selection.PageIndex) is not { PageInfo: { } pageInfo } canvas)
        {
            if (TouchSelectionHandlesCanvas.Visibility != Visibility.Collapsed)
            {
                TouchSelectionHandlesCanvas.Visibility = Visibility.Collapsed;
            }

            return;
        }

        try
        {
            PageCoordinateTransform pageTransform = PageCoordinateTransform.FromPage(pageInfo, canvas.RotationAngle);
            GeneralTransform toRoot = canvas.TransformToVisual(ScrollViewerContentRoot);

            Rect firstPageRect = pageTransform.ToPageRect(selection.Rects[0]);
            Rect lastPageRect = pageTransform.ToPageRect(selection.Rects[selection.Rects.Count - 1]);

            Rect firstRootRect = toRoot.TransformBounds(firstPageRect);
            Rect lastRootRect = toRoot.TransformBounds(lastPageRect);

            double startStemH = Math.Clamp(firstRootRect.Height, 10.0, 72.0);
            double endStemH = Math.Clamp(lastRootRect.Height, 10.0, 72.0);

            TouchSelectionStartStem.Height = startStemH;
            TouchSelectionEndStem.Height = endStemH;

            Canvas.SetLeft(TouchSelectionStartHandle, firstRootRect.Left - 30.0);
            Canvas.SetTop(TouchSelectionStartHandle, firstRootRect.Bottom - startStemH);

            Canvas.SetLeft(TouchSelectionEndHandle, lastRootRect.Right - 14.0);
            Canvas.SetTop(TouchSelectionEndHandle, lastRootRect.Bottom - endStemH);

            if (TouchSelectionHandlesCanvas.Visibility != Visibility.Visible)
            {
                TouchSelectionHandlesCanvas.Visibility = Visibility.Visible;
            }
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
            ViewModel?.CurrentDocument?.ActiveSelection is not { HasContent: true } selection ||
            FindRealizedCanvasForPage(selection.PageIndex) is not { PageInfo: { } pageInfo } targetCanvas)
        {
            return;
        }

        e.Handled = true;
        _isLastPointerInputTouch = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch;
        _isTouchSelectionHandlesActive = true;
        _isDraggingSelectionHandle = true;
        _handleDragPointerId = e.Pointer.PointerId;
        _handleDragTargetCanvas = targetCanvas;
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

        PageCoordinateTransform pageTransform = PageCoordinateTransform.FromPage(pageInfo, targetCanvas.RotationAngle);
        PdfRect firstPdfRect = selection.Rects[0];
        PdfRect lastPdfRect = selection.Rects[selection.Rects.Count - 1];

        int fixedAnchorCharIndex;
        System.Numerics.Vector2 fixedAnchorPdfPt;
        Rect grabbedPageDipRect;

        if (isStartHandle)
        {
            fixedAnchorCharIndex = selection.EndCharIndex;
            fixedAnchorPdfPt = new System.Numerics.Vector2(
                (lastPdfRect.Left + lastPdfRect.Right) * 0.5f,
                (lastPdfRect.Bottom + lastPdfRect.Top) * 0.5f);
            grabbedPageDipRect = pageTransform.ToPageRect(firstPdfRect);
        }
        else
        {
            fixedAnchorCharIndex = selection.StartCharIndex;
            fixedAnchorPdfPt = new System.Numerics.Vector2(
                (firstPdfRect.Left + firstPdfRect.Right) * 0.5f,
                (firstPdfRect.Bottom + firstPdfRect.Top) * 0.5f);
            grabbedPageDipRect = pageTransform.ToPageRect(lastPdfRect);
        }

        Point grabbedCharCenterDip = new(
            isStartHandle ? (grabbedPageDipRect.Left + Math.Min(4.0, grabbedPageDipRect.Width * 0.5)) : (grabbedPageDipRect.Right - Math.Min(4.0, grabbedPageDipRect.Width * 0.5)),
            (grabbedPageDipRect.Top + grabbedPageDipRect.Bottom) * 0.5);
        Point pointerCanvasDip = e.GetCurrentPoint(targetCanvas).Position;
        _handleGrabOffsetCanvasDip = new Point(
            grabbedCharCenterDip.X - pointerCanvasDip.X,
            grabbedCharCenterDip.Y - pointerCanvasDip.Y);
        _lastHandleTargetCanvasDip = grabbedCharCenterDip;

        targetCanvas.BeginHandleSelectionDrag(fixedAnchorCharIndex, fixedAnchorPdfPt);
    }

    private void OnTouchSelectionHandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSelectionHandle || e.Pointer.PointerId != _handleDragPointerId || _handleDragTargetCanvas is null)
        {
            return;
        }

        e.Handled = true;
        Point pointerCanvasDip = e.GetCurrentPoint(_handleDragTargetCanvas).Position;
        Point targetCharDip = new(
            pointerCanvasDip.X + _handleGrabOffsetCanvasDip.X,
            pointerCanvasDip.Y + _handleGrabOffsetCanvasDip.Y);
        _lastHandleTargetCanvasDip = targetCharDip;
        _handleDragTargetCanvas.UpdateHandleSelectionDrag(targetCharDip);
    }

    private void OnTouchSelectionHandlePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSelectionHandle || e.Pointer.PointerId != _handleDragPointerId)
        {
            return;
        }

        e.Handled = true;
        if (_handleDragTargetCanvas is not null)
        {
            Point pointerCanvasDip = e.GetCurrentPoint(_handleDragTargetCanvas).Position;
            _lastHandleTargetCanvasDip = new Point(
                pointerCanvasDip.X + _handleGrabOffsetCanvasDip.X,
                pointerCanvasDip.Y + _handleGrabOffsetCanvasDip.Y);
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

        PdfPageCanvas? targetCanvas = _handleDragTargetCanvas;
        UIElement? handleElem = _handleDragElement;
        Point finalTargetDip = _lastHandleTargetCanvasDip;

        _isDraggingSelectionHandle = false;
        _handleDragTargetCanvas = null;
        _handleDragElement = null;

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

        if (targetCanvas is not null)
        {
            targetCanvas.EndHandleSelectionDrag(finalTargetDip);
        }
    }

    private void ShowTouchSelectionCopyFlyout(PdfPageCanvas canvas, PdfTextSelection selection)
    {
        if (TouchSelectionCommandBarFlyout is null ||
            ScrollViewerContentRoot is null ||
            !selection.HasContent ||
            canvas.PageInfo is not { } pageInfo ||
            ViewModel?.IsPresentationMode == true)
        {
            return;
        }

        try
        {
            PageCoordinateTransform pageTransform = PageCoordinateTransform.FromPage(pageInfo, canvas.RotationAngle);
            GeneralTransform toRoot = canvas.TransformToVisual(ScrollViewerContentRoot);

            double rootW = ScrollViewerContentRoot.ActualWidth;
            double rootH = ScrollViewerContentRoot.ActualHeight;
            if (rootW <= 0 || rootH <= 0)
            {
                return;
            }

            double minX = double.MaxValue;
            double maxX = double.MinValue;
            double minY = double.MaxValue;
            double maxY = double.MinValue;
            bool hasVisibleRect = false;

            for (int i = 0; i < selection.Rects.Count; i++)
            {
                Rect pageDipRect = pageTransform.ToPageRect(selection.Rects[i]);
                Rect rootRect = toRoot.TransformBounds(pageDipRect);
                if (rootRect.Right < 0 || rootRect.Left > rootW || rootRect.Bottom < 0 || rootRect.Top > rootH)
                {
                    continue;
                }

                hasVisibleRect = true;
                minX = Math.Min(minX, rootRect.Left);
                maxX = Math.Max(maxX, rootRect.Right);
                minY = Math.Min(minY, rootRect.Top);
                maxY = Math.Max(maxY, rootRect.Bottom);
            }

            if (!hasVisibleRect)
            {
                for (int i = 0; i < selection.Rects.Count; i++)
                {
                    Rect pageDipRect = pageTransform.ToPageRect(selection.Rects[i]);
                    Rect rootRect = toRoot.TransformBounds(pageDipRect);
                    minX = Math.Min(minX, rootRect.Left);
                    maxX = Math.Max(maxX, rootRect.Right);
                    minY = Math.Min(minY, rootRect.Top);
                    maxY = Math.Max(maxY, rootRect.Bottom);
                }
            }

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
        foreach (PdfPageCanvas canvas in _activeCanvases.ToArray())
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
            if (!stillDown || (nowTick - kvp.Value.PressTick > 800 &&
                (_activeTouchSelectingCanvas is null || !_activeTouchSelectingCanvas.IsTouchSelecting)))
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

    private void StopTwoFingerTapPolling()
    {
        if (_isTwoFingerTapPollingSubscribed)
        {
            CompositionTarget.Rendering -= OnTwoFingerTapPollingRendering;
            _isTwoFingerTapPollingSubscribed = false;
        }
    }

    private void OnTwoFingerTapPollingRendering(object? sender, object e)
    {
        StopTwoFingerTapPolling();
    }

    private void OnSlideSwipePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingSelectionHandle || _isDraggingTouchScrollScrubber || IsDescendantOfTouchSelectionHandle(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var pt = e.GetCurrentPoint(ScrollViewerContentRoot);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            _isLastPointerInputTouch = false;
            _isMouseButtonDown = pt.Properties.IsLeftButtonPressed || pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed;
            _lastMousePointerPosition = pt.Position;
            _hasLastMousePointerPosition = true;
            if (pt.Properties.IsLeftButtonPressed && ViewModel.CurrentDocument?.ActiveSelection is { HasContent: true })
            {
                ViewModel.CurrentDocument.ClearSelection();
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
            return;
        }

        long nowTick = Environment.TickCount64;
        _isLastPointerInputTouch = true;
        _activeTouchpadFingerCount = 0;
        _isMouseButtonDown = false;
        PruneStaleTouchPointers(nowTick);
        _activeTouchPointers[e.Pointer.PointerId] = (nowTick, pt.Position);

        // Immediately clear any existing text selection when touching anywhere outside the selection handles
        if (ViewModel.CurrentDocument?.ActiveSelection is { HasContent: true })
        {
            ViewModel.CurrentDocument.ClearSelection();
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
                CancelInteractiveSlideSwipeImmediate();
            }

            StopTwoFingerTapPolling();
            EnsureScrollModesEnabledForViewChange();
            return;
        }

        StopTwoFingerTapPolling();
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
        if (_isDraggingSelectionHandle || _isDraggingTouchScrollScrubber || IsDescendantOfTouchScrubber(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            if (_isLastPointerInputTouch)
            {
                _isLastPointerInputTouch = false;
                if (_isTouchScrollScrubberVisible && !_isDraggingTouchScrollScrubber)
                {
                    HideTouchScrollScrubberImmediate();
                }
            }
            var mousePt = e.GetCurrentPoint(ScrollViewerContentRoot);
            _isMouseButtonDown = mousePt.Properties.IsLeftButtonPressed || mousePt.Properties.IsRightButtonPressed || mousePt.Properties.IsMiddleButtonPressed;
            Point curMousePos = mousePt.Position;
            if (_hasLastMousePointerPosition)
            {
                double mdx = curMousePos.X - _lastMousePointerPosition.X;
                double mdy = curMousePos.Y - _lastMousePointerPosition.Y;
                if (((mdx * mdx) + (mdy * mdy)) > 3.0 &&
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

        if (((totalDx * totalDx) + (totalDy * totalDy)) > 64.0)
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
        if (_isDraggingSelectionHandle || _isDraggingTouchScrollScrubber || IsDescendantOfTouchSelectionHandle(e.OriginalSource as DependencyObject))
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
            if (_activeTouchSelectingCanvas is { IsTouchSelecting: true })
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
        if (_isDraggingSelectionHandle || _isDraggingTouchScrollScrubber)
        {
            e.Mode = ManipulationModes.None;
            return;
        }

        // In Continuous Mode (when not actively dragging a touch text selection), leave e.Mode untouched
        // as ManipulationModes.System so ContinuousScrollViewer DirectManipulation handles 100% of vertical
        // scrolling, 2D zoomed panning, and pinch-to-zoom natively on the compositor thread.
        if (!CanUseInteractiveSlideSwipe() && _activeTouchSelectingCanvas is null)
        {
            return;
        }

        if (ScrollViewerContentRoot is not null)
        {
            e.Container = ScrollViewerContentRoot;
            bool allowPanInertia = CanPanHorizontallyInPagedMode() || CanPanVerticallyInPagedMode();
            e.Mode = allowPanInertia && _activeTouchSelectingCanvas is null
                ? (PagedCustomManipulationMode | ManipulationModes.TranslateInertia)
                : PagedCustomManipulationMode;
        }
    }

    private void OnSlideSwipeManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        if (_isMouseButtonDown || _isDraggingSelectionHandle || _isDraggingTouchScrollScrubber)
        {
            return;
        }

        if (!CanUseInteractiveSlideSwipe() && _activeTouchSelectingCanvas is null)
        {
            return;
        }

        PruneStaleTouchPointers(Environment.TickCount64);

        if (_isSlideSwipeAnimating)
        {
            CompleteSlideSwipeAnimationImmediate();
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
                CancelInteractiveSlideSwipeImmediate();
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
        if (!_isManipulatingSlideSwipe || _isDraggingSelectionHandle || _isDraggingTouchScrollScrubber)
        {
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
        if (_activeTouchPointers.Count > 1 || Math.Abs(scaleDelta - 1.0) > 0.002)
        {
            CancelTouchHoldOnAllCanvases();
            _touchGestureAxis = PagedTouchGestureAxis.PanOrZoom;
            if (_isSlideSwipeActive)
            {
                CancelInteractiveSlideSwipeImmediate();
            }

            if (Math.Abs(scaleDelta - 1.0) > 0.001)
            {
                ApplyInteractiveZoomAroundPoint(e.Position, scaleDelta, deltaX, deltaY);
            }
            else
            {
                PanPagedScrollViewerBy(deltaX, deltaY);
            }

            return;
        }

        _manipCumulativeDx += deltaX;
        _manipCumulativeDy += deltaY;
        double totalDistSq = (_manipCumulativeDx * _manipCumulativeDx) + (_manipCumulativeDy * _manipCumulativeDy);

        if (!CanUseInteractiveSlideSwipe())
        {
            return;
        }

        if (totalDistSq > 36.0)
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
                    Math.Abs(_manipCumulativeDx) >= 14.0 &&
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
                else if (totalDistSq >= 16.0)
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
            else if (Math.Abs(_manipCumulativeDx) >= 4.0 &&
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
                if (Math.Abs(_touchpadTargetDeltaX) >= 24.0 || (nowTick - _lastTouchpadHorizontalPacketTick) > 65)
                {
                    // Finger drag distance is already established (or user held the slide still and then lifted):
                    // release and animate immediately on the exact frame the fingers leave the touchpad!
                    _touchpadSwipeEndTimer.Stop();
                    StopTouchpadSwipeSmoothingLoop();
                    if (Math.Abs(_touchpadTargetDeltaX) > Math.Abs(_swipeRawDeltaX))
                    {
                        UpdateInteractiveSlideSwipeDelta(_touchpadTargetDeltaX);
                    }

                    CompleteInteractiveSlideSwipe(fromTouchpad: true);
                }
                else
                {
                    // Ultra-fast micro-flick where < 24 DIPs arrived before finger lift: wait 28ms once for in-flight flick packets
                    _touchpadSwipeEndTimer.Stop();
                    _touchpadSwipeEndTimer.Interval = TimeSpan.FromMilliseconds(28);
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

                _slideSwipeStoryboard?.Stop();
                _slideSwipeStoryboard = null;
                _isSlideSwipeAnimating = false;
                _isSlideSwipeActive = true;
                _isTouchpadWheelSwipeActive = true;
                _swipeRawDeltaX = inFlightScreenDeltaX;
                _swipeVisualDeltaX = inFlightScreenDeltaX;
                _touchpadTargetDeltaX = inFlightScreenDeltaX;
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
                // Fingers already lifted during an ultra-fast flick: start a single 32ms completion timer without resetting it on inertia
                _touchpadSwipeEndTimer.Interval = TimeSpan.FromMilliseconds(32);
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
        if (!_isTouchpadWheelSwipeActive || !_isSlideSwipeActive || _isSlideSwipeAnimating)
        {
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
        if (Math.Abs(diff) <= 0.25)
        {
            UpdateInteractiveSlideSwipeDelta(_touchpadTargetDeltaX);
            if (Math.Abs(_swipeVelocityX) < 5.0)
            {
                // Unsubscribe while fingers are held completely motionless so idle CPU is 0.0%
                StopTouchpadSwipeSmoothingLoop();
            }

            return;
        }

        // High-response VSync exponential smoothing (eliminates quantized WM_MOUSEHWHEEL stepping while tracking fingers tightly)
        double alpha = 1.0 - Math.Exp(-42.0 * dt);
        double nextRawX = _swipeRawDeltaX + (diff * alpha);
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
        StopTouchpadSwipeSmoothingLoop();
        if (_isTouchpadWheelSwipeActive && _isSlideSwipeActive && !_isSlideSwipeAnimating)
        {
            // Ensure final target delta is reflected before evaluating commit vs bounce
            if (Math.Abs(_touchpadTargetDeltaX) > Math.Abs(_swipeRawDeltaX))
            {
                UpdateInteractiveSlideSwipeDelta(_touchpadTargetDeltaX);
            }

            CompleteInteractiveSlideSwipe(fromTouchpad: true);
        }
    }

    private void BeginInteractiveSlideSwipe()
    {
        _slideSwipeStoryboard?.Stop();
        _slideSwipeStoryboard = null;
        _horizontalPushStoryboard?.Stop();
        _isHorizontalPushAnimating = false;

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
        }

        double rawViewerW = ContinuousScrollViewer.ActualWidth > 0 ? ContinuousScrollViewer.ActualWidth : ContinuousScrollViewer.ViewportWidth;
        double availW = Math.Max(200.0, GetAvailableDocumentWidthDip(rawViewerW));

        double absDx = Math.Abs(_swipeRawDeltaX);
        double absVx = Math.Abs(_swipeVelocityX);
        bool sameVelocityDirection = Math.Abs(_swipeVelocityX) < 40.0 || (Math.Sign(_swipeVelocityX) == Math.Sign(_swipeRawDeltaX));

        bool distanceCommitted = absDx >= (availW * SlideSwipeMinCommitRatio);
        bool flickCommitted = absVx >= SlideSwipeMinFlickVelocityDipPerSec &&
                              absDx >= SlideSwipeMinFlickDistanceDip &&
                              sameVelocityDirection;

        // If the user dragged in one direction and then reversed direction strongly at the end, bounce back
        bool reversedAtEnd = absVx >= 240.0 && (Math.Sign(_swipeVelocityX) != Math.Sign(_swipeRawDeltaX));

        bool shouldCommit = _swipeTargetPageIndex >= 0 &&
                            _swipeTargetPageIndex < doc.Pages.Count &&
                            !reversedAtEnd &&
                            (distanceCommitted || flickCommitted);

        StopTouchpadSwipeSmoothingLoop();

        if (fromTouchpad)
        {
            _touchpadSwipeCompletedSinceLastTwoFingerDown = true;
            _isDrainingTouchpadInertia = true;
            _committedTouchpadWheelSign = _swipeRawDeltaX < 0 ? 1 : -1;
            _lastTouchpadInertiaDrainTick = nowTick;
            _touchpadInertiaDrainStartTick = nowTick;
            _touchpadFreshSurgeCount = 0;
        }

        AnimateSlideSwipeToCompletion(shouldCommit, _swipeTargetPageIndex);
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
                      (Math.Abs(_swipeRawDeltaX) >= 32.0);
        int targetPage = _swipeTargetPageIndex;

        _slideSwipeStoryboard?.Stop();
        _slideSwipeStoryboard = null;
        FinalizeSlideSwipeAnimation(commit, targetPage);
    }

    private void FinalizeSlideSwipeAnimation(bool committed, int targetPageIndex)
    {
        Storyboard? completedSb = _slideSwipeStoryboard;
        _slideSwipeStoryboard = null;
        completedSb?.Stop();
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

    private void CancelInteractiveSlideSwipeImmediate()
    {
        StopSmoothWheelZoom();
        StopTouchpadSwipeSmoothingLoop();
        _touchpadSwipeEndTimer.Stop();
        _slideSwipeStoryboard?.Stop();
        _slideSwipeStoryboard = null;
        _isSlideSwipeAnimating = false;
        _isSlideSwipeAnimatingCommit = false;
        _isSlideSwipeActive = false;
        _touchpadTargetDeltaX = 0.0;
        _isManipulatingSlideSwipe = false;
        _isTouchSwipeTracking = false;
        _isTouchSwipeEngaged = false;
        _isTouchpadWheelSwipeActive = false;
        _touchGestureAxis = PagedTouchGestureAxis.Undecided;
        _manipCumulativeDx = 0.0;
        _manipCumulativeDy = 0.0;
        _swipeRawDeltaX = 0.0;
        _swipeVisualDeltaX = 0.0;
        _swipeVelocityX = 0.0;
        _swipeTargetPageIndex = -1;

        double curZoom = Math.Max(0.05, ContinuousScrollViewer?.ZoomFactor > 0.05 ? ContinuousScrollViewer.ZoomFactor : (ViewModel?.CurrentDocument?.ZoomFactor ?? 1.0));
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

    // =========================================================================
    // Compact Chrome (< 750 DIPs) Status Bar "Swallowed" Animation
    // =========================================================================

    private void ApplyStatusBarCompactStateImmediate(bool compact)
    {
        _statusBarCompactStoryboard?.Stop();

        if (StatusBarMetadataClipContainer is not null &&
            StatusBarMetadataInnerPanel is not null &&
            StatusBarMetadataTransform is not null &&
            StatusBarMetadataClip is not null)
        {
            if (compact)
            {
                if (StatusBarMetadataInnerPanel.ActualWidth > 1.0)
                {
                    _statusBarMetadataExpandedWidth = StatusBarMetadataInnerPanel.ActualWidth;
                }

                StatusBarMetadataClipContainer.Width = 0.0;
                StatusBarMetadataClipContainer.Opacity = 0.0;
                StatusBarMetadataClipContainer.Visibility = Visibility.Collapsed;
                StatusBarMetadataTransform.TranslateX = -_statusBarMetadataExpandedWidth;
            }
            else
            {
                StatusBarMetadataClipContainer.Visibility = Visibility.Visible;
                StatusBarMetadataClipContainer.Width = double.NaN;
                StatusBarMetadataClipContainer.Opacity = 1.0;
                StatusBarMetadataTransform.TranslateX = 0.0;
                StatusBarMetadataClip.Rect = new Rect(0, 0, 400, 32);
            }
        }

        if (StatusBarPresentationClipContainer is not null &&
            StatusBarPresentationInnerPanel is not null &&
            StatusBarPresentationTransform is not null &&
            StatusBarPresentationClip is not null)
        {
            if (compact)
            {
                if (StatusBarPresentationInnerPanel.ActualWidth > 1.0)
                {
                    _statusBarPresentationExpandedWidth = StatusBarPresentationInnerPanel.ActualWidth;
                }

                StatusBarPresentationClipContainer.Width = 0.0;
                StatusBarPresentationClipContainer.Opacity = 0.0;
                StatusBarPresentationClipContainer.Visibility = Visibility.Collapsed;
                StatusBarPresentationTransform.TranslateX = -_statusBarPresentationExpandedWidth;
            }
            else
            {
                StatusBarPresentationClipContainer.Visibility = Visibility.Visible;
                StatusBarPresentationClipContainer.Width = double.NaN;
                StatusBarPresentationClipContainer.Opacity = 1.0;
                StatusBarPresentationTransform.TranslateX = 0.0;
                StatusBarPresentationClip.Rect = new Rect(0, 0, 200, 32);
            }
        }
    }

    private void AnimateStatusBarCompactMode(bool collapse)
    {
        if (StatusBarMetadataClipContainer is null ||
            StatusBarMetadataInnerPanel is null ||
            StatusBarMetadataTransform is null ||
            StatusBarMetadataClip is null ||
            StatusBarPresentationClipContainer is null ||
            StatusBarPresentationInnerPanel is null ||
            StatusBarPresentationTransform is null ||
            StatusBarPresentationClip is null)
        {
            return;
        }

        _statusBarCompactStoryboard?.Stop();

        if (!collapse)
        {
            StatusBarMetadataClipContainer.Visibility = Visibility.Visible;
            StatusBarPresentationClipContainer.Visibility = Visibility.Visible;
        }

        StatusBarMetadataInnerPanel.Measure(new Size(double.PositiveInfinity, 32));
        if (StatusBarMetadataInnerPanel.DesiredSize.Width > 1.0)
        {
            _statusBarMetadataExpandedWidth = StatusBarMetadataInnerPanel.DesiredSize.Width;
        }
        else if (StatusBarMetadataInnerPanel.ActualWidth > 1.0)
        {
            _statusBarMetadataExpandedWidth = StatusBarMetadataInnerPanel.ActualWidth;
        }

        StatusBarPresentationInnerPanel.Measure(new Size(double.PositiveInfinity, 32));
        if (StatusBarPresentationInnerPanel.DesiredSize.Width > 1.0)
        {
            _statusBarPresentationExpandedWidth = StatusBarPresentationInnerPanel.DesiredSize.Width;
        }
        else if (StatusBarPresentationInnerPanel.ActualWidth > 1.0)
        {
            _statusBarPresentationExpandedWidth = StatusBarPresentationInnerPanel.ActualWidth;
        }

        StatusBarMetadataClip.Rect = new Rect(0, 0, Math.Max(400.0, _statusBarMetadataExpandedWidth + 32.0), 32);
        StatusBarPresentationClip.Rect = new Rect(0, 0, Math.Max(200.0, _statusBarPresentationExpandedWidth + 32.0), 32);

        double fromMetaW = collapse
            ? (StatusBarMetadataClipContainer.ActualWidth > 0.5 ? StatusBarMetadataClipContainer.ActualWidth : _statusBarMetadataExpandedWidth)
            : (double.IsNaN(StatusBarMetadataClipContainer.Width) ? 0.0 : StatusBarMetadataClipContainer.Width);
        double toMetaW = collapse ? 0.0 : _statusBarMetadataExpandedWidth;

        double fromPresW = collapse
            ? (StatusBarPresentationClipContainer.ActualWidth > 0.5 ? StatusBarPresentationClipContainer.ActualWidth : _statusBarPresentationExpandedWidth)
            : (double.IsNaN(StatusBarPresentationClipContainer.Width) ? 0.0 : StatusBarPresentationClipContainer.Width);
        double toPresW = collapse ? 0.0 : _statusBarPresentationExpandedWidth;

        var sb = new Storyboard();
        var ease = new ExponentialEase { Exponent = 4.5, EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(220));

        var metaWidthAnim = new DoubleAnimation
        {
            From = fromMetaW,
            To = toMetaW,
            Duration = duration,
            EasingFunction = ease,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(metaWidthAnim, StatusBarMetadataClipContainer);
        Storyboard.SetTargetProperty(metaWidthAnim, "Width");
        sb.Children.Add(metaWidthAnim);

        var metaTranslateAnim = new DoubleAnimation
        {
            From = collapse ? StatusBarMetadataTransform.TranslateX : -_statusBarMetadataExpandedWidth,
            To = collapse ? -_statusBarMetadataExpandedWidth : 0.0,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(metaTranslateAnim, StatusBarMetadataTransform);
        Storyboard.SetTargetProperty(metaTranslateAnim, "TranslateX");
        sb.Children.Add(metaTranslateAnim);

        var metaOpacityAnim = new DoubleAnimation
        {
            From = collapse ? StatusBarMetadataClipContainer.Opacity : 0.0,
            To = collapse ? 0.0 : 1.0,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(metaOpacityAnim, StatusBarMetadataClipContainer);
        Storyboard.SetTargetProperty(metaOpacityAnim, "Opacity");
        sb.Children.Add(metaOpacityAnim);

        var presWidthAnim = new DoubleAnimation
        {
            From = fromPresW,
            To = toPresW,
            Duration = duration,
            EasingFunction = ease,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(presWidthAnim, StatusBarPresentationClipContainer);
        Storyboard.SetTargetProperty(presWidthAnim, "Width");
        sb.Children.Add(presWidthAnim);

        var presTranslateAnim = new DoubleAnimation
        {
            From = collapse ? StatusBarPresentationTransform.TranslateX : -_statusBarPresentationExpandedWidth,
            To = collapse ? -_statusBarPresentationExpandedWidth : 0.0,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(presTranslateAnim, StatusBarPresentationTransform);
        Storyboard.SetTargetProperty(presTranslateAnim, "TranslateX");
        sb.Children.Add(presTranslateAnim);

        var presOpacityAnim = new DoubleAnimation
        {
            From = collapse ? StatusBarPresentationClipContainer.Opacity : 0.0,
            To = collapse ? 0.0 : 1.0,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(presOpacityAnim, StatusBarPresentationClipContainer);
        Storyboard.SetTargetProperty(presOpacityAnim, "Opacity");
        sb.Children.Add(presOpacityAnim);

        sb.Completed += (_, _) =>
        {
            if (ViewModel.IsCompactChrome)
            {
                StatusBarMetadataClipContainer.Visibility = Visibility.Collapsed;
                StatusBarPresentationClipContainer.Visibility = Visibility.Collapsed;
            }
            else
            {
                StatusBarMetadataClipContainer.Width = double.NaN;
                StatusBarMetadataClipContainer.Opacity = 1.0;
                StatusBarMetadataTransform.TranslateX = 0.0;
                StatusBarPresentationClipContainer.Width = double.NaN;
                StatusBarPresentationClipContainer.Opacity = 1.0;
                StatusBarPresentationTransform.TranslateX = 0.0;
            }
        };

        _statusBarCompactStoryboard = sb;
        sb.Begin();
    }

    private void OnStatusBarZoomSliderValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingSliderFromZoom || ViewModel.CurrentDocument is null)
        {
            return;
        }

        _isFittedVertically = false;
        double newFactor = Math.Clamp(e.NewValue / 100.0, 0.25, 5.0);
        if (Math.Abs(ViewModel.CurrentDocument.ZoomFactor - newFactor) > 0.01)
        {
            ApplyZoomAroundCenter((float)newFactor);
        }
    }

    private void UpdateZoomSlider(double zoomFactor)
    {
        if (StatusBarZoomSlider is null)
        {
            return;
        }

        _isUpdatingSliderFromZoom = true;
        try
        {
            double percentage = Math.Clamp(zoomFactor * 100.0, StatusBarZoomSlider.Minimum, StatusBarZoomSlider.Maximum);
            StatusBarZoomSlider.Value = percentage;
        }
        finally
        {
            _isUpdatingSliderFromZoom = false;
        }
    }
}