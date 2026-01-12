//! Layout and color constants for the AgentTerm application.

// Sidebar layout constants
pub const SIDEBAR_INSET: f32 = 4.0;
pub const SIDEBAR_GAP: f32 = 16.0;
pub const SIDEBAR_MIN_WIDTH: f32 = 200.0;
pub const SIDEBAR_MAX_WIDTH: f32 = 420.0;
pub const SIDEBAR_HEADER_LEFT_PADDING: f32 = 16.0;

// Alpha values for glass effect
// SURFACE_ROOT_ALPHA: Base opacity when transparency slider is at 0%
// Higher value = more solid/opaque window at min transparency
// At transparency=0%: window has this much dark tint
// At transparency=100%: window is fully transparent (blur shows through)
pub const SURFACE_ROOT_ALPHA: f32 = 0.85;
pub const BORDER_SOFT_ALPHA: f32 = 0.50;
