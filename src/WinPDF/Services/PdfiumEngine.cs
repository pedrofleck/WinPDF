using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using WinPDF.Interop.Pdfium;
using WinPDF.Interop.Printing;
using WinPDF.Models;
using WinPDF.Rendering;

namespace WinPDF.Services;

/// <summary>
/// All <c>FPDF_*</c> work: library lifetime, documents, page geometry, rendering, text, search, links, outline and attachments.
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
    private const int MaxStackAllocChars = 1024;
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
    // Text Search, Interactive Selection, and Clickable Links
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Scans all pages of <paramref name="documentId"/> for <paramref name="query"/> using <c>FPDFText_FindStart</c>
    /// and <c>FPDFText_FindNext</c>. Pages not currently in <see cref="_pageCache"/> are loaded and released
    /// transiently so visible page caches are never evicted and memory stays flat.
    /// </summary>
    internal IReadOnlyList<PdfSearchMatch> SearchDocument(
        long documentId,
        string query,
        bool matchCase,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(query))
        {
            return Array.Empty<PdfSearchMatch>();
        }

        PdfiumDocumentHandle document = GetDocument(documentId);
        int pageCount = Math.Max(0, PdfiumNative.FPDF_GetPageCount(document));
        if (pageCount == 0)
        {
            return Array.Empty<PdfSearchMatch>();
        }

        uint searchFlags = matchCase ? PdfiumConstants.MatchCase : 0u;
        List<PdfSearchMatch>? matches = null;

        int queryLen = query.Length;
        char[]? rentedQuery = null;
        Span<char> querySpan = queryLen + 1 <= MaxStackAllocChars
            ? stackalloc char[queryLen + 1]
            : (rentedQuery = System.Buffers.ArrayPool<char>.Shared.Rent(queryLen + 1)).AsSpan(0, queryLen + 1);

        try
        {
            query.AsSpan().CopyTo(querySpan);
            querySpan[queryLen] = '\0';

            fixed (char* queryPtr = querySpan)
            {
                for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (_pageCache.TryGetCached(documentId, pageIndex, out PdfiumPageHandle? cachedPage, out PdfiumTextPageHandle? cachedTextPage)
                        && cachedPage is { IsInvalid: false })
                    {
                        PdfiumTextPageHandle? textPage = cachedTextPage is { IsInvalid: false }
                            ? cachedTextPage
                            : _pageCache.GetOrLoadTextPage(document, documentId, pageIndex);

                        if (textPage is { IsInvalid: false })
                        {
                            SearchSingleTextPage(textPage, pageIndex, queryPtr, searchFlags, ref matches, cancellationToken);
                        }
                    }
                    else
                    {
                        using PdfiumPageHandle transientPage = PdfiumNative.FPDF_LoadPage(document, pageIndex);
                        if (transientPage.IsInvalid)
                        {
                            continue;
                        }

                        using PdfiumTextPageHandle transientTextPage = PdfiumNative.FPDFText_LoadPage(transientPage);
                        if (transientTextPage.IsInvalid)
                        {
                            continue;
                        }

                        SearchSingleTextPage(transientTextPage, pageIndex, queryPtr, searchFlags, ref matches, cancellationToken);
                    }
                }
            }
        }
        finally
        {
            if (rentedQuery is not null)
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rentedQuery);
            }
        }

        return matches is null ? Array.Empty<PdfSearchMatch>() : matches;
    }

    private static void SearchSingleTextPage(
        PdfiumTextPageHandle textPage,
        int pageIndex,
        char* queryPtr,
        uint searchFlags,
        ref List<PdfSearchMatch>? matches,
        CancellationToken cancellationToken)
    {
        using PdfiumSearchHandle searchHandle = PdfiumNative.FPDFText_FindStart(textPage, queryPtr, searchFlags, 0);
        if (searchHandle.IsInvalid)
        {
            return;
        }

        int matchIndexOnPage = 0;
        while (PdfiumNative.FPDFText_FindNext(searchHandle) != 0)
        {
            if ((matchIndexOnPage & 0x1F) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int startCharIndex = PdfiumNative.FPDFText_GetSchResultIndex(searchHandle);
            int charCount = PdfiumNative.FPDFText_GetSchCount(searchHandle);
            if (startCharIndex < 0 || charCount <= 0)
            {
                continue;
            }

            IReadOnlyList<PdfRect> rects = ExtractTextRects(textPage, startCharIndex, charCount);
            if (rects.Count > 0)
            {
                (matches ??= new List<PdfSearchMatch>()).Add(
                    new PdfSearchMatch(pageIndex, matchIndexOnPage++, startCharIndex, charCount, rects));
            }
        }
    }

    /// <summary>
    /// Hit-tests a point in PDF user space for both clickable links and selectable text characters.
    /// </summary>
    internal PdfPointerHitResult HitTestPagePoint(
        long documentId,
        int pageIndex,
        double pdfX,
        double pdfY,
        double tolerancePt,
        CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumPageHandle? page = _pageCache.GetOrLoad(document, documentId, pageIndex);
        if (page is null || page.IsInvalid)
        {
            return PdfPointerHitResult.Empty;
        }

        PdfLinkTarget link = ResolveLinkAtPoint(document, documentId, pageIndex, page, pdfX, pdfY);

        PdfiumTextPageHandle? textPage = _pageCache.GetOrLoadTextPage(document, documentId, pageIndex);
        int charIndex = -1;
        if (textPage is { IsInvalid: false })
        {
            double tol = Math.Max(1.0, tolerancePt);
            int hit = PdfiumNative.FPDFText_GetCharIndexAtPos(textPage, pdfX, pdfY, tol, tol);
            if (hit >= 0)
            {
                charIndex = hit;
            }
        }

        return new PdfPointerHitResult(charIndex, link);
    }

    /// <summary>
    /// Resolves any clickable internal or external link at <c>(pdfX, pdfY)</c> on <paramref name="pageIndex"/>.
    /// </summary>
    internal PdfLinkTarget GetLinkAtPoint(
        long documentId,
        int pageIndex,
        double pdfX,
        double pdfY,
        CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumPageHandle? page = _pageCache.GetOrLoad(document, documentId, pageIndex);
        if (page is null || page.IsInvalid)
        {
            return PdfLinkTarget.None;
        }

        return ResolveLinkAtPoint(document, documentId, pageIndex, page, pdfX, pdfY);
    }

    private PdfLinkTarget ResolveLinkAtPoint(
        PdfiumDocumentHandle document,
        long documentId,
        int pageIndex,
        PdfiumPageHandle page,
        double pdfX,
        double pdfY)
    {
        nint link = PdfiumNative.FPDFLink_GetLinkAtPoint(page, pdfX, pdfY);
        if (link != nint.Zero)
        {
            PdfLinkTarget resolved = ResolveNativeLinkHandle(document, link);
            if (resolved.IsValid)
            {
                return resolved;
            }
        }

        // Also check auto-detected web links in the page text stream (FPDFLink_LoadWebLinks).
        PdfiumPageLinkHandle? webLinks = _pageCache.GetOrLoadWebLinks(document, documentId, pageIndex);
        if (webLinks is { IsInvalid: false })
        {
            int count = PdfiumNative.FPDFLink_CountWebLinks(webLinks);
            for (int i = 0; i < count; i++)
            {
                int rectCount = PdfiumNative.FPDFLink_CountRects(webLinks, i);
                for (int r = 0; r < rectCount; r++)
                {
                    if (PdfiumNative.FPDFLink_GetRect(webLinks, i, r, out double left, out double top, out double right, out double bottom) != 0)
                    {
                        PdfRect rect = PdfRect.FromCorners(left, bottom, right, top);
                        if (rect.Contains(pdfX, pdfY, tolerancePt: 2.0))
                        {
                            string url = ReadWebLinkUrl(webLinks, i);
                            if (!string.IsNullOrWhiteSpace(url))
                            {
                                return new PdfLinkTarget(PdfLinkKind.ExternalUri, -1, url);
                            }
                        }
                    }
                }
            }
        }

        return PdfLinkTarget.None;
    }

    private static PdfLinkTarget ResolveNativeLinkHandle(PdfiumDocumentHandle document, nint link)
    {
        nint dest = PdfiumNative.FPDFLink_GetDest(document, link);
        if (dest != nint.Zero)
        {
            int targetPage = PdfiumNative.FPDFDest_GetDestPageIndex(document, dest);
            if (targetPage >= 0)
            {
                return new PdfLinkTarget(PdfLinkKind.InternalPage, targetPage, null);
            }
        }

        nint action = PdfiumNative.FPDFLink_GetAction(link);
        if (action != nint.Zero)
        {
            uint actionType = PdfiumNative.FPDFAction_GetType(action);
            if (actionType == PdfiumConstants.ActionGoto)
            {
                nint actionDest = PdfiumNative.FPDFAction_GetDest(document, action);
                if (actionDest != nint.Zero)
                {
                    int targetPage = PdfiumNative.FPDFDest_GetDestPageIndex(document, actionDest);
                    if (targetPage >= 0)
                    {
                        return new PdfLinkTarget(PdfLinkKind.InternalPage, targetPage, null);
                    }
                }
            }
            else if (actionType == PdfiumConstants.ActionUri)
            {
                string? uri = ReadActionUri(document, action);
                if (!string.IsNullOrWhiteSpace(uri))
                {
                    return new PdfLinkTarget(PdfLinkKind.ExternalUri, -1, uri);
                }
            }
        }

        return PdfLinkTarget.None;
    }

    private static string? ReadActionUri(PdfiumDocumentHandle document, nint action)
    {
        uint required = PdfiumNative.FPDFAction_GetURIPath(document, action, null, 0);
        if (required <= 1)
        {
            return null;
        }

        int capacity = (int)Math.Min(required, 2048u);
        byte* buffer = stackalloc byte[2048];
        uint written = PdfiumNative.FPDFAction_GetURIPath(document, action, buffer, (uint)capacity);
        int byteCount = (int)Math.Min(written, (uint)capacity);
        ReadOnlySpan<byte> bytes = new(buffer, byteCount);
        int nullIdx = bytes.IndexOf((byte)0);
        if (nullIdx >= 0)
        {
            bytes = bytes[..nullIdx];
        }

        string uri = Encoding.UTF8.GetString(bytes).Trim();
        return string.IsNullOrWhiteSpace(uri) ? null : uri;
    }

    private static string ReadWebLinkUrl(PdfiumPageLinkHandle webLinks, int linkIndex)
    {
        int requiredChars = PdfiumNative.FPDFLink_GetURL(webLinks, linkIndex, null, 0);
        if (requiredChars <= 1)
        {
            return string.Empty;
        }

        int capacity = Math.Min(requiredChars, MaxStackAllocChars);
        char* buffer = stackalloc char[MaxStackAllocChars];
        int written = PdfiumNative.FPDFLink_GetURL(webLinks, linkIndex, buffer, capacity);
        int charCount = Math.Clamp(written, 0, capacity);
        ReadOnlySpan<char> chars = new(buffer, charCount);
        int nullIdx = chars.IndexOf('\0');
        if (nullIdx >= 0)
        {
            chars = chars[..nullIdx];
        }

        return new string(chars).Trim();
    }

    /// <summary>
    /// Computes the updated <see cref="PdfTextSelection"/> while dragging from <paramref name="anchorCharIndex"/>
    /// (or <c>(anchorPdfX, anchorPdfY)</c> if <paramref name="anchorCharIndex"/> is negative) to <c>(currentPdfX, currentPdfY)</c>.
    /// </summary>
    internal PdfTextSelection? SelectTextByPoint(
        long documentId,
        int pageIndex,
        int anchorCharIndex,
        double anchorPdfX,
        double anchorPdfY,
        double currentPdfX,
        double currentPdfY,
        int previousEndCharIndex,
        CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumTextPageHandle? textPage = _pageCache.GetOrLoadTextPage(document, documentId, pageIndex);
        if (textPage is null || textPage.IsInvalid)
        {
            return null;
        }

        int totalChars = PdfiumNative.FPDFText_CountChars(textPage);
        if (totalChars <= 0)
        {
            return null;
        }

        int resolvedAnchor = anchorCharIndex;
        if (resolvedAnchor < 0 || resolvedAnchor >= totalChars)
        {
            resolvedAnchor = FindCharIndexForDrag(textPage, totalChars, anchorPdfX, anchorPdfY);
            if (resolvedAnchor < 0 || resolvedAnchor >= totalChars)
            {
                return null;
            }
        }

        int endCharIndex = FindCharIndexForDrag(textPage, totalChars, currentPdfX, currentPdfY);
        if (endCharIndex < 0 || endCharIndex == previousEndCharIndex)
        {
            return null;
        }

        int startChar = Math.Min(resolvedAnchor, endCharIndex);
        int endChar = Math.Max(resolvedAnchor, endCharIndex);
        int count = endChar - startChar + 1;

        IReadOnlyList<PdfRect> rects = ExtractTextRects(textPage, startChar, count);
        string selectedText = ExtractTextString(textPage, startChar, count);
        return new PdfTextSelection(pageIndex, startChar, endChar, rects, selectedText, resolvedAnchor, endCharIndex);
    }

    /// <summary>
    /// Selects the entire word at <c>(pdfX, pdfY)</c> on <paramref name="pageIndex"/>.
    /// </summary>
    internal PdfTextSelection? SelectWordAtPoint(
        long documentId,
        int pageIndex,
        double pdfX,
        double pdfY,
        double tolerancePt,
        CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumTextPageHandle? textPage = _pageCache.GetOrLoadTextPage(document, documentId, pageIndex);
        if (textPage is null || textPage.IsInvalid)
        {
            return null;
        }

        int totalChars = PdfiumNative.FPDFText_CountChars(textPage);
        if (totalChars <= 0)
        {
            return null;
        }

        double tol = Math.Max(2.0, tolerancePt);
        int hitIndex = PdfiumNative.FPDFText_GetCharIndexAtPos(textPage, pdfX, pdfY, tol, tol);
        if (hitIndex < 0)
        {
            hitIndex = PdfiumNative.FPDFText_GetCharIndexAtPos(textPage, pdfX, pdfY, tol * 2.0, tol * 1.5);
        }

        if (hitIndex < 0 || hitIndex >= totalChars)
        {
            return null;
        }

        char hitChar = (char)PdfiumNative.FPDFText_GetUnicode(textPage, hitIndex);
        if (char.IsWhiteSpace(hitChar) || hitChar == '\0')
        {
            return null;
        }

        bool isWordChar = IsWordCharacter(hitChar);
        int start = hitIndex;
        while (start > 0)
        {
            char prev = (char)PdfiumNative.FPDFText_GetUnicode(textPage, start - 1);
            if (prev == '\0' || char.IsWhiteSpace(prev) || IsWordCharacter(prev) != isWordChar)
            {
                break;
            }

            start--;
        }

        int end = hitIndex;
        while (end + 1 < totalChars)
        {
            char next = (char)PdfiumNative.FPDFText_GetUnicode(textPage, end + 1);
            if (next == '\0' || char.IsWhiteSpace(next) || IsWordCharacter(next) != isWordChar)
            {
                break;
            }

            end++;
        }

        int count = end - start + 1;
        IReadOnlyList<PdfRect> rects = ExtractTextRects(textPage, start, count);
        string selectedText = ExtractTextString(textPage, start, count);
        return new PdfTextSelection(pageIndex, start, end, rects, selectedText, start, end);
    }

    /// <summary>
    /// Selects the entire sentence (or paragraph/heading line if unpunctuated) at <c>(pdfX, pdfY)</c> on <paramref name="pageIndex"/>.
    /// </summary>
    internal PdfTextSelection? SelectSentenceAtPoint(
        long documentId,
        int pageIndex,
        double pdfX,
        double pdfY,
        double tolerancePt,
        CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumTextPageHandle? textPage = _pageCache.GetOrLoadTextPage(document, documentId, pageIndex);
        if (textPage is null || textPage.IsInvalid)
        {
            return null;
        }

        int totalChars = PdfiumNative.FPDFText_CountChars(textPage);
        if (totalChars <= 0)
        {
            return null;
        }

        double tol = Math.Max(4.0, tolerancePt);
        int hitIndex = PdfiumNative.FPDFText_GetCharIndexAtPos(textPage, pdfX, pdfY, tol, tol);
        if (hitIndex < 0)
        {
            hitIndex = FindCharIndexForDrag(textPage, totalChars, pdfX, pdfY);
        }

        if (hitIndex < 0 || hitIndex >= totalChars)
        {
            return null;
        }

        // If the user triple-clicked on a space between two words on a line, nudge to the nearest non-whitespace char.
        if (char.IsWhiteSpace((char)PdfiumNative.FPDFText_GetUnicode(textPage, hitIndex)))
        {
            if (hitIndex > 0 && !char.IsWhiteSpace((char)PdfiumNative.FPDFText_GetUnicode(textPage, hitIndex - 1)))
            {
                hitIndex--;
            }
            else if (hitIndex + 1 < totalChars && !char.IsWhiteSpace((char)PdfiumNative.FPDFText_GetUnicode(textPage, hitIndex + 1)))
            {
                hitIndex++;
            }
            else
            {
                return null;
            }
        }

        double lineHeight = 12.0;
        if (TryGetCharVerticalBounds(textPage, hitIndex, out double hitBottom, out double hitTop))
        {
            lineHeight = Math.Clamp(hitTop - hitBottom, 6.0, 48.0);
        }

        double maxParagraphGapPt = lineHeight * 1.85;

        // 1. Expand backwards to the start of the sentence or text block.
        int start = hitIndex;
        double currentRefBottom = hitBottom;
        while (start > 0)
        {
            int prevIdx = start - 1;
            char prevChar = (char)PdfiumNative.FPDFText_GetUnicode(textPage, prevIdx);
            if (prevChar == '\0')
            {
                break;
            }

            char curChar = (char)PdfiumNative.FPDFText_GetUnicode(textPage, start);

            // Stop at double newline (paragraph break).
            if ((prevChar == '\n' || prevChar == '\r') && prevIdx > 0)
            {
                char prevPrev = (char)PdfiumNative.FPDFText_GetUnicode(textPage, prevIdx - 1);
                if (prevPrev == '\n' && prevChar == '\n')
                {
                    break;
                }

                if (prevIdx >= 2 &&
                    prevChar == '\n' &&
                    prevPrev == '\r' &&
                    (char)PdfiumNative.FPDFText_GetUnicode(textPage, prevIdx - 2) == '\n')
                {
                    break;
                }
            }

            // Stop after sentence-ending punctuation followed by whitespace (ignoring decimal points like 3.14).
            if (IsSentenceTerminator(prevChar) && char.IsWhiteSpace(curChar))
            {
                break;
            }

            if (IsClosingDelimiter(prevChar) && prevIdx > 0 &&
                IsSentenceTerminator((char)PdfiumNative.FPDFText_GetUnicode(textPage, prevIdx - 1)) &&
                char.IsWhiteSpace(curChar))
            {
                break;
            }

            if (!char.IsWhiteSpace(prevChar) && TryGetCharVerticalBounds(textPage, prevIdx, out double prevBottom, out double prevTop))
            {
                double prevHeight = Math.Clamp(prevTop - prevBottom, 6.0, 64.0);
                // Stop if there is a large vertical gap or a significant font-size change (e.g. heading vs body).
                if (Math.Abs(prevBottom - currentRefBottom) > Math.Max(maxParagraphGapPt, prevHeight * 1.85) ||
                    Math.Abs(prevHeight - lineHeight) > lineHeight * 0.45)
                {
                    break;
                }

                currentRefBottom = prevBottom;
            }

            start--;
        }

        // 2. Expand forwards to the end of the sentence or text block.
        int end = hitIndex;
        currentRefBottom = hitBottom;
        while (end + 1 < totalChars)
        {
            char curChar = (char)PdfiumNative.FPDFText_GetUnicode(textPage, end);
            int nextIdx = end + 1;
            char nextChar = (char)PdfiumNative.FPDFText_GetUnicode(textPage, nextIdx);
            if (nextChar == '\0')
            {
                break;
            }

            // Check if current character terminates the sentence.
            if (IsSentenceTerminator(curChar))
            {
                if (char.IsWhiteSpace(nextChar))
                {
                    break;
                }

                if (IsClosingDelimiter(nextChar) &&
                    (nextIdx + 1 >= totalChars || char.IsWhiteSpace((char)PdfiumNative.FPDFText_GetUnicode(textPage, nextIdx + 1))))
                {
                    end = nextIdx;
                    break;
                }
            }

            // Stop at double newline.
            if (curChar == '\n' && (nextChar == '\n' || nextChar == '\r'))
            {
                break;
            }

            if (!char.IsWhiteSpace(nextChar) && TryGetCharVerticalBounds(textPage, nextIdx, out double nextBottom, out double nextTop))
            {
                double nextHeight = Math.Clamp(nextTop - nextBottom, 6.0, 64.0);
                if (Math.Abs(nextBottom - currentRefBottom) > Math.Max(maxParagraphGapPt, nextHeight * 1.85) ||
                    Math.Abs(nextHeight - lineHeight) > lineHeight * 0.45)
                {
                    break;
                }

                currentRefBottom = nextBottom;
            }

            end++;
        }

        // Trim leading and trailing whitespace/newlines.
        while (start < end && char.IsWhiteSpace((char)PdfiumNative.FPDFText_GetUnicode(textPage, start)))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace((char)PdfiumNative.FPDFText_GetUnicode(textPage, end)))
        {
            end--;
        }

        int count = end - start + 1;
        IReadOnlyList<PdfRect> rects = ExtractTextRects(textPage, start, count);
        string selectedText = ExtractTextString(textPage, start, count);
        return new PdfTextSelection(pageIndex, start, end, rects, selectedText, start, end);
    }

    private static bool IsSentenceTerminator(char c) =>
        c is '.' or '!' or '?' or '…';

    private static bool IsClosingDelimiter(char c) =>
        c is '"' or '\'' or '”' or '’' or ')' or ']' or '»';

    private static bool TryGetCharVerticalBounds(PdfiumTextPageHandle textPage, int charIndex, out double bottom, out double top)
    {
        if (PdfiumNative.FPDFText_GetLooseCharBox(textPage, charIndex, out FsRectF box) != 0)
        {
            bottom = Math.Min(box.Bottom, box.Top);
            top = Math.Max(box.Bottom, box.Top);
            if (top > bottom)
            {
                return true;
            }
        }

        if (PdfiumNative.FPDFText_GetCharBox(textPage, charIndex, out _, out _, out double b, out double t) != 0)
        {
            bottom = Math.Min(b, t);
            top = Math.Max(b, t);
            if (top > bottom)
            {
                return true;
            }
        }

        bottom = 0.0;
        top = 0.0;
        return false;
    }

    /// <summary>
    /// Selects an explicit character range <c>[startCharIndex, startCharIndex + charCount - 1]</c> on <paramref name="pageIndex"/>.
    /// </summary>
    internal PdfTextSelection? SelectTextRange(
        long documentId,
        int pageIndex,
        int startCharIndex,
        int charCount,
        CancellationToken cancellationToken)
    {
        if (startCharIndex < 0 || charCount <= 0)
        {
            return null;
        }

        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumTextPageHandle? textPage = _pageCache.GetOrLoadTextPage(document, documentId, pageIndex);
        if (textPage is null || textPage.IsInvalid)
        {
            return null;
        }

        int totalChars = PdfiumNative.FPDFText_CountChars(textPage);
        if (totalChars <= 0 || startCharIndex >= totalChars)
        {
            return null;
        }

        int clampedCount = Math.Min(charCount, totalChars - startCharIndex);
        int endCharIndex = startCharIndex + clampedCount - 1;
        IReadOnlyList<PdfRect> rects = ExtractTextRects(textPage, startCharIndex, clampedCount);
        string selectedText = ExtractTextString(textPage, startCharIndex, clampedCount);
        return new PdfTextSelection(pageIndex, startCharIndex, endCharIndex, rects, selectedText, startCharIndex, endCharIndex);
    }

    /// <summary>
    /// Extracts the text bounded by <paramref name="bounds"/> in PDF user space via <c>FPDFText_GetBoundedText</c>.
    /// </summary>
    internal string GetBoundedText(
        long documentId,
        int pageIndex,
        PdfRect bounds,
        CancellationToken cancellationToken)
    {
        if (bounds.IsEmpty)
        {
            return string.Empty;
        }

        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumTextPageHandle? textPage = _pageCache.GetOrLoadTextPage(document, documentId, pageIndex);
        if (textPage is null || textPage.IsInvalid)
        {
            return string.Empty;
        }

        int required = PdfiumNative.FPDFText_GetBoundedText(
            textPage,
            bounds.Left,
            bounds.Top,
            bounds.Right,
            bounds.Bottom,
            null,
            0);

        if (required <= 1)
        {
            return string.Empty;
        }

        if (required <= MaxStackAllocChars)
        {
            char* buf = stackalloc char[MaxStackAllocChars];
            int written = PdfiumNative.FPDFText_GetBoundedText(
                textPage,
                bounds.Left,
                bounds.Top,
                bounds.Right,
                bounds.Bottom,
                buf,
                required);
            return TrimNullTerminatedSpan(new ReadOnlySpan<char>(buf, Math.Clamp(written, 0, required)));
        }

        char* heapBuf = (char*)NativeMemory.Alloc((nuint)required, sizeof(char));
        try
        {
            int written = PdfiumNative.FPDFText_GetBoundedText(
                textPage,
                bounds.Left,
                bounds.Top,
                bounds.Right,
                bounds.Bottom,
                heapBuf,
                required);
            return TrimNullTerminatedSpan(new ReadOnlySpan<char>(heapBuf, Math.Clamp(written, 0, required)));
        }
        finally
        {
            NativeMemory.Free(heapBuf);
        }
    }

    private static bool IsWordCharacter(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '-' or '\'' or '’';

    private static int FindCharIndexForDrag(PdfiumTextPageHandle textPage, int totalChars, double pdfX, double pdfY)
    {
        int hit = PdfiumNative.FPDFText_GetCharIndexAtPos(textPage, pdfX, pdfY, 6.0, 6.0);
        if (hit >= 0)
        {
            return hit;
        }

        hit = PdfiumNative.FPDFText_GetCharIndexAtPos(textPage, pdfX, pdfY, 18.0, 14.0);
        if (hit >= 0)
        {
            return hit;
        }

        hit = PdfiumNative.FPDFText_GetCharIndexAtPos(textPage, pdfX, pdfY, 64.0, 20.0);
        if (hit >= 0)
        {
            return hit;
        }

        // Fallback when dragging into page margins or inter-paragraph whitespace: snap to the closest character box.
        int bestIndex = -1;
        double bestScore = double.MaxValue;
        int limit = Math.Min(totalChars, 8000);

        for (int i = 0; i < limit; i++)
        {
            if (PdfiumNative.FPDFText_GetLooseCharBox(textPage, i, out FsRectF box) == 0)
            {
                continue;
            }

            double left = Math.Min(box.Left, box.Right);
            double right = Math.Max(box.Left, box.Right);
            double bottom = Math.Min(box.Bottom, box.Top);
            double top = Math.Max(box.Bottom, box.Top);

            double dy = pdfY < bottom ? (bottom - pdfY) : (pdfY > top ? (pdfY - top) : 0.0);
            double dx = pdfX < left ? (left - pdfX) : (pdfX > right ? (pdfX - right) : 0.0);

            // Weight vertical distance heavily so dragging horizontally in a line's margin stays on that line.
            double score = (dy * 12.0) + dx;
            if (score < bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static IReadOnlyList<PdfRect> ExtractTextRects(PdfiumTextPageHandle textPage, int startCharIndex, int charCount)
    {
        if (charCount <= 0)
        {
            return Array.Empty<PdfRect>();
        }

        int rectCount = PdfiumNative.FPDFText_CountRects(textPage, startCharIndex, charCount);
        if (rectCount <= 0)
        {
            return Array.Empty<PdfRect>();
        }

        List<PdfRect> rects = new(rectCount);
        for (int r = 0; r < rectCount; r++)
        {
            if (PdfiumNative.FPDFText_GetRect(textPage, r, out double left, out double top, out double right, out double bottom) == 0)
            {
                continue;
            }

            PdfRect candidate = PdfRect.FromCorners(left, bottom, right, top);
            if (candidate.IsEmpty)
            {
                continue;
            }

            // Coalesce horizontally adjacent segments on the same text baseline to eliminate overlapping alpha seams.
            if (rects.Count > 0)
            {
                PdfRect prev = rects[^1];
                if (Math.Abs(prev.Top - candidate.Top) <= 2.5f &&
                    Math.Abs(prev.Bottom - candidate.Bottom) <= 2.5f &&
                    candidate.Left >= prev.Left - 2.0f &&
                    candidate.Left - prev.Right <= 4.0f)
                {
                    rects[^1] = new PdfRect(
                        Math.Min(prev.Left, candidate.Left),
                        Math.Min(prev.Bottom, candidate.Bottom),
                        Math.Max(prev.Right, candidate.Right),
                        Math.Max(prev.Top, candidate.Top));
                    continue;
                }
            }

            rects.Add(candidate);
        }

        return rects;
    }

    private static string ExtractTextString(PdfiumTextPageHandle textPage, int startCharIndex, int charCount)
    {
        if (charCount <= 0)
        {
            return string.Empty;
        }

        int bufferLength = charCount + 1;
        if (bufferLength <= MaxStackAllocChars)
        {
            char* buf = stackalloc char[MaxStackAllocChars];
            int written = PdfiumNative.FPDFText_GetText(textPage, startCharIndex, charCount, buf);
            return TrimNullTerminatedSpan(new ReadOnlySpan<char>(buf, Math.Clamp(written, 0, bufferLength)));
        }

        // Zero-LOH: use unmanaged memory for large selections so >= 85 KB strings never allocate temporary arrays on the LOH.
        char* nativeBuf = (char*)NativeMemory.Alloc((nuint)bufferLength, sizeof(char));
        try
        {
            int written = PdfiumNative.FPDFText_GetText(textPage, startCharIndex, charCount, nativeBuf);
            return TrimNullTerminatedSpan(new ReadOnlySpan<char>(nativeBuf, Math.Clamp(written, 0, bufferLength)));
        }
        finally
        {
            NativeMemory.Free(nativeBuf);
        }
    }

    private static string TrimNullTerminatedSpan(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
        {
            return string.Empty;
        }

        int nullIdx = span.IndexOf('\0');
        if (nullIdx >= 0)
        {
            span = span[..nullIdx];
        }

        return span.IsEmpty ? string.Empty : new string(span);
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
