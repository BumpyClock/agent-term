//! Global window registry for multi-window support.
//!
//! This module tracks all open AgentTerm windows, enabling features like:
//! - "Move to Window" context menu with list of available windows
//! - Window numbering for titles (Agent Term, Agent Term - 2, etc.)
//! - Cross-window communication for session transfer

use gpui::{AnyWindowHandle, WeakEntity};
use parking_lot::Mutex;
use std::collections::HashMap;
use std::sync::{Arc, OnceLock};

use super::state::AgentTermApp;

/// Information about a registered window.
#[derive(Clone)]
pub struct WindowInfo {
    /// Weak reference to the window's AgentTermApp entity.
    pub app: WeakEntity<AgentTermApp>,
    /// Display title for this window (e.g., "Agent Term - 2").
    pub title: String,
    /// Window number (1, 2, 3, etc.) used for identification.
    pub number: u32,
}

/// Global registry of all open AgentTerm windows.
///
/// Provides a centralized way to track windows for features that need
/// cross-window awareness, such as session transfer between windows.
#[derive(Clone)]
pub struct WindowRegistry {
    inner: Arc<Mutex<WindowRegistryInner>>,
}

struct WindowRegistryInner {
    windows: HashMap<AnyWindowHandle, WindowInfo>,
    next_window_number: u32,
}

impl WindowRegistry {
    /// Returns the global window registry singleton.
    pub fn global() -> Self {
        static INSTANCE: OnceLock<WindowRegistry> = OnceLock::new();
        INSTANCE
            .get_or_init(|| WindowRegistry {
                inner: Arc::new(Mutex::new(WindowRegistryInner {
                    windows: HashMap::new(),
                    next_window_number: 1,
                })),
            })
            .clone()
    }

    /// Registers a new window in the registry.
    ///
    /// Returns the window number assigned to this window.
    pub fn register(&self, handle: AnyWindowHandle, app: WeakEntity<AgentTermApp>) -> u32 {
        let mut inner = self.inner.lock();
        let number = inner.next_window_number;
        inner.next_window_number += 1;

        let title = if number == 1 {
            "Agent Term".to_string()
        } else {
            format!("Agent Term - {}", number)
        };

        inner
            .windows
            .insert(handle, WindowInfo { app, title, number });

        number
    }

    /// Unregisters a window from the registry.
    pub fn unregister(&self, handle: &AnyWindowHandle) {
        let mut inner = self.inner.lock();
        inner.windows.remove(handle);
    }

    /// Lists all registered windows with their handles, titles, and app references.
    ///
    /// Filters out windows whose app entities have been dropped.
    pub fn list_windows(&self) -> Vec<(AnyWindowHandle, WindowInfo)> {
        let inner = self.inner.lock();
        inner
            .windows
            .iter()
            .filter(|(_, info)| info.app.upgrade().is_some())
            .map(|(handle, info)| (*handle, info.clone()))
            .collect()
    }

    /// Lists windows excluding the specified one (useful for "Move to Window" menu).
    pub fn list_other_windows(
        &self,
        exclude: AnyWindowHandle,
    ) -> Vec<(AnyWindowHandle, WindowInfo)> {
        self.list_windows()
            .into_iter()
            .filter(|(handle, _)| *handle != exclude)
            .collect()
    }

    /// Returns the number of registered windows.
    pub fn window_count(&self) -> usize {
        let inner = self.inner.lock();
        inner.windows.len()
    }

    /// Gets info for a specific window.
    pub fn get_window_info(&self, handle: &AnyWindowHandle) -> Option<WindowInfo> {
        let inner = self.inner.lock();
        inner.windows.get(handle).cloned()
    }

    /// Gets the AgentTermApp entity for a window by its handle.
    pub fn get_app(&self, handle: &AnyWindowHandle) -> Option<WeakEntity<AgentTermApp>> {
        let inner = self.inner.lock();
        inner.windows.get(handle).map(|info| info.app.clone())
    }
}

impl Default for WindowRegistry {
    fn default() -> Self {
        Self::global()
    }
}
