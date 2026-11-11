using System;
using System.Collections.Generic;
using System.Linq;
using AgentTerm.Terminal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace AgentTerm_App;

// A single retained control draws the terminal grid. Metrics are shared with the resize calculation.
public sealed class TerminalSurface : UserControl
{
    private const string FallbackFont = "Consolas";
    private static readonly Lazy<string[]> AvailableFonts = new(FindTerminalFonts);
    private readonly CanvasControl _canvas = new();
    private TerminalFrame? _frame;
    private bool _cursorPhase = true;
    private bool _resourcesReady;
    private float _terminalFontSize = 14;
    private string _terminalFontFamily = FallbackFont;
    private bool _acrylicBackground;
    public event Action? MetricsChanged;
    public float CellWidth { get; private set; } = 8.4f;
    public float CellHeight { get; private set; } = 19f;

    public TerminalSurface()
    {
        IsTabStop = true;
        Content = _canvas;
        _canvas.Draw += Draw;
        _canvas.CreateResources += (_, _) => { _resourcesReady = true; MeasureCells(); };
        PointerPressed += (_, _) => Focus(FocusState.Pointer);
    }

    public void SetAcrylicBackground(bool enabled)
    {
        if (_acrylicBackground == enabled) return;
        _acrylicBackground = enabled;
        _canvas.Invalidate();
    }

    internal static IReadOnlyList<string> FontFamilies => AvailableFonts.Value;

    internal static string ResolveFontFamily(string? preferredFamily) =>
        FontFamilies.FirstOrDefault(name => string.Equals(name, preferredFamily, StringComparison.OrdinalIgnoreCase))
            ?? FontFamilies[0];

    private static string[] FindTerminalFonts()
    {
        var installed = CanvasTextFormat.GetSystemFontFamilies().ToHashSet(StringComparer.OrdinalIgnoreCase);
        // DirectWrite can expose either full Nerd Font family names or their legacy short names.
        string[] candidates =
        [
            "CaskaydiaCove Nerd Font Mono", "CaskaydiaCove NFM", "CaskaydiaCove Nerd Font", "CaskaydiaCove NF",
            "Cascadia Mono NF", "Cascadia Mono", FallbackFont, "Cascadia Code", "FiraCode Nerd Font Mono",
            "FiraCode NFM", "FiraCode Nerd Font", "FiraCode NF", "Fira Code", "JetBrains Mono", "Lucida Console"
        ];
        return candidates.Where(installed.Contains).DefaultIfEmpty(FallbackFont).ToArray();
    }

    public void SetTerminalFont(float size, string preferredFamily)
    {
        size = Math.Clamp(size, 8, 36);
        var family = ResolveFontFamily(preferredFamily);
        if (_terminalFontSize == size && _terminalFontFamily == family) return;
        _terminalFontSize = size;
        _terminalFontFamily = family;
        if (_resourcesReady) MeasureCells();
        _canvas.Invalidate();
    }

    public void SetFrame(TerminalFrame frame)
    {
        if (ReferenceEquals(_frame, frame)) return;
        _frame = frame;
        _canvas.Invalidate();
    }

    public void SetCursorPhase(bool visible)
    {
        if (_cursorPhase == visible) return;
        _cursorPhase = visible;
        if (_frame?.Cursor.Blink == true) _canvas.Invalidate();
    }

    public void RefreshCursor()
    {
        _cursorPhase = true;
        _canvas.Invalidate();
    }

    public void ReleaseResources() => _canvas.RemoveFromVisualTree();

    private void MeasureCells()
    {
        using var format = CreateFormat(false, false, _terminalFontSize, _terminalFontFamily);
        using var layout = new CanvasTextLayout(_canvas, "M", format, 100, 100);
        CellWidth = (float)Math.Ceiling(layout.LayoutBounds.Width);
        CellHeight = (float)Math.Ceiling(layout.LayoutBounds.Height);
        MetricsChanged?.Invoke();
    }

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_frame is { } frame)
            DrawFrame(args.DrawingSession, frame, CellWidth, CellHeight,
                FocusState != FocusState.Unfocused, _cursorPhase, _terminalFontSize, _acrylicBackground, _terminalFontFamily);
        else
            args.DrawingSession.Clear(_acrylicBackground ? Color.FromArgb(0, 0, 0, 0) : Color.FromArgb(255, 12, 12, 12));
    }

    public static void DrawFrame(CanvasDrawingSession drawing, TerminalFrame frame, float cellWidth,
        float cellHeight, bool focused, bool cursorPhase, float fontSize = 14, bool transparentBackground = false,
        string fontFamily = FallbackFont)
    {
        drawing.Clear(transparentBackground ? Color.FromArgb(0, 0, 0, 0) : ToColor(frame.Background));
        for (var row = 0; row < frame.Rows; row++)
        for (var col = 0; col < frame.Columns; col++)
        {
            var cell = frame.Cells[row * frame.Columns + col];
            if (cell.Selected || !transparentBackground || cell.Background != frame.Background)
                drawing.FillRectangle(col * cellWidth, row * cellHeight, cellWidth, cellHeight, cell.Selected ? SelectionColor : ToColor(cell.Background));
        }
        using var normal = CreateFormat(false, false, fontSize, fontFamily);
        using var bold = CreateFormat(true, false, fontSize, fontFamily);
        using var italic = CreateFormat(false, true, fontSize, fontFamily);
        using var boldItalic = CreateFormat(true, true, fontSize, fontFamily);
        for (var row = 0; row < frame.Rows; row++)
        for (var col = 0; col < frame.Columns; col++)
        {
            var cell = frame.Cells[row * frame.Columns + col];
            if (cell.Width == 0 || cell.Hidden) continue;
            var foreground = cell.Selected ? Color.FromArgb(255, 255, 255, 255) : ToColor(cell.Foreground);
            if (cell.Dim && !cell.Selected) foreground = Color.FromArgb(255, (byte)(foreground.R / 2), (byte)(foreground.G / 2), (byte)(foreground.B / 2));
            var x = col * cellWidth;
            var y = row * cellHeight;
            var width = cellWidth * cell.Width;
            if (!string.IsNullOrWhiteSpace(cell.Text))
            {
                using (drawing.CreateLayer(1, new Rect(x, y, width, cellHeight)))
                    drawing.DrawText(cell.Text, x, y, foreground, cell.Bold ? (cell.Italic ? boldItalic : bold) : (cell.Italic ? italic : normal));
            }
            if (cell.Underline) drawing.DrawLine(x, y + cellHeight - 2, x + width, y + cellHeight - 2, foreground);
            if (cell.Strikethrough) drawing.DrawLine(x, y + cellHeight / 2, x + width, y + cellHeight / 2, foreground);
        }
        var cursor = frame.Cursor;
        if (!cursor.Visible || (cursor.Blink && focused && !cursorPhase) || cursor.Column < 0 || cursor.Column >= frame.Columns || cursor.Row < 0 || cursor.Row >= frame.Rows) return;
        var cx = cursor.Column * cellWidth;
        var cy = cursor.Row * cellHeight;
        var color = ToColor(cursor.Color);
        if (!focused)
            drawing.DrawRectangle(cx + 0.5f, cy + 0.5f, cellWidth - 1, cellHeight - 1, color);
        else if (cursor.Shape == TerminalCursorShape.Bar)
            drawing.FillRectangle(cx, cy, 2, cellHeight, color);
        else if (cursor.Shape == TerminalCursorShape.Underline)
            drawing.FillRectangle(cx, cy + cellHeight - 2, cellWidth, 2, color);
        else
        {
            var cell = frame.Cells[cursor.Row * frame.Columns + cursor.Column];
            var cursorWidth = cellWidth * Math.Max(1, (int)cell.Width);
            drawing.FillRectangle(cx, cy, cursorWidth, cellHeight, color);
            if (!cell.Hidden && cell.Width != 0)
            {
                using (drawing.CreateLayer(1, new Rect(cx, cy, cursorWidth, cellHeight)))
                    drawing.DrawText(cell.Text, cx, cy, ToColor(cell.Background), cell.Bold ? (cell.Italic ? boldItalic : bold) : (cell.Italic ? italic : normal));
            }
        }
    }

    private static readonly Color SelectionColor = Color.FromArgb(255, 45, 92, 150);

    private static CanvasTextFormat CreateFormat(bool bold, bool italic, float fontSize, string fontFamily) => new()
    {
        FontFamily = fontFamily,
        FontSize = fontSize,
        FontWeight = new FontWeight { Weight = (ushort)(bold ? 700 : 400) },
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        WordWrapping = CanvasWordWrapping.NoWrap
    };

    private static Color ToColor(TerminalColor color) => Color.FromArgb(255, color.R, color.G, color.B);
}
