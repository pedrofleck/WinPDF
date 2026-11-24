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
    internal static partial PdfiumBitmapHandle FPDFBitmap_Create(int width, int height, int alpha);

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
    internal static partial int FPDFBitmap_GetFormat(PdfiumBitmapHandle bitmap);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFBitmap_GetWidth(PdfiumBitmapHandle bitmap);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFBitmap_GetHeight(PdfiumBitmapHandle bitmap);

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

    // ------------------------------------------------------------------------------------------
    // Annotations (fpdf_annot.h)
    // ------------------------------------------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPage_GetAnnotCount(PdfiumPageHandle page);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial PdfiumAnnotationHandle FPDFPage_GetAnnot(PdfiumPageHandle page, int index);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial PdfiumAnnotationHandle FPDFPage_CreateAnnot(PdfiumPageHandle page, int subtype);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPage_GetAnnotIndex(PdfiumPageHandle page, PdfiumAnnotationHandle annot);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDFPage_CloseAnnot(nint annot);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPage_RemoveAnnot(PdfiumPageHandle page, int index);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_GetSubtype(PdfiumAnnotationHandle annot);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_GetRect(PdfiumAnnotationHandle annot, FsRectF* rect);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_SetRect(PdfiumAnnotationHandle annot, FsRectF* rect);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_GetColor(
        PdfiumAnnotationHandle annot,
        int type,
        out uint r,
        out uint g,
        out uint b,
        out uint a);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_SetColor(
        PdfiumAnnotationHandle annot,
        int type,
        uint r,
        uint g,
        uint b,
        uint a);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_HasAttachmentPoints(PdfiumAnnotationHandle annot);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint FPDFAnnot_CountAttachmentPoints(PdfiumAnnotationHandle annot);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_GetAttachmentPoints(
        PdfiumAnnotationHandle annot,
        nuint quadIndex,
        FsQuadPointsF* quadPoints);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_AppendAttachmentPoints(
        PdfiumAnnotationHandle annot,
        FsQuadPointsF* quadPoints);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_HasKey(PdfiumAnnotationHandle annot, string key);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_GetValueType(PdfiumAnnotationHandle annot, string key);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFAnnot_GetStringValue(
        PdfiumAnnotationHandle annot,
        string key,
        char* buffer,
        uint buflen);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_SetStringValue(
        PdfiumAnnotationHandle annot,
        string key,
        char* value);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_SetAP(
        PdfiumAnnotationHandle annot,
        int appearanceMode,
        char* value);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_GetFlags(PdfiumAnnotationHandle annot);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_SetFlags(PdfiumAnnotationHandle annot, int flags);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial PdfiumAnnotationHandle FPDFAnnot_GetLinkedAnnot(PdfiumAnnotationHandle annot, string key);

    // ------------------------------------------------------------------------------------------
    // Digital signatures (fpdf_signature.h)
    // ------------------------------------------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDF_GetSignatureCount(PdfiumDocumentHandle document);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDF_GetSignatureObject(PdfiumDocumentHandle document, int index);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFSignatureObj_GetContents(nint signature, byte* buffer, uint length);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFSignatureObj_GetByteRange(nint signature, int* buffer, uint length);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFSignatureObj_GetSubFilter(nint signature, byte* buffer, uint length);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFSignatureObj_GetReason(nint signature, char* buffer, uint length);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFSignatureObj_GetTime(nint signature, byte* buffer, uint length);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFSignatureObj_GetDocMDPPermission(nint signature);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFAnnot_GetInkListCount(PdfiumAnnotationHandle annot);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFAnnot_GetInkListPath(
        PdfiumAnnotationHandle annot,
        uint pathIndex,
        FsPointF* buffer,
        uint length);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_RemoveInkList(PdfiumAnnotationHandle annot);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_AddInkStroke(
        PdfiumAnnotationHandle annot,
        FsPointF* points,
        nuint pointCount);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_GetBorder(
        PdfiumAnnotationHandle annot,
        out float horizontalRadius,
        out float verticalRadius,
        out float borderWidth);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_SetBorder(
        PdfiumAnnotationHandle annot,
        float horizontalRadius,
        float verticalRadius,
        float borderWidth);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFAnnot_GetVertices(
        PdfiumAnnotationHandle annot,
        FsPointF* buffer,
        uint length);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_GetLine(
        PdfiumAnnotationHandle annot,
        FsPointF* start,
        FsPointF* end);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_GetNumberValue(
        PdfiumAnnotationHandle annot,
        string key,
        out float value);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_GetObjectCount(PdfiumAnnotationHandle annot);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFAnnot_GetObject(PdfiumAnnotationHandle annot, int index);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPageObj_GetType(nint pageObject);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPageObj_GetStrokeColor(
        nint pageObject,
        out uint r,
        out uint g,
        out uint b,
        out uint a);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPageObj_GetFillColor(
        nint pageObject,
        out uint r,
        out uint g,
        out uint b,
        out uint a);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPageObj_GetStrokeWidth(nint pageObject, out float width);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPageObj_GetMatrix(nint pageObject, FsMatrix* matrix);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPath_GetDrawMode(nint path, out int fillMode, out int stroke);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPath_CountSegments(nint path);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFPath_GetPathSegment(nint path, int index);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPathSegment_GetPoint(nint segment, out float x, out float y);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPathSegment_GetType(nint segment);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPathSegment_GetClose(nint segment);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint FPDFAnnot_GetAP(PdfiumAnnotationHandle annot, int appearanceMode, char* buffer, uint buflen);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFFormObj_CountObjects(nint formObject);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFFormObj_GetObject(nint formObject, uint index);

    // ------------------------------------------------------------------------------------------
    // FormFill & interactive widget rendering (fpdf_formfill.h)
    // ------------------------------------------------------------------------------------------

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFPageObj_NewImageObj(PdfiumDocumentHandle document);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDFPageObj_Destroy(nint pageObject);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFImageObj_SetBitmap(nint* pages, int count, nint imageObject, PdfiumBitmapHandle bitmap);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFImageObj_SetMatrix(nint imageObject, double a, double b, double c, double d, double e, double f);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial PdfiumBitmapHandle FPDFImageObj_GetBitmap(nint imageObject);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDFPageObj_Transform(nint pageObject, double a, double b, double c, double d, double e, double f);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_AppendObject(PdfiumAnnotationHandle annot, nint pageObject);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_UpdateObject(PdfiumAnnotationHandle annot, nint pageObject);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFAnnot_RemoveObject(PdfiumAnnotationHandle annot, int index);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFText_LoadStandardFont(
        PdfiumDocumentHandle document,
        string font);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDFFont_Close(nint font);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFPageObj_CreateTextObj(
        PdfiumDocumentHandle document,
        nint font,
        float fontSize);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFText_SetText(nint textObject, char* text);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPageObj_SetFillColor(
        nint pageObject,
        uint r,
        uint g,
        uint b,
        uint a);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPageObj_SetStrokeColor(
        nint pageObject,
        uint r,
        uint g,
        uint b,
        uint a);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPageObj_SetStrokeWidth(nint pageObject, float width);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPageObj_GetBounds(
        nint pageObject,
        out float left,
        out float bottom,
        out float right,
        out float top);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFPageObj_CreateNewPath(float x, float y);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPath_LineTo(nint path, float x, float y);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FPDFPath_SetDrawMode(nint path, int fillMode, int stroke);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint FPDFDOC_InitFormFillEnvironment(PdfiumDocumentHandle document, void* formInfo);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDFDOC_ExitFormFillEnvironment(nint hHandle);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FORM_OnAfterLoadPage(PdfiumPageHandle page, nint hHandle);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FORM_OnBeforeClosePage(PdfiumPageHandle page, nint hHandle);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void FPDF_FFLDraw(
        nint hHandle,
        PdfiumBitmapHandle bitmap,
        PdfiumPageHandle page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);
}
