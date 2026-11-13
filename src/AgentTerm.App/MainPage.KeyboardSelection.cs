using AgentTerm.Terminal;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace AgentTerm_App;

public sealed partial class MainPage
{
    private bool _keyboardMarkMode;

    private void InitializeTerminalAutomation()
    {
        Terminal.AutomationSelectRange = (frame, startColumn, startRow, endColumn, endRow) =>
        {
            if (_session is not { } session || !session.TrySelectFrameRange(frame, startColumn, startRow, endColumn, endRow)) return false;
            RefreshOutput(session);
            return true;
        };
        Terminal.AutomationClearSelection = () =>
        {
            if (_session is not { } session) return;
            session.ClearSelection();
            SetKeyboardMarkMode(false);
            RefreshOutput(session);
        };
    }

    private static void ForwardKey(TerminalSession session, TerminalKeyboardInput.KeyEvent key) =>
        session.SendPhysicalKey(key.Key, key.Modifiers, key.Action, key.Text, key.Physical.ScanCode,
            key.Physical.Extended, key.ConsumedModifiers, key.LayoutCodepoint);

    private bool HandleKeyboardSelection(TerminalSession session, KeyRoutedEventArgs e,
        TerminalKeyboardInput.PhysicalKey physical, VirtualKey key, TerminalModifiers modifiers)
    {
        bool toggle = modifiers == (TerminalModifiers.Control | TerminalModifiers.Shift) && key == VirtualKey.M;
        TerminalSelectionAdjustment? adjustment = key switch
        {
            VirtualKey.Left => TerminalSelectionAdjustment.Left,
            VirtualKey.Right => TerminalSelectionAdjustment.Right,
            VirtualKey.Up => TerminalSelectionAdjustment.Up,
            VirtualKey.Down => TerminalSelectionAdjustment.Down,
            VirtualKey.PageUp => TerminalSelectionAdjustment.PageUp,
            VirtualKey.PageDown => TerminalSelectionAdjustment.PageDown,
            VirtualKey.Home => (modifiers & TerminalModifiers.Control) != 0
                ? TerminalSelectionAdjustment.Home : TerminalSelectionAdjustment.BeginningOfLine,
            VirtualKey.End => (modifiers & TerminalModifiers.Control) != 0
                ? TerminalSelectionAdjustment.End : TerminalSelectionAdjustment.EndOfLine,
            _ => null
        };
        if (!toggle && (!_keyboardMarkMode || adjustment is null ||
            (modifiers & (TerminalModifiers.Alt | TerminalModifiers.Super)) != 0)) return false;
        if (_keyboard.IsForwarded(physical)) return false;

        e.Handled = true;
        _keyboard.KeyDown(physical, (uint)key, modifiers, e.KeyStatus.WasKeyDown, TerminalKeyboardInput.Route.Shortcut);
        try
        {
            if (toggle)
            {
                if (e.KeyStatus.WasKeyDown) return true;
                if (!_keyboardMarkMode) session.BeginKeyboardSelection();
                else session.ClearSelection();
                SetKeyboardMarkMode(!_keyboardMarkMode);
            }
            else session.AdjustSelection(adjustment!.Value);
            RefreshOutput(session);
        }
        catch (Exception exception) { ShowInteractionFailure(exception); }
        return true;
    }

    private void SetKeyboardMarkMode(bool enabled)
    {
        if (_keyboardMarkMode == enabled) return;
        _keyboardMarkMode = enabled;
        var message = enabled
            ? "Mark mode. Arrow keys select text. Home, End and Page keys move the selection. Enter copies. Escape cancels."
            : "Mark mode ended.";
        StatusText.Text = enabled ? message : _state == SessionState.Running ? "Running · Local session" : "Session output";
        AutomationProperties.SetLiveSetting(StatusText, AutomationLiveSetting.Polite);
        AutomationProperties.SetHelpText(Terminal.Input, enabled ? message : "Ctrl+Shift+M starts keyboard selection.");
        FrameworkElementAutomationPeer.FromElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
