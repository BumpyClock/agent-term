using System.Threading.Channels;

namespace AgentTerm.Terminal;

public sealed class TerminalOperationQueueFullException() : InvalidOperationException("The terminal operation queue is full.");

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
    /// Output parsing pauses between slices from enqueue until the operation first holds the terminal.
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
                using (_gate.Enter())
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
        _gate.Reserve();
        if (!_operations.Writer.TryWrite(() => _gate.RunReserved(() =>
        {
            try { result.SetResult(operation(this)); }
            catch (Exception error) { result.SetException(error); }
        })))
        {
            _gate.Unreserve();
            return Task.FromException<T>(new TerminalOperationQueueFullException());
        }
        _worker ??= Task.Run(async () =>
        {
            await foreach (var action in _operations.Reader.ReadAllAsync().ConfigureAwait(false)) action();
        });
        return result.Task;
    }

    private Task StopWorkerAsync()
    {
        lock (_workerGate)
        {
            _workerClosed = true;
            _operations.Writer.TryComplete();
            return _worker ?? Task.CompletedTask;
        }
    }
}
