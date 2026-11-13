using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AgentTerm_App;

public sealed partial class MainPage
{
    private uint _hostCellWidth, _hostCellHeight;
    private uint HostCellWidth => (uint)Math.Max(1, Math.Round(Terminal.CellWidth * (XamlRoot?.RasterizationScale ?? 1)));
    private uint HostCellHeight => (uint)Math.Max(1, Math.Round(Terminal.CellHeight * (XamlRoot?.RasterizationScale ?? 1)));
    private XamlRoot? _hostXamlRoot;

    private void ConfigureHostUi()
    {
        Loaded += (_, _) =>
        {
            if (_hostXamlRoot is not null) _hostXamlRoot.Changed -= HostRootChanged;
            _hostXamlRoot = XamlRoot;
            if (_hostXamlRoot is not null) _hostXamlRoot.Changed += HostRootChanged;
            UpdateGridSize();
        };
        Unloaded += (_, _) =>
        {
            if (_hostXamlRoot is not null) _hostXamlRoot.Changed -= HostRootChanged;
            _hostXamlRoot = null;
        };
        var diagnostics = new KeyboardAccelerator
        {
            Key = VirtualKey.F12,
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift
        };
        diagnostics.Invoked += async (_, args) => { args.Handled = true; await ShowHostDiagnosticsAsync(); };
        KeyboardAccelerators.Add(diagnostics);
    }

    private void HostRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateGridSize();

    /// <summary>Shared diagnostic command, also available with Ctrl+Shift+F12.</summary>
    public async Task ShowHostDiagnosticsAsync()
    {
        if (_session is not { } session) return;
        string Report() => session.ReadDiagnosticReport() + $"Device image cache bytes {Terminal.ImageBitmapCacheBytes} (excludes transient draws)\n";
        var text = new TextBox { Text = Report(), IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinWidth = 420, MaxHeight = 500 };
        var capture = new CheckBox { Content = "Count unsupported protocols (no payloads)", IsChecked = session.UnknownSequenceDiagnosticsEnabled };
        capture.Click += (_, _) =>
        {
            session.SetUnknownSequenceDiagnostics(capture.IsChecked == true);
            text.Text = Report();
        };
        var refresh = new Button { Content = "Refresh" };
        refresh.Click += (_, _) => text.Text = Report();
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(capture);
        content.Children.Add(refresh);
        content.Children.Add(text);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Terminal diagnostics", Content = content, CloseButtonText = "Close" };
        await dialog.ShowAsync();
    }
}
