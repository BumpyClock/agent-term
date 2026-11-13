using AgentTerm.Agents;
using AgentTerm_App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

internal static class TimelinePreferenceChecks
{
    public static async Task RunAsync(Window window)
    {
        var preferences = new Preferences();
        var fixture = new Program.Harness();
        await using var controller = new AgentSessionController(fixture);
        var view = new AgentTimelineView(preferences);
        controller.Changed += () => view.Update(controller, controller.CurrentTask, true);
        var host = new Grid { Children = { view } };
        window.Content = host;
        window.Activate();
        await controller.SubmitAsync("Quiet preference fixture", new(Environment.CurrentDirectory));
        await Program.WaitAsync(() => view.IsLoaded && controller.State == AgentState.Running);
        var ring = Descendants(view).OfType<ProgressRing>().Single();
        var fallback = Descendants(view).OfType<FontIcon>().Single(i => i.Glyph == AgentUi.Glyph(0xE917));
        var rows = view.Children.ToArray();
        await CheckAsync(true);
        preferences.Set(false, false);
        await CheckAsync(false);
        preferences.Set(true, false);
        await CheckAsync(true);
        preferences.Set(true, true);
        await CheckAsync(false);
        preferences.Set(true, false);
        await CheckAsync(true);
        host.Children.Remove(view);
        await Program.WaitAsync(() => !view.IsLoaded && preferences.Subscribers == 0 && !ring.IsActive);
        preferences.Set(false, true);
        host.Children.Add(view);
        await Program.WaitAsync(() => view.IsLoaded);
        await CheckAsync(false);
        if (preferences.Subscribers != 1) throw new Exception("Reload must restore exactly one settings subscription.");
        preferences.Set(true, false);
        await CheckAsync(true);
        if (!rows.SequenceEqual(view.Children)) throw new Exception("Preference changes must preserve coalesced timeline rows.");
        Console.WriteLine("Timeline preference checks passed. Quiet turn, motion and contrast both ways, unload/reload, stable rows.");

        async Task CheckAsync(bool spinning)
        {
            await Program.WaitAsync(() => ring.IsActive == spinning && fallback.Visibility == (spinning ? Visibility.Collapsed : Visibility.Visible));
            if (ring.Visibility != Visibility.Visible) throw new Exception("Quiet work must retain its progress affordance.");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed class Preferences : ITimelineIndicatorPreferences
    {
        public bool Animate { get; private set; } = true;
        public bool HighContrast { get; private set; }
        private Action? _changed;
        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;
        public event Action Changed { add => _changed += value; remove => _changed -= value; }
        public void Set(bool animate, bool highContrast)
        {
            Animate = animate;
            HighContrast = highContrast;
            _changed?.Invoke();
        }
    }
}
