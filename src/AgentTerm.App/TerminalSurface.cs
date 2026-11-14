using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
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

public sealed partial class TerminalSurface : UserControl
{
    private const string FallbackFont = "Consolas";
    private static readonly Lazy<string[]> AvailableFonts = new(FindTerminalFonts);
    private static Task? _fontLookup;
    private CanvasControl? _cursorCanvas;
    private double _cursorOpacity;
    private (Thickness Margin, double Width, double Height) _inputLayout;
    private readonly GlyphLayouts _cursorLayouts = new();
    private TerminalFrame? _paintedFrame;
    private (Rect Bounds, TerminalCursor Cursor, TerminalCell Cell, TerminalColor? CursorText, bool Focused,
        float CellWidth, float CellHeight, float FontSize, string FontFamily, double GridTop) _cursorContent;
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
        UpdateCursorOverlay();
    }

    internal string TerminalFontFamily
    {
        get
        {
            if (_terminalFontFamily is null)
            {
                _terminalFontFamily = ResolveFontFamily(_preferredFontFamily);
                _input.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(_terminalFontFamily);
            }
            return _terminalFontFamily;
        }
    }

    private bool _bottomAligned;
    public void SetBottomAligned(bool aligned)
    {
        if (_bottomAligned == aligned) return;
        _bottomAligned = aligned;
        PositionInput();
        RequestRaster();
        UpdateCursorOverlay();
    }

    internal TerminalGridGeometry Geometry => _frame is { } frame ? GeometryFor(frame) : new(0, 0, 0, CellWidth, CellHeight);
    internal TerminalGridGeometry GeometryFor(TerminalFrame frame) =>
        TerminalGridGeometry.For(frame, ActualHeight, CellWidth, CellHeight, _bottomAligned, _scrollFraction);

    private TerminalFrame? _frame;
    private bool _cursorPhase = true;
    private bool _resourcesReady;
    private bool _suspended;
    private float _terminalFontSize = 14;
    private string _preferredFontFamily = FallbackFont;
    private string? _terminalFontFamily = FallbackFont;
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
        root.Children.Add(_rasterHost);
        root.Children.Add(_input);
        Content = root;
        PrefetchFonts();
        _input.GotFocus += (_, _) => UpdateCursorOverlay();
        _input.LostFocus += (_, _) => UpdateCursorOverlay();
        Loaded += (_, _) =>
        {
            TrackXamlRoot(XamlRoot);
            if (!_suspended) AttachCanvas();
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            TrackXamlRoot(null);
            ReleaseCanvas();
        };
        ActualThemeChanged += (_, _) => UpdateHostBackground();
        SizeChanged += (_, _) => { PositionInput(); RequestRaster(); UpdateCursorOverlay(); };
    }

    public void SetAcrylicBackground(bool enabled)
    {
        if (_acrylicBackground == enabled) return;
        _acrylicBackground = enabled;
        UpdateHostBackground();
        RequestRaster();
    }

    internal static IReadOnlyList<string> FontFamilies => AvailableFonts.Value;

    private static void PrefetchFonts() => _fontLookup ??= Task.Run(() => AvailableFonts.Value);

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
        var family = AvailableFonts.IsValueCreated ? ResolveFontFamily(preferredFamily) : null;
        if (_terminalFontSize == size && (family is null
            ? _terminalFontFamily is null && _preferredFontFamily == preferredFamily
            : _terminalFontFamily == family)) return;
        _terminalFontSize = size;
        _preferredFontFamily = preferredFamily;
        _terminalFontFamily = family;
        if (family is not null) _input.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(family);
        _input.FontSize = size;
        if (_resourcesReady) MeasureCells();
        RequestRaster();
    }

    public void SetFrame(TerminalFrame frame)
    {
        if (ReferenceEquals(_frame, frame)) return;
        var previous = _frame;
        _frame = frame;
        PositionInput();
        if (previous is null) UpdateHostBackground();
        if (previous is null || !RasterEquivalent(previous, frame)) RequestRaster();
        else if (ReferenceEquals(_paintedFrame, previous)) _paintedFrame = frame;
        NotifyAutomationFrameChanged(previous is null || !ReferenceEquals(previous.Cells, frame.Cells) ||
            previous.Viewport != frame.Viewport || previous.Columns != frame.Columns || previous.Rows != frame.Rows,
            previous is null || previous.HasSelection != frame.HasSelection ||
            frame.HasSelection && !ReferenceEquals(previous.Cells, frame.Cells));
        UpdateCursorOverlay();
    }

    public void SetCursorPhase(bool visible)
    {
        if (_cursorPhase == visible) return;
        _cursorPhase = visible;
        UpdateCursorOverlay();
        if (HasBlinkingText)
        {
            RequestRaster();
            _cursorCanvas?.Invalidate();
        }
    }

    public void RefreshCursor()
    {
        SetCursorPhase(true);
        UpdateCursorOverlay();
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
        if (_cursorCanvas is not null) return;
        AttachRaster();
        var cursor = new CanvasControl
        {
            IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Width = 0, Height = 0, Opacity = 0
        };
        _cursorCanvas = cursor;
        _cursorContent = default;
        _cursorOpacity = 0;
        cursor.Draw += DrawCursorOverlay;
        cursor.CreateResources += CreateCursorResources;
        ((Grid)Content).Children.Insert(1, cursor);
    }

    private void ReleaseCanvas()
    {
        var cursor = _cursorCanvas;
        _cursorCanvas = null;
        _cursorContent = default;
        _cursorLayouts.Clear();
        ReleaseRaster();
        if (cursor is null) return;
        cursor.Draw -= DrawCursorOverlay;
        cursor.CreateResources -= CreateCursorResources;
        cursor.RemoveFromVisualTree();
        ((Grid)Content).Children.Remove(cursor);
    }

    private void CreateCursorResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args) =>
        _cursorLayouts.Clear();

    private void MeasureCells()
    {
        using var format = CreateFormat(false, false, _terminalFontSize, TerminalFontFamily);
        using var layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), "M", format, 100, 100);
        CellWidth = (float)Math.Ceiling(layout.LayoutBounds.Width);
        CellHeight = (float)Math.Ceiling(layout.LayoutBounds.Height);
        PositionInput();
        UpdateCursorOverlay();
        MetricsChanged?.Invoke();
    }

    private void PositionInput()
    {
        if (_frame is not { } frame) return;
        var caret = Geometry.Caret(frame.Cursor);
        if (!Changed(ref _inputLayout,
            (new Thickness(caret.X, caret.Y, 0, 0), Math.Max(CellWidth, (frame.Columns * CellWidth) - caret.X), CellHeight))) return;
        (_input.Margin, _input.Width, _input.Height) = _inputLayout;
    }

    private static bool Changed<T>(ref T shadow, T value)
    {
        if (EqualityComparer<T>.Default.Equals(shadow, value)) return false;
        shadow = value;
        return true;
    }

    private static int _nextCapturePaneId;
    internal int CapturePaneId { get; } = Interlocked.Increment(ref _nextCapturePaneId);

    private void UpdateCursorOverlay()
    {
        if (_cursorCanvas is not { } overlay) return;
        var frame = _paintedFrame;
        var focused = _input.FocusState != FocusState.Unfocused;
        if (_suspended || !_resourcesReady || _compositionVisible || frame is null || !frame.Cursor.Visible ||
            frame.Cursor.Column < 0 || frame.Cursor.Column >= frame.Columns ||
            frame.Cursor.Row < 0 || frame.Cursor.Row >= TerminalGridGeometry.VisibleRows(frame))
        {
            if (Changed(ref _cursorOpacity, 0)) overlay.Opacity = 0;
            return;
        }
        var cursor = frame.Cursor;
        var column = cursor.WideTail && cursor.Column > 0 ? cursor.Column - 1 : cursor.Column;
        var cell = frame.Cells[cursor.Row * frame.Columns + column];
        var (bounds, gridTop) = CursorOverlayGeometry(GeometryFor(frame), column, cursor.Row, Math.Max(1, (int)cell.Width));
        var content = (bounds, cursor, cell, frame.CursorText, focused, CellWidth, CellHeight, _terminalFontSize, TerminalFontFamily, gridTop);
        if (content != _cursorContent)
        {
            if (bounds != _cursorContent.Bounds)
            {
                overlay.Margin = new Thickness(bounds.X, bounds.Y, 0, 0);
                overlay.Width = bounds.Width;
                overlay.Height = bounds.Height;
            }
            _cursorContent = content;
            overlay.Invalidate();
        }
        if (Changed(ref _cursorOpacity, cursor.Blink && focused && !_cursorPhase ? 0 : 1)) overlay.Opacity = _cursorOpacity;
    }

    internal static (Rect Bounds, double GridTop) CursorOverlayGeometry(TerminalGridGeometry geometry,
        int column, int row, int cellColumns)
    {
        var x = column * geometry.CellWidth;
        var y = (float)geometry.Top + row * geometry.CellHeight;
        var width = geometry.CellWidth * cellColumns;
        var left = MathF.Floor(x / 4) * 4 - 4;
        var upper = MathF.Floor(y / 4) * 4 - 4;
        return (new Rect(left, upper, MathF.Ceiling((x + width) / 4) * 4 + 4 - left,
            MathF.Ceiling((y + geometry.CellHeight) / 4) * 4 + 4 - upper), geometry.Top);
    }

    private void DrawCursorOverlay(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var drawing = args.DrawingSession;
        drawing.Clear(Microsoft.UI.Colors.Transparent);
        if (_suspended || _paintedFrame is not { } frame) return;
        var content = _cursorContent;
        _cursorLayouts.Configure(TerminalFontFamily, _terminalFontSize, CellWidth, CellHeight);
        drawing.Transform = Matrix3x2.CreateTranslation(-(float)content.Bounds.X, -(float)content.Bounds.Y);
        DrawCursor(drawing, frame, CellWidth, CellHeight, _cursorLayouts, content.Focused, true, false, (float)content.GridTop, _cursorPhase);
    }

    private static bool HasText(TerminalFrame frame, int row, int column) =>
        !string.IsNullOrWhiteSpace(frame.Cells[row * frame.Columns + column].Text);

    internal static bool CellNeedsRepaint(TerminalCell previous, TerminalCell current, bool textPhaseChanged) =>
        !ReferenceEquals(previous, current) || textPhaseChanged && current.Blink && !current.Hidden;

    public static void DrawFrame(CanvasDrawingSession drawing, TerminalFrame frame, float cellWidth,
        float cellHeight, bool focused, bool cursorPhase, float fontSize = 14, bool transparentBackground = false,
        string fontFamily = FallbackFont, bool hideCursor = false, float gridTop = 0, bool textPhase = true)
    {
        using var layouts = new GlyphLayouts();
        using var images = new ImageBitmaps();
        DrawFrameCore(drawing, frame, cellWidth, cellHeight, layouts, focused, cursorPhase, fontSize,
            transparentBackground, fontFamily, hideCursor, gridTop, textPhase, images);
        drawing.Flush();
    }

    private static void DrawFrameCore(CanvasDrawingSession drawing, TerminalFrame frame, float cellWidth,
        float cellHeight, GlyphLayouts layouts, bool focused, bool cursorPhase, float fontSize, bool transparentBackground,
        string fontFamily, bool hideCursor, float gridTop, bool textPhase = true, ImageBitmaps? images = null, float? surfaceHeight = null)
    {
        layouts.Configure(fontFamily, fontSize, cellWidth, cellHeight);
        layouts.FitGrid(frame.Cells.Length);
        drawing.Clear(transparentBackground ? Color.FromArgb(0, 0, 0, 0) : ToColor(frame.Background));
        var overscan = frame.OverscanAbove.Length != 0 || frame.OverscanBelow.Length != 0;
        Rect? viewportClip = overscan ? new Rect(0, 0, frame.Columns * cellWidth,
            surfaceHeight ?? frame.Rows * cellHeight) : null;
        DrawCells(drawing, frame, cellWidth, cellHeight, layouts, gridTop,
            frame.OverscanAbove.Length != 0 ? -1 : 0,
            frame.OverscanBelow.Length != 0 ? frame.Rows + 1 : TerminalGridGeometry.VisibleRows(frame),
            0, frame.Columns, textPhase, images, viewportClip);
        DrawCursor(drawing, frame, cellWidth, cellHeight, layouts, focused, cursorPhase, hideCursor, gridTop, textPhase);
    }

    private static void DrawCells(CanvasDrawingSession drawing, TerminalFrame frame, float cellWidth,
        float cellHeight, GlyphLayouts layouts, float gridTop,
        int firstRow, int endRow, int firstColumn, int endColumn, bool textPhase, ImageBitmaps? images = null,
        Rect? viewportClip = null, Rect? damageClip = null)
    {
        TerminalCell CellAt(int row, int col) => row < 0 ? frame.OverscanAbove[0].Cells[col]
            : row >= frame.Rows ? frame.OverscanBelow[0].Cells[col] : frame.Cells[row * frame.Columns + col];
        var device = drawing.Device;
        DrawImages(drawing, frame, TerminalImageLayer.BelowBackground, cellWidth, cellHeight, gridTop, images, viewportClip, damageClip);
        var antialiasing = drawing.Antialiasing;
        drawing.Antialiasing = CanvasAntialiasing.Aliased;
        for (var row = firstRow; row < endRow; row++)
        {
            var y = gridTop + row * cellHeight;
            var runStart = -1;
            var runColor = frame.Background;
            for (var col = firstColumn; col <= endColumn; col++)
            {
                var fill = false;
                var color = frame.Background;
                if (col < endColumn)
                {
                    var cell = CellAt(row, col);
                    fill = cell.Selected || cell.HasBackground || cell.Background != frame.Background;
                    color = cell.Selected ? frame.SelectionBackground : cell.Background;
                }
                if (runStart >= 0 && (!fill || color != runColor))
                {
                    drawing.FillRectangle(runStart * cellWidth, y, (col - runStart) * cellWidth, cellHeight, ToColor(runColor));
                    runStart = -1;
                }
                if (fill && runStart < 0) (runStart, runColor) = (col, color);
            }
        }
        drawing.Antialiasing = antialiasing;
        DrawImages(drawing, frame, TerminalImageLayer.BelowText, cellWidth, cellHeight, gridTop, images, viewportClip, damageClip);
        if (frame.SearchHighlights is { } highlights)
            foreach (var span in highlights.Spans)
            {
                if (span.Row < firstRow || span.Row >= endRow || span.Row < 0 || span.Row >= frame.Rows) continue;
                var first = Math.Max(firstColumn, Math.Clamp(span.StartColumn, 0, frame.Columns - 1));
                var last = Math.Clamp(span.EndColumn, 0, frame.Columns - 1);
                last = Math.Min(endColumn - 1, last + Math.Max(1, (int)CellAt(span.Row, last).Width) - 1);
                if (last < first) continue;
                var color = span.Selected ? Color.FromArgb(100, 255, 170, 30) : Color.FromArgb(65, 255, 210, 70);
                drawing.FillRectangle(first * cellWidth, gridTop + span.Row * cellHeight,
                    (last - first + 1) * cellWidth, cellHeight, color);
            }
        var glyphRuns = TextMode != TextRendering.Layout && drawing.Transform.IsIdentity;
        var dpi = drawing.Dpi;
        for (var row = firstRow; row < endRow; row++)
        {
            var y = gridTop + row * cellHeight;
            var start = firstColumn;
            while (start > 0 && JoinsTextRun(frame, CellAt(row, start - 1), CellAt(row, start), textPhase)) start--;
            while (start < endColumn)
            {
                var first = CellAt(row, start);
                var end = start + 1;
                if (!DrawsText(first, textPhase)) { start = end; continue; }
                while (end < frame.Columns && JoinsTextRun(frame, CellAt(row, end - 1), CellAt(row, end), textPhase)) end++;
                var foreground = TextColor(frame, first);
                var shape = glyphRuns && end - start > 1;
                if (shape)
                {
                    layouts.Runs.Begin();
                    for (var col = start; col < end; col++) layouts.Runs.AppendCell(CellAt(row, col).Text);
                    if (layouts.Runs.Draw(drawing, device, dpi, start, y, foreground, first.Bold, first.Italic)) { start = end; continue; }
                }
                CanvasTextLayout? run = null;
                if (end - start > 1 && !shape)
                {
                    layouts.ResetKey();
                    for (var col = start; col < end; col++) layouts.AppendCell(CellAt(row, col).Text);
                    run = layouts.GetRun(device, end - start, first.Bold, first.Italic);
                }
                if (run is not null) drawing.DrawTextLayout(run, start * cellWidth, y, foreground);
                else
                    for (var col = start; col < end; col++)
                    {
                        var cell = CellAt(row, col);
                        drawing.DrawTextLayout(layouts.Get(device, cell.Text, cell.Width, cell.Bold, cell.Italic), col * cellWidth, y, foreground);
                    }
                start = end;
            }
            for (var col = firstColumn; col < endColumn; col++)
            {
                var cell = CellAt(row, col);
                if (cell.Width == 0 || cell.Hidden || (cell.Blink && !textPhase)) continue;
                DrawDecorations(drawing, cell, col * cellWidth, y, cellWidth * cell.Width, cellHeight, TextColor(frame, cell));
            }
        }
        DrawImages(drawing, frame, TerminalImageLayer.AboveText, cellWidth, cellHeight, gridTop, images, viewportClip, damageClip);
    }

    private static bool DrawsText(TerminalCell cell, bool textPhase) =>
        cell.Width != 0 && !cell.Hidden && (!cell.Blink || textPhase) && !string.IsNullOrWhiteSpace(cell.Text);

    internal static bool JoinsTextRun(TerminalFrame frame, TerminalCell left, TerminalCell right, bool textPhase) =>
        left.Width == 1 && right.Width == 1 && DrawsText(left, textPhase) && DrawsText(right, textPhase) &&
        left.Bold == right.Bold && left.Italic == right.Italic && left.Blink == right.Blink &&
        TextColor(frame, left) == TextColor(frame, right);

    private static Color TextColor(TerminalFrame frame, TerminalCell cell)
    {
        var color = ToColor(cell.Selected ? frame.SelectionForeground : cell.Foreground);
        return cell.Dim && !cell.Selected ? Color.FromArgb(255, (byte)(color.R / 2), (byte)(color.G / 2), (byte)(color.B / 2)) : color;
    }

    private static void DrawDecorations(CanvasDrawingSession drawing, TerminalCell cell, float x, float y,
        float width, float cellHeight, Color foreground, bool cursorText = false)
    {
        if (cell.Underline)
        {
            var underline = cursorText || cell.Selected ? foreground : cell.UnderlineColor is { } uc ? ToColor(uc) : foreground;
            if (!cursorText && cell.Dim && !cell.Selected && cell.UnderlineColor is not null) underline = Color.FromArgb(255, (byte)(underline.R / 2), (byte)(underline.G / 2), (byte)(underline.B / 2));
            var lineY = y + cellHeight - 2;
            switch (cell.UnderlineStyle)
            {
                case TerminalUnderlineStyle.Double:
                    drawing.DrawLine(x, lineY - 2, x + width, lineY - 2, underline);
                    goto default;
                case TerminalUnderlineStyle.Curly:
                    for (float i = 0; i < width; i += 1) drawing.DrawLine(x + i, lineY + MathF.Sin(i * MathF.PI / 2), x + MathF.Min(i + 1, width), lineY + MathF.Sin((i + 1) * MathF.PI / 2), underline);
                    break;
                case TerminalUnderlineStyle.Dotted:
                case TerminalUnderlineStyle.Dashed:
                    var length = cell.UnderlineStyle == TerminalUnderlineStyle.Dotted ? 1 : 3;
                    for (float i = 0; i < width; i += length * 2) drawing.DrawLine(x + i, lineY, x + MathF.Min(i + length, width), lineY, underline);
                    break;
                default: drawing.DrawLine(x, lineY, x + width, lineY, underline); break;
            }
        }
        if (cell.Overline) drawing.DrawLine(x, y + 1, x + width, y + 1, foreground);
        if (cell.Strikethrough) drawing.DrawLine(x, y + cellHeight / 2, x + width, y + cellHeight / 2, foreground);
    }

    private static void DrawCursor(CanvasDrawingSession drawing, TerminalFrame frame, float cellWidth,
        float cellHeight, GlyphLayouts layouts, bool focused, bool cursorPhase, bool hideCursor, float gridTop, bool textPhase)
    {
        var rows = TerminalGridGeometry.VisibleRows(frame);
        var device = drawing.Device;
        var cursor = frame.Cursor;
        if (hideCursor || !cursor.Visible || (cursor.Blink && focused && !cursorPhase) || cursor.Column < 0 || cursor.Column >= frame.Columns || cursor.Row < 0 || cursor.Row >= rows) return;
        var column = cursor.WideTail && cursor.Column > 0 ? cursor.Column - 1 : cursor.Column;
        var cell = frame.Cells[cursor.Row * frame.Columns + column];
        var cursorWidth = cellWidth * Math.Max(1, (int)cell.Width);
        var cx = column * cellWidth;
        var cy = gridTop + cursor.Row * cellHeight;
        var color = ToColor(cursor.Color);
        if (!focused || cursor.Shape == TerminalCursorShape.Hollow)
            drawing.DrawRectangle(cx + 0.5f, cy + 0.5f, cursorWidth - 1, cellHeight - 1, color);
        else if (cursor.Shape == TerminalCursorShape.Bar)
            drawing.FillRectangle(cx, cy, 2, cellHeight, color);
        else if (cursor.Shape == TerminalCursorShape.Underline)
            drawing.FillRectangle(cx, cy + cellHeight - 2, cursorWidth, 2, color);
        else
        {
            drawing.FillRectangle(cx, cy, cursorWidth, cellHeight, color);
            if (!cell.Hidden && cell.Width != 0 && (!cell.Blink || textPhase))
            {
                drawing.DrawTextLayout(layouts.Get(device, cell.Text, Math.Max(1, (int)cell.Width), cell.Bold, cell.Italic),
                    cx, cy, ToColor(frame.CursorText ?? cell.Background));
                DrawDecorations(drawing, cell, cx, cy, cursorWidth, cellHeight,
                    ToColor(frame.CursorText ?? cell.Background), cursorText: true);
            }
        }
    }

    private sealed class GlyphLayouts : IDisposable
    {
        private int _capacity = 1024;
        private int _cachedCells;
        private long _created;
        private readonly Dictionary<string, CanvasTextLayout?> _layouts = new();
        private readonly Dictionary<string, CanvasTextLayout?>.AlternateLookup<ReadOnlySpan<char>> _lookup;
        private readonly Queue<(string Key, int Cells)> _order = new();
        private readonly (FaceMetrics Face, float Baseline)?[] _references = new (FaceMetrics, float)?[4];
        private readonly GlyphRunFaces _runFaces = new();
        private char[] _key = new char[64];
        private int _keyLength;
        private (string Family, float Size, float CellWidth, float CellHeight) _metrics;

        internal GlyphRuns Runs { get; } = new();

        internal GlyphLayouts() => _lookup = _layouts.GetAlternateLookup<ReadOnlySpan<char>>();

        internal (int Count, int Cells, int Capacity, long Created) Usage
        {
            get
            {
                var runs = Runs.Usage;
                return (_layouts.Count + runs.Count, _cachedCells + runs.Cells, _capacity + runs.Capacity, _created + runs.Created);
            }
        }

        internal void Configure(string family, float size, float cellWidth, float cellHeight)
        {
            Runs.Configure(family, size, cellWidth, cellHeight);
            var metrics = (family, size, cellWidth, cellHeight);
            if (_metrics == metrics) return;
            Clear();
            _metrics = metrics;
        }

        internal void FitGrid(int cells)
        {
            _capacity = Math.Clamp(cells * 2, 1024, 65536);
            while (_cachedCells > _capacity) EvictOldest();
            Runs.FitGrid(cells);
        }

        internal void ResetKey() => _keyLength = 0;

        internal void AppendCell(string text)
        {
            if (_keyLength + text.Length + 2 > _key.Length) Array.Resize(ref _key, Math.Max(_key.Length * 2, _keyLength + text.Length + 2));
            text.CopyTo(_key.AsSpan(_keyLength));
            _keyLength += text.Length;
            _key[_keyLength++] = '\0';
        }

        internal CanvasTextLayout Get(CanvasDevice device, string text, int cells, bool bold, bool italic)
        {
            ResetKey();
            AppendCell(text);
            var key = FinishKey(bold, italic, cells);
            if (_lookup.TryGetValue(key, out var layout)) return layout!;
            using var format = CreateFormat(bold, italic, _metrics.Size, _metrics.Family);
            layout = new CanvasTextLayout(device, text, format, cells * _metrics.CellWidth, _metrics.CellHeight)
            {
                Options = CanvasDrawTextOptions.Clip
            };
            Add(key, cells, layout);
            return layout;
        }

        internal CanvasTextLayout? GetRun(CanvasDevice device, int cells, bool bold, bool italic)
        {
            var key = FinishKey(bold, italic, 0);
            if (_lookup.TryGetValue(key, out var layout)) return layout;
            layout = CreateRun(device, key[..^1], cells, bold, italic);
            Add(key, cells, layout);
            return layout;
        }

        private ReadOnlySpan<char> FinishKey(bool bold, bool italic, int singleCellWidth)
        {
            _key[_keyLength] = (char)('0' + Style(bold, italic) + 4 * singleCellWidth);
            return _key.AsSpan(0, _keyLength + 1);
        }

        private static int Style(bool bold, bool italic) => (bold ? 1 : 0) | (italic ? 2 : 0);

        private CanvasTextLayout? CreateRun(CanvasDevice device, ReadOnlySpan<char> cellTexts, int cells, bool bold, bool italic)
        {
            var starts = new int[cells + 1];
            var text = new char[cellTexts.Length - cells];
            int length = 0, cell = 0;
            foreach (var character in cellTexts)
            {
                if (character != '\0') text[length++] = character;
                else if (cell < cells) starts[++cell] = length;
                else return null;
            }
            if (cell != cells) return null;
            using var format = CreateFormat(bold, italic, _metrics.Size, _metrics.Family);
            if (_references[Style(bold, italic)] is not { } reference)
            {
                using var primary = new CanvasTextLayout(device, "M", format, _metrics.CellWidth, _metrics.CellHeight);
                if (_runFaces.Read(primary) is not { } face) return null;
                _references[Style(bold, italic)] = reference = (face, primary.LineMetrics[0].Baseline);
            }
            var layout = new CanvasTextLayout(device, new string(text, 0, length), format, cells * _metrics.CellWidth, _metrics.CellHeight)
            {
                Options = CanvasDrawTextOptions.Clip
            };
            if (_runFaces.Read(layout) == reference.Face && AnchorClusters(layout, starts, _metrics.CellWidth, reference.Baseline)) return layout;
            layout.Dispose();
            return null;
        }

        private static bool AnchorClusters(CanvasTextLayout layout, int[] cellStarts, float cellWidth, float baseline)
        {
            if (layout.LineMetrics is not [var line] || MathF.Abs(line.Baseline - baseline) > 0.001f) return false;
            var clusters = layout.ClusterMetrics;
            var targets = new float[clusters.Length];
            int offset = 0, cell = 0, cells = cellStarts.Length - 1;
            for (var index = 0; index < clusters.Length; index++)
            {
                var cluster = clusters[index];
                if ((cluster.Properties & CanvasClusterProperties.RightToLeft) != 0 || cell == cells || cellStarts[cell] != offset) return false;
                var end = offset + cluster.CharacterCount;
                var next = cell + 1;
                while (next < cells && cellStarts[next] < end) next++;
                if (cellStarts[next] != end) return false;
                targets[index] = (next - cell) * cellWidth;
                layout.SetCharacterSpacing(offset, cluster.CharacterCount, 0, targets[index] - cluster.Width, 0);
                (offset, cell) = (end, next);
            }
            if (cell != cells) return false;
            var anchored = layout.ClusterMetrics;
            if (anchored.Length != targets.Length) return false;
            for (var index = 0; index < targets.Length; index++)
                if (MathF.Abs(anchored[index].Width - targets[index]) > 0.001f) return false;
            return true;
        }

        private void Add(ReadOnlySpan<char> key, int cells, CanvasTextLayout? layout)
        {
            _created++;
            while (_order.Count != 0 && _cachedCells + cells > _capacity) EvictOldest();
            var stored = new string(key);
            _layouts.Add(stored, layout);
            _order.Enqueue((stored, cells));
            _cachedCells += cells;
        }

        private void EvictOldest()
        {
            var (oldest, cells) = _order.Dequeue();
            _layouts[oldest]?.Dispose();
            _layouts.Remove(oldest);
            _cachedCells -= cells;
        }

        internal void Clear()
        {
            foreach (var layout in _layouts.Values) layout?.Dispose();
            _layouts.Clear();
            _order.Clear();
            _cachedCells = 0;
            Array.Clear(_references);
            Runs.Clear();
        }

        public void Dispose() => Clear();
    }

    private readonly record struct FaceMetrics(float Ascent, float Descent, float LineGap);

    private sealed class GlyphRunFaces : ICanvasTextRenderer
    {
        private FaceMetrics? _face;
        private bool _uniform;

        internal FaceMetrics? Read(CanvasTextLayout layout)
        {
            (_face, _uniform) = (null, true);
            layout.DrawToTextRenderer(this, 0, 0);
            return _uniform ? _face : null;
        }

        public void DrawGlyphRun(Vector2 point, CanvasFontFace fontFace, float fontSize, CanvasGlyph[] glyphs, bool isSideways,
            uint bidiLevel, object brush, CanvasTextMeasuringMode measuringMode, string localeName, string textString,
            int[] clusterMapIndices, uint characterIndex, CanvasGlyphOrientation glyphOrientation)
        {
            var face = new FaceMetrics(fontFace.Ascent, fontFace.Descent, fontFace.LineGap);
            if (isSideways || bidiLevel % 2 != 0 || _face is { } previous && previous != face) _uniform = false;
            _face = face;
        }

        public void DrawStrikethrough(Vector2 point, float strikethroughWidth, float strikethroughThickness, float strikethroughOffset,
            CanvasTextDirection textDirection, object brush, CanvasTextMeasuringMode textMeasuringMode, string localeName,
            CanvasGlyphOrientation glyphOrientation) { }

        public void DrawUnderline(Vector2 point, float underlineWidth, float underlineThickness, float underlineOffset, float runHeight,
            CanvasTextDirection textDirection, object brush, CanvasTextMeasuringMode textMeasuringMode, string localeName,
            CanvasGlyphOrientation glyphOrientation) { }

        public void DrawInlineObject(Vector2 point, ICanvasTextInlineObject inlineObject, bool isSideways, bool isRightToLeft,
            object brush, CanvasGlyphOrientation glyphOrientation) { }

        public float Dpi => 96;
        public bool PixelSnappingDisabled => true;
        public Matrix3x2 Transform => Matrix3x2.Identity;
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

    internal static TerminalGridGeometry For(TerminalFrame frame, double surfaceHeight, float cellWidth, float cellHeight, bool bottomAligned, double scrollFraction = 0)
    {
        var remainder = bottomAligned ? surfaceHeight - frame.Rows * cellHeight : 0;
        return new(remainder + (frame.Viewport.TopInset - scrollFraction) * cellHeight, frame.Columns, VisibleRows(frame), cellWidth, cellHeight);
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
                var cell = frame.Cells[checked((int)row * frame.Columns + column)];
                if (!string.IsNullOrWhiteSpace(cell.Text) || cell.HasBackground || cell.Background != frame.Background || cell.Underline || cell.Strikethrough || cell.Overline)
                    return false;
            }
        return true;
    }

    internal Point Caret(TerminalCursor cursor) =>
        new(Math.Clamp(cursor.Column, 0, Math.Max(0, Columns - 1)) * CellWidth,
            RowTop(Math.Clamp(cursor.Row, 0, Math.Max(0, Rows - 1))));
}
