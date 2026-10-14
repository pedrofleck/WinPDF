using WinPDF.Interop.Pdfium;

namespace WinPDF.Services;

/// <summary>
/// Receives native bitmaps that are no longer used so they can be destroyed on the PDFium thread.
/// </summary>
internal interface IPdfiumBitmapOwner
{
    /// <summary>Destroys a bitmap, marshaling the call to the PDFium thread when it is still running.</summary>
    /// <param name="bitmap">The bitmap handle to release.</param>
    void ReleaseBitmap(PdfiumBitmapHandle bitmap);
}

/// <summary>
/// A rendered page (or tile) living in PDFium-allocated native memory: the zero-copy, zero-LOH hand-off between
/// the PDFium thread and consumers (thumbnail upload now, <c>CanvasBitmap</c> upload in Step 3B).
/// </summary>
/// <remarks>
/// The pixels are BGRA, 4 bytes per pixel, opaque (the bitmap is filled white before rendering). Consumers read
/// them straight from <see cref="Scan0"/> / <see cref="AsSpan"/>; no managed byte array is ever allocated.
/// <see cref="Dispose"/> returns the memory to PDFium (on its dedicated thread).
/// </remarks>
public sealed unsafe class PdfBitmapLease : IDisposable
{
    private readonly IPdfiumBitmapOwner _owner;
    private PdfiumBitmapHandle? _handle;

    internal PdfBitmapLease(PdfiumBitmapHandle handle, int width, int height, int stride, void* scan0, IPdfiumBitmapOwner owner)
    {
        _handle = handle;
        _owner = owner;
        Width = width;
        Height = height;
        Stride = stride;
        Scan0 = (nint)scan0;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the number of bytes per pixel row (may exceed <c>Width × 4</c>).</summary>
    public int Stride { get; }

    /// <summary>Gets the address of the first pixel row. Valid until <see cref="Dispose"/>.</summary>
    public nint Scan0 { get; }

    /// <summary>Gets the size of the pixel buffer in bytes.</summary>
    public long SizeInBytes => (long)Stride * Height;

    /// <summary>Gets a value indicating whether the lease has been disposed.</summary>
    public bool IsDisposed => _handle is null;

    /// <summary>
    /// Exposes the whole native pixel buffer as a read-only span (no copy).
    /// </summary>
    /// <returns>A span over <c>Stride × Height</c> bytes.</returns>
    /// <exception cref="ObjectDisposedException">The lease was disposed.</exception>
    public ReadOnlySpan<byte> AsSpan()
    {
        ObjectDisposedException.ThrowIf(_handle is null, this);
        return new ReadOnlySpan<byte>((void*)Scan0, checked((int)SizeInBytes));
    }

    /// <summary>
    /// Alias for <see cref="AsSpan"/> matching user guidelines.
    /// </summary>
    /// <returns>A span over <c>Stride × Height</c> bytes.</returns>
    public ReadOnlySpan<byte> AsReadOnlySpan() => AsSpan();

    /// <summary>
    /// Copies pixels to a managed byte array sized exactly for <c>Width * Height * 4</c>.
    /// </summary>
    /// <returns>The pixel byte array.</returns>
    public byte[] ToPixelArray()
    {
        ReadOnlySpan<byte> span = AsSpan();
        int expectedSize = Width * Height * 4;
        if (span.Length == expectedSize)
        {
            return span.ToArray();
        }

        byte[] result = GC.AllocateUninitializedArray<byte>(expectedSize);
        int srcStride = Stride;
        int dstStride = Width * 4;
        for (int y = 0; y < Height; y++)
        {
            span.Slice(y * srcStride, dstStride).CopyTo(result.AsSpan(y * dstStride, dstStride));
        }
        return result;
    }

    /// <summary>Returns the bitmap memory to PDFium.</summary>
    public void Dispose()
    {
        PdfiumBitmapHandle? handle = Interlocked.Exchange(ref _handle, null);
        if (handle is not null)
        {
            _owner.ReleaseBitmap(handle);
        }
    }
}
