using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPDF.ViewModels;

namespace WinPDF.Views.Sidebar;

/// <summary>
/// Sidebar tab page presenting document outline and table of contents.
/// </summary>
public sealed partial class OutlineTabPage : Page
{
    /// <summary>
    /// Gets the shell view model.
    /// </summary>
    public ShellViewModel ViewModel { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="OutlineTabPage"/> class.
    /// </summary>
    public OutlineTabPage()
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

    private void OnOutlineItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is OutlineItemViewModel outlineItem && ViewModel.CurrentDocument is not null)
        {
            ViewModel.CurrentDocument.GoToPage(outlineItem.PageIndex);
        }
    }
}
