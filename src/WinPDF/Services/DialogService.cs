using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinPDF.Helpers;
using WinPDF.Models;
using WinPDF.ViewModels;

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

    /// <inheritdoc/>
    public Task ShowSignatureDetailsAsync(IReadOnlyList<PdfSignatureInfo> signatures, int initialIndex = 0, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource tcs = new();

        _windowContext.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_windowContext.XamlRoot is null || signatures.Count == 0)
                {
                    tcs.TrySetResult();
                    return;
                }

                StackPanel cardsStack = new() { Spacing = 12 };

                // Order with the focused signature first when clicked directly on a specific stamp
                List<PdfSignatureInfo> ordered = new(signatures.Count);
                if (initialIndex >= 0 && initialIndex < signatures.Count)
                {
                    ordered.Add(signatures[initialIndex]);
                }

                for (int i = 0; i < signatures.Count; i++)
                {
                    if (i != initialIndex)
                    {
                        ordered.Add(signatures[i]);
                    }
                }

                foreach (PdfSignatureInfo sig in ordered)
                {
                    cardsStack.Children.Add(BuildSignatureCard(sig));
                }

                ScrollViewer scroller = new()
                {
                    Content = cardsStack,
                    MaxHeight = 460,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                };

                ContentDialog dialog = new()
                {
                    Title = ResourceStrings.Get(ResourceKeys.SigDialogTitle),
                    Content = scroller,
                    CloseButtonText = ResourceStrings.Get(ResourceKeys.DialogClose),
                    XamlRoot = _windowContext.XamlRoot,
                    DefaultButton = ContentDialogButton.Close,
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

    private static Border BuildSignatureCard(PdfSignatureInfo sig)
    {
        StackPanel root = new() { Spacing = 8 };

        string statusLabel = SignatureItemViewModel.ResolveStatusText(sig.Status);
        (string glyph, Windows.UI.Color badgeColor) = sig.Status switch
        {
            PdfSignatureVerificationStatus.Valid => ("\uE73E", Windows.UI.Color.FromArgb(255, 16, 124, 16)),
            PdfSignatureVerificationStatus.ValidUntrustedRoot => ("\uE946", Windows.UI.Color.FromArgb(255, 202, 80, 16)),
            PdfSignatureVerificationStatus.ModifiedAfterSigning => ("\uE7BA", Windows.UI.Color.FromArgb(255, 202, 80, 16)),
            PdfSignatureVerificationStatus.Invalid => ("\uEA39", Windows.UI.Color.FromArgb(255, 197, 15, 31)),
            _ => ("\uE9CE", Windows.UI.Color.FromArgb(255, 118, 118, 118)),
        };

        StackPanel headerRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerRow.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            Foreground = new SolidColorBrush(badgeColor),
            VerticalAlignment = VerticalAlignment.Center,
        });
        headerRow.Children.Add(new TextBlock
        {
            Text = statusLabel,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        root.Children.Add(headerRow);

        if (!string.IsNullOrWhiteSpace(sig.StatusDetails))
        {
            root.Children.Add(new TextBlock
            {
                Text = sig.StatusDetails,
                FontSize = 12,
                Opacity = 0.78,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        AddSignatureField(root, ResourceKeys.SigDialogSigner, !string.IsNullOrWhiteSpace(sig.SignerName) ? sig.SignerName : sig.SignerDistinguishedName);
        if (!string.IsNullOrWhiteSpace(sig.IssuerName))
        {
            AddSignatureField(root, ResourceKeys.SigDialogIssuer, sig.IssuerName);
        }

        if (sig.SigningTime is { } st)
        {
            AddSignatureField(root, ResourceKeys.SigDialogTime, st.ToLocalTime().ToString("f"));
        }

        if (!string.IsNullOrWhiteSpace(sig.Reason))
        {
            AddSignatureField(root, ResourceKeys.SigDialogReason, sig.Reason);
        }

        if (!string.IsNullOrWhiteSpace(sig.SubFilter))
        {
            AddSignatureField(root, ResourceKeys.SigDialogFormat, sig.SubFilter);
        }

        string coverageText = sig.CoversWholeDocument
            ? ResourceStrings.Get(ResourceKeys.SigDialogCoverageWhole)
            : ResourceStrings.Get(ResourceKeys.SigDialogCoverageModified);
        AddSignatureField(root, ResourceKeys.SigDialogCoverage, coverageText);

        string mdpText = sig.DocMdpPermission switch
        {
            1 => ResourceStrings.Get(ResourceKeys.SigDocMdpNoChanges),
            2 => ResourceStrings.Get(ResourceKeys.SigDocMdpFormFillAndSign),
            3 => ResourceStrings.Get(ResourceKeys.SigDocMdpAnnotationsAllowed),
            _ => ResourceStrings.Get(ResourceKeys.SigDocMdpUnspecified),
        };
        AddSignatureField(root, ResourceKeys.SigDialogPermission, mdpText);

        if (sig.CertNotBefore.HasValue && sig.CertNotAfter.HasValue)
        {
            string validity = $"{sig.CertNotBefore.Value.ToLocalTime():d} – {sig.CertNotAfter.Value.ToLocalTime():d}";
            AddSignatureField(root, ResourceKeys.SigDialogCertValidity, validity);
        }

        if (!string.IsNullOrWhiteSpace(sig.CertThumbprint))
        {
            AddSignatureField(root, ResourceKeys.SigDialogThumbprint, sig.CertThumbprint, monospace: true);
        }

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Child = root,
        };
    }

    private static void AddSignatureField(StackPanel parent, string labelKey, string value, bool monospace = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        StackPanel row = new() { Spacing = 2 };
        row.Children.Add(new TextBlock
        {
            Text = ResourceStrings.Get(labelKey),
            FontSize = 11,
            Opacity = 0.65,
        });
        TextBlock valBlock = new()
        {
            Text = value,
            FontSize = 12.5,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        if (monospace)
        {
            valBlock.FontFamily = new FontFamily("Consolas");
            valBlock.FontSize = 11.5;
        }

        row.Children.Add(valBlock);
        parent.Children.Add(row);
    }
}
