using AgentTerm.Terminal;
using AgentTerm_App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

internal static class EffectsSettingsChecks
{
    internal static void Run()
    {
        var panel = new EffectsSettingsPanel(new Style(typeof(Border)), new Style(typeof(TextBlock)), new Style(typeof(TextBlock)));
        var custom = TerminalEffectOptions.Default with { ClipboardMaxBytes = 100_001 };
        panel.Load(custom);
        if (panel.Options != custom) throw new Exception("Opening and applying a custom clipboard budget must preserve it exactly.");
        var choices = panel.Children.OfType<Border>().Select(row => (Grid)row.Child)
            .SelectMany(row => row.Children.OfType<ComboBox>())
            .Single(box => AutomationProperties.GetName(box) == "Clipboard size limit");
        if (choices.SelectedItem is not ComboBoxItem { Tag: 100_001 }) throw new Exception("The custom budget must be represented by the selected choice.");
        var changes = 0;
        panel.Changed += () => changes++;
        choices.SelectedItem = choices.Items.OfType<ComboBoxItem>().Single(item => item.Tag is int bytes && bytes == 64 * 1024);
        if (panel.Options.ClipboardMaxBytes != 64 * 1024 || changes != 1) throw new Exception("Selecting a preset must replace the custom budget and report the edit.");
        Console.WriteLine("Effects settings checks passed. Exact custom budget and preset selection.");
    }
}
