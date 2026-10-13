namespace WinPDF.Services;

/// <summary>
/// Service abstraction for opening and saving PDF document files via system pickers.
/// </summary>
public interface IFilePickerService
{
    /// <summary>
    /// Prompts the user to select an existing PDF file to open.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The path to the selected file, or <c>null</c> if the user cancelled.</returns>
    Task<string?> PickOpenPdfAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Prompts the user to select a destination path for saving a copy of the PDF.
    /// </summary>
    /// <param name="suggestedFileName">Initial file name suggestion.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The path chosen by the user, or <c>null</c> if cancelled.</returns>
    Task<string?> PickSavePdfAsync(string suggestedFileName, CancellationToken cancellationToken = default);
}
