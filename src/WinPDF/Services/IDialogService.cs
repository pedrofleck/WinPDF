namespace WinPDF.Services;

/// <summary>
/// User choice returned by the unsaved-changes confirmation prompt.
/// </summary>
public enum UnsavedChangesChoice
{
    /// <summary>Save changes before proceeding.</summary>
    Save,

    /// <summary>Discard unsaved changes and proceed.</summary>
    DontSave,

    /// <summary>Abort closing or switching away from the document.</summary>
    Cancel,
}

/// <summary>
/// Service abstraction for presenting dialogs and confirmations to the user.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Displays an informational message or alert dialog.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="content">Dialog message body.</param>
    /// <param name="closeButtonText">Text for the acknowledgment button.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ShowMessageAsync(string title, string content, string closeButtonText, CancellationToken cancellationToken = default);

    /// <summary>
    /// Displays a confirmation dialog asking the user to make a choice.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="content">Dialog message body.</param>
    /// <param name="primaryButtonText">Text for the primary/confirm button.</param>
    /// <param name="secondaryButtonText">Text for the secondary/cancel button.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns><c>true</c> if the primary button was clicked; otherwise, <c>false</c>.</returns>
    Task<bool> ShowConfirmationAsync(string title, string content, string primaryButtonText, string secondaryButtonText, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prompts the user to save, discard, or cancel when closing or replacing a modified document.
    /// </summary>
    /// <param name="fileName">Display name of the modified document.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The chosen <see cref="UnsavedChangesChoice"/>.</returns>
    Task<UnsavedChangesChoice> ShowUnsavedChangesAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Displays the modal properties dialog for the specified document.
    /// </summary>
    /// <param name="document">The document view model whose properties should be displayed.</param>
    /// <param name="focusSignatures">Whether to scroll directly to the digital signatures section when opened.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ShowDocumentPropertiesAsync(
        ViewModels.DocumentViewModel document,
        bool focusSignatures = false,
        CancellationToken cancellationToken = default);
}
