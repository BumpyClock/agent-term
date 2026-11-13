using System.Collections.Generic;
using AgentTerm.Terminal;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace AgentTerm_App;

/// <summary>
/// The operating system pointer over the terminal. This is the mouse pointer. It is separate from the text cursor,
/// which the surface draws on its own canvas.
/// </summary>
public sealed partial class TerminalSurface
{
    private static readonly Dictionary<TerminalPointerKind, InputSystemCursor> PointerCursors = [];
    private TerminalPointerKind? _pointerKind;

    /// <summary>Shows a pointer over the surface. Repeating the current kind does nothing.</summary>
    internal void SetPointerKind(TerminalPointerKind kind)
    {
        if (_pointerKind == kind) return;
        _pointerKind = kind;
        if (!PointerCursors.TryGetValue(kind, out var cursor))
            PointerCursors[kind] = cursor = InputSystemCursor.Create(ToSystemShape(kind));
        ProtectedCursor = cursor;
    }

    /// <summary>Returns the pointer to the system default for this element.</summary>
    internal void ResetPointerKind()
    {
        if (_pointerKind is null) return;
        _pointerKind = null;
        ProtectedCursor = null;
    }

    internal static InputSystemCursorShape ToSystemShape(TerminalPointerKind kind) => kind switch
    {
        TerminalPointerKind.IBeam => InputSystemCursorShape.IBeam,
        TerminalPointerKind.Hand => InputSystemCursorShape.Hand,
        TerminalPointerKind.Help => InputSystemCursorShape.Help,
        TerminalPointerKind.Wait => InputSystemCursorShape.Wait,
        TerminalPointerKind.AppStarting => InputSystemCursorShape.AppStarting,
        TerminalPointerKind.Cross => InputSystemCursorShape.Cross,
        TerminalPointerKind.SizeAll => InputSystemCursorShape.SizeAll,
        TerminalPointerKind.SizeWestEast => InputSystemCursorShape.SizeWestEast,
        TerminalPointerKind.SizeNorthSouth => InputSystemCursorShape.SizeNorthSouth,
        TerminalPointerKind.SizeNortheastSouthwest => InputSystemCursorShape.SizeNortheastSouthwest,
        TerminalPointerKind.SizeNorthwestSoutheast => InputSystemCursorShape.SizeNorthwestSoutheast,
        TerminalPointerKind.UniversalNo => InputSystemCursorShape.UniversalNo,
        _ => InputSystemCursorShape.Arrow
    };
}
