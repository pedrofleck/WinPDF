using WinPDF.Models;

namespace WinPDF.Rendering;

/// <summary>
/// Pixel-space geometry helpers for full-page and tiled rasterization (AGENTS.md §5.4).
/// </summary>
public static class RenderGeometry
{
    /// <summary>
    /// Computes the pixel size of the whole page rasterized at <paramref name="zoom"/>:
    /// <c>ceil(W · k · Zq · R) × ceil(H · k · Zq · R)</c>, where W/H are the displayed (rotated) dimensions.
    /// </summary>
    /// <param name="page">Page metadata.</param>
    /// <param name="userRotationDegrees">Extra user rotation applied on top of the page's <c>/Rotate</c>.</param>
    /// <param name="zoom">Quantized zoom factor <c>Zq</c>.</param>
    /// <param name="rasterizationScale"><c>XamlRoot.RasterizationScale</c>.</param>
    /// <returns>The page width and height in physical pixels (each at least 1).</returns>
    public static (int Width, int Height) GetPagePixelSize(PdfPageInfo page, int userRotationDegrees, double zoom, double rasterizationScale)
    {
        ArgumentNullException.ThrowIfNull(page);

        bool swap = PageCoordinateTransform.NormalizeRotation(page.Rotation + userRotationDegrees) is 90 or 270;
        double widthPt = swap ? page.HeightPt : page.WidthPt;
        double heightPt = swap ? page.WidthPt : page.HeightPt;
        double factor = (96.0 / 72.0) * zoom * rasterizationScale;

        return (
            Math.Max(1, (int)Math.Round(widthPt * factor)),
            Math.Max(1, (int)Math.Round(heightPt * factor)));
    }

    /// <summary>
    /// Standard tile dimension in pixels (512×512).
    /// </summary>
    public const int TileSizePx = 512;

    /// <summary>
    /// Maximum dimension in pixels for a single full-page bitmap to prevent Direct3D 11 limits (16384px)
    /// and excessive VRAM consumption on large architectural sheets.
    /// </summary>
    public const int MaxFullPageDimensionPx = 4096;

    /// <summary>
    /// Computes the pixel size of the whole page rasterized at <paramref name="zoom"/>:
    /// <c>ceil(W · k · Zq · R) × ceil(H · k · Zq · R)</c>, where W/H are the displayed (rotated) dimensions,
    /// clamped to <see cref="MaxFullPageDimensionPx"/> while preserving aspect ratio.
    /// </summary>
    /// <param name="page">Page metadata.</param>
    /// <param name="zoom">Quantized zoom factor <c>Zq</c>.</param>
    /// <param name="rasterizationScale"><c>XamlRoot.RasterizationScale</c>.</param>
    /// <param name="userRotationDegrees">Extra user rotation applied on top of the page's <c>/Rotate</c>.</param>
    /// <returns>The page width and height in physical pixels (each at least 1, max dimension 4096).</returns>
    public static (int Width, int Height) ComputeFullPagePixelSize(PdfPageInfo page, double zoom, double rasterizationScale, int userRotationDegrees = 0)
    {
        (int rawWidth, int rawHeight) = GetPagePixelSize(page, userRotationDegrees, zoom, rasterizationScale);
        if (rawWidth <= MaxFullPageDimensionPx && rawHeight <= MaxFullPageDimensionPx)
        {
            return (rawWidth, rawHeight);
        }

        double aspect = (double)rawWidth / rawHeight;
        if (rawWidth > rawHeight)
        {
            int clampedWidth = MaxFullPageDimensionPx;
            int clampedHeight = Math.Max(1, (int)Math.Round(clampedWidth / aspect));
            return (clampedWidth, clampedHeight);
        }
        else
        {
            int clampedHeight = MaxFullPageDimensionPx;
            int clampedWidth = Math.Max(1, (int)Math.Round(clampedHeight * aspect));
            return (clampedWidth, clampedHeight);
        }
    }

    /// <summary>
    /// Gets the number of 512×512 tile columns and rows needed to cover a page rasterization.
    /// </summary>
    /// <param name="pagePixelWidth">Full page width in pixels.</param>
    /// <param name="pagePixelHeight">Full page height in pixels.</param>
    /// <returns>The tile column and row counts.</returns>
    public static (int Columns, int Rows) GetTileGrid(int pagePixelWidth, int pagePixelHeight)
        => (
            (pagePixelWidth + ZoomQuantizer.TileSize - 1) / ZoomQuantizer.TileSize,
            (pagePixelHeight + ZoomQuantizer.TileSize - 1) / ZoomQuantizer.TileSize);

    /// <summary>
    /// Gets the pixel rectangle of tile <c>(tileX, tileY)</c> within the page rasterization; edge tiles are clipped.
    /// </summary>
    /// <param name="pagePixelWidth">Full page width in pixels.</param>
    /// <param name="pagePixelHeight">Full page height in pixels.</param>
    /// <param name="tileX">Zero-based tile column.</param>
    /// <param name="tileY">Zero-based tile row.</param>
    /// <returns>The tile's left, top, width and height in pixels.</returns>
    public static (int Left, int Top, int Width, int Height) GetTileRect(int pagePixelWidth, int pagePixelHeight, int tileX, int tileY)
    {
        int left = tileX * ZoomQuantizer.TileSize;
        int top = tileY * ZoomQuantizer.TileSize;
        return (
            left,
            top,
            Math.Clamp(pagePixelWidth - left, 1, ZoomQuantizer.TileSize),
            Math.Clamp(pagePixelHeight - top, 1, ZoomQuantizer.TileSize));
    }
}
