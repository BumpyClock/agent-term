//! Command palette for AgentTerm, built on the gpui-kit `Command` component.

use std::rc::Rc;

use agentterm_search::MessageSource;
use agentterm_session::SessionRecord;
use gpui_kit::component::command::{Command, CommandGroup, CommandItem, CommandState};
use gpui_kit::component::kbd::Kbd;
use gpui_kit::component::{Icon, IconName};
use gpui_kit::{
    div, px, AppContext as _, Context, Entity, EventEmitter, FontWeight, Hsla, IntoElement,
    Keystroke, ParentElement as _, Render, SharedString, Styled as _, Window,
};
use nucleo_matcher::pattern::{CaseMatching, Normalization, Pattern};
use nucleo_matcher::{Config, Matcher, Utf32Str};

use super::search_manager;
use crate::ui::{ActiveTheme as _, WorkspaceItem};
use gpui_kit::component::{h_flex, v_flex};

const PALETTE_MAX_HEIGHT: f32 = 500.0;
const HISTORY_RESULT_LIMIT: usize = 10;
const MINIMUM_HISTORY_QUERY_LENGTH: usize = 2;
const MAX_RESULTS: usize = 50;
const TITLE_SCORE_BOOST: i64 = 1000;
const SUBTITLE_SCORE_BOOST: i64 = 500;
const CATEGORY_SCORE_DIVISOR: i64 = 10;

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
    category: &'static str,
    icon: IconName,
    shortcut: Option<&'static str>,
    payload: CommandPalettePayload,
}

struct RankedRow {
    row: PaletteRow,
    score: i64,
    title_ranges: Vec<(usize, usize)>,
}

/// Scores rows the way the previous palette did: nucleo fuzzy matching with
/// smart case, a title boost, a subtitle boost, and a category bonus.
struct RowMatcher {
    pattern: Pattern,
    matcher: Matcher,
    query_is_empty: bool,
}

impl RowMatcher {
    fn new(query: &str) -> Self {
        Self {
            pattern: Pattern::parse(query, CaseMatching::Smart, Normalization::Smart),
            matcher: Matcher::new(Config::DEFAULT),
            query_is_empty: query.is_empty(),
        }
    }

    fn match_text(&mut self, text: &str) -> Option<(i64, Vec<(usize, usize)>)> {
        if self.query_is_empty {
            return Some((0, Vec::new()));
        }
        let characters: Vec<char> = text.chars().collect();
        let mut indices = Vec::new();
        let score = self.pattern.indices(
            Utf32Str::Unicode(&characters),
            &mut self.matcher,
            &mut indices,
        )?;
        Some((i64::from(score), indices_to_byte_ranges(&mut indices, text)))
    }

    fn rank(&mut self, row: PaletteRow) -> Option<RankedRow> {
        if self.query_is_empty {
            return Some(RankedRow {
                row,
                score: 0,
                title_ranges: Vec::new(),
            });
        }

        let title_match = self.match_text(&row.label);
        let subtitle_match = row
            .subtitle
            .as_ref()
            .and_then(|subtitle| self.match_text(subtitle));
        let category_match = self.match_text(row.category);

        let mut best_score = None;
        let mut title_ranges = Vec::new();
        if let Some((score, ranges)) = title_match {
            best_score = Some(score + TITLE_SCORE_BOOST);
            title_ranges = ranges;
        }
        if let Some((score, _)) = subtitle_match {
            let adjusted = score + SUBTITLE_SCORE_BOOST;
            if best_score.is_none_or(|best| adjusted > best) {
                best_score = Some(adjusted);
                title_ranges = Vec::new();
            }
        }
        if let (Some(best), Some((category_score, _))) = (best_score, category_match) {
            best_score = Some(best + category_score / CATEGORY_SCORE_DIVISOR);
        }

        best_score.map(|score| RankedRow {
            row,
            score,
            title_ranges,
        })
    }
}

/// Nucleo reports character indices in match order. Highlighting needs sorted byte ranges.
fn indices_to_byte_ranges(indices: &mut Vec<u32>, text: &str) -> Vec<(usize, usize)> {
    indices.sort_unstable();
    indices.dedup();
    let byte_offsets: Vec<usize> = text
        .char_indices()
        .map(|(offset, _)| offset)
        .chain(std::iter::once(text.len()))
        .collect();

    let mut ranges: Vec<(usize, usize)> = Vec::new();
    let mut run: Option<(usize, usize)> = None;
    for &index in indices.iter() {
        let index = index as usize;
        run = match run {
            Some((start, end)) if index == end => Some((start, end + 1)),
            Some(finished) => {
                ranges.push(finished);
                Some((index, index + 1))
            }
            None => Some((index, index + 1)),
        };
    }
    ranges.extend(run);

    ranges
        .into_iter()
        .filter_map(|(start, end)| {
            Some((*byte_offsets.get(start)?, *byte_offsets.get(end)?))
        })
        .collect()
}

fn rank_rows(matcher: &mut RowMatcher, rows: Vec<PaletteRow>, sort: bool) -> Vec<RankedRow> {
    let mut ranked: Vec<RankedRow> = rows
        .into_iter()
        .filter_map(|row| matcher.rank(row))
        .collect();
    if sort {
        ranked.sort_by(|left, right| {
            right
                .score
                .cmp(&left.score)
                .then_with(|| left.row.label.as_ref().cmp(right.row.label.as_ref()))
        });
    }
    ranked
}

fn highlighted_label(
    text: SharedString,
    ranges: &[(usize, usize)],
    highlight: Hsla,
) -> gpui_kit::AnyElement {
    if ranges.is_empty() {
        return div().truncate().child(text).into_any_element();
    }

    let mut segments = Vec::new();
    let mut cursor = 0;
    for &(start, end) in ranges {
        if let Some(plain) = text.get(cursor..start).filter(|plain| !plain.is_empty()) {
            segments.push(div().child(plain.to_string()).into_any_element());
        }
        if let Some(matched) = text.get(start..end).filter(|matched| !matched.is_empty()) {
            segments.push(
                div()
                    .text_color(highlight)
                    .font_weight(FontWeight::SEMIBOLD)
                    .child(matched.to_string())
                    .into_any_element(),
            );
        }
        cursor = end;
    }
    if let Some(rest) = text.get(cursor..).filter(|rest| !rest.is_empty()) {
        segments.push(div().child(rest.to_string()).into_any_element());
    }
    h_flex().truncate().children(segments).into_any_element()
}

impl RankedRow {
    fn into_item(self) -> (CommandItem, CommandPalettePayload) {
        let RankedRow {
            row:
                PaletteRow {
                    label,
                    subtitle,
                    category,
                    icon,
                    shortcut,
                    payload,
                },
            title_ranges,
            ..
        } = self;
        let item = CommandItem::new().label(label.clone()).child(move |_window, cx| {
            let muted = cx.theme().muted_foreground;
            let accent = cx.theme().accent;
            h_flex()
                .w_full()
                .gap_2()
                .items_center()
                .child(Icon::new(icon.clone()).text_color(muted))
                .child(
                    v_flex()
                        .flex_1()
                        .min_w_0()
                        .child(highlighted_label(label.clone(), &title_ranges, accent))
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
                .child(div().text_xs().text_color(muted).child(category))
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
        self.query = query.to_string();
        self.history = self.search_history();
        cx.notify();
    }

    fn search_history(&self) -> Vec<HistoryEntry> {
        if self.history_search_inactive() {
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

    fn history_search_inactive(&self) -> bool {
        self.query.len() < MINIMUM_HISTORY_QUERY_LENGTH
            || search_manager::try_global_search_manager().is_none()
            || search_manager::search_indexing_in_progress()
    }

    fn action_rows(&self) -> Vec<PaletteRow> {
        [
            ("new_tab", "New Tab", IconName::Plus, "cmd-t"),
            ("new_window", "New Window", IconName::ExternalLink, "cmd-n"),
            ("settings", "Settings", IconName::Settings, "cmd-,"),
            ("toggle_sidebar", "Toggle Sidebar", IconName::Menu, "cmd-b"),
        ]
        .into_iter()
        .map(|(action_id, label, icon, shortcut)| PaletteRow {
            label: label.into(),
            subtitle: None,
            category: "Actions",
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
                    category: "Workspaces",
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
        self.sessions
            .iter()
            .map(|session| PaletteRow {
                label: session.title.clone().into(),
                subtitle: Some(format!("{:?}", session.tool).into()),
                category: "Sessions",
                icon: IconName::SquareTerminal,
                shortcut: None,
                payload: CommandPalettePayload::Session {
                    id: session.id.clone(),
                },
            })
            .collect()
    }

    fn history_rows(&self) -> Vec<PaletteRow> {
        self.history
            .iter()
            .map(|entry| {
                let (icon, category, payload) = match entry.source {
                    MessageSource::Claude => (
                        IconName::Bot,
                        "Claude History",
                        CommandPalettePayload::ClaudeConversation {
                            session_id: entry.session_id.clone(),
                            workspace: entry.workspace.clone(),
                        },
                    ),
                    MessageSource::Codex => (
                        IconName::File,
                        "Codex History",
                        CommandPalettePayload::CodexConversation {
                            session_id: entry.session_id.clone(),
                            workspace: entry.workspace.clone(),
                        },
                    ),
                };
                PaletteRow {
                    label: entry.workspace.clone().into(),
                    subtitle: Some(entry.snippet.clone().into()),
                    category,
                    icon,
                    shortcut: None,
                    payload,
                }
            })
            .collect()
    }

    /// Returns command rows and session-history rows, each ranked by score then title.
    /// A one-character query shows nothing, and history needs two characters.
    fn ranked_sections(&self) -> (Vec<RankedRow>, Vec<RankedRow>) {
        if self.query.len() == 1 {
            return (Vec::new(), Vec::new());
        }
        let mut matcher = RowMatcher::new(&self.query);
        let sort = !self.query.is_empty();

        let mut command_rows = self.action_rows();
        command_rows.extend(self.workspace_rows());
        let mut commands = rank_rows(&mut matcher, command_rows, sort);

        let mut history = if self.history_search_inactive() {
            Vec::new()
        } else {
            let mut history_rows = self.session_rows();
            history_rows.extend(self.history_rows());
            rank_rows(&mut matcher, history_rows, sort)
        };

        commands.truncate(MAX_RESULTS);
        history.truncate(MAX_RESULTS - commands.len());
        (commands, history)
    }
}

impl Render for CommandPaletteView {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let (commands, history) = self.ranked_sections();
        let commands_label = (!self.query.is_empty()).then_some("Commands");
        let groups = [
            (commands_label, commands),
            (Some("Session History"), history),
        ];

        let mut command = Command::new(&self.state);
        let mut payloads: Vec<Vec<CommandPalettePayload>> = Vec::new();
        for (label, rows) in groups {
            if rows.is_empty() {
                continue;
            }
            let mut group = CommandGroup::new();
            if let Some(label) = label {
                group = group.label(label);
            }
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

#[cfg(test)]
mod tests {
    use super::*;

    fn row(label: &str, subtitle: Option<&str>, category: &'static str) -> PaletteRow {
        PaletteRow {
            label: label.to_string().into(),
            subtitle: subtitle.map(|subtitle| subtitle.to_string().into()),
            category,
            icon: IconName::Plus,
            shortcut: None,
            payload: CommandPalettePayload::Action {
                action_id: label.to_string(),
            },
        }
    }

    #[test]
    fn fuzzy_query_matches_non_contiguous_title_and_highlights_it() {
        let mut matcher = RowMatcher::new("tsb");
        let ranked = rank_rows(
            &mut matcher,
            vec![row("Toggle Sidebar", None, "Actions"), row("New Tab", None, "Actions")],
            true,
        );
        assert_eq!(ranked.len(), 1);
        assert_eq!(ranked[0].row.label.as_ref(), "Toggle Sidebar");
        assert!(!ranked[0].title_ranges.is_empty());
    }

    #[test]
    fn title_match_outranks_subtitle_match_and_category_alone_never_matches() {
        let mut matcher = RowMatcher::new("work");
        let ranked = rank_rows(
            &mut matcher,
            vec![
                row("Alpha", Some("work dir"), "Other"),
                row("Work", None, "Other"),
                row("Beta", None, "Workspaces"),
            ],
            true,
        );
        let labels: Vec<&str> = ranked.iter().map(|ranked| ranked.row.label.as_ref()).collect();
        assert_eq!(labels, ["Work", "Alpha"]);
    }

    #[test]
    fn ranges_use_byte_offsets_for_multibyte_text() {
        let mut indices = vec![2, 1];
        assert_eq!(indices_to_byte_ranges(&mut indices, "éab"), vec![(2, 4)]);
    }
}
