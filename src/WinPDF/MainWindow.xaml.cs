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
    private const double CompactBreakpoint = 768.0;
    private const double MinSidebarWidth = 220.0;
    private const double MaxSidebarWidth = 480.0;
    private const double DefaultSidebarWidth = 260.0;

    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private double _savedSidebarWidth = DefaultSidebarWidth;
    private uint? _touchSplitterActivePointerId;
    private uint? _touchSplitterPendingPointerId;
    private double _touchSplitterStartX;
    private double _touchSplitterStartY;
    private double _touchSplitterInitialWidth;
    private bool _wasSidebarClosedByNarrowWindow;
    private bool _isInternalSidebarToggle;
    private int _previousSidebarIndex = -1;


    [LibraryImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
    private static partial IntPtr SendMessage(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

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
        RegisterSidebarTouchAndInertiaHandlers();

        _savedSidebarWidth = Math.Clamp(ViewModel.SettingsService.Settings.SidebarWidth, MinSidebarWidth, MaxSidebarWidth);
        SidebarPaneContainer.Width = _savedSidebarWidth;
        SidebarContentFrame.SizeChanged += OnSidebarContentFrameSizeChanged;
        SidebarContentGrid.SizeChanged += OnSidebarContentGridSizeChanged;
        Closed += OnMainWindowClosed;

        SelectSidebarTab(ViewModel.SidebarTab);

        AppWindow.Changed += OnAppWindowChanged;

        if (Content is FrameworkElement root)
        {
            root.Loaded += (_, _) =>
            {
                SetInitialWindowBounds();
                ApplyRequestedThemeFromViewModel(isStartup: true);
                UpdateTitleBarTheme();
                UpdateTitleBarInsets();
                UpdateTitleBarNonClientRegions();
                UpdateSidebarLayout();
                UpdateMainContainerStyle();

                // Initial frame navigation
                MainContentFrame.Navigate(typeof(DocumentView), ViewModel);
                if (MainContentFrame.Content is DocumentView initialDocView)
                {
                    initialDocView.SetSidebarStateImmediate(
                        ViewModel.HasDocument && ViewModel.IsSidebarOpen,
                        _savedSidebarWidth);
                }

                SelectSidebarTab(ViewModel.SidebarTab);

                if (root.XamlRoot is not null)
                {
                    root.XamlRoot.Changed += (_, _) =>
                    {
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
        double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
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
    }

    private Windows.Foundation.Size _lastWindowSize;

    private void OnSidebarContentFrameSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            SidebarContentFrame.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };
        }
    }

    private void OnSidebarContentGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            SidebarContentGrid.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };
        }
    }

    private void OnMainWindowSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        if (!_hasSetInitialBounds || args.Size.Width <= 0 || args.Size.Height <= 0)
        {
            return;
        }

        if (args.Size.Width == _lastWindowSize.Width && args.Size.Height == _lastWindowSize.Height)
        {
            return;
        }

        _lastWindowSize = args.Size;

        UpdateTitleBarInsets();
        UpdateTitleBarNonClientRegions();

        // Responsive behavior with state memory:
        // Automatically close sidebar if window is narrower than 768 DIPs,
        // and restore it when expanding back to >= 768 DIPs if it was closed automatically.
        if (args.Size.Width < CompactBreakpoint)
        {
            if (ViewModel.IsSidebarOpen)
            {
                _wasSidebarClosedByNarrowWindow = true;
                _isInternalSidebarToggle = true;
                try
                {
                    ViewModel.SetSidebarOpenInternal(false);
                }
                finally
                {
                    _isInternalSidebarToggle = false;
                }
            }
        }
        else
        {
            if (_wasSidebarClosedByNarrowWindow && !ViewModel.IsSidebarOpen)
            {
                _wasSidebarClosedByNarrowWindow = false;
                _isInternalSidebarToggle = true;
                try
                {
                    ViewModel.SetSidebarOpenInternal(true);
                }
                finally
                {
                    _isInternalSidebarToggle = false;
                }
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
    private bool _isSidebarVisualOpen;

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
        bool effectiveSidebarOpen = _wasSidebarClosedByNarrowWindow
            ? true
            : (ViewModel.IsFullScreen ? _sidebarOpenBeforeFullScreen : ViewModel.IsSidebarOpen);

        ViewModel.SettingsService.Update(s =>
        {
            s.IsSidebarOpen = effectiveSidebarOpen;
            s.SidebarWidth = _savedSidebarWidth;
            if (ViewModel.CurrentDocument is { } doc && !string.IsNullOrWhiteSpace(doc.FilePath))
            {
                s.LastOpenedFilePath = doc.FilePath;
                s.LastOpenedPageIndex = doc.CurrentPageIndex;
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

    private void RegisterSidebarTouchAndInertiaHandlers()
    {
        // Wide touch-only resize target around the sidebar border (leaves Mouse 100% on the 6 DIP SplitterContainer)
        MainSplitView.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnMainSplitViewTouchSplitterPointerPressed), handledEventsToo: true);
        MainSplitView.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMainSplitViewTouchSplitterPointerMoved), handledEventsToo: true);
        MainSplitView.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnMainSplitViewTouchSplitterPointerReleased), handledEventsToo: true);
        MainSplitView.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnMainSplitViewTouchSplitterPointerReleased), handledEventsToo: true);
        MainSplitView.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnMainSplitViewTouchSplitterPointerReleased), handledEventsToo: true);

        // Release any active touchpad/document scroll inertia when the user interacts with the sidebar
        SidebarPaneContainer.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(OnSidebarPanePointerEnteredOrInteracted), handledEventsToo: true);
        SidebarPaneContainer.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSidebarPanePointerEnteredOrInteracted), handledEventsToo: true);
        SidebarPaneContainer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnSidebarPanePointerEnteredOrInteracted), handledEventsToo: true);
    }

    private void OnSidebarPanePointerEnteredOrInteracted(object sender, PointerRoutedEventArgs e)
    {
        if (SidebarContentFrame.Content is WinPDF.Views.Sidebar.ThumbnailsTabPage thumbPage &&
            (e.Pointer.IsInContact || e.GetCurrentPoint(SidebarPaneContainer).Properties.MouseWheelDelta != 0))
        {
            thumbPage.ActivateManualSidebarScrollLock();
        }

        if (MainContentFrame.Content is DocumentView docView)
        {
            docView.StopDocumentInertiaIfActive();
        }
    }

    private void OnMainSplitViewTouchSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse ||
            !ViewModel.HasDocument ||
            !ViewModel.IsSidebarOpen ||
            ViewModel.IsSettingsOpen ||
            ViewModel.IsPresentationMode)
        {
            return;
        }

        double currentWidth = SidebarPaneContainer.Width > 0 ? SidebarPaneContainer.Width : _savedSidebarWidth;
        Windows.Foundation.Point pt = e.GetCurrentPoint(MainSplitView).Position;

        // Zone A: [currentWidth - 8, currentWidth + 20] (28 DIPs straddling the visual border at X = currentWidth) -> immediate resize capture
        if (pt.X >= currentWidth - 8.0 && pt.X <= currentWidth + 20.0)
        {
            _touchSplitterActivePointerId = e.Pointer.PointerId;
            _touchSplitterPendingPointerId = null;
            _touchSplitterStartX = pt.X;
            _touchSplitterStartY = pt.Y;
            _touchSplitterInitialWidth = currentWidth;
            MainSplitView.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        // Zone B: [currentWidth - 20, currentWidth - 8) -> directional disambiguation (promote to resize if dragged horizontally)
        if (pt.X >= currentWidth - 20.0 && pt.X < currentWidth - 8.0)
        {
            _touchSplitterPendingPointerId = e.Pointer.PointerId;
            _touchSplitterStartX = pt.X;
            _touchSplitterStartY = pt.Y;
            _touchSplitterInitialWidth = currentWidth;
        }
    }

    private void OnMainSplitViewTouchSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            return;
        }

        uint pointerId = e.Pointer.PointerId;
        if (_touchSplitterPendingPointerId == pointerId)
        {
            Windows.Foundation.Point pendingPt = e.GetCurrentPoint(MainSplitView).Position;
            double dx = pendingPt.X - _touchSplitterStartX;
            double dy = pendingPt.Y - _touchSplitterStartY;
            if (Math.Abs(dx) > 5.0 || Math.Abs(dy) > 5.0)
            {
                _touchSplitterPendingPointerId = null;
                if (Math.Abs(dx) > Math.Abs(dy))
                {
                    _touchSplitterActivePointerId = pointerId;
                    MainSplitView.CapturePointer(e.Pointer);
                }
                else
                {
                    return;
                }
            }
            else
            {
                return;
            }
        }

        if (_touchSplitterActivePointerId != pointerId)
        {
            return;
        }

        Windows.Foundation.Point pt = e.GetCurrentPoint(MainSplitView).Position;
        double targetWidth = Math.Clamp(_touchSplitterInitialWidth + (pt.X - _touchSplitterStartX), MinSidebarWidth, MaxSidebarWidth);
        ApplySidebarWidthImmediate(targetWidth);
        e.Handled = true;
    }

    private void OnMainSplitViewTouchSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        uint pointerId = e.Pointer.PointerId;
        if (_touchSplitterPendingPointerId == pointerId)
        {
            _touchSplitterPendingPointerId = null;
        }

        if (_touchSplitterActivePointerId == pointerId)
        {
            _touchSplitterActivePointerId = null;
            MainSplitView.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnSidebarSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        double currentWidth = SidebarPaneContainer.Width > 0 ? SidebarPaneContainer.Width : _savedSidebarWidth;
        double newWidth = Math.Clamp(currentWidth + e.HorizontalChange, MinSidebarWidth, MaxSidebarWidth);
        ApplySidebarWidthImmediate(newWidth);
    }

    private void ApplySidebarWidthImmediate(double newWidth)
    {
        _savedSidebarWidth = newWidth;
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
        bool shouldOpenPane = ViewModel.HasDocument && ViewModel.IsSidebarOpen;
        double paneWidth = _savedSidebarWidth > 0 ? _savedSidebarWidth : DefaultSidebarWidth;
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
                if (ViewModel.IsSidebarOpen && ViewModel.HasDocument)
                {
                    SidebarPaneTransform.TranslateX = 0.0;
                    SidebarPaneContainer.Opacity = 1.0;
                    MainContentBackdropTransform.TranslateX = paneWidth;
                    MainContentClipTransform.TranslateX = paneWidth;
                    if (SidebarContentFrame.Content is WinPDF.Views.Sidebar.ThumbnailsTabPage tp)
                    {
                        DispatcherQueue.TryEnqueue(tp.RefreshAfterVisibilityRestored);
                    }
                }
            };
        }
        else
        {
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
                if (!ViewModel.IsSidebarOpen || !ViewModel.HasDocument)
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
            MainContentBackdropBorder.BorderThickness = ViewModel.IsSidebarOpen ? new Thickness(1, 1, 0, 0) : new Thickness(0, 1, 0, 0);
            MainContentBackdropBorder.CornerRadius = ViewModel.IsSidebarOpen ? new CornerRadius(8, 0, 0, 0) : new CornerRadius(0);
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
            if (!_isInternalSidebarToggle)
            {
                _wasSidebarClosedByNarrowWindow = false;
            }

            UpdateSidebarLayout(animate: !_isInternalSidebarToggle);
        }
        else if (e.PropertyName == nameof(ShellViewModel.HasDocument))
        {
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
                    _isInternalSidebarToggle = true;
                    try
                    {
                        ViewModel.SetSidebarOpenInternal(false);
                    }
                    finally
                    {
                        _isInternalSidebarToggle = false;
                    }
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
            if (!ViewModel.IsPresentationMode && _sidebarOpenBeforeFullScreen)
            {
                _sidebarOpenBeforeFullScreen = false;
                _isInternalSidebarToggle = true;
                try
                {
                    ViewModel.SetSidebarOpenInternal(true);
                }
                finally
                {
                    _isInternalSidebarToggle = false;
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
