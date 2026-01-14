//! Terminal entity wrapping Alacritty's terminal emulation.
//!
//! This module provides the core Terminal struct that bridges GPUI with Alacritty's
//! terminal emulator. It handles PTY communication, event processing, and provides
//! a clean API for the terminal view to interact with.
//!
//! # Architecture
//!
//! The terminal system consists of:
//! - `ZedListener`: Bridges Alacritty events to GPUI via an unbounded channel
//! - `TerminalBounds`: Manages terminal dimensions (cells, pixels, bounds)
//! - `TerminalContent`: Holds rendered output for display
//! - `Terminal`: Main entity wrapping `Arc<FairMutex<Term<ZedListener>>>`
//! - `TerminalBuilder`: Factory for creating terminals with PTY subscription
//!
//! # Event Processing
//!
//! Events from Alacritty are batched in 4ms windows to reduce UI update overhead.
//! The event loop runs in a GPUI spawn task and processes events asynchronously.

use std::{
    borrow::Cow, cmp, collections::VecDeque, ops::Deref, path::PathBuf, sync::Arc, time::Duration,
};

use alacritty_terminal::{
    Term,
    event::{Event as AlacTermEvent, EventListener, Notify, WindowSize},
    event_loop::{EventLoop, Msg, Notifier},
    grid::{Dimensions, Scroll as AlacScroll},
    index::{Column, Direction as AlacDirection, Line, Point as AlacPoint, Side},
    selection::{Selection, SelectionRange, SelectionType},
    sync::FairMutex,
    term::{Config, RenderableCursor, TermMode, cell::Cell},
    tty,
    vte::ansi::{
        ClearMode, CursorShape as AlacCursorShape, CursorStyle as AlacCursorStyle, Handler,
    },
};
use anyhow::{Context as _, Result};
use futures::{
    FutureExt, StreamExt,
    channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded},
};
use gpui::{
    App, Bounds, ClipboardItem, Context, EventEmitter, Keystroke, MouseButton, MouseDownEvent,
    MouseMoveEvent, MouseUpEvent, Pixels, Point, ScrollWheelEvent, Size, Task, TouchPhase, Window,
    px,
};
use once_cell::sync::Lazy;
use regex::Regex;

use crate::mappings::{
    keys::to_esc_str,
    mouse::{
        alt_scroll, grid_point, grid_point_and_side, mouse_button_report, mouse_moved_report,
        scroll_report,
    },
};

const DEFAULT_SCROLL_HISTORY_LINES: usize = 10_000;
const MAX_SCROLL_HISTORY_LINES: usize = 100_000;
const DEBUG_TERMINAL_WIDTH: Pixels = px(500.);
const DEBUG_TERMINAL_HEIGHT: Pixels = px(30.);
const DEBUG_CELL_WIDTH: Pixels = px(5.);
const DEBUG_LINE_HEIGHT: Pixels = px(5.);

/// URL regex pattern for detecting URLs in terminal output.
/// Matches http://, https://, file://, and common URL patterns.
static URL_REGEX: Lazy<Regex> = Lazy::new(|| {
    Regex::new(
        r"(?x)
        # Protocol-based URLs
        (?:https?://|file://|ftp://|mailto:|git://|ssh://)
        # Domain and path
        [^\s<>\[\]{}|\\^`\x00-\x1f\x7f]+
        |
        # www. URLs without protocol
        www\.[^\s<>\[\]{}|\\^`\x00-\x1f\x7f]+
        |
        # localhost URLs
        localhost(?::\d+)?(?:/[^\s<>\[\]{}|\\^`\x00-\x1f\x7f]*)?
        ",
    )
    .expect("URL regex should be valid")
});

/// A URL detected via regex pattern matching in terminal content.
#[derive(Clone, Debug)]
pub struct DetectedUrl {
    /// The URL string
    pub url: String,
    /// Starting line (viewport-relative)
    pub start_line: i32,
    /// Starting column
    pub start_col: usize,
    /// Ending line (viewport-relative)
    pub end_line: i32,
    /// Ending column (exclusive)
    pub end_col: usize,
}

impl DetectedUrl {
    /// Check if a viewport point is within this URL's range.
    pub fn contains(&self, line: i32, col: usize) -> bool {
        if line < self.start_line || line > self.end_line {
            return false;
        }
        if line == self.start_line && col < self.start_col {
            return false;
        }
        if line == self.end_line && col >= self.end_col {
            return false;
        }
        true
    }
}

/// Events emitted by the Terminal for the view layer to handle.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Event {
    /// Terminal title has changed
    TitleChanged,
    /// Bell character received
    Bell,
    /// Terminal content has changed and needs redraw
    Wakeup,
    /// Cursor blinking state changed
    BlinkChanged(bool),
    /// Selection has changed
    SelectionsChanged,
    /// Terminal process exited
    CloseTerminal,
    /// A hyperlink was clicked and should be opened
    OpenHyperlink(String),
}

impl EventEmitter<Event> for Terminal {}

/// Bridges Alacritty events to GPUI via an unbounded channel.
///
/// Implements Alacritty's `EventListener` trait to receive events from the terminal
/// emulator and forward them to the GPUI event loop for processing.
#[derive(Clone)]
pub struct ZedListener(pub UnboundedSender<AlacTermEvent>);

impl EventListener for ZedListener {
    fn send_event(&self, event: AlacTermEvent) {
        self.0.unbounded_send(event).ok();
    }
}

/// Internal events for terminal state management.
///
/// These events are queued and processed during sync to update terminal state.
#[derive(Clone)]
enum InternalEvent {
    Resize(TerminalBounds),
    Clear,
    Scroll(AlacScroll),
    ScrollToAlacPoint(AlacPoint),
    SetSelection(Option<(Selection, AlacPoint)>),
    UpdateSelection(Point<Pixels>),
    Copy(Option<bool>),
}

/// Terminal dimension management.
///
/// Handles the relationship between pixel bounds, cell dimensions, and grid size.
/// Used for coordinate translation between GPUI and Alacritty.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TerminalBounds {
    pub cell_width: Pixels,
    pub line_height: Pixels,
    pub bounds: Bounds<Pixels>,
}

impl TerminalBounds {
    pub fn new(line_height: Pixels, cell_width: Pixels, bounds: Bounds<Pixels>) -> Self {
        TerminalBounds {
            cell_width,
            line_height,
            bounds,
        }
    }

    pub fn num_lines(&self) -> usize {
        (self.bounds.size.height / self.line_height).floor() as usize
    }

    pub fn num_columns(&self) -> usize {
        (self.bounds.size.width / self.cell_width).floor() as usize
    }

    pub fn height(&self) -> Pixels {
        self.bounds.size.height
    }

    pub fn width(&self) -> Pixels {
        self.bounds.size.width
    }

    pub fn last_column(&self) -> Column {
        Column(self.num_columns().saturating_sub(1))
    }

    pub fn bottommost_line(&self) -> Line {
        Line(self.num_lines().saturating_sub(1) as i32)
    }
}

impl Default for TerminalBounds {
    fn default() -> Self {
        TerminalBounds::new(
            DEBUG_LINE_HEIGHT,
            DEBUG_CELL_WIDTH,
            Bounds {
                origin: Point::default(),
                size: Size {
                    width: DEBUG_TERMINAL_WIDTH,
                    height: DEBUG_TERMINAL_HEIGHT,
                },
            },
        )
    }
}

impl From<TerminalBounds> for WindowSize {
    fn from(val: TerminalBounds) -> Self {
        WindowSize {
            num_lines: val.num_lines() as u16,
            num_cols: val.num_columns() as u16,
            cell_width: f32::from(val.cell_width) as u16,
            cell_height: f32::from(val.line_height) as u16,
        }
    }
}

impl Dimensions for TerminalBounds {
    fn total_lines(&self) -> usize {
        self.screen_lines()
    }

    fn screen_lines(&self) -> usize {
        self.num_lines()
    }

    fn columns(&self) -> usize {
        self.num_columns()
    }
}

/// A single cell with its position in the terminal grid.
#[derive(Clone, Debug)]
pub struct IndexedCell {
    pub point: AlacPoint,
    pub cell: Cell,
}

impl Deref for IndexedCell {
    type Target = Cell;

    fn deref(&self) -> &Cell {
        &self.cell
    }
}

/// Rendered terminal content for display.
///
/// Contains all the information needed to render the terminal:
/// cells, cursor, selection, mode, and scroll state.
#[derive(Clone)]
pub struct TerminalContent {
    pub cells: Vec<IndexedCell>,
    pub mode: TermMode,
    pub display_offset: usize,
    pub selection_text: Option<String>,
    pub selection: Option<SelectionRange>,
    pub cursor: RenderableCursor,
    pub cursor_char: char,
    pub terminal_bounds: TerminalBounds,
    pub scrolled_to_top: bool,
    pub scrolled_to_bottom: bool,
    /// URLs detected via regex pattern matching
    pub detected_urls: Vec<DetectedUrl>,
}

impl Default for TerminalContent {
    fn default() -> Self {
        TerminalContent {
            cells: Vec::new(),
            mode: TermMode::empty(),
            display_offset: 0,
            selection_text: None,
            selection: None,
            cursor: RenderableCursor {
                shape: AlacCursorShape::Block,
                point: AlacPoint::new(Line(0), Column(0)),
            },
            cursor_char: ' ',
            terminal_bounds: TerminalBounds::default(),
            scrolled_to_top: false,
            scrolled_to_bottom: true,
            detected_urls: Vec::new(),
        }
    }
}

#[derive(PartialEq, Eq)]
pub enum SelectionPhase {
    Selecting,
    Ended,
}

/// Factory for creating Terminal instances with PTY subscription.
///
/// Handles the async PTY creation and provides the `subscribe` method
/// to wire up the event loop.
pub struct TerminalBuilder {
    terminal: Terminal,
    events_rx: UnboundedReceiver<AlacTermEvent>,
}

impl TerminalBuilder {
    /// Creates a new terminal with a PTY connection.
    ///
    /// # Arguments
    ///
    /// * `working_directory` - Initial working directory for the shell
    /// * `shell` - Shell program to run (None uses system default)
    /// * `shell_args` - Optional args for the shell/tool command
    /// * `env` - Additional environment variables
    /// * `max_scroll_history_lines` - Maximum scrollback history
    /// * `window_id` - GPUI window identifier
    /// * `cx` - Application context
    pub fn new(
        working_directory: Option<PathBuf>,
        shell: Option<String>,
        shell_args: Option<Vec<String>>,
        env: std::collections::HashMap<String, String>,
        max_scroll_history_lines: Option<usize>,
        window_id: u64,
        cx: &App,
    ) -> Task<Result<TerminalBuilder>> {
        cx.spawn(async move |_| {
            let mut shell_cmd = shell.clone();
            let shell_args: Vec<String> = shell_args.unwrap_or_default();

            if shell_cmd.is_none() {
                shell_cmd = std::env::var("SHELL").ok();
            }

            let alac_shell = shell_cmd.map(|program| tty::Shell::new(program, shell_args));

            let pty_options = tty::Options {
                shell: alac_shell,
                working_directory: working_directory.clone(),
                drain_on_exit: true,
                env: env.into_iter().collect(),
                #[cfg(windows)]
                escape_args: false,
            };

            let scrolling_history = max_scroll_history_lines
                .unwrap_or(DEFAULT_SCROLL_HISTORY_LINES)
                .min(MAX_SCROLL_HISTORY_LINES);

            let config = Config {
                scrolling_history,
                default_cursor_style: AlacCursorStyle {
                    shape: AlacCursorShape::Block,
                    blinking: false,
                },
                ..Config::default()
            };

            let pty = tty::new(&pty_options, TerminalBounds::default().into(), window_id)
                .context("failed to create PTY")?;

            let (events_tx, events_rx) = unbounded();

            let term = Term::new(
                config.clone(),
                &TerminalBounds::default(),
                ZedListener(events_tx.clone()),
            );

            let term = Arc::new(FairMutex::new(term));

            let event_loop = EventLoop::new(
                term.clone(),
                ZedListener(events_tx),
                pty,
                pty_options.drain_on_exit,
                false,
            )
            .context("failed to create event loop")?;

            let pty_tx = event_loop.channel();
            let _io_thread = event_loop.spawn();

            let terminal = Terminal {
                term,
                pty_tx: Some(Notifier(pty_tx)),
                events: VecDeque::with_capacity(10),
                last_content: TerminalContent::default(),
                last_mouse: None,
                selection_head: None,
                breadcrumb_text: String::new(),
                scroll_px: px(0.),
                selection_phase: SelectionPhase::Ended,
                event_loop_task: Task::ready(Ok(())),
            };

            Ok(TerminalBuilder {
                terminal,
                events_rx,
            })
        })
    }

    /// Subscribes to terminal events and returns the configured Terminal.
    ///
    /// This method sets up the event loop that processes Alacritty events
    /// in batched 4ms windows to reduce UI update overhead.
    pub fn subscribe(mut self, cx: &Context<Terminal>) -> Terminal {
        self.terminal.event_loop_task = cx.spawn(async move |terminal, cx| {
            while let Some(event) = self.events_rx.next().await {
                terminal.update(cx, |terminal, cx| {
                    terminal.process_event(event, cx);
                })?;

                'outer: loop {
                    let mut events = Vec::new();

                    let mut timer = cx
                        .background_executor()
                        .timer(Duration::from_millis(4))
                        .fuse();

                    let mut wakeup = false;
                    loop {
                        futures::select_biased! {
                            _ = timer => break,
                            event = self.events_rx.next() => {
                                if let Some(event) = event {
                                    if matches!(event, AlacTermEvent::Wakeup) {
                                        wakeup = true;
                                    } else {
                                        events.push(event);
                                    }

                                    if events.len() > 100 {
                                        break;
                                    }
                                } else {
                                    break;
                                }
                            },
                        }
                    }

                    if events.is_empty() && !wakeup {
                        smol::future::yield_now().await;
                        break 'outer;
                    }

                    terminal.update(cx, |this, cx| {
                        if wakeup {
                            this.process_event(AlacTermEvent::Wakeup, cx);
                        }

                        for event in events {
                            this.process_event(event, cx);
                        }
                    })?;
                    smol::future::yield_now().await;
                }
            }
            anyhow::Ok(())
        });
        self.terminal
    }
}

/// Main terminal entity wrapping Alacritty's Term.
///
/// Provides the interface between GPUI and Alacritty's terminal emulator.
/// Handles input, output, scrolling, selection, and event processing.
pub struct Terminal {
    term: Arc<FairMutex<Term<ZedListener>>>,
    pty_tx: Option<Notifier>,
    events: VecDeque<InternalEvent>,
    last_mouse: Option<(AlacPoint, AlacDirection)>,
    pub last_content: TerminalContent,
    pub selection_head: Option<AlacPoint>,
    pub breadcrumb_text: String,
    scroll_px: Pixels,
    selection_phase: SelectionPhase,
    event_loop_task: Task<Result<(), anyhow::Error>>,
}

impl Terminal {
    pub fn shutdown(&self) {
        if let Some(pty_tx) = &self.pty_tx {
            let _ = pty_tx.0.send(Msg::Shutdown);
        }
    }

    /// Writes bytes to the PTY.
    fn write_to_pty(&self, input: impl Into<Cow<'static, [u8]>>) {
        if let Some(pty_tx) = &self.pty_tx {
            pty_tx.notify(input.into());
        }
    }

    /// Sends input to the terminal, scrolling to bottom and clearing selection.
    pub fn input(&mut self, input: impl Into<Cow<'static, [u8]>>) {
        self.events
            .push_back(InternalEvent::Scroll(AlacScroll::Bottom));
        self.events.push_back(InternalEvent::SetSelection(None));
        self.write_to_pty(input);
    }

    /// Attempts to handle a keystroke, returning true if handled.
    ///
    /// This only handles special keys (arrows, function keys, ctrl combinations, etc.)
    /// Regular character input is handled via InputHandler::replace_text_in_range.
    pub fn try_keystroke(&mut self, keystroke: &Keystroke, option_as_meta: bool) -> bool {
        let esc = to_esc_str(keystroke, &self.last_content.mode, option_as_meta);
        if let Some(esc) = esc {
            match esc {
                Cow::Borrowed(string) => self.input(string.as_bytes()),
                Cow::Owned(string) => self.input(string.into_bytes()),
            };
            true
        } else {
            false
        }
    }

    /// Commits text input directly to the terminal.
    /// Called by InputHandler when the user types regular characters.
    pub fn input_text(&mut self, text: &str) {
        if !text.is_empty() {
            self.input(text.as_bytes().to_vec());
        }
    }

    /// Pastes text into the terminal.
    pub fn paste(&mut self, text: &str) {
        let paste_text = if self.last_content.mode.contains(TermMode::BRACKETED_PASTE) {
            format!("{}{}{}", "\x1b[200~", text.replace('\x1b', ""), "\x1b[201~")
        } else {
            text.replace("\r\n", "\r").replace('\n', "\r")
        };

        self.input(paste_text.into_bytes());
    }

    /// Resizes the terminal to new bounds.
    pub fn set_size(&mut self, new_bounds: TerminalBounds) {
        if self.last_content.terminal_bounds != new_bounds {
            self.events.push_back(InternalEvent::Resize(new_bounds));
        }
    }

    /// Synchronizes terminal state and updates content for rendering.
    pub fn sync(&mut self, _window: &mut Window, cx: &mut Context<Self>) {
        let term = self.term.clone();
        let mut terminal = term.lock_unfair();

        while let Some(event) = self.events.pop_front() {
            self.process_terminal_event(&event, &mut terminal, cx);
        }

        self.last_content = Self::make_content(&terminal, &self.last_content);
    }

    fn make_content(term: &Term<ZedListener>, last_content: &TerminalContent) -> TerminalContent {
        let content = term.renderable_content();

        let estimated_size = content.display_iter.size_hint().0;
        let mut cells = Vec::with_capacity(estimated_size);

        cells.extend(content.display_iter.map(|ic| IndexedCell {
            point: ic.point,
            cell: ic.cell.clone(),
        }));

        let selection_text = if content.selection.is_some() {
            term.selection_to_string()
        } else {
            None
        };

        // Detect URLs in the terminal content using regex
        let detected_urls = Self::detect_urls_in_cells(&cells);

        TerminalContent {
            cells,
            mode: content.mode,
            display_offset: content.display_offset,
            selection_text,
            selection: content.selection,
            cursor: content.cursor,
            cursor_char: term.grid()[content.cursor.point].c,
            terminal_bounds: last_content.terminal_bounds,
            scrolled_to_top: content.display_offset == term.history_size(),
            scrolled_to_bottom: content.display_offset == 0,
            detected_urls,
        }
    }

    /// Detects URLs in the terminal cells using regex pattern matching.
    /// Handles URLs that wrap across multiple lines by joining wrapped lines.
    fn detect_urls_in_cells(cells: &[IndexedCell]) -> Vec<DetectedUrl> {
        use alacritty_terminal::term::cell::Flags;

        let mut detected_urls = Vec::new();

        // Group cells by line
        let mut lines: std::collections::BTreeMap<i32, Vec<(usize, char, Flags)>> =
            std::collections::BTreeMap::new();

        for cell in cells {
            let line = cell.point.line.0;
            let col = cell.point.column.0;
            let c = cell.c;
            let flags = cell.cell.flags;
            lines.entry(line).or_default().push((col, c, flags));
        }

        // Check if a line is wrapped (ends with WRAPLINE flag on last cell)
        let is_wrapped = |chars: &[(usize, char, Flags)]| -> bool {
            if let Some((_, _, flags)) = chars.iter().max_by_key(|(col, _, _)| col) {
                flags.contains(Flags::WRAPLINE)
            } else {
                false
            }
        };

        // Process lines, joining wrapped ones together
        let line_nums: Vec<i32> = lines.keys().copied().collect();
        let mut processed_lines: std::collections::HashSet<i32> = std::collections::HashSet::new();

        for &start_line in &line_nums {
            if processed_lines.contains(&start_line) {
                continue;
            }

            // Collect all wrapped lines starting from this one
            let mut current_line = start_line;
            let mut joined_text = String::new();
            // Maps character index in joined_text to (line, column)
            let mut char_to_pos: Vec<(i32, usize)> = Vec::new();

            loop {
                processed_lines.insert(current_line);

                if let Some(chars) = lines.get(&current_line) {
                    let mut sorted_chars = chars.clone();
                    sorted_chars.sort_by_key(|(col, _, _)| *col);

                    for (col, c, _) in &sorted_chars {
                        char_to_pos.push((current_line, *col));
                        joined_text.push(*c);
                    }

                    // Check if this line wraps to the next
                    if is_wrapped(chars) {
                        // Look for the next line
                        if let Some(&next_line) = line_nums.iter().find(|&&l| l == current_line + 1)
                        {
                            current_line = next_line;
                            continue;
                        }
                    }
                }
                break;
            }

            // Find URLs in the joined text
            for m in URL_REGEX.find_iter(&joined_text) {
                let start_char_idx = m.start();
                let end_char_idx = m.end();

                if start_char_idx >= char_to_pos.len() || end_char_idx > char_to_pos.len() {
                    continue;
                }

                let (start_line_num, start_col) = char_to_pos[start_char_idx];
                let (end_line_num, end_col) =
                    if end_char_idx > 0 && end_char_idx <= char_to_pos.len() {
                        let (l, c) = char_to_pos[end_char_idx - 1];
                        (l, c + 1) // end_col is exclusive
                    } else {
                        char_to_pos[start_char_idx]
                    };

                let mut url = m.as_str().to_string();

                // Add https:// prefix if it starts with www.
                if url.starts_with("www.") {
                    url = format!("https://{}", url);
                }
                // Add http:// prefix for localhost
                if url.starts_with("localhost") {
                    url = format!("http://{}", url);
                }

                detected_urls.push(DetectedUrl {
                    url,
                    start_line: start_line_num,
                    start_col,
                    end_line: end_line_num,
                    end_col,
                });
            }
        }

        detected_urls
    }

    pub fn scroll_line_up(&mut self) {
        self.events
            .push_back(InternalEvent::Scroll(AlacScroll::Delta(1)));
    }

    pub fn scroll_line_down(&mut self) {
        self.events
            .push_back(InternalEvent::Scroll(AlacScroll::Delta(-1)));
    }

    pub fn scroll_page_up(&mut self) {
        self.events
            .push_back(InternalEvent::Scroll(AlacScroll::PageUp));
    }

    pub fn scroll_page_down(&mut self) {
        self.events
            .push_back(InternalEvent::Scroll(AlacScroll::PageDown));
    }

    pub fn scroll_to_top(&mut self) {
        self.events
            .push_back(InternalEvent::Scroll(AlacScroll::Top));
    }

    pub fn scroll_to_bottom(&mut self) {
        self.events
            .push_back(InternalEvent::Scroll(AlacScroll::Bottom));
    }

    /// Selects all text in the terminal.
    pub fn select_all(&mut self) {
        let term = self.term.lock();
        let start = AlacPoint::new(term.topmost_line(), Column(0));
        let end = AlacPoint::new(term.bottommost_line(), term.last_column());
        drop(term);
        self.set_selection(Some((make_selection(&(start..=end)), end)));
    }

    fn set_selection(&mut self, selection: Option<(Selection, AlacPoint)>) {
        self.events
            .push_back(InternalEvent::SetSelection(selection));
    }

    /// Copies selected text to clipboard.
    pub fn copy(&mut self, keep_selection: Option<bool>) {
        self.events.push_back(InternalEvent::Copy(keep_selection));
    }

    /// Clears the terminal screen.
    pub fn clear(&mut self) {
        self.events.push_back(InternalEvent::Clear);
    }

    pub fn selection_started(&self) -> bool {
        self.selection_phase == SelectionPhase::Selecting
    }

    pub fn last_content(&self) -> &TerminalContent {
        &self.last_content
    }

    pub fn mouse_mode(&self, shift: bool) -> bool {
        self.last_content.mode.intersects(TermMode::MOUSE_MODE) && !shift
    }

    /// Returns the hyperlink at the given grid point, if any.
    /// Checks both OSC 8 hyperlinks and regex-detected URLs.
    fn hyperlink_at_point(&self, point: AlacPoint) -> Option<String> {
        // First, check for OSC 8 hyperlinks in the cell
        {
            let term = self.term.lock();
            let grid_line = alacritty_terminal::index::Line(point.line.0);
            let grid_col = alacritty_terminal::index::Column(point.column.0 as usize);
            let grid_point = alacritty_terminal::index::Point::new(grid_line, grid_col);

            if grid_line.0 >= 0 && grid_col.0 < term.columns() {
                let cell = &term.grid()[grid_point];
                if let Some(hyperlink) = cell.hyperlink() {
                    return Some(hyperlink.uri().to_string());
                }
            }
        }

        // Then, check for regex-detected URLs
        let line = point.line.0;
        let col = point.column.0 as usize;
        for detected_url in &self.last_content.detected_urls {
            if detected_url.contains(line, col) {
                return Some(detected_url.url.clone());
            }
        }

        None
    }

    fn mouse_changed(&mut self, point: AlacPoint, side: AlacDirection) -> bool {
        match self.last_mouse {
            Some((old_point, old_side)) => {
                if old_point == point && old_side == side {
                    false
                } else {
                    self.last_mouse = Some((point, side));
                    true
                }
            }
            None => {
                self.last_mouse = Some((point, side));
                true
            }
        }
    }

    pub fn mouse_down(&mut self, e: &MouseDownEvent, cx: &mut Context<Self>) {
        let position = e.position - self.last_content.terminal_bounds.bounds.origin;
        let point = grid_point(
            position,
            self.last_content.terminal_bounds,
            self.last_content.display_offset,
        );

        // Handle Cmd+click (macOS) or Ctrl+click (Linux/Windows) to open hyperlinks
        if e.button == MouseButton::Left
            && e.click_count == 1
            && (e.modifiers.platform || e.modifiers.control)
        {
            if let Some(url) = self.hyperlink_at_point(point) {
                cx.emit(Event::OpenHyperlink(url));
                return;
            }
        }

        if self.mouse_mode(e.modifiers.shift) {
            if let Some(bytes) =
                mouse_button_report(point, e.button, e.modifiers, true, self.last_content.mode)
            {
                self.write_to_pty(bytes);
            }
        } else {
            match e.button {
                MouseButton::Left => {
                    let (point, side) = grid_point_and_side(
                        position,
                        self.last_content.terminal_bounds,
                        self.last_content.display_offset,
                    );

                    let selection_type = match e.click_count {
                        0 => return,
                        1 => Some(SelectionType::Simple),
                        2 => Some(SelectionType::Semantic),
                        3 => Some(SelectionType::Lines),
                        _ => None,
                    };

                    if selection_type == Some(SelectionType::Simple) && e.modifiers.shift {
                        self.events
                            .push_back(InternalEvent::UpdateSelection(position));
                        return;
                    }

                    let selection = selection_type
                        .map(|selection_type| Selection::new(selection_type, point, side));

                    if let Some(sel) = selection {
                        self.events
                            .push_back(InternalEvent::SetSelection(Some((sel, point))));
                    }
                }
                _ => {}
            }
        }
    }

    pub fn mouse_up(&mut self, e: &MouseUpEvent, _cx: &Context<Self>) {
        let position = e.position - self.last_content.terminal_bounds.bounds.origin;

        if self.mouse_mode(e.modifiers.shift) {
            let point = grid_point(
                position,
                self.last_content.terminal_bounds,
                self.last_content.display_offset,
            );

            if let Some(bytes) =
                mouse_button_report(point, e.button, e.modifiers, false, self.last_content.mode)
            {
                self.write_to_pty(bytes);
            }
        }

        self.selection_phase = SelectionPhase::Ended;
        self.last_mouse = None;
    }

    pub fn mouse_move(&mut self, e: &MouseMoveEvent, cx: &mut Context<Self>) {
        let position = e.position - self.last_content.terminal_bounds.bounds.origin;

        if self.mouse_mode(e.modifiers.shift) {
            let (point, side) = grid_point_and_side(
                position,
                self.last_content.terminal_bounds,
                self.last_content.display_offset,
            );

            if self.mouse_changed(point, side) {
                if let Some(bytes) =
                    mouse_moved_report(point, e.pressed_button, e.modifiers, self.last_content.mode)
                {
                    self.write_to_pty(bytes);
                }
            }
        }
        cx.notify();
    }

    pub fn mouse_drag(
        &mut self,
        e: &MouseMoveEvent,
        region: Bounds<Pixels>,
        cx: &mut Context<Self>,
    ) {
        let position = e.position - self.last_content.terminal_bounds.bounds.origin;

        if !self.mouse_mode(e.modifiers.shift) {
            self.selection_phase = SelectionPhase::Selecting;
            self.events
                .push_back(InternalEvent::UpdateSelection(position));

            if !self.last_content.mode.contains(TermMode::ALT_SCREEN) {
                if let Some(scroll_lines) = self.drag_line_delta(e, region) {
                    self.events
                        .push_back(InternalEvent::Scroll(AlacScroll::Delta(scroll_lines)));
                }
            }

            cx.notify();
        }
    }

    fn drag_line_delta(&self, e: &MouseMoveEvent, region: Bounds<Pixels>) -> Option<i32> {
        let top = region.origin.y;
        let bottom = region.bottom_left().y;

        let scroll_lines = if e.position.y < top {
            let scroll_delta = (top - e.position.y).pow(1.1);
            (scroll_delta / self.last_content.terminal_bounds.line_height).ceil() as i32
        } else if e.position.y > bottom {
            let scroll_delta = -((e.position.y - bottom).pow(1.1));
            (scroll_delta / self.last_content.terminal_bounds.line_height).floor() as i32
        } else {
            return None;
        };

        Some(scroll_lines.clamp(-3, 3))
    }

    /// Handles scroll wheel events.
    pub fn scroll_wheel(&mut self, e: &ScrollWheelEvent, scroll_multiplier: f32) {
        let mouse_mode = self.mouse_mode(e.shift);
        let scroll_multiplier = if mouse_mode { 1. } else { scroll_multiplier };

        if let Some(scroll_lines) = self.determine_scroll_lines(e, scroll_multiplier) {
            if mouse_mode {
                let point = grid_point(
                    e.position - self.last_content.terminal_bounds.bounds.origin,
                    self.last_content.terminal_bounds,
                    self.last_content.display_offset,
                );

                if let Some(scrolls) = scroll_report(point, scroll_lines, e, self.last_content.mode)
                {
                    for scroll in scrolls {
                        self.write_to_pty(scroll);
                    }
                }
            } else if self
                .last_content
                .mode
                .contains(TermMode::ALT_SCREEN | TermMode::ALTERNATE_SCROLL)
                && !e.shift
            {
                self.write_to_pty(alt_scroll(scroll_lines));
            } else if scroll_lines != 0 {
                self.events
                    .push_back(InternalEvent::Scroll(AlacScroll::Delta(scroll_lines)));
            }
        }
    }

    fn determine_scroll_lines(
        &mut self,
        e: &ScrollWheelEvent,
        scroll_multiplier: f32,
    ) -> Option<i32> {
        let line_height = self.last_content.terminal_bounds.line_height;
        match e.touch_phase {
            TouchPhase::Started => {
                self.scroll_px = px(0.);
                None
            }
            TouchPhase::Moved => {
                let old_offset = (self.scroll_px / line_height) as i32;
                self.scroll_px += e.delta.pixel_delta(line_height).y * scroll_multiplier;
                let new_offset = (self.scroll_px / line_height) as i32;
                self.scroll_px %= self.last_content.terminal_bounds.height();
                Some(new_offset - old_offset)
            }
            TouchPhase::Ended => None,
        }
    }

    pub fn focus_in(&self) {
        if self.last_content.mode.contains(TermMode::FOCUS_IN_OUT) {
            self.write_to_pty("\x1b[I".as_bytes());
        }
    }

    pub fn focus_out(&mut self) {
        if self.last_content.mode.contains(TermMode::FOCUS_IN_OUT) {
            self.write_to_pty("\x1b[O".as_bytes());
        }
    }

    fn process_event(&mut self, event: AlacTermEvent, cx: &mut Context<Self>) {
        match event {
            AlacTermEvent::Title(title) => {
                self.breadcrumb_text = title;
                cx.emit(Event::TitleChanged);
            }
            AlacTermEvent::ResetTitle => {
                self.breadcrumb_text = String::new();
                cx.emit(Event::TitleChanged);
            }
            AlacTermEvent::ClipboardStore(_, data) => {
                cx.write_to_clipboard(ClipboardItem::new_string(data));
            }
            AlacTermEvent::ClipboardLoad(_, format) => {
                self.write_to_pty(
                    match &cx.read_from_clipboard().and_then(|item| item.text()) {
                        Some(text) => format(text),
                        _ => format(""),
                    }
                    .into_bytes(),
                );
            }
            AlacTermEvent::PtyWrite(out) => {
                self.write_to_pty(out.into_bytes());
            }
            AlacTermEvent::TextAreaSizeRequest(format) => {
                self.write_to_pty(format(self.last_content.terminal_bounds.into()).into_bytes());
            }
            AlacTermEvent::CursorBlinkingChange => {
                let terminal = self.term.lock();
                let blinking = terminal.cursor_style().blinking;
                cx.emit(Event::BlinkChanged(blinking));
            }
            AlacTermEvent::Bell => {
                cx.emit(Event::Bell);
            }
            AlacTermEvent::Exit | AlacTermEvent::ChildExit(_) => {
                cx.emit(Event::CloseTerminal);
            }
            AlacTermEvent::Wakeup => {
                cx.emit(Event::Wakeup);
            }
            AlacTermEvent::MouseCursorDirty => {}
            AlacTermEvent::ColorRequest(index, format) => {
                let color = self.term.lock().colors()[index]
                    .unwrap_or(alacritty_terminal::vte::ansi::Rgb { r: 0, g: 0, b: 0 });
                self.write_to_pty(format(color).into_bytes());
            }
        }
    }

    fn process_terminal_event(
        &mut self,
        event: &InternalEvent,
        term: &mut Term<ZedListener>,
        cx: &mut Context<Self>,
    ) {
        match event {
            InternalEvent::Resize(new_bounds) => {
                let mut new_bounds = *new_bounds;
                new_bounds.bounds.size.height =
                    cmp::max(new_bounds.line_height, new_bounds.height());
                new_bounds.bounds.size.width = cmp::max(new_bounds.cell_width, new_bounds.width());

                self.last_content.terminal_bounds = new_bounds;

                if let Some(pty_tx) = &self.pty_tx {
                    pty_tx.0.send(Msg::Resize(new_bounds.into())).ok();
                }

                term.resize(new_bounds);
            }
            InternalEvent::Clear => {
                term.clear_screen(ClearMode::Saved);

                let cursor = term.grid().cursor.point;
                term.grid_mut().reset_region(..cursor.line);

                let line = term.grid()[cursor.line][..Column(term.grid().columns())]
                    .iter()
                    .cloned()
                    .enumerate()
                    .collect::<Vec<(usize, Cell)>>();

                for (i, cell) in line {
                    term.grid_mut()[Line(0)][Column(i)] = cell;
                }

                term.grid_mut().cursor.point =
                    AlacPoint::new(Line(0), term.grid_mut().cursor.point.column);
                let new_cursor = term.grid().cursor.point;

                if (new_cursor.line.0 as usize) < term.screen_lines() - 1 {
                    term.grid_mut().reset_region((new_cursor.line + 1)..);
                }

                cx.emit(Event::Wakeup);
            }
            InternalEvent::Scroll(scroll) => {
                term.scroll_display(*scroll);
            }
            InternalEvent::ScrollToAlacPoint(point) => {
                term.scroll_to_point(*point);
            }
            InternalEvent::SetSelection(selection) => {
                term.selection = selection.as_ref().map(|(sel, _)| sel.clone());

                if let Some((_, head)) = selection {
                    self.selection_head = Some(*head);
                }
                cx.emit(Event::SelectionsChanged);
            }
            InternalEvent::UpdateSelection(position) => {
                if let Some(mut selection) = term.selection.take() {
                    let (point, side) = grid_point_and_side(
                        *position,
                        self.last_content.terminal_bounds,
                        term.grid().display_offset(),
                    );

                    selection.update(point, side);
                    term.selection = Some(selection);

                    self.selection_head = Some(point);
                    cx.emit(Event::SelectionsChanged);
                }
            }
            InternalEvent::Copy(keep_selection) => {
                if let Some(txt) = term.selection_to_string() {
                    cx.write_to_clipboard(ClipboardItem::new_string(txt));
                    if !keep_selection.unwrap_or(false) {
                        self.events.push_back(InternalEvent::SetSelection(None));
                    }
                }
            }
        }
    }
}

impl Drop for Terminal {
    fn drop(&mut self) {
        self.shutdown();
    }
}

fn make_selection(range: &std::ops::RangeInclusive<AlacPoint>) -> Selection {
    let mut selection = Selection::new(SelectionType::Simple, *range.start(), Side::Left);
    selection.update(*range.end(), Side::Right);
    selection
}
