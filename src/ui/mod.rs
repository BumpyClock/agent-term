//! UI components for agentterm-gpui
//!
//! Re-exports gpui-component UI primitives and provides
//! app-specific components (IconPicker, LucideSearchModal).

pub mod helpers;
mod icon_picker;
mod lucide_search;

// App-specific components
pub use helpers::*;
pub use icon_picker::*;
pub use lucide_search::*;

// Re-export gpui-component UI primitives
pub use gpui_component::{
    // Button components
    button::{Button, ButtonVariants},
    // Dialog components
    dialog::Dialog,
    // Divider components
    divider::Divider,
    // Form controls
    slider::{Slider, SliderEvent, SliderState},
    switch::Switch,
    // List components
    list::ListItem,
    // Tab components
    tab::{Tab, TabBar},
    // Menu components
    menu::ContextMenuExt,
    // Theme and styling
    ActiveTheme,
    Sizable,
    // Layout helpers
    h_flex, v_flex,
    // Window extensions for dialogs
    WindowExt,
};
