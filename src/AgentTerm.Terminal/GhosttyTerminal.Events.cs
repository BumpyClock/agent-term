using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentTerm.Terminal;

/// <summary>Base of the protocol events the native parser reports while it consumes program output.</summary>
public abstract record TerminalProtocolEvent;

/// <summary>A program set the title with OSC 0 or OSC 2. Native parsing truncates titles to 1024 bytes.</summary>
public sealed record TerminalTitleChanged(string Title) : TerminalProtocolEvent;

/// <summary>A program reported or cleared its working directory with OSC 7. The report is the raw, undecoded payload.</summary>
public sealed record TerminalWorkingDirectoryChanged(string Report) : TerminalProtocolEvent;

/// <summary>A full reset (RIS). Native program status records were cleared just before this event.</summary>
public sealed record TerminalReset : TerminalProtocolEvent;

public enum SemanticPromptStep { PromptStart = 1, InputStart = 2, OutputStart = 3, CommandEnd = 4 }

public enum SemanticPromptKind { Primary = 0, Right = 1, Continuation = 2, Secondary = 3 }

/// <summary>An OSC 133 shell integration step. Shells may skip steps or repeat a prompt start.</summary>
public sealed record TerminalSemanticPrompt(SemanticPromptStep Step, SemanticPromptKind PromptKind, int? ExitCode,
    string Command, string Error) : TerminalProtocolEvent
{
    /// <summary>True when a new top-level prompt starts, not a continuation line of the same input.</summary>
    public bool StartsNewPrompt => Step == SemanticPromptStep.PromptStart && PromptKind is SemanticPromptKind.Primary or SemanticPromptKind.Right;
}

public enum ProgramStatusReportState { Idle, Working, Done, Blocked, Error, Clear }

/// <summary>
/// A validated OSC 7501 report. Native parsing already enforced every specification limit and decoded the text.
/// Text the program did not send is null. Progress is null when indeterminate or not applicable.
/// </summary>
public sealed record TerminalProgramStatusReport(ProgramStatusReportState State, string? Kind, int? Progress, string Id,
    string? App, string? Title, string? Message) : TerminalProtocolEvent;

public sealed partial class GhosttyTerminal
{
    private List<TerminalProtocolEvent>? _protocolEvents;
    private EventsNative.NotifyCallback? _titleChanged;
    private EventsNative.NotifyCallback? _pwdChanged;
    private EventsNative.NotifyCallback? _reset;
    private EventsNative.SemanticPromptCallback? _semanticPrompt;
    private EventsNative.ProgramStatusCallback? _programStatus;

    /// <summary>
    /// Registers native title, directory, semantic prompt, reset and program status callbacks. Events queue in stream
    /// order until <see cref="TakeProtocolEvents"/>. Registering program status also makes the terminal answer OSC 7501
    /// support queries through the normal reply queue.
    /// </summary>
    internal void EnableProtocolEvents()
    {
        ThrowIfDisposed();
        if (_protocolEvents is not null) return;
        _protocolEvents = [];
        _titleChanged = (_, _) => Queue(() => new TerminalTitleChanged(ReadTitle()));
        _pwdChanged = (_, _) => Queue(() => new TerminalWorkingDirectoryChanged(ReadWorkingDirectory()));
        unsafe
        {
            _semanticPrompt = (_, _, report) => Queue(() => SemanticPrompt(report));
            _programStatus = (_, _, report) => Queue(() => ProgramStatus(report));
        }
        Register(EventsNative.TitleChanged, _titleChanged, "title");
        Register(EventsNative.PwdChanged, _pwdChanged, "working directory");
        Register(EventsNative.SemanticPrompt, _semanticPrompt, "semantic prompt");
        Register(EventsNative.ProgramStatus, _programStatus, "program status");
    }

    private void ConfigureResetEvents()
    {
        _reset = (_, _) => Queue(() => { Effects.Reset(clearHost: true); return new TerminalReset(); });
        Register(EventsNative.Reset, _reset, "reset");
    }

    private void Register(int option, Delegate callback, string name) =>
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, option, Marshal.GetFunctionPointerForDelegate(callback)),
            $"Could not register {name} events.");

    /// <summary>Moves queued protocol events, oldest first, into <paramref name="into"/>.</summary>
    internal void TakeProtocolEvents(List<TerminalProtocolEvent> into)
    {
        if (_protocolEvents is not { Count: > 0 } events) return;
        into.AddRange(events);
        events.Clear();
    }

    // Native callbacks run inside a VT write. They only copy borrowed data into owned records; exceptions never cross
    // the ABI and surface after the write returns.
    private void Queue(Func<TerminalProtocolEvent?> create)
    {
        try { if (create() is { } created) _protocolEvents?.Add(created); }
        catch (Exception exception) { _callbackError ??= ExceptionDispatchInfo.Capture(exception); }
    }

    private static unsafe TerminalSemanticPrompt? SemanticPrompt(EventsNative.SemanticPromptEvent* report) =>
        report->Kind is < 1 or > 4 ? null : new((SemanticPromptStep)report->Kind,
            report->PromptKind is >= 0 and <= 3 ? (SemanticPromptKind)report->PromptKind : SemanticPromptKind.Primary,
            report->HasExitCode != 0 ? report->ExitCode : null, Text(report->Command), Text(report->Error));

    private static unsafe TerminalProgramStatusReport? ProgramStatus(EventsNative.ProgramStatusEvent* report)
    {
        if (report->State is < 0 or > 5) return null;
        var state = (ProgramStatusReportState)report->State;
        var kind = state != ProgramStatusReportState.Blocked ? null : report->Kind switch
        {
            1 => "permission", 2 => "question", 3 => "auth", _ => null
        };
        int? progress = report->Progress is >= 0 and <= 100 ? report->Progress : null;
        return new(state, kind, progress, Text(report->Id), Optional(report->App), Optional(report->Title), Optional(report->Message));
    }

    private static unsafe string Text(Native.Utf8String value) =>
        value.Length == 0 || value.Data == IntPtr.Zero ? "" : Encoding.UTF8.GetString((byte*)value.Data, checked((int)value.Length));

    private static string? Optional(Native.Utf8String value) => Text(value) is { Length: > 0 } text ? text : null;

    /// <summary>
    /// Parses bytes up to and including the first control string introducer or terminator, running callbacks only for
    /// consumed bytes. Returns the consumed byte count. Callers finish a series of these with <see cref="FinishWrite"/>.
    /// </summary>
    internal unsafe int WriteUntilStringBoundary(ReadOnlySpan<byte> data, out TerminalWriteStop stop)
    {
        ThrowIfDisposed();
        var native = new EventsNative.WriteStop { Size = (nuint)sizeof(EventsNative.WriteStop) };
        int result;
        ContentVersion++;
        fixed (byte* bytes = data)
        {
            try { result = EventsNative.ghostty_terminal_vt_write_until(_handle, bytes, (nuint)data.Length, EventsNative.StringBoundaries, &native); }
            finally { ContentVersion++; }
        }
        _presentationDirty = true;
        if (result == NoValue)
        {
            stop = default;
            return data.Length;
        }
        ThrowIfFailed(result, "Could not write terminal output.");
        stop = new((TerminalBoundaries)native.Reached, (TerminalStringKind)native.Kind,
            native.Introducer == 1, (TerminalStringEnd)native.End, native.Byte);
        return checked((int)native.Consumed);
    }

    private const int NoValue = -4;

    private static unsafe partial class EventsNative
    {
        private const string Lib = "ghostty-vt";
        internal const int TitleChanged = 6, PwdChanged = 26, SemanticPrompt = 43, Reset = 44, ProgramStatus = 47;
        internal const uint StringBoundaries = 0b111_1110;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void NotifyCallback(IntPtr terminal, IntPtr userdata);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void SemanticPromptCallback(IntPtr terminal, IntPtr userdata, SemanticPromptEvent* report);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void ProgramStatusCallback(IntPtr terminal, IntPtr userdata, ProgramStatusEvent* report);

        // Frozen 48-byte layout on 64-bit targets.
        [StructLayout(LayoutKind.Sequential)]
        internal struct SemanticPromptEvent
        {
            internal int Kind;
            internal int PromptKind;
            internal byte HasExitCode;
            internal int ExitCode;
            internal Native.Utf8String Command;
            internal Native.Utf8String Error;
        }

        // Frozen 80-byte layout on 64-bit targets.
        [StructLayout(LayoutKind.Sequential)]
        internal struct ProgramStatusEvent
        {
            internal int State;
            internal int Kind;
            internal sbyte Progress;
            internal Native.Utf8String Id;
            internal Native.Utf8String App;
            internal Native.Utf8String Title;
            internal Native.Utf8String Message;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WriteStop
        {
            internal nuint Size;
            internal nuint Consumed;
            internal uint Reached;
            internal int Kind;
            internal int Introducer;
            internal int End;
            internal byte Byte;
        }

        // Not SuppressGCTransition: a write parses arbitrary input and runs managed callbacks.
        [LibraryImport(Lib)]
        internal static partial int ghostty_terminal_vt_write_until(TerminalHandle terminal, byte* data, nuint length, uint boundaries, WriteStop* stop);
    }
}

[Flags]
public enum TerminalBoundaries : uint
{
    None = 0, ApcStart = 1 << 1, ApcEnd = 1 << 2, DcsStart = 1 << 3, DcsEnd = 1 << 4, OscStart = 1 << 5, OscEnd = 1 << 6,
    Starts = ApcStart | DcsStart | OscStart,
    Ends = ApcEnd | DcsEnd | OscEnd
}

public enum TerminalStringKind { Apc, Dcs, Osc }

public enum TerminalStringEnd { StEsc, StC1, Cancelled, Aborted, Bel }

/// <summary>Where a boundary write stopped. Kind is the ended string when one byte both ends and starts strings.</summary>
public readonly record struct TerminalWriteStop(TerminalBoundaries Reached, TerminalStringKind Kind, bool C1Introducer,
    TerminalStringEnd End, byte Byte);
