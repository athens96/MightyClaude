//! CLI accounts (C# `CliAccountSupport`, `CliAccountMethod`, `CliAccountsCoordinator`, `CliAccountSmoke`, `CliAccountStrings`;
//! CliAccounts.cs, CliAccountStrings.cs), with pi's sign-in (C# `PiAccounts` and the credentials half of `PiAgent`, PiSupport.cs).
//!
//! The app only asks the CLIs (or reads their account files) for the account label, never for a token: what a read keeps is the
//! label and the plan. The status record and opencode's and Hermes's readers are mighty-run's (`acp::accounts`), reused here.

use std::collections::{BTreeMap, HashMap};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use base64::Engine;
use mighty_platform::process::ChildReader;
use mighty_platform::{BoxFuture, PlatformError, ProcessHost};
use mighty_run::acp::accounts as acp;
use mighty_run::model::CliCommand;
use mighty_run::providers::ProviderCatalog;
use mighty_run::{meta, os, strings};
use serde_json::{Map, Value};
use tokio::io::AsyncReadExt;

pub use mighty_run::acp::accounts::{CliAccountStatus, clean, login_hint, logout_arguments, status_arguments};

use mighty_platform::{TerminalLaunch, TerminalLauncher, TerminalWindow};

use crate::terminal::CliTerminalSignIn;
use crate::{bedrock, env_get, lock};

/// `CliAccountMethod`: the language-free ids of the sign-in methods [`CliAccountStatus::method_id`] carries. Decisions compare
/// these, never the displayed method.
pub mod method {
    pub use mighty_run::acp::accounts::method::{HERMES_PROVIDER, OPENCODE_API_KEY, OPENCODE_SUBSCRIPTION};

    pub const CLAUDE_SUBSCRIPTION: &str = "claude-subscription";
    pub const CLAUDE_CONSOLE: &str = "claude-console";
    pub const CLAUDE_API_KEY: &str = "claude-api-key";
    pub const CLAUDE_BEDROCK: &str = "claude-bedrock";
    pub const CLAUDE_VERTEX: &str = "claude-vertex";
    pub const CLAUDE_FOUNDRY: &str = "claude-foundry";
    pub const CLAUDE_EXTERNAL: &str = "claude-external";
    pub const CLAUDE_OTHER: &str = "claude-other";
    pub const CODEX_CHATGPT: &str = "codex-chatgpt";
    pub const CODEX_API_KEY: &str = "codex-api-key";
    /// Antigravity's one sign-in: a Google account (personal, or Business for Gemini Enterprise).
    pub const GEMINI_GOOGLE: &str = "gemini-google";
    /// pi: a subscription sign-in (`/login`), API keys (`auth.json` or the environment), or only models from `models.json`.
    pub const PI_SUBSCRIPTION: &str = "pi-subscription";
    pub const PI_API_KEY: &str = "pi-api-key";
    pub const PI_MODELS: &str = "pi-models";
}

crate::copy! {
    /// `CliAccountStrings`: the CLI accounts section's copy, from the shared locale files. The one OS-bound substitution is the
    /// description's Windows wording ("외부 터미널 창에서").
    pub mod copy {
        section_title("SectionTitle") = "settings.cliAccounts.sectionTitle";
        section_description("SectionDescription") = "settings.cliAccounts.sectionDescriptionWindows";
        status_checking("StatusChecking") = "settings.cliAccounts.statusChecking";
        status_pending("StatusPending") = "settings.cliAccounts.statusPending";
        status_not_installed("StatusNotInstalled") = "settings.cliAccounts.statusNotInstalled";
        button_cancel_wait("ButtonCancelWait") = "settings.cliAccounts.buttonCancelWait";
        button_change("ButtonChange") = "settings.cliAccounts.buttonChange";
        button_logout("ButtonLogout") = "settings.cliAccounts.buttonLogout";
        button_login("ButtonLogin") = "settings.cliAccounts.buttonLogin";
        button_login_claude("ButtonLoginClaude") = "settings.cliAccounts.buttonLoginClaude";
        button_login_console("ButtonLoginConsole") = "settings.cliAccounts.buttonLoginConsole";
        button_cancel("ButtonCancel") = "settings.cliAccounts.buttonCancel";
        refresh_tooltip("RefreshTooltip") = "settings.cliAccounts.refreshTooltip";
        confirm_logout_title_template("ConfirmLogoutTitleTemplate") = "settings.cliAccounts.confirmLogoutTitleTemplate";
        confirm_change_title_template("ConfirmChangeTitleTemplate") = "settings.cliAccounts.confirmChangeTitleTemplate";
        confirm_message_template("ConfirmMessageTemplate") = "settings.cliAccounts.confirmMessageTemplate";
        summary_signed_out("SummarySignedOut") = "settings.cliAccounts.summarySignedOut";
        summary_unknown("SummaryUnknown") = "settings.cliAccounts.summaryUnknown";
        summary_signed_in("SummarySignedIn") = "settings.cliAccounts.summarySignedIn";
        detail_claude_parse_error("DetailClaudeParseError") = "settings.cliAccounts.detailClaudeParseError";
        detail_codex_parse_error("DetailCodexParseError") = "settings.cliAccounts.detailCodexParseError";
        detail_not_installed_template("DetailNotInstalledTemplate") = "settings.cliAccounts.detailNotInstalledTemplate";
        detail_unsupported_provider("DetailUnsupportedProvider") = "settings.cliAccounts.detailUnsupportedProvider";
        detail_claude_timeout("DetailClaudeTimeout") = "settings.cliAccounts.detailClaudeTimeout";
        detail_claude_unknown("DetailClaudeUnknown") = "settings.cliAccounts.detailClaudeUnknown";
        detail_run_failed("DetailRunFailed") = "settings.cliAccounts.detailRunFailed";
    }
}

/// `MainWindow.CliAccountProviders`: the providers the CLI accounts section lists, every one the app runs, in the provider order.
pub const SECTION_PROVIDERS: [&str; 6] = ["claude", "codex", "gemini", "pi", "opencode", "hermes"];

/// `CliLoginOption`: Claude signs in with its subscription, the API-billed console, or Bedrock's own setup.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum CliLoginOption {
    #[default]
    Account,
    Console,
    Bedrock,
}

/// `CliAccountSupport.FileSizeCap`.
pub const FILE_SIZE_CAP: usize = 1_048_576;
const CLAIMS_DATA_CAP: usize = 65_536;

/// `CliAccountSupport.CodexApiKeyMethod`.
pub fn codex_api_key_method() -> String {
    strings::get("windows.cli.method.apiKey")
}

/// `CliAccountSupport.GeminiGoogleMethod`: the method label of Antigravity's Google sign-in.
pub fn gemini_google_method() -> String {
    strings::get("windows.cli.method.googleAccount")
}

fn status(provider: &str) -> CliAccountStatus {
    CliAccountStatus::new(provider)
}

fn text<'a>(root: &'a Value, key: &str) -> Option<&'a str> {
    meta::text(root, key)
}

/// `Capitalize`: the first character in upper case (one character for one, as `char.ToUpperInvariant` maps it).
fn capitalize(value: &str) -> String {
    let mut chars = value.chars();
    let Some(first) = chars.next() else { return String::new() };
    let mut upper = first.to_uppercase();
    let first = match (upper.next(), upper.next()) {
        (Some(single), None) => single,
        _ => first,
    };
    std::iter::once(first).chain(chars).collect()
}

/// `CliAccountSupport.ParseClaudeStatus`: `claude auth status --json`. An external provider (Bedrock, Vertex AI, Foundry) or an
/// API key is configuration the app cannot sign out of, and no leftover subscription identity labels it.
pub fn parse_claude_status(json: &str) -> CliAccountStatus {
    let parse_error = || CliAccountStatus { detail: copy::detail_claude_parse_error(), ..status("claude") };
    let Ok(root) = serde_json::from_str::<Value>(json) else { return parse_error() };
    let Some(logged_in) = root.as_object().and_then(|o| o.get("loggedIn")).and_then(Value::as_bool) else { return parse_error() };
    let api_provider = text(&root, "apiProvider");
    let auth_method = text(&root, "authMethod");
    let external = match api_provider {
        Some("bedrock") => Some(("AWS Bedrock", method::CLAUDE_BEDROCK)),
        Some("vertex") => Some(("Google Vertex AI", method::CLAUDE_VERTEX)),
        Some("foundry") => Some(("Microsoft Foundry", method::CLAUDE_FOUNDRY)),
        _ => None,
    };
    if external.is_some() || matches!(auth_method, Some("third_party" | "api_key")) {
        let (label, id) = external.unwrap_or(if auth_method == Some("api_key") {
            ("Anthropic API key", method::CLAUDE_API_KEY)
        } else {
            ("External provider", method::CLAUDE_EXTERNAL)
        });
        return CliAccountStatus {
            logged_in: Some(logged_in),
            method: Some(label.into()),
            method_id: Some(id.into()),
            can_sign_out: false,
            access_verified: Some(false),
            detail: if label == "AWS Bedrock" { bedrock::configuration_only() } else { bedrock::external_configuration_only() },
            ..status("claude")
        };
    }
    if !logged_in {
        return CliAccountStatus { logged_in: Some(false), ..status("claude") };
    }
    let method = match auth_method {
        Some("claude.ai") => Some(strings::get("windows.cli.method.claudeSubscription")),
        Some("console") => Some("Anthropic Console".to_string()),
        Some(other) if !other.is_empty() => Some(clean(other)),
        _ => None,
    };
    let method_id = match auth_method {
        Some("claude.ai") => Some(method::CLAUDE_SUBSCRIPTION),
        Some("console") => Some(method::CLAUDE_CONSOLE),
        Some("none") => None,
        Some(other) if !other.is_empty() => Some(method::CLAUDE_OTHER),
        _ => None,
    };
    let non_empty = |key: &str| text(&root, key).filter(|s| !s.is_empty()).map(clean);
    let plan = non_empty("subscriptionType").map(|s| capitalize(&s));
    let account = non_empty("email").or_else(|| non_empty("orgName"));
    CliAccountStatus { logged_in: Some(true), method, method_id: method_id.map(str::to_string), account, plan, ..status("claude") }
}

/// `CliAccountSupport.ParseCodexStatus`: `codex login status`'s words, and the account claims inside `auth.json`'s id token (the
/// e-mail and the ChatGPT plan; no other part of any token is kept).
pub fn parse_codex_status(text: &str, auth_json: Option<&str>) -> CliAccountStatus {
    let lower = text.to_lowercase();
    if !lower.contains("logged in") && !lower.contains("not logged in") {
        return CliAccountStatus { detail: copy::detail_codex_parse_error(), ..status("codex") };
    }
    if lower.contains("not logged in") {
        return CliAccountStatus { logged_in: Some(false), ..status("codex") };
    }
    let (method, method_id) = if lower.contains("chatgpt") {
        (Some("ChatGPT".to_string()), Some(method::CODEX_CHATGPT))
    } else if lower.contains("api key") {
        (Some(codex_api_key_method()), Some(method::CODEX_API_KEY))
    } else {
        (None, None)
    };
    let (mut account, mut plan) = (None, None);
    if let Some(claims) = auth_json
        .filter(|json| !json.is_empty())
        .and_then(|json| serde_json::from_str::<Value>(json).ok())
        .and_then(|root| root.get("tokens")?.get("id_token")?.as_str().and_then(jwt_claims))
    {
        account = claims.get("email").and_then(Value::as_str).map(clean);
        plan = claims
            .get("https://api.openai.com/auth")
            .filter(|auth| auth.is_object())
            .and_then(|auth| auth.get("chatgpt_plan_type"))
            .and_then(Value::as_str)
            .filter(|plan| !plan.is_empty())
            .map(|plan| capitalize(&clean(plan)));
    }
    CliAccountStatus { logged_in: Some(true), method, method_id: method_id.map(str::to_string), account, plan, ..status("codex") }
}

/// `CliAccountSupport.AntigravityStatus`: Antigravity has no status command and keeps its credentials in the system's credential
/// store, so its model list (`agy models`) stands in: signed in only for a clean list with exit 0 (agy 1.5.x says it is signed out
/// with exit 0, in its own words); anything else (no network, a timeout) stays unknown. It has no sign-out command either.
pub fn antigravity_status(exit_code: i32, output: &str, timed_out: bool) -> CliAccountStatus {
    let base = CliAccountStatus { can_sign_out: false, ..status("gemini") };
    if mighty_run::failure::antigravity(output) {
        return CliAccountStatus {
            logged_in: Some(false),
            method: Some(gemini_google_method()),
            method_id: Some(method::GEMINI_GOOGLE.into()),
            ..base
        };
    }
    if exit_code == 0 && !timed_out {
        return CliAccountStatus {
            logged_in: Some(true),
            method: Some(gemini_google_method()),
            method_id: Some(method::GEMINI_GOOGLE.into()),
            detail: strings::get("settings.cliAccounts.detailAntigravityNoSignOut"),
            ..base
        };
    }
    let key = if timed_out { "settings.cliAccounts.detailAntigravityTimeout" } else { "settings.cliAccounts.detailAntigravityUnknown" };
    CliAccountStatus { detail: strings::get(key), ..base }
}

/// `CliAccountSupport.LoginArguments`: argv of the CLI's own sign-in, handed to the external terminal. Claude has the console and
/// Bedrock's setup besides its account sign-in; the others are mighty-run's (`acp::accounts::login_arguments`).
pub fn login_arguments(provider: &str, option: CliLoginOption) -> Option<Vec<&'static str>> {
    match (provider, option) {
        ("claude", CliLoginOption::Bedrock) => Some(vec!["claude", "/setup-bedrock"]),
        ("claude", CliLoginOption::Console) => Some(vec!["claude", "auth", "login", "--console"]),
        _ => acp::login_arguments(provider),
    }
}

/// `Path.IsPathRooted` under the OS rules the host chose.
pub(crate) fn rooted(path: &str) -> bool {
    if os::windows() {
        let bytes = path.as_bytes();
        path.starts_with(['\\', '/']) || bytes.len() >= 2 && bytes[1] == b':' && bytes[0].is_ascii_alphabetic()
    } else {
        path.starts_with('/')
    }
}

/// A folder of the environment's, else (on Windows) the user profile's as this process has it (C#'s
/// `Environment.GetFolderPath`): only a full path, never one holding a NUL.
fn folder(environment: &BTreeMap<String, String>, variable: &str) -> Option<String> {
    let usable = |path: &str| !path.is_empty() && !path.contains('\0') && rooted(path);
    env_get(environment, variable)
        .filter(|path| usable(path))
        .map(str::to_string)
        .or_else(|| os::windows().then(|| std::env::var(variable).ok().filter(|path| usable(path))).flatten())
}

/// `CliAccountSupport.InstallerCopies`: where a CLI's own installer leaves it, looked in when that folder is not on PATH yet:
/// opencode's npm shim in `%APPDATA%\npm`, and Hermes's `hermes.exe` (or the `hermes.cmd` its installer falls back to) in
/// `$HERMES_HOME\bin`, by default `%LOCALAPPDATA%\hermes\bin`.
pub fn installer_copies(name: &str, environment: &BTreeMap<String, String>) -> Vec<String> {
    match name {
        "opencode" => folder(environment, "APPDATA")
            .map(|roaming| vec![os::combine(&os::combine(&roaming, "npm"), "opencode.cmd")])
            .unwrap_or_default(),
        "hermes" => {
            let home = env_get(environment, "HERMES_HOME")
                .filter(|path| !path.is_empty() && !path.contains('\0') && rooted(path))
                .map(str::to_string)
                .or_else(|| folder(environment, "LOCALAPPDATA").map(|local| os::combine(&local, "hermes")));
            home.map(|home| {
                let bin = os::combine(&home, "bin");
                vec![os::combine(&bin, "hermes.exe"), os::combine(&bin, "hermes.cmd")]
            })
            .unwrap_or_default()
        }
        _ => vec![],
    }
}

/// `CliAccountSupport.CodexHome`: Codex keeps its files under `$CODEX_HOME` when that is set.
pub fn codex_home(home: &str, environment: &BTreeMap<String, String>) -> String {
    match env_get(environment, "CODEX_HOME").filter(|value| !value.is_empty()) {
        Some(codex) => codex.to_string(),
        None => os::combine(home, ".codex"),
    }
}

/// `CliAccountSupport.JwtClaims`: a JWT's payload claims, or None for a malformed token or a payload past 64 KiB. The token is never
/// kept or logged.
pub fn jwt_claims(token: &str) -> Option<Map<String, Value>> {
    let parts: Vec<&str> = token.split('.').collect();
    if parts.len() != 3 {
        return None;
    }
    let mut payload = parts[1].replace('-', "+").replace('_', "/");
    while !payload.len().is_multiple_of(4) {
        payload.push('=');
    }
    let data = base64::engine::general_purpose::STANDARD.decode(payload).ok()?;
    if data.len() > CLAIMS_DATA_CAP {
        return None;
    }
    match serde_json::from_slice::<Value>(&data).ok()? {
        Value::Object(claims) => Some(claims),
        _ => None,
    }
}

/// `CliAccountSupport.BoundedFileText`: a small regular file's text (UTF-8, a byte order mark dropped), its size checked before it
/// is read; None for a folder, a link, a file past `max_bytes` or one that cannot be read. The size is the opened file's and at most
/// one byte past the bound is read, so a file swapped or grown after the check is still refused rather than read whole.
pub fn bounded_file_text(path: &Path, max_bytes: usize) -> Option<String> {
    let info = std::fs::symlink_metadata(path).ok()?;
    if info.is_dir() || info.file_type().is_symlink() {
        return None;
    }
    let file = std::fs::File::open(path).ok()?;
    let info = file.metadata().ok()?;
    if !info.is_file() || info.len() > max_bytes as u64 {
        return None;
    }
    let mut bytes = Vec::new();
    std::io::Read::read_to_end(&mut std::io::Read::take(file, max_bytes as u64 + 1), &mut bytes).ok()?;
    if bytes.len() > max_bytes {
        return None;
    }
    let bytes = bytes.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(&bytes);
    Some(String::from_utf8_lossy(bytes).into_owned())
}

// ── pi ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `PiAgent.ApiKeyVariables`: the API key variables pi reads. Any one non-empty counts as a sign-in.
pub const PI_API_KEY_VARIABLES: [&str; 26] = [
    "ANTHROPIC_API_KEY",
    "ANTHROPIC_OAUTH_TOKEN",
    "OPENAI_API_KEY",
    "AZURE_OPENAI_API_KEY",
    "DEEPSEEK_API_KEY",
    "GEMINI_API_KEY",
    "GROQ_API_KEY",
    "CEREBRAS_API_KEY",
    "XAI_API_KEY",
    "FIREWORKS_API_KEY",
    "OPENROUTER_API_KEY",
    "AI_GATEWAY_API_KEY",
    "ZAI_API_KEY",
    "MISTRAL_API_KEY",
    "MINIMAX_API_KEY",
    "MINIMAX_CN_API_KEY",
    "MOONSHOT_API_KEY",
    "OPENCODE_API_KEY",
    "KIMI_API_KEY",
    "CLOUDFLARE_API_KEY",
    "XIAOMI_API_KEY",
    "XIAOMI_TOKEN_PLAN_CN_API_KEY",
    "XIAOMI_TOKEN_PLAN_AMS_API_KEY",
    "XIAOMI_TOKEN_PLAN_SGP_API_KEY",
    "HF_TOKEN",
    "AWS_BEARER_TOKEN_BEDROCK",
];

/// `PiAgent.AuthFile`: `auth.json` in `$PI_CODING_AGENT_DIR` (when absolute), else in `~/.pi/agent`.
pub fn pi_auth_file(home: &str, environment: &BTreeMap<String, String>) -> String {
    // `Absolute`: a `/` path, or one `Path.IsPathFullyQualified` takes (a drive and its separator, or a UNC share).
    let absolute = |path: &str| {
        let bytes = path.as_bytes();
        path.starts_with('/')
            || os::windows()
                && (path.starts_with("\\\\")
                    || bytes.len() >= 3 && bytes[0].is_ascii_alphabetic() && bytes[1] == b':' && matches!(bytes[2], b'\\' | b'/'))
    };
    let directory = match env_get(environment, "PI_CODING_AGENT_DIR").filter(|given| !given.is_empty() && absolute(given)) {
        Some(given) => given.to_string(),
        None => os::combine(&os::combine(home, ".pi"), "agent"),
    };
    os::combine(&directory, "auth.json")
}

/// `PiAgent.Credentials`: the providers `auth.json` holds a credential for (sorted, at most 16, names only), and whether one of them
/// is a subscription (OAuth) sign-in rather than an API key.
pub fn pi_credentials(auth_json: Option<&str>) -> (Vec<String>, bool) {
    let Some(Value::Object(root)) = auth_json.and_then(|json| serde_json::from_str::<Value>(json).ok()) else { return (vec![], false) };
    let entries: Vec<(&String, &Value)> = root.iter().filter(|(_, value)| value.is_object()).collect();
    let mut names: Vec<&String> = entries.iter().map(|(name, _)| *name).collect();
    names.sort_by(|a, b| a.encode_utf16().cmp(b.encode_utf16()));
    let names = names.into_iter().take(16).map(|name| mighty_core::wire::clean(name, 64)).collect();
    (names, entries.iter().any(|(_, value)| meta::text(value, "type") == Some("oauth")))
}

/// `PiAgent.EnvironmentKeys`: the API key variables set to something.
pub fn pi_environment_keys(environment: &BTreeMap<String, String>) -> Vec<&'static str> {
    PI_API_KEY_VARIABLES.into_iter().filter(|name| env_get(environment, name).is_some_and(|value| !value.is_empty())).collect()
}

/// `PiAccounts.Status`: pi has no status command. It is signed in when it can list a model; what with comes from `auth.json` (a
/// subscription or API keys) or the environment's API keys, and a list from `models.json` alone still counts. "No models
/// available" reads signed out; a failed or timed-out call stays unknown. The app signs nothing out.
pub fn pi_status(
    exit_code: i32,
    output: &str,
    auth_json: Option<&str>,
    environment: &BTreeMap<String, String>,
    timed_out: bool,
) -> CliAccountStatus {
    let base = CliAccountStatus { can_sign_out: false, ..status("pi") };
    if output.to_lowercase().contains("no models available") {
        return CliAccountStatus { logged_in: Some(false), ..base };
    }
    if exit_code != 0 || timed_out {
        let key = if timed_out { "settings.cliAccounts.detailPiTimeout" } else { "settings.cliAccounts.detailPiUnknown" };
        return CliAccountStatus { detail: strings::get(key), ..base };
    }
    let (providers, subscription) = pi_credentials(auth_json);
    if !providers.is_empty() {
        return CliAccountStatus {
            logged_in: Some(true),
            method: Some(strings::get(if subscription { "cliAccounts.method.piSubscription" } else { "windows.cli.method.apiKey" })),
            method_id: Some(if subscription { method::PI_SUBSCRIPTION } else { method::PI_API_KEY }.into()),
            account: Some(providers.join(", ")),
            detail: strings::get("settings.cliAccounts.detailPiSignOut"),
            ..base
        };
    }
    if !pi_environment_keys(environment).is_empty() {
        return CliAccountStatus {
            logged_in: Some(true),
            method: Some(strings::get("cliAccounts.method.piEnvironmentKey")),
            method_id: Some(method::PI_API_KEY.into()),
            ..base
        };
    }
    CliAccountStatus {
        logged_in: Some(true),
        method: Some(strings::get("cliAccounts.method.piModelsFile")),
        method_id: Some(method::PI_MODELS.into()),
        ..base
    }
}

// ── the runner ────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `CliRunResult`: the exit code (-1 when the time limit killed the child), both outputs as text, and whether it timed out.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CliRunResult {
    pub exit_code: i32,
    pub output: String,
    pub error_output: String,
    pub timed_out: bool,
}

/// C# `ICliRunner`: runs a CLI found at `executable` (a full path) to its end within `timeout`. A child that cannot start is an
/// error; one past its time is killed with everything it started and reported timed out with exit code -1.
pub trait CliRunner: Send + Sync {
    fn run<'a>(
        &'a self,
        executable: &'a str,
        arguments: &'a [String],
        timeout: Duration,
        environment: &'a BTreeMap<String, String>,
        working_directory: Option<&'a Path>,
    ) -> BoxFuture<'a, mighty_platform::Result<CliRunResult>>;
}

/// C# `CliRunner.OutputCapBytes`: each output is kept to 1 MiB while the pipe goes on being drained.
pub const OUTPUT_CAP_BYTES: usize = 1024 * 1024;

/// [`CliRunner`] over the platform's `ProcessHost`: the CLI starts as `ProviderCatalog.StartCli` starts one (a `.cmd` shim through
/// cmd.exe on Windows), with its standard input closed, both outputs drained to their end and capped, in `working_directory` when
/// that is an existing folder, else the temporary folder.
pub struct HostRunner {
    processes: Arc<dyn ProcessHost>,
}

impl HostRunner {
    pub fn new(processes: Arc<dyn ProcessHost>) -> HostRunner {
        HostRunner { processes }
    }
}

async fn read_capped(reader: Option<ChildReader>) -> String {
    let Some(mut reader) = reader else { return String::new() };
    let mut kept = Vec::new();
    let mut buffer = [0u8; 8192];
    while let Ok(read) = reader.read(&mut buffer).await {
        if read == 0 {
            break;
        }
        let room = OUTPUT_CAP_BYTES.saturating_sub(kept.len());
        kept.extend_from_slice(&buffer[..read.min(room)]);
    }
    String::from_utf8_lossy(&kept).into_owned()
}

impl CliRunner for HostRunner {
    fn run<'a>(
        &'a self,
        executable: &'a str,
        arguments: &'a [String],
        timeout: Duration,
        environment: &'a BTreeMap<String, String>,
        working_directory: Option<&'a Path>,
    ) -> BoxFuture<'a, mighty_platform::Result<CliRunResult>> {
        Box::pin(async move {
            let cwd = working_directory.filter(|folder| folder.is_dir()).map(Path::to_path_buf).unwrap_or_else(std::env::temp_dir);
            let request = self.processes.cli_request(Path::new(executable), arguments, &cwd, environment)?;
            let mut child = self.processes.spawn(request).await?;
            let control = child.control();
            drop(child.stdin.take());
            let output = tokio::spawn(read_capped(child.stdout.take()));
            let error = tokio::spawn(read_capped(child.stderr.take()));
            let (exit_code, timed_out) = match tokio::time::timeout(timeout, control.wait()).await {
                Ok(code) => (code?, false),
                Err(_) => {
                    control.kill();
                    (-1, true)
                }
            };
            let grace = Duration::from_millis(if timed_out { 1000 } else { 500 });
            let output = tokio::time::timeout(grace, output).await.ok().and_then(Result::ok).unwrap_or_default();
            let error_output = tokio::time::timeout(grace, error).await.ok().and_then(Result::ok).unwrap_or_default();
            control.close().await?;
            Ok(CliRunResult { exit_code, output, error_output, timed_out })
        })
    }
}

// ── the coordinator ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// How a found `hermes` is asked for its sign-in (`HermesAccounts.ProbeAsync` by default).
pub type HermesProbe = Arc<dyn Fn(CliCommand, BTreeMap<String, String>) -> BoxFuture<'static, Option<acp::Probe>> + Send + Sync>;

/// A sign-in terminal that opened (C# `StartSignInAsync`'s task and its `opened` callback): whether its window closes with the
/// sign-in's process (the console host; a Windows Terminal tab hands off at once), and the window itself, whose end the caller may
/// await.
pub struct SignInWindow {
    pub closes_with_window: bool,
    pub window: Box<dyn TerminalWindow>,
}

impl std::fmt::Debug for SignInWindow {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SignInWindow").field("closes_with_window", &self.closes_with_window).finish_non_exhaustive()
    }
}

/// C# `CliAccountsCoordinator`: reads each CLI's account status through the runner (Antigravity: its model list; Hermes: its ACP
/// server's answer to `initialize`), signs out through the CLI's own command and opens its own sign-in in a terminal.
///
/// Signing out and signing in change the user's CLI credentials: the host calls [`logout`](Self::logout) and
/// [`start_sign_in`](Self::start_sign_in) only for an explicit user action it allows (`--allow-state-write` until W9).
pub struct CliAccountsCoordinator {
    runner: Arc<dyn CliRunner>,
    catalog: Arc<ProviderCatalog>,
    fixed_environment: Option<BTreeMap<String, String>>,
    home: String,
    hermes_probe: Option<HermesProbe>,
    terminal: Option<Arc<dyn TerminalLauncher>>,
    statuses: Mutex<HashMap<String, CliAccountStatus>>,
}

/// `StatusTimeout`.
pub const STATUS_TIMEOUT: Duration = Duration::from_secs(20);
/// `LogoutTimeout`.
pub const LOGOUT_TIMEOUT: Duration = Duration::from_secs(30);
/// `VersionTimeout`.
pub const VERSION_TIMEOUT: Duration = Duration::from_secs(10);

impl CliAccountsCoordinator {
    /// `runner` runs the status and logout commands; `catalog` gives the CLI environment and starts Hermes's probe; `home` is the
    /// user's folder (the commands start there, and the account files are read under it).
    pub fn new(runner: Arc<dyn CliRunner>, catalog: Arc<ProviderCatalog>, home: impl Into<String>) -> CliAccountsCoordinator {
        CliAccountsCoordinator {
            runner,
            catalog,
            fixed_environment: None,
            home: home.into(),
            hermes_probe: None,
            terminal: None,
            statuses: Mutex::new(HashMap::new()),
        }
    }

    /// The checks' fixed environment, in place of the catalog's CLI environment (which is then never resolved).
    pub fn with_environment(mut self, environment: BTreeMap<String, String>) -> CliAccountsCoordinator {
        self.fixed_environment = Some(environment);
        self
    }

    /// How a found `hermes` is asked (the checks' fake); by default `HermesAccounts.ProbeAsync` in the temporary folder.
    pub fn with_hermes_probe(mut self, probe: HermesProbe) -> CliAccountsCoordinator {
        self.hermes_probe = Some(probe);
        self
    }

    /// The window a sign-in opens in (mighty-platform's `TerminalLauncher`).
    pub fn with_terminal(mut self, launcher: Arc<dyn TerminalLauncher>) -> CliAccountsCoordinator {
        self.terminal = Some(launcher);
        self
    }

    /// `Statuses`: the last status read of each provider.
    pub fn statuses(&self) -> HashMap<String, CliAccountStatus> {
        lock(&self.statuses).clone()
    }

    pub fn status_of(&self, provider: &str) -> Option<CliAccountStatus> {
        lock(&self.statuses).get(provider).cloned()
    }

    async fn environment(&self, force: bool) -> BTreeMap<String, String> {
        match &self.fixed_environment {
            Some(fixed) => fixed.clone(),
            None => self.catalog.environment().resolve(None, force).await.values,
        }
    }

    /// `RefreshAsync`: every provider's status read again, all at once.
    pub async fn refresh(&self, providers: &[&str]) {
        if self.fixed_environment.is_none() {
            self.environment(true).await;
        }
        let reads: Vec<BoxFuture<'_, (String, CliAccountStatus)>> = providers
            .iter()
            .map(|provider| -> BoxFuture<'_, (String, CliAccountStatus)> {
                let provider = provider.to_string();
                Box::pin(async move {
                    let value = self.status(&provider).await;
                    (provider, value)
                })
            })
            .collect();
        for (provider, value) in crate::join_all(reads).await {
            lock(&self.statuses).insert(provider, value);
        }
    }

    /// `StatusAsync`: one provider's status, read now.
    pub async fn status(&self, provider: &str) -> CliAccountStatus {
        let environment = self.environment(false).await;
        let Some(argv) = status_arguments(provider) else {
            return CliAccountStatus {
                installed: false,
                detail: copy::detail_unsupported_provider(),
                can_sign_out: false,
                ..status(provider)
            };
        };
        let Some(binary) = self.find_binary(argv[0], &environment) else {
            let detail = copy::detail_not_installed_template().replace("{provider}", strings::provider_label(provider));
            return CliAccountStatus { installed: false, detail, can_sign_out: false, ..status(provider) };
        };
        // Hermes has no status command: its ACP server's initialize answer names the provider whose credentials resolve. The server
        // is started and asked alone, never through the runner, which gives no standard input.
        if provider == "hermes" {
            let probe = self.probe_hermes(CliCommand::new(binary, vec![], None), environment).await;
            return acp::hermes_status(probe.as_ref());
        }
        let mut env = environment.clone();
        env.insert("NO_COLOR".into(), "1".into());
        env.insert("CI".into(), "1".into());
        let arguments: Vec<String> = argv[1..].iter().map(|a| a.to_string()).collect();
        let home = PathBuf::from(&self.home);
        let Ok(result) = self.runner.run(&binary, &arguments, STATUS_TIMEOUT, &env, Some(&home)).await else {
            return CliAccountStatus { detail: copy::detail_run_failed(), ..status(provider) };
        };
        match provider {
            "pi" => {
                let auth = bounded_file_text(Path::new(&pi_auth_file(&self.home, &environment)), FILE_SIZE_CAP);
                pi_status(
                    result.exit_code,
                    &format!("{}\n{}", result.output, result.error_output),
                    auth.as_deref(),
                    &environment,
                    result.timed_out,
                )
            }
            "opencode" => {
                // A list that is not JSON may be OpenCode 1, which has no --format: its version says.
                let mut version = None;
                if acp::opencode_connections(&result.output).is_none() {
                    let flag = vec!["--version".to_string()];
                    if let Ok(named) = self.runner.run(&binary, &flag, VERSION_TIMEOUT, &env, Some(&home)).await
                        && named.exit_code == 0
                        && !named.timed_out
                    {
                        version = Some(named.output.trim().to_string());
                    }
                }
                acp::opencode_status(result.exit_code, &result.output, version.as_deref(), result.timed_out)
            }
            "gemini" => antigravity_status(result.exit_code, &format!("{}\n{}", result.output, result.error_output), result.timed_out),
            "claude" => {
                let mut value = parse_claude_status(&result.output);
                if value.logged_in.is_none() {
                    value.detail = if result.timed_out { copy::detail_claude_timeout() } else { copy::detail_claude_unknown() };
                }
                if value.method_id.as_deref() == Some(method::CLAUDE_BEDROCK) {
                    let settings = bounded_file_text(Path::new(&bedrock::settings_path(&self.home, &environment)), FILE_SIZE_CAP);
                    if let Some(detail) = bedrock::conflict_detail(&environment, settings.as_deref()) {
                        value.detail = format!("{} {detail}", value.detail);
                    }
                }
                value
            }
            _ => {
                let text = if result.error_output.is_empty() {
                    result.output.clone()
                } else {
                    format!("{}\n{}", result.output, result.error_output)
                };
                let auth = Path::new(&codex_home(&self.home, &environment)).join("auth.json");
                parse_codex_status(&text, bounded_file_text(&auth, FILE_SIZE_CAP).as_deref())
            }
        }
    }

    async fn probe_hermes(&self, command: CliCommand, environment: BTreeMap<String, String>) -> Option<acp::Probe> {
        match &self.hermes_probe {
            Some(probe) => probe(command, environment).await,
            None => acp::hermes_probe(&self.catalog, &command, &environment, &std::env::temp_dir(), acp::HERMES_PROBE_TIMEOUT).await,
        }
    }

    /// `LogoutAsync`: the CLI's own logout command (Antigravity, pi, opencode and Hermes have none the app runs, so their status
    /// comes back as it was), then the status read again.
    pub async fn logout(&self, provider: &str) -> CliAccountStatus {
        let environment = self.environment(true).await;
        if let Some(argv) = logout_arguments(provider)
            && let Some(binary) = self.find_binary(argv[0], &environment)
        {
            let mut env = environment.clone();
            env.insert("NO_COLOR".into(), "1".into());
            env.insert("CI".into(), "1".into());
            let arguments: Vec<String> = argv[1..].iter().map(|a| a.to_string()).collect();
            let home = PathBuf::from(&self.home);
            let _ = self.runner.run(&binary, &arguments, LOGOUT_TIMEOUT, &env, Some(&home)).await;
        }
        let value = self.status(provider).await;
        lock(&self.statuses).insert(provider.to_string(), value.clone());
        value
    }

    /// `StartSignInAsync`: opens the external sign-in terminal on the CLI's own login command. The app never types or receives
    /// credentials; it only starts the command, handed over as a separate argument list (no command line is built from text).
    /// `directory`: the folder the command starts in (a terminal sign-in from a failed pane opens in its workspace); `title`: the
    /// Windows Terminal tab's title. A CLI of [`CliTerminalSignIn::table`] starts by its full path (agy may sit only in its
    /// installer's folder, pi is an npm shim the console host cannot find by name).
    pub async fn start_sign_in(
        &self,
        login_argv: &[String],
        directory: Option<&str>,
        title: Option<&str>,
    ) -> mighty_platform::Result<SignInWindow> {
        let Some(launcher) = self.terminal.clone() else { return Err(PlatformError::unsupported("terminal", "open")) };
        let environment = self.environment(true).await;
        let directory = directory.filter(|folder| Path::new(folder).is_dir());
        let mut argv = login_argv.to_vec();
        let resolved = argv
            .first()
            .filter(|name| {
                CliTerminalSignIn::table().iter().any(|entry| entry.command().and_then(|c| c.first().copied()) == Some(name.as_str()))
            })
            .and_then(|name| self.find_binary(name, &environment));
        if let Some(path) = &resolved {
            argv[0] = path.clone();
        }
        let bedrock = argv.iter().any(|a| a == "/setup-bedrock");
        let mut env = environment;
        if bedrock {
            env.insert("CLAUDE_CODE_USE_BEDROCK".into(), "1".into());
        }
        // Bedrock's setup holds the account until its window closes, so it takes the console host, never a Windows Terminal tab.
        let window = launcher.open(TerminalLaunch {
            argv,
            resolved,
            directory: directory.map(PathBuf::from),
            title: title.map(str::to_string),
            environment: env,
            prefer_terminal_app: !bedrock,
        })?;
        let closes_with_window = window.holds_command();
        Ok(SignInWindow { closes_with_window, window })
    }

    /// `FindBinary`: the CLI along the environment's PATH (the first 64 folders that are full paths; on Windows `.exe`, `.cmd`,
    /// `.bat`, then the bare name), else where its installer leaves it: agy's own folder, npm's for pi, opencode's and Hermes's.
    pub fn find_binary(&self, name: &str, environment: &BTreeMap<String, String>) -> Option<String> {
        find_binary(name, environment)
    }
}

/// `CliAccountsCoordinator.FindBinary`.
pub fn find_binary(name: &str, environment: &BTreeMap<String, String>) -> Option<String> {
    let windows = os::windows();
    let path = env_get(environment, "PATH").unwrap_or("");
    let extensions: &[&str] = if windows { &[".exe", ".cmd", ".bat", ""] } else { &[""] };
    for directory in path.split(os::path_separator()).take(64) {
        if directory.is_empty() || directory.contains('\0') || !rooted(directory) {
            continue;
        }
        for extension in extensions {
            let candidate = os::combine(directory, &format!("{name}{extension}"));
            if Path::new(&candidate).is_file() {
                return Some(candidate);
            }
        }
    }
    if windows && name == "agy" {
        // Antigravity's installer folder reaches PATH for new sessions only.
        let installed = os::combine(&antigravity_install_folder(environment), "agy.exe");
        if Path::new(&installed).is_file() {
            return Some(installed);
        }
    }
    if windows
        && name == "pi"
        && let Some(roaming) = folder(environment, "APPDATA")
    {
        // npm's global folder, where pi.cmd is, is looked in as the CLIs are found.
        let shim = os::combine(&os::combine(&roaming, "npm"), "pi.cmd");
        if Path::new(&shim).is_file() {
            return Some(shim);
        }
    }
    installer_copies(name, environment).into_iter().find(|copy| Path::new(copy).is_file())
}

/// `ProviderCatalog.AntigravityInstallFolder`: `%LOCALAPPDATA%\agy\bin`.
pub fn antigravity_install_folder(environment: &BTreeMap<String, String>) -> String {
    let local = env_get(environment, "LOCALAPPDATA")
        .filter(|value| !value.is_empty())
        .map(str::to_string)
        .or_else(|| std::env::var("LOCALAPPDATA").ok())
        .unwrap_or_default();
    os::combine(&os::combine(&local, "agy"), "bin")
}

// ── the smoke ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `CliAccountSmokeOutcome`, recorded under [`CliAccountSmokeOutcome::RESULT_KEY`].
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CliAccountSmokeOutcome {
    pub summaries: Vec<String>,
    pub confirmation_shown: bool,
    pub cancelled: bool,
}

impl CliAccountSmokeOutcome {
    pub const RESULT_KEY: &'static str = "cliAccountsSection";
}

/// `CliAccountSmoke.FixtureStatuses`: signed in (an account and a plan), signed out, not installed, and one that cannot sign out.
pub fn smoke_fixture_statuses() -> Vec<CliAccountStatus> {
    vec![
        CliAccountStatus {
            logged_in: Some(true),
            account: Some("me@example.com".into()),
            plan: Some("Max".into()),
            method: Some(strings::get("windows.cli.method.claudeSubscription")),
            method_id: Some(method::CLAUDE_SUBSCRIPTION.into()),
            ..status("claude")
        },
        CliAccountStatus { logged_in: Some(false), ..status("codex") },
        CliAccountStatus {
            installed: false,
            detail: copy::detail_not_installed_template().replace("{provider}", strings::provider_label("gemini")),
            can_sign_out: false,
            ..status("gemini")
        },
        CliAccountStatus { installed: true, ..antigravity_status(0, "", false) },
    ]
}

/// `CliAccountSmoke.RunAsync`: checks that the summaries the section drew show every required status, then triggers the sign-out
/// confirmation of the first provider that can sign out and cancels it, through the gestures' callbacks. Err says what is missing.
pub async fn run_smoke<L, C>(
    rendered_summaries: &[String],
    trigger_logout: impl FnOnce(String) -> L,
    cancel: impl FnOnce() -> C,
) -> Result<CliAccountSmokeOutcome, String>
where
    L: std::future::Future<Output = ()>,
    C: std::future::Future<Output = ()>,
{
    if !rendered_summaries.iter().any(|s| s.contains("me@example.com")) {
        return Err("smoke: fixture must show a signed-in status with an account label".into());
    }
    if !rendered_summaries.contains(&copy::summary_signed_out()) {
        return Err("smoke: fixture must show a signed-out status".into());
    }
    let fixtures = smoke_fixture_statuses();
    if !rendered_summaries.iter().any(|s| *s == fixtures[2].detail || *s == copy::status_not_installed()) {
        return Err("smoke: fixture must show a not-installed status".into());
    }
    let Some(target) = fixtures.iter().find(|s| s.can_sign_out && s.logged_in == Some(true)) else {
        return Err("smoke: no fixture can sign out".into());
    };
    trigger_logout(target.provider.clone()).await;
    cancel().await;
    Ok(CliAccountSmokeOutcome { summaries: rendered_summaries.to_vec(), confirmation_shown: true, cancelled: true })
}
