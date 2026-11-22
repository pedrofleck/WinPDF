using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinPDF.Interop.Pdfium;

/// <summary>
/// Owns an <c>FPDF_DOCUMENT</c> together with its optional FormFill environment and the
/// <see cref="PdfiumStreamAccess"/> that feeds it. All native handles are released in strict order.
/// </summary>
/// <remarks>
/// Like every PDFium call, the release must happen on the PDFium thread. The finalizer path exists only as a
/// last-resort leak guard.
/// </remarks>
internal sealed unsafe class PdfiumDocumentHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes a new, invalid handle (required by the LibraryImport marshaller).</summary>
    public PdfiumDocumentHandle()
        : base(ownsHandle: true)
    {
    }

    /// <summary>Gets or sets the stream access that must stay alive while the document is open.</summary>
    internal PdfiumStreamAccess? StreamAccess { get; set; }

    /// <summary>Gets or sets the optional <c>FPDF_FORMHANDLE</c> created by <c>FPDFDOC_InitFormFillEnvironment</c>.</summary>
    internal nint FormHandle { get; set; }

    /// <summary>Gets or sets the native <c>FPDF_FORMFILLINFO</c> allocation owned for <see cref="FormHandle"/>.</summary>
    internal void* FormFillInfoPtr { get; set; }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        if (FormHandle != nint.Zero)
        {
            PdfiumNative.FPDFDOC_ExitFormFillEnvironment(FormHandle);
            FormHandle = nint.Zero;
        }

        PdfiumNative.FPDF_CloseDocument(handle);

        if (FormFillInfoPtr is not null)
        {
            NativeMemory.Free(FormFillInfoPtr);
            FormFillInfoPtr = null;
        }

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

/// <summary>
/// Owns an <c>FPDF_TEXTPAGE</c> returned by <c>FPDFText_LoadPage</c>. Must be released before its owning
/// <see cref="PdfiumPageHandle"/> is closed.
/// </summary>
internal sealed class PdfiumTextPageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes a new, invalid handle (required by the LibraryImport marshaller).</summary>
    public PdfiumTextPageHandle()
        : base(ownsHandle: true)
    {
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        PdfiumNative.FPDFText_ClosePage(handle);
        return true;
    }
}

/// <summary>
/// Owns an <c>FPDF_SCHHANDLE</c> returned by <c>FPDFText_FindStart</c>. Must be released before its owning
/// <see cref="PdfiumTextPageHandle"/> is closed.
/// </summary>
internal sealed class PdfiumSearchHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes a new, invalid handle (required by the LibraryImport marshaller).</summary>
    public PdfiumSearchHandle()
        : base(ownsHandle: true)
    {
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        PdfiumNative.FPDFText_FindClose(handle);
        return true;
    }
}

/// <summary>
/// Owns an <c>FPDF_PAGELINK</c> returned by <c>FPDFLink_LoadWebLinks</c>. Must be released before its owning
/// <see cref="PdfiumTextPageHandle"/> is closed.
/// </summary>
internal sealed class PdfiumPageLinkHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes a new, invalid handle (required by the LibraryImport marshaller).</summary>
    public PdfiumPageLinkHandle()
        : base(ownsHandle: true)
    {
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        PdfiumNative.FPDFLink_CloseWebLinks(handle);
        return true;
    }
}

/// <summary>
/// Owns an <c>FPDF_ANNOTATION</c> returned by <c>FPDFPage_GetAnnot</c>, <c>FPDFPage_CreateAnnot</c>, or
/// <c>FPDFAnnot_GetLinkedAnnot</c>. Must be closed strictly before its owning <see cref="PdfiumPageHandle"/>.
/// </summary>
internal sealed class PdfiumAnnotationHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes a new, invalid handle (required by the LibraryImport marshaller).</summary>
    public PdfiumAnnotationHandle()
        : base(ownsHandle: true)
    {
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        PdfiumNative.FPDFPage_CloseAnnot(handle);
        return true;
    }
}
