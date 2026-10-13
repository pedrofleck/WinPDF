namespace WinPDF.Models;

/// <summary>
/// Active interaction tool on the document canvas.
/// </summary>
public enum AnnotationTool
{
    /// <summary>
    /// Pan, zoom and select annotations or text.
    /// </summary>
    Select,

    /// <summary>
    /// Freehand ink drawing with stroke smoothing.
    /// </summary>
    Pen,

    /// <summary>
    /// Semi-transparent highlight strokes or area selection.
    /// </summary>
    Highlighter,

    /// <summary>
    /// Erases vector annotation strokes under the pointer.
    /// </summary>
    Eraser,

    /// <summary>
    /// Adds a popup text note annotation.
    /// </summary>
    TextNote
}
