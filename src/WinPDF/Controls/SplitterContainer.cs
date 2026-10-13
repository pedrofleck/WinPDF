using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace WinPDF.Controls;

/// <summary>
/// A wrapper container for the draggable splitter thumb displaying a horizontal resize cursor.
/// </summary>
public sealed class SplitterContainer : ContentControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SplitterContainer"/> class.
    /// </summary>
    public SplitterContainer()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
        VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch;
        HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch;
    }
}
