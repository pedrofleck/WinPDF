using Microsoft.UI.Xaml;
using WinPDF.Models;

namespace WinPDF.Converters;

/// <summary>
/// Strongly-typed static helper methods for compile-time <c>x:Bind</c> expressions in XAML.
/// </summary>
public static class UiHelpers
{
    /// <summary>
    /// Returns <see cref="Visibility.Visible"/> if the condition is true; otherwise, <see cref="Visibility.Collapsed"/>.
    /// </summary>
    /// <param name="condition">The boolean condition.</param>
    /// <returns>Matching visibility state.</returns>
    public static Visibility VisibleIf(bool condition) =>
        condition ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Returns <see cref="Visibility.Collapsed"/> if the condition is true; otherwise, <see cref="Visibility.Visible"/>.
    /// </summary>
    /// <param name="condition">The boolean condition.</param>
    /// <returns>Inverted visibility state.</returns>
    public static Visibility CollapsedIf(bool condition) =>
        condition ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Checks if the active view mode is Continuous.
    /// </summary>
    public static bool IsContinuous(ViewMode mode) => mode == ViewMode.Continuous;

    /// <summary>
    /// Checks if the active view mode is Paged.
    /// </summary>
    public static bool IsPaged(ViewMode mode) => mode == ViewMode.Paged;

    /// <summary>
    /// Returns Visible if the active view mode is Continuous.
    /// </summary>
    public static Visibility ContinuousVisibility(ViewMode mode) =>
        mode == ViewMode.Continuous ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Returns Visible if the active view mode is Paged.
    /// </summary>
    public static Visibility PagedVisibility(ViewMode mode) =>
        mode == ViewMode.Paged ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Returns Visible if the active sidebar tab is Thumbnails.
    /// </summary>
    public static Visibility ThumbnailsVisibility(SidebarTab tab) =>
        tab == SidebarTab.Thumbnails ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Returns Visible if the active sidebar tab is Outline.
    /// </summary>
    public static Visibility OutlineVisibility(SidebarTab tab) =>
        tab == SidebarTab.Outline ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Checks if the active tool matches Select.
    /// </summary>
    public static bool IsSelectTool(AnnotationTool tool) => tool == AnnotationTool.Select;

    /// <summary>
    /// Checks if the active tool matches Pen.
    /// </summary>
    public static bool IsPenTool(AnnotationTool tool) => tool == AnnotationTool.Pen;

    /// <summary>
    /// Checks if the active tool matches Highlighter.
    /// </summary>
    public static bool IsHighlighterTool(AnnotationTool tool) => tool == AnnotationTool.Highlighter;

    /// <summary>
    /// Checks if the active tool matches Eraser.
    /// </summary>
    public static bool IsEraserTool(AnnotationTool tool) => tool == AnnotationTool.Eraser;

    /// <summary>
    /// Checks if the active tool matches TextNote.
    /// </summary>
    public static bool IsTextNoteTool(AnnotationTool tool) => tool == AnnotationTool.TextNote;

    /// <summary>
    /// Returns Visible if the count is zero.
    /// </summary>
    public static Visibility HasNoItems(int count) =>
        count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Returns Visible if the count is greater than zero.
    /// </summary>
    public static Visibility HasItems(int count) =>
        count > 0 ? Visibility.Visible : Visibility.Collapsed;
}
