using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinPDF.ViewModels;

namespace WinPDF.Views;

/// <summary>
/// Floating card view (Windows Photos style) displaying real PDF file metadata, page dimensions, and folder location link.
/// </summary>
public sealed partial class DocumentPropertiesView : UserControl
{
    /// <summary>
    /// Gets the shell view model providing document properties and navigation actions.
    /// </summary>
    public ShellViewModel ViewModel { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentPropertiesView"/> class.
    /// </summary>
    public DocumentPropertiesView()
    {
        ViewModel = App.CurrentApp.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Bindings.Update();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.CurrentDocument) or nameof(ShellViewModel.IsPropertiesOpen))
        {
            Bindings.Update();
        }
    }
}
