namespace WinPDF.Models;

/// <summary>
/// Represents an extensible page-level edit committer (for future ink strokes, text notes, highlights,
/// geometric figures, and comments) that mutates a native <c>FPDF_PAGE</c> prior to <c>FPDF_SaveAsCopy</c>.
/// </summary>
public interface IPdfPageEditCommitter
{
    /// <summary>
    /// Gets the zero-based page index targeted by this edit operation.
    /// </summary>
    int PageIndex { get; }

    /// <summary>
    /// Applies the pending edits to the native PDFium document and page handles on the dedicated PDFium thread.
    /// </summary>
    /// <param name="documentHandle">Native <c>FPDF_DOCUMENT</c> pointer.</param>
    /// <param name="pageHandle">Native <c>FPDF_PAGE</c> pointer.</param>
    /// <returns><c>true</c> if page content streams were modified and <c>FPDFPage_GenerateContent</c> should be invoked.</returns>
    bool CommitToPage(nint documentHandle, nint pageHandle);
}

/// <summary>
/// Options controlling how a PDF document and its pending view/annotation edits are persisted via PDFium.
/// </summary>
/// <param name="UserRotationDegrees">Extra clockwise rotation in degrees (0, 90, 180, 270) applied to pages.</param>
/// <param name="PageEditCommitters">Optional collection of page edit committers (ink, text, shapes, comments).</param>
/// <param name="SaveFlags">PDFium save flags (0 = default standard save, 1 = incremental, 2 = no incremental).</param>
public sealed record PdfSaveOptions(
    int UserRotationDegrees = 0,
    IReadOnlyList<IPdfPageEditCommitter>? PageEditCommitters = null,
    uint SaveFlags = 0)
{
    /// <summary>
    /// Gets the default save options with no extra rotation or pending edit committers.
    /// </summary>
    public static readonly PdfSaveOptions Default = new();
}
