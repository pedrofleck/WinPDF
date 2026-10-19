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
    /// Gets or sets the user-defined custom bookmark name. When empty, <see cref="DisplayText"/> falls back to the page number label.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(HasCustomTitle))]
    [NotifyPropertyChangedFor(nameof(PageBadgeText))]
    public partial string CustomTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this bookmark item is currently being renamed inline.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisplayMode))]
    public partial bool IsEditing { get; set; }

    /// <summary>
    /// Gets a value indicating whether this bookmark item is in normal display mode (not being edited).
    /// </summary>
    public bool IsDisplayMode => !IsEditing;

    /// <summary>
    /// Gets or sets the temporary text bound to the inline rename TextBox while <see cref="IsEditing"/> is true.
    /// </summary>
    [ObservableProperty]
    public partial string EditingText { get; set; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether a non-empty custom name has been assigned to this bookmark.
    /// </summary>
    public bool HasCustomTitle => !string.IsNullOrWhiteSpace(CustomTitle);

    /// <summary>
    /// Gets the primary label displayed for this bookmark (custom name if set; otherwise localized "Page X").
    /// </summary>
    public string DisplayText =>
        HasCustomTitle ? CustomTitle.Trim() : UiHelpers.FormatBookmarkPage(PageNumber);

    /// <summary>
    /// Gets the localized page label ("Page X" / "Página X") used for tooltips.
    /// </summary>
    public string PageBadgeText => UiHelpers.FormatBookmarkPage(PageNumber);

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkItemViewModel"/> class.
    /// </summary>
    /// <param name="pageIndex">Zero-based index of the page.</param>
    /// <param name="customTitle">Optional persisted custom bookmark name.</param>
    public BookmarkItemViewModel(int pageIndex, string? customTitle = null)
    {
        PageIndex = pageIndex;
        if (!string.IsNullOrWhiteSpace(customTitle))
        {
            CustomTitle = customTitle.Trim();
        }
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
