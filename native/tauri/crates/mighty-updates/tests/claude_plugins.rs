//! C# ClaudePluginVerification (Verification.cs:1485-1496), every check under its name: the Claude plugin list over a fake runner
//! that answers the plugin subcommands (ClaudePluginTests.swift's fake claude script) — no real claude, no network, no real
//! profile. `WindowIsARealWinUISurfaceWiredIntoTheApp` scans WinUI source for the window's wiring: its rules about which controls
//! may name a change are checked here (`the_read_only_guard_allows_only_the_macos_changes`); the surface and its wiring become
//! registry and smoke checks with the plugin panel (W6-D phase 2).

mod common;

use std::collections::HashMap;
use std::path::Path;
use std::sync::Arc;

use common::*;
use mighty_composer::slash::{SlashCommandAction, catalog_builtins, palette};
use mighty_core::locale::Locale;
use mighty_updates::plugins::browser::ClaudePluginSmokeOutcome;
use mighty_updates::plugins::strings::plugin as strings;
use mighty_updates::plugins::support::{self, MAXIMUM_LISTING_BYTES};
use mighty_updates::plugins::{
    ClaudeInstalledPlugin, ClaudePluginBrowser, ClaudePluginMarketplace, ClaudePluginOperationResult, ClaudePluginReader,
    ClaudePluginSnapshot, PluginReader, status,
};
use mighty_updates::runner::CancellationToken;

fn none() -> CancellationToken {
    CancellationToken::new()
}

fn mutated(runner: &FakeRunner) -> bool {
    runner
        .args()
        .iter()
        .any(|a| a.iter().any(|x| matches!(x.as_str(), "install" | "uninstall" | "remove" | "enable" | "disable" | "update" | "add")))
}

/// A PATH whose only entry holds a file named claude, so discovery finds one candidate; the fake runner answers it.
fn fixture(runner: &Arc<FakeRunner>, installed: bool) -> (ClaudePluginReader, String, String) {
    let workspace = temp("claude-workspace");
    let bin = temp("claude-bin");
    let mut environment = env(&[&bin]);
    environment.insert("FORCE_AUTOUPDATE_PLUGINS".into(), "1".into());
    let executable = text(&bin.join("claude"));
    let expected = executable.clone();
    let reader = ClaudePluginReader::with_probes(
        runner.clone(),
        environment,
        Arc::new(move |path: &str| installed && path == expected),
        Arc::new(|path: &str| Path::new(path).is_dir()),
    );
    (reader, text(&workspace), executable)
}

fn json(value: &str) -> String {
    serde_json::to_string(value).unwrap()
}

fn listing(workspace_path: &str, other_path: Option<&str>) -> String {
    format!(
        concat!(
            r#"{{"installed":["#,
            r#"{{"id":"fmt@sample","scope":"user","enabled":false,"version":"1.2.0"}},"#,
            r#"{{"id":"fmt@sample","scope":"project","enabled":true,"projectPath":{},"errors":["픽스처 경고"]}},"#,
            r#"{{"id":"fmt@sample","scope":"local","enabled":true,"projectPath":{}}},"#,
            r#"{{"id":"invalid;touch@sample","scope":"user","enabled":true}}"#,
            r#"],"available":["#,
            r#"{{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","description":"Formats source files","version":"1.2.0","source":{{"source":"github"}}}},"#,
            r#"{{"pluginId":"ghost@nowhere","name":"ghost","marketplaceName":"nowhere","description":"Unregistered marketplace","source":"git"}}"#,
            r#"]}}"#
        ),
        json(workspace_path),
        json(other_path.unwrap_or("/somewhere/else"))
    )
}

const MARKETS: &str = r#"[{"name":"sample","source":{"source":"github"}}]"#;

fn healthy(workspace_path: &str, listing_body: Option<String>, markets: &str, version: &str) -> Arc<FakeRunner> {
    let body = listing_body.unwrap_or_else(|| listing(workspace_path, None));
    let (markets, version) = (markets.to_string(), version.to_string());
    FakeRunner::new(move |_, args| {
        if args.iter().any(|a| a == "--version") {
            ok(&version)
        } else if args.iter().any(|a| a == "marketplace") {
            ok(&markets)
        } else {
            ok(&body)
        }
    })
}

fn healthy_default(workspace_path: &str) -> Arc<FakeRunner> {
    healthy(workspace_path, None, MARKETS, "2.1.271 (Claude Code)")
}

/// ClaudePluginTests.listsCurrentWorkspaceScopesAndCachedCatalogWithoutMutation
#[tokio::test]
async fn lists_workspace_scopes_and_cached_catalog_without_mutation() {
    korean();
    let workspace = text(&temp("claude-list"));
    let runner = healthy_default(&workspace);
    let (reader, _, executable) = fixture(&runner, true);
    let snapshot = reader.snapshot(&common::workspace(&workspace), none()).await;

    assert_eq!(snapshot.status, status::READY, "status must be ready: {}", snapshot.detail);
    assert_eq!(snapshot.detail, strings::detail_ready(), "ready detail must be the macOS sentence");
    assert_eq!(snapshot.cli_version.as_deref(), Some("2.1.271 (Claude Code)"), "the CLI version is shown");
    assert_eq!(snapshot.installed.len(), 2, "user + this workspace's project record only");
    assert!(snapshot.installed.iter().any(|p| p.scope == "user" && p.enabled == Some(false)), "the user record keeps its disabled flag");
    assert!(snapshot.installed.iter().any(|p| p.scope == "project" && p.errors == ["픽스처 경고"]), "row errors survive");
    assert!(!snapshot.installed.iter().any(|p| p.scope == "local"), "another project's local record must not be listed");
    assert!(snapshot.installed.iter().all(|p| p.description == "Formats source files"), "the catalog description fills an installed row");
    assert!(snapshot.installed.iter().all(|p| p.marketplace.as_deref() == Some("sample")), "marketplace comes from the plugin id");
    assert!(snapshot.available.len() == 1 && snapshot.available[0].id == "fmt@sample", "only registered marketplaces are offered");
    assert_eq!(snapshot.available[0].source_kind, "github", "source kind");
    assert_eq!(snapshot.marketplaces, vec![ClaudePluginMarketplace::new("sample", "github")], "marketplace row");
    assert!(snapshot.updated_at.as_deref().is_some_and(|t| !t.is_empty()), "the read is timestamped");

    let calls = runner.calls();
    assert_eq!(calls.len(), 3, "exactly --version, plugin list and marketplace list");
    assert!(calls.iter().all(|c| c.executable == executable), "every call uses the discovered CLI");
    assert!(calls.iter().all(|c| c.working_directory.as_deref() == Some(workspace.as_str())), "every call runs in the workspace folder");
    assert!(calls.iter().any(|c| c.arguments == ["plugin", "list", "--json", "--available"]), "the catalog read needs --available");
    assert!(calls.iter().any(|c| c.arguments == ["plugin", "marketplace", "list", "--json"]), "marketplaces are only listed");
    assert!(!mutated(&runner), "looking must never install, enable, remove, update or add anything");
    let environment = calls[0].environment.clone().unwrap();
    assert!(
        environment["DISABLE_AUTOUPDATER"] == "1"
            && environment["CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL"] == "1"
            && environment["CLAUDE_CODE_DISABLE_BACKGROUND_TASKS"] == "1"
            && environment["GIT_TERMINAL_PROMPT"] == "0",
        "the macOS switches are forced on every call"
    );
    assert!(!environment.contains_key("FORCE_AUTOUPDATE_PLUGINS"), "a caller's FORCE_AUTOUPDATE_PLUGINS is dropped");
    // Rust only: the app's own FORCE_AUTOUPDATE_PLUGINS is not inherited either (C# hands the child the app's environment).
    assert!(calls.iter().all(|c| c.without == ["FORCE_AUTOUPDATE_PLUGINS"]), "the child never inherits FORCE_AUTOUPDATE_PLUGINS");
}

/// An ancestor's local record still applies to a nested workspace.
#[tokio::test]
async fn nested_workspace_sees_its_ancestors_record() {
    korean();
    let parent = temp("claude-parent");
    let nested = dir(&parent, &["packages", "app"]);
    let runner = healthy(&text(&nested), Some(listing(&text(&nested), Some(&text(&parent)))), MARKETS, "2.1.271 (Claude Code)");
    let (reader, _, _) = fixture(&runner, true);
    let snapshot = reader.snapshot(&workspace(&text(&nested)), none()).await;
    assert_eq!(snapshot.status, status::READY, "status");
    assert!(snapshot.installed.iter().any(|p| p.scope == "local"), "the ancestor's local record applies to the nested folder");
    assert_eq!(snapshot.installed.len(), 3, "user + project + ancestor local");
    let ids: std::collections::HashSet<String> = snapshot.installed.iter().map(ClaudeInstalledPlugin::id).collect();
    assert_eq!(ids.len(), 3, "each scope/path pair is its own row");
}

/// ClaudePluginTests.missingOrOldCLIAndEmptySourcesAreExplainedWithoutInstalling
#[tokio::test]
async fn missing_old_and_empty_are_explained_without_installing() {
    korean();
    let missing_runner = FakeRunner::new(|_, _| ok("never"));
    let (missing, missing_workspace, _) = fixture(&missing_runner, false);
    let absent = missing.snapshot(&workspace(&missing_workspace), none()).await;
    assert!(
        absent.status == status::MISSING && absent.detail == strings::detail_missing_cli(),
        "a missing CLI is explained: {}",
        absent.status
    );
    assert!(missing_runner.calls().is_empty(), "a missing CLI starts no process");

    let folder = text(&temp("claude-old"));
    let old_runner = healthy(&folder, None, MARKETS, "2.1.267 (Claude Code)");
    let (old, _, _) = fixture(&old_runner, true);
    let unsupported = old.snapshot(&workspace(&folder), none()).await;
    assert!(
        unsupported.status == status::UNSUPPORTED && unsupported.detail == strings::detail_unsupported(),
        "an old CLI is explained: {}",
        unsupported.status
    );
    assert_eq!(old_runner.calls().len(), 1, "an old CLI is never asked for the list");
    assert!(!mutated(&old_runner), "an old CLI changes nothing");

    let empty_runner = healthy(&folder, Some(r#"{"installed": [], "available": []}"#.into()), "[]", "2.1.271 (Claude Code)");
    let (empty, _, _) = fixture(&empty_runner, true);
    let none_listed = empty.snapshot(&workspace(&folder), none()).await;
    assert_eq!(none_listed.status, status::READY, "an empty but well-formed answer is ready");
    assert!(
        none_listed.detail == strings::detail_no_marketplaces() && none_listed.detail.contains("marketplace add"),
        "the empty-sources sentence names marketplace add"
    );
    assert!(none_listed.marketplaces.is_empty() && !mutated(&empty_runner), "nothing was registered on the user's behalf");
}

/// ClaudePluginTests.malformedOrOversizedCatalogDoesNotBecomeReadyEmpty
#[tokio::test]
async fn malformed_or_oversized_answer_never_becomes_an_empty_list() {
    // Rust only: an answer of too many rows is refused by a pre-scan that keeps no value, before the listing is parsed into values.
    let rows = |n: usize| (0..n).map(|i| format!(r#"{{"id":"p{i}@sample","scope":"user"}}"#)).collect::<Vec<_>>().join(",");
    assert!(support::within_row_caps(&format!(r#"{{"installed":[{}],"available":[]}}"#, rows(10_000))), "the cap itself passes");
    assert!(!support::within_row_caps(&format!(r#"{{"installed":[{}],"available":[]}}"#, rows(10_001))), "one row more is refused");
    assert!(!support::within_row_caps(&format!(r#"{{"installed":[],"available":[{}]}}"#, rows(10_001))), "in either array");
    assert!(!support::within_row_caps(&format!("[{}]", vec!["{}"; 257].join(","))), "Claude's marketplaces past 256");
    assert!(!support::within_row_caps(&format!(r#"{{"marketplaces":[{}]}}"#, vec!["{}"; 257].join(","))), "Codex's marketplaces past 256");
    assert!(!support::within_row_caps("{broken"), "text that is no JSON");
    assert!(support::within_row_caps(r#"{"installed":[],"available":[],"other":[1,2,3]}"#), "other fields are not counted");

    korean();
    let folder = text(&temp("claude-malformed"));
    for body in ["{broken", "[]", r#"{"installed":[],"available":"wrong"}"#, "", r#"{"available":[]}"#] {
        let runner = healthy(&folder, Some(body.into()), MARKETS, "2.1.271 (Claude Code)");
        let (reader, _, _) = fixture(&runner, true);
        let snapshot = reader.snapshot(&workspace(&folder), none()).await;
        assert_eq!(snapshot.status, status::FAILED, "malformed listing must fail, not read empty: {body}");
        assert_eq!(snapshot.detail, strings::detail_malformed(), "malformed listing uses the macOS sentence");
        assert!(!mutated(&runner), "a malformed answer changes nothing");
    }
    let bad_markets = healthy(&folder, None, "{}", "2.1.271 (Claude Code)");
    let (market_reader, _, _) = fixture(&bad_markets, true);
    assert_eq!(
        market_reader.snapshot(&workspace(&folder), none()).await.status,
        status::FAILED,
        "a non-array marketplace answer must fail"
    );

    let rows: Vec<String> = (0..10_001).map(|i| format!(r#"{{"id":"p{i}@sample","scope":"user"}}"#)).collect();
    let too_many = format!(r#"{{"installed":[{}],"available":[]}}"#, rows.join(","));
    let oversized = healthy(&folder, Some(too_many), MARKETS, "2.1.271 (Claude Code)");
    let (oversized_reader, _, _) = fixture(&oversized, true);
    assert_eq!(oversized_reader.snapshot(&workspace(&folder), none()).await.status, status::FAILED, "more rows than the cap must fail");

    let huge = "x".repeat(MAXIMUM_LISTING_BYTES + 1);
    assert_eq!(
        support::parse_snapshot(&huge, MARKETS, &folder, Some("2.1.271")).status,
        status::FAILED,
        "output past the byte cap must fail"
    );
}

/// A CLI that answers nonsense on the pipe, exits badly, or runs too long.
#[tokio::test]
async fn failed_runs_and_timeouts_keep_the_screen_intact() {
    korean();
    let folder = text(&temp("claude-failed"));
    let ws = workspace(&folder);

    let list_failed = FakeRunner::new(|_, args| {
        if args.iter().any(|a| a == "--version") {
            ok("2.1.271")
        } else if args.iter().any(|a| a == "marketplace") {
            ok(MARKETS)
        } else {
            failed("boom")
        }
    });
    let (a, _, _) = fixture(&list_failed, true);
    let failure = a.snapshot(&ws, none()).await;
    assert!(failure.status == status::FAILED && failure.detail == strings::detail_listing_failed(), "a failing list call is explained");
    assert!(failure.diagnostic_output.contains("boom"), "the bounded CLI output is kept for the diagnostics disclosure");

    let listing_body = listing(&folder, None);
    let markets_failed = FakeRunner::new(move |_, args| {
        if args.iter().any(|a| a == "--version") {
            ok("2.1.271")
        } else if args.iter().any(|a| a == "marketplace") {
            failed("no markets")
        } else {
            ok(&listing_body)
        }
    });
    let (b, _, _) = fixture(&markets_failed, true);
    let market_failure = b.snapshot(&ws, none()).await;
    assert!(
        market_failure.status == status::FAILED && market_failure.detail == strings::detail_marketplaces_failed(),
        "a failing marketplace call is explained"
    );

    let slow = FakeRunner::new(|_, args| if args.iter().any(|a| a == "--version") { ok("2.1.271") } else { timed_out() });
    let (c, _, _) = fixture(&slow, true);
    let timed = c.snapshot(&ws, none()).await;
    assert!(timed.status == status::FAILED && timed.detail == strings::detail_incomplete(), "a slow CLI is explained: {}", timed.detail);

    let unreadable = FakeRunner::new(|_, args| if args.iter().any(|a| a == "--version") { failed("nope") } else { ok(MARKETS) });
    let (d, _, _) = fixture(&unreadable, true);
    let unknown = d.snapshot(&ws, none()).await;
    assert!(unknown.status == status::FAILED && unknown.detail == strings::detail_unknown_version(), "an unreadable version is explained");

    let throwing = FakeRunner::new(|_, _| Err(mighty_updates::RunError::Failed("pipe".into())));
    let (e, _, _) = fixture(&throwing, true);
    assert_eq!(e.snapshot(&ws, none()).await.status, status::FAILED, "a runner that throws never escapes as an error");

    let gone = FakeRunner::new(|_, _| ok("never"));
    let (f, _, _) = fixture(&gone, true);
    let missing_folder = f.snapshot(&workspace(&text(&Path::new(&folder).join("not-there"))), none()).await;
    assert!(
        missing_folder.status == status::FAILED && missing_folder.detail == strings::detail_missing_workspace(),
        "a folder that is gone is explained"
    );
    let relative = f.snapshot(&workspace("relative/path"), none()).await;
    assert_eq!(relative.detail, strings::detail_invalid_workspace(), "a path that is not rooted is refused");
    assert!(gone.calls().is_empty(), "neither case started a process");
}

/// One read at a time: a second request joins the running read.
#[tokio::test(flavor = "multi_thread")]
async fn second_request_joins_the_running_read() {
    korean();
    let folder = text(&temp("claude-join"));
    let (started, release) = (Signal::new(), Signal::new());
    let runner = healthy_default(&folder);
    let body = listing(&folder, None);
    let (started_in, release_in) = (started.clone(), release.clone());
    runner.set_async(move |call, _| {
        let (started, release, body) = (started_in.clone(), release_in.clone(), body.clone());
        Box::pin(async move {
            if call.arguments.iter().any(|a| a == "--version") {
                return ok("2.1.271");
            }
            if call.arguments.iter().any(|a| a == "marketplace") {
                return ok(MARKETS);
            }
            started.set();
            release.wait().await;
            ok(&body)
        })
    });
    let (reader, _, _) = fixture(&runner, true);
    let ws = workspace(&folder);
    let first = reader.snapshot(&ws, none());
    tokio::time::timeout(std::time::Duration::from_secs(5), started.wait()).await.expect("the read started");
    let second = reader.snapshot(&ws, none());
    release.set();
    let (first, second) = (first.await, second.await);
    assert!(first.status == status::READY && Arc::ptr_eq(&first, &second), "both callers get the one answer");
    assert_eq!(
        runner.args().iter().filter(|a| a.iter().any(|x| x == "list") && a.iter().any(|x| x == "--available")).count(),
        1,
        "only one catalog read ran"
    );

    reader.shutdown();
    let after_close = reader.snapshot(&ws, none()).await;
    assert!(
        after_close.status == status::CANCELLED && after_close.detail == strings::detail_cancelled(),
        "a closed reader admits nothing more"
    );
}

/// The window: tabs with counts, the marketplace filter with 전체, the search box, the composed row subtitles and the empty copy.
#[tokio::test]
async fn browser_tabs_filter_search_and_rows_match_macos() {
    korean();
    let folder = text(&temp("claude-browser"));
    let (reader, _, _) = fixture(&healthy_default(&folder), true);
    let browser = ClaudePluginBrowser::new("claude", workspace(&folder));
    assert_eq!(browser.title(), "Claude 플러그인", "window title");
    browser.begin_load();
    assert_eq!(browser.empty_message(), strings::empty_loading(), "loading copy");
    browser.apply(reader.snapshot(browser.workspace(), none()).await);

    assert_eq!(browser.tab_label(ClaudePluginBrowser::INSTALLED_TAB), "설치됨 2", "installed tab");
    assert_eq!(browser.tab_label(ClaudePluginBrowser::MARKETPLACE_TAB), "마켓플레이스 1", "marketplace tab");
    assert_eq!(browser.marketplaces(), ["sample"], "the filter offers every marketplace the list mentions");
    assert_eq!(browser.marketplace_filter(), "", "전체 is the empty filter");

    let installed = browser.installed_rows();
    assert_eq!(installed.len(), 2, "installed rows");
    assert!(
        installed.iter().any(|r| r.subtitle == "sample · 사용자 · 전체"
            && r.state.as_deref() == Some(strings::state_disabled().as_str())
            && r.version.as_deref() == Some("1.2.0")),
        "user row: {}",
        installed[0].subtitle
    );
    assert!(
        installed.iter().any(|r| r.subtitle == "sample · 프로젝트 · 공유" && r.state.as_deref() == Some(strings::state_enabled().as_str())),
        "project row"
    );
    assert!(installed.iter().all(|r| r.description == "Formats source files"), "the description is drawn");

    browser.set_tab(ClaudePluginBrowser::MARKETPLACE_TAB);
    let available = browser.rows();
    assert!(available.len() == 1 && available[0].subtitle == "sample · github", "catalog row shows marketplace and source kind");
    assert!(available[0].state.is_none(), "a catalog row has no enabled badge");

    browser.set_marketplace_filter("nowhere");
    assert!(browser.rows().is_empty() && browser.empty_message() == strings::empty_filtered(), "an unmatched filter empties the list");
    browser.set_marketplace_filter("sample");
    assert_eq!(browser.rows().len(), 1, "the filter keeps its own marketplace");
    browser.set_search("FORMAT");
    assert_eq!(browser.rows().len(), 1, "search is case-insensitive over name and description");
    browser.set_search("없는이름");
    assert!(
        browser.rows().is_empty() && browser.empty_message() == strings::empty_filtered(),
        "a search with no hit shows the filtered copy"
    );

    browser.set_search("");
    browser.set_marketplace_filter("");
    assert_eq!(browser.status_text(), None, "a ready list shows no status sentence");
    browser.apply(ClaudePluginSnapshot::of(status::MISSING, strings::detail_missing_cli()));
    assert_eq!(browser.status_text(), Some(strings::detail_missing_cli()), "a failed read explains itself in the macOS words");
    assert_eq!(browser.empty_message(), strings::empty_failed(), "a failed read offers the reload copy");
    assert_eq!(browser.marketplace_filter(), "", "a filter the new list no longer offers falls back to 전체");
}

/// Asking again re-reads the CLI and replaces the list.
#[tokio::test]
async fn reload_reads_again_and_replaces_the_list() {
    korean();
    let folder = text(&temp("claude-reload"));
    let runner = healthy_default(&folder);
    let (reader, _, _) = fixture(&runner, true);
    let browser = ClaudePluginBrowser::new("claude", workspace(&folder));
    browser.apply(reader.snapshot(browser.workspace(), none()).await);
    assert_eq!(browser.installed_count(), 2, "first read");
    runner.set_async(|call, _| {
        Box::pin(async move {
            if call.arguments.iter().any(|a| a == "--version") {
                ok("2.1.271")
            } else if call.arguments.iter().any(|a| a == "marketplace") {
                ok(MARKETS)
            } else {
                ok(r#"{"installed": [], "available": []}"#)
            }
        })
    });
    browser.begin_load();
    browser.apply(reader.snapshot(browser.workspace(), none()).await);
    assert!(browser.installed_count() == 0 && browser.is_ready(), "the reload replaced the list: {}", browser.installed_count());
    assert_eq!(runner.calls().len(), 6, "the reload ran the same three reads again");
    assert!(!mutated(&runner), "a reload changes nothing either");
}

/// The palette offers /plugin for Claude and /plugins for Codex.
#[test]
fn palette_offers_plugin_for_both_providers() {
    let l = Locale::new("ko");
    let opens = |provider: &str, invocation: &str| {
        palette::builtins(provider, &l).iter().any(|c| c.action == Some(SlashCommandAction::OpenPlugins) && c.invocation == invocation)
    };
    assert!(opens("claude", "plugin"), "the Claude palette offers /plugin");
    assert!(opens("codex", "plugins"), "the Codex palette now offers /plugins");
    assert!(
        !palette::builtins("gemini", &l).iter().any(|c| c.action == Some(SlashCommandAction::OpenPlugins)),
        "Antigravity has no plugin browser on macOS either"
    );
    assert!(
        catalog_builtins("codex", &l).iter().any(|c| c.action == Some(SlashCommandAction::OpenPlugins)),
        "the macOS catalog itself is left untouched"
    );
}

/// Korean copy: every PluginStrings field is the macOS literal (ClaudePluginView.swift and ClaudePluginService.swift, by field).
#[test]
fn strings_match_macos() {
    korean();
    let mac_os: HashMap<&str, &str> = HashMap::from([
        ("TitleTemplate", "{provider} 플러그인"),
        ("TabInstalled", "설치됨"),
        ("TabMarketplace", "마켓플레이스"),
        ("TabCountTemplate", "{title} {count}"),
        ("SearchPlaceholder", "이름 또는 설명 검색"),
        ("FilterAll", "전체"),
        ("ButtonReload", "목록 새로고침"),
        ("ButtonClose", "닫기"),
        ("DiagnosticsDisclosure", "명령 실행 상세"),
        ("MarketplaceHelpLink", "마켓플레이스 추가 방법"),
        ("DirectInstall", "직접 설치"),
        ("SubtitleTemplate", "{left} · {right}"),
        ("ScopeLocal", "로컬 · 나만"),
        ("ScopeProject", "프로젝트 · 공유"),
        ("ScopeUser", "사용자 · 전체"),
        ("ScopeManaged", "관리자 관리"),
        ("StateEnabled", "활성"),
        ("StateDisabled", "비활성"),
        ("StateUnknown", "상태 미확인"),
        ("NoDescription", "설명이 제공되지 않았습니다."),
        ("EmptyLoading", "플러그인 목록을 불러오는 중…"),
        ("EmptyFailed", "목록을 불러오지 못했습니다. 목록 새로고침으로 다시 확인하세요."),
        ("EmptyFiltered", "검색 조건에 맞는 플러그인이 없습니다."),
        ("EmptyInstalled", "설치된 플러그인이 없습니다."),
        ("EmptyAvailable", "등록된 마켓플레이스에서 제공한 플러그인이 없습니다."),
        ("ProgressLoading", "목록을 불러오는 중…"),
        ("FooterNote", "현재 폴더의 설정과 저장된 목록입니다. 새 설치는 다음 Claude 실행부터 적용됩니다."),
        (
            "DetailReady",
            "현재 작업 폴더의 CLI 설정과 등록된 마켓플레이스의 캐시 목록입니다. 이미 실행 중인 세션의 로드 상태와 다를 수 있습니다.",
        ),
        ("DetailNoMarketplaces", "등록된 마켓플레이스가 없습니다. Claude CLI에서 marketplace add로 등록한 뒤 목록을 다시 읽으세요."),
        ("DetailCancelled", "플러그인 조회를 취소했습니다."),
        ("DetailFailed", "플러그인 목록을 읽지 못했습니다."),
        ("DetailInvalidWorkspace", "로컬 작업 폴더가 올바르지 않습니다."),
        ("DetailMissingWorkspace", "작업 폴더를 찾지 못했습니다."),
        ("DetailMissingCli", "Claude CLI가 설치되어 있지 않습니다. 먼저 CLI를 설치하세요."),
        ("DetailUnknownVersion", "설치된 Claude CLI 버전을 확인하지 못했습니다."),
        ("DetailUnsupported", "이 플러그인 관리 화면은 JSON 설치 결과를 지원하는 Claude Code 2.1.268 이상이 필요합니다."),
        ("DetailListingFailed", "Claude CLI에서 플러그인 목록을 읽지 못했습니다."),
        ("DetailMarketplacesFailed", "등록된 마켓플레이스 목록을 읽지 못했습니다."),
        ("DetailMalformed", "플러그인 목록 형식 또는 크기가 올바르지 않습니다. 빈 목록으로 처리하지 않았습니다."),
        ("DetailIncomplete", "플러그인 작업을 완료하지 못했습니다. 실행 시간·출력 한도 또는 CLI 접근 상태를 확인하세요."),
        ("ScopePickerLabel", "설치 범위"),
        ("ScopeLocalOption", "로컬 · 이 워크스페이스, 나만"),
        ("ScopeProjectOption", "프로젝트 · 팀과 공유"),
        ("ScopeUserOption", "사용자 · 모든 프로젝트"),
        ("ScopeNoteLocal", "현재 워크스페이스에만 적용하며 팀의 공유 설정은 바꾸지 않습니다."),
        ("ScopeNoteProject", "프로젝트 설정에 기록해 팀과 같은 플러그인을 사용합니다."),
        // OS-bound substitution, recorded in docs/windows-plugins.md: macOS reads "이 Mac의 모든 프로젝트에서 ...".
        ("ScopeNoteUser", "이 PC의 모든 프로젝트에서 사용하는 사용자 설정에 설치합니다."),
        ("ButtonInstall", "설치"),
        ("ButtonInstalling", "설치 중…"),
        ("ButtonMarketplaceRefresh", "마켓플레이스 새로고침"),
        ("ButtonCancelOperation", "작업 취소"),
        ("ButtonCancelling", "취소 중…"),
        ("ProgressInstalling", "플러그인 설치 중…"),
        ("ProgressRefreshing", "마켓플레이스 갱신 중…"),
        ("ProgressCancelling", "작업을 취소하는 중…"),
        ("NoRefreshableMarketplaces", "새로고침할 등록된 마켓플레이스가 없습니다."),
        ("SelectPluginAndScopeAgain", "목록에서 플러그인과 설치 범위를 다시 선택하세요."),
        ("LoadListFirst", "플러그인 목록을 먼저 불러오세요."),
        ("MarketplacesRefreshedTemplate", "{count}개 마켓플레이스의 목록을 갱신했습니다."),
        ("OperationBusy", "다른 플러그인 작업이 진행 중입니다."),
        ("OperationCancelled", "플러그인 작업을 취소했습니다."),
        ("OperationCancelledByUser", "작업을 취소했습니다. 이미 반영된 변경이 있을 수 있어 목록을 다시 확인합니다."),
        ("InstallBadIdOrScope", "플러그인 이름 또는 설치 범위가 올바르지 않습니다."),
        ("InstallNotFound", "현재 등록된 마켓플레이스 목록에서 이 플러그인을 찾지 못했습니다. 목록을 다시 확인하세요."),
        ("InstallSkipped", "선택한 범위에 이미 설치되어 있습니다. 비활성 상태라면 Claude CLI에서 활성화하세요."),
        (
            "InstallCommandRequired",
            "이 플러그인은 추가 명령 실행 동의가 필요합니다. Claude CLI에서 표시된 명령을 확인한 뒤 설치하세요. 앱은 자동 승인하지 않습니다.",
        ),
        ("InstallUnconfirmed", "설치 결과를 확인하지 못했습니다. 목록을 다시 읽어 설치 상태를 확인하세요."),
        ("InstallFailed", "플러그인 설치에 실패했습니다. 네트워크·권한·조직 정책을 확인하세요."),
        ("InstallSucceeded", "플러그인을 설치했습니다. 다음 Claude 실행부터 적용됩니다."),
        ("MarketplaceBadName", "마켓플레이스 이름이 올바르지 않습니다."),
        ("MarketplaceNotRegistered", "등록되지 않은 마켓플레이스입니다. 기존 등록 목록에서 선택하세요."),
        ("MarketplaceRefreshFailed", "마켓플레이스 새로고침에 실패했습니다. 네트워크 상태와 접근 권한을 확인하세요."),
        ("MarketplaceRefreshSucceeded", "선택한 마켓플레이스 목록을 새로고침했습니다."),
    ]);
    let actual = strings::all();
    let mut seen: HashMap<String, &str> = HashMap::new();
    for (name, value) in &actual {
        assert!(!value.is_empty(), "PluginStrings.{name} is empty");
        assert!(!seen.contains_key(value), "PluginStrings.{name} duplicates {}", seen.get(value).unwrap_or(&""));
        seen.insert(value.clone(), name);
        let expected = mac_os.get(name).unwrap_or_else(|| panic!("PluginStrings.{name} mirrors no macOS literal"));
        assert_eq!(value, expected, "PluginStrings.{name} differs from macOS");
    }
    for name in mac_os.keys() {
        assert!(actual.iter().any(|(n, _)| n == name), "PluginStrings is missing {name}");
    }
    assert_eq!(actual.len(), mac_os.len(), "the copy table and the class must hold the same fields");
}

/// The Core half of `WindowIsARealWinUISurfaceWiredIntoTheApp`: the read-only guard judges an automation id one word at a time, and
/// only the two macOS changes (install with its scope, marketplace refresh) are allowed through.
#[test]
fn the_read_only_guard_allows_only_the_macos_changes() {
    for part in
        ["tab-installed", "tab-marketplace", "marketplace-filter", "search", "reload", "close", "diagnostics", "help-link", "status"]
    {
        let id = support::automation_id("claude", part);
        assert!(!support::names_a_change(&id, "claude") || support::names_allowed_change(&id, "claude"), "a read-only control: {part}");
    }
    for change in ["install-fmt", "uninstall", "scope-picker", "marketplace-refresh", "add-marketplace", "enable-fmt"] {
        assert!(
            support::names_a_change(&support::automation_id("claude", change), "claude"),
            "a changing control must be caught: {change}"
        );
    }
    for allowed in ["install-fmt", "scope-picker", "refresh-marketplaces"] {
        assert!(
            support::names_allowed_change(&support::automation_id("claude", allowed), "claude"),
            "the macOS change must be allowed: {allowed}"
        );
    }
    for invented in ["uninstall", "enable-fmt", "disable-fmt", "add-marketplace", "remove-marketplace"] {
        assert!(
            !support::names_allowed_change(&support::automation_id("claude", invented), "claude"),
            "a change macOS does not have must stay refused: {invented}"
        );
    }
    assert!(
        !support::names_allowed_change(&support::automation_id("claude", "reload"), "claude"),
        "a control that changes nothing never counts"
    );
    assert!(!support::names_a_change("codex-plugin-install", "claude"), "one provider's window never judges another's ids");
    assert_eq!(support::automation_id("claude", "reload"), "claude-plugin-reload");
    assert_eq!(ClaudePluginSmokeOutcome::RESULT_KEY, "claudePluginList", "the smoke key is claudePluginList");
}

/// The model shapes the Codex feature and the marketplace feature reuse.
#[test]
fn models_stay_reusable() {
    korean();
    let installed = ClaudeInstalledPlugin {
        plugin_id: "fmt@sample".into(),
        name: "fmt".into(),
        scope: "local".into(),
        project_path: Some("/a".into()),
        ..Default::default()
    };
    let twin = ClaudeInstalledPlugin { project_path: Some("/a/b".into()), ..installed.clone() };
    assert_ne!(installed.id(), twin.id(), "the row key separates two paths");
    assert_ne!(
        ClaudeInstalledPlugin { plugin_id: "a@b".into(), scope: "x|y".into(), ..Default::default() }.id(),
        ClaudeInstalledPlugin { plugin_id: "a@b|x".into(), scope: "y".into(), ..Default::default() }.id(),
        "the row key is length-prefixed so no field can forge another row"
    );
    assert_eq!(ClaudePluginSnapshot::default().status, status::FAILED, "a default snapshot is never ready");
    let operation = ClaudePluginOperationResult::new("succeeded", "d");
    assert!(
        operation.output.is_empty() && operation.status == "succeeded",
        "the operation result shape is ready for the marketplace feature"
    );
    assert_eq!(support::plugin_parts(Some("fmt@sample")), Some(("fmt".into(), "sample".into())), "plugin id split");
    assert!(
        support::plugin_parts(Some("fmt")).is_none()
            && support::plugin_parts(Some("a@b@c")).is_none()
            && support::plugin_parts(Some("!@b")).is_none(),
        "a bad plugin id is refused"
    );
    assert!(
        support::supported_version("2.1.268")
            && support::supported_version("2.2.0 (Claude Code)")
            && !support::supported_version("2.1.267")
            && !support::supported_version("x"),
        "version gate"
    );
    assert_eq!(support::display(Some("a\u{0}b\u{1b}c"), 100), "abc", "control characters never reach the screen");
    assert_eq!(support::display(Some(&"a".repeat(50)), 10).len(), 10, "display text is bounded");
    assert!(
        ClaudePluginBrowser::scope_label("managed") == strings::scope_managed() && ClaudePluginBrowser::scope_label("session") == "session",
        "scope labels"
    );
    assert_eq!(ClaudePluginSmokeOutcome::RESULT_KEY, "claudePluginList", "the smoke key is claudePluginList");
}
