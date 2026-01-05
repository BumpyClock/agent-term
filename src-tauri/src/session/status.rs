use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use std::time::{Duration, Instant};

use super::model::{SessionStatus, SessionTool};

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
