using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WinPDF.Services;
using WinPDF.ViewModels;

namespace WinPDF;

/// <summary>
/// Application entry point configuring dependency injection and initial window activation.
/// </summary>
public partial class App : Application
{
    private readonly IServiceProvider _services;
    private MainWindow? _mainWindow;

    /// <summary>
    /// Gets the current application instance strongly typed.
    /// </summary>
    public static App CurrentApp => (App)Current;

    /// <summary>
    /// Gets the application-wide dependency injection service provider.
    /// </summary>
    public IServiceProvider Services => _services;

    /// <summary>
    /// Initializes the singleton application object and configures dependency injection.
    /// </summary>
    public App()
    {
        InitializeComponent();
        _services = ConfigureServices();
    }

    private static IServiceProvider ConfigureServices()
    {
        ServiceCollection services = new();

        // UI & Window Context Services
        services.AddSingleton<IWindowContext, WindowContext>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<IDialogService, DialogService>();

        // View Models
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<SettingsViewModel>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Invoked when the application is launched normally or via command-line arguments.
    /// </summary>
    /// <param name="args">Details about the launch request and arguments.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();

        // Initialize window context for pickers and dialogs
        IWindowContext windowContext = _services.GetRequiredService<IWindowContext>();
        windowContext.Initialize(_mainWindow);

        // Check command-line activation for file paths
        if (!string.IsNullOrWhiteSpace(args.Arguments))
        {
            string rawArg = args.Arguments.Trim('\"', ' ');
            if (System.IO.File.Exists(rawArg))
            {
                ShellViewModel shellViewModel = _services.GetRequiredService<ShellViewModel>();
                _ = shellViewModel.OpenFilePathAsync(rawArg);
            }
        }

        _mainWindow.Activate();
    }
}
