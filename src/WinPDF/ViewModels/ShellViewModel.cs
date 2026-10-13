using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinPDF.Helpers;
using WinPDF.Models;
using WinPDF.Services;

namespace WinPDF.ViewModels;

/// <summary>
/// Top-level shell view model orchestrating the window chrome, navigation sidebar,
/// tool selection, active document, theme preferences, and system dialogs.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IFilePickerService _filePickerService;
    private readonly IDialogService _dialogService;

    /// <summary>
    /// Gets or sets a value indicating whether the navigation sidebar pane is visible.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSidebarOpen { get; set; } = true;

    /// <summary>
    /// Gets or sets the active tab displayed in the sidebar (Thumbnails, Outline, Annotations, Bookmarks, Attachments).
    /// </summary>
    [ObservableProperty]
    public partial SidebarTab SidebarTab { get; set; } = SidebarTab.Thumbnails;

    /// <summary>
    /// Gets or sets the active page view mode (Continuous vertical scroll or Paged slide flip).
    /// </summary>
    [ObservableProperty]
    public partial ViewMode ViewMode { get; set; } = ViewMode.Continuous;

    /// <summary>
    /// Gets or sets the active interaction tool on the document canvas.
    /// </summary>
    [ObservableProperty]
    public partial AnnotationTool ActiveTool { get; set; } = AnnotationTool.Select;

    /// <summary>
    /// Gets or sets a value indicating whether touch contacts are routed to drawing instead of panning/zooming.
    /// </summary>
    [ObservableProperty]
    public partial bool IsTouchDrawingEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the floating annotation toolbar is expanded.
    /// </summary>
    [ObservableProperty]
    public partial bool IsAnnotationToolbarOpen { get; set; }

    /// <summary>
    /// Gets or sets the visual theme preference of the application.
    /// </summary>
    [ObservableProperty]
    public partial AppThemePreference CurrentTheme { get; set; } = AppThemePreference.System;

    /// <summary>
    /// Gets or sets a value indicating whether the full-page settings view is open.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether dual page mode is enabled.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDualPage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether odd pages are aligned on the left.
    /// </summary>
    [ObservableProperty]
    public partial bool IsOddPagesLeft { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether right-to-left document flow is enabled.
    /// </summary>
    [ObservableProperty]
    public partial bool IsRightToLeft { get; set; }

    /// <summary>
    /// Gets or sets the active PDF document, or <c>null</c> if no document is currently open.
    /// </summary>
    [ObservableProperty]
    public partial DocumentViewModel? CurrentDocument { get; set; }

    /// <summary>
    /// Gets a value indicating whether a document is currently loaded.
    /// </summary>
    public bool HasDocument => CurrentDocument is not null;

    /// <summary>
    /// Gets or sets the window title string.
    /// </summary>
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>
    /// Gets or sets the formatted total page count string (e.g. "of 10").
    /// </summary>
    [ObservableProperty]
    public partial string FormattedPageCount { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the formatted zoom percentage string (e.g. "100%").
    /// </summary>
    [ObservableProperty]
    public partial string FormattedZoomPercent { get; set; } = "100%";

    /// <summary>
    /// Gets or sets a value indicating whether the window is in presentation or full screen mode.
    /// </summary>
    [ObservableProperty]
    public partial bool IsFullScreen { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShellViewModel"/> class.
    /// </summary>
    /// <param name="filePickerService">File open and save picker service.</param>
    /// <param name="dialogService">Message and confirmation dialog service.</param>
    public ShellViewModel(IFilePickerService filePickerService, IDialogService dialogService)
    {
        _filePickerService = filePickerService;
        _dialogService = dialogService;
        Title = ResourceStrings.Get(ResourceKeys.AppDisplayName);
        UpdateFormattedStrings();
    }

    partial void OnCurrentDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnDocumentPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnDocumentPropertyChanged;
        }

        UpdateTitle();
        UpdateFormattedStrings();
        OnPropertyChanged(nameof(HasDocument));
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.FileName) or nameof(DocumentViewModel.HasUnsavedChanges))
        {
            UpdateTitle();
        }
        else if (e.PropertyName is nameof(DocumentViewModel.PageCount) or nameof(DocumentViewModel.ZoomFactor))
        {
            UpdateFormattedStrings();
        }
    }

    private void UpdateTitle()
    {
        if (CurrentDocument is null)
        {
            Title = ResourceStrings.Get(ResourceKeys.AppDisplayName);
        }
        else if (CurrentDocument.HasUnsavedChanges)
        {
            Title = ResourceStrings.Format(ResourceKeys.WindowTitleModifiedFormat, CurrentDocument.FileName);
        }
        else
        {
            Title = ResourceStrings.Format(ResourceKeys.WindowTitleFormat, CurrentDocument.FileName);
        }
    }

    private void UpdateFormattedStrings()
    {
        int count = CurrentDocument?.PageCount ?? 0;
        FormattedPageCount = ResourceStrings.Format(ResourceKeys.PageCountFormat, count);

        int zoomInt = (int)Math.Round((CurrentDocument?.ZoomFactor ?? 1.0) * 100);
        FormattedZoomPercent = ResourceStrings.Format(ResourceKeys.ZoomPercentFormat, zoomInt);
    }

    /// <summary>
    /// Prompts the user to pick and open a PDF document.
    /// </summary>
    [RelayCommand]
    public async Task OpenFileAsync(CancellationToken cancellationToken)
    {
        string? path = await _filePickerService.PickOpenPdfAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(path))
        {
            await OpenFilePathAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens the specified PDF file path.
    /// </summary>
    /// <param name="filePath">Absolute path to the PDF file.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    public Task OpenFilePathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        DocumentViewModel doc = new(filePath, pageCount: 8);
        doc.Outline.Add(new OutlineItemViewModel("Chapter 1: Introduction", 0));
        doc.Outline.Add(new OutlineItemViewModel("Chapter 2: Architecture", 2));
        doc.Outline.Add(new OutlineItemViewModel("Chapter 3: Rendering Pipeline", 5));
        doc.Bookmarks.Add(new BookmarkItemViewModel(0));
        doc.AnnotationsSummary.Add(new AnnotationItemViewModel("Highlight on page 1"));
        doc.Attachments.Add(new AttachmentItemViewModel("supplementary_data.xlsx"));

        CurrentDocument = doc;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves the current document annotations.
    /// </summary>
    [RelayCommand]
    public Task SaveAsync()
    {
        if (CurrentDocument is not null)
        {
            CurrentDocument.HasUnsavedChanges = false;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Prompts the user for a new file location and saves a copy of the document.
    /// </summary>
    [RelayCommand]
    public async Task SaveAsAsync(CancellationToken cancellationToken)
    {
        if (CurrentDocument is null)
        {
            return;
        }

        string? targetPath = await _filePickerService.PickSavePdfAsync(CurrentDocument.FileName, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            CurrentDocument.FilePath = targetPath;
            CurrentDocument.FileName = System.IO.Path.GetFileName(targetPath);
            CurrentDocument.HasUnsavedChanges = false;
        }
    }

    /// <summary>
    /// Initiates document printing.
    /// </summary>
    [RelayCommand]
    public Task PrintAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens a copy of the current document.
    /// </summary>
    [RelayCommand]
    public Task OpenCopyAsync(CancellationToken cancellationToken)
    {
        if (CurrentDocument is null)
        {
            return Task.CompletedTask;
        }

        return OpenFilePathAsync(CurrentDocument.FilePath, cancellationToken);
    }

    /// <summary>
    /// Launches the external system application picker to open the current PDF file.
    /// </summary>
    [RelayCommand]
    public async Task OpenWithAsync()
    {
        if (CurrentDocument is null || string.IsNullOrWhiteSpace(CurrentDocument.FilePath) || !System.IO.File.Exists(CurrentDocument.FilePath))
        {
            return;
        }

        var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(CurrentDocument.FilePath);
        var options = new Windows.System.LauncherOptions { DisplayApplicationPicker = true };
        await Windows.System.Launcher.LaunchFileAsync(storageFile, options);
    }

    /// <summary>
    /// Enters slide presentation mode.
    /// </summary>
    [RelayCommand]
    public void StartPresentation()
    {
        ViewMode = ViewMode.Paged;
        IsFullScreen = true;
    }

    /// <summary>
    /// Toggles between continuous scrolling and paged slide viewing modes.
    /// </summary>
    [RelayCommand]
    public void ToggleContinuousMode()
    {
        ViewMode = ViewMode == ViewMode.Continuous ? ViewMode.Paged : ViewMode.Continuous;
    }

    /// <summary>
    /// Toggles the navigation sidebar visibility.
    /// </summary>
    [RelayCommand]
    public void ToggleSidebar()
    {
        IsSidebarOpen = !IsSidebarOpen;
    }

    /// <summary>
    /// Switches the active view mode between Continuous and Paged.
    /// </summary>
    /// <param name="mode">The target view mode.</param>
    [RelayCommand]
    public void SetViewMode(ViewMode mode)
    {
        if (ViewMode != mode)
        {
            ViewMode = mode;
        }
    }

    /// <summary>
    /// Switches the active sidebar tab.
    /// </summary>
    /// <param name="tab">The target sidebar tab.</param>
    [RelayCommand]
    public void SetSidebarTab(SidebarTab tab)
    {
        if (SidebarTab != tab)
        {
            SidebarTab = tab;
        }
    }

    /// <summary>
    /// Selects the active annotation or interaction tool.
    /// </summary>
    /// <param name="tool">The target tool.</param>
    [RelayCommand]
    public void SetTool(AnnotationTool tool)
    {
        if (ActiveTool != tool)
        {
            ActiveTool = tool;
        }
    }

    /// <summary>
    /// Toggles the touch drawing mode on or off.
    /// </summary>
    [RelayCommand]
    public void ToggleTouchDrawing()
    {
        IsTouchDrawingEnabled = !IsTouchDrawingEnabled;
    }

    /// <summary>
    /// Toggles the visibility of the floating annotation tool bar.
    /// </summary>
    [RelayCommand]
    public void ToggleAnnotationToolbar()
    {
        IsAnnotationToolbarOpen = !IsAnnotationToolbarOpen;
    }

    /// <summary>
    /// Toggles favorite/bookmark status on the current page.
    /// </summary>
    [RelayCommand]
    public void ToggleBookmarkCurrentPage()
    {
        CurrentDocument?.ToggleBookmarkCurrentPage();
    }

    /// <summary>
    /// Rotates the active document 90 degrees clockwise.
    /// </summary>
    [RelayCommand]
    public void RotateClockwise()
    {
        CurrentDocument?.RotateClockwise();
    }

    /// <summary>
    /// Navigates to the previous page of the active document.
    /// </summary>
    [RelayCommand]
    public void PreviousPage()
    {
        CurrentDocument?.PreviousPage();
    }

    /// <summary>
    /// Navigates to the next page of the active document.
    /// </summary>
    [RelayCommand]
    public void NextPage()
    {
        CurrentDocument?.NextPage();
    }

    /// <summary>
    /// Increases the document zoom level.
    /// </summary>
    [RelayCommand]
    public void ZoomIn()
    {
        CurrentDocument?.ZoomIn();
    }

    /// <summary>
    /// Decreases the document zoom level.
    /// </summary>
    [RelayCommand]
    public void ZoomOut()
    {
        CurrentDocument?.ZoomOut();
    }

    /// <summary>
    /// Sets an explicit zoom factor on the active document.
    /// </summary>
    /// <param name="zoom">Target zoom factor (1.0 = 100%).</param>
    [RelayCommand]
    public void SetZoom(double zoom)
    {
        if (CurrentDocument is not null)
        {
            double target = Math.Clamp(zoom, 0.25, 8.0);
            if (Math.Abs(CurrentDocument.ZoomFactor - target) > 0.0001)
            {
                CurrentDocument.ZoomFactor = target;
            }
        }
    }

    /// <summary>
    /// Adjusts zoom factor to fit page width.
    /// </summary>
    [RelayCommand]
    public void FitWidth()
    {
        SetZoom(1.25);
    }

    /// <summary>
    /// Adjusts zoom factor to fit the entire page height.
    /// </summary>
    [RelayCommand]
    public void FitPage()
    {
        SetZoom(1.0);
    }

    /// <summary>
    /// Toggles full screen / presentation mode.
    /// </summary>
    [RelayCommand]
    public void ToggleFullScreen()
    {
        IsFullScreen = !IsFullScreen;
    }

    /// <summary>
    /// Sets application visual theme preference.
    /// </summary>
    /// <param name="theme">Target theme preference.</param>
    [RelayCommand]
    public void SetTheme(AppThemePreference theme)
    {
        if (CurrentTheme != theme)
        {
            CurrentTheme = theme;
        }
    }

    /// <summary>
    /// Opens the full-page settings view.
    /// </summary>
    [RelayCommand]
    public void OpenSettings()
    {
        if (!IsSettingsOpen)
        {
            IsSettingsOpen = true;
        }
    }

    /// <summary>
    /// Closes the full-page settings view and returns to the document viewing area.
    /// </summary>
    [RelayCommand]
    public void CloseSettings()
    {
        if (IsSettingsOpen)
        {
            IsSettingsOpen = false;
        }
    }

    /// <summary>
    /// Toggles the settings view between open and closed.
    /// </summary>
    [RelayCommand]
    public void ToggleSettings()
    {
        IsSettingsOpen = !IsSettingsOpen;
    }

    /// <summary>
    /// Displays the document properties dialog.
    /// </summary>
    [RelayCommand]
    public async Task ShowPropertiesAsync()
    {
        if (CurrentDocument is null)
        {
            return;
        }

        string title = ResourceStrings.Get(ResourceKeys.DocPropsTitle);
        string close = ResourceStrings.Get(ResourceKeys.DialogClose);
        string body = $"{ResourceStrings.Get(ResourceKeys.DocPropsFileName)} {CurrentDocument.FileName}\n" +
                      $"{ResourceStrings.Get(ResourceKeys.DocPropsFilePath)} {CurrentDocument.FilePath}\n" +
                      $"{ResourceStrings.Get(ResourceKeys.DocPropsPageCount)} {CurrentDocument.PageCount}\n" +
                      $"{ResourceStrings.Get(ResourceKeys.DocPropsFileSize)} {CurrentDocument.FileSizeBytes / 1024:N0} KB";

        await _dialogService.ShowMessageAsync(title, body, close).ConfigureAwait(false);
    }

    /// <summary>
    /// Displays the application information dialog.
    /// </summary>
    [RelayCommand]
    public async Task ShowAboutAsync()
    {
        string name = ResourceStrings.Get(ResourceKeys.AppDisplayName);
        string description = ResourceStrings.Get(ResourceKeys.AppDescription);
        string close = ResourceStrings.Get(ResourceKeys.DialogClose);

        await _dialogService.ShowMessageAsync(name, description, close).ConfigureAwait(false);
    }
}
