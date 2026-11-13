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
    private CanvasControl? _canvas;
    private CanvasControl? _cursorCanvas;
    private readonly GlyphLayouts _glyphLayouts = new();
    private CanvasRenderTarget? _renderTarget;
    private TerminalFrame? _paintedFrame;
    private readonly List<(int FirstRow, int EndRow, int FirstColumn, int EndColumn)> _damageSpans = [];
    private readonly Dictionary<(int First, int End), int> _damageColumns = [];
    private (Rect Bounds, TerminalCursor Cursor, TerminalCell Cell, TerminalColor? CursorText, bool Focused,
        float CellWidth, float CellHeight, float FontSize, string FontFamily, double GridTop) _cursorContent;
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
        _canvas?.Invalidate();
        UpdateCursorOverlay();
    }

    internal TerminalGridGeometry Geometry => _frame is { } frame ? GeometryFor(frame) : new(0, 0, 0, CellWidth, CellHeight);
    internal TerminalGridGeometry GeometryFor(TerminalFrame frame) =>
        TerminalGridGeometry.For(frame, ActualHeight, CellWidth, CellHeight, _bottomAligned, _scrollFraction);

    private TerminalFrame? _frame;
    private bool _cursorPhase = true;
    private bool _paintedTextPhase = true;
    internal bool HasBlinkingText { get; private set; }
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
        root.Children.Add(_input);
        Content = root;
        PrefetchFonts();
        _input.GotFocus += (_, _) => UpdateCursorOverlay();
        _input.LostFocus += (_, _) => UpdateCursorOverlay();
        Loaded += (_, _) =>
        {
            if (!_suspended) AttachCanvas();
        };
        Unloaded += (_, _) => { if (!IsLoaded) ReleaseCanvas(); };
        ActualThemeChanged += (_, _) => _canvas?.Invalidate();
        SizeChanged += (_, _) => { PositionInput(); _canvas?.Invalidate(); UpdateCursorOverlay(); };
    }

    public void SetAcrylicBackground(bool enabled)
    {
        if (_acrylicBackground == enabled) return;
        _acrylicBackground = enabled;
        _canvas?.Invalidate();
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
        _glyphLayouts.Clear();
        ResetRaster();
        if (family is not null) _input.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(family);
        _input.FontSize = size;
        if (_resourcesReady) MeasureCells();
        _canvas?.Invalidate();
    }

    public void SetFrame(TerminalFrame frame)
    {
        if (ReferenceEquals(_frame, frame)) return;
        var previous = _frame;
        _frame = frame;
        if (previous is null || !ReferenceEquals(previous.Cells, frame.Cells))
            HasBlinkingText = frame.Cells.Any(cell => cell.Blink && !cell.Hidden);
        PositionInput();
        if (previous is null || !ReferenceEquals(previous.Cells, frame.Cells) || previous.Columns != frame.Columns ||
            previous.Rows != frame.Rows || previous.Viewport.TopInset != frame.Viewport.TopInset || previous.Background != frame.Background ||
            previous.SelectionBackground != frame.SelectionBackground || previous.SelectionForeground != frame.SelectionForeground ||
            !ReferenceEquals(previous.Images, frame.Images))
            _canvas?.Invalidate();
        else if (ReferenceEquals(_paintedFrame, previous)) _paintedFrame = frame;
        UpdateCursorOverlay();
    }

    public void SetCursorPhase(bool visible)
    {
        if (_cursorPhase == visible) return;
        _cursorPhase = visible;
        UpdateCursorOverlay();
        if (HasBlinkingText)
        {
            _canvas?.Invalidate();
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
        if (_canvas is not null) return;
        var canvas = new CanvasControl();
        _canvas = canvas;
        canvas.Draw += Draw;
        canvas.CreateResources += CreateResources;
        var cursor = new CanvasControl
        {
            IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Width = 0, Height = 0, Opacity = 0
        };
        _cursorCanvas = cursor;
        _cursorContent = default;
        cursor.Draw += DrawCursorOverlay;
        ((Grid)Content).Children.Insert(0, canvas);
        ((Grid)Content).Children.Insert(1, cursor);
        canvas.Invalidate();
    }

    private void ReleaseCanvas()
    {
        var canvas = _canvas;
        var cursor = _cursorCanvas;
        _canvas = null;
        _cursorCanvas = null;
        _cursorContent = default;
        _resourcesReady = false;
        _glyphLayouts.Clear();
        _imageBitmaps.Clear();
        ResetRaster();
        if (cursor is not null)
        {
            cursor.Draw -= DrawCursorOverlay;
            cursor.RemoveFromVisualTree();
            ((Grid)Content).Children.Remove(cursor);
        }
        if (canvas is null) return;
        canvas.Draw -= Draw;
        canvas.CreateResources -= CreateResources;
        canvas.RemoveFromVisualTree();
        ((Grid)Content).Children.Remove(canvas);
    }

    private void CreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        _glyphLayouts.Clear();
        _imageBitmaps.Clear();
        ResetRaster();
        _resourcesReady = true;
        MeasureCells();
    }

    private void MeasureCells()
    {
        using var format = CreateFormat(false, false, _terminalFontSize, TerminalFontFamily);
        using var layout = new CanvasTextLayout(_canvas!, "M", format, 100, 100);
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
        }
        else
        {
            var shade = ActualTheme == ElementTheme.Light ? (byte)250 : (byte)0;
            args.DrawingSession.Clear(_acrylicBackground ? Color.FromArgb(0, 0, 0, 0) : Color.FromArgb(255, shade, shade, shade));
        }
        measured?.Invoke(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        UpdateCursorOverlay();
    }

    private void UpdateCursorOverlay()
    {
        if (_cursorCanvas is not { } overlay) return;
        var frame = _frame;
        var focused = _input.FocusState != FocusState.Unfocused;
        if (_suspended || !_resourcesReady || _compositionVisible || frame is null || !frame.Cursor.Visible ||
            frame.Cursor.Column < 0 || frame.Cursor.Column >= frame.Columns ||
            frame.Cursor.Row < 0 || frame.Cursor.Row >= TerminalGridGeometry.VisibleRows(frame))
        {
            overlay.Opacity = 0;
            return;
        }
        var cursor = frame.Cursor;
        var column = cursor.WideTail && cursor.Column > 0 ? cursor.Column - 1 : cursor.Column;
        var cell = frame.Cells[cursor.Row * frame.Columns + column];
        var (bounds, gridTop) = CursorOverlayGeometry(Geometry, column, cursor.Row, Math.Max(1, (int)cell.Width));
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
        overlay.Opacity = cursor.Blink && focused && !_cursorPhase ? 0 : 1;
    }

    // Bounds use coarse buckets to avoid resizing the overlay on every animation tick. The exact origin
    // remains part of its content key so movement inside a bucket still redraws the cursor.
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
        if (_suspended || _frame is not { } frame) return;
        var content = _cursorContent;
        _glyphLayouts.Configure(TerminalFontFamily, _terminalFontSize, CellWidth, CellHeight);
        drawing.Transform = Matrix3x2.CreateTranslation(-(float)content.Bounds.X, -(float)content.Bounds.Y);
        DrawCursor(drawing, frame, CellWidth, CellHeight, _glyphLayouts, content.Focused, true, false, (float)content.GridTop, _cursorPhase);
    }

    private void UpdateRaster(CanvasDevice device, TerminalFrame frame, float top)
    {
        var metrics = (ActualWidth, ActualHeight, _canvas!.Dpi, CellWidth, CellHeight,
            _terminalFontSize, TerminalFontFamily, _acrylicBackground, top);
        var originChanged = _rasterMetrics.GridTop != top;
        _rasterMetrics.GridTop = top;
        if (_renderTarget is null || _rasterMetrics != metrics)
        {
            ResetRaster();
            _renderTarget = new CanvasRenderTarget(device, (float)ActualWidth, (float)ActualHeight, _canvas.Dpi);
            _rasterMetrics = metrics;
        }
        var previous = _paintedFrame;
        var full = originChanged || frame.OverscanAbove.Length != 0 || frame.OverscanBelow.Length != 0 || previous is null || previous.Columns != frame.Columns || previous.Rows != frame.Rows ||
            previous.Viewport.TopInset != frame.Viewport.TopInset || previous.Background != frame.Background ||
            previous.SelectionBackground != frame.SelectionBackground || previous.SelectionForeground != frame.SelectionForeground ||
            !ReferenceEquals(previous.Images, frame.Images);
        _glyphLayouts.Configure(TerminalFontFamily, _terminalFontSize, CellWidth, CellHeight);
        _glyphLayouts.FitGrid(frame.Cells.Length);
        if (full)
        {
            using (var drawing = _renderTarget.CreateDrawingSession())
                DrawFrameCore(drawing, frame, CellWidth, CellHeight, _glyphLayouts, false, false,
                    _terminalFontSize, _acrylicBackground, TerminalFontFamily, true, top, _cursorPhase, _imageBitmaps, (float)ActualHeight);
            _imageBitmaps.Trim(frame.Images);
        }
        else if (!ReferenceEquals(previous!.Cells, frame.Cells) || _paintedTextPhase != _cursorPhase && HasBlinkingText)
        {
            using var drawing = _renderTarget.CreateDrawingSession();
            var scale = _canvas.Dpi / 96;
            var rows = TerminalGridGeometry.VisibleRows(frame);
            var spans = _damageSpans;
            var latest = _damageColumns;
            spans.Clear();
            latest.Clear();
            for (var row = 0; row < rows; row++)
            for (var col = 0; col < frame.Columns;)
            {
                if (!CellNeedsRepaint(previous.Cells[row * frame.Columns + col], frame.Cells[row * frame.Columns + col], _paintedTextPhase != _cursorPhase)) { col++; continue; }
                var first = Math.Max(0, col - 2);
                var lastChanged = col++;
                while (col < frame.Columns && col <= lastChanged + 4)
                {
                    if (CellNeedsRepaint(previous.Cells[row * frame.Columns + col], frame.Cells[row * frame.Columns + col], _paintedTextPhase != _cursorPhase)) lastChanged = col;
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
                DrawCells(drawing, frame, CellWidth, CellHeight, _glyphLayouts, top,
                    Math.Max(0, (int)MathF.Floor((upper - top) / CellHeight)),
                    Math.Min(rows, (int)MathF.Ceiling((lower - top) / CellHeight)),
                    Math.Max(0, (int)MathF.Floor(left / CellWidth) - 1),
                    Math.Min(frame.Columns, (int)MathF.Ceiling(right / CellWidth) + 1), _cursorPhase, _imageBitmaps);
            }
        }
        _paintedFrame = frame;
        _paintedTextPhase = _cursorPhase;
    }

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
        drawing.Flush(); // Submit draws before the temporary bitmap owner is disposed.
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
        int firstRow, int endRow, int firstColumn, int endColumn, bool textPhase, ImageBitmaps? images = null, Rect? viewportClip = null)
    {
        TerminalCell CellAt(int row, int col) => row < 0 ? frame.OverscanAbove[0].Cells[col]
            : row >= frame.Rows ? frame.OverscanBelow[0].Cells[col] : frame.Cells[row * frame.Columns + col];
        var device = drawing.Device;
        DrawImages(drawing, frame, TerminalImageLayer.BelowBackground, cellWidth, cellHeight, gridTop, images, viewportClip);
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
                    fill = cell.Selected || cell.Background != frame.Background;
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
        DrawImages(drawing, frame, TerminalImageLayer.BelowText, cellWidth, cellHeight, gridTop, images, viewportClip);
        for (var row = firstRow; row < endRow; row++)
        for (var col = firstColumn; col < endColumn; col++)
        {
            var cell = CellAt(row, col);
            if (cell.Width == 0 || cell.Hidden || (cell.Blink && !textPhase)) continue;
            var foreground = cell.Selected ? ToColor(frame.SelectionForeground) : ToColor(cell.Foreground);
            if (cell.Dim && !cell.Selected) foreground = Color.FromArgb(255, (byte)(foreground.R / 2), (byte)(foreground.G / 2), (byte)(foreground.B / 2));
            var x = col * cellWidth;
            var y = gridTop + row * cellHeight;
            var width = cellWidth * cell.Width;
            if (!string.IsNullOrWhiteSpace(cell.Text))
            {
                drawing.DrawTextLayout(layouts.Get(device, cell.Text, width, cell.Bold, cell.Italic), x, y, foreground);
            }
            DrawDecorations(drawing, cell, x, y, width, cellHeight, foreground);
        }
        DrawImages(drawing, frame, TerminalImageLayer.AboveText, cellWidth, cellHeight, gridTop, images, viewportClip);
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
                drawing.DrawTextLayout(layouts.Get(device, cell.Text, cursorWidth, cell.Bold, cell.Italic),
                    cx, cy, ToColor(frame.CursorText ?? cell.Background));
                DrawDecorations(drawing, cell, cx, cy, cursorWidth, cellHeight,
                    ToColor(frame.CursorText ?? cell.Background), cursorText: true);
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
                var cell = frame.Cells[row * frame.Columns + column];
                if (!string.IsNullOrWhiteSpace(cell.Text) || cell.Background != frame.Background || cell.Underline || cell.Strikethrough || cell.Overline)
                    return false;
            }
        return true;
    }

    internal Point Caret(TerminalCursor cursor) =>
        new(Math.Clamp(cursor.Column, 0, Math.Max(0, Columns - 1)) * CellWidth,
            RowTop(Math.Clamp(cursor.Row, 0, Math.Max(0, Rows - 1))));
}
