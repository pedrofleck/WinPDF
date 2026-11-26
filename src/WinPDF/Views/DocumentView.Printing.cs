using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Printing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using WinPDF.Helpers;
using WinPDF.Models;
using WinPDF.Rendering;
using WinPDF.Services;
using WinPDF.ViewModels;

namespace WinPDF.Views;

public sealed partial class DocumentView
{
    [ComImport]
    [Guid("905a0fef-bc53-11df-8c49-001e4fc686da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPrintBufferByteAccess
    {
        unsafe byte* Buffer { get; }
    }

    private PrintManager? _registeredPrintManager;
    private nint _registeredPrintHwnd;
    private PrintDocument? _printDocument;
    private IPrintDocumentSource? _printDocumentSource;
    private DocumentViewModel? _activePrintDocumentVm;
    private PrintTaskOptions? _currentPrintTaskOptions;

    // =========================================================================
    // Native Windows 11 Print Preview & High-DPI Printing (PrintManagerInterop)
    // =========================================================================

    private async Task OnPrintRequestedAsync()
    {
        if (ViewModel.CurrentDocument is not { PageCount: > 0 } doc)
        {
            return;
        }

        nint hwnd = _windowContext.WindowHandle;
        if (hwnd == nint.Zero)
        {
            return;
        }

        try
        {
            PreparePrintDocumentForWindow(hwnd, doc);
            await PrintManagerInterop.ShowPrintUIForWindowAsync(hwnd);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ShowPrintUIForWindowAsync failed: {ex.Message}");
        }
    }

    private void PreparePrintDocumentForWindow(nint hwnd, DocumentViewModel doc)
    {
        _activePrintDocumentVm = doc;

        if (_registeredPrintManager is null || _registeredPrintHwnd != hwnd)
        {
            if (_registeredPrintManager is not null)
            {
                _registeredPrintManager.PrintTaskRequested -= OnPrintTaskRequested;
            }

            _registeredPrintManager = PrintManagerInterop.GetForWindow(hwnd);
            _registeredPrintHwnd = hwnd;
            _registeredPrintManager.PrintTaskRequested += OnPrintTaskRequested;
        }

        CleanupPrintDocumentInstance();

        _printDocument = new PrintDocument();
        _printDocumentSource = _printDocument.DocumentSource;
        _printDocument.Paginate += OnPrintDocumentPaginate;
        _printDocument.GetPreviewPage += OnPrintDocumentGetPreviewPage;
        _printDocument.AddPages += OnPrintDocumentAddPages;
    }

    private void CleanupPrintDocumentInstance()
    {
        if (_printDocument is not null)
        {
            _printDocument.Paginate -= OnPrintDocumentPaginate;
            _printDocument.GetPreviewPage -= OnPrintDocumentGetPreviewPage;
            _printDocument.AddPages -= OnPrintDocumentAddPages;
            _printDocument = null;
        }

        _printDocumentSource = null;
        _currentPrintTaskOptions = null;
        PrintStagingCanvas?.Children.Clear();
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        DocumentViewModel? doc = _activePrintDocumentVm ?? ViewModel.CurrentDocument;
        IPrintDocumentSource? source = _printDocumentSource;
        if (doc is null || source is null)
        {
            return;
        }

        string title = string.IsNullOrWhiteSpace(doc.FileName) ? "WinPDF Document" : doc.FileName;

        PrintTask printTask = args.Request.CreatePrintTask(title, sourceRequestedArgs =>
        {
            PrintTaskSourceRequestedDeferral deferral = sourceRequestedArgs.GetDeferral();
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    sourceRequestedArgs.SetSource(source);
                }
                finally
                {
                    deferral.Complete();
                }
            });
        });

        try
        {
            PrintPageRangeOptions rangeOptions = printTask.Options.PageRangeOptions;
            rangeOptions.AllowAllPages = true;
            rangeOptions.AllowCurrentPage = true;
            rangeOptions.AllowCustomSetOfPages = true;
        }
        catch
        {
        }

        try
        {
            if (doc.Pages.Count > 0)
            {
                int curIdx = Math.Clamp(doc.CurrentPageIndex, 0, doc.Pages.Count - 1);
                double aspect = DocumentViewModel.GetPageDisplayAspectRatio(doc.Pages[curIdx], doc.Rotation);
                printTask.Options.Orientation = aspect > 1.05
                    ? PrintOrientation.Landscape
                    : PrintOrientation.Portrait;
            }
        }
        catch
        {
        }

        printTask.Completed += OnPrintTaskCompleted;
    }

    private void OnPrintTaskCompleted(PrintTask sender, PrintTaskCompletedEventArgs args)
    {
        sender.Completed -= OnPrintTaskCompleted;
        DispatcherQueue.TryEnqueue(() =>
        {
            PrintStagingCanvas?.Children.Clear();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: true);
        });
    }

    private void OnPrintDocumentPaginate(object sender, PaginateEventArgs e)
    {
        DocumentViewModel? doc = _activePrintDocumentVm ?? ViewModel.CurrentDocument;
        if (_printDocument is null || doc is null)
        {
            return;
        }

        _currentPrintTaskOptions = e.PrintTaskOptions;
        _printDocument.SetPreviewPageCount(doc.PageCount, PreviewPageCountType.Final);
    }

    private void OnPrintDocumentGetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        DocumentViewModel? doc = _activePrintDocumentVm ?? ViewModel.CurrentDocument;
        if (_printDocument is null || doc is null || e.PageNumber < 1 || e.PageNumber > doc.PageCount)
        {
            return;
        }

        try
        {
            UIElement pageVisual = BuildPrintPageElement(doc, e.PageNumber - 1, _currentPrintTaskOptions, targetDpi: 150.0);
            _printDocument.SetPreviewPage(e.PageNumber, pageVisual);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetPreviewPage failed for page {e.PageNumber}: {ex.Message}");
        }
    }

    private void OnPrintDocumentAddPages(object sender, AddPagesEventArgs e)
    {
        DocumentViewModel? doc = _activePrintDocumentVm ?? ViewModel.CurrentDocument;
        if (_printDocument is null || doc is null)
        {
            _printDocument?.AddPagesComplete();
            return;
        }

        try
        {
            PrintTaskOptions options = e.PrintTaskOptions ?? _currentPrintTaskOptions!;
            double targetDpi = doc.PageCount <= 30 ? 220.0 : 160.0;
            for (int i = 0; i < doc.PageCount; i++)
            {
                UIElement pageVisual = BuildPrintPageElement(doc, i, options, targetDpi);
                _printDocument.AddPage(pageVisual);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AddPages failed: {ex.Message}");
        }
        finally
        {
            _printDocument.AddPagesComplete();
        }
    }

    private UIElement BuildPrintPageElement(
        DocumentViewModel doc,
        int pageIndex,
        PrintTaskOptions? printOptions,
        double targetDpi)
    {
        PdfPageInfo pageInfo = doc.Pages[pageIndex];
        int effectiveRot = PageCoordinateTransform.NormalizeRotation(pageInfo.Rotation + doc.Rotation);
        bool swap = effectiveRot is 90 or 270;
        double pageWidthPt = Math.Max(1.0, swap ? pageInfo.HeightPt : pageInfo.WidthPt);
        double pageHeightPt = Math.Max(1.0, swap ? pageInfo.WidthPt : pageInfo.HeightPt);

        double paperWidthDip = pageWidthPt * (96.0 / 72.0);
        double paperHeightDip = pageHeightPt * (96.0 / 72.0);
        Rect imageableRect = new(0, 0, paperWidthDip, paperHeightDip);

        if (printOptions is not null)
        {
            try
            {
                PrintPageDescription desc = printOptions.GetPageDescription((uint)(pageIndex + 1));
                if (desc.PageSize.Width > 0 && desc.PageSize.Height > 0)
                {
                    paperWidthDip = desc.PageSize.Width;
                    paperHeightDip = desc.PageSize.Height;
                    imageableRect = (desc.ImageableRect.Width > 0 && desc.ImageableRect.Height > 0)
                        ? desc.ImageableRect
                        : new Rect(0, 0, paperWidthDip, paperHeightDip);
                }
            }
            catch
            {
            }
        }

        double fitScaleDip = Math.Min(imageableRect.Width / pageWidthPt, imageableRect.Height / pageHeightPt);
        double drawWidthDip = Math.Max(1.0, pageWidthPt * fitScaleDip);
        double drawHeightDip = Math.Max(1.0, pageHeightPt * fitScaleDip);
        double offsetLeftDip = imageableRect.X + ((imageableRect.Width - drawWidthDip) / 2.0);
        double offsetTopDip = imageableRect.Y + ((imageableRect.Height - drawHeightDip) / 2.0);

        double dpiScale = Math.Max(1.0, targetDpi / 96.0);
        int renderWidthPx = Math.Max(1, (int)Math.Round(drawWidthDip * dpiScale));
        int renderHeightPx = Math.Max(1, (int)Math.Round(drawHeightDip * dpiScale));

        const int MaxPrintDimensionPx = 2800;
        if (renderWidthPx > MaxPrintDimensionPx || renderHeightPx > MaxPrintDimensionPx)
        {
            double clamp = Math.Min((double)MaxPrintDimensionPx / renderWidthPx, (double)MaxPrintDimensionPx / renderHeightPx);
            renderWidthPx = Math.Max(1, (int)Math.Round(renderWidthPx * clamp));
            renderHeightPx = Math.Max(1, (int)Math.Round(renderHeightPx * clamp));
        }

        int userRot = PageCoordinateTransform.NormalizeRotation(doc.Rotation);
        PdfRenderRequest request = new(
            doc.DocumentId,
            pageIndex,
            renderWidthPx,
            renderHeightPx,
            renderWidthPx,
            renderHeightPx,
            0,
            0,
            userRot,
            PdfRenderFlags.Annotations | PdfRenderFlags.Printing | PdfRenderFlags.LimitedImageCache);

        using PdfBitmapLease lease = _pdfService.RenderAsync(request, PdfJobPriority.Critical).GetAwaiter().GetResult();
        WriteableBitmap? bitmap = CreatePrintWriteableBitmap(lease);

        Canvas pageCanvas = new()
        {
            Width = paperWidthDip,
            Height = paperHeightDip,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White)
        };

        if (bitmap is not null)
        {
            Image pageImage = new()
            {
                Width = drawWidthDip,
                Height = drawHeightDip,
                Stretch = Stretch.Fill,
                Source = bitmap
            };
            Canvas.SetLeft(pageImage, offsetLeftDip);
            Canvas.SetTop(pageImage, offsetTopDip);
            pageCanvas.Children.Add(pageImage);
        }

        if (PrintStagingCanvas is not null)
        {
            PrintStagingCanvas.Children.Add(pageCanvas);
            pageCanvas.Measure(new Size(paperWidthDip, paperHeightDip));
            pageCanvas.Arrange(new Rect(0, 0, paperWidthDip, paperHeightDip));
            pageCanvas.UpdateLayout();
            PrintStagingCanvas.Children.Remove(pageCanvas);
        }
        else
        {
            pageCanvas.Measure(new Size(paperWidthDip, paperHeightDip));
            pageCanvas.Arrange(new Rect(0, 0, paperWidthDip, paperHeightDip));
            pageCanvas.UpdateLayout();
        }

        return pageCanvas;
    }

    private static unsafe WriteableBitmap? CreatePrintWriteableBitmap(PdfBitmapLease lease)
    {
        if (lease.IsDisposed || lease.Width <= 0 || lease.Height <= 0 || lease.SizeInBytes <= 0)
        {
            return null;
        }

        try
        {
            WriteableBitmap bitmap = new(lease.Width, lease.Height);
            if (bitmap.PixelBuffer is IPrintBufferByteAccess byteAccess)
            {
                byte* dest = byteAccess.Buffer;
                byte* src = (byte*)lease.Scan0;
                long bytesToCopy = Math.Min(lease.SizeInBytes, (long)lease.Stride * lease.Height);
                Buffer.MemoryCopy(src, dest, bytesToCopy, bytesToCopy);
            }
            else
            {
                using Stream stream = bitmap.PixelBuffer.AsStream();
                stream.Write(lease.AsSpan());
            }

            bitmap.Invalidate();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

}
