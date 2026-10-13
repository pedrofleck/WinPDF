using Microsoft.UI.Xaml;

namespace WinPDF;

/// <summary>
/// Application entry point. Creates and activates the main window.
/// </summary>
/// <remarks>
/// Step 2 will extend this class with command-line / file-association handling
/// and dependency injection setup for services and view models.
/// </remarks>
public partial class App : Application
{
    private Window? _mainWindow;

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }
}
