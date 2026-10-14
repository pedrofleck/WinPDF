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
