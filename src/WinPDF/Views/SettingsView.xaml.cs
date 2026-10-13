using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPDF.ViewModels;

namespace WinPDF.Views;

/// <summary>
/// Full-page Settings view containing Windows 11 card-styled preferences and app information.
/// </summary>
public sealed partial class SettingsView : Page
{
    private const double ResponsiveBreakpoint = 900.0;

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

    private void UpdateLayoutForWidth(double width)
    {
        if (width <= 0)
        {
            return;
        }

        if (width >= ResponsiveBreakpoint)
        {
            CardsColumn.MaxWidth = 760;
            SpacerColumn.Width = new GridLength(1, GridUnitType.Star);
            SpacerColumn.MinWidth = 48;
            AboutColumn.Width = GridLength.Auto;

            Grid.SetRow(AboutPanel, 0);
            Grid.SetColumn(AboutPanel, 2);
            AboutPanel.HorizontalAlignment = HorizontalAlignment.Right;
            AboutPanel.Margin = new Thickness(0);
            AboutPanel.Width = 280;
            AboutPanel.MaxWidth = 280;
        }
        else
        {
            CardsColumn.MaxWidth = double.PositiveInfinity;
            SpacerColumn.Width = new GridLength(0);
            SpacerColumn.MinWidth = 0;
            AboutColumn.Width = new GridLength(0);

            Grid.SetRow(AboutPanel, 1);
            Grid.SetColumn(AboutPanel, 0);
            AboutPanel.HorizontalAlignment = HorizontalAlignment.Left;
            AboutPanel.Margin = new Thickness(0, 32, 0, 0);
            AboutPanel.Width = double.NaN;
            AboutPanel.MaxWidth = 760;
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
}
