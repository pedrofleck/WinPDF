namespace WinPDF.Models;

/// <summary>
/// Application visual theme preference options.
/// </summary>
public enum AppThemePreference
{
    /// <summary>
    /// Synchronizes with the current Windows system theme.
    /// </summary>
    System,

    /// <summary>
    /// Forces the Fluent light theme.
    /// </summary>
    Light,

    /// <summary>
    /// Forces the Fluent dark theme.
    /// </summary>
    Dark
}
