using System.Runtime.InteropServices;
using WinPDF.Interop.Pdfium;
using WinPDF.Models;
using WinPDF.Rendering;

namespace WinPDF.Services;

/// <summary>
/// All <c>FPDF_*</c> work: library lifetime, documents, page geometry, rendering, outline and attachments.
/// </summary>
/// <remarks>
/// <b>Not thread-safe by design.</b> One instance is created on, and used exclusively from, the dedicated PDFium
/// thread of <see cref="PdfiumService"/>. It never touches UI types.
/// </remarks>
internal sealed unsafe class PdfiumEngine : IDisposable
{
    private const int MaxOutlineDepth = 32;
    private const int MaxOutlineNodes = 50_000;
    private const int MaxWideStringBytes = 4096;
    private const double DefaultPageWidthPt = 612;
    private const double DefaultPageHeightPt = 792;

    private readonly Dictionary<long, PdfiumDocumentHandle> _documents = new();
    private readonly PdfiumPageCache _pageCache = new();
    private long _nextDocumentId;
    private bool _libraryInitialized;

    /// <summary>Initializes PDFium (<c>FPDF_InitLibraryWithConfig</c>). Must run on the PDFium thread.</summary>
    internal PdfiumEngine()
    {
        FpdfLibraryConfig config = new() { Version = PdfiumConstants.LibraryConfigVersion };
        PdfiumNative.FPDF_InitLibraryWithConfig(&config);
        _libraryInitialized = true;
    }

    // ------------------------------------------------------------------------------------------
    // Documents
    // ------------------------------------------------------------------------------------------

    /// <summary>Opens a document and reads the geometry of every page.</summary>
    internal PdfDocumentInfo OpenDocument(string filePath, string? password, CancellationToken cancellationToken)
    {
        PdfiumStreamAccess access;
        try
        {
            access = PdfiumStreamAccess.Open(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new PdfiumException(PdfiumErrorCode.File, $"Cannot open '{filePath}': {ex.Message}", ex);
        }

        PdfiumDocumentHandle document = PdfiumNative.FPDF_LoadCustomDocument(access.Native, password);
        if (document.IsInvalid)
        {
            uint error = PdfiumNative.FPDF_GetLastError();
            document.Dispose();
            access.Dispose();
            throw CreateOpenException(error, filePath);
        }

        // The stream access must stay alive (and be freed last) for as long as the document is open.
        document.StreamAccess = access;

        try
        {
            int pageCount = Math.Max(0, PdfiumNative.FPDF_GetPageCount(document));
            List<PdfPageInfo> pages = new(pageCount);
            for (int i = 0; i < pageCount; i++)
            {
                if ((i & 0x3F) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                pages.Add(ReadPageInfo(document, i));
            }

            long id = ++_nextDocumentId;
            _documents[id] = document;
            return new PdfDocumentInfo(id, filePath, pages);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>Closes a document; its cached pages are closed first.</summary>
    internal void CloseDocument(long documentId)
    {
        if (_documents.Remove(documentId, out PdfiumDocumentHandle? document))
        {
            _pageCache.RemoveDocument(documentId);
            document.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------------
    // Metadata: outline and attachments
    // ------------------------------------------------------------------------------------------

    /// <summary>Reads the bookmark tree (depth- and size-limited, cycle-safe).</summary>
    internal IReadOnlyList<OutlineItem> ReadOutline(long documentId, CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        int budget = MaxOutlineNodes;
        HashSet<nint> visited = new();
        return ReadBookmarkSiblings(document, nint.Zero, depth: 0, ref budget, visited, cancellationToken);
    }

    /// <summary>Reads the names of the embedded file attachments.</summary>
    internal IReadOnlyList<string> ReadAttachmentNames(long documentId, CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        int count = Math.Max(0, PdfiumNative.FPDFDoc_GetAttachmentCount(document));
        List<string> names = new(count);

        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            nint attachment = PdfiumNative.FPDFDoc_GetAttachment(document, i);
            if (attachment == nint.Zero)
            {
                continue;
            }

            string name = ReadWideString(attachment, static (handle, buffer, length) => PdfiumNative.FPDFAttachment_GetName(handle, buffer, length));
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private IReadOnlyList<OutlineItem> ReadBookmarkSiblings(
        PdfiumDocumentHandle document,
        nint parent,
        int depth,
        ref int budget,
        HashSet<nint> visited,
        CancellationToken cancellationToken)
    {
        List<OutlineItem>? items = null;
        nint bookmark = PdfiumNative.FPDFBookmark_GetFirstChild(document, parent);

        while (bookmark != nint.Zero && budget > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Malformed files may contain sibling/child cycles.
            if (!visited.Add(bookmark))
            {
                break;
            }

            budget--;
            string title = ReadWideString(bookmark, static (handle, buffer, length) => PdfiumNative.FPDFBookmark_GetTitle(handle, buffer, length));
            int pageIndex = ResolveBookmarkPage(document, bookmark);
            IReadOnlyList<OutlineItem> children = depth < MaxOutlineDepth
                ? ReadBookmarkSiblings(document, bookmark, depth + 1, ref budget, visited, cancellationToken)
                : Array.Empty<OutlineItem>();

            (items ??= new List<OutlineItem>()).Add(new OutlineItem(title, pageIndex, children));
            bookmark = PdfiumNative.FPDFBookmark_GetNextSibling(document, bookmark);
        }

        return items is null ? Array.Empty<OutlineItem>() : items;
    }

    // Resolves the target page: direct destination first, then a GoTo action. Returns -1 when there is none.
    private static int ResolveBookmarkPage(PdfiumDocumentHandle document, nint bookmark)
    {
        nint dest = PdfiumNative.FPDFBookmark_GetDest(document, bookmark);
        if (dest == nint.Zero)
        {
            nint action = PdfiumNative.FPDFBookmark_GetAction(bookmark);
            if (action != nint.Zero && PdfiumNative.FPDFAction_GetType(action) == PdfiumConstants.ActionGoto)
            {
                dest = PdfiumNative.FPDFAction_GetDest(document, action);
            }
        }

        return dest == nint.Zero ? -1 : PdfiumNative.FPDFDest_GetDestPageIndex(document, dest);
    }

    private delegate uint WideStringReader(nint handle, void* buffer, uint length);

    // Reads a UTF-16LE string returned through PDFium's two-call length/buffer pattern, using stack memory only.
    private static string ReadWideString(nint handle, WideStringReader reader)
    {
        uint required = reader(handle, null, 0);
        if (required <= sizeof(char))
        {
            return string.Empty;
        }

        int capacity = (int)Math.Min(required, MaxWideStringBytes);
        byte* buffer = stackalloc byte[MaxWideStringBytes];
        uint written = reader(handle, buffer, (uint)capacity);
        int byteCount = (int)Math.Min(written, (uint)capacity) & ~1;

        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(new ReadOnlySpan<byte>(buffer, byteCount));
        int end = chars.IndexOf('\0');
        return new string(end >= 0 ? chars[..end] : chars);
    }

    // ------------------------------------------------------------------------------------------
    // Page geometry
    // ------------------------------------------------------------------------------------------

    // Loads the page transiently (not through the LRU, so opening a 1000-page file cannot flush useful entries).
    private static PdfPageInfo ReadPageInfo(PdfiumDocumentHandle document, int index)
    {
        using PdfiumPageHandle page = PdfiumNative.FPDF_LoadPage(document, index);
        if (page.IsInvalid)
        {
            return ReadPageInfoFromSize(document, index, rotation: 0);
        }

        int rotation = PdfiumNative.FPDFPage_GetRotation(page);
        rotation = rotation is >= 0 and <= 3 ? rotation * 90 : 0;

        bool hasMedia = TryGetBox(PdfiumNative.FPDFPage_GetMediaBox(page, out float ml, out float mb, out float mr, out float mt), ml, mb, mr, mt, out Box media);
        bool hasCrop = TryGetBox(PdfiumNative.FPDFPage_GetCropBox(page, out float cl, out float cb, out float cr, out float ct), cl, cb, cr, ct, out Box crop);

        // PDFium renders CropBox ∩ MediaBox; fall back to whichever box exists.
        Box visible;
        if (hasMedia && hasCrop)
        {
            Box intersection = crop.Intersect(media);
            visible = intersection.IsEmpty ? media : intersection;
        }
        else if (hasCrop)
        {
            visible = crop;
        }
        else if (hasMedia)
        {
            visible = media;
        }
        else
        {
            return ReadPageInfoFromSize(document, index, rotation);
        }

        return new PdfPageInfo(index, visible.Width, visible.Height, rotation, visible.Left, visible.Bottom);
    }

    // Fallback: FPDF_GetPageSizeByIndexF reports the rotated (displayed) size; undo the swap.
    private static PdfPageInfo ReadPageInfoFromSize(PdfiumDocumentHandle document, int index, int rotation)
    {
        FsSizeF size = default;
        if (PdfiumNative.FPDF_GetPageSizeByIndexF(document, index, &size) != 0 && size.Width > 0 && size.Height > 0)
        {
            bool swap = rotation is 90 or 270;
            return new PdfPageInfo(index, swap ? size.Height : size.Width, swap ? size.Width : size.Height, rotation);
        }

        return new PdfPageInfo(index, DefaultPageWidthPt, DefaultPageHeightPt, rotation);
    }

    private static bool TryGetBox(int success, float left, float bottom, float right, float top, out Box box)
    {
        box = default;
        if (success == 0)
        {
            return false;
        }

        // PDF rectangles may list their corners in any order.
        Box candidate = new(Math.Min(left, right), Math.Min(bottom, top), Math.Max(left, right), Math.Max(bottom, top));
        if (!float.IsFinite(candidate.Width) || !float.IsFinite(candidate.Height) || candidate.IsEmpty)
        {
            return false;
        }

        box = candidate;
        return true;
    }

    // ------------------------------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Rasterizes into a PDFium-allocated BGRA bitmap and returns the pixels without copying them.
    /// </summary>
    internal PdfBitmapLease Render(in PdfRenderRequest request, IPdfiumBitmapOwner owner, CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(request.DocumentId);

        if (request.BitmapWidth <= 0 || request.BitmapHeight <= 0 || request.PageWidthPx <= 0 || request.PageHeightPx <= 0
            || (long)request.BitmapWidth * request.BitmapHeight * 4 > int.MaxValue)
        {
            throw new PdfiumException(PdfiumErrorCode.OutOfMemory, $"Unsupported bitmap size {request.BitmapWidth}x{request.BitmapHeight}.");
        }

        PdfiumPageHandle page = _pageCache.GetOrLoad(document, request.DocumentId, request.PageIndex)
            ?? throw new PdfiumException(PdfiumErrorCode.Page, $"Page {request.PageIndex} could not be loaded.");

        cancellationToken.ThrowIfCancellationRequested();

        // Zero-LOH: PDFium allocates the pixels natively; they are never copied into a managed array.
        PdfiumBitmapHandle bitmap = PdfiumNative.FPDFBitmap_CreateEx(request.BitmapWidth, request.BitmapHeight, PdfiumConstants.BitmapBgra, null, 0);
        if (bitmap.IsInvalid)
        {
            bitmap.Dispose();
            throw new PdfiumException(PdfiumErrorCode.OutOfMemory, $"Cannot allocate a {request.BitmapWidth}x{request.BitmapHeight} bitmap.");
        }

        try
        {
            PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, request.BitmapWidth, request.BitmapHeight, PdfiumConstants.OpaqueWhite);

            int rotate = PageCoordinateTransform.NormalizeRotation(request.Rotation) / 90;
            PdfiumNative.FPDF_RenderPageBitmap(
                bitmap,
                page,
                request.OffsetX,
                request.OffsetY,
                request.PageWidthPx,
                request.PageHeightPx,
                rotate,
                (int)request.Flags);

            // A render in progress cannot be interrupted; honor cancellation by discarding its result.
            cancellationToken.ThrowIfCancellationRequested();

            void* scan0 = PdfiumNative.FPDFBitmap_GetBuffer(bitmap);
            int stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
            return new PdfBitmapLease(bitmap, request.BitmapWidth, request.BitmapHeight, stride, scan0, owner);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Housekeeping
    // ------------------------------------------------------------------------------------------

    private PdfiumDocumentHandle GetDocument(long documentId)
    {
        // A document that was closed while jobs were queued behaves like a cancelled job.
        return _documents.TryGetValue(documentId, out PdfiumDocumentHandle? document)
            ? document
            : throw new OperationCanceledException($"Document {documentId} is closed.");
    }

    private static PdfiumException CreateOpenException(uint error, string filePath) => error switch
    {
        PdfiumConstants.ErrFile => new PdfiumException(PdfiumErrorCode.File, $"'{filePath}' was not found or could not be opened."),
        PdfiumConstants.ErrFormat => new PdfiumException(PdfiumErrorCode.Format, $"'{filePath}' is not a valid PDF file."),
        PdfiumConstants.ErrPassword => new PdfiumException(PdfiumErrorCode.Password, $"'{filePath}' requires a (valid) password."),
        PdfiumConstants.ErrSecurity => new PdfiumException(PdfiumErrorCode.Security, $"'{filePath}' uses an unsupported security handler."),
        PdfiumConstants.ErrPage => new PdfiumException(PdfiumErrorCode.Page, $"'{filePath}' has missing or invalid page content."),
        _ => new PdfiumException(PdfiumErrorCode.Unknown, $"PDFium failed to open '{filePath}' (error {error})."),
    };

    /// <summary>Closes every page and document, then destroys the library.</summary>
    public void Dispose()
    {
        _pageCache.Clear();

        foreach (PdfiumDocumentHandle document in _documents.Values)
        {
            document.Dispose();
        }

        _documents.Clear();

        if (_libraryInitialized)
        {
            _libraryInitialized = false;
            PdfiumNative.FPDF_DestroyLibrary();
        }
    }

    private readonly record struct Box(float Left, float Bottom, float Right, float Top)
    {
        internal float Width => Right - Left;

        internal float Height => Top - Bottom;

        internal bool IsEmpty => Width < 1f || Height < 1f;

        internal Box Intersect(Box other)
            => new(Math.Max(Left, other.Left), Math.Max(Bottom, other.Bottom), Math.Min(Right, other.Right), Math.Min(Top, other.Top));
    }
}
