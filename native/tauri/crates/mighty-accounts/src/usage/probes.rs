//! The three account probes (C# `ClaudeAccountProbe`, `CodexAccountProbe`, `AntigravityAccountProbe`, AccountUsage.cs:168-502) and
//! the probe the running app gives the service (C# `AccountUsageRuntime.Probe`, AccountUsageRuntime.cs).

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, OnceLock};
use std::time::Duration;

use chrono::{DateTime, Utc};
use mighty_platform::process::{ChildReader, ChildWriter};
use mighty_platform::{BoxFuture, ChildControl, ProcessHost};
use mighty_run::model::CliCommand;
use mighty_run::{meta, strings};
use serde_json::Value;
use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader, Lines};

use super::reset::SHARED_DEADLINE;
use super::{
    AccountUsageFailure, AccountUsageFailureKind, AccountUsageHttpResponse, AccountUsageShapeLog, AccountUsageSnapshot, AccountUsageWindow,
    ClaudeQuotaCredential, ClaudeResetEntitlements, Guarded, HttpFailure, HttpsGet, HttpsRequest, MAXIMUM_BODY_BYTES, ResetReadDeadlineBox,
    UsageError, UsageProbe, copy, credential_file, endpoint, number, reset, retry_interval, send, text,
};

fn failure(kind: AccountUsageFailureKind, detail: String) -> UsageError {
    UsageError::Failure(AccountUsageFailure::new(kind, detail))
}

fn network() -> UsageError {
    UsageError::Failure(AccountUsageFailure::refresh_failed(AccountUsageFailureKind::Network))
}

fn invalid() -> UsageError {
    UsageError::Failure(AccountUsageFailure::refresh_failed(AccountUsageFailureKind::InvalidResponse))
}

// ── Claude ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `ClaudeAccountProbe`: the direct Claude lookup. Only `https://api.anthropic.com` is ever asked, a redirect is refused, the
/// timeout is 10 seconds and there is one attempt.
pub struct ClaudeAccountProbe;

/// `AccountUsageShapeLog.Shared`: the app's one log (its lines reach no sink until the host gives one of its own).
fn shared_shape_log() -> &'static AccountUsageShapeLog {
    static SHARED: OnceLock<AccountUsageShapeLog> = OnceLock::new();
    SHARED.get_or_init(|| AccountUsageShapeLog::new(|_| {}))
}

impl ClaudeAccountProbe {
    pub const HOST: &'static str = super::HOST;
    pub const TIMEOUT: Duration = Duration::from_secs(10);
    /// The entitlement reads get the CLI's own 5 second budget.
    pub const RESET_TIMEOUT: Duration = Duration::from_secs(5);
    /// `BlockedEnvironment`: any of these means a custom or non-subscription sign-in; the production endpoint must never see it.
    pub const BLOCKED_ENVIRONMENT: [&'static str; 10] = [
        "CLAUDE_CODE_CUSTOM_OAUTH_URL",
        "CLAUDE_LOCAL_OAUTH_API_BASE",
        "USE_LOCAL_OAUTH",
        "USE_STAGING_OAUTH",
        "ANTHROPIC_BASE_URL",
        "CLAUDE_CODE_OAUTH_TOKEN",
        "ANTHROPIC_API_KEY",
        "CLAUDE_CODE_USE_BEDROCK",
        "CLAUDE_CODE_USE_VERTEX",
        "CLAUDE_CODE_USE_FOUNDRY",
    ];

    /// `CustomAuthentication`.
    pub fn custom_authentication(environment: &BTreeMap<String, String>) -> bool {
        Self::BLOCKED_ENVIRONMENT.iter().any(|key| {
            crate::env_get(environment, key)
                .is_some_and(|value| !value.is_empty() && !matches!(value.to_lowercase().as_str(), "0" | "false"))
        })
    }

    /// `CheckResponse`: 401/403 is the sign-in; 429 a back-off; a redirect is refused, never followed; anything else that is not a
    /// 2xx with a body under 1 MiB failed.
    pub fn check_response(response: &AccountUsageHttpResponse, now: DateTime<Utc>) -> Result<(), AccountUsageFailure> {
        match response.status {
            401 | 403 => return Err(AccountUsageFailure::new(AccountUsageFailureKind::Authentication, copy::detail_authentication())),
            429 => {
                return Err(AccountUsageFailure {
                    kind: AccountUsageFailureKind::RateLimited,
                    detail: copy::detail_rate_limited(),
                    retry_after_seconds: retry_interval(response.retry_after.as_deref(), now),
                });
            }
            _ => {}
        }
        if (300..400).contains(&response.status)
            || response.redirect_location.is_some()
            || !(200..300).contains(&response.status)
            || response.body.len() > MAXIMUM_BODY_BYTES
        {
            return Err(AccountUsageFailure::refresh_failed(AccountUsageFailureKind::Network));
        }
        Ok(())
    }

    /// `Map`: the five-hour, seven-day and seven-day Sonnet windows (an unusable utilization is dropped, never shown as 0%), the
    /// profile's e-mail and rate-limit tier (else the credential's plan).
    pub fn map(body: &Value, profile: &Value, plan: Option<&str>) -> AccountUsageSnapshot {
        let mut windows = Vec::new();
        for (key, kind, minutes) in
            [("five_hour", "session", 300), ("seven_day", "weekly", 10080), ("seven_day_sonnet", "seven_day_Sonnet", 10080)]
        {
            let Some(row) = meta::prop(body, key).filter(|r| r.is_object()) else { continue };
            let Some(used) = number(row, "utilization").filter(|u| (0.0..=100.0).contains(u)) else { continue };
            windows.push(AccountUsageWindow {
                kind: kind.into(),
                used_percent: used,
                resets_at: reset(meta::prop(row, "resets_at")),
                window_minutes: Some(minutes),
            });
        }
        let account = meta::prop(profile, "account").cloned().unwrap_or(Value::Null);
        let organization = meta::prop(profile, "organization").cloned().unwrap_or(Value::Null);
        let empty = windows.is_empty();
        AccountUsageSnapshot {
            account_label: text(meta::text(&account, "email")),
            plan: text(meta::text(&organization, "rate_limit_tier")).or_else(|| text(plan)),
            windows,
            status: if empty { "unavailable" } else { "available" }.into(),
            detail: if empty { copy::detail_claude_no_windows() } else { copy::detail_claude() },
            ..AccountUsageSnapshot::new("claude")
        }
    }

    /// `ReadAsync`: a custom sign-in is refused before the credential is loaded; then the quota, the profile (a failure of it
    /// leaves the profile out, a 429 sets the retry hint) and the 리셋권 rows, which never change the windows. The token exists only
    /// inside this call, in one header of each request, and is never kept, logged or returned.
    pub async fn read(
        environment: &BTreeMap<String, String>,
        load: impl FnOnce() -> Option<ClaudeQuotaCredential>,
        http: &dyn HttpsGet,
        clock: &(dyn Fn() -> DateTime<Utc> + Send + Sync),
        reset_deadline: Option<&ResetReadDeadlineBox>,
        shape_log: Option<&AccountUsageShapeLog>,
    ) -> Result<AccountUsageSnapshot, UsageError> {
        if Self::custom_authentication(environment) {
            return Err(failure(AccountUsageFailureKind::Unavailable, copy::detail_custom_authentication()));
        }
        let credential = load().ok_or_else(|| failure(AccountUsageFailureKind::Authentication, copy::detail_authentication()))?;
        let request = |path: &str, query: Option<&str>, timeout: Duration| -> Result<HttpsRequest, AccountUsageFailure> {
            Ok(HttpsRequest {
                url: endpoint(path, query)?.to_string(),
                headers: vec![
                    ("Authorization".into(), format!("Bearer {}", credential.token)),
                    ("Accept".into(), "application/json".into()),
                    ("anthropic-beta".into(), "oauth-2025-04-20".into()),
                ],
                timeout,
            })
        };
        let usage = send(http, request("usage", None, Self::TIMEOUT)?).await.map_err(|error| match error {
            HttpFailure::Refused(refused) => UsageError::Failure(refused),
            HttpFailure::Transport(why) => UsageError::Other(why),
        })?;
        Self::check_response(&usage, clock())?;
        let body = match serde_json::from_str::<Value>(&usage.body) {
            Ok(body @ Value::Object(_)) => body,
            _ => return Err(invalid()),
        };
        let mut profile = Value::Null;
        let mut retry_after = None;
        match send(http, request("profile", None, Self::TIMEOUT)?).await {
            Ok(reply) if reply.status == 429 => retry_after = Some(retry_interval(reply.retry_after.as_deref(), clock())),
            Ok(reply) if (200..300).contains(&reply.status) && reply.redirect_location.is_none() => {
                profile = serde_json::from_str(&reply.body).unwrap_or(Value::Null);
            }
            Ok(_) | Err(HttpFailure::Refused(_)) => {}
            Err(HttpFailure::Transport(why)) => return Err(UsageError::Other(why)),
        }
        let shape_log: &AccountUsageShapeLog = match shape_log {
            Some(log) => log,
            None => shared_shape_log(),
        };
        let resets = ClaudeResetEntitlements::read(
            |query| request("usage", Some(query), Self::RESET_TIMEOUT),
            http,
            clock,
            Some(reset_deadline.unwrap_or(&SHARED_DEADLINE)),
            Some(shape_log),
        )
        .await?;
        Ok(AccountUsageSnapshot { retry_after_seconds: retry_after, resets, ..Self::map(&body, &profile, credential.plan.as_deref()) })
    }
}

// ── Codex ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `IAccountUsageStdio`: one line-delimited JSON-RPC channel to `codex app-server`. The app never sees Codex's credentials; the
/// CLI owns them.
pub trait AccountUsageStdio: Send {
    fn write_line<'a>(&'a mut self, line: &'a str) -> BoxFuture<'a, Result<(), String>>;
    /// The next line, or None once the output ended.
    fn read_line(&mut self) -> BoxFuture<'_, Result<Option<String>, String>>;
    /// The channel closed and its process ended.
    fn close(&mut self) -> BoxFuture<'_, ()>;
}

/// C# `AccountUsageRuntime.CodexStdio`: `codex app-server --listen stdio://` over the platform's `ProcessHost`; its standard error
/// is read and dropped, and the child ends with the channel.
pub struct HostCodexStdio {
    control: Arc<dyn ChildControl>,
    input: Option<ChildWriter>,
    lines: Option<Lines<BufReader<ChildReader>>>,
}

impl HostCodexStdio {
    pub async fn start(
        processes: &dyn ProcessHost,
        command: &CliCommand,
        working_directory: &Path,
    ) -> mighty_platform::Result<HostCodexStdio> {
        let mut args = command.prefix.clone();
        args.extend(["app-server".to_string(), "--listen".to_string(), "stdio://".to_string()]);
        let request = processes.cli_request(Path::new(&command.binary), &args, working_directory, &BTreeMap::new())?;
        let mut child = processes.spawn(request).await?;
        if let Some(mut errors) = child.stderr.take() {
            tokio::spawn(async move {
                let mut buffer = [0u8; 4096];
                while matches!(errors.read(&mut buffer).await, Ok(read) if read > 0) {}
            });
        }
        Ok(HostCodexStdio {
            control: child.control(),
            input: child.stdin.take(),
            lines: child.stdout.take().map(|output| BufReader::new(output).lines()),
        })
    }
}

impl AccountUsageStdio for HostCodexStdio {
    fn write_line<'a>(&'a mut self, line: &'a str) -> BoxFuture<'a, Result<(), String>> {
        Box::pin(async move {
            let Some(input) = self.input.as_mut() else { return Ok(()) };
            input.write_all(format!("{line}\n").as_bytes()).await.map_err(|e| e.to_string())?;
            input.flush().await.map_err(|e| e.to_string())
        })
    }

    fn read_line(&mut self) -> BoxFuture<'_, Result<Option<String>, String>> {
        Box::pin(async move {
            match self.lines.as_mut() {
                None => Ok(None),
                Some(lines) => lines.next_line().await.map_err(|e| e.to_string()),
            }
        })
    }

    fn close(&mut self) -> BoxFuture<'_, ()> {
        Box::pin(async move {
            self.input = None;
            self.lines = None;
            self.control.kill();
            let _ = self.control.close().await;
        })
    }
}

/// C# `CodexAccountProbe`: asks the installed Codex CLI's own app-server. It never starts or resumes a thread and never calls a
/// tool.
pub struct CodexAccountProbe;

impl CodexAccountProbe {
    pub const TIMEOUT: Duration = Duration::from_secs(15);

    /// `Map`: a ChatGPT account's `codex` bucket (else `rateLimits`), its primary and secondary windows; a reported nonstandard
    /// period is kept, never relabelled as a week.
    pub fn map(account: &Value, limits: &Value) -> Result<AccountUsageSnapshot, AccountUsageFailure> {
        let info = meta::prop(account, "account").filter(|i| i.is_object() && meta::text(i, "type") == Some("chatgpt"));
        let Some(info) = info else {
            return Err(AccountUsageFailure::new(AccountUsageFailureKind::Unavailable, copy::detail_codex_needs_chat_gpt()));
        };
        let primary = meta::prop(limits, "rateLimitsByLimitId")
            .and_then(|buckets| meta::prop(buckets, "codex"))
            .filter(|p| p.is_object())
            .or_else(|| meta::prop(limits, "rateLimits").filter(|p| p.is_object()));
        let Some(primary) = primary else { return Err(AccountUsageFailure::refresh_failed(AccountUsageFailureKind::InvalidResponse)) };
        let mut windows = Vec::new();
        for (key, fallback) in [("primary", "session"), ("secondary", "weekly")] {
            let Some(row) = meta::prop(primary, key).filter(|r| r.is_object()) else { continue };
            let Some(used) = number(row, "usedPercent").filter(|u| (0.0..=100.0).contains(u)) else { continue };
            let minutes = number(row, "windowDurationMins").filter(|v| *v > 0.0 && *v <= 525_600.0 && v.round() == *v).map(|v| v as i32);
            let kind = match minutes {
                None => fallback.to_string(),
                Some(10080) => "weekly".into(),
                Some(300) => "session".into(),
                Some(other) => format!("{other}m"),
            };
            windows.push(AccountUsageWindow {
                kind,
                used_percent: used,
                resets_at: reset(meta::prop(row, "resetsAt")),
                window_minutes: minutes,
            });
        }
        let empty = windows.is_empty();
        Ok(AccountUsageSnapshot {
            account_label: text(meta::text(info, "email")),
            plan: text(meta::text(primary, "planType")).or_else(|| text(meta::text(info, "planType"))),
            windows,
            status: if empty { "unavailable" } else { "available" }.into(),
            detail: if empty { copy::detail_codex_no_windows() } else { copy::detail_codex() },
            ..AccountUsageSnapshot::new("codex")
        })
    }

    /// `ReadAsync`: `initialize` → `initialized` → `account/read` (no token refresh) → `account/rateLimits/read`, all within
    /// `timeout`; the channel is closed whatever happened.
    pub async fn read(mut channel: Box<dyn AccountUsageStdio>, timeout: Duration) -> Result<AccountUsageSnapshot, UsageError> {
        let outcome = match tokio::time::timeout(timeout, Self::exchange(channel.as_mut())).await {
            Ok(outcome) => outcome,
            Err(_) => Err(network()),
        };
        channel.close().await;
        outcome
    }

    async fn exchange(channel: &mut dyn AccountUsageStdio) -> Result<AccountUsageSnapshot, UsageError> {
        const INITIALIZE: &str =
            r#"{"id":1,"method":"initialize","params":{"clientInfo":{"name":"mightyclaude_account_usage","version":"0.1.0"}}}"#;
        channel.write_line(INITIALIZE).await.map_err(UsageError::Other)?;
        let mut expected = 1;
        let mut account = Value::Null;
        let mut read = 0usize;
        loop {
            let line = channel.read_line().await.map_err(|_| network())?.ok_or_else(network)?;
            read += line.len();
            if read > MAXIMUM_BODY_BYTES {
                return Err(invalid());
            }
            let Ok(message) = serde_json::from_str::<Value>(&line) else { continue };
            if meta::integer(&message, "id") != Some(expected) {
                continue;
            }
            if let Some(error) = meta::prop(&message, "error").filter(|e| e.is_object()) {
                let limited = number(error, "code") == Some(429.0) || meta::text(error, "message").unwrap_or("").contains("429");
                return Err(if limited {
                    UsageError::Failure(AccountUsageFailure {
                        kind: AccountUsageFailureKind::RateLimited,
                        detail: copy::detail_rate_limited(),
                        retry_after_seconds: super::DEFAULT_BACKOFF_SECONDS,
                    })
                } else {
                    network()
                });
            }
            let Some(result) = meta::prop(&message, "result").filter(|r| r.is_object()) else { return Err(invalid()) };
            match expected {
                1 => {
                    channel.write_line(r#"{"method":"initialized"}"#).await.map_err(UsageError::Other)?;
                    expected = 2;
                    channel
                        .write_line(r#"{"id":2,"method":"account/read","params":{"refreshToken":false}}"#)
                        .await
                        .map_err(UsageError::Other)?;
                }
                2 => {
                    account = result.clone();
                    if meta::prop(result, "account").and_then(|a| meta::text(a, "type")) != Some("chatgpt") {
                        return Err(failure(AccountUsageFailureKind::Unavailable, copy::detail_codex_needs_chat_gpt()));
                    }
                    expected = 3;
                    channel.write_line(r#"{"id":3,"method":"account/rateLimits/read"}"#).await.map_err(UsageError::Other)?;
                }
                _ => return Self::map(&account, result).map_err(UsageError::Failure),
            }
        }
    }
}

// ── Antigravity ───────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `AntigravityAccountProbe`: Antigravity's own read of its quota, `agy -p /usage --output-format json`, answered locally
/// without a model call. It runs in the temporary folder so no project's configuration is read, with the CLI's own sign-in; the
/// output is parsed and dropped, never logged.
pub struct AntigravityAccountProbe;

/// `ProviderCatalog.ReadBoundedAsync`: a pipe's text, failing past `limit` characters.
async fn read_bounded(reader: Option<ChildReader>, limit: usize) -> Result<String, ()> {
    let Some(mut reader) = reader else { return Ok(String::new()) };
    let mut bytes = Vec::new();
    let mut buffer = [0u8; 8192];
    loop {
        let read = reader.read(&mut buffer).await.map_err(|_| ())?;
        if read == 0 {
            break;
        }
        bytes.extend_from_slice(&buffer[..read]);
        if bytes.len() > limit.saturating_mul(4) {
            return Err(());
        }
    }
    let text = String::from_utf8_lossy(&bytes).into_owned();
    if text.encode_utf16().count() > limit { Err(()) } else { Ok(text) }
}

fn prefix_chars(text: &str, count: usize) -> &str {
    text.char_indices().nth(count).map_or(text, |(at, _)| &text[..at])
}

impl AntigravityAccountProbe {
    pub const TIMEOUT: Duration = Duration::from_secs(20);
    pub const ARGUMENTS: [&'static str; 4] = ["-p", "/usage", "--output-format", "json"];

    /// `ReadAsync`: agy started in the temporary folder with `NO_COLOR` and `CI`, its input closed, both outputs read (1 MiB each)
    /// within `timeout` (20 s); what it said on standard error is read once it closed it or a second passed. A start, read or wait
    /// that fails is a network failure.
    pub async fn read(
        processes: &dyn ProcessHost,
        command: &CliCommand,
        environment: &BTreeMap<String, String>,
        timeout: Option<Duration>,
    ) -> Result<AccountUsageSnapshot, UsageError> {
        let mut quiet = environment.clone();
        quiet.insert("NO_COLOR".into(), "1".into());
        quiet.insert("CI".into(), "1".into());
        let mut args = command.prefix.clone();
        args.extend(Self::ARGUMENTS.iter().map(|a| a.to_string()));
        let request = processes.cli_request(Path::new(&command.binary), &args, &std::env::temp_dir(), &quiet).map_err(|_| network())?;
        let mut child = processes.spawn(request).await.map_err(|_| network())?;
        let control = child.control();
        drop(child.stdin.take());
        let errors = tokio::spawn(read_bounded(child.stderr.take(), MAXIMUM_BODY_BYTES));
        let output = child.stdout.take();
        let waiting = control.clone();
        let work = async move {
            let stdout = read_bounded(output, MAXIMUM_BODY_BYTES).await?;
            waiting.wait().await.map_err(|_| ())?;
            Ok::<String, ()>(stdout)
        };
        let stdout = tokio::time::timeout(timeout.unwrap_or(Self::TIMEOUT), work).await;
        let stdout = match stdout {
            Ok(Ok(stdout)) => stdout,
            _ => {
                control.kill();
                let _ = control.close().await;
                return Err(network());
            }
        };
        let stderr = match tokio::time::timeout(Duration::from_secs(1), errors).await {
            Ok(Ok(Ok(text))) => text,
            _ => String::new(),
        };
        let _ = control.close().await;
        Self::map(&stdout, &stderr).map_err(UsageError::Failure)
    }

    /// `Map`: each agy group ("Gemini Models", "Claude and GPT models") has its own 5-hour and weekly bucket; a window's kind is
    /// `<window>@<group>`. A signed-out agy is a sign-in to check; anything that is not its reply is an invalid response.
    pub fn map(stdout: &str, stderr: &str) -> Result<AccountUsageSnapshot, AccountUsageFailure> {
        let Some(groups) = Self::groups(stdout) else {
            let said = format!("{}{}", prefix_chars(stdout, 65_536), prefix_chars(stderr, 65_536));
            if mighty_run::failure::antigravity(&said) {
                return Err(AccountUsageFailure::new(AccountUsageFailureKind::Authentication, copy::detail_authentication()));
            }
            return Err(AccountUsageFailure::refresh_failed(AccountUsageFailureKind::InvalidResponse));
        };
        let mut windows: Vec<AccountUsageWindow> = Vec::new();
        for group in groups.iter().take(16) {
            let Some(Value::Array(buckets)) = meta::prop(group, "buckets") else { continue };
            let name = text(meta::text(group, "name"));
            for bucket in buckets.iter().take(16) {
                let Some(remaining) = number(bucket, "remaining_fraction").filter(|r| (-0.001..=1.001).contains(r)) else { continue };
                let Some(token) = Self::group_token(name.as_deref(), text(meta::text(bucket, "id")).as_deref()) else { continue };
                let (window, minutes) = match text(meta::text(bucket, "window")).map(|w| w.to_lowercase()) {
                    Some(w) if w == "5h" => ("session".to_string(), Some(300)),
                    Some(w) if w == "weekly" => ("weekly".to_string(), Some(10080)),
                    Some(w) if w == "daily" => ("daily".to_string(), Some(1440)),
                    Some(other) => (other.chars().filter(|c| c.is_alphanumeric() || *c == '_').collect(), None),
                    None => (String::new(), None),
                };
                if window.is_empty() {
                    continue;
                }
                let kind = format!("{window}@{token}");
                if windows.iter().any(|w| w.kind == kind) {
                    continue;
                }
                windows.push(AccountUsageWindow {
                    kind,
                    used_percent: ((1.0 - remaining) * 100.0).clamp(0.0, 100.0),
                    resets_at: reset(meta::prop(bucket, "reset_time")),
                    window_minutes: minutes,
                });
            }
        }
        let empty = windows.is_empty();
        Ok(AccountUsageSnapshot {
            windows,
            status: if empty { "unavailable" } else { "available" }.into(),
            detail: if empty { copy::detail_antigravity_no_windows() } else { copy::detail_antigravity() },
            ..AccountUsageSnapshot::new("gemini")
        })
    }

    /// `Groups`: `command.data.groups` of a successful `/usage` reply (a line of noise around it is allowed), or None.
    fn groups(text: &str) -> Option<Vec<Value>> {
        if text.len() > MAXIMUM_BODY_BYTES {
            return None;
        }
        let root = serde_json::from_str::<Value>(text).ok().or_else(|| {
            let (start, end) = (text.find('{')?, text.rfind('}')?);
            (start < end).then(|| serde_json::from_str::<Value>(&text[start..=end]).ok()).flatten()
        })?;
        if !root.is_object() || meta::text(&root, "status").is_some_and(|status| !status.eq_ignore_ascii_case("SUCCESS")) {
            return None;
        }
        let groups = meta::prop(&root, "command")
            .filter(|c| c.is_object())
            .and_then(|command| meta::prop(command, "data"))
            .filter(|d| d.is_object())
            .and_then(|data| meta::prop(data, "groups"))
            .and_then(Value::as_array)?;
        Some(groups.iter().filter(|group| group.is_object()).cloned().collect())
    }

    /// `GroupToken`: the known groups by name or bucket id; any other keeps agy's own group name (no `@`, at most 40 characters).
    fn group_token(name: Option<&str>, bucket_id: Option<&str>) -> Option<String> {
        let lowered = name.unwrap_or("").to_lowercase();
        let id = bucket_id.unwrap_or("").to_lowercase();
        if lowered.starts_with("gemini") || id.starts_with("gemini-") {
            return Some("gemini".into());
        }
        if lowered.contains("claude") && lowered.contains("gpt") || id.starts_with("3p-") {
            return Some("claude_gpt".into());
        }
        name.map(|name| prefix_chars(&name.replace('@', " "), 40).to_string())
    }
}

// ── the app's probe ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// How the app finds a provider's CLI (`ProviderCatalog.FindAsync`).
pub type Finder = Arc<dyn Fn(String) -> BoxFuture<'static, Option<CliCommand>> + Send + Sync>;

/// C# `AccountUsageRuntime.Probe`: what the running app gives [`super::AccountUsageService`]. Claude is reached only when the caller
/// already checked the saved switch, through `http` wrapped in the guard (the app's: WinHTTP, redirects off); Codex through its
/// app-server, Antigravity through `agy -p /usage`. pi, opencode and Hermes have no account usage to read (each request's tokens are
/// in the pane's usage), so no CLI is looked for.
pub struct RuntimeProbe {
    pub find: Finder,
    pub environment: Arc<dyn Fn() -> BTreeMap<String, String> + Send + Sync>,
    pub home: Arc<dyn Fn() -> String + Send + Sync>,
    pub working_directory: PathBuf,
    pub processes: Arc<dyn ProcessHost>,
    pub http: Arc<dyn HttpsGet>,
}

/// [`RuntimeProbe`] as the service's probe.
pub fn runtime_probe(parts: RuntimeProbe) -> UsageProbe {
    let parts = Arc::new(parts);
    Arc::new(move |provider: String| -> BoxFuture<'static, Result<AccountUsageSnapshot, UsageError>> {
        let parts = parts.clone();
        Box::pin(async move {
            match provider.as_str() {
                "claude" => {
                    let environment = (parts.environment)();
                    let home = (parts.home)();
                    let http = Guarded(parts.http.clone());
                    ClaudeAccountProbe::read(
                        &environment,
                        || credential_file::read(&home, &environment, mighty_core::time::clock_now()),
                        &http,
                        &mighty_core::time::clock_now,
                        None,
                        None,
                    )
                    .await
                }
                "codex" => {
                    let Some(command) = (parts.find)("codex".into()).await else {
                        return Err(failure(AccountUsageFailureKind::Unavailable, copy::detail_codex_not_installed()));
                    };
                    let channel = HostCodexStdio::start(parts.processes.as_ref(), &command, &parts.working_directory)
                        .await
                        .map_err(|error| UsageError::Other(error.to_string()))?;
                    CodexAccountProbe::read(Box::new(channel), CodexAccountProbe::TIMEOUT).await
                }
                "gemini" => {
                    let Some(command) = (parts.find)("gemini".into()).await else {
                        return Err(failure(AccountUsageFailureKind::Unavailable, copy::detail_antigravity_not_installed()));
                    };
                    AntigravityAccountProbe::read(parts.processes.as_ref(), &command, &(parts.environment)(), None).await
                }
                "pi" => Err(failure(AccountUsageFailureKind::Unavailable, strings::get("accountUsage.detailPiUnavailable"))),
                "opencode" => Err(failure(AccountUsageFailureKind::Unavailable, strings::get("accountUsage.detailOpencodeUnavailable"))),
                "hermes" => Err(failure(AccountUsageFailureKind::Unavailable, strings::get("accountUsage.detailHermesUnavailable"))),
                _ => Err(failure(AccountUsageFailureKind::Unavailable, copy::detail_unsupported_provider())),
            }
        })
    })
}
