using WinPDF.Models;

namespace WinPDF.Services;

/// <summary>
/// Abstraction over the PDF engine. Every method is thread-safe and asynchronous: the work is executed on the
/// single dedicated PDFium thread and cancelled jobs are discarded without touching PDFium.
/// </summary>
public interface IPdfService : IDisposable
{
    /// <summary>
    /// Opens a PDF file and reads the exact geometry of every page.
    /// </summary>
    /// <param name="filePath">Absolute path of the file.</param>
    /// <param name="password">Optional password for encrypted documents.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The opened document description.</returns>
    /// <exception cref="PdfiumException">The file cannot be opened.</exception>
    Task<PdfDocumentInfo> OpenDocumentAsync(string filePath, string? password = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes a document and releases its native handles and cached pages. Pending jobs for it are cancelled.
    /// </summary>
    /// <param name="documentId">Document id returned by <see cref="OpenDocumentAsync"/>.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task completing when the document is closed.</returns>
    Task CloseDocumentAsync(long documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dynamically sets the page handle LRU cache capacity on the dedicated PDFium thread.
    /// </summary>
    /// <param name="capacity">Maximum number of pages to keep in the PDFium handle cache.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task completing when the capacity is updated.</returns>
    Task SetPageCacheCapacityAsync(int capacity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the hierarchical table of contents (bookmarks).
    /// </summary>
    /// <param name="documentId">Document id.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The top-level outline items (empty when the document has no outline).</returns>
    Task<IReadOnlyList<OutlineItem>> GetOutlineAsync(long documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the names of the files embedded in the document.
    /// </summary>
    /// <param name="documentId">Document id.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The attachment file names (empty when there are none).</returns>
    Task<IReadOnlyList<string>> GetAttachmentNamesAsync(long documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads document metadata properties (Title, Author, Subject, Creator, Producer, Dates, etc.).
    /// </summary>
    /// <param name="documentId">Document identifier.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The metadata record extracted from the PDF.</returns>
    Task<PdfMetadata> GetDocumentMetadataAsync(long documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the specified document (including any committed page rotations, ink strokes, text, shapes, or comments)
    /// to <paramref name="targetFilePath"/> using PDFium's <c>FPDF_SaveAsCopy</c> pipeline with atomic temp-file replacement.
    /// </summary>
    /// <param name="documentId">Document identifier.</param>
    /// <param name="targetFilePath">Destination file path on disk.</param>
    /// <param name="options">Optional save options and page edit committers.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that completes when the file has been written and flushed to disk.</returns>
    Task SaveDocumentAsync(
        long documentId,
        string targetFilePath,
        PdfSaveOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Prints the specified document pages directly to a native Win32 printer <c>HDC</c> using PDFium's
    /// vector GDI renderer (<c>FPDF_RenderPage</c>) on the dedicated worker thread.
    /// Always releases <see cref="PdfPrintJobOptions.PrinterDc"/> via <c>DeleteDC</c> when finished.
    /// </summary>
    /// <param name="documentId">Document identifier.</param>
    /// <param name="options">Print job parameters including the native printer device context and page indices.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns><c>true</c> if the print job completed and was spooled; otherwise <c>false</c>.</returns>
    Task<bool> PrintDocumentAsync(
        long documentId,
        PdfPrintJobOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches all pages of the specified document for <paramref name="query"/> on the dedicated PDFium worker thread,
    /// extracting the bounding PDF user-space rectangles for every match.
    /// </summary>
    /// <param name="documentId">Document identifier.</param>
    /// <param name="query">Text string to search for.</param>
    /// <param name="matchCase">Whether the search should be case-sensitive (<c>FPDF_MATCHCASE</c>).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>Ordered list of all matches found across the document.</returns>
    Task<IReadOnlyList<PdfSearchMatch>> SearchDocumentAsync(
        long documentId,
        string query,
        bool matchCase = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hit-tests a point in PDF user space (points) on <paramref name="pageIndex"/> for both clickable links
    /// (<c>FPDFLink_GetLinkAtPoint</c> and web links) and selectable text characters (<c>FPDFText_GetCharIndexAtPos</c>).
    /// </summary>
    /// <param name="documentId">Document identifier.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="pdfX">X coordinate in PDF user space (points).</param>
    /// <param name="pdfY">Y coordinate in PDF user space (points).</param>
    /// <param name="tolerancePt">Character hit-test tolerance in points.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The combined character index and link target at the point.</returns>
    Task<PdfPointerHitResult> HitTestPagePointAsync(
        long documentId,
        int pageIndex,
        double pdfX,
        double pdfY,
        double tolerancePt = 5.0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves any clickable internal or external link at the specified point in PDF user space (points).
    /// </summary>
    /// <param name="documentId">Document identifier.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="pdfX">X coordinate in PDF user space (points).</param>
    /// <param name="pdfY">Y coordinate in PDF user space (points).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The resolved link target, or <see cref="PdfLinkTarget.None"/> if no link is present.</returns>
    Task<PdfLinkTarget> GetLinkAtPointAsync(
        long documentId,
        int pageIndex,
        double pdfX,
        double pdfY,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an interactive text drag selection anchored at <paramref name="anchorCharIndex"/> (or resolved from
    /// <c>(anchorPdfX, anchorPdfY)</c> when <paramref name="anchorCharIndex"/> is negative) to the character nearest
    /// <c>(currentPdfX, currentPdfY)</c> on <paramref name="pageIndex"/>.
    /// Returns <see langword="null"/> if the resolved end character index matches <paramref name="previousEndCharIndex"/>
    /// (allowing the caller to skip redundant redraws) or if no character could be resolved.
    /// </summary>
    /// <param name="documentId">Document identifier.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="anchorCharIndex">Zero-based character index where the drag started, or -1 to resolve from <c>(anchorPdfX, anchorPdfY)</c>.</param>
    /// <param name="anchorPdfX">Anchor pointer X coordinate in PDF user space (points).</param>
    /// <param name="anchorPdfY">Anchor pointer Y coordinate in PDF user space (points).</param>
    /// <param name="currentPdfX">Current pointer X coordinate in PDF user space (points).</param>
    /// <param name="currentPdfY">Current pointer Y coordinate in PDF user space (points).</param>
    /// <param name="previousEndCharIndex">Last resolved end character index, or -2 to force computation.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The updated <see cref="PdfTextSelection"/>, or <see langword="null"/> if unchanged.</returns>
    Task<PdfTextSelection?> SelectTextByPointAsync(
        long documentId,
        int pageIndex,
        int anchorCharIndex,
        double anchorPdfX,
        double anchorPdfY,
        double currentPdfX,
        double currentPdfY,
        int previousEndCharIndex = -2,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Selects the whole word located at <c>(pdfX, pdfY)</c> in PDF user space on <paramref name="pageIndex"/>,
    /// computing its bounding rectangles (<c>FPDFText_CountRects</c> / <c>FPDFText_GetRect</c>) and UTF-16 string
    /// (<c>FPDFText_GetText</c>).
    /// </summary>
    /// <param name="documentId">Document identifier.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="pdfX">X coordinate in PDF user space (points).</param>
    /// <param name="pdfY">Y coordinate in PDF user space (points).</param>
    /// <param name="tolerancePt">Hit-test tolerance in points.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The word <see cref="PdfTextSelection"/>, or <see langword="null"/> if no word is at the point.</returns>
    Task<PdfTextSelection?> SelectWordAtPointAsync(
        long documentId,
        int pageIndex,
        double pdfX,
        double pdfY,
        double tolerancePt = 8.0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Selects the entire sentence (or text block/line if unpunctuated) located at <c>(pdfX, pdfY)</c> in PDF user space
    /// on <paramref name="pageIndex"/> (triggered on triple-click).
    /// </summary>
    /// <param name="documentId">Document identifier.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="pdfX">X coordinate in PDF user space (points).</param>
    /// <param name="pdfY">Y coordinate in PDF user space (points).</param>
    /// <param name="tolerancePt">Hit-test tolerance in points.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The sentence <see cref="PdfTextSelection"/>, or <see langword="null"/> if no text is at the point.</returns>
    Task<PdfTextSelection?> SelectSentenceAtPointAsync(
        long documentId,
        int pageIndex,
        double pdfX,
        double pdfY,
        double tolerancePt = 10.0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Selects an explicit character range on <paramref name="pageIndex"/>, returning its bounding rectangles
    /// and extracted UTF-16 text.
    /// </summary>
    /// <param name="documentId">Document identifier.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="startCharIndex">Zero-based start character index.</param>
    /// <param name="charCount">Number of characters to select.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The resulting <see cref="PdfTextSelection"/>, or <see langword="null"/> if invalid.</returns>
    Task<PdfTextSelection?> SelectTextRangeAsync(
        long documentId,
        int pageIndex,
        int startCharIndex,
        int charCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts the UTF-16 text contained within a rectangular region in PDF user space via <c>FPDFText_GetBoundedText</c>.
    /// </summary>
    /// <param name="documentId">Document identifier.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="bounds">Rectangle in PDF user space (points).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The extracted text string (empty if no text lies within the rectangle).</returns>
    Task<string> GetBoundedTextAsync(
        long documentId,
        int pageIndex,
        PdfRect bounds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rasterizes an entire page at the specified pixel size.
    /// </summary>
    /// <param name="documentId">Document identifier.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="widthPx">Target bitmap width in pixels.</param>
    /// <param name="heightPx">Target bitmap height in pixels.</param>
    /// <param name="highPriority">Whether this job takes priority over background thumbnail renders.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A lease owning the rendered native bitmap.</returns>
    Task<PdfBitmapLease> RenderPageAsync(
        long documentId,
        int pageIndex,
        int widthPx,
        int heightPx,
        bool highPriority = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rasterizes a specific region or tile of a page.
    /// </summary>
    /// <param name="documentId">Document identifier.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="startXPx">Horizontal pixel offset of the sub-region.</param>
    /// <param name="startYPx">Vertical pixel offset of the sub-region.</param>
    /// <param name="widthPx">Width of the tile in pixels.</param>
    /// <param name="heightPx">Height of the tile in pixels.</param>
    /// <param name="totalWidthPx">Full rendered page width at this zoom in pixels.</param>
    /// <param name="totalHeightPx">Full rendered page height at this zoom in pixels.</param>
    /// <param name="highPriority">Whether this job takes priority over background thumbnail renders.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A lease owning the rendered native bitmap.</returns>
    Task<PdfBitmapLease> RenderRegionAsync(
        long documentId,
        int pageIndex,
        int startXPx,
        int startYPx,
        int widthPx,
        int heightPx,
        int totalWidthPx,
        int totalHeightPx,
        bool highPriority = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rasterizes a page, thumbnail or tile into a native bitmap.
    /// </summary>
    /// <param name="request">What to render.</param>
    /// <param name="priority">Scheduling priority.</param>
    /// <param name="cancellationToken">
    /// Cancels the job. A job that has not started is dropped without touching PDFium; a running
    /// <c>FPDF_RenderPageBitmap</c> call cannot be interrupted, but its result is discarded.
    /// </param>
    /// <returns>
    /// A lease owning the pixels; the caller must dispose it (or hand it to a
    /// <see cref="Rendering.TileCacheEntry"/>).
    /// </returns>
    /// <exception cref="OperationCanceledException">The job was cancelled or the document was closed.</exception>
    Task<PdfBitmapLease> RenderAsync(PdfRenderRequest request, PdfJobPriority priority, CancellationToken cancellationToken = default);
}
