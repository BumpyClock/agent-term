using System.Text.Json.Serialization;

namespace AgentTerm.Terminal;

/// <summary>Whether a program may write the Windows clipboard through OSC 52 or the Kitty clipboard protocol (OSC 5522).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClipboardWritePolicy
{
    /// <summary>Refuse every write. Programs that expect an acknowledgement receive a permission error.</summary>
    Deny,
    /// <summary>Hold the text and ask first. The program is told the write was denied. Approving still copies the text.</summary>
    Ask,
    /// <summary>Copy immediately.</summary>
    Allow,
}

/// <summary>Whether a program may read the Windows clipboard. Reading discloses what the user copied elsewhere.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClipboardReadPolicy
{
    /// <summary>Answer every read with an empty clipboard or a permission error.</summary>
    Deny,
    /// <summary>Answer reads with the current clipboard text, up to the size limit.</summary>
    Allow,
}

/// <summary>How a terminal bell (BEL, 0x07) is shown.</summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BellStyle
{
    None = 0,
    /// <summary>Play the Windows default sound.</summary>
    Audible = 1,
    /// <summary>Flash the pane and, when the window is in the background, the taskbar button.</summary>
    Visual = 2,
    Both = Audible | Visual,
}

/// <summary>Where desktop notification requests (OSC 9, OSC 777) appear.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationStyle
{
    /// <summary>Ignore the requests.</summary>
    Off,
    /// <summary>Show a card in the pane that asked.</summary>
    InApp,
    /// <summary>Show a card, and a Windows notification when the window is in the background and the system supports it.</summary>
    InAppAndSystem,
}

/// <summary>Persistent host preferences for effects that programs request through the terminal. Defaults are conservative.</summary>
public sealed record TerminalEffectOptions
{
    public const int MinimumClipboardBytes = 4 * 1024;
    public const int MaximumClipboardBytes = 8 * 1024 * 1024;
    public const int DefaultClipboardBytes = 1024 * 1024;

    public ClipboardWritePolicy ClipboardWrite { get; init; } = ClipboardWritePolicy.Allow;
    public ClipboardReadPolicy ClipboardRead { get; init; } = ClipboardReadPolicy.Deny;
    /// <summary>Largest decoded clipboard payload, in bytes, a program may write or read. Larger requests are refused.</summary>
    public int ClipboardMaxBytes { get; init; } = DefaultClipboardBytes;
    public BellStyle Bell { get; init; } = BellStyle.Visual;
    public NotificationStyle Notifications { get; init; } = NotificationStyle.InAppAndSystem;
    /// <summary>Show progress reported with OSC 9;4 on the pane and the taskbar button.</summary>
    public bool Progress { get; init; } = true;
    /// <summary>Let a program resize the window with CSI 8 ; rows ; columns t. Off by default because it is a nuisance.</summary>
    public bool AllowWindowResize { get; init; }

    public static TerminalEffectOptions Default { get; } = new();

    /// <summary>Returns a copy with undefined enum values and out-of-range sizes replaced by safe values.</summary>
    public TerminalEffectOptions Normalize() => this with
    {
        ClipboardWrite = Enum.IsDefined(ClipboardWrite) ? ClipboardWrite : ClipboardWritePolicy.Allow,
        ClipboardRead = Enum.IsDefined(ClipboardRead) ? ClipboardRead : ClipboardReadPolicy.Deny,
        ClipboardMaxBytes = Math.Clamp(ClipboardMaxBytes, MinimumClipboardBytes, MaximumClipboardBytes),
        Bell = (BellStyle)((int)Bell & (int)BellStyle.Both),
        Notifications = Enum.IsDefined(Notifications) ? Notifications : NotificationStyle.InAppAndSystem,
    };
}
