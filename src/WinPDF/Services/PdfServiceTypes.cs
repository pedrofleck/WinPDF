using WinPDF.Models;

namespace WinPDF.Services;

/// <summary>
/// Category of a PDFium failure, mapped from <c>FPDF_GetLastError</c>.
/// </summary>
public enum PdfiumErrorCode
{
    /// <summary>Unknown failure.</summary>
    Unknown,

    /// <summary>The file was not found or could not be opened (<c>FPDF_ERR_FILE</c>).</summary>
    File,

    /// <summary>The file is not a PDF or is corrupted (<c>FPDF_ERR_FORMAT</c>).</summary>
    Format,

    /// <summary>A password is required, or the supplied one is wrong (<c>FPDF_ERR_PASSWORD</c>).</summary>
    Password,

    /// <summary>The document uses an unsupported security handler (<c>FPDF_ERR_SECURITY</c>).</summary>
    Security,

    /// <summary>A page was not found or has invalid content (<c>FPDF_ERR_PAGE</c>).</summary>
    Page,

    /// <summary>A bitmap could not be allocated (request too large).</summary>
    OutOfMemory,
}

/// <summary>
/// Exception raised when a PDFium operation fails.
/// </summary>
public sealed class PdfiumException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PdfiumException"/> class.</summary>
    /// <param name="errorCode">Failure category.</param>
    /// <param name="message">Developer-facing description (English).</param>
    /// <param name="innerException">Underlying exception, if any.</param>
    public PdfiumException(PdfiumErrorCode errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Gets the failure category.</summary>
    public PdfiumErrorCode ErrorCode { get; }
}

/// <summary>
/// Immutable description of an opened document.
/// </summary>
/// <param name="DocumentId">Engine-assigned id used by every other <see cref="IPdfService"/> call.</param>
/// <param name="FilePath">Absolute path the document was opened from.</param>
/// <param name="Pages">Exact geometry of every page, in document order.</param>
public sealed record PdfDocumentInfo(long DocumentId, string FilePath, IReadOnlyList<PdfPageInfo> Pages)
{
    /// <summary>Gets the number of pages.</summary>
    public int PageCount => Pages.Count;
}

/// <summary>
/// Scheduling priority of a PDFium job; lower values run first.
/// </summary>
public enum PdfJobPriority
{
    /// <summary>Open/close/metadata/cleanup: FIFO and ahead of any rendering.</summary>
    Critical = 0,

    /// <summary>Pages or tiles intersecting the viewport (newest request first).</summary>
    Visible = 1,

    /// <summary>Neighbouring pages rendered speculatively (newest request first).</summary>
    Prefetch = 2,

    /// <summary>Sidebar thumbnails (newest request first).</summary>
    Thumbnail = 3,

    /// <summary>Anything else that can wait (newest request first).</summary>
    Background = 4,
}

/// <summary>
/// PDFium render flags (<c>FPDF_*</c> values of <c>fpdfview.h</c>).
/// </summary>
[Flags]
public enum PdfRenderFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>Render annotations stored in the file (<c>FPDF_ANNOT</c>).</summary>
    Annotations = 0x01,

    /// <summary>Use LCD-optimized text rendering (<c>FPDF_LCD_TEXT</c>); off by default.</summary>
    LcdText = 0x02,

    /// <summary>Render in grayscale (<c>FPDF_GRAYSCALE</c>).</summary>
    Grayscale = 0x08,

    /// <summary>Limit the image cache to reduce memory (<c>FPDF_RENDER_LIMITEDIMAGECACHE</c>).</summary>
    LimitedImageCache = 0x200,

    /// <summary>Render for printing (<c>FPDF_PRINTING</c>).</summary>
    Printing = 0x0800,
}

/// <summary>
/// Options describing a native GDI print job executed on the dedicated PDFium worker thread.
/// </summary>
/// <param name="PrinterDc">Native printer device context (<c>HDC</c>) returned by <c>PrintDlgExW</c>. Always released via <c>DeleteDC</c> when the job finishes.</param>
/// <param name="DocumentTitle">Document title shown in the Windows print spooler queue.</param>
/// <param name="PageIndices">Zero-based page indices to print, in print order.</param>
/// <param name="Copies">Number of copies to print.</param>
/// <param name="PageUserRotations">Optional per-page extra user rotation in degrees (0, 90, 180, 270) keyed by zero-based page index.</param>
/// <param name="UserRotationDegrees">Default extra user rotation in degrees (0, 90, 180, 270) applied to pages.</param>
public sealed record PdfPrintJobOptions(
    nint PrinterDc,
    string DocumentTitle,
    IReadOnlyList<int> PageIndices,
    int Copies,
    IReadOnlyDictionary<int, int>? PageUserRotations = null,
    int UserRotationDegrees = 0);

/// <summary>
/// Describes one <c>FPDF_RenderPageBitmap</c> call: the native bitmap to allocate and where the page lands in it.
/// </summary>
/// <param name="DocumentId">Engine-assigned document id.</param>
/// <param name="PageIndex">Zero-based page index.</param>
/// <param name="BitmapWidth">Width in pixels of the native bitmap (the full page, or one tile).</param>
/// <param name="BitmapHeight">Height in pixels of the native bitmap.</param>
/// <param name="PageWidthPx">Width in pixels of the <em>whole</em> page rasterization (<c>size_x</c>).</param>
/// <param name="PageHeightPx">Height in pixels of the <em>whole</em> page rasterization (<c>size_y</c>).</param>
/// <param name="OffsetX">Page origin X inside the bitmap (<c>start_x</c>); negative for tiles.</param>
/// <param name="OffsetY">Page origin Y inside the bitmap (<c>start_y</c>); negative for tiles.</param>
/// <param name="Rotation">User rotation in degrees (0, 90, 180, 270) applied on top of the page's <c>/Rotate</c>.</param>
/// <param name="Flags">PDFium render flags.</param>
public readonly record struct PdfRenderRequest(
    long DocumentId,
    int PageIndex,
    int BitmapWidth,
    int BitmapHeight,
    int PageWidthPx,
    int PageHeightPx,
    int OffsetX,
    int OffsetY,
    int Rotation,
    PdfRenderFlags Flags)
{
    /// <summary>
    /// Creates a request that rasterizes a whole page in a single call (Standard Reading, zoom ≤ 1.5).
    /// </summary>
    /// <param name="documentId">Engine-assigned document id.</param>
    /// <param name="page">Page metadata.</param>
    /// <param name="zoom">Quantized zoom <c>Zq</c>.</param>
    /// <param name="rasterizationScale"><c>XamlRoot.RasterizationScale</c>.</param>
    /// <param name="userRotationDegrees">User rotation on top of <c>/Rotate</c>.</param>
    /// <param name="flags">PDFium render flags.</param>
    /// <returns>The render request.</returns>
    public static PdfRenderRequest ForFullPage(
        long documentId,
        PdfPageInfo page,
        double zoom,
        double rasterizationScale,
        int userRotationDegrees = 0,
        PdfRenderFlags flags = PdfRenderFlags.Annotations)
    {
        (int width, int height) = Rendering.RenderGeometry.GetPagePixelSize(page, userRotationDegrees, zoom, rasterizationScale);
        return new PdfRenderRequest(documentId, page.Index, width, height, width, height, 0, 0, userRotationDegrees, flags);
    }

    /// <summary>
    /// Creates a request that rasterizes one 512×512 tile (clipped at page edges) of a magnified page.
    /// </summary>
    /// <param name="documentId">Engine-assigned document id.</param>
    /// <param name="page">Page metadata.</param>
    /// <param name="zoom">Quantized zoom <c>Zq</c>.</param>
    /// <param name="rasterizationScale"><c>XamlRoot.RasterizationScale</c>.</param>
    /// <param name="tileX">Tile column.</param>
    /// <param name="tileY">Tile row.</param>
    /// <param name="userRotationDegrees">User rotation on top of <c>/Rotate</c>.</param>
    /// <param name="flags">PDFium render flags.</param>
    /// <returns>The render request.</returns>
    public static PdfRenderRequest ForTile(
        long documentId,
        PdfPageInfo page,
        double zoom,
        double rasterizationScale,
        int tileX,
        int tileY,
        int userRotationDegrees = 0,
        PdfRenderFlags flags = PdfRenderFlags.Annotations)
    {
        (int pageWidth, int pageHeight) = Rendering.RenderGeometry.GetPagePixelSize(page, userRotationDegrees, zoom, rasterizationScale);
        (int left, int top, int width, int height) = Rendering.RenderGeometry.GetTileRect(pageWidth, pageHeight, tileX, tileY);
        return new PdfRenderRequest(documentId, page.Index, width, height, pageWidth, pageHeight, -left, -top, userRotationDegrees, flags);
    }

    /// <summary>
    /// Creates a request that rasterizes a whole page into an exact pixel box (sidebar thumbnails).
    /// </summary>
    /// <param name="documentId">Engine-assigned document id.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="widthPx">Thumbnail width in pixels.</param>
    /// <param name="heightPx">Thumbnail height in pixels.</param>
    /// <param name="userRotationDegrees">User rotation in degrees applied on top of the page's intrinsic <c>/Rotate</c>.</param>
    /// <returns>The render request.</returns>
    public static PdfRenderRequest ForThumbnail(long documentId, int pageIndex, int widthPx, int heightPx, int userRotationDegrees = 0)
        => new(documentId, pageIndex, widthPx, heightPx, widthPx, heightPx, 0, 0, userRotationDegrees, PdfRenderFlags.Annotations);
}
