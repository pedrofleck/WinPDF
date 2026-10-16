using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
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
    private const double MinSidebarWidth = 180.0;
    private const double MaxSidebarWidth = 480.0;
    private const double DefaultSidebarWidth = 260.0;

    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private double _savedSidebarWidth = DefaultSidebarWidth;
    private bool _wasSidebarClosedByNarrowWindow;
    private bool _isInternalSidebarToggle;
    private int _previousSidebarIndex = -1;
    private RectInt32[]? _lastPassthroughRects;

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

        // Custom TitleBar integration with Mica
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

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

        // Window size and layout events
        SizeChanged += OnMainWindowSizeChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarNonClientRegions();
        SettingsBackBtn.SizeChanged += (_, _) => UpdateTitleBarNonClientRegions();
        ExitFullScreenBtn.SizeChanged += (_, _) => UpdateTitleBarNonClientRegions();

        MainSplitView.OpenPaneLength = _savedSidebarWidth;
        MainSplitView.PaneOpening += (_, _) =>
        {
            if (!ViewModel.IsSettingsOpen)
            {
                MainContentContainer.CornerRadius = new CornerRadius(8, 0, 0, 0);
                MainContentContainer.BorderThickness = new Thickness(1, 1, 0, 0);
            }
        };
        MainSplitView.PaneClosing += (_, _) =>
        {
            MainContentContainer.CornerRadius = new CornerRadius(0);
            MainContentContainer.BorderThickness = new Thickness(0, 1, 0, 0);
        };

        AppWindow.Changed += OnAppWindowChanged;

        if (Content is FrameworkElement root)
        {
            root.Loaded += (_, _) =>
            {
                SetInitialWindowBounds();
                UpdateTitleBarTheme();
                UpdateTitleBarInsets();
                UpdateTitleBarNonClientRegions();
                UpdateSidebarLayout();

                // Initial frame navigation
                MainContentFrame.Navigate(typeof(DocumentView), ViewModel);
                UpdateSidebarFooterSelectorBarSelection();

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

    private void OnMainWindowSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
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
                    ViewModel.IsSidebarOpen = false;
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
                    ViewModel.IsSidebarOpen = true;
                }
                finally
                {
                    _isInternalSidebarToggle = false;
                }
            }
        }
    }

    private void OnSidebarToggleClick(object sender, RoutedEventArgs e)
    {
        // Manual user interaction clears automatic narrow memory
        _wasSidebarClosedByNarrowWindow = false;
    }

    private void OnSidebarSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        double currentWidth = MainSplitView.OpenPaneLength > 0 ? MainSplitView.OpenPaneLength : _savedSidebarWidth;
        double newWidth = Math.Clamp(currentWidth + e.HorizontalChange, MinSidebarWidth, MaxSidebarWidth);
        _savedSidebarWidth = newWidth;
        MainSplitView.OpenPaneLength = newWidth;
    }

    private void UpdateSidebarLayout()
    {
        if (ViewModel.IsSettingsOpen)
        {
            MainSplitView.IsPaneOpen = false;
            MainContentContainer.CornerRadius = new CornerRadius(0);
            MainContentContainer.BorderThickness = new Thickness(0, 1, 0, 0);
        }
        else
        {
            MainSplitView.IsPaneOpen = ViewModel.IsSidebarOpen;
            MainContentContainer.CornerRadius = ViewModel.IsSidebarOpen ? new CornerRadius(8, 0, 0, 0) : new CornerRadius(0);
            MainContentContainer.BorderThickness = ViewModel.IsSidebarOpen ? new Thickness(1, 1, 0, 0) : new Thickness(0, 1, 0, 0);
        }
    }

    private double _lastRightInsetDip = -1;
    private FrameworkElement[]? _documentInteractiveElements;
    private FrameworkElement[]? _settingsInteractiveElements;
    private readonly List<RectInt32> _reusablePassthroughRects = new(8);

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

    private static bool AreRectsEqualList(RectInt32[] a, List<RectInt32> b)
    {
        if (a.Length != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].X != b[i].X || a[i].Y != b[i].Y || a[i].Width != b[i].Width || a[i].Height != b[i].Height)
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateTitleBarNonClientRegions()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported() || Content?.XamlRoot is null)
        {
            return;
        }

        double scale = Content.XamlRoot.RasterizationScale;
        _reusablePassthroughRects.Clear();

        FrameworkElement[] interactiveElements = ViewModel.IsSettingsOpen
            ? (_settingsInteractiveElements ??= [SettingsBackBtn, ExitFullScreenBtn])
            : (_documentInteractiveElements ??=
            [
                SidebarToggleButton,
                OpenFileBtn,
                SearchBtn,
                AnnotationToolsToggleBtn,
                DocumentMenuBtn,
                SettingsBtn,
                ExitFullScreenBtn
            ]);

        foreach (var element in interactiveElements)
        {
            if (element.IsLoaded && element.Visibility == Visibility.Visible && element.ActualWidth > 0 && element.ActualHeight > 0)
            {
                var transform = element.TransformToVisual(null);
                var bounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
                _reusablePassthroughRects.Add(new RectInt32(
                    (int)Math.Round(bounds.X * scale),
                    (int)Math.Round(bounds.Y * scale),
                    (int)Math.Round(bounds.Width * scale),
                    (int)Math.Round(bounds.Height * scale)));
            }
        }

        if (_lastPassthroughRects is not null && AreRectsEqualList(_lastPassthroughRects, _reusablePassthroughRects))
        {
            return;
        }

        var newRects = _reusablePassthroughRects.ToArray();
        try
        {
            var nonClientInput = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            nonClientInput.SetRegionRects(NonClientRegionKind.Passthrough, newRects);
            _lastPassthroughRects = newRects;
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
            RootGrid.RequestedTheme = ViewModel.CurrentTheme switch
            {
                AppThemePreference.Light => ElementTheme.Light,
                AppThemePreference.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
            UpdateTitleBarTheme();
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsSidebarOpen))
        {
            if (!_isInternalSidebarToggle)
            {
                _wasSidebarClosedByNarrowWindow = false;
            }

            UpdateSidebarLayout();
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsAnnotationToolbarOpen))
        {
            DispatcherQueue.TryEnqueue(UpdateTitleBarNonClientRegions);
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsSettingsOpen))
        {
            UpdateSidebarLayout();
            if (ViewModel.IsSettingsOpen)
            {
                MainContentFrame.Navigate(typeof(SettingsView), ViewModel, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });
            }
            else
            {
                if (MainContentFrame.CanGoBack)
                {
                    MainContentFrame.GoBack(new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft });
                }
                else
                {
                    MainContentFrame.Navigate(typeof(DocumentView), ViewModel, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft });
                }
            }

            DispatcherQueue.TryEnqueue(UpdateTitleBarNonClientRegions);
        }
        else if (e.PropertyName == nameof(ShellViewModel.SidebarTab))
        {
            UpdateSidebarFooterSelectorBarSelection();
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
                        ViewModel.IsSidebarOpen = false;
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
                    ViewModel.IsSidebarOpen = true;
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

        bool isDark = RootGrid.RequestedTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark
        };

        titleBar.ButtonForegroundColor = isDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        titleBar.ButtonHoverForegroundColor = isDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        titleBar.ButtonHoverBackgroundColor = isDark ? Windows.UI.Color.FromArgb(24, 255, 255, 255) : Windows.UI.Color.FromArgb(16, 0, 0, 0);
        titleBar.ButtonPressedBackgroundColor = isDark ? Windows.UI.Color.FromArgb(40, 255, 255, 255) : Windows.UI.Color.FromArgb(28, 0, 0, 0);
        titleBar.ButtonInactiveForegroundColor = isDark ? Windows.UI.Color.FromArgb(128, 255, 255, 255) : Windows.UI.Color.FromArgb(128, 0, 0, 0);
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
    }

    private void OnSidebarFooterSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is null)
        {
            return;
        }

        int currentSelectedIndex = sender.Items.IndexOf(sender.SelectedItem);
        Type pageType = sender.SelectedItem switch
        {
            var item when item == ThumbnailsSelectorItem => typeof(ThumbnailsTabPage),
            var item when item == OutlineSelectorItem => typeof(OutlineTabPage),
            var item when item == AnnotationsSelectorItem => typeof(AnnotationsTabPage),
            var item when item == BookmarksSelectorItem => typeof(BookmarksTabPage),
            var item when item == AttachmentsSelectorItem => typeof(AttachmentsTabPage),
            _ => typeof(ThumbnailsTabPage)
        };

        SidebarTab targetTab = sender.SelectedItem switch
        {
            var item when item == ThumbnailsSelectorItem => SidebarTab.Thumbnails,
            var item when item == OutlineSelectorItem => SidebarTab.Outline,
            var item when item == AnnotationsSelectorItem => SidebarTab.Annotations,
            var item when item == BookmarksSelectorItem => SidebarTab.Bookmarks,
            var item when item == AttachmentsSelectorItem => SidebarTab.Attachments,
            _ => SidebarTab.Thumbnails
        };

        if (ViewModel.SidebarTab != targetTab)
        {
            ViewModel.SetSidebarTab(targetTab);
        }

        if (_previousSidebarIndex == -1)
        {
            SidebarContentFrame.Navigate(pageType, ViewModel);
        }
        else if (currentSelectedIndex != _previousSidebarIndex)
        {
            var effect = currentSelectedIndex - _previousSidebarIndex > 0
                ? SlideNavigationTransitionEffect.FromRight
                : SlideNavigationTransitionEffect.FromLeft;
            SidebarContentFrame.Navigate(pageType, ViewModel, new SlideNavigationTransitionInfo { Effect = effect });
        }

        _previousSidebarIndex = currentSelectedIndex;
    }

    private void UpdateSidebarFooterSelectorBarSelection()
    {
        if (SidebarFooterSelectorBar is null)
        {
            return;
        }

        SelectorBarItem targetItem = ViewModel.SidebarTab switch
        {
            SidebarTab.Thumbnails => ThumbnailsSelectorItem,
            SidebarTab.Outline => OutlineSelectorItem,
            SidebarTab.Annotations => AnnotationsSelectorItem,
            SidebarTab.Bookmarks => BookmarksSelectorItem,
            SidebarTab.Attachments => AttachmentsSelectorItem,
            _ => ThumbnailsSelectorItem
        };

        if (SidebarFooterSelectorBar.SelectedItem != targetItem)
        {
            SidebarFooterSelectorBar.SelectedItem = targetItem;
        }
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
