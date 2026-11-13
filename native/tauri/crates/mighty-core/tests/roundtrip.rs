//! Every state-shaped fixture in the repository loads and is written back without losing a field.

mod common;

use std::path::PathBuf;

use common::{comparable, first_difference, read_json, repo};
use mighty_core::{demo, store};
use serde_json::{Value, json};

fn assert_round_trip(name: &str, original: &Value) {
    let snapshot = store::decode(original.clone()).unwrap_or_else(|e| panic!("{name}: does not decode: {e}"));
    let written = serde_json::to_value(&snapshot).unwrap();
    let (before, after) = (comparable(original), comparable(&written));
    if let Some(difference) = first_difference(&before, &after, name) {
        panic!("{name}: the round trip changed {difference}");
    }
    // A second trip changes nothing more.
    let again = serde_json::to_value(store::decode(written.clone()).unwrap()).unwrap();
    assert_eq!(written, again, "{name}: a second round trip changed the value");
}

/// The delegation-record fixtures' profiles (scripts/fixtures/delegation-record/*/profile/workspace-state.json).
fn delegation_states() -> Vec<PathBuf> {
    let root = repo().join("scripts/fixtures/delegation-record");
    let mut found: Vec<PathBuf> = std::fs::read_dir(&root)
        .unwrap_or_else(|e| panic!("{}: {e}", root.display()))
        .filter_map(|entry| entry.ok().map(|e| e.path().join("profile").join(store::STATE_FILE)))
        .filter(|path| path.exists())
        .collect();
    found.sort();
    found
}

#[test]
fn delegation_record_states_round_trip() {
    let states = delegation_states();
    assert!(states.len() >= 9, "expected the nine delegation-record profiles, found {}", states.len());
    for path in states {
        let value = read_json(&path);
        assert_round_trip(&path.display().to_string(), &value);
        // Their paths are "@{root}/…" placeholders, which the load keeps (Normalize, not the load, drops invalid paths).
        let bytes = std::fs::read(&path).unwrap();
        let loaded = store::parse_state(&bytes).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
        assert_eq!(comparable(&serde_json::to_value(&loaded).unwrap()), comparable(&value), "{}", path.display());
    }
}

#[test]
fn a_state_with_fields_this_host_does_not_know_round_trips() {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/unknown-fields.json");
    let value = read_json(&path);
    assert_round_trip("unknown-fields.json", &value);
    let loaded = store::parse_state(&std::fs::read(&path).unwrap()).unwrap();
    // The lenient fields kept their wrong-typed values instead of failing the load.
    let written = serde_json::to_value(&loaded).unwrap();
    assert_eq!(written["expandedWorkspaceIds"], json!("not a list"));
    assert_eq!(written["sessions"][0]["parentSessionId"], json!(7));
    assert_eq!(written["sessions"][0]["settings"]["advisor"], json!(42));
    assert_eq!(written["futureTopLevel"]["deep"]["x"], json!(-7));
    // maxTurns and maxBudgetUsd are always written, null when unset, as C#'s RunSettingsJsonConverter writes them.
    assert_eq!(written["sessions"][1]["settings"]["maxTurns"], Value::Null);
    assert!(written["sessions"][1]["settings"].as_object().unwrap().contains_key("maxBudgetUsd"));
}

#[test]
fn the_demo_profile_seeds_a_state_that_round_trips() {
    let profile = read_json(&repo().join("native/contracts/fixtures/demo-profile.json"));
    for language in ["en", "ko"] {
        let state = demo::demo_state(&profile, language, &PathBuf::from("C:\\demo\\Projects"));
        assert_round_trip(&format!("demo-profile ({language})"), &state);
        let bytes = serde_json::to_vec(&state).unwrap();
        let loaded = store::parse_state(&bytes).unwrap();
        assert_eq!(loaded.workspaces.len(), 3);
        assert_eq!(loaded.sessions.len(), 12);
    }
}

#[test]
fn remote_references_load_as_the_contract_says() {
    // native/contracts/fixtures/remote-v2/state-references.json: which saved workspace shapes a load keeps.
    let cases = read_json(&repo().join("native/contracts/fixtures/remote-v2/state-references.json"));
    let cases = cases["cases"].as_array().unwrap();
    assert!(!cases.is_empty());
    for case in cases {
        let name = case["name"].as_str().unwrap();
        let workspace = &case["workspace"];
        assert_eq!(store::keeps_workspace(workspace), case["kept"].as_bool().unwrap(), "{name}");
        let state = json!({"version": 1, "workspaces": [workspace], "sessions": []});
        let loaded = store::parse_state(&serde_json::to_vec(&state).unwrap()).unwrap();
        assert_eq!(loaded.workspaces.len() == 1, case["kept"].as_bool().unwrap(), "{name}: load");
        // Every case, kept or not, round-trips as raw JSON.
        assert_round_trip(name, &state);
    }
}

#[test]
fn every_state_shaped_contract_fixture_is_covered() {
    // The contract fixtures that hold a version 1 state (a top-level "version": 1 with "workspaces" and "sessions" arrays) must be
    // listed here: a new one fails this test until it is added to the round trip.
    let dir = repo().join("native/contracts/fixtures");
    let mut shaped = vec![];
    let mut stack = vec![dir.clone()];
    while let Some(folder) = stack.pop() {
        for entry in std::fs::read_dir(&folder).unwrap().flatten() {
            let path = entry.path();
            if path.is_dir() {
                stack.push(path);
            } else if path.extension().is_some_and(|e| e == "json") {
                let Ok(value) = serde_json::from_slice::<Value>(&std::fs::read(&path).unwrap()) else { continue };
                if value["version"] == json!(1) && value["workspaces"].is_array() && value["sessions"].is_array() {
                    shaped.push(path.strip_prefix(&dir).unwrap().display().to_string());
                }
            }
        }
    }
    assert!(shaped.is_empty(), "state-shaped contract fixtures not in the round trip: {shaped:?}");
}
