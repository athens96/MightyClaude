//! The smoke's ACP step (W5-B; C#'s smoke has none — AcpRunVerification holds these runs to C# without the page): with `--smoke-fake-cli`
//! opencode is the stand-in agent replaying a recorded opencode session that asks one question (`mighty-fake-cli --fake-acp`,
//! native/contracts/fixtures/opencode-acp-question.jsonl). The host adds an opencode pane in a workspace of its own (a temporary folder,
//! in memory) shown in the Mighty view; the probe types a request and sends it, the agent's `elicitation/create` form is the page's
//! question card (its three options), the probe picks "Red" and submits, and the answer must reach the agent as the form's content
//! (`acpAnswered`), the card go, the run complete and the diagram draw the recording's answer (`acpDiagram`).

use std::path::{Path, PathBuf};

use mighty_core::{AppSnapshot, Wire};
use serde_json::{Value, json};
use tauri::Manager;

use crate::host::Host;

/// The pane and workspace the step adds (UUID-shaped, as the state's ids are).
const PANE: &str = "5a0e0b0b-0000-4000-8000-0000000ac90d";
const WORKSPACE: &str = "5a0e0b0b-0000-4000-8000-0000000ac90e";
/// The recording opencode replays, and the answer it ends with.
const RECORDING: &str = "opencode-acp-question.jsonl";
const ANSWER: &str = "Great choice — red it is.";

/// The recording as the stand-in reads it (the repository's shared fixtures).
pub fn fixture() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("..").join("..").join("contracts").join("fixtures").join(RECORDING)
}

/// Where the stand-in agent records what it was sent (`stdin.txt`, `argv.json`, `env.json`): beside the stand-in Claude's record.
pub fn state(record: &Path) -> PathBuf {
    record.parent().map(Path::to_path_buf).unwrap_or_default().join("acp")
}

const PROBE: &str = r#"(async () => {
  const until = async (test, ms) => { const end = performance.now() + ms; while (performance.now() < end) { const v = await test(); if (v) return v; await new Promise((r) => setTimeout(r, 100)); } return test(); };
  const invoke = (command, args) => window.__TAURI_INTERNALS__.invoke(command, args);
  const paneId = '__PANE__';
  const scope = () => document.querySelector('.pane-slot:not(.hidden)[data-pane-id="' + paneId + '"]');
  const q = (s) => scope()?.querySelector(s);
  const report = { paneId };
  try {
    const input = await until(() => q('[data-testid=composer-input]'), 15000);
    report.input = !!input;
    input.focus();
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(input, 'hi there');
    input.dispatchEvent(new Event('input', { bubbles: true }));
    report.sendEnabled = !!(await until(() => q('[data-testid=composer-primary]') && !q('[data-testid=composer-primary]').disabled, 15000));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true, cancelable: true }));
    report.outcome = await until(() => q('[data-testid=composer]')?.dataset.lastOutcome ?? null, 20000);
    report.cardShown = !!(await until(() => q('[data-card=questionnaire]'), 30000));
    report.options = [...(scope()?.querySelectorAll('[data-card=questionnaire] [data-answer^=option-0-]') ?? [])].map((e) => e.querySelector('strong')?.textContent.trim() ?? '');
    q('[data-card=questionnaire] [data-answer=option-0-0]')?.click();
    const submit = await until(() => { const b = q('[data-card=questionnaire] [data-answer=submit]'); return b && !b.disabled ? b : null; }, 5000);
    report.submitEnabled = !!submit;
    submit?.click();
    report.cardGone = !!(await until(() => !q('[data-card=questionnaire]'), 15000));
    const blocks = () => [...(scope()?.querySelectorAll('[data-testid=mighty-diagram] .mighty-block') ?? [])];
    report.answerShown = await until(() => blocks().map((e) => e.textContent).find((t) => t.includes('red it is')) ?? null, 30000);
  } catch (error) {
    report.error = String(error);
  }
  await invoke('smoke_probe', { probe: report });
})();"#;

/// An opencode pane (the Mighty diagram) in a workspace of its own, made the active one; in memory as the other steps' are.
fn add_pane(host: &Host) -> Result<(), String> {
    let folder = std::env::temp_dir().join(format!("mighty-tauri-smoke-acp-{}", std::process::id()));
    std::fs::create_dir_all(&folder).map_err(|e| e.to_string())?;
    let mut view = host.view();
    let mut state: Value = serde_json::from_str(&view.snapshot.to_json()).map_err(|e| e.to_string())?;
    let now = chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true);
    let workspace = json!({"id": WORKSPACE, "name": "Smoke ACP", "path": folder, "createdAt": now});
    let pane = json!({
        "id": PANE, "workspaceId": WORKSPACE, "title": "Smoke opencode", "kind": "claude", "status": "idle", "model": "default",
        "provider": "opencode", "settings": {"effort": "default", "permissionMode": "manual", "maxTurns": null, "maxBudgetUsd": null},
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
    Ok(())
}

/// Adds the step's pane and runs its probe; false without the stand-in CLI.
pub fn start(host: &Host) -> bool {
    let Some(smoke) = &host.smoke else { return false };
    if smoke.fake_record().is_none() {
        return false;
    }
    if let Err(error) = add_pane(host) {
        host.log.line(&format!("smoke: the ACP step's pane was not added: {error}"));
        return false;
    }
    host.flush();
    host.log.line("smoke: ACP step (opencode, a question)");
    match host.app.get_webview(crate::host::WINDOW) {
        Some(page) => {
            if let Err(error) = page.eval(PROBE.replace("__PANE__", PANE)) {
                host.log.line(&format!("smoke: the ACP probe did not run: {error}"));
                return false;
            }
            true
        }
        None => false,
    }
}

/// The step's checks from its probe's report and what the run left: the agent's record of its stdin and the pane.
pub fn checks(host: &Host, report: &Value) -> Vec<(&'static str, bool)> {
    let yes = |key: &str| report.get(key) == Some(&Value::Bool(true));
    let options: Vec<&str> =
        report.get("options").and_then(Value::as_array).map(|o| o.iter().filter_map(Value::as_str).collect()).unwrap_or_default();
    let answered = host
        .smoke
        .as_ref()
        .and_then(|s| s.fake_record())
        .map(|record| std::fs::read_to_string(state(record).join("stdin.txt")).unwrap_or_default())
        .is_some_and(|stdin| stdin.lines().any(|l| l.contains("\"action\":\"accept\"") && l.contains("\"q0\":\"Red\"")));
    let (status, output) = {
        let view = host.view();
        let pane = view.snapshot.session(PANE).cloned();
        let output = pane.as_ref().and_then(|p| p.graph_runs().last().and_then(|r| r.final_output().map(str::to_string)));
        (pane.map(|p| p.status().to_string()), output)
    };
    vec![
        ("acpNoError", report.get("error").is_none()),
        (
            "acpQuestionCard",
            report.get("outcome").and_then(Value::as_str) == Some("started") && yes("cardShown") && options == ["Red", "Green", "Blue"],
        ),
        ("acpAnswered", yes("submitEnabled") && yes("cardGone") && answered),
        (
            "acpDiagram",
            report.get("answerShown").and_then(Value::as_str).is_some()
                && matches!(status.as_deref(), Some("completed" | "idle"))
                && output.as_deref() == Some(ANSWER),
        ),
    ]
}
