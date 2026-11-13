//! The Mighty view's decisions and blocks (C# MightyViewModelVerification, MightyCode.Core.Tests/MightyViewModelVerification.cs).

mod view_support;

use std::collections::HashSet;
use std::sync::Arc;

use mighty_core::graph::{GraphTokenUsage, MightyGraphRun};
use mighty_core::model::{AppSnapshot, RunSession, Workspace};
use mighty_graph::camera::{Point, Size};
use mighty_graph::layout::{self, Layout, Options};
use mighty_graph::view_model::{self, ResultFilesPanel};
use mighty_graph::{node, support, usage_format};
use view_support::*;

fn pane(kind: &str, provider: &str) -> RunSession {
    RunSession { kind: Some(kind.into()), provider: Some(provider.into()), ..RunSession::default() }
}

/// `MightyGraphViewModel.CanvasLayout(runs, draft, running, {})`: `MightyGraphLayout.Make` as it is.
fn canvas(runs: &[MightyGraphRun], draft: &str, running: bool, options: Options) -> Layout {
    layout::make(runs, draft, running, &HashSet::new(), &options, &ko())
}

/// "mighty view is for claude, codex and antigravity panes only" (ShowsOnlyOnClaudeAndCodexPanes).
#[test]
fn shows_only_on_claude_and_codex_panes() {
    check(view_model::supports_mighty(&pane("claude", "claude")), "claude pane must have the Mighty view");
    check(view_model::supports_mighty(&pane("claude", "codex")), "codex pane must have the Mighty view");
    check(view_model::supports_mighty(&pane("claude", "gemini")), "an Antigravity pane must have the Mighty view");
    check(!view_model::supports_mighty(&pane("shell", "claude")), "shell pane must not have the Mighty view");
    check(!view_model::supports_mighty(&pane("browser", "claude")), "browser pane must not have the Mighty view");
    check(ko().get(view_model::LOCALE_KEY_MIGHTY) == "마이티", "graph.view.mighty must be 마이티");
}

/// "a saved default view loads as mighty" (SavedDefaultLoadsAsMighty).
#[test]
fn saved_default_loads_as_mighty() {
    let workspace = Workspace { path: Some(std::env::temp_dir().to_string_lossy().into_owned()), ..Workspace::default() };
    let load = |p: RunSession| -> RunSession {
        let p = RunSession { workspace_id: workspace.id.clone(), ..p };
        let snapshot = AppSnapshot { version: 1, workspaces: Some(vec![workspace.clone()]), sessions: Some(vec![Arc::new(p)]), ..AppSnapshot::default() };
        mighty_core::normalize::normalize(&snapshot, true).unwrap().sessions()[0].as_ref().clone()
    };
    for saved in [Some("default"), Some("mighty"), None] {
        let loaded = load(RunSession {
            kind: Some("claude".into()),
            provider: Some("codex".into()),
            draft: Some("my composer draft".into()),
            agent_view_mode: saved.map(str::to_string),
            ..RunSession::default()
        });
        check(loaded.agent_view_mode.as_deref() == Some("mighty"), format!("an agent pane saved as {} must load as mighty", saved.unwrap_or("nothing")));
        check(loaded.draft.as_deref() == Some("my composer draft"), "the view mode must not touch the draft");
    }
    check(
        load(RunSession { kind: Some("shell".into()), agent_view_mode: Some("default".into()), ..RunSession::default() }).agent_view_mode.is_none(),
        "a shell has no Mighty view",
    );
}

/// "mighty canvas blocks follow the layout" (CanvasBlocksFollowTheLayout).
#[test]
fn canvas_blocks_follow_the_layout() {
    let mut r = with_agents(
        run("run-1", "Build", "running"),
        vec![agent("agent-1", "Reader", "read", "running"), parent(agent("agent-2", "Writer", "write", "running"), "agent-1")],
    );
    r.provider = Some("claude".into());
    let runs = vec![r.clone()];
    let made = canvas(&runs, "", true, Options::default());
    check(made.nodes.len() >= 3, "running graph must produce at least request + 2 agent blocks");
    let request_id = layout::node_id(&r, "request");
    check(made.nodes.iter().any(|n| n.id == request_id && n.kind == "request"), "canvas must include a request block");
    check(made.nodes.iter().any(|n| n.kind == "agent"), "canvas must include agent blocks");
    check(canvas(&runs, "type here", false, Options::default()).nodes.iter().any(|n| n.kind == "draft"), "draft block must appear when draft is non-empty");
    check(!made.edges.is_empty(), "canvas must have edges connecting blocks");
}

/// "mighty capsule and tooltip match macOS" (CapsuleAndTooltipMatchMacOS).
#[test]
fn capsule_and_tooltip_match_macos() {
    let usage = GraphTokenUsage { input_tokens: 1000, output_tokens: 200, ..GraphTokenUsage::default() };
    let records = vec![node::response_record("resp-1", Some("claude-opus-5"), usage, &["tool-1".to_string()], false)];
    let capsule = usage_format::block_capsule(Some(usage), &records, None, None, false, &ko());
    check(capsule.is_some(), "capsule must not be null when records and usage are present");
    check(capsule.unwrap().contains("claude-opus-5"), "capsule must contain the model name");
    check(usage_format::block_capsule_help(&records, None, false, &ko()).contains("claude-opus-5"), "capsule help must list the model");
    check(
        usage_format::block_capsule(None, &[], Some("claude-opus-5"), None, false, &ko()).as_deref() == Some("claude-opus-5"),
        "with no records capsule must fall back to nodeModelLabel",
    );
    let ko = ko();
    check(ko.get(view_model::LOCALE_KEY_ZOOM_OUT) == "축소", "graph.zoom.out must be 축소");
    check(ko.get(view_model::LOCALE_KEY_ZOOM_RESET) == "실제 크기", "graph.zoom.reset must be 실제 크기");
    check(ko.get(view_model::LOCALE_KEY_ZOOM_IN) == "확대", "graph.zoom.in must be 확대");
    check(ko.get(view_model::LOCALE_KEY_RESULT_FILES_TITLE) == "결과에 나온 파일", "graph.resultFiles.title must be 결과에 나온 파일");
    check(ko.get(view_model::LOCALE_KEY_RESULT_FILES_CLOSE) == "파일 목록 닫기", "graph.resultFiles.closeButton must be 파일 목록 닫기");
    check(ko.get(view_model::LOCALE_KEY_BLOCK_SCROLLING) == "블록 스크롤", "graph.block.scrolling must be 블록 스크롤");
    check(ko.get(view_model::LOCALE_KEY_RESULT_FILES_OPEN) == "결과에 나온 파일 보기", "graph.resultFiles.openButton must be 결과에 나온 파일 보기");
    check(
        ko.get_with(view_model::LOCALE_KEY_RESULT_FILES_COUNT, &[("count", "3")]) == "결과 파일 3개 · 목록 토글",
        "graph.resultFiles.countLabel must be 결과 파일 3개 · 목록 토글",
    );
}

/// "mighty zoom steps match macOS" (ZoomStepsMatchMacOS).
#[test]
fn zoom_steps_match_macos() {
    let levels = view_model::zoom_levels();
    check(levels[0] == 0.5, "zoom must start at 0.5 (50%)");
    check(*levels.last().unwrap() == 1.5, "zoom must end at 1.5 (150%)");
    check(levels.len() == 11, "zoom must have 11 levels from 50% to 150% in 10% steps");
    check(view_model::zoom_in_disabled(1.5), "zoom-in must be disabled at max");
    check(!view_model::zoom_in_disabled(1.4), "zoom-in must be enabled below max");
    check(view_model::zoom_out_disabled(0.5), "zoom-out must be disabled at min");
    check(!view_model::zoom_out_disabled(0.6), "zoom-out must be enabled above min");
    check(view_model::zoom_in(1.0) == 1.1, "zoom-in from 100% must give 110%");
    check(view_model::zoom_out(1.0) == 0.9, "zoom-out from 100% must give 90%");
    check(view_model::zoom_in(1.5) == 1.5, "zoom-in at max must stay at max");
    check(view_model::zoom_out(0.5) == 0.5, "zoom-out at min must stay at min");
    check(view_model::ZOOM_DEFAULT == 1.0, "default zoom must be 1.0 (100%)");
    check(view_model::zoom_label(1.0) == "100%", "100% zoom must format as '100%'");
    check(view_model::zoom_label(0.5) == "50%", "50% zoom must format as '50%'");
    check(view_model::zoom_label(1.5) == "150%", "150% zoom must format as '150%'");
}

/// "mighty selection routes the wheel" (SelectionRoutesTheWheel).
#[test]
fn selection_routes_the_wheel() {
    check(view_model::apply_selection(None, Some("node-1")).as_deref() == Some("node-1"), "clicking a block must select it");
    check(view_model::apply_selection(Some("node-1"), Some("node-2")).as_deref() == Some("node-2"), "clicking another block must select it");
    check(view_model::apply_selection(Some("node-1"), None).is_none(), "clicking empty background must clear selection");
    check(view_model::wheel_scrolls_block(Some("node-1")), "selected block must capture the wheel");
    check(!view_model::wheel_scrolls_block(None), "no selection must let wheel pan canvas");
    check(view_model::wheel_pan(120.0, false, false) == Point::new(0.0, 120.0), "the vertical wheel must pan up and down");
    check(view_model::wheel_pan(120.0, true, false) == Point::new(-120.0, 0.0), "a tilt to the right must pan left and right");
    check(view_model::wheel_pan(-120.0, false, true) == Point::new(-120.0, 0.0), "Shift with the wheel down must pan like a tilt to the right");
    check(view_model::wheel_pan(120.0, true, true) == Point::new(-120.0, 0.0), "Shift must not flip a horizontal wheel");
}

fn sources(list: &[(&str, bool)]) -> Vec<(String, bool)> {
    list.iter().map(|(id, has)| (id.to_string(), *has)).collect()
}

/// "mighty result files panel rules match macOS" (ResultFilesPanelRulesMatchMacOS).
#[test]
fn result_files_panel_rules_match_macos() {
    let mut panel = ResultFilesPanel::new();
    panel.publish(&sources(&[("run-1", true), ("run-2", false)]));
    check(panel.selected_run_id() == Some("run-1"), "the newest result that names files must open, not a newer one that names none");
    panel.publish(&sources(&[("run-1", true), ("run-2", true)]));
    check(panel.selected_run_id() == Some("run-2"), "a newer result with files must open");
    panel.toggle("run-1", true);
    check(panel.selected_run_id() == Some("run-1"), "an older result must open its own list");
    panel.publish(&sources(&[("run-1", true), ("run-2", true)]));
    check(panel.selected_run_id() == Some("run-1"), "the newest result opens by itself once only: the user's choice stays");
    panel.toggle("run-3", false);
    check(panel.selected_run_id() == Some("run-1"), "a result that names no file opens nothing");
    panel.toggle("run-1", true);
    check(panel.selected_run_id().is_none(), "the open result's toggle must close its list");
    panel.toggle("run-2", true);
    panel.close();
    panel.publish(&sources(&[("run-1", true), ("run-2", true)]));
    check(panel.selected_run_id().is_none(), "a manual close must stick for the same results");
    panel.publish(&sources(&[("run-1", true), ("run-2", true), ("run-3", true)]));
    check(panel.selected_run_id() == Some("run-3"), "a new result with files must open even after a manual close");
    panel.publish(&sources(&[("run-1", true), ("run-3", false)]));
    check(panel.selected_run_id() == Some("run-1"), "a list with no files left closes, and the newest result that still names files opens");
    panel.publish(&[]);
    check(panel.selected_run_id().is_none(), "no completed run means no panel");
    check(view_model::wheel_scroll_target(Some("node-1"), Some("node-1"), false).as_deref() == Some("node-1"), "the selected block under the pointer scrolls");
    check(
        view_model::wheel_scroll_target(Some("node-1"), Some("node-2"), false).is_none() && view_model::wheel_scroll_target(Some("node-1"), None, false).is_none(),
        "with the pointer elsewhere the wheel pans, selection or not",
    );
    check(
        view_model::wheel_scroll_target(None, Some("files"), true).as_deref() == Some("files")
            && view_model::wheel_scroll_target(Some("node-1"), Some("files"), true).as_deref() == Some("files"),
        "an attachment scrolls with no selection",
    );
    check(
        layout::is_auxiliary("resultFiles")
            && layout::is_auxiliary("planRecord")
            && layout::is_auxiliary(layout::EXECUTION_KIND)
            && layout::is_auxiliary(layout::IMAGES_KIND)
            && !layout::is_auxiliary("result")
            && !layout::is_auxiliary(layout::WORK_KIND)
            && !layout::is_auxiliary("history"),
        "the attachments are the file list, the answered plan, the execution and the pictures",
    );
}

/// "mighty indicators respect animations off" (IndicatorsRespectAnimationsOff).
#[test]
fn indicators_respect_animations_off() {
    check(view_model::block_indicator("running", true) == "animating", "running + animations on must be animating");
    check(view_model::block_indicator("running", false) == "static", "running + animations off must be static");
    check(view_model::block_indicator("waiting", true) == "waiting", "waiting + animations on must be waiting");
    check(view_model::block_indicator("waiting", false) == "waiting", "waiting + animations off must still be waiting");
    for status in ["completed", "error", "stopped", "idle"] {
        check(view_model::block_indicator(status, true) == "none", format!("{status} + animations on must be none"));
        check(view_model::block_indicator(status, false) == "none", format!("{status} + animations off must be none"));
    }
}

/// "mighty view model exposes the WinUI surface" (ExposesWinUiSurface).
#[test]
fn exposes_win_ui_surface() {
    check(view_model::ZOOM_MIN == 0.5, "ZoomMin must be 0.5");
    check(view_model::ZOOM_MAX == 1.5, "ZoomMax must be 1.5");
    check(view_model::ZOOM_DEFAULT == 1.0, "ZoomDefault must be 1.0");
    check(view_model::ZOOM_STEP == 0.1, "ZoomStep must be 0.1");
    for key in [
        view_model::LOCALE_KEY_MIGHTY,
        view_model::LOCALE_KEY_ZOOM_OUT,
        view_model::LOCALE_KEY_ZOOM_RESET,
        view_model::LOCALE_KEY_ZOOM_IN,
        view_model::LOCALE_KEY_RESULT_FILES_TITLE,
        view_model::LOCALE_KEY_RESULT_FILES_CLOSE,
        view_model::LOCALE_KEY_BLOCK_SCROLLING,
        view_model::LOCALE_KEY_RESULT_FILES_OPEN,
        view_model::LOCALE_KEY_RESULT_FILES_COUNT,
    ] {
        check(!key.is_empty() && key.contains('.'), format!("locale key must be a dotted path: {key}"));
    }
    check(view_model::supports_mighty(&pane("claude", "claude")), "SupportsMighty must exist and work");
    check(view_model::zoom_in(1.0) > 1.0, "ZoomIn must increase zoom");
    check(view_model::zoom_out(1.0) < 1.0, "ZoomOut must decrease zoom");
    check(!view_model::zoom_label(1.0).is_empty(), "ZoomLabel must return a string");
}

/// "mighty canvas lays out a nested delegation run" (LaysOutNestedDelegationRun). Its result-file listing (`FilesFor`, which reads the
/// workspace through `ResultFiles.In`) belongs to `result_files`; the rest is here.
#[test]
fn lays_out_nested_delegation_run() {
    let mut l2 = parent(agent("agent-l2", "L2", "Level 2", "completed"), "agent-l1");
    l2.entries = Some(vec![entry(&mighty_core::wire::new_id(), "assistant", "Deep answer", "2026-01-01T00:00:00Z", Some("claude"))]);
    let mut r = with_final(with_agents(run("delivered-run", "Nested task", "completed"), vec![agent("agent-l1", "L1", "Level 1", "completed"), l2]), "Root done");
    r.provider = Some("claude".into());
    let runs = vec![r.clone()];
    let made = canvas(&runs, "", false, Options::default());
    check(made.nodes.len() >= 3, format!("canvas must have at least 3 blocks; got {}", made.nodes.len()));
    check(made.edges.len() >= 2, format!("canvas must have at least 2 edges; got {}", made.edges.len()));
    let kinds: HashSet<&str> = made.nodes.iter().map(|n| n.kind.as_str()).collect();
    check(kinds.len() >= 2, format!("canvas must have at least 2 distinct block kinds: {kinds:?}"));
    let levels = view_model::zoom_levels();
    check((levels[0] * 100.0).round() as i64 == 50, "min zoom must be 50%");
    check((view_model::ZOOM_DEFAULT * 100.0).round() as i64 == 100, "default zoom must be 100%");
    check((levels.last().unwrap() * 100.0).round() as i64 == 150, "max zoom must be 150%");
    let blocks = view_model::blocks(&made, &runs, "", "Claude", true, None, None, &ko());
    check(blocks.len() == made.nodes.len(), format!("every layout node must become a block; got {}", blocks.len()));
    let request = blocks.iter().find(|b| b.kind == "request").unwrap();
    check(request.title == "요청 1 · Claude", format!("request title must read 요청 1 · Claude; got {}", request.title));
    check(request.request == "Nested task", "the request block must carry the request text");
    check(request.state == "완료", format!("a completed run's request block must read 완료; got {}", request.state));
    let result = blocks.iter().find(|b| b.kind == "result").unwrap();
    check(result.title == "최종 결과", format!("a completed run's result block must read 최종 결과; got {}", result.title));
    check(blocks.iter().filter(|b| b.kind == "agent").count() == 2, "both sub-agent blocks must be present");
    check(blocks.iter().all(|b| b.indicator == "none"), "a finished graph must show no motion");
    let summary = view_model::toolbar_summary(&runs, &ko());
    check(summary.starts_with("요청 1 · 하위 에이전트 2"), format!("toolbar total must read 요청 1 · 하위 에이전트 2; got {summary}"));

    let mut mixed = with_final(
        with_agents(
            run("delivered-kinds", "Mixed", "completed"),
            vec![kind(agent("k-task", "", "", "completed"), "task"), kind(agent("k-steer", "", "", "completed"), "steer"), kind(agent("k-compact", "", "", "completed"), "compact")],
        ),
        "done",
    );
    mixed.provider = Some("claude".into());
    let mixed_runs = vec![mixed];
    let mixed_layout = canvas(&mixed_runs, "", false, Options::default());
    let mixed_kinds: HashSet<String> = view_model::blocks(&mixed_layout, &mixed_runs, "", "Claude", true, None, None, &ko()).into_iter().map(|b| b.kind).collect();
    for k in ["request", "task", "steer", "compact", "result"] {
        check(mixed_kinds.contains(k), format!("the canvas must carry a {k} block; got {mixed_kinds:?}"));
    }
    let drafted = canvas(&[], "next", false, Options::default());
    let draft = view_model::blocks(&drafted, &[], "next", "Claude", true, None, None, &ko()).into_iter().find(|b| b.kind == "draft").unwrap();
    check(draft.title == "첫 요청" && draft.state == "작성 중", format!("the draft block must read 첫 요청 / 작성 중; got {} / {}", draft.title, draft.state));

    let mut filed = with_final(run("delivered-files", "x", "completed"), "wrote docs/note.md and /etc/passwd.txt");
    filed.provider = Some("claude".into());
    support::refresh_result(&mut filed);
    check(
        view_model::result_texts(&filed) == vec!["wrote docs/note.md and /etc/passwd.txt".to_string(), "wrote docs/note.md and /etc/passwd.txt".to_string()],
        "the result's texts are what its files are read from",
    );
    let filed_runs = vec![filed];
    check(view_model::latest_completed_run(&filed_runs).map(|r| r.id()) == Some("delivered-files"), "the newest completed run must be found");
    let panel_layout = canvas(
        &filed_runs,
        "",
        false,
        Options { result_files_run_id: Some("delivered-files".into()), viewport: Some(Size::new(1200.0, 800.0)), ..Options::default() },
    );
    let panel_blocks = view_model::blocks(&panel_layout, &filed_runs, "", "Claude", true, None, None, &ko());
    check(panel_blocks.len() == panel_layout.nodes.len(), "every layout node must become a block, the files panel included");
    check(panel_blocks.iter().any(|b| b.kind == "resultFiles" && b.title == "결과에 나온 파일"), "the open panel must draw as a 결과에 나온 파일 block");
}
