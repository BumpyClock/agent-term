using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AgentTerm_App;

internal sealed class TabProgramStatus
{
    private readonly TabViewItem item;
    private readonly Flyout _details = new();
    private readonly TextBox _detailsText = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        Width = 480, MaxHeight = 480
    };

    internal TabProgramStatus(TabViewItem item)
    {
        this.item = item;
        _details.Content = _detailsText;
        AutomationProperties.SetName(_detailsText, "Program status details");
        _details.Opened += (_, _) => _detailsText.Focus(FocusState.Programmatic);
        item.ContextFlyout = new MenuFlyout { Items = { CreateDetailsMenuItem(item) } };
    }

    internal MenuFlyoutItem CreateDetailsMenuItem(FrameworkElement target)
    {
        var entry = new MenuFlyoutItem { Text = "Program status details" };
        entry.Click += (_, _) => ShowDetails(target);
        return entry;
    }

    internal void ShowDetails(FrameworkElement target)
    {
        _detailsText.Text = _title + "\n\n" + (_presentation.Details.Length == 0 ? "No program status reported." : _presentation.Details);
        _details.ShowAt(target);
    }

    internal static readonly DependencyProperty DetailsProperty = DependencyProperty.RegisterAttached(
        "Details", typeof(string), typeof(TabProgramStatus), new PropertyMetadata(""));
    private IReadOnlyList<PaneProgramStatus> _records = [];
    private string _title = "";
    private IconSource? _shellIcon;
    private ProgramStatusPresentation _presentation = new("", "", "");

    internal void SetShellIcon(IconSource icon)
    {
        _shellIcon = icon;
        if (_presentation.Glyph.Length == 0) item.IconSource = icon;
    }

    internal void Update(IReadOnlyList<PaneProgramStatus> records)
    {
        var title = item.Header as string ?? "";
        if (_title == title && _records.SequenceEqual(records)) return;
        _title = title;
        _records = records;
        var presentation = ProgramStatusPresentation.Create(records);
        _presentation = presentation;
        if (_details.IsOpen) _detailsText.Text = title + "\n\n" + (presentation.Details.Length == 0 ? "No program status reported." : presentation.Details);
        item.IconSource = presentation.Glyph.Length == 0 ? _shellIcon : new FontIconSource { Glyph = presentation.Glyph };
        item.SetValue(DetailsProperty, presentation.Details);
        AutomationProperties.SetHelpText(item, presentation.Summary);
        ToolTipService.SetToolTip(item, presentation.Details.Length == 0 ? null : CreateToolTip(item.Header as string ?? "", presentation.Details));
    }

    internal static TextBlock CreateToolTip(string title, string details) => new()
    {
        MaxWidth = 420,
        TextWrapping = TextWrapping.Wrap,
        Text = title + "\n\n" + string.Join("\n", details.Split('\n').Take(10).Select(line => line.Length > 160 ? line[..160] + "…" : line))
            + "\n\nRight-click or Shift+F10 for program status details."
    };
}
