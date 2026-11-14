//! Login recovery (C# `CliAuthFailure.Providers` / `SignInCanFix`, `CliAutoLoginGate`, `CliLoginOutputParser`, `CliLoginRetryBook`,
//! `CliBackgroundLogin`; CliLoginRecovery.cs:138-248, :363-397). The failure signals a run reads are mighty-run's (`failure`).
//!
//! The sign-in's output exists in memory only: it never enters a run log or a snapshot.

use std::collections::{BTreeMap, HashMap, HashSet};
use std::path::Path;
use std::sync::{Arc, Mutex, OnceLock};

use chrono::{DateTime, Utc};
use mighty_platform::{PtyControl, PtyHost, PtyRequest, PtySize};
use mighty_run::model::{CliCommand, StartRunRequest};
use regex::Regex;
use tokio::sync::watch;
use url::Url;

use crate::accounts::{CliAccountStatus, method};
use crate::lock;
use crate::terminal::CliTerminalSignIn;

/// `CliAuthFailure.Providers`: the providers whose lost sign-in raises the card and may start a sign-in.
pub const PROVIDERS: [&str; 6] = ["claude", "codex", "gemini", "pi", "opencode", "hermes"];

/// `CliAuthFailure.SignInCanFix`: whether signing in again can fix a run that failed with a sign-in error. A Codex API-key login
/// cannot, nor can configuration the app does not own (Bedrock, an API key: `access_verified` false). The terminal sign-ins'
/// status is a live call: only one that reads signed out asks for a sign-in; unknown (offline, a timeout) opens none.
pub fn sign_in_can_fix(status: &CliAccountStatus) -> bool {
    PROVIDERS.contains(&status.provider.as_str())
        && status.installed
        && status.access_verified != Some(false)
        && !(status.provider == "codex" && status.method_id.as_deref() == Some(method::CODEX_API_KEY))
        && (CliTerminalSignIn::get(&status.provider).is_none() || status.logged_in == Some(false))
}

/// C# `CliAutoLoginGate`: whether a lost sign-in starts its provider's background sign-in by itself. One per provider: none while
/// one already runs, and none for [`CliAutoLoginGate::COOLDOWN`] after one failed, was cancelled or succeeded, so neither a refused
/// sign-in nor a request failing again right after approval reopens the browser in a loop. A request login recovery itself resent,
/// or one sent before the last sign-in, only raises the card. The card's button is never held by this.
#[derive(Debug, Default, Clone)]
pub struct CliAutoLoginGate {
    held: HashMap<String, DateTime<Utc>>,
    signed_in: HashMap<String, DateTime<Utc>>,
}

impl CliAutoLoginGate {
    /// `Cooldown`: two minutes.
    pub const COOLDOWN: chrono::TimeDelta = chrono::TimeDelta::minutes(2);

    /// `ShouldStart`.
    #[allow(clippy::too_many_arguments)]
    pub fn should_start(
        &self,
        provider: &str,
        enabled: bool,
        status: &CliAccountStatus,
        login_active: bool,
        now: DateTime<Utc>,
        resent: bool,
        sent_at: Option<DateTime<Utc>>,
    ) -> bool {
        enabled
            && !login_active
            && !resent
            && PROVIDERS.contains(&provider)
            && status.provider == provider
            && sign_in_can_fix(status)
            && !sent_at.is_some_and(|sent| self.signed_in.get(provider).is_some_and(|confirmed| sent < *confirmed))
            && self.held.get(provider).is_none_or(|at| now - *at >= Self::COOLDOWN)
    }

    /// `Stopped`: a sign-in of the provider failed, timed out or was cancelled.
    pub fn stopped(&mut self, provider: &str, now: DateTime<Utc>) {
        self.held.insert(provider.to_string(), now);
    }

    /// `Succeeded`: signed in again (in the background, a terminal or Settings): automatic starts wait out the cooldown.
    pub fn succeeded(&mut self, provider: &str, now: DateTime<Utc>) {
        self.held.insert(provider.to_string(), now);
        self.signed_in.insert(provider.to_string(), now);
    }
}

/// `CliLoginOutput`: the sign-in link the CLI printed, and whether it asks for a pasted code.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CliLoginOutput {
    pub url: Option<Url>,
    pub asks_for_code: bool,
}

/// C# `CliLoginOutputParser`: the sign-in's output, at most 64 KiB of UTF-8, read for its link (the sign-in host's first, else the
/// first `https` link; one still being printed at the end of the output waits until more follows or the output ends) and for a
/// prompt to paste a code. In memory only.
#[derive(Debug, Default)]
pub struct CliLoginOutputParser {
    raw: String,
    bytes: usize,
    current: CliLoginOutput,
}

const AUTH_HOSTS: [&str; 5] = ["claude.ai", "claude.com", "console.anthropic.com", "auth.openai.com", "chatgpt.com"];

fn links() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| Regex::new("https://[^\\s\"'<>`\\x00-\\x1F\\x7F]+").expect("pattern"))
}

fn hyperlinks() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| Regex::new("\\x1b\\]8;[^;\\x07\\x1b]*;([^\\x07\\x1b]*)(?:\\x07|\\x1b\\\\)").expect("pattern"))
}

fn escapes() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| Regex::new("\\x1b(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07\\x1b]*(?:\\x07|\\x1b\\\\)|[@-Z\\\\-_])").expect("pattern"))
}

impl CliLoginOutputParser {
    /// `MaximumBytes`.
    pub const MAXIMUM_BYTES: usize = 65_536;

    pub fn new() -> CliLoginOutputParser {
        CliLoginOutputParser::default()
    }

    /// `Current`.
    pub fn current(&self) -> &CliLoginOutput {
        &self.current
    }

    /// `Append`: what fits of `value` under the byte cap, then the output read again.
    pub fn append(&mut self, value: &str) {
        for c in value.chars() {
            if self.bytes + c.len_utf8() > Self::MAXIMUM_BYTES {
                break;
            }
            self.raw.push(c);
            self.bytes += c.len_utf8();
        }
        self.parse(false);
    }

    /// `Finish`: the output ended, so a link at its end is whole.
    pub fn finish(&mut self) {
        self.parse(true);
    }

    /// `Clear`.
    pub fn clear(&mut self) {
        self.raw.clear();
        self.bytes = 0;
        self.current = CliLoginOutput::default();
    }

    /// `IsAuthUrl`: a sign-in host or one of its subdomains.
    pub fn is_auth_url(url: Option<&Url>) -> bool {
        let Some(host) = url.and_then(Url::host_str) else { return false };
        AUTH_HOSTS.iter().any(|auth| {
            host.eq_ignore_ascii_case(auth)
                || host.len() > auth.len() + 1
                    && host.is_char_boundary(host.len() - auth.len() - 1)
                    && host[host.len() - auth.len() - 1..].eq_ignore_ascii_case(&format!(".{auth}"))
        })
    }

    fn parse(&mut self, last: bool) {
        let linked = hyperlinks().replace_all(&self.raw, " ${1} ");
        let text = escapes().replace_all(&linked, "").replace('\r', "\n");
        let mut url = self.current.url.clone();
        if !Self::is_auth_url(url.as_ref()) {
            let mut first: Option<Url> = None;
            for found in links().find_iter(&text) {
                if !last && found.end() == text.len() {
                    continue;
                }
                let link = found.as_str().trim_end_matches(['.', ',', ';', ':', '!', '?', ')', ']', '}', '>']);
                if link.len() > 8192 {
                    continue;
                }
                let Ok(candidate) = Url::parse(link) else { continue };
                if candidate.scheme() != "https"
                    || candidate.host_str().is_none_or(str::is_empty)
                    || !candidate.username().is_empty()
                    || candidate.password().is_some()
                {
                    continue;
                }
                let auth = Self::is_auth_url(Some(&candidate));
                if first.is_none() || auth {
                    first = Some(candidate);
                }
                if auth {
                    break;
                }
            }
            url = first.or(url);
        }
        let asks = self.current.asks_for_code
            || text.split('\n').any(|line| {
                let lower = line.to_lowercase();
                lower.contains("paste") && lower.contains("code")
            });
        self.current = CliLoginOutput { url, asks_for_code: asks };
    }
}

/// `CliLoginRetry`: a request that lost its sign-in, kept for its retry. `resent`: login recovery itself sent it again after a
/// sign-in; `sent_at`: when its run started.
#[derive(Debug, Clone)]
pub struct CliLoginRetry {
    pub request: StartRunRequest,
    pub generation: i64,
    pub resent: bool,
    pub sent_at: Option<DateTime<Utc>>,
}

impl PartialEq for CliLoginRetry {
    /// C#'s record equality, as the deferred start compares a pane's retry: the same generation of the same pane.
    fn eq(&self, other: &Self) -> bool {
        self.request.session_id == other.request.session_id
            && self.generation == other.generation
            && self.resent == other.resent
            && self.sent_at == other.sent_at
    }
}

/// C# `CliLoginRetryBook`: each pane's last request of a provider with login recovery, by generation. A new submission
/// invalidates both its old retry and any pending status check.
#[derive(Debug, Default)]
pub struct CliLoginRetryBook {
    serial: i64,
    generations: HashMap<String, i64>,
    inflight: HashMap<String, CliLoginRetry>,
    retries: HashMap<String, CliLoginRetry>,
    resending: HashSet<String>,
}

impl CliLoginRetryBook {
    pub fn new() -> CliLoginRetryBook {
        CliLoginRetryBook::default()
    }

    /// `Requests`: the panes waiting on a sign-in, each with its request.
    pub fn requests(&self) -> &HashMap<String, CliLoginRetry> {
        &self.retries
    }

    /// `ExpectResend`: the pane's next request is login recovery's own resend (reported back later through [`sent`](Self::sent)).
    pub fn expect_resend(&mut self, session: &str) {
        self.resending.insert(session.to_string());
    }

    /// `CancelResend`.
    pub fn cancel_resend(&mut self, session: &str) {
        self.resending.remove(session);
    }

    /// `Sent`: a request started (`now`, by default the clock's now); its generation.
    pub fn sent(&mut self, request: &StartRunRequest, now: Option<DateTime<Utc>>) -> i64 {
        self.serial += 1;
        let generation = self.serial;
        let session = request.session_id.clone();
        self.generations.insert(session.clone(), generation);
        self.retries.remove(&session);
        self.inflight.remove(&session);
        let resent = self.resending.remove(&session);
        if request.kind == "claude" && PROVIDERS.contains(&request.provider.as_str()) {
            let sent_at = Some(now.unwrap_or_else(mighty_core::time::clock_now));
            self.inflight.insert(session, CliLoginRetry { request: request.clone(), generation, resent, sent_at });
        }
        generation
    }

    /// `Settled`: the pane's run ended; its request, once.
    pub fn settled(&mut self, session: &str) -> Option<CliLoginRetry> {
        self.inflight.remove(session)
    }

    /// `IsCurrent`: no newer request of the pane was sent since.
    pub fn is_current(&self, retry: &CliLoginRetry) -> bool {
        self.generations.get(&retry.request.session_id).copied().unwrap_or_default() == retry.generation
    }

    /// `Remember`: kept for its retry, when still current.
    pub fn remember(&mut self, retry: CliLoginRetry) -> bool {
        if !self.is_current(&retry) {
            return false;
        }
        self.retries.insert(retry.request.session_id.clone(), retry);
        true
    }

    /// `Take`: the pane's retry, once, when still current.
    pub fn take(&mut self, session: &str) -> Option<CliLoginRetry> {
        let retry = self.retries.remove(session)?;
        self.is_current(&retry).then_some(retry)
    }

    /// `Drop`: a dismissed or closed pane, which no late status brings back.
    pub fn drop_session(&mut self, session: &str) {
        self.retries.remove(session);
        self.inflight.remove(session);
        self.generations.remove(session);
        self.resending.remove(session);
    }

    /// `Clear`.
    pub fn clear(&mut self) {
        self.retries.clear();
        self.inflight.clear();
        self.generations.clear();
        self.resending.clear();
    }
}

/// UTF-8 split across the terminal's chunks, decoded as it completes (invalid bytes read as U+FFFD).
#[derive(Default)]
struct Utf8Chunks(Vec<u8>);

impl Utf8Chunks {
    fn push(&mut self, bytes: &[u8]) -> String {
        self.0.extend_from_slice(bytes);
        let mut out = String::new();
        loop {
            match std::str::from_utf8(&self.0) {
                Ok(text) => {
                    out.push_str(text);
                    self.0.clear();
                    return out;
                }
                Err(error) => {
                    let valid = error.valid_up_to();
                    out.push_str(std::str::from_utf8(&self.0[..valid]).unwrap_or_default());
                    match error.error_len() {
                        Some(bad) => {
                            out.push('\u{FFFD}');
                            self.0.drain(..valid + bad);
                        }
                        None => {
                            self.0.drain(..valid);
                            return out;
                        }
                    }
                }
            }
        }
    }
}

/// C# `CliBackgroundLogin`: the CLI's own sign-in (`claude auth login`, `codex login`) in a hidden pseudo terminal 1000 columns
/// wide, which keeps its browser-and-code flow working; its output is read for the link and the code prompt
/// ([`CliLoginOutputParser`]) and never kept. Each change of what it read reaches [`changes`](Self::changes).
pub struct CliBackgroundLogin {
    parser: Arc<Mutex<CliLoginOutputParser>>,
    control: Arc<dyn PtyControl>,
    changed: watch::Sender<CliLoginOutput>,
    /// None while it runs; then its exit code, or None when its end could not be read.
    exit: watch::Receiver<Option<Option<i32>>>,
}

impl std::fmt::Debug for CliBackgroundLogin {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CliBackgroundLogin").field("running", &self.is_running()).finish_non_exhaustive()
    }
}

impl CliBackgroundLogin {
    /// Starts `command` with `arguments` in `directory` with the CLI environment `environment` (the child's whole environment).
    pub async fn start(
        ptys: &dyn PtyHost,
        command: &CliCommand,
        arguments: &[String],
        directory: &Path,
        environment: BTreeMap<String, String>,
    ) -> mighty_platform::Result<CliBackgroundLogin> {
        let mut args = command.prefix.clone();
        args.extend(arguments.iter().cloned());
        let request = PtyRequest {
            program: command.binary.clone().into(),
            args,
            cwd: directory.to_path_buf(),
            env: environment,
            size: PtySize { columns: 1000, ..PtySize::default() },
        };
        let mut session = ptys.open(request).await?;
        let parser = Arc::new(Mutex::new(CliLoginOutputParser::new()));
        let (changed, _) = watch::channel(CliLoginOutput::default());
        let (ended, exit) = watch::channel(None);
        let control = session.control.clone();
        let reading = (parser.clone(), changed.clone());
        tokio::spawn(async move {
            let (parser, changed) = reading;
            let mut decoder = Utf8Chunks::default();
            while let Some(bytes) = session.output.recv().await {
                let text = decoder.push(&bytes);
                let (before, after) = {
                    let mut parser = lock(&parser);
                    let before = parser.current().clone();
                    parser.append(&text);
                    (before, parser.current().clone())
                };
                if before != after {
                    changed.send_replace(after);
                }
            }
        });
        let waiting = control.clone();
        tokio::spawn(async move {
            let code = waiting.wait().await.ok();
            ended.send_replace(Some(code));
        });
        Ok(CliBackgroundLogin { parser, control, changed, exit })
    }

    /// `Output`.
    pub fn output(&self) -> CliLoginOutput {
        lock(&self.parser).current().clone()
    }

    /// `Changed`: each new reading of the output.
    pub fn changes(&self) -> watch::Receiver<CliLoginOutput> {
        self.changed.subscribe()
    }

    /// `IsRunning`.
    pub fn is_running(&self) -> bool {
        self.exit.borrow().is_none()
    }

    /// `ExitCode`: once it ended by itself.
    pub fn exit_code(&self) -> Option<i32> {
        self.exit.borrow().flatten()
    }

    /// `Completion`: its end (None when its end could not be read).
    pub async fn completion(&self) -> Option<i32> {
        let mut exit = self.exit.clone();
        match exit.wait_for(Option::is_some).await {
            Ok(code) => code.flatten(),
            Err(_) => None,
        }
    }

    /// `FinishOutput`: the output ended, so a link at its end is whole; the reading is announced either way.
    pub fn finish_output(&self) {
        let value = {
            let mut parser = lock(&self.parser);
            parser.finish();
            parser.current().clone()
        };
        self.changed.send_replace(value);
    }

    /// `SendCode`: the pasted code and Enter, when it is one line of at most 4 KiB without control characters.
    pub fn send_code(&self, code: &str) -> bool {
        let clean = code.trim();
        if clean.is_empty() || clean.len() > 4096 || clean.chars().any(char::is_control) {
            return false;
        }
        self.control.write(format!("{clean}\r").as_bytes())
    }

    /// `DisposeAsync`: the terminal closed with everything in it, and what was read forgotten.
    pub async fn close(&self) {
        let _ = self.control.close().await;
        lock(&self.parser).clear();
    }
}
