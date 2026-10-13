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
    private static readonly Lazy<ResourceLoader> Loader = new(() => new ResourceLoader(), isThreadSafe: true);

    /// <summary>
    /// Gets the localized string for the given resource key.
    /// </summary>
    /// <param name="key">Resource key (code keys use underscores, e.g. <c>Dialog_Ok</c>).</param>
    /// <returns>The localized string, or the key itself wrapped in brackets if it is missing.</returns>
    public static string Get(string key)
    {
        string value = Loader.Value.GetString(key);
        return string.IsNullOrEmpty(value) ? $"[{key}]" : value;
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
