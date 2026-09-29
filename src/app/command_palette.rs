//! Command palette for AgentTerm, built on the gpui-kit `Command` component.

use std::rc::Rc;

use agentterm_search::MessageSource;
use agentterm_session::SessionRecord;
use gpui_kit::component::command::{Command, CommandGroup, CommandItem, CommandState};
use gpui_kit::component::kbd::Kbd;
use gpui_kit::component::{Icon, IconName};
use gpui_kit::{
    div, px, AppContext as _, Context, Entity, EventEmitter, IntoElement,
    Keystroke, ParentElement as _, Render, SharedString, Styled as _, Window,
};

use super::search_manager;
use crate::ui::{ActiveTheme as _, WorkspaceItem};
use gpui_kit::component::{h_flex, v_flex};

const PALETTE_MAX_HEIGHT: f32 = 500.0;
const HISTORY_RESULT_LIMIT: usize = 10;
const MINIMUM_HISTORY_QUERY_LENGTH: usize = 2;

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
        workspace: String,
    },
    CodexConversation {
        session_id: String,
        workspace: String,
    },
    Action {
        action_id: String,
    },
}

pub enum CommandPaletteEvent {
    Selected(CommandPalettePayload),
    Dismissed,
}

#[derive(Clone)]
struct PaletteRow {
    label: SharedString,
    subtitle: Option<SharedString>,
    icon: IconName,
    shortcut: Option<&'static str>,
    payload: CommandPalettePayload,
}

impl PaletteRow {
    fn into_item(self) -> (CommandItem, CommandPalettePayload) {
        let PaletteRow {
            label,
            subtitle,
            icon,
            shortcut,
            payload,
        } = self;
        let item = CommandItem::new().label(label.clone()).child(move |_window, cx| {
            let muted = cx.theme().muted_foreground;
            h_flex()
                .w_full()
                .gap_2()
                .items_center()
                .child(Icon::new(icon.clone()).text_color(muted))
                .child(
                    v_flex()
                        .flex_1()
                        .min_w_0()
                        .child(div().truncate().child(label.clone()))
                        .when_some(subtitle.clone(), |this, subtitle| {
                            this.child(
                                div()
                                    .truncate()
                                    .text_xs()
                                    .text_color(muted)
                                    .child(subtitle),
                            )
                        }),
                )
                .when_some(
                    shortcut.and_then(|shortcut| Keystroke::parse(shortcut).ok()),
                    |this, keystroke| this.child(Kbd::new(keystroke)),
                )
        });
        (item, payload)
    }
}

use gpui_kit::prelude::FluentBuilder as _;

struct HistoryEntry {
    source: MessageSource,
    session_id: String,
    workspace: String,
    snippet: String,
}

/// Dialog content that owns the palette state and the entries shown in it.
pub struct CommandPaletteView {
    state: Entity<CommandState>,
    sessions: Vec<SessionRecord>,
    workspaces: Vec<WorkspaceItem>,
    query: String,
    history: Vec<HistoryEntry>,
}

impl EventEmitter<CommandPaletteEvent> for CommandPaletteView {}

impl CommandPaletteView {
    pub fn new(
        sessions: Vec<SessionRecord>,
        workspaces: Vec<WorkspaceItem>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let state = cx.new(|cx| CommandState::new(window, cx));
        state.update(cx, |state, cx| state.focus(window, cx));
        Self {
            state,
            sessions,
            workspaces,
            query: String::new(),
            history: Vec::new(),
        }
    }

    pub fn dismiss(&mut self, cx: &mut Context<Self>) {
        cx.emit(CommandPaletteEvent::Dismissed);
    }

    fn set_query(&mut self, query: &str, cx: &mut Context<Self>) {
        self.query = query.trim().to_string();
        self.history = self.search_history();
        cx.notify();
    }

    fn search_history(&self) -> Vec<HistoryEntry> {
        if self.query.len() < MINIMUM_HISTORY_QUERY_LENGTH
            || search_manager::search_indexing_in_progress()
        {
            return Vec::new();
        }
        let Some(search_manager) = search_manager::try_global_search_manager() else {
            return Vec::new();
        };
        search_manager
            .search(&self.query, HISTORY_RESULT_LIMIT)
            .into_iter()
            .filter_map(|result| {
                Some(HistoryEntry {
                    source: result.source,
                    session_id: result.session_id?,
                    workspace: result.workspace_name,
                    snippet: result.snippet,
                })
            })
            .collect()
    }

    fn matches_query(&self, text: &str) -> bool {
        self.query.is_empty() || text.to_lowercase().contains(&self.query.to_lowercase())
    }

    fn action_rows(&self) -> Vec<PaletteRow> {
        [
            ("new_tab", "New Tab", IconName::Plus, "cmd-t"),
            ("new_window", "New Window", IconName::ExternalLink, "cmd-n"),
            ("settings", "Settings", IconName::Settings, "cmd-,"),
            ("toggle_sidebar", "Toggle Sidebar", IconName::Menu, "cmd-b"),
        ]
        .into_iter()
        .filter(|(_, label, _, _)| self.matches_query(label))
        .map(|(action_id, label, icon, shortcut)| PaletteRow {
            label: label.into(),
            subtitle: None,
            icon,
            shortcut: Some(shortcut),
            payload: CommandPalettePayload::Action {
                action_id: action_id.to_string(),
            },
        })
        .collect()
    }

    fn workspace_rows(&self) -> Vec<PaletteRow> {
        self.workspaces
            .iter()
            .filter(|item| self.matches_query(&item.workspace.name))
            .map(|item| {
                let subtitle = if item.workspace.path.is_empty() {
                    if item.is_default {
                        "Default workspace".to_string()
                    } else {
                        "No path".to_string()
                    }
                } else {
                    item.workspace.path.clone()
                };
                PaletteRow {
                    label: item.workspace.name.clone().into(),
                    subtitle: Some(subtitle.into()),
                    icon: if item.is_default {
                        IconName::SquareTerminal
                    } else {
                        IconName::Folder
                    },
                    shortcut: None,
                    payload: CommandPalettePayload::Workspace {
                        id: item.workspace.id.clone(),
                    },
                }
            })
            .collect()
    }

    fn session_rows(&self) -> Vec<PaletteRow> {
        if self.history_search_inactive() {
            return Vec::new();
        }
        self.sessions
            .iter()
            .filter(|session| self.matches_query(&session.title))
            .map(|session| PaletteRow {
                label: session.title.clone().into(),
                subtitle: Some(format!("{:?}", session.tool).into()),
                icon: IconName::SquareTerminal,
                shortcut: None,
                payload: CommandPalettePayload::Session {
                    id: session.id.clone(),
                },
            })
            .collect()
    }

    fn history_search_inactive(&self) -> bool {
        self.query.len() < MINIMUM_HISTORY_QUERY_LENGTH
            || search_manager::try_global_search_manager().is_none()
            || search_manager::search_indexing_in_progress()
    }

    fn history_rows(&self, source: MessageSource) -> Vec<PaletteRow> {
        self.history
            .iter()
            .filter(|entry| entry.source == source)
            .map(|entry| {
                let (icon, payload) = match source {
                    MessageSource::Claude => (
                        IconName::Bot,
                        CommandPalettePayload::ClaudeConversation {
                            session_id: entry.session_id.clone(),
                            workspace: entry.workspace.clone(),
                        },
                    ),
                    MessageSource::Codex => (
                        IconName::File,
                        CommandPalettePayload::CodexConversation {
                            session_id: entry.session_id.clone(),
                            workspace: entry.workspace.clone(),
                        },
                    ),
                };
                PaletteRow {
                    label: entry.workspace.clone().into(),
                    subtitle: Some(entry.snippet.clone().into()),
                    icon,
                    shortcut: None,
                    payload,
                }
            })
            .collect()
    }
}

impl Render for CommandPaletteView {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let groups = [
            ("Actions", self.action_rows()),
            ("Workspaces", self.workspace_rows()),
            ("Sessions", self.session_rows()),
            ("Claude History", self.history_rows(MessageSource::Claude)),
            ("Codex History", self.history_rows(MessageSource::Codex)),
        ];

        let mut command = Command::new(&self.state);
        let mut payloads: Vec<Vec<CommandPalettePayload>> = Vec::new();
        for (label, rows) in groups {
            if rows.is_empty() {
                continue;
            }
            let mut group = CommandGroup::new().label(label);
            let mut group_payloads = Vec::with_capacity(rows.len());
            for row in rows {
                let (item, payload) = row.into_item();
                group = group.item(item);
                group_payloads.push(payload);
            }
            command = command.group(group);
            payloads.push(group_payloads);
        }
        let payloads = Rc::new(payloads);

        let view = cx.entity().downgrade();
        let indexing = !self.query.is_empty() && search_manager::search_indexing_in_progress();

        command
            .filterable(false)
            .bordered(false)
            .max_h(px(PALETTE_MAX_HEIGHT))
            .placeholder("Search sessions, workspaces, actions...")
            .on_query({
                let view = view.clone();
                move |query, _window, cx| {
                    view.update(cx, |this, cx| this.set_query(query, cx)).ok();
                }
            })
            .on_confirm(move |index_path, _window, cx| {
                let payload = payloads
                    .get(index_path.section)
                    .and_then(|group| group.get(index_path.row))
                    .cloned();
                if let Some(payload) = payload {
                    view.update(cx, |_, cx| cx.emit(CommandPaletteEvent::Selected(payload)))
                        .ok();
                }
            })
            .when(indexing, |command| {
                command.footer(|_state, _window, cx| {
                    div()
                        .px_3()
                        .py_2()
                        .text_xs()
                        .text_color(cx.theme().muted_foreground)
                        .child("Indexing search history...")
                })
            })
    }
}
