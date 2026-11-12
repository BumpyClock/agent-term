using System.Text;
using AgentTerm.Agents;

namespace AgentTerm.Terminal;

/// <summary>Owns one shell and serializes access to its Ghostty terminal state.</summary>
public sealed class TerminalSession : IAsyncDisposable, IHostTerminal
{
    private readonly object _gate = new();
    private readonly GhosttyTerminal _terminal;
    private readonly ConPtyConnection _connection;
    private readonly Task<int> _completion;
    private readonly ShellProfile _profile;
    private readonly string _promptToken = Guid.NewGuid().ToString("N");
    private readonly ShellPromptProtocol _prompt;
    private readonly ProgramStatusProtocol _programStatus = new();
    private readonly OscScanner _osc = new();
    private sealed class OwnedCommand
    {
        public readonly string Id = "a:" + Guid.NewGuid().ToString("N")[..8];
        public readonly CommandOutputCapture Capture;
        public OwnedCommand() { Capture = new(Id); }
        public readonly TaskCompletionSource<TerminalExecutionResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Interrupted;
        public bool InteractiveInput;
    }
    private OwnedCommand? _owned;
    public bool AgentOwnsTerminal { get { lock (_gate) return _owned is not null; } }
    public void AllowAgentInteractiveInput() { lock (_gate) { if (_owned is not null) _owned.InteractiveInput = true; } }
    private bool _started;
    private Task? _stopTask;
    private Task? _disposeTask;

    public TerminalSession(ushort columns = 80, ushort rows = 24, ShellProfile? profile = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ConPTY requires Windows.");
        _profile = profile ?? ShellProfile.Default;
        _prompt = new(_promptToken);
        _osc.Sequence += _prompt.Accept;
        _osc.Sequence += _programStatus.Accept;
        _osc.Reset += _programStatus.Reset;
        _prompt.PromptStarted += _programStatus.EndCommand;
        _osc.OutputByte += b => _owned?.Capture.Feed(b);
        _prompt.Marker += message => _owned?.Capture.Marker(message);
        _connection = new ConPtyConnection(columns, rows);
        _programStatus.SupportRequested += () =>
        {
            if (_disposeTask is null) _connection.SendReply(ProgramStatusProtocol.SupportReply);
        };
        _terminal = new GhosttyTerminal(columns, rows);
        _connection.OutputReceived += ProcessOutput;
        _completion = CompleteAsync();
    }

    public string ShellName => _profile.Name;
    public ShellProfile Profile => _profile;
    public Task<int> Completion => _completion;
    public int ProcessId => _connection.ProcessId;

    public IReadOnlyList<ProgramStatus> ReadProgramStatuses()
    {
        lock (_gate) return _programStatus.ReadStatuses();
    }

    private async Task<int> CompleteAsync()
    {
        try { return await _connection.Completion.ConfigureAwait(false); }
        finally { lock (_gate) _programStatus.EndCommand(); }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_started) throw new InvalidOperationException("This session has already started.");
            _connection.Start(_profile.Executable, ShellIntegration.Arguments(_profile, _promptToken), _profile.StartingDirectory);
            _started = true;
        }
    }

    public void Send(string text)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_owned is { InteractiveInput: false }) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length != 0) PrepareInput();
            _connection.Send(bytes);
        }
    }

    public ShellPromptState PromptState { get { lock (_gate) return Completion.IsCompleted ? ShellPromptState.Exited : _prompt.State; } }
    public IReadOnlySet<string> KnownCommands { get { lock (_gate) return _prompt.Commands; } }
    public bool? LastCommandSucceeded { get { lock (_gate) return _prompt.LastCommandSucceeded; } }
    public void EnableManagedPrompt()
    {
        lock (_gate)
        {
            if (_prompt.State != ShellPromptState.Raw) throw new InvalidOperationException("Shell integration is not ready.");
            _prompt.State = ShellPromptState.Executing;
            Send("$global:__AgentTermManaged=$true\r");
        }
    }
    public void RestoreRawPrompt()
    {
        lock (_gate)
        {
            if (_owned is not null || _prompt.State != ShellPromptState.Ready) return;
            _prompt.State = ShellPromptState.Executing;
            Send("$global:__AgentTermManaged=$false\r");
        }
    }
    public void SubmitCommand(string command) => SubmitCommand(command, Guid.NewGuid().ToString("N"));
    private void SubmitCommand(string command, string id)
    {
        lock (_gate)
        {
            if (_owned is not null) throw new InvalidOperationException("Agent using terminal.");
            if (_prompt.State != ShellPromptState.Ready) throw new InvalidOperationException("Wait for shell readiness or use terminal input.");
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(id + "\n" + command));
            _prompt.State = ShellPromptState.Executing;
            _connection.Send(Encoding.UTF8.GetBytes(encoded + "\u001b[24;5~"));
        }
    }

    /// <summary>
    /// Reserves two blank history rows at the managed prompt for an inline card, without echo or a command.
    /// Returns null unless the managed prompt is ready. The anchor marks the first reserved row.
    /// </summary>
    public TerminalAnchor? ReserveRows()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_owned is not null || _prompt.State != ShellPromptState.Ready) return null;
            _terminal.ScrollToBottom();
            var frame = _terminal.ReadFrame();
            if (frame.Viewport.IsAlternateScreen) return null;
            var anchor = _terminal.AnchorActiveRow(Math.Clamp(frame.Cursor.Row, 0, frame.Rows - 1));
            SubmitCommand("", "c:" + Guid.NewGuid().ToString("N")[..8]);
            return anchor;
        }
    }
    public long? ReadAnchor(TerminalAnchor anchor)
    {
        lock (_gate) return _disposeTask is null ? _terminal.ReadAnchor(anchor) : null;
    }
    public void ReleaseAnchor(TerminalAnchor anchor)
    {
        lock (_gate) { if (_disposeTask is null) _terminal.ReleaseAnchor(anchor); }
    }

    public async Task<TerminalExecutionResult> ExecuteAsync(TerminalExecutionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OwnedCommand owned;
        lock (_gate)
        {
            if (_disposeTask is not null || Completion.IsCompleted) return new(TerminalExecutionStatus.Exited);
            if (_owned is not null || _prompt.State == ShellPromptState.Executing) return new(TerminalExecutionStatus.Busy);
            if (_prompt.State != ShellPromptState.Ready) return new(TerminalExecutionStatus.Unsupported, Message: "Managed PowerShell must be ready.");
            owned = new();
            SubmitCommand(request.Command, owned.Id);
            _owned = owned;
        }
        using var cancel = cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                if (_owned != owned) return;
                owned.Interrupted = true;
                _connection.Send([3]);
            }
        });
        using var signalLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exited = Completion;
        var finished = await Task.WhenAny(owned.Completion.Task, exited, CancellationSignal(signalLifetime.Token)).ConfigureAwait(false);
        signalLifetime.Cancel();
        if (finished == owned.Completion.Task) return await owned.Completion.Task.ConfigureAwait(false);
        if (finished == exited)
        {
            lock (_gate) { if (_owned == owned) _owned = null; }
            return new(TerminalExecutionStatus.Exited, owned.Capture.Text);
        }
        try { return await owned.Completion.Task.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // Ownership deliberately remains held until the shell actually reports ready.
            return new(TerminalExecutionStatus.InterruptionFailed, Message: "PowerShell did not report readiness after Ctrl+C. Terminal remains agent-owned; use interactive input or close the pane.");
        }
    }
    private static async Task CancellationSignal(CancellationToken token)
    {
        try { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    public string ReadText()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            return _terminal.ReadText();
        }
    }

    public ShellDirectory? ReadWorkingDirectory()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            return ShellDirectory.FromReport(_terminal.ReadWorkingDirectory(), _profile);
        }
    }

    public TerminalFrame ReadFrame()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            return _terminal.ReadFrame();
        }
    }

    public void SendKey(uint virtualKey, TerminalModifiers modifiers, TerminalKeyAction action, string? text = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_owned is { InteractiveInput: false }) return;
            var bytes = _terminal.EncodeKey(virtualKey, modifiers, action, text);
            if (bytes.Length != 0 && action != TerminalKeyAction.Release &&
                virtualKey is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5)) PrepareInput();
            _connection.Send(bytes);
        }
    }

    public bool IsMouseTrackingEnabled
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
                return _terminal.IsMouseTrackingEnabled;
            }
        }
    }

    /// <summary>Returns true when application mouse tracking owns this event, including filtered events.</summary>
    public bool SendMouse(TerminalMouseEvent mouse, TerminalMouseGeometry geometry)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_owned is { InteractiveInput: false }) return true;
            if ((mouse.Modifiers & TerminalModifiers.Shift) != 0 || !_terminal.IsMouseTrackingEnabled) return false;
            var bytes = _terminal.EncodeMouse(mouse, geometry);
            // Mouse coordinates refer to the displayed viewport. Hover and drag must not jump it to the bottom.
            if (mouse.Action == TerminalMouseAction.Press && bytes.Length != 0) _terminal.ClearSelection();
            if (bytes.Length != 0) _connection.Send(bytes);
            return true;
        }
    }

    public void Paste(string text)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_owned is { InteractiveInput: false }) return;
            var bytes = _terminal.EncodePaste(text);
            if (bytes.Length != 0) PrepareInput();
            _connection.Send(bytes);
        }
    }

    public void SetFocus(bool focused)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _connection.SendReply(_terminal.EncodeFocus(focused));
        }
    }

    public void SetLightTheme(bool light) => MutateView(() => _terminal.SetLightTheme(light));

    public void Resize(ushort columns, ushort rows)
    {
        bool stopped;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _terminal.Resize(columns, rows);
            stopped = _stopTask is not null;
        }
        // ConPTY resize can block while it emits a redraw. Keep the output reader
        // free to enter ProcessOutput and drain that redraw at the new dimensions.
        if (!stopped) _connection.Resize(columns, rows);
    }

    private void PrepareInput()
    {
        _terminal.ClearSelection();
        _terminal.ScrollToBottom();
    }

    public void ScrollLines(int delta) => MutateView(() => _terminal.ScrollLines(delta));
    public void ScrollTo(long offset) => MutateView(() => _terminal.ScrollTo(offset));
    public void ScrollToBottom() => MutateView(_terminal.ScrollToBottom);
    public void BeginSelection(int column, int row, int clickCount = 1, bool rectangular = false) =>
        MutateView(() => _terminal.BeginSelection(column, row, clickCount, rectangular));
    public void ExtendSelection(int column, int row) => MutateView(() => _terminal.ExtendSelection(column, row));
    public void EndSelection() => MutateView(_terminal.EndSelection);
    public void ClearSelection() => MutateView(_terminal.ClearSelection);

    public string? ReadSelectionText()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            return _terminal.ReadSelectionText();
        }
    }

    public bool Find(string query, bool forward, bool caseSensitive = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            return _terminal.Find(query, forward, caseSensitive);
        }
    }

    private void MutateView(Action change)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            change();
        }
    }

    private void ProcessOutput(ReadOnlyMemory<byte> bytes)
    {
        lock (_gate)
        {
            _terminal.Write(bytes.Span);
            _osc.Feed(bytes.Span);
            if (_owned is { } owned && _prompt.State == ShellPromptState.Ready && _prompt.ReadyCommandId == owned.Id && owned.Capture.Ended)
            {
                _owned = null;
                owned.Completion.TrySetResult(new(owned.Interrupted ? TerminalExecutionStatus.Interrupted : TerminalExecutionStatus.Completed,
                    owned.Capture.Text, _prompt.LastCommandSucceeded, _prompt.LastExitCode,
                    ShellDirectory.FromReport(_terminal.ReadWorkingDirectory(), _profile)?.Path, owned.Capture.Truncated));
            }
            foreach (var reply in _terminal.TakeResponses())
            {
                // During teardown keep draining output, but the input channel is closing.
                if (_disposeTask is null) _connection.SendReply(reply);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        // Native close must not wait for the reader while we hold its terminal lock.
        lock (_gate) return new ValueTask(_disposeTask ??= Task.Run(CloseAsync));
    }

    /// <summary>Stops the owned shell while retaining its screen and history for inspection.</summary>
    public ValueTask StopAsync()
    {
        // Closing ConPTY can wait for output. Its reader must remain free to enter the terminal lock.
        lock (_gate) return new ValueTask(_stopTask ??= Task.Run(async () =>
            await _connection.DisposeAsync().ConfigureAwait(false)));
    }

    private async Task CloseAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                _connection.OutputReceived -= ProcessOutput;
                _terminal.Dispose();
            }
        }
    }

}
