namespace WinPDF.Services;

/// <summary>
/// A unit of work executed on the dedicated PDFium thread.
/// </summary>
internal interface IPdfiumJob
{
    /// <summary>Gets the scheduling priority.</summary>
    PdfJobPriority Priority { get; }

    /// <summary>Gets or sets the arrival sequence number.</summary>
    long Sequence { get; set; }

    /// <summary>Runs the job on the PDFium thread.</summary>
    /// <param name="engine">The engine owned by the PDFium thread.</param>
    void Execute(PdfiumEngine engine);

    /// <summary>Cancels the pending task.</summary>
    void Cancel();

    /// <summary>Completes the job without running it (service shutdown or failure).</summary>
    /// <param name="reason">Exception to fault the job with, or <see langword="null"/> to cancel it.</param>
    void Abort(Exception? reason);
}

/// <summary>
/// Base class for units of work executed on the dedicated PDFium thread.
/// </summary>
internal abstract class PdfiumJob : IPdfiumJob
{
    /// <summary>Initializes the job with its scheduling priority.</summary>
    /// <param name="priority">Scheduling priority.</param>
    protected PdfiumJob(PdfJobPriority priority)
    {
        Priority = priority;
    }

    /// <inheritdoc/>
    public PdfJobPriority Priority { get; }

    /// <inheritdoc/>
    public long Sequence { get; set; }

    /// <inheritdoc/>
    public abstract void Execute(PdfiumEngine engine);

    /// <inheritdoc/>
    public abstract void Cancel();

    /// <inheritdoc/>
    public abstract void Abort(Exception? reason);
}

/// <summary>
/// A <see cref="PdfiumJob"/> producing a result exposed through a <see cref="Task{TResult}"/>.
/// </summary>
/// <typeparam name="TResult">Result type.</typeparam>
internal class PdfiumJob<TResult> : PdfiumJob
{
    private readonly Func<PdfiumEngine, CancellationToken, TResult> _work;
    private readonly TaskCompletionSource<TResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationToken _cancellationToken;
    private CancellationTokenRegistration _registration;

    internal PdfiumJob(PdfJobPriority priority, Func<PdfiumEngine, CancellationToken, TResult> work, CancellationToken cancellationToken)
        : base(priority)
    {
        _work = work;
        _cancellationToken = cancellationToken;

        // Complete the Task as cancelled immediately, even while the job still sits in the queue.
        if (cancellationToken.CanBeCanceled)
        {
            _registration = cancellationToken.Register(
                static state => ((PdfiumJob<TResult>)state!).CancelFromToken(),
                this);
        }
    }

    /// <summary>Gets the task that completes with the job's outcome.</summary>
    internal Task<TResult> Task => _completion.Task;

    /// <inheritdoc/>
    public override void Execute(PdfiumEngine engine)
    {
        try
        {
            if (_cancellationToken.IsCancellationRequested || _completion.Task.IsCompleted)
            {
                _completion.TrySetCanceled();
                return;
            }

            TResult result = _work(engine, _cancellationToken);

            // The caller may have cancelled while the job was running; never leak a native resource.
            if (!_completion.TrySetResult(result) && result is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            _completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
        }
        finally
        {
            _registration.Dispose();
        }
    }

    /// <inheritdoc/>
    public override void Cancel()
    {
        _completion.TrySetCanceled();
        _registration.Dispose();
    }

    /// <inheritdoc/>
    public override void Abort(Exception? reason)
    {
        if (reason is null)
        {
            _completion.TrySetCanceled();
        }
        else
        {
            _completion.TrySetException(reason);
        }

        _registration.Dispose();
    }

    private void CancelFromToken() => _completion.TrySetCanceled(_cancellationToken);
}

/// <summary>
/// Job for loading a PDF document.
/// </summary>
internal sealed class LoadDocumentJob : PdfiumJob<PdfDocumentInfo>
{
    internal LoadDocumentJob(string filePath, string? password, CancellationToken cancellationToken)
        : base(PdfJobPriority.Critical, (engine, token) => engine.OpenDocument(filePath, password, token), cancellationToken)
    {
    }
}

/// <summary>
/// Job for rendering a full PDF page.
/// </summary>
internal sealed class RenderPageJob : PdfiumJob<PdfBitmapLease>
{
    internal RenderPageJob(PdfRenderRequest request, IPdfiumBitmapOwner owner, bool highPriority, CancellationToken cancellationToken)
        : base(highPriority ? PdfJobPriority.Visible : PdfJobPriority.Thumbnail, (engine, token) => engine.Render(request, owner, token), cancellationToken)
    {
    }
}

/// <summary>
/// Job for rendering a sub-region (tile) of a PDF page.
/// </summary>
internal sealed class RenderRegionJob : PdfiumJob<PdfBitmapLease>
{
    internal RenderRegionJob(PdfRenderRequest request, IPdfiumBitmapOwner owner, bool highPriority, CancellationToken cancellationToken)
        : base(highPriority ? PdfJobPriority.Visible : PdfJobPriority.Thumbnail, (engine, token) => engine.Render(request, owner, token), cancellationToken)
    {
    }
}
