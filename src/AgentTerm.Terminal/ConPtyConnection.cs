using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace AgentTerm.Terminal;

/// <summary>A single Windows pseudoconsole session. Output memory is valid only during the callback.</summary>
public sealed partial class ConPtyConnection : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly short columns;
    private readonly short rows;
    private readonly Channel<byte[]> input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr pseudoConsole;
    private IntPtr process;
    private volatile bool started;
    private volatile bool disposed;
    private volatile bool inputClosed;
    private Task? disposal;
    private Exception? transportError;

    public ConPtyConnection(ushort columns, ushort rows)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ConPTY requires Windows.");
        (this.columns, this.rows) = Size(columns, rows);
    }

    public event Action<ReadOnlyMemory<byte>>? OutputReceived;
    public Task<int> Completion => completion.Task;
    public int ProcessId { get; private set; }

    public void Start(string executable, string arguments, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("The connection has already been started.");
            started = true;
            IntPtr inputRead = IntPtr.Zero, inputWrite = IntPtr.Zero;
            IntPtr outputRead = IntPtr.Zero, outputWrite = IntPtr.Zero;
            IntPtr attributes = IntPtr.Zero;
            IntPtr environment = IntPtr.Zero;
            bool attributesInitialized = false;
            try
            {
                Check(Native.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0));
                Check(Native.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0));
                Marshal.ThrowExceptionForHR(Native.CreatePseudoConsole(new Coord(columns, rows), inputRead, outputWrite, 0, out pseudoConsole));
                nuint bytes = 0;
                Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
                attributes = Marshal.AllocHGlobal(checked((nint)bytes));
                Check(Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes));
                attributesInitialized = true;
                Check(Native.UpdateProcThreadAttribute(attributes, 0, (nuint)0x00020016, pseudoConsole, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
                // Explicit null standard handles prevent redirected parent handles from being duplicated into the child.
                var startup = new StartupInfoEx { StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>(), flags = 0x100 }, AttributeList = attributes };
                var command = $"\"{executable}\" {arguments}\0".ToCharArray();
                environment = CreateChildEnvironment();
                Check(Native.CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, 0x00080400, environment, workingDirectory, ref startup, out var info));
                process = info.Process;
                ProcessId = checked((int)info.ProcessId);
                Native.CloseHandle(info.Thread);
                // The pseudoconsole owns its own references after process creation.
                Native.CloseHandle(inputRead); inputRead = IntPtr.Zero;
                Native.CloseHandle(outputWrite); outputWrite = IntPtr.Zero;
                IntPtr readerHandle = outputRead, writerHandle = inputWrite;
                var reader = Task.Factory.StartNew(() => ReadOutput(readerHandle), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                var writer = Task.Factory.StartNew(() => WriteInput(writerHandle), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                _ = Task.Factory.StartNew(() => Finish(reader, writer, readerHandle, writerHandle), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                outputRead = inputWrite = IntPtr.Zero;
            }
            catch (Exception ex)
            {
                if (process != IntPtr.Zero) { Native.TerminateProcess(process, 1); Native.CloseHandle(process); process = IntPtr.Zero; }
                if (inputRead != IntPtr.Zero) Native.CloseHandle(inputRead);
                if (outputWrite != IntPtr.Zero) Native.CloseHandle(outputWrite);
                if (outputRead != IntPtr.Zero) { Native.CloseHandle(outputRead); outputRead = IntPtr.Zero; }
                if (pseudoConsole != IntPtr.Zero) { Native.ClosePseudoConsole(pseudoConsole); pseudoConsole = IntPtr.Zero; }
                if (inputWrite != IntPtr.Zero) Native.CloseHandle(inputWrite);
                CloseInput();
                completion.TrySetException(ex);
                throw;
            }
            finally
            {
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                if (attributesInitialized) Native.DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            }
        }
    }

    private static IntPtr CreateChildEnvironment()
    {
        // Preserve the native environment, including Windows drive-current-directory
        // entries that are not exposed by every managed environment enumeration.
        var source = Native.GetEnvironmentStrings();
        if (source == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var offset = 0;
            while (true)
            {
                var entry = Marshal.PtrToStringUni(IntPtr.Add(source, offset))!;
                if (entry.Length == 0) break;
                var separator = entry.IndexOf('=', entry[0] == '=' ? 1 : 0);
                if (separator > 0) variables[entry[..separator]] = entry;
                offset = checked(offset + (entry.Length + 1) * sizeof(char));
            }
        }
        finally { Native.FreeEnvironmentStrings(source); }
        variables.Remove("NO_COLOR");
        variables["TERM"] = "TERM=xterm-256color";
        variables["COLORTERM"] = "COLORTERM=truecolor";
        variables["TERM_PROGRAM"] = "TERM_PROGRAM=AgentTerm";
        var block = string.Join('\0', variables.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).Select(entry => entry.Value)) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }
    public void Send(ReadOnlySpan<byte> data) => Enqueue(data, reply: false);

    // A final output frame can request a reply after the child has exited.
    internal void SendReply(ReadOnlySpan<byte> data) => Enqueue(data, reply: true);

    private void Enqueue(ReadOnlySpan<byte> data, bool reply)
    {
        if (disposed)
        {
            if (reply) return;
            throw new ObjectDisposedException(nameof(ConPtyConnection));
        }
        if (!started) throw new InvalidOperationException("The connection has not been started.");
        if (inputClosed)
        {
            if (reply) return;
            throw new InvalidOperationException("The terminal input is closed.");
        }
        if (data.IsEmpty) return;
        if (!input.Writer.TryWrite(data.ToArray()))
        {
            if (reply && (inputClosed || disposed)) return;
            throw new InvalidOperationException("The terminal input queue is full or closed.");
        }
    }

    private void CloseInput()
    {
        inputClosed = true;
        input.Writer.TryComplete();
    }
    public void Resize(ushort columns, ushort rows)
    {
        var (width, height) = Size(columns, rows);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!started || pseudoConsole == IntPtr.Zero) throw new InvalidOperationException("The connection is not running.");
            Marshal.ThrowExceptionForHR(Native.ResizePseudoConsole(pseudoConsole, new Coord(width, height)));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new ValueTask(disposal);
            disposed = true;
            CloseInput();
            if (!started)
            {
                completion.TrySetException(new ObjectDisposedException(nameof(ConPtyConnection)));
                return new ValueTask(disposal = Task.CompletedTask);
            }
            if (process != IntPtr.Zero) Native.TerminateProcess(process, 1);
            return new ValueTask(disposal = AwaitCompletion());
        }
    }

    private async Task AwaitCompletion()
    {
        try { await completion.Task.ConfigureAwait(false); }
        catch { }
    }

    private void Finish(Task<Exception?> reader, Task<Exception?> writer, IntPtr outputRead, IntPtr inputWrite)
    {
        Exception? error = null;
        int exitCode = 1;
        try
        {
            if (Native.WaitForSingleObject(process, uint.MaxValue) != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Check(Native.GetExitCodeProcess(process, out uint code));
            exitCode = unchecked((int)code);
            CloseInput();
            IntPtr console;
            lock (gate) { console = pseudoConsole; pseudoConsole = IntPtr.Zero; }
            // This can block while writing the final frame. The reader remains active.
            if (console != IntPtr.Zero) Native.ClosePseudoConsole(console);
            Exception? readError = reader.GetAwaiter().GetResult();
            Exception? writeError = writer.GetAwaiter().GetResult();
            error = transportError ?? readError ?? writeError;
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            CloseInput();
            Native.CloseHandle(outputRead);
            Native.CloseHandle(inputWrite);
            lock (gate) { if (process != IntPtr.Zero) { Native.CloseHandle(process); process = IntPtr.Zero; } }
            if (error is null) completion.TrySetResult(exitCode); else completion.TrySetException(error);
        }
    }

    private Exception? ReadOutput(IntPtr handle)
    {
        var buffer = new byte[16384];
        Exception? callbackError = null;
        while (true)
        {
            if (!Native.ReadFile(handle, buffer, buffer.Length, out int count, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 109) return callbackError;
                var exception = new Win32Exception(error);
                FailTransport(exception);
                return exception;
            }
            if (count == 0) return callbackError;
            if (callbackError is null)
            {
                try { OutputReceived?.Invoke(buffer.AsMemory(0, count)); }
                catch (Exception ex) { callbackError = ex; FailTransport(ex); }
            }
        }
    }

    private Exception? WriteInput(IntPtr handle)
    {
        try
        {
            while (input.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (input.Reader.TryRead(out byte[]? bytes))
                {
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        if (!Native.WriteFile(handle, bytes.AsSpan(offset).ToArray(), bytes.Length - offset, out int written, IntPtr.Zero))
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error == 109 || error == 232) return null;
                            var exception = new Win32Exception(error);
                            FailTransport(exception);
                            return exception;
                        }
                        if (written == 0) { var exception = new IOException("Writing terminal input made no progress."); FailTransport(exception); return exception; }
                        offset += written;
                    }
                }
            }
            return null;
        }
        catch (Exception ex) { FailTransport(ex); return ex; }
    }

    private void FailTransport(Exception error)
    {
        Interlocked.CompareExchange(ref transportError, error, null);
        lock (gate) { if (process != IntPtr.Zero) Native.TerminateProcess(process, 1); }
    }

    private static (short, short) Size(ushort columns, ushort rows)
    {
        if (columns == 0 || rows == 0 || columns > short.MaxValue || rows > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal dimensions must be between 1 and 32767.");
        return ((short)columns, (short)rows);
    }

    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Coord(short X, short Y);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr reserved, desktop, title;
        public int x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags;
        public short showWindow, reserved2Size;
        public IntPtr reserved2, stdInput, stdOutput, stdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    private static partial class Native
    {
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static partial bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, int size);
        // The bundled host forwards OSC replies that older inbox ConPTY versions discard.
        [LibraryImport("conpty.dll", EntryPoint = "ConptyCreatePseudoConsole")] public static partial int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr console);
        [LibraryImport("conpty.dll", EntryPoint = "ConptyResizePseudoConsole")] public static partial int ResizePseudoConsole(IntPtr console, Coord size);
        [LibraryImport("conpty.dll", EntryPoint = "ConptyClosePseudoConsole")] public static partial void ClosePseudoConsole(IntPtr console);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static partial bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static partial bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);
        [LibraryImport("kernel32.dll")] public static partial void DeleteProcThreadAttributeList(IntPtr list);
        [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "GetEnvironmentStringsW")] public static partial IntPtr GetEnvironmentStrings();
        [LibraryImport("kernel32.dll", EntryPoint = "FreeEnvironmentStringsW")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool FreeEnvironmentStrings(IntPtr environment);
        [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateProcessW", StringMarshalling = StringMarshalling.Utf16)][return: MarshalAs(UnmanagedType.Bool)] public static partial bool CreateProcess(string? application, char[] command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static partial bool ReadFile(IntPtr handle, [Out] byte[] buffer, int count, out int read, IntPtr overlapped);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static partial bool WriteFile(IntPtr handle, byte[] buffer, int count, out int written, IntPtr overlapped);
        [LibraryImport("kernel32.dll", SetLastError = true)] public static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static partial bool TerminateProcess(IntPtr process, uint exitCode);
        [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool CloseHandle(IntPtr handle);
    }
}
