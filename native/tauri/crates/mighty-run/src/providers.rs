//! C# `ProviderCatalog` (ProviderCatalog.cs): the agents' CLIs — what each is called, the settings and efforts it takes, the
//! arguments a run gets (`Arguments`, every provider, pure), where its CLI is (`FindAsync`, `DiscoverAsync`) and its model list
//! (`ReadModelsAsync` for Claude and Codex, `ReadListCommandAsync` for agy, pi and opencode, `GetRuntimeAsync`).
//!
//! The last list each CLI gave is kept ([`crate::kept_lists`], C# `KeptModelLists`): in memory, and in the storage folder when the
//! catalog has one ([`ProviderCatalog::with_storage`], C#'s `storageDirectory`), so a restart still has it. Hermes lists the picker its
//! last session offered (`HermesAgent.CachedCatalog`, `hermes-models.json` in the same folder: [`ProviderCatalog::hermes_model_cache`];
//! none kept, only its own default).
//!
//! The agents' shipped files (`AgentFiles`: pi's `ask_user` extension, opencode's question instructions) are looked up in `Agents`
//! beside the executable, or in the folder [`set_agent_files`] names (the app names the repository's own in a development build).

use std::collections::{BTreeMap, HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, RwLock};
use std::time::{Duration, Instant};

use mighty_core::json::format_double;
use mighty_core::model::{RegisteredModelEntry, RunSettings};
use mighty_core::{normalize, wire};
use mighty_platform::process::ChildReader;
use mighty_platform::{BoxFuture, ProcessHost, ShellEnvironment, SpawnRequest};
use regex::Regex;
use serde_json::{Value, json};
use tokio::io::{AsyncReadExt, AsyncWriteExt};

use crate::activity::clean;
use crate::kept_lists::{self, KeptModelLists};
use crate::lines::{Lines, Next};
use crate::meta;
use crate::model::{CliCommand, ModelCatalog, ModelOption, ModsInfo, ProviderCapabilities, ProviderRuntime, RuntimeInfo, StartRunRequest};
use crate::os;
use crate::phase_models;
use crate::strings;
use crate::text_json;
use crate::{Result, lock};

/// `ProviderCatalog.Name`.
pub fn name(provider: &str) -> &'static str {
    normalize::provider_name(provider)
}

/// `ProviderCatalog.Executable`: Antigravity's is `agy`.
pub fn executable(provider: &str) -> &str {
    if provider == "gemini" { "agy" } else { provider }
}

/// `ProviderCatalog.IsBeta`.
pub fn is_beta(provider: &str) -> bool {
    matches!(provider, "codex" | "gemini" | "pi" | "opencode" | "hermes")
}

/// `ProviderCatalog.IsLocalOnly`: pi, opencode and Hermes run on this computer only.
pub fn is_local_only(provider: &str) -> bool {
    matches!(provider, "pi" | "opencode" | "hermes")
}

/// `ProviderCatalog.IsAcp`.
pub fn is_acp(provider: &str) -> bool {
    matches!(provider, "opencode" | "hermes")
}

/// `ProviderCatalog.BetaLabel`.
pub fn beta_label(provider: &str, text: &str) -> String {
    if is_beta(provider) { format!("{text} · {}", strings::get("badge.beta")) } else { text.to_string() }
}

/// `ProviderCatalog.ShowsBetaBadge`.
pub fn shows_beta_badge(session: &mighty_core::model::RunSession) -> bool {
    session.kind() == "claude" && is_beta(session.provider())
}

/// `ProviderCatalog.PermissionModes`.
pub fn permission_modes(provider: &str, include_auto: bool) -> Vec<&'static str> {
    match provider {
        "pi" => vec!["manual", "fullAccess"],
        "hermes" => vec!["manual", "acceptEdits", "fullAccess"],
        "codex" => vec!["manual", "onRequest", "acceptEdits", "fullAccess"],
        "claude" if include_auto => vec!["plan", "manual", "acceptEdits", "auto", "fullAccess"],
        _ => vec!["manual", "plan", "acceptEdits", "fullAccess"],
    }
}

/// `ProviderCatalog.Capabilities`.
pub fn capabilities(provider: &str, version: Option<&str>) -> ProviderCapabilities {
    let modes = permission_modes(provider, provider == "claude" && supports_mods(version))
        .into_iter()
        .filter(|mode| *mode != "onRequest" || supports_codex_approvals(version))
        .map(str::to_string)
        .collect();
    ProviderCapabilities {
        effort: provider != "gemini",
        permission_modes: modes,
        max_turns: provider == "claude",
        max_budget_usd: provider == "claude",
        resume: true,
        fast_mode: provider == "codex",
        web_search: provider == "codex",
        network_access: provider == "codex",
        attachments: true,
    }
}

/// `ProviderCatalog.NormalizeSettings`.
pub fn normalize_settings(provider: &str, value: Option<&RunSettings>) -> RunSettings {
    normalize::settings(provider, value)
}

/// `ProviderCatalog.AntigravityModels`.
pub const ANTIGRAVITY_MODELS: [(&str, &str); 13] = [
    ("gemini-4-argon-medium", "Gemini 4 Argon (Medium)"),
    ("gemini-4-argon-high", "Gemini 4 Argon (High)"),
    ("gemini-4-argon-low", "Gemini 4 Argon (Low)"),
    ("gemini-3.8-flash-high", "Gemini 3.8 Flash (High)"),
    ("gemini-3.8-flash-medium", "Gemini 3.8 Flash (Medium)"),
    ("gemini-3.8-flash-low", "Gemini 3.8 Flash (Low)"),
    ("claude-opus-5-5-low", "Claude Opus 5.5 (Low)"),
    ("claude-opus-5-5-medium", "Claude Opus 5.5 (Medium)"),
    ("claude-opus-5-5-high", "Claude Opus 5.5 (High)"),
    ("claude-sonnet-5-5-low", "Claude Sonnet 5.5 (Low)"),
    ("claude-sonnet-5-5-medium", "Claude Sonnet 5.5 (Medium)"),
    ("claude-sonnet-5-5-high", "Claude Sonnet 5.5 (High)"),
    ("gpt-oss-120b-medium", "GPT-OSS 120B (Medium)"),
];

/// `Wire.ListedModelLimit`.
pub fn listed_model_limit(provider: &str) -> usize {
    match provider {
        "opencode" | "hermes" => 1_024,
        "pi" => 512,
        _ => 128,
    }
}

/// `ProviderCatalog.Fallback`: the built-in list.
pub fn fallback(provider: &str) -> ModelCatalog {
    let mut models = vec![ModelOption::new(
        "default",
        &strings::get_with("provider.fallback.defaultLabel", &[("name", strings::provider_label(provider))]),
        &strings::get("provider.fallback.defaultDescription"),
    )];
    let example = strings::get("provider.fallback.exampleDescription");
    if provider == "gemini" {
        models.extend(ANTIGRAVITY_MODELS.iter().map(|(slug, name)| ModelOption::new(slug, name, &example)));
    } else if !matches!(provider, "pi" | "opencode" | "hermes") {
        let values: &[&str] =
            if provider == "claude" { &["best", "fable", "opus", "sonnet", "haiku", "opusplan"] } else { &["gpt-5.6-sol", "gpt-6-astra"] };
        for value in values {
            let mut option = ModelOption::new(value, value, &example);
            option.supports_effort = value.contains("haiku").then_some(false);
            models.push(option);
        }
    }
    ModelCatalog::new("fallback", models, &strings::get("provider.fallback.source"))
}

/// `ProviderCatalog.AntigravityCatalog`: `agy models`, one `slug<TAB>Display Name` per line.
pub fn antigravity_catalog(text: &str) -> ModelCatalog {
    let fallback = fallback("gemini");
    let mut models = vec![fallback.models[0].clone()];
    let mut seen: HashSet<String> = HashSet::from(["default".to_string()]);
    for line in text.split('\n').take(256) {
        let fields: Vec<&str> = line.splitn(2, '\t').map(|f| f.trim_matches([' ', '\t', '\r'])).collect();
        if fields.len() != 2
            || !wire::model(fields[0])
            || fields[1].is_empty()
            || models.len() > listed_model_limit("gemini")
            || !seen.insert(fields[0].to_string())
        {
            continue;
        }
        models.push(ModelOption::new(fields[0], &wire::clean(fields[1], 160), ""));
    }
    if models.len() > 1 { ModelCatalog::new("cli", models, &strings::get("provider.catalog.antigravitySource")) } else { fallback }
}

/// `ProviderCatalog.PiCatalog`: `pi --list-models`, a header row and one padded row per model.
pub fn pi_catalog(text: &str) -> ModelCatalog {
    let fallback = fallback("pi");
    let mut models = vec![fallback.models[0].clone()];
    let mut seen: HashSet<String> = HashSet::from(["default".to_string()]);
    for line in text.split('\n').take(1_024) {
        let fields: Vec<&str> = line.split(char::is_whitespace).filter(|f| !f.is_empty()).collect();
        if fields.len() < 6 || fields[0] == "provider" || !matches!(fields[4], "yes" | "no") {
            continue;
        }
        let value = format!("{}/{}", fields[0], fields[1]);
        if !wire::model(&value) || models.len() > listed_model_limit("pi") || !seen.insert(value.clone()) {
            continue;
        }
        let mut option = ModelOption::new(
            &value,
            &value,
            &strings::get_with("provider.catalog.piModelRow", &[("context", fields[2]), ("output", fields[3])]),
        );
        option.supports_effort = Some(fields[4] == "yes");
        models.push(option);
    }
    if models.len() > 1 { ModelCatalog::new("cli", models, &strings::get("provider.catalog.piSource")) } else { fallback }
}

/// `ProviderCatalog.OpencodeCatalog`: `opencode models --standalone`, one `provider/model` per line.
pub fn opencode_catalog(text: &str) -> ModelCatalog {
    let fallback = fallback("opencode");
    let mut models = vec![fallback.models[0].clone()];
    let mut seen: HashSet<String> = HashSet::from(["default".to_string()]);
    for line in text.split('\n').take(4_096) {
        let value = line.trim_matches([' ', '\t', '\r']);
        let slash = value.find('/');
        let Some(slash) = slash.filter(|s| *s > 0 && *s != value.len() - 1) else { continue };
        if value.contains(' ') || !wire::model(value) || models.len() > listed_model_limit("opencode") || !seen.insert(value.to_string()) {
            continue;
        }
        models.push(ModelOption::new(
            value,
            value,
            &strings::get_with("provider.catalog.opencodeModelRow", &[("provider", &value[..slash])]),
        ));
    }
    if models.len() > 1 { ModelCatalog::new("cli", models, &strings::get("provider.catalog.opencodeSource")) } else { fallback }
}

/// `ProviderCatalog.ClaudeHeadlessArguments`: one stream-json message in, no tools, no MCP, no saved session.
pub const CLAUDE_HEADLESS_ARGUMENTS: [&str; 17] = [
    "--print",
    "--verbose",
    "--input-format",
    "stream-json",
    "--output-format",
    "stream-json",
    "--no-session-persistence",
    "--permission-mode",
    "dontAsk",
    "--permission-prompts",
    "none",
    "--strict-mcp-config",
    "--mcp-config",
    "{\"mcpServers\":{}}",
    "--tools",
    "",
    "--safe-mode",
];

/// `ProviderCatalog.ModelListArguments`: what each CLI's model-list read runs (checked against the real CLI).
pub fn model_list_arguments(provider: &str) -> Option<Vec<String>> {
    let list: &[&str] = match provider {
        "claude" => &CLAUDE_HEADLESS_ARGUMENTS,
        "codex" => &["app-server", "--listen", "stdio://"],
        "gemini" => &["models"],
        "pi" => &["--list-models"],
        "opencode" => &["models", "--standalone"],
        _ => return None,
    };
    Some(list.iter().map(|s| s.to_string()).collect())
}

/// `ProviderCatalog.ModelListCommand`: the command a failed read names (Claude's long launch is just `claude`).
pub fn model_list_command(provider: &str) -> String {
    let executable = executable(provider);
    match model_list_arguments(provider) {
        Some(arguments) if provider != "claude" => format!("{executable} {}", arguments.join(" ")),
        _ => executable.to_string(),
    }
}

/// `ProviderCatalog.TellingLine`: the last line naming an error, else the first that says anything.
pub fn telling_line(output: &str) -> Option<String> {
    let all: Vec<&str> = output.split(['\r', '\n']).filter(|l| !l.is_empty()).collect();
    let skip = all.len().saturating_sub(200);
    let lines: Vec<String> = all.into_iter().skip(skip).map(|line| clean(Some(line), 240, true)).filter(|l| !l.is_empty()).collect();
    lines.iter().rev().find(|l| l.to_lowercase().contains("error")).or_else(|| lines.first()).cloned()
}

fn replace_ignore_case(text: &str, from: &str, to: &str) -> String {
    let lower_text = text.to_lowercase();
    let lower_from = from.to_lowercase();
    if lower_text.len() != text.len() || lower_from.len() != from.len() || from.is_empty() {
        return text.replace(from, to);
    }
    let mut out = String::new();
    let mut at = 0;
    while let Some(found) = lower_text[at..].find(&lower_from) {
        out.push_str(&text[at..at + found]);
        out.push_str(to);
        at += found + from.len();
    }
    out.push_str(&text[at..]);
    out
}

/// `ProviderCatalog.ReadFailure`: the command, what went wrong, the output's telling line; the home folder reads `~`.
pub fn read_failure(provider: &str, problem: &str, output: &str) -> String {
    let mut text = format!("{}: {problem}", model_list_command(provider));
    if let Some(line) = telling_line(output) {
        text.push_str(" · ");
        text.push_str(&line);
    }
    if let Some(home) = dirs_home().filter(|h| h.len() > 1 && h != "/") {
        text = if os::windows() { replace_ignore_case(&text, &home, "~") } else { text.replace(&home, "~") };
    }
    clean(Some(&text), 400, true)
}

fn dirs_home() -> Option<String> {
    std::env::var(if os::windows() { "USERPROFILE" } else { "HOME" }).ok().filter(|h| !h.is_empty())
}

fn regex(pattern: &str) -> Regex {
    Regex::new(pattern).expect("pattern")
}

/// `ProviderCatalog.SilentHaiku`: a Haiku model the CLI says nothing about takes no effort.
pub fn silent_haiku(model: &str, supports_effort: Option<bool>, levels: Option<&[String]>) -> bool {
    supports_effort.is_none() && levels.is_none() && model.to_lowercase().contains("haiku")
}

/// `ProviderCatalog.Efforts`: the efforts a model takes besides "default".
pub fn efforts(provider: &str, model: &str, catalog: &ModelCatalog, registered: Option<&[RegisteredModelEntry]>) -> Vec<String> {
    let owned = |list: &[&str]| list.iter().map(|s| s.to_string()).collect::<Vec<_>>();
    let exact = catalog.models.iter().find(|m| m.value == model);
    match provider {
        "gemini" => return vec![],
        "pi" => return if exact.and_then(|m| m.supports_effort) == Some(false) { vec![] } else { owned(&wire::PI_THINKING_LEVELS) },
        "opencode" => {
            return if model == "default" || exact.and_then(|m| m.supports_effort) == Some(false) {
                vec![]
            } else {
                owned(&["low", "medium", "high", "max"])
            };
        }
        "hermes" => return owned(&wire::EFFORTS),
        _ => {}
    }
    let row = catalog.models.iter().find(|m| m.value == model || m.resolved_model.as_deref() == Some(model));
    if row.and_then(|r| r.supports_effort) == Some(false) {
        return vec![];
    }
    if let Some(levels) = row.and_then(|r| r.supported_effort_levels.as_ref()) {
        let known: &[&str] = if provider == "codex" { &wire::CODEX_EFFORTS } else { &wire::EFFORTS };
        let mut seen = HashSet::new();
        return levels.iter().filter(|l| known.contains(&l.as_str()) && seen.insert(l.to_string())).cloned().collect();
    }
    if row.is_none()
        && let Some(registered) = registered
        && let Some(entry) = registered.iter().find(|r| r.name.as_deref() == Some(model))
    {
        if !entry.supports_effort {
            return vec![];
        }
        return entry.supported_effort_levels.iter().flatten().flatten().filter(|l| wire::EFFORTS.contains(&l.as_str())).cloned().collect();
    }
    if provider == "codex"
        || silent_haiku(model, row.and_then(|r| r.supports_effort), row.and_then(|r| r.supported_effort_levels.as_deref()))
    {
        return vec![];
    }
    if regex(r"(opus|sonnet)[-.]4[-.]6").is_match(model) {
        return owned(&["low", "medium", "high", "max"]);
    }
    if regex(r"^(default|best|fable|opus|sonnet|opusplan)(\[1m\])?$|fable[-.]5|opus[-.](5|4[-.][78])|sonnet[-.]5").is_match(model) {
        return owned(&wire::EFFORTS);
    }
    if row.and_then(|r| r.supports_effort) == Some(true) { owned(&["low", "medium", "high"]) } else { vec![] }
}

/// `ProviderCatalog.EffortRefusal`: why a run's effort cannot go with its model, or None.
pub fn effort_refusal(
    provider: &str,
    model: &str,
    effort: &str,
    catalog: &ModelCatalog,
    registered: Option<&[RegisteredModelEntry]>,
) -> Option<String> {
    if effort == "default" {
        return None;
    }
    let levels = efforts(provider, model, catalog, registered);
    if levels.iter().any(|l| l == effort) {
        return None;
    }
    Some(strings::get(if provider == "claude" && levels.is_empty() && model.to_lowercase().contains("haiku") {
        "validation.run.haikuEffort"
    } else {
        "run.error.effortNotSupported"
    }))
}

/// The first `x.y.z` in `version` with no digit before it and no digit or `-` after it (`(?<!\d)(\d+)\.(\d+)\.(\d+)(?![\d-])`).
fn version_triple(version: Option<&str>) -> Option<(u64, u64, u64)> {
    let bytes = version?.as_bytes();
    let digits = |at: usize| bytes[at..].iter().take_while(|b| b.is_ascii_digit()).count();
    for start in 0..bytes.len() {
        if !bytes[start].is_ascii_digit() || start > 0 && bytes[start - 1].is_ascii_digit() {
            continue;
        }
        let a = digits(start);
        let mut at = start + a;
        if bytes.get(at) != Some(&b'.') {
            continue;
        }
        let b = digits(at + 1);
        if b == 0 {
            continue;
        }
        let second = at + 1;
        at = second + b;
        if bytes.get(at) != Some(&b'.') {
            continue;
        }
        let c = digits(at + 1);
        if c == 0 {
            continue;
        }
        let third = at + 1;
        let end = third + c;
        if bytes.get(end).is_some_and(|x| x.is_ascii_digit() || *x == b'-') {
            continue;
        }
        let parse = |from: usize, length: usize| {
            std::str::from_utf8(&bytes[from..from + length]).ok()?.parse::<u64>().ok().filter(|n| *n <= i32::MAX as u64)
        };
        return Some((parse(start, a)?, parse(second, b)?, parse(third, c)?));
    }
    None
}

/// `ProviderCatalog.SupportsMods`: Claude Code 2.1.271 or later.
pub fn supports_mods(version: Option<&str>) -> bool {
    version_triple(version).is_some_and(|v| v >= (2, 1, 271))
}

/// `ProviderCatalog.SupportsCodexApprovals`: Codex CLI 0.153.4 or later.
pub fn supports_codex_approvals(version: Option<&str>) -> bool {
    version_triple(version).is_some_and(|v| v >= (0, 153, 4))
}

/// `ProviderCatalog.ReadModel`: one row of a CLI's list.
fn read_model(row: &Value, provider: &str) -> Option<ModelOption> {
    let value = meta::text(row, if provider == "codex" { "model" } else { "value" }).filter(|v| wire::model(v))?;
    if matches!(meta::prop(row, "hidden"), Some(Value::Bool(true))) {
        return None;
    }
    let known: &[&str] = if provider == "codex" { &wire::CODEX_EFFORTS } else { &wire::EFFORTS };
    let mut efforts: Option<Vec<String>> =
        match meta::prop(row, if provider == "codex" { "supportedReasoningEfforts" } else { "supportedEffortLevels" }) {
            Some(Value::Array(levels)) => {
                let mut seen = HashSet::new();
                Some(
                    levels
                        .iter()
                        .filter_map(|l| if provider == "codex" { meta::text(l, "reasoningEffort") } else { l.as_str() })
                        .filter(|s| known.contains(s) && seen.insert(s.to_string()))
                        .map(str::to_string)
                        .collect(),
                )
            }
            _ => None,
        };
    let said = meta::prop(row, "supportsEffort").and_then(Value::as_bool);
    let mut support = said.or_else(|| efforts.as_ref().map(|e| !e.is_empty()));
    if provider == "claude" && silent_haiku(value, said, efforts.as_deref()) {
        support = Some(false);
        efforts = Some(vec![]);
    }
    Some(ModelOption {
        value: value.to_string(),
        display_name: wire::clean(meta::text(row, "displayName").unwrap_or(value), 160),
        description: wire::clean(meta::text(row, "description").unwrap_or(""), 2400),
        resolved_model: meta::text(row, "resolvedModel").filter(|m| wire::model(m)).map(str::to_string),
        supports_effort: support,
        supported_effort_levels: efforts,
    })
}

fn distinct_by_value(models: Vec<ModelOption>) -> Vec<ModelOption> {
    let mut seen = HashSet::new();
    models.into_iter().filter(|m| seen.insert(m.value.clone())).collect()
}

/// `ProviderCatalog.ClaudeCatalog`: Claude's `initialize` answer; its own "default" row only lends the fallback row what it stands
/// for. An answer with no model is the built-in list.
pub fn claude_catalog(rows: &[Value]) -> ModelCatalog {
    let mut models = vec![fallback("claude").models[0].clone()];
    let mut listed = false;
    for row in rows.iter().take(listed_model_limit("claude")) {
        let Some(model) = read_model(row, "claude") else { continue };
        listed = true;
        if model.value == "default" {
            models[0].resolved_model = model.resolved_model;
            models[0].supports_effort = model.supports_effort;
            models[0].supported_effort_levels = model.supported_effort_levels;
        } else {
            models.push(model);
        }
    }
    if listed { ModelCatalog::new("cli", distinct_by_value(models), &strings::get("provider.claudeCliSource")) } else { fallback("claude") }
}

/// `ProviderCatalog.AddCodexModels`: Codex's rows; the one it marks `isDefault` lends the default row its model.
pub fn add_codex_models(models: &mut Vec<ModelOption>, data: &[Value]) {
    let room = (1 + listed_model_limit("codex")).saturating_sub(models.len());
    for row in data.iter().take(room) {
        let Some(model) = read_model(row, "codex") else { continue };
        models.push(model.clone());
        if models[0].value == "default" && matches!(meta::prop(row, "isDefault"), Some(Value::Bool(true))) {
            models[0].resolved_model = Some(model.value.clone());
            models[0].supports_effort = model.supports_effort;
            models[0].supported_effort_levels = model.supported_effort_levels.clone();
        }
    }
}

fn codex_from(models: Vec<ModelOption>) -> ModelCatalog {
    if models.len() > 1 {
        ModelCatalog::new("cli", distinct_by_value(models), &strings::get("provider.codexCliSource"))
    } else {
        fallback("codex")
    }
}

/// `ProviderCatalog.CodexCatalog`: Codex's `model/list` rows.
pub fn codex_catalog(data: &[Value]) -> ModelCatalog {
    let mut models = vec![fallback("codex").models[0].clone()];
    add_codex_models(&mut models, data);
    codex_from(models)
}

// ── a run's arguments ───────────────────────────────────────────────────────────────────────────────────────────────────

static AGENT_FILES: RwLock<Option<PathBuf>> = RwLock::new(None);

/// `AgentFiles.Directory`: where the app's shipped agent extensions are (by default `Agents` beside the executable).
pub fn set_agent_files(directory: Option<PathBuf>) {
    if let Ok(mut current) = AGENT_FILES.write() {
        *current = directory;
    }
}

/// `AgentFiles.Find`.
pub fn agent_file(name: &str) -> Option<String> {
    let directory = AGENT_FILES
        .read()
        .ok()
        .and_then(|d| d.clone())
        .or_else(|| std::env::current_exe().ok().and_then(|exe| exe.parent().map(|p| p.join("Agents"))))?;
    let path = directory.join(name);
    path.is_file().then(|| path.to_string_lossy().into_owned())
}

/// pi's question extension and its tool (`PiAgent.QuestionExtensionName`, `QuestionTool`).
pub const PI_QUESTION_EXTENSION: &str = "pi-ask-user.js";
pub const PI_QUESTION_TOOL: &str = "ask_user";

/// `ProviderCatalog.Arguments` (ProviderCatalog.cs:623-720): the arguments after the executable and its prefix, for every provider.
pub fn arguments(
    value: &StartRunRequest,
    plugin_directory: &str,
    phase_models: Option<&mighty_core::model::PhaseModelsSnapshot>,
) -> Result<Vec<String>> {
    let request = value.validate()?;
    let settings = request.settings();
    let mut args: Vec<String> = Vec::new();
    let push = |args: &mut Vec<String>, items: &[&str]| args.extend(items.iter().map(|s| s.to_string()));
    let phase = phase_models::normalized(phase_models.or(request.phase_models.as_ref()));
    let model_or_default = |m: &Option<String>| m.clone().unwrap_or_else(|| "default".into());
    match request.provider.as_str() {
        "claude" => {
            let mode = request.permission_mode_override.clone().unwrap_or_else(|| settings.permission_mode.clone());
            let mode = if mode == "fullAccess" { "bypassPermissions".to_string() } else { mode };
            push(
                &mut args,
                &["--print", "--verbose", "--output-format", "stream-json", "--permission-prompts", "none", "--permission-mode"],
            );
            args.push(mode);
            args.push("--plugin-dir".into());
            args.push(plugin_directory.into());
            let mut allowed: Vec<String> = Vec::new();
            for tool in request.style_auto_allow.iter().flatten().map(String::as_str).chain(crate::flow_steps::TASK_TOOLS) {
                if !allowed.iter().any(|a| a == tool) {
                    allowed.push(tool.to_string());
                }
            }
            args.push("--allowedTools".into());
            args.push(allowed.join(","));
            let mut env = serde_json::Map::new();
            if settings.effort != "default" {
                args.push("--effort".into());
                args.push(settings.effort.clone());
                env.insert("CLAUDE_CODE_EFFORT_LEVEL".into(), Value::String(settings.effort.clone()));
            }
            if let Some(phase) = &phase {
                for (key, value) in [
                    ("ANTHROPIC_DEFAULT_OPUS_MODEL", model_or_default(&phase.claude_opus_alias)),
                    ("ANTHROPIC_DEFAULT_SONNET_MODEL", model_or_default(&phase.claude_sonnet_alias)),
                    ("ANTHROPIC_DEFAULT_HAIKU_MODEL", model_or_default(&phase.claude_haiku_alias)),
                    ("CLAUDE_CODE_SUBAGENT_MODEL", model_or_default(&phase.claude_subagent_default)),
                ] {
                    if value != "default" {
                        env.insert(key.into(), Value::String(value));
                    }
                }
            }
            if !env.is_empty() {
                args.push("--settings".into());
                args.push(text_json::wire(&json!({"env": Value::Object(env)})));
            }
            let claude_main = phase.as_ref().map(|p| model_or_default(&p.claude_main));
            if request.model == "default" && (claude_main.as_deref().is_some_and(|m| m != "default") || request.resume_id.is_some()) {
                args.push("--model".into());
                args.push(claude_main.unwrap_or_else(|| "default".into()));
            }
            args.push("--append-system-prompt".into());
            args.push(crate::flow_steps::PLAN_INSTRUCTION.into());
            if let Some(turns) = settings.max_turns {
                args.push("--max-turns".into());
                args.push(turns.to_string());
            }
            if let Some(budget) = settings.max_budget_usd {
                args.push("--max-budget-usd".into());
                args.push(format_double(budget));
            }
        }
        "codex" => {
            let approvals = settings.permission_mode == "onRequest";
            let sandbox = match settings.permission_mode.as_str() {
                "fullAccess" => "danger-full-access",
                "acceptEdits" | "onRequest" => "workspace-write",
                _ => "read-only",
            };
            let flag = |b: bool| if b { "true" } else { "false" };
            for setting in [
                format!("approval_policy=\"{}\"", if approvals { "on-request" } else { "never" }),
                format!("sandbox_mode=\"{sandbox}\""),
                format!("sandbox_workspace_write.network_access={}", flag(settings.network_access)),
                format!("features.fast_mode={}", flag(settings.fast_mode)),
                format!("service_tier=\"{}\"", if settings.fast_mode { "fast" } else { "default" }),
            ] {
                args.push("-c".into());
                args.push(setting);
            }
            if settings.web_search != "default" {
                args.push("-c".into());
                args.push(format!("web_search=\"{}\"", settings.web_search));
            }
            if sandbox == "workspace-write"
                && let Some(root) = &request.delegation_worktree_root
            {
                args.push("-c".into());
                args.push(format!("sandbox_workspace_write.writable_roots={}", text_json::wire(&json!([root]))));
            }
            if settings.effort != "default" {
                args.push("-c".into());
                args.push(format!("model_reasoning_effort=\"{}\"", settings.effort));
            }
            if let Some(phase) = &phase {
                let review = model_or_default(&phase.codex_review_model);
                if review != "default" {
                    args.push("-c".into());
                    args.push(format!("review_model=\"{review}\""));
                }
                let subagent = model_or_default(&phase.codex_subagent_default);
                if subagent != "default" {
                    args.push("-c".into());
                    args.push(format!("agents.default_subagent_model=\"{subagent}\""));
                }
                if let Some(effort) = &phase.codex_subagent_effort {
                    args.push("-c".into());
                    args.push(format!("agents.default_subagent_reasoning_effort=\"{effort}\""));
                }
                let plan_effort = model_or_default(&phase.codex_plan_mode_reasoning_effort);
                if plan_effort != "default" {
                    args.push("-c".into());
                    args.push(format!("plan_mode_reasoning_effort=\"{plan_effort}\""));
                }
            }
            if approvals {
                push(&mut args, &["-c", "approvals_reviewer=\"user\""]);
                if request.model != "default" {
                    args.push("-c".into());
                    args.push(format!("model=\"{}\"", request.model));
                }
                push(&mut args, &["app-server", "--listen", "stdio://"]);
            } else {
                args.push("exec".into());
                if let Some(resume) = &request.resume_id {
                    args.push("resume".into());
                    args.push(resume.clone());
                }
                push(&mut args, &["--json", "--skip-git-repo-check"]);
            }
        }
        "pi" => {
            push(&mut args, &["--mode", "rpc"]);
            if request.model != "default" {
                args.push("--model".into());
                args.push(request.model.clone());
            }
            if settings.effort != "default" {
                args.push("--thinking".into());
                args.push(settings.effort.clone());
            }
            let questions = agent_file(PI_QUESTION_EXTENSION);
            if settings.permission_mode != "fullAccess" {
                args.push("--tools".into());
                args.push(format!("read,grep,find,ls{}", if questions.is_some() { format!(",{PI_QUESTION_TOOL}") } else { String::new() }));
            }
            if let Some(questions) = questions {
                args.push("--extension".into());
                args.push(questions);
            }
            if let Some(resume) = &request.resume_id {
                args.push("--session".into());
                args.push(resume.clone());
            }
            return Ok(args);
        }
        provider if is_acp(provider) => {
            args.push("acp".into());
            return Ok(args);
        }
        _ => {
            push(&mut args, &["--print=", "--input-format", "stream-json", "--output-format", "stream-json", "--print-timeout", "0"]);
            match settings.permission_mode.as_str() {
                "plan" => push(&mut args, &["--mode", "plan"]),
                "acceptEdits" => push(&mut args, &["--mode", "accept-edits"]),
                "fullAccess" => args.push("--dangerously-skip-permissions".into()),
                _ => {}
            }
        }
    }
    let on_request = request.provider == "codex" && settings.permission_mode == "onRequest";
    if request.model != "default" && !on_request {
        args.push("--model".into());
        args.push(request.model.clone());
    }
    if request.provider != "codex"
        && let Some(resume) = &request.resume_id
    {
        args.push(if request.provider == "gemini" { "--conversation" } else { "--resume" }.into());
        args.push(resume.clone());
    }
    if request.provider == "gemini"
        && let Some(added) = &request.delegation_worktree_root
    {
        args.push("--add-dir".into());
        args.push(added.clone());
    }
    if request.provider == "codex" && !on_request {
        args.push("-".into());
    }
    Ok(args)
}

// ── the CLIs on this computer ───────────────────────────────────────────────────────────────────────────────────────────

/// `ProviderCatalog.QuietEnvironment`: the CLI environment with the auto-updater, error reports and update notices off.
/// `DISABLE_TELEMETRY` is left out on purpose: with it Claude Code answers with its built-in model list.
pub fn quiet_environment(environment: &dyn ShellEnvironment) -> BTreeMap<String, String> {
    let mut values = environment.current().values;
    for key in ["DISABLE_AUTOUPDATER", "DISABLE_ERROR_REPORTING", "NO_UPDATE_NOTIFIER"] {
        values.insert(key.into(), "1".into());
    }
    values
}

/// `ProviderCatalog.ClaudeHeadlessEnvironment`.
pub fn claude_headless_environment(environment: &dyn ShellEnvironment) -> BTreeMap<String, String> {
    let mut values = quiet_environment(environment);
    for key in [
        "CLAUDE_CODE_SAFE_MODE",
        "CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL",
        "CLAUDE_CODE_DISABLE_BACKGROUND_TASKS",
        "CLAUDE_CODE_SKIP_PROMPT_HISTORY",
    ] {
        values.insert(key.into(), "1".into());
    }
    values
}

/// `ChildProcess.StartInfo(binary, args, cwd, env)`: the program started directly.
fn direct_request(binary: &str, args: Vec<String>, cwd: &Path, env: &BTreeMap<String, String>) -> SpawnRequest {
    let mut request = SpawnRequest::new(binary, args, cwd);
    for (name, value) in env {
        request.env.insert(name.clone(), value.clone());
    }
    request.env.insert("NO_COLOR".into(), "1".into());
    request
}

/// `ProviderCatalog.ReadBoundedAsync`: the text of a pipe, failing past `limit` characters.
async fn read_bounded(reader: Option<ChildReader>, limit: usize) -> std::result::Result<String, ()> {
    let Some(mut reader) = reader else { return Ok(String::new()) };
    let mut bytes = Vec::new();
    let mut buffer = [0u8; 4096];
    loop {
        let read = reader.read(&mut buffer).await.map_err(|_| ())?;
        if read == 0 {
            break;
        }
        bytes.extend_from_slice(&buffer[..read]);
        if bytes.len() > limit.saturating_mul(4) {
            return Err(());
        }
    }
    let text = String::from_utf8_lossy(&bytes).into_owned();
    if text.encode_utf16().count() > limit { Err(()) } else { Ok(text) }
}

/// `ErrorTail`: a CLI's stderr read to its end on a task of its own, its last 8 KiB kept.
struct ErrorTail {
    text: Arc<Mutex<String>>,
    done: tokio::sync::watch::Receiver<bool>,
}

impl ErrorTail {
    fn start(reader: Option<ChildReader>) -> ErrorTail {
        let text = Arc::new(Mutex::new(String::new()));
        let (sender, done) = tokio::sync::watch::channel(false);
        let kept = text.clone();
        tokio::spawn(async move {
            if let Some(mut reader) = reader {
                let mut buffer = [0u8; 4096];
                while let Ok(read) = reader.read(&mut buffer).await {
                    if read == 0 {
                        break;
                    }
                    let mut text = lock(&kept);
                    text.push_str(&String::from_utf8_lossy(&buffer[..read]));
                    let length = text.chars().count();
                    if length > 8192 {
                        let cut: String = text.chars().skip(length - 8192).collect();
                        *text = cut;
                    }
                }
            }
            let _ = sender.send(true);
        });
        ErrorTail { text, done }
    }

    /// What the CLI wrote there, once it closed it or a second passed.
    async fn text(&mut self) -> String {
        let _ = tokio::time::timeout(Duration::from_secs(1), self.done.wait_for(|done| *done)).await;
        lock(&self.text).clone()
    }
}

pub type Finder = Arc<dyn Fn(String) -> BoxFuture<'static, Option<CliCommand>> + Send + Sync>;

struct RuntimeCache {
    cached: Option<RuntimeInfo>,
    directory: Option<String>,
    refreshed: Option<Instant>,
}

/// C# `ProviderCatalog`, the part that finds the CLIs and reads their model lists.
pub struct ProviderCatalog {
    processes: Arc<dyn ProcessHost>,
    environment: Arc<dyn ShellEnvironment>,
    finder: Option<Finder>,
    commands: Mutex<HashMap<String, Option<CliCommand>>>,
    overrides: Mutex<HashMap<String, CliCommand>>,
    gate: tokio::sync::Mutex<RuntimeCache>,
    /// `storageDirectory`: where the last list each CLI gave is kept ([`kept_lists::FILE_NAME`]); None keeps them in memory only.
    storage: Option<PathBuf>,
    /// `keptLists` (loaded from the storage folder on first use) and `keptWrittenAt`, under C#'s `keptGate`.
    kept: Mutex<Kept>,
}

#[derive(Default)]
struct Kept {
    lists: Option<KeptModelLists>,
    written_at: Option<mighty_core::time::DateTimeOffset>,
}

impl Kept {
    /// `LoadedKeptLists()`: the kept lists, read from the storage folder the first time.
    fn loaded(&mut self, storage: Option<&Path>) -> &mut KeptModelLists {
        self.lists.get_or_insert_with(|| KeptModelLists::load(KeptModelLists::file_path(storage).as_deref()))
    }
}

impl ProviderCatalog {
    /// `finder`: finds a provider's CLI (the checks' fake); None looks along the CLI environment's PATH and the installers' folders.
    pub fn new(processes: Arc<dyn ProcessHost>, environment: Arc<dyn ShellEnvironment>, finder: Option<Finder>) -> ProviderCatalog {
        ProviderCatalog {
            processes,
            environment,
            finder,
            commands: Mutex::new(HashMap::new()),
            overrides: Mutex::new(HashMap::new()),
            gate: tokio::sync::Mutex::new(RuntimeCache { cached: None, directory: None, refreshed: None }),
            storage: None,
            kept: Mutex::new(Kept::default()),
        }
    }

    /// C#'s `storageDirectory`: the folder the last list each CLI gave is kept in (the profile folder, or this machine's own when the
    /// app may not write its profile); None (the checks, helpers) keeps the lists in memory only.
    pub fn with_storage(mut self, directory: Option<PathBuf>) -> ProviderCatalog {
        self.storage = directory;
        self
    }

    /// The storage folder [`with_storage`](Self::with_storage) gave.
    pub fn storage_directory(&self) -> Option<&Path> {
        self.storage.as_deref()
    }

    /// `HermesModelCache`: `hermes-models.json` in the storage folder, beside the kept model lists as C# keeps both; none without one,
    /// so a checks' fake Hermes never writes over a user's.
    pub fn hermes_model_cache(&self) -> Option<PathBuf> {
        self.storage_directory().map(|folder| folder.join(crate::acp::agents::HERMES_MODEL_CACHE_FILE))
    }

    pub fn processes(&self) -> Arc<dyn ProcessHost> {
        self.processes.clone()
    }

    pub fn environment(&self) -> Arc<dyn ShellEnvironment> {
        self.environment.clone()
    }

    /// `ProviderCatalog.QuietEnvironment` of this catalog's CLI environment.
    pub fn quiet_environment(&self) -> BTreeMap<String, String> {
        quiet_environment(self.environment.as_ref())
    }

    /// `ProviderCatalog.Override`: `provider` found as `command` until set back with None.
    pub fn set_override(&self, provider: &str, command: Option<CliCommand>) {
        let mut overrides = lock(&self.overrides);
        match command {
            None => {
                overrides.remove(provider);
            }
            Some(command) => {
                overrides.insert(provider.into(), command);
            }
        }
        lock(&self.commands).remove(provider);
    }

    /// `ProviderCatalog.RefreshEnvironmentAsync`: the CLI environment, captured again when `force` (a fake finder reads the current one).
    pub async fn refresh_environment(&self, force: bool) -> BTreeMap<String, String> {
        if self.finder.is_none() { self.environment.resolve(None, force).await.values } else { self.environment.current().values }
    }

    /// `ProviderCatalog.FindAsync`.
    pub async fn find(&self, provider: &str) -> Option<CliCommand> {
        if let Some(forced) = lock(&self.overrides).get(provider).cloned() {
            return Some(forced);
        }
        self.refresh_environment(false).await;
        if let Some(saved) = lock(&self.commands).get(provider).cloned() {
            return saved;
        }
        let result = match &self.finder {
            Some(finder) => finder(provider.to_string()).await,
            None => self.discover(provider).await,
        };
        lock(&self.commands).insert(provider.into(), result.clone());
        result
    }

    /// `ProviderCatalog.StartCli`: a CLI started with its pipes (a `.cmd` shim through cmd.exe on Windows).
    pub async fn start_cli(
        &self,
        binary: &str,
        args: &[String],
        cwd: &Path,
        env: &BTreeMap<String, String>,
    ) -> Result<mighty_platform::Child> {
        let request = self.processes.cli_request(Path::new(binary), args, cwd, env)?;
        Ok(self.processes.spawn(request).await?)
    }

    /// `ProviderCatalog.DiscoverAsync`: the provider's CLI along the PATH and the installers' folders, the first that answers
    /// `--version` (4 s) with exit 0.
    async fn discover(&self, provider: &str) -> Option<CliCommand> {
        let environment = self.environment.current().values;
        let windows = os::windows();
        let home = dirs_home().unwrap_or_default();
        let path =
            environment.iter().find(|(k, _)| if windows { k.eq_ignore_ascii_case("PATH") } else { *k == "PATH" }).map(|(_, v)| v.clone());
        let mut directories: Vec<String> = path.unwrap_or_default().split(os::path_separator()).map(str::to_string).collect();
        directories.push(os::combine(&os::combine(&home, ".local"), "bin"));
        directories.extend(["/opt/homebrew/bin".to_string(), "/usr/local/bin".to_string()]);
        if let Ok(roaming) = std::env::var("APPDATA") {
            directories.push(os::combine(&roaming, "npm"));
        }
        let mut seen = HashSet::new();
        let mut directories: Vec<String> = directories.into_iter().filter(|d| !d.trim().is_empty() && seen.insert(d.clone())).collect();
        if provider == "gemini" && windows {
            let local = environment.get("LOCALAPPDATA").cloned().or_else(|| std::env::var("LOCALAPPDATA").ok()).unwrap_or_default();
            directories.push(os::combine(&os::combine(&local, "agy"), "bin"));
        }
        let mut candidates: Vec<CliCommand> =
            cli_files(provider, &directories, windows).into_iter().map(|p| CliCommand::new(p, vec![], None)).collect();
        if windows && provider == "claude" {
            for directory in &directories {
                let exe = ["node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"]
                    .iter()
                    .fold(directory.clone(), |a, b| os::combine(&a, b));
                candidates.push(CliCommand::new(exe, vec![], None));
            }
        }
        let temp = std::env::temp_dir();
        for candidate in candidates.into_iter().filter(|c| Path::new(&c.binary).is_file()) {
            let mut args = candidate.prefix.clone();
            args.push("--version".into());
            let Ok(mut child) = self.start_cli(&candidate.binary, &args, &temp, &self.quiet_environment()).await else { continue };
            drop(child.stdin.take());
            let control = child.control();
            let (stdout, stderr) = (child.stdout.take(), child.stderr.take());
            let waiting = control.clone();
            let outcome = tokio::time::timeout(Duration::from_secs(4), async move {
                let drain = async move {
                    if let Some(mut reader) = stderr {
                        let mut buffer = [0u8; 4096];
                        while let Ok(read) = reader.read(&mut buffer).await {
                            if read == 0 {
                                break;
                            }
                        }
                    }
                };
                let (text, code, _) = tokio::join!(read_bounded(stdout, 16384), waiting.wait(), drain);
                (text, code)
            })
            .await;
            let _ = control.close().await;
            let Ok((Ok(text), Ok(0))) = outcome else { continue };
            let version = version_text(executable(provider), &text);
            if !version.is_empty() {
                return Some(CliCommand { version: Some(wire::clean(&version, 160)), ..candidate });
            }
        }
        None
    }

    /// `ProviderCatalog.ReadModelsAsync`: Claude's `initialize` answer, or Codex's app-server `initialize` then `model/list` (7 s
    /// unless `limit`); a CLI that ends first, refuses, times out, says too much or lists no model is a failed read.
    pub async fn read_models(
        &self,
        provider: &str,
        command: &CliCommand,
        working_directory: Option<&str>,
        limit: Option<Duration>,
    ) -> ModelCatalog {
        let no_models = strings::get("provider.catalog.failure.noModels");
        let Some(args) = model_list_arguments(provider) else {
            return fallback(provider).with_failure(read_failure(provider, &no_models, ""));
        };
        let env = if provider == "claude" { claude_headless_environment(self.environment.as_ref()) } else { self.quiet_environment() };
        let directory = working_directory.map(str::to_string).or_else(dirs_home).unwrap_or_default();
        let mut all = command.prefix.clone();
        all.extend(args);
        let request = direct_request(&command.binary, all, Path::new(&directory), &env);
        let child = match self.processes.spawn(request).await {
            Ok(child) => child,
            Err(error) => return fallback(provider).with_failure(read_failure(provider, &error.to_string(), "")),
        };
        let control = child.control();
        let outcome = tokio::time::timeout(limit.unwrap_or(Duration::from_secs(7)), Self::probe(provider, child, &no_models)).await;
        let _ = control.close().await;
        match outcome {
            Ok(Ok(catalog)) => catalog,
            Ok(Err((problem, output))) => fallback(provider).with_failure(read_failure(provider, &problem, &output)),
            Err(_) => fallback(provider).with_failure(read_failure(provider, &strings::get("process.error.timeout"), "")),
        }
    }

    async fn probe(
        provider: &str,
        mut child: mighty_platform::Child,
        no_models: &str,
    ) -> std::result::Result<ModelCatalog, (String, String)> {
        let mut errors = ErrorTail::start(child.stderr.take());
        let mut stdin = child.stdin.take();
        let new_line = os::new_line();
        // A CLI that already ended takes no more input: what it wrote and its exit code say why.
        async fn send(stdin: &mut Option<mighty_platform::process::ChildWriter>, line: String) {
            if let Some(pipe) = stdin.as_mut()
                && (pipe.write_all(line.as_bytes()).await.is_err() || pipe.flush().await.is_err())
            {
                *stdin = None;
            }
        }
        let request_id = wire::new_id();
        let mut expected: i64 = 1;
        let mut pages = 0;
        let mut models = vec![fallback(provider).models[0].clone()];
        let first = if provider == "codex" {
            json!({"id": expected, "method": "initialize", "params": {"clientInfo": {"name": "mighty_claude", "title": "Mighty Code", "version": "0.1.0"}}})
        } else {
            json!({"type": "control_request", "request_id": request_id, "request": {"subtype": "initialize"}})
        };
        send(&mut stdin, format!("{}{new_line}", text_json::wire(&first))).await;
        let mut lines = Lines::new(child.stdout.take(), 1024 * 1024);
        let mut total = 0usize;
        let mut answer: Option<String> = None;
        loop {
            let line = match lines.next().await {
                Next::Line(line) => line,
                Next::TooLong => continue,
                Next::End => break,
            };
            total += line.encode_utf16().count();
            if total > 2 * 1024 * 1024 {
                answer = Some(strings::get("process.error.outputLimit"));
                break;
            }
            let Ok(root) = serde_json::from_str::<Value>(&line) else { continue };
            if provider == "claude" {
                let response = meta::prop(&root, "response").filter(|r| r.is_object());
                let (Some("control_response"), Some(response)) = (meta::text(&root, "type"), response) else { continue };
                if meta::text(response, "request_id") != Some(request_id.as_str()) {
                    continue;
                }
                if meta::text(response, "subtype") != Some("success") {
                    answer = Some(meta::text(response, "error").filter(|e| !e.is_empty()).unwrap_or(no_models).to_string());
                    break;
                }
                let rows = meta::prop(response, "response")
                    .filter(|p| p.is_object())
                    .and_then(|p| meta::prop(p, "models"))
                    .and_then(Value::as_array);
                let Some(rows) = rows else {
                    answer = Some(no_models.to_string());
                    break;
                };
                let catalog = claude_catalog(rows);
                if catalog.is_fresh() {
                    return Ok(catalog);
                }
                answer = Some(no_models.to_string());
                break;
            }
            if meta::prop(&root, "id").and_then(Value::as_i64).filter(|n| i32::try_from(*n).is_ok()) != Some(expected) {
                continue;
            }
            if let Some(error) = meta::prop(&root, "error") {
                answer = Some(meta::text(error, "message").filter(|m| !m.is_empty()).unwrap_or(no_models).to_string());
                break;
            }
            if expected == 1 {
                send(&mut stdin, format!("{{\"method\":\"initialized\"}}{new_line}")).await;
                expected += 1;
                send(
                    &mut stdin,
                    format!(
                        "{}{new_line}",
                        text_json::wire(&json!({"id": expected, "method": "model/list", "params": {"limit": 64, "includeHidden": false}}))
                    ),
                )
                .await;
            } else if let Some(result) = meta::prop(&root, "result").filter(|r| r.is_object())
                && let Some(data) = meta::prop(result, "data").and_then(Value::as_array)
            {
                add_codex_models(&mut models, data);
                pages += 1;
                let cursor = meta::text(result, "nextCursor").filter(|c| !c.is_empty() && c.encode_utf16().count() < 4096);
                if pages < 4 && models.len() < 1 + listed_model_limit("codex") && cursor.is_some() {
                    expected += 1;
                    let next =
                        json!({"id": expected, "method": "model/list", "params": {"limit": 64, "includeHidden": false, "cursor": cursor}});
                    send(&mut stdin, format!("{}{new_line}", text_json::wire(&next))).await;
                } else {
                    let catalog = codex_from(std::mem::take(&mut models));
                    if catalog.is_fresh() {
                        return Ok(catalog);
                    }
                    answer = Some(no_models.to_string());
                    break;
                }
            } else {
                answer = Some(no_models.to_string());
                break;
            }
        }
        if let Some(answer) = answer {
            return Err((answer, String::new()));
        }
        // The CLI closed its output before it answered: it ended, and its exit code and its last error line say why.
        let control = child.control();
        drop(child);
        let code = control.wait().await.unwrap_or(-1);
        Err((strings::get_with("provider.catalog.failure.exit", &[("code", &code.to_string())]), errors.text().await))
    }

    /// `ProviderCatalog.ReadListCommandAsync`: `agy models`, `pi --list-models`, `opencode models --standalone` (15 s, opencode 20 s).
    pub async fn read_list_command(&self, provider: &str, command: &CliCommand, working_directory: Option<&str>) -> ModelCatalog {
        let Some(arguments) = model_list_arguments(provider) else {
            return fallback(provider).with_failure(read_failure(provider, &strings::get("provider.catalog.failure.noModels"), ""));
        };
        let limit = Duration::from_secs(if provider == "opencode" { 20 } else { 15 });
        let cwd = working_directory.map(PathBuf::from).unwrap_or_else(std::env::temp_dir);
        let mut all = command.prefix.clone();
        all.extend(arguments);
        let mut child = match self.start_cli(&command.binary, &all, &cwd, &self.quiet_environment()).await {
            Ok(child) => child,
            Err(error) => return fallback(provider).with_failure(read_failure(provider, error.message(), "")),
        };
        drop(child.stdin.take());
        let control = child.control();
        let mut errors = ErrorTail::start(child.stderr.take());
        let stdout = child.stdout.take();
        let maximum = match provider {
            "opencode" => 2_097_152,
            "pi" => 1_048_576,
            _ => 262_144,
        };
        let waiting = control.clone();
        let outcome = tokio::time::timeout(limit, async move {
            let text = read_bounded(stdout, maximum).await;
            let code = waiting.wait().await;
            (text, code)
        })
        .await;
        let (problem, output) = match outcome {
            Err(_) => (strings::get("process.error.timeout"), String::new()),
            Ok((Err(()), _)) => (strings::get("process.error.outputLimit"), String::new()),
            Ok((Ok(_), Err(error))) => (error.to_string(), String::new()),
            Ok((Ok(text), Ok(0))) => {
                let catalog = match provider {
                    "pi" => pi_catalog(&text),
                    "opencode" => opencode_catalog(&text),
                    _ => antigravity_catalog(&text),
                };
                if catalog.is_fresh() {
                    let _ = control.close().await;
                    return catalog;
                }
                (strings::get("provider.catalog.failure.noModels"), format!("{text}\n{}", errors.text().await))
            }
            Ok((Ok(text), Ok(code))) => {
                let stderr = errors.text().await;
                (
                    strings::get_with("provider.catalog.failure.exit", &[("code", &code.to_string())]),
                    if stderr.trim().is_empty() { text } else { stderr },
                )
            }
        };
        let _ = control.close().await;
        fallback(provider).with_failure(read_failure(provider, &problem, &output))
    }

    /// `ReadProviderAsync`: one provider's runtime for a folder.
    async fn read_provider(&self, id: &str, directory: &str, workspace: Option<&str>) -> ProviderRuntime {
        let command = self.find(id).await;
        let read = match &command {
            None => {
                fallback(id).with_failure(strings::get_with("run.error.cliExecutableMissing", &[("name", strings::provider_label(id))]))
            }
            // Hermes lists models only inside a session: the picker its last session offered is kept and read here (on a blocking
            // thread: a file read).
            Some(_) if id == "hermes" => {
                let kept = self.hermes_model_cache();
                tokio::task::spawn_blocking(move || crate::acp::agents::hermes_cached_catalog(kept.as_deref()))
                    .await
                    .unwrap_or_else(|_| crate::acp::agents::hermes_cached_catalog(None))
            }
            Some(command) if matches!(id, "gemini" | "pi" | "opencode") => self.read_list_command(id, command, Some(directory)).await,
            Some(command) => self.read_models(id, command, Some(directory), None).await,
        };
        let catalog = self.settle(id, read, workspace).await;
        let version = command.as_ref().and_then(|c| c.version.clone());
        let available = command.is_some() && (id != "claude" || supports_mods(version.as_deref()));
        let detail = if command.is_none() {
            strings::get("provider.notInstalled")
        } else if id == "claude" && !supports_mods(version.as_deref()) {
            strings::get("provider.unsupportedVersion")
        } else {
            strings::get("provider.available")
        };
        ProviderRuntime {
            id: id.into(),
            name: name(id).into(),
            available,
            capabilities: capabilities(id, version.as_deref()),
            version,
            detail,
            model_catalog: catalog,
        }
    }

    /// `SettleAsync`: a list the CLI gave is kept as its newest; a failed read answers with the list kept for this workspace (else the
    /// provider's newest anywhere), or the built-in list, either marked with why the read failed (M/MightyCore/ProviderService.swift
    /// settle). Hermes's picker is its own kept list: one forgotten for a Hermes with no provider is not brought back as an earlier list.
    async fn settle(&self, provider: &str, read: ModelCatalog, workspace: Option<&str>) -> ModelCatalog {
        if provider == "hermes" {
            return read;
        }
        if read.is_fresh() {
            self.keep(&read, provider, workspace).await;
            return read;
        }
        let reason =
            read.read_failure.clone().unwrap_or_else(|| read_failure(provider, &strings::get("provider.catalog.failure.noModels"), ""));
        let entry = lock(&self.kept).loaded(self.storage.as_deref()).list(provider, workspace).cloned();
        match entry {
            Some(entry) => kept_lists::kept(&entry, &reason),
            None => fallback(provider).with_failure(reason),
        }
    }

    /// `KeepAsync`: keeps a list the CLI just gave, writing the file (in the storage folder only) when it changed or is five minutes
    /// old. A write that fails leaves the list kept in memory; the next change writes it.
    async fn keep(&self, catalog: &ModelCatalog, provider: &str, workspace: Option<&str>) {
        let now = mighty_core::time::DateTimeOffset::now();
        let data = {
            let mut kept = lock(&self.kept);
            let written_at = kept.written_at;
            let lists = kept.loaded(self.storage.as_deref());
            (lists.remember(catalog, provider, workspace, now, written_at) && self.storage.is_some()).then(|| lists.encoded())
        };
        let (Some(data), Some(path)) = (data, KeptModelLists::file_path(self.storage.as_deref())) else { return };
        if let Ok(Ok(())) = tokio::task::spawn_blocking(move || kept_lists::write(&path, &data)).await {
            lock(&self.kept).written_at = Some(now);
        }
    }

    fn info(providers: Vec<ProviderRuntime>) -> RuntimeInfo {
        let claude = providers[0].clone();
        RuntimeInfo {
            platform: if os::windows() { "win32".into() } else { "darwin".into() },
            app_version: "0.1.0".into(),
            claude_available: claude.version.is_some(),
            claude_version: claude.version.clone(),
            model_catalog: Some(claude.model_catalog.clone()),
            mods: Some(ModsInfo {
                status: if claude.available {
                    "available"
                } else if claude.version.is_none() {
                    "unavailable"
                } else {
                    "unsupported"
                }
                .into(),
                minimum_version: "2.1.271".into(),
                detail: claude.detail.clone(),
            }),
            providers,
        }
    }

    /// `GetRuntimeAsync`: every provider read for a folder, kept for a minute unless `force` (which looks for every CLI again).
    pub async fn get_runtime(&self, force: bool, working_directory: Option<&str>) -> RuntimeInfo {
        let mut cache = self.gate.lock().await;
        self.refresh_environment(force).await;
        let directory = working_directory.map(str::to_string).or_else(dirs_home).unwrap_or_default();
        if !force
            && cache.directory.as_deref() == Some(directory.as_str())
            && let (Some(cached), Some(at)) = (&cache.cached, cache.refreshed)
            && at.elapsed() < Duration::from_secs(60)
        {
            return cached.clone();
        }
        if force {
            lock(&self.commands).clear();
        }
        let mut providers = Vec::new();
        for id in wire::PROVIDERS {
            providers.push(self.read_provider(id, &directory, working_directory).await);
        }
        let info = Self::info(providers);
        cache.cached = Some(info.clone());
        cache.directory = Some(directory);
        cache.refreshed = Some(Instant::now());
        info
    }

    /// `RefreshProviderAsync`: one provider read again for a folder; a runtime kept for that folder takes the new reading.
    pub async fn refresh_provider(&self, provider: &str, working_directory: Option<&str>, rediscover: bool) -> ProviderRuntime {
        let mut cache = self.gate.lock().await;
        if rediscover {
            lock(&self.commands).remove(provider);
        }
        let directory = working_directory.map(str::to_string).or_else(dirs_home).unwrap_or_default();
        let value = self.read_provider(provider, &directory, working_directory).await;
        if cache.directory.as_deref() == Some(directory.as_str())
            && let Some(cached) = cache.cached.take()
        {
            let providers = cached.providers.into_iter().map(|p| if p.id == value.id { value.clone() } else { p }).collect();
            cache.cached = Some(Self::info(providers));
        }
        value
    }
}

/// `CliDiscovery.Files`: the files tried for `provider` along `directories` (Windows: opencode's binary, npm's copy of it, its shim;
/// Hermes's exe and cmd; pi's `pi.cmd`; any other `<name>.exe`). Each file once.
pub fn cli_files(provider: &str, directories: &[String], windows: bool) -> Vec<String> {
    let name = executable(provider);
    let mut seen = HashSet::new();
    let mut files = Vec::new();
    for folder in directories.iter().filter(|f| !f.is_empty() && !f.contains('\0')) {
        let names: Vec<String> = if !windows {
            vec![os::combine(folder, name)]
        } else {
            match provider {
                "opencode" => vec![
                    os::combine(folder, "opencode.exe"),
                    ["node_modules", "@opencode", "cli", "bin", "opencode.exe"].iter().fold(folder.clone(), |a, b| os::combine(&a, b)),
                    os::combine(folder, "opencode.cmd"),
                ],
                "hermes" => vec![os::combine(folder, "hermes.exe"), os::combine(folder, "hermes.cmd")],
                "pi" => vec![os::combine(folder, "pi.cmd")],
                _ => vec![os::combine(folder, &format!("{name}.exe"))],
            }
        };
        for file in names {
            if seen.insert(if windows { file.to_lowercase() } else { file.clone() }) {
                files.push(file);
            }
        }
    }
    files
}

/// `CliDiscovery.VersionText`: the trimmed `--version` output (Hermes's first non-blank line).
pub fn version_text(program: &str, output: &str) -> String {
    let text = output.trim();
    if program == "hermes" { text.split('\n').map(str::trim).find(|l| !l.is_empty()).unwrap_or("").to_string() } else { text.to_string() }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn versions_read_as_the_pattern_does() {
        assert!(supports_mods(Some("2.1.271 (Claude Code)")));
        assert!(supports_mods(Some("2.1.322")));
        assert!(!supports_mods(Some("2.1.270")));
        assert!(!supports_mods(Some("2.1.300-beta")));
        assert!(!supports_mods(None));
        assert!(supports_codex_approvals(Some("codex-cli 0.167.0")));
        assert!(!supports_codex_approvals(Some("codex-cli 0.153.3")));
    }
}
