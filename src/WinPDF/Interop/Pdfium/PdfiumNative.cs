using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinPDF.Interop.Pdfium;

/// <summary>
/// Hand-written, source-generated (<c>[LibraryImport]</c>) bindings for the subset of the PDFium C API
/// used by WinPDF. All functions use the cdecl convention (<c>FPDF_CALLCONV</c> is empty for consumers).
/// </summary>
/// <remarks>
/// PDFium is not thread-safe: every function here must be called from the single dedicated PDFium thread
/// owned by <c>PdfiumService</c>. Raw <see cref="nint"/> parameters are borrowed handles owned by their
/// document (bookmarks, destinations, actions, attachments) and must never be closed.
/// </remarks>
internal static unsafe partial class PdfiumNative
{
    private const string Lib = PdfiumConstants.LibraryName;

    // ------------------------------------------------------------------------------------------
    // Library lifetime
    // ------------------------------------------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDF_InitLibraryWithConfig(FpdfLibraryConfig* config);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDF_DestroyLibrary();

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDF_GetLastError();

    // ------------------------------------------------------------------------------------------
    // Documents
    // ------------------------------------------------------------------------------------------

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial PdfiumDocumentHandle FPDF_LoadCustomDocument(FpdfFileAccess* fileAccess, string? password);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDF_GetPageCount(PdfiumDocumentHandle document);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDF_GetPageSizeByIndexF(PdfiumDocumentHandle document, int pageIndex, FsSizeF* size);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDF_GetMetaText(PdfiumDocumentHandle document, string tag, void* buffer, uint buflen);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDF_GetFileVersion(PdfiumDocumentHandle document, out int fileVersion);

    // ------------------------------------------------------------------------------------------
    // Pages and geometry
    // ------------------------------------------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial PdfiumPageHandle FPDF_LoadPage(PdfiumDocumentHandle document, int pageIndex);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPage_GetRotation(PdfiumPageHandle page);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPage_GetCropBox(PdfiumPageHandle page, out float left, out float bottom, out float right, out float top);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPage_GetMediaBox(PdfiumPageHandle page, out float left, out float bottom, out float right, out float top);

    // ------------------------------------------------------------------------------------------
    // Bitmaps and rendering
    // ------------------------------------------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial PdfiumBitmapHandle FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFBitmap_FillRect(PdfiumBitmapHandle bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void* FPDFBitmap_GetBuffer(PdfiumBitmapHandle bitmap);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFBitmap_GetStride(PdfiumBitmapHandle bitmap);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDF_RenderPageBitmap(
        PdfiumBitmapHandle bitmap,
        PdfiumPageHandle page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);

    // ------------------------------------------------------------------------------------------
    // Bookmarks (document outline)
    // ------------------------------------------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFBookmark_GetFirstChild(PdfiumDocumentHandle document, nint bookmark);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFBookmark_GetNextSibling(PdfiumDocumentHandle document, nint bookmark);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFBookmark_GetTitle(nint bookmark, void* buffer, uint bufferLength);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFBookmark_GetDest(PdfiumDocumentHandle document, nint bookmark);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFBookmark_GetAction(nint bookmark);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFAction_GetType(nint action);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFAction_GetDest(PdfiumDocumentHandle document, nint action);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFDest_GetDestPageIndex(PdfiumDocumentHandle document, nint dest);

    // ------------------------------------------------------------------------------------------
    // Embedded file attachments
    // ------------------------------------------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFDoc_GetAttachmentCount(PdfiumDocumentHandle document);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFDoc_GetAttachment(PdfiumDocumentHandle document, int index);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFAttachment_GetName(nint attachment, void* buffer, uint bufferLength);
}
