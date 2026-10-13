using Microsoft.UI.Xaml;
using WinPDF.Helpers;

namespace WinPDF;

/// <summary>
/// Main application window hosting the document shell.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        Title = ResourceStrings.Get(ResourceKeys.AppDisplayName);
    }
}
