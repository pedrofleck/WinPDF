using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace WinPDF.Services;

/// <summary>
/// Default implementation of <see cref="IWindowContext"/> capturing the active window.
/// </summary>
public sealed class WindowContext : IWindowContext
{
    private Window? _window;

    /// <inheritdoc/>
    public nint WindowHandle => _window is not null ? WindowNative.GetWindowHandle(_window) : nint.Zero;

    /// <inheritdoc/>
    public Microsoft.UI.WindowId WindowId => _window?.AppWindow?.Id
        ?? (_window is not null ? Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowHandle) : default);

    /// <inheritdoc/>
    public XamlRoot? XamlRoot => _window?.Content?.XamlRoot;

    /// <inheritdoc/>
    public DispatcherQueue DispatcherQueue => _window?.DispatcherQueue
        ?? DispatcherQueue.GetForCurrentThread();

    /// <inheritdoc/>
    public void Initialize(Window window)
    {
        _window = window;
    }
}
