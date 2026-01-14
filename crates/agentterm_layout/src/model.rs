//! Layout data model for multi-window session management.
//!
//! This module defines the core data structures for tracking window layouts,
//! tab ordering, and session restore functionality.

use serde::{Deserialize, Serialize};

/// Complete layout snapshot persisted to disk.
///
/// Contains the last active session layout, closed session for restore,
/// closed tab stack, and saved workspaces.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LayoutSnapshot {
    #[serde(default = "default_schema_version")]
    pub schema_version: u32,
    pub last_session: Option<LayoutSessionSnapshot>,
    pub last_closed_session: Option<LayoutSessionSnapshot>,
    #[serde(default)]
    pub closed_tab_stack: Vec<ClosedTabSnapshot>,
    #[serde(default)]
    pub saved_workspaces: Vec<SavedWorkspaceSnapshot>,
}

fn default_schema_version() -> u32 {
    1
}

impl Default for LayoutSnapshot {
    fn default() -> Self {
        Self {
            schema_version: default_schema_version(),
            last_session: None,
            last_closed_session: None,
            closed_tab_stack: Vec::new(),
            saved_workspaces: Vec::new(),
        }
    }
}

/// Snapshot of a complete session layout across all windows.
///
/// Captured on app exit and used for session restore on launch.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LayoutSessionSnapshot {
    pub id: String,
    pub created_at: String,
    pub windows: Vec<WindowSnapshot>,
}

/// Snapshot of a single window's layout.
///
/// Contains tab ordering, active tab, and section (project) ordering.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WindowSnapshot {
    pub id: String,
    pub order: u32,
    pub active_session_id: Option<String>,
    #[serde(default)]
    pub section_order: Vec<String>,
    #[serde(default)]
    pub tabs: Vec<TabSnapshot>,
    #[serde(default)]
    pub collapsed_sections: Vec<String>,
}

impl WindowSnapshot {
    /// Creates a new window snapshot with the given id and order.
    pub fn new(id: String, order: u32) -> Self {
        Self {
            id,
            order,
            active_session_id: None,
            section_order: Vec::new(),
            tabs: Vec::new(),
            collapsed_sections: Vec::new(),
        }
    }

    /// Returns the tab with the highest order value, if any.
    pub fn max_tab_order(&self) -> u32 {
        self.tabs.iter().map(|t| t.order).max().unwrap_or(0)
    }

    /// Adds a tab at the end of the tab list.
    pub fn append_tab(&mut self, session_id: String, section_id: String) {
        let order = self.max_tab_order().saturating_add(1);
        self.tabs.push(TabSnapshot {
            session_id,
            section_id,
            order,
        });
    }

    /// Removes a tab by session_id and returns it if found.
    pub fn remove_tab(&mut self, session_id: &str) -> Option<TabSnapshot> {
        if let Some(idx) = self.tabs.iter().position(|t| t.session_id == session_id) {
            Some(self.tabs.remove(idx))
        } else {
            None
        }
    }

    /// Returns tabs belonging to a specific section, sorted by order.
    pub fn tabs_in_section(&self, section_id: &str) -> Vec<&TabSnapshot> {
        let mut tabs: Vec<_> = self
            .tabs
            .iter()
            .filter(|t| t.section_id == section_id)
            .collect();
        tabs.sort_by_key(|t| t.order);
        tabs
    }

    /// Returns all session_ids in this window.
    pub fn session_ids(&self) -> Vec<String> {
        self.tabs.iter().map(|t| t.session_id.clone()).collect()
    }
}

/// Snapshot of a single tab within a window.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TabSnapshot {
    pub session_id: String,
    pub section_id: String,
    pub order: u32,
}

/// Snapshot of a closed tab for restore functionality.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClosedTabSnapshot {
    pub session_id: String,
    pub section_id: String,
    pub window_id: Option<String>,
    pub order: u32,
    pub closed_at: String,
}

/// A saved workspace layout for explicit user saves.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SavedWorkspaceSnapshot {
    pub id: String,
    pub name: String,
    pub created_at: String,
    pub session: LayoutSessionSnapshot,
}
