using Microsoft.Win32.SafeHandles;

namespace WinPDF.Interop.Pdfium;

/// <summary>
/// Owns an <c>FPDF_DOCUMENT</c> together with the <see cref="PdfiumStreamAccess"/> that feeds it.
/// The stream access (file handle, <see cref="System.Runtime.InteropServices.GCHandle"/>, native
/// <c>FPDF_FILEACCESS</c> block) must outlive the document, so it is released only after
/// <c>FPDF_CloseDocument</c> returns.
/// </summary>
/// <remarks>
/// Like every PDFium call, the release must happen on the PDFium thread. The finalizer path exists only as a
/// last-resort leak guard.
/// </remarks>
internal sealed class PdfiumDocumentHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes a new, invalid handle (required by the LibraryImport marshaller).</summary>
    public PdfiumDocumentHandle()
        : base(ownsHandle: true)
    {
    }

    /// <summary>Gets or sets the stream access that must stay alive while the document is open.</summary>
    internal PdfiumStreamAccess? StreamAccess { get; set; }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        PdfiumNative.FPDF_CloseDocument(handle);
        StreamAccess?.Dispose();
        StreamAccess = null;
        return true;
    }
}

/// <summary>
/// Owns an <c>FPDF_PAGE</c>. Must be released before its owning document is closed.
/// </summary>
internal sealed class PdfiumPageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes a new, invalid handle (required by the LibraryImport marshaller).</summary>
    public PdfiumPageHandle()
        : base(ownsHandle: true)
    {
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        PdfiumNative.FPDF_ClosePage(handle);
        return true;
    }
}

/// <summary>
/// Owns an <c>FPDF_BITMAP</c> allocated by PDFium (<c>FPDFBitmap_CreateEx</c> with no external buffer).
/// </summary>
internal sealed class PdfiumBitmapHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes a new, invalid handle (required by the LibraryImport marshaller).</summary>
    public PdfiumBitmapHandle()
        : base(ownsHandle: true)
    {
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        PdfiumNative.FPDFBitmap_Destroy(handle);
        return true;
    }
}
