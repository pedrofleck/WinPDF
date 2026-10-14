using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinPDF.ViewModels;

/// <summary>
/// View model representing an entry in the hierarchical document outline (bookmarks).
/// </summary>
public sealed partial class OutlineItemViewModel : ObservableObject
{
    /// <summary>
    /// Gets the display title of the bookmark item.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the zero-based target page index, or -1 when the bookmark has no resolvable destination.
    /// </summary>
    public int PageIndex { get; }

    /// <summary>
    /// Gets the 1-based target page display number (empty when the bookmark has no destination).
    /// </summary>
    public string DisplayPageNumber => PageIndex >= 0 ? (PageIndex + 1).ToString() : string.Empty;

    /// <summary>
    /// Gets the collection of nested child outline items.
    /// </summary>
    public ObservableCollection<OutlineItemViewModel> Children { get; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="OutlineItemViewModel"/> class.
    /// </summary>
    /// <param name="title">Display title of the bookmark.</param>
    /// <param name="pageIndex">Target page index.</param>
    public OutlineItemViewModel(string title, int pageIndex)
    {
        Title = title;
        PageIndex = pageIndex;
    }
}
