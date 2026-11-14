//! C# CodexPluginVerification (Verification.cs:1497-1508), every check under its name: the Codex plugin list over a fake runner that
//! answers the Codex subcommands and the capability probes — no real codex, no network, no real profile.
//! `WindowIsARealWinUISurfaceWiredIntoTheApp` scans WinUI source: its rules about the Codex window's controls are checked here
//! (`the_codex_window_offers_only_the_macos_changes`); the surface and its wiring become registry and smoke checks in phase 2.

mod common;

use std::collections::HashMap;
use std::path::Path;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

use common::*;
use mighty_composer::slash::{SlashCommandAction, catalog_builtins, palette};
use mighty_core::locale::Locale;
use mighty_updates::plugins::browser::CodexPluginSmokeOutcome;
use mighty_updates::plugins::strings::{codex as strings, plugin as shared};
use mighty_updates::plugins::support;
use mighty_updates::plugins::{ClaudeInstalledPlugin, ClaudePluginBrowser, ClaudePluginSnapshot, CodexPluginReader, PluginReader, status};
use mighty_updates::runner::{CancellationToken, CliRunResult, RunError};

fn none() -> CancellationToken {
    CancellationToken::new()
}

/// Reading the help of "plugin add" is still only reading; a call that would change the registry names the verb without --help.
fn mutated(runner: &FakeRunner) -> bool {
    runner.args().iter().any(|a| {
        !a.iter().any(|x| x == "--help")
            && a.iter()
                .any(|x| matches!(x.as_str(), "install" | "uninstall" | "remove" | "enable" | "disable" | "update" | "add" | "upgrade"))
    })
}

/// A PATH whose only entry holds a file named codex; the fake runner answers it.
fn fixture(runner: &Arc<FakeRunner>, installed: bool) -> (CodexPluginReader, String) {
    let workspace = temp("codex-workspace");
    let bin = temp("codex-bin");
    let executable = text(&bin.join("codex"));
    let reader = CodexPluginReader::with_probes(
        runner.clone(),
        env(&[&bin]),
        Arc::new(move |path: &str| installed && path == executable),
        Arc::new(|path: &str| Path::new(path).is_dir()),
    );
    (reader, text(&workspace))
}

fn has(args: &[String], value: &str) -> bool {
    args.iter().any(|a| a == value)
}

/// Help answers that pass all four capability probes.
fn help_for(args: &[String]) -> Result<CliRunResult, RunError> {
    let args: Vec<&str> = args.iter().map(String::as_str).collect();
    match args.as_slice() {
        [_, "list", "--help"] => ok("  codex plugin list [OPTIONS]\n  --json    Output JSON\n  --available  Include catalog\n"),
        [_, "add", "--help"] => ok("  codex plugin add [OPTIONS]\n  --json    Output JSON\n"),
        [_, "marketplace", "list", "--help"] => ok("  codex plugin marketplace list [OPTIONS]\n  --json    Output JSON\n"),
        [_, "marketplace", "upgrade", "--help"] => ok("  codex plugin marketplace upgrade [OPTIONS]\n  --json    Output JSON\n"),
        _ => failed("unknown"),
    }
}

const MARKETS: &str =
    r#"{"marketplaces":[{"name":"sample","marketplaceSource":{"sourceType":"git","source":"https://example.com/sample"}}]}"#;
const EMPTY_MARKETS: &str = r#"{"marketplaces":[]}"#;

fn listing(with_installed: bool) -> &'static str {
    if with_installed {
        r#"{"installed":[{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","version":"1.0.0","installed":true,"enabled":true}],"available":[{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","version":"1.0.0","installed":false,"enabled":false,"installPolicy":"AVAILABLE"},{"pluginId":"docs@sample","name":"docs","marketplaceName":"sample","version":"0.5.0","installed":false,"enabled":false,"installPolicy":"AVAILABLE"}]}"#
    } else {
        r#"{"installed":[],"available":[{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","version":"1.0.0","installed":false,"enabled":false,"installPolicy":"AVAILABLE"}]}"#
    }
}

/// A codex that answers its version and help, and its reads from `reads`.
fn runner(reads: impl Fn(&[String]) -> Result<CliRunResult, RunError> + Send + Sync + 'static) -> Arc<FakeRunner> {
    FakeRunner::new(move |_, args| {
        if has(args, "--version") {
            ok("1.0.0")
        } else if has(args, "--help") {
            help_for(args)
        } else {
            reads(args)
        }
    })
}

fn healthy(listing_body: &'static str, markets: &'static str) -> Arc<FakeRunner> {
    runner(move |args| if has(args, "--available") { ok(listing_body) } else { ok(markets) })
}

/// A full read is a ready snapshot with the expected counts.
#[tokio::test]
async fn reads_parses_listing() {
    korean();
    let runner = healthy(listing(true), MARKETS);
    let (reader, folder) = fixture(&runner, true);
    let browser = ClaudePluginBrowser::new("codex", workspace(&folder));
    browser.apply(reader.snapshot(browser.workspace(), none()).await);
    assert!(browser.is_ready(), "ready: {}", browser.snapshot().unwrap().detail);
    assert_eq!(browser.installed_count(), 1, "installed count");
    assert_eq!(browser.available_count(), 2, "available count");
    assert_eq!(browser.marketplaces(), ["sample"], "marketplace");
    assert_eq!(browser.snapshot().unwrap().detail, strings::detail_ready(), "detail");
    assert!(!mutated(&runner), "a read changes nothing");
}

/// A help answer that does not name a required flag: unsupported.
#[tokio::test]
async fn capability_probe_blocks_unsupported_cli() {
    korean();
    let runner = FakeRunner::new(|_, args| {
        if has(args, "--version") {
            ok("1.0.0")
        } else if has(args, "--help") {
            // Every probe names --json, so `plugin list --help` lacks --available.
            ok("  --json\n")
        } else {
            failed("")
        }
    });
    let (reader, folder) = fixture(&runner, true);
    let snapshot = reader.snapshot(&workspace(&folder), none()).await;
    assert_eq!(snapshot.status, status::UNSUPPORTED, "unsupported when --available is missing");
    assert_eq!(snapshot.detail, strings::detail_unsupported(), "detail");
    assert!(!mutated(&runner), "a probe changes nothing");
}

/// No codex on PATH: the missing status, not an error.
#[tokio::test]
async fn missing_codex_becomes_missing() {
    korean();
    let runner = FakeRunner::new(|_, _| failed(""));
    let (reader, folder) = fixture(&runner, false);
    let snapshot = reader.snapshot(&workspace(&folder), none()).await;
    assert_eq!(snapshot.status, status::MISSING, "missing CLI");
    assert_eq!(snapshot.detail, strings::detail_missing_cli(), "detail");
}

/// Malformed or oversized output fails, never a ready-but-empty list.
#[tokio::test]
async fn malformed_or_oversized_never_becomes_empty_list() {
    korean();
    let runner = healthy("not json at all", MARKETS);
    let (reader, folder) = fixture(&runner, true);
    let snapshot = reader.snapshot(&workspace(&folder), none()).await;
    assert_eq!(snapshot.status, status::FAILED, "failed on a malformed listing");
    assert_eq!(snapshot.detail, shared::detail_malformed(), "detail");

    // A bare marketplace array instead of {"marketplaces":[...]} is malformed too.
    let bare = healthy(listing(true), r#"[{"name":"sample"}]"#);
    let (reader, folder) = fixture(&bare, true);
    assert_eq!(reader.snapshot(&workspace(&folder), none()).await.status, status::FAILED, "failed on a bare marketplace array");
}

/// A row whose policy is neither AVAILABLE nor INSTALLED_BY_DEFAULT is left out and counted in the restricted suffix.
#[tokio::test]
async fn restricted_policy_excludes_rows() {
    korean();
    let restricted = r#"{"installed":[],"available":[{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","installed":false,"enabled":false,"installPolicy":"AVAILABLE"},{"pluginId":"sec@sample","name":"sec","marketplaceName":"sample","installed":false,"enabled":false,"installPolicy":"RESTRICTED"}]}"#;
    let runner = healthy(restricted, MARKETS);
    let (reader, folder) = fixture(&runner, true);
    let snapshot = reader.snapshot(&workspace(&folder), none()).await;
    assert_eq!(snapshot.status, status::READY, "ready");
    assert_eq!(snapshot.available.len(), 1, "only AVAILABLE shown");
    assert!(snapshot.detail.contains('1'), "restricted count in detail: {}", snapshot.detail);
    assert_eq!(snapshot.detail, strings::detail_ready() + &strings::detail_restricted_suffix().replace("{count}", "1"), "the suffix");
}

/// Codex installed rows are always user scope.
#[tokio::test]
async fn installed_rows_are_always_user_scope() {
    korean();
    let runner = healthy(listing(true), MARKETS);
    let (reader, folder) = fixture(&runner, true);
    let browser = ClaudePluginBrowser::new("codex", workspace(&folder));
    browser.apply(reader.snapshot(browser.workspace(), none()).await);
    assert!(browser.is_ready(), "ready");
    assert_eq!(browser.installed_count(), 1, "installed");
    let snapshot = browser.snapshot().unwrap();
    assert_eq!(snapshot.installed[0].scope, "user", "scope");
    assert_eq!(browser.supported_scopes(), ["user"], "scopes");

    // A row claiming a scope Codex does not have is neither drawn nor counted, so the tab count equals the rows the tab lists.
    let mut stray = (*snapshot).clone();
    stray.installed.push(ClaudeInstalledPlugin {
        plugin_id: "stray@sample".into(),
        name: "stray".into(),
        marketplace: Some("sample".into()),
        scope: "project".into(),
        project_path: Some("/elsewhere".into()),
        ..Default::default()
    });
    browser.apply(stray.clone());
    assert_eq!(browser.installed_rows().len(), 1, "a project-scope row is not listed under the Codex title");
    assert!(
        browser.installed_count() == 1 && browser.tab_label(ClaudePluginBrowser::INSTALLED_TAB) == "설치됨 1",
        "the tab count matches the rows the tab lists: {}",
        browser.tab_label(ClaudePluginBrowser::INSTALLED_TAB)
    );

    // The Claude window counts every scope its CLI reports, as before.
    let claude = ClaudePluginBrowser::new("claude", workspace(&folder));
    claude.apply(stray);
    assert!(claude.installed_count() == 2 && claude.installed_rows().len() == 2, "the Claude window still counts and lists every scope");
}

/// A second concurrent read joins the running read rather than starting a new one.
#[tokio::test(flavor = "multi_thread")]
async fn second_request_joins_the_running_read() {
    korean();
    let called = Arc::new(AtomicUsize::new(0));
    let gate = Signal::new();
    let runner = FakeRunner::new(|_, _| ok(""));
    let (counted, held) = (called.clone(), gate.clone());
    runner.set_async(move |call, _| {
        let (counted, held) = (counted.clone(), held.clone());
        Box::pin(async move {
            let args = &call.arguments;
            if has(args, "--version") {
                counted.fetch_add(1, Ordering::SeqCst);
                held.wait().await;
                return ok("1.0.0");
            }
            if has(args, "--help") {
                return help_for(args);
            }
            if has(args, "--available") {
                return ok(listing(true));
            }
            ok(MARKETS)
        })
    });
    let (reader, folder) = fixture(&runner, true);
    let ws = workspace(&folder);
    let first = reader.snapshot(&ws, none());
    let second = reader.snapshot(&ws, none());
    gate.set();
    let (s1, s2) = (first.await, second.await);
    assert!(s1.status == status::READY && s2.status == status::READY, "both ready");
    assert_eq!(called.load(Ordering::SeqCst), 1, "only one version call");
    assert!(Arc::ptr_eq(&s1, &s2) || s1.updated_at == s2.updated_at, "same snapshot");
}

/// The window shows the Codex footer, the user scope only, and no Claude help link.
#[tokio::test]
async fn browser_shows_codex_footer_and_scopes() {
    korean();
    let runner = healthy(listing(false), EMPTY_MARKETS);
    let (reader, folder) = fixture(&runner, true);
    let browser = ClaudePluginBrowser::new("codex", workspace(&folder));
    browser.apply(reader.snapshot(browser.workspace(), none()).await);
    assert!(browser.is_ready(), "ready");
    assert_eq!(browser.footer_note(), strings::footer_note(), "footer");
    assert!(browser.footer_note().contains("이 PC의"), "the footer names this PC, not a Mac");
    browser.set_tab(ClaudePluginBrowser::MARKETPLACE_TAB);
    assert_eq!(browser.empty_message(), shared::empty_available(), "empty message");
    assert_eq!(browser.marketplace_help_text(), Some(strings::marketplace_help()), "macOS puts the Codex sentence under that copy");
    assert!(!browser.shows_marketplace_help_link(), "no Claude help link for Codex");
    assert_eq!(browser.status_text(), Some(strings::detail_no_marketplaces()), "Codex shows its detail even after a good read");

    let claude = ClaudePluginBrowser::new("claude", workspace(&folder));
    claude.apply(ClaudePluginSnapshot::of(status::READY, shared::detail_no_marketplaces()));
    claude.set_tab(ClaudePluginBrowser::MARKETPLACE_TAB);
    assert!(
        claude.footer_note() == shared::footer_note() && claude.shows_marketplace_help_link() && claude.marketplace_help_text().is_none(),
        "the Claude window keeps its own footer and its help link"
    );
    assert_eq!(claude.status_text(), None, "a ready Claude list still shows no status sentence");
    assert_eq!(claude.empty_message(), shared::empty_available(), "the Claude empty copy is unchanged");
}

/// CodexPluginStrings are the macOS literals of CodexPluginService.swift and ClaudePluginView.swift's Codex branches, and none
/// repeats a shared PluginStrings sentence. The OS-bound substitution (이 Mac의 → 이 PC의 in FooterNote) is checked.
#[test]
fn strings_match_macos() {
    korean();
    let mac_os: HashMap<&str, &str> = HashMap::from([
        (
            "DetailReady",
            "Codex 사용자 설치 목록과 현재 작업 폴더의 설정을 반영한 목록입니다. 이미 실행 중인 세션의 로드 상태와 다를 수 있습니다.",
        ),
        ("DetailNoMarketplaces", "등록된 마켓플레이스가 없습니다. Codex CLI에서 plugin marketplace add로 등록한 뒤 목록을 다시 읽으세요."),
        ("DetailRestrictedSuffix", " 설치 정책으로 설치할 수 없는 항목 {count}개는 제외했습니다."),
        ("DetailWarningSuffix", " CLI 경고가 있습니다. 일부 목록이 최신 상태가 아닐 수 있으니 진단 출력을 확인하세요."),
        ("DetailMissingCli", "Codex CLI가 설치되어 있지 않습니다. 먼저 CLI를 설치하세요."),
        ("DetailUnknownVersion", "설치된 Codex CLI 버전을 확인하지 못했습니다."),
        ("DetailUnsupported", "설치된 Codex CLI가 필요한 JSON 플러그인 명령을 지원하지 않습니다. 최신 Codex CLI로 업데이트하세요."),
        ("DetailListingFailed", "Codex CLI에서 플러그인 목록을 읽지 못했습니다."),
        ("MarketplaceHelp", "Codex CLI에서 마켓플레이스를 등록한 뒤 목록을 새로고침하세요."),
        // OS-bound substitution, recorded in docs/windows-plugins.md: macOS reads "이 Mac의 Codex 설치 목록과 마켓플레이스 목록입니다."
        ("FooterNote", "이 PC의 Codex 설치 목록과 마켓플레이스 목록입니다. 설치 후 새 Codex 세션을 시작하세요."),
        ("InstallSkipped", "이미 사용자 범위에 설치되어 있습니다. 비활성 상태라면 Codex에서 활성화하세요."),
        ("InstallNotFound", "현재 설치 가능한 마켓플레이스 목록에서 이 플러그인을 찾지 못했습니다. 목록과 설치 정책을 다시 확인하세요."),
        (
            "InstallSucceeded",
            "플러그인을 사용자 범위에 설치했습니다. 새 Codex 세션부터 적용됩니다. 연결이 필요한 앱은 Codex에서 인증하세요.",
        ),
        ("InstallVerifyFailed", "CLI가 설치 결과를 반환했지만 설치 목록에서 확인하지 못했습니다. 목록을 다시 읽으세요."),
        ("OperationFailed", "플러그인 작업에 실패했거나 결과 형식이 올바르지 않습니다. 네트워크·권한·조직 정책을 확인하세요."),
        (
            "MarketplaceNotGit",
            "이 마켓플레이스는 Git 소스가 아니어서 Codex CLI로 갱신할 수 없습니다. 목록을 다시 읽으면 현재 상태를 확인할 수 있습니다.",
        ),
        ("MarketplaceRefreshUnconfirmed", "마켓플레이스 새로고침을 확인하지 못했습니다. CLI 진단 출력을 확인하세요."),
        ("NoGitMarketplaces", "갱신할 Git 마켓플레이스가 없습니다. 로컬·기본 제공 마켓플레이스는 목록 새로고침으로 확인하세요."),
        ("RefreshGitOnly", "마켓플레이스 갱신은 등록된 Git 소스만 지원합니다. 다른 소스는 목록 새로고침으로 확인하세요."),
    ]);
    let actual = strings::all();
    let shared_values: HashMap<String, &str> = shared::all().into_iter().map(|(name, value)| (value, name)).collect();
    for (name, value) in &actual {
        assert!(!value.is_empty(), "CodexPluginStrings.{name} is empty");
        let expected = mac_os.get(name).unwrap_or_else(|| panic!("CodexPluginStrings.{name} mirrors no macOS literal"));
        assert_eq!(value, expected, "CodexPluginStrings.{name} differs from macOS");
        assert!(
            !shared_values.contains_key(value),
            "CodexPluginStrings.{name} repeats a PluginStrings sentence; reuse the shared one instead"
        );
    }
    assert_eq!(actual.len(), mac_os.len(), "the copy table and the class must hold the same fields");
    for name in mac_os.keys() {
        assert!(actual.iter().any(|(n, _)| n == name), "CodexPluginStrings is missing {name}");
    }
    assert!(
        strings::footer_note().contains("이 PC의") && !strings::footer_note().contains("Mac"),
        "FooterNote must say 이 PC의, not 이 Mac의"
    );
}

/// Codex /plugins is in the Windows palette, and no app action is left out of it any more (docs/windows-slash-commands.md).
#[test]
fn palette_offers_codex_plugins() {
    let l = Locale::new("ko");
    let opens = |provider: &str, invocation: &str| {
        palette::builtins(provider, &l).iter().any(|c| c.action == Some(SlashCommandAction::OpenPlugins) && c.invocation == invocation)
    };
    assert!(opens("codex", "plugins"), "the Codex palette offers /plugins");
    assert!(palette::UNAVAILABLE_ACTIONS.is_empty(), "no app action is universally unavailable");
    for provider in ["claude", "codex", "gemini"] {
        assert_eq!(
            palette::builtins(provider, &l).len(),
            catalog_builtins(provider, &l).len(),
            "the Windows palette offers every macOS built-in for {provider}"
        );
    }
    assert!(opens("claude", "plugin"), "the Claude palette is unchanged");
}

/// A CLI that exits badly, answers nonsense or runs too long: each a status with its macOS sentence, never an error.
#[tokio::test]
async fn failed_runs_and_timeouts_keep_the_screen_intact() {
    korean();
    let list_failed = runner(|args| if has(args, "--available") { failed("boom") } else { ok(MARKETS) });
    let (a, folder_a) = fixture(&list_failed, true);
    let failure = a.snapshot(&workspace(&folder_a), none()).await;
    assert!(
        failure.status == status::FAILED && failure.detail == strings::detail_listing_failed(),
        "a failing list call in the Codex words"
    );
    assert!(failure.diagnostic_output.contains("boom"), "the bounded CLI output is kept for the diagnostics disclosure");
    assert!(!mutated(&list_failed), "a failing read changes nothing");

    let markets_failed = runner(|args| if has(args, "--available") { ok(listing(true)) } else { failed("no markets") });
    let (b, folder_b) = fixture(&markets_failed, true);
    let market_failure = b.snapshot(&workspace(&folder_b), none()).await;
    assert!(
        market_failure.status == status::FAILED && market_failure.detail == shared::detail_marketplaces_failed(),
        "a failing marketplace call"
    );

    let slow = runner(|_| timed_out());
    let (c, folder_c) = fixture(&slow, true);
    let timed = c.snapshot(&workspace(&folder_c), none()).await;
    assert!(timed.status == status::FAILED && timed.detail == shared::detail_incomplete(), "a slow CLI is explained");

    let slow_probe = FakeRunner::new(|_, args| if has(args, "--version") { ok("1.0.0") } else { timed_out() });
    let (d, folder_d) = fixture(&slow_probe, true);
    assert_eq!(
        d.snapshot(&workspace(&folder_d), none()).await.status,
        status::UNSUPPORTED,
        "a probe that never answers is a CLI that cannot do it"
    );

    let unreadable = FakeRunner::new(|_, args| if has(args, "--version") { failed("nope") } else { ok(MARKETS) });
    let (e, folder_e) = fixture(&unreadable, true);
    let unknown = e.snapshot(&workspace(&folder_e), none()).await;
    assert!(unknown.status == status::FAILED && unknown.detail == strings::detail_unknown_version(), "an unreadable version is explained");

    let throwing = FakeRunner::new(|_, _| Err(RunError::Failed("pipe".into())));
    let (f, folder_f) = fixture(&throwing, true);
    let thrown = f.snapshot(&workspace(&folder_f), none()).await;
    assert!(matches!(thrown.status.as_str(), status::FAILED | status::UNSUPPORTED), "a runner that throws never escapes as an error");

    let gone = FakeRunner::new(|_, _| ok("never"));
    let (g, folder_g) = fixture(&gone, true);
    let missing_folder = g.snapshot(&workspace(&text(&Path::new(&folder_g).join("not-there"))), none()).await;
    assert_eq!(missing_folder.detail, shared::detail_missing_workspace(), "a folder that is gone is explained");
    assert_eq!(
        g.snapshot(&workspace("relative/path"), none()).await.detail,
        shared::detail_invalid_workspace(),
        "a path that is not rooted is refused"
    );
    assert!(gone.calls().is_empty(), "neither case started a process");

    // A CLI that warns on stderr but still answers keeps its warning.
    let warned = FakeRunner::new(|_, args| {
        if has(args, "--version") {
            ok("1.0.0")
        } else if has(args, "--help") {
            help_for(args)
        } else if has(args, "--available") {
            Ok(CliRunResult::new(0, listing(true), "Remote catalog unavailable; using cache", false))
        } else {
            ok(MARKETS)
        }
    });
    let (h, folder_h) = fixture(&warned, true);
    let stale = h.snapshot(&workspace(&folder_h), none()).await;
    assert_eq!(stale.status, status::READY, "a warning is not a failure");
    assert!(stale.diagnostic_output.contains("Remote catalog unavailable"), "the warning is kept");
    assert!(stale.detail.ends_with(&strings::detail_warning_suffix()), "the list says it may be stale: {}", stale.detail);
}

/// The Core half of `WindowIsARealWinUISurfaceWiredIntoTheApp` (Codex): the window offers only install (user level) and marketplace
/// refresh; the smoke key is codexPluginList.
#[test]
fn the_codex_window_offers_only_the_macos_changes() {
    for change in ["install-fmt", "marketplace-upgrade", "add-marketplace", "scope-picker"] {
        assert!(support::names_a_change(&support::automation_id("codex", change), "codex"), "a changing control must be caught: {change}");
    }
    for invented in ["uninstall", "enable-fmt", "add-marketplace", "marketplace-upgrade"] {
        assert!(
            !support::names_allowed_change(&support::automation_id("codex", invented), "codex"),
            "a change macOS does not have must stay refused: {invented}"
        );
    }
    assert_eq!(ClaudePluginBrowser::CODEX_PROVIDER, "codex", "the provider name is Core's constant");
    assert_eq!(CodexPluginSmokeOutcome::RESULT_KEY, "codexPluginList", "the smoke key is codexPluginList");
}
