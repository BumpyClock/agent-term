using System;
using AgentTerm.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AgentTerm_App;

/// <summary>
/// Settings for what programs may ask the terminal to do: clipboard, bell, notifications, progress, and window resize.
/// It edits a draft and raises <see cref="Changed"/>; the settings window saves it with the other pages.
/// </summary>
internal sealed class EffectsSettingsPanel : StackPanel
{
    private readonly Style _card, _label, _description;
    private readonly ComboBox _write, _read, _limit, _bell, _notifications;
    private readonly ToggleSwitch _progress, _resize;
    private bool _loading;

    internal EffectsSettingsPanel(Style card, Style label, Style description)
    {
        _card = card;
        _label = label;
        _description = description;
        Spacing = 8;

        _write = Choices("Clipboard writes", ("Allow", ClipboardWritePolicy.Allow), ("Ask first", ClipboardWritePolicy.Ask), ("Block", ClipboardWritePolicy.Deny));
        _read = Choices("Clipboard reads", ("Block", ClipboardReadPolicy.Deny), ("Allow", ClipboardReadPolicy.Allow));
        _limit = Choices("Clipboard size limit", ("64 KiB", 64 * 1024), ("256 KiB", 256 * 1024), ("1 MiB", 1024 * 1024), ("4 MiB", 4 * 1024 * 1024), ("8 MiB", 8 * 1024 * 1024));
        _bell = Choices("Bell", ("Off", BellStyle.None), ("Flash the pane", BellStyle.Visual), ("Play a sound", BellStyle.Audible), ("Sound and flash", BellStyle.Both));
        _notifications = Choices("Notifications", ("Off", NotificationStyle.Off), ("In AgentTerm", NotificationStyle.InApp),
            ("In AgentTerm and Windows", NotificationStyle.InAppAndSystem));
        _progress = Toggle("Show progress");
        _resize = Toggle("Allow window resize requests");

        Children.Add(Row("Clipboard writes",
            "Let a program replace your clipboard through OSC 52 or the Kitty clipboard protocol, for example to copy from a remote editor. Ask first holds the text until you approve it.", _write));
        Children.Add(Row("Clipboard reads",
            "Let a program read your clipboard text. Off by default because it exposes anything you copied, including passwords. Pasting into a program is always allowed.", _read));
        Children.Add(Row("Clipboard size limit", "Largest clipboard text a program may write or read. Bigger requests are refused.", _limit));
        Children.Add(Row("Bell", "What happens when a program rings the terminal bell. A background window also flashes its taskbar button.", _bell));
        Children.Add(Row("Notifications", "Desktop notification requests from programs (OSC 9 and OSC 777). Windows notifications appear only while AgentTerm is in the background.", _notifications));
        Children.Add(Row("Progress", "Show progress a program reports (OSC 9;4) on the pane, the tab, and the taskbar button.", _progress));
        Children.Add(Row("Window resize requests", "Let a program resize the window to a number of rows and columns. Off by default because it can be disruptive.", _resize));
    }

    internal event Action? Changed;

    internal TerminalEffectOptions Options => new()
    {
        ClipboardWrite = Selected(_write, ClipboardWritePolicy.Allow),
        ClipboardRead = Selected(_read, ClipboardReadPolicy.Deny),
        ClipboardMaxBytes = Selected(_limit, TerminalEffectOptions.DefaultClipboardBytes),
        Bell = Selected(_bell, BellStyle.Visual),
        Notifications = Selected(_notifications, NotificationStyle.InAppAndSystem),
        Progress = _progress.IsOn,
        AllowWindowResize = _resize.IsOn,
    };

    internal void Load(TerminalEffectOptions options)
    {
        _loading = true;
        Select(_write, options.ClipboardWrite);
        Select(_read, options.ClipboardRead);
        if (!_limit.Items.OfType<ComboBoxItem>().Any(item => item.Tag is int bytes && bytes == options.ClipboardMaxBytes))
            _limit.Items.Add(new ComboBoxItem { Content = $"Custom ({options.ClipboardMaxBytes:N0} bytes)", Tag = options.ClipboardMaxBytes });
        Select(_limit, options.ClipboardMaxBytes);
        Select(_bell, options.Bell);
        Select(_notifications, options.Notifications);
        _progress.IsOn = options.Progress;
        _resize.IsOn = options.AllowWindowResize;
        _loading = false;
    }

    private ComboBox Choices<T>(string name, params (string Label, T Value)[] values) where T : notnull
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, name);
        foreach (var (text, value) in values) box.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        box.SelectionChanged += (_, _) => { if (!_loading) Changed?.Invoke(); };
        return box;
    }

    private ToggleSwitch Toggle(string name)
    {
        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, name);
        toggle.Toggled += (_, _) => { if (!_loading) Changed?.Invoke(); };
        return toggle;
    }

    private Border Row(string title, string description, FrameworkElement control)
    {
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = _label });
        text.Children.Add(new TextBlock { Text = description, Style = _description });
        var grid = new Grid { ColumnSpacing = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(236) });
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return new Border { Style = _card, Child = grid };
    }

    private static T Selected<T>(ComboBox box, T fallback) =>
        box.SelectedItem is ComboBoxItem { Tag: T value } ? value : fallback;

    private static void Select<T>(ComboBox box, T value)
    {
        foreach (var item in box.Items)
            if (item is ComboBoxItem { Tag: T tag } combo && EqualityComparerEquals(tag, value))
            {
                box.SelectedItem = combo;
                return;
            }
        box.SelectedIndex = 0;
    }

    private static bool EqualityComparerEquals<T>(T left, T right) => System.Collections.Generic.EqualityComparer<T>.Default.Equals(left, right);
}
