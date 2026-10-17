using System.Runtime.InteropServices;
using WinPDF.Services;

namespace WinPDF.Interop.Printing;

/// <summary>
/// Win32 P/Invoke declarations and helper routines for the native Windows print dialog (<c>PrintDlgExW</c>)
/// and GDI spooler device context management (<c>gdi32.dll</c>).
/// </summary>
internal static partial class Win32PrintInterop
{
    internal const uint PD_ALLPAGES = 0x00000000;
    internal const uint PD_SELECTION = 0x00000001;
    internal const uint PD_PAGENUMS = 0x00000002;
    internal const uint PD_NOSELECTION = 0x00000004;
    internal const uint PD_RETURNDC = 0x00000100;
    internal const uint PD_USEDEVMODECOPIESANDCOLLATE = 0x00040000;
    internal const uint PD_CURRENTPAGE = 0x00400000;
    internal const uint START_PAGE_GENERAL = 0xFFFFFFFF;

    internal const uint PD_RESULT_CANCEL = 0;
    internal const uint PD_RESULT_PRINT = 1;
    internal const uint PD_RESULT_APPLY = 2;

    // GetDeviceCaps indices
    internal const int HORZRES = 8;
    internal const int VERTRES = 10;
    internal const int LOGPIXELSX = 88;
    internal const int LOGPIXELSY = 90;
    internal const int PHYSICALWIDTH = 110;
    internal const int PHYSICALHEIGHT = 111;
    internal const int PHYSICALOFFSETX = 112;
    internal const int PHYSICALOFFSETY = 113;

    private const int MaxPageRangesCount = 16;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PRINTPAGERANGE
    {
        public uint nFromPage;
        public uint nToPage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PRINTDLGEXW
    {
        public uint lStructSize;
        public nint hwndOwner;
        public nint hDevMode;
        public nint hDevNames;
        public nint hDC;
        public uint Flags;
        public uint Flags2;
        public uint ExclusionFlags;
        public uint nPageRanges;
        public uint nMaxPageRanges;
        public nint lpPageRanges;
        public uint nMinPage;
        public uint nMaxPage;
        public uint nCopies;
        public nint hInstance;
        public nint lpPrintTemplateName;
        public nint lpCallback;
        public uint nPropertyPages;
        public nint lphPropertyPages;
        public uint nStartPage;
        public uint dwResultAction;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DOCINFOW
    {
        public int cbSize;
        public nint lpszDocName;
        public nint lpszOutput;
        public nint lpszDatatype;
        public uint fwType;
    }

    [LibraryImport("comdlg32.dll", EntryPoint = "PrintDlgExW")]
    internal static partial int PrintDlgExW(ref PRINTDLGEXW lppd);

    [LibraryImport("gdi32.dll", EntryPoint = "StartDocW")]
    internal static partial int StartDocW(nint hdc, in DOCINFOW lpdi);

    [LibraryImport("gdi32.dll")]
    internal static partial int EndDoc(nint hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial int StartPage(nint hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial int EndPage(nint hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial int AbortDoc(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial int GetDeviceCaps(nint hdc, int index);

    [LibraryImport("gdi32.dll")]
    internal static partial int SaveDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RestoreDC(nint hdc, int nSavedDc);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GlobalFree(nint hMem);

    /// <summary>
    /// Displays the native Windows print dialog (<c>PrintDlgExW</c>) and returns the configured
    /// <see cref="PdfPrintJobOptions"/> if the user confirms printing, or <see langword="null"/> if cancelled.
    /// </summary>
    /// <param name="hwnd">Owner window handle (<c>HWND</c>).</param>
    /// <param name="documentTitle">Document title to display in the print queue.</param>
    /// <param name="currentPage">Current 1-based page number.</param>
    /// <param name="totalPages">Total number of pages in the document.</param>
    /// <param name="userRotationDegrees">Default user rotation in degrees applied to pages.</param>
    /// <param name="pageUserRotations">Optional per-page user rotations in degrees.</param>
    /// <returns>A <see cref="PdfPrintJobOptions"/> owning the printer <c>HDC</c>, or <see langword="null"/>.</returns>
    internal static PdfPrintJobOptions? ShowPrintDialog(
        nint hwnd,
        string documentTitle,
        int currentPage,
        int totalPages,
        int userRotationDegrees = 0,
        IReadOnlyDictionary<int, int>? pageUserRotations = null)
    {
        if (hwnd == nint.Zero || totalPages <= 0)
        {
            return null;
        }

        int rangeStructSize = Marshal.SizeOf<PRINTPAGERANGE>();
        nint rangesPtr = Marshal.AllocHGlobal(rangeStructSize * MaxPageRangesCount);

        PRINTDLGEXW pd = default;
        try
        {
            for (int i = 0; i < MaxPageRangesCount; i++)
            {
                PRINTPAGERANGE emptyRange = default;
                if (i == 0)
                {
                    emptyRange.nFromPage = 1;
                    emptyRange.nToPage = (uint)totalPages;
                }

                Marshal.StructureToPtr(emptyRange, rangesPtr + (i * rangeStructSize), fDeleteOld: false);
            }

            pd.lStructSize = (uint)Marshal.SizeOf<PRINTDLGEXW>();
            pd.hwndOwner = hwnd;
            pd.Flags = PD_RETURNDC | PD_USEDEVMODECOPIESANDCOLLATE | PD_NOSELECTION | PD_ALLPAGES;
            pd.nPageRanges = 1;
            pd.nMaxPageRanges = MaxPageRangesCount;
            pd.lpPageRanges = rangesPtr;
            pd.nMinPage = 1;
            pd.nMaxPage = (uint)totalPages;
            pd.nCopies = 1;
            pd.nStartPage = START_PAGE_GENERAL;

            int hr = PrintDlgExW(ref pd);
            if (hr != 0 || pd.dwResultAction != PD_RESULT_PRINT || pd.hDC == nint.Zero)
            {
                if (pd.hDC != nint.Zero)
                {
                    _ = DeleteDC(pd.hDC);
                    pd.hDC = nint.Zero;
                }

                return null;
            }

            List<int> pagesToPrint = new();
            if ((pd.Flags & PD_CURRENTPAGE) != 0)
            {
                int currentIdx = Math.Clamp(currentPage - 1, 0, totalPages - 1);
                pagesToPrint.Add(currentIdx);
            }
            else if ((pd.Flags & PD_PAGENUMS) != 0 && pd.nPageRanges > 0)
            {
                int count = (int)Math.Min(pd.nPageRanges, (uint)MaxPageRangesCount);
                for (int i = 0; i < count; i++)
                {
                    PRINTPAGERANGE range = Marshal.PtrToStructure<PRINTPAGERANGE>(rangesPtr + (i * rangeStructSize));
                    if (range.nFromPage == 0 && range.nToPage == 0)
                    {
                        continue;
                    }

                    int fromIdx = Math.Clamp((int)range.nFromPage - 1, 0, totalPages - 1);
                    int toIdx = Math.Clamp((int)range.nToPage - 1, 0, totalPages - 1);
                    if (fromIdx <= toIdx)
                    {
                        for (int p = fromIdx; p <= toIdx; p++)
                        {
                            pagesToPrint.Add(p);
                        }
                    }
                    else
                    {
                        for (int p = fromIdx; p >= toIdx; p--)
                        {
                            pagesToPrint.Add(p);
                        }
                    }
                }
            }

            if (pagesToPrint.Count == 0)
            {
                for (int p = 0; p < totalPages; p++)
                {
                    pagesToPrint.Add(p);
                }
            }

            int copies = Math.Max(1, (int)pd.nCopies);
            string title = string.IsNullOrWhiteSpace(documentTitle) ? "WinPDF Document" : documentTitle;

            return new PdfPrintJobOptions(
                pd.hDC,
                title,
                pagesToPrint,
                copies,
                pageUserRotations,
                userRotationDegrees);
        }
        catch
        {
            if (pd.hDC != nint.Zero)
            {
                _ = DeleteDC(pd.hDC);
                pd.hDC = nint.Zero;
            }

            throw;
        }
        finally
        {
            if (rangesPtr != nint.Zero)
            {
                Marshal.FreeHGlobal(rangesPtr);
            }

            if (pd.hDevMode != nint.Zero)
            {
                _ = GlobalFree(pd.hDevMode);
            }

            if (pd.hDevNames != nint.Zero)
            {
                _ = GlobalFree(pd.hDevNames);
            }
        }
    }
}
