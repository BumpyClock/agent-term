using AgentTerm.Terminal;
using AgentTerm_App;
using AgentTerm_App.Effects;

/// <summary>Headless checks for how the host presents requested effects: bell, notifications, progress, cards, and settings.</summary>
internal static class EffectsHostChecks
{
    private sealed class FakeHost : IEffectHost
    {
        public bool Active, Available = true, ShowResult = true;
        public int Beeps, Flashes;
        public readonly List<SystemNotification> Toasts = [];
        public readonly List<long> Removed = [];
        public readonly List<(nint Window, TaskbarProgress Progress)> Taskbar = [];
        public event Action<long>? SystemNotificationActivated;
        public bool IsActive(nint window) => Active;
        public void PlayBell() => Beeps++;
        public void FlashWindow(nint window) => Flashes++;
        public void SetTaskbarProgress(nint window, TaskbarProgress progress) => Taskbar.Add((window, progress));
        public bool SystemNotificationsAvailable => Available;
        public bool ShowSystemNotification(SystemNotification notification) { Toasts.Add(notification); return ShowResult; }
        public void RemoveSystemNotification(long id) => Removed.Add(id);
        public void Activate(long id) => SystemNotificationActivated?.Invoke(id);
    }

    private sealed class FakeView(nint window = 7) : IPaneEffectView
    {
        public bool Visible = true;
        public int Bells, Reveals;
        public readonly List<(TerminalProgressState State, int Percent)> Progress = [];
        public readonly List<(int Rows, int Columns)> Resizes = [];
        public nint Window => window;
        public string Name => "pwsh";
        public bool IsVisible => Visible;
        public void ShowBell() => Bells++;
        public void ShowProgress(TerminalProgressState state, int percent) => Progress.Add((state, percent));
        public void RequestWindowResize(int rows, int columns) => Resizes.Add((rows, columns));
        public void Reveal() => Reveals++;
    }

    private sealed class Rig
    {
        public readonly FakeHost Host = new();
        public readonly FakeView View;
        public readonly ProgressAggregator Aggregator;
        public readonly List<(long Id, bool Allow)> Writes = [];
        public ClipboardOutcome WriteOutcome = ClipboardOutcome.Success;
        public TerminalEffectOptions Options = TerminalEffectOptions.Default;
        public long Now = 1_000_000;
        public readonly PaneEffects Effects;

        public Rig(ProgressAggregator? shared = null, FakeView? view = null)
        {
            View = view ?? new FakeView();
            Aggregator = shared ?? new ProgressAggregator(Host);
            Effects = new PaneEffects(Host, View, Aggregator, () => Options, (id, allow) => { Writes.Add((id, allow)); return WriteOutcome; }, () => Now);
        }
    }

    public static void Run()
    {
        var inbox = new EffectInbox();
        for (var request = 1; request <= 1000; request++)
            inbox.Add([new ClipboardEffect(ClipboardNoticeKind.WriteRequested, 1, "test", request)]);
        Require(inbox.Take().Count <= EffectInbox.Capacity, "Undelivered clipboard approvals must stay bounded across batches.");
        inbox.Add([new NotificationEffect("before", "reset"), new ResetEffect(), new NotificationEffect("after", "reset")]);
        Require(inbox.Take() is [ResetEffect, NotificationEffect { Title: "after" }], "A reset must remove earlier queued notices and preserve later ones.");

        CheckBell();
        CheckNotifications();
        CheckNotificationActivation();
        CheckProgress();
        CheckClipboardCards();
        CheckResizeGate();
        CheckNoticeCenter();
        CheckPreferences();
        Console.WriteLine("Effects host checks passed bell, notification lifecycle, progress aggregation, clipboard cards, resize gating, and preferences.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void CheckBell()
    {
        var rig = new Rig { Options = new TerminalEffectOptions { Bell = BellStyle.Both } };
        rig.Host.Active = true;
        rig.Effects.Apply([new BellEffect(1)]);
        Require(rig.Host.Beeps == 1 && rig.View.Bells == 1 && rig.Host.Flashes == 0, "A bell in the active window should beep and flash the pane, not the taskbar.");
        rig.Effects.Apply([new BellEffect(5)]);
        Require(rig.Host.Beeps == 1 && rig.View.Bells == 1, "Bells inside the throttle interval should be absorbed.");
        rig.Now += 400;
        rig.Host.Active = false;
        rig.Effects.Apply([new BellEffect(1)]);
        Require(rig.Host.Beeps == 2 && rig.View.Bells == 2 && rig.Host.Flashes == 1, "A bell in a background window should also flash the taskbar button.");
        Require(rig.Effects.Badge == "●", "A bell the user did not see should mark the tab.");
        rig.Host.Active = true;
        rig.Effects.Seen();
        Require(rig.Effects.Badge == "", "Looking at the pane should clear the marker.");

        rig.Options = new TerminalEffectOptions { Bell = BellStyle.None };
        rig.Now += 400;
        rig.Effects.Apply([new BellEffect(1)]);
        Require(rig.Host.Beeps == 2 && rig.View.Bells == 2, "Turning the bell off should silence it immediately.");

        rig.Options = new TerminalEffectOptions { Bell = BellStyle.Audible };
        rig.Effects.Apply([new BellEffect(1)]);
        Require(rig.Host.Beeps == 3 && rig.View.Bells == 2, "An audible-only bell should not flash.");
        rig.Options = new TerminalEffectOptions { Bell = BellStyle.Visual };
        rig.Now += 400;
        rig.Effects.Apply([new BellEffect(1)]);
        Require(rig.Host.Beeps == 3 && rig.View.Bells == 3, "A visual-only bell should not beep.");
    }

    private static void CheckNotifications()
    {
        var rig = new Rig();
        rig.Host.Active = true;
        rig.Effects.Apply([new NotificationEffect("Build", "Done", 3)]);
        var card = rig.Effects.Notices.Items.Single();
        Require(card.Title == "Build" && card.Message == "Done" && card.Repeats == 3 && card.ExpiresAt != 0, "A notification in the pane the user watches should show a card that expires.");
        Require(rig.Host.Toasts.Count == 0, "No Windows notification when the user is looking at the pane.");

        rig.Host.Active = false;
        rig.Effects.Apply([new NotificationEffect("", "Tests failed", 1)]);
        Require(rig.Effects.Notices.Items.Last().Title == "pwsh" && rig.Effects.Notices.Items.Last().ExpiresAt == 0, "An unseen notification should name its pane and wait for the user.");
        Require(rig.Host.Toasts.Count == 1 && rig.Host.Flashes == 1 && rig.Effects.Badge == "●", "A background notification should raise a Windows notification, flash the taskbar, and mark the tab.");

        rig.Effects.Apply([new NotificationEffect("", "Another", 1)]);
        Require(rig.Host.Toasts.Count == 1, "Windows notifications from one pane should be rate limited.");
        rig.Now += 2000;
        rig.Effects.Apply([new NotificationEffect("", "Third", 1)]);
        Require(rig.Host.Toasts.Count == 2, "The rate limit should lift after the interval.");

        rig.Options = new TerminalEffectOptions { Notifications = NotificationStyle.InApp };
        rig.Now += 2000;
        var before = rig.Host.Toasts.Count;
        rig.Effects.Apply([new NotificationEffect("", "In app only", 1)]);
        Require(rig.Host.Toasts.Count == before, "In-app mode should never raise a Windows notification.");

        rig.Options = new TerminalEffectOptions { Notifications = NotificationStyle.Off };
        var cards = rig.Effects.Notices.Items.Count;
        rig.Effects.Apply([new NotificationEffect("", "Off", 1)]);
        Require(rig.Effects.Notices.Items.Count == cards, "Turning notifications off should ignore requests.");

        rig.Options = TerminalEffectOptions.Default;
        rig.Host.Available = false;
        rig.Now += 2000;
        before = rig.Host.Toasts.Count;
        rig.Effects.Apply([new NotificationEffect("", "No system", 1)]);
        Require(rig.Host.Toasts.Count == before && rig.Effects.Notices.Items.Any(item => item.Message == "No system"),
            "Without system support the in-app card is the notification.");

        // Seeing the pane starts the clock on cards that waited and withdraws Windows notifications.
        rig.Host.Active = true;
        rig.Effects.Seen();
        Require(rig.Effects.Notices.Items.All(item => item.ExpiresAt != 0) && rig.Host.Removed.Count == 2 && rig.Effects.Badge == "",
            "Seeing the pane should retire its Windows notifications and let its cards expire.");

        var shared = new Rig();
        shared.Host.Active = false;
        shared.Host.ShowResult = false;
        shared.Effects.Apply([new NotificationEffect("", "refused", 1)]);
        shared.Host.Activate(shared.Host.Toasts.Single().Id);
        Require(shared.View.Reveals == 0, "A refused Windows notification should not leave an activation route.");
    }

    private static void CheckNotificationActivation()
    {
        var rig = new Rig();
        rig.Host.Active = false;
        rig.View.Visible = false;
        rig.Effects.Apply([new NotificationEffect("Agent", "Needs input", 1)]);
        var toast = rig.Host.Toasts.Single();
        Require(toast.Title == "Agent" && toast.Body == "Needs input" && toast.Window == 7, "The Windows notification should carry the title, body, and window.");
        rig.Host.Activate(toast.Id + 100);
        Require(rig.View.Reveals == 0, "An unrelated notification id must not activate this pane.");
        rig.Host.Activate(toast.Id);
        Require(rig.View.Reveals == 1 && rig.Effects.Notices.Items.Count == 0 && rig.Effects.Badge == "", "Activating the notification should reveal the pane and retire its card.");
        rig.Host.Activate(toast.Id);
        Require(rig.View.Reveals == 1, "Activation is single use.");

        rig.Now += 5000;
        rig.Effects.Apply([new NotificationEffect("Agent", "Second", 1)]);
        var second = rig.Host.Toasts.Last();
        rig.Effects.Dispose();
        Require(rig.Host.Removed.Contains(second.Id), "Closing the pane should withdraw its Windows notifications.");
        rig.Host.Activate(second.Id);
        Require(rig.View.Reveals == 1, "A closed pane must not react to activation.");
        rig.Effects.Apply([new BellEffect(1)]);
        Require(rig.View.Bells == 0, "A closed pane ignores later effects.");
    }

    private static void CheckProgress()
    {
        var rig = new Rig();
        rig.Effects.Apply([new ProgressEffect(TerminalProgressState.Set, 40)]);
        Require(rig.View.Progress.Last() == (TerminalProgressState.Set, 40) && rig.Aggregator.Shown(7) == new TaskbarProgress(TaskbarProgressKind.Normal, 40) &&
            rig.Effects.Badge == "40%", "Progress should show on the pane, the taskbar, and the tab.");
        var calls = rig.Host.Taskbar.Count;
        rig.Effects.Apply([new ProgressEffect(TerminalProgressState.Set, 40)]);
        Require(rig.Host.Taskbar.Count == calls, "Repeating the same taskbar state should not call Windows again.");

        rig.Effects.Apply([new ProgressEffect(TerminalProgressState.Error, -1)]);
        Require(rig.Aggregator.Shown(7) == new TaskbarProgress(TaskbarProgressKind.Error, 40) && rig.Effects.Badge == "failed",
            "An error without a value should keep the previous percentage.");
        rig.Effects.Apply([new ProgressEffect(TerminalProgressState.Pause, 55)]);
        Require(rig.Aggregator.Shown(7) == new TaskbarProgress(TaskbarProgressKind.Paused, 55) && rig.Effects.Badge == "paused", "Pause should show paused.");
        rig.Effects.Apply([new ProgressEffect(TerminalProgressState.Indeterminate, 0)]);
        Require(rig.Aggregator.Shown(7).Kind == TaskbarProgressKind.Indeterminate && rig.Effects.Badge == "working", "Indeterminate should show working.");
        rig.Effects.Apply([new ProgressEffect(TerminalProgressState.Remove, 0)]);
        Require(rig.Aggregator.Shown(7).Kind == TaskbarProgressKind.None && rig.Effects.Badge == "" && rig.View.Progress.Last().State == TerminalProgressState.Remove,
            "Remove should clear every indicator.");

        // Two panes in one window share the taskbar button. Another window keeps its own.
        var first = new Rig();
        var second = new Rig(first.Aggregator, new FakeView(7));
        var other = new Rig(first.Aggregator, new FakeView(9));
        first.Effects.Apply([new ProgressEffect(TerminalProgressState.Set, 20)]);
        second.Effects.Apply([new ProgressEffect(TerminalProgressState.Set, 80)]);
        other.Effects.Apply([new ProgressEffect(TerminalProgressState.Indeterminate, 0)]);
        Require(first.Aggregator.Shown(7) == new TaskbarProgress(TaskbarProgressKind.Normal, 80), "The most recent determinate report should win in a window.");
        Require(first.Aggregator.Shown(9).Kind == TaskbarProgressKind.Indeterminate, "Another window should keep its own taskbar state.");
        first.Effects.Apply([new ProgressEffect(TerminalProgressState.Error, 20)]);
        Require(first.Aggregator.Shown(7).Kind == TaskbarProgressKind.Error, "An error should outrank progress in other panes.");
        first.Effects.Apply([new ProgressEffect(TerminalProgressState.Remove, 0)]);
        Require(first.Aggregator.Shown(7) == new TaskbarProgress(TaskbarProgressKind.Normal, 80), "Clearing the error should fall back to the other pane.");
        second.Effects.Dispose();
        Require(first.Aggregator.Shown(7).Kind == TaskbarProgressKind.None, "Closing the last pane with progress should clear the taskbar.");

        var off = new Rig { Options = new TerminalEffectOptions { Progress = false } };
        off.Effects.Apply([new ProgressEffect(TerminalProgressState.Set, 10)]);
        Require(off.Aggregator.Shown(7).Kind == TaskbarProgressKind.None && off.Effects.Badge == "", "Disabled progress should show nothing.");

        var exit = new Rig();
        exit.Effects.Apply([new ProgressEffect(TerminalProgressState.Set, 10)]);
        exit.Effects.Reset();
        Require(exit.Aggregator.Shown(7).Kind == TaskbarProgressKind.None && !exit.Effects.HasProgress, "A shell exit should clear progress its program can no longer remove.");
    }

    private static void CheckClipboardCards()
    {
        var rig = new Rig();
        rig.Host.Active = true;
        rig.Effects.Apply([new ClipboardEffect(ClipboardNoticeKind.Wrote, 1234, "")]);
        var wrote = rig.Effects.Notices.Items.Single();
        Require(wrote.Title == "Clipboard updated" && wrote.Message.Contains("1,234", StringComparison.Ordinal) || wrote.Message.Contains("1234", StringComparison.Ordinal),
            $"A write should be disclosed with its size, got <{wrote.Message}>.");
        rig.Effects.Apply([new ClipboardEffect(ClipboardNoticeKind.Wrote, 5, "", Repeats: 3)]);
        Require(rig.Effects.Notices.Items.Single().Repeats == 4, "Repeated writes should update one card with a count.");

        rig.Effects.Apply([new ClipboardEffect(ClipboardNoticeKind.WriteRequested, 9, "nvim", 42, "hello world")]);
        var ask = rig.Effects.Notices.Items.Last();
        Require(ask.RequestId == 42 && ask.ExpiresAt == 0 && ask.Actions.Contains(NoticeAction.AllowWrite) && ask.Message.Contains("hello world", StringComparison.Ordinal) &&
            ask.Message.Contains("nvim", StringComparison.Ordinal), "A held write should wait for a decision and show its preview.");
        Require(rig.Effects.Invoke(ask.Id, NoticeAction.AllowWrite) && rig.Writes.SequenceEqual([(42L, true)]), "Allowing should complete the write.");
        Require(rig.Effects.Notices.Items.All(item => item.RequestId == 0) && rig.Effects.Notices.Items.Any(item => item.Key == "clip-wrote"), "Allowing should replace the question with the result.");
        Require(!rig.Effects.Invoke(ask.Id, NoticeAction.AllowWrite) && rig.Writes.Count == 1, "A card acts once.");

        rig.Effects.Apply([new ClipboardEffect(ClipboardNoticeKind.WriteRequested, 3, "", 43, "x")]);
        var deny = rig.Effects.Notices.Items.Last();
        rig.Effects.Invoke(deny.Id, NoticeAction.Dismiss);
        Require(rig.Writes.Last() == (43L, false) && rig.Effects.Notices.Find(deny.Id) is null, "Dismissing a held write should deny it.");

        rig.Effects.Apply([new ClipboardEffect(ClipboardNoticeKind.WriteRequested, 3, "", 44, "y")]);
        rig.WriteOutcome = ClipboardOutcome.Unsupported;
        rig.Effects.Invoke(rig.Effects.Notices.Items.Last().Id, NoticeAction.AllowWrite);
        Require(rig.Effects.Notices.Items.Any(item => item.Message.Contains("no longer available", StringComparison.Ordinal)), "An expired request should say so.");

        rig.Effects.Apply([new ClipboardEffect(ClipboardNoticeKind.ReadBlocked, 0, ""), new ClipboardEffect(ClipboardNoticeKind.ReadServed, 12, "app"),
            new ClipboardEffect(ClipboardNoticeKind.WriteTooLarge, 5 * 1024 * 1024, ""), new ClipboardEffect(ClipboardNoticeKind.WriteBlocked, 1, "")]);
        var titles = rig.Effects.Notices.Items.Select(item => item.Title).ToArray();
        Require(titles.Length <= NoticeCenter.MaxVisible, "Cards must stay bounded.");
        Require(titles.Contains("Clipboard write blocked") || titles.Contains("Clipboard write too large"), "Refusals should be disclosed.");

        // A stack of held writes keeps its decisions when transient cards compete for room.
        var crowded = new Rig();
        for (var id = 1; id <= NoticeCenter.MaxVisible; id++) crowded.Effects.Apply([new ClipboardEffect(ClipboardNoticeKind.WriteRequested, 1, "", id, "p")]);
        crowded.Effects.Apply([new ClipboardEffect(ClipboardNoticeKind.Wrote, 1, "")]);
        Require(crowded.Effects.Notices.Items.Count == NoticeCenter.MaxVisible, "Cards must stay bounded when decisions wait.");
    }

    private static void CheckResizeGate()
    {
        var rig = new Rig();
        rig.Effects.Apply([new ResizeRequestEffect(30, 100)]);
        Require(rig.View.Resizes.Count == 0, "Resize requests are ignored by default.");
        rig.Options = new TerminalEffectOptions { AllowWindowResize = true };
        rig.Effects.Apply([new ResizeRequestEffect(30, 100)]);
        Require(rig.View.Resizes.SequenceEqual([(30, 100)]), "An allowed resize should reach the window.");
    }

    private static void CheckNoticeCenter()
    {
        var center = new NoticeCenter();
        var changes = 0;
        center.Changed += () => changes++;
        center.Show("a", NoticeSeverity.Info, "A", "one", 100);
        var merged = center.Show("a", NoticeSeverity.Info, "A", "two", 200);
        Require(center.Items.Count == 1 && merged.Repeats == 2 && merged.Message == "two", "A repeated key should merge and count.");
        center.Show("b", NoticeSeverity.Info, "B", "b", 300);
        Require(center.NextExpiry == 200, "The next expiry should be the earliest deadline.");
        Require(center.Expire(250) && center.Items.Count == 1 && center.NextExpiry == 300, "Expiry should remove only past deadlines.");
        Require(!center.Expire(250), "Expiring nothing should not notify.");
        var persistent = center.Show("held", NoticeSeverity.Warning, "H", "h", 0, [NoticeAction.AllowWrite], 5);
        Require(center.NextExpiry == 300 && center.Expire(10_000) && center.Items.Single().Id == persistent.Id, "A persistent card must outlive timers.");
        center.Show("x1", NoticeSeverity.Info, "x", "x", 0);
        center.Show("x2", NoticeSeverity.Info, "x", "x", 0);
        center.Show("x3", NoticeSeverity.Info, "x", "x", 0);
        center.Show("x4", NoticeSeverity.Info, "x", "x", 0);
        Require(center.Items.Count == NoticeCenter.MaxVisible, "The center is bounded.");
        center.Clear();
        Require(center.Items.Count == 0 && changes > 0, "Clear should empty and notify.");
    }

    private static void CheckPreferences()
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-term-effects-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var defaults = new TerminalPreferences(path);
            Require(defaults.Effects.ClipboardRead == ClipboardReadPolicy.Deny && defaults.Effects.ClipboardWrite == ClipboardWritePolicy.Allow &&
                defaults.Effects.Bell == BellStyle.Visual && !defaults.Effects.AllowWindowResize, "Missing settings should use conservative effect defaults.");

            var changed = 0;
            defaults.Changed += () => changed++;
            var options = new TerminalEffectOptions
            {
                ClipboardRead = ClipboardReadPolicy.Allow, ClipboardWrite = ClipboardWritePolicy.Ask, Bell = BellStyle.Both,
                Notifications = NotificationStyle.InApp, Progress = false, AllowWindowResize = true, ClipboardMaxBytes = 64 * 1024,
            };
            defaults.Update("cmd", 15);
            defaults.UpdateEffects(options);
            Require(changed == 2, "Saving effects should notify open panes.");
            var text = File.ReadAllText(path);
            Require(text.Contains("\"ClipboardRead\": \"Allow\"", StringComparison.Ordinal) && text.Contains("\"Bell\": \"Both\"", StringComparison.Ordinal),
                $"Effect preferences should persist as readable names, got {text}.");

            var reloaded = new TerminalPreferences(path);
            Require(reloaded.Effects == options && reloaded.DefaultProfileId == "cmd" && reloaded.FontSize == 15, "Effect preferences should round trip beside the other settings.");

            reloaded.Update("pwsh", 16);
            Require(new TerminalPreferences(path).Effects == options, "Saving other settings must keep the effect preferences.");

            File.WriteAllText(path, """{"DefaultProfileId":"cmd","FontSize":17,"Effects":{"ClipboardRead":"Sometimes","Bell":"Audible","ClipboardMaxBytes":99999999999,"Progress":"yes"}}""");
            var tolerant = new TerminalPreferences(path);
            Require(tolerant.LoadError is null && tolerant.DefaultProfileId == "cmd" && tolerant.FontSize == 17, "A bad effects section must not discard the other settings.");
            Require(tolerant.Effects.ClipboardRead == ClipboardReadPolicy.Deny && tolerant.Effects.Bell == BellStyle.Audible && tolerant.Effects.Progress &&
                tolerant.Effects.ClipboardMaxBytes == TerminalEffectOptions.DefaultClipboardBytes, "Invalid values should fall back to safe defaults while valid ones are kept.");

            File.WriteAllText(path, """{"DefaultProfileId":"cmd","Effects":"broken"}""");
            Require(new TerminalPreferences(path).Effects == TerminalEffectOptions.Default, "A malformed effects section should use defaults.");
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }
}
