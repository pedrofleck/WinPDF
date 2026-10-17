namespace WinPDF.Models;

/// <summary>
/// Represents document metadata extracted from a PDF via <c>FPDF_GetMetaText</c> and file properties.
/// </summary>
/// <param name="Title">The document title, or <c>null</c> if not defined.</param>
/// <param name="Author">The document author, or <c>null</c> if not defined.</param>
/// <param name="Subject">The document subject, or <c>null</c> if not defined.</param>
/// <param name="Keywords">The document keywords, or <c>null</c> if not defined.</param>
/// <param name="Creator">The application that created the original document, or <c>null</c> if not defined.</param>
/// <param name="Producer">The PDF producer or conversion engine, or <c>null</c> if not defined.</param>
/// <param name="CreationDate">The raw creation date string, or <c>null</c> if not defined.</param>
/// <param name="ModificationDate">The raw modification date string, or <c>null</c> if not defined.</param>
/// <param name="PdfVersion">The PDF version string (e.g. "PDF 1.7"), or <c>null</c> if not detected.</param>
public sealed record PdfMetadata(
    string? Title,
    string? Author,
    string? Subject,
    string? Keywords,
    string? Creator,
    string? Producer,
    string? CreationDate,
    string? ModificationDate,
    string? PdfVersion)
{
    /// <summary>
    /// Gets an empty metadata record.
    /// </summary>
    public static readonly PdfMetadata Empty = new(null, null, null, null, null, null, null, null, null);
}
