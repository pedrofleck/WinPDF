namespace WinPDF.Rendering;

/// <summary>
/// Rasterization strategy chosen for a zoom level (AGENTS.md §4.2).
/// </summary>
public enum PageRenderMode
{
    /// <summary>
    /// Standard reading (zoom ≤ 1.5): the whole page is rasterized in a single PDFium call, never tiled.
    /// </summary>
    FullPage,

    /// <summary>
    /// High magnification (zoom &gt; 1.5): the page is split into 512×512 tiles and only tiles intersecting
    /// the viewport are rasterized.
    /// </summary>
    Tiled,
}

/// <summary>
/// A quantized zoom level together with the render mode it implies.
/// </summary>
/// <param name="Step">Exponent <c>n</c> such that <see cref="Factor"/> = (√2)<sup>n</sup>; used as an integer cache key.</param>
/// <param name="Factor">Quantized zoom factor <c>Zq</c>.</param>
/// <param name="Mode">Full-page or tiled rasterization.</param>
public readonly record struct ZoomLevel(int Step, double Factor, PageRenderMode Mode)
{
    /// <summary>
    /// Gets a value indicating whether this is the Level 0 base level (<c>Zq == 1.0</c>), whose bitmap is already
    /// final and must not be re-rendered after a gesture debounce.
    /// </summary>
    public bool IsBaseLevel => Step == 0;
}

/// <summary>
/// Quantizes continuous zoom factors to steps of √2 (…, 0.71, 1, 1.41, 2, 2.83, 4, …) to maximize cache reuse and
/// decides between full-page and tiled rasterization.
/// </summary>
public static class ZoomQuantizer
{
    /// <summary>Raw zoom factors at or below this value use full-page rendering; above it, tiles.</summary>
    public const double TilingThreshold = 1.5;

    /// <summary>Edge length in physical pixels of one tile.</summary>
    public const int TileSize = 512;

    /// <summary>Minimum supported zoom factor (matches <c>ScrollViewer.MinZoomFactor</c>).</summary>
    public const double MinZoom = 0.25;

    /// <summary>Maximum supported zoom factor (matches <c>ScrollViewer.MaxZoomFactor</c>).</summary>
    public const double MaxZoom = 8.0;

    /// <summary>Quantization step index of <see cref="MinZoom"/> (√2<sup>−4</sup> = 0.25).</summary>
    public const int MinStep = -4;

    /// <summary>Quantization step index of <see cref="MaxZoom"/> (√2<sup>6</sup> = 8).</summary>
    public const int MaxStep = 6;

    /// <summary>
    /// Converts a step index to its zoom factor (√2)<sup>step</sup>. Even steps are exact powers of two.
    /// </summary>
    /// <param name="step">Step index.</param>
    /// <returns>The zoom factor.</returns>
    public static double StepToFactor(int step) => Math.Pow(2.0, step * 0.5);

    /// <summary>
    /// Finds the quantization step nearest (in log space) to <paramref name="zoomFactor"/>, clamped to
    /// [<see cref="MinStep"/>, <see cref="MaxStep"/>]. Invalid input maps to step 0 (zoom 1.0).
    /// </summary>
    /// <param name="zoomFactor">Raw zoom factor.</param>
    /// <returns>The nearest step index.</returns>
    public static int ToStep(double zoomFactor)
    {
        if (!double.IsFinite(zoomFactor) || zoomFactor <= 0)
        {
            return 0;
        }

        int step = (int)Math.Round(2.0 * Math.Log2(zoomFactor), MidpointRounding.AwayFromZero);
        return Math.Clamp(step, MinStep, MaxStep);
    }

    /// <summary>
    /// Quantizes <paramref name="zoomFactor"/> to <c>Zq</c>.
    /// </summary>
    /// <param name="zoomFactor">Raw zoom factor.</param>
    /// <returns>The nearest quantized zoom factor.</returns>
    public static double Quantize(double zoomFactor) => StepToFactor(ToStep(zoomFactor));

    /// <summary>
    /// Determines the render mode for a raw zoom factor: <see cref="PageRenderMode.FullPage"/> when
    /// <paramref name="zoomFactor"/> ≤ <see cref="TilingThreshold"/>, otherwise <see cref="PageRenderMode.Tiled"/>.
    /// </summary>
    /// <param name="zoomFactor">Raw zoom factor.</param>
    /// <returns>The render mode.</returns>
    public static PageRenderMode GetRenderMode(double zoomFactor)
        => double.IsFinite(zoomFactor) && zoomFactor > TilingThreshold ? PageRenderMode.Tiled : PageRenderMode.FullPage;

    /// <summary>
    /// Resolves a raw zoom factor to its quantized level and render mode.
    /// </summary>
    /// <param name="zoomFactor">Raw <c>ScrollViewer.ZoomFactor</c>.</param>
    /// <returns>The quantized zoom level.</returns>
    /// <remarks>
    /// The mode is decided on the <em>raw</em> factor. When that selects tiling but the nearest step is still
    /// ≤ 1.5 (raw zoom in (1.5, 1.68)), the step is bumped to the next one (2.0) so tiles are never
    /// rasterized below the displayed resolution.
    /// </remarks>
    public static ZoomLevel Resolve(double zoomFactor)
    {
        PageRenderMode mode = GetRenderMode(zoomFactor);
        int step = ToStep(zoomFactor);

        if (mode == PageRenderMode.Tiled && StepToFactor(step) <= TilingThreshold)
        {
            step = Math.Min(step + 1, MaxStep);
        }

        return new ZoomLevel(step, StepToFactor(step), mode);
    }
}
