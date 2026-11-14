//! C# `CliAutoUpdateSchedule`: an automatic pass every 6 hours (checked each minute), a provider updated only after 3 idle minutes,
//! a 5-minute budget for plugins, and the providers a busy pass skipped retried once they are ready.

use std::collections::BTreeSet;
use std::time::Duration;

use chrono::{DateTime, Utc};

#[derive(Debug, Default, Clone)]
pub struct CliAutoUpdateSchedule {
    deferred: BTreeSet<String>,
    last_pass: Option<DateTime<Utc>>,
}

impl CliAutoUpdateSchedule {
    pub const INTERVAL: Duration = Duration::from_secs(6 * 60 * 60);
    pub const TICK: Duration = Duration::from_secs(60);
    pub const IDLE_DELAY: Duration = Duration::from_secs(3 * 60);
    pub const PLUGIN_BUDGET: Duration = Duration::from_secs(5 * 60);

    pub fn new() -> CliAutoUpdateSchedule {
        CliAutoUpdateSchedule::default()
    }

    pub fn last_pass(&self) -> Option<DateTime<Utc>> {
        self.last_pass
    }

    pub fn is_due(&self, now: DateTime<Utc>, enabled: bool) -> bool {
        enabled && self.last_pass.is_none_or(|last| elapsed(last, now) >= Self::INTERVAL)
    }

    pub fn pass_started(&mut self, now: DateTime<Utc>) {
        self.last_pass = Some(now);
    }

    pub fn skipped_busy(&mut self, provider: &str) {
        self.deferred.insert(provider.to_string());
    }

    pub fn updated(&mut self, provider: &str) {
        self.deferred.remove(provider);
    }

    /// Not running or starting, nothing queued, and the provider's last activity at least [`Self::IDLE_DELAY`] ago.
    pub fn idle_long_enough(busy: bool, queued: bool, last_active: Option<DateTime<Utc>>, now: DateTime<Utc>) -> bool {
        !busy && !queued && last_active.is_none_or(|last| elapsed(last, now) >= Self::IDLE_DELAY)
    }

    /// The deferred providers that are ready now, in ordinal order, taken off the list; with both preferences off the list is
    /// forgotten.
    pub fn due_retries(&mut self, enabled: bool, ready: impl Fn(&str) -> bool) -> Vec<String> {
        if !enabled {
            self.deferred.clear();
            return Vec::new();
        }
        let due: Vec<String> = self.deferred.iter().filter(|provider| ready(provider)).cloned().collect();
        for provider in &due {
            self.deferred.remove(provider);
        }
        due
    }
}

/// `now - then` (negative reads as zero, which no comparison here lets through).
fn elapsed(then: DateTime<Utc>, now: DateTime<Utc>) -> Duration {
    (now - then).to_std().unwrap_or(Duration::ZERO)
}
