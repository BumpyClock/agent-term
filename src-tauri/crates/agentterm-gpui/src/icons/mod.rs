//! Icon system for agentterm-gpui.
//!
//! Provides Icon component with support for:
//! - Embedded Lucide SVG icons (via rust-embed assets)
//! - Runtime-loaded tool icons from disk

mod lucide_icons;
mod tool_icons;

pub use lucide_icons::*;
pub use tool_icons::*;

use gpui::{prelude::*, img, px, svg, Hsla, IntoElement, SharedString, Styled, Window, App};
use std::path::PathBuf;

/// Common UI icons with type safety (subset of Lucide).
/// These are the most frequently used icons with compile-time safety.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IconName {
    Terminal,
    Code,
    Sparkles,
    Bot,
    ChevronDown,
    ChevronRight,
    ChevronUp,
    ChevronLeft,
    Plus,
    X,
    Check,
    Settings,
    Folder,
    FolderOpen,
    File,
    Star,
    Zap,
    Cpu,
    Search,
    MoreHorizontal,
    MoreVertical,
    Edit,
    Trash,
    GripVertical,
    Play,
    Pause,
    Square,
    Circle,
    RefreshCw,
    Download,
    Upload,
    Copy,
    Clipboard,
    ExternalLink,
    Link,
    Unlink,
    Eye,
    EyeOff,
    Lock,
    Unlock,
    User,
    Users,
    Home,
    Menu,
    ArrowLeft,
    ArrowRight,
    ArrowUp,
    ArrowDown,
}

impl IconName {
    /// Returns the asset path for this icon.
    pub fn path(&self) -> &'static str {
        match self {
            Self::Terminal => "icons/terminal.svg",
            Self::Code => "icons/code.svg",
            Self::Sparkles => "icons/sparkles.svg",
            Self::Bot => "icons/bot.svg",
            Self::ChevronDown => "icons/chevron-down.svg",
            Self::ChevronRight => "icons/chevron-right.svg",
            Self::ChevronUp => "icons/chevron-up.svg",
            Self::ChevronLeft => "icons/chevron-left.svg",
            Self::Plus => "icons/plus.svg",
            Self::X => "icons/x.svg",
            Self::Check => "icons/check.svg",
            Self::Settings => "icons/settings.svg",
            Self::Folder => "icons/folder.svg",
            Self::FolderOpen => "icons/folder-open.svg",
            Self::File => "icons/file.svg",
            Self::Star => "icons/star.svg",
            Self::Zap => "icons/zap.svg",
            Self::Cpu => "icons/cpu.svg",
            Self::Search => "icons/search.svg",
            Self::MoreHorizontal => "icons/more-horizontal.svg",
            Self::MoreVertical => "icons/more-vertical.svg",
            Self::Edit => "icons/edit.svg",
            Self::Trash => "icons/trash.svg",
            Self::GripVertical => "icons/grip-vertical.svg",
            Self::Play => "icons/play.svg",
            Self::Pause => "icons/pause.svg",
            Self::Square => "icons/square.svg",
            Self::Circle => "icons/circle.svg",
            Self::RefreshCw => "icons/refresh-cw.svg",
            Self::Download => "icons/download.svg",
            Self::Upload => "icons/upload.svg",
            Self::Copy => "icons/copy.svg",
            Self::Clipboard => "icons/clipboard.svg",
            Self::ExternalLink => "icons/external-link.svg",
            Self::Link => "icons/link.svg",
            Self::Unlink => "icons/unlink.svg",
            Self::Eye => "icons/eye.svg",
            Self::EyeOff => "icons/eye-off.svg",
            Self::Lock => "icons/lock.svg",
            Self::Unlock => "icons/unlock.svg",
            Self::User => "icons/user.svg",
            Self::Users => "icons/users.svg",
            Self::Home => "icons/home.svg",
            Self::Menu => "icons/menu.svg",
            Self::ArrowLeft => "icons/arrow-left.svg",
            Self::ArrowRight => "icons/arrow-right.svg",
            Self::ArrowUp => "icons/arrow-up.svg",
            Self::ArrowDown => "icons/arrow-down.svg",
        }
    }
}

/// Standard icon sizes.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub enum IconSize {
    /// 12px - for indicators
    XSmall,
    /// 14px - for inline icons
    Small,
    /// 16px - default size
    #[default]
    Medium,
    /// 20px - for buttons
    Large,
    /// 24px - for headers
    XLarge,
}

impl IconSize {
    /// Returns the size in pixels.
    pub fn px(&self) -> gpui::Pixels {
        match self {
            Self::XSmall => px(12.),
            Self::Small => px(14.),
            Self::Medium => px(16.),
            Self::Large => px(20.),
            Self::XLarge => px(24.),
        }
    }
}

/// Icon source - embedded or external.
enum IconSource {
    /// Lucide SVG from embedded assets
    Embedded(SharedString),
    /// Tool icon loaded from disk at runtime
    External(PathBuf),
}

/// Icon component for rendering Lucide or tool icons.
#[derive(IntoElement)]
pub struct Icon {
    source: IconSource,
    size: IconSize,
    color: Option<Hsla>,
}

impl Icon {
    /// Create an icon from the IconName enum (embedded Lucide).
    pub fn new(name: IconName) -> Self {
        Self {
            source: IconSource::Embedded(name.path().into()),
            size: IconSize::default(),
            color: None,
        }
    }

    /// Create an icon from a Lucide icon name string (embedded).
    /// Use this for dynamic icon selection, e.g., from user input.
    pub fn lucide(name: &str) -> Self {
        Self {
            source: IconSource::Embedded(format!("icons/{}.svg", name).into()),
            size: IconSize::default(),
            color: None,
        }
    }

    /// Create an icon from an external file path (runtime loaded).
    /// Use this for tool icons that are loaded from disk.
    pub fn from_file(path: impl Into<PathBuf>) -> Self {
        Self {
            source: IconSource::External(path.into()),
            size: IconSize::default(),
            color: None,
        }
    }

    /// Set the icon size.
    pub fn size(mut self, size: IconSize) -> Self {
        self.size = size;
        self
    }

    /// Set the icon color.
    /// Only applies to embedded SVG icons (Lucide).
    pub fn color(mut self, color: impl Into<Hsla>) -> Self {
        self.color = Some(color.into());
        self
    }
}

impl RenderOnce for Icon {
    fn render(self, _window: &mut Window, _cx: &mut App) -> impl IntoElement {
        let size = self.size.px();

        match self.source {
            IconSource::Embedded(path) => {
                let mut el = svg().path(path).size(size).flex_none();
                if let Some(color) = self.color {
                    el = el.text_color(color);
                }
                el.into_any_element()
            }
            IconSource::External(path) => {
                img(path).size(size).flex_none().into_any_element()
            }
        }
    }
}

/// Describes an icon for serialization/storage.
/// Used to persist icon choices in settings/sessions.
#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
#[serde(tag = "kind")]
pub enum IconDescriptor {
    /// A Lucide icon by name
    #[serde(rename = "lucide")]
    Lucide { id: String },
    /// A tool icon by ID
    #[serde(rename = "tool")]
    Tool { id: String },
}

impl IconDescriptor {
    /// Create a Lucide icon descriptor.
    pub fn lucide(name: impl Into<String>) -> Self {
        Self::Lucide { id: name.into() }
    }

    /// Create a tool icon descriptor.
    pub fn tool(id: impl Into<String>) -> Self {
        Self::Tool { id: id.into() }
    }

    /// Convert this descriptor to an Icon element.
    pub fn to_icon(&self) -> Icon {
        match self {
            Self::Lucide { id } => Icon::lucide(id),
            Self::Tool { id } => {
                if let Some(info) = find_tool_icon(id) {
                    Icon::from_file(tool_icon_path(info.filename))
                } else {
                    Icon::new(IconName::File)
                }
            }
        }
    }
}

/// Parse an icon string into an Icon element.
///
/// Supports formats:
/// - "lucide:terminal" -> Lucide icon
/// - "claude" -> Tool icon by ID
/// - Any other string -> Try as tool icon, fallback to File icon
pub fn icon_from_string(icon_str: &str) -> Icon {
    if icon_str.starts_with("lucide:") {
        Icon::lucide(&icon_str[7..])
    } else if let Some(info) = find_tool_icon(icon_str) {
        Icon::from_file(tool_icon_path(info.filename))
    } else {
        let path = tool_icon_path(icon_str);
        if path.exists() {
            Icon::from_file(path)
        } else {
            Icon::new(IconName::File)
        }
    }
}
