namespace AgentTerm.Terminal;

public sealed partial class TerminalSession
{
    public void SendPhysicalKey(uint virtualKey, TerminalModifiers modifiers, TerminalKeyAction action,
        string? text, uint scanCode, bool extended, TerminalModifiers consumedModifiers, uint? layoutCodepoint)
    {
        lock (_renderGate) lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_owned is { InteractiveInput: false } || ReservationPending || _restoringDraft) return;
            var bytes = _terminal.EncodeKey(virtualKey, modifiers, action, text, scanCode, extended,
                consumedModifiers, layoutCodepoint);
            if (bytes.Length != 0 && action != TerminalKeyAction.Release &&
                virtualKey is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5)) PrepareInput();
            _connection.Send(bytes);
        }
    }
}
