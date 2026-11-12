using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Threading.Channels;
using AgentTerm.Agents;
using AgentTerm_App;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
            ProgramStatusUiChecks.Run();
            if (Environment.GetCommandLineArgs().Contains("--status-layout"))
            {
                await ProgramStatusUiChecks.CheckLayoutAsync(window);
                return;
            }
            await ProgramStatusUiChecks.CheckDetailsAsync(window);
            await ProgramStatusUiChecks.CheckIconsAsync(window);
            var fixture = new Harness();
            await using var controller = new AgentSessionController(fixture);
            var view = new AgentTimelineView { Width = 480, HorizontalAlignment = HorizontalAlignment.Left };
            controller.Changed += () => view.Update(controller, controller.CurrentTask, true);
            window.Content = new Grid { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Children = { view } };
            window.Activate();
            await controller.SubmitAsync("Run a fixture", new(Environment.CurrentDirectory));
            fixture.Session.Emit(new ToolStarted(fixture.Session.Turn, "one", "terminal", ToolCategory.Command, "fixture", "fixture command"));
            await WaitAsync(() => controller.Timeline.Entries.OfType<TimelineWorkGroup>().Any());

            var moving = new UISettings().AnimationsEnabled && !new AccessibilitySettings().HighContrast;
            var activeFrames = await SampleAsync(view, 8);
            if (moving ? activeFrames < 2 : activeFrames != 1) throw new Exception($"Single-tool activity rendered {activeFrames} distinct frames; motion enabled={moving}.");
            var approval = Guid.NewGuid();
            fixture.Session.Emit(new ApprovalRequest(fixture.Session.Turn, approval, "Fixture approval"));
            await WaitAsync(() => controller.State == AgentState.WaitingForApproval);
            if (await SampleAsync(view, 3) != 1) throw new Exception("Waiting-for-approval activity kept animating.");
            await controller.RespondAsync(new(approval, true));
            fixture.Session.Emit(new ToolCompleted(fixture.Session.Turn, "one", WorkStatus.Succeeded));
            fixture.Session.Emit(new TurnCompleted(fixture.Session.Turn));
            await WaitAsync(() => controller.State == AgentState.Idle);
            if (await SampleAsync(view, 3) != 1) throw new Exception("Completed activity kept animating.");
            Console.WriteLine($"Native single-tool timeline rendered {activeFrames} active frames; waiting and completed frames stayed static. Motion enabled={moving}.");
        }
        catch (Exception error) { Console.Error.WriteLine(error); _result = 1; }
        finally { window.Close(); app.Exit(); }
    }

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

    private static async Task WaitAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Harness : IAgentHarness
    {
        public Session Session { get; } = new();
        public Task<HarnessDescription> DescribeAsync(AgentLocation location, CancellationToken cancellationToken) => Task.FromResult(new HarnessDescription(true, "", []));
        public Task<IAgentSession> OpenAsync(AgentOptions options, CancellationToken cancellationToken) => Task.FromResult<IAgentSession>(Session);
    }
    private sealed class Session : IAgentSession
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();
        public Guid Turn { get; private set; }
        public void Emit(AgentEvent item) => _events.Writer.TryWrite(item);
        public IAsyncEnumerable<AgentEvent> Events(CancellationToken cancellationToken) => _events.Reader.ReadAllAsync(cancellationToken);
        public Task SubmitAsync(AgentPrompt prompt, CancellationToken cancellationToken) { Turn = prompt.Turn; return Task.CompletedTask; }
        public Task RespondAsync(AgentResponse response, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task InterruptAsync(Guid turn, CancellationToken cancellationToken) { Emit(new TurnInterrupted(turn)); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
