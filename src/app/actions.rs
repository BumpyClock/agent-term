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
pub struct EditSection(pub String);

#[derive(Clone, PartialEq, serde::Deserialize, schemars::JsonSchema, gpui::Action)]
pub struct RemoveSection(pub String);

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
