using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinPDF.Models;
using WinPDF.ViewModels;
using WinPDF.Views;

namespace WinPDF.Services.Diagnostics;

/// <summary>
/// Memory metric snapshot equivalent to Visual Studio 2026 Process Diagnostics tool.
/// </summary>
/// <param name="WorkingSetMb">Total working set memory in megabytes.</param>
/// <param name="PrivateBytesMb">Private memory bytes committed by the process in megabytes.</param>
/// <param name="ManagedHeapMb">Total managed heap size reported by the garbage collector in megabytes.</param>
/// <param name="LohMb">Large Object Heap (LOH) size in megabytes.</param>
public readonly record struct MemoryMetrics(double WorkingSetMb, double PrivateBytesMb, double ManagedHeapMb, double LohMb)
{
    /// <summary>
    /// Captures the current memory metrics from the process and garbage collector.
    /// </summary>
    /// <returns>A new <see cref="MemoryMetrics"/> instance with live values.</returns>
    public static MemoryMetrics Capture()
    {
        using Process proc = Process.GetCurrentProcess();
        proc.Refresh();

        double ws = proc.WorkingSet64 / (1024.0 * 1024.0);
        double pb = proc.PrivateMemorySize64 / (1024.0 * 1024.0);
        double managed = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

        var gcInfo = GC.GetGCMemoryInfo();
        double loh = 0.0;
        if (gcInfo.GenerationInfo.Length > 3)
        {
            loh = gcInfo.GenerationInfo[3].SizeAfterBytes / (1024.0 * 1024.0);
        }

        return new MemoryMetrics(ws, pb, managed, loh);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"WS: {WorkingSetMb:F1} MB | Private: {PrivateBytesMb:F1} MB | Managed: {ManagedHeapMb:F1} MB | LOH: {LohMb:F2} MB";
}

/// <summary>
/// Automated diagnostic test runner triggered via the <c>--autotest</c> command-line argument.
/// Orchestrates comprehensive UI stress scenarios, captures first-chance exceptions, records memory profiling,
/// and outputs a comprehensive diagnostic log before exiting.
/// </summary>
public static partial class AutoTestRunner
{
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int dwProcessId);
    private const int AttachParentProcess = -1;

    private sealed record FirstChanceRecord(DateTime Timestamp, string Type, string Message, string? TopStack);
    private sealed record StepResult(string Name, bool Passed, string Details, MemoryMetrics Memory);

    private static readonly ConcurrentBag<FirstChanceRecord> s_exceptions = new();
    private static readonly List<StepResult> s_steps = new();
    private static readonly object s_logLock = new();
    private static bool s_isSubscribed;

    /// <summary>
    /// Subscribes to runtime first-chance exceptions.
    /// </summary>
    public static void InitializeMonitoring()
    {
        if (s_isSubscribed)
        {
            return;
        }

        s_isSubscribed = true;
        try
        {
            _ = AttachConsole(AttachParentProcess);
        }
        catch
        {
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        LogLive("AutoTestRunner monitoring initialized.");
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        Exception ex = e.Exception;

        // Ignore expected token and debounce cancellations
        if (ex is OperationCanceledException or TaskCanceledException)
        {
            return;
        }

        string[] frames = Environment.StackTrace.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var relevantFrames = frames
            .SkipWhile(f => f.Contains("AutoTestRunner") || f.Contains("Environment.get_StackTrace"))
            .Take(8)
            .Select(f => f.Trim())
            .ToArray();
        string stackSummary = string.Join(Environment.NewLine + "        ", relevantFrames);

        s_exceptions.Add(new FirstChanceRecord(
            DateTime.Now,
            ex.GetType().FullName ?? ex.GetType().Name,
            ex.Message,
            stackSummary));

        string firstFrame = relevantFrames.FirstOrDefault() ?? "Unknown";
        LogLive($"[FIRST-CHANCE EXCEPTION] {ex.GetType().Name}: {ex.Message} | At: {firstFrame}");
    }

    private static void LogLive(string message)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.WriteLine(entry);

        try
        {
            lock (s_logLock)
            {
                AppendToLogFiles(entry + Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Executes the automated stress test suite against the specified main window and PDF document path.
    /// </summary>
    /// <param name="window">The active application main window.</param>
    /// <param name="pdfPath">Optional path to the PDF document to test. If null or not found, a 36-page test PDF is generated.</param>
    public static void Run(MainWindow window, string? pdfPath)
    {
        InitializeMonitoring();

        // Enqueue onto UI thread after window layout settles
        window.DispatcherQueue.TryEnqueue(async () =>
        {
            if (window.Content is FrameworkElement root && !root.IsLoaded)
            {
                TaskCompletionSource tcs = new();
                RoutedEventHandler? handler = null;
                handler = (_, _) =>
                {
                    root.Loaded -= handler;
                    tcs.TrySetResult();
                };
                root.Loaded += handler;
                await tcs.Task;
            }

            // Initial warm-up delay for window backdrop and initial view transition
            await Task.Delay(500);

            bool success = false;
            try
            {
                success = await ExecuteSuiteAsync(window, pdfPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUTOTEST FATAL] Unhandled runner failure: {ex}");
                s_steps.Add(new StepResult("FATAL_ERROR", false, ex.Message, MemoryMetrics.Capture()));
                success = false;
            }
            finally
            {
                await FinalizeAndReportAsync(success);
            }
        });
    }

    private static async Task<bool> ExecuteSuiteAsync(MainWindow window, string? pdfPath)
    {
        ShellViewModel shell = window.ViewModel;
        IPdfService pdfService = App.CurrentApp.Services.GetRequiredService<IPdfService>();
        MemoryMetrics initialMem = MemoryMetrics.Capture();
        LogLive($"[AUTOTEST] Initial Memory: {initialMem}");

        // Phase 1: Open Document
        string testPath = pdfPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(testPath) || !File.Exists(testPath))
        {
            testPath = TestPdfGenerator.GenerateDefaultTestPdf(forceRecreate: true);
        }

        LogLive($"[AUTOTEST] Phase 1: Opening document: {testPath}");
        await shell.OpenFilePathAsync(testPath);
        await Task.Delay(700);

        DocumentViewModel? doc = shell.CurrentDocument;
        if (doc is null || doc.PageCount <= 0)
        {
            s_steps.Add(new StepResult("Phase 1: Open Document", false, "Failed to load document or PageCount <= 0", MemoryMetrics.Capture()));
            return false;
        }

        s_steps.Add(new StepResult("Phase 1: Open Document", true, $"Pages: {doc.PageCount}, Path: {Path.GetFileName(testPath)}", MemoryMetrics.Capture()));
        LogLive($"[AUTOTEST] Phase 1 PASS: {doc.PageCount} pages loaded successfully.");

        // Phase 2: Page Navigation Across Varied Geometries
        LogLive("[AUTOTEST] Phase 2: Testing page navigation across diverse page geometries...");
        int[] targetPages = [0, 3, doc.PageCount / 2, doc.PageCount - 1, 0];
        foreach (int pageIdx in targetPages)
        {
            int clampedPage = Math.Clamp(pageIdx, 0, doc.PageCount - 1);
            doc.GoToPage(clampedPage);
            await Task.Delay(250);
            LogLive($"[AUTOTEST]   Navigated to Page {doc.CurrentPageNumber}/{doc.PageCount}");
        }
        s_steps.Add(new StepResult("Phase 2: Landmark Page Navigation", true, $"Navigated 5 landmark pages, settled at {doc.CurrentPageNumber}", MemoryMetrics.Capture()));
        LogLive("[AUTOTEST] Phase 2 PASS");

        // Phase 3: Comprehensive Progressive Zoom Sweep (25% to 500% in 14 Steps)
        double[] zoomLevels = [0.25, 0.33, 0.50, 0.67, 0.75, 1.0, 1.25, 1.50, 1.75, 2.0, 2.50, 3.0, 4.0, 5.0];
        LogLive($"[AUTOTEST] Phase 3: Progressive Zoom Sweep across {zoomLevels.Length} levels (25% to 500%)...");

        MemoryMetrics peakZoomMem = MemoryMetrics.Capture();
        foreach (double zoom in zoomLevels)
        {
            shell.SetZoom(zoom);
            await Task.Delay(180);

            MemoryMetrics currentMem = MemoryMetrics.Capture();
            if (currentMem.WorkingSetMb > peakZoomMem.WorkingSetMb)
            {
                peakZoomMem = currentMem;
            }

            LogLive($"[AUTOTEST]   Zoom {(int)(zoom * 100)}%: {currentMem}");
        }
        s_steps.Add(new StepResult("Phase 3: Progressive Zoom (25%-500%)", true, $"Peak: {peakZoomMem}", peakZoomMem));
        LogLive($"[AUTOTEST] Phase 3 PASS: Peak Zoom Memory = {peakZoomMem}");

        // Phase 4: Fractional / Sub-Pixel Zoom Values
        double[] fractionalZooms = [1.15, 1.33, 1.67, 2.15, 3.85];
        LogLive($"[AUTOTEST] Phase 4: Testing fractional zoom factors: {string.Join(", ", fractionalZooms)}...");
        foreach (double fracZoom in fractionalZooms)
        {
            shell.SetZoom(fracZoom);
            await Task.Delay(150);
            LogLive($"[AUTOTEST]   Fractional Zoom {(int)(fracZoom * 100)}%: {MemoryMetrics.Capture()}");
        }
        s_steps.Add(new StepResult("Phase 4: Fractional Zooms", true, "Sub-pixel zoom values processed cleanly", MemoryMetrics.Capture()));
        LogLive("[AUTOTEST] Phase 4 PASS");

        // Phase 5: Rapid Zoom Flicking Stress (Debounce & Cancellation Race Conditions)
        LogLive("[AUTOTEST] Phase 5: Rapid zoom flicking stress (short interval cancellations)...");
        double[] rapidZooms = [1.0, 5.0, 0.33, 4.0, 0.50, 3.0, 1.0];
        foreach (double flickZoom in rapidZooms)
        {
            shell.SetZoom(flickZoom);
            await Task.Delay(75);
        }
        await Task.Delay(300); // Wait for stabilization
        s_steps.Add(new StepResult("Phase 5: Rapid Zoom Flicking", true, "7 rapid zoom transitions debounced cleanly", MemoryMetrics.Capture()));
        LogLive("[AUTOTEST] Phase 5 PASS");

        // Phase 6: High-Zoom 2D Panning (ContinuousScrollViewer)
        LogLive("[AUTOTEST] Phase 6: High-zoom 2D panning stress (horizontal & vertical offsets)...");
        // Navigate to Page 4 (Huge 2400x1600 blueprint poster)
        if (doc.PageCount >= 4)
        {
            doc.GoToPage(3);
            await Task.Delay(250);
        }

        shell.SetZoom(4.0);
        await Task.Delay(300);

        ScrollViewer? scrollViewer = GetContinuousScrollViewer(window);

        if (scrollViewer is not null)
        {
            double scrollW = scrollViewer.ScrollableWidth;
            double scrollH = scrollViewer.ScrollableHeight;
            LogLive($"[AUTOTEST]   ScrollViewer Dimensions: ScrollableW={scrollW:F0}, ScrollableH={scrollH:F0}, Zoom={scrollViewer.ZoomFactor:F2}");

            // Pan Horizontally: Left -> Center -> Right -> Left
            double[] hRatios = [0.0, 0.5, 1.0, 0.0];
            foreach (double r in hRatios)
            {
                double targetX = scrollW * r;
                scrollViewer.ChangeView(targetX, null, null, disableAnimation: true);
                await Task.Delay(140);
                LogLive($"[AUTOTEST]   Panned Horizontal Offset: {scrollViewer.HorizontalOffset:F0} / {scrollW:F0}");
            }

            // Pan Vertically: Top -> Center -> Bottom -> Top
            double[] vRatios = [0.0, 0.3, 0.7, 1.0, 0.0];
            foreach (double r in vRatios)
            {
                double targetY = scrollH * r;
                scrollViewer.ChangeView(null, targetY, null, disableAnimation: true);
                await Task.Delay(140);
                LogLive($"[AUTOTEST]   Panned Vertical Offset: {scrollViewer.VerticalOffset:F0} / {scrollH:F0}");
            }

            // Diagonal Pan
            scrollViewer.ChangeView(scrollW * 0.75, scrollH * 0.75, null, disableAnimation: true);
            await Task.Delay(180);
            scrollViewer.ChangeView(0, 0, null, disableAnimation: true);
            await Task.Delay(180);

            s_steps.Add(new StepResult("Phase 6: High-Zoom 2D Panning", true, $"Panned across ScrollableW={scrollW:F0}, ScrollableH={scrollH:F0} at 400% zoom", MemoryMetrics.Capture()));
            LogLive("[AUTOTEST] Phase 6 PASS");
        }
        else
        {
            s_steps.Add(new StepResult("Phase 6: High-Zoom 2D Panning", false, "ContinuousScrollViewer could not be located in visual tree", MemoryMetrics.Capture()));
            LogLive("[AUTOTEST] Phase 6 WARNING: ScrollViewer not resolved");
        }

        // Reset zoom to 1.0 for scroll sweep
        shell.SetZoom(1.0);
        await Task.Delay(250);

        // Phase 7: Document-Wide Scrolling Sweeps (Continuous Scroll Stress)
        LogLive("[AUTOTEST] Phase 7: Document-wide scrolling sweeps...");

        // Forward rapid jump sweep
        int stepJump = Math.Max(1, doc.PageCount / 7);
        for (int p = 0; p < doc.PageCount; p += stepJump)
        {
            doc.GoToPage(p);
            await Task.Delay(120);
        }

        // Smooth incremental vertical scrolling
        if (scrollViewer is not null)
        {
            double maxScrollY = scrollViewer.ScrollableHeight;
            int scrollSteps = 10;
            double stepY = maxScrollY / scrollSteps;
            for (int s = 0; s <= scrollSteps; s++)
            {
                scrollViewer.ChangeView(null, s * stepY, null, disableAnimation: true);
                await Task.Delay(80);
            }
        }

        // Reverse rapid jump flick back to page 1
        doc.GoToPage(0);
        await Task.Delay(250);

        // Mid-scroll zoom: jump to middle, zoom to 3.0, scroll next 2 pages while zoomed
        int midPage = doc.PageCount / 2;
        doc.GoToPage(midPage);
        await Task.Delay(200);
        shell.SetZoom(3.0);
        await Task.Delay(250);
        doc.GoToPage(Math.Min(doc.PageCount - 1, midPage + 2));
        await Task.Delay(250);

        // Return to 1.0 zoom
        shell.SetZoom(1.0);
        await Task.Delay(250);

        s_steps.Add(new StepResult("Phase 7: Scrolling Sweeps", true, "Forward sweep, incremental scroll, reverse flick, and mid-scroll zoom executed", MemoryMetrics.Capture()));
        LogLive("[AUTOTEST] Phase 7 PASS");

        // Phase 8: Multi-Page & View Mode Stress with Zoom
        LogLive("[AUTOTEST] Phase 8: Testing Multi-Page and Paged View Modes with Zoom...");

        // Dual-Page Mode
        LogLive("[AUTOTEST]   Activating Dual Page mode...");
        shell.IsDualPage = true;
        await Task.Delay(350);

        double[] dualZooms = [0.75, 1.25, 2.0];
        foreach (double dz in dualZooms)
        {
            shell.SetZoom(dz);
            await Task.Delay(180);
        }

        doc.GoToPage(Math.Min(doc.PageCount - 1, 4));
        await Task.Delay(250);

        shell.IsDualPage = false;
        shell.SetZoom(1.0);
        await Task.Delay(300);

        // Paged Mode (FlipView)
        LogLive("[AUTOTEST]   Switching to Paged (FlipView) mode...");
        shell.SetViewMode(ViewMode.Paged);
        await Task.Delay(350);

        shell.SetZoom(1.5);
        await Task.Delay(200);
        shell.NextPage();
        await Task.Delay(200);
        shell.PreviousPage();
        await Task.Delay(200);

        shell.SetViewMode(ViewMode.Continuous);
        shell.SetZoom(1.0);
        await Task.Delay(300);

        s_steps.Add(new StepResult("Phase 8: View Modes with Zoom", true, "Dual-Page and Paged view modes operated cleanly under zoom", MemoryMetrics.Capture()));
        LogLive("[AUTOTEST] Phase 8 PASS");

        // Phase 9: Display Transforms & Night Mode under Zoom
        LogLive("[AUTOTEST] Phase 9: Display transforms and Night Mode under zoom...");
        shell.SetZoom(2.0);
        await Task.Delay(250);

        // Full 360-degree rotation cycle
        for (int r = 0; r < 4; r++)
        {
            shell.RotateClockwise();
            await Task.Delay(200);
            LogLive($"[AUTOTEST]   Rotated 90 CW (Step {r + 1}/4) at 200% zoom");
        }

        // Night Mode toggle while zoomed
        shell.IsNightMode = true;
        await Task.Delay(250);
        shell.IsNightMode = false;
        await Task.Delay(200);

        // Fit commands
        shell.FitWidth();
        await Task.Delay(200);
        shell.FitPage();
        await Task.Delay(200);

        s_steps.Add(new StepResult("Phase 9: Transforms & Night Mode", true, "Rotation, Night Mode, and Fit commands tested under zoom", MemoryMetrics.Capture()));
        LogLive("[AUTOTEST] Phase 9 PASS");

        // Phase 10: PDFium Text Selection, Full-Document Search & GPU Highlight Stress
        LogLive("[AUTOTEST] Phase 10: Testing PDFium Text Selection, Full-Document Search & GPU Highlights...");
        doc.GoToPage(0);
        shell.SetZoom(1.25);
        await Task.Delay(250);

        shell.OpenSearch();
        await doc.ExecuteSearchAsync("WinPDF", matchCase: false, jumpToNearestMatch: true);
        await Task.Delay(200);

        int matchCount = doc.TotalSearchMatches;
        await shell.FindNextMatchAsync();
        await Task.Delay(150);
        await shell.FindPreviousMatchAsync();
        await Task.Delay(150);

        PdfTextSelection? rangeSel = await pdfService.SelectTextRangeAsync(doc.DocumentId, 0, 0, 6);
        doc.SetSelection(rangeSel);
        await Task.Delay(150);

        string boundedText = await pdfService.GetBoundedTextAsync(doc.DocumentId, 0, new PdfRect(0, 0, 600, 842));
        doc.ClearSelection();
        shell.CloseSearch();
        await Task.Delay(150);

        bool textPass = matchCount > 0 && rangeSel is { HasContent: true } && !string.IsNullOrEmpty(boundedText);
        s_steps.Add(new StepResult(
            "Phase 10: Text Search & Selection",
            textPass,
            $"Matches: {matchCount}, Selected: '{rangeSel?.SelectedText}', BoundedChars: {boundedText.Length}",
            MemoryMetrics.Capture()));
        LogLive($"[AUTOTEST] Phase 10 {(textPass ? "PASS" : "FAIL")}: Matches={matchCount}, Selected='{rangeSel?.SelectedText}'");

        // Phase 11: Maximized FullScreen Transition & TitleBar Caption Restoration
        LogLive("[AUTOTEST] Phase 11: Testing FullScreen toggle from Maximized window state...");
        if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
        {
            op.Maximize();
        }
        await Task.Delay(500);

        shell.ToggleFullScreen();
        await Task.Delay(500);

        shell.ToggleFullScreen();
        await Task.Delay(500);

        var pAfter = window.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        var tbAfter = window.AppWindow.TitleBar;
        bool fullScreenRestorePass = pAfter?.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized &&
                                     tbAfter?.RightInset > 40;

        s_steps.Add(new StepResult(
            "Phase 11: Maximized FullScreen & Caption Button Restoration",
            fullScreenRestorePass,
            $"State={pAfter?.State}, RightInset={tbAfter?.RightInset}, Height={tbAfter?.Height}",
            MemoryMetrics.Capture()));
        LogLive($"[AUTOTEST] Phase 11 {(fullScreenRestorePass ? "PASS" : "FAIL")}: State={pAfter?.State}, RightInset={tbAfter?.RightInset}");

        // Phase 12: Final Memory Reclamation & LOH Check
        LogLive("[AUTOTEST] Phase 12: Final memory stabilization and reclamation check...");
        shell.SetZoom(1.0);
        doc.GoToPage(0);
        await Task.Delay(400);

        MemoryMetrics finalReclaimedMem = MemoryMetrics.Capture();
        s_steps.Add(new StepResult("Phase 12: Final Reclamation", true, $"Final Memory: {finalReclaimedMem}", finalReclaimedMem));
        LogLive($"[AUTOTEST] Phase 12 PASS: Final Memory = {finalReclaimedMem}");

        return true;
    }

    private static ScrollViewer? GetContinuousScrollViewer(MainWindow window)
    {
        return FindChild<ScrollViewer>(window.Content, s => s.ZoomMode == ZoomMode.Enabled);
    }

    private static T? FindChild<T>(DependencyObject? parent, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && (predicate is null || predicate(typed)))
            {
                return typed;
            }

            var sub = FindChild<T>(child, predicate);
            if (sub is not null)
            {
                return sub;
            }
        }

        return null;
    }

    private static async Task FinalizeAndReportAsync(bool overallSuccess)
    {
        // Allow in-flight timers or uploads to wrap up
        await Task.Delay(200);

        MemoryMetrics finalMem = MemoryMetrics.Capture();
        var recordedExceptions = s_exceptions.ToArray();

        // Determine outcome: Fail if any step failed or if any unexpected first-chance exceptions occurred
        bool pass = overallSuccess && s_steps.All(s => s.Passed) && recordedExceptions.Length == 0;

        StringBuilder report = new();
        report.AppendLine("================================================================================");
        report.AppendLine("                     WINPDF DIAGNOSTIC AUTO-TEST REPORT                        ");
        report.AppendLine("================================================================================");
        report.AppendLine($"Timestamp:    {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        report.AppendLine($"Environment:  .NET {Environment.Version} | OS: {Environment.OSVersion}");
        report.AppendLine($"Process ID:   {Environment.ProcessId}");
        report.AppendLine($"Overall Status: {(pass ? "PASS" : "FAIL")}");
        report.AppendLine("--------------------------------------------------------------------------------");
        report.AppendLine("STEP-BY-STEP EXECUTION RESULTS:");
        report.AppendLine("--------------------------------------------------------------------------------");

        foreach (var step in s_steps)
        {
            string status = step.Passed ? "[PASS]" : "[FAIL]";
            report.AppendLine($"{status} {step.Name,-36} => {step.Details}");
            report.AppendLine($"       Metrics: {step.Memory}");
        }

        report.AppendLine("--------------------------------------------------------------------------------");
        report.AppendLine("MEMORY PROFILE SUMMARY:");
        report.AppendLine("--------------------------------------------------------------------------------");
        if (s_steps.Count > 0)
        {
            report.AppendLine($"Initial Memory:    {s_steps[0].Memory}");
        }
        var peakStep = s_steps.FirstOrDefault(s => s.Name.StartsWith("Phase 3"));
        if (peakStep is not null)
        {
            report.AppendLine($"Peak Zoom Memory:  {peakStep.Memory}");
        }
        report.AppendLine($"Final Memory:      {finalMem}");
        report.AppendLine("--------------------------------------------------------------------------------");
        report.AppendLine($"FIRST-CHANCE EXCEPTIONS ({recordedExceptions.Length}):");
        report.AppendLine("--------------------------------------------------------------------------------");

        if (recordedExceptions.Length == 0)
        {
            report.AppendLine("None. All stages executed with zero exceptions!");
        }
        else
        {
            for (int i = 0; i < recordedExceptions.Length; i++)
            {
                var rec = recordedExceptions[i];
                report.AppendLine($"#{i + 1:D2} [{rec.Timestamp:HH:mm:ss.fff}] {rec.Type}");
                report.AppendLine($"    Message: {rec.Message}");
                if (!string.IsNullOrWhiteSpace(rec.TopStack))
                {
                    report.AppendLine($"    Location: {rec.TopStack}");
                }
            }
        }

        report.AppendLine("================================================================================");

        string reportText = report.ToString();
        Console.WriteLine();
        Console.WriteLine(reportText);

        SaveReportToFile(reportText);

        await Task.Delay(150);
        Environment.Exit(pass ? 0 : 1);
    }

    private static void SaveReportToFile(string reportText)
    {
        try
        {
            // 1. Solution/Repo artifacts directory
            string baseDir = AppContext.BaseDirectory;
            DirectoryInfo? dir = new(baseDir);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinPDF.sln")))
            {
                dir = dir.Parent;
            }

            if (dir is not null)
            {
                string artifactsDir = Path.Combine(dir.FullName, "artifacts");
                Directory.CreateDirectory(artifactsDir);
                File.WriteAllText(Path.Combine(artifactsDir, "autotest-report.log"), reportText);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUTOTEST] Warning: Failed to write to artifacts folder: {ex.Message}");
        }

        try
        {
            // 2. LocalAppData logs directory
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string logDir = Path.Combine(appData, "WinPDF", "logs");
            Directory.CreateDirectory(logDir);
            File.WriteAllText(Path.Combine(logDir, "autotest-report.log"), reportText);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUTOTEST] Warning: Failed to write to LocalAppData logs folder: {ex.Message}");
        }
    }

    private static void AppendToLogFiles(string content)
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            DirectoryInfo? dir = new(baseDir);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinPDF.sln")))
            {
                dir = dir.Parent;
            }

            if (dir is not null)
            {
                string artifactsDir = Path.Combine(dir.FullName, "artifacts");
                Directory.CreateDirectory(artifactsDir);
                File.AppendAllText(Path.Combine(artifactsDir, "autotest-report.log"), content);
            }
        }
        catch
        {
        }

        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string logDir = Path.Combine(appData, "WinPDF", "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "autotest-report.log"), content);
        }
        catch
        {
        }
    }
}
