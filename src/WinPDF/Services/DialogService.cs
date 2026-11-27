using Microsoft.UI.Xaml.Controls;
using WinPDF.Helpers;

namespace WinPDF.Services;

/// <summary>
/// Implements <see cref="IDialogService"/> using WinUI 3 <see cref="ContentDialog"/> routed through the UI thread.
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly IWindowContext _windowContext;
    private readonly ISignatureStoreService _signatureStoreService;
    private readonly IFilePickerService _filePickerService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DialogService"/> class.
    /// </summary>
    /// <param name="windowContext">Window context providing access to the XamlRoot and DispatcherQueue.</param>
    /// <param name="signatureStoreService">Local signature persistence service.</param>
    /// <param name="filePickerService">File picker service for signature image uploads.</param>
    public DialogService(
        IWindowContext windowContext,
        ISignatureStoreService signatureStoreService,
        IFilePickerService filePickerService)
    {
        _windowContext = windowContext;
        _signatureStoreService = signatureStoreService;
        _filePickerService = filePickerService;
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
    public Task<UnsavedChangesChoice> ShowUnsavedChangesAsync(string fileName, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<UnsavedChangesChoice> tcs = new();

        _windowContext.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_windowContext.XamlRoot is null)
                {
                    tcs.TrySetResult(UnsavedChangesChoice.Cancel);
                    return;
                }

                ContentDialog dialog = new()
                {
                    Title = ResourceStrings.Format(ResourceKeys.DialogUnsavedChangesTitle, fileName),
                    Content = ResourceStrings.Get(ResourceKeys.DialogUnsavedChangesContent),
                    PrimaryButtonText = ResourceStrings.Get(ResourceKeys.DialogUnsavedChangesSave),
                    SecondaryButtonText = ResourceStrings.Get(ResourceKeys.DialogUnsavedChangesDontSave),
                    CloseButtonText = ResourceStrings.Get(ResourceKeys.DialogCancel),
                    XamlRoot = _windowContext.XamlRoot,
                    DefaultButton = ContentDialogButton.Primary
                };

                ContentDialogResult result = await dialog.ShowAsync();
                tcs.TrySetResult(result switch
                {
                    ContentDialogResult.Primary => UnsavedChangesChoice.Save,
                    ContentDialogResult.Secondary => UnsavedChangesChoice.DontSave,
                    _ => UnsavedChangesChoice.Cancel,
                });
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    /// <inheritdoc/>
    public Task ShowDocumentPropertiesAsync(
        ViewModels.DocumentViewModel document,
        bool focusSignatures = false,
        CancellationToken cancellationToken = default)
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

                Views.DocumentPropertiesDialog dialog = new(document, focusSignatures)
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

    /// <inheritdoc/>
    public Task<Models.SavedSignatureItem?> ShowSignaturePickerAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<Models.SavedSignatureItem?> tcs = new();

        _windowContext.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_windowContext.XamlRoot is null)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                Views.SignaturePickerDialog dialog = new(_signatureStoreService, _filePickerService)
                {
                    XamlRoot = _windowContext.XamlRoot
                };

                await dialog.ShowAsync();
                tcs.TrySetResult(dialog.SelectedSignature);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }
}
