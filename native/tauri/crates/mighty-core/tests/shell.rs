//! The frame model for the demo state, loaded as C# loads it (`StateStore.LoadAsync`, normalize included).

mod common;

use std::collections::HashMap;
use std::path::PathBuf;

use common::{read_json, repo};
use mighty_core::locale::Locale;
use mighty_core::shell::{Runtime, ShellContext, WorkspaceGitInfo, shell_model};
use mighty_core::{demo, store};

fn demo_snapshot(language: &str) -> mighty_core::AppSnapshot {
    let profile = read_json(&repo().join("native/contracts/fixtures/demo-profile.json"));
    let root = if cfg!(windows) { PathBuf::from("C:\\demo\\Projects") } else { PathBuf::from("/demo/Projects") };
    let state = demo::demo_state(&profile, language, &root);
    store::read_state(&serde_json::to_vec(&state).unwrap(), &Locale::new(language)).unwrap()
}

struct Window {
    attention: HashMap<String, mighty_core::shell::Attention>,
    git: HashMap<String, WorkspaceGitInfo>,
    remote: HashMap<String, mighty_core::shell::RemoteConnection>,
}

impl Window {
    fn new() -> Self {
        Window { attention: HashMap::new(), git: HashMap::new(), remote: HashMap::new() }
    }

    fn context<'a>(&'a self, language: &str, git: Option<&'a WorkspaceGitInfo>, dashboard: bool) -> ShellContext<'a> {
        ShellContext {
            locale: Locale::new(language),
            version: "0.4.59",
            now: chrono::Utc::now(),
            error: None,
            resource: None,
            dialog_open: false,
            shows_dashboard: dashboard,
            attention: &self.attention,
            git,
            dashboard_git: &self.git,
            children: &[],
            runtime: Runtime::Known,
            setup_badge: false,
            setup_needed: false,
            remote: &self.remote,
        }
    }
}

#[test]
fn the_demo_frame_lists_every_workspace_and_its_layout() {
    let snapshot = demo_snapshot("en");
    let window = Window::new();
    let mut defaults = HashMap::new();
    let model = shell_model(&snapshot, &window.context("en", None, false), &mut defaults);
    let value = serde_json::to_value(&model).unwrap();

    assert_eq!(model.workspaces.len(), 3);
    assert_eq!(model.panes.len(), 12);
    assert!(model.workspaces.iter().all(|w| w.expanded && w.items.as_ref().is_some_and(|i| !i.is_empty())));
    let header = model.header.as_ref().unwrap();
    assert_eq!(header.name, "my-app");
    assert!(!header.work_map_enabled, "no git read: the work map says the folder is no repository");
    assert_eq!(model.status.panes, "4 panes");
    assert_eq!(model.status.machine, "Running on this PC");
    let layout = model.layout.as_ref().unwrap();
    assert_eq!(layout.mode, "grid");
    assert_eq!(layout.root.kind(), "split");
    assert_eq!(snapshot.active_session_id.as_deref(), Some("demo-pane-mighty"));
    // The panes whose run had no finish were running: a load reads them as stopped.
    let plan = model.panes.iter().find(|p| p.id == "demo-pane-plan").unwrap();
    assert_eq!((plan.tone, plan.word.as_str()), ("stop", "Stopped"));
    let shell = model.panes.iter().find(|p| p.id == "demo-pane-shell").unwrap();
    assert_eq!(shell.meta, "Shell · Local terminal");
    assert!(shell.agent.is_none());
    let codex = model.panes.iter().find(|p| p.id == "demo-pane-codex").unwrap();
    assert!(codex.beta);
    // Nulls are left out as C# Wire.Json leaves them out.
    assert!(value["panes"][0].get("wait").is_none());
    assert!(value.get("dashboard").is_none());

    // The same tree again: the made layout is kept, so its ids do not change from one model to the next.
    let again = shell_model(&snapshot, &window.context("en", None, false), &mut defaults);
    assert_eq!(again.layout, model.layout);
}

#[test]
fn the_header_shows_git_and_the_dashboard_its_rows() {
    let snapshot = demo_snapshot("en");
    let window = Window::new();
    let git = WorkspaceGitInfo::parse("# branch.head feature\n# branch.ab +1 -0\n").unwrap();
    let model = shell_model(&snapshot, &window.context("en", Some(&git), true), &mut HashMap::new());
    let header = model.header.unwrap();
    assert!(header.work_map_enabled);
    assert_eq!(header.git.as_ref().map(|g| (g.label.as_str(), g.ahead)), Some(("feature", 1)));
    let dashboard = model.dashboard.unwrap();
    assert_eq!(dashboard.workspaces.len(), 3);
    assert_eq!(dashboard.workspaces.iter().map(|w| w.rows.len()).sum::<usize>(), 12);
}

/// LocaleKeyLeak's rule over every visible string of the frame, in each language (the dashboard and git shown too).
#[test]
fn no_frame_string_is_a_bare_locale_key() {
    fn strings<'a>(value: &'a serde_json::Value, out: &mut Vec<&'a str>) {
        match value {
            serde_json::Value::String(s) => out.push(s),
            serde_json::Value::Array(items) => items.iter().for_each(|v| strings(v, out)),
            serde_json::Value::Object(map) => map.values().for_each(|v| strings(v, out)),
            _ => {}
        }
    }
    let keys = mighty_core::locale::keys();
    let git = WorkspaceGitInfo::parse("# branch.head main\n").unwrap();
    for language in mighty_core::locale::LANGUAGES {
        let snapshot = demo_snapshot(language);
        let window = Window::new();
        let model = serde_json::to_value(shell_model(&snapshot, &window.context(language, Some(&git), true), &mut HashMap::new())).unwrap();
        let mut visible = vec![];
        strings(&model, &mut visible);
        let leaks = mighty_core::locale::key_leaks(visible, &keys);
        assert!(leaks.is_empty(), "{language}: {leaks:?}");
    }
}

#[test]
fn the_frame_speaks_the_chosen_language() {
    let snapshot = demo_snapshot("ko");
    let window = Window::new();
    let model = shell_model(&snapshot, &window.context("ko", None, false), &mut HashMap::new());
    assert_eq!(model.status.panes, Locale::new("ko").get_with("window.status.paneCount", &[("count", "4")]));
    assert_ne!(model.status.panes, "4 panes");
}
