using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WinPDF.Models;
using WinPDF.Rendering;
using WinPDF.Services;
using WinPDF.ViewModels;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]

namespace WinPDF;

/// <summary>
/// Application entry point configuring dependency injection and initial window activation.
/// </summary>
public partial class App : Application
{
    private static readonly object s_logLock = new();
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

    [LibraryImport("shell32.dll", EntryPoint = "SetCurrentProcessExplicitAppUserModelID", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appID);

    /// <summary>
    /// Initializes the singleton application object and configures dependency injection.
    /// </summary>
    public App()
    {
        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID("PedroFleck.WinPDF");
        }
        catch
        {
        }

        UnhandledException += OnAppUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;

        _services = ConfigureServices();

        InitializeComponent();

        string[] cmdArgs = Environment.GetCommandLineArgs();
        for (int i = 1; i < cmdArgs.Length; i++)
        {
            if (string.Equals(cmdArgs[i], "--autotest", StringComparison.OrdinalIgnoreCase))
            {
                WinPDF.Services.Diagnostics.AutoTestRunner.InitializeMonitoring();
                break;
            }
        }
    }

    private void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogException("XamlUnhandledException", e.Exception, e.Message);
        e.Handled = true;
    }

    private void OnCurrentDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        LogException("AppDomainUnhandledException", e.ExceptionObject as Exception);
    }

    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void LogException(string source, Exception? ex, string? message = null)
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string logDir = System.IO.Path.Combine(appData, "WinPDF", "logs");
            System.IO.Directory.CreateDirectory(logDir);
            string logPath = System.IO.Path.Combine(logDir, "crash.log");

            lock (s_logLock)
            {
                if (System.IO.File.Exists(logPath))
                {
                    System.IO.FileInfo fileInfo = new(logPath);
                    if (fileInfo.Length > 2 * 1024 * 1024)
                    {
                        string oldLog = System.IO.Path.Combine(logDir, "crash.old.log");
                        System.IO.File.Move(logPath, oldLog, overwrite: true);
                    }
                }

                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {message}\n{ex}\n{new string('-', 60)}\n";
                System.IO.File.AppendAllText(logPath, logEntry);
            }
        }
        catch
        {
        }
    }

    private static IServiceProvider ConfigureServices()
    {
        ServiceCollection services = new();

        // UI, Configuration & Window Context Services
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IWindowContext, WindowContext>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<IDialogService, DialogService>();

        // Core PDF Engine & Rendering Caches
        services.AddSingleton<IPdfService, PdfiumService>();
        services.AddSingleton<TileCache>();

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
        if (_mainWindow is not null)
        {
            _mainWindow.Activate();
            return;
        }

        string[] cmdArgs = Environment.GetCommandLineArgs();

        // Check for automated diagnostic test mode
        bool isAutoTest = false;
        string? autoTestPdf = null;
        for (int i = 1; i < cmdArgs.Length; i++)
        {
            if (string.Equals(cmdArgs[i], "--autotest", StringComparison.OrdinalIgnoreCase))
            {
                isAutoTest = true;
                if (i + 1 < cmdArgs.Length && !cmdArgs[i + 1].StartsWith('-'))
                {
                    autoTestPdf = cmdArgs[i + 1].Trim('\"', ' ');
                }
                break;
            }
        }

        // Check command-line activation for file paths before creating main window
        string? startupPdf = null;
        if (!string.IsNullOrWhiteSpace(args.Arguments))
        {
            try
            {
                string rawArg = args.Arguments.Trim('\"', ' ');
                string fullPath = System.IO.Path.GetFullPath(rawArg);
                if (System.IO.File.Exists(fullPath) &&
                    string.Equals(System.IO.Path.GetExtension(fullPath), ".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    startupPdf = fullPath;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse launch arguments: {ex.Message}");
            }
        }
        else if (cmdArgs.Length > 1)
        {
            try
            {
                string candidate = cmdArgs[1].Trim('\"', ' ');
                if (!candidate.StartsWith('-') && System.IO.File.Exists(candidate) &&
                    string.Equals(System.IO.Path.GetExtension(candidate), ".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    startupPdf = System.IO.Path.GetFullPath(candidate);
                }
            }
            catch
            {
            }
        }

        ShellViewModel shellViewModel = _services.GetRequiredService<ShellViewModel>();
        bool isSessionRestore = false;
        int initialPageIndex = 0;

        if (startupPdf is null && !isAutoTest)
        {
            ISettingsService settingsService = _services.GetRequiredService<ISettingsService>();
            if (settingsService.Settings.ReopenLastDocument &&
                !string.IsNullOrWhiteSpace(settingsService.Settings.LastOpenedFilePath))
            {
                string lastPath = settingsService.Settings.LastOpenedFilePath;
                if (System.IO.File.Exists(lastPath))
                {
                    startupPdf = lastPath;
                    isSessionRestore = true;
                    initialPageIndex = Math.Max(0, settingsService.Settings.LastOpenedPageIndex);
                }
                else
                {
                    string missingFileName = System.IO.Path.GetFileName(lastPath);
                    if (string.IsNullOrWhiteSpace(missingFileName))
                    {
                        missingFileName = lastPath;
                    }

                    settingsService.Update(s =>
                    {
                        s.LastOpenedFilePath = null;
                        s.LastOpenedPageIndex = 0;
                    });
                    shellViewModel.ShowMissingLastDocumentNotification(missingFileName);
                }
            }
        }

        if (startupPdf is not null)
        {
            shellViewModel.IsInitialDocumentOpening = true;
        }

        _mainWindow = new MainWindow();

        // Initialize window context for pickers and dialogs before starting any async file operations
        IWindowContext windowContext = _services.GetRequiredService<IWindowContext>();
        windowContext.Initialize(_mainWindow);

        if (isAutoTest)
        {
            _mainWindow.Activate();
            WinPDF.Services.Diagnostics.AutoTestRunner.Run(_mainWindow, autoTestPdf);
            return;
        }

        _mainWindow.Activate();

        if (startupPdf is not null)
        {
            string pathToOpen = startupPdf;
            bool restoreFlag = isSessionRestore;
            int pageToRestore = initialPageIndex;
            _mainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                _ = shellViewModel.OpenFilePathAsync(
                    pathToOpen,
                    default,
                    isStartupSessionRestore: restoreFlag,
                    initialPageIndex: pageToRestore);
            });
        }
    }
}
