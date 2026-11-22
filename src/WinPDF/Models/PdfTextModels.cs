namespace WinPDF.Models;

/// <summary>
/// Represents an axis-aligned bounding rectangle in PDF user space (points, where 1 pt = 1/72 in, origin bottom-left).
/// </summary>
/// <param name="Left">Left coordinate in PDF user space.</param>
/// <param name="Bottom">Bottom coordinate in PDF user space.</param>
/// <param name="Right">Right coordinate in PDF user space.</param>
/// <param name="Top">Top coordinate in PDF user space.</param>
public readonly record struct PdfRect(float Left, float Bottom, float Right, float Top)
{
    /// <summary>
    /// Gets the width of the rectangle in PDF points.
    /// </summary>
    public float Width => Math.Max(0f, Right - Left);

    /// <summary>
    /// Gets the height of the rectangle in PDF points.
    /// </summary>
    public float Height => Math.Max(0f, Top - Bottom);

    /// <summary>
    /// Gets a value indicating whether the rectangle has zero or negligible area.
    /// </summary>
    public bool IsEmpty => Width <= 0.0001f || Height <= 0.0001f;

    /// <summary>
    /// Constructs a <see cref="PdfRect"/> from any two opposite corner coordinates.
    /// </summary>
    /// <param name="c0x">First corner X.</param>
    /// <param name="c0y">First corner Y.</param>
    /// <param name="c1x">Second corner X.</param>
    /// <param name="c1y">Second corner Y.</param>
    /// <returns>A normalized <see cref="PdfRect"/>.</returns>
    public static PdfRect FromCorners(double c0x, double c0y, double c1x, double c1y)
    {
        float left = (float)Math.Min(c0x, c1x);
        float bottom = (float)Math.Min(c0y, c1y);
        float right = (float)Math.Max(c0x, c1x);
        float top = (float)Math.Max(c0y, c1y);
        return new PdfRect(left, bottom, right, top);
    }

    /// <summary>
    /// Determines whether the rectangle contains the specified point in PDF user space, within an optional tolerance.
    /// </summary>
    /// <param name="x">Point X coordinate in PDF points.</param>
    /// <param name="y">Point Y coordinate in PDF points.</param>
    /// <param name="tolerancePt">Hit-testing tolerance in PDF points.</param>
    /// <returns><see langword="true"/> if the point is within the rectangle; otherwise, <see langword="false"/>.</returns>
    public bool Contains(double x, double y, double tolerancePt = 0.0)
    {
        return x >= (Left - tolerancePt) &&
               x <= (Right + tolerancePt) &&
               y >= (Bottom - tolerancePt) &&
               y <= (Top + tolerancePt);
    }
}

/// <summary>
/// Represents a single text search match found within a PDF document.
/// </summary>
/// <param name="PageIndex">The 0-based page index containing the match.</param>
/// <param name="MatchIndexOnPage">The 0-based match index among all occurrences on this page.</param>
/// <param name="StartCharIndex">The starting character index within the page text stream.</param>
/// <param name="CharCount">The number of characters comprising the match.</param>
/// <param name="Rects">The bounding rectangles in PDF user space covering the matched characters.</param>
public sealed record PdfSearchMatch(
    int PageIndex,
    int MatchIndexOnPage,
    int StartCharIndex,
    int CharCount,
    IReadOnlyList<PdfRect> Rects);

/// <summary>
/// Specifies how the active endpoint of an interactive text selection is clamped relative to the fixed anchor.
/// </summary>
public enum PdfSelectionAnchorClamp
{
    /// <summary>No directional clamp; selection may extend before or after the anchor.</summary>
    None = 0,

    /// <summary>Clamps the active endpoint at or before the anchor (used when dragging the start handle).</summary>
    AtOrBeforeAnchor = 1,

    /// <summary>Clamps the active endpoint at or after the anchor (used when dragging the end handle).</summary>
    AtOrAfterAnchor = 2,
}

/// <summary>
/// Represents the selected text span and bounding rectangles on a single page within a selection.
/// </summary>
/// <param name="PageIndex">The 0-based page index.</param>
/// <param name="StartCharIndex">The starting character index on this page.</param>
/// <param name="EndCharIndex">The ending character index on this page.</param>
/// <param name="Rects">The visual bounding rectangles on this page in PDF user space.</param>
/// <param name="SelectedText">The extracted UTF-16 text on this page.</param>
public sealed record PdfPageTextSelection(
    int PageIndex,
    int StartCharIndex,
    int EndCharIndex,
    IReadOnlyList<PdfRect> Rects,
    string SelectedText);

/// <summary>
/// Represents an active or completed text selection across one or more PDF pages.
/// </summary>
/// <param name="PageIndex">The 0-based starting page index where text is selected.</param>
/// <param name="StartCharIndex">The starting character index on the first selected page.</param>
/// <param name="EndCharIndex">The ending character index on the last selected page.</param>
/// <param name="Rects">The visual bounding rectangles in PDF user space (for the primary/first page).</param>
/// <param name="SelectedText">The extracted UTF-16 text string.</param>
/// <param name="AnchorCharIndex">The initial anchor character index where the drag selection started (-1 if unspecified).</param>
/// <param name="ActiveCharIndex">The active end character index currently under the pointer (-1 if unspecified).</param>
/// <param name="Pages">Optional per-page selection breakdown when selecting across one or more pages.</param>
/// <param name="AnchorPageIndex">The 0-based page index containing the fixed anchor (-1 if unspecified).</param>
/// <param name="ActivePageIndex">The 0-based page index containing the active pointer endpoint (-1 if unspecified).</param>
public sealed record PdfTextSelection(
    int PageIndex,
    int StartCharIndex,
    int EndCharIndex,
    IReadOnlyList<PdfRect> Rects,
    string SelectedText,
    int AnchorCharIndex = -1,
    int ActiveCharIndex = -1,
    IReadOnlyList<PdfPageTextSelection>? Pages = null,
    int AnchorPageIndex = -1,
    int ActivePageIndex = -1)
{
    /// <summary>
    /// Gets a value indicating whether the selection contains non-empty text and at least one bounding rectangle.
    /// </summary>
    public bool HasContent => !string.IsNullOrEmpty(SelectedText) &&
                              ((Pages is { Count: > 0 }) || Rects is { Count: > 0 });

    /// <summary>
    /// Gets the zero-based index of the first page included in the selection.
    /// </summary>
    public int StartPageIndex => Pages is { Count: > 0 } ? Pages[0].PageIndex : PageIndex;

    /// <summary>
    /// Gets the zero-based index of the last page included in the selection.
    /// </summary>
    public int LastPageIndex => Pages is { Count: > 0 } ? Pages[^1].PageIndex : PageIndex;

    /// <summary>
    /// Gets the starting character index on the first selected page.
    /// </summary>
    public int FirstPageStartCharIndex => Pages is { Count: > 0 } ? Pages[0].StartCharIndex : StartCharIndex;

    /// <summary>
    /// Gets the ending character index on the last selected page.
    /// </summary>
    public int LastPageEndCharIndex => Pages is { Count: > 0 } ? Pages[^1].EndCharIndex : EndCharIndex;

    /// <summary>
    /// Gets a value indicating whether this selection spans more than one page.
    /// </summary>
    public bool IsMultiPage => Pages is { Count: > 1 };

    /// <summary>
    /// Returns the bounding rectangles on <paramref name="pageIndex"/> in PDF user space, or an empty list if none.
    /// </summary>
    /// <param name="pageIndex">The zero-based page index.</param>
    /// <returns>The list of bounding rectangles on that page.</returns>
    public IReadOnlyList<PdfRect> GetRectsForPage(int pageIndex)
    {
        if (Pages is { Count: > 0 })
        {
            for (int i = 0; i < Pages.Count; i++)
            {
                if (Pages[i].PageIndex == pageIndex)
                {
                    return Pages[i].Rects;
                }
            }

            return Array.Empty<PdfRect>();
        }

        return PageIndex == pageIndex ? Rects : Array.Empty<PdfRect>();
    }

    /// <summary>
    /// Returns the first visual bounding rectangle of the selection and outputs its zero-based page index.
    /// </summary>
    /// <param name="pageIndex">Receives the zero-based page index containing the first rectangle, or -1 if empty.</param>
    /// <returns>The first bounding rectangle, or <see langword="null"/> if none exists.</returns>
    public PdfRect? GetFirstRect(out int pageIndex)
    {
        if (Pages is { Count: > 0 })
        {
            for (int i = 0; i < Pages.Count; i++)
            {
                PdfPageTextSelection pageSel = Pages[i];
                if (pageSel.Rects.Count > 0)
                {
                    pageIndex = pageSel.PageIndex;
                    return pageSel.Rects[0];
                }
            }

            pageIndex = -1;
            return null;
        }

        if (Rects is { Count: > 0 })
        {
            pageIndex = PageIndex;
            return Rects[0];
        }

        pageIndex = -1;
        return null;
    }

    /// <summary>
    /// Returns the last visual bounding rectangle of the selection and outputs its zero-based page index.
    /// </summary>
    /// <param name="pageIndex">Receives the zero-based page index containing the last rectangle, or -1 if empty.</param>
    /// <returns>The last bounding rectangle, or <see langword="null"/> if none exists.</returns>
    public PdfRect? GetLastRect(out int pageIndex)
    {
        if (Pages is { Count: > 0 })
        {
            for (int i = Pages.Count - 1; i >= 0; i--)
            {
                PdfPageTextSelection pageSel = Pages[i];
                if (pageSel.Rects.Count > 0)
                {
                    pageIndex = pageSel.PageIndex;
                    return pageSel.Rects[^1];
                }
            }

            pageIndex = -1;
            return null;
        }

        if (Rects is { Count: > 0 })
        {
            pageIndex = PageIndex;
            return Rects[^1];
        }

        pageIndex = -1;
        return null;
    }
}

/// <summary>
/// Specifies the type of destination targeted by an interactive PDF link.
/// </summary>
public enum PdfLinkKind
{
    /// <summary>No link target.</summary>
    None = 0,

    /// <summary>An internal destination navigating to a specific page index.</summary>
    InternalPage = 1,

    /// <summary>An external web or file URI.</summary>
    ExternalUri = 2,
}

/// <summary>
/// Represents a resolved clickable link target inside a PDF page.
/// </summary>
/// <param name="Kind">The destination kind (internal page or external URI).</param>
/// <param name="TargetPageIndex">The target 0-based page index (if <see cref="PdfLinkKind.InternalPage"/>); otherwise -1.</param>
/// <param name="Uri">The external target URI (if <see cref="PdfLinkKind.ExternalUri"/>); otherwise <see langword="null"/>.</param>
public readonly record struct PdfLinkTarget(
    PdfLinkKind Kind,
    int TargetPageIndex,
    string? Uri)
{
    /// <summary>
    /// Represents an invalid or empty link target.
    /// </summary>
    public static readonly PdfLinkTarget None = new(PdfLinkKind.None, -1, null);

    /// <summary>
    /// Gets a value indicating whether this link target represents a valid navigation action.
    /// </summary>
    public bool IsValid => Kind != PdfLinkKind.None &&
        (Kind == PdfLinkKind.InternalPage ? TargetPageIndex >= 0 : !string.IsNullOrWhiteSpace(Uri));
}

/// <summary>
/// Represents the result of hit-testing a point on a PDF page for interactive links and text selection.
/// </summary>
/// <param name="CharIndex">The 0-based character index under the pointer, or -1 if no character was hit.</param>
/// <param name="Link">The clickable link target under the pointer, or <see cref="PdfLinkTarget.None"/>.</param>
public readonly record struct PdfPointerHitResult(
    int CharIndex,
    PdfLinkTarget Link)
{
    /// <summary>
    /// Represents an empty hit test result hitting neither text nor links.
    /// </summary>
    public static readonly PdfPointerHitResult Empty = new(-1, PdfLinkTarget.None);
}
