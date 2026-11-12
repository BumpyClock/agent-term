using System.Runtime.InteropServices;
using AgentTerm.Agents;
using AgentTerm_App;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

internal static class RequestFocusChecks
{
    internal const int Inconclusive = 2;

    internal static async Task<int> RunAsync(Window window)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        await using var left = new Pane(dispatcher, "Left");
        await using var right = new Pane(dispatcher, "Right");
        var elsewhere = new TextBox { Name = "Elsewhere", PlaceholderText = "Unrelated field" };
        var root = new Grid
        {
            RequestedTheme = ElementTheme.Dark, Padding = new Thickness(12), ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() },
            RowDefinitions = { new RowDefinition(), new RowDefinition { Height = GridLength.Auto } },
        };
        Grid.SetColumn(right.View, 1);
        Grid.SetRow(elsewhere, 1);
        root.Children.Add(left.View);
        root.Children.Add(right.View);
        root.Children.Add(elsewhere);
        var probe = new EscapeProbe(root);
        window.Content = root;
        window.Activate();
        await Program.WaitAsync(() => root.XamlRoot is not null && root.IsLoaded);
        if (!await probe.TakeForegroundAsync(window))
        {
            Console.WriteLine("Request focus checks inconclusive: the fixture window could not take the foreground.");
            return Inconclusive;
        }
        var failures = new List<string>();
        var inconclusive = false;

        await left.StartAsync();
        await right.StartAsync();

        foreach (var button in new[] { left.Approve, left.Reject })
        {
            await left.AskApprovalAsync("Write-Output reject");
            var request = (ApprovalRequest)left.Panel.Shown!;
            Require(button.Focus(FocusState.Keyboard) && ReferenceEquals(Focused(root), button), "Setup: approval button did not accept focus.");
            if (!await probe.CtrlCAsync(window)) return Inconclusive;
            await left.WaitDismissedAsync("Ctrl+C rejection");
            Require(left.Harness.Session.LastResponse is { Approved: false } response && response.Request == request.Request,
                "Ctrl+C did not reject the focused approval request.");
        }
        Console.WriteLine("Ctrl+C rejected approval from focused Run and Reject buttons.");

        await left.AskApprovalAsync("Write-Output refresh");
        Require(left.Approve.Focus(FocusState.Keyboard) && ReferenceEquals(Focused(root), left.Approve), "Setup: Run button did not accept keyboard focus.");
        left.Harness.Session.Emit(new TextDelta(left.Harness.Session.Turn, "progress while approval waits"));
        await Settle();
        if (!ReferenceEquals(Focused(root), left.Approve)) failures.Add($"Same-request refresh moved focus to {Describe(Focused(root))}.");

        await ApproveFromInsideAsync("keyboard-focused Run button", FocusState.Keyboard);
        await left.AskApprovalAsync("Write-Output clicked");
        Require(left.Approve.Focus(FocusState.Pointer) && ReferenceEquals(Focused(root), left.Approve), "Setup: Run button did not accept pointer focus.");
        await ApproveFromInsideAsync("pointer-focused Run button", FocusState.Programmatic);
        await left.AskApprovalAsync("Start-Sleep 20");
        Require(left.RequestText.Focus(FocusState.Pointer) && ReferenceEquals(Focused(root), left.RequestText), "Setup: selectable request text did not accept focus.");
        await ApproveFromInsideAsync("focused selectable request text", FocusState.Programmatic);

        var question = new QuestionRequest(left.Harness.Session.Turn, Guid.NewGuid(), "Pick a target", ["alpha", "beta"], false);
        left.Harness.Session.Emit(question);
        await Program.WaitAsync(() => left.Panel.Shown == question && left.Panel.Visibility == Visibility.Visible);
        var choice = left.Choices.Children.OfType<Button>().First();
        Require(choice.Focus(FocusState.Keyboard) && ReferenceEquals(Focused(root), choice), "Setup: choice button did not accept keyboard focus.");
        new ButtonAutomationPeer(choice).Invoke();
        await left.WaitDismissedAsync("choice");
        await CheckOwnerAsync("removed choice button", left.Reply, FocusState.Keyboard);

        await left.AskApprovalAsync("Write-Output elsewhere");
        Require(elsewhere.Focus(FocusState.Keyboard) && ReferenceEquals(Focused(root), elsewhere), "Setup: unrelated field did not accept focus.");
        await left.Panel.ApproveAsync();
        await left.WaitDismissedAsync("approval with focus elsewhere");
        if (!ReferenceEquals(Focused(root), elsewhere)) failures.Add($"Dismissal took focus from an unrelated field to {Describe(Focused(root))}.");

        await left.AskApprovalAsync("Write-Output left");
        await right.AskApprovalAsync("Write-Output right");
        Require(right.Approve.Focus(FocusState.Keyboard) && ReferenceEquals(Focused(root), right.Approve), "Setup: other pane's Run button did not accept focus.");
        await left.Panel.ApproveAsync();
        await left.WaitDismissedAsync("approval with focus in the other pane");
        if (!ReferenceEquals(Focused(root), right.Approve)) failures.Add($"Dismissal took focus from the other pane to {Describe(Focused(root))}.");

        if (failures.Count > 0) throw new Exception("Request focus regression:\n" + string.Join("\n", failures));
        Console.WriteLine(inconclusive
            ? "Request focus checks passed, but Escape could not be injected because the fixture window was not foreground. Inconclusive."
            : "Request focus checks passed. Escape tunnelled from the visible reply field after each dismissal.");
        return inconclusive ? Inconclusive : 0;

        async Task ApproveFromInsideAsync(string label, FocusState expected)
        {
            new ButtonAutomationPeer(left.Approve).Invoke();
            await left.WaitDismissedAsync(label);
            await CheckOwnerAsync(label, left.Reply, expected);
        }

        async Task CheckOwnerAsync(string label, TextBox reply, FocusState expected)
        {
            var focused = Focused(root);
            var focusState = reply.FocusState;
            var beforeEscape = Describe(focused);
            var modalityMatches = expected == FocusState.Programmatic ? focusState != FocusState.Unfocused : focusState == expected;
            var ok = ReferenceEquals(focused, reply) && IsShown(reply) && modalityMatches;
            var escape = await probe.PressAsync(window);
            if (!escape.Foreground) inconclusive = true;
            else if (!escape.Reached || !ReferenceEquals(escape.Source, reply)) ok = false;
            if (!ok)
                failures.Add($"After dismissing with {label}: focus before Escape {beforeEscape} ({focusState}), expected visible {reply.Name} after a {expected} focus request. " +
                    (escape.Foreground ? $"Escape reached root: {escape.Reached}, source {Describe(escape.Source)}." : "Escape not injected; window not foreground."));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static object? Focused(UIElement root) => FocusManager.GetFocusedElement(root.XamlRoot);

    private static Task Settle() => Task.Delay(200);

    private static bool IsShown(DependencyObject element)
    {
        if (element is UIElement { XamlRoot: null }) return false;
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }

    private static string Describe(object? element) => element switch
    {
        null => "nothing",
        FrameworkElement e => $"{e.GetType().Name} '{e.Name}' (shown={IsShown(e)}, in tree={e.XamlRoot is not null})",
        _ => element.GetType().Name,
    };

    private sealed class Pane : IAsyncDisposable
    {
        public Pane(DispatcherQueue dispatcher, string name)
        {
            Agent = new(Harness);
            Reply = new TextBox { Name = name + "Reply", AcceptsReturn = true, PlaceholderText = "Reply to the agent" };
            Panel.Visibility = Visibility.Collapsed;
            Panel.Attach(Agent, Reply);
            View = new StackPanel { Spacing = 8, Children = { Panel, Reply } };
            Agent.Changed += () => dispatcher.TryEnqueue(() =>
            {
                Panel.Update();
                Reply.IsReadOnly = Agent.HasActiveTurn && Agent.Request is not QuestionRequest { AllowFreeform: true };
            });
        }

        public Program.Harness Harness { get; } = new();
        public AgentSessionController Agent { get; }
        public AgentRequestPanel Panel { get; } = new();
        public TextBox Reply { get; }
        public StackPanel View { get; }
        public Button Approve => (Button)Panel.FindName("ApproveRequest");
        public Button Reject => (Button)Panel.FindName("DenyRequest");
        public TextBlock RequestText => (TextBlock)Panel.FindName("RequestText");
        public StackPanel Choices => (StackPanel)Panel.FindName("RequestChoices");

        public async Task StartAsync()
        {
            await Agent.SubmitAsync("Run a slow command", new(Environment.CurrentDirectory));
            await Program.WaitAsync(() => Agent.State == AgentState.Running);
        }

        public async Task AskApprovalAsync(string command)
        {
            var request = new ApprovalRequest(Harness.Session.Turn, Guid.NewGuid(), command);
            Harness.Session.Emit(request);
            await Program.WaitAsync(() => Panel.Shown == request && Panel.Visibility == Visibility.Visible);
            await Settle();
        }

        public async Task WaitDismissedAsync(string label)
        {
            try { await Program.WaitAsync(() => Agent.Request is null && Agent.State == AgentState.Running && Panel.Shown is null); }
            catch (OperationCanceledException) { throw new Exception($"Setup: {label} did not leave the command running with no request (state {Agent.State})."); }
            if (!Agent.HasActiveTurn || !Reply.IsReadOnly || Panel.Visibility != Visibility.Collapsed)
                throw new Exception($"Setup: {label} did not reach running with input paused and the panel hidden.");
            await Settle();
        }

        public ValueTask DisposeAsync() => Agent.DisposeAsync();
    }

    private readonly record struct EscapeResult(bool Foreground, bool Reached, object? Source);

    private sealed class EscapeProbe
    {
        private bool _armed;
        private bool _reached;
        private object? _source;

        public EscapeProbe(UIElement root) => root.PreviewKeyDown += (_, e) =>
        {
            if (!_armed || e.Key != VirtualKey.Escape) return;
            _reached = true;
            _source = e.OriginalSource;
            e.Handled = true;
        };

        public async Task<bool> TakeForegroundAsync(Window window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            SetForegroundWindow(hwnd);
            await Task.Delay(100);
            return GetForegroundWindow() == hwnd;
        }

        public async Task<EscapeResult> PressAsync(Window window)
        {
            if (!await TakeForegroundAsync(window)) return new(false, false, null);
            (_armed, _reached, _source) = (true, false, null);
            INPUT[] keys = [Key(0), Key(KeyUp)];
            if (SendInput((uint)keys.Length, keys, Marshal.SizeOf<INPUT>()) != keys.Length) return new(false, false, null);
            for (var i = 0; i < 50 && !_reached; i++) await Task.Delay(20);
            _armed = false;
            return new(true, _reached, _source);
        }

        public async Task<bool> CtrlCAsync(Window window)
        {
            if (!await TakeForegroundAsync(window)) return false;
            INPUT[] keys = [KeyCode(0x11, 0), KeyCode(0x43, 0), KeyCode(0x43, KeyUp), KeyCode(0x11, KeyUp)];
            return SendInput((uint)keys.Length, keys, Marshal.SizeOf<INPUT>()) == keys.Length;
        }

        private static INPUT KeyCode(ushort code, uint flags) => new() { Type = 1, Union = new() { Keyboard = new() { VirtualKey = code, Flags = flags } } };
        private const uint KeyUp = 0x0002;
        private static INPUT Key(uint flags) => new() { Type = 1, Union = new() { Keyboard = new() { VirtualKey = 0x1B, Flags = flags } } };

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint Type;
            public InputUnion Union;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT Mouse;
            [FieldOffset(0)] public KEYBDINPUT Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int X, Y; public uint Data, Flags, Time; public nint Extra; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nint Extra; }

        [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    }
}
