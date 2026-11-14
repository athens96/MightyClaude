//! C# `ClaudePluginSupport`: parsing and shaping that depend only on data — no process, no file system but the path rules. Both
//! CLI answers become one snapshot; malformed or oversized output is a failed status, never a ready-but-empty list.

use std::collections::{HashMap, HashSet};

use serde_json::{Map, Value};

use super::model::{ClaudeCatalogPlugin, ClaudeInstalledPlugin, ClaudePluginMarketplace, ClaudePluginSnapshot, status};
use super::strings::{codex as codex_strings, plugin as strings};
use crate::paths;
use crate::runner::CliRunResult;

pub const MAXIMUM_LISTING_BYTES: usize = 8 * 1024 * 1024;
pub const MAXIMUM_MARKETPLACE_BYTES: usize = 512 * 1024;
pub const MAXIMUM_ROWS: usize = 10_000;
pub const MAXIMUM_MARKETPLACE_ROWS: usize = 256;
pub const DESCRIPTION_CAP: usize = 4096;
pub const VERSION_CAP: usize = 160;
pub const MESSAGE_CAP: usize = 1024;
pub const OUTPUT_CAP: usize = 16_384;
pub const PATH_CAP: usize = 16_384;

const KNOWN_SCOPES: [&str; 5] = ["user", "project", "local", "managed", "session"];
const KNOWN_SOURCE_KINDS: [&str; 9] = ["github", "git", "git-subdir", "npm", "url", "directory", "file", "command", "zip"];
/// Codex's own shorter whitelist: a marketplace registered from a folder reports "local", the curated remote catalog "remote".
const CODEX_SOURCE_KINDS: [&str; 5] = ["local", "remote", "github", "git", "directory"];
/// A change this window must never offer, read one dash-separated word at a time, so the installed tab ("tab-installed") is not an
/// install button and the list reread ("reload") is not a marketplace refresh. "upgrade" is Codex's marketplace verb.
const CHANGING_WORDS: [&str; 10] = ["install", "uninstall", "enable", "disable", "update", "upgrade", "scope", "refresh", "remove", "add"];
/// The only changes macOS offers: install a plugin, choose its scope, refresh a registered marketplace.
const ALLOWED_CHANGE_WORDS: [&str; 3] = ["install", "scope", "refresh"];

/// `\A[A-Za-z0-9][A-Za-z0-9._-]*\z`, at most 128 UTF-8 bytes.
pub fn identifier(value: Option<&str>) -> bool {
    let Some(value) = value else { return false };
    let bytes = value.as_bytes();
    !bytes.is_empty()
        && bytes.len() <= 128
        && bytes[0].is_ascii_alphanumeric()
        && bytes[1..].iter().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b'-'))
}

/// The automation id of one control in the plugin window.
pub fn automation_id(provider: &str, part: &str) -> String {
    format!("{provider}-plugin-{part}")
}

fn words<'a>(automation_id: &'a str, provider: &str) -> Option<Vec<&'a str>> {
    let prefix = self::automation_id(provider, "");
    automation_id.strip_prefix(prefix.as_str()).map(|rest| rest.split('-').collect())
}

/// Whether a control of `provider`'s window names a change.
pub fn names_a_change(automation_id: &str, provider: &str) -> bool {
    words(automation_id, provider).is_some_and(|words| words.iter().any(|w| CHANGING_WORDS.contains(w)))
}

/// Whether a control names a change, and only the changes macOS has (uninstall, enable, disable, add stay refused).
pub fn names_allowed_change(automation_id: &str, provider: &str) -> bool {
    words(automation_id, provider).is_some_and(|words| {
        words.iter().any(|w| CHANGING_WORDS.contains(w))
            && words.iter().all(|w| !CHANGING_WORDS.contains(w) || ALLOWED_CHANGE_WORDS.contains(w))
    })
}

/// "name@marketplace" split, both halves identifiers.
pub fn plugin_parts(value: Option<&str>) -> Option<(String, String)> {
    let parts: Vec<&str> = value?.split('@').collect();
    if parts.len() != 2 || !identifier(Some(parts[0])) || !identifier(Some(parts[1])) {
        return None;
    }
    Some((parts[0].to_string(), parts[1].to_string()))
}

/// Claude Code 2.1.268 or later: the JSON plugin subcommands (`\A(\d+)\.(\d+)\.(\d+)(\s|$)` on the trimmed text).
pub fn supported_version(version_text: &str) -> bool {
    let text = version_text.trim();
    let mut numbers = [0u64; 3];
    let mut rest = text;
    for (index, number) in numbers.iter_mut().enumerate() {
        let digits = rest.bytes().take_while(u8::is_ascii_digit).count();
        if digits == 0 {
            return false;
        }
        let Ok(value) = rest[..digits].parse::<u64>() else { return false };
        *number = value;
        rest = &rest[digits..];
        if index < 2 {
            let Some(after) = rest.strip_prefix('.') else { return false };
            rest = after;
        }
    }
    if !(rest.is_empty() || rest.starts_with(char::is_whitespace) || rest.starts_with('\n')) {
        return false;
    }
    numbers >= [2, 1, 268]
}

/// macOS `display()`: control characters dropped (but LF and tab), at most `limit` UTF-16 units.
pub fn display(value: Option<&str>, limit: usize) -> String {
    let Some(value) = value else { return String::new() };
    let mut out = String::new();
    let mut units = 0;
    for c in value.chars() {
        if c.is_control() && !matches!(c, '\n' | '\t') {
            continue;
        }
        out.push(c);
        units += c.len_utf16();
        if units >= limit {
            break;
        }
    }
    out
}

/// A project or local record applies when the working folder is that project or sits inside it; another project's never does.
pub fn applies(project_path: &str, working_directory: &str) -> bool {
    if project_path.is_empty() || project_path.contains('\0') || project_path.len() > PATH_CAP {
        return false;
    }
    let (Some(root), Some(cwd)) = (paths::full_path(project_path), paths::full_path(working_directory)) else { return false };
    let (mut root, mut cwd) = (root.to_string_lossy().into_owned(), cwd.to_string_lossy().into_owned());
    if mighty_run::os::windows() {
        root = root.to_lowercase();
        cwd = cwd.to_lowercase();
    }
    if cwd == root {
        return true;
    }
    let separator = std::path::MAIN_SEPARATOR;
    let prefix = if root.ends_with(separator) { root } else { format!("{root}{separator}") };
    cwd.starts_with(&prefix)
}

/// A source's kind as the Claude list words it: the known ones, a path as "directory", anything else "unknown".
pub fn source_kind(value: Option<&Value>) -> String {
    let kind = match value {
        Some(Value::Object(object)) => object.get("source").and_then(Value::as_str).unwrap_or("unknown"),
        Some(Value::String(text)) => text.as_str(),
        _ => "unknown",
    };
    known_kind(kind, &KNOWN_SOURCE_KINDS)
}

fn known_kind(kind: &str, known: &[&str]) -> String {
    if known.contains(&kind) {
        kind.to_string()
    } else if kind.starts_with("./") || kind.starts_with('/') {
        "directory".into()
    } else {
        "unknown".into()
    }
}

/// Codex's `sourceKind`: `sourceType` first, then `source`, of `value` or else `fallback`, against Codex's whitelist.
fn codex_source_kind(value: Option<&Value>, fallback: Option<&Value>) -> String {
    let Some(Value::Object(object)) = value.filter(|v| v.is_object()).or(fallback) else { return "unknown".into() };
    let kind =
        object.get("sourceType").and_then(Value::as_str).or_else(|| object.get("source").and_then(Value::as_str)).unwrap_or("unknown");
    known_kind(kind, &CODEX_SOURCE_KINDS)
}

/// The C# `Text` extension: a property's string, None when absent or not a string (or the row not an object).
fn text<'a>(row: &'a Value, name: &str) -> Option<&'a str> {
    row.as_object()?.get(name)?.as_str()
}

fn property<'a>(row: &'a Value, name: &str) -> Option<&'a Value> {
    row.as_object()?.get(name)
}

fn boolean(row: &Value, name: &str) -> Option<bool> {
    property(row, name)?.as_bool()
}

/// A row's `errors` or `notes`: at most 16 strings, each displayed.
fn messages(row: &Value, name: &str) -> Vec<String> {
    match property(row, name) {
        Some(Value::Array(values)) => values.iter().filter_map(Value::as_str).take(16).map(|v| display(Some(v), MESSAGE_CAP)).collect(),
        _ => Vec::new(),
    }
}

fn version(row: &Value) -> Option<String> {
    text(row, "version").filter(|v| !v.is_empty()).map(|v| display(Some(v), VERSION_CAP))
}

fn malformed(cli_version: Option<&str>) -> ClaudePluginSnapshot {
    ClaudePluginSnapshot {
        cli_version: cli_version.map(str::to_string),
        ..ClaudePluginSnapshot::of(status::FAILED, strings::detail_malformed())
    }
}

fn array<'a>(object: &'a Map<String, Value>, name: &str) -> Option<&'a Vec<Value>> {
    object.get(name)?.as_array()
}

/// Whether an answer's row arrays stay within the caps — a top-level array (Claude's marketplaces), or the `installed`,
/// `available` and `marketplaces` arrays of a top-level object — read without keeping a value and stopped at the first array past
/// its cap, so an answer of too many rows is refused before it is parsed into values. Text that is no JSON answers false too (the
/// parse would refuse it).
pub fn within_row_caps(text: &str) -> bool {
    use serde::Deserialize;
    let mut reader = serde_json::Deserializer::from_str(text);
    Rows::deserialize(&mut reader).is_ok() && reader.end().is_ok()
}

/// What [`within_row_caps`] reads: nothing kept, an error once an array runs past its cap.
struct Rows;

/// One array's elements counted (ignored) up to `cap`; anything that is not an array passes.
struct Capped(usize);

impl<'de> serde::de::DeserializeSeed<'de> for Capped {
    type Value = ();

    fn deserialize<D: serde::Deserializer<'de>>(self, deserializer: D) -> Result<(), D::Error> {
        deserializer.deserialize_any(self)
    }
}

impl<'de> serde::de::Visitor<'de> for Capped {
    type Value = ();

    fn expecting(&self, f: &mut std::fmt::Formatter) -> std::fmt::Result {
        write!(f, "at most {} rows", self.0)
    }

    fn visit_seq<A: serde::de::SeqAccess<'de>>(self, mut seq: A) -> Result<(), A::Error> {
        let mut count = 0;
        while seq.next_element::<serde::de::IgnoredAny>()?.is_some() {
            count += 1;
            if count > self.0 {
                return Err(serde::de::Error::custom("too many rows"));
            }
        }
        Ok(())
    }

    fn visit_map<A: serde::de::MapAccess<'de>>(self, mut map: A) -> Result<(), A::Error> {
        while map.next_entry::<serde::de::IgnoredAny, serde::de::IgnoredAny>()?.is_some() {}
        Ok(())
    }

    fn visit_bool<E>(self, _: bool) -> Result<(), E> {
        Ok(())
    }
    fn visit_i64<E>(self, _: i64) -> Result<(), E> {
        Ok(())
    }
    fn visit_u64<E>(self, _: u64) -> Result<(), E> {
        Ok(())
    }
    fn visit_f64<E>(self, _: f64) -> Result<(), E> {
        Ok(())
    }
    fn visit_str<E>(self, _: &str) -> Result<(), E> {
        Ok(())
    }
    fn visit_unit<E>(self) -> Result<(), E> {
        Ok(())
    }
}

impl<'de> serde::Deserialize<'de> for Rows {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Rows, D::Error> {
        struct Top;
        impl<'de> serde::de::Visitor<'de> for Top {
            type Value = Rows;

            fn expecting(&self, f: &mut std::fmt::Formatter) -> std::fmt::Result {
                f.write_str("a plugin answer")
            }

            fn visit_seq<A: serde::de::SeqAccess<'de>>(self, seq: A) -> Result<Rows, A::Error> {
                serde::de::Visitor::visit_seq(Capped(MAXIMUM_MARKETPLACE_ROWS), seq).map(|()| Rows)
            }

            fn visit_map<A: serde::de::MapAccess<'de>>(self, mut map: A) -> Result<Rows, A::Error> {
                while let Some(key) = map.next_key::<String>()? {
                    match key.as_str() {
                        "installed" | "available" => map.next_value_seed(Capped(MAXIMUM_ROWS))?,
                        "marketplaces" => map.next_value_seed(Capped(MAXIMUM_MARKETPLACE_ROWS))?,
                        _ => {
                            map.next_value::<serde::de::IgnoredAny>()?;
                        }
                    }
                }
                Ok(Rows)
            }

            fn visit_bool<E>(self, _: bool) -> Result<Rows, E> {
                Ok(Rows)
            }
            fn visit_i64<E>(self, _: i64) -> Result<Rows, E> {
                Ok(Rows)
            }
            fn visit_u64<E>(self, _: u64) -> Result<Rows, E> {
                Ok(Rows)
            }
            fn visit_f64<E>(self, _: f64) -> Result<Rows, E> {
                Ok(Rows)
            }
            fn visit_str<E>(self, _: &str) -> Result<Rows, E> {
                Ok(Rows)
            }
            fn visit_unit<E>(self) -> Result<Rows, E> {
                Ok(Rows)
            }
        }
        deserializer.deserialize_any(Top)
    }
}

/// Claude's two answers (`plugin list --json --available`, `plugin marketplace list --json`) as one snapshot.
pub fn parse_snapshot(listing: &str, marketplaces: &str, working_directory: &str, cli_version: Option<&str>) -> ClaudePluginSnapshot {
    if listing.len() > MAXIMUM_LISTING_BYTES
        || marketplaces.len() > MAXIMUM_MARKETPLACE_BYTES
        || !within_row_caps(listing)
        || !within_row_caps(marketplaces)
    {
        return malformed(cli_version);
    }
    let (Ok(root), Ok(markets)) = (serde_json::from_str::<Value>(listing), serde_json::from_str::<Value>(marketplaces)) else {
        return malformed(cli_version);
    };
    let (Some(root), Some(markets)) = (root.as_object(), markets.as_array()) else { return malformed(cli_version) };
    let (Some(installed_rows), Some(available_rows)) = (array(root, "installed"), array(root, "available")) else {
        return malformed(cli_version);
    };
    if installed_rows.len() > MAXIMUM_ROWS || available_rows.len() > MAXIMUM_ROWS || markets.len() > MAXIMUM_MARKETPLACE_ROWS {
        return malformed(cli_version);
    }

    let mut market_names = HashSet::new();
    let mut marketplace_list = Vec::new();
    for row in markets {
        let name = text(row, "name");
        if !identifier(name) || !market_names.insert(name.unwrap_or("").to_string()) {
            continue;
        }
        marketplace_list.push(ClaudePluginMarketplace::new(name.unwrap_or(""), &source_kind(property(row, "source"))));
    }
    marketplace_list.sort_by(|a, b| a.name.cmp(&b.name));

    let mut catalog_ids = HashSet::new();
    let mut available = Vec::new();
    for row in available_rows {
        let id = text(row, "pluginId");
        let Some((name, marketplace)) = plugin_parts(id) else { continue };
        if text(row, "name") != Some(name.as_str()) || text(row, "marketplaceName") != Some(marketplace.as_str()) {
            continue;
        }
        if !market_names.contains(&marketplace) || !catalog_ids.insert(id.unwrap_or("").to_string()) {
            continue;
        }
        available.push(ClaudeCatalogPlugin {
            id: id.unwrap_or("").to_string(),
            name,
            description: display(text(row, "description"), DESCRIPTION_CAP),
            marketplace,
            version: version(row),
            source_kind: source_kind(property(row, "source")),
        });
    }
    available.sort_by(|a, b| a.id.cmp(&b.id));
    let catalog: HashMap<&str, &ClaudeCatalogPlugin> = available.iter().map(|p| (p.id.as_str(), p)).collect();

    let mut installed_ids = HashSet::new();
    let mut installed = Vec::new();
    for row in installed_rows {
        let id = text(row, "id");
        let Some((name, marketplace)) = plugin_parts(id) else { continue };
        let Some(scope) = text(row, "scope").filter(|s| KNOWN_SCOPES.contains(s)) else { continue };
        let project_path = text(row, "projectPath");
        if matches!(scope, "project" | "local") && !project_path.is_some_and(|p| applies(p, working_directory)) {
            continue;
        }
        let id = id.unwrap_or("");
        let mut description = display(text(row, "description"), DESCRIPTION_CAP);
        if description.is_empty()
            && let Some(known) = catalog.get(id)
        {
            description = known.description.clone();
        }
        let value = ClaudeInstalledPlugin {
            plugin_id: id.to_string(),
            name,
            marketplace: Some(marketplace),
            version: version(row),
            scope: scope.to_string(),
            enabled: boolean(row, "enabled"),
            project_path: project_path.filter(|p| !p.is_empty()).map(|p| paths::utf16_prefix(p, PATH_CAP)),
            description,
            errors: messages(row, "errors"),
            notes: messages(row, "notes"),
        };
        if installed_ids.insert(value.id()) {
            installed.push(value);
        }
    }
    installed.sort_by_key(ClaudeInstalledPlugin::id);

    let detail = if marketplace_list.is_empty() { strings::detail_no_marketplaces() } else { strings::detail_ready() };
    ClaudePluginSnapshot {
        status: status::READY.into(),
        detail,
        cli_version: cli_version.map(str::to_string),
        installed,
        available,
        marketplaces: marketplace_list,
        updated_at: Some(mighty_core::time::now_o()),
        diagnostic_output: String::new(),
    }
}

/// Codex's two answers (CodexPluginService.parseSnapshot): the marketplaces in a `{"marketplaces":[...]}` envelope with
/// `marketplaceSource.sourceType`; every row must be well formed or the whole answer is malformed; an available row whose
/// `installPolicy` is neither AVAILABLE nor INSTALLED_BY_DEFAULT is left out and counted; installed rows must say `"installed":true`
/// and are always user scope.
pub fn parse_codex_snapshot(
    listing: &str,
    marketplaces: &str,
    _working_directory: &str,
    cli_version: Option<&str>,
) -> ClaudePluginSnapshot {
    if listing.len() > MAXIMUM_LISTING_BYTES
        || marketplaces.len() > MAXIMUM_MARKETPLACE_BYTES
        || !within_row_caps(listing)
        || !within_row_caps(marketplaces)
    {
        return malformed(cli_version);
    }
    let (Ok(root), Ok(markets)) = (serde_json::from_str::<Value>(listing), serde_json::from_str::<Value>(marketplaces)) else {
        return malformed(cli_version);
    };
    let (Some(root), Some(markets)) = (root.as_object(), markets.as_object()) else { return malformed(cli_version) };
    let Some(market_rows) = array(markets, "marketplaces") else { return malformed(cli_version) };
    let (Some(installed_rows), Some(available_rows)) = (array(root, "installed"), array(root, "available")) else {
        return malformed(cli_version);
    };
    if installed_rows.len() > MAXIMUM_ROWS || available_rows.len() > MAXIMUM_ROWS || market_rows.len() > MAXIMUM_MARKETPLACE_ROWS {
        return malformed(cli_version);
    }

    let mut market_names = HashSet::new();
    let mut marketplace_list = Vec::new();
    for row in market_rows {
        let name = text(row, "name");
        if !identifier(name) || !market_names.insert(name.unwrap_or("").to_string()) {
            return malformed(cli_version);
        }
        let kind = codex_source_kind(property(row, "marketplaceSource"), property(row, "source"));
        marketplace_list.push(ClaudePluginMarketplace::new(name.unwrap_or(""), &kind));
    }
    marketplace_list.sort_by(|a, b| a.name.cmp(&b.name));

    let identity = |row: &Value| -> Option<(String, String, String)> {
        let id = text(row, "pluginId");
        let (name, marketplace) = plugin_parts(id)?;
        if text(row, "name") != Some(name.as_str()) || text(row, "marketplaceName") != Some(marketplace.as_str()) {
            return None;
        }
        Some((id.unwrap_or("").to_string(), name, marketplace))
    };

    let mut catalog_ids = HashSet::new();
    let mut available = Vec::new();
    let mut restricted = 0;
    for row in available_rows {
        let Some((id, name, marketplace)) = identity(row) else { return malformed(cli_version) };
        if boolean(row, "installed").is_none() || boolean(row, "enabled").is_none() {
            return malformed(cli_version);
        }
        if !catalog_ids.insert(id.clone()) {
            return malformed(cli_version);
        }
        if !matches!(text(row, "installPolicy"), Some("AVAILABLE" | "INSTALLED_BY_DEFAULT")) {
            restricted += 1;
            continue;
        }
        available.push(ClaudeCatalogPlugin {
            id,
            name,
            description: display(text(row, "description"), DESCRIPTION_CAP),
            marketplace,
            version: version(row),
            source_kind: codex_source_kind(property(row, "source"), None),
        });
    }
    available.sort_by(|a, b| a.id.cmp(&b.id));
    let catalog: HashMap<&str, &ClaudeCatalogPlugin> = available.iter().map(|p| (p.id.as_str(), p)).collect();

    let mut installed_ids = HashSet::new();
    let mut installed = Vec::new();
    for row in installed_rows {
        let Some((id, name, marketplace)) = identity(row) else { return malformed(cli_version) };
        if boolean(row, "installed") != Some(true) {
            return malformed(cli_version);
        }
        if !installed_ids.insert(id.clone()) {
            return malformed(cli_version);
        }
        let mut description = display(text(row, "description"), DESCRIPTION_CAP);
        if description.is_empty()
            && let Some(known) = catalog.get(id.as_str())
        {
            description = known.description.clone();
        }
        installed.push(ClaudeInstalledPlugin {
            plugin_id: id,
            name,
            marketplace: Some(marketplace),
            version: version(row),
            scope: "user".into(),
            enabled: boolean(row, "enabled"),
            project_path: None,
            description,
            errors: messages(row, "errors"),
            notes: messages(row, "notes"),
        });
    }
    installed.sort_by_key(ClaudeInstalledPlugin::id);

    let mut detail = if marketplace_list.is_empty() { codex_strings::detail_no_marketplaces() } else { codex_strings::detail_ready() };
    if restricted > 0 {
        detail += &codex_strings::detail_restricted_suffix().replace("{count}", &restricted.to_string());
    }
    ClaudePluginSnapshot {
        status: status::READY.into(),
        detail,
        cli_version: cli_version.map(str::to_string),
        installed,
        available,
        marketplaces: marketplace_list,
        updated_at: Some(mighty_core::time::now_o()),
        diagnostic_output: String::new(),
    }
}

/// The bounded stdout + stderr kept for the diagnostics disclosure.
pub fn output(result: &CliRunResult) -> String {
    display(Some(&format!("{}\n{}", result.output, result.error_output)), OUTPUT_CAP)
}
