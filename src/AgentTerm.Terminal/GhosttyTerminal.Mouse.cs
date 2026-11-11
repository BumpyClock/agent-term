using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentTerm.Terminal;

public enum TerminalMouseAction { Press, Release, Motion }

public enum TerminalMouseButton
{
    Left = 1, Right = 2, Middle = 3,
    WheelUp = 4, WheelDown = 5, WheelLeft = 6, WheelRight = 7,
    Back = 8, Forward = 9
}

/// <summary>A pointer event in surface pixels. A motion without a held button has a null Button.</summary>
public readonly record struct TerminalMouseEvent(TerminalMouseAction Action, TerminalMouseButton? Button,
    TerminalModifiers Modifiers, float X, float Y);

/// <summary>Surface and cell dimensions, in the same pixel units as pointer coordinates.</summary>
public readonly record struct TerminalMouseGeometry(uint ScreenWidth, uint ScreenHeight, uint CellWidth, uint CellHeight,
    float PixelScale = 1);

public sealed partial class GhosttyTerminal
{
    private MouseEncoderHandle? _mouseEncoder;
    private MouseEventHandle? _mouseEvent;

    public bool IsMouseTrackingEnabled
    {
        get
        {
            ThrowIfDisposed();
            ThrowIfFailed(MouseNative.ghostty_terminal_get(_handle, 11, out var enabled),
                "Could not read libghostty-vt mouse tracking mode.");
            return enabled != 0;
        }
    }

    /// <summary>Encodes a pointer event using the application's current tracking mode and format.</summary>
    public unsafe byte[] EncodeMouse(TerminalMouseEvent mouse, TerminalMouseGeometry geometry)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(mouse.Action)) throw new ArgumentOutOfRangeException(nameof(mouse));
        if (mouse.Button is { } button && !Enum.IsDefined(button)) throw new ArgumentOutOfRangeException(nameof(mouse));
        if (!float.IsFinite(mouse.X) || !float.IsFinite(mouse.Y)) throw new ArgumentOutOfRangeException(nameof(mouse));
        if (geometry.CellWidth == 0 || geometry.CellHeight == 0 ||
            geometry.ScreenWidth < geometry.CellWidth || geometry.ScreenHeight < geometry.CellHeight ||
            !float.IsFinite(geometry.PixelScale) || geometry.PixelScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(geometry));

        // Keep cell reporting in layout units to preserve exact cell boundaries at fractional DPI.
        // SGR pixel reports use physical pixels instead.
        float scale = IsInputModeEnabled((InputNative.DecMode)1016) ? geometry.PixelScale : 1;

        if (_mouseEncoder is null)
        {
            ThrowIfFailed(MouseNative.ghostty_mouse_encoder_new(IntPtr.Zero, out var encoder),
                "Could not create a libghostty-vt mouse encoder.");
            _mouseEncoder = new MouseEncoderHandle(encoder);
        }
        if (_mouseEvent is null)
        {
            ThrowIfFailed(MouseNative.ghostty_mouse_event_new(IntPtr.Zero, out var mouseEvent),
                "Could not create a libghostty-vt mouse event.");
            _mouseEvent = new MouseEventHandle(mouseEvent);
        }

        ThrowIfFailed(MouseNative.ghostty_mouse_encoder_set_from_terminal(_mouseEncoder, _handle),
            "Could not synchronize libghostty-vt mouse modes.");
        var size = new MouseNative.EncoderSize
        {
            Size = (nuint)sizeof(MouseNative.EncoderSize),
            ScreenWidth = checked((uint)Math.Max(1, Math.Round(geometry.ScreenWidth * (double)scale))),
            ScreenHeight = checked((uint)Math.Max(1, Math.Round(geometry.ScreenHeight * (double)scale))),
            CellWidth = checked((uint)Math.Max(1, Math.Round(geometry.CellWidth * (double)scale))),
            CellHeight = checked((uint)Math.Max(1, Math.Round(geometry.CellHeight * (double)scale)))
        };
        const string optionError = "Could not set libghostty-vt mouse geometry.";
        ThrowIfFailed(MouseNative.ghostty_mouse_encoder_set(_mouseEncoder, 3, &size), optionError);
        byte held = (mouse.Button is TerminalMouseButton.Left or TerminalMouseButton.Right or TerminalMouseButton.Middle
            or TerminalMouseButton.Back or TerminalMouseButton.Forward) && mouse.Action != TerminalMouseAction.Release
            ? (byte)1 : (byte)0;
        ThrowIfFailed(MouseNative.ghostty_mouse_encoder_set(_mouseEncoder, 4, &held), optionError);

        const string eventError = "Could not set a libghostty-vt mouse event field.";
        ThrowIfFailed(MouseNative.ghostty_mouse_event_set_action(_mouseEvent, mouse.Action), eventError);
        ThrowIfFailed(mouse.Button is { } value
            ? MouseNative.ghostty_mouse_event_set_button(_mouseEvent, value)
            : MouseNative.ghostty_mouse_event_clear_button(_mouseEvent), eventError);
        ThrowIfFailed(MouseNative.ghostty_mouse_event_set_mods(_mouseEvent, (ushort)mouse.Modifiers), eventError);
        ThrowIfFailed(MouseNative.ghostty_mouse_event_set_position(_mouseEvent, new(mouse.X * scale, mouse.Y * scale)), eventError);

        const string encodeError = "Could not encode a libghostty-vt mouse event.";
        Span<byte> buffer = stackalloc byte[128];
        int result;
        nuint written;
        fixed (byte* pointer = buffer)
            result = MouseNative.ghostty_mouse_encoder_encode(_mouseEncoder, _mouseEvent, pointer,
                (nuint)buffer.Length, out written);
        if (result != InputNative.OutOfSpace)
        {
            ThrowIfFailed(result, encodeError);
            return buffer[..checked((int)written)].ToArray();
        }
        var output = new byte[checked((int)written)];
        fixed (byte* pointer = output)
            ThrowIfFailed(MouseNative.ghostty_mouse_encoder_encode(_mouseEncoder, _mouseEvent, pointer,
                (nuint)output.Length, out _), encodeError);
        return output;
    }

    private sealed class MouseEncoderHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public MouseEncoderHandle(IntPtr value) : base(ownsHandle: true) => SetHandle(value);
        protected override bool ReleaseHandle() { MouseNative.ghostty_mouse_encoder_free(handle); return true; }
    }

    private sealed class MouseEventHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public MouseEventHandle(IntPtr value) : base(ownsHandle: true) => SetHandle(value);
        protected override bool ReleaseHandle() { MouseNative.ghostty_mouse_event_free(handle); return true; }
    }

    private static class MouseNative
    {
        private const string Library = "ghostty-vt";

        [StructLayout(LayoutKind.Sequential)]
        internal readonly record struct Position(float X, float Y);

        [StructLayout(LayoutKind.Sequential)]
        internal struct EncoderSize
        {
            internal nuint Size;
            internal uint ScreenWidth, ScreenHeight, CellWidth, CellHeight;
            internal uint PaddingTop, PaddingBottom, PaddingRight, PaddingLeft;
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_terminal_get(TerminalHandle terminal, int data, out byte enabled);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_mouse_encoder_new(IntPtr allocator, out IntPtr encoder);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void ghostty_mouse_encoder_free(IntPtr encoder);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_mouse_encoder_set_from_terminal(MouseEncoderHandle encoder, TerminalHandle terminal);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern unsafe int ghostty_mouse_encoder_set(MouseEncoderHandle encoder, int option, void* value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_mouse_event_new(IntPtr allocator, out IntPtr mouseEvent);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void ghostty_mouse_event_free(IntPtr mouseEvent);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_mouse_event_set_action(MouseEventHandle mouseEvent, TerminalMouseAction action);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_mouse_event_set_button(MouseEventHandle mouseEvent, TerminalMouseButton button);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_mouse_event_clear_button(MouseEventHandle mouseEvent);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_mouse_event_set_mods(MouseEventHandle mouseEvent, ushort mods);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int ghostty_mouse_event_set_position(MouseEventHandle mouseEvent, Position position);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern unsafe int ghostty_mouse_encoder_encode(MouseEncoderHandle encoder, MouseEventHandle mouseEvent,
            byte* buffer, nuint length, out nuint written);
    }
}
