namespace WinPDF.Helpers;

/// <summary>
/// Compile-time constants for every resource key consumed from C# code.
/// </summary>
/// <remarks>
/// Every constant here MUST exist in both <c>Strings/en-US/Resources.resw</c> and
/// <c>Strings/pt-BR/Resources.resw</c>. Keys consumed only via XAML <c>x:Uid</c>
/// (e.g. <c>OpenFileButton.Label</c>) are not listed here.
/// </remarks>
public static class ResourceKeys
{
    /// <summary>Application display name.</summary>
    public const string AppDisplayName = "AppDisplayName";

    /// <summary>Short application description used in the About dialog.</summary>
    public const string AppDescription = "AppDescription";

    /// <summary>Window title format. {0} = document file name.</summary>
    public const string WindowTitleFormat = "Window_TitleFormat";

    /// <summary>Window title format when there are unsaved changes. {0} = document file name.</summary>
    public const string WindowTitleModifiedFormat = "Window_TitleModifiedFormat";

    /// <summary>File type description shown in file pickers for *.pdf.</summary>
    public const string FilePickerPdfFileType = "FilePicker_PdfFileType";

    /// <summary>"of {0}" suffix next to the page number box. {0} = page count.</summary>
    public const string PageCountFormat = "Page_CountFormat";

    /// <summary>Bookmark list item page label format. {0} = page number.</summary>
    public const string BookmarkPageFormat = "Bookmark_PageFormat";

    /// <summary>Zoom percentage format. {0} = integer percentage.</summary>
    public const string ZoomPercentFormat = "Zoom_PercentFormat";

    /// <summary>Generic OK button.</summary>
    public const string DialogOk = "Dialog_Ok";

    /// <summary>Generic Cancel button.</summary>
    public const string DialogCancel = "Dialog_Cancel";

    /// <summary>Generic Close button.</summary>
    public const string DialogClose = "Dialog_Close";

    /// <summary>Unsaved changes dialog title. {0} = document file name.</summary>
    public const string DialogUnsavedChangesTitle = "Dialog_UnsavedChanges_Title";

    /// <summary>Unsaved changes dialog body.</summary>
    public const string DialogUnsavedChangesContent = "Dialog_UnsavedChanges_Content";

    /// <summary>Unsaved changes dialog primary button.</summary>
    public const string DialogUnsavedChangesSave = "Dialog_UnsavedChanges_Save";

    /// <summary>Unsaved changes dialog secondary button.</summary>
    public const string DialogUnsavedChangesDontSave = "Dialog_UnsavedChanges_DontSave";

    /// <summary>Open error dialog title.</summary>
    public const string DialogOpenErrorTitle = "Dialog_OpenError_Title";

    /// <summary>Open error dialog body. {0} = file name, {1} = reason.</summary>
    public const string DialogOpenErrorContent = "Dialog_OpenError_Content";

    /// <summary>Save error dialog title.</summary>
    public const string DialogSaveErrorTitle = "Dialog_SaveError_Title";

    /// <summary>Save error dialog body. {0} = file name, {1} = reason.</summary>
    public const string DialogSaveErrorContent = "Dialog_SaveError_Content";

    /// <summary>Password dialog title.</summary>
    public const string DialogPasswordTitle = "Dialog_Password_Title";

    /// <summary>Password dialog body. {0} = file name.</summary>
    public const string DialogPasswordContent = "Dialog_Password_Content";

    /// <summary>Password box placeholder.</summary>
    public const string DialogPasswordPlaceholder = "Dialog_Password_Placeholder";

    /// <summary>Password dialog primary button.</summary>
    public const string DialogPasswordUnlock = "Dialog_Password_Unlock";

    /// <summary>Error: wrong password.</summary>
    public const string ErrorInvalidPassword = "Error_InvalidPassword";

    /// <summary>Error: file not found or not readable (PDFium FPDF_ERR_FILE).</summary>
    public const string ErrorFileNotFound = "Error_FileNotFound";

    /// <summary>Error: invalid or corrupted PDF (PDFium FPDF_ERR_FORMAT).</summary>
    public const string ErrorInvalidFormat = "Error_InvalidFormat";

    /// <summary>Error: unsupported security handler (PDFium FPDF_ERR_SECURITY).</summary>
    public const string ErrorUnsupportedSecurity = "Error_UnsupportedSecurity";

    /// <summary>Error: page not found or content error (PDFium FPDF_ERR_PAGE).</summary>
    public const string ErrorPage = "Error_Page";

    /// <summary>Error: file could not be written.</summary>
    public const string ErrorWriteFailed = "Error_WriteFailed";

    /// <summary>Error: unknown failure.</summary>
    public const string ErrorUnknown = "Error_Unknown";

    /// <summary>Status: document loading.</summary>
    public const string StatusLoading = "Status_Loading";

    /// <summary>Status: document saving.</summary>
    public const string StatusSaving = "Status_Saving";

    /// <summary>Status: document saved.</summary>
    public const string StatusSaved = "Status_Saved";

    /// <summary>Text note editor placeholder.</summary>
    public const string NotePlaceholder = "Note_Placeholder";

    /// <summary>Text note delete action.</summary>
    public const string NoteDelete = "Note_Delete";

    /// <summary>Settings dialog title.</summary>
    public const string SettingsDialogTitle = "SettingsDialog_Title";

    /// <summary>Document properties dialog title.</summary>
    public const string DocPropsTitle = "DocProps_Title";

    /// <summary>Document properties: file name label.</summary>
    public const string DocPropsFileName = "DocProps_FileName";

    /// <summary>Document properties: file path label.</summary>
    public const string DocPropsFilePath = "DocProps_FilePath";

    /// <summary>Document properties: file size label.</summary>
    public const string DocPropsFileSize = "DocProps_FileSize";

    /// <summary>Document properties: page count label.</summary>
    public const string DocPropsPageCount = "DocProps_PageCount";

    /// <summary>Document properties: page size label.</summary>
    public const string DocPropsPageSize = "DocProps_PageSize";

    /// <summary>Drag-and-drop overlay caption when dragging a PDF file over the window.</summary>
    public const string OpenFileDragCaption = "OpenFile_DragCaption";

    /// <summary>Alias for <see cref="OpenFileDragCaption"/>.</summary>
    public const string OpenFile_DragCaption = "OpenFile_DragCaption";

    /// <summary>Status bar: page prefix.</summary>
    public const string StatusBarPagePrefix = "StatusBar_PagePrefix";

    /// <summary>Status bar: page box tooltip.</summary>
    public const string StatusBarPageBoxToolTip = "StatusBar_PageBoxToolTip";

    /// <summary>Status bar: document format tooltip.</summary>
    public const string StatusBarFormatToolTip = "StatusBar_FormatToolTip";

    /// <summary>Status bar: document properties tooltip.</summary>
    public const string StatusBarPropertiesToolTip = "StatusBar_PropertiesToolTip";

    /// <summary>Status bar: two-page mode toggle tooltip.</summary>
    public const string StatusBarDoublePageToolTip = "StatusBar_DoublePageToolTip";

    /// <summary>Status bar: continuous scroll mode toggle tooltip.</summary>
    public const string StatusBarContinuousToolTip = "StatusBar_ContinuousToolTip";

    /// <summary>Status bar: fit vertical (fit page) tooltip.</summary>
    public const string StatusBarFitVerticalToolTip = "StatusBar_FitVerticalToolTip";

    /// <summary>Status bar: fit horizontal (fit width) tooltip.</summary>
    public const string StatusBarFitHorizontalToolTip = "StatusBar_FitHorizontalToolTip";

    /// <summary>Status bar: zoom preset dropdown tooltip.</summary>
    public const string StatusBarZoomPresetToolTip = "StatusBar_ZoomPresetToolTip";

    /// <summary>Status bar: zoom out button tooltip.</summary>
    public const string StatusBarZoomOutToolTip = "StatusBar_ZoomOutToolTip";

    /// <summary>Status bar: zoom slider tooltip.</summary>
    public const string StatusBarZoomSliderToolTip = "StatusBar_ZoomSliderToolTip";

    /// <summary>Status bar: zoom in button tooltip.</summary>
    public const string StatusBarZoomInToolTip = "StatusBar_ZoomInToolTip";

    /// <summary>Status bar: slideshow presentation tooltip.</summary>
    public const string StatusBarSlideshowToolTip = "StatusBar_SlideshowToolTip";

    /// <summary>Status bar: full screen toggle tooltip.</summary>
    public const string StatusBarFullScreenToolTip = "StatusBar_FullScreenToolTip";

    /// <summary>Floating exit presentation button text.</summary>
    public const string PresentationExitButton = "Presentation_ExitButton";

    /// <summary>Title for the top InfoBar notification when the last document is missing on startup.</summary>
    public const string MissingLastDocInfoBarTitle = "MissingLastDocInfoBar_Title";

    /// <summary>Message format for the top InfoBar notification when the last document is missing on startup. {0} = file name.</summary>
    public const string MissingLastDocInfoBarMessageFormat = "MissingLastDocInfoBar_MessageFormat";

    /// <summary>Search match counter format in the floating search bar. {0} = current match (1-based), {1} = total matches.</summary>
    public const string SearchMatchCountFormat = "Search_MatchCountFormat";

    /// <summary>Confirmation dialog title when clicking an external URI link inside a PDF.</summary>
    public const string DialogOpenExternalLinkTitle = "Dialog_OpenExternalLink_Title";

    /// <summary>Confirmation dialog body when clicking an external URI link inside a PDF. {0} = target URI.</summary>
    public const string DialogOpenExternalLinkContent = "Dialog_OpenExternalLink_Content";

    /// <summary>Primary button text to confirm opening an external URI link.</summary>
    public const string DialogOpenExternalLinkConfirm = "Dialog_OpenExternalLink_Confirm";

    /// <summary>Dialog title for GNU GPLv3 license terms.</summary>
    public const string DialogLicenseTitle = "Dialog_License_Title";

    /// <summary>Dialog title for third-party software acknowledgments.</summary>
    public const string DialogThirdPartyTitle = "Dialog_ThirdParty_Title";

    /// <summary>Close button text for legal notice dialogs.</summary>
    public const string DialogCloseButton = "Dialog_CloseButton";
}
