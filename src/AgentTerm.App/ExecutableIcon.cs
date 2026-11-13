using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentTerm_App;

internal static partial class ExecutableIcon
{
    public static byte[]? Read(string executable)
    {
        // Resource-only loading never runs the executable or its DLL entry point.
        var module = LoadLibraryEx(executable, IntPtr.Zero, 0x00000002 | 0x00000020);
        if (module == IntPtr.Zero && ReadAppExecutionAliasTarget(executable) is { } target)
            module = LoadLibraryEx(target, IntPtr.Zero, 0x00000002 | 0x00000020);
        if (module == IntPtr.Zero) return null;
        try
        {
            IntPtr groupResource = IntPtr.Zero;
            _ = EnumResourceNames(module, (IntPtr)14, (library, type, name, _) =>
            {
                groupResource = FindResource(library, name, type);
                return false;
            }, IntPtr.Zero);
            var group = ReadResource(module, groupResource);
            if (group is null || group.Length < 6) return null;
            var count = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(4));
            if (count == 0 || group.Length < 6 + count * 14) return null;

            // PE group entries use resource IDs where ICO entries use file offsets.
            var images = new byte[count][];
            for (var index = 0; index < count; index++)
            {
                var id = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(6 + index * 14 + 12));
                var image = ReadResource(module, FindResource(module, (IntPtr)id, (IntPtr)3));
                if (image is null) return null;
                images[index] = image;
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(group, 0, 6);
            var offset = 6 + count * 16;
            for (var index = 0; index < count; index++)
            {
                writer.Write(group, 6 + index * 14, 8);
                writer.Write(images[index].Length);
                writer.Write(offset);
                offset = checked(offset + images[index].Length);
            }
            foreach (var image in images) writer.Write(image);
            return stream.ToArray();
        }
        finally
        {
            _ = FreeLibrary(module);
        }
    }

    private static string? ReadAppExecutionAliasTarget(string executable)
    {
        using var file = CreateFile(executable, 0, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (file.IsInvalid) return null;
        var data = new byte[16 * 1024]; // MAXIMUM_REPARSE_DATA_BUFFER_SIZE
        if (!DeviceIoControl(file, 0x000900A8, IntPtr.Zero, 0, data, data.Length, out var count, IntPtr.Zero))
            return null;
        return ParseAppExecutionAliasTarget(data.AsSpan(0, count));
    }

    internal static string? ParseAppExecutionAliasTarget(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x8000001B)
            return null; // IO_REPARSE_TAG_APPEXECLINK
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (payloadLength < 4 || payloadLength > data.Length - 8 || (payloadLength & 1) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) < 3)
            return null;

        // APPEXECLINK stores package identity, entry point, then executable path.
        var strings = MemoryMarshal.Cast<byte, char>(data.Slice(12, payloadLength - 4));
        for (var index = 0; index < 3; index++)
        {
            var end = strings.IndexOf('\0');
            if (end <= 0) return null;
            if (index == 2)
            {
                var target = strings[..end].ToString();
                return Path.IsPathFullyQualified(target) ? target : null;
            }
            strings = strings[(end + 1)..];
        }
        return null;
    }

    private static byte[]? ReadResource(IntPtr module, IntPtr resource)
    {
        if (resource == IntPtr.Zero) return null;
        var size = SizeofResource(module, resource);
        var data = LockResource(LoadResource(module, resource));
        if (size == 0 || data == IntPtr.Zero) return null;
        var bytes = new byte[checked((int)size)];
        Marshal.Copy(data, bytes, 0, bytes.Length);
        return bytes;
    }

    private delegate bool EnumResourceNameCallback(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string file, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, int inputSize, Span<byte> output, int outputSize, out int returned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "EnumResourceNamesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumResourceNames(IntPtr module, IntPtr type, EnumResourceNameCallback callback, IntPtr parameter);

    [LibraryImport("kernel32.dll", EntryPoint = "FindResourceW")]
    private static partial IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

    [LibraryImport("kernel32.dll")]
    private static partial uint SizeofResource(IntPtr module, IntPtr resource);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LoadResource(IntPtr module, IntPtr resource);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LockResource(IntPtr resource);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(IntPtr module);
}
