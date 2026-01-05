use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use std::time::{Duration, Instant};

use super::model::{SessionStatus, SessionTool};

/// Extracted session ID from tool output.
#[derive(Debug, Clone)]
pub enum ExtractedSessionId {
    Claude(String),
    Gemini(String),
}

/// Detects prompts for a given tool.
///
/// Example:
/// ```rust,ignore
/// let detector = prompt_detector(SessionTool::Claude);
/// let waiting = detector.has_prompt("\n> ");
/// ```
pub struct PromptDetector {
    tool: SessionTool,
}

/// Tracks status transitions for a session.
///
/// Example:
/// ```rust,ignore
/// let mut tracker = status_tracker();
/// let status = tracker.update("output", true);
/// ```
pub struct StatusTracker {
    last_hash: u64,
    last_change: Instant,
    acknowledged: bool,
    cooldown: Duration,
}

pub fn prompt_detector(tool: SessionTool) -> PromptDetector {
    PromptDetector { tool }
}

pub fn status_tracker() -> StatusTracker {
    StatusTracker {
        last_hash: 0,
        last_change: Instant::now(),
        acknowledged: false,
        cooldown: Duration::from_secs(2),
    }
}

impl PromptDetector {
    pub fn has_prompt(&self, content: &str) -> bool {
        let lower = content.to_lowercase();
        match &self.tool {
            SessionTool::Claude => has_prompt_for_claude(&lower, content),
            SessionTool::Gemini => lower.contains("gemini>") || has_line_ending_with(content, ">"),
            SessionTool::Codex => lower.contains("codex>") || lower.contains("continue?") || has_line_ending_with(content, ">"),
            SessionTool::OpenCode => lower.contains("ask anything") || lower.contains("open code") || has_line_ending_with(content, ">"),
            SessionTool::Shell | SessionTool::Custom(_) => has_line_ending_with(content, ">") || has_line_ending_with(content, "$"),
        }
    }
}

impl StatusTracker {
    pub fn acknowledge(&mut self) {
        self.acknowledged = true;
    }

    pub fn update(&mut self, content: &str, has_prompt: bool) -> SessionStatus {
        let current_hash = hash_content(content);
        if current_hash != self.last_hash {
            self.last_hash = current_hash;
            self.last_change = Instant::now();
            self.acknowledged = false;
            return SessionStatus::Running;
        }
        if self.last_change.elapsed() < self.cooldown {
            return SessionStatus::Running;
        }
        if has_prompt {
            return if self.acknowledged {
                SessionStatus::Idle
            } else {
                SessionStatus::Waiting
            };
        }
        SessionStatus::Idle
    }
}

fn hash_content(content: &str) -> u64 {
    let mut hasher = DefaultHasher::new();
    content.hash(&mut hasher);
    hasher.finish()
}

fn has_line_ending_with(content: &str, suffix: &str) -> bool {
    content
        .lines()
        .rev()
        .find(|line| !line.trim().is_empty())
        .map(|line| line.trim_end().ends_with(suffix))
        .unwrap_or(false)
}

fn has_prompt_for_claude(lower: &str, original: &str) -> bool {
    if lower.contains("esc to interrupt") {
        return false;
    }
    if has_line_ending_with(original, ">") {
        return true;
    }
    lower.contains("allow once") || lower.contains("allow always") || lower.contains("do you want")
}

/// Extract session ID from tool output.
/// Claude Code shows session ID in startup output, typically in the format:
/// "Session: /path/to/.claude/projects/.../uuid" or "Resuming session uuid"
pub fn extract_session_id(tool: &SessionTool, content: &str) -> Option<ExtractedSessionId> {
    match tool {
        SessionTool::Claude => extract_claude_session_id(content),
        SessionTool::Gemini => extract_gemini_session_id(content),
        _ => None,
    }
}

fn extract_claude_session_id(content: &str) -> Option<ExtractedSessionId> {
    // Claude Code typically shows conversation ID like:
    // "Resuming session abc123..." or in the status bar
    // The session ID is a UUID or path-based identifier
    for line in content.lines() {
        let line_lower = line.to_lowercase();

        // Look for "session:" or "conversation:" patterns
        if line_lower.contains("session:") || line_lower.contains("conversation:") {
            // Extract the ID part after the colon
            if let Some(pos) = line.find(':') {
                let id_part = line[pos + 1..].trim();
                if !id_part.is_empty() && id_part.len() >= 8 {
                    return Some(ExtractedSessionId::Claude(id_part.to_string()));
                }
            }
        }

        // Look for UUID patterns (8-4-4-4-12 format)
        if line_lower.contains("resuming") || line_lower.contains("continuing") {
            if let Some(uuid) = extract_uuid_from_line(line) {
                return Some(ExtractedSessionId::Claude(uuid));
            }
        }
    }
    None
}

fn extract_gemini_session_id(content: &str) -> Option<ExtractedSessionId> {
    // Gemini CLI may show session ID in startup
    for line in content.lines() {
        let line_lower = line.to_lowercase();
        if line_lower.contains("session") || line_lower.contains("id:") {
            if let Some(uuid) = extract_uuid_from_line(line) {
                return Some(ExtractedSessionId::Gemini(uuid));
            }
        }
    }
    None
}

fn extract_uuid_from_line(line: &str) -> Option<String> {
    // UUID pattern: 8-4-4-4-12 hex digits
    let hex_chars: Vec<char> = "0123456789abcdefABCDEF-".chars().collect();

    for word in line.split_whitespace() {
        let clean: String = word.chars().filter(|c| hex_chars.contains(c)).collect();
        // Check if it looks like a UUID (at least 32 hex chars with dashes)
        if clean.len() >= 32 && clean.contains('-') {
            let parts: Vec<&str> = clean.split('-').collect();
            if parts.len() >= 4 {
                return Some(clean);
            }
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_shell_prompt_detection() {
        let detector = prompt_detector(SessionTool::Shell);

        // Should detect shell prompts ending with $ or >
        assert!(detector.has_prompt("user@host:~$ "));
        assert!(detector.has_prompt("bash-5.0$ "));
        assert!(detector.has_prompt("PS C:\\Users> "));

        // Should not detect mid-output
        assert!(!detector.has_prompt("Running command..."));
        assert!(!detector.has_prompt("Processing data"));
    }

    #[test]
    fn test_claude_prompt_detection() {
        let detector = prompt_detector(SessionTool::Claude);

        // Should detect Claude prompts
        assert!(detector.has_prompt("claude> "));
        assert!(detector.has_prompt("Some output\n> "));
        assert!(detector.has_prompt("Do you want to allow this?"));
        assert!(detector.has_prompt("Allow once or Allow always"));

        // Should not detect when running (esc to interrupt shown)
        assert!(!detector.has_prompt("Processing... (esc to interrupt)"));
    }

    #[test]
    fn test_gemini_prompt_detection() {
        let detector = prompt_detector(SessionTool::Gemini);

        assert!(detector.has_prompt("gemini> "));
        assert!(detector.has_prompt("Output\n> "));
    }

    #[test]
    fn test_status_tracker_running_on_new_content() {
        let mut tracker = status_tracker();

        // New content should return Running
        let status = tracker.update("some output", false);
        assert_eq!(status, SessionStatus::Running);

        // Same content should still be Running within cooldown
        let status = tracker.update("some output", false);
        assert_eq!(status, SessionStatus::Running);
    }

    #[test]
    fn test_status_tracker_transitions() {
        let mut tracker = status_tracker();

        // Start with some output
        let status = tracker.update("initial", false);
        assert_eq!(status, SessionStatus::Running);

        // New content keeps Running
        let status = tracker.update("more output", false);
        assert_eq!(status, SessionStatus::Running);

        // Different content is still Running
        let status = tracker.update("different content", true);
        assert_eq!(status, SessionStatus::Running);
    }

    #[test]
    fn test_has_line_ending_with() {
        assert!(has_line_ending_with("user@host:~$ ", "$"));
        assert!(has_line_ending_with("line1\nuser@host:~$ ", "$"));
        assert!(has_line_ending_with("PS C:\\>", ">"));

        assert!(!has_line_ending_with("running command", "$"));
        assert!(!has_line_ending_with("", "$"));
    }

    #[test]
    fn test_extract_session_id_claude() {
        let content = "Session: abc-123-def-456-ghi";
        let result = extract_session_id(&SessionTool::Claude, content);
        assert!(result.is_some());

        if let Some(ExtractedSessionId::Claude(id)) = result {
            assert!(id.contains("abc"));
        }
    }

    #[test]
    fn test_extract_uuid_from_line() {
        // Valid UUID format
        let uuid = extract_uuid_from_line("Session 550e8400-e29b-41d4-a716-446655440000");
        assert!(uuid.is_some());

        // No UUID
        let no_uuid = extract_uuid_from_line("Just some regular text");
        assert!(no_uuid.is_none());
    }
}
