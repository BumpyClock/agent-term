using System.Security.Cryptography;
using AgentTerm.Terminal;
using Microsoft.Graphics.Canvas;

internal static partial class RasterWorkerChecks
{
    internal static void RunScrollReuse(CanvasDevice reference, string? hashesPath = null)
    {
        var hashes = new List<string>();
        foreach (var dpi in new[] { 96f, 120f, 144f })
        {
            using var terminal = new GhosttyTerminal(24, 6);
            using var pane = new Pane();
            Write(terminal, "\x1b[?25l" + string.Concat(Enumerable.Range(0, 6).Select(row =>
                $"\x1b[{row + 1};1H\x1b[0;38;5;{row + 1}mrow-{row} 界e\u0301 ┼█\x1b[0m")));
            var original = terminal.ReadFrame();
            Show("initial", original);
            Step("up", "\x1b[S");
            Step("stable-edit", "\x1b[2;1HEDIT\x1b[3;7H語");
            Step("style-edit", "\x1b[2;1H\x1b[1;3;4:3;9;53;5;58;2;20;30;40mSTYLE\x1b[0m");
            Step("multi-up", "\x1b[2S");
            Step("down", "\x1b[T\x1b[1;1Hnew");
            for (var index = 0; index < 6; index++)
                Step($"alternating-{index}", index % 2 == 0 ? "\x1b[S\x1b[6;1Hbottom" : "\x1b[T\x1b[1;1Htop");
            Step("partial-region", "\x1b[2;5r\x1b[2S\x1b[r");
            terminal.BeginSelection(0, 1); terminal.ExtendSelection(8, 2); terminal.EndSelection();
            Show("selection", terminal.ReadFrame());
            Step("selected-up", "\x1b[S");
            terminal.ClearSelection();
            Step("palette-content", "\x1b[2;1H\x1b[38;5;2;48;5;2mcolor\x1b[0m");
            Step("palette-up", "\x1b[S\x1b]4;2;rgb:ab/cd/ef\x1b\\");
            Step("image", "\x1b[2;1H\x1b_Ga=T,f=32,s=1,v=1,c=2,r=1,i=7,q=2;/2QA/w==\x1b\\");
            Step("image-up", "\x1b[S");
            Step("reverse-up", "\x1b[?5h\x1b[S");
            Step("alternate-up", "\x1b[?5l\x1b[?1049h\x1b[Halt\x1b[2;1Hscreen\x1b[S");
            Step("alternate-down", "\x1b[T\x1b[1;1Hinsert");
            Step("primary-return", "\x1b[?1049l\x1b[S");
            Write(terminal, "\x1b[S"); terminal.ReadFrame();
            Step("dropped-capture", "\x1b[S\x1b[6;1Hlatest");
            terminal.Resize(28, 8);
            Step("resize-output", "\x1b[8;1H\r\noutput");
            terminal.Resize(24, 6);
            Show("resize-original", terminal.ReadFrame());
            Show("historical-frame", original);

            void Step(string name, string value)
            {
                Write(terminal, value);
                Show(name, terminal.ReadFrame());
            }

            void Show(string name, TerminalFrame frame)
            {
                var request = For(frame, dpi: dpi);
                pane.Rasterizer.Submit(request);
                var shown = pane.Next(name);
                if (!ReferenceEquals(shown.Request, request)) throw new Exception($"{name} presented a different scroll frame.");
                var pixels = pane.Sink.Screen!.GetPixelBytes();
                AssertMatches($"scroll {name} at {dpi} DPI", pixels, Expected(reference, request));
                hashes.Add($"{dpi} {name} {Convert.ToHexString(SHA256.HashData(pixels))}");
            }
        }
        if (hashesPath is not null)
        {
            var output = Path.GetFullPath(hashesPath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllLines(output, hashes);
        }
        Console.WriteLine($"Scroll transition raster parity passed {hashes.Count} frames.");
    }
}
