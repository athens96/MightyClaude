//! Parse-only reader for toolkit.json (C# `ToolkitFileReader`, ToolkitFileReader.cs): the entries, their install specs and approvals,
//! validated as the Mac's `ToolkitEntryDecoder` validates them. An entry that does not validate is skipped.
//!
//! C#'s patterns end in `$`, which .NET also matches before one final line feed (`abc\n` passes); the patterns here say so with
//! `\n?\z`.

use std::sync::OnceLock;

use regex::Regex;
use serde_json::Value;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolkitApproval {
    pub content_hash: String,
    pub resolved_commit: Option<String>,
}

/// Source of a toolkit entry: bundled with the app or added by the user.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ToolkitEntrySource {
    Bundled,
    User,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ToolkitInstallSpec {
    Plugin {
        source: String,
        plugin_id: String,
    },
    Mcp {
        name: String,
        executable: String,
        args: Vec<String>,
    },
    Skill {
        url: String,
    },
    /// `executable` is required for winget and absent for brew and npm.
    Package {
        manager: String,
        name: String,
        executable: Option<String>,
    },
    RepoScript {
        url: String,
        git_ref: String,
        script_path: String,
    },
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolkitFileEntry {
    pub id: String,
    pub display_name: String,
    pub install_kind: String,
    pub install: ToolkitInstallSpec,
    pub approval: Option<ToolkitApproval>,
    pub source: ToolkitEntrySource,
}

impl ToolkitFileEntry {
    pub fn new(id: &str, display_name: &str, install_kind: &str, install: ToolkitInstallSpec) -> ToolkitFileEntry {
        ToolkitFileEntry {
            id: id.into(),
            display_name: display_name.into(),
            install_kind: install_kind.into(),
            install,
            approval: None,
            source: ToolkitEntrySource::User,
        }
    }
}

/// Platform table (Swift `ToolkitInstallSpec.platforms`): plugin, mcp, skill and npm run on both; brew and repoScript on the Mac
/// only; winget on Windows only.
pub fn is_windows_platform(entry: &ToolkitFileEntry) -> bool {
    !is_macos_only(entry)
}

pub fn is_macos_only(entry: &ToolkitFileEntry) -> bool {
    entry.install_kind == "repoScript"
        || entry.install_kind == "package" && matches!(&entry.install, ToolkitInstallSpec::Package { manager, .. } if manager == "brew")
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolkitFile {
    pub entries: Vec<ToolkitFileEntry>,
}

/// Why toolkit.json did not parse (C# `InvalidDataException`, or the JSON reader's own error).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FileError(pub String);

impl std::fmt::Display for FileError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

/// C# `ToolkitFileReader.Parse`.
pub fn parse(json: &str) -> Result<ToolkitFile, FileError> {
    let root: Value = serde_json::from_str(json).map_err(|error| FileError(error.to_string()))?;
    let Some(root) = root.as_object() else { return Err(FileError("toolkit.json root must be an object".into())) };
    if root.get("version").and_then(Value::as_i64) != Some(1) {
        return Err(FileError("toolkit.json must have version:1".into()));
    }
    let Some(entries) = root.get("entries").and_then(Value::as_array) else {
        return Err(FileError("toolkit.json must have an 'entries' array".into()));
    };
    Ok(ToolkitFile { entries: entries.iter().filter(|item| item.is_object()).filter_map(parse_entry).collect() })
}

/// The one tool a file given to "Add" holds: a JSON object that is a tool entry, its approval dropped, nothing at its root but the
/// entry's own fields (a stale `platforms` is let through). None for anything else, a whole toolkit.json or an exported list among
/// them: Import reads those.
pub fn parse_added_entry(json: &str) -> Option<ToolkitFileEntry> {
    let root: Value = serde_json::from_str(json).ok()?;
    let object = root.as_object()?;
    if object.keys().any(|name| !matches!(name.as_str(), "id" | "displayName" | "install" | "platforms" | "approval")) {
        return None;
    }
    parse_entry(&root).map(|entry| ToolkitFileEntry { approval: None, ..entry })
}

/// C# `ToolkitFileReader.ParseEntry`.
pub fn parse_entry(item: &Value) -> Option<ToolkitFileEntry> {
    let id = item.get("id")?.as_str()?;
    if !valid_identifier(id) {
        return None;
    }
    let display_name = item.get("displayName")?.as_str()?;
    if !(1..=120).contains(&display_name.encode_utf16().count()) {
        return None;
    }
    let install = item.get("install").filter(|install| install.is_object())?;
    let kind = install.get("kind")?.as_str()?;
    let spec = match kind {
        "plugin" => parse_plugin(install),
        "mcp" => parse_mcp(install),
        "skill" => parse_skill(install),
        "package" => parse_package(install),
        "repoScript" => parse_repo_script(install),
        _ => None,
    }?;
    let approval = item.get("approval").filter(|approval| approval.is_object()).and_then(|approval| {
        let hash = approval.get("contentHash")?.as_str()?;
        let commit = approval.get("resolvedCommit").and_then(Value::as_str).map(str::to_string);
        (!hash.is_empty()).then(|| ToolkitApproval { content_hash: hash.to_string(), resolved_commit: commit })
    });
    Some(ToolkitFileEntry {
        id: id.to_string(),
        display_name: display_name.to_string(),
        install_kind: kind.to_string(),
        install: spec,
        approval,
        source: ToolkitEntrySource::User,
    })
}

fn text<'a>(value: &'a Value, key: &str) -> Option<&'a str> {
    value.get(key)?.as_str()
}

fn parse_plugin(el: &Value) -> Option<ToolkitInstallSpec> {
    let source = text(el, "source")?;
    let plugin_id = text(el, "pluginID")?;
    (valid_plugin_source(source) && valid_plugin_id(plugin_id))
        .then(|| ToolkitInstallSpec::Plugin { source: source.into(), plugin_id: plugin_id.into() })
}

fn parse_mcp(el: &Value) -> Option<ToolkitInstallSpec> {
    let name = text(el, "name")?;
    let executable = text(el, "executable")?;
    if !valid_identifier(name) || !valid_executable(executable) {
        return None;
    }
    let mut args = Vec::new();
    if let Some(Value::Array(items)) = el.get("args") {
        if items.len() > 64 {
            return None;
        }
        for arg in items {
            let arg = arg.as_str()?;
            if arg.contains('\0') {
                return None;
            }
            args.push(arg.to_string());
        }
    }
    Some(ToolkitInstallSpec::Mcp { name: name.into(), executable: executable.into(), args })
}

fn parse_skill(el: &Value) -> Option<ToolkitInstallSpec> {
    let url = text(el, "url")?;
    valid_https_url(url).then(|| ToolkitInstallSpec::Skill { url: url.into() })
}

fn parse_package(el: &Value) -> Option<ToolkitInstallSpec> {
    let manager = text(el, "manager")?;
    let name = text(el, "name")?;
    if manager == "winget" {
        // The executable is required for winget: a bare file name, no path.
        if !valid_winget_name(name) {
            return None;
        }
        let executable = text(el, "executable")?;
        if !valid_winget_executable(executable) {
            return None;
        }
        return Some(ToolkitInstallSpec::Package { manager: manager.into(), name: name.into(), executable: Some(executable.into()) });
    }
    // brew and npm: an executable field is rejected, as on macOS.
    if manager != "brew" && manager != "npm" || el.get("executable").is_some() {
        return None;
    }
    valid_package_name(name).then(|| ToolkitInstallSpec::Package { manager: manager.into(), name: name.into(), executable: None })
}

fn parse_repo_script(el: &Value) -> Option<ToolkitInstallSpec> {
    if el.get("arguments").is_some() {
        return None;
    }
    let url = text(el, "url")?;
    let git_ref = text(el, "ref")?;
    let script_path = text(el, "scriptPath")?;
    (valid_https_url(url) && valid_ref(git_ref) && valid_script_path(script_path)).then(|| ToolkitInstallSpec::RepoScript {
        url: url.into(),
        git_ref: git_ref.into(),
        script_path: script_path.into(),
    })
}

// Validation helpers (Swift `ToolkitEntryDecoder`).

struct Patterns {
    identifier: Regex,
    short_source: Regex,
    scoped_package: Regex,
    winget_name: Regex,
    commit: Regex,
}

fn patterns() -> &'static Patterns {
    static PATTERNS: OnceLock<Patterns> = OnceLock::new();
    PATTERNS.get_or_init(|| {
        let r = |pattern: &str| Regex::new(pattern).expect("a valid pattern");
        Patterns {
            identifier: r(r"^[A-Za-z0-9][A-Za-z0-9._\-]*\n?\z"),
            short_source: r(r"^[A-Za-z0-9][A-Za-z0-9._\-]*/[A-Za-z0-9][A-Za-z0-9._\-]*\n?\z"),
            scoped_package: r(r"^@[A-Za-z0-9][A-Za-z0-9._\-]*/[A-Za-z0-9][A-Za-z0-9._\-]*\n?\z"),
            winget_name: r(r"^[A-Za-z0-9][A-Za-z0-9._+\-]*\n?\z"),
            commit: r(r"^[0-9a-f]{40}\n?\z"),
        }
    })
}

fn valid_identifier(value: &str) -> bool {
    !value.is_empty() && value.len() <= 128 && patterns().identifier.is_match(value)
}

fn valid_plugin_source(value: &str) -> bool {
    if value.is_empty() || value.contains('\0') || value.len() > 2048 {
        return false;
    }
    patterns().short_source.is_match(value) || value.starts_with("https://")
}

fn valid_plugin_id(value: &str) -> bool {
    let parts: Vec<&str> = value.split('@').collect();
    parts.len() == 2 && valid_identifier(parts[0]) && valid_identifier(parts[1])
}

fn valid_executable(value: &str) -> bool {
    if value.is_empty() || value.contains('\0') || value.contains(' ') || value.len() > 4096 {
        return false;
    }
    if value.starts_with('/') {
        return !value.contains("..") && !value.contains(';') && !value.contains('|');
    }
    patterns().identifier.is_match(value)
}

fn valid_package_name(value: &str) -> bool {
    if value.is_empty() || value.len() > 128 {
        return false;
    }
    if value.starts_with('@') { patterns().scoped_package.is_match(value) } else { patterns().identifier.is_match(value) }
}

fn valid_winget_name(value: &str) -> bool {
    !value.is_empty() && value.len() <= 128 && patterns().winget_name.is_match(value)
}

fn valid_winget_executable(value: &str) -> bool {
    !value.is_empty() && value.len() <= 128 && patterns().identifier.is_match(value)
}

fn valid_https_url(value: &str) -> bool {
    value.starts_with("https://") && !value.contains('\0') && value.len() <= 2048
}

fn valid_ref(value: &str) -> bool {
    if value.is_empty() || value.len() > 128 {
        return false;
    }
    patterns().commit.is_match(value) || patterns().identifier.is_match(value)
}

fn valid_script_path(value: &str) -> bool {
    if value.is_empty() || value.starts_with('/') || value.contains('\0') || value.len() > 4096 {
        return false;
    }
    !value.split('/').any(|part| part == "..")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dotnet_dollar_takes_one_final_line_feed() {
        assert!(valid_identifier("abc\n"));
        assert!(!valid_identifier("abc\n\n"));
        assert!(!valid_identifier("a b"));
        assert!(valid_package_name("@scope/name"));
        assert!(!valid_package_name("@scope"));
    }
}
