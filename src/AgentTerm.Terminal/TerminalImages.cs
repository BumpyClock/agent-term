namespace AgentTerm.Terminal;

/// <summary>Where an image draws relative to cell backgrounds and text. Draw order is the declaration order.</summary>
public enum TerminalImageLayer
{
    /// <summary>Below cell backgrounds. Visible only through cells that use the default background.</summary>
    BelowBackground,
    /// <summary>Above cell backgrounds and below text.</summary>
    BelowText,
    /// <summary>Above text.</summary>
    AboveText,
}

/// <summary>How a program positioned a Kitty graphics placement.</summary>
public enum TerminalImagePlacementKind
{
    /// <summary>One row run of Unicode placeholder cells.</summary>
    Virtual,
    /// <summary>Positioned relative to a parent placement.</summary>
    Relative,
    /// <summary>Anchored to a cell. Scrolls with the text.</summary>
    Fixed,
}

/// <summary>
/// Owned, immutable pixels of one image version. Pixels are premultiplied RGBA8, four bytes per pixel, rows packed
/// without padding. <see cref="Generation"/> is unique across images, screens and terminals, so it alone identifies
/// these pixels and is a complete cache key.
/// </summary>
public sealed class TerminalImageData
{
    private readonly byte[] _pixels;

    internal TerminalImageData(uint id, ulong generation, int width, int height, byte[] premultipliedRgba)
    {
        ArgumentNullException.ThrowIfNull(premultipliedRgba);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image size must be positive.");
        if (premultipliedRgba.LongLength != (long)width * height * 4)
            throw new ArgumentException("Pixel data must hold four bytes for every pixel.", nameof(premultipliedRgba));
        Id = id;
        Generation = generation;
        Width = width;
        Height = height;
        _pixels = premultipliedRgba;
    }

    public uint Id { get; }
    public ulong Generation { get; }
    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Pixels => _pixels;
    public long ByteCount => _pixels.LongLength;
}

/// <summary>
/// One image to draw. Positions are viewport cells, where row 0 is the top visible row and negative values start
/// above it. Offsets and sizes are native pixels of the cell size in <see cref="TerminalImages"/>. The source
/// rectangle is in image pixels and scales to the pixel size. A placement can extend past the grid and must be clipped.
/// </summary>
public sealed record TerminalImagePlacement(
    TerminalImageData Image,
    TerminalImageLayer Layer,
    int Z,
    int Column,
    int Row,
    int Columns,
    int Rows,
    int CellOffsetX,
    int CellOffsetY,
    int PixelWidth,
    int PixelHeight,
    int SourceX,
    int SourceY,
    int SourceWidth,
    int SourceHeight)
{
    public uint PlacementId { get; init; }
    public TerminalImagePlacementKind Kind { get; init; } = TerminalImagePlacementKind.Fixed;
}

/// <summary>
/// The images of one frame, sorted in draw order (layer, then z-index). An unchanged image list keeps its instance
/// across frames, so reference equality detects image-only changes.
/// </summary>
public sealed record TerminalImages(IReadOnlyList<TerminalImagePlacement> Placements, int CellPixelWidth, int CellPixelHeight)
{
    public const uint DefaultStorageLimit = 64 * 1024 * 1024;

    public static TerminalImages Empty { get; } = new([], 0, 0);
    public bool IsEmpty => Placements.Count == 0;
}
