namespace WinPDF.Services;

/// <summary>
/// A unit of work executed on the dedicated PDFium thread.
/// </summary>
internal abstract class PdfiumJob
{
    /// <summary>Initializes the job with its scheduling priority.</summary>
    /// <param name="priority">Scheduling priority.</param>
    protected PdfiumJob(PdfJobPriority priority)
    {
        Priority = priority;
    }

    /// <summary>Gets the scheduling priority.</summary>
    internal PdfJobPriority Priority { get; }

    /// <summary>Gets or sets the arrival sequence number (assigned when enqueued).</summary>
    internal long Sequence { get; set; }

    /// <summary>Runs the job on the PDFium thread (skipped, not executed, if already cancelled).</summary>
    /// <param name="engine">The engine owned by the PDFium thread.</param>
    internal abstract void Execute(PdfiumEngine engine);

    /// <summary>Completes the job without running it (service shutdown or failure).</summary>
    /// <param name="reason">Exception to fault the job with, or <see langword="null"/> to cancel it.</param>
    internal abstract void Abort(Exception? reason);
}

/// <summary>
/// A <see cref="PdfiumJob"/> producing a result exposed through a <see cref="Task{TResult}"/>.
/// </summary>
/// <typeparam name="TResult">Result type.</typeparam>
internal sealed class PdfiumJob<TResult> : PdfiumJob
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

    internal override void Execute(PdfiumEngine engine)
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

    internal override void Abort(Exception? reason)
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
