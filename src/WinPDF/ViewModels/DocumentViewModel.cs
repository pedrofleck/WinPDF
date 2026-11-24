using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinPDF.Helpers;
using WinPDF.Models;
using WinPDF.Rendering;
using WinPDF.Services;

namespace WinPDF.ViewModels;

/// <summary>
/// View model representing the active PDF document state, navigation, rotation, text selection, search, and collections.
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
    [NotifyPropertyChangedFor(nameof(DisplayFileName))]
    public partial string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets the document file name prefixed with an unsaved indicator dot when dirty (tab-ready).
    /// </summary>
    public string DisplayFileName => HasUnsavedChanges ? $"• {FileName}" : FileName;

    /// <summary>
    /// Gets or sets the total number of pages in the document.
    /// </summary>
    [ObservableProperty]
    public partial int PageCount { get; set; }

    /// <summary>
    /// Event raised when the user initiates a scroll or navigation interaction directly on the document viewport.
    /// </summary>
    public event Action? UserDocumentScrollInitiated;

    /// <summary>
    /// Notifies listeners that the user has started scrolling the document viewport directly.
    /// </summary>
    public void NotifyUserDocumentScrollInitiated()
    {
        UserDocumentScrollInitiated?.Invoke();
    }

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
    /// Gets or sets the document metadata (title, author, producer, etc.).
    /// </summary>
    [ObservableProperty]
    public partial PdfMetadata? Metadata { get; set; }

    partial void OnMetadataChanged(PdfMetadata? value)
    {
        OnPropertyChanged(nameof(HasAdditionalMetadata));
        OnPropertyChanged(nameof(HasMetaPdfVersion));
        OnPropertyChanged(nameof(MetaPdfVersion));
        OnPropertyChanged(nameof(HasMetaTitle));
        OnPropertyChanged(nameof(MetaTitle));
        OnPropertyChanged(nameof(HasMetaAuthor));
        OnPropertyChanged(nameof(MetaAuthor));
        OnPropertyChanged(nameof(HasMetaSubject));
        OnPropertyChanged(nameof(MetaSubject));
        OnPropertyChanged(nameof(HasMetaKeywords));
        OnPropertyChanged(nameof(MetaKeywords));
        OnPropertyChanged(nameof(HasMetaCreator));
        OnPropertyChanged(nameof(MetaCreator));
        OnPropertyChanged(nameof(HasMetaProducer));
        OnPropertyChanged(nameof(MetaProducer));
    }

    /// <summary>Gets a value indicating whether at least one PDF metadata field is present.</summary>
    public bool HasAdditionalMetadata =>
        HasMetaPdfVersion || HasMetaTitle || HasMetaAuthor || HasMetaSubject ||
        HasMetaKeywords || HasMetaCreator || HasMetaProducer;

    /// <summary>Gets a value indicating whether the PDF version string is available.</summary>
    public bool HasMetaPdfVersion => !string.IsNullOrWhiteSpace(Metadata?.PdfVersion);

    /// <summary>Gets the non-null PDF version string.</summary>
    public string MetaPdfVersion => Metadata?.PdfVersion ?? string.Empty;

    /// <summary>Gets a value indicating whether the document title metadata is available.</summary>
    public bool HasMetaTitle => !string.IsNullOrWhiteSpace(Metadata?.Title);

    /// <summary>Gets the non-null document title metadata string.</summary>
    public string MetaTitle => Metadata?.Title ?? string.Empty;

    /// <summary>Gets a value indicating whether the document author metadata is available.</summary>
    public bool HasMetaAuthor => !string.IsNullOrWhiteSpace(Metadata?.Author);

    /// <summary>Gets the non-null document author metadata string.</summary>
    public string MetaAuthor => Metadata?.Author ?? string.Empty;

    /// <summary>Gets a value indicating whether the document subject metadata is available.</summary>
    public bool HasMetaSubject => !string.IsNullOrWhiteSpace(Metadata?.Subject);

    /// <summary>Gets the non-null document subject metadata string.</summary>
    public string MetaSubject => Metadata?.Subject ?? string.Empty;

    /// <summary>Gets a value indicating whether the document keywords metadata is available.</summary>
    public bool HasMetaKeywords => !string.IsNullOrWhiteSpace(Metadata?.Keywords);

    /// <summary>Gets the non-null document keywords metadata string.</summary>
    public string MetaKeywords => Metadata?.Keywords ?? string.Empty;

    /// <summary>Gets a value indicating whether the creation application metadata is available.</summary>
    public bool HasMetaCreator => !string.IsNullOrWhiteSpace(Metadata?.Creator);

    /// <summary>Gets the non-null creation application metadata string.</summary>
    public string MetaCreator => Metadata?.Creator ?? string.Empty;

    /// <summary>Gets a value indicating whether the PDF producer metadata is available.</summary>
    public bool HasMetaProducer => !string.IsNullOrWhiteSpace(Metadata?.Producer);

    /// <summary>Gets the non-null PDF producer metadata string.</summary>
    public string MetaProducer => Metadata?.Producer ?? string.Empty;

    /// <summary>
    /// Refreshes file system size, dates, and location properties after saving or renaming the document.
    /// </summary>
    public void RefreshFileInfo()
    {
        if (System.IO.File.Exists(FilePath))
        {
            try
            {
                FileSizeBytes = new System.IO.FileInfo(FilePath).Length;
            }
            catch
            {
            }
        }

        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(DirectoryPath));
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(FormattedFileSize));
        OnPropertyChanged(nameof(FileSizeDetailed));
        OnPropertyChanged(nameof(FileCreationDateFormatted));
        OnPropertyChanged(nameof(FileModificationDateFormatted));
        OnPropertyChanged(nameof(FileModificationDateOnlyFormatted));
        OnPropertyChanged(nameof(FileModificationTimeOnlyFormatted));
    }

    /// <summary>
    /// Gets the full directory path where the document file resides.
    /// </summary>
    public string DirectoryPath =>
        string.IsNullOrEmpty(FilePath) ? string.Empty : (System.IO.Path.GetDirectoryName(FilePath) ?? string.Empty);

    /// <summary>
    /// Gets the parent directory folder name for concise location display.
    /// </summary>
    public string FolderName
    {
        get
        {
            if (string.IsNullOrEmpty(FilePath))
            {
                return string.Empty;
            }

            string? dir = System.IO.Path.GetDirectoryName(FilePath);
            return string.IsNullOrEmpty(dir) ? string.Empty : (System.IO.Path.GetFileName(dir) ?? dir);
        }
    }

    /// <summary>
    /// Gets the creation date and time formatted in standard display format.
    /// </summary>
    public string FileCreationDateFormatted
    {
        get
        {
            if (System.IO.File.Exists(FilePath))
            {
                try
                {
                    return System.IO.File.GetCreationTime(FilePath).ToString("g");
                }
                catch
                {
                    // Fallback to metadata
                }
            }

            return Metadata?.CreationDate ?? string.Empty;
        }
    }

    /// <summary>
    /// Gets the modification date and time formatted in standard display format.
    /// </summary>
    public string FileModificationDateFormatted
    {
        get
        {
            if (System.IO.File.Exists(FilePath))
            {
                try
                {
                    return System.IO.File.GetLastWriteTime(FilePath).ToString("g");
                }
                catch
                {
                    // Fallback to metadata
                }
            }

            return Metadata?.ModificationDate ?? string.Empty;
        }
    }

    /// <summary>
    /// Gets the localized long modification date (e.g. "12 de outubro de 2026") matching Windows Photos style.
    /// </summary>
    public string FileModificationDateOnlyFormatted
    {
        get
        {
            if (System.IO.File.Exists(FilePath))
            {
                try
                {
                    return System.IO.File.GetLastWriteTime(FilePath).ToString("D");
                }
                catch
                {
                }
            }

            return Metadata?.ModificationDate ?? string.Empty;
        }
    }

    /// <summary>
    /// Gets the localized modification time (e.g. "13:48") matching Windows Photos style.
    /// </summary>
    public string FileModificationTimeOnlyFormatted
    {
        get
        {
            if (System.IO.File.Exists(FilePath))
            {
                try
                {
                    return System.IO.File.GetLastWriteTime(FilePath).ToString("t");
                }
                catch
                {
                }
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// Gets a detailed file size string including byte count (e.g. "3.3 MB (3,456,789 bytes)").
    /// </summary>
    public string FileSizeDetailed =>
        FileSizeBytes > 0 ? $"{FormattedFileSize} ({FileSizeBytes:N0} bytes)" : FormattedFileSize;

    /// <summary>
    /// Gets the dimensions of the currently active page in points and millimeters with standard paper size detection.
    /// </summary>
    public string ActivePageDimensionsFormatted
    {
        get
        {
            if (Pages.Count == 0 || CurrentPageIndex < 0 || CurrentPageIndex >= Pages.Count)
            {
                return string.Empty;
            }

            PdfPageInfo page = Pages[CurrentPageIndex];
            int effectiveRotation = PageCoordinateTransform.NormalizeRotation(page.Rotation + Rotation);
            bool swap = effectiveRotation is 90 or 270;
            double widthPt = swap ? page.HeightPt : page.WidthPt;
            double heightPt = swap ? page.WidthPt : page.HeightPt;

            double widthMm = widthPt * 25.4 / 72.0;
            double heightMm = heightPt * 25.4 / 72.0;

            string standardName = DetectPaperSize(widthMm, heightMm);
            if (!string.IsNullOrEmpty(standardName))
            {
                return $"{standardName} ({widthMm:F0} × {heightMm:F0} mm) • {widthPt:F0} × {heightPt:F0} pt";
            }

            return $"{widthMm:F0} × {heightMm:F0} mm • {widthPt:F0} × {heightPt:F0} pt";
        }
    }

    private static string DetectPaperSize(double wMm, double hMm)
    {
        double min = Math.Min(wMm, hMm);
        double max = Math.Max(wMm, hMm);

        const double tol = 3.5;

        if (Math.Abs(min - 210) <= tol && Math.Abs(max - 297) <= tol)
        {
            return "A4";
        }

        if (Math.Abs(min - 297) <= tol && Math.Abs(max - 420) <= tol)
        {
            return "A3";
        }

        if (Math.Abs(min - 148) <= tol && Math.Abs(max - 210) <= tol)
        {
            return "A5";
        }

        if (Math.Abs(min - 215.9) <= tol && Math.Abs(max - 279.4) <= tol)
        {
            return "Letter";
        }

        if (Math.Abs(min - 215.9) <= tol && Math.Abs(max - 355.6) <= tol)
        {
            return "Legal";
        }

        if (Math.Abs(min - 279.4) <= tol && Math.Abs(max - 431.8) <= tol)
        {
            return "Tabloid";
        }

        if (Math.Abs(min - 184.1) <= tol && Math.Abs(max - 266.7) <= tol)
        {
            return "Executive";
        }

        return string.Empty;
    }

    /// <summary>
    /// Gets or sets a value indicating whether two-page / dual-page display mode is active.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDoublePage { get; set; }

    partial void OnIsDoublePageChanged(bool value)
    {
        UpdateDisplayedRows();
        UpdatePageCacheCapacity();
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
        int preservedPage = Math.Clamp(CurrentPageIndex, 0, Math.Max(0, PageCount - 1));
        UpdateDisplayedRows();
        if (CurrentPageIndex != preservedPage)
        {
            CurrentPageIndex = preservedPage;
        }

        if (Math.Abs(FractionalPageIndex - preservedPage) > 0.001)
        {
            FractionalPageIndex = preservedPage;
        }

        ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gets or sets a value indicating whether slideshow presentation mode is active.
    /// </summary>
    [ObservableProperty]
    public partial bool IsPresentationMode { get; set; }

    partial void OnIsPresentationModeChanged(bool value)
    {
        for (int i = 0; i < DisplayedRows.Count; i++)
        {
            DisplayedRows[i].IsPresentationMode = value;
        }

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

    partial void OnIsNightModeChanged(bool value)
    {
        for (int i = 0; i < DisplayedRows.Count; i++)
        {
            DisplayedRows[i].IsNightMode = value;
        }

        for (int i = 0; i < Thumbnails.Count; i++)
        {
            Thumbnails[i].IsNightMode = value;
        }
    }

    /// <summary>
    /// Gets or sets the clockwise rotation angle in degrees (0, 90, 180, 270).
    /// </summary>
    [ObservableProperty]
    public partial int Rotation { get; set; }

    partial void OnRotationChanged(int value)
    {
        UpdateDisplayedRows();
        RefreshThumbnailsForRotation();
        OnPropertyChanged(nameof(ActivePageDimensionsFormatted));
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gets or sets a value indicating whether there are unsaved annotation changes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayFileName))]
    public partial bool HasUnsavedChanges { get; set; }

    /// <summary>
    /// Gets or sets the file size in bytes for properties display.
    /// </summary>
    [ObservableProperty]
    public partial long FileSizeBytes { get; set; }

    partial void OnFileSizeBytesChanged(long value)
    {
        OnPropertyChanged(nameof(FormattedFileSize));
        OnPropertyChanged(nameof(FileSizeDetailed));
        OnPropertyChanged(nameof(IsHeavyDocument));
        UpdatePageCacheCapacity();
    }

    /// <summary>
    /// Gets a value indicating whether the current document is classified as heavy (e.g. large file size or scanned).
    /// </summary>
    public bool IsHeavyDocument => FileSizeBytes > 25L * 1024 * 1024 || (Pages.Count > 0 && FileSizeBytes > 0 && (FileSizeBytes / Pages.Count) > 1.5 * 1024 * 1024);

    /// <summary>
    /// Updates the PDFium page handle cache capacity based on document heaviness and view mode.
    /// </summary>
    public void UpdatePageCacheCapacity()
    {
        // Double page: 8 pages. Single page: 4 pages (if heavy) or 6 pages (if normal).
        int targetCapacity = IsDoublePage ? 8 : (IsHeavyDocument ? 4 : 6);
        _ = _pdfService.SetPageCacheCapacityAsync(targetCapacity);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the current page is pinned in user favorites/bookmarks.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCurrentPageBookmarked { get; set; }

    // ------------------------------------------------------------------------------------------
    // Text Selection & Document Search State
    // ------------------------------------------------------------------------------------------

    private readonly Dictionary<int, List<PdfSearchMatch>> _searchMatchesByPage = new();
    private IReadOnlyList<PdfSearchMatch> _searchMatches = Array.Empty<PdfSearchMatch>();
    private CancellationTokenSource? _searchCts;
    private string _lastSearchedQuery = string.Empty;
    private bool _lastSearchedMatchCase;
    private bool _hasCompletedSearchForCurrentQuery;

    /// <summary>
    /// Event raised when GPU highlight overlays (selection or search matches) should be redrawn.
    /// Passes <c>-1</c> to invalidate all realized pages, or a zero-based page index for a single page.
    /// </summary>
    public event Action<int>? PageOverlaysInvalidated;

    /// <summary>
    /// Event raised when the viewport should scroll to center the active search match.
    /// </summary>
    public event Action<PdfSearchMatch>? ActiveSearchMatchScrollRequested;

    /// <summary>
    /// Gets or sets the active interactive text selection on the document.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTextSelection))]
    public partial PdfTextSelection? ActiveSelection { get; set; }

    /// <summary>
    /// Gets a value indicating whether non-empty text is currently selected on a page.
    /// </summary>
    public bool HasTextSelection => ActiveSelection is { HasContent: true };

    partial void OnActiveSelectionChanged(PdfTextSelection? oldValue, PdfTextSelection? newValue)
    {
        if (oldValue is null && newValue is null)
        {
            return;
        }

        if (oldValue is not null &&
            newValue is not null &&
            !oldValue.IsMultiPage &&
            !newValue.IsMultiPage &&
            oldValue.StartPageIndex == newValue.StartPageIndex)
        {
            PageOverlaysInvalidated?.Invoke(newValue.StartPageIndex);
            return;
        }

        int oldStart = oldValue?.StartPageIndex ?? -1;
        int oldEnd = oldValue?.LastPageIndex ?? -1;
        int newStart = newValue?.StartPageIndex ?? -1;
        int newEnd = newValue?.LastPageIndex ?? -1;

        int minPage = (oldStart >= 0 && newStart >= 0) ? Math.Min(oldStart, newStart) : Math.Max(oldStart, newStart);
        int maxPage = Math.Max(oldEnd, newEnd);
        if (minPage >= 0 && maxPage >= minPage && (maxPage - minPage + 1) > 12)
        {
            PageOverlaysInvalidated?.Invoke(-1);
            return;
        }

        if (oldStart >= 0 && oldEnd >= oldStart)
        {
            for (int p = oldStart; p <= oldEnd; p++)
            {
                PageOverlaysInvalidated?.Invoke(p);
            }
        }

        if (newStart >= 0 && newEnd >= newStart)
        {
            for (int p = newStart; p <= newEnd; p++)
            {
                if (oldStart < 0 || p < oldStart || p > oldEnd)
                {
                    PageOverlaysInvalidated?.Invoke(p);
                }
            }
        }
    }

    /// <summary>
    /// Updates the active text selection, or clears it if <paramref name="selection"/> is null or empty.
    /// </summary>
    /// <param name="selection">The new selection.</param>
    public void SetSelection(PdfTextSelection? selection)
    {
        ActiveSelection = selection is { HasContent: true } ? selection : null;
    }

    /// <summary>
    /// Clears any active text selection on the document.
    /// </summary>
    public void ClearSelection()
    {
        if (ActiveSelection is not null)
        {
            ActiveSelection = null;
        }
    }

    /// <summary>
    /// Gets or sets the current search query string for this document.
    /// </summary>
    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether document search is case-sensitive.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSearchMatchCase { get; set; }

    /// <summary>
    /// Gets or sets the zero-based index of the currently focused search match in <see cref="SearchMatches"/>, or -1 if none.
    /// </summary>
    [ObservableProperty]
    public partial int ActiveSearchMatchIndex { get; set; } = -1;

    /// <summary>
    /// Gets or sets the total number of search matches found across the document.
    /// </summary>
    [ObservableProperty]
    public partial int TotalSearchMatches { get; set; }

    /// <summary>
    /// Gets or sets the formatted match counter text (e.g. "1 of 12" or "0 of 0").
    /// </summary>
    [ObservableProperty]
    public partial string SearchStatusText { get; set; } = string.Empty;

    /// <summary>
    /// Gets the list of all search matches found in the document for the latest completed search.
    /// </summary>
    public IReadOnlyList<PdfSearchMatch> SearchMatches => _searchMatches;

    /// <summary>
    /// Gets the currently active search match, or <see langword="null"/> if none is active.
    /// </summary>
    public PdfSearchMatch? ActiveSearchMatch =>
        (ActiveSearchMatchIndex >= 0 && ActiveSearchMatchIndex < _searchMatches.Count)
            ? _searchMatches[ActiveSearchMatchIndex]
            : null;

    /// <summary>
    /// Returns the search matches located on <paramref name="pageIndex"/> in O(1) time.
    /// </summary>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <returns>The matches on that page, or an empty list if none.</returns>
    public IReadOnlyList<PdfSearchMatch> GetPageSearchMatches(int pageIndex)
    {
        return _searchMatchesByPage.TryGetValue(pageIndex, out List<PdfSearchMatch>? list)
            ? list
            : Array.Empty<PdfSearchMatch>();
    }

    /// <summary>
    /// Executes a background full-document text search on the PDFium thread and updates GPU highlight overlays.
    /// </summary>
    /// <param name="query">Query string to search for.</param>
    /// <param name="matchCase">Whether the search should be case-sensitive.</param>
    /// <param name="jumpToNearestMatch">Whether to automatically scroll the viewport to the nearest match.</param>
    /// <returns>A task representing the asynchronous search operation.</returns>
    public async Task ExecuteSearchAsync(string query, bool matchCase, bool jumpToNearestMatch = true)
    {
        if (_isDisposed)
        {
            return;
        }

        SearchQuery = query;
        IsSearchMatchCase = matchCase;

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        if (string.IsNullOrEmpty(query))
        {
            ClearSearchResults();
            return;
        }

        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _searchCts = cts;

        try
        {
            IReadOnlyList<PdfSearchMatch> results = await _pdfService.SearchDocumentAsync(
                DocumentId,
                query,
                matchCase,
                cts.Token);

            if (_isDisposed || cts.IsCancellationRequested)
            {
                return;
            }

            _searchMatches = results;
            _searchMatchesByPage.Clear();

            for (int i = 0; i < results.Count; i++)
            {
                PdfSearchMatch match = results[i];
                if (!_searchMatchesByPage.TryGetValue(match.PageIndex, out List<PdfSearchMatch>? pageList))
                {
                    pageList = new List<PdfSearchMatch>(4);
                    _searchMatchesByPage[match.PageIndex] = pageList;
                }

                pageList.Add(match);
            }

            _lastSearchedQuery = query;
            _lastSearchedMatchCase = matchCase;
            _hasCompletedSearchForCurrentQuery = true;
            TotalSearchMatches = results.Count;

            if (results.Count > 0)
            {
                int targetIndex = 0;
                int curPage = CurrentPageIndex;
                for (int i = 0; i < results.Count; i++)
                {
                    if (results[i].PageIndex >= curPage)
                    {
                        targetIndex = i;
                        break;
                    }
                }

                ActiveSearchMatchIndex = targetIndex;
                SearchStatusText = ResourceStrings.Format(ResourceKeys.SearchMatchCountFormat, targetIndex + 1, results.Count);
            }
            else
            {
                ActiveSearchMatchIndex = -1;
                SearchStatusText = ResourceStrings.Format(ResourceKeys.SearchMatchCountFormat, 0, 0);
            }

            PageOverlaysInvalidated?.Invoke(-1);

            if (jumpToNearestMatch && ActiveSearchMatch is { } activeMatch)
            {
                ActiveSearchMatchScrollRequested?.Invoke(activeMatch);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Document search failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Advances to the next search match (wrapping around to the first match) and scrolls it into view.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task FindNextMatchAsync()
    {
        if (_isDisposed || string.IsNullOrEmpty(SearchQuery))
        {
            return;
        }

        if (!_hasCompletedSearchForCurrentQuery ||
            !string.Equals(_lastSearchedQuery, SearchQuery, StringComparison.Ordinal) ||
            _lastSearchedMatchCase != IsSearchMatchCase)
        {
            await ExecuteSearchAsync(SearchQuery, IsSearchMatchCase, jumpToNearestMatch: true);
            return;
        }

        if (_searchMatches.Count == 0)
        {
            return;
        }

        int oldPage = ActiveSearchMatch?.PageIndex ?? -1;
        ActiveSearchMatchIndex = (ActiveSearchMatchIndex + 1) % _searchMatches.Count;
        SearchStatusText = ResourceStrings.Format(ResourceKeys.SearchMatchCountFormat, ActiveSearchMatchIndex + 1, _searchMatches.Count);

        PdfSearchMatch active = _searchMatches[ActiveSearchMatchIndex];
        if (oldPage >= 0 && oldPage != active.PageIndex)
        {
            PageOverlaysInvalidated?.Invoke(oldPage);
        }

        PageOverlaysInvalidated?.Invoke(active.PageIndex);
        ActiveSearchMatchScrollRequested?.Invoke(active);
    }

    /// <summary>
    /// Moves to the previous search match (wrapping around to the last match) and scrolls it into view.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task FindPreviousMatchAsync()
    {
        if (_isDisposed || string.IsNullOrEmpty(SearchQuery))
        {
            return;
        }

        if (!_hasCompletedSearchForCurrentQuery ||
            !string.Equals(_lastSearchedQuery, SearchQuery, StringComparison.Ordinal) ||
            _lastSearchedMatchCase != IsSearchMatchCase)
        {
            await ExecuteSearchAsync(SearchQuery, IsSearchMatchCase, jumpToNearestMatch: true);
            return;
        }

        if (_searchMatches.Count == 0)
        {
            return;
        }

        int oldPage = ActiveSearchMatch?.PageIndex ?? -1;
        ActiveSearchMatchIndex = (ActiveSearchMatchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
        SearchStatusText = ResourceStrings.Format(ResourceKeys.SearchMatchCountFormat, ActiveSearchMatchIndex + 1, _searchMatches.Count);

        PdfSearchMatch active = _searchMatches[ActiveSearchMatchIndex];
        if (oldPage >= 0 && oldPage != active.PageIndex)
        {
            PageOverlaysInvalidated?.Invoke(oldPage);
        }

        PageOverlaysInvalidated?.Invoke(active.PageIndex);
        ActiveSearchMatchScrollRequested?.Invoke(active);
    }

    /// <summary>
    /// Cancels any active search and clears all search match highlights from the document.
    /// </summary>
    public void ClearSearchResults()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        bool hadMatches = _searchMatches.Count > 0;
        _searchMatches = Array.Empty<PdfSearchMatch>();
        _searchMatchesByPage.Clear();
        _lastSearchedQuery = string.Empty;
        _hasCompletedSearchForCurrentQuery = false;
        ActiveSearchMatchIndex = -1;
        TotalSearchMatches = 0;
        SearchStatusText = string.Empty;

        if (hadMatches)
        {
            PageOverlaysInvalidated?.Invoke(-1);
        }
    }

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
    /// Gets the collection of digital signatures embedded in the document.
    /// </summary>
    public ObservableCollection<SignatureItemViewModel> Signatures { get; } = new();

    private readonly Dictionary<int, List<PdfAnnotationInfo>> _annotationsByPage = new();

    /// <summary>
    /// Event raised when rendered page bitmaps must be invalidated and re-rasterized because annotations changed.
    /// Passes <c>-1</c> to invalidate all realized pages, or a zero-based page index.
    /// </summary>
    public event Action<int>? PageContentInvalidated;

    /// <summary>
    /// Event raised when a user selects an annotation in the sidebar or elsewhere and wants the viewport
    /// to focus it and open its comment editor.
    /// </summary>
    public event Action<PdfAnnotationInfo>? AnnotationFocusRequested;

    /// <summary>
    /// Event raised when a user asks to inspect a specific digital signature.
    /// </summary>
    public event Action<PdfSignatureInfo>? SignatureInspectRequested;

    /// <summary>
    /// Gets or sets a value indicating whether the document contains one or more digital signatures.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverallSignatureStatus))]
    [NotifyPropertyChangedFor(nameof(SignatureStatusGlyph))]
    [NotifyPropertyChangedFor(nameof(SignatureStatusTooltip))]
    public partial bool HasSignatures { get; set; }

    /// <summary>
    /// Gets the overall verification status across all digital signatures in the document.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignatureStatusGlyph))]
    public partial PdfSignatureVerificationStatus OverallSignatureStatus { get; set; } = PdfSignatureVerificationStatus.Unknown;

    /// <summary>
    /// Gets or sets the tooltip text summarizing signature verification for the status bar indicator.
    /// </summary>
    [ObservableProperty]
    public partial string SignatureStatusTooltip { get; set; } = string.Empty;

    /// <summary>
    /// Gets the Segoe Fluent Icons glyph representing <see cref="OverallSignatureStatus"/>.
    /// </summary>
    public string SignatureStatusGlyph => OverallSignatureStatus switch
    {
        PdfSignatureVerificationStatus.Valid or PdfSignatureVerificationStatus.ValidUntrustedRoot => "\uE73E",
        PdfSignatureVerificationStatus.ModifiedAfterSigning => "\uE7BA",
        PdfSignatureVerificationStatus.Invalid => "\uEA39",
        _ => "\uE946",
    };

    /// <summary>
    /// Gets a value indicating whether the document currently has at least one annotation or digital signature.
    /// </summary>
    public bool HasAnnotationsOrSignatures => AnnotationsSummary.Count > 0 || Signatures.Count > 0;

    /// <summary>
    /// Gets or sets the currently focused annotation (drawn with a subtle accent focus outline on its page).
    /// </summary>
    [ObservableProperty]
    public partial PdfAnnotationInfo? SelectedAnnotation { get; set; }

    partial void OnSelectedAnnotationChanged(PdfAnnotationInfo? oldValue, PdfAnnotationInfo? newValue)
    {
        if (oldValue is not null)
        {
            PageOverlaysInvalidated?.Invoke(oldValue.PageIndex);
        }

        if (newValue is not null && newValue.PageIndex != oldValue?.PageIndex)
        {
            PageOverlaysInvalidated?.Invoke(newValue.PageIndex);
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether at least one annotation edit, move, creation, or deletion can be undone.
    /// </summary>
    [ObservableProperty]
    public partial bool CanUndoAnnotation { get; set; }

    private readonly List<AnnotationUndoEntry> _annotationUndoStack = new();
    private const int MaxAnnotationUndoDepth = 40;

    /// <summary>
    /// Gets or sets the preferred RGBA color used when creating new highlights, comments, or text boxes.
    /// </summary>
    [ObservableProperty]
    public partial PdfAnnotationColor ActiveAnnotationColor { get; set; } = PdfAnnotationColor.Yellow;

    /// <summary>
    /// Returns the cached annotations for <paramref name="pageIndex"/> in O(1) time.
    /// </summary>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <returns>The annotations on that page, or an empty list when none are present.</returns>
    public IReadOnlyList<PdfAnnotationInfo> GetPageAnnotations(int pageIndex)
    {
        return _annotationsByPage.TryGetValue(pageIndex, out List<PdfAnnotationInfo>? list)
            ? list
            : Array.Empty<PdfAnnotationInfo>();
    }

    /// <summary>
    /// Finds the highest-priority annotation containing <c>(pdfX, pdfY)</c> on <paramref name="pageIndex"/>.
    /// Prioritizes digital signature widgets, then sticky notes / FreeText boxes, then smallest bounding box.
    /// </summary>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="pdfX">X coordinate in PDF user space (points).</param>
    /// <param name="pdfY">Y coordinate in PDF user space (points).</param>
    /// <param name="tolerancePt">Hit-test tolerance in PDF points.</param>
    /// <returns>The matching <see cref="PdfAnnotationInfo"/>, or <see langword="null"/> if none matched.</returns>
    public PdfAnnotationInfo? HitTestAnnotation(int pageIndex, double pdfX, double pdfY, double tolerancePt = 6.0)
    {
        if (!_annotationsByPage.TryGetValue(pageIndex, out List<PdfAnnotationInfo>? list) || list.Count == 0)
        {
            return null;
        }

        PdfAnnotationInfo? best = null;
        int bestPriority = int.MinValue;
        double bestArea = double.MaxValue;

        for (int i = 0; i < list.Count; i++)
        {
            PdfAnnotationInfo candidate = list[i];
            if (!candidate.ContainsPoint(pdfX, pdfY, tolerancePt))
            {
                continue;
            }

            int priority = candidate.IsSignatureWidget
                ? 300
                : candidate.Subtype is PdfAnnotationSubtype.Text or PdfAnnotationSubtype.FreeText
                    ? 200
                    : 100;

            double area = Math.Max(1.0, candidate.Bounds.Width * candidate.Bounds.Height);
            if (priority > bestPriority || (priority == bestPriority && area < bestArea))
            {
                best = candidate;
                bestPriority = priority;
                bestArea = area;
            }
        }

        return best;
    }

    /// <summary>
    /// Returns the inspected <see cref="PdfSignatureInfo"/> at <paramref name="signatureIndex"/>, if available.
    /// </summary>
    /// <param name="signatureIndex">Zero-based signature index.</param>
    /// <returns>The matching <see cref="PdfSignatureInfo"/> or <see langword="null"/>.</returns>
    public PdfSignatureInfo? GetSignatureByIndex(int signatureIndex)
    {
        if (signatureIndex >= 0 && signatureIndex < Signatures.Count)
        {
            return Signatures[signatureIndex].Info;
        }

        return Signatures.FirstOrDefault(s => s.Index == signatureIndex)?.Info;
    }

    /// <summary>
    /// Requests the active view to scroll to and open the editor for <paramref name="annotation"/>.
    /// </summary>
    /// <param name="annotation">Target annotation metadata.</param>
    public void RequestFocusAnnotation(PdfAnnotationInfo annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        SelectedAnnotation = annotation;
        GoToPage(annotation.PageIndex);
        AnnotationFocusRequested?.Invoke(annotation);
    }

    /// <summary>
    /// Requests inspection details for <paramref name="signature"/>.
    /// </summary>
    /// <param name="signature">Target signature metadata.</param>
    public void RequestInspectSignature(PdfSignatureInfo signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.PageIndex >= 0)
        {
            GoToPage(signature.PageIndex);
        }

        SignatureInspectRequested?.Invoke(signature);
    }

    /// <summary>
    /// Asynchronously loads digital signatures and all existing annotations across the document without blocking startup.
    /// </summary>
    /// <returns>A task representing the asynchronous scan.</returns>
    public async Task LoadAnnotationsAndSignaturesAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            IReadOnlyList<PdfSignatureInfo> sigs = await _pdfService.GetDocumentSignaturesAsync(
                DocumentId,
                FilePath,
                _lifetime.Token);

            if (_isDisposed)
            {
                return;
            }

            Signatures.Clear();
            for (int i = 0; i < sigs.Count; i++)
            {
                Signatures.Add(new SignatureItemViewModel(sigs[i]));
            }

            HasSignatures = Signatures.Count > 0;
            if (HasSignatures)
            {
                PdfSignatureVerificationStatus overall = PdfSignatureVerificationStatus.Unknown;
                bool anyValid = false;
                bool anyUntrusted = false;
                bool anyModified = false;
                bool anyInvalid = false;

                for (int i = 0; i < Signatures.Count; i++)
                {
                    switch (Signatures[i].Status)
                    {
                        case PdfSignatureVerificationStatus.Invalid:
                            anyInvalid = true;
                            break;
                        case PdfSignatureVerificationStatus.ModifiedAfterSigning:
                            anyModified = true;
                            break;
                        case PdfSignatureVerificationStatus.ValidUntrustedRoot:
                            anyUntrusted = true;
                            break;
                        case PdfSignatureVerificationStatus.Valid:
                            anyValid = true;
                            break;
                    }
                }

                if (anyInvalid)
                {
                    overall = PdfSignatureVerificationStatus.Invalid;
                }
                else if (anyModified)
                {
                    overall = PdfSignatureVerificationStatus.ModifiedAfterSigning;
                }
                else if (anyUntrusted)
                {
                    overall = PdfSignatureVerificationStatus.ValidUntrustedRoot;
                }
                else if (anyValid)
                {
                    overall = PdfSignatureVerificationStatus.Valid;
                }

                OverallSignatureStatus = overall;
                string primarySigner = Signatures[0].SignerName;
                string summary = Signatures.Count == 1
                    ? ResourceStrings.Format(ResourceKeys.SigBannerSingleFormat, primarySigner)
                    : ResourceStrings.Format(ResourceKeys.SigBannerMultiFormat, Signatures.Count, primarySigner);
                string statusText = ResourceStrings.Get(overall switch
                {
                    PdfSignatureVerificationStatus.Valid => ResourceKeys.SigStatusValid,
                    PdfSignatureVerificationStatus.ValidUntrustedRoot => ResourceKeys.SigStatusValidUntrustedRoot,
                    PdfSignatureVerificationStatus.ModifiedAfterSigning => ResourceKeys.SigStatusModifiedAfterSigning,
                    PdfSignatureVerificationStatus.Invalid => ResourceKeys.SigStatusInvalid,
                    _ => ResourceKeys.SigStatusUnknown,
                });
                SignatureStatusTooltip = $"{statusText}\n{summary}";
            }
            else
            {
                OverallSignatureStatus = PdfSignatureVerificationStatus.Unknown;
                SignatureStatusTooltip = string.Empty;
            }

            OnPropertyChanged(nameof(HasAnnotationsOrSignatures));
            OnPropertyChanged(nameof(SignatureStatusGlyph));

            IReadOnlyList<PdfAnnotationInfo> annots = await _pdfService.GetDocumentAnnotationsAsync(
                DocumentId,
                _lifetime.Token);

            if (_isDisposed)
            {
                return;
            }

            _annotationsByPage.Clear();
            for (int i = 0; i < annots.Count; i++)
            {
                PdfAnnotationInfo a = annots[i];
                if (!_annotationsByPage.TryGetValue(a.PageIndex, out List<PdfAnnotationInfo>? pageList))
                {
                    pageList = new List<PdfAnnotationInfo>(4);
                    _annotationsByPage[a.PageIndex] = pageList;
                }

                pageList.Add(a);
            }

            RebuildAnnotationsSummary();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load annotations/signatures: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates text markup annotations covering a single- or multi-page <see cref="PdfTextSelection"/>,
    /// refreshes caches and overlays, marks the document dirty, and returns the primary created annotation.
    /// </summary>
    /// <param name="selection">Active text selection.</param>
    /// <param name="subtype">Markup subtype (<c>Highlight</c>, <c>Underline</c>, <c>StrikeOut</c>, or <c>Squiggly</c>).</param>
    /// <param name="color">Annotation RGBA color.</param>
    /// <param name="comment">Optional comment body attached to the first selected page's markup.</param>
    /// <param name="author">Optional author name.</param>
    /// <param name="recordUndo">Whether to push an entry onto the annotation undo stack.</param>
    /// <returns>The primary created <see cref="PdfAnnotationInfo"/>, or <see langword="null"/> if invalid.</returns>
    public async Task<PdfAnnotationInfo?> AddMarkupForSelectionAsync(
        PdfTextSelection selection,
        PdfAnnotationSubtype subtype,
        PdfAnnotationColor color,
        string comment = "",
        string author = "",
        bool recordUndo = true)
    {
        if (_isDisposed || !selection.HasContent)
        {
            return null;
        }

        string effectiveAuthor = string.IsNullOrWhiteSpace(author) ? Environment.UserName : author;
        PdfAnnotationInfo? firstCreated = null;
        List<AnnotationLocation>? createdLocations = recordUndo ? new() : null;

        IReadOnlyList<PdfPageTextSelection> pageSlices = selection.Pages is { Count: > 0 }
            ? selection.Pages
            : [new PdfPageTextSelection(selection.PageIndex, selection.StartCharIndex, selection.EndCharIndex, selection.Rects, selection.SelectedText)];

        for (int i = 0; i < pageSlices.Count; i++)
        {
            PdfPageTextSelection slice = pageSlices[i];
            if (slice.Rects.Count == 0)
            {
                continue;
            }

            PdfCreateMarkupRequest req = new(
                PageIndex: slice.PageIndex,
                Subtype: subtype,
                Rects: slice.Rects,
                Color: color,
                Contents: i == 0 ? comment : string.Empty,
                Author: effectiveAuthor);

            PdfPageAnnotationMutationResult result = await _pdfService.AddMarkupAnnotationAsync(DocumentId, req, _lifetime.Token);
            if (_isDisposed)
            {
                return firstCreated;
            }

            ApplyPageAnnotationMutation(result.PageIndex, result.Annotations);
            createdLocations?.Add(new AnnotationLocation(result.PageIndex, result.TargetAnnotIndex));
            if (firstCreated is null)
            {
                firstCreated = FindAnnotationByIndex(result.PageIndex, result.TargetAnnotIndex);
            }
        }

        if (createdLocations is { Count: > 0 })
        {
            PushUndoEntry(new UndoCreatedAnnotationsEntry(createdLocations));
        }

        if (firstCreated is not null)
        {
            ClearSelection();
            HasUnsavedChanges = true;
            SelectedAnnotation = firstCreated;
        }

        return firstCreated;
    }

    /// <summary>
    /// Creates a sticky-note comment annotation (<c>/Text</c>) on <paramref name="pageIndex"/>.
    /// </summary>
    public async Task<PdfAnnotationInfo?> AddTextNoteAsync(
        int pageIndex,
        double pdfX,
        double pdfY,
        PdfAnnotationColor color,
        string contents,
        string author = "",
        bool recordUndo = true)
    {
        if (_isDisposed)
        {
            return null;
        }

        PdfCreateNoteRequest req = new(
            PageIndex: pageIndex,
            PdfX: pdfX,
            PdfY: pdfY,
            Color: color,
            Contents: contents ?? string.Empty,
            Author: string.IsNullOrWhiteSpace(author) ? Environment.UserName : author);

        PdfPageAnnotationMutationResult result = await _pdfService.AddTextNoteAnnotationAsync(DocumentId, req, _lifetime.Token);
        if (_isDisposed)
        {
            return null;
        }

        ApplyPageAnnotationMutation(result.PageIndex, result.Annotations);
        PdfAnnotationInfo? created = FindAnnotationByIndex(result.PageIndex, result.TargetAnnotIndex);
        if (recordUndo && created is not null)
        {
            PushUndoEntry(new UndoCreatedAnnotationsEntry([new AnnotationLocation(created.PageIndex, created.AnnotIndex)]));
        }

        HasUnsavedChanges = true;
        SelectedAnnotation = created;
        return created;
    }

    /// <summary>
    /// Inserts a raster image onto <paramref name="pageIndex"/> as a <c>/Stamp</c> annotation.
    /// </summary>
    public async Task<PdfAnnotationInfo?> AddImageAnnotationAsync(
        int pageIndex,
        double pdfX,
        double pdfY,
        PdfCapturedImage image,
        string author = "",
        bool recordUndo = true)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (_isDisposed || pageIndex < 0 || pageIndex >= Pages.Count)
        {
            return null;
        }

        PdfCreateImageRequest req = new(
            PageIndex: pageIndex,
            PdfX: pdfX,
            PdfY: pdfY,
            PixelWidth: image.PixelWidth,
            PixelHeight: image.PixelHeight,
            BgraPixels: image.BgraPixels,
            Author: string.IsNullOrWhiteSpace(author) ? Environment.UserName : author,
            Subject: image.Subject);

        PdfPageAnnotationMutationResult result = await _pdfService.AddImageAnnotationAsync(DocumentId, req, _lifetime.Token);
        if (_isDisposed)
        {
            return null;
        }

        ApplyPageAnnotationMutation(result.PageIndex, result.Annotations);
        PdfAnnotationInfo? created = FindAnnotationByIndex(result.PageIndex, result.TargetAnnotIndex);
        if (recordUndo && created is not null)
        {
            PushUndoEntry(new UndoCreatedAnnotationsEntry([new AnnotationLocation(created.PageIndex, created.AnnotIndex)]));
        }

        HasUnsavedChanges = true;
        SelectedAnnotation = created;
        return created;
    }

    /// <summary>
    /// Captures the embedded raster image payload from an existing <c>/Stamp</c> annotation.
    /// </summary>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="annotIndex">Zero-based annotation index.</param>
    /// <returns>The captured image payload, or <see langword="null"/> if not available.</returns>
    public Task<PdfCapturedImage?> CaptureStampImageAsync(int pageIndex, int annotIndex)
        => _isDisposed
            ? Task.FromResult<PdfCapturedImage?>(null)
            : _pdfService.CaptureStampImageAsync(DocumentId, pageIndex, annotIndex, _lifetime.Token);

    /// <summary>
    /// Creates a direct-on-page vector text-box annotation on <paramref name="pageIndex"/>.
    /// </summary>
    public async Task<PdfAnnotationInfo?> AddFreeTextAsync(
        int pageIndex,
        double pdfX,
        double pdfY,
        PdfAnnotationColor color,
        string contents,
        string author = "",
        double maxWidthPt = 280.0,
        PdfFreeTextStyle? style = null,
        bool recordUndo = true)
    {
        if (_isDisposed)
        {
            return null;
        }

        PdfCreateFreeTextRequest req = new(
            PageIndex: pageIndex,
            PdfX: pdfX,
            PdfY: pdfY,
            Color: color,
            Contents: contents ?? string.Empty,
            Author: string.IsNullOrWhiteSpace(author) ? Environment.UserName : author,
            MaxWidthPt: maxWidthPt,
            Style: style);

        PdfPageAnnotationMutationResult result = await _pdfService.AddFreeTextAnnotationAsync(DocumentId, req, _lifetime.Token);
        if (_isDisposed)
        {
            return null;
        }

        ApplyPageAnnotationMutation(result.PageIndex, result.Annotations);
        PdfAnnotationInfo? created = FindAnnotationByIndex(result.PageIndex, result.TargetAnnotIndex);
        if (recordUndo && created is not null)
        {
            PushUndoEntry(new UndoCreatedAnnotationsEntry([new AnnotationLocation(created.PageIndex, created.AnnotIndex)]));
        }

        HasUnsavedChanges = true;
        SelectedAnnotation = created;
        return created;
    }

    /// <summary>
    /// Updates the text contents, optional color, and optional text formatting style of an existing annotation.
    /// </summary>
    public async Task<PdfAnnotationInfo?> UpdateAnnotationAsync(
        int pageIndex,
        int annotIndex,
        string contents,
        PdfAnnotationColor? newColor = null,
        PdfFreeTextStyle? newTextStyle = null,
        bool recordUndo = true)
    {
        if (_isDisposed)
        {
            return null;
        }

        PdfAnnotationInfo? before = recordUndo ? FindExactAnnotation(pageIndex, annotIndex) : null;

        PdfUpdateAnnotationRequest req = new(pageIndex, annotIndex, contents ?? string.Empty, newColor, newTextStyle);
        PdfPageAnnotationMutationResult result = await _pdfService.UpdateAnnotationAsync(DocumentId, req, _lifetime.Token);
        if (_isDisposed)
        {
            return null;
        }

        ApplyPageAnnotationMutation(result.PageIndex, result.Annotations);
        PdfAnnotationInfo? updated = FindAnnotationByIndex(result.PageIndex, result.TargetAnnotIndex);
        if (recordUndo && before is not null && updated is not null &&
            (!string.Equals(before.Contents, updated.Contents, StringComparison.Ordinal) ||
             !before.Color.Equals(updated.Color) ||
             !Equals(before.TextStyle, updated.TextStyle)))
        {
            PushUndoEntry(new UndoUpdatedAnnotationEntry(
                updated.PageIndex,
                updated.AnnotIndex,
                before.Contents,
                before.Color,
                before.TextStyle));
        }

        HasUnsavedChanges = true;
        SelectedAnnotation = updated;
        return updated;
    }

    /// <summary>
    /// Deletes an existing annotation from <paramref name="pageIndex"/> and refreshes caches and views.
    /// </summary>
    public async Task DeleteAnnotationAsync(int pageIndex, int annotIndex, bool recordUndo = true)
    {
        if (_isDisposed)
        {
            return;
        }

        PdfAnnotationInfo? before = recordUndo ? FindExactAnnotation(pageIndex, annotIndex) : null;
        if (before is { Subtype: PdfAnnotationSubtype.Stamp, IsTextBox: false, CapturedImage: null })
        {
            PdfCapturedImage? captured = await CaptureStampImageAsync(pageIndex, annotIndex);
            if (_isDisposed)
            {
                return;
            }

            if (captured is not null)
            {
                before = before with { CapturedImage = captured };
            }
        }

        IReadOnlyList<PdfAnnotationInfo> refreshed = await _pdfService.DeleteAnnotationAsync(DocumentId, pageIndex, annotIndex, _lifetime.Token);
        if (_isDisposed)
        {
            return;
        }

        if (SelectedAnnotation is { } sel && sel.PageIndex == pageIndex && sel.AnnotIndex == annotIndex)
        {
            SelectedAnnotation = null;
        }

        ApplyPageAnnotationMutation(pageIndex, refreshed, deletedAnnotIndex: annotIndex);
        if (recordUndo && before is not null)
        {
            AdjustUndoEntriesForDeletion(pageIndex, annotIndex);
            PushUndoEntry(new UndoDeletedAnnotationEntry(before));
        }

        HasUnsavedChanges = true;
    }

    /// <summary>
    /// Moves an existing movable annotation on <paramref name="pageIndex"/> by a PDF-space delta.
    /// </summary>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="annotIndex">Zero-based index in the page's annotation list.</param>
    /// <param name="deltaPdfX">Horizontal translation in PDF points.</param>
    /// <param name="deltaPdfY">Vertical translation in PDF points.</param>
    /// <param name="recordUndo">Whether to push an entry onto the annotation undo stack.</param>
    /// <returns>The updated <see cref="PdfAnnotationInfo"/>, or <see langword="null"/> if unsuccessful.</returns>
    public async Task<PdfAnnotationInfo?> MoveAnnotationAsync(
        int pageIndex,
        int annotIndex,
        double deltaPdfX,
        double deltaPdfY,
        bool recordUndo = true)
    {
        if (_isDisposed)
        {
            return null;
        }

        PdfAnnotationInfo? before = recordUndo ? FindExactAnnotation(pageIndex, annotIndex) : null;

        PdfMoveAnnotationRequest req = new(pageIndex, annotIndex, deltaPdfX, deltaPdfY);
        PdfPageAnnotationMutationResult result = await _pdfService.MoveAnnotationAsync(DocumentId, req, _lifetime.Token);
        if (_isDisposed)
        {
            return null;
        }

        ApplyPageAnnotationMutation(result.PageIndex, result.Annotations);
        PdfAnnotationInfo? updated = FindAnnotationByIndex(result.PageIndex, result.TargetAnnotIndex);
        if (recordUndo && before is not null && updated is not null)
        {
            double actualDx = updated.Bounds.Left - before.Bounds.Left;
            double actualDy = updated.Bounds.Bottom - before.Bounds.Bottom;
            if (Math.Abs(actualDx) > 0.05 || Math.Abs(actualDy) > 0.05)
            {
                PushUndoEntry(new UndoMovedAnnotationEntry(updated.PageIndex, updated.AnnotIndex, actualDx, actualDy));
            }
        }

        HasUnsavedChanges = true;
        SelectedAnnotation = updated;
        return updated;
    }

    /// <summary>
    /// Resizes an existing resizable annotation (<c>/Stamp</c>) on <paramref name="pageIndex"/> to <paramref name="newBounds"/>.
    /// </summary>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="annotIndex">Zero-based index in the page's annotation list.</param>
    /// <param name="newBounds">Target bounding rectangle in PDF user space (points).</param>
    /// <param name="recordUndo">Whether to push an entry onto the annotation undo stack.</param>
    /// <returns>The updated <see cref="PdfAnnotationInfo"/>, or <see langword="null"/> if unsuccessful.</returns>
    public async Task<PdfAnnotationInfo?> ResizeAnnotationAsync(
        int pageIndex,
        int annotIndex,
        PdfRect newBounds,
        bool recordUndo = true)
    {
        if (_isDisposed)
        {
            return null;
        }

        PdfAnnotationInfo? before = recordUndo ? FindExactAnnotation(pageIndex, annotIndex) : null;

        PdfResizeAnnotationRequest req = new(pageIndex, annotIndex, newBounds);
        PdfPageAnnotationMutationResult result = await _pdfService.ResizeAnnotationAsync(DocumentId, req, _lifetime.Token);
        if (_isDisposed)
        {
            return null;
        }

        ApplyPageAnnotationMutation(result.PageIndex, result.Annotations);
        PdfAnnotationInfo? updated = FindAnnotationByIndex(result.PageIndex, result.TargetAnnotIndex);
        if (recordUndo && before is not null && updated is not null)
        {
            PushUndoEntry(new UndoResizedAnnotationEntry(updated.PageIndex, updated.AnnotIndex, before.Bounds));
        }

        HasUnsavedChanges = true;
        SelectedAnnotation = updated;
        return updated;
    }

    /// <summary>
    /// Pastes a copied or cut annotation onto <paramref name="targetPageIndex"/> near <c>(targetPdfX, targetPdfY)</c>.
    /// </summary>
    /// <param name="source">Source annotation snapshot.</param>
    /// <param name="targetPageIndex">Destination zero-based page index.</param>
    /// <param name="targetPdfX">Target X coordinate in PDF user space (points).</param>
    /// <param name="targetPdfY">Target Y coordinate in PDF user space (points).</param>
    /// <param name="recordUndo">Whether to push an entry onto the annotation undo stack.</param>
    /// <returns>The newly created <see cref="PdfAnnotationInfo"/>, or <see langword="null"/> if not supported.</returns>
    public async Task<PdfAnnotationInfo?> PasteAnnotationAsync(
        PdfAnnotationInfo source,
        int targetPageIndex,
        double targetPdfX,
        double targetPdfY,
        bool recordUndo = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_isDisposed || !source.CanCopyOrCut || targetPageIndex < 0 || targetPageIndex >= Pages.Count)
        {
            return null;
        }

        if (source.IsTextBox)
        {
            string body = string.IsNullOrWhiteSpace(source.Contents) ? "Text" : source.Contents;
            double maxW = source.TextStyle?.MaxWidthPt is > 60.0 and double sw
                ? sw
                : (source.Bounds.Width >= 110f ? source.Bounds.Width : 280.0);
            PdfAnnotationInfo? ft = await AddFreeTextAsync(
                targetPageIndex,
                targetPdfX,
                targetPdfY,
                source.TextStyle?.Color ?? source.Color,
                body,
                source.Author,
                maxW,
                style: source.TextStyle,
                recordUndo: false);
            if (recordUndo && ft is not null)
            {
                PushUndoEntry(new UndoCreatedAnnotationsEntry([new AnnotationLocation(ft.PageIndex, ft.AnnotIndex)]));
            }

            return ft;
        }

        if (source.Subtype == PdfAnnotationSubtype.Text)
        {
            PdfAnnotationInfo? note = await AddTextNoteAsync(targetPageIndex, targetPdfX, targetPdfY, source.Color, source.Contents, source.Author, recordUndo: false);
            if (recordUndo && note is not null)
            {
                PushUndoEntry(new UndoCreatedAnnotationsEntry([new AnnotationLocation(note.PageIndex, note.AnnotIndex)]));
            }

            return note;
        }

        PdfPasteAnnotationRequest req = new(source, targetPageIndex, targetPdfX, targetPdfY);
        PdfPageAnnotationMutationResult result = await _pdfService.PasteAnnotationAsync(DocumentId, req, _lifetime.Token);
        if (_isDisposed)
        {
            return null;
        }

        ApplyPageAnnotationMutation(result.PageIndex, result.Annotations);
        PdfAnnotationInfo? created = FindAnnotationByIndex(result.PageIndex, result.TargetAnnotIndex);
        if (recordUndo && created is not null)
        {
            PushUndoEntry(new UndoCreatedAnnotationsEntry([new AnnotationLocation(created.PageIndex, created.AnnotIndex)]));
        }

        HasUnsavedChanges = true;
        SelectedAnnotation = created;
        return created;
    }

    /// <summary>
    /// Undoes the most recent annotation creation, edit, move, or deletion performed during this session.
    /// </summary>
    /// <returns><see langword="true"/> if an action was undone; otherwise <see langword="false"/>.</returns>
    public async Task<bool> UndoAnnotationAsync()
    {
        if (_isDisposed || _annotationUndoStack.Count == 0)
        {
            return false;
        }

        AnnotationUndoEntry entry = _annotationUndoStack[^1];
        _annotationUndoStack.RemoveAt(_annotationUndoStack.Count - 1);
        CanUndoAnnotation = _annotationUndoStack.Count > 0;

        switch (entry)
        {
            case UndoCreatedAnnotationsEntry created:
                for (int i = created.Locations.Count - 1; i >= 0; i--)
                {
                    AnnotationLocation loc = created.Locations[i];
                    if (loc.IsValid)
                    {
                        await DeleteAnnotationAsync(loc.PageIndex, loc.AnnotIndex, recordUndo: false);
                    }
                }

                return true;

            case UndoUpdatedAnnotationEntry updated:
                if (updated.Location.IsValid)
                {
                    _ = await UpdateAnnotationAsync(
                        updated.Location.PageIndex,
                        updated.Location.AnnotIndex,
                        updated.PreviousContents,
                        updated.PreviousColor,
                        newTextStyle: updated.PreviousTextStyle,
                        recordUndo: false);
                }

                return true;

            case UndoMovedAnnotationEntry moved:
                if (moved.Location.IsValid)
                {
                    _ = await MoveAnnotationAsync(
                        moved.Location.PageIndex,
                        moved.Location.AnnotIndex,
                        -moved.ActualDeltaPdfX,
                        -moved.ActualDeltaPdfY,
                        recordUndo: false);
                }

                return true;

            case UndoResizedAnnotationEntry resized:
                if (resized.Location.IsValid)
                {
                    _ = await ResizeAnnotationAsync(
                        resized.Location.PageIndex,
                        resized.Location.AnnotIndex,
                        resized.OldBounds,
                        recordUndo: false);
                }

                return true;

            case UndoDeletedAnnotationEntry deleted:
                PdfAnnotationInfo snapshot = deleted.Snapshot;
                PdfAnnotationInfo? restored = null;
                if (snapshot.IsTextMarkup)
                {
                    IReadOnlyList<PdfRect> rects = snapshot.QuadRects.Count > 0 ? snapshot.QuadRects : [snapshot.Bounds];
                    PdfCreateMarkupRequest req = new(
                        PageIndex: snapshot.PageIndex,
                        Subtype: snapshot.Subtype,
                        Rects: rects,
                        Color: snapshot.Color,
                        Contents: snapshot.Contents,
                        Author: snapshot.Author,
                        Subject: snapshot.Subject,
                        SkipPadding: true);
                    PdfPageAnnotationMutationResult res = await _pdfService.AddMarkupAnnotationAsync(DocumentId, req, _lifetime.Token);
                    if (!_isDisposed)
                    {
                        ApplyPageAnnotationMutation(res.PageIndex, res.Annotations);
                        restored = FindAnnotationByIndex(res.PageIndex, res.TargetAnnotIndex);
                        HasUnsavedChanges = true;
                        SelectedAnnotation = restored;
                    }
                }
                else if (snapshot.Subtype == PdfAnnotationSubtype.Text)
                {
                    double cx = (snapshot.Bounds.Left + snapshot.Bounds.Right) * 0.5;
                    double cy = (snapshot.Bounds.Bottom + snapshot.Bounds.Top) * 0.5;
                    restored = await AddTextNoteAsync(snapshot.PageIndex, cx, cy, snapshot.Color, snapshot.Contents, snapshot.Author, recordUndo: false);
                }
                else if (snapshot.IsTextBox)
                {
                    double maxW = snapshot.TextStyle?.MaxWidthPt is > 60.0 and double sw
                        ? sw
                        : Math.Max(110.0, snapshot.Bounds.Width);
                    restored = await AddFreeTextAsync(
                        snapshot.PageIndex,
                        snapshot.Bounds.Left,
                        snapshot.Bounds.Top,
                        snapshot.TextStyle?.Color ?? snapshot.Color,
                        snapshot.Contents,
                        snapshot.Author,
                        maxW,
                        style: snapshot.TextStyle,
                        recordUndo: false);
                }
                else
                {
                    PdfPasteAnnotationRequest req = new(snapshot, snapshot.PageIndex, snapshot.Bounds.Left, snapshot.Bounds.Top);
                    PdfPageAnnotationMutationResult res = await _pdfService.PasteAnnotationAsync(DocumentId, req, _lifetime.Token);
                    if (!_isDisposed)
                    {
                        ApplyPageAnnotationMutation(res.PageIndex, res.Annotations);
                        restored = FindAnnotationByIndex(res.PageIndex, res.TargetAnnotIndex);
                        HasUnsavedChanges = true;
                        SelectedAnnotation = restored;
                    }
                }

                if (restored is not null)
                {
                    RemapRestoredAnnotationIndex(snapshot.PageIndex, snapshot.AnnotIndex, restored.AnnotIndex);
                }

                return restored is not null;

            default:
                return false;
        }
    }

    private void PushUndoEntry(AnnotationUndoEntry entry)
    {
        _annotationUndoStack.Add(entry);
        if (_annotationUndoStack.Count > MaxAnnotationUndoDepth)
        {
            _annotationUndoStack.RemoveAt(0);
        }

        CanUndoAnnotation = _annotationUndoStack.Count > 0;
    }

    private void AdjustUndoEntriesForDeletion(int pageIndex, int removedAnnotIndex)
    {
        for (int i = 0; i < _annotationUndoStack.Count; i++)
        {
            _annotationUndoStack[i].OnAnnotationDeleted(pageIndex, removedAnnotIndex);
        }
    }

    private void RemapRestoredAnnotationIndex(int pageIndex, int originalAnnotIndex, int restoredAnnotIndex)
    {
        for (int i = 0; i < _annotationUndoStack.Count; i++)
        {
            _annotationUndoStack[i].OnAnnotationRestored(pageIndex, originalAnnotIndex, restoredAnnotIndex);
        }
    }

    private PdfAnnotationInfo? FindExactAnnotation(int pageIndex, int annotIndex)
    {
        if (!_annotationsByPage.TryGetValue(pageIndex, out List<PdfAnnotationInfo>? list))
        {
            return null;
        }

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].AnnotIndex == annotIndex)
            {
                return list[i];
            }
        }

        return null;
    }

    private sealed class AnnotationLocation(int pageIndex, int annotIndex)
    {
        public int PageIndex { get; } = pageIndex;
        public int AnnotIndex { get; private set; } = annotIndex;
        public bool IsValid => PageIndex >= 0 && AnnotIndex >= 0;

        public void OnDeleted(int deletedPage, int deletedIndex)
        {
            if (PageIndex != deletedPage || AnnotIndex < 0)
            {
                return;
            }

            if (AnnotIndex == deletedIndex)
            {
                // Encode orphaned prior index negatively so a subsequent Undo of the delete can relink it.
                AnnotIndex = -(deletedIndex + 1);
            }
            else if (AnnotIndex > deletedIndex)
            {
                AnnotIndex--;
            }
        }

        public void OnRestored(int restoredPage, int originalIndex, int newIndex)
        {
            if (PageIndex == restoredPage && AnnotIndex == -(originalIndex + 1))
            {
                AnnotIndex = newIndex;
            }
        }
    }

    private abstract class AnnotationUndoEntry
    {
        public abstract void OnAnnotationDeleted(int pageIndex, int removedAnnotIndex);
        public abstract void OnAnnotationRestored(int pageIndex, int originalAnnotIndex, int restoredAnnotIndex);
    }

    private sealed class UndoCreatedAnnotationsEntry(List<AnnotationLocation> locations) : AnnotationUndoEntry
    {
        public List<AnnotationLocation> Locations { get; } = locations;

        public override void OnAnnotationDeleted(int pageIndex, int removedAnnotIndex)
        {
            for (int i = 0; i < Locations.Count; i++)
            {
                Locations[i].OnDeleted(pageIndex, removedAnnotIndex);
            }
        }

        public override void OnAnnotationRestored(int pageIndex, int originalAnnotIndex, int restoredAnnotIndex)
        {
            for (int i = 0; i < Locations.Count; i++)
            {
                Locations[i].OnRestored(pageIndex, originalAnnotIndex, restoredAnnotIndex);
            }
        }
    }

    private sealed class UndoUpdatedAnnotationEntry(
        int pageIndex,
        int annotIndex,
        string previousContents,
        PdfAnnotationColor previousColor,
        PdfFreeTextStyle? previousTextStyle = null) : AnnotationUndoEntry
    {
        public AnnotationLocation Location { get; } = new(pageIndex, annotIndex);
        public string PreviousContents { get; } = previousContents;
        public PdfAnnotationColor PreviousColor { get; } = previousColor;
        public PdfFreeTextStyle? PreviousTextStyle { get; } = previousTextStyle;

        public override void OnAnnotationDeleted(int pageIndex, int removedAnnotIndex) => Location.OnDeleted(pageIndex, removedAnnotIndex);
        public override void OnAnnotationRestored(int pageIndex, int originalAnnotIndex, int restoredAnnotIndex) => Location.OnRestored(pageIndex, originalAnnotIndex, restoredAnnotIndex);
    }

    private sealed class UndoMovedAnnotationEntry(
        int pageIndex,
        int annotIndex,
        double actualDeltaPdfX,
        double actualDeltaPdfY) : AnnotationUndoEntry
    {
        public AnnotationLocation Location { get; } = new(pageIndex, annotIndex);
        public double ActualDeltaPdfX { get; } = actualDeltaPdfX;
        public double ActualDeltaPdfY { get; } = actualDeltaPdfY;

        public override void OnAnnotationDeleted(int pageIndex, int removedAnnotIndex) => Location.OnDeleted(pageIndex, removedAnnotIndex);
        public override void OnAnnotationRestored(int pageIndex, int originalAnnotIndex, int restoredAnnotIndex) => Location.OnRestored(pageIndex, originalAnnotIndex, restoredAnnotIndex);
    }

    private sealed class UndoResizedAnnotationEntry(
        int pageIndex,
        int annotIndex,
        PdfRect oldBounds) : AnnotationUndoEntry
    {
        public AnnotationLocation Location { get; } = new(pageIndex, annotIndex);
        public PdfRect OldBounds { get; } = oldBounds;

        public override void OnAnnotationDeleted(int pageIndex, int removedAnnotIndex) => Location.OnDeleted(pageIndex, removedAnnotIndex);
        public override void OnAnnotationRestored(int pageIndex, int originalAnnotIndex, int restoredAnnotIndex) => Location.OnRestored(pageIndex, originalAnnotIndex, restoredAnnotIndex);
    }

    private sealed class UndoDeletedAnnotationEntry(PdfAnnotationInfo snapshot) : AnnotationUndoEntry
    {
        public PdfAnnotationInfo Snapshot { get; } = snapshot;

        public override void OnAnnotationDeleted(int pageIndex, int removedAnnotIndex)
        {
        }

        public override void OnAnnotationRestored(int pageIndex, int originalAnnotIndex, int restoredAnnotIndex)
        {
        }
    }

    private PdfAnnotationInfo? FindAnnotationByIndex(int pageIndex, int annotIndex)
    {
        if (!_annotationsByPage.TryGetValue(pageIndex, out List<PdfAnnotationInfo>? list))
        {
            return null;
        }

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].AnnotIndex == annotIndex)
            {
                return list[i];
            }
        }

        return list.Count > 0 ? list[^1] : null;
    }

    private void ApplyPageAnnotationMutation(int pageIndex, IReadOnlyList<PdfAnnotationInfo> pageAnnotations, int deletedAnnotIndex = -1)
    {
        if (pageAnnotations.Count == 0)
        {
            _annotationsByPage.Remove(pageIndex);
        }
        else
        {
            _annotationsByPage[pageIndex] = new List<PdfAnnotationInfo>(pageAnnotations);
        }

        SyncPageAnnotationsSummary(pageIndex, pageAnnotations, deletedAnnotIndex);
        _tileCache.RemovePage(DocumentId, pageIndex);
        RefreshThumbnailForPage(pageIndex);
        PageContentInvalidated?.Invoke(pageIndex);
    }

    private void SyncPageAnnotationsSummary(int pageIndex, IReadOnlyList<PdfAnnotationInfo> pageAnnotations, int deletedAnnotIndex)
    {
        bool hadAny = HasAnnotationsOrSignatures;

        List<PdfAnnotationInfo> visibleNew = new(pageAnnotations.Count);
        for (int i = 0; i < pageAnnotations.Count; i++)
        {
            if (!pageAnnotations[i].IsSignatureWidget)
            {
                visibleNew.Add(pageAnnotations[i]);
            }
        }

        int sliceStart = 0;
        while (sliceStart < AnnotationsSummary.Count && AnnotationsSummary[sliceStart].PageIndex < pageIndex)
        {
            sliceStart++;
        }

        int sliceCount = 0;
        while (sliceStart + sliceCount < AnnotationsSummary.Count && AnnotationsSummary[sliceStart + sliceCount].PageIndex == pageIndex)
        {
            sliceCount++;
        }

        if (visibleNew.Count == sliceCount)
        {
            for (int i = 0; i < sliceCount; i++)
            {
                AnnotationsSummary[sliceStart + i].UpdateFrom(visibleNew[i]);
            }
        }
        else if (visibleNew.Count < sliceCount)
        {
            if (deletedAnnotIndex >= 0)
            {
                for (int k = 0; k < sliceCount; k++)
                {
                    if (AnnotationsSummary[sliceStart + k].AnnotIndex == deletedAnnotIndex)
                    {
                        AnnotationsSummary.RemoveAt(sliceStart + k);
                        sliceCount--;
                        break;
                    }
                }
            }

            while (sliceCount > visibleNew.Count)
            {
                int removeOffset = sliceCount - 1;
                for (int k = 0; k < visibleNew.Count; k++)
                {
                    PdfAnnotationInfo existing = AnnotationsSummary[sliceStart + k].Info;
                    PdfAnnotationInfo target = visibleNew[k];
                    if (existing.Subtype != target.Subtype ||
                        !string.Equals(existing.Contents, target.Contents, StringComparison.Ordinal))
                    {
                        removeOffset = k;
                        break;
                    }
                }

                AnnotationsSummary.RemoveAt(sliceStart + removeOffset);
                sliceCount--;
            }

            for (int i = 0; i < visibleNew.Count; i++)
            {
                AnnotationsSummary[sliceStart + i].UpdateFrom(visibleNew[i]);
            }
        }
        else
        {
            for (int i = 0; i < visibleNew.Count; i++)
            {
                if (i < sliceCount)
                {
                    PdfAnnotationInfo existing = AnnotationsSummary[sliceStart + i].Info;
                    PdfAnnotationInfo target = visibleNew[i];
                    if (existing.AnnotIndex == target.AnnotIndex ||
                        (existing.Subtype == target.Subtype && string.Equals(existing.Contents, target.Contents, StringComparison.Ordinal)))
                    {
                        AnnotationsSummary[sliceStart + i].UpdateFrom(target);
                        continue;
                    }
                }

                AnnotationsSummary.Insert(sliceStart + i, new AnnotationItemViewModel(visibleNew[i]));
                sliceCount++;
            }

            while (sliceCount > visibleNew.Count)
            {
                AnnotationsSummary.RemoveAt(sliceStart + sliceCount - 1);
                sliceCount--;
            }
        }

        if (hadAny != HasAnnotationsOrSignatures)
        {
            OnPropertyChanged(nameof(HasAnnotationsOrSignatures));
        }
    }

    private void RefreshThumbnailForPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Thumbnails.Count)
        {
            return;
        }

        ThumbnailItemViewModel item = Thumbnails[pageIndex];
        if (item.LoadCancellation is not null)
        {
            item.LoadCancellation.Cancel();
            item.LoadCancellation.Dispose();
            item.LoadCancellation = null;
        }

        item.ClearImage();
        if (_thumbnailsActive && item.IsRealized)
        {
            RequestThumbnail(item, _thumbnailScale);
        }
    }

    private void RebuildAnnotationsSummary()
    {
        AnnotationsSummary.Clear();
        if (_annotationsByPage.Count > 0)
        {
            List<int> pageKeys = new(_annotationsByPage.Keys);
            pageKeys.Sort();
            for (int k = 0; k < pageKeys.Count; k++)
            {
                List<PdfAnnotationInfo> list = _annotationsByPage[pageKeys[k]];
                for (int i = 0; i < list.Count; i++)
                {
                    // Skip duplicate signature widgets in AnnotationsSummary when already displayed in Signatures section
                    if (!list[i].IsSignatureWidget)
                    {
                        AnnotationsSummary.Add(new AnnotationItemViewModel(list[i]));
                    }
                }
            }
        }

        OnPropertyChanged(nameof(HasAnnotationsOrSignatures));
    }

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
    /// <param name="metadata">Document metadata extracted from the PDF, if available.</param>
    /// <param name="settingsService">Settings service for persisting bookmarks and user preferences.</param>
    public DocumentViewModel(
        IPdfService pdfService,
        TileCache tileCache,
        PdfDocumentInfo info,
        IReadOnlyList<OutlineItem> outline,
        IReadOnlyList<string> attachmentNames,
        PdfMetadata? metadata = null,
        ISettingsService? settingsService = null)
    {
        _pdfService = pdfService;
        _tileCache = tileCache;
        _settingsService = settingsService;
        Metadata = metadata ?? PdfMetadata.Empty;

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

        UpdatePageCacheCapacity();

        foreach (OutlineItem item in outline)
        {
            Outline.Add(CreateOutlineViewModel(item));
        }

        foreach (string name in attachmentNames)
        {
            Attachments.Add(new AttachmentItemViewModel(name));
        }

        LoadSavedBookmarks();
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
                // Recompute card dimensions from current item or default box preserving dynamic orientation.
                double aspect = GetPageDisplayAspectRatio(Pages[i], Rotation);
                double maxDim = Math.Max(DefaultMaxThumbnailWidthDip, Math.Max(item.WidthDip, item.HeightDip));
                double maxW = maxDim;
                double maxH = Math.Round(maxDim * 1.48);
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
    private readonly ISettingsService? _settingsService;
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

    private int _lastSelectedThumbnailIndex;

    partial void OnCurrentPageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentPageNumber));
        OnPropertyChanged(nameof(ActivePageDimensionsFormatted));

        if (!IsContinuous)
        {
            UpdateDisplayedRows();
            if (Math.Abs(FractionalPageIndex - value) > 0.001)
            {
                FractionalPageIndex = value;
            }
        }

        if (_lastSelectedThumbnailIndex >= 0 &&
            _lastSelectedThumbnailIndex < Thumbnails.Count &&
            _lastSelectedThumbnailIndex != value)
        {
            Thumbnails[_lastSelectedThumbnailIndex].IsSelected = false;
            Thumbnails[_lastSelectedThumbnailIndex].IsCurrentPage = false;
        }

        if (value >= 0 && value < Thumbnails.Count)
        {
            Thumbnails[value].IsSelected = true;
            Thumbnails[value].IsCurrentPage = true;
            _lastSelectedThumbnailIndex = value;
        }

        UpdateIsCurrentPageBookmarked();
    }

    private void LoadSavedBookmarks()
    {
        if (_settingsService is null || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        if (_settingsService.Settings.DocumentBookmarks.TryGetValue(FilePath, out List<SavedBookmarkItem>? savedList) &&
            savedList is { Count: > 0 })
        {
            Bookmarks.Clear();
            foreach (SavedBookmarkItem entry in savedList.OrderBy(b => b.PageIndex))
            {
                if (entry.PageIndex >= 0 && entry.PageIndex < PageCount &&
                    Bookmarks.All(b => b.PageIndex != entry.PageIndex))
                {
                    Bookmarks.Add(new BookmarkItemViewModel(entry.PageIndex, entry.CustomTitle));
                }
            }
        }
    }

    /// <summary>
    /// Persists the current document's bookmarks and custom titles to <see cref="ISettingsService"/>.
    /// </summary>
    public void SaveBookmarksToSettings()
    {
        if (_settingsService is null || string.IsNullOrWhiteSpace(FilePath))
        {
            return;
        }

        string key = FilePath;
        List<SavedBookmarkItem> snapshot = Bookmarks
            .OrderBy(b => b.PageIndex)
            .Select(b => new SavedBookmarkItem
            {
                PageIndex = b.PageIndex,
                CustomTitle = b.HasCustomTitle ? b.CustomTitle.Trim() : null
            })
            .ToList();

        _settingsService.Update(s =>
        {
            if (snapshot.Count == 0)
            {
                s.DocumentBookmarks.Remove(key);
            }
            else
            {
                s.DocumentBookmarks[key] = snapshot;
            }
        });
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
        SaveBookmarksToSettings();
    }

    /// <summary>
    /// Renames a bookmark item and immediately persists the updated name to disk.
    /// </summary>
    /// <param name="bookmark">The bookmark item to rename.</param>
    /// <param name="newTitle">The new user-defined name (or empty/whitespace to restore the default page number label).</param>
    public void RenameBookmark(BookmarkItemViewModel? bookmark, string? newTitle)
    {
        if (bookmark is null)
        {
            return;
        }

        string trimmed = string.IsNullOrWhiteSpace(newTitle) ? string.Empty : newTitle.Trim();
        string defaultLabel = WinPDF.Converters.UiHelpers.FormatBookmarkPage(bookmark.PageNumber);
        if (string.Equals(trimmed, defaultLabel, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = string.Empty;
        }

        bookmark.CustomTitle = trimmed;
        bookmark.IsEditing = false;
        SaveBookmarksToSettings();
    }

    /// <summary>
    /// Removes a specific bookmark item from the bookmarks collection even when viewing a different page.
    /// </summary>
    /// <param name="bookmark">The bookmark item to remove.</param>
    [RelayCommand]
    public void RemoveBookmark(BookmarkItemViewModel? bookmark)
    {
        if (bookmark is null)
        {
            return;
        }

        if (Bookmarks.Remove(bookmark))
        {
            UpdateIsCurrentPageBookmarked();
            SaveBookmarksToSettings();
        }
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
                    DisplayedRows[i].Rotation != targetRows[i].Rotation)
                {
                    isIdentical = false;
                    break;
                }

                DisplayedRows[i].IsPresentationMode = targetRows[i].IsPresentationMode;
                DisplayedRows[i].IsNightMode = targetRows[i].IsNightMode;
            }
        }

        if (!isIdentical)
        {
            if (DisplayedRows.Count == 1 && targetRows.Count == 1)
            {
                DisplayedRows[0] = targetRows[0];
            }
            else
            {
                DisplayedRows.Clear();
                foreach (var r in targetRows)
                {
                    DisplayedRows.Add(r);
                }
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
    /// Ensures that the <see cref="Thumbnails"/> collection is populated on demand when the Thumbnails tab becomes active.
    /// </summary>
    public void EnsureThumbnailsInitialized()
    {
        if (_isDisposed || Thumbnails.Count > 0 || Pages.Count == 0)
        {
            return;
        }

        for (int i = 0; i < Pages.Count; i++)
        {
            PdfPageInfo page = Pages[i];
            (double widthDip, double heightDip) = FitThumbnail(page, Rotation);
            bool isCur = (page.Index == CurrentPageIndex);
            Thumbnails.Add(new ThumbnailItemViewModel(page.Index, widthDip, heightDip)
            {
                Owner = this,
                IsNightMode = IsNightMode,
                IsSelected = isCur,
                IsCurrentPage = isCur,
            });
        }

        _lastSelectedThumbnailIndex = Math.Clamp(CurrentPageIndex, 0, Math.Max(0, Thumbnails.Count - 1));
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

        if (active)
        {
            EnsureThumbnailsInitialized();
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
                if (item.IsRealized && (item.Image is null || item.Image.IsDisposed))
                {
                    if (item.Image is { IsDisposed: true })
                    {
                        item.ClearImage();
                    }

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

        if (item.Image is { IsDisposed: true })
        {
            item.ClearImage();
        }

        RequestThumbnail(item, _thumbnailScale);
    }

    /// <summary>
    /// Discards all realized thumbnail bitmaps and cancels any in-flight thumbnail render requests
    /// when the sidebar is closed or navigated away from the Thumbnails tab.
    /// </summary>
    public void DeactivateAndClearThumbnails()
    {
        if (_isDisposed)
        {
            return;
        }

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

        _tileCache.RemoveAllThumbnails(DocumentId);
    }

    /// <summary>
    /// Releases cached thumbnail bitmaps outside <c>[currentPage - margin, currentPage + margin]</c>
    /// when the application has been minimized in the background. Realized thumbnails will automatically
    /// re-render when <see cref="SetThumbnailsActive"/> is re-enabled upon window restoration.
    /// </summary>
    /// <param name="currentPage">Zero-based active page index.</param>
    /// <param name="margin">Adjacent page margin to retain (default 1).</param>
    public void TrimThumbnailsForBackground(int currentPage, int margin = 1)
    {
        if (_isDisposed)
        {
            return;
        }

        foreach (ThumbnailItemViewModel item in Thumbnails)
        {
            if (Math.Abs(item.PageIndex - currentPage) > margin)
            {
                if (item.LoadCancellation is not null)
                {
                    item.LoadCancellation.Cancel();
                    item.LoadCancellation.Dispose();
                    item.LoadCancellation = null;
                }

                item.ClearImage();
            }
        }
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
        if (item.Image is { IsDisposed: true })
        {
            item.ClearImage();
        }

        if (item.Image is { IsDisposed: false } || item.LoadCancellation is not null || _isDisposed)
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
            if (cachedEntry is PdfBitmapEntry { IsDisposed: false } bitmapEntry)
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
            // Yield briefly so thumbnails that fly past the sidebar viewport during high-speed scrubbing
            // are cancelled on recycle before ever enqueuing native PDFium rasterization jobs.
            await Task.Delay(25, cts.Token);

            if (_isDisposed || cts.IsCancellationRequested || item.LoadCancellation != cts || !item.IsRealized || !_thumbnailsActive)
            {
                return;
            }

            // Do NOT use ConfigureAwait(false) here: resuming on the UI thread guarantees thread-safe
            // mutation of ThumbnailItemViewModel.Image (which is bound via x:Bind Mode=OneWay in ThumbnailsTabPage)
            // and eliminates race conditions with OnThumbnailRecycled.
            PdfBitmapLease lease = await _pdfService.RenderAsync(
                request,
                PdfJobPriority.Thumbnail,
                cts.Token);

            if (_isDisposed || cts.IsCancellationRequested || item.LoadCancellation != cts || !item.IsRealized || !_thumbnailsActive)
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
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

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
        Thumbnails.Clear();
        DisplayedRows.Clear();
        AnnotationsSummary.Clear();
        Signatures.Clear();
        _annotationsByPage.Clear();
        SelectedAnnotation = null;

        _tileCache.RemoveDocument(DocumentId);
        _ = _pdfService.CloseDocumentAsync(DocumentId);

        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: true);
    }
}
