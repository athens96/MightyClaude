//! The rest of C# `WorkDashboard` (WorkDashboard.cs): the counts and ages the macOS / phone work dashboard reads. Its card, badges, tones
//! and ordering are mighty-core's (`shell::make_card`, `badges`, `Tone`, `ordered`), which the frame model already uses; unknown usage
//! stays unknown.

use mighty_core::model::RunSession;
use mighty_core::permission::ToolPermissionRequest;
use mighty_core::shell::{Attention, is_counted};
use mighty_core::time::DateTimeOffset;

/// `WorkDashboard.Stats(Running, Waiting, Done)`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Stats {
    pub running: usize,
    pub waiting: usize,
    pub done: usize,
}

/// `WorkDashboard.Pending`: a pane's waiting questions and permissions.
pub fn pending(requests: &[ToolPermissionRequest]) -> Attention {
    Attention {
        questions: requests.iter().filter(|r| r.state == "pending" && r.can_answer_questions).count(),
        permissions: requests.iter().filter(|r| r.state == "pending" && !r.can_answer_questions).count(),
    }
}

/// `WorkDashboard.Pending(requests, antigravity)`: a pane's Antigravity card is one more permission request, as the phone counts it.
pub fn pending_with_card(requests: &[ToolPermissionRequest], antigravity_card: bool) -> Attention {
    let mut value = pending(requests);
    if antigravity_card {
        value.permissions += 1;
    }
    value
}

/// `WorkDashboard.Count`: a waiting pane also counts as running, whereas the waiting number counts individual requests, not panes.
pub fn count<'a>(sessions: impl IntoIterator<Item = &'a RunSession>, attention: &dyn Fn(&str) -> Attention) -> Stats {
    let mut stats = Stats::default();
    for session in sessions.into_iter().filter(|s| is_counted(s.kind())) {
        if session.status() == "running" {
            stats.running += 1;
        }
        stats.waiting += attention(session.id()).total();
        if session.status() == "completed" {
            stats.done += 1;
        }
    }
    stats
}

/// `WorkDashboard.Age(Unit, Count)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Age {
    pub unit: &'static str,
    pub count: i64,
}

/// `WorkDashboard.Since`: the largest whole unit; a time in the future reads "now".
pub fn since(date: DateTimeOffset, now: DateTimeOffset) -> Age {
    let minutes = ((now.utc_ticks() - date.utc_ticks()) as f64 / 600_000_000.0).clamp(0.0, i32::MAX as f64) as i64;
    match minutes {
        m if m < 1 => Age { unit: "now", count: 0 },
        m if m < 60 => Age { unit: "minutes", count: m },
        m if m < 1440 => Age { unit: "hours", count: m / 60 },
        m => Age { unit: "days", count: m / 1440 },
    }
}
