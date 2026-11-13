//! The host's log, `<data dir>\tauri-logs\host.log` (or `--log-dir`): one timestamped line per entry, appended.

use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

pub struct LogFile {
    dir: PathBuf,
    file: Mutex<Option<File>>,
}

impl LogFile {
    pub fn new(dir: PathBuf) -> Self {
        LogFile { dir, file: Mutex::new(None) }
    }

    pub fn dir(&self) -> &Path {
        &self.dir
    }

    /// Appends one line; a log that cannot be written is skipped (logging never changes what the app does).
    pub fn line(&self, text: &str) {
        let Ok(mut file) = self.file.lock() else { return };
        if file.is_none() {
            *file = fs::create_dir_all(&self.dir)
                .and_then(|_| OpenOptions::new().create(true).append(true).open(self.dir.join("host.log")))
                .ok();
        }
        if let Some(file) = file.as_mut() {
            let _ = writeln!(
                file,
                "{} {}",
                chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true),
                text.replace(['\r', '\n'], " ")
            );
        }
    }
}
