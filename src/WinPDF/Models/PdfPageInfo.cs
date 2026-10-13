namespace WinPDF.Models;

/// <summary>
/// Immutable metadata describing a single page of a PDF document.
/// </summary>
/// <param name="Index">Zero-based index of the page within the document.</param>
/// <param name="WidthPt">CropBox width in points (1/72 inch).</param>
/// <param name="HeightPt">CropBox height in points (1/72 inch).</param>
/// <param name="Rotation">Clockwise rotation angle in degrees (0, 90, 180, or 270).</param>
public sealed record PdfPageInfo(int Index, double WidthPt, double HeightPt, int Rotation)
{
    /// <summary>
    /// Gets the display width in DIPs (1/96 inch) at 100% zoom factor.
    /// </summary>
    public double DisplayWidthDip => (Rotation is 90 or 270 ? HeightPt : WidthPt) * (96.0 / 72.0);

    /// <summary>
    /// Gets the display height in DIPs (1/96 inch) at 100% zoom factor.
    /// </summary>
    public double DisplayHeightDip => (Rotation is 90 or 270 ? WidthPt : HeightPt) * (96.0 / 72.0);
}
