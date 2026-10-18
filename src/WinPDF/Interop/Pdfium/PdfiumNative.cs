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
/// document (bookmarks, destinations, actions, links, attachments) and must never be closed.
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
    internal static partial float FPDF_GetPageWidthF(PdfiumPageHandle page);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial float FPDF_GetPageHeightF(PdfiumPageHandle page);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPage_GetRotation(PdfiumPageHandle page);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDFPage_SetRotation(PdfiumPageHandle page, int rotate);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPage_GenerateContent(PdfiumPageHandle page);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDF_SaveAsCopy(PdfiumDocumentHandle document, FpdfFileWrite* fileWrite, uint flags);

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

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDF_RenderPage(
        nint dc,
        PdfiumPageHandle page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);

    // ------------------------------------------------------------------------------------------
    // Bookmarks (document outline), actions and destinations
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
    internal static partial uint FPDFAction_GetFilePath(nint action, void* buffer, uint buflen);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFAction_GetURIPath(PdfiumDocumentHandle document, nint action, void* buffer, uint buflen);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFDest_GetDestPageIndex(PdfiumDocumentHandle document, nint dest);

    // ------------------------------------------------------------------------------------------
    // Text extraction, geometry and search (fpdf_text.h)
    // ------------------------------------------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial PdfiumTextPageHandle FPDFText_LoadPage(PdfiumPageHandle page);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDFText_ClosePage(nint textPage);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFText_CountChars(PdfiumTextPageHandle textPage);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFText_GetUnicode(PdfiumTextPageHandle textPage, int index);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFText_GetCharBox(
        PdfiumTextPageHandle textPage,
        int index,
        out double left,
        out double right,
        out double bottom,
        out double top);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFText_GetLooseCharBox(
        PdfiumTextPageHandle textPage,
        int index,
        out FsRectF rect);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFText_GetCharIndexAtPos(
        PdfiumTextPageHandle textPage,
        double x,
        double y,
        double xTolerance,
        double yTolerance);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFText_GetText(
        PdfiumTextPageHandle textPage,
        int startIndex,
        int count,
        char* result);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFText_CountRects(
        PdfiumTextPageHandle textPage,
        int startIndex,
        int count);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFText_GetRect(
        PdfiumTextPageHandle textPage,
        int rectIndex,
        out double left,
        out double top,
        out double right,
        out double bottom);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFText_GetBoundedText(
        PdfiumTextPageHandle textPage,
        double left,
        double top,
        double right,
        double bottom,
        char* buffer,
        int buflen);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial PdfiumSearchHandle FPDFText_FindStart(
        PdfiumTextPageHandle textPage,
        char* findwhat,
        uint flags,
        int startIndex);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFText_FindNext(PdfiumSearchHandle handle);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFText_FindPrev(PdfiumSearchHandle handle);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFText_GetSchResultIndex(PdfiumSearchHandle handle);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFText_GetSchCount(PdfiumSearchHandle handle);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDFText_FindClose(nint handle);

    // ------------------------------------------------------------------------------------------
    // Links (fpdf_doc.h and fpdf_text.h)
    // ------------------------------------------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFLink_GetLinkAtPoint(PdfiumPageHandle page, double x, double y);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFLink_GetLinkZOrderAtPoint(PdfiumPageHandle page, double x, double y);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFLink_GetDest(PdfiumDocumentHandle document, nint link);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFLink_GetAction(nint link);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFLink_Enumerate(PdfiumPageHandle page, ref int startPos, out nint linkAnnot);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFLink_GetAnnotRect(nint linkAnnot, out FsRectF rect);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial PdfiumPageLinkHandle FPDFLink_LoadWebLinks(PdfiumTextPageHandle textPage);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFLink_CountWebLinks(PdfiumPageLinkHandle linkPage);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFLink_GetURL(PdfiumPageLinkHandle linkPage, int linkIndex, char* buffer, int buflen);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFLink_CountRects(PdfiumPageLinkHandle linkPage, int linkIndex);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFLink_GetRect(
        PdfiumPageLinkHandle linkPage,
        int linkIndex,
        int rectIndex,
        out double left,
        out double top,
        out double right,
        out double bottom);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDFLink_CloseWebLinks(nint linkPage);

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
