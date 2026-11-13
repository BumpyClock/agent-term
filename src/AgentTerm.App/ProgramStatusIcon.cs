using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace AgentTerm_App;

internal sealed partial class ProgramStatusIcon : ContentControl
{
    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(nameof(Tab), typeof(TabViewItem), typeof(ProgramStatusIcon), new PropertyMetadata(null, (sender, _) => ((ProgramStatusIcon)sender).Connect()));
    public TabViewItem? Tab { get => (TabViewItem?)GetValue(TabProperty); set => SetValue(TabProperty, value); }
    private readonly ProgramIconPresentation _presentation;
    private TabViewItem? _observed;
    private long _statusToken;
    private long _iconToken;
    private bool _loaded;

    public ProgramStatusIcon() : this(null) { }

    internal ProgramStatusIcon(Func<bool>? motionAllowed, IProgramIconPreferences? preferences = null)
    {
        _presentation = new ProgramIconPresentation(preferences, motionAllowed);
        IsTabStop = false;
        IsHitTestVisible = false;
        Content = _presentation;
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Loaded += (_, _) => { _loaded = true; Connect(); };
        Unloaded += (_, _) => { _loaded = false; Disconnect(); _presentation.Update(null, false); };
    }

    private void Disconnect()
    {
        if (_observed is null) return;
        _observed.UnregisterPropertyChangedCallback(TabProgramStatus.DetailsProperty, _statusToken);
        _observed.UnregisterPropertyChangedCallback(TabViewItem.IconSourceProperty, _iconToken);
        _observed = null;
    }
    private void Connect()
    {
        Disconnect();
        if (!_loaded || Tab is not { } tab)
        {
            _presentation.Update(null, false);
            return;
        }
        _observed = tab;
        _statusToken = tab.RegisterPropertyChangedCallback(TabProgramStatus.DetailsProperty, (_, _) => Refresh());
        _iconToken = tab.RegisterPropertyChangedCallback(TabViewItem.IconSourceProperty, (_, _) => Refresh());
        Refresh();
    }
    private void Refresh()
    {
        var tab = _observed;
        if (tab is null) return;
        _presentation.Update(tab.IconSource,
            (tab.Tag as TabProgramStatus)?.Primary?.State == AgentTerm.Terminal.ProgramState.Working);
    }
}
