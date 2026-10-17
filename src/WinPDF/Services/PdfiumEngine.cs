using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinPDF.Interop.Pdfium;
using WinPDF.Interop.Printing;
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
    private const int MaxOutlineDepth = 64;
    private const int MaxSiblingsPerLevel = 2000;
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
            long id = ++_nextDocumentId;
            int pageCount = Math.Max(0, PdfiumNative.FPDF_GetPageCount(document));
            List<PdfPageInfo> pages = new(pageCount);
            for (int i = 0; i < pageCount; i++)
            {
                if ((i & 0x3F) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                pages.Add(ReadPageInfo(document, i, id));
            }

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

    /// <summary>
    /// Saves the open document to <paramref name="targetFilePath"/> via <c>FPDF_SaveAsCopy</c>, applying any
    /// page rotation and extensible page edit committers (ink, text, shapes, comments) before writing.
    /// </summary>
    internal void SaveDocument(
        long documentId,
        string targetFilePath,
        PdfSaveOptions options,
        CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        string fullTargetPath = System.IO.Path.GetFullPath(targetFilePath);
        string? targetDir = System.IO.Path.GetDirectoryName(fullTargetPath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            System.IO.Directory.CreateDirectory(targetDir);
        }

        string tempFilePath = System.IO.Path.Combine(
            targetDir ?? System.IO.Path.GetTempPath(),
            $".{System.IO.Path.GetFileName(fullTargetPath)}.{Guid.NewGuid():N}.winpdf.tmp");

        int extraRotationSteps = (PageCoordinateTransform.NormalizeRotation(options.UserRotationDegrees) / 90) % 4;
        IReadOnlyList<IPdfPageEditCommitter>? committers = options.PageEditCommitters;
        bool hasCommitters = committers is { Count: > 0 };

        List<(int PageIndex, int OriginalRotation)>? modifiedRotations = null;

        try
        {
            if (extraRotationSteps != 0 || hasCommitters)
            {
                int pageCount = Math.Max(0, PdfiumNative.FPDF_GetPageCount(document));
                if (extraRotationSteps != 0)
                {
                    modifiedRotations = new List<(int, int)>(pageCount);
                }

                for (int i = 0; i < pageCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    bool pageNeedsRotation = extraRotationSteps != 0;
                    bool pageHasCommitter = false;
                    if (hasCommitters)
                    {
                        for (int c = 0; c < committers!.Count; c++)
                        {
                            if (committers[c].PageIndex == i)
                            {
                                pageHasCommitter = true;
                                break;
                            }
                        }
                    }

                    if (!pageNeedsRotation && !pageHasCommitter)
                    {
                        continue;
                    }

                    using PdfiumPageHandle page = PdfiumNative.FPDF_LoadPage(document, i);
                    if (page.IsInvalid)
                    {
                        continue;
                    }

                    if (pageNeedsRotation)
                    {
                        int origRot = PdfiumNative.FPDFPage_GetRotation(page);
                        if (origRot is < 0 or > 3)
                        {
                            origRot = 0;
                        }

                        modifiedRotations!.Add((i, origRot));
                        int newRot = (origRot + extraRotationSteps) % 4;
                        PdfiumNative.FPDFPage_SetRotation(page, newRot);
                    }

                    if (pageHasCommitter)
                    {
                        bool contentChanged = false;
                        for (int c = 0; c < committers!.Count; c++)
                        {
                            if (committers[c].PageIndex == i)
                            {
                                contentChanged |= committers[c].CommitToPage(document.DangerousGetHandle(), page.DangerousGetHandle());
                            }
                        }

                        if (contentChanged)
                        {
                            _ = PdfiumNative.FPDFPage_GenerateContent(page);
                        }
                    }
                }
            }

            using (FileStream outStream = new(
                tempFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65536,
                FileOptions.SequentialScan))
            {
                GCHandle streamHandle = GCHandle.Alloc(outStream, GCHandleType.Normal);
                try
                {
                    FpdfFileWrite fileWrite = new()
                    {
                        Version = 1,
                        WriteBlock = &WriteFileBlock,
                        UserData = GCHandle.ToIntPtr(streamHandle)
                    };

                    int saveResult = PdfiumNative.FPDF_SaveAsCopy(document, &fileWrite, options.SaveFlags);
                    if (saveResult == 0)
                    {
                        throw new PdfiumException(PdfiumErrorCode.File, $"FPDF_SaveAsCopy failed for '{fullTargetPath}'.");
                    }
                }
                finally
                {
                    if (streamHandle.IsAllocated)
                    {
                        streamHandle.Free();
                    }
                }

                outStream.Flush(flushToDisk: true);
            }

            System.IO.File.Move(tempFilePath, fullTargetPath, overwrite: true);
        }
        catch (Exception ex) when (ex is not PdfiumException and not OperationCanceledException)
        {
            throw new PdfiumException(PdfiumErrorCode.File, $"Failed to write '{fullTargetPath}': {ex.Message}", ex);
        }
        finally
        {
            if (modifiedRotations is { Count: > 0 })
            {
                foreach ((int pageIdx, int origRot) in modifiedRotations)
                {
                    try
                    {
                        using PdfiumPageHandle page = PdfiumNative.FPDF_LoadPage(document, pageIdx);
                        if (!page.IsInvalid)
                        {
                            PdfiumNative.FPDFPage_SetRotation(page, origRot);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            try
            {
                if (System.IO.File.Exists(tempFilePath))
                {
                    System.IO.File.Delete(tempFilePath);
                }
            }
            catch
            {
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteFileBlock(FpdfFileWrite* pThis, void* data, uint size)
    {
        try
        {
            if (size == 0)
            {
                return 1;
            }

            if (pThis is null || data is null || size > int.MaxValue)
            {
                return 0;
            }

            if (GCHandle.FromIntPtr(pThis->UserData).Target is not FileStream stream)
            {
                return 0;
            }

            ReadOnlySpan<byte> buffer = new(data, (int)size);
            stream.Write(buffer);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Renders the requested pages as vector GDI commands directly into the native printer <c>HDC</c>
    /// via <c>FPDF_RenderPage</c> and always deletes the <c>HDC</c> upon completion.
    /// </summary>
    internal bool PrintDocument(
        long documentId,
        PdfPrintJobOptions options,
        CancellationToken cancellationToken)
    {
        nint hdc = options.PrinterDc;
        nint docNamePtr = nint.Zero;
        bool docStarted = false;

        try
        {
            if (hdc == nint.Zero || options.PageIndices.Count == 0)
            {
                return false;
            }

            PdfiumDocumentHandle document = GetDocument(documentId);
            cancellationToken.ThrowIfCancellationRequested();

            string title = string.IsNullOrWhiteSpace(options.DocumentTitle) ? "WinPDF Document" : options.DocumentTitle;
            docNamePtr = Marshal.StringToHGlobalUni(title);

            Win32PrintInterop.DOCINFOW docInfo = new()
            {
                cbSize = Marshal.SizeOf<Win32PrintInterop.DOCINFOW>(),
                lpszDocName = docNamePtr,
                lpszOutput = nint.Zero,
                lpszDatatype = nint.Zero,
                fwType = 0,
            };

            if (Win32PrintInterop.StartDocW(hdc, in docInfo) <= 0)
            {
                return false;
            }

            docStarted = true;

            int printableW = Math.Max(1, Win32PrintInterop.GetDeviceCaps(hdc, Win32PrintInterop.HORZRES));
            int printableH = Math.Max(1, Win32PrintInterop.GetDeviceCaps(hdc, Win32PrintInterop.VERTRES));
            int copies = Math.Max(1, options.Copies);
            int pageCount = Math.Max(0, PdfiumNative.FPDF_GetPageCount(document));

            for (int copy = 0; copy < copies; copy++)
            {
                for (int i = 0; i < options.PageIndices.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int pageIndex = options.PageIndices[i];
                    if (pageIndex < 0 || pageIndex >= pageCount)
                    {
                        continue;
                    }

                    using PdfiumPageHandle page = PdfiumNative.FPDF_LoadPage(document, pageIndex);
                    if (page.IsInvalid)
                    {
                        continue;
                    }

                    double pageW = PdfiumNative.FPDF_GetPageWidthF(page);
                    double pageH = PdfiumNative.FPDF_GetPageHeightF(page);
                    if (!double.IsFinite(pageW) || !double.IsFinite(pageH) || pageW <= 0 || pageH <= 0)
                    {
                        PdfPageInfo fallbackInfo = ReadPageInfo(document, pageIndex, documentId);
                        bool intrinsicSwap = fallbackInfo.Rotation is 90 or 270;
                        pageW = intrinsicSwap ? fallbackInfo.HeightPt : fallbackInfo.WidthPt;
                        pageH = intrinsicSwap ? fallbackInfo.WidthPt : fallbackInfo.HeightPt;
                    }

                    int userDegrees = options.UserRotationDegrees;
                    if (options.PageUserRotations is not null &&
                        options.PageUserRotations.TryGetValue(pageIndex, out int perPageDegrees))
                    {
                        userDegrees = perPageDegrees;
                    }

                    int userRotationSteps = (PageCoordinateTransform.NormalizeRotation(userDegrees) / 90) % 4;
                    if (userRotationSteps % 2 != 0)
                    {
                        (pageW, pageH) = (pageH, pageW);
                    }

                    double scale = Math.Min(printableW / Math.Max(1.0, pageW), printableH / Math.Max(1.0, pageH));
                    int targetW = Math.Max(1, (int)Math.Round(pageW * scale));
                    int targetH = Math.Max(1, (int)Math.Round(pageH * scale));
                    int offsetX = (printableW - targetW) / 2;
                    int offsetY = (printableH - targetH) / 2;

                    if (Win32PrintInterop.StartPage(hdc) <= 0)
                    {
                        _ = Win32PrintInterop.AbortDoc(hdc);
                        docStarted = false;
                        return false;
                    }

                    int savedDc = Win32PrintInterop.SaveDC(hdc);
                    try
                    {
                        int flags = (int)(PdfRenderFlags.Annotations | PdfRenderFlags.Printing);
                        PdfiumNative.FPDF_RenderPage(
                            hdc,
                            page,
                            offsetX,
                            offsetY,
                            targetW,
                            targetH,
                            userRotationSteps,
                            flags);
                    }
                    finally
                    {
                        if (savedDc != 0)
                        {
                            _ = Win32PrintInterop.RestoreDC(hdc, savedDc);
                        }
                    }

                    if (Win32PrintInterop.EndPage(hdc) <= 0)
                    {
                        _ = Win32PrintInterop.AbortDoc(hdc);
                        docStarted = false;
                        return false;
                    }
                }
            }

            _ = Win32PrintInterop.EndDoc(hdc);
            docStarted = false;
            return true;
        }
        catch
        {
            if (docStarted && hdc != nint.Zero)
            {
                _ = Win32PrintInterop.AbortDoc(hdc);
            }

            throw;
        }
        finally
        {
            if (docNamePtr != nint.Zero)
            {
                Marshal.FreeHGlobal(docNamePtr);
            }

            if (hdc != nint.Zero)
            {
                _ = Win32PrintInterop.DeleteDC(hdc);
            }
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

    /// <summary>Reads document metadata properties via FPDF_GetMetaText and FPDF_GetFileVersion.</summary>
    internal PdfMetadata ReadDocumentMetadata(long documentId, CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        string? title = ReadMetaText(document, "Title");
        string? author = ReadMetaText(document, "Author");
        string? subject = ReadMetaText(document, "Subject");
        string? keywords = ReadMetaText(document, "Keywords");
        string? creator = ReadMetaText(document, "Creator");
        string? producer = ReadMetaText(document, "Producer");
        string? creationDate = ReadMetaText(document, "CreationDate");
        string? modDate = ReadMetaText(document, "ModDate");

        string? pdfVersion = null;
        if (PdfiumNative.FPDF_GetFileVersion(document, out int fileVersion) != 0 && fileVersion > 0)
        {
            pdfVersion = $"PDF {fileVersion / 10}.{fileVersion % 10}";
        }

        return new PdfMetadata(
            title,
            author,
            subject,
            keywords,
            creator,
            producer,
            creationDate,
            modDate,
            pdfVersion);
    }

    private static string? ReadMetaText(PdfiumDocumentHandle document, string tag)
    {
        uint required = PdfiumNative.FPDF_GetMetaText(document, tag, null, 0);
        if (required <= sizeof(char))
        {
            return null;
        }

        int capacity = (int)Math.Min(required, MaxWideStringBytes);
        byte* buffer = stackalloc byte[MaxWideStringBytes];
        uint written = PdfiumNative.FPDF_GetMetaText(document, tag, buffer, (uint)capacity);
        int byteCount = (int)Math.Min(written, (uint)capacity) & ~1;

        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(new ReadOnlySpan<byte>(buffer, byteCount));
        int end = chars.IndexOf('\0');
        string result = new(end >= 0 ? chars[..end] : chars);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    private IReadOnlyList<OutlineItem> ReadBookmarkSiblings(
        PdfiumDocumentHandle document,
        nint parent,
        int depth,
        ref int budget,
        HashSet<nint> visited,
        CancellationToken cancellationToken)
    {
        if (depth >= MaxOutlineDepth)
        {
            return Array.Empty<OutlineItem>();
        }

        List<OutlineItem>? items = null;
        nint bookmark = PdfiumNative.FPDFBookmark_GetFirstChild(document, parent);
        int siblingsCount = 0;

        while (bookmark != nint.Zero && budget > 0 && siblingsCount < MaxSiblingsPerLevel)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Malformed files may contain sibling/child cycles.
            if (!visited.Add(bookmark))
            {
                break;
            }

            budget--;
            siblingsCount++;
            string title = ReadWideString(bookmark, static (handle, buffer, length) => PdfiumNative.FPDFBookmark_GetTitle(handle, buffer, length));
            int pageIndex = ResolveBookmarkPage(document, bookmark);
            IReadOnlyList<OutlineItem> children = (depth + 1 < MaxOutlineDepth)
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
    private static PdfPageInfo ReadPageInfo(PdfiumDocumentHandle document, int index, long documentId = 0)
    {
        using PdfiumPageHandle page = PdfiumNative.FPDF_LoadPage(document, index);
        if (page.IsInvalid)
        {
            return ReadPageInfoFromSize(document, index, rotation: 0, documentId);
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
            return ReadPageInfoFromSize(document, index, rotation, documentId);
        }

        return new PdfPageInfo(index, visible.Width, visible.Height, rotation, visible.Left, visible.Bottom, documentId);
    }

    // Fallback: FPDF_GetPageSizeByIndexF reports the rotated (displayed) size; undo the swap.
    private static PdfPageInfo ReadPageInfoFromSize(PdfiumDocumentHandle document, int index, int rotation, long documentId = 0)
    {
        FsSizeF size = default;
        if (PdfiumNative.FPDF_GetPageSizeByIndexF(document, index, &size) != 0 && size.Width > 0 && size.Height > 0)
        {
            bool swap = rotation is 90 or 270;
            return new PdfPageInfo(index, swap ? size.Height : size.Width, swap ? size.Width : size.Height, rotation, 0, 0, documentId);
        }

        return new PdfPageInfo(index, DefaultPageWidthPt, DefaultPageHeightPt, rotation, 0, 0, documentId);
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
