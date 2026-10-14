using System.ComponentModel;

namespace WinPDF.Models;

/// <summary>
/// Immutable metadata describing a single page of a PDF document.
/// </summary>
/// <param name="Index">Zero-based index of the page within the document.</param>
/// <param name="WidthPt">Visible-box (CropBox ∩ MediaBox) width in points (1/72 inch), before rotation.</param>
/// <param name="HeightPt">Visible-box (CropBox ∩ MediaBox) height in points (1/72 inch), before rotation.</param>
/// <param name="Rotation">Intrinsic <c>/Rotate</c> of the page: clockwise angle in degrees (0, 90, 180, or 270).</param>
/// <param name="OriginXPt">Left edge (<c>cx0</c>) of the visible box in PDF user space, in points.</param>
/// <param name="OriginYPt">Bottom edge (<c>cy0</c>) of the visible box in PDF user space, in points.</param>
/// <param name="DocumentId">Engine-assigned document identifier, or 0 if unassigned.</param>
public sealed record PdfPageInfo(
    int Index,
    double WidthPt,
    double HeightPt,
    int Rotation,
    double OriginXPt = 0,
    double OriginYPt = 0,
    long DocumentId = 0) : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Gets the display width in DIPs (1/96 inch) at 100% zoom factor.
    /// </summary>
    public double DisplayWidthDip => (Rotation is 90 or 270 ? HeightPt : WidthPt) * (96.0 / 72.0);

    /// <summary>
    /// Gets the display height in DIPs (1/96 inch) at 100% zoom factor.
    /// </summary>
    public double DisplayHeightDip => (Rotation is 90 or 270 ? WidthPt : HeightPt) * (96.0 / 72.0);
}
