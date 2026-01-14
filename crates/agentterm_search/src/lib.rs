//! Global Search module for indexing and searching Claude JSONL conversation logs.
//!
//! Provides in-memory indexing of conversation logs from `~/.claude/projects/*/`
//! and fuzzy search with snippet previews.

use parking_lot::Mutex;
use std::path::PathBuf;
use std::sync::Arc;

mod index;
mod query;

pub use index::{IndexStatus, IndexedMessage, MessageRef, SearchIndex};
pub use query::SearchResult;

use query::SearchEngine;

/// Configuration for the search index.
#[derive(Debug, Clone)]
pub struct SearchConfig {
    /// Number of days to look back when indexing (default: 90).
    pub recent_days: u32,
    /// Root directory for Claude logs (default: ~/.claude/projects).
    pub log_root: Option<String>,
    /// Path for storing index metadata (enables incremental indexing).
    pub metadata_path: Option<PathBuf>,
}

impl Default for SearchConfig {
    fn default() -> Self {
        let metadata_path =
            dirs::home_dir().map(|h| h.join(".agentterm").join("search").join("index_metadata.json"));
        Self {
            recent_days: 90,
            log_root: None,
            metadata_path,
        }
    }
}

/// Manages the search index and query operations.
pub struct SearchManager {
    index: Mutex<SearchIndex>,
    engine: SearchEngine,
    config: SearchConfig,
}

impl SearchManager {
    /// Create a new SearchManager with default configuration.
    pub fn new() -> Self {
        Self::with_config(SearchConfig::default())
    }

    /// Create a new SearchManager with custom configuration.
    pub fn with_config(config: SearchConfig) -> Self {
        let index = match &config.metadata_path {
            Some(path) => SearchIndex::with_metadata_path(path.clone()),
            None => SearchIndex::new(),
        };
        Self {
            index: Mutex::new(index),
            engine: SearchEngine::new(),
            config,
        }
    }

    /// Get the current indexing status.
    pub fn status(&self) -> IndexStatus {
        self.index.lock().status()
    }

    /// Trigger a full re-index of all JSONL logs.
    pub fn reindex(&self) -> Result<IndexStatus, String> {
        let log_root = self.config.log_root.clone().unwrap_or_else(|| {
            dirs::home_dir()
                .map(|h| h.join(".claude").join("projects").to_string_lossy().to_string())
                .unwrap_or_else(|| "~/.claude/projects".to_string())
        });

        let mut index = self.index.lock();
        index.reindex(&log_root, self.config.recent_days)?;
        Ok(index.status())
    }

    /// Search indexed messages with fuzzy matching.
    pub fn search(&self, query: &str, limit: usize) -> Vec<SearchResult> {
        let index = self.index.lock();
        self.engine.search(&index, query, limit)
    }

    /// Check if the index needs to be built (no messages indexed yet).
    pub fn needs_indexing(&self) -> bool {
        !self.index.lock().status().indexed
    }
}

impl Default for SearchManager {
    fn default() -> Self {
        Self::new()
    }
}

/// Build a new SearchManager for use in the app.
pub fn build_search_manager() -> Arc<SearchManager> {
    Arc::new(SearchManager::new())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fresh_manager() -> SearchManager {
        // Use config without persistence to avoid loading existing index
        SearchManager::with_config(SearchConfig {
            recent_days: 90,
            log_root: None,
            metadata_path: None,
        })
    }

    #[test]
    fn test_search_manager_creation() {
        let manager = fresh_manager();
        let status = manager.status();
        assert!(!status.indexed);
        assert_eq!(status.message_count, 0);
    }

    #[test]
    fn test_empty_query_returns_empty() {
        let manager = fresh_manager();
        let results = manager.search("", 10);
        assert!(results.is_empty());

        let results = manager.search("   ", 10);
        assert!(results.is_empty());
    }
}
