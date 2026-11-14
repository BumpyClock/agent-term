using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentTerm.Terminal;

// libghostty-vt image pixels stay valid only until the terminal is next modified, so BeginFrame copies them while
// the caller still excludes writers.
public sealed partial class GhosttyTerminal
{
    internal const long RetainedPixelBudget = 32L * 1024 * 1024;
    internal const long PixelCopyPerFrame = 16L * 1024 * 1024;
    private ImageIteratorHandle? _imageIteratorHandle, _visibleIteratorHandle;
    private IntPtr _imageIterator;
    private readonly List<(uint Id, ulong Generation)> _visibleImages = [], _scannedVisible = [];
    private long _imageScans;
    private ulong? _scannedStorageGeneration;
    private bool _imageRescan;
    private long? _imageRefreshAt;
    private volatile bool _imageRetry;
    // Image pixels keyed by image generation, which never repeats across images, screens or terminals.
    private readonly Dictionary<ulong, TerminalImageData> _imagePixels = [];
    private readonly Dictionary<ulong, RawImage> _rawImages = [];
    private readonly Dictionary<ulong, long> _lastVisibleScan = [];
    private ImageCapture _lastImageCapture = new([], 0, 0);
    private RawImage[]? _materializedImages;
    private ulong? _placementsGeneration, _retriedPlacements;
    private TerminalImages _images = TerminalImages.Empty;

    // ghostty_sys_set is process-global and not thread-safe. A static constructor runs before any instance exists, so
    // no other thread can be inside the library yet.
    static unsafe GhosttyTerminal()
    {
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, nuint, SysImage*, byte> decode = &DecodePng;
        ImageNative.ghostty_sys_set(2, (IntPtr)decode);
    }

    /// <summary>
    /// PNG decode callback for Kitty graphics. Runs inside a terminal write, so it only decodes into memory the library
    /// allocates and never lets an exception cross the native boundary.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static unsafe byte DecodePng(IntPtr userdata, IntPtr allocator, byte* data, nuint length, SysImage* output)
    {
        byte* buffer = null;
        nuint size = 0;
        try
        {
            if (length < 24 || length > int.MaxValue) return 0;
            var png = new ReadOnlySpan<byte>(data, (int)length);
            ulong width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png[16..]);
            ulong height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png[20..]);
            if (width == 0 || height == 0 || width * height > (ulong)PngDecoder.MaxPixels ||
                width * height > (ulong)PngDecoder.MaxDecodedBytes / 4) return 0;
            size = (nuint)(width * height * 4);
            var pixels = PngDecoder.TryDecode(png, out var decodedWidth, out var decodedHeight);
            if (pixels is null || (ulong)decodedWidth != width || (ulong)decodedHeight != height) return 0;
            buffer = ImageNative.ghostty_alloc(allocator, size);
            if (buffer is null) return 0;
            pixels.CopyTo(new Span<byte>(buffer, pixels.Length));
            output->Width = (uint)width;
            output->Height = (uint)height;
            output->Data = buffer;
            output->Length = size;
            buffer = null;
            return 1;
        }
        catch { return 0; }
        finally { if (buffer is not null) ImageNative.ghostty_free(allocator, buffer, size); }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SysImage { public uint Width, Height; public byte* Data; public nuint Length; }

    /// <summary>Bytes of decoded pixels held for the active screen's placed images.</summary>
    public long ImageCacheBytes => _imagePixels.Values.Sum(image => image.ByteCount);

    /// <summary>
    /// Sets the cell size in device pixels. Kitty placement sizes, cell-size reports and image cell counts depend on
    /// it, and the terminal positions no image while it is zero.
    /// </summary>
    public void SetCellPixelSize(uint width, uint height)
    {
        ThrowIfDisposed();
        if (width == _cellWidthPx && height == _cellHeightPx) return;
        Resize(_columns, _rows, width, height);
    }

    /// <summary>Sets the Kitty image storage limit for both screens. Zero disables the protocol and deletes every image.</summary>
    public unsafe void SetImageStorageLimit(ulong bytes)
    {
        ThrowIfDisposed();
        ThrowIfFailed(Native.ghostty_terminal_set(_handle, 16, (IntPtr)(&bytes)), "Could not set the image storage limit.");
        _presentationDirty = true;
    }

    /// <summary>The Kitty image storage limit of the active screen, in bytes.</summary>
    public unsafe ulong ImageStorageLimit
    {
        get
        {
            ThrowIfDisposed();
            ulong value = 0;
            ThrowIfFailed(RenderNative.ghostty_terminal_get(_handle, 26, &value), "Could not read the image storage limit.");
            return value;
        }
    }

    /// <summary>When the next frame must be captured for images: a due animation frame, or now after a missed image.</summary>
    private long? ImageRefreshAt => _imageRetry ? Environment.TickCount64 : _imageRefreshAt;

    /// <summary>Advances playing animations. Runs under terminal exclusion before the render update.</summary>
    private unsafe void TickImageAnimations()
    {
        _imageRefreshAt = null;
        _imageRescan |= _imageRetry;
        _imageRetry = false;
        var graphics = ReadGraphics();
        if (graphics == IntPtr.Zero) return;
        ulong next = 0;
        if (ImageNative.ghostty_kitty_graphics_animation_tick(graphics, (ulong)Environment.TickCount64, &next) != 0) return;
        // The storage generation does not change when an animation advances, so rescan image generations each frame.
        _imageRefreshAt = (long)Math.Min(next, long.MaxValue);
        _imageRescan = true;
    }

    private unsafe ImageCapture CaptureImagePixels(IntPtr state)
    {
        var cell = ((int)_cellWidthPx, (int)_cellHeightPx);
        var graphics = ReadGraphics();
        ulong storageGeneration = 0;
        if (graphics != IntPtr.Zero && ImageNative.ghostty_kitty_graphics_get(graphics, 2, &storageGeneration) != 0) storageGeneration = 0;
        _visibleImages.Clear();
        if (storageGeneration != 0 && _cellWidthPx != 0 && _cellHeightPx != 0) ReadVisibleImages(state);
        if (!_imageRescan && _scannedStorageGeneration == storageGeneration &&
            CollectionsMarshal.AsSpan(_visibleImages).SequenceEqual(CollectionsMarshal.AsSpan(_scannedVisible)))
            return (_lastImageCapture.CellWidth, _lastImageCapture.CellHeight) == cell
                ? _lastImageCapture
                : _lastImageCapture = _lastImageCapture with { CellWidth = cell.Item1, CellHeight = cell.Item2 };
        _imageRescan = false;
        _scannedStorageGeneration = storageGeneration;
        _scannedVisible.Clear();
        _scannedVisible.AddRange(_visibleImages);
        if (storageGeneration == 0)
        {
            _rawImages.Clear();
            _lastVisibleScan.Clear();
            return _lastImageCapture = new([], cell.Item1, cell.Item2);
        }
        var scan = ++_imageScans;
        var deferred = false;
        long copied = 0;
        uint width = 0, height = 0; int format = 0; Native.Utf8String pixels = default;
        var imageKeys = stackalloc int[] { 3, 4, 5, 7 };
        var imageValues = stackalloc void*[] { &width, &height, &format, &pixels };
        foreach (var (id, generation) in _visibleImages)
        {
            if (_rawImages.ContainsKey(generation)) { _lastVisibleScan[generation] = scan; continue; }
            var image = StoredImage(graphics, id, generation);
            if (image == IntPtr.Zero) continue;
            var result = ImageNative.ghostty_kitty_graphics_image_get_multi(image, 4, imageKeys, imageValues, null);
            if (result == -4) continue; // Pixels still arriving. The storage generation changes when they land.
            ThrowIfFailed(result, "Could not read image pixels.");
            var channels = format switch { 0 => 3, 1 => 4, 3 => 2, 4 => 1, _ => 0 };
            if (channels == 0 || width == 0 || height == 0 || (ulong)width * height > (ulong)Array.MaxLength / 4 ||
                pixels.Length != (nuint)((ulong)width * height * (ulong)channels)) continue;
            var decoded = (long)width * height * 4;
            // A held frame is reused without image retries until the hold ends, so it copies every visible image.
            if (!_holdActive && copied > 0 && copied + decoded > PixelCopyPerFrame) { deferred = true; continue; }
            copied += decoded;
            var copy = GC.AllocateUninitializedArray<byte>(checked((int)pixels.Length));
            Marshal.Copy(pixels.Data, copy, 0, copy.Length);
            _rawImages[generation] = new RawImage(id, generation, (int)width, (int)height, channels, copy);
            _lastVisibleScan[generation] = scan;
        }
        if (deferred) _imageRescan = _imageRetry = true;
        ReleaseHiddenImages(graphics, scan);
        return _lastImageCapture = new(_rawImages.Values.ToArray(), cell.Item1, cell.Item2);
    }

    private unsafe void ReadVisibleImages(IntPtr state)
    {
        if (_visibleIteratorHandle is null)
        {
            ThrowIfFailed(ImageNative.ghostty_render_state_image_iterator_new(IntPtr.Zero, out var created), "Could not create an image iterator.");
            _visibleIteratorHandle = new ImageIteratorHandle(created);
        }
        using var lease = new HandleLease(_visibleIteratorHandle);
        var iterator = lease.Raw;
        ThrowIfFailed(RenderNative.ghostty_render_state_get(state, RenderStateData.ImageIterator, &iterator), "Could not read render state images.");
        while (ImageNative.ghostty_render_state_image_iterator_next(iterator) != 0)
        {
            int protocol = 0; uint id = 0; ulong generation = 0;
            ImageGet(iterator, 1, &protocol);
            if (protocol != 0) continue;
            ImageGet(iterator, 2, &id); ImageGet(iterator, 3, &generation);
            _visibleImages.Add((id, generation));
        }
    }

    private static unsafe IntPtr StoredImage(IntPtr graphics, uint id, ulong generation)
    {
        var image = ImageNative.ghostty_kitty_graphics_image(graphics, id);
        if (image == IntPtr.Zero) return IntPtr.Zero;
        ulong current = 0;
        ThrowIfFailed(ImageNative.ghostty_kitty_graphics_image_get(image, 9, &current), "Could not read image generation.");
        return current == generation ? image : IntPtr.Zero;
    }

    private void ReleaseHiddenImages(IntPtr graphics, long scan)
    {
        long bytes = 0;
        foreach (var (generation, _) in _lastVisibleScan.Where(entry => entry.Value != scan).OrderByDescending(entry => entry.Value).ToArray())
        {
            var image = _rawImages[generation];
            if (StoredImage(graphics, image.Id, generation) != IntPtr.Zero && (bytes += image.DecodedBytes) <= RetainedPixelBudget) continue;
            _rawImages.Remove(generation);
            _lastVisibleScan.Remove(generation);
        }
    }

    /// <summary>Builds the frame's image list from the render state. Reads no terminal memory.</summary>
    private unsafe TerminalImages CompleteImages(ImageCapture? capture)
    {
        if (capture is null) return TerminalImages.Empty;
        var added = false;
        if (!ReferenceEquals(_materializedImages, capture.Images))
        {
            if (capture.Images.Length == 0) _imagePixels.Clear();
            else
            {
                var live = new HashSet<ulong>();
                foreach (var raw in capture.Images)
                {
                    live.Add(raw.Generation);
                    if (_imagePixels.ContainsKey(raw.Generation)) continue;
                    _imagePixels[raw.Generation] = raw.Materialize();
                    added = true;
                }
                foreach (var generation in _imagePixels.Keys.Where(key => !live.Contains(key)).ToArray()) _imagePixels.Remove(generation);
            }
            _materializedImages = capture.Images;
        }
        ulong placementsGeneration = 0;
        StateGet(RenderStateData.ImageGeneration, &placementsGeneration);
        var cell = (Width: capture.CellWidth, Height: capture.CellHeight);
        if (!added && _placementsGeneration == placementsGeneration &&
            (_images.IsEmpty || (_images.CellPixelWidth, _images.CellPixelHeight) == cell)) return _images;
        _placementsGeneration = placementsGeneration;
        EnsureImageIterator();
        using var iteratorLease = new HandleLease(_imageIteratorHandle);
        var iterator = iteratorLease.Raw;
        StateGet(RenderStateData.ImageIterator, &iterator);
        var placements = new List<TerminalImagePlacement>();
        var missing = false;
        int layer = 0, z = 0, column = 0, row = 0, kind = 2;
        uint columns = 0, rows = 0, offsetX = 0, offsetY = 0, width = 0, height = 0, sourceX = 0, sourceY = 0, sourceWidth = 0, sourceHeight = 0, placementId = 0;
        var keys = stackalloc int[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 };
        var values = stackalloc void*[] { &layer, &z, &column, &row, &columns, &rows, &offsetX, &offsetY,
            &width, &height, &sourceX, &sourceY, &sourceWidth, &sourceHeight, &placementId, &kind };
        while (ImageNative.ghostty_render_state_image_iterator_next(_imageIterator) != 0)
        {
            int protocol = 0;
            ImageGet(_imageIterator, 1, &protocol);
            if (protocol != 0) continue;
            uint id = 0; ulong generation = 0;
            ImageGet(_imageIterator, 2, &id); ImageGet(_imageIterator, 3, &generation);
            if (!_imagePixels.TryGetValue(generation, out var image)) { missing = true; continue; }
            ThrowIfFailed(ImageNative.ghostty_render_state_image_get_multi(_imageIterator, 16, keys, values, null), "Could not read an image placement.");
            if (layer is < 0 or > 2 || width == 0 || height == 0 || sourceWidth == 0 || sourceHeight == 0 ||
                (ulong)sourceX + sourceWidth > (ulong)image.Width || (ulong)sourceY + sourceHeight > (ulong)image.Height) continue;
            placements.Add(new TerminalImagePlacement(image, (TerminalImageLayer)layer, z, column, row,
                (int)Math.Min(columns, int.MaxValue), (int)Math.Min(rows, int.MaxValue), (int)Math.Min(offsetX, int.MaxValue),
                (int)Math.Min(offsetY, int.MaxValue), (int)Math.Min(width, int.MaxValue), (int)Math.Min(height, int.MaxValue),
                (int)sourceX, (int)sourceY, (int)sourceWidth, (int)sourceHeight)
            {
                PlacementId = placementId,
                Kind = kind is >= 0 and <= 2 ? (TerminalImagePlacementKind)kind : TerminalImagePlacementKind.Fixed,
            });
        }
        if (missing && _retriedPlacements != placementsGeneration)
        {
            // Pixels changed without a storage generation change, as an animation frame can. Fetch on the next frame,
            // once per placement list, so pixels that can never be read do not keep requesting frames.
            _retriedPlacements = placementsGeneration;
            _placementsGeneration = null;
            _imageRetry = true;
        }
        _images = placements.Count == 0 ? TerminalImages.Empty
            : new TerminalImages(placements.OrderBy(p => p.Layer).ThenBy(p => p.Z).ToArray(), cell.Width, cell.Height);
        return _images;
    }

    private unsafe int LastImageRow(IntPtr state)
    {
        if (_cellHeightPx == 0) return -1;
        ThrowIfFailed(ImageNative.ghostty_render_state_image_iterator_new(IntPtr.Zero, out var iterator), "Could not create presentation images.");
        using var owned = new ImageIteratorHandle(iterator);
        ThrowIfFailed(RenderNative.ghostty_render_state_get(state, RenderStateData.ImageIterator, &iterator), "Could not read presentation images.");
        var last = -1;
        while (ImageNative.ghostty_render_state_image_iterator_next(iterator) != 0)
        {
            int row = 0;
            uint height = 0, offset = 0;
            ImageNative.ghostty_render_state_image_get(iterator, 7, &row);
            ImageNative.ghostty_render_state_image_get(iterator, 13, &height);
            ImageNative.ghostty_render_state_image_get(iterator, 11, &offset);
            if (height == 0) continue;
            var end = row + ((long)offset + height + _cellHeightPx - 1) / _cellHeightPx - 1;
            last = (int)Math.Max(last, Math.Min(_rows - 1, end));
        }
        return last;
    }

    /// <summary>Converts decoded Kitty pixels (gray, gray+alpha, RGB or straight RGBA) to premultiplied RGBA8.</summary>
    internal static byte[] ToPremultipliedRgba(ReadOnlySpan<byte> source, int channels, int pixels)
    {
        var output = GC.AllocateUninitializedArray<byte>(checked(pixels * 4));
        for (int i = 0, s = 0, o = 0; i < pixels; i++, s += channels, o += 4)
        {
            byte r, g, b, a;
            switch (channels)
            {
                case 1: r = g = b = source[s]; a = 255; break;
                case 2: r = g = b = source[s]; a = source[s + 1]; break;
                case 3: r = source[s]; g = source[s + 1]; b = source[s + 2]; a = 255; break;
                default: r = source[s]; g = source[s + 1]; b = source[s + 2]; a = source[s + 3]; break;
            }
            if (a != 255) { r = Premultiply(r, a); g = Premultiply(g, a); b = Premultiply(b, a); }
            output[o] = r; output[o + 1] = g; output[o + 2] = b; output[o + 3] = a;
        }
        return output;
    }

    private static byte Premultiply(byte color, byte alpha) => (byte)((color * alpha + 127) / 255);

    private unsafe IntPtr ReadGraphics()
    {
        IntPtr graphics = 0;
        return RenderNative.ghostty_terminal_get(_handle, 30, &graphics) == 0 ? graphics : IntPtr.Zero;
    }

    private static unsafe void ImageGet(IntPtr iterator, int key, void* value)
    {
        var result = ImageNative.ghostty_render_state_image_get(iterator, key, value);
        if (result != -4) ThrowIfFailed(result, "Could not read an image placement.");
    }

    private void EnsureImageIterator()
    {
        if (_imageIteratorHandle is not null) return;
        ThrowIfFailed(ImageNative.ghostty_render_state_image_iterator_new(IntPtr.Zero, out var iterator), "Could not create an image iterator.");
        _imageIteratorHandle = new ImageIteratorHandle(iterator);
        _imageIterator = iterator;
    }

    private void DisposeImages()
    {
        _imageIterator = IntPtr.Zero;
        _imageIteratorHandle?.Dispose();
        _visibleIteratorHandle?.Dispose();
        _imagePixels.Clear();
        _rawImages.Clear();
        _lastVisibleScan.Clear();
        _visibleImages.Clear();
        _scannedVisible.Clear();
        _lastImageCapture = new([], 0, 0);
        _materializedImages = null;
        _images = TerminalImages.Empty;
    }

    internal sealed record ImageCapture(RawImage[] Images, int CellWidth, int CellHeight);

    internal sealed class RawImage(uint id, ulong generation, int width, int height, int channels, byte[] pixels)
    {
        private byte[]? _raw = pixels;
        private TerminalImageData? _materialized;
        internal uint Id { get; } = id;
        internal ulong Generation { get; } = generation;
        internal long RawBytes => _raw?.LongLength ?? 0;
        internal long DecodedBytes { get; } = (long)width * height * 4;

        internal TerminalImageData Materialize()
        {
            if (_materialized is not null) return _materialized;
            _materialized = new TerminalImageData(Id, Generation, width, height,
                ToPremultipliedRgba(_raw!, channels, width * height));
            _raw = null;
            return _materialized;
        }
    }

    private sealed class ImageIteratorHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ImageIteratorHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() { ImageNative.ghostty_render_state_image_iterator_free(handle); return true; }
    }

    private static unsafe partial class ImageNative
    {
        private const string Lib = "ghostty-vt";
        [LibraryImport(Lib)] internal static partial int ghostty_render_state_image_iterator_new(IntPtr allocator, out IntPtr iterator);
        [LibraryImport(Lib)] internal static partial void ghostty_render_state_image_iterator_free(IntPtr iterator);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial byte ghostty_render_state_image_iterator_next(IntPtr iterator);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial int ghostty_render_state_image_get(IntPtr iterator, int key, void* value);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial int ghostty_render_state_image_get_multi(IntPtr iterator, nuint count, int* keys, void** values, nuint* written);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial int ghostty_kitty_graphics_get(IntPtr graphics, int key, void* value);
        [LibraryImport(Lib)] internal static partial int ghostty_kitty_graphics_animation_tick(IntPtr graphics, ulong nowMs, ulong* nextMs);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial IntPtr ghostty_kitty_graphics_image(IntPtr graphics, uint imageId);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial int ghostty_kitty_graphics_image_get(IntPtr image, int key, void* value);
        [LibraryImport(Lib), SuppressGCTransition] internal static partial int ghostty_kitty_graphics_image_get_multi(IntPtr image, nuint count, int* keys, void** values, nuint* written);
        [LibraryImport(Lib)] internal static partial int ghostty_sys_set(int option, IntPtr value);
        [LibraryImport(Lib)] internal static partial byte* ghostty_alloc(IntPtr allocator, nuint length);
        [LibraryImport(Lib)] internal static partial void ghostty_free(IntPtr allocator, byte* memory, nuint length);
    }
}
