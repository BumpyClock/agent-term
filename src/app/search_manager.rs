//! Global search manager for command palette search.
//!
//! Provides a singleton SearchManager that indexes Claude conversation logs
//! from ~/.claude/projects/ for quick search in the command palette.

use agentterm_search::SearchManager;
use std::sync::{Arc, OnceLock};

/// Returns the global SearchManager singleton.
///
/// The search manager is initialized once and shared across all windows.
/// Indexing happens lazily on first search or can be triggered manually.
pub fn global_search_manager() -> Arc<SearchManager> {
    static INSTANCE: OnceLock<Arc<SearchManager>> = OnceLock::new();
    INSTANCE
        .get_or_init(|| {
            let manager = Arc::new(SearchManager::new());
            // Trigger initial indexing in background
            let manager_clone = manager.clone();
            std::thread::spawn(move || {
                if let Err(e) = manager_clone.reindex() {
                    eprintln!("Search index error: {}", e);
                }
            });
            manager
        })
        .clone()
}
