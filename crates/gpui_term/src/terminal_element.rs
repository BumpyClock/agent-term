//! Terminal element for rendering terminal content in GPUI.
//!
//! This module provides the `TerminalElement` struct that implements GPUI's `Element` trait
//! to render terminal content. It handles:
//! - Batching adjacent cells with the same style for efficient rendering
//! - Color conversion from Alacritty's color format to GPUI's Hsla
//! - Cell flag handling (bold, italic, underline, strikethrough, dim)
//! - Cursor rendering with support for block, bar, and underline shapes
//!
//! # Architecture
//!
//! The rendering pipeline consists of three phases:
//! 1. `request_layout` - Returns layout ID with the requested size
//! 2. `prepaint` - Computes layout state: batched text runs, background rects, cursor
//! 3. `paint` - Renders backgrounds, text runs, and cursor
//!
//! # Example
//!
//! ```ignore
//! let element = TerminalElement::new(terminal_entity, focus_handle);
//! ```

use std::mem;

use alacritty_terminal::{
    index::Point as AlacPoint,
    selection::SelectionRange,
    term::{TermMode, cell::Flags},
    vte::ansi::{Color as AnsiColor, CursorShape as AlacCursorShape, NamedColor},
};
use gpui::{
    AbsoluteLength, App, Bounds, ContentMask, Element, ElementId, Entity, FocusHandle, Font,
    FontStyle, FontWeight, GlobalElementId, Hitbox, Hsla, InputHandler, IntoElement, LayoutId,
    Pixels, Point, Rgba, ShapedLine, StrikethroughStyle, TextRun, UTF16Selection, UnderlineStyle,
    Window, fill, point, px, size,
};
use itertools::Itertools;

use crate::{
    IndexedCell, Terminal, TerminalBounds, TerminalContent, terminal_palette::terminal_palette,
};

/// Layout state computed during prepaint, used for painting.
pub struct LayoutState {
    hitbox: Hitbox,
    batched_text_runs: Vec<BatchedTextRun>,
    background_rects: Vec<LayoutRect>,
    cursor: Option<CursorLayout>,
    background_color: Hsla,
    dimensions: TerminalBounds,
    mode: TermMode,
}

/// Helper for converting Alacritty cursor points to display coordinates.
struct DisplayCursor {
    line: i32,
    col: usize,
}

impl DisplayCursor {
    fn from(cursor_point: AlacPoint, display_offset: usize) -> Self {
        Self {
            line: cursor_point.line.0 + display_offset as i32,
            col: cursor_point.column.0,
        }
    }

    fn line(&self) -> i32 {
        self.line
    }

    fn col(&self) -> usize {
        self.col
    }
}

/// A batched text run combining adjacent cells with identical styles.
///
/// Batching reduces draw calls by combining multiple characters that share
/// the same font, color, and decoration properties into a single text shape.
#[derive(Debug)]
pub struct BatchedTextRun {
    pub start_point: AlacPoint<i32, i32>,
    pub text: String,
    pub cell_count: usize,
    pub style: TextRun,
    pub font_size: AbsoluteLength,
}

impl BatchedTextRun {
    fn new_from_char(
        start_point: AlacPoint<i32, i32>,
        c: char,
        style: TextRun,
        font_size: AbsoluteLength,
    ) -> Self {
        let mut text = String::with_capacity(100);
        text.push(c);
        BatchedTextRun {
            start_point,
            text,
            cell_count: 1,
            style,
            font_size,
        }
    }

    fn can_append(&self, other_style: &TextRun) -> bool {
        self.style.font == other_style.font
            && self.style.color == other_style.color
            && self.style.background_color == other_style.background_color
            && self.style.underline == other_style.underline
            && self.style.strikethrough == other_style.strikethrough
    }

    fn append_char(&mut self, c: char) {
        self.append_char_internal(c, true);
    }

    fn append_zero_width_chars(&mut self, chars: &[char]) {
        for &c in chars {
            self.append_char_internal(c, false);
        }
    }

    fn append_char_internal(&mut self, c: char, counts_cell: bool) {
        self.text.push(c);
        if counts_cell {
            self.cell_count += 1;
        }
        self.style.len += c.len_utf8();
    }

    fn paint(
        &self,
        origin: Point<Pixels>,
        dimensions: &TerminalBounds,
        window: &mut Window,
        cx: &mut App,
    ) {
        let pos = Point::new(
            origin.x + self.start_point.column as f32 * dimensions.cell_width,
            origin.y + self.start_point.line as f32 * dimensions.line_height,
        );

        let _ = window
            .text_system()
            .shape_line(
                self.text.clone().into(),
                self.font_size.to_pixels(window.rem_size()),
                std::slice::from_ref(&self.style),
                Some(dimensions.cell_width),
            )
            .paint(
                pos,
                dimensions.line_height,
                gpui::TextAlign::Left,
                None,
                window,
                cx,
            );
    }
}

/// A background rectangle for cells with non-default background colors.
#[derive(Clone, Debug, Default)]
pub struct LayoutRect {
    point: AlacPoint<i32, i32>,
    num_of_cells: usize,
    color: Hsla,
}

impl LayoutRect {
    fn new(point: AlacPoint<i32, i32>, num_of_cells: usize, color: Hsla) -> LayoutRect {
        LayoutRect {
            point,
            num_of_cells,
            color,
        }
    }

    fn paint(&self, origin: Point<Pixels>, dimensions: &TerminalBounds, window: &mut Window) {
        let position = {
            let alac_point = self.point;
            point(
                (origin.x + alac_point.column as f32 * dimensions.cell_width).floor(),
                origin.y + alac_point.line as f32 * dimensions.line_height,
            )
        };
        let rect_size = point(
            (dimensions.cell_width * self.num_of_cells as f32).ceil(),
            dimensions.line_height,
        )
        .into();

        window.paint_quad(fill(Bounds::new(position, rect_size), self.color));
    }
}

/// Cursor rendering information.
pub struct CursorLayout {
    origin: Point<Pixels>,
    block_width: Pixels,
    line_height: Pixels,
    color: Hsla,
    shape: CursorShape,
    text: Option<ShapedLine>,
}

/// Supported cursor shapes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CursorShape {
    Block,
    Hollow,
    Bar,
    Underline,
}

impl CursorLayout {
    fn new(
        origin: Point<Pixels>,
        block_width: Pixels,
        line_height: Pixels,
        color: Hsla,
        shape: CursorShape,
        text: Option<ShapedLine>,
    ) -> Self {
        CursorLayout {
            origin,
            block_width,
            line_height,
            color,
            shape,
            text,
        }
    }

    fn bounds(&self, origin: Point<Pixels>) -> Bounds<Pixels> {
        Bounds::new(
            origin + self.origin,
            size(self.block_width, self.line_height),
        )
    }

    fn paint(&self, origin: Point<Pixels>, window: &mut Window, cx: &mut App) {
        let bounds = self.bounds(origin);

        match self.shape {
            CursorShape::Block => {
                window.paint_quad(fill(bounds, self.color));
                if let Some(text) = &self.text {
                    let _ = text.paint(
                        bounds.origin,
                        self.line_height,
                        gpui::TextAlign::Left,
                        None,
                        window,
                        cx,
                    );
                }
            }
            CursorShape::Hollow => {
                window.paint_quad(gpui::outline(bounds, self.color, gpui::BorderStyle::Solid));
            }
            CursorShape::Bar => {
                let bar_bounds = Bounds::new(bounds.origin, size(px(2.0), bounds.size.height));
                window.paint_quad(fill(bar_bounds, self.color));
            }
            CursorShape::Underline => {
                let underline_bounds = Bounds::new(
                    point(
                        bounds.origin.x,
                        bounds.origin.y + bounds.size.height - px(2.0),
                    ),
                    size(bounds.size.width, px(2.0)),
                );
                window.paint_quad(fill(underline_bounds, self.color));
            }
        }
    }
}

/// Rectangular region for background merging optimization.
#[derive(Debug, Clone)]
struct BackgroundRegion {
    start_line: i32,
    start_col: i32,
    end_line: i32,
    end_col: i32,
    color: Hsla,
}

impl BackgroundRegion {
    fn new(line: i32, col: i32, color: Hsla) -> Self {
        BackgroundRegion {
            start_line: line,
            start_col: col,
            end_line: line,
            end_col: col,
            color,
        }
    }

    fn can_merge_with(&self, other: &BackgroundRegion) -> bool {
        if self.color != other.color {
            return false;
        }

        if self.start_line == other.start_line && self.end_line == other.end_line {
            return self.end_col + 1 == other.start_col || other.end_col + 1 == self.start_col;
        }

        if self.start_col == other.start_col && self.end_col == other.end_col {
            return self.end_line + 1 == other.start_line || other.end_line + 1 == self.start_line;
        }

        false
    }

    fn merge_with(&mut self, other: &BackgroundRegion) {
        self.start_line = self.start_line.min(other.start_line);
        self.start_col = self.start_col.min(other.start_col);
        self.end_line = self.end_line.max(other.end_line);
        self.end_col = self.end_col.max(other.end_col);
    }
}

fn merge_background_regions(regions: Vec<BackgroundRegion>) -> Vec<BackgroundRegion> {
    if regions.is_empty() {
        return regions;
    }

    let mut merged = regions;
    let mut changed = true;

    while changed {
        changed = false;
        let mut i = 0;

        while i < merged.len() {
            let mut j = i + 1;
            while j < merged.len() {
                if merged[i].can_merge_with(&merged[j]) {
                    let other = merged.remove(j);
                    merged[i].merge_with(&other);
                    changed = true;
                } else {
                    j += 1;
                }
            }
            i += 1;
        }
    }

    merged
}

/// GPUI element for rendering terminal content.
///
/// Implements the three-phase rendering pipeline:
/// 1. `request_layout` - Requests space from the layout system
/// 2. `prepaint` - Computes batched text runs, background rects, and cursor
/// 3. `paint` - Renders all computed elements
pub struct TerminalElement {
    terminal: Entity<Terminal>,
    focus: FocusHandle,
    focused: bool,
    cursor_visible: bool,
    font_family: String,
    font_size: f32,
}

impl TerminalElement {
    /// Creates a new terminal element with default font settings.
    ///
    /// # Arguments
    ///
    /// * `terminal` - The terminal entity to render
    /// * `focus` - Focus handle for keyboard input
    /// * `focused` - Whether the terminal currently has focus
    /// * `cursor_visible` - Whether the cursor should be visible (for blinking)
    pub fn new(
        terminal: Entity<Terminal>,
        focus: FocusHandle,
        focused: bool,
        cursor_visible: bool,
    ) -> Self {
        Self::with_settings(
            terminal,
            focus,
            focused,
            cursor_visible,
            "JetBrains Mono",
            14.0,
        )
    }

    /// Creates a new terminal element with custom font settings.
    ///
    /// # Arguments
    ///
    /// * `terminal` - The terminal entity to render
    /// * `focus` - Focus handle for keyboard input
    /// * `focused` - Whether the terminal currently has focus
    /// * `cursor_visible` - Whether the cursor should be visible (for blinking)
    /// * `font_family` - The font family name to use for rendering
    /// * `font_size` - The font size in pixels
    pub fn with_settings(
        terminal: Entity<Terminal>,
        focus: FocusHandle,
        focused: bool,
        cursor_visible: bool,
        font_family: impl Into<String>,
        font_size: f32,
    ) -> Self {
        TerminalElement {
            terminal,
            focus,
            focused,
            cursor_visible,
            font_family: font_family.into(),
            font_size,
        }
    }

    /// Lays out the grid of cells, producing batched text runs and background rects.
    ///
    /// # Arguments
    /// * `grid` - Iterator over indexed cells to render
    /// * `text_style` - Text styling configuration
    /// * `selection` - Optional selection range for highlighting selected text
    /// * `display_offset` - Current scroll offset for selection coordinate conversion
    fn layout_grid(
        grid: impl Iterator<Item = IndexedCell>,
        text_style: &TextStyle,
        selection: Option<&SelectionRange>,
        display_offset: usize,
    ) -> (Vec<LayoutRect>, Vec<BatchedTextRun>) {
        let estimated_cells = grid.size_hint().0;
        let estimated_runs = estimated_cells / 10;
        let estimated_regions = estimated_cells / 20;

        let mut batched_runs = Vec::with_capacity(estimated_runs);
        let mut background_regions: Vec<BackgroundRegion> = Vec::with_capacity(estimated_regions);
        let mut current_batch: Option<BatchedTextRun> = None;

        let linegroups = grid.into_iter().chunk_by(|i| i.point.line);
        for (line_index, (_, line)) in linegroups.into_iter().enumerate() {
            let alac_line = line_index as i32;

            if let Some(batch) = current_batch.take() {
                batched_runs.push(batch);
            }

            let mut previous_cell_had_extras = false;

            for cell in line {
                let mut fg = cell.fg;
                let mut bg = cell.bg;
                if cell.flags.contains(Flags::INVERSE) {
                    mem::swap(&mut fg, &mut bg);
                }

                // Check if this cell is selected and swap colors for selection highlighting.
                // The cell.point uses viewport-relative coordinates, but SelectionRange uses
                // grid-absolute coordinates, so we need to convert.
                let is_selected = selection.is_some_and(|sel| {
                    // Convert viewport line to grid line by subtracting display_offset
                    let grid_line =
                        alacritty_terminal::index::Line(cell.point.line.0 - display_offset as i32);
                    let grid_point = alacritty_terminal::index::Point::new(
                        grid_line,
                        alacritty_terminal::index::Column(cell.point.column.0),
                    );
                    sel.contains(grid_point)
                });

                if is_selected {
                    mem::swap(&mut fg, &mut bg);
                }

                // Paint background if it's not transparent, OR if cell is selected
                // (selected cells need a visible highlight even if originally transparent)
                let needs_background =
                    is_selected || !matches!(bg, AnsiColor::Named(NamedColor::Background));

                if needs_background {
                    // For selected cells with transparent bg, use the foreground color as bg
                    let color =
                        if is_selected && matches!(bg, AnsiColor::Named(NamedColor::Background)) {
                            convert_color(&fg)
                        } else {
                            convert_color(&bg)
                        };
                    let col = cell.point.column.0 as i32;

                    if let Some(last_region) = background_regions.last_mut() {
                        if last_region.color == color
                            && last_region.start_line == alac_line
                            && last_region.end_line == alac_line
                            && last_region.end_col + 1 == col
                        {
                            last_region.end_col = col;
                        } else {
                            background_regions.push(BackgroundRegion::new(alac_line, col, color));
                        }
                    } else {
                        background_regions.push(BackgroundRegion::new(alac_line, col, color));
                    }
                }

                if cell.flags.contains(Flags::WIDE_CHAR_SPACER) {
                    continue;
                }

                if cell.c == ' ' && previous_cell_had_extras {
                    previous_cell_had_extras = false;
                    continue;
                }
                previous_cell_had_extras =
                    matches!(cell.zerowidth(), Some(chars) if !chars.is_empty());

                if !is_blank(&cell) {
                    let has_hyperlink = cell.hyperlink().is_some();
                    let cell_style = Self::cell_style(&cell, fg, text_style, has_hyperlink);
                    let cell_point = AlacPoint::new(alac_line, cell.point.column.0 as i32);
                    let zero_width_chars = cell.zerowidth();

                    if let Some(ref mut batch) = current_batch {
                        if batch.can_append(&cell_style)
                            && batch.start_point.line == cell_point.line
                            && batch.start_point.column + batch.cell_count as i32
                                == cell_point.column
                        {
                            batch.append_char(cell.c);
                            if let Some(chars) = zero_width_chars {
                                batch.append_zero_width_chars(chars);
                            }
                        } else {
                            let old_batch = current_batch.take().unwrap();
                            batched_runs.push(old_batch);
                            let mut new_batch = BatchedTextRun::new_from_char(
                                cell_point,
                                cell.c,
                                cell_style,
                                text_style.font_size,
                            );
                            if let Some(chars) = zero_width_chars {
                                new_batch.append_zero_width_chars(chars);
                            }
                            current_batch = Some(new_batch);
                        }
                    } else {
                        let mut new_batch = BatchedTextRun::new_from_char(
                            cell_point,
                            cell.c,
                            cell_style,
                            text_style.font_size,
                        );
                        if let Some(chars) = zero_width_chars {
                            new_batch.append_zero_width_chars(chars);
                        }
                        current_batch = Some(new_batch);
                    }
                }
            }
        }

        if let Some(batch) = current_batch {
            batched_runs.push(batch);
        }

        let merged_regions = merge_background_regions(background_regions);
        let mut rects = Vec::with_capacity(merged_regions.len() * 2);

        for region in merged_regions {
            for line in region.start_line..=region.end_line {
                rects.push(LayoutRect::new(
                    AlacPoint::new(line, region.start_col),
                    (region.end_col - region.start_col + 1) as usize,
                    region.color,
                ));
            }
        }

        (rects, batched_runs)
    }

    /// Computes cursor position and dimensions.
    fn shape_cursor(
        cursor_point: DisplayCursor,
        size: TerminalBounds,
        text_fragment: &ShapedLine,
    ) -> Option<(Point<Pixels>, Pixels)> {
        if cursor_point.line() < size.num_lines() as i32 {
            let cursor_width = if text_fragment.width == Pixels::ZERO {
                size.cell_width
            } else {
                text_fragment.width
            };

            Some((
                point(
                    (cursor_point.col() as f32 * size.cell_width).floor(),
                    (cursor_point.line() as f32 * size.line_height).floor(),
                ),
                cursor_width.ceil(),
            ))
        } else {
            None
        }
    }

    /// Converts Alacritty cell styles to a GPUI TextRun.
    fn cell_style(
        indexed: &IndexedCell,
        fg: AnsiColor,
        text_style: &TextStyle,
        has_hyperlink: bool,
    ) -> TextRun {
        let flags = indexed.cell.flags;
        let mut fg_color = convert_color(&fg);

        if flags.intersects(Flags::DIM) {
            fg_color.a *= 0.7;
        }

        // Apply underline for cells with underline flags OR hyperlinks
        let underline = if flags.intersects(Flags::ALL_UNDERLINES) {
            Some(UnderlineStyle {
                color: Some(fg_color),
                thickness: Pixels::from(1.0),
                wavy: flags.contains(Flags::UNDERCURL),
            })
        } else if has_hyperlink {
            // Hyperlinks get a subtle underline
            Some(UnderlineStyle {
                color: Some(fg_color),
                thickness: Pixels::from(1.0),
                wavy: false,
            })
        } else {
            None
        };

        let strikethrough = flags
            .intersects(Flags::STRIKEOUT)
            .then(|| StrikethroughStyle {
                color: Some(fg_color),
                thickness: Pixels::from(1.0),
            });

        let weight = if flags.intersects(Flags::BOLD) {
            FontWeight::BOLD
        } else {
            text_style.font_weight
        };

        let style = if flags.intersects(Flags::ITALIC) {
            FontStyle::Italic
        } else {
            FontStyle::Normal
        };

        TextRun {
            len: indexed.c.len_utf8(),
            color: fg_color,
            background_color: None,
            font: Font {
                weight,
                style,
                ..text_style.font.clone()
            },
            underline,
            strikethrough,
        }
    }
}

/// Text styling configuration for terminal rendering.
#[derive(Clone)]
pub struct TextStyle {
    pub font: Font,
    pub font_size: AbsoluteLength,
    pub font_weight: FontWeight,
    pub foreground: Hsla,
    pub background: Hsla,
}

impl Default for TextStyle {
    fn default() -> Self {
        TextStyle {
            font: Font {
                family: "FiraCode Nerd Font".into(),
                features: gpui::FontFeatures::default(),
                fallbacks: None,
                weight: FontWeight::NORMAL,
                style: FontStyle::Normal,
            },
            font_size: AbsoluteLength::Pixels(px(14.0)),
            font_weight: FontWeight::NORMAL,
            foreground: Hsla::white(),
            // Semi-transparent background for blur effect support
            background: Hsla {
                h: 0.0,
                s: 0.0,
                l: 0.0,
                a: 0.0, // Fully transparent - let parent handle background
            },
        }
    }
}

impl TextStyle {
    /// Creates a TextStyle with custom font settings.
    ///
    /// # Arguments
    ///
    /// * `font_family` - The font family name
    /// * `font_size` - The font size in pixels
    pub fn with_font(font_family: &str, font_size: f32) -> Self {
        TextStyle {
            font: Font {
                family: font_family.to_string().into(),
                features: gpui::FontFeatures::default(),
                fallbacks: None,
                weight: FontWeight::NORMAL,
                style: FontStyle::Normal,
            },
            font_size: AbsoluteLength::Pixels(px(font_size)),
            font_weight: FontWeight::NORMAL,
            foreground: Hsla::white(),
            background: Hsla {
                h: 0.0,
                s: 0.0,
                l: 0.0,
                a: 0.0,
            },
        }
    }
}

impl Element for TerminalElement {
    type RequestLayoutState = ();
    type PrepaintState = LayoutState;

    fn id(&self) -> Option<ElementId> {
        Some("terminal-element".into())
    }

    fn source_location(&self) -> Option<&'static core::panic::Location<'static>> {
        None
    }

    fn request_layout(
        &mut self,
        _global_id: Option<&GlobalElementId>,
        _inspector_id: Option<&gpui::InspectorElementId>,
        window: &mut Window,
        cx: &mut App,
    ) -> (LayoutId, Self::RequestLayoutState) {
        let mut style = gpui::Style::default();
        style.size.width = gpui::Length::Definite(gpui::DefiniteLength::Fraction(1.0));
        style.size.height = gpui::Length::Definite(gpui::DefiniteLength::Fraction(1.0));
        style.flex_grow = 1.0;

        let layout_id = window.request_layout(style, None, cx);
        (layout_id, ())
    }

    fn prepaint(
        &mut self,
        _global_id: Option<&GlobalElementId>,
        _inspector_id: Option<&gpui::InspectorElementId>,
        bounds: Bounds<Pixels>,
        _request_layout: &mut Self::RequestLayoutState,
        window: &mut Window,
        cx: &mut App,
    ) -> Self::PrepaintState {
        let hitbox = window.insert_hitbox(bounds, gpui::HitboxBehavior::Normal);

        let text_style = TextStyle::with_font(&self.font_family, self.font_size);
        let background_color = text_style.background;

        let font_id = window.text_system().resolve_font(&text_style.font);
        let font_pixels = text_style.font_size.to_pixels(window.rem_size());
        let cell_width = window
            .text_system()
            .advance(font_id, font_pixels, 'm')
            .unwrap()
            .width;
        let line_height = px(f32::from(font_pixels) * 1.4);

        let dimensions = TerminalBounds::new(line_height, cell_width, bounds);

        self.terminal.update(cx, |terminal, cx| {
            terminal.set_size(dimensions);
            terminal.sync(window, cx);
        });

        let TerminalContent {
            cells,
            mode,
            display_offset,
            cursor_char,
            cursor,
            selection,
            ..
        } = &self.terminal.read(cx).last_content;
        let mode = *mode;
        let display_offset = *display_offset;

        let (rects, batched_text_runs) = Self::layout_grid(
            cells.iter().cloned(),
            &text_style,
            selection.as_ref(),
            display_offset,
        );

        let cursor_layout = if let AlacCursorShape::Hidden = cursor.shape {
            None
        } else {
            let cursor_point = DisplayCursor::from(cursor.point, display_offset);
            let cursor_text = {
                let str_text = cursor_char.to_string();
                let len = str_text.len();
                window.text_system().shape_line(
                    str_text.into(),
                    font_pixels,
                    &[TextRun {
                        len,
                        font: text_style.font.clone(),
                        color: text_style.background,
                        ..Default::default()
                    }],
                    None,
                )
            };

            let focused = self.focused;
            Self::shape_cursor(cursor_point, dimensions, &cursor_text).map(
                move |(cursor_position, block_width)| {
                    let (shape, text) = match cursor.shape {
                        AlacCursorShape::Block if !focused => (CursorShape::Hollow, None),
                        AlacCursorShape::Block => (CursorShape::Block, Some(cursor_text)),
                        AlacCursorShape::Underline => (CursorShape::Underline, None),
                        AlacCursorShape::Beam => (CursorShape::Bar, None),
                        AlacCursorShape::HollowBlock => (CursorShape::Hollow, None),
                        AlacCursorShape::Hidden => unreachable!(),
                    };

                    CursorLayout::new(
                        cursor_position,
                        block_width,
                        dimensions.line_height,
                        terminal_palette().cursor,
                        shape,
                        text,
                    )
                },
            )
        };

        LayoutState {
            hitbox,
            batched_text_runs,
            background_rects: rects,
            cursor: cursor_layout,
            background_color,
            dimensions,
            mode,
        }
    }

    fn paint(
        &mut self,
        _global_id: Option<&GlobalElementId>,
        _inspector_id: Option<&gpui::InspectorElementId>,
        bounds: Bounds<Pixels>,
        _request_layout: &mut Self::RequestLayoutState,
        layout: &mut Self::PrepaintState,
        window: &mut Window,
        cx: &mut App,
    ) {
        // Register input handler for text input
        let input_handler = TerminalInputHandler {
            terminal: self.terminal.clone(),
            cursor_bounds: layout.cursor.as_ref().map(|c| c.bounds(bounds.origin)),
        };
        window.handle_input(&self.focus, input_handler, cx);

        window.with_content_mask(Some(ContentMask { bounds }), |window| {
            window.paint_quad(fill(bounds, layout.background_color));
            let origin = bounds.origin;

            for rect in &layout.background_rects {
                rect.paint(origin, &layout.dimensions, window);
            }

            for batch in &layout.batched_text_runs {
                batch.paint(origin, &layout.dimensions, window, cx);
            }

            if self.cursor_visible {
                if let Some(cursor) = &layout.cursor {
                    cursor.paint(origin, window, cx);
                }
            }
        });
    }
}

impl IntoElement for TerminalElement {
    type Element = Self;

    fn into_element(self) -> Self::Element {
        self
    }
}

/// Checks if a cell is blank and can be skipped during rendering.
fn is_blank(cell: &IndexedCell) -> bool {
    if cell.c != ' ' {
        return false;
    }

    if cell.bg != AnsiColor::Named(NamedColor::Background) {
        return false;
    }

    if cell
        .flags
        .intersects(Flags::ALL_UNDERLINES | Flags::INVERSE | Flags::STRIKEOUT)
    {
        return false;
    }

    true
}

/// Converts an Alacritty ANSI color to GPUI's Hsla format.
///
/// Supports:
/// - Named colors (16 standard ANSI colors)
/// - True colors (24-bit RGB)
/// - Indexed colors (256-color palette)
pub fn convert_color(color: &AnsiColor) -> Hsla {
    match color {
        AnsiColor::Named(named) => named_color_to_hsla(*named),
        AnsiColor::Spec(rgb) => {
            let rgba = Rgba {
                r: rgb.r as f32 / 255.0,
                g: rgb.g as f32 / 255.0,
                b: rgb.b as f32 / 255.0,
                a: 1.0,
            };
            rgba.into()
        }
        AnsiColor::Indexed(idx) => indexed_color_to_hsla(*idx),
    }
}

/// Converts a named ANSI color to Hsla.
fn named_color_to_hsla(named: NamedColor) -> Hsla {
    let palette = terminal_palette();
    match named {
        NamedColor::Black => palette.black,
        NamedColor::Red => palette.red,
        NamedColor::Green => palette.green,
        NamedColor::Yellow => palette.yellow,
        NamedColor::Blue => palette.blue,
        NamedColor::Magenta => palette.magenta,
        NamedColor::Cyan => palette.cyan,
        NamedColor::White => palette.white,
        NamedColor::BrightBlack => palette.bright_black,
        NamedColor::BrightRed => palette.bright_red,
        NamedColor::BrightGreen => palette.bright_green,
        NamedColor::BrightYellow => palette.bright_yellow,
        NamedColor::BrightBlue => palette.bright_blue,
        NamedColor::BrightMagenta => palette.bright_magenta,
        NamedColor::BrightCyan => palette.bright_cyan,
        NamedColor::BrightWhite => palette.bright_white,
        NamedColor::Foreground => palette.foreground,
        NamedColor::Background => palette.background,
        NamedColor::Cursor => palette.cursor,
        NamedColor::DimBlack => palette.dim_black,
        NamedColor::DimRed => palette.dim_red,
        NamedColor::DimGreen => palette.dim_green,
        NamedColor::DimYellow => palette.dim_yellow,
        NamedColor::DimBlue => palette.dim_blue,
        NamedColor::DimMagenta => palette.dim_magenta,
        NamedColor::DimCyan => palette.dim_cyan,
        NamedColor::DimWhite => palette.dim_white,
        NamedColor::BrightForeground => palette.bright_foreground,
        NamedColor::DimForeground => palette.dim_foreground,
    }
}

/// Converts a 256-color palette index to Hsla.
fn indexed_color_to_hsla(idx: u8) -> Hsla {
    match idx {
        0..=15 => {
            let named = match idx {
                0 => NamedColor::Black,
                1 => NamedColor::Red,
                2 => NamedColor::Green,
                3 => NamedColor::Yellow,
                4 => NamedColor::Blue,
                5 => NamedColor::Magenta,
                6 => NamedColor::Cyan,
                7 => NamedColor::White,
                8 => NamedColor::BrightBlack,
                9 => NamedColor::BrightRed,
                10 => NamedColor::BrightGreen,
                11 => NamedColor::BrightYellow,
                12 => NamedColor::BrightBlue,
                13 => NamedColor::BrightMagenta,
                14 => NamedColor::BrightCyan,
                15 => NamedColor::BrightWhite,
                _ => unreachable!(),
            };
            named_color_to_hsla(named)
        }
        16..=231 => {
            let idx = idx - 16;
            let r = (idx / 36) % 6;
            let g = (idx / 6) % 6;
            let b = idx % 6;

            let r = if r > 0 { r * 40 + 55 } else { 0 };
            let g = if g > 0 { g * 40 + 55 } else { 0 };
            let b = if b > 0 { b * 40 + 55 } else { 0 };

            hsla_from_rgb(r, g, b)
        }
        232..=255 => {
            let gray = (idx - 232) * 10 + 8;
            hsla_from_rgb(gray, gray, gray)
        }
    }
}

/// Helper function to create Hsla from RGB values (0-255).
fn hsla_from_rgb(r: u8, g: u8, b: u8) -> Hsla {
    let rgba = Rgba {
        r: r as f32 / 255.0,
        g: g as f32 / 255.0,
        b: b as f32 / 255.0,
        a: 1.0,
    };
    rgba.into()
}

/// Input handler for the terminal that processes text input.
///
/// This handles regular character input (typing), IME composition,
/// and other text-related input events via GPUI's InputHandler trait.
struct TerminalInputHandler {
    terminal: Entity<Terminal>,
    cursor_bounds: Option<Bounds<Pixels>>,
}

impl InputHandler for TerminalInputHandler {
    fn selected_text_range(
        &mut self,
        _ignore_disabled_input: bool,
        _window: &mut Window,
        cx: &mut App,
    ) -> Option<UTF16Selection> {
        // Return empty selection when not in ALT_SCREEN mode
        // This allows text input to work
        if self
            .terminal
            .read(cx)
            .last_content
            .mode
            .contains(TermMode::ALT_SCREEN)
        {
            None
        } else {
            Some(UTF16Selection {
                range: 0..0,
                reversed: false,
            })
        }
    }

    fn marked_text_range(
        &mut self,
        _window: &mut Window,
        _cx: &mut App,
    ) -> Option<std::ops::Range<usize>> {
        // No IME composition support for now
        None
    }

    fn text_for_range(
        &mut self,
        _range: std::ops::Range<usize>,
        _actual_range: &mut Option<std::ops::Range<usize>>,
        _window: &mut Window,
        _cx: &mut App,
    ) -> Option<String> {
        None
    }

    fn replace_text_in_range(
        &mut self,
        _replacement_range: Option<std::ops::Range<usize>>,
        text: &str,
        _window: &mut Window,
        cx: &mut App,
    ) {
        // Send the typed text to the terminal
        self.terminal.update(cx, |terminal, _| {
            terminal.input_text(text);
        });
    }

    fn replace_and_mark_text_in_range(
        &mut self,
        _range_utf16: Option<std::ops::Range<usize>>,
        _new_text: &str,
        _new_marked_range: Option<std::ops::Range<usize>>,
        _window: &mut Window,
        _cx: &mut App,
    ) {
        // IME composition - not implemented yet
    }

    fn unmark_text(&mut self, _window: &mut Window, _cx: &mut App) {
        // IME - not implemented yet
    }

    fn bounds_for_range(
        &mut self,
        _range_utf16: std::ops::Range<usize>,
        _window: &mut Window,
        _cx: &mut App,
    ) -> Option<Bounds<Pixels>> {
        // Return cursor bounds for IME positioning
        self.cursor_bounds
    }

    fn character_index_for_point(
        &mut self,
        _point: Point<Pixels>,
        _window: &mut Window,
        _cx: &mut App,
    ) -> Option<usize> {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_convert_named_colors_returns_valid_hsla() {
        let colors = [
            NamedColor::Black,
            NamedColor::Red,
            NamedColor::Green,
            NamedColor::Yellow,
            NamedColor::Blue,
            NamedColor::Magenta,
            NamedColor::Cyan,
            NamedColor::White,
        ];

        for color in colors {
            let hsla = named_color_to_hsla(color);
            assert!(
                hsla.a >= 0.0 && hsla.a <= 1.0,
                "Alpha should be in valid range for {:?}",
                color
            );
            assert!(
                hsla.s >= 0.0 && hsla.s <= 1.0,
                "Saturation should be in valid range for {:?}",
                color
            );
            assert!(
                hsla.l >= 0.0 && hsla.l <= 1.0,
                "Lightness should be in valid range for {:?}",
                color
            );
        }
    }

    #[test]
    fn test_indexed_color_standard_colors_map_to_named() {
        let black = indexed_color_to_hsla(0);
        let named_black = named_color_to_hsla(NamedColor::Black);
        assert_eq!(black, named_black, "Index 0 should map to black");

        let bright_white = indexed_color_to_hsla(15);
        let named_bright_white = named_color_to_hsla(NamedColor::BrightWhite);
        assert_eq!(
            bright_white, named_bright_white,
            "Index 15 should map to bright white"
        );
    }

    #[test]
    fn test_indexed_color_cube_colors_produce_valid_hsla() {
        for idx in 16..=231u8 {
            let hsla = indexed_color_to_hsla(idx);
            assert!(
                hsla.a == 1.0,
                "Indexed color {} should have full alpha",
                idx
            );
        }
    }

    #[test]
    fn test_indexed_color_grayscale_produces_valid_hsla() {
        for idx in 232..=255u8 {
            let hsla = indexed_color_to_hsla(idx);
            assert!(
                hsla.a == 1.0,
                "Grayscale color {} should have full alpha",
                idx
            );
            assert!(
                hsla.s < 0.01,
                "Grayscale color {} should have near-zero saturation",
                idx
            );
        }
    }

    #[test]
    fn test_convert_spec_color_rgb_values() {
        let rgb = alacritty_terminal::vte::ansi::Rgb {
            r: 255,
            g: 128,
            b: 64,
        };
        let color = AnsiColor::Spec(rgb);
        let hsla = convert_color(&color);

        assert!(hsla.a == 1.0, "Spec color should have full alpha");
    }

    #[test]
    fn test_batched_text_run_can_append_same_style() {
        let style1 = TextRun {
            len: 1,
            font: Font::default(),
            color: Hsla::red(),
            ..Default::default()
        };

        let style2 = TextRun {
            len: 1,
            font: Font::default(),
            color: Hsla::red(),
            ..Default::default()
        };

        let font_size = AbsoluteLength::Pixels(px(12.0));
        let batch = BatchedTextRun::new_from_char(AlacPoint::new(0, 0), 'a', style1, font_size);

        assert!(
            batch.can_append(&style2),
            "Should be able to append same style"
        );
    }

    #[test]
    fn test_batched_text_run_cannot_append_different_color() {
        let style1 = TextRun {
            len: 1,
            font: Font::default(),
            color: Hsla::red(),
            ..Default::default()
        };

        let style2 = TextRun {
            len: 1,
            font: Font::default(),
            color: Hsla::blue(),
            ..Default::default()
        };

        let font_size = AbsoluteLength::Pixels(px(12.0));
        let batch = BatchedTextRun::new_from_char(AlacPoint::new(0, 0), 'a', style1, font_size);

        assert!(
            !batch.can_append(&style2),
            "Should not be able to append different color"
        );
    }

    #[test]
    fn test_batched_text_run_append_increments_cell_count() {
        let style = TextRun {
            len: 1,
            font: Font::default(),
            color: Hsla::red(),
            ..Default::default()
        };

        let font_size = AbsoluteLength::Pixels(px(12.0));
        let mut batch = BatchedTextRun::new_from_char(AlacPoint::new(0, 0), 'a', style, font_size);

        assert_eq!(batch.cell_count, 1, "Initial cell count should be 1");
        assert_eq!(batch.text, "a", "Initial text should be 'a'");

        batch.append_char('b');

        assert_eq!(
            batch.cell_count, 2,
            "Cell count should increment after append"
        );
        assert_eq!(batch.text, "ab", "Text should be 'ab' after append");
    }

    #[test]
    fn test_background_region_can_merge_horizontal_adjacent() {
        let color = Hsla::red();
        let mut region1 = BackgroundRegion::new(0, 0, color);
        region1.end_col = 2;
        let region2 = BackgroundRegion::new(0, 3, color);

        assert!(
            region1.can_merge_with(&region2),
            "Adjacent horizontal regions with same color should merge"
        );
    }

    #[test]
    fn test_background_region_cannot_merge_different_colors() {
        let region1 = BackgroundRegion::new(0, 0, Hsla::red());
        let region2 = BackgroundRegion::new(0, 1, Hsla::blue());

        assert!(
            !region1.can_merge_with(&region2),
            "Regions with different colors should not merge"
        );
    }

    #[test]
    fn test_display_cursor_calculates_offset_correctly() {
        let cursor_point = AlacPoint::new(
            alacritty_terminal::index::Line(5),
            alacritty_terminal::index::Column(10),
        );
        let display_offset = 3usize;

        let display_cursor = DisplayCursor::from(cursor_point, display_offset);

        assert_eq!(
            display_cursor.line(),
            8,
            "Line should be cursor line + display offset"
        );
        assert_eq!(
            display_cursor.col(),
            10,
            "Column should match cursor column"
        );
    }
}
