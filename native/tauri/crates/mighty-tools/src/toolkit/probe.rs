//! Machine-wide, presence-only detection for toolkit entries (C# `ToolkitProbe`, `ToolkitProbeContext`, ToolkitProbe.cs): files and
//! paths only, no process launch, no handshake. The paths are injected, so the checks use temporary folders and a smoke run reads
//! only its own profile.

use std::path::{Path, PathBuf};

use serde_json::Value;

use crate::toolkit::file::{ToolkitApproval, ToolkitFileEntry, ToolkitInstallSpec};

/// C# `ToolkitProbeContext`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolkitProbeContext {
    pub home_directory: PathBuf,
    pub path_directories: Vec<String>,
    pub local_app_data: PathBuf,
}

impl ToolkitProbeContext {
    /// C# `LiveProbeContext`: the user's home, this process's PATH (empty parts dropped), `%LOCALAPPDATA%`.
    pub fn live(home: PathBuf, path: &str, local_app_data: PathBuf, separator: char) -> ToolkitProbeContext {
        ToolkitProbeContext {
            home_directory: home,
            path_directories: path.split(separator).filter(|part| !part.is_empty()).map(str::to_string).collect(),
            local_app_data,
        }
    }

    /// A smoke run never looks at the user's own tools: its probe reads the isolated profile only.
    pub fn smoke(state_directory: &Path) -> ToolkitProbeContext {
        ToolkitProbeContext { home_directory: state_directory.into(), path_directories: vec![], local_app_data: state_directory.into() }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProbeResult {
    Installed,
    Missing,
}

/// C# `ToolkitProbe.Probe`. A skill whose address does not read as a URL counts as missing (the C# box catches the same
/// `UriFormatException`).
pub fn probe(entry: &ToolkitFileEntry, _approval: Option<&ToolkitApproval>, ctx: &ToolkitProbeContext) -> ProbeResult {
    match &entry.install {
        ToolkitInstallSpec::Plugin { plugin_id, .. } => {
            if user_scope_plugin_installed(&ctx.home_directory, &|id: &str| id == plugin_id) {
                ProbeResult::Installed
            } else {
                ProbeResult::Missing
            }
        }
        ToolkitInstallSpec::Mcp { name, .. } => probe_mcp(name, &ctx.home_directory),
        ToolkitInstallSpec::Skill { url } => probe_skill(url, &ctx.home_directory),
        ToolkitInstallSpec::Package { manager, name, .. } if manager == "npm" => probe_npm(name, &ctx.path_directories),
        ToolkitInstallSpec::Package { manager, executable: Some(executable), .. } if manager == "winget" => {
            probe_winget(executable, &ctx.local_app_data, &ctx.path_directories)
        }
        // brew and repoScript are macOS-only; always missing on Windows.
        _ => ProbeResult::Missing,
    }
}

/// A file of more than 4 Mi characters counts as missing; one of more than four times as many bytes holds more characters than
/// that whatever its text, so it is refused before it is read.
fn read_bounded(path: &Path) -> Option<String> {
    let meta = std::fs::metadata(path).ok()?;
    if !meta.is_file() || meta.len() > 16 * 1024 * 1024 {
        return None;
    }
    let bytes = std::fs::read(path).ok()?;
    let text = String::from_utf8_lossy(&bytes);
    let text = text.strip_prefix('\u{feff}').unwrap_or(&text).to_string();
    (text.encode_utf16().count() <= 4 * 1024 * 1024).then_some(text)
}

/// Whether `installed_plugins.json` has a plugin whose ID `matching` accepts with at least one user-scope record (the toolkit asks
/// for one exact ID, the setup screen for a whole plugin by its `name@` prefix).
pub fn user_scope_plugin_installed(home: &Path, matching: &dyn Fn(&str) -> bool) -> bool {
    let path = home.join(".claude").join("plugins").join("installed_plugins.json");
    let Some(text) = read_bounded(&path) else { return false };
    let Ok(root) = serde_json::from_str::<Value>(&text) else { return false };
    let Some(plugins) = root.get("plugins").and_then(Value::as_object) else { return false };
    plugins.iter().any(|(name, records)| {
        matching(name)
            && records.as_array().is_some_and(|records| {
                records.iter().any(|record| record.get("scope").and_then(Value::as_str) == Some("user") && record.is_object())
            })
    })
}

/// mcp: the name under the top-level `mcpServers` of `%USERPROFILE%\.claude.json`.
fn probe_mcp(name: &str, home: &Path) -> ProbeResult {
    let Some(text) = read_bounded(&home.join(".claude.json")) else { return ProbeResult::Missing };
    let Ok(root) = serde_json::from_str::<Value>(&text) else { return ProbeResult::Missing };
    match root.get("mcpServers").and_then(Value::as_object) {
        Some(servers) if servers.contains_key(name) => ProbeResult::Installed,
        _ => ProbeResult::Missing,
    }
}

/// The skill's folder name: the URL's last path segment with a trailing `/` and `.git` (any case) taken off (C#
/// `new Uri(url).Segments.LastOrDefault()?.TrimEnd('/')`). None when the address is no URL, names no segment, or names one that is
/// no folder name ([`skill_folder`]).
pub fn skill_folder_name(url: &str) -> Option<String> {
    skill_folder(url).ok().flatten()
}

/// [`skill_folder_name`] with why it has none: an address that is no URL is an error (C#'s `new Uri` throws), as is a last segment
/// that is no plain folder name — a drive (`D:evil`), a separator, `.` or `..`, or a character Windows refuses — which joined to
/// `.claude\skills` would name a folder outside it. Ok(None) when the address names no segment.
pub fn skill_folder(url: &str) -> Result<Option<String>, String> {
    let parsed = url::Url::parse(url).map_err(|_| format!("Invalid URI: The format of the URI could not be determined. ({url})"))?;
    let path = parsed.path();
    let trimmed = path.strip_suffix('/').unwrap_or(path);
    let mut component = trimmed.rsplit('/').next().unwrap_or("").to_string();
    if component.len() >= 4
        && component.is_char_boundary(component.len() - 4)
        && component[component.len() - 4..].eq_ignore_ascii_case(".git")
    {
        component.truncate(component.len() - 4);
    }
    if component.is_empty() {
        return Ok(None);
    }
    let plain = component != "."
        && component != ".."
        && !component.chars().any(|c| matches!(c, ':' | '\\' | '/' | '<' | '>' | '"' | '|' | '?' | '*') || c.is_control());
    if !plain {
        return Err(format!("The skill's folder name is not a folder in .claude\\skills: {component} ({url})"));
    }
    Ok(Some(component))
}

/// skill: `%USERPROFILE%\.claude\skills\<name>\SKILL.md`.
fn probe_skill(url: &str, home: &Path) -> ProbeResult {
    match skill_folder_name(url) {
        Some(name) if home.join(".claude").join("skills").join(&name).join("SKILL.md").is_file() => ProbeResult::Installed,
        _ => ProbeResult::Missing,
    }
}

/// npm: npm found in a PATH folder, the package in that folder's or its parent's `node_modules` (npm's global prefix).
fn probe_npm(name: &str, path_directories: &[String]) -> ProbeResult {
    for dir in path_directories {
        let folder = Path::new(dir);
        let npm = folder.join("npm");
        if !npm.is_file() && !folder.join("npm.cmd").is_file() && !folder.join("npm.exe").is_file() {
            continue;
        }
        if folder.join("node_modules").join(name).is_dir() {
            return ProbeResult::Installed;
        }
        // e.g. npm in %APPDATA%\npm with node_modules at the same level.
        if let Some(parent) = directory_name(dir)
            && Path::new(parent).join("node_modules").join(name).is_dir()
        {
            return ProbeResult::Installed;
        }
    }
    ProbeResult::Missing
}

/// .NET's `Path.GetDirectoryName`: the text before the last separator (a root kept whole), None for a root or a bare name. A
/// folder written with a trailing separator is its own directory name there (`C:\npm\` gives `C:\npm`), not its parent.
fn directory_name(path: &str) -> Option<&str> {
    let at = path.rfind(['\\', '/'])?;
    let head = &path[..at];
    let root_len = match head.as_bytes() {
        [] => return (path.len() > 1).then(|| &path[..1]),
        [letter, b':'] if letter.is_ascii_alphabetic() => 2,
        _ => return Some(head),
    };
    (path.len() > root_len + 1).then(|| &path[..=root_len])
}

/// winget: the executable in `%LOCALAPPDATA%\Microsoft\WinGet\Links` or a PATH folder.
fn probe_winget(executable: &str, local_app_data: &Path, path_directories: &[String]) -> ProbeResult {
    if local_app_data.join("Microsoft").join("WinGet").join("Links").join(executable).is_file() {
        return ProbeResult::Installed;
    }
    if path_directories.iter().any(|dir| Path::new(dir).join(executable).is_file()) { ProbeResult::Installed } else { ProbeResult::Missing }
}
