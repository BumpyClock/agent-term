using AgentTerm.Terminal;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using Windows.Foundation;

namespace AgentTerm_App;

public sealed partial class TerminalSurface
{
    internal Func<TerminalFrame, int, int, int, int, bool>? AutomationSelectRange { get; set; }
    internal Action? AutomationClearSelection { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new TerminalTextPeer(this);

    // Call after publishing a frame. Queries also refresh lazily, so the provider
    // reads an owned immutable frame even when no automation client is listening.
    internal void NotifyAutomationFrameChanged(bool textChanged, bool selectionChanged)
    {
        if (FrameworkElementAutomationPeer.FromElement(this) is not TerminalTextPeer peer) return;
        if (textChanged) peer.RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
        if (selectionChanged) peer.RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);
    }

    private sealed class TerminalTextPeer(TerminalSurface surface) : FrameworkElementAutomationPeer(surface), ITextProvider
    {
        private TextDocument? _document;
        internal TextDocument Document => _document is { } document && ReferenceEquals(document.Frame, surface._frame)
            ? document : _document = new TextDocument(surface._frame);
        internal IRawElementProviderSimple Provider => ProviderFromPeer(this);
        protected override string GetClassNameCore() => "TerminalSurface";
        protected override string GetNameCore() => "Terminal output";
        protected override string GetHelpTextCore() => "Visible terminal output. Ctrl+Shift+M starts keyboard selection. Enter copies. Escape cancels.";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.Text ? this : base.GetPatternCore(pattern);
        public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;
        public ITextRangeProvider DocumentRange => new TextRange(this, Document, 0, Document.Text.Length);
        public ITextRangeProvider[] GetVisibleRanges() => [DocumentRange];
        public ITextRangeProvider[] GetSelection()
        {
            var document = Document;
            return document.Selections.Select(span => (ITextRangeProvider)new TextRange(this, document, span.Start, span.End)).ToArray();
        }
        public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => throw new ArgumentException("Terminal text has no embedded children.", nameof(childElement));
        public ITextRangeProvider RangeFromPoint(Point screenLocation)
        {
            var bounds = GetBoundingRectangle();
            var cell = surface.Geometry.ClampedCellAt(new Point(screenLocation.X - bounds.X, screenLocation.Y - bounds.Y));
            var document = Document;
            int offset = document.CellOffset(cell.Column, cell.Row);
            return new TextRange(this, document, offset, offset);
        }
        internal void Select(TextDocument document, int start, int end)
        {
            if (!ReferenceEquals(document.Frame, surface._frame)) throw new InvalidOperationException("Terminal output changed. Request a new text range.");
            if (document.Frame is not { } frame) return;
            if (start == end) { surface.AutomationClearSelection?.Invoke(); return; }
            var first = document.CellAt(start);
            var last = document.CellAt(end - 1);
            if (surface.AutomationSelectRange?.Invoke(frame, first.Column, first.Row, last.Column, last.Row) != true)
                throw new InvalidOperationException("Terminal output changed. Request a new text range.");
        }
        internal double[] Bounds(TextDocument document, int start, int end)
        {
            if (!ReferenceEquals(document.Frame, surface._frame) || start == end) return [];
            var bounds = GetBoundingRectangle();
            var geometry = surface.Geometry;
            var rectangles = new List<double>();
            for (int row = 0; row < document.Rows; row++)
            {
                int first = Math.Max(start, document.LineStarts[row]);
                int last = Math.Min(end, document.LineEnds[row]);
                if (first >= last) continue;
                var a = document.CellAt(first);
                var b = document.CellAt(last - 1);
                double x = bounds.X + a.Column * geometry.CellWidth;
                double y = bounds.Y + geometry.RowTop(row);
                double right = Math.Min(bounds.Right, bounds.X + (b.Column + document.CellWidthAt(last - 1)) * geometry.CellWidth);
                double bottom = Math.Min(bounds.Bottom, y + geometry.CellHeight);
                y = Math.Max(bounds.Y, y);
                if (right > x && bottom > y) rectangles.AddRange([x, y, right - x, bottom - y]);
            }
            return rectangles.ToArray();
        }
        // UIA_IsReadOnlyAttributeId from UIAutomationClient.h.
        internal object Attribute(int id) => id == 40015 ? true : null!;
    }

    private sealed class TextRange(TerminalTextPeer peer, TextDocument document, int start, int end) : ITextRangeProvider
    {
        private int _start = start, _end = end;
        private TerminalTextPeer Peer => peer;
        private TextDocument Document => document;
        private TextRange Matching(ITextRangeProvider other) => other is TextRange range && range.Peer == peer && range.Document == document
            ? range : throw new ArgumentException("Text ranges must belong to the same terminal frame.", nameof(other));
        private int Endpoint(TextPatternRangeEndpoint endpoint) => endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        private void SetEndpoint(TextPatternRangeEndpoint endpoint, int value)
        {
            if (endpoint == TextPatternRangeEndpoint.Start) { _start = value; _end = Math.Max(_end, value); }
            else { _end = value; _start = Math.Min(_start, value); }
        }
        public ITextRangeProvider Clone() => new TextRange(peer, document, _start, _end);
        public bool Compare(ITextRangeProvider range) => range is TextRange other && other.Peer == peer && other.Document == document && other._start == _start && other._end == _end;
        public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint) => Endpoint(endpoint).CompareTo(Matching(targetRange).Endpoint(targetEndpoint));
        public void ExpandToEnclosingUnit(TextUnit unit)
        {
            var boundaries = document.Boundaries(unit);
            int index = Math.Max(0, Array.FindLastIndex(boundaries, value => value <= _start));
            if (index == boundaries.Length - 1 && index > 0) index--;
            _start = boundaries[index];
            _end = boundaries[Math.Min(index + 1, boundaries.Length - 1)];
        }
        public ITextRangeProvider FindAttribute(int attributeId, object value, bool backward) => attributeId == 40015 && Equals(value, true) ? Clone() : null!;
        public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase)
        {
            ArgumentException.ThrowIfNullOrEmpty(text);
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var span = document.Text.Substring(_start, _end - _start);
            int index = backward ? span.LastIndexOf(text, comparison) : span.IndexOf(text, comparison);
            return index < 0 ? null! : new TextRange(peer, document, _start + index, _start + index + text.Length);
        }
        public object GetAttributeValue(int attributeId) => peer.Attribute(attributeId);
        public void GetBoundingRectangles(out double[] returnValue) => returnValue = peer.Bounds(document, _start, _end);
        public IRawElementProviderSimple[] GetChildren() => [];
        public IRawElementProviderSimple GetEnclosingElement() => peer.Provider;
        public string GetText(int maxLength)
        {
            if (maxLength < -1) throw new ArgumentOutOfRangeException(nameof(maxLength));
            return document.Text.Substring(_start, maxLength < 0 ? _end - _start : Math.Min(maxLength, _end - _start));
        }
        public int Move(TextUnit unit, int count)
        {
            if (count == 0) return 0;
            bool degenerate = _start == _end;
            var boundaries = document.Boundaries(unit);
            int lastStart = Math.Max(0, boundaries.Length - (degenerate ? 1 : 2));
            int index = Math.Clamp(Array.FindLastIndex(boundaries, value => value <= _start), 0, lastStart);
            int target = (int)Math.Clamp((long)index + count, 0, lastStart);
            _start = boundaries[target];
            _end = degenerate ? _start : boundaries[Math.Min(target + 1, boundaries.Length - 1)];
            return target - index;
        }
        public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint) => SetEndpoint(endpoint, Matching(targetRange).Endpoint(targetEndpoint));
        public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
        {
            if (count == 0) return 0;
            var boundaries = document.Boundaries(unit);
            int position = Endpoint(endpoint);
            int index = count > 0 ? Array.FindLastIndex(boundaries, value => value <= position)
                : Array.FindIndex(boundaries, value => value >= position);
            index = Math.Max(0, index);
            int target = (int)Math.Clamp((long)index + count, 0, boundaries.Length - 1);
            SetEndpoint(endpoint, boundaries[target]);
            return target - index;
        }
        public void Select() => peer.Select(document, _start, _end);
        public void AddToSelection()
        {
            if (document.Selections.Any(span => span.Start < _start || span.End > _end))
                throw new InvalidOperationException("The terminal supports one selection.");
            Select();
        }
        public void RemoveFromSelection()
        {
            if (document.Selections.Any(span => span.Start < _start || span.End > _end))
                throw new InvalidOperationException("Remove the whole terminal selection.");
            if (document.Selections.Count > 0) peer.Select(document, _start, _start);
        }
        public void ScrollIntoView(bool alignToTop) { /* This provider's document is the visible viewport. */ }
    }
}
