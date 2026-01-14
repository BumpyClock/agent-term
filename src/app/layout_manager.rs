//! Global layout manager for multi-window support.
//!
//! Provides a singleton LayoutStore that manages window layouts, tab ordering,
//! and session restore across all windows.

use agentterm_layout::{LayoutStore, new_window_snapshot};
use gpui::AnyWindowHandle;
use parking_lot::Mutex;
use std::collections::HashMap;
use std::sync::{Arc, OnceLock};

/// Maps layout window IDs to gpui window handles for restore operations.
#[derive(Clone)]
pub struct LayoutManager {
    inner: Arc<Mutex<LayoutManagerInner>>,
    store: Arc<LayoutStore>,
}

struct LayoutManagerInner {
    /// Maps layout window ID -> gpui window handle
    window_handles: HashMap<String, AnyWindowHandle>,
    /// Next window order number
    next_order: u32,
}

impl LayoutManager {
    /// Returns the global layout manager singleton.
    pub fn global() -> Self {
        static INSTANCE: OnceLock<LayoutManager> = OnceLock::new();
        INSTANCE
            .get_or_init(|| {
                let store = Arc::new(
                    LayoutStore::open_default_profile().expect("failed to open layout store"),
                );
                LayoutManager {
                    inner: Arc::new(Mutex::new(LayoutManagerInner {
                        window_handles: HashMap::new(),
                        next_order: 1,
                    })),
                    store,
                }
            })
            .clone()
    }

    /// Returns the shared LayoutStore.
    pub fn store(&self) -> &Arc<LayoutStore> {
        &self.store
    }

    /// Creates a new window layout and returns its ID.
    /// The window is added to the layout store's current session.
    pub fn create_window(&self) -> String {
        let mut inner = self.inner.lock();
        let window = new_window_snapshot(inner.next_order);
        inner.next_order += 1;
        self.store.add_window(window)
    }

    /// Registers a gpui window handle for a layout window ID.
    pub fn register_handle(&self, layout_window_id: String, handle: AnyWindowHandle) {
        let mut inner = self.inner.lock();
        inner.window_handles.insert(layout_window_id, handle);
    }

    /// Unregisters a window handle.
    pub fn unregister(&self, layout_window_id: &str) {
        let mut inner = self.inner.lock();
        inner.window_handles.remove(layout_window_id);
    }

    /// Gets the gpui handle for a layout window ID.
    pub fn get_handle(&self, layout_window_id: &str) -> Option<AnyWindowHandle> {
        let inner = self.inner.lock();
        inner.window_handles.get(layout_window_id).copied()
    }

    /// Returns the number of registered windows.
    pub fn window_count(&self) -> usize {
        let inner = self.inner.lock();
        inner.window_handles.len()
    }

    pub fn set_next_order(&self, next_order: u32) {
        let mut inner = self.inner.lock();
        inner.next_order = next_order;
    }

    /// Lists all layout window IDs except the given one.
    pub fn list_other_windows(&self, exclude: &str) -> Vec<String> {
        let inner = self.inner.lock();
        inner
            .window_handles
            .keys()
            .filter(|id| *id != exclude)
            .cloned()
            .collect()
    }
}

impl Default for LayoutManager {
    fn default() -> Self {
        Self::global()
    }
}
