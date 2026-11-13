//! Custom sub-agents' colour and model from their definition files, for the flow diagram's headers (C# `AgentDefinitions`,
//! MightyCode.Core/AgentDefinitions.cs; macOS AgentDefinitions): the project's `<workspace>/.claude/agents/*.md`, the user's
//! `<Claude config>/agents/*.md` (a project agent wins), and a plugin's agent (`plugin:agent`) from that plugin's `agents` folder. Nothing is
//! written; the files are read through [`AgentFiles`].

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::{LazyLock, Mutex};

use mighty_core::wire;
use serde_json::Value;

use crate::flow::roles::AGENT_COLORS;
pub use crate::flow::roles::AgentDefinition;
use crate::node::clean;

/// `MaximumBytes`: a file's head read for its frontmatter.
pub const MAXIMUM_BYTES: usize = 16 * 1024;
/// `MaximumFiles`: a folder's files read at most.
pub const MAXIMUM_FILES: usize = 256;
/// `MaximumLines`: frontmatter lines read at most.
pub const MAXIMUM_LINES: usize = 80;
const MAXIMUM_PLUGIN_INDEX_BYTES: u64 = 1024 * 1024;
const LINE_BREAKS: [char; 7] = ['\n', '\r', '\u{b}', '\u{c}', '\u{85}', '\u{2028}', '\u{2029}'];

/// What a folder listing says of one entry.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FileInfo {
    pub name: String,
    pub length: u64,
    /// The last write time, in any unit that changes when the file does.
    pub modified: i64,
    pub is_dir: bool,
}

/// The file access the definitions need.
pub trait AgentFiles {
    /// The files of `folder` (not its sub-folders' contents); None when the folder does not exist or cannot be read.
    fn list(&self, folder: &Path) -> Option<Vec<FileInfo>>;
    /// One file's entry; None when it is not there.
    fn info(&self, path: &Path) -> Option<FileInfo>;
    /// The first `max` bytes of a file; None when it does not open.
    fn head(&self, path: &Path, max: usize) -> Option<Vec<u8>>;
}

/// The file system itself.
pub struct DiskFiles;

impl AgentFiles for DiskFiles {
    fn list(&self, folder: &Path) -> Option<Vec<FileInfo>> {
        let entries = std::fs::read_dir(folder).ok()?;
        Some(
            entries
                .filter_map(Result::ok)
                .filter_map(|e| {
                    let meta = e.metadata().ok()?;
                    (!meta.is_dir()).then(|| FileInfo {
                        name: e.file_name().to_string_lossy().into_owned(),
                        length: meta.len(),
                        modified: modified(&meta),
                        is_dir: false,
                    })
                })
                .collect(),
        )
    }

    fn info(&self, path: &Path) -> Option<FileInfo> {
        let meta = std::fs::metadata(path).ok()?;
        Some(FileInfo {
            name: path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default(),
            length: meta.len(),
            modified: modified(&meta),
            is_dir: meta.is_dir(),
        })
    }

    fn head(&self, path: &Path, max: usize) -> Option<Vec<u8>> {
        use std::io::Read;
        let file = std::fs::File::open(path).ok()?;
        let mut buffer = Vec::with_capacity(max.min(64 * 1024));
        file.take(max as u64).read_to_end(&mut buffer).ok()?;
        Some(buffer)
    }
}

fn modified(meta: &std::fs::Metadata) -> i64 {
    meta.modified().ok().and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok()).map_or(0, |d| d.as_nanos().min(i64::MAX as u128) as i64)
}

/// Swift's `split(omittingEmptySubsequences: false, whereSeparator: \.isNewline)`, where "\r\n" is one break.
fn split_lines(text: &str) -> Vec<&str> {
    let mut result = vec![];
    let mut start = 0;
    let bytes = text.as_bytes();
    let mut iter = text.char_indices().peekable();
    while let Some((at, c)) = iter.next() {
        if !LINE_BREAKS.contains(&c) {
            continue;
        }
        result.push(&text[start..at]);
        let mut end = at + c.len_utf8();
        if c == '\r' && bytes.get(end) == Some(&b'\n') {
            iter.next();
            end += 1;
        }
        start = end;
    }
    result.push(&text[start..]);
    result
}

/// `Scalar`: a frontmatter value on its key's line, quotes taken off, a trailing comment cut; none for an empty value and for a list, a
/// mapping or a block.
pub fn scalar(raw: &str) -> Option<String> {
    let mut value = raw.trim_matches([' ', '\t']);
    let first = value.chars().next()?;
    if "[{|>&*!".contains(first) {
        return None;
    }
    if first == '"' || first == '\'' {
        let end = value[1..].find(first)? + 1;
        value = &value[1..end];
    } else if let Some(comment) = value.find(" #") {
        value = value[..comment].trim_matches([' ', '\t']);
    }
    (!value.is_empty()).then(|| value.to_string())
}

/// `Parse`: the definition a file's text holds, or none when it does not open with a frontmatter block. Read line by line, not as YAML.
pub fn parse(text: &str, file_name: &str) -> Option<AgentDefinition> {
    let lines = split_lines(text);
    let first = lines.first()?;
    let first = first.strip_prefix('\u{feff}').unwrap_or(first);
    if first.trim_matches([' ', '\t']) != "---" {
        return None;
    }
    let mut fields: HashMap<String, String> = HashMap::new();
    let mut closed = false;
    for line in lines.iter().skip(1).take(MAXIMUM_LINES) {
        let trimmed = line.trim_matches([' ', '\t']);
        if trimmed == "---" || trimmed == "..." {
            closed = true;
            break;
        }
        let Some(lead) = line.chars().next() else { continue };
        if !(lead.is_alphabetic() || lead == '_') {
            continue;
        }
        let Some(colon) = line.find(':') else { continue };
        let key = line[..colon].trim_matches([' ', '\t']).to_lowercase();
        if !matches!(key.as_str(), "name" | "color" | "model") || fields.contains_key(&key) {
            continue;
        }
        if let Some(value) = scalar(&line[colon + 1..]) {
            fields.insert(key, value);
        }
    }
    if !closed {
        return None;
    }
    let fallback = Path::new(file_name).file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
    let name = clean(fields.get("name").map_or(fallback.as_str(), String::as_str), 160, true);
    if name.is_empty() {
        return None;
    }
    let color = fields.get("color").map(|c| c.to_lowercase()).filter(|c| AGENT_COLORS.contains(&c.as_str()));
    let model = fields.get("model").cloned().filter(|m| wire::model(m));
    Some(AgentDefinition { name, color, model })
}

/// `ConfigDirectory`: Claude's user folder, `CLAUDE_CONFIG_DIR` when set (from `environment`, else this process's), else `~/.claude`.
pub fn config_directory(home: &Path, environment: Option<&HashMap<String, String>>) -> PathBuf {
    let configured = match environment {
        Some(env) => env.get("CLAUDE_CONFIG_DIR").cloned(),
        None => std::env::var("CLAUDE_CONFIG_DIR").ok(),
    };
    match configured.filter(|c| !c.trim().is_empty()) {
        Some(c) => home.join(c),
        None => home.join(".claude"),
    }
}

/// `Load`: the definitions of the agents `types` name, by type. `workspace` is the pane's local folder (none reads no project agents).
pub fn load(
    types: &HashSet<String>,
    workspace: Option<&Path>,
    config_directory: &Path,
    cache: &Cache,
    files: &dyn AgentFiles,
) -> HashMap<String, AgentDefinition> {
    let mut found = HashMap::new();
    if types.is_empty() {
        return found;
    }
    // The user's first, so the project's replace them.
    let mut folders = vec![config_directory.join("agents")];
    if let Some(workspace) = workspace {
        folders.push(workspace.join(".claude").join("agents"));
    }
    for folder in &folders {
        for definition in cache.definitions(folder, files) {
            if types.contains(&definition.name) {
                found.insert(definition.name.clone(), definition);
            }
        }
    }
    let mut scoped: Vec<&String> = types.iter().filter(|t| t.contains(':') && !found.contains_key(*t)).collect();
    if scoped.is_empty() {
        return found;
    }
    scoped.sort();
    let installs = cache.plugin_folders(config_directory, files);
    for kind in scoped {
        let parts: Vec<&str> = kind.split(':').collect();
        let agent = parts[parts.len() - 1];
        if parts.len() < 2 || agent.is_empty() || agent.contains('/') || agent.contains('\\') || agent == ".." {
            continue;
        }
        let Some(folder) = installs.get(parts[0]) else { continue };
        // The file is named after the agent; its frontmatter's name is the same one without the plugin.
        if let Some(definition) = cache.definitions(&Path::new(folder).join("agents"), files).into_iter().find(|d| d.name == agent) {
            found.insert(kind.clone(), AgentDefinition { name: kind.clone(), ..definition });
        }
    }
    found
}

/// `Cache`: what was read of each folder, kept until the folder's files change.
#[derive(Default)]
pub struct Cache {
    folders: Mutex<HashMap<String, (String, Vec<AgentDefinition>)>>,
    plugins: Mutex<HashMap<String, (String, PluginFolders)>>,
}

/// Each installed plugin's folder by the plugin's name.
pub type PluginFolders = HashMap<String, String>;

static SHARED: LazyLock<Cache> = LazyLock::new(Cache::default);

impl Cache {
    /// `Cache.Shared`.
    pub fn shared() -> &'static Cache {
        &SHARED
    }

    fn key(path: &Path) -> String {
        path.to_string_lossy().to_lowercase()
    }

    /// `Definitions`: every definition in `folder`'s `*.md` files (not its sub-folders), in file-name order.
    pub fn definitions(&self, folder: &Path, files: &dyn AgentFiles) -> Vec<AgentDefinition> {
        let key = Self::key(folder);
        let Some(entries) = files.list(folder) else {
            self.folders.lock().unwrap_or_else(|e| e.into_inner()).remove(&key);
            return vec![];
        };
        let mut names: Vec<&FileInfo> =
            entries.iter().filter(|e| !e.is_dir && e.name.to_lowercase().ends_with(".md") && !e.name.starts_with('.')).collect();
        names.sort_by(|a, b| mighty_core::normalize::ordinal(&a.name, &b.name));
        let mut stamp = String::new();
        let mut readable = vec![];
        for entry in names.into_iter().take(MAXIMUM_FILES) {
            stamp.push_str(&format!("{}|{}|{}\n", entry.name, entry.length, entry.modified));
            readable.push(entry.name.clone());
        }
        if let Some((held, definitions)) = self.folders.lock().unwrap_or_else(|e| e.into_inner()).get(&key)
            && *held == stamp
        {
            return definitions.clone();
        }
        let definitions: Vec<AgentDefinition> = readable
            .iter()
            .filter_map(|name| {
                let head = files.head(&folder.join(name), MAXIMUM_BYTES).filter(|h| !h.is_empty())?;
                parse(&String::from_utf8_lossy(&head), name)
            })
            .collect();
        self.folders.lock().unwrap_or_else(|e| e.into_inner()).insert(key, (stamp, definitions.clone()));
        definitions
    }

    /// `PluginFolders`: each installed plugin's folder by the plugin's name, from `plugins/installed_plugins.json`.
    pub fn plugin_folders(&self, config_directory: &Path, files: &dyn AgentFiles) -> HashMap<String, String> {
        let index = config_directory.join("plugins").join("installed_plugins.json");
        let Some(info) = files.info(&index).filter(|i| !i.is_dir && i.length <= MAXIMUM_PLUGIN_INDEX_BYTES) else { return HashMap::new() };
        let stamp = format!("{}|{}", info.length, info.modified);
        let key = Self::key(&index);
        if let Some((held, folders)) = self.plugins.lock().unwrap_or_else(|e| e.into_inner()).get(&key)
            && *held == stamp
        {
            return folders.clone();
        }
        let mut result = HashMap::new();
        if let Some(bytes) = files.head(&index, MAXIMUM_PLUGIN_INDEX_BYTES as usize)
            && let Ok(document) = serde_json::from_slice::<Value>(&bytes)
            && let Some(installed) = document.get("plugins").and_then(Value::as_object)
        {
            let mut names: Vec<&String> = installed.keys().collect();
            names.sort_by(|a, b| mighty_core::normalize::ordinal(a, b));
            for name in names {
                let plugin = name.split('@').next().unwrap_or("");
                let value = &installed[name];
                let entry = match value {
                    Value::Array(items) => items.first(),
                    other => Some(other),
                };
                let Some(path) = entry.and_then(|e| e.get("installPath")).and_then(Value::as_str).filter(|p| !p.is_empty()) else {
                    continue;
                };
                if plugin.is_empty() || result.contains_key(plugin) {
                    continue;
                }
                result.insert(plugin.to_string(), path.to_string());
            }
        }
        self.plugins.lock().unwrap_or_else(|e| e.into_inner()).insert(key, (stamp, result.clone()));
        result
    }
}
