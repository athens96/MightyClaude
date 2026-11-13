//! The frame model for the demo state, read through the page normalize as the host sends it.

mod common;

use std::collections::HashMap;
use std::path::PathBuf;

use common::{read_json, repo};
use mighty_core::locale::Locale;
use mighty_core::shell::{ShellContext, shell_model};
use mighty_core::{demo, normalize, store};

fn demo_snapshot(language: &str) -> mighty_core::AppSnapshot {
    let profile = read_json(&repo().join("native/contracts/fixtures/demo-profile.json"));
    let root = if cfg!(windows) { PathBuf::from("C:\\demo\\Projects") } else { PathBuf::from("/demo/Projects") };
    let state = demo::demo_state(&profile, language, &root);
    normalize::for_page(&store::decode(state).unwrap())
}

#[test]
fn the_demo_frame_lists_every_workspace_and_its_layout() {
    let snapshot = demo_snapshot("en");
    let context = ShellContext { locale: Locale::new("en"), version: "0.4.59", now: chrono::Utc::now(), error: None };
    let mut defaults = HashMap::new();
    let model = shell_model(&snapshot, &context, &mut defaults);
    let value = serde_json::to_value(&model).unwrap();

    assert_eq!(model.workspaces.len(), 3);
    assert_eq!(model.panes.len(), 12);
    assert!(model.workspaces.iter().all(|w| w.expanded && w.items.as_ref().is_some_and(|i| !i.is_empty())));
    let header = model.header.as_ref().unwrap();
    assert_eq!(header.name, "my-app");
    assert_eq!(model.status.panes, "4 panes");
    assert_eq!(model.status.machine, "Running on this PC");
    let layout = model.layout.as_ref().unwrap();
    assert_eq!(layout.mode, "grid");
    assert_eq!(layout.root.kind, "split");
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

    // The same tree again: the made layout is kept, so its ids do not change from one model to the next.
    let again = shell_model(&snapshot, &context, &mut defaults);
    assert_eq!(again.layout, model.layout);
}

#[test]
fn the_frame_speaks_the_chosen_language() {
    let snapshot = demo_snapshot("ko");
    let context = ShellContext { locale: Locale::new("ko"), version: "0.4.59", now: chrono::Utc::now(), error: None };
    let model = shell_model(&snapshot, &context, &mut HashMap::new());
    assert_eq!(model.status.panes, Locale::new("ko").get_with("window.status.paneCount", &[("count", "4")]));
    assert_ne!(model.status.panes, "4 panes");
}
