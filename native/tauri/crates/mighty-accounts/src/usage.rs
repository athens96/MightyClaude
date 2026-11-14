//! Account usage (C# AccountUsage.cs, AccountUsagePresentation.cs, AccountUsageReset.cs, AccountUsageResetSmoke.cs,
//! AccountUsageRuntime.cs, AccountUsageStrings.cs): each provider's account quota windows (independent of an agent's context
//! window), read locally — Codex through its own app-server, Antigravity through `agy -p /usage` — and, only once the user switches
//! the direct lookup on, Claude's from `https://api.anthropic.com/api/oauth/*` with the sign-in the CLI keeps in its credentials
//! file.
//!
//! Secrets: the Claude sign-in exists only inside one [`ClaudeAccountProbe::read`] call, in the `Authorization` header of a request
//! to `api.anthropic.com` ([`guard`]: HTTPS, that host, four paths, nothing else); a redirect is refused, never followed. No
//! snapshot, row, error, log line or `Debug` text carries it. The HTTPS GET is a port this crate defines ([`HttpsGet`]) until W6-A's
//! `mighty_platform::http` lands; the Windows one is WinHTTP with redirects off.

mod presentation;
mod probes;
mod reset;

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use chrono::{DateTime, Utc};
use mighty_platform::BoxFuture;
use mighty_run::{meta, strings};
use serde_json::Value;
use tokio::sync::watch;
use url::Url;

pub use presentation::{
    AccountUsageBar, AccountUsageBars, AccountUsageCard, AccountUsageChip, AccountUsageSmokeOutcome, AccountUsageStatus,
    AccountUsageWindowRow, leading, leading_bars_of, local_providers, session_reported,
};
pub use probes::{
    AccountUsageStdio, AntigravityAccountProbe, ClaudeAccountProbe, CodexAccountProbe, Finder, HostCodexStdio, RuntimeProbe, runtime_probe,
};
pub use reset::{
    AccountResetEntitlement, AccountResetRow, AccountResetSmokeResult, AccountUsageShapeLog, ClaudeResetEntitlements, ResetProgram,
    ResetReadDeadlineBox, ResetState, claude_reset_smoke,
};

use crate::lock;

crate::copy! {
    /// `AccountUsageStrings`: the account usage copy, read on demand so a language chosen later applies.
    pub mod copy {
        title("Title") = "windows.accountUsage.title";
        chips_tooltip("ChipsTooltip") = "windows.accountUsage.chipsTooltip";
        refresh_tooltip("RefreshTooltip") = "windows.accountUsage.refreshTooltip";
        refresh_accessibility_label("RefreshAccessibilityLabel") = "windows.accountUsage.refreshAccessibilityLabel";
        chip_before_first_run("ChipBeforeFirstRun") = "windows.accountUsage.chipBeforeFirstRun";
        chip_checking("ChipChecking") = "windows.accountUsage.chipChecking";
        chip_empty("ChipEmpty") = "windows.accountUsage.chipEmpty";
        toggle_label("ToggleLabel") = "windows.accountUsage.toggleLabel";
        toggle_description("ToggleDescription") = "windows.accountUsage.toggleDescription";
        shared_limits_note("SharedLimitsNote") = "windows.accountUsage.sharedLimitsNote";
        used_percent_template("UsedPercentTemplate") = "windows.accountUsage.usedPercentTemplate";
        reset_template("ResetTemplate") = "windows.accountUsage.resetTemplate";
        checked_at_template("CheckedAtTemplate") = "windows.accountUsage.checkedAtTemplate";
        last_known_prefix("LastKnownPrefix") = "windows.accountUsage.lastKnownPrefix";
        window_five_hour_suffix("WindowFiveHourSuffix") = "windows.accountUsage.windowFiveHourSuffix";
        window_seven_day_suffix("WindowSevenDaySuffix") = "windows.accountUsage.windowSevenDaySuffix";
        claude_before_first_run_note("ClaudeBeforeFirstRunNote") = "windows.accountUsage.claudeBeforeFirstRunNote";
        card_checking("CardChecking") = "windows.accountUsage.cardChecking";
        card_not_checked_yet("CardNotCheckedYet") = "windows.accountUsage.cardNotCheckedYet";
        window_session("WindowSession") = "windows.accountUsage.windowSession";
        window_weekly("WindowWeekly") = "windows.accountUsage.windowWeekly";
        window_daily("WindowDaily") = "windows.accountUsage.windowDaily";
        window_monthly("WindowMonthly") = "windows.accountUsage.windowMonthly";
        window_spend_limit("WindowSpendLimit") = "windows.accountUsage.windowSpendLimit";
        detail_not_checked_yet("DetailNotCheckedYet") = "windows.accountUsage.detailNotCheckedYet";
        detail_shutdown("DetailShutdown") = "windows.accountUsage.detailShutdown";
        detail_cancelled("DetailCancelled") = "windows.accountUsage.detailCancelled";
        detail_refresh_failed("DetailRefreshFailed") = "windows.accountUsage.detailRefreshFailed";
        detail_authentication("DetailAuthentication") = "windows.accountUsage.detailAuthentication";
        detail_rate_limited("DetailRateLimited") = "windows.accountUsage.detailRateLimited";
        detail_last_known_suffix("DetailLastKnownSuffix") = "windows.accountUsage.detailLastKnownSuffix";
        detail_antigravity_unavailable("DetailAntigravityUnavailable") = "accountUsage.detailAntigravityUnavailable";
        /// A grouped window's label (Antigravity's model groups): "{group} · {window}".
        group_window_template("GroupWindowTemplate") = "accountUsage.groupWindow";
        antigravity_group_gemini("AntigravityGroupGemini") = "accountUsage.antigravityGroupGemini";
        antigravity_group_claude_gpt("AntigravityGroupClaudeGpt") = "accountUsage.antigravityGroupClaudeGPT";
        detail_antigravity("DetailAntigravity") = "accountUsage.detailAntigravity";
        detail_antigravity_not_installed("DetailAntigravityNotInstalled") = "accountUsage.detailAntigravityNotInstalled";
        detail_antigravity_no_windows("DetailAntigravityNoWindows") = "accountUsage.detailAntigravityNoWindows";
        detail_unsupported_provider("DetailUnsupportedProvider") = "windows.accountUsage.detailUnsupportedProvider";
        detail_codex_not_installed("DetailCodexNotInstalled") = "windows.accountUsage.detailCodexNotInstalled";
        detail_codex_needs_chat_gpt("DetailCodexNeedsChatGPT") = "windows.accountUsage.detailCodexNeedsChatGPT";
        detail_custom_authentication("DetailCustomAuthentication") = "windows.accountUsage.detailCustomAuthentication";
        detail_codex_no_windows("DetailCodexNoWindows") = "windows.accountUsage.detailCodexNoWindows";
        detail_codex("DetailCodex") = "windows.accountUsage.detailCodex";
        detail_claude_no_windows("DetailClaudeNoWindows") = "windows.accountUsage.detailClaudeNoWindows";
        detail_claude("DetailClaude") = "windows.accountUsage.detailClaude";
        detail_session_reported_stale("DetailSessionReportedStale") = "windows.accountUsage.detailSessionReportedStale";
        detail_session_reported("DetailSessionReported") = "windows.accountUsage.detailSessionReported";
        reset_title("ResetTitle") = "usage.reset.title";
        reset_link("ResetLink") = "usage.reset.link";
        reset_program_cedar_ember("ResetProgramCedarEmber") = "usage.reset.program.cedarEmber";
        reset_program_juniper_tide("ResetProgramJuniperTide") = "usage.reset.program.juniperTide";
        reset_available_cedar_ember("ResetAvailableCedarEmber") = "usage.reset.available.cedarEmber";
        reset_available_juniper_tide("ResetAvailableJuniperTide") = "usage.reset.available.juniperTide";
        reset_held("ResetHeld") = "usage.reset.held";
        reset_cooldown_cedar_ember("ResetCooldownCedarEmber") = "usage.reset.cooldown.cedarEmber";
        reset_cooldown_juniper_tide("ResetCooldownJuniperTide") = "usage.reset.cooldown.juniperTide";
        reset_exhausted("ResetExhausted") = "usage.reset.exhausted";
        reset_none("ResetNone") = "usage.reset.none";
        reset_ineligible("ResetIneligible") = "usage.reset.ineligible";
        reset_unknown("ResetUnknown") = "usage.reset.unknown";
    }
}

// ── the records ───────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `AccountUsageWindow`: one quota window, `used_percent` 0–100.
#[derive(Debug, Clone, PartialEq)]
pub struct AccountUsageWindow {
    pub kind: String,
    pub used_percent: f64,
    pub resets_at: Option<String>,
    pub window_minutes: Option<i32>,
}

impl AccountUsageWindow {
    pub fn new(kind: &str, used_percent: f64) -> AccountUsageWindow {
        AccountUsageWindow { kind: kind.into(), used_percent, resets_at: None, window_minutes: None }
    }
}

/// `AccountUsageSnapshot`: memory-only presentation data — never a credential, a raw HTTP body or a transcript. `status` is
/// `available`, `unavailable`, `error`, `stale` or `cancelled`.
#[derive(Debug, Clone, PartialEq)]
pub struct AccountUsageSnapshot {
    pub provider: String,
    pub account_label: Option<String>,
    pub plan: Option<String>,
    pub windows: Vec<AccountUsageWindow>,
    /// The Claude limit-reset (리셋권) rows, one per programme; empty until the entitlement read happened. Never a grant id.
    pub resets: Vec<AccountResetEntitlement>,
    pub fetched_at: Option<String>,
    pub status: String,
    pub detail: String,
    /// A scheduling hint for the service; not presentation data.
    pub retry_after_seconds: Option<f64>,
}

impl AccountUsageSnapshot {
    /// `new AccountUsageSnapshot { Provider = provider }`: unavailable, not checked yet.
    pub fn new(provider: &str) -> AccountUsageSnapshot {
        AccountUsageSnapshot {
            provider: provider.into(),
            account_label: None,
            plan: None,
            windows: vec![],
            resets: vec![],
            fetched_at: None,
            status: "unavailable".into(),
            detail: copy::detail_not_checked_yet(),
            retry_after_seconds: None,
        }
    }

    fn cancelled(provider: &str, detail: String) -> AccountUsageSnapshot {
        AccountUsageSnapshot { status: "cancelled".into(), detail, ..AccountUsageSnapshot::new(provider) }
    }
}

/// `AccountUsageFailureKind`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AccountUsageFailureKind {
    Unavailable,
    InvalidResponse,
    Authentication,
    RateLimited,
    Network,
}

/// `AccountUsageFailure`: its detail is copy from [`copy`] only — never a credential, a header value or a response body.
#[derive(Debug, Clone, PartialEq)]
pub struct AccountUsageFailure {
    pub kind: AccountUsageFailureKind,
    pub detail: String,
    pub retry_after_seconds: f64,
}

impl AccountUsageFailure {
    pub fn new(kind: AccountUsageFailureKind, detail: String) -> AccountUsageFailure {
        AccountUsageFailure { kind, detail, retry_after_seconds: 0.0 }
    }

    fn refresh_failed(kind: AccountUsageFailureKind) -> AccountUsageFailure {
        AccountUsageFailure::new(kind, copy::detail_refresh_failed())
    }
}

impl std::fmt::Display for AccountUsageFailure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.detail)
    }
}

impl std::error::Error for AccountUsageFailure {}

/// What a probe ends with instead of a snapshot: a failure it named, anything else that went wrong (C#'s other exceptions: an I/O
/// error, a transport's), or its cancellation.
#[derive(Debug, Clone, PartialEq)]
pub enum UsageError {
    Failure(AccountUsageFailure),
    Other(String),
    Cancelled,
}

impl From<AccountUsageFailure> for UsageError {
    fn from(failure: AccountUsageFailure) -> Self {
        UsageError::Failure(failure)
    }
}

// ── the HTTPS GET over mighty-platform's port ────────────────────────────────────────────────────────────────────────────

pub use mighty_platform::http::{HttpsGet, HttpsRequest, HttpsResponse};

/// `AccountUsageHttpRequest`: the port's GET (always a GET: the port has no other method, so no POST can exist on this surface).
pub type AccountUsageHttpRequest = HttpsRequest;
/// `AccountUsageHttpResponse`.
pub type AccountUsageHttpResponse = HttpsResponse;

/// A response with `status` and `body` and nothing else.
pub fn response(status: u16, body: &str) -> HttpsResponse {
    HttpsResponse { status, body: body.into(), retry_after: None, redirect_location: None }
}

/// A request's header by name (ignoring case).
pub fn header<'a>(request: &'a HttpsRequest, name: &str) -> Option<&'a str> {
    request.headers.iter().find(|(key, _)| key.eq_ignore_ascii_case(name)).map(|(_, value)| value.as_str())
}

/// Why a GET gave no response: the guard refused its URL before it was sent, or the transport failed (no network, a timeout, a
/// body past the cap). Neither message carries a header value.
#[derive(Debug, Clone, PartialEq)]
pub enum HttpFailure {
    Refused(AccountUsageFailure),
    Transport(String),
}

/// One GET through the port: the URL guarded first ([`guard`]: refused before it leaves), then sent with the 1 MiB body cap.
pub async fn send(http: &dyn HttpsGet, request: HttpsRequest) -> Result<HttpsResponse, HttpFailure> {
    let refused = || HttpFailure::Refused(AccountUsageFailure::refresh_failed(AccountUsageFailureKind::Network));
    let url = Url::parse(&request.url).map_err(|_| refused())?;
    guard(&url).map_err(HttpFailure::Refused)?;
    http.get(request, MAXIMUM_BODY_BYTES).await.map_err(|error| HttpFailure::Transport(error.to_string()))
}

/// A synchronous handler as an [`HttpsGet`] (the checks' recording transports, the reset smoke's fake): it never reaches a network.
pub struct HttpFn<F>(pub F);

impl<F> HttpsGet for HttpFn<F>
where
    F: Fn(&HttpsRequest) -> mighty_platform::Result<HttpsResponse> + Send + Sync,
{
    fn get(&self, request: HttpsRequest, _max_body: usize) -> BoxFuture<'_, mighty_platform::Result<HttpsResponse>> {
        let answer = (self.0)(&request);
        Box::pin(async move { answer })
    }
}

/// `ClaudeAccountProbe.Guarded`: the guard runs before the transport, so a URL it refuses never reaches the network (refused with
/// the guard's copy, never a header value).
pub struct Guarded(pub Arc<dyn HttpsGet>);

impl HttpsGet for Guarded {
    fn get(&self, request: HttpsRequest, max_body: usize) -> BoxFuture<'_, mighty_platform::Result<HttpsResponse>> {
        let allowed = Url::parse(&request.url).ok().is_some_and(|url| guard(&url).is_ok());
        if !allowed {
            return Box::pin(async { Err(mighty_platform::PlatformError::Refused(copy::detail_refresh_failed())) });
        }
        self.0.get(request, max_body)
    }
}

/// `ClaudeAccountProbe.Host`.
pub const HOST: &str = "api.anthropic.com";

const ALLOWED: [&str; 4] =
    ["/api/oauth/usage", "/api/oauth/profile", "/api/oauth/usage?cedar_ember=1&skip_spend=1", "/api/oauth/usage?at_wall=1&skip_spend=1"];

/// `Uri.PathAndQuery` of a URL.
pub fn path_and_query(url: &Url) -> String {
    match url.query() {
        Some(query) => format!("{}?{query}", url.path()),
        None => url.path().to_string(),
    }
}

/// `ClaudeAccountProbe.Guard`: HTTPS, exactly `api.anthropic.com` (its own port, no user info, no fragment), and only the four
/// allowed paths — a usage query with a reset variant must carry `skip_spend=1` and nothing else. Anything else is refused here,
/// before the sign-in ever leaves.
pub fn guard(url: &Url) -> Result<(), AccountUsageFailure> {
    let host_ok = url.scheme() == "https"
        && url.host_str() == Some(HOST)
        && url.port().is_none()
        && url.username().is_empty()
        && url.password().is_none()
        && url.fragment().is_none();
    if !host_ok || !ALLOWED.contains(&path_and_query(url).as_str()) {
        return Err(AccountUsageFailure::refresh_failed(AccountUsageFailureKind::Network));
    }
    Ok(())
}

/// `ClaudeAccountProbe.Endpoint`: `https://api.anthropic.com/api/oauth/<path>[?query]`, guarded.
pub fn endpoint(path: &str, query: Option<&str>) -> Result<Url, AccountUsageFailure> {
    let text = match query.filter(|q| !q.is_empty()) {
        Some(query) => format!("https://{HOST}/api/oauth/{path}?{query}"),
        None => format!("https://{HOST}/api/oauth/{path}"),
    };
    let url = Url::parse(&text).map_err(|_| AccountUsageFailure::refresh_failed(AccountUsageFailureKind::Network))?;
    guard(&url)?;
    Ok(url)
}

// ── support ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `AccountUsageSupport.MaximumBodyBytes`.
pub const MAXIMUM_BODY_BYTES: usize = 1024 * 1024;
/// `MinimumBackoffSeconds`.
pub const MINIMUM_BACKOFF_SECONDS: f64 = 60.0;
/// `MaximumBackoffSeconds`.
pub const MAXIMUM_BACKOFF_SECONDS: f64 = 86_400.0;
/// `DefaultBackoffSeconds`.
pub const DEFAULT_BACKOFF_SECONDS: f64 = 300.0;

/// `AccountUsageSupport.Text`: control characters stripped, capped at 160, trimmed; None when nothing is left.
pub fn text(value: Option<&str>) -> Option<String> {
    let clean = mighty_core::wire::clean(value.unwrap_or(""), 160).trim().to_string();
    (!clean.is_empty()).then_some(clean)
}

/// `AccountUsageSupport.Timestamp`: the instant's `"O"` text in UTC.
pub fn timestamp(value: DateTime<Utc>) -> String {
    mighty_core::time::DateTimeOffset::from_utc_ticks(mighty_core::time::ticks_of(value)).to_o()
}

/// `AccountUsageSupport.Date` (`AgentRunTiming.Parse`).
pub fn date(value: Option<&str>) -> Option<DateTime<Utc>> {
    mighty_core::time::parse(value).map(|parsed| parsed.to_chrono())
}

/// `AccountUsageSupport.Reset`: an ISO date, or epoch seconds inside a sane range; anything else is dropped so a malformed reset
/// never becomes a real-looking date.
pub fn reset(value: Option<&Value>) -> Option<String> {
    match value? {
        Value::String(text) => date(Some(text)).map(timestamp),
        Value::Number(number) => {
            let seconds = number.as_f64()?;
            (seconds.is_finite() && seconds > 0.0 && seconds < 32_503_680_000.0)
                .then(|| DateTime::<Utc>::from_timestamp_millis((seconds * 1000.0) as i64))
                .flatten()
                .map(timestamp)
        }
        _ => None,
    }
}

/// `AccountUsageSupport.Backoff`: clamped into a minute … a day; a number that is not finite waits the default five minutes.
pub fn backoff(seconds: f64) -> f64 {
    if seconds.is_finite() { seconds.clamp(MINIMUM_BACKOFF_SECONDS, MAXIMUM_BACKOFF_SECONDS) } else { DEFAULT_BACKOFF_SECONDS }
}

/// `AccountUsageSupport.RetryInterval`: `Retry-After` as seconds or as an HTTP date, clamped into the back-off range.
pub fn retry_interval(value: Option<&str>, now: DateTime<Utc>) -> f64 {
    let Some(value) = value else { return DEFAULT_BACKOFF_SECONDS };
    if let Ok(seconds) = value.trim().parse::<f64>() {
        return backoff(seconds);
    }
    let when = date(Some(value)).or_else(|| DateTime::parse_from_rfc2822(value.trim()).ok().map(|d| d.with_timezone(&Utc)));
    match when {
        Some(when) => backoff((when - now).num_milliseconds() as f64 / 1000.0),
        None => DEFAULT_BACKOFF_SECONDS,
    }
}

/// `AccountUsageSupport.WindowLabel` (the Mac's `RateLimitWindowLabel`), in the app's language. A grouped window,
/// `<window>@<group>`, reads "<group> · <window>".
pub fn window_label(kind: &str) -> String {
    if let Some(at) = kind.find('@') {
        return copy::group_window_template()
            .replace("{window}", &window_label(&kind[..at]))
            .replace("{group}", &group_label(&kind[at + 1..]));
    }
    let lowered = kind.to_lowercase();
    match lowered.as_str() {
        "session" | "five_hour" | "5h" | "primary" => return copy::window_session(),
        "weekly" | "seven_day" | "7d" | "secondary" => return copy::window_weekly(),
        "daily" => return copy::window_daily(),
        "monthly" => return copy::window_monthly(),
        "spend_limit" => return copy::window_spend_limit(),
        _ => {}
    }
    let rest = || kind.get(10..).unwrap_or("");
    if lowered.starts_with("seven_day_") {
        return format!("{} {}", copy::window_weekly(), rest());
    }
    if lowered.starts_with("five_hour_") {
        return format!("{} {}", copy::window_session(), rest());
    }
    let digits =
        |unit: char| lowered.len() > 1 && lowered.ends_with(unit) && lowered[..lowered.len() - 1].bytes().all(|b| b.is_ascii_digit());
    if digits('m') {
        return strings::get_with("windows.accountUsage.minutes", &[("count", &lowered[..lowered.len() - 1])]);
    }
    if digits('h') {
        return strings::get_with("windows.accountUsage.hours", &[("count", &lowered[..lowered.len() - 1])]);
    }
    kind.replace('_', " ")
}

/// `AccountUsageSupport.Window`: the window part of a grouped kind ("session@gemini" → "session").
pub fn window(kind: &str) -> &str {
    kind.find('@').map_or(kind, |at| &kind[..at])
}

/// `AccountUsageSupport.Grouped`.
pub fn grouped(kind: &str) -> bool {
    kind.contains('@')
}

/// `AccountUsageSupport.GroupLabel`: Antigravity's two known groups in the app's language; any other keeps agy's own name.
pub fn group_label(group: &str) -> String {
    match group {
        "gemini" => copy::antigravity_group_gemini(),
        "claude_gpt" => copy::antigravity_group_claude_gpt(),
        other => other.to_string(),
    }
}

/// `AccountUsageSupport.Percent`: rounded half away from zero.
pub fn percent(value: f64) -> String {
    (value.round() as i64).to_string()
}

fn number(value: &Value, key: &str) -> Option<f64> {
    meta::number(value, key)
}

// ── the Claude credentials file ───────────────────────────────────────────────────────────────────────────────────────────

/// `ClaudeQuotaCredential`: the sign-in, for one read. Its `Debug` never shows the token.
#[derive(Clone, PartialEq, Eq)]
pub struct ClaudeQuotaCredential {
    pub token: String,
    pub plan: Option<String>,
}

impl std::fmt::Debug for ClaudeQuotaCredential {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ClaudeQuotaCredential").field("plan", &self.plan).finish_non_exhaustive()
    }
}

/// C# `ClaudeCredentialFile`: the credentials file the CLI writes under the user's `.claude` folder. Read only: never written,
/// rotated or moved; the token lives in memory for one request.
pub mod credential_file {
    use super::*;

    pub const MAXIMUM_BYTES: usize = 262_144;
    pub const MAXIMUM_TOKEN_BYTES: usize = 16_384;
    pub const FILE_NAME: &str = ".credentials.json";

    /// `Path`: `$CLAUDE_CONFIG_DIR` wins, otherwise `<home>/.claude`. There is no Keychain on Windows: the file is the only source.
    pub fn path(home: &str, config_directory: Option<&str>) -> String {
        let folder = match config_directory.filter(|dir| !dir.is_empty()) {
            Some(dir) => dir.to_string(),
            None => mighty_run::os::combine(home, ".claude"),
        };
        mighty_run::os::combine(&folder, FILE_NAME)
    }

    /// `Parse`: `claudeAiOauth.accessToken` (at most 16 KiB of clean text), refused once expired or when its scopes leave out
    /// `user:profile`; the subscription type as the plan.
    pub fn parse(json: &str, now: DateTime<Utc>) -> Option<ClaudeQuotaCredential> {
        if json.is_empty() || json.len() > MAXIMUM_BYTES {
            return None;
        }
        let root: Value = serde_json::from_str(json).ok()?;
        let oauth = meta::prop(&root, "claudeAiOauth").filter(|o| o.is_object())?;
        let token = meta::text(oauth, "accessToken").filter(|t| !t.is_empty())?;
        if token.len() > MAXIMUM_TOKEN_BYTES || token != mighty_core::wire::clean(token, MAXIMUM_TOKEN_BYTES) {
            return None;
        }
        if let Some(expires) = number(oauth, "expiresAt")
            && expires <= now.timestamp_millis() as f64
        {
            return None;
        }
        if let Some(Value::Array(scopes)) = meta::prop(oauth, "scopes")
            && !scopes.is_empty()
            && !scopes.iter().any(|scope| scope.as_str() == Some("user:profile"))
        {
            return None;
        }
        Some(ClaudeQuotaCredential { token: token.to_string(), plan: text(meta::text(oauth, "subscriptionType")) })
    }

    /// `Read`: size-checked before it is read, and never touched otherwise.
    pub fn read(home: &str, environment: &std::collections::BTreeMap<String, String>, now: DateTime<Utc>) -> Option<ClaudeQuotaCredential> {
        let path = path(home, crate::env_get(environment, "CLAUDE_CONFIG_DIR"));
        let info = std::fs::metadata(&path).ok()?;
        if !info.is_file() || info.len() > MAXIMUM_BYTES as u64 {
            return None;
        }
        parse(&String::from_utf8_lossy(&std::fs::read(&path).ok()?), now)
    }
}

// ── the service ───────────────────────────────────────────────────────────────────────────────────────────────────────────

/// What reads a provider's usage (the app's: [`runtime_probe`]).
pub type UsageProbe = Arc<dyn Fn(String) -> BoxFuture<'static, Result<AccountUsageSnapshot, UsageError>> + Send + Sync>;
/// The clock every comparison reads.
pub type Clock = Arc<dyn Fn() -> DateTime<Utc> + Send + Sync>;

/// The clock the app runs on (`DateTimeOffset.UtcNow`, which the checks may pin through `mighty_core::time::set_clock`).
pub fn system_clock() -> Clock {
    Arc::new(mighty_core::time::clock_now)
}

#[derive(Default)]
struct ServiceState {
    in_flight: HashMap<String, watch::Receiver<Option<AccountUsageSnapshot>>>,
    cache: HashMap<String, AccountUsageSnapshot>,
    next_read: HashMap<String, DateTime<Utc>>,
    closed: bool,
}

struct ServiceInner {
    probe: UsageProbe,
    clock: Clock,
    state: Mutex<ServiceState>,
    closing: watch::Sender<bool>,
}

/// C# `AccountUsageService`: local account reads only. One read at a time per provider, a cache, a next read time and a back-off;
/// a failure keeps the last known value and marks it. The probe and the clock are injected.
#[derive(Clone)]
pub struct AccountUsageService {
    inner: Arc<ServiceInner>,
}

enum Decision {
    Ready(AccountUsageSnapshot),
    Waiting(watch::Receiver<Option<AccountUsageSnapshot>>),
}

impl AccountUsageService {
    pub fn new(probe: UsageProbe, clock: Clock) -> AccountUsageService {
        AccountUsageService {
            inner: Arc::new(ServiceInner { probe, clock, state: Mutex::new(ServiceState::default()), closing: watch::channel(false).0 }),
        }
    }

    /// `Cached`.
    pub fn cached(&self, provider: &str) -> Option<AccountUsageSnapshot> {
        lock(&self.inner.state).cache.get(provider).cloned()
    }

    /// `ReadAsync`: the provider's usage. A read already under way is joined; the server's back-off holds even a forced read
    /// back while a value is kept; an unforced read takes a value under a minute old. Must run inside a Tokio runtime.
    pub fn read(&self, provider: &str, force: bool) -> impl std::future::Future<Output = AccountUsageSnapshot> + Send + 'static {
        let inner = self.inner.clone();
        let now = (inner.clock)();
        let provider = provider.to_string();
        let decision = {
            let mut state = lock(&inner.state);
            if state.closed || *inner.closing.borrow() {
                Decision::Ready(AccountUsageSnapshot::cancelled(&provider, copy::detail_shutdown()))
            } else if let Some(running) = state.in_flight.get(&provider) {
                Decision::Waiting(running.clone())
            } else if let (Some(deadline), Some(held)) = (state.next_read.get(&provider), state.cache.get(&provider))
                && *deadline > now
            {
                Decision::Ready(held.clone())
            } else if let Some(saved) = state.cache.get(&provider).filter(|_| !force)
                && date(saved.fetched_at.as_deref()).is_some_and(|stamp| (now - stamp).num_milliseconds() < 60_000)
            {
                Decision::Ready(saved.clone())
            } else {
                let (sender, receiver) = watch::channel(None);
                state.in_flight.insert(provider.clone(), receiver.clone());
                tokio::spawn(run(inner.clone(), provider.clone(), now, sender));
                Decision::Waiting(receiver)
            }
        };
        async move {
            match decision {
                Decision::Ready(value) => value,
                Decision::Waiting(mut receiver) => match receiver.wait_for(Option::is_some).await {
                    Ok(value) => value.clone().unwrap_or_else(|| AccountUsageSnapshot::cancelled(&provider, copy::detail_cancelled())),
                    Err(_) => AccountUsageSnapshot::cancelled(&provider, copy::detail_cancelled()),
                },
            }
        }
    }

    /// `DisposeAsync`: closing the app cancels the pending reads and drops every cached value.
    pub async fn close(&self) {
        let pending: Vec<watch::Receiver<Option<AccountUsageSnapshot>>> = {
            let state = lock(&self.inner.state);
            if state.closed {
                return;
            }
            state.in_flight.values().cloned().collect()
        };
        self.inner.closing.send_replace(true);
        for mut read in pending {
            let _ = read.wait_for(Option::is_some).await;
        }
        let mut state = lock(&self.inner.state);
        state.closed = true;
        state.in_flight.clear();
        state.cache.clear();
        state.next_read.clear();
    }
}

async fn run(inner: Arc<ServiceInner>, provider: String, started: DateTime<Utc>, sender: watch::Sender<Option<AccountUsageSnapshot>>) {
    tokio::task::yield_now().await;
    let mut closing = inner.closing.subscribe();
    let outcome = tokio::select! {
        outcome = (inner.probe)(provider.clone()) => outcome,
        _ = closing.wait_for(|closing| *closing) => Err(UsageError::Cancelled),
    };
    let value = match outcome {
        Ok(read) => {
            let value = AccountUsageSnapshot { fetched_at: Some(timestamp(started)), ..read };
            let mut state = lock(&inner.state);
            if !state.closed {
                state.cache.insert(provider.clone(), value.clone());
                let wait = value.retry_after_seconds.map(backoff).unwrap_or(5.0);
                state.next_read.insert(provider.clone(), (inner.clock)() + chrono::TimeDelta::milliseconds((wait * 1000.0) as i64));
            }
            value
        }
        Err(error) => failed(&inner, &provider, error),
    };
    let mut state = lock(&inner.state);
    state.in_flight.remove(&provider);
    sender.send_replace(Some(value));
}

/// `Failed`: a failure keeps the last known windows and marks them stale; one that invalidates the account (authentication, an
/// unavailable account) drops them instead of showing another account's numbers.
fn failed(inner: &ServiceInner, provider: &str, error: UsageError) -> AccountUsageSnapshot {
    let mut state = lock(&inner.state);
    if state.closed || *inner.closing.borrow() || error == UsageError::Cancelled {
        return AccountUsageSnapshot::cancelled(provider, copy::detail_cancelled());
    }
    let (mut delay, mut detail, mut preserve) = (MINIMUM_BACKOFF_SECONDS, copy::detail_refresh_failed(), true);
    if let UsageError::Failure(failure) = &error {
        match failure.kind {
            AccountUsageFailureKind::Unavailable => {
                detail = failure.detail.clone();
                preserve = false;
            }
            AccountUsageFailureKind::Authentication => {
                detail = copy::detail_authentication();
                preserve = false;
            }
            AccountUsageFailureKind::RateLimited => {
                delay = backoff(failure.retry_after_seconds);
                detail = copy::detail_rate_limited();
            }
            _ => {}
        }
    }
    let held = state.cache.get(provider).filter(|_| preserve).cloned().unwrap_or_else(|| AccountUsageSnapshot::new(provider));
    let empty = held.windows.is_empty();
    let value = AccountUsageSnapshot {
        provider: provider.to_string(),
        status: if empty { if preserve { "error" } else { "unavailable" } } else { "stale" }.into(),
        detail: if empty { detail } else { format!("{detail}{}", copy::detail_last_known_suffix()) },
        retry_after_seconds: None,
        ..held
    };
    state.cache.insert(provider.to_string(), value.clone());
    state.next_read.insert(provider.to_string(), (inner.clock)() + chrono::TimeDelta::milliseconds((delay * 1000.0) as i64));
    value
}
