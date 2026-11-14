//! The account usage state behind the status bar, the popover and the dashboard's usage card (C# `AccountUsageStatus` and its
//! rows, AccountUsagePresentation.cs). All of it is here so a check drives it; the host only draws these rows and forwards the
//! click, the refresh and the switch.

use std::collections::{BTreeMap, HashMap, HashSet};
use std::sync::{Arc, Mutex};

use chrono::{DateTime, Local, Timelike, Utc};
use mighty_core::model::AppSnapshot;
use mighty_core::{normalize, wire};

use super::{
    AccountResetRow, AccountUsageService, AccountUsageSnapshot, AccountUsageWindow, ClaudeResetEntitlements, Clock, copy, date, grouped,
    percent, window, window_label,
};
use crate::lock;

/// `AccountUsageChip`: one chip in the bottom status bar; `warning` marks a window at 90% or more.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AccountUsageChip {
    pub provider: String,
    pub text: String,
    pub warning: bool,
}

/// `AccountUsageWindowRow`: one window of a popover card.
#[derive(Debug, Clone, PartialEq)]
pub struct AccountUsageWindowRow {
    pub label: String,
    pub used: String,
    pub fraction: f64,
    pub reset: Option<String>,
    pub warning: bool,
}

/// `AccountUsageBar`: one bar of the dashboard's usage card — a leading window's label, its percent worded as the chips word it,
/// the fill, and whether it is at 90% or more.
#[derive(Debug, Clone, PartialEq)]
pub struct AccountUsageBar {
    pub label: String,
    pub percent: String,
    pub fraction: f64,
    pub warning: bool,
}

/// `AccountUsageBars`: a provider's leading bars on the dashboard: the session window, then the weekly one.
#[derive(Debug, Clone, PartialEq)]
pub struct AccountUsageBars {
    pub provider: String,
    pub bars: Vec<AccountUsageBar>,
}

/// `AccountUsageCard`: one provider card in the popover.
#[derive(Debug, Clone, PartialEq)]
pub struct AccountUsageCard {
    pub provider: String,
    pub title: String,
    pub account: Option<String>,
    pub windows: Vec<AccountUsageWindowRow>,
    pub detail: Option<String>,
    pub checked_at: Option<String>,
    pub note: Option<String>,
}

/// `AccountUsageSmokeOutcome`: what the smoke records under [`AccountUsageSmokeOutcome::RESULT_KEY`] after driving the real chips
/// and popover with fixture data — counts and copy only, never an account label, a plan or anything read from a credential.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct AccountUsageSmokeOutcome {
    pub chips: i64,
    pub cards: i64,
    pub window_rows: i64,
    pub direct_lookup_default_off: bool,
    pub claude_chip: String,
    pub gemini_note: String,
    pub restored: bool,
}

impl AccountUsageSmokeOutcome {
    pub const RESULT_KEY: &'static str = "accountUsage";

    /// The JSON the smoke writes (C#'s `JsonPropertyName`s, in order).
    pub fn json(&self) -> String {
        let mut out = mighty_core::json::JsonOut::new();
        out.begin_object();
        out.prop_int("chips", self.chips);
        out.prop_int("cards", self.cards);
        out.prop_int("windowRows", self.window_rows);
        out.prop_bool("directLookupDefaultOff", self.direct_lookup_default_off);
        out.prop_str("claudeChip", Some(&self.claude_chip));
        out.prop_str("geminiNote", Some(&self.gemini_note));
        out.prop_bool("restored", self.restored);
        out.end_object();
        out.finish()
    }
}

// ── the cultures' dates ───────────────────────────────────────────────────────────────────────────────────────────────────

/// `DisplayCulture`: the app's language's culture (ko-KR, en-US, zh-CN, ja-JP).
fn display_language() -> &'static str {
    mighty_run::strings::locale().language()
}

fn local(at: DateTime<Utc>) -> DateTime<Local> {
    at.with_timezone(&Local)
}

/// `ToString("t", culture)`, as .NET 10 writes each culture's short time (ko-KR `tt h:mm`, en-US `h:mm tt`, zh-CN `HH:mm`, ja-JP
/// `H:mm`).
pub(crate) fn short_time(at: DateTime<Utc>, language: &str) -> String {
    let at = local(at);
    let (pm, hour12) = at.hour12();
    match language {
        "ko" => format!("{} {hour12}:{:02}", if pm { "오후" } else { "오전" }, at.minute()),
        "zh" => at.format("%H:%M").to_string(),
        "ja" => format!("{}:{:02}", at.hour(), at.minute()),
        _ => format!("{hour12}:{:02} {}", at.minute(), if pm { "PM" } else { "AM" }),
    }
}

/// `ToString("d", culture)` (ko-KR `yyyy. M. d.`, en-US `M/d/yyyy`, zh-CN `yyyy/M/d`, ja-JP `yyyy/MM/dd`).
pub(crate) fn short_date(at: DateTime<Utc>, language: &str) -> String {
    let at = local(at);
    match language {
        "ko" => at.format("%Y. %-m. %-d.").to_string(),
        "zh" => at.format("%Y/%-m/%-d").to_string(),
        "ja" => at.format("%Y/%m/%d").to_string(),
        _ => at.format("%-m/%-d/%Y").to_string(),
    }
}

/// `ToString("g", culture)`: the short date, a space, the short time.
pub(crate) fn short_date_time(at: DateTime<Utc>, language: &str) -> String {
    format!("{} {}", short_date(at, language), short_time(at, language))
}

// ── the projections ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// `LocalProviders`: the providers with an AI pane in a registered workspace, in the provider order. A shell, files or work-map pane
/// has none.
pub fn local_providers(snapshot: &AppSnapshot) -> Vec<String> {
    let ids: HashSet<&str> = snapshot.workspaces.iter().flatten().map(|w| w.id()).collect();
    let mut found: Vec<&str> = Vec::new();
    for session in snapshot.sessions.iter().flatten() {
        let provider = session.provider();
        if session.kind() != "shell"
            && !normalize::is_files(session.kind())
            && !normalize::is_work_map(session.kind())
            && ids.contains(session.workspace_id())
            && wire::PROVIDERS.contains(&provider)
            && !found.contains(&provider)
        {
            found.push(provider);
        }
    }
    found.sort_by_key(|p| wire::PROVIDERS.iter().position(|known| known == p));
    found.into_iter().map(str::to_string).collect()
}

/// `SessionReported`: the limits the CLI reported during a run, newest per provider — what Claude shows while the direct lookup is
/// off, and newer than any polled read. Over five minutes old reads stale.
pub fn session_reported(snapshot: &AppSnapshot, now: DateTime<Utc>) -> BTreeMap<String, AccountUsageSnapshot> {
    let ids: HashSet<&str> = snapshot.workspaces.iter().flatten().map(|w| w.id()).collect();
    let mut found: BTreeMap<String, AccountUsageSnapshot> = BTreeMap::new();
    for session in snapshot.sessions.iter().flatten().filter(|s| ids.contains(s.workspace_id())) {
        let Some(usage) = session.session_usage.as_ref().and_then(mighty_run::usage::normalized) else { continue };
        if usage.provider != session.provider() {
            continue;
        }
        let Some(limits) = usage.rate_limits.as_ref().filter(|l| !l.is_empty()) else { continue };
        let windows: Vec<AccountUsageWindow> = limits
            .iter()
            .filter_map(|limit| {
                Some(AccountUsageWindow {
                    kind: limit.kind.clone(),
                    used_percent: limit.percent_used?,
                    resets_at: limit.resets_at.clone(),
                    window_minutes: None,
                })
            })
            .collect();
        let Some(stamp) = date(usage.rate_limits_updated_at.as_deref()).filter(|_| !windows.is_empty()) else { continue };
        if let Some(older) = found.get(session.provider()).and_then(|existing| date(existing.fetched_at.as_deref()))
            && older >= stamp
        {
            continue;
        }
        let stale = (now - stamp).num_milliseconds() > 300_000;
        found.insert(
            session.provider().to_string(),
            AccountUsageSnapshot {
                windows,
                fetched_at: usage.rate_limits_updated_at.clone(),
                status: if stale { "stale" } else { "available" }.into(),
                detail: if stale { copy::detail_session_reported_stale() } else { copy::detail_session_reported() },
                ..AccountUsageSnapshot::new(session.provider())
            },
        );
    }
    found
}

/// Ranked by what the window is (its window part for a grouped one), not by its label in the current language.
fn rank(kind: &str) -> u8 {
    match window(kind).to_lowercase().as_str() {
        "session" | "five_hour" | "5h" | "primary" => 0,
        "weekly" | "seven_day" | "7d" | "secondary" => 1,
        _ => 2,
    }
}

/// `Leading`: the two windows worth a chip — the session window, then the weekly one. Grouped windows (Antigravity's model groups)
/// give the most-used group of each, so the chip shows the limit that runs out first; the popover lists every group. Never the
/// spend limit.
pub fn leading(windows: &[AccountUsageWindow]) -> Vec<AccountUsageWindow> {
    let mut ranked: Vec<(usize, &AccountUsageWindow)> = windows.iter().enumerate().filter(|(_, w)| w.kind != "spend_limit").collect();
    ranked.sort_by(|(ia, a), (ib, b)| {
        rank(&a.kind)
            .cmp(&rank(&b.kind))
            .then_with(|| {
                if grouped(&a.kind) && grouped(&b.kind) && a.used_percent != b.used_percent {
                    b.used_percent.total_cmp(&a.used_percent)
                } else {
                    std::cmp::Ordering::Equal
                }
            })
            .then(ia.cmp(ib))
    });
    let mut seen = HashSet::new();
    ranked
        .into_iter()
        .map(|(_, w)| w)
        .filter(|w| !grouped(&w.kind) || seen.insert(window_label(window(&w.kind))))
        .take(2)
        .cloned()
        .collect()
}

/// `LeadingBars(providers)`: each provider with leading windows, and its bars; a provider with none gets no row.
pub fn leading_bars_of(providers: &[(String, Vec<AccountUsageWindow>)]) -> Vec<AccountUsageBars> {
    providers
        .iter()
        .map(|(provider, windows)| AccountUsageBars {
            provider: provider.clone(),
            bars: leading(windows)
                .iter()
                .map(|w| AccountUsageBar {
                    label: window_label(&w.kind),
                    percent: format!("{}%", percent(w.used_percent)),
                    fraction: (w.used_percent / 100.0).clamp(0.0, 1.0),
                    warning: w.used_percent >= 90.0,
                })
                .collect(),
        })
        .filter(|row| !row.bars.is_empty())
        .collect()
}

/// `IsSessionReport`: a detail only a session's report carries (in English or Korean, as C# compares it).
fn is_session_report(detail: &str) -> bool {
    ["en", "ko"].iter().any(|language| {
        let catalogue = mighty_core::locale::catalogue(language);
        ["windows.accountUsage.detailSessionReported", "windows.accountUsage.detailSessionReportedStale"]
            .iter()
            .any(|key| catalogue.get(*key).is_some_and(|text| text == detail))
    })
}

#[derive(Default)]
struct StatusState {
    providers: Vec<String>,
    refreshing: bool,
    direct: bool,
    snapshots: HashMap<String, AccountUsageSnapshot>,
    stopped: bool,
}

/// C# `AccountUsageStatus`: the providers with a chip, what each last read, and the direct Claude lookup's switch (off out of
/// the box, so nothing is looked up directly).
pub struct AccountUsageStatus {
    service: AccountUsageService,
    clock: Clock,
    state: Arc<Mutex<StatusState>>,
}

impl AccountUsageStatus {
    pub fn new(service: AccountUsageService, clock: Clock) -> AccountUsageStatus {
        AccountUsageStatus { service, clock, state: Arc::new(Mutex::new(StatusState::default())) }
    }

    pub fn service(&self) -> &AccountUsageService {
        &self.service
    }

    /// `Providers`.
    pub fn providers(&self) -> Vec<String> {
        lock(&self.state).providers.clone()
    }

    /// `Refreshing`.
    pub fn refreshing(&self) -> bool {
        lock(&self.state).refreshing
    }

    /// `DirectClaudeLookupEnabled`: the saved switch.
    pub fn direct_claude_lookup_enabled(&self) -> bool {
        lock(&self.state).direct
    }

    /// `Snapshot`.
    pub fn snapshot(&self, provider: &str) -> Option<AccountUsageSnapshot> {
        lock(&self.state).snapshots.get(provider).cloned()
    }

    /// `Update`: on app start and on every state change (a run event, a pane added, a workspace switched); true when the rows
    /// changed. A session's report never replaces a newer value, and keeps the 리셋권 rows the service read.
    pub fn update(&self, snapshot: &AppSnapshot) -> bool {
        let now = (self.clock)();
        let providers = local_providers(snapshot);
        let mut state = lock(&self.state);
        let mut changed = providers != state.providers;
        state.providers = providers;
        state.direct = snapshot.claude_direct_usage_lookup_enabled;
        for (provider, session) in session_reported(snapshot, now) {
            let held = state.snapshots.get(&provider);
            if let (Some(older), Some(fresh)) = (held.and_then(|h| date(h.fetched_at.as_deref())), date(session.fetched_at.as_deref()))
                && older >= fresh
            {
                continue;
            }
            let reported = AccountUsageSnapshot { resets: held.map(|h| h.resets.clone()).unwrap_or_default(), ..session };
            if held == Some(&reported) {
                continue;
            }
            state.snapshots.insert(provider, reported);
            changed = true;
        }
        changed
    }

    /// `Targets`: only the providers that may be asked right now; Claude is absent until the user switches the direct lookup on.
    pub fn targets(&self) -> Vec<String> {
        let state = lock(&self.state);
        state.providers.iter().filter(|p| *p != "claude" || state.direct).cloned().collect()
    }

    /// `RefreshAsync`: one read per provider, one after the other; a value a running session reported is never replaced by an older
    /// read.
    pub async fn refresh(&self, force: bool) {
        let targets = self.targets();
        {
            let mut state = lock(&self.state);
            if state.stopped || targets.is_empty() || state.refreshing {
                return;
            }
            state.refreshing = true;
        }
        struct Done(Arc<Mutex<StatusState>>);
        impl Drop for Done {
            fn drop(&mut self) {
                lock(&self.0).refreshing = false;
            }
        }
        let _done = Done(self.state.clone());
        for provider in targets {
            if lock(&self.state).stopped {
                break;
            }
            let value = self.service.read(&provider, force).await;
            let mut state = lock(&self.state);
            if let Some(held) = state.snapshots.get(&provider)
                && !held.windows.is_empty()
                && let (Some(older), Some(fresh)) = (date(held.fetched_at.as_deref()), date(value.fetched_at.as_deref()))
                && older > fresh
            {
                continue;
            }
            state.snapshots.insert(provider, value);
        }
    }

    /// `SetDirectClaudeLookup`: turning the switch off drops a value only the direct lookup could have produced.
    pub fn set_direct_claude_lookup(&self, enabled: bool) {
        let mut state = lock(&self.state);
        state.direct = enabled;
        if !enabled && state.snapshots.get("claude").is_some_and(|held| !is_session_report(&held.detail)) {
            state.snapshots.remove("claude");
        }
    }

    /// `LeadingBars()`: the dashboard usage card's rows.
    pub fn leading_bars(&self) -> Vec<AccountUsageBars> {
        let state = lock(&self.state);
        let rows: Vec<(String, Vec<AccountUsageWindow>)> =
            state.providers.iter().map(|p| (p.clone(), state.snapshots.get(p).map(|s| s.windows.clone()).unwrap_or_default())).collect();
        leading_bars_of(&rows)
    }

    /// `Chips`.
    pub fn chips(&self) -> Vec<AccountUsageChip> {
        let state = lock(&self.state);
        state
            .providers
            .iter()
            .map(|provider| {
                if let Some(usage) = state.snapshots.get(provider).filter(|u| !u.windows.is_empty()) {
                    let leading = leading(&usage.windows);
                    let text: Vec<String> =
                        leading.iter().map(|w| format!("{} {}%", window_label(&w.kind), percent(w.used_percent))).collect();
                    return AccountUsageChip {
                        provider: provider.clone(),
                        text: text.join(" "),
                        warning: leading.iter().any(|w| w.used_percent >= 90.0),
                    };
                }
                let text = if provider == "claude" && !state.direct {
                    copy::chip_before_first_run()
                } else if state.refreshing {
                    copy::chip_checking()
                } else {
                    copy::chip_empty()
                };
                AccountUsageChip { provider: provider.clone(), text, warning: false }
            })
            .collect()
    }

    /// `ResetRows`: the Claude card's 리셋권 rows; none while the switch is off.
    pub fn reset_rows(&self) -> Vec<AccountResetRow> {
        let state = lock(&self.state);
        ClaudeResetEntitlements::rows(state.snapshots.get("claude"), state.direct)
    }

    /// `Cards`.
    pub fn cards(&self) -> Vec<AccountUsageCard> {
        let language = display_language();
        let state = lock(&self.state);
        state
            .providers
            .iter()
            .map(|provider| {
                let title = mighty_run::providers::name(provider).to_string();
                let Some(usage) = state.snapshots.get(provider) else {
                    let note = if provider == "claude" && !state.direct {
                        copy::claude_before_first_run_note()
                    } else if state.refreshing {
                        copy::card_checking()
                    } else {
                        copy::card_not_checked_yet()
                    };
                    return AccountUsageCard {
                        provider: provider.clone(),
                        title,
                        account: None,
                        windows: vec![],
                        detail: None,
                        checked_at: None,
                        note: Some(note),
                    };
                };
                let rows: Vec<AccountUsageWindowRow> = usage
                    .windows
                    .iter()
                    .map(|w| {
                        let suffix = match w.window_minutes {
                            Some(300) => copy::window_five_hour_suffix(),
                            Some(10080) => copy::window_seven_day_suffix(),
                            _ => String::new(),
                        };
                        AccountUsageWindowRow {
                            label: format!("{}{suffix}", window_label(&w.kind)),
                            used: copy::used_percent_template().replace("{percent}", &percent(w.used_percent)),
                            fraction: (w.used_percent / 100.0).clamp(0.0, 1.0),
                            reset: date(w.resets_at.as_deref())
                                .map(|at| copy::reset_template().replace("{date}", &short_date_time(at, language))),
                            warning: w.used_percent >= 90.0,
                        }
                    })
                    .collect();
                let account: Vec<&str> =
                    [usage.account_label.as_deref(), usage.plan.as_deref()].into_iter().flatten().filter(|v| !v.is_empty()).collect();
                let checked_at = date(usage.fetched_at.as_deref()).map(|stamp| {
                    let prefix = if matches!(usage.status.as_str(), "error" | "stale") { copy::last_known_prefix() } else { String::new() };
                    format!("{prefix}{}", copy::checked_at_template().replace("{time}", &short_time(stamp, language)))
                });
                let detail = (usage.status != "available" || rows.is_empty()).then(|| usage.detail.clone());
                AccountUsageCard {
                    provider: provider.clone(),
                    title,
                    account: (!account.is_empty()).then(|| account.join(" · ")),
                    windows: rows,
                    detail,
                    checked_at,
                    note: None,
                }
            })
            .collect()
    }

    /// `DisposeAsync`: the service closed (its pending reads cancelled) and every row dropped.
    pub async fn close(&self) {
        lock(&self.state).stopped = true;
        self.service.close().await;
        lock(&self.state).snapshots.clear();
    }
}
