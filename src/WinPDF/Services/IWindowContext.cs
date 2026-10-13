using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WinPDF.Services;

/// <summary>
/// Provides window-level context and handles needed by UI-dependent services
/// such as file pickers and dialogs, keeping ViewModels completely decoupled from XAML windows.
/// </summary>
public interface IWindowContext
{
    /// <summary>
    /// Gets the native Win32 window handle (HWND) of the active application window.
    /// </summary>
    nint WindowHandle { get; }

    /// <summary>
    /// Gets the <see cref="XamlRoot"/> of the active window content for hosting dialogs and flyouts.
    /// </summary>
    XamlRoot? XamlRoot { get; }

    /// <summary>
    /// Gets the <see cref="DispatcherQueue"/> associated with the UI thread.
    /// </summary>
    DispatcherQueue DispatcherQueue { get; }

    /// <summary>
    /// Initializes this context with the active window instance.
    /// </summary>
    /// <param name="window">The active application window.</param>
    void Initialize(Window window);
}
