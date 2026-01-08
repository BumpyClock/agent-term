//! UI components for agentterm-gpui
//!
//! Re-exports gpui-component UI primitives and provides
//! app-specific components (IconPicker, LucideSearchModal).

mod icon_picker;
mod lucide_search;

// App-specific components
pub use icon_picker::*;
pub use lucide_search::*;

// Re-export gpui-component UI primitives
pub use gpui_component::{
    // Button components
    button::{Button, ButtonVariants},
    // Form controls
    slider::{Slider, SliderEvent, SliderState},
    switch::Switch,
    // Tab components
    tab::{Tab, TabBar},
    // Menu components
    menu::ContextMenuExt,
    // Theme
    ActiveTheme,
};
