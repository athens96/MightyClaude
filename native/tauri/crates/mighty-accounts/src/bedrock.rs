//! Claude's Bedrock configuration as the accounts read it, and its reset in the user's Claude settings (C# `BedrockSettings`,
//! BedrockSettings.cs). The guidance for a Bedrock 403 an organization's policy caused is mighty-run's
//! (`failure::bedrock_guidance`, `RuntimeFailureGuidance`).
//!
//! [`reset_user_settings`] writes `~/.claude/settings.json` (or `$CLAUDE_CONFIG_DIR`'s): an explicit user action only, which the
//! host allows only with `--allow-state-write` until W9 and never in a smoke.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::OnceLock;

use mighty_run::{os, strings};
use regex::Regex;
use serde_json::Value;

pub use mighty_run::failure::bedrock_guidance as runtime_failure_guidance;

use crate::env_get;

/// `MaximumSettingsBytes`.
pub const MAXIMUM_SETTINGS_BYTES: usize = 1_048_576;

/// `ConfigurationOnly`: what a Bedrock status says (configuration, not verified access).
pub fn configuration_only() -> String {
    strings::get("settings.cliAccounts.bedrockConfigurationOnly")
}

/// `ExternalConfigurationOnly`.
pub fn external_configuration_only() -> String {
    strings::get("settings.cliAccounts.externalConfigurationOnly")
}

/// `ConflictMessage`.
pub fn conflict_message() -> String {
    strings::get("settings.cliAccounts.bedrockConflict")
}

/// `SettingsPath`: `settings.json` in `$CLAUDE_CONFIG_DIR` (taken against `home` when relative), else in `~/.claude`.
pub fn settings_path(home: &str, environment: &BTreeMap<String, String>) -> String {
    let folder = match env_get(environment, "CLAUDE_CONFIG_DIR").filter(|dir| !dir.trim().is_empty()) {
        Some(dir) if crate::accounts::rooted(dir) => dir.to_string(),
        Some(dir) => os::combine(home, dir),
        None => os::combine(home, ".claude"),
    };
    os::combine(&folder, "settings.json")
}

/// `ConflictDetail`: the environment's Bedrock key and the settings' differ, said without either key.
pub fn conflict_detail(environment: &BTreeMap<String, String>, json: Option<&str>) -> Option<String> {
    let json = json?;
    let token = env_get(environment, "AWS_BEARER_TOKEN_BEDROCK").filter(|t| !t.trim().is_empty())?;
    if json.len() > MAXIMUM_SETTINGS_BYTES {
        return None;
    }
    let root: Value = serde_json::from_str(json).ok()?;
    let other = root.as_object()?.get("env").and_then(|env| mighty_run::meta::text(env, "AWS_BEARER_TOKEN_BEDROCK"))?;
    (!other.trim().is_empty() && token.trim() != other.trim()).then(conflict_message)
}

const BEDROCK_KEYS: [&str; 4] =
    ["AWS_BEARER_TOKEN_BEDROCK", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_SKIP_BEDROCK_AUTH", "ANTHROPIC_BEDROCK_BASE_URL"];
const MODEL_KEYS: [&str; 8] = [
    "ANTHROPIC_MODEL",
    "ANTHROPIC_SMALL_FAST_MODEL",
    "ANTHROPIC_DEFAULT_HAIKU_MODEL",
    "ANTHROPIC_DEFAULT_SONNET_MODEL",
    "ANTHROPIC_DEFAULT_OPUS_MODEL",
    "ANTHROPIC_DEFAULT_FABLE_MODEL",
    "ANTHROPIC_CUSTOM_MODEL_OPTION",
    "CLAUDE_CODE_SUBAGENT_MODEL",
];

/// `BedrockModel`: a Bedrock model id or ARN.
fn bedrock_model(value: Option<&Value>) -> bool {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    let pattern = PATTERN.get_or_init(|| Regex::new(r"^(?:(?:us|eu|apac|global)\.)?anthropic\.|^arn:aws[^:]*:bedrock:").expect("pattern"));
    value.map(node_text).is_some_and(|text| pattern.is_match(&text))
}

/// `JsonNode.ToString()`: a string's own text, anything else its JSON.
fn node_text(value: &Value) -> String {
    match value {
        Value::String(text) => text.clone(),
        other => other.to_string(),
    }
}

/// Why a reset was refused (C#'s `InvalidDataException` and `IOException` messages).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ResetError(pub String);

impl std::fmt::Display for ResetError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for ResetError {}

/// `ResetUserSettingsJson`: the settings without Bedrock — its four keys, the region once Bedrock was on, and the model pins that
/// name a Bedrock model; everything else (other AWS credentials, permissions, other models) stays. Written as
/// `JsonSerializer` writes indented JSON, with a final newline.
pub fn reset_user_settings_json(json: &str) -> Result<String, ResetError> {
    if json.len() > MAXIMUM_SETTINGS_BYTES {
        return Err(ResetError("Claude settings exceed the size limit.".into()));
    }
    let Ok(Value::Object(mut root)) = serde_json::from_str::<Value>(json) else {
        return Err(ResetError("Claude settings must be a JSON object.".into()));
    };
    match root.get_mut("env") {
        None | Some(Value::Null) => {}
        Some(Value::Object(env)) => {
            let enabled = env.get("CLAUDE_CODE_USE_BEDROCK").map(node_text).is_some_and(|value| value == "1" || value == "true");
            let doomed: Vec<String> = env
                .iter()
                .filter(|(key, value)| {
                    BEDROCK_KEYS.contains(&key.as_str())
                        || enabled && matches!(key.as_str(), "AWS_REGION" | "AWS_DEFAULT_REGION")
                        || MODEL_KEYS.contains(&key.as_str()) && bedrock_model(Some(value))
                })
                .map(|(key, _)| key.clone())
                .collect();
            for key in doomed {
                env.shift_remove(&key);
            }
        }
        Some(_) => return Err(ResetError("Claude settings env must be an object.".into())),
    }
    if bedrock_model(root.get("model")) {
        root.shift_remove("model");
    }
    let mut out = String::new();
    write_indented(&mut out, &Value::Object(root), 0);
    out.push('\n');
    Ok(out)
}

/// `JsonSerializer` with `WriteIndented`: two spaces, `"name": value`, `Environment.NewLine` between lines, text escaped as
/// `JavaScriptEncoder.Default` escapes it.
fn write_indented(out: &mut String, value: &Value, depth: usize) {
    let new_line = os::new_line();
    let indent = |out: &mut String, depth: usize| out.push_str(&"  ".repeat(depth));
    match value {
        Value::Null => out.push_str("null"),
        Value::Bool(b) => out.push_str(if *b { "true" } else { "false" }),
        Value::Number(n) => match (n.as_i64(), n.as_u64(), n.as_f64()) {
            (Some(i), _, _) => out.push_str(&i.to_string()),
            (_, Some(u), _) => out.push_str(&u.to_string()),
            (_, _, Some(f)) => out.push_str(&mighty_core::json::format_double(f)),
            _ => out.push_str(&n.to_string()),
        },
        Value::String(s) => mighty_core::json::escape_into(out, s),
        Value::Array(items) => {
            if items.is_empty() {
                out.push_str("[]");
                return;
            }
            out.push('[');
            for (i, item) in items.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                out.push_str(new_line);
                indent(out, depth + 1);
                write_indented(out, item, depth + 1);
            }
            out.push_str(new_line);
            indent(out, depth);
            out.push(']');
        }
        Value::Object(map) => {
            if map.is_empty() {
                out.push_str("{}");
                return;
            }
            out.push('{');
            for (i, (name, item)) in map.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                out.push_str(new_line);
                indent(out, depth + 1);
                mighty_core::json::escape_into(out, name);
                out.push_str(": ");
                write_indented(out, item, depth + 1);
            }
            out.push_str(new_line);
            indent(out, depth);
            out.push('}');
        }
    }
}

fn io(message: &str) -> ResetError {
    ResetError(message.into())
}

/// `ResetUserSettings`: Bedrock taken out of the user's Claude settings ([`reset_user_settings_json`]), the original kept beside
/// them as `settings.json.bedrock-backup-<id>`; the backup's path, or None when there is no file or nothing to change. A linked
/// settings folder or file, one past 1 MiB, or one changed while it was reset is refused. Keeps unrelated AWS credentials and never
/// touches project or managed settings or the OS credential store.
pub fn reset_user_settings(home: &str, environment: &BTreeMap<String, String>) -> Result<Option<PathBuf>, ResetError> {
    let path = PathBuf::from(settings_path(home, environment));
    if !path.is_file() {
        return Ok(None);
    }
    let mut parent = path.parent();
    while let Some(folder) = parent {
        if std::fs::symlink_metadata(folder).is_ok_and(|info| info.file_type().is_symlink()) {
            return Err(io("Refusing a linked Claude settings directory."));
        }
        parent = folder.parent();
    }
    let info = std::fs::symlink_metadata(&path).map_err(|e| ResetError(e.to_string()))?;
    if info.file_type().is_symlink() || info.len() > MAXIMUM_SETTINGS_BYTES as u64 {
        return Err(io("Claude settings are not a bounded regular file."));
    }
    let original = read_text(&path)?;
    let updated = reset_user_settings_json(&original)?;
    let same = serde_json::from_str::<Value>(&original).ok() == serde_json::from_str::<Value>(&updated).ok();
    if same {
        return Ok(None);
    }
    let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let backup = path.with_file_name(format!("{name}.bedrock-backup-{}", mighty_core::wire::new_id().replace('-', "")));
    let temporary = path.with_file_name(format!("{name}.{}.tmp", mighty_core::wire::new_id().replace('-', "")));
    let result = (|| {
        copy_new(&path, &backup)?;
        std::fs::write(&temporary, updated.as_bytes()).map_err(|e| ResetError(e.to_string()))?;
        if let Ok(permissions) = std::fs::metadata(&path).map(|m| m.permissions()) {
            let _ = std::fs::set_permissions(&temporary, permissions);
        }
        if read_text(&path)? != original {
            return Err(io("Claude settings changed during reset; retry after saving settings."));
        }
        std::fs::rename(&temporary, &path).map_err(|e| ResetError(e.to_string()))?;
        Ok(Some(backup))
    })();
    if temporary.exists() {
        let _ = std::fs::remove_file(&temporary);
    }
    result
}

/// `File.ReadAllText`: UTF-8, a byte order mark dropped.
fn read_text(path: &Path) -> Result<String, ResetError> {
    let bytes = std::fs::read(path).map_err(|e| ResetError(e.to_string()))?;
    let bytes = bytes.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(&bytes);
    Ok(String::from_utf8_lossy(bytes).into_owned())
}

/// `File.Copy(from, to, overwrite: false)`.
fn copy_new(from: &Path, to: &Path) -> Result<(), ResetError> {
    let bytes = std::fs::read(from).map_err(|e| ResetError(e.to_string()))?;
    let mut file = std::fs::OpenOptions::new().write(true).create_new(true).open(to).map_err(|e| ResetError(e.to_string()))?;
    std::io::Write::write_all(&mut file, &bytes).map_err(|e| ResetError(e.to_string()))
}
