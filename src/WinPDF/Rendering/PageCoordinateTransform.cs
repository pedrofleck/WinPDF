using System.Numerics;
using WinPDF.Models;

namespace WinPDF.Rendering;

/// <summary>
/// Bidirectional coordinate transforms between PDF user space (points, 72 DPI, origin bottom-left, Y up) and
/// page DIP space (96 DPI, origin top-left of the visible page, Y down), plus the content/viewport/pixel
/// chain of AGENTS.md §5.
/// </summary>
/// <remarks>
/// All matrices use the <see cref="Matrix3x2"/> row-vector convention (<c>v' = v · M</c>) so they compose in
/// application order. The rotation is the <em>effective</em> clockwise rotation: the page's intrinsic
/// <c>/Rotate</c> plus any user rotation applied on top (which is also what PDFium's <c>rotate</c> render
/// argument adds to the page's own orientation).
/// </remarks>
public readonly struct PageCoordinateTransform
{
    /// <summary>DIPs per PDF point at zoom 1.0: <c>96 / 72</c>.</summary>
    public const float DipsPerPoint = 96f / 72f;

    private readonly Matrix3x2 _pdfToPage;
    private readonly Matrix3x2 _pageToPdf;

    /// <summary>
    /// Initializes a transform for a page whose visible box is given in PDF user space.
    /// </summary>
    /// <param name="cropLeft"><c>cx0</c>: left edge of the visible box, in points.</param>
    /// <param name="cropBottom"><c>cy0</c>: bottom edge of the visible box, in points.</param>
    /// <param name="cropRight"><c>cx1</c>: right edge of the visible box, in points.</param>
    /// <param name="cropTop"><c>cy1</c>: top edge of the visible box, in points.</param>
    /// <param name="rotationDegrees">Effective clockwise rotation (any multiple of 90; normalized to 0/90/180/270).</param>
    public PageCoordinateTransform(float cropLeft, float cropBottom, float cropRight, float cropTop, int rotationDegrees)
    {
        CropLeft = cropLeft;
        CropBottom = cropBottom;
        CropRight = cropRight;
        CropTop = cropTop;
        Rotation = NormalizeRotation(rotationDegrees);

        const float k = DipsPerPoint;
        float cx0 = cropLeft;
        float cy0 = cropBottom;
        float cx1 = cropRight;
        float cy1 = cropTop;

        // Matrix3x2 layout: x' = x·M11 + y·M21 + M31 ; y' = x·M12 + y·M22 + M32.
        // Each case encodes the formulas of AGENTS.md §5.1 / §5.3.
        _pdfToPage = Rotation switch
        {
            // x_d = (x_p − cx0)·k            y_d = (cy1 − y_p)·k
            0 => new Matrix3x2(k, 0, 0, -k, -k * cx0, k * cy1),

            // x_d = (y_p − cy0)·k            y_d = (x_p − cx0)·k
            90 => new Matrix3x2(0, k, k, 0, -k * cy0, -k * cx0),

            // x_d = (cx1 − x_p)·k            y_d = (y_p − cy0)·k
            180 => new Matrix3x2(-k, 0, 0, k, k * cx1, -k * cy0),

            // x_d = (cy1 − y_p)·k            y_d = (cx1 − x_p)·k
            _ => new Matrix3x2(0, -k, -k, 0, k * cy1, k * cx1),
        };

        _pageToPdf = Matrix3x2.Invert(_pdfToPage, out Matrix3x2 inverse) ? inverse : Matrix3x2.Identity;
    }

    /// <summary>Gets <c>cx0</c>: left edge of the visible box in PDF user space (points).</summary>
    public float CropLeft { get; }

    /// <summary>Gets <c>cy0</c>: bottom edge of the visible box in PDF user space (points).</summary>
    public float CropBottom { get; }

    /// <summary>Gets <c>cx1</c>: right edge of the visible box in PDF user space (points).</summary>
    public float CropRight { get; }

    /// <summary>Gets <c>cy1</c>: top edge of the visible box in PDF user space (points).</summary>
    public float CropTop { get; }

    /// <summary>Gets the effective clockwise rotation in degrees (0, 90, 180 or 270).</summary>
    public int Rotation { get; }

    /// <summary>Gets the unrotated visible-box width in points.</summary>
    public float WidthPt => CropRight - CropLeft;

    /// <summary>Gets the unrotated visible-box height in points.</summary>
    public float HeightPt => CropTop - CropBottom;

    /// <summary>Gets the displayed page width in DIPs at zoom 1.0 (axes swap for 90°/270°).</summary>
    public float DisplayWidthDip => (Rotation is 90 or 270 ? HeightPt : WidthPt) * DipsPerPoint;

    /// <summary>Gets the displayed page height in DIPs at zoom 1.0 (axes swap for 90°/270°).</summary>
    public float DisplayHeightDip => (Rotation is 90 or 270 ? WidthPt : HeightPt) * DipsPerPoint;

    /// <summary>Gets the matrix <c>M_pdf→page</c> (PDF points → page DIPs).</summary>
    public Matrix3x2 PdfToPage => _pdfToPage;

    /// <summary>Gets the matrix <c>M_page→pdf</c>, the inverse of <see cref="PdfToPage"/> (page DIPs → PDF points).</summary>
    public Matrix3x2 PageToPdf => _pageToPdf;

    /// <summary>
    /// Creates the transform for a page described by <see cref="PdfPageInfo"/>.
    /// </summary>
    /// <param name="page">Page metadata (visible box and intrinsic rotation).</param>
    /// <param name="userRotationDegrees">Extra clockwise rotation applied by the user on top of <c>/Rotate</c>.</param>
    /// <returns>The page transform.</returns>
    public static PageCoordinateTransform FromPage(PdfPageInfo page, int userRotationDegrees = 0)
    {
        ArgumentNullException.ThrowIfNull(page);

        float left = (float)page.OriginXPt;
        float bottom = (float)page.OriginYPt;
        return new PageCoordinateTransform(
            left,
            bottom,
            left + (float)page.WidthPt,
            bottom + (float)page.HeightPt,
            page.Rotation + userRotationDegrees);
    }

    /// <summary>Converts a point from PDF user space (points) to page DIP space.</summary>
    /// <param name="pdfPoint">Point in PDF user space.</param>
    /// <returns>Point in page DIP space.</returns>
    public Vector2 ToPage(Vector2 pdfPoint) => Vector2.Transform(pdfPoint, _pdfToPage);

    /// <summary>Converts a point from page DIP space to PDF user space (points).</summary>
    /// <param name="pagePoint">Point in page DIP space (as reported by pointer events on the page element).</param>
    /// <returns>Point in PDF user space.</returns>
    public Vector2 ToPdf(Vector2 pagePoint) => Vector2.Transform(pagePoint, _pageToPdf);

    /// <summary>Builds <c>M_page→content = Translate(ox, oy)</c>.</summary>
    /// <param name="originX">Page origin X in content DIP space.</param>
    /// <param name="originY">Page origin Y in content DIP space.</param>
    /// <returns>The page→content matrix.</returns>
    public static Matrix3x2 CreatePageToContent(float originX, float originY)
        => Matrix3x2.CreateTranslation(originX, originY);

    /// <summary>Builds <c>M_content→viewport = Scale(Z) · Translate(−sx, −sy)</c>.</summary>
    /// <param name="zoomFactor"><c>ScrollViewer.ZoomFactor</c>.</param>
    /// <param name="horizontalOffset"><c>ScrollViewer.HorizontalOffset</c> (already in zoomed units).</param>
    /// <param name="verticalOffset"><c>ScrollViewer.VerticalOffset</c> (already in zoomed units).</param>
    /// <returns>The content→viewport matrix.</returns>
    public static Matrix3x2 CreateContentToViewport(float zoomFactor, float horizontalOffset, float verticalOffset)
        => Matrix3x2.CreateScale(zoomFactor) * Matrix3x2.CreateTranslation(-horizontalOffset, -verticalOffset);

    /// <summary>Builds <c>M_viewport→pixels = Scale(R)</c>.</summary>
    /// <param name="rasterizationScale"><c>XamlRoot.RasterizationScale</c>.</param>
    /// <returns>The viewport→pixels matrix.</returns>
    public static Matrix3x2 CreateViewportToPixels(float rasterizationScale)
        => Matrix3x2.CreateScale(rasterizationScale);

    /// <summary>
    /// Builds the full chain <c>M_pdf→pixels = M_pdf→page · M_page→content · M_content→viewport · M_viewport→pixels</c>.
    /// </summary>
    /// <param name="originX">Page origin X in content DIP space.</param>
    /// <param name="originY">Page origin Y in content DIP space.</param>
    /// <param name="zoomFactor"><c>ScrollViewer.ZoomFactor</c>.</param>
    /// <param name="horizontalOffset"><c>ScrollViewer.HorizontalOffset</c>.</param>
    /// <param name="verticalOffset"><c>ScrollViewer.VerticalOffset</c>.</param>
    /// <param name="rasterizationScale"><c>XamlRoot.RasterizationScale</c>.</param>
    /// <returns>The PDF→physical-pixels matrix.</returns>
    public Matrix3x2 CreatePdfToPixels(
        float originX,
        float originY,
        float zoomFactor,
        float horizontalOffset,
        float verticalOffset,
        float rasterizationScale)
        => _pdfToPage
           * CreatePageToContent(originX, originY)
           * CreateContentToViewport(zoomFactor, horizontalOffset, verticalOffset)
           * CreateViewportToPixels(rasterizationScale);

    /// <summary>
    /// Normalizes any angle to one of 0, 90, 180 or 270 (rounding to the nearest quarter turn).
    /// </summary>
    /// <param name="degrees">Angle in degrees, positive clockwise.</param>
    /// <returns>The normalized angle.</returns>
    public static int NormalizeRotation(int degrees)
    {
        int quarter = (int)Math.Round(degrees / 90.0);
        return ((quarter % 4) + 4) % 4 * 90;
    }
}
