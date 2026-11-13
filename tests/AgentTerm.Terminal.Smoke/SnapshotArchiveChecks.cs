using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgentTerm.Terminal;

internal static class SnapshotArchiveChecks
{
    internal static void Run()
    {
        using var original = new GhosttyTerminal(32, 4);
        var cluster = "a" + new string('\u0301', 48);
        original.Write(Encoding.UTF8.GetBytes("history marker\r\n1\r\n2\r\n3\r\n\x1b[31;1m界 " + cluster + "\x1b[0m"));
        original.BeginSelection(0, 0); original.ExtendSelection(1, 0); original.EndSelection();
        using var archive = new MemoryStream();
        TerminalSnapshotArchive.Save(archive, original.ExportSnapshot());
        archive.Position = 0;
        using var restored = TerminalSnapshotArchive.Open(archive);
        Equal(original.ReadText(), restored.ReadText(), "screen text and long graphemes");
        CompareFrames(original.ReadFrame(), restored.ReadFrame());
        Equal(false, restored.ReadFrame().HasSelection, "native format intentionally omits selection");
        Equal(original.ReadPresentedViewport().TotalRows, restored.ReadPresentedViewport().TotalRows, "history extent");
        if (!restored.Find("history marker", true)) throw new Exception("Restored history must be searchable.");
        restored.ClearSelection(); restored.ScrollToBottom();
        // Snapshot restore must reconnect the managed reply callback.
        restored.Write("\x1b[6n"u8);
        if (restored.TakeResponses().Length != 1) throw new Exception("Restored reply callback missing.");

        using var alternate = new GhosttyTerminal(20, 3);
        alternate.Write("primary\x1b[?1049h\x1b[32malt界"u8);
        using var altCopy = GhosttyTerminal.FromSnapshot(alternate.ExportSnapshot());
        Equal(true, altCopy.IsAlternateScreen, "alternate mode");
        CompareFrames(alternate.ReadFrame(), altCopy.ReadFrame());
        altCopy.Write("\x1b[?1049l"u8);
        Equal("primary", altCopy.ReadText(), "inactive primary screen");

        foreach (var prefix in new[] { new byte[] { 0xF0, 0x9F }, "\x1b[3"u8.ToArray(), "\x1b]2;par"u8.ToArray() })
        {
            using var partial = new GhosttyTerminal(20, 3);
            partial.Write(prefix);
            using var copy = GhosttyTerminal.FromSnapshot(partial.ExportSnapshot());
            using var secondCopy = GhosttyTerminal.FromSnapshot(copy.ExportSnapshot());
            var suffix = prefix[0] == 0xF0 ? new byte[] { 0x99, 0x82 } : prefix[1] == (byte)'[' ? "1mred"u8.ToArray() : "tial\a"u8.ToArray();
            partial.Write(suffix); copy.Write(suffix); secondCopy.Write(suffix);
            Equal(partial.ReadText(), copy.ReadText(), "parser continuation");
            Equal(partial.ReadText(), secondCopy.ReadText(), "retained continuation");
            Equal(partial.ReadTitle(), copy.ReadTitle(), "OSC continuation");
            CompareFrames(partial.ReadFrame(), copy.ReadFrame());
        }

        var valid = archive.ToArray();
        Reject(valid[..20], "short header");
        Reject(valid[..^1], "truncated payload");
        Reject([.. valid, 0], "trailing data");
        var version = (byte[])valid.Clone(); version[8] = 2; Reject(version, "envelope version");
        var large = (byte[])valid.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(large.AsSpan(12), uint.MaxValue); Reject(large, "oversized payload");
        var damaged = (byte[])valid.Clone(); damaged[^1] ^= 1; Reject(damaged, "checksum");
        var nativeDamage = (byte[])valid.Clone(); nativeDamage[56] ^= 0xFF;
        SHA256.HashData(nativeDamage.AsSpan(56), nativeDamage.AsSpan(24, 32)); Reject(nativeDamage, "native decoder corruption");
        var nativeTruncated = valid[..^1];
        BinaryPrimitives.WriteUInt32LittleEndian(nativeTruncated.AsSpan(12), (uint)nativeTruncated.Length - 56);
        SHA256.HashData(nativeTruncated.AsSpan(56), nativeTruncated.AsSpan(24, 32)); Reject(nativeTruncated, "native FINISH truncation");
        Throws(() => GhosttyTerminal.FromSnapshot(new byte[64 * 1024 * 1024 + 1]), "native size limit");
        Equal(true, original.ReadText().Contains(cluster), "failed decode leaves original intact");
        restored.Dispose();
        try { restored.ReadText(); throw new Exception("Disposed archive terminal remained usable."); }
        catch (ObjectDisposedException) { }
        Console.WriteLine("Snapshot archive checks passed.");
    }

    private static void CompareFrames(TerminalFrame expected, TerminalFrame actual)
    {
        Equal(expected.Columns, actual.Columns, "columns"); Equal(expected.Rows, actual.Rows, "rows");
        Equal(expected.Cursor, actual.Cursor, "cursor state");
        for (var i = 0; i < expected.Cells.Length; i++)
            Equal(expected.Cells[i] with { Selected = false }, actual.Cells[i] with { Selected = false }, "cell style/text/width");
    }
    private static void Equal<T>(T expected, T actual, string reason)
    {
        if (!Equals(expected, actual)) throw new Exception($"Snapshot {reason}: expected {expected}, got {actual}.");
    }
    private static void Reject(byte[] bytes, string reason) => Throws(() => TerminalSnapshotArchive.Open(new MemoryStream(bytes)).Dispose(), reason);
    private static void Throws(Action action, string reason)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Archive must reject " + reason);
    }
}
