using System.Buffers.Binary;
using System.Text;
using AgentTerm_App;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var app = new IconTestApplication();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            RunAsync(app);
        });
    }

    private static async void RunAsync(Application app)
    {
        try
        {
            AssertAliasMetadata();
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            await AssertDecodedIconAsync(Path.Combine(system, "cmd.exe"));
            await AssertDecodedIconAsync(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"));
            var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "pwsh.exe");
            if (File.Exists(alias)) await AssertDecodedIconAsync(alias);
            else Console.WriteLine("Store PowerShell alias is not installed; installed-alias check skipped.");

            if (ExecutableIcon.Read(Path.Combine(system, "missing-agentterm-shell.exe")) is not null)
                throw new Exception("An unavailable shell must not produce an icon.");
            Console.WriteLine("Executable icon extraction, alias metadata, and WinUI decode checks passed.");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Environment.ExitCode = 1;
        }
        finally { app.Exit(); }
    }

    private static async Task AssertDecodedIconAsync(string executable)
    {
        var bytes = await Task.Run(() => ExecutableIcon.Read(executable));
        if (bytes is null) throw new Exception($"No icon was extracted from {executable}.");
        using var stream = new MemoryStream(bytes).AsRandomAccessStream();
        var image = new BitmapImage { DecodePixelWidth = 32 };
        string? failure = null;
        image.ImageFailed += (_, error) => failure = error.ErrorMessage;
        await image.SetSourceAsync(stream).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        if (failure is not null || image.PixelWidth <= 0 || image.PixelHeight <= 0)
            throw new Exception($"WinUI failed to decode {executable}. Size={image.PixelWidth}x{image.PixelHeight}. {failure}");
        var item = new TabViewItem { IconSource = new ImageIconSource { ImageSource = image } };
        if (item.IconSource is not ImageIconSource source || !ReferenceEquals(source.ImageSource, image))
            throw new Exception($"The decoded image did not reach the tab icon for {executable}.");
        Console.WriteLine($"WinUI icon {image.PixelWidth}x{image.PixelHeight}. Executable {executable}");
    }

    private static void AssertAliasMetadata()
    {
        const string target = @"C:\Program Files\WindowsApps\Example.Shell\shell.exe";
        var valid = CreateAliasMetadata("Example.Package", "Example.Package!App", target);
        if (ExecutableIcon.ParseAppExecutionAliasTarget(valid) != target)
            throw new Exception("App execution alias must resolve its third string as the executable.");
        AssertRejected(valid[..11], "truncated header");
        var wrongTag = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongTag, 0xA000000C);
        AssertRejected(wrongTag, "ordinary symbolic link");
        var overrun = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(overrun.AsSpan(4), (ushort)valid.Length);
        AssertRejected(overrun, "payload length outside buffer");
        var oddLength = (byte[])valid.Clone();
        oddLength[4] |= 1;
        AssertRejected(oddLength, "incomplete UTF-16 code unit");
        var fewStrings = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(fewStrings.AsSpan(8), 2);
        AssertRejected(fewStrings, "fewer than three strings");
        AssertRejected(CreateAliasMetadata("", "App", target), "empty package identity");
        AssertRejected(CreateAliasMetadata("Package", "", target), "empty entry point");
        AssertRejected(CreateAliasMetadata("Package", "App", ""), "empty executable");
        AssertRejected(CreateAliasMetadata("Package", "App", "shell.exe"), "relative executable");
        var unterminated = valid[..^2];
        BinaryPrimitives.WriteUInt16LittleEndian(unterminated.AsSpan(4), (ushort)(unterminated.Length - 8));
        AssertRejected(unterminated, "unterminated executable");
    }

    private static byte[] CreateAliasMetadata(string package, string entryPoint, string executable)
    {
        var strings = Encoding.Unicode.GetBytes($"{package}\0{entryPoint}\0{executable}\0");
        var data = new byte[12 + strings.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x8000001B);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)(4 + strings.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 3);
        strings.CopyTo(data, 12);
        return data;
    }

    private static void AssertRejected(byte[] data, string scenario)
    {
        if (ExecutableIcon.ParseAppExecutionAliasTarget(data) is not null)
            throw new Exception($"Malformed alias metadata was accepted. Scenario {scenario}.");
    }

    private sealed class IconTestApplication : Application { }
}
