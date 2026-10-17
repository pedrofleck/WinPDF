using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPDF.ViewModels;

namespace WinPDF.Views.Sidebar;

/// <summary>
/// Sidebar tab page presenting bookmarks and quick navigation marks.
/// </summary>
public sealed partial class BookmarksTabPage : Page
{
    /// <summary>
    /// Gets the shell view model.
    /// </summary>
    public ShellViewModel ViewModel { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarksTabPage"/> class.
    /// </summary>
    public BookmarksTabPage()
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

    private void OnBookmarkItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookmarkItemViewModel bookmark && ViewModel.CurrentDocument is not null)
        {
            ViewModel.CurrentDocument.GoToPage(bookmark.PageIndex);
        }
    }

    private void OnRemoveBookmarkItemClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is null)
        {
            return;
        }

        if (sender is Microsoft.UI.Xaml.FrameworkElement { Tag: BookmarkItemViewModel bookmark })
        {
            ViewModel.CurrentDocument.RemoveBookmark(bookmark);
        }
        else if (sender is Microsoft.UI.Xaml.FrameworkElement { DataContext: BookmarkItemViewModel dcBookmark })
        {
            ViewModel.CurrentDocument.RemoveBookmark(dcBookmark);
        }
    }
}
