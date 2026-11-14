//! Account usage, `usageRefresh` and `usageDirect` (C# MainWindow.AccountUsage.cs, MainWindow.WebShell.Shell.cs:422-452, :508-511).
//! Owner: W6-C.
//!
//! The window's `AccountUsageStatus` (mighty-accounts): the frame model reads it on every pass ([`shell`], with the state locked) —
//! the status bar's chips, the usage popover and the dashboard's bars — and each pass gives it the state (`Update`), which queues one
//! read when the providers with an AI pane changed. A read runs off the state's lock, one at a time (`QueueAccountUsageRefresh`), and
//! never in a smoke. Codex is asked through its app-server and Antigravity through `agy -p /usage` (`ProcessHost`); Claude only once
//! the user switched the direct lookup on, through the HTTPS GET port behind mighty-accounts' guard (`api.anthropic.com`, redirects
//! refused).

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use mighty_accounts::usage::{
    self, AccountUsageService, AccountUsageStatus, ClaudeResetEntitlements, Finder, RuntimeProbe, copy, leading, percent, window_label,
};
use mighty_core::model::AppSnapshot;
use mighty_core::shell::{
    UsageBar, UsageBars, UsageCard, UsageChip, UsageDetails, UsageDirect, UsageModel, UsageReset, UsageResetRow, UsageWindowRow, UsageWord,
};
use mighty_run::{providers, strings};
use tauri::Manager as _;

use crate::host::Host;

/// The window's account usage (C# `usage`, `usageRefreshQueued`).
pub struct Usage {
    pub status: AccountUsageStatus,
    queued: AtomicBool,
}

/// The app's probe (C# `AccountUsageRuntime.Probe`): the CLIs found through the run engine's catalog, its CLI environment, the user's
/// home, the temporary folder, the host's processes and the HTTPS GET port, each looked up when a read starts.
fn probe(host: &Host) -> usage::UsageProbe {
    let app = host.app.clone();
    let find: Finder = {
        let app = app.clone();
        Arc::new(move |provider: String| {
            let catalog = app.try_state::<Arc<Host>>().and_then(|h| h.runs().map(|r| r.catalog.clone()));
            Box::pin(async move {
                match catalog {
                    Some(catalog) => catalog.find(&provider).await,
                    None => None,
                }
            })
        })
    };
    let environment = {
        let app = app.clone();
        Arc::new(move || {
            app.try_state::<Arc<Host>>()
                .and_then(|h| h.runs().map(|r| r.catalog.environment().current().values))
                .unwrap_or_else(|| std::env::vars().collect())
        })
    };
    usage::runtime_probe(RuntimeProbe {
        find,
        environment,
        home: Arc::new(crate::accounts::home),
        working_directory: std::env::temp_dir(),
        processes: host.processes.clone(),
        http: Arc::new(AppHttps(app)),
    })
}

/// The HTTPS GET port as the app manages it, looked up when a read asks: the ports are managed after the frame's clocks first read
/// the usage. None yet: refused, so nothing leaves.
struct AppHttps(tauri::AppHandle);

impl mighty_platform::HttpsGet for AppHttps {
    fn get(
        &self,
        request: mighty_platform::http::HttpsRequest,
        max_body: usize,
    ) -> mighty_platform::BoxFuture<'_, mighty_platform::Result<mighty_platform::http::HttpsResponse>> {
        let https = self.0.try_state::<crate::platform::Ports>().map(|ports| ports.https.clone());
        Box::pin(async move {
            match https {
                Some(https) => https.get(request, max_body).await,
                None => Err(mighty_platform::PlatformError::unsupported("https", "get")),
            }
        })
    }
}

/// The window's usage, made on first use.
pub fn of(host: &Host) -> Arc<Usage> {
    if let Some(usage) = host.app.try_state::<Arc<Usage>>() {
        return usage.inner().clone();
    }
    let clock = usage::system_clock();
    let service = AccountUsageService::new(probe(host), clock.clone());
    // Managed once: a second maker (two passes at once) leaves the first one's in place.
    host.app.manage(Arc::new(Usage { status: AccountUsageStatus::new(service, clock), queued: AtomicBool::new(false) }));
    host.app.state::<Arc<Usage>>().inner().clone()
}

/// `UsageChipWords`: when the chip's text is exactly its leading windows, each window is its own word in its own ink; otherwise the
/// chip's one text with its own warning.
fn chip_words(status: &AccountUsageStatus, provider: &str, text: &str, warning: bool) -> Vec<UsageWord> {
    if let Some(snapshot) = status.snapshot(provider).filter(|s| !s.windows.is_empty()) {
        let words: Vec<UsageWord> = leading(&snapshot.windows)
            .iter()
            .map(|w| UsageWord { text: format!("{} {}%", window_label(&w.kind), percent(w.used_percent)), warning: w.used_percent >= 90.0 })
            .collect();
        if !words.is_empty() && words.iter().map(|w| w.text.as_str()).collect::<Vec<_>>().join(" ") == text {
            return words;
        }
    }
    vec![UsageWord { text: text.to_string(), warning }]
}

/// The frame model's usage parts (C# `RenderAccountUsage` then `status.chips`, `WebUsageDetails` and the dashboard's bars), with the
/// state locked: the state given to the usage first (`Update`), a read queued when its providers changed.
pub fn shell(host: &Host, snapshot: &AppSnapshot) -> Option<UsageModel> {
    // The frame pass also keeps the sign-in cards' retries to live panes (accounts.rs, C# `RefreshLoginCards`).
    crate::accounts::prune(host, snapshot);
    let usage = of(host);
    if usage.status.update(snapshot) {
        queue(host, &usage, false);
    }
    let status = &usage.status;
    let providers_shown = status.providers();
    let chips = (!providers_shown.is_empty()).then(|| {
        status
            .chips()
            .into_iter()
            .map(|chip| UsageChip {
                name: format!("{} {}", providers::name(&chip.provider), chip.text),
                words: chip_words(status, &chip.provider, &chip.text, chip.warning),
                provider: chip.provider,
            })
            .collect()
    });
    let reset_rows = status.reset_rows();
    let cards = status
        .cards()
        .into_iter()
        .map(|card| UsageCard {
            name: strings::provider_label(&card.provider).to_string(),
            title: card.title,
            beta: providers::is_beta(&card.provider),
            account: card.account,
            windows: card
                .windows
                .into_iter()
                .map(|w| UsageWindowRow { label: w.label, used: w.used, fraction: w.fraction, reset: w.reset, warning: w.warning })
                .collect(),
            detail: card.detail,
            note: card.note,
            checked_at: card.checked_at,
            reset: (card.provider == "claude" && !reset_rows.is_empty()).then(|| UsageReset {
                title: ClaudeResetEntitlements::title(),
                rows: reset_rows
                    .iter()
                    .map(|r| UsageResetRow { program: r.program.clone(), label: r.label.clone(), text: r.text.clone() })
                    .collect(),
                link: ClaudeResetEntitlements::link_label(),
                url: ClaudeResetEntitlements::LINK_TARGET.to_string(),
            }),
            provider: card.provider,
        })
        .collect();
    let details = UsageDetails {
        title: copy::title(),
        refreshing: status.refreshing() || usage.queued.load(Ordering::SeqCst),
        refresh_label: copy::refresh_accessibility_label(),
        refresh_tooltip: copy::refresh_tooltip(),
        cards,
        direct: providers_shown.iter().any(|p| p == "claude").then(|| UsageDirect {
            label: copy::toggle_label(),
            description: copy::toggle_description(),
            on: status.direct_claude_lookup_enabled(),
        }),
        shared: copy::shared_limits_note(),
    };
    let bars = status
        .leading_bars()
        .into_iter()
        .map(|row| UsageBars {
            name: strings::provider_label(&row.provider).to_string(),
            provider: row.provider,
            bars: row
                .bars
                .into_iter()
                .map(|b| UsageBar { label: b.label, percent: b.percent, fraction: b.fraction, warning: b.warning })
                .collect(),
        })
        .collect();
    Some(UsageModel { chips, details, bars })
}

/// `QueueAccountUsageRefresh`: one read of every target at a time, off the state's lock; none in a smoke, while one is queued, or
/// with nothing to ask (Claude is among the targets only once the direct lookup is on).
fn queue(host: &Host, usage: &Arc<Usage>, force: bool) {
    if host.smoke.is_some() || host.closing() || usage.status.targets().is_empty() || usage.queued.swap(true, Ordering::SeqCst) {
        return;
    }
    let usage = usage.clone();
    tauri::async_runtime::spawn(async move {
        usage.status.refresh(force).await;
        usage.queued.store(false, Ordering::SeqCst);
    });
}

/// `usageRefresh{force}`: C# `QueueAccountUsageRefresh(force)` (the popover opening, its refresh button).
pub fn refresh(host: &Arc<Host>, force: bool) {
    queue(host, &of(host), force);
}

/// `usageDirect{on}`: C# `SetUsageDirectLookup(on)` — the direct HTTPS lookup switched: the status told, the state saved (written only
/// with `--allow-state-write`), and read at once when on.
pub async fn direct(host: &Arc<Host>, on: bool) -> Result<(), String> {
    let usage = of(host);
    if usage.status.direct_claude_lookup_enabled() == on {
        return Ok(());
    }
    usage.status.set_direct_claude_lookup(on);
    host.view().update(|state| state.claude_direct_usage_lookup_enabled = on)?;
    host.schedule();
    if on {
        queue(host, &usage, true);
    }
    Ok(())
}

/// `ShutdownAccountUsageAsync`: closing the app cancels the pending reads and drops every value.
#[allow(dead_code, reason = "the integrator's shutdown hook (main.rs is W6-A's) calls it")]
pub async fn shutdown(host: &Host) {
    if let Some(usage) = host.app.try_state::<Arc<Usage>>().map(|u| u.inner().clone()) {
        usage.status.close().await;
    }
}
