//! Fuzzy search and snippet extraction.
//!
//! Provides search functionality over indexed messages with
//! snippet previews showing context around matches.
//! Content is loaded on-demand from source files.

use super::index::{IndexedMessage, MessageRef, SearchIndex};
use serde::{Deserialize, Serialize};

/// A search result with snippet and metadata.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SearchResult {
    /// Path to the source JSONL file.
    pub file_path: String,
    /// Project name derived from directory.
    pub project_name: String,
    /// Message type: "user" or "assistant".
    pub message_type: String,
    /// ISO timestamp of the message.
    pub timestamp: Option<String>,
    /// Snippet with context around the match.
    pub snippet: String,
    /// Positions of matches in the snippet (start, end).
    pub match_positions: Vec<(usize, usize)>,
    /// Relevance score (higher is better).
    pub score: f32,
}

/// A single match within content.
#[derive(Debug, Clone)]
pub struct SnippetMatch {
    /// Start position in original content.
    pub start: usize,
    /// End position in original content.
    pub end: usize,
}

/// Intermediate result for scoring (owns data for on-demand content loading).
struct ScoredCandidate {
    msg_ref: MessageRef,
    content: String,
    matches: Vec<SnippetMatch>,
    score: f32,
}

/// Search engine for querying indexed messages.
pub struct SearchEngine {
    /// Context characters to show before/after match.
    context_chars: usize,
    /// Maximum snippet length.
    max_snippet_len: usize,
}

impl SearchEngine {
    pub fn new() -> Self {
        Self {
            context_chars: 100,
            max_snippet_len: 300,
        }
    }

    /// Search indexed messages using inverted index for efficient lookup.
    ///
    /// Uses case-insensitive term matching with OR semantics.
    /// Returns results sorted by relevance score.
    ///
    /// Content is loaded on-demand from source files for scoring and snippets.
    pub fn search(&self, index: &SearchIndex, query: &str, limit: usize) -> Vec<SearchResult> {
        if query.trim().is_empty() {
            return vec![];
        }

        let query_lower = query.to_lowercase();
        let query_terms: Vec<&str> = query_lower.split_whitespace().collect();

        if query_terms.is_empty() {
            return vec![];
        }

        let candidate_indices = self.get_candidates(index, &query_terms);

        let mut scored: Vec<ScoredCandidate> = candidate_indices
            .iter()
            .filter_map(|&idx| {
                let msg_ref = index.get_message_ref(idx)?;
                let content = index.load_content(msg_ref).ok()?;
                self.score_with_content(msg_ref.clone(), content, &query_terms)
            })
            .collect();

        scored.sort_by(|a, b| b.score.partial_cmp(&a.score).unwrap_or(std::cmp::Ordering::Equal));
        scored.truncate(limit);

        scored.into_iter().map(|c| self.build_result(c)).collect()
    }

    /// Get candidate message indices from inverted index using OR semantics.
    fn get_candidates(&self, index: &SearchIndex, query_terms: &[&str]) -> Vec<usize> {
        use std::collections::HashSet;

        let mut all: HashSet<usize> = HashSet::new();
        for term in query_terms {
            if let Some(indices) = index.get_term_indices(term) {
                all.extend(indices.iter().copied());
            }
        }
        all.into_iter().collect()
    }

    /// Score a message with content and build candidate for ranking.
    fn score_with_content(
        &self,
        msg_ref: MessageRef,
        content: String,
        query_terms: &[&str],
    ) -> Option<ScoredCandidate> {
        let content_lower = content.to_lowercase();

        let mut all_matches: Vec<SnippetMatch> = Vec::new();
        let mut term_match_count = 0;

        for term in query_terms {
            let term_matches: Vec<SnippetMatch> = content_lower
                .match_indices(term)
                .map(|(start, matched)| SnippetMatch {
                    start,
                    end: start + matched.len(),
                })
                .collect();

            if !term_matches.is_empty() {
                term_match_count += 1;
                all_matches.extend(term_matches);
            }
        }

        if all_matches.is_empty() {
            return None;
        }

        let term_coverage = term_match_count as f32 / query_terms.len() as f32;
        let match_density = (all_matches.len() as f32).min(10.0) / 10.0;
        let length_factor = 1.0 / (1.0 + (content.len() as f32 / 1000.0).ln());
        let score = term_coverage * 0.6 + match_density * 0.3 + length_factor * 0.1;

        Some(ScoredCandidate {
            msg_ref,
            content,
            matches: all_matches,
            score,
        })
    }

    /// Build final result from scored candidate.
    fn build_result(&self, candidate: ScoredCandidate) -> SearchResult {
        let (snippet, match_positions) =
            self.build_snippet(&candidate.content, &candidate.matches);

        SearchResult {
            file_path: candidate.msg_ref.file_path,
            project_name: candidate.msg_ref.project_name,
            message_type: candidate.msg_ref.message_type,
            timestamp: candidate.msg_ref.timestamp,
            snippet,
            match_positions,
            score: candidate.score,
        }
    }

    /// Score a message against query terms (used by tests).
    #[cfg(test)]
    fn score_message(&self, msg: &IndexedMessage, query_terms: &[&str]) -> Option<SearchResult> {
        let msg_ref = MessageRef {
            file_path: msg.file_path.clone(),
            project_name: msg.project_name.clone(),
            message_type: msg.message_type.clone(),
            timestamp: msg.timestamp.clone(),
            uuid: msg.uuid.clone(),
            file_offset: 0,
            line_length: 0,
        };
        self.score_with_content(msg_ref, msg.content.clone(), query_terms)
            .map(|candidate| self.build_result(candidate))
    }

    /// Build a snippet with context around matches.
    fn build_snippet(
        &self,
        content: &str,
        matches: &[SnippetMatch],
    ) -> (String, Vec<(usize, usize)>) {
        if matches.is_empty() {
            return (String::new(), vec![]);
        }

        // Sort matches by position
        let mut sorted_matches = matches.to_vec();
        sorted_matches.sort_by_key(|m| m.start);

        // Use the first match to center the snippet
        let first_match = &sorted_matches[0];

        // Calculate snippet bounds
        let snippet_start = first_match.start.saturating_sub(self.context_chars);
        let snippet_end = (first_match.end + self.context_chars).min(content.len());

        // Adjust to character boundaries
        let snippet_start = content
            .char_indices()
            .take_while(|(i, _)| *i < snippet_start)
            .last()
            .map(|(i, _)| i)
            .unwrap_or(0);

        let snippet_end = content
            .char_indices()
            .skip_while(|(i, _)| *i < snippet_end)
            .next()
            .map(|(i, _)| i)
            .unwrap_or(content.len());

        // Extract snippet
        let snippet = &content[snippet_start..snippet_end];

        // Truncate if too long
        let (final_snippet, truncated_end) = if snippet.len() > self.max_snippet_len {
            let end = content[snippet_start..]
                .char_indices()
                .take_while(|(i, _)| *i < self.max_snippet_len)
                .last()
                .map(|(i, c)| i + c.len_utf8())
                .unwrap_or(self.max_snippet_len);
            (&snippet[..end], snippet_start + end)
        } else {
            (snippet, snippet_end)
        };

        // Add ellipsis if truncated
        let mut result = String::new();
        if snippet_start > 0 {
            result.push_str("...");
        }
        result.push_str(final_snippet);
        if truncated_end < content.len() {
            result.push_str("...");
        }

        // Calculate match positions relative to snippet
        let prefix_len = if snippet_start > 0 { 3 } else { 0 };
        let match_positions: Vec<(usize, usize)> = sorted_matches
            .iter()
            .filter(|m| m.start >= snippet_start && m.end <= truncated_end)
            .map(|m| {
                (
                    m.start - snippet_start + prefix_len,
                    m.end - snippet_start + prefix_len,
                )
            })
            .collect();

        (result, match_positions)
    }
}

impl Default for SearchEngine {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn create_test_message(content: &str) -> IndexedMessage {
        IndexedMessage {
            file_path: "/test/path.jsonl".to_string(),
            project_name: "test-project".to_string(),
            message_type: "user".to_string(),
            timestamp: Some("2025-01-01T00:00:00Z".to_string()),
            content: content.to_string(),
            uuid: None,
        }
    }

    #[test]
    fn test_search_single_term() {
        let msg = create_test_message("Hello world, this is a test message");

        let engine = SearchEngine::new();
        let result = engine.score_message(&msg, &["hello"]);

        assert!(result.is_some());
        let result = result.unwrap();
        assert!(result.score > 0.0);
        assert!(result.snippet.to_lowercase().contains("hello"));
    }

    #[test]
    fn test_search_multiple_terms() {
        let msg = create_test_message("The quick brown fox jumps over the lazy dog");
        let engine = SearchEngine::new();

        // Two matching terms
        let result = engine.score_message(&msg, &["quick", "fox"]);
        assert!(result.is_some());
        let two_term_score = result.unwrap().score;

        // One matching term
        let result = engine.score_message(&msg, &["quick"]);
        let one_term_score = result.unwrap().score;

        // Two terms should score higher
        assert!(two_term_score > one_term_score);
    }

    #[test]
    fn test_search_no_match() {
        let msg = create_test_message("Hello world");
        let engine = SearchEngine::new();

        let result = engine.score_message(&msg, &["xyz123"]);
        assert!(result.is_none());
    }

    #[test]
    fn test_snippet_with_context() {
        let content = "Start of message. The important keyword is here. End of message.";
        let matches = vec![SnippetMatch { start: 22, end: 31 }]; // "important"

        let engine = SearchEngine::new();
        let (snippet, positions) = engine.build_snippet(content, &matches);

        assert!(snippet.contains("important"));
        assert!(!positions.is_empty());
    }

    #[test]
    fn test_snippet_truncation() {
        let content = "A".repeat(1000);
        let matches = vec![SnippetMatch { start: 0, end: 1 }];

        let engine = SearchEngine::new();
        let (snippet, _) = engine.build_snippet(&content, &matches);

        assert!(snippet.len() <= 310); // max_snippet_len + ellipsis
    }

    #[test]
    fn test_case_insensitive_search() {
        let msg = create_test_message("HELLO World");
        let engine = SearchEngine::new();

        // Note: score_message expects pre-lowercased terms (as search() does)
        let result = engine.score_message(&msg, &["hello"]);
        assert!(result.is_some());

        let result = engine.score_message(&msg, &["world"]);
        assert!(result.is_some());
    }
}
