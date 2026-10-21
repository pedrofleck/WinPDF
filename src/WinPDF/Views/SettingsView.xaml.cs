using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using WinPDF.Helpers;
using WinPDF.ViewModels;

namespace WinPDF.Views;

/// <summary>
/// Full-page Settings view containing Windows 11 card-styled preferences and app information.
/// </summary>
public sealed partial class SettingsView : Page
{
    private const double ResponsiveBreakpoint = 860.0;
    private Storyboard? _entranceStoryboard;

    /// <summary>
    /// Gets the settings view model.
    /// </summary>
    public SettingsViewModel ViewModel { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsView"/> class.
    /// </summary>
    public SettingsView()
    {
        ViewModel = App.CurrentApp.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();

        SizeChanged += OnSettingsViewSizeChanged;
        Loaded += OnSettingsViewLoaded;
    }

    private void OnSettingsViewLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayoutForWidth(ActualWidth);
    }

    private void OnSettingsViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayoutForWidth(e.NewSize.Width);
    }

    /// <summary>
    /// Plays the standard Windows 11 Fluent cascading downward entrance animation ("descendo")
    /// across the settings header, preference groups, and about panel.
    /// </summary>
    public void PlayEntranceAnimation()
    {
        if (SettingsHeaderTitle is null)
        {
            return;
        }

        _entranceStoryboard?.Stop();

        (UIElement Element, CompositeTransform Transform, int DelayMs)[] targets =
        [
            (SettingsHeaderTitle, SettingsHeaderTransform, 0),
            (AppearanceSectionPanel, AppearanceSectionTransform, 25),
            (ViewingSectionPanel, ViewingSectionTransform, 50),
            (AboutPanel, AboutPanelTransform, 45),
            (AnnotationsSectionPanel, AnnotationsSectionTransform, 75),
            (AdvancedSectionPanel, AdvancedSectionTransform, 90),
        ];

        var sb = new Storyboard();
        var moveEase = new ExponentialEase { Exponent = 5.0, EasingMode = EasingMode.EaseOut };
        var fadeEase = new CubicEase { EasingMode = EasingMode.EaseOut };

        foreach (var (element, transform, delayMs) in targets)
        {
            if (element is null || transform is null)
            {
                continue;
            }

            element.Opacity = 0.0;
            transform.TranslateY = -28.0;

            var translateAnim = new DoubleAnimation
            {
                From = -28.0,
                To = 0.0,
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                Duration = new Duration(TimeSpan.FromMilliseconds(320)),
                EasingFunction = moveEase
            };
            Storyboard.SetTarget(translateAnim, transform);
            Storyboard.SetTargetProperty(translateAnim, "TranslateY");
            sb.Children.Add(translateAnim);

            var opacityAnim = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = fadeEase
            };
            Storyboard.SetTarget(opacityAnim, element);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            sb.Children.Add(opacityAnim);
        }

        sb.Completed += (_, _) =>
        {
            foreach (var (element, transform, _) in targets)
            {
                if (element is not null && transform is not null)
                {
                    element.Opacity = 1.0;
                    transform.TranslateY = 0.0;
                }
            }
        };

        _entranceStoryboard = sb;
        sb.Begin();
    }

    private void UpdateLayoutForWidth(double width)
    {
        if (width <= 0)
        {
            return;
        }

        if (width >= ResponsiveBreakpoint)
        {
            CardsColumn.MaxWidth = double.PositiveInfinity;
            SpacerColumn.Width = new GridLength(48);
            SpacerColumn.MinWidth = 48;
            AboutColumn.Width = new GridLength(300);

            Grid.SetRow(AboutPanel, 1);
            Grid.SetColumn(AboutPanel, 2);
            AboutPanel.HorizontalAlignment = HorizontalAlignment.Right;
            AboutPanel.Margin = new Thickness(0);
            AboutPanel.Width = 300;
            AboutPanel.MaxWidth = 300;
        }
        else
        {
            CardsColumn.MaxWidth = double.PositiveInfinity;
            SpacerColumn.Width = new GridLength(0);
            SpacerColumn.MinWidth = 0;
            AboutColumn.Width = new GridLength(0);

            Grid.SetRow(AboutPanel, 2);
            Grid.SetColumn(AboutPanel, 0);
            AboutPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            AboutPanel.Margin = new Thickness(0, 36, 0, 0);
            AboutPanel.Width = double.NaN;
            AboutPanel.MaxWidth = double.PositiveInfinity;
        }
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is SettingsViewModel vm)
        {
            ViewModel = vm;
        }
        else if (e.Parameter is ShellViewModel)
        {
            ViewModel = App.CurrentApp.Services.GetRequiredService<SettingsViewModel>();
        }
    }

    private async void OnLicenseLinkClick(object sender, RoutedEventArgs e)
    {
        string title = ResourceStrings.Get(ResourceKeys.DialogLicenseTitle);
        string body = ReadBundledTextFile("LICENSE.txt");
        await ShowLegalNoticeDialogAsync(title, body);
    }

    private async void OnThirdPartyNoticesLinkClick(object sender, RoutedEventArgs e)
    {
        string title = ResourceStrings.Get(ResourceKeys.DialogThirdPartyTitle);
        string body = ReadBundledTextFile("THIRD-PARTY-NOTICES.txt");
        await ShowLegalNoticeDialogAsync(title, body);
    }

    private static string ReadBundledTextFile(string fileName)
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, fileName);
            if (System.IO.File.Exists(path))
            {
                return System.IO.File.ReadAllText(path);
            }
        }
        catch
        {
            // Fallback when file is missing or unreadable
        }

        return $"{fileName} not found.";
    }

    private async Task ShowLegalNoticeDialogAsync(string title, string contentText)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 8, -24, 0),
            Padding = new Thickness(0, 0, 20, 0),
            Content = new TextBlock
            {
                Text = contentText,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontSize = 13,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = title,
            Content = scrollViewer,
            CloseButtonText = ResourceStrings.Get(ResourceKeys.DialogCloseButton),
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }
}
