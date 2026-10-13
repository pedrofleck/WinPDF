using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinPDF.Models;
using WinPDF.ViewModels;

namespace WinPDF;

/// <summary>
/// Main application window hosting the Mica backdrop, custom TitleBar,
/// Papers-inspired command bar, and navigation sidebar.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// Gets the shell view model for data binding.
    /// </summary>
    public ShellViewModel ViewModel { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        ViewModel = App.CurrentApp.Services.GetRequiredService<ShellViewModel>();

        // Custom TitleBar integration with Mica
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Bind items repeater source when document changes
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentDocument))
        {
            if (ViewModel.CurrentDocument is not null)
            {
                ContinuousPagesRepeater.ItemsSource = ViewModel.CurrentDocument.Thumbnails;
            }
            else
            {
                ContinuousPagesRepeater.ItemsSource = null;
            }
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsFullScreen))
        {
            UpdateFullScreenState(ViewModel.IsFullScreen);
        }
    }

    private void UpdateFullScreenState(bool isFullScreen)
    {
        AppWindow.SetPresenter(isFullScreen
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Default);
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

    private void OnContinuousModeClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetViewMode(ViewMode.Continuous);
    }

    private void OnPagedModeClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetViewMode(ViewMode.Paged);
    }

    private void OnSidebarSelectorBarChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem == ThumbnailsSelectorItem)
        {
            ViewModel.SetSidebarTab(SidebarTab.Thumbnails);
        }
        else if (sender.SelectedItem == OutlineSelectorItem)
        {
            ViewModel.SetSidebarTab(SidebarTab.Outline);
        }
    }

    private void OnThumbnailSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbnailsList.SelectedItem is ThumbnailItemViewModel thumbnail && ViewModel.CurrentDocument is not null)
        {
            ViewModel.CurrentDocument.GoToPage(thumbnail.PageIndex);
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
