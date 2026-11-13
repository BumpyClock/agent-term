using System.Threading.Channels;

namespace AgentTerm.Terminal;

/// <summary>An owned frame and its next presentation deadline, captured on the session worker.</summary>
public sealed record TerminalFrameSnapshot(TerminalFrame Frame, TimeSpan? PendingRefreshDelay)
{
    public TerminalPointerMode PointerMode { get; init; }
    public ShellPromptState PromptState { get; init; }
}

public sealed partial class TerminalSession
{
    private readonly object _workerGate = new();
    private readonly Channel<Action> _operations = Channel.CreateBounded<Action>(new BoundedChannelOptions(1024)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private Task? _worker;
    private bool _workerClosed;
    private int _outstandingOperations;
    private long _operationVersion;
    private long _captureVersion;
    private Task<TerminalFrameSnapshot>? _pendingCapture;
    private TerminalFrame? _latestFrame;

    /// <summary>The most recently completed owned snapshot. Reading it never takes a terminal lock.</summary>
    public TerminalFrame? LatestFrame => Volatile.Read(ref _latestFrame);

    /// <summary>
    /// Executes a terminal operation in session order, away from the UI thread. Capture UI-owned values before
    /// submitting. Operations must not access UI objects, block on another dispatched operation, or dispose the session.
    /// A full queue rejects the operation explicitly; accepted operations are never silently discarded.
    /// Between output slices the parser gives queued work a bounded scheduling opportunity without waiting for it to finish.
    /// </summary>
    public Task DispatchAsync(Action<TerminalSession> operation) => DispatchAsync(session =>
    {
        operation(session);
        return true;
    });

    public Task<T> DispatchAsync<T>(Func<TerminalSession, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_workerGate)
        {
            _operationVersion++;
            return EnqueueOperation(operation);
        }
    }

    /// <summary>
    /// Coalesces capture requests until the queued capture completes. A command submitted between requests forces
    /// a new capture after that command, so resize, selection and input cannot receive an older pending frame.
    /// </summary>
    public Task<TerminalFrameSnapshot> CaptureFrameAsync()
    {
        lock (_workerGate)
        {
            if (!_workerClosed && _pendingCapture is { IsCompleted: false } && _captureVersion == _operationVersion)
                return _pendingCapture;
            _captureVersion = _operationVersion;
            return _pendingCapture = EnqueueOperation(session =>
            {
                var frame = session.ReadFrame();
                Volatile.Write(ref _latestFrame, frame);
                return new TerminalFrameSnapshot(frame, session.PendingFrameRefreshDelay)
                {
                    PointerMode = session.ReadPointerMode(),
                    PromptState = session.PromptState
                };
            });
        }
    }

    private void InvalidatePendingFrameCapture()
    {
        lock (_workerGate) _operationVersion++;
    }

    private Task<T> EnqueueOperation<T>(Func<TerminalSession, T> operation)
    {
        if (_workerClosed) return Task.FromException<T>(new ObjectDisposedException(nameof(TerminalSession)));
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_operations.Writer.TryWrite(() =>
        {
            try { result.SetResult(operation(this)); }
            catch (Exception error) { result.SetException(error); }
            finally
            {
                lock (_workerGate)
                {
                    _outstandingOperations--;
                    Monitor.PulseAll(_workerGate);
                }
            }
        })) return Task.FromException<T>(new InvalidOperationException("The terminal operation queue is full."));
        _outstandingOperations++;
        _worker ??= Task.Run(async () =>
        {
            await foreach (var action in _operations.Reader.ReadAllAsync().ConfigureAwait(false)) action();
        });
        return result.Task;
    }

    private void YieldOutputToWorker()
    {
        lock (_workerGate)
        {
            if (!_workerClosed && _outstandingOperations != 0) Monitor.Wait(_workerGate, 1);
        }
    }

    private Task StopWorkerAsync()
    {
        lock (_workerGate)
        {
            _workerClosed = true;
            Monitor.PulseAll(_workerGate);
            _operations.Writer.TryComplete();
            return _worker ?? Task.CompletedTask;
        }
    }
}
