//! The smoke's accounts, sign-in card and usage steps (C# MainWindow.AccountUsage.cs:288-345 `RunAccountUsageSmoke`, `ClaudeResetSmoke`,
//! `CliAccountSmoke`). Owner: W6-C.
//!
//! - `accountUsage`: the Claude panes carry a fixture of the limits a run reported, the direct lookup off, an Antigravity pane added;
//!   the page's status bar shows a chip per provider with an AI pane (Claude's the reported session and weekly windows), its popover a
//!   card each with the direct lookup's switch off and the shared note, and the dashboard's model has Claude's bars; then the state is
//!   put back (`restored`). No CLI is asked and nothing is looked up: a smoke queues no read.
//! - `usageReset`: the 리셋권 read over a fixture clock and a GET-only fake transport renders cedar_ember available and juniper_tide
//!   unknown, with the four allowed GETs and no other method (C# `ClaudeResetSmoke`; L1427's "wired into the app's smoke").
//! - `cliAccountsSection`: Settings' CLI tab shows `CliAccountSmoke`'s fixture statuses (signed in with an account, signed out, not
//!   installed, one that cannot sign out), with no account read; the change button opens dialog D8, cancelled, and nothing is signed
//!   out.
//! - `loginCard` (Rust only): a fixture retry on a pane draws its sign-in card; its dismiss (`loginAction dismiss`) removes it.
//!
//! The step's contract (smoke.rs): [`start`] begins it and answers whether it runs; its probe in the page reports through
//! `smoke_probe`, and [`checks`] reads that report. The probe reaches the host's side through the `smoke_accounts` command.

use std::sync::{Arc, Mutex};

use mighty_accounts::accounts::{self, copy as account_copy};
use mighty_accounts::usage::{self, claude_reset_smoke};
use mighty_core::model::{AppSnapshot, RunSession, SessionUsage};
use mighty_core::wire;
use serde_json::{Value, json};

use crate::host::Host;

/// The step's probe; `__WORDS__` is filled in with the copy it looks for.
const PROBE: &str = r#"(async () => {
  const until = async (test, ms) => { const end = performance.now() + ms; while (performance.now() < end) { const v = await test(); if (v) return v; await new Promise((r) => setTimeout(r, 50)); } return test(); };
  const invoke = window.__TAURI_INTERNALS__.invoke;
  const host = (action, args) => invoke('smoke_accounts', { action, args: args ?? null });
  const words = __WORDS__;
  const report = {};
  const press = (scope, aid) => { const b = scope?.querySelector(`[data-aid="${aid}"] button[data-native-id]`) ?? scope?.querySelector(`[data-aid="${aid}"]`); b?.click(); return !!b; };
  try {
    // accountUsage: the chips, the popover, the dashboard's bars.
    const fixture = await host('usageFixture');
    report.providers = fixture.providers;
    const chips = () => [...document.querySelectorAll('[data-testid=usage-chips] .usage-chip')];
    report.chipsShown = !!(await until(() => chips().length === fixture.providers.length && chips().length > 0, 15000));
    report.chips = chips().length;
    report.claudeChip = chips().find((c) => (c.getAttribute('aria-label') ?? '').startsWith(words.claudeName))?.textContent ?? '';
    document.querySelector('[data-testid=usage-chips]')?.click();
    const popover = () => document.querySelector('[data-testid=usage-popover]');
    report.popoverShown = !!(await until(() => popover() && popover().querySelectorAll('.usage-card').length === fixture.providers.length, 10000));
    report.cards = popover()?.querySelectorAll('.usage-card').length ?? 0;
    report.windowRows = popover()?.querySelectorAll('.usage-window').length ?? 0;
    const direct = popover()?.querySelector('.usage-direct input[role=switch]');
    report.directShown = !!direct;
    report.directOff = !!direct && !direct.checked;
    report.sharedNote = (popover()?.textContent ?? '').includes(words.shared);
    document.querySelector('[data-testid=usage-chips]')?.click();
    await until(() => !popover(), 5000);
    const state = await host('usageState');
    Object.assign(report, { hostChips: state.chips, hostCards: state.cards, directLookupDefaultOff: state.directOff, dashboardBars: state.bars, geminiNote: state.geminiNote });
    report.restored = (await host('usageRestore')).restored;
    report.usageReset = await host('usageReset');
    // cliAccountsSection: the fixture statuses in Settings' CLI tab, then D8 opened and cancelled.
    await host('cliOpen');
    const form = () => document.querySelector('[data-native-view=settings]');
    report.cliShown = !!(await until(() => form()?.querySelector('[data-aid="cli-account-row-claude"]'), 15000));
    report.summaries = [...(form()?.querySelectorAll('[data-aid^="cli-account-status-"]') ?? [])].map((e) => e.textContent ?? '');
    report.rows = [...(form()?.querySelectorAll('[data-aid^="cli-account-row-"]') ?? [])].map((e) => e.dataset.aid);
    report.changePressed = press(form(), 'cli-account-change-claude');
    const dialog = () => [...document.querySelectorAll('[data-native-view^=dialog-]')].pop();
    report.confirmationShown = !!(await until(() => dialog() && (dialog().textContent ?? '').includes(words.confirm), 10000));
    dialog()?.querySelector('[data-testid=native-button-close]')?.click();
    report.cancelled = !!(await until(async () => !dialog() && !(await host('cliState')).changing, 10000));
    report.signedOut = (await host('cliState')).signedOut;
    await host('cliClose');
    await until(() => !form(), 10000);
    // loginCard: a fixture retry draws the pane's sign-in card; its dismiss removes it.
    const login = await host('loginCard');
    report.loginPane = login.pane;
    const card = () => document.querySelector(`[data-session-id="${login.pane}"] [data-card=login]`);
    report.loginShown = !!login.pane && !!(await until(() => card(), 15000));
    report.loginTitle = card()?.querySelector('.login-title')?.textContent ?? '';
    card()?.querySelector('.card-icon-button')?.click();
    report.loginDismissed = !!(await until(async () => !card() && !(await host('loginState', { pane: login.pane })).kept, 10000));
  } catch (error) {
    report.error = String(error);
  }
  await invoke('smoke_probe', { probe: report });
})();"#;

/// Starts the step: its probe, with the copy it looks for.
pub fn start(host: &Arc<Host>) -> bool {
    let Some(smoke) = &host.smoke else { return false };
    let words = json!({
        "claudeName": mighty_run::providers::name("claude"),
        "shared": usage::copy::shared_limits_note(),
        "confirm": account_copy::confirm_message_template().replace("{provider}", "Claude"),
    });
    host.log.line("smoke: accounts steps");
    smoke.eval(host, &PROBE.replace("__WORDS__", &words.to_string()));
    true
}

/// The step's checks from its report, each `(name, passed)` in `smoke-result.json`'s `checks`.
pub fn checks(report: &Value) -> Vec<(&'static str, bool)> {
    let yes = |key: &str| report.get(key) == Some(&Value::Bool(true));
    let number = |key: &str| report.get(key).and_then(Value::as_u64).unwrap_or(0);
    let text = |key: &str| report.get(key).and_then(Value::as_str).unwrap_or("").to_string();
    let providers = report.get("providers").and_then(Value::as_array).map_or(0, Vec::len) as u64;
    let claude = text("claudeChip");
    let usage = report.get("usageReset").cloned().unwrap_or(Value::Null);
    let summaries: Vec<String> = report
        .get("summaries")
        .and_then(Value::as_array)
        .map(|s| s.iter().filter_map(Value::as_str).map(str::to_string).collect())
        .unwrap_or_default();
    let fixtures = accounts::smoke_fixture_statuses();
    vec![
        ("accountsNoError", report.get("error").is_none()),
        (
            "accountUsage",
            yes("chipsShown")
                && providers >= 2
                && number("chips") == providers
                && number("hostChips") == providers
                && yes("popoverShown")
                && number("cards") == providers
                && yes("directShown")
                && yes("directOff")
                && yes("directLookupDefaultOff")
                && yes("sharedNote")
                && claude.contains(&usage::copy::window_session())
                && claude.contains(&usage::copy::window_weekly())
                && number("dashboardBars") >= 1
                && !text("geminiNote").is_empty()
                && yes("restored"),
        ),
        (
            "usageReset",
            usage.get("passed") == Some(&Value::Bool(true))
                && usage.get("cedarEmber").and_then(Value::as_str) == Some("available")
                && usage.get("juniperTide").and_then(Value::as_str) == Some("unknown")
                && usage.get("fakeTransportPostCount").and_then(Value::as_u64) == Some(0),
        ),
        (
            "cliAccountsSection",
            yes("cliShown")
                && summaries.iter().any(|s| s.contains("me@example.com"))
                && summaries.contains(&account_copy::summary_signed_out())
                && summaries.iter().any(|s| *s == fixtures[2].detail || *s == account_copy::status_not_installed())
                && yes("changePressed")
                && yes("confirmationShown")
                && yes("cancelled")
                && report.get("signedOut") == Some(&Value::Bool(false)),
        ),
        ("loginCard", yes("loginShown") && !text("loginTitle").is_empty() && yes("loginDismissed")),
    ]
}

/// What the usage fixture changed, put back by `usageRestore`.
static SAVED: Mutex<Option<(Vec<Arc<RunSession>>, bool)>> = Mutex::new(None);

fn saved() -> std::sync::MutexGuard<'static, Option<(Vec<Arc<RunSession>>, bool)>> {
    SAVED.lock().unwrap_or_else(|p| p.into_inner())
}

/// `RunAccountUsageSmoke`'s state: the direct lookup off, every Claude agent pane carrying the limits a run reported a minute ago (the
/// session window at 12.5% resetting in three hours, the weekly one at 44%), and an Antigravity pane in the front workspace when it
/// has none, so its card (no figures of its own) is among the popover's.
fn usage_fixture(state: &mut AppSnapshot) {
    let now = chrono::Utc::now();
    let o = |at: chrono::DateTime<chrono::Utc>| usage::timestamp(at);
    let reported = json!({
        "provider": "claude", "source": "smoke.fixture", "tokenScope": "response", "updatedAt": o(now),
        "rateLimits": [{"kind": "five_hour", "percentUsed": 12.5, "resetsAt": o(now + chrono::TimeDelta::hours(3))}, {"kind": "seven_day", "percentUsed": 44}],
        "rateLimitsUpdatedAt": o(now - chrono::TimeDelta::minutes(1)),
    });
    let fixture = SessionUsage::from_json(&reported);
    state.claude_direct_usage_lookup_enabled = false;
    for pane in state.sessions_mut() {
        if pane.kind() != "shell" && pane.provider() == "claude" {
            *pane = Arc::new(RunSession { session_usage: fixture.clone(), ..(**pane).clone() });
        }
    }
    let has_gemini = state.sessions().iter().any(|p| p.kind() != "shell" && p.provider() == "gemini");
    if let (false, Some(front)) = (has_gemini, state.active_workspace_id.clone())
        && let Some(model) = state.sessions().iter().find(|p| p.kind() == "claude").cloned()
    {
        let pane = RunSession {
            id: Some(wire::new_id()),
            workspace_id: Some(front),
            title: Some(mighty_run::providers::name("gemini").into()),
            kind: Some("claude".into()),
            status: Some("completed".into()),
            provider: Some("gemini".into()),
            model: Some("default".into()),
            logs: None,
            session_usage: None,
            resume_id: None,
            ..(*model).clone()
        };
        state.sessions.get_or_insert_with(Vec::new).push(Arc::new(pane));
    }
}

/// An agent pane the page shows: the active one when it is an agent's, else the front workspace's first.
fn shown_agent_pane(host: &Host) -> Option<RunSession> {
    let view = host.view();
    let state = &view.snapshot;
    let agent = |p: &&Arc<RunSession>| p.kind() == "claude" && wire::PROVIDERS.contains(&p.provider());
    let active = state.active_session_id.as_deref().and_then(|id| state.session(id)).filter(agent);
    let front = state.active_workspace_id.clone();
    active.or_else(|| state.sessions().iter().filter(|p| Some(p.workspace_id().to_string()) == front).find(agent)).map(|p| (**p).clone())
}

/// The step's host side, for its probe; smoke only.
#[tauri::command]
pub async fn smoke_accounts(action: String, args: Value, host: tauri::State<'_, Arc<Host>>) -> Result<Value, String> {
    if host.smoke.is_none() {
        return Err("smoke only".into());
    }
    let host = host.inner().clone();
    match action.as_str() {
        "usageFixture" => {
            {
                let mut view = host.view();
                *saved() = Some((view.snapshot.sessions().to_vec(), view.snapshot.claude_direct_usage_lookup_enabled));
                view.update(usage_fixture)?;
            }
            host.schedule();
            // The state the frame model reads next: its providers, in order.
            let snapshot = host.view().snapshot.clone();
            let model = crate::usage::shell(&host, &snapshot);
            let providers = crate::usage::of(&host).status.providers();
            Ok(json!({"providers": providers, "chips": model.and_then(|m| m.chips).map_or(0, |c| c.len())}))
        }
        "usageState" => {
            let snapshot = host.view().snapshot.clone();
            let model = crate::usage::shell(&host, &snapshot).ok_or("no usage")?;
            let usage = crate::usage::of(&host);
            let gemini = model.details.cards.iter().find(|c| c.provider == "gemini");
            Ok(json!({
                "chips": model.chips.as_ref().map_or(0, Vec::len),
                "cards": model.details.cards.len(),
                "directOff": !usage.status.direct_claude_lookup_enabled() && !usage.status.targets().iter().any(|p| p == "claude"),
                "bars": model.bars.iter().filter(|b| b.provider == "claude").map(|b| b.bars.len()).sum::<usize>(),
                "geminiNote": gemini.and_then(|c| c.detail.clone().or_else(|| c.note.clone())).unwrap_or_default(),
            }))
        }
        "usageRestore" => {
            let Some((sessions, direct)) = saved().take() else { return Ok(json!({"restored": false})) };
            host.view().update(|state| {
                state.sessions = Some(sessions);
                state.claude_direct_usage_lookup_enabled = direct;
            })?;
            crate::usage::of(&host).status.set_direct_claude_lookup(direct);
            host.schedule();
            Ok(json!({"restored": true}))
        }
        "usageReset" => {
            let result = claude_reset_smoke::run().await;
            Ok(json!({
                "passed": result.passed,
                "fakeTransportPostCount": result.fake_transport_post_count,
                "cedarEmber": result.cedar_ember_state,
                "juniperTide": result.juniper_tide_state,
                "requests": result.requests,
                "lines": result.lines,
            }))
        }
        "cliOpen" => {
            crate::settings::cli_accounts::show_fixture(Some(accounts::smoke_fixture_statuses()));
            crate::settings::open(&host, Some("cli"));
            Ok(Value::Null)
        }
        "cliState" => {
            let accounts = crate::accounts::of(&host);
            Ok(
                json!({"changing": crate::accounts::is_changing(&accounts, "claude"), "signedOut": crate::accounts::signed_out_in_smoke(&host)}),
            )
        }
        "cliClose" => {
            crate::settings::close(&host);
            crate::settings::cli_accounts::show_fixture(None);
            Ok(Value::Null)
        }
        "loginCard" => {
            let Some(pane) = shown_agent_pane(&host) else { return Ok(json!({"pane": null})) };
            crate::accounts::smoke_retry(&host, &pane);
            crate::panes::mark_all(&host);
            host.schedule();
            Ok(json!({"pane": pane.id()}))
        }
        "loginState" => {
            let pane = args.get("pane").and_then(Value::as_str).unwrap_or("");
            Ok(json!({"kept": crate::accounts::has_retry(&host, pane)}))
        }
        other => Err(format!("unknown smoke action {other}")),
    }
}

#[cfg(test)]
mod tests {
    use mighty_core::Wire;

    use super::*;

    /// The checks pass for a report as the probe makes it when everything showed, and fail for a missing part.
    #[test]
    fn the_checks_read_the_probes_report() {
        mighty_run::strings::set_language("ko");
        let claude_chip = format!("{} 13% {} 44%", usage::copy::window_session(), usage::copy::window_weekly());
        let report = json!({
            "providers": ["claude", "gemini"], "chipsShown": true, "chips": 2, "hostChips": 2, "claudeChip": claude_chip,
            "popoverShown": true, "cards": 2, "windowRows": 2, "directShown": true, "directOff": true, "directLookupDefaultOff": true,
            "sharedNote": true, "dashboardBars": 2, "geminiNote": "x", "restored": true,
            "usageReset": {"passed": true, "cedarEmber": "available", "juniperTide": "unknown", "fakeTransportPostCount": 0},
            "cliShown": true, "summaries": ["me@example.com · Max · Claude 구독", account_copy::summary_signed_out(), accounts::smoke_fixture_statuses()[2].detail],
            "changePressed": true, "confirmationShown": true, "cancelled": true, "signedOut": false,
            "loginShown": true, "loginTitle": "Claude 로그인", "loginDismissed": true,
        });
        let all = checks(&report);
        assert!(all.iter().all(|(_, passed)| *passed), "{all:?}");
        assert_eq!(
            all.iter().map(|(name, _)| *name).collect::<Vec<_>>(),
            ["accountsNoError", "accountUsage", "usageReset", "cliAccountsSection", "loginCard"]
        );
        let mut missing = report.clone();
        missing["usageReset"]["juniperTide"] = json!("available");
        missing["signedOut"] = json!(true);
        let failed: Vec<&str> = checks(&missing).into_iter().filter(|(_, p)| !p).map(|(n, _)| n).collect();
        assert_eq!(failed, ["usageReset", "cliAccountsSection"]);
    }

    /// The usage fixture puts the reported limits on Claude's agent panes, the direct lookup off, and an Antigravity pane in front.
    #[test]
    fn the_usage_fixture_reports_claudes_limits_and_adds_antigravity() {
        let mut state = AppSnapshot::read(&json!({
            "activeWorkspaceId": "w", "claudeDirectUsageLookupEnabled": true,
            "workspaces": [{"id": "w", "path": "/fixture"}],
            "sessions": [{"id": "a", "workspaceId": "w", "kind": "claude", "provider": "claude"}, {"id": "b", "workspaceId": "w", "kind": "shell", "provider": "claude"}],
        }))
        .unwrap();
        usage_fixture(&mut state);
        assert!(!state.claude_direct_usage_lookup_enabled);
        let claude = state.session("a").unwrap();
        assert!(claude.session_usage.as_ref().and_then(|u| u.rate_limits.as_ref()).is_some_and(|l| l.len() == 2), "{claude:?}");
        assert!(state.session("b").unwrap().session_usage.is_none(), "a shell pane reports nothing");
        assert!(state.sessions().iter().any(|p| p.provider() == "gemini" && p.workspace_id() == "w"), "an Antigravity pane in front");
    }
}
