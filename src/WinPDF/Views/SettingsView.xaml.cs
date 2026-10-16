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
    private const double ResponsiveBreakpoint = 860.0;

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
}
