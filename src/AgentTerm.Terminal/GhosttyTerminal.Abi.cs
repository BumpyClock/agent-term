namespace AgentTerm.Terminal;

public sealed partial class GhosttyTerminal
{
    // Checked against the loaded library, not against a second set of numeric offsets. Dotted native paths
    // cover our flattened union/aggregate mirrors. This runs only in explicit diagnostics and smoke checks.
    private static (string Native, Type Managed, (string Native, string Managed)[] Fields)[] InteropLayouts() =>
    [
        ("GhosttyString", typeof(Native.Utf8String), [("ptr", "Data"), ("len", "Length")]),
        ("GhosttyString", typeof(EffectsNative.GhosttyString), [("ptr", "Ptr"), ("len", "Length")]),
        ("GhosttyPoint", typeof(Native.Point), [("tag", "Tag"), ("value", "Value")]),
        ("GhosttyPointValue", typeof(Native.PointValue), [("coordinate.x", "X"), ("coordinate.y", "Y")]),
        ("GhosttyGridRef", typeof(Native.GridRef), [("node", "Node"), ("x", "X"), ("y", "Y")]),
        ("GhosttySelection", typeof(Native.Selection), [("start", "Start"), ("end", "End"), ("rectangle", "Rectangle")]),
        ("GhosttySelectionGestureBehaviors", typeof(GestureBehaviors), [("single_click", "Single"), ("double_click", "Double"), ("triple_click", "Triple")]),
        ("GhosttySelectionGestureGeometry", typeof(GestureGeometry), [("columns", "Columns"), ("cell_width", "CellWidth"), ("padding_left", "PaddingLeft"), ("screen_height", "Height")]),
        ("GhosttySurfacePosition", typeof(SurfacePosition), [("x", "X"), ("y", "Y")]),
        ("GhosttyTerminalScrollViewport", typeof(ScrollBehavior), [("tag", "Tag"), ("value", "Value")]),
        ("GhosttyTerminalScrollbar", typeof(Scrollbar), [("total", "Total"), ("offset", "Offset"), ("len", "Length")]),
        ("GhosttyRenderStateOverscan", typeof(OverscanRequest), [("above", "Above"), ("below", "Below")]),
        ("GhosttyMouseEncoderSize", typeof(MouseNative.EncoderSize), [("size", "Size"), ("screen_width", "ScreenWidth"), ("screen_height", "ScreenHeight"), ("cell_width", "CellWidth"), ("cell_height", "CellHeight"), ("padding_top", "PaddingTop"), ("padding_bottom", "PaddingBottom"), ("padding_right", "PaddingRight"), ("padding_left", "PaddingLeft")]),
        ("GhosttyTerminalModeConfig", typeof(InputNative.ModeConfig), [("mode", "Mode"), ("value", "Value")]),
        ("GhosttySizeReportSize", typeof(HostNative.Size), [("rows", "Rows"), ("columns", "Columns"), ("cell_width", "CellWidth"), ("cell_height", "CellHeight")]),
        ("GhosttyDeviceAttributes", typeof(HostNative.Attributes), [("primary.conformance_level", "Conformance"), ("primary.features", "Features"), ("primary.num_features", "FeatureCount"), ("secondary.device_type", "DeviceType"), ("secondary.firmware_version", "Firmware"), ("secondary.rom_cartridge", "Cartridge"), ("tertiary.unit_id", "UnitId")]),
        ("GhosttySysImage", typeof(SysImage), [("width", "Width"), ("height", "Height"), ("data", "Data"), ("data_len", "Length")]),
        ("GhosttyTerminalMemoryUsage", typeof(DiagnosticNative.Memory), [("size", "Size"), ("compression_supported", "CompressionSupported"), ("primary_pages", "Values")]),
        ("GhosttyTerminalUnknownSequence", typeof(DiagnosticNative.UnknownSequence), [("tag", "Kind"), ("value.osc.truncated", "Truncated"), ("value.osc.content", "Content")]),
        ("GhosttyTerminalSemanticPrompt", typeof(EventsNative.SemanticPromptEvent), [("kind", "Kind"), ("prompt_kind", "PromptKind"), ("has_exit_code", "HasExitCode"), ("exit_code", "ExitCode"), ("command", "Command"), ("error", "Error")]),
        ("GhosttyTerminalProgramStatus", typeof(EventsNative.ProgramStatusEvent), [("state", "State"), ("kind", "Kind"), ("progress", "Progress"), ("id", "Id"), ("app", "App"), ("title", "Title"), ("message", "Message")]),
        ("GhosttyTerminalWriteStop", typeof(EventsNative.WriteStop), [("size", "Size"), ("consumed", "Consumed"), ("reached", "Reached"), ("kind", "Kind"), ("introducer", "Introducer"), ("end", "End"), ("byte", "Byte")]),
        ("GhosttyWriter", typeof(EffectsNative.GhosttyWriter), [("write", "Write"), ("userdata", "Userdata")]),
        ("GhosttyTerminalDesktopNotification", typeof(EffectsNative.NotificationRequest), [("title", "Title"), ("body", "Body")]),
        ("GhosttyTerminalProgressReport", typeof(EffectsNative.ProgressReport), [("state", "State"), ("progress", "Percent")]),
        ("GhosttyTerminalResizeWindow", typeof(EffectsNative.ResizeWindowRequest), [("tag", "Tag"), ("value.grid.rows", "Rows"), ("value.grid.columns", "Columns")]),
        ("GhosttyClipboardContent", typeof(EffectsNative.ClipboardContent), [("mime", "Mime"), ("data", "Data")]),
        ("GhosttyClipboardWrite", typeof(EffectsNative.ClipboardWriteRequest), [("location", "Location"), ("contents", "Contents"), ("contents_len", "ContentsLength"), ("name", "Name"), ("granted", "Granted"), ("can_remember", "CanRemember"), ("ctx", "Context"), ("reply", "Reply")]),
        ("GhosttyClipboardWriteReply", typeof(EffectsNative.ClipboardWriteReply), [("size", "Size"), ("result", "Result"), ("remember", "Remember")]),
        ("GhosttyClipboardRead", typeof(EffectsNative.ClipboardReadRequest), [("location", "Location"), ("mimes", "Mimes"), ("mimes_len", "MimesLength"), ("list", "List"), ("name", "Name"), ("granted", "Granted"), ("can_remember", "CanRemember"), ("ctx", "Context"), ("reply", "Reply")]),
        ("GhosttyClipboardReadReply", typeof(EffectsNative.ClipboardReadReply), [("size", "Size"), ("result", "Result"), ("contents", "Contents"), ("contents_len", "ContentsLength"), ("available", "Available"), ("available_len", "AvailableLength"), ("remember", "Remember")]),
        ("GhosttyPaste", typeof(EffectsNative.PasteRequest), [("size", "Size"), ("location", "Location"), ("source", "Source"), ("mimes", "Mimes"), ("mimes_len", "MimesLength"), ("reader.read", "ReaderFunction"), ("reader.userdata", "ReaderUserdata"), ("allow_unsafe", "AllowUnsafe")]),
    ];
}
