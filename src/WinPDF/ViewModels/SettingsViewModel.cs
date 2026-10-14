using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinPDF.Models;

namespace WinPDF.ViewModels;

/// <summary>
/// View model managing the full-page settings categories, preferences, and app information.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shellViewModel;

    /// <summary>
    /// Gets the shell view model for root window and document integration.
    /// </summary>
    public ShellViewModel ShellViewModel => _shellViewModel;

    /// <summary>
    /// Gets or sets the currently selected settings category.
    /// </summary>
    [ObservableProperty]
    public partial SettingsCategory SelectedCategory { get; set; } = SettingsCategory.Appearance;

    /// <summary>
    /// Gets or sets the application visual theme preference.
    /// </summary>
    public AppThemePreference AppTheme
    {
        get => _shellViewModel.CurrentTheme;
        set
        {
            if (_shellViewModel.CurrentTheme != value)
            {
                _shellViewModel.SetTheme(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThemeIndex));
            }
        }
    }

    /// <summary>
    /// Gets or sets the theme ComboBox selected index (0: System, 1: Light, 2: Dark).
    /// </summary>
    public int ThemeIndex
    {
        get => AppTheme switch
        {
            AppThemePreference.Light => 1,
            AppThemePreference.Dark => 2,
            _ => 0
        };
        set
        {
            AppTheme = value switch
            {
                1 => AppThemePreference.Light,
                2 => AppThemePreference.Dark,
                _ => AppThemePreference.System
            };
        }
    }

    /// <summary>
    /// Gets or sets the default document view mode upon opening files.
    /// </summary>
    public ViewMode DefaultViewMode
    {
        get => _defaultViewMode;
        set
        {
            if (_defaultViewMode != value)
            {
                _defaultViewMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ViewModeIndex));
            }
        }
    }
    private ViewMode _defaultViewMode = ViewMode.Continuous;

    /// <summary>
    /// Gets or sets the view mode ComboBox selected index (0: Continuous, 1: Paged).
    /// </summary>
    public int ViewModeIndex
    {
        get => DefaultViewMode switch
        {
            ViewMode.Paged => 1,
            _ => 0
        };
        set
        {
            DefaultViewMode = value switch
            {
                1 => ViewMode.Paged,
                _ => ViewMode.Continuous
            };
        }
    }

    /// <summary>
    /// Gets or sets the default initial zoom option index (0: Fit Width, 1: Fit Page, 2: 100%).
    /// </summary>
    [ObservableProperty]
    public partial int DefaultZoomIndex { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether touch drawing is active by default.
    /// </summary>
    public bool DefaultTouchDrawing
    {
        get => _shellViewModel.IsTouchDrawingEnabled;
        set
        {
            if (_shellViewModel.IsTouchDrawingEnabled != value)
            {
                _shellViewModel.IsTouchDrawingEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Gets the application display version string.
    /// </summary>
    public string AppVersion => "0.1.1";

    /// <summary>
    /// Gets the runtime processor architecture description.
    /// </summary>
    public string AppArchitecture => RuntimeInformation.ProcessArchitecture.ToString();

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsViewModel"/> class.
    /// </summary>
    /// <param name="shellViewModel">The parent shell view model.</param>
    public SettingsViewModel(ShellViewModel shellViewModel)
    {
        _shellViewModel = shellViewModel;
        _shellViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.CurrentTheme))
            {
                OnPropertyChanged(nameof(AppTheme));
                OnPropertyChanged(nameof(ThemeIndex));
            }
            else if (e.PropertyName == nameof(ShellViewModel.IsTouchDrawingEnabled))
            {
                OnPropertyChanged(nameof(DefaultTouchDrawing));
            }
        };
    }

    /// <summary>
    /// Selects the active settings category.
    /// </summary>
    /// <param name="category">Target settings category.</param>
    [RelayCommand]
    public void SelectCategory(SettingsCategory category)
    {
        SelectedCategory = category;
    }
}
