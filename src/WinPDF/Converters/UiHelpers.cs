using Microsoft.UI.Xaml;
using WinPDF.Models;

namespace WinPDF.Converters;

/// <summary>
/// Strongly-typed static helper methods for compile-time <c>x:Bind</c> expressions in XAML.
/// </summary>
public static class UiHelpers
{
    /// <summary>
    /// Returns <see cref="Visibility.Visible"/> if the condition is true; otherwise, <see cref="Visibility.Collapsed"/>.
    /// </summary>
    public static Visibility VisibleIf(bool condition) =>
        condition ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Returns <see cref="Visibility.Collapsed"/> if the condition is true; otherwise, <see cref="Visibility.Visible"/>.
    /// </summary>
    public static Visibility CollapsedIf(bool condition) =>
        condition ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Checks if the active view mode is Continuous.
    /// </summary>
    public static bool IsContinuous(ViewMode mode) => mode == ViewMode.Continuous;

    /// <summary>
    /// Checks if the active view mode is Paged.
    /// </summary>
    public static bool IsPaged(ViewMode mode) => mode == ViewMode.Paged;

    /// <summary>
    /// Returns Visible if the active view mode is Continuous.
    /// </summary>
    public static Visibility ContinuousVisibility(ViewMode mode) =>
        mode == ViewMode.Continuous ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Returns Visible if the active view mode is Paged.
    /// </summary>
    public static Visibility PagedVisibility(ViewMode mode) =>
        mode == ViewMode.Paged ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Checks if active sidebar tab is Thumbnails.</summary>
    public static bool IsThumbnailsTab(SidebarTab tab) => tab == SidebarTab.Thumbnails;

    /// <summary>Checks if active sidebar tab is Outline.</summary>
    public static bool IsOutlineTab(SidebarTab tab) => tab == SidebarTab.Outline;

    /// <summary>Checks if active sidebar tab is Annotations.</summary>
    public static bool IsAnnotationsTab(SidebarTab tab) => tab == SidebarTab.Annotations;

    /// <summary>Checks if active sidebar tab is Bookmarks.</summary>
    public static bool IsBookmarksTab(SidebarTab tab) => tab == SidebarTab.Bookmarks;

    /// <summary>Checks if active sidebar tab is Attachments.</summary>
    public static bool IsAttachmentsTab(SidebarTab tab) => tab == SidebarTab.Attachments;

    /// <summary>Returns Visible if active sidebar tab is Thumbnails.</summary>
    public static Visibility ThumbnailsVisibility(SidebarTab tab) =>
        tab == SidebarTab.Thumbnails ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Returns Visible if active sidebar tab is Outline.</summary>
    public static Visibility OutlineVisibility(SidebarTab tab) =>
        tab == SidebarTab.Outline ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Returns Visible if active sidebar tab is Annotations.</summary>
    public static Visibility AnnotationsVisibility(SidebarTab tab) =>
        tab == SidebarTab.Annotations ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Returns Visible if active sidebar tab is Bookmarks.</summary>
    public static Visibility BookmarksVisibility(SidebarTab tab) =>
        tab == SidebarTab.Bookmarks ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Returns Visible if active sidebar tab is Attachments.</summary>
    public static Visibility AttachmentsVisibility(SidebarTab tab) =>
        tab == SidebarTab.Attachments ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Checks if the active tool matches Select.</summary>
    public static bool IsSelectTool(AnnotationTool tool) => tool == AnnotationTool.Select;

    /// <summary>Checks if the active tool matches Pen.</summary>
    public static bool IsPenTool(AnnotationTool tool) => tool == AnnotationTool.Pen;

    /// <summary>Checks if the active tool matches Highlighter.</summary>
    public static bool IsHighlighterTool(AnnotationTool tool) => tool == AnnotationTool.Highlighter;

    /// <summary>Checks if the active tool matches Eraser.</summary>
    public static bool IsEraserTool(AnnotationTool tool) => tool == AnnotationTool.Eraser;

    /// <summary>Checks if the active tool matches TextNote.</summary>
    public static bool IsTextNoteTool(AnnotationTool tool) => tool == AnnotationTool.TextNote;

    /// <summary>Returns Visible if the count is zero.</summary>
    public static Visibility HasNoItems(int count) =>
        count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Returns Visible if the count is greater than zero.</summary>
    public static Visibility HasItems(int count) =>
        count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Checks if the sidebar pane should be open, requiring both a document to be open and sidebar enabled.
    /// </summary>
    public static bool IsSidebarOpenWithDoc(bool hasDoc, bool isSidebarOpen) => hasDoc && isSidebarOpen;

    /// <summary>
    /// Checks if the sidebar pane should be open in the active document view (false when settings is open).
    /// </summary>
    public static bool IsSidebarOpenInDocumentView(bool hasDoc, bool isSidebarOpen, bool isSettingsOpen) =>
        hasDoc && isSidebarOpen && !isSettingsOpen;

    /// <summary>
    /// Computes the corner radius for the main content container creating the curved junction with the sidebar.
    /// </summary>
    public static CornerRadius MainContainerCornerRadius(bool hasDoc, bool isSidebarOpen, bool isSettingsOpen) =>
        (hasDoc && isSidebarOpen && !isSettingsOpen) ? new CornerRadius(8, 0, 0, 0) : new CornerRadius(0);

    /// <summary>
    /// Computes the border thickness for the main content container.
    /// </summary>
    public static Thickness MainContainerBorderThickness(bool hasDoc, bool isSidebarOpen, bool isSettingsOpen) =>
        (!hasDoc || isSettingsOpen) ? new Thickness(0) : (isSidebarOpen ? new Thickness(1, 1, 0, 0) : new Thickness(0, 1, 0, 0));

    /// <summary>
    /// Returns the background brush for the main content container based on whether a document is loaded and settings state.
    /// </summary>
    public static Microsoft.UI.Xaml.Media.Brush MainContainerBackground(bool hasDoc, bool isSettingsOpen) =>
        (hasDoc && !isSettingsOpen)
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerFillColorDefaultBrush"]
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    /// <summary>
    /// Returns the border brush for the main content container based on whether a document is loaded and settings state.
    /// </summary>
    public static Microsoft.UI.Xaml.Media.Brush MainContainerBorderBrush(bool hasDoc, bool isSettingsOpen) =>
        (hasDoc && !isSettingsOpen)
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    /// <summary>
    /// Formats a 1-based page number for bookmark display.
    /// </summary>
    public static string FormatBookmarkPage(int pageNumber) =>
        WinPDF.Helpers.ResourceStrings.Format(WinPDF.Helpers.ResourceKeys.BookmarkPageFormat, pageNumber);

    /// <summary>Checks if active settings category matches the target category.</summary>
    public static bool IsCategory(SettingsCategory active, SettingsCategory target) => active == target;

    /// <summary>Returns Visible if active settings category matches target category.</summary>
    public static Visibility CategoryVisibility(SettingsCategory active, SettingsCategory target) =>
        active == target ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Checks if the active theme matches the target theme preference.</summary>
    public static bool IsTheme(AppThemePreference active, AppThemePreference target) => active == target;

    /// <summary>Returns border thickness for page border depending on presentation mode.</summary>
    public static Thickness PresentationBorderThickness(bool isPresentation) =>
        isPresentation ? new Thickness(0) : new Thickness(1);

    /// <summary>Returns corner radius for page border depending on presentation mode.</summary>
    public static CornerRadius PresentationCornerRadius(bool isPresentation) =>
        isPresentation ? new CornerRadius(0) : new CornerRadius(4);

    /// <summary>Returns background brush for viewer grid depending on presentation mode.</summary>
    public static Microsoft.UI.Xaml.Media.Brush ViewerBackground(bool isPresentation) =>
        isPresentation
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0))
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];

    /// <summary>Returns page repeater margin depending on presentation mode and continuous scrolling.</summary>
    public static Thickness RepeaterMargin(bool isPresentation, bool isContinuous) =>
        (isPresentation || !isContinuous) ? new Thickness(0) : new Thickness(24);

    /// <summary>Returns border thickness for thumbnail card depending on current page state.</summary>
    public static Thickness ThumbnailBorderThickness(bool isCurrent) =>
        isCurrent ? new Thickness(2) : new Thickness(1);

    /// <summary>Returns border brush for thumbnail card depending on current page state.</summary>
    public static Microsoft.UI.Xaml.Media.Brush ThumbnailBorderBrush(bool isCurrent) =>
        isCurrent
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];

    /// <summary>Returns font weight for thumbnail number label depending on current page state.</summary>
    public static Windows.UI.Text.FontWeight ThumbnailFontWeight(bool isCurrent) =>
        isCurrent ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;

    /// <summary>Returns foreground brush for thumbnail number label depending on current page state.</summary>
    public static Microsoft.UI.Xaml.Media.Brush ThumbnailNumberForeground(bool isCurrent) =>
        isCurrent
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
}
