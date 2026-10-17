using System.Buffers;
using WinPDF.Services;

namespace WinPDF.Rendering;

/// <summary>
/// <see cref="TileCacheEntry"/> wrapping a rented buffer from <see cref="ArrayPool{T}.Shared"/> (CPU-resident pixels,
/// e.g. sidebar thumbnails) for zero-LOH allocation. Not device-bound, so it survives <see cref="TileCache.HandleDeviceLost"/>.
/// </summary>
public sealed class PdfBitmapEntry : TileCacheEntry, IDisposable
{
    private int _disposed;

    /// <summary>
    /// Gets the rented pixel buffer array.
    /// </summary>
    public byte[] Pixels { get; }

    /// <summary>
    /// Gets the exact number of valid bytes in the pixel buffer (<c>stride * height</c>).
    /// </summary>
    public int ByteLength { get; }

    /// <summary>
    /// Gets the width in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the height in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the stride in bytes.
    /// </summary>
    public int Stride { get; }

    /// <summary>
    /// Gets the wrapped native bitmap lease, if any.
    /// </summary>
    public PdfBitmapLease? Bitmap { get; }

    /// <summary>
    /// Gets a value indicating whether this entry has been disposed.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc/>
    public override long SizeInBytes => ByteLength;

    /// <summary>
    /// Initializes a new entry from a rented buffer.
    /// </summary>
    /// <param name="pixels">The rented byte array.</param>
    /// <param name="byteLength">The exact byte length.</param>
    /// <param name="width">Pixel width.</param>
    /// <param name="height">Pixel height.</param>
    /// <param name="stride">Byte stride.</param>
    /// <param name="bitmap">Optional native bitmap lease.</param>
    public PdfBitmapEntry(byte[] pixels, int byteLength, int width, int height, int stride = 0, PdfBitmapLease? bitmap = null)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        Pixels = pixels;
        ByteLength = byteLength;
        Width = width;
        Height = height;
        Stride = stride > 0 ? stride : width * 4;
        Bitmap = bitmap;
    }

    /// <summary>
    /// Initializes a new entry by copying pixels from a <see cref="PdfBitmapLease"/> into a rented buffer.
    /// </summary>
    /// <param name="bitmap">The source bitmap lease.</param>
    public PdfBitmapEntry(PdfBitmapLease bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        Width = bitmap.Width;
        Height = bitmap.Height;
        Stride = bitmap.Stride;
        ByteLength = checked((int)bitmap.SizeInBytes);
        Pixels = ArrayPool<byte>.Shared.Rent(ByteLength);

        try
        {
            unsafe
            {
                fixed (byte* pDst = Pixels)
                {
                    Buffer.MemoryCopy((void*)bitmap.Scan0, pDst, ByteLength, ByteLength);
                }
            }
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    /// <summary>
    /// Returns the rented buffer to <see cref="ArrayPool{T}.Shared"/> and disposes underlying native resources.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (Pixels is { Length: > 0 })
            {
                ArrayPool<byte>.Shared.Return(Pixels, clearArray: false);
            }

            Bitmap?.Dispose();
        }
    }

    /// <inheritdoc/>
    protected override void DisposeResource() => Dispose();
}
