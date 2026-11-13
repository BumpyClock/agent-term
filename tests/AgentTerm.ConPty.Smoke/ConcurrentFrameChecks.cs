using System.Diagnostics;
using AgentTerm.Terminal;

internal static class ConcurrentFrameChecks
{
    public static async Task RunAsync()
    {
        await using var session = new TerminalSession(120, 30, TestShell.Profile);
        session.SetBottomAnchored(true);
        await TestShell.StartLegacyAsync(session);
        Exception? failure = null;
        var frames = 0;
        using var stop = new CancellationTokenSource();
        var reader = Task.Factory.StartNew(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var frame = session.ReadFrame();
                    if (frame.Cells.Length != frame.Columns * frame.Rows) throw new Exception("Frame grid is incomplete.");
                    frames++;
                }
            }
            catch (Exception error) { failure = error; }
        }, TaskCreationOptions.LongRunning);

        session.Send("1..4000 | ForEach-Object { 'FLOOD_' + $_.ToString('D5') + ' ' + ('x' * 90) }; 'FLOOD_DONE'\r");
        var deadline = Stopwatch.StartNew();
        while (!session.ReadText().Contains("FLOOD_DONE") || session.PromptState != ShellPromptState.Ready)
        {
            if (failure is not null || deadline.Elapsed > TimeSpan.FromSeconds(60)) break;
            session.ScrollLines(0);
            session.ReadFrame();
            await Task.Delay(5);
        }
        stop.Cancel();
        await reader;
        if (failure is not null) throw new Exception("Concurrent frame capture failed.", failure);
        if (deadline.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Flood output did not finish. " + session.ReadText());

        var settled = Stopwatch.StartNew();
        string expected, actual;
        do
        {
            expected = Normalize(session.ReadText());
            actual = Normalize(Text(session.ReadFrame()));
            if (expected == actual) break;
            await Task.Delay(50);
        } while (settled.Elapsed < TimeSpan.FromSeconds(10));
        if (expected != actual) throw new Exception($"Final frame differs from terminal text.\nframe:\n{actual}\ntext:\n{expected}");
        if (frames < 10) throw new Exception($"Concurrent capture ran only {frames} frames during the flood.");
        Console.WriteLine($"Concurrent frame checks passed {frames} captures during a 4000-line flood with an exact final frame.");
    }

    private static string Text(TerminalFrame frame) => string.Join("\n", Enumerable.Range(0, frame.Rows)
        .Select(row => string.Concat(frame.Cells.Skip(row * frame.Columns).Take(frame.Columns).Select(cell => cell.Text))));

    private static string Normalize(string text) => string.Join("\n", text.Replace("\r", "").Split('\n').Select(line => line.TrimEnd())).TrimEnd();
}
