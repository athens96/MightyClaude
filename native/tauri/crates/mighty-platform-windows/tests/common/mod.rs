//! What the Windows checks share (C# `Verification.Temp`, `Until`, `Alive`, `Self`).
#![allow(dead_code)]

use std::path::{Path, PathBuf};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use mighty_platform::SpawnRequest;
use mighty_platform::process::ChildReader;
use tokio::io::AsyncReadExt;
use windows_sys::Win32::Foundation::{CloseHandle, WAIT_TIMEOUT};
use windows_sys::Win32::System::Threading::{OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE, WaitForSingleObject};

/// A new empty folder of this test's own (C# `Verification.Temp`).
pub fn temp(name: &str) -> PathBuf {
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
    let path = std::env::temp_dir().join(format!("mighty-platform-test-{name}-{}-{nanos}", std::process::id()));
    std::fs::create_dir_all(&path).unwrap();
    path
}

/// Removes `path`, waiting a little for a process that is still letting go of it (C# `DeleteWhenFree`); a folder that stays
/// busy is left behind, which is no failure of the check.
pub async fn remove_when_free(path: &Path) {
    for _ in 0..40 {
        if !path.exists() || std::fs::remove_dir_all(path).is_ok() {
            return;
        }
        tokio::time::sleep(Duration::from_millis(250)).await;
    }
}

/// Waits until `condition` holds, failing after `milliseconds` (C# `Verification.Until`).
pub async fn until(mut condition: impl FnMut() -> bool, milliseconds: u64, what: &str) {
    let end = Instant::now() + Duration::from_millis(milliseconds);
    while !condition() {
        assert!(Instant::now() < end, "expected condition was not reached: {what}");
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
}

/// Whether the process is still running (C# `Alive`).
pub fn alive(id: u32) -> bool {
    // SAFETY: a query-only open of a process id, closed below.
    unsafe {
        let process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE, 0, id);
        if process.is_null() {
            return false;
        }
        let running = WaitForSingleObject(process, 0) == WAIT_TIMEOUT;
        CloseHandle(process);
        running
    }
}

/// The stand-in child (C# `Verification.Self`).
pub fn fake() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_mighty-fake-child"))
}

pub fn fake_text() -> String {
    fake().to_string_lossy().into_owned()
}

/// A request to run the stand-in child with `args` in `cwd`.
pub fn fake_request(args: &[&str], cwd: &Path) -> SpawnRequest {
    SpawnRequest::new(fake(), args.iter().copied(), cwd)
}

/// All of a pipe, as bytes.
pub async fn read_all(reader: Option<ChildReader>) -> Vec<u8> {
    let mut bytes = Vec::new();
    if let Some(mut reader) = reader {
        reader.read_to_end(&mut bytes).await.unwrap();
    }
    bytes
}

/// The id a `--long-child` wrote, once it has.
pub async fn pid_from(file: &Path) -> u32 {
    until(|| file.exists(), 15_000, "the pid file").await;
    std::fs::read_to_string(file).unwrap().trim().parse().unwrap()
}

/// The quoted command line that runs `--long-child <pid file>` (C# `Verification.LongCommand`).
pub fn long_command(pid_file: &Path) -> String {
    let parts = [fake_text(), "--long-child".into(), pid_file.to_string_lossy().into_owned()];
    parts.iter().map(|part| mighty_platform_windows::command::quote_windows(part)).collect::<Vec<_>>().join(" ")
}
