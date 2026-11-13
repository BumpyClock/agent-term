using System.Text;

namespace AgentTerm.Terminal;

/// <summary>
/// Policy, bounds, and the ordered queue behind the native effect callbacks (bell, notification, progress,
/// clipboard, window resize). The queue is managed and thread-safe. Native callbacks copy payloads under the terminal lock.
/// Clipboard operations may wait for the clipboard owner thread for a bounded time so their synchronous reply is truthful.
/// The UI drains <see cref="TakePending"/> after the session releases its lock.
/// </summary>
public sealed class TerminalEffects
{
    public const int MaxPendingNotifications = 16;
    public const int MaxPendingClipboardNotices = 8;
    public const int MaxPendingWrites = 4;
    public const int MaxTitleChars = 256;
    public const int MaxBodyChars = 1024;
    public const int MaxProgramChars = 64;
    public const int MaxPreviewChars = 160;
    public const int MaxBellCount = 1000;
    public static readonly TimeSpan PasteGrantLifetime = TimeSpan.FromSeconds(30);

    private sealed record PendingWrite(string? Text, string Program);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] TextMimes = ["text/plain"];

    private readonly object _gate = new();
    private readonly List<TerminalEffect> _pending = [];
    private readonly Dictionary<long, PendingWrite> _writes = [];
    private readonly Func<long> _clock;
    private volatile TerminalEffectOptions _options = TerminalEffectOptions.Default;
    private volatile ITerminalClipboard? _clipboard;
    private long _nextRequest;
    private bool _uiProgressActive;
    private int? _progressPercent;
    private string? _pasteText;
    private long _pasteExpires;
    private int _faults;

    public TerminalEffects(Func<long>? clock = null) => _clock = clock ?? (() => Environment.TickCount64);

    /// <summary>Current host preferences. Safe to change at any time; the next request sees the new value.</summary>
    public TerminalEffectOptions Options
    {
        get => _options;
        set
        {
            var options = (value ?? throw new ArgumentNullException(nameof(value))).Normalize();
            lock (_gate)
            {
                _options = options;
                if (!options.Progress) Progress(TerminalProgressState.Remove, 0);
            }
        }
    }

    /// <summary>The clipboard adapter. Null refuses every clipboard request as unsupported.</summary>
    public ITerminalClipboard? Clipboard { get => _clipboard; set => _clipboard = value; }

    /// <summary>Count of callback exceptions that were swallowed. A failing callback answers as a refusal.</summary>
    public int Faults => Volatile.Read(ref _faults);

    /// <summary>
    /// True when a program's clipboard write can succeed or be offered to the user. A device attributes reply can use this to
    /// advertise OSC 52 clipboard access (DA1 feature 52) only when the host would honor it.
    /// </summary>
    public bool AdvertisesClipboardAccess => _clipboard is not null && _options.ClipboardWrite != ClipboardWritePolicy.Deny;

    public int PendingWriteCount { get { lock (_gate) return _writes.Count; } }

    public void RecordFault() => Interlocked.Increment(ref _faults);

    // Bell.

    public void Bell()
    {
        if ((_options.Bell & BellStyle.Both) == 0) return;
        lock (_gate)
        {
            var index = _pending.FindIndex(effect => effect is BellEffect);
            if (index < 0) _pending.Add(new BellEffect(1));
            else _pending[index] = new BellEffect(Math.Min(MaxBellCount, ((BellEffect)_pending[index]).Count + 1));
        }
    }

    // Desktop notifications.

    public void Notify(string title, string body)
    {
        if (_options.Notifications == NotificationStyle.Off) return;
        title = Clip(Sanitize(title), MaxTitleChars);
        body = Clip(Sanitize(body), MaxBodyChars);
        if (title.Length == 0 && body.Length == 0) return;
        lock (_gate)
        {
            var last = _pending.FindLastIndex(effect => effect is NotificationEffect);
            if (last >= 0)
            {
                var previous = (NotificationEffect)_pending[last];
                var count = _pending.Count(effect => effect is NotificationEffect);
                if (count >= MaxPendingNotifications || previous.Title == title && previous.Body == body)
                {
                    _pending[last] = previous with { Repeats = previous.Repeats + 1 };
                    return;
                }
            }
            _pending.Add(new NotificationEffect(title, body));
        }
    }

    // Progress.

    /// <summary>Records the latest progress state. Earlier undelivered states are replaced, so a storm costs one update.</summary>
    public void Progress(TerminalProgressState state, int percent)
    {
        if (!Enum.IsDefined(state)) return;
        percent = state switch
        {
            TerminalProgressState.Set => Math.Clamp(percent < 0 ? 0 : percent, 0, 100),
            TerminalProgressState.Error or TerminalProgressState.Pause => percent < 0 ? -1 : Math.Min(percent, 100),
            _ => 0,
        };
        lock (_gate)
        {
            if (state != TerminalProgressState.Remove && !_options.Progress) return;
            if (state is TerminalProgressState.Error or TerminalProgressState.Pause && percent < 0)
                percent = _progressPercent ?? (state == TerminalProgressState.Error ? 100 : 0);
            _progressPercent = state == TerminalProgressState.Remove ? null : percent;
            _pending.RemoveAll(effect => effect is ProgressEffect);
            // Nothing is visible, so there is nothing to remove. This keeps a flood of removals from waking the UI.
            if (state == TerminalProgressState.Remove && !_uiProgressActive) return;
            _pending.Add(new ProgressEffect(state, percent));
        }
    }

    // Window resize.

    public void RequestResize(int rows, int columns)
    {
        if (!_options.AllowWindowResize || rows < 0 || columns < 0 || rows == 0 && columns == 0) return;
        lock (_gate)
        {
            _pending.RemoveAll(effect => effect is ResizeRequestEffect);
            _pending.Add(new ResizeRequestEffect(rows, columns));
        }
    }

    // Clipboard.

    /// <summary>Applies write policy. Runs on the reader thread inside a native callback.</summary>
    public ClipboardWriteDecision HandleWrite(ClipboardWriteInput input)
    {
        return HandleWrite(input, _options);
    }

    internal ClipboardWriteDecision HandleWrite(ClipboardWriteInput input, TerminalEffectOptions options)
    {
        var program = Clip(Sanitize(input.Program), MaxProgramChars);
        if (options.ClipboardWrite == ClipboardWritePolicy.Deny)
        {
            AddClipboard(new ClipboardEffect(ClipboardNoticeKind.WriteBlocked, ClampLength(input.TotalBytes), program));
            return new(ClipboardOutcome.Denied);
        }
        if (input.TotalBytes > options.ClipboardMaxBytes)
        {
            AddClipboard(new ClipboardEffect(ClipboardNoticeKind.WriteTooLarge, ClampLength(input.TotalBytes), program));
            return new(ClipboardOutcome.Denied);
        }
        if (input.Location != ClipboardLocation.Standard) return new(ClipboardOutcome.Unsupported);

        string? text = null;
        if (input.Parts.Count != 0)
        {
            var part = input.Parts.FirstOrDefault(candidate => IsText(candidate.Mime));
            if (part is null) return new(ClipboardOutcome.Unsupported);
            try { text = StrictUtf8.GetString(part.Data); }
            catch (ArgumentException) { return new(ClipboardOutcome.InvalidData); }
            // An explicit empty representation is not a request to clear the clipboard.
            if (text.Length == 0) return new(ClipboardOutcome.Success);
        }

        if (options.ClipboardWrite == ClipboardWritePolicy.Ask)
        {
            long id;
            lock (_gate)
            {
                if (_writes.Count >= MaxPendingWrites)
                {
                    // Keep the newest requests. The oldest is dropped, so a spamming program cannot fill memory.
                    var oldest = _writes.Keys.Min();
                    _writes.Remove(oldest);
                    _pending.RemoveAll(effect => effect is ClipboardEffect { RequestId: var request } && request == oldest);
                }
                id = ++_nextRequest;
                _writes[id] = new(text, program);
            }
            AddClipboard(new ClipboardEffect(ClipboardNoticeKind.WriteRequested, text?.Length ?? 0, program, id, Preview(text)));
            return new(ClipboardOutcome.Denied);
        }

        var outcome = Write(text);
        AddClipboard(new ClipboardEffect(outcome == ClipboardOutcome.Success
            ? text is null ? ClipboardNoticeKind.Cleared : ClipboardNoticeKind.Wrote
            : ClipboardNoticeKind.WriteFailed, text?.Length ?? 0, program));
        return new(outcome);
    }

    /// <summary>
    /// Applies read policy. A read the user just triggered by pasting into a program that enabled Kitty paste events
    /// is served from the pasted text, since the user already chose to share it.
    /// </summary>
    public ClipboardReadDecision HandleRead(ClipboardReadInput input)
    {
        var options = _options;
        var program = Clip(Sanitize(input.Program), MaxProgramChars);
        var wantsText = input.Mimes.Count == 0 || input.Mimes.Any(IsText);
        var mime = input.Mimes.FirstOrDefault(IsText) ?? TextMimes[0];

        string? pasted = null;
        if (input.Granted)
            lock (_gate)
            {
                if (_pasteText is not null && _clock() <= _pasteExpires) pasted = _pasteText;
                else _pasteText = null;
            }
        if (pasted is not null && input.Location == ClipboardLocation.Standard)
        {
            if (input.Mimes.Count == 0) return new(ClipboardOutcome.Success, null, TextMimes);
            if (!wantsText) return new(ClipboardOutcome.Success, null, TextMimes);
            return new(ClipboardOutcome.Success, new(mime, Encoding.UTF8.GetBytes(pasted)), TextMimes);
        }

        if (input.Location != ClipboardLocation.Standard) return new(ClipboardOutcome.Unsupported, null, []);
        var allowed = options.ClipboardRead == ClipboardReadPolicy.Allow;
        // A targets-only request discloses just whether text exists. Deny answers it as an empty clipboard.
        if (input.Mimes.Count == 0 && input.List)
            return new(ClipboardOutcome.Success, null, allowed && Safe(() => _clipboard?.HasText() == true) ? TextMimes : []);
        if (!allowed)
        {
            AddClipboard(new ClipboardEffect(ClipboardNoticeKind.ReadBlocked, 0, program));
            return new(ClipboardOutcome.Denied, null, []);
        }
        if (!wantsText) return new(ClipboardOutcome.Success, null, []);
        var clipboard = _clipboard;
        if (clipboard is null) return new(ClipboardOutcome.Unsupported, null, []);

        ClipboardOutcome outcome;
        string text;
        try { outcome = clipboard.TryReadText(options.ClipboardMaxBytes, out text); }
        catch (Exception) { RecordFault(); outcome = ClipboardOutcome.IoError; text = ""; }
        byte[] bytes = [];
        if (outcome == ClipboardOutcome.Success)
        {
            bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > options.ClipboardMaxBytes) outcome = ClipboardOutcome.TooLarge;
        }
        if (outcome != ClipboardOutcome.Success)
        {
            AddClipboard(new ClipboardEffect(ClipboardNoticeKind.ReadFailed, 0, program));
            return new(outcome, null, []);
        }
        AddClipboard(new ClipboardEffect(ClipboardNoticeKind.ReadServed, text.Length, program));
        return new(ClipboardOutcome.Success, new(mime, bytes), input.List ? TextMimes : []);
    }

    /// <summary>Completes a write the user was asked about. Runs on the caller's thread. A refusal discards the text.</summary>
    public ClipboardOutcome ResolveWrite(long requestId, bool allow)
    {
        PendingWrite? pending;
        lock (_gate)
        {
            if (!_writes.Remove(requestId, out pending)) return ClipboardOutcome.Unsupported;
            _pending.RemoveAll(effect => effect is ClipboardEffect { RequestId: var request } && request == requestId);
        }
        return allow ? Write(pending.Text) : ClipboardOutcome.Denied;
    }

    /// <summary>Remembers the text a user just pasted so a Kitty paste event's follow-up read is answered from it.</summary>
    public void RecordPaste(string text)
    {
        lock (_gate)
        {
            _pasteText = text;
            _pasteExpires = _clock() + (long)PasteGrantLifetime.TotalMilliseconds;
        }
    }

    // Lifetime.

    /// <summary>
    /// Takes every undelivered effect in the order the programs requested them. Bells, notifications, clipboard notices,
    /// and progress are already coalesced. Returns an empty list when nothing is pending.
    /// </summary>
    public IReadOnlyList<TerminalEffect> TakePending()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return [];
            var taken = _pending.ToArray();
            _pending.Clear();
            foreach (var effect in taken)
            {
                if (effect is ResetEffect) _uiProgressActive = false;
                else if (effect is ProgressEffect progress) _uiProgressActive = progress.State != TerminalProgressState.Remove;
            }
            return taken;
        }
    }

    public bool HasPending { get { lock (_gate) return _pending.Count != 0; } }

    /// <summary>
    /// Drops everything that has not been delivered, forgets approvals and paste grants, and removes shown progress.
    /// A full terminal reset and shell exit use this.
    /// </summary>
    public void Reset(bool clearHost = false)
    {
        lock (_gate)
        {
            _pending.Clear();
            _progressPercent = null;
            _writes.Clear();
            _pasteText = null;
            if (clearHost) _pending.Add(new ResetEffect());
            else if (_uiProgressActive) _pending.Add(new ProgressEffect(TerminalProgressState.Remove, 0));
        }
    }

    private ClipboardOutcome Write(string? text)
    {
        var clipboard = _clipboard;
        if (clipboard is null) return ClipboardOutcome.Unsupported;
        try { return text is null ? clipboard.TryClear() : clipboard.TryWriteText(text); }
        catch (Exception) { RecordFault(); return ClipboardOutcome.IoError; }
    }

    private bool Safe(Func<bool> action)
    {
        try { return action(); }
        catch (Exception) { RecordFault(); return false; }
    }

    private void AddClipboard(ClipboardEffect notice)
    {
        lock (_gate)
        {
            if (notice.Kind != ClipboardNoticeKind.WriteRequested)
            {
                var index = _pending.FindIndex(effect => effect is ClipboardEffect existing && existing.Kind == notice.Kind &&
                    existing.Program == notice.Program);
                if (index >= 0)
                {
                    _pending[index] = notice with { Repeats = ((ClipboardEffect)_pending[index]).Repeats + 1 };
                    return;
                }
            }
            if (_pending.Count(effect => effect is ClipboardEffect) >= MaxPendingClipboardNotices)
            {
                var oldest = _pending.FindIndex(effect => effect is ClipboardEffect { Kind: not ClipboardNoticeKind.WriteRequested });
                if (oldest < 0) return;
                _pending.RemoveAt(oldest);
            }
            _pending.Add(notice);
        }
    }

    private static bool IsText(string mime)
    {
        var end = mime.IndexOf(';');
        return string.Equals((end < 0 ? mime : mime[..end]).Trim(), "text/plain", StringComparison.OrdinalIgnoreCase);
    }

    private static int ClampLength(long bytes) => (int)Math.Min(bytes, int.MaxValue);

    private static string Preview(string? text) => text is null ? "" : Clip(Sanitize(text), MaxPreviewChars);

    /// <summary>Replaces control and bidirectional formatting characters, which a program could use to forge or hide text in a prompt, with spaces.</summary>
    internal static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
            builder.Append(char.IsControl(character) || (int)character is >= 0x2028 and <= 0x202E or >= 0x2066 and <= 0x2069 ? ' ' : character);
        return builder.ToString().Trim();
    }

    private static string Clip(string text, int limit)
    {
        if (text.Length <= limit) return text;
        var end = limit;
        if (char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end] + "…";
    }
}
