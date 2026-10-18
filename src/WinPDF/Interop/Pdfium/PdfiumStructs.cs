using System.Runtime.InteropServices;

namespace WinPDF.Interop.Pdfium;

/// <summary>
/// Compile-time constants mirrored from the PDFium public headers (<c>fpdfview.h</c>, <c>fpdf_doc.h</c>, <c>fpdf_text.h</c>).
/// </summary>
internal static class PdfiumConstants
{
    /// <summary>Name of the native library resolved next to the executable.</summary>
    internal const string LibraryName = "pdfium";

    /// <summary><c>FPDFBitmap_BGRA</c>: 4 bytes per pixel, byte order B, G, R, A (straight alpha).</summary>
    internal const int BitmapBgra = 4;

    /// <summary><c>FPDF_ERR_SUCCESS</c>.</summary>
    internal const uint ErrSuccess = 0;

    /// <summary><c>FPDF_ERR_UNKNOWN</c>.</summary>
    internal const uint ErrUnknown = 1;

    /// <summary><c>FPDF_ERR_FILE</c>: file not found or could not be opened.</summary>
    internal const uint ErrFile = 2;

    /// <summary><c>FPDF_ERR_FORMAT</c>: file not in PDF format or corrupted.</summary>
    internal const uint ErrFormat = 3;

    /// <summary><c>FPDF_ERR_PASSWORD</c>: password required or incorrect password.</summary>
    internal const uint ErrPassword = 4;

    /// <summary><c>FPDF_ERR_SECURITY</c>: unsupported security scheme.</summary>
    internal const uint ErrSecurity = 5;

    /// <summary><c>FPDF_ERR_PAGE</c>: page not found or content error.</summary>
    internal const uint ErrPage = 6;

    /// <summary><c>PDFACTION_UNSUPPORTED</c>: unsupported action type.</summary>
    internal const uint ActionUnsupported = 0;

    /// <summary><c>PDFACTION_GOTO</c>: go to a destination within the current document.</summary>
    internal const uint ActionGoto = 1;

    /// <summary><c>PDFACTION_REMOTEGOTO</c>: go to a destination within another document.</summary>
    internal const uint ActionRemoteGoto = 2;

    /// <summary><c>PDFACTION_URI</c>: universal resource identifier, including web pages and other internet resources.</summary>
    internal const uint ActionUri = 3;

    /// <summary><c>PDFACTION_LAUNCH</c>: launch an application or open a file.</summary>
    internal const uint ActionLaunch = 4;

    /// <summary><c>FPDF_MATCHCASE</c>: if not set, it will not match case by default.</summary>
    internal const uint MatchCase = 0x00000001;

    /// <summary><c>FPDF_MATCHWHOLEWORD</c>: if not set, it will not match the whole word by default.</summary>
    internal const uint MatchWholeWord = 0x00000002;

    /// <summary><c>FPDF_CONSECUTIVE</c>: if not set, it will skip past the current match to look for the next match.</summary>
    internal const uint MatchConsecutive = 0x00000004;

    /// <summary>Opaque white in the 0xAARRGGBB layout expected by <c>FPDFBitmap_FillRect</c>.</summary>
    internal const uint OpaqueWhite = 0xFFFFFFFF;

    /// <summary>Version of <see cref="FpdfLibraryConfig"/> that this binding fills in.</summary>
    internal const int LibraryConfigVersion = 2;
}

/// <summary>
/// Managed mirror of <c>FPDF_LIBRARY_CONFIG</c> (version 2 layout; later fields are never read by PDFium
/// when <see cref="Version"/> is 2).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FpdfLibraryConfig
{
    /// <summary>Interface version. Must be 2.</summary>
    public int Version;

    /// <summary>NULL-terminated array of font search paths, or NULL for the defaults.</summary>
    public byte** UserFontPaths;

    /// <summary>Optional <c>v8::Isolate*</c>; always NULL (V8/XFA is not used).</summary>
    public void* Isolate;

    /// <summary>Embedder data slot for the V8 isolate; unused.</summary>
    public uint V8EmbedderSlot;
}

/// <summary>
/// Managed mirror of <c>FPDF_FILEACCESS</c>. <c>unsigned long</c> is 32-bit on Windows, which caps
/// custom-stream documents at 4 GiB.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FpdfFileAccess
{
    /// <summary>File length in bytes.</summary>
    public uint FileLength;

    /// <summary>Block reader callback: <c>int (void* param, ulong position, byte* buffer, ulong size)</c>.</summary>
    public delegate* unmanaged[Cdecl]<void*, uint, byte*, uint, int> GetBlock;

    /// <summary>Opaque pointer handed back to <see cref="GetBlock"/> as its first argument.</summary>
    public void* Param;
}

/// <summary>Managed mirror of <c>FS_SIZEF</c> (size in points).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FsSizeF
{
    /// <summary>Width in points.</summary>
    public float Width;

    /// <summary>Height in points.</summary>
    public float Height;
}

/// <summary>Managed mirror of <c>FS_RECTF</c> (<c>fpdfview.h</c>: left, top, right, bottom in PDF user space points).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FsRectF
{
    /// <summary>The x-coordinate of the left-top corner.</summary>
    public float Left;

    /// <summary>The y-coordinate of the left-top corner.</summary>
    public float Top;

    /// <summary>The x-coordinate of the right-bottom corner.</summary>
    public float Right;

    /// <summary>The y-coordinate of the right-bottom corner.</summary>
    public float Bottom;
}

/// <summary>
/// Managed mirror of <c>FPDF_FILEWRITE</c> (<c>fpdf_save.h</c>) with an embedded user context pointer
/// passed back via the <c>pThis</c> first argument of <see cref="WriteBlock"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FpdfFileWrite
{
    /// <summary>Structure version. Must be 1.</summary>
    public int Version;

    /// <summary>Block writer callback: <c>int (FpdfFileWrite* pThis, void* pData, uint size)</c>.</summary>
    public delegate* unmanaged[Cdecl]<FpdfFileWrite*, void*, uint, int> WriteBlock;

    /// <summary>Opaque GCHandle pointer referencing the target <see cref="System.IO.Stream"/>.</summary>
    public nint UserData;
}
