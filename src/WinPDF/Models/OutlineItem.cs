namespace WinPDF.Models;

/// <summary>
/// Represents a node in the hierarchical table of contents (bookmarks / outline) of a PDF.
/// </summary>
/// <param name="Title">Display title of the bookmark entry.</param>
/// <param name="PageIndex">Target zero-based page index.</param>
/// <param name="Children">Child outline items nested under this entry.</param>
public sealed record OutlineItem(string Title, int PageIndex, IReadOnlyList<OutlineItem> Children);
