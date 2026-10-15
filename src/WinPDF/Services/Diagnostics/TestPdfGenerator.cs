using System.Globalization;
using System.Text;

namespace WinPDF.Services.Diagnostics;

/// <summary>
/// Generates compliant, native PDF 1.4 documents containing varied page sizes, aspect ratios,
/// dense typography, fine vector grids, and calibration patterns used for automated stress testing,
/// zoom ramping, scrolling, and memory profiling.
/// </summary>
public static class TestPdfGenerator
{
    private const int DefaultTotalPages = 36;

    /// <summary>
    /// Generates a 36-page test PDF document in the local temporary directory if it does not already exist,
    /// or overwrites it if requested.
    /// </summary>
    /// <param name="forceRecreate">Whether to overwrite the file even if it exists.</param>
    /// <returns>The absolute path to the generated test PDF document.</returns>
    public static string GenerateDefaultTestPdf(bool forceRecreate = false)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "winpdf_autotest.pdf");
        if (!forceRecreate && File.Exists(tempPath))
        {
            try
            {
                FileInfo fi = new(tempPath);
                if (fi.Length > 4096)
                {
                    return tempPath;
                }
            }
            catch
            {
            }
        }

        byte[] pdfBytes = CreateTestPdfBytes(DefaultTotalPages);
        File.WriteAllBytes(tempPath, pdfBytes);
        return tempPath;
    }

    /// <summary>
    /// Gets the dimensions (width and height in points) for a given page index.
    /// Covers 9 distinct aspect ratios and sizes, ranging from compact A6 to 2400x1600 blueprint posters.
    /// </summary>
    /// <param name="pageIndexZeroBased">The zero-based page index.</param>
    /// <returns>A tuple containing (WidthPt, HeightPt, KindName).</returns>
    public static (int Width, int Height, string Kind) GetPageGeometry(int pageIndexZeroBased)
    {
        return (pageIndexZeroBased % 9) switch
        {
            0 => (595, 842, "A4 Portrait"),
            1 => (1600, 900, "16:9 Presentation Slide"),
            2 => (1920, 1080, "4K Ultra-Wide Slide"),
            3 => (2400, 1600, "Architectural Blueprint Poster"),
            4 => (842, 595, "A4 Landscape"),
            5 => (612, 792, "US Letter Portrait"),
            6 => (2000, 600, "Ultra-Panoramic Strip"),
            7 => (1000, 1000, "1:1 Square Canvas"),
            _ => (298, 420, "Compact A6 Document")
        };
    }

    /// <summary>
    /// Builds a valid PDF 1.4 binary byte array with the specified number of pages
    /// featuring diverse page sizes, high-density text scales, and vector test patterns.
    /// </summary>
    /// <param name="pageCount">Number of pages to include in the document.</param>
    /// <returns>The raw PDF document bytes.</returns>
    public static byte[] CreateTestPdfBytes(int pageCount)
    {
        using MemoryStream ms = new();
        using StreamWriter writer = new(ms, Encoding.ASCII, leaveOpen: true);

        List<long> objectOffsets = new();

        // 1. PDF Header
        writer.Write("%PDF-1.4\n%âãÏÓ\n");
        writer.Flush();

        // Object 0 dummy offset for 1-based indexing in xref table
        objectOffsets.Add(0);

        // Object 1: Catalog
        objectOffsets.Add(ms.Position);
        writer.Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        writer.Flush();

        // Object 2: Pages tree
        objectOffsets.Add(ms.Position);
        StringBuilder kidsBuilder = new();
        for (int i = 0; i < pageCount; i++)
        {
            kidsBuilder.Append($"{3 + i} 0 R ");
        }

        writer.Write($"2 0 obj\n<< /Type /Pages /Kids [ {kidsBuilder} ] /Count {pageCount} >>\nendobj\n");
        writer.Flush();

        // Page objects: 3 to (2 + pageCount)
        // Content objects: (3 + pageCount) to (2 + 2 * pageCount)
        int firstContentObjIndex = 3 + pageCount;

        for (int i = 0; i < pageCount; i++)
        {
            int pageObjIndex = 3 + i;
            int contentObjIndex = firstContentObjIndex + i;

            var (width, height, _) = GetPageGeometry(i);

            objectOffsets.Add(ms.Position);
            writer.Write($"{pageObjIndex} 0 obj\n");
            writer.Write($"<< /Type /Page /Parent 2 0 R /MediaBox [ 0 0 {width} {height} ] /Contents {contentObjIndex} 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> /F2 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >> >> >> >>\n");
            writer.Write("endobj\n");
            writer.Flush();
        }

        // Content stream objects
        for (int i = 0; i < pageCount; i++)
        {
            int contentObjIndex = firstContentObjIndex + i;
            var (width, height, kind) = GetPageGeometry(i);

            string streamContent = BuildPageContentStream(i + 1, pageCount, width, height, kind);
            byte[] streamBytes = Encoding.ASCII.GetBytes(streamContent);

            objectOffsets.Add(ms.Position);
            writer.Write($"{contentObjIndex} 0 obj\n");
            writer.Write($"<< /Length {streamBytes.Length} >>\nstream\n");
            writer.Flush();

            ms.Write(streamBytes, 0, streamBytes.Length);

            writer.Write("\nendstream\nendobj\n");
            writer.Flush();
        }

        // Cross-Reference Table
        long xrefOffset = ms.Position;
        int totalObjects = objectOffsets.Count;
        writer.Write($"xref\n0 {totalObjects}\n");
        writer.Write("0000000000 65535 f \n");

        for (int i = 1; i < totalObjects; i++)
        {
            writer.Write($"{objectOffsets[i]:D10} 00000 n \n");
        }

        // Trailer
        writer.Write("trailer\n");
        writer.Write($"<< /Size {totalObjects} /Root 1 0 R >>\n");
        writer.Write("startxref\n");
        writer.Write($"{xrefOffset}\n");
        writer.Write("%%EOF\n");
        writer.Flush();

        return ms.ToArray();
    }

    private static string BuildPageContentStream(int pageNumber, int totalPages, int width, int height, string kind)
    {
        StringBuilder sb = new();

        // 1. Top Decorative Banner
        double headerHeight = Math.Max(50.0, Math.Min(80.0, height * 0.08));
        sb.Append(CultureInfo.InvariantCulture, $"0.11 0.42 0.82 rg\n0 {height - headerHeight} {width} {headerHeight} re f\n");

        // 2. Bottom Accent Bar
        sb.Append(CultureInfo.InvariantCulture, $"0.82 0.32 0.12 rg\n0 0 {width} 14 re f\n");

        // 3. Header Title and Page Geometry Text
        sb.Append("BT\n/F2 22 Tf\n1 1 1 rg\n");
        sb.Append(CultureInfo.InvariantCulture, $"30 {height - (headerHeight * 0.58)} Td\n");
        sb.Append(CultureInfo.InvariantCulture, $"(WinPDF AutoTest - Page {pageNumber} of {totalPages} [{kind}]) Tj\n");
        sb.Append("ET\n");

        sb.Append("BT\n/F1 12 Tf\n0.92 0.94 0.98 rg\n");
        sb.Append(CultureInfo.InvariantCulture, $"30 {height - (headerHeight * 0.88)} Td\n");
        sb.Append(CultureInfo.InvariantCulture, $"(Dimensions: {width} x {height} pt | Aspect: {(double)width / height:F2}:1) Tj\n");
        sb.Append("ET\n");

        // 4. Perimeter Rulers (Tick marks every 50 points along borders)
        sb.Append("0.6 0.6 0.6 RG\n0.5 w\n");
        for (int x = 50; x < width; x += 50)
        {
            double tickLen = (x % 100 == 0) ? 12 : 6;
            sb.Append(CultureInfo.InvariantCulture, $"{x} 14 m {x} {14 + tickLen} l S\n");
            sb.Append(CultureInfo.InvariantCulture, $"{x} {height - headerHeight} m {x} {height - headerHeight - tickLen} l S\n");
        }

        for (int y = 50; y < height - headerHeight; y += 50)
        {
            double tickLen = (y % 100 == 0) ? 12 : 6;
            sb.Append(CultureInfo.InvariantCulture, $"0 {y} m {tickLen} {y} l S\n");
            sb.Append(CultureInfo.InvariantCulture, $"{width} {y} m {width - tickLen} {y} l S\n");
        }

        // 5. Center Diagnostic Card
        int cardW = Math.Min(540, Math.Max(260, width - 80));
        int cardH = Math.Min(320, Math.Max(180, height - (int)headerHeight - 40));
        int cardX = (width - cardW) / 2;
        int cardY = (height - (int)headerHeight - cardH) / 2 + 10;

        sb.Append(CultureInfo.InvariantCulture, $"0.95 0.96 0.98 rg\n{cardX} {cardY} {cardW} {cardH} re f\n");
        sb.Append(CultureInfo.InvariantCulture, $"0.75 0.78 0.82 RG\n1.5 w\n{cardX} {cardY} {cardW} {cardH} re S\n");

        // Card Header
        sb.Append("BT\n/F2 16 Tf\n0.15 0.20 0.30 rg\n");
        sb.Append(CultureInfo.InvariantCulture, $"{cardX + 20} {cardY + cardH - 32} Td\n");
        sb.Append(CultureInfo.InvariantCulture, $"(Resolution & Stress Target: Page #{pageNumber}) Tj\n");
        sb.Append("ET\n");

        // Concentric Circles & Crosshair Calibration (Center of Card)
        double circleCx = cardX + cardW - 75;
        double circleCy = cardY + (cardH / 2.0);
        double[] radii = [15.0, 30.0, 48.0];

        sb.Append("0.2 0.5 0.8 RG\n1.0 w\n");
        foreach (double r in radii)
        {
            AppendCircle(sb, circleCx, circleCy, r);
        }

        // Crosshairs through circle center
        sb.Append(CultureInfo.InvariantCulture, $"{circleCx - 55} {circleCy} m {circleCx + 55} {circleCy} l S\n");
        sb.Append(CultureInfo.InvariantCulture, $"{circleCx} {circleCy - 55} m {circleCx} {circleCy + 55} l S\n");

        // 6. Multi-Scale Font Resolution Ladder
        int ladderX = cardX + 20;
        int ladderTop = cardY + cardH - 60;

        AppendText(sb, ladderX, ladderTop, 14, true, "0.15 0.15 0.15", "Font Sharpness & Vector Quality Ladder:");
        AppendText(sb, ladderX, ladderTop - 22, 6, false, "0.25 0.25 0.25", "6pt: ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789 (Ultra-fine micro glyph resolution test)");
        AppendText(sb, ladderX, ladderTop - 38, 8, false, "0.20 0.20 0.20", "8pt: ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789 (Small text sharpness verification)");
        AppendText(sb, ladderX, ladderTop - 56, 10, false, "0.15 0.15 0.15", "10pt: ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789 (Standard document body font)");
        AppendText(sb, ladderX, ladderTop - 76, 12, true, "0.10 0.10 0.10", "12pt: ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789 (Sub-heading bold text)");
        AppendText(sb, ladderX, ladderTop - 100, 16, true, "0.08 0.25 0.65", "16pt: High Zoom Edge Contrast & Antialiasing");
        AppendText(sb, ladderX, ladderTop - 128, 22, true, "0.05 0.15 0.45", "22pt: WinPDF Render Stress");

        // 7. Fine Vector Line Resolution Grid (0.25pt, 0.5pt, 1.0pt, 2.0pt)
        int gridX = cardX + 20;
        int gridY = cardY + 25;
        double[] lineThicknesses = [0.25, 0.5, 1.0, 2.0];

        AppendText(sb, gridX, gridY + 35, 9, false, "0.35 0.35 0.35", "Line Weights (0.25pt, 0.5pt, 1.0pt, 2.0pt):");

        for (int li = 0; li < lineThicknesses.Length; li++)
        {
            double lw = lineThicknesses[li];
            double lineY = gridY + (li * 8);
            sb.Append(CultureInfo.InvariantCulture, $"0.3 0.3 0.3 RG\n{lw:F2} w\n");
            sb.Append(CultureInfo.InvariantCulture, $"{gridX} {lineY} m {gridX + 160} {lineY} l S\n");
        }

        // 8. Color Calibration Swatches (RGB, CMYK-like primaries)
        int swatchX = gridX + 190;
        int swatchY = gridY;
        string[] swatches =
        [
            "0.85 0.15 0.15", // Red
            "0.15 0.70 0.25", // Green
            "0.15 0.35 0.85", // Blue
            "0.92 0.75 0.10", // Yellow
            "0.75 0.15 0.70", // Magenta
            "0.10 0.75 0.85"  // Cyan
        ];

        for (int si = 0; si < swatches.Length; si++)
        {
            double sx = swatchX + (si * 18);
            sb.Append(CultureInfo.InvariantCulture, $"{swatches[si]} rg\n{sx} {swatchY} 14 24 re f\n");
            sb.Append(CultureInfo.InvariantCulture, $"0.4 0.4 0.4 RG\n0.5 w\n{sx} {swatchY} 14 24 re S\n");
        }

        return sb.ToString();
    }

    private static void AppendText(StringBuilder sb, double x, double y, int fontSize, bool bold, string rgbColor, string text)
    {
        string fontName = bold ? "/F2" : "/F1";
        sb.Append($"BT\n{fontName} {fontSize} Tf\n{rgbColor} rg\n");
        sb.Append(CultureInfo.InvariantCulture, $"{x} {y} Td\n");
        sb.Append($"({EscapePdfString(text)}) Tj\n");
        sb.Append("ET\n");
    }

    private static void AppendCircle(StringBuilder sb, double cx, double cy, double r)
    {
        double k = r * 0.552284749831;
        sb.Append(CultureInfo.InvariantCulture, $"{cx + r} {cy} m\n");
        sb.Append(CultureInfo.InvariantCulture, $"{cx + r} {cy + k} {cx + k} {cy + r} {cx} {cy + r} c\n");
        sb.Append(CultureInfo.InvariantCulture, $"{cx - k} {cy + r} {cx - r} {cy + k} {cx - r} {cy} c\n");
        sb.Append(CultureInfo.InvariantCulture, $"{cx - r} {cy - k} {cx - k} {cy - r} {cx} {cy - r} c\n");
        sb.Append(CultureInfo.InvariantCulture, $"{cx + k} {cy - r} {cx + r} {cy - k} {cx + r} {cy} c S\n");
    }

    private static string EscapePdfString(string input)
    {
        return input
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
    }
}
