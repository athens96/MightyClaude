//! What is each ACP agent's own (AcpAgents.cs): C# `OpencodeAgent` (OpenCode 2, `opencode acp`: its version, the pane's permission mode
//! as its own rules for the run in `OPENCODE_CONFIG_CONTENT`, the settings sent as config options), `HermesAgent` (`hermes acp`: its
//! version line, the pane's mode as the session's edit policy and `HERMES_YOLO_MODE`, the settings, the provider its `initialize` names,
//! its stderr kept out of the pane, the picker its sessions offer kept for the model menu, when a session may be continued) and
//! `HermesQuestionTool` (Hermes's clarify switched on for ACP sessions once per launch through its own `config` command). `AgentFiles`
//! is [`crate::providers::agent_file`].

use std::collections::{BTreeMap, HashMap};
use std::path::Path;
use std::sync::{Arc, Mutex, OnceLock};
use std::time::Duration;

use mighty_core::json::JsonOut;
use mighty_core::wire;
use mighty_platform::ChildControl;
use regex::Regex;
use serde_json::{Map, Value, json};
use tokio::io::AsyncReadExt;

use crate::activity::clean;
use crate::lock;
use crate::model::{CliCommand, ModelCatalog, ModelOption, StartRunRequest};
use crate::providers::{self, ProviderCatalog};
use crate::strings;
use crate::text_json;

/// `SetupProber.VersionNumbers`: the first `1`, `1.2` or `1.2.3` in a version text, as numbers.
pub fn version_numbers(text: &str) -> Option<Vec<i32>> {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    let pattern = PATTERN.get_or_init(|| Regex::new(r"[0-9]+(\.[0-9]+){0,2}").expect("pattern"));
    let found = pattern.find(text)?;
    let numbers: Vec<i32> = found.as_str().split('.').filter_map(|part| part.parse::<i32>().ok()).collect();
    (!numbers.is_empty()).then_some(numbers)
}

fn text(node: Option<&Value>) -> Option<&str> {
    node.and_then(Value::as_str)
}

// ── opencode ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `OpencodeAgent.MinimumMajor`: an older `opencode` is OpenCode 1 (`opencode-ai`), whose files, permissions and credentials differ, and
/// is replaced rather than run.
pub const OPENCODE_MINIMUM_MAJOR: i32 = 2;
/// `OpencodeAgent.QuestionInstructionsName`: the app's instruction file for a run (opencode's model asks through its `question` tool).
pub const OPENCODE_QUESTION_INSTRUCTIONS: &str = "opencode-questions.md";

/// `OpencodeAgent.IsSupported`: whether `opencode --version` (`opencode v2.2.7`; OpenCode 1 printed `1.19.5`) names OpenCode 2.
pub fn opencode_supported(version: Option<&str>) -> bool {
    version.and_then(version_numbers).is_some_and(|numbers| numbers[0] >= OPENCODE_MINIMUM_MAJOR)
}

/// `OpencodeAgent.PermissionRules`: the pane's permission mode as OpenCode 2's ordered rules (the last match wins), given to the run
/// alone after the user's own config. OpenCode 2 allows everything by default; what these leave at "ask" comes to the pane as the
/// approval card. Plan sets no edit rule, so the plan agent's own edit denial stays.
pub fn opencode_permission_rules(mode: &str) -> Vec<(&'static str, &'static str)> {
    let outside = ["shell", "webfetch", "websearch", "subagent", "external_directory"];
    let mut rules = match mode {
        "fullAccess" => return vec![("*", "allow")],
        "acceptEdits" => vec![("edit", "allow")],
        "plan" => vec![],
        _ => vec![("edit", "ask")],
    };
    rules.extend(outside.iter().map(|action| (*action, "ask")));
    rules
}

/// `OpencodeAgent.ConfigContent`: the run's `OPENCODE_CONFIG_CONTENT`, the rules and the question instructions (opencode joins every
/// source's `instructions`, so the user's own stay).
pub fn opencode_config_content(mode: &str, instructions: Option<&str>) -> String {
    let rules: Vec<Value> = opencode_permission_rules(mode)
        .into_iter()
        .map(|(action, effect)| json!({"action": action, "effect": effect, "resource": "*"}))
        .collect();
    let mut content = Map::new();
    content.insert("permissions".into(), Value::Array(rules));
    if let Some(instructions) = instructions {
        content.insert("instructions".into(), json!([instructions]));
    }
    text_json::relaxed(&Value::Object(content), false)
}

/// `OpencodeAgent.ConfigValues`: the settings a run sends as `session/set_config_option` before its prompt.
pub fn opencode_config_values(request: &StartRunRequest) -> Vec<(String, String)> {
    let settings = request.settings();
    let mut values = Vec::new();
    if request.model != "default" {
        values.push(("model".into(), request.model.clone()));
    }
    if settings.effort != "default" {
        values.push(("effort".into(), settings.effort.clone()));
    }
    // opencode's two primary agents: build (the default, left as the session has it) and plan, which cannot edit.
    if settings.permission_mode == "plan" {
        values.push(("mode".into(), "plan".into()));
    }
    values
}

// ── Hermes ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `HermesAgent.SetupMethod`: the ACP method id Hermes always advertises, its terminal setup, not a sign-in.
pub const HERMES_SETUP_METHOD: &str = "hermes-setup";
pub const HERMES_MODEL_OPTION: &str = "model";
pub const HERMES_EFFORT_OPTION: &str = "reasoning_effort";
pub const HERMES_EDIT_POLICY_OPTION: &str = "edit_approval_policy";
/// `HermesAgent.ModelCacheFileName`: the picker the last Hermes session offered, in the app's profile folder (never in Hermes's own).
pub const HERMES_MODEL_CACHE_FILE: &str = "hermes-models.json";

/// `HermesAgent.VersionLine`: `hermes --version` prints its version first ("Hermes Agent v0.23.1 (2026.9.24) · stable"), then its install
/// folder, Python and update status.
pub fn hermes_version_line(output: &str) -> String {
    output.split('\n').map(str::trim).find(|line| !line.is_empty()).unwrap_or("").to_string()
}

/// `HermesAgent.EditPolicy`: Hermes's edit approval policy for the pane's mode (ACP modes Default / Accept Edits / Don't Ask).
pub fn hermes_edit_policy(mode: &str) -> &'static str {
    match mode {
        "acceptEdits" => "workspace_session",
        "fullAccess" => "session",
        _ => "ask",
    }
}

/// `HermesAgent.Apply`: the run's environment. Full access is Hermes's `--yolo` for a session the run creates, given as its documented
/// `HERMES_YOLO_MODE`; any other mode removes one the user's environment set, so the pane's choice holds.
pub fn hermes_apply(mode: &str, environment: &mut BTreeMap<String, String>) {
    // C#'s environment ignores the names' case: one variable whatever its spelling.
    environment.retain(|key, _| !key.eq_ignore_ascii_case("HERMES_YOLO_MODE"));
    if mode == "fullAccess" {
        environment.insert("HERMES_YOLO_MODE".into(), "1".into());
    }
}

/// `HermesAgent.ConfigValues`: the model (Hermes's picker id, `provider:model`), the reasoning effort, and the edit policy, always, so a
/// resumed session follows the pane.
pub fn hermes_config_values(request: &StartRunRequest) -> Vec<(String, String)> {
    let settings = request.settings();
    let mut values = Vec::new();
    if request.model != "default" {
        values.push((HERMES_MODEL_OPTION.into(), request.model.clone()));
    }
    if settings.effort != "default" {
        values.push((HERMES_EFFORT_OPTION.into(), settings.effort.clone()));
    }
    values.push((HERMES_EDIT_POLICY_OPTION.into(), hermes_edit_policy(&settings.permission_mode).into()));
    values
}

/// `HermesAgent.Resume`: whether a run continues the pane's Hermes session, and what the pane is told (the notice's locale key). Hermes
/// fixes `--yolo` when a session starts, so one that started with it is not continued by a pane that asks, and one that started
/// without it keeps asking in a full-access pane; a session whose first request never got an answer (`message_count` 0) keeps the
/// provider it was made with and is not continued. A pane that asks never continues a session whose policy receipt cannot be read
/// (no `state.db`, no receipt, an unreadable one): it may have started with `--yolo` and would then not ask, so a new session starts
/// (the rule the C# and Mac apps take too). A full-access pane continues it as asked.
pub fn hermes_resume(
    resume_id: &str,
    mode: &str,
    message_count: Option<i32>,
    started_with_full_access: Option<bool>,
) -> (Option<String>, Option<&'static str>) {
    let full = mode == "fullAccess";
    if message_count == Some(0) || !full && started_with_full_access.is_none() {
        return (None, None);
    }
    if started_with_full_access == Some(true) && !full {
        return (None, Some("run.notice.hermesFullAccessSession"));
    }
    (Some(resume_id.to_string()), (started_with_full_access == Some(false) && full).then_some("run.notice.hermesAskingSession"))
}

/// `HermesAgent.Provider`: the provider an `initialize` result's `authMethods` name, the entry that is not the terminal setup.
pub fn hermes_provider(result: &Value) -> Option<String> {
    for method in result.get("authMethods").and_then(Value::as_array).into_iter().flatten().filter(|m| m.is_object()) {
        let Some(name) = text(method.get("id")).filter(|id| !id.is_empty() && *id != HERMES_SETUP_METHOD) else { continue };
        match method.get("type") {
            Some(Value::String(kind)) if kind != "agent" => continue,
            _ => return Some(name.to_string()),
        }
    }
    None
}

/// `HermesAgent.StderrFilter`: keeps Hermes's own log lines (`2026-11-11 01:18:54 [INFO] …`) and the Python traceback it writes for every
/// request it answers with an error out of the pane, which shows the ACP error itself. Anything else (a start-up failure's words) passes.
#[derive(Debug, Default)]
pub struct HermesStderrFilter {
    in_traceback: bool,
}

impl HermesStderrFilter {
    /// `Keep`: whether one complete stderr line reaches the pane.
    pub fn keep(&mut self, line: &str) -> bool {
        static LOG_LINE: OnceLock<Regex> = OnceLock::new();
        let log_line = LOG_LINE
            .get_or_init(|| Regex::new(r"^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}(,[0-9]+)? \[[A-Z]+\]").expect("pattern"));
        if log_line.is_match(line) {
            self.in_traceback = false;
            return false;
        }
        if line.starts_with("Traceback (most recent call last)") {
            self.in_traceback = true;
            return false;
        }
        if line.starts_with("During handling of the above exception") || line.starts_with("The above exception was the direct cause") {
            return false;
        }
        if !self.in_traceback {
            return !line.trim().is_empty();
        }
        // The frames are indented; the first line that is not is the exception itself, the traceback's last.
        if line.chars().next().is_none_or(|first| !first.is_whitespace()) {
            self.in_traceback = false;
        }
        false
    }
}

/// `HermesAgent.Models`: the `model` option of a session's `configOptions` as picker rows (`provider:model` ids with Hermes's own names).
pub fn hermes_models(config_options: Option<&Value>) -> Vec<ModelOption> {
    let mut models: Vec<ModelOption> = Vec::new();
    let mut seen = std::collections::HashSet::new();
    let Some(option) = config_options
        .and_then(Value::as_array)
        .and_then(|options| options.iter().filter(|o| o.is_object()).find(|o| text(o.get("id")) == Some(HERMES_MODEL_OPTION)))
    else {
        return models;
    };
    let limit = providers::listed_model_limit("hermes");
    let mut add = |entry: &Value| {
        let Some(value) = text(entry.get("value")) else { return };
        if value == "default" || !wire::model(value) || models.len() >= limit || !seen.insert(value.to_string()) {
            return;
        }
        let name = clean(Some(text(entry.get("name")).unwrap_or(value)), 160, true);
        let provider = value.find(':').filter(|colon| *colon > 0).map(|colon| &value[..colon]).unwrap_or("");
        let description = if provider.is_empty() {
            String::new()
        } else {
            strings::get_with("provider.catalog.hermesModelRow", &[("provider", provider)])
        };
        models.push(ModelOption::new(value, if name.is_empty() { value } else { &name }, &description));
    };
    for entry in option.get("options").and_then(Value::as_array).into_iter().flatten().filter(|e| e.is_object()).take(4_096) {
        add(entry);
        for inner in entry.get("options").and_then(Value::as_array).into_iter().flatten().filter(|e| e.is_object()).take(4_096) {
            add(inner);
        }
    }
    models
}

/// The rows as `JsonSerializer.SerializeToUtf8Bytes(models, Wire.Json)` writes them.
fn models_json(models: &[ModelOption]) -> Vec<u8> {
    let mut out = JsonOut::new();
    out.begin_array();
    for model in models {
        model.write(&mut out);
    }
    out.end_array();
    out.finish().into_bytes()
}

/// `HermesAgent.SaveModels`: keeps a session's picker for the model menu; an empty one changes nothing. Without a file nothing is kept.
pub fn hermes_save_models(config_options: Option<&Value>, path: Option<&Path>) {
    let models = hermes_models(config_options);
    let Some(path) = path.filter(|_| !models.is_empty()) else { return };
    let data = models_json(&models);
    if std::fs::read(path).is_ok_and(|kept| kept == data) {
        return;
    }
    let Some(folder) = path.parent() else { return };
    if std::fs::create_dir_all(folder).is_err() {
        return;
    }
    let temporary = folder.join(format!(
        "{}.{}.tmp",
        path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default(),
        wire::new_id().replace('-', "")
    ));
    if std::fs::write(&temporary, &data).is_err() || std::fs::rename(&temporary, path).is_err() {
        let _ = std::fs::remove_file(&temporary);
    }
}

/// `HermesAgent.ForgetModels`: without a provider the kept picker is a past sign-in's: it goes.
pub fn hermes_forget_models(path: Option<&Path>) {
    if let Some(path) = path {
        let _ = std::fs::remove_file(path);
    }
}

/// A field of a kept row, its name matched without regard to case as `Wire.Json` reads it.
fn field<'a>(row: &'a Map<String, Value>, name: &str) -> Option<&'a str> {
    row.iter().rev().find(|(key, _)| key.eq_ignore_ascii_case(name)).and_then(|(_, value)| value.as_str())
}

/// `HermesAgent.CachedCatalog`: "Follow Hermes's settings" and, once a session ran, the models its picker offered.
pub fn hermes_cached_catalog(path: Option<&Path>) -> ModelCatalog {
    let fallback = providers::fallback("hermes");
    let empty = ModelCatalog::new("fallback", vec![fallback.models[0].clone()], &strings::get("provider.catalog.hermesEmpty"));
    let stored = path
        .filter(|p| std::fs::metadata(p).is_ok_and(|m| m.is_file() && m.len() <= 2_097_152))
        .and_then(|p| std::fs::read(p).ok())
        .and_then(|data| serde_json::from_slice::<Value>(&data).ok());
    let Some(Value::Array(rows)) = stored else { return empty };
    // `JsonSerializer.Deserialize<List<ModelOption>>` refuses the whole file for a row that is not an object, or a field of the record
    // with another kind of value: then nothing was kept. A null row is left out.
    let typed = |row: &Map<String, Value>| {
        row.iter().all(|(key, value)| match key.to_ascii_lowercase().as_str() {
            "value" | "displayname" | "description" | "resolvedmodel" => value.is_string() || value.is_null(),
            "supportseffort" => value.is_boolean() || value.is_null(),
            "supportedeffortlevels" => value.is_null() || value.as_array().is_some_and(|l| l.iter().all(|i| i.is_string() || i.is_null())),
            _ => true,
        })
    };
    if !rows.iter().all(|row| row.is_null() || row.as_object().is_some_and(typed)) {
        return empty;
    }
    let mut seen: std::collections::HashSet<String> = std::collections::HashSet::from(["default".to_string()]);
    let mut models = vec![fallback.models[0].clone()];
    for row in rows.iter().take(providers::listed_model_limit("hermes")) {
        let Value::Object(row) = row else { continue };
        let Some(value) = field(row, "value").filter(|v| wire::model(v)) else { continue };
        if !seen.insert(value.to_string()) {
            continue;
        }
        let name = clean(field(row, "displayName"), 160, true);
        models.push(ModelOption::new(value, if name.is_empty() { value } else { &name }, &clean(field(row, "description"), 2_400, false)));
    }
    if models.len() == 1 { empty } else { ModelCatalog::new("cli", models, &strings::get("provider.catalog.hermesSource")) }
}

// ── Hermes's clarify tool ─────────────────────────────────────────────────────────────────────────────────────────────────

/// `HermesQuestionTool.Key`: the one way to turn clarify on for ACP sessions.
pub const HERMES_QUESTION_KEY: &str = "platform_toolsets.acp";
pub const HERMES_QUESTION_TOOL: &str = "clarify";
/// `HermesQuestionTool.PromptNote`: what follows the user's request in a Hermes prompt while clarify is on (Hermes has no per-run system
/// prompt). After the request, not before it: Hermes titles a session by the start of its first message.
pub const HERMES_PROMPT_NOTE: &str = "[Note from the Mighty Code app, not from the user: this pane shows your clarify tool as a card the user answers with a click. Whenever you present the user with options to choose from (a quiz or multiple-choice problem, a preference, a decision), call clarify with the question and the choices instead of listing them in your reply.]";

/// `HermesQuestionTool.Outcome`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum QuestionOutcome {
    Enabled,
    Missing,
    Ready,
}

/// `HermesQuestionTool.WithoutNote`: a stored request without the note, as the session list shows it.
pub fn hermes_without_note(text: &str) -> String {
    match text.find("[Note from the Mighty Code app, not from the user:") {
        Some(at) => text[..at].trim().to_string(),
        None => text.to_string(),
    }
}

/// `HermesQuestionTool.Value`: what the unset key becomes. Saving a list makes Hermes stop adding two things it adds to the unsaved
/// default, so they are named: `x_search`, and `kanban` when the config's legacy top-level `toolsets` opts into it.
pub fn hermes_question_value(legacy_toolsets: &[String]) -> Vec<String> {
    let mut value = vec!["hermes-acp".to_string(), HERMES_QUESTION_TOOL.to_string(), "x_search".to_string()];
    if legacy_toolsets.iter().any(|t| t == "kanban") {
        value.push("kanban".into());
    }
    value
}

fn names_of(list: &[Value]) -> Vec<String> {
    list.iter()
        .map(|item| match item {
            Value::String(name) => name.clone(),
            other => other.to_string(),
        })
        .collect()
}

/// `HermesQuestionTool.Names`: a `config get --json` answer as toolset names, a list or the list a string spells (older builds saved
/// `'["a", "b"]'` as text).
pub fn hermes_question_names(json: &str) -> Option<Vec<String>> {
    match serde_json::from_str::<Value>(json).ok()? {
        Value::Array(list) => Some(names_of(&list)),
        Value::String(text) if text.trim().starts_with('[') => match serde_json::from_str::<Value>(&text.replace('\'', "\"")).ok()? {
            Value::Array(list) => Some(names_of(&list)),
            _ => None,
        },
        _ => None,
    }
}

/// What this launch found for each Hermes (its executable and data folder).
static LOOKED: Mutex<Option<HashMap<String, QuestionOutcome>>> = Mutex::new(None);

/// `HermesQuestionTool.EnsureAsync`'s answer: what this launch found the first time (the pane says it once, with the list set when it
/// was enabled), and whether Hermes's model has the tool as far as the app knows.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct QuestionTool {
    pub first: Option<QuestionOutcome>,
    pub list: Option<Vec<String>>,
    pub asks: bool,
}

/// `HermesQuestionTool.EnsureAsync`: once per app launch for each Hermes; a Hermes that could not answer is asked again by the next run.
pub async fn hermes_ensure_question_tool(
    catalog: &ProviderCatalog,
    command: &CliCommand,
    environment: &BTreeMap<String, String>,
    working_directory: &str,
) -> QuestionTool {
    let identity = format!("{}\n{}", command.binary, environment.get("HERMES_HOME").map(String::as_str).unwrap_or(""));
    if let Some(known) = lock(&LOOKED).get_or_insert_with(HashMap::new).get(&identity) {
        return QuestionTool { first: None, list: None, asks: *known != QuestionOutcome::Missing };
    }
    let Some((outcome, list)) = hermes_apply_question_tool(catalog, command, environment, working_directory).await else {
        return QuestionTool::default();
    };
    let first = match lock(&LOOKED).get_or_insert_with(HashMap::new).entry(identity) {
        std::collections::hash_map::Entry::Occupied(_) => false,
        std::collections::hash_map::Entry::Vacant(entry) => {
            entry.insert(outcome);
            true
        }
    };
    QuestionTool { first: first.then_some(outcome), list: if first { list } else { None }, asks: outcome != QuestionOutcome::Missing }
}

/// `HermesQuestionTool.Reset`: forgets what this launch found (the checks run more than one fake Hermes).
pub fn hermes_reset_question_tool() {
    *lock(&LOOKED) = None;
}

/// `HermesQuestionTool.ApplyAsync`: None when Hermes could not be asked (no answer, a managed or unreadable config).
pub async fn hermes_apply_question_tool(
    catalog: &ProviderCatalog,
    command: &CliCommand,
    environment: &BTreeMap<String, String>,
    working_directory: &str,
) -> Option<(QuestionOutcome, Option<Vec<String>>)> {
    let ask = |arguments: Vec<String>| run_cli(catalog, command, arguments, environment, working_directory);
    let get = || vec!["config".to_string(), "get".into(), "--json".into(), HERMES_QUESTION_KEY.into()];
    let current = ask(get()).await?;
    if current.code == 0 {
        let names = hermes_question_names(&current.output)?;
        let outcome = if names.iter().any(|n| n == HERMES_QUESTION_TOOL) { QuestionOutcome::Ready } else { QuestionOutcome::Missing };
        return Some((outcome, None));
    }
    // Only Hermes's own "not set" answer is an unset key; any other failure changes nothing.
    if current.code != 1 || !current.output.trim().is_empty() || !current.error.contains(&format!("not set: {HERMES_QUESTION_KEY}")) {
        return None;
    }
    let legacy = ask(vec!["config".into(), "get".into(), "--json".into(), "toolsets".into()]).await;
    let legacy = legacy.filter(|found| found.code == 0).and_then(|found| hermes_question_names(&found.output)).unwrap_or_default();
    let list = hermes_question_value(&legacy);
    let json = serde_json::to_string(&list).unwrap_or_default();
    ask(vec!["config".into(), "set".into(), HERMES_QUESTION_KEY.into(), json]).await.filter(|set| set.code == 0)?;
    let check = ask(get()).await.filter(|check| check.code == 0)?;
    (hermes_question_names(&check.output).as_ref() == Some(&list)).then_some((QuestionOutcome::Enabled, Some(list)))
}

struct Ran {
    code: i32,
    output: String,
    error: String,
}

/// Ends a child that a dropped future still owned (C#'s `await using`).
struct CloseOnDrop(Option<Arc<dyn ChildControl>>);

impl CloseOnDrop {
    /// The child closed here and awaited (C#'s `await using` on the way out), so nothing of it is left once the call returns; the drop
    /// closes it in a task of its own only when the future is dropped first.
    async fn close(mut self) {
        if let Some(control) = self.0.take() {
            let _ = control.close().await;
        }
    }
}

impl Drop for CloseOnDrop {
    fn drop(&mut self) {
        if let Some(control) = self.0.take() {
            control.kill();
            if let Ok(runtime) = tokio::runtime::Handle::try_current() {
                runtime.spawn(async move {
                    let _ = control.close().await;
                });
            }
        }
    }
}

/// `HermesQuestionTool.RunAsync`: one `hermes …` with `NO_COLOR`, its input closed, its output and error read to their ends within 20 s;
/// None when it could not be started or did not end in time.
async fn run_cli(
    catalog: &ProviderCatalog,
    command: &CliCommand,
    arguments: Vec<String>,
    environment: &BTreeMap<String, String>,
    working_directory: &str,
) -> Option<Ran> {
    let mut env = environment.clone();
    env.retain(|key, _| !key.eq_ignore_ascii_case("NO_COLOR"));
    env.insert("NO_COLOR".into(), "1".into());
    let mut all = command.prefix.clone();
    all.extend(arguments);
    let mut child = catalog.start_cli(&command.binary, &all, Path::new(working_directory), &env).await.ok()?;
    let guard = CloseOnDrop(Some(child.control()));
    drop(child.stdin.take());
    let (stdout, stderr) = (child.stdout.take(), child.stderr.take());
    let control = child.control();
    let read = async move {
        let read_all = |reader: Option<mighty_platform::process::ChildReader>| async move {
            let mut bytes = Vec::new();
            if let Some(mut reader) = reader {
                let _ = reader.read_to_end(&mut bytes).await;
            }
            String::from_utf8_lossy(&bytes).into_owned()
        };
        let (output, error) = tokio::join!(read_all(stdout), read_all(stderr));
        let code = control.wait().await.ok()?;
        Some(Ran { code, output: utf16_prefix(&output, 1_048_576), error })
    };
    let ran = tokio::time::timeout(Duration::from_secs(20), read).await.ok().flatten();
    guard.close().await;
    ran
}

/// The first `units` UTF-16 units of `text` (C#'s `text[..units]`), never cutting a pair.
pub(crate) fn utf16_prefix(text: &str, units: usize) -> String {
    let mut count = 0;
    for (at, c) in text.char_indices() {
        count += c.len_utf16();
        if count > units {
            return text[..at].to_string();
        }
    }
    text.to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn versions_and_prefixes() {
        assert_eq!(version_numbers("opencode v2.2.7"), Some(vec![2, 2, 7]));
        assert_eq!(version_numbers("local"), None);
        assert_eq!(utf16_prefix("ab😀c", 3), "ab");
        assert_eq!(utf16_prefix("ab😀c", 4), "ab😀");
    }
}
