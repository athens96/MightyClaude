//! The Claude limit-reset (리셋권) entitlement, read only (C# AccountUsageReset.cs, AccountUsageResetSmoke.cs; the Mac's
//! AccountResetEntitlement.swift): the same two programmes, the same seven states, the same decision lists and the same shared
//! `usage.reset.*` keys. The app never claims, spends or writes one; the only action is the link. Every time comparison reads the
//! caller's clock.

use std::collections::HashSet;
use std::sync::{Arc, Mutex};

use chrono::{DateTime, Utc};
use mighty_run::{meta, strings};
use serde_json::Value;

use super::{
    AccountUsageFailure, AccountUsageHttpRequest, AccountUsageHttpResponse, AccountUsageSnapshot, ClaudeAccountProbe,
    ClaudeQuotaCredential, HttpFailure, HttpFn, MAXIMUM_BODY_BYTES, UsageError, date, guard, number, retry_interval, timestamp,
};
use crate::lock;

/// C# `ResetReadDeadlineBox`: the reset reads' 429 deadline, kept across reads.
#[derive(Debug, Default)]
pub struct ResetReadDeadlineBox(Mutex<Option<DateTime<Utc>>>);

impl ResetReadDeadlineBox {
    pub const fn new() -> ResetReadDeadlineBox {
        ResetReadDeadlineBox(Mutex::new(None))
    }
    pub fn get(&self) -> Option<DateTime<Utc>> {
        *lock(&self.0)
    }
    pub fn set(&self, value: Option<DateTime<Utc>>) {
        *lock(&self.0) = value;
    }
}

/// `ClaudeAccountProbe.SharedDeadlineBox`: the app's one deadline.
pub static SHARED_DEADLINE: ResetReadDeadlineBox = ResetReadDeadlineBox::new();

type Sink = Arc<dyn Fn(&str) + Send + Sync>;

/// C# `AccountUsageShapeLog`: the key paths and value types (never a value) of the first successful reset response per programme,
/// once per instance, each line prefixed `account-usage: `, through an injected sink.
pub struct AccountUsageShapeLog {
    emit: Sink,
    logged: Mutex<HashSet<String>>,
}

impl AccountUsageShapeLog {
    pub fn new(emit: impl Fn(&str) + Send + Sync + 'static) -> AccountUsageShapeLog {
        AccountUsageShapeLog { emit: Arc::new(emit), logged: Mutex::new(HashSet::new()) }
    }

    /// `LogShapeOnce`.
    pub fn log_shape_once(&self, program: &str, body: &Value) {
        if !lock(&self.logged).insert(program.to_string()) {
            return;
        }
        let Value::Object(map) = body else { return };
        let mut names: Vec<&String> = map.keys().collect();
        names.sort_by(|a, b| a.encode_utf16().cmp(b.encode_utf16()));
        for name in names {
            for line in shape_lines(&map[name.as_str()], name) {
                (self.emit)(&format!("account-usage: {line}"));
            }
        }
    }
}

impl std::fmt::Debug for AccountUsageShapeLog {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AccountUsageShapeLog").finish_non_exhaustive()
    }
}

fn shape_lines(element: &Value, path: &str) -> Vec<String> {
    match element {
        Value::Object(map) => {
            let mut lines = vec![format!("{path}: object")];
            let mut names: Vec<&String> = map.keys().collect();
            names.sort_by(|a, b| a.encode_utf16().cmp(b.encode_utf16()));
            for name in names {
                lines.extend(shape_lines(&map[name.as_str()], &format!("{path}.{name}")));
            }
            lines
        }
        Value::Array(items) => {
            let mut lines = vec![format!("{path}: array")];
            if let Some(first) = items.first() {
                lines.extend(shape_lines(first, &format!("{path}[]")));
            }
            lines
        }
        Value::Bool(_) => vec![format!("{path}: boolean")],
        Value::Number(_) => vec![format!("{path}: number")],
        Value::String(_) => vec![format!("{path}: string")],
        Value::Null => vec![format!("{path}: null")],
    }
}

/// C# `ResetProgram`: the two programmes.
pub struct ResetProgram;

impl ResetProgram {
    pub const CEDAR_EMBER: &'static str = "cedar_ember";
    pub const JUNIPER_TIDE: &'static str = "juniper_tide";
    pub const ALL: [&'static str; 2] = [Self::CEDAR_EMBER, Self::JUNIPER_TIDE];

    /// `Query`: the one query variant that carries this programme, `skip_spend=1` included.
    pub fn query(program: &str) -> &'static str {
        if program == Self::CEDAR_EMBER { "cedar_ember=1&skip_spend=1" } else { "at_wall=1&skip_spend=1" }
    }

    /// `LabelKey`.
    pub fn label_key(program: &str) -> &'static str {
        if program == Self::CEDAR_EMBER { "usage.reset.program.cedarEmber" } else { "usage.reset.program.juniperTide" }
    }
}

/// `ResetState`: seven states, highest precedence first.
pub struct ResetState;

impl ResetState {
    pub const AVAILABLE: &'static str = "available";
    pub const HELD: &'static str = "held";
    pub const COOLDOWN: &'static str = "cooldown";
    pub const EXHAUSTED: &'static str = "exhausted";
    pub const NONE: &'static str = "none";
    pub const INELIGIBLE: &'static str = "ineligible";
    pub const UNKNOWN: &'static str = "unknown";
    pub const ALL: [&'static str; 7] =
        [Self::AVAILABLE, Self::HELD, Self::COOLDOWN, Self::EXHAUSTED, Self::NONE, Self::INELIGIBLE, Self::UNKNOWN];
}

/// C# `AccountResetEntitlement`: one 리셋권 row. No credential, no grant id, no account identifier.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AccountResetEntitlement {
    pub program: String,
    pub state: String,
    /// cedar_ember: the selected grant's `resets_left`.
    pub remaining_count: Option<i32>,
    /// cedar_ember: the selected grant's `ends_at`.
    pub expires_at: Option<String>,
    /// juniper_tide: `next_available_at`; cedar_ember: `cooldown_until`.
    pub next_available_at: Option<String>,
    /// juniper_tide: `weekly_resets_at`.
    pub weekly_resets_at: Option<String>,
    /// juniper_tide: `resets_per_week`.
    pub resets_per_week: Option<i32>,
}

impl AccountResetEntitlement {
    pub fn new(program: &str, state: &str) -> AccountResetEntitlement {
        AccountResetEntitlement {
            program: program.into(),
            state: state.into(),
            remaining_count: None,
            expires_at: None,
            next_available_at: None,
            weekly_resets_at: None,
            resets_per_week: None,
        }
    }

    /// `CopyKey`: one shared key per programme and state. Unknown blames this app's connection, never Anthropic's policy.
    pub fn copy_key(&self) -> &'static str {
        let cedar = self.program == ResetProgram::CEDAR_EMBER;
        match self.state.as_str() {
            ResetState::AVAILABLE if cedar => "usage.reset.available.cedarEmber",
            ResetState::AVAILABLE => "usage.reset.available.juniperTide",
            ResetState::COOLDOWN if cedar => "usage.reset.cooldown.cedarEmber",
            ResetState::COOLDOWN => "usage.reset.cooldown.juniperTide",
            ResetState::HELD => "usage.reset.held",
            ResetState::EXHAUSTED => "usage.reset.exhausted",
            ResetState::NONE => "usage.reset.none",
            ResetState::INELIGIBLE => "usage.reset.ineligible",
            _ => "usage.reset.unknown",
        }
    }
}

/// C# `AccountResetRow`: the programme's own name and its one sentence, both from the shared keys.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AccountResetRow {
    pub program: String,
    pub state: String,
    pub label: String,
    pub text: String,
}

fn flag(block: &Value, key: &str) -> Option<bool> {
    meta::prop(block, key).and_then(Value::as_bool)
}

fn present(block: &Value, key: &str) -> bool {
    meta::prop(block, key).is_some()
}

fn whole(block: &Value, key: &str) -> Option<i32> {
    number(block, key).filter(|v| (0.0..=1_000_000.0).contains(v) && v.round() == *v).map(|v| v as i32)
}

fn instant(block: &Value, key: &str) -> Option<DateTime<Utc>> {
    meta::prop(block, key).and_then(Value::as_str).and_then(|text| date(Some(text)))
}

/// `ineligible_reason` marks the state; its value is never matched literally.
fn reason_present(block: &Value) -> bool {
    meta::prop(block, "ineligible_reason").and_then(Value::as_str).is_some_and(|reason| !reason.is_empty())
}

/// C# `ClaudeResetEntitlements`: the decision lists, top to bottom, first match wins.
pub struct ClaudeResetEntitlements;

impl ClaudeResetEntitlements {
    /// `LinkKey`: the link, live in every one of the seven states.
    pub const LINK_KEY: &'static str = "usage.reset.link";
    pub const LINK_TARGET: &'static str = "https://claude.ai/settings/usage";
    pub const TITLE_KEY: &'static str = "usage.reset.title";

    pub fn link_label() -> String {
        strings::get(Self::LINK_KEY)
    }

    pub fn title() -> String {
        strings::get(Self::TITLE_KEY)
    }

    pub fn unknown(program: &str) -> AccountResetEntitlement {
        AccountResetEntitlement::new(program, ResetState::UNKNOWN)
    }

    pub fn ineligible(program: &str) -> AccountResetEntitlement {
        AccountResetEntitlement::new(program, ResetState::INELIGIBLE)
    }

    /// `CedarEmber`: granted resets (the field names are the CLI 2.1.280 decoder's).
    pub fn cedar_ember(body: &Value, now: DateTime<Utc>) -> AccountResetEntitlement {
        let program = ResetProgram::CEDAR_EMBER;
        // (1) a wrong-typed field or a missing block: this app cannot tell, so it says so.
        let Some(block) = body.as_object().and_then(|o| o.get(program)).filter(|b| b.is_object()) else {
            return Self::unknown(program);
        };
        if present(block, "eligible") && flag(block, "eligible").is_none() {
            return Self::unknown(program);
        }
        let grants = meta::prop(block, "grants");
        if present(block, "grants") && !grants.is_some_and(Value::is_array) {
            return Self::unknown(program);
        }
        let selected = meta::prop(block, "next_grant_id");
        if present(block, "next_grant_id") && !selected.is_some_and(|s| s.is_string() || s.is_null()) {
            return Self::unknown(program);
        }
        // (2) the server says this account is not in the programme (`in_experiment` too, as juniper_tide spells it).
        if flag(block, "eligible") == Some(false) || flag(block, "in_experiment") == Some(false) || reason_present(block) {
            return Self::ineligible(program);
        }
        // (3) no grant is selected, or the selected id matches nothing.
        let id = selected.and_then(Value::as_str).filter(|id| !id.is_empty());
        let grant = id.and_then(|id| {
            grants.and_then(Value::as_array)?.iter().find(|candidate| candidate.is_object() && meta::text(candidate, "id") == Some(id))
        });
        let Some(grant) = grant else { return AccountResetEntitlement::new(program, ResetState::NONE) };
        let left = whole(grant, "resets_left");
        let ends = instant(grant, "ends_at");
        let expiry = ends.map(timestamp);
        let with = |state: &str| AccountResetEntitlement {
            remaining_count: left,
            expires_at: expiry.clone(),
            ..AccountResetEntitlement::new(program, state)
        };
        // (4) a paused grant, or one whose window already closed.
        if flag(grant, "paused") == Some(true) || ends.is_some_and(|closes| closes <= now) {
            return AccountResetEntitlement::new(program, ResetState::NONE);
        }
        // (5) usable right now.
        if flag(grant, "usable_now") == Some(true) {
            return with(ResetState::AVAILABLE);
        }
        // (6) held back until the account reaches its limit.
        if flag(grant, "use_requires_limit") == Some(true) && flag(block, "at_limit") != Some(true) {
            return with(ResetState::HELD);
        }
        // (7) still cooling down from the last use.
        if let Some(cooldown) = instant(block, "cooldown_until").filter(|c| *c > now) {
            return AccountResetEntitlement { next_available_at: Some(timestamp(cooldown)), ..with(ResetState::COOLDOWN) };
        }
        // (8) nothing left this period: `exhausted` is a list or a flag.
        let spent = match meta::prop(block, "exhausted") {
            Some(Value::Array(items)) => !items.is_empty(),
            Some(Value::Bool(true)) => true,
            _ => false,
        };
        if spent || left == Some(0) {
            return with(ResetState::EXHAUSTED);
        }
        // (9) otherwise there is nothing to offer.
        with(ResetState::NONE)
    }

    /// `JuniperTide`: the reset offered when the account is at the wall.
    pub fn juniper_tide(body: &Value, now: DateTime<Utc>) -> AccountResetEntitlement {
        let program = ResetProgram::JUNIPER_TIDE;
        let Some(block) = body.as_object().and_then(|o| o.get(program)).filter(|b| b.is_object()) else {
            return Self::unknown(program);
        };
        if present(block, "in_experiment") && flag(block, "in_experiment").is_none()
            || present(block, "available") && flag(block, "available").is_none()
        {
            return Self::unknown(program);
        }
        if flag(block, "in_experiment") == Some(false) || reason_present(block) {
            return Self::ineligible(program);
        }
        let per_week = whole(block, "resets_per_week");
        let weekly = instant(block, "weekly_resets_at").map(timestamp);
        let with = |state: &str| AccountResetEntitlement {
            weekly_resets_at: weekly.clone(),
            resets_per_week: per_week,
            ..AccountResetEntitlement::new(program, state)
        };
        if flag(block, "available") == Some(true) {
            return with(ResetState::AVAILABLE);
        }
        if let Some(next) = instant(block, "next_available_at").filter(|n| *n > now) {
            return AccountResetEntitlement { next_available_at: Some(timestamp(next)), ..with(ResetState::COOLDOWN) };
        }
        if per_week == Some(0) {
            return with(ResetState::NONE);
        }
        with(ResetState::EXHAUSTED)
    }

    /// `ReadAsync`: one GET per programme (the two query variants the installed CLI uses), on the base usage read's schedule. A
    /// 404, or a 200 whose body has no block for it, is an account outside the programme; anything else this app could not read
    /// stays unknown. A 429 sets the deadline (`deadline`): until it passes, no reset GET is sent and every row reads unknown. A
    /// failure here never changes the base usage. A transport failure ends the whole read, as C#'s exception does.
    pub async fn read(
        request: impl Fn(&str) -> Result<AccountUsageHttpRequest, AccountUsageFailure>,
        http: &dyn super::HttpsGet,
        clock: &(dyn Fn() -> DateTime<Utc> + Send + Sync),
        deadline: Option<&ResetReadDeadlineBox>,
        shape_log: Option<&AccountUsageShapeLog>,
    ) -> Result<Vec<AccountResetEntitlement>, UsageError> {
        let now = clock();
        if deadline.and_then(ResetReadDeadlineBox::get).is_some_and(|skip_until| now < skip_until) {
            return Ok(ResetProgram::ALL.iter().map(|program| Self::unknown(program)).collect());
        }
        let mut rows = Vec::new();
        let mut new_deadline: Option<DateTime<Utc>> = None;
        for program in ResetProgram::ALL {
            let mut row = Self::unknown(program);
            let reply = match request(ResetProgram::query(program)) {
                Err(_) => None,
                Ok(request) => match http.get(request).await {
                    Ok(reply) => Some(reply),
                    Err(HttpFailure::Refused(_)) => None,
                    Err(HttpFailure::Transport(why)) => return Err(UsageError::Other(why)),
                },
            };
            if let Some(reply) = reply {
                if reply.status == 404 {
                    row = Self::ineligible(program);
                } else if reply.status == 429 {
                    let at = now + chrono::TimeDelta::milliseconds((retry_interval(reply.retry_after.as_deref(), now) * 1000.0) as i64);
                    if new_deadline.is_none_or(|d| at > d) {
                        new_deadline = Some(at);
                    }
                } else if (200..300).contains(&reply.status)
                    && reply.redirect_location.is_none()
                    && reply.body.len() <= MAXIMUM_BODY_BYTES
                    && let Ok(body) = serde_json::from_str::<Value>(&reply.body)
                {
                    // A body without the block at all is an account outside the programme, not a gap in what this app sees.
                    row = if body.as_object().is_some_and(|o| !o.contains_key(program)) {
                        Self::ineligible(program)
                    } else if program == ResetProgram::CEDAR_EMBER {
                        Self::cedar_ember(&body, clock())
                    } else {
                        Self::juniper_tide(&body, clock())
                    };
                    if let Some(log) = shape_log {
                        log.log_shape_once(program, &body);
                    }
                }
                // A 5xx leaves the row unknown and the base usage untouched.
            }
            rows.push(row);
        }
        if let Some(deadline) = deadline {
            deadline.set(new_deadline);
        }
        Ok(rows)
    }

    /// `Line`: the row's one sentence. The granted line carries the remaining count and the expiry; the cooldown lines the next
    /// time (and the at-wall one the weekly count).
    pub fn line(row: &AccountResetEntitlement) -> String {
        let cedar = row.program == ResetProgram::CEDAR_EMBER;
        match row.state.as_str() {
            ResetState::AVAILABLE if cedar => strings::get_with(
                row.copy_key(),
                &[("count", &row.remaining_count.unwrap_or(0).to_string()), ("expiry", &day(row.expires_at.as_deref()))],
            ),
            ResetState::COOLDOWN if cedar => strings::get_with(row.copy_key(), &[("time", &moment(row.next_available_at.as_deref()))]),
            ResetState::COOLDOWN => strings::get_with(
                row.copy_key(),
                &[("time", &moment(row.next_available_at.as_deref())), ("count", &row.resets_per_week.unwrap_or(0).to_string())],
            ),
            _ => strings::get(row.copy_key()),
        }
    }

    /// `Render`.
    pub fn render(row: &AccountResetEntitlement) -> AccountResetRow {
        AccountResetRow {
            program: row.program.clone(),
            state: row.state.clone(),
            label: strings::get(ResetProgram::label_key(&row.program)),
            text: Self::line(row),
        }
    }

    /// `Rows`: the rows the window draws. While the direct-lookup switch is off there is nothing to draw; with it on but nothing read
    /// yet every programme reads unknown.
    pub fn rows(snapshot: Option<&AccountUsageSnapshot>, direct_lookup_enabled: bool) -> Vec<AccountResetRow> {
        if !direct_lookup_enabled {
            return vec![];
        }
        let read = snapshot.map(|s| s.resets.as_slice()).unwrap_or(&[]);
        ResetProgram::ALL
            .iter()
            .map(|program| Self::render(&read.iter().find(|r| r.program == *program).cloned().unwrap_or_else(|| Self::unknown(program))))
            .collect()
    }
}

/// `Day`: the local date in Korean's short form, as C# writes it whatever the app's language.
fn day(value: Option<&str>) -> String {
    date(value).map(|d| super::presentation::short_date(d, "ko")).unwrap_or_default()
}

/// `Moment`: the local time in Korean's short form.
fn moment(value: Option<&str>) -> String {
    date(value).map(|d| super::presentation::short_time(d, "ko")).unwrap_or_default()
}

// ── the smoke ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `AccountResetSmokeResult`. `fake_transport_post_count` is part of the contract: no POST exists in this app at all.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AccountResetSmokeResult {
    pub passed: bool,
    pub fake_transport_post_count: usize,
    pub cedar_ember_state: String,
    pub juniper_tide_state: String,
    pub requests: Vec<String>,
    pub lines: Vec<String>,
    /// The rows themselves, so the smoke can draw them with the real builder.
    pub rows: Vec<AccountResetRow>,
}

/// C# `ClaudeResetSmoke`: the probe driven with a fixture clock and a fake transport that only ever sees GET; cedar_ember renders
/// available, juniper_tide (answered 503) unknown, whatever the direct-lookup switch says and without writing any setting.
pub mod claude_reset_smoke {
    use super::*;

    /// `CedarAvailableBody`: the CLI 2.1.280 decoder's shape; the values are a fixture whose grant closes well after the clock.
    pub const CEDAR_AVAILABLE_BODY: &str = r#"{"cedar_ember":{"eligible":true,"in_experiment":true,"ineligible_reason":null,"next_grant_id":"grant-fixture","grants":[{"id":"grant-fixture","resets_total":3,"resets_left":2,"starts_at":"2026-09-01T00:00:00Z","ends_at":"2026-12-31T00:00:00Z","usable_now":true,"use_requires_limit":false,"paused":false,"percent_used":33.3}],"at_limit":false,"cooldown_until":null,"exhausted":[]}}"#;
    const USAGE_BODY: &str = r#"{"five_hour":{"utilization":12.5,"resets_at":"2026-09-23T20:00:00Z"}}"#;
    /// A made-up sign-in value; not a real token, and it never leaves the fixture.
    pub const FIXTURE_TOKEN: &str = "smoke-fixture-not-a-real-token";
    /// The expected GETs, in order: usage, profile, then the two entitlement variants, each with `skip_spend=1`.
    pub const EXPECTED_REQUESTS: [&str; 4] = [
        "GET /api/oauth/usage",
        "GET /api/oauth/profile",
        "GET /api/oauth/usage?cedar_ember=1&skip_spend=1",
        "GET /api/oauth/usage?at_wall=1&skip_spend=1",
    ];

    /// `FixtureClock`: every comparison in the run reads this instant.
    pub fn fixture_clock() -> DateTime<Utc> {
        DateTime::parse_from_rfc3339("2026-09-23T10:00:00Z").map(|d| d.with_timezone(&Utc)).unwrap_or_default()
    }

    /// `RunAsync`.
    pub async fn run() -> AccountResetSmokeResult {
        let seen: Mutex<(Vec<String>, usize)> = Mutex::new((vec![], 0));
        let http = HttpFn(|request: &AccountUsageHttpRequest| {
            guard(&request.url).map_err(HttpFailure::Refused)?;
            let mut seen = lock(&seen);
            seen.0.push(format!("{} {}", request.method, request.path_and_query()));
            if request.method != "GET" {
                seen.1 += 1;
            }
            Ok(match request.url.query() {
                Some("cedar_ember=1&skip_spend=1") => AccountUsageHttpResponse::new(200, CEDAR_AVAILABLE_BODY),
                // A 5xx on an entitlement GET leaves that row unknown and never disturbs the base usage windows.
                Some("at_wall=1&skip_spend=1") => AccountUsageHttpResponse::new(503, ""),
                _ => AccountUsageHttpResponse::new(200, if request.url.path().ends_with("profile") { "{}" } else { USAGE_BODY }),
            })
        });
        let deadline = ResetReadDeadlineBox::new();
        let log = AccountUsageShapeLog::new(|_| {});
        let snapshot = ClaudeAccountProbe::read(
            &Default::default(),
            || Some(ClaudeQuotaCredential { token: FIXTURE_TOKEN.into(), plan: Some("max".into()) }),
            &http,
            &fixture_clock,
            Some(&deadline),
            Some(&log),
        )
        .await
        .ok();
        // The rows render with the switch on because this service is injected; the switch itself is never touched.
        let rows = ClaudeResetEntitlements::rows(snapshot.as_ref(), true);
        let state = |program: &str| {
            rows.iter().find(|r| r.program == program).map(|r| r.state.clone()).unwrap_or_else(|| ResetState::UNKNOWN.into())
        };
        let (cedar, juniper) = (state(ResetProgram::CEDAR_EMBER), state(ResetProgram::JUNIPER_TIDE));
        let lines: Vec<String> = rows.iter().map(|r| r.text.clone()).collect();
        let (requests, posts) = lock(&seen).clone();
        let resolved = lines.len() == 2 && lines.iter().all(|l| !l.is_empty() && !l.starts_with("usage.reset."));
        let passed = posts == 0
            && cedar == ResetState::AVAILABLE
            && juniper == ResetState::UNKNOWN
            && requests.iter().map(String::as_str).eq(EXPECTED_REQUESTS)
            && resolved;
        AccountResetSmokeResult {
            passed,
            fake_transport_post_count: posts,
            cedar_ember_state: cedar,
            juniper_tide_state: juniper,
            requests,
            lines,
            rows,
        }
    }
}
