using AgentTerm.Terminal;
using Microsoft.Graphics.Canvas;

namespace AgentTerm_App;

public sealed partial class TerminalSurface
{
    private double _scrollFraction;

    internal void SetScrollFraction(double fraction)
    {
        if (_scrollFraction == fraction) return;
        _scrollFraction = fraction;
        PositionInput();
        _canvas?.Invalidate();
        UpdateCursorOverlay();
    }

    private static void DrawOverscan(CanvasDrawingSession drawing, TerminalFrame frame, float cellWidth,
        float cellHeight, GlyphLayouts layouts, float gridTop, bool textPhase)
    {
        foreach (var row in frame.OverscanAbove) DrawRow(row);
        foreach (var row in frame.OverscanBelow) DrawRow(row);
        void DrawRow(TerminalOverscanRow row)
        {
            var extra = frame with { Rows = 1, Cells = row.Cells };
            DrawCells(drawing, extra, cellWidth, cellHeight, layouts,
                gridTop + row.ViewportY * cellHeight, 0, 1, 0, frame.Columns, textPhase);
        }
    }
}
