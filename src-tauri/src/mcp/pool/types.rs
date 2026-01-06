use serde::{Deserialize, Serialize};

/// Pool proxy lifecycle status.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ServerStatus {
    Stopped,
    Starting,
    Running,
    Failed,
}

impl ServerStatus {
}
