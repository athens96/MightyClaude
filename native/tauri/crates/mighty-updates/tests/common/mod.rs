//! What the update and plugin checks share (C# `Verification.Temp`, `Until`, and each suite's `FakeRunner`). Every check pins Korean
//! first, as C#'s `Verification.RunAsync` does. The fake runner starts no process: no real claude, codex, npm or winget, no network,
//! no real profile.
#![allow(dead_code)]

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use mighty_core::model::Workspace;
use mighty_platform::BoxFuture;
use mighty_updates::runner::{CancellationToken, CliCall, CliRunResult, CliRunner, RunError};
use tokio::sync::watch;

/// Korean copy, as every C# check reads it.
pub fn korean() {
    mighty_run::strings::set_language("ko");
}

/// A new empty folder of this check's own, named in full (CI's temp folder sits under an 8.3 name, `C:\Users\RUNNER~1`).
pub fn temp(name: &str) -> PathBuf {
    // The count keeps two checks of one name apart when they read the same clock (tests run in parallel).
    static NEXT: AtomicU64 = AtomicU64::new(0);
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
    let count = NEXT.fetch_add(1, Ordering::Relaxed);
    let path = std::env::temp_dir().join(format!("mighty-updates-test-{name}-{}-{nanos}-{count}", std::process::id()));
    std::fs::create_dir_all(&path).unwrap();
    long_path(path)
}

#[cfg(windows)]
fn long_path(path: PathBuf) -> PathBuf {
    let Ok(real) = std::fs::canonicalize(&path) else { return path };
    let text = real.to_string_lossy();
    match text.strip_prefix(r"\\?\") {
        Some(rest) if rest.as_bytes().get(1) == Some(&b':') => PathBuf::from(rest),
        _ => path,
    }
}

#[cfg(not(windows))]
fn long_path(path: PathBuf) -> PathBuf {
    path
}

pub fn text(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}

/// `Path.Combine(parts)`, created as a folder.
pub fn dir(base: &Path, parts: &[&str]) -> PathBuf {
    let path = parts.iter().fold(base.to_path_buf(), |p, part| p.join(part));
    std::fs::create_dir_all(&path).unwrap();
    path
}

/// An empty file named `name` in `directory`.
pub fn exe(directory: &Path, name: &str) -> PathBuf {
    let path = directory.join(name);
    std::fs::write(&path, "").unwrap();
    path
}

/// `{ PATH = entries joined }`.
pub fn env(entries: &[&Path]) -> BTreeMap<String, String> {
    let separator = mighty_run::os::path_separator().to_string();
    BTreeMap::from([("PATH".to_string(), entries.iter().map(|p| text(p)).collect::<Vec<_>>().join(&separator))])
}

pub fn workspace(path: &str) -> Workspace {
    Workspace { path: Some(path.to_string()), name: Some("Fixture".into()), ..Workspace::default() }
}

pub fn ok(output: &str) -> Result<CliRunResult, RunError> {
    Ok(CliRunResult::new(0, output, "", false))
}

pub fn failed(error: &str) -> Result<CliRunResult, RunError> {
    Ok(CliRunResult::new(1, "", error, false))
}

pub fn timed_out() -> Result<CliRunResult, RunError> {
    Ok(CliRunResult::new(-1, "", "", true))
}

pub fn strings(values: &[&str]) -> Vec<String> {
    values.iter().map(|v| v.to_string()).collect()
}

/// A script that answers one call (its executable and arguments) at once.
pub type SyncScript = Arc<dyn Fn(&str, &[String]) -> Result<CliRunResult, RunError> + Send + Sync>;
/// A script that answers later, given the call and the caller's token.
pub type AsyncScript = Arc<dyn Fn(CliCall, CancellationToken) -> BoxFuture<'static, Result<CliRunResult, RunError>> + Send + Sync>;

/// Each C# suite's `FakeRunner`: records every call, refuses a cancelled one, answers from its script.
pub struct FakeRunner {
    calls: Mutex<Vec<CliCall>>,
    script: Mutex<SyncScript>,
    asynchronous: Mutex<Option<AsyncScript>>,
}

impl FakeRunner {
    pub fn new(script: impl Fn(&str, &[String]) -> Result<CliRunResult, RunError> + Send + Sync + 'static) -> Arc<FakeRunner> {
        Arc::new(FakeRunner { calls: Mutex::new(Vec::new()), script: Mutex::new(Arc::new(script)), asynchronous: Mutex::new(None) })
    }

    /// C#'s `runner.Async = …`: from now on the calls are answered by `script`.
    pub fn set_async(
        &self,
        script: impl Fn(CliCall, CancellationToken) -> BoxFuture<'static, Result<CliRunResult, RunError>> + Send + Sync + 'static,
    ) {
        *self.asynchronous.lock().unwrap() = Some(Arc::new(script));
    }

    pub fn calls(&self) -> Vec<CliCall> {
        self.calls.lock().unwrap().clone()
    }

    pub fn args(&self) -> Vec<Vec<String>> {
        self.calls().into_iter().map(|c| c.arguments).collect()
    }

    pub fn clear(&self) {
        self.calls.lock().unwrap().clear();
    }
}

impl CliRunner for FakeRunner {
    fn run(&self, call: CliCall, cancel: CancellationToken) -> BoxFuture<'_, Result<CliRunResult, RunError>> {
        self.calls.lock().unwrap().push(call.clone());
        if cancel.is_cancelled() {
            return Box::pin(async { Err(RunError::Cancelled) });
        }
        if let Some(script) = self.asynchronous.lock().unwrap().clone() {
            return script(call, cancel);
        }
        let script = self.script.lock().unwrap().clone();
        let answer = script(&call.executable, &call.arguments);
        Box::pin(async move { answer })
    }
}

/// C#'s `TaskCompletionSource`: set once, awaited by anyone.
#[derive(Clone)]
pub struct Signal(Arc<watch::Sender<bool>>);

impl Default for Signal {
    fn default() -> Self {
        Signal(Arc::new(watch::channel(false).0))
    }
}

impl Signal {
    pub fn new() -> Signal {
        Signal::default()
    }
    pub fn set(&self) {
        self.0.send_replace(true);
    }
    pub fn is_set(&self) -> bool {
        *self.0.borrow()
    }
    pub async fn wait(&self) {
        let mut receiver = self.0.subscribe();
        let _ = receiver.wait_for(|set| *set).await;
    }
    /// `gate.Task.WaitAsync(token)`: Err when the token is cancelled first.
    pub async fn wait_or_cancel(&self, cancel: &CancellationToken) -> Result<(), RunError> {
        tokio::select! {
            _ = self.wait() => Ok(()),
            _ = cancel.cancelled() => Err(RunError::Cancelled),
        }
    }
}

/// Waits until `condition` holds, failing after `milliseconds` (C# `Verification.Until`).
pub async fn until(mut condition: impl FnMut() -> bool, milliseconds: u64, what: &str) {
    let end = Instant::now() + Duration::from_millis(milliseconds);
    while !condition() {
        assert!(Instant::now() < end, "expected condition was not reached: {what}");
        tokio::time::sleep(Duration::from_millis(5)).await;
    }
}

/// The value of every locale key in Korean.
pub fn ko(key: &str) -> String {
    mighty_core::locale::Locale::new("ko").get(key)
}

/// C# StringsVerification `Placeholders`: None when every brace pair is a `{name}`, else the offending fragment.
pub fn bad_placeholder(text: &str) -> Option<String> {
    let chars: Vec<char> = text.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        match chars[i] {
            '}' => return Some("}".into()),
            '{' => {
                let Some(close) = chars[i..].iter().position(|c| *c == '}').map(|at| at + i) else {
                    return Some(chars[i..].iter().collect());
                };
                let name: String = chars[i + 1..close].iter().collect();
                let first = name.chars().next();
                if !first.is_some_and(|c| c.is_ascii_alphabetic()) || !name.chars().all(|c| c.is_ascii_alphanumeric() || c == '_') {
                    return Some(format!("{{{name}}}"));
                }
                i = close;
            }
            _ => {}
        }
        i += 1;
    }
    None
}
