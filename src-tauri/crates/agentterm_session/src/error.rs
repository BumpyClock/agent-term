use serde::Serialize;
use std::fmt;

#[derive(Debug, Clone, Serialize)]
#[serde(tag = "type", content = "message")]
pub enum SessionError {
    NotFound(String),
    AlreadyRunning(String),
    NotRunning(String),
    PtyError(String),
    IoError(String),
}

#[derive(Debug, Clone, Serialize)]
#[serde(tag = "type", content = "message")]
pub enum StorageError {
    ReadError(String),
    WriteError(String),
    ParseError(String),
    SerializeError(String),
}

#[derive(Debug, Clone, Serialize)]
#[serde(tag = "type", content = "message")]
pub enum AppError {
    Session(SessionError),
    Storage(StorageError),
    InvalidInput(String),
}

impl fmt::Display for SessionError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            SessionError::NotFound(id) => write!(f, "session not found: {}", id),
            SessionError::AlreadyRunning(id) => write!(f, "session already running: {}", id),
            SessionError::NotRunning(id) => write!(f, "session not running: {}", id),
            SessionError::PtyError(msg) => write!(f, "pty error: {}", msg),
            SessionError::IoError(msg) => write!(f, "io error: {}", msg),
        }
    }
}

impl fmt::Display for StorageError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            StorageError::ReadError(msg) => write!(f, "failed to read: {}", msg),
            StorageError::WriteError(msg) => write!(f, "failed to write: {}", msg),
            StorageError::ParseError(msg) => write!(f, "failed to parse: {}", msg),
            StorageError::SerializeError(msg) => write!(f, "failed to serialize: {}", msg),
        }
    }
}

impl fmt::Display for AppError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            AppError::Session(e) => write!(f, "{}", e),
            AppError::Storage(e) => write!(f, "{}", e),
            AppError::InvalidInput(msg) => write!(f, "invalid input: {}", msg),
        }
    }
}

impl std::error::Error for SessionError {}
impl std::error::Error for StorageError {}
impl std::error::Error for AppError {}

impl From<SessionError> for AppError {
    fn from(e: SessionError) -> Self {
        AppError::Session(e)
    }
}

impl From<StorageError> for AppError {
    fn from(e: StorageError) -> Self {
        AppError::Storage(e)
    }
}

impl From<std::io::Error> for StorageError {
    fn from(e: std::io::Error) -> Self {
        StorageError::ReadError(e.to_string())
    }
}

impl From<serde_json::Error> for StorageError {
    fn from(e: serde_json::Error) -> Self {
        StorageError::ParseError(e.to_string())
    }
}

pub type StorageResult<T> = Result<T, StorageError>;

impl From<AppError> for String {
    fn from(e: AppError) -> Self {
        e.to_string()
    }
}

impl From<SessionError> for String {
    fn from(e: SessionError) -> Self {
        e.to_string()
    }
}

impl From<StorageError> for String {
    fn from(e: StorageError) -> Self {
        e.to_string()
    }
}
