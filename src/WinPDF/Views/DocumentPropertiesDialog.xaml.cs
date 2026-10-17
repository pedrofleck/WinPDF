using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinPDF.Helpers;
using WinPDF.ViewModels;

namespace WinPDF.Views;

/// <summary>
/// Native modal dialog presenting document properties in a single column with window dimming,
/// Acrylic upper body, distinct footer tone, and responsive centering across any window size.
/// </summary>
public sealed partial class DocumentPropertiesDialog : ContentDialog
{
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
    /// Gets the localized close button label.
    /// </summary>
    public string CloseButtonLabel => ResourceStrings.Get(ResourceKeys.DialogClose);

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentPropertiesDialog"/> class.
    /// </summary>
    /// <param name="document">The document view model.</param>
    public DocumentPropertiesDialog(DocumentViewModel document)
    {
        Document = document;
        InitializeComponent();
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

        CloseButton.Focus(FocusState.Programmatic);
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
