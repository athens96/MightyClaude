//! C# CliUpdateVerification (Verification.cs:1137-1151, every check under its name), StringsVerification.CliUpdateStringsMatchMacOS
//! (:1152) and AcpAgentAccountVerification.OpencodeUpgradesItselfOpenCode1IsLeftToTheSetupScreenAndHermesUpdatesWithYes (:1401): the
//! CLI updater proven with a fake runner — no claude, codex, agy, npm or winget starts and nothing touches the network. Every
//! fixture is a private temp tree, so recognition runs against real paths without a real install.

mod common;

use std::collections::{BTreeMap, HashMap};
use std::path::Path;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use common::*;
use mighty_core::model::AppSnapshot;
use mighty_core::wire::PROVIDERS;
use mighty_updates::cli_update::coordinator::Updater;
use mighty_updates::cli_update::service::DIAGNOSTIC_CHARACTER_CAP;
use mighty_updates::cli_update::{CliUpdateCoordinator, CliUpdateResult, CliUpdateService, strings};
use mighty_updates::runner::{CancellationToken, CliRunResult, RunError};

fn none() -> CancellationToken {
    CancellationToken::new()
}

fn updates(runner: &FakeRunner) -> Vec<Vec<String>> {
    runner.args().into_iter().filter(|a| !a.iter().any(|x| x == "--version")).collect()
}

fn update_executables(runner: &FakeRunner) -> Vec<String> {
    runner.calls().into_iter().filter(|c| !c.arguments.iter().any(|x| x == "--version")).map(|c| c.executable).collect()
}

fn version_answer(value: &str) -> Result<CliRunResult, RunError> {
    ok(&format!("{value}\n"))
}

fn package(root: &Path, name: &str, version: &str, binary: &str) {
    std::fs::create_dir_all(root).unwrap();
    let json = serde_json::json!({ "name": name, "version": version, "bin": { binary: "cli.js" } });
    std::fs::write(root.join("package.json"), json.to_string()).unwrap();
}

fn make_service(runner: &Arc<FakeRunner>, environment: BTreeMap<String, String>, home: &Path) -> CliUpdateService {
    CliUpdateService::new(runner.clone(), environment, text(home))
}

/// Nothing installed: skipped, method missing, and not one command is run.
#[tokio::test]
async fn missing_cli_is_skipped() {
    korean();
    let home = temp("missing");
    let runner = FakeRunner::new(|_, _| version_answer("2.1.271"));
    let service = make_service(&runner, env(&[&dir(&home, &["empty"])]), &home);
    let result = service.update("claude", &none()).await;
    assert!(result.status == "skipped" && result.method == "missing", "missing CLI must be skipped: {result:?}");
    assert_eq!(result.detail, strings::detail_missing(), "missing detail must be the macOS sentence");
    assert!(runner.calls().is_empty(), "a missing CLI must not start any command");
    let installation = service.inspect("claude", &none()).await;
    assert!(
        installation.method == "missing" && !installation.can_update && installation.version.is_none(),
        "inspect must report the missing install"
    );
    let _ = std::fs::remove_dir_all(home);
}

/// A native Claude install is updated with Claude Code's own update command.
#[tokio::test]
async fn native_claude_uses_its_own_update_command() {
    korean();
    let home = temp("native");
    let versions = dir(&home, &[".local", "share", "claude", "versions", "2.1.270"]);
    let claude = exe(&versions, "claude");
    let version = Arc::new(Mutex::new("2.1.270".to_string()));
    let current = version.clone();
    let runner = FakeRunner::new(move |_, args| {
        if args.iter().any(|a| a == "--version") {
            return version_answer(&current.lock().unwrap());
        }
        *current.lock().unwrap() = "2.1.271".into();
        ok("updated\n")
    });
    let service = make_service(&runner, env(&[&versions]), &home);

    let installation = service.inspect("claude", &none()).await;
    assert!(
        installation.method == "native" && installation.can_update && installation.version.as_deref() == Some("2.1.270"),
        "native install must be recognised: {installation:?}"
    );
    assert_eq!(installation.detail, strings::detail_native_claude(), "native detail must be the macOS sentence");
    assert!(updates(&runner).is_empty(), "inspect must not run an installer");

    let result = service.update("claude", &none()).await;
    assert!(
        result.status == "updated"
            && result.before_version.as_deref() == Some("2.1.270")
            && result.after_version.as_deref() == Some("2.1.271")
            && result.method == "native",
        "native update must report both versions: {result:?}"
    );
    assert_eq!(result.detail, strings::detail_updated(), "updated detail must be the macOS sentence");
    assert_eq!(updates(&runner), vec![strings(&["update"])], "native update must be the CLI's own update command");
    assert_eq!(update_executables(&runner), vec![text(&claude)], "the CLI's own executable must run the update");
    assert_eq!(
        CliUpdateService::result_row(&result),
        format!("Claude · {}", strings::status_updated()),
        "the row must be {{provider}} · {{status}}"
    );
    assert_eq!(
        CliUpdateService::version_change(&result).as_deref(),
        Some("2.1.270 → 2.1.271"),
        "the version line must be {{before}} → {{after}}"
    );
    let _ = std::fs::remove_dir_all(home);
}

#[cfg(windows)]
fn link(target: &Path, link: &Path) -> bool {
    std::os::windows::fs::symlink_file(target, link).is_ok()
}

#[cfg(unix)]
fn link(target: &Path, link: &Path) -> bool {
    std::os::unix::fs::symlink(target, link).is_ok()
}

/// winget upgrades exactly the one package the install location names, with no --all, no fresh install and no interactive or
/// elevation prompt. (Where this account may not create a symbolic link, the package's own folder stands on PATH instead of
/// WinGet\Links: the package is still read from where the CLI really is.)
#[tokio::test]
async fn winget_upgrades_exactly_the_package_it_found() {
    korean();
    let home = temp("winget");
    let package = dir(&home, &["Local", "Microsoft", "WinGet", "Packages", "Anthropic.ClaudeCode_8wekyb3d8bbwe"]);
    let target = exe(&package, "claude");
    let links = dir(&home, &["Local", "Microsoft", "WinGet", "Links"]);
    let on_path = if link(&target, &links.join("claude")) { links } else { package.clone() };
    let tools = dir(&home, &["tools"]);
    let winget = exe(&tools, "winget");

    let runner = FakeRunner::new(|_, args| {
        if args.iter().any(|a| a == "--version") { version_answer("2.1.271") } else { ok("No applicable upgrade found.\n") }
    });
    let service = make_service(&runner, env(&[&on_path, &tools]), &home);

    let result = service.update("claude", &none()).await;
    assert!(
        result.status == "current"
            && result.method == "winget"
            && result.before_version.as_deref() == Some("2.1.271")
            && result.after_version.as_deref() == Some("2.1.271"),
        "an unchanged winget package must read current: {result:?}"
    );
    assert_eq!(result.detail, strings::detail_unchanged(), "current detail must be the macOS sentence");
    let update = updates(&runner).into_iter().next().expect("one update");
    assert_eq!(
        update,
        strings(&[
            "upgrade",
            "--id",
            "Anthropic.ClaudeCode",
            "--exact",
            "--silent",
            "--accept-source-agreements",
            "--accept-package-agreements",
            "--disable-interactivity"
        ]),
        "winget arguments changed"
    );
    assert!(!update.iter().any(|a| a == "--all" || a == "install"), "winget must never upgrade everything or install anything new");
    assert_eq!(update_executables(&runner), vec![text(&winget)], "winget itself must run the upgrade");
    assert_eq!(CliUpdateService::version_change(&result), None, "an unchanged version shows no version line");
    let _ = std::fs::remove_dir_all(home);
}

/// The npm plan updates only the official package, in the prefix it is already installed in, through Node and npm's own cli
/// script.
#[tokio::test]
async fn npm_updates_only_the_official_package_in_its_prefix() {
    korean();
    let home = temp("npm");
    let prefix = dir(&home, &["AppData", "Roaming", "npm"]);
    exe(&prefix, "codex.cmd");
    package(&prefix.join("node_modules").join("@openai").join("codex"), "@openai/codex", "0.51.0", "codex");
    let npm_script = dir(&prefix, &["node_modules", "npm", "bin"]).join("npm-cli.js");
    std::fs::write(&npm_script, "").unwrap();
    let node_directory = dir(&home, &["nodejs"]);
    let node = exe(&node_directory, "node.exe");

    let version = Arc::new(Mutex::new("0.51.0".to_string()));
    let current = version.clone();
    let runner = FakeRunner::new(move |_, args| {
        if args.iter().any(|a| a == "--version") {
            return version_answer(&current.lock().unwrap());
        }
        *current.lock().unwrap() = "0.52.0".into();
        ok("added 1 package\n")
    });
    let service = make_service(&runner, env(&[&prefix, &node_directory]), &home);

    let installation = service.inspect("codex", &none()).await;
    assert!(installation.method == "npm" && installation.can_update, "npm install must be recognised: {installation:?}");
    assert_eq!(installation.detail, strings::detail_npm_plan(), "npm detail must be the macOS sentence");

    let result = service.update("codex", &none()).await;
    assert!(
        result.status == "updated"
            && result.method == "npm"
            && result.before_version.as_deref() == Some("0.51.0")
            && result.after_version.as_deref() == Some("0.52.0"),
        "npm update must report both versions: {result:?}"
    );
    let update = updates(&runner).into_iter().next().expect("one update");
    assert_eq!(
        update,
        vec![
            text(&npm_script),
            "install".into(),
            "--global".into(),
            "--prefix".into(),
            text(&prefix),
            "@openai/codex@latest".into(),
            "--no-audit".into(),
            "--no-fund".into()
        ],
        "npm arguments changed"
    );
    assert_eq!(update_executables(&runner), vec![text(&node)], "npm must run through Node, never a shell");
    assert_eq!(update.iter().filter(|a| a.ends_with("@latest")).count(), 1, "exactly one package may be named");
    let _ = std::fs::remove_dir_all(home);
}

/// A prerelease npm channel is reported as skipped with the macOS sentence and is never changed.
#[tokio::test]
async fn prerelease_npm_channel_is_skipped() {
    korean();
    let home = temp("prerelease");
    let prefix = dir(&home, &["AppData", "Roaming", "npm"]);
    exe(&prefix, "codex.cmd");
    package(&prefix.join("node_modules").join("@openai").join("codex"), "@openai/codex", "0.9.0-nightly.20260919", "codex");
    std::fs::write(dir(&prefix, &["node_modules", "npm", "bin"]).join("npm-cli.js"), "").unwrap();
    let node_directory = dir(&home, &["nodejs"]);
    exe(&node_directory, "node.exe");

    let runner = FakeRunner::new(|_, _| version_answer("0.9.0-nightly.20260919"));
    let service = make_service(&runner, env(&[&prefix, &node_directory]), &home);
    let result = service.update("codex", &none()).await;
    assert!(result.status == "skipped" && result.method == "npm", "a prerelease channel must be skipped: {result:?}");
    assert_eq!(result.detail, strings::detail_npm_prerelease(), "the skipped sentence must be the macOS one");
    assert!(updates(&runner).is_empty(), "a prerelease channel must not be changed");
    let _ = std::fs::remove_dir_all(home);
}

/// An install method the app does not recognise is skipped, not guessed at.
#[tokio::test]
async fn unknown_install_method_is_skipped() {
    korean();
    let home = temp("unknown");
    let manual = dir(&home, &["tools"]);
    exe(&manual, "claude.exe");
    let runner = FakeRunner::new(|_, _| version_answer("2.1.271"));
    let service = make_service(&runner, env(&[&manual]), &home);
    let result = service.update("claude", &none()).await;
    assert!(
        result.status == "skipped" && result.method == "unknown" && result.before_version.as_deref() == Some("2.1.271"),
        "an unknown method must be skipped: {result:?}"
    );
    assert_eq!(result.detail, strings::detail_unknown_method(), "the unknown-method sentence must be the macOS one");
    assert!(updates(&runner).is_empty(), "an unrecognised install must not be touched");
    let _ = std::fs::remove_dir_all(home);
}

/// A failing installer is reported as failed with the exit code, and its diagnostic output is bounded and control-character free.
#[tokio::test]
async fn failing_installer_is_reported_with_bounded_output() {
    korean();
    let home = temp("failing");
    let versions = dir(&home, &[".local", "share", "claude", "versions", "2.1.270"]);
    exe(&versions, "claude");
    let noisy = format!("{}\u{7}tail", "x".repeat(20000));
    let runner = FakeRunner::new(move |_, args| {
        if args.iter().any(|a| a == "--version") {
            version_answer("2.1.270")
        } else {
            Ok(CliRunResult::new(3, noisy.clone(), "권한이 없습니다.", false))
        }
    });
    let service = make_service(&runner, env(&[&versions]), &home);

    let result = service.update("claude", &none()).await;
    assert!(
        result.status == "failed" && result.method == "native" && result.after_version.is_none(),
        "a failing installer must read failed: {result:?}"
    );
    assert_eq!(
        result.detail,
        strings::detail_failed_exit_template().replace("{code}", "3"),
        "the failure sentence must carry the exit code"
    );
    assert_eq!(result.output.encode_utf16().count(), DIAGNOSTIC_CHARACTER_CAP, "diagnostic output must be bounded");
    assert!(
        !result.output.chars().any(|c| c.is_control() && !matches!(c, '\n' | '\t')),
        "control characters must not reach the details view"
    );
    assert!(result.output.ends_with("tail\n권한이 없습니다."), "the tail of the output must be kept");
    let _ = std::fs::remove_dir_all(home);
}

/// One update at a time: a second request reports busy, cancel stops the running installer, and closing the app refuses new work.
#[tokio::test(flavor = "multi_thread")]
async fn second_request_is_busy_and_cancel_stops_the_run() {
    korean();
    let home = temp("busy");
    let versions = dir(&home, &[".local", "share", "claude", "versions", "2.1.270"]);
    exe(&versions, "claude");
    let (started, gate) = (Signal::new(), Signal::new());
    let runner = FakeRunner::new(|_, _| ok(""));
    let (started_in, gate_in) = (started.clone(), gate.clone());
    runner.set_async(move |call, token| {
        let (started, gate) = (started_in.clone(), gate_in.clone());
        Box::pin(async move {
            if call.arguments.iter().any(|a| a == "--version") {
                return ok("2.1.270\n");
            }
            started.set();
            gate.wait_or_cancel(&token).await?;
            ok("")
        })
    });
    let service = Arc::new(make_service(&runner, env(&[&versions]), &home));

    let running = {
        let service = service.clone();
        tokio::spawn(async move { service.update("claude", &CancellationToken::new()).await })
    };
    tokio::time::timeout(std::time::Duration::from_secs(10), started.wait()).await.expect("the installer started");
    let busy = service.update("codex", &none()).await;
    assert!(busy.status == "busy" && busy.provider == "codex", "a second request must report busy: {busy:?}");
    assert_eq!(busy.detail, strings::detail_busy(), "the busy sentence must be the macOS one");

    service.cancel().await;
    let cancelled = running.await.unwrap();
    assert!(cancelled.status == "cancelled" && cancelled.method == "native", "cancel must stop the running installer: {cancelled:?}");
    assert_eq!(cancelled.detail, strings::detail_cancelled(), "the cancelled sentence must be the macOS one");

    service.shutdown().await;
    let after_shutdown = service.update("claude", &none()).await;
    assert!(
        after_shutdown.status == "cancelled" && after_shutdown.detail == strings::detail_closing(),
        "closing the app must refuse new updates"
    );
    gate.set();
    let _ = std::fs::remove_dir_all(home);
}

/// The start-up pass covers every CLI and runs only when the saved switch is a JSON true.
#[tokio::test]
async fn startup_pass_covers_every_provider_only_when_the_switch_is_on() {
    korean();
    assert!(!CliUpdateService::should_run_at_startup(&AppSnapshot::default()), "the default must not update at start");
    assert!(
        !CliUpdateService::should_run_at_startup(&AppSnapshot { auto_update_clis: Some(false), ..AppSnapshot::default() }),
        "an explicit off must not update at start"
    );
    assert!(
        CliUpdateService::should_run_at_startup(&AppSnapshot { auto_update_clis: Some(true), ..AppSnapshot::default() }),
        "an explicit on must update at start"
    );

    let home = temp("startup");
    let runner = FakeRunner::new(|_, _| version_answer("2.1.271"));
    let service = make_service(&runner, env(&[&dir(&home, &["empty"])]), &home);
    let results = service.update_all(&none()).await;
    assert_eq!(
        results.iter().map(|r| r.provider.as_str()).collect::<Vec<_>>(),
        PROVIDERS,
        "the start-up pass must cover every provider in order"
    );
    assert!(results.iter().all(|r| r.status == "skipped"), "nothing installed means every provider is skipped");
    assert!(runner.calls().is_empty(), "the start-up pass must not start anything when no CLI is installed");
    let _ = std::fs::remove_dir_all(home);
}

// ── CliUpdateCoordinator ────────────────────────────────────────────────────────────────────────────────────────────────

fn updated(provider: &str) -> CliUpdateResult {
    CliUpdateResult::new(provider, "updated")
        .with_versions(Some("1.0"), Some("2.0"))
        .with_method("native")
        .with_detail(strings::detail_updated())
}

fn updater(
    script: impl Fn(String, CancellationToken) -> mighty_platform::BoxFuture<'static, Option<CliUpdateResult>> + Send + Sync + 'static,
) -> Updater {
    Arc::new(script)
}

fn fake_coordinator() -> Arc<CliUpdateCoordinator> {
    CliUpdateCoordinator::new(updater(|provider, _| Box::pin(async move { Some(updated(&provider)) })))
}

/// The coordinator runs every provider in order, tells its listeners after each result, and keeps isUpdating and finishedAt right.
#[tokio::test(flavor = "multi_thread")]
async fn coordinator_tracks_state_and_runs_in_order() {
    korean();
    let coordinator = fake_coordinator();
    let changes = Arc::new(Mutex::new(Vec::<(bool, usize)>::new()));
    {
        let (changes, watched) = (changes.clone(), coordinator.clone());
        coordinator.on_state_changed(move || changes.lock().unwrap().push((watched.is_updating(), watched.results().len())));
    }
    assert!(!coordinator.is_updating(), "must not be updating before Start");
    assert!(coordinator.finished_at().is_none(), "finishedAt must be null before any run");

    assert!(coordinator.start(), "Start must return true when idle");
    assert!(coordinator.is_updating(), "must be updating immediately after Start");
    until(|| !coordinator.is_updating(), 5000, "the run ends").await;

    let results = coordinator.results();
    assert_eq!(results.len(), PROVIDERS.len(), "coordinator must have one result per provider");
    assert_eq!(results.iter().map(|r| r.provider.as_str()).collect::<Vec<_>>(), PROVIDERS, "providers must appear in Wire.Providers order");
    assert!(results.iter().all(|r| r.status == "updated"), "all results must be updated");
    assert!(coordinator.finished_at().is_some(), "finishedAt must be set after the run");
    let changes = changes.lock().unwrap().clone();
    assert!(changes.len() >= 4, "StateChanged must fire at start and after each provider plus at end");
    assert_eq!(changes[0], (true, 0), "first StateChanged must show isUpdating=true");
    assert!(!changes.last().unwrap().0, "last StateChanged must show isUpdating=false");
}

/// A second Start while running returns false; the first run still completes.
#[tokio::test(flavor = "multi_thread")]
async fn coordinator_refuses_second_start_while_running() {
    korean();
    let gate = Signal::new();
    let held = gate.clone();
    let coordinator = CliUpdateCoordinator::new(updater(move |provider, token| {
        let gate = held.clone();
        Box::pin(async move {
            if provider == PROVIDERS[0] {
                gate.wait_or_cancel(&token).await.ok()?;
            }
            Some(CliUpdateResult::new(&provider, "updated"))
        })
    }));
    assert!(coordinator.start(), "first Start must succeed");
    assert!(coordinator.is_updating(), "must be updating after first Start");
    assert!(!coordinator.start(), "second Start while running must return false");
    gate.set();
    until(|| !coordinator.is_updating(), 5000, "the run ends").await;
    assert_eq!(coordinator.results().len(), PROVIDERS.len(), "first run must complete all providers");
}

/// BeginAutomaticIfNeeded starts a run only when AutoUpdateCLIs is true, and only once however often it is called.
#[tokio::test(flavor = "multi_thread")]
async fn coordinator_begins_automatic_only_once_and_only_when_switch_is_on() {
    korean();
    let calls = Arc::new(AtomicUsize::new(0));
    let counted = calls.clone();
    let coordinator = CliUpdateCoordinator::new(updater(move |provider, _| {
        counted.fetch_add(1, Ordering::SeqCst);
        Box::pin(async move { Some(CliUpdateResult::new(&provider, "updated")) })
    }));
    coordinator.begin_automatic_if_needed(&AppSnapshot::default());
    coordinator.begin_automatic_if_needed(&AppSnapshot { auto_update_clis: Some(false), ..AppSnapshot::default() });
    assert!(!coordinator.is_updating(), "BeginAutomaticIfNeeded must not start when the switch is off or default");

    coordinator.begin_automatic_if_needed(&AppSnapshot { auto_update_clis: Some(true), ..AppSnapshot::default() });
    assert!(coordinator.is_updating(), "BeginAutomaticIfNeeded must start when the switch is on");
    until(|| !coordinator.is_updating(), 5000, "the run ends").await;
    let after_first = calls.load(Ordering::SeqCst);
    assert_eq!(after_first, PROVIDERS.len(), "the automatic run must cover all providers");

    coordinator.begin_automatic_if_needed(&AppSnapshot { auto_update_clis: Some(true), ..AppSnapshot::default() });
    tokio::time::sleep(std::time::Duration::from_millis(50)).await;
    assert_eq!(calls.load(Ordering::SeqCst), after_first, "BeginAutomaticIfNeeded must not start a second automatic run");
}

/// CancelAsync stops the running update, and ShutdownAsync refuses any later Start.
#[tokio::test(flavor = "multi_thread")]
async fn coordinator_cancel_stops_run_and_shutdown_refuses_new() {
    korean();
    let (started, blocked) = (Signal::new(), Signal::new());
    let (started_in, blocked_in) = (started.clone(), blocked.clone());
    let coordinator = CliUpdateCoordinator::new(updater(move |provider, token| {
        let (started, blocked) = (started_in.clone(), blocked_in.clone());
        Box::pin(async move {
            started.set();
            blocked.wait_or_cancel(&token).await.ok()?;
            Some(CliUpdateResult::new(&provider, "updated"))
        })
    }));
    coordinator.start();
    tokio::time::timeout(std::time::Duration::from_secs(5), started.wait()).await.expect("the run started");
    coordinator.cancel().await;
    assert!(!coordinator.is_updating(), "CancelAsync must stop the running update");
    coordinator.shutdown().await;
    assert!(!coordinator.start(), "Start after ShutdownAsync must return false");
    blocked.set();
}

/// Each provider's result appears as soon as that provider is done, while the button still reads 업데이트 중… and refuses a start.
#[tokio::test(flavor = "multi_thread")]
async fn coordinator_shows_each_result_while_the_button_reads_updating() {
    korean();
    let gates: Arc<HashMap<&'static str, Signal>> = Arc::new(PROVIDERS.iter().map(|p| (*p, Signal::new())).collect());
    let held = gates.clone();
    let coordinator = CliUpdateCoordinator::new(updater(move |provider, token| {
        let gate = held[provider.as_str()].clone();
        Box::pin(async move {
            gate.wait_or_cancel(&token).await.ok()?;
            Some(updated(&provider))
        })
    }));
    assert_eq!(coordinator.button_label(), strings::update_button(), "an idle button must read 업데이트 하기");
    assert!(coordinator.can_start(), "an idle button must be enabled");

    coordinator.start();
    assert_eq!(coordinator.button_label(), strings::updating_button(), "a running button must read 업데이트 중…");
    assert!(!coordinator.can_start(), "a running button must be disabled");

    gates[PROVIDERS[0]].set();
    until(|| coordinator.results().len() == 1, 5000, "the first result").await;
    assert!(coordinator.is_updating(), "the run must continue after the first result appears");
    assert_eq!(coordinator.results()[0].provider, PROVIDERS[0], "the first result must be the first provider");
    assert_eq!(
        CliUpdateService::result_row(&coordinator.results()[0]),
        format!("Claude · {}", strings::status_updated()),
        "the row must read {{provider}} · {{status}}"
    );
    assert_eq!(coordinator.button_label(), strings::updating_button(), "the button must still read 업데이트 중… between providers");

    gates[PROVIDERS[1]].set();
    until(|| coordinator.results().len() == 2, 5000, "the second result").await;
    assert!(coordinator.is_updating(), "two of the providers' results must not end the run");

    for provider in &PROVIDERS[2..] {
        gates[provider].set();
    }
    until(|| !coordinator.is_updating(), 5000, "the run ends").await;
    assert_eq!(coordinator.results().len(), PROVIDERS.len(), "every provider must leave a result");
    assert_eq!(coordinator.button_label(), strings::update_button(), "the finished button must read 업데이트 하기 again");
    assert!(coordinator.can_start(), "the finished button must be enabled again");
}

/// A provider that fails is reported with the macOS failure label and the remaining providers are still updated.
#[tokio::test(flavor = "multi_thread")]
async fn coordinator_reports_a_failed_provider_and_still_updates_the_rest() {
    korean();
    let failing = PROVIDERS[1];
    let failure = || {
        CliUpdateResult::new(failing, "failed")
            .with_versions(Some("1.0"), None)
            .with_method("npm")
            .with_detail(strings::detail_failed_exit_template().replace("{code}", "3"))
    };
    let coordinator = CliUpdateCoordinator::new(updater(move |provider, _| {
        Box::pin(async move { Some(if provider == failing { failure() } else { updated(&provider) }) })
    }));
    coordinator.start();
    until(|| !coordinator.is_updating(), 5000, "the run ends").await;

    let results = coordinator.results();
    assert_eq!(results.iter().map(|r| r.provider.as_str()).collect::<Vec<_>>(), PROVIDERS, "a failure must not cut the run short");
    let mut expected = vec!["updated", "failed"];
    expected.extend(std::iter::repeat_n("updated", PROVIDERS.len() - 2));
    assert_eq!(results.iter().map(|r| r.status.as_str()).collect::<Vec<_>>(), expected, "the failure must be kept next to the successes");
    let failed = results.iter().find(|r| r.status == "failed").unwrap();
    assert_eq!(
        CliUpdateService::result_row(failed),
        format!("Codex · {}", strings::status_failed()),
        "the failed row must read 업데이트 실패"
    );
    assert_eq!(CliUpdateService::version_change(failed), None, "a failed provider shows no version line");
    assert_eq!(failed.detail, strings::detail_failed_exit_template().replace("{code}", "3"), "the failure sentence must reach the section");
    assert!(coordinator.finished_at().is_some(), "a run with a failure still finishes");
}

/// The section keeps the last run's results and finish time; a new run clears them while it works and replaces them when done.
#[tokio::test(flavor = "multi_thread")]
async fn coordinator_keeps_the_last_run_results_and_finish_time() {
    korean();
    let status = Arc::new(Mutex::new("updated".to_string()));
    let hold = Arc::new(Mutex::new(false));
    let gate = Signal::new();
    let (status_in, hold_in, gate_in) = (status.clone(), hold.clone(), gate.clone());
    let coordinator = CliUpdateCoordinator::new(updater(move |provider, token| {
        let (status, hold, gate) = (status_in.lock().unwrap().clone(), *hold_in.lock().unwrap(), gate_in.clone());
        Box::pin(async move {
            if hold {
                gate.wait_or_cancel(&token).await.ok()?;
            }
            Some(
                CliUpdateResult::new(&provider, &status)
                    .with_versions(Some("1.0"), Some("2.0"))
                    .with_method("native")
                    .with_detail(strings::detail_updated()),
            )
        })
    }));

    coordinator.start();
    until(|| !coordinator.is_updating(), 5000, "the first run ends").await;
    let first_finished = coordinator.finished_at();
    assert!(first_finished.is_some(), "the first run must record a finish time");
    assert!(coordinator.results().iter().all(|r| r.status == "updated"), "the first run's results must be kept");

    tokio::time::sleep(std::time::Duration::from_millis(20)).await;
    assert_eq!(coordinator.finished_at(), first_finished, "an idle coordinator must not move the finish time");
    assert_eq!(coordinator.results().len(), PROVIDERS.len(), "an idle coordinator must keep showing the last run");

    *status.lock().unwrap() = "current".into();
    *hold.lock().unwrap() = true;
    coordinator.start();
    assert!(coordinator.results().is_empty(), "a new run must clear the previous results");
    assert!(coordinator.finished_at().is_none(), "a new run must clear the previous finish time");

    gate.set();
    until(|| !coordinator.is_updating(), 5000, "the second run ends").await;
    assert!(coordinator.results().iter().all(|r| r.status == "current"), "the second run's results must replace the first");
    assert!(coordinator.finished_at() >= first_finished, "the second finish time must not go backwards");
}

// ── StringsVerification.CliUpdateStringsMatchMacOS ──────────────────────────────────────────────────────────────────────

/// StringsVerification `CliUpdateShared`: every field of `CliUpdateStrings` and the locale key it must read (the Windows
/// description, "이 PC에").
const CLI_UPDATE_SHARED: [(&str, &str); 35] = [
    ("SectionTitle", "settings.cliUpdate.sectionTitle"),
    ("AutoUpdateToggle", "settings.cliUpdate.autoUpdateToggle"),
    ("SectionDescription", "settings.cliUpdate.sectionDescriptionWindows"),
    ("ProgressInspecting", "settings.cliUpdate.progressInspecting"),
    ("ProgressProviderTemplate", "settings.cliUpdate.progressProviderTemplate"),
    ("LastRunTemplate", "settings.cliUpdate.lastRunTemplate"),
    ("UpdateButton", "settings.cliUpdate.updateButton"),
    ("UpdatingButton", "settings.cliUpdate.updatingButton"),
    ("ResultRowTemplate", "settings.cliUpdate.resultRowTemplate"),
    ("VersionChangeTemplate", "settings.cliUpdate.versionChangeTemplate"),
    ("StatusUpdated", "settings.cliUpdate.statusUpdated"),
    ("StatusCurrent", "settings.cliUpdate.statusCurrent"),
    ("StatusFailed", "settings.cliUpdate.statusFailed"),
    ("StatusCancelled", "settings.cliUpdate.statusCancelled"),
    ("StatusBusy", "settings.cliUpdate.statusBusy"),
    ("StatusSkipped", "settings.cliUpdate.statusSkipped"),
    ("DetailClosing", "settings.cliUpdate.detailClosing"),
    ("DetailInspectCancelled", "settings.cliUpdate.detailInspectCancelled"),
    ("DetailInspectFailed", "settings.cliUpdate.detailInspectFailed"),
    ("DetailBusy", "settings.cliUpdate.detailBusy"),
    ("DetailCancelled", "settings.cliUpdate.detailCancelled"),
    ("DetailUnsupportedProvider", "settings.cliUpdate.detailUnsupportedProvider"),
    ("DetailVersionUnknown", "settings.cliUpdate.detailVersionUnknown"),
    ("DetailMissing", "settings.cliUpdate.detailMissing"),
    ("DetailUnknownMethod", "settings.cliUpdate.detailUnknownMethod"),
    ("DetailNativeClaude", "settings.cliUpdate.detailNativeClaude"),
    ("DetailNpmPrerelease", "settings.cliUpdate.detailNpmPrerelease"),
    ("DetailNpmRuntimeMissing", "settings.cliUpdate.detailNpmRuntimeMissing"),
    ("DetailNpmPlan", "settings.cliUpdate.detailNpmPlan"),
    ("DetailFailedExitTemplate", "settings.cliUpdate.detailFailedExitTemplate"),
    ("DetailVersionRecheckFailed", "settings.cliUpdate.detailVersionRecheckFailed"),
    ("DetailUpdated", "settings.cliUpdate.detailUpdated"),
    ("DetailUnchanged", "settings.cliUpdate.detailUnchanged"),
    ("DetailWingetPlan", "settings.cliUpdate.detailWingetPlan"),
    ("DetailWingetRuntimeMissing", "settings.cliUpdate.detailWingetRuntimeMissing"),
];

/// StringsVerification `Validate` against locales/ko.json.
fn validate(actual: &[(&str, String)]) -> Option<String> {
    let truth: HashMap<&str, String> = CLI_UPDATE_SHARED.iter().map(|(name, key)| (*name, ko(key))).collect();
    let mut seen: HashMap<&str, &str> = HashMap::new();
    for (name, value) in actual {
        if value.is_empty() {
            return Some(format!("CliUpdateStrings.{name} is empty"));
        }
        if let Some(twin) = seen.get(value.as_str()) {
            return Some(format!("CliUpdateStrings.{name} duplicates {twin}"));
        }
        seen.insert(value, name);
        if let Some(bad) = bad_placeholder(value) {
            return Some(format!("CliUpdateStrings.{name} has a placeholder that is not {{name}}: {bad}"));
        }
        let Some(expected) = truth.get(name) else { return Some(format!("CliUpdateStrings.{name} mirrors nothing in locales/ko.json")) };
        if value != expected {
            return Some(format!("CliUpdateStrings.{name} differs from locales/ko.json: {value}"));
        }
    }
    CLI_UPDATE_SHARED
        .iter()
        .find(|(name, _)| !actual.iter().any(|(n, _)| n == name))
        .map(|(name, _)| format!("CliUpdateStrings is missing {name}"))
}

/// The CLI update copy is the macOS copy apart from the recorded substitution, and the status labels come from the same table.
#[test]
fn cli_update_strings_match_macos() {
    korean();
    let actual = strings::all();
    assert_eq!(validate(&actual), None);

    let broken = |field: &str, value: &str| -> Vec<(&str, String)> {
        actual.iter().map(|(n, v)| (*n, if *n == field { value.to_string() } else { v.clone() })).collect()
    };
    assert!(
        validate(&broken("SectionDescription", "이 Mac에 설치된 Claude Code·Codex·Gemini CLI를 기존 설치 방식으로 업데이트합니다."))
            .is_some(),
        "an unrecorded OS name must fail"
    );
    assert!(validate(&broken("StatusUpdated", "Updated")).is_some(), "English copy must fail");
    assert!(validate(&broken("VersionChangeTemplate", "{ before } → {after}")).is_some(), "a placeholder that is not {{name}} must fail");
    assert!(validate(&broken("StatusCurrent", &strings::status_skipped())).is_some(), "a duplicate value must fail");
    let missing: Vec<(&str, String)> = actual.iter().filter(|(n, _)| *n != "DetailMissing").cloned().collect();
    assert!(validate(&missing).is_some(), "a missing literal must fail");

    for (status, label) in [
        ("updated", strings::status_updated()),
        ("current", strings::status_current()),
        ("skipped", strings::status_skipped()),
        ("failed", strings::status_failed()),
        ("cancelled", strings::status_cancelled()),
        ("busy", strings::status_busy()),
    ] {
        assert_eq!(strings::status_label(status), label, "status label for {status} changed");
    }
    assert_eq!(
        strings::status_label("something-else"),
        strings::status_skipped(),
        "an unknown status must read 건너뜀, as it does on macOS"
    );
}

// ── AcpAgentAccountVerification (the CLI update half) ───────────────────────────────────────────────────────────────────

/// OpenCode 2 upgrades itself (`opencode upgrade`, by the shim npm left), OpenCode 1 is left to the setup screen, Hermes updates
/// with `update --yes` (found in its installer's bin folder, or `$HERMES_HOME\bin`); with neither CLI anywhere nothing runs.
#[tokio::test]
async fn opencode_upgrades_itself_open_code1_is_left_to_the_setup_screen_and_hermes_updates_with_yes() {
    korean();
    let home = temp("acp-update");
    let empty = dir(&home, &["empty"]);
    let roaming = dir(&home, &["AppData", "Roaming"]);
    let local = dir(&home, &["AppData", "Local"]);
    let opencode = exe(&dir(&roaming, &["npm"]), "opencode.cmd");
    let hermes = exe(&dir(&local, &["hermes", "bin"]), "hermes.exe");
    let environment = BTreeMap::from([
        ("PATH".to_string(), text(&empty)),
        ("APPDATA".to_string(), text(&roaming)),
        ("LOCALAPPDATA".to_string(), text(&local)),
    ]);
    let opencode_version = Arc::new(Mutex::new("opencode v2.2.7".to_string()));
    let hermes_version = Arc::new(Mutex::new(
        "Hermes Agent v0.23.0 (2026.9.1) · stable · local 1a2b3c4d5e6f\nInstall: C:\\Users\\someone\\AppData\\Local\\hermes\\hermes-agent\nPython: 3.12.8\nUpdate available\n"
            .to_string(),
    ));
    let (opencode_path, hermes_path) = (text(&opencode), text(&hermes));
    let (o, h) = (opencode_version.clone(), hermes_version.clone());
    let runner = FakeRunner::new(move |executable, arguments| {
        if arguments == ["--version"] {
            let value = if executable == hermes_path { h.lock().unwrap().clone() } else { o.lock().unwrap().clone() };
            return ok(&format!("{value}\n"));
        }
        if executable == opencode_path && arguments == ["upgrade"] {
            *o.lock().unwrap() = "opencode v2.2.8".into();
            return ok("Upgraded to 2.2.8\n");
        }
        if executable == hermes_path && arguments == ["update", "--yes"] {
            let next = h.lock().unwrap().replace("v0.23.0 (2026.9.1)", "v0.23.1 (2026.9.24)");
            *h.lock().unwrap() = next;
            return ok("Updated\n");
        }
        Ok(CliRunResult::new(2, "", format!("unexpected: {}", arguments.join(" ")), false))
    });
    let service = make_service(&runner, environment, &home);
    let get = mighty_run::strings::get;

    let inspected = service.inspect("opencode", &none()).await;
    assert!(
        inspected.method == "opencode"
            && inspected.can_update
            && inspected.version.as_deref() == Some("opencode v2.2.7")
            && inspected.executable_path.as_deref() == Some(text(&opencode).as_str())
            && inspected.detail == get("settings.cliUpdate.detailOpencode"),
        "OpenCode 2 upgrades itself: {inspected:?}"
    );
    let upgraded = service.update("opencode", &none()).await;
    assert!(
        upgraded.status == "updated"
            && upgraded.before_version.as_deref() == Some("opencode v2.2.7")
            && upgraded.after_version.as_deref() == Some("opencode v2.2.8")
            && upgraded.method == "opencode",
        "upgraded: {upgraded:?}"
    );
    let call = runner.calls().into_iter().find(|c| c.arguments != ["--version"]).expect("one upgrade");
    assert!(call.executable == text(&opencode) && call.arguments == ["upgrade"], "opencode upgrade, by the shim npm left");
    assert!(
        CliUpdateService::result_row(&upgraded) == format!("opencode · {}", strings::status_updated())
            && CliUpdateService::version_change(&upgraded).as_deref() == Some("opencode v2.2.7 → opencode v2.2.8"),
        "the row"
    );

    // OpenCode 1's own upgrade stays on 1: it is not run, and the setup screen's reinstall is named instead.
    runner.clear();
    *opencode_version.lock().unwrap() = "1.19.5".into();
    let legacy = service.update("opencode", &none()).await;
    assert!(
        legacy.status == "skipped"
            && legacy.method == "opencode"
            && legacy.before_version.as_deref() == Some("1.19.5")
            && legacy.detail == get("settings.cliUpdate.detailOpencodeV1")
            && runner.args().iter().all(|a| a == &["--version"]),
        "OpenCode 1 is skipped: {legacy:?}"
    );
    let offered = service.inspect("opencode", &none()).await;
    assert!(!offered.can_update && offered.method == "opencode", "and not offered");

    runner.clear();
    let before = service.inspect("hermes", &none()).await;
    assert!(
        before.method == "hermes"
            && before.can_update
            && before.version.as_deref() == Some("Hermes Agent v0.23.0 (2026.9.1) · stable · local 1a2b3c4d5e6f")
            && before.executable_path.as_deref() == Some(text(&hermes).as_str())
            && before.detail == get("settings.cliUpdate.detailHermes"),
        "Hermes, by its version's first line: {before:?}"
    );
    let updated = service.update("hermes", &none()).await;
    assert!(
        updated.status == "updated"
            && updated.after_version.as_deref() == Some("Hermes Agent v0.23.1 (2026.9.24) · stable · local 1a2b3c4d5e6f")
            && updated.method == "hermes",
        "updated: {updated:?}"
    );
    let update = runner.calls().into_iter().find(|c| c.arguments != ["--version"]).expect("one update");
    assert!(update.executable == text(&hermes) && update.arguments == ["update", "--yes"], "hermes update --yes");
    assert!(
        CliUpdateService::result_row(&updated) == format!("Hermes · {}", strings::status_updated())
            && CliUpdateService::provider_label("opencode") == "opencode"
            && CliUpdateService::provider_label("hermes") == "Hermes",
        "the rows' names"
    );

    // $HERMES_HOME\bin is looked in when it is set; with neither CLI anywhere, nothing runs.
    let hermes_home = home.join("hermes-home");
    let fallback = exe(&dir(&hermes_home, &["bin"]), "hermes.cmd");
    let fallback_path = text(&fallback);
    let elsewhere = CliUpdateService::new(
        FakeRunner::new(move |executable, _| ok(if executable == fallback_path { "Hermes Agent v0.23.1\n" } else { "" })),
        BTreeMap::from([("PATH".to_string(), text(&empty)), ("HERMES_HOME".to_string(), text(&hermes_home))]),
        text(&home),
    )
    .inspect("hermes", &none())
    .await;
    assert!(
        elsewhere.method == "hermes" && elsewhere.executable_path.as_deref() == Some(text(&fallback).as_str()),
        "$HERMES_HOME\\bin: {elsewhere:?}"
    );
    let nobody = FakeRunner::new(|_, _| ok("x"));
    let nowhere = make_service(
        &nobody,
        BTreeMap::from([
            ("PATH".to_string(), text(&empty)),
            ("APPDATA".to_string(), text(&empty)),
            ("LOCALAPPDATA".to_string(), text(&empty)),
        ]),
        &home,
    );
    let (opencode_missing, hermes_missing) = (nowhere.update("opencode", &none()).await, nowhere.update("hermes", &none()).await);
    assert!(
        opencode_missing.status == "skipped"
            && opencode_missing.method == "missing"
            && hermes_missing.status == "skipped"
            && hermes_missing.method == "missing"
            && nobody.calls().is_empty(),
        "missing: nothing runs"
    );
    let _ = std::fs::remove_dir_all(home);
}

/// Rust only: making the service reads nothing (the app makes it as its run engine attaches, possibly with the state locked); the
/// CLI environment is read each time an update is planned, on a blocking thread, so a PATH refreshed meanwhile counts.
#[tokio::test]
async fn the_environment_is_read_when_an_update_is_planned_not_when_the_service_is_made() {
    korean();
    let home = temp("source");
    let reads = Arc::new(AtomicUsize::new(0));
    let (counted, empty) = (reads.clone(), text(&dir(&home, &["empty"])));
    let runner = FakeRunner::new(|_, _| version_answer("2.1.271"));
    let service = CliUpdateService::with_environment_source(
        runner.clone(),
        Arc::new(move || {
            counted.fetch_add(1, Ordering::SeqCst);
            BTreeMap::from([("PATH".to_string(), empty.clone())])
        }),
        text(&home),
    );
    assert_eq!(reads.load(Ordering::SeqCst), 0, "made, it read nothing");
    assert_eq!(service.inspect("claude", &none()).await.method, "missing");
    assert_eq!(reads.load(Ordering::SeqCst), 1, "a plan reads the environment once");
    assert_eq!(service.update("codex", &none()).await.method, "missing");
    assert_eq!(reads.load(Ordering::SeqCst), 2, "each plan reads it again");
    let _ = std::fs::remove_dir_all(home);
}

/// C#'s `finally`: a run whose updater panics still ends — not updating any more, its finish time set, the listeners told — so the
/// box and the holds never stay on.
#[tokio::test(flavor = "multi_thread")]
async fn a_coordinator_whose_updater_panics_still_ends_its_run() {
    korean();
    let coordinator = CliUpdateCoordinator::new(updater(|provider, _| {
        Box::pin(async move {
            if provider == PROVIDERS[1] {
                panic!("the stand-in updater fails");
            }
            Some(CliUpdateResult::new(&provider, "updated"))
        })
    }));
    let told = Arc::new(AtomicUsize::new(0));
    let count = told.clone();
    coordinator.on_state_changed(move || {
        count.fetch_add(1, Ordering::SeqCst);
    });
    assert!(coordinator.start());
    until(|| !coordinator.is_updating(), 5000, "the run ends").await;
    assert!(coordinator.finished_at().is_some(), "it has a finish time");
    assert_eq!(coordinator.results().len(), 1, "the first provider's result is kept");
    coordinator.cancel().await;
    assert!(told.load(Ordering::SeqCst) >= 3, "started, the first result, the end");
    assert!(coordinator.can_start(), "and a new run may start");
}
