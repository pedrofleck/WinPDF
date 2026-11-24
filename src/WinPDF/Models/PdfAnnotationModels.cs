namespace WinPDF.Models;

/// <summary>
/// Annotation subtypes defined by PDFium (<c>fpdf_annot.h</c>).
/// </summary>
public enum PdfAnnotationSubtype
{
    /// <summary>Unknown or unsupported subtype.</summary>
    Unknown = 0,

    /// <summary>Sticky note / text comment icon (<c>/Text</c>).</summary>
    Text = 1,

    /// <summary>Hyperlink annotation (<c>/Link</c>).</summary>
    Link = 2,

    /// <summary>Direct-on-page text box annotation (<c>/FreeText</c>).</summary>
    FreeText = 3,

    /// <summary>Line drawing annotation (<c>/Line</c>).</summary>
    Line = 4,

    /// <summary>Rectangle shape annotation (<c>/Square</c>).</summary>
    Square = 5,

    /// <summary>Circle or ellipse annotation (<c>/Circle</c>).</summary>
    Circle = 6,

    /// <summary>Closed polygon annotation (<c>/Polygon</c>).</summary>
    Polygon = 7,

    /// <summary>Open polyline annotation (<c>/PolyLine</c>).</summary>
    PolyLine = 8,

    /// <summary>Text highlight markup annotation (<c>/Highlight</c>).</summary>
    Highlight = 9,

    /// <summary>Text underline markup annotation (<c>/Underline</c>).</summary>
    Underline = 10,

    /// <summary>Text squiggly underline markup annotation (<c>/Squiggly</c>).</summary>
    Squiggly = 11,

    /// <summary>Text strikeout markup annotation (<c>/StrikeOut</c>).</summary>
    StrikeOut = 12,

    /// <summary>Rubber stamp annotation (<c>/Stamp</c>).</summary>
    Stamp = 13,

    /// <summary>Text insertion caret annotation (<c>/Caret</c>).</summary>
    Caret = 14,

    /// <summary>Freehand ink drawing annotation (<c>/Ink</c>).</summary>
    Ink = 15,

    /// <summary>Linked popup note window (<c>/Popup</c>).</summary>
    Popup = 16,

    /// <summary>Embedded file attachment annotation (<c>/FileAttachment</c>).</summary>
    FileAttachment = 17,

    /// <summary>Sound annotation (<c>/Sound</c>).</summary>
    Sound = 18,

    /// <summary>Movie annotation (<c>/Movie</c>).</summary>
    Movie = 19,

    /// <summary>Interactive form or signature field widget (<c>/Widget</c>).</summary>
    Widget = 20,

    /// <summary>Screen multimedia annotation (<c>/Screen</c>).</summary>
    Screen = 21,

    /// <summary>Printer's mark annotation (<c>/PrinterMark</c>).</summary>
    PrinterMark = 22,

    /// <summary>Trap network annotation (<c>/TrapNet</c>).</summary>
    TrapNet = 23,

    /// <summary>Watermark annotation (<c>/Watermark</c>).</summary>
    Watermark = 24,

    /// <summary>3D artwork annotation (<c>/3D</c>).</summary>
    ThreeD = 25,

    /// <summary>Rich media annotation (<c>/RichMedia</c>).</summary>
    RichMedia = 26,

    /// <summary>XFA widget annotation.</summary>
    XfaWidget = 27,

    /// <summary>Redaction markup annotation (<c>/Redact</c>).</summary>
    Redact = 28,
}

/// <summary>
/// Represents an RGBA annotation color stored in PDFium (<c>0..255</c> per channel).
/// </summary>
/// <param name="R">Red channel.</param>
/// <param name="G">Green channel.</param>
/// <param name="B">Blue channel.</param>
/// <param name="A">Alpha channel (opacity).</param>
public readonly record struct PdfAnnotationColor(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Preset yellow highlight/note color.</summary>
    public static readonly PdfAnnotationColor Yellow = new(255, 230, 32, 255);

    /// <summary>Preset mint-green highlight/note color.</summary>
    public static readonly PdfAnnotationColor Green = new(118, 224, 138, 255);

    /// <summary>Preset sky-blue highlight/note color.</summary>
    public static readonly PdfAnnotationColor Blue = new(112, 194, 255, 255);

    /// <summary>Preset rose-pink highlight/note color.</summary>
    public static readonly PdfAnnotationColor Pink = new(255, 148, 200, 255);

    /// <summary>Preset warm-orange highlight/note color.</summary>
    public static readonly PdfAnnotationColor Orange = new(255, 178, 92, 255);

    /// <summary>Preset crimson-red highlight/note color.</summary>
    public static readonly PdfAnnotationColor Red = new(242, 76, 76, 255);
}

/// <summary>
/// Captured vector path segment from a PDF annotation appearance stream (<c>/AP</c>).
/// </summary>
/// <param name="Type">Segment type: <c>0</c> = LineTo, <c>1</c> = BezierTo, <c>2</c> = MoveTo.</param>
/// <param name="X">X coordinate in page user space (points).</param>
/// <param name="Y">Y coordinate in page user space (points).</param>
/// <param name="IsClosed">Whether the subpath is closed after this segment.</param>
public readonly record struct PdfCapturedPathSegment(
    byte Type,
    float X,
    float Y,
    bool IsClosed);

/// <summary>
/// Captured raster image payload (BGRA8888) extracted from or destined for a PDF <c>/Stamp</c> annotation.
/// </summary>
/// <param name="PixelWidth">Image width in pixels.</param>
/// <param name="PixelHeight">Image height in pixels.</param>
/// <param name="BgraPixels">Tightly packed BGRA8888 pixel bytes (<c>PixelWidth * PixelHeight * 4</c>).</param>
/// <param name="Subject">Optional image/stamp subject label.</param>
public sealed record PdfCapturedImage(
    int PixelWidth,
    int PixelHeight,
    byte[] BgraPixels,
    string Subject = "");

/// <summary>
/// Captured vector path object from an annotation appearance stream so ink strokes and arbitrary shapes can be pasted faithfully.
/// </summary>
/// <param name="Segments">Ordered path segments in page user space (points).</param>
/// <param name="StrokeColor">Stroke RGBA color, if stroked.</param>
/// <param name="FillColor">Fill RGBA color, if filled.</param>
/// <param name="StrokeWidthPt">Stroke width in PDF points.</param>
/// <param name="FillMode">PDFium fill mode (<c>0</c> = none, <c>1</c> = alternate/even-odd, <c>2</c> = winding).</param>
/// <param name="HasStroke">Whether the path is stroked.</param>
public sealed record PdfCapturedPath(
    IReadOnlyList<PdfCapturedPathSegment> Segments,
    PdfAnnotationColor? StrokeColor,
    PdfAnnotationColor? FillColor,
    float StrokeWidthPt,
    int FillMode,
    bool HasStroke);

/// <summary>
/// Formatting and layout metadata for an inline vector text box annotation (<c>/WinPDFStyle</c>),
/// rendered using the universal PDF Standard-14 <c>Arial / Helvetica</c> family.
/// </summary>
/// <param name="FontFamily">Font family name (normalized to universal <c>Arial</c> / <c>Helvetica</c>).</param>
/// <param name="FontSizePt">Font size in PDF points.</param>
/// <param name="IsBold">Whether bold weight is enabled.</param>
/// <param name="IsItalic">Whether italic style is enabled.</param>
/// <param name="IsUnderline">Whether underline decoration is enabled.</param>
/// <param name="Color">Text RGBA color.</param>
/// <param name="MaxWidthPt">Maximum box width in PDF points before word-wrapping.</param>
public sealed record PdfFreeTextStyle(
    string FontFamily,
    float FontSizePt,
    bool IsBold,
    bool IsItalic,
    bool IsUnderline,
    PdfAnnotationColor Color,
    double MaxWidthPt = 280.0)
{
    /// <summary>Default dark text color (#FF141414) for new inline text boxes.</summary>
    public static readonly PdfAnnotationColor DefaultTextColor = new(20, 20, 20, 255);

    /// <summary>Default inline text box formatting style.</summary>
    public static PdfFreeTextStyle Default => new("Arial", 12.0f, false, false, false, DefaultTextColor, 280.0);

    /// <summary>
    /// Serializes this style into a compact invariant string for storage in the PDF annotation dictionary.
    /// </summary>
    /// <returns>The serialized style string.</returns>
    public string Serialize()
    {
        string safeFont = (FontFamily ?? string.Empty).Replace('|', ' ').Trim();
        if (string.IsNullOrEmpty(safeFont))
        {
            safeFont = "Arial";
        }

        float size = Math.Clamp(FontSizePt, 6f, 144f);
        double maxW = Math.Clamp(MaxWidthPt, 60.0, 1200.0);
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"v1|{safeFont}|{size:F2}|{(IsBold ? 1 : 0)}|{(IsItalic ? 1 : 0)}|{(IsUnderline ? 1 : 0)}|{Color.R},{Color.G},{Color.B},{Color.A}|{maxW:F2}");
    }

    /// <summary>
    /// Parses a serialized <see cref="PdfFreeTextStyle"/> string from a PDF annotation dictionary.
    /// </summary>
    /// <param name="raw">Raw serialized string value.</param>
    /// <param name="style">Parsed style when successful; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(string? raw, out PdfFreeTextStyle? style)
    {
        style = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string[] parts = raw.Split('|');
        if (parts.Length < 7 || !string.Equals(parts[0], "v1", StringComparison.Ordinal))
        {
            return false;
        }

        string fontFamily = parts[1].Trim();
        if (string.IsNullOrEmpty(fontFamily))
        {
            fontFamily = "Arial";
        }

        if (!float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sizePt))
        {
            return false;
        }

        bool isBold = parts[3] == "1";
        bool isItalic = parts[4] == "1";
        bool isUnderline = parts[5] == "1";

        string[] rgba = parts[6].Split(',');
        if (rgba.Length < 3 ||
            !byte.TryParse(rgba[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out byte r) ||
            !byte.TryParse(rgba[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(rgba[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out byte b))
        {
            return false;
        }

        byte a = 255;
        if (rgba.Length >= 4 && byte.TryParse(rgba[3], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out byte parsedA))
        {
            a = parsedA;
        }

        double maxWidth = 280.0;
        if (parts.Length >= 8 &&
            double.TryParse(parts[7], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedMaxW) &&
            double.IsFinite(parsedMaxW))
        {
            maxWidth = Math.Clamp(parsedMaxW, 60.0, 1200.0);
        }

        style = new PdfFreeTextStyle(
            fontFamily,
            Math.Clamp(sizePt, 6f, 144f),
            isBold,
            isItalic,
            isUnderline,
            new PdfAnnotationColor(r, g, b, a),
            maxWidth);
        return true;
    }
}

/// <summary>
/// Immutable snapshot of a PDF annotation read from a page dictionary.
/// </summary>
/// <param name="PageIndex">Zero-based page index.</param>
/// <param name="AnnotIndex">Zero-based index in the page's annotation array.</param>
/// <param name="Subtype">Annotation subtype (<see cref="PdfAnnotationSubtype"/>).</param>
/// <param name="Bounds">Bounding box in PDF user space (points).</param>
/// <param name="QuadRects">Normalized rectangles extracted from attachment QuadPoints (when present).</param>
/// <param name="Contents">Annotation text / comment body (<c>/Contents</c>).</param>
/// <param name="Author">Author name (<c>/T</c>).</param>
/// <param name="ModifiedDateRaw">Raw PDF modification date string (<c>/M</c>).</param>
/// <param name="ModifiedDate">Parsed modification timestamp, if valid.</param>
/// <param name="Color">Primary annotation RGBA color.</param>
/// <param name="Flags">Raw PDF annotation flags bitmask.</param>
/// <param name="Subject">Optional annotation subject (<c>/Subj</c>).</param>
/// <param name="QuotedText">Optional quoted document text covered by a text markup annotation.</param>
/// <param name="IsSignatureWidget">Whether this widget hosts a digital signature appearance stamp.</param>
/// <param name="SignatureIndex">Matched index into the document signature list, or -1 if none.</param>
/// <param name="InteriorColor">Optional interior fill color (<c>/IC</c>) for shape annotations.</param>
/// <param name="BorderWidthPt">Stroke / border width in PDF points.</param>
/// <param name="Strokes">Optional vector stroke paths for ink, line, polygon, or polyline annotations.</param>
/// <param name="CapturedPaths">Optional captured vector paths from the annotation's appearance stream (<c>/AP</c>).</param>
/// <param name="CapturedImage">Optional captured raster image payload for <c>/Stamp</c> annotations.</param>
/// <param name="TextStyle">Optional inline text formatting metadata when this annotation is a WinPDF text box.</param>
public sealed record PdfAnnotationInfo(
    int PageIndex,
    int AnnotIndex,
    PdfAnnotationSubtype Subtype,
    PdfRect Bounds,
    IReadOnlyList<PdfRect> QuadRects,
    string Contents,
    string Author,
    string ModifiedDateRaw,
    DateTimeOffset? ModifiedDate,
    PdfAnnotationColor Color,
    int Flags,
    string Subject,
    string QuotedText,
    bool IsSignatureWidget = false,
    int SignatureIndex = -1,
    PdfAnnotationColor? InteriorColor = null,
    float BorderWidthPt = 2.0f,
    IReadOnlyList<IReadOnlyList<System.Numerics.Vector2>>? Strokes = null,
    IReadOnlyList<PdfCapturedPath>? CapturedPaths = null,
    PdfCapturedImage? CapturedImage = null,
    PdfFreeTextStyle? TextStyle = null)
{
    /// <summary>Gets a value indicating whether this annotation carries a non-empty comment or text body.</summary>
    public bool HasComment => !string.IsNullOrWhiteSpace(Contents);

    /// <summary>Gets a value indicating whether this annotation represents a direct-on-page text box.</summary>
    public bool IsTextBox => Subtype == PdfAnnotationSubtype.FreeText || TextStyle is not null;

    /// <summary>Gets a value indicating whether this annotation is a text markup type (highlight, underline, squiggly, strikeout).</summary>
    public bool IsTextMarkup => Subtype is PdfAnnotationSubtype.Highlight or PdfAnnotationSubtype.Underline or PdfAnnotationSubtype.Squiggly or PdfAnnotationSubtype.StrikeOut;

    /// <summary>Gets a value indicating whether the user can recolor this annotation directly.</summary>
    public bool SupportsColorChange => IsTextMarkup || Subtype is PdfAnnotationSubtype.Text or PdfAnnotationSubtype.FreeText || TextStyle is not null;

    /// <summary>Gets a value indicating whether this annotation can be dragged to a new position on the page.</summary>
    public bool IsMovable => !IsSignatureWidget && !IsTextMarkup;

    /// <summary>Gets a value indicating whether this annotation can be copied, cut, or pasted.</summary>
    public bool CanCopyOrCut => !IsSignatureWidget && !IsTextMarkup;

    /// <summary>Gets a value indicating whether this annotation supports interactive corner-handle resizing.</summary>
    public bool IsResizable => Subtype == PdfAnnotationSubtype.Stamp && !IsSignatureWidget && TextStyle is null;

    /// <summary>
    /// Determines whether the specified PDF user-space point lies inside this annotation's hit area.
    /// </summary>
    /// <param name="pdfX">X coordinate in PDF points.</param>
    /// <param name="pdfY">Y coordinate in PDF points.</param>
    /// <param name="tolerancePt">Hit-test padding in PDF points.</param>
    /// <returns><see langword="true"/> when inside any quad rect or bounding box.</returns>
    public bool ContainsPoint(double pdfX, double pdfY, double tolerancePt = 4.0)
    {
        if (QuadRects is { Count: > 0 })
        {
            for (int i = 0; i < QuadRects.Count; i++)
            {
                if (QuadRects[i].Contains(pdfX, pdfY, tolerancePt))
                {
                    return true;
                }
            }

            return false;
        }

        return Bounds.Contains(pdfX, pdfY, tolerancePt);
    }
}

/// <summary>
/// Verification outcome of an embedded PKCS#7 / CAdES digital signature.
/// </summary>
public enum PdfSignatureVerificationStatus
{
    /// <summary>Document bytes match the signed digest, covers the full file, and the certificate chain is trusted by Windows.</summary>
    Valid,

    /// <summary>Cryptographic digest and signature are intact, but the issuing root CA is not installed in the Windows Trusted Root store.</summary>
    ValidUntrustedRoot,

    /// <summary>The signature verifies over its byte range, but incremental updates were appended after signing.</summary>
    ModifiedAfterSigning,

    /// <summary>Digest mismatch, tampered bytes, or invalid cryptographic signature.</summary>
    Invalid,

    /// <summary>Unsupported or unparseable signature payload.</summary>
    Unknown,
}

/// <summary>
/// Detailed metadata and cryptographic verification result for a PDF digital signature.
/// </summary>
/// <param name="Index">Zero-based signature index in the document.</param>
/// <param name="SignerName">Common name (CN) of the signing certificate.</param>
/// <param name="SignerDistinguishedName">Full X.500 subject distinguished name.</param>
/// <param name="IssuerName">Certificate Authority issuer name.</param>
/// <param name="SigningTime">Verified or declared signing timestamp.</param>
/// <param name="Reason">Optional signing reason (<c>/Reason</c>).</param>
/// <param name="SubFilter">PDF signature SubFilter encoding (e.g. <c>ETSI.CAdES.detached</c>).</param>
/// <param name="DocMdpPermission">DocMDP permission level (0 = unspecified, 1 = no changes, 2 = form fill/sign, 3 = annotations/form fill).</param>
/// <param name="CoversWholeDocument">Whether the signature byte range spans to the end of the file.</param>
/// <param name="IsCryptographicallyValid">Whether the message digest and CMS signature verified mathematically.</param>
/// <param name="IsCertificateChainTrusted">Whether Windows trusted the certificate chain offline.</param>
/// <param name="Status">Overall verification classification.</param>
/// <param name="StatusDetails">Diagnostic summary of the verification outcome.</param>
/// <param name="CertNotBefore">Certificate validity start timestamp.</param>
/// <param name="CertNotAfter">Certificate validity end timestamp.</param>
/// <param name="CertThumbprint">SHA-256 thumbprint of the signer certificate.</param>
/// <param name="CertSerialNumber">Hexadecimal certificate serial number.</param>
/// <param name="PageIndex">Zero-based page index of the visible signature stamp, or -1 if invisible.</param>
/// <param name="WidgetBounds">Visible signature stamp bounds in PDF points, if present.</param>
public sealed record PdfSignatureInfo(
    int Index,
    string SignerName,
    string SignerDistinguishedName,
    string IssuerName,
    DateTimeOffset? SigningTime,
    string Reason,
    string SubFilter,
    uint DocMdpPermission,
    bool CoversWholeDocument,
    bool IsCryptographicallyValid,
    bool IsCertificateChainTrusted,
    PdfSignatureVerificationStatus Status,
    string StatusDetails,
    DateTimeOffset? CertNotBefore,
    DateTimeOffset? CertNotAfter,
    string CertThumbprint,
    string CertSerialNumber,
    int PageIndex = -1,
    PdfRect? WidgetBounds = null);

/// <summary>
/// Request to create a text markup annotation (Highlight, Underline, StrikeOut, Squiggly) on a single page.
/// </summary>
/// <param name="PageIndex">Zero-based target page index.</param>
/// <param name="Subtype">Markup subtype.</param>
/// <param name="Rects">Character bounding rectangles in PDF user space.</param>
/// <param name="Color">Annotation color.</param>
/// <param name="Contents">Optional comment body.</param>
/// <param name="Author">Author display name.</param>
/// <param name="Subject">Optional subject label.</param>
/// <param name="SkipPadding">Whether to use <paramref name="Rects"/> verbatim without applying extra visual padding.</param>
public sealed record PdfCreateMarkupRequest(
    int PageIndex,
    PdfAnnotationSubtype Subtype,
    IReadOnlyList<PdfRect> Rects,
    PdfAnnotationColor Color,
    string Contents,
    string Author,
    string Subject = "",
    bool SkipPadding = false);

/// <summary>
/// Request to create a sticky-note comment annotation (<c>/Text</c>) at a point in PDF space.
/// </summary>
/// <param name="PageIndex">Zero-based target page index.</param>
/// <param name="PdfX">Anchor X coordinate in PDF points.</param>
/// <param name="PdfY">Anchor Y coordinate in PDF points.</param>
/// <param name="Color">Note icon color.</param>
/// <param name="Contents">Note text body.</param>
/// <param name="Author">Author display name.</param>
public sealed record PdfCreateNoteRequest(
    int PageIndex,
    double PdfX,
    double PdfY,
    PdfAnnotationColor Color,
    string Contents,
    string Author);

/// <summary>
/// Request to create a visible <c>/FreeText</c> text-box annotation on a page.
/// </summary>
/// <param name="PageIndex">Zero-based target page index.</param>
/// <param name="PdfX">Top-left X coordinate in PDF points.</param>
/// <param name="PdfY">Top-left Y coordinate in PDF points.</param>
/// <param name="Color">Accent / background tint color.</param>
/// <param name="Contents">Text box body.</param>
/// <param name="Author">Author display name.</param>
/// <param name="MaxWidthPt">Maximum box width in PDF points before word-wrapping.</param>
/// <param name="Style">Optional explicit text formatting metadata.</param>
public sealed record PdfCreateFreeTextRequest(
    int PageIndex,
    double PdfX,
    double PdfY,
    PdfAnnotationColor Color,
    string Contents,
    string Author,
    double MaxWidthPt = 240.0,
    PdfFreeTextStyle? Style = null);

/// <summary>
/// Request to insert a raster image onto a PDF page as a <c>/Stamp</c> annotation.
/// </summary>
/// <param name="PageIndex">Zero-based target page index.</param>
/// <param name="PdfX">Anchor top-left X coordinate in PDF points.</param>
/// <param name="PdfY">Anchor top-left Y coordinate in PDF points.</param>
/// <param name="PixelWidth">Image width in pixels.</param>
/// <param name="PixelHeight">Image height in pixels.</param>
/// <param name="BgraPixels">Tightly packed BGRA8888 pixel bytes.</param>
/// <param name="Author">Author display name.</param>
/// <param name="Subject">Optional subject label.</param>
/// <param name="ExplicitWidthPt">Optional explicit width in PDF points (used when pasting/undoing).</param>
/// <param name="ExplicitHeightPt">Optional explicit height in PDF points (used when pasting/undoing).</param>
public sealed record PdfCreateImageRequest(
    int PageIndex,
    double PdfX,
    double PdfY,
    int PixelWidth,
    int PixelHeight,
    byte[] BgraPixels,
    string Author,
    string Subject = "",
    double? ExplicitWidthPt = null,
    double? ExplicitHeightPt = null);

/// <summary>
/// Request to update the comment body and optional color of an existing annotation.
/// </summary>
/// <param name="PageIndex">Zero-based page index.</param>
/// <param name="AnnotIndex">Zero-based index in the page's annotation list.</param>
/// <param name="Contents">Updated text body.</param>
/// <param name="NewColor">Optional updated RGBA color.</param>
/// <param name="NewTextStyle">Optional updated inline text formatting metadata.</param>
public sealed record PdfUpdateAnnotationRequest(
    int PageIndex,
    int AnnotIndex,
    string Contents,
    PdfAnnotationColor? NewColor = null,
    PdfFreeTextStyle? NewTextStyle = null);

/// <summary>
/// Request to translate a movable annotation (<c>FreeText</c>, <c>Text</c>, <c>Ink</c>, shapes, etc.) by a PDF-space delta.
/// </summary>
/// <param name="PageIndex">Zero-based page index.</param>
/// <param name="AnnotIndex">Zero-based index in the page's annotation list.</param>
/// <param name="DeltaPdfX">Horizontal translation in PDF points.</param>
/// <param name="DeltaPdfY">Vertical translation in PDF points.</param>
public sealed record PdfMoveAnnotationRequest(
    int PageIndex,
    int AnnotIndex,
    double DeltaPdfX,
    double DeltaPdfY);

/// <summary>
/// Request to resize an existing resizable annotation (<c>Stamp</c>) to new PDF user-space bounds.
/// </summary>
/// <param name="PageIndex">Zero-based page index.</param>
/// <param name="AnnotIndex">Zero-based index in the page's annotation list.</param>
/// <param name="NewBounds">Target bounding rectangle in PDF user space (points).</param>
public sealed record PdfResizeAnnotationRequest(
    int PageIndex,
    int AnnotIndex,
    PdfRect NewBounds);

/// <summary>
/// Request to paste a non-markup annotation snapshot (comment note, text box, ink drawing, or shape) onto a target page.
/// </summary>
/// <param name="Source">Source annotation snapshot.</param>
/// <param name="TargetPageIndex">Destination zero-based page index.</param>
/// <param name="TargetPdfX">Target X coordinate in PDF user space (points).</param>
/// <param name="TargetPdfY">Target Y coordinate in PDF user space (points).</param>
public sealed record PdfPasteAnnotationRequest(
    PdfAnnotationInfo Source,
    int TargetPageIndex,
    double TargetPdfX,
    double TargetPdfY);

/// <summary>
/// Result returned after creating or updating an annotation, carrying refreshed page annotations and target index.
/// </summary>
/// <param name="PageIndex">Zero-based page index.</param>
/// <param name="TargetAnnotIndex">Index of the newly created or updated annotation.</param>
/// <param name="Annotations">Complete refreshed list of annotations on the page.</param>
public sealed record PdfPageAnnotationMutationResult(
    int PageIndex,
    int TargetAnnotIndex,
    IReadOnlyList<PdfAnnotationInfo> Annotations);
