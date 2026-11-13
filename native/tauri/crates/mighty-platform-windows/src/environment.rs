//! The environment the agent CLIs start with on Windows: C# `CliEnvironment` (CliEnvironment.cs, this process's environment with
//! the persisted PATH and credential variables read again from the registry) and `WindowsShellEnvironment`
//! (WindowsShellEnvironment.cs, the same ConsoleHost profiles as the app's terminal, captured from the user's home rather than an
//! untrusted project; normal PowerShell execution policy is kept; a prompt, broken profile or malformed capture falls back after
//! a bounded wait; neither stream is logged or put in an error).

use std::collections::BTreeMap;
use std::fmt;
use std::path::Path;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use base64::Engine;
use base64::engine::general_purpose::{GeneralPurpose, GeneralPurposeConfig};
use base64::engine::{DecodePaddingMode, general_purpose};
use mighty_platform::process::ChildReader;
use mighty_platform::{BoxFuture, CliEnvironment, EnvironmentSource, ProcessHost, ShellEnvironment, SpawnRequest};
use serde::de::{Deserializer, MapAccess, Visitor};
use tokio::io::AsyncReadExt;
use windows_sys::Win32::Foundation::ERROR_SUCCESS;
use windows_sys::Win32::System::Environment::ExpandEnvironmentStringsW;
use windows_sys::Win32::System::Registry::{
    HKEY, HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, KEY_READ, REG_BINARY, REG_DWORD, REG_EXPAND_SZ, REG_MULTI_SZ, REG_QWORD, REG_SZ,
    RegCloseKey, RegEnumValueW, RegOpenKeyExW,
};

use crate::Variables;
use crate::process::{CloseOnDrop, WindowsProcessHost, lock};
use crate::text::TextDecoder;
use crate::win::{equal_ignore_case, fold, starts_with_ignore_case, wide};

// ---- CliEnvironment: the baseline --------------------------------------------------------------------------------------------

/// The persisted variables a launch takes from the registry (CliEnvironment.cs:11): PATH and the credential families.
fn relevant(key: &str) -> bool {
    equal_ignore_case(key, "PATH")
        || ["AWS_", "ANTHROPIC_", "CLAUDE_", "CODEX_", "OPENAI_", "GEMINI_", "GOOGLE_"]
            .iter()
            .any(|prefix| starts_with_ignore_case(key, prefix))
}

/// `Environment.ExpandEnvironmentVariables`: `%NAME%` replaced from this process's environment.
pub(crate) fn expand(value: &str) -> String {
    if !value.contains('%') {
        return value.to_string();
    }
    let source = wide(value);
    let mut buffer = vec![0u16; value.len() + 64];
    loop {
        // SAFETY: a NUL-terminated source and a buffer of `buffer.len()` units.
        let needed = unsafe { ExpandEnvironmentStringsW(source.as_ptr(), buffer.as_mut_ptr(), buffer.len() as u32) } as usize;
        if needed == 0 {
            return value.to_string();
        }
        if needed <= buffer.len() {
            return String::from_utf16_lossy(&buffer[..needed - 1]);
        }
        buffer.resize(needed, 0);
    }
}

/// One registry environment (`Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Machine | User)`): every value as
/// .NET's `RegistryKey.GetValue(name, "").ToString()` gives it — REG_EXPAND_SZ expanded, numbers in decimal, other kinds as their
/// .NET type name. Nothing when the key cannot be opened.
fn registry_environment(machine: bool) -> Variables {
    let (root, path) = if machine {
        (HKEY_LOCAL_MACHINE, r"System\CurrentControlSet\Control\Session Manager\Environment")
    } else {
        (HKEY_CURRENT_USER, "Environment")
    };
    let mut values = Variables::new();
    let path = wide(path);
    let mut key: HKEY = std::ptr::null_mut();
    // SAFETY: a NUL-terminated subkey; `key` receives the opened handle.
    if unsafe { RegOpenKeyExW(root, path.as_ptr(), 0, KEY_READ, &mut key) } != ERROR_SUCCESS {
        return values;
    }
    let mut index = 0u32;
    loop {
        let mut name = vec![0u16; 16_384];
        let mut data = vec![0u8; 4096];
        let (mut name_length, mut data_length, mut kind) = (name.len() as u32, data.len() as u32, 0u32);
        // SAFETY: buffers of the lengths given; `key` is open.
        let mut status = unsafe {
            RegEnumValueW(key, index, name.as_mut_ptr(), &mut name_length, std::ptr::null(), &mut kind, data.as_mut_ptr(), &mut data_length)
        };
        if status == windows_sys::Win32::Foundation::ERROR_MORE_DATA {
            data.resize(data_length as usize + 2, 0);
            (name_length, data_length) = (name.len() as u32, data.len() as u32);
            // SAFETY: as above, with a buffer the size Windows asked for.
            status = unsafe {
                RegEnumValueW(
                    key,
                    index,
                    name.as_mut_ptr(),
                    &mut name_length,
                    std::ptr::null(),
                    &mut kind,
                    data.as_mut_ptr(),
                    &mut data_length,
                )
            };
        }
        if status != ERROR_SUCCESS {
            break;
        }
        let name = String::from_utf16_lossy(&name[..name_length as usize]);
        let data = &data[..data_length as usize];
        let text = |bytes: &[u8]| {
            let units: Vec<u16> = bytes.as_chunks::<2>().0.iter().map(|pair| u16::from_le_bytes(*pair)).collect();
            let end = units.iter().position(|unit| *unit == 0).unwrap_or(units.len());
            String::from_utf16_lossy(&units[..end])
        };
        let value = match kind {
            REG_SZ => text(data),
            REG_EXPAND_SZ => expand(&text(data)),
            REG_DWORD if data.len() >= 4 => i32::from_le_bytes([data[0], data[1], data[2], data[3]]).to_string(),
            REG_QWORD if data.len() >= 8 => i64::from_le_bytes(data[..8].try_into().unwrap_or([0; 8])).to_string(),
            REG_MULTI_SZ => "System.String[]".to_string(),
            REG_BINARY => "System.Byte[]".to_string(),
            _ => "System.Byte[]".to_string(),
        };
        if !values.contains(&name) {
            values.set(name, value);
        }
        index += 1;
    }
    // SAFETY: the key opened above.
    unsafe { RegCloseKey(key) };
    values
}

/// The persisted variables a previous baseline took, so one removed from the registry since is unset rather than inherited
/// (C# `previouslyPersisted`, CliEnvironment.cs:10, 40).
static PREVIOUSLY_PERSISTED: Mutex<BTreeMap<String, String>> = Mutex::new(BTreeMap::new());

/// C# `CliEnvironment.Baseline()` (CliEnvironment.cs:29-44): this process's environment, with the PATH and credential variables
/// Windows keeps for new sessions (machine, then user; the PATH is the machine's and the user's joined) read again, so a GUI
/// process that predates a changed user-level Bedrock token or PATH sees the new one. A persisted variable since removed is set
/// empty. Values stay in memory.
pub fn baseline() -> Variables {
    let mut process = Variables::process();
    let mut previously = lock(&PREVIOUSLY_PERSISTED);
    let mut persisted = registry_environment(true);
    let user = registry_environment(false);
    let machine_path = persisted.get("PATH").unwrap_or("").to_string();
    for (name, value) in user.iter() {
        persisted.set(name, value);
    }
    if let Some(user_path) = user.get("PATH") {
        persisted.set("PATH", format!("{machine_path};{user_path}"));
    }
    for name in previously.values() {
        if !persisted.contains(name) {
            process.set(name.clone(), "");
        }
    }
    for (name, value) in persisted.iter().filter(|(name, _)| relevant(name)) {
        process.set(name, expand(value));
        previously.entry(fold(name)).or_insert_with(|| name.to_string());
    }
    process
}

// ---- WindowsShellEnvironment ---------------------------------------------------------------------------------------------------

/// The most a capture may print (WindowsShellEnvironment.cs:22).
pub const MAXIMUM_OUTPUT: usize = 1_048_576;

/// One resolved environment (C# `CliEnvironmentSnapshot`): credentials in memory; its `Debug` and `Display` never show a value.
#[derive(Clone, PartialEq, Eq)]
pub struct CliEnvironmentSnapshot {
    pub values: Variables,
    pub from_profile: bool,
}

impl fmt::Display for CliEnvironmentSnapshot {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(if self.from_profile { "CLI environment (terminal profile)" } else { "CLI environment (process/registry fallback)" })
    }
}

impl fmt::Debug for CliEnvironmentSnapshot {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        fmt::Display::fmt(self, f)
    }
}

/// Runs the capture: given the baseline and the frame's nonce, the shell's standard output, or None (C#'s `capture` delegate).
pub type Capture = Arc<dyn Fn(Variables, String) -> BoxFuture<'static, Option<String>> + Send + Sync>;

/// C# `WindowsShellEnvironment` and the platform's [`ShellEnvironment`]. One capture serves every workspace: it always runs in the
/// user's home.
pub struct WindowsShellEnvironment {
    /// One resolve at a time (C# `SemaphoreSlim gate`), so a forced refresh never joins a capture already running.
    gate: tokio::sync::Mutex<()>,
    saved: Mutex<Saved>,
    capture: Capture,
    timeout: Duration,
    lifetime: Duration,
}

#[derive(Default)]
struct Saved {
    values: Option<Variables>,
    at: Option<Instant>,
}

impl Default for WindowsShellEnvironment {
    fn default() -> WindowsShellEnvironment {
        WindowsShellEnvironment::new()
    }
}

impl WindowsShellEnvironment {
    /// The real capture (the app's terminal shell, PowerShell's profiles), 4 s, reused for 10 s (WindowsShellEnvironment.cs:30-36).
    pub fn new() -> WindowsShellEnvironment {
        WindowsShellEnvironment::with_capture(Arc::new(|baseline, nonce| Box::pin(capture_profile(baseline, nonce))), None, None)
    }

    /// Another capture, time limit or lifetime (the checks).
    pub fn with_capture(capture: Capture, timeout: Option<Duration>, lifetime: Option<Duration>) -> WindowsShellEnvironment {
        WindowsShellEnvironment {
            gate: tokio::sync::Mutex::new(()),
            saved: Mutex::new(Saved::default()),
            capture,
            timeout: timeout.unwrap_or(Duration::from_secs(4)),
            lifetime: lifetime.unwrap_or(Duration::from_secs(10)),
        }
    }

    /// C# `Current(baseline)` (WindowsShellEnvironment.cs:38-43): synchronous terminal consumers keep the last resolved snapshot. The
    /// lifetime decides when async entrypoints refresh, never when credentials vanish.
    pub fn current_with(&self, baseline: &Variables) -> Variables {
        merge(baseline, lock(&self.saved).values.as_ref())
    }

    /// C# `ResolveAsync(baseline, force)` (WindowsShellEnvironment.cs:45-68). Dropping the future (C#'s cancelled token) ends the
    /// capture and changes nothing.
    pub async fn resolve_with(&self, baseline: &Variables, force: bool) -> CliEnvironmentSnapshot {
        let _gate = self.gate.lock().await;
        {
            let saved = lock(&self.saved);
            if !force
                && let (Some(values), Some(at)) = (&saved.values, saved.at)
                && at.elapsed() < self.lifetime
            {
                return CliEnvironmentSnapshot { values: merge(baseline, Some(values)), from_profile: true };
            }
        }
        let nonce = new_nonce();
        let output = tokio::time::timeout(self.timeout, (self.capture)(baseline.clone(), nonce.clone())).await.ok().flatten();
        let next = output.and_then(|output| parse(&output, &nonce));
        // Force failures discard a previous token too; an older successful capture must never silently outlive a changed/broken
        // profile.
        let from_profile = next.is_some();
        let values = merge(baseline, next.as_ref());
        *lock(&self.saved) = Saved { values: next, at: Some(Instant::now()) };
        CliEnvironmentSnapshot { values, from_profile }
    }

    fn source(&self) -> EnvironmentSource {
        if lock(&self.saved).values.is_some() { EnvironmentSource::Profile } else { EnvironmentSource::ProcessFallback }
    }
}

impl ShellEnvironment for WindowsShellEnvironment {
    /// `CliEnvironment.RefreshAsync(force)`: the registry-refreshed baseline under the captured profile. Windows captures in the
    /// home folder whatever the workspace, so `workspace` changes nothing.
    fn resolve(&self, _workspace: Option<&Path>, force: bool) -> BoxFuture<'_, CliEnvironment> {
        Box::pin(async move {
            let snapshot = self.resolve_with(&baseline(), force).await;
            let source = if snapshot.from_profile { EnvironmentSource::Profile } else { EnvironmentSource::ProcessFallback };
            CliEnvironment { values: snapshot.values.into_map(), source }
        })
    }

    /// `CliEnvironment.Current()`.
    fn current(&self) -> CliEnvironment {
        let values = self.current_with(&baseline()).into_map();
        CliEnvironment { values, source: self.source() }
    }

    fn invalidate(&self, _workspace: Option<&Path>) {
        *lock(&self.saved) = Saved::default();
    }
}

/// C# `Merge` (WindowsShellEnvironment.cs:70-79): the baseline, and over it the capture. A child process lays its environment over
/// its parent's, so every baseline name the capture lacks is set empty: a credential the fresh profile unset is not inherited.
fn merge(baseline: &Variables, captured: Option<&Variables>) -> Variables {
    let mut result = baseline.clone();
    let Some(captured) = captured else { return result };
    let names: Vec<String> = result.names().map(String::from).collect();
    for name in names {
        if !captured.contains(&name) {
            result.set(name, "");
        }
    }
    for (name, value) in captured.iter() {
        result.set(name, value);
    }
    result
}

/// A fresh frame nonce: 32 lowercase hex digits (`Guid.NewGuid().ToString("N")`).
fn new_nonce() -> String {
    use std::hash::{BuildHasher, Hasher};
    let random = |salt: u64| {
        let mut hasher = std::collections::hash_map::RandomState::new().build_hasher();
        hasher.write_u64(salt);
        hasher.write_u128(std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|t| t.as_nanos()).unwrap_or_default());
        hasher.finish()
    };
    format!("{:016x}{:016x}", random(1), random(2))
}

fn valid_nonce(nonce: &str) -> bool {
    nonce.len() == 32 && nonce.bytes().all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
}

/// The PowerShell the capture runs (WindowsShellEnvironment.cs:82-91). No credential values or paths appear in argv. Base64 keeps
/// profile chatter and newlines inside values outside the framing grammar. None for a nonce that is not 32 lowercase hex digits
/// (C#'s ArgumentException).
pub fn command(nonce: &str) -> Option<String> {
    if !valid_nonce(nonce) {
        return None;
    }
    Some(format!(
        "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);\
         $mightyEnvJson=Microsoft.PowerShell.Utility\\ConvertTo-Json -InputObject ([Environment]::GetEnvironmentVariables('Process')) -Compress;\
         $mightyEnvData=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($mightyEnvJson));\
         [Console]::Out.Write(([string][char]0)+'MIGHTY_ENV_{nonce}'+[char]0+$mightyEnvData+[char]0+'MIGHTY_END_{nonce}'+[char]0)"
    ))
}

/// The shell's arguments (WindowsShellEnvironment.cs:93): `-NoLogo -NonInteractive -EncodedCommand <base64 of the UTF-16LE command>`.
pub fn arguments(nonce: &str) -> Option<Vec<String>> {
    let command = command(nonce)?;
    let utf16: Vec<u8> = command.encode_utf16().flat_map(u16::to_le_bytes).collect();
    Some(vec!["-NoLogo".into(), "-NonInteractive".into(), "-EncodedCommand".into(), general_purpose::STANDARD.encode(utf16)])
}

/// `Convert.FromBase64String`: padding required (the length is a multiple of 4 once white space is gone), unused trailing bits
/// ignored.
const DOTNET_BASE64: GeneralPurpose = GeneralPurpose::new(
    &base64::alphabet::STANDARD,
    GeneralPurposeConfig::new().with_decode_allow_trailing_bits(true).with_decode_padding_mode(DecodePaddingMode::Indifferent),
);

/// The JSON object's members in order, duplicates kept (System.Text.Json's `EnumerateObject` sees each).
struct Members(Vec<(String, serde_json::Value)>);

impl<'de> serde::Deserialize<'de> for Members {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> std::result::Result<Members, D::Error> {
        struct Object;
        impl<'de> Visitor<'de> for Object {
            type Value = Members;
            fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                f.write_str("an object")
            }
            fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> std::result::Result<Members, A::Error> {
                let mut members = Vec::new();
                while let Some(member) = map.next_entry::<String, serde_json::Value>()? {
                    members.push(member);
                }
                Ok(Members(members))
            }
        }
        deserializer.deserialize_map(Object)
    }
}

/// C# `Parse` (WindowsShellEnvironment.cs:95-120): the one frame of this nonce in the shell's output, base64 of a JSON object of
/// string values, read strictly. None for a different invocation's frame, a repeated or partial frame, more than 1 MiB of output,
/// a name that is empty, too long, holds `=` or a control character, a value that is not a string, holds NUL or is too long, two
/// names that differ only in case, more than 4096 names, or no PATH.
pub fn parse(output: &str, nonce: &str) -> Option<Variables> {
    if !valid_nonce(nonce) || output.len() > MAXIMUM_OUTPUT {
        return None;
    }
    let start = format!("\0MIGHTY_ENV_{nonce}\0");
    let end = format!("\0MIGHTY_END_{nonce}\0");
    let first = output.find(&start)?;
    let after_start = first + start.len();
    if output[after_start..].contains(&start) {
        return None;
    }
    let last = after_start + output[after_start..].find(&end)?;
    if output[last + end.len()..].contains(&end) {
        return None;
    }
    let encoded = &output[after_start..last];
    if encoded.is_empty() || !encoded.encode_utf16().count().is_multiple_of(4) || encoded.contains(['\r', '\n', ' ']) {
        return None;
    }
    let compact: String = encoded.chars().filter(|c| *c != '\t').collect();
    if !compact.len().is_multiple_of(4) {
        return None;
    }
    let bytes = DOTNET_BASE64.decode(compact).ok()?;
    // `JsonDocument.Parse(bytes)` takes no byte order mark (tests/parity: "a byte order mark"), and neither does serde_json.
    let Members(members) = serde_json::from_slice(&bytes).ok()?;
    let mut values = Variables::new();
    for (name, value) in members {
        let length = name.encode_utf16().count();
        if values.len() >= 4096 || length == 0 || length > 32_767 || name.contains('=') || name.chars().any(char::is_control) {
            return None;
        }
        let serde_json::Value::String(value) = value else { return None };
        if value.contains('\0') || value.encode_utf16().count() > 131_072 || values.contains(&name) {
            return None;
        }
        values.set(name, value);
    }
    values.get("PATH").is_some_and(|path| !path.trim().is_empty()).then_some(values)
}

/// C# `CaptureAsync` (WindowsShellEnvironment.cs:122-139): the app's terminal shell, when it is Windows PowerShell or pwsh, run in
/// the user's home with the baseline, its stdin closed; its output when it exits 0. Any failure is None.
async fn capture_profile(baseline: Variables, nonce: String) -> Option<String> {
    let shell = crate::pty::default_shell();
    let name = shell.file_name()?.to_string_lossy().into_owned();
    if !equal_ignore_case(&name, "powershell.exe") && !equal_ignore_case(&name, "pwsh.exe") {
        return None;
    }
    let home = dirs::home_dir()?;
    if !home.is_absolute() || !home.is_dir() {
        return None;
    }
    let mut request = SpawnRequest::new(shell, arguments(&nonce)?, home);
    for (name, value) in baseline.iter() {
        request.env.insert(name.to_string(), value.to_string());
    }
    // `ChildProcess.StartInfo` sets NO_COLOR after the given environment.
    request.env.retain(|name, _| !equal_ignore_case(name, "NO_COLOR"));
    request.env.insert("NO_COLOR".into(), "1".into());
    run_capture(request).await
}

/// Runs one capture child (C# `CaptureAsync` past choosing the shell; the wire check runs it with its own arguments): stdin
/// closed, stdout read (1 MiB of UTF-8 at most), stderr drained (1 Mi characters at most), the exit code 0 or nothing; the child
/// closed after (`await using`), a failed close failing the capture. Dropping the future kills the child.
pub async fn run_capture(request: SpawnRequest) -> Option<String> {
    let mut child = WindowsProcessHost.spawn(request).await.ok()?;
    let guard = CloseOnDrop(Some(child.control()));
    let control = child.control();
    drop(child.stdin.take());
    let (output, error, exit) = tokio::join!(
        read_bounded(child.stdout.take(), MAXIMUM_OUTPUT),
        discard_bounded(child.stderr.take(), MAXIMUM_OUTPUT),
        control.wait()
    );
    let result = match (output, error, exit) {
        (Some(output), true, Ok(0)) => Some(output),
        _ => None,
    };
    if result.is_none() {
        control.kill();
    }
    guard.disarm().close().await.ok()?;
    result
}

/// C# `ReadAsync` (WindowsShellEnvironment.cs:140-150): the whole output as text; None past `limit` bytes of UTF-8.
///
/// A deliberate difference (docs/tauri-architecture.md §2.2): C# throws `InvalidDataException` past the limit, which none of
/// `ResolveAsync`'s catches takes, so `RefreshAsync` itself fails. Here the capture is only a failed one (as [`discard_bounded`]
/// past its limit is): the resolve falls back to the baseline, never fails, and drops the capture it had kept, as after any
/// other failed capture. A profile that prints a megabyte must not leave the CLIs without an environment.
pub(crate) async fn read_bounded(reader: Option<ChildReader>, limit: usize) -> Option<String> {
    let mut reader = reader?;
    let mut decoder = TextDecoder::new();
    let mut text = String::new();
    let mut buffer = vec![0u8; 4096];
    loop {
        let read = reader.read(&mut buffer).await.ok()?;
        let chunk = if read == 0 { decoder.finish() } else { decoder.push(&buffer[..read]) };
        text.push_str(&chunk);
        if text.len() > limit {
            return None;
        }
        if read == 0 {
            return Some(text);
        }
    }
}

/// C# `DiscardAsync` (WindowsShellEnvironment.cs:151-156): read and drop; false past `limit` characters (where C# throws, the
/// same deliberate difference as [`read_bounded`]'s).
pub(crate) async fn discard_bounded(reader: Option<ChildReader>, limit: usize) -> bool {
    let Some(mut reader) = reader else { return true };
    let mut decoder = TextDecoder::new();
    let mut count = 0usize;
    let mut buffer = vec![0u8; 4096];
    loop {
        let Ok(read) = reader.read(&mut buffer).await else { return false };
        let chunk = if read == 0 { decoder.finish() } else { decoder.push(&buffer[..read]) };
        count += chunk.encode_utf16().count();
        if count > limit {
            return false;
        }
        if read == 0 {
            return true;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn relevant_variables_are_the_path_and_the_credential_families() {
        assert!(relevant("Path") && relevant("aws_profile") && relevant("ANTHROPIC_API_KEY") && relevant("GOOGLE_CLOUD_PROJECT"));
        assert!(!relevant("TEMP") && !relevant("PATHEXT"));
    }

    #[test]
    fn a_nonce_is_32_lowercase_hex_digits() {
        let nonce = new_nonce();
        assert!(valid_nonce(&nonce), "{nonce}");
        assert_ne!(nonce, new_nonce());
        assert!(command("0123456789ABCDEF0123456789abcdef").is_none());
        assert!(arguments(&nonce).is_some_and(|arguments| arguments.len() == 4 && arguments[2] == "-EncodedCommand"));
    }

    #[test]
    fn the_baseline_takes_the_persisted_path() {
        let baseline = baseline();
        assert!(baseline.get("PATH").is_some_and(|path| !path.is_empty()));
        assert!(!format!("{baseline:?}").contains('\\'), "names only");
    }
}
