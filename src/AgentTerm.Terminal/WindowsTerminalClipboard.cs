using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AgentTerm.Terminal;

/// <summary>
/// Windows clipboard text through the Win32 clipboard API, usable from any thread. Reads open the clipboard without an owner.
/// Writes need an owner window, so they run on one dedicated message-pump thread that owns a message-only window.
/// Neither path waits for the UI thread. Each clipboard open retries briefly, then reports Busy.
/// </summary>
public sealed class WindowsTerminalClipboard : ITerminalClipboard
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const uint WmApp = 0x8000;
    private static readonly IntPtr MessageParent = new(-3);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromMilliseconds(750);

    public static WindowsTerminalClipboard Shared { get; } = new();

    private WindowsTerminalClipboard() { }

    private readonly object _startGate = new();
    private readonly ConcurrentQueue<Job> _jobs = new();
    private Thread? _owner;
    private IntPtr _window;
    private readonly ManualResetEventSlim _ready = new();

    private sealed class Job(Func<ClipboardOutcome> work)
    {
        private int _state;
        public readonly ManualResetEventSlim Done = new();
        public ClipboardOutcome Outcome = ClipboardOutcome.IoError;
        public bool TryClaim() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
        public bool TryCancel() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;
        public void Run()
        {
            if (!TryClaim()) return;
            try { Outcome = work(); }
            catch (Exception) { Outcome = ClipboardOutcome.IoError; }
            finally { Done.Set(); }
        }
    }

    public bool HasText() => OperatingSystem.IsWindows() && IsClipboardFormatAvailable(CfUnicodeText);

    public ClipboardOutcome TryReadText(int maxChars, out string text)
    {
        text = "";
        if (!OperatingSystem.IsWindows()) return ClipboardOutcome.Unsupported;
        if (!IsClipboardFormatAvailable(CfUnicodeText)) return ClipboardOutcome.Success;
        if (!Open(IntPtr.Zero, attempts: 3)) return ClipboardOutcome.Busy;
        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero) return ClipboardOutcome.Success;
            var capacity = checked((long)GlobalSize(handle) / 2);
            if (capacity == 0) return ClipboardOutcome.IoError;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return ClipboardOutcome.IoError;
            try
            {
                var length = 0;
                while (length < capacity && Marshal.ReadInt16(pointer, length * 2) != 0)
                {
                    length++;
                    if (length > maxChars) return ClipboardOutcome.TooLarge;
                }
                text = Marshal.PtrToStringUni(pointer, length);
                return ClipboardOutcome.Success;
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    public ClipboardOutcome TryWriteText(string text) => OnOwnerThread(window => Replace(window, text));

    public ClipboardOutcome TryClear() => OnOwnerThread(window => Replace(window, null));

    private static ClipboardOutcome Replace(IntPtr window, string? text)
    {
        if (!Open(window, attempts: 20)) return ClipboardOutcome.Busy;
        try
        {
            if (!EmptyClipboard()) return ClipboardOutcome.IoError;
            if (text is null) return ClipboardOutcome.Success;
            var bytes = checked((nuint)((text.Length + 1) * 2));
            var memory = GlobalAlloc(GmemMoveable, bytes);
            if (memory == IntPtr.Zero) return ClipboardOutcome.IoError;
            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(memory);
                return ClipboardOutcome.IoError;
            }
            Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
            Marshal.WriteInt16(pointer, text.Length * 2, 0);
            GlobalUnlock(memory);
            // On success the system owns the memory.
            if (SetClipboardData(CfUnicodeText, memory) != IntPtr.Zero) return ClipboardOutcome.Success;
            GlobalFree(memory);
            return ClipboardOutcome.IoError;
        }
        finally { CloseClipboard(); }
    }

    private static bool Open(IntPtr owner, int attempts)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (OpenClipboard(owner)) return true;
            if (attempt + 1 < attempts) Thread.Sleep(5);
        }
        return false;
    }

    private ClipboardOutcome OnOwnerThread(Func<IntPtr, ClipboardOutcome> work)
    {
        if (!OperatingSystem.IsWindows()) return ClipboardOutcome.Unsupported;
        if (!EnsureOwner()) return ClipboardOutcome.IoError;
        var window = _window;
        var job = new Job(() => work(window));
        _jobs.Enqueue(job);
        if (!PostMessage(window, WmApp, IntPtr.Zero, IntPtr.Zero)) return ClipboardOutcome.IoError;
        if (job.Done.Wait(WriteTimeout)) return job.Outcome;
        // A write that has not started must not land after the program was told the clipboard was busy.
        return job.TryCancel() ? ClipboardOutcome.Busy : job.Done.Wait(WriteTimeout) ? job.Outcome : ClipboardOutcome.Busy;
    }

    private bool EnsureOwner()
    {
        lock (_startGate)
        {
            if (_owner is null)
            {
                _owner = new Thread(RunOwner) { IsBackground = true, Name = "AgentTerm clipboard owner" };
                _owner.Start();
            }
        }
        _ready.Wait();
        return _window != IntPtr.Zero;
    }

    private static readonly WindowProcedure Procedure = (window, message, wParam, lParam) =>
    {
        if (message == WmApp)
        {
            while (Shared._jobs.TryDequeue(out var job)) job.Run();
            return IntPtr.Zero;
        }
        return DefWindowProc(window, message, wParam, lParam);
    };

    private void RunOwner()
    {
        try
        {
            var instance = GetModuleHandle(null);
            var className = "AgentTermClipboardOwner";
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
                Instance = instance,
                ClassName = className,
            };
            RegisterClassEx(ref windowClass);
            _window = CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, MessageParent, IntPtr.Zero, instance, IntPtr.Zero);
        }
        finally { _ready.Set(); }
        if (_window == IntPtr.Zero) return;
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public IntPtr Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string? windowName, uint style, int x, int y, int width,
        int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
}
