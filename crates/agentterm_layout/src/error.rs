use serde::Serialize;
use std::fmt;

/// Error types for layout storage operations.
#[derive(Debug, Clone, Serialize)]
#[serde(tag = "type", content = "message")]
pub enum LayoutError {
    ReadError(String),
    WriteError(String),
    ParseError(String),
    SerializeError(String),
    NotFound(String),
}

impl fmt::Display for LayoutError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            LayoutError::ReadError(msg) => write!(f, "failed to read layout: {}", msg),
            LayoutError::WriteError(msg) => write!(f, "failed to write layout: {}", msg),
            LayoutError::ParseError(msg) => write!(f, "failed to parse layout: {}", msg),
            LayoutError::SerializeError(msg) => write!(f, "failed to serialize layout: {}", msg),
            LayoutError::NotFound(msg) => write!(f, "layout not found: {}", msg),
        }
    }
}

impl std::error::Error for LayoutError {}

impl From<std::io::Error> for LayoutError {
    fn from(e: std::io::Error) -> Self {
        LayoutError::ReadError(e.to_string())
    }
}

impl From<serde_json::Error> for LayoutError {
    fn from(e: serde_json::Error) -> Self {
        LayoutError::ParseError(e.to_string())
    }
}

impl From<LayoutError> for String {
    fn from(e: LayoutError) -> Self {
        e.to_string()
    }
}

pub type LayoutResult<T> = Result<T, LayoutError>;
