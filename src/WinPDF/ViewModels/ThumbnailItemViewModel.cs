using CommunityToolkit.Mvvm.ComponentModel;

namespace WinPDF.ViewModels;

/// <summary>
/// View model representing a page thumbnail item in the sidebar gallery.
/// </summary>
public sealed partial class ThumbnailItemViewModel : ObservableObject
{
    /// <summary>
    /// Gets the zero-based page index.
    /// </summary>
    public int PageIndex { get; }

    /// <summary>
    /// Gets the 1-based display label (e.g. "1", "2").
    /// </summary>
    public string DisplayNumber => (PageIndex + 1).ToString();

    /// <summary>
    /// Gets or sets a value indicating whether this thumbnail corresponds to the currently active page.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>
    /// Gets or sets the aspect ratio width for the thumbnail placeholder card.
    /// </summary>
    [ObservableProperty]
    public partial double WidthDip { get; set; } = 120;

    /// <summary>
    /// Gets or sets the aspect ratio height for the thumbnail placeholder card.
    /// </summary>
    [ObservableProperty]
    public partial double HeightDip { get; set; } = 160;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThumbnailItemViewModel"/> class.
    /// </summary>
    /// <param name="pageIndex">Zero-based index of the page.</param>
    /// <param name="widthDip">Display width in DIPs.</param>
    /// <param name="heightDip">Display height in DIPs.</param>
    public ThumbnailItemViewModel(int pageIndex, double widthDip = 120, double heightDip = 160)
    {
        PageIndex = pageIndex;
        WidthDip = widthDip;
        HeightDip = heightDip;
    }
}
