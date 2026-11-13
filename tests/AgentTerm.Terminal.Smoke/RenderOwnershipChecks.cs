using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentTerm.Terminal;

/// <summary>Render-state ownership, render holds, capture failure recovery and native lifetime.</summary>
internal static class RenderOwnershipChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Render ownership: " + message);
    }

    public static void Run()
    {
        CheckNativePin();
        CheckExactBoundary();
        CheckReleaseAndRestartInOneWrite();
        CheckRepeatedStartDoesNotExtend();
        CheckIndefiniteHoldClearsNativeMode();
        CheckResizeAndResetRelease();
        CheckViewChangeOverridesHold();
        CheckPresentationReadsDuringHold();
        CheckHandoffDuringInFlightCapture();
        CheckAnchoredHandoff();
        CheckFailedBoundary();
        CheckSplitCaptureMetadata();
        CheckUnicodeClusters();
        CheckRetainedFrames();
        CheckFailureRecovery();
        MeasureAnchoredBoundaries();
        CheckHandleLifetime();
        CheckConcurrentHolds();
    }

    private static string Row(TerminalFrame frame, int row)
    {
        var text = new StringBuilder();
        for (var column = 0; column < frame.Columns; column++)
        {
            var cell = frame.Cells[row * frame.Columns + column];
            if (cell.Width != 0) text.Append(cell.Text.Length == 0 ? " " : cell.Text);
        }
        return text.ToString().TrimEnd();
    }

    private static string Screen(TerminalFrame frame) =>
        string.Join("\n", Enumerable.Range(0, frame.Rows).Select(row => Row(frame, row))).TrimEnd('\n');

    private static void Write(GhosttyTerminal terminal, string text) => terminal.Write(Encoding.UTF8.GetBytes(text));

    private static void CheckNativePin()
    {
        Check(GhosttyTerminal.RenderLayoutMismatch is null, "managed render mirrors differ from the linked library: " + GhosttyTerminal.RenderLayoutMismatch);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native-dependencies.json"))) directory = directory.Parent;
        Check(directory is not null, "native-dependencies.json was not found above the test output.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "native-dependencies.json")));
        var pinned = manifest.RootElement.GetProperty("ghostty").GetProperty("version").GetString();
        Check(GhosttyTerminal.NativeVersion.StartsWith(pinned + "", StringComparison.Ordinal),
            $"loaded libghostty-vt {GhosttyTerminal.NativeVersion} does not match the pinned {pinned}. Rebuild with scripts/build-native.ps1.");
    }

    private static void CheckExactBoundary()
    {
        using var terminal = new GhosttyTerminal(20, 3);
        Write(terminal, "before\x1b[?2026hafter");
        Check(terminal.IsRenderHeld && terminal.HoldCounters.DirectCaptures == 1, "a hold start must capture its boundary in the callback.");
        var held = terminal.ReadFrame();
        Check(Row(held, 0) == "before", $"the first capture while held must show the boundary, got <{Row(held, 0)}>.");
        Check(terminal.PendingFrameRefreshDelay is { TotalMilliseconds: > 0 and <= 1000 }, "a held frame must schedule its timeout.");
        Write(terminal, " more");
        Check(ReferenceEquals(held, terminal.ReadFrame()), "output during a hold must not replace the held frame.");
        Write(terminal, "\x1b[?2026l");
        var released = terminal.ReadFrame();
        Check(Row(released, 0) == "beforeafter more" && !terminal.IsRenderHeld, "releasing the hold must publish everything written during it.");
        Check(terminal.PendingFrameRefreshDelay is null, "a released hold must not schedule recapture.");
    }

    private static void CheckReleaseAndRestartInOneWrite()
    {
        using var terminal = new GhosttyTerminal(20, 3);
        terminal.ReadFrame();
        Write(terminal, "A\x1b[?2026hB\x1b[?2026lC\x1b[?2026hD");
        Check(Row(terminal.ReadFrame(), 0) == "ABC", "a release and new hold in one write must show the finished frame between them.");
        Write(terminal, "\x1b[?2026l");
        Check(Row(terminal.ReadFrame(), 0) == "ABCD", "the second release must publish the rest.");
    }

    private static void CheckRepeatedStartDoesNotExtend()
    {
        using var terminal = new GhosttyTerminal(20, 3);
        Write(terminal, "\x1b[?2026h");
        var held = terminal.ReadFrame();
        Thread.Sleep(600);
        Write(terminal, "x\x1b[?2026h");
        Check(terminal.HoldCounters.Starts == 1, "setting mode 2026 during a hold is not a new hold.");
        Check(ReferenceEquals(held, terminal.ReadFrame()), "the hold must stay in effect before its deadline.");
        Thread.Sleep(500);
        var live = terminal.ReadFrame();
        Check(Row(live, 0) == "x" && !terminal.IsRenderHeld && !terminal.SynchronizedOutputEnabled,
            "a repeated start must not push the deadline back, and the timeout must clear native mode 2026.");
    }

    private static void CheckIndefiniteHoldClearsNativeMode()
    {
        using var terminal = new GhosttyTerminal(20, 3);
        Write(terminal, "old");
        terminal.ReadFrame();
        Write(terminal, "\x1b[?2026h\x1b[2J\x1b[Hnew");
        Check(Row(terminal.ReadFrame(), 0) == "old", "a hold must keep the last finished frame.");
        Thread.Sleep((int)GhosttyTerminal.RenderHoldTimeout.TotalMilliseconds + 100);
        var live = terminal.ReadFrame();
        Check(Row(live, 0) == "new" && !terminal.SynchronizedOutputEnabled && terminal.HoldCounters.Timeouts == 1,
            "a silent hold must time out, publish live content and clear native mode.");
        Check(terminal.PendingFrameRefreshDelay is null, "a timed-out hold must not keep scheduling recaptures.");
        Write(terminal, "\x1b[?2026h!");
        Check(terminal.IsRenderHeld && terminal.HoldCounters.Starts == 2 && Row(terminal.ReadFrame(), 0) == "new",
            "after a host timeout the program can start a new hold.");
    }

    private static void CheckResizeAndResetRelease()
    {
        using var terminal = new GhosttyTerminal(20, 3);
        Write(terminal, "\x1b[?2026hresized");
        terminal.ReadFrame();
        terminal.Resize(24, 4);
        var resized = terminal.ReadFrame();
        Check(!terminal.IsRenderHeld && resized.Columns == 24 && Row(resized, 0) == "resized", "resize must release a hold.");
        Write(terminal, "\x1b[?2026h\u001bcz");
        var reset = terminal.ReadFrame();
        Check(!terminal.IsRenderHeld && Row(reset, 0) == "z", $"a full reset must release a hold, held={terminal.IsRenderHeld} row=<{Row(reset, 0)}> starts={terminal.HoldCounters.Starts}.");
    }

    private static void CheckViewChangeOverridesHold()
    {
        using var terminal = new GhosttyTerminal(10, 3);
        for (var i = 0; i < 8; i++) Write(terminal, $"line{i}\r\n");
        terminal.ReadFrame();
        Write(terminal, "\x1b[?2026hpending");
        var held = terminal.ReadFrame();
        terminal.ScrollLines(-2);
        var scrolled = terminal.ReadFrame();
        Check(!ReferenceEquals(held, scrolled) && scrolled.Viewport.Offset == held.Viewport.Offset - 2, "scrolling during a hold must show the scrolled view.");
        Check(ReferenceEquals(scrolled, terminal.ReadFrame()), "after a user view change the hold resumes on that frame.");
    }

    private static void CheckPresentationReadsDuringHold()
    {
        using var terminal = new GhosttyTerminal(12, 6);
        terminal.SetBottomAnchored(true);
        for (var i = 0; i < 10; i++) Write(terminal, $"history{i}\r\n");
        Write(terminal, "prompt>");
        var before = terminal.ReadFrame();
        Write(terminal, "\x1b[?2026h\r\nhalf\r\ndrawn");
        // Mouse encoding and anchor reads prepare presentation; neither may overwrite the pending boundary.
        terminal.ReadPresentedViewport();
        var anchor = terminal.AnchorActiveRow(0);
        terminal.ReadAnchor(anchor);
        terminal.ReleaseAnchor(anchor);
        var held = terminal.ReadFrame();
        Check(Screen(held) == Screen(before), $"presentation reads during a hold must keep the boundary, got <{Screen(held)}>.");
        Check(ReferenceEquals(held, terminal.ReadFrame()), "the held frame must stay published.");
        Write(terminal, "\x1b[?2026l");
        var released = terminal.ReadFrame();
        Check(Row(released, released.Rows - 1) == "drawn" && released.Viewport.IsAtBottom, "presentation resumes after release.");
    }

    private static void CheckHandoffDuringInFlightCapture()
    {
        using var terminal = new GhosttyTerminal(20, 4);
        Write(terminal, "base");
        terminal.ReadFrame();
        var capture = terminal.BeginFrame();
        Write(terminal, "\r\np\x1b[?2026hq");
        Check(terminal.HoldCounters.HandoffCaptures == 1 && terminal.HoldCounters.DirectCaptures == 0,
            "a hold start while a capture is in flight must capture into a fresh state.");
        var old = terminal.CompleteFrame(capture);
        Check(Screen(old) == "base", $"the in-flight capture must reflect its begin, got <{Screen(old)}>.");
        var boundary = terminal.ReadFrame();
        Check(terminal.HoldCounters.Adoptions == 1 && Screen(boundary) == "base\np", $"the adopted handoff must show the boundary, got <{Screen(boundary)}>.");
        Write(terminal, "\x1b[?2026l\r\nafter");
        Check(Screen(terminal.ReadFrame()) == "base\npq\nafter", "an adopted state must continue incrementally without losing rows.");
        Write(terminal, "\x1b[1;1HBASE");
        Check(Screen(terminal.ReadFrame()) == "BASE\npq\nafter", "rows untouched since adoption must stay correct.");
    }

    private static void CheckAnchoredHandoff()
    {
        using var terminal = new GhosttyTerminal(20, 6);
        using var reference = new GhosttyTerminal(20, 6);
        foreach (var t in new[] { terminal, reference })
        {
            t.SetBottomAnchored(true);
            for (var i = 0; i < 12; i++) Write(t, $"history{i}\r\n");
            Write(t, "prompt>");
            t.ReadFrame();
        }
        var capture = terminal.BeginFrame();
        Write(reference, "\r\nfinished");
        var expected = reference.ReadFrame();
        Write(terminal, "\r\nfinished\x1b[?2026h\r\nhalf");
        terminal.CompleteFrame(capture);
        var held = terminal.ReadFrame();
        Check(Screen(held) == Screen(expected) && held.Viewport == expected.Viewport,
            $"An in-flight anchored boundary must settle to the exact viewport. Expected <{Screen(expected)}> {expected.Viewport}, got <{Screen(held)}> {held.Viewport}.");
        Write(terminal, "\x1b[?2026l");
        Check(Row(terminal.ReadFrame(), 5) == "half", "Release resumes bottom anchoring.");
    }

    private static void CheckFailedBoundary()
    {
        foreach (var inFlight in new[] { false, true })
        {
            using var terminal = new GhosttyTerminal(30, 4) { RenderAllocator = FailingAllocator.Instance };
            Write(terminal, "published");
            var stable = terminal.ReadFrame();
            var capture = inFlight ? terminal.BeginFrame() : default;
            FailingAllocator.Fail = true;
            try { Write(terminal, "\r\n" + new string('x', 25) + "é̂\x1b[?2026h\x1b[Hhalf"); }
            finally { FailingAllocator.Fail = false; }
            Check(terminal.HoldCounters.CaptureFailures == 1, "Fixture must fail the boundary allocation.");
            if (inFlight) stable = terminal.CompleteFrame(capture);
            Check(ReferenceEquals(stable, terminal.ReadFrame()), "Failed boundary must retain the published frame after allocator recovery.");
            Check(ReferenceEquals(stable, terminal.ReadFrame()), "Failure must keep holding until release.");
            if (inFlight) Thread.Sleep((int)GhosttyTerminal.RenderHoldTimeout.TotalMilliseconds + 20);
            else Write(terminal, "\x1b[?2026l");
            Check(!ReferenceEquals(stable, terminal.ReadFrame()), "Release or timeout publishes recovered output.");
        }
    }

    private static void CheckSplitCaptureMetadata()
    {
        using var terminal = new GhosttyTerminal(20, 3);
        Write(terminal, "first");
        var capture = terminal.BeginFrame();
        Write(terminal, "\x1b]4;1;rgb:ff/00/00\x1b\\ second\x1b[31mred");
        terminal.Resize(30, 5);
        var frame = terminal.CompleteFrame(capture);
        Check(frame.Columns == 20 && frame.Rows == 3 && frame.Viewport.Rows == 3 && Screen(frame) == "first",
            "a frame must describe the moment of begin_update, not later output or resize.");
        var next = terminal.ReadFrame();
        Check(next.Columns == 30 && next.Rows == 5 && Row(next, 0) == "first secondred", "the next capture must not miss changes made during completion.");
        Check(frame.Cells.Length == 60 && Row(frame, 0) == "first", "the completed frame must stay stable after a newer capture.");
    }

    private static void CheckUnicodeClusters()
    {
        using var terminal = new GhosttyTerminal(40, 4);
        var combining = "e" + new string('́', 40);
        var family = "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466";
        var flag = "\U0001F1EF\U0001F1F5";
        var thumbs = "\U0001F44D\U0001F3FD";
        var heart = "❤️";
        // Mode 2027 asks for Unicode grapheme clustering; without it libghostty keeps legacy segmentation.
        Write(terminal, $"[?2027h{combining}|{family}|{flag}|{thumbs}|{heart}|界");
        var frame = terminal.ReadFrame();
        var texts = frame.Cells.Take(frame.Columns).Where(cell => cell.Width != 0).Select(cell => cell.Text).ToArray();
        Check(Encoding.UTF8.GetByteCount(combining) > 64 && texts[0] == combining, $"a cluster over 64 UTF-8 bytes must stay whole, got {texts[0].Length} chars.");
        Check(texts.Contains(family) && texts.Contains(flag) && texts.Contains(thumbs) && texts.Contains(heart), $"ZWJ, flag, skin-tone and variation clusters must stay whole, got {string.Join(" ", texts.Skip(1).Select(t => string.Join(",", t.EnumerateRunes().Select(r => r.Value.ToString("X")))))}.");
        var wide = Array.FindIndex(frame.Cells, cell => cell.Text == "界");
        Check(frame.Cells[wide].Width == 2 && frame.Cells[wide + 1].Width == 0, "CJK cells need a spacer tail.");
        Check(Row(frame, 0) == combining + "|" + family + "|" + flag + "|" + thumbs + "|" + heart + "|界", "row text must round-trip.");
    }

    private static void CheckRetainedFrames()
    {
        TerminalFrame retained;
        string snapshot;
        using (var terminal = new GhosttyTerminal(12, 3))
        {
            Write(terminal, "keeṕ 界");
            retained = terminal.ReadFrame();
            snapshot = Screen(retained);
            for (var i = 0; i < 50; i++) Write(terminal, $"\x1b[2J\x1b[H{i} overwrite \x1b[?5h");
            terminal.Resize(5, 2);
            terminal.ReadFrame();
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Check(Screen(retained) == snapshot && retained.Columns == 12, "frames must own their data after later captures, resize and disposal.");
    }

    private static void CheckFailureRecovery()
    {
        using var terminal = new GhosttyTerminal(30, 4) { RenderAllocator = FailingAllocator.Instance };
        Write(terminal, "stable");
        var stable = terminal.ReadFrame();
        Write(terminal, "\r\n" + new string('x', 25) + "é̂\r\n\x1b[41mcolored\x1b[0m");
        FailingAllocator.Fail = true;
        try
        {
            var during = terminal.ReadFrame();
            Check(ReferenceEquals(stable, during) && terminal.CaptureFailures >= 1, "a failed capture must keep the previous complete frame.");
            Check(terminal.PendingFrameRefreshDelay is not null, "a failed capture must schedule a retry.");
            terminal.ReadFrame();
        }
        finally { FailingAllocator.Fail = false; }
        var recovered = terminal.ReadFrame();
        Check(Screen(recovered) == "stable\n" + new string('x', 25) + "é̂\ncolored", $"recovery must rebuild every row, got <{Screen(recovered)}>.");
        Check(recovered.Cells[2 * 30].Background != recovered.Background, "recovered rows keep their colors.");
        Check(terminal.PendingFrameRefreshDelay is null, "a recovered capture clears the retry.");

        using var fresh = new GhosttyTerminal(10, 2) { RenderAllocator = FailingAllocator.Instance };
        FailingAllocator.Fail = true;
        try
        {
            var threw = false;
            try { fresh.ReadFrame(); } catch (InvalidOperationException) { threw = true; }
            Check(threw, "a failure with no previous frame must surface instead of publishing nothing.");
        }
        finally { FailingAllocator.Fail = false; }
        Check(fresh.ReadFrame().Columns == 10, "a terminal recovers after its first capture failed.");
    }

    private static void MeasureAnchoredBoundaries()
    {
        // Compare callback cost for the same dirty, bottom-anchored viewport. The in-flight path must
        // scan through local native ownership. Report spread; this is not a whole-render throughput claim.
        foreach (var inFlight in new[] { false, true })
        {
            var trials = new List<double>();
            for (var trial = 0; trial < 6; trial++)
            {
                using var terminal = new GhosttyTerminal(120, 40);
                terminal.SetBottomAnchored(true);
                for (var row = 0; row < 60; row++) Write(terminal, new string('x', 100) + "\r\n");
                terminal.ReadFrame();
                long ticks = 0;
                for (var i = 0; i < 100; i++)
                {
                    var capture = inFlight ? terminal.BeginFrame() : default;
                    Write(terminal, "line\r\n");
                    var start = System.Diagnostics.Stopwatch.GetTimestamp();
                    Write(terminal, "\x1b[?2026h");
                    ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
                    if (inFlight) terminal.CompleteFrame(capture);
                    terminal.ReadFrame();
                    Write(terminal, "\x1b[?2026l");
                    terminal.ReadFrame();
                }
                Check((inFlight ? terminal.HoldCounters.HandoffCaptures : terminal.HoldCounters.DirectCaptures) == 100,
                    "Every timed boundary must execute its requested capture path.");
                if (trial != 0) trials.Add(ticks * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency / 100);
            }
            trials.Sort();
            Console.WriteLine($"Anchored hold callback {(inFlight ? "handoff" : "direct")}, 120x40, five trials x 100 after warmup, microseconds mean-per-trial min/median/max {trials[0]:F1}/{trials[2]:F1}/{trials[^1]:F1}.");
        }
    }

    private static void CheckHandleLifetime()
    {
        static WeakReference Abandon()
        {
            var terminal = new GhosttyTerminal(10, 2);
            terminal.Write("gone"u8);
            terminal.ReadFrame();
            return new WeakReference(terminal);
        }
        var weak = Abandon();
        for (var i = 0; i < 3 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Check(!weak.IsAlive, "an abandoned terminal must be collectable.");
        for (var i = 0; i < 200; i++)
        {
            new GhosttyTerminal(8, 2).Write("x\x1b[?2026hy"u8);
            if (i % 20 == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        }
        var disposed = new GhosttyTerminal(8, 2);
        var last = disposed.ReadFrame();
        disposed.Dispose();
        var rejected = 0;
        foreach (var action in new Action[] { () => disposed.ReadFrame(), () => disposed.Write("x"u8), () => disposed.ReadTitle(), () => disposed.HasSelection(), () => _ = disposed.IsRenderHeld })
        {
            try { action(); } catch (ObjectDisposedException) { rejected++; }
        }
        Check(rejected == 5 && last.Columns == 8, "calls after disposal must be rejected without touching freed native memory.");
    }

    /// <summary>
    /// Mirrors TerminalSession: output parses under the terminal gate on one thread while another completes captures
    /// with only the render gate. Every program frame is one letter, drawn inside a hold, so a frame mixing letters
    /// means a half-drawn screen was published.
    /// </summary>
    private static void CheckConcurrentHolds()
    {
        const int columns = 24, rows = 6, frames = 400;
        using var terminal = new GhosttyTerminal(columns, rows);
        object renderGate = new(), gate = new();
        var stream = new StringBuilder();
        for (var frame = 0; frame < frames; frame++)
        {
            var letter = (char)('A' + frame % 26);
            stream.Append("\x1b[?2026h\x1b[H");
            for (var row = 0; row < rows; row++) stream.Append(new string(letter, columns)).Append(row + 1 < rows ? "\r\n" : "");
            stream.Append("\x1b[?2026l");
        }
        var bytes = Encoding.ASCII.GetBytes(stream.ToString());
        var done = false;
        Exception? writerError = null;
        var writer = new Thread(() =>
        {
            try
            {
                var random = new Random(7);
                for (var offset = 0; offset < bytes.Length;)
                {
                    var length = Math.Min(bytes.Length - offset, random.Next(1, 90));
                    lock (gate) terminal.Write(bytes.AsSpan(offset, length));
                    offset += length;
                    if (random.Next(8) == 0) Thread.Yield();
                }
            }
            catch (Exception error) { writerError = error; }
            finally { Volatile.Write(ref done, true); }
        });
        writer.Start();
        var captures = 0;
        while (!Volatile.Read(ref done) || captures == 0)
        {
            TerminalFrame frame;
            lock (renderGate)
            {
                GhosttyTerminal.FrameCapture capture;
                lock (gate) capture = terminal.BeginFrame();
                frame = terminal.CompleteFrame(capture);
            }
            captures++;
            var first = frame.Cells[0].Text;
            Check(first.Length == 0 || frame.Cells.All(cell => cell.Text == first), $"capture {captures} published a half-drawn frame.");
        }
        writer.Join();
        if (writerError is not null) throw new InvalidOperationException("Render ownership: concurrent writer failed.", writerError);
        TerminalFrame final;
        lock (renderGate) lock (gate) final = terminal.ReadFrame();
        Check(final.Cells.All(cell => cell.Text == ((char)('A' + (frames - 1) % 26)).ToString()), "the last program frame must be published.");
        var counters = terminal.HoldCounters;
        Console.WriteLine($"Concurrent holds: {captures} captures, {counters.Starts} holds, {counters.DirectCaptures} direct, {counters.HandoffCaptures} handoff, {counters.Adoptions} adopted.");
    }

    /// <summary>A libghostty allocator backed by NativeMemory that fails every allocation while <see cref="Fail"/> is set.</summary>
    internal static unsafe class FailingAllocator
    {
        public static volatile bool Fail;
        public static long Allocations;
        public static readonly IntPtr Instance = Create();

        private static IntPtr Create()
        {
            var vtable = (IntPtr*)NativeMemory.AllocZeroed((nuint)(8 * sizeof(IntPtr)));
            vtable[0] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, nuint, byte, nuint, void*>)&Alloc;
            vtable[1] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, void*, nuint, byte, nuint, nuint, byte>)&Resize;
            vtable[2] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, void*, nuint, byte, nuint, nuint, void*>)&Remap;
            vtable[3] = (IntPtr)(delegate* unmanaged[Cdecl]<void*, void*, nuint, byte, nuint, void>)&Free;
            var allocator = (IntPtr*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(IntPtr)));
            allocator[1] = (IntPtr)vtable;
            return (IntPtr)allocator;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void* Alloc(void* context, nuint length, byte alignment, nuint returnAddress)
        {
            Interlocked.Increment(ref Allocations);
            return Fail ? null : NativeMemory.AlignedAlloc(Math.Max(length, 1), (nuint)1 << alignment);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static byte Resize(void* context, void* memory, nuint length, byte alignment, nuint newLength, nuint returnAddress) =>
            newLength <= length ? (byte)1 : (byte)0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void* Remap(void* context, void* memory, nuint length, byte alignment, nuint newLength, nuint returnAddress) => null;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Free(void* context, void* memory, nuint length, byte alignment, nuint returnAddress) => NativeMemory.AlignedFree(memory);
    }
}
