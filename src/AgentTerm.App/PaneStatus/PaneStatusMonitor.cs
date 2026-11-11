using AgentTerm.Terminal;

namespace AgentTerm_App.PaneStatus;

/// <summary>One pane owns one query slot. Call on its UI thread; completions return to that context.</summary>
internal sealed class PaneStatusMonitor : IDisposable
{
    private readonly Func<ShellDirectory, CancellationToken, Task<GitResult>> _query;
    private CancellationTokenSource? _request;
    private bool _active;
    private bool _disposed;
    private DateTimeOffset _nextQuery;
    internal ShellDirectory? Directory { get; private set; }
    internal GitResult Result { get; private set; } = new(GitQueryState.NoRepository);
    internal event Action? Changed;

    internal PaneStatusMonitor(Func<ShellDirectory, CancellationToken, Task<GitResult>>? query = null)
        => _query = query ?? new GitStatusProvider().QueryAsync;

    internal void Refresh(ShellDirectory? directory, bool active, DateTimeOffset now)
    {
        if (_disposed) return;
        if (Directory != directory)
        {
            _request?.Cancel();
            Directory = directory;
            Result = new(GitQueryState.NoRepository);
            _nextQuery = default;
            Changed?.Invoke();
        }
        if (_active != active)
        {
            _active = active;
            if (!active) _request?.Cancel();
            else _nextQuery = default;
        }
        if (!active || directory is null || _request is not null || now < _nextQuery) return;
        _request = new CancellationTokenSource();
        _ = QueryAsync(directory, _request, now);
    }

    private async Task QueryAsync(ShellDirectory directory, CancellationTokenSource request, DateTimeOffset now)
    {
        try
        {
            var result = await _query(directory, request.Token);
            if (request.IsCancellationRequested || _disposed) return;
            Result = result;
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            _request = null;
            _nextQuery = request.IsCancellationRequested ? default : now + TimeSpan.FromSeconds(5);
            request.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _request?.Cancel();
        Changed = null;
    }
}
