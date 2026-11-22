using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinPDF.Helpers;
using WinPDF.Models;
using WinPDF.ViewModels;

namespace WinPDF.Views;

/// <summary>
/// Native modal dialog presenting document properties in a single column with window dimming,
/// Acrylic upper body, embedded digital signature cards, distinct footer tone, and responsive centering across any window size.
/// </summary>
public sealed partial class DocumentPropertiesDialog : ContentDialog
{
    private readonly bool _focusSignatures;
    private XamlRoot? _subscribedXamlRoot;

    /// <summary>
    /// Gets the document view model.
    /// </summary>
    public DocumentViewModel Document { get; }

    /// <summary>
    /// Gets the localized dialog title.
    /// </summary>
    public string DialogTitle => ResourceStrings.Get(ResourceKeys.DocPropsTitle);

    /// <summary>
    /// Gets the localized digital signatures section heading.
    /// </summary>
    public string SignaturesSectionTitle => ResourceStrings.Get(ResourceKeys.SigDialogTitle);

    /// <summary>
    /// Gets the localized close button label.
    /// </summary>
    public string CloseButtonLabel => ResourceStrings.Get(ResourceKeys.DialogClose);

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentPropertiesDialog"/> class.
    /// </summary>
    /// <param name="document">The document view model.</param>
    /// <param name="focusSignatures">Whether to scroll down to the digital signatures section after loading.</param>
    public DocumentPropertiesDialog(DocumentViewModel document, bool focusSignatures = false)
    {
        Document = document;
        _focusSignatures = focusSignatures;
        InitializeComponent();
        PopulateSignatureCards();
        Loaded += OnDialogLoaded;
        Unloaded += OnDialogUnloaded;
        AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(OnDialogKeyDown), true);
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ConfigureTemplateParts();
    }

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        ConfigureTemplateParts();
        SyncDialogHostSizeAndCenter();

        if (XamlRoot is not null && _subscribedXamlRoot != XamlRoot)
        {
            if (_subscribedXamlRoot is not null)
            {
                _subscribedXamlRoot.Changed -= OnXamlRootChanged;
            }

            _subscribedXamlRoot = XamlRoot;
            _subscribedXamlRoot.Changed += OnXamlRootChanged;
        }

        if (_focusSignatures && Document.HasSignatures && PropertiesScrollViewer is not null && SignaturesSection is not null)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    Windows.Foundation.Point pt = SignaturesSection.TransformToVisual(PropertiesScrollViewer.Content as UIElement).TransformPoint(new Windows.Foundation.Point(0, 0));
                    PropertiesScrollViewer.ChangeView(null, Math.Max(0.0, pt.Y - 8.0), null, disableAnimation: false);
                }
                catch
                {
                }
            });
        }

        CloseButton.Focus(FocusState.Programmatic);
    }

    private void PopulateSignatureCards()
    {
        if (SignatureCardsHost is null)
        {
            return;
        }

        SignatureCardsHost.Children.Clear();
        for (int i = 0; i < Document.Signatures.Count; i++)
        {
            SignatureCardsHost.Children.Add(BuildSignatureCard(Document.Signatures[i].Info));
        }
    }

    private static Border BuildSignatureCard(PdfSignatureInfo sig)
    {
        StackPanel root = new() { Spacing = 8 };

        string statusLabel = SignatureItemViewModel.ResolveStatusText(sig.Status);
        (string glyph, Windows.UI.Color badgeColor) = sig.Status switch
        {
            PdfSignatureVerificationStatus.Valid or PdfSignatureVerificationStatus.ValidUntrustedRoot => ("\uE73E", Windows.UI.Color.FromArgb(255, 16, 124, 16)),
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

    private void OnDialogUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedXamlRoot is not null)
        {
            _subscribedXamlRoot.Changed -= OnXamlRootChanged;
            _subscribedXamlRoot = null;
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        SyncDialogHostSizeAndCenter();
    }

    private void ConfigureTemplateParts()
    {
        if (GetTemplateChild("Title") is UIElement titleElement)
        {
            titleElement.Visibility = Visibility.Collapsed;
        }

        if (GetTemplateChild("CommandSpace") is UIElement commandSpace)
        {
            commandSpace.Visibility = Visibility.Collapsed;
        }

        if (GetTemplateChild("ContentScrollViewer") is ScrollViewer outerScrollViewer)
        {
            outerScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
            outerScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            outerScrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
            outerScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            outerScrollViewer.Padding = new Thickness(0);
        }

        if (GetTemplateChild("BackgroundElement") is Border bgElement)
        {
            bgElement.HorizontalAlignment = HorizontalAlignment.Center;
            bgElement.VerticalAlignment = VerticalAlignment.Center;
            bgElement.Padding = new Thickness(0);
        }
    }

    private void SyncDialogHostSizeAndCenter()
    {
        if (XamlRoot is null)
        {
            return;
        }

        double rootW = XamlRoot.Size.Width;
        double rootH = XamlRoot.Size.Height;
        if (rootW <= 0 || rootH <= 0)
        {
            return;
        }

        if (GetTemplateChild("Container") is FrameworkElement container)
        {
            container.Width = rootW;
            container.Height = rootH;
        }

        if (GetTemplateChild("LayoutRoot") is FrameworkElement layoutRoot)
        {
            layoutRoot.Width = rootW;
            layoutRoot.Height = rootH;
            layoutRoot.HorizontalAlignment = HorizontalAlignment.Stretch;
            layoutRoot.VerticalAlignment = VerticalAlignment.Stretch;
        }

        if (GetTemplateChild("BackgroundElement") is FrameworkElement bgElement)
        {
            bgElement.HorizontalAlignment = HorizontalAlignment.Center;
            bgElement.VerticalAlignment = VerticalAlignment.Center;
        }
    }

    private void OnDialogKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Hide();
        }
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void OnOpenFileLocationClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Document.FilePath))
        {
            return;
        }

        try
        {
            if (System.IO.File.Exists(Document.FilePath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{Document.FilePath}\"",
                    UseShellExecute = true
                });
                return;
            }

            string? dir = System.IO.Path.GetDirectoryName(Document.FilePath);
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
        }
    }
}
