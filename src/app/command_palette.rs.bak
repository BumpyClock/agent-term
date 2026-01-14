//! Command palette for quick navigation and actions.
//!
//! Provides a Cmd+P searchable interface for:
//! - Current sessions
//! - Saved workspaces
//! - Past Claude conversations
//! - App actions (New Tab, Settings, etc.)

use std::sync::Arc;

use agentterm_search::{MessageSource, SearchManager};
use gpui::{
    App, Context, Entity, FocusHandle, Focusable, InteractiveElement, IntoElement, KeyBinding,
    MouseButton, ParentElement, Render, Styled, Subscription, Window, actions, div, prelude::*, px,
};
use gpui_component::input::{Input, InputEvent, InputState};

use crate::icons::{Icon, IconName, IconSize};
use crate::ui::{ActiveTheme, h_flex, v_flex};

// Command palette actions
actions!(command_palette, [SelectUp, SelectDown, Confirm, Cancel]);

/// Initialize command palette keybindings.
pub(crate) fn init(cx: &mut App) {
    let context: Option<&str> = Some("CommandPalette");
    cx.bind_keys([
        KeyBinding::new("escape", Cancel, context),
        KeyBinding::new("enter", Confirm, context),
        KeyBinding::new("up", SelectUp, context),
        KeyBinding::new("down", SelectDown, context),
    ]);
}

/// Result categories for the command palette.
#[derive(Clone, Debug)]
pub enum CommandResult {
    /// Current session in any window
    Session {
        id: String,
        title: String,
        project: String,
    },
    /// Saved workspace
    Workspace {
        id: String,
        name: String,
        created_at: String,
    },
    /// Past Claude conversation from JSONL logs
    PastConversation {
        file_path: String,
        project: String,
        snippet: String,
        session_id: String,
    },
    /// Past Codex conversation from sessions
    CodexConversation {
        file_path: String,
        project: String,
        snippet: String,
        session_id: String,
    },
    /// Static app action
    Action {
        name: String,
        icon: IconName,
        action_id: String,
    },
}

impl CommandResult {
    fn title(&self) -> &str {
        match self {
            CommandResult::Session { title, .. } => title,
            CommandResult::Workspace { name, .. } => name,
            CommandResult::PastConversation { project, .. } => project,
            CommandResult::CodexConversation { project, .. } => project,
            CommandResult::Action { name, .. } => name,
        }
    }

    fn subtitle(&self) -> Option<&str> {
        match self {
            CommandResult::Session { project, .. } => Some(project),
            CommandResult::Workspace { created_at, .. } => Some(created_at),
            CommandResult::PastConversation { snippet, .. } => Some(snippet),
            CommandResult::CodexConversation { snippet, .. } => Some(snippet),
            CommandResult::Action { .. } => None,
        }
    }

    fn icon(&self) -> IconName {
        match self {
            CommandResult::Session { .. } => IconName::Terminal,
            CommandResult::Workspace { .. } => IconName::Folder,
            CommandResult::PastConversation { .. } => IconName::Sparkles,
            CommandResult::CodexConversation { .. } => IconName::Code,
            CommandResult::Action { icon, .. } => *icon,
        }
    }

    fn category(&self) -> &'static str {
        match self {
            CommandResult::Session { .. } => "Sessions",
            CommandResult::Workspace { .. } => "Workspaces",
            CommandResult::PastConversation { .. } => "Claude History",
            CommandResult::CodexConversation { .. } => "Codex History",
            CommandResult::Action { .. } => "Actions",
        }
    }
}

/// Event emitted when a result is selected.
#[derive(Clone, Debug)]
pub struct CommandPaletteSelectEvent(pub CommandResult);

/// Event emitted when the palette is dismissed.
#[derive(Clone, Debug)]
pub struct CommandPaletteDismissEvent;

/// The command palette modal.
pub struct CommandPalette {
    focus_handle: FocusHandle,
    input_state: Entity<InputState>,
    results: Vec<CommandResult>,
    filtered_results: Vec<CommandResult>,
    selected_index: usize,
    search_manager: Option<Arc<SearchManager>>,
    _input_subscription: Subscription,
}

impl CommandPalette {
    pub fn new(window: &mut Window, cx: &mut Context<Self>) -> Self {
        let input_state = cx.new(|cx| {
            InputState::new(window, cx).placeholder("Search sessions, workspaces, actions...")
        });

        let _input_subscription = cx.subscribe_in(&input_state, window, Self::on_input_event);

        Self {
            focus_handle: cx.focus_handle(),
            input_state,
            results: Vec::new(),
            filtered_results: Vec::new(),
            selected_index: 0,
            search_manager: None,
            _input_subscription,
        }
    }

    /// Set the search manager for past conversation search.
    pub fn set_search_manager(&mut self, manager: Arc<SearchManager>) {
        self.search_manager = Some(manager);
    }

    /// Set the available results (sessions, workspaces, actions).
    pub fn set_results(&mut self, results: Vec<CommandResult>, cx: &mut Context<Self>) {
        self.results = results;
        self.filter_results(cx);
    }

    /// Focus the input field.
    pub fn focus(&self, window: &mut Window, cx: &mut Context<Self>) {
        self.input_state.update(cx, |state, cx| {
            state.focus(window, cx);
        });
    }

    fn on_input_event(
        &mut self,
        _input: &Entity<InputState>,
        event: &InputEvent,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if matches!(event, InputEvent::Change) {
            self.filter_results(cx);
        }
    }

    fn filter_results(&mut self, cx: &mut Context<Self>) {
        let query = self.input_state.read(cx).text().to_string().to_lowercase();

        if query.is_empty() {
            self.filtered_results = self.results.clone();
        } else {
            // Filter local results
            self.filtered_results = self
                .results
                .iter()
                .filter(|r| {
                    let title = r.title().to_lowercase();
                    let subtitle = r.subtitle().map(|s| s.to_lowercase()).unwrap_or_default();
                    title.contains(&query) || subtitle.contains(&query)
                })
                .cloned()
                .collect();

            // Add past conversation results from search manager
            if let Some(manager) = &self.search_manager {
                let search_results = manager.search(&query, 10);
                for result in search_results {
                    if let Some(session_id) = result.session_id {
                        let cmd_result = match result.source {
                            MessageSource::Claude => CommandResult::PastConversation {
                                file_path: result.file_path,
                                project: result.project_name,
                                snippet: result.snippet,
                                session_id,
                            },
                            MessageSource::Codex => CommandResult::CodexConversation {
                                file_path: result.file_path,
                                project: result.project_name,
                                snippet: result.snippet,
                                session_id,
                            },
                        };
                        self.filtered_results.push(cmd_result);
                    }
                }
            }
        }

        // Reset selection
        self.selected_index = 0;
        cx.notify();
    }

    fn select_up(&mut self, _: &SelectUp, _window: &mut Window, cx: &mut Context<Self>) {
        if !self.filtered_results.is_empty() {
            self.selected_index = self
                .selected_index
                .checked_sub(1)
                .unwrap_or(self.filtered_results.len() - 1);
            cx.notify();
        }
    }

    fn select_down(&mut self, _: &SelectDown, _window: &mut Window, cx: &mut Context<Self>) {
        if !self.filtered_results.is_empty() {
            self.selected_index = (self.selected_index + 1) % self.filtered_results.len();
            cx.notify();
        }
    }

    fn confirm(&mut self, _: &Confirm, _window: &mut Window, cx: &mut Context<Self>) {
        if let Some(result) = self.filtered_results.get(self.selected_index).cloned() {
            cx.emit(CommandPaletteSelectEvent(result));
        }
    }

    fn cancel(&mut self, _: &Cancel, _window: &mut Window, cx: &mut Context<Self>) {
        cx.emit(CommandPaletteDismissEvent);
    }

    fn render_result(
        &self,
        index: usize,
        result: &CommandResult,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let is_selected = index == self.selected_index;

        h_flex()
            .id(("result", index))
            .w_full()
            .px_3()
            .py_2()
            .gap_3()
            .rounded_md()
            .cursor_pointer()
            .when(is_selected, |this| this.bg(cx.theme().accent))
            .hover(|this| this.bg(cx.theme().accent.opacity(0.5)))
            .on_mouse_down(
                MouseButton::Left,
                cx.listener(move |this, _, window, cx| {
                    this.selected_index = index;
                    this.confirm(&Confirm, window, cx);
                }),
            )
            .child(
                Icon::new(result.icon())
                    .size(IconSize::Small)
                    .color(if is_selected {
                        cx.theme().accent_foreground
                    } else {
                        cx.theme().muted_foreground
                    }),
            )
            .child(
                v_flex()
                    .flex_1()
                    .overflow_hidden()
                    .child(
                        div()
                            .text_sm()
                            .font_weight(gpui::FontWeight::MEDIUM)
                            .text_color(if is_selected {
                                cx.theme().accent_foreground
                            } else {
                                cx.theme().foreground
                            })
                            .text_ellipsis()
                            .child(result.title().to_string()),
                    )
                    .when_some(result.subtitle(), |this, subtitle| {
                        this.child(
                            div()
                                .text_xs()
                                .text_color(if is_selected {
                                    cx.theme().accent_foreground.opacity(0.8)
                                } else {
                                    cx.theme().muted_foreground
                                })
                                .text_ellipsis()
                                .child(subtitle.to_string()),
                        )
                    }),
            )
            .child(
                div()
                    .text_xs()
                    .text_color(cx.theme().muted_foreground)
                    .child(result.category()),
            )
    }
}

impl Focusable for CommandPalette {
    fn focus_handle(&self, _cx: &App) -> FocusHandle {
        self.focus_handle.clone()
    }
}

impl gpui::EventEmitter<CommandPaletteSelectEvent> for CommandPalette {}
impl gpui::EventEmitter<CommandPaletteDismissEvent> for CommandPalette {}

impl Render for CommandPalette {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let input = self.input_state.clone();
        let is_empty = self.filtered_results.is_empty();
        let muted_foreground = cx.theme().muted_foreground;
        let border_color = cx.theme().border;
        let background = cx.theme().background;

        // Build results list using a for loop to avoid closure borrow issues
        let mut results_list = v_flex()
            .id("command-palette-results")
            .flex_1()
            .overflow_y_scroll()
            .py_2();

        for (i, result) in self.filtered_results.iter().enumerate() {
            results_list = results_list.child(self.render_result(i, result, cx));
        }

        if is_empty {
            results_list = results_list.child(
                div()
                    .w_full()
                    .py_8()
                    .flex()
                    .items_center()
                    .justify_center()
                    .text_sm()
                    .text_color(muted_foreground)
                    .child("No results found"),
            );
        }

        v_flex()
            .key_context("CommandPalette")
            .track_focus(&self.focus_handle)
            .on_action(cx.listener(Self::select_up))
            .on_action(cx.listener(Self::select_down))
            .on_action(cx.listener(Self::confirm))
            .on_action(cx.listener(Self::cancel))
            .w(px(600.))
            .max_h(px(500.))
            .bg(background)
            .border_1()
            .border_color(border_color)
            .rounded_lg()
            .shadow_xl()
            .overflow_hidden()
            // Search input
            .child(
                div()
                    .w_full()
                    .px_3()
                    .py_2()
                    .border_b_1()
                    .border_color(border_color)
                    .child(Input::new(&input).appearance(false)),
            )
            // Results list
            .child(results_list)
            // Footer with keyboard hints
            .child(
                div()
                    .w_full()
                    .px_3()
                    .py_2()
                    .border_t_1()
                    .border_color(border_color)
                    .flex()
                    .items_center()
                    .gap_4()
                    .text_xs()
                    .text_color(muted_foreground)
                    .child("↑↓ Navigate")
                    .child("⏎ Select")
                    .child("Esc Close"),
            )
    }
}

/// Static actions available in the command palette.
pub fn default_actions() -> Vec<CommandResult> {
    vec![
        CommandResult::Action {
            name: "New Tab".to_string(),
            icon: IconName::Plus,
            action_id: "new_tab".to_string(),
        },
        CommandResult::Action {
            name: "New Window".to_string(),
            icon: IconName::ExternalLink,
            action_id: "new_window".to_string(),
        },
        CommandResult::Action {
            name: "Settings".to_string(),
            icon: IconName::Settings,
            action_id: "settings".to_string(),
        },
        CommandResult::Action {
            name: "Toggle Sidebar".to_string(),
            icon: IconName::Menu,
            action_id: "toggle_sidebar".to_string(),
        },
    ]
}
