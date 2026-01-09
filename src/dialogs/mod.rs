//! Dialog components for the AgentTerm application.

mod mcp_manager;
mod project_editor;
mod session_editor;
mod tab_picker;

pub use mcp_manager::{McpItem, McpManagerDialog};
pub use project_editor::ProjectEditorDialog;
pub use session_editor::SessionEditorDialog;
pub use tab_picker::TabPickerDialog;

// Re-export AgentTermApp for dialog dependencies
pub use crate::app::AgentTermApp;
