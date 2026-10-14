using WinPDF.Services;

namespace WinPDF.Rendering;

/// <summary>
/// <see cref="TileCacheEntry"/> wrapping a PDFium-allocated <see cref="PdfBitmapLease"/> (CPU-resident pixels,
/// e.g. sidebar thumbnails). Not device-bound, so it survives <see cref="TileCache.HandleDeviceLost"/>.
/// </summary>
public sealed class PdfBitmapEntry : TileCacheEntry
{
    /// <summary>Initializes a new entry that takes ownership of <paramref name="bitmap"/>.</summary>
    /// <param name="bitmap">The bitmap lease; disposed when the last reference is released.</param>
    public PdfBitmapEntry(PdfBitmapLease bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        Bitmap = bitmap;
    }

    /// <summary>Gets the wrapped bitmap. Valid while the caller holds a reference to this entry.</summary>
    public PdfBitmapLease Bitmap { get; }

    /// <inheritdoc/>
    public override long SizeInBytes => Bitmap.SizeInBytes;

    /// <inheritdoc/>
    protected override void DisposeResource() => Bitmap.Dispose();
}
