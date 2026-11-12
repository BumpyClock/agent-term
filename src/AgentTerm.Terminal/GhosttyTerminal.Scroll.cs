using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    private GestureHandle? _gesture;
    private TrackedHandle? _anchor;
    private TrackedHandle? _viewportAnchor;
    private int _viewportAnchorScreen;
    private int? _selectionScreen;
    private bool _selectionRectangle;
    private bool _selecting;
    private int _selectionClickCount;
    private long _viewRevision;
    private long _capturedViewRevision;

    private unsafe void ConfigureScrollback()
    {
        nuint bytes = 10 * 1024 * 1024;
        nuint lines = 10_000;
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 28, (IntPtr)(&bytes)), "Could not configure scrollback memory.");
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 29, (IntPtr)(&lines)), "Could not configure scrollback lines.");
    }

    public void ScrollLines(int delta) => ScrollTo(ReadFrame().Viewport.Offset + delta);
    public void ScrollTo(long offset)
    {
        ThrowIfDisposed();
        EnsurePresentation();
        var viewport = ReadViewport();
        var target = Math.Clamp(offset, 0, viewport.MaximumOffset);
        NativeScroll(target);
        _following = target == viewport.MaximumOffset;
        _viewRevision++;
        CaptureSelectedViewport();
    }
    public void ScrollToBottom()
    {
        _following = true;
        ScrollTo(ReadFrame().Viewport.MaximumOffset);
    }

    public unsafe void BeginSelection(int column, int row, int clickCount = 1, bool rectangular = false)
    {
        ThrowIfDisposed();
        if (clickCount < 1) throw new ArgumentOutOfRangeException(nameof(clickCount));
        var point = ViewportPoint(column, row);
        ClearSelection();
        if (_gesture is null)
        {
            ThrowIfFailed(ScrollNative.ghostty_selection_gesture_new(IntPtr.Zero, out var gesture), "Could not create selection gesture.");
            _gesture = new GestureHandle(gesture);
        }
        ScrollNative.ghostty_selection_gesture_reset(_gesture, _handle);
        _selectionScreen = ActiveScreen();
        _selectionRectangle = rectangular;
        _selectionClickCount = clickCount;
        _selecting = true;
        _anchor?.Dispose();
        ThrowIfFailed(ScrollNative.ghostty_terminal_grid_ref_track(_handle, point, out var tracked), "Could not track selection anchor.");
        _anchor = new TrackedHandle(tracked);
        ThrowIfFailed(Native.ghostty_terminal_grid_ref(_handle, point, out var gridRef), "Could not find selection cell.");
        using var ev = NewGestureEvent(0);
        SetEvent(ev, 1, &gridRef);
        var behavior = new GestureBehaviors { Single = clickCount >= 3 ? 2 : clickCount == 2 ? 1 : 0, Double = 1, Triple = 2 };
        SetEvent(ev, 7, &behavior);
        ApplyGesture(ev);
    }

    public unsafe void ExtendSelection(int column, int row)
    {
        ThrowIfDisposed();
        CheckSelectionScreen();
        var point = ViewportPoint(column, row);
        if (_selecting && _selectionClickCount > 1 && !_selectionRectangle)
        {
            ThrowIfFailed(Native.ghostty_terminal_grid_ref(_handle, point, out var dragRef), "Could not find selection cell.");
            using var drag = NewGestureEvent(2);
            SetEvent(drag, 1, &dragRef);
            var geometry = new GestureGeometry { Columns = _columns, CellWidth = 100, Height = _rows };
            SetEvent(drag, 9, &geometry);
            var position = new SurfacePosition { X = column * 100 + 50, Y = row + 0.5 };
            SetEvent(drag, 2, &position);
            ApplyGesture(drag);
            return;
        }
        Native.GridRef anchor;
        if (_selecting && _anchor is not null)
        {
            var anchorResult = ScrollNative.ghostty_tracked_grid_ref_snapshot(_anchor, out anchor);
            if (anchorResult == -4) { _selecting = false; return; }
            ThrowIfFailed(anchorResult, "Could not read selection anchor.");
        }
        else
        {
            Native.Selection existing = default;
            var result = RenderNative.ghostty_terminal_get(_handle, 31, &existing);
            if (result == -4) return;
            ThrowIfFailed(result, "Could not read selection anchor.");
            anchor = existing.Start;
        }
        ThrowIfFailed(Native.ghostty_terminal_grid_ref(_handle, point, out var end), "Could not find selection cell.");
        var selection = new Native.Selection { Start = anchor, End = end, Rectangle = _selectionRectangle ? (byte)1 : (byte)0 };
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 22, (IntPtr)(&selection)), "Could not set selection.");
        _viewRevision++;
        CaptureSelectedViewport();
    }
    public void EndSelection()
    {
        ThrowIfDisposed();
        CheckSelectionScreen();
        if (_selectionScreen is null || _gesture is null) return;
        using var ev = NewGestureEvent(1);
        ApplyGesture(ev);
        _selecting = false;
    }

    public void ClearSelection()
    {
        ThrowIfDisposed();
        if (_gesture is not null) ScrollNative.ghostty_selection_gesture_reset(_gesture, _handle);
        _selectionScreen = null;
        _selecting = false;
        _anchor?.Dispose(); _anchor = null;
        _viewportAnchor?.Dispose(); _viewportAnchor = null;
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 22, IntPtr.Zero), "Could not clear selection.");
        _viewRevision++;
    }

    public unsafe string? ReadSelectionText()
    {
        ThrowIfDisposed();
        Native.Selection selection = default;
        var result = RenderNative.ghostty_terminal_get(_handle, 31, &selection);
        if (result == -4) return null;
        ThrowIfFailed(result, "Could not read selection.");
        ThrowIfFailed(Native.ghostty_formatter_terminal_new(IntPtr.Zero, out var formatter, _handle), "Could not create selection formatter.");
        using var handle = new FormatterHandle(formatter);
        ThrowIfFailed(Native.ghostty_formatter_set(handle, Native.FormatterOption.Selection, in selection), "Could not format selection.");
        byte unwrap = selection.Rectangle == 0 ? (byte)1 : (byte)0;
        ThrowIfFailed(ScrollNative.ghostty_formatter_set(handle, 2, &unwrap), "Could not configure selection formatting.");
        ThrowIfFailed(Native.ghostty_formatter_format_alloc(handle, IntPtr.Zero, out var output, out var length), "Could not format selection.");
        try { return length == 0 ? string.Empty : Marshal.PtrToStringUTF8(output, checked((int)length))!; }
        finally { Native.ghostty_free(IntPtr.Zero, output, length); }
    }

    private Native.Point ViewportPoint(int column, int row)
    {
        if ((uint)column >= _columns) throw new ArgumentOutOfRangeException(nameof(column));
        if ((uint)row >= _rows) throw new ArgumentOutOfRangeException(nameof(row));
        return new Native.Point { Tag = 1, Value = new Native.PointValue { X = (ushort)column, Y = (uint)row } };
    }
    private unsafe int ActiveScreen()
    {
        int screen = 0;
        ThrowIfFailed(RenderNative.ghostty_terminal_get(_handle, 6, &screen), "Could not read active screen.");
        return screen;
    }
    private void CheckSelectionScreen()
    {
        if (_selectionScreen is not null && ActiveScreen() != _selectionScreen)
        {
            if (_gesture is not null) ScrollNative.ghostty_selection_gesture_reset(_gesture, _handle);
            _selectionScreen = null;
            _selecting = false;
            _anchor?.Dispose(); _anchor = null;
        }
    }
    private TerminalViewport ReadViewport()
    {
        var native = ReadNativeViewport();
        return !_preparingPresentation && _presentedTotal is { } total && !native.IsAlternateScreen
            ? native with { TotalRows = total } : native;
    }
    private unsafe TerminalViewport ReadNativeViewport()
    {
        Scrollbar bar = default;
        ThrowIfFailed(RenderNative.ghostty_terminal_get(_handle, 9, &bar), "Could not read terminal scrollbar.");
        return new TerminalViewport(checked((long)bar.Total), checked((long)bar.Offset), checked((int)bar.Length), ActiveScreen() != 0);
    }
    private void CaptureSelectedViewport()
    {
        _viewportAnchor?.Dispose();
        _viewportAnchor = null;
        if (!HasSelection()) return;
        ThrowIfFailed(ScrollNative.ghostty_terminal_grid_ref_track(_handle, ViewportPoint(0, 0), out var tracked), "Could not track selection viewport.");
        _viewportAnchor = new TrackedHandle(tracked);
        _viewportAnchorScreen = ActiveScreen();
    }
    private void RestoreSelectedViewport()
    {
        if (_viewportAnchor is null) return;
        if (ActiveScreen() != _viewportAnchorScreen || !HasSelection())
        {
            _viewportAnchor.Dispose(); _viewportAnchor = null;
            return;
        }
        var result = ScrollNative.ghostty_tracked_grid_ref_point(_viewportAnchor, 2, out var point);
        if (result == -4) { _viewportAnchor.Dispose(); _viewportAnchor = null; return; }
        ThrowIfFailed(result, "Could not restore selection viewport.");
        if (ReadViewport().Offset != point.Y)
        {
            ThrowIfFailed(ScrollNative.ghostty_terminal_scroll_viewport(_handle, new ScrollBehavior { Tag = 3, Value = (nint)point.Y }), "Could not restore selection viewport.");
            _viewRevision++;
        }
    }
    private unsafe bool HasSelection()
    {
        Native.Selection selection = default;
        var result = RenderNative.ghostty_terminal_get(_handle, 31, &selection);
        if (result == -4) return false;
        ThrowIfFailed(result, "Could not read selection.");
        return true;
    }
    private static GestureEventHandle NewGestureEvent(int type)
    {
        ThrowIfFailed(ScrollNative.ghostty_selection_gesture_event_new(IntPtr.Zero, out var ev, type), "Could not create selection event.");
        return new GestureEventHandle(ev);
    }
    private static unsafe void SetEvent(GestureEventHandle ev, int option, void* value) =>
        ThrowIfFailed(ScrollNative.ghostty_selection_gesture_event_set(ev, option, value), "Could not configure selection event.");
    private unsafe void ApplyGesture(GestureEventHandle ev)
    {
        Native.Selection selection = default;
        var result = ScrollNative.ghostty_selection_gesture_event(_gesture!, _handle, ev, &selection);
        if (result == -4) return;
        ThrowIfFailed(result, "Could not update selection gesture.");
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 22, (IntPtr)(&selection)), "Could not set selection.");
        _viewRevision++;
        CaptureSelectedViewport();
    }
    private void DisposeSelection() { _viewportAnchor?.Dispose(); _anchor?.Dispose(); if (_gesture is not null) { ScrollNative.ghostty_selection_gesture_reset(_gesture, _handle); _gesture.Dispose(); } }

    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct ScrollBehavior { [FieldOffset(0)] public int Tag; [FieldOffset(8)] public nint Value; }
    [StructLayout(LayoutKind.Sequential)] private struct Scrollbar { public ulong Total, Offset, Length; }
    [StructLayout(LayoutKind.Sequential)] private struct GestureBehaviors { public int Single, Double, Triple; }
    [StructLayout(LayoutKind.Sequential)] private struct GestureGeometry { public uint Columns, CellWidth, PaddingLeft, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct SurfacePosition { public double X, Y; }
    internal sealed class TrackedHandle : SafeHandleZeroOrMinusOneIsInvalid { public TrackedHandle(IntPtr p) : base(true) => SetHandle(p); protected override bool ReleaseHandle() { ScrollNative.ghostty_tracked_grid_ref_free(handle); return true; } }
    private sealed class GestureHandle : SafeHandleZeroOrMinusOneIsInvalid { public GestureHandle(IntPtr p) : base(true) => SetHandle(p); protected override bool ReleaseHandle() { ScrollNative.ghostty_selection_gesture_free(handle, IntPtr.Zero); return true; } }
    private sealed class GestureEventHandle : SafeHandleZeroOrMinusOneIsInvalid { public GestureEventHandle(IntPtr p) : base(true) => SetHandle(p); protected override bool ReleaseHandle() { ScrollNative.ghostty_selection_gesture_event_free(handle); return true; } }
    private static unsafe class ScrollNative
    {
        private const string Lib = "ghostty-vt";
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_terminal_grid_ref_track(TerminalHandle terminal, Native.Point point, out IntPtr tracked);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_tracked_grid_ref_point(TrackedHandle tracked, int tag, out Native.PointValue point);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_tracked_grid_ref_snapshot(TrackedHandle tracked, out Native.GridRef reference);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void ghostty_tracked_grid_ref_free(IntPtr tracked);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_terminal_scroll_viewport(TerminalHandle terminal, ScrollBehavior behavior);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_selection_gesture_new(IntPtr allocator, out IntPtr gesture);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void ghostty_selection_gesture_free(IntPtr gesture, IntPtr terminal);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void ghostty_selection_gesture_reset(GestureHandle gesture, TerminalHandle terminal);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_selection_gesture_event_new(IntPtr allocator, out IntPtr ev, int type);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void ghostty_selection_gesture_event_free(IntPtr ev);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_selection_gesture_event_set(GestureEventHandle ev, int option, void* value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int ghostty_selection_gesture_event(GestureHandle gesture, TerminalHandle terminal, GestureEventHandle ev, Native.Selection* selection);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ghostty_formatter_set")] internal static extern int ghostty_formatter_set(FormatterHandle formatter, int option, void* value);
    }
}
