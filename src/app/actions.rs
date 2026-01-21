//! Action definitions for the AgentTerm application.

use gpui::actions;

actions!(
    agentterm_gpui,
    [
        Quit,
        ToggleSidebar,
        ToggleMcpManager,
        NewShellTab,
        NewWindow,
        CloseTab,
        CloseWindow,
        ReopenClosed,
        OpenSettings,
        // Window actions (cross-platform)
        Minimize,
        Zoom,
        // macOS-only actions (defined here, but only registered on macOS)
        About,
        Hide,
        HideOthers,
        ShowAll,
    ]
);

// Actions with data for context menu items

#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct RenameSession(pub String);

#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct CloseSessionAction(pub String);

#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct RestartSessionAction(pub String);

#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct EditWorkspace(pub String);

#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct RemoveWorkspace(pub String);

// Multi-window session transfer actions

/// Move a session to another window (terminal stays running in pool).
#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct MoveSessionToWindow {
    pub session_id: String,
    pub target_window_id: u64,
}

/// Open a session in a new window (moves from current window).
#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct OpenSessionInNewWindow(pub String);

/// Move a workspace to another window.
#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct MoveWorkspaceToWindow {
    pub workspace_id: String,
    pub target_window_id: u64,
}

/// Move a workspace to a new window.
#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct MoveWorkspaceToNewWindow(pub String);

// Command palette actions

/// Toggle the command palette (Alt+K).
#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct ToggleCommandPalette;
