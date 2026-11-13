//! What the work map checks share (C# `WorkMapVerification`'s fixture, `Git`, `Commit`, `MakeRepository`, `Verification.Temp`, `Self`).
//! Every check reads Korean, as C#'s `Verification.RunAsync` pins it.
#![allow(dead_code)]

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};

use mighty_core::locale::Locale;
use serde_json::Value;

pub fn ko() -> Locale {
    Locale::new("ko")
}

pub fn fixture() -> Value {
    let path = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../contracts/fixtures/work-map.json");
    serde_json::from_str(&std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("{}: {e}", path.display()))).unwrap()
}

pub fn strings(value: &Value) -> Vec<String> {
    value.as_array().map(|a| a.iter().filter_map(Value::as_str).map(str::to_string).collect()).unwrap_or_default()
}

pub fn text(value: &Value, name: &str) -> Option<String> {
    value.get(name).and_then(Value::as_str).map(str::to_string)
}

/// A new empty folder of this check's own.
pub fn temp(name: &str) -> PathBuf {
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
    let path = std::env::temp_dir().join(format!("mighty-workmap-{name}-{}-{nanos}", std::process::id()));
    std::fs::create_dir_all(&path).unwrap();
    path
}

pub fn remove(path: &Path) {
    let _ = std::fs::remove_dir_all(path);
}

/// The real Windows process layer (the checks that run git or the scripted Claude are Windows-only, as the run engine's are).
#[cfg(windows)]
pub fn processes() -> Arc<dyn mighty_platform::ProcessHost> {
    Arc::new(mighty_platform_windows::WindowsProcessHost::new())
}

#[cfg(windows)]
pub fn git(parent: &Path) -> mighty_workmap::Git {
    mighty_workmap::Git::new(processes(), parent.join("git-no-hooks"))
}

/// C# `Git(folder, arguments)`: a fixture git command that must succeed.
#[cfg(windows)]
pub async fn run_git(git: &mighty_workmap::Git, folder: &Path, arguments: &[&str]) {
    let mut all =
        vec!["-c", "user.name=Check", "-c", "user.email=check@example.com", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false"];
    all.extend_from_slice(arguments);
    let result = git.run(&all, folder, 30.0).await;
    assert!(result.as_ref().is_some_and(|r| r.exit_code == 0), "git {}: {:?}", arguments.join(" "), result.map(|r| r.stderr));
}

/// C# `Commit(folder, file, subject, date)`.
#[cfg(windows)]
pub async fn commit(git: &mighty_workmap::Git, folder: &Path, file: &str, subject: &str, date: &str) {
    std::fs::write(folder.join(file), format!("{subject}{}", mighty_core::wire::new_id())).unwrap();
    run_git(git, folder, &["add", "--", file]).await;
    let dated = format!("--date={date}");
    run_git(git, folder, &["commit", "-m", subject, &dated]).await;
}

/// C# `MakeRepository`: v0.1.0 (one commit), v0.1.1 (two commits and a side branch merged in), a tag that is no release, then one commit
/// past it with a VERSION file and a release in flight.
#[cfg(windows)]
pub async fn make_repository(git: &mighty_workmap::Git, parent: &Path) -> PathBuf {
    let folder = parent.join("repo");
    std::fs::create_dir_all(&folder).unwrap();
    run_git(git, &folder, &["init", "-q"]).await;
    commit(git, &folder, "a.txt", "(mac) one", "2026-01-01T00:00:00+00:00").await;
    run_git(git, &folder, &["tag", "v0.1.0"]).await;
    commit(git, &folder, "b.txt", "(windows) two", "2026-01-02T00:00:00+00:00").await;
    commit(git, &folder, "c.txt", "fix(phone): three", "2026-01-03T00:00:00+00:00").await;
    run_git(git, &folder, &["switch", "-q", "-c", "side"]).await;
    commit(git, &folder, "d.txt", "(ci) side", "2026-01-04T00:00:00+00:00").await;
    run_git(git, &folder, &["switch", "-q", "-"]).await;
    run_git(git, &folder, &["merge", "--no-ff", "-q", "-m", "Merge branch 'side'", "side"]).await;
    run_git(git, &folder, &["tag", "v0.1.1"]).await;
    run_git(git, &folder, &["tag", "mighty-style-engine-v3"]).await;
    commit(git, &folder, "a.txt", "(mac, windows) four", "2026-02-01T00:00:00+00:00").await;
    std::fs::write(folder.join("VERSION"), "0.1.2\n").unwrap();
    std::fs::create_dir_all(folder.join("release").join("0.1.2")).unwrap();
    std::fs::write(folder.join("release").join("0.1.2").join("state.json"), r#"{"done":{"check":true,"version":true,"push":true}}"#)
        .unwrap();
    folder
}

/// The scripted Claude (C# `Verification.Self("--workmap-fake", mode)`).
pub fn fake_claude(mode: &str) -> mighty_run::model::CliCommand {
    mighty_run::model::CliCommand::new(env!("CARGO_BIN_EXE_mighty-workmap-fake"), vec![mode.to_string()], Some("1"))
}
