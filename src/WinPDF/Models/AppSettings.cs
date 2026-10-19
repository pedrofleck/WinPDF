namespace WinPDF.Models;

/// <summary>
/// Represents user preferences and session state persisted to <c>%LOCALAPPDATA%\WinPDF\settings.json</c>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Gets or sets the application visual theme preference.
    /// </summary>
    public AppThemePreference Theme { get; set; } = AppThemePreference.System;

    /// <summary>
    /// Gets or sets the default document viewing mode when opening files.
    /// </summary>
    public ViewMode DefaultViewMode { get; set; } = ViewMode.Continuous;

    /// <summary>
    /// Gets or sets the default initial zoom option index (0: Fit Width, 1: Fit Page, 2: 100%).
    /// </summary>
    public int DefaultZoomIndex { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether touch contacts draw annotations by default.
    /// </summary>
    public bool DefaultTouchDrawing { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the last open document should be reopened on startup.
    /// </summary>
    public bool ReopenLastDocument { get; set; }

    /// <summary>
    /// Gets or sets the full file system path of the last opened PDF document.
    /// </summary>
    public string? LastOpenedFilePath { get; set; }

    /// <summary>
    /// Gets or sets the zero-based page index of the last opened PDF document.
    /// </summary>
    public int LastOpenedPageIndex { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the navigation sidebar is open by default.
    /// </summary>
    public bool IsSidebarOpen { get; set; } = true;

    /// <summary>
    /// Gets or sets the user-configured width of the navigation sidebar in DIPs.
    /// </summary>
    public double SidebarWidth { get; set; } = 260.0;

    /// <summary>
    /// Gets or sets the per-document persisted bookmarks keyed by full PDF file path.
    /// </summary>
    public Dictionary<string, List<SavedBookmarkItem>> DocumentBookmarks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Represents a persisted page bookmark entry with an optional user-defined custom title.
/// </summary>
public sealed class SavedBookmarkItem
{
    /// <summary>
    /// Gets or sets the zero-based page index of the bookmarked page.
    /// </summary>
    public int PageIndex { get; set; }

    /// <summary>
    /// Gets or sets the custom user-defined title for this bookmark, or <see langword="null"/> for the default page label.
    /// </summary>
    public string? CustomTitle { get; set; }
}
