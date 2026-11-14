//! What the accounts' checks share (C# `Verification.Temp`, `Verification.Self`, the fake runner). Every check pins Korean first,
//! as C#'s `Verification.RunAsync` does; none starts a real CLI, touches the network or reads the user's profile.
#![allow(dead_code)]

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use mighty_accounts::accounts::{CliRunResult, CliRunner};
use mighty_platform::BoxFuture;
use mighty_platform::fake::{FakeProcessHost, FakeShellEnvironment};
use mighty_run::providers::ProviderCatalog;

/// Korean copy, as every C# check reads it.
pub fn korean() {
    mighty_run::strings::set_language("ko");
}

/// A new empty folder of this check's own, named by its long name (CI's temporary folder sits under an 8.3 name).
pub fn temp(name: &str) -> PathBuf {
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
    let path = std::env::temp_dir().join(format!("mighty-accounts-test-{name}-{}-{nanos}", std::process::id()));
    std::fs::create_dir_all(&path).unwrap();
    long_path(path)
}

#[cfg(windows)]
fn long_path(path: PathBuf) -> PathBuf {
    let Ok(real) = std::fs::canonicalize(&path) else { return path };
    let text = real.to_string_lossy().into_owned();
    match text.strip_prefix(r"\\?\") {
        Some(rest) if rest.as_bytes().get(1) == Some(&b':') => PathBuf::from(rest),
        _ => path,
    }
}

#[cfg(not(windows))]
fn long_path(path: PathBuf) -> PathBuf {
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

pub fn text(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}

/// An empty file at `parts` joined, its folders made (C#'s `Touch`).
pub fn touch(path: PathBuf) -> String {
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(&path, "").unwrap();
    text(&path)
}

pub fn env(pairs: &[(&str, &str)]) -> BTreeMap<String, String> {
    pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect()
}

/// A program's file name on this OS (`name.exe` on Windows).
pub fn program(name: &str) -> String {
    if mighty_run::os::windows() { format!("{name}.exe") } else { name.to_string() }
}

/// A catalog over the fake process host and a fake environment (nothing real is ever started through it).
pub fn catalog() -> Arc<ProviderCatalog> {
    Arc::new(ProviderCatalog::new(Arc::new(FakeProcessHost::default()), Arc::new(FakeShellEnvironment::new(BTreeMap::new())), None))
}

/// One call the fake runner saw.
#[derive(Debug, Clone)]
pub struct Call {
    pub executable: String,
    pub arguments: Vec<String>,
    pub environment: BTreeMap<String, String>,
    pub working_directory: Option<PathBuf>,
}

type Respond = dyn Fn(&str, &[String]) -> CliRunResult + Send + Sync;

/// C#'s `FakeCliRunner`: records each call and answers from `respond`.
pub struct FakeRunner {
    respond: Mutex<Arc<Respond>>,
    pub calls: Mutex<Vec<Call>>,
}

impl FakeRunner {
    pub fn new(respond: impl Fn(&str, &[String]) -> CliRunResult + Send + Sync + 'static) -> Arc<FakeRunner> {
        Arc::new(FakeRunner { respond: Mutex::new(Arc::new(respond)), calls: Mutex::new(vec![]) })
    }

    pub fn answer(&self, respond: impl Fn(&str, &[String]) -> CliRunResult + Send + Sync + 'static) {
        *self.respond.lock().unwrap() = Arc::new(respond);
    }

    pub fn calls(&self) -> Vec<Call> {
        self.calls.lock().unwrap().clone()
    }

    pub fn clear(&self) {
        self.calls.lock().unwrap().clear();
    }
}

impl CliRunner for FakeRunner {
    fn run<'a>(
        &'a self,
        executable: &'a str,
        arguments: &'a [String],
        _timeout: Duration,
        environment: &'a BTreeMap<String, String>,
        working_directory: Option<&'a Path>,
    ) -> BoxFuture<'a, mighty_platform::Result<CliRunResult>> {
        self.calls.lock().unwrap().push(Call {
            executable: executable.to_string(),
            arguments: arguments.to_vec(),
            environment: environment.clone(),
            working_directory: working_directory.map(Path::to_path_buf),
        });
        let respond = self.respond.lock().unwrap().clone();
        let answer = respond(executable, arguments);
        Box::pin(async move { Ok(answer) })
    }
}

pub fn ok(output: &str) -> CliRunResult {
    CliRunResult { exit_code: 0, output: output.into(), error_output: String::new(), timed_out: false }
}

pub fn result(exit_code: i32, output: &str, error_output: &str) -> CliRunResult {
    CliRunResult { exit_code, output: output.into(), error_output: error_output.into(), timed_out: false }
}

/// The stand-in CLI (C# `Verification.Self`).
pub fn fake_cli() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_mighty-accounts-fake"))
}

pub fn json(text: &str) -> serde_json::Value {
    serde_json::from_str(text).unwrap_or_else(|e| panic!("{e}: {text}"))
}
