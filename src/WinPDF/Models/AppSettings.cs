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
    /// Gets or sets the application language override code (e.g. "", "en-US", "pt-BR"). An empty string denotes the system default.
    /// </summary>
    public string AppLanguage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the last viewed page of each document should be remembered and restored.
    /// </summary>
    public bool RememberDocumentPage { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether each document's custom view settings (zoom level and continuous/paged mode) should be remembered and restored.
    /// </summary>
    public bool SaveDocumentViewSettings { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether each document's custom zoom level should be remembered and restored.
    /// </summary>
    [Obsolete("Use SaveDocumentViewSettings instead")]
    public bool KeepDocumentZoomLevel
    {
        get => SaveDocumentViewSettings;
        set => SaveDocumentViewSettings = value;
    }

    /// <summary>
    /// Gets or sets the per-document persisted bookmarks keyed by full PDF file path.
    /// </summary>
    public Dictionary<string, List<SavedBookmarkItem>> DocumentBookmarks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the per-document persisted reading state (page index, zoom factor, and view mode) keyed by normalized PDF file path.
    /// </summary>
    public Dictionary<string, SavedDocumentState> DocumentStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Represents individual per-document reading state persisted across sessions.
/// </summary>
public sealed class SavedDocumentState
{
    /// <summary>
    /// Gets or sets the zero-based page index last viewed by the user.
    /// </summary>
    public int PageIndex { get; set; }

    /// <summary>
    /// Gets or sets the custom zoom factor last applied by the user.
    /// </summary>
    public double? ZoomFactor { get; set; }

    /// <summary>
    /// Gets or sets the view mode (continuous or paged) last used for this document.
    /// </summary>
    public ViewMode? ViewMode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this document has custom view settings explicitly saved by the user on document close.
    /// </summary>
    public bool HasCustomViewSettings { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when this document was last opened.
    /// </summary>
    public DateTime LastOpenedUtc { get; set; } = DateTime.UtcNow;
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
