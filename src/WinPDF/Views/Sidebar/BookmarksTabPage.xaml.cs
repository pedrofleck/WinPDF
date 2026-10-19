using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinPDF.ViewModels;

namespace WinPDF.Views.Sidebar;

/// <summary>
/// Sidebar tab page presenting bookmarks and quick navigation marks with inline renaming.
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
        if (e.ClickedItem is BookmarkItemViewModel { IsEditing: false } bookmark && ViewModel.CurrentDocument is not null)
        {
            ViewModel.CurrentDocument.GoToPage(bookmark.PageIndex);
        }
    }

    private void OnBookmarkCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is DependencyObject card && FindDescendantByName<Button>(card, "RenameBookmarkItemButton") is { } renameBtn)
        {
            renameBtn.Opacity = 1.0;
        }
    }

    private void OnBookmarkCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is DependencyObject card && FindDescendantByName<Button>(card, "RenameBookmarkItemButton") is { } renameBtn)
        {
            renameBtn.Opacity = 0.0;
        }
    }

    private void OnRenameBookmarkItemClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is not { } doc)
        {
            return;
        }

        BookmarkItemViewModel? bookmark = (sender as FrameworkElement)?.DataContext as BookmarkItemViewModel
            ?? (sender as FrameworkElement)?.Tag as BookmarkItemViewModel;
        if (bookmark is null)
        {
            return;
        }

        foreach (BookmarkItemViewModel item in doc.Bookmarks)
        {
            if (!ReferenceEquals(item, bookmark) && item.IsEditing)
            {
                doc.RenameBookmark(item, item.EditingText);
            }
        }

        bookmark.EditingText = bookmark.DisplayText;
        bookmark.IsEditing = true;

        if (sender is DependencyObject btn)
        {
            DependencyObject? card = FindAncestorBorder(btn);
            if (card is not null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (FindDescendantByName<TextBox>(card, "BookmarkRenameTextBox") is { } textBox)
                    {
                        textBox.Focus(FocusState.Programmatic);
                        textBox.SelectAll();
                    }
                });
            }
        }
    }

    private void OnRenameTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not BookmarkItemViewModel bookmark)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            ViewModel.CurrentDocument?.RenameBookmark(bookmark, textBox.Text);
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            bookmark.IsEditing = false;
        }
    }

    private void OnRenameTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox &&
            textBox.DataContext is BookmarkItemViewModel { IsEditing: true } bookmark)
        {
            ViewModel.CurrentDocument?.RenameBookmark(bookmark, textBox.Text);
        }
    }

    private void OnRemoveBookmarkItemClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is null)
        {
            return;
        }

        if (sender is FrameworkElement { Tag: BookmarkItemViewModel bookmark })
        {
            ViewModel.CurrentDocument.RemoveBookmark(bookmark);
        }
        else if (sender is FrameworkElement { DataContext: BookmarkItemViewModel dcBookmark })
        {
            ViewModel.CurrentDocument.RemoveBookmark(dcBookmark);
        }
    }

    private static DependencyObject? FindAncestorBorder(DependencyObject element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is Border)
            {
                return current;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindDescendantByName<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name)
            {
                return fe;
            }

            T? nested = FindDescendantByName<T>(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
