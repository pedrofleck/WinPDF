namespace WinPDF.Models;

/// <summary>
/// Represents a row of one or two pages displayed within the unified document view.
/// </summary>
public sealed class PdfPageRow
{
    /// <summary>
    /// Gets the primary (left) page of this row.
    /// </summary>
    public PdfPageInfo LeftPage { get; }

    /// <summary>
    /// Gets the secondary (right) page of this row, if double-page mode is active and present.
    /// </summary>
    public PdfPageInfo? RightPage { get; }

    /// <summary>
    /// Gets a value indicating whether this row has a second (right) page.
    /// </summary>
    public bool HasRightPage => RightPage is not null;

    /// <summary>
    /// Gets the display width of the right page in DIPs, or zero if none.
    /// </summary>
    public double RightPageWidth => RightPage?.DisplayWidthDip ?? 0.0;

    /// <summary>
    /// Gets the display height of the right page in DIPs, or zero if none.
    /// </summary>
    public double RightPageHeight => RightPage?.DisplayHeightDip ?? 0.0;

    /// <summary>
    /// Gets a value indicating whether presentation mode is active.
    /// </summary>
    public bool IsPresentationMode { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PdfPageRow"/> class.
    /// </summary>
    /// <param name="leftPage">The primary left page.</param>
    /// <param name="rightPage">The optional secondary right page.</param>
    /// <param name="isPresentationMode">Whether presentation mode is active.</param>
    public PdfPageRow(PdfPageInfo leftPage, PdfPageInfo? rightPage = null, bool isPresentationMode = false)
    {
        LeftPage = leftPage;
        RightPage = rightPage;
        IsPresentationMode = isPresentationMode;
    }
}
