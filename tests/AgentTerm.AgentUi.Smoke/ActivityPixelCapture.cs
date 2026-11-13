using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Windows.Graphics.Imaging;
using Windows.Storage;

// RenderTargetBitmap does not observe all compositor-owned animations. Sample the
// displayed control instead, and fail if another window takes foreground ownership.
internal static class ActivityPixelCapture
{
    internal static async Task<int> SampleAsync(Window window, FrameworkElement element, int count, string? artifactName = null,
        Windows.Foundation.Rect? region = null)
    {
        await Task.Delay(200);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var hashes = new HashSet<string>();
        for (var index = 0; index < count; index++)
        {
            if (GetForegroundWindow() != hwnd)
                throw new InvalidOperationException("Activity pixel capture requires the smoke window in the foreground.");
            // null targets the XAML visual root, including the centered content's
            // offset inside the window client area.
            var area = region ?? new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight);
            var origin = element.TransformToVisual(null).TransformPoint(new(area.X, area.Y));
            var client = new Point();
            if (!ClientToScreen(hwnd, ref client)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var scale = element.XamlRoot.RasterizationScale;
            var left = client.X + (int)Math.Floor(origin.X * scale);
            var top = client.Y + (int)Math.Floor(origin.Y * scale);
            var width = (int)Math.Ceiling(area.Width * scale);
            var height = (int)Math.Ceiling(area.Height * scale);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("Activity pixel capture has empty bounds.");
            if (!GetClientRect(hwnd, out var bounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (left < client.X || top < client.Y || left + width > client.X + bounds.Right || top + height > client.Y + bounds.Bottom)
                throw new InvalidOperationException("Activity pixel capture extends outside its own window.");
            var pixels = Capture(left, top, width, height);
            hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
            if (artifactName is not null)
            {
                var path = Path.GetFullPath(Path.Combine("artifacts", "program-status", $"{artifactName}-{index}.png"));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, []);
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
                await encoder.FlushAsync();
            }
            await Task.Delay(250);
        }
        return hashes.Count;
    }

    private static byte[] Capture(int left, int top, int width, int height)
    {
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!BitBlt(memory, 0, 0, width, height, screen, left, top, 0x00CC0020))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            SelectObject(memory, previous);
            previous = IntPtr.Zero;
            var info = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height,
                Planes = 1, BitCount = 32
            };
            var pixels = new byte[checked(width * height * 4)];
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) != height)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // BI_RGB leaves the fourth byte unused. Hash only defined color data.
            for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
            return pixels;
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint count, [Out] byte[] pixels, ref BitmapInfoHeader info, uint usage);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
}
