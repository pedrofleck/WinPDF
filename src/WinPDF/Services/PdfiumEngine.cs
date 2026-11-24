using System.Globalization;
using System.Numerics;
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
    private readonly Dictionary<long, Dictionary<string, nint>> _documentFonts = new();
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

        void* formInfo = NativeMemory.AllocZeroed(1024);
        *(int*)formInfo = 1; // FPDF_FORMFILLINFO version 1 (all callbacks optional for read-only rendering)
        nint formHandle = PdfiumNative.FPDFDOC_InitFormFillEnvironment(document, formInfo);
        if (formHandle != nint.Zero)
        {
            document.FormFillInfoPtr = formInfo;
            document.FormHandle = formHandle;
        }
        else
        {
            NativeMemory.Free(formInfo);
        }

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

                if (i == 0)
                {
                    pages.Add(ReadPageInfo(document, 0, id));
                }
                else
                {
                    pages.Add(ReadPageInfoFast(document, i, id));
                }
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

    /// <summary>
    /// Dynamically sets the page handle LRU cache capacity.
    /// </summary>
    internal void SetPageCacheCapacity(int capacity)
    {
        _pageCache.SetCapacity(capacity);
    }

    /// <summary>
    /// Trims cached page handles outside [currentPage - margin, currentPage + margin].
    /// </summary>
    internal void TrimPagesOutside(long documentId, int currentPage, int margin = 1)
    {
        _pageCache.TrimPagesOutside(documentId, currentPage, margin);
    }

    /// <summary>Closes a document; its cached pages and fonts are closed first.</summary>
    internal void CloseDocument(long documentId)
    {
        if (_documents.Remove(documentId, out PdfiumDocumentHandle? document))
        {
            _pageCache.RemoveDocument(documentId);
            ReleaseDocumentFonts(documentId);
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

            if (System.IO.File.Exists(fullTargetPath))
            {
                string backupPath = System.IO.Path.Combine(
                    targetDir ?? System.IO.Path.GetTempPath(),
                    $".{System.IO.Path.GetFileName(fullTargetPath)}.{Guid.NewGuid():N}.winpdf.old");
                System.IO.File.Move(fullTargetPath, backupPath);
                try
                {
                    System.IO.File.Move(tempFilePath, fullTargetPath);
                }
                catch
                {
                    try { System.IO.File.Move(backupPath, fullTargetPath); } catch { }
                    throw;
                }

                try
                {
                    System.IO.File.SetAttributes(backupPath, FileAttributes.Hidden);
                    System.IO.File.Delete(backupPath);
                }
                catch
                {
                }
            }
            else
            {
                System.IO.File.Move(tempFilePath, fullTargetPath);
            }
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
    /// Computes an updated single- or multi-page <see cref="PdfTextSelection"/> between an anchor page/character
    /// and an active page/coordinate, supporting directional clamping and initial-word preservation.
    /// </summary>
    internal PdfTextSelection? SelectTextAcrossPages(
        long documentId,
        int anchorPageIndex,
        int anchorCharIndex,
        double anchorPdfX,
        double anchorPdfY,
        int activePageIndex,
        double activePdfX,
        double activePdfY,
        PdfSelectionAnchorClamp clampMode,
        int initialWordPageIndex,
        int initialWordStartCharIndex,
        int initialWordEndCharIndex,
        int previousActivePageIndex,
        int previousActiveCharIndex,
        CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        int pageCount = Math.Max(0, PdfiumNative.FPDF_GetPageCount(document));
        if (anchorPageIndex < 0 || anchorPageIndex >= pageCount ||
            activePageIndex < 0 || activePageIndex >= pageCount)
        {
            return null;
        }

        PdfiumTextPageHandle? anchorTextPage = _pageCache.GetOrLoadTextPage(document, documentId, anchorPageIndex);
        int anchorTotalChars = anchorTextPage is { IsInvalid: false }
            ? PdfiumNative.FPDFText_CountChars(anchorTextPage)
            : 0;

        int resolvedAnchor = -1;
        if (anchorTotalChars > 0 && anchorTextPage is not null)
        {
            resolvedAnchor = (anchorCharIndex >= 0 && anchorCharIndex < anchorTotalChars)
                ? anchorCharIndex
                : FindCharIndexForDrag(anchorTextPage, anchorTotalChars, anchorPdfX, anchorPdfY);
        }

        bool clampedCrossPageToAnchor =
            (clampMode == PdfSelectionAnchorClamp.AtOrBeforeAnchor && activePageIndex > anchorPageIndex) ||
            (clampMode == PdfSelectionAnchorClamp.AtOrAfterAnchor && activePageIndex < anchorPageIndex);

        if (clampedCrossPageToAnchor)
        {
            activePageIndex = anchorPageIndex;
        }

        // Single-page selection path (including cross-page handle drag clamped back to anchor).
        if (anchorPageIndex == activePageIndex)
        {
            if (anchorTextPage is null || anchorTextPage.IsInvalid || anchorTotalChars <= 0 ||
                resolvedAnchor < 0 || resolvedAnchor >= anchorTotalChars)
            {
                return null;
            }

            int activeChar = clampedCrossPageToAnchor
                ? resolvedAnchor
                : FindCharIndexForDrag(anchorTextPage, anchorTotalChars, activePdfX, activePdfY);
            if (activeChar < 0 || activeChar >= anchorTotalChars)
            {
                return null;
            }

            if (clampMode == PdfSelectionAnchorClamp.AtOrBeforeAnchor && activeChar > resolvedAnchor)
            {
                activeChar = resolvedAnchor;
            }
            else if (clampMode == PdfSelectionAnchorClamp.AtOrAfterAnchor && activeChar < resolvedAnchor)
            {
                activeChar = resolvedAnchor;
            }

            int startChar;
            int endChar;
            int outAnchor = resolvedAnchor;

            if (clampMode == PdfSelectionAnchorClamp.None &&
                initialWordPageIndex == anchorPageIndex &&
                initialWordStartCharIndex >= 0 &&
                initialWordEndCharIndex >= initialWordStartCharIndex)
            {
                int wordStart = Math.Clamp(initialWordStartCharIndex, 0, anchorTotalChars - 1);
                int wordEnd = Math.Clamp(initialWordEndCharIndex, wordStart, anchorTotalChars - 1);
                if (activeChar < wordStart)
                {
                    startChar = activeChar;
                    endChar = wordEnd;
                    outAnchor = wordEnd;
                }
                else if (activeChar > wordEnd)
                {
                    startChar = wordStart;
                    endChar = activeChar;
                    outAnchor = wordStart;
                }
                else
                {
                    startChar = wordStart;
                    endChar = wordEnd;
                    outAnchor = wordStart;
                    activeChar = wordEnd;
                }
            }
            else
            {
                startChar = Math.Min(resolvedAnchor, activeChar);
                endChar = Math.Max(resolvedAnchor, activeChar);
            }

            if (activePageIndex == previousActivePageIndex &&
                activeChar == previousActiveCharIndex &&
                outAnchor == anchorCharIndex)
            {
                return null;
            }

            int count = endChar - startChar + 1;
            IReadOnlyList<PdfRect> rects = ExtractTextRects(anchorTextPage, startChar, count);
            string selectedText = ExtractTextString(anchorTextPage, startChar, count);
            return new PdfTextSelection(
                anchorPageIndex,
                startChar,
                endChar,
                rects,
                selectedText,
                AnchorCharIndex: outAnchor,
                ActiveCharIndex: activeChar,
                Pages: null,
                AnchorPageIndex: anchorPageIndex,
                ActivePageIndex: anchorPageIndex);
        }

        // Multi-page selection path.
        bool isForward = activePageIndex > anchorPageIndex;
        int effectiveAnchor = resolvedAnchor;

        if (clampMode == PdfSelectionAnchorClamp.None &&
            initialWordPageIndex == anchorPageIndex &&
            initialWordStartCharIndex >= 0 &&
            initialWordEndCharIndex >= initialWordStartCharIndex &&
            anchorTotalChars > 0)
        {
            int wordStart = Math.Clamp(initialWordStartCharIndex, 0, anchorTotalChars - 1);
            int wordEnd = Math.Clamp(initialWordEndCharIndex, wordStart, anchorTotalChars - 1);
            effectiveAnchor = isForward ? wordStart : wordEnd;
        }

        PdfiumTextPageHandle? activeTextPage = _pageCache.GetOrLoadTextPage(document, documentId, activePageIndex);
        int activeTotalChars = activeTextPage is { IsInvalid: false }
            ? PdfiumNative.FPDFText_CountChars(activeTextPage)
            : 0;
        int activeEndpointChar = (activeTotalChars > 0 && activeTextPage is not null)
            ? FindCharIndexForDrag(activeTextPage, activeTotalChars, activePdfX, activePdfY)
            : -1;

        if (activePageIndex == previousActivePageIndex &&
            activeEndpointChar == previousActiveCharIndex &&
            effectiveAnchor == anchorCharIndex)
        {
            return null;
        }

        int minPage = Math.Min(anchorPageIndex, activePageIndex);
        int maxPage = Math.Max(anchorPageIndex, activePageIndex);
        List<PdfPageTextSelection> pageSelections = new(maxPage - minPage + 1);
        StringBuilder combinedText = new();

        for (int p = minPage; p <= maxPage; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PdfiumPageHandle? transientPage = null;
            PdfiumTextPageHandle? transientTextPage = null;
            try
            {
                PdfiumTextPageHandle? pText = null;
                if (p == anchorPageIndex)
                {
                    pText = anchorTextPage;
                }
                else if (p == activePageIndex)
                {
                    pText = activeTextPage;
                }
                else if (_pageCache.TryGetCached(documentId, p, out PdfiumPageHandle? cachedPage, out PdfiumTextPageHandle? cachedText)
                         && cachedPage is { IsInvalid: false })
                {
                    pText = cachedText is { IsInvalid: false }
                        ? cachedText
                        : _pageCache.GetOrLoadTextPage(document, documentId, p);
                }
                else
                {
                    transientPage = PdfiumNative.FPDF_LoadPage(document, p);
                    if (!transientPage.IsInvalid)
                    {
                        transientTextPage = PdfiumNative.FPDFText_LoadPage(transientPage);
                        if (!transientTextPage.IsInvalid)
                        {
                            pText = transientTextPage;
                        }
                    }
                }

                if (pText is null || pText.IsInvalid)
                {
                    continue;
                }

                int pTotal = PdfiumNative.FPDFText_CountChars(pText);
                if (pTotal <= 0)
                {
                    continue;
                }

                int pStart;
                int pEnd;
                if (p == anchorPageIndex)
                {
                    if (effectiveAnchor < 0 || effectiveAnchor >= pTotal)
                    {
                        continue;
                    }

                    if (isForward)
                    {
                        pStart = effectiveAnchor;
                        pEnd = pTotal - 1;
                    }
                    else
                    {
                        pStart = 0;
                        pEnd = effectiveAnchor;
                    }
                }
                else if (p == activePageIndex)
                {
                    if (activeEndpointChar < 0 || activeEndpointChar >= pTotal)
                    {
                        continue;
                    }

                    if (isForward)
                    {
                        pStart = 0;
                        pEnd = activeEndpointChar;
                    }
                    else
                    {
                        pStart = activeEndpointChar;
                        pEnd = pTotal - 1;
                    }
                }
                else
                {
                    pStart = 0;
                    pEnd = pTotal - 1;
                }

                if (pEnd < pStart)
                {
                    continue;
                }

                int count = pEnd - pStart + 1;
                IReadOnlyList<PdfRect> rects = ExtractTextRects(pText, pStart, count);
                string text = ExtractTextString(pText, pStart, count);
                if (rects.Count == 0 && string.IsNullOrEmpty(text))
                {
                    continue;
                }

                pageSelections.Add(new PdfPageTextSelection(p, pStart, pEnd, rects, text));
                if (!string.IsNullOrEmpty(text))
                {
                    if (combinedText.Length > 0)
                    {
                        combinedText.AppendLine();
                    }

                    combinedText.Append(text);
                }
            }
            finally
            {
                transientTextPage?.Dispose();
                transientPage?.Dispose();
            }
        }

        if (pageSelections.Count == 0)
        {
            return null;
        }

        PdfPageTextSelection first = pageSelections[0];
        PdfPageTextSelection last = pageSelections[^1];
        return new PdfTextSelection(
            first.PageIndex,
            first.StartCharIndex,
            last.EndCharIndex,
            first.Rects,
            combinedText.ToString(),
            AnchorCharIndex: effectiveAnchor,
            ActiveCharIndex: activeEndpointChar,
            Pages: pageSelections,
            AnchorPageIndex: anchorPageIndex,
            ActivePageIndex: activePageIndex);
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
        int stride = Math.Max(1, (totalChars + 7999) / 8000);

        for (int i = 0; i < totalChars; i += stride)
        {
            double score = ScoreCharBoxDistance(textPage, i, pdfX, pdfY);
            if (score < bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        if (stride > 1 && bestIndex >= 0)
        {
            int refineStart = Math.Max(0, bestIndex - stride);
            int refineEnd = Math.Min(totalChars - 1, bestIndex + stride);
            for (int i = refineStart; i <= refineEnd; i++)
            {
                double score = ScoreCharBoxDistance(textPage, i, pdfX, pdfY);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }
        }

        return bestIndex;
    }

    private static double ScoreCharBoxDistance(PdfiumTextPageHandle textPage, int charIndex, double pdfX, double pdfY)
    {
        if (PdfiumNative.FPDFText_GetLooseCharBox(textPage, charIndex, out FsRectF box) == 0)
        {
            return double.MaxValue;
        }

        double left = Math.Min(box.Left, box.Right);
        double right = Math.Max(box.Left, box.Right);
        double bottom = Math.Min(box.Bottom, box.Top);
        double top = Math.Max(box.Bottom, box.Top);

        double dy = pdfY < bottom ? (bottom - pdfY) : (pdfY > top ? (pdfY - top) : 0.0);
        double dx = pdfX < left ? (left - pdfX) : (pdfX > right ? (pdfX - right) : 0.0);

        // Weight vertical distance heavily so dragging horizontally in a line's margin stays on that line.
        return (dy * 12.0) + dx;
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
        string result;
        if (bufferLength <= MaxStackAllocChars)
        {
            char* buf = stackalloc char[MaxStackAllocChars];
            int written = PdfiumNative.FPDFText_GetText(textPage, startCharIndex, charCount, buf);
            result = TrimNullTerminatedSpan(new ReadOnlySpan<char>(buf, Math.Clamp(written, 0, bufferLength)));
        }
        else
        {
            // Zero-LOH: use unmanaged memory for large selections so >= 85 KB strings never allocate temporary arrays on the LOH.
            char* nativeBuf = (char*)NativeMemory.Alloc((nuint)bufferLength, sizeof(char));
            try
            {
                int written = PdfiumNative.FPDFText_GetText(textPage, startCharIndex, charCount, nativeBuf);
                result = TrimNullTerminatedSpan(new ReadOnlySpan<char>(nativeBuf, Math.Clamp(written, 0, bufferLength)));
            }
            finally
            {
                NativeMemory.Free(nativeBuf);
            }
        }

        if (!string.IsNullOrEmpty(result))
        {
            return result;
        }

        // Fallback via per-character FPDFText_GetUnicode in case FPDFText_GetText encountered an embedded NUL or unmapped glyph prefix.
        System.Text.StringBuilder sb = new(charCount);
        for (int i = 0; i < charCount; i++)
        {
            uint u = PdfiumNative.FPDFText_GetUnicode(textPage, startCharIndex + i);
            if (u is > 0 and <= 0xFFFF)
            {
                sb.Append((char)u);
            }
            else if (u > 0xFFFF && u <= 0x10FFFF)
            {
                sb.Append(char.ConvertFromUtf32((int)u));
            }
        }

        return sb.ToString();
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

    // Fast path for bulk pages: reads page dimensions directly from dictionary metadata (FPDF_GetPageSizeByIndexF)
    // without stream decompression or memory allocations; falls back to full ReadPageInfo on error.
    private static PdfPageInfo ReadPageInfoFast(PdfiumDocumentHandle document, int index, long documentId = 0)
    {
        FsSizeF size = default;
        if (PdfiumNative.FPDF_GetPageSizeByIndexF(document, index, &size) != 0 && size.Width > 0 && size.Height > 0)
        {
            return new PdfPageInfo(index, size.Width, size.Height, 0, 0, 0, documentId);
        }

        return ReadPageInfo(document, index, documentId);
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
            int renderFlags = (int)(request.Flags | PdfRenderFlags.LimitedImageCache);
            PdfiumNative.FPDF_RenderPageBitmap(
                bitmap,
                page,
                request.OffsetX,
                request.OffsetY,
                request.PageWidthPx,
                request.PageHeightPx,
                rotate,
                renderFlags);

            if (document.FormHandle != nint.Zero && (request.Flags & PdfRenderFlags.Annotations) != 0)
            {
                // Omit FPDF_ANNOT when calling FPDF_FFLDraw so signature and form widgets render cleanly without hover popups.
                int fflFlags = renderFlags & ~(int)PdfRenderFlags.Annotations;
                PdfiumNative.FPDF_FFLDraw(
                    document.FormHandle,
                    bitmap,
                    page,
                    request.OffsetX,
                    request.OffsetY,
                    request.PageWidthPx,
                    request.PageHeightPx,
                    rotate,
                    fflFlags);
            }

            long fileSize = document.StreamAccess?.FileLength ?? 0;
            int docPageCount = Math.Max(1, PdfiumNative.FPDF_GetPageCount(document));
            if (fileSize > 50L * 1024 * 1024 || (fileSize / docPageCount) > 5L * 1024 * 1024)
            {
                _pageCache.RemovePage(request.DocumentId, request.PageIndex);
            }

            // A render in progress cannot be interrupted; honor cancellation by discarding its result.
            cancellationToken.ThrowIfCancellationRequested();

            void* scan0 = PdfiumNative.FPDFBitmap_GetBuffer(bitmap);
            int stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);

            // Enhance text contrast and trim subpixel antialiasing blur in-place to match DirectWrite / ClearType.
            ApplyContrastLut((byte*)scan0, request.BitmapHeight, stride, request.BitmapWidth);

            return new PdfBitmapLease(bitmap, request.BitmapWidth, request.BitmapHeight, stride, scan0, owner);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static readonly byte[] s_contrastLut = InitializeContrastLut();

    // private static byte[] InitializeContrastLut()
    // {
    //     byte[] lut = new byte[256];
    //     double thresh = 15.1; // Trim faint antialiasing halo (> 239.9 sRGB to pure white)
    //     double gamma = 1.25;  // DirectWrite / ClearType gamma contrast enhancement
    //     for (int i = 0; i < 256; i++)
    //     {
    //         double ink = 255.0 - i;
    //         if (ink <= thresh)
    //         {
    //             lut[i] = 255;
    //         }
    //         else
    //         {
    //             double adj = (ink - thresh) / (255.0 - thresh);
    //             lut[i] = (byte)Math.Clamp(Math.Round(255.0 - Math.Pow(adj, gamma) * 255.0), 0, 255);
    //         }
    //     }

    //     return lut;
    // }

    private static byte[] InitializeContrastLut()
{
    byte[] lut = new byte[256];
    
    // 1. White Threshold: removes the white halo (> 239.9 sRGB to pure white)
    double whiteThresh = 18; // default = 15.1
    
    // 2. Black Threshold: any pixel with brightness <= 45 (ink >= 210) becomes PURE BLACK (0)
    double blackThresh = 210.0; // default = 210
    
    // 3. Transition curve between the edge and the core
    double gamma = 1.10; // 1.25 = default, 1.10 = sharper
    for (int i = 0; i < 256; i++)
    {
        double ink = 255.0 - i;
        if (ink <= whiteThresh)
        {
            lut[i] = 255; // pure white
        }
        else if (ink >= blackThresh)
        {
            lut[i] = 0;   // pure black
        }
        else
        {
            double adj = (ink - whiteThresh) / (blackThresh - whiteThresh);
            lut[i] = (byte)Math.Clamp(Math.Round(255.0 - Math.Pow(adj, gamma) * 255.0), 0, 255);
        }
    }
    return lut;
}


    private static void ApplyContrastLut(byte* scan0, int height, int stride, int width)
    {
        fixed (byte* lut = s_contrastLut)
        {
            for (int y = 0; y < height; y++)
            {
                byte* row = scan0 + (y * stride);
                for (int x = 0; x < width; x++)
                {
                    int offset = x * 4;
                    row[offset] = lut[row[offset]];         // B
                    row[offset + 1] = lut[row[offset + 1]]; // G
                    row[offset + 2] = lut[row[offset + 2]]; // R
                    // row[offset + 3] is Alpha (255), preserved
                }
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Annotations & Digital Signatures
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads and cryptographically inspects all digital signatures in the document, matching signature field
    /// widget bounds across pages when present.
    /// </summary>
    internal IReadOnlyList<PdfSignatureInfo> ReadDocumentSignatures(
        long documentId,
        string filePath,
        CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        int sigCount = PdfiumNative.FPDF_GetSignatureCount(document);
        if (sigCount <= 0)
        {
            return Array.Empty<PdfSignatureInfo>();
        }

        long fileLength = 0;
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            try
            {
                fileLength = new FileInfo(filePath).Length;
            }
            catch
            {
            }
        }

        if (fileLength <= 0)
        {
            fileLength = document.StreamAccess?.FileLength ?? 0;
        }

        List<PdfSignatureInfo> signatures = new(sigCount);
        for (int i = 0; i < sigCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nint sigHandle = PdfiumNative.FPDF_GetSignatureObject(document, i);
            if (sigHandle == nint.Zero)
            {
                continue;
            }

            byte[] contents = ReadSignatureContents(sigHandle);
            int[] byteRange = ReadSignatureByteRange(sigHandle);
            string subFilter = ReadSignatureAscii(sigHandle, static (h, b, l) => PdfiumNative.FPDFSignatureObj_GetSubFilter(h, b, l));
            string reason = ReadWideString(sigHandle, static (h, b, l) => PdfiumNative.FPDFSignatureObj_GetReason(h, (char*)b, l));
            string rawTime = ReadSignatureAscii(sigHandle, static (h, b, l) => PdfiumNative.FPDFSignatureObj_GetTime(h, b, l));
            uint docMdp = PdfiumNative.FPDFSignatureObj_GetDocMDPPermission(sigHandle);

            signatures.Add(PdfSignatureInspector.InspectSignature(
                i,
                filePath,
                fileLength,
                contents,
                byteRange,
                subFilter,
                reason,
                rawTime,
                docMdp));
        }

        // Locate visual signature widget locations across pages without flushing the visible page cache.
        int pageCount = Math.Max(0, PdfiumNative.FPDF_GetPageCount(document));
        int nextMatchIdx = 0;
        for (int p = 0; p < pageCount && nextMatchIdx < signatures.Count; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_pageCache.TryGetCached(documentId, p, out PdfiumPageHandle? cachedPage, out _) && cachedPage is { IsInvalid: false })
            {
                MatchSignatureWidgetsOnPage(cachedPage, p, signatures, ref nextMatchIdx);
            }
            else
            {
                using PdfiumPageHandle transientPage = PdfiumNative.FPDF_LoadPage(document, p);
                if (!transientPage.IsInvalid)
                {
                    MatchSignatureWidgetsOnPage(transientPage, p, signatures, ref nextMatchIdx);
                }
            }
        }

        return signatures;
    }

    private static void MatchSignatureWidgetsOnPage(
        PdfiumPageHandle page,
        int pageIndex,
        List<PdfSignatureInfo> signatures,
        ref int nextMatchIdx)
    {
        int count = Math.Max(0, PdfiumNative.FPDFPage_GetAnnotCount(page));
        for (int i = 0; i < count && nextMatchIdx < signatures.Count; i++)
        {
            using PdfiumAnnotationHandle annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
            if (annot.IsInvalid || PdfiumNative.FPDFAnnot_GetSubtype(annot) != (int)PdfAnnotationSubtype.Widget)
            {
                continue;
            }

            if (!IsSignatureWidgetAnnotation(annot))
            {
                continue;
            }

            FsRectF rect = default;
            if (PdfiumNative.FPDFAnnot_GetRect(annot, &rect) != 0)
            {
                PdfRect bounds = PdfRect.FromCorners(rect.Left, rect.Bottom, rect.Right, rect.Top);
                signatures[nextMatchIdx] = signatures[nextMatchIdx] with
                {
                    PageIndex = pageIndex,
                    WidgetBounds = bounds,
                };
            }
            else
            {
                signatures[nextMatchIdx] = signatures[nextMatchIdx] with { PageIndex = pageIndex };
            }

            nextMatchIdx++;
        }
    }

    /// <summary>
    /// Scans all pages of <paramref name="documentId"/> for annotations without flushing the visible page LRU cache.
    /// </summary>
    internal IReadOnlyList<PdfAnnotationInfo> ReadDocumentAnnotations(long documentId, CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        int pageCount = Math.Max(0, PdfiumNative.FPDF_GetPageCount(document));
        if (pageCount == 0)
        {
            return Array.Empty<PdfAnnotationInfo>();
        }

        int totalSigCount = Math.Max(0, PdfiumNative.FPDF_GetSignatureCount(document));
        int nextSigWidgetIdx = 0;
        List<PdfAnnotationInfo> results = new();

        for (int p = 0; p < pageCount; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_pageCache.TryGetCached(documentId, p, out PdfiumPageHandle? cachedPage, out PdfiumTextPageHandle? cachedText) &&
                cachedPage is { IsInvalid: false })
            {
                ReadPageAnnotationsCore(
                    document,
                    documentId,
                    cachedPage,
                    cachedText,
                    p,
                    allowTextQuote: true,
                    ref nextSigWidgetIdx,
                    totalSigCount,
                    results,
                    useCachedTextPage: true);
            }
            else
            {
                using PdfiumPageHandle transientPage = PdfiumNative.FPDF_LoadPage(document, p);
                if (transientPage.IsInvalid)
                {
                    continue;
                }

                ReadPageAnnotationsCore(
                    document,
                    documentId,
                    transientPage,
                    cachedTextPage: null,
                    p,
                    allowTextQuote: true,
                    ref nextSigWidgetIdx,
                    totalSigCount,
                    results,
                    useCachedTextPage: false);
            }
        }

        return results;
    }

    /// <summary>
    /// Reads all annotations present on <paramref name="pageIndex"/>.
    /// </summary>
    internal IReadOnlyList<PdfAnnotationInfo> ReadPageAnnotations(long documentId, int pageIndex, CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumPageHandle? page = _pageCache.GetOrLoad(document, documentId, pageIndex);
        if (page is null || page.IsInvalid)
        {
            return Array.Empty<PdfAnnotationInfo>();
        }

        int totalSigCount = Math.Max(0, PdfiumNative.FPDF_GetSignatureCount(document));
        int nextSigWidgetIdx = totalSigCount > 0 && pageIndex > 0
            ? CountSignatureWidgetsBeforePage(document, documentId, pageIndex)
            : 0;

        List<PdfAnnotationInfo> results = new();
        _ = _pageCache.TryGetCached(documentId, pageIndex, out _, out PdfiumTextPageHandle? cachedText);
        ReadPageAnnotationsCore(
            document,
            documentId,
            page,
            cachedText,
            pageIndex,
            allowTextQuote: true,
            ref nextSigWidgetIdx,
            totalSigCount,
            results,
            useCachedTextPage: true);

        return results;
    }

    /// <summary>
    /// Creates a text markup annotation (Highlight, Underline, StrikeOut, Squiggly) on the target page.
    /// </summary>
    internal PdfPageAnnotationMutationResult AddMarkupAnnotation(
        long documentId,
        PdfCreateMarkupRequest request,
        CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumPageHandle page = _pageCache.GetOrLoad(document, documentId, request.PageIndex)
            ?? throw new PdfiumException(PdfiumErrorCode.Page, $"Page {request.PageIndex} could not be loaded.");

        if (request.Subtype is not (PdfAnnotationSubtype.Highlight or PdfAnnotationSubtype.Underline or PdfAnnotationSubtype.StrikeOut or PdfAnnotationSubtype.Squiggly))
        {
            throw new ArgumentException($"Unsupported markup subtype: {request.Subtype}", nameof(request));
        }

        if (request.Rects.Count == 0)
        {
            throw new ArgumentException("At least one bounding rectangle is required.", nameof(request));
        }

        int createdIndex;
        using (PdfiumAnnotationHandle annot = PdfiumNative.FPDFPage_CreateAnnot(page, (int)request.Subtype))
        {
            if (annot.IsInvalid)
            {
                throw new PdfiumException(PdfiumErrorCode.Page, "Failed to create text markup annotation.");
            }

            byte alpha = request.Subtype == PdfAnnotationSubtype.Highlight ? (byte)195 : (byte)255;
            _ = PdfiumNative.FPDFAnnot_SetColor(
                annot,
                PdfiumConstants.AnnotColorTypeColor,
                request.Color.R,
                request.Color.G,
                request.Color.B,
                alpha);

            Box pageBox = ReadVisiblePageBox(document, page, request.PageIndex);
            float minL = float.MaxValue;
            float minB = float.MaxValue;
            float maxR = float.MinValue;
            float maxT = float.MinValue;
            List<PdfRect> finalRects = new(request.Rects.Count);

            for (int i = 0; i < request.Rects.Count; i++)
            {
                PdfRect rawRect = request.Rects[i];
                if (rawRect.IsEmpty)
                {
                    continue;
                }

                PdfRect r = rawRect;
                if (!request.SkipPadding)
                {
                    if (request.Subtype == PdfAnnotationSubtype.Highlight)
                    {
                        float h = Math.Max(4f, rawRect.Height);
                        float padTop = Math.Clamp(h * 0.16f, 1.4f, 3.0f);
                        float padBottom = Math.Clamp(h * 0.14f, 1.2f, 2.8f);
                        const float padX = 1.2f;
                        r = new PdfRect(
                            Math.Clamp(rawRect.Left - padX, pageBox.Left, pageBox.Right),
                            Math.Clamp(rawRect.Bottom - padBottom, pageBox.Bottom, pageBox.Top),
                            Math.Clamp(rawRect.Right + padX, pageBox.Left, pageBox.Right),
                            Math.Clamp(rawRect.Top + padTop, pageBox.Bottom, pageBox.Top));
                    }
                    else if (request.Subtype is PdfAnnotationSubtype.Underline or PdfAnnotationSubtype.Squiggly)
                    {
                        float h = Math.Max(4f, rawRect.Height);
                        float dropBottom = request.Subtype == PdfAnnotationSubtype.Squiggly
                            ? Math.Clamp(h * 0.16f, 1.3f, 2.2f)
                            : Math.Clamp(h * 0.12f, 1.0f, 1.8f);
                        r = new PdfRect(
                            Math.Clamp(rawRect.Left, pageBox.Left, pageBox.Right),
                            Math.Clamp(rawRect.Bottom - dropBottom, pageBox.Bottom, pageBox.Top),
                            Math.Clamp(rawRect.Right, pageBox.Left, pageBox.Right),
                            Math.Clamp(rawRect.Top, pageBox.Bottom, pageBox.Top));
                    }
                }

                finalRects.Add(r);
                FsQuadPointsF quad = new()
                {
                    X1 = r.Left,
                    Y1 = r.Top,
                    X2 = r.Right,
                    Y2 = r.Top,
                    X3 = r.Left,
                    Y3 = r.Bottom,
                    X4 = r.Right,
                    Y4 = r.Bottom,
                };
                _ = PdfiumNative.FPDFAnnot_AppendAttachmentPoints(annot, &quad);

                minL = Math.Min(minL, r.Left);
                minB = Math.Min(minB, r.Bottom);
                maxR = Math.Max(maxR, r.Right);
                maxT = Math.Max(maxT, r.Top);
            }

            if (minL <= maxR && minB <= maxT)
            {
                FsRectF unionRect = new() { Left = minL, Bottom = minB, Right = maxR, Top = maxT };
                _ = PdfiumNative.FPDFAnnot_SetRect(annot, &unionRect);
            }

            _ = PdfiumNative.FPDFAnnot_SetFlags(annot, PdfiumConstants.AnnotFlagPrint);
            SetAnnotString(annot, "T", string.IsNullOrWhiteSpace(request.Author) ? Environment.UserName : request.Author);
            SetAnnotString(annot, "M", PdfSignatureInspector.FormatPdfDate(DateTimeOffset.Now));

            if (!string.IsNullOrWhiteSpace(request.Contents))
            {
                SetAnnotString(annot, "Contents", request.Contents);
            }

            if (!string.IsNullOrWhiteSpace(request.Subject))
            {
                SetAnnotString(annot, "Subj", request.Subject);
            }

            if (request.Subtype is PdfAnnotationSubtype.Underline or PdfAnnotationSubtype.Squiggly && finalRects.Count > 0)
            {
                string apStream = BuildMarkupLineAppearanceStream(request.Subtype, finalRects, request.Color);
                SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
            }

            createdIndex = PdfiumNative.FPDFPage_GetAnnotIndex(page, annot);
        }

        IReadOnlyList<PdfAnnotationInfo> refreshed = ReadPageAnnotations(documentId, request.PageIndex, cancellationToken);
        return new PdfPageAnnotationMutationResult(request.PageIndex, createdIndex, refreshed);
    }

    /// <summary>
    /// Creates a sticky-note comment annotation (<c>/Text</c>) on the target page.
    /// </summary>
    internal PdfPageAnnotationMutationResult AddTextNoteAnnotation(
        long documentId,
        PdfCreateNoteRequest request,
        CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumPageHandle page = _pageCache.GetOrLoad(document, documentId, request.PageIndex)
            ?? throw new PdfiumException(PdfiumErrorCode.Page, $"Page {request.PageIndex} could not be loaded.");

        Box pageBox = ReadVisiblePageBox(document, page, request.PageIndex);
        const float IconSize = 22f;
        float left = (float)Math.Clamp(request.PdfX - (IconSize * 0.5), pageBox.Left + 2f, Math.Max(pageBox.Left + 2f, pageBox.Right - IconSize - 2f));
        float top = (float)Math.Clamp(request.PdfY + (IconSize * 0.5), pageBox.Bottom + IconSize + 2f, Math.Max(pageBox.Bottom + IconSize + 2f, pageBox.Top - 2f));
        FsRectF noteRect = new()
        {
            Left = left,
            Bottom = top - IconSize,
            Right = left + IconSize,
            Top = top,
        };

        int createdIndex;
        using (PdfiumAnnotationHandle annot = PdfiumNative.FPDFPage_CreateAnnot(page, (int)PdfAnnotationSubtype.Text))
        {
            if (annot.IsInvalid)
            {
                throw new PdfiumException(PdfiumErrorCode.Page, "Failed to create text note annotation.");
            }

            _ = PdfiumNative.FPDFAnnot_SetRect(annot, &noteRect);
            _ = PdfiumNative.FPDFAnnot_SetColor(annot, PdfiumConstants.AnnotColorTypeColor, request.Color.R, request.Color.G, request.Color.B, 255);
            _ = PdfiumNative.FPDFAnnot_SetFlags(annot, PdfiumConstants.AnnotFlagPrint);
            SetAnnotString(annot, "Name", "Comment");
            SetAnnotString(annot, "T", string.IsNullOrWhiteSpace(request.Author) ? Environment.UserName : request.Author);
            SetAnnotString(annot, "M", PdfSignatureInspector.FormatPdfDate(DateTimeOffset.Now));
            SetAnnotString(annot, "Contents", request.Contents ?? string.Empty);

            string apStream = BuildCommentIconAppearanceStream(noteRect, request.Color);
            SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
            createdIndex = PdfiumNative.FPDFPage_GetAnnotIndex(page, annot);
        }

        IReadOnlyList<PdfAnnotationInfo> refreshed = ReadPageAnnotations(documentId, request.PageIndex, cancellationToken);
        return new PdfPageAnnotationMutationResult(request.PageIndex, createdIndex, refreshed);
    }

    /// <summary>
    /// Creates a visible vector text box annotation (<c>/Stamp</c> with <c>/Subj (Text)</c> and <c>/WinPDFStyle</c>)
    /// on the target page using PDF Standard-14 <c>Helvetica</c> vector text objects.
    /// </summary>
    internal PdfPageAnnotationMutationResult AddFreeTextAnnotation(
        long documentId,
        PdfCreateFreeTextRequest request,
        CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumPageHandle page = _pageCache.GetOrLoad(document, documentId, request.PageIndex)
            ?? throw new PdfiumException(PdfiumErrorCode.Page, $"Page {request.PageIndex} could not be loaded.");

        Box pageBox = ReadVisiblePageBox(document, page, request.PageIndex);
        string contents = request.Contents ?? string.Empty;
        double maxWidthPt = request.MaxWidthPt > 0 ? request.MaxWidthPt : 280.0;

        PdfFreeTextStyle style = request.Style ?? new PdfFreeTextStyle(
            "Arial",
            12.0f,
            IsBold: false,
            IsItalic: false,
            IsUnderline: false,
            Color: request.Color,
            MaxWidthPt: maxWidthPt);
        style = style with
        {
            FontFamily = "Arial",
            Color = request.Color,
            MaxWidthPt = maxWidthPt
        };

        nint font = GetOrLoadTextFont(document, documentId, style.IsBold, style.IsItalic);

        int createdIndex;
        using (PdfiumAnnotationHandle annot = PdfiumNative.FPDFPage_CreateAnnot(page, (int)PdfAnnotationSubtype.Stamp))
        {
            if (annot.IsInvalid)
            {
                throw new PdfiumException(PdfiumErrorCode.Page, "Failed to create text stamp annotation.");
            }

            _ = PdfiumNative.FPDFAnnot_SetFlags(annot, PdfiumConstants.AnnotFlagPrint);
            SetAnnotString(annot, "T", string.IsNullOrWhiteSpace(request.Author) ? Environment.UserName : request.Author);
            SetAnnotString(annot, "M", PdfSignatureInspector.FormatPdfDate(DateTimeOffset.Now));
            SetAnnotString(annot, "Contents", contents);
            SetAnnotString(annot, "Subj", "Text");
            SetAnnotString(annot, "WinPDFStyle", style.Serialize());

            BuildAndAttachTextStampObjects(
                document,
                annot,
                font,
                pageBox,
                (float)request.PdfX,
                (float)request.PdfY,
                style,
                contents);
            createdIndex = PdfiumNative.FPDFPage_GetAnnotIndex(page, annot);
        }

        IReadOnlyList<PdfAnnotationInfo> refreshed = ReadPageAnnotations(documentId, request.PageIndex, cancellationToken);
        return new PdfPageAnnotationMutationResult(request.PageIndex, createdIndex, refreshed);
    }

    /// <summary>
    /// Updates an existing annotation's contents and/or color on the dedicated PDFium thread.
    /// </summary>
    internal PdfPageAnnotationMutationResult UpdateAnnotation(
        long documentId,
        PdfUpdateAnnotationRequest request,
        CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumPageHandle page = _pageCache.GetOrLoad(document, documentId, request.PageIndex)
            ?? throw new PdfiumException(PdfiumErrorCode.Page, $"Page {request.PageIndex} could not be loaded.");

        using (PdfiumAnnotationHandle annot = PdfiumNative.FPDFPage_GetAnnot(page, request.AnnotIndex))
        {
            if (annot.IsInvalid)
            {
                throw new PdfiumException(PdfiumErrorCode.Page, $"Annotation {request.AnnotIndex} not found on page {request.PageIndex}.");
            }

            PdfAnnotationSubtype subtype = (PdfAnnotationSubtype)PdfiumNative.FPDFAnnot_GetSubtype(annot);
            string existingStyleRaw = ReadAnnotString(annot, "WinPDFStyle");
            PdfFreeTextStyle? existingStyle = null;
            bool isWinPdfTextStamp = subtype == PdfAnnotationSubtype.Stamp &&
                (PdfFreeTextStyle.TryParse(existingStyleRaw, out existingStyle) || request.NewTextStyle is not null);

            SetAnnotString(annot, "Contents", request.Contents ?? string.Empty);
            SetAnnotString(annot, "M", PdfSignatureInspector.FormatPdfDate(DateTimeOffset.Now));

            if (isWinPdfTextStamp)
            {
                PdfFreeTextStyle effectiveStyle = request.NewTextStyle ?? existingStyle ?? PdfFreeTextStyle.Default;
                effectiveStyle = effectiveStyle with { FontFamily = "Arial" };
                if (request.NewColor is { } newStampColor)
                {
                    effectiveStyle = effectiveStyle with { Color = newStampColor };
                }

                FsRectF rect = default;
                _ = PdfiumNative.FPDFAnnot_GetRect(annot, &rect);
                float oldMargin = Math.Max(3.0f, (existingStyle?.FontSizePt ?? effectiveStyle.FontSizePt) * 0.12f);
                float origLeft = Math.Min(rect.Left, rect.Right) + oldMargin;
                float origTop = Math.Max(rect.Top, rect.Bottom) - oldMargin;

                for (int idx = PdfiumNative.FPDFAnnot_GetObjectCount(annot) - 1; idx >= 0; idx--)
                {
                    _ = PdfiumNative.FPDFAnnot_RemoveObject(annot, idx);
                }

                SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, null);

                Box pageBox = ReadVisiblePageBox(document, page, request.PageIndex);
                string contents = request.Contents ?? string.Empty;
                nint font = GetOrLoadTextFont(document, documentId, effectiveStyle.IsBold, effectiveStyle.IsItalic);

                SetAnnotString(annot, "Subj", "Text");
                SetAnnotString(annot, "WinPDFStyle", effectiveStyle.Serialize());
                BuildAndAttachTextStampObjects(document, annot, font, pageBox, origLeft, origTop, effectiveStyle, contents);
            }
            else
            {
                if (request.NewColor is { } newColor)
                {
                    // PDFium refuses FPDFAnnot_SetColor if an appearance stream already exists; clear AP first.
                    SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, null);
                    byte alpha = subtype == PdfAnnotationSubtype.Highlight ? (byte)195 : (byte)255;
                    _ = PdfiumNative.FPDFAnnot_SetColor(
                        annot,
                        PdfiumConstants.AnnotColorTypeColor,
                        newColor.R,
                        newColor.G,
                        newColor.B,
                        alpha);
                }

                if (subtype == PdfAnnotationSubtype.FreeText)
                {
                    FsRectF rect = default;
                    _ = PdfiumNative.FPDFAnnot_GetRect(annot, &rect);
                    Box pageBox = ReadVisiblePageBox(document, page, request.PageIndex);
                    double maxW = Math.Clamp(Math.Max(160.0, Math.Abs(rect.Right - rect.Left)), 120.0, 320.0);
                    List<string> lines = WrapFreeTextLines(request.Contents ?? string.Empty, maxW, out float w, out float h);
                    float left = Math.Clamp(Math.Min(rect.Left, rect.Right), pageBox.Left + 2f, Math.Max(pageBox.Left + 2f, pageBox.Right - w - 2f));
                    float top = Math.Clamp(Math.Max(rect.Top, rect.Bottom), pageBox.Bottom + h + 2f, Math.Max(pageBox.Bottom + h + 2f, pageBox.Top - 2f));
                    FsRectF updatedRect = new() { Left = left, Bottom = top - h, Right = left + w, Top = top };
                    _ = PdfiumNative.FPDFAnnot_SetRect(annot, &updatedRect);

                    PdfAnnotationColor color = request.NewColor ?? ReadAnnotColor(annot, subtype);
                    string apStream = BuildFreeTextAppearanceStream(updatedRect, color, lines);
                    SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
                }
                else if (subtype == PdfAnnotationSubtype.Text && request.NewColor is { } noteColor)
                {
                    FsRectF rect = default;
                    if (PdfiumNative.FPDFAnnot_GetRect(annot, &rect) != 0)
                    {
                        string apStream = BuildCommentIconAppearanceStream(rect, noteColor);
                        SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
                    }
                }
                else if (subtype is PdfAnnotationSubtype.Underline or PdfAnnotationSubtype.Squiggly && request.NewColor is { } markupColor)
                {
                    IReadOnlyList<PdfRect> quads = ReadAnnotQuadRects(annot);
                    if (quads.Count > 0)
                    {
                        string apStream = BuildMarkupLineAppearanceStream(subtype, quads, markupColor);
                        SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
                    }
                }
            }
        }

        IReadOnlyList<PdfAnnotationInfo> refreshed = ReadPageAnnotations(documentId, request.PageIndex, cancellationToken);
        return new PdfPageAnnotationMutationResult(request.PageIndex, request.AnnotIndex, refreshed);
    }

    /// <summary>
    /// Translates an existing movable annotation by a PDF user-space delta.
    /// </summary>
    internal PdfPageAnnotationMutationResult MoveAnnotation(
        long documentId,
        PdfMoveAnnotationRequest request,
        CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumPageHandle page = _pageCache.GetOrLoad(document, documentId, request.PageIndex)
            ?? throw new PdfiumException(PdfiumErrorCode.Page, $"Page {request.PageIndex} could not be loaded.");

        using (PdfiumAnnotationHandle annot = PdfiumNative.FPDFPage_GetAnnot(page, request.AnnotIndex))
        {
            if (annot.IsInvalid)
            {
                throw new PdfiumException(PdfiumErrorCode.Page, $"Annotation {request.AnnotIndex} not found on page {request.PageIndex}.");
            }

            PdfAnnotationSubtype subtype = (PdfAnnotationSubtype)PdfiumNative.FPDFAnnot_GetSubtype(annot);
            PdfAnnotationColor existingColor = ReadAnnotColor(annot, subtype);
            FsRectF rawRect = default;
            if (PdfiumNative.FPDFAnnot_GetRect(annot, &rawRect) == 0)
            {
                throw new PdfiumException(PdfiumErrorCode.Page, "Failed to read annotation bounds.");
            }

            float rLeft = Math.Min(rawRect.Left, rawRect.Right);
            float rRight = Math.Max(rawRect.Left, rawRect.Right);
            float rBottom = Math.Min(rawRect.Bottom, rawRect.Top);
            float rTop = Math.Max(rawRect.Bottom, rawRect.Top);

            Box pageBox = ReadVisiblePageBox(document, page, request.PageIndex);
            float dx = Math.Clamp((float)request.DeltaPdfX, pageBox.Left - rLeft, Math.Max(pageBox.Left - rLeft, pageBox.Right - rRight));
            float dy = Math.Clamp((float)request.DeltaPdfY, pageBox.Bottom - rBottom, Math.Max(pageBox.Bottom - rBottom, pageBox.Top - rTop));

            FsRectF newRect = new()
            {
                Left = rLeft + dx,
                Bottom = rBottom + dy,
                Right = rRight + dx,
                Top = rTop + dy,
            };
            _ = PdfiumNative.FPDFAnnot_SetRect(annot, &newRect);
            SetAnnotString(annot, "M", PdfSignatureInspector.FormatPdfDate(DateTimeOffset.Now));

            if (subtype == PdfAnnotationSubtype.FreeText)
            {
                string contents = ReadAnnotString(annot, "Contents");
                double boxW = Math.Clamp(newRect.Right - newRect.Left, 110.0, 320.0);
                List<string> lines = WrapFreeTextLines(contents, boxW, out float w, out float h);
                FsRectF apRect = new()
                {
                    Left = newRect.Left,
                    Bottom = newRect.Top - h,
                    Right = newRect.Left + w,
                    Top = newRect.Top,
                };
                _ = PdfiumNative.FPDFAnnot_SetRect(annot, &apRect);
                string apStream = BuildFreeTextAppearanceStream(apRect, existingColor, lines);
                SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
            }
            else if (subtype == PdfAnnotationSubtype.Ink)
            {
                uint strokeCount = PdfiumNative.FPDFAnnot_GetInkListCount(annot);
                if (strokeCount > 0)
                {
                    List<FsPointF[]> translatedStrokes = new((int)Math.Min(strokeCount, 1024u));
                    for (uint s = 0; s < strokeCount; s++)
                    {
                        uint ptCount = PdfiumNative.FPDFAnnot_GetInkListPath(annot, s, null, 0);
                        if (ptCount == 0 || ptCount > 65536)
                        {
                            continue;
                        }

                        FsPointF[] pts = new FsPointF[ptCount];
                        fixed (FsPointF* ptr = pts)
                        {
                            uint copied = PdfiumNative.FPDFAnnot_GetInkListPath(annot, s, ptr, ptCount);
                            for (uint p = 0; p < copied; p++)
                            {
                                pts[p].X += dx;
                                pts[p].Y += dy;
                            }
                        }

                        translatedStrokes.Add(pts);
                    }

                    if (translatedStrokes.Count > 0 && PdfiumNative.FPDFAnnot_RemoveInkList(annot) != 0)
                    {
                        foreach (FsPointF[] pts in translatedStrokes)
                        {
                            fixed (FsPointF* ptr = pts)
                            {
                                _ = PdfiumNative.FPDFAnnot_AddInkStroke(annot, ptr, (nuint)pts.Length);
                            }
                        }
                    }
                }
            }
            else if (subtype == PdfAnnotationSubtype.Text)
            {
                string apStream = BuildCommentIconAppearanceStream(newRect, existingColor);
                SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
            }
        }

        IReadOnlyList<PdfAnnotationInfo> refreshed = ReadPageAnnotations(documentId, request.PageIndex, cancellationToken);
        return new PdfPageAnnotationMutationResult(request.PageIndex, request.AnnotIndex, refreshed);
    }

    /// <summary>
    /// Resizes an existing resizable annotation (<c>/Stamp</c>) to new PDF user-space bounds.
    /// </summary>
    internal PdfPageAnnotationMutationResult ResizeAnnotation(
        long documentId,
        PdfResizeAnnotationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumPageHandle page = _pageCache.GetOrLoad(document, documentId, request.PageIndex)
            ?? throw new PdfiumException(PdfiumErrorCode.Page, $"Page {request.PageIndex} could not be loaded.");

        using (PdfiumAnnotationHandle annot = PdfiumNative.FPDFPage_GetAnnot(page, request.AnnotIndex))
        {
            if (annot.IsInvalid)
            {
                throw new PdfiumException(PdfiumErrorCode.Page, $"Annotation {request.AnnotIndex} not found on page {request.PageIndex}.");
            }

            Box pageBox = ReadVisiblePageBox(document, page, request.PageIndex);
            float left = Math.Clamp(request.NewBounds.Left, pageBox.Left, Math.Max(pageBox.Left, pageBox.Right - 16f));
            float right = Math.Clamp(request.NewBounds.Right, Math.Min(pageBox.Right, left + 16f), pageBox.Right);
            float bottom = Math.Clamp(request.NewBounds.Bottom, pageBox.Bottom, Math.Max(pageBox.Bottom, pageBox.Top - 16f));
            float top = Math.Clamp(request.NewBounds.Top, Math.Min(pageBox.Top, bottom + 16f), pageBox.Top);

            FsRectF newRect = new()
            {
                Left = left,
                Bottom = bottom,
                Right = right,
                Top = top,
            };
            _ = PdfiumNative.FPDFAnnot_SetRect(annot, &newRect);
            SetAnnotString(annot, "M", PdfSignatureInspector.FormatPdfDate(DateTimeOffset.Now));
        }

        IReadOnlyList<PdfAnnotationInfo> refreshed = ReadPageAnnotations(documentId, request.PageIndex, cancellationToken);
        return new PdfPageAnnotationMutationResult(request.PageIndex, request.AnnotIndex, refreshed);
    }

    /// <summary>
    /// Creates a raster image stamp annotation (<c>/Stamp</c>) anchored at <c>(request.PdfX, request.PdfY)</c>.
    /// </summary>
    internal PdfPageAnnotationMutationResult AddImageAnnotation(
        long documentId,
        PdfCreateImageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PixelWidth <= 0 || request.PixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Image dimensions must be positive.");
        }

        long expectedBytes = (long)request.PixelWidth * request.PixelHeight * 4L;
        if (request.BgraPixels is null || request.BgraPixels.Length < expectedBytes)
        {
            throw new ArgumentException("Image pixel buffer is smaller than expected BGRA dimensions.", nameof(request));
        }

        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumPageHandle page = _pageCache.GetOrLoad(document, documentId, request.PageIndex)
            ?? throw new PdfiumException(PdfiumErrorCode.Page, $"Page {request.PageIndex} could not be loaded.");

        Box pageBox = ReadVisiblePageBox(document, page, request.PageIndex);
        double pageW = Math.Max(48.0, pageBox.Right - pageBox.Left);
        double pageH = Math.Max(48.0, pageBox.Top - pageBox.Bottom);

        double widthPt;
        double heightPt;
        if (request.ExplicitWidthPt is > 4.0 and double ew &&
            request.ExplicitHeightPt is > 4.0 and double eh)
        {
            widthPt = Math.Clamp(ew, 12.0, pageW);
            heightPt = Math.Clamp(eh, 12.0, pageH);
        }
        else
        {
            double naturalW = request.PixelWidth * 0.75;
            double naturalH = request.PixelHeight * 0.75;
            double maxW = Math.Clamp(pageW * 0.45, 72.0, 240.0);
            double maxH = Math.Clamp(pageH * 0.45, 72.0, 240.0);
            double scale = Math.Min(1.0, Math.Min(maxW / Math.Max(1.0, naturalW), maxH / Math.Max(1.0, naturalH)));
            widthPt = naturalW * scale;
            heightPt = naturalH * scale;
            double minSide = Math.Min(widthPt, heightPt);
            if (minSide < 24.0 && minSide > 0.01)
            {
                double up = Math.Min(24.0 / minSide, Math.Min(maxW / Math.Max(1.0, widthPt), maxH / Math.Max(1.0, heightPt)));
                widthPt *= up;
                heightPt *= up;
            }
        }

        float wPt = (float)widthPt;
        float hPt = (float)heightPt;
        float left = Math.Clamp((float)request.PdfX, pageBox.Left, Math.Max(pageBox.Left, pageBox.Right - wPt));
        float top = Math.Clamp((float)request.PdfY, Math.Min(pageBox.Top, pageBox.Bottom + hPt), pageBox.Top);
        FsRectF rect = new()
        {
            Left = left,
            Bottom = top - hPt,
            Right = left + wPt,
            Top = top,
        };

        int createdIndex;
        using (PdfiumAnnotationHandle annot = PdfiumNative.FPDFPage_CreateAnnot(page, (int)PdfAnnotationSubtype.Stamp))
        {
            if (annot.IsInvalid)
            {
                throw new PdfiumException(PdfiumErrorCode.Page, "Failed to create Stamp annotation.");
            }

            _ = PdfiumNative.FPDFAnnot_SetRect(annot, &rect);
            _ = PdfiumNative.FPDFAnnot_SetFlags(annot, PdfiumConstants.AnnotFlagPrint);
            SetAnnotString(annot, "T", string.IsNullOrWhiteSpace(request.Author) ? Environment.UserName : request.Author);
            SetAnnotString(annot, "M", PdfSignatureInspector.FormatPdfDate(DateTimeOffset.Now));
            SetAnnotString(annot, "Subj", string.IsNullOrWhiteSpace(request.Subject) ? "Image" : request.Subject);

            using PdfiumBitmapHandle bmp = PdfiumNative.FPDFBitmap_Create(request.PixelWidth, request.PixelHeight, 1);
            if (bmp.IsInvalid)
            {
                throw new PdfiumException(PdfiumErrorCode.Unknown, "Failed to create PDFium bitmap for image stamp.");
            }

            byte* dstPtr = (byte*)PdfiumNative.FPDFBitmap_GetBuffer(bmp);
            int dstStride = PdfiumNative.FPDFBitmap_GetStride(bmp);
            int srcStride = request.PixelWidth * 4;
            if (dstPtr == null || dstStride < srcStride)
            {
                throw new PdfiumException(PdfiumErrorCode.Unknown, "Invalid PDFium bitmap buffer.");
            }

            fixed (byte* srcBase = request.BgraPixels)
            {
                for (int y = 0; y < request.PixelHeight; y++)
                {
                    Buffer.MemoryCopy(
                        srcBase + ((long)y * srcStride),
                        dstPtr + ((long)y * dstStride),
                        dstStride,
                        srcStride);
                }
            }

            nint imgObj = PdfiumNative.FPDFPageObj_NewImageObj(document);
            if (imgObj == nint.Zero)
            {
                throw new PdfiumException(PdfiumErrorCode.Unknown, "Failed to create PDFium image object.");
            }

            if (PdfiumNative.FPDFImageObj_SetBitmap(null, 0, imgObj, bmp) == 0 ||
                PdfiumNative.FPDFImageObj_SetMatrix(imgObj, wPt, 0.0, 0.0, hPt, rect.Left, rect.Bottom) == 0 ||
                PdfiumNative.FPDFAnnot_AppendObject(annot, imgObj) == 0)
            {
                PdfiumNative.FPDFPageObj_Destroy(imgObj);
                throw new PdfiumException(PdfiumErrorCode.Unknown, "Failed to attach image object to Stamp annotation.");
            }

            createdIndex = PdfiumNative.FPDFPage_GetAnnotIndex(page, annot);
        }

        IReadOnlyList<PdfAnnotationInfo> refreshed = ReadPageAnnotations(documentId, request.PageIndex, cancellationToken);
        return new PdfPageAnnotationMutationResult(request.PageIndex, createdIndex, refreshed);
    }

    /// <summary>
    /// Extracts the embedded raster image payload from a <c>/Stamp</c> annotation, if present.
    /// </summary>
    internal PdfCapturedImage? CaptureStampImage(
        long documentId,
        int pageIndex,
        int annotIndex,
        CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumPageHandle page = _pageCache.GetOrLoad(document, documentId, pageIndex)
            ?? throw new PdfiumException(PdfiumErrorCode.Page, $"Page {pageIndex} could not be loaded.");

        using PdfiumAnnotationHandle annot = PdfiumNative.FPDFPage_GetAnnot(page, annotIndex);
        if (annot.IsInvalid)
        {
            return null;
        }

        nint imgObj = FindFirstImageObjectInAnnot(annot);
        if (imgObj == nint.Zero)
        {
            return null;
        }

        using PdfiumBitmapHandle bmp = PdfiumNative.FPDFImageObj_GetBitmap(imgObj);
        if (bmp.IsInvalid)
        {
            return null;
        }

        int w = PdfiumNative.FPDFBitmap_GetWidth(bmp);
        int h = PdfiumNative.FPDFBitmap_GetHeight(bmp);
        int stride = PdfiumNative.FPDFBitmap_GetStride(bmp);
        int format = PdfiumNative.FPDFBitmap_GetFormat(bmp);
        byte* srcPtr = (byte*)PdfiumNative.FPDFBitmap_GetBuffer(bmp);
        if (w <= 0 || h <= 0 || stride <= 0 || srcPtr == null)
        {
            return null;
        }

        const int BitmapFormatGray = 1;
        const int BitmapFormatBgr = 2;
        const int BitmapFormatBgrx = 3;
        const int BitmapFormatBgra = 4;

        byte[] bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            byte* row = srcPtr + ((long)y * stride);
            int dstRowOffset = y * w * 4;
            switch (format)
            {
                case BitmapFormatBgra:
                    Marshal.Copy((nint)row, bgra, dstRowOffset, w * 4);
                    break;
                case BitmapFormatBgrx:
                    Marshal.Copy((nint)row, bgra, dstRowOffset, w * 4);
                    for (int x = 0; x < w; x++)
                    {
                        bgra[dstRowOffset + (x * 4) + 3] = 255;
                    }
                    break;
                case BitmapFormatBgr:
                    for (int x = 0; x < w; x++)
                    {
                        int d = dstRowOffset + (x * 4);
                        int s = x * 3;
                        bgra[d] = row[s];
                        bgra[d + 1] = row[s + 1];
                        bgra[d + 2] = row[s + 2];
                        bgra[d + 3] = 255;
                    }
                    break;
                case BitmapFormatGray:
                    for (int x = 0; x < w; x++)
                    {
                        int d = dstRowOffset + (x * 4);
                        byte g = row[x];
                        bgra[d] = g;
                        bgra[d + 1] = g;
                        bgra[d + 2] = g;
                        bgra[d + 3] = 255;
                    }
                    break;
                default:
                    return null;
            }
        }

        return new PdfCapturedImage(w, h, bgra, ReadAnnotString(annot, "Subj"));
    }

    private static nint FindFirstImageObjectInAnnot(PdfiumAnnotationHandle annot)
    {
        int count = Math.Min(PdfiumNative.FPDFAnnot_GetObjectCount(annot), 256);
        for (int i = 0; i < count; i++)
        {
            nint obj = PdfiumNative.FPDFAnnot_GetObject(annot, i);
            nint found = FindFirstImageObjectRecursive(obj, depth: 0);
            if (found != nint.Zero)
            {
                return found;
            }
        }

        return nint.Zero;
    }

    private static nint FindFirstImageObjectRecursive(nint obj, int depth)
    {
        const int PageObjImage = 3;
        const int PageObjForm = 5;
        if (obj == nint.Zero)
        {
            return nint.Zero;
        }

        int objType = PdfiumNative.FPDFPageObj_GetType(obj);
        if (objType == PageObjImage)
        {
            return obj;
        }

        if (objType == PageObjForm && depth < 6)
        {
            int childCount = Math.Min(PdfiumNative.FPDFFormObj_CountObjects(obj), 256);
            for (int i = 0; i < childCount; i++)
            {
                nint child = PdfiumNative.FPDFFormObj_GetObject(obj, (uint)i);
                nint found = FindFirstImageObjectRecursive(child, depth + 1);
                if (found != nint.Zero)
                {
                    return found;
                }
            }
        }

        return nint.Zero;
    }

    /// <summary>
    /// Pastes a non-markup annotation snapshot (comment note, FreeText box, ink drawing, or geometric shape) onto a page.
    /// </summary>
    internal PdfPageAnnotationMutationResult PasteAnnotation(
        long documentId,
        PdfPasteAnnotationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PdfAnnotationInfo source = request.Source;
        if (!source.CanCopyOrCut)
        {
            throw new ArgumentException("Signature widgets and text markup annotations cannot be pasted.", nameof(request));
        }

        if (source.Subtype == PdfAnnotationSubtype.Text)
        {
            return AddTextNoteAnnotation(
                documentId,
                new PdfCreateNoteRequest(
                    request.TargetPageIndex,
                    request.TargetPdfX,
                    request.TargetPdfY,
                    source.Color,
                    source.Contents,
                    string.IsNullOrWhiteSpace(source.Author) ? Environment.UserName : source.Author),
                cancellationToken);
        }

        if (source.IsTextBox)
        {
            double maxW = source.TextStyle?.MaxWidthPt is > 60.0 and double sw
                ? sw
                : (source.Bounds.Width >= 110f ? source.Bounds.Width : 280.0);
            PdfFreeTextStyle style = source.TextStyle ?? new PdfFreeTextStyle(
                "Arial",
                12.0f,
                IsBold: false,
                IsItalic: false,
                IsUnderline: false,
                Color: source.Color,
                MaxWidthPt: maxW);
            style = style with { FontFamily = "Arial" };
            return AddFreeTextAnnotation(
                documentId,
                new PdfCreateFreeTextRequest(
                    request.TargetPageIndex,
                    request.TargetPdfX,
                    request.TargetPdfY,
                    style.Color,
                    string.IsNullOrWhiteSpace(source.Contents) ? "Text" : source.Contents,
                    string.IsNullOrWhiteSpace(source.Author) ? Environment.UserName : source.Author,
                    MaxWidthPt: maxW,
                    Style: style),
                cancellationToken);
        }

        if (source.Subtype == PdfAnnotationSubtype.Stamp && source.CapturedImage is { } stampImg)
        {
            return AddImageAnnotation(
                documentId,
                new PdfCreateImageRequest(
                    request.TargetPageIndex,
                    request.TargetPdfX,
                    request.TargetPdfY,
                    stampImg.PixelWidth,
                    stampImg.PixelHeight,
                    stampImg.BgraPixels,
                    string.IsNullOrWhiteSpace(source.Author) ? Environment.UserName : source.Author,
                    string.IsNullOrWhiteSpace(stampImg.Subject) ? source.Subject : stampImg.Subject,
                    ExplicitWidthPt: source.Bounds.Width > 4f ? source.Bounds.Width : null,
                    ExplicitHeightPt: source.Bounds.Height > 4f ? source.Bounds.Height : null),
                cancellationToken);
        }

        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumPageHandle page = _pageCache.GetOrLoad(document, documentId, request.TargetPageIndex)
            ?? throw new PdfiumException(PdfiumErrorCode.Page, $"Page {request.TargetPageIndex} could not be loaded.");

        Box pageBox = ReadVisiblePageBox(document, page, request.TargetPageIndex);
        float w = Math.Max(14f, source.Bounds.Width);
        float h = Math.Max(14f, source.Bounds.Height);
        float left = Math.Clamp((float)request.TargetPdfX, pageBox.Left + 2f, Math.Max(pageBox.Left + 2f, pageBox.Right - w - 2f));
        float top = Math.Clamp((float)request.TargetPdfY, pageBox.Bottom + h + 2f, Math.Max(pageBox.Bottom + h + 2f, pageBox.Top - 2f));
        FsRectF newRect = new()
        {
            Left = left,
            Bottom = top - h,
            Right = left + w,
            Top = top,
        };

        float dx = newRect.Left - source.Bounds.Left;
        float dy = newRect.Bottom - source.Bounds.Bottom;
        float borderW = Math.Clamp(source.BorderWidthPt, 0.75f, 24f);
        string author = string.IsNullOrWhiteSpace(source.Author) ? Environment.UserName : source.Author;

        int createdIndex;
        PdfAnnotationSubtype targetSubtype = source.Subtype is PdfAnnotationSubtype.Square or PdfAnnotationSubtype.Circle
            ? source.Subtype
            : PdfAnnotationSubtype.Ink;

        using (PdfiumAnnotationHandle annot = PdfiumNative.FPDFPage_CreateAnnot(page, (int)targetSubtype))
        {
            if (annot.IsInvalid)
            {
                throw new PdfiumException(PdfiumErrorCode.Page, $"Failed to create {targetSubtype} annotation.");
            }

            _ = PdfiumNative.FPDFAnnot_SetRect(annot, &newRect);
            _ = PdfiumNative.FPDFAnnot_SetColor(annot, PdfiumConstants.AnnotColorTypeColor, source.Color.R, source.Color.G, source.Color.B, source.Color.A);
            if (source.InteriorColor is { } ic)
            {
                _ = PdfiumNative.FPDFAnnot_SetColor(annot, PdfiumConstants.AnnotColorTypeInterior, ic.R, ic.G, ic.B, ic.A);
            }

            _ = PdfiumNative.FPDFAnnot_SetBorder(annot, 0f, 0f, borderW);
            _ = PdfiumNative.FPDFAnnot_SetFlags(annot, PdfiumConstants.AnnotFlagPrint);
            SetAnnotString(annot, "T", author);
            SetAnnotString(annot, "M", PdfSignatureInspector.FormatPdfDate(DateTimeOffset.Now));
            if (!string.IsNullOrWhiteSpace(source.Contents))
            {
                SetAnnotString(annot, "Contents", source.Contents);
            }

            string apStream;
            if (source.CapturedPaths is { Count: > 0 } capturedPaths)
            {
                if (targetSubtype == PdfAnnotationSubtype.Ink && source.Strokes is { Count: > 0 })
                {
                    AddTranslatedInkStrokes(annot, source.Strokes, dx, dy);
                }

                apStream = BuildCapturedPathsAppearanceStream(capturedPaths, source.Bounds, newRect, borderW, source.Color);
            }
            else if (targetSubtype is PdfAnnotationSubtype.Square or PdfAnnotationSubtype.Circle)
            {
                apStream = BuildShapeAppearanceStream(targetSubtype, newRect, source.Color, source.InteriorColor, borderW);
            }
            else
            {
                List<FsPointF[]> translatedStrokes = AddTranslatedInkStrokesOrFallback(annot, source.Strokes, newRect, dx, dy);
                apStream = BuildInkAppearanceStream(translatedStrokes, source.Color, borderW);
            }

            SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
            createdIndex = PdfiumNative.FPDFPage_GetAnnotIndex(page, annot);
        }

        IReadOnlyList<PdfAnnotationInfo> refreshed = ReadPageAnnotations(documentId, request.TargetPageIndex, cancellationToken);
        return new PdfPageAnnotationMutationResult(request.TargetPageIndex, createdIndex, refreshed);
    }

    /// <summary>
    /// Removes an annotation (and any linked Popup annotation) from <paramref name="pageIndex"/>.
    /// </summary>
    internal IReadOnlyList<PdfAnnotationInfo> DeleteAnnotation(
        long documentId,
        int pageIndex,
        int annotIndex,
        CancellationToken cancellationToken)
    {
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumPageHandle page = _pageCache.GetOrLoad(document, documentId, pageIndex)
            ?? throw new PdfiumException(PdfiumErrorCode.Page, $"Page {pageIndex} could not be loaded.");

        int popupIndex = -1;
        using (PdfiumAnnotationHandle annot = PdfiumNative.FPDFPage_GetAnnot(page, annotIndex))
        {
            if (!annot.IsInvalid)
            {
                using PdfiumAnnotationHandle popup = PdfiumNative.FPDFAnnot_GetLinkedAnnot(annot, "Popup");
                if (!popup.IsInvalid)
                {
                    popupIndex = PdfiumNative.FPDFPage_GetAnnotIndex(page, popup);
                }
            }
        }

        if (popupIndex >= 0 && popupIndex != annotIndex)
        {
            if (popupIndex > annotIndex)
            {
                _ = PdfiumNative.FPDFPage_RemoveAnnot(page, popupIndex);
                _ = PdfiumNative.FPDFPage_RemoveAnnot(page, annotIndex);
            }
            else
            {
                _ = PdfiumNative.FPDFPage_RemoveAnnot(page, annotIndex);
                _ = PdfiumNative.FPDFPage_RemoveAnnot(page, popupIndex);
            }
        }
        else
        {
            _ = PdfiumNative.FPDFPage_RemoveAnnot(page, annotIndex);
        }

        return ReadPageAnnotations(documentId, pageIndex, cancellationToken);
    }

    private void ReadPageAnnotationsCore(
        PdfiumDocumentHandle document,
        long documentId,
        PdfiumPageHandle page,
        PdfiumTextPageHandle? cachedTextPage,
        int pageIndex,
        bool allowTextQuote,
        ref int nextSigWidgetIdx,
        int totalSigCount,
        List<PdfAnnotationInfo> destination,
        bool useCachedTextPage)
    {
        int count = Math.Max(0, PdfiumNative.FPDFPage_GetAnnotCount(page));
        if (count == 0)
        {
            return;
        }

        PdfiumTextPageHandle? ownedTransientText = null;
        try
        {
            for (int i = 0; i < count; i++)
            {
                using PdfiumAnnotationHandle annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
                if (annot.IsInvalid)
                {
                    continue;
                }

                PdfAnnotationSubtype subtype = (PdfAnnotationSubtype)PdfiumNative.FPDFAnnot_GetSubtype(annot);
                if (subtype is PdfAnnotationSubtype.Link or PdfAnnotationSubtype.Popup)
                {
                    continue;
                }

                bool isSigWidget = false;
                int signatureIndex = -1;
                string contents = ReadAnnotString(annot, "Contents");

                if (subtype == PdfAnnotationSubtype.Widget)
                {
                    if (totalSigCount > 0 && IsSignatureWidgetAnnotation(annot))
                    {
                        isSigWidget = true;
                        signatureIndex = Math.Min(nextSigWidgetIdx, totalSigCount - 1);
                        nextSigWidgetIdx++;
                    }
                    else if (string.IsNullOrWhiteSpace(contents))
                    {
                        continue;
                    }
                }

                FsRectF rawRect = default;
                _ = PdfiumNative.FPDFAnnot_GetRect(annot, &rawRect);
                PdfRect bounds = PdfRect.FromCorners(rawRect.Left, rawRect.Bottom, rawRect.Right, rawRect.Top);

                IReadOnlyList<PdfRect> quadRects = ReadAnnotQuadRects(annot);

                string author = ReadAnnotString(annot, "T");
                string modRaw = ReadAnnotString(annot, "M");
                DateTimeOffset? modDate = PdfSignatureInspector.ParsePdfDate(modRaw);
                string subject = ReadAnnotString(annot, "Subj");
                int flags = PdfiumNative.FPDFAnnot_GetFlags(annot);

                PdfAnnotationColor? interiorColor = null;
                float borderWidthPt = 2.0f;
                IReadOnlyList<IReadOnlyList<Vector2>>? strokes = null;
                IReadOnlyList<PdfCapturedPath>? capturedPaths = null;

                if (!isSigWidget && subtype is not (PdfAnnotationSubtype.Highlight or PdfAnnotationSubtype.Underline or PdfAnnotationSubtype.Squiggly or PdfAnnotationSubtype.StrikeOut))
                {
                    if (PdfiumNative.FPDFAnnot_GetColor(annot, PdfiumConstants.AnnotColorTypeInterior, out uint ir, out uint ig, out uint ib, out uint ia) != 0 && ia > 0)
                    {
                        interiorColor = new PdfAnnotationColor((byte)Math.Clamp(ir, 0u, 255u), (byte)Math.Clamp(ig, 0u, 255u), (byte)Math.Clamp(ib, 0u, 255u), (byte)Math.Clamp(ia, 0u, 255u));
                    }

                    if (PdfiumNative.FPDFAnnot_GetBorder(annot, out _, out _, out float bw) != 0 && bw > 0.1f)
                    {
                        borderWidthPt = Math.Clamp(bw, 0.5f, 32f);
                    }
                    else if (PdfiumNative.FPDFAnnot_GetNumberValue(annot, "W", out float numW) != 0 && numW > 0.1f)
                    {
                        borderWidthPt = Math.Clamp(numW, 0.5f, 32f);
                    }

                    strokes = ReadAnnotStrokes(annot, subtype);
                    capturedPaths = ReadAnnotCapturedPaths(annot);
                    if (capturedPaths is { Count: > 0 })
                    {
                        for (int cp = 0; cp < capturedPaths.Count; cp++)
                        {
                            PdfCapturedPath path = capturedPaths[cp];
                            if (interiorColor is null && path.FillMode != 0 && path.FillColor is { A: > 0 } fc && subtype != PdfAnnotationSubtype.Text)
                            {
                                interiorColor = fc;
                            }

                            if (path.HasStroke && path.StrokeWidthPt > 0.1f)
                            {
                                borderWidthPt = Math.Clamp(path.StrokeWidthPt, 0.5f, 32f);
                                break;
                            }
                        }
                    }
                }

                PdfAnnotationColor color = ReadAnnotColor(annot, subtype, capturedPaths);
                PdfFreeTextStyle? textStyle = null;
                if (subtype == PdfAnnotationSubtype.Stamp &&
                    PdfFreeTextStyle.TryParse(ReadAnnotString(annot, "WinPDFStyle"), out PdfFreeTextStyle? parsedStyle) &&
                    parsedStyle is not null)
                {
                    textStyle = parsedStyle;
                    color = parsedStyle.Color;
                }

                string quotedText = string.Empty;
                if (allowTextQuote &&
                    string.IsNullOrWhiteSpace(contents) &&
                    subtype is PdfAnnotationSubtype.Highlight or PdfAnnotationSubtype.Underline or PdfAnnotationSubtype.Squiggly or PdfAnnotationSubtype.StrikeOut)
                {
                    PdfiumTextPageHandle? textPage = cachedTextPage;
                    if (textPage is null)
                    {
                        if (useCachedTextPage)
                        {
                            textPage = _pageCache.GetOrLoadTextPage(document, documentId, pageIndex);
                            cachedTextPage = textPage;
                        }
                        else
                        {
                            ownedTransientText ??= PdfiumNative.FPDFText_LoadPage(page);
                            textPage = ownedTransientText;
                        }
                    }

                    if (textPage is { IsInvalid: false })
                    {
                        quotedText = ReadMarkupSnippet(textPage, quadRects, bounds);
                    }
                }

                destination.Add(new PdfAnnotationInfo(
                    PageIndex: pageIndex,
                    AnnotIndex: i,
                    Subtype: subtype,
                    Bounds: bounds,
                    QuadRects: quadRects,
                    Contents: contents,
                    Author: author,
                    ModifiedDateRaw: modRaw,
                    ModifiedDate: modDate,
                    Color: color,
                    Flags: flags,
                    Subject: subject,
                    QuotedText: quotedText,
                    IsSignatureWidget: isSigWidget,
                    SignatureIndex: signatureIndex,
                    InteriorColor: interiorColor,
                    BorderWidthPt: borderWidthPt,
                    Strokes: strokes,
                    CapturedPaths: capturedPaths,
                    TextStyle: textStyle));
            }
        }
        finally
        {
            ownedTransientText?.Dispose();
        }
    }

    private int CountSignatureWidgetsBeforePage(PdfiumDocumentHandle document, long documentId, int targetPageIndex)
    {
        int count = 0;
        for (int p = 0; p < targetPageIndex; p++)
        {
            if (_pageCache.TryGetCached(documentId, p, out PdfiumPageHandle? cachedPage, out _) && cachedPage is { IsInvalid: false })
            {
                count += CountSignatureWidgetsOnSinglePage(cachedPage);
            }
            else
            {
                using PdfiumPageHandle transientPage = PdfiumNative.FPDF_LoadPage(document, p);
                if (!transientPage.IsInvalid)
                {
                    count += CountSignatureWidgetsOnSinglePage(transientPage);
                }
            }
        }

        return count;
    }

    private static int CountSignatureWidgetsOnSinglePage(PdfiumPageHandle page)
    {
        int total = Math.Max(0, PdfiumNative.FPDFPage_GetAnnotCount(page));
        int sigs = 0;
        for (int i = 0; i < total; i++)
        {
            using PdfiumAnnotationHandle annot = PdfiumNative.FPDFPage_GetAnnot(page, i);
            if (!annot.IsInvalid &&
                PdfiumNative.FPDFAnnot_GetSubtype(annot) == (int)PdfAnnotationSubtype.Widget &&
                IsSignatureWidgetAnnotation(annot))
            {
                sigs++;
            }
        }

        return sigs;
    }

    private static bool IsSignatureWidgetAnnotation(PdfiumAnnotationHandle annot)
    {
        string ft = ReadAnnotString(annot, "FT");
        if (string.Equals(ft, "Sig", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int vType = PdfiumNative.FPDFAnnot_GetValueType(annot, "V");
        return vType is PdfiumConstants.ObjectDictionary or PdfiumConstants.ObjectReference;
    }

    private static PdfAnnotationColor ReadAnnotColor(
        PdfiumAnnotationHandle annot,
        PdfAnnotationSubtype subtype,
        IReadOnlyList<PdfCapturedPath>? capturedPaths = null)
    {
        if (subtype != PdfAnnotationSubtype.Text && capturedPaths is { Count: > 0 })
        {
            for (int i = 0; i < capturedPaths.Count; i++)
            {
                PdfCapturedPath path = capturedPaths[i];
                if (path.HasStroke && path.StrokeColor is { A: > 0 } sc)
                {
                    return sc;
                }
            }

            for (int i = 0; i < capturedPaths.Count; i++)
            {
                PdfCapturedPath path = capturedPaths[i];
                if (path.FillMode != 0 && path.FillColor is { A: > 0 } fc)
                {
                    return fc;
                }
            }
        }

        if (PdfiumNative.FPDFAnnot_GetColor(annot, PdfiumConstants.AnnotColorTypeColor, out uint r, out uint g, out uint b, out uint a) != 0 && a > 0)
        {
            return new PdfAnnotationColor((byte)Math.Clamp(r, 0u, 255u), (byte)Math.Clamp(g, 0u, 255u), (byte)Math.Clamp(b, 0u, 255u), (byte)Math.Clamp(a, 0u, 255u));
        }

        // PDFium returns 0 from FPDFAnnot_GetColor once an /AP stream is attached; inspect vector /AP objects first.
        int objCount = Math.Min(PdfiumNative.FPDFAnnot_GetObjectCount(annot), 64);
        if (objCount > 0)
        {
            const int PageObjPath = 2;
            PdfAnnotationColor? fallbackFill = null;
            for (int i = 0; i < objCount; i++)
            {
                nint obj = PdfiumNative.FPDFAnnot_GetObject(annot, i);
                if (obj == nint.Zero || PdfiumNative.FPDFPageObj_GetType(obj) != PageObjPath)
                {
                    continue;
                }

                if (PdfiumNative.FPDFPath_GetDrawMode(obj, out int fillMode, out int stroke) == 0)
                {
                    continue;
                }

                if (subtype == PdfAnnotationSubtype.Text && fillMode != 0 &&
                    PdfiumNative.FPDFPageObj_GetFillColor(obj, out uint fr, out uint fg, out uint fb, out uint fa) != 0 && fa > 0)
                {
                    return new PdfAnnotationColor((byte)Math.Clamp(fr, 0u, 255u), (byte)Math.Clamp(fg, 0u, 255u), (byte)Math.Clamp(fb, 0u, 255u), (byte)Math.Clamp(fa, 0u, 255u));
                }

                if (stroke != 0 &&
                    PdfiumNative.FPDFPageObj_GetStrokeColor(obj, out uint sr, out uint sg, out uint sb, out uint sa) != 0 && sa > 0)
                {
                    if (subtype != PdfAnnotationSubtype.Text)
                    {
                        return new PdfAnnotationColor((byte)Math.Clamp(sr, 0u, 255u), (byte)Math.Clamp(sg, 0u, 255u), (byte)Math.Clamp(sb, 0u, 255u), (byte)Math.Clamp(sa, 0u, 255u));
                    }

                    fallbackFill ??= new PdfAnnotationColor((byte)Math.Clamp(sr, 0u, 255u), (byte)Math.Clamp(sg, 0u, 255u), (byte)Math.Clamp(sb, 0u, 255u), (byte)Math.Clamp(sa, 0u, 255u));
                }

                if (fillMode != 0 &&
                    PdfiumNative.FPDFPageObj_GetFillColor(obj, out uint fr2, out uint fg2, out uint fb2, out uint fa2) != 0 && fa2 > 0)
                {
                    fallbackFill ??= new PdfAnnotationColor((byte)Math.Clamp(fr2, 0u, 255u), (byte)Math.Clamp(fg2, 0u, 255u), (byte)Math.Clamp(fb2, 0u, 255u), (byte)Math.Clamp(fa2, 0u, 255u));
                }
            }

            if (fallbackFill is { } resolved)
            {
                return resolved;
            }
        }

        if (TryParseColorFromApStream(annot, subtype) is { } apColor)
        {
            return apColor;
        }

        if (PdfiumNative.FPDFAnnot_GetColor(annot, PdfiumConstants.AnnotColorTypeInterior, out uint ir, out uint ig, out uint ib, out uint ia) != 0 && ia > 0)
        {
            return new PdfAnnotationColor((byte)Math.Clamp(ir, 0u, 255u), (byte)Math.Clamp(ig, 0u, 255u), (byte)Math.Clamp(ib, 0u, 255u), (byte)Math.Clamp(ia, 0u, 255u));
        }

        return subtype switch
        {
            PdfAnnotationSubtype.Highlight => PdfAnnotationColor.Yellow,
            PdfAnnotationSubtype.Text => PdfAnnotationColor.Yellow,
            PdfAnnotationSubtype.FreeText => PdfAnnotationColor.Yellow,
            PdfAnnotationSubtype.StrikeOut => new PdfAnnotationColor(220, 72, 72, 255),
            PdfAnnotationSubtype.Underline or PdfAnnotationSubtype.Squiggly => new PdfAnnotationColor(45, 165, 95, 255),
            _ => PdfAnnotationColor.Blue,
        };
    }

    private static PdfAnnotationColor? TryParseColorFromApStream(PdfiumAnnotationHandle annot, PdfAnnotationSubtype subtype)
    {
        uint bytesNeeded = PdfiumNative.FPDFAnnot_GetAP(annot, PdfiumConstants.AnnotAppearanceNormal, null, 0);
        if (bytesNeeded <= 2 || bytesNeeded > 262144)
        {
            return null;
        }

        int charCount = (int)(bytesNeeded / sizeof(char));
        char[] buffer = new char[charCount];
        fixed (char* pBuf = buffer)
        {
            uint written = PdfiumNative.FPDFAnnot_GetAP(annot, PdfiumConstants.AnnotAppearanceNormal, pBuf, bytesNeeded);
            if (written <= 2)
            {
                return null;
            }
        }

        string ap = new string(buffer).TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(ap))
        {
            return null;
        }

        // Tokenize basic PDF content stream color operators (RG/rg, G/g, K/k).
        string[] tokens = ap.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        PdfAnnotationColor? firstStroke = null;
        PdfAnnotationColor? firstFill = null;

        for (int i = 0; i < tokens.Length; i++)
        {
            string op = tokens[i];
            if ((op is "RG" or "rg") && i >= 3 &&
                float.TryParse(tokens[i - 3], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
                float.TryParse(tokens[i - 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float g) &&
                float.TryParse(tokens[i - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
            {
                PdfAnnotationColor c = new(
                    (byte)Math.Clamp((int)MathF.Round(r * 255f), 0, 255),
                    (byte)Math.Clamp((int)MathF.Round(g * 255f), 0, 255),
                    (byte)Math.Clamp((int)MathF.Round(b * 255f), 0, 255),
                    255);
                if (op == "RG")
                {
                    firstStroke ??= c;
                }
                else
                {
                    firstFill ??= c;
                }
            }
            else if ((op is "G" or "g") && i >= 1 &&
                float.TryParse(tokens[i - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float gray))
            {
                byte v = (byte)Math.Clamp((int)MathF.Round(gray * 255f), 0, 255);
                PdfAnnotationColor c = new(v, v, v, 255);
                if (op == "G")
                {
                    firstStroke ??= c;
                }
                else
                {
                    firstFill ??= c;
                }
            }
            else if ((op is "K" or "k") && i >= 4 &&
                float.TryParse(tokens[i - 4], NumberStyles.Float, CultureInfo.InvariantCulture, out float cyan) &&
                float.TryParse(tokens[i - 3], NumberStyles.Float, CultureInfo.InvariantCulture, out float magenta) &&
                float.TryParse(tokens[i - 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float yellow) &&
                float.TryParse(tokens[i - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float black))
            {
                byte rByte = (byte)Math.Clamp((int)MathF.Round(255f * (1f - cyan) * (1f - black)), 0, 255);
                byte gByte = (byte)Math.Clamp((int)MathF.Round(255f * (1f - magenta) * (1f - black)), 0, 255);
                byte bByte = (byte)Math.Clamp((int)MathF.Round(255f * (1f - yellow) * (1f - black)), 0, 255);
                PdfAnnotationColor c = new(rByte, gByte, bByte, 255);
                if (op == "K")
                {
                    firstStroke ??= c;
                }
                else
                {
                    firstFill ??= c;
                }
            }

            if (subtype == PdfAnnotationSubtype.Text && firstFill is not null)
            {
                return firstFill;
            }

            if (subtype != PdfAnnotationSubtype.Text && firstStroke is not null)
            {
                return firstStroke;
            }
        }

        return subtype == PdfAnnotationSubtype.Text
            ? firstFill ?? firstStroke
            : firstStroke ?? firstFill;
    }

    private static IReadOnlyList<PdfRect> ReadAnnotQuadRects(PdfiumAnnotationHandle annot)
    {
        if (PdfiumNative.FPDFAnnot_HasAttachmentPoints(annot) == 0)
        {
            return Array.Empty<PdfRect>();
        }

        nuint quadCount = PdfiumNative.FPDFAnnot_CountAttachmentPoints(annot);
        int cappedQuads = (int)Math.Min(quadCount, 2048);
        if (cappedQuads <= 0)
        {
            return Array.Empty<PdfRect>();
        }

        List<PdfRect> quads = new(cappedQuads);
        for (int q = 0; q < cappedQuads; q++)
        {
            FsQuadPointsF qp = default;
            if (PdfiumNative.FPDFAnnot_GetAttachmentPoints(annot, (nuint)q, &qp) != 0)
            {
                float ql = Math.Min(Math.Min(qp.X1, qp.X2), Math.Min(qp.X3, qp.X4));
                float qr = Math.Max(Math.Max(qp.X1, qp.X2), Math.Max(qp.X3, qp.X4));
                float qb = Math.Min(Math.Min(qp.Y1, qp.Y2), Math.Min(qp.Y3, qp.Y4));
                float qt = Math.Max(Math.Max(qp.Y1, qp.Y2), Math.Max(qp.Y3, qp.Y4));
                PdfRect qRect = new(ql, qb, qr, qt);
                if (!qRect.IsEmpty)
                {
                    quads.Add(qRect);
                }
            }
        }

        return quads.Count > 0 ? quads : Array.Empty<PdfRect>();
    }

    private static IReadOnlyList<PdfCapturedPath>? ReadAnnotCapturedPaths(PdfiumAnnotationHandle annot)
    {
        int objCount = Math.Min(PdfiumNative.FPDFAnnot_GetObjectCount(annot), 512);
        if (objCount <= 0)
        {
            return null;
        }

        FsMatrix identity = new() { A = 1f, B = 0f, C = 0f, D = 1f, E = 0f, F = 0f };
        List<PdfCapturedPath> paths = new(objCount);
        for (int i = 0; i < objCount; i++)
        {
            nint obj = PdfiumNative.FPDFAnnot_GetObject(annot, i);
            if (obj != nint.Zero)
            {
                CollectAnnotPathObjects(obj, identity, paths, depth: 0);
            }
        }

        return paths.Count > 0 ? paths : null;
    }

    private static FsMatrix MultiplyMatrix(FsMatrix local, FsMatrix parent) =>
        new()
        {
            A = (local.A * parent.A) + (local.B * parent.C),
            B = (local.A * parent.B) + (local.B * parent.D),
            C = (local.C * parent.A) + (local.D * parent.C),
            D = (local.C * parent.B) + (local.D * parent.D),
            E = (local.E * parent.A) + (local.F * parent.C) + parent.E,
            F = (local.E * parent.B) + (local.F * parent.D) + parent.F,
        };

    private static void CollectAnnotPathObjects(
        nint obj,
        FsMatrix parentMatrix,
        List<PdfCapturedPath> paths,
        int depth)
    {
        const int PageObjPath = 2;
        const int PageObjForm = 5;

        int objType = PdfiumNative.FPDFPageObj_GetType(obj);
        if (objType == PageObjForm && depth < 8)
        {
            FsMatrix local = new() { A = 1f, B = 0f, C = 0f, D = 1f, E = 0f, F = 0f };
            _ = PdfiumNative.FPDFPageObj_GetMatrix(obj, &local);
            FsMatrix composed = MultiplyMatrix(local, parentMatrix);

            int childCount = Math.Min(PdfiumNative.FPDFFormObj_CountObjects(obj), 512);
            for (int i = 0; i < childCount; i++)
            {
                nint child = PdfiumNative.FPDFFormObj_GetObject(obj, (uint)i);
                if (child != nint.Zero)
                {
                    CollectAnnotPathObjects(child, composed, paths, depth + 1);
                }
            }

            return;
        }

        if (objType != PageObjPath)
        {
            return;
        }

        if (PdfiumNative.FPDFPath_GetDrawMode(obj, out int fillMode, out int strokeFlag) == 0)
        {
            return;
        }

        bool hasStroke = strokeFlag != 0;
        PdfAnnotationColor? strokeColor = null;
        if (hasStroke && PdfiumNative.FPDFPageObj_GetStrokeColor(obj, out uint sr, out uint sg, out uint sb, out uint sa) != 0)
        {
            if (sa > 0)
            {
                strokeColor = new PdfAnnotationColor((byte)Math.Clamp(sr, 0u, 255u), (byte)Math.Clamp(sg, 0u, 255u), (byte)Math.Clamp(sb, 0u, 255u), (byte)Math.Clamp(sa, 0u, 255u));
            }
            else
            {
                hasStroke = false;
            }
        }

        PdfAnnotationColor? fillColor = null;
        if (fillMode != 0 && PdfiumNative.FPDFPageObj_GetFillColor(obj, out uint fr, out uint fg, out uint fb, out uint fa) != 0)
        {
            if (fa > 0)
            {
                fillColor = new PdfAnnotationColor((byte)Math.Clamp(fr, 0u, 255u), (byte)Math.Clamp(fg, 0u, 255u), (byte)Math.Clamp(fb, 0u, 255u), (byte)Math.Clamp(fa, 0u, 255u));
            }
            else
            {
                fillMode = 0;
            }
        }

        if (fillMode == 0 && !hasStroke)
        {
            return;
        }

        int segCount = PdfiumNative.FPDFPath_CountSegments(obj);
        if (segCount <= 0 || segCount > 32768)
        {
            return;
        }

        FsMatrix localPath = new() { A = 1f, B = 0f, C = 0f, D = 1f, E = 0f, F = 0f };
        _ = PdfiumNative.FPDFPageObj_GetMatrix(obj, &localPath);
        FsMatrix m = MultiplyMatrix(localPath, parentMatrix);

        float strokeWidth = 1.5f;
        if (PdfiumNative.FPDFPageObj_GetStrokeWidth(obj, out float rawW) != 0 && rawW > 0.05f)
        {
            float scale = MathF.Sqrt((m.A * m.A) + (m.B * m.B));
            strokeWidth = Math.Clamp(rawW * (scale > 0.05f ? scale : 1f), 0.35f, 32f);
        }

        List<PdfCapturedPathSegment> segs = new(segCount);
        for (int s = 0; s < segCount; s++)
        {
            nint seg = PdfiumNative.FPDFPath_GetPathSegment(obj, s);
            if (seg == nint.Zero || PdfiumNative.FPDFPathSegment_GetPoint(seg, out float rawX, out float rawY) == 0)
            {
                continue;
            }

            float tx = (m.A * rawX) + (m.C * rawY) + m.E;
            float ty = (m.B * rawX) + (m.D * rawY) + m.F;
            byte segType = (byte)Math.Clamp(PdfiumNative.FPDFPathSegment_GetType(seg), 0, 2);
            bool isClosed = PdfiumNative.FPDFPathSegment_GetClose(seg) != 0;
            segs.Add(new PdfCapturedPathSegment(segType, tx, ty, isClosed));
        }

        if (segs.Count > 0)
        {
            paths.Add(new PdfCapturedPath(segs, strokeColor, fillColor, strokeWidth, fillMode, hasStroke));
        }
    }

    private static string ReadMarkupSnippet(PdfiumTextPageHandle textPage, IReadOnlyList<PdfRect> quadRects, PdfRect bounds)
    {
        if (quadRects.Count > 0)
        {
            StringBuilder sb = new(128);
            int maxRects = Math.Min(quadRects.Count, 3);
            for (int i = 0; i < maxRects && sb.Length < 120; i++)
            {
                string part = ReadBoundedTextCore(textPage, quadRects[i]);
                if (!string.IsNullOrWhiteSpace(part))
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(' ');
                    }

                    sb.Append(part.Trim());
                }
            }

            string joined = sb.ToString();
            return joined.Length > 140 ? string.Concat(joined.AsSpan(0, 137), "...") : joined;
        }

        string single = ReadBoundedTextCore(textPage, bounds).Trim();
        return single.Length > 140 ? string.Concat(single.AsSpan(0, 137), "...") : single;
    }

    private static string ReadBoundedTextCore(PdfiumTextPageHandle textPage, PdfRect rect)
    {
        int required = PdfiumNative.FPDFText_GetBoundedText(textPage, rect.Left - 1.0, rect.Top + 1.0, rect.Right + 1.0, rect.Bottom - 1.0, null, 0);
        if (required <= 1)
        {
            return string.Empty;
        }

        int capped = Math.Min(required, 512);
        char* buf = stackalloc char[capped];
        int written = PdfiumNative.FPDFText_GetBoundedText(textPage, rect.Left - 1.0, rect.Top + 1.0, rect.Right + 1.0, rect.Bottom - 1.0, buf, capped);
        return TrimNullTerminatedSpan(new ReadOnlySpan<char>(buf, Math.Clamp(written, 0, capped)));
    }

    private static string ReadAnnotString(PdfiumAnnotationHandle annot, string key)
    {
        uint requiredBytes = PdfiumNative.FPDFAnnot_GetStringValue(annot, key, null, 0);
        if (requiredBytes <= sizeof(char))
        {
            return string.Empty;
        }

        int cappedBytes = (int)Math.Min(requiredBytes, 65536u);
        int charCapacity = (cappedBytes / sizeof(char)) + 1;
        if (charCapacity <= MaxStackAllocChars)
        {
            char* buf = stackalloc char[MaxStackAllocChars];
            uint writtenBytes = PdfiumNative.FPDFAnnot_GetStringValue(annot, key, buf, (uint)(MaxStackAllocChars * sizeof(char)));
            int charCount = (int)(Math.Min(writtenBytes, (uint)(MaxStackAllocChars * sizeof(char))) / sizeof(char));
            return TrimNullTerminatedSpan(new ReadOnlySpan<char>(buf, charCount));
        }

        char* nativeBuf = (char*)NativeMemory.Alloc((nuint)charCapacity, sizeof(char));
        try
        {
            uint writtenBytes = PdfiumNative.FPDFAnnot_GetStringValue(annot, key, nativeBuf, (uint)(charCapacity * sizeof(char)));
            int charCount = (int)(Math.Min(writtenBytes, (uint)(charCapacity * sizeof(char))) / sizeof(char));
            return TrimNullTerminatedSpan(new ReadOnlySpan<char>(nativeBuf, charCount));
        }
        finally
        {
            NativeMemory.Free(nativeBuf);
        }
    }

    private static void SetAnnotString(PdfiumAnnotationHandle annot, string key, string? value)
    {
        string safeVal = value ?? string.Empty;
        int neededChars = safeVal.Length + 1;
        if (neededChars <= MaxStackAllocChars)
        {
            char* buf = stackalloc char[MaxStackAllocChars];
            safeVal.AsSpan().CopyTo(new Span<char>(buf, neededChars));
            buf[safeVal.Length] = '\0';
            _ = PdfiumNative.FPDFAnnot_SetStringValue(annot, key, buf);
            return;
        }

        char* nativeBuf = (char*)NativeMemory.Alloc((nuint)neededChars, sizeof(char));
        try
        {
            safeVal.AsSpan().CopyTo(new Span<char>(nativeBuf, neededChars));
            nativeBuf[safeVal.Length] = '\0';
            _ = PdfiumNative.FPDFAnnot_SetStringValue(annot, key, nativeBuf);
        }
        finally
        {
            NativeMemory.Free(nativeBuf);
        }
    }

    private static void SetAnnotApStream(PdfiumAnnotationHandle annot, int appearanceMode, string? apStream)
    {
        if (apStream is null)
        {
            _ = PdfiumNative.FPDFAnnot_SetAP(annot, appearanceMode, null);
            return;
        }

        int neededChars = apStream.Length + 1;
        if (neededChars <= MaxStackAllocChars)
        {
            char* buf = stackalloc char[MaxStackAllocChars];
            apStream.AsSpan().CopyTo(new Span<char>(buf, neededChars));
            buf[apStream.Length] = '\0';
            _ = PdfiumNative.FPDFAnnot_SetAP(annot, appearanceMode, buf);
            return;
        }

        char* nativeBuf = (char*)NativeMemory.Alloc((nuint)neededChars, sizeof(char));
        try
        {
            apStream.AsSpan().CopyTo(new Span<char>(nativeBuf, neededChars));
            nativeBuf[apStream.Length] = '\0';
            _ = PdfiumNative.FPDFAnnot_SetAP(annot, appearanceMode, nativeBuf);
        }
        finally
        {
            NativeMemory.Free(nativeBuf);
        }
    }

    private static byte[] ReadSignatureContents(nint sigHandle)
    {
        uint len = PdfiumNative.FPDFSignatureObj_GetContents(sigHandle, null, 0);
        if (len == 0 || len > 4 * 1024 * 1024)
        {
            return Array.Empty<byte>();
        }

        byte[] buffer = new byte[len];
        fixed (byte* ptr = buffer)
        {
            uint copied = PdfiumNative.FPDFSignatureObj_GetContents(sigHandle, ptr, len);
            if (copied == 0)
            {
                return Array.Empty<byte>();
            }
        }

        return buffer;
    }

    private static int[] ReadSignatureByteRange(nint sigHandle)
    {
        uint count = PdfiumNative.FPDFSignatureObj_GetByteRange(sigHandle, null, 0);
        if (count == 0 || count > 64)
        {
            return Array.Empty<int>();
        }

        int[] buffer = new int[count];
        fixed (int* ptr = buffer)
        {
            _ = PdfiumNative.FPDFSignatureObj_GetByteRange(sigHandle, ptr, count);
        }

        return buffer;
    }

    private delegate uint AsciiReader(nint handle, byte* buffer, uint length);

    private static string ReadSignatureAscii(nint sigHandle, AsciiReader reader)
    {
        uint len = reader(sigHandle, null, 0);
        if (len <= 1 || len > 4096)
        {
            return string.Empty;
        }

        byte* buf = stackalloc byte[(int)len];
        uint written = reader(sigHandle, buf, len);
        ReadOnlySpan<byte> span = new(buf, (int)Math.Min(written, len));
        int nulIdx = span.IndexOf((byte)0);
        if (nulIdx >= 0)
        {
            span = span[..nulIdx];
        }

        return Encoding.ASCII.GetString(span);
    }

    private static Box ReadVisiblePageBox(PdfiumDocumentHandle document, PdfiumPageHandle page, int pageIndex)
    {
        bool hasMedia = TryGetBox(PdfiumNative.FPDFPage_GetMediaBox(page, out float ml, out float mb, out float mr, out float mt), ml, mb, mr, mt, out Box media);
        bool hasCrop = TryGetBox(PdfiumNative.FPDFPage_GetCropBox(page, out float cl, out float cb, out float cr, out float ct), cl, cb, cr, ct, out Box crop);
        if (hasMedia && hasCrop)
        {
            Box inter = crop.Intersect(media);
            return inter.IsEmpty ? media : inter;
        }

        if (hasCrop)
        {
            return crop;
        }

        if (hasMedia)
        {
            return media;
        }

        FsSizeF size = default;
        if (PdfiumNative.FPDF_GetPageSizeByIndexF(document, pageIndex, &size) != 0 && size.Width > 0 && size.Height > 0)
        {
            return new Box(0f, 0f, size.Width, size.Height);
        }

        return new Box(0f, 0f, (float)DefaultPageWidthPt, (float)DefaultPageHeightPt);
    }

    private static List<string> WrapFreeTextLines(string contents, double maxWidthPt, out float widthPt, out float heightPt)
    {
        double targetMaxW = Math.Clamp(maxWidthPt, 110.0, 320.0);
        const double CharWidthPt = 5.8;
        const double HorizontalPadPt = 16.0;
        const double VerticalPadPt = 14.0;
        const double LineHeightPt = 14.0;

        int maxCharsPerLine = Math.Max(14, (int)Math.Floor((targetMaxW - HorizontalPadPt) / CharWidthPt));
        List<string> wrapped = new();

        string normalized = (contents ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] paragraphs = normalized.Split('\n');

        int longestChars = 0;
        foreach (string para in paragraphs)
        {
            if (para.Length == 0)
            {
                wrapped.Add(string.Empty);
                continue;
            }

            string remaining = para.TrimEnd();
            while (remaining.Length > maxCharsPerLine)
            {
                int breakIdx = remaining.LastIndexOf(' ', maxCharsPerLine);
                if (breakIdx <= 0)
                {
                    breakIdx = maxCharsPerLine;
                }

                string line = remaining[..breakIdx].TrimEnd();
                wrapped.Add(line);
                longestChars = Math.Max(longestChars, line.Length);
                remaining = remaining[breakIdx..].TrimStart();
            }

            wrapped.Add(remaining);
            longestChars = Math.Max(longestChars, remaining.Length);
        }

        if (wrapped.Count == 0)
        {
            wrapped.Add(string.Empty);
        }

        widthPt = (float)Math.Clamp((longestChars * CharWidthPt) + HorizontalPadPt + 6.0, 120.0, targetMaxW);
        heightPt = (float)Math.Clamp((wrapped.Count * LineHeightPt) + VerticalPadPt, 28.0, 420.0);
        return wrapped;
    }

    private static string BuildFreeTextAppearanceStream(FsRectF boxRect, PdfAnnotationColor color, IReadOnlyList<string> lines)
    {
        float w = Math.Max(10f, boxRect.Right - boxRect.Left);
        float h = Math.Max(10f, boxRect.Top - boxRect.Bottom);

        // Soft paper-tinted fill blended toward white so dark text remains high-contrast.
        double fillR = ((color.R * 0.28) + (255.0 * 0.72)) / 255.0;
        double fillG = ((color.G * 0.28) + (255.0 * 0.72)) / 255.0;
        double fillB = ((color.B * 0.28) + (255.0 * 0.72)) / 255.0;
        double borderR = (color.R * 0.72) / 255.0;
        double borderG = (color.G * 0.72) / 255.0;
        double borderB = (color.B * 0.72) / 255.0;

        StringBuilder sb = new(256);
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"q {fillR:F3} {fillG:F3} {fillB:F3} rg {boxRect.Left:F2} {boxRect.Bottom:F2} {w:F2} {h:F2} re f ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{borderR:F3} {borderG:F3} {borderB:F3} RG 0.9 w {boxRect.Left:F2} {boxRect.Bottom:F2} {w:F2} {h:F2} re S ");

        float textX = boxRect.Left + 7f;
        float textY = boxRect.Top - 16f;
        sb.Append("BT /Helv 11 Tf 0.10 0.10 0.10 rg ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{textX:F2} {textY:F2} Td ");

        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("0 -14 Td ");
            }

            sb.Append('(');
            AppendWinAnsiEscapedPdfLiteral(sb, lines[i]);
            sb.Append(") Tj ");
        }

        sb.Append("ET Q");
        return sb.ToString();
    }

    private nint GetOrLoadTextFont(
        PdfiumDocumentHandle document,
        long documentId,
        bool isBold,
        bool isItalic)
    {
        if (!_documentFonts.TryGetValue(documentId, out Dictionary<string, nint>? cache))
        {
            cache = new Dictionary<string, nint>(StringComparer.Ordinal);
            _documentFonts[documentId] = cache;
        }

        string stdName = (isBold, isItalic) switch
        {
            (true, true) => "Helvetica-BoldOblique",
            (true, false) => "Helvetica-Bold",
            (false, true) => "Helvetica-Oblique",
            _ => "Helvetica",
        };

        string stdKey = "std:" + stdName;
        if (cache.TryGetValue(stdKey, out nint cachedStd) && cachedStd != nint.Zero)
        {
            return cachedStd;
        }

        nint loadedStd = PdfiumNative.FPDFText_LoadStandardFont(document, stdName);
        if (loadedStd != nint.Zero)
        {
            cache[stdKey] = loadedStd;
            return loadedStd;
        }

        throw new PdfiumException(PdfiumErrorCode.Page, $"Failed to load PDF standard font '{stdName}'.");
    }

    private void ReleaseDocumentFonts(long documentId)
    {
        if (!_documentFonts.Remove(documentId, out Dictionary<string, nint>? cache))
        {
            return;
        }

        foreach (nint font in cache.Values)
        {
            if (font != nint.Zero)
            {
                PdfiumNative.FPDFFont_Close(font);
            }
        }
    }

    private static void SetPdfTextObjectString(nint textObject, string text)
    {
        if (textObject == nint.Zero || string.IsNullOrEmpty(text))
        {
            return;
        }

        fixed (char* pText = text)
        {
            _ = PdfiumNative.FPDFText_SetText(textObject, pText);
        }
    }

    private static float MeasurePdfTextWidthPt(PdfiumDocumentHandle document, nint font, float fontSizePt, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0f;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return text.Length * fontSizePt * 0.28f;
        }

        nint tempObj = PdfiumNative.FPDFPageObj_CreateTextObj(document, font, fontSizePt);
        if (tempObj == nint.Zero)
        {
            return text.Length * fontSizePt * 0.52f;
        }

        try
        {
            SetPdfTextObjectString(tempObj, text);
            if (PdfiumNative.FPDFPageObj_GetBounds(tempObj, out float left, out _, out float right, out _) != 0 &&
                right >= left)
            {
                int trailingSpaces = 0;
                for (int i = text.Length - 1; i >= 0 && text[i] == ' '; i--)
                {
                    trailingSpaces++;
                }

                return (right - left) + (trailingSpaces * fontSizePt * 0.28f);
            }
        }
        finally
        {
            PdfiumNative.FPDFPageObj_Destroy(tempObj);
        }

        return text.Length * fontSizePt * 0.52f;
    }

    private static List<string> WrapVectorTextLines(
        PdfiumDocumentHandle document,
        nint font,
        float fontSizePt,
        string contents,
        double maxWidthPt)
    {
        float sizePt = Math.Clamp(fontSizePt, 6f, 144f);
        double availW = Math.Max(40.0, maxWidthPt);
        List<string> wrapped = new();

        string normalized = (contents ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] paragraphs = normalized.Split('\n');

        foreach (string para in paragraphs)
        {
            if (para.Length == 0)
            {
                wrapped.Add(string.Empty);
                continue;
            }

            string remaining = para.TrimEnd();
            while (remaining.Length > 0)
            {
                float fullW = MeasurePdfTextWidthPt(document, font, sizePt, remaining);
                if (fullW <= availW)
                {
                    wrapped.Add(remaining);
                    break;
                }

                int low = 1;
                int high = remaining.Length;
                int best = 1;
                while (low <= high)
                {
                    int mid = low + ((high - low) / 2);
                    float w = MeasurePdfTextWidthPt(document, font, sizePt, remaining[..mid]);
                    if (w <= availW)
                    {
                        best = mid;
                        low = mid + 1;
                    }
                    else
                    {
                        high = mid - 1;
                    }
                }

                int breakIdx = remaining.LastIndexOf(' ', Math.Min(best, remaining.Length - 1));
                if (breakIdx <= 0)
                {
                    breakIdx = Math.Max(1, best);
                }

                string line = remaining[..breakIdx].TrimEnd();
                if (line.Length == 0)
                {
                    line = remaining[..Math.Max(1, best)];
                    breakIdx = line.Length;
                }

                wrapped.Add(line);
                remaining = remaining[breakIdx..].TrimStart();
            }
        }

        if (wrapped.Count == 0)
        {
            wrapped.Add(string.Empty);
        }

        return wrapped;
    }

    private static void BuildAndAttachTextStampObjects(
        PdfiumDocumentHandle document,
        PdfiumAnnotationHandle annot,
        nint font,
        Box pageBox,
        float anchorLeft,
        float anchorTop,
        PdfFreeTextStyle style,
        string contents)
    {
        float sizePt = Math.Clamp(style.FontSizePt, 6f, 144f);
        float lineHeightPt = sizePt * 1.16f;
        float ascentPt = sizePt * 0.905f;
        float margin = Math.Max(3.0f, sizePt * 0.12f);

        List<string> lines = WrapVectorTextLines(document, font, sizePt, contents, style.MaxWidthPt);
        List<nint> createdObjects = new(lines.Count * 2);
        bool hasBounds = false;
        float minL = 0f, minB = 0f, maxR = 0f, maxT = 0f;

        void AccumulateBounds(float l, float b, float r, float t)
        {
            if (!hasBounds)
            {
                minL = l;
                minB = b;
                maxR = r;
                maxT = t;
                hasBounds = true;
            }
            else
            {
                minL = Math.Min(minL, l);
                minB = Math.Min(minB, b);
                maxR = Math.Max(maxR, r);
                maxT = Math.Max(maxT, t);
            }
        }

        try
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                float baselineY = anchorTop - ascentPt - (i * lineHeightPt);
                nint textObj = PdfiumNative.FPDFPageObj_CreateTextObj(document, font, sizePt);
                if (textObj == nint.Zero)
                {
                    continue;
                }

                SetPdfTextObjectString(textObj, line);
                _ = PdfiumNative.FPDFPageObj_SetFillColor(textObj, style.Color.R, style.Color.G, style.Color.B, style.Color.A);
                PdfiumNative.FPDFPageObj_Transform(textObj, 1.0, 0.0, 0.0, 1.0, anchorLeft, baselineY);
                createdObjects.Add(textObj);

                float objL = anchorLeft;
                float objR = anchorLeft;
                if (PdfiumNative.FPDFPageObj_GetBounds(textObj, out objL, out float objB, out objR, out float objT) != 0)
                {
                    AccumulateBounds(objL, objB, objR, objT);
                }

                if (style.IsUnderline && objR > objL)
                {
                    float ulY = baselineY - (sizePt * 0.14f);
                    float strokeW = Math.Max(0.6f, sizePt * 0.065f);
                    nint pathObj = PdfiumNative.FPDFPageObj_CreateNewPath(objL, ulY);
                    if (pathObj != nint.Zero)
                    {
                        _ = PdfiumNative.FPDFPath_LineTo(pathObj, objR, ulY);
                        _ = PdfiumNative.FPDFPageObj_SetStrokeColor(pathObj, style.Color.R, style.Color.G, style.Color.B, style.Color.A);
                        _ = PdfiumNative.FPDFPageObj_SetStrokeWidth(pathObj, strokeW);
                        _ = PdfiumNative.FPDFPath_SetDrawMode(pathObj, PdfiumConstants.FillModeNone, stroke: 1);
                        createdObjects.Add(pathObj);
                        AccumulateBounds(objL, ulY - strokeW, objR, ulY + strokeW);
                    }
                }
            }

            float rawLeft = (hasBounds ? Math.Min(anchorLeft, minL) : anchorLeft) - margin;
            float rawTop = (hasBounds ? Math.Max(anchorTop, maxT) : anchorTop) + margin;
            float rawRight = (hasBounds ? Math.Max(anchorLeft + 16f, maxR) : anchorLeft + 24f) + margin;
            float rawBottom = (hasBounds ? Math.Min(anchorTop - (lines.Count * lineHeightPt), minB) : anchorTop - lineHeightPt) - margin;

            float width = Math.Max(16f, rawRight - rawLeft);
            float height = Math.Max(lineHeightPt, rawTop - rawBottom);

            float clampedLeft = Math.Clamp(rawLeft, pageBox.Left + 2f, Math.Max(pageBox.Left + 2f, pageBox.Right - width - 2f));
            float clampedTop = Math.Clamp(rawTop, pageBox.Bottom + height + 2f, Math.Max(pageBox.Bottom + height + 2f, pageBox.Top - 2f));
            float dx = clampedLeft - rawLeft;
            float dy = clampedTop - rawTop;

            if (Math.Abs(dx) > 0.001f || Math.Abs(dy) > 0.001f)
            {
                for (int i = 0; i < createdObjects.Count; i++)
                {
                    PdfiumNative.FPDFPageObj_Transform(createdObjects[i], 1.0, 0.0, 0.0, 1.0, dx, dy);
                }
            }

            FsRectF boxRect = new()
            {
                Left = clampedLeft,
                Bottom = clampedTop - height,
                Right = clampedLeft + width,
                Top = clampedTop,
            };

            // Set /Rect before appending objects so PDFium initializes /AP /N /BBox to the exact union bounds.
            _ = PdfiumNative.FPDFAnnot_SetRect(annot, &boxRect);

            for (int i = 0; i < createdObjects.Count; i++)
            {
                nint obj = createdObjects[i];
                if (PdfiumNative.FPDFAnnot_AppendObject(annot, obj) != 0)
                {
                    createdObjects[i] = nint.Zero;
                }
            }
        }
        finally
        {
            for (int i = 0; i < createdObjects.Count; i++)
            {
                if (createdObjects[i] != nint.Zero)
                {
                    PdfiumNative.FPDFPageObj_Destroy(createdObjects[i]);
                }
            }
        }
    }

    private static IReadOnlyList<IReadOnlyList<Vector2>>? ReadAnnotStrokes(PdfiumAnnotationHandle annot, PdfAnnotationSubtype subtype)
    {
        if (subtype == PdfAnnotationSubtype.Ink)
        {
            uint strokeCount = Math.Min(PdfiumNative.FPDFAnnot_GetInkListCount(annot), 1024u);
            if (strokeCount == 0)
            {
                return null;
            }

            List<IReadOnlyList<Vector2>> strokes = new((int)strokeCount);
            for (uint s = 0; s < strokeCount; s++)
            {
                uint ptCount = PdfiumNative.FPDFAnnot_GetInkListPath(annot, s, null, 0);
                if (ptCount == 0 || ptCount > 32768)
                {
                    continue;
                }

                FsPointF[] rawPts = new FsPointF[ptCount];
                uint copied;
                fixed (FsPointF* ptr = rawPts)
                {
                    copied = PdfiumNative.FPDFAnnot_GetInkListPath(annot, s, ptr, ptCount);
                }

                if (copied > 0)
                {
                    Vector2[] vecs = new Vector2[copied];
                    for (uint p = 0; p < copied; p++)
                    {
                        vecs[p] = new Vector2(rawPts[p].X, rawPts[p].Y);
                    }

                    strokes.Add(vecs);
                }
            }

            return strokes.Count > 0 ? strokes : null;
        }

        if (subtype is PdfAnnotationSubtype.Polygon or PdfAnnotationSubtype.PolyLine)
        {
            uint ptCount = PdfiumNative.FPDFAnnot_GetVertices(annot, null, 0);
            if (ptCount < 2 || ptCount > 16384)
            {
                return null;
            }

            FsPointF[] rawPts = new FsPointF[ptCount];
            uint copied;
            fixed (FsPointF* ptr = rawPts)
            {
                copied = PdfiumNative.FPDFAnnot_GetVertices(annot, ptr, ptCount);
            }

            if (copied >= 2)
            {
                bool closeLoop = subtype == PdfAnnotationSubtype.Polygon;
                Vector2[] vecs = new Vector2[closeLoop ? copied + 1 : copied];
                for (uint p = 0; p < copied; p++)
                {
                    vecs[p] = new Vector2(rawPts[p].X, rawPts[p].Y);
                }

                if (closeLoop)
                {
                    vecs[copied] = vecs[0];
                }

                return [vecs];
            }
        }

        if (subtype == PdfAnnotationSubtype.Line)
        {
            FsPointF start = default;
            FsPointF end = default;
            if (PdfiumNative.FPDFAnnot_GetLine(annot, &start, &end) != 0)
            {
                return [new[] { new Vector2(start.X, start.Y), new Vector2(end.X, end.Y) }];
            }
        }

        return null;
    }

    private static string BuildCommentIconAppearanceStream(FsRectF noteRect, PdfAnnotationColor color)
    {
        float x = noteRect.Left;
        float y = noteRect.Bottom;
        double fillR = color.R / 255.0;
        double fillG = color.G / 255.0;
        double fillB = color.B / 255.0;
        double strokeR = (color.R * 0.42) / 255.0;
        double strokeG = (color.G * 0.42) / 255.0;
        double strokeB = (color.B * 0.42) / 255.0;

        // Speech-bubble icon (22x22 pt) with rounded corners, bottom-left tail, and two horizontal text lines.
        float bL = x + 2.0f;
        float bR = x + 20.0f;
        float bB = y + 6.2f;
        float bT = y + 19.6f;
        float r = 3.2f;
        float k = r * 0.5522847f;

        StringBuilder sb = new(512);
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"q 1 J 1 j 1.25 w {fillR:F3} {fillG:F3} {fillB:F3} rg {strokeR:F3} {strokeG:F3} {strokeB:F3} RG ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(bL + r):F2} {bT:F2} m ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(bR - r):F2} {bT:F2} l ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(bR - r + k):F2} {bT:F2} {bR:F2} {(bT - r + k):F2} {bR:F2} {(bT - r):F2} c ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{bR:F2} {(bB + r):F2} l ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{bR:F2} {(bB + r - k):F2} {(bR - r + k):F2} {bB:F2} {(bR - r):F2} {bB:F2} c ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(x + 9.6f):F2} {bB:F2} l ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(x + 4.5f):F2} {(y + 2.4f):F2} l ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(x + 5.6f):F2} {bB:F2} l ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(bL + r):F2} {bB:F2} l ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(bL + r - k):F2} {bB:F2} {bL:F2} {(bB + r - k):F2} {bL:F2} {(bB + r):F2} c ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{bL:F2} {(bT - r):F2} l ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{bL:F2} {(bT - r + k):F2} {(bL + r - k):F2} {bT:F2} {(bL + r):F2} {bT:F2} c h B ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"1.35 w {(x + 6.0f):F2} {(y + 14.6f):F2} m {(x + 16.0f):F2} {(y + 14.6f):F2} l {(x + 6.0f):F2} {(y + 11.2f):F2} m {(x + 13.2f):F2} {(y + 11.2f):F2} l S Q");
        return sb.ToString();
    }

    private static string BuildShapeAppearanceStream(
        PdfAnnotationSubtype subtype,
        FsRectF rect,
        PdfAnnotationColor strokeColor,
        PdfAnnotationColor? interiorColor,
        float borderWidthPt)
    {
        float pad = Math.Max(0.5f, borderWidthPt * 0.5f);
        float l = rect.Left + pad;
        float b = rect.Bottom + pad;
        float r = Math.Max(l + 1f, rect.Right - pad);
        float t = Math.Max(b + 1f, rect.Top - pad);
        float w = r - l;
        float h = t - b;

        double sR = strokeColor.R / 255.0;
        double sG = strokeColor.G / 255.0;
        double sB = strokeColor.B / 255.0;

        StringBuilder sb = new(256);
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"q {borderWidthPt:F2} w {sR:F3} {sG:F3} {sB:F3} RG ");
        string paintOp = "S";
        if (interiorColor is { A: > 0 } ic)
        {
            double fR = ic.R / 255.0;
            double fG = ic.G / 255.0;
            double fB = ic.B / 255.0;
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{fR:F3} {fG:F3} {fB:F3} rg ");
            paintOp = "B";
        }

        if (subtype == PdfAnnotationSubtype.Circle)
        {
            float cx = (l + r) * 0.5f;
            float cy = (b + t) * 0.5f;
            float rx = w * 0.5f;
            float ry = h * 0.5f;
            float kx = rx * 0.5522847f;
            float ky = ry * 0.5522847f;
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(cx + rx):F2} {cy:F2} m ");
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(cx + rx):F2} {(cy + ky):F2} {(cx + kx):F2} {(cy + ry):F2} {cx:F2} {(cy + ry):F2} c ");
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(cx - kx):F2} {(cy + ry):F2} {(cx - rx):F2} {(cy + ky):F2} {(cx - rx):F2} {cy:F2} c ");
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(cx - rx):F2} {(cy - ky):F2} {(cx - kx):F2} {(cy - ry):F2} {cx:F2} {(cy - ry):F2} c ");
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(cx + kx):F2} {(cy - ry):F2} {(cx + rx):F2} {(cy - ky):F2} {(cx + rx):F2} {cy:F2} c h {paintOp} Q");
        }
        else
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{l:F2} {b:F2} {w:F2} {h:F2} re {paintOp} Q");
        }

        return sb.ToString();
    }

    private static string BuildInkAppearanceStream(
        IReadOnlyList<FsPointF[]> strokes,
        PdfAnnotationColor color,
        float borderWidthPt)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        StringBuilder sb = new(512);
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"q 1 J 1 j {borderWidthPt:F2} w {r:F3} {g:F3} {b:F3} RG ");
        for (int s = 0; s < strokes.Count; s++)
        {
            FsPointF[] pts = strokes[s];
            if (pts.Length == 0)
            {
                continue;
            }

            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{pts[0].X:F2} {pts[0].Y:F2} m ");
            if (pts.Length == 1)
            {
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{(pts[0].X + 0.1f):F2} {pts[0].Y:F2} l ");
            }
            else
            {
                for (int p = 1; p < pts.Length; p++)
                {
                    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{pts[p].X:F2} {pts[p].Y:F2} l ");
                }
            }

            sb.Append("S ");
        }

        sb.Append('Q');
        return sb.ToString();
    }

    private static void AddTranslatedInkStrokes(
        PdfiumAnnotationHandle annot,
        IReadOnlyList<IReadOnlyList<Vector2>> strokes,
        float dx,
        float dy)
    {
        for (int s = 0; s < strokes.Count; s++)
        {
            IReadOnlyList<Vector2> stroke = strokes[s];
            if (stroke.Count == 0)
            {
                continue;
            }

            FsPointF[] pts = new FsPointF[stroke.Count];
            for (int p = 0; p < stroke.Count; p++)
            {
                pts[p] = new FsPointF { X = stroke[p].X + dx, Y = stroke[p].Y + dy };
            }

            fixed (FsPointF* ptr = pts)
            {
                _ = PdfiumNative.FPDFAnnot_AddInkStroke(annot, ptr, (nuint)pts.Length);
            }
        }
    }

    private static List<FsPointF[]> AddTranslatedInkStrokesOrFallback(
        PdfiumAnnotationHandle annot,
        IReadOnlyList<IReadOnlyList<Vector2>>? strokes,
        FsRectF newRect,
        float dx,
        float dy)
    {
        List<FsPointF[]> translated = new();
        if (strokes is { Count: > 0 })
        {
            for (int s = 0; s < strokes.Count; s++)
            {
                IReadOnlyList<Vector2> stroke = strokes[s];
                if (stroke.Count == 0)
                {
                    continue;
                }

                FsPointF[] pts = new FsPointF[stroke.Count];
                for (int p = 0; p < stroke.Count; p++)
                {
                    pts[p] = new FsPointF { X = stroke[p].X + dx, Y = stroke[p].Y + dy };
                }

                fixed (FsPointF* ptr = pts)
                {
                    _ = PdfiumNative.FPDFAnnot_AddInkStroke(annot, ptr, (nuint)pts.Length);
                }

                translated.Add(pts);
            }
        }

        if (translated.Count == 0)
        {
            FsPointF[] fallbackPts =
            [
                new FsPointF { X = newRect.Left + 1f, Y = newRect.Bottom + 1f },
                new FsPointF { X = newRect.Right - 1f, Y = newRect.Bottom + 1f },
                new FsPointF { X = newRect.Right - 1f, Y = newRect.Top - 1f },
                new FsPointF { X = newRect.Left + 1f, Y = newRect.Top - 1f },
                new FsPointF { X = newRect.Left + 1f, Y = newRect.Bottom + 1f },
            ];
            fixed (FsPointF* ptr = fallbackPts)
            {
                _ = PdfiumNative.FPDFAnnot_AddInkStroke(annot, ptr, (nuint)fallbackPts.Length);
            }

            translated.Add(fallbackPts);
        }

        return translated;
    }

    private static string BuildCapturedPathsAppearanceStream(
        IReadOnlyList<PdfCapturedPath> paths,
        PdfRect sourceBounds,
        FsRectF targetRect,
        float fallbackBorderWidthPt,
        PdfAnnotationColor? fallbackColor = null)
    {
        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        float maxStrokeW = fallbackBorderWidthPt;

        for (int p = 0; p < paths.Count; p++)
        {
            PdfCapturedPath path = paths[p];
            if (path.HasStroke && path.StrokeWidthPt > maxStrokeW)
            {
                maxStrokeW = path.StrokeWidthPt;
            }

            for (int s = 0; s < path.Segments.Count; s++)
            {
                PdfCapturedPathSegment seg = path.Segments[s];
                minX = Math.Min(minX, seg.X);
                minY = Math.Min(minY, seg.Y);
                maxX = Math.Max(maxX, seg.X);
                maxY = Math.Max(maxY, seg.Y);
            }
        }

        float targetW = Math.Max(1f, targetRect.Right - targetRect.Left);
        float targetH = Math.Max(1f, targetRect.Top - targetRect.Bottom);

        float srcRefX;
        float srcRefY;
        float dstRefX;
        float dstRefY;
        float sx;
        float sy;

        bool isPageSpace = !sourceBounds.IsEmpty &&
                           minX >= sourceBounds.Left - 10f && maxX <= sourceBounds.Right + 10f &&
                           minY >= sourceBounds.Bottom - 10f && maxY <= sourceBounds.Top + 10f;
        bool isLocalBox = !isPageSpace &&
                          !sourceBounds.IsEmpty &&
                          minX >= -10f && minY >= -10f &&
                          maxX <= sourceBounds.Width + 10f && maxY <= sourceBounds.Height + 10f;

        if (isPageSpace)
        {
            srcRefX = sourceBounds.Left;
            srcRefY = sourceBounds.Bottom;
            dstRefX = targetRect.Left;
            dstRefY = targetRect.Bottom;
            sx = targetW / Math.Max(1f, sourceBounds.Width);
            sy = targetH / Math.Max(1f, sourceBounds.Height);
        }
        else if (isLocalBox)
        {
            srcRefX = 0f;
            srcRefY = 0f;
            dstRefX = targetRect.Left;
            dstRefY = targetRect.Bottom;
            sx = targetW / Math.Max(1f, sourceBounds.Width);
            sy = targetH / Math.Max(1f, sourceBounds.Height);
        }
        else
        {
            float pad = Math.Clamp(maxStrokeW * 0.5f, 0.25f, Math.Min(targetW, targetH) * 0.25f);
            float spanX = maxX - minX;
            float spanY = maxY - minY;
            srcRefX = minX;
            srcRefY = minY;
            dstRefX = targetRect.Left + pad;
            dstRefY = targetRect.Bottom + pad;
            float usableW = Math.Max(1f, targetW - (pad * 2f));
            float usableH = Math.Max(1f, targetH - (pad * 2f));

            if (spanX < 0.5f && spanY < 0.5f)
            {
                sx = 1f;
                sy = 1f;
                dstRefX = targetRect.Left + (targetW * 0.5f);
                dstRefY = targetRect.Bottom + (targetH * 0.5f);
            }
            else if (spanX < 0.5f)
            {
                sy = usableH / spanY;
                sx = sy;
                dstRefX = targetRect.Left + (targetW * 0.5f);
            }
            else if (spanY < 0.5f)
            {
                sx = usableW / spanX;
                sy = sx;
                dstRefY = targetRect.Bottom + (targetH * 0.5f);
            }
            else
            {
                sx = usableW / spanX;
                sy = usableH / spanY;
            }
        }

        float scaleStroke = Math.Clamp((sx + sy) * 0.5f, 0.25f, 4f);
        float MapX(float x) => dstRefX + ((x - srcRefX) * sx);
        float MapY(float y) => dstRefY + ((y - srcRefY) * sy);

        StringBuilder sb = new(1024);
        sb.Append("q 1 J 1 j ");

        for (int p = 0; p < paths.Count; p++)
        {
            PdfCapturedPath path = paths[p];
            if (path.Segments.Count == 0)
            {
                continue;
            }

            if (path.HasStroke)
            {
                PdfAnnotationColor sc = path.StrokeColor ?? fallbackColor ?? PdfAnnotationColor.Blue;
                float sw = Math.Clamp(path.StrokeWidthPt * scaleStroke, 0.35f, 32f);
                sb.Append(CultureInfo.InvariantCulture, $"{sw:F2} w {(sc.R / 255.0):F3} {(sc.G / 255.0):F3} {(sc.B / 255.0):F3} RG ");
            }

            if (path.FillMode != 0)
            {
                PdfAnnotationColor fc = path.FillColor ?? path.StrokeColor ?? fallbackColor ?? PdfAnnotationColor.Blue;
                sb.Append(CultureInfo.InvariantCulture, $"{(fc.R / 255.0):F3} {(fc.G / 255.0):F3} {(fc.B / 255.0):F3} rg ");
            }

            for (int i = 0; i < path.Segments.Count; i++)
            {
                PdfCapturedPathSegment seg = path.Segments[i];
                if (seg.Type == 2)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{MapX(seg.X):F2} {MapY(seg.Y):F2} m ");
                    if (seg.IsClosed)
                    {
                        sb.Append("h ");
                    }
                }
                else if (seg.Type == 1 && i + 2 < path.Segments.Count && path.Segments[i + 1].Type == 1 && path.Segments[i + 2].Type == 1)
                {
                    PdfCapturedPathSegment c2 = path.Segments[i + 1];
                    PdfCapturedPathSegment end = path.Segments[i + 2];
                    sb.Append(CultureInfo.InvariantCulture, $"{MapX(seg.X):F2} {MapY(seg.Y):F2} {MapX(c2.X):F2} {MapY(c2.Y):F2} {MapX(end.X):F2} {MapY(end.Y):F2} c ");
                    if (end.IsClosed)
                    {
                        sb.Append("h ");
                    }

                    i += 2;
                }
                else
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{MapX(seg.X):F2} {MapY(seg.Y):F2} l ");
                    if (seg.IsClosed)
                    {
                        sb.Append("h ");
                    }
                }
            }

            string op = (path.HasStroke, path.FillMode) switch
            {
                (true, 1) => "B*",
                (true, 2) => "B",
                (true, _) => "S",
                (false, 1) => "f*",
                (false, _) => "f",
            };
            sb.Append(op);
            sb.Append(' ');
        }

        sb.Append('Q');
        return sb.ToString();
    }

    private static string BuildMarkupLineAppearanceStream(
        PdfAnnotationSubtype subtype,
        IReadOnlyList<PdfRect> rects,
        PdfAnnotationColor color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        StringBuilder sb = new(256);
        sb.Append(CultureInfo.InvariantCulture, $"q 1 J 1 j {r:F3} {g:F3} {b:F3} RG ");

        for (int i = 0; i < rects.Count; i++)
        {
            PdfRect rect = rects[i];
            if (rect.Width < 0.5f)
            {
                continue;
            }

            if (subtype == PdfAnnotationSubtype.Squiggly)
            {
                float yLow = rect.Bottom + 0.35f;
                float yHigh = Math.Min(rect.Top - 0.5f, rect.Bottom + 1.55f);
                const float halfPeriod = 1.6f;
                sb.Append(CultureInfo.InvariantCulture, $"0.80 w {rect.Left:F2} {yLow:F2} m ");
                float x = rect.Left + halfPeriod;
                bool up = true;
                while (x < rect.Right)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{x:F2} {(up ? yHigh : yLow):F2} l ");
                    up = !up;
                    x += halfPeriod;
                }

                sb.Append(CultureInfo.InvariantCulture, $"{rect.Right:F2} {(up ? yHigh : yLow):F2} l S ");
            }
            else
            {
                float strokeW = Math.Clamp(rect.Height * 0.075f, 0.75f, 1.35f);
                float lineY = Math.Min(rect.Top - strokeW, rect.Bottom + (strokeW * 0.7f));
                sb.Append(CultureInfo.InvariantCulture, $"{strokeW:F2} w {rect.Left:F2} {lineY:F2} m {rect.Right:F2} {lineY:F2} l S ");
            }
        }

        sb.Append('Q');
        return sb.ToString();
    }

    private static void AppendWinAnsiEscapedPdfLiteral(StringBuilder sb, string text)
    {
        foreach (char ch in text)
        {
            if (ch is '\\' or '(' or ')')
            {
                sb.Append('\\');
                sb.Append(ch);
            }
            else if (ch is >= ' ' and <= '~')
            {
                sb.Append(ch);
            }
            else if (ch is >= '\u00A0' and <= '\u00FF')
            {
                uint code = ch;
                sb.Append('\\');
                sb.Append((char)('0' + ((code >> 6) & 7)));
                sb.Append((char)('0' + ((code >> 3) & 7)));
                sb.Append((char)('0' + (code & 7)));
            }
            else
            {
                sb.Append('?');
            }
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

        foreach (long docId in _documentFonts.Keys.ToArray())
        {
            ReleaseDocumentFonts(docId);
        }

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
