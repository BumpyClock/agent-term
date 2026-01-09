//! UI helper functions and types.

use agentterm_session::SectionRecord;
use gpui::Entity;
use gpui_component::input::{Input as GpuiInput, InputState as GpuiInputState};

use crate::icons::IconDescriptor;

/// Wrapper for SectionRecord with additional display metadata.
#[derive(Clone)]
pub struct SectionItem {
    pub section: SectionRecord,
    pub is_default: bool,
}

/// Convert IconDescriptor to a string format for storage.
pub fn icon_descriptor_to_string(icon: &IconDescriptor) -> String {
    match icon {
        IconDescriptor::Lucide { id } => format!("lucide:{}", id),
        IconDescriptor::Tool { id } => id.clone(),
    }
}

/// Convert a string to IconDescriptor.
pub fn icon_descriptor_from_string(s: &str) -> IconDescriptor {
    if s.starts_with("lucide:") {
        IconDescriptor::lucide(&s[7..])
    } else {
        IconDescriptor::tool(s)
    }
}

/// Create an input field with AgentTerm styling.
pub fn agentterm_input_field(input: &Entity<GpuiInputState>) -> GpuiInput {
    GpuiInput::new(input)
}
