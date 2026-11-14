//! C# `ModelSettingsFileStore` and `OmcAgentCatalog` (ModelSettingsFileStore.cs, OmcAgentCatalog.cs; macOS ModelSettingsFileStore.swift,
//! OmcAgentCatalog.swift): the model knobs Mighty Code owns in two outside tools' files, read and written as C# does.
//!
//! - omc: `agents.<camelCaseKey>.model` in `<home>/.config/claude-omc/config.jsonc`;
//! - Ouroboros: the dotted `*_model` scalar keys in `<home>/.ouroboros/config.yaml`.
//!
//! A file is read again just before it is written, backed up beside itself, and written through a temporary file moved over it. A
//! file that cannot be read is refused and left byte for byte as it was. The home folder is always given, so the checks never touch a
//! real user's file.

use std::path::{Path, PathBuf};

use indexmap::IndexMap;
use mighty_core::json::{escape_into, format_double};
use mighty_core::wire;
use serde::de::{Deserialize, Deserializer, MapAccess, SeqAccess, Visitor};

use crate::{os, strings};

// ── JSON as System.Text.Json and a .NET Dictionary keep it ─────────────────────────────────────────────────────────────

/// What `ElementToValue` makes of a JSON value: a `Dictionary<string, object?>`, a `List<object?>`, a string, a long (else a
/// double), a bool or null.
#[derive(Debug, Clone, PartialEq)]
enum Json {
    Null,
    Bool(bool),
    Long(i64),
    Double(f64),
    Text(String),
    List(Vec<Json>),
    Object(Dict),
}

/// A `Dictionary<string, object?>` in its enumeration order: a key set again keeps its place, a removed key's slot is the next new
/// key's (the most recently freed first), as .NET's `Dictionary` reuses its entries.
#[derive(Debug, Clone, Default, PartialEq)]
struct Dict {
    entries: Vec<Option<(String, Json)>>,
    free: Vec<usize>,
}

impl Dict {
    fn position(&self, key: &str) -> Option<usize> {
        self.entries.iter().position(|e| e.as_ref().is_some_and(|(k, _)| k == key))
    }
    fn get(&self, key: &str) -> Option<&Json> {
        self.position(key).and_then(|i| self.entries[i].as_ref().map(|(_, v)| v))
    }
    /// `dict[key] = value`.
    fn set(&mut self, key: &str, value: Json) {
        if let Some(i) = self.position(key) {
            self.entries[i] = Some((key.to_string(), value));
        } else if let Some(i) = self.free.pop() {
            self.entries[i] = Some((key.to_string(), value));
        } else {
            self.entries.push(Some((key.to_string(), value)));
        }
    }
    /// `dict.Remove(key)`.
    fn remove(&mut self, key: &str) -> Option<Json> {
        let i = self.position(key)?;
        self.free.push(i);
        self.entries[i].take().map(|(_, v)| v)
    }
    fn len(&self) -> usize {
        self.entries.iter().flatten().count()
    }
    fn iter(&self) -> impl Iterator<Item = (&String, &Json)> {
        self.entries.iter().flatten().map(|(k, v)| (k, v))
    }
}

struct Parsed(Json);

impl<'de> Deserialize<'de> for Parsed {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> std::result::Result<Parsed, D::Error> {
        deserializer.deserialize_any(JsonVisitor).map(Parsed)
    }
}

struct JsonVisitor;

impl<'de> Visitor<'de> for JsonVisitor {
    type Value = Json;

    fn expecting(&self, formatter: &mut std::fmt::Formatter) -> std::fmt::Result {
        formatter.write_str("JSON")
    }
    fn visit_unit<E>(self) -> std::result::Result<Json, E> {
        Ok(Json::Null)
    }
    fn visit_bool<E>(self, value: bool) -> std::result::Result<Json, E> {
        Ok(Json::Bool(value))
    }
    fn visit_i64<E>(self, value: i64) -> std::result::Result<Json, E> {
        Ok(Json::Long(value))
    }
    /// `TryGetInt64` fails past a long: the double.
    fn visit_u64<E>(self, value: u64) -> std::result::Result<Json, E> {
        Ok(i64::try_from(value).map_or(Json::Double(value as f64), Json::Long))
    }
    fn visit_f64<E>(self, value: f64) -> std::result::Result<Json, E> {
        Ok(Json::Double(value))
    }
    fn visit_str<E>(self, value: &str) -> std::result::Result<Json, E> {
        Ok(Json::Text(value.to_string()))
    }
    fn visit_string<E>(self, value: String) -> std::result::Result<Json, E> {
        Ok(Json::Text(value))
    }
    fn visit_seq<A: SeqAccess<'de>>(self, mut seq: A) -> std::result::Result<Json, A::Error> {
        let mut items = Vec::new();
        while let Some(Parsed(item)) = seq.next_element::<Parsed>()? {
            items.push(item);
        }
        Ok(Json::List(items))
    }
    /// `ElementToDict`: `dict[prop.Name] = value`, so a name written twice keeps its first place and its last value.
    fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> std::result::Result<Json, A::Error> {
        let mut dict = Dict::default();
        while let Some((name, Parsed(value))) = map.next_entry::<String, Parsed>()? {
            dict.set(&name, value);
        }
        Ok(Json::Object(dict))
    }
}

/// `SerializeToJson`: `JsonSerializer.Serialize(value, WriteIndented = true)` — two spaces, `"name": value`, `Environment.NewLine`
/// between lines, text escaped as `JavaScriptEncoder.Default` escapes it, nulls written.
fn serialize(value: &Json) -> String {
    let mut out = String::new();
    write_json(&mut out, value, 0, os::new_line());
    out
}

fn write_json(out: &mut String, value: &Json, depth: usize, new_line: &str) {
    let indent = |out: &mut String, depth: usize| out.push_str(&"  ".repeat(depth));
    match value {
        Json::Null => out.push_str("null"),
        Json::Bool(b) => out.push_str(if *b { "true" } else { "false" }),
        Json::Long(n) => out.push_str(&n.to_string()),
        Json::Double(d) => out.push_str(&format_double(*d)),
        Json::Text(s) => escape_into(out, s),
        Json::List(items) => {
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
                write_json(out, item, depth + 1, new_line);
            }
            out.push_str(new_line);
            indent(out, depth);
            out.push(']');
        }
        Json::Object(dict) => {
            if dict.len() == 0 {
                out.push_str("{}");
                return;
            }
            out.push('{');
            for (i, (name, item)) in dict.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                out.push_str(new_line);
                indent(out, depth + 1);
                escape_into(out, name);
                out.push_str(": ");
                write_json(out, item, depth + 1, new_line);
            }
            out.push_str(new_line);
            indent(out, depth);
            out.push('}');
        }
    }
}

/// `ParseJSONC`: the comments taken out, then the root object; what does not read is refused with the settings screen's words.
fn parse_jsonc(data: &[u8]) -> std::result::Result<Dict, String> {
    let source = String::from_utf8_lossy(data);
    let stripped = strip_jsonc_comments(&source);
    match serde_json::from_str::<Parsed>(&stripped) {
        Ok(Parsed(Json::Object(dict))) => Ok(dict),
        Ok(_) => Err(strings::get("settings.phaseModels.error.jsoncNotObject")),
        Err(_) => Err(strings::get("settings.phaseModels.error.jsoncUnparseable")),
    }
}

/// `StripJSONCComments`: `//` to the end of the line (the line break kept) and `/* … */` out, never inside a string.
pub fn strip_jsonc_comments(source: &str) -> String {
    let chars: Vec<char> = source.chars().collect();
    let mut result = String::with_capacity(source.len());
    let (mut in_string, mut in_line, mut in_block) = (false, false, false);
    let mut i = 0;
    while i < chars.len() {
        let c = chars[i];
        let next = chars.get(i + 1).copied().unwrap_or('\0');
        if in_line {
            if c == '\n' {
                in_line = false;
                result.push(c);
            }
        } else if in_block {
            if c == '*' && next == '/' {
                in_block = false;
                i += 1;
            }
        } else if in_string {
            result.push(c);
            if c == '\\' {
                i += 1;
                if let Some(escaped) = chars.get(i) {
                    result.push(*escaped);
                }
            } else if c == '"' {
                in_string = false;
            }
        } else if c == '"' {
            in_string = true;
            result.push(c);
        } else if c == '/' && next == '/' {
            in_line = true;
            i += 1;
        } else if c == '/' && next == '*' {
            in_block = true;
            i += 1;
        } else {
            result.push(c);
        }
        i += 1;
    }
    result
}

// ── YAML ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// A top-level line (`section:`) that opens a section: no leading space or tab, a trailing `:` and no space in it.
fn section_of(line: &str, refuse_comment: bool) -> Option<String> {
    let t = line.trim_end();
    (t.ends_with(':') && !t.contains(' ') && !(refuse_comment && t.starts_with('#'))).then(|| t[..t.len() - 1].to_string())
}

fn top_level(line: &str) -> bool {
    !line.is_empty() && !line.starts_with(' ') && !line.starts_with('\t')
}

/// `ParseYAMLScalars`: every `section.key: value` scalar one level under a top-level section (lists, blocks and flow values left out).
pub fn parse_yaml_scalars(content: &str) -> IndexMap<String, String> {
    let mut result = IndexMap::new();
    let mut section: Option<String> = None;
    for line in content.split('\n') {
        let trimmed = line.trim_start();
        if trimmed.is_empty() || trimmed.starts_with('#') {
            continue;
        }
        if top_level(line) {
            section = section_of(line, false);
        } else if let Some(section) = &section {
            if trimmed.starts_with('-') {
                continue;
            }
            let Some(colon) = trimmed.find(':') else { continue };
            let key = trimmed[..colon].trim();
            let after = trimmed[colon + 1..].trim();
            if after.is_empty() || after.starts_with(['{', '[', '|', '>']) {
                continue;
            }
            result.insert(format!("{section}.{key}"), after.to_string());
        }
    }
    result
}

/// `RewriteYAMLKeys`: only the lines whose dotted key is in `updates` are written again (`<indent>key: value`); every other line is
/// kept as it is.
pub fn rewrite_yaml_keys(content: &str, updates: &IndexMap<String, String>) -> String {
    let mut lines: Vec<String> = Vec::new();
    let mut section: Option<String> = None;
    for line in content.split('\n') {
        let trimmed = line.trim_start();
        if top_level(line) {
            section = section_of(line, true);
            lines.push(line.to_string());
            continue;
        }
        let Some(current) = section.as_ref().filter(|_| !(trimmed.is_empty() || trimmed.starts_with('#') || trimmed.starts_with('-')))
        else {
            lines.push(line.to_string());
            continue;
        };
        let Some(colon) = trimmed.find(':') else {
            lines.push(line.to_string());
            continue;
        };
        let key = trimmed[..colon].trim();
        match updates.get(&format!("{current}.{key}")) {
            Some(value) => {
                let leading = line.len() - trimmed.len();
                lines.push(format!("{}{key}: {value}", &line[..leading]));
            }
            None => lines.push(line.to_string()),
        }
    }
    lines.join("\n")
}

// ── the files ───────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `ModelSettingsFileStore(homeDirectory)`.
#[derive(Debug, Clone)]
pub struct ModelSettingsFileStore {
    home: PathBuf,
}

impl ModelSettingsFileStore {
    pub fn new(home: &Path) -> ModelSettingsFileStore {
        ModelSettingsFileStore { home: home.to_path_buf() }
    }

    /// `OmcConfigPath`.
    pub fn omc_config_path(&self) -> PathBuf {
        self.home.join(".config").join("claude-omc").join("config.jsonc")
    }

    /// `OuroborosConfigPath`.
    pub fn ouroboros_config_path(&self) -> PathBuf {
        self.home.join(".ouroboros").join("config.yaml")
    }

    /// `LoadOmcAgents`: `[camelCaseKey: model]` for every agent that names a model; None when the file does not exist; the screen's
    /// words when it cannot be read.
    pub fn load_omc_agents(&self) -> std::result::Result<Option<IndexMap<String, String>>, String> {
        let path = self.omc_config_path();
        if !path.is_file() {
            return Ok(None);
        }
        let data = std::fs::read(&path).map_err(|e| e.to_string())?;
        let root = parse_jsonc(&data)?;
        let mut result = IndexMap::new();
        if let Some(Json::Object(agents)) = root.get("agents") {
            for (key, agent) in agents.iter() {
                if let Json::Object(agent) = agent
                    && let Some(Json::Text(model)) = agent.get("model")
                {
                    result.insert(key.clone(), model.clone());
                }
            }
        }
        Ok(Some(result))
    }

    /// `SaveOmcAgents`: only `agents.<key>.model` of the given keys written ("default" takes the model out, and an agent left with
    /// nothing goes); read again just before, backed up, written atomically. Refused, the file byte for byte as it was, when the file
    /// there cannot be read.
    pub fn save_omc_agents(&self, agents: &IndexMap<String, String>) -> std::result::Result<(), String> {
        let path = self.omc_config_path();
        let mut root = Dict::default();
        if path.is_file() {
            let existing = std::fs::read(&path).map_err(|e| e.to_string())?;
            root = parse_jsonc(&existing)?;
            write_backup(&existing, &path)?;
        }
        let mut section = match root.get("agents") {
            Some(Json::Object(section)) => section.clone(),
            _ => Dict::default(),
        };
        for (key, model) in agents {
            if model == "default" {
                if let Some(Json::Object(entry)) = section.get(key) {
                    let mut entry = entry.clone();
                    entry.remove("model");
                    if entry.len() == 0 {
                        section.remove(key);
                    } else {
                        section.set(key, Json::Object(entry));
                    }
                }
            } else {
                let mut entry = match section.get(key) {
                    Some(Json::Object(entry)) => entry.clone(),
                    _ => Dict::default(),
                };
                entry.set("model", Json::Text(model.clone()));
                section.set(key, Json::Object(entry));
            }
        }
        root.set("agents", Json::Object(section));
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        atomic_write(serialize(&Json::Object(root)).as_bytes(), &path)
    }

    /// `LoadOuroborosKeys`: `[dottedKey: value]` for the scalars whose key ends in `_model`; None when the file does not exist.
    pub fn load_ouroboros_keys(&self) -> std::result::Result<Option<IndexMap<String, String>>, String> {
        let path = self.ouroboros_config_path();
        if !path.is_file() {
            return Ok(None);
        }
        let bytes = std::fs::read(&path).map_err(|e| e.to_string())?;
        let content = String::from_utf8_lossy(&bytes);
        Ok(Some(parse_yaml_scalars(&content).into_iter().filter(|(k, _)| k.ends_with("_model")).collect()))
    }

    /// `SaveOuroborosKeys`: only the lines of the owned dotted keys are written again; every other line (`orchestrator.cli_path`
    /// included) is kept as it is. Nothing is written when the file does not exist.
    pub fn save_ouroboros_keys(&self, owned: &IndexMap<String, String>) -> std::result::Result<(), String> {
        let path = self.ouroboros_config_path();
        if !path.is_file() {
            return Ok(());
        }
        let bytes = std::fs::read(&path).map_err(|e| e.to_string())?;
        let content = String::from_utf8_lossy(&bytes).into_owned();
        write_backup(&bytes, &path)?;
        atomic_write(rewrite_yaml_keys(&content, owned).as_bytes(), &path)
    }
}

/// `WriteBackup`: `config.mighty-backup-<UTC yyyyMMddTHHmmssZ>-<8 hex>.<extension>` beside the file.
fn write_backup(data: &[u8], path: &Path) -> std::result::Result<(), String> {
    let stamp = chrono::Utc::now().format("%Y%m%dT%H%M%SZ");
    let id: String = wire::new_id().replace('-', "").chars().take(8).collect();
    let extension = path.extension().map(|e| format!(".{}", e.to_string_lossy())).unwrap_or_default();
    let directory = path.parent().unwrap_or(Path::new("."));
    std::fs::write(directory.join(format!("config.mighty-backup-{stamp}-{id}{extension}")), data).map_err(|e| e.to_string())
}

/// `AtomicWrite`: a temporary `.<name>.tmp-<id>` beside the file, moved over it; the temporary file goes when the move fails.
fn atomic_write(data: &[u8], path: &Path) -> std::result::Result<(), String> {
    let directory = path.parent().unwrap_or(Path::new("."));
    std::fs::create_dir_all(directory).map_err(|e| e.to_string())?;
    let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let temporary = directory.join(format!(".{name}.tmp-{}", wire::new_id().replace('-', "")));
    std::fs::write(&temporary, data).map_err(|e| e.to_string())?;
    std::fs::rename(&temporary, path).map_err(|e| {
        let _ = std::fs::remove_file(&temporary);
        e.to_string()
    })
}

// ── the installed omc agents ────────────────────────────────────────────────────────────────────────────────────────────

/// C# `OmcAgentCatalog(homeDirectory)`: the installed oh-my-claudecode plugin's agents and the default model each one's frontmatter
/// names (a value shown, never written: config.jsonc's `agents.<key>.model` stays the value's owner).
#[derive(Debug, Clone)]
pub struct OmcAgentCatalog {
    home: PathBuf,
}

impl OmcAgentCatalog {
    pub fn new(home: &Path) -> OmcAgentCatalog {
        OmcAgentCatalog { home: home.to_path_buf() }
    }

    /// `InstalledPluginsPath`.
    pub fn installed_plugins_path(&self) -> PathBuf {
        self.home.join(".claude").join("plugins").join("installed_plugins.json")
    }

    /// `Scan`: `[camelCaseKey: frontmatter model]` from the first user-scope record of an `oh-my-claudecode@…` plugin whose
    /// `agents` folder holds `*.md` files; None when omc is not installed (the screen leaves its block out). The files are read in
    /// name order.
    pub fn scan(&self) -> Option<IndexMap<String, String>> {
        let path = self.installed_plugins_path();
        if !path.is_file() {
            return None;
        }
        let root: serde_json::Value = serde_json::from_slice(&std::fs::read(&path).ok()?).ok()?;
        let plugins = root.as_object()?.get("plugins")?.as_object()?;
        for (name, records) in plugins {
            if !name.starts_with("oh-my-claudecode@") {
                continue;
            }
            let Some(records) = records.as_array() else { continue };
            for record in records {
                let Some(record) = record.as_object() else { continue };
                if record.get("scope").and_then(serde_json::Value::as_str) != Some("user") {
                    continue;
                }
                let Some(install) = record.get("installPath").and_then(serde_json::Value::as_str) else { continue };
                let agents = Path::new(install).join("agents");
                if !agents.is_dir() {
                    continue;
                }
                let mut files: Vec<PathBuf> = std::fs::read_dir(&agents)
                    .map(|entries| entries.flatten().map(|e| e.path()).filter(|p| p.is_file() && markdown(p)).collect())
                    .unwrap_or_default();
                if files.is_empty() {
                    continue;
                }
                files.sort();
                let mut result = IndexMap::new();
                for file in files {
                    let stem = file.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
                    result.insert(kebab_to_camel_case(&stem), frontmatter_model(&file).unwrap_or_else(|| "default".into()));
                }
                return Some(result);
            }
        }
        None
    }
}

/// `Directory.GetFiles(folder, "*.md")`: the `.md` extension, in any case on Windows.
fn markdown(path: &Path) -> bool {
    let extension = path.extension().map(|e| e.to_string_lossy().into_owned()).unwrap_or_default();
    if os::windows() { extension.eq_ignore_ascii_case("md") } else { extension == "md" }
}

/// `KebabToCamelCase`: a file name (kebab-case) as omc's key (lowerCamelCase); one word stays as it is.
pub fn kebab_to_camel_case(value: &str) -> String {
    let parts: Vec<&str> = value.split('-').collect();
    if parts.len() < 2 {
        return value.to_string();
    }
    let mut result = parts[0].to_string();
    for part in &parts[1..] {
        let mut chars = part.chars();
        if let Some(first) = chars.next() {
            let mut upper = first.to_uppercase();
            match (upper.next(), upper.next()) {
                (Some(one), None) => result.push(one),
                _ => result.push(first),
            }
            result.push_str(chars.as_str());
        }
    }
    result
}

/// `FrontmatterModel`: the `model:` scalar of the `---`-fenced YAML frontmatter, or None.
pub fn frontmatter_model(path: &Path) -> Option<String> {
    let bytes = std::fs::read(path).ok()?;
    let text = String::from_utf8_lossy(bytes.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(&bytes)).into_owned();
    let mut inside = false;
    for raw in text.split('\n') {
        let line = raw.trim();
        if line == "---" {
            if !inside {
                inside = true;
                continue;
            }
            break;
        }
        if inside && let Some(model) = line.strip_prefix("model:") {
            return Some(model.trim().to_string());
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_dictionary_reuses_a_removed_keys_place_as_dotnet_does() {
        let mut dict = Dict::default();
        dict.set("a", Json::Long(1));
        dict.set("b", Json::Long(2));
        dict.set("c", Json::Long(3));
        dict.remove("b");
        dict.set("d", Json::Long(4));
        dict.set("a", Json::Long(5));
        assert_eq!(dict.iter().map(|(k, _)| k.as_str()).collect::<Vec<_>>(), ["a", "d", "c"]);
        assert_eq!(dict.get("a"), Some(&Json::Long(5)));
    }

    #[test]
    fn indented_json_is_what_system_text_json_writes() {
        let Ok(Parsed(value)) = serde_json::from_str::<Parsed>(r#"{"a":"<한>","b":{},"c":[],"d":[1,2.5,1e300,null,true],"e":{"f":"g"}}"#)
        else {
            panic!("reads")
        };
        let nl = os::new_line();
        // JavaScriptEncoder.Default escapes < > and every non-ASCII character.
        let escaped = format!(r#"  "a": "{b}u003C{b}uD55C{b}u003E","#, b = '\\');
        let expected = [
            "{",
            escaped.as_str(),
            r#"  "b": {},"#,
            r#"  "c": [],"#,
            r#"  "d": ["#,
            "    1,",
            "    2.5,",
            "    1E+300,",
            "    null,",
            "    true",
            "  ],",
            r#"  "e": {"#,
            r#"    "f": "g""#,
            "  }",
            "}",
        ]
        .join(nl);
        assert_eq!(serialize(&value), expected);
    }
}
