//! UI components for agentterm-gpui
//!
//! Re-exports gpui-component UI primitives and provides
//! app-specific components (IconPicker, LucideSearchModal).

pub mod helpers;
mod icon_picker;
mod lucide_search;
mod material_theme;
mod surface;
mod window_shell;

// App-specific components
pub use helpers::*;
pub use icon_picker::*;
pub use lucide_search::*;
pub use material_theme::*;
pub use surface::*;
pub use window_shell::*;

// Re-export gpui-component UI primitives
pub use gpui_kit::component::{
    // Theme and styling
    ActiveTheme,
    Sizable,
    // Window extensions for dialogs
    WindowExt,
    // Button components
    button::{Button, ButtonVariants},
    // Separator components
    separator::Separator,
    // Menu components
    menu::{ContextMenuExt, DropdownMenu, PopupMenu, PopupMenuItem},
    // Form controls
    slider::{Slider, SliderEvent, SliderState},
    switch::Switch,
    // Tab components
    tab::{Tab, TabBar},
    v_flex,
};
