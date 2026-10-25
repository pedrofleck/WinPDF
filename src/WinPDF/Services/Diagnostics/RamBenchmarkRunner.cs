using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinPDF.Models;
using WinPDF.Rendering;
using WinPDF.ViewModels;

namespace WinPDF.Services.Diagnostics;

/// <summary>
/// Detailed memory and performance metrics snapshot for benchmark reporting.
/// </summary>
public sealed record RamBenchmarkSnapshot
{
    /// <summary>Phase name of the benchmark.</summary>
    public required string Phase { get; init; }

    /// <summary>Private working set in megabytes (Task Manager metric).</summary>
    public double PrivateWorkingSetMb { get; init; }

    /// <summary>Private committed bytes in megabytes.</summary>
    public double PrivateCommitMb { get; init; }

    /// <summary>Managed heap size in megabytes.</summary>
    public double ManagedHeapMb { get; init; }

    /// <summary>Large Object Heap size in megabytes.</summary>
    public double LohSizeMb { get; init; }

    /// <summary>Total process CPU time consumed up to this snapshot in milliseconds.</summary>
    public double CpuTimeMs { get; init; }

    /// <summary>Total PDFium render jobs executed so far.</summary>
    public long TotalPdfiumRenders { get; init; }

    /// <summary>Additional notes or sub-metrics.</summary>
    public string? Details { get; init; }
}

/// <summary>
/// Complete benchmark report serialized to JSON.
/// </summary>
public sealed record RamBenchmarkReport
{
    /// <summary>Report generation timestamp.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>PDF file used for the benchmark.</summary>
    public string? DocumentPath { get; init; }

    /// <summary>Operating system and runtime description.</summary>
    public string EnvironmentDescription { get; init; } = $".NET {Environment.Version} | {RuntimeInformation.OSDescription}";

    /// <summary>Snapshots captured across benchmark phases.</summary>
    public List<RamBenchmarkSnapshot> Snapshots { get; init; } = new();

    /// <summary>Total elapsed time of benchmark run in milliseconds.</summary>
    public double TotalElapsedMs { get; init; }
}

/// <summary>
/// Fast automated RAM and performance benchmark runner activated via <c>--ram-benchmark</c>.
/// Executes a deterministically timed sequence covering idle, scroll, zoom, thumbnails, and modes.
/// </summary>
public static partial class RamBenchmarkRunner
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb;
        public uint PageFaultCount;
        public nint PeakWorkingSetSize;
        public nint WorkingSetSize;
        public nint QuotaPeakPagedPoolUsage;
        public nint QuotaPagedPoolUsage;
        public nint QuotaPeakNonPagedPoolUsage;
        public nint QuotaNonPagedPoolUsage;
        public nint PagefileUsage;
        public nint PeakPagefileUsage;
        public nint PrivateUsage;
        public nint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(
        nint hProcess,
        out PROCESS_MEMORY_COUNTERS_EX2 counters,
        uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    internal static RamBenchmarkSnapshot CaptureSnapshot(string phase, string? details = null)
    {
        double pwsMb = 0.0;
        double commitMb = 0.0;
        nint hProcess = GetCurrentProcess();
        uint structSize = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>();
        if (GetProcessMemoryInfo(hProcess, out PROCESS_MEMORY_COUNTERS_EX2 counters, structSize))
        {
            pwsMb = (double)counters.PrivateWorkingSetSize / (1024.0 * 1024.0);
            commitMb = (double)counters.PrivateUsage / (1024.0 * 1024.0);
        }

        using Process proc = Process.GetCurrentProcess();
        if (pwsMb <= 0.0)
        {
            pwsMb = proc.WorkingSet64 / (1024.0 * 1024.0);
        }
        if (commitMb <= 0.0)
        {
            commitMb = proc.PrivateMemorySize64 / (1024.0 * 1024.0);
        }

        double managedMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        var gcInfo = GC.GetGCMemoryInfo();
        double lohMb = 0.0;
        if (gcInfo.GenerationInfo.Length > 3)
        {
            lohMb = gcInfo.GenerationInfo[3].SizeAfterBytes / (1024.0 * 1024.0);
        }

        double cpuMs = proc.TotalProcessorTime.TotalMilliseconds;
        long totalRenders = TileCache.TotalRenderJobsExecuted;

        return new RamBenchmarkSnapshot
        {
            Phase = phase,
            PrivateWorkingSetMb = Math.Round(pwsMb, 2),
            PrivateCommitMb = Math.Round(commitMb, 2),
            ManagedHeapMb = Math.Round(managedMb, 2),
            LohSizeMb = Math.Round(lohMb, 2),
            CpuTimeMs = Math.Round(cpuMs, 1),
            TotalPdfiumRenders = totalRenders,
            Details = details,
        };
    }

    /// <summary>
    /// Executes the fast automated RAM benchmark sequence on the provided window.
    /// </summary>
    /// <param name="window">The active main window.</param>
    /// <param name="customPdfPath">Optional PDF file path override.</param>
    public static void Run(MainWindow window, string? customPdfPath)
    {
        _ = Task.Run(async () =>
        {
            Stopwatch sw = Stopwatch.StartNew();
            RamBenchmarkReport report = new();

            try
            {
                // Resolve PDF path
                string resolvedPath = customPdfPath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                {
                    string baseDir = AppContext.BaseDirectory;
                    DirectoryInfo? dir = new(baseDir);
                    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinPDF.sln")))
                    {
                        dir = dir.Parent;
                    }

                    if (dir is not null)
                    {
                        string testDoc = Path.Combine(dir.FullName, "test_doc.pdf");
                        string sampleDoc = Path.Combine(dir.FullName, "sample.pdf");
                        resolvedPath = File.Exists(testDoc) ? testDoc : (File.Exists(sampleDoc) ? sampleDoc : string.Empty);
                    }
                }

                if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                {
                    resolvedPath = TestPdfGenerator.GenerateDefaultTestPdf(forceRecreate: true);
                }

                report = report with { DocumentPath = Path.GetFileName(resolvedPath) };

                // Phase 0: Cold Start (App Shell Ready)
                await Task.Delay(400);
                report.Snapshots.Add(CaptureSnapshot("Phase 0: Cold Start"));

                // Phase 1: Open Document (100% Zoom Idle)
                await window.DispatcherQueue.EnqueueAsync(async () =>
                {
                    await window.ViewModel.OpenFilePathAsync(resolvedPath);
                });
                await Task.Delay(2500); // Allow initial viewport Level 0 renders to complete
                report.Snapshots.Add(CaptureSnapshot("Phase 1: Open Document (100% Zoom Idle)"));

                // Phase 2: Rapid Scroll to Middle/End and Return to Top
                await window.DispatcherQueue.EnqueueAsync(async () =>
                {
                    if (window.ViewModel.CurrentDocument is { } doc && doc.PageCount > 1)
                    {
                        int mid = Math.Max(1, doc.PageCount / 2);
                        doc.GoToPage(mid);
                        await Task.Delay(1200);
                        doc.GoToPage(doc.PageCount - 1);
                        await Task.Delay(1200);
                        doc.GoToPage(0);
                    }
                });
                await Task.Delay(1800);
                report.Snapshots.Add(CaptureSnapshot("Phase 2: Scroll Mid/End & Return Top"));

                // Phase 3: Zoom 300% & Return to 100%
                await window.DispatcherQueue.EnqueueAsync(async () =>
                {
                    window.ViewModel.SetZoom(3.0);
                });
                await Task.Delay(1600);
                report.Snapshots.Add(CaptureSnapshot("Phase 3A: Zoom 300% Peak"));

                await window.DispatcherQueue.EnqueueAsync(async () =>
                {
                    window.ViewModel.SetZoom(1.0);
                });
                await Task.Delay(1200);
                report.Snapshots.Add(CaptureSnapshot("Phase 3B: Return to 100% Zoom"));

                // Phase 4: Open Sidebar Thumbnails & Close
                await window.DispatcherQueue.EnqueueAsync(async () =>
                {
                    window.ViewModel.SidebarTab = SidebarTab.Thumbnails;
                    window.ViewModel.IsSidebarOpen = true;
                });
                await Task.Delay(1800); // Allow sidebar thumbnails to realize
                await window.DispatcherQueue.EnqueueAsync(() =>
                {
                    window.ViewModel.IsSidebarOpen = false;
                });
                await Task.Delay(1000);
                report.Snapshots.Add(CaptureSnapshot("Phase 4: Open & Close Thumbnails Sidebar"));

                // Phase 5: Alternate Paged / Continuous Layout
                await window.DispatcherQueue.EnqueueAsync(async () =>
                {
                    if (window.ViewModel.CurrentDocument is { } doc)
                    {
                        doc.IsContinuous = false;
                        await Task.Delay(800);
                        doc.IsContinuous = true;
                    }
                });
                await Task.Delay(1000);
                report.Snapshots.Add(CaptureSnapshot("Phase 5: Alternate Continuous/Paged"));

                // Phase 6: Close Document
                await window.DispatcherQueue.EnqueueAsync(() =>
                {
                    window.ViewModel.CloseDocumentCommand.Execute(null);
                });
                await Task.Delay(1000);
                report.Snapshots.Add(CaptureSnapshot("Phase 6: Close Document"));
            }
            catch (Exception ex)
            {
                report.Snapshots.Add(CaptureSnapshot("Benchmark Error", ex.Message));
            }
            finally
            {
                sw.Stop();
                report = report with { TotalElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1) };
                SaveReport(report);
                await Task.Delay(200);
                Environment.Exit(0);
            }
        });
    }

    private static void SaveReport(RamBenchmarkReport report)
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            DirectoryInfo? dir = new(baseDir);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinPDF.sln")))
            {
                dir = dir.Parent;
            }

            string artifactsDir = dir is not null
                ? Path.Combine(dir.FullName, "artifacts")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPDF", "artifacts");

            Directory.CreateDirectory(artifactsDir);
            string jsonPath = Path.Combine(artifactsDir, "ram-benchmark.json");
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json);
            Console.WriteLine($"[RAM BENCHMARK] Report successfully written to: {jsonPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RAM BENCHMARK] Failed to save report: {ex.Message}");
        }
    }

    private static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue queue, Action action)
    {
        TaskCompletionSource tcs = new();
        if (!queue.TryEnqueue(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
        {
            tcs.SetResult();
        }
        return tcs.Task;
    }

    private static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue queue, Func<Task> asyncAction)
    {
        TaskCompletionSource tcs = new();
        if (!queue.TryEnqueue(async () =>
        {
            try { await asyncAction(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
        {
            tcs.SetResult();
        }
        return tcs.Task;
    }
}
