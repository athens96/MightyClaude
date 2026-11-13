//! C# `AgentRunTiming` (AgentRunTiming.cs): a run's start, the last time it was seen working and its end. The record and its JSON are
//! mighty-core's; these are the steps a run takes it through.

use mighty_core::model::AgentRunTiming;
use mighty_core::time::DateTimeOffset;

use crate::strings;

/// `AgentRunTiming.Begin(at)`.
pub fn begin(at: Option<DateTimeOffset>) -> AgentRunTiming {
    let now = at.unwrap_or_else(DateTimeOffset::now);
    AgentRunTiming { started_at: now, last_observed_at: now, finished_at: None, is_approximate: false }
}

/// `AgentRunTiming.Observe(at)`.
pub fn observe(timing: AgentRunTiming, at: Option<DateTimeOffset>) -> AgentRunTiming {
    timing.observe(at)
}

/// `AgentRunTiming.Finish(at)`: observed once more, and finished where it was last seen.
pub fn finish(timing: AgentRunTiming, at: Option<DateTimeOffset>) -> AgentRunTiming {
    if timing.finished_at.is_some() {
        return timing;
    }
    let value = timing.observe(at);
    AgentRunTiming { finished_at: Some(value.last_observed_at), ..value }
}

/// `AgentRunTiming.Elapsed(at)`, in seconds.
pub fn elapsed(timing: &AgentRunTiming, at: Option<DateTimeOffset>) -> f64 {
    if !timing.is_valid() {
        return 0.0;
    }
    let end = timing.finished_at.or(at).unwrap_or_else(DateTimeOffset::now);
    ((end.utc_ticks() - timing.started_at.utc_ticks()) as f64 / 10_000_000.0).max(0.0)
}

/// `AgentRunTiming.Label(at)`: `mm:ss`, or `h:mm:ss` past an hour; an approximate one in the dashboard's words.
pub fn label(timing: &AgentRunTiming, at: Option<DateTimeOffset>) -> String {
    let seconds = elapsed(timing, at) as i64;
    let text = if seconds >= 3600 {
        format!("{}:{:02}:{:02}", seconds / 3600, seconds / 60 % 60, seconds % 60)
    } else {
        format!("{:02}:{:02}", seconds / 60, seconds % 60)
    };
    if timing.is_approximate { strings::get("dashboard.card.approximate").replace("{time}", &text) } else { text }
}
