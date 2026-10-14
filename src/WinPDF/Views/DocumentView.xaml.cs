using System.Globalization;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinPDF.Helpers;
using WinPDF.Models;
using WinPDF.ViewModels;

namespace WinPDF.Views;

/// <summary>
/// Page hosting the responsive document canvas, scroll viewer, flip view, and floating overlays.
/// </summary>
public sealed partial class DocumentView : Page
{
    /// <summary>
    /// Gets the shell view model.
    /// </summary>
    public ShellViewModel ViewModel { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentView"/> class.
    /// </summary>
    public DocumentView()
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

    private void OnZoomPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string tagStr &&
            double.TryParse(tagStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoomFactor))
        {
            ViewModel.SetZoom(zoomFactor);
        }
    }

    private void OnToolSelectClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetTool(AnnotationTool.Select);
    }

    private void OnToolPenClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetTool(AnnotationTool.Pen);
    }

    private void OnToolHighlighterClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetTool(AnnotationTool.Highlighter);
    }

    private void OnToolEraserClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetTool(AnnotationTool.Eraser);
    }

    private void OnToolTextNoteClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetTool(AnnotationTool.TextNote);
    }

    private void OnDocumentDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = ResourceStrings.Get(ResourceKeys.OpenFile_DragCaption);
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            e.Handled = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void OnDocumentDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var pdfFile = items.OfType<StorageFile>()
                .FirstOrDefault(f => string.Equals(f.FileType, ".pdf", StringComparison.OrdinalIgnoreCase));

            if (pdfFile is not null)
            {
                e.Handled = true;
                await ViewModel.OpenFilePathAsync(pdfFile.Path);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open dropped PDF file: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }
}
