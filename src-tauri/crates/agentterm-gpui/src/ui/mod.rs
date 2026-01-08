//! UI components for agentterm-gpui
//!
//! Simplified UI components adapted from gpui-component.
//! All components use hardcoded colors and avoid complex theming
//! or animation systems.

mod context_menu;
mod icon_picker;
mod lucide_search;
mod popup_menu;
mod slider;
mod switch;
mod tab;
mod tab_bar;

pub use context_menu::*;
pub use icon_picker::*;
pub use lucide_search::*;
pub use popup_menu::*;
pub use slider::*;
pub use switch::*;
pub use tab::*;
pub use tab_bar::*;
