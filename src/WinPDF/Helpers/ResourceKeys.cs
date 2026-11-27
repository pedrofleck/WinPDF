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

    /// <summary>Touch selection flyout copy tooltip.</summary>
    public const string TouchSelectionCopyToolTip = "TouchSelection_Copy_ToolTip";

    /// <summary>Touch selection flyout highlight tooltip.</summary>
    public const string TouchSelectionHighlightToolTip = "TouchSelection_Highlight_ToolTip";

    /// <summary>Touch selection flyout highlight-with-comment tooltip.</summary>
    public const string TouchSelectionCommentToolTip = "TouchSelection_Comment_ToolTip";

    /// <summary>Annotation popup save button text.</summary>
    public const string AnnotSave = "Annot_Save";

    /// <summary>Annotation popup delete button text.</summary>
    public const string AnnotDelete = "Annot_Delete";

    /// <summary>Annotation popup close button text.</summary>
    public const string AnnotClose = "Annot_Close";

    /// <summary>Annotation comment textbox placeholder.</summary>
    public const string AnnotCommentPlaceholder = "Annot_CommentPlaceholder";

    /// <summary>Annotation FreeText textbox placeholder.</summary>
    public const string AnnotTextBoxPlaceholder = "Annot_TextBoxPlaceholder";

    /// <summary>Color name: Yellow.</summary>
    public const string ColorYellow = "Color_Yellow";

    /// <summary>Color name: Green.</summary>
    public const string ColorGreen = "Color_Green";

    /// <summary>Color name: Blue.</summary>
    public const string ColorBlue = "Color_Blue";

    /// <summary>Color name: Pink.</summary>
    public const string ColorPink = "Color_Pink";

    /// <summary>Color name: Orange.</summary>
    public const string ColorOrange = "Color_Orange";

    /// <summary>Color name: Red.</summary>
    public const string ColorRed = "Color_Red";

    /// <summary>Color name: Black.</summary>
    public const string ColorBlack = "Color_Black";

    /// <summary>Annotation subtype label: Highlight.</summary>
    public const string AnnotSubtypeHighlight = "Annot_Subtype_Highlight";

    /// <summary>Annotation subtype label: Underline.</summary>
    public const string AnnotSubtypeUnderline = "Annot_Subtype_Underline";

    /// <summary>Annotation subtype label: Squiggly.</summary>
    public const string AnnotSubtypeSquiggly = "Annot_Subtype_Squiggly";

    /// <summary>Annotation subtype label: StrikeOut.</summary>
    public const string AnnotSubtypeStrikeOut = "Annot_Subtype_StrikeOut";

    /// <summary>Annotation subtype label: Sticky Note / Text.</summary>
    public const string AnnotSubtypeText = "Annot_Subtype_Text";

    /// <summary>Annotation subtype label: FreeText box.</summary>
    public const string AnnotSubtypeFreeText = "Annot_Subtype_FreeText";

    /// <summary>Annotation subtype label: Ink drawing.</summary>
    public const string AnnotSubtypeInk = "Annot_Subtype_Ink";

    /// <summary>Annotation subtype label: Rectangle / Square.</summary>
    public const string AnnotSubtypeSquare = "Annot_Subtype_Square";

    /// <summary>Annotation subtype label: Circle.</summary>
    public const string AnnotSubtypeCircle = "Annot_Subtype_Circle";

    /// <summary>Annotation subtype label: Line.</summary>
    public const string AnnotSubtypeLine = "Annot_Subtype_Line";

    /// <summary>Annotation subtype label: Polygon.</summary>
    public const string AnnotSubtypePolygon = "Annot_Subtype_Polygon";

    /// <summary>Annotation subtype label: PolyLine.</summary>
    public const string AnnotSubtypePolyLine = "Annot_Subtype_PolyLine";

    /// <summary>Annotation subtype label: Stamp.</summary>
    public const string AnnotSubtypeStamp = "Annot_Subtype_Stamp";

    /// <summary>Annotation subtype label: Caret.</summary>
    public const string AnnotSubtypeCaret = "Annot_Subtype_Caret";

    /// <summary>Annotation subtype label: File Attachment.</summary>
    public const string AnnotSubtypeFileAttachment = "Annot_Subtype_FileAttachment";

    /// <summary>Annotation subtype label: Redact.</summary>
    public const string AnnotSubtypeRedact = "Annot_Subtype_Redact";

    /// <summary>Annotation subtype label: Digital Signature.</summary>
    public const string AnnotSubtypeSignature = "Annot_Subtype_Signature";

    /// <summary>Annotation subtype label: Generic annotation.</summary>
    public const string AnnotSubtypeOther = "Annot_Subtype_Other";

    /// <summary>Digital signature banner message format for 1 signature. {0} = signer name.</summary>
    public const string SigBannerSingleFormat = "Sig_Banner_SingleFormat";

    /// <summary>Digital signature banner message format for multiple signatures. {0} = count, {1} = primary signer name.</summary>
    public const string SigBannerMultiFormat = "Sig_Banner_MultiFormat";

    /// <summary>Digital signature banner button text.</summary>
    public const string SigBannerViewButton = "Sig_Banner_ViewButton";

    /// <summary>Digital signatures dialog title.</summary>
    public const string SigDialogTitle = "Sig_Dialog_Title";

    /// <summary>Digital signature dialog field: Signer.</summary>
    public const string SigDialogSigner = "Sig_Dialog_Signer";

    /// <summary>Digital signature dialog field: Issuer (CA).</summary>
    public const string SigDialogIssuer = "Sig_Dialog_Issuer";

    /// <summary>Digital signature dialog field: Signing time.</summary>
    public const string SigDialogTime = "Sig_Dialog_Time";

    /// <summary>Digital signature dialog field: Reason.</summary>
    public const string SigDialogReason = "Sig_Dialog_Reason";

    /// <summary>Digital signature dialog field: Format / SubFilter.</summary>
    public const string SigDialogFormat = "Sig_Dialog_Format";

    /// <summary>Digital signature dialog field: Document coverage.</summary>
    public const string SigDialogCoverage = "Sig_Dialog_Coverage";

    /// <summary>Digital signature dialog coverage value: whole document.</summary>
    public const string SigDialogCoverageWhole = "Sig_Dialog_CoverageWhole";

    /// <summary>Digital signature dialog coverage value: incremental edits appended.</summary>
    public const string SigDialogCoverageModified = "Sig_Dialog_CoverageModified";

    /// <summary>Digital signature dialog field: DocMDP modification policy.</summary>
    public const string SigDialogPermission = "Sig_Dialog_Permission";

    /// <summary>Digital signature dialog field: Certificate validity.</summary>
    public const string SigDialogCertValidity = "Sig_Dialog_CertValidity";

    /// <summary>Digital signature dialog field: SHA-256 thumbprint.</summary>
    public const string SigDialogThumbprint = "Sig_Dialog_Thumbprint";

    /// <summary>Digital signature status: Valid.</summary>
    public const string SigStatusValid = "Sig_Status_Valid";

    /// <summary>Digital signature status: Valid cryptographic signature with untrusted root CA.</summary>
    public const string SigStatusValidUntrustedRoot = "Sig_Status_ValidUntrustedRoot";

    /// <summary>Digital signature status: Modified after signing.</summary>
    public const string SigStatusModifiedAfterSigning = "Sig_Status_ModifiedAfterSigning";

    /// <summary>Digital signature status: Invalid.</summary>
    public const string SigStatusInvalid = "Sig_Status_Invalid";

    /// <summary>Digital signature status: Unknown / unsupported.</summary>
    public const string SigStatusUnknown = "Sig_Status_Unknown";

    /// <summary>DocMDP permission level 1 label.</summary>
    public const string SigDocMdpNoChanges = "Sig_DocMdp_NoChanges";

    /// <summary>DocMDP permission level 2 label.</summary>
    public const string SigDocMdpFormFillAndSign = "Sig_DocMdp_FormFillAndSign";

    /// <summary>DocMDP permission level 3 label.</summary>
    public const string SigDocMdpAnnotationsAllowed = "Sig_DocMdp_AnnotationsAllowed";

    /// <summary>DocMDP default permission label.</summary>
    public const string SigDocMdpUnspecified = "Sig_DocMdp_Unspecified";

    /// <summary>Status bar signed document indicator label.</summary>
    public const string StatusBarSignedLabel = "StatusBar_SignedLabel";

    /// <summary>Compact annotation quick-bar tooltip: add or edit comment.</summary>
    public const string AnnotAddCommentToolTip = "Annot_AddComment_ToolTip";

    /// <summary>Compact annotation quick-bar tooltip: change color.</summary>
    public const string AnnotColorToolTip = "Annot_Color_ToolTip";

    /// <summary>Compact annotation quick-bar tooltip: copy annotation.</summary>
    public const string AnnotCopyToolTip = "Annot_Copy_ToolTip";

    /// <summary>Compact annotation quick-bar tooltip: cut annotation.</summary>
    public const string AnnotCutToolTip = "Annot_Cut_ToolTip";

    /// <summary>Touch empty-hold flyout paste button label.</summary>
    public const string TouchEmptyPasteLabel = "TouchEmpty_Paste_Label";

    /// <summary>Detailed signature verification description: valid and trusted chain.</summary>
    public const string SigDetailValid = "Sig_Detail_Valid";

    /// <summary>Detailed signature verification description: valid cryptographic integrity with root CA not in Windows store.</summary>
    public const string SigDetailValidUntrustedRoot = "Sig_Detail_ValidUntrustedRoot";

    /// <summary>Detailed signature verification description: modified after signing.</summary>
    public const string SigDetailModifiedAfterSigning = "Sig_Detail_ModifiedAfterSigning";

    /// <summary>Detailed signature verification description: invalid cryptographic signature or altered content.</summary>
    public const string SigDetailInvalid = "Sig_Detail_Invalid";

    /// <summary>Detailed signature verification description: unknown or unsupported signature format.</summary>
    public const string SigDetailUnknown = "Sig_Detail_Unknown";

    /// <summary>Tooltip for the annotation undo action.</summary>
    public const string AnnotUndoToolTip = "Annot_Undo_ToolTip";

    /// <summary>Context menu item for undoing the last annotation edit.</summary>
    public const string ContextUndoAnnotation = "Context_UndoAnnotation";

    /// <summary>Tooltip for the top annotation toolbar default color picker.</summary>
    public const string ToolbarColorToolTip = "Toolbar_Color_ToolTip";

    /// <summary>Toast hint shown after choosing Add Comment from the Insert menu.</summary>
    public const string InsertCommentHint = "Insert_CommentHint";

    /// <summary>Toast hint shown after choosing an image to insert on the page.</summary>
    public const string InsertImageHint = "Insert_ImageHint";

    /// <summary>Toast message shown when the selected image exceeds the 20 MB limit.</summary>
    public const string InsertImageTooLarge = "Insert_ImageTooLarge";

    /// <summary>Toast hint shown after choosing Add Text from the Insert menu.</summary>
    public const string InsertTextHint = "Insert_TextHint";

    /// <summary>Compact annotation quick-bar tooltip: edit text box.</summary>
    public const string AnnotEditTextToolTip = "Annot_EditText_ToolTip";

    /// <summary>Inline text formatting bar tooltip: font size.</summary>
    public const string TextBarSizeToolTip = "TextBar_Size_ToolTip";

    /// <summary>Inline text formatting bar tooltip: bold.</summary>
    public const string TextBarBoldToolTip = "TextBar_Bold_ToolTip";

    /// <summary>Inline text formatting bar tooltip: italic.</summary>
    public const string TextBarItalicToolTip = "TextBar_Italic_ToolTip";

    /// <summary>Inline text formatting bar tooltip: underline.</summary>
    public const string TextBarUnderlineToolTip = "TextBar_Underline_ToolTip";

    /// <summary>Inline text formatting bar tooltip: text color.</summary>
    public const string TextBarColorToolTip = "TextBar_Color_ToolTip";

    /// <summary>Inline text formatting bar tooltip: discard or delete text box.</summary>
    public const string TextBarDeleteToolTip = "TextBar_Delete_ToolTip";

    /// <summary>Inline text formatting bar tooltip: confirm text box.</summary>
    public const string TextBarConfirmToolTip = "TextBar_Confirm_ToolTip";

    /// <summary>Annotation subtype label: Arrow (<c>/Line</c> with arrowhead).</summary>
    public const string AnnotSubtypeArrow = "Annot_Subtype_Arrow";

    /// <summary>Format string for stroke thickness in points (e.g. "2 pt").</summary>
    public const string ToolbarThicknessPtFormat = "ToolbarThickness_PtFormat";

    /// <summary>Dynamic tooltip format for the shapes toolbar button ({0} = active shape name).</summary>
    public const string ToolShapeFormatToolTip = "Tool_Shape_FormatToolTip";

    /// <summary>Tooltip for the Eraser button when set to full stroke/object mode.</summary>
    public const string ToolEraserStrokeToolTip = "Tool_Eraser_StrokeToolTip";

    /// <summary>Tooltip for the Eraser button when set to point/partial stroke mode.</summary>
    public const string ToolEraserPointToolTip = "Tool_Eraser_PointToolTip";

    /// <summary>Toast hint shown after selecting a signature to place on the page.</summary>
    public const string InsertSignatureHint = "Insert_SignatureHint";

    /// <summary>Title of the signature picker dialog.</summary>
    public const string SignatureDialogTitle = "SignatureDialog_Title";

    /// <summary>Button label to open the signature drawing pad.</summary>
    public const string SignatureDialogDrawButton = "SignatureDialog_DrawButton";

    /// <summary>Button label to upload a signature image.</summary>
    public const string SignatureDialogAddImageButton = "SignatureDialog_AddImageButton";

    /// <summary>Close button label on the signature picker dialog.</summary>
    public const string SignatureDialogCloseButton = "SignatureDialog_CloseButton";

    /// <summary>Empty state message when no saved signatures exist.</summary>
    public const string SignatureDialogEmptyText = "SignatureDialog_EmptyText";

    /// <summary>Notice shown when the maximum number of saved signatures has been reached.</summary>
    public const string SignatureDialogLimitReached = "SignatureDialog_LimitReached";

    /// <summary>Format string for the signature count badge (e.g. "2 / 6").</summary>
    public const string SignatureDialogCountFormat = "SignatureDialog_CountFormat";

    /// <summary>Helper subtitle instructing the user to click a saved signature to place it.</summary>
    public const string SignatureDialogSelectHint = "SignatureDialog_SelectHint";

    /// <summary>Tooltip for the delete button on a saved signature card.</summary>
    public const string SignatureDialogDeleteToolTip = "SignatureDialog_DeleteToolTip";

    /// <summary>Confirmation prompt text before deleting a saved signature.</summary>
    public const string SignatureDialogDeleteConfirmText = "SignatureDialog_DeleteConfirmText";

    /// <summary>Confirm delete button text inside the signature delete flyout.</summary>
    public const string SignatureDialogDeleteConfirmButton = "SignatureDialog_DeleteConfirmButton";

    /// <summary>Cancel button text inside the signature delete flyout or opaque image warning.</summary>
    public const string SignatureDialogCancelButton = "SignatureDialog_CancelButton";

    /// <summary>Warning title when an uploaded signature image has no transparent background.</summary>
    public const string SignatureDialogOpaqueWarningTitle = "SignatureDialog_OpaqueWarningTitle";

    /// <summary>Warning message when an uploaded signature image has no transparent background.</summary>
    public const string SignatureDialogOpaqueWarningMessage = "SignatureDialog_OpaqueWarningMessage";

    /// <summary>Action button text to add an opaque signature image anyway.</summary>
    public const string SignatureDialogAddAnywayButton = "SignatureDialog_AddAnywayButton";

    /// <summary>Instructional subtitle above the signature drawing pad.</summary>
    public const string SignatureDialogDrawHint = "SignatureDialog_DrawHint";

    /// <summary>Button label to clear the signature drawing pad.</summary>
    public const string SignatureDialogClearButton = "SignatureDialog_ClearButton";

    /// <summary>Button label to return from drawing pad to signature gallery.</summary>
    public const string SignatureDialogBackButton = "SignatureDialog_BackButton";

    /// <summary>Primary button label to save a drawn signature.</summary>
    public const string SignatureDialogSaveButton = "SignatureDialog_SaveButton";

    /// <summary>Accessible tooltip/name for the black signature ink swatch.</summary>
    public const string SignatureDialogColorBlack = "SignatureDialog_ColorBlack";

    /// <summary>Accessible tooltip/name for the blue signature ink swatch.</summary>
    public const string SignatureDialogColorBlue = "SignatureDialog_ColorBlue";

    /// <summary>Accessible tooltip/name for the red signature ink swatch.</summary>
    public const string SignatureDialogColorRed = "SignatureDialog_ColorRed";

    /// <summary>Tooltip shown on the yellow warning badge of a saved signature without a transparent background.</summary>
    public const string SignatureDialogOpaqueBadgeToolTip = "SignatureDialog_OpaqueBadgeToolTip";

    /// <summary>Windows taskbar Jump List category header for recent PDF documents.</summary>
    public const string JumpListRecentCategory = "JumpList_RecentCategory";

    /// <summary>Windows taskbar Jump List task label to open a new blank window.</summary>
    public const string JumpListNewWindowTask = "JumpList_NewWindowTask";
}
