using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPDF.ViewModels;

namespace WinPDF.Views.Sidebar;

/// <summary>
/// Sidebar tab page presenting comments and document annotations.
/// </summary>
public sealed partial class AnnotationsTabPage : Page
{
    /// <summary>
    /// Gets the shell view model.
    /// </summary>
    public ShellViewModel ViewModel { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AnnotationsTabPage"/> class.
    /// </summary>
    public AnnotationsTabPage()
    {
        ViewModel = App.CurrentApp.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ShellViewModel vm)
        {
            ViewModel = vm;
        }
    }

    private void OnSignatureItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SignatureItemViewModel item)
        {
            ViewModel.CurrentDocument?.RequestInspectSignature(item.Info);
        }
    }

    private void OnAnnotationItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AnnotationItemViewModel item)
        {
            ViewModel.CurrentDocument?.RequestFocusAnnotation(item.Info);
        }
    }

    private void OnDeleteAnnotationClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AnnotationItemViewModel item } &&
            ViewModel.CurrentDocument is { } doc)
        {
            _ = doc.DeleteAnnotationAsync(item.PageIndex, item.AnnotIndex);
        }
    }
}
