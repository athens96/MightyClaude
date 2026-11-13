//! C# `StateStore.LoadAsync` and `SaveAsync` on real folders (temporary ones): the damaged-file recovery, the rule that an
//! unreadable file stops every save, the legacy copy, the atomic write with its backup, and a store that may not write.

mod common;

use std::fs;
use std::path::Path;

use common::temp_dir;
use mighty_core::locale::Locale;
use mighty_core::store::{self, LoadSource, StateError, Store};

const GOOD: &str = r#"{"version":1,"workspaces":[{"id":"w1","name":"A","path":"C:\\a","createdAt":"2026-10-24T00:00:00Z"}],"sessions":[],"theme":"light"}"#;
const OLDER: &str = r#"{"version":1,"workspaces":[],"sessions":[],"theme":"dark"}"#;

fn listing(dir: &Path) -> Vec<(String, Vec<u8>)> {
    let mut files: Vec<(String, Vec<u8>)> =
        fs::read_dir(dir).unwrap().flatten().map(|e| (e.file_name().to_string_lossy().into_owned(), fs::read(e.path()).unwrap())).collect();
    files.sort();
    files
}

fn en() -> Locale {
    Locale::new("en")
}

#[test]
fn a_zeroed_state_is_moved_aside_and_its_backup_restored() {
    let dir = temp_dir("zeroed");
    fs::write(dir.join(store::STATE_FILE), vec![0u8; GOOD.len()]).unwrap();
    fs::write(dir.join("workspace-state.json.bak"), OLDER).unwrap();
    let mut store = Store::new(dir.clone(), None, true);
    let loaded = store.load(&Locale::new("ko")).unwrap();
    assert_eq!(store.source, Some(LoadSource::Backup));
    assert_eq!(loaded.theme(), "dark");
    let aside = store.aside.clone().unwrap();
    assert!(aside.starts_with("workspace-state.json.unreadable-"), "{aside}");
    assert!(dir.join(&aside).exists() && !dir.join(store::STATE_FILE).exists());
    assert_eq!(store.load_notice.as_deref(), Some(Locale::new("ko").get_with("store.notice.restoredBackup", &[("file", &aside)]).as_str()));
    // The next save writes a state again (the backup file stays the backup of nothing until then).
    store.save(&loaded).unwrap();
    assert!(dir.join(store::STATE_FILE).exists());
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn a_store_that_may_not_write_reports_what_it_would_do_and_changes_nothing() {
    let dir = temp_dir("readonly");
    fs::write(dir.join(store::STATE_FILE), vec![0u8; 64]).unwrap();
    fs::write(dir.join("workspace-state.json.bak"), OLDER).unwrap();
    let before = listing(&dir);
    let mut store = Store::new(dir.clone(), None, false);
    let loaded = store.load(&en()).unwrap();
    assert_eq!(store.source, Some(LoadSource::Backup));
    assert!(store.aside.is_some() && store.load_notice.is_some());
    assert!(matches!(store.save(&loaded), Err(StateError::WritesOff)));
    assert_eq!(listing(&dir), before, "a read-only store changed the folder");
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn a_damaged_state_without_a_readable_backup_starts_empty() {
    for backup in [None, Some(&b"{not json"[..]), Some(&[0u8; 8][..])] {
        let dir = temp_dir("damaged");
        let mut bytes = GOOD.as_bytes().to_vec();
        bytes[20] = 0;
        fs::write(dir.join(store::STATE_FILE), bytes).unwrap();
        if let Some(backup) = backup {
            fs::write(dir.join("workspace-state.json.bak"), backup).unwrap();
        }
        let mut store = Store::new(dir.clone(), None, true);
        let loaded = store.load(&en()).unwrap();
        assert_eq!(store.source, Some(LoadSource::DamagedEmpty));
        assert!(loaded.workspaces().is_empty());
        assert_eq!(loaded.antigravity_migrated, Some(true));
        let aside = store.aside.clone().unwrap();
        assert_eq!(store.load_notice.as_deref(), Some(en().get_with("store.notice.unreadableStartedEmpty", &[("file", &aside)]).as_str()));
        fs::remove_dir_all(dir).unwrap();
    }
}

#[test]
fn an_unreadable_state_stops_every_save() {
    let dir = temp_dir("unreadable");
    fs::write(dir.join(store::STATE_FILE), r#"{"version":2,"workspaces":[],"sessions":[]}"#).unwrap();
    fs::write(dir.join("workspace-state.json.bak"), OLDER).unwrap();
    let before = listing(&dir);
    let mut store = Store::new(dir.clone(), None, true);
    let error = store.load(&en()).unwrap_err();
    assert_eq!(error.kind(), "store.error.invalidFormat");
    assert!(!store.is_loaded());
    assert!(matches!(store.save(&mighty_core::AppSnapshot::default()), Err(StateError::NotLoaded)));
    assert_eq!(listing(&dir), before);
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn a_state_over_eight_megabytes_is_refused() {
    let dir = temp_dir("large");
    let padding = " ".repeat(8 * 1024 * 1024);
    fs::write(dir.join(store::STATE_FILE), format!("{{\"version\":1,\"workspaces\":[],\"sessions\":[]}}{padding}")).unwrap();
    let error = Store::new(dir.clone(), None, true).load(&en()).unwrap_err();
    assert_eq!(error.kind(), "store.error.fileTooLarge");
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn a_new_default_profile_takes_the_legacy_state() {
    let dir = temp_dir("new");
    let legacy = temp_dir("legacy");
    fs::write(legacy.join(store::STATE_FILE), GOOD).unwrap();
    // Read-only: read where it is, nothing copied.
    let mut reader = Store::new(dir.clone(), Some(legacy.clone()), false);
    reader.load(&en()).unwrap();
    assert_eq!(reader.source, Some(LoadSource::Legacy(legacy.join(store::STATE_FILE))));
    assert!(!dir.join(store::STATE_FILE).exists(), "the legacy state was copied");
    // Writing: copied once, as C# copies it.
    let mut writer = Store::new(dir.clone(), Some(legacy.clone()), true);
    writer.load(&en()).unwrap();
    assert_eq!(fs::read(dir.join(store::STATE_FILE)).unwrap(), GOOD.as_bytes());
    fs::remove_dir_all(dir).unwrap();
    fs::remove_dir_all(legacy).unwrap();
}

#[test]
fn a_save_keeps_the_file_it_replaces_as_the_backup_and_leaves_no_temporary_file() {
    let dir = temp_dir("save");
    let root = if cfg!(windows) { "C:\\\\a" } else { "/a" };
    let first = format!(
        r#"{{"version":1,"workspaces":[{{"id":"w1","name":"A","path":"{root}","createdAt":"2026-10-24T00:00:00Z"}}],"sessions":[]}}"#
    );
    fs::write(dir.join(store::STATE_FILE), &first).unwrap();
    let mut store = Store::new(dir.clone(), None, true);
    let mut loaded = store.load(&en()).unwrap();
    loaded.theme = Some("light".into());
    store.save(&loaded).unwrap();
    assert_eq!(fs::read_to_string(dir.join("workspace-state.json.bak")).unwrap(), first);
    let saved = fs::read(dir.join(store::STATE_FILE)).unwrap();
    assert_eq!(saved, store::encode_for_save(&loaded).unwrap().1);
    assert!(String::from_utf8_lossy(&saved).contains(r#""theme":"light""#));
    let names: Vec<String> = listing(&dir).into_iter().map(|(n, _)| n).collect();
    assert_eq!(names, vec!["workspace-state.json", "workspace-state.json.bak"]);
    // A workspace the load did not have, and no one approved, is refused.
    let mut foreign = store.snapshot.clone();
    foreign.workspaces_mut()[0].path = Some(if cfg!(windows) { "C:\\elsewhere".into() } else { "/elsewhere".into() });
    assert!(matches!(store.save(&foreign), Err(StateError::PathNotApproved)));
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn a_refused_replace_is_tried_again() {
    use std::sync::atomic::{AtomicUsize, Ordering};
    static CALLS: AtomicUsize = AtomicUsize::new(0);
    fn flaky(source: &Path, destination: &Path, backup: &Path) -> std::io::Result<()> {
        if CALLS.fetch_add(1, Ordering::SeqCst) < 3 {
            return Err(std::io::Error::new(std::io::ErrorKind::PermissionDenied, "held by a reader"));
        }
        store::portable_replace(source, destination, backup)
    }
    let dir = temp_dir("retry");
    let path = dir.join(store::STATE_FILE);
    fs::write(&path, "old").unwrap();
    store::atomic_write(&path, b"new", Some(&dir.join("workspace-state.json.bak")), flaky).unwrap();
    assert_eq!(CALLS.load(Ordering::SeqCst), 4);
    assert_eq!(fs::read(&path).unwrap(), b"new");
    assert_eq!(fs::read(dir.join("workspace-state.json.bak")).unwrap(), b"old");
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn the_saved_theme_is_read_before_the_load() {
    let dir = temp_dir("theme");
    assert_eq!(store::saved_theme(&dir), "dark");
    fs::write(dir.join(store::STATE_FILE), GOOD).unwrap();
    assert_eq!(store::saved_theme(&dir), "light");
    fs::remove_dir_all(dir).unwrap();
}
