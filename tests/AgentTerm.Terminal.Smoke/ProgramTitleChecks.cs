using AgentTerm_App;

internal static class ProgramTitleChecks
{
    internal static void Run()
    {
        if (OperatingSystem.IsWindows())
            Check(ProgramTitle.Clean("C:\\" + new string('a', 90) + "\\pwsh.exe") is null, "Classify complete executable paths before display truncation.");
        Check(ProgramTitle.Clean(new string('a', 90)) == new string('a', 80), "Limit ordinary display titles.");
        Check(ProgramTitle.Clean(new string('a', 79) + "😀z") == new string('a', 79), "Do not split a supplementary character.");
        Check(ProgramTitle.Clean("safe\u202Etxt\u2066.exe\u2069") == "safetxt.exe", "Remove directional controls from display titles.");
        Check(ProgramTitle.Clean("👩\u200D💻 café") == "👩\u200D💻 café", "Preserve grapheme joiners and ordinary Unicode.");
        Check(ProgramTitle.Clean("\u202E ⠂ Working\r\n") == "Working", "Ignore controls before leading spinner cleanup.");
        var cached = new ProgramTitle.Cache();
        Check(cached.Read("⠂ Working") == "Working", "Cache must normalize its first title.");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) cached.Read("⠂ Working");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, $"Repeated unchanged titles must not allocate. Allocated {allocated} bytes.");
        Check(cached.Read("⠄ Ready") == "Ready" && cached.Read("") is null, "Title changes and cleared titles invalidate the cached label.");
        Console.WriteLine($"Title cache allocated {allocated} bytes for 10000 unchanged reads.");
        Console.WriteLine("Program title checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
