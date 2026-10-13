namespace WinPDF.Models;

/// <summary>
/// Settings category sections available in the full-page Settings view.
/// </summary>
public enum SettingsCategory
{
    /// <summary>Application theme and visual appearance.</summary>
    Appearance,

    /// <summary>Default document viewing mode and initial zoom level.</summary>
    Viewing,

    /// <summary>Default pen, highlighter, and touch drawing preferences.</summary>
    Annotations,

    /// <summary>Application metadata, version, architecture, and open-source credits.</summary>
    About
}
