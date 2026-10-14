using Windows.Storage;
using Windows.Storage.Pickers;
using WinPDF.Helpers;
using WinRT.Interop;

namespace WinPDF.Services;

/// <summary>
/// Implements <see cref="IFilePickerService"/> using Windows App SDK storage pickers initialized with the window handle.
/// </summary>
public sealed class FilePickerService : IFilePickerService
{
    private readonly IWindowContext _windowContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilePickerService"/> class.
    /// </summary>
    /// <param name="windowContext">Window context providing HWND initialization.</param>
    public FilePickerService(IWindowContext windowContext)
    {
        _windowContext = windowContext;
    }

    /// <inheritdoc/>
    public async Task<string?> PickOpenPdfAsync(CancellationToken cancellationToken = default)
    {
        FileOpenPicker picker = new()
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };

        InitializeWithWindow.Initialize(picker, _windowContext.WindowHandle);
        picker.FileTypeFilter.Add(".pdf");

        StorageFile? file = await picker.PickSingleFileAsync().AsTask(cancellationToken);
        return file?.Path;
    }

    /// <inheritdoc/>
    public async Task<string?> PickSavePdfAsync(string suggestedFileName, CancellationToken cancellationToken = default)
    {
        FileSavePicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = string.IsNullOrWhiteSpace(suggestedFileName) ? "document.pdf" : suggestedFileName
        };

        InitializeWithWindow.Initialize(picker, _windowContext.WindowHandle);
        string fileTypeDesc = ResourceStrings.Get(ResourceKeys.FilePickerPdfFileType);
        picker.FileTypeChoices.Add(fileTypeDesc, new List<string> { ".pdf" });

        StorageFile? file = await picker.PickSaveFileAsync().AsTask(cancellationToken);
        return file?.Path;
    }
}
