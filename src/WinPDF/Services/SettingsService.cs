using System.IO;
using System.Text.Json;
using WinPDF.Models;

namespace WinPDF.Services;

/// <summary>
/// Thread-safe JSON persistence service storing user settings in <c>%LOCALAPPDATA%\WinPDF\settings.json</c>.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly object _syncLock = new();

    /// <inheritdoc/>
    public AppSettings Settings { get; private set; } = new();

    /// <inheritdoc/>
    public string SettingsFilePath { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsService"/> class and loads persisted settings from disk.
    /// </summary>
    public SettingsService()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appDir = Path.Combine(localAppData, "WinPDF");
        SettingsFilePath = Path.Combine(appDir, "settings.json");
        Load();
    }

    /// <inheritdoc/>
    public void Load()
    {
        lock (_syncLock)
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    Settings = new AppSettings();
                    SaveInternal();
                    return;
                }

                byte[] bytes = File.ReadAllBytes(SettingsFilePath);
                if (bytes.Length == 0)
                {
                    Settings = new AppSettings();
                    SaveInternal();
                    return;
                }

                using JsonDocument doc = JsonDocument.Parse(bytes);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    Settings = new AppSettings();
                    return;
                }

                AppSettings loaded = new();

                if (TryGetPropertyIgnoreCase(root, "theme", out JsonElement themeEl))
                {
                    if (themeEl.ValueKind == JsonValueKind.String &&
                        Enum.TryParse<AppThemePreference>(themeEl.GetString(), ignoreCase: true, out var parsedTheme))
                    {
                        loaded.Theme = parsedTheme;
                    }
                    else if (themeEl.ValueKind == JsonValueKind.Number && themeEl.TryGetInt32(out int themeInt) &&
                             Enum.IsDefined(typeof(AppThemePreference), themeInt))
                    {
                        loaded.Theme = (AppThemePreference)themeInt;
                    }
                }

                if (TryGetPropertyIgnoreCase(root, "defaultViewMode", out JsonElement modeEl))
                {
                    if (modeEl.ValueKind == JsonValueKind.String &&
                        Enum.TryParse<ViewMode>(modeEl.GetString(), ignoreCase: true, out var parsedMode))
                    {
                        loaded.DefaultViewMode = parsedMode;
                    }
                    else if (modeEl.ValueKind == JsonValueKind.Number && modeEl.TryGetInt32(out int modeInt) &&
                             Enum.IsDefined(typeof(ViewMode), modeInt))
                    {
                        loaded.DefaultViewMode = (ViewMode)modeInt;
                    }
                }

                if (TryGetPropertyIgnoreCase(root, "defaultZoomIndex", out JsonElement zoomEl) &&
                    zoomEl.ValueKind == JsonValueKind.Number && zoomEl.TryGetInt32(out int zoomIdx))
                {
                    loaded.DefaultZoomIndex = Math.Clamp(zoomIdx, 0, 2);
                }

                if (TryGetPropertyIgnoreCase(root, "defaultTouchDrawing", out JsonElement touchEl) &&
                    (touchEl.ValueKind == JsonValueKind.True || touchEl.ValueKind == JsonValueKind.False))
                {
                    loaded.DefaultTouchDrawing = touchEl.GetBoolean();
                }

                if (TryGetPropertyIgnoreCase(root, "reopenLastDocument", out JsonElement reopenEl) &&
                    (reopenEl.ValueKind == JsonValueKind.True || reopenEl.ValueKind == JsonValueKind.False))
                {
                    loaded.ReopenLastDocument = reopenEl.GetBoolean();
                }

                if (TryGetPropertyIgnoreCase(root, "lastOpenedFilePath", out JsonElement pathEl))
                {
                    loaded.LastOpenedFilePath = pathEl.ValueKind == JsonValueKind.String ? pathEl.GetString() : null;
                }

                if (TryGetPropertyIgnoreCase(root, "lastOpenedPageIndex", out JsonElement pageEl) &&
                    pageEl.ValueKind == JsonValueKind.Number && pageEl.TryGetInt32(out int pageIdx))
                {
                    loaded.LastOpenedPageIndex = Math.Max(0, pageIdx);
                }

                if (TryGetPropertyIgnoreCase(root, "isSidebarOpen", out JsonElement sidebarOpenEl) &&
                    (sidebarOpenEl.ValueKind == JsonValueKind.True || sidebarOpenEl.ValueKind == JsonValueKind.False))
                {
                    loaded.IsSidebarOpen = sidebarOpenEl.GetBoolean();
                }

                if (TryGetPropertyIgnoreCase(root, "sidebarWidth", out JsonElement widthEl) &&
                    widthEl.ValueKind == JsonValueKind.Number && widthEl.TryGetDouble(out double sidebarW) &&
                    double.IsFinite(sidebarW))
                {
                    loaded.SidebarWidth = Math.Clamp(sidebarW, 220.0, 480.0);
                }

                if (TryGetPropertyIgnoreCase(root, "appLanguage", out JsonElement langEl) && langEl.ValueKind == JsonValueKind.String)
                {
                    loaded.AppLanguage = langEl.GetString() ?? string.Empty;
                }

                if (TryGetPropertyIgnoreCase(root, "rememberDocumentPage", out JsonElement remPageEl) &&
                    (remPageEl.ValueKind == JsonValueKind.True || remPageEl.ValueKind == JsonValueKind.False))
                {
                    loaded.RememberDocumentPage = remPageEl.GetBoolean();
                }

                if (TryGetPropertyIgnoreCase(root, "saveDocumentViewSettings", out JsonElement saveViewEl) &&
                    (saveViewEl.ValueKind == JsonValueKind.True || saveViewEl.ValueKind == JsonValueKind.False))
                {
                    loaded.SaveDocumentViewSettings = saveViewEl.GetBoolean();
                }
                else if (TryGetPropertyIgnoreCase(root, "keepDocumentZoomLevel", out JsonElement keepZoomEl) &&
                    (keepZoomEl.ValueKind == JsonValueKind.True || keepZoomEl.ValueKind == JsonValueKind.False))
                {
                    loaded.SaveDocumentViewSettings = keepZoomEl.GetBoolean();
                }

                if (TryGetPropertyIgnoreCase(root, "documentBookmarks", out JsonElement bookmarksEl) &&
                    bookmarksEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty docProp in bookmarksEl.EnumerateObject())
                    {
                        if (string.IsNullOrWhiteSpace(docProp.Name) || docProp.Value.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        List<SavedBookmarkItem> list = new();
                        foreach (JsonElement itemEl in docProp.Value.EnumerateArray())
                        {
                            if (itemEl.ValueKind == JsonValueKind.Object &&
                                TryGetPropertyIgnoreCase(itemEl, "pageIndex", out JsonElement pIdxEl) &&
                                pIdxEl.ValueKind == JsonValueKind.Number &&
                                pIdxEl.TryGetInt32(out int pIdx) && pIdx >= 0)
                            {
                                string? customTitle = null;
                                if (TryGetPropertyIgnoreCase(itemEl, "customTitle", out JsonElement titleEl) &&
                                    titleEl.ValueKind == JsonValueKind.String)
                                {
                                    customTitle = titleEl.GetString();
                                }

                                list.Add(new SavedBookmarkItem
                                {
                                    PageIndex = pIdx,
                                    CustomTitle = string.IsNullOrWhiteSpace(customTitle) ? null : customTitle.Trim()
                                });
                            }
                        }

                        if (list.Count > 0)
                        {
                            loaded.DocumentBookmarks[docProp.Name] = list;
                        }
                    }
                }

                if (TryGetPropertyIgnoreCase(root, "documentStates", out JsonElement statesEl) &&
                    statesEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty docProp in statesEl.EnumerateObject())
                    {
                        if (string.IsNullOrWhiteSpace(docProp.Name) || docProp.Value.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        int pIdx = 0;
                        if (TryGetPropertyIgnoreCase(docProp.Value, "pageIndex", out JsonElement pEl) &&
                            pEl.ValueKind == JsonValueKind.Number && pEl.TryGetInt32(out int parsedP))
                        {
                            pIdx = Math.Max(0, parsedP);
                        }

                        double? zoomFactor = null;
                        if (TryGetPropertyIgnoreCase(docProp.Value, "zoomFactor", out JsonElement zEl) &&
                            zEl.ValueKind == JsonValueKind.Number && zEl.TryGetDouble(out double parsedZ) &&
                            double.IsFinite(parsedZ))
                        {
                            zoomFactor = parsedZ;
                        }

                        DateTime lastOpened = DateTime.UtcNow;
                        if (TryGetPropertyIgnoreCase(docProp.Value, "lastOpenedUtc", out JsonElement dtEl) &&
                            dtEl.ValueKind == JsonValueKind.String &&
                            DateTime.TryParse(dtEl.GetString(), out DateTime parsedDt))
                        {
                            lastOpened = parsedDt;
                        }

                        ViewMode? viewMode = null;
                        if (TryGetPropertyIgnoreCase(docProp.Value, "viewMode", out JsonElement vmEl) &&
                            vmEl.ValueKind == JsonValueKind.String &&
                            Enum.TryParse(vmEl.GetString(), ignoreCase: true, out ViewMode parsedVm))
                        {
                            viewMode = parsedVm;
                        }

                        bool hasCustomView = false;
                        if (TryGetPropertyIgnoreCase(docProp.Value, "hasCustomViewSettings", out JsonElement hcvEl) &&
                            (hcvEl.ValueKind == JsonValueKind.True || hcvEl.ValueKind == JsonValueKind.False))
                        {
                            hasCustomView = hcvEl.GetBoolean();
                        }

                        loaded.DocumentStates[docProp.Name] = new SavedDocumentState
                        {
                            PageIndex = pIdx,
                            ZoomFactor = hasCustomView ? zoomFactor : null,
                            ViewMode = hasCustomView ? viewMode : null,
                            HasCustomViewSettings = hasCustomView,
                            LastOpenedUtc = lastOpened
                        };
                    }
                }

                Settings = loaded;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings.json: {ex.Message}");
                Settings = new AppSettings();
            }
        }
    }

    /// <inheritdoc/>
    public void Save()
    {
        lock (_syncLock)
        {
            SaveInternal();
        }
    }

    /// <inheritdoc/>
    public void Update(Action<AppSettings> updateAction)
    {
        ArgumentNullException.ThrowIfNull(updateAction);
        lock (_syncLock)
        {
            updateAction(Settings);
            SaveInternal();
        }
    }

    private void SaveInternal()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using MemoryStream ms = new();
            using (Utf8JsonWriter writer = new(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("theme", Settings.Theme.ToString());
                writer.WriteString("defaultViewMode", Settings.DefaultViewMode.ToString());
                writer.WriteNumber("defaultZoomIndex", Math.Clamp(Settings.DefaultZoomIndex, 0, 2));
                writer.WriteBoolean("defaultTouchDrawing", Settings.DefaultTouchDrawing);
                writer.WriteBoolean("reopenLastDocument", Settings.ReopenLastDocument);
                if (!string.IsNullOrWhiteSpace(Settings.LastOpenedFilePath))
                {
                    writer.WriteString("lastOpenedFilePath", Settings.LastOpenedFilePath);
                }
                else
                {
                    writer.WriteNull("lastOpenedFilePath");
                }

                writer.WriteNumber("lastOpenedPageIndex", Math.Max(0, Settings.LastOpenedPageIndex));
                writer.WriteBoolean("isSidebarOpen", Settings.IsSidebarOpen);
                writer.WriteString("appLanguage", Settings.AppLanguage ?? string.Empty);
                writer.WriteBoolean("rememberDocumentPage", Settings.RememberDocumentPage);
                writer.WriteBoolean("saveDocumentViewSettings", Settings.SaveDocumentViewSettings);
                WriteDocumentBookmarksJson(writer, Settings.DocumentBookmarks);
                WriteDocumentStatesJson(writer, Settings.DocumentStates);
                writer.WriteEndObject();
            }

            byte[] payload = ms.ToArray();
            string tempPath = SettingsFilePath + ".tmp";
            File.WriteAllBytes(tempPath, payload);
            File.Move(tempPath, SettingsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            try
            {
                using MemoryStream ms = new();
                using (Utf8JsonWriter writer = new(ms, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteString("theme", Settings.Theme.ToString());
                    writer.WriteString("defaultViewMode", Settings.DefaultViewMode.ToString());
                    writer.WriteNumber("defaultZoomIndex", Math.Clamp(Settings.DefaultZoomIndex, 0, 2));
                    writer.WriteBoolean("defaultTouchDrawing", Settings.DefaultTouchDrawing);
                    writer.WriteBoolean("reopenLastDocument", Settings.ReopenLastDocument);
                    if (!string.IsNullOrWhiteSpace(Settings.LastOpenedFilePath))
                    {
                        writer.WriteString("lastOpenedFilePath", Settings.LastOpenedFilePath);
                    }
                    else
                    {
                        writer.WriteNull("lastOpenedFilePath");
                    }

                    writer.WriteNumber("lastOpenedPageIndex", Math.Max(0, Settings.LastOpenedPageIndex));
                    writer.WriteBoolean("isSidebarOpen", Settings.IsSidebarOpen);
                    writer.WriteNumber("sidebarWidth", Math.Round(Math.Clamp(Settings.SidebarWidth, 220.0, 480.0), 1));
                    writer.WriteString("appLanguage", Settings.AppLanguage ?? string.Empty);
                    writer.WriteBoolean("rememberDocumentPage", Settings.RememberDocumentPage);
                    writer.WriteBoolean("saveDocumentViewSettings", Settings.SaveDocumentViewSettings);
                    WriteDocumentBookmarksJson(writer, Settings.DocumentBookmarks);
                    WriteDocumentStatesJson(writer, Settings.DocumentStates);
                    writer.WriteEndObject();
                }

                File.WriteAllBytes(SettingsFilePath, ms.ToArray());
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings.json: {ex.Message}");
            }
        }
    }

    /// <inheritdoc/>
    public bool TryGetDocumentState(string filePath, out SavedDocumentState? state)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            state = null;
            return false;
        }

        lock (_syncLock)
        {
            return Settings.DocumentStates.TryGetValue(filePath, out state);
        }
    }

    /// <inheritdoc/>
    public void UpdateDocumentState(
        string filePath,
        int? pageIndex,
        double? zoomFactor,
        ViewMode? viewMode = null,
        bool? hasCustomViewSettings = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        lock (_syncLock)
        {
            if (!Settings.DocumentStates.TryGetValue(filePath, out SavedDocumentState? state) || state is null)
            {
                state = new SavedDocumentState();
                Settings.DocumentStates[filePath] = state;
            }

            if (pageIndex.HasValue)
            {
                state.PageIndex = Math.Max(0, pageIndex.Value);
            }

            if (hasCustomViewSettings.HasValue)
            {
                state.HasCustomViewSettings = hasCustomViewSettings.Value;
            }

            if (zoomFactor.HasValue && double.IsFinite(zoomFactor.Value))
            {
                state.ZoomFactor = zoomFactor.Value;
            }

            if (viewMode.HasValue)
            {
                state.ViewMode = viewMode.Value;
            }

            state.LastOpenedUtc = DateTime.UtcNow;
            SaveInternal();
        }
    }

    private static void WriteDocumentBookmarksJson(Utf8JsonWriter writer, Dictionary<string, List<SavedBookmarkItem>>? documentBookmarks)
    {
        writer.WritePropertyName("documentBookmarks");
        writer.WriteStartObject();
        if (documentBookmarks is not null)
        {
            foreach (KeyValuePair<string, List<SavedBookmarkItem>> kvp in documentBookmarks)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value is not { Count: > 0 })
                {
                    continue;
                }

                writer.WritePropertyName(kvp.Key);
                writer.WriteStartArray();
                for (int i = 0; i < kvp.Value.Count; i++)
                {
                    SavedBookmarkItem item = kvp.Value[i];
                    writer.WriteStartObject();
                    writer.WriteNumber("pageIndex", Math.Max(0, item.PageIndex));
                    if (!string.IsNullOrWhiteSpace(item.CustomTitle))
                    {
                        writer.WriteString("customTitle", item.CustomTitle.Trim());
                    }
                    else
                    {
                        writer.WriteNull("customTitle");
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteDocumentStatesJson(Utf8JsonWriter writer, Dictionary<string, SavedDocumentState>? documentStates)
    {
        writer.WritePropertyName("documentStates");
        writer.WriteStartObject();
        if (documentStates is not null)
        {
            var ordered = documentStates
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                .OrderByDescending(kvp => kvp.Value.LastOpenedUtc)
                .Take(200);

            foreach (KeyValuePair<string, SavedDocumentState> kvp in ordered)
            {
                writer.WritePropertyName(kvp.Key);
                writer.WriteStartObject();
                writer.WriteNumber("pageIndex", Math.Max(0, kvp.Value.PageIndex));
                writer.WriteBoolean("hasCustomViewSettings", kvp.Value.HasCustomViewSettings);
                if (kvp.Value.HasCustomViewSettings && kvp.Value.ZoomFactor is { } z && double.IsFinite(z))
                {
                    writer.WriteNumber("zoomFactor", Math.Round(z, 4));
                }
                else
                {
                    writer.WriteNull("zoomFactor");
                }

                if (kvp.Value.HasCustomViewSettings && kvp.Value.ViewMode is { } vm)
                {
                    writer.WriteString("viewMode", vm.ToString());
                }
                else
                {
                    writer.WriteNull("viewMode");
                }

                writer.WriteString("lastOpenedUtc", kvp.Value.LastOpenedUtc.ToString("o"));
                writer.WriteEndObject();
            }
        }

        writer.WriteEndObject();
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (JsonProperty prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
