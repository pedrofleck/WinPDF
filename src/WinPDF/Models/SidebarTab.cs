namespace WinPDF.Models;

/// <summary>
/// Selected tab in the navigation sidebar pane, matching GNOME Papers sidebar tabs.
/// </summary>
public enum SidebarTab
{
    /// <summary>
    /// Displays page thumbnail previews gallery.
    /// </summary>
    Thumbnails,

    /// <summary>
    /// Displays the document outline and bookmarks table of contents hierarchy.
    /// </summary>
    Outline,

    /// <summary>
    /// Displays the list of annotations and comments in the document.
    /// </summary>
    Annotations,

    /// <summary>
    /// Displays user-pinned favorite pages / bookmarks.
    /// </summary>
    Bookmarks,

    /// <summary>
    /// Displays embedded file attachments found in the PDF.
    /// </summary>
    Attachments
}
