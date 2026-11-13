using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Threading.Channels;
using AgentTerm.Agents;
using AgentTerm_App;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.ViewManagement;

internal static class Program
{
    private static int _result;
    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var app = new AgentUiSmoke.App();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
        });
        return _result;
    }

    internal static async void RunAsync(Application app)
    {
        var window = new Window();
        try
        {
            if (Environment.GetCommandLineArgs().Contains("--request-focus-only"))
            {
                _result = await RequestFocusChecks.RunAsync(window);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--request-layout"))
            {
                await RequestLayoutChecks.RunAsync(window);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--pane-header"))
            {
                await PaneHeaderChecks.RunAsync(window);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--timeline-preferences"))
            {
                await TimelinePreferenceChecks.RunAsync(window);
                return;
            }
            ProgramStatusUiChecks.Run();
            if (Environment.GetCommandLineArgs().Contains("--program-activity"))
            {
                await ProgramActivityChecks.RunAsync(window);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--status-icons"))
            {
                window.Activate();
                await ProgramStatusUiChecks.CheckIconsAsync(window);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--status-layout"))
            {
                await ProgramStatusUiChecks.CheckIconPreferencesAsync(window);
                await ProgramStatusUiChecks.CheckLayoutAsync(window);
                await ProgramStatusUiChecks.CheckCardAsync(window);
                return;
            }
            if (!Environment.GetCommandLineArgs().Contains("--timeline-only"))
            {
                await ProgramActivityChecks.RunAsync(window);
                await ProgramStatusUiChecks.CheckIconPreferencesAsync(window);
                await ProgramStatusUiChecks.CheckDetailsAsync(window);
                await ProgramStatusUiChecks.CheckCardAsync(window);
                await ProgramStatusUiChecks.CheckIconsAsync(window);
                await PaneHeaderChecks.RunAsync(window);
            }
            var fixture = new Harness();
            await using var controller = new AgentSessionController(fixture);
            var view = new AgentTimelineView { Width = 480, HorizontalAlignment = HorizontalAlignment.Left };
            controller.Changed += () => view.Update(controller, controller.CurrentTask, true);
            window.Content = new Grid { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Children = { view } };
            window.Activate();
            await controller.SubmitAsync("Run a fixture", new(Environment.CurrentDirectory));
            await WaitAsync(() => HasText(view, "Working"));
            var requestText = Descendants(view).OfType<TextBlock>().Single(t => t.Text == "Run a fixture");
            if (requestText.FontWeight != Microsoft.UI.Text.FontWeights.Normal) throw new Exception("User prompt must remain regular weight.");
            fixture.Session.Emit(new TextDelta(fixture.Session.Turn, "Primary fixture answer"));
            await WaitAsync(() => HasText(view, "Primary fixture answer"));
            var answerText = Descendants(view).OfType<TextBlock>().Single(t => t.Text == "Primary fixture answer");
            if (Equals(requestText.Foreground, answerText.Foreground)) throw new Exception("Prompt and answer need distinct visual hierarchy.");
            if (!Descendants(view).OfType<ProgressRing>().Any(p => p.Visibility == Visibility.Visible && p.Width >= 20 && p.Height >= 20)) throw new Exception("Quiet work needs a visible progress affordance.");
            using (var elapsedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
                while (!Descendants(view).OfType<TextBlock>().Any(t => t.Text.StartsWith("Working · 0:")))
                    await Task.Delay(100, elapsedTimeout.Token);
            fixture.Session.Emit(new ToolStarted(fixture.Session.Turn, "one", "terminal", ToolCategory.Command, "fixture", "fixture command"));
            fixture.Session.Emit(new TextDelta(fixture.Session.Turn, ""));
            await WaitAsync(() => controller.Timeline.Entries.OfType<TimelineWorkGroup>().Any());
            await WaitAsync(() => ShowsText(view, "pending"));
            if (ShowsText(view, "running")) throw new Exception("A requested call with no reported execution must not say running.");

            var moving = new UISettings().AnimationsEnabled && !new AccessibilitySettings().HighContrast;
            var activity = Descendants(view).OfType<Border>().First(b => Descendants(b).OfType<ActivityLabel>().Any(label => HasText(label, "pending")));
            var activeFrames = await SampleAsync(activity, 8);
            if (moving ? activeFrames < 2 && !Shimmering(activity) : activeFrames != 1 || Shimmering(activity)) throw new Exception($"Single-tool activity rendered {activeFrames} distinct frames; motion enabled={moving}.");
            var approval = Guid.NewGuid();
            fixture.Session.Emit(new ApprovalRequest(fixture.Session.Turn, approval, "Fixture approval", "one"));
            await WaitAsync(() => controller.State == AgentState.WaitingForApproval);
            await WaitAsync(() => ShowsText(view, "awaiting approval"));
            if (Descendants(view).OfType<TextBlock>().Any(t => t.Style == AgentUi.Style("AgentBodyText") && t.Text.Length == 0 && IsVisible(t)))
                throw new Exception("An empty assistant message rendered a blank reply row.");
            if (ShowsText(view, "running") || ShowsText(view, "pending")) throw new Exception("The gated call must say awaiting approval, not running or pending.");
            if (await SampleAsync(activity, 3) != 1 || Shimmering(activity)) throw new Exception("Waiting-for-approval activity kept animating.");
            await controller.RespondAsync(new(approval, true));
            fixture.Session.Emit(new ToolExecuting(fixture.Session.Turn, "one"));
            await WaitAsync(() => ShowsText(view, "running"));
            if (ShowsText(view, "awaiting approval")) throw new Exception("An approved, executing call kept its approval label.");
            fixture.Session.Emit(new ToolCompleted(fixture.Session.Turn, "one", WorkStatus.Succeeded));
            fixture.Session.Emit(new TurnCompleted(fixture.Session.Turn));
            await WaitAsync(() => controller.State == AgentState.Idle);
            await WaitAsync(() => HasText(view, "Done"));
            if (await SampleAsync(activity, 3) != 1 || Shimmering(activity)) throw new Exception("Completed activity kept animating.");
            await controller.SubmitAsync("Stop fixture", new(Environment.CurrentDirectory));
            await controller.InterruptAsync();
            await WaitAsync(() => HasText(view, "Stopped before any reply."));
            if (Descendants(view).OfType<TextBlock>().Count(t => t.Text == "Stopped before any reply." && IsVisible(t)) != 1)
                throw new Exception("Cancellation should show one Stopped outcome, not a duplicate status.");
            await controller.SubmitAsync("Partial stop fixture", new(Environment.CurrentDirectory));
            fixture.Session.Emit(new TextDelta(fixture.Session.Turn, "Keep this partial reply"));
            await WaitAsync(() => HasText(view, "Keep this partial reply"));
            await controller.InterruptAsync();
            await WaitAsync(() => HasText(view, "Stopped. Partial reply kept.") && HasText(view, "Keep this partial reply"));
            await controller.SubmitAsync("Fail fixture", new(Environment.CurrentDirectory));
            fixture.Session.Emit(new TurnFailed(fixture.Session.Turn, "Safe fixture failure"));
            await WaitAsync(() => HasText(view, "Failed") && HasText(view, "Safe fixture failure"));
            if (!Environment.GetCommandLineArgs().Contains("--timeline-only"))
            {
                await RequestLayoutChecks.RunAsync(window);
                _result = await RequestFocusChecks.RunAsync(window);
            }
            Console.WriteLine($"Native single-tool timeline rendered {activeFrames} active frames; waiting and completed frames stayed static. Motion enabled={moving}.");
        }
        catch (Exception error) { Console.Error.WriteLine(error); _result = 1; }
        finally { window.Close(); app.Exit(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static bool HasText(DependencyObject root, string text) => Descendants(root).OfType<TextBlock>().Any(t => t.Text == text && t.Visibility == Visibility.Visible);

    private static bool ShowsText(DependencyObject root, string text) => Descendants(root).OfType<TextBlock>().Any(t => t.Text == text && IsVisible(t));

    private static bool IsVisible(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }

    private static bool Shimmering(FrameworkElement element) =>
        Descendants(element).OfType<TextBlock>().Any(text => ElementCompositionPreview.GetElementChildVisual(text) is not null);

    internal static async Task<int> SampleAsync(FrameworkElement element, int count)
    {
        await Task.Delay(200);
        var frames = new HashSet<string>();
        for (var i = 0; i < count; i++)
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(element);
            var bytes = (await bitmap.GetPixelsAsync()).ToArray();
            if (bytes.Length == 0) throw new Exception("Native timeline did not render.");
            frames.Add(Convert.ToHexString(SHA256.HashData(bytes)));
            await Task.Delay(250);
        }
        return frames.Count;
    }

    internal static async Task WaitAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }

    internal sealed class Harness : IAgentHarness
    {
        public Session Session { get; } = new();
        public Task<HarnessDescription> DescribeAsync(AgentLocation location, CancellationToken cancellationToken) => Task.FromResult(new HarnessDescription(true, "", []));
        public Task<IAgentSession> OpenAsync(AgentOptions options, CancellationToken cancellationToken) => Task.FromResult<IAgentSession>(Session);
    }
    internal sealed class Session : IAgentSession
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();
        public Guid Turn { get; private set; }
        public void Emit(AgentEvent item) => _events.Writer.TryWrite(item);
        public IAsyncEnumerable<AgentEvent> Events(CancellationToken cancellationToken) => _events.Reader.ReadAllAsync(cancellationToken);
        public Task SubmitAsync(AgentPrompt prompt, CancellationToken cancellationToken) { Turn = prompt.Turn; return Task.CompletedTask; }
        public AgentResponse? LastResponse { get; private set; }
        public Task RespondAsync(AgentResponse response, CancellationToken cancellationToken) { LastResponse = response; return Task.CompletedTask; }
        public Task InterruptAsync(Guid turn, CancellationToken cancellationToken) { Emit(new TurnInterrupted(turn)); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
