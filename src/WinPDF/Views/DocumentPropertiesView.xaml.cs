using Microsoft.UI.Xaml.Controls;
using WinPDF.ViewModels;

namespace WinPDF.Views;

/// <summary>
/// Sidebar view displaying real PDF file metadata, page dimensions, and location actions.
/// </summary>
public sealed partial class DocumentPropertiesView : UserControl
{
    /// <summary>
    /// Gets the shell view model providing document properties and navigation actions.
    /// </summary>
    public ShellViewModel? ViewModel => DataContext as ShellViewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentPropertiesView"/> class.
    /// </summary>
    public DocumentPropertiesView()
    {
        InitializeComponent();
    }
}
