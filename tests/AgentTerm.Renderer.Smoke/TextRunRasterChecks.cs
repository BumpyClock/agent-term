using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

internal static class TextRunRasterChecks
{
    private const float CellHeight = 20;
    private static readonly TerminalColor Black = new(0, 0, 0);
    private static readonly TerminalColor White = new(230, 230, 230);

    internal static void Run(CanvasDevice device)
    {
        var installed = CanvasTextFormat.GetSystemFontFamilies().ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] ligatureFonts = ["FiraCode Nerd Font", "Cascadia Code"];
        foreach (var dpi in new[] { 96f, 120f, 144f })
        foreach (var cellWidth in new[] { 8.4f, 10f })
        foreach (var gridTop in new[] { 0f, 3.25f })
        {
            var setup = new Setup(device, "Consolas", dpi, cellWidth, gridTop);
            foreach (var bold in new[] { false, true })
                AssertAnchored(setup, Chars("hello_world(){}[]<>=;|", bold: bold), $"ASCII bold={bold}");
            AssertAnchored(setup, Cells("e\u0301", "x", "o\u0308", "n\u0303", "a"), "combining marks", markedFirst: true);
            AssertAnchored(setup, Cells("a", "\u2A00", "b", "\u16A0", "c"), "narrow fallback glyphs", baseline: true);
            AssertAnchored(setup, Cells("a", "\u0F00", "b"), "shorter fallback glyph", baseline: true);
            AssertAnchored(setup, [.. Cells("a", "\u754C"), Tail(), .. Cells("b", "c")], "wide fallback glyph", baseline: true);
            AssertAnchored(setup, Cells("\u0633", "\u0645", "\u0643", "\u0628", "x"), "RTL letters keep their columns");
            AssertStyles(setup);
            AssertSelection(setup);
            foreach (var family in ligatureFonts.Where(installed.Contains))
            {
                AssertLigatures(setup with { Family = family });
                AssertAnchored(setup with { Family = family }, Cells("\u0633", "\u0645", "\u0643", "\u0628", "x"), "RTL letters keep their columns");
            }
        }
        foreach (var family in ligatureFonts.Where(installed.Contains).Prepend("Consolas"))
            AssertExactPerCell(new Setup(device, family, 96, 10, 0), Cells("\u0633", "\u0645", "\u0643", "\u0628", "x"), "RTL letters");
        AssertExactPerCell(new Setup(device, "Consolas", 96, 10, 0), Cells("a", "\u2A00", "b"), "narrow fallback glyph");
        foreach (var missing in ligatureFonts.Where(family => !installed.Contains(family)))
            Console.WriteLine($"SKIPPED ligature raster checks: font '{missing}' is not installed.");
        AssertGlyphRunsMatchLayout(device, installed);
        ReportSegmentation();
        Console.WriteLine("Text run anchoring, fallback, RTL, style, selection and ligature checks passed.");
    }

    private static void AssertGlyphRunsMatchLayout(CanvasDevice device, HashSet<string> installed)
    {
        TerminalColor blue = new(90, 140, 255), amber = new(250, 200, 60);
        var cases = new List<(string Family, float Dpi, float CellWidth, float CellHeight, string[] Cells, bool Bold, bool Italic, TerminalColor Foreground)>
        {
            ("MS Gothic", 96, 8, 17, ["✔️", "x"], true, false, blue),
            ("MS Gothic", 96, 8, 17, ["✔︎", "x"], true, false, amber),
            ("MS Gothic", 144, 10, 20, ["✔️", "x"], true, false, blue),
            ("MS Gothic", 144, 10, 20, ["✔︎", "x"], true, false, amber),
            ("MS Gothic", 144, 10, 20, ["─", "╮"], false, true, White),
            ("Cascadia Mono", 96, 16, 32, ["╭", "│"], false, true, White),
            ("Cascadia Code", 96, 16, 32, ["╭", "│"], false, true, White),
            ("FiraCode Nerd Font", 96, 16, 32, ["é", "Å"], false, true, blue),
        };
        foreach (var dpi in new[] { 96f, 144f })
        foreach (var bold in new[] { false, true })
        foreach (var italic in new[] { false, true })
            cases.Add(("Consolas", dpi, 10, 20, ["a", "α", "б", "b", "ω", "ж"], bold, italic, White));
        using (var shaper = new DirectWriteShaper())
        {
            if (!shaper.Analyze("abαωбж") || shaper.Scripts.Length != 3 ||
                shaper.Scripts[0] is not { Start: 0, Length: 2 } || shaper.Scripts[1] is not { Start: 2, Length: 2 } ||
                shaper.Scripts[2] is not { Start: 4, Length: 2 })
                throw new Exception("DirectWrite script analysis must split Latin, Greek and Cyrillic into ordered items.");
            shaper.Dispose();
        }
        var previous = TerminalSurface.TextMode;
        try
        {
            foreach (var (family, dpi, cellWidth, cellHeight, text, bold, italic, foreground) in cases)
            {
                if (!installed.Contains(family))
                {
                    Console.WriteLine($"SKIPPED glyph run parity: font '{family}' is not installed.");
                    continue;
                }
                var cells = text.Select(value => new TerminalCell(value, 1, foreground, Black, bold, italic, false, false, false, false)).ToArray();
                TerminalCell[] grid = [.. Enumerable.Repeat(Cell(" "), cells.Length), .. cells, .. Enumerable.Repeat(Cell(" "), cells.Length)];
                var frame = new TerminalFrame(cells.Length, 3, grid,
                    new TerminalCursor(0, 0, false, false, TerminalCursorShape.Block, White), White, Black);
                byte[] Render(TerminalSurface.TextRendering mode)
                {
                    TerminalSurface.TextMode = mode;
                    using var target = new CanvasRenderTarget(device, cells.Length * cellWidth, cellHeight * 3, dpi);
                    using (var drawing = target.CreateDrawingSession())
                        TerminalSurface.DrawFrame(drawing, frame, cellWidth, cellHeight, false, true, 14, fontFamily: family, hideCursor: true);
                    return target.GetPixelBytes();
                }
                var layout = Render(TerminalSurface.TextRendering.Layout);
                var glyphs = Render(TerminalSurface.TextRendering.GlyphRunClipped);
                var differing = layout.Where((value, index) => value != glyphs[index]).Count();
                if (differing != 0)
                    throw new Exception($"Glyph runs differ from text layouts in {differing} bytes: {family} {dpi} DPI, " +
                        $"cells [{string.Join(", ", text.Select(value => string.Join(" ", value.Select(c => $"U+{(int)c:X4}"))))}], bold={bold}, italic={italic}.");
            }
        }
        finally
        {
            TerminalSurface.TextMode = previous;
        }
    }

    private static void ReportSegmentation()
    {
        using var terminal = new GhosttyTerminal(120, 40);
        string[] screen =
        [
            "\x1b[32muser@host\x1b[0m \x1b[34m~/Projects/agent-term\x1b[0m (main) $ git diff --stat",
            " src/AgentTerm.App/TerminalSurface.cs                  | 212 \x1b[32m+++++++++++++\x1b[31m-----\x1b[0m",
            " tests/AgentTerm.Renderer.Smoke/TextRunRasterChecks.cs | 260 \x1b[32m++++++++++++++++\x1b[0m",
            "\x1b[1m\u256D\u2500 Agent \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u256E\x1b[0m",
            "\u2502 Running renderer smoke tests (3/7) \u2502",
            "\u2570\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u256F",
            "    \x1b[35mif\x1b[0m (run \x1b[35mis not null\x1b[0m) drawing.DrawTextLayout(run, start * cellWidth, y, foreground);",
            "    \x1b[35mfor\x1b[0m (\x1b[35mvar\x1b[0m col = start; col < end; col++) layouts.AppendCell(CellAt(row, col).Text);",
            "  \x1b[2m// 14:02:11 build succeeded in 7.72 s, 0 warnings, 0 errors\x1b[0m",
        ];
        for (var row = 0; row < 40; row++) terminal.Write(System.Text.Encoding.UTF8.GetBytes(screen[row % screen.Length] + "\r\n"));
        var frame = terminal.ReadFrame();
        int cells = 0, runs = 0, multiCellRuns = 0;
        var keys = new HashSet<string>();
        var cellKeys = new HashSet<(string, byte, bool, bool)>();
        for (var row = 0; row < frame.Rows; row++)
        for (var column = 0; column < frame.Columns;)
        {
            var cell = frame.Cells[row * frame.Columns + column];
            var end = column + 1;
            if (cell.Width == 0 || cell.Hidden || string.IsNullOrWhiteSpace(cell.Text)) { column = end; continue; }
            while (end < frame.Columns && TerminalSurface.JoinsTextRun(frame, frame.Cells[row * frame.Columns + end - 1], frame.Cells[row * frame.Columns + end], true)) end++;
            cells += end - column;
            for (var index = column; index < end; index++)
            {
                var member = frame.Cells[row * frame.Columns + index];
                cellKeys.Add((member.Text, member.Width, member.Bold, member.Italic));
            }
            runs++;
            if (end - column > 1) multiCellRuns++;
            keys.Add(string.Join("\0", Enumerable.Range(column, end - column).Select(index => frame.Cells[row * frame.Columns + index].Text)) + (cell.Bold ? "b" : "") + (cell.Italic ? "i" : ""));
            column = end;
        }
        Console.WriteLine($"Text run segmentation of a representative 120x40 agent screen: {cells} text cells and {cellKeys.Count} distinct cell layouts per-cell; {runs} runs ({multiCellRuns} multi-cell) and {keys.Count} distinct run keys. Runs rejected by shaping gates draw per cell.");
    }

    private sealed record Setup(CanvasDevice Device, string Family, float Dpi, float CellWidth, float GridTop)
    {
        internal float Scale => Dpi / 96;
        public override string ToString() => $"{Family} {Dpi} DPI, {CellWidth} DIP cells, grid top {GridTop}";
    }

    private sealed class Raster(Color[] pixels, int width, int height)
    {
        internal Color[] Pixels { get; } = pixels;
        internal int Width { get; } = width;
        internal int Height { get; } = height;
    }

    private static TerminalCell Cell(string text, TerminalColor? foreground = null, bool bold = false, bool italic = false) =>
        new(text, 1, foreground ?? White, Black, bold, italic, false, false, false, false);

    private static TerminalCell[] Chars(string text, bool bold = false) =>
        [.. text.Select(character => Cell(character.ToString(), bold: bold))];

    private static TerminalCell[] Cells(params string[] cells) => [.. cells.Select(text => Cell(text))];

    private static TerminalCell Tail() => Cell("") with { Width = 0 };

    private static TerminalCell[] Wide(TerminalCell[] cells) =>
        [.. cells.Select((cell, index) => index + 1 < cells.Length && cells[index + 1].Width == 0 ? cell with { Width = 2 } : cell)];

    private static TerminalFrame Frame(TerminalCell[] row, Func<TerminalFrame, TerminalFrame>? customize = null)
    {
        TerminalCell[] cells = [Cell(" "), .. Wide(row), Cell(" ")];
        var frame = new TerminalFrame(cells.Length, 1, cells,
            new TerminalCursor(0, 0, false, false, TerminalCursorShape.Block, White), White, Black)
        {
            SelectionForeground = new TerminalColor(0, 255, 0),
            SelectionBackground = new TerminalColor(0, 0, 90)
        };
        return customize?.Invoke(frame) ?? frame;
    }

    private static Raster Render(Setup setup, TerminalFrame frame)
    {
        using var target = new CanvasRenderTarget(setup.Device, frame.Columns * setup.CellWidth, CellHeight + 8, setup.Dpi);
        using (var drawing = target.CreateDrawingSession())
            TerminalSurface.DrawFrame(drawing, frame, setup.CellWidth, CellHeight, false, true, 14,
                fontFamily: setup.Family, hideCursor: true, gridTop: setup.GridTop);
        return new Raster(target.GetPixelColors(), (int)target.SizeInPixels.Width, (int)target.SizeInPixels.Height);
    }

    private static TerminalFrame Only(TerminalFrame frame, int column)
    {
        var cells = frame.Cells.ToArray();
        for (var index = 0; index < cells.Length; index++)
            if (index != column && !(cells[index].Width == 0 && index == column + 1))
                cells[index] = cells[index] with { Text = " ", Width = 1 };
        return frame.WithCells(cells);
    }

    private static TerminalFrame Blank(TerminalFrame frame) =>
        frame.WithCells([.. frame.Cells.Select(cell => cell with { Text = " ", Width = 1 })]);

    private static int[] Ink(Raster raster, Raster blank) =>
        [.. raster.Pixels.Zip(blank.Pixels, (pixel, background) =>
            Math.Max(Math.Abs(pixel.R - background.R), Math.Max(Math.Abs(pixel.G - background.G), Math.Abs(pixel.B - background.B))))];

    private static int[] PerCellInk(Setup setup, TerminalFrame frame, Raster blank)
    {
        var composite = new int[blank.Pixels.Length];
        for (var column = 0; column < frame.Columns; column++)
        {
            if (frame.Cells[column].Width == 0 || string.IsNullOrWhiteSpace(frame.Cells[column].Text)) continue;
            var ink = Ink(Render(setup, Only(frame, column)), blank);
            for (var index = 0; index < ink.Length; index++) composite[index] = Math.Max(composite[index], ink[index]);
        }
        return composite;
    }

    private static (double Mass, double X, double Y) Moments(Setup setup, int[] ink, int width, int column, int columns)
    {
        var left = (int)MathF.Floor(column * setup.CellWidth * setup.Scale);
        var right = (int)MathF.Ceiling((column + columns) * setup.CellWidth * setup.Scale);
        double mass = 0, x = 0, y = 0;
        for (var row = 0; row < ink.Length / width; row++)
        for (var pixel = Math.Max(0, left); pixel < Math.Min(width, right); pixel++)
        {
            var value = ink[row * width + pixel];
            mass += value;
            x += value * pixel;
            y += value * row;
        }
        return mass == 0 ? (0, 0, 0) : (mass, x / mass, y / mass);
    }

    private static void AssertAnchored(Setup setup, TerminalCell[] row, string label, bool markedFirst = false, bool baseline = false)
    {
        var frame = Frame(row);
        var blank = Render(setup, Blank(frame));
        var actual = Ink(Render(setup, frame), blank);
        var expected = PerCellInk(setup, frame, blank);
        for (var column = 1; column < frame.Columns - 1; column++)
        {
            var cell = frame.Cells[column];
            if (cell.Width == 0 || string.IsNullOrWhiteSpace(cell.Text)) continue;
            var got = Moments(setup, actual, blank.Width, column, cell.Width);
            var want = Moments(setup, expected, blank.Width, column, cell.Width);
            if (want.Mass == 0) throw new Exception($"{label}: column {column} drew no reference ink ({setup}).");
            var tolerance = baseline ? 0.35 : 0.6;
            if (got.Mass < want.Mass * 0.7 || got.Mass > want.Mass * 1.45 ||
                Math.Abs(got.X - want.X) > 0.6 || Math.Abs(got.Y - want.Y) > tolerance)
                throw new Exception($"{label}: column {column} '{cell.Text}' drifted from its cell ({setup}). " +
                    $"Mass {got.Mass:0}/{want.Mass:0}, centroid ({got.X:0.00},{got.Y:0.00}) vs ({want.X:0.00},{want.Y:0.00}).");
        }
        if (markedFirst)
        {
            var bare = Frame([Cell(row[0].Text[..1]), .. row.Skip(1)]);
            var withoutMark = Moments(setup, Ink(Render(setup, bare), blank), blank.Width, 1, 1);
            var withMark = Moments(setup, actual, blank.Width, 1, 1);
            if (withMark.Mass < withoutMark.Mass * 1.08 || withMark.Y >= withoutMark.Y)
                throw new Exception($"{label}: the combining mark did not render above its base ({setup}).");
        }
    }

    private static void AssertExactPerCell(Setup setup, TerminalCell[] row, string label)
    {
        var frame = Frame(row);
        var blank = Render(setup, Blank(frame));
        if (!Ink(Render(setup, frame), blank).SequenceEqual(PerCellInk(setup, frame, blank)))
            throw new Exception($"{label} must render exactly like independent cells ({setup}).");
    }

    private static void AssertStyles(Setup setup)
    {
        TerminalColor red = new(255, 40, 40), green = new(40, 255, 40), blue = new(60, 60, 255);
        TerminalCell[] row =
        [
            Cell("a", red, bold: true), Cell("b", red, bold: true), Cell("c", green), Cell("d", green),
            Cell("e", blue, italic: true), Cell("f", blue, italic: true), Cell("g", red), Cell("h", green)
        ];
        AssertAnchored(setup, row[..6], "adjacent bold, regular and italic styles");
        AssertAnchored(setup, row[6..], "adjacent colors");
        var frame = Frame(row);
        var raster = Render(setup, frame);
        for (var column = 1; column <= row.Length; column++)
        {
            var color = frame.Cells[column].Foreground;
            var dominant = color.R == 255 ? 0 : color.G == 255 ? 1 : 2;
            var sums = new double[3];
            var left = (int)MathF.Ceiling(column * setup.CellWidth * setup.Scale) + 1;
            var right = (int)MathF.Floor((column + 1) * setup.CellWidth * setup.Scale) - 1;
            for (var y = 0; y < raster.Height; y++)
            for (var x = left; x < right; x++)
            {
                var pixel = raster.Pixels[y * raster.Width + x];
                sums[0] += pixel.R; sums[1] += pixel.G; sums[2] += pixel.B;
            }
            if (Enumerable.Range(0, 3).Any(channel => channel != dominant && sums[channel] * 1.8 > sums[dominant]))
                throw new Exception($"Column {column} lost its own foreground color at a style boundary ({setup}).");
        }
    }

    private static void AssertSelection(Setup setup)
    {
        TerminalCell[] row = [Cell("w"), Cell("o") with { Selected = true }, Cell("r") with { Selected = true }, Cell("d")];
        AssertAnchored(setup, row, "selection inside a word");
        var raster = Render(setup, Frame(row));
        for (var column = 1; column <= row.Length; column++)
        {
            var selected = row[column - 1].Selected;
            var left = (int)MathF.Ceiling(column * setup.CellWidth * setup.Scale) + 1;
            var right = (int)MathF.Floor((column + 1) * setup.CellWidth * setup.Scale) - 1;
            var pixels = Enumerable.Range(0, raster.Height)
                .SelectMany(y => Enumerable.Range(left, right - left).Select(x => raster.Pixels[y * raster.Width + x])).ToArray();
            var greenInk = pixels.Count(pixel => pixel.G > 150 && pixel.R < 100);
            var whiteInk = pixels.Count(pixel => pixel.G > 150 && pixel.R > 150);
            if (selected ? greenInk == 0 || whiteInk != 0 : whiteInk == 0 || greenInk != 0)
                throw new Exception($"Selection must recolor exactly its own cells, column {column} ({setup}).");
        }
    }

    private static void AssertLigatures(Setup setup)
    {
        foreach (var ligature in new[] { "=>", "!=", "->", "===", "<!--", "--->" })
        {
            TerminalCell[] row = [.. Chars("x"), .. Chars(ligature), .. Chars("y")];
            var frame = Frame(row);
            var blank = Render(setup, Blank(frame));
            var actual = Ink(Render(setup, frame), blank);
            var expected = PerCellInk(setup, frame, blank);
            foreach (var column in new[] { 1, row.Length })
            {
                var got = Moments(setup, actual, blank.Width, column, 1);
                var want = Moments(setup, expected, blank.Width, column, 1);
                if (Math.Abs(got.X - want.X) > 0.6 || Math.Abs(got.Y - want.Y) > 0.6 || got.Mass < want.Mass * 0.7)
                    throw new Exception($"Ligature '{ligature}' moved its neighbor in column {column} ({setup}).");
            }
            var left = (int)MathF.Floor(1 * setup.CellWidth * setup.Scale) - 1;
            var right = (int)MathF.Ceiling((row.Length + 1) * setup.CellWidth * setup.Scale) + 1;
            for (var index = 0; index < actual.Length; index++)
                if (actual[index] > 24 && (index % blank.Width < left || index % blank.Width >= right))
                    throw new Exception($"Ligature '{ligature}' drew outside its run cells ({setup}).");
            var shaped = Moments(setup, actual, blank.Width, 2, ligature.Length);
            var separate = Moments(setup, expected, blank.Width, 2, ligature.Length);
            if (shaped.Mass < separate.Mass * 0.4 || shaped.Mass > separate.Mass * 2.5)
                throw new Exception($"Ligature '{ligature}' lost or duplicated ink ({setup}).");
            var changed = 0;
            var first = (int)MathF.Ceiling(2 * setup.CellWidth * setup.Scale);
            var last = (int)MathF.Floor((2 + ligature.Length) * setup.CellWidth * setup.Scale);
            for (var index = 0; index < actual.Length; index++)
                if (index % blank.Width >= first && index % blank.Width < last && Math.Abs(actual[index] - expected[index]) > 64) changed++;
            if (changed < 6)
                throw new Exception($"Ligature '{ligature}' was not shaped across its cells ({setup}); {changed} pixels differ from per-cell glyphs.");
        }
    }
}
