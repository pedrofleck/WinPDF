using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinPDF.Interop.Pdfium;

/// <summary>
/// Bridges a managed <see cref="FileStream"/> to PDFium's <c>FPDF_LoadCustomDocument</c> callback model.
/// </summary>
/// <remarks>
/// Lifetime contract (all three resources must outlive the open document):
/// <list type="bullet">
/// <item>the <see cref="FileStream"/>, opened with <c>FileShare.Read | FileShare.Delete</c> so the file can be
/// atomically replaced while open;</item>
/// <item>the <see cref="GCHandle"/> that keeps this object alive and whose address is passed to native code as
/// <c>m_Param</c>;</item>
/// <item>the native <c>FPDF_FILEACCESS</c> block (allocated off the managed heap; the callback is a static
/// <see cref="UnmanagedCallersOnlyAttribute"/> method, so there is no delegate that could be collected).</item>
/// </list>
/// <see cref="PdfiumDocumentHandle"/> disposes this object only after <c>FPDF_CloseDocument</c> returns.
/// Reads use <see cref="RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/> straight into PDFium's buffer:
/// no seeks, no intermediate managed arrays.
/// </remarks>
internal sealed unsafe class PdfiumStreamAccess : IDisposable
{
    private readonly FileStream _stream;
    private readonly SafeFileHandle _fileHandle;
    private GCHandle _selfHandle;
    private FpdfFileAccess* _native;

    private PdfiumStreamAccess(FileStream stream, uint length)
    {
        _stream = stream;
        _fileHandle = stream.SafeFileHandle;

        _native = (FpdfFileAccess*)NativeMemory.AllocZeroed((nuint)sizeof(FpdfFileAccess));
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);

        _native->FileLength = length;
        _native->GetBlock = &GetBlock;
        _native->Param = (void*)GCHandle.ToIntPtr(_selfHandle);
    }

    /// <summary>Gets the native <c>FPDF_FILEACCESS</c> block to pass to <c>FPDF_LoadCustomDocument</c>.</summary>
    internal FpdfFileAccess* Native => _native;

    /// <summary>
    /// Opens <paramref name="path"/> for shared, read-only, random access.
    /// </summary>
    /// <param name="path">Absolute path of the PDF file.</param>
    /// <returns>A new instance owning the file stream.</returns>
    /// <exception cref="IOException">The file cannot be opened or exceeds 4 GiB.</exception>
    internal static PdfiumStreamAccess Open(string path)
    {
        FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 1,
            FileOptions.RandomAccess);

        try
        {
            if (stream.Length > uint.MaxValue)
            {
                throw new IOException("PDF files larger than 4 GiB are not supported by the PDFium stream interface.");
            }

            return new PdfiumStreamAccess(stream, (uint)stream.Length);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_native is null)
        {
            return;
        }

        NativeMemory.Free(_native);
        _native = null;

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        _stream.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetBlock(void* param, uint position, byte* buffer, uint size)
    {
        // Exceptions must never cross the native boundary.
        try
        {
            if (size > int.MaxValue)
            {
                return 0;
            }

            PdfiumStreamAccess? self = GCHandle.FromIntPtr((nint)param).Target as PdfiumStreamAccess;
            if (self is null)
            {
                return 0;
            }

            Span<byte> destination = new(buffer, (int)size);
            long offset = position;
            while (!destination.IsEmpty)
            {
                int read = RandomAccess.Read(self._fileHandle, destination, offset);
                if (read <= 0)
                {
                    return 0;
                }

                destination = destination[read..];
                offset += read;
            }

            return 1;
        }
        catch
        {
            return 0;
        }
    }
}
