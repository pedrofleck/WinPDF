using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinPDF.Helpers;
using WinPDF.Interop.Printing;
using WinPDF.Models;
using WinPDF.Rendering;
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
    private readonly IPdfService _pdfService;
    private readonly TileCache _tileCache;
    private readonly IWindowContext _windowContext;
    private readonly ISettingsService _settingsService;
    private readonly bool _isInitializingSettings;
    private string? _pendingNotificationTitle;
    private string? _pendingNotificationMessage;

    /// <summary>
    /// Gets the application settings persistence service.
    /// </summary>
    public ISettingsService SettingsService => _settingsService;

    /// <summary>
    /// Gets the persisted default zoom option index (0: Fit Width, 1: Fit Page, 2: 100%).
    /// </summary>
    public int DefaultZoomIndex => _settingsService.Settings.DefaultZoomIndex;

    /// <summary>
    /// Event raised when a non-intrusive top InfoBar notification should be shown.
    /// </summary>
    public event Action<string, string>? NotificationRequested;

    /// <summary>
    /// Event raised when the user changes the default zoom preference in Settings.
    /// </summary>
    public event Action<int>? DefaultZoomPreferenceChanged;

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

    partial void OnIsSidebarOpenChanged(bool value)
    {
        CurrentDocument?.SetThumbnailsActive(!IsSettingsOpen && value && SidebarTab == SidebarTab.Thumbnails);
        if (!_isInitializingSettings && !IsPresentationMode && !IsFullScreen)
        {
            _settingsService.Update(s => s.IsSidebarOpen = value);
        }
    }

    partial void OnSidebarTabChanged(SidebarTab value)
    {
        CurrentDocument?.SetThumbnailsActive(!IsSettingsOpen && IsSidebarOpen && value == SidebarTab.Thumbnails);
    }

    /// <summary>
    /// Gets or sets the active page view mode (Continuous vertical scroll or Paged slide flip).
    /// </summary>
    [ObservableProperty]
    public partial ViewMode ViewMode { get; set; } = ViewMode.Continuous;

    partial void OnViewModeChanged(ViewMode value)
    {
        if (CurrentDocument is not null)
        {
            bool continuous = value == ViewMode.Continuous;
            if (CurrentDocument.IsContinuous != continuous)
            {
                CurrentDocument.IsContinuous = continuous;
            }
        }
    }

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

    partial void OnIsTouchDrawingEnabledChanged(bool value)
    {
        if (!_isInitializingSettings)
        {
            _settingsService.Update(s => s.DefaultTouchDrawing = value);
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the floating annotation toolbar is expanded.
    /// </summary>
    [ObservableProperty]
    public partial bool IsAnnotationToolbarOpen { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the floating document search bar is open.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSearchOpen { get; set; }

    /// <summary>
    /// Event raised when the floating search bar should receive keyboard focus and select its query text.
    /// </summary>
    public event Action? SearchFocusRequested;

    /// <summary>
    /// Gets or sets the visual theme preference of the application.
    /// </summary>
    [ObservableProperty]
    public partial AppThemePreference CurrentTheme { get; set; } = AppThemePreference.System;

    partial void OnCurrentThemeChanged(AppThemePreference value)
    {
        if (!_isInitializingSettings)
        {
            _settingsService.Update(s => s.Theme = value);
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether document night / inverted reading mode is active.
    /// This is independent of the application UI shell theme configured in settings.
    /// </summary>
    [ObservableProperty]
    public partial bool IsNightMode { get; set; }

    partial void OnIsNightModeChanged(bool value)
    {
        if (CurrentDocument is not null && CurrentDocument.IsNightMode != value)
        {
            CurrentDocument.IsNightMode = value;
        }
    }

    /// <summary>
    /// Toggles the document night reading mode.
    /// </summary>
    [RelayCommand]
    public void ToggleNightMode()
    {
        IsNightMode = !IsNightMode;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the full-page settings view is open.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    partial void OnIsSettingsOpenChanged(bool value)
    {
        CurrentDocument?.SetThumbnailsActive(!value && IsSidebarOpen && SidebarTab == SidebarTab.Thumbnails);
    }


    /// <summary>
    /// Gets or sets a value indicating whether dual page mode is enabled.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDualPage { get; set; }

    partial void OnIsDualPageChanged(bool value)
    {
        if (CurrentDocument is not null && CurrentDocument.IsDoublePage != value)
        {
            CurrentDocument.IsDoublePage = value;
        }
    }

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
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    public partial DocumentViewModel? CurrentDocument { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an initial document is opening from launch arguments.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    public partial bool IsInitialDocumentOpening { get; set; }

    /// <summary>
    /// Gets a value indicating whether a document is currently loaded or opening.
    /// </summary>
    public bool HasDocument => CurrentDocument is not null || IsInitialDocumentOpening;

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
    /// Gets or sets a value indicating whether the window is in full screen mode (F11).
    /// </summary>
    [ObservableProperty]
    public partial bool IsFullScreen { get; set; }

    partial void OnIsFullScreenChanged(bool value)
    {
        if (!value)
        {
            IsPresentationMode = false;
        }
    }

    private bool _hasSavedPresentationState;
    private bool _sidebarOpenBeforePresentation;
    private ViewMode _viewModeBeforePresentation = ViewMode.Continuous;
    private bool _dualPageBeforePresentation;
    private double _zoomBeforePresentation = 1.0;

    /// <summary>
    /// Gets the zoom factor captured right before entering presentation mode.
    /// </summary>
    public double ZoomBeforePresentation => _zoomBeforePresentation;

    /// <summary>
    /// Gets or sets a value indicating whether slide presentation mode is active (F5).
    /// </summary>
    [ObservableProperty]
    public partial bool IsPresentationMode { get; set; }

    partial void OnIsPresentationModeChanged(bool value)
    {
        if (CurrentDocument is not null)
        {
            CurrentDocument.IsPresentationMode = value;
        }

        if (!value && _hasSavedPresentationState)
        {
            _hasSavedPresentationState = false;
            int activePage = CurrentDocument?.CurrentPageIndex ?? 0;

            if (CurrentDocument is not null)
            {
                IsDualPage = _dualPageBeforePresentation;
                CurrentDocument.IsDoublePage = _dualPageBeforePresentation;
                bool restoreContinuous = _viewModeBeforePresentation == ViewMode.Continuous;
                ViewMode = _viewModeBeforePresentation;
                CurrentDocument.IsContinuous = restoreContinuous;
                CurrentDocument.GoToPage(activePage);
                if (_zoomBeforePresentation > 0.05)
                {
                    CurrentDocument.ZoomFactor = _zoomBeforePresentation;
                }
            }

            if (_sidebarOpenBeforePresentation)
            {
                IsSidebarOpen = true;
                _sidebarOpenBeforePresentation = false;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShellViewModel"/> class.
    /// </summary>
    /// <param name="filePickerService">File open and save picker service.</param>
    /// <param name="dialogService">Message and confirmation dialog service.</param>
    /// <param name="pdfService">PDF rendering and document engine.</param>
    /// <param name="tileCache">Bitmap and tile cache.</param>
    /// <param name="windowContext">Window context providing UI dispatcher queue access.</param>
    /// <param name="settingsService">Settings persistence service.</param>
    public ShellViewModel(
        IFilePickerService filePickerService,
        IDialogService dialogService,
        IPdfService pdfService,
        TileCache tileCache,
        IWindowContext windowContext,
        ISettingsService settingsService)
    {
        _filePickerService = filePickerService;
        _dialogService = dialogService;
        _pdfService = pdfService;
        _tileCache = tileCache;
        _windowContext = windowContext;
        _settingsService = settingsService;

        _isInitializingSettings = true;
        CurrentTheme = _settingsService.Settings.Theme;
        ViewMode = _settingsService.Settings.DefaultViewMode;
        IsTouchDrawingEnabled = _settingsService.Settings.DefaultTouchDrawing;
        IsSidebarOpen = _settingsService.Settings.IsSidebarOpen;
        _isInitializingSettings = false;

        Title = ResourceStrings.Get(ResourceKeys.AppDisplayName);
        UpdateFormattedStrings();
    }

    /// <summary>
    /// Queues and raises a non-intrusive top InfoBar notification when the last opened PDF file is missing on startup.
    /// </summary>
    /// <param name="fileName">The missing file name to display in the notification message.</param>
    public void ShowMissingLastDocumentNotification(string fileName)
    {
        string title = ResourceStrings.Get(ResourceKeys.MissingLastDocInfoBarTitle);
        string message = ResourceStrings.Format(ResourceKeys.MissingLastDocInfoBarMessageFormat, fileName);
        _pendingNotificationTitle = title;
        _pendingNotificationMessage = message;
        NotificationRequested?.Invoke(title, message);
    }

    /// <summary>
    /// Attempts to consume any pending startup notification so it is displayed once when the document view loads.
    /// </summary>
    /// <param name="title">The notification title.</param>
    /// <param name="message">The notification body text.</param>
    /// <returns><c>true</c> if a pending notification was consumed; otherwise, <c>false</c>.</returns>
    public bool TryConsumePendingNotification(out string title, out string message)
    {
        if (!string.IsNullOrEmpty(_pendingNotificationMessage))
        {
            title = _pendingNotificationTitle ?? string.Empty;
            message = _pendingNotificationMessage;
            _pendingNotificationTitle = null;
            _pendingNotificationMessage = null;
            return true;
        }

        title = string.Empty;
        message = string.Empty;
        return false;
    }

    /// <summary>
    /// Notifies listeners that the default zoom preference changed in Settings.
    /// </summary>
    /// <param name="zoomIndex">The new default zoom index (0: Fit Width, 1: Fit Page, 2: 100%).</param>
    public void NotifyDefaultZoomPreferenceChanged(int zoomIndex)
    {
        DefaultZoomPreferenceChanged?.Invoke(zoomIndex);
    }

    partial void OnCurrentDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnDocumentPropertyChanged;
            oldValue.Dispose();
        }

        if (newValue is not null)
        {
            newValue.IsNightMode = IsNightMode;
            newValue.IsContinuous = (ViewMode == ViewMode.Continuous);
            newValue.IsDoublePage = IsDualPage;
            newValue.IsPresentationMode = IsPresentationMode;
            newValue.PropertyChanged += OnDocumentPropertyChanged;
            newValue.SetThumbnailsActive(IsSidebarOpen && SidebarTab == SidebarTab.Thumbnails);
        }
        else
        {
            IsSearchOpen = false;
            IsAnnotationToolbarOpen = false;
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
        else if (e.PropertyName == nameof(DocumentViewModel.IsContinuous))
        {
            if (CurrentDocument is not null)
            {
                ViewMode = CurrentDocument.IsContinuous ? ViewMode.Continuous : ViewMode.Paged;
            }
        }
        else if (e.PropertyName == nameof(DocumentViewModel.IsDoublePage))
        {
            if (CurrentDocument is not null)
            {
                IsDualPage = CurrentDocument.IsDoublePage;
            }
        }
        else if (e.PropertyName == nameof(DocumentViewModel.CurrentPageIndex))
        {
            if (CurrentDocument is not null)
            {
                _settingsService.Settings.LastOpenedPageIndex = CurrentDocument.CurrentPageIndex;
            }
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
        string? path = await _filePickerService.PickOpenPdfAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(path))
        {
            await OpenFilePathAsync(path, cancellationToken);
        }
    }

    /// <summary>
    /// Opens the specified PDF file path.
    /// </summary>
    /// <param name="filePath">Absolute path to the PDF file.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    public Task OpenFileFromPathAsync(string filePath, CancellationToken cancellationToken = default)
        => OpenFilePathAsync(filePath, cancellationToken);

    /// <summary>
    /// Opens the specified PDF file path.
    /// </summary>
    /// <param name="filePath">Absolute path to the PDF file.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <param name="isStartupSessionRestore">Whether this open request is an automatic startup restore of the last document.</param>
    /// <param name="initialPageIndex">Optional initial zero-based page index to restore.</param>
    public async Task OpenFilePathAsync(
        string filePath,
        CancellationToken cancellationToken = default,
        bool isStartupSessionRestore = false,
        int initialPageIndex = 0)
    {
        try
        {
            if (isStartupSessionRestore && !System.IO.File.Exists(filePath))
            {
                string missingName = System.IO.Path.GetFileName(filePath);
                if (string.IsNullOrWhiteSpace(missingName))
                {
                    missingName = filePath;
                }

                _settingsService.Update(s =>
                {
                    s.LastOpenedFilePath = null;
                    s.LastOpenedPageIndex = 0;
                });

                if (_windowContext.DispatcherQueue?.HasThreadAccess == true)
                {
                    ShowMissingLastDocumentNotification(missingName);
                }
                else
                {
                    _windowContext.DispatcherQueue?.TryEnqueue(() => ShowMissingLastDocumentNotification(missingName));
                }

                return;
            }

            PdfDocumentInfo docInfo = await _pdfService.OpenDocumentAsync(filePath, null, cancellationToken);
            var outline = await _pdfService.GetOutlineAsync(docInfo.DocumentId, cancellationToken);
            var attachments = await _pdfService.GetAttachmentNamesAsync(docInfo.DocumentId, cancellationToken);
            var metadata = await _pdfService.GetDocumentMetadataAsync(docInfo.DocumentId, cancellationToken);

            DocumentViewModel doc = new(_pdfService, _tileCache, docInfo, outline, attachments, metadata);
            if (initialPageIndex > 0 && initialPageIndex < doc.PageCount)
            {
                doc.GoToPage(initialPageIndex);
            }

            _settingsService.Update(s =>
            {
                s.LastOpenedFilePath = doc.FilePath;
                s.LastOpenedPageIndex = doc.CurrentPageIndex;
            });

            if (_windowContext.DispatcherQueue.HasThreadAccess)
            {
                CurrentDocument = doc;
            }
            else
            {
                _windowContext.DispatcherQueue.TryEnqueue(() => CurrentDocument = doc);
            }
        }
        catch (PdfiumException ex)
        {
            if (isStartupSessionRestore && ex.ErrorCode == PdfiumErrorCode.File)
            {
                string missingName = System.IO.Path.GetFileName(filePath);
                _settingsService.Update(s =>
                {
                    s.LastOpenedFilePath = null;
                    s.LastOpenedPageIndex = 0;
                });
                ShowMissingLastDocumentNotification(missingName);
                return;
            }

            string errorReason = ex.ErrorCode switch
            {
                PdfiumErrorCode.Password => ResourceStrings.Get(ResourceKeys.ErrorInvalidPassword),
                PdfiumErrorCode.File => ResourceStrings.Get(ResourceKeys.ErrorFileNotFound),
                PdfiumErrorCode.Format => ResourceStrings.Get(ResourceKeys.ErrorInvalidFormat),
                PdfiumErrorCode.Security => ResourceStrings.Get(ResourceKeys.ErrorUnsupportedSecurity),
                PdfiumErrorCode.Page => ResourceStrings.Get(ResourceKeys.ErrorPage),
                _ => ResourceStrings.Get(ResourceKeys.ErrorUnknown)
            };

            string title = ResourceStrings.Get(ResourceKeys.DialogOpenErrorTitle);
            string content = ResourceStrings.Format(ResourceKeys.DialogOpenErrorContent, System.IO.Path.GetFileName(filePath), errorReason);
            string close = ResourceStrings.Get(ResourceKeys.DialogClose);
            await _dialogService.ShowMessageAsync(title, content, close, cancellationToken);
        }
        catch (Exception ex)
        {
            if (isStartupSessionRestore)
            {
                string missingName = System.IO.Path.GetFileName(filePath);
                _settingsService.Update(s =>
                {
                    s.LastOpenedFilePath = null;
                    s.LastOpenedPageIndex = 0;
                });
                ShowMissingLastDocumentNotification(missingName);
                return;
            }

            string title = ResourceStrings.Get(ResourceKeys.DialogOpenErrorTitle);
            string content = ResourceStrings.Format(ResourceKeys.DialogOpenErrorContent, System.IO.Path.GetFileName(filePath), ex.Message);
            string close = ResourceStrings.Get(ResourceKeys.DialogClose);
            await _dialogService.ShowMessageAsync(title, content, close, cancellationToken);
        }
        finally
        {
            if (IsInitialDocumentOpening)
            {
                if (_windowContext.DispatcherQueue?.HasThreadAccess == true)
                {
                    IsInitialDocumentOpening = false;
                }
                else
                {
                    _windowContext.DispatcherQueue?.TryEnqueue(() => IsInitialDocumentOpening = false);
                }
            }
        }
    }

    private bool _isPrinting;

    /// <summary>
    /// Event raised when the active document should be printed using the Windows 11 PrintManager preview pipeline.
    /// </summary>
    public event Func<Task>? PrintRequested;

    /// <summary>
    /// Gets the command that opens the native print dialog and spools the current document.
    /// </summary>
    public IAsyncRelayCommand PrintDocumentCommand => PrintCommand;

    /// <summary>
    /// Saves the current document (including any rotation and future annotation edits) in-place,
    /// or prompts for a target location if the file cannot be overwritten directly.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    [RelayCommand]
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentDocument is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(CurrentDocument.FilePath) || !System.IO.File.Exists(CurrentDocument.FilePath))
        {
            await SaveAsAsync(cancellationToken);
            return;
        }

        try
        {
            PdfSaveOptions options = new(
                UserRotationDegrees: CurrentDocument.Rotation,
                PageEditCommitters: null,
                SaveFlags: 0);

            await _pdfService.SaveDocumentAsync(
                CurrentDocument.DocumentId,
                CurrentDocument.FilePath,
                options,
                cancellationToken);

            CurrentDocument.HasUnsavedChanges = false;
            CurrentDocument.RefreshFileInfo();
            UpdateTitle();
            UpdateFormattedStrings();

            string savedTitle = ResourceStrings.Get(ResourceKeys.StatusSaved);
            NotificationRequested?.Invoke(savedTitle, CurrentDocument.FileName);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            string title = ResourceStrings.Get(ResourceKeys.DialogSaveErrorTitle);
            string content = ResourceStrings.Format(ResourceKeys.DialogSaveErrorContent, CurrentDocument.FileName, ex.Message);
            string close = ResourceStrings.Get(ResourceKeys.DialogClose);
            await _dialogService.ShowMessageAsync(title, content, close, cancellationToken);
        }
    }

    /// <summary>
    /// Prompts the user for a destination file path via the native Windows Save Picker and writes
    /// the PDF document (including any committed page rotations and future annotation layers) via PDFium.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    [RelayCommand]
    public async Task SaveAsAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentDocument is null)
        {
            return;
        }

        string? targetPath = await _filePickerService.PickSavePdfAsync(CurrentDocument.FileName, cancellationToken);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        try
        {
            PdfSaveOptions options = new(
                UserRotationDegrees: CurrentDocument.Rotation,
                PageEditCommitters: null,
                SaveFlags: 0);

            await _pdfService.SaveDocumentAsync(
                CurrentDocument.DocumentId,
                targetPath,
                options,
                cancellationToken);

            CurrentDocument.FilePath = targetPath;
            CurrentDocument.FileName = System.IO.Path.GetFileName(targetPath);
            CurrentDocument.HasUnsavedChanges = false;
            CurrentDocument.RefreshFileInfo();
            UpdateTitle();
            UpdateFormattedStrings();

            _settingsService.Update(s =>
            {
                s.LastOpenedFilePath = targetPath;
                s.LastOpenedPageIndex = CurrentDocument.CurrentPageIndex;
            });

            string savedTitle = ResourceStrings.Get(ResourceKeys.StatusSaved);
            NotificationRequested?.Invoke(savedTitle, CurrentDocument.FileName);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            string title = ResourceStrings.Get(ResourceKeys.DialogSaveErrorTitle);
            string content = ResourceStrings.Format(ResourceKeys.DialogSaveErrorContent, System.IO.Path.GetFileName(targetPath), ex.Message);
            string close = ResourceStrings.Get(ResourceKeys.DialogClose);
            await _dialogService.ShowMessageAsync(title, content, close, cancellationToken);
        }
    }

    /// <summary>
    /// Opens the native Windows print dialog (<c>PrintDlgExW</c>) and spools vector GDI commands
    /// for the selected pages on the dedicated PDFium worker thread.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    public async Task PrintAsync(CancellationToken cancellationToken = default)
    {
        if (_isPrinting || CurrentDocument is null || _windowContext.WindowHandle == nint.Zero)
        {
            return;
        }

        _isPrinting = true;
        try
        {
            if (PrintRequested is not null)
            {
                await PrintRequested.Invoke();
                return;
            }

            DocumentViewModel doc = CurrentDocument;
            Dictionary<int, int>? pageRotations = null;
            if (doc.Rotation != 0)
            {
                pageRotations = new Dictionary<int, int>(doc.Pages.Count);
                for (int i = 0; i < doc.Pages.Count; i++)
                {
                    pageRotations[i] = doc.Rotation;
                }
            }

            PdfPrintJobOptions? jobOptions = Win32PrintInterop.ShowPrintDialog(
                _windowContext.WindowHandle,
                doc.FileName,
                doc.CurrentPageNumber,
                doc.PageCount,
                doc.Rotation,
                pageRotations);

            if (jobOptions is null)
            {
                return;
            }

            await _pdfService.PrintDocumentAsync(doc.DocumentId, jobOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Print failed: {ex.Message}");
        }
        finally
        {
            _isPrinting = false;
        }
    }

    /// <summary>
    /// Opens a copy of the current document in a new independent WinPDF window (Ctrl+Shift+N).
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    [RelayCommand]
    public Task OpenCopyAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentDocument is null || string.IsNullOrWhiteSpace(CurrentDocument.FilePath))
        {
            return Task.CompletedTask;
        }

        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && System.IO.File.Exists(exePath))
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = false,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(exePath) ?? System.AppContext.BaseDirectory
                };
                startInfo.ArgumentList.Add(CurrentDocument.FilePath);
                System.Diagnostics.Process.Start(startInfo);
                return Task.CompletedTask;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to spawn new window for OpenCopy: {ex.Message}");
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
    /// Enters slide presentation mode (F5).
    /// </summary>
    [RelayCommand]
    public void StartPresentation()
    {
        if (CurrentDocument is null)
        {
            return;
        }

        if (!IsPresentationMode)
        {
            _hasSavedPresentationState = true;
            _sidebarOpenBeforePresentation = IsSidebarOpen;
            _viewModeBeforePresentation = ViewMode;
            _dualPageBeforePresentation = IsDualPage;
            _zoomBeforePresentation = CurrentDocument.ZoomFactor > 0.05 ? CurrentDocument.ZoomFactor : 1.0;
        }

        int preservedPage = CurrentDocument.CurrentPageIndex;
        IsSidebarOpen = false;
        IsSearchOpen = false;
        IsAnnotationToolbarOpen = false;
        IsPresentationMode = true;
        IsFullScreen = true;
        CurrentDocument.IsDoublePage = false;
        CurrentDocument.IsContinuous = false;
        ViewMode = ViewMode.Paged;
        CurrentDocument.GoToPage(preservedPage);
    }

    /// <summary>
    /// Exits slide presentation mode (Esc).
    /// </summary>
    [RelayCommand]
    public void ExitPresentation()
    {
        IsPresentationMode = false;
        IsFullScreen = false;
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
    /// Opens the floating search bar and requests focus on the search input box.
    /// </summary>
    [RelayCommand]
    public void OpenSearch()
    {
        if (CurrentDocument is null)
        {
            return;
        }

        if (!IsSearchOpen)
        {
            IsSearchOpen = true;
        }

        SearchFocusRequested?.Invoke();
    }

    /// <summary>
    /// Closes the floating search bar.
    /// </summary>
    [RelayCommand]
    public void CloseSearch()
    {
        if (IsSearchOpen)
        {
            IsSearchOpen = false;
        }
    }

    /// <summary>
    /// Toggles the visibility of the floating search bar.
    /// </summary>
    [RelayCommand]
    public void ToggleSearch()
    {
        if (CurrentDocument is null)
        {
            IsSearchOpen = false;
            return;
        }

        IsSearchOpen = !IsSearchOpen;
        if (IsSearchOpen)
        {
            SearchFocusRequested?.Invoke();
        }
    }

    /// <summary>
    /// Closes the active PDF document and returns to the initial empty state screen.
    /// </summary>
    [RelayCommand]
    public void CloseDocument()
    {
        if (CurrentDocument is null)
        {
            return;
        }

        IsSearchOpen = false;
        IsAnnotationToolbarOpen = false;
        if (IsPresentationMode)
        {
            IsPresentationMode = false;
            IsFullScreen = false;
        }

        if (IsSettingsOpen)
        {
            IsSettingsOpen = false;
        }

        CurrentDocument = null;
        _settingsService.Update(s =>
        {
            s.LastOpenedFilePath = null;
            s.LastOpenedPageIndex = 0;
        });
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
    /// Rotates the active document 90 degrees counter-clockwise.
    /// </summary>
    [RelayCommand]
    public void RotateCounterClockwise()
    {
        CurrentDocument?.RotateCounterClockwise();
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
    /// Toggles full screen mode (F11).
    /// </summary>
    [RelayCommand]
    public void ToggleFullScreen()
    {
        IsFullScreen = !IsFullScreen;
        if (!IsFullScreen)
        {
            IsPresentationMode = false;
        }
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
    /// Displays the document properties modal dialog.
    /// </summary>
    [RelayCommand]
    public async Task TogglePropertiesAsync()
    {
        if (CurrentDocument is not null)
        {
            await _dialogService.ShowDocumentPropertiesAsync(CurrentDocument);
        }
    }

    /// <summary>
    /// Opens the folder containing the current document in File Explorer with the file selected.
    /// </summary>
    [RelayCommand]
    public void OpenFileLocation()
    {
        if (CurrentDocument is null || string.IsNullOrWhiteSpace(CurrentDocument.FilePath))
        {
            return;
        }

        try
        {
            if (System.IO.File.Exists(CurrentDocument.FilePath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{CurrentDocument.FilePath}\"",
                    UseShellExecute = true
                });
                return;
            }

            string? dir = System.IO.Path.GetDirectoryName(CurrentDocument.FilePath);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Ignore explorer launch failures
        }
    }

    /// <summary>
    /// Copies the full file path of the current document to the system clipboard.
    /// </summary>
    [RelayCommand]
    public void CopyFilePath()
    {
        if (CurrentDocument is null || string.IsNullOrWhiteSpace(CurrentDocument.FilePath))
        {
            return;
        }

        try
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(CurrentDocument.FilePath);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
        }
        catch
        {
            // Ignore clipboard errors
        }
    }

    /// <summary>
    /// Displays the document properties modal dialog.
    /// </summary>
    [RelayCommand]
    public async Task ShowPropertiesAsync()
    {
        if (CurrentDocument is not null)
        {
            await _dialogService.ShowDocumentPropertiesAsync(CurrentDocument);
        }
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

        await _dialogService.ShowMessageAsync(name, description, close);
    }
}
