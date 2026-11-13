using System;
using System.Globalization;
using AgentTerm_App.Effects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace AgentTerm_App;

/// <summary>
/// Cards a pane shows for requests programs make: notifications, clipboard disclosures, and clipboard questions.
/// It renders a <see cref="NoticeCenter"/> and reports button clicks. It owns no timers and does nothing while the center is empty.
/// </summary>
internal sealed class EffectNoticeView : StackPanel
{
    private static readonly Style CardStyle = (Style)XamlReader.Load("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Border">
            <Setter Property="Background" Value="{ThemeResource AcrylicInAppFillColorDefaultBrush}"/>
            <Setter Property="BorderBrush" Value="{ThemeResource CardStrokeColorDefaultBrush}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="CornerRadius" Value="8"/>
            <Setter Property="Padding" Value="12"/>
        </Style>
        """);

    private static readonly Style InfoIconStyle = IconStyle("AccentTextFillColorPrimaryBrush");
    private static readonly Style WarningIconStyle = IconStyle("SystemFillColorCautionBrush");
    private static readonly Style ErrorIconStyle = IconStyle("SystemFillColorCriticalBrush");

    private readonly NoticeCenter _center;
    private readonly Action<long, NoticeAction> _invoke;

    internal EffectNoticeView(NoticeCenter center, Action<long, NoticeAction> invoke)
    {
        _center = center;
        _invoke = invoke;
        Spacing = 8;
        MaxWidth = 380;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(12);
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(this, "Program requests");
        _center.Changed += Rebuild;
    }

    internal void Detach() => _center.Changed -= Rebuild;

    private static Style IconStyle(string brush) => (Style)XamlReader.Load($$"""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="FontIcon">
            <Setter Property="Foreground" Value="{ThemeResource {{brush}}}"/>
        </Style>
        """);

    private void Rebuild()
    {
        Children.Clear();
        foreach (var notice in _center.Items) Children.Add(Card(notice));
        Visibility = Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private UIElement Card(Notice notice)
    {
        var text = new StackPanel { Spacing = 2 };
        var title = notice.Repeats > 1
            ? string.Format(CultureInfo.CurrentCulture, "{0} (×{1})", notice.Title, notice.Repeats)
            : notice.Title;
        text.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        if (notice.Message.Length != 0)
            text.Children.Add(new TextBlock
            {
                Text = notice.Message, TextWrapping = TextWrapping.Wrap, MaxLines = 5, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });

        var decision = notice.Actions.Contains(NoticeAction.AllowWrite);
        if (decision)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
            var allow = new Button { Content = "Copy", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            AutomationProperties.SetName(allow, "Copy to the clipboard");
            allow.Click += (_, _) => _invoke(notice.Id, NoticeAction.AllowWrite);
            var deny = new Button { Content = "Don't copy" };
            AutomationProperties.SetName(deny, "Do not copy");
            deny.Click += (_, _) => _invoke(notice.Id, NoticeAction.Dismiss);
            buttons.Children.Add(allow);
            buttons.Children.Add(deny);
            text.Children.Add(buttons);
        }

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon
        {
            Glyph = notice.Severity == NoticeSeverity.Info ? "" : "", FontSize = 16, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            Style = notice.Severity switch { NoticeSeverity.Error => ErrorIconStyle, NoticeSeverity.Warning => WarningIconStyle, _ => InfoIconStyle },
        };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (!decision)
        {
            var close = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 10 }, Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Top,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            };
            AutomationProperties.SetName(close, "Dismiss");
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(close, "Dismiss");
            close.Click += (_, _) => _invoke(notice.Id, NoticeAction.Dismiss);
            Grid.SetColumn(close, 2);
            grid.Children.Add(close);
        }

        var card = new Border { Style = CardStyle, Child = grid };
        AutomationProperties.SetName(card, notice.Message.Length == 0 ? title : title + ". " + notice.Message);
        AutomationProperties.SetLiveSetting(card, notice.Severity == NoticeSeverity.Info ? AutomationLiveSetting.Polite : AutomationLiveSetting.Assertive);
        return card;
    }
}
