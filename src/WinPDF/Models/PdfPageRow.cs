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
    /// Gets the document rotation angle applied to this row.
    /// </summary>
    public int Rotation { get; }

    /// <summary>
    /// Gets the display width of the left page in DIPs, considering document rotation.
    /// </summary>
    public double LeftPageWidth
    {
        get
        {
            int effRot = (LeftPage.Rotation + Rotation) % 360;
            return (effRot is 90 or 270 ? LeftPage.HeightPt : LeftPage.WidthPt) * (96.0 / 72.0);
        }
    }

    /// <summary>
    /// Gets the display height of the left page in DIPs, considering document rotation.
    /// </summary>
    public double LeftPageHeight
    {
        get
        {
            int effRot = (LeftPage.Rotation + Rotation) % 360;
            return (effRot is 90 or 270 ? LeftPage.WidthPt : LeftPage.HeightPt) * (96.0 / 72.0);
        }
    }

    /// <summary>
    /// Gets the display width of the right page in DIPs, or zero if none.
    /// </summary>
    public double RightPageWidth
    {
        get
        {
            if (RightPage is null)
            {
                return 0.0;
            }

            int effRot = (RightPage.Rotation + Rotation) % 360;
            return (effRot is 90 or 270 ? RightPage.HeightPt : RightPage.WidthPt) * (96.0 / 72.0);
        }
    }

    /// <summary>
    /// Gets the display height of the right page in DIPs, or zero if none.
    /// </summary>
    public double RightPageHeight
    {
        get
        {
            if (RightPage is null)
            {
                return 0.0;
            }

            int effRot = (RightPage.Rotation + Rotation) % 360;
            return (effRot is 90 or 270 ? RightPage.WidthPt : RightPage.HeightPt) * (96.0 / 72.0);
        }
    }

    /// <summary>
    /// Gets a value indicating whether presentation mode is active.
    /// </summary>
    public bool IsPresentationMode { get; }

    /// <summary>
    /// Gets a value indicating whether night mode is active for this row.
    /// </summary>
    public bool IsNightMode { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PdfPageRow"/> class.
    /// </summary>
    /// <param name="leftPage">The primary left page.</param>
    /// <param name="rightPage">The optional secondary right page.</param>
    /// <param name="isPresentationMode">Whether presentation mode is active.</param>
    /// <param name="rotation">Document view rotation angle in degrees.</param>
    /// <param name="isNightMode">Whether night reading mode is enabled.</param>
    public PdfPageRow(
        PdfPageInfo leftPage,
        PdfPageInfo? rightPage = null,
        bool isPresentationMode = false,
        int rotation = 0,
        bool isNightMode = false)
    {
        LeftPage = leftPage;
        RightPage = rightPage;
        IsPresentationMode = isPresentationMode;
        Rotation = rotation;
        IsNightMode = isNightMode;
    }
}
