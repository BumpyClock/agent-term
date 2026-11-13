using System.Numerics;
using AgentTerm.Agents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.ViewManagement;

namespace AgentTerm_App;

internal static class AgentUi
{
    public const double Gutter = 22;
    public const int OutputLines = 12;
    public static readonly string Chevron = Glyph(0xE76C), Check = Glyph(0xE73E), Cross = Glyph(0xE711), Prompt = Glyph(0xE756),
        File = Glyph(0xE8A5), Edit = Glyph(0xE70F), Tool = Glyph(0xE90F), Idea = Glyph(0xE82F), Info = Glyph(0xE946),
        Stop = Glyph(0xE73B), CopyGlyph = Glyph(0xE8C8), Person = Glyph(0xE77B), Agent = Glyph(0xE99A), Shield = Glyph(0xEA18);
    private static readonly UISettings Settings = new();
    private static readonly AccessibilitySettings Accessibility = new();
    public static string Glyph(int code) => ((char)code).ToString();
    public static Style Style(string key) => (Style)Application.Current.Resources[key];
    public static FontFamily Symbols => (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"];
    public static bool Animate => Settings.AnimationsEnabled;
    public static bool HighContrast => Accessibility.HighContrast;
    public static string TaskStatus(AgentSessionController controller, AgentTask? task) => controller.RunningTask == task && task is not null
        ? controller.State switch
        {
            AgentState.Starting => "Starting",
            AgentState.WaitingForApproval => "Needs approval",
            AgentState.WaitingForInput => "Needs an answer",
            AgentState.Interrupting => "Stopping",
            _ => "Working",
        }
        : task?.LastOutcome switch
        {
            WorkStatus.Succeeded => "Done",
            WorkStatus.Cancelled => "Stopped",
            WorkStatus.Failed => "Failed",
            _ => "",
        };
    public static FontIcon Icon(string glyph, string style = "AgentIcon")
    {
        var icon = new FontIcon { Glyph = glyph, FontFamily = Symbols, Style = Style(style), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        return icon;
    }
    public static TextBlock Text(string style, string text = "") => new() { Style = Style(style), Text = text };
    public static Grid Row(UIElement? gutter, UIElement content, UIElement? trailing = null)
    {
        var row = new Grid { ColumnSpacing = 6, ColumnDefinitions = { new() { Width = new GridLength(Gutter - 6) }, new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } } };
        if (gutter is FrameworkElement g) { g.HorizontalAlignment = HorizontalAlignment.Center; g.VerticalAlignment = VerticalAlignment.Top; g.Margin = new Thickness(0, 4, 0, 0); row.Children.Add(g); }
        Grid.SetColumn((FrameworkElement)content, 1); row.Children.Add(content);
        if (trailing is not null) { Grid.SetColumn((FrameworkElement)trailing, 2); row.Children.Add(trailing); }
        return row;
    }
    public static void Copy(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }
    public static string FirstLine(string text, out string rest)
    {
        var split = text.IndexOf('\n');
        rest = split < 0 ? "" : text[(split + 1)..].Trim();
        return (split < 0 ? text : text[..split]).Trim();
    }
}

internal sealed class DisclosureButton : Button
{
    private bool _expanded;
    public FontIcon Chevron { get; } = AgentUi.Icon(AgentUi.Chevron);
    public event Action<bool>? Toggled;
    public DisclosureButton()
    {
        Style = AgentUi.Style("AgentRowButtonStyle");
        Chevron.FontSize = 10; Chevron.Width = Chevron.Height = 12; Chevron.CenterPoint = new Vector3(6, 6, 0);
        if (AgentUi.Animate) Chevron.RotationTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) };
        Click += (_, _) => Toggle();
    }
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            Chevron.Rotation = value ? 90 : 0;
            (FrameworkElementAutomationPeer.FromElement(this) as Peer)?.Raise(!value, value);
        }
    }
    public void Toggle() { IsExpanded = !IsExpanded; Toggled?.Invoke(IsExpanded); }
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);
    private sealed class Peer(DisclosureButton owner) : ButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        public ExpandCollapseState ExpandCollapseState => owner.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        public void Expand() { if (!owner.IsExpanded) owner.Toggle(); }
        public void Collapse() { if (owner.IsExpanded) owner.Toggle(); }
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.ExpandCollapse ? this : base.GetPatternCore(pattern);
        public void Raise(bool wasExpanded, bool expanded) => RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
            wasExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed, expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
    }
}

internal interface ITimelineIndicatorPreferences
{
    bool Animate { get; }
    bool HighContrast { get; }
    event Action Changed;
}

internal sealed class TimelineIndicatorPreferences : ITimelineIndicatorPreferences
{
    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private Action? _changed;
    public bool Animate => _settings.AnimationsEnabled;
    public bool HighContrast => _accessibility.HighContrast;
    public event Action Changed
    {
        add
        {
            if (_changed is null)
            {
                _settings.AnimationsEnabledChanged += SettingsChanged;
                // ColorValuesChanged supports unpackaged hosts where HighContrastChanged throws.
                _settings.ColorValuesChanged += SettingsChanged;
            }
            _changed += value;
        }
        remove
        {
            _changed -= value;
            if (_changed is null)
            {
                _settings.AnimationsEnabledChanged -= SettingsChanged;
                _settings.ColorValuesChanged -= SettingsChanged;
            }
        }
    }
    private void SettingsChanged(UISettings sender, object args) => _changed?.Invoke();
}

internal sealed class AgentTimelineView : StackPanel
{
    private readonly Dictionary<Guid, IRow> _rows = [];
    private readonly List<IRow> _ordered = [];
    private readonly List<TimelineEntry> _entries = [];
    private readonly ActivityLabel _activity = new();
    private readonly ProgressRing _progress = new() { Width = 20, Height = 20, MinWidth = 20, MinHeight = 20 };
    private readonly FontIcon _staticProgress = AgentUi.Icon(AgentUi.Glyph(0xE917));
    private readonly Grid _activityRow;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _elapsedTimer;
    private AgentSessionController? _controller;
    private bool _active;
    private bool _working;
    private (bool Trimmed, bool Activity)? _chrome;
    private readonly ITimelineIndicatorPreferences _preferences;
    private bool _loaded;
    private readonly TextBlock _trimmed = AgentUi.Text("AgentHintText", "Earlier activity in this pane was trimmed.");
    private AgentTask? _task;
    public FontFamily Mono { get; set; } = new("Cascadia Mono, Consolas");

    public AgentTimelineView() : this(new TimelineIndicatorPreferences()) { }

    internal AgentTimelineView(ITimelineIndicatorPreferences preferences)
    {
        _preferences = preferences;
        Spacing = 6;
        var indicator = new Grid { Children = { _progress, _staticProgress } };
        _activityRow = AgentUi.Row(indicator, _activity);
        _elapsedTimer = DispatcherQueue.CreateTimer();
        _elapsedTimer.Interval = TimeSpan.FromSeconds(1);
        _elapsedTimer.Tick += (_, _) => { if (_controller is { } controller) _activity.Update(Status(controller, _task), _working && _active); };
        Loaded += (_, _) =>
        {
            _loaded = true;
            _preferences.Changed += PreferencesChanged;
            if (_controller is { } controller) Update(controller, _task, _active);
            else UpdateIndicatorPolicy();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            _preferences.Changed -= PreferencesChanged;
            _elapsedTimer.Stop();
            UpdateIndicatorPolicy();
            _chrome = null;
        };
        _trimmed.Margin = new Thickness(AgentUi.Gutter, 0, 0, 0);
        Children.Add(_trimmed); Children.Add(_activityRow);
        AutomationProperties.SetName(this, "Agent conversation");
    }

    public void Update(AgentSessionController controller, AgentTask? task, bool active)
    {
        _controller = controller;
        _active = active;
        if (task != _task)
        {
            foreach (var row in _ordered) Children.Remove(row.Element);
            _rows.Clear();
            _ordered.Clear();
            _task = task;
        }
        _entries.Clear();
        foreach (var entry in controller.Timeline.Entries) if (task?.Contains(entry.Turn) == true) _entries.Add(entry);
        var trimmed = controller.Timeline.HasTrimmed && task is not null && task.Turns.Count > 0 && !controller.Timeline.Entries.Any(e => e.Turn == task.Turns[0]);
        if (_entries.Count(e => _rows.ContainsKey(e.Id)) != _rows.Count)
        {
            var retained = _entries.Select(e => e.Id).ToHashSet();
            foreach (var id in _rows.Keys.Where(id => !retained.Contains(id)).ToArray()) { Children.Remove(_rows[id].Element); _ordered.Remove(_rows[id]); _rows.Remove(id); }
        }
        var running = controller.State is AgentState.Running or AgentState.Starting && controller.RunningTask == task;
        for (var index = 0; index < _entries.Count; index++)
        {
            var entry = _entries[index];
            if (!_rows.TryGetValue(entry.Id, out var row))
            {
                row = entry switch
                {
                    TimelineWorkGroup group => new WorkRow(group, Mono, AwaitsApproval),
                    TimelineMessage { Role: MessageRole.User } message => new RequestRow(message, Mono),
                    TimelineMessage { Role: MessageRole.Assistant } message => new AnswerRow(message, Mono),
                    TimelineMessage message => new OutcomeRow(message, Mono),
                    _ => throw new InvalidOperationException("Unknown timeline entry."),
                };
                _rows.Add(entry.Id, row);
                _ordered.Insert(index, row);
                Children.Insert(index + 1, row.Element);
            }
            else if (_ordered[index] != row)
            {
                _ordered.Remove(row); _ordered.Insert(index, row);
                Children.Remove(row.Element); Children.Insert(index + 1, row.Element);
            }
            row.Update(active && running, index == _entries.Count - 1);
        }
        var toolsRunning = _entries.OfType<TimelineWorkGroup>().Any(g => g.Active);
        var thinking = _entries.LastOrDefault() is TimelineWorkGroup { Reasoning: not null, Tools.Count: 0 };
        _working = running && !toolsRunning && !thinking;
        var status = Status(controller, task);
        if (controller.HasActiveTurn && controller.RunningTask == task && active && IsLoaded && controller.State is AgentState.Starting or AgentState.Running or AgentState.Interrupting) _elapsedTimer.Start(); else _elapsedTimer.Stop();
        var cancellationShown = task?.LastOutcome == WorkStatus.Cancelled && controller.RunningTask != task
            && _entries.OfType<TimelineMessage>().Any(m => m.Role == MessageRole.Cancelled);
        var activity = status.Length > 0 && !cancellationShown;
        if (_chrome != (trimmed, activity))
        {
            _chrome = (trimmed, activity);
            _trimmed.Visibility = trimmed ? Visibility.Visible : Visibility.Collapsed;
            _activityRow.Visibility = activity ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateIndicatorPolicy();
        _activity.Update(status, _working && active);
    }

    private void PreferencesChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_loaded) UpdateIndicatorPolicy();
    });

    private void UpdateIndicatorPolicy()
    {
        var spinning = _working && _active && _loaded && _preferences.Animate && !_preferences.HighContrast;
        _progress.Visibility = _working ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsActive = spinning;
        _staticProgress.Visibility = _working && !spinning ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Status(AgentSessionController controller, AgentTask? task)
    {
        var status = AgentUi.TaskStatus(controller, task);
        return controller.HasActiveTurn && controller.RunningTask == task && controller.TurnElapsed.TotalSeconds >= 3
            ? status + " · " + controller.TurnElapsed.ToString(@"m\:ss") : status;
    }

    private bool AwaitsApproval(TimelineTool tool) => _controller?.AwaitsApproval(tool.CallId) == true;

    internal static string ToolPhase(TimelineTool tool, bool awaitingApproval) => tool.Status switch
    {
        WorkStatus.Running when awaitingApproval => "awaiting approval",
        WorkStatus.Running => tool.Executing ? "running" : "pending",
        WorkStatus.Failed => "failed",
        WorkStatus.Cancelled => "stopped",
        _ => "done",
    };

    private interface IRow
    {
        FrameworkElement Element { get; }
        void Update(bool live, bool last);
    }

    private static TextBlock MonoText(string style, FontFamily font, string text = "")
    {
        var block = AgentUi.Text(style, text);
        block.FontFamily = font;
        return block;
    }

    private sealed class RequestRow(TimelineMessage message, FontFamily font) : IRow
    {
        private readonly TextBlock _text = MonoText("AgentRequestText", font);
        private string? _shown;
        private FrameworkElement? _element;
        public FrameworkElement Element => _element ??= Build();
        private Grid Build()
        {
            var row = AgentUi.Row(AgentUi.Icon(AgentUi.Person), _text);
            row.Margin = new Thickness(0, 8, 0, 0);
            return row;
        }
        public void Update(bool live, bool last)
        {
            _ = Element;
            if (_shown == message.Text) return;
            _text.Text = _shown = message.Text;
            AutomationProperties.SetName(_text, "You asked: " + _shown);
        }
    }

    private sealed class AnswerRow(TimelineMessage message, FontFamily font) : IRow
    {
        private readonly TextBlock _text = MonoText("AgentBodyText", font);
        private string? _shown;
        private FrameworkElement? _element;
        public FrameworkElement Element => _element ??= AgentUi.Row(AgentUi.Icon(AgentUi.Agent), _text);
        public void Update(bool live, bool last)
        {
            _ = Element;
            if (_shown != message.Text) _text.Text = _shown = message.Text;
        }
    }

    private sealed class OutcomeRow(TimelineMessage message, FontFamily font) : IRow
    {
        private readonly TextBlock _label = AgentUi.Text("AgentLabelText");
        private readonly TextBlock _detail = MonoText("AgentDetailText", font);
        private string? _shown;
        private FrameworkElement? _element;
        public FrameworkElement Element => _element ??= Build();
        private Grid Build()
        {
            var critical = message.Role == MessageRole.Failure;
            var label = AgentUi.FirstLine(message.Text, out _);
            var icon = message.Role switch
            {
                MessageRole.Failure => AgentUi.Icon(AgentUi.Cross, "AgentCriticalIcon"),
                MessageRole.Cancelled => AgentUi.Icon(AgentUi.Stop),
                _ => AgentUi.Icon(AgentUi.Info),
            };
            if (critical) _label.Style = AgentUi.Style("AgentCriticalText");
            _label.TextWrapping = TextWrapping.Wrap;
            _detail.MaxLines = 4;
            var stack = new StackPanel { Spacing = 2, Children = { _label, _detail } };
            return AgentUi.Row(icon, stack);
        }
        public void Update(bool live, bool last)
        {
            _ = Element;
            if (_shown == message.Text) return;
            _shown = message.Text;
            var label = AgentUi.FirstLine(message.Text, out var detail);
            _label.Text = label;
            _detail.Text = detail;
            _detail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(_detail, detail.Length > 0 ? detail : null);
            AutomationProperties.SetName(_label, (message.Role == MessageRole.Failure ? "Failed: " : "") + label);
        }
    }

    private sealed class WorkRow(TimelineWorkGroup group, FontFamily font, Func<TimelineTool, bool> awaitsApproval) : IRow
    {
        private readonly StackPanel _root = new() { Spacing = 4 };
        private readonly DisclosureButton _header = new();
        private readonly FontIcon _icon = AgentUi.Icon(AgentUi.Tool);
        private readonly ActivityLabel _label = new();
        private readonly FontIcon _failed = AgentUi.Icon(AgentUi.Cross, "AgentCriticalIcon");
        private readonly StackPanel _details = new() { Spacing = 4, Margin = new Thickness(AgentUi.Gutter, 0, 0, 0) };
        private readonly TextBlock _reasoning = AgentUi.Text("AgentDetailText");
        private readonly Dictionary<string, ToolCard> _cards = [];
        private (bool Live, bool Last, string? Reasoning)? _shown;
        private string _reasoningShown = "";
        private bool _built;
        public FrameworkElement Element { get { Build(); return _root; } }
        private void Build()
        {
            if (_built) return;
            _built = true;
            var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { _failed, _header.Chevron } };
            _header.Content = AgentUi.Row(_icon, _label, trailing);
            _header.IsExpanded = group.Expanded;
            _header.Toggled += expanded => { group.Expanded = expanded; Layout(); };
            _details.Children.Add(_reasoning);
            _root.Children.Add(_header); _root.Children.Add(_details);
        }
        public void Update(bool live, bool last)
        {
            Build();
            var changed = _shown != (live, last, group.Reasoning);
            foreach (var tool in group.Tools)
            {
                if (!_cards.TryGetValue(tool.CallId, out var card)) { card = new ToolCard(tool, font, awaitsApproval); _cards.Add(tool.CallId, card); _details.Children.Add(card.Element); changed = true; }
                changed |= card.Sync(live);
            }
            if (!changed) return;
            _shown = (live, last, group.Reasoning);
            var reasoningOnly = group.Tools.Count == 0;
            var thinking = live && reasoningOnly && last;
            var gated = group.Tools.LastOrDefault(t => t.Status == WorkStatus.Running && awaitsApproval(t));
            var label = reasoningOnly ? (thinking ? "Thinking" : "Thought") : gated is not null ? $"Awaiting approval for {gated.Action}" : group.Summary;
            _label.Update(label, thinking || (live && group.Active && group.Tools.Count > 1));
            _icon.Glyph = reasoningOnly ? AgentUi.Idea : group.Tools.All(t => t.Category == ToolCategory.Command) ? AgentUi.Prompt
                : group.Tools.All(t => t.Category == ToolCategory.ReadFile) ? AgentUi.File : group.Tools.All(t => t.Category == ToolCategory.WriteFile) ? AgentUi.Edit : AgentUi.Tool;
            var failed = group.Tools.Any(t => t.Status == WorkStatus.Failed);
            _failed.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(_header, label + (failed ? ", includes a failed call" : ""));
            if (_reasoningShown != (group.Reasoning ?? "")) _reasoning.Text = _reasoningShown = group.Reasoning ?? "";
            _reasoning.Visibility = group.Reasoning is null ? Visibility.Collapsed : Visibility.Visible;
            Layout();
        }
        private void Layout()
        {
            var single = group.Tools.Count == 1 && group.Reasoning is null;
            _header.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
            _details.Margin = new Thickness(AgentUi.Gutter, 0, 0, 0);
            _details.Visibility = single || group.Expanded ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private sealed class ToolCard
    {
        private readonly TimelineTool _tool;
        private readonly Func<TimelineTool, bool> _awaitsApproval;
        private readonly Border _card = new() { Style = AgentUi.Style("AgentCard") };
        private readonly DisclosureButton _header = new();
        private readonly Grid _status = new() { Width = 14 };
        private readonly FontIcon _statusIcon = AgentUi.Icon(AgentUi.Check, "AgentSuccessIcon");
        private readonly TextBlock _command;
        private readonly ActivityLabel _runningHint = new();
        private readonly StackPanel _body = new() { Spacing = 6, Padding = new Thickness(AgentUi.Gutter + 8, 0, 10, 10) };
        private readonly TextBlock _fullCommand;
        private readonly TextBlock _output;
        private readonly Button _more = new() { Style = AgentUi.Style("AgentQuietButtonStyle"), FontSize = 12 };
        private bool _showAll;
        private bool _live;
        private (bool Live, WorkStatus Status, bool Executing, string Output, bool Awaiting)? _synced;
        private string? _outputShown;
        public FrameworkElement Element => _card;
        public ToolCard(TimelineTool tool, FontFamily font, Func<TimelineTool, bool> awaitsApproval)
        {
            _tool = tool;
            _awaitsApproval = awaitsApproval;
            _command = MonoText("AgentHintText", font);
            _fullCommand = MonoText("AgentDetailText", font);
            _output = MonoText("AgentDetailText", font);
            _status.Children.Add(_statusIcon);
            var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { _runningHint, _header.Chevron } };
            var header = AgentUi.Row(_status, _command, trailing);
            header.Margin = new Thickness(8, 4, 8, 4);
            ((FrameworkElement)header.Children[0]).Margin = new Thickness(0, 2, 0, 0);
            _header.Content = header;
            _header.Padding = new Thickness(0);
            _header.IsExpanded = tool.OutputExpanded;
            _header.Toggled += expanded => { tool.OutputExpanded = expanded; Refresh(); };
            var copy = new Button { Style = AgentUi.Style("AgentQuietButtonStyle"), FontSize = 12, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { AgentUi.Icon(AgentUi.CopyGlyph), new TextBlock { Text = "Copy output" } } } };
            copy.Click += (_, _) => AgentUi.Copy(tool.Output);
            _more.Click += (_, _) => { _showAll = !_showAll; Refresh(); };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(-4, 0, 0, 0), Children = { _more, copy } };
            _body.Children.Add(_fullCommand); _body.Children.Add(_output); _body.Children.Add(actions);
            var menu = new MenuFlyout();
            var copyCommand = new MenuFlyoutItem { Text = "Copy command" }; copyCommand.Click += (_, _) => AgentUi.Copy(tool.Command.Length > 0 ? tool.Command : tool.Action);
            var copyOutput = new MenuFlyoutItem { Text = "Copy output" }; copyOutput.Click += (_, _) => AgentUi.Copy(tool.Output);
            menu.Items.Add(copyCommand); menu.Items.Add(copyOutput);
            _header.ContextFlyout = menu;
            _card.Child = new StackPanel { Children = { _header, _body } };
        }
        public bool Sync(bool live)
        {
            var state = (live, _tool.Status, _tool.Executing, _tool.Output, _awaitsApproval(_tool));
            if (_synced == state) return false;
            _synced = state;
            _live = live;
            Refresh();
            return true;
        }
        private void Refresh()
        {
            var command = _tool.Command.Length > 0 ? _tool.Command : _tool.Action;
            var line = AgentUi.FirstLine(command, out var more);
            if (_command.Text != line) _command.Text = line;
            ToolTipService.SetToolTip(_command, command);
            var running = _tool.Status == WorkStatus.Running;
            _statusIcon.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            _statusIcon.Glyph = _tool.Status == WorkStatus.Succeeded ? AgentUi.Check : _tool.Status == WorkStatus.Failed ? AgentUi.Cross : AgentUi.Stop;
            _statusIcon.Style = AgentUi.Style(_tool.Status switch { WorkStatus.Succeeded => "AgentSuccessIcon", WorkStatus.Failed => "AgentCriticalIcon", _ => "AgentIcon" });
            var awaiting = _awaitsApproval(_tool);
            var status = ToolPhase(_tool, awaiting);
            var hint = status == "done" ? "" : status;
            _runningHint.Update(hint, running && !awaiting && _live);
            _runningHint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(_header, $"{line}, {status}");
            var hasBody = _tool.Output.Length > 0 || more.Length > 0 || line.Length < command.Length;
            _header.Chevron.Visibility = hasBody ? Visibility.Visible : Visibility.Collapsed;
            _body.Visibility = hasBody && _tool.OutputExpanded ? Visibility.Visible : Visibility.Collapsed;
            if (_body.Visibility == Visibility.Collapsed) return;
            _fullCommand.Text = command;
            _fullCommand.Visibility = command.Contains('\n') || command.Length > 80 ? Visibility.Visible : Visibility.Collapsed;
            if (_outputShown != _tool.Output) _output.Text = _outputShown = _tool.Output;
            _output.Visibility = _tool.Output.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var lines = _tool.Output.Count(c => c == '\n') + 1;
            var clamped = lines > AgentUi.OutputLines || _tool.Output.Length > AgentUi.OutputLines * 120;
            _output.MaxLines = _showAll ? 0 : AgentUi.OutputLines;
            _more.Visibility = clamped ? Visibility.Visible : Visibility.Collapsed;
            _more.Content = _showAll ? "Show less" : $"Show all {lines} lines";
        }
    }
}
