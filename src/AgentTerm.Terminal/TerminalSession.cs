using System.Text;

namespace AgentTerm.Terminal;

/// <summary>Owns one shell and serializes access to its Ghostty terminal state.</summary>
public sealed class TerminalSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly GhosttyTerminal _terminal;
    private readonly ConPtyConnection _connection;
    private readonly ShellProfile _profile;
    private bool _started;
    private Task? _stopTask;
    private Task? _disposeTask;

    public TerminalSession(ushort columns = 80, ushort rows = 24, ShellProfile? profile = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ConPTY requires Windows.");
        _profile = profile ?? ShellProfile.Default;
        _connection = new ConPtyConnection(columns, rows);
        _terminal = new GhosttyTerminal(columns, rows);
        _connection.OutputReceived += ProcessOutput;
    }

    public string ShellName => _profile.Name;
    public ShellProfile Profile => _profile;
    public Task<int> Completion => _connection.Completion;
    public int ProcessId => _connection.ProcessId;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_started) throw new InvalidOperationException("This session has already started.");
            _connection.Start(_profile.Executable, ShellIntegration.Arguments(_profile), _profile.StartingDirectory);
            _started = true;
        }
    }

    public void Send(string text)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length != 0) PrepareInput();
            _connection.Send(bytes);
        }
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
