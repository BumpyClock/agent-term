//! Global terminal pool for multi-window support.
//!
//! This module provides a global registry of Terminal entities that can be shared
//! across multiple windows. Terminals live in the pool until explicitly closed,
//! independent of window lifetime. This enables browser-like tab behavior where
//! sessions can be moved between windows without restarting the terminal process.

use gpui::Entity;
use gpui_term::Terminal;
use parking_lot::Mutex;
use std::collections::HashMap;
use std::sync::{Arc, OnceLock};

/// Global pool of Terminal entities shared across all windows.
///
/// Terminals are stored by session ID and persist until explicitly shutdown.
/// Multiple windows can create views for the same terminal, enabling
/// session transfer without losing scrollback or terminal state.
#[derive(Clone)]
pub struct TerminalPool {
    inner: Arc<Mutex<TerminalPoolInner>>,
}

struct TerminalPoolInner {
    terminals: HashMap<String, Entity<Terminal>>,
}

impl TerminalPool {
    /// Returns the global terminal pool singleton.
    pub fn global() -> Self {
        static INSTANCE: OnceLock<TerminalPool> = OnceLock::new();
        INSTANCE
            .get_or_init(|| TerminalPool {
                inner: Arc::new(Mutex::new(TerminalPoolInner {
                    terminals: HashMap::new(),
                })),
            })
            .clone()
    }

    /// Gets an existing terminal by session ID.
    ///
    /// Returns None if no terminal exists for this session.
    pub fn get(&self, session_id: &str) -> Option<Entity<Terminal>> {
        let inner = self.inner.lock();
        inner.terminals.get(session_id).cloned()
    }

    /// Inserts a terminal into the pool.
    ///
    /// If a terminal already exists for this session ID, it is replaced
    /// and the old terminal will be dropped (shutting down its PTY).
    pub fn insert(&self, session_id: String, terminal: Entity<Terminal>) {
        let mut inner = self.inner.lock();
        inner.terminals.insert(session_id, terminal);
    }

    /// Removes and returns a terminal from the pool.
    ///
    /// The caller is responsible for shutting down the terminal.
    pub fn remove(&self, session_id: &str) -> Option<Entity<Terminal>> {
        let mut inner = self.inner.lock();
        inner.terminals.remove(session_id)
    }

    /// Checks if a terminal exists for the given session ID.
    pub fn contains(&self, session_id: &str) -> bool {
        let inner = self.inner.lock();
        inner.terminals.contains_key(session_id)
    }

    /// Returns the number of terminals in the pool.
    #[allow(dead_code)]
    pub fn len(&self) -> usize {
        let inner = self.inner.lock();
        inner.terminals.len()
    }

    /// Returns true if the pool is empty.
    #[allow(dead_code)]
    pub fn is_empty(&self) -> bool {
        let inner = self.inner.lock();
        inner.terminals.is_empty()
    }

    /// Returns all session IDs currently in the pool.
    #[allow(dead_code)]
    pub fn session_ids(&self) -> Vec<String> {
        let inner = self.inner.lock();
        inner.terminals.keys().cloned().collect()
    }
}

impl Default for TerminalPool {
    fn default() -> Self {
        Self::global()
    }
}
