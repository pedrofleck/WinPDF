using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPDF.ViewModels;

namespace WinPDF.Views.Sidebar;

/// <summary>
/// Sidebar tab page presenting document page thumbnails.
/// </summary>
public sealed partial class ThumbnailsTabPage : Page
{
    /// <summary>
    /// Gets the shell view model.
    /// </summary>
    public ShellViewModel ViewModel { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ThumbnailsTabPage"/> class.
    /// </summary>
    public ThumbnailsTabPage()
    {
        ViewModel = App.CurrentApp.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ShellViewModel vm)
        {
            ViewModel = vm;
        }
    }

    private void OnThumbnailSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbnailsList.SelectedItem is ThumbnailItemViewModel thumbnail && ViewModel.CurrentDocument is not null)
        {
            ViewModel.CurrentDocument.GoToPage(thumbnail.PageIndex);
        }
    }

    private void OnThumbnailsContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is ThumbnailItemViewModel thumbnail)
        {
            if (args.InRecycleQueue)
            {
                thumbnail.NotifyRecycled();
            }
            else
            {
                double scale = XamlRoot?.RasterizationScale ?? 1.0;
                thumbnail.NotifyRealized(scale);
            }
        }
    }
}
