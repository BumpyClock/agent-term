//! CommandPalette provider for AgentTerm.
//!
//! Provides sessions, workspaces, actions, and search history items
//! to the gpui-component CommandPalette.

use agentterm_search::MessageSource;
use agentterm_session::SessionRecord;
use gpui::{App, Task};
use gpui_component::command_palette::{CommandPaletteItem, CommandPaletteProvider};
use gpui_component::IconName;

use super::search_manager;
use crate::ui::WorkspaceItem;

/// Payload for command palette items to distinguish selection types.
#[derive(Clone, Debug)]
pub enum CommandPalettePayload {
    Session {
        id: String,
    },
    Workspace {
        id: String,
    },
    ClaudeConversation {
        session_id: String,
        file_path: String,
        workspace: String,
    },
    CodexConversation {
        session_id: String,
        file_path: String,
        workspace: String,
    },
    Action {
        action_id: String,
    },
}

/// Provider for AgentTerm command palette items.
pub struct AgentTermProvider {
    sessions: Vec<SessionRecord>,
    workspaces: Vec<WorkspaceItem>,
}

impl AgentTermProvider {
    pub fn new(sessions: Vec<SessionRecord>, workspaces: Vec<WorkspaceItem>) -> Self {
        Self {
            sessions,
            workspaces,
        }
    }
}

impl CommandPaletteProvider for AgentTermProvider {
    fn items(&self, _cx: &App) -> Vec<CommandPaletteItem> {
        let mut items = Vec::new();

        // Add static commands
        items.push(
            CommandPaletteItem::new("new_tab", "New Tab")
                .category("Actions")
                .icon(IconName::Plus)
                .shortcut("cmd-t")
                .payload(CommandPalettePayload::Action {
                    action_id: "new_tab".to_string(),
                }),
        );
        items.push(
            CommandPaletteItem::new("new_window", "New Window")
                .category("Actions")
                .icon(IconName::ExternalLink)
                .shortcut("cmd-n")
                .payload(CommandPalettePayload::Action {
                    action_id: "new_window".to_string(),
                }),
        );
        items.push(
            CommandPaletteItem::new("settings", "Settings")
                .category("Actions")
                .icon(IconName::Settings)
                .shortcut("cmd-,")
                .payload(CommandPalettePayload::Action {
                    action_id: "settings".to_string(),
                }),
        );
        items.push(
            CommandPaletteItem::new("toggle_sidebar", "Toggle Sidebar")
                .category("Actions")
                .icon(IconName::Menu)
                .shortcut("cmd-b")
                .payload(CommandPalettePayload::Action {
                    action_id: "toggle_sidebar".to_string(),
                }),
        );

        // Add workspaces
        for workspace in &self.workspaces {
            let subtitle = if workspace.workspace.path.is_empty() {
                if workspace.is_default {
                    "Default workspace".to_string()
                } else {
                    "No path".to_string()
                }
            } else {
                workspace.workspace.path.clone()
            };
            let icon = if workspace.is_default {
                IconName::SquareTerminal
            } else {
                IconName::Folder
            };
            items.push(
                CommandPaletteItem::new(&workspace.workspace.id, &workspace.workspace.name)
                    .category("Workspaces")
                    .subtitle(subtitle)
                    .icon(icon)
                    .payload(CommandPalettePayload::Workspace {
                        id: workspace.workspace.id.clone(),
                    }),
            );
        }

        items
    }

    fn query(&self, query: &str, _cx: &App) -> Task<Vec<CommandPaletteItem>> {
        let mut items = Vec::new();

        let Some(search_manager) = search_manager::try_global_search_manager() else {
            return Task::ready(items);
        };

        if query.len() < 2 {
            return Task::ready(items);
        }

        if search_manager::search_indexing_in_progress() {
            return Task::ready(items);
        }

        for session in &self.sessions {
            items.push(
                CommandPaletteItem::new(&session.id, &session.title)
                    .category("Sessions")
                    .subtitle(format!("{:?}", session.tool))
                    .icon(IconName::SquareTerminal)
                    .payload(CommandPalettePayload::Session {
                        id: session.id.clone(),
                    }),
            );
        }

        let search_results = search_manager.search(query, 10);

        for result in search_results {
            if let Some(session_id) = result.session_id {
                let item = match result.source {
                    MessageSource::Claude => CommandPaletteItem::new(
                        format!("claude_{}", session_id),
                        &result.workspace_name,
                    )
                    .category("Claude History")
                    .subtitle(&result.snippet)
                    .icon(IconName::Bot)
                    .payload(CommandPalettePayload::ClaudeConversation {
                        session_id,
                        file_path: result.file_path,
                        workspace: result.workspace_name,
                    }),
                    MessageSource::Codex => CommandPaletteItem::new(
                        format!("codex_{}", session_id),
                        &result.workspace_name,
                    )
                    .category("Codex History")
                    .subtitle(&result.snippet)
                    .icon(IconName::File)
                    .payload(CommandPalettePayload::CodexConversation {
                        session_id,
                        file_path: result.file_path,
                        workspace: result.workspace_name,
                    }),
                };
                items.push(item);
            }
        }

        Task::ready(items)
    }
}
