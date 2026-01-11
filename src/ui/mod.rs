//! UI components for agentterm-gpui
//!
//! Re-exports gpui-component UI primitives and provides
//! app-specific components (IconPicker, LucideSearchModal).

mod blurred_dropdown;
pub mod helpers;
mod icon_picker;
mod lucide_search;

// App-specific components
pub use blurred_dropdown::*;
pub use helpers::*;
pub use icon_picker::*;
pub use lucide_search::*;

// Re-export gpui-component UI primitives
pub use gpui_component::{
    // Theme and styling
    ActiveTheme,
    Sizable,
    // Window extensions for dialogs
    WindowExt,
    // Button components
    button::{Button, ButtonVariants},
    // Dialog components
    dialog::Dialog,
    // Divider components
    divider::Divider,
    // Layout helpers
    h_flex,
    // List components
    list::ListItem,
    // Menu components
    menu::{ContextMenuExt, DropdownMenu, PopupMenu, PopupMenuItem},
    // Form controls
    slider::{Slider, SliderEvent, SliderState},
    switch::Switch,
    // Tab components
    tab::{Tab, TabBar},
    v_flex,
};
