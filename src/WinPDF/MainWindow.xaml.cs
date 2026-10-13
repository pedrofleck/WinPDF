using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
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
    private bool _wasSidebarClosedByNarrowWindow;
    private bool _isInternalSidebarToggle;
    private int _previousSidebarIndex = -1;
    private RectInt32[]? _lastPassthroughRects;

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

        if (Content is FrameworkElement root)
        {
            root.Loaded += (_, _) =>
            {
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
        if (AppWindowTitleBar.IsCustomizationSupported() && AppWindow.TitleBar.RightInset > 0)
        {
            double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
            double rightInsetDip = AppWindow.TitleBar.RightInset / scale;
            if (Math.Abs(_lastRightInsetDip - rightInsetDip) > 0.5)
            {
                _lastRightInsetDip = rightInsetDip;
                AppTitleBar.Padding = new Thickness(12, 0, Math.Max(140, rightInsetDip), 0);
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
            ? (_settingsInteractiveElements ??= [SettingsBackBtn])
            : (_documentInteractiveElements ??=
            [
                SidebarToggleButton,
                OpenFileBtn,
                SearchBtn,
                PageNumberBoxControl,
                AnnotationToolsToggleBtn,
                DocumentMenuBtn,
                SettingsBtn
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
            DispatcherQueue.TryEnqueue(UpdateTitleBarNonClientRegions);
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsFullScreen))
        {
            UpdateFullScreenState(ViewModel.IsFullScreen);
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
                MainContentFrame.Navigate(typeof(DocumentView), ViewModel, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft });
            }

            DispatcherQueue.TryEnqueue(UpdateTitleBarNonClientRegions);
        }
        else if (e.PropertyName == nameof(ShellViewModel.SidebarTab))
        {
            UpdateSidebarFooterSelectorBarSelection();
        }
    }

    private void UpdateFullScreenState(bool isFullScreen)
    {
        AppWindow.SetPresenter(isFullScreen
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Default);
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

    private void OnPresentationClick(object sender, RoutedEventArgs e)
    {
        ViewModel.StartPresentation();
    }

    private void OnContinuousMenuClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SetViewMode(ViewMode.Continuous);
    }
}
