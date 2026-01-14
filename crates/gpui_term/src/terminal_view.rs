//! Terminal view component for gpui-term.
//!
//! This module provides the TerminalView struct which is the main GPUI entity
//! that handles input events and renders the terminal using TerminalElement.
//!
//! # Architecture
//!
//! TerminalView acts as the glue between GPUI's event system and the Terminal entity.
//! It:
//! - Receives keyboard events and forwards them to the terminal via try_keystroke
//! - Handles mouse events (down, up, move, scroll) for selection and mouse reporting
//! - Provides copy/paste/clear/select_all actions bound to keyboard shortcuts
//! - Subscribes to terminal events (Wakeup, Bell, TitleChanged, etc.) to update UI
//!
//! # Example
//!
//! ```ignore
//! let terminal = TerminalBuilder::new(...)?.subscribe(cx);
//! let terminal_entity = cx.new(|_| terminal);
//! let view = cx.new(|cx| TerminalView::new(terminal_entity, cx));
//! ```

use gpui::{
    App, ClipboardItem, Context, Entity, FocusHandle, Focusable, InteractiveElement, IntoElement,
    KeyDownEvent, MouseButton, MouseDownEvent, MouseMoveEvent, MouseUpEvent, ParentElement, Render,
    ScrollWheelEvent, Styled, Window, actions, div,
};

use crate::{Event, Terminal, TerminalElement};

actions!(
    terminal,
    [
        Copy,
        Paste,
        Clear,
        SelectAll,
        ScrollLineUp,
        ScrollLineDown,
        ScrollPageUp,
        ScrollPageDown,
        SendTab,
        SendShiftTab,
        FocusOut
    ]
);

/// Main terminal view component that handles input and coordinates rendering.
///
/// This struct wraps a Terminal entity and provides:
/// - Focus management for keyboard input routing
/// - Event handlers for keyboard and mouse input
/// - Action handlers for clipboard operations and scrolling
/// - Subscription to terminal events for UI updates
pub struct TerminalView {
    terminal: Entity<Terminal>,
    focus_handle: FocusHandle,
    has_bell: bool,
    font_family: String,
    font_size: f32,
}

impl TerminalView {
    /// Creates a new TerminalView wrapping the given Terminal entity.
    ///
    /// Sets up event subscriptions and focus handling for the terminal.
    pub fn new(terminal: Entity<Terminal>, window: &mut Window, cx: &mut Context<Self>) -> Self {
        Self::with_settings(terminal, window, cx, "JetBrains Mono", 14.0)
    }

    /// Creates a new TerminalView with custom font settings.
    ///
    /// # Arguments
    ///
    /// * `terminal` - The Terminal entity to wrap
    /// * `font_family` - The font family name for terminal rendering
    /// * `font_size` - The font size in pixels
    pub fn with_settings(
        terminal: Entity<Terminal>,
        window: &mut Window,
        cx: &mut Context<Self>,
        font_family: impl Into<String>,
        font_size: f32,
    ) -> Self {
        let focus_handle = cx.focus_handle();

        cx.subscribe(&terminal, |this, _, event: &Event, cx| {
            this.handle_terminal_event(event, cx);
        })
        .detach();

        cx.on_focus_in(&focus_handle, window, |this: &mut Self, _window, cx| {
            this.terminal.update(cx, |terminal, _| {
                terminal.focus_in();
            });
        })
        .detach();

        cx.on_focus_out(
            &focus_handle,
            window,
            |this: &mut Self, _event, _window, cx| {
                this.terminal.update(cx, |terminal, _| {
                    terminal.focus_out();
                });
            },
        )
        .detach();

        Self {
            terminal,
            focus_handle,
            has_bell: false,
            font_family: font_family.into(),
            font_size,
        }
    }

    /// Updates the font settings for the terminal.
    pub fn set_font_settings(&mut self, font_family: impl Into<String>, font_size: f32) {
        self.font_family = font_family.into();
        self.font_size = font_size;
    }

    /// Returns a reference to the underlying Terminal entity.
    pub fn terminal(&self) -> &Entity<Terminal> {
        &self.terminal
    }

    /// Returns whether the terminal bell has sounded since last cleared.
    pub fn has_bell(&self) -> bool {
        self.has_bell
    }

    /// Clears the bell indicator.
    pub fn clear_bell(&mut self, cx: &mut Context<Self>) {
        self.has_bell = false;
        cx.notify();
    }

    fn handle_terminal_event(&mut self, event: &Event, cx: &mut Context<Self>) {
        match event {
            Event::Wakeup => cx.notify(),
            Event::Bell => {
                self.has_bell = true;
                cx.notify();
            }
            Event::TitleChanged => cx.notify(),
            Event::BlinkChanged(_) => cx.notify(),
            Event::SelectionsChanged => cx.notify(),
            Event::CloseTerminal => {
                cx.notify();
            }
            Event::OpenHyperlink(url) => {
                // Open the hyperlink using the system's default handler
                #[cfg(target_os = "macos")]
                {
                    let _ = std::process::Command::new("open").arg(url).spawn();
                }
                #[cfg(target_os = "linux")]
                {
                    let _ = std::process::Command::new("xdg-open").arg(url).spawn();
                }
                #[cfg(target_os = "windows")]
                {
                    let _ = std::process::Command::new("cmd")
                        .args(["/C", "start", "", url])
                        .spawn();
                }
            }
        }
    }

    fn on_key_down(&mut self, event: &KeyDownEvent, _window: &mut Window, cx: &mut Context<Self>) {
        self.clear_bell(cx);

        let handled = self.terminal.update(cx, |terminal, _| {
            terminal.try_keystroke(&event.keystroke, false)
        });

        if handled {
            cx.stop_propagation();
        }
    }

    fn on_mouse_down(
        &mut self,
        event: &MouseDownEvent,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        window.focus(&self.focus_handle, cx);
        window.prevent_default();

        self.terminal.update(cx, |terminal, cx| {
            terminal.mouse_down(event, cx);
        });
        cx.notify();
    }

    fn on_mouse_up(&mut self, event: &MouseUpEvent, _window: &mut Window, cx: &mut Context<Self>) {
        self.terminal.update(cx, |terminal, cx| {
            terminal.mouse_up(event, cx);
        });
        cx.notify();
    }

    fn on_mouse_move(
        &mut self,
        event: &MouseMoveEvent,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.terminal.update(cx, |terminal, cx| {
            terminal.mouse_move(event, cx);
        });
    }

    fn on_mouse_drag(
        &mut self,
        event: &MouseMoveEvent,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let bounds = self.terminal.read(cx).last_content.terminal_bounds.bounds;
        self.terminal.update(cx, |terminal, cx| {
            terminal.mouse_drag(event, bounds, cx);
        });
    }

    fn on_scroll(
        &mut self,
        event: &ScrollWheelEvent,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.terminal.update(cx, |terminal, _| {
            terminal.scroll_wheel(event, 1.0);
        });
        cx.notify();
    }

    fn copy(&mut self, _: &Copy, _window: &mut Window, cx: &mut Context<Self>) {
        self.terminal.update(cx, |terminal, _| {
            terminal.copy(None);
        });

        if let Some(text) = self.terminal.read(cx).last_content.selection_text.clone() {
            cx.write_to_clipboard(ClipboardItem::new_string(text));
        }
        cx.notify();
    }

    fn paste(&mut self, _: &Paste, _window: &mut Window, cx: &mut Context<Self>) {
        if let Some(item) = cx.read_from_clipboard() {
            if let Some(text) = item.text() {
                self.terminal.update(cx, |terminal, _| {
                    terminal.paste(&text);
                });
            }
        }
    }

    fn clear(&mut self, _: &Clear, _window: &mut Window, cx: &mut Context<Self>) {
        self.terminal.update(cx, |terminal, _| {
            terminal.clear();
        });
        cx.notify();
    }

    fn select_all(&mut self, _: &SelectAll, _window: &mut Window, cx: &mut Context<Self>) {
        self.terminal.update(cx, |terminal, _| {
            terminal.select_all();
        });
        cx.notify();
    }

    fn scroll_line_up(&mut self, _: &ScrollLineUp, _window: &mut Window, cx: &mut Context<Self>) {
        self.terminal.update(cx, |terminal, _| {
            terminal.scroll_line_up();
        });
        cx.notify();
    }

    fn scroll_line_down(
        &mut self,
        _: &ScrollLineDown,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.terminal.update(cx, |terminal, _| {
            terminal.scroll_line_down();
        });
        cx.notify();
    }

    fn scroll_page_up(&mut self, _: &ScrollPageUp, _window: &mut Window, cx: &mut Context<Self>) {
        self.terminal.update(cx, |terminal, _| {
            terminal.scroll_page_up();
        });
        cx.notify();
    }

    fn scroll_page_down(
        &mut self,
        _: &ScrollPageDown,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.terminal.update(cx, |terminal, _| {
            terminal.scroll_page_down();
        });
        cx.notify();
    }

    /// Synchronizes terminal state with the window for rendering.
    ///
    /// Should be called before rendering to ensure the terminal content is up to date.
    pub fn sync(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.terminal.update(cx, |terminal, cx| {
            terminal.sync(window, cx);
        });
    }

    /// Sends a tab character to the terminal.
    fn send_tab(&mut self, _: &SendTab, _window: &mut Window, cx: &mut Context<Self>) {
        self.clear_bell(cx);
        self.terminal.update(cx, |terminal, _| {
            // Send tab character (0x09) directly to terminal
            terminal.input("\x09".as_bytes());
        });
    }

    /// Sends shift-tab escape sequence to the terminal.
    fn send_shift_tab(&mut self, _: &SendShiftTab, _window: &mut Window, cx: &mut Context<Self>) {
        self.clear_bell(cx);
        self.terminal.update(cx, |terminal, _| {
            // Send shift-tab escape sequence (CSI Z) to terminal
            terminal.input("\x1b[Z".as_bytes());
        });
    }

    /// Moves focus out of the terminal to the next focusable element.
    fn focus_out(&mut self, _: &FocusOut, window: &mut Window, cx: &mut Context<Self>) {
        window.focus_next(cx);
    }
}

impl Focusable for TerminalView {
    fn focus_handle(&self, _cx: &App) -> FocusHandle {
        self.focus_handle.clone()
    }
}

impl Render for TerminalView {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let terminal = self.terminal.clone();
        let focus_handle = self.focus_handle.clone();
        let is_focused = self.focus_handle.is_focused(window);

        div()
            .id("terminal-view")
            .size_full()
            // Transparent background to allow blur effect from parent window
            .bg(gpui::transparent_black())
            .track_focus(&focus_handle)
            .key_context("Terminal")
            .on_key_down(cx.listener(Self::on_key_down))
            .on_mouse_down(MouseButton::Left, cx.listener(Self::on_mouse_down))
            .on_mouse_down(MouseButton::Right, cx.listener(Self::on_mouse_down))
            .on_mouse_down(MouseButton::Middle, cx.listener(Self::on_mouse_down))
            .on_mouse_up(MouseButton::Left, cx.listener(Self::on_mouse_up))
            .on_mouse_up(MouseButton::Right, cx.listener(Self::on_mouse_up))
            .on_mouse_up(MouseButton::Middle, cx.listener(Self::on_mouse_up))
            .on_mouse_move(cx.listener(|this, event: &MouseMoveEvent, window, cx| {
                if event.dragging() {
                    this.on_mouse_drag(event, window, cx);
                } else {
                    this.on_mouse_move(event, window, cx);
                }
            }))
            .on_scroll_wheel(cx.listener(Self::on_scroll))
            .on_action(cx.listener(Self::copy))
            .on_action(cx.listener(Self::paste))
            .on_action(cx.listener(Self::clear))
            .on_action(cx.listener(Self::select_all))
            .on_action(cx.listener(Self::scroll_line_up))
            .on_action(cx.listener(Self::scroll_line_down))
            .on_action(cx.listener(Self::scroll_page_up))
            .on_action(cx.listener(Self::scroll_page_down))
            .on_action(cx.listener(Self::send_tab))
            .on_action(cx.listener(Self::send_shift_tab))
            .on_action(cx.listener(Self::focus_out))
            .child(TerminalElement::with_settings(
                terminal,
                focus_handle.clone(),
                is_focused,
                true,
                &self.font_family,
                self.font_size,
            ))
    }
}
