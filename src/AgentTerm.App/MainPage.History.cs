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
        // An empty query frees the native search, so the next frame captures no highlights.
        try { await session.FindAsync("", true, cancellationToken: cancellation.Token); if (_session == session) RefreshTerminalFrame(session); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { ShowInteractionFailure(error); }
    }
    // Automatic refresh starts at most this often. A finished search otherwise restarts on every output chunk and
    // competes with the shell's own output for the whole stream. The last change is always picked up by a trailing refresh.
    private const int FindRefreshIntervalMs = 250;
    private long _findRefreshedAt;
    private bool _findRefreshQueued;
    private async void RefreshFindHistory(TerminalSession session)
    {
        if (FindBar.Visibility != Visibility.Visible || !IsLoaded || !_viewActive ||
            FindBox.Text.Length == 0 || _findInFlight || session.HistoryVersion == _findHistoryVersion) return;
        var wait = FindRefreshIntervalMs - (Environment.TickCount64 - _findRefreshedAt);
        if (wait > 0)
        {
            if (_findRefreshQueued) return;
            _findRefreshQueued = true;
            await Task.Delay((int)wait);
            _findRefreshQueued = false;
            if (_session == session) RefreshFindHistory(session);
            return;
        }
        _findRefreshedAt = Environment.TickCount64;
        FindMatch(true, retainSelection: true);
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
            FindStatus.Text = FindBox.Text.Length == 0 ? "" : result.Matches == 0 ? "No matches"
                : result.SelectedOrdinal is { } ordinal ? $"{ordinal} of {result.Matches}" : $"{result.Matches} matches";
            RefreshOutput(session);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) ShowInteractionFailure(error); }
        finally { if (_findCancellation == cancellation) _findInFlight = false; }
    }
    private void PreviousPrompt_Click(object sender, RoutedEventArgs e) => MovePrompt(-1);
    private void NextPrompt_Click(object sender, RoutedEventArgs e) => MovePrompt(1);
    private async void MovePrompt(int delta)
    {
        try { if (_session is { } session) { await session.DispatchAsync(owner => owner.NavigatePrompt(delta)); if (_session == session) RefreshOutput(session); } }
        catch (Exception error) { ShowInteractionFailure(error); }
    }
    private async void CopyRich_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_session is not { } session || await session.CopySelectionRichAsync() is not { } copy) return;
            var data = new DataPackage();
            data.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(copy.Html));
            data.SetText(copy.Text);
            Clipboard.SetContent(data);
        }
        catch (ObjectDisposedException) { }
        catch (Exception error) { ShowInteractionFailure(error); }
    }

    private void ExportHtml_Click(object sender, RoutedEventArgs e) => ExportHistoryFile(TerminalExportFormat.Html, false);
    private void ExportVt_Click(object sender, RoutedEventArgs e) => ExportHistoryFile(TerminalExportFormat.StyledVt, false);
    private void ExportSelectionHtml_Click(object sender, RoutedEventArgs e) => ExportHistoryFile(TerminalExportFormat.Html, true);
    private void ExportSelectionVt_Click(object sender, RoutedEventArgs e) => ExportHistoryFile(TerminalExportFormat.StyledVt, true);
    private async void ExportHistoryFile(TerminalExportFormat format, bool selectionOnly)
    {
        using var cancellation = new CancellationTokenSource();
        Task<TerminalHistoryExport?>? prepared = null;
        try
        {
            if (_session is not { } session) return;
            if (selectionOnly && _presentedFrame?.HasSelection != true) return;
            prepared = session.ExportHistoryAsync(format, selectionOnly, cancellation.Token);
            if (selectionOnly && await prepared is null) return;
            var picker = new FileSavePicker { SuggestedFileName = selectionOnly ? "terminal-selection" : "terminal-history" };
            picker.FileTypeChoices.Add(format == TerminalExportFormat.Html ? "HTML" : "Styled VT", new[] { format == TerminalExportFormat.Html ? ".html" : ".ansi" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, GetActiveWindow());
            var file = await picker.PickSaveFileAsync();
            if (file is null) { cancellation.Cancel(); return; }
            var export = await prepared;
            if (export is not null) await Windows.Storage.FileIO.WriteBytesAsync(file, export.Utf8);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { ShowInteractionFailure(error); }
        finally
        {
            cancellation.Cancel();
            // A picker failure or cancellation must still observe the prepared task. An in-flight native format
            // cannot be interrupted; its owned bytes are discarded when it returns.
            if (prepared is not null)
                try { await prepared; } catch (Exception) { }
        }
    }
}
