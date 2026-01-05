use super::model::{SessionStatus, SessionTool};
use super::status::{prompt_detector, status_tracker, PromptDetector, StatusTracker};

/// Runtime state for a live PTY-backed session.
///
/// Example:
/// ```rust,ignore
/// let runtime = session_runtime("session-1".to_string(), SessionTool::Shell);
/// ```
pub struct SessionRuntime {
    pub session_id: String,
    pub status: SessionStatus,
    detector: PromptDetector,
    tracker: StatusTracker,
}

pub fn session_runtime(session_id: String, tool: SessionTool) -> SessionRuntime {
    SessionRuntime {
        session_id,
        status: SessionStatus::Starting,
        detector: prompt_detector(tool),
        tracker: status_tracker(),
    }
}

impl SessionRuntime {
    pub fn ingest_output(&mut self, output: &str) -> SessionStatus {
        let has_prompt = self.detector.has_prompt(output);
        let next = self.tracker.update(output, has_prompt);
        self.status = next;
        next
    }

    pub fn acknowledge(&mut self) {
        self.tracker.acknowledge();
    }
}
