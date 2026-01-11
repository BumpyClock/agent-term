//! Project editor dialog for editing project name, path, and icon.

use std::collections::HashSet;
use std::path::{Path, PathBuf};

use gpui::{ClickEvent, Context, Entity, IntoElement, Render, Styled, Window, div, prelude::*, px};
use gpui_component::input::{InputEvent, InputState as GpuiInputState};
use rfd::FileDialog;

use crate::icons::IconDescriptor;
use crate::ui::{
    ActiveTheme, Button, IconPicker, WindowExt,
    helpers::{agentterm_input_field, icon_descriptor_from_string, icon_descriptor_to_string},
    v_flex,
};

use super::AgentTermApp;

const MAX_RECENT_PATHS: usize = 5;
const MAX_PATH_SUGGESTIONS: usize = 8;

struct PathAutocompleteState {
    recent_paths: Vec<String>,
    suggestions: Vec<String>,
    show_suggestions: bool,
    error: Option<String>,
}

impl PathAutocompleteState {
    fn new(recent_paths: Vec<String>) -> Self {
        Self {
            recent_paths,
            suggestions: Vec::new(),
            show_suggestions: false,
            error: None,
        }
    }

    fn update_suggestions(&mut self, value: &str) {
        self.suggestions = build_path_suggestions(value, &self.recent_paths);
        self.show_suggestions = true;
    }

    fn clear_suggestions(&mut self) {
        self.suggestions.clear();
        self.show_suggestions = false;
    }

    fn set_error(&mut self, error: Option<String>) {
        self.error = error;
    }
}

/// ProjectEditorDialog - A dialog for editing project name, path, and icon.
pub struct ProjectEditorDialog {
    view: Entity<AgentTermApp>,
    section_id: String,
    name_input: Entity<GpuiInputState>,
    path_input: Entity<GpuiInputState>,
    current_icon: Option<String>,
    path_state: PathAutocompleteState,
}

impl ProjectEditorDialog {
    pub fn new(
        view: Entity<AgentTermApp>,
        section_id: String,
        name_input: Entity<GpuiInputState>,
        path_input: Entity<GpuiInputState>,
        current_icon: Option<String>,
        recent_paths: Vec<String>,
    ) -> Self {
        Self {
            view,
            section_id,
            name_input,
            path_input,
            current_icon,
            path_state: PathAutocompleteState::new(recent_paths),
        }
    }

    pub fn set_icon(&mut self, icon: Option<IconDescriptor>, cx: &mut Context<Self>) {
        self.current_icon = icon.map(|d| icon_descriptor_to_string(&d));
        cx.notify();
    }

    pub fn setup_path_input_subscriptions(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        cx.subscribe_in(
            &self.path_input,
            window,
            |this, input, event: &InputEvent, _window, cx| match event {
                InputEvent::Change => {
                    let value = input.read(cx).value().to_string();
                    this.path_state.set_error(None);
                    this.path_state.update_suggestions(&value);
                    cx.notify();
                }
                InputEvent::Focus => {
                    let value = input.read(cx).value().to_string();
                    this.path_state.update_suggestions(&value);
                    cx.notify();
                }
                InputEvent::Blur => {
                    let value = input.read(cx).value().to_string();
                    let normalized = normalize_path_value(&value);
                    this.path_state.set_error(validate_path_value(&normalized));
                    this.path_state.clear_suggestions();
                    cx.notify();
                }
                InputEvent::PressEnter { .. } => {}
            },
        )
        .detach();
    }

    fn apply_path_suggestion(
        &mut self,
        value: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.path_input.update(cx, |input, cx| {
            input.set_value(value, window, cx);
        });
        self.path_state.set_error(None);
        self.path_state.clear_suggestions();
        cx.notify();
    }

    fn browse_for_path(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let current_value = self.path_input.read(cx).value().to_string();
        let mut dialog = FileDialog::new();
        if let Some(start_dir) = resolve_picker_start_path(&current_value) {
            dialog = dialog.set_directory(start_dir);
        }
        if let Some(path) = dialog.pick_folder() {
            self.path_input.update(cx, |input, cx| {
                input.set_value(path.to_string_lossy().to_string(), window, cx);
            });
            self.path_state.set_error(None);
            self.path_state.clear_suggestions();
            cx.notify();
        }
    }

    pub fn save(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let name = self
            .name_input
            .read(cx)
            .value()
            .to_string()
            .trim()
            .to_string();
        let path = self
            .path_input
            .read(cx)
            .value()
            .to_string()
            .trim()
            .to_string();

        if name.is_empty() {
            return;
        }

        let normalized_path = normalize_path_value(&path);
        if let Some(error) = validate_path_value(&normalized_path) {
            self.path_state.set_error(Some(error));
            cx.notify();
            return;
        }

        window.close_dialog(cx);

        let view = self.view.clone();
        let section_id = self.section_id.clone();
        let icon = self.current_icon.clone();
        view.update(cx, |app, cx| {
            let _ = app.session_store.rename_section(&section_id, name);
            let _ = app
                .session_store
                .set_section_path(&section_id, normalized_path);
            let _ = app.session_store.set_section_icon(&section_id, icon);
            app.reload_from_store(cx);
            app.ensure_active_terminal(window, cx);
        });
    }
}

impl Render for ProjectEditorDialog {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let current_icon = self.current_icon.clone();
        let entity = cx.entity().clone();
        let icon_entity = entity.clone();

        v_flex()
            .gap(px(16.))
            // Icon section with inline IconPicker
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(8.))
                    .child(
                        div()
                            .text_sm()
                            .text_color(cx.theme().muted_foreground)
                            .child("Icon"),
                    )
                    .child(
                        IconPicker::new("project-icon-picker")
                            .value(
                                current_icon
                                    .as_ref()
                                    .map(|s| icon_descriptor_from_string(s)),
                            )
                            .on_change(move |icon, _window, cx| {
                                icon_entity.update(cx, |this, cx| {
                                    this.set_icon(icon, cx);
                                });
                            }),
                    ),
            )
            // Name input
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(8.))
                    .child(
                        div()
                            .text_sm()
                            .text_color(cx.theme().muted_foreground)
                            .child("Name"),
                    )
                    .child(agentterm_input_field(&self.name_input)),
            )
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(8.))
                    .child(
                        div()
                            .text_sm()
                            .text_color(cx.theme().muted_foreground)
                            .child("Path"),
                    )
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap(px(8.))
                            .child(agentterm_input_field(&self.path_input).flex_1())
                            .child(
                                Button::new("project-path-browse")
                                    .label("Browse")
                                    .compact()
                                    .on_click(cx.listener(|this, _: &ClickEvent, window, cx| {
                                        this.browse_for_path(window, cx);
                                    })),
                            ),
                    )
                    .when(
                        self.path_state.show_suggestions && !self.path_state.suggestions.is_empty(),
                        |el| {
                            let mut list = div()
                                .mt(px(6.))
                                .rounded(px(6.))
                                .border_1()
                                .border_color(cx.theme().border)
                                .bg(cx.theme().background)
                                .px(px(4.))
                                .py(px(4.))
                                .flex()
                                .flex_col()
                                .gap(px(2.));
                            for (index, suggestion) in
                                self.path_state.suggestions.iter().enumerate()
                            {
                                let suggestion_value = suggestion.clone();
                                list = list.child(
                                    div()
                                        .id(format!("project-path-suggestion-{}", index))
                                        .px(px(6.))
                                        .py(px(4.))
                                        .rounded(px(4.))
                                        .cursor_pointer()
                                        .hover(|s| s.bg(cx.theme().list_hover))
                                        .text_sm()
                                        .text_color(cx.theme().foreground)
                                        .child(suggestion_value.clone())
                                        .on_click(cx.listener({
                                            let suggestion_value = suggestion_value.clone();
                                            move |this, _: &ClickEvent, window, cx| {
                                                this.apply_path_suggestion(
                                                    suggestion_value.clone(),
                                                    window,
                                                    cx,
                                                );
                                            }
                                        })),
                                );
                            }
                            el.child(list)
                        },
                    )
                    .when_some(self.path_state.error.as_ref(), |el, err| {
                        el.child(
                            div()
                                .text_sm()
                                .text_color(cx.theme().danger)
                                .child(err.clone()),
                        )
                    }),
            )
    }
}

pub struct AddProjectDialog {
    view: Entity<AgentTermApp>,
    name_input: Entity<GpuiInputState>,
    path_input: Entity<GpuiInputState>,
    current_icon: Option<String>,
    path_state: PathAutocompleteState,
}

impl AddProjectDialog {
    pub fn new(
        view: Entity<AgentTermApp>,
        name_input: Entity<GpuiInputState>,
        path_input: Entity<GpuiInputState>,
        recent_paths: Vec<String>,
    ) -> Self {
        Self {
            view,
            name_input,
            path_input,
            current_icon: None,
            path_state: PathAutocompleteState::new(recent_paths),
        }
    }

    pub fn set_icon(&mut self, icon: Option<IconDescriptor>, cx: &mut Context<Self>) {
        self.current_icon = icon.map(|d| icon_descriptor_to_string(&d));
        cx.notify();
    }

    pub fn setup_path_input_subscriptions(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        cx.subscribe_in(
            &self.path_input,
            window,
            |this, input, event: &InputEvent, _window, cx| match event {
                InputEvent::Change => {
                    let value = input.read(cx).value().to_string();
                    this.path_state.set_error(None);
                    this.path_state.update_suggestions(&value);
                    cx.notify();
                }
                InputEvent::Focus => {
                    let value = input.read(cx).value().to_string();
                    this.path_state.update_suggestions(&value);
                    cx.notify();
                }
                InputEvent::Blur => {
                    let value = input.read(cx).value().to_string();
                    let normalized = normalize_path_value(&value);
                    this.path_state.set_error(validate_path_value(&normalized));
                    this.path_state.clear_suggestions();
                    cx.notify();
                }
                InputEvent::PressEnter { .. } => {}
            },
        )
        .detach();
    }

    fn apply_path_suggestion(
        &mut self,
        value: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.path_input.update(cx, |input, cx| {
            input.set_value(value, window, cx);
        });
        self.path_state.set_error(None);
        self.path_state.clear_suggestions();
        cx.notify();
    }

    fn browse_for_path(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let current_value = self.path_input.read(cx).value().to_string();
        let mut dialog = FileDialog::new();
        if let Some(start_dir) = resolve_picker_start_path(&current_value) {
            dialog = dialog.set_directory(start_dir);
        }
        if let Some(path) = dialog.pick_folder() {
            self.path_input.update(cx, |input, cx| {
                input.set_value(path.to_string_lossy().to_string(), window, cx);
            });
            self.path_state.set_error(None);
            self.path_state.clear_suggestions();
            cx.notify();
        }
    }

    pub fn save(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let name = self
            .name_input
            .read(cx)
            .value()
            .to_string()
            .trim()
            .to_string();
        let path = self
            .path_input
            .read(cx)
            .value()
            .to_string()
            .trim()
            .to_string();

        if name.is_empty() {
            return;
        }

        let normalized_path = normalize_path_value(&path);
        if let Some(error) = validate_path_value(&normalized_path) {
            self.path_state.set_error(Some(error));
            cx.notify();
            return;
        }

        window.close_dialog(cx);

        let view = self.view.clone();
        let icon = self
            .current_icon
            .clone()
            .or_else(|| Some(icon_descriptor_to_string(&IconDescriptor::lucide("folder"))));

        view.update(cx, |app, cx| {
            if let Ok(section) = app.session_store.create_section(name, normalized_path) {
                let _ = app.session_store.set_section_icon(&section.id, icon);
                app.reload_from_store(cx);
                app.ensure_active_terminal(window, cx);
            }
        });
    }
}

impl Render for AddProjectDialog {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let current_icon = self.current_icon.clone();
        let entity = cx.entity().clone();
        let icon_entity = entity.clone();

        v_flex()
            .gap(px(16.))
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(8.))
                    .child(
                        div()
                            .text_sm()
                            .text_color(cx.theme().muted_foreground)
                            .child("Icon"),
                    )
                    .child(
                        IconPicker::new("add-project-icon-picker")
                            .value(
                                current_icon
                                    .as_ref()
                                    .map(|s| icon_descriptor_from_string(s)),
                            )
                            .on_change(move |icon, _window, cx| {
                                icon_entity.update(cx, |this, cx| {
                                    this.set_icon(icon, cx);
                                });
                            }),
                    ),
            )
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(8.))
                    .child(
                        div()
                            .text_sm()
                            .text_color(cx.theme().muted_foreground)
                            .child("Name"),
                    )
                    .child(agentterm_input_field(&self.name_input)),
            )
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap(px(8.))
                    .child(
                        div()
                            .text_sm()
                            .text_color(cx.theme().muted_foreground)
                            .child("Path"),
                    )
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap(px(8.))
                            .child(agentterm_input_field(&self.path_input).flex_1())
                            .child(
                                Button::new("add-project-path-browse")
                                    .label("Browse")
                                    .compact()
                                    .on_click(cx.listener(|this, _: &ClickEvent, window, cx| {
                                        this.browse_for_path(window, cx);
                                    })),
                            ),
                    )
                    .when(
                        self.path_state.show_suggestions && !self.path_state.suggestions.is_empty(),
                        |el| {
                            let mut list = div()
                                .mt(px(6.))
                                .rounded(px(6.))
                                .border_1()
                                .border_color(cx.theme().border)
                                .bg(cx.theme().background)
                                .px(px(4.))
                                .py(px(4.))
                                .flex()
                                .flex_col()
                                .gap(px(2.));
                            for (index, suggestion) in
                                self.path_state.suggestions.iter().enumerate()
                            {
                                let suggestion_value = suggestion.clone();
                                list = list.child(
                                    div()
                                        .id(format!("add-project-path-suggestion-{}", index))
                                        .px(px(6.))
                                        .py(px(4.))
                                        .rounded(px(4.))
                                        .cursor_pointer()
                                        .hover(|s| s.bg(cx.theme().list_hover))
                                        .text_sm()
                                        .text_color(cx.theme().foreground)
                                        .child(suggestion_value.clone())
                                        .on_click(cx.listener({
                                            let suggestion_value = suggestion_value.clone();
                                            move |this, _: &ClickEvent, window, cx| {
                                                this.apply_path_suggestion(
                                                    suggestion_value.clone(),
                                                    window,
                                                    cx,
                                                );
                                            }
                                        })),
                                );
                            }
                            el.child(list)
                        },
                    )
                    .when_some(self.path_state.error.as_ref(), |el, err| {
                        el.child(
                            div()
                                .text_sm()
                                .text_color(cx.theme().danger)
                                .child(err.clone()),
                        )
                    }),
            )
    }
}

fn normalize_path_value(value: &str) -> String {
    expand_tilde(value).unwrap_or_else(|| value.to_string())
}

fn expand_tilde(value: &str) -> Option<String> {
    let trimmed = value.trim();
    if !trimmed.starts_with('~') {
        return Some(trimmed.to_string());
    }
    let home = dirs::home_dir()?;
    if trimmed == "~" {
        return Some(home.to_string_lossy().to_string());
    }
    let without_tilde = trimmed.strip_prefix("~").unwrap_or(trimmed);
    let expanded = home.join(without_tilde.trim_start_matches(std::path::MAIN_SEPARATOR));
    Some(expanded.to_string_lossy().to_string())
}

fn resolve_picker_start_path(value: &str) -> Option<PathBuf> {
    let home_dir = dirs::home_dir();
    let expanded = expand_tilde(value).unwrap_or_default();
    let path = Path::new(&expanded);
    if path.is_dir() {
        return Some(path.to_path_buf());
    }
    if let Some(parent) = path.parent() {
        if parent.is_dir() {
            return Some(parent.to_path_buf());
        }
    }
    home_dir
}

fn validate_path_value(value: &str) -> Option<String> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        return None;
    }
    if trimmed.contains("..") {
        return Some("Path traversal not allowed".to_string());
    }
    match std::fs::canonicalize(trimmed) {
        Ok(_) => None,
        Err(error) => Some(format!("Invalid path '{}': {}", trimmed, error)),
    }
}

fn build_path_suggestions(value: &str, recent_paths: &[String]) -> Vec<String> {
    let trimmed = value.trim();
    let expanded = expand_tilde(trimmed).unwrap_or_else(|| trimmed.to_string());
    let mut suggestions = Vec::new();
    let mut seen = HashSet::new();

    for path in recent_paths.iter().take(MAX_RECENT_PATHS) {
        if path.is_empty() {
            continue;
        }
        if !expanded.is_empty() && !path.starts_with(&expanded) {
            continue;
        }
        if seen.insert(path.clone()) {
            suggestions.push(path.clone());
        }
    }

    if let Some((directory, partial)) = split_completion_path(&expanded) {
        if let Ok(entries) = std::fs::read_dir(&directory) {
            for entry in entries.flatten() {
                let Ok(file_type) = entry.file_type() else {
                    continue;
                };
                if !file_type.is_dir() {
                    continue;
                }
                let name = entry.file_name().to_string_lossy().to_string();
                if !partial.is_empty() && !name.starts_with(&partial) {
                    continue;
                }
                let suggestion_path = if directory == Path::new(".") {
                    name
                } else {
                    directory.join(&name).to_string_lossy().to_string()
                };
                if seen.insert(suggestion_path.clone()) {
                    suggestions.push(suggestion_path);
                }
                if suggestions.len() >= MAX_PATH_SUGGESTIONS {
                    break;
                }
            }
        }
    }

    suggestions
}

fn split_completion_path(value: &str) -> Option<(PathBuf, String)> {
    if value.is_empty() {
        return None;
    }
    let path = Path::new(value);
    if value.ends_with(std::path::MAIN_SEPARATOR) {
        return Some((path.to_path_buf(), String::new()));
    }
    match path.parent() {
        Some(parent) if parent.as_os_str().is_empty() => {
            Some((PathBuf::from("."), value.to_string()))
        }
        Some(parent) => Some((
            parent.to_path_buf(),
            path.file_name()
                .unwrap_or_default()
                .to_string_lossy()
                .to_string(),
        )),
        None => Some((PathBuf::from("."), value.to_string())),
    }
}
