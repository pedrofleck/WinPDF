using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinPDF.Models;
using WinPDF.Rendering;
using WinPDF.Services;

namespace WinPDF.ViewModels;

/// <summary>
/// View model representing the active PDF document state, navigation, rotation, and collections.
/// </summary>
public sealed partial class DocumentViewModel : ObservableObject, IDisposable
{
    private const double DefaultMaxThumbnailWidthDip = 108.0;
    private const double DefaultMaxThumbnailHeightDip = 156.0;
    private const double MinThumbnailRenderWidthDip = 160.0;
    private const double MinThumbnailRenderHeightDip = 232.0;

    /// <summary>
    /// Gets or sets the absolute file path on disk.
    /// </summary>
    [ObservableProperty]
    public partial string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file name displayed in titles and headers.
    /// </summary>
    [ObservableProperty]
    public partial string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the total number of pages in the document.
    /// </summary>
    [ObservableProperty]
    public partial int PageCount { get; set; }

    /// <summary>
    /// Gets or sets the zero-based index of the currently viewed page.
    /// </summary>
    [ObservableProperty]
    public partial int CurrentPageIndex { get; set; }

    /// <summary>
    /// Gets or sets the 1-based display number of the current page.
    /// </summary>
    public int CurrentPageNumber
    {
        get => CurrentPageIndex + 1;
        set
        {
            int targetIndex = Math.Clamp(value - 1, 0, Math.Max(0, PageCount - 1));
            if (targetIndex != CurrentPageIndex)
            {
                CurrentPageIndex = targetIndex;
            }
        }
    }

    /// <summary>
    /// Gets or sets the badge representing the document format.
    /// </summary>
    [ObservableProperty]
    public partial string FileFormatBadge { get; set; } = "PDF";

    /// <summary>
    /// Gets a human-readable representation of the document file size (e.g. "845 KB", "2.4 MB").
    /// </summary>
    public string FormattedFileSize => FormatFileSize(FileSizeBytes);

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 KB";
        }

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F0} KB";
        }

        if (bytes < 1024 * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }

        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    /// <summary>
    /// Gets or sets a value indicating whether two-page / dual-page display mode is active.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDoublePage { get; set; }

    partial void OnIsDoublePageChanged(bool value)
    {
        UpdateDisplayedRows();
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gets or sets a value indicating whether continuous vertical scrolling is active.
    /// Synchronized with <see cref="ViewMode.Continuous"/> versus <see cref="ViewMode.Paged"/>.
    /// </summary>
    [ObservableProperty]
    public partial bool IsContinuous { get; set; } = true;

    partial void OnIsContinuousChanged(bool value)
    {
        UpdateDisplayedRows();
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gets or sets a value indicating whether slideshow presentation mode is active.
    /// </summary>
    [ObservableProperty]
    public partial bool IsPresentationMode { get; set; }

    partial void OnIsPresentationModeChanged(bool value)
    {
        UpdateDisplayedRows();
    }

    /// <summary>
    /// Event raised when view modes (Continuous or DoublePage) change, requesting viewport refitting.
    /// </summary>
    public event EventHandler? ModeChanged;

    /// <summary>
    /// Gets the rows of pages currently displayed in the unified document view.
    /// </summary>
    public ObservableCollection<PdfPageRow> DisplayedRows { get; } = new();

    /// <summary>
    /// Gets the minimum zoom factor supported (0.25x = 25%).
    /// </summary>
    public const double MinZoom = 0.25;

    /// <summary>
    /// Gets the maximum zoom factor supported (8.0x = 800%).
    /// </summary>
    public const double MaxZoom = 8.0;

    /// <summary>
    /// Gets or sets the current zoom factor (1.0 = 100%).
    /// </summary>
    [ObservableProperty]
    public partial double ZoomFactor { get; set; } = 1.0;

    partial void OnZoomFactorChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomPercentage));
    }

    /// <summary>
    /// Gets or sets the zoom percentage (25.0 to 500.0) bound to zoom sliders and percentage labels.
    /// </summary>
    public double ZoomPercentage
    {
        get => Math.Round(ZoomFactor * 100.0, 1);
        set
        {
            double factor = Math.Clamp(value / 100.0, 0.25, 8.0);
            if (Math.Abs(ZoomFactor - factor) > 0.001)
            {
                ZoomFactor = factor;
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether night/dark reading mode is active (inverting page colors on GPU).
    /// </summary>
    [ObservableProperty]
    public partial bool IsNightMode { get; set; }

    /// <summary>
    /// Gets or sets the clockwise rotation angle in degrees (0, 90, 180, 270).
    /// </summary>
    [ObservableProperty]
    public partial int Rotation { get; set; }

    partial void OnRotationChanged(int value)
    {
        UpdateDisplayedRows();
        RefreshThumbnailsForRotation();
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gets or sets a value indicating whether there are unsaved annotation changes.
    /// </summary>
    [ObservableProperty]
    public partial bool HasUnsavedChanges { get; set; }

    /// <summary>
    /// Gets or sets the file size in bytes for properties display.
    /// </summary>
    [ObservableProperty]
    public partial long FileSizeBytes { get; set; }

    partial void OnFileSizeBytesChanged(long value)
    {
        OnPropertyChanged(nameof(FormattedFileSize));
    }

    /// <summary>
    /// Gets or sets a value indicating whether the current page is pinned in user favorites/bookmarks.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCurrentPageBookmarked { get; set; }

    /// <summary>
    /// Gets the collection of page thumbnail items for sidebar gallery binding.
    /// </summary>
    public ObservableCollection<ThumbnailItemViewModel> Thumbnails { get; } = new();

    /// <summary>
    /// Gets the collection of top-level outline items for bookmark hierarchy binding.
    /// </summary>
    public ObservableCollection<OutlineItemViewModel> Outline { get; } = new();

    /// <summary>
    /// Gets the collection of user-pinned favorite pages.
    /// </summary>
    public ObservableCollection<BookmarkItemViewModel> Bookmarks { get; } = new();

    /// <summary>
    /// Gets the collection of annotation descriptions in the document.
    /// </summary>
    public ObservableCollection<AnnotationItemViewModel> AnnotationsSummary { get; } = new();

    /// <summary>
    /// Gets the collection of embedded file attachment names.
    /// </summary>
    public ObservableCollection<AttachmentItemViewModel> Attachments { get; } = new();

    /// <summary>
    /// Gets the engine-assigned id of the open PDFium document.
    /// </summary>
    public long DocumentId { get; }

    /// <summary>
    /// Gets the exact geometry of every page, in document order.
    /// </summary>
    public IReadOnlyList<PdfPageInfo> Pages { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentViewModel"/> class from an opened PDF document.
    /// </summary>
    /// <param name="pdfService">PDF engine used to render thumbnails and to close the document.</param>
    /// <param name="tileCache">Shared bitmap cache (thumbnails are cached here).</param>
    /// <param name="info">Description of the opened document (id, path and page geometry).</param>
    /// <param name="outline">Real table of contents read from the document.</param>
    /// <param name="attachmentNames">Names of the embedded file attachments.</param>
    public DocumentViewModel(
        IPdfService pdfService,
        TileCache tileCache,
        PdfDocumentInfo info,
        IReadOnlyList<OutlineItem> outline,
        IReadOnlyList<string> attachmentNames)
    {
        _pdfService = pdfService;
        _tileCache = tileCache;

        DocumentId = info.DocumentId;
        Pages = info.Pages;
        FilePath = info.FilePath;
        FileName = System.IO.Path.GetFileName(info.FilePath);
        PageCount = info.PageCount;
        CurrentPageIndex = 0;

        if (System.IO.File.Exists(info.FilePath))
        {
            try
            {
                FileSizeBytes = new System.IO.FileInfo(info.FilePath).Length;
            }
            catch
            {
                FileSizeBytes = 0;
            }
        }

        foreach (PdfPageInfo page in info.Pages)
        {
            (double widthDip, double heightDip) = FitThumbnail(page, Rotation);
            Thumbnails.Add(new ThumbnailItemViewModel(page.Index, widthDip, heightDip) { Owner = this });
        }

        if (Thumbnails.Count > 0)
        {
            Thumbnails[0].IsSelected = true;
            Thumbnails[0].IsCurrentPage = true;
        }

        foreach (OutlineItem item in outline)
        {
            Outline.Add(CreateOutlineViewModel(item));
        }

        foreach (string name in attachmentNames)
        {
            Attachments.Add(new AttachmentItemViewModel(name));
        }

        UpdateIsCurrentPageBookmarked();
        UpdateDisplayedRows();
    }

    private static OutlineItemViewModel CreateOutlineViewModel(OutlineItem item)
    {
        OutlineItemViewModel viewModel = new(item.Title, item.PageIndex);
        foreach (OutlineItem child in item.Children)
        {
            viewModel.Children.Add(CreateOutlineViewModel(child));
        }

        return viewModel;
    }

    /// <summary>
    /// Computes the displayed aspect ratio (<c>width / height</c>) of a PDF page, accounting for both its
    /// intrinsic <see cref="PdfPageInfo.Rotation"/> and optional user rotation in degrees.
    /// </summary>
    /// <param name="page">Page metadata.</param>
    /// <param name="userRotationDegrees">Extra user rotation in degrees (0, 90, 180, 270).</param>
    /// <returns>The positive displayed aspect ratio (<c>width / height</c>).</returns>
    public static double GetPageDisplayAspectRatio(PdfPageInfo page, int userRotationDegrees = 0)
    {
        ArgumentNullException.ThrowIfNull(page);

        int effectiveRotation = PageCoordinateTransform.NormalizeRotation(page.Rotation + userRotationDegrees);
        bool swap = effectiveRotation is 90 or 270;
        double widthPt = swap ? page.HeightPt : page.WidthPt;
        double heightPt = swap ? page.WidthPt : page.HeightPt;

        if (!double.IsFinite(widthPt) || !double.IsFinite(heightPt) || widthPt <= 0 || heightPt <= 0)
        {
            return 1.0 / Math.Sqrt(2.0);
        }

        return widthPt / heightPt;
    }

    /// <summary>
    /// Fits a page with the specified <paramref name="aspectRatio"/> (<c>width / height</c>) inside a bounding box
    /// of <paramref name="maxWidthDip"/> by <paramref name="maxHeightDip"/> while strictly preserving its aspect ratio.
    /// </summary>
    /// <param name="aspectRatio">Page aspect ratio (<c>width / height</c>).</param>
    /// <param name="maxWidthDip">Maximum card width in DIPs.</param>
    /// <param name="maxHeightDip">Maximum card height in DIPs.</param>
    /// <returns>The proportional width and height in DIPs.</returns>
    public static (double WidthDip, double HeightDip) ComputeThumbnailBox(double aspectRatio, double maxWidthDip, double maxHeightDip)
    {
        if (!double.IsFinite(aspectRatio) || aspectRatio <= 0.01)
        {
            aspectRatio = 1.0 / Math.Sqrt(2.0);
        }

        double maxW = Math.Max(24.0, maxWidthDip);
        double maxH = Math.Max(24.0, maxHeightDip);

        double w = maxW;
        double h = w / aspectRatio;
        if (h > maxH)
        {
            h = maxH;
            w = h * aspectRatio;
        }

        return (Math.Max(16.0, Math.Round(w)), Math.Max(16.0, Math.Round(h)));
    }

    // Fits the page's aspect ratio within the default thumbnail bounding box.
    private static (double Width, double Height) FitThumbnail(PdfPageInfo page, int userRotationDegrees = 0)
    {
        double aspect = GetPageDisplayAspectRatio(page, userRotationDegrees);
        return ComputeThumbnailBox(aspect, DefaultMaxThumbnailWidthDip, DefaultMaxThumbnailHeightDip);
    }

    private void RefreshThumbnailsForRotation()
    {
        for (int i = 0; i < Thumbnails.Count; i++)
        {
            ThumbnailItemViewModel item = Thumbnails[i];
            if (item.LoadCancellation is not null)
            {
                item.LoadCancellation.Cancel();
                item.LoadCancellation.Dispose();
                item.LoadCancellation = null;
            }

            if (i < Pages.Count)
            {
                // Swap current card dimensions if already sized by the sidebar, otherwise recompute from default box.
                double aspect = GetPageDisplayAspectRatio(Pages[i], Rotation);
                double maxW = Math.Max(DefaultMaxThumbnailWidthDip, Math.Max(item.WidthDip, item.HeightDip / 1.45));
                double maxH = Math.Round(maxW * 1.45);
                (double w, double h) = ComputeThumbnailBox(aspect, maxW, maxH);
                item.WidthDip = w;
                item.HeightDip = h;
            }

            item.ClearImage();
            if (_thumbnailsActive && item.IsRealized)
            {
                RequestThumbnail(item, _thumbnailScale);
            }
        }
    }

    private readonly IPdfService _pdfService;
    private readonly TileCache _tileCache;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _thumbnailsActive;
    private double _thumbnailScale = 1.0;
    private bool _isDisposed;

    /// <summary>
    /// Gets or sets the fractional page index currently visible at the center of the viewport (e.g. 3.5).
    /// </summary>
    [ObservableProperty]
    public partial double FractionalPageIndex { get; set; }

    /// <summary>
    /// Gets or sets the last horizontal scroll offset saved for state preservation.
    /// </summary>
    public double LastHorizontalOffset { get; set; }

    /// <summary>
    /// Gets or sets the last vertical scroll offset saved for state preservation.
    /// </summary>
    public double LastVerticalOffset { get; set; }

    partial void OnCurrentPageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentPageNumber));

        if (!IsContinuous)
        {
            UpdateDisplayedRows();
            if (Math.Abs(FractionalPageIndex - value) > 0.001)
            {
                FractionalPageIndex = value;
            }
        }

        for (int i = 0; i < Thumbnails.Count; i++)
        {
            bool isCur = (i == value);
            Thumbnails[i].IsSelected = isCur;
            Thumbnails[i].IsCurrentPage = isCur;
        }

        UpdateIsCurrentPageBookmarked();
    }

    private void UpdateIsCurrentPageBookmarked()
    {
        IsCurrentPageBookmarked = Bookmarks.Any(b => b.PageIndex == CurrentPageIndex);
    }

    /// <summary>
    /// Toggles the favorite / bookmark status of the currently active page.
    /// </summary>
    public void ToggleBookmarkCurrentPage()
    {
        var existing = Bookmarks.FirstOrDefault(b => b.PageIndex == CurrentPageIndex);
        if (existing is not null)
        {
            Bookmarks.Remove(existing);
        }
        else
        {
            int insertIndex = 0;
            while (insertIndex < Bookmarks.Count && Bookmarks[insertIndex].PageIndex < CurrentPageIndex)
            {
                insertIndex++;
            }
            Bookmarks.Insert(insertIndex, new BookmarkItemViewModel(CurrentPageIndex));
        }

        UpdateIsCurrentPageBookmarked();
    }

    /// <summary>
    /// Rotates the document view 90 degrees clockwise.
    /// </summary>
    public void RotateClockwise()
    {
        Rotation = (Rotation + 90) % 360;
    }

    /// <summary>
    /// Rotates the document view 90 degrees counter-clockwise.
    /// </summary>
    public void RotateCounterClockwise()
    {
        Rotation = (Rotation + 270) % 360;
    }

    /// <summary>
    /// Navigates to the specified zero-based page index.
    /// </summary>
    /// <param name="pageIndex">Target zero-based page index.</param>
    public void GoToPage(int pageIndex)
    {
        if (PageCount > 0)
        {
            int targetIndex = Math.Clamp(pageIndex, 0, PageCount - 1);
            if (CurrentPageIndex != targetIndex)
            {
                CurrentPageIndex = targetIndex;
            }
            else if (!IsContinuous)
            {
                UpdateDisplayedRows();
            }

            if (Math.Abs(FractionalPageIndex - targetIndex) > 0.001)
            {
                FractionalPageIndex = targetIndex;
            }
        }
    }

    /// <summary>
    /// Moves to the previous page (or pair of pages in double-page mode).
    /// </summary>
    public void PreviousPage()
    {
        int step = (!IsContinuous && IsDoublePage) ? 2 : 1;
        GoToPage(CurrentPageIndex - step);
    }

    /// <summary>
    /// Moves to the next page (or pair of pages in double-page mode).
    /// </summary>
    public void NextPage()
    {
        int step = (!IsContinuous && IsDoublePage) ? 2 : 1;
        GoToPage(CurrentPageIndex + step);
    }

    /// <summary>
    /// Updates the collection of displayed page rows based on current view modes and page index.
    /// </summary>
    public void UpdateDisplayedRows()
    {
        if (Pages.Count == 0)
        {
            DisplayedRows.Clear();
            return;
        }

        List<PdfPageRow> targetRows = new();

        if (IsContinuous)
        {
            if (IsDoublePage)
            {
                for (int i = 0; i < Pages.Count; i += 2)
                {
                    PdfPageInfo left = Pages[i];
                    PdfPageInfo? right = (i + 1 < Pages.Count) ? Pages[i + 1] : null;
                    targetRows.Add(new PdfPageRow(left, right, IsPresentationMode, Rotation, IsNightMode));
                }
            }
            else
            {
                for (int i = 0; i < Pages.Count; i++)
                {
                    targetRows.Add(new PdfPageRow(Pages[i], null, IsPresentationMode, Rotation, IsNightMode));
                }
            }
        }
        else
        {
            int cur = Math.Clamp(CurrentPageIndex, 0, Pages.Count - 1);
            if (IsDoublePage)
            {
                int leftIndex = cur;
                PdfPageInfo left = Pages[leftIndex];
                PdfPageInfo? right = (leftIndex + 1 < Pages.Count) ? Pages[leftIndex + 1] : null;
                targetRows.Add(new PdfPageRow(left, right, IsPresentationMode, Rotation, IsNightMode));
            }
            else
            {
                targetRows.Add(new PdfPageRow(Pages[cur], null, IsPresentationMode, Rotation, IsNightMode));
            }
        }

        bool isIdentical = (DisplayedRows.Count == targetRows.Count);
        if (isIdentical)
        {
            for (int i = 0; i < targetRows.Count; i++)
            {
                if (DisplayedRows[i].LeftPage.Index != targetRows[i].LeftPage.Index ||
                    DisplayedRows[i].RightPage?.Index != targetRows[i].RightPage?.Index ||
                    DisplayedRows[i].IsPresentationMode != targetRows[i].IsPresentationMode ||
                    DisplayedRows[i].Rotation != targetRows[i].Rotation ||
                    DisplayedRows[i].IsNightMode != targetRows[i].IsNightMode)
                {
                    isIdentical = false;
                    break;
                }
            }
        }

        if (!isIdentical)
        {
            DisplayedRows.Clear();
            foreach (var r in targetRows)
            {
                DisplayedRows.Add(r);
            }
        }
    }

    /// <summary>
    /// Event raised when fitting the document vertically to the current viewport is requested.
    /// </summary>
    public event Action? FitVerticalRequested;

    /// <summary>
    /// Event raised when fitting the document horizontally to the current viewport is requested.
    /// </summary>
    public event Action? FitHorizontalRequested;

    /// <summary>
    /// Fits the active page vertically within the viewing viewport.
    /// </summary>
    [RelayCommand]
    public void FitVertical()
    {
        if (FitVerticalRequested is not null)
        {
            FitVerticalRequested.Invoke();
        }
        else
        {
            ZoomFactor = 1.0;
        }
    }

    /// <summary>
    /// Fits the active page horizontally to the viewing viewport width.
    /// </summary>
    [RelayCommand]
    public void FitHorizontal()
    {
        if (FitHorizontalRequested is not null)
        {
            FitHorizontalRequested.Invoke();
        }
        else
        {
            ZoomFactor = 1.25;
        }
    }

    /// <summary>
    /// Multiplies the current zoom factor by 1.25.
    /// </summary>
    [RelayCommand]
    public void ZoomIn()
    {
        double newZoom = Math.Min(8.0, ZoomFactor * 1.25);
        if (Math.Abs(ZoomFactor - newZoom) > 0.0001)
        {
            ZoomFactor = newZoom;
        }
    }

    /// <summary>
    /// Divides the current zoom factor by 1.25.
    /// </summary>
    [RelayCommand]
    public void ZoomOut()
    {
        double newZoom = Math.Max(0.25, ZoomFactor / 1.25);
        if (Math.Abs(ZoomFactor - newZoom) > 0.0001)
        {
            ZoomFactor = newZoom;
        }
    }

    /// <summary>
    /// Sets an explicit zoom factor. Supports numeric and string parameter inputs (e.g. from XAML CommandParameter).
    /// </summary>
    /// <param name="parameter">The target zoom factor or string representation.</param>
    [RelayCommand]
    public void SetZoom(object? parameter)
    {
        double factor = 1.0;
        if (parameter is double d)
        {
            factor = d;
        }
        else if (parameter is float f)
        {
            factor = f;
        }
        else if (parameter is int i)
        {
            factor = i;
        }
        else if (parameter is string s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
        {
            factor = parsed;
        }

        double target = Math.Clamp(factor, 0.25, 8.0);
        if (Math.Abs(ZoomFactor - target) > 0.0001)
        {
            ZoomFactor = target;
        }
    }

    /// <summary>
    /// Activates or deactivates sidebar thumbnail rasterization. When deactivated, pending thumbnail jobs are cancelled immediately.
    /// </summary>
    /// <param name="active">Whether thumbnail loading is active.</param>
    /// <param name="scale">Display rasterization scale.</param>
    public void SetThumbnailsActive(bool active, double scale = 1.0)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_thumbnailsActive == active && Math.Abs(_thumbnailScale - scale) <= 0.01)
        {
            return;
        }

        _thumbnailsActive = active;
        _thumbnailScale = scale > 0 ? scale : 1.0;

        if (!active)
        {
            foreach (ThumbnailItemViewModel item in Thumbnails)
            {
                if (item.LoadCancellation is not null)
                {
                    item.LoadCancellation.Cancel();
                    item.LoadCancellation.Dispose();
                    item.LoadCancellation = null;
                }
            }
        }
        else
        {
            foreach (ThumbnailItemViewModel item in Thumbnails)
            {
                if (item.IsRealized && item.Image is null)
                {
                    RequestThumbnail(item, _thumbnailScale);
                }
            }
        }
    }

    internal void OnThumbnailRealized(ThumbnailItemViewModel item, double scale)
    {
        item.IsRealized = true;
        if (scale > 0)
        {
            _thumbnailScale = scale;
        }

        if (!_thumbnailsActive || _isDisposed)
        {
            return;
        }

        RequestThumbnail(item, _thumbnailScale);
    }

    internal void OnThumbnailRecycled(ThumbnailItemViewModel item)
    {
        item.IsRealized = false;
        if (item.LoadCancellation is not null)
        {
            item.LoadCancellation.Cancel();
            item.LoadCancellation.Dispose();
            item.LoadCancellation = null;
        }

        item.ClearImage();
    }

    private void RequestThumbnail(ThumbnailItemViewModel item, double scale)
    {
        if (item.Image is not null || item.LoadCancellation is not null || _isDisposed)
        {
            return;
        }

        int effectiveRot = PageCoordinateTransform.NormalizeRotation(Rotation);

        TileKey key = new(
            DocumentId,
            item.PageIndex,
            TileKind.Thumbnail,
            ZoomStep: 0,
            TileX: 0,
            TileY: 0,
            RenderFlags: (int)PdfRenderFlags.Annotations,
            Rotation: effectiveRot,
            ScalePercent: (int)Math.Round(scale * 100));

        if (_tileCache.TryGet(in key, out TileCacheEntry? cachedEntry))
        {
            if (cachedEntry is PdfBitmapEntry bitmapEntry)
            {
                item.SetImage(bitmapEntry);
                return;
            }

            cachedEntry?.Release();
        }

        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        item.LoadCancellation = cts;

        double aspect = (item.PageIndex >= 0 && item.PageIndex < Pages.Count)
            ? GetPageDisplayAspectRatio(Pages[item.PageIndex], effectiveRot)
            : (item.WidthDip / Math.Max(1.0, item.HeightDip));

        double targetMaxW = Math.Max(MinThumbnailRenderWidthDip, item.WidthDip);
        double targetMaxH = Math.Max(MinThumbnailRenderHeightDip, item.HeightDip);
        (double renderDipW, double renderDipH) = ComputeThumbnailBox(aspect, targetMaxW, targetMaxH);

        int widthPx = Math.Max(1, (int)Math.Round(renderDipW * scale));
        int heightPx = Math.Max(1, (int)Math.Round(renderDipH * scale));
        PdfRenderRequest request = PdfRenderRequest.ForThumbnail(DocumentId, item.PageIndex, widthPx, heightPx, effectiveRot);

        _ = LoadThumbnailAsync(item, key, request, cts);
    }

    private async Task LoadThumbnailAsync(
        ThumbnailItemViewModel item,
        TileKey key,
        PdfRenderRequest request,
        CancellationTokenSource cts)
    {
        try
        {
            PdfBitmapLease lease = await _pdfService.RenderAsync(
                request,
                PdfJobPriority.Thumbnail,
                cts.Token).ConfigureAwait(false);

            if (_isDisposed || cts.IsCancellationRequested || !item.IsRealized || !_thumbnailsActive)
            {
                lease.Dispose();
                return;
            }

            PdfBitmapEntry entry = new(lease);
            _tileCache.Add(in key, entry);
            item.SetImage(entry);
        }
        catch (OperationCanceledException)
        {
            // Expected when scrolling past or closing the sidebar.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to render thumbnail for page {item.PageIndex}: {ex.Message}");
        }
        finally
        {
            if (item.LoadCancellation == cts)
            {
                item.LoadCancellation = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Releases all native document handles, cancels background rendering, and clears thumbnail memory.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();

        foreach (ThumbnailItemViewModel item in Thumbnails)
        {
            if (item.LoadCancellation is not null)
            {
                item.LoadCancellation.Cancel();
                item.LoadCancellation.Dispose();
                item.LoadCancellation = null;
            }

            item.ClearImage();
        }

        _tileCache.RemoveDocument(DocumentId);
        _ = _pdfService.CloseDocumentAsync(DocumentId);

        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: true);
    }
}
