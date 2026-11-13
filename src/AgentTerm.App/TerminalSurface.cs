using System;
using System.Collections.Generic;
using System.Diagnostics;
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

public sealed class TerminalSurface : UserControl
{
    private const string FallbackFont = "Consolas";
    private static readonly Lazy<string[]> AvailableFonts = new(FindTerminalFonts);
    private CanvasControl? _canvas;
    private readonly GlyphLayouts _glyphLayouts = new();
    private CanvasRenderTarget? _renderTarget;
    private TerminalFrame? _paintedFrame;
    private (double Width, double Height, float Dpi, float CellWidth, float CellHeight, float FontSize,
        string FontFamily, bool Transparent, float GridTop) _rasterMetrics;
    internal event Action<double>? DrawCompleted;
    private readonly TextBox _input = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0),
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
        Opacity = 0, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(FallbackFont), FontSize = 14,
        IsSpellCheckEnabled = false, IsTextPredictionEnabled = false,
        ContextFlyout = null, SelectionFlyout = null,
        AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap
    };
    internal TextBox Input => _input;
    public bool FocusInput(FocusState state) => _input.Focus(state);

    private bool _compositionVisible;
    internal void SetCompositionVisible(bool visible)
    {
        _compositionVisible = visible;
        _input.Opacity = visible ? 1 : 0;
        _canvas?.Invalidate();
    }

    internal string TerminalFontFamily => _terminalFontFamily;

    private bool _bottomAligned;
    public void SetBottomAligned(bool aligned)
    {
        if (_bottomAligned == aligned) return;
        _bottomAligned = aligned;
        PositionInput();
        _canvas?.Invalidate();
    }

    internal TerminalGridGeometry Geometry => _frame is { } frame ? GeometryFor(frame) : new(0, 0, 0, CellWidth, CellHeight);
    internal TerminalGridGeometry GeometryFor(TerminalFrame frame) =>
        TerminalGridGeometry.For(frame, ActualHeight, CellWidth, CellHeight, _bottomAligned);

    private TerminalFrame? _frame;
    private bool _cursorPhase = true;
    private bool _resourcesReady;
    private bool _suspended;
    private float _terminalFontSize = 14;
    private string _terminalFontFamily = FallbackFont;
    private bool _acrylicBackground;
    public event Action? MetricsChanged;
    public float CellWidth { get; private set; } = 8.4f;
    public float CellHeight { get; private set; } = 19f;

    public TerminalSurface()
    {
        IsTabStop = false;
        var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _input.Resources["TextControlBackgroundFocused"] = transparent;
        _input.Resources["TextControlBorderBrushFocused"] = transparent;
        _input.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        _input.Resources["TextControlBackgroundPointerOver"] = transparent;
        _input.Resources["TextControlBorderBrushPointerOver"] = transparent;
        var root = new Grid();
        root.Children.Add(_input);
        Content = root;
        Loaded += (_, _) =>
        {
            if (!_suspended) AttachCanvas();
        };
        Unloaded += (_, _) => { if (!IsLoaded) ReleaseCanvas(); };
        ActualThemeChanged += (_, _) => _canvas?.Invalidate();
        SizeChanged += (_, _) => { PositionInput(); _canvas?.Invalidate(); };
    }

    public void SetAcrylicBackground(bool enabled)
    {
        if (_acrylicBackground == enabled) return;
        _acrylicBackground = enabled;
        _canvas?.Invalidate();
    }

    internal static IReadOnlyList<string> FontFamilies => AvailableFonts.Value;

    internal static string ResolveFontFamily(string? preferredFamily) =>
        FontFamilies.FirstOrDefault(name => string.Equals(name, preferredFamily, StringComparison.OrdinalIgnoreCase))
            ?? FontFamilies[0];

    private static string[] FindTerminalFonts()
    {
        var installed = CanvasTextFormat.GetSystemFontFamilies().ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        _glyphLayouts.Clear();
        ResetRaster();
        _input.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(family);
        _input.FontSize = size;
        if (_resourcesReady) MeasureCells();
        _canvas?.Invalidate();
    }

    public void SetFrame(TerminalFrame frame)
    {
        if (ReferenceEquals(_frame, frame)) return;
        _frame = frame;
        PositionInput();
        _canvas?.Invalidate();
    }

    public void SetCursorPhase(bool visible)
    {
        if (_cursorPhase == visible) return;
        _cursorPhase = visible;
        if (_frame?.Cursor.Blink == true) _canvas?.Invalidate();
    }

    public void RefreshCursor()
    {
        _cursorPhase = true;
        _canvas?.Invalidate();
    }

    public void ReleaseResources()
    {
        _suspended = true;
        ReleaseCanvas();
    }

    internal void SuspendRendering()
    {
        _suspended = true;
        ReleaseCanvas();
    }

    internal void ResumeRendering()
    {
        _suspended = false;
        if (IsLoaded) AttachCanvas();
    }

    private void AttachCanvas()
    {
        if (_canvas is not null) return;
        var canvas = new CanvasControl();
        _canvas = canvas;
        canvas.Draw += Draw;
        canvas.CreateResources += CreateResources;
        ((Grid)Content).Children.Insert(0, canvas);
        canvas.Invalidate();
    }

    private void ReleaseCanvas()
    {
        var canvas = _canvas;
        _canvas = null;
        _resourcesReady = false;
        _glyphLayouts.Clear();
        ResetRaster();
        if (canvas is null) return;
        canvas.Draw -= Draw;
        canvas.CreateResources -= CreateResources;
        canvas.RemoveFromVisualTree();
        ((Grid)Content).Children.Remove(canvas);
    }

    private void CreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        _glyphLayouts.Clear();
        ResetRaster();
        _resourcesReady = true;
        MeasureCells();
    }

    private void MeasureCells()
    {
        using var format = CreateFormat(false, false, _terminalFontSize, _terminalFontFamily);
        using var layout = new CanvasTextLayout(_canvas!, "M", format, 100, 100);
        CellWidth = (float)Math.Ceiling(layout.LayoutBounds.Width);
        CellHeight = (float)Math.Ceiling(layout.LayoutBounds.Height);
        PositionInput();
        MetricsChanged?.Invoke();
    }

    private void PositionInput()
    {
        if (_frame is not { } frame) return;
        var caret = Geometry.Caret(frame.Cursor);
        _input.Margin = new Thickness(caret.X, caret.Y, 0, 0);
        _input.Width = Math.Max(CellWidth, (frame.Columns * CellWidth) - caret.X);
        _input.Height = CellHeight;
    }

    private void ResetRaster()
    {
        _renderTarget?.Dispose();
        _renderTarget = null;
        _paintedFrame = null;
    }

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_suspended) { args.DrawingSession.Clear(Microsoft.UI.Colors.Transparent); return; }
        var measured = DrawCompleted;
        var started = measured is null ? 0 : Stopwatch.GetTimestamp();
        if (_frame is { } frame)
        {
            var top = (float)Geometry.Top;
            UpdateRaster(args.DrawingSession.Device, frame, top);
            args.DrawingSession.Clear(Microsoft.UI.Colors.Transparent);
            args.DrawingSession.DrawImage(_renderTarget);
            DrawCursor(args.DrawingSession, frame, CellWidth, CellHeight, _glyphLayouts,
                _input.FocusState != FocusState.Unfocused, _cursorPhase, _compositionVisible, top);
        }
        else
        {
            var shade = ActualTheme == ElementTheme.Light ? (byte)250 : (byte)0;
            args.DrawingSession.Clear(_acrylicBackground ? Color.FromArgb(0, 0, 0, 0) : Color.FromArgb(255, shade, shade, shade));
        }
        measured?.Invoke(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private void UpdateRaster(CanvasDevice device, TerminalFrame frame, float top)
    {
        var metrics = (ActualWidth, ActualHeight, _canvas!.Dpi, CellWidth, CellHeight,
            _terminalFontSize, _terminalFontFamily, _acrylicBackground, top);
        if (_renderTarget is null || _rasterMetrics != metrics)
        {
            ResetRaster();
            _renderTarget = new CanvasRenderTarget(device, (float)ActualWidth, (float)ActualHeight, _canvas.Dpi);
            _rasterMetrics = metrics;
        }
        var previous = _paintedFrame;
        var full = previous is null || previous.Columns != frame.Columns || previous.Rows != frame.Rows ||
            previous.Viewport.TopInset != frame.Viewport.TopInset || previous.Background != frame.Background ||
            previous.SelectionBackground != frame.SelectionBackground || previous.SelectionForeground != frame.SelectionForeground;
        _glyphLayouts.Configure(_terminalFontFamily, _terminalFontSize, CellWidth, CellHeight);
        _glyphLayouts.FitGrid(frame.Cells.Length);
        if (full)
        {
            using (var drawing = _renderTarget.CreateDrawingSession())
                DrawFrameCore(drawing, frame, CellWidth, CellHeight, _glyphLayouts, false, false,
                    _terminalFontSize, _acrylicBackground, _terminalFontFamily, true, top);
        }
        else if (!ReferenceEquals(previous!.Cells, frame.Cells))
        {
            using var drawing = _renderTarget.CreateDrawingSession();
            var scale = _canvas.Dpi / 96;
            var rows = TerminalGridGeometry.VisibleRows(frame);
            var spans = new List<(int FirstRow, int EndRow, int FirstColumn, int EndColumn)>();
            var latest = new Dictionary<(int First, int End), int>();
            for (var row = 0; row < rows; row++)
            for (var col = 0; col < frame.Columns;)
            {
                if (previous.Cells[row * frame.Columns + col] == frame.Cells[row * frame.Columns + col]) { col++; continue; }
                var first = Math.Max(0, col - 2);
                var lastChanged = col++;
                while (col < frame.Columns && col <= lastChanged + 4)
                {
                    if (previous.Cells[row * frame.Columns + col] != frame.Cells[row * frame.Columns + col]) lastChanged = col;
                    col++;
                }
                var end = Math.Min(frame.Columns, lastChanged + 3);
                if (latest.TryGetValue((first, end), out var previousSpan) && spans[previousSpan].EndRow == row)
                {
                    var span = spans[previousSpan];
                    spans[previousSpan] = (span.FirstRow, row + 1, first, end);
                }
                else
                {
                    latest[(first, end)] = spans.Count;
                    spans.Add((row, row + 1, first, end));
                }
            }
            foreach (var span in spans)
            {
                var left = MathF.Floor(span.FirstColumn * CellWidth * scale) / scale;
                var right = MathF.Ceiling(span.EndColumn * CellWidth * scale) / scale;
                var upper = MathF.Floor((top + span.FirstRow * CellHeight) * scale) / scale;
                var lower = MathF.Ceiling((top + span.EndRow * CellHeight) * scale) / scale;
                var damage = new Rect(left, upper, right - left, lower - upper);
                using var clip = drawing.CreateLayer(1, damage);
                drawing.Blend = CanvasBlend.Copy;
                drawing.FillRectangle(damage, _acrylicBackground ? Microsoft.UI.Colors.Transparent : ToColor(frame.Background));
                drawing.Blend = CanvasBlend.SourceOver;
                DrawCells(drawing, frame, CellWidth, CellHeight, _glyphLayouts, _acrylicBackground, top,
                    Math.Max(0, (int)MathF.Floor((upper - top) / CellHeight)),
                    Math.Min(rows, (int)MathF.Ceiling((lower - top) / CellHeight)),
                    Math.Max(0, (int)MathF.Floor(left / CellWidth) - 1),
                    Math.Min(frame.Columns, (int)MathF.Ceiling(right / CellWidth) + 1));
            }
        }
        _paintedFrame = frame;
    }

    public static void DrawFrame(CanvasDrawingSession drawing, TerminalFrame frame, float cellWidth,
        float cellHeight, bool focused, bool cursorPhase, float fontSize = 14, bool transparentBackground = false,
        string fontFamily = FallbackFont, bool hideCursor = false, float gridTop = 0)
    {
        using var layouts = new GlyphLayouts();
        DrawFrameCore(drawing, frame, cellWidth, cellHeight, layouts, focused, cursorPhase, fontSize,
            transparentBackground, fontFamily, hideCursor, gridTop);
    }

    private static void DrawFrameCore(CanvasDrawingSession drawing, TerminalFrame frame, float cellWidth,
        float cellHeight, GlyphLayouts layouts, bool focused, bool cursorPhase, float fontSize, bool transparentBackground,
        string fontFamily, bool hideCursor, float gridTop)
    {
        layouts.Configure(fontFamily, fontSize, cellWidth, cellHeight);
        layouts.FitGrid(frame.Cells.Length);
        drawing.Clear(transparentBackground ? Color.FromArgb(0, 0, 0, 0) : ToColor(frame.Background));
        DrawCells(drawing, frame, cellWidth, cellHeight, layouts, transparentBackground, gridTop,
            0, TerminalGridGeometry.VisibleRows(frame), 0, frame.Columns);
        DrawCursor(drawing, frame, cellWidth, cellHeight, layouts, focused, cursorPhase, hideCursor, gridTop);
    }

    private static void DrawCells(CanvasDrawingSession drawing, TerminalFrame frame, float cellWidth,
        float cellHeight, GlyphLayouts layouts, bool transparentBackground, float gridTop,
        int firstRow, int endRow, int firstColumn, int endColumn)
    {
        var device = drawing.Device;
        var antialiasing = drawing.Antialiasing;
        drawing.Antialiasing = CanvasAntialiasing.Aliased;
        for (var row = firstRow; row < endRow; row++)
        for (var col = firstColumn; col < endColumn; col++)
        {
            var cell = frame.Cells[row * frame.Columns + col];
            if (cell.Selected || !transparentBackground || cell.Background != frame.Background)
                drawing.FillRectangle(col * cellWidth, gridTop + row * cellHeight, cellWidth, cellHeight, cell.Selected ? ToColor(frame.SelectionBackground) : ToColor(cell.Background));
        }
        drawing.Antialiasing = antialiasing;
        for (var row = firstRow; row < endRow; row++)
        for (var col = firstColumn; col < endColumn; col++)
        {
            var cell = frame.Cells[row * frame.Columns + col];
            if (cell.Width == 0 || cell.Hidden) continue;
            var foreground = cell.Selected ? ToColor(frame.SelectionForeground) : ToColor(cell.Foreground);
            if (cell.Dim && !cell.Selected) foreground = Color.FromArgb(255, (byte)(foreground.R / 2), (byte)(foreground.G / 2), (byte)(foreground.B / 2));
            var x = col * cellWidth;
            var y = gridTop + row * cellHeight;
            var width = cellWidth * cell.Width;
            if (!string.IsNullOrWhiteSpace(cell.Text))
            {
                drawing.DrawTextLayout(layouts.Get(device, cell.Text, width, cell.Bold, cell.Italic), x, y, foreground);
            }
            if (cell.Underline) drawing.DrawLine(x, y + cellHeight - 2, x + width, y + cellHeight - 2, foreground);
            if (cell.Strikethrough) drawing.DrawLine(x, y + cellHeight / 2, x + width, y + cellHeight / 2, foreground);
        }
    }

    private static void DrawCursor(CanvasDrawingSession drawing, TerminalFrame frame, float cellWidth,
        float cellHeight, GlyphLayouts layouts, bool focused, bool cursorPhase, bool hideCursor, float gridTop)
    {
        var rows = TerminalGridGeometry.VisibleRows(frame);
        var device = drawing.Device;
        var cursor = frame.Cursor;
        if (hideCursor || !cursor.Visible || (cursor.Blink && focused && !cursorPhase) || cursor.Column < 0 || cursor.Column >= frame.Columns || cursor.Row < 0 || cursor.Row >= rows) return;
        var cx = cursor.Column * cellWidth;
        var cy = gridTop + cursor.Row * cellHeight;
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
                drawing.DrawTextLayout(layouts.Get(device, cell.Text, cursorWidth, cell.Bold, cell.Italic),
                    cx, cy, ToColor(frame.CursorText ?? cell.Background));
            }
        }
    }

    private sealed class GlyphLayouts : IDisposable
    {
        private int _capacity = 1024;
        private long _created;
        private readonly record struct Key(string Text, float Width, bool Bold, bool Italic);
        private readonly Dictionary<Key, CanvasTextLayout> _layouts = new();
        private readonly Queue<Key> _order = new();
        private (string Family, float Size, float CellWidth, float CellHeight) _metrics;

        internal void Configure(string family, float size, float cellWidth, float cellHeight)
        {
            var metrics = (family, size, cellWidth, cellHeight);
            if (_metrics == metrics) return;
            Clear();
            _metrics = metrics;
        }

        internal void FitGrid(int cells)
        {
            _capacity = Math.Clamp(cells * 2, 1024, 65536);
            while (_layouts.Count > _capacity) EvictOldest();
        }

        internal CanvasTextLayout Get(CanvasDevice device, string text, float width, bool bold, bool italic)
        {
            var key = new Key(text, width, bold, italic);
            if (_layouts.TryGetValue(key, out var layout)) return layout;
            using var format = CreateFormat(bold, italic, _metrics.Size, _metrics.Family);
            layout = new CanvasTextLayout(device, text, format, width, _metrics.CellHeight)
            {
                Options = CanvasDrawTextOptions.Clip
            };
            _created++;
            if (_layouts.Count == _capacity) EvictOldest();
            _layouts.Add(key, layout);
            _order.Enqueue(key);
            return layout;
        }

        private void EvictOldest()
        {
            var oldest = _order.Dequeue();
            _layouts[oldest].Dispose();
            _layouts.Remove(oldest);
        }

        internal void Clear()
        {
            foreach (var layout in _layouts.Values) layout.Dispose();
            _layouts.Clear();
            _order.Clear();
        }

        public void Dispose() => Clear();
    }

    private static CanvasTextFormat CreateFormat(bool bold, bool italic, float fontSize, string fontFamily) => new()
    {
        FontFamily = fontFamily,
        FontSize = fontSize,
        FontWeight = new FontWeight { Weight = (ushort)(bold ? 700 : 400) },
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        WordWrapping = CanvasWordWrapping.NoWrap,
        Options = CanvasDrawTextOptions.Clip
    };

    private static Color ToColor(TerminalColor color) => Color.FromArgb(255, color.R, color.G, color.B);
}

internal readonly record struct TerminalGridGeometry(double Top, int Columns, int Rows, float CellWidth, float CellHeight)
{
    internal double Bottom => Top + Rows * CellHeight;

    internal static int VisibleRows(TerminalFrame frame) => Math.Clamp(frame.Rows - frame.Viewport.TopInset, 0, frame.Rows);

    internal static TerminalGridGeometry For(TerminalFrame frame, double surfaceHeight, float cellWidth, float cellHeight, bool bottomAligned)
    {
        var remainder = bottomAligned ? surfaceHeight - frame.Rows * cellHeight : 0;
        return new(remainder + frame.Viewport.TopInset * cellHeight, frame.Columns, VisibleRows(frame), cellWidth, cellHeight);
    }

    internal double RowTop(long frameRow) => Top + frameRow * CellHeight;

    internal bool TryCellAt(Point point, out int column, out int row)
    {
        column = row = -1;
        if (Rows == 0 || Columns == 0 || point.Y < Top || point.Y >= Bottom || point.X < 0 || point.X >= Columns * CellWidth) return false;
        column = Math.Clamp((int)Math.Floor(point.X / CellWidth), 0, Columns - 1);
        row = Math.Clamp((int)Math.Floor((point.Y - Top) / CellHeight), 0, Rows - 1);
        return true;
    }

    internal (int Column, int Row) ClampedCellAt(Point point) =>
        (Math.Clamp((int)Math.Floor(point.X / CellWidth), 0, Math.Max(0, Columns - 1)),
         Math.Clamp((int)Math.Floor((point.Y - Top) / CellHeight), 0, Math.Max(0, Rows - 1)));

    internal Point ToFrame(Point point) => new(point.X, point.Y - Top);

    internal Point ToFrameClamped(Point point) =>
        new(Math.Clamp(point.X, 0, Math.Max(0, Columns * CellWidth - 1)), Math.Clamp(point.Y - Top, 0, Math.Max(0, Rows * CellHeight - 1)));

    internal bool RowsBlank(TerminalFrame frame, long firstRow, int count)
    {
        for (var row = Math.Max(0, firstRow); row < Math.Min(firstRow + count, Rows); row++)
            for (var column = 0; column < frame.Columns; column++)
            {
                var cell = frame.Cells[row * frame.Columns + column];
                if (!string.IsNullOrWhiteSpace(cell.Text) || cell.Background != frame.Background || cell.Underline || cell.Strikethrough)
                    return false;
            }
        return true;
    }

    internal Point Caret(TerminalCursor cursor) =>
        new(Math.Clamp(cursor.Column, 0, Math.Max(0, Columns - 1)) * CellWidth,
            RowTop(Math.Clamp(cursor.Row, 0, Math.Max(0, Rows - 1))));
}
