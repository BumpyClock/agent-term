using System.Text;
using System.Text.RegularExpressions;
using AgentTerm.Terminal;

/// <summary>Headless checks for native bell, notification, progress, clipboard, and resize callbacks. Never touches the real clipboard.</summary>
internal static class EffectsChecks
{
    private sealed class FakeClipboard : ITerminalClipboard
    {
        public string? Text;
        public int Writes, Clears, Reads;
        public ClipboardOutcome ReadOutcome = ClipboardOutcome.Success, WriteOutcome = ClipboardOutcome.Success;
        public bool Throw;
        public bool HasText() => Text is { Length: > 0 };

        public ClipboardOutcome TryReadText(int maxChars, out string text)
        {
            Reads++;
            if (Throw) throw new InvalidOperationException("clipboard fault");
            text = "";
            if (ReadOutcome != ClipboardOutcome.Success) return ReadOutcome;
            if (Text is { Length: var length } && length > maxChars) return ClipboardOutcome.TooLarge;
            text = Text ?? "";
            return ClipboardOutcome.Success;
        }

        public ClipboardOutcome TryWriteText(string text)
        {
            if (Throw) throw new InvalidOperationException("clipboard fault");
            if (WriteOutcome != ClipboardOutcome.Success) return WriteOutcome;
            Text = text;
            Writes++;
            return ClipboardOutcome.Success;
        }

        public ClipboardOutcome TryClear()
        {
            Text = null;
            Clears++;
            return ClipboardOutcome.Success;
        }
    }

    private const string Esc = "\u001b";
    private const string Bel = "\u0007";
    private const string St = "\u001b\\";

    public static void Run()
    {
        CheckBell();
        CheckNotifications();
        CheckProgress();
        CheckProgressReset();
        CheckClipboardWrite();
        CheckClipboardWritePolicies();
        CheckClipboardWriteLimits();
        CheckClipboardRead();
        CheckClipboardFaults();
        CheckKittyClipboard();
        CheckKittyPaste();
        CheckResizeRequests();
        CheckOrderingAndReset();
        CheckOptionsNormalization();
        Console.WriteLine("Effects checks passed native bell, notification, progress, clipboard, paste event, and resize callbacks.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string Osc52Write(string text, string selection = "c") => $"{Esc}]52;{selection};{Base64(text)}{Bel}";

    private static (GhosttyTerminal Terminal, FakeClipboard Clipboard) Create(TerminalEffectOptions? options = null)
    {
        var terminal = new GhosttyTerminal(40, 6);
        var clipboard = new FakeClipboard();
        terminal.Effects.Clipboard = clipboard;
        if (options is not null) terminal.Effects.Options = options;
        return (terminal, clipboard);
    }

    private static void Feed(GhosttyTerminal terminal, string text) => terminal.Write(Encoding.UTF8.GetBytes(text));

    private static string Reply(GhosttyTerminal terminal) =>
        Encoding.UTF8.GetString(terminal.TakeResponses().SelectMany(bytes => bytes).ToArray());

    private static void CheckBell()
    {
        var (terminal, _) = Create();
        using (terminal)
        {
            Feed(terminal, "\a\a\a");
            var effects = terminal.Effects.TakePending();
            Require(effects is [BellEffect { Count: 3 }], "Three bells in one chunk should coalesce into one effect with a count.");
            Require(terminal.Effects.TakePending().Count == 0, "Taking effects should empty the queue.");
            for (var index = 0; index < 5000; index++) Feed(terminal, "\a");
            Require(terminal.Effects.TakePending() is [BellEffect { Count: TerminalEffects.MaxBellCount }], "A bell storm should be bounded.");
            terminal.Effects.Options = new TerminalEffectOptions { Bell = BellStyle.None };
            Feed(terminal, "\a");
            Require(terminal.Effects.TakePending().Count == 0, "A disabled bell should queue nothing.");
            terminal.Effects.Options = new TerminalEffectOptions { Bell = BellStyle.Audible };
            Feed(terminal, "\a");
            Require(terminal.Effects.TakePending() is [BellEffect { Count: 1 }], "An audible-only bell should still be delivered to the host.");
        }
    }

    private static void CheckNotifications()
    {
        var (terminal, _) = Create();
        using (terminal)
        {
            Feed(terminal, $"{Esc}]9;Build done{Bel}");
            Feed(terminal, $"{Esc}]777;notify;Deploy;Finished é{St}");
            var effects = terminal.Effects.TakePending();
            Require(effects.Count == 2 && effects[0] is NotificationEffect { Title: "", Body: "Build done", Repeats: 1 } &&
                effects[1] is NotificationEffect { Title: "Deploy", Body: "Finished é" }, $"OSC 9 and OSC 777 should queue notifications, got {Describe(effects)}.");

            for (var index = 0; index < 50; index++) Feed(terminal, $"{Esc}]9;same{Bel}");
            effects = terminal.Effects.TakePending();
            Require(effects is [NotificationEffect { Body: "same", Repeats: 50 }], $"Identical notifications should merge, got {Describe(effects)}.");

            for (var index = 0; index < 100; index++) Feed(terminal, $"{Esc}]9;n{index}{Bel}");
            effects = terminal.Effects.TakePending();
            Require(effects.Count == TerminalEffects.MaxPendingNotifications && effects.Cast<NotificationEffect>().Sum(effect => effect.Repeats) == 100,
                "A notification storm should keep a bounded list and count the suppressed requests.");

            terminal.Effects.Notify("a\u0001b‮c", "line\u0000two");
            var sanitized = (NotificationEffect)terminal.Effects.TakePending().Single();
            Require(sanitized.Title.All(character => !char.IsControl(character) && character != '‮') &&
                sanitized.Body.All(character => !char.IsControl(character)), "Notification text should not carry control or bidi override characters.");

            terminal.Effects.Options = new TerminalEffectOptions { Notifications = NotificationStyle.Off };
            Feed(terminal, $"{Esc}]9;ignored{Bel}");
            Require(terminal.Effects.TakePending().Count == 0, "Disabled notifications should queue nothing.");
        }
    }

    private static void CheckProgress()
    {
        var (terminal, _) = Create();
        using (terminal)
        {
            Feed(terminal, $"{Esc}]9;4;1;50{Bel}");
            Require(terminal.Effects.TakePending() is [ProgressEffect { State: TerminalProgressState.Set, Percent: 50 }], "OSC 9;4;1 should set determinate progress.");
            for (var percent = 0; percent <= 100; percent++) Feed(terminal, $"{Esc}]9;4;1;{percent}{Bel}");
            Require(terminal.Effects.TakePending() is [ProgressEffect { State: TerminalProgressState.Set, Percent: 100 }],
                "A progress flood should publish only the latest state.");
            Feed(terminal, $"{Esc}]9;4;3{Bel}");
            Require(terminal.Effects.TakePending() is [ProgressEffect { State: TerminalProgressState.Indeterminate }], "State 3 should be indeterminate.");
            Feed(terminal, $"{Esc}]9;4;2;70{Bel}");
            Require(terminal.Effects.TakePending() is [ProgressEffect { State: TerminalProgressState.Error, Percent: 70 }], "State 2 should be an error with its percentage.");
            Feed(terminal, $"{Esc}]9;4;4{Bel}");
            Require(terminal.Effects.TakePending() is [ProgressEffect { State: TerminalProgressState.Pause, Percent: -1 }], "A paused state without a value should keep the previous percentage.");
            Feed(terminal, $"{Esc}]9;4;0{Bel}");
            Require(terminal.Effects.TakePending() is [ProgressEffect { State: TerminalProgressState.Remove }], "State 0 should remove progress.");
            for (var index = 0; index < 100; index++) Feed(terminal, $"{Esc}]9;4;0{Bel}");
            Require(terminal.Effects.TakePending().Count == 0, "Removing progress that is not shown should queue nothing.");
            Feed(terminal, $"{Esc}]9;4;1;40{Bel}{Esc}]9;4;0{Bel}");
            Require(terminal.Effects.TakePending().Count == 0, "Progress set and removed before delivery should cancel.");
            terminal.Effects.Options = new TerminalEffectOptions { Progress = false };
            Feed(terminal, $"{Esc}]9;4;1;40{Bel}");
            Require(terminal.Effects.TakePending().Count == 0, "Disabled progress should queue nothing.");
        }
    }

    private static void CheckProgressReset()
    {
        var (terminal, _) = Create();
        using (terminal)
        {
            Feed(terminal, $"{Esc}]9;4;1;30{Bel}");
            terminal.Effects.TakePending();
            Feed(terminal, $"{Esc}c");
            var afterReset = terminal.Effects.TakePending();
            Require(afterReset is [ProgressEffect { State: TerminalProgressState.Remove }],
                $"A full reset (RIS) should remove progress that is visible, got {Describe(afterReset)}.");
            Feed(terminal, $"{Esc}]9;4;1;30{Bel}");
            terminal.Effects.Reset();
            var effects = terminal.Effects.TakePending();
            Require(effects.Count == 0, $"Resetting effects before delivery should drop the undelivered progress, got {Describe(effects)}.");
            Feed(terminal, $"{Esc}]9;4;1;30{Bel}");
            terminal.Effects.TakePending();
            terminal.Effects.Reset();
            Require(terminal.Effects.TakePending() is [ProgressEffect { State: TerminalProgressState.Remove }],
                "Resetting effects should remove progress the host is showing.");
        }
    }

    private static void CheckClipboardWrite()
    {
        var (terminal, clipboard) = Create();
        using (terminal)
        {
            Feed(terminal, Osc52Write("copied é 日本"));
            Require(clipboard.Text == "copied é 日本" && clipboard.Writes == 1, $"OSC 52 should write the clipboard, got <{clipboard.Text}>.");
            var effects = terminal.Effects.TakePending();
            Require(effects is [ClipboardEffect { Kind: ClipboardNoticeKind.Wrote, Length: 11 }], $"A write should be disclosed, got {Describe(effects)}.");

            Feed(terminal, Osc52Write("one"));
            Feed(terminal, Osc52Write("two"));
            effects = terminal.Effects.TakePending();
            Require(clipboard.Text == "two" && effects is [ClipboardEffect { Kind: ClipboardNoticeKind.Wrote, Repeats: 2 }],
                $"Repeated writes should coalesce their notices, got {Describe(effects)}.");

            Feed(terminal, Osc52Write("kept"));
            Feed(terminal, Osc52Write(""));
            Require(clipboard.Text is null && clipboard.Clears == 1, "An empty OSC 52 payload asks to clear the clipboard.");
            Require(terminal.Effects.TakePending().OfType<ClipboardEffect>().Any(effect => effect.Kind == ClipboardNoticeKind.Cleared),
                "A clear should be disclosed.");
            clipboard.Text = "kept";

            Feed(terminal, Osc52Write("primary", "p"));
            Require(clipboard.Text == "kept", "A primary selection write has no Windows equivalent and must not touch the clipboard.");

            Feed(terminal, $"{Esc}]52;c;!!!not base64!!!{Bel}");
            Require(clipboard.Text == "kept", "Invalid base64 must not reach the clipboard.");

            Feed(terminal, $"{Esc}]52;c;{Convert.ToBase64String([0xFF, 0xFE, 0xFD])}{Bel}");
            Require(clipboard.Text == "kept", "Invalid UTF-8 must not reach the clipboard.");
        }
    }

    private static void CheckClipboardWritePolicies()
    {
        var (terminal, clipboard) = Create(new TerminalEffectOptions { ClipboardWrite = ClipboardWritePolicy.Deny });
        using (terminal)
        {
            Feed(terminal, Osc52Write("blocked"));
            Require(clipboard.Text is null && clipboard.Writes == 0, "Deny should refuse the write.");
            Require(terminal.Effects.TakePending() is [ClipboardEffect { Kind: ClipboardNoticeKind.WriteBlocked }], "A refused write should be disclosed.");

            terminal.Effects.Options = new TerminalEffectOptions { ClipboardWrite = ClipboardWritePolicy.Ask };
            Feed(terminal, Osc52Write("pending <ESC>" + Esc + "[31m"));
            Require(clipboard.Text is null, "Ask should not copy before approval.");
            var request = terminal.Effects.TakePending().Single() as ClipboardEffect;
            Require(request is { Kind: ClipboardNoticeKind.WriteRequested, RequestId: > 0 } && request.Preview.All(c => !char.IsControl(c)),
                "Ask should queue a request with a sanitized preview.");
            Require(terminal.Effects.PendingWriteCount == 1, "The text should wait for a decision.");
            Require(terminal.Effects.ResolveWrite(request!.RequestId, allow: true) == ClipboardOutcome.Success && clipboard.Text!.StartsWith("pending", StringComparison.Ordinal),
                "Approving should copy the held text.");
            Require(terminal.Effects.ResolveWrite(request.RequestId, allow: true) == ClipboardOutcome.Unsupported && clipboard.Writes == 1,
                "An approval is single use.");

            Feed(terminal, Osc52Write("second"));
            var second = (ClipboardEffect)terminal.Effects.TakePending().Single();
            Require(terminal.Effects.ResolveWrite(second.RequestId, allow: false) == ClipboardOutcome.Denied && clipboard.Writes == 1,
                "Denying should discard the text.");

            for (var index = 0; index < 10; index++) Feed(terminal, Osc52Write("flood" + index));
            var flood = terminal.Effects.TakePending();
            Require(terminal.Effects.PendingWriteCount == TerminalEffects.MaxPendingWrites && flood.Count == TerminalEffects.MaxPendingWrites,
                $"Held writes should be bounded, keeping the newest, got {terminal.Effects.PendingWriteCount} held and {Describe(flood)}.");

            Feed(terminal, Osc52Write("reset me"));
            terminal.Effects.Reset();
            Require(terminal.Effects.PendingWriteCount == 0, "Reset should discard held writes.");

            clipboard.WriteOutcome = ClipboardOutcome.Busy;
            terminal.Effects.Options = new TerminalEffectOptions { ClipboardWrite = ClipboardWritePolicy.Allow };
            Feed(terminal, Osc52Write("busy"));
            Require(terminal.Effects.TakePending().OfType<ClipboardEffect>().Single().Kind == ClipboardNoticeKind.WriteFailed,
                "A busy clipboard should be reported as a failed write.");
        }
    }

    private static void CheckClipboardWriteLimits()
    {
        var (terminal, clipboard) = Create(new TerminalEffectOptions { ClipboardMaxBytes = TerminalEffectOptions.MinimumClipboardBytes });
        using (terminal)
        {
            Feed(terminal, Osc52Write(new string('x', 2000)));
            Require(clipboard.Text is { Length: 2000 }, "A write under the limit should copy.");
            Feed(terminal, Osc52Write(new string('y', 3500) + new string('z', 600)));
            Require(clipboard.Text is { Length: 2000 }, "A write over the limit should be refused.");
            Require(terminal.Effects.TakePending().OfType<ClipboardEffect>().Any(effect => effect.Kind == ClipboardNoticeKind.WriteTooLarge),
                "An oversized write should be disclosed.");
        }
    }

    private static void CheckClipboardRead()
    {
        var (terminal, clipboard) = Create();
        using (terminal)
        {
            clipboard.Text = "secret é";
            Feed(terminal, $"{Esc}]52;c;?{Bel}");
            var denied = Reply(terminal);
            Require(clipboard.Reads == 0, "The default policy must not read the clipboard.");
            Require(!denied.Contains(Base64("secret é"), StringComparison.Ordinal), $"A denied read must not disclose the clipboard, got <{denied}>.");
            Require(terminal.Effects.TakePending() is [ClipboardEffect { Kind: ClipboardNoticeKind.ReadBlocked }], "A refused read should be disclosed.");

            terminal.Effects.Options = new TerminalEffectOptions { ClipboardRead = ClipboardReadPolicy.Allow };
            Feed(terminal, $"{Esc}]52;c;?{Bel}");
            var served = Reply(terminal);
            Require(served.Contains(Base64("secret é"), StringComparison.Ordinal), $"An allowed read should answer with the clipboard text, got <{served}>.");
            Require(terminal.Effects.TakePending() is [ClipboardEffect { Kind: ClipboardNoticeKind.ReadServed, Length: 8 }], "A served read should be disclosed.");

            clipboard.Text = new string('a', TerminalEffectOptions.MinimumClipboardBytes + 10);
            terminal.Effects.Options = new TerminalEffectOptions { ClipboardRead = ClipboardReadPolicy.Allow, ClipboardMaxBytes = TerminalEffectOptions.MinimumClipboardBytes };
            Feed(terminal, $"{Esc}]52;c;?{Bel}");
            Require(!Reply(terminal).Contains("YWFh", StringComparison.Ordinal), "A clipboard over the limit must not be returned.");
            Require(terminal.Effects.TakePending().OfType<ClipboardEffect>().Single().Kind == ClipboardNoticeKind.ReadFailed, "An oversized read should be reported.");

            clipboard.Text = "again";
            clipboard.ReadOutcome = ClipboardOutcome.Busy;
            Feed(terminal, $"{Esc}]52;c;?{Bel}");
            Require(!Reply(terminal).Contains("YWdhaW4", StringComparison.Ordinal), "A busy clipboard should answer empty.");
            clipboard.ReadOutcome = ClipboardOutcome.Success;
            terminal.Effects.TakePending();

            // The permission transition applies to the next request without recreating the terminal.
            terminal.Effects.Options = new TerminalEffectOptions { ClipboardRead = ClipboardReadPolicy.Deny };
            var reads = clipboard.Reads;
            Feed(terminal, $"{Esc}]52;c;?{Bel}");
            Reply(terminal);
            Require(clipboard.Reads == reads, "Turning reads off should take effect immediately.");
        }
    }

    private static void CheckClipboardFaults()
    {
        var (terminal, clipboard) = Create(new TerminalEffectOptions { ClipboardRead = ClipboardReadPolicy.Allow });
        using (terminal)
        {
            clipboard.Throw = true;
            clipboard.Text = "x";
            Feed(terminal, Osc52Write("boom"));
            Feed(terminal, $"{Esc}]52;c;?{Bel}");
            Reply(terminal);
            Require(terminal.Effects.Faults >= 2, "Adapter exceptions should be contained and counted.");
            Feed(terminal, "ok");
            Require(terminal.ReadText() == "ok", "The terminal should keep working after a failed clipboard.");
            terminal.Effects.Clipboard = null;
            Feed(terminal, Osc52Write("none"));
            terminal.Effects.TakePending();
        }
    }

    private static void CheckKittyClipboard()
    {
        var (terminal, clipboard) = Create(new TerminalEffectOptions { ClipboardRead = ClipboardReadPolicy.Allow });
        using (terminal)
        {
            clipboard.Text = "kitty text";
            var mimes = Base64("text/plain");
            Feed(terminal, $"{Esc}]5522;type=read;{mimes}{St}");
            var response = Reply(terminal);
            Require(response.Contains("5522", StringComparison.Ordinal) && response.Contains("status=OK", StringComparison.Ordinal) &&
                response.Contains(Base64("kitty text"), StringComparison.Ordinal), $"A Kitty read should answer with the text, got <{response}>.");

            terminal.Effects.Options = new TerminalEffectOptions { ClipboardRead = ClipboardReadPolicy.Deny };
            Feed(terminal, $"{Esc}]5522;type=read;{mimes}{St}");
            response = Reply(terminal);
            Require(response.Contains("EPERM", StringComparison.Ordinal) && !response.Contains(Base64("kitty text"), StringComparison.Ordinal),
                $"A refused Kitty read should answer EPERM, got <{response}>.");

            // A targets-only listing reveals nothing when reads are off.
            Feed(terminal, $"{Esc}]5522;type=read{St}");
            response = Reply(terminal);
            Require(!response.Contains(mimes, StringComparison.Ordinal),
                $"A listing request must not reveal available formats while reads are off, got <{response}>.");
            terminal.Effects.TakePending();

            // Kitty writes arrive as one transaction and are acknowledged.
            terminal.Effects.Options = TerminalEffectOptions.Default;
            var data = Base64("from kitty");
            var write = $"{Esc}]5522;type=write{St}{Esc}]5522;type=wdata:mime={mimes};{data}{St}{Esc}]5522;type=wdata{St}";
            Feed(terminal, write);
            response = Reply(terminal);
            Require(clipboard.Text == "from kitty", $"A Kitty write should reach the clipboard, got <{clipboard.Text}> after <{response}>.");
            Require(response.Contains("DONE", StringComparison.Ordinal), $"A successful Kitty write should acknowledge DONE, got <{response}>.");

            terminal.Effects.Options = new TerminalEffectOptions { ClipboardWrite = ClipboardWritePolicy.Deny };
            Feed(terminal, write);
            response = Reply(terminal);
            Require(response.Contains("EPERM", StringComparison.Ordinal), $"A refused Kitty write should answer EPERM, got <{response}>.");
        }
    }

    private static void CheckKittyPaste()
    {
        var (terminal, clipboard) = Create();
        using (terminal)
        {
            var plain = terminal.EncodeClipboardPaste("hello");
            Require(Encoding.UTF8.GetString(plain) == "hello", "Without paste events a paste should be the plain text.");

            Feed(terminal, $"{Esc}[?2004h");
            Require(Encoding.UTF8.GetString(terminal.EncodeClipboardPaste("hi")) == $"{Esc}[200~hi{Esc}[201~", "Bracketed paste should still work.");
            Feed(terminal, $"{Esc}[?2004l");

            Feed(terminal, $"{Esc}[?5522h");
            var pasteEvent = Encoding.UTF8.GetString(terminal.EncodeClipboardPaste("pasted é"));
            Require(pasteEvent.Contains("5522", StringComparison.Ordinal) && !pasteEvent.Contains("pasted", StringComparison.Ordinal),
                $"With mode 5522 a paste should be an event, not the text, got <{pasteEvent}>.");
            Require(terminal.TakeResponses().Length == 0, "The paste event should not also be queued as a terminal reply.");

            // The program's follow-up read is answered from the pasted text even though general reads are off.
            var password = Regex.Match(pasteEvent, @"pw=([^:;\u001b]+)");
            var pw = password.Success ? $":pw={password.Groups[1].Value}:name={Base64("Paste test")}" : "";
            Feed(terminal, $"{Esc}]5522;type=read{pw};{Base64("text/plain")}{St}");
            var response = Reply(terminal);
            Require(response.Contains(Base64("pasted é"), StringComparison.Ordinal) && clipboard.Reads == 0,
                $"A paste event read should be served from the pasted text without reading the clipboard, got <{response}> from <{pasteEvent}> sent <{pw}> effects {Describe(terminal.Effects.TakePending())}.");
            Require(!terminal.Effects.TakePending().OfType<ClipboardEffect>().Any(effect => effect.Kind == ClipboardNoticeKind.ReadBlocked),
                "A read answered by the paste grant should not be reported as blocked.");
        }
    }

    private static void CheckResizeRequests()
    {
        var (terminal, _) = Create();
        using (terminal)
        {
            Feed(terminal, $"{Esc}[8;30;100t");
            Require(terminal.Effects.TakePending().Count == 0, "Resize requests are off by default.");
            terminal.Effects.Options = new TerminalEffectOptions { AllowWindowResize = true };
            Feed(terminal, $"{Esc}[8;30;100t");
            Require(terminal.Effects.TakePending() is [ResizeRequestEffect { Rows: 30, Columns: 100 }], "An allowed resize request should be delivered.");
            Feed(terminal, $"{Esc}[8;0;90t");
            Require(terminal.Effects.TakePending() is [ResizeRequestEffect { Rows: 0, Columns: 90 }], "Zero should keep that dimension.");
            Feed(terminal, $"{Esc}[8;10;50t{Esc}[8;12;60t");
            Require(terminal.Effects.TakePending() is [ResizeRequestEffect { Rows: 12, Columns: 60 }], "Only the latest resize request should remain.");
            Require(terminal.ReadText() == "", "Requesting a resize must not resize the terminal grid itself.");
        }
    }

    private static void CheckOrderingAndReset()
    {
        var (terminal, _) = Create(new TerminalEffectOptions { Notifications = NotificationStyle.InApp });
        using (terminal)
        {
            Feed(terminal, $"{Esc}]9;4;1;10{Bel}{Esc}]9;first{Bel}{Esc}]9;4;1;20{Bel}{Esc}]9;second{Bel}");
            var effects = terminal.Effects.TakePending();
            Require(effects is [NotificationEffect { Body: "first" }, ProgressEffect { Percent: 20 }, NotificationEffect { Body: "second" }],
                $"Effects should keep request order with coalesced progress, got {Describe(effects)}.");
        }
    }

    private static void CheckOptionsNormalization()
    {
        var bad = new TerminalEffectOptions { ClipboardMaxBytes = int.MaxValue, ClipboardRead = (ClipboardReadPolicy)99, Bell = (BellStyle)99,
            Notifications = (NotificationStyle)99, ClipboardWrite = (ClipboardWritePolicy)99 }.Normalize();
        Require(bad.ClipboardMaxBytes == TerminalEffectOptions.MaximumClipboardBytes && bad.ClipboardRead == ClipboardReadPolicy.Deny &&
            bad.ClipboardWrite == ClipboardWritePolicy.Allow && bad.Notifications == NotificationStyle.InAppAndSystem && ((int)bad.Bell & ~3) == 0,
            "Out-of-range preferences should normalize to safe values.");
        Require(TerminalEffectOptions.Default.ClipboardRead == ClipboardReadPolicy.Deny, "The default must not allow clipboard reads.");
        Require(!TerminalEffectOptions.Default.AllowWindowResize, "The default must not let programs resize the window.");
        using var terminal = new GhosttyTerminal(10, 2);
        Require(terminal.SupportsClipboardProtocols, "The native clipboard callbacks should register.");
    }

    private static string Describe(IReadOnlyList<TerminalEffect> effects) => string.Join(", ", effects.Select(effect => effect.ToString()));
}
