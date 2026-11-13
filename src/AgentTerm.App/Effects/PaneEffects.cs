using System.Globalization;
using AgentTerm.Terminal;

namespace AgentTerm_App.Effects;

/// <summary>
/// Turns the effects a pane's program requested into what the user sees: pane flash, sound, taskbar flash and progress, cards in the
/// pane, and Windows notifications. It owns no UI. The view and host are injected so every rule here runs headless.
/// UI thread only. Dispose when the pane closes: it clears the pane's progress and cards and withdraws its Windows notifications.
/// </summary>
internal sealed class PaneEffects : IDisposable
{
    internal static readonly TimeSpan BellInterval = TimeSpan.FromMilliseconds(300);
    internal static readonly TimeSpan SystemNotificationInterval = TimeSpan.FromMilliseconds(1500);
    internal static readonly TimeSpan InfoLifetime = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan WarningLifetime = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan NotificationLifetime = TimeSpan.FromSeconds(15);

    private static long _nextSystemId;

    private readonly IEffectHost _host;
    private readonly IPaneEffectView _view;
    private readonly ProgressAggregator _progress;
    private readonly Func<TerminalEffectOptions> _options;
    private readonly Func<long, bool, ClipboardOutcome> _resolveWrite;
    private readonly Func<long> _clock;
    private readonly Dictionary<long, string> _systemNotices = [];
    private long _lastBell = long.MinValue / 2;
    private long _lastSystem = long.MinValue / 2;
    private bool _attention;
    private bool _disposed;
    private (TerminalProgressState State, int Percent)? _current;

    internal PaneEffects(IEffectHost host, IPaneEffectView view, ProgressAggregator progress, Func<TerminalEffectOptions> options,
        Func<long, bool, ClipboardOutcome> resolveWrite, Func<long>? clock = null)
    {
        _host = host;
        _view = view;
        _progress = progress;
        _options = options;
        _resolveWrite = resolveWrite;
        _clock = clock ?? (() => Environment.TickCount64);
        _host.SystemNotificationActivated += SystemNotificationActivated;
        Notices.Removed += notice =>
        {
            if (notice.RequestId != 0) _resolveWrite(notice.RequestId, false);
            foreach (var id in _systemNotices.Where(pair => pair.Value == notice.Key).Select(pair => pair.Key).ToArray())
            {
                _systemNotices.Remove(id);
                Safe(() => _host.RemoveSystemNotification(id));
            }
        };
    }

    internal NoticeCenter Notices { get; } = new();

    /// <summary>A short marker for the tab: the pane's progress, or a dot when something asked for attention while unseen. Empty otherwise.</summary>
    internal string Badge => _current is { } progress
        ? progress.State switch
        {
            TerminalProgressState.Set => progress.Percent.ToString(CultureInfo.InvariantCulture) + "%",
            TerminalProgressState.Error => "failed",
            TerminalProgressState.Pause => "paused",
            _ => "working",
        }
        : _attention ? "●" : "";

    internal bool HasProgress => _current is not null;

    internal void Apply(IReadOnlyList<TerminalEffect> effects)
    {
        if (_disposed) return;
        var options = _options();
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case ResetEffect: Reset(); break;
                case BellEffect: Bell(options); break;
                case NotificationEffect notification: Notify(options, notification); break;
                case ProgressEffect progress: ApplyProgress(options, progress); break;
                case ClipboardEffect clipboard: Clipboard(options, clipboard); break;
                case ResizeRequestEffect resize when options.AllowWindowResize:
                    Safe(() => _view.RequestWindowResize(resize.Rows, resize.Columns));
                    break;
            }
        }
    }

    /// <summary>The user is looking at this pane in the active window. Clears the attention marker and starts card timers.</summary>
    internal void Seen()
    {
        if (_disposed) return;
        _attention = false;
        Notices.SetExpiry(notice => notice.ExpiresAt == 0 && !notice.Actions.Contains(NoticeAction.AllowWrite), _clock() + (long)NotificationLifetime.TotalMilliseconds);
        foreach (var id in _systemNotices.Keys) Safe(() => _host.RemoveSystemNotification(id));
        _systemNotices.Clear();
    }

    /// <summary>The shell exited or the pane is closing. A program that is gone cannot clear its own progress.</summary>
    internal void Reset()
    {
        _current = null;
        _progress.Remove(this);
        Safe(() => _view.ShowProgress(TerminalProgressState.Remove, 0));
        Notices.Clear();
        _attention = false;
        foreach (var id in _systemNotices.Keys) Safe(() => _host.RemoveSystemNotification(id));
        _systemNotices.Clear();
    }

    /// <summary>Runs a card's action. Returns false when the card is already gone.</summary>
    internal bool Invoke(long noticeId, NoticeAction action)
    {
        if (Notices.Find(noticeId) is not { } notice) return false;
        switch (action)
        {
            case NoticeAction.AllowWrite when notice.RequestId != 0:
                var outcome = _resolveWrite(notice.RequestId, true);
                Notices.Dismiss(noticeId, cancelDecision: false);
                if (outcome == ClipboardOutcome.Success)
                    ShowCard("clip-wrote", NoticeSeverity.Info, "Clipboard updated", "Copied the text.", Lifetime(InfoLifetime));
                else
                    ShowCard("clip-failed", NoticeSeverity.Warning, "Clipboard not updated",
                        outcome == ClipboardOutcome.Unsupported ? "That request is no longer available." : "Windows did not accept the copy.",
                        Lifetime(WarningLifetime));
                break;
            case NoticeAction.Dismiss:
                Notices.Dismiss(noticeId);
                break;
            case NoticeAction.Show:
                Notices.Dismiss(noticeId);
                Safe(_view.Reveal);
                break;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Reset();
        _disposed = true;
        _host.SystemNotificationActivated -= SystemNotificationActivated;
    }

    private void Bell(TerminalEffectOptions options)
    {
        if (options.Bell == BellStyle.None) return;
        var now = _clock();
        if (now - _lastBell < (long)BellInterval.TotalMilliseconds) return;
        _lastBell = now;
        if ((options.Bell & BellStyle.Audible) != 0) Safe(_host.PlayBell);
        if ((options.Bell & BellStyle.Visual) != 0)
        {
            Safe(_view.ShowBell);
            if (!_host.IsActive(_view.Window)) Safe(() => _host.FlashWindow(_view.Window));
        }
        MarkUnseen();
    }

    private void Notify(TerminalEffectOptions options, NotificationEffect notification)
    {
        if (options.Notifications == NotificationStyle.Off) return;
        var title = notification.Title.Length != 0 ? notification.Title : _view.Name;
        var unseen = !(_view.IsVisible && _host.IsActive(_view.Window));
        var key = "note:" + notification.Title + "\n" + notification.Body;
        var stored = Notices.Show(key, NoticeSeverity.Info, title, notification.Body, unseen ? 0 : Lifetime(NotificationLifetime),
            [NoticeAction.Dismiss], fromProgram: true);
        // Repeats merged into the program's request add to the card's count.
        if (notification.Repeats > 1) Notices.AddRepeats(stored.Id, notification.Repeats - 1);
        if (!unseen) return;
        MarkUnseen();
        if (!_host.IsActive(_view.Window)) Safe(() => _host.FlashWindow(_view.Window));
        var now = _clock();
        if (_host.IsActive(_view.Window) || options.Notifications != NotificationStyle.InAppAndSystem || !_host.SystemNotificationsAvailable ||
            now - _lastSystem < (long)SystemNotificationInterval.TotalMilliseconds) return;
        _lastSystem = now;
        if (Notices.Find(stored.Id) is null) return;
        foreach (var previous in _systemNotices.Where(pair => pair.Value == key).Select(pair => pair.Key).ToArray())
        {
            _systemNotices.Remove(previous);
            Safe(() => _host.RemoveSystemNotification(previous));
        }
        var id = Interlocked.Increment(ref _nextSystemId);
        var shown = false;
        Safe(() => shown = _host.ShowSystemNotification(new SystemNotification(id, title, notification.Body, _view.Window)));
        if (shown) _systemNotices[id] = key;
    }

    private void ApplyProgress(TerminalEffectOptions options, ProgressEffect effect)
    {
        if (!options.Progress && effect.State != TerminalProgressState.Remove) return;
        if (effect.State == TerminalProgressState.Remove)
        {
            _current = null;
        }
        else
        {
            _current = (effect.State, effect.Percent);
        }
        _progress.Update(this, _view.Window, _current?.State, _current?.Percent ?? 0);
        Safe(() => _view.ShowProgress(_current?.State ?? TerminalProgressState.Remove, _current?.Percent ?? 0));
    }

    private void Clipboard(TerminalEffectOptions options, ClipboardEffect effect)
    {
        var who = effect.Program.Length != 0 ? effect.Program : "A program";
        var count = effect.Length.ToString("N0", CultureInfo.CurrentCulture);
        switch (effect.Kind)
        {
            case ClipboardNoticeKind.Wrote:
                ShowCard("clip-wrote", NoticeSeverity.Info, "Clipboard updated", $"{who} copied {count} characters.", Lifetime(InfoLifetime), repeats: effect.Repeats);
                break;
            case ClipboardNoticeKind.Cleared:
                ShowCard("clip-cleared", NoticeSeverity.Info, "Clipboard cleared", $"{who} cleared the clipboard.", Lifetime(InfoLifetime), repeats: effect.Repeats);
                break;
            case ClipboardNoticeKind.WriteBlocked:
                ShowCard("clip-write-blocked", NoticeSeverity.Warning, "Clipboard write blocked",
                    $"{who} tried to copy to the clipboard. Your settings block this.", Lifetime(WarningLifetime), repeats: effect.Repeats);
                break;
            case ClipboardNoticeKind.WriteTooLarge:
                ShowCard("clip-write-large", NoticeSeverity.Warning, "Clipboard write too large",
                    $"{who} tried to copy {FormatBytes(effect.Length)}. The limit is {FormatBytes(options.ClipboardMaxBytes)}.", Lifetime(WarningLifetime), repeats: effect.Repeats);
                break;
            case ClipboardNoticeKind.WriteFailed:
                ShowCard("clip-failed", NoticeSeverity.Warning, "Clipboard not updated", "Windows did not accept the copy.", Lifetime(WarningLifetime), repeats: effect.Repeats);
                break;
            case ClipboardNoticeKind.WriteRequested:
                var preview = effect.Preview.Length == 0 ? "" : $" “{effect.Preview}”";
                ShowCard("clip-ask:" + effect.RequestId.ToString(CultureInfo.InvariantCulture), NoticeSeverity.Warning, "Copy to clipboard?",
                    $"{who} wants to copy {count} characters.{preview}", 0, [NoticeAction.AllowWrite, NoticeAction.Dismiss], effect.RequestId);
                MarkUnseen();
                break;
            case ClipboardNoticeKind.ReadServed:
                ShowCard("clip-read", NoticeSeverity.Warning, "Clipboard read", $"{who} read {count} characters from your clipboard.", Lifetime(WarningLifetime), repeats: effect.Repeats);
                break;
            case ClipboardNoticeKind.ReadBlocked:
                ShowCard("clip-read-blocked", NoticeSeverity.Info, "Clipboard read blocked",
                    $"{who} asked to read your clipboard. Reads are off in Settings.", Lifetime(WarningLifetime), repeats: effect.Repeats);
                break;
            case ClipboardNoticeKind.ReadFailed:
                ShowCard("clip-read-failed", NoticeSeverity.Info, "Clipboard read failed",
                    $"{who} asked to read your clipboard, but it could not be read or is over the size limit.", Lifetime(WarningLifetime), repeats: effect.Repeats);
                break;
        }
    }

    private void ShowCard(string key, NoticeSeverity severity, string title, string message, long expiresAt,
        IReadOnlyList<NoticeAction>? actions = null, long requestId = 0, int repeats = 1)
    {
        var card = Notices.Show(key, severity, title, message, expiresAt, actions, requestId, fromProgram: true);
        if (repeats > 1) Notices.AddRepeats(card.Id, repeats - 1);
    }

    private long Lifetime(TimeSpan span) => _clock() + (long)span.TotalMilliseconds;

    private void MarkUnseen()
    {
        if (!(_view.IsVisible && _host.IsActive(_view.Window))) _attention = true;
    }

    private void SystemNotificationActivated(long id)
    {
        if (!_systemNotices.TryGetValue(id, out var key)) return;
        _systemNotices.Remove(id);
        Safe(() => _host.RemoveSystemNotification(id));
        var notice = Notices.Items.FirstOrDefault(item => item.Key == key);
        if (notice is not null) Notices.Dismiss(notice.Id);
        _attention = false;
        Safe(_view.Reveal);
    }

    private static string FormatBytes(int bytes) => bytes >= 1024 * 1024
        ? (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.CurrentCulture) + " MiB"
        : bytes >= 1024 ? (bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " KiB"
        : bytes.ToString(CultureInfo.CurrentCulture) + " bytes";

    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception)
        {
            // A failing host must not take the pane down. The in-app card is the record of the request.
        }
    }
}
