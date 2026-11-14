using System;
using AgentTerm.Terminal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace AgentTerm_App;

public sealed partial class TerminalSurface
{
    private static readonly byte[] BoxLines =
    [
        0x44, 0x88, 0x11, 0x22, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x14, 0x18, 0x24, 0x28,
        0x50, 0x90, 0x60, 0xA0, 0x05, 0x09, 0x06, 0x0A, 0x41, 0x81, 0x42, 0x82, 0x15, 0x19, 0x16, 0x25,
        0x26, 0x1A, 0x29, 0x2A, 0x51, 0x91, 0x52, 0x61, 0x62, 0x92, 0xA1, 0xA2, 0x54, 0x94, 0x58, 0x98,
        0x64, 0xA4, 0x68, 0xA8, 0x45, 0x85, 0x49, 0x89, 0x46, 0x86, 0x4A, 0x8A, 0x55, 0x95, 0x59, 0x99,
        0x56, 0x65, 0x66, 0x96, 0x5A, 0xA5, 0x69, 0x9A, 0xA9, 0xA6, 0x6A, 0xAA, 0x00, 0x00, 0x00, 0x00,
        0xCC, 0x33, 0x1C, 0x34, 0x3C, 0xD0, 0x70, 0xF0, 0x0D, 0x07, 0x0F, 0xC1, 0x43, 0xC3, 0x1D, 0x37,
        0x3F, 0xD1, 0x73, 0xF3, 0xDC, 0x74, 0xFC, 0xCD, 0x47, 0xCF, 0xDD, 0x77, 0xFF, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x40, 0x01, 0x04, 0x10, 0x80, 0x02, 0x08, 0x20, 0x48, 0x21, 0x84, 0x12
    ];

    private enum Stroke { None, Light, Heavy, Double }

    internal static bool IsBoxGlyph(TerminalCell cell) => cell.Text is [>= '─' and <= '▟'];

    internal static bool TilesBoxGlyph(char glyph) => glyph is '─' or '━' or '═' or
        (>= '▀' and <= '█') or (>= '░' and <= '▔');

    private static int DrawBoxRun(CanvasDrawingSession drawing, TerminalFrame frame, int row, int start, int endColumn, float top,
        float cellWidth, float cellHeight, bool textPhase)
    {
        var first = CellAt(frame, row, start);
        var color = TextColor(frame, first);
        var end = start + 1;
        if (first.Width == 1 && TilesBoxGlyph(first.Text[0]))
            while (end < endColumn && CellAt(frame, row, end) is var next && next.Text == first.Text && next.Width == 1 &&
                DrawsText(next, textPhase) && TextColor(frame, next) == color) end++;
        DrawBoxGlyph(drawing, first.Text[0], start, Math.Max(end - start, first.Width), top, row, cellWidth, cellHeight, color);
        return end;
    }

    private static void DrawCellGlyph(CanvasDrawingSession drawing, GlyphLayouts layouts, TerminalCell cell, int column, int row,
        float top, float cellWidth, float cellHeight, Color color)
    {
        if (IsBoxGlyph(cell)) DrawBoxGlyph(drawing, cell.Text[0], column, cell.Width, top, row, cellWidth, cellHeight, color);
        else drawing.DrawTextLayout(layouts.Get(drawing.Device, cell.Text, cell.Width, cell.Bold, cell.Italic),
            column * cellWidth, top + row * cellHeight, color);
    }

    private static void DrawBoxGlyph(CanvasDrawingSession drawing, char glyph, int column, int cells, float top, int row,
        float cellWidth, float cellHeight, Color color)
    {
        var box = new BoxCell(drawing, column, cells, top, row, cellWidth, cellHeight, color);
        var antialiasing = drawing.Antialiasing;
        drawing.Antialiasing = CanvasAntialiasing.Aliased;
        try { box.Draw(glyph); }
        finally { drawing.Antialiasing = antialiasing; }
    }

    private readonly ref struct BoxCell
    {
        private readonly CanvasDrawingSession _drawing;
        private readonly Color _color;
        private readonly float _scale;
        private readonly int _left, _top, _right, _bottom, _light;

        internal BoxCell(CanvasDrawingSession drawing, int column, int cells, float top, int row, float cellWidth, float cellHeight,
            Color color)
        {
            _drawing = drawing;
            _color = color;
            _scale = drawing.Dpi / 96;
            _left = Snap(column * cellWidth);
            _right = Snap((column + cells) * cellWidth);
            _top = Snap(top + row * cellHeight);
            _bottom = Snap(top + (row + 1) * cellHeight);
            _light = Math.Max(1, (int)MathF.Round(cellWidth * _scale / 6));
        }

        private int Width => _right - _left;
        private int Height => _bottom - _top;
        private int Snap(float value) => (int)MathF.Round(value * _scale);

        internal void Draw(char glyph)
        {
            var index = glyph - '─';
            if (index < BoxLines.Length && BoxLines[index] is var arms and not 0)
            {
                Lines((Stroke)(arms & 3), (Stroke)(arms >> 2 & 3), (Stroke)(arms >> 4 & 3), (Stroke)(arms >> 6));
                return;
            }
            switch (glyph)
            {
                case '┄' or '┅': DashHorizontal(3, glyph == '┅'); break;
                case '┆' or '┇': DashVertical(3, glyph == '┇'); break;
                case '┈' or '┉': DashHorizontal(4, glyph == '┉'); break;
                case '┊' or '┋': DashVertical(4, glyph == '┋'); break;
                case '╌' or '╍': DashHorizontal(2, glyph == '╍'); break;
                case '╎' or '╏': DashVertical(2, glyph == '╏'); break;
                case >= '╭' and <= '╰': Arc(glyph); break;
                case '╱': Diagonals(rising: true, falling: false); break;
                case '╲': Diagonals(rising: false, falling: true); break;
                case '╳': Diagonals(rising: true, falling: true); break;
                case '▀': Fill(_left, _top, _right, Row(4)); break;
                case >= '▁' and <= '█': Fill(_left, Row('█' - glyph), _right, _bottom); break;
                case >= '▉' and <= '▏': Fill(_left, _top, Column('▐' - glyph), _bottom); break;
                case '▐': Fill(Column(4), _top, _right, _bottom); break;
                case >= '░' and <= '▓': Fill(_left, _top, _right, _bottom, _color with { A = (byte)(_color.A * (glyph - '▐') / 4) }); break;
                case '▔': Fill(_left, _top, _right, Row(1)); break;
                case '▕': Fill(Column(7), _top, _right, _bottom); break;
                case >= '▖' and <= '▟': Quadrants(glyph); break;
            }
        }

        private int Column(int eighths) => Eighth(_left, _right, eighths);
        private int Row(int eighths) => Eighth(_top, _bottom, eighths);

        private static int Eighth(int start, int end, int eighths)
        {
            var edge = start + (end - start) * eighths / 8;
            return eighths is <= 0 or >= 8 ? edge : Math.Max(start + 1, Math.Min(end - 1, edge));
        }

        private void Fill(int left, int top, int right, int bottom, Color? color = null)
        {
            if (right <= left || bottom <= top) return;
            _drawing.FillRectangle(left / _scale, top / _scale, (right - left) / _scale, (bottom - top) / _scale, color ?? _color);
        }

        private void Quadrants(char glyph)
        {
            var bits = (glyph - '▖') switch
            {
                0 => 0b0100, 1 => 0b1000, 2 => 0b0001, 3 => 0b1101, 4 => 0b1001,
                5 => 0b0111, 6 => 0b1011, 7 => 0b0010, 8 => 0b0110, _ => 0b1110
            };
            var (midX, midY) = (Column(4), Row(4));
            if ((bits & 1) != 0) Fill(_left, _top, midX, midY);
            if ((bits & 2) != 0) Fill(midX, _top, _right, midY);
            if ((bits & 4) != 0) Fill(_left, midY, midX, _bottom);
            if ((bits & 8) != 0) Fill(midX, midY, _right, _bottom);
        }

        private void Lines(Stroke up, Stroke right, Stroke down, Stroke left)
        {
            int light = _light, heavy = _light * 2;
            var hLightTop = _top + (Height - light) / 2;
            var hLightBottom = hLightTop + light;
            var hHeavyTop = _top + (Height - heavy) / 2;
            var hHeavyBottom = hHeavyTop + heavy;
            var hDoubleTop = hLightTop - light;
            var hDoubleBottom = hLightBottom + light;
            var vLightLeft = _left + (Width - light) / 2;
            var vLightRight = vLightLeft + light;
            var vHeavyLeft = _left + (Width - heavy) / 2;
            var vHeavyRight = vHeavyLeft + heavy;
            var vDoubleLeft = vLightLeft - light;
            var vDoubleRight = vLightRight + light;

            var upBottom = left == Stroke.Heavy || right == Stroke.Heavy ? hHeavyBottom
                : left != right || down == up ? left == Stroke.Double || right == Stroke.Double ? hDoubleBottom : hLightBottom
                : left == Stroke.None && right == Stroke.None ? hLightBottom : hLightTop;
            var downTop = left == Stroke.Heavy || right == Stroke.Heavy ? hHeavyTop
                : left != right || up == down ? left == Stroke.Double || right == Stroke.Double ? hDoubleTop : hLightTop
                : left == Stroke.None && right == Stroke.None ? hLightTop : hLightBottom;
            var leftRight = up == Stroke.Heavy || down == Stroke.Heavy ? vHeavyRight
                : up != down || left == right ? up == Stroke.Double || down == Stroke.Double ? vDoubleRight : vLightRight
                : up == Stroke.None && down == Stroke.None ? vLightRight : vLightLeft;
            var rightLeft = up == Stroke.Heavy || down == Stroke.Heavy ? vHeavyLeft
                : up != down || right == left ? up == Stroke.Double || down == Stroke.Double ? vDoubleLeft : vLightLeft
                : up == Stroke.None && down == Stroke.None ? vLightLeft : vLightRight;

            switch (up)
            {
                case Stroke.Light: Fill(vLightLeft, _top, vLightRight, upBottom); break;
                case Stroke.Heavy: Fill(vHeavyLeft, _top, vHeavyRight, upBottom); break;
                case Stroke.Double:
                    Fill(vDoubleLeft, _top, vLightLeft, left == Stroke.Double ? hLightTop : upBottom);
                    Fill(vLightRight, _top, vDoubleRight, right == Stroke.Double ? hLightTop : upBottom);
                    break;
            }
            switch (right)
            {
                case Stroke.Light: Fill(rightLeft, hLightTop, _right, hLightBottom); break;
                case Stroke.Heavy: Fill(rightLeft, hHeavyTop, _right, hHeavyBottom); break;
                case Stroke.Double:
                    Fill(up == Stroke.Double ? vLightRight : rightLeft, hDoubleTop, _right, hLightTop);
                    Fill(down == Stroke.Double ? vLightRight : rightLeft, hLightBottom, _right, hDoubleBottom);
                    break;
            }
            switch (down)
            {
                case Stroke.Light: Fill(vLightLeft, downTop, vLightRight, _bottom); break;
                case Stroke.Heavy: Fill(vHeavyLeft, downTop, vHeavyRight, _bottom); break;
                case Stroke.Double:
                    Fill(vDoubleLeft, left == Stroke.Double ? hLightBottom : downTop, vLightLeft, _bottom);
                    Fill(vLightRight, right == Stroke.Double ? hLightBottom : downTop, vDoubleRight, _bottom);
                    break;
            }
            switch (left)
            {
                case Stroke.Light: Fill(_left, hLightTop, leftRight, hLightBottom); break;
                case Stroke.Heavy: Fill(_left, hHeavyTop, leftRight, hHeavyBottom); break;
                case Stroke.Double:
                    Fill(_left, hDoubleTop, up == Stroke.Double ? vLightLeft : leftRight, hLightTop);
                    Fill(_left, hLightBottom, down == Stroke.Double ? vLightLeft : leftRight, hDoubleBottom);
                    break;
            }
        }

        private void DashHorizontal(int count, bool heavy)
        {
            var thickness = heavy ? _light * 2 : _light;
            var top = _top + (Height - thickness) / 2;
            for (var dash = 0; dash < count; dash++)
                Fill(_left + Width * dash / count, top, _left + Width * (3 * dash + 2) / (3 * count), top + thickness);
        }

        private void DashVertical(int count, bool heavy)
        {
            var thickness = heavy ? _light * 2 : _light;
            var left = _left + (Width - thickness) / 2;
            for (var dash = 0; dash < count; dash++)
                Fill(left, _top + Height * dash / count, left + thickness, _top + Height * (3 * dash + 2) / (3 * count));
        }

        private void Arc(char glyph)
        {
            var centerX = _left + (Width - _light) / 2 + _light / 2f;
            var centerY = _top + (Height - _light) / 2 + _light / 2f;
            var down = glyph is '╭' or '╮';
            var right = glyph is '╭' or '╰';
            float edgeY = down ? _bottom : _top, edgeX = right ? _right : _left;
            float signY = down ? 1 : -1, signX = right ? 1 : -1;
            var radius = Math.Max(0, Math.Min(MathF.Abs(edgeX - centerX), MathF.Abs(edgeY - centerY)) - 1);
            using var path = new CanvasPathBuilder(_drawing);
            path.BeginFigure(centerX / _scale, edgeY / _scale);
            path.AddLine(centerX / _scale, (centerY + signY * radius) / _scale);
            path.AddCubicBezier(
                new(centerX / _scale, (centerY + signY * radius / 4) / _scale),
                new((centerX + signX * radius / 4) / _scale, centerY / _scale),
                new((centerX + signX * radius) / _scale, centerY / _scale));
            path.AddLine(edgeX / _scale, centerY / _scale);
            path.EndFigure(CanvasFigureLoop.Open);
            using var geometry = CanvasGeometry.CreatePath(path);
            _drawing.Antialiasing = CanvasAntialiasing.Antialiased;
            _drawing.DrawGeometry(geometry, _color, _light / _scale);
            _drawing.Antialiasing = CanvasAntialiasing.Aliased;
        }

        private void Diagonals(bool rising, bool falling)
        {
            using (_drawing.CreateLayer(1, new Rect(_left / _scale, _top / _scale, Width / _scale, Height / _scale)))
            {
                _drawing.Antialiasing = CanvasAntialiasing.Antialiased;
                if (rising) _drawing.DrawLine(_left / _scale, _bottom / _scale, _right / _scale, _top / _scale, _color, _light / _scale);
                if (falling) _drawing.DrawLine(_left / _scale, _top / _scale, _right / _scale, _bottom / _scale, _color, _light / _scale);
                _drawing.Antialiasing = CanvasAntialiasing.Aliased;
            }
        }
    }
}
