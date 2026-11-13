using AgentTerm.Terminal;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace AgentTerm_App;

public sealed partial class MainPage
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetActiveWindow();

    private CancellationTokenSource? _findCancellation;
    private bool _findInFlight;
    private long _findHistoryVersion = -1;

    private void CancelFind()
    {
        _findCancellation?.Cancel();
        _findCancellation?.Dispose();
        _findCancellation = null;
        _findInFlight = false;
        _findHistoryVersion = -1;
    }
    private async void ReleaseFind()
    {
        if (_session is not { } session) return;
        var cancellation = _findCancellation = new CancellationTokenSource();
        try { await session.FindAsync("", true, cancellationToken: cancellation.Token); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { ShowInteractionFailure(error); }
    }
    private void RefreshFindHistory(TerminalSession session)
    {
        if (FindBar.Visibility == Visibility.Visible && IsLoaded && _viewActive &&
            FindBox.Text.Length != 0 && !_findInFlight && session.HistoryVersion != _findHistoryVersion) FindMatch(true, retainSelection: true);
    }
    private async void FindMatch(bool forward, bool retainSelection = false)
    {
        if (_session is not { } session || FindBox is null || FindStatus is null || FindMatchCase is null) return;
        CancelFind();
        var cancellation = _findCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _findHistoryVersion = session.HistoryVersion;
        _findInFlight = true;
        try
        {
            var progress = new Progress<TerminalSearchProgress>(value =>
            {
                if (token.IsCancellationRequested || _session != session) return;
                FindStatus.Text = value.Complete ? $"{value.Matches} matches" : $"Searching… {value.Matches} matches";
                if (value.Found && !value.Complete) RefreshTerminalFrame(session);
            });
            var result = await session.FindAsync(FindBox.Text, forward, FindMatchCase.IsChecked == true, progress, token, retainSelection);
            if (token.IsCancellationRequested || _session != session) return;
            _findHistoryVersion = result.HistoryVersion;
            _findInFlight = false;
            FindStatus.Text = FindBox.Text.Length == 0 ? "" : result.Matches == 0 ? "No matches" : $"{result.Matches} matches";
            RefreshOutput(session);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) ShowInteractionFailure(error); }
        finally { if (_findCancellation == cancellation) _findInFlight = false; }
    }
    private void PreviousPrompt_Click(object sender, RoutedEventArgs e) => MovePrompt(-1);
    private void NextPrompt_Click(object sender, RoutedEventArgs e) => MovePrompt(1);
    private void MovePrompt(int delta)
    {
        try { if (_session is { } session) { session.NavigatePrompt(delta); RefreshOutput(session); } }
        catch (Exception error) { ShowInteractionFailure(error); }
    }
    private void CopyRich_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_session is not { } session || session.ExportHistory(TerminalExportFormat.Html, true) is not { } html) return;
            var data = new DataPackage();
            data.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(html));
            data.SetText(session.ReadSelectionText() ?? "");
            Clipboard.SetContent(data);
        }
        catch (Exception error) { ShowInteractionFailure(error); }
    }
    private void ExportHtml_Click(object sender, RoutedEventArgs e) => ExportHistoryFile(TerminalExportFormat.Html, false);
    private void ExportVt_Click(object sender, RoutedEventArgs e) => ExportHistoryFile(TerminalExportFormat.StyledVt, false);
    private void ExportSelectionHtml_Click(object sender, RoutedEventArgs e) => ExportHistoryFile(TerminalExportFormat.Html, true);
    private void ExportSelectionVt_Click(object sender, RoutedEventArgs e) => ExportHistoryFile(TerminalExportFormat.StyledVt, true);
    private async void ExportHistoryFile(TerminalExportFormat format, bool selectionOnly)
    {
        try
        {
            if (_session is not { } session || session.ExportHistory(format, selectionOnly) is not { } text) return;
            var picker = new FileSavePicker { SuggestedFileName = selectionOnly ? "terminal-selection" : "terminal-history" };
            picker.FileTypeChoices.Add(format == TerminalExportFormat.Html ? "HTML" : "Styled VT", new[] { format == TerminalExportFormat.Html ? ".html" : ".ansi" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, GetActiveWindow());
            var file = await picker.PickSaveFileAsync();
            if (file is not null) await Windows.Storage.FileIO.WriteTextAsync(file, text);
        }
        catch (Exception error) { ShowInteractionFailure(error); }
    }
}
