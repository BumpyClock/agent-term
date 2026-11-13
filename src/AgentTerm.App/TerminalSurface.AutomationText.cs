using System.Globalization;
using System.Text;
using AgentTerm.Terminal;
using Microsoft.UI.Xaml.Automation.Text;

namespace AgentTerm_App;

public sealed partial class TerminalSurface
{
    // UTF-16 offsets belong to this immutable frame, never to native page memory.
    private sealed class TextDocument
    {
        internal readonly TerminalFrame? Frame;
        internal readonly string Text;
        internal readonly int Rows;
        internal readonly int[] LineStarts, LineEnds;
        internal readonly List<(int Offset, int Column, int Row, int Width)> Cells = [];
        internal readonly List<(int Start, int End)> Selections = [];
        internal TextDocument(TerminalFrame? frame)
        {
            Frame = frame;
            Rows = frame is null ? 0 : TerminalGridGeometry.VisibleRows(frame);
            LineStarts = new int[Rows];
            LineEnds = new int[Rows];
            var text = new StringBuilder();
            for (int row = 0; row < Rows; row++)
            {
                LineStarts[row] = text.Length;
                int selected = -1;
                for (int column = 0; column < frame!.Columns; column++)
                {
                    var cell = frame.Cells[row * frame.Columns + column];
                    if (cell.Width == 0) continue;
                    Cells.Add((text.Length, column, row, cell.Width));
                    if (cell.Selected && selected < 0) selected = text.Length;
                    if (!cell.Selected && selected >= 0) { Selections.Add((selected, text.Length)); selected = -1; }
                    text.Append(cell.Hidden || string.IsNullOrEmpty(cell.Text) ? " " : cell.Text);
                }
                LineEnds[row] = text.Length;
                if (selected >= 0) Selections.Add((selected, text.Length));
                if (row + 1 < Rows) text.Append('\n');
            }
            Text = text.ToString();
            for (int index = 1; index < Selections.Count;)
            {
                var previous = Selections[index - 1];
                var next = Selections[index];
                if (previous.End + 1 == next.Start && Text[previous.End] == '\n')
                {
                    Selections[index - 1] = (previous.Start, next.End);
                    Selections.RemoveAt(index);
                }
                else index++;
            }
        }
        internal int CellOffset(int column, int row) => Cells.LastOrDefault(cell => cell.Row == row && cell.Column <= column).Offset;
        internal (int Column, int Row) CellAt(int offset)
        {
            var cell = Cells.LastOrDefault(cell => cell.Offset <= offset);
            return (cell.Column, cell.Row);
        }
        internal int CellWidthAt(int offset) => Cells.LastOrDefault(cell => cell.Offset <= offset).Width;
        internal int[] Boundaries(TextUnit unit)
        {
            IEnumerable<int> starts = unit switch
            {
                TextUnit.Character => StringInfo.ParseCombiningCharacters(Text),
                TextUnit.Word => StringInfo.ParseCombiningCharacters(Text).Where(i => i == 0 || char.IsWhiteSpace(Text, i) != char.IsWhiteSpace(Text, i - 1)),
                TextUnit.Line or TextUnit.Paragraph => LineStarts,
                _ => [0]
            };
            return starts.Append(Text.Length).Distinct().Order().ToArray();
        }
    }

}
