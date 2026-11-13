using Microsoft.UI.Xaml;

namespace AgentTerm_App;

public partial class App : Application
{
    internal static AgentTerm.Agents.IAgentHarness AgentHarness { get; } = new AgentTerm.Copilot.CopilotHarness();
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        PerformanceCapture.Start(_window.DispatcherQueue);
        _window.Closed += (_, _) => PerformanceCapture.Stop();
        _window.Activate();
    }
}
