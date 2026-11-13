using System;
using System.Collections.Generic;
using System.IO;
using AgentTerm.Terminal;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AgentTerm_App;

public sealed partial class MainWindow
{
    private readonly List<TerminalArchiveWindow> _archiveWindows = [];
    private bool _archivesClosed;

    private void ConfigureArchiveMenu()
    {
        var save = new MenuFlyoutItem { Text = "Save terminal archive…" };
        var open = new MenuFlyoutItem { Text = "Open terminal archive…" };
        ProfileMenu.Items.Add(new MenuFlyoutSeparator());
        ProfileMenu.Items.Add(save); ProfileMenu.Items.Add(open);
        save.Click += async (_, _) =>
        {
            try
            {
                var page = _visibleTab?.ActivePage ?? throw new InvalidOperationException("No terminal is selected.");
                var picker = new FileSavePicker { SuggestedFileName = "Terminal archive", SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeChoices.Add("AgentTerm terminal archive", new List<string> { ".agtvt" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                if (await picker.PickSaveFileAsync() is not { } file) return;
                var snapshot = await page.CaptureArchiveSnapshotAsync();
                await System.Threading.Tasks.Task.Run(() => TerminalSnapshotArchive.SaveFile(file.Path, snapshot));
            }
            catch (Exception exception) { await ShowArchiveError(exception); }
        };
        open.Click += async (_, _) =>
        {
            GhosttyTerminal? terminal = null;
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add(".agtvt");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                if (await picker.PickSingleFileAsync() is not { } file) return;
                terminal = await System.Threading.Tasks.Task.Run(() =>
                {
                    using var stream = File.OpenRead(file.Path);
                    return TerminalSnapshotArchive.Open(stream);
                });
                if (_archivesClosed) { terminal.Dispose(); return; }
                var window = new TerminalArchiveWindow(terminal, file.Name);
                terminal = null;
                _archiveWindows.Add(window);
                window.Closed += (_, _) => _archiveWindows.Remove(window);
                window.Activate();
            }
            catch (Exception exception) { terminal?.Dispose(); await ShowArchiveError(exception); }
        };
        Closed += (_, _) => { _archivesClosed = true; foreach (var window in _archiveWindows.ToArray()) window.Close(); };
    }

    private async System.Threading.Tasks.Task ShowArchiveError(Exception exception)
    {
        if (_archivesClosed) return;
        var dialog = new ContentDialog { XamlRoot = WindowRoot.XamlRoot, Title = "Terminal archive unavailable", Content = exception.Message, CloseButtonText = "Close" };
        await dialog.ShowAsync();
    }
}
