//! C# `WorkspaceCreateVerification.TheSharedRulesHold` (and the Mac's WorkspaceCreateTests): the rules held to
//! native/contracts/fixtures/workspace-create.json.

mod common;

use mighty_core::workspace_create::{check_name, error_kind, looks_like_git_url, progress, repo_name};
use serde_json::Value;

fn fixture() -> Value {
    common::read_json(&common::repo().join("native/contracts/fixtures/workspace-create.json"))
}

fn text(value: &Value) -> Option<&str> {
    value.as_str()
}

/// `RepoNamesFollowTheFixture`.
#[test]
fn repo_names_follow_the_fixture() {
    for row in fixture()["repoNames"].as_array().unwrap() {
        let url = row["url"].as_str().unwrap();
        assert_eq!(repo_name(url).as_deref(), text(&row["name"]), "the folder name {url:?} suggests");
    }
}

/// `GitUrlsFollowTheFixture`.
#[test]
fn git_urls_follow_the_fixture() {
    let urls = fixture()["gitURLs"].clone();
    for url in urls["valid"].as_array().unwrap().iter().map(|u| u.as_str().unwrap()) {
        assert!(looks_like_git_url(url), "{url:?} is a Git address");
    }
    for url in urls["invalid"].as_array().unwrap().iter().map(|u| u.as_str().unwrap()) {
        assert!(!looks_like_git_url(url), "{url:?} is no Git address");
    }
}

/// `FolderNamesFollowTheFixture`.
#[test]
fn folder_names_follow_the_fixture() {
    for row in fixture()["names"].as_array().unwrap() {
        let name = row["name"].as_str().unwrap();
        let shown: String = name.chars().take(40).collect();
        assert_eq!(check_name(name, true), text(&row["windows"]), "the Windows rule for {shown:?}");
        assert_eq!(check_name(name, false), text(&row["mac"]), "the Mac rule for {shown:?}");
    }
}

/// `GitErrorsFollowTheFixture`.
#[test]
fn git_errors_follow_the_fixture() {
    for row in fixture()["errors"].as_array().unwrap() {
        let stderr = row["stderr"].as_str().unwrap();
        assert_eq!(error_kind(stderr), row["kind"].as_str().unwrap(), "the kind of {stderr:?}");
    }
}

/// `CloneProgressFollowsTheFixture`.
#[test]
fn clone_progress_follows_the_fixture() {
    for row in fixture()["progress"].as_array().unwrap() {
        let line = row["line"].as_str().unwrap();
        let found = progress(line);
        let Some(phase) = text(&row["phase"]) else {
            assert!(found.is_none(), "{line:?} gives no progress");
            continue;
        };
        let found = found.unwrap_or_else(|| panic!("{line:?} gives progress"));
        assert_eq!(found.phase, phase, "the phase of {line:?}");
        assert_eq!(i64::from(found.percent), row["percent"].as_i64().unwrap(), "the percent of {line:?}");
        let fraction = row["fraction"].as_f64().unwrap();
        assert!((found.fraction - fraction).abs() < 0.001, "the fraction of {line:?}: expected {fraction} but was {}", found.fraction);
    }
}
