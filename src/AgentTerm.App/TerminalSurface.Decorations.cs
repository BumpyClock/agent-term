using System;
using System.Collections.Generic;
using AgentTerm.Terminal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace AgentTerm_App;

public sealed partial class TerminalSurface
{
    private sealed class DecorationRuns
    {
        private readonly Dictionary<(TerminalUnderlineStyle Style, int Columns, int Span), CanvasGeometry> _patterns = new();
        private float _cellWidth, _cellHeight;
        private Run? _underline, _overline, _strikethrough;

        private readonly record struct Run(int Start, int End, float Y, Color Color, TerminalUnderlineStyle Style, int Span);

        internal void Configure(float cellWidth, float cellHeight) => (_cellWidth, _cellHeight) = (cellWidth, cellHeight);

        internal void Add(CanvasDrawingSession drawing, TerminalFrame frame, TerminalCell cell, int column, float y, bool textPhase,
            Color? cursorText = null)
        {
            if (!ShowsInk(cell, textPhase)) return;
            var foreground = cursorText ?? TextColor(frame, cell);
            var end = column + cell.Width;
            if (cell.Underline)
            {
                var color = cursorText is null && !cell.Selected && cell.UnderlineColor is { } underline
                    ? cell.Dim ? Dimmed(ToColor(underline)) : ToColor(underline)
                    : foreground;
                var span = cell.UnderlineStyle is TerminalUnderlineStyle.Curly or TerminalUnderlineStyle.Dotted or TerminalUnderlineStyle.Dashed
                    ? cell.Width : 1;
                Extend(drawing, ref _underline, new Run(column, end, y + _cellHeight - 2, color, cell.UnderlineStyle, span));
            }
            if (cell.Overline) Extend(drawing, ref _overline, new Run(column, end, y + 1, foreground, TerminalUnderlineStyle.None, 1));
            if (cell.Strikethrough) Extend(drawing, ref _strikethrough, new Run(column, end, y + _cellHeight / 2, foreground, TerminalUnderlineStyle.None, 1));
        }

        internal void Flush(CanvasDrawingSession drawing)
        {
            Draw(drawing, _underline);
            Draw(drawing, _overline);
            Draw(drawing, _strikethrough);
            _underline = _overline = _strikethrough = null;
        }

        private void Extend(CanvasDrawingSession drawing, ref Run? run, Run next)
        {
            if (run is { } open && open.End == next.Start &&
                (open.Y, open.Color, open.Style, open.Span) == (next.Y, next.Color, next.Style, next.Span))
            {
                run = open with { End = next.End };
                return;
            }
            Draw(drawing, run);
            run = next;
        }

        private void Draw(CanvasDrawingSession drawing, Run? decoration)
        {
            if (decoration is not { } run) return;
            float left = run.Start * _cellWidth, right = run.End * _cellWidth;
            switch (run.Style)
            {
                case TerminalUnderlineStyle.Double:
                    drawing.DrawLine(left, run.Y - 2, right, run.Y - 2, run.Color);
                    goto default;
                case TerminalUnderlineStyle.Curly:
                case TerminalUnderlineStyle.Dotted:
                case TerminalUnderlineStyle.Dashed:
                    drawing.DrawGeometry(Pattern(drawing.Device, run.Style, run.End - run.Start, run.Span), left, run.Y, run.Color, 1);
                    break;
                default: drawing.DrawLine(left, run.Y, right, run.Y, run.Color); break;
            }
        }

        private CanvasGeometry Pattern(CanvasDevice device, TerminalUnderlineStyle style, int columns, int span)
        {
            var key = (style, columns, span);
            if (_patterns.TryGetValue(key, out var pattern)) return pattern;
            if (_patterns.Count >= 256) Clear();
            using var path = new CanvasPathBuilder(device);
            var width = span * _cellWidth;
            for (var column = 0; column < columns; column += span)
            {
                var x = column * _cellWidth;
                if (style == TerminalUnderlineStyle.Curly)
                    for (float i = 0; i < width; i += 1)
                        Segment(path, x + i, MathF.Sin(i * MathF.PI / 2), x + MathF.Min(i + 1, width), MathF.Sin((i + 1) * MathF.PI / 2));
                else
                {
                    var length = style == TerminalUnderlineStyle.Dotted ? 1 : 3;
                    for (float i = 0; i < width; i += length * 2) Segment(path, x + i, 0, x + MathF.Min(i + length, width), 0);
                }
            }
            pattern = CanvasGeometry.CreatePath(path);
            _patterns.Add(key, pattern);
            return pattern;
        }

        private static void Segment(CanvasPathBuilder path, float x0, float y0, float x1, float y1)
        {
            path.BeginFigure(x0, y0);
            path.AddLine(x1, y1);
            path.EndFigure(CanvasFigureLoop.Open);
        }

        internal void Clear()
        {
            foreach (var pattern in _patterns.Values) pattern.Dispose();
            _patterns.Clear();
        }
    }
}
