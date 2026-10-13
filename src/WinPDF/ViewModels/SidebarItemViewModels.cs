using CommunityToolkit.Mvvm.ComponentModel;
using WinPDF.Converters;

namespace WinPDF.ViewModels;

/// <summary>
/// View model representing a bookmarked page in the sidebar.
/// </summary>
public sealed partial class BookmarkItemViewModel : ObservableObject
{
    /// <summary>
    /// Gets the zero-based page index.
    /// </summary>
    public int PageIndex { get; }

    /// <summary>
    /// Gets the 1-based page number.
    /// </summary>
    public int PageNumber => PageIndex + 1;

    /// <summary>
    /// Gets the localized page display label (e.g. "Page 1" / "Página 1").
    /// </summary>
    public string DisplayText => UiHelpers.FormatBookmarkPage(PageNumber);

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkItemViewModel"/> class.
    /// </summary>
    /// <param name="pageIndex">Zero-based index of the page.</param>
    public BookmarkItemViewModel(int pageIndex)
    {
        PageIndex = pageIndex;
    }
}

/// <summary>
/// View model representing a document annotation item in the sidebar.
/// </summary>
public sealed partial class AnnotationItemViewModel : ObservableObject
{
    /// <summary>
    /// Gets the annotation description text.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AnnotationItemViewModel"/> class.
    /// </summary>
    /// <param name="text">Description or snippet of the annotation.</param>
    public AnnotationItemViewModel(string text)
    {
        Text = text;
    }
}

/// <summary>
/// View model representing an embedded file attachment item in the sidebar.
/// </summary>
public sealed partial class AttachmentItemViewModel : ObservableObject
{
    /// <summary>
    /// Gets the attachment file name.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AttachmentItemViewModel"/> class.
    /// </summary>
    /// <param name="fileName">File name of the embedded attachment.</param>
    public AttachmentItemViewModel(string fileName)
    {
        FileName = fileName;
    }
}
