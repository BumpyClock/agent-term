using System.Collections.Generic;
using System.Text;
using AgentTerm.Terminal;

namespace AgentTerm_App;

internal sealed class TerminalKeyboardInput
{
    internal enum Route { Text, Direct, Shortcut }
    internal readonly record struct PhysicalKey(uint ScanCode, bool Extended);
    internal readonly record struct KeyEvent(uint Key, TerminalModifiers Modifiers, TerminalKeyAction Action,
        string? Text = null, PhysicalKey Physical = default,
        TerminalModifiers ConsumedModifiers = TerminalModifiers.None, uint? LayoutCodepoint = null);

    internal static TerminalModifiers ShortcutModifiers(TerminalModifiers modifiers) =>
        modifiers & (TerminalModifiers.Shift | TerminalModifiers.Control | TerminalModifiers.Alt | TerminalModifiers.Super);

    private readonly record struct PressedKey(uint Key, TerminalModifiers Modifiers, TerminalKeyAction Action,
        Route Route, TerminalModifiers ConsumedModifiers, uint? LayoutCodepoint, bool Forwarded, bool CharacterPending);

    private readonly Dictionary<PhysicalKey, PressedKey> _pressed = [];
    private (PhysicalKey Key, char Value)? _highSurrogate;

    internal bool IsShortcut(PhysicalKey physical) =>
        _pressed.TryGetValue(physical, out var key) && key.Route == Route.Shortcut;

    internal bool IsForwarded(PhysicalKey physical) =>
        _pressed.TryGetValue(physical, out var key) && key.Forwarded;

    internal TerminalKeyAction KeyDown(PhysicalKey physical, uint key, TerminalModifiers modifiers, bool wasDown,
        Route route, TerminalModifiers consumedModifiers = TerminalModifiers.None, uint? layoutCodepoint = null)
    {
        bool held = _pressed.TryGetValue(physical, out var previous);
        // A key already delivered to the application must finish there even if
        // modifiers change during autorepeat and now resemble a local shortcut.
        if (wasDown && held && previous.Forwarded && route == Route.Shortcut) route = previous.Route;
        bool forwarded = wasDown && held && previous.Forwarded;
        var action = forwarded ? TerminalKeyAction.Repeat : TerminalKeyAction.Press;
        _pressed[physical] = new PressedKey(key, modifiers, action, route, consumedModifiers, layoutCodepoint,
            forwarded || route == Route.Direct, route == Route.Text);
        return action;
    }

    internal KeyEvent? KeyUp(PhysicalKey physical, TerminalModifiers modifiers) =>
        _pressed.Remove(physical, out var key) && key.Forwarded
            ? new KeyEvent(key.Key, modifiers, TerminalKeyAction.Release, Physical: physical,
                ConsumedModifiers: key.ConsumedModifiers, LayoutCodepoint: key.LayoutCodepoint)
            : null;

    internal KeyEvent? Text(PhysicalKey? physical, string text)
    {
        if (text.Length == 0) return null;
        if (physical is { } key && Rune.TryGetRuneAt(text, 0, out var rune) && rune.Utf16SequenceLength == text.Length)
            return Character(key, (uint)rune.Value);
        return new KeyEvent(0, TerminalModifiers.None, TerminalKeyAction.Press, text);
    }

    internal KeyEvent? Character(PhysicalKey physical, uint codepoint)
    {
        // A scanless character can be an IME commit or injected Unicode input.
        var key = physical.ScanCode != 0 && _pressed.TryGetValue(physical, out var matched) ? matched : default;
        if (key.Route is Route.Direct or Route.Shortcut)
            return null;

        Rune rune;
        if (codepoint <= char.MaxValue && char.IsHighSurrogate((char)codepoint))
        {
            _highSurrogate = (physical, (char)codepoint);
            return null;
        }
        var high = _highSurrogate;
        _highSurrogate = null;
        if (codepoint <= char.MaxValue && char.IsLowSurrogate((char)codepoint))
        {
            if (high is not { } pair || pair.Key != physical || !Rune.TryCreate(pair.Value, (char)codepoint, out rune))
                return null;
        }
        else if (!Rune.TryCreate((int)codepoint, out rune))
        {
            return null;
        }

        if (Rune.IsControl(rune))
            return null;
        if (!key.CharacterPending)
            return new KeyEvent(0, TerminalModifiers.None, TerminalKeyAction.Press, rune.ToString());

        _pressed[physical] = key with { Forwarded = true, CharacterPending = false };
        return new KeyEvent(key.Key, key.Modifiers, key.Action, rune.ToString(), physical,
            key.ConsumedModifiers, key.LayoutCodepoint);
    }

    internal KeyEvent[] Reset()
    {
        var releases = new List<KeyEvent>();
        foreach (var (physical, key) in _pressed)
        {
            if (key.Forwarded)
                releases.Add(new KeyEvent(key.Key, TerminalModifiers.None, TerminalKeyAction.Release,
                    Physical: physical, ConsumedModifiers: key.ConsumedModifiers, LayoutCodepoint: key.LayoutCodepoint));
        }
        _pressed.Clear();
        _highSurrogate = null;
        return releases.ToArray();
    }
}
