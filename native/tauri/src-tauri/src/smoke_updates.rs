//! The smoke's plugin and update steps (C# MainWindow.AutomaticUpdatesSmoke.cs, MainWindow.Plugins.cs:568-965). Owner: W6-D.
//!
//! The step's contract (smoke.rs): [`start`] begins it and answers whether it runs; a running step ends by reporting once — its probe
//! in the page invoking `smoke_probe` — and [`checks`] reads that report. The probe reaches the host's side through the
//! `smoke_updates` command. Nothing here updates anything or starts a CLI: the plugin window reads fixtures (C#'s
//! `smokePluginRead`), installs through a fake reader (`smokeReaderFactory`), and the manual updater is a stand-in.
//!
//! - `claudePluginList`, `codexPluginList` (C# `RunClaudePluginSmoke`, `RunCodexPluginSmoke`): the window opened from a pane's plugin
//!   button (`openPlugins`) or, without one, as `openPlugins` opens it, drawn by the page as the panel `plugins`; its tabs, counts,
//!   filter, search, reload and the sentences of a missing, empty or unsupported CLI read off the page; the ✕ closes it.
//! - `pluginMarketplace` (`RunPluginMarketplaceSmoke`): the scope choices, an install and a marketplace refresh pressed in the page,
//!   an install held until the page's cancel stops it (the ✕ refused meanwhile), Codex's one scope.
//! - The C# checks that read WinUI source become what the running window shows: `pluginWindowWired` and `codexPluginWindowWired`
//!   (`WindowIsARealWinUISurfaceWiredIntoTheApp`), `pluginMarketplaceWired` (`WindowIsWiredIntoTheRunningAppAndTheSmokeRun`),
//!   `pluginOperationsOwned` (the WinUI half of `TheRunningAppOwnsTheOperationsObject`).
//! - `automaticUpdates` (`RunAutomaticUpdatesSmoke`), host-side over the live seams and the composer's model: the plugin switch's
//!   default and its write, an automatic update holding only its provider's local pane (the phone's half is W8), requests queued
//!   under the hold kept and drained in order once it is released, the page's `stop` dropping a held request, a manual update
//!   blocking only its provider without consuming the draft or the queue, a manual plugin change blocking Claude and Codex. A queue's
//!   drain is a stand-in that records the pane (C#'s `smokeStart`), so nothing queued here ever starts a CLI.

use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use mighty_core::model::{RunSession, Workspace};
use mighty_native::ui::{Input, Kind};
use mighty_updates::cli_update::CliUpdateResult;
use mighty_updates::plugins::browser::ClaudePluginBrowser;
use mighty_updates::plugins::smoke::{FakeMarketplaceReader, claude_snapshot, codex_snapshot};
use mighty_updates::plugins::strings::{codex as codex_strings, plugin as plugin_strings};
use mighty_updates::plugins::{ClaudePluginSnapshot, PluginReader, status, support};
use mighty_updates::runner::CancellationToken;
use mighty_updates::view;
use serde_json::{Value, json};

use crate::host::Host;

/// The step's probe.
const PROBE: &str = r#"(async () => {
  const until = async (test, ms) => { const end = performance.now() + ms; while (performance.now() < end) { const v = await test(); if (v) return v; await new Promise((r) => setTimeout(r, 50)); } return test(); };
  const invoke = window.__TAURI_INTERNALS__.invoke;
  const host = (action, args) => invoke('smoke_updates', { action, args: args ?? null });
  const view = () => [...document.querySelectorAll('[data-native-view^=dialog-][data-dialog-kind=panel]')].pop();
  const q = (p, part) => view()?.querySelector(`[data-aid="${p}-plugin-${part}"]`);
  const press = (p, part) => { const b = q(p, part)?.querySelector('button'); if (!b || b.disabled) return false; b.click(); return true; };
  const rows = (p) => view()?.querySelectorAll(`[data-aid^="${p}-plugin-row-"]`).length ?? -1;
  const words = (p, part) => (q(p, part)?.textContent ?? '').trim();
  const choose = (select, index) => { Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, String(index)); select.dispatchEvent(new Event('change', { bubbles: true })); };
  const type = (input, text) => { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, text); input.dispatchEvent(new Event('input', { bubbles: true })); };
  const panel = () => host('panel');
  const settled = async (test) => until(async () => { const s = await panel(); return s.open && !s.loading && rows(s.provider) === s.rows && (await test(s)); }, 10000);
  const loaded = async (p) => until(async () => { const s = await panel(); return view() && s.open && s.provider === p && s.reads >= 1 && !s.loading && rows(p) === s.rows; }, 15000);
  const close = async () => { view()?.querySelector('[data-testid=native-close]')?.click(); return !!(await until(async () => !view() && !(await panel()).open, 10000)); };
  const aids = () => [...(view()?.querySelectorAll('[data-aid]') ?? [])].map((e) => e.dataset.aid);
  const report = { claude: {}, codex: {}, market: {} };
  try {
    // ---- the Claude list, opened from a Claude pane's plugin button when the page shows one ----
    const ids = (await host('claudeList')).panes ?? [];
    let button = null;
    for (const id of ids) { button = document.querySelector(`section.pane[data-session-id="${id}"] [data-testid=pane-plugins]`); if (button) break; }
    const c = report.claude;
    c.fromPane = !!button;
    if (button) button.click();
    if (!button || !(await until(() => view(), 5000))) { c.fromPane = false; await host('openClaude'); }
    c.loaded = !!(await loaded('claude'));
    c.title = view()?.querySelector('[role=dialog]')?.getAttribute('aria-label') ?? '';
    c.controls = view()?.querySelectorAll('[data-native-id]').length ?? 0;
    c.closeButton = !!view()?.querySelector('[data-testid=native-close]');
    c.installedTab = words('claude', 'tab-installed');
    c.marketplaceTab = words('claude', 'tab-marketplace');
    c.installedRows = rows('claude');
    c.texts = [...(view()?.querySelectorAll('.native-text') ?? [])].map((e) => e.textContent);
    press('claude', 'tab-marketplace');
    await settled(async (s) => s.tab === 'marketplace');
    c.availableRows = rows('claude');
    const filter = q('claude', 'marketplace-filter')?.querySelector('select');
    c.filterOptions = filter ? [...filter.options].map((o) => o.textContent) : [];
    if (filter) choose(filter, c.filterOptions.indexOf('other'));
    await settled(async (s) => s.filter === 'other');
    c.filteredRows = rows('claude');
    const search = q('claude', 'search')?.querySelector('input');
    c.placeholder = search?.placeholder ?? '';
    if (search) type(search, 'no-such-plugin-zz');
    await settled(async (s) => s.search === 'no-such-plugin-zz');
    c.searchedRows = rows('claude');
    if (search) type(search, '');
    if (filter) choose(filter, 0);
    await settled(async (s) => s.search === '' && s.filter === '');
    c.aids = aids();
    c.dialogButtons = view()?.querySelectorAll('[data-testid^=native-button-]').length ?? -1;
    c.reloadText = (q('claude', 'reload')?.querySelector('.native-text')?.textContent ?? '').trim();
    press('claude', 'reload');
    await until(async () => (await panel()).reads >= 2 && words('claude', 'load-status').length > 0, 10000);
    c.reloadedStatus = words('claude', 'load-status');
    c.reads = (await panel()).reads;
    c.closed = await close();

    // ---- the Codex list: a fixture, then an empty registry, then a CLI without the JSON plugin commands ----
    const x = report.codex;
    await host('codexList');
    x.loaded = !!(await loaded('codex'));
    x.title = view()?.querySelector('[role=dialog]')?.getAttribute('aria-label') ?? '';
    x.controls = view()?.querySelectorAll('[data-native-id]').length ?? 0;
    x.closeButton = !!view()?.querySelector('[data-testid=native-close]');
    x.installedTab = words('codex', 'tab-installed');
    x.marketplaceTab = words('codex', 'tab-marketplace');
    x.installedRows = rows('codex');
    x.installedSubtitle = (await panel()).installedSubtitle;
    x.texts = [...(view()?.querySelectorAll('.native-text') ?? [])].map((e) => e.textContent);
    x.readyStatus = words('codex', 'load-status');
    press('codex', 'tab-marketplace');
    await settled(async (s) => s.tab === 'marketplace');
    x.availableRows = rows('codex');
    const codexFilter = q('codex', 'marketplace-filter')?.querySelector('select');
    if (codexFilter) choose(codexFilter, [...codexFilter.options].map((o) => o.textContent).indexOf('sample'));
    await settled(async (s) => s.filter === 'sample');
    x.filteredRows = rows('codex');
    const codexSearch = q('codex', 'search')?.querySelector('input');
    if (codexSearch) type(codexSearch, 'no-such-plugin-zz');
    await settled(async (s) => s.search === 'no-such-plugin-zz');
    x.searchedRows = rows('codex');
    if (codexSearch) type(codexSearch, '');
    if (codexFilter) choose(codexFilter, 0);
    await settled(async (s) => s.search === '' && s.filter === '');
    x.aids = aids();
    x.dialogButtons = view()?.querySelectorAll('[data-testid^=native-button-]').length ?? -1;
    press('codex', 'reload');
    await until(async () => (await panel()).reads >= 2 && words('codex', 'marketplace-help').length > 0, 10000);
    x.noMarketplaceHelp = words('codex', 'marketplace-help');
    press('codex', 'reload');
    await until(async () => (await panel()).reads >= 3 && !(await panel()).loading, 10000);
    await until(() => words('codex', 'load-status').length > 0 && words('codex', 'load-status') !== x.readyStatus, 5000);
    x.unsupportedStatus = words('codex', 'load-status');
    x.reads = (await panel()).reads;
    x.closed = await close();

    // ---- the marketplace: install and refresh pressed, a held install cancelled, Codex's scope ----
    const m = report.market;
    m.before = await host('state');
    await host('marketClaude', { hold: false });
    m.loaded = !!(await loaded('claude'));
    press('claude', 'tab-marketplace');
    await settled(async (s) => s.tab === 'marketplace');
    m.claudeScopes = q('claude', 'scope-picker')?.querySelector('select')?.options.length ?? 0;
    m.claudeScopeValues = (await panel()).scopeValues;
    m.aids = aids();
    m.canInstallDocs = !(q('claude', 'install-docs@sample')?.querySelector('button')?.disabled ?? true);
    press('claude', 'install-docs@sample');
    await until(async () => { const s = await panel(); return !s.mutating && s.result.length > 0 && words('claude', 'operation-result') === s.result; }, 10000);
    m.installResult = words('claude', 'operation-result');
    m.resultAids = aids();
    press('claude', 'refresh-marketplaces');
    await until(async () => { const s = await panel(); return !s.mutating && s.result !== m.installResult && words('claude', 'operation-result') === s.result; }, 10000);
    m.refreshResult = words('claude', 'operation-result');
    m.refreshStatus = (await panel()).resultStatus;
    m.closed = await close();

    await host('marketClaude', { hold: true });
    await loaded('claude');
    press('claude', 'tab-marketplace');
    await settled(async (s) => s.tab === 'marketplace');
    press('claude', 'install-docs@sample');
    m.held = !!(await until(async () => (await panel()).mutating && q('claude', 'operation-progress') && q('claude', 'cancel-operation'), 10000));
    const busy = await panel();
    m.progress = words('claude', 'operation-progress');
    m.heldAids = aids();
    m.mutating = busy.mutating; m.canClose = busy.canClose; m.canCancel = busy.canCancel;
    m.operationsRunning = (await host('state')).operationsRunning;
    m.closeBusy = !!view()?.querySelector('[data-testid=native-close]')?.classList.contains('busy');
    view()?.querySelector('[data-testid=native-close]')?.click();
    await new Promise((r) => setTimeout(r, 400));
    m.closeRefused = !!view() && (await panel()).open;
    press('claude', 'cancel-operation');
    await until(async () => { const s = await panel(); return !s.mutating && s.result.length > 0; }, 10000);
    await until(async () => words('claude', 'operation-result') === (await panel()).result, 5000);
    m.cancelResult = words('claude', 'operation-result');
    const after = await panel();
    m.idleAfterCancel = after.canClose && !after.mutating;
    m.cancelClosed = await close();

    await host('marketCodex');
    await loaded('codex');
    press('codex', 'tab-marketplace');
    await settled(async (s) => s.tab === 'marketplace');
    m.codexScopes = q('codex', 'scope-picker')?.querySelector('select')?.options.length ?? 0;
    m.codexScopeValues = (await panel()).scopeValues;
    m.codexClosed = await close();
    m.after = await host('state');

    // ---- the automatic updates and their holds, host-side ----
    report.automatic = await host('automatic');
  } catch (error) {
    report.error = String(error);
  }
  try { await host('restore'); } catch (error) { report.restoreError = String(error); }
  await invoke('smoke_probe', { probe: report });
})();"#;

/// How many list reads the step's stand-in answered (C#'s `reads`).
static READS: AtomicUsize = AtomicUsize::new(0);

/// Starts the step; false outside a smoke or before the run engine (and with it the updates) exists.
pub fn start(host: &Arc<Host>) -> bool {
    let Some(smoke) = &host.smoke else { return false };
    if crate::updates::of(host).is_none() {
        host.log.line("smoke: no updates step (the run engine is not attached)");
        return false;
    }
    host.log.line("smoke: updates steps");
    smoke.eval(host, PROBE);
    true
}

fn text<'a>(value: &'a Value, key: &str) -> &'a str {
    value.get(key).and_then(Value::as_str).unwrap_or("")
}

fn number(value: &Value, key: &str) -> i64 {
    value.get(key).and_then(Value::as_i64).unwrap_or(-1)
}

fn yes(value: &Value, key: &str) -> bool {
    value.get(key) == Some(&Value::Bool(true))
}

fn strings_of(value: &Value, key: &str) -> Vec<String> {
    value.get(key).and_then(Value::as_array).map(|a| a.iter().filter_map(Value::as_str).map(str::to_string).collect()).unwrap_or_default()
}

/// The ids the page drew that name a change, and whether each is one the Mac has (C#'s read-only guard over the window's ids).
fn changes(aids: &[String], provider: &str) -> (usize, bool) {
    let changing: Vec<&String> = aids.iter().filter(|aid| support::names_a_change(aid, provider)).collect();
    (changing.len(), changing.iter().all(|aid| support::names_allowed_change(aid, provider)))
}

fn tab_label(title: String, count: usize) -> String {
    plugin_strings::tab_count_template().replace("{title}", &title).replace("{count}", &count.to_string())
}

/// The step's checks, each `(name, passed)` in `smoke-result.json`'s `checks`.
pub fn checks(report: &Value) -> Vec<(&'static str, bool)> {
    let no_error = report.get("error").is_none() && report.get("restoreError").is_none();
    let empty = json!({});
    let c = report.get("claude").unwrap_or(&empty);
    let x = report.get("codex").unwrap_or(&empty);
    let m = report.get("market").unwrap_or(&empty);
    let a = report.get("automatic").unwrap_or(&empty);

    let (claude_changes, claude_allowed) = changes(&strings_of(c, "aids"), "claude");
    let claude_list = no_error
        && yes(c, "loaded")
        && text(c, "title") == plugin_strings::title_template().replace("{provider}", "Claude")
        && text(c, "installedTab") == tab_label(plugin_strings::tab_installed(), 2)
        && text(c, "marketplaceTab") == tab_label(plugin_strings::tab_marketplace(), 3)
        && (number(c, "installedRows"), number(c, "availableRows"), number(c, "filteredRows"), number(c, "searchedRows")) == (2, 3, 1, 0)
        && strings_of(c, "filterOptions") == [plugin_strings::filter_all(), "other".into(), "sample".into()]
        && text(c, "placeholder") == plugin_strings::search_placeholder()
        && text(c, "reloadText") == plugin_strings::button_reload()
        // scope-picker + refresh-marketplaces + 3 install-* buttons
        && claude_changes == 5
        && text(c, "reloadedStatus") == plugin_strings::detail_missing_cli()
        && number(c, "reads") == 2
        && yes(c, "closed");
    // Opened by the pane's own plugin button (`openPlugins`), as the user opens it.
    let claude_wired = yes(c, "fromPane")
        && yes(c, "loaded")
        && number(c, "controls") >= 2
        && yes(c, "closeButton")
        && number(c, "dialogButtons") == 0
        && claude_allowed
        && strings_of(c, "texts").contains(&plugin_strings::footer_note())
        && ["tab-installed", "marketplace-filter", "reload", "search"]
            .iter()
            .all(|part| strings_of(c, "aids").contains(&support::automation_id("claude", part)));

    let (codex_changes, codex_allowed) = changes(&strings_of(x, "aids"), "codex");
    let codex_list = no_error
        && yes(x, "loaded")
        && text(x, "title") == plugin_strings::title_template().replace("{provider}", "Codex")
        && text(x, "installedTab") == tab_label(plugin_strings::tab_installed(), 1)
        && text(x, "marketplaceTab") == tab_label(plugin_strings::tab_marketplace(), 2)
        && number(x, "installedRows") == 1
        && text(x, "installedSubtitle") == plugin_strings::subtitle_template().replace("{left}", "sample").replace("{right}", &plugin_strings::scope_user())
        && (number(x, "availableRows"), number(x, "filteredRows"), number(x, "searchedRows")) == (2, 1, 0)
        // scope-picker + refresh-marketplaces + 2 install-* buttons
        && codex_changes == 4
        && strings_of(x, "texts").contains(&codex_strings::footer_note())
        && text(x, "readyStatus") == codex_strings::detail_ready()
        && text(x, "noMarketplaceHelp") == codex_strings::marketplace_help()
        && text(x, "unsupportedStatus") == codex_strings::detail_unsupported()
        && number(x, "reads") == 3
        && yes(x, "closed");
    let codex_wired =
        yes(x, "loaded") && number(x, "controls") >= 2 && yes(x, "closeButton") && number(x, "dialogButtons") == 0 && codex_allowed;

    let marketplace = no_error
        && yes(m, "loaded")
        && number(m, "claudeScopes") == 3
        && strings_of(m, "claudeScopeValues") == ["local", "project", "user"]
        && yes(m, "canInstallDocs")
        && text(m, "installResult") == plugin_strings::install_succeeded()
        && text(m, "refreshStatus") == status::SUCCEEDED
        && yes(m, "held")
        && text(m, "progress") == plugin_strings::progress_installing()
        && yes(m, "mutating")
        && !yes(m, "canClose")
        && yes(m, "canCancel")
        && yes(m, "closeBusy")
        && yes(m, "closeRefused")
        && text(m, "cancelResult") == plugin_strings::operation_cancelled_by_user()
        && yes(m, "idleAfterCancel")
        && number(m, "codexScopes") == 1
        && strings_of(m, "codexScopeValues") == ["user"]
        && yes(m, "closed")
        && yes(m, "cancelClosed")
        && yes(m, "codexClosed");
    let market_aids = strings_of(m, "aids");
    let held_aids = strings_of(m, "heldAids");
    let id = |part: &str| support::automation_id("claude", part);
    let result_aids = strings_of(m, "resultAids");
    let marketplace_wired = ["scope-picker", "refresh-marketplaces"].iter().all(|part| market_aids.contains(&id(part)))
        && result_aids.contains(&id("operation-result"))
        && ["cancel-operation", "operation-progress"].iter().all(|part| held_aids.contains(&id(part)))
        && ["fmt@sample", "docs@sample", "ship@other"].iter().all(|plugin| market_aids.contains(&id(&format!("install-{plugin}"))));
    let (before, after) = (m.get("before").unwrap_or(&empty), m.get("after").unwrap_or(&empty));
    let operations_owned = yes(before, "usesInstalledCli")
        && before.get("operations").is_some()
        && before.get("operations") == after.get("operations")
        && yes(m, "operationsRunning")
        && !yes(after, "operationsRunning");

    vec![
        ("claudePluginList", claude_list),
        ("codexPluginList", codex_list),
        ("pluginMarketplace", marketplace),
        ("pluginWindowWired", claude_wired),
        ("codexPluginWindowWired", codex_wired),
        ("pluginMarketplaceWired", marketplace_wired),
        ("pluginOperationsOwned", operations_owned),
        (
            "automaticUpdates",
            no_error
                && a.get("error").is_none()
                && [
                    "defaultAndPersistedPreference",
                    "sameProviderLocalHeld",
                    "manualUpdateRefusesWithoutConsumingDrafts",
                    "releasedQueuePreservesOrder",
                    "stopCancelsHeldRequests",
                    "manualPluginMutationBlocksBothProviders",
                ]
                .iter()
                .all(|k| yes(a, k)),
        ),
    ]
}

/// The active workspace (C#'s `service.Snapshot.Workspaces.First(w => w.Id == ActiveWorkspaceId)`).
fn active_workspace(host: &Host) -> Option<Workspace> {
    let view = host.view();
    view.snapshot.active_workspace_id.as_deref().and_then(|id| view.snapshot.workspace(id)).cloned()
}

/// The window over the active workspace, shown on a task of its own (the probe drives it in the page).
fn show(host: &Arc<Host>, provider: &'static str) -> Result<(), String> {
    let workspace = active_workspace(host).ok_or("no active workspace")?;
    let host = host.clone();
    tauri::async_runtime::spawn(async move {
        if let Err(error) = crate::plugins::show(&host, provider, workspace).await {
            host.log.line(&format!("smoke: plugin window: {error}"));
        }
    });
    Ok(())
}

/// The stand-in list read: each read counted, answered by `answer(reads)`.
fn counted_read(provider: &str, answer: fn(usize) -> ClaudePluginSnapshot) {
    READS.store(0, Ordering::SeqCst);
    crate::plugins::set_smoke_read(
        provider,
        Some(Arc::new(move |_| {
            let reads = READS.fetch_add(1, Ordering::SeqCst) + 1;
            answer(reads)
        })),
    );
}

/// The open window's state as the probe compares it with the page.
fn panel_state(host: &Host) -> Value {
    let Some((browser, id)) = crate::plugins::open_window() else {
        return json!({"open": false, "dialogOpen": host.view().dialog_open, "reads": READS.load(Ordering::SeqCst)});
    };
    let open = crate::native::is_open(host, &id);
    json!({
        "open": open,
        "id": id,
        "provider": browser.provider(),
        "tab": browser.tab(),
        "rows": browser.rows().len(),
        "loading": browser.loading(),
        "filter": browser.marketplace_filter(),
        "search": browser.search(),
        "reads": READS.load(Ordering::SeqCst),
        "scopeValues": browser.scope_options().into_iter().map(|o| o.value).collect::<Vec<_>>(),
        "mutating": browser.is_mutating(),
        "canClose": browser.can_close(),
        "canCancel": browser.can_cancel(),
        "result": browser.result_text().unwrap_or_default(),
        "resultStatus": browser.last_result().map(|r| r.status).unwrap_or_default(),
        "installedSubtitle": browser.installed_rows().first().map(|r| r.subtitle.clone()).unwrap_or_default(),
        "dialogOpen": host.view().dialog_open,
    })
}

/// A Claude agent pane's record (the automatic step's held pane).
fn claude_pane(host: &Host) -> Option<Arc<RunSession>> {
    let view = host.view();
    let active = view.snapshot.active_workspace_id.clone();
    let panes = view.snapshot.sessions();
    panes
        .iter()
        .find(|p| p.kind() == "claude" && p.provider() == "claude" && Some(p.workspace_id()) == active.as_deref())
        .or_else(|| panes.iter().find(|p| p.kind() == "claude" && p.provider() == "claude"))
        .cloned()
}

/// `pane with { Provider = provider }`.
fn as_provider(pane: &RunSession, provider: &str) -> RunSession {
    RunSession { provider: Some(provider.to_string()), ..pane.clone() }
}

/// The pane's composer as panes.rs makes it: (busy, can send, the hold's provider).
fn composer_of(host: &Host, id: &str) -> (bool, bool, Option<String>) {
    let mut view = host.view();
    let _ = crate::panes::ensure(&mut view, Some(id));
    let (busy, can_send) =
        crate::panes::refreshed(host, &mut view, id).map(|(busy, refreshed)| (busy, refreshed.can_send)).unwrap_or((false, true));
    let hold = crate::panes::composer_state(host, &mut view, id).and_then(|state| state.hold);
    (busy, can_send, hold)
}

/// The pane's queued requests' words, in order.
fn queued(host: &Host, id: &str) -> Vec<String> {
    let view = host.view();
    view.panes.get(id).map(|ui| ui.queue.items().iter().map(|item| item.text.clone()).collect()).unwrap_or_default()
}

/// A request queued on the pane as the composer queues one (C# `queuedInputs.Add`).
fn enqueue(host: &Host, id: &str, text: &str) -> bool {
    let mut view = host.view();
    let _ = crate::panes::ensure(&mut view, Some(id));
    let Some(ui) = view.panes.get_mut(id) else { return false };
    let added = ui.queue.add(text, vec![], None, false, false, &host.locale).is_ok();
    ui.dirty = true;
    added
}

/// The pane's queue emptied (the fixture put back).
fn clear_queue(host: &Host, id: &str) {
    if let Some(ui) = host.view().panes.get_mut(id) {
        ui.queue.clear();
        ui.forget_unqueued();
        ui.dirty = true;
    }
}

fn composer_draft(host: &Host, id: &str) -> String {
    host.view().panes.get(id).map(|ui| ui.view.draft.clone()).unwrap_or_default()
}

fn set_draft(host: &Host, id: &str, draft: &str) {
    if let Some(ui) = host.view().panes.get_mut(id) {
        ui.view.draft = draft.to_string();
        ui.dirty = true;
    }
}

async fn wait(mut test: impl FnMut() -> bool, milliseconds: u64) -> bool {
    for _ in 0..milliseconds / 20 {
        if test() {
            return true;
        }
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    test()
}

/// C# `RunAutomaticUpdatesSmoke`, host-side: the plugin switch, an automatic update's hold, a manual update's and a manual plugin
/// change's blocks, each read through the seams with the live state and through the composer's model; everything put back.
async fn automatic(host: &Arc<Host>) -> Result<Value, String> {
    let updates = crate::updates::of(host).ok_or("no updates")?;
    let pane = claude_pane(host).ok_or("no Claude pane")?;
    let id = pane.id().to_string();
    let saved = host.view().snapshot.auto_update_plugins;
    let mut report = json!({});

    // defaultAndPersistedPreference: unset, the plugin switch reads on; switched off in the built box, the state says false.
    host.view().update(|state| state.auto_update_plugins = None)?;
    let built = crate::updates::built_box(host, &crate::updates::box_model(host), &crate::updates::box_actions(host));
    let switch = view::elements(&built).into_iter().find(|el| el.aid.as_deref() == Some("plugin-auto-update")).map(|el| {
        let on = matches!(el.kind, Kind::ToggleButton { on: true, .. });
        (on, el.on.clone())
    });
    let default_on = switch.as_ref().is_some_and(|(on, _)| *on);
    if let Some((_, Some(handler))) = switch {
        handler(Input::Toggle(false));
    }
    let persisted = host.view().snapshot.auto_update_plugins == Some(false);
    report["defaultAndPersistedPreference"] = json!(default_on && persisted);

    // sameProviderLocalHeld: an automatic update of Claude holds the Claude pane only; the composer reads it busy with the hold.
    updates.automatic.set_holding(Some("claude"), false);
    crate::updates::notify(host);
    let (held, codex_held) = {
        let view = host.view();
        (crate::updates::holds(host, &view.snapshot, &pane), crate::updates::holds(host, &view.snapshot, &as_provider(&pane, "codex")))
    };
    let (busy, _, hold) = composer_of(host, &id);
    updates.automatic.set_holding(None, false);
    crate::updates::notify(host);
    let snapshot = host.view().snapshot.clone();
    let released = !crate::updates::holds(host, &snapshot, &pane) && !composer_of(host, &id).0;
    report["sameProviderLocalHeld"] =
        json!(held && !codex_held && busy && hold.as_deref() == Some(mighty_run::strings::provider_label("claude")) && released);

    // From here a queue's drain is a stand-in that records the pane (C#'s `smokeStart`): nothing queued ever starts a CLI.
    if !queued(host, &id).is_empty() || matches!(pane.status(), "running" | "error") {
        return Err("the automatic-update fixture needs an idle Claude pane with an empty queue".into());
    }
    let drained = Arc::new(std::sync::Mutex::new(Vec::<String>::new()));
    let into = drained.clone();
    updates.set_drain_hook(Some(Arc::new(move |pane: &str| into.lock().unwrap_or_else(|p| p.into_inner()).push(pane.to_string()))));
    let drains =
        |drained: &std::sync::Mutex<Vec<String>>| drained.lock().unwrap_or_else(|p| p.into_inner()).iter().filter(|p| **p == id).count();

    // releasedQueuePreservesOrder: two requests queued while Claude's update holds the pane neither start nor admit another
    // idle update; releasing the update drains them, in the order they came (C#: "Releasing an update must schedule queued input
    // draining", "Released local and mobile inputs must retain FIFO order").
    updates.automatic.set_holding(Some("claude"), false);
    let enqueued = enqueue(host, &id, "local held instruction") && enqueue(host, &id, "mobile held instruction");
    crate::updates::notify(host);
    let held_drains = drains(&drained);
    let admits =
        mighty_updates::cli_update::holds::ready_for_automatic_update(&crate::updates::provider_use(host, "claude"), chrono::Utc::now());
    updates.automatic.set_holding(None, false);
    crate::updates::notify(host);
    let released_drains = drains(&drained);
    let order = queued(host, &id);
    report["releasedQueuePreservesOrder"] = json!(
        enqueued && held_drains == 0 && !admits && released_drains == 1 && order == ["local held instruction", "mobile held instruction"]
    );
    clear_queue(host, &id);

    // stopCancelsHeldRequests: a request held by the update, then the page's stop: the queue is dropped and the release starts nothing.
    updates.set_drain_hook(Some({
        let into = drained.clone();
        Arc::new(move |pane: &str| into.lock().unwrap_or_else(|p| p.into_inner()).push(pane.to_string()))
    }));
    updates.automatic.set_holding(Some("claude"), false);
    let queued_again = enqueue(host, &id, "cancel held instruction");
    crate::updates::notify(host);
    let before_stop = drains(&drained);
    let (first, _) = tokio::sync::oneshot::channel();
    host.clone().dispatch(json!({"kind": "call", "id": 9_000_001, "method": "stop", "args": {"paneId": id}}), first).await;
    let dropped = wait(|| queued(host, &id).is_empty(), 5000).await;
    updates.automatic.set_holding(None, false);
    crate::updates::notify(host);
    report["stopCancelsHeldRequests"] =
        json!(queued_again && before_stop == released_drains && dropped && drains(&drained) == released_drains);

    // manualUpdateRefusesWithoutConsumingDrafts: a manual update of Claude (a stand-in that waits) blocks Claude's pane only, the
    // composer refuses a send, the draft is left as it was, and a queued request is held, not started, until the update ends.
    let draft = composer_draft(host, &id);
    set_draft(host, &id, "keep blocked draft");
    let last = updates.shown_results();
    let gate = CancellationToken::new();
    let held_gate = gate.clone();
    updates.set_smoke_updater(Some(Arc::new(move |provider: String, token: CancellationToken| {
        let gate = held_gate.clone();
        Box::pin(async move {
            if provider == "claude" {
                tokio::select! {
                    _ = gate.cancelled() => {}
                    _ = token.cancelled() => {}
                }
            }
            CliUpdateResult::new(&provider, "updated")
        })
    })));
    let started = updates.coordinator.start();
    let updating = wait(|| updates.activity().manual_provider.as_deref() == Some("claude"), 5000).await;
    let (claude_block, codex_block) = {
        let view = host.view();
        (
            crate::updates::manual_block(host, &view.snapshot, &pane),
            crate::updates::manual_block(host, &view.snapshot, &as_provider(&pane, "codex")),
        )
    };
    let (_, can_send, _) = composer_of(host, &id);
    let held_queue = enqueue(host, &id, "keep blocked queued request");
    crate::updates::notify(host);
    let blocked_drains = drains(&drained);
    let kept = composer_draft(host, &id) == "keep blocked draft" && queued(host, &id) == ["keep blocked queued request"];
    clear_queue(host, &id);
    gate.cancel();
    let finished = wait(|| !updates.coordinator.is_updating(), 10_000).await;
    // The run's end (its results the last ones) is told before its task ends: once that is waited for, the restore is final.
    updates.coordinator.cancel().await;
    updates.set_smoke_updater(None);
    updates.automatic.set_cli_results(last);
    set_draft(host, &id, &draft);
    report["manualUpdateRefusesWithoutConsumingDrafts"] = json!(
        started
            && updating
            && claude_block.is_some()
            && codex_block.is_none()
            && !can_send
            && held_queue
            && blocked_drains == released_drains
            && kept
            && finished
    );

    // manualPluginMutationBlocksBothProviders: a plugin install held by a fake reader blocks Claude and Codex, not Antigravity.
    let workspace = active_workspace(host).ok_or("no active workspace")?;
    let browser = Arc::new(ClaudePluginBrowser::new("claude", workspace));
    browser.apply(claude_snapshot());
    browser.set_scope("user");
    let reader: Arc<dyn PluginReader> = Arc::new(FakeMarketplaceReader::new(claude_snapshot(), true));
    let stop = CancellationToken::new();
    let install = {
        let (operations, browser, reader, stop) = (updates.operations.clone(), browser.clone(), reader.clone(), stop.clone());
        tauri::async_runtime::spawn(async move { operations.install(&browser, &reader, "docs@sample", &stop).await })
    };
    let running = wait(|| updates.operations.is_running(), 5000).await;
    let blocks = {
        let view = host.view();
        ["claude", "codex", "gemini"].map(|p| crate::updates::manual_block(host, &view.snapshot, &as_provider(&pane, p)).is_some())
    };
    let (_, plugin_can_send, _) = composer_of(host, &id);
    stop.cancel();
    let _ = install.await;
    reader.shutdown();
    crate::updates::notify(host);
    report["manualPluginMutationBlocksBothProviders"] =
        json!(running && blocks == [true, true, false] && !plugin_can_send && !updates.operations.is_running());

    clear_queue(host, &id);
    updates.set_drain_hook(None);
    host.view().update(|state| state.auto_update_plugins = saved)?;
    Ok(report)
}

/// The step's host side, for its probe (`invoke('smoke_updates', {action, args})`); smoke only.
#[tauri::command]
pub async fn smoke_updates(action: String, args: Value, host: tauri::State<'_, Arc<Host>>) -> Result<Value, String> {
    if host.smoke.is_none() {
        return Err("smoke only".into());
    }
    let host = host.inner().clone();
    match action.as_str() {
        "claudeList" => {
            // C#'s smokePluginRead: the fixture first, then a missing CLI.
            crate::plugins::set_smoke_reader(None);
            counted_read("claude", |reads| {
                if reads <= 1 { claude_snapshot() } else { ClaudePluginSnapshot::of(status::MISSING, plugin_strings::detail_missing_cli()) }
            });
            let view = host.view();
            let panes: Vec<String> = view
                .snapshot
                .sessions()
                .iter()
                .filter(|p| p.kind() == "claude" && p.provider() == "claude")
                .map(|p| p.id().to_string())
                .collect();
            Ok(json!({"panes": panes}))
        }
        "openClaude" => show(&host, "claude").map(|()| Value::Null),
        "codexList" => {
            // C#'s smokeCodexPluginRead: the fixture, an empty registry, a CLI without the JSON plugin commands.
            crate::plugins::set_smoke_reader(None);
            counted_read("codex", |reads| match reads {
                1 => codex_snapshot(),
                2 => ClaudePluginSnapshot {
                    cli_version: Some("codex-cli 0.153.4 (smoke fixture)".into()),
                    ..ClaudePluginSnapshot::of(status::READY, codex_strings::detail_no_marketplaces())
                },
                _ => ClaudePluginSnapshot::of(status::UNSUPPORTED, codex_strings::detail_unsupported()),
            });
            show(&host, "codex").map(|()| Value::Null)
        }
        "marketClaude" => {
            let hold = args.get("hold").and_then(Value::as_bool).unwrap_or(false);
            crate::plugins::set_smoke_read("claude", None);
            READS.store(0, Ordering::SeqCst);
            crate::plugins::set_smoke_reader(Some(Arc::new(move |_| {
                READS.fetch_add(1, Ordering::SeqCst);
                Arc::new(FakeMarketplaceReader::new(claude_snapshot(), hold)) as Arc<dyn PluginReader>
            })));
            show(&host, "claude").map(|()| Value::Null)
        }
        "marketCodex" => {
            crate::plugins::set_smoke_read("codex", None);
            READS.store(0, Ordering::SeqCst);
            crate::plugins::set_smoke_reader(Some(Arc::new(|_| {
                READS.fetch_add(1, Ordering::SeqCst);
                Arc::new(FakeMarketplaceReader::new(codex_snapshot(), false)) as Arc<dyn PluginReader>
            })));
            show(&host, "codex").map(|()| Value::Null)
        }
        "panel" => Ok(panel_state(&host)),
        "state" => {
            let updates = crate::updates::of(&host).ok_or("no updates")?;
            Ok(json!({
                "usesInstalledCli": updates.operations.uses_installed_cli(),
                "operationsRunning": updates.operations.is_running(),
                "operations": Arc::as_ptr(&updates.operations) as usize,
            }))
        }
        "automatic" => automatic(&host).await.or_else(|error| Ok(json!({"error": error}))),
        "restore" => {
            crate::plugins::set_smoke_read("claude", None);
            crate::plugins::set_smoke_read("codex", None);
            crate::plugins::set_smoke_reader(None);
            if let Some((_, id)) = crate::plugins::open_window() {
                crate::native::hide_dialog(&host, &id);
            }
            Ok(Value::Null)
        }
        other => Err(format!("unknown smoke action {other}")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The read-only guard over what the page drew: the Claude marketplace tab shows exactly the Mac's changes.
    #[test]
    fn the_guard_counts_only_the_macs_changes() {
        let drawn: Vec<String> = [
            "tab-installed",
            "tab-marketplace",
            "reload",
            "search",
            "marketplace-filter",
            "scope-picker",
            "refresh-marketplaces",
            "install-fmt@sample",
            "row-x",
        ]
        .iter()
        .map(|part| support::automation_id("claude", part))
        .collect();
        assert_eq!(changes(&drawn, "claude"), (3, true));
        let invented = vec![support::automation_id("claude", "uninstall-fmt")];
        assert_eq!(changes(&invented, "claude"), (1, false));
    }

    /// A report that is missing everything passes nothing.
    #[test]
    fn an_empty_report_fails_every_check() {
        assert!(checks(&json!({"error": "no report"})).iter().all(|(_, passed)| !passed));
    }
}
