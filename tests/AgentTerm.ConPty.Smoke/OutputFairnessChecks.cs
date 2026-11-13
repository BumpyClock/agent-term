using System.Reflection;
using System.Text;
using AgentTerm.Terminal;

internal static class OutputFairnessChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task RunAsync()
    {
        await CheckQueuedInputAndFrameAsync();
        await CheckBlockedWorkerAsync();
        await CheckParserBoundariesAsync();
        Console.WriteLine("Output fairness checks passed queued input and frame progress, blocked-worker independence, parser continuation and synchronized-output parity.");
    }

    private static async Task CheckQueuedInputAndFrameAsync()
    {
        var executable = Environment.ProcessPath!;
        var prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? "\"" + Assembly.GetExecutingAssembly().Location + "\" " : "";
        var profile = new ShellProfile("output-fairness", "Output fairness", executable,
            prefix + "--transport-hold-fixture", Environment.CurrentDirectory);
        await using var session = new TerminalSession(80, 24, profile);
        var parse = Parser(session);
        var connection = Field<ConPtyConnection>(session, "_connection");
        connection.OutputReceived -= parse;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportOutput = new StringBuilder();
        connection.OutputReceived += bytes =>
        {
            transportOutput.Append(Encoding.UTF8.GetString(bytes.Span));
            if (transportOutput.ToString().Contains("TRANSPORT_READY")) ready.TrySetResult();
        };
        session.Start();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        using var workerStarted = new ManualResetEventSlim();
        Task<string>? input = null;
        Task<TerminalFrameSnapshot>? capture = null;
        Field<TerminalProtocolStream>(session, "_stream").Output += _ =>
        {
            if (input is not null) return;
            input = session.DispatchAsync(owner =>
            {
                workerStarted.Set();
                owner.Send("worker-input");
                return owner.ReadText();
            });
            capture = session.CaptureFrameAsync();
            if (!workerStarted.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Input worker did not contend with parsing.");
        };
        var payload = Encoding.UTF8.GetBytes("BEGIN\r\n" + string.Concat(Enumerable.Repeat("continuous output\r\n", 700)) + "FINAL");
        await Task.Run(() => parse(payload)).WaitAsync(TimeSpan.FromSeconds(15));
        var inputScreen = await input!.WaitAsync(TimeSpan.FromSeconds(10));
        var intermediate = await capture!.WaitAsync(TimeSpan.FromSeconds(10));
        if (inputScreen.Contains("FINAL") || FrameText(intermediate.Frame).Contains("FINAL"))
            throw new Exception("Queued same-pane input or frame capture waited for the entire output buffer.");
        if (!inputScreen.Contains("continuous output") || !FrameText(intermediate.Frame).Contains("continuous output"))
            throw new Exception("Contention fixture did not capture parsed output while input and frame capture made progress.");
        var final = await session.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await using var expected = new TerminalSession(80, 24, TestShell.Profile);
        Field<TerminalProtocolStream>(expected, "_stream").Write(payload);
        var expectedFrame = expected.ReadFrame();
        if (!session.ReadText().Contains("FINAL") || Normalize(FrameText(final.Frame)) != Normalize(session.ReadText()))
            throw new Exception("Output fairness lost the final terminal update or final frame parity.");
        if (session.ReadText() != expected.ReadText() || !final.Frame.Cells.SequenceEqual(expectedFrame.Cells) || final.Frame.Cursor != expectedFrame.Cursor)
            throw new Exception("Contended output differs from an uninterrupted native parse.");
    }

    private static async Task CheckBlockedWorkerAsync()
    {
        await using var session = new TerminalSession(80, 24, TestShell.Profile);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocked = session.DispatchAsync(_ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Blocked worker release missing.");
        });
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Worker did not start.");
            var parse = Parser(session);
            var payload = Encoding.UTF8.GetBytes(new string('x', 16384) + "FINAL");
            await Task.Run(() => parse(payload)).WaitAsync(TimeSpan.FromSeconds(10));
            if (!session.ReadText().Contains("FINAL")) throw new Exception("Output parsing depended on a blocked session operation.");
        }
        finally { release.Set(); }
        await blocked;
    }

    private static async Task CheckParserBoundariesAsync()
    {
        foreach (var sequence in new[] { "🙂", "\u001b[31mRED\u001b[0m", "\u001b]2;title🙂\u001b\\", "\u001b[?2026hHELD", "\u001bPignored\u001b\\", "\u001b]7501;state=working:app=fixture\a" })
        for (var offset = 0; offset < Encoding.UTF8.GetByteCount(sequence); offset++)
        {
            await using var actual = new TerminalSession(80, 24, TestShell.Profile);
            await using var expected = new TerminalSession(80, 24, TestShell.Profile);
            var initial = "BEFORE\r\n";
            var payload = Encoding.UTF8.GetBytes(initial + new string('\0', 2048 - Encoding.UTF8.GetByteCount(initial) - offset) + sequence);
            var expectedStream = Field<TerminalProtocolStream>(expected, "_stream");
            expectedStream.Write(payload);
            Parser(actual)(payload);
            if (actual.ReadText() != expected.ReadText() || actual.Title != expected.Title ||
                !actual.ReadProgramStatuses().Select(status => status with { UpdatedAt = default })
                    .SequenceEqual(expected.ReadProgramStatuses().Select(status => status with { UpdatedAt = default })))
                throw new Exception("Parser slice changed terminal text, title or program status at byte offset " + offset);
            var actualFrame = actual.ReadFrame();
            var expectedFrame = expected.ReadFrame();
            if (!actualFrame.Cells.SequenceEqual(expectedFrame.Cells) || actualFrame.Cursor != expectedFrame.Cursor)
                throw new Exception("Parser slice changed the presented frame or synchronized-output boundary at byte offset " + offset);
            var tail = "\u001b[?2026l\r\nFINAL🙂\u001b[3";
            expectedStream.Write(Encoding.UTF8.GetBytes(tail));
            Parser(actual)(Encoding.UTF8.GetBytes(tail));
            expectedStream.Write("2mGREEN"u8);
            Parser(actual)("2mGREEN"u8.ToArray());
            actualFrame = actual.ReadFrame();
            expectedFrame = expected.ReadFrame();
            if (actual.ReadText() != expected.ReadText() || !actualFrame.Cells.SequenceEqual(expectedFrame.Cells) || actualFrame.Cursor != expectedFrame.Cursor)
                throw new Exception("Parser continuation or released synchronized-output frame differs at byte offset " + offset);
        }
    }

    private static T Field<T>(TerminalSession session, string name) => (T)typeof(TerminalSession).GetField(name, PrivateInstance)!.GetValue(session)!;

    private static Action<ReadOnlyMemory<byte>> Parser(TerminalSession session) =>
        typeof(TerminalSession).GetMethod("ProcessOutput", PrivateInstance)!.CreateDelegate<Action<ReadOnlyMemory<byte>>>(session);

    private static string FrameText(TerminalFrame frame) => string.Join("\n", Enumerable.Range(0, frame.Rows)
        .Select(row => string.Concat(frame.Cells.Skip(row * frame.Columns).Take(frame.Columns).Select(cell => cell.Text))));

    private static string Normalize(string text) => string.Join("\n", text.Replace("\r", "").Split('\n').Select(line => line.TrimEnd())).TrimEnd();
}
