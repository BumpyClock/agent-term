//! Layout store for multi-window session management.
//!
//! This crate provides persistence for window layouts, tab ordering, and
//! session restore functionality. It stores ephemeral session state that
//! gets restored on app launch, plus a stack of closed tabs for undo.
//!
//! ## Usage
//!
//! ```rust,ignore
//! let store = LayoutStore::open_default_profile()?;
//!
//! // Get current window layout
//! let window = store.get_window("window-1");
//!
//! // Move a tab to another window
//! store.move_tab("session-id", "source-window", "target-window");
//!
//! // Save session on app exit
//! store.save_current_session(&session_snapshot);
//!
//! // Restore on next launch
//! if let Some(session) = store.load_last_session() {
//!     // Recreate windows and tabs from session
//! }
//! ```

pub mod error;
pub mod model;
pub mod storage;

pub use error::{LayoutError, LayoutResult};
pub use model::{
    ClosedTabSnapshot, LayoutSessionSnapshot, LayoutSnapshot, SavedWorkspaceSnapshot, TabSnapshot,
    WindowSnapshot,
};

use parking_lot::Mutex;
use uuid::Uuid;

use storage::{DebouncedStorage, Storage, default_storage_root};

/// The default section ID for tabs not assigned to a project.
pub const DEFAULT_SECTION_ID: &str = "default-section";

/// Maximum number of closed tabs to keep in the stack.
const MAX_CLOSED_TABS: usize = 50;

/// Layout store for managing window layouts and session restore.
///
/// Provides methods for tracking window/tab layouts, moving tabs between
/// windows, and restoring sessions on app launch.
///
/// The store maintains two separate concerns:
/// 1. **Current session** - live window/tab state during runtime (in-memory)
/// 2. **Persisted state** - last_session, closed_tab_stack, saved_workspaces (on disk)
pub struct LayoutStore {
    storage: DebouncedStorage,
    snapshot: Mutex<LayoutSnapshot>,
    /// Current runtime session (windows currently open).
    /// This is separate from last_session which is only for restore.
    current_session: Mutex<LayoutSessionSnapshot>,
}

impl LayoutStore {
    /// Opens the layout store for the default profile.
    pub fn open_default_profile() -> Result<Self, String> {
        Self::open_profile("default")
    }

    /// Opens the layout store for a specific profile.
    pub fn open_profile(profile: impl Into<String>) -> Result<Self, String> {
        let storage = Storage::new(default_storage_root(), profile.into());
        let snapshot = storage.load().map_err(|e| e.to_string())?;
        let debounced = DebouncedStorage::new(storage, 500);
        Ok(Self {
            storage: debounced,
            snapshot: Mutex::new(snapshot),
            current_session: Mutex::new(LayoutSessionSnapshot {
                id: Uuid::new_v4().to_string(),
                created_at: now_rfc3339(),
                windows: Vec::new(),
            }),
        })
    }

    /// Returns a clone of the current snapshot.
    pub fn snapshot(&self) -> LayoutSnapshot {
        self.snapshot.lock().clone()
    }

    /// Loads the last session snapshot for restore on launch.
    pub fn load_last_session(&self) -> Option<LayoutSessionSnapshot> {
        self.snapshot.lock().last_session.clone()
    }

    /// Saves the current session layout (called on app exit).
    pub fn save_last_session(&self, session: LayoutSessionSnapshot) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        snapshot.last_session = Some(session);
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    /// Clears the last session (e.g., after successful restore).
    pub fn clear_last_session(&self) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        snapshot.last_session = None;
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    /// Gets a window snapshot by ID from the current runtime session.
    pub fn get_window(&self, window_id: &str) -> Option<WindowSnapshot> {
        let session = self.current_session.lock();
        session.windows.iter().find(|w| w.id == window_id).cloned()
    }

    /// Gets a window snapshot by ID from the persisted last session (for restore).
    pub fn get_persisted_window(&self, window_id: &str) -> Option<WindowSnapshot> {
        let snapshot = self.snapshot.lock();
        snapshot
            .last_session
            .as_ref()
            .and_then(|session| session.windows.iter().find(|w| w.id == window_id).cloned())
    }

    /// Adds a new window to the current session and returns its ID.
    pub fn add_window(&self, window: WindowSnapshot) -> String {
        let mut session = self.current_session.lock();
        let id = window.id.clone();
        session.windows.push(window);
        id
    }

    /// Removes a window from the current session.
    pub fn remove_window(&self, window_id: &str) -> Option<WindowSnapshot> {
        let mut session = self.current_session.lock();
        if let Some(idx) = session.windows.iter().position(|w| w.id == window_id) {
            Some(session.windows.remove(idx))
        } else {
            None
        }
    }

    /// Updates a window in the current session.
    pub fn update_window<F>(&self, window_id: &str, f: F) -> bool
    where
        F: FnOnce(&mut WindowSnapshot),
    {
        let mut session = self.current_session.lock();
        if let Some(window) = session.windows.iter_mut().find(|w| w.id == window_id) {
            f(window);
            true
        } else {
            false
        }
    }

    /// Returns all windows in the current session.
    pub fn list_windows(&self) -> Vec<WindowSnapshot> {
        self.current_session.lock().windows.clone()
    }

    /// Returns the number of windows in the current session.
    pub fn window_count(&self) -> usize {
        self.current_session.lock().windows.len()
    }

    /// Returns the current session snapshot (for saving on exit).
    pub fn current_session(&self) -> LayoutSessionSnapshot {
        self.current_session.lock().clone()
    }

    /// Replaces the current session snapshot.
    pub fn set_current_session(&self, session: LayoutSessionSnapshot) {
        *self.current_session.lock() = session;
    }

    /// Pushes a closed tab onto the stack for later restore.
    pub fn push_closed_tab(&self, tab: ClosedTabSnapshot) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        snapshot.closed_tab_stack.push(tab);
        if snapshot.closed_tab_stack.len() > MAX_CLOSED_TABS {
            snapshot.closed_tab_stack.remove(0);
        }
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    /// Pops the most recently closed tab from the stack.
    pub fn pop_closed_tab(&self) -> Option<ClosedTabSnapshot> {
        let mut snapshot = self.snapshot.lock();
        let tab = snapshot.closed_tab_stack.pop();
        if tab.is_some() {
            let _ = self.storage.save(&snapshot);
        }
        tab
    }

    /// Returns the number of closed tabs in the stack.
    pub fn closed_tab_count(&self) -> usize {
        self.snapshot.lock().closed_tab_stack.len()
    }

    /// Saves the last closed session for full restore.
    pub fn save_closed_session(&self, session: LayoutSessionSnapshot) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        snapshot.last_closed_session = Some(session);
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    /// Retrieves and clears the last closed session.
    pub fn pop_closed_session(&self) -> Option<LayoutSessionSnapshot> {
        let mut snapshot = self.snapshot.lock();
        let session = snapshot.last_closed_session.take();
        if session.is_some() {
            let _ = self.storage.save(&snapshot);
        }
        session
    }

    /// Saves a named workspace for later restore.
    pub fn save_workspace(
        &self,
        name: String,
        session: LayoutSessionSnapshot,
    ) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        let workspace = SavedWorkspaceSnapshot {
            id: Uuid::new_v4().to_string(),
            name,
            created_at: now_rfc3339(),
            session,
        };
        snapshot.saved_workspaces.push(workspace);
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    /// Lists all saved workspaces.
    pub fn list_workspaces(&self) -> Vec<SavedWorkspaceSnapshot> {
        self.snapshot.lock().saved_workspaces.clone()
    }

    /// Deletes a saved workspace by ID.
    pub fn delete_workspace(&self, id: &str) -> Result<(), String> {
        let mut snapshot = self.snapshot.lock();
        snapshot.saved_workspaces.retain(|w| w.id != id);
        self.storage.save(&snapshot).map_err(|e| e.to_string())
    }

    /// Gets a saved workspace by ID.
    pub fn get_workspace(&self, id: &str) -> Option<SavedWorkspaceSnapshot> {
        self.snapshot
            .lock()
            .saved_workspaces
            .iter()
            .find(|w| w.id == id)
            .cloned()
    }

    /// Forces an immediate save (useful before app exit).
    pub fn flush(&self) -> Result<(), String> {
        let snapshot = self.snapshot.lock();
        self.storage
            .save_immediate(&snapshot)
            .map_err(|e| e.to_string())
    }
}

fn now_rfc3339() -> String {
    time::OffsetDateTime::now_utc()
        .format(&time::format_description::well_known::Rfc3339)
        .unwrap_or_default()
}

/// Creates a new session snapshot with a unique ID.
pub fn new_session_snapshot(windows: Vec<WindowSnapshot>) -> LayoutSessionSnapshot {
    LayoutSessionSnapshot {
        id: Uuid::new_v4().to_string(),
        created_at: now_rfc3339(),
        windows,
    }
}

/// Creates a new window snapshot with a unique ID.
pub fn new_window_snapshot(order: u32) -> WindowSnapshot {
    WindowSnapshot::new(Uuid::new_v4().to_string(), order)
}

/// Creates a closed tab snapshot from a tab.
pub fn closed_tab_from(tab: &TabSnapshot, window_id: Option<String>) -> ClosedTabSnapshot {
    ClosedTabSnapshot {
        session_id: tab.session_id.clone(),
        section_id: tab.section_id.clone(),
        window_id,
        order: tab.order,
        closed_at: now_rfc3339(),
    }
}
