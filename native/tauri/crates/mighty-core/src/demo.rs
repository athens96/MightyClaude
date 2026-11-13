//! A version 1 state made from the help capture's demo profile (`native/contracts/fixtures/demo-profile.json`), the way the C#
//! capture seeds it (MainWindow.HelpCapture.cs `SeedHelpDemo`) without playing the runs: its workspaces under `projects`, its panes
//! with their titles, models, settings and drafts, each run's request as the pane's transcript and its finish as the pane's status,
//! and each workspace's layout preset and active pane. The delegation workspace is included (C# adds it for the delegation shots).

use std::path::Path;

use serde_json::{Map, Value, json};

use crate::locale::LANGUAGES;

const AT: &str = "2026-10-24T09:00:00Z";

/// C# `HelpLocalized`: an object whose keys are exactly the four languages is one string, the one for `language`.
pub fn localized(value: &Value, language: &str) -> Value {
    match value {
        Value::Object(map) if map.len() == LANGUAGES.len() && LANGUAGES.iter().all(|l| map.contains_key(*l)) => {
            Value::String(map.get(language).and_then(Value::as_str).unwrap_or("").to_string())
        }
        Value::Object(map) => Value::Object(map.iter().map(|(k, v)| (k.clone(), localized(v, language))).collect()),
        Value::Array(list) => Value::Array(list.iter().map(|v| localized(v, language)).collect()),
        other => other.clone(),
    }
}

fn text<'a>(value: &'a Value, key: &str) -> &'a str {
    value.get(key).and_then(Value::as_str).unwrap_or("")
}

/// The pane id a demo role gets (deterministic, a valid `Wire.Identifier`).
pub fn pane_id(role: &str) -> String {
    format!("demo-pane-{role}")
}

/// The demo state for `language` ("ko", "en", "zh" or "ja"), its workspaces in folders under `projects`.
pub fn demo_state(profile: &Value, language: &str, projects: &Path) -> Value {
    let profile = localized(profile, language);
    let empty = vec![];
    let list = |key: &str| profile.get(key).and_then(Value::as_array).unwrap_or(&empty).clone();

    let workspaces: Vec<Value> = list("workspaces")
        .iter()
        .map(|w| {
            json!({
                "id": text(w, "id"),
                "name": text(w, "name"),
                "path": projects.join(text(w, "directory")).to_string_lossy(),
                "createdAt": AT,
            })
        })
        .collect();

    let mut sessions = vec![];
    for pane in list("panes") {
        let role = text(&pane, "role");
        let id = pane_id(role);
        let runs = pane.get("runs").and_then(Value::as_array).cloned().unwrap_or_default();
        let logs: Vec<Value> = runs
            .iter()
            .enumerate()
            .filter(|(_, run)| !text(run, "input").is_empty())
            .map(|(at, run)| json!({"id": format!("{id}-request-{at}"), "kind": "user", "text": text(run, "input"), "timestamp": AT}))
            .collect();
        // A run without a finish was still going when the capture took its picture; a restart reads it as stopped.
        let status = match runs.last() {
            None => "idle",
            Some(run) => run.get("finish").and_then(Value::as_str).unwrap_or("running"),
        };
        let mut session = Map::new();
        for (key, value) in [
            ("id", json!(id)),
            ("workspaceId", json!(text(&pane, "workspace"))),
            ("title", json!(text(&pane, "title"))),
            ("kind", json!(pane.get("kind").and_then(Value::as_str).unwrap_or("claude"))),
            ("status", json!(status)),
            ("model", json!(pane.get("model").and_then(Value::as_str).unwrap_or("default"))),
            ("provider", json!(pane.get("provider").and_then(Value::as_str).unwrap_or("claude"))),
            (
                "settings",
                json!({
                    "effort": pane.get("effort").and_then(Value::as_str).unwrap_or("default"),
                    "permissionMode": pane.get("permissionMode").and_then(Value::as_str).unwrap_or("manual"),
                    "maxTurns": null, "maxBudgetUsd": null
                }),
            ),
            ("logs", Value::Array(logs)),
            ("createdAt", json!(AT)),
            ("draft", json!(text(&pane, "draft"))),
            ("agentViewMode", json!("mighty")),
            ("titleMode", json!("fixed")),
        ] {
            session.insert(key.into(), value);
        }
        sessions.push(Value::Object(session));
    }

    let mut modes = Map::new();
    let mut selections = Map::new();
    for layout in list("layouts") {
        let workspace = text(&layout, "workspace");
        modes.insert(workspace.into(), json!(layout.get("preset").and_then(Value::as_str).unwrap_or("grid")));
        selections.insert(workspace.into(), json!(pane_id(text(&layout, "active"))));
    }
    let active = text(&profile, "activeWorkspace").to_string();
    let mut expanded: Vec<String> = workspaces.iter().map(|w| text(w, "id").to_string()).collect();
    expanded.sort();
    json!({
        "version": 1,
        "workspaces": workspaces,
        "sessions": sessions,
        "activeWorkspaceId": active,
        "activeSessionId": selections.get(&active).cloned().unwrap_or(Value::Null),
        "expandedWorkspaceIds": expanded,
        "layout": "grid",
        "paneLayoutModes": modes,
        "paneLayoutActiveSessionIds": selections,
        "theme": "dark",
        "sidebarWidth": 252,
        "sidebarCollapsed": false,
        "statusLineEnabled": false,
        "languagePreference": language,
        "antigravityMigrated": true,
    })
}
