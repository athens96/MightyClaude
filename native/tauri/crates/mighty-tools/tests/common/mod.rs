//! What the W6-B checks share (C# `Verification.Temp`, `Until`, the fixtures found under native/contracts). Every check pins Korean
//! first, as C#'s `Verification.RunAsync` does.
#![allow(dead_code)]

use std::path::{Path, PathBuf};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

/// Korean copy, as every C# check reads it.
pub fn korean() {
    mighty_run::strings::set_language("ko");
}

/// The folder as its long name: a temporary folder can sit under an 8.3 name (`C:\Users\RUNNER~1\…` on CI), so every check names
/// its folder in full.
pub fn long_path(path: PathBuf) -> PathBuf {
    let Ok(real) = std::fs::canonicalize(&path) else { return path };
    let text = real.to_string_lossy();
    match text.strip_prefix(r"\\?\") {
        Some(rest) if rest.as_bytes().get(1) == Some(&b':') => PathBuf::from(rest),
        Some(_) => path,
        None => real,
    }
}

/// A new empty folder of this check's own, named in full.
pub fn temp(name: &str) -> PathBuf {
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
    let path = std::env::temp_dir().join(format!("mighty-tools-test-{name}-{}-{nanos}", std::process::id()));
    std::fs::create_dir_all(&path).unwrap();
    long_path(path)
}

pub fn remove(path: &Path) {
    let _ = std::fs::remove_dir_all(path);
}

/// Waits until `condition` holds, failing after `milliseconds` (C# `Verification.Until`).
pub async fn until(mut condition: impl FnMut() -> bool, milliseconds: u64, what: &str) {
    let end = Instant::now() + Duration::from_millis(milliseconds);
    while !condition() {
        assert!(Instant::now() < end, "expected condition was not reached: {what}");
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
}

pub fn contracts() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("..").join("..").join("..").join("contracts")
}

pub fn fixture(name: &str) -> String {
    std::fs::read_to_string(contracts().join("fixtures").join(name)).unwrap_or_else(|e| panic!("fixture {name}: {e}"))
}

/// The stand-in CLI built beside the checks (`mighty-tools-fake`).
pub fn fake_cli() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_mighty-tools-fake"))
}

pub fn write(path: &Path, text: &str) {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).unwrap();
    }
    std::fs::write(path, text).unwrap();
}

/// A directory link at `link` to `target`: a symbolic link where the OS lets this user make one, else (Windows without the
/// privilege) a junction, which C#'s checks treat the same way.
#[allow(clippy::disallowed_methods, reason = "the checks make a junction with cmd's mklink when Windows refuses a symbolic link")]
pub fn directory_link(link: &Path, target: &Path) {
    #[cfg(windows)]
    {
        if std::os::windows::fs::symlink_dir(target, link).is_ok() {
            return;
        }
        let status = std::process::Command::new("cmd")
            .args(["/d", "/c", "mklink", "/J"])
            .arg(link)
            .arg(target)
            .stdout(std::process::Stdio::null())
            .status()
            .expect("cmd runs");
        assert!(status.success(), "a junction at {}", link.display());
    }
    #[cfg(not(windows))]
    std::os::unix::fs::symlink(target, link).expect("a symbolic link");
}
