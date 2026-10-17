using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinPDF.Helpers;
using WinPDF.ViewModels;

namespace WinPDF.Views;

/// <summary>
/// Native modal dialog presenting document properties in a single column with window dimming.
/// </summary>
public sealed partial class DocumentPropertiesDialog : ContentDialog
{
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
        Loaded += (_, _) => CloseButton.Focus(FocusState.Programmatic);
        AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(OnDialogKeyDown), true);
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
