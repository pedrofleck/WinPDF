using WinPDF.Models;

namespace WinPDF.Services;

/// <summary>
/// Service responsible for loading and persisting user settings to <c>%LOCALAPPDATA%\WinPDF\settings.json</c>.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Gets the current in-memory application settings instance.
    /// </summary>
    AppSettings Settings { get; }

    /// <summary>
    /// Gets the absolute path to the persisted <c>settings.json</c> file on disk.
    /// </summary>
    string SettingsFilePath { get; }

    /// <summary>
    /// Loads settings from disk into <see cref="Settings"/>, or initializes defaults if the file does not exist.
    /// </summary>
    void Load();

    /// <summary>
    /// Persists the current <see cref="Settings"/> state to <c>%LOCALAPPDATA%\WinPDF\settings.json</c>.
    /// </summary>
    void Save();

    /// <summary>
    /// Applies a mutation to <see cref="Settings"/> and immediately saves the updated configuration to disk.
    /// </summary>
    /// <param name="updateAction">Callback that modifies the settings object.</param>
    void Update(Action<AppSettings> updateAction);

    /// <summary>
    /// Attempts to retrieve the persisted reading state for a specific document file path.
    /// </summary>
    /// <param name="filePath">Full file system path of the PDF.</param>
    /// <param name="state">The persisted document state if found, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a state entry exists; otherwise <see langword="false"/>.</returns>
    bool TryGetDocumentState(string filePath, out SavedDocumentState? state);

    /// <summary>
    /// Updates or creates the persisted reading state (page index, zoom factor, view mode, and custom view status) for a document.
    /// </summary>
    /// <param name="filePath">Full file system path of the PDF.</param>
    /// <param name="pageIndex">Optional zero-based page index to update.</param>
    /// <param name="zoomFactor">Optional zoom factor to update.</param>
    /// <param name="viewMode">Optional view mode (continuous or paged) to update.</param>
    /// <param name="hasCustomViewSettings">Optional flag indicating whether custom view settings are explicitly saved.</param>
    void UpdateDocumentState(string filePath, int? pageIndex, double? zoomFactor, ViewMode? viewMode = null, bool? hasCustomViewSettings = null);
}
