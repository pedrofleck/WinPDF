namespace WinPDF.Models;

/// <summary>
/// Document layout and pagination mode.
/// </summary>
public enum ViewMode
{
    /// <summary>
    /// Pages are laid out vertically in a continuous virtualized scroll viewer.
    /// </summary>
    Continuous,

    /// <summary>
    /// One page is displayed at a time with touch swipe and flip transitions.
    /// </summary>
    Paged
}
