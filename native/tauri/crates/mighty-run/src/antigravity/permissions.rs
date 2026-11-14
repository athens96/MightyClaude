//! Antigravity permission cards (C# AntigravityPermissions.cs:14-534, M/MightyCore/AntigravityPermissions.swift): what a turn was refused
//! (`AntigravityDeniedAction`), the card it leaves (`AntigravityPermissionRequest`), the rules an allow may add (`AntigravityRuleSafety`),
//! how a turn's refusals are collected from agy's stream (`AntigravityDenialCollector`), the cards of every pane
//! (`AntigravityPermissionBoard`) and what an answer does (`AntigravityPermissionFlow`).

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::{LazyLock, RwLock};

use mighty_core::json::{self, JsonOut, Obj, R, Wire};
use mighty_core::{normalize, wire};
use regex::Regex;
use serde_json::Value;

use crate::activity;
use crate::meta;
use crate::strings;

// ── the records ─────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `AntigravityDeniedAction(Kind, Action, DisplayName, Target?, Rule?)`: one action a turn was refused. `kind` is command, url, file or
/// other (a kind a newer host sends reads as other); `action` is agy's permission name (command, write_file, read_file, read_url,
/// execute_url, mcp) or the tool's own name; `display_name` agy's name for it (RunCommand, WriteFile), else the tool's; `target` the
/// command line, domain or path when agy said; `rule` the `permissions.allow` rule that allows exactly this, none when no rule can (the
/// card then says to change the permission mode instead).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeniedAction {
    pub kind: String,
    pub action: String,
    pub display_name: String,
    pub target: Option<String>,
    pub rule: Option<String>,
}

/// `AntigravityDeniedAction.Kinds`.
pub const KINDS: [&str; 4] = ["command", "url", "file", "other"];

/// `AntigravityDeniedAction.KindOf`: a kind this app does not know reads as other.
pub fn kind_of(value: Option<&str>) -> &str {
    match value {
        Some(kind) if KINDS.contains(&kind) => kind,
        _ => "other",
    }
}

impl DeniedAction {
    pub fn new(kind: &str, action: &str, display_name: &str, target: Option<&str>, rule: Option<&str>) -> DeniedAction {
        DeniedAction {
            kind: kind.into(),
            action: action.into(),
            display_name: display_name.into(),
            target: target.map(str::to_string),
            rule: rule.map(str::to_string),
        }
    }

    /// `OnceOnly`: its rule may be allowed only for the follow-up run (`AntigravityRuleSafety`).
    pub fn once_only(&self) -> bool {
        self.rule.as_deref().is_some_and(|rule| verdict(rule, None, None) == RuleVerdict::Once)
    }

    /// `OnceOnlyReason`: why it may be allowed only once; None when it may be kept (or has no rule).
    pub fn once_only_reason(&self) -> Option<OnceReason> {
        match &self.rule {
            Some(rule) if self.once_only() => once_reason_of(rule, None),
            _ => None,
        }
    }
}

impl Wire for DeniedAction {
    /// As `Wire.Json` reads the record: the kind through `AntigravityKindConverter` (a string, one this app does not know is `other`;
    /// any other token fails), the rest as strings.
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "a denied action")?;
        let kind = match o.get("kind") {
            None | Some(Value::Null) => "",
            Some(Value::String(kind)) => kind_of(Some(kind)),
            Some(_) => return json::fail("the kind is not a string"),
        };
        Ok(DeniedAction {
            kind: kind.to_string(),
            action: json::opt_string(o.get("action"))?.unwrap_or_default(),
            display_name: json::opt_string(o.get("displayName"))?.unwrap_or_default(),
            target: json::opt_string(o.get("target"))?,
            rule: json::opt_string(o.get("rule"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("kind", Some(&self.kind));
        out.prop_str("action", Some(&self.action));
        out.prop_str("displayName", Some(&self.display_name));
        out.prop_str("target", self.target.as_deref());
        out.prop_str("rule", self.rule.as_deref());
        out.end_object();
    }
}

/// C# `AntigravityPermissionRequest(Id, RunId, Actions, ReceivedAt?)`: the card one Antigravity turn leaves, what it was refused together.
/// `id` is `agy-<uuid>`, `run_id` the refused run's activity id (the phone answers by both), `received_at` when the turn ended.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PermissionRequest {
    pub id: String,
    pub run_id: String,
    pub actions: Vec<DeniedAction>,
    pub received_at: Option<String>,
}

/// `AntigravityPermissionRequest.MaximumActions`.
pub const MAXIMUM_ACTIONS: usize = 16;

impl PermissionRequest {
    /// `Make`: a new card of `run_id`, stamped now as the Mac stamps it (whole seconds).
    pub fn make(run_id: &str, actions: Vec<DeniedAction>) -> PermissionRequest {
        PermissionRequest {
            id: format!("agy-{}", wire::new_id()),
            run_id: run_id.into(),
            actions,
            received_at: Some(chrono::Utc::now().format("%Y-%m-%dT%H:%M:%SZ").to_string()),
        }
    }

    /// `Rules`: the rules an allow adds, once each, in order (never one `AntigravityRuleSafety` refuses).
    pub fn rules(&self) -> Vec<String> {
        let mut rules: Vec<String> = Vec::new();
        for rule in self.actions.iter().filter_map(|a| a.rule.as_deref()) {
            if verdict(rule, None, None) != RuleVerdict::Refused && !rules.iter().any(|r| r == rule) {
                rules.push(rule.to_string());
            }
        }
        rules
    }

    /// `CanAllow`: at least one action can be allowed with a rule.
    pub fn can_allow(&self) -> bool {
        !self.rules().is_empty()
    }

    /// `CanAllowAlways`: 항상 허용 is offered — every rule may be kept (none chains, pipes, redirects or runs a destructive command).
    pub fn can_allow_always(&self) -> bool {
        let rules = self.rules();
        !rules.is_empty() && rules.iter().all(|rule| verdict(rule, None, None) == RuleVerdict::Always)
    }

    /// `Names`: what the transcript line and the continuation name — the targets, else agy's names.
    pub fn names(&self) -> Vec<String> {
        self.actions.iter().map(|a| a.target.clone().unwrap_or_else(|| a.display_name.clone())).collect()
    }
}

impl Wire for PermissionRequest {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "an Antigravity card")?;
        let actions = match o.get("actions") {
            None | Some(Value::Null) => vec![],
            Some(Value::Array(items)) => {
                let mut actions = vec![];
                for item in items {
                    if item.is_null() {
                        return json::fail("a null action");
                    }
                    actions.push(DeniedAction::read(item)?);
                }
                actions
            }
            Some(_) => return json::fail("the actions are not a list"),
        };
        Ok(PermissionRequest {
            id: json::opt_string(o.get("id"))?.unwrap_or_default(),
            run_id: json::opt_string(o.get("runId"))?.unwrap_or_default(),
            actions,
            received_at: json::opt_string(o.get("receivedAt"))?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("id", Some(&self.id));
        out.prop_str("runId", Some(&self.run_id));
        out.name("actions");
        out.list(&self.actions);
        out.prop_str("receivedAt", self.received_at.as_deref());
        out.end_object();
    }
}

/// C# `AntigravityPermissionDecision`: the card's three buttons, 이번만 허용, 항상 허용, 거절.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PermissionDecision {
    Once,
    Always,
    Deny,
}

impl PermissionDecision {
    /// `Enum.TryParse<AntigravityPermissionDecision>(text, ignoreCase: true)` over the names (the page sends `Once`, `Always`, `Deny`).
    pub fn parse(text: &str) -> Option<PermissionDecision> {
        match text.trim().to_ascii_lowercase().as_str() {
            "once" => Some(PermissionDecision::Once),
            "always" => Some(PermissionDecision::Always),
            "deny" => Some(PermissionDecision::Deny),
            _ => None,
        }
    }
}

/// C# `AntigravityAllowGrant(RequestId, Rules, Temporary)`: what the follow-up run of an allowed card carries — the rules to put in place
/// while it runs (`temporary`) or for good. `request_id` is the card's: a host that runs remote panes adds only rules it reported itself.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AllowGrant {
    pub request_id: String,
    pub rules: Vec<String>,
    pub temporary: bool,
}

/// `AntigravityAllowGrant.MaximumRuleBytes`.
pub const MAXIMUM_RULE_BYTES: usize = 4_096;

static RULE_SHAPE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\A[a-z][a-z_]{0,63}\(.+\)\z").expect("regex"));

/// `AntigravityAllowGrant.ValidRule`: a rule is `name(…)` — a lowercase permission name and a non-empty argument, at most 4 KiB, no NUL,
/// no line break.
pub fn valid_rule(rule: &str) -> bool {
    rule.len() <= MAXIMUM_RULE_BYTES && !rule.contains(['\0', '\n', '\r']) && RULE_SHAPE.is_match(rule)
}

impl AllowGrant {
    pub fn new(request_id: &str, rules: &[&str], temporary: bool) -> AllowGrant {
        AllowGrant { request_id: request_id.into(), rules: rules.iter().map(|r| r.to_string()).collect(), temporary }
    }

    /// `IsValid`: well formed, and no rule `AntigravityRuleSafety` refuses; whether it may be kept for good is
    /// [`AllowGrant::keeps_only_allowed_rules`].
    pub fn is_valid(&self) -> bool {
        wire::identifier(&self.request_id)
            && (1..=MAXIMUM_ACTIONS).contains(&self.rules.len())
            && self.rules.iter().all(|rule| valid_rule(rule) && verdict(rule, None, None) != RuleVerdict::Refused)
    }

    /// `KeepsOnlyAllowedRules`: a temporary grant, or one whose every rule may be kept.
    pub fn keeps_only_allowed_rules(&self) -> bool {
        self.temporary || self.rules.iter().all(|rule| verdict(rule, None, None) == RuleVerdict::Always)
    }

    /// `Same`.
    pub fn same(&self, other: Option<&AllowGrant>) -> bool {
        other.is_some_and(|o| o.request_id == self.request_id && o.temporary == self.temporary && o.rules == self.rules)
    }
}

impl Wire for AllowGrant {
    fn read(value: &Value) -> R<Self> {
        let o = Obj::of(value, "an Antigravity grant")?;
        let rules = match o.get("rules") {
            None | Some(Value::Null) => vec![],
            Some(Value::Array(items)) => {
                let mut rules = vec![];
                for item in items {
                    match item {
                        Value::String(rule) => rules.push(rule.clone()),
                        _ => return json::fail("a rule is not a string"),
                    }
                }
                rules
            }
            Some(_) => return json::fail("the rules are not a list"),
        };
        Ok(AllowGrant {
            request_id: json::opt_string(o.get("requestId"))?.unwrap_or_default(),
            rules,
            temporary: json::boolean(o.get("temporary"), false)?,
        })
    }

    fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("requestId", Some(&self.request_id));
        out.prop_strings("rules", Some(&self.rules));
        out.prop_bool("temporary", self.temporary);
        out.end_object();
    }
}

/// C# `RemoteWorkspaceValidation.AntigravityCard` (M/Remote/RemoteWorkspaceModels.swift antigravityCard): ids, at most 16 actions,
/// bounded text, and only well-formed rules [`verdict`] does not refuse — what a run event's card must be.
pub fn valid_card(card: &PermissionRequest) -> bool {
    wire::identifier(&card.id)
        && wire::identifier(&card.run_id)
        && (1..=MAXIMUM_ACTIONS).contains(&card.actions.len())
        && card.actions.iter().all(|action| {
            !action.action.is_empty()
                && action.action.len() <= 128
                && !action.display_name.is_empty()
                && action.display_name.len() <= 128
                && action.target.as_deref().unwrap_or("").len() <= MAXIMUM_RULE_BYTES
                && action.rule.as_deref().is_none_or(|rule| valid_rule(rule) && verdict(rule, None, None) != RuleVerdict::Refused)
        })
}

// ── which rules an allow may add ────────────────────────────────────────────────────────────────────────────────────────────

/// C# `AntigravityRuleVerdict`: Refused is never offered or added; Once is offered and added only for the follow-up run (no 항상 허용);
/// Always may be kept.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RuleVerdict {
    Refused,
    Once,
    Always,
}

impl RuleVerdict {
    /// The enum's name as C#'s `ToString()` writes it.
    pub fn name(self) -> &'static str {
        match self {
            RuleVerdict::Refused => "Refused",
            RuleVerdict::Once => "Once",
            RuleVerdict::Always => "Always",
        }
    }
}

/// C# `AntigravityOnceReason`: why a command rule may be allowed only once (M/MightyCore/AntigravityPermissions.swift OnceReason). Risky
/// chains, pipes, redirects, substitutes, backgrounds or runs a destructive program; Interpreter is a shell, interpreter or wrapper alone
/// or with inline code (command(bash) would allow bash -c …); AgyFolder names agy's own .gemini folder.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OnceReason {
    Risky,
    Interpreter,
    AgyFolder,
}

impl OnceReason {
    pub const ALL: [OnceReason; 3] = [OnceReason::Risky, OnceReason::Interpreter, OnceReason::AgyFolder];

    /// The enum's name as C#'s `ToString()` writes it.
    pub fn name(self) -> &'static str {
        match self {
            OnceReason::Risky => "Risky",
            OnceReason::Interpreter => "Interpreter",
            OnceReason::AgyFolder => "AgyFolder",
        }
    }
}

/// `AntigravityRuleSafety.ToolPermissions`: the permission an agy tool's rule names; any other tool gets no rule.
pub const TOOL_PERMISSIONS: [(&str, &str); 11] = [
    ("run_command", "command"),
    ("write_file", "write_file"),
    ("write_to_file", "write_file"),
    ("replace_file_content", "write_file"),
    ("multi_replace_file_content", "write_file"),
    ("sed_file", "write_file"),
    ("notebook_edit", "write_file"),
    ("view_file", "read_file"),
    ("read_file", "read_file"),
    ("list_dir", "read_file"),
    ("read_url_content", "read_url"),
];

/// The permission `tool`'s rule names (`ToolPermissions.GetValueOrDefault`).
pub fn tool_permission(tool: &str) -> Option<&'static str> {
    TOOL_PERMISSIONS.iter().find(|(name, _)| *name == tool).map(|(_, permission)| *permission)
}

/// `AntigravityRuleSafety.Permissions`: the permissions a rule may name.
pub fn is_permission(name: &str) -> bool {
    TOOL_PERMISSIONS.iter().any(|(_, permission)| *permission == name)
}

/// `DestructivePrograms`: programs that destroy data, escalate or end processes (by name in lower case, without a folder or .exe); mkfs…
/// too.
pub const DESTRUCTIVE_PROGRAMS: [&str; 25] = [
    "rm",
    "rmdir",
    "unlink",
    "shred",
    "dd",
    "sudo",
    "su",
    "doas",
    "kill",
    "killall",
    "pkill",
    "shutdown",
    "reboot",
    "halt",
    "poweroff",
    "eval",
    "del",
    "erase",
    "rd",
    "format",
    "remove-item",
    "taskkill",
    "stop-process",
    "stop-computer",
    "restart-computer",
];

/// `Interpreters`: shells and interpreters, once alone or with an inline-code flag.
pub const INTERPRETERS: [&str; 24] = [
    "bash",
    "sh",
    "zsh",
    "dash",
    "fish",
    "ksh",
    "csh",
    "tcsh",
    "pwsh",
    "powershell",
    "cmd",
    "python",
    "python3",
    "node",
    "deno",
    "bun",
    "ruby",
    "perl",
    "php",
    "lua",
    "osascript",
    "py",
    "awk",
    "gawk",
];

/// `ProgramFirst`: interpreters whose first argument is their program, once whatever follows.
pub const PROGRAM_FIRST: [&str; 2] = ["awk", "gawk"];

/// `Wrappers`: programs that run the command after them, once whatever follows.
pub const WRAPPERS: [&str; 23] = [
    "env",
    "xargs",
    "nohup",
    "time",
    "timeout",
    "nice",
    "watch",
    "exec",
    "eval",
    "source",
    ".",
    "npx",
    "bunx",
    "uvx",
    "pipx",
    "command",
    "builtin",
    "caffeinate",
    "arch",
    "stdbuf",
    "wsl",
    "setsid",
    "script",
];

/// `InfoFlags`: options that only print an interpreter's version or help.
pub const INFO_FLAGS: [&str; 8] = ["--version", "-v", "-version", "--help", "-h", "-help", "-?", "/?"];

/// `InlineCodeFlags`: flags that hand an interpreter code to run.
pub const INLINE_CODE_FLAGS: [&str; 13] =
    ["-c", "-e", "-p", "-r", "--eval", "--print", "--command", "-command", "-encodedcommand", "-enc", "-ec", "/c", "/k"];

/// `ChainCharacters`: characters that chain, pipe, redirect, background or substitute.
pub const CHAIN_CHARACTERS: [char; 6] = [';', '&', '|', '>', '<', '`'];

fn words(command: &str) -> impl Iterator<Item = &str> {
    command.split([' ', '\t']).filter(|w| !w.is_empty())
}

fn trim_quotes(word: &str) -> &str {
    word.trim_matches(['"', '\''])
}

/// `OnceReasonOf`: why a rule may be allowed only once, checked in this order; None for a rule that may be kept or is refused.
/// `protected_folders`: the folders file rules keep out of (None: this computer's, [`protected_folders`]).
pub fn once_reason_of(rule: &str, protected_folders: Option<&[String]>) -> Option<OnceReason> {
    if verdict(rule, protected_folders, None) != RuleVerdict::Once {
        return None;
    }
    parts(rule).and_then(|(_, argument)| command_reason(argument))
}

/// `CommandReason`.
pub fn command_reason(command: &str) -> Option<OnceReason> {
    if risky(command) {
        Some(OnceReason::Risky)
    } else if runs_code(command) {
        Some(OnceReason::Interpreter)
    } else if command.to_lowercase().contains(".gemini") {
        Some(OnceReason::AgyFolder)
    } else {
        None
    }
}

/// `RunsCode`: a shell, interpreter or wrapper alone or running inline code, judged by the first word after any leading VAR=x (or env
/// VAR=x). command(bash) would allow bash -c "<anything>": agy matches words from the start.
pub fn runs_code(command: &str) -> bool {
    // An empty quoted word ('' or "") is dropped: the next word is the one that runs.
    let words: Vec<String> = words(command).map(|w| trim_quotes(w).to_lowercase()).filter(|w| !w.is_empty()).collect();
    let assignment = |word: &str| !word.starts_with('-') && !word.starts_with('=') && word.contains('=');
    let mut at = 0;
    while at < words.len() && assignment(&words[at]) {
        at += 1;
    }
    if at + 1 < words.len() && words[at] == "env" && assignment(&words[at + 1]) {
        at += 1;
        while at < words.len() && assignment(&words[at]) {
            at += 1;
        }
    }
    // Nothing but assignments (FOO=1, env FOO=1) runs whatever a prefix rule lets follow.
    if at >= words.len() {
        return true;
    }
    // The program by its name, without a folder or .exe.
    let mut program = words[at].as_str();
    if program != "."
        && let Some(slash) = program.rfind(['/', '\\'])
    {
        program = &program[slash + 1..];
    }
    let program = program.strip_suffix(".exe").unwrap_or(program);
    let program = if program.starts_with("python") { "python" } else { program };
    if WRAPPERS.contains(&program) {
        return true;
    }
    if !INTERPRETERS.contains(&program) {
        return false;
    }
    if PROGRAM_FIRST.contains(&program) || at + 1 == words.len() {
        return true;
    }
    // The interpreter's own options come before its script; what follows the script is the script's.
    let mut only_info = true;
    for word in &words[at + 1..] {
        if INLINE_CODE_FLAGS.contains(&word.as_str()) {
            return true;
        }
        let rest = word.get(1..).unwrap_or("");
        let short_flag = word.starts_with('-') && !word.starts_with("--") && word.len() > 1 && rest.chars().all(char::is_alphabetic);
        if short_flag && rest.chars().any(|c| "cerp".contains(c)) {
            return true;
        }
        // cmd's options are /x; anyone else's word starting with / is a script path.
        if !word.starts_with('-') && !(program == "cmd" && word.starts_with('/')) {
            return false;
        }
        if !INFO_FLAGS.contains(&word.as_str()) {
            only_info = false;
        }
    }
    // No script: the interpreter reads its code from what a prefix rule lets follow (bash -l, python3 -u), unless it only prints its
    // version or help.
    !only_info
}

/// `Parts`: the name and the argument of a well-formed rule.
pub fn parts(rule: &str) -> Option<(&str, &str)> {
    if !valid_rule(rule) {
        return None;
    }
    let open = rule.find('(')?;
    Some((&rule[..open], &rule[open + 1..rule.len() - 1]))
}

static APP_DATA_FOLDER: RwLock<Option<PathBuf>> = RwLock::new(None);

/// `AppDataFolder`: the app's data folder (where the ledger lives); the desktop service sets it. No file rule may reach into it or above
/// it.
pub fn app_data_folder() -> Option<PathBuf> {
    APP_DATA_FOLDER.read().ok().and_then(|folder| folder.clone())
}

pub fn set_app_data_folder(folder: Option<PathBuf>) {
    if let Ok(mut current) = APP_DATA_FOLDER.write() {
        *current = folder;
    }
}

/// The user's profile folder (`Environment.SpecialFolder.UserProfile`).
pub fn user_profile() -> Option<PathBuf> {
    std::env::home_dir().filter(|home| !home.as_os_str().is_empty())
}

/// `ProtectedFolders`: the folders no file rule may reach into or above — agy's own `%USERPROFILE%\.gemini` (an always rule there would
/// let agy rewrite its own permissions) and the app's data folder with its ledger, each as written and with its links resolved.
pub fn protected_folders() -> Vec<String> {
    let mut folders = vec![];
    let mut add = |path: &Path| {
        let written = path.to_string_lossy().into_owned();
        let resolved = resolved_path(&written);
        folders.push(written.clone());
        if let Some(resolved) = resolved.filter(|r| *r != written) {
            folders.push(resolved);
        }
    };
    if let Some(home) = user_profile() {
        add(&home.join(".gemini"));
    }
    if let Some(app) = app_data_folder().filter(|a| !a.as_os_str().is_empty()) {
        add(&app);
    }
    folders
}

/// `Verdict(rule, protectedFolders, resolvingLinks)`. `protected_folders`: the folders file rules keep out of, compared as written (the
/// shared cases); None uses this computer's ([`protected_folders`]). `resolving_links` (by default when None: when `protected_folders`
/// is None) also checks the rule's path with its links resolved.
pub fn verdict(rule: &str, protected_folders: Option<&[String]>, resolving_links: Option<bool>) -> RuleVerdict {
    let Some((name, argument)) = parts(rule) else { return RuleVerdict::Refused };
    if !is_permission(name) || refused_argument(argument) {
        return RuleVerdict::Refused;
    }
    if name == "write_file" || name == "read_file" {
        let mut paths = vec![argument.to_string()];
        if resolving_links.unwrap_or(protected_folders.is_none())
            && let Some(resolved) = resolved_path(argument).filter(|r| r != argument)
        {
            paths.push(resolved);
        }
        let computed;
        let folders = match protected_folders {
            Some(folders) => folders,
            None => {
                computed = self::protected_folders();
                &computed
            }
        };
        let guarded: Vec<String> = folders.iter().filter_map(|f| normalized_path(f)).collect();
        for path in &paths {
            let Some(normalized) = normalized_path(path) else { return RuleVerdict::Refused };
            if guarded.iter().any(|folder| within(&normalized, folder) || within(folder, &normalized)) {
                return RuleVerdict::Refused;
            }
        }
    }
    // A risky command, a shell or interpreter running code, or one that names agy's own folder may run once, never for good.
    if name == "command" && command_reason(argument).is_some() { RuleVerdict::Once } else { RuleVerdict::Always }
}

/// `NormalizedPath`: an absolute path compared without case, `\` as `/`, `.` and `..` taken out and no trailing slash; None when it is
/// not absolute (`/…`, `C:\…`).
pub fn normalized_path(path: &str) -> Option<String> {
    let slashed = path.replace('\\', "/");
    let bytes = slashed.as_bytes();
    let drive = bytes.len() >= 3 && bytes[0].is_ascii_alphabetic() && bytes[1] == b':' && bytes[2] == b'/';
    if !drive && !slashed.starts_with('/') {
        return None;
    }
    let mut parts: Vec<String> = vec![];
    for part in (if drive { &slashed[2..] } else { &slashed[..] }).split('/').filter(|p| !p.is_empty()) {
        match part {
            "." => {}
            ".." => {
                parts.pop();
            }
            _ => parts.push(part.to_lowercase()),
        }
    }
    let prefix = if drive { slashed[..2].to_lowercase() } else { String::new() };
    Some(format!("{prefix}/{}", parts.join("/")))
}

/// `Within`: `path` is `folder` or inside it.
pub fn within(path: &str, folder: &str) -> bool {
    path == folder || path.starts_with(&if folder.ends_with('/') { folder.to_string() } else { format!("{folder}/") })
}

/// The target of the link at `path`, followed to its last link (`ResolveLinkTarget(returnFinalTarget: true)`); None when it is no link
/// or cannot be read.
fn final_target(path: &Path) -> Option<PathBuf> {
    let mut current = path.to_path_buf();
    let mut followed = false;
    for _ in 0..40 {
        let is_link = std::fs::symlink_metadata(&current).is_ok_and(|m| m.file_type().is_symlink());
        if !is_link {
            return followed.then_some(current);
        }
        let target = std::fs::read_link(&current).ok()?;
        let joined = if target.is_absolute() { target } else { current.parent().map(|p| p.join(&target)).unwrap_or(target) };
        current = lexically_full(&joined)?;
        followed = true;
    }
    None
}

/// `Path.GetFullPath`: absolute, `.` and `..` taken out.
fn lexically_full(path: &Path) -> Option<PathBuf> {
    let absolute = std::path::absolute(path).ok()?;
    let mut result = PathBuf::new();
    for component in absolute.components() {
        match component {
            std::path::Component::ParentDir => {
                result.pop();
            }
            std::path::Component::CurDir => {}
            other => result.push(other.as_os_str()),
        }
    }
    Some(result)
}

/// `ResolvedPath`: the path with every link along it resolved, as far as it exists; None when it is not absolute or cannot be read.
pub fn resolved_path(path: &str) -> Option<String> {
    if !normalize::fully_qualified(path) {
        return None;
    }
    let full = lexically_full(Path::new(path))?;
    let mut current = PathBuf::new();
    for component in full.components() {
        current.push(component.as_os_str());
        if matches!(component, std::path::Component::Normal(_))
            && std::fs::symlink_metadata(&current).is_ok_and(|m| m.file_type().is_symlink())
        {
            current = final_target(&current)?;
        }
    }
    Some(current.to_string_lossy().into_owned())
}

/// `RefusedArgument`: empty, the whole disk or home (`/`, `~`, `C:\`), a wildcard, a parenthesis or a control character.
pub fn refused_argument(argument: &str) -> bool {
    if argument.contains(['*', '(', ')']) {
        return true;
    }
    if argument.chars().any(|c| (c as u32) < 0x20 || c as u32 == 0x7F) {
        return true;
    }
    let core = argument.trim_matches([' ', '\t']).trim_end_matches(['/', '\\']).trim_matches([' ', '\t']);
    if core.is_empty() || core == "~" {
        return true;
    }
    let bytes = core.as_bytes();
    bytes.len() == 2 && bytes[1] == b':' && bytes[0].is_ascii_alphabetic()
}

/// `Risky`: a command line that may be allowed only once.
pub fn risky(command: &str) -> bool {
    if command.contains(CHAIN_CHARACTERS) || command.contains("$(") {
        return true;
    }
    let words: Vec<String> = words(command)
        .map(|word| {
            let mut value = trim_quotes(word);
            if let Some(slash) = value.rfind(['/', '\\']) {
                value = &value[slash + 1..];
            }
            let value = value.to_lowercase();
            value.strip_suffix(".exe").map(str::to_string).unwrap_or(value)
        })
        .collect();
    if words.iter().any(|word| DESTRUCTIVE_PROGRAMS.contains(&word.as_str()) || word.starts_with("mkfs")) {
        return true;
    }
    let short_flag = |word: &str, letter: char| word.starts_with('-') && !word.starts_with("--") && word.contains(letter);
    let has = |name: &str| words.iter().any(|w| w == name);
    if (has("chmod") || has("chown")) && words.iter().any(|w| short_flag(w, 'r') || w == "--recursive") {
        return true;
    }
    if has("git") {
        if has("push") && words.iter().any(|w| w == "-f" || w.starts_with("--force") || w.starts_with('+') || short_flag(w, 'f')) {
            return true;
        }
        if has("reset") && has("--hard") {
            return true;
        }
        if has("clean") {
            return true;
        }
    }
    has("find") && has("-delete")
}

// ── one turn's refusals ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `AntigravityDenialCollector`: collects one turn's refusals from agy's stream-json — each tool step that ended ERROR with a permission
/// denial ("permission check failed … user denied permission …") whose permission the result's `denied_actions` lists, then what
/// `denied_actions` lists that no step showed.
#[derive(Debug, Default)]
pub struct DenialCollector {
    steps: HashMap<i64, DeniedAction>,
    order: Vec<i64>,
}

/// The first `count` UTF-16 units, never cutting a surrogate pair (C# `Prefix`).
fn prefix(value: &str, count: usize) -> String {
    let mut units = 0;
    let mut end = value.len();
    for (at, c) in value.char_indices() {
        if units + c.len_utf16() > count {
            end = at;
            break;
        }
        units += c.len_utf16();
    }
    value[..end].to_string()
}

impl DenialCollector {
    pub fn new() -> DenialCollector {
        DenialCollector::default()
    }

    /// `DenialMessage`: a denial's message, or None when the step's error is something else.
    pub fn denial_message(info: Option<&Value>) -> Option<String> {
        let error = info.and_then(|i| meta::prop(i, "error"));
        let message = match error {
            Some(error @ Value::Object(_)) => meta::text(error, "message"),
            Some(Value::String(message)) => Some(message.as_str()),
            _ => None,
        }?;
        if message.len() > 65_536 {
            return None;
        }
        let lowered = message.to_lowercase();
        (lowered.contains("permission check failed") && lowered.contains("denied permission")).then(|| message.to_string())
    }

    /// `Observe`: one `step_update` payload of a tool step.
    pub fn observe(&mut self, payload: Option<&Value>) {
        let Some(payload) = payload else { return };
        if meta::text(payload, "state").unwrap_or("").to_uppercase() != "ERROR" {
            return;
        }
        let Some(index) = meta::integer(payload, "step_index") else { return };
        let info = meta::prop(payload, "tool_info");
        let Some(message) = Self::denial_message(info) else { return };
        let tool = info.and_then(|i| meta::text(i, "name")).or_else(|| meta::text(payload, "tool_name")).unwrap_or("tool");
        if !self.steps.contains_key(&index) {
            self.order.push(index);
        }
        let parameters = info.and_then(|i| meta::prop(i, "parameters"));
        self.steps.insert(index, Self::action(&prefix(tool, 128), parameters, &message));
    }

    /// `Finish`: the turn's `result` payload — every refusal, once each. Starts over for the next turn.
    pub fn finish(&mut self, payload: Option<&Value>) -> Vec<DeniedAction> {
        let steps = std::mem::take(&mut self.steps);
        let order = std::mem::take(&mut self.order);
        let reported: Vec<(String, String)> = match payload.and_then(|p| meta::prop(p, "denied_actions")) {
            Some(Value::Array(rows)) => rows
                .iter()
                .take(MAXIMUM_ACTIONS)
                .filter_map(|row| {
                    let action = meta::text(row, "action").filter(|a| !a.is_empty())?;
                    let name = meta::text(row, "display_name").map(|n| prefix(n, 128)).unwrap_or_else(|| prefix(action, 128));
                    Some((prefix(action, 128), name))
                })
                .collect(),
            _ => vec![],
        };
        let mut result: Vec<DeniedAction> = vec![];
        let mut seen: HashSet<String> = HashSet::new();
        let mut add = |value: DeniedAction, result: &mut Vec<DeniedAction>| {
            let key =
                value.rule.clone().unwrap_or_else(|| format!("{}|{}|{}", value.kind, value.action, value.target.as_deref().unwrap_or("")));
            if result.len() < MAXIMUM_ACTIONS && seen.insert(key) {
                result.push(value);
            }
        };
        // A step counts only when the result says its permission was refused.
        let listed: HashSet<&str> = reported.iter().map(|(action, _)| action.as_str()).collect();
        for index in &order {
            let Some(value) = steps.get(index) else { continue };
            if !listed.contains(value.action.as_str()) {
                continue;
            }
            let mut value = value.clone();
            if let Some((_, name)) = reported.iter().find(|(action, _)| *action == value.action) {
                value.display_name = name.clone();
            }
            add(value, &mut result);
        }
        // A refusal the steps did not show (agy reports each permission once).
        let covered: HashSet<String> = result.iter().map(|a| a.action.clone()).collect();
        for (action, name) in reported.iter().filter(|(action, _)| !covered.contains(action)) {
            add(DeniedAction::new(kind(action, action), action, name, None, None), &mut result);
        }
        result
    }

    /// `Action`: what one denied step was. A command is allowed by its whole command line (`command(<line>)`, a word-prefix rule in agy);
    /// any other denial names its own rule ("user denied permission for write_file(/path)"), used as agy wrote it when it names the tool's
    /// own permission ([`TOOL_PERMISSIONS`]). A rule the safety rules refuse, or a tool outside the mapping, leaves the action without one.
    pub fn action(tool: &str, parameters: Option<&Value>, message: &str) -> DeniedAction {
        let parameter = |names: &[&str]| -> Option<String> {
            names.iter().find_map(|name| {
                parameters.and_then(|p| meta::text(p, name)).filter(|v| !v.is_empty()).map(|v| prefix(v, MAXIMUM_RULE_BYTES))
            })
        };
        let permission = tool_permission(tool);
        if permission == Some("command") {
            let line = parameter(&["CommandLine", "Command", "command"]);
            let rule =
                line.as_ref().map(|line| format!("command({line})")).filter(|rule| verdict(rule, None, None) != RuleVerdict::Refused);
            return DeniedAction { kind: "command".into(), action: "command".into(), display_name: tool.into(), target: line, rule };
        }
        if let Some(permission) = permission
            && let Some(named) = named_rule(message)
            && let Some((name, argument)) = parts(&named)
            && name == permission
            && verdict(&named, None, None) != RuleVerdict::Refused
        {
            let kind = kind(name, tool);
            let mut target = argument.to_string();
            // A URL rule names a domain; show the domain, as the rule does.
            if kind == "url"
                && let Some(host) = host(&target).filter(|h| !h.is_empty())
            {
                target = host;
            }
            return DeniedAction::new(kind, name, tool, Some(&target), Some(&named));
        }
        let action = permission.unwrap_or(tool);
        let plain = kind(action, tool);
        let shown = match plain {
            "url" => parameter(&["Url", "URL", "url"]).map(|url| host(&url).unwrap_or(url)),
            "file" => parameter(&["Path", "TargetFile", "AbsolutePath", "path"]),
            "command" => parameter(&["CommandLine"]),
            _ => None,
        };
        DeniedAction { kind: plain.into(), action: action.into(), display_name: tool.into(), target: shown, rule: None }
    }
}

/// `Kind(action, tool)`.
pub fn kind(action: &str, tool: &str) -> &'static str {
    let name = action.to_lowercase();
    let tool_name = tool.to_lowercase();
    if name == "command" || tool_name == "run_command" {
        return "command";
    }
    if name.ends_with("_url") || name == "url" || tool_name.contains("url") || tool_name.contains("browser") {
        return "url";
    }
    if name.ends_with("_file")
        || matches!(
            tool_name.as_str(),
            "write_to_file" | "replace_file_content" | "multi_replace_file_content" | "sed_file" | "notebook_edit" | "view_file"
        )
    {
        return "file";
    }
    "other"
}

/// `NamedRule`: the rule a denial names — the text after "denied permission for " up to the end of its line, when it is a whole
/// `name(…)` rule.
pub fn named_rule(message: &str) -> Option<String> {
    const MARKER: &str = "denied permission for ";
    let bytes = message.as_bytes();
    let at =
        (0..=bytes.len().checked_sub(MARKER.len())?).find(|at| bytes[*at..*at + MARKER.len()].eq_ignore_ascii_case(MARKER.as_bytes()))?;
    let rest = &message[at + MARKER.len()..];
    let end = rest.find(['\n', '\r']).unwrap_or(rest.len());
    let line = rest[..end].trim_matches([' ', '\t']);
    // "User denied permission for write_file(/x)." ends its sentence.
    let rule = if line.ends_with(").") { &line[..line.len() - 1] } else { line };
    valid_rule(rule).then(|| rule.to_string())
}

static URL_AUTHORITY: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\A[A-Za-z][A-Za-z0-9+.\-]*://([^/?#]*)").expect("regex"));

/// `Host`: the host of an absolute URL as written (`scheme://[user@]host[:port]/…`); None for anything else, a bare domain included.
pub fn host(value: &str) -> Option<String> {
    let captures = URL_AUTHORITY.captures(value)?;
    let mut authority = captures.get(1).map_or("", |m| m.as_str());
    if let Some(user) = authority.rfind('@') {
        authority = &authority[user + 1..];
    }
    if authority.starts_with('[') {
        let close = authority.find(']')?;
        return (close > 1).then(|| authority[1..close].to_string());
    }
    let host = match authority.find(':') {
        Some(colon) => &authority[..colon],
        None => authority,
    };
    (!host.is_empty()).then(|| host.to_string())
}

// ── the cards of every pane ─────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `AntigravityPermissionBoard`: one card per pane, the last turn's. A card stays until it is answered, or until a later run of that
/// pane ends — that run's own card (if any) replaces it, and one without any leaves none. Not thread-safe: its owner locks around it.
#[derive(Debug, Default)]
pub struct PermissionBoard {
    cards: HashMap<String, PermissionRequest>,
    /// Panes whose card came from the run that is still ending.
    from_running_turn: HashSet<String>,
}

impl PermissionBoard {
    pub fn new() -> PermissionBoard {
        PermissionBoard::default()
    }

    /// `this[sessionId]`.
    pub fn get(&self, session_id: &str) -> Option<&PermissionRequest> {
        self.cards.get(session_id)
    }

    pub fn cards(&self) -> &HashMap<String, PermissionRequest> {
        &self.cards
    }

    /// `Received`: a turn ended with refusals (before its run's final status).
    pub fn received(&mut self, card: PermissionRequest, session_id: &str) {
        self.cards.insert(session_id.into(), card);
        self.from_running_turn.insert(session_id.into());
    }

    /// `RunEnded`: a run of the pane ended (completed, error or stopped). True when the pane's card went.
    pub fn run_ended(&mut self, session_id: &str) -> bool {
        !self.from_running_turn.remove(session_id) && self.cards.remove(session_id).is_some()
    }

    /// `Restore`: puts back a card whose answer could not run; the next run's end clears it as usual.
    pub fn restore(&mut self, card: PermissionRequest, session_id: &str) -> bool {
        if self.cards.contains_key(session_id) {
            return false;
        }
        self.cards.insert(session_id.into(), card);
        true
    }

    /// `Remove`.
    pub fn remove(&mut self, session_id: &str) -> Option<PermissionRequest> {
        self.from_running_turn.remove(session_id);
        self.cards.remove(session_id)
    }

    /// `Pending`: the pane's card when it is this one.
    pub fn pending(&self, session_id: &str, request_id: &str, run_id: &str) -> Option<&PermissionRequest> {
        self.cards.get(session_id).filter(|card| card.id == request_id && card.run_id == run_id)
    }
}

// ── what an answer does ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `AntigravityContinuation(Grant, Prompt)`: what an answer does, decided without the app — the grant and the user turn sent on the
/// same conversation.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Continuation {
    pub grant: AllowGrant,
    pub prompt: String,
}

/// `AntigravityPermissionFlow.Refusal`: why an allow cannot go ahead — no rule can allow the card, or 항상 허용 on a card with a rule that
/// may be allowed only once. None for deny and for an allow that can.
pub fn refusal(card: &PermissionRequest, decision: PermissionDecision) -> Option<String> {
    if decision == PermissionDecision::Deny {
        return None;
    }
    if !card.can_allow() {
        return Some(strings::get("antigravity.permission.noRule"));
    }
    (decision == PermissionDecision::Always && !card.can_allow_always()).then(|| strings::get("remote.error.antigravityOnceOnly"))
}

/// `AntigravityPermissionFlow.Continuation`: None for deny, and for an allow [`refusal`] refuses.
pub fn continuation(card: &PermissionRequest, decision: PermissionDecision) -> Option<Continuation> {
    if decision == PermissionDecision::Deny || refusal(card, decision).is_some() {
        return None;
    }
    let allowed: Vec<String> =
        card.actions.iter().filter(|a| a.rule.is_some()).map(|a| a.target.clone().unwrap_or_else(|| a.display_name.clone())).collect();
    let list = activity::clean(Some(&allowed.join(", ")), 2_048, true);
    Some(Continuation {
        grant: AllowGrant { request_id: card.id.clone(), rules: card.rules(), temporary: decision == PermissionDecision::Once },
        prompt: strings::get_with("antigravity.permission.continue", &[("actions", &list)]),
    })
}

/// `AntigravityPermissionFlow.TranscriptLine`: the short transcript line a card leaves (what an older phone shows).
pub fn transcript_line(card: &PermissionRequest) -> String {
    strings::get_with("run.notice.antigravityAsked", &[("actions", &activity::clean(Some(&card.names().join(", ")), 512, true))])
}

/// `AntigravityPermissionFlow.RuleLine`: what the card says an action's rule allows — commands starting with it, a file or everything in
/// a folder, a domain.
pub fn rule_line(action: &DeniedAction) -> Option<String> {
    let rule = action.rule.as_deref()?;
    let key = if action.action == "command" { "antigravity.permission.ruleCommand" } else { "antigravity.permission.rule" };
    Some(strings::get_with(key, &[("rule", rule)]))
}

/// `AntigravityPermissionFlow.OnceOnlyText(reason)`: the words under a rule that may be allowed only once.
pub fn once_only_reason_text(reason: OnceReason) -> String {
    strings::get(once_only_key(reason))
}

/// The locale key of [`once_only_reason_text`].
pub fn once_only_key(reason: OnceReason) -> &'static str {
    match reason {
        OnceReason::Interpreter => "antigravity.permission.onceOnlyInterpreter",
        OnceReason::AgyFolder => "antigravity.permission.onceOnlyAgyFolder",
        OnceReason::Risky => "antigravity.permission.onceOnly",
    }
}

/// `AntigravityPermissionFlow.OnceOnlyText(action)`: the words under an action's rule, or None when it may be kept.
pub fn once_only_text(action: &DeniedAction) -> Option<String> {
    match action.once_only_reason() {
        Some(reason) => Some(once_only_reason_text(reason)),
        None if action.once_only() => Some(once_only_reason_text(OnceReason::Risky)),
        None => None,
    }
}

/// `AntigravityPermissionFlow.KindLabel`: the card's label for an action's kind.
pub fn kind_label(kind: &str) -> String {
    strings::get(&format!("antigravity.permission.kind.{}", kind_of(Some(kind))))
}

/// `AntigravityPermissionFlow.Decision`: the phone's and the desktop's answer to a permission card — allow alone is once, allow with always
/// is always.
pub fn decision(allow: bool, always: bool) -> PermissionDecision {
    match (allow, always) {
        (true, true) => PermissionDecision::Always,
        (true, false) => PermissionDecision::Once,
        _ => PermissionDecision::Deny,
    }
}
