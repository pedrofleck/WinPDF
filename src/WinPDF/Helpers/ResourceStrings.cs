using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace WinPDF.Helpers;

/// <summary>
/// Thin, thread-safe wrapper around the MRT Core <see cref="ResourceLoader"/> used to read
/// localized strings from <c>Strings/&lt;language&gt;/Resources.resw</c> in C# code.
/// </summary>
/// <remarks>
/// XAML elements must use <c>x:Uid</c> instead of this class. Use this class only for strings
/// produced in code (dialogs, formatted titles, error messages). Keys are listed in
/// <see cref="ResourceKeys"/>.
/// </remarks>
public static class ResourceStrings
{
    private static readonly Lazy<ResourceManager> Manager = new(() => new ResourceManager(), isThreadSafe: true);
    private static readonly object s_contextLock = new();
    private static ResourceContext? s_cachedContext;
    private static string? s_cachedLanguage;

    /// <summary>
    /// Gets the localized string for the given resource key.
    /// </summary>
    /// <param name="key">Resource key (code keys use underscores, e.g. <c>Dialog_Ok</c>).</param>
    /// <returns>The localized string, or the key itself wrapped in brackets if it is missing.</returns>
    public static string Get(string key)
    {
        try
        {
            string overrideLang = Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride;
            ResourceContext? context = null;
            if (!string.IsNullOrEmpty(overrideLang))
            {
                lock (s_contextLock)
                {
                    if (s_cachedContext is null || !string.Equals(s_cachedLanguage, overrideLang, StringComparison.OrdinalIgnoreCase))
                    {
                        s_cachedContext = Manager.Value.CreateResourceContext();
                        s_cachedContext.QualifierValues["Language"] = overrideLang;
                        s_cachedLanguage = overrideLang;
                    }

                    context = s_cachedContext;
                }
            }

            ResourceCandidate? candidate = context is not null
                ? (Manager.Value.MainResourceMap.TryGetValue($"Resources/{key}", context) ?? Manager.Value.MainResourceMap.TryGetValue(key, context))
                : (Manager.Value.MainResourceMap.TryGetValue($"Resources/{key}") ?? Manager.Value.MainResourceMap.TryGetValue(key));

            string? value = candidate?.ValueAsString;
            return string.IsNullOrEmpty(value) ? $"[{key}]" : value;
        }
        catch
        {
            return $"[{key}]";
        }
    }

    /// <summary>
    /// Gets a localized composite format string and formats it with the current UI culture.
    /// </summary>
    /// <param name="key">Resource key of a composite format string (e.g. <c>Window_TitleFormat</c>).</param>
    /// <param name="args">Format arguments.</param>
    /// <returns>The formatted, localized string.</returns>
    public static string Format(string key, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), args);
    }
}
