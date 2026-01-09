//! Layout and color constants for the AgentTerm application.

// Sidebar layout constants
pub const SIDEBAR_INSET: f32 = 4.0;
pub const SIDEBAR_GAP: f32 = 16.0;
pub const SIDEBAR_MIN_WIDTH: f32 = 200.0;
pub const SIDEBAR_MAX_WIDTH: f32 = 420.0;
pub const SIDEBAR_HEADER_LEFT_PADDING: f32 = 68.0;

// Sidebar glass effect constants
pub const SIDEBAR_GLASS_BASE_ALPHA: f32 = 0.18;
pub const SIDEBAR_GLASS_BORDER_ALPHA: f32 = 0.14;

// Color palette (RGB hex values)
pub const SURFACE_ROOT: u32 = 0x000000;
pub const SURFACE_SIDEBAR: u32 = 0x202020;
pub const BORDER_SOFT: u32 = 0x3a3a3a;

// Alpha values for glass effect
pub const SURFACE_ROOT_ALPHA: f32 = 0.05;
pub const SURFACE_SIDEBAR_ALPHA: f32 = 0.32;
pub const BORDER_SOFT_ALPHA: f32 = 0.50;

// Feature flags
pub const ENABLE_BLUR: bool = true;

/// Convert RGB u32 + alpha to RGBA u32.
pub fn rgba_u32(rgb: u32, alpha: f32) -> u32 {
    let a = (alpha.clamp(0.0, 1.0) * 255.0).round() as u32;
    (rgb << 8) | a
}
