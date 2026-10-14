using Microsoft.Graphics.Canvas;

namespace WinPDF.Rendering;

/// <summary>
/// <see cref="TileCacheEntry"/> wrapping a Win2D <see cref="CanvasBitmap"/> (GPU-resident Direct2D bitmap).
/// Device-bound, so it is automatically evicted on <see cref="TileCache.HandleDeviceLost"/>.
/// </summary>
public sealed class CanvasBitmapEntry : TileCacheEntry
{
    /// <summary>
    /// Initializes a new entry that takes ownership of <paramref name="bitmap"/>.
    /// </summary>
    /// <param name="bitmap">The Win2D bitmap.</param>
    public CanvasBitmapEntry(CanvasBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        Bitmap = bitmap;
    }

    /// <summary>
    /// Gets the wrapped GPU bitmap. Valid while the caller holds a reference to this entry.
    /// </summary>
    public CanvasBitmap Bitmap { get; }

    /// <summary>
    /// Gets a value indicating whether the underlying bitmap has been disposed.
    /// </summary>
    public bool IsDisposed { get; private set; }

    /// <inheritdoc/>
    public override bool IsDeviceBound => true;

    /// <inheritdoc/>
    public override long SizeInBytes => (long)Bitmap.SizeInPixels.Width * Bitmap.SizeInPixels.Height * 4;

    /// <inheritdoc/>
    protected override void DisposeResource()
    {
        IsDisposed = true;
        Bitmap.Dispose();
    }
}
