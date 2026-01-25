//! Dialog components for the AgentTerm application.

mod about_dialog;
mod mcp_manager;
mod project_editor;
mod release_notes_dialog;
mod session_editor;
mod tab_picker;
mod tool_editor_dialog;

pub use about_dialog::AboutDialog;
pub use mcp_manager::McpManagerDialog;
pub use project_editor::{AddWorkspaceDialog, WorkspaceEditorDialog};
pub use release_notes_dialog::ReleaseNotesDialog;
pub use session_editor::SessionEditorDialog;
pub use tab_picker::TabPickerDialog;
pub use tool_editor_dialog::ToolEditorDialog;

// Re-export AgentTermApp for dialog dependencies
pub use crate::app::AgentTermApp;
