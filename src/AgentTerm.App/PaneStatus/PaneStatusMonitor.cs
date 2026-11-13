using AgentTerm.Terminal;

namespace AgentTerm_App.PaneStatus;

/// <summary>One pane's view of its repository's shared Git status. Call on its UI thread; completions return to that context.</summary>
internal sealed class PaneStatusMonitor : IDisposable
{
    private readonly GitStatusHub _hub;
    private CancellationTokenSource? _request;
    private GitStatusHub.Entry? _entry;
    private GitStatusHub.Entry? _retired;
    private int _version;
    private DateTimeOffset _membershipChecked = DateTimeOffset.MinValue;
    private bool _disposed;
    internal ShellDirectory? Directory { get; private set; }
    internal GitResult Result { get; private set; } = new(GitQueryState.NoRepository);
    internal event Action? Changed;

    internal PaneStatusMonitor(GitStatusHub? hub = null) => _hub = hub ?? GitStatusHub.Shared;

    internal void Refresh(ShellDirectory? directory, bool active, DateTimeOffset now)
    {
        if (_disposed) return;
        if (Directory != directory)
        {
            _request?.Cancel();
            ReleaseRetired();
            _retired = _entry;
            _entry = null;
            _version = 0;
            _membershipChecked = DateTimeOffset.MinValue;
            if (directory is null) ReleaseRetired();
            Directory = directory;
            Result = new(GitQueryState.NoRepository);
            Changed?.Invoke();
        }
        if (!active || _hub.Paused)
        {
            _request?.Cancel();
            return;
        }
        if (directory is null) return;
        if (_request is not null)
        {
            // Stop waiting for an old shared query so membership can be checked even while it runs.
            if (_entry is not null && now >= _membershipChecked + GitStatusHub.UnwatchedInterval) _request.Cancel();
            return;
        }
        Task<GitResult>? running = null;
        if (_entry is { } entry)
        {
            Adopt();
            if (now < _membershipChecked + GitStatusHub.UnwatchedInterval && !_hub.NeedsRefresh(entry, now))
            {
                if ((running = _hub.Ensure(entry, now)) is null) return;
            }
        }
        _request = new CancellationTokenSource();
        _ = QueryAsync(directory, _request, now, running);
    }

    private async Task QueryAsync(ShellDirectory directory, CancellationTokenSource request, DateTimeOffset now, Task<GitResult>? running)
    {
        try
        {
            if (running is null)
            {
                var entry = await Task.Run(() => _hub.Acquire(directory));
                if (request.IsCancellationRequested || _disposed)
                {
                    _hub.Release(entry);
                    return;
                }
                var old = _entry;
                _entry = entry;
                if (old is not null) _hub.Release(old);
                if (!ReferenceEquals(old, entry))
                {
                    _version = 0;
                    if (Result.State != GitQueryState.NoRepository)
                    {
                        Result = new(GitQueryState.NoRepository);
                        Changed?.Invoke();
                    }
                }
                _membershipChecked = now;
                ReleaseRetired();
                Adopt();
                if ((running = _hub.Ensure(entry, now)) is null) return;
            }
            await running.WaitAsync(request.Token);
            if (!request.IsCancellationRequested && !_disposed) Adopt();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (_request == request) _request = null;
            request.Dispose();
        }
    }

    private void Adopt()
    {
        if (_entry is not { } entry || !_hub.TryRead(entry, ref _version, out var result) || result == Result) return;
        Result = result;
        Changed?.Invoke();
    }

    private void ReleaseRetired()
    {
        if (_retired is { } retired) _hub.Release(retired);
        _retired = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _request?.Cancel();
        ReleaseRetired();
        if (_entry is { } entry) _hub.Release(entry);
        _entry = null;
        Changed = null;
    }
}
