//! C# PluginMarketplaceVerification (Verification.cs:1509-1528), every check under its name: the two plugin mutations macOS has —
//! install a plugin, refresh a marketplace — over a fake runner that answers every subcommand. No real claude or codex starts, no
//! network, no real profile; a real install stays an on-device checklist item.
//!
//! `WindowIsWiredIntoTheRunningAppAndTheSmokeRun` and the WinUI half of `TheRunningAppOwnsTheOperationsObject` scan WinUI source for
//! the window's buttons; their Core halves are here (the smoke's fake reader and key, the app's operations object and its runner),
//! and the wiring becomes registry and smoke checks with the plugin panel (phase 2).

mod common;

use std::collections::{HashSet, VecDeque};
use std::path::Path;
use std::sync::{Arc, Mutex};

use common::*;
use mighty_core::model::Workspace;
use mighty_platform::BoxFuture;
use mighty_platform::fake::{FakeProcessHost, FakeShellEnvironment};
use mighty_updates::plugins::browser::PluginMarketplaceSmokeOutcome;
use mighty_updates::plugins::smoke::{FakeMarketplaceReader, claude_snapshot, codex_snapshot};
use mighty_updates::plugins::strings::{codex as codex_strings, plugin as strings};
use mighty_updates::plugins::{
    ClaudeCatalogPlugin, ClaudePluginBrowser, ClaudePluginMarketplace, ClaudePluginOperationResult, ClaudePluginReader,
    ClaudePluginSnapshot, CodexPluginReader, PluginOperations, PluginReader, ProviderReader, status,
};
use mighty_updates::runner::{CancellationToken, CliCall, CliRunResult, CliRunner, RunError};

fn none() -> CancellationToken {
    CancellationToken::new()
}

/// A run that asks for a change (the help of "plugin add" is still only reading).
fn changes(args: &[String]) -> bool {
    let a: Vec<&str> = args.iter().map(String::as_str).collect();
    !a.contains(&"--help")
        && matches!(
            a.as_slice(),
            ["plugin", "install", ..]
                | ["plugin", "add", ..]
                | ["plugin", "marketplace", "update", ..]
                | ["plugin", "marketplace", "upgrade", ..]
        )
}

fn mutation(runner: &FakeRunner) -> Option<Vec<String>> {
    runner.args().into_iter().find(|a| changes(a))
}

fn mutations(runner: &FakeRunner) -> usize {
    runner.args().iter().filter(|a| changes(a)).count()
}

fn as_strs(args: &[String]) -> Vec<&str> {
    args.iter().map(String::as_str).collect()
}

// ---- Claude fixtures ------------------------------------------------------------------------------------------------------

const CLAUDE_MARKETS: &str = r#"[{"name":"sample","source":{"source":"github"}}]"#;
const INSTALL_OK: &str = r#"{"command":"install","outcome":"ok","pluginId":"fmt@sample","scope":"local"}"#;

fn claude_listing(installed_scope: &str, workspace_path: &str) -> String {
    let installed = if installed_scope.is_empty() {
        String::new()
    } else {
        let project = if matches!(installed_scope, "project" | "local") {
            format!(r#","projectPath":{}"#, serde_json::to_string(workspace_path).unwrap())
        } else {
            String::new()
        };
        format!(r#"{{"id":"fmt@sample","scope":"{installed_scope}","enabled":true{project}}}"#)
    };
    format!(
        r#"{{"installed":[{installed}],"available":[{{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","description":"Formats source files","version":"1.2.0","source":{{"source":"github"}}}}]}}"#
    )
}

fn claude_reader(runner: &Arc<FakeRunner>) -> ClaudePluginReader {
    let bin = temp("market-claude-bin");
    let executable = text(&bin.join("claude"));
    ClaudePluginReader::with_probes(
        runner.clone(),
        env(&[&bin]),
        Arc::new(move |path: &str| path == executable),
        Arc::new(|path: &str| Path::new(path).is_dir()),
    )
}

fn claude(runner: &Arc<FakeRunner>) -> (ClaudePluginReader, Workspace) {
    (claude_reader(runner), workspace(&text(&temp("market-claude"))))
}

/// A claude that answers the version probe and both reads, and hands the mutation to `mutate`.
fn claude_runner(
    mutate: impl Fn(&[String]) -> Result<CliRunResult, RunError> + Send + Sync + 'static,
    listing: Option<String>,
    version: &str,
) -> Arc<FakeRunner> {
    let listing = listing.unwrap_or_else(|| claude_listing("", ""));
    let version = version.to_string();
    FakeRunner::new(move |_, args| match as_strs(args).as_slice() {
        ["--version"] => ok(&version),
        ["plugin", "list", ..] => ok(&listing),
        ["plugin", "marketplace", "list", ..] => ok(CLAUDE_MARKETS),
        _ => mutate(args),
    })
}

fn claude_ok(mutate: impl Fn(&[String]) -> Result<CliRunResult, RunError> + Send + Sync + 'static) -> Arc<FakeRunner> {
    claude_runner(mutate, None, "2.1.271 (Claude Code)")
}

/// The asynchronous claude of the window checks: an install (or a refresh when `refresh`) waits for `gate` or the cancel.
fn gated_claude(runner: &FakeRunner, gate: &Signal, refresh: bool, listings: Arc<Mutex<VecDeque<String>>>, fallback: String) {
    let gate = gate.clone();
    runner.set_async(move |call: CliCall, token| {
        let (gate, listings, fallback) = (gate.clone(), listings.clone(), fallback.clone());
        Box::pin(async move {
            let a = as_strs(&call.arguments);
            let gated = if refresh {
                matches!(a.as_slice(), ["plugin", "marketplace", "update", ..])
            } else {
                matches!(a.as_slice(), ["plugin", "install", ..])
            };
            if gated {
                gate.wait_or_cancel(&token).await?;
                return ok(if refresh { "" } else { INSTALL_OK });
            }
            match a.as_slice() {
                ["--version"] => ok("2.1.271 (Claude Code)"),
                ["plugin", "marketplace", "list", ..] => ok(CLAUDE_MARKETS),
                _ => {
                    let next = listings.lock().unwrap().pop_front();
                    ok(&next.unwrap_or(fallback))
                }
            }
        })
    });
}

// ---- Codex fixtures -------------------------------------------------------------------------------------------------------

const CODEX_MARKETS: &str =
    r#"{"marketplaces":[{"name":"sample","marketplaceSource":{"sourceType":"git","source":"https://example.com/sample"}}]}"#;
const CODEX_LOCAL_MARKETS: &str = r#"{"marketplaces":[{"name":"sample","marketplaceSource":{"sourceType":"local","source":"./sample"}}]}"#;

fn codex_listing(installed: bool) -> String {
    format!(
        r#"{{"installed":[{}],"available":[{{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","installed":false,"enabled":false,"installPolicy":"AVAILABLE"}}]}}"#,
        if installed { r#"{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","installed":true,"enabled":true}"# } else { "" }
    )
}

fn codex(runner: &Arc<FakeRunner>) -> (CodexPluginReader, Workspace) {
    let bin = temp("market-codex-bin");
    let executable = text(&bin.join("codex"));
    let reader = CodexPluginReader::with_probes(
        runner.clone(),
        env(&[&bin]),
        Arc::new(move |path: &str| path == executable),
        Arc::new(|path: &str| Path::new(path).is_dir()),
    );
    (reader, workspace(&text(&temp("market-codex"))))
}

fn codex_help(args: &[&str]) -> Result<CliRunResult, RunError> {
    match args {
        [_, "list", "--help"] => ok("  codex plugin list [OPTIONS]\n  --json    Output JSON\n  --available  Include catalog\n"),
        [_, "add", "--help"] => ok("  codex plugin add [OPTIONS]\n  --json    Output JSON\n"),
        [_, "marketplace", "list", "--help"] => ok("  codex plugin marketplace list [OPTIONS]\n  --json    Output JSON\n"),
        [_, "marketplace", "upgrade", "--help"] => ok("  codex plugin marketplace upgrade [OPTIONS]\n  --json    Output JSON\n"),
        _ => failed("unknown"),
    }
}

/// A codex whose reads answer from a queue, so the verifying second read an install does can report the plugin installed.
fn codex_runner(
    mutate: impl Fn(&[String]) -> Result<CliRunResult, RunError> + Send + Sync + 'static,
    listings: Vec<String>,
    markets: &'static str,
) -> Arc<FakeRunner> {
    let queue = Mutex::new(VecDeque::from(listings));
    FakeRunner::new(move |_, args| {
        let a = as_strs(args);
        match a.as_slice() {
            ["--version"] => ok("codex-cli 0.9.0"),
            [_, _, "--help"] | [_, _, _, "--help"] => codex_help(&a),
            ["plugin", "list", ..] => ok(&queue.lock().unwrap().pop_front().unwrap_or_else(|| codex_listing(false))),
            ["plugin", "marketplace", "list", ..] => ok(markets),
            _ => mutate(args),
        }
    })
}

fn dyn_reader(reader: &ClaudePluginReader) -> Arc<dyn PluginReader> {
    Arc::new(reader.clone())
}

// ---- Claude install --------------------------------------------------------------------------------------------------------

#[tokio::test]
async fn claude_install_runs_the_cli_command_and_reports_success() {
    korean();
    let runner = claude_ok(|_| ok(INSTALL_OK));
    let (reader, ws) = claude(&runner);
    let result = reader.install("fmt@sample", "local", &ws, none()).await;
    assert_eq!(result.status, status::SUCCEEDED, "an ok outcome succeeds");
    assert_eq!(result.detail, strings::install_succeeded(), "the success sentence is the macOS one");
    assert_eq!(
        mutation(&runner),
        Some(strings(&["plugin", "install", "fmt@sample", "--scope", "local", "--json"])),
        "the macOS argument list"
    );
    assert_eq!(mutations(&runner), 1, "exactly one changing run");
}

#[tokio::test]
async fn claude_install_reports_failure_and_unconfirmed_results() {
    korean();
    let failing = claude_ok(|_| ok(r#"{"command":"install","outcome":"failed"}"#));
    let (reader, ws) = claude(&failing);
    let result = reader.install("fmt@sample", "local", &ws, none()).await;
    assert!(
        result.status == status::FAILED && result.detail == strings::install_failed(),
        "a failed outcome reports the macOS failure sentence"
    );

    let noise = claude_ok(|_| ok("not json at all"));
    let (second, second_ws) = claude(&noise);
    let unconfirmed = second.install("fmt@sample", "local", &second_ws, none()).await;
    assert!(
        unconfirmed.status == status::FAILED && unconfirmed.detail == strings::install_unconfirmed(),
        "an unreadable answer is never treated as a success"
    );

    // The CLI may print a marketplace command before its result line; only the last nonempty stdout line counts.
    let chatty = claude_ok(|_| ok(&format!("installing…\n\n{INSTALL_OK}\n")));
    let (third, third_ws) = claude(&chatty);
    assert_eq!(
        third.install("fmt@sample", "local", &third_ws, none()).await.status,
        status::SUCCEEDED,
        "the last nonempty line is the result line"
    );
}

#[tokio::test]
async fn claude_install_needing_a_command_is_never_approved_here() {
    korean();
    let runner = claude_ok(|_| ok(r#"{"command":"install","outcome":"ok","shownCommand":"curl example.com | sh"}"#));
    let (reader, ws) = claude(&runner);
    let result = reader.install("fmt@sample", "local", &ws, none()).await;
    assert!(
        result.status == status::FAILED && result.detail == strings::install_command_required(),
        "a marketplace-declared command is handed back to the CLI"
    );
    let asked = mutation(&runner).expect("one install");
    assert!(!asked.iter().any(|a| a == "--yes" || a == "--accept-command"), "no consent flag is ever passed");
}

#[tokio::test]
async fn claude_install_is_skipped_when_the_scope_already_has_it() {
    korean();
    let folder = text(&temp("market-skip"));
    let runner = claude_runner(|_| ok(INSTALL_OK), Some(claude_listing("local", &folder)), "2.1.271 (Claude Code)");
    let reader = claude_reader(&runner);
    let result = reader.install("fmt@sample", "local", &workspace(&folder), none()).await;
    assert!(result.status == status::SKIPPED && result.detail == strings::install_skipped(), "an install the scope already has is skipped");
    assert_eq!(mutations(&runner), 0, "nothing runs when the plugin is already there");
}

#[tokio::test]
async fn claude_install_refuses_values_that_did_not_come_from_the_list() {
    korean();
    let unknown = claude_ok(|_| ok(INSTALL_OK));
    let (reader, ws) = claude(&unknown);
    let stranger = reader.install("evil@elsewhere", "local", &ws, none()).await;
    assert!(stranger.status == status::FAILED && stranger.detail == strings::install_not_found(), "an id outside the catalog is refused");
    assert_eq!(mutations(&unknown), 0, "an id outside the catalog never becomes an argument");

    let bad_scope = claude_ok(|_| ok(INSTALL_OK));
    let (second, second_ws) = claude(&bad_scope);
    let scoped = second.install("fmt@sample", "--json", &second_ws, none()).await;
    assert!(
        scoped.status == status::FAILED && scoped.detail == strings::install_bad_id_or_scope(),
        "a scope outside the three macOS scopes is refused"
    );
    let injected = second.install("fmt@sample; rm -rf /", "local", &second_ws, none()).await;
    assert!(
        injected.status == status::FAILED && injected.detail == strings::install_bad_id_or_scope(),
        "an id that is not name@marketplace is refused"
    );
    assert!(bad_scope.calls().is_empty(), "a refused argument starts no process at all");

    let refresh = claude_ok(|_| ok(""));
    let (third, third_ws) = claude(&refresh);
    let unregistered = third.refresh_marketplace("nowhere", &third_ws, none()).await;
    assert!(
        unregistered.status == status::FAILED && unregistered.detail == strings::marketplace_not_registered(),
        "an unregistered marketplace is refused"
    );
    assert_eq!(mutations(&refresh), 0, "an unregistered marketplace never becomes an argument");
    let bad_name = third.refresh_marketplace("sample --json", &third_ws, none()).await;
    assert!(
        bad_name.status == status::FAILED && bad_name.detail == strings::marketplace_bad_name(),
        "a marketplace name that is not an identifier"
    );
}

#[tokio::test]
async fn claude_install_reports_an_unsupported_cli_version() {
    korean();
    let runner = claude_runner(|_| ok(INSTALL_OK), None, "2.1.267 (Claude Code)");
    let (reader, ws) = claude(&runner);
    let result = reader.install("fmt@sample", "local", &ws, none()).await;
    assert_eq!(result.status, status::FAILED, "an old CLI cannot install");
    assert_eq!(result.detail, strings::detail_unsupported(), "the unsupported sentence is the macOS one");
    assert_eq!(mutations(&runner), 0, "an old CLI is never asked to install");
}

#[tokio::test(flavor = "multi_thread")]
async fn claude_install_is_cancellable() {
    korean();
    let gate = Signal::new();
    let runner = claude_ok(|_| ok(INSTALL_OK));
    gated_claude(&runner, &gate, false, Arc::new(Mutex::new(VecDeque::new())), claude_listing("", ""));
    let (reader, ws) = claude(&runner);
    let cancel = CancellationToken::new();
    let running = tokio::spawn(reader.install("fmt@sample", "local", &ws, cancel.clone()));
    until(|| mutations(&runner) > 0, 5000, "the install started").await;
    cancel.cancel();
    let result = running.await.unwrap();
    assert_eq!(result.status, status::CANCELLED, "a cancelled install reports cancelled");
    assert_eq!(result.detail, strings::operation_cancelled(), "the cancel sentence is the macOS one");
    gate.set();
}

#[tokio::test(flavor = "multi_thread")]
async fn claude_runs_one_operation_at_a_time() {
    korean();
    let gate = Signal::new();
    let runner = claude_ok(|_| ok(INSTALL_OK));
    gated_claude(&runner, &gate, false, Arc::new(Mutex::new(VecDeque::new())), claude_listing("", ""));
    let (reader, ws) = claude(&runner);
    let first = tokio::spawn(reader.install("fmt@sample", "local", &ws, none()));
    until(|| mutations(&runner) > 0, 5000, "the first install started").await;

    let second = reader.install("fmt@sample", "user", &ws, none()).await;
    assert_eq!(second.status, status::BUSY, "a second operation is refused while one runs");
    assert_eq!(second.detail, strings::operation_busy(), "the busy sentence is the macOS one");
    assert_eq!(mutations(&runner), 1, "the refused operation started no process");

    gate.set();
    assert_eq!(first.await.unwrap().status, status::SUCCEEDED, "the first operation still finishes");
    let third = reader.install("fmt@sample", "local", &ws, none()).await;
    assert_eq!(third.status, status::SUCCEEDED, "the next operation is admitted once the first ended");
}

// ---- Claude marketplace refresh --------------------------------------------------------------------------------------------

#[tokio::test]
async fn claude_marketplace_refresh_runs_the_cli_command() {
    korean();
    let runner = claude_ok(|_| ok("updated"));
    let (reader, ws) = claude(&runner);
    let result = reader.refresh_marketplace("sample", &ws, none()).await;
    assert_eq!(result.status, status::SUCCEEDED, "a clean exit succeeds");
    assert_eq!(result.detail, strings::marketplace_refresh_succeeded(), "the success sentence is the macOS one");
    assert_eq!(
        mutation(&runner),
        Some(strings(&["plugin", "marketplace", "update", "sample"])),
        "the refresh argument list is the macOS one"
    );

    let broken = claude_ok(|_| failed("network down"));
    let (second, second_ws) = claude(&broken);
    let failure = second.refresh_marketplace("sample", &second_ws, none()).await;
    assert!(
        failure.status == status::FAILED && failure.detail == strings::marketplace_refresh_failed(),
        "a failing refresh reports the macOS sentence"
    );
    assert!(failure.output.contains("network down"), "the bounded CLI output is kept for the disclosure");
}

// ---- Codex ------------------------------------------------------------------------------------------------------------------

#[tokio::test]
async fn codex_install_runs_add_at_user_level_and_verifies_the_result() {
    korean();
    let runner = codex_runner(
        |_| ok(r#"{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","installedPath":"/tmp/fmt"}"#),
        vec![codex_listing(false), codex_listing(true)],
        CODEX_MARKETS,
    );
    let (reader, ws) = codex(&runner);
    let result = reader.install("fmt@sample", "user", &ws, none()).await;
    assert_eq!(result.status, status::SUCCEEDED, "a verified Codex install succeeds: {}", result.detail);
    assert_eq!(result.detail, codex_strings::install_succeeded(), "the Codex success sentence is the macOS one");
    assert_eq!(
        mutation(&runner),
        Some(strings(&["plugin", "add", "fmt@sample", "--json"])),
        "the Codex install argument list is the macOS one"
    );
}

#[tokio::test]
async fn codex_install_refuses_another_scope_and_an_id_not_from_the_list() {
    korean();
    let runner = codex_runner(|_| ok("{}"), Vec::new(), CODEX_MARKETS);
    let (reader, ws) = codex(&runner);
    let scoped = reader.install("fmt@sample", "local", &ws, none()).await;
    assert!(scoped.status == status::FAILED && scoped.detail == strings::install_bad_id_or_scope(), "Codex installs at user level only");
    let stranger = reader.install("evil@elsewhere", "user", &ws, none()).await;
    assert!(
        stranger.status == status::FAILED && stranger.detail == codex_strings::install_not_found(),
        "an id outside the policy-filtered catalog"
    );
    assert_eq!(mutations(&runner), 0, "neither refusal became an argument");

    let already = codex_runner(|_| ok("{}"), vec![codex_listing(true)], CODEX_MARKETS);
    let (second, second_ws) = codex(&already);
    let skipped = second.install("fmt@sample", "user", &second_ws, none()).await;
    assert!(
        skipped.status == status::SKIPPED && skipped.detail == codex_strings::install_skipped(),
        "an already installed Codex plugin is skipped"
    );
}

#[tokio::test]
async fn codex_install_reports_an_unverifiable_result() {
    korean();
    let mismatched = codex_runner(
        |_| ok(r#"{"pluginId":"other@sample","name":"other","marketplaceName":"sample","installedPath":"/tmp/x"}"#),
        Vec::new(),
        CODEX_MARKETS,
    );
    let (reader, ws) = codex(&mismatched);
    let wrong = reader.install("fmt@sample", "user", &ws, none()).await;
    assert!(wrong.status == status::FAILED && wrong.detail == strings::install_unconfirmed(), "a result naming another plugin is refused");

    // The CLI answered for the right plugin, but the list still does not show it.
    let unverified = codex_runner(
        |_| ok(r#"{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","installedPath":"/tmp/fmt"}"#),
        Vec::new(),
        CODEX_MARKETS,
    );
    let (second, second_ws) = codex(&unverified);
    let result = second.install("fmt@sample", "user", &second_ws, none()).await;
    assert!(
        result.status == status::FAILED && result.detail == codex_strings::install_verify_failed(),
        "an install the list does not confirm"
    );

    let broken = codex_runner(|_| failed("policy denied"), Vec::new(), CODEX_MARKETS);
    let (third, third_ws) = codex(&broken);
    let failure = third.install("fmt@sample", "user", &third_ws, none()).await;
    assert!(
        failure.status == status::FAILED && failure.detail == codex_strings::operation_failed(),
        "a failing Codex run reports the macOS sentence"
    );
}

#[tokio::test]
async fn codex_marketplace_upgrade_runs_only_for_a_registered_git_source() {
    korean();
    let runner = codex_runner(
        |_| ok(r#"{"selectedMarketplaces":["sample"],"upgradedRoots":["/tmp/sample"],"errors":[]}"#),
        Vec::new(),
        CODEX_MARKETS,
    );
    let (reader, ws) = codex(&runner);
    let result = reader.refresh_marketplace("sample", &ws, none()).await;
    assert!(
        result.status == status::SUCCEEDED && result.detail == strings::marketplace_refresh_succeeded(),
        "a Git marketplace upgrade succeeds"
    );
    assert_eq!(
        mutation(&runner),
        Some(strings(&["plugin", "marketplace", "upgrade", "sample", "--json"])),
        "the Codex refresh argument list"
    );

    let local = codex_runner(|_| ok("{}"), Vec::new(), CODEX_LOCAL_MARKETS);
    let (second, second_ws) = codex(&local);
    let skipped = second.refresh_marketplace("sample", &second_ws, none()).await;
    assert!(
        skipped.status == status::SKIPPED && skipped.detail == codex_strings::marketplace_not_git(),
        "a non-Git source cannot be upgraded"
    );
    assert_eq!(mutations(&local), 0, "a non-Git source never becomes an argument");

    let unknown = codex_runner(|_| ok("{}"), Vec::new(), CODEX_MARKETS);
    let (third, third_ws) = codex(&unknown);
    let unregistered = third.refresh_marketplace("nowhere", &third_ws, none()).await;
    assert!(
        unregistered.status == status::FAILED && unregistered.detail == strings::marketplace_not_registered(),
        "an unregistered marketplace"
    );

    let noisy = codex_runner(|_| ok(r#"{"selectedMarketplaces":["other"],"upgradedRoots":[],"errors":[]}"#), Vec::new(), CODEX_MARKETS);
    let (fourth, fourth_ws) = codex(&noisy);
    let unconfirmed = fourth.refresh_marketplace("sample", &fourth_ws, none()).await;
    assert!(
        unconfirmed.status == status::FAILED && unconfirmed.detail == codex_strings::marketplace_refresh_unconfirmed(),
        "an answer about another marketplace is refused"
    );
}

// ---- The window --------------------------------------------------------------------------------------------------------------

#[test]
fn window_offers_the_macos_scope_choice_and_controls() {
    korean();
    let claude = ClaudePluginBrowser::new("claude", workspace(&text(&temp("market-window"))));
    assert_eq!(claude.scope(), "local", "Claude starts on the local scope");
    assert_eq!(
        claude.scope_options().into_iter().map(|o| o.label).collect::<Vec<_>>(),
        vec![strings::scope_local_option(), strings::scope_project_option(), strings::scope_user_option()],
        "the three Claude scopes carry the macOS labels"
    );
    assert_eq!(claude.scope_note(), strings::scope_note_local(), "the local scope explains itself");
    claude.set_scope("user");
    assert_eq!(claude.scope_note(), strings::scope_note_user(), "the user scope explains itself");

    let codex = ClaudePluginBrowser::new(ClaudePluginBrowser::CODEX_PROVIDER, workspace(&text(&temp("market-window-codex"))));
    let options = codex.scope_options();
    assert!(
        codex.scope() == "user" && options.len() == 1 && options[0].label == strings::scope_user_option(),
        "Codex offers the user scope only"
    );
    assert_eq!(codex.refresh_unavailable_note(), Some(codex_strings::refresh_git_only()), "Codex says only Git sources can be refreshed");
    assert_eq!(claude.refresh_unavailable_note(), None, "Claude carries no Git-only sentence");

    assert!(
        !claude.is_busy() && !claude.is_mutating() && claude.can_close() && !claude.can_cancel() && claude.progress_label().is_none(),
        "an idle window is closable and shows no progress"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn window_install_shows_progress_cancel_result_and_reloads() {
    korean();
    let folder = text(&temp("market-install"));
    // Three plugin-list reads: the snapshot, the operation's re-read before the mutation, and the reload after it.
    let listings = Arc::new(Mutex::new(VecDeque::from([claude_listing("", ""), claude_listing("", ""), claude_listing("local", &folder)])));
    let gate = Signal::new();
    let runner = FakeRunner::new(|_, _| ok(""));
    gated_claude(&runner, &gate, false, listings, claude_listing("local", &folder));
    let reader = dyn_reader(&claude_reader(&runner));

    let browser = Arc::new(ClaudePluginBrowser::new("claude", workspace(&folder)));
    browser.begin_load();
    browser.apply(reader.snapshot(&workspace(&folder), none()).await);
    assert!(browser.is_ready() && browser.can_install("fmt@sample"), "the catalog row offers 설치");
    assert_eq!(browser.install_button_label("fmt@sample"), strings::button_install(), "the button reads 설치");

    let running = {
        let (browser, reader) = (browser.clone(), reader.clone());
        tokio::spawn(async move { browser.install(&reader, "fmt@sample", &CancellationToken::new()).await })
    };
    until(|| mutations(&runner) > 0, 5000, "the install started").await;
    assert!(
        browser.is_mutating() && browser.progress_label() == Some(strings::progress_installing()),
        "the window shows the install progress line"
    );
    assert!(!browser.can_close(), "닫기 is disabled while the install runs");
    assert!(browser.can_cancel() && browser.cancel_label() == strings::button_cancel_operation(), "the cancel button is offered");
    assert_eq!(browser.install_button_label("fmt@sample"), strings::button_installing(), "the pressed row reads 설치 중…");

    gate.set();
    let result = running.await.unwrap();
    assert_eq!(result.status, status::SUCCEEDED, "the install succeeded: {}", result.detail);
    assert_eq!(browser.result_text(), Some(strings::install_succeeded()), "the result line is the macOS sentence");
    assert!(!browser.is_busy() && browser.can_close() && browser.progress_label().is_none(), "the window is idle again");
    assert!(browser.installed_in_selected_scope("fmt@sample"), "the list was reloaded after the install");
    assert!(
        browser.install_button_label("fmt@sample") == strings::tab_installed() && !browser.can_install("fmt@sample"),
        "an installed row no longer offers 설치"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn window_cancel_stops_the_operation_and_says_so() {
    korean();
    let folder = text(&temp("market-cancel"));
    let gate = Signal::new();
    let runner = FakeRunner::new(|_, _| ok(""));
    gated_claude(&runner, &gate, true, Arc::new(Mutex::new(VecDeque::new())), claude_listing("", ""));
    let reader = dyn_reader(&claude_reader(&runner));

    let browser = Arc::new(ClaudePluginBrowser::new("claude", workspace(&folder)));
    browser.apply(reader.snapshot(&workspace(&folder), none()).await);
    assert_eq!(browser.refreshable_marketplaces(), ["sample"], "the registered marketplace can be refreshed");
    assert!(browser.can_refresh_marketplaces(), "마켓플레이스 새로고침 is offered");

    let running = {
        let (browser, reader) = (browser.clone(), reader.clone());
        tokio::spawn(async move { browser.refresh_marketplaces(&reader, &CancellationToken::new()).await })
    };
    until(|| mutations(&runner) > 0, 5000, "the refresh started").await;
    assert_eq!(browser.progress_label(), Some(strings::progress_refreshing()), "the refresh progress line is shown");
    browser.request_cancel();
    let result = running.await.unwrap();
    gate.set();

    assert_eq!(result.status, status::CANCELLED, "the refresh was cancelled");
    assert_eq!(
        browser.result_text(),
        Some(strings::operation_cancelled_by_user()),
        "a cancel the user asked for says the list is read again"
    );
    assert!(browser.is_ready() && !browser.is_busy() && browser.can_close(), "the window is idle and the list was read again");
}

#[tokio::test]
async fn window_refuses_a_change_it_cannot_start() {
    korean();
    let reader: Arc<dyn PluginReader> = Arc::new(ClaudePluginReader::with_probes(
        FakeRunner::new(|_, _| ok("")),
        env(&[&temp("market-refuse-bin")]),
        Arc::new(|_: &str| false),
        Arc::new(|path: &str| Path::new(path).is_dir()),
    ));

    let cold = ClaudePluginBrowser::new("claude", workspace(&text(&temp("market-cold"))));
    let early = cold.install(&reader, "fmt@sample", &none()).await;
    assert!(early.status == status::FAILED && early.detail == strings::load_list_first(), "a window with no list asks for the list first");

    let empty = ClaudePluginBrowser::new("claude", workspace(&text(&temp("market-empty"))));
    empty.apply(ClaudePluginSnapshot::of(status::READY, strings::detail_no_marketplaces()));
    let nothing = empty.refresh_marketplaces(&reader, &none()).await;
    assert!(nothing.status == status::SKIPPED && nothing.detail == strings::no_refreshable_marketplaces(), "nothing to refresh says so");
    assert!(!empty.can_refresh_marketplaces(), "마켓플레이스 새로고침 is disabled when nothing can be refreshed");

    let ready = ClaudePluginBrowser::new("claude", workspace(&text(&temp("market-ready"))));
    ready.apply(ClaudePluginSnapshot {
        status: status::READY.into(),
        available: vec![ClaudeCatalogPlugin {
            id: "fmt@sample".into(),
            name: "fmt".into(),
            marketplace: "sample".into(),
            ..Default::default()
        }],
        marketplaces: vec![ClaudePluginMarketplace::new("sample", "github")],
        ..ClaudePluginSnapshot::default()
    });
    let stranger = ready.install(&reader, "evil@elsewhere", &none()).await;
    assert!(
        stranger.status == status::FAILED && stranger.detail == strings::select_plugin_and_scope_again(),
        "the window refuses an id it never drew"
    );
}

/// A gateway with exactly the macOS reads and mutations: were a method added to `PluginReader`, this would stop compiling.
struct Gateway;

impl PluginReader for Gateway {
    fn snapshot(&self, _: &Workspace, _: CancellationToken) -> BoxFuture<'static, Arc<ClaudePluginSnapshot>> {
        Box::pin(async { Arc::new(ClaudePluginSnapshot::default()) })
    }
    fn shutdown(&self) {}
    fn install(&self, _: &str, _: &str, _: &Workspace, _: CancellationToken) -> BoxFuture<'static, ClaudePluginOperationResult> {
        Box::pin(async { ClaudePluginOperationResult::new(status::FAILED, "") })
    }
    fn refresh_marketplace(&self, _: &str, _: &Workspace, _: CancellationToken) -> BoxFuture<'static, ClaudePluginOperationResult> {
        Box::pin(async { ClaudePluginOperationResult::new(status::FAILED, "") })
    }
}

/// The copy of both mutations, against the macOS literals it mirrors.
#[test]
fn copy_matches_macos() {
    korean();
    assert_eq!(strings::scope_local_option(), "로컬 · 이 워크스페이스, 나만", "the local scope label");
    assert_eq!(strings::scope_project_option(), "프로젝트 · 팀과 공유", "the project scope label");
    assert_eq!(strings::scope_user_option(), "사용자 · 모든 프로젝트", "the user scope label");
    assert_eq!(strings::button_install(), "설치", "the install button");
    assert_eq!(strings::button_marketplace_refresh(), "마켓플레이스 새로고침", "the refresh button");
    assert_eq!(strings::button_cancel_operation(), "작업 취소", "the cancel button");
    assert!(strings::scope_note_user().contains("이 PC의") && !strings::scope_note_user().contains("Mac"), "the user scope names the PC");

    // Every sentence a mutation can report is a constant of one of the two classes, so no result line is composed at the screen.
    let known: HashSet<String> = strings::all().into_iter().chain(codex_strings::all()).map(|(_, value)| value).collect();
    for sentence in [
        strings::operation_busy(),
        strings::operation_cancelled(),
        strings::operation_cancelled_by_user(),
        strings::install_succeeded(),
        strings::install_skipped(),
        strings::install_failed(),
        strings::install_unconfirmed(),
        strings::install_not_found(),
        strings::install_bad_id_or_scope(),
        strings::install_command_required(),
        strings::marketplace_bad_name(),
        strings::marketplace_not_registered(),
        strings::marketplace_refresh_failed(),
        strings::marketplace_refresh_succeeded(),
        codex_strings::install_succeeded(),
        codex_strings::install_skipped(),
        codex_strings::install_not_found(),
        codex_strings::install_verify_failed(),
        codex_strings::operation_failed(),
        codex_strings::marketplace_not_git(),
        codex_strings::marketplace_refresh_unconfirmed(),
    ] {
        assert!(known.contains(&sentence), "a result sentence must be a Core constant: {sentence}");
    }
    // The two mutations macOS has and no invented third one: `Gateway` implements the whole trait with exactly these four.
    let gateway: Arc<dyn PluginReader> = Arc::new(Gateway);
    gateway.shutdown();
}

/// The Core half of `WindowIsWiredIntoTheRunningAppAndTheSmokeRun`: the smoke's fake reader installs, holds until the window's own
/// cancel, and refreshes without any CLI, over the smoke's fixture snapshots, under the key pluginMarketplace.
#[tokio::test(flavor = "multi_thread")]
async fn the_smoke_drives_the_window_through_a_fake_reader() {
    korean();
    assert_eq!(PluginMarketplaceSmokeOutcome::RESULT_KEY, "pluginMarketplace", "the smoke key is pluginMarketplace");
    let folder = workspace(&text(&temp("market-smoke")));
    let reader: Arc<dyn PluginReader> = Arc::new(FakeMarketplaceReader::new(claude_snapshot(), false));
    let browser = ClaudePluginBrowser::new("claude", folder.clone());
    browser.apply(reader.snapshot(&folder, none()).await);
    browser.set_scope("user");
    let installed = browser.install(&reader, "docs@sample", &none()).await;
    assert_eq!(installed.detail, strings::install_succeeded(), "the install result");
    let refreshed = browser.refresh_marketplaces(&reader, &none()).await;
    assert_eq!(refreshed.status, status::SUCCEEDED, "the refresh result: {}", refreshed.detail);

    let holding: Arc<dyn PluginReader> = Arc::new(FakeMarketplaceReader::new(claude_snapshot(), true));
    let held = Arc::new(ClaudePluginBrowser::new("claude", folder.clone()));
    held.apply(holding.snapshot(&folder, none()).await);
    held.set_scope("user");
    let running = {
        let (held, holding) = (held.clone(), holding.clone());
        tokio::spawn(async move { held.install(&holding, "docs@sample", &CancellationToken::new()).await })
    };
    until(|| held.is_mutating(), 5000, "the held install runs").await;
    held.request_cancel();
    let cancelled = running.await.unwrap();
    assert!(cancelled.status == status::CANCELLED && held.result_text() == Some(strings::operation_cancelled_by_user()), "the cancel leg");

    let codex = ClaudePluginBrowser::new(ClaudePluginBrowser::CODEX_PROVIDER, folder.clone());
    codex.apply(codex_snapshot());
    assert!(codex.scope_options().len() == 1 && browser.scope_options().len() == 3, "the scope pickers the outcome counts");
    assert_eq!(codex.installed_rows().len(), 1, "the Codex fixture's project row is never drawn");
}

/// The Core half of `TheRunningAppOwnsTheOperationsObject`: the app's instance speaks to the installed CLI through the shared
/// runner over the platform's processes (making it starts nothing); a check's instance carries its fake runner; one operation at a
/// time for the whole app, refused with the macOS sentence and never a second CLI run.
#[tokio::test(flavor = "multi_thread")]
async fn the_running_app_owns_the_operations_object() {
    korean();
    let host = Arc::new(FakeProcessHost::default());
    let real = PluginOperations::installed(host.clone(), Arc::new(FakeShellEnvironment::new(env(&[&temp("market-real-bin")]))));
    assert!(real.uses_installed_cli(), "the app's own operations object speaks to the installed CLI");
    assert!(
        matches!(real.reader("claude"), ProviderReader::Claude(_)) && matches!(real.reader("codex"), ProviderReader::Codex(_)),
        "the provider picks the reader"
    );
    assert!(!real.is_running(), "a fresh operations object has nothing running");
    assert!(host.spawned().is_empty(), "making it starts no process");
    let folder = temp("market-real");
    let program = text(&folder.join("claude.exe"));
    let ran = real.runner().run(CliCall::new(&program, ["--version"], std::time::Duration::from_secs(5)), none()).await;
    assert!(ran.is_ok() && host.spawned().len() == 1, "its runner starts the CLI through the platform's processes");
    assert_eq!(host.spawned()[0].program, Path::new(&program), "the CLI it was asked for");

    let gate = Signal::new();
    let workspace_path = text(&temp("market-owns"));
    let runner = claude_ok(|_| ok(INSTALL_OK));
    gated_claude(&runner, &gate, false, Arc::new(Mutex::new(VecDeque::new())), claude_listing("", ""));
    let reader = dyn_reader(&claude_reader(&runner));
    let operations = Arc::new(PluginOperations::new(runner.clone()));
    let as_runner: Arc<dyn CliRunner> = runner.clone();
    assert!(!operations.uses_installed_cli() && Arc::ptr_eq(&operations.runner(), &as_runner), "a check's object carries the fake runner");

    let browser = Arc::new(ClaudePluginBrowser::new("claude", workspace(&workspace_path)));
    browser.apply(reader.snapshot(&workspace(&workspace_path), none()).await);
    assert!(browser.can_install("fmt@sample"), "the catalog row offers 설치");

    let running = {
        let (operations, browser, reader) = (operations.clone(), browser.clone(), reader.clone());
        tokio::spawn(async move { operations.install(&browser, &reader, "fmt@sample", &CancellationToken::new()).await })
    };
    until(|| mutations(&runner) > 0, 5000, "the install started").await;
    assert!(operations.is_running(), "the operations object knows its operation is running");

    let second = ClaudePluginBrowser::new("claude", workspace(&workspace_path));
    second.apply(reader.snapshot(&workspace(&workspace_path), none()).await);
    let refused = operations.refresh_marketplaces(&second, &reader, &none()).await;
    assert!(
        refused.status == status::BUSY && refused.detail == strings::operation_busy(),
        "a second operation is refused with the macOS sentence"
    );
    assert_eq!(mutations(&runner), 1, "the refused operation never became a second CLI run");

    gate.set();
    let result = running.await.unwrap();
    assert!(
        result.status == status::SUCCEEDED && result.detail == strings::install_succeeded(),
        "the install through the operations object"
    );
    assert!(!operations.is_running(), "the gate is open again once the operation ends");
    assert_eq!(
        mutation(&runner),
        Some(strings(&["plugin", "install", "fmt@sample", "--scope", "local", "--json"])),
        "the macOS argument list"
    );
}

/// C#'s `OperateAsync` sets `running` before any await: the window reserves the app-wide gate synchronously (under the state's lock)
/// and hands the operation to a task, so nothing — another window's change, an automatic update, a composer's start reading the
/// holds — slips in between. The reservation refuses every other operation until it ends or is dropped.
#[tokio::test]
async fn a_reserved_gate_refuses_every_other_operation_until_it_is_released() {
    korean();
    let runner = claude_ok(|_| ok(INSTALL_OK));
    let reader = dyn_reader(&claude_reader(&runner));
    let folder = text(&temp("market-reserve"));
    let operations = PluginOperations::new(runner.clone());
    let browser = ClaudePluginBrowser::new("claude", workspace(&folder));
    browser.apply(reader.snapshot(&workspace(&folder), none()).await);

    let reservation = operations.reserve().expect("the gate is free");
    assert!(operations.is_running(), "reserved, the gate reads running at once (the holds read it)");
    assert!(operations.reserve().is_none(), "a second reservation is refused");
    let refused = operations.install(&browser, &reader, "fmt@sample", &none()).await;
    assert!(refused.status == status::BUSY && refused.detail == strings::operation_busy(), "an operation is refused while it is held");
    let automatic = operations.update_installed("claude", &workspace(&folder), std::time::Duration::from_secs(1), &none(), None).await;
    assert_eq!(automatic.status, "busy", "an automatic update too");
    assert_eq!(mutations(&runner), 0, "nothing reached the CLI");

    let result = PluginOperations::install_reserved(reservation, &browser, &reader, "fmt@sample", &none()).await;
    assert_eq!(result.status, status::SUCCEEDED, "the reserved operation runs under its own reservation");
    assert!(!operations.is_running(), "and releases the gate when it ends");
    drop(operations.reserve().expect("free again"));
    assert!(!operations.is_running(), "a dropped reservation releases it too");
}
