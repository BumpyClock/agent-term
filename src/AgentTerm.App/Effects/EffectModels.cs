using AgentTerm.Terminal;

namespace AgentTerm_App.Effects;

internal enum TaskbarProgressKind { None, Indeterminate, Normal, Error, Paused }

/// <summary>What a window's taskbar button shows. <see cref="Percent"/> is 0 to 100 and meaningful unless the kind is None or Indeterminate.</summary>
internal readonly record struct TaskbarProgress(TaskbarProgressKind Kind, int Percent)
{
    internal static TaskbarProgress None => new(TaskbarProgressKind.None, 0);
}

internal enum NoticeSeverity { Info, Warning, Error }

internal enum NoticeAction
{
    /// <summary>Close the notice. For a held clipboard write this denies it.</summary>
    Dismiss,
    /// <summary>Complete the clipboard write the notice asks about.</summary>
    AllowWrite,
    /// <summary>Bring the pane forward.</summary>
    Show,
}

/// <summary>
/// A card shown inside the pane that raised it. A notice with the same <see cref="Key"/> replaces the earlier one and counts a repeat.
/// <see cref="ExpiresAt"/> is a clock value in milliseconds, or zero when the notice stays until the user acts.
/// </summary>
internal sealed record Notice(long Id, string Key, NoticeSeverity Severity, string Title, string Message, int Repeats, long ExpiresAt,
    IReadOnlyList<NoticeAction> Actions, long RequestId = 0, bool FromProgram = false);

/// <summary>A Windows notification request. <see cref="Id"/> identifies it when the user activates it.</summary>
internal sealed record SystemNotification(long Id, string Title, string Body, nint Window);

/// <summary>Process-wide Windows services. A fake stands in for tests.</summary>
internal interface IEffectHost
{
    /// <summary>True when the window is the foreground window and not minimized.</summary>
    bool IsActive(nint window);
    void PlayBell();
    void FlashWindow(nint window);
    void SetTaskbarProgress(nint window, TaskbarProgress progress);
    bool SystemNotificationsAvailable { get; }
    /// <summary>Returns false when Windows refused or the system cannot show it.</summary>
    bool ShowSystemNotification(SystemNotification notification);
    void RemoveSystemNotification(long id);
    /// <summary>Raised on the UI thread when the user clicks a Windows notification this host showed.</summary>
    event Action<long>? SystemNotificationActivated;
}

/// <summary>The pane's own UI. Methods run on the UI thread.</summary>
internal interface IPaneEffectView
{
    nint Window { get; }
    /// <summary>A short name for notifications from this pane, such as the program or shell.</summary>
    string Name { get; }
    /// <summary>True when the user can see this pane right now.</summary>
    bool IsVisible { get; }
    void ShowBell();
    /// <summary>Shows or clears the pane's progress indicator. A percent of -1 keeps the current one.</summary>
    void ShowProgress(TerminalProgressState state, int percent);
    void RequestWindowResize(int rows, int columns);
    /// <summary>Selects this pane's tab and brings its window forward.</summary>
    void Reveal();
}
