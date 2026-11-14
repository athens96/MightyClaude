//! C# CliAccountVerification (MightyCode.Core.Tests/CliAccountVerification.cs, Verification.cs:1389-1394), the account half of
//! StringsVerification (`CliAccountStringsMatchMacOS`, :1388), AcpAgentAccountVerification's coordinator checks (:1396, :1399,
//! :1400) and PiVerification's sign-in checks (:562, :563), against the fake runner and temporary homes. With them: the runner over
//! the fake process host, the sign-in terminal over the recording launcher, and (Windows only) the coordinator over the real
//! process layer and copies of the stand-in CLI.

mod common;

use std::sync::{Arc, Mutex};
use std::time::Duration;

use base64::Engine;
use common::{FakeRunner, catalog, env, ok, result, temp, text, touch};
#[cfg(windows)]
use common::{TerminalHosts, launch_plan};
use mighty_accounts::accounts::{self, CliAccountStatus, CliAccountsCoordinator, CliLoginOption, CliRunner, HostRunner, copy, method};
use mighty_platform::fake::RecordingTerminalLauncher;
use mighty_platform::fake::{FakeProcessHost, FakeScript};
use mighty_run::acp::accounts::Probe;
use mighty_run::model::CliCommand;
use mighty_run::strings;

fn jwt(claims: serde_json::Value) -> String {
    let encode = |data: &[u8]| base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(data);
    format!("{}.{}.sig", encode(br#"{"alg":"none"}"#), encode(claims.to_string().as_bytes()))
}

fn strings_of(values: &[&str]) -> Vec<String> {
    values.iter().map(|v| v.to_string()).collect()
}

/// The fields `FromShared` maps for `CliAccountStrings` (StringsVerification.cs:452-482): the key each must read.
const CLI_ACCOUNT_SHARED: [(&str, &str); 26] = [
    ("SectionTitle", "settings.cliAccounts.sectionTitle"),
    ("SectionDescription", "settings.cliAccounts.sectionDescriptionWindows"),
    ("StatusChecking", "settings.cliAccounts.statusChecking"),
    ("StatusPending", "settings.cliAccounts.statusPending"),
    ("StatusNotInstalled", "settings.cliAccounts.statusNotInstalled"),
    ("ButtonCancelWait", "settings.cliAccounts.buttonCancelWait"),
    ("ButtonChange", "settings.cliAccounts.buttonChange"),
    ("ButtonLogout", "settings.cliAccounts.buttonLogout"),
    ("ButtonLogin", "settings.cliAccounts.buttonLogin"),
    ("ButtonLoginClaude", "settings.cliAccounts.buttonLoginClaude"),
    ("ButtonLoginConsole", "settings.cliAccounts.buttonLoginConsole"),
    ("ButtonCancel", "settings.cliAccounts.buttonCancel"),
    ("RefreshTooltip", "settings.cliAccounts.refreshTooltip"),
    ("ConfirmLogoutTitleTemplate", "settings.cliAccounts.confirmLogoutTitleTemplate"),
    ("ConfirmChangeTitleTemplate", "settings.cliAccounts.confirmChangeTitleTemplate"),
    ("ConfirmMessageTemplate", "settings.cliAccounts.confirmMessageTemplate"),
    ("SummarySignedOut", "settings.cliAccounts.summarySignedOut"),
    ("SummaryUnknown", "settings.cliAccounts.summaryUnknown"),
    ("SummarySignedIn", "settings.cliAccounts.summarySignedIn"),
    ("DetailClaudeParseError", "settings.cliAccounts.detailClaudeParseError"),
    ("DetailCodexParseError", "settings.cliAccounts.detailCodexParseError"),
    ("DetailNotInstalledTemplate", "settings.cliAccounts.detailNotInstalledTemplate"),
    ("DetailUnsupportedProvider", "settings.cliAccounts.detailUnsupportedProvider"),
    ("DetailClaudeTimeout", "settings.cliAccounts.detailClaudeTimeout"),
    ("DetailClaudeUnknown", "settings.cliAccounts.detailClaudeUnknown"),
    ("DetailRunFailed", "settings.cliAccounts.detailRunFailed"),
];

/// StringsVerification.cs:89 `Validate`: the reason the copy fails, or None.
pub fn validate(class: &str, actual: &[(String, String)], truth: &[(String, String)]) -> Option<String> {
    let mut seen: Vec<(&str, &str)> = vec![];
    for (name, value) in actual {
        if value.is_empty() {
            return Some(format!("{class}.{name} is empty"));
        }
        if let Some((_, twin)) = seen.iter().find(|(v, _)| v == value) {
            return Some(format!("{class}.{name} duplicates {twin}"));
        }
        seen.push((value, name));
        if let Some(bad) = placeholders(value) {
            return Some(format!("{class}.{name} has a placeholder that is not {{name}}: {bad}"));
        }
        match truth.iter().find(|(field, _)| field == name) {
            None => return Some(format!("{class}.{name} mirrors nothing")),
            Some((_, expected)) if expected != value => return Some(format!("{class}.{name} differs: {value}")),
            _ => {}
        }
    }
    truth.iter().find(|(field, _)| !actual.iter().any(|(name, _)| name == field)).map(|(field, _)| format!("{class} is missing {field}"))
}

/// StringsVerification.cs:111 `Placeholders`.
fn placeholders(text: &str) -> Option<String> {
    let chars: Vec<char> = text.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        if chars[i] == '}' {
            return Some("}".into());
        }
        if chars[i] == '{' {
            let Some(close) = chars[i..].iter().position(|c| *c == '}').map(|p| p + i) else {
                return Some(chars[i..].iter().collect());
            };
            let name: String = chars[i + 1..close].iter().collect();
            if name.is_empty()
                || !name.starts_with(|c: char| c.is_ascii_alphabetic())
                || !name.chars().all(|c| c.is_ascii_alphanumeric() || c == '_')
            {
                return Some(format!("{{{name}}}"));
            }
            i = close;
        }
        i += 1;
    }
    None
}

/// StringsVerification.cs:532 `CliAccountStringsMatchMacOS`: the copy is the shared Korean copy, the description its Windows
/// wording, and each rule rejects its own failure.
#[test]
fn cli_account_strings_match_macos() {
    common::korean();
    let korean = mighty_core::locale::catalogue("ko");
    let truth: Vec<(String, String)> = CLI_ACCOUNT_SHARED
        .iter()
        .map(|(field, key)| (field.to_string(), korean.get(*key).unwrap_or_else(|| panic!("ko.json has no {key}")).clone()))
        .collect();
    let actual: Vec<(String, String)> = copy::all().into_iter().map(|(field, value)| (field.to_string(), value)).collect();
    assert_eq!(validate("CliAccountStrings", &actual, &truth), None);
    let broken = |field: &str, value: &str| -> Vec<(String, String)> {
        actual.iter().map(|(name, v)| (name.clone(), if name == field { value.to_string() } else { v.clone() })).collect()
    };
    let bad = |copy: &[(String, String)]| validate("CliAccountStrings", copy, &truth);
    assert!(
        bad(&broken("SectionDescription", "로그인은 터미널 실행 창에서 진행됩니다. 앱이 명령을 실행해 두면 CLI가 브라우저를 엽니다. 다른 계정으로 바꿀 때는 브라우저에서 원하는 계정을 고르세요. 바꾼 계정은 다음 요청부터 적용됩니다.")).is_some(),
        "the macOS description must fail; only the Windows wording is accepted"
    );
    assert!(bad(&broken("SectionTitle", "")).is_some(), "an empty value must fail");
    assert!(bad(&broken("ButtonLogin", &copy::button_logout())).is_some(), "a duplicate value must fail");
    assert!(bad(&broken("ConfirmMessageTemplate", "{ provider }...")).is_some(), "a placeholder that is not {{name}} must fail");
    let missing: Vec<(String, String)> = actual.iter().filter(|(name, _)| name != "SummarySignedOut").cloned().collect();
    assert!(bad(&missing).is_some(), "a missing literal must fail");
}

/// CliAccountVerification.cs:18 `ClaudeAndCodexStatusesExposeAccountLabelsOnly`.
#[test]
fn claude_and_codex_statuses_expose_account_labels_only() {
    common::korean();
    let claude = accounts::parse_claude_status(
        r#"{"loggedIn":true,"authMethod":"claude.ai","email":"me@example.com","orgName":"Org","subscriptionType":"max"}"#,
    );
    assert_eq!(claude.logged_in, Some(true), "claude: logged in");
    assert_eq!(claude.account.as_deref(), Some("me@example.com"), "claude: account is email");
    assert_eq!(claude.plan.as_deref(), Some("Max"), "claude: plan capitalised");
    assert!(
        claude.method.as_deref() == Some("Claude 구독") && claude.method_id.as_deref() == Some(method::CLAUDE_SUBSCRIPTION),
        "claude: claude.ai method and its id"
    );
    assert_eq!(claude.summary(), "me@example.com · Max · Claude 구독", "claude: summary");

    let signed_out = accounts::parse_claude_status(r#"{"loggedIn":false}"#);
    assert!(signed_out.logged_in == Some(false) && signed_out.summary() == copy::summary_signed_out(), "claude: signed out");

    let unknown = accounts::parse_claude_status("nonsense");
    assert!(unknown.logged_in.is_none(), "claude: unknown on bad json");
    assert!(unknown.summary() == unknown.detail && !unknown.detail.is_empty(), "claude: detail shown as summary");
    assert_eq!(CliAccountStatus::new("x").summary(), copy::summary_unknown(), "default unknown summary");

    let org = accounts::parse_claude_status(r#"{"loggedIn":true,"authMethod":"console","orgName":"Acme"}"#);
    assert_eq!(org.account.as_deref(), Some("Acme"), "claude: org name when no email");
    assert!(
        org.method.as_deref() == Some("Anthropic Console") && org.method_id.as_deref() == Some(method::CLAUDE_CONSOLE),
        "claude: console method and its id"
    );
    assert!(org.can_sign_out, "claude: console can sign out");

    let id_token = jwt(serde_json::json!({"email": "dev@example.com", "https://api.openai.com/auth": {"chatgpt_plan_type": "plus"}}));
    let auth_json = serde_json::json!({"tokens": {"id_token": id_token, "access_token": "secret"}}).to_string();
    let codex = accounts::parse_codex_status("Logged in using ChatGPT\n", Some(&auth_json));
    assert_eq!(codex.logged_in, Some(true), "codex: logged in");
    assert!(
        codex.method.as_deref() == Some("ChatGPT") && codex.method_id.as_deref() == Some(method::CODEX_CHATGPT),
        "codex: method and its id"
    );
    assert_eq!(codex.account.as_deref(), Some("dev@example.com"), "codex: account from JWT");
    assert_eq!(codex.plan.as_deref(), Some("Plus"), "codex: plan capitalised");
    assert!(!codex.summary().contains("secret") && !codex.account.clone().unwrap_or_default().contains("secret"), "token blindness");
    assert_eq!(accounts::parse_codex_status("Not logged in", Some(&auth_json)).logged_in, Some(false), "codex: signed out");
    assert_eq!(
        accounts::parse_codex_status("Logged in using an API key - sk-***", None).method_id.as_deref(),
        Some(method::CODEX_API_KEY),
        "codex: API key method id"
    );
    assert!(accounts::parse_codex_status("error: boom", None).logged_in.is_none(), "codex: unknown on unrecognised output");
    assert!(accounts::jwt_claims("not-a-jwt").is_none(), "jwt: 2-part token is not a JWT");
}

/// CliAccountVerification.cs:262 `TokenNeverSurvivesTheRead`: no part of any token survives in the status, its text form or a
/// field.
#[test]
fn token_never_survives_the_read() {
    common::korean();
    let id_token = jwt(serde_json::json!({"email": "dev@example.com", "https://api.openai.com/auth": {"chatgpt_plan_type": "plus"}}))
        .replace(".sig", ".FIXTURE-SIGNATURE-VALUE");
    let payload = id_token.split('.').nth(1).unwrap().to_string();
    let secrets = [
        id_token.clone(),
        payload,
        "FIXTURE-SIGNATURE-VALUE".into(),
        "FIXTURE-ACCESS-VALUE".into(),
        "FIXTURE-REFRESH-VALUE".into(),
        "sk-FIXTURE-API-KEY".into(),
    ];
    let auth_json = serde_json::json!({"OPENAI_API_KEY": "sk-FIXTURE-API-KEY", "tokens": {"id_token": id_token, "access_token": "FIXTURE-ACCESS-VALUE", "refresh_token": "FIXTURE-REFRESH-VALUE"}}).to_string();
    let broken =
        serde_json::json!({"tokens": {"id_token": "aaa.%%%not-base64%%%.FIXTURE-SIGNATURE-VALUE", "access_token": "FIXTURE-ACCESS-VALUE"}})
            .to_string();
    for status in [
        accounts::parse_codex_status("Logged in using ChatGPT\n", Some(&auth_json)),
        accounts::parse_codex_status("Logged in using ChatGPT\n", Some(&broken)),
        accounts::parse_codex_status("Not logged in", Some(&auth_json)),
        accounts::parse_codex_status("error: boom", Some(&auth_json)),
        accounts::parse_codex_status("Logged in using ChatGPT\n", Some("{ not json FIXTURE-ACCESS-VALUE")),
    ] {
        let visible =
            format!("{status:?}\n{}\n{}\n{:?}\n{:?}\n{:?}", status.summary(), status.detail, status.account, status.plan, status.method);
        for secret in &secrets {
            assert!(!visible.contains(secret.as_str()), "a token value survived the read: {}", &secret[..secret.len().min(12)]);
        }
    }
    let good = accounts::parse_codex_status("Logged in using ChatGPT\n", Some(&auth_json));
    assert!(
        good.account.as_deref() == Some("dev@example.com") && good.plan.as_deref() == Some("Plus"),
        "the label and the plan are still read"
    );
    let bad = accounts::parse_codex_status("Logged in using ChatGPT\n", Some(&broken));
    assert!(bad.logged_in == Some(true) && bad.account.is_none(), "a broken id token gives no label and no error");
}

/// CliAccountVerification.cs:79 `AntigravityStatusIsItsModelListWithNoSignOut`.
#[test]
fn antigravity_status_is_its_model_list_with_no_sign_out() {
    common::korean();
    let home = temp("antigravity-status");
    let signed_in = accounts::antigravity_status(0, "Fetching available models...\ngemini-3.8-flash-low\tGemini 3.8 Flash (Low)\n", false);
    assert!(
        signed_in.logged_in == Some(true)
            && signed_in.method_id.as_deref() == Some(method::GEMINI_GOOGLE)
            && !signed_in.can_sign_out
            && signed_in.detail.contains("/logout"),
        "antigravity: a model list is a sign-in, with no sign-out here"
    );
    let signed_out = accounts::antigravity_status(1, "Error: authentication required. Run 'agy' to log in, then retry.\n", false);
    assert!(signed_out.logged_in == Some(false) && !signed_out.can_sign_out, "antigravity: agy's own words are a lost sign-in");
    let offline = accounts::antigravity_status(1, "dial tcp: lookup failed", false);
    assert!(offline.logged_in.is_none() && !offline.detail.is_empty(), "antigravity: anything else is unknown");
    assert!(accounts::antigravity_status(0, "", true).logged_in.is_none(), "antigravity: a timeout is unknown");
    assert!(accounts::bounded_file_text(&home, accounts::FILE_SIZE_CAP).is_none(), "boundedFileText: rejects directories");
    let big = home.join("big.json");
    std::fs::write(&big, "x".repeat(32)).unwrap();
    assert!(
        accounts::bounded_file_text(&big, 16).is_none() && accounts::bounded_file_text(&big, 32).is_some(),
        "boundedFileText: rejects over-sized files"
    );
    let home_text = text(&home);
    assert_eq!(accounts::codex_home(&home_text, &env(&[("CODEX_HOME", "/opt/codex")])), "/opt/codex", "codexHome: uses CODEX_HOME");
    assert_eq!(accounts::codex_home(&home_text, &env(&[])), text(&home.join(".codex")), "codexHome: falls back to .codex");
    let _ = std::fs::remove_dir_all(&home);
}

/// CliAccountVerification.cs:108 `CommandsAreTheCLIsOwn`.
#[test]
fn commands_are_the_clis_own() {
    assert_eq!(accounts::login_arguments("claude", CliLoginOption::Account), Some(vec!["claude", "auth", "login"]), "claude: login argv");
    assert_eq!(
        accounts::login_arguments("claude", CliLoginOption::Console),
        Some(vec!["claude", "auth", "login", "--console"]),
        "claude: console argv"
    );
    assert_eq!(accounts::login_arguments("codex", CliLoginOption::Account), Some(vec!["codex", "login"]), "codex: login argv");
    assert_eq!(accounts::login_arguments("gemini", CliLoginOption::Account), Some(vec!["agy"]), "antigravity: login argv is agy itself");
    assert_eq!(accounts::login_arguments("other", CliLoginOption::Account), None, "unknown: no login argv");
    assert_eq!(accounts::status_arguments("claude"), Some(vec!["claude", "auth", "status", "--json"]), "claude: status argv");
    assert_eq!(accounts::status_arguments("gemini"), Some(vec!["agy", "models"]), "antigravity: the model list stands in for a status");
    assert_eq!(accounts::logout_arguments("codex"), Some(vec!["codex", "logout"]), "codex: logout argv");
    assert_eq!(accounts::logout_arguments("gemini"), None, "antigravity: no logout argv");
}

/// CliAccountVerification.cs:125-140, the terminal half of `CommandsAreTheCLIsOwn`: the external sign-in terminal carries the CLI's
/// own argv as a separate argument list (the plan is mighty-platform-windows' `launch_plan`, the launcher's own).
#[cfg(windows)]
#[test]
fn the_sign_in_terminal_carries_the_clis_own_argv() {
    let hosts = TerminalHosts::new(None);
    let login = strings_of(&accounts::login_arguments("claude", CliLoginOption::Console).unwrap());
    let wt = launch_plan(&hosts, &login, true, None, None, None, None).unwrap();
    assert_eq!(wt.0, "wt.exe", "terminal: Windows Terminal preferred when available");
    assert_eq!(wt.1, strings_of(&["new-tab", "--", "claude", "auth", "login", "--console"]), "terminal: wt passes the login argv after --");
    let fallback = launch_plan(&hosts, &login, false, None, None, None, None).unwrap();
    assert!(fallback.0 == hosts.console_host && fallback.0.ends_with("conhost.exe"), "terminal: console host is the fallback");
    assert_eq!(fallback.1, login, "terminal: fallback passes the login argv unchanged");
    assert!(wt.1.iter().chain(&fallback.1).all(|a| !a.contains(' ')), "terminal: arguments are not joined");
    assert!(launch_plan(&hosts, &[], false, None, None, None, None).is_err(), "terminal: an empty login command is refused");
    let system = TerminalHosts::new(Some(r"C:\Windows\System32"));
    assert!(
        system.console_host.ends_with("conhost.exe") && system.console_host.starts_with(r"C:\Windows\System32"),
        "the system folder's console host"
    );
}

/// CliAccountVerification.cs:146 `CoordinatorReadsStatusesAndCallsLogout`: the runner for every CLI; Antigravity's status is its
/// model list, and it has no logout.
#[tokio::test]
async fn coordinator_reads_statuses_and_calls_logout() {
    common::korean();
    let home = temp("coordinator");
    let bin = home.join("bin");
    for name in ["claude", "codex", "agy"] {
        touch(bin.join(common::program(name)));
    }
    let runner = FakeRunner::new(|_, arguments| {
        let argv = arguments.join(" ");
        if argv.contains("auth status") {
            ok(r#"{"loggedIn":true,"authMethod":"claude.ai","email":"me@example.com"}"#)
        } else if argv.contains("login status") {
            ok("Logged in using ChatGPT\n")
        } else if arguments == ["models"] {
            result(1, "", "Error: authentication required. Run 'agy' to log in, then retry.\n")
        } else {
            ok("")
        }
    });
    let coordinator = CliAccountsCoordinator::new(runner.clone(), catalog(), text(&home))
        .with_environment(env(&[("PATH", &text(&bin)), ("NO_COLOR", "1")]));
    coordinator.refresh(&["claude", "codex", "gemini"]).await;
    let statuses = coordinator.statuses();
    assert_eq!(statuses["claude"].logged_in, Some(true), "coordinator: claude logged in");
    assert_eq!(statuses["claude"].account.as_deref(), Some("me@example.com"), "coordinator: claude account");
    assert_eq!(statuses["codex"].logged_in, Some(true), "coordinator: codex logged in");
    let stem = |path: &str| std::path::Path::new(path).file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
    assert!(
        statuses["gemini"].logged_in == Some(false)
            && runner.calls().iter().any(|c| c.arguments == ["models"] && stem(&c.executable) == "agy"),
        "coordinator: antigravity signed out, read from agy models"
    );
    assert!(runner.calls().iter().any(|c| c.arguments.contains(&"auth".to_string())), "coordinator: ran claude auth status");
    assert!(
        runner.calls().iter().any(|c| c.arguments.contains(&"status".to_string()) && c.arguments.contains(&"login".to_string())),
        "coordinator: ran codex login status"
    );
    assert!(
        runner.calls().iter().all(|c| c.working_directory.as_deref() == Some(home.as_path())
            && c.environment.get("NO_COLOR").map(String::as_str) == Some("1")
            && c.environment.get("CI").map(String::as_str) == Some("1")),
        "in the home, quiet"
    );

    let before = runner.calls().len();
    coordinator.logout("claude").await;
    let after: Vec<_> = runner.calls().into_iter().skip(before).collect();
    assert!(!after.is_empty() && after.iter().any(|c| c.arguments.contains(&"logout".to_string())), "coordinator: logout used logout argv");

    let before = runner.calls().len();
    let unchanged = coordinator.logout("gemini").await;
    assert!(
        runner.calls().iter().skip(before).all(|c| c.arguments == ["models"])
            && unchanged.logged_in == Some(false)
            && !unchanged.can_sign_out,
        "coordinator: antigravity logout runs no logout command"
    );
    let _ = std::fs::remove_dir_all(&home);
}

/// CliAccountVerification.cs:217 `SmokeShowsFixtureStatusesAndConfirmationFlowWorks`.
#[tokio::test]
async fn smoke_shows_fixture_statuses_and_confirmation_flow_works() {
    common::korean();
    let fixtures = accounts::smoke_fixture_statuses();
    assert!(fixtures.iter().any(|s| s.logged_in == Some(true) && s.account.is_some()), "smoke fixture: signed-in with account");
    assert!(fixtures.iter().any(|s| s.logged_in == Some(false)), "smoke fixture: signed-out");
    assert!(fixtures.iter().any(|s| !s.installed), "smoke fixture: not-installed");
    assert!(fixtures.iter().any(|s| !s.can_sign_out && s.logged_in == Some(true)), "smoke fixture: cannot-sign-out");
    let summaries: Vec<String> = fixtures.iter().map(CliAccountStatus::summary).collect();
    assert!(summaries.iter().any(|s| s.contains("me@example.com")), "smoke: signed-in summary present");
    assert!(summaries.contains(&copy::summary_signed_out()), "smoke: signed-out summary present");
    assert!(summaries.iter().any(|s| *s == fixtures[2].detail && s.contains("Antigravity")), "smoke: not-installed detail present");

    let triggered = Arc::new(Mutex::new(String::new()));
    let cancelled = Arc::new(Mutex::new(false));
    let (t, c) = (triggered.clone(), cancelled.clone());
    let outcome = accounts::run_smoke(
        &summaries,
        move |provider| async move {
            *t.lock().unwrap() = provider;
        },
        move || async move {
            *c.lock().unwrap() = true;
        },
    )
    .await
    .expect("the smoke passes");
    assert!(!triggered.lock().unwrap().is_empty(), "smoke: logout triggered for a provider");
    assert!(*cancelled.lock().unwrap(), "smoke: confirmation cancelled");
    assert!(outcome.confirmation_shown && outcome.cancelled && outcome.summaries == summaries, "smoke: the outcome");
    let no_signed_in: Vec<String> = summaries.iter().filter(|s| !s.contains("me@example.com")).cloned().collect();
    assert!(accounts::run_smoke(&no_signed_in, |_| async {}, || async {}).await.is_err(), "smoke: missing signed-in summary must fail");
}

const AUTH_LIST: &str = r#"[{"id":"anthropic","name":"Anthropic","connections":[{"type":"credential","id":"cred_01","label":"OAuth","method":"oauth"}]},{"id":"openrouter","name":"OpenRouter","connections":[{"type":"env","name":"OPENROUTER_API_KEY"}]},{"id":"openai","name":"OpenAI","connections":[{"type":"credential","id":"cred_02","label":"API key","method":"key","status":{"status":"needs_auth","message":"expired"}}]}]"#;
const LIST_ARGUMENTS: [&str; 5] = ["auth", "list", "--format", "json", "--standalone"];

/// AcpAgentAccountVerification.cs:86 `TheCoordinatorAsksAuthListAndAsksItsVersionOnlyWhenTheListIsNotJson`.
#[tokio::test]
async fn the_coordinator_asks_auth_list_and_asks_its_version_only_when_the_list_is_not_json() {
    common::korean();
    let home = temp("opencode-coordinator");
    let empty = home.join("empty");
    std::fs::create_dir_all(&empty).unwrap();
    let roaming = home.join("AppData").join("Roaming");
    // npm's global folder is not on PATH: opencode.cmd is found where npm puts it.
    let shim = touch(roaming.join("npm").join("opencode.cmd"));
    let list = Arc::new(Mutex::new(result(1, "", "Unknown argument: format\n")));
    let answer = list.clone();
    let runner = FakeRunner::new(move |_, arguments| {
        if arguments == ["--version"] {
            ok("1.19.5\n")
        } else if arguments == LIST_ARGUMENTS {
            answer.lock().unwrap().clone()
        } else {
            result(2, "", "unexpected")
        }
    });
    let coordinator = CliAccountsCoordinator::new(runner.clone(), catalog(), text(&home))
        .with_environment(env(&[("PATH", &text(&empty)), ("APPDATA", &text(&roaming))]));
    coordinator.refresh(&["opencode"]).await;
    let legacy = coordinator.status_of("opencode").unwrap();
    assert!(
        legacy.installed && legacy.logged_in.is_none() && legacy.detail == strings::get("settings.cliAccounts.detailOpencodeV1"),
        "OpenCode 1: {legacy:?}"
    );
    let asked: Vec<String> = runner.calls().iter().map(|c| c.arguments.join(" ")).collect();
    assert!(
        asked == ["auth list --format json --standalone", "--version"] && runner.calls().iter().all(|c| c.executable == shim),
        "auth list, then its version, from the shim: {asked:?}"
    );
    assert!(
        runner
            .calls()
            .iter()
            .all(|c| c.environment.get("NO_COLOR").map(String::as_str) == Some("1")
                && c.environment.get("CI").map(String::as_str) == Some("1")),
        "quiet"
    );

    runner.clear();
    *list.lock().unwrap() = ok(AUTH_LIST);
    let status = coordinator.status("opencode").await;
    assert!(
        status.logged_in == Some(true)
            && status.account.as_deref() == Some("Anthropic, OpenRouter")
            && !status.can_sign_out
            && runner.calls().len() == 1,
        "a JSON list asks nothing more"
    );
    runner.clear();
    let after = coordinator.logout("opencode").await;
    let calls = runner.calls();
    assert!(
        after.logged_in == Some(true) && calls.len() == 1 && calls[0].arguments == LIST_ARGUMENTS,
        "no opencode auth logout from the app"
    );

    let bin = home.join("bin");
    let on_path = touch(bin.join(common::program("opencode")));
    runner.clear();
    CliAccountsCoordinator::new(runner.clone(), catalog(), text(&home))
        .with_environment(env(&[("PATH", &text(&bin)), ("APPDATA", &text(&roaming))]))
        .status("opencode")
        .await;
    assert!(runner.calls().len() == 1 && runner.calls().iter().all(|c| c.executable == on_path), "PATH first");
    // The profile folders are given empty, so a Windows PC's own opencode is never found instead.
    let missing = CliAccountsCoordinator::new(runner.clone(), catalog(), text(&home))
        .with_environment(env(&[("PATH", &text(&empty)), ("APPDATA", &text(&empty))]))
        .status("opencode")
        .await;
    assert!(
        !missing.installed && missing.detail.contains("opencode") && !missing.detail.contains("Claude"),
        "not installed, by its own name: {}",
        missing.detail
    );
    let _ = std::fs::remove_dir_all(&home);
}

/// AcpAgentAccountVerification.cs:232 `TheCoordinatorFindsHermesWhereItsInstallerPutsItAndAsksItsAcpServer`, the part every OS runs
/// (the live probe over the stand-in is mighty-run's tests/acp_accounts.rs, as C# runs it on Windows there).
#[tokio::test]
async fn the_coordinator_finds_hermes_where_its_installer_puts_it_and_asks_its_acp_server() {
    common::korean();
    let home = temp("hermes-coordinator");
    let empty = home.join("empty");
    std::fs::create_dir_all(&empty).unwrap();
    let local = home.join("AppData").join("Local");
    let installed = touch(local.join("hermes").join("bin").join("hermes.exe"));
    type Asked = Arc<Mutex<Vec<(CliCommand, std::collections::BTreeMap<String, String>)>>>;
    let asked: Asked = Arc::new(Mutex::new(vec![]));
    let answer: Arc<Mutex<Option<Probe>>> = Arc::new(Mutex::new(Some(Probe {
        provider: Some("nous".into()),
        answered: true,
        timed_out: false,
        version: Some("0.23.1".into()),
    })));
    let probe = |asked: Asked, answer: Arc<Mutex<Option<Probe>>>| -> accounts::HermesProbe {
        Arc::new(move |command, values| {
            asked.lock().unwrap().push((command, values));
            let value = answer.lock().unwrap().clone();
            Box::pin(async move { value })
        })
    };
    let runner = FakeRunner::new(|_, _| result(2, "", "the runner is not for Hermes"));
    let fixed = env(&[("PATH", &text(&empty)), ("LOCALAPPDATA", &text(&local))]);
    let coordinator = CliAccountsCoordinator::new(runner.clone(), catalog(), text(&home))
        .with_environment(fixed.clone())
        .with_hermes_probe(probe(asked.clone(), answer.clone()));
    coordinator.refresh(&["hermes"]).await;
    let status = coordinator.status_of("hermes").unwrap();
    assert!(
        status.installed && status.logged_in == Some(true) && status.account.as_deref() == Some("nous") && !status.can_sign_out,
        "signed in: {status:?}"
    );
    {
        let asked = asked.lock().unwrap();
        assert!(
            asked.len() == 1
                && asked[0].0.binary == installed
                && asked[0].0.prefix.is_empty()
                && asked[0].1.get("LOCALAPPDATA") == Some(&text(&local))
                && runner.calls().is_empty(),
            "%LOCALAPPDATA%\\hermes\\bin\\hermes.exe is asked, never through the runner"
        );
    }
    *answer.lock().unwrap() = None;
    assert_eq!(coordinator.status("hermes").await.detail, copy::detail_run_failed(), "one that cannot start");
    *answer.lock().unwrap() = Some(Probe { provider: None, answered: true, timed_out: false, version: Some("0.23.1".into()) });
    let after = coordinator.logout("hermes").await;
    assert!(after.logged_in == Some(false) && runner.calls().is_empty(), "no sign-out command");

    let hermes_home = home.join("hermes-home");
    let fallback = touch(hermes_home.join("bin").join("hermes.cmd"));
    asked.lock().unwrap().clear();
    *answer.lock().unwrap() = Some(Probe { provider: Some("openrouter".into()), answered: true, timed_out: false, version: None });
    CliAccountsCoordinator::new(runner.clone(), catalog(), text(&home))
        .with_environment(env(&[("PATH", &text(&empty)), ("LOCALAPPDATA", &text(&local)), ("HERMES_HOME", &text(&hermes_home))]))
        .with_hermes_probe(probe(asked.clone(), answer.clone()))
        .status("hermes")
        .await;
    assert!(asked.lock().unwrap().len() == 1 && asked.lock().unwrap()[0].0.binary == fallback, "$HERMES_HOME\\bin\\hermes.cmd");
    assert_eq!(
        accounts::installer_copies("hermes", &env(&[("HERMES_HOME", "relative"), ("LOCALAPPDATA", &text(&local))])),
        vec![installed.clone(), text(&local.join("hermes").join("bin").join("hermes.cmd"))],
        "a HERMES_HOME that is not a full path is not used"
    );
    assert!(accounts::installer_copies("claude", &fixed).is_empty(), "only opencode and Hermes");
    let nothing: accounts::HermesProbe = Arc::new(|_, _| panic!("nothing to ask"));
    let missing = CliAccountsCoordinator::new(runner.clone(), catalog(), text(&home))
        .with_environment(env(&[("PATH", &text(&empty)), ("LOCALAPPDATA", &text(&empty))]))
        .with_hermes_probe(nothing)
        .status("hermes")
        .await;
    assert!(!missing.installed && missing.detail.contains("Hermes"), "not installed: {}", missing.detail);
    let _ = std::fs::remove_dir_all(&home);
}

/// AcpAgentAccountVerification.cs:390 `TheWindowsAccountsSectionListsEveryProvider`, its Core half: the section lists every provider
/// in the provider order, and each terminal sign-in has the hint its row says (the row itself is the section's, W6-C phase 2).
#[test]
fn the_windows_accounts_section_lists_every_provider() {
    common::korean();
    assert_eq!(accounts::SECTION_PROVIDERS, mighty_core::wire::PROVIDERS, "the accounts section lists every provider");
    for provider in ["pi", "opencode", "hermes"] {
        assert!(
            accounts::login_hint(provider).is_some_and(|hint| !hint.is_empty()),
            "{provider}: a terminal sign-in's row says what it asks"
        );
    }
    assert!(accounts::login_hint("claude").is_none(), "a browser sign-in has no hint");
}

const PI_MODEL_LIST: &str = "provider   model                      context  max-out  thinking  images\nanthropic  claude-sonnet-4-5          200K     64K      yes       yes\n";
const PI_NO_MODELS: &str = "No models available. Use /login to log into a provider via OAuth or API key. See:\n";

/// PiVerification.cs:218 `NoModelsReadsSignedOutAndCredentialsAndEnvironmentKeysSignPiIn`.
#[test]
fn no_models_reads_signed_out_and_credentials_and_environment_keys_sign_pi_in() {
    common::korean();
    let none = env(&[]);
    let out = accounts::pi_status(0, PI_NO_MODELS, None, &none, false);
    assert!(out.logged_in == Some(false) && out.provider == "pi" && !out.can_sign_out, "no models: signed out");
    assert_eq!(
        accounts::pi_status(
            0,
            PI_NO_MODELS,
            Some(r#"{"anthropic":{"type":"api_key","key":"x"}}"#),
            &env(&[("OPENAI_API_KEY", "k")]),
            false
        )
        .logged_in,
        Some(false),
        "whatever else is around"
    );
    let subscription = accounts::pi_status(
        0,
        PI_MODEL_LIST,
        Some(r#"{"anthropic":{"type":"oauth","access":"a","refresh":"r","expires":1}}"#),
        &none,
        false,
    );
    assert!(
        subscription.logged_in == Some(true)
            && subscription.method_id.as_deref() == Some(method::PI_SUBSCRIPTION)
            && subscription.account.as_deref() == Some("anthropic")
            && !subscription.can_sign_out,
        "a subscription"
    );
    let key = accounts::pi_status(
        0,
        PI_MODEL_LIST,
        Some(r#"{"openai":{"type":"api_key","key":"sk"},"google":{"type":"api_key","key":"g"}}"#),
        &none,
        false,
    );
    assert!(
        key.logged_in == Some(true)
            && key.method_id.as_deref() == Some(method::PI_API_KEY)
            && key.account.as_deref() == Some("google, openai"),
        "API keys"
    );
    let environment = accounts::pi_status(0, PI_MODEL_LIST, Some("{}"), &env(&[("GEMINI_API_KEY", "k")]), false);
    assert!(
        environment.logged_in == Some(true)
            && environment.method_id.as_deref() == Some(method::PI_API_KEY)
            && environment.account.is_none(),
        "the environment's key"
    );
    assert_eq!(
        accounts::pi_status(0, PI_MODEL_LIST, None, &none, false).method_id.as_deref(),
        Some(method::PI_MODELS),
        "models.json alone"
    );
    assert!(
        accounts::pi_status(1, "boom", None, &none, false).logged_in.is_none()
            && accounts::pi_status(1, "", None, &none, true).logged_in.is_none(),
        "a failed or timed-out call is unknown"
    );
    assert!(
        accounts::login_arguments("pi", CliLoginOption::Account) == Some(vec!["pi"])
            && accounts::status_arguments("pi") == Some(vec!["pi", "--list-models"])
            && accounts::logout_arguments("pi").is_none(),
        "argv"
    );
}

/// PiVerification.cs:236 `TheAuthFileAndTheCoordinatorAreReadFromTheHomeAndNeverShown`.
#[tokio::test]
async fn the_auth_file_and_the_coordinator_are_read_from_the_home_and_never_shown() {
    common::korean();
    let home = temp("pi-auth");
    let home_text = text(&home);
    let none = env(&[]);
    let auth = accounts::pi_auth_file(&home_text, &none);
    let read = || accounts::bounded_file_text(std::path::Path::new(&auth), accounts::FILE_SIZE_CAP);
    assert!(accounts::pi_credentials(read().as_deref()).0.is_empty(), "no file: no credential");
    std::fs::create_dir_all(std::path::Path::new(&auth).parent().unwrap()).unwrap();
    std::fs::write(&auth, r#"{"anthropic":{"type":"api_key","key":"sk-ant-SECRET"},"note":"text"}"#).unwrap();
    let credentials = accounts::pi_credentials(read().as_deref());
    assert!(credentials.0 == ["anthropic"] && !credentials.1, "providers only");
    let status = accounts::pi_status(0, PI_MODEL_LIST, read().as_deref(), &none, false);
    assert!(
        !format!("{}{}{:?}{status:?}", status.summary(), status.detail, status.account).contains("SECRET"),
        "a credential is never shown"
    );
    let elsewhere = if mighty_run::os::windows() { r"C:\elsewhere\agent" } else { "/elsewhere/agent" };
    assert_eq!(
        accounts::pi_auth_file(&home_text, &env(&[("PI_CODING_AGENT_DIR", elsewhere)])),
        mighty_run::os::combine(elsewhere, "auth.json"),
        "PI_CODING_AGENT_DIR"
    );
    assert_eq!(
        accounts::pi_environment_keys(&env(&[("ANTHROPIC_API_KEY", ""), ("XAI_API_KEY", "x"), ("PATH", "/bin")])),
        vec!["XAI_API_KEY"],
        "only a set key counts"
    );

    let bin = home.join("bin");
    touch(bin.join(common::program("pi")));
    std::fs::write(&auth, r#"{"github-copilot":{"type":"oauth","refresh":"r","access":"a","expires":1}}"#).unwrap();
    let runner = FakeRunner::new(|_, _| ok(PI_MODEL_LIST));
    let coordinator = CliAccountsCoordinator::new(runner.clone(), catalog(), home_text.clone())
        .with_environment(env(&[("PATH", &text(&bin)), ("NO_COLOR", "1")]));
    coordinator.refresh(&["pi"]).await;
    let pi = coordinator.status_of("pi").unwrap();
    assert!(
        pi.installed
            && pi.logged_in == Some(true)
            && pi.method_id.as_deref() == Some(method::PI_SUBSCRIPTION)
            && pi.account.as_deref() == Some("github-copilot")
            && runner.calls().len() == 1
            && runner.calls()[0].arguments == ["--list-models"],
        "signed in by a model list and a subscription"
    );
    runner.answer(|_, _| ok("No models available. Use /login\n"));
    coordinator.refresh(&["pi"]).await;
    assert_eq!(coordinator.status_of("pi").unwrap().logged_in, Some(false), "no models: signed out");
    let missing = CliAccountsCoordinator::new(runner.clone(), catalog(), home_text.clone())
        .with_environment(env(&[("PATH", &text(&home.join("nowhere"))), ("APPDATA", &text(&home.join("nowhere")))]));
    missing.refresh(&["pi"]).await;
    assert!(!missing.status_of("pi").unwrap().installed, "not installed");
    let _ = std::fs::remove_dir_all(&home);
}

/// The runner over the platform's process host (C# `CliRunner`'s contract): the found program with its arguments in the home, the
/// environment laid over with `NO_COLOR`, both outputs and the exit code; one past its time killed and reported `-1`, timed out.
#[tokio::test]
async fn the_host_runner_runs_a_cli_to_its_end_and_kills_one_past_its_time() {
    let home = temp("host-runner");
    let host = Arc::new(FakeProcessHost::default());
    host.push(FakeScript { stdout: b"Logged in using ChatGPT\n".to_vec(), stderr: b"note\n".to_vec(), exit_code: 0, hang: false });
    host.push(FakeScript { hang: true, ..FakeScript::default() });
    let runner = HostRunner::new(host.clone());
    let program = home.join(common::program("codex"));
    let values = env(&[("CI", "1")]);
    let done =
        runner.run(&text(&program), &strings_of(&["login", "status"]), Duration::from_secs(5), &values, Some(&home)).await.expect("it ran");
    assert!(
        done.exit_code == 0 && done.output == "Logged in using ChatGPT\n" && done.error_output == "note\n" && !done.timed_out,
        "{done:?}"
    );
    let request = &host.spawned()[0];
    assert!(
        request.program == program
            && request.args == ["login", "status"]
            && request.cwd == home
            && request.env.get("CI").map(String::as_str) == Some("1")
            && request.env.get("NO_COLOR").map(String::as_str) == Some("1"),
        "{request:?}"
    );
    assert!(host.closed(0), "the child is closed once it ended");
    let late = runner.run(&text(&program), &[], Duration::from_millis(50), &values, Some(&home)).await.expect("it ran");
    assert!(late.timed_out && late.exit_code == -1 && host.closed(1), "past its time: {late:?}");
    let _ = std::fs::remove_dir_all(&home);
}

/// `CliAccountsCoordinator.StartSignInAsync` over mighty-platform's recording launcher (it never opens a window): a terminal sign-in's
/// CLI by its full path (the name replaced, the path also given as resolved), titled and in its folder, a terminal app preferred, with
/// the CLI environment; Bedrock's setup in the system's own console with `CLAUDE_CODE_USE_BEDROCK`; a refused start says so; no
/// launcher, no sign-in. Where the window opens (Windows Terminal or the console host) is the launcher's plan.
#[tokio::test]
async fn a_sign_in_opens_the_clis_own_command_in_a_terminal() {
    common::korean();
    let home = temp("sign-in");
    let bin = home.join("bin");
    let agy = touch(bin.join(common::program("agy")));
    touch(bin.join(common::program("claude")));
    let launcher = Arc::new(RecordingTerminalLauncher::default());
    let coordinator = CliAccountsCoordinator::new(FakeRunner::new(|_, _| ok("")), catalog(), text(&home))
        .with_environment(env(&[("PATH", &text(&bin)), ("FIXTURE_KEY", "sk-FIXTURE-SECRET")]))
        .with_terminal(launcher.clone());
    let home_text = text(&home);
    let opened = coordinator.start_sign_in(&strings_of(&["agy"]), Some(&home_text), Some("Antigravity 로그인")).await.expect("it opened");
    assert!(opened.closes_with_window, "the recording launcher's window holds its command");
    let launch = &launcher.opened()[0];
    assert!(
        launch.argv == strings_of(&[&agy])
            && launch.resolved.as_deref() == Some(agy.as_str())
            && launch.directory.as_deref() == Some(home.as_path())
            && launch.title.as_deref() == Some("Antigravity 로그인")
            && launch.prefer_terminal_app
            && launch.environment.get("FIXTURE_KEY").map(String::as_str) == Some("sk-FIXTURE-SECRET"),
        "the terminal sign-in: {:?}",
        (&launch.argv, &launch.resolved, &launch.directory, &launch.title)
    );
    let bedrock = coordinator
        .start_sign_in(&strings_of(&accounts::login_arguments("claude", CliLoginOption::Bedrock).unwrap()), None, None)
        .await
        .expect("it opened");
    let launch = &launcher.opened()[1];
    assert!(
        launch.argv == strings_of(&["claude", "/setup-bedrock"])
            && launch.resolved.is_none()
            && !launch.prefer_terminal_app
            && launch.environment.get("CLAUDE_CODE_USE_BEDROCK").map(String::as_str) == Some("1"),
        "Bedrock's setup in the system's own console: {:?}",
        (&launch.argv, launch.prefer_terminal_app)
    );
    assert_eq!(bedrock.window.wait().await.ok(), Some(0), "the window's end is awaited");
    launcher.refuse();
    assert!(coordinator.start_sign_in(&strings_of(&["agy"]), None, None).await.is_err(), "a terminal that cannot start says so");
    let without = CliAccountsCoordinator::new(FakeRunner::new(|_, _| ok("")), catalog(), text(&home));
    assert!(without.start_sign_in(&strings_of(&["agy"]), None, None).await.is_err_and(|e| e.is_unsupported()), "no launcher, no sign-in");
    let _ = std::fs::remove_dir_all(&home);
}

/// The coordinator over the real Windows process layer: copies of the stand-in CLI named `claude`, `codex` and `agy` on the PATH
/// answer the status commands, `claude auth logout` signs Claude out, and each ran in the home with `NO_COLOR` and `CI`.
#[cfg(windows)]
#[tokio::test(flavor = "multi_thread")]
async fn the_coordinator_reads_real_children_through_the_process_host() {
    common::korean();
    let home = temp("coordinator-live");
    let bin = home.join("bin");
    let state = home.join("state");
    std::fs::create_dir_all(&bin).unwrap();
    std::fs::create_dir_all(&state).unwrap();
    for name in ["claude", "codex", "agy"] {
        std::fs::copy(common::fake_cli(), bin.join(format!("{name}.exe"))).unwrap();
    }
    let host: Arc<dyn mighty_platform::ProcessHost> = Arc::new(mighty_platform_windows::WindowsProcessHost::new());
    let mut values: std::collections::BTreeMap<String, String> = std::env::vars().collect();
    values.retain(|key, _| !key.eq_ignore_ascii_case("PATH"));
    values.insert("PATH".into(), text(&bin));
    values.insert("MIGHTY_ACCOUNTS_FAKE_STATE".into(), text(&state));
    let coordinator = CliAccountsCoordinator::new(Arc::new(HostRunner::new(host)), catalog(), text(&home)).with_environment(values);
    coordinator.refresh(&["claude", "codex", "gemini"]).await;
    let statuses = coordinator.statuses();
    assert!(
        statuses["claude"].logged_in == Some(true) && statuses["claude"].account.as_deref() == Some("me@example.com"),
        "{:?}",
        statuses["claude"]
    );
    assert_eq!(statuses["codex"].logged_in, Some(true), "codex reads its standard error too: {:?}", statuses["codex"]);
    assert_eq!(statuses["gemini"].logged_in, Some(false), "agy's own words: {:?}", statuses["gemini"]);
    let signed_out = coordinator.logout("claude").await;
    assert_eq!(signed_out.logged_in, Some(false), "claude auth logout took");
    let calls: Vec<serde_json::Value> = std::fs::read_to_string(state.join("calls.jsonl")).unwrap().lines().map(common::json).collect();
    assert!(
        calls.len() == 5
            && calls.iter().all(|c| c["NO_COLOR"] == "1"
                && c["CI"] == "1"
                && c["cwd"].as_str().is_some_and(|cwd| cwd.eq_ignore_ascii_case(&text(&home)))),
        "{calls:?}"
    );
    common::remove_when_free(&home).await;
}
