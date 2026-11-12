namespace AgentUiSmoke;

public partial class App : Microsoft.UI.Xaml.Application
{
    public App() => InitializeComponent();
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args) => Program.RunAsync(this);
}
