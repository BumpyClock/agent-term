using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AgentTerm.Terminal;

/// <summary>User-selected archive envelope. Its version is independent of the native snapshot format and C ABI.</summary>
public static class TerminalSnapshotArchive
{
    private const int HeaderSize = 56;
    private static ReadOnlySpan<byte> Magic => "AGTERMVT"u8;

    /// <summary>Writes beside the destination, flushes, then replaces it. A failed write leaves the existing archive intact.</summary>
    public static void SaveFile(string path, ReadOnlySpan<byte> snapshot)
    {
        var destination = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".agentterm-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                Save(stream, snapshot);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public static void Save(Stream destination, ReadOnlySpan<byte> snapshot)
    {
        if (snapshot.IsEmpty || snapshot.Length > GhosttyTerminal.MaximumSnapshotBytes) throw new InvalidDataException("Invalid archive payload size.");
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)snapshot.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header[16..], DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        SHA256.HashData(snapshot, header[24..]);
        destination.Write(header);
        destination.Write(snapshot);
    }

    public static GhosttyTerminal Open(Stream source)
    {
        try
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            source.ReadExactly(header);
            if (!header[..8].SequenceEqual(Magic)) throw new InvalidDataException("This file is not an AgentTerm terminal archive.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != 1) throw new InvalidDataException("Unsupported AgentTerm archive version.");
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            if (size == 0 || size > GhosttyTerminal.MaximumSnapshotBytes) throw new InvalidDataException("Archive exceeds the 64 MiB limit or has an empty payload.");
            var bytes = new byte[(int)size];
            source.ReadExactly(bytes);
            if (source.ReadByte() != -1) throw new InvalidDataException("Archive has trailing data.");
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), header[24..])) throw new InvalidDataException("Archive checksum does not match. The file is damaged.");
            return GhosttyTerminal.FromSnapshot(bytes);
        }
        catch (EndOfStreamException exception) { throw new InvalidDataException("Terminal archive is truncated.", exception); }
    }
}
