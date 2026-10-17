using Microsoft.UI.Xaml.Controls;

namespace WinPDF.Services;

/// <summary>
/// Implements <see cref="IDialogService"/> using WinUI 3 <see cref="ContentDialog"/> routed through the UI thread.
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly IWindowContext _windowContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="DialogService"/> class.
    /// </summary>
    /// <param name="windowContext">Window context providing access to the XamlRoot and DispatcherQueue.</param>
    public DialogService(IWindowContext windowContext)
    {
        _windowContext = windowContext;
    }

    /// <inheritdoc/>
    public Task ShowMessageAsync(string title, string content, string closeButtonText, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource tcs = new();

        _windowContext.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_windowContext.XamlRoot is null)
                {
                    tcs.TrySetResult();
                    return;
                }

                ContentDialog dialog = new()
                {
                    Title = title,
                    Content = content,
                    CloseButtonText = closeButtonText,
                    XamlRoot = _windowContext.XamlRoot,
                    DefaultButton = ContentDialogButton.Close
                };

                await dialog.ShowAsync();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    /// <inheritdoc/>
    public Task<bool> ShowConfirmationAsync(string title, string content, string primaryButtonText, string secondaryButtonText, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool> tcs = new();

        _windowContext.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_windowContext.XamlRoot is null)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                ContentDialog dialog = new()
                {
                    Title = title,
                    Content = content,
                    PrimaryButtonText = primaryButtonText,
                    CloseButtonText = secondaryButtonText,
                    XamlRoot = _windowContext.XamlRoot,
                    DefaultButton = ContentDialogButton.Primary
                };

                ContentDialogResult result = await dialog.ShowAsync();
                tcs.TrySetResult(result == ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    /// <inheritdoc/>
    public Task ShowDocumentPropertiesAsync(ViewModels.DocumentViewModel document, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource tcs = new();

        _windowContext.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_windowContext.XamlRoot is null)
                {
                    tcs.TrySetResult();
                    return;
                }

                Views.DocumentPropertiesDialog dialog = new(document)
                {
                    XamlRoot = _windowContext.XamlRoot
                };

                await dialog.ShowAsync();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }
}
