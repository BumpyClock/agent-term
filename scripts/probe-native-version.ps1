param([Parameter(Mandatory)][string]$Library)
$ErrorActionPreference = 'Stop'
# Run only in a disposable child process. Never load the native library into MSBuild.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeVersionProbe {
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32", CharSet = CharSet.Ansi)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr module);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Version();
    public static string Read(string path) {
        var module = LoadLibraryW(path);
        if (module == IntPtr.Zero) throw new Exception("Cannot load native library, Win32 error " + Marshal.GetLastWin32Error());
        try {
            var symbol = GetProcAddress(module, "ghostty_version");
            if (symbol == IntPtr.Zero) throw new Exception("Missing ghostty_version export.");
            return Marshal.PtrToStringAnsi(((Version)Marshal.GetDelegateForFunctionPointer(symbol, typeof(Version)))());
        } finally { FreeLibrary(module); }
    }
}
"@
[NativeVersionProbe]::Read([IO.Path]::GetFullPath($Library))
