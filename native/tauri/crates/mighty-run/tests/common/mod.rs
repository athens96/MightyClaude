//! What the run engine's checks share (C# `Verification.Temp`, `Until`, `Self`, the fixtures found by walking up to
//! native/contracts/fixtures). Every check pins Korean first, as C#'s `Verification.RunAsync` does.
#![allow(dead_code)]

use std::path::{Path, PathBuf};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use serde_json::Value;

/// Korean copy, as every C# check reads it.
pub fn korean() {
    mighty_run::strings::set_language("ko");
}

/// A new empty folder of this check's own.
pub fn temp(name: &str) -> PathBuf {
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
    let path = std::env::temp_dir().join(format!("mighty-run-test-{name}-{}-{nanos}", std::process::id()));
    std::fs::create_dir_all(&path).unwrap();
    path
}

/// Removes a folder, waiting a little for a child still letting go of it.
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

pub fn fixtures() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("..").join("..").join("..").join("contracts").join("fixtures")
}

/// The agents' files the app ships (the Mac bundle's Resources/Agents, which C#'s build copies beside its exe and checks as
/// `Agents`): pi's `ask_user` extension among them.
pub fn agents() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("..")
        .join("macos")
        .join("Sources")
        .join("MightyCore")
        .join("Resources")
        .join("Agents")
}

pub fn fixture(name: &str) -> String {
    std::fs::read_to_string(fixtures().join(name)).unwrap_or_else(|e| panic!("fixture {name}: {e}"))
}

pub fn fixture_json(name: &str) -> Value {
    serde_json::from_str(&fixture(name)).unwrap()
}

/// The frames of a recording, one per line.
pub fn recording(name: &str) -> Vec<String> {
    fixture(&format!("{name}.jsonl")).split('\n').map(|l| l.trim_end_matches('\r').to_string()).filter(|l| !l.trim().is_empty()).collect()
}

pub fn json(text: &str) -> Value {
    serde_json::from_str(text).unwrap_or_else(|e| panic!("{e}: {text}"))
}

/// The stand-in CLI (C# `Verification.Self`).
pub fn fake_cli() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_mighty-fake-cli"))
}

pub fn fake_text() -> String {
    fake_cli().to_string_lossy().into_owned()
}

/// `Verification.Self(args)`: the fake with `args` first, at version 2.1.271.
pub fn fake_command(args: &[&str]) -> mighty_run::model::CliCommand {
    mighty_run::model::CliCommand::new(fake_text(), args.iter().map(|a| a.to_string()).collect(), Some("2.1.271"))
}

/// A folder with a mighty-bridge plugin in it (`.claude-plugin/plugin.json`), as the checks make one.
pub fn plugin(directory: &Path) -> String {
    let plugin = directory.join("plugin");
    std::fs::create_dir_all(plugin.join(".claude-plugin")).unwrap();
    std::fs::write(plugin.join(".claude-plugin").join("plugin.json"), "{}").unwrap();
    plugin.to_string_lossy().into_owned()
}

/// The arguments a fake recorded.
pub fn recorded_args(record: &Path) -> Vec<String> {
    let text = std::fs::read_to_string(format!("{}.args", record.display())).unwrap();
    serde_json::from_str(&text).unwrap()
}

pub fn after(args: &[String], flag: &str) -> String {
    let at = args.iter().position(|a| a == flag).unwrap_or_else(|| panic!("{flag} missing from {args:?}"));
    args[at + 1].clone()
}

/// The CLI environment the checks resolve: this process's own variables, as a plain `CliEnvironment.Current()` gives.
pub fn environment() -> std::sync::Arc<dyn mighty_platform::ShellEnvironment> {
    std::sync::Arc::new(mighty_platform::fake::FakeShellEnvironment::new(std::env::vars().collect()))
}

/// `new ProviderCatalog((provider, _) => …)` over the real Windows process layer: `find` names the command for a provider.
#[cfg(windows)]
pub fn catalog(
    find: impl Fn(&str) -> Option<mighty_run::model::CliCommand> + Send + Sync + 'static,
) -> std::sync::Arc<mighty_run::providers::ProviderCatalog> {
    let find = std::sync::Arc::new(find);
    let finder: mighty_run::providers::Finder = std::sync::Arc::new(move |provider: String| {
        let found = find(&provider);
        Box::pin(async move { found })
    });
    std::sync::Arc::new(mighty_run::providers::ProviderCatalog::new(
        std::sync::Arc::new(mighty_platform_windows::WindowsProcessHost::new()),
        environment(),
        Some(finder),
    ))
}

/// A catalog whose every provider is the fake with `args`.
#[cfg(windows)]
pub fn fake_catalog(args: &[&str]) -> std::sync::Arc<mighty_run::providers::ProviderCatalog> {
    let command = fake_command(args);
    catalog(move |_| Some(command.clone()))
}

/// Whether the process is still running (C# `Alive`).
#[cfg(windows)]
pub fn alive(id: u32) -> bool {
    use windows_sys::Win32::Foundation::{CloseHandle, WAIT_TIMEOUT};
    use windows_sys::Win32::System::Threading::{OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE, WaitForSingleObject};
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

/// A catalog that finds no CLI (C# `Verification.Absent`).
#[cfg(windows)]
pub fn absent_catalog() -> std::sync::Arc<mighty_run::providers::ProviderCatalog> {
    catalog(|_| None)
}
