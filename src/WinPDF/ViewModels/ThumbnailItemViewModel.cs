using CommunityToolkit.Mvvm.ComponentModel;
using WinPDF.Rendering;

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
    /// Gets or sets a value indicating whether this thumbnail is the currently active page in the document.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCurrentPage { get; set; }

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
    /// Gets the rendered thumbnail pixels, or <see langword="null"/> while the item is not realized or still loading.
    /// The view model holds one reference on the entry and releases it when the image is replaced or cleared.
    /// </summary>
    [ObservableProperty]
    public partial PdfBitmapEntry? Image { get; private set; }

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

    /// <summary>Gets or sets the document that renders this thumbnail on demand.</summary>
    internal DocumentViewModel? Owner { get; set; }

    /// <summary>Gets or sets a value indicating whether the sidebar list currently has a container for this item.</summary>
    internal bool IsRealized { get; set; }

    /// <summary>Gets or sets the cancellation source of the in-flight render, if any.</summary>
    internal CancellationTokenSource? LoadCancellation { get; set; }

    /// <summary>
    /// Called by the virtualized list when a container for this item is created or bound (<c>ContainerContentChanging</c>).
    /// Requests the thumbnail render, which only happens while the sidebar thumbnails tab is visible.
    /// </summary>
    /// <param name="rasterizationScale"><c>XamlRoot.RasterizationScale</c> of the host window.</param>
    public void NotifyRealized(double rasterizationScale) => Owner?.OnThumbnailRealized(this, rasterizationScale);

    /// <summary>
    /// Called by the virtualized list when the container is recycled or the list is unloaded. Cancels any pending
    /// render and drops the in-memory image.
    /// </summary>
    public void NotifyRecycled() => Owner?.OnThumbnailRecycled(this);

    /// <summary>Takes ownership of one reference on <paramref name="entry"/> and displays it.</summary>
    /// <param name="entry">A retained cache entry.</param>
    internal void SetImage(PdfBitmapEntry entry) => Image = entry;

    /// <summary>Releases the displayed image, if any.</summary>
    internal void ClearImage()
    {
        if (Image is not null)
        {
            Image = null;
        }
    }

    partial void OnImageChanged(PdfBitmapEntry? oldValue, PdfBitmapEntry? newValue) => oldValue?.Release();
}
