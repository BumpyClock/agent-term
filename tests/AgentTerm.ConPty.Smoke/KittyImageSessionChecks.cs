using AgentTerm.Terminal;

/// <summary>Kitty graphics written by a real program must survive ConPTY and reach the session frame.</summary>
internal static class KittyImageSessionChecks
{
    internal static async Task RunAsync()
    {
        await using var session = new TerminalSession(60, 12, TestShell.Profile);
        session.SetCellPixelSize(9, 18);
        await TestShell.StartLegacyAsync(session);

        var rgba = Convert.ToBase64String([10, 20, 30, 255, 40, 50, 60, 128]);
        session.Send($"[Console]::Write([char]27 + '_Ga=T,f=32,s=2,v=1,i=41,q=2;{rgba}' + [char]27 + '\\')\r");
        var direct = await WaitAsync(session, images => images.Placements.Any(p => p.Image.Id == 41), "A raw RGBA transmission should cross ConPTY.");
        var pixels = direct.Placements.First(p => p.Image.Id == 41).Image.Pixels.ToArray();
        if (!pixels.SequenceEqual(new byte[] { 10, 20, 30, 255, 20, 25, 30, 128 }))
            throw new Exception($"ConPTY must deliver Kitty pixels intact, got {string.Join(",", pixels)}.");
        if (direct.CellPixelWidth != 9 || direct.CellPixelHeight != 18)
            throw new Exception("Session images must use the cell size the host set.");

        // Chunked PNG, the way icat-style tools send images.
        var png = Convert.ToBase64String(Png1x1Red);
        var split = png.Length / 2 / 4 * 4;
        session.Send($"[Console]::Write([char]27 + '_Ga=T,f=100,i=42,q=2,m=1;{png[..split]}' + [char]27 + '\\' + [char]27 + '_Gm=0;{png[split..]}' + [char]27 + '\\')\r");
        var chunked = await WaitAsync(session, images => images.Placements.Any(p => p.Image.Id == 42), "A chunked PNG transmission should cross ConPTY.");
        var red = chunked.Placements.First(p => p.Image.Id == 42).Image;
        if (red.Width != 1 || red.Height != 1 || !red.Pixels.ToArray().SequenceEqual(new byte[] { 255, 0, 0, 255 }))
            throw new Exception("A chunked PNG must decode to its pixels.");

        session.Send("[Console]::Write([char]27 + '_Ga=d,d=A,q=2' + [char]27 + '\\')\r");
        await WaitAsync(session, images => images.IsEmpty, "Deleting every image should clear the session frame.");
        if (session.ImageCacheBytes != 0) throw new Exception("Deleted images must free the session's pixel cache.");

        // Frames captured while a program replaces images and scrolls must stay coherent: every published placement
        // holds the pixels of its own image version.
        using var stop = new CancellationTokenSource();
        var captures = 0;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var placement in session.ReadFrame().Images.Placements)
                {
                    var first = placement.Image.Pixels.Span[0];
                    if (placement.Image.Width != 1 || placement.Image.Pixels.Span[1] != first)
                        throw new Exception("A captured image must hold one coherent pixel version.");
                }
                captures++;
            }
        });
        session.Send("foreach ($n in 1..300) { $b = [Convert]::ToBase64String([byte[]]@(($n % 256), ($n % 256), 0, 255)); " +
            "[Console]::Write([char]27 + '_Ga=T,f=32,s=1,v=1,i=' + (50 + $n % 7) + ',q=2;' + $b + [char]27 + '\\' + \"line $n`n\") }\r");
        await WaitAsync(session, images => images.Placements.Any(p => p.Image.Id == 50 + 300 % 7 && p.Image.Pixels.Span[0] == 300 % 256),
            "The last replaced image should reach the frame.");
        stop.Cancel();
        await reader;
        if (captures < 10) throw new Exception($"The concurrent reader must capture frames during the flood, got {captures}.");
        Console.WriteLine($"Kitty image session checks passed raw, chunked PNG, delete and {captures} concurrent captures during 300 replacements through ConPTY.");
    }

    // 1x1 opaque red PNG, encoded by Pillow.
    private static readonly byte[] Png1x1Red = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");

    private static async Task<TerminalImages> WaitAsync(TerminalSession session, Func<TerminalImages, bool> condition, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                var images = session.ReadFrame().Images;
                if (condition(images)) return images;
                await Task.Delay(40, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            throw new Exception($"{message} Screen {session.ReadText()}");
        }
    }
}
