using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace AgentTerm_App;

public sealed partial class TerminalSurface
{
    internal enum TextRendering { Layout, GlyphRunClipped }

    internal static TextRendering TextMode { get; set; } = TextRendering.GlyphRunClipped;

    internal record struct GlyphRunCounters(
        long StyleFaces, long CacheHits, long NativeShaped, long FallbackShaped, long ScriptSegments,
        long Rejected, long Submissions, long ClipLayers, long Brushes);

    private sealed class StyleFace(CanvasTextFormat format, CanvasFontFace face, nint native, FaceMetrics metrics, float baseline)
    {
        internal CanvasTextFormat Format { get; } = format;
        internal CanvasFontFace Face { get; } = face;
        internal nint Native { get; } = native;
        internal FaceMetrics Metrics { get; } = metrics;
        internal float Baseline { get; } = baseline;
    }

    private readonly record struct GlyphSegment(CanvasFontFace Face, float FontSize, float Offset, CanvasGlyph[] Glyphs)
    {
        internal int Count => Glyphs.Length;
    }

    private readonly record struct GlyphPart(CanvasFontFace Face, float FontSize, float Trailing, int Start, int Count);

    private sealed class ShapedRun(StyleFace style, GlyphSegment[] segments)
    {
        internal StyleFace Style { get; } = style;
        internal GlyphSegment[] Segments { get; } = segments;
    }

    private sealed class GlyphRuns
    {
        private readonly StyleFace?[] _styles = new StyleFace?[4];
        private readonly FirstGlyphRun _first = new();
        private DirectWriteShaper? _shaper;
        private readonly Dictionary<string, ShapedRun?> _shaped = new();
        private readonly Dictionary<string, ShapedRun?>.AlternateLookup<ReadOnlySpan<char>> _lookup;
        private readonly Queue<(string Key, int Cells)> _order = new();
        private readonly List<GlyphPart> _parts = [];
        private (string Family, float Size, float CellWidth, float CellHeight) _metrics;
        private int _capacity = 1024;
        private int _cachedCells;
        private char[] _text = new char[64];
        private char[] _key = new char[64];
        private int[] _starts = new int[65];
        private ushort[] _clusters = new ushort[64];
        private CanvasGlyph[] _glyphs = new CanvasGlyph[96];
        private int _glyphCount;
        private int _length;
        private int _keyLength;
        private int _cells;
        private int _cell;
        private CanvasSolidColorBrush? _brush;
        private CanvasDevice? _brushDevice;
        internal GlyphRunCounters Counters;

        internal GlyphRuns() => _lookup = _shaped.GetAlternateLookup<ReadOnlySpan<char>>();

        internal (int Count, int Cells, int Capacity, long Created) Usage =>
            (_shaped.Count, _cachedCells, _capacity, Counters.NativeShaped + Counters.FallbackShaped);

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
            while (_cachedCells > _capacity) EvictOldest();
        }

        internal void Begin() => _length = _keyLength = _cells = 0;

        internal void AppendCell(string text)
        {
            if (_length + text.Length + 1 > _text.Length) Array.Resize(ref _text, Math.Max(_text.Length * 2, _length + text.Length + 1));
            if (_keyLength + text.Length + 2 > _key.Length) Array.Resize(ref _key, Math.Max(_key.Length * 2, _keyLength + text.Length + 2));
            if (_cells + 2 > _starts.Length) Array.Resize(ref _starts, _starts.Length * 2);
            _starts[_cells++] = _length;
            text.CopyTo(_text.AsSpan(_length));
            text.CopyTo(_key.AsSpan(_keyLength));
            _length += text.Length;
            _keyLength += text.Length;
            _key[_keyLength++] = '\0';
            _starts[_cells] = _length;
        }

        internal ShapedRun? Prepare(CanvasDevice device, bool bold, bool italic)
        {
            if (Style(device, bold, italic) is not { } style) return null;
            _key[_keyLength] = (char)('0' + StyleIndex(bold, italic));
            var key = _key.AsSpan(0, _keyLength + 1);
            if (_lookup.TryGetValue(key, out var shaped)) Counters.CacheHits++;
            else
            {
                shaped = Shape(style);
                Add(key, _cells, shaped);
            }
            if (shaped is null) Counters.Rejected++;
            return shaped;
        }

        internal bool Draw(CanvasDrawingSession drawing, CanvasDevice device, float dpi, int column, float y, Color color, bool bold, bool italic)
        {
            if (Prepare(device, bold, italic) is not { } shaped) return false;
            var x = column * _metrics.CellWidth;
            var baseline = SnapBaseline(dpi, y + shaped.Style.Baseline);
            Counters.ClipLayers++;
            using var layer = drawing.CreateLayer(1, new Rect(x, y, _cells * _metrics.CellWidth, _metrics.CellHeight));
            foreach (var segment in shaped.Segments)
                Submit(drawing, device, segment.Face, segment.FontSize, new Vector2(x + segment.Offset, baseline), segment.Glyphs, color);
            return true;
        }

        private void Submit(CanvasDrawingSession drawing, CanvasDevice device, CanvasFontFace face, float size, Vector2 origin, CanvasGlyph[] glyphs, Color color)
        {
            if (_brush is null || !ReferenceEquals(_brushDevice, device))
            {
                _brush?.Dispose();
                _brush = new CanvasSolidColorBrush(device, color);
                _brushDevice = device;
                Counters.Brushes++;
            }
            else _brush.Color = color;
            Counters.Submissions++;
            drawing.DrawGlyphRun(origin, face, size, glyphs, false, 0, _brush);
        }

        internal static float SnapBaseline(float dpi, float baseline)
        {
            var scale = dpi / 96;
            return MathF.Floor(baseline * scale + 0.5f) * (1 / scale);
        }

        private StyleFace? Style(CanvasDevice device, bool bold, bool italic)
        {
            var index = StyleIndex(bold, italic);
            if (_styles[index] is { } existing) return existing;
            var format = CreateFormat(bold, italic, _metrics.Size, _metrics.Family);
            using var primary = new CanvasTextLayout(device, "M", format, _metrics.CellWidth, _metrics.CellHeight);
            if (_first.Read(primary) is not { } face)
            {
                format.Dispose();
                return null;
            }
            Counters.StyleFaces++;
            return _styles[index] = new StyleFace(format, face, DirectWriteShaper.CreateFontFace(face),
                new FaceMetrics(face.Ascent, face.Descent, face.LineGap), primary.LineMetrics[0].Baseline);
        }

        private ShapedRun? Shape(StyleFace style)
        {
            var text = _text.AsSpan(0, _length);
            (_glyphCount, _cell) = (0, 0);
            _parts.Clear();
            _shaper ??= new DirectWriteShaper();
            if (!_shaper.Covers(style.Native, text)) return ShapeWithFallback(style);
            Counters.NativeShaped++;
            if (!_shaper.Analyze(text)) return null;
            var offset = 0;
            foreach (var script in _shaper.Scripts)
            {
                if (script.Start != offset || script.Length == 0) return null;
                Counters.ScriptSegments++;
                var count = _shaper.Shape(style.Native, _metrics.Size, text.Slice(offset, script.Length), script);
                Reserve(count);
                for (var index = 0; index < count; index++)
                    _glyphs[_glyphCount + index] = new CanvasGlyph
                    {
                        Index = _shaper.Glyphs[index],
                        Advance = _shaper.Advances[index],
                        AdvanceOffset = _shaper.Offsets[index].AdvanceOffset,
                        AscenderOffset = _shaper.Offsets[index].AscenderOffset
                    };
                if (!Anchor(offset, _shaper.Clusters[..script.Length], count, style.Face, _metrics.Size)) return null;
                offset += script.Length;
            }
            return offset == _length && _cell == _cells ? Finish(style) : null;
        }

        private ShapedRun? ShapeWithFallback(StyleFace style)
        {
            Counters.FallbackShaped++;
            var analyzer = new CanvasTextAnalyzer(new string(_text, 0, _length), CanvasTextDirection.LeftToRightThenTopToBottom);
            foreach (var bidi in analyzer.GetBidi())
                if (bidi.Value.ResolvedLevel % 2 != 0) return null;
            var fonts = analyzer.GetFonts(style.Format);
            var scripts = analyzer.GetScript();
            int fontIndex = 0, scriptIndex = 0, offset = 0;
            while (offset < _length)
            {
                while (fontIndex < fonts.Count && End(fonts[fontIndex].Key) <= offset) fontIndex++;
                while (scriptIndex < scripts.Count && End(scripts[scriptIndex].Key) <= offset) scriptIndex++;
                if (scriptIndex == scripts.Count || scripts[scriptIndex].Key.CharacterIndex > offset) return null;
                var mapped = fontIndex < fonts.Count && fonts[fontIndex].Key.CharacterIndex <= offset;
                var face = mapped ? fonts[fontIndex].Value.FontFace : style.Face;
                if (new FaceMetrics(face.Ascent, face.Descent, face.LineGap) != style.Metrics) return null;
                var end = Math.Min(End(scripts[scriptIndex].Key), mapped ? End(fonts[fontIndex].Key)
                    : fontIndex < fonts.Count ? fonts[fontIndex].Key.CharacterIndex : _length);
                var size = _metrics.Size * (mapped ? fonts[fontIndex].Value.ScaleFactor : 1);
                Counters.ScriptSegments++;
                var shaped = analyzer.GetGlyphs(new CanvasCharacterRange { CharacterIndex = offset, CharacterCount = end - offset }, face, size,
                    false, false, scripts[scriptIndex].Value, "", null, null, out var clusters, out _, out _);
                if (clusters.Length != end - offset) return null;
                if (_clusters.Length < clusters.Length) Array.Resize(ref _clusters, Math.Max(_clusters.Length * 2, clusters.Length));
                for (var index = 0; index < clusters.Length; index++) _clusters[index] = (ushort)clusters[index];
                Reserve(shaped.Length);
                shaped.CopyTo(_glyphs, _glyphCount);
                if (!Anchor(offset, _clusters.AsSpan(0, clusters.Length), shaped.Length, face, size)) return null;
                offset = end;
            }
            return _cell == _cells ? Finish(style) : null;
        }

        private bool Anchor(int offset, ReadOnlySpan<ushort> clusters, int glyphs, CanvasFontFace face, float size)
        {
            var item = _parts.Count;
            for (var start = 0; start < clusters.Length;)
            {
                var stop = start + 1;
                while (stop < clusters.Length && clusters[stop] == clusters[start]) stop++;
                var glyphEnd = stop < clusters.Length ? clusters[stop] : glyphs;
                if (glyphEnd <= clusters[start] || _cell == _cells || _starts[_cell] != offset + start) return false;
                var next = _cell + 1;
                while (next < _cells && _starts[next] < offset + stop) next++;
                if (_starts[next] != offset + stop) return false;
                var first = _glyphCount + clusters[start];
                var last = _glyphCount + glyphEnd - 1;
                var natural = 0f;
                for (var index = first; index <= last; index++) natural += _glyphs[index].Advance;
                var trailing = (next - _cell) * _metrics.CellWidth - natural;
                _glyphs[last].Advance += trailing;
                AddPart(face, size, trailing, first, last - first + 1, _parts.Count > item);
                (start, _cell) = (stop, next);
            }
            _glyphCount += glyphs;
            return true;
        }

        private void AddPart(CanvasFontFace face, float size, float trailing, int start, int count, bool sameItem)
        {
            if (sameItem && _parts[^1] is var part && part.Trailing == trailing)
                _parts[^1] = part with { Count = part.Count + count };
            else _parts.Add(new GlyphPart(face, size, trailing, start, count));
        }

        private ShapedRun Finish(StyleFace style)
        {
            var segments = new GlyphSegment[_parts.Count];
            var pen = 0f;
            var next = 0;
            for (var index = 0; index < segments.Length; index++)
            {
                var part = _parts[index];
                for (; next < part.Start; next++) pen += _glyphs[next].Advance;
                segments[index] = new GlyphSegment(part.Face, part.FontSize, pen, _glyphs.AsSpan(part.Start, part.Count).ToArray());
            }
            return new ShapedRun(style, segments);
        }

        private void Reserve(int count)
        {
            if (_glyphCount + count > _glyphs.Length) Array.Resize(ref _glyphs, Math.Max(_glyphs.Length * 2, _glyphCount + count));
        }

        private static int End(CanvasCharacterRange range) => range.CharacterIndex + range.CharacterCount;

        private void Add(ReadOnlySpan<char> key, int cells, ShapedRun? shaped)
        {
            while (_order.Count != 0 && _cachedCells + cells > _capacity) EvictOldest();
            var stored = new string(key);
            _shaped.Add(stored, shaped);
            _order.Enqueue((stored, cells));
            _cachedCells += cells;
        }

        private void EvictOldest()
        {
            var (oldest, cells) = _order.Dequeue();
            _shaped.Remove(oldest);
            _cachedCells -= cells;
        }

        internal void Clear()
        {
            _shaped.Clear();
            _order.Clear();
            _cachedCells = 0;
            _shaper?.Dispose();
            _shaper = null;
            for (var index = 0; index < _styles.Length; index++)
            {
                if (_styles[index] is not { } style) continue;
                DirectWriteShaper.ReleaseFontFace(style.Native);
                style.Format.Dispose();
                _styles[index] = null;
            }
            _brush?.Dispose();
            _brush = null;
            _brushDevice = null;
        }
    }

    private static int StyleIndex(bool bold, bool italic) => (bold ? 1 : 0) | (italic ? 2 : 0);

    private sealed class FirstGlyphRun : ICanvasTextRenderer
    {
        private CanvasFontFace? _face;
        private bool _uniform;

        internal CanvasFontFace? Read(CanvasTextLayout layout)
        {
            (_face, _uniform) = (null, true);
            layout.DrawToTextRenderer(this, 0, 0);
            return _uniform ? _face : null;
        }

        public void DrawGlyphRun(Vector2 point, CanvasFontFace fontFace, float fontSize, CanvasGlyph[] glyphs, bool isSideways,
            uint bidiLevel, object brush, CanvasTextMeasuringMode measuringMode, string localeName, string textString,
            int[] clusterMapIndices, uint characterIndex, CanvasGlyphOrientation glyphOrientation)
        {
            if (isSideways || bidiLevel % 2 != 0 || _face is not null && !ReferenceEquals(_face, fontFace)) _uniform = false;
            _face ??= fontFace;
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
}
