using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using WinPDF.Interop.Pdfium;
using WinPDF.Models;

namespace WinPDF.Services;

internal sealed unsafe partial class PdfiumEngine
{
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
                IReadOnlyList<PdfCapturedPath>? existingPathsForRecolor = null;
                PdfAnnotationColor? existingInteriorForRecolor = null;
                if (request.NewColor is not null)
                {
                    if (subtype is PdfAnnotationSubtype.Line or PdfAnnotationSubtype.Ink)
                    {
                        existingPathsForRecolor = ReadAnnotCapturedPaths(annot);
                    }
                    else if (subtype is PdfAnnotationSubtype.Square or PdfAnnotationSubtype.Circle)
                    {
                        existingInteriorForRecolor = ReadAnnotInteriorColor(annot, subtype);
                    }
                }

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
                else if (subtype is PdfAnnotationSubtype.Square or PdfAnnotationSubtype.Circle && request.NewColor is { } shapeColor)
                {
                    FsRectF rect = default;
                    if (PdfiumNative.FPDFAnnot_GetRect(annot, &rect) != 0)
                    {
                        FsRectF normRect = new()
                        {
                            Left = Math.Min(rect.Left, rect.Right),
                            Bottom = Math.Min(rect.Bottom, rect.Top),
                            Right = Math.Max(rect.Left, rect.Right),
                            Top = Math.Max(rect.Bottom, rect.Top),
                        };
                        float bw = PdfiumNative.FPDFAnnot_GetBorder(annot, out _, out _, out float borderVal) != 0 && borderVal > 0.1f
                            ? Math.Clamp(borderVal, 0.5f, 32f)
                            : 2.0f;
                        string apStream = BuildShapeAppearanceStream(subtype, normRect, shapeColor, existingInteriorForRecolor, bw);
                        SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
                    }
                }
                else if (subtype == PdfAnnotationSubtype.Line && request.NewColor is { } lineColor)
                {
                    FsRectF rect = default;
                    if (PdfiumNative.FPDFAnnot_GetRect(annot, &rect) != 0)
                    {
                        PdfRect bounds = PdfRect.FromCorners(rect.Left, rect.Bottom, rect.Right, rect.Top);
                        FsRectF normRect = new() { Left = bounds.Left, Bottom = bounds.Bottom, Right = bounds.Right, Top = bounds.Top };
                        float bw = PdfiumNative.FPDFAnnot_GetBorder(annot, out _, out _, out float borderVal) != 0 && borderVal > 0.1f
                            ? Math.Clamp(borderVal, 0.5f, 32f)
                            : 2.0f;
                        if (existingPathsForRecolor is { Count: > 0 })
                        {
                            string apStream = BuildCapturedPathsAppearanceStream(existingPathsForRecolor, bounds, normRect, bw, lineColor, overrideColor: lineColor);
                            SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
                        }
                    }
                }
                else if (subtype == PdfAnnotationSubtype.Ink && request.NewColor is { } inkColor)
                {
                    float bw = PdfiumNative.FPDFAnnot_GetBorder(annot, out _, out _, out float borderVal) != 0 && borderVal > 0.1f
                        ? Math.Clamp(borderVal, 0.5f, 32f)
                        : 2.0f;
                    uint strokeCount = PdfiumNative.FPDFAnnot_GetInkListCount(annot);
                    List<FsPointF[]> strokes = new((int)Math.Min(strokeCount, 1024u));
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
                            if (copied > 0 && copied < ptCount)
                            {
                                pts = pts[..(int)copied];
                            }
                        }

                        if (pts.Length > 0)
                        {
                            strokes.Add(pts);
                        }
                    }

                    if (strokes.Count > 0)
                    {
                        string apStream = BuildInkAppearanceStream(strokes, inkColor, bw);
                        SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
                    }
                    else if (existingPathsForRecolor is { Count: > 0 })
                    {
                        FsRectF rect = default;
                        if (PdfiumNative.FPDFAnnot_GetRect(annot, &rect) != 0)
                        {
                            PdfRect bounds = PdfRect.FromCorners(rect.Left, rect.Bottom, rect.Right, rect.Top);
                            FsRectF normRect = new() { Left = bounds.Left, Bottom = bounds.Bottom, Right = bounds.Right, Top = bounds.Top };
                            string apStream = BuildCapturedPathsAppearanceStream(existingPathsForRecolor, bounds, normRect, bw, inkColor, overrideColor: inkColor);
                            SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
                        }
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

                        float bw = PdfiumNative.FPDFAnnot_GetBorder(annot, out _, out _, out float borderVal) != 0 && borderVal > 0.1f
                            ? Math.Clamp(borderVal, 0.5f, 32f)
                            : 2.0f;
                        string apStream = BuildInkAppearanceStream(translatedStrokes, existingColor, bw);
                        SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
                    }
                }
                else
                {
                    IReadOnlyList<PdfCapturedPath>? captured = ReadAnnotCapturedPaths(annot);
                    if (captured is { Count: > 0 })
                    {
                        PdfRect oldBounds = new(rLeft, rBottom, rRight, rTop);
                        float bw = PdfiumNative.FPDFAnnot_GetBorder(annot, out _, out _, out float borderVal) != 0 && borderVal > 0.1f
                            ? Math.Clamp(borderVal, 0.5f, 32f)
                            : 2.0f;
                        string apStream = BuildCapturedPathsAppearanceStream(captured, oldBounds, newRect, bw, existingColor);
                        SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
                    }
                }
            }
            else if (subtype == PdfAnnotationSubtype.Text)
            {
                string apStream = BuildCommentIconAppearanceStream(newRect, existingColor);
                SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
            }
            else if (subtype is PdfAnnotationSubtype.Line or PdfAnnotationSubtype.Square or PdfAnnotationSubtype.Circle)
            {
                IReadOnlyList<PdfCapturedPath>? captured = ReadAnnotCapturedPaths(annot);
                if (captured is { Count: > 0 })
                {
                    PdfRect oldBounds = new(rLeft, rBottom, rRight, rTop);
                    float bw = PdfiumNative.FPDFAnnot_GetBorder(annot, out _, out _, out float borderVal) != 0 && borderVal > 0.1f
                        ? Math.Clamp(borderVal, 0.5f, 32f)
                        : 2.0f;
                    string apStream = BuildCapturedPathsAppearanceStream(captured, oldBounds, newRect, bw, existingColor);
                    SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
                }
                else if (subtype is PdfAnnotationSubtype.Square or PdfAnnotationSubtype.Circle)
                {
                    PdfAnnotationColor? interior = ReadAnnotInteriorColor(annot, subtype);
                    float bw = PdfiumNative.FPDFAnnot_GetBorder(annot, out _, out _, out float borderVal) != 0 && borderVal > 0.1f
                        ? Math.Clamp(borderVal, 0.5f, 32f)
                        : 2.0f;
                    string apStream = BuildShapeAppearanceStream(subtype, newRect, existingColor, interior, bw);
                    SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
                }
            }
        }

        IReadOnlyList<PdfAnnotationInfo> refreshed = ReadPageAnnotations(documentId, request.PageIndex, cancellationToken);
        return new PdfPageAnnotationMutationResult(request.PageIndex, request.AnnotIndex, refreshed);
    }

    /// <summary>
    /// Resizes an existing resizable annotation (<c>/Stamp</c>, <c>/Square</c>, <c>/Circle</c>) to new PDF user-space bounds.
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

            PdfAnnotationSubtype subtype = (PdfAnnotationSubtype)PdfiumNative.FPDFAnnot_GetSubtype(annot);
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

            if (subtype is PdfAnnotationSubtype.Square or PdfAnnotationSubtype.Circle)
            {
                PdfAnnotationColor strokeColor = ReadAnnotColor(annot, subtype);
                PdfAnnotationColor? interior = ReadAnnotInteriorColor(annot, subtype);
                float bw = PdfiumNative.FPDFAnnot_GetBorder(annot, out _, out _, out float borderVal) != 0 && borderVal > 0.1f
                    ? Math.Clamp(borderVal, 0.5f, 32f)
                    : 2.0f;
                string apStream = BuildShapeAppearanceStream(subtype, newRect, strokeColor, interior, bw);
                SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
            }
        }

        IReadOnlyList<PdfAnnotationInfo> refreshed = ReadPageAnnotations(documentId, request.PageIndex, cancellationToken);
        return new PdfPageAnnotationMutationResult(request.PageIndex, request.AnnotIndex, refreshed);
    }

    /// <summary>
    /// Creates a geometric shape annotation (Line, Arrow, Rectangle, or Ellipse) on <paramref name="request"/> page.
    /// </summary>
    internal PdfPageAnnotationMutationResult AddShapeAnnotation(
        long documentId,
        PdfCreateShapeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PdfiumDocumentHandle document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();

        PdfiumPageHandle page = _pageCache.GetOrLoad(document, documentId, request.PageIndex)
            ?? throw new PdfiumException(PdfiumErrorCode.Page, $"Page {request.PageIndex} could not be loaded.");

        Box pageBox = ReadVisiblePageBox(document, page, request.PageIndex);
        float borderW = Math.Clamp(request.StrokeWidthPt, 0.75f, 24f);
        float x1 = Math.Clamp(request.StartPdfX, pageBox.Left + 1f, pageBox.Right - 1f);
        float y1 = Math.Clamp(request.StartPdfY, pageBox.Bottom + 1f, pageBox.Top - 1f);
        float x2 = Math.Clamp(request.EndPdfX, pageBox.Left + 1f, pageBox.Right - 1f);
        float y2 = Math.Clamp(request.EndPdfY, pageBox.Bottom + 1f, pageBox.Top - 1f);
        string author = string.IsNullOrWhiteSpace(request.Author) ? Environment.UserName : request.Author;

        PdfAnnotationSubtype subtype = request.ShapeKind switch
        {
            PdfAnnotationShapeKind.Rectangle => PdfAnnotationSubtype.Square,
            PdfAnnotationShapeKind.Ellipse => PdfAnnotationSubtype.Circle,
            _ => PdfAnnotationSubtype.Ink,
        };

        string subject = request.ShapeKind switch
        {
            PdfAnnotationShapeKind.Arrow => "Arrow",
            PdfAnnotationShapeKind.Rectangle => "Rectangle",
            PdfAnnotationShapeKind.Ellipse => "Ellipse",
            _ => "Line",
        };

        FsRectF rect;
        string apStream;
        List<FsPointF[]>? inkStrokes = null;
        if (request.ShapeKind is PdfAnnotationShapeKind.Line or PdfAnnotationShapeKind.Arrow)
        {
            bool isArrow = request.ShapeKind == PdfAnnotationShapeKind.Arrow;
            float pad = isArrow ? Math.Max(14f, (borderW * 4.2f) + 8f) : Math.Max(4f, borderW * 1.5f);
            rect = new FsRectF
            {
                Left = Math.Max(pageBox.Left, Math.Min(x1, x2) - pad),
                Bottom = Math.Max(pageBox.Bottom, Math.Min(y1, y2) - pad),
                Right = Math.Min(pageBox.Right, Math.Max(x1, x2) + pad),
                Top = Math.Min(pageBox.Top, Math.Max(y1, y2) + pad),
            };
            inkStrokes = BuildLineOrArrowStrokes(isArrow, x1, y1, x2, y2, borderW);
            apStream = BuildInkAppearanceStream(inkStrokes, request.Color, borderW);
        }
        else
        {
            float pad = Math.Max(0.5f, borderW * 0.5f);
            float minX = Math.Min(x1, x2);
            float maxX = Math.Max(minX + 4f, Math.Max(x1, x2));
            float minY = Math.Min(y1, y2);
            float maxY = Math.Max(minY + 4f, Math.Max(y1, y2));
            rect = new FsRectF
            {
                Left = Math.Max(pageBox.Left, minX - pad),
                Bottom = Math.Max(pageBox.Bottom, minY - pad),
                Right = Math.Min(pageBox.Right, maxX + pad),
                Top = Math.Min(pageBox.Top, maxY + pad),
            };
            apStream = BuildShapeAppearanceStream(subtype, rect, request.Color, interiorColor: null, borderW);
        }

        int createdIndex;
        using (PdfiumAnnotationHandle annot = PdfiumNative.FPDFPage_CreateAnnot(page, (int)subtype))
        {
            if (annot.IsInvalid)
            {
                throw new PdfiumException(PdfiumErrorCode.Page, $"Failed to create {subtype} annotation.");
            }

            _ = PdfiumNative.FPDFAnnot_SetRect(annot, &rect);
            if (inkStrokes is not null)
            {
                foreach (FsPointF[] pts in inkStrokes)
                {
                    fixed (FsPointF* ptr = pts)
                    {
                        _ = PdfiumNative.FPDFAnnot_AddInkStroke(annot, ptr, (nuint)pts.Length);
                    }
                }
            }

            _ = PdfiumNative.FPDFAnnot_SetColor(
                annot,
                PdfiumConstants.AnnotColorTypeColor,
                request.Color.R,
                request.Color.G,
                request.Color.B,
                255);
            _ = PdfiumNative.FPDFAnnot_SetBorder(annot, 0f, 0f, borderW);
            _ = PdfiumNative.FPDFAnnot_SetFlags(annot, PdfiumConstants.AnnotFlagPrint);
            SetAnnotString(annot, "T", author);
            SetAnnotString(annot, "M", PdfSignatureInspector.FormatPdfDate(DateTimeOffset.Now));
            SetAnnotString(annot, "Subj", subject);
            SetAnnotApStream(annot, PdfiumConstants.AnnotAppearanceNormal, apStream);
            createdIndex = PdfiumNative.FPDFPage_GetAnnotIndex(page, annot);
        }

        IReadOnlyList<PdfAnnotationInfo> refreshed = ReadPageAnnotations(documentId, request.PageIndex, cancellationToken);
        return new PdfPageAnnotationMutationResult(request.PageIndex, createdIndex, refreshed);
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
            if (!string.IsNullOrWhiteSpace(source.Subject))
            {
                SetAnnotString(annot, "Subj", source.Subject);
            }
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
                    if (PdfiumNative.FPDFAnnot_HasKey(annot, "IC") != 0 &&
                        PdfiumNative.FPDFAnnot_GetColor(annot, PdfiumConstants.AnnotColorTypeInterior, out uint ir, out uint ig, out uint ib, out uint ia) != 0 && ia > 0)
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

        if (PdfiumNative.FPDFAnnot_HasKey(annot, "IC") != 0 &&
            PdfiumNative.FPDFAnnot_GetColor(annot, PdfiumConstants.AnnotColorTypeInterior, out uint ir, out uint ig, out uint ib, out uint ia) != 0 && ia > 0)
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

    private static PdfAnnotationColor? ReadAnnotInteriorColor(PdfiumAnnotationHandle annot, PdfAnnotationSubtype subtype)
    {
        if (PdfiumNative.FPDFAnnot_HasKey(annot, "IC") != 0 &&
            PdfiumNative.FPDFAnnot_GetColor(annot, PdfiumConstants.AnnotColorTypeInterior, out uint ir, out uint ig, out uint ib, out uint ia) != 0 &&
            ia > 0)
        {
            return new PdfAnnotationColor(
                (byte)Math.Clamp(ir, 0u, 255u),
                (byte)Math.Clamp(ig, 0u, 255u),
                (byte)Math.Clamp(ib, 0u, 255u),
                (byte)Math.Clamp(ia, 0u, 255u));
        }

        if (subtype is PdfAnnotationSubtype.Square or PdfAnnotationSubtype.Circle or PdfAnnotationSubtype.Polygon)
        {
            IReadOnlyList<PdfCapturedPath>? captured = ReadAnnotCapturedPaths(annot);
            if (captured is { Count: > 0 })
            {
                for (int i = 0; i < captured.Count; i++)
                {
                    PdfCapturedPath path = captured[i];
                    if (path.FillMode != 0 && path.FillColor is { A: > 0 } fc)
                    {
                        return fc;
                    }
                }
            }
        }

        return null;
    }

    private static List<FsPointF[]> BuildLineOrArrowStrokes(
        bool isArrow,
        float x1,
        float y1,
        float x2,
        float y2,
        float borderWidthPt)
    {
        List<FsPointF[]> strokes =
        [
            [
                new FsPointF { X = x1, Y = y1 },
                new FsPointF { X = x2, Y = y2 },
            ]
        ];

        if (isArrow)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            float len = MathF.Sqrt((dx * dx) + (dy * dy));
            if (len > 1.0f)
            {
                float ux = dx / len;
                float uy = dy / len;
                float headLen = Math.Min(len * 0.45f, Math.Clamp((borderWidthPt * 4.2f) + 6.0f, 9.0f, 34.0f));
                const float cosA = 0.8829476f; // cos(28 deg)
                const float sinA = 0.4694716f; // sin(28 deg)
                float w1x = x2 - (headLen * ((ux * cosA) - (uy * sinA)));
                float w1y = y2 - (headLen * ((ux * sinA) + (uy * cosA)));
                float w2x = x2 - (headLen * ((ux * cosA) + (uy * sinA)));
                float w2y = y2 - (headLen * (-(ux * sinA) + (uy * cosA)));
                strokes.Add(
                [
                    new FsPointF { X = w1x, Y = w1y },
                    new FsPointF { X = x2, Y = y2 },
                    new FsPointF { X = w2x, Y = w2y },
                ]);
            }
        }

        return strokes;
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
        PdfAnnotationColor? fallbackColor = null,
        PdfAnnotationColor? overrideColor = null)
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
                PdfAnnotationColor sc = overrideColor ?? path.StrokeColor ?? fallbackColor ?? PdfAnnotationColor.Blue;
                float sw = Math.Clamp(path.StrokeWidthPt * scaleStroke, 0.35f, 32f);
                sb.Append(CultureInfo.InvariantCulture, $"{sw:F2} w {(sc.R / 255.0):F3} {(sc.G / 255.0):F3} {(sc.B / 255.0):F3} RG ");
            }

            if (path.FillMode != 0)
            {
                PdfAnnotationColor fc = overrideColor ?? path.FillColor ?? path.StrokeColor ?? fallbackColor ?? PdfAnnotationColor.Blue;
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

}
