//! A status line command found in Claude's settings (C# `StatusLineConfig`, `StatusLineDiscovery`, `StatusLineSupport.Discover`,
//! `ConfigDir`, `ReadEntry`, StatusLine.cs:167-394): both levels Claude consults, kept apart so a workspace command still waiting on
//! trust does not blank the status line while a user command exists.
//!
//! A settings file is read as C#'s `JsonDocument.Parse(bytes)` reads it: a byte-order mark or a file of another kind is no entry; a
//! `statusLine` key that is not a command entry disables its level; an entry whose fields are of a kind C# throws on reading is no
//! entry either.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use serde_json::Value;
use sha2::{Digest, Sha256};

use crate::status_line::strings::{self, SOURCE_USER, SOURCE_WORKSPACE, SOURCE_WORKSPACE_LOCAL};

/// C# `StatusLineConfig(Command, Padding, Source, FromWorkspace, OutputStyle?, ThinkingEnabled?)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StatusLineConfig {
    pub command: String,
    pub padding: i32,
    pub source: String,
    pub from_workspace: bool,
    pub output_style: Option<String>,
    pub thinking_enabled: Option<bool>,
}

impl StatusLineConfig {
    pub fn new(command: &str, padding: i32, source: &str, from_workspace: bool) -> StatusLineConfig {
        StatusLineConfig {
            command: command.into(),
            padding,
            source: source.into(),
            from_workspace,
            output_style: None,
            thinking_enabled: None,
        }
    }

    /// The source as the screen names it, in the app language; `source` itself feeds the fingerprint.
    pub fn source_label(&self) -> String {
        strings::source_label(&self.source)
    }

    /// SHA-256(source + LF + command) as 64 lower-case hex digits: the Mac's fingerprint.
    pub fn fingerprint(&self) -> String {
        Sha256::digest(format!("{}\n{}", self.source, self.command).as_bytes()).iter().map(|byte| format!("{byte:02x}")).collect()
    }
}

/// Both levels Claude consults (the Mac's `StatusLineConfig.Discovery`): `workspace` is the winning `.claude/settings.local.json` /
/// `settings.json` entry, `user` is `~/.claude/settings.json` (or `CLAUDE_CONFIG_DIR`).
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct StatusLineDiscovery {
    pub workspace: Option<StatusLineConfig>,
    pub user: Option<StatusLineConfig>,
    pub workspace_disabled: bool,
}

impl StatusLineDiscovery {
    pub fn new(workspace: Option<StatusLineConfig>, user: Option<StatusLineConfig>) -> StatusLineDiscovery {
        StatusLineDiscovery { workspace, user, workspace_disabled: false }
    }

    pub fn preferred(&self) -> Option<&StatusLineConfig> {
        if self.workspace_disabled { None } else { self.workspace.as_ref().or(self.user.as_ref()) }
    }
}

/// C# `Discover`: the workspace's local settings, then its settings (the first of the two with an entry wins, a disabling entry
/// included), and the user's settings from the config folder.
pub fn discover(workspace_path: Option<&Path>, home_dir: Option<&Path>, env: Option<&BTreeMap<String, String>>) -> StatusLineDiscovery {
    let mut workspace = None;
    let mut workspace_disabled = false;
    if let Some(folder) = workspace_path {
        for (path, source) in [
            (folder.join(".claude").join("settings.local.json"), SOURCE_WORKSPACE_LOCAL),
            (folder.join(".claude").join("settings.json"), SOURCE_WORKSPACE),
        ] {
            let (disabled, config) = read_entry(&path, true, source);
            if !disabled && config.is_none() {
                continue;
            }
            if disabled {
                workspace_disabled = true;
            } else {
                workspace = config;
            }
            break;
        }
    }
    let user = config_dir(home_dir, env).and_then(|dir| read_entry(&dir.join("settings.json"), false, SOURCE_USER).1);
    StatusLineDiscovery { workspace, user, workspace_disabled }
}

/// C# `ConfigDir`: `CLAUDE_CONFIG_DIR` when the given environment names it, else `<home>/.claude`.
pub fn config_dir(home_dir: Option<&Path>, env: Option<&BTreeMap<String, String>>) -> Option<PathBuf> {
    if let Some(dir) = env.and_then(|env| env.get("CLAUDE_CONFIG_DIR")).filter(|dir| !dir.is_empty()) {
        return Some(PathBuf::from(dir));
    }
    home_dir.map(|home| home.join(".claude"))
}

/// `(disabled, config)`: `(true, None)` when `statusLine` is there but no valid command entry (the Mac's `.some(nil)`), `(false,
/// None)` when it is absent or the file cannot be read as C# reads it.
fn read_entry(settings_path: &Path, from_workspace: bool, source: &str) -> (bool, Option<StatusLineConfig>) {
    let Ok(bytes) = std::fs::read(settings_path) else { return (false, None) };
    if bytes.len() > 4 * 1024 * 1024 {
        return (false, None);
    }
    // Utf8JsonReader takes no byte-order mark.
    let Ok(root) = serde_json::from_slice::<Value>(&bytes) else { return (false, None) };
    let Some(root) = root.as_object() else { return (false, None) };
    let Some(entry) = root.get("statusLine") else { return (false, None) };
    // TryGetProperty on anything but an object throws in C#, which reads as no entry.
    let Some(entry) = entry.as_object() else { return (false, None) };
    match entry.get("type") {
        None | Some(Value::Null) => return (true, None),
        Some(Value::String(kind)) if kind != "command" => return (true, None),
        Some(Value::String(_)) => {}
        Some(_) => return (false, None),
    }
    let command = match entry.get("command") {
        None | Some(Value::Null) => return (true, None),
        Some(Value::String(command)) => command.clone(),
        Some(_) => return (false, None),
    };
    if command.trim().is_empty() || command.len() > 4096 {
        return (true, None);
    }
    let padding = match entry.get("padding") {
        None => 0,
        Some(Value::Number(number)) => number.as_i64().and_then(|p| i32::try_from(p).ok()).map_or(0, |p| p.clamp(0, 8)),
        Some(_) => return (false, None),
    };
    let output_style = match root.get("outputStyle") {
        None | Some(Value::Null) => None,
        Some(Value::String(style)) => (!style.is_empty() && style.len() <= 80).then(|| style.clone()),
        Some(_) => return (false, None),
    };
    let thinking_enabled = root.get("alwaysThinkingEnabled").and_then(Value::as_bool);
    (false, Some(StatusLineConfig { command, padding, source: source.into(), from_workspace, output_style, thinking_enabled }))
}
