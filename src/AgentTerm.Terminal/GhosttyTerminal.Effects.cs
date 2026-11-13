using System.Runtime.InteropServices;
using System.Text;

namespace AgentTerm.Terminal;

// Native effect callbacks: bell, desktop notification, progress, clipboard read and write, and window resize requests.
// Callbacks run inside ghostty_terminal_vt_write while the caller holds the terminal lock. They copy their borrowed payload,
// apply policy in TerminalEffects, queue the result, and return. They never touch UI, never wait on another thread, and never
// let an exception cross the ABI. A callback that fails answers as a refusal.
public sealed partial class GhosttyTerminal
{
    private EffectsNative.Bell _bellCallback = null!;
    private EffectsNative.Notification _notificationCallback = null!;
    private EffectsNative.Progress _progressCallback = null!;
    private EffectsNative.ResizeWindow _resizeCallback = null!;
    private EffectsNative.ClipboardWrite _clipboardWriteCallback = null!;
    private EffectsNative.ClipboardRead _clipboardReadCallback = null!;
    private readonly EffectsNative.MimeReader _pasteReader = static (_, _, _) => false;
    private bool _effectsConfigured;

    /// <summary>Preferences, queue, and clipboard adapter for requests programs make through the terminal.</summary>
    public TerminalEffects Effects { get; } = new();

    /// <summary>
    /// True after the native library accepted the clipboard read callback. While it is installed, a program that enables
    /// Kitty paste events (mode 5522) receives a paste event instead of the text. <see cref="EncodeClipboardPaste"/> handles that.
    /// </summary>
    public bool SupportsClipboardProtocols => _effectsConfigured;

    private unsafe void ConfigureEffects()
    {
        // The delegate fields keep each callback rooted for the terminal's lifetime.
        _bellCallback = OnBell;
        _notificationCallback = OnNotification;
        _progressCallback = OnProgress;
        _resizeCallback = OnResizeWindow;
        _clipboardWriteCallback = OnClipboardWrite;
        _clipboardReadCallback = OnClipboardRead;
        const int optBell = 3, optClipboardWrite = 27, optNotification = 30, optProgress = 31, optClipboardRead = 39,
            optClipboardWriteMax = 40, optResizeWindow = 48;
        Set(optBell, _bellCallback);
        Set(optClipboardWrite, _clipboardWriteCallback);
        Set(optNotification, _notificationCallback);
        Set(optProgress, _progressCallback);
        Set(optClipboardRead, _clipboardReadCallback);
        Set(optResizeWindow, _resizeCallback);
        // The default is 64 MiB per Kitty write transaction. Bound the memory a program can make the terminal buffer
        // to what the host would accept anyway.
        var limit = (nuint)TerminalEffectOptions.MaximumClipboardBytes;
        ThrowIfFailed(EffectsNative.ghostty_terminal_set(_handle, optClipboardWriteMax, &limit),
            "Could not bound the clipboard transaction size.");
        _effectsConfigured = true;

        void Set(int option, Delegate callback) =>
            ThrowIfFailed(EffectsNative.ghostty_terminal_set(_handle, option, (void*)Marshal.GetFunctionPointerForDelegate(callback)),
                "Could not register an effect callback.");
    }

    private unsafe void DisposeEffects()
    {
        if (!_effectsConfigured) return;
        _effectsConfigured = false;
        foreach (var option in new[] { 3, 27, 30, 31, 39, 48 })
            EffectsNative.ghostty_terminal_set(_handle, option, null);
        GC.KeepAlive(_bellCallback);
        GC.KeepAlive(_notificationCallback);
        GC.KeepAlive(_progressCallback);
        GC.KeepAlive(_resizeCallback);
        GC.KeepAlive(_clipboardWriteCallback);
        GC.KeepAlive(_clipboardReadCallback);
        Effects.Reset();
    }

    /// <summary>
    /// Encodes a user paste. When the program enabled Kitty paste events (mode 5522) this is a paste event and a later read
    /// request is answered from <paramref name="text"/>. Otherwise it is the ordinary bracketed or plain paste.
    /// </summary>
    public unsafe byte[] EncodeClipboardPaste(string text)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || !_effectsConfigured || !KittyPasteEventsEnabled()) return EncodePaste(text);

        Effects.RecordPaste(text);
        var mime = "text/plain"u8.ToArray();
        var before = _responses.Count;
        bool pasted;
        int result;
        fixed (byte* mimePointer = mime)
        {
            var mimeString = new EffectsNative.GhosttyString { Ptr = mimePointer, Length = (nuint)mime.Length };
            var request = new EffectsNative.PasteRequest
            {
                Size = (nuint)sizeof(EffectsNative.PasteRequest),
                Location = 0,
                Source = 0,
                Mimes = &mimeString,
                MimesLength = 1,
                ReaderFunction = Marshal.GetFunctionPointerForDelegate(_pasteReader),
            };
            result = EffectsNative.ghostty_terminal_paste(_handle, &request, &pasted);
        }
        GC.KeepAlive(_pasteReader);
        if (result != 0 || !pasted) return [];
        // The event reaches the program through the write_pty callback. Take it here so it is sent once, with the paste.
        var written = _responses.GetRange(before, _responses.Count - before);
        _responses.RemoveRange(before, written.Count);
        return written.SelectMany(static bytes => bytes).ToArray();
    }

    private bool KittyPasteEventsEnabled()
    {
        try { return IsInputModeEnabled((InputNative.DecMode)5522); }
        catch (InvalidOperationException) { return false; }
    }

    private unsafe void OnBell(IntPtr terminal, IntPtr userdata)
    {
        try { Effects.Bell(); }
        catch (Exception) { Effects.RecordFault(); }
    }

    private unsafe void OnNotification(IntPtr terminal, IntPtr userdata, EffectsNative.NotificationRequest* request)
    {
        try { Effects.Notify(request->Title.ToManaged(), request->Body.ToManaged()); }
        catch (Exception) { Effects.RecordFault(); }
    }

    private unsafe void OnProgress(IntPtr terminal, IntPtr userdata, EffectsNative.ProgressReport* report)
    {
        try { Effects.Progress((TerminalProgressState)report->State, report->Percent); }
        catch (Exception) { Effects.RecordFault(); }
    }

    private unsafe void OnResizeWindow(IntPtr terminal, IntPtr userdata, EffectsNative.ResizeWindowRequest* request)
    {
        try
        {
            if (request->Tag == 0) Effects.RequestResize(request->Rows, request->Columns);
        }
        catch (Exception) { Effects.RecordFault(); }
    }

    private unsafe void OnClipboardWrite(IntPtr terminal, IntPtr userdata, EffectsNative.ClipboardWriteRequest* request)
    {
        var result = EffectsNative.WriteDenied;
        try
        {
            long total = 0;
            for (nuint index = 0; index < request->ContentsLength; index++)
                total += (long)request->Contents[index].Data.Length;
            var parts = new List<ClipboardPart>();
            if (total <= Effects.Options.ClipboardMaxBytes)
                for (nuint index = 0; index < request->ContentsLength; index++)
                {
                    var content = request->Contents[index];
                    parts.Add(new ClipboardPart(content.Mime.ToManaged(), content.Data.ToBytes()));
                }
            var decision = Effects.HandleWrite(new ClipboardWriteInput(Location(request->Location), parts,
                request->Name.ToManaged(), total));
            result = decision.Outcome switch
            {
                ClipboardOutcome.Success => EffectsNative.WriteSuccess,
                ClipboardOutcome.Unsupported => EffectsNative.WriteUnsupported,
                ClipboardOutcome.Busy => EffectsNative.WriteBusy,
                ClipboardOutcome.InvalidData => EffectsNative.WriteInvalidData,
                ClipboardOutcome.IoError => EffectsNative.WriteIoError,
                _ => EffectsNative.WriteDenied,
            };
        }
        catch (Exception) { Effects.RecordFault(); }

        // Never remember a grant. Policy stays under the user's control in settings, and a remembered grant would outlive it.
        var reply = new EffectsNative.ClipboardWriteReply { Size = (nuint)sizeof(EffectsNative.ClipboardWriteReply), Result = result };
        ((delegate* unmanaged[Cdecl]<EffectsNative.ClipboardWriteRequest*, EffectsNative.ClipboardWriteReply*, void>)request->Reply)(request, &reply);
    }

    private unsafe void OnClipboardRead(IntPtr terminal, IntPtr userdata, EffectsNative.ClipboardReadRequest* request)
    {
        var result = EffectsNative.ReadDenied;
        ClipboardReadDecision decision = new(ClipboardOutcome.Denied, null, []);
        try
        {
            var mimes = new string[checked((int)request->MimesLength)];
            for (var index = 0; index < mimes.Length; index++) mimes[index] = request->Mimes[index].ToManaged();
            decision = Effects.HandleRead(new ClipboardReadInput(Location(request->Location), mimes, request->List != 0,
                request->Name.ToManaged(), request->Granted != 0));
            result = decision.Outcome switch
            {
                ClipboardOutcome.Success => EffectsNative.ReadSuccess,
                ClipboardOutcome.Unsupported => EffectsNative.ReadUnsupported,
                ClipboardOutcome.Busy => EffectsNative.ReadBusy,
                ClipboardOutcome.Denied => EffectsNative.ReadDenied,
                _ => EffectsNative.ReadIoError,
            };
        }
        catch (Exception)
        {
            Effects.RecordFault();
            result = EffectsNative.ReadDenied;
            decision = new(ClipboardOutcome.Denied, null, []);
        }

        var reply = new EffectsNative.ClipboardReadReply { Size = (nuint)sizeof(EffectsNative.ClipboardReadReply), Result = result };
        var function = (delegate* unmanaged[Cdecl]<EffectsNative.ClipboardReadRequest*, EffectsNative.ClipboardReadReply*, void>)request->Reply;
        if (result != EffectsNative.ReadSuccess)
        {
            function(request, &reply);
            return;
        }

        // The reply borrows these buffers only for the call. Pin them for exactly that long.
        var mimeBytes = decision.Content is { } content ? Encoding.UTF8.GetBytes(content.Mime) : [];
        var data = decision.Content?.Data ?? [];
        var available = new byte[decision.Available.Count][];
        for (var index = 0; index < available.Length; index++) available[index] = Encoding.UTF8.GetBytes(decision.Available[index]);
        var handles = new System.Runtime.InteropServices.GCHandle[available.Length];
        try
        {
            var strings = new EffectsNative.GhosttyString[available.Length];
            for (var index = 0; index < available.Length; index++)
            {
                handles[index] = GCHandle.Alloc(available[index], GCHandleType.Pinned);
                strings[index] = new EffectsNative.GhosttyString
                {
                    Ptr = (byte*)handles[index].AddrOfPinnedObject(),
                    Length = (nuint)available[index].Length,
                };
            }
            fixed (byte* mimePointer = mimeBytes)
            fixed (byte* dataPointer = data)
            fixed (EffectsNative.GhosttyString* availablePointer = strings)
            {
                var item = new EffectsNative.ClipboardContent
                {
                    Mime = new EffectsNative.GhosttyString { Ptr = mimePointer, Length = (nuint)mimeBytes.Length },
                    Data = new EffectsNative.GhosttyString { Ptr = dataPointer, Length = (nuint)data.Length },
                };
                if (decision.Content is not null)
                {
                    reply.Contents = &item;
                    reply.ContentsLength = 1;
                }
                reply.Available = strings.Length == 0 ? null : availablePointer;
                reply.AvailableLength = (nuint)strings.Length;
                function(request, &reply);
            }
        }
        finally
        {
            foreach (var handle in handles)
                if (handle.IsAllocated) handle.Free();
        }
    }

    private static ClipboardLocation Location(int value) => value switch
    {
        1 => ClipboardLocation.Selection,
        2 => ClipboardLocation.Primary,
        _ => ClipboardLocation.Standard,
    };

    private static unsafe class EffectsNative
    {
        private const string Library = "ghostty-vt";

        internal const int WriteSuccess = 0, WriteDenied = 1, WriteUnsupported = 2, WriteBusy = 3, WriteInvalidData = 4, WriteIoError = 5;
        internal const int ReadSuccess = 0, ReadDenied = 1, ReadUnsupported = 2, ReadBusy = 3, ReadIoError = 4;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Bell(IntPtr terminal, IntPtr userdata);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Notification(IntPtr terminal, IntPtr userdata, NotificationRequest* request);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Progress(IntPtr terminal, IntPtr userdata, ProgressReport* report);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ResizeWindow(IntPtr terminal, IntPtr userdata, ResizeWindowRequest* request);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ClipboardWrite(IntPtr terminal, IntPtr userdata, ClipboardWriteRequest* request);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ClipboardRead(IntPtr terminal, IntPtr userdata, ClipboardReadRequest* request);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)]
        internal delegate bool MimeReader(IntPtr userdata, GhosttyString mime, GhosttyWriter writer);

        [StructLayout(LayoutKind.Sequential)]
        internal struct GhosttyString
        {
            internal byte* Ptr;
            internal nuint Length;

            internal readonly string ToManaged() => Length == 0 || Ptr == null ? "" : Encoding.UTF8.GetString(Ptr, checked((int)Length));

            internal readonly byte[] ToBytes()
            {
                if (Length == 0 || Ptr == null) return [];
                var bytes = new byte[checked((int)Length)];
                Marshal.Copy((IntPtr)Ptr, bytes, 0, bytes.Length);
                return bytes;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct GhosttyWriter
        {
            internal IntPtr Write;
            internal IntPtr Userdata;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NotificationRequest
        {
            internal GhosttyString Title;
            internal GhosttyString Body;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProgressReport
        {
            internal int State;
            internal sbyte Percent;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        internal struct ResizeWindowRequest
        {
            [FieldOffset(0)] internal int Tag;
            [FieldOffset(8)] internal ushort Rows;
            [FieldOffset(10)] internal ushort Columns;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ClipboardContent
        {
            internal GhosttyString Mime;
            internal GhosttyString Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ClipboardWriteRequest
        {
            internal int Location;
            internal ClipboardContent* Contents;
            internal nuint ContentsLength;
            internal GhosttyString Name;
            internal byte Granted;
            internal byte CanRemember;
            internal IntPtr Context;
            internal IntPtr Reply;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ClipboardWriteReply
        {
            internal nuint Size;
            internal int Result;
            internal byte Remember;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ClipboardReadRequest
        {
            internal int Location;
            internal GhosttyString* Mimes;
            internal nuint MimesLength;
            internal byte List;
            internal GhosttyString Name;
            internal byte Granted;
            internal byte CanRemember;
            internal IntPtr Context;
            internal IntPtr Reply;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ClipboardReadReply
        {
            internal nuint Size;
            internal int Result;
            internal ClipboardContent* Contents;
            internal nuint ContentsLength;
            internal GhosttyString* Available;
            internal nuint AvailableLength;
            internal byte Remember;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PasteRequest
        {
            internal nuint Size;
            internal int Location;
            internal int Source;
            internal GhosttyString* Mimes;
            internal nuint MimesLength;
            internal IntPtr ReaderFunction;
            internal IntPtr ReaderUserdata;
            internal byte AllowUnsafe;
        }

        [DllImport(Library)]
        internal static extern int ghostty_terminal_set(TerminalHandle terminal, int option, void* value);

        [DllImport(Library)]
        internal static extern int ghostty_terminal_paste(TerminalHandle terminal, PasteRequest* paste, bool* pasted);
    }
}
