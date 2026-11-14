//! What the CLI accounts read of the two ACP agents (C# `OpencodeAccounts`, OpencodeAccounts.cs; `HermesAccounts`, HermesAccounts.cs):
//! OpenCode 2's sign-ins as `opencode auth list --format json --standalone` lists them, and Hermes's provider as its ACP server's
//! `initialize` answer names it (the app starts `hermes acp`, writes `initialize` alone, reads the answer and closes its input). Names
//! only, never a credential. With them [`CliAccountStatus`] (CliAccounts.cs), the record the accounts section and the sign-in card read,
//! and the parts of `CliAccountSupport` these two use. The section, the coordinator that asks each CLI, the sign-in card and its
//! recovery are W6's: they take these statuses as they are.

use std::collections::BTreeMap;
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use mighty_platform::ChildControl;
use serde_json::Value;
use tokio::io::{AsyncReadExt, AsyncWriteExt};

use crate::model::CliCommand;
use crate::providers::ProviderCatalog;
use crate::strings;

use super::agents;

/// C# `CliAccountStatus`: who a CLI is signed in as, and how to change it. Label and plan only, never a token.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CliAccountStatus {
    pub provider: String,
    pub installed: bool,
    /// None when the CLI could not be asked.
    pub logged_in: Option<bool>,
    /// How the CLI is signed in, as shown to the user.
    pub method: Option<String>,
    /// The same method as a language-free id ([`method`]).
    pub method_id: Option<String>,
    pub account: Option<String>,
    pub plan: Option<String>,
    pub detail: String,
    /// False when the sign-in cannot be undone by the app.
    pub can_sign_out: bool,
    pub access_verified: Option<bool>,
}

impl CliAccountStatus {
    /// `new CliAccountStatus { Provider = provider }` with C#'s initializers.
    pub fn new(provider: &str) -> CliAccountStatus {
        CliAccountStatus {
            provider: provider.into(),
            installed: true,
            logged_in: None,
            method: None,
            method_id: None,
            account: None,
            plan: None,
            detail: String::new(),
            can_sign_out: true,
            access_verified: None,
        }
    }

    /// `Summary`: "user@example.com · Max · Claude 구독".
    pub fn summary(&self) -> String {
        if self.logged_in != Some(true) {
            if self.logged_in == Some(false) {
                return strings::get("settings.cliAccounts.summarySignedOut");
            }
            return if self.detail.is_empty() { strings::get("settings.cliAccounts.summaryUnknown") } else { self.detail.clone() };
        }
        let parts: Vec<&str> =
            [&self.account, &self.plan, &self.method].into_iter().flatten().map(String::as_str).filter(|s| !s.is_empty()).collect();
        if parts.is_empty() { strings::get("settings.cliAccounts.summarySignedIn") } else { parts.join(" · ") }
    }
}

/// `CliAccountMethod`: the ids of these two agents' sign-in methods.
pub mod method {
    /// opencode: a subscription sign-in (`opencode auth login` with OAuth).
    pub const OPENCODE_SUBSCRIPTION: &str = "opencode-subscription";
    /// opencode: API keys, stored in its database or in the environment.
    pub const OPENCODE_API_KEY: &str = "opencode-api-key";
    /// Hermes: the model provider `hermes model` set up.
    pub const HERMES_PROVIDER: &str = "hermes-provider";
}

/// `CliAccountSupport.Clean`: control characters removed, at most 200 UTF-8 bytes (a character beyond the BMP counts as its two halves,
/// three bytes each, as C# counts it).
pub fn clean(value: &str) -> String {
    let mut out = String::new();
    let mut bytes = 0;
    for c in value.chars().filter(|c| !c.is_control()) {
        let width = if c.len_utf16() == 2 { 6 } else { c.len_utf8() };
        if bytes + width > 200 {
            break;
        }
        bytes += width;
        out.push(c);
    }
    out
}

/// `CliAccountSupport.StatusArguments`: argv of the CLI's own status command (the executable's name first). opencode's `--standalone`
/// reads the list without starting OpenCode 2's background service; Hermes has none, so its ACP server is asked ([`hermes_probe`]).
pub fn status_arguments(provider: &str) -> Option<Vec<&'static str>> {
    Some(match provider {
        "claude" => vec!["claude", "auth", "status", "--json"],
        "codex" => vec!["codex", "login", "status"],
        "gemini" => vec!["agy", "models"],
        "pi" => vec!["pi", "--list-models"],
        "opencode" => vec!["opencode", "auth", "list", "--format", "json", "--standalone"],
        "hermes" => vec!["hermes", "acp"],
        _ => return None,
    })
}

/// `CliAccountSupport.LoginArguments` (the account option): argv of the CLI's own sign-in, run in a terminal.
pub fn login_arguments(provider: &str) -> Option<Vec<&'static str>> {
    Some(match provider {
        "claude" => vec!["claude", "auth", "login"],
        "codex" => vec!["codex", "login"],
        "gemini" => vec!["agy"],
        "pi" => vec!["pi"],
        "opencode" => vec!["opencode", "auth", "login"],
        "hermes" => vec!["hermes", "model"],
        _ => return None,
    })
}

/// `CliAccountSupport.LogoutArguments`: Antigravity, pi, opencode and Hermes have none the app runs.
pub fn logout_arguments(provider: &str) -> Option<Vec<&'static str>> {
    Some(match provider {
        "claude" => vec!["claude", "auth", "logout"],
        "codex" => vec!["codex", "logout"],
        _ => return None,
    })
}

/// `CliAccountSupport.LoginHint`: what a sign-in started in a terminal asks of the user there.
pub fn login_hint(provider: &str) -> Option<String> {
    match provider {
        "pi" => Some(strings::get("settings.cliAccounts.piLoginHint")),
        "opencode" => Some(strings::get("settings.cliAccounts.opencodeLoginHint")),
        "hermes" => Some(strings::get("settings.cliAccounts.hermesLoginHint")),
        _ => None,
    }
}

// ── opencode ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `OpencodeAccounts.Listed`: the integrations with a live connection, sorted; whether one is a subscription (OAuth) sign-in; whether all
/// are environment keys.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Listed {
    pub integrations: Vec<String>,
    pub subscription: bool,
    pub environment_only: bool,
}

/// `OpencodeAccounts.IsSupported`.
pub fn opencode_supported(version: &str) -> bool {
    agents::opencode_supported(Some(version))
}

/// `OpencodeAccounts.Connections`: `[{id, name, connections: [{type: "credential", method, status?} | {type: "env", name}]}]`. An
/// integration counts with at least one connection whose `status.status` is not `needs_auth`. None for anything that is not that list.
pub fn opencode_connections(list_json: &str) -> Option<Listed> {
    let Ok(Value::Array(rows)) = serde_json::from_str::<Value>(list_json) else { return None };
    let mut names = Vec::new();
    let (mut subscription, mut stored) = (false, false);
    for row in rows.iter().take(64) {
        let Some(Value::Array(connections)) = row.get("connections") else { continue };
        let live: Vec<&Value> = connections
            .iter()
            .take(64)
            .filter(|c| c.is_object() && c.get("status").and_then(|s| s.get("status")).and_then(Value::as_str) != Some("needs_auth"))
            .collect();
        let name = row.get("name").and_then(Value::as_str).or_else(|| row.get("id").and_then(Value::as_str));
        let Some(name) = name.filter(|_| !live.is_empty()) else { continue };
        names.push(clean(name));
        let credential = |c: &Value| c.get("type").and_then(Value::as_str) == Some("credential");
        if live.iter().any(|c| credential(c)) {
            stored = true;
        }
        if live.iter().any(|c| credential(c) && c.get("method").and_then(Value::as_str) == Some("oauth")) {
            subscription = true;
        }
    }
    names.sort_by(|a, b| a.encode_utf16().cmp(b.encode_utf16()));
    let environment_only = !names.is_empty() && !stored;
    Some(Listed { integrations: names, subscription, environment_only })
}

/// `OpencodeAccounts.Status`: OpenCode 2's sign-ins are what `opencode auth list` lists. With none it still runs opencode Zen's free
/// models, which reads as signed out with that note; a failed, timed-out or unreadable list stays unknown; OpenCode 1 (its version,
/// read when the list is not JSON) asks for OpenCode 2. The app signs nothing out: the row says `opencode auth logout` in a terminal does.
pub fn opencode_status(exit_code: i32, output: &str, version: Option<&str>, timed_out: bool) -> CliAccountStatus {
    let mut status = CliAccountStatus { can_sign_out: false, ..CliAccountStatus::new("opencode") };
    if let Some(version) = version
        && !opencode_supported(version)
    {
        status.detail = strings::get("settings.cliAccounts.detailOpencodeV1");
        return status;
    }
    let listed = if exit_code != 0 || timed_out { None } else { opencode_connections(output) };
    let Some(listed) = listed else {
        status.detail = strings::get(if timed_out {
            "settings.cliAccounts.detailOpencodeTimeout"
        } else {
            "settings.cliAccounts.detailOpencodeUnknown"
        });
        return status;
    };
    if listed.integrations.is_empty() {
        status.logged_in = Some(false);
        status.detail = strings::get("settings.cliAccounts.detailOpencodeFreeOnly");
        return status;
    }
    status.logged_in = Some(true);
    status.method = Some(strings::get(if listed.environment_only {
        "cliAccounts.method.opencodeEnvironmentKey"
    } else if listed.subscription {
        "cliAccounts.method.opencodeSubscription"
    } else {
        "windows.cli.method.apiKey"
    }));
    status.method_id = Some(if listed.subscription { method::OPENCODE_SUBSCRIPTION } else { method::OPENCODE_API_KEY }.into());
    status.account = Some(listed.integrations.iter().take(16).cloned().collect::<Vec<_>>().join(", "));
    status.detail = strings::get("settings.cliAccounts.detailOpencodeSignOut");
    status
}

// ── Hermes ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `HermesAccounts.Probe`: what `hermes acp` answered — the provider whose credentials resolved, whether it answered `initialize` at all,
/// whether the wait ran out first, and the version it names.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Probe {
    pub provider: Option<String>,
    pub answered: bool,
    pub timed_out: bool,
    pub version: Option<String>,
}

/// `HermesAccounts.RequestId`: an ACP channel's first, which the recorded answers in native/contracts/fixtures/hermes-acp-*.jsonl carry.
pub const HERMES_REQUEST_ID: &str = "mighty-1";
/// `HermesAccounts.InitializeLine`: the one line written to `hermes acp`, `initialize` with the capabilities a run announces.
pub const HERMES_INITIALIZE_LINE: &str = r#"{"jsonrpc":"2.0","id":"mighty-1","method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false,"elicitation":{"form":{}}},"clientInfo":{"name":"mighty-code","title":"Mighty Code","version":"1.0"}}}"#;
/// `HermesAccounts.ProbeTimeout`: how long Hermes has to answer (a cold start of its Python takes a while).
pub const HERMES_PROBE_TIMEOUT: Duration = Duration::from_secs(25);
/// How long Hermes has to end once its standard input is closed, before it is stopped.
const EXIT_GRACE: Duration = Duration::from_secs(2);
const MAXIMUM_LINES: usize = 64;
const MAXIMUM_OUTPUT: usize = 1_048_576;

/// `HermesAccounts.VersionLine`.
pub fn hermes_version_line(output: &str) -> String {
    agents::hermes_version_line(output)
}

/// `HermesAccounts.ParseInitialize`: the `initialize` answer among the lines `hermes acp` printed (a log line or a warning may come
/// first), an error answer counting as none. None when none of the first 64 lines is it.
pub fn hermes_parse_initialize(output: &str) -> Option<Probe> {
    output.split('\n').take(MAXIMUM_LINES).find_map(answer)
}

fn answer(line: &str) -> Option<Probe> {
    if line.trim().is_empty() {
        return None;
    }
    let root: Value = serde_json::from_str(line).ok()?;
    if root.get("id").and_then(Value::as_str) != Some(HERMES_REQUEST_ID) {
        return None;
    }
    let Some(result) = root.get("result").filter(|r| r.is_object()) else {
        return Some(Probe { provider: None, answered: false, timed_out: false, version: None });
    };
    let version = result.get("agentInfo").and_then(|info| info.get("version")).and_then(Value::as_str);
    Some(Probe { provider: hermes_provider(result).map(|p| clean(&p)), answered: true, timed_out: false, version: version.map(clean) })
}

/// `HermesAccounts.Provider`: the provider an `initialize` result's `authMethods` name. Hermes always lists its terminal setup, and an
/// agent method named after the provider when that provider's credentials resolve. Only ids are read.
pub fn hermes_provider(result: &Value) -> Option<String> {
    let methods = result.get("authMethods").and_then(Value::as_array)?;
    methods.iter().take(64).find_map(|method| {
        let id = method.get("id").and_then(Value::as_str).filter(|id| !id.is_empty() && *id != agents::HERMES_SETUP_METHOD)?;
        (method.get("type").and_then(Value::as_str).unwrap_or("agent") == "agent").then(|| id.to_string())
    })
}

/// `HermesAccounts.Status`: Hermes is signed in when its ACP server names a provider whose credentials resolve; with none it only offers
/// its terminal setup. No answer (an old Hermes, a missing ACP package, a timeout) stays unknown. The app signs nothing out.
pub fn hermes_status(probe: Option<&Probe>) -> CliAccountStatus {
    let mut status = CliAccountStatus { can_sign_out: false, ..CliAccountStatus::new("hermes") };
    let Some(probe) = probe else {
        status.detail = strings::get("settings.cliAccounts.detailRunFailed");
        return status;
    };
    if !probe.answered {
        status.detail = strings::get(if probe.timed_out {
            "settings.cliAccounts.detailHermesTimeout"
        } else {
            "settings.cliAccounts.detailHermesUnknown"
        });
        return status;
    }
    let Some(provider) = probe.provider.as_ref().filter(|p| !p.is_empty()) else {
        status.logged_in = Some(false);
        status.detail = strings::get("settings.cliAccounts.detailHermesNoProvider");
        return status;
    };
    status.logged_in = Some(true);
    status.method = Some(strings::get("cliAccounts.method.hermesProvider"));
    status.method_id = Some(method::HERMES_PROVIDER.into());
    status.account = Some(provider.clone());
    status.detail = strings::get("settings.cliAccounts.detailHermesSignOut");
    status
}

/// Ends the probe's child whatever happened to the probe.
struct CloseOnDrop(Option<Arc<dyn ChildControl>>);

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

/// `HermesAccounts.ProbeAsync`: starts `hermes acp` in `working_directory`, writes [`HERMES_INITIALIZE_LINE`] and reads the answer, then
/// closes its standard input, which ends it; one that does not end within two seconds is stopped. Its standard error (its log) is read
/// and dropped. None when it cannot be started; not answered when it ended first (an old Hermes without `acp`, an install without its ACP
/// packages), answered with an error, or did not answer within `timeout` (timed out).
pub async fn hermes_probe(
    catalog: &ProviderCatalog,
    command: &CliCommand,
    environment: &BTreeMap<String, String>,
    working_directory: &Path,
    timeout: Duration,
) -> Option<Probe> {
    let mut values = environment.clone();
    values.insert("NO_COLOR".into(), "1".into());
    let mut arguments = command.prefix.clone();
    arguments.push("acp".into());
    let mut child = catalog.start_cli(&command.binary, &arguments, working_directory, &values).await.ok()?;
    let control = child.control();
    let guard = CloseOnDrop(Some(control.clone()));
    if let Some(mut errors) = child.stderr.take() {
        tokio::spawn(async move {
            let mut buffer = [0u8; 4096];
            while matches!(errors.read(&mut buffer).await, Ok(read) if read > 0) {}
        });
    }
    let mut stdin = child.stdin.take();
    let stdout = child.stdout.take();
    let exchange = async {
        // One that is gone before it read the request has nothing to answer: what it printed is still read.
        if let Some(pipe) = stdin.as_mut() {
            let _ = pipe.write_all(format!("{HERMES_INITIALIZE_LINE}\n").as_bytes()).await;
            let _ = pipe.flush().await;
        }
        read_answer(stdout).await
    };
    let (answer, timed_out) = match tokio::time::timeout(timeout, exchange).await {
        Ok(answer) => (answer, false),
        Err(_) => (None, true),
    };
    drop(stdin);
    let _ = tokio::time::timeout(EXIT_GRACE, control.wait()).await;
    drop(guard);
    Some(answer.unwrap_or(Probe { provider: None, answered: false, timed_out, version: None }))
}

/// `ReadAnswerAsync`: lines until the answer; none after 64 lines or 1 MiB, or once the output ends.
async fn read_answer(stdout: Option<mighty_platform::process::ChildReader>) -> Option<Probe> {
    let mut reader = stdout?;
    let mut line: Vec<u8> = Vec::new();
    let (mut lines, mut total) = (0, 0);
    let mut buffer = [0u8; 4096];
    loop {
        let read = reader.read(&mut buffer).await.ok()?;
        if read == 0 {
            break;
        }
        for byte in &buffer[..read] {
            if *byte != b'\n' {
                line.push(*byte);
                continue;
            }
            if let Some(probe) = answer(&String::from_utf8_lossy(&line)) {
                return Some(probe);
            }
            line.clear();
            lines += 1;
            if lines >= MAXIMUM_LINES {
                return None;
            }
        }
        total += read;
        if total > MAXIMUM_OUTPUT {
            return None;
        }
    }
    answer(&String::from_utf8_lossy(&line))
}
