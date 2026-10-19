using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using WinPDF.Helpers;
using WinPDF.Models;
using WinPDF.ViewModels;
using WinPDF.Views;
using WinPDF.Views.Sidebar;

namespace WinPDF;

/// <summary>
/// Main application window hosting the Mica backdrop, unified single 48 DIPs TitleBar,
/// resizable sidebar with directional frame navigation, and responsive document view frame.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const double CompactBreakpoint = 750.0;
    private const double SidebarAutoCloseBreakpoint = 950.0;
    private const double SidebarMaxConstraintBreakpoint = 1200.0;
    private const double MinDocumentAreaWithSidebarDip = 720.0;
    private const double MinWindowWidthDip = 500.0;
    private const double MinWindowHeightDip = 360.0;
    private const double MinSidebarWidth = 230.0;
    private const double MaxSidebarWidth = 480.0;
    private const double DefaultSidebarWidth = 260.0;
    private const double SidebarToggleSlotWidthDip = 44.0;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint WM_DPICHANGED = 0x02E0;
    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;
    private static readonly UIntPtr WindowSubclassId = new(0x57504446);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private double _savedSidebarWidth = DefaultSidebarWidth;
    private double _effectiveSidebarWidth = DefaultSidebarWidth;
    private bool _wasSidebarClosedByNarrowWindow;
    private bool _isNarrowWindowSidebarToggle;
    private bool _suppressSidebarAnimation;
    private bool _isSidebarOpenCloseAnimating;
    private int _previousSidebarIndex = -1;
    private IntPtr _hwnd;
    private SubclassProc? _subclassDelegate;
    private IntPtr _subclassPtr;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
    private static partial IntPtr SendMessage(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(IntPtr hWnd, IntPtr pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(IntPtr hWnd, IntPtr pfnSubclass, UIntPtr uIdSubclass);

    [LibraryImport("comctl32.dll")]
    private static partial IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Gets the shell view model orchestrating window state, documents, and navigation.
    /// </summary>
    public ShellViewModel ViewModel { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        ViewModel = App.CurrentApp.Services.GetRequiredService<ShellViewModel>();

        // Custom TitleBar integration with Mica: only center drag region moves the window
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        string iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "appicon.ico");
        if (System.IO.File.Exists(iconPath))
        {
            try { AppWindow.SetIcon(iconPath); } catch { }

            try
            {
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (hwnd != IntPtr.Zero)
                {
                    const uint IMAGE_ICON = 1;
                    const uint LR_LOADFROMFILE = 0x00000010;
                    const uint WM_SETICON = 0x0080;

                    IntPtr hSmall = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                    if (hSmall != IntPtr.Zero)
                    {
                        _ = SendMessage(hwnd, WM_SETICON, UIntPtr.Zero, hSmall);
                    }

                    IntPtr hBig = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
                    if (hBig != IntPtr.Zero)
                    {
                        _ = SendMessage(hwnd, WM_SETICON, new UIntPtr(1), hBig);
                    }
                }
            }
            catch { }
        }

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }

        RootGrid.RequestedTheme = ViewModel.CurrentTheme switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        UpdateTitleBarTheme();
        _uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateTitleBarTheme);
        RootGrid.Loaded += (_, _) => UpdateTitleBarTheme();
        RootGrid.ActualThemeChanged += (_, _) => UpdateTitleBarTheme();

        // Window size and layout events
        SizeChanged += OnMainWindowSizeChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RegisterTitleBarAnimatedIconPointerHandlers();

        InstallMinSizeWindowSubclass();

        _savedSidebarWidth = Math.Clamp(ViewModel.SettingsService.Settings.SidebarWidth, MinSidebarWidth, MaxSidebarWidth);
        _effectiveSidebarWidth = _savedSidebarWidth;
        SidebarPaneContainer.Width = _effectiveSidebarWidth;
        SetupSidebarClipping();
        Closed += OnMainWindowClosed;

        SelectSidebarTab(ViewModel.SidebarTab);

        AppWindow.Changed += OnAppWindowChanged;

        // Position and size the window while it is still hidden (before Activate() is called)
        // so it never flashes at the default Win32 cascade position before centering.
        SetInitialWindowBounds();

        if (Content is FrameworkElement root)
        {
            root.Loaded += (_, _) =>
            {
                SetInitialWindowBounds();
                InstallMinSizeWindowSubclass();
                UpdateWindowMinimumSize();
                ApplyRequestedThemeFromViewModel(isStartup: true);
                UpdateTitleBarTheme();
                UpdateTitleBarInsets();
                UpdateTitleBarNonClientRegions();

                double initialWidthDip = root.ActualWidth > 0 ? root.ActualWidth : 1280.0;
                bool initialSidebarAllowed = initialWidthDip >= SidebarAutoCloseBreakpoint;
                ViewModel.IsSidebarAllowed = initialSidebarAllowed;
                _effectiveSidebarWidth = GetEffectiveSidebarWidth(initialWidthDip);
                SidebarPaneContainer.Width = _effectiveSidebarWidth;

                if (!initialSidebarAllowed && ViewModel.IsSidebarOpen)
                {
                    _wasSidebarClosedByNarrowWindow = true;
                    SetSidebarOpenFromWindowThreshold(false, animate: false);
                }

                ApplySidebarToggleStateImmediate(collapse: !ViewModel.HasDocument || !initialSidebarAllowed);

                bool initialCompact = initialWidthDip < CompactBreakpoint;
                ViewModel.IsCompactChrome = initialCompact;
                ApplyTitleBarCompactStateImmediate(initialCompact);

                UpdateSidebarLayout();
                UpdateSidebarClipGeometries();
                UpdateMainContainerStyle();

                // Initial frame navigation
                MainContentFrame.Navigate(typeof(DocumentView), ViewModel);
                if (MainContentFrame.Content is DocumentView initialDocView)
                {
                    initialDocView.SetSidebarStateImmediate(
                        ViewModel.HasDocument && ViewModel.IsSidebarOpen,
                        _effectiveSidebarWidth);
                }

                SelectSidebarTab(ViewModel.SidebarTab);

                if (root.XamlRoot is not null)
                {
                    root.XamlRoot.Changed += (_, _) =>
                    {
                        UpdateWindowMinimumSize();
                        UpdateTitleBarInsets();
                        UpdateTitleBarNonClientRegions();
                    };
                }
            };
        }
    }

    private bool _hasSetInitialBounds;

    private void SetInitialWindowBounds()
    {
        if (_hasSetInitialBounds)
        {
            return;
        }

        _hasSetInitialBounds = true;
        double scale = Content?.XamlRoot?.RasterizationScale ?? 0.0;
        if (scale <= 0.0)
        {
            try
            {
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                uint dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : 96u;
                scale = dpi > 0 ? dpi / 96.0 : 1.0;
            }
            catch
            {
                scale = 1.0;
            }
        }

        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);

        int targetWidthDip = 1280;
        int targetHeightDip = 800;

        int targetWidthPx = (int)Math.Round(targetWidthDip * scale);
        int targetHeightPx = (int)Math.Round(targetHeightDip * scale);

        if (displayArea is not null)
        {
            targetWidthPx = Math.Min(targetWidthPx, (int)(displayArea.WorkArea.Width * 0.90));
            targetHeightPx = Math.Min(targetHeightPx, (int)(displayArea.WorkArea.Height * 0.90));
            int x = displayArea.WorkArea.X + ((displayArea.WorkArea.Width - targetWidthPx) / 2);
            int y = displayArea.WorkArea.Y + ((displayArea.WorkArea.Height - targetHeightPx) / 2);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, targetWidthPx, targetHeightPx));
        }
        else
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(targetWidthPx, targetHeightPx));
        }

        UpdateWindowMinimumSize(scale);
    }

    private void InstallMinSizeWindowSubclass()
    {
        try
        {
            if (_hwnd == IntPtr.Zero)
            {
                _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            }

            if (_hwnd != IntPtr.Zero && _subclassDelegate is null)
            {
                _subclassDelegate = WindowSubclassProc;
                _subclassPtr = Marshal.GetFunctionPointerForDelegate(_subclassDelegate);
                _ = SetWindowSubclass(_hwnd, _subclassPtr, WindowSubclassId, UIntPtr.Zero);
            }
        }
        catch
        {
        }
    }

    private void ComputeMinWindowTrackSizePx(IntPtr hwnd, double explicitScale, out int minWidthPx, out int minHeightPx)
    {
        uint dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : 96u;
        if (dpi == 0)
        {
            dpi = 96u;
        }

        double scale = explicitScale > 0.0 ? explicitScale : (dpi / 96.0);
        int extraW = 0;
        int extraH = 0;

        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT winRect) && GetClientRect(hwnd, out RECT clientRect))
        {
            int winW = winRect.Right - winRect.Left;
            int clientW = clientRect.Right - clientRect.Left;
            if (clientW > 0 && winW >= clientW && (winW - clientW) <= (int)Math.Round(64 * scale))
            {
                extraW = winW - clientW;
            }

            int winH = winRect.Bottom - winRect.Top;
            int clientH = clientRect.Bottom - clientRect.Top;
            if (clientH > 0 && winH >= clientH && (winH - clientH) <= (int)Math.Round(64 * scale))
            {
                extraH = winH - clientH;
            }
        }

        if (extraW == 0)
        {
            try
            {
                int borderX = GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
                if (borderX > 0 && borderX <= 32)
                {
                    extraW = borderX * 2;
                }
            }
            catch
            {
            }
        }

        if (extraH == 0)
        {
            try
            {
                int borderY = GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
                if (borderY > 0 && borderY <= 32)
                {
                    extraH = borderY;
                }
            }
            catch
            {
            }
        }

        minWidthPx = (int)Math.Ceiling(MinWindowWidthDip * scale) + extraW;
        minHeightPx = (int)Math.Ceiling(MinWindowHeightDip * scale) + extraH;
    }

    private IntPtr WindowSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        IntPtr result = DefSubclassProc(hWnd, uMsg, wParam, lParam);

        if (uMsg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
        {
            if (ViewModel?.IsFullScreen != true && ViewModel?.IsPresentationMode != true)
            {
                ComputeMinWindowTrackSizePx(hWnd, 0.0, out int minW, out int minH);
                unsafe
                {
                    MINMAXINFO* mmi = (MINMAXINFO*)lParam;
                    if (mmi->ptMinTrackSize.X < minW)
                    {
                        mmi->ptMinTrackSize.X = minW;
                    }

                    if (mmi->ptMinTrackSize.Y < minH)
                    {
                        mmi->ptMinTrackSize.Y = minH;
                    }
                }
            }
        }
        else if (uMsg == WM_DPICHANGED)
        {
            DispatcherQueue?.TryEnqueue(() => UpdateWindowMinimumSize());
        }

        return result;
    }

    private void UpdateWindowMinimumSize(double explicitScale = 0.0)
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                if (_hwnd == IntPtr.Zero)
                {
                    _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                }

                double scale = explicitScale > 0.0 ? explicitScale : (Content?.XamlRoot?.RasterizationScale ?? 0.0);
                ComputeMinWindowTrackSizePx(_hwnd, scale, out int minW, out int minH);
                presenter.PreferredMinimumWidth = minW;
                presenter.PreferredMinimumHeight = minH;
            }
        }
        catch
        {
        }
    }

    private void SetupSidebarClipping()
    {
        try
        {
            var frameVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(SidebarContentFrame);
            frameVisual.Clip = frameVisual.Compositor.CreateInsetClip();

            var paneVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(SidebarPaneContainer);
            paneVisual.Clip = paneVisual.Compositor.CreateInsetClip();

            var gridVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(SidebarContentGrid);
            gridVisual.Clip = gridVisual.Compositor.CreateInsetClip();
        }
        catch
        {
            // Ignore in environments where composition is unavailable
        }

        SidebarContentFrame.SizeChanged += (_, _) => UpdateSidebarClipGeometries();
        SidebarContentGrid.SizeChanged += (_, _) => UpdateSidebarClipGeometries();
        SidebarPaneContainer.SizeChanged += (_, _) => UpdateSidebarClipGeometries();
        UpdateSidebarClipGeometries();
    }

    private void UpdateSidebarClipGeometries()
    {
        double paneW = SidebarPaneContainer.ActualWidth > 0 ? SidebarPaneContainer.ActualWidth : SidebarPaneContainer.Width;
        double paneH = SidebarPaneContainer.ActualHeight;
        if (paneW > 0 && paneH > 0)
        {
            SidebarPaneContainer.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, paneW, paneH)
            };
        }

        double frameW = SidebarContentFrame.ActualWidth;
        double frameH = SidebarContentFrame.ActualHeight;
        if (frameW > 0 && frameH > 0)
        {
            SidebarContentFrame.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, frameW, frameH)
            };
        }

        double gridW = SidebarContentGrid.ActualWidth;
        double gridH = SidebarContentGrid.ActualHeight;
        if (gridW > 0 && gridH > 0)
        {
            SidebarContentGrid.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, gridW, gridH)
            };
        }
    }

    private Windows.Foundation.Size _lastWindowSize;

    private double GetCurrentWindowWidthDip()
    {
        if (_lastWindowSize.Width > 0)
        {
            return _lastWindowSize.Width;
        }

        if (RootGrid?.ActualWidth > 0)
        {
            return RootGrid.ActualWidth;
        }

        return 1280.0;
    }

    private static double GetMaxAllowedSidebarWidth(double windowWidthDip)
    {
        if (windowWidthDip <= 0 || windowWidthDip >= SidebarMaxConstraintBreakpoint)
        {
            return MaxSidebarWidth;
        }

        return Math.Clamp(windowWidthDip - MinDocumentAreaWithSidebarDip, MinSidebarWidth, MaxSidebarWidth);
    }

    private double GetEffectiveSidebarWidth(double windowWidthDip)
    {
        double maxAllowed = GetMaxAllowedSidebarWidth(windowWidthDip);
        return Math.Clamp(_savedSidebarWidth, MinSidebarWidth, maxAllowed);
    }

    private void SetSidebarOpenFromWindowThreshold(bool open, bool animate)
    {
        _isNarrowWindowSidebarToggle = true;
        _suppressSidebarAnimation = !animate;
        ViewModel.SuppressSidebarSettingsPersistence = true;
        try
        {
            ViewModel.IsSidebarOpen = open;
        }
        finally
        {
            ViewModel.SuppressSidebarSettingsPersistence = false;
            _suppressSidebarAnimation = false;
            _isNarrowWindowSidebarToggle = false;
        }
    }

    private void ApplyEffectiveSidebarWidthForWindow(double windowWidthDip)
    {
        double targetEffective = GetEffectiveSidebarWidth(windowWidthDip);
        if (Math.Abs(targetEffective - _effectiveSidebarWidth) <= 0.5 &&
            Math.Abs(SidebarPaneContainer.Width - targetEffective) <= 0.5)
        {
            return;
        }

        _effectiveSidebarWidth = targetEffective;
        SidebarPaneContainer.Width = targetEffective;

        if (ViewModel.HasDocument && ViewModel.IsSidebarOpen && !_isSidebarOpenCloseAnimating)
        {
            MainContentBackdropTransform.TranslateX = targetEffective;
            MainContentClipTransform.TranslateX = targetEffective;
            if (MainContentFrame.Content is DocumentView docView)
            {
                docView.SetSidebarStateImmediate(true, targetEffective);
            }
        }

        UpdateSidebarClipGeometries();
    }

    private void OnMainWindowSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        if (args.Size.Width == _lastWindowSize.Width && args.Size.Height == _lastWindowSize.Height)
        {
            return;
        }

        _lastWindowSize = args.Size;
        double widthDip = args.Size.Width;

        if (!ViewModel.IsFullScreen && !ViewModel.IsPresentationMode &&
            (widthDip < MinWindowWidthDip - 0.5 || args.Size.Height < MinWindowHeightDip - 0.5))
        {
            UpdateWindowMinimumSize();
            if (_hwnd != IntPtr.Zero)
            {
                ComputeMinWindowTrackSizePx(_hwnd, 0.0, out int minW, out int minH);
                int curW = Math.Max(AppWindow.Size.Width, minW);
                int curH = Math.Max(AppWindow.Size.Height, minH);
                if (curW != AppWindow.Size.Width || curH != AppWindow.Size.Height)
                {
                    AppWindow.Resize(new Windows.Graphics.SizeInt32(curW, curH));
                }
            }
        }

        UpdateTitleBarInsets();
        UpdateTitleBarNonClientRegions();

        // 1. < 750 DIPs Compact Chrome (TitleBar & StatusBar text/buttons swallow animation)
        bool isCompact = widthDip < CompactBreakpoint;
        if (ViewModel.IsCompactChrome != isCompact)
        {
            ViewModel.IsCompactChrome = isCompact;
            AnimateTitleBarCompactMode(isCompact);
        }

        // 2. < 950 DIPs Sidebar & Sidebar Toggle Button auto-hide with animation,
        //    and [950..1200] DIPs dynamic sidebar width reduction (keeping document area >= 720 DIPs)
        bool isSidebarAllowed = widthDip >= SidebarAutoCloseBreakpoint;
        bool sidebarAllowedChanged = ViewModel.IsSidebarAllowed != isSidebarAllowed;
        if (sidebarAllowedChanged)
        {
            ViewModel.IsSidebarAllowed = isSidebarAllowed;
            if (ViewModel.HasDocument)
            {
                AnimateSidebarToggleButton(collapse: !isSidebarAllowed);
            }
            else
            {
                ApplySidebarToggleStateImmediate(collapse: true);
            }
        }

        if (!isSidebarAllowed)
        {
            if (ViewModel.IsSidebarOpen)
            {
                _wasSidebarClosedByNarrowWindow = true;
                SetSidebarOpenFromWindowThreshold(false, animate: true);
            }
        }
        else
        {
            ApplyEffectiveSidebarWidthForWindow(widthDip);

            if (_wasSidebarClosedByNarrowWindow && !ViewModel.IsSidebarOpen)
            {
                _wasSidebarClosedByNarrowWindow = false;
                SetSidebarOpenFromWindowThreshold(true, animate: true);
            }
        }
    }

    private void OnToggleMenuFlyoutItemLoaded(object sender, RoutedEventArgs e)
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

    private void OnSidebarToggleClick(object sender, RoutedEventArgs e)
    {
        // Manual user interaction clears automatic narrow memory
        _wasSidebarClosedByNarrowWindow = false;
    }

    private void OnRootGridPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (ViewModel.IsSettingsOpen)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                ViewModel.CloseSettings();
            }
            return;
        }


        if (MainContentFrame.Content is DocumentView docView)
        {
            docView.HandleWindowPreviewKeyDown(e);
        }
    }

    private void RegisterTitleBarAnimatedIconPointerHandlers()
    {
        HookAnimatedIconButton(SidebarToggleButton, SidebarToggleAnimatedIcon);
        HookAnimatedIconButton(SearchBtn, SearchAnimatedIcon);
        HookAnimatedIconButton(SettingsBackBtn, SettingsBackAnimatedIcon);
        HookAnimatedIconButton(SettingsBtn, SettingsAnimatedIcon);
        HookDocumentMenuButton();
    }

    private static void HookAnimatedIconButton(Button button, AnimatedIcon icon)
    {
        AnimatedIcon.SetState(icon, "Normal");

        button.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler((_, _) =>
        {
            AnimatedIcon.SetState(icon, "PointerOver");
        }), true);

        button.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) =>
        {
            AnimatedIcon.SetState(icon, "Normal");
        }), true);

        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            AnimatedIcon.SetState(icon, "Pressed");
        }), true);

        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((sender, e) =>
        {
            if (sender is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                var pos = e.GetCurrentPoint(fe).Position;
                if (pos.X >= 0 && pos.Y >= 0 && pos.X <= fe.ActualWidth && pos.Y <= fe.ActualHeight)
                {
                    AnimatedIcon.SetState(icon, "PointerOver");
                    return;
                }
            }

            AnimatedIcon.SetState(icon, "Normal");
        }), true);

        button.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) =>
        {
            AnimatedIcon.SetState(icon, "Normal");
        }), true);

        button.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) =>
        {
            AnimatedIcon.SetState(icon, "Normal");
        }), true);
    }

    private Storyboard? _ellipsisWaveStoryboard;
    private Storyboard? _settingsTransitionStoryboard;
    private Storyboard? _sidebarStoryboard;
    private Storyboard? _sidebarToggleStoryboard;
    private Storyboard? _titleBarCompactStoryboard;
    private double _cachedAppTitleTextWidth = 56.0;
    private double _cachedOpenFileTextWidth = 88.0;
    private double _cachedSettingsTitleTextWidth = 56.0;
    private bool _isSidebarVisualOpen;

    private void ApplySidebarToggleStateImmediate(bool collapse)
    {
        if (SidebarToggleClipContainer is null || SidebarToggleButton is null || SidebarToggleTransform is null)
        {
            return;
        }

        _sidebarToggleStoryboard?.Stop();
        if (collapse)
        {
            SidebarToggleClipContainer.Width = 0.0;
            SidebarToggleClipContainer.Visibility = Visibility.Collapsed;
            SidebarToggleTransform.TranslateX = -SidebarToggleSlotWidthDip;
            SidebarToggleButton.Opacity = 0.0;
        }
        else
        {
            SidebarToggleClipContainer.Visibility = Visibility.Visible;
            SidebarToggleClipContainer.Width = SidebarToggleSlotWidthDip;
            SidebarToggleTransform.TranslateX = 0.0;
            SidebarToggleButton.Opacity = 1.0;
        }
    }

    private void AnimateSidebarToggleButton(bool collapse)
    {
        if (SidebarToggleClipContainer is null || SidebarToggleButton is null || SidebarToggleTransform is null)
        {
            return;
        }

        _sidebarToggleStoryboard?.Stop();
        SidebarToggleClipContainer.Visibility = Visibility.Visible;

        double startW = collapse
            ? (SidebarToggleClipContainer.ActualWidth > 0.5 ? SidebarToggleClipContainer.ActualWidth : SidebarToggleSlotWidthDip)
            : (double.IsNaN(SidebarToggleClipContainer.Width) ? 0.0 : SidebarToggleClipContainer.Width);
        double targetW = collapse ? 0.0 : SidebarToggleSlotWidthDip;

        double startX = collapse ? SidebarToggleTransform.TranslateX : -SidebarToggleSlotWidthDip;
        double targetX = collapse ? -SidebarToggleSlotWidthDip : 0.0;

        double startOpacity = collapse ? SidebarToggleButton.Opacity : 0.0;
        double targetOpacity = collapse ? 0.0 : 1.0;

        var sb = new Storyboard();
        var ease = new ExponentialEase { Exponent = 4.5, EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(250));

        var widthAnim = new DoubleAnimation
        {
            From = startW,
            To = targetW,
            Duration = duration,
            EasingFunction = ease,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(widthAnim, SidebarToggleClipContainer);
        Storyboard.SetTargetProperty(widthAnim, "Width");
        sb.Children.Add(widthAnim);

        var translateAnim = new DoubleAnimation
        {
            From = startX,
            To = targetX,
            Duration = duration,
            EasingFunction = ease
        };
        Storyboard.SetTarget(translateAnim, SidebarToggleTransform);
        Storyboard.SetTargetProperty(translateAnim, "TranslateX");
        sb.Children.Add(translateAnim);

        var opacityAnim = new DoubleAnimation
        {
            From = startOpacity,
            To = targetOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacityAnim, SidebarToggleButton);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        sb.Children.Add(opacityAnim);

        sb.Completed += (_, _) =>
        {
            ApplySidebarToggleStateImmediate(!ViewModel.HasDocument || !ViewModel.IsSidebarAllowed);
        };

        _sidebarToggleStoryboard = sb;
        sb.Begin();
    }

    private void ApplyTitleBarCompactStateImmediate(bool compact)
    {
        _titleBarCompactStoryboard?.Stop();
        if (OpenFileGroupPanel is not null)
        {
            OpenFileGroupPanel.Margin = compact ? new Thickness(8, 0, 0, 0) : new Thickness(20, 0, 0, 0);
        }

        SetElementSwallowStateImmediate(AppTitleTextClipContainer, AppTitleTextBlock, AppTitleTextTransform, compact, ref _cachedAppTitleTextWidth, 56.0);
        SetElementSwallowStateImmediate(OpenFileTextClipContainer, OpenFileButtonTextBlock, OpenFileTextTransform, compact, ref _cachedOpenFileTextWidth, 88.0);
        SetElementSwallowStateImmediate(SettingsTitleTextClipContainer, SettingsTitleTextBlock, SettingsTitleTextTransform, compact, ref _cachedSettingsTitleTextWidth, 56.0);
    }

    private static void SetElementSwallowStateImmediate(
        FrameworkElement? clipContainer,
        FrameworkElement? innerElement,
        CompositeTransform? innerTransform,
        bool compact,
        ref double cachedWidth,
        double fallbackWidth)
    {
        if (clipContainer is null || innerElement is null || innerTransform is null)
        {
            return;
        }

        if (clipContainer.ActualWidth > 1.0)
        {
            cachedWidth = clipContainer.ActualWidth;
        }
        else if (innerElement.ActualWidth > 1.0)
        {
            cachedWidth = innerElement.ActualWidth;
        }

        if (compact)
        {
            double w = cachedWidth > 1.0 ? cachedWidth : fallbackWidth;
            clipContainer.Width = 0.0;
            clipContainer.Visibility = Visibility.Collapsed;
            innerTransform.TranslateX = -w;
            innerElement.Opacity = 0.0;
        }
        else
        {
            clipContainer.Visibility = Visibility.Visible;
            clipContainer.Width = double.NaN;
            innerTransform.TranslateX = 0.0;
            innerElement.Opacity = 1.0;
        }
    }

    private void AnimateTitleBarCompactMode(bool collapse)
    {
        _titleBarCompactStoryboard?.Stop();
        if (OpenFileGroupPanel is not null)
        {
            OpenFileGroupPanel.Margin = collapse ? new Thickness(8, 0, 0, 0) : new Thickness(20, 0, 0, 0);
        }

        var sb = new Storyboard();
        AddSwallowAnimations(sb, AppTitleTextClipContainer, AppTitleTextBlock, AppTitleTextTransform, collapse, ref _cachedAppTitleTextWidth, 56.0);
        AddSwallowAnimations(sb, OpenFileTextClipContainer, OpenFileButtonTextBlock, OpenFileTextTransform, collapse, ref _cachedOpenFileTextWidth, 88.0);
        AddSwallowAnimations(sb, SettingsTitleTextClipContainer, SettingsTitleTextBlock, SettingsTitleTextTransform, collapse, ref _cachedSettingsTitleTextWidth, 56.0);

        sb.Completed += (_, _) =>
        {
            ApplyTitleBarCompactStateImmediate(ViewModel.IsCompactChrome);
        };

        _titleBarCompactStoryboard = sb;
        sb.Begin();
    }

    private static void AddSwallowAnimations(
        Storyboard sb,
        FrameworkElement? clipContainer,
        FrameworkElement? innerElement,
        CompositeTransform? innerTransform,
        bool collapse,
        ref double cachedWidth,
        double fallbackWidth)
    {
        if (clipContainer is null || innerElement is null || innerTransform is null)
        {
            return;
        }

        if (collapse)
        {
            double startW = clipContainer.ActualWidth > 1.0
                ? clipContainer.ActualWidth
                : (innerElement.ActualWidth > 1.0 ? innerElement.ActualWidth : (cachedWidth > 1.0 ? cachedWidth : fallbackWidth));
            if (startW > 1.0)
            {
                cachedWidth = startW;
            }

            clipContainer.Visibility = Visibility.Visible;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(185));

            var widthAnim = new DoubleAnimation
            {
                From = startW,
                To = 0.0,
                Duration = duration,
                EasingFunction = ease,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(widthAnim, clipContainer);
            Storyboard.SetTargetProperty(widthAnim, "Width");
            sb.Children.Add(widthAnim);

            var translateAnim = new DoubleAnimation
            {
                From = innerTransform.TranslateX,
                To = -startW,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(translateAnim, innerTransform);
            Storyboard.SetTargetProperty(translateAnim, "TranslateX");
            sb.Children.Add(translateAnim);

            var opacityAnim = new DoubleAnimation
            {
                From = innerElement.Opacity,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                EasingFunction = ease
            };
            Storyboard.SetTarget(opacityAnim, innerElement);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            sb.Children.Add(opacityAnim);
        }
        else
        {
            clipContainer.Visibility = Visibility.Visible;
            innerElement.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double targetW = innerElement.DesiredSize.Width > 1.0
                ? innerElement.DesiredSize.Width
                : (cachedWidth > 1.0 ? cachedWidth : fallbackWidth);
            cachedWidth = targetW;

            clipContainer.Width = 0.0;
            innerTransform.TranslateX = -targetW;
            innerElement.Opacity = 0.0;

            var ease = new ExponentialEase { Exponent = 4.0, EasingMode = EasingMode.EaseOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(210));

            var widthAnim = new DoubleAnimation
            {
                From = 0.0,
                To = targetW,
                Duration = duration,
                EasingFunction = ease,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(widthAnim, clipContainer);
            Storyboard.SetTargetProperty(widthAnim, "Width");
            sb.Children.Add(widthAnim);

            var translateAnim = new DoubleAnimation
            {
                From = -targetW,
                To = 0.0,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(translateAnim, innerTransform);
            Storyboard.SetTargetProperty(translateAnim, "TranslateX");
            sb.Children.Add(translateAnim);

            var opacityAnim = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(175)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(opacityAnim, innerElement);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            sb.Children.Add(opacityAnim);
        }
    }

    private void AnimateSettingsTransition(bool opening)
    {
        _settingsTransitionStoryboard?.Stop();

        if (opening)
        {
            if (SettingsContentFrame.Content is not SettingsView)
            {
                SettingsContentFrame.Navigate(typeof(SettingsView), ViewModel, new SuppressNavigationTransitionInfo());
            }

            SettingsContentFrame.Visibility = Visibility.Visible;
            SettingsContentFrame.IsHitTestVisible = true;
            MainSplitView.IsHitTestVisible = false;

            MainSplitView.Opacity = 1.0;
            MainSplitViewTransform.TranslateX = 0.0;
            MainSplitViewTransform.TranslateY = 0.0;
            SettingsContentFrame.Opacity = 0.0;
            SettingsFrameTransform.TranslateX = 0.0;
            SettingsFrameTransform.TranslateY = -28.0;

            var sb = new Storyboard();
            var outEase = new CubicEase { EasingMode = EasingMode.EaseOut };
            var inEase = new ExponentialEase { Exponent = 5.0, EasingMode = EasingMode.EaseOut };

            var splitOpacityAnim = new DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(110)),
                EasingFunction = outEase
            };
            Storyboard.SetTarget(splitOpacityAnim, MainSplitView);
            Storyboard.SetTargetProperty(splitOpacityAnim, "Opacity");
            sb.Children.Add(splitOpacityAnim);

            var settingsOpacityAnim = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                EasingFunction = outEase
            };
            Storyboard.SetTarget(settingsOpacityAnim, SettingsContentFrame);
            Storyboard.SetTargetProperty(settingsOpacityAnim, "Opacity");
            sb.Children.Add(settingsOpacityAnim);

            var settingsDropAnim = new DoubleAnimation
            {
                From = -28.0,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(280)),
                EasingFunction = inEase
            };
            Storyboard.SetTarget(settingsDropAnim, SettingsFrameTransform);
            Storyboard.SetTargetProperty(settingsDropAnim, "TranslateY");
            sb.Children.Add(settingsDropAnim);

            sb.Completed += (_, _) =>
            {
                if (ViewModel.IsSettingsOpen)
                {
                    MainSplitView.Opacity = 0.0;
                    MainSplitViewTransform.TranslateX = 0.0;
                    MainSplitViewTransform.TranslateY = 0.0;
                    SettingsContentFrame.Opacity = 1.0;
                    SettingsFrameTransform.TranslateX = 0.0;
                    SettingsFrameTransform.TranslateY = 0.0;
                }
            };

            _settingsTransitionStoryboard = sb;
            sb.Begin();
        }
        else
        {
            MainSplitView.IsHitTestVisible = true;
            SettingsContentFrame.IsHitTestVisible = false;

            if (MainContentFrame.Content is DocumentView docView)
            {
                docView.OnReturnedFromSettings();
            }

            if (SidebarContentFrame.Content is WinPDF.Views.Sidebar.ThumbnailsTabPage thumbPage)
            {
                thumbPage.RefreshAfterVisibilityRestored();
            }

            SettingsContentFrame.Visibility = Visibility.Visible;
            SettingsContentFrame.Opacity = 1.0;
            SettingsFrameTransform.TranslateX = 0.0;
            SettingsFrameTransform.TranslateY = 0.0;
            MainSplitView.Opacity = 0.0;
            MainSplitViewTransform.TranslateX = 0.0;
            MainSplitViewTransform.TranslateY = -20.0;

            var sb = new Storyboard();
            var outEase = new CubicEase { EasingMode = EasingMode.EaseOut };
            var inEase = new ExponentialEase { Exponent = 4.5, EasingMode = EasingMode.EaseOut };

            var settingsOpacityAnim = new DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(130)),
                EasingFunction = outEase
            };
            Storyboard.SetTarget(settingsOpacityAnim, SettingsContentFrame);
            Storyboard.SetTargetProperty(settingsOpacityAnim, "Opacity");
            sb.Children.Add(settingsOpacityAnim);

            var settingsTranslateAnim = new DoubleAnimation
            {
                From = 0.0,
                To = 20.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(160)),
                EasingFunction = outEase
            };
            Storyboard.SetTarget(settingsTranslateAnim, SettingsFrameTransform);
            Storyboard.SetTargetProperty(settingsTranslateAnim, "TranslateY");
            sb.Children.Add(settingsTranslateAnim);

            var splitOpacityAnim = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = outEase
            };
            Storyboard.SetTarget(splitOpacityAnim, MainSplitView);
            Storyboard.SetTargetProperty(splitOpacityAnim, "Opacity");
            sb.Children.Add(splitOpacityAnim);

            var splitTranslateAnim = new DoubleAnimation
            {
                From = -20.0,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(260)),
                EasingFunction = inEase
            };
            Storyboard.SetTarget(splitTranslateAnim, MainSplitViewTransform);
            Storyboard.SetTargetProperty(splitTranslateAnim, "TranslateY");
            sb.Children.Add(splitTranslateAnim);

            sb.Completed += (_, _) =>
            {
                if (!ViewModel.IsSettingsOpen)
                {
                    SettingsContentFrame.Visibility = Visibility.Collapsed;
                    SettingsContentFrame.Opacity = 0.0;
                    SettingsFrameTransform.TranslateX = 0.0;
                    SettingsFrameTransform.TranslateY = 0.0;
                    MainSplitView.Opacity = 1.0;
                    MainSplitViewTransform.TranslateX = 0.0;
                    MainSplitViewTransform.TranslateY = 0.0;
                }
            };

            _settingsTransitionStoryboard = sb;
            sb.Begin();
        }
    }

    private void HookDocumentMenuButton()
    {
        DocumentMenuBtn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            PlayEllipsisWaveAnimation();
        }), true);
    }

    private void PlayEllipsisWaveAnimation()
    {
        _ellipsisWaveStoryboard?.Stop();
        var sb = new Storyboard();

        TranslateTransform[] dots = [EllipsisDot1Transform, EllipsisDot2Transform, EllipsisDot3Transform];
        for (int i = 0; i < dots.Length; i++)
        {
            var keyFrames = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = TimeSpan.FromMilliseconds(i * 45)
            };
            keyFrames.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0)),
                Value = 0.0
            });
            keyFrames.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(105)),
                Value = -2.5,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            keyFrames.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(225)),
                Value = 0.0,
                EasingFunction = new BounceEase { Bounces = 1, Bounciness = 3, EasingMode = EasingMode.EaseOut }
            });

            Storyboard.SetTarget(keyFrames, dots[i]);
            Storyboard.SetTargetProperty(keyFrames, "Y");
            sb.Children.Add(keyFrames);
        }

        _ellipsisWaveStoryboard = sb;
        sb.Begin();
    }

    private static void AddDoubleAnimation(Storyboard sb, DependencyObject target, string property, double toValue, double durationMs)
    {
        var anim = new DoubleAnimation
        {
            To = toValue,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        sb.Children.Add(anim);
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        if (_hwnd != IntPtr.Zero && _subclassPtr != IntPtr.Zero)
        {
            try { _ = RemoveWindowSubclass(_hwnd, _subclassPtr, WindowSubclassId); } catch { }
            _subclassPtr = IntPtr.Zero;
        }

        ViewModel.SettingsService.Update(s =>
        {
            s.SidebarWidth = _savedSidebarWidth;
            if (ViewModel.CurrentDocument is { } doc && !string.IsNullOrWhiteSpace(doc.FilePath))
            {
                s.LastOpenedFilePath = doc.FilePath;
                s.LastOpenedPageIndex = doc.CurrentPageIndex;

                int? pageToSave = s.RememberDocumentPage ? doc.CurrentPageIndex : null;
                bool saveView = s.SaveDocumentViewSettings;
                double? zoomToSave = saveView ? doc.ZoomFactor : null;
                ViewMode? viewModeToSave = saveView ? ViewModel.ViewMode : null;

                if (pageToSave.HasValue || saveView)
                {
                    if (!s.DocumentStates.TryGetValue(doc.FilePath, out var docState) || docState is null)
                    {
                        docState = new Models.SavedDocumentState();
                        s.DocumentStates[doc.FilePath] = docState;
                    }

                    if (pageToSave.HasValue)
                    {
                        docState.PageIndex = pageToSave.Value;
                    }

                    if (saveView)
                    {
                        docState.HasCustomViewSettings = true;
                        if (zoomToSave.HasValue && double.IsFinite(zoomToSave.Value))
                        {
                            docState.ZoomFactor = zoomToSave.Value;
                        }

                        if (viewModeToSave.HasValue)
                        {
                            docState.ViewMode = viewModeToSave.Value;
                        }
                    }

                    docState.LastOpenedUtc = DateTime.UtcNow;
                }
            }
            else
            {
                s.LastOpenedFilePath = null;
                s.LastOpenedPageIndex = 0;
            }
        });
    }

    private void ApplyRequestedThemeFromViewModel(bool isStartup = false)
    {
        ElementTheme desiredTheme = ViewModel.CurrentTheme switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (isStartup && desiredTheme != ElementTheme.Default && RootGrid.ActualTheme == desiredTheme)
        {
            return;
        }

        if (RootGrid.RequestedTheme != desiredTheme)
        {
            RootGrid.RequestedTheme = desiredTheme;
        }
    }

    private void OnSidebarSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        double maxAllowed = GetMaxAllowedSidebarWidth(GetCurrentWindowWidthDip());
        double currentWidth = SidebarPaneContainer.Width > 0 ? SidebarPaneContainer.Width : _effectiveSidebarWidth;
        double newWidth = Math.Clamp(currentWidth + e.HorizontalChange, MinSidebarWidth, maxAllowed);
        _savedSidebarWidth = newWidth;
        _effectiveSidebarWidth = newWidth;
        SidebarPaneContainer.Width = newWidth;
        ViewModel.SettingsService.Settings.SidebarWidth = newWidth;

        if (ViewModel.HasDocument && ViewModel.IsSidebarOpen)
        {
            MainContentBackdropTransform.TranslateX = newWidth;
            MainContentClipTransform.TranslateX = newWidth;
            if (MainContentFrame.Content is DocumentView docView)
            {
                docView.SetSidebarStateImmediate(true, newWidth);
            }
        }

        UpdateSidebarClipGeometries();
    }

    private void OnMainContentContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            MainContentClipGeometry.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width + 600, e.NewSize.Height + 100);
        }
    }

    private void UpdateSidebarLayout(bool animate = false)
    {
        bool shouldOpenPane = ViewModel.HasDocument && ViewModel.IsSidebarOpen && ViewModel.IsSidebarAllowed;
        double targetEffectiveWidth = GetEffectiveSidebarWidth(GetCurrentWindowWidthDip());
        _effectiveSidebarWidth = targetEffectiveWidth;

        double paneWidth = shouldOpenPane
            ? targetEffectiveWidth
            : (SidebarPaneContainer.ActualWidth > 0 ? SidebarPaneContainer.ActualWidth : targetEffectiveWidth);
        SidebarPaneContainer.Width = paneWidth;

        if (!ViewModel.HasDocument)
        {
            MainContentBackdropBorder.CornerRadius = new CornerRadius(0);
            MainContentBackdropBorder.BorderThickness = new Thickness(0);
        }
        else if (shouldOpenPane)
        {
            MainContentBackdropBorder.CornerRadius = new CornerRadius(8, 0, 0, 0);
            MainContentBackdropBorder.BorderThickness = new Thickness(1, 1, 0, 0);
        }

        if (!animate || !ViewModel.HasDocument || ViewModel.IsSettingsOpen || ViewModel.IsPresentationMode)
        {
            _sidebarStoryboard?.Stop();
            _isSidebarOpenCloseAnimating = false;
            _isSidebarVisualOpen = shouldOpenPane;
            SidebarPaneContainer.Visibility = shouldOpenPane ? Visibility.Visible : Visibility.Collapsed;
            SidebarPaneContainer.Opacity = shouldOpenPane ? 1.0 : 0.0;
            SidebarPaneTransform.TranslateX = shouldOpenPane ? 0.0 : -paneWidth;
            MainContentBackdropTransform.TranslateX = shouldOpenPane ? paneWidth : 0.0;
            MainContentClipTransform.TranslateX = shouldOpenPane ? paneWidth : 0.0;

            if (!shouldOpenPane && ViewModel.HasDocument)
            {
                MainContentBackdropBorder.CornerRadius = new CornerRadius(0);
                MainContentBackdropBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            }

            if (MainContentFrame.Content is DocumentView docViewImmediate)
            {
                docViewImmediate.SetSidebarStateImmediate(shouldOpenPane, paneWidth);
            }

            if (shouldOpenPane && SidebarContentFrame.Content is WinPDF.Views.Sidebar.ThumbnailsTabPage thumbPage)
            {
                DispatcherQueue.TryEnqueue(thumbPage.RefreshAfterVisibilityRestored);
            }

            return;
        }

        if (_isSidebarVisualOpen == shouldOpenPane)
        {
            return;
        }

        _isSidebarVisualOpen = shouldOpenPane;
        _sidebarStoryboard?.Stop();
        _isSidebarOpenCloseAnimating = true;

        if (MainContentFrame.Content is DocumentView docView)
        {
            docView.AnimateSidebarTransition(shouldOpenPane, paneWidth);
        }

        var sb = new Storyboard();
        var ease = new ExponentialEase { Exponent = 4.5, EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(250));

        if (shouldOpenPane)
        {
            if (SidebarPaneContainer.Visibility == Visibility.Collapsed)
            {
                SidebarPaneTransform.TranslateX = -paneWidth;
                SidebarPaneContainer.Opacity = 0.0;
                SidebarPaneContainer.Visibility = Visibility.Visible;
            }

            var paneTranslate = new DoubleAnimation
            {
                To = 0.0,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(paneTranslate, SidebarPaneTransform);
            Storyboard.SetTargetProperty(paneTranslate, "TranslateX");
            sb.Children.Add(paneTranslate);

            var paneOpacity = new DoubleAnimation
            {
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(paneOpacity, SidebarPaneContainer);
            Storyboard.SetTargetProperty(paneOpacity, "Opacity");
            sb.Children.Add(paneOpacity);

            var backdropTranslate = new DoubleAnimation
            {
                To = paneWidth,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(backdropTranslate, MainContentBackdropTransform);
            Storyboard.SetTargetProperty(backdropTranslate, "TranslateX");
            sb.Children.Add(backdropTranslate);

            var clipTranslate = new DoubleAnimation
            {
                To = paneWidth,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(clipTranslate, MainContentClipTransform);
            Storyboard.SetTargetProperty(clipTranslate, "TranslateX");
            sb.Children.Add(clipTranslate);

            if (SidebarContentFrame.Content is WinPDF.Views.Sidebar.ThumbnailsTabPage thumbPage)
            {
                DispatcherQueue.TryEnqueue(thumbPage.RefreshAfterVisibilityRestored);
            }

            sb.Completed += (_, _) =>
            {
                _isSidebarOpenCloseAnimating = false;
                if (ViewModel.IsSidebarOpen && ViewModel.HasDocument && ViewModel.IsSidebarAllowed)
                {
                    double latestWidth = GetEffectiveSidebarWidth(GetCurrentWindowWidthDip());
                    _effectiveSidebarWidth = latestWidth;
                    SidebarPaneContainer.Width = latestWidth;
                    SidebarPaneTransform.TranslateX = 0.0;
                    SidebarPaneContainer.Opacity = 1.0;
                    MainContentBackdropTransform.TranslateX = latestWidth;
                    MainContentClipTransform.TranslateX = latestWidth;
                    if (MainContentFrame.Content is DocumentView dv)
                    {
                        dv.SetSidebarStateImmediate(true, latestWidth);
                    }
                    if (SidebarContentFrame.Content is WinPDF.Views.Sidebar.ThumbnailsTabPage tp)
                    {
                        DispatcherQueue.TryEnqueue(tp.RefreshAfterVisibilityRestored);
                    }
                }
            };
        }
        else
        {
            if (ViewModel.HasDocument)
            {
                MainContentBackdropBorder.CornerRadius = new CornerRadius(8, 0, 0, 0);
                MainContentBackdropBorder.BorderThickness = new Thickness(1, 1, 0, 0);
            }

            var paneTranslate = new DoubleAnimation
            {
                To = -paneWidth,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(paneTranslate, SidebarPaneTransform);
            Storyboard.SetTargetProperty(paneTranslate, "TranslateX");
            sb.Children.Add(paneTranslate);

            var paneOpacity = new DoubleAnimation
            {
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(170)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(paneOpacity, SidebarPaneContainer);
            Storyboard.SetTargetProperty(paneOpacity, "Opacity");
            sb.Children.Add(paneOpacity);

            var backdropTranslate = new DoubleAnimation
            {
                To = 0.0,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(backdropTranslate, MainContentBackdropTransform);
            Storyboard.SetTargetProperty(backdropTranslate, "TranslateX");
            sb.Children.Add(backdropTranslate);

            var clipTranslate = new DoubleAnimation
            {
                To = 0.0,
                Duration = duration,
                EasingFunction = ease
            };
            Storyboard.SetTarget(clipTranslate, MainContentClipTransform);
            Storyboard.SetTargetProperty(clipTranslate, "TranslateX");
            sb.Children.Add(clipTranslate);

            sb.Completed += (_, _) =>
            {
                _isSidebarOpenCloseAnimating = false;
                if (!ViewModel.IsSidebarOpen || !ViewModel.HasDocument || !ViewModel.IsSidebarAllowed)
                {
                    SidebarPaneContainer.Visibility = Visibility.Collapsed;
                    SidebarPaneContainer.Opacity = 0.0;
                    SidebarPaneTransform.TranslateX = -paneWidth;
                    MainContentBackdropTransform.TranslateX = 0.0;
                    MainContentClipTransform.TranslateX = 0.0;
                    MainContentBackdropBorder.CornerRadius = new CornerRadius(0);
                    MainContentBackdropBorder.BorderThickness = ViewModel.HasDocument ? new Thickness(0, 1, 0, 0) : new Thickness(0);
                }
            };
        }

        _sidebarStoryboard = sb;
        sb.Begin();
    }

    private void UpdateMainContainerStyle()
    {
        MainContentBackdropBorder.Visibility = ViewModel.HasDocument ? Visibility.Visible : Visibility.Collapsed;
        if (ViewModel.HasDocument)
        {
            bool sidebarOpen = (ViewModel.IsSidebarOpen && ViewModel.IsSidebarAllowed) || _isSidebarOpenCloseAnimating;
            MainContentBackdropBorder.BorderThickness = sidebarOpen ? new Thickness(1, 1, 0, 0) : new Thickness(0, 1, 0, 0);
            MainContentBackdropBorder.CornerRadius = sidebarOpen ? new CornerRadius(8, 0, 0, 0) : new CornerRadius(0);
        }
        else
        {
            MainContentBackdropBorder.BorderThickness = new Thickness(0);
            MainContentBackdropBorder.CornerRadius = new CornerRadius(0);
        }
    }

    private double _lastRightInsetDip = -1;

    private void UpdateTitleBarInsets()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        if (ViewModel.IsFullScreen)
        {
            if (Math.Abs(_lastRightInsetDip - 12.0) > 0.5)
            {
                _lastRightInsetDip = 12.0;
                AppTitleBar.Padding = new Thickness(12, 0, 12, 0);
            }
            return;
        }

        if (AppWindow.TitleBar.RightInset > 0)
        {
            double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
            double rightInsetDip = AppWindow.TitleBar.RightInset / scale;
            if (Math.Abs(_lastRightInsetDip - rightInsetDip) > 0.5)
            {
                _lastRightInsetDip = rightInsetDip;
                AppTitleBar.Padding = new Thickness(12, 0, Math.Max(140, rightInsetDip), 0);
            }
        }
        else
        {
            if (_lastRightInsetDip < 140)
            {
                _lastRightInsetDip = 140;
                AppTitleBar.Padding = new Thickness(12, 0, 140, 0);
            }
        }
    }



    private void UpdateTitleBarNonClientRegions()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        try
        {
            var nonClientInput = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            nonClientInput.ClearRegionRects(NonClientRegionKind.Passthrough);
        }
        catch
        {
            // Fallback gracefully on environments where custom non-client regions are unavailable
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentDocument))
        {
            var docName = ViewModel.CurrentDocument?.FileName;
            Title = string.IsNullOrEmpty(docName) ? "WinPDF" : $"{docName} — WinPDF";

            DispatcherQueue.TryEnqueue(UpdateTitleBarNonClientRegions);
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsFullScreen))
        {
            UpdateFullScreenState(ViewModel.IsFullScreen);
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsPresentationMode))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateTitleBarInsets();
                UpdateTitleBarNonClientRegions();
            });
        }
        else if (e.PropertyName == nameof(ShellViewModel.CurrentTheme))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ApplyRequestedThemeFromViewModel(isStartup: false);
                UpdateTitleBarTheme();
            });
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsSidebarOpen))
        {
            if (!_isNarrowWindowSidebarToggle && !_suppressSidebarAnimation)
            {
                _wasSidebarClosedByNarrowWindow = false;
            }

            UpdateSidebarLayout(animate: !_suppressSidebarAnimation);
        }
        else if (e.PropertyName == nameof(ShellViewModel.HasDocument))
        {
            double currentW = GetCurrentWindowWidthDip();
            bool sidebarAllowed = currentW >= SidebarAutoCloseBreakpoint;
            ViewModel.IsSidebarAllowed = sidebarAllowed;

            if (ViewModel.HasDocument && !sidebarAllowed && ViewModel.IsSidebarOpen)
            {
                _wasSidebarClosedByNarrowWindow = true;
                SetSidebarOpenFromWindowThreshold(false, animate: false);
            }

            ApplySidebarToggleStateImmediate(collapse: !ViewModel.HasDocument || !sidebarAllowed);
            UpdateSidebarLayout();
            UpdateMainContainerStyle();
            SelectSidebarTab(ViewModel.SidebarTab);
            DispatcherQueue.TryEnqueue(UpdateTitleBarNonClientRegions);
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsAnnotationToolbarOpen))
        {
            DispatcherQueue.TryEnqueue(UpdateTitleBarNonClientRegions);
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsSettingsOpen))
        {
            UpdateSidebarLayout();
            UpdateMainContainerStyle();
            AnimateSettingsTransition(ViewModel.IsSettingsOpen);
            DispatcherQueue.TryEnqueue(UpdateTitleBarNonClientRegions);
        }
        else if (e.PropertyName == nameof(ShellViewModel.SidebarTab))
        {
            SelectSidebarTab(ViewModel.SidebarTab);
        }
    }

    private void OnAppWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange)
        {
            bool isFullScreen = AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
            if (ViewModel.IsFullScreen != isFullScreen)
            {
                ViewModel.IsFullScreen = isFullScreen;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateTitleBarInsets();
                UpdateTitleBarNonClientRegions();
            });
        }
    }

    private bool _sidebarOpenBeforeFullScreen;

    private void UpdateFullScreenState(bool isFullScreen)
    {
        if (isFullScreen)
        {
            if (!ViewModel.IsPresentationMode)
            {
                _sidebarOpenBeforeFullScreen = ViewModel.IsSidebarOpen;
                if (ViewModel.IsSidebarOpen)
                {
                    SetSidebarOpenFromWindowThreshold(false, animate: false);
                }
            }

            AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateTitleBarInsets();
                UpdateTitleBarNonClientRegions();
            });
        }
        else
        {
            AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
            UpdateWindowMinimumSize();
            if (!ViewModel.IsPresentationMode && _sidebarOpenBeforeFullScreen)
            {
                _sidebarOpenBeforeFullScreen = false;
                if (GetCurrentWindowWidthDip() >= SidebarAutoCloseBreakpoint)
                {
                    SetSidebarOpenFromWindowThreshold(true, animate: false);
                }
                else
                {
                    _wasSidebarClosedByNarrowWindow = true;
                }
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateTitleBarInsets();
                UpdateTitleBarNonClientRegions();
            });
        }
    }

    private void UpdateTitleBarTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = AppWindow.TitleBar;

        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;

        titleBar.ButtonForegroundColor = isDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        titleBar.ButtonHoverForegroundColor = isDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        titleBar.ButtonHoverBackgroundColor = isDark ? Windows.UI.Color.FromArgb(24, 255, 255, 255) : Windows.UI.Color.FromArgb(16, 0, 0, 0);
        titleBar.ButtonPressedBackgroundColor = isDark ? Windows.UI.Color.FromArgb(40, 255, 255, 255) : Windows.UI.Color.FromArgb(28, 0, 0, 0);
        titleBar.ButtonInactiveForegroundColor = isDark ? Windows.UI.Color.FromArgb(128, 255, 255, 255) : Windows.UI.Color.FromArgb(128, 0, 0, 0);
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
    }

    private void OnSidebarTabButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } &&
            Enum.TryParse<SidebarTab>(tag, out var tab))
        {
            SelectSidebarTab(tab);
        }
    }

    private void SelectSidebarTab(SidebarTab targetTab)
    {
        int targetIndex = (int)targetTab;
        Type pageType = targetTab switch
        {
            SidebarTab.Thumbnails => typeof(ThumbnailsTabPage),
            SidebarTab.Outline => typeof(OutlineTabPage),
            SidebarTab.Annotations => typeof(AnnotationsTabPage),
            SidebarTab.Bookmarks => typeof(BookmarksTabPage),
            SidebarTab.Attachments => typeof(AttachmentsTabPage),
            _ => typeof(ThumbnailsTabPage)
        };

        if (ViewModel.SidebarTab != targetTab)
        {
            ViewModel.SetSidebarTab(targetTab);
        }

        if (_previousSidebarIndex == -1 || SidebarContentFrame.Content is null)
        {
            SidebarContentFrame.Navigate(pageType, ViewModel);
        }
        else if (targetIndex != _previousSidebarIndex)
        {
            var effect = targetIndex - _previousSidebarIndex > 0
                ? SlideNavigationTransitionEffect.FromRight
                : SlideNavigationTransitionEffect.FromLeft;
            SidebarContentFrame.Navigate(pageType, ViewModel, new SlideNavigationTransitionInfo { Effect = effect });
        }

        _previousSidebarIndex = targetIndex;
    }

    private void OnPrintClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.PrintAsync();
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
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

    private async void OnWindowDrop(object sender, DragEventArgs e)
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
            System.Diagnostics.Debug.WriteLine($"Failed to open dropped PDF file in window: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }
}
