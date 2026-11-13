//! The load's choices (C# `StateStore.LoadAsync`), made without writing: a damaged file reads its backup, and nothing in the folder
//! is renamed, created or changed.

mod common;

use std::fs;
use std::path::Path;

use common::temp_dir;
use mighty_core::store::{self, LoadSource};

const GOOD: &str = r#"{"version":1,"workspaces":[{"id":"w1","name":"A","path":"C:\\a","createdAt":"2026-10-24T00:00:00Z"}],"sessions":[],"theme":"light"}"#;
const OLDER: &str = r#"{"version":1,"workspaces":[],"sessions":[],"theme":"dark"}"#;

/// Every file in the folder with its bytes, to show a load left it as it was.
fn listing(dir: &Path) -> Vec<(String, Vec<u8>)> {
    let mut files: Vec<(String, Vec<u8>)> =
        fs::read_dir(dir).unwrap().flatten().map(|e| (e.file_name().to_string_lossy().into_owned(), fs::read(e.path()).unwrap())).collect();
    files.sort();
    files
}

#[test]
fn a_zeroed_state_reads_its_backup_and_moves_nothing() {
    let dir = temp_dir("zeroed");
    fs::write(dir.join(store::STATE_FILE), vec![0u8; GOOD.len()]).unwrap();
    fs::write(dir.join("workspace-state.json.bak"), OLDER).unwrap();
    let before = listing(&dir);
    let report = store::load(&dir, &[]);
    assert_eq!(report.source, LoadSource::Backup);
    assert_eq!(report.snapshot.theme(), "dark");
    assert_eq!(report.notice_key, Some("store.notice.restoredBackup"));
    assert!(report.aside.as_deref().unwrap().starts_with("workspace-state.json.unreadable-"));
    assert_eq!(listing(&dir), before, "the load changed the folder");
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn a_file_with_one_nul_byte_is_damaged_too() {
    let dir = temp_dir("nul");
    let mut bytes = GOOD.as_bytes().to_vec();
    bytes[20] = 0;
    fs::write(dir.join(store::STATE_FILE), bytes).unwrap();
    let report = store::load(&dir, &[]);
    assert_eq!(report.source, LoadSource::DamagedEmpty);
    assert_eq!(report.notice_key, Some("store.notice.unreadableStartedEmpty"));
    assert!(report.snapshot.workspaces.is_empty());
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn a_damaged_state_with_a_damaged_backup_starts_empty() {
    let dir = temp_dir("both");
    fs::write(dir.join(store::STATE_FILE), vec![0u8; 64]).unwrap();
    fs::write(dir.join("workspace-state.json.bak"), vec![0u8; 64]).unwrap();
    let before = listing(&dir);
    let report = store::load(&dir, &[]);
    assert_eq!(report.source, LoadSource::DamagedEmpty);
    assert_eq!(listing(&dir), before);
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn a_good_state_ignores_its_backup() {
    let dir = temp_dir("good");
    fs::write(dir.join(store::STATE_FILE), GOOD).unwrap();
    fs::write(dir.join("workspace-state.json.bak"), OLDER).unwrap();
    let report = store::load(&dir, &[]);
    assert_eq!(report.source, LoadSource::State);
    assert_eq!(report.snapshot.theme(), "light");
    assert_eq!(report.snapshot.workspaces.len(), 1);
    assert!(report.notice_key.is_none() && report.error.is_none());
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn an_unreadable_state_is_reported_not_replaced() {
    let dir = temp_dir("unreadable");
    fs::write(dir.join(store::STATE_FILE), r#"{"version":2,"workspaces":[],"sessions":[]}"#).unwrap();
    fs::write(dir.join("workspace-state.json.bak"), OLDER).unwrap();
    let before = listing(&dir);
    let report = store::load(&dir, &[]);
    // Not damaged: C# stops loading (the backup is only for a power cut's zeros); the skeleton starts empty and says why.
    assert_eq!(report.source, LoadSource::Unreadable);
    assert_eq!(report.error.as_ref().unwrap().locale_key(), "store.error.invalidFormat");
    assert_eq!(listing(&dir), before);
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn a_state_over_eight_megabytes_is_refused() {
    let dir = temp_dir("large");
    let padding = " ".repeat(8 * 1024 * 1024);
    fs::write(dir.join(store::STATE_FILE), format!("{{\"version\":1,\"workspaces\":[],\"sessions\":[]}}{padding}")).unwrap();
    let report = store::load(&dir, &[]);
    assert_eq!(report.source, LoadSource::Unreadable);
    assert_eq!(report.error.as_ref().unwrap().locale_key(), "store.error.fileTooLarge");
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn a_new_profile_reads_the_legacy_state_without_copying_it() {
    let dir = temp_dir("new");
    let legacy = temp_dir("legacy");
    fs::write(legacy.join(store::STATE_FILE), GOOD).unwrap();
    let report = store::load(&dir, std::slice::from_ref(&legacy));
    assert_eq!(report.source, LoadSource::Legacy(legacy.join(store::STATE_FILE)));
    assert_eq!(report.snapshot.workspaces.len(), 1);
    assert!(!dir.join(store::STATE_FILE).exists(), "the legacy state was copied");
    let empty = store::load(&dir, &[]);
    assert_eq!(empty.source, LoadSource::Empty);
    fs::remove_dir_all(dir).unwrap();
    fs::remove_dir_all(legacy).unwrap();
}
