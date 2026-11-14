//! The smoke's setup, components and status line steps (C# MainWindow.SetupSmoke.cs, MainWindow.Smoke.cs:105-170,
//! MainWindow.WebShell.Settings.cs:417-434). Owner: W6-B.
//!
//! The step's contract (smoke.rs): [`start`] begins it and answers whether it runs; its probe in the page reports once through
//! `smoke_probe`, and [`checks`] reads that report. The probe reaches the host's side through the `smoke_setup` command. Nothing of
//! this PC is probed, installed or read: the screen runs over fixed probe results, the toolkit over a temporary `toolkit.json` with a
//! fake executor, the status line over a temporary home and workspace.
//!
//! - The panel (`setupView`, `setup`): every item missing, the screen drawn in the page with its controls, closed by its ✕.
//! - `setupTools` (C# `RunSetupSmoke`): Claude Code out of date, Codex there, the rest missing. Git missing opens the screen at launch
//!   unless never-show was chosen (`firstRunDecision`) and the welcome offers it (`welcomeButton`, the frame's `setupNeeded`); the
//!   screen focused on Antigravity reads each row as the probes say and lists none of the Mac's tools (`rows`); Git and the focused
//!   CLI start ticked, gh is picked, glab is not, Claude offers its update, Tailscale its page, the install button is on (`actions`);
//!   the plan is in install order (`planOrder`); the install's confirmation (D10) lists the first lines and every step and is
//!   cancelled (`confirmation`) with no install started and no window opened (`cancelledWithoutTerminal`); a pane blocked for a
//!   missing Antigravity offers its install, a shell never, an old Claude its update (`blockedPaneInstall`); the screen holds no dialog
//!   and closes by its ✕ (`setupTools`).
//! - `componentsSection` (C# `RunComponentsSectionSmoke`): the five-entry fixture with one plugin, npm and winget entry each and two
//!   of the Mac's; the three approved; Settings › 도구 shows the setup list and this PC's three tools; the plan runs through a fake
//!   executor that only records (a winget install leaves its link, so the probe after it finds it installed); the Mac's entries stay
//!   in the file byte for byte; the box's install returns at once in a smoke (C#'s `RunToolkitInstall`).
//! - `statusLine`: a Claude pane in a temporary workspace whose `.claude\settings.json` has a command, and a temporary home whose own
//!   has another: the card asks for trust while the home's line shows, and Allow shows the workspace's rows.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use mighty_core::{AppSnapshot, Wire};
use mighty_platform::BoxFuture;
use mighty_run::model::ProviderRuntime;
use mighty_tools::setup::catalog::SetupPlatform;
use mighty_tools::setup::probe::{SetupFirstRun, SetupProbeResult, SetupRowState};
use mighty_tools::setup::screen::smoke_fixed_results;
use mighty_tools::toolkit::{CommandOutput, ToolkitExecutor, ToolkitProbeContext, ToolkitRunItem, ToolkitRunner, ToolkitStore};
use serde_json::{Value, json};

use crate::host::Host;

/// The step's probe.
const PROBE: &str = r#"(async () => {
  const until = async (test, ms) => { const end = performance.now() + ms; while (performance.now() < end) { const v = await test(); if (v) return v; await new Promise((r) => setTimeout(r, 50)); } return test(); };
  const invoke = window.__TAURI_INTERNALS__.invoke;
  const host = (action) => invoke('smoke_setup', { action, args: null });
  const report = {};
  const setup = () => document.querySelector('[data-native-view=setup]');
  const dialogs = () => [...document.querySelectorAll('[data-native-view^=dialog-]')];
  const at = (root, aid) => (root ? root.querySelector(`[data-aid="${aid}"]`) : null);
  const ticked = (root, id) => !!at(root, 'setup-check-' + id)?.querySelector('input')?.checked;
  const closeSetup = async () => { setup()?.querySelector('[data-testid=native-close]')?.click(); return !!(await until(async () => !(await host('state')).setupOpen && !setup(), 10000)); };
  try {
    // The panel over fixed probes: drawn with its controls, closed by its ✕.
    await host('setupPanel');
    report.panel = await until(() => { const v = setup(); const n = v ? v.querySelectorAll('[data-native-id]').length : 0; return n >= 2 ? { controls: n, title: v.querySelector('.dialog-title')?.textContent ?? '' } : null; }, 15000);
    report.panelClosed = await closeSetup();
    // setupTools: focused on Antigravity, gh picked.
    report.tools = await host('setupTools');
    const view = await until(() => { const v = setup(); return v && at(v, 'setup-row-agy') && ticked(v, 'gh') ? v : null; }, 15000);
    report.domRows = Object.fromEntries(['claude', 'codex', 'agy', 'git', 'node', 'gh', 'tailscale', 'glab'].map((id) => [id, !!at(view, 'setup-row-' + id)]));
    report.macRows = ['homebrew', 'gstack', 'bun'].filter((id) => !!at(view, 'setup-row-' + id));
    report.ticks = Object.fromEntries(['git', 'agy', 'gh', 'glab'].map((id) => [id, ticked(view, id)]));
    report.updateClaude = !!at(view, 'setup-update-claude');
    report.pageTailscale = !!at(view, 'setup-page-tailscale');
    const install = at(view, 'setup-install')?.querySelector('button');
    report.installEnabled = !!install && !install.disabled;
    install?.click();
    const confirm = await until(() => dialogs().find((d) => at(d, 'setup-confirm-preamble')), 10000);
    report.confirmSteps = confirm ? [...confirm.querySelectorAll('[data-aid^="setup-confirm-step-"]')].map((e) => e.dataset.aid.slice('setup-confirm-step-'.length)) : [];
    report.preamble = at(confirm, 'setup-confirm-preamble')?.textContent ?? '';
    confirm?.querySelector('[data-testid=native-button-close]')?.click();
    report.confirmClosed = !!(await until(() => !dialogs().some((d) => at(d, 'setup-confirm-preamble')), 10000));
    report.afterCancel = await host('state');
    report.blocked = await host('blockedPane');
    report.toolsClosed = await closeSetup();
    // componentsSection: Settings › 도구 over the fixture.
    report.components = await host('components');
    const form = await until(() => { const f = document.querySelector('[data-native-view=settings] [data-testid=settings-form][data-category=tools]'); return f && f.querySelector('[data-aid^="toolkit-entry-smoke"]') && at(f, 'setup-list') && at(f, 'settings-toolkit-results') ? f : null; }, 15000);
    report.visibleIds = form ? [...form.querySelectorAll('[data-aid^="toolkit-entry-"]')].map((e) => e.dataset.aid.slice('toolkit-entry-'.length)) : [];
    report.setupList = !!at(form, 'setup-list');
    report.resultsShown = !!at(form, 'settings-toolkit-results');
    const before = dialogs().length;
    at(form, 'settings-toolkit-install')?.querySelector('button')?.click();
    await new Promise((r) => setTimeout(r, 800));
    report.installReturned = !!form && dialogs().length === before;
    await host('componentsEnd');
    report.componentsClosed = !!(await until(() => !document.querySelector('[data-native-view=settings]'), 10000));
    // statusLine: the workspace's command asks first, the home's runs meanwhile, Allow runs the workspace's.
    const line = await host('statusLine');
    const card = () => [...document.querySelectorAll('[data-card=statusLine]')].find((c) => (c.textContent ?? '').includes('mighty-smoke-'));
    report.trustAsked = !!(await until(() => (card()?.querySelector('.status-trust-command')?.textContent ?? '').includes(line.workspace), 20000));
    report.userRows = !!(await until(() => [...(card()?.querySelectorAll('.status-row') ?? [])].some((r) => r.textContent.includes(line.user)), 15000));
    card()?.querySelector('.status-trust-answers button')?.click();
    report.pageTrust = !!(await until(async () => (await host('statusLineState')).untrusted === null, 3000));
    if (!report.pageTrust) await host('statusLineTrust');
    report.workspaceRows = !!(await until(() => { const c = card(); return c && !c.querySelector('.status-trust') && [...c.querySelectorAll('.status-row')].some((r) => r.textContent.includes(line.workspace)); }, 15000));
    report.lineState = await host('statusLineState');
  } catch (error) {
    report.error = String(error);
  }
  try { await host('reset'); } catch (error) { report.resetError = String(error); }
  await invoke('smoke_probe', { probe: report });
})();"#;

/// The status line step's workspace and pane.
const WORKSPACE: &str = "smoke-status-line-workspace";
const PANE: &str = "smoke-status-line-pane";
/// What the two commands print.
const USER_LINE: &str = "mighty-smoke-user-line";
const WORKSPACE_LINE: &str = "mighty-smoke-workspace-line";

/// The components fixture (C# `ComponentsSmokeFixture`): one plugin, npm and winget entry each (this PC's) and a brew and a
/// repoScript entry (the Mac's).
const COMPONENTS_FIXTURE: &str = r#"{
  "version": 1,
  "entries": [
    { "id": "smoke-plugin", "displayName": "Smoke plugin", "install": { "kind": "plugin", "source": "athens96/smoke-plugin", "pluginID": "smoke-plugin@smoke-market" } },
    { "id": "smoke-npm", "displayName": "Smoke npm", "install": { "kind": "package", "manager": "npm", "name": "smoke-npm" } },
    { "id": "smoke-winget", "displayName": "Smoke winget", "install": { "kind": "package", "manager": "winget", "name": "Smoke.Tool", "executable": "smoke-tool.exe" } },
    { "id": "smoke-brew", "displayName": "Smoke brew", "install": { "kind": "package", "manager": "brew", "name": "smoke-brew" }, "approval": { "contentHash": "0000000000000000000000000000000000000000000000000000000000000000" } },
    { "id": "smoke-repo-script", "displayName": "Smoke script", "install": { "kind": "repoScript", "url": "https://github.com/athens96/smoke-script.git", "ref": "v1", "scriptPath": "install.sh" } }
  ]
}"#;
const COMPONENTS_VISIBLE: [&str; 3] = ["smoke-plugin", "smoke-npm", "smoke-winget"];
const COMPONENTS_OTHER_OS: [&str; 2] = ["smoke-brew", "smoke-repo-script"];

/// What the step put in place, taken away by `reset`.
#[derive(Default)]
struct Fixtures {
    components: Option<PathBuf>,
    /// The status line's folder, and what the state had before: the active workspace and pane, whether the status line was on.
    status_line: Option<(PathBuf, Option<String>, Option<String>, bool)>,
}

static FIXTURES: Mutex<Fixtures> = Mutex::new(Fixtures { components: None, status_line: None });

fn fixtures() -> MutexGuard<'static, Fixtures> {
    FIXTURES.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// A temporary folder of the step's, named in full.
fn temp_folder(kind: &str) -> PathBuf {
    let stamp = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or_default();
    std::env::temp_dir().join(format!("mighty-tauri-smoke-{kind}-{}-{stamp}", std::process::id()))
}

/// Starts the step: its probe.
pub fn start(host: &Arc<Host>) -> bool {
    let Some(smoke) = &host.smoke else { return false };
    host.log.line("smoke: setup, components and status line steps");
    smoke.eval(host, PROBE);
    true
}

/// The step's checks.
pub fn checks(report: &Value) -> Vec<(&'static str, bool)> {
    let yes = |value: Option<&Value>| value == Some(&Value::Bool(true));
    let at = |key: &str| report.get(key);
    let tool = |key: &str| report.get("tools").and_then(|t| t.get(key));
    let words = |value: Option<&Value>| -> Vec<String> {
        value.and_then(Value::as_array).map(|list| list.iter().filter_map(Value::as_str).map(str::to_string).collect()).unwrap_or_default()
    };
    let order = vec!["agy".to_string(), "git".to_string(), "gh".to_string()];
    let panel = at("panel").and_then(|p| p.get("controls")).and_then(Value::as_u64).unwrap_or(0) >= 2;
    let dom_rows = at("domRows").and_then(Value::as_object).is_some_and(|rows| rows.len() == 8 && rows.values().all(|v| v == &json!(true)));
    let rows = yes(tool("rows")) && dom_rows && words(at("macRows")).is_empty();
    let ticks = at("ticks") == Some(&json!({"git": true, "agy": true, "gh": true, "glab": false}));
    let actions = ticks && yes(at("updateClaude")) && yes(at("pageTailscale")) && yes(at("installEnabled"));
    let plan = words(tool("planOrder")) == order;
    let preamble = at("preamble").and_then(Value::as_str).unwrap_or("");
    let confirmation = words(at("confirmSteps")) == order
        && words(tool("confirmationSteps")) == order
        && yes(tool("noElevation"))
        && preamble.contains("Set-Location -LiteralPath $HOME")
        && yes(at("confirmClosed"));
    let after = at("afterCancel");
    let cancelled = after.and_then(|a| a.get("run")) == Some(&json!(false))
        && after.and_then(|a| a.get("terminalsOpened")).is_some_and(|n| Some(n) == tool("terminalsOpened"));
    let blocked = at("blocked").is_some_and(|b| {
        b.get("agy") == Some(&json!("agy")) && b.get("shell") == Some(&Value::Null) && b.get("claude") == Some(&Value::Null)
    });
    let first_run = yes(tool("firstRunDecision"));
    let welcome = yes(tool("welcomeButton"));
    let tools = first_run
        && welcome
        && rows
        && actions
        && plan
        && confirmation
        && cancelled
        && blocked
        && yes(tool("noDialog"))
        && yes(at("toolsClosed"));
    let components = at("components");
    let visible = words(at("visibleIds"));
    let components_ok = yes(components.and_then(|c| c.get("passed")))
        && COMPONENTS_VISIBLE.iter().all(|id| visible.iter().any(|v| v == id))
        && COMPONENTS_OTHER_OS.iter().all(|id| !visible.iter().any(|v| v == id))
        && yes(at("setupList"))
        && yes(at("resultsShown"))
        && yes(at("installReturned"))
        && yes(at("componentsClosed"));
    let status_line = yes(at("trustAsked")) && yes(at("userRows")) && yes(at("workspaceRows"));
    vec![
        ("setupView", report.get("error").is_none() && panel),
        ("setup", panel && yes(at("panelClosed"))),
        ("setupToolsFirstRunDecision", first_run),
        ("setupToolsWelcomeButton", welcome),
        ("setupToolsRows", rows),
        ("setupToolsActions", actions),
        ("setupToolsPlanOrder", plan),
        ("setupToolsConfirmation", confirmation),
        ("setupToolsCancelledWithoutTerminal", cancelled),
        ("setupToolsBlockedPaneInstall", blocked),
        ("setupTools", tools),
        ("componentsSection", components_ok),
        ("statusLine", status_line),
    ]
}

/// The screen over `results` alone: what it read and picked forgotten (C#: `setupFixedResults = …; setupResults.Clear();
/// setupSelection.Clear(); setupDidPreselect = false`).
fn fix(host: &Host, results: Option<HashMap<String, SetupProbeResult>>) {
    let state = crate::setup::of(host);
    let mut screen = lock(&state.screen);
    screen.load_catalog();
    screen.fixed_results = results;
    screen.results.clear();
    screen.selection.clear();
    screen.did_preselect = false;
    screen.focus = None;
}

/// Every Windows item missing (C# RunWebSettingsSmoke's setup branch).
fn all_missing(host: &Host) -> Result<HashMap<String, SetupProbeResult>, String> {
    let catalog = lock(&crate::setup::of(host).screen).load_catalog().ok_or("the app carries no setup catalog")?;
    Ok(catalog
        .items
        .iter()
        .filter(|item| item.entry(SetupPlatform::Windows).is_some())
        .map(|item| (item.id.clone(), SetupProbeResult::missing()))
        .collect())
}

/// Waits (at most `limit`) until `test` holds.
async fn until(limit: Duration, test: impl Fn() -> bool) -> bool {
    let end = Instant::now() + limit;
    while Instant::now() < end {
        if test() {
            return true;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    test()
}

/// The screen's state the probe reads.
fn state(host: &Host) -> Value {
    let setup_open = crate::native::is_open(host, mighty_tools::view::SETUP_SURFACE);
    let settings_open = crate::native::is_open(host, crate::settings::SURFACE);
    let dialog_open = host.view().dialog_open;
    let state = crate::setup::of(host);
    let screen = lock(&state.screen);
    json!({
        "setupOpen": setup_open,
        "settingsOpen": settings_open,
        "dialogOpen": dialog_open,
        "terminalsOpened": screen.terminals_opened,
        "run": screen.run.is_some(),
        "probing": screen.probing,
        "message": screen.message,
    })
}

/// C# `RunSetupSmoke` up to the page's part: the fixed results read, the launch's decision and the welcome's button, the screen opened
/// on Antigravity with its first picks, gh picked; the rows, the plan and the confirmation as the screen has them.
async fn setup_tools(host: &Arc<Host>) -> Result<Value, String> {
    let state = crate::setup::of(host);
    let catalog = lock(&state.screen).load_catalog().ok_or("the app carries no setup catalog")?;
    fix(host, Some(smoke_fixed_results(&catalog)));
    crate::setup::refresh(host, false, None).await;
    let first_run = {
        let screen = lock(&state.screen);
        SetupFirstRun::should_present(&catalog, &screen.results, false) && !SetupFirstRun::should_present(&catalog, &screen.results, true)
    };
    let welcome = {
        let view = host.view();
        crate::setup::shell_flags(host, &view.snapshot).needed
    };
    crate::setup::open(host, Some("agy"));
    let ready = until(Duration::from_secs(10), || {
        let screen = lock(&state.screen);
        screen.did_preselect && !screen.probing
    })
    .await;
    lock(&state.screen).toggle("gh");
    crate::setup::redraw(host);
    let expected = [
        ("claude", SetupRowState::Outdated),
        ("codex", SetupRowState::Installed),
        ("agy", SetupRowState::Missing),
        ("git", SetupRowState::Missing),
        ("node", SetupRowState::Missing),
        ("gh", SetupRowState::Missing),
        ("tailscale", SetupRowState::Missing),
        ("glab", SetupRowState::Missing),
    ];
    let environment = host.runs().map(|runs| runs.catalog.environment().current().values).unwrap_or_default();
    let no_dialog = !host.view().dialog_open;
    let screen = lock(&state.screen);
    let found: serde_json::Map<String, Value> =
        expected.iter().map(|(id, _)| (id.to_string(), json!(catalog.item(id).map(|item| screen.state_of(item).name())))).collect();
    let rows = expected.iter().all(|(id, state)| catalog.item(id).is_some_and(|item| screen.state_of(item) == *state))
        && screen.items().iter().all(|item| !["homebrew", "gstack", "bun"].contains(&item.id.as_str()));
    let confirmation = screen.confirmation(&environment);
    let steps: Vec<String> = confirmation.iter().flat_map(|c| c.steps.iter().map(|s| s.item_id.clone())).collect();
    let no_elevation =
        confirmation.as_ref().is_some_and(|c| c.steps.iter().all(|s| !s.body.to_lowercase().contains("runas") && !s.body.contains("sudo")));
    Ok(json!({
        "ready": ready,
        "firstRunDecision": first_run,
        "welcomeButton": welcome,
        "noDialog": no_dialog,
        "rows": rows,
        "states": found,
        "planOrder": screen.plan_ids(),
        "confirmationSteps": steps,
        "noElevation": no_elevation,
        "terminalsOpened": screen.terminals_opened,
    }))
}

/// C# `blockedPaneInstall`: over the runtime as read with Antigravity missing, an agent pane offers its install and a shell never;
/// with an old Claude, Claude's pane offers no install (it gets the update).
async fn blocked_pane(host: &Arc<Host>) -> Value {
    until(Duration::from_secs(15), || !crate::setup::runtimes(host).is_empty()).await;
    let held = crate::setup::runtimes(host);
    let with = |id: &str, change: fn(&mut ProviderRuntime)| -> Vec<ProviderRuntime> {
        held.iter()
            .cloned()
            .map(|mut provider| {
                if provider.id == id {
                    change(&mut provider);
                }
                provider
            })
            .collect()
    };
    let no_agy = with("gemini", |p| {
        p.available = false;
        p.version = None;
    });
    let old_claude = with("claude", |p| {
        p.available = false;
        p.version = Some("2.0.0".into());
    });
    json!({
        "runtime": !held.is_empty(),
        "agy": crate::setup::item_for(host, "claude", "gemini", false, &no_agy),
        "shell": crate::setup::item_for(host, "shell", "gemini", false, &no_agy),
        "claude": crate::setup::item_for(host, "claude", "claude", false, &old_claude),
    })
}

/// C#'s `SmokeToolkitExecutor`: records each command and starts no process; a winget install leaves `smoke-tool.exe` in the
/// temporary local folder's WinGet links, so the probe after it judges the tool installed from the file alone.
struct SmokeExecutor {
    local_app_data: PathBuf,
    commands: Mutex<Vec<Vec<String>>>,
}

impl ToolkitExecutor for SmokeExecutor {
    fn run<'a>(&'a self, argv: &'a [String]) -> BoxFuture<'a, CommandOutput> {
        Box::pin(async move {
            lock(&self.commands).push(argv.to_vec());
            if argv.len() > 4 && argv[0] == "winget" && argv[1] == "install" {
                let links = self.local_app_data.join("Microsoft").join("WinGet").join("Links");
                if std::fs::create_dir_all(&links).is_ok() {
                    let _ = std::fs::write(links.join("smoke-tool.exe"), "");
                }
            }
            CommandOutput::success()
        })
    }
}

/// The entries of a toolkit.json's text by id, as JSON values (C#'s `JsonNode.DeepEquals`).
fn entries_of(text: &str) -> HashMap<String, Value> {
    let root: Value = serde_json::from_str(text.trim_start_matches('\u{feff}')).unwrap_or(Value::Null);
    root.get("entries")
        .and_then(Value::as_array)
        .map(|list| list.iter().filter_map(|e| Some((e.get("id")?.as_str()?.to_string(), e.clone()))).collect())
        .unwrap_or_default()
}

/// C# `RunComponentsSectionSmoke` over `folder`: the fixture written, this PC's entries approved (a save the Mac's entries must
/// survive), the plan run through the fake executor; what it shows and what it ran.
async fn components_fixture(folder: &Path) -> Result<(Arc<ToolkitStore>, Vec<ToolkitRunItem>, Value), String> {
    std::fs::create_dir_all(folder).map_err(|e| e.to_string())?;
    let file = folder.join(mighty_tools::toolkit::store::FILE_NAME);
    std::fs::write(&file, COMPONENTS_FIXTURE).map_err(|e| e.to_string())?;
    let store = Arc::new(ToolkitStore::new(folder));
    for id in COMPONENTS_VISIBLE {
        store.approve(id, None)?;
    }
    let (entries, error) = store.list();
    let listed: Vec<String> = entries.iter().map(|e| e.id.clone()).collect();
    let probe = ToolkitProbeContext { home_directory: folder.join("home"), path_directories: vec![], local_app_data: folder.join("local") };
    let executor = SmokeExecutor { local_app_data: probe.local_app_data.clone(), commands: Mutex::new(vec![]) };
    let runner = ToolkitRunner::new(&store, probe);
    let plan = runner.plan();
    let runs = runner.run(&plan, &executor).await;
    let commands = lock(&executor.commands).clone();
    let run_ids: Vec<String> = runs.iter().map(|r| r.entry_id.clone()).collect();
    let (before, after) = (entries_of(COMPONENTS_FIXTURE), entries_of(&std::fs::read_to_string(&file).map_err(|e| e.to_string())?));
    let kept = COMPONENTS_OTHER_OS.iter().all(|id| before.contains_key(*id) && before.get(*id) == after.get(*id));
    let this_pc =
        !commands.is_empty() && commands.iter().all(|argv| matches!(argv.first().map(String::as_str), Some("claude" | "npm" | "winget")));
    let winget = runs.iter().any(|r| r.entry_id == "smoke-winget" && r.verdict == mighty_tools::toolkit::Verdict::Installed);
    let listed_ok = error.is_none()
        && COMPONENTS_VISIBLE.iter().all(|id| listed.iter().any(|l| l == id))
        && COMPONENTS_OTHER_OS.iter().all(|id| !listed.iter().any(|l| l == id));
    let planned = COMPONENTS_VISIBLE.iter().all(|id| run_ids.iter().any(|r| r == id))
        && COMPONENTS_OTHER_OS.iter().all(|id| !run_ids.iter().any(|r| r == id));
    let report = json!({
        "passed": listed_ok && planned && this_pc && winget && kept,
        "listed": listed,
        "runs": runs.iter().map(|r| json!({
            "id": r.entry_id,
            "verdict": r.verdict.name(),
            "steps": r.steps.iter().map(|s| format!("{} => {}", s.argv.join(" "), s.outcome.name())).collect::<Vec<_>>(),
        })).collect::<Vec<_>>(),
        "commandsThisPc": this_pc,
        "wingetInstalled": winget,
        "otherOsKept": kept,
    });
    Ok((store, runs, report))
}

/// The components step: the fixture's store in place of the app's, its run's results in the box, Settings opened on 도구.
async fn components(host: &Arc<Host>) -> Result<Value, String> {
    let folder = temp_folder("components");
    fixtures().components = Some(folder.clone());
    let (store, runs, report) = components_fixture(&folder).await?;
    let toolkit = crate::settings::tools::toolkit_of(host);
    toolkit.replace_store(Some(store));
    crate::settings::tools::read(host);
    lock(&toolkit.model).results = Some(runs);
    crate::settings::open(host, Some("tools"));
    Ok(report)
}

/// The status line step: a temporary home and workspace, each with a command; a Claude pane there, made active, the status line on.
fn status_line(host: &Arc<Host>) -> Result<Value, String> {
    let folder = temp_folder("status-line");
    let (home, workspace) = (folder.join("home"), folder.join("workspace"));
    for (root, line) in [(&home, USER_LINE), (&workspace, WORKSPACE_LINE)] {
        let settings = root.join(".claude");
        std::fs::create_dir_all(&settings).map_err(|e| e.to_string())?;
        let text = json!({"statusLine": {"type": "command", "command": format!("echo {line}")}}).to_string();
        std::fs::write(settings.join("settings.json"), text).map_err(|e| e.to_string())?;
    }
    crate::status_line::smoke_home(host, Some(&home));
    {
        let mut view = host.view();
        let saved = (view.snapshot.active_workspace_id.clone(), view.snapshot.active_session_id.clone(), view.snapshot.status_line_enabled);
        fixtures().status_line = Some((folder.clone(), saved.0, saved.1, saved.2));
        let mut state: Value = serde_json::from_str(&view.snapshot.to_json()).map_err(|e| e.to_string())?;
        let now = chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true);
        let entry = json!({"id": WORKSPACE, "name": "Smoke status line", "path": workspace, "createdAt": now});
        let pane = json!({
            "id": PANE, "workspaceId": WORKSPACE, "title": "Smoke status line", "kind": "claude", "status": "idle", "model": "default",
            "provider": "claude", "settings": {"effort": "default", "permissionMode": "manual", "maxTurns": null, "maxBudgetUsd": null},
            "logs": [], "draft": "", "createdAt": now, "agentViewMode": "mighty", "graphViewMode": "diagram",
        });
        for (name, item) in [("workspaces", entry), ("sessions", pane)] {
            state.get_mut(name).and_then(Value::as_array_mut).ok_or_else(|| format!("the state has no {name}"))?.push(item);
        }
        let mut expanded: Vec<Value> = state.get("expandedWorkspaceIds").and_then(Value::as_array).cloned().unwrap_or_default();
        expanded.push(json!(WORKSPACE));
        state["expandedWorkspaceIds"] = json!(expanded);
        state["activeWorkspaceId"] = json!(WORKSPACE);
        state["activeSessionId"] = json!(PANE);
        state["statusLineEnabled"] = json!(true);
        view.replace(AppSnapshot::read(&state).map_err(|e| e.to_string())?);
        crate::panes::ensure(&mut view, Some(PANE))?;
    }
    host.flush();
    Ok(json!({"pane": PANE, "user": USER_LINE, "workspace": WORKSPACE_LINE}))
}

/// The status line step's pane and workspace taken away, the active pane and the switch as they were, its folder removed.
fn status_line_end(host: &Arc<Host>) -> Result<(), String> {
    let Some((folder, workspace, session, enabled)) = fixtures().status_line.take() else { return Ok(()) };
    crate::status_line::smoke_home(host, None);
    {
        let mut view = host.view();
        let mut state: Value = serde_json::from_str(&view.snapshot.to_json()).map_err(|e| e.to_string())?;
        for (name, id) in [("workspaces", WORKSPACE), ("sessions", PANE)] {
            if let Some(list) = state.get_mut(name).and_then(Value::as_array_mut) {
                list.retain(|item| item.get("id").and_then(Value::as_str) != Some(id));
            }
        }
        if let Some(list) = state.get_mut("expandedWorkspaceIds").and_then(Value::as_array_mut) {
            list.retain(|id| id.as_str() != Some(WORKSPACE));
        }
        state["activeWorkspaceId"] = json!(workspace);
        state["activeSessionId"] = json!(session);
        state["statusLineEnabled"] = json!(enabled);
        if let Some(map) = state.get_mut("trustedStatusLines").and_then(Value::as_object_mut) {
            map.remove(WORKSPACE);
        }
        view.replace(AppSnapshot::read(&state).map_err(|e| e.to_string())?);
    }
    host.flush();
    let _ = std::fs::remove_dir_all(folder);
    Ok(())
}

/// Everything the step put in place taken away (C#'s `finally`s).
fn reset(host: &Arc<Host>) -> Result<(), String> {
    fix(host, None);
    crate::native::close(host, mighty_tools::view::SETUP_SURFACE);
    if let Some(folder) = fixtures().components.take() {
        crate::settings::tools::toolkit_of(host).replace_store(None);
        crate::settings::close(host);
        let _ = std::fs::remove_dir_all(folder);
    }
    status_line_end(host)
}

/// The step's host side, for its probe; smoke only.
#[tauri::command]
pub async fn smoke_setup(action: String, args: Value, host: tauri::State<'_, Arc<Host>>) -> Result<Value, String> {
    let _ = args;
    if host.smoke.is_none() {
        return Err("smoke only".into());
    }
    let host = host.inner().clone();
    match action.as_str() {
        "state" => Ok(state(&host)),
        "setupPanel" => {
            fix(&host, Some(all_missing(&host)?));
            crate::setup::open(&host, None);
            Ok(Value::Null)
        }
        "setupTools" => setup_tools(&host).await,
        "blockedPane" => Ok(blocked_pane(&host).await),
        "components" => components(&host).await,
        "componentsEnd" => {
            if let Some(folder) = fixtures().components.take() {
                crate::settings::tools::toolkit_of(&host).replace_store(None);
                let _ = std::fs::remove_dir_all(folder);
            }
            crate::settings::close(&host);
            Ok(Value::Null)
        }
        "statusLine" => status_line(&host),
        "statusLineState" => Ok(crate::status_line::smoke_state(&host, PANE)),
        "statusLineTrust" => crate::status_line::trust_answer(&host, PANE, true).map(|_| Value::Null),
        "reset" => reset(&host).map(|_| Value::Null),
        other => Err(format!("unknown smoke action {other}")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// C#'s components smoke over a temporary folder: this PC's three entries listed and run through the fake executor (the winget
    /// one installed by its link), the Mac's two neither listed nor run and kept in the file as they were.
    #[test]
    fn the_components_fixture_runs_only_this_pcs_entries_and_keeps_the_macs() {
        let folder = std::env::temp_dir().join(format!("mighty-tauri-smoke-setup-test-components-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&folder);
        let runtime = tokio::runtime::Builder::new_current_thread().enable_all().build().expect("a runtime");
        let (_, runs, report) = runtime.block_on(components_fixture(&folder)).expect("the fixture runs");
        let _ = std::fs::remove_dir_all(&folder);
        assert_eq!(report["passed"], json!(true), "{report}");
        assert_eq!(report["otherOsKept"], json!(true));
        assert!(runs.iter().all(|r| !COMPONENTS_OTHER_OS.contains(&r.entry_id.as_str())));
    }

    /// The checks read the report the probe gives: a full report passes every check, a missing one fails them.
    #[test]
    fn the_checks_follow_the_probes_report() {
        let report = json!({
            "panel": {"controls": 12, "title": "필요한 도구"}, "panelClosed": true,
            "tools": {"firstRunDecision": true, "welcomeButton": true, "noDialog": true, "rows": true, "planOrder": ["agy", "git", "gh"],
                      "confirmationSteps": ["agy", "git", "gh"], "noElevation": true, "terminalsOpened": 0},
            "domRows": {"claude": true, "codex": true, "agy": true, "git": true, "node": true, "gh": true, "tailscale": true, "glab": true},
            "macRows": [], "ticks": {"git": true, "agy": true, "gh": true, "glab": false},
            "updateClaude": true, "pageTailscale": true, "installEnabled": true,
            "confirmSteps": ["agy", "git", "gh"], "preamble": "실행 전에 Set-Location -LiteralPath $HOME", "confirmClosed": true,
            "afterCancel": {"run": false, "terminalsOpened": 0}, "blocked": {"agy": "agy", "shell": null, "claude": null}, "toolsClosed": true,
            "components": {"passed": true}, "visibleIds": ["mighty-toolkit", "smoke-plugin", "smoke-npm", "smoke-winget"],
            "setupList": true, "resultsShown": true, "installReturned": true, "componentsClosed": true,
            "trustAsked": true, "userRows": true, "workspaceRows": true,
        });
        let all = checks(&report);
        assert!(all.iter().all(|(_, passed)| *passed), "{all:?}");
        assert_eq!(all.len(), 13);
        let opened = json!({"afterCancel": {"run": true, "terminalsOpened": 1}});
        let mut broken = report.clone();
        broken["afterCancel"] = opened["afterCancel"].clone();
        let failed: Vec<&str> = checks(&broken).into_iter().filter(|(_, passed)| !passed).map(|(name, _)| name).collect();
        assert_eq!(failed, ["setupToolsCancelledWithoutTerminal", "setupTools"]);
        assert!(checks(&json!({"error": "no main window"})).iter().all(|(_, passed)| !passed));
    }
}
