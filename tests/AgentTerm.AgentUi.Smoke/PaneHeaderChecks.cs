using AgentTerm.Agents;
using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

internal static class PaneHeaderChecks
{
    private const double NarrowWidth = 178;
    private const double MidWidth = 360;
    private const double NormalWidth = 718;
    private const double Tolerance = 0.5;

    private sealed record State(string Name, ShellPromptState Prompt, bool EntryUnavailable, AgentState Agent, bool SurfaceOpen, bool Interact)
    {
        public bool TaskShown => !SurfaceOpen && Agent is AgentState.Starting or AgentState.Running or AgentState.Interrupting
            or AgentState.WaitingForApproval or AgentState.WaitingForInput;
    }

    private static readonly State Idle = new("idle", ShellPromptState.Ready, false, AgentState.Idle, false, false);
    private static readonly State Handoff = new("working with input handoff", ShellPromptState.Executing, false, AgentState.Running, false, true);

    private static readonly State[] States =
    [
        Idle,
        new("starting shell", ShellPromptState.Initializing, false, AgentState.Idle, false, false),
        new("no integration", ShellPromptState.Unknown, false, AgentState.Idle, false, false),
        new("Enter handler missing", ShellPromptState.Ready, true, AgentState.Idle, false, false),
        new("agent starting", ShellPromptState.Executing, false, AgentState.Starting, false, false),
        new("working", ShellPromptState.Executing, false, AgentState.Running, false, false),
        new("needs approval", ShellPromptState.Executing, false, AgentState.WaitingForApproval, false, false),
        new("needs an answer", ShellPromptState.Executing, false, AgentState.WaitingForInput, false, false),
        Handoff,
        new("approval with input handoff", ShellPromptState.Executing, false, AgentState.WaitingForApproval, false, true),
        new("surface open with input handoff", ShellPromptState.Executing, false, AgentState.Running, true, true),
        new("shell exited while stopping with handoff", ShellPromptState.Exited, false, AgentState.Interrupting, false, true),
    ];

    internal static async Task RunAsync(Window window)
    {
        var bar = new AgentStatusBar();
        var header = new PaneHeader { Height = 28, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Context = Context() };
        header.AgentBar = bar;
        var raised = new List<string>();
        bar.TaskRequested += () => raised.Add("task");
        bar.InteractRequested += () => raised.Add("interact");
        header.SplitRequested += right => raised.Add(right ? "split right" : "split down");
        header.CloseRequested += () => raised.Add("close");
        window.Content = new Grid { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Children = { header } };
        var size = window.AppWindow.Size;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(size.Width, 1600), Math.Max(size.Height, 300)));
        window.Activate();
        header.Width = NarrowWidth;
        await Program.WaitAsync(() => header.ActualWidth > 0);
        try
        {
            foreach (var width in new[] { NarrowWidth, NormalWidth })
                foreach (var state in States)
                    await CheckAsync(header, bar, state, width, compact: width == NarrowWidth);

            await CheckAsync(header, bar, Idle, MidWidth, compact: false);
            await CheckAsync(header, bar, Handoff, MidWidth, compact: true);
            await CheckAsync(header, bar, Idle, MidWidth, compact: false);
            await CheckAsync(header, bar, Handoff, NarrowWidth, compact: true);
            await CheckAsync(header, bar, Handoff, NormalWidth, compact: false);
            await CheckAsync(header, bar, Handoff, NarrowWidth, compact: true);

            await CheckActionsAsync(header, bar, raised);
        }
        finally { window.AppWindow.Resize(size); }
        Console.WriteLine($"Pane header fit {States.Length} task and input handoff states at {NarrowWidth} and {NormalWidth} DIP, and switched layout both ways at {MidWidth} DIP.");
    }

    private static async Task CheckAsync(PaneHeader header, AgentStatusBar bar, State state, double width, bool compact)
    {
        header.Width = width;
        bar.ShowShellStatus(state.Prompt, state.EntryUnavailable);
        bar.ShowTask(state.Agent, !state.SurfaceOpen, true);
        bar.ShowInteract(state.Interact);
        await Task.Delay(150);
        var where = $"{state.Name} at {width} DIP";
        Check(Math.Abs(header.ActualWidth - width) < Tolerance, $"{where}: the header measured {header.ActualWidth} DIP.");
        Check(header.Compact == compact, $"{where}: expected compact={compact}, got {header.Compact}.");

        Check(bar.FindName("RawTerminalButton") is null, $"{where}: the removed display toggle returned.");
        var task = Named<Button>(bar, "TaskStatusButton");
        var interact = Named<Button>(bar, "InteractButton");
        var menu = Named<Button>(header, "PaneMenuButton");
        var actions = new[] { Named<Button>(header, "SplitRightButton"), Named<Button>(header, "SplitDownButton"), Named<Button>(header, "CloseButton") };

        Check(IsShown(task) == state.TaskShown, $"{where}: task reopen visible={IsShown(task)}, expected {state.TaskShown}.");
        Check(IsShown(interact) == state.Interact, $"{where}: input handoff visible={IsShown(interact)}, expected {state.Interact}.");
        Check(IsShown(menu) == compact && actions.All(action => IsShown(action) != compact), $"{where}: pane actions do not match the layout.");

        if (state.TaskShown) Check(AutomationProperties.GetName(task).EndsWith(". Open the agent task") && AutomationProperties.GetName(task).Length > ". Open the agent task".Length, $"{where}: task reopen has no state in its name.");
        if (state.Interact) Check(AutomationProperties.GetName(interact) == "Type into the agent's running command", $"{where}: input handoff lost its name.");
        Check(AutomationProperties.GetName(menu) == "Pane actions", $"{where}: pane action menu lost its name.");

        var label = Named<FrameworkElement>(bar, "TaskStatusLabelHost");
        var interactLabel = Named<FrameworkElement>(bar, "InteractLabel");
        var status = Named<TextBlock>(bar, "ShellStatus");
        var statusShown = state.Prompt is ShellPromptState.Unknown or ShellPromptState.Initializing or ShellPromptState.Exited || state.EntryUnavailable;
        Check(IsShown(status) == statusShown, $"{where}: shell status must remain visible in both layouts.");
        if (statusShown)
        {
            var detail = AutomationProperties.GetName(status);
            Check(detail.Length > 0 && Equals(ToolTipService.GetToolTip(status), detail), $"{where}: shell status needs its full accessible explanation and tooltip.");
            if (state.Prompt == ShellPromptState.Unknown || state.EntryUnavailable)
                Check(detail.Contains("/agent lines run as ordinary commands"), $"{where}: missing /agent integration needs an explanation.");
        }
        if (!compact)
        {
            Check(!state.TaskShown || IsShown(label), $"{where}: the full layout must keep the task label.");
            Check(!state.Interact || IsShown(interactLabel), $"{where}: the full layout must keep the input handoff label.");
            Check(!statusShown || status.Text.Length > 1, $"{where}: the full layout must keep the shell status text.");
        }

        var root = (FrameworkElement)header.Content;
        var right = width - ((Grid)root).Padding.Right;
        var controls = new List<FrameworkElement>();
        if (IsShown(task)) controls.Add(task);
        if (IsShown(interact)) controls.Add(interact);
        controls.AddRange(compact ? new[] { menu } : actions);
        var bounds = controls.Select(control => (Control: control, Rect: Bounds(control, header))).OrderBy(item => item.Rect.X).ToArray();
        foreach (var (control, rect) in bounds)
        {
            var id = AutomationProperties.GetName(control);
            Check(rect.Width > 0 && rect.Height > 0, $"{where}: \"{id}\" has no size.");
            Check(rect.X >= -Tolerance && rect.Right <= right + Tolerance, $"{where}: \"{id}\" spans {rect.X:F1}..{rect.Right:F1}, outside 0..{right:F1}.");
            Check(rect.Width + Tolerance >= control.DesiredSize.Width, $"{where}: \"{id}\" is clipped to {rect.Width:F1} of {control.DesiredSize.Width:F1} DIP.");
            Check(control is Control { IsEnabled: true, IsTabStop: true }, $"{where}: \"{id}\" is not keyboard reachable.");
        }
        for (var i = 1; i < bounds.Length; i++)
            Check(bounds[i - 1].Rect.Right <= bounds[i].Rect.X + Tolerance, $"{where}: controls overlap at {bounds[i].Rect.X:F1}.");

        var context = Named<FrameworkElement>(header, "ContextHost");
        var contextRect = Bounds(context, header);
        Check(contextRect.Right <= bounds[0].Rect.X + Tolerance, $"{where}: the pane context overlaps the controls.");
        var minimum = compact ? 13 : PaneHeader.ContextMinimum;
        Check(contextRect.Width + Tolerance >= minimum, $"{where}: the pane context kept {contextRect.Width:F1} DIP, expected at least {minimum}.");

        var before = header.Compact;
        await Task.Delay(120);
        Check(header.Compact == before, $"{where}: the layout did not settle.");
    }

    private static async Task CheckActionsAsync(PaneHeader header, AgentStatusBar bar, List<string> raised)
    {
        Check(header.Compact, "The action check needs the compact layout.");
        raised.Clear();
        foreach (var id in new[] { "TaskStatusButton", "InteractButton" })
            new ButtonAutomationPeer(Named<Button>(bar, id)).Invoke();
        var menuButton = Named<Button>(header, "PaneMenuButton");
        var menu = (MenuFlyout)menuButton.Flyout;
        var names = menu.Items.OfType<MenuFlyoutItem>().Select(item => item.Text).ToArray();
        Check(names.SequenceEqual(new[] { "Split pane right", "Split pane down", "Close pane" }), $"The pane action menu lists {string.Join(", ", names)}.");
        foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
        {
            menu.ShowAt(menuButton);
            await Task.Delay(150);
            new MenuFlyoutItemAutomationPeer(item).Invoke();
            await Task.Delay(150);
        }
        menu.Hide();
        Check(raised.SequenceEqual(new[] { "task", "interact", "split right", "split down", "close" }), $"Compact actions raised {string.Join(", ", raised)}.");
    }

    private static Grid Context()
    {
        var context = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        context.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        context.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        context.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        context.Children.Add(new Border { Width = 13, Height = 12, BorderThickness = new(1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray) });
        var shell = new TextBlock { Text = "PowerShell", MaxWidth = 110, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(shell, 1);
        context.Children.Add(shell);
        var directory = new TextBlock { Text = @"C:\Users\fixture\Projects\a-very-long-repository-name\src\deeply\nested\folder", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(directory, 2);
        context.Children.Add(directory);
        return context;
    }

    private static T Named<T>(FrameworkElement scope, string name) where T : class =>
        scope.FindName(name) as T ?? throw new InvalidOperationException($"Missing {name}.");

    private static Rect Bounds(FrameworkElement element, UIElement root) =>
        element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static bool IsShown(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
