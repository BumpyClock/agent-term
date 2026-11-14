using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

internal static class ActivityPixelCaptureChecks
{
    internal static async Task RunAsync(Window window)
    {
        var scene = new Grid { Width = 160, Height = 80, Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
        window.Content = scene;
        window.Activate();
        await Program.WaitAsync(() => scene.ActualWidth == 160 && scene.ActualHeight == 80);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        Check(await ActivityPixelCapture.SampleAsync(window, scene, 2) == 1, "Uncovered static content must produce one frame.");
        var origin = scene.TransformToVisual(null).TransformPoint(new(0, 0));
        var client = new Point();
        if (!ClientToScreen(hwnd, ref client)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var scale = scene.XamlRoot.RasterizationScale;
        var left = client.X + (int)Math.Floor(origin.X * scale);
        var top = client.Y + (int)Math.Floor(origin.Y * scale);
        var width = (int)Math.Ceiling(scene.ActualWidth * scale);
        var height = (int)Math.Ceiling(scene.ActualHeight * scale);

        foreach (var (name, disabled, changing, narrow) in new[]
        {
            ("static", false, false, false),
            ("changing", false, true, false),
            ("disabled static", true, false, false),
            ("disabled changing", true, true, false),
            ("one-pixel overlap", false, false, true),
        })
        {
            using (var overlay = new Overlay(narrow ? left + width / 3 : left, narrow ? top + height / 3 : top,
                narrow ? 1 : width, narrow ? 1 : height, disabled))
            {
                Check(GetForegroundWindow() == hwnd, $"The {name} overlay must not activate.");
                Check(IsWindowEnabled(overlay.Handle) != disabled, $"The {name} fixture has the wrong enabled state.");
                Check(overlay.Paint(true) == 0xFFFFFF, $"The {name} overlay must visibly cover the sample.");
                var sampling = ActivityPixelCapture.SampleAsync(window, scene, 3);
                var colors = new HashSet<uint>();
                if (changing)
                {
                    for (var frame = 0; frame < 8 && !sampling.IsCompleted; frame++)
                    {
                        colors.Add(overlay.Paint(frame % 2 == 0));
                        await Task.Delay(50);
                    }
                    Check(colors.SetEquals([0u, 0xFFFFFFu]), $"The {name} overlay must display both black and white pixels.");
                }
                var rejected = false;
                try { await sampling; }
                catch (InvalidOperationException error) when (error.Message.StartsWith("Activity pixel capture is covered by a foreign window", StringComparison.Ordinal))
                {
                    rejected = true;
                }
                Check(rejected, $"The {name} overlay must reject sampling instead of returning a motion result.");
                Check(GetForegroundWindow() == hwnd, $"The {name} rejection must preserve foreground ownership.");
            }
            Check(await ActivityPixelCapture.SampleAsync(window, scene, 2) == 1, $"Removing the {name} overlay must restore clean sampling.");
            Console.WriteLine($"Activity capture rejected {name} nonactivating topmost overlay and recovered.");
        }

        var interrupted = ActivityPixelCapture.SampleAsync(window, scene, 4);
        await Task.Delay(350);
        using (var late = new Overlay(left, top, width, height, false))
        {
            Check(late.Paint(true) == 0xFFFFFF, "The late overlay must visibly cover an ongoing sample.");
            var rejected = false;
            try { await interrupted; }
            catch (InvalidOperationException error) when (error.Message.StartsWith("Activity pixel capture is covered by a foreign window", StringComparison.Ordinal))
            {
                rejected = true;
            }
            Check(rejected, "An overlay arriving during sampling must reject subsequent frames.");
        }
        Check(await ActivityPixelCapture.SampleAsync(window, scene, 2) == 1, "Removing an overlay during sampling must restore clean capture.");

        using (var owned = new Overlay(left, top, width, height, false, hwnd))
        {
            Check(owned.Paint(true) == 0xFFFFFF, "The owned popup must visibly cover the sample.");
            Check(GetForegroundWindow() == hwnd, "The owned popup must not activate.");
            Check(await ActivityPixelCapture.SampleAsync(window, scene, 2) == 1, "An intentional owned popup must remain available for occlusion assertions.");
        }
        using (var outside = new Overlay(left - 2, top, 1, height, false))
            Check(await ActivityPixelCapture.SampleAsync(window, scene, 2) == 1, "A foreign overlay outside the sample must not reject capture.");
        Check(await ActivityPixelCapture.SampleAsync(window, scene, 2) == 1, "Popup cleanup must restore clean sampling.");
        Console.WriteLine("Activity capture preserved intentional owned popup and nonintersecting window sampling.");
    }

    private sealed class Overlay : IDisposable
    {
        internal IntPtr Handle { get; }
        private readonly uint _style;
        private readonly int _left, _top;

        internal Overlay(int left, int top, int width, int height, bool disabled, IntPtr owner = default)
        {
            _left = left + width / 2;
            _top = top + height / 2;
            _style = 0x80000000u | (disabled ? 0x08000000u : 0);
            Handle = CreateWindowEx(0x08000088, "STATIC", "Activity capture overlay fixture", _style | 6,
                left, top, width, height, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!SetWindowPos(Handle, new IntPtr(-1), left, top, width, height, 0x0050))
            {
                var error = Marshal.GetLastWin32Error();
                DestroyWindow(Handle);
                throw new Win32Exception(error);
            }
        }

        internal uint Paint(bool white)
        {
            SetWindowLong(Handle, -16, _style | 0x10000000u | (white ? 6u : 4u));
            if (!RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0105)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.ThrowExceptionForHR(DwmFlush());
            var screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return GetPixel(screen, _left, _top); }
            finally { ReleaseDC(IntPtr.Zero, screen); }
        }

        public void Dispose()
        {
            if (!DestroyWindow(Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr owner, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern uint SetWindowLong(IntPtr hwnd, int index, uint value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr update, IntPtr region, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
}
