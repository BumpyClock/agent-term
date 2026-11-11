using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

namespace AgentTerm_App;

internal static class ExecutableIcon
{
    public static byte[]? Read(string executable)
    {
        // Resource-only loading never runs the executable or its DLL entry point.
        var module = LoadLibraryEx(executable, IntPtr.Zero, 0x00000002 | 0x00000020);
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
            // Keep every size and the original encoded pixels, including alpha and masks.
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

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "EnumResourceNamesW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResourceNameCallback callback, IntPtr parameter);

    [DllImport("kernel32.dll", EntryPoint = "FindResourceW", ExactSpelling = true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LockResource(IntPtr resource);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}
