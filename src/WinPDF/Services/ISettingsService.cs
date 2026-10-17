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
}
