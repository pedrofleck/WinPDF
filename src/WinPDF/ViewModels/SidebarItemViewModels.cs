using CommunityToolkit.Mvvm.ComponentModel;
using WinPDF.Converters;
using WinPDF.Helpers;
using WinPDF.Models;

namespace WinPDF.ViewModels;

/// <summary>
/// View model representing a bookmarked page in the sidebar.
/// </summary>
public sealed partial class BookmarkItemViewModel : ObservableObject
{
    /// <summary>
    /// Gets the zero-based page index.
    /// </summary>
    public int PageIndex { get; }

    /// <summary>
    /// Gets the 1-based page number.
    /// </summary>
    public int PageNumber => PageIndex + 1;

    /// <summary>
    /// Gets or sets the user-defined custom bookmark name. When empty, <see cref="DisplayText"/> falls back to the page number label.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(HasCustomTitle))]
    [NotifyPropertyChangedFor(nameof(PageBadgeText))]
    public partial string CustomTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this bookmark item is currently being renamed inline.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisplayMode))]
    public partial bool IsEditing { get; set; }

    /// <summary>
    /// Gets a value indicating whether this bookmark item is in normal display mode (not being edited).
    /// </summary>
    public bool IsDisplayMode => !IsEditing;

    /// <summary>
    /// Gets or sets the temporary text bound to the inline rename TextBox while <see cref="IsEditing"/> is true.
    /// </summary>
    [ObservableProperty]
    public partial string EditingText { get; set; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether a non-empty custom name has been assigned to this bookmark.
    /// </summary>
    public bool HasCustomTitle => !string.IsNullOrWhiteSpace(CustomTitle);

    /// <summary>
    /// Gets the primary label displayed for this bookmark (custom name if set; otherwise localized "Page X").
    /// </summary>
    public string DisplayText =>
        HasCustomTitle ? CustomTitle.Trim() : UiHelpers.FormatBookmarkPage(PageNumber);

    /// <summary>
    /// Gets the localized page label ("Page X" / "Página X") used for tooltips.
    /// </summary>
    public string PageBadgeText => UiHelpers.FormatBookmarkPage(PageNumber);

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkItemViewModel"/> class.
    /// </summary>
    /// <param name="pageIndex">Zero-based index of the page.</param>
    /// <param name="customTitle">Optional persisted custom bookmark name.</param>
    public BookmarkItemViewModel(int pageIndex, string? customTitle = null)
    {
        PageIndex = pageIndex;
        if (!string.IsNullOrWhiteSpace(customTitle))
        {
            CustomTitle = customTitle.Trim();
        }
    }
}

/// <summary>
/// View model representing a document annotation item in the sidebar.
/// </summary>
public sealed partial class AnnotationItemViewModel : ObservableObject
{
    /// <summary>Gets the underlying annotation model.</summary>
    public PdfAnnotationInfo Info { get; private set; }

    /// <summary>Gets the zero-based page index.</summary>
    public int PageIndex => Info.PageIndex;

    /// <summary>Gets the 1-based page number.</summary>
    public int PageNumber => Info.PageIndex + 1;

    /// <summary>Gets the zero-based annotation index on its page.</summary>
    public int AnnotIndex => Info.AnnotIndex;

    /// <summary>Gets the annotation subtype.</summary>
    public PdfAnnotationSubtype Subtype => Info.Subtype;

    /// <summary>Gets the localized subtype title (e.g. Highlight, Comment, Text Box, Drawing).</summary>
    public string TitleText { get; private set; }

    /// <summary>Gets the annotation comment body or quoted document text snippet.</summary>
    public string Text { get; private set; }

    /// <summary>Gets a value indicating whether there is non-empty body or quoted text to display.</summary>
    public bool HasBodyText => !string.IsNullOrWhiteSpace(Text);

    /// <summary>Gets the annotation author display string.</summary>
    public string Author => Info.Author;

    /// <summary>Gets a value indicating whether author information is present.</summary>
    public bool HasAuthor => !string.IsNullOrWhiteSpace(Info.Author);

    /// <summary>Gets the formatted local modification timestamp.</summary>
    public string FormattedDate { get; private set; }

    /// <summary>Gets a value indicating whether a valid timestamp string is available.</summary>
    public bool HasDate => !string.IsNullOrWhiteSpace(FormattedDate);

    /// <summary>Gets the localized page label (e.g. "Page 3").</summary>
    public string PageBadgeText => UiHelpers.FormatBookmarkPage(PageNumber);

    /// <summary>Gets the Segoe Fluent icon glyph representing this annotation subtype.</summary>
    public string IconGlyph { get; private set; }

    /// <summary>Gets the opaque swatch color of this annotation.</summary>
    public Windows.UI.Color SwatchColor { get; private set; }

    /// <summary>Gets a value indicating whether the user can delete this annotation from the sidebar.</summary>
    public bool CanDelete => !Info.IsSignatureWidget;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnnotationItemViewModel"/> class.
    /// </summary>
    /// <param name="info">Extracted PDF annotation record.</param>
    public AnnotationItemViewModel(PdfAnnotationInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        TitleText = ResolveSubtypeTitle(info);
        Text = !string.IsNullOrWhiteSpace(info.Contents) ? info.Contents.Trim() : info.QuotedText.Trim();
        FormattedDate = info.ModifiedDate?.ToLocalTime().ToString("g") ?? string.Empty;
        IconGlyph = ResolveIconGlyph(info);
        SwatchColor = Windows.UI.Color.FromArgb(255, info.Color.R, info.Color.G, info.Color.B);
    }

    /// <summary>
    /// Updates this item in place from a refreshed <see cref="PdfAnnotationInfo"/> snapshot,
    /// raising <see cref="ObservableObject.PropertyChanged"/> only for properties that visibly changed.
    /// </summary>
    /// <param name="updated">Refreshed annotation record.</param>
    public void UpdateFrom(PdfAnnotationInfo updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        PdfAnnotationInfo previous = Info;
        Info = updated;

        string newTitle = ResolveSubtypeTitle(updated);
        if (!string.Equals(TitleText, newTitle, StringComparison.Ordinal))
        {
            TitleText = newTitle;
            OnPropertyChanged(nameof(TitleText));
        }

        string newText = !string.IsNullOrWhiteSpace(updated.Contents) ? updated.Contents.Trim() : updated.QuotedText.Trim();
        if (!string.Equals(Text, newText, StringComparison.Ordinal))
        {
            bool oldHasBody = HasBodyText;
            Text = newText;
            OnPropertyChanged(nameof(Text));
            if (oldHasBody != HasBodyText)
            {
                OnPropertyChanged(nameof(HasBodyText));
            }
        }

        string newDate = updated.ModifiedDate?.ToLocalTime().ToString("g") ?? string.Empty;
        if (!string.Equals(FormattedDate, newDate, StringComparison.Ordinal))
        {
            bool oldHasDate = HasDate;
            FormattedDate = newDate;
            OnPropertyChanged(nameof(FormattedDate));
            if (oldHasDate != HasDate)
            {
                OnPropertyChanged(nameof(HasDate));
            }
        }

        string newGlyph = ResolveIconGlyph(updated);
        if (!string.Equals(IconGlyph, newGlyph, StringComparison.Ordinal))
        {
            IconGlyph = newGlyph;
            OnPropertyChanged(nameof(IconGlyph));
        }

        Windows.UI.Color newSwatch = Windows.UI.Color.FromArgb(255, updated.Color.R, updated.Color.G, updated.Color.B);
        if (SwatchColor != newSwatch)
        {
            SwatchColor = newSwatch;
            OnPropertyChanged(nameof(SwatchColor));
        }

        if (!string.Equals(previous.Author, updated.Author, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Author));
            OnPropertyChanged(nameof(HasAuthor));
        }
    }

    /// <summary>Gets a localized human-readable label for a PDF annotation subtype.</summary>
    /// <param name="info">Annotation metadata record.</param>
    /// <returns>The localized subtype name.</returns>
    public static string ResolveSubtypeTitle(PdfAnnotationInfo info)
    {
        if (info.IsSignatureWidget)
        {
            return ResourceStrings.Get(ResourceKeys.AnnotSubtypeSignature);
        }

        if (info.IsTextBox)
        {
            return ResourceStrings.Get(ResourceKeys.AnnotSubtypeFreeText);
        }

        string key = info.Subtype switch
        {
            PdfAnnotationSubtype.Highlight => ResourceKeys.AnnotSubtypeHighlight,
            PdfAnnotationSubtype.Underline => ResourceKeys.AnnotSubtypeUnderline,
            PdfAnnotationSubtype.Squiggly => ResourceKeys.AnnotSubtypeSquiggly,
            PdfAnnotationSubtype.StrikeOut => ResourceKeys.AnnotSubtypeStrikeOut,
            PdfAnnotationSubtype.Text => ResourceKeys.AnnotSubtypeText,
            PdfAnnotationSubtype.FreeText => ResourceKeys.AnnotSubtypeFreeText,
            PdfAnnotationSubtype.Ink => ResourceKeys.AnnotSubtypeInk,
            PdfAnnotationSubtype.Square => ResourceKeys.AnnotSubtypeSquare,
            PdfAnnotationSubtype.Circle => ResourceKeys.AnnotSubtypeCircle,
            PdfAnnotationSubtype.Line => ResourceKeys.AnnotSubtypeLine,
            PdfAnnotationSubtype.Polygon => ResourceKeys.AnnotSubtypePolygon,
            PdfAnnotationSubtype.PolyLine => ResourceKeys.AnnotSubtypePolyLine,
            PdfAnnotationSubtype.Stamp => ResourceKeys.AnnotSubtypeStamp,
            PdfAnnotationSubtype.Caret => ResourceKeys.AnnotSubtypeCaret,
            PdfAnnotationSubtype.FileAttachment => ResourceKeys.AnnotSubtypeFileAttachment,
            PdfAnnotationSubtype.Redact => ResourceKeys.AnnotSubtypeRedact,
            _ => ResourceKeys.AnnotSubtypeOther,
        };

        return ResourceStrings.Get(key);
    }

    private static string ResolveIconGlyph(PdfAnnotationInfo info)
    {
        if (info.IsSignatureWidget)
        {
            return "\uE72E";
        }

        if (info.IsTextBox)
        {
            return "\uE8D2";
        }

        return info.Subtype switch
        {
            PdfAnnotationSubtype.Highlight => "\uE7E6",
            PdfAnnotationSubtype.Underline or PdfAnnotationSubtype.StrikeOut or PdfAnnotationSubtype.Squiggly => "\uE8E9",
            PdfAnnotationSubtype.Text => "\uE90A",
            PdfAnnotationSubtype.FreeText => "\uE8D2",
            PdfAnnotationSubtype.Ink or PdfAnnotationSubtype.Line or PdfAnnotationSubtype.Square or PdfAnnotationSubtype.Circle or PdfAnnotationSubtype.Polygon or PdfAnnotationSubtype.PolyLine => "\uED63",
            PdfAnnotationSubtype.Stamp => "\uE7C3",
            PdfAnnotationSubtype.FileAttachment => "\uE723",
            _ => "\uE90A",
        };
    }
}

/// <summary>
/// View model representing a verified digital signature in the sidebar.
/// </summary>
public sealed partial class SignatureItemViewModel : ObservableObject
{
    /// <summary>Gets the inspected signature metadata record.</summary>
    public PdfSignatureInfo Info { get; }

    /// <summary>Gets the zero-based signature index.</summary>
    public int Index => Info.Index;

    /// <summary>Gets the zero-based page index where the visible signature stamp resides, or -1 if invisible.</summary>
    public int PageIndex => Info.PageIndex;

    /// <summary>Gets a value indicating whether the signature has a visible page stamp location.</summary>
    public bool HasPageLocation => Info.PageIndex >= 0;

    /// <summary>Gets the localized page label when <see cref="HasPageLocation"/> is true.</summary>
    public string PageBadgeText => Info.PageIndex >= 0 ? UiHelpers.FormatBookmarkPage(Info.PageIndex + 1) : string.Empty;

    /// <summary>Gets the signer's common name (fallback to localized label if absent).</summary>
    public string SignerName => !string.IsNullOrWhiteSpace(Info.SignerName)
        ? Info.SignerName
        : ResourceStrings.Get(ResourceKeys.AnnotSubtypeSignature);

    /// <summary>Gets the issuing Certificate Authority name.</summary>
    public string IssuerName => Info.IssuerName;

    /// <summary>Gets a value indicating whether an issuer name is available.</summary>
    public bool HasIssuerName => !string.IsNullOrWhiteSpace(Info.IssuerName);

    /// <summary>Gets the formatted local signing timestamp.</summary>
    public string FormattedSigningTime { get; }

    /// <summary>Gets a value indicating whether a signing timestamp is available.</summary>
    public bool HasSigningTime => !string.IsNullOrWhiteSpace(FormattedSigningTime);

    /// <summary>Gets the verification status enum.</summary>
    public PdfSignatureVerificationStatus Status => Info.Status;

    /// <summary>Gets the localized human-readable verification status.</summary>
    public string StatusText { get; }

    /// <summary>Gets the Segoe Fluent icon glyph representing the signature status.</summary>
    public string StatusGlyph { get; }

    /// <summary>Gets the status badge color.</summary>
    public Windows.UI.Color StatusColor { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SignatureItemViewModel"/> class.
    /// </summary>
    /// <param name="info">Inspected signature metadata record.</param>
    public SignatureItemViewModel(PdfSignatureInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        FormattedSigningTime = info.SigningTime?.ToLocalTime().ToString("g") ?? string.Empty;
        StatusText = ResolveStatusText(info.Status);
        (StatusGlyph, StatusColor) = ResolveStatusVisual(info.Status);
    }

    /// <summary>Returns the localized status label for a verification result.</summary>
    /// <param name="status">Signature verification status.</param>
    /// <returns>The localized status string.</returns>
    public static string ResolveStatusText(PdfSignatureVerificationStatus status) => status switch
    {
        PdfSignatureVerificationStatus.Valid => ResourceStrings.Get(ResourceKeys.SigStatusValid),
        PdfSignatureVerificationStatus.ValidUntrustedRoot => ResourceStrings.Get(ResourceKeys.SigStatusValidUntrustedRoot),
        PdfSignatureVerificationStatus.ModifiedAfterSigning => ResourceStrings.Get(ResourceKeys.SigStatusModifiedAfterSigning),
        PdfSignatureVerificationStatus.Invalid => ResourceStrings.Get(ResourceKeys.SigStatusInvalid),
        _ => ResourceStrings.Get(ResourceKeys.SigStatusUnknown),
    };

    private static (string Glyph, Windows.UI.Color Color) ResolveStatusVisual(PdfSignatureVerificationStatus status) => status switch
    {
        PdfSignatureVerificationStatus.Valid or PdfSignatureVerificationStatus.ValidUntrustedRoot => ("\uE73E", Windows.UI.Color.FromArgb(255, 16, 124, 16)),
        PdfSignatureVerificationStatus.ModifiedAfterSigning => ("\uE7BA", Windows.UI.Color.FromArgb(255, 202, 80, 16)),
        PdfSignatureVerificationStatus.Invalid => ("\uEA39", Windows.UI.Color.FromArgb(255, 197, 15, 31)),
        _ => ("\uE9CE", Windows.UI.Color.FromArgb(255, 118, 118, 118)),
    };
}

/// <summary>
/// View model representing an embedded file attachment item in the sidebar.
/// </summary>
public sealed partial class AttachmentItemViewModel : ObservableObject
{
    /// <summary>
    /// Gets the attachment file name.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AttachmentItemViewModel"/> class.
    /// </summary>
    /// <param name="fileName">File name of the embedded attachment.</param>
    public AttachmentItemViewModel(string fileName)
    {
        FileName = fileName;
    }
}
