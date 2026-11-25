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
    TextNote,

    /// <summary>
    /// Positions a selected image stamp annotation onto the page.
    /// </summary>
    InsertImage,

    /// <summary>
    /// Places an inline formatted text box annotation onto the page.
    /// </summary>
    InsertText,

    /// <summary>
    /// Draws geometric shape annotations (Line, Arrow, Rectangle, Ellipse).
    /// </summary>
    Shape
}

/// <summary>
/// Supported geometric shape variants for <see cref="AnnotationTool.Shape"/>.
/// </summary>
public enum PdfAnnotationShapeKind
{
    /// <summary>Straight line segment (<c>/Line</c>).</summary>
    Line = 0,

    /// <summary>Line segment with an arrowhead at the end point (<c>/Line</c>).</summary>
    Arrow = 1,

    /// <summary>Axis-aligned rectangle or square (<c>/Square</c>).</summary>
    Rectangle = 2,

    /// <summary>Axis-aligned ellipse or circle (<c>/Circle</c>).</summary>
    Ellipse = 3,
}
