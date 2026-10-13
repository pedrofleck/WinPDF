namespace WinPDF.Services;

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
}
