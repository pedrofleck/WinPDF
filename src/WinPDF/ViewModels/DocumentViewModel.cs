using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinPDF.ViewModels;

/// <summary>
/// View model representing the active PDF document state, navigation, rotation, and collections.
/// </summary>
public sealed partial class DocumentViewModel : ObservableObject
{
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
    /// Gets or sets the current zoom factor (1.0 = 100%).
    /// </summary>
    [ObservableProperty]
    public partial double ZoomFactor { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the clockwise rotation angle in degrees (0, 90, 180, 270).
    /// </summary>
    [ObservableProperty]
    public partial int Rotation { get; set; }

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
    /// Initializes a new instance of the <see cref="DocumentViewModel"/> class.
    /// </summary>
    /// <param name="filePath">Path to the document file.</param>
    /// <param name="pageCount">Initial number of pages.</param>
    public DocumentViewModel(string filePath, int pageCount = 1)
    {
        FilePath = filePath;
        FileName = System.IO.Path.GetFileName(filePath);
        PageCount = pageCount;
        CurrentPageIndex = 0;

        if (System.IO.File.Exists(filePath))
        {
            try
            {
                FileSizeBytes = new System.IO.FileInfo(filePath).Length;
            }
            catch
            {
                FileSizeBytes = 0;
            }
        }

        for (int i = 0; i < pageCount; i++)
        {
            Thumbnails.Add(new ThumbnailItemViewModel(i));
        }

        if (Thumbnails.Count > 0)
        {
            Thumbnails[0].IsSelected = true;
        }

        UpdateIsCurrentPageBookmarked();
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentPageNumber));

        for (int i = 0; i < Thumbnails.Count; i++)
        {
            Thumbnails[i].IsSelected = (i == value);
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
    /// Navigates to the specified zero-based page index.
    /// </summary>
    /// <param name="pageIndex">Target zero-based page index.</param>
    public void GoToPage(int pageIndex)
    {
        if (PageCount > 0)
        {
            CurrentPageIndex = Math.Clamp(pageIndex, 0, PageCount - 1);
        }
    }

    /// <summary>
    /// Moves to the previous page if available.
    /// </summary>
    public void PreviousPage()
    {
        if (CurrentPageIndex > 0)
        {
            CurrentPageIndex--;
        }
    }

    /// <summary>
    /// Moves to the next page if available.
    /// </summary>
    public void NextPage()
    {
        if (CurrentPageIndex < PageCount - 1)
        {
            CurrentPageIndex++;
        }
    }

    /// <summary>
    /// Multiplies the current zoom factor by √2 (~1.414).
    /// </summary>
    public void ZoomIn()
    {
        ZoomFactor = Math.Min(8.0, ZoomFactor * Math.Sqrt(2));
    }

    /// <summary>
    /// Divides the current zoom factor by √2 (~1.414).
    /// </summary>
    public void ZoomOut()
    {
        ZoomFactor = Math.Max(0.25, ZoomFactor / Math.Sqrt(2));
    }
}
