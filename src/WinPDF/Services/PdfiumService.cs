using System.Threading.Channels;
using WinPDF.Interop.Pdfium;
using WinPDF.Interop.Printing;
using WinPDF.Models;

namespace WinPDF.Services;

/// <summary>
/// PDFium-backed <see cref="IPdfService"/>.
/// </summary>
/// <remarks>
/// <para>
/// PDFium is not thread-safe, so every <c>FPDF_*</c> call runs on <b>one dedicated thread</b>. Callers enqueue
/// jobs through dual priority channels: <c>_highPriorityChannel</c> (document loading, outline, metadata and
/// main visible renders) and <c>_lowPriorityChannel</c> (background sidebar thumbnails).
/// </para>
/// <para>
/// <b>Zero idle cost:</b> when no job is pending the thread blocks on <see cref="SemaphoreSlim.Wait(CancellationToken)"/>
/// (a kernel wait): no polling, timers, spin-waits or sleeps. The thread and the PDFium library are created lazily
/// on the first job.
/// </para>
/// </remarks>
public sealed class PdfiumService : IPdfService, IPdfiumBitmapOwner
{
    private const int WorkerStackSizeBytes = 8 * 1024 * 1024;

    private readonly Channel<IPdfiumJob> _highPriorityChannel = Channel.CreateUnbounded<IPdfiumJob>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });

    private readonly Channel<IPdfiumJob> _lowPriorityChannel = Channel.CreateUnbounded<IPdfiumJob>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });

    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly object _startGate = new();

    private Thread? _thread;
    private long _sequence;
    private volatile bool _disposed;
    private volatile Exception? _fatalError;

    /// <inheritdoc/>
    public Task<PdfDocumentInfo> OpenDocumentAsync(string filePath, string? password = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        LoadDocumentJob job = new(filePath, password, cancellationToken);
        return Enqueue<PdfDocumentInfo>(job, highPriority: true);
    }

    /// <inheritdoc/>
    public Task CloseDocumentAsync(long documentId, CancellationToken cancellationToken = default)
        => Enqueue<bool>(
            new PdfiumJob<bool>(
                PdfJobPriority.Critical,
                (engine, _) =>
                {
                    engine.CloseDocument(documentId);
                    return true;
                },
                cancellationToken),
            highPriority: true);

    /// <inheritdoc/>
    public Task<IReadOnlyList<OutlineItem>> GetOutlineAsync(long documentId, CancellationToken cancellationToken = default)
        => Enqueue<IReadOnlyList<OutlineItem>>(
            new PdfiumJob<IReadOnlyList<OutlineItem>>(
                PdfJobPriority.Critical,
                (engine, token) => engine.ReadOutline(documentId, token),
                cancellationToken),
            highPriority: true);

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> GetAttachmentNamesAsync(long documentId, CancellationToken cancellationToken = default)
        => Enqueue<IReadOnlyList<string>>(
            new PdfiumJob<IReadOnlyList<string>>(
                PdfJobPriority.Critical,
                (engine, token) => engine.ReadAttachmentNames(documentId, token),
                cancellationToken),
            highPriority: true);

    /// <inheritdoc/>
    public Task<PdfMetadata> GetDocumentMetadataAsync(long documentId, CancellationToken cancellationToken = default)
        => Enqueue<PdfMetadata>(
            new PdfiumJob<PdfMetadata>(
                PdfJobPriority.Critical,
                (engine, token) => engine.ReadDocumentMetadata(documentId, token),
                cancellationToken),
            highPriority: true);

    /// <inheritdoc/>
    public Task SaveDocumentAsync(
        long documentId,
        string targetFilePath,
        PdfSaveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);
        return Enqueue<bool>(
            new PdfiumJob<bool>(
                PdfJobPriority.Critical,
                (engine, token) =>
                {
                    engine.SaveDocument(documentId, targetFilePath, options ?? PdfSaveOptions.Default, token);
                    return true;
                },
                cancellationToken),
            highPriority: true);
    }

    /// <inheritdoc/>
    public Task<bool> PrintDocumentAsync(
        long documentId,
        PdfPrintJobOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Enqueue<bool>(
            new PrintDocumentJob(documentId, options, cancellationToken),
            highPriority: true);
    }

    /// <inheritdoc/>
    public Task<PdfBitmapLease> RenderPageAsync(
        long documentId,
        int pageIndex,
        int widthPx,
        int heightPx,
        bool highPriority = true,
        CancellationToken cancellationToken = default)
    {
        PdfRenderRequest request = PdfRenderRequest.ForThumbnail(documentId, pageIndex, widthPx, heightPx);
        RenderPageJob job = new(request, this, highPriority, cancellationToken);
        return Enqueue<PdfBitmapLease>(job, highPriority);
    }

    /// <inheritdoc/>
    public Task<PdfBitmapLease> RenderRegionAsync(
        long documentId,
        int pageIndex,
        int startXPx,
        int startYPx,
        int widthPx,
        int heightPx,
        int totalWidthPx,
        int totalHeightPx,
        bool highPriority = true,
        CancellationToken cancellationToken = default)
    {
        PdfRenderRequest request = new(
            documentId,
            pageIndex,
            widthPx,
            heightPx,
            totalWidthPx,
            totalHeightPx,
            -startXPx,
            -startYPx,
            0,
            PdfRenderFlags.Annotations);
        RenderRegionJob job = new(request, this, highPriority, cancellationToken);
        return Enqueue<PdfBitmapLease>(job, highPriority);
    }

    /// <inheritdoc/>
    public Task<PdfBitmapLease> RenderAsync(PdfRenderRequest request, PdfJobPriority priority, CancellationToken cancellationToken = default)
    {
        bool highPriority = priority != PdfJobPriority.Thumbnail;
        RenderPageJob job = new(request, this, highPriority, cancellationToken);
        return Enqueue<PdfBitmapLease>(job, highPriority);
    }

    /// <inheritdoc/>
    void IPdfiumBitmapOwner.ReleaseBitmap(PdfiumBitmapHandle bitmap)
    {
        BitmapReleaseJob job = new(bitmap);
        if (_disposed || !_highPriorityChannel.Writer.TryWrite(job))
        {
            bitmap.Dispose();
        }
        else
        {
            _signal.Release();
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
        _highPriorityChannel.Writer.TryComplete();
        _lowPriorityChannel.Writer.TryComplete();
        _shutdownCts.Cancel();

        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }

        Thread? thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }

        _shutdownCts.Dispose();
        _signal.Dispose();
    }

    private Task<T> Enqueue<T>(IPdfiumJob job, bool highPriority)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureStarted();

        job.Sequence = Interlocked.Increment(ref _sequence);

        Channel<IPdfiumJob> targetChannel = highPriority ? _highPriorityChannel : _lowPriorityChannel;

        if (!targetChannel.Writer.TryWrite(job))
        {
            Exception reason = _fatalError ?? new ObjectDisposedException(nameof(PdfiumService));
            job.Abort(reason);
        }
        else
        {
            _signal.Release();
        }

        if (job is PdfiumJob<T> typedJob)
        {
            return typedJob.Task;
        }

        throw new InvalidOperationException("Unexpected job type.");
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
        WorkerLoopAsync(_shutdownCts.Token);
    }

    private void WorkerLoopAsync(CancellationToken cancellationToken)
    {
        PdfiumEngine? engine = null;
        try
        {
            engine = new PdfiumEngine();
        }
        catch (Exception ex)
        {
            _fatalError = ex;
            _highPriorityChannel.Writer.TryComplete();
            _lowPriorityChannel.Writer.TryComplete();
        }

        try
        {
            while (!_disposed && !cancellationToken.IsCancellationRequested)
            {
                // Always drain ALL high priority jobs first
                while (_highPriorityChannel.Reader.TryRead(out IPdfiumJob? highJob))
                {
                    ExecuteJob(highJob, engine);
                }

                // If high priority is clear, drain ONE low priority job, then loop back to check high priority again
                if (_lowPriorityChannel.Reader.TryRead(out IPdfiumJob? lowJob))
                {
                    ExecuteJob(lowJob, engine);
                    continue;
                }

                // Drain any extra signal permits since we already drained both channels
                while (_signal.CurrentCount > 0 && _signal.Wait(0)) { }

                // Check again in case a job arrived just before draining the signal
                if (_highPriorityChannel.Reader.TryRead(out IPdfiumJob? arrivedHigh))
                {
                    ExecuteJob(arrivedHigh, engine);
                    continue;
                }

                if (_lowPriorityChannel.Reader.TryRead(out IPdfiumJob? arrivedLow))
                {
                    ExecuteJob(arrivedLow, engine);
                    continue;
                }

                // Idle: wait in the kernel until signaled with 0.0% CPU
                try
                {
                    _signal.Wait(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            // Cleanly cancel all remaining pending jobs in both channels so no caller Task hangs
            while (_highPriorityChannel.Reader.TryRead(out IPdfiumJob? job))
            {
                job.Cancel();
            }

            while (_lowPriorityChannel.Reader.TryRead(out IPdfiumJob? job))
            {
                job.Cancel();
            }

            engine?.Dispose();
        }
    }

    private void ExecuteJob(IPdfiumJob job, PdfiumEngine? engine)
    {
        if (engine is null)
        {
            job.Abort(_fatalError);
        }
        else
        {
            job.Execute(engine);
        }
    }

    private sealed class PrintDocumentJob : PdfiumJob<bool>
    {
        private readonly PdfPrintJobOptions _options;
        private int _dcReleased;

        internal PrintDocumentJob(long documentId, PdfPrintJobOptions options, CancellationToken cancellationToken)
            : base(
                PdfJobPriority.Critical,
                (engine, token) => engine.PrintDocument(documentId, options, token),
                cancellationToken)
        {
            _options = options;
        }

        public override void Execute(PdfiumEngine engine)
        {
            Interlocked.Exchange(ref _dcReleased, 1);
            base.Execute(engine);
        }

        public override void Cancel()
        {
            ReleaseDcIfNeeded();
            base.Cancel();
        }

        public override void Abort(Exception? reason)
        {
            ReleaseDcIfNeeded();
            base.Abort(reason);
        }

        private void ReleaseDcIfNeeded()
        {
            if (Interlocked.Exchange(ref _dcReleased, 1) == 0 && _options.PrinterDc != nint.Zero)
            {
                _ = Win32PrintInterop.DeleteDC(_options.PrinterDc);
            }
        }
    }

    private sealed class BitmapReleaseJob : PdfiumJob
    {
        private readonly PdfiumBitmapHandle _bitmap;

        internal BitmapReleaseJob(PdfiumBitmapHandle bitmap)
            : base(PdfJobPriority.Critical)
        {
            _bitmap = bitmap;
        }

        public override void Execute(PdfiumEngine engine) => _bitmap.Dispose();

        public override void Cancel() => _bitmap.Dispose();

        public override void Abort(Exception? reason) => _bitmap.Dispose();
    }
}
