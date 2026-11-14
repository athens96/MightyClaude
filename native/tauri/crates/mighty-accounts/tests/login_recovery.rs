//! C# CliLoginRecoveryVerification (MightyCode.Core.Tests/CliLoginRecoveryVerification.cs, Verification.cs:529-537): the signals that
//! may ask for a sign-in, the sign-in's output, the retry book, the waits, the automatic start's gate, Antigravity's and pi's terminal
//! sign-ins and their launch plans. The run-engine halves (pi's stream, the fake-CLI runs, L537-540) are mighty-run's (tests/pi.rs,
//! tests/runner.rs, tests/acp.rs). With them: the terminal sign-in's wait (`CliTerminalLogin`) and the hidden sign-in over the fake
//! pseudo terminal (`CliBackgroundLogin`).

mod common;

use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use chrono::{DateTime, TimeDelta, Utc};
use mighty_accounts::accounts::{self, CliAccountStatus, CliLoginOption, method};
use mighty_accounts::login::{self, CliAutoLoginGate, CliBackgroundLogin, CliLoginOutputParser, CliLoginRetryBook};
use mighty_accounts::terminal::{self, CliLoginOutcome, CliTerminalSignIn, TerminalHosts, launch_plan, login_wait, terminal_login};
use mighty_core::json::Wire;
use mighty_core::model::AppSnapshot;
use mighty_platform::fake::FakePtyHost;
use mighty_run::failure;
use mighty_run::model::{CliCommand, StartRunRequest};
use mighty_run::strings;
use serde_json::Value;

fn status(provider: &str) -> CliAccountStatus {
    CliAccountStatus::new(provider)
}

fn strings_of(values: &[&str]) -> Vec<String> {
    values.iter().map(|v| v.to_string()).collect()
}

fn epoch_days(days: i64) -> DateTime<Utc> {
    DateTime::<Utc>::UNIX_EPOCH + TimeDelta::days(days)
}

fn request(input: &str, provider: &str) -> StartRunRequest {
    StartRunRequest { provider: provider.into(), ..StartRunRequest::new("pane", "workspace", "claude", input) }
}

/// CliLoginRecoveryVerification.cs:8 `ErrorSignalsExcludeContentAndExternalProviders`.
#[test]
fn error_signals_exclude_content_and_external_providers() {
    common::korean();
    let ordinary: Value =
        serde_json::from_str(r#"{"type":"assistant","message":{"content":[{"type":"text","text":"Please run /login"}]}}"#).unwrap();
    assert!(!failure::claude(&ordinary), "normal assistant prose cannot request a sign-in");
    let error: Value = serde_json::from_str(
        r#"{"type":"assistant","error":"authentication_failed","message":{"content":[{"type":"text","text":"OAuth token has expired"}]}}"#,
    )
    .unwrap();
    assert!(failure::claude(&error), "CLI-marked authentication error qualifies");
    assert!(
        !failure::codex("Reconnecting... 2/5 (401 Unauthorized)") && failure::codex("401 Unauthorized"),
        "retry notices must not invalidate a live turn"
    );
    assert!(
        !failure::claude_text("Bedrock service control policy explicit deny, invalid API key"),
        "organization policy cannot be fixed by browser sign-in"
    );
    for label in ["AWS Bedrock", "Google Vertex AI", "Microsoft Foundry", "Anthropic API key"] {
        let external =
            CliAccountStatus { method: Some(label.into()), access_verified: Some(false), logged_in: Some(true), ..status("claude") };
        assert!(!login::sign_in_can_fix(&external), "external configuration is not a browser login: {label}");
    }
    let api_key = CliAccountStatus { method: Some("any label".into()), method_id: Some(method::CODEX_API_KEY.into()), ..status("codex") };
    assert!(!login::sign_in_can_fix(&api_key), "Codex API-key login is excluded");
    assert!(
        !login::sign_in_can_fix(&accounts::parse_codex_status("Logged in using an API key - sk-***", None)),
        "a parsed Codex API-key login is excluded, whatever its label reads"
    );
    let labelled = CliAccountStatus {
        method: Some(accounts::codex_api_key_method()),
        method_id: Some(method::CODEX_CHATGPT.into()),
        ..status("codex")
    };
    assert!(login::sign_in_can_fix(&labelled), "the decision reads the method id, not the label");
    let gemini = CliAccountStatus {
        method: Some(accounts::gemini_google_method()),
        method_id: Some(method::GEMINI_GOOGLE.into()),
        ..status("gemini")
    };
    assert!(!login::sign_in_can_fix(&gemini), "an Antigravity status that cannot tell opens no sign-in");
    assert!(
        login::sign_in_can_fix(&CliAccountStatus { logged_in: Some(true), ..status("claude") }) && login::sign_in_can_fix(&status("codex")),
        "stale persisted credentials or unknown status do not rule out login recovery"
    );
}

/// CliLoginRecoveryVerification.cs:25 `AutomaticStartOncePerProviderWithCooldown`.
#[test]
fn automatic_start_once_per_provider_with_cooldown() {
    let mut gate = CliAutoLoginGate::default();
    let now = epoch_days(10_000);
    let cooldown = CliAutoLoginGate::COOLDOWN;
    let second = TimeDelta::seconds(1);
    let claude = CliAccountStatus { logged_in: Some(true), method: Some("claude.ai".into()), ..status("claude") };
    let codex = status("codex");
    assert!(gate.should_start("claude", true, &claude, false, now, false, None), "a lost sign-in starts one sign-in");
    assert!(
        !gate.should_start("claude", true, &claude, true, now, false, None),
        "panes failing while the provider's sign-in runs join it instead"
    );
    assert!(!gate.should_start("claude", false, &claude, false, now, false, None), "the switch off leaves only the card");
    assert!(
        gate.should_start("gemini", true, &CliAccountStatus { logged_in: Some(false), ..status("gemini") }, false, now, false, None)
            && !gate.should_start("gemini", true, &claude, false, now, false, None)
            && !gate.should_start("codex", true, &claude, false, now, false, None),
        "Claude, Codex and Antigravity, each decided on its own status"
    );
    assert!(
        !gate.should_start("other", true, &status("other"), false, now, false, None),
        "a provider without login recovery never starts one"
    );
    let bedrock = CliAccountStatus { method: Some("AWS Bedrock".into()), access_verified: Some(false), ..claude.clone() };
    let codex_key =
        CliAccountStatus { method: Some(accounts::codex_api_key_method()), method_id: Some(method::CODEX_API_KEY.into()), ..codex.clone() };
    let missing = CliAccountStatus { installed: false, ..claude.clone() };
    assert!(
        !gate.should_start("claude", true, &bedrock, false, now, false, None)
            && !gate.should_start("codex", true, &codex_key, false, now, false, None)
            && !gate.should_start("claude", true, &missing, false, now, false, None),
        "methods a browser sign-in cannot renew never start one"
    );
    gate.stopped("claude", now);
    assert!(
        !gate.should_start("claude", true, &claude, false, now, false, None)
            && !gate.should_start("claude", true, &claude, false, now + cooldown - second, false, None),
        "a failed or cancelled sign-in waits for the cooldown"
    );
    assert!(gate.should_start("codex", true, &codex, false, now, false, None), "the cooldown is per provider");
    assert!(
        gate.should_start("claude", true, &claude, false, now + cooldown, false, None) && cooldown == TimeDelta::minutes(2),
        "a new failure after two minutes starts again"
    );
    let approved = now + TimeDelta::minutes(10);
    gate.succeeded("claude", approved);
    assert!(
        !gate.should_start("claude", true, &claude, false, approved + TimeDelta::seconds(5), false, Some(approved + second))
            && !gate.should_start("claude", true, &claude, false, approved + cooldown - second, false, None),
        "a success holds automatic starts for the cooldown"
    );
    assert!(
        gate.should_start("claude", true, &claude, false, approved + cooldown, false, Some(approved + cooldown)),
        "a new failure after the hold starts again"
    );
    assert!(
        !gate.should_start("claude", true, &claude, false, approved + TimeDelta::hours(1), false, Some(approved - second)),
        "a run that started before the last sign-in only raises the card"
    );
    assert!(
        !gate.should_start("claude", true, &claude, false, approved + TimeDelta::hours(1), true, Some(approved + TimeDelta::hours(1))),
        "a request login recovery resent only raises the card"
    );

    let mut book = CliLoginRetryBook::new();
    let at = epoch_days(1);
    book.expect_resend("pane");
    book.sent(&request("again", "claude"), Some(at));
    let resent = book.settled("pane").expect("kept");
    assert!(resent.resent && resent.sent_at == Some(at), "login recovery's own resend is marked with its start time");
    book.sent(&request("typed", "claude"), Some(at));
    assert!(!book.settled("pane").unwrap().resent, "the mark is used once; the user's next send is not a resend");
    book.expect_resend("pane");
    book.cancel_resend("pane");
    book.sent(&request("typed", "claude"), None);
    assert!(!book.settled("pane").unwrap().resent, "a resend that never started leaves no mark");

    assert!(AppSnapshot::default().auto_login_clis != Some(false), "absent preference must default on");
    assert!(AppSnapshot::read(&serde_json::json!({})).unwrap().auto_login_clis != Some(false), "an empty file defaults on");
    for invalid in ["\"false\"", "0", "1", "null", "[]"] {
        let value: Value = serde_json::from_str(&format!("{{\"autoLoginCLIs\":{invalid}}}")).unwrap();
        assert!(
            AppSnapshot::read(&value).unwrap().auto_login_clis.is_none(),
            "only a JSON boolean may switch automatic sign-in: {invalid}"
        );
    }
    let off = AppSnapshot { auto_login_clis: Some(false), ..AppSnapshot::default() };
    let saved = mighty_core::normalize::normalize(&off, true).unwrap().to_json();
    assert!(
        saved.contains("\"autoLoginCLIs\":false")
            && AppSnapshot::read(&serde_json::from_str(&saved).unwrap()).unwrap().auto_login_clis == Some(false),
        "an explicit off must survive normalization and a restart under the Mac's key; got {saved}"
    );
}

/// CliLoginRecoveryVerification.cs:62 `OutputHandlesPartialUrlsAndCodePrompts`.
#[test]
fn output_handles_partial_urls_and_code_prompts() {
    let mut parser = CliLoginOutputParser::new();
    parser.append("https://docs.example.com/help\nhttps://auth.openai.com/oauth?state=abc");
    assert_eq!(parser.current().url.as_ref().and_then(|u| u.host_str()), Some("docs.example.com"), "incomplete URL remains pending");
    parser.append("DEF\nPaste code here >");
    assert!(
        parser.current().url.as_ref().and_then(|u| u.query()) == Some("state=abcDEF") && parser.current().asks_for_code,
        "complete auth host replaces earlier docs URL and detects prompt"
    );
    let mut parser = CliLoginOutputParser::new();
    parser.append("\u{1b}]8;;https://claude.ai/oauth?state=fixture\u{7}Sign in\u{1b}]8;;\u{7}");
    assert_eq!(parser.current().url.as_ref().and_then(|u| u.host_str()), Some("claude.ai"), "OSC hyperlink retains its target");
    let mut parser = CliLoginOutputParser::new();
    parser.append("Enter this code in your browser: ABC123\nhttps://auth.openai.com/device");
    assert!(
        !parser.current().asks_for_code && parser.current().url.is_none(),
        "displayed code is not a code-input prompt and end-of-output URL waits"
    );
    parser.finish();
    assert_eq!(parser.current().url.as_ref().map(|u| u.path()), Some("/device"), "process completion finishes trailing URL");
    parser.clear();
    parser.append(&"가".repeat(CliLoginOutputParser::MAXIMUM_BYTES));
    parser.append("\nhttps://evil.example.com/\nPaste code");
    assert!(parser.current().url.is_none() && !parser.current().asks_for_code, "UTF-8 byte cap bounds hostile output");
    assert!(
        CliLoginOutputParser::is_auth_url(url::Url::parse("https://login.chatgpt.com/x").ok().as_ref())
            && !CliLoginOutputParser::is_auth_url(url::Url::parse("https://notchatgpt.com/x").ok().as_ref()),
        "a sign-in host or its subdomain only"
    );
}

/// CliLoginRecoveryVerification.cs:78 `RetryGenerationPreservesAttachmentsAndDropsStaleWork`.
#[test]
fn retry_generation_preserves_attachments_and_drops_stale_work() {
    let mut book = CliLoginRetryBook::new();
    let first = request("first", "codex");
    book.sent(&first, None);
    let failed = book.settled("pane").unwrap();
    assert!(book.remember(failed.clone()) && book.requests().len() == 1, "failed request retained for retry");
    book.sent(&request("second", "codex"), None);
    assert!(book.requests().is_empty() && !book.remember(failed), "new prompt invalidates previous failure and async confirmation");
    let second = book.settled("pane").unwrap();
    book.remember(second.clone());
    book.drop_session("pane");
    assert!(!book.remember(second) && book.take("pane").is_none(), "dismissed or closed pane cannot be resurrected by late status");
    book.sent(&first, None);
    let third = book.settled("pane").unwrap();
    book.remember(third);
    assert!(book.take("pane").is_some_and(|r| r.request.input == "first") && book.take("pane").is_none(), "retry consumed once");
    book.sent(&StartRunRequest::new("shell-pane", "workspace", "shell", "ls"), None);
    assert!(book.settled("shell-pane").is_none(), "a shell pane's request is nobody's sign-in");
}

/// CliLoginRecoveryVerification.cs:209 `WaitRequiresFreshSuccessWhenAlreadySignedIn`.
#[tokio::test]
async fn wait_requires_fresh_success_when_already_signed_in() {
    let reads = Arc::new(AtomicUsize::new(0));
    let signed_in = |reads: Arc<AtomicUsize>| {
        move || {
            reads.fetch_add(1, Ordering::SeqCst);
            async { CliAccountStatus { logged_in: Some(true), ..CliAccountStatus::new("codex") } }
        }
    };
    let ms = Duration::from_millis;
    let expired = login_wait(|| true, || None, signed_in(reads.clone()), None, Some(ms(15)), Some(ms(2))).await;
    assert!(
        expired.outcome == CliLoginOutcome::TimedOut && reads.load(Ordering::SeqCst) == 2,
        "stale signed-in status cannot auto-resend while login still runs"
    );
    let failed = login_wait(|| false, || Some(1), signed_in(reads.clone()), None, None, Some(ms(1))).await;
    assert_eq!(failed.outcome, CliLoginOutcome::Exited, "nonzero exit cannot confirm an already-signed-in account");
    let passed = login_wait(|| false, || Some(0), signed_in(reads.clone()), None, None, Some(ms(1))).await;
    assert_eq!(passed.outcome, CliLoginOutcome::LoggedIn, "zero exit plus signed-in status confirms recovery");
    let count = Arc::new(AtomicUsize::new(0));
    let turning = {
        let count = count.clone();
        move || {
            let n = count.fetch_add(1, Ordering::SeqCst) + 1;
            async move { CliAccountStatus { logged_in: Some(n > 1), ..CliAccountStatus::new("claude") } }
        }
    };
    let transition = login_wait(|| true, || None, turning, Some(Duration::ZERO), None, Some(ms(1))).await;
    assert_eq!(transition.outcome, CliLoginOutcome::LoggedIn, "signed-out to signed-in transition confirms a still-running login");
    // Cancelling is dropping the wait: once dropped it reads nothing more.
    let polls = Arc::new(AtomicUsize::new(0));
    let task = tokio::spawn(login_wait(|| true, || None, signed_in(polls.clone()), None, None, Some(ms(1))));
    tokio::time::sleep(ms(20)).await;
    task.abort();
    let seen = polls.load(Ordering::SeqCst);
    tokio::time::sleep(ms(20)).await;
    assert!(seen == 1 && polls.load(Ordering::SeqCst) == seen, "a cancelled wait reads no more: {seen}");
}

/// CliLoginRecoveryVerification.cs:96 `GeminiSignalsAndMethods`.
#[test]
fn gemini_signals_and_methods() {
    common::korean();
    for text in ["Error: authentication required. Run 'agy' to log in, then retry.", "Print mode: auth error: token expired"] {
        assert!(failure::antigravity(text) && failure::matches("gemini", text), "antigravity sign-in failure: {text}");
    }
    for text in [
        "warning: conversation \"x\" not found",
        "Print mode: auth timed out",
        "HTTP 407 Proxy Authentication Required",
        "authentication required",
        "401 files changed",
    ] {
        assert!(!failure::antigravity(text), "not an antigravity sign-in failure: {text}");
    }
    assert!(
        !failure::matches("claude", "Print mode: auth error") && !failure::matches("other", "authentication required. Run 'agy'"),
        "each provider by its own rules"
    );
    assert!(
        failure::antigravity_succeeded(r#"{"event":"result","result":{"status":"SUCCESS"}}"#)
            && !failure::antigravity_succeeded(r#"{"event":"result","result":{"status":"ERROR"}}"#)
            && !failure::antigravity_succeeded(r#"{"event":"step_update","step_update":{"text_delta":"\"result\""}}"#)
            && !failure::antigravity_succeeded(r#"{"result""#),
        "only a successful result line clears the signal"
    );
    let signed_in = accounts::antigravity_status(0, "gemini-3.8-flash-low\tGemini 3.8 Flash (Low)\n", false);
    let signed_out = accounts::antigravity_status(1, "Error: authentication required. Run 'agy' to log in, then retry.\n", false);
    let offline = accounts::antigravity_status(1, "dial tcp: lookup failed", false);
    assert!(
        !login::sign_in_can_fix(&signed_in) && login::sign_in_can_fix(&signed_out) && !login::sign_in_can_fix(&offline),
        "only a signed-out status can be fixed by signing in"
    );
    assert!(!login::sign_in_can_fix(&CliAccountStatus { installed: false, ..signed_out.clone() }), "no CLI, no sign-in");
    let mut gate = CliAutoLoginGate::default();
    let now = epoch_days(10_000);
    assert!(
        !gate.should_start("gemini", true, &offline, false, now, false, None)
            && !gate.should_start("gemini", true, &signed_in, false, now, false, None),
        "an unknown or signed-in status never opens the sign-in terminal"
    );
    assert!(
        gate.should_start("gemini", true, &signed_out, false, now, false, None)
            && !gate.should_start("gemini", true, &signed_out, true, now, false, None),
        "one Antigravity sign-in at a time"
    );
    gate.stopped("gemini", now);
    assert!(
        !gate.should_start("gemini", true, &signed_out, false, now + CliAutoLoginGate::COOLDOWN - TimeDelta::seconds(1), false, None)
            && gate.should_start("gemini", true, &signed_out, false, now + CliAutoLoginGate::COOLDOWN, false, None),
        "a cancelled Antigravity sign-in waits for the cooldown"
    );
    gate.succeeded("gemini", now + TimeDelta::hours(1));
    assert!(
        !gate.should_start("gemini", true, &signed_out, false, now + TimeDelta::hours(2), false, Some(now + TimeDelta::minutes(59))),
        "an Antigravity run older than the last sign-in only raises the card"
    );
    let mut book = CliLoginRetryBook::new();
    book.sent(&request("again", "gemini"), None);
    assert!(book.settled("pane").is_some_and(|r| r.request.provider == "gemini"), "an Antigravity request is kept for its retry");
}

/// CliLoginRecoveryVerification.cs:125 `AntigravitySignInIsItsStatusReadingSignedIn`.
#[tokio::test]
async fn antigravity_sign_in_is_its_status_reading_signed_in() {
    let signed_out =
        CliAccountStatus { logged_in: Some(false), method_id: Some(method::GEMINI_GOOGLE.into()), can_sign_out: false, ..status("gemini") };
    let signed_in = CliAccountStatus { logged_in: Some(true), ..signed_out.clone() };
    let ms = Duration::from_millis;
    let reads = Arc::new(AtomicUsize::new(0));
    let (r, out, inn) = (reads.clone(), signed_out.clone(), signed_in.clone());
    let read = move || {
        let value = if r.fetch_add(1, Ordering::SeqCst) + 1 >= 3 { inn.clone() } else { out.clone() };
        async move { value }
    };
    let outcome = CliTerminalSignIn::wait("gemini", read, || true, Some(Duration::ZERO), Some(Duration::from_secs(20)), Some(ms(10))).await;
    assert!(
        outcome.outcome == CliLoginOutcome::LoggedIn && outcome.status == signed_in,
        "the status reading signed in ends the wait signed in"
    );
    let fixed = |value: CliAccountStatus| move || std::future::ready(value.clone());
    let closed =
        CliTerminalSignIn::wait("gemini", fixed(signed_out.clone()), || false, Some(Duration::from_secs(60)), None, Some(ms(10))).await;
    assert!(closed.outcome == CliLoginOutcome::Exited && closed.status == signed_out, "a closed terminal without a sign-in ends the wait");
    let expired =
        CliTerminalSignIn::wait("gemini", fixed(signed_out.clone()), || true, Some(Duration::from_secs(60)), Some(ms(50)), Some(ms(10)))
            .await;
    assert_eq!(expired.outcome, CliLoginOutcome::TimedOut, "the limit ends the wait");
    let polled = Arc::new(AtomicUsize::new(0));
    let (p, out) = (polled.clone(), signed_out.clone());
    let counting = move || {
        p.fetch_add(1, Ordering::SeqCst);
        std::future::ready(out.clone())
    };
    let slow = CliTerminalSignIn::wait("gemini", counting, || true, Some(Duration::from_secs(60)), Some(ms(200)), Some(ms(10))).await;
    assert!(
        slow.outcome == CliLoginOutcome::TimedOut && polled.load(Ordering::SeqCst) == 1,
        "the status is read only every interval, and once more at the limit; got {}",
        polled.load(Ordering::SeqCst)
    );
    let claude = CliAccountStatus { logged_in: Some(true), ..status("claude") };
    let other = CliTerminalSignIn::wait("gemini", fixed(claude.clone()), || true, Some(Duration::ZERO), Some(ms(100)), Some(ms(10))).await;
    assert!(other.outcome == CliLoginOutcome::TimedOut && other.status == claude, "another provider's sign-in is not this one's");
    let pi = CliTerminalSignIn::wait(
        "pi",
        fixed(CliAccountStatus { logged_in: Some(true), ..status("pi") }),
        || true,
        Some(Duration::ZERO),
        Some(Duration::from_secs(20)),
        Some(ms(10)),
    )
    .await;
    assert_eq!(pi.outcome, CliLoginOutcome::LoggedIn, "pi's terminal sign-in ends with pi's status");

    let hosts = TerminalHosts::new(None);
    let agy = strings_of(&accounts::login_arguments("gemini", CliLoginOption::Account).unwrap());
    let plan = launch_plan(&hosts, &agy, true, Some(r"C:\Projects\app"), None, None, None).unwrap();
    assert_eq!(
        plan.1,
        strings_of(&["new-tab", "-d", r"C:\Projects\app", "--", "agy"]),
        "the Antigravity sign-in terminal starts in the pane's workspace"
    );
    assert_eq!(
        launch_plan(&hosts, &agy, true, Some(r"C:\a;b"), None, None, None).unwrap().1,
        strings_of(&["new-tab", "--", "agy"]),
        "a folder Windows Terminal would split is not passed on"
    );
    let split = launch_plan(&hosts, &agy, true, Some(r"C:\work"), Some(r"C:\Tools;x\agy.exe"), None, None).unwrap();
    assert!(
        split.0 == hosts.console_host
            && split.1 == strings_of(&[r"C:\Tools;x\agy.exe"])
            && launch_plan(&hosts, &strings_of(&["agy", "a;b"]), true, None, None, None, None).unwrap().0 == hosts.console_host
            && launch_plan(&hosts, &agy, true, None, Some(r"C:\Tools\agy.exe"), None, None).unwrap().0 == "wt.exe",
        "a command Windows Terminal would split runs in conhost"
    );
    assert_eq!(
        launch_plan(&hosts, &agy, false, Some(r"C:\Projects\app"), None, None, None).unwrap().1,
        agy,
        "the console host inherits the folder instead"
    );
    let full = r"C:\Users\me\AppData\Local\agy\bin\agy.exe";
    assert_eq!(
        launch_plan(&hosts, &strings_of(&[full]), false, None, Some(full), None, None).unwrap().1,
        strings_of(&[full]),
        "the console host runs agy by its full path"
    );
}

const AGY_SIGNED_OUT: &str =
    "Fetching available models...\nError: Please sign in to view available models. Launch the CLI without arguments to sign in.";

/// CliLoginRecoveryVerification.cs:229 `AgySignedOutWithExitZeroReadsSignedOut`.
#[test]
fn agy_signed_out_with_exit_zero_reads_signed_out() {
    common::korean();
    assert_eq!(
        accounts::antigravity_status(0, AGY_SIGNED_OUT, false).logged_in,
        Some(false),
        "exit 0 with its sign-in words is signed out"
    );
    assert_eq!(accounts::antigravity_status(1, AGY_SIGNED_OUT, false).logged_in, Some(false), "and with exit 1");
    assert_eq!(
        accounts::antigravity_status(0, "Fetching available models...\ngemini-3.8-flash-low\tGemini 3.8 Flash (Low)\n", false).logged_in,
        Some(true),
        "a clean list with exit 0 is signed in"
    );
    assert!(
        accounts::antigravity_status(-1, "", false).logged_in.is_none() && accounts::antigravity_status(0, "", true).logged_in.is_none(),
        "a failure or a timeout stays unknown"
    );
    assert!(login::sign_in_can_fix(&accounts::antigravity_status(0, AGY_SIGNED_OUT, false)), "a sign-in can fix it");
    for text in
        ["authentication failed or timed out", "Error: Please sign in to view available models.", "You are not signed in", AGY_SIGNED_OUT]
    {
        assert!(failure::antigravity(text) && failure::matches("gemini", text), "agy's newer words: {text}");
    }
    assert!(!failure::antigravity("407 Proxy Authentication Required"), "a proxy's 407 is not a lost sign-in");
}

/// CliLoginRecoveryVerification.cs:245 `PiSignInFailuresAndTheTerminalTable`, the terminal sign-ins' half (the stream half is
/// mighty-run's tests/pi.rs; opencode's and Hermes's words its tests/acp.rs).
#[test]
fn pi_sign_in_failures_and_the_terminal_table() {
    common::korean();
    assert!(
        login::sign_in_can_fix(&CliAccountStatus { logged_in: Some(false), ..status("pi") })
            && !login::sign_in_can_fix(&CliAccountStatus { logged_in: Some(true), ..status("pi") }),
        "pi: only a signed-out status asks for a sign-in"
    );
    assert!(
        !login::sign_in_can_fix(&CliAccountStatus { detail: "offline".into(), ..status("pi") })
            && !login::sign_in_can_fix(&CliAccountStatus { installed: false, logged_in: Some(false), ..status("pi") }),
        "unknown or no CLI opens none"
    );
    assert!(
        login::sign_in_can_fix(&accounts::pi_status(0, "No models available. Use /login to log in.", None, &Default::default(), false)),
        "pi's own no-models reading can be fixed by signing in"
    );
    assert_eq!(
        login::PROVIDERS,
        ["claude", "codex", "gemini", "pi", "opencode", "hermes"],
        "the providers whose lost sign-in raises the card"
    );
    let mut keys: Vec<&str> = CliTerminalSignIn::table().iter().map(|e| e.provider).collect();
    keys.sort();
    assert_eq!(keys, ["gemini", "hermes", "opencode", "pi"], "Antigravity, pi, opencode and Hermes sign in in a terminal");
    let entry = |p: &str| CliTerminalSignIn::get(p).unwrap();
    assert!(
        entry("opencode").command() == Some(vec!["opencode", "auth", "login"])
            && entry("hermes").command() == Some(vec!["hermes", "model"])
            && entry("opencode").title() == strings::get("loginRecovery.opencodeTerminalTitle")
            && entry("hermes").title() == strings::get("loginRecovery.hermesTerminalTitle"),
        "opencode's and Hermes's terminals"
    );
    assert!(
        entry("pi").command() == Some(vec!["pi"]) && entry("pi").title_key.is_some(),
        "pi's terminal runs pi, titled with what to type"
    );
    assert_eq!(entry("gemini").command(), Some(vec![mighty_run::providers::executable("gemini")]), "Antigravity's runs agy");
    assert!(
        CliTerminalSignIn::table().iter().all(|e| login::PROVIDERS.contains(&e.provider) && strings::get(e.waiting_key) != e.waiting_key),
        "each has its card sentence"
    );
    assert!(
        CliTerminalSignIn::waiting_text("pi") == strings::get("loginRecovery.piTerminal")
            && CliTerminalSignIn::waiting_text("gemini") == strings::get("loginRecovery.antigravityTerminal")
            && CliTerminalSignIn::waiting_text("claude") == strings::get("loginRecovery.waiting"),
        "the card's words while it waits"
    );
    assert!(
        entry("pi").title() == strings::get("loginRecovery.piTerminalTitle")
            && entry("gemini").title() == strings::get_with("loginRecovery.terminalTitle", &[("provider", "Antigravity")]),
        "the terminal's title"
    );
    let mut book = CliLoginRetryBook::new();
    book.sent(&request("again", "pi"), None);
    assert!(book.settled("pane").is_some_and(|r| r.request.provider == "pi"), "a pi request is kept for its retry");
    let gate = CliAutoLoginGate::default();
    let now = epoch_days(10_000);
    assert!(
        gate.should_start("pi", true, &CliAccountStatus { logged_in: Some(false), ..status("pi") }, false, now, false, None)
            && !gate.should_start("pi", true, &status("pi"), false, now, false, None),
        "pi's sign-in starts only for a signed-out status"
    );

    let hosts = TerminalHosts::new(None);
    let shim = r"C:\Users\me\AppData\Roaming\npm\pi.cmd";
    let title = entry("pi").title();
    let tab =
        launch_plan(&hosts, &strings_of(&[shim]), true, Some(r"C:\Projects\app"), Some(shim), Some(r"C:\wt.exe"), Some(&title)).unwrap();
    assert!(
        tab.0 == r"C:\wt.exe"
            && tab.1 == strings_of(&["new-tab", "--title", &title, "-d", r"C:\Projects\app", "--", &hosts.command_interpreter, "/c", shim]),
        "Windows Terminal: {:?}",
        tab.1
    );
    let host = launch_plan(&hosts, &strings_of(&[shim]), false, Some(r"C:\Projects\app"), Some(shim), None, Some(&title)).unwrap();
    assert!(
        host.0 == hosts.console_host && host.1 == strings_of(&[&hosts.command_interpreter, "/c", shim]),
        "the console host: {:?}",
        host.1
    );
    assert_eq!(
        launch_plan(&hosts, &strings_of(&["agy"]), true, None, None, None, Some("a;b")).unwrap().1,
        strings_of(&["new-tab", "--", "agy"]),
        "a title Windows Terminal would split is left out"
    );
    assert!(terminal::is_batch("PI.CMD") && terminal::is_batch("x.bat") && !terminal::is_batch("agy.exe"), "batch files");
}

/// `CliTerminalLogin.RunAsync` (CliLoginRecovery.cs:286): a status that turns from signed out to signed in confirms (and resends);
/// one signed in from the start confirms without resending unless it read signed out before the terminal opened; a closed window
/// ends the wait with one more reading; the limit ends it.
#[tokio::test]
async fn the_terminal_sign_in_wait_reads_the_status_until_it_turns_signed_in() {
    let ms = Duration::from_millis;
    let out = CliAccountStatus { logged_in: Some(false), ..status("claude") };
    let inn = CliAccountStatus { logged_in: Some(true), ..status("claude") };
    let count = Arc::new(AtomicUsize::new(0));
    let (c, o, i) = (count.clone(), out.clone(), inn.clone());
    let turning = move || {
        let value = if c.fetch_add(1, Ordering::SeqCst) >= 2 { i.clone() } else { o.clone() };
        std::future::ready(value)
    };
    let result = terminal_login::run(turning, None, Some(ms(5)), Some(Duration::from_secs(5)), None).await;
    assert!(result.outcome == CliLoginOutcome::LoggedIn && result.started_signed_out && terminal_login::resends(&result), "{result:?}");
    let fixed = |value: CliAccountStatus| move || std::future::ready(value.clone());
    let already = terminal_login::run(fixed(inn.clone()), None, Some(ms(5)), None, None).await;
    assert!(already.outcome == CliLoginOutcome::LoggedIn && !terminal_login::resends(&already), "signed in from the start resends nothing");
    let before = terminal_login::run(fixed(inn.clone()), None, Some(ms(5)), None, Some(&out)).await;
    assert!(terminal_login::resends(&before), "signed out before the terminal opened, then signed in");
    let (window, closed) = tokio::sync::watch::channel(false);
    let closing = tokio::spawn(async move {
        tokio::time::sleep(ms(30)).await;
        window.send_replace(true);
        window
    });
    let shut = terminal_login::run(fixed(out.clone()), Some(closed), Some(Duration::from_secs(60)), None, None).await;
    assert_eq!(shut.outcome, CliLoginOutcome::Exited, "a closed window ends the wait at once");
    drop(closing.await);
    let late = terminal_login::run(fixed(out.clone()), None, Some(ms(5)), Some(ms(30)), None).await;
    assert_eq!(late.outcome, CliLoginOutcome::TimedOut, "the limit ends the wait");
    let unverified = CliAccountStatus { access_verified: Some(false), ..inn.clone() };
    assert!(terminal_login::signed_in(&inn) && !terminal_login::signed_in(&unverified), "configuration is not a sign-in");
    assert!(
        terminal_login::POLL_INTERVAL == Duration::from_secs(5) && terminal_login::POLL_LIMIT == Duration::from_secs(600),
        "every 5 s for 10 minutes"
    );
}

/// `CliBackgroundLogin` over the fake pseudo terminal (which echoes its input back): the CLI's own sign-in 1000 columns wide in the
/// home, its link and code prompt read from its output, a pasted code taken only as one clean line, its end seen, and the terminal
/// closed with what was read forgotten.
#[tokio::test]
async fn the_hidden_sign_in_reads_its_link_and_takes_a_pasted_code() {
    let home = common::temp("background-login");
    let ptys = FakePtyHost::default();
    let command = CliCommand::new(common::text(&home.join(common::program("claude"))), vec!["--prefix".into()], None);
    let login = CliBackgroundLogin::start(&ptys, &command, &strings_of(&["auth", "login"]), &home, common::env(&[("PATH", "/bin")]))
        .await
        .expect("it started");
    let opened = &ptys.opened()[0];
    assert!(
        opened.program == std::path::Path::new(&command.binary)
            && opened.args == strings_of(&["--prefix", "auth", "login"])
            && opened.cwd == home
            && opened.size.columns == 1000
            && opened.env.get("PATH").map(String::as_str) == Some("/bin"),
        "{opened:?}"
    );
    let mut changes = login.changes();
    assert!(!login.send_code("") && !login.send_code("two\nlines") && !login.send_code(&"x".repeat(4097)), "only one clean line");
    assert!(login.send_code(" https://claude.ai/oauth?state=fixture "), "a code is written");
    tokio::time::timeout(Duration::from_secs(5), changes.wait_for(|o| o.url.is_some())).await.expect("the link").unwrap();
    assert_eq!(login.output().url.as_ref().and_then(|u| u.host_str()), Some("claude.ai"), "the link it printed");
    assert!(login.send_code("Paste code here"), "the prompt");
    tokio::time::timeout(Duration::from_secs(5), changes.wait_for(|o| o.asks_for_code)).await.expect("the prompt").unwrap();
    assert!(login.is_running() && login.exit_code().is_none(), "it runs");
    ptys.exit(0, 0);
    assert_eq!(tokio::time::timeout(Duration::from_secs(5), login.completion()).await.unwrap(), Some(0), "its end");
    assert!(!login.is_running() && login.exit_code() == Some(0), "ended by itself");
    login.finish_output();
    login.close().await;
    assert!(ptys.closed(0) && login.output().url.is_none(), "closed, and what was read forgotten");
    let _ = std::fs::remove_dir_all(&home);
}
