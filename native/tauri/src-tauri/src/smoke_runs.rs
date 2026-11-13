//! The smoke's run steps (C# MainWindow.WebShell.Composer.cs `RunWebComposerSmoke`, .Cards.cs `RunWebCardsSmoke`, .Mighty.cs
//! `RunWebMightySmoke` and `RunWebStepsSmoke`, with a real CLI process instead of C#'s stand-in start): the host adds an agent pane in
//! a workspace of its own (a temporary folder, in memory) shown in the Mighty view, and the probe works the page as a person would.
//!
//! - With `--smoke-fake-cli` (mighty-run's `mighty-fake-cli --replay-cli claude-stream-subagents`): the request is typed into the
//!   composer and sent; the stand-in asks one Bash permission, allowed from the page's card, and one plan, approved from the page's plan
//!   card (both answers must reach the CLI), then replays the recording — the diagram must draw the request, the sub-agents and the
//!   result with the recording's answer. Then a second request is sent; while its permission waits, the progress panel draws a
//!   three-step plan on it (the newest request, running: 1/3, done, current, pending) and none without it, and the request is stopped
//!   from the composer (the run ends stopped, its card goes).
//! - With `--smoke-real-claude`: one request to the installed `claude` CLI ("Reply with exactly the word: ok", the pane's default
//!   permission mode), waited for until its result block shows the answer; the usage the CLI reported is recorded.

use std::path::PathBuf;
use std::sync::Arc;

use mighty_core::graph::FlowStep;
use mighty_core::{AppSnapshot, Wire};
use serde_json::{Value, json};

use crate::host::Host;

/// The recording the stand-in replays.
pub const RECORDING: &str = "claude-stream-subagents";
/// The pane and workspace the run steps add (UUID-shaped, as the state's ids are).
pub const PANE: &str = "5a0e0b0b-0000-4000-8000-0000000c1a0d";
const WORKSPACE: &str = "5a0e0b0b-0000-4000-8000-0000000c1a0e";
/// What the probe types.
pub const FAKE_REQUEST: &str = "smoke request replay-marker";
pub const STOP_REQUEST: &str = "smoke request to stop";
pub const REAL_REQUEST: &str = "Reply with exactly the word: ok";

/// The probe of the stand-in run (see the module comment); `__PANE__`, `__TYPED__` and `__STOP__` are filled in.
pub const FAKE_PROBE: &str = r#"(async () => {
  const until = async (test, ms) => { const end = performance.now() + ms; while (performance.now() < end) { const v = await test(); if (v) return v; await new Promise((r) => setTimeout(r, 100)); } return test(); };
  const invoke = (command, args) => window.__TAURI_INTERNALS__.invoke(command, args);
  const step = (name) => invoke('smoke_step', { step: name });
  const paneId = '__PANE__', typed = '__TYPED__', stopText = '__STOP__';
  const scope = () => document.querySelector('.pane-slot:not(.hidden)[data-pane-id="' + paneId + '"]');
  const q = (s) => scope()?.querySelector(s);
  const type = (text) => { const e = q('[data-testid=composer-input]'); if (!e) return false; e.focus(); Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(e, text); e.dispatchEvent(new Event('input', { bubbles: true })); return true; };
  const enter = () => q('[data-testid=composer-input]')?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true, cancelable: true }));
  const report = { paneId };
  try {
    report.available = !!(await until(async () => (await step('state')).available, 30000));
    report.input = !!(await until(() => q('[data-testid=composer-input]'), 15000));
    report.typed = type(typed);
    report.drafted = !!(await until(async () => (await step('state')).draft === typed, 10000));
    report.sendEnabled = !!(await until(() => q('[data-testid=composer-primary]') && !q('[data-testid=composer-primary]').disabled, 10000));
    enter();
    report.outcome = await until(() => q('[data-testid=composer]')?.dataset.lastOutcome ?? null, 20000);
    const allow = await until(() => q('[data-card=permission] [data-answer=allow]'), 30000);
    report.permissionShown = !!allow;
    report.permissionTool = q('[data-card=permission] .card-tool')?.textContent ?? null;
    allow?.click();
    const approve = await until(() => q('[data-card=plan] [data-answer=plan-approveAuto]'), 30000);
    report.planShown = !!approve;
    approve?.click();
    report.answers = await until(async () => { const s = await step('state'); return s.answers >= 2 ? s.answers : 0; }, 20000);
    const blocks = (kind) => [...(scope()?.querySelectorAll('[data-testid=mighty-diagram] .mighty-block[data-kind=' + kind + ']') ?? [])];
    report.requestBlock = !!(await until(() => blocks('request').length > 0, 30000));
    const finished = await until(async () => { const s = await step('state'); return ['completed', 'idle'].includes(s.status) ? s : null; }, 60000);
    report.status = finished?.status ?? (await step('state')).status;
    report.graphRuns = finished?.graphRuns ?? null;
    report.resultText = await until(() => blocks('result').map((e) => e.textContent).find((t) => t.includes('launched') || t.includes('done')) ?? null, 30000);
    report.agentCards = scope()?.querySelectorAll('[data-testid=mighty-diagram] .mighty-flow-card').length ?? 0;
    report.blocks = scope()?.querySelectorAll('[data-testid=mighty-diagram] .mighty-block').length ?? 0;
    // A second request: while its permission waits, the progress panel draws a three-step plan on it (the newest request, running)
    // and none without it; then the request is stopped from the composer.
    await until(() => q('[data-testid=composer-input]') && !q('[data-testid=composer-input]').disabled, 5000);
    type(stopText);
    await until(() => q('[data-testid=composer-primary]') && !q('[data-testid=composer-primary]').disabled, 10000);
    enter();
    report.stopPermission = !!(await until(() => q('[data-card=permission] [data-answer=allow]'), 30000));
    report.secondRun = !!(await until(async () => (await step('state')).graphRuns >= 3, 10000));
    await step('steps');
    const rows = () => [...(scope()?.querySelectorAll('[data-testid=mighty-progress] [data-testid^=mighty-progress-row-]') ?? [])];
    report.stepsDrawn = !!(await until(() => rows().length === 3, 15000));
    report.stepsCount = q('[data-testid=mighty-progress-count]')?.textContent ?? null;
    report.stepsStates = rows().map((e) => [...e.classList].find((c) => c.startsWith('state-'))?.slice(6) ?? '').join(',');
    await step('clearSteps');
    report.stepsHidden = !!(await until(() => rows().length === 0 && !q('[data-testid=mighty-progress-count]'), 15000));
    const stop = await until(() => { const b = q('[data-testid=composer-primary]'); return b && !b.disabled ? b : null; }, 10000);
    stop?.click();
    const stopped = await until(async () => { const s = await step('state'); return s.status === 'stopped' ? s : null; }, 30000);
    report.stopped = !!stopped;
    report.stopCardGone = !!(await until(() => !q('[data-card=permission]'), 10000));
  } catch (error) {
    report.error = String(error);
  }
  await invoke('smoke_probe', { probe: report });
})();"#;

/// The probe of the real request: typed, sent, and waited for until its result block shows the answer.
pub const REAL_PROBE: &str = r#"(async () => {
  const until = async (test, ms) => { const end = performance.now() + ms; while (performance.now() < end) { const v = await test(); if (v) return v; await new Promise((r) => setTimeout(r, 200)); } return test(); };
  const invoke = (command, args) => window.__TAURI_INTERNALS__.invoke(command, args);
  const step = (name) => invoke('smoke_step', { step: name });
  const paneId = '__PANE__', typed = '__TYPED__';
  const scope = () => document.querySelector('.pane-slot:not(.hidden)[data-pane-id="' + paneId + '"]');
  const q = (s) => scope()?.querySelector(s);
  const report = { paneId };
  try {
    report.available = !!(await until(async () => (await step('state')).available, 60000));
    const e = await until(() => q('[data-testid=composer-input]'), 15000);
    e.focus();
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(e, typed);
    e.dispatchEvent(new Event('input', { bubbles: true }));
    report.sendEnabled = !!(await until(() => q('[data-testid=composer-primary]') && !q('[data-testid=composer-primary]').disabled, 15000));
    e.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true, cancelable: true }));
    report.outcome = await until(() => q('[data-testid=composer]')?.dataset.lastOutcome ?? null, 30000);
    const finished = await until(async () => { const s = await step('state'); return ['completed', 'idle', 'error', 'stopped'].includes(s.status) ? s : null; }, 240000);
    report.state = finished ?? (await step('state'));
    const blocks = () => [...(scope()?.querySelectorAll('[data-testid=mighty-diagram] .mighty-block[data-kind=result]') ?? [])];
    // The block's text runs its title, agent and time into the answer ("Final resultClaude · 2:49 AMok"): the answer is its end.
    report.resultBlock = await until(() => blocks().map((b) => b.textContent.trim()).find((t) => t.toLowerCase().endsWith('ok')) ?? null, 20000);
    report.resultBlockText = blocks().map((b) => b.textContent.trim()).join(' | ');
    report.requestBlock = !!scope()?.querySelector('[data-testid=mighty-diagram] .mighty-block[data-kind=request]');
    await new Promise((r) => setTimeout(r, 1500));
  } catch (error) {
    report.error = String(error);
  }
  await invoke('smoke_probe', { probe: report });
})();"#;

/// The run steps' workspace folder: a new temporary folder.
fn workspace_folder() -> std::io::Result<PathBuf> {
    let folder = std::env::temp_dir().join(format!("mighty-tauri-smoke-ws-{}", std::process::id()));
    std::fs::create_dir_all(&folder)?;
    Ok(folder)
}

/// An agent pane (Claude, the Mighty diagram) in a workspace of its own, made the active one; in memory as the browser step's is.
pub fn add_pane(host: &Host) -> Result<String, String> {
    let folder = workspace_folder().map_err(|e| e.to_string())?;
    let mut view = host.view();
    let mut state: Value = serde_json::from_str(&view.snapshot.to_json()).map_err(|e| e.to_string())?;
    let now = chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true);
    let workspace = json!({"id": WORKSPACE, "name": "Smoke runs", "path": folder, "createdAt": now});
    let pane = json!({
        "id": PANE, "workspaceId": WORKSPACE, "title": "Smoke agent", "kind": "claude", "status": "idle", "model": "default",
        "provider": "claude", "settings": {"effort": "default", "permissionMode": "manual", "maxTurns": null, "maxBudgetUsd": null},
        "logs": [], "draft": "", "createdAt": now, "agentViewMode": "mighty", "graphViewMode": "diagram",
    });
    for (name, item) in [("workspaces", workspace), ("sessions", pane)] {
        state.get_mut(name).and_then(Value::as_array_mut).ok_or_else(|| format!("the state has no {name}"))?.push(item);
    }
    let mut expanded: Vec<Value> = state.get("expandedWorkspaceIds").and_then(Value::as_array).cloned().unwrap_or_default();
    expanded.push(json!(WORKSPACE));
    state["expandedWorkspaceIds"] = json!(expanded);
    state["activeWorkspaceId"] = json!(WORKSPACE);
    state["activeSessionId"] = json!(PANE);
    view.replace(AppSnapshot::read(&state).map_err(|e| e.to_string())?);
    Ok(PANE.into())
}

/// A three-step plan on the pane's newest request (`RunWebStepsSmoke`'s `Plan()`), or none on every run.
fn set_steps(host: &Host, on: bool) -> Result<(), String> {
    let start = chrono::Utc::now() - chrono::Duration::minutes(2);
    let at = |seconds: i64| Some((start + chrono::Duration::seconds(seconds)).to_rfc3339_opts(chrono::SecondsFormat::Millis, true));
    let step = |key: &str, content: &str, active: Option<&str>, status: &str, started: Option<String>, ended: Option<String>| FlowStep {
        key: Some(key.into()),
        content: Some(content.into()),
        active_form: active.map(str::to_string),
        status: Some(status.into()),
        started_at: started,
        ended_at: ended,
    };
    let plan = vec![
        step("text:Implement", "Implement the app", None, "completed", at(0), at(10)),
        step("text:Build", "Build the app", Some("Building the app"), "in_progress", at(10), None),
        step("text:Deploy", "Deploy the app", None, "pending", None, None),
    ];
    let mut view = host.view();
    let newest =
        view.snapshot.session(PANE).and_then(|p| mighty_graph::support::newest_request_run(p.graph_runs()).map(|r| r.id().to_string()));
    crate::composer::change(&mut view, PANE, |pane| {
        let mut next = pane.clone();
        for run in next.graph_runs.iter_mut().flatten() {
            run.steps = (on && Some(run.id()) == newest.as_deref()).then(|| plan.clone());
        }
        next
    })
}

/// What the probe asks the host (smoke only): `state` of the run steps' pane, the progress panel's plan on (`steps`) or off
/// (`clearSteps`).
#[tauri::command]
pub fn smoke_step(step: String, host: tauri::State<'_, Arc<Host>>) -> Result<Value, String> {
    let Some(smoke) = &host.smoke else { return Err("smoke only".into()) };
    match step.as_str() {
        "state" => {
            let available = host.runs().and_then(|r| r.runtime("claude")).is_some_and(|r| r.available);
            let answers = smoke.fake_record().map_or(0, |record| {
                std::fs::read_to_string(format!("{}.answers", record.display()))
                    .map_or(0, |t| t.lines().filter(|l| !l.trim().is_empty()).count())
            });
            let pending = host.runs().map_or(0, |r| r.service.pending_permissions(PANE).len());
            let view = host.view();
            let pane = view.snapshot.session(PANE).cloned();
            let usage = pane.as_ref().and_then(|p| p.session_usage.as_ref()).map(|u| {
                json!({"provider": u.provider, "model": u.model, "inputTokens": u.input_tokens, "outputTokens": u.output_tokens,
                    "cacheReadTokens": u.cache_read_tokens, "totalTokens": u.total_tokens, "costUsd": u.cost_usd,
                    "contextUsedTokens": u.context_used_tokens})
            });
            let runs = pane.as_ref().map(|p| p.graph_runs()).unwrap_or(&[]);
            Ok(json!({
                "status": pane.as_ref().map(|p| p.status().to_string()),
                "draft": pane.as_ref().and_then(|p| p.draft.clone()).unwrap_or_default(),
                "available": available,
                "answers": answers,
                "pending": pending,
                "logs": pane.as_ref().map_or(0, |p| p.logs().len()),
                "errors": pane.as_ref().map(|p| p.logs().iter().filter(|e| e.kind() == "error").map(|e| e.text().to_string()).collect::<Vec<_>>()),
                "usage": usage,
                "graphRuns": runs.len(),
                "finalOutputs": runs.iter().map(|r| r.final_output().map(str::to_string)).collect::<Vec<_>>(),
            }))
        }
        "steps" => set_steps(&host, true).map(|_| Value::Null),
        "clearSteps" => set_steps(&host, false).map(|_| Value::Null),
        other => Err(format!("unknown smoke step {other}")),
    }
}
