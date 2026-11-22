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
    public static readonly PdfAnnotationColor Yellow = new(255, 226, 74, 255);

    /// <summary>Preset mint-green highlight/note color.</summary>
    public static readonly PdfAnnotationColor Green = new(118, 224, 138, 255);

    /// <summary>Preset sky-blue highlight/note color.</summary>
    public static readonly PdfAnnotationColor Blue = new(112, 194, 255, 255);

    /// <summary>Preset rose-pink highlight/note color.</summary>
    public static readonly PdfAnnotationColor Pink = new(255, 148, 200, 255);

    /// <summary>Preset warm-orange highlight/note color.</summary>
    public static readonly PdfAnnotationColor Orange = new(255, 178, 92, 255);
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
    int SignatureIndex = -1)
{
    /// <summary>Gets a value indicating whether this annotation carries a non-empty comment or text body.</summary>
    public bool HasComment => !string.IsNullOrWhiteSpace(Contents);

    /// <summary>Gets a value indicating whether this annotation is a text markup type (highlight, underline, squiggly, strikeout).</summary>
    public bool IsTextMarkup => Subtype is PdfAnnotationSubtype.Highlight or PdfAnnotationSubtype.Underline or PdfAnnotationSubtype.Squiggly or PdfAnnotationSubtype.StrikeOut;

    /// <summary>Gets a value indicating whether the user can recolor this annotation directly.</summary>
    public bool SupportsColorChange => IsTextMarkup || Subtype is PdfAnnotationSubtype.Text or PdfAnnotationSubtype.FreeText;

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
public sealed record PdfCreateMarkupRequest(
    int PageIndex,
    PdfAnnotationSubtype Subtype,
    IReadOnlyList<PdfRect> Rects,
    PdfAnnotationColor Color,
    string Contents,
    string Author,
    string Subject = "");

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
public sealed record PdfCreateFreeTextRequest(
    int PageIndex,
    double PdfX,
    double PdfY,
    PdfAnnotationColor Color,
    string Contents,
    string Author,
    double MaxWidthPt = 240.0);

/// <summary>
/// Request to update the comment body and optional color of an existing annotation.
/// </summary>
/// <param name="PageIndex">Zero-based page index.</param>
/// <param name="AnnotIndex">Zero-based index in the page's annotation list.</param>
/// <param name="Contents">Updated text body.</param>
/// <param name="NewColor">Optional updated RGBA color.</param>
public sealed record PdfUpdateAnnotationRequest(
    int PageIndex,
    int AnnotIndex,
    string Contents,
    PdfAnnotationColor? NewColor = null);

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
