//! C# AcpAgentAccountVerification, the parts about what opencode and Hermes report of their sign-ins (OpencodeAccounts, HermesAccounts,
//! CliAccountStatus, the argv and hints of CliAccountSupport): `OpencodeSignInIsWhatAuthListNamesAndOtherwiseZensFreeModelsOnly`,
//! `HermesSignInIsTheProviderItsAcpServerNames` and `TheProbeWritesInitializeOnceReadsTheAnswerAndClosesItsInput` (over the stand-in's
//! `--hermes-acp-cli`, Windows only). Their `CliAuthFailure.SignInCanFix` lines, the coordinator, the CLI updates, the account usage and
//! the WinUI accounts section are the settings' and sign-in card's stage (W6).

mod common;

use mighty_run::acp::accounts::{self, CliAccountStatus, Probe, method};
use mighty_run::strings;

/// What `opencode auth list --format json` prints: an OAuth sign-in, an environment key and a key that needs signing in again.
const AUTH_LIST: &str = r#"[{"id":"anthropic","name":"Anthropic","connections":[{"type":"credential","id":"cred_01","label":"OAuth","method":"oauth"}]},{"id":"openrouter","name":"OpenRouter","connections":[{"type":"env","name":"OPENROUTER_API_KEY"}]},{"id":"openai","name":"OpenAI","connections":[{"type":"credential","id":"cred_02","label":"API key","method":"key","status":{"status":"needs_auth","message":"expired"}}]}]"#;

fn fixture_line(name: &str) -> String {
    common::fixture(name).split('\n').map(|l| l.trim_end_matches('\r')).find(|l| !l.is_empty()).unwrap_or("").to_string()
}

fn unknown(status: &CliAccountStatus) -> bool {
    status.logged_in.is_none()
}

#[test]
fn opencode_sign_in_is_what_auth_list_names_and_otherwise_zens_free_models_only() {
    common::korean();
    let signed_in = accounts::opencode_status(0, AUTH_LIST, None, false);
    // A connection that needs signing in again does not count.
    assert!(
        signed_in.provider == "opencode"
            && signed_in.logged_in == Some(true)
            && signed_in.method_id.as_deref() == Some(method::OPENCODE_SUBSCRIPTION)
            && signed_in.account.as_deref() == Some("Anthropic, OpenRouter")
            && !signed_in.can_sign_out,
        "signed in: {signed_in:?}"
    );
    assert!(
        signed_in.method.as_deref() == Some(strings::get("cliAccounts.method.opencodeSubscription").as_str())
            && signed_in.detail == strings::get("settings.cliAccounts.detailOpencodeSignOut")
            && signed_in.detail.contains("opencode auth logout"),
        "a subscription, signed out in a terminal"
    );
    assert_eq!(signed_in.summary(), format!("Anthropic, OpenRouter · {}", signed_in.method.clone().unwrap()), "the row's line");
    let environment = accounts::opencode_status(
        0,
        r#"[{"id":"openrouter","name":"OpenRouter","connections":[{"type":"env","name":"OPENROUTER_API_KEY"}]}]"#,
        None,
        false,
    );
    assert!(
        environment.logged_in == Some(true)
            && environment.method_id.as_deref() == Some(method::OPENCODE_API_KEY)
            && environment.method.as_deref() == Some(strings::get("cliAccounts.method.opencodeEnvironmentKey").as_str()),
        "an environment key"
    );
    let stored = accounts::opencode_status(
        0,
        r#"[{"id":"openai","connections":[{"type":"credential","id":"cred_03","label":"API key","method":"key"}]}]"#,
        None,
        false,
    );
    assert!(
        stored.logged_in == Some(true)
            && stored.method_id.as_deref() == Some(method::OPENCODE_API_KEY)
            && stored.account.as_deref() == Some("openai")
            && stored.method.as_deref() == Some(strings::get("windows.cli.method.apiKey").as_str()),
        "a stored key, named by its id without a name"
    );
    let free = accounts::opencode_status(0, "[]\n", None, false);
    assert!(
        free.logged_in == Some(false)
            && !free.can_sign_out
            && free.detail == strings::get("settings.cliAccounts.detailOpencodeFreeOnly")
            && free.summary() == strings::get("settings.cliAccounts.summarySignedOut"),
        "no sign-in: opencode Zen's free models only"
    );
    let lapsed = r#"[{"id":"openai","name":"OpenAI","connections":[{"type":"credential","method":"oauth","status":{"status":"needs_auth"}}]},{"id":"x","name":"X","connections":[]}]"#;
    assert_eq!(accounts::opencode_status(0, lapsed, None, false).logged_in, Some(false), "only lapsed or no connections: signed out");
    // Anything else stays unknown, never signed out.
    let timed_out = accounts::opencode_status(-1, "", None, true);
    assert!(
        unknown(&timed_out)
            && timed_out.detail == strings::get("settings.cliAccounts.detailOpencodeTimeout")
            && timed_out.summary() == timed_out.detail,
        "a timeout"
    );
    for (code, output) in [(1, AUTH_LIST), (0, "No authenticated integrations\n"), (0, r#"{"integrations":[]}"#), (0, "")] {
        let status = accounts::opencode_status(code, output, None, false);
        assert!(
            unknown(&status) && status.detail == strings::get("settings.cliAccounts.detailOpencodeUnknown"),
            "unknown: {code} {output}"
        );
    }
    // OpenCode 1 has no --format json: its version names it, and it asks for OpenCode 2.
    let legacy = accounts::opencode_status(1, "", Some("1.19.5"), false);
    assert!(
        unknown(&legacy) && !legacy.can_sign_out && legacy.detail == strings::get("settings.cliAccounts.detailOpencodeV1"),
        "OpenCode 1"
    );
    assert_eq!(
        accounts::opencode_status(1, "Unknown argument: format", Some("opencode v2.2.7"), false).detail,
        strings::get("settings.cliAccounts.detailOpencodeUnknown"),
        "OpenCode 2 whose list failed is unknown"
    );
    assert!(
        accounts::opencode_supported("opencode v2.2.7")
            && accounts::opencode_supported("2.0.0")
            && !accounts::opencode_supported("1.19.5")
            && !accounts::opencode_supported("local"),
        "OpenCode 2 is 2.0 and later"
    );
    // Names only: no credential ever reaches the row, and a long list is cut at sixteen.
    let secret = accounts::opencode_status(
        0,
        r#"[{"id":"anthropic","name":"Anthro\u0007pic","connections":[{"type":"credential","method":"key","key":"sk-ant-FIXTURE-SECRET","token":"FIXTURE-TOKEN"}]}]"#,
        None,
        false,
    );
    let visible = format!("{secret:?}\n{}", secret.summary());
    assert!(
        secret.account.as_deref() == Some("Anthropic") && !visible.contains("FIXTURE-SECRET") && !visible.contains("FIXTURE-TOKEN"),
        "a credential is never shown"
    );
    let many = format!(
        "[{}]",
        (10..30)
            .map(|n| format!("{{\"id\":\"p{n}\",\"connections\":[{{\"type\":\"env\",\"name\":\"K{n}\"}}]}}"))
            .collect::<Vec<_>>()
            .join(",")
    );
    assert!(
        accounts::opencode_status(0, &many, None, false).account.unwrap().split(", ").count() == 16
            && accounts::opencode_connections(&many).unwrap().integrations.len() == 20,
        "sixteen names shown"
    );
    assert!(
        accounts::status_arguments("opencode") == Some(vec!["opencode", "auth", "list", "--format", "json", "--standalone"])
            && accounts::login_arguments("opencode") == Some(vec!["opencode", "auth", "login"])
            && accounts::logout_arguments("opencode").is_none(),
        "argv: auth list, auth login, and no sign-out the app runs"
    );
    assert!(
        accounts::login_hint("opencode") == Some(strings::get("settings.cliAccounts.opencodeLoginHint"))
            && accounts::login_hint("opencode").unwrap().contains("opencode auth login")
            && accounts::login_hint("pi") == Some(strings::get("settings.cliAccounts.piLoginHint"))
            && accounts::login_hint("claude").is_none()
            && accounts::login_hint("gemini").is_none(),
        "what the terminal asks"
    );
}

#[test]
fn hermes_sign_in_is_the_provider_its_acp_server_names() {
    common::korean();
    // The recorded answers: a configured Hermes names its provider beside the terminal setup, an unconfigured one the setup alone.
    let configured = accounts::hermes_parse_initialize(&format!("warning line\n{}\n", fixture_line("hermes-acp-answer.jsonl")));
    assert_eq!(
        configured,
        Some(Probe { provider: Some("custom".into()), answered: true, timed_out: false, version: Some("0.23.1".into()) }),
        "configured"
    );
    let unconfigured = accounts::hermes_parse_initialize(&fixture_line("hermes-acp-unconfigured.jsonl"));
    assert!(unconfigured.as_ref().is_some_and(|p| p.answered && p.provider.is_none()), "unconfigured: {unconfigured:?}");
    let signed_in = accounts::hermes_status(configured.as_ref());
    assert!(
        signed_in.provider == "hermes"
            && signed_in.logged_in == Some(true)
            && signed_in.account.as_deref() == Some("custom")
            && signed_in.method_id.as_deref() == Some(method::HERMES_PROVIDER)
            && !signed_in.can_sign_out
            && signed_in.method.as_deref() == Some(strings::get("cliAccounts.method.hermesProvider").as_str()),
        "signed in: {signed_in:?}"
    );
    assert!(
        signed_in.detail == strings::get("settings.cliAccounts.detailHermesSignOut") && signed_in.detail.contains("hermes auth"),
        "signed out in a terminal"
    );
    let signed_out = accounts::hermes_status(unconfigured.as_ref());
    assert!(
        signed_out.logged_in == Some(false)
            && !signed_out.can_sign_out
            && signed_out.detail == strings::get("settings.cliAccounts.detailHermesNoProvider"),
        "no provider: signed out"
    );
    // No answer is unknown, not signed out.
    assert!(
        accounts::hermes_parse_initialize("The ACP server can't start: its protocol packages are missing from this install.\n").is_none(),
        "the missing ACP packages are no answer"
    );
    assert_eq!(
        accounts::hermes_parse_initialize(r#"{"jsonrpc":"2.0","id":"mighty-1","error":{"code":-32603,"message":"Internal error"}}"#),
        Some(Probe { provider: None, answered: false, timed_out: false, version: None }),
        "an error answer"
    );
    assert!(
        accounts::hermes_parse_initialize(&fixture_line("hermes-acp-answer.jsonl").replace("\"mighty-1\"", "\"mighty-2\"")).is_none()
            && accounts::hermes_parse_initialize("").is_none(),
        "only the answer to the probe's request"
    );
    let silent = accounts::hermes_status(Some(&Probe { provider: None, answered: false, timed_out: false, version: None }));
    assert!(
        unknown(&silent)
            && silent.detail == strings::get("settings.cliAccounts.detailHermesUnknown")
            && silent.detail.contains("hermes pm install --extra acp"),
        "unknown says how to add the ACP packages"
    );
    let late = accounts::hermes_status(Some(&Probe { provider: None, answered: false, timed_out: true, version: None }));
    assert!(unknown(&late) && late.detail == strings::get("settings.cliAccounts.detailHermesTimeout"), "a timeout");
    assert_eq!(accounts::hermes_status(None).detail, strings::get("settings.cliAccounts.detailRunFailed"), "a Hermes that cannot start");
    // Only an agent method other than the terminal setup is a provider.
    let methods = common::json(
        r#"{"authMethods":[{"id":"hermes-setup","type":"terminal"},{"id":"other-setup","type":"terminal"},{"id":""},{"id":"nous","name":"Nous Portal"}]}"#,
    );
    assert_eq!(accounts::hermes_provider(&methods).as_deref(), Some("nous"), "the provider's method");
    // The one request: initialize with the capabilities a run announces, under the channel's first id.
    assert_eq!(
        accounts::HERMES_INITIALIZE_LINE,
        r#"{"jsonrpc":"2.0","id":"mighty-1","method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false,"elicitation":{"form":{}}},"clientInfo":{"name":"mighty-code","title":"Mighty Code","version":"1.0"}}}"#,
        "the initialize line"
    );
    // The line a run's channel writes first is the same one.
    let written = std::sync::Arc::new(std::sync::Mutex::new(Vec::<String>::new()));
    let sink = written.clone();
    let channel = mighty_run::acp::AcpChannel::new(
        mighty_run::acp::AcpRequest::default(),
        mighty_run::acp::AcpHooks::writing(std::sync::Arc::new(move |frame| sink.lock().unwrap().push(frame.to_string()))),
    );
    channel.start();
    assert_eq!(written.lock().unwrap()[0], format!("{}\n", accounts::HERMES_INITIALIZE_LINE), "a run's first frame");
    assert!(
        accounts::hermes_version_line(
            "Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882\r\nInstall: C:\\Users\\someone\\AppData\\Local\\hermes\\hermes-agent\r\nPython: 3.12.8\r\nUp to date\r\n"
        ) == "Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882"
            && accounts::hermes_version_line("\n  0.23.0  \nmore") == "0.23.0"
            && accounts::hermes_version_line("").is_empty(),
        "the version is the first line"
    );
    assert!(
        accounts::status_arguments("hermes") == Some(vec!["hermes", "acp"])
            && accounts::login_arguments("hermes") == Some(vec!["hermes", "model"])
            && accounts::logout_arguments("hermes").is_none()
            && accounts::login_hint("hermes") == Some(strings::get("settings.cliAccounts.hermesLoginHint"))
            && accounts::login_hint("hermes").unwrap().contains("hermes model"),
        "argv and what the terminal asks"
    );
}

#[cfg(windows)]
#[tokio::test(flavor = "multi_thread")]
async fn the_probe_writes_initialize_once_reads_the_answer_and_closes_its_input() {
    use std::time::Duration;

    use mighty_run::model::CliCommand;

    common::korean();
    let state = common::temp("hermes-probe");
    let folder = state.file_name().unwrap().to_string_lossy().into_owned();
    let marker = std::env::temp_dir().join(format!("mighty-hermes-probe-{folder}.marker"));
    std::fs::write(&marker, "").unwrap();
    let environment: std::collections::BTreeMap<String, String> = std::env::vars().collect();
    let catalog = common::catalog(|_| None);
    let probe = |mode: &str, seconds: f64| {
        let command =
            CliCommand::new(common::fake_text(), vec!["--hermes-acp-cli".into(), state.to_string_lossy().into_owned(), mode.into()], None);
        let (catalog, environment) = (catalog.clone(), environment.clone());
        async move { accounts::hermes_probe(&catalog, &command, &environment, &std::env::temp_dir(), Duration::from_secs_f64(seconds)).await }
    };
    let read = |name: &str| std::fs::read_to_string(state.join(name)).unwrap_or_default();

    let answer = probe("answer", 20.0).await;
    assert_eq!(
        answer,
        Some(Probe { provider: Some("custom".into()), answered: true, timed_out: false, version: Some("0.23.1".into()) }),
        "the configured answer"
    );
    assert_eq!(read("answer.stdin"), accounts::HERMES_INITIALIZE_LINE, "initialize, written once as one line");
    assert_eq!(common::json(&read("answer.argv.json")), serde_json::json!(["acp"]), "hermes acp");
    assert_eq!(read("answer.closed"), "input ended after 0 more line(s)", "its input closed after the answer, with nothing more written");
    let run = common::json(&read("answer.run.json"));
    assert!(run["NO_COLOR"] == "1" && run["inTemporaryFolder"] == "yes", "quiet, in the temporary folder: {run}");

    let unconfigured = probe("unconfigured", 20.0).await;
    assert!(
        unconfigured.is_some_and(|p| p.answered && p.provider.is_none()) && state.join("unconfigured.closed").exists(),
        "the setup alone"
    );
    let missing = probe("missing", 20.0).await;
    assert!(
        missing.as_ref().is_some_and(|p| !p.answered && !p.timed_out)
            && accounts::hermes_status(missing.as_ref()).detail == strings::get("settings.cliAccounts.detailHermesUnknown"),
        "no ACP packages: unknown, {missing:?}"
    );
    assert!(probe("error", 20.0).await.is_some_and(|p| !p.answered && !p.timed_out), "an error answer");
    let silent = probe("silent", 1.5).await;
    assert!(
        silent.as_ref().is_some_and(|p| !p.answered && p.timed_out)
            && accounts::hermes_status(silent.as_ref()).detail == strings::get("settings.cliAccounts.detailHermesTimeout"),
        "no answer in time: {silent:?}"
    );
    common::until(|| state.join("silent.closed").exists(), 5_000, "its input closed when the wait ran out").await;
    let nowhere = CliCommand::new(state.join("no-hermes-here").to_string_lossy().into_owned(), vec![], None);
    assert!(
        accounts::hermes_probe(&catalog, &nowhere, &environment, &std::env::temp_dir(), Duration::from_secs(5)).await.is_none(),
        "a Hermes that cannot start"
    );
    let _ = std::fs::remove_file(&marker);
    common::remove_when_free(&state).await;
}
