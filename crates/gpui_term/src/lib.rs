mod mappings;
mod terminal;
mod terminal_element;
mod terminal_palette;
mod terminal_view;

pub use terminal::{
    DetectedUrl, Event, IndexedCell, Terminal, TerminalBounds, TerminalBuilder, TerminalContent,
    ZedListener,
};
pub use terminal_element::{TerminalElement, TextStyle, convert_color};
pub use terminal_palette::{TerminalPalette, set_terminal_palette, terminal_palette};
pub use terminal_view::{
    Clear, Copy, FocusOut, Paste, ScrollLineDown, ScrollLineUp, ScrollPageDown, ScrollPageUp,
    SelectAll, SendShiftTab, SendTab, TerminalView,
};
