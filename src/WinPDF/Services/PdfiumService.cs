using System.Threading.Channels;
using WinPDF.Interop.Pdfium;
using WinPDF.Models;

namespace WinPDF.Services;

/// <summary>
/// PDFium-backed <see cref="IPdfService"/>.
/// </summary>
/// <remarks>
/// <para>
/// PDFium is not thread-safe, so every <c>FPDF_*</c> call runs on <b>one dedicated thread</b>. Callers enqueue
/// jobs through an unbounded <see cref="Channel{T}"/>; the thread drains it into a priority queue (critical
/// metadata first, then visible renders, prefetch, thumbnails; newest request first among renders so a fast
/// scroll serves what is on screen now) and executes one job at a time.
/// </para>
/// <para>
/// <b>Zero idle cost:</b> when no job is pending the thread blocks on
/// <see cref="ChannelReader{T}.WaitToReadAsync(CancellationToken)"/> (a kernel wait): no polling, timers, spin-waits
/// or sleeps. The thread and the PDFium library are created lazily on the first job.
/// </para>
/// </remarks>
public sealed class PdfiumService : IPdfService, IPdfiumBitmapOwner
{
    private const int WorkerStackSizeBytes = 8 * 1024 * 1024;

    private readonly Channel<PdfiumJob> _channel = Channel.CreateUnbounded<PdfiumJob>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });

    private readonly object _startGate = new();
    private Thread? _thread;
    private long _sequence;
    private volatile bool _disposed;
    private volatile Exception? _fatalError;

    /// <inheritdoc/>
    public Task<PdfDocumentInfo> OpenDocumentAsync(string filePath, string? password = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return Enqueue(PdfJobPriority.Critical, cancellationToken, (engine, token) => engine.OpenDocument(filePath, password, token));
    }

    /// <inheritdoc/>
    public Task CloseDocumentAsync(long documentId, CancellationToken cancellationToken = default)
        => Enqueue(
            PdfJobPriority.Critical,
            cancellationToken,
            (engine, _) =>
            {
                engine.CloseDocument(documentId);
                return true;
            });

    /// <inheritdoc/>
    public Task<IReadOnlyList<OutlineItem>> GetOutlineAsync(long documentId, CancellationToken cancellationToken = default)
        => Enqueue(PdfJobPriority.Critical, cancellationToken, (engine, token) => engine.ReadOutline(documentId, token));

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> GetAttachmentNamesAsync(long documentId, CancellationToken cancellationToken = default)
        => Enqueue(PdfJobPriority.Critical, cancellationToken, (engine, token) => engine.ReadAttachmentNames(documentId, token));

    /// <inheritdoc/>
    public Task<PdfBitmapLease> RenderAsync(PdfRenderRequest request, PdfJobPriority priority, CancellationToken cancellationToken = default)
        => Enqueue(priority, cancellationToken, (engine, token) => engine.Render(request, this, token));

    /// <inheritdoc/>
    void IPdfiumBitmapOwner.ReleaseBitmap(PdfiumBitmapHandle bitmap)
    {
        // Native memory goes back to PDFium on its own thread (all FPDF_* calls live there). Fire-and-forget,
        // no Task is allocated; if the worker is gone nothing can race with this call, so destroy inline.
        PdfiumJob job = new BitmapReleaseJob(bitmap);
        if (_disposed || !_channel.Writer.TryWrite(job))
        {
            bitmap.Dispose();
        }
    }

    /// <summary>
    /// Stops accepting jobs, lets the worker finish its current job, closes every document and destroys PDFium.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();

        Thread? thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private Task<T> Enqueue<T>(PdfJobPriority priority, CancellationToken cancellationToken, Func<PdfiumEngine, CancellationToken, T> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureStarted();

        PdfiumJob<T> job = new(priority, work, cancellationToken);
        job.Sequence = Interlocked.Increment(ref _sequence);

        if (!_channel.Writer.TryWrite(job))
        {
            Exception reason = _fatalError ?? new ObjectDisposedException(nameof(PdfiumService));
            job.Abort(reason);
        }

        return job.Task;
    }

    private void EnsureStarted()
    {
        if (_thread is not null)
        {
            return;
        }

        lock (_startGate)
        {
            if (_thread is not null)
            {
                return;
            }

            Thread thread = new(WorkerMain, WorkerStackSizeBytes)
            {
                IsBackground = true,
                Name = "WinPDF.Pdfium",
            };

            _thread = thread;
            thread.Start();
        }
    }

    private void WorkerMain()
    {
        PdfiumEngine? engine = null;
        try
        {
            engine = new PdfiumEngine();
        }
        catch (Exception ex)
        {
            // e.g. pdfium.dll missing: fail this and every later job instead of hanging callers.
            _fatalError = ex;
            _channel.Writer.TryComplete();
        }

        ChannelReader<PdfiumJob> reader = _channel.Reader;
        PriorityQueue<PdfiumJob, (int Priority, long Order)> queue = new();

        try
        {
            while (true)
            {
                // Pick up everything that arrived while the previous job was running.
                while (reader.TryRead(out PdfiumJob? incoming))
                {
                    queue.Enqueue(incoming, GetOrderKey(incoming));
                }

                if (queue.TryDequeue(out PdfiumJob? job, out _))
                {
                    if (engine is null)
                    {
                        job.Abort(_fatalError);
                    }
                    else
                    {
                        job.Execute(engine);
                    }

                    continue;
                }

                // Idle: block in the kernel until a job arrives or the channel completes. No polling.
                if (!reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    break;
                }
            }
        }
        finally
        {
            while (queue.TryDequeue(out PdfiumJob? leftover, out _))
            {
                leftover.Abort(_fatalError);
            }

            engine?.Dispose();
        }
    }

    // Critical work is FIFO (open before queries). Render-class work is LIFO: the newest request is what the
    // user is looking at; older ones are usually cancelled and skipped without touching PDFium.
    private static (int Priority, long Order) GetOrderKey(PdfiumJob job)
        => (
            (int)job.Priority,
            job.Priority == PdfJobPriority.Critical ? job.Sequence : long.MaxValue - job.Sequence);

    private sealed class BitmapReleaseJob : PdfiumJob
    {
        private readonly PdfiumBitmapHandle _bitmap;

        internal BitmapReleaseJob(PdfiumBitmapHandle bitmap)
            : base(PdfJobPriority.Critical)
        {
            _bitmap = bitmap;
        }

        internal override void Execute(PdfiumEngine engine) => _bitmap.Dispose();

        internal override void Abort(Exception? reason) => _bitmap.Dispose();
    }
}
