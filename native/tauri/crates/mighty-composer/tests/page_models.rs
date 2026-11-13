//! The page-facing models, checked two ways. Golden cases built from the C# builders' source (MainWindow.WebShell.Composer.cs,
//! MainWindow.cs, MainWindow.QueuedComposer.cs, MainWindow.WebShell.Cards.cs; file and line in each test's name and doc): the
//! WinUI builders read WinUI controls and cannot be compiled without WinUI, so each expected JSON is the C# anonymous object or
//! record written out by hand, field for field, in its order. And the field names against the page's own TypeScript types
//! (`src/composer/types.ts`, `src/composer/menus.ts`, `src/cards/types.ts`), parsed here.

mod common;

use std::collections::BTreeSet;
use std::path::Path;

use common::ko;
use mighty_composer::cards::{
    self, BackgroundTask, CardInputs, LoginInput, LoginJob, PermissionRequest, PlanDraft, StatusLineInput, StatusRun,
};
use mighty_composer::composer::{self, ComposerInputs, HeldItem, PaneViewState, StyleRow, WindowRules};
use mighty_composer::menus::{self, MenuAction, MenuInputs, MenuRow};
use mighty_composer::queue::{ComposerSecondSend, QueuedComposerInput};
use mighty_composer::runtime::{CatalogModel, ModelCatalog, ProviderCapabilities, ProviderRuntime};
use mighty_core::locale::Locale;
use mighty_core::model::{BackgroundTask as CoreTask, BackgroundWork, LogEntry, PlanRecord, RunSession, RunSettings, Workspace};
use mighty_core::time::DateTimeOffset;
use serde_json::{Value, json};

fn now() -> DateTimeOffset {
    mighty_core::time::parse(Some("2027-01-15T08:10:00.0000000+00:00")).unwrap()
}

fn pane(kind: &str, status: &str) -> RunSession {
    RunSession {
        id: Some("p1".into()),
        workspace_id: Some("w1".into()),
        kind: Some(kind.into()),
        status: Some(status.into()),
        model: Some("default".into()),
        provider: Some("claude".into()),
        settings: Some(RunSettings::default()),
        logs: Some(vec![]),
        created_at: Some("2027-01-15T08:00:00.0000000+00:00".into()),
        ..RunSession::default()
    }
}

fn workspace() -> Workspace {
    Workspace { id: Some("w1".into()), path: Some("C:\\work".into()), ..Workspace::default() }
}

fn claude_runtime() -> ProviderRuntime {
    ProviderRuntime {
        name: "Claude Code".into(),
        available: true,
        detail: String::new(),
        catalog: ModelCatalog {
            source: "cli".into(),
            models: vec![
                CatalogModel {
                    value: "default".into(),
                    display_name: "Default".into(),
                    description: "The CLI's choice".into(),
                    resolved_model: None,
                },
                CatalogModel { value: "opus".into(), display_name: "Opus".into(), description: String::new(), resolved_model: None },
            ],
            notice: None,
        },
        capabilities: ProviderCapabilities {
            effort: true,
            permission_modes: ["plan", "manual", "acceptEdits", "auto", "fullAccess"].map(String::from).to_vec(),
            max_turns: true,
            max_budget_usd: true,
            resume: true,
            fast_mode: false,
            web_search: false,
            network_access: false,
            attachments: true,
        },
    }
}

fn efforts() -> Vec<String> {
    ["low", "medium", "high", "xhigh", "max"].map(String::from).to_vec()
}

fn inputs<'a>(
    pane: &'a RunSession,
    ws: &'a Workspace,
    runtime: Option<&'a ProviderRuntime>,
    levels: &'a [String],
    view: &'a PaneViewState,
    window: &'a WindowRules,
) -> ComposerInputs<'a> {
    ComposerInputs { locale: ko(), now: now(), pane, workspace: Some(ws), runtime, efforts: levels, view, window }
}

fn same(actual: &impl serde::Serialize, expected: Value) {
    let actual = serde_json::to_string(actual).unwrap();
    let expected = serde_json::to_string(&expected).unwrap();
    assert_eq!(actual, expected, "\nactual:   {actual}\nexpected: {expected}");
}

fn pills(l: &Locale, model: &str, tip_tail: &str) -> Value {
    json!({
        "provider": "claude", "model": model, "modelTip": format!("Claude · {model}{tip_tail}"),
        "showsEffort": true, "effort": "Auto", "effortEnabled": true,
        "effortTip": format!("{} · {}", l.get("composer.effort.label"), l.get("settings.run.appliesNextRequest")),
        "permission": "Always ask", "permissionHelp": l.get("composer.permissionHelp.manual"), "unlocked": false,
        "showsFast": false, "fast": false, "fastEnabled": false, "moreOn": false, "resuming": false,
    })
}

/// MainWindow.WebShell.Composer.cs:560 `WebComposerState` with MainWindow.cs:592 `RefreshComposerState`: an idle local Claude pane
/// whose CLI was read, with words in its editor, can send them.
#[test]
fn web_composer_state_cs560_an_idle_claude_pane_can_send() {
    let l = ko();
    let (pane, ws, runtime, levels) = (pane("claude", "idle"), workspace(), claude_runtime(), efforts());
    let view = PaneViewState { draft: "hello".into(), ..Default::default() };
    let window = WindowRules::default();
    let i = inputs(&pane, &ws, Some(&runtime), &levels, &view, &window);
    let model = mighty_core::model_label::selection(&pane, &runtime.catalog.options(), &l);
    same(
        &composer::composer_state(&i),
        json!({
            "paneId": "p1", "kind": "claude", "busy": false, "starting": false, "stopping": false, "canSend": true,
            "checkingModels": false, "attachmentsLoading": false, "placeholderKey": "composer.placeholder.idle", "install": false,
            "primary": "send", "primaryEnabled": true, "worktree": false, "worktreeMenuOnly": false, "worktreeEnabled": false,
            "queue": { "held": [], "items": [] }, "attachments": [], "attachmentsAllowed": true,
            "pills": pills(&l, &model, "\nThe CLI's choice"),
            "header": { "word": l.get("session.state.idle"), "tone": "idle", "timed": false, "rest": "", "help": "Claude Code" },
            "resumed": false, "toggles": { "statusLine": false }, "askTargets": [],
        }),
    );
}

/// MainWindow.QueuedComposer.cs:133-154 `RefreshQueuedComposer` and :290-313 `RunNowPane`/`RunNowRows`, read by
/// MainWindow.WebShell.Composer.cs:566-587: a running pane queues the draft, offers a worktree, and decides 바로 실행 per row.
#[test]
fn web_composer_state_cs566_a_running_pane_queues_and_offers_a_worktree() {
    let l = ko();
    let (mut pane, ws, runtime, levels) = (pane("claude", "running"), workspace(), claude_runtime(), efforts());
    pane.resume_id = Some("resume-1".into());
    let queued = QueuedComposerInput {
        id: "q-1".into(),
        text: "queued 1".into(),
        attachments: vec![],
        permission_mode_override: None,
        prepared: false,
        continuation: false,
    };
    let view = PaneViewState { draft: "fix it".into(), queue: vec![queued], ..Default::default() };
    let window = WindowRules {
        second_send: ComposerSecondSend::Worktree,
        held_items: vec![HeldItem { id: "n-1".into(), text: "Notice".into() }, HeldItem { id: "n-2".into(), text: "Notice 2".into() }],
        status_line_enabled: true,
        style: Some(StyleRow { label: "Plan".into(), badge: None, hint: "Plans first".into(), enabled: true }),
        ..WindowRules::default()
    };
    let i = inputs(&pane, &ws, Some(&runtime), &levels, &view, &window);
    let model = mighty_core::model_label::selection(&pane, &runtime.catalog.options(), &l);
    let mut expected_pills = pills(&l, &model, "\nThe CLI's choice");
    expected_pills["resuming"] = json!(true);
    same(
        &composer::composer_state(&i),
        json!({
            "paneId": "p1", "kind": "claude", "busy": true, "starting": false, "stopping": false, "canSend": false,
            "checkingModels": false, "attachmentsLoading": false, "placeholderKey": "composer.placeholder.busyWorktree", "install": false,
            "primary": "queue", "primaryEnabled": true, "worktree": true, "worktreeMenuOnly": false, "worktreeEnabled": true,
            "queue": {
                "held": [ { "id": "n-1", "text": "Notice", "runNow": "held" }, { "id": "n-2", "text": "Notice 2" } ],
                "items": [ { "id": "q-1", "text": "queued 1", "attachments": [], "runNow": "worktree" } ],
            },
            "attachments": [], "attachmentsAllowed": true, "pills": expected_pills,
            "style": { "label": "Plan", "hint": "Plans first", "enabled": true },
            "header": { "word": l.get("session.state.running"), "tone": "run", "timed": false, "rest": "", "help": "Claude Code" },
            "resumed": true, "toggles": { "statusLine": true }, "askTargets": [],
        }),
    );
}

/// MainWindow.cs:607-613: a Claude pane whose CLI was not read yet says why the draft waits, offers the install, and cannot send.
#[test]
fn refresh_composer_state_cs607_no_runtime_blocks_with_the_install_row() {
    let l = ko();
    let (pane, ws, levels) = (pane("claude", "idle"), workspace(), efforts());
    let view = PaneViewState { draft: "hi".into(), ..Default::default() };
    let window = WindowRules { setup_item_for_missing_cli: true, ..WindowRules::default() };
    let state = composer::composer_state(&inputs(&pane, &ws, None, &levels, &view, &window));
    let reason = l.get_with("composer.hint.draftStillAllowed", &[("reason", &l.get("composer.hint.refreshRuntime"))]);
    assert_eq!(state.blocked.as_deref(), Some(reason.as_str()));
    assert!(state.install && !state.can_send && state.primary == "send" && !state.primary_enabled);
    // The fallback list names the model and its description (ProviderCatalog.Fallback, ProviderCatalog.cs:74).
    let pills = state.pills.unwrap();
    assert_eq!(
        pills.model_tip,
        format!("Claude · {} · ...", pills.model).replace(" · ...", &format!("\n{}", l.get("provider.fallback.defaultDescription")))
    );
}

/// MainWindow.WebShell.Composer.cs:590-604: a shell pane has no pills, no toggles and no attachments.
#[test]
fn web_composer_state_cs590_a_shell_pane_has_no_pills() {
    let (pane, ws, levels) = (pane("shell", "idle"), workspace(), efforts());
    let view = PaneViewState { draft: "ls".into(), ..Default::default() };
    let window = WindowRules::default();
    let state = composer::composer_state(&inputs(&pane, &ws, None, &levels, &view, &window));
    let value = serde_json::to_value(&state).unwrap();
    assert!(value.get("pills").is_none() && value.get("toggles").is_none() && value.get("blocked").is_none());
    assert!(state.can_send && state.placeholder_key == "composer.placeholder.shell" && !state.attachments_allowed);
}

/// MainWindow.WebShell.Composer.cs:570-578: a pane held for a CLI update with a manual block queues nothing, names the CLI that
/// holds it, and says the block.
#[test]
fn web_composer_state_cs571_a_held_pane_says_why_and_queues_nothing() {
    let (pane, ws, runtime, levels) = (pane("claude", "idle"), workspace(), claude_runtime(), efforts());
    let view = PaneViewState { draft: "go".into(), ..Default::default() };
    let window = WindowRules {
        background_update_holds: true,
        manual_mutation_block: Some("Updating Claude".into()),
        setup_item_for_missing_cli: true,
        ..WindowRules::default()
    };
    let state = composer::composer_state(&inputs(&pane, &ws, Some(&runtime), &levels, &view, &window));
    assert!(state.busy && state.primary == "queue" && !state.primary_enabled && !state.worktree_enabled);
    assert_eq!(state.hold.as_deref(), Some("Claude"));
    assert_eq!(state.blocked.as_deref(), Some("Updating Claude"));
    assert!(!state.install, "a manual block offers no install");
}

/// MainWindow.WebShell.Composer.cs:545-553 `WebHeader` with MainWindow.PaneHeader.cs:303 `HeaderFigures`: waiting requests name
/// themselves; the figures after the clock are the context, the cost and the tools since the request.
#[test]
fn web_header_cs545_the_word_and_the_figures() {
    let l = ko();
    let (mut pane, ws, runtime, levels) = (pane("claude", "running"), workspace(), claude_runtime(), efforts());
    let mut entries = vec![LogEntry::new("u".into(), "user", "do it".into(), "2027-01-15T08:00:00.0000000+00:00".into(), None)];
    let activity = mighty_core::model::AgentActivity {
        id: "a1".into(),
        provider: "claude".into(),
        kind: "tool".into(),
        state: "completed".into(),
        summary: "Read".into(),
        tool_name: None,
        output: None,
        duration_ms: None,
    };
    entries.push(LogEntry { activity: Some(activity), ..LogEntry::new("t".into(), "system", "Read".into(), String::new(), None) });
    pane.logs = Some(entries);
    pane.session_usage = mighty_core::model::SessionUsage::from_json(&json!({
        "provider": "claude", "source": "stream", "tokenScope": "session", "contextUsedTokens": 425, "contextWindowTokens": 1000,
        "costUSD": 0.125, "updatedAt": "2027-01-15T08:05:00.0000000+00:00"
    }));
    let view = PaneViewState::default();
    let window = WindowRules { pending_requests: 2, ..WindowRules::default() };
    let i = inputs(&pane, &ws, Some(&runtime), &levels, &view, &window);
    let header = composer::header(&i);
    let named = |key: &str, value: &str| l.get_with("pane.hero.figure", &[("label", &l.get(key)), ("value", value)]);
    assert_eq!(header.word, l.get_with("phone.card.attention", &[("count", "2")]));
    assert_eq!(header.tone, "wait");
    assert_eq!(header.rest, format!("43% · $0.13 · {}", named("phone.session.hero.tools", "1")));
    let pills = composer::pills(&i);
    assert_eq!(pills.context_percent, Some(42.0), "Math.Round rounds half to even: 42.5 is 42");
    assert!(serde_json::to_string(&pills).unwrap().contains("\"contextPercent\":42,"), "a whole double is written without a fraction");
}

// ── the cards ───────────────────────────────────────────────────────────────────────────────────────────────────────────

struct CardWorld {
    pane: RunSession,
    requests: Vec<PermissionRequest>,
    rows: Vec<BackgroundTask>,
    web: Vec<(String, String)>,
    login: Option<LoginInput>,
    status: Option<StatusLineInput>,
    draft: Option<PlanDraft>,
}

impl CardWorld {
    fn new(p: RunSession) -> Self {
        CardWorld { pane: p, requests: vec![], rows: vec![], web: vec![], login: None, status: None, draft: None }
    }
    fn inputs(&self, busy: bool, styled: bool, diagram_draws_plan: bool, open: bool) -> CardInputs<'_> {
        CardInputs {
            locale: ko(),
            utc_offset_minutes: 540,
            pane: &self.pane,
            busy,
            styled,
            style_draws_tasks: false,
            diagram_draws_plan,
            requests: &self.requests,
            plan_draft: self.draft.as_ref(),
            background_open: open,
            background_rows: &self.rows,
            antigravity: None,
            terminal_prompt: None,
            web_open: &self.web,
            login: self.login.as_ref(),
            status_line: self.status.as_ref(),
        }
    }
}

/// MainWindow.WebShell.Cards.cs:391-414 and :463-494: on an idle pane the next actions come first, then the answered plans newest
/// first, then the first waiting request's card (a Bash permission, counting the other waiting one), the web prompt and the status
/// line, each record's fields in its order with `kind` last.
#[test]
fn web_cards_cs391_in_the_composers_order() {
    let l = ko();
    let mut p = pane("claude", "idle");
    p.logs = Some(vec![LogEntry::new("a1".into(), "assistant", "done\n◆ ok → next: `ooo run` or Review".into(), String::new(), None)]);
    let record = |id: &str, outcome: &str, plan: &str| PlanRecord {
        id: Some(id.into()),
        run_id: Some("r".into()),
        plan: Some(plan.into()),
        received_at: Some("2027-01-15T08:05:00.000Z".into()),
        decided_at: Some("2027-01-15T08:06:00.000Z".into()),
        outcome: Some(outcome.into()),
        feedback: Some(String::new()),
        plan_truncated: None,
        graph_run_id: None,
        launch_override: None,
    };
    p.plan_history = Some(vec![record("old", "approvedAuto", "# First\nbody"), record("new", "revised", "\n## Second plan\n")]);
    let mut world = CardWorld::new(p);
    let mut bash = PermissionRequest::new(
        "perm-1",
        "p1",
        "tu-1",
        "Bash",
        r#"{"command":"ls /tmp","description":"List","timeout":5000,"run_in_background":false}"#,
        "Run command",
    );
    bash.blocked_path = Some("C:\\secret".into());
    world.requests = vec![bash, PermissionRequest::new("perm-2", "p1", "tu-2", "Read", r#"{"file_path":"a.txt"}"#, "Read")];
    world.web = vec![("web-1".into(), "https://example.com/".into())];
    world.status = Some(StatusLineInput {
        config: Some(("Workspace".into(), "status.sh".into())),
        untrusted: None,
        lines: Some(vec![vec![
            StatusRun {
                text: "main".into(),
                color: Some("#ff0000".into()),
                bright_black: false,
                dim: false,
                bold: true,
                italic: false,
                underline: false,
            },
            StatusRun { text: " · ".into(), color: None, bright_black: true, dim: true, bold: false, italic: false, underline: false },
        ]]),
        error_text: None,
    });
    let (cards, ticks) = cards::cards(&world.inputs(false, false, false, false));
    assert!(!ticks);
    let field = |key: &str, value: &str, code: bool| json!({ "label": l.get(key), "value": value, "code": code });
    same(
        &cards,
        json!([
            { "entryId": "a1", "actions": ["ooo run", "Review"], "kind": "nextActions" },
            { "records": [
                { "id": "new", "outcomeTitle": l.get("plan.outcome.revised"), "tone": "wait", "time": "17:06",
                  "received": l.get_with("plan.card.received", &[("time", "17:05")]), "headline": "Second plan", "plan": "\n## Second plan\n", "truncated": false },
                { "id": "old", "outcomeTitle": l.get("plan.outcome.approvedAuto"), "tone": "done", "time": "17:06",
                  "received": l.get_with("plan.card.received", &[("time", "17:05")]), "headline": "First", "plan": "# First\nbody", "truncated": false },
              ], "kind": "planHistory" },
            { "requestId": "perm-1", "title": l.get("permission.bar.title").replace("{title}", &l.get("permission.tool.bash")), "tool": "Bash",
              "waiting": l.get("phone.questionnaire.waiting").replace("{count}", "2"), "headline": "List",
              "fields": [ field("permission.field.command", "ls /tmp", true), field("permission.field.timeout", "5000", false),
                          field("permission.field.runInBackground", &l.get("permission.value.no"), false) ],
              "path": l.get("permission.bar.blockedPath").replace("{path}", "C:\\secret"), "canAllow": true,
              "inputJson": r#"{"command":"ls /tmp","description":"List","timeout":5000,"run_in_background":false}"#,
              "rawJson": l.get("styles.approval.raw"), "onceOnly": l.get("permission.bar.thisRequestOnly"),
              "deny": l.get("permission.deny"), "allowOnce": l.get("permission.allowOnce"), "kind": "permission" },
            { "requestId": "web-1", "url": "https://example.com/", "kind": "webOpen" },
            { "lines": [[
                { "text": "main", "color": "#ff0000", "dim": false, "bold": true, "italic": false, "underline": false },
                { "text": " · ", "ink": "ink2", "dim": true, "bold": false, "italic": false, "underline": false },
              ]], "tip": "statusLine · Workspace · status.sh", "label": l.get("composer.statusLine.name"), "kind": "statusLine" },
        ]),
    );
    // A busy pane shows no next actions (:409), and the diagram keeps the answered plans (:428, PlanCardSupport.ShowsHistoryStrip).
    let (busy, _) = cards::cards(&world.inputs(true, false, false, false));
    assert_eq!(busy.first().map(|c| c.kind()), Some("planHistory"));
}

/// MainWindow.WebShell.Cards.cs:476-485 and :460: a plan's card is its answers only while the diagram draws it, and under a style
/// the waiting question is drawn first.
#[test]
fn web_request_card_cs476_plan_and_question() {
    let l = ko();
    let mut world = CardWorld::new(pane("claude", "running"));
    let mut plan = PermissionRequest::new("plan-1", "p1", "tu", "ExitPlanMode", r##"{"plan":"# Smoke plan\n\n1. First"}"##, "plan");
    plan.can_allow = false;
    plan.can_answer_plan = true;
    plan.received_at = Some("2027-01-15T08:05:00.000Z".into());
    let question_input = r#"{"questions":[{"header":"Color","question":"Which colour?","multiSelect":false,"options":[{"label":"Red","description":"warm"},{"label":"Blue","description":"cool"}]}]}"#;
    let mut question = PermissionRequest::new("q-1", "p1", "tu2", "AskUserQuestion", question_input, "Ask");
    question.can_allow = false;
    question.can_answer_questions = true;
    world.requests = vec![plan, question];
    world.draft = Some(PlanDraft { request_id: "plan-1".into(), sending: true, error: Some("closed".into()) });
    let card = |styled: bool, diagram: bool| {
        serde_json::to_value(cards::request_card(&world.inputs(true, styled, diagram, false)).unwrap()).unwrap()
    };
    assert_eq!(
        card(false, true),
        json!({ "requestId": "plan-1", "count": 2, "received": l.get_with("plan.card.received", &[("time", "17:05")]), "plan": "# Smoke plan\n\n1. First",
                "actionsOnly": true, "sending": true, "error": "closed", "kind": "plan" })
    );
    assert_eq!(card(false, false)["actionsOnly"], false);
    assert_eq!(
        card(true, true),
        json!({ "requestId": "q-1", "count": 2, "questions": [ { "header": "Color", "question": "Which colour?", "multiSelect": false,
                "options": [ { "label": "Red", "description": "warm" }, { "label": "Blue", "description": "cool" } ] } ], "kind": "questionnaire" })
    );
}

/// MainWindow.WebShell.Cards.cs:554-576 `WebLogin`: the lines and buttons by the sign-in's state.
#[test]
fn web_login_cs554_lines_and_buttons() {
    let l = ko();
    let job = |terminal: bool| LoginJob {
        automatic: true,
        phase: "waiting".into(),
        terminal,
        url: Some("https://claude.ai/x".into()),
        asks_for_code: true,
    };
    let running = cards::login_card(
        &LoginInput { provider: "claude".into(), job: Some(job(false)), failure: None, note: Some("busy".into()), account_wait: false },
        &l,
    );
    same(
        &running,
        json!({ "title": l.get_with("loginRecovery.title", &[("provider", "Claude Code")]),
                "lines": [ { "text": l.get_with("loginRecovery.resendBlockedTemplate", &[("reason", "busy")]), "tone": "wait", "busy": false },
                           { "text": l.get("loginRecovery.autoStarted"), "tone": "ink2", "busy": false },
                           { "text": l.get("loginRecovery.waiting"), "tone": "ink2", "busy": true } ],
                "url": "https://claude.ai/x", "asksForCode": true, "buttons": ["cancel", "terminal"], "loginEnabled": true, "kind": "login" }),
    );
    let terminal = cards::login_card(
        &LoginInput { provider: "gemini".into(), job: Some(job(true)), failure: None, note: None, account_wait: false },
        &l,
    );
    assert_eq!(terminal.buttons, ["cancel"]);
    assert_eq!(terminal.lines[1].text, l.get("loginRecovery.antigravityTerminal"));
    let failed = cards::login_card(
        &LoginInput { provider: "codex".into(), job: None, failure: Some("denied".into()), note: None, account_wait: false },
        &l,
    );
    assert_eq!(failed.buttons, ["login", "terminal", "resend"]);
    let waiting = cards::login_card(&LoginInput { provider: "codex".into(), job: None, failure: None, note: None, account_wait: true }, &l);
    assert!(
        !waiting.login_enabled && waiting.lines[0].text == l.get("loginRecovery.terminalPending") && waiting.buttons == ["login", "resend"]
    );
}

/// MainWindow.WebShell.Cards.cs:436-447 `WebBackground`: the strip once the turn ended, its counts done · running, its rows when open.
#[test]
fn web_background_cs436_counts_and_rows() {
    let l = ko();
    let mut p = pane("claude", "running");
    let task = |id: &str, status: &str| CoreTask {
        id: Some(id.into()),
        kind: Some("agent".into()),
        description: Some("Look".into()),
        started_at: None,
        status: Some(status.into()),
        tool_use_id: None,
        summary: None,
        ended_at: None,
    };
    p.background_work = Some(BackgroundWork { tasks: Some(vec![task("t1", "running"), task("t2", "completed")]), turn_ended: true });
    let mut world = CardWorld::new(p);
    world.rows = vec![BackgroundTask {
        text: "Look".into(),
        status: "running".into(),
        detail: "sub-agent · 3s".into(),
        tail: None,
        id: Some("t1".into()),
        command: None,
        tail_lines: None,
    }];
    let (card, ticks) = cards::background_card(&world.inputs(true, false, false, true)).unwrap();
    assert!(ticks);
    same(
        &card,
        json!({ "counts": format!("{} · {}", l.get_with("plan.background.doneCount", &[("count", "1")]), l.get_with("plan.background.runningCount", &[("count", "1")])),
                "open": true, "tasks": [ { "text": "Look", "status": "running", "detail": "sub-agent · 3s", "id": "t1" } ], "kind": "background" }),
    );
    let (closed, ticks) = cards::background_card(&world.inputs(true, false, false, false)).unwrap();
    assert!(closed.tasks.is_empty() && !ticks);
}

// ── menus and run settings ──────────────────────────────────────────────────────────────────────────────────────────────

fn menu_inputs<'a>(pane: &'a RunSession, runtime: Option<&'a ProviderRuntime>, levels: &'a [String], remote: bool) -> MenuInputs<'a> {
    MenuInputs { locale: ko(), pane, runtime, efforts: levels, remote_session: remote, refreshing_models: false, style_rows: None }
}

/// MainWindow.cs:858-873 `ModelMenu` read by MainWindow.WebShell.Composer.cs:399 `WebMenu`: the runners (pi, opencode and Hermes
/// only on this PC), the refresh, the models with their descriptions, the hand-typed id.
#[test]
fn model_menu_cs858_rows() {
    let l = ko();
    let (p, runtime, levels) = (pane("claude", "idle"), claude_runtime(), efforts());
    let rows = menus::composer_menu("model", &menu_inputs(&p, Some(&runtime), &levels, true)).unwrap();
    let value = serde_json::to_value(&rows).unwrap();
    let kinds: Vec<&str> = value.as_array().unwrap().iter().map(|r| r["kind"].as_str().unwrap()).collect();
    assert_eq!(
        kinds,
        ["header", "choice", "choice", "choice", "separator", "action", "separator", "header", "choice", "choice", "separator", "action"]
    );
    assert_eq!(value[0], json!({ "kind": "header", "text": l.get("composer.label.runner") }));
    assert_eq!(value[1], json!({ "kind": "choice", "text": "Claude", "enabled": true, "checked": true }));
    assert_eq!(value[2]["text"], format!("Codex · {}", l.get("badge.beta")));
    assert_eq!(value[8]["help"], "The CLI's choice");
    assert!(value[9].get("help").is_none(), "an empty description gives no help");
    assert_eq!(menus::menu_action(&rows, &[9]).unwrap(), Some(MenuAction::ChangeModel("opus".into())));
    assert_eq!(menus::menu_action(&rows, &[0]).unwrap(), None, "a header does nothing");
    assert!(menus::menu_action(&rows, &[99]).is_err());
    assert_eq!(
        menus::composer_menu("model", &menu_inputs(&p, Some(&runtime), &levels, false)).unwrap().len(),
        15,
        "this PC's pane lists all six runners"
    );
    assert!(menus::composer_menu("nope", &menu_inputs(&p, Some(&runtime), &levels, false)).is_err());
}

/// MainWindow.cs:891-911 `OptionsMenu`: effort and permission as submenus, Codex's Fast, then the run settings.
#[test]
fn options_menu_cs891_rows() {
    let l = ko();
    let mut p = pane("claude", "idle");
    p.provider = Some("codex".into());
    p.settings = Some(RunSettings { fast_mode: true, permission_mode: "acceptEdits".into(), ..RunSettings::default() });
    let levels = efforts();
    let rows = menus::composer_menu("options", &menu_inputs(&p, None, &levels, false)).unwrap();
    let value = serde_json::to_value(&rows).unwrap();
    assert_eq!(value[0]["kind"], "submenu");
    assert_eq!(value[0]["text"], format!("{} · Auto", l.get("composer.effort.label")));
    assert_eq!(value[0]["items"].as_array().unwrap().len(), 6);
    assert_eq!(value[1]["text"], format!("{} · {}", l.get("composer.label.permission"), l.get("permission.label.acceptEditsCodex")));
    let modes: Vec<&str> = value[1]["items"].as_array().unwrap().iter().map(|r| r["text"].as_str().unwrap()).collect();
    assert_eq!(modes.len(), 3, "the fallback Codex modes without on-request: {modes:?}");
    assert_eq!(
        value[2],
        json!({ "kind": "choice", "text": l.get("composer.fast.on"), "enabled": true, "checked": true, "help": l.get("composer.fast.tooltip") })
    );
    assert_eq!(value[4], json!({ "kind": "runSettings", "text": l.get("composer.runSettings.more") }));
    assert_eq!(menus::menu_action(&rows, &[1, 0]).unwrap(), Some(MenuAction::SetPermission("manual".into())));
    assert_eq!(menus::menu_action(&rows, &[4]).unwrap(), Some(MenuAction::RunSettings));
}

/// MainWindow.WebShell.Composer.cs:459-483 `WebRunSettings` and MainWindow.RunSettings.cs:211-227 `RunSettingsFrom`.
#[test]
fn run_settings_cs459_info_and_apply() {
    let l = ko();
    let mut p = pane("claude", "running");
    p.settings = Some(RunSettings { permission_mode: "acceptEdits".into(), max_budget_usd: Some(2.5), ..RunSettings::default() });
    let (runtime, levels) = (claude_runtime(), efforts());
    let info = menus::run_settings_info(&menu_inputs(&p, Some(&runtime), &levels, false));
    let value = serde_json::to_value(&info).unwrap();
    assert_eq!(value["provider"], "Claude");
    assert_eq!(value["modelSource"], l.get("settings.run.modelSourceCli"));
    assert_eq!(value["approval"], l.get("settings.run.approvalInApp"));
    assert_eq!(value["networkNote"], l.get("settings.run.shellNetworkAcceptEdits"));
    assert_eq!(
        value["values"],
        json!({ "fastMode": false, "webSearch": "default", "networkAccess": false, "maxTurns": "", "maxBudgetUsd": "2.5" })
    );
    assert_eq!(value["searchDescriptions"].as_object().unwrap().keys().collect::<Vec<_>>(), ["default", "disabled", "cached", "live"]);
    assert_eq!(value["held"], true);
    let ok = |_: &RunSettings| Ok(());
    let from = |turns: Option<&str>, budget: Option<&str>| {
        let form = menus::RunSettingsForm {
            fast_mode: false,
            web_search: "live".into(),
            network_access: true,
            max_turns: turns.map(str::to_string),
            max_budget_usd: budget.map(str::to_string),
        };
        menus::run_settings_from(&p, &form, &ok, &l)
    };
    let plain = menus::RunSettingsForm { web_search: "default".into(), ..Default::default() };
    let saved = from(Some(" 17 "), Some("12.5")).unwrap();
    assert!(saved.max_turns == Some(17) && saved.max_budget_usd == Some(12.5) && saved.network_access && saved.web_search == "live");
    assert_eq!(from(Some("0"), None), Err(l.get("settings.run.maxTurnsError")));
    assert_eq!(from(Some("+5"), None), Err(l.get("settings.run.maxTurnsError")));
    assert_eq!(from(None, Some("-1")), Err(l.get("settings.run.maxBudgetError")));
    assert_eq!(from(None, Some("1e3")), Err(l.get("settings.run.maxBudgetError")));
    assert_eq!(from(Some(""), Some(" ")).unwrap().max_turns, None, "a blank limit is unlimited");
    assert_eq!(menus::apply_run_settings(&p, true, true, &plain, &ok, &l), Err(l.get("phone.session.settingsLocked")));
    assert_eq!(menus::apply_run_settings(&p, false, false, &plain, &ok, &l), Err(l.get("remote.error.paneNotFound")));
}

/// MainWindow.WebShell.Composer.cs:352-369 `WebSlashCatalog`: the built-ins, the scanned catalogue and each argument built-in's
/// choices, in rows of the C# `WebSlashRow` shape.
#[test]
fn web_slash_catalog_cs355_rows_and_choices() {
    let l = ko();
    let (p, runtime, levels) = (pane("claude", "idle"), claude_runtime(), efforts());
    let catalog = menus::slash_catalog(&menu_inputs(&p, Some(&runtime), &levels, false), &[]);
    let value = serde_json::to_value(&catalog).unwrap();
    assert_eq!(
        value["builtins"][0],
        json!({ "invocation": "plugin", "description": l.get("slash.builtin.plugin"), "source": l.get("slash.source.app"), "origin": "App", "action": "OpenPlugins" })
    );
    assert_eq!(value["builtins"][1]["argument"], "Model");
    assert_eq!(value["choices"].as_object().unwrap().keys().collect::<Vec<_>>(), ["model", "permissions"]);
    assert_eq!(value["choices"]["model"][0]["actionArg"], "default");
    assert!(value["choices"]["model"][0]["description"].as_str().unwrap().ends_with(&l.get("slash.currentSuffix")));
    assert_eq!(value["choices"]["permissions"].as_array().unwrap().len(), 5);
}

// ── the page's TypeScript types ─────────────────────────────────────────────────────────────────────────────────────────

fn page_source(path: &str) -> String {
    let file = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../windows/web/src").join(path);
    std::fs::read_to_string(&file).unwrap_or_else(|e| panic!("{}: {e}", file.display()))
}

/// The top-level keys of `export interface <name> { … }`, each with whether it is optional (`?`).
fn interface_keys(source: &str, name: &str) -> BTreeSet<(String, bool)> {
    let start = source.find(&format!("export interface {name} {{")).unwrap_or_else(|| panic!("no interface {name}"));
    let mut keys = BTreeSet::new();
    let mut depth = 0i32;
    for line in source[start..].lines().skip(1) {
        let trimmed = line.trim();
        if trimmed.starts_with("/**") || trimmed.starts_with('*') || trimmed.starts_with("//") {
            continue;
        }
        if depth == 0
            && let Some(colon) = trimmed.find(':')
        {
            let key = &trimmed[..colon];
            let (key, optional) = match key.strip_suffix('?') {
                Some(k) => (k, true),
                None => (key, false),
            };
            if !key.is_empty() && key.chars().all(|c| c.is_ascii_alphanumeric()) {
                keys.insert((key.to_string(), optional));
            }
        }
        depth += trimmed.matches('{').count() as i32 - trimmed.matches('}').count() as i32;
        if depth < 0 {
            break;
        }
    }
    keys
}

fn rust_keys(value: &impl serde::Serialize) -> BTreeSet<String> {
    serde_json::to_value(value).unwrap().as_object().unwrap().keys().cloned().collect()
}

/// Every key the page reads is one the Rust model writes, and no other: with every optional value present, the serialized keys
/// equal the interface's.
fn same_keys(source: &str, name: &str, value: &impl serde::Serialize) {
    let page: BTreeSet<String> = interface_keys(source, name).into_iter().map(|(k, _)| k).collect();
    assert_eq!(rust_keys(value), page, "{name}");
}

#[test]
fn the_composer_state_has_the_pages_field_names() {
    let types = page_source("composer/types.ts");
    let (pane, ws, runtime, levels) = (pane("claude", "running"), workspace(), claude_runtime(), efforts());
    let queued = QueuedComposerInput {
        id: "q".into(),
        text: "t".into(),
        attachments: vec![],
        permission_mode_override: None,
        prepared: false,
        continuation: false,
    };
    let file = mighty_composer::attachments::make("a.txt", b"text", &ko()).unwrap();
    let view = PaneViewState {
        draft: "x".into(),
        attachments: vec![file],
        attachment_error: Some("e".into()),
        queue: vec![queued],
        ..Default::default()
    };
    let window = WindowRules {
        background_update_holds: true,
        held_items: vec![HeldItem { id: "h".into(), text: "t".into() }],
        ask_targets: vec![mighty_composer::ask_targets::ALL[0]],
        ask_notice: Some("n".into()),
        style: Some(StyleRow { label: "s".into(), badge: Some("b".into()), hint: "h".into(), enabled: true }),
        guided: Some(json!({})),
        manual_mutation_block: Some("m".into()),
        ..WindowRules::default()
    };
    let mut full_pane = pane.clone();
    full_pane.ask_target = Some("codex".into());
    full_pane.session_usage = mighty_core::model::SessionUsage::from_json(
        &json!({ "provider": "claude", "source": "s", "tokenScope": "session", "contextUsedTokens": 1, "contextWindowTokens": 2, "updatedAt": "2027-01-15T08:05:00.0000000+00:00" }),
    );
    let state = composer::composer_state(&inputs(&full_pane, &ws, Some(&runtime), &levels, &view, &window));
    let value = serde_json::to_value(&state).unwrap();
    same_keys(&types, "ComposerState", &state);
    for (name, item) in [
        ("QueueHeld", &value["queue"]["held"][0]),
        ("QueueItem", &value["queue"]["items"][0]),
        ("ComposerAttachment", &value["attachments"][0]),
        ("AskTargetOption", &value["askTargets"][0]),
    ] {
        same_keys(&types, name, item);
    }
    let menus_ts = page_source("composer/menus.ts");
    same_keys(&menus_ts, "Pills", &value["pills"]);
    let info = menus::run_settings_info(&menu_inputs(&full_pane, Some(&runtime), &levels, false));
    let mut info_value = serde_json::to_value(&info).unwrap();
    info_value["modelDescription"] = json!("d");
    same_keys(&menus_ts, "RunSettingsInfo", &info_value);
    same_keys(&menus_ts, "RunSettingsValues", &info_value["values"]);
    // The inline `queue` and `header` objects of ComposerState.
    assert_eq!(
        rust_keys(&value["queue"]),
        ["held", "items", "waitsOnBackground"].map(String::from).into_iter().filter(|k| k != "waitsOnBackground").collect()
    );
    assert_eq!(rust_keys(&value["header"]), ["word", "tone", "timed", "rest", "help"].map(String::from).into_iter().collect());
}

#[test]
fn the_cards_have_the_pages_field_names() {
    let types = page_source("cards/types.ts");
    let l = ko();
    let mut world = CardWorld::new(pane("claude", "idle"));
    world.pane.logs = Some(vec![LogEntry::new("a".into(), "assistant", "◆ x → next: a".into(), String::new(), None)]);
    world.pane.plan_history = Some(vec![PlanRecord {
        id: Some("r".into()),
        run_id: None,
        plan: Some("p".into()),
        received_at: Some("x".into()),
        decided_at: Some("y".into()),
        outcome: Some("revised".into()),
        feedback: Some("f".into()),
        plan_truncated: Some(true),
        graph_run_id: None,
        launch_override: None,
    }]);
    let mut request = PermissionRequest::new("r", "p1", "t", "mcp__srv__x", r#"{"a":"b"}"#, "s");
    request.reason = Some("why".into());
    request.blocked_path = Some("p".into());
    request.can_allow = false;
    world.requests = vec![request.clone(), request];
    world.web = vec![("w".into(), "https://x/".into())];
    world.login = Some(LoginInput {
        provider: "claude".into(),
        job: Some(LoginJob { automatic: false, phase: "x".into(), terminal: false, url: Some("u".into()), asks_for_code: false }),
        failure: None,
        note: None,
        account_wait: false,
    });
    world.status = Some(StatusLineInput {
        config: Some(("s".into(), "c".into())),
        untrusted: Some(("s".into(), "c".into())),
        lines: Some(vec![]),
        error_text: Some("e".into()),
    });
    let (cards, _) = cards::cards(&world.inputs(false, false, false, false));
    let value = serde_json::to_value(&cards).unwrap();
    let card =
        |kind: &str| value.as_array().unwrap().iter().find(|c| c["kind"] == kind).unwrap_or_else(|| panic!("no {kind} card")).clone();
    same_keys(&types, "NextActionsCard", &card("nextActions"));
    same_keys(&types, "PlanHistoryCard", &card("planHistory"));
    same_keys(&types, "PlanRecord", &card("planHistory")["records"][0]);
    let mut permission = card("permission");
    permission["headline"] = json!("h");
    permission["summary"] = json!("s");
    same_keys(&types, "PermissionCard", &permission);
    same_keys(&types, "PermissionField", &permission["fields"][0]);
    same_keys(&types, "WebOpenCard", &card("webOpen"));
    same_keys(&types, "LoginCard", &card("login"));
    same_keys(&types, "LoginLine", &card("login")["lines"][0]);
    let mut status = card("statusLine");
    status["lines"] =
        json!([[{ "text": "t", "color": "#000000", "ink": "ink", "dim": false, "bold": false, "italic": false, "underline": false }]]);
    same_keys(&types, "StatusLineCard", &status);
    same_keys(&types, "StatusSegment", &status["lines"][0][0]);
    same_keys(&types, "StatusTrust", &status["trust"]);
    // The cards the inputs above do not raise, made directly.
    let task = BackgroundTask {
        text: "t".into(),
        status: "s".into(),
        detail: "d".into(),
        tail: Some("x".into()),
        id: Some("i".into()),
        command: Some("c".into()),
        tail_lines: Some(vec!["l".into()]),
    };
    same_keys(&types, "BackgroundTask", &task);
    same_keys(&types, "BackgroundCard", &cards::BackgroundCard { counts: "c".into(), open: true, tasks: vec![task], kind: "background" });
    let questionnaire = cards::QuestionnaireCard {
        request_id: "r".into(),
        count: 1,
        questions: vec![cards::Question {
            header: "h".into(),
            question: "q".into(),
            multi_select: false,
            options: vec![cards::QuestionOption { label: "l".into(), description: "d".into() }],
        }],
        kind: "questionnaire",
    };
    same_keys(&types, "QuestionnaireCard", &questionnaire);
    same_keys(&types, "Question", &questionnaire.questions[0]);
    same_keys(&types, "QuestionOption", &questionnaire.questions[0].options[0]);
    same_keys(
        &types,
        "PlanCard",
        &cards::PlanCard {
            request_id: "r".into(),
            count: 1,
            received: "x".into(),
            plan: "p".into(),
            actions_only: false,
            sending: false,
            error: Some("e".into()),
            kind: "plan",
        },
    );
    let action = cards::AntigravityAction {
        heading: "h".into(),
        target: Some("t".into()),
        code: true,
        rule: Some("r".into()),
        once_only: Some("o".into()),
    };
    same_keys(&types, "AntigravityAction", &action);
    same_keys(
        &types,
        "AntigravityCard",
        &cards::AntigravityCard {
            card_id: "c".into(),
            run_id: "r".into(),
            actions: vec![action],
            reason: Some("x".into()),
            busy: false,
            can_allow: true,
            can_allow_always: false,
            always_tip: Some("t".into()),
            kind: "antigravity",
        },
    );
    same_keys(
        &types,
        "TerminalPromptCard",
        &cards::TerminalPromptCard {
            prompt_id: "p".into(),
            text: "t".into(),
            options: vec![],
            input: "yesNo".into(),
            default_yes: Some(true),
            kind: "terminalPrompt",
        },
    );
    // Every kind of the page's union is one this model makes.
    let kinds: BTreeSet<&str> =
        types.lines().filter_map(|line| line.trim().strip_prefix("kind: '")).map(|rest| rest.trim_end_matches("';")).collect();
    let made: BTreeSet<&str> = [
        "nextActions",
        "planHistory",
        "background",
        "permission",
        "questionnaire",
        "plan",
        "antigravity",
        "webOpen",
        "terminalPrompt",
        "login",
        "statusLine",
    ]
    .into();
    assert_eq!(kinds, made);
    let _ = l;
}

#[test]
fn the_menu_rows_have_the_pages_field_names() {
    let source = page_source("composer/menus.ts");
    let union = &source[source.find("export type MenuRow =").unwrap()..];
    let union = &union[..union.find("export type MenuName").unwrap()];
    let mut page: Vec<(String, BTreeSet<String>)> = Vec::new();
    for member in union.lines().filter_map(|l| l.trim().strip_prefix("| {")) {
        let body = member.trim_end_matches(';').trim_end().trim_end_matches('}');
        let keys: BTreeSet<String> = body
            .split(';')
            .filter_map(|part| part.split(':').next())
            .map(|k| k.trim().trim_end_matches('?').to_string())
            .filter(|k| !k.is_empty())
            .collect();
        let kind = member.split('\'').nth(1).unwrap().to_string();
        page.push((kind, keys));
    }
    let rows = [
        MenuRow::Separator,
        MenuRow::Header { text: "h".into() },
        MenuRow::Note { text: "n".into(), help: "h".into() },
        MenuRow::Choice { text: "c".into(), enabled: true, checked: false, help: Some("h".into()), action: None },
        MenuRow::Action { text: "a".into(), enabled: true, help: Some("h".into()), action: None },
        MenuRow::RunSettings { text: "r".into(), action: None },
        MenuRow::Submenu { text: "s".into(), enabled: true, items: vec![] },
    ];
    assert_eq!(page.len(), rows.len());
    for row in rows {
        let value = serde_json::to_value(&row).unwrap();
        let kind = value["kind"].as_str().unwrap();
        let expected = &page.iter().find(|(k, _)| k == kind).unwrap_or_else(|| panic!("the page has no {kind} row")).1;
        assert_eq!(&rust_keys(&row), expected, "{kind}");
    }
}
