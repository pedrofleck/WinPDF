using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinPDF.Models;
using WinPDF.Services;

namespace WinPDF.ViewModels;

/// <summary>
/// View model managing the full-page settings categories, persisted preferences, and app information.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shellViewModel;
    private readonly ISettingsService _settingsService;
    private readonly bool _isInitializing;

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
                if (!_isInitializing)
                {
                    _settingsService.Update(s => s.DefaultViewMode = value);
                    if (!_shellViewModel.IsPresentationMode)
                    {
                        _shellViewModel.SetViewMode(value);
                    }
                }

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

    partial void OnDefaultZoomIndexChanged(int value)
    {
        if (_isInitializing)
        {
            return;
        }

        int clamped = Math.Clamp(value, 0, 2);
        _settingsService.Update(s => s.DefaultZoomIndex = clamped);
        _shellViewModel.NotifyDefaultZoomPreferenceChanged(clamped);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the last opened PDF document should be reopened on startup.
    /// </summary>
    [ObservableProperty]
    public partial bool ReopenLastDocument { get; set; }

    partial void OnReopenLastDocumentChanged(bool value)
    {
        if (_isInitializing)
        {
            return;
        }

        _settingsService.Update(s =>
        {
            s.ReopenLastDocument = value;
            if (value && _shellViewModel.CurrentDocument is { } doc && !string.IsNullOrWhiteSpace(doc.FilePath))
            {
                s.LastOpenedFilePath = doc.FilePath;
                s.LastOpenedPageIndex = doc.CurrentPageIndex;
            }
        });
    }

    /// <summary>
    /// Gets or sets a value indicating whether the last viewed page of each document should be remembered and restored.
    /// </summary>
    [ObservableProperty]
    public partial bool RememberDocumentPage { get; set; }

    partial void OnRememberDocumentPageChanged(bool value)
    {
        if (_isInitializing)
        {
            return;
        }

        _settingsService.Update(s => s.RememberDocumentPage = value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether each document's custom view settings (zoom level and continuous/paged mode) should be remembered and restored.
    /// </summary>
    [ObservableProperty]
    public partial bool SaveDocumentViewSettings { get; set; }

    partial void OnSaveDocumentViewSettingsChanged(bool value)
    {
        if (_isInitializing)
        {
            return;
        }

        _settingsService.Update(s => s.SaveDocumentViewSettings = value);
    }

    private readonly string _initialLanguage;

    /// <summary>
    /// Gets or sets the application language override code (e.g. "", "en-US", "pt-BR").
    /// </summary>
    [ObservableProperty]
    public partial string SelectedLanguage { get; set; } = string.Empty;

    partial void OnSelectedLanguageChanged(string value)
    {
        if (_isInitializing)
        {
            return;
        }

        _settingsService.Update(s => s.AppLanguage = value);
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = value;
        }
        catch
        {
        }

        OnPropertyChanged(nameof(LanguageIndex));
        OnPropertyChanged(nameof(IsLanguageRestartNoticeVisible));
    }

    /// <summary>
    /// Gets or sets the language ComboBox selected index.
    /// 0 = System Default, 2 = English, 3 = Português (Brasil). Index 1 is the non-selectable separator.
    /// </summary>
    public int LanguageIndex
    {
        get => SelectedLanguage switch
        {
            "en-US" => 2,
            "pt-BR" => 3,
            _ => 0
        };
        set
        {
            SelectedLanguage = value switch
            {
                2 => "en-US",
                3 => "pt-BR",
                _ => string.Empty
            };
        }
    }

    /// <summary>
    /// Gets a value indicating whether the language was changed from the launch language, requiring an app restart.
    /// </summary>
    public bool IsLanguageRestartNoticeVisible =>
        !string.Equals(_initialLanguage, SelectedLanguage, StringComparison.OrdinalIgnoreCase);

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
    public string AppVersion => typeof(SettingsViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion
        .Split('+')[0]
        ?? typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>
    /// Gets the runtime processor architecture description.
    /// </summary>
    public string AppArchitecture => RuntimeInformation.ProcessArchitecture.ToString();

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsViewModel"/> class.
    /// </summary>
    /// <param name="shellViewModel">The parent shell view model.</param>
    /// <param name="settingsService">The disk settings persistence service.</param>
    public SettingsViewModel(ShellViewModel shellViewModel, ISettingsService settingsService)
    {
        _shellViewModel = shellViewModel;
        _settingsService = settingsService;

        _isInitializing = true;
        _defaultViewMode = _settingsService.Settings.DefaultViewMode;
        DefaultZoomIndex = Math.Clamp(_settingsService.Settings.DefaultZoomIndex, 0, 2);
        ReopenLastDocument = _settingsService.Settings.ReopenLastDocument;
        RememberDocumentPage = _settingsService.Settings.RememberDocumentPage;
        SaveDocumentViewSettings = _settingsService.Settings.SaveDocumentViewSettings;
        SelectedLanguage = _settingsService.Settings.AppLanguage ?? string.Empty;
        _initialLanguage = SelectedLanguage;
        _isInitializing = false;

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
