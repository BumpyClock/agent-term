//! JSONL log indexing logic.
//!
//! Scans `~/.claude/projects/*/` for JSONL conversation logs,
//! parses them to extract messages, and stores indexed content in memory.

use serde::{Deserialize, Serialize};
use std::fs;
use std::path::Path;
use std::time::SystemTime;

/// Represents an indexed message from a JSONL log.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct IndexedMessage {
    /// Path to the source JSONL file.
    pub file_path: String,
    /// Project name derived from directory.
    pub project_name: String,
    /// Message type: "user" or "assistant".
    pub message_type: String,
    /// ISO timestamp of the message.
    pub timestamp: Option<String>,
    /// The actual message content (text only).
    pub content: String,
    /// UUID of this message entry.
    pub uuid: Option<String>,
}

/// Status of the search index.
#[derive(Debug, Clone, Default)]
pub struct IndexStatus {
    /// Whether the index has been built.
    pub indexed: bool,
    /// Total number of indexed messages.
    pub message_count: usize,
    /// Number of JSONL files processed.
    pub file_count: usize,
    /// ISO timestamp of last indexing.
    pub last_indexed_at: Option<String>,
}

/// In-memory search index.
pub struct SearchIndex {
    messages: Vec<IndexedMessage>,
    status: IndexStatus,
}

impl SearchIndex {
    pub fn new() -> Self {
        Self {
            messages: Vec::new(),
            status: IndexStatus::default(),
        }
    }

    pub fn status(&self) -> IndexStatus {
        self.status.clone()
    }

    pub fn messages(&self) -> &[IndexedMessage] {
        &self.messages
    }

    /// Perform a full reindex of all JSONL logs.
    pub fn reindex(&mut self, log_root: &str, recent_days: u32) -> Result<(), String> {
        let root_path = Path::new(log_root);
        if !root_path.exists() {
            return Err(format!("Log root does not exist: {}", log_root));
        }

        let cutoff = SystemTime::now()
            .duration_since(SystemTime::UNIX_EPOCH)
            .map(|d| d.as_secs().saturating_sub(recent_days as u64 * 24 * 60 * 60))
            .unwrap_or(0);

        let mut messages = Vec::new();
        let mut file_count = 0;

        // Scan all project directories
        let entries = fs::read_dir(root_path)
            .map_err(|e| format!("Failed to read log root: {}", e))?;

        for entry in entries.flatten() {
            let project_path = entry.path();
            if !project_path.is_dir() {
                continue;
            }

            let project_name = project_path
                .file_name()
                .and_then(|n| n.to_str())
                .unwrap_or("unknown")
                .to_string();

            // Find all JSONL files in this project directory
            if let Ok(files) = fs::read_dir(&project_path) {
                for file_entry in files.flatten() {
                    let file_path = file_entry.path();
                    if !is_jsonl_file(&file_path) {
                        continue;
                    }

                    // Check file modification time against cutoff
                    if let Ok(metadata) = file_path.metadata() {
                        if let Ok(modified) = metadata.modified() {
                            if let Ok(duration) = modified.duration_since(SystemTime::UNIX_EPOCH) {
                                if duration.as_secs() < cutoff {
                                    continue;
                                }
                            }
                        }
                    }

                    // Parse the JSONL file
                    if let Ok(parsed) = parse_jsonl_file(&file_path, &project_name) {
                        messages.extend(parsed);
                        file_count += 1;
                    }
                }
            }
        }

        let message_count = messages.len();
        self.messages = messages;
        self.status = IndexStatus {
            indexed: true,
            message_count,
            file_count,
            last_indexed_at: Some(now_iso()),
        };

        Ok(())
    }
}

impl Default for SearchIndex {
    fn default() -> Self {
        Self::new()
    }
}

/// Check if a path is a JSONL file.
fn is_jsonl_file(path: &Path) -> bool {
    path.extension()
        .and_then(|e| e.to_str())
        .map(|e| e == "jsonl")
        .unwrap_or(false)
}

/// JSONL entry structure matching Claude's log format.
#[derive(Debug, Deserialize)]
struct JsonlEntry {
    #[serde(rename = "type")]
    entry_type: Option<String>,
    message: Option<JsonlMessage>,
    timestamp: Option<String>,
    uuid: Option<String>,
}

#[derive(Debug, Deserialize)]
struct JsonlMessage {
    role: Option<String>,
    content: Option<JsonlContent>,
}

#[derive(Debug, Deserialize)]
#[serde(untagged)]
enum JsonlContent {
    Text(String),
    Array(Vec<JsonlContentPart>),
}

#[derive(Debug, Deserialize)]
struct JsonlContentPart {
    #[serde(rename = "type")]
    part_type: Option<String>,
    text: Option<String>,
}

/// Parse a JSONL file and extract messages.
fn parse_jsonl_file(path: &Path, project_name: &str) -> Result<Vec<IndexedMessage>, String> {
    let content = fs::read_to_string(path)
        .map_err(|e| format!("Failed to read {}: {}", path.display(), e))?;

    let file_path = path.to_string_lossy().to_string();
    let mut messages = Vec::new();

    for line in content.lines() {
        if line.trim().is_empty() {
            continue;
        }

        // Parse the JSON line
        let entry: JsonlEntry = match serde_json::from_str(line) {
            Ok(e) => e,
            Err(_) => continue, // Skip malformed lines
        };

        // Extract message content
        let (message_type, content_text) = match (&entry.entry_type, &entry.message) {
            (Some(t), Some(msg)) if t == "user" || t == "assistant" => {
                let role = msg.role.as_deref().unwrap_or(t.as_str());
                let text = extract_content_text(&msg.content);
                if text.is_empty() {
                    continue;
                }
                (role.to_string(), text)
            }
            _ => continue,
        };

        messages.push(IndexedMessage {
            file_path: file_path.clone(),
            project_name: project_name.to_string(),
            message_type,
            timestamp: entry.timestamp,
            content: content_text,
            uuid: entry.uuid,
        });
    }

    Ok(messages)
}

/// Extract text content from JsonlContent.
fn extract_content_text(content: &Option<JsonlContent>) -> String {
    match content {
        Some(JsonlContent::Text(s)) => s.clone(),
        Some(JsonlContent::Array(parts)) => {
            parts
                .iter()
                .filter_map(|p| {
                    if p.part_type.as_deref() == Some("text") {
                        p.text.clone()
                    } else {
                        None
                    }
                })
                .collect::<Vec<_>>()
                .join("\n")
        }
        None => String::new(),
    }
}

/// Get current time as ISO 8601 string.
fn now_iso() -> String {
    time::OffsetDateTime::now_utc()
        .format(&time::format_description::well_known::Rfc3339)
        .unwrap_or_else(|_| String::new())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use std::path::PathBuf;
    use tempfile::TempDir;

    fn create_test_jsonl(dir: &Path, filename: &str, content: &str) -> PathBuf {
        let path = dir.join(filename);
        let mut file = fs::File::create(&path).unwrap();
        file.write_all(content.as_bytes()).unwrap();
        path
    }

    #[test]
    fn test_parse_jsonl_user_message() {
        let temp = TempDir::new().unwrap();
        let content = r#"{"type":"user","message":{"role":"user","content":"Hello world"},"timestamp":"2025-01-01T00:00:00Z","uuid":"abc123"}"#;
        let path = create_test_jsonl(temp.path(), "test.jsonl", content);

        let messages = parse_jsonl_file(&path, "test-project").unwrap();
        assert_eq!(messages.len(), 1);
        assert_eq!(messages[0].message_type, "user");
        assert_eq!(messages[0].content, "Hello world");
        assert_eq!(messages[0].project_name, "test-project");
    }

    #[test]
    fn test_parse_jsonl_array_content() {
        let temp = TempDir::new().unwrap();
        let content = r#"{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Part 1"},{"type":"text","text":"Part 2"}]},"timestamp":"2025-01-01T00:00:00Z"}"#;
        let path = create_test_jsonl(temp.path(), "test.jsonl", content);

        let messages = parse_jsonl_file(&path, "test-project").unwrap();
        assert_eq!(messages.len(), 1);
        assert_eq!(messages[0].message_type, "assistant");
        assert_eq!(messages[0].content, "Part 1\nPart 2");
    }

    #[test]
    fn test_parse_jsonl_skips_invalid_lines() {
        let temp = TempDir::new().unwrap();
        let content = r#"not valid json
{"type":"user","message":{"role":"user","content":"Valid"},"timestamp":"2025-01-01T00:00:00Z"}
{"type":"other","message":null}"#;
        let path = create_test_jsonl(temp.path(), "test.jsonl", content);

        let messages = parse_jsonl_file(&path, "test-project").unwrap();
        assert_eq!(messages.len(), 1);
        assert_eq!(messages[0].content, "Valid");
    }

    #[test]
    fn test_index_reindex() {
        let temp = TempDir::new().unwrap();
        let project_dir = temp.path().join("test-project");
        fs::create_dir(&project_dir).unwrap();

        let content = r#"{"type":"user","message":{"role":"user","content":"Test message"},"timestamp":"2025-01-01T00:00:00Z"}"#;
        create_test_jsonl(&project_dir, "conv.jsonl", content);

        let mut index = SearchIndex::new();
        index.reindex(temp.path().to_str().unwrap(), 90).unwrap();

        assert!(index.status().indexed);
        assert_eq!(index.status().message_count, 1);
        assert_eq!(index.status().file_count, 1);
    }

    #[test]
    fn test_is_jsonl_file() {
        assert!(is_jsonl_file(Path::new("test.jsonl")));
        assert!(!is_jsonl_file(Path::new("test.json")));
        assert!(!is_jsonl_file(Path::new("test.txt")));
        assert!(!is_jsonl_file(Path::new("test")));
    }
}
