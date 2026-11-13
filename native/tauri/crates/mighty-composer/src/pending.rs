//! Natural submit (C# `PendingDisplay.cs`; `native/contracts/fixtures/pending.json`): when the pending area shows, the submit answers
//! a page or a phone reads (`SubmitResponse`) and the host writes (`SubmitReceipt`), and where a view moves for a new request
//! (`SubmitScroll`).

use mighty_core::time::DateTimeOffset;
use serde::Serialize;
use serde_json::Value;

/// `DesignMetrics.Motion.PendingShowAfter` (design-tokens.json motion.pendingShowAfter), ms.
pub const PENDING_SHOW_AFTER: i64 = 150;
/// `DesignMetrics.Motion.PendingMinVisible`, ms.
pub const PENDING_MIN_VISIBLE: i64 = 300;

/// C# `PendingState` (PendingDisplay.cs:11): where one user action is.
#[derive(Debug, Clone, PartialEq)]
pub enum PendingState {
    Idle,
    Pending { since: DateTimeOffset },
    Done { since: DateTimeOffset, at: DateTimeOffset },
    Failed { message: String, since: DateTimeOffset, at: Option<DateTimeOffset> },
}

/// C# `PendingIndicator` (PendingDisplay.cs:20).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PendingIndicator {
    Hidden,
    Loading,
    Failed(String),
}

/// C# `PendingDisplay.Visible(double, double?, double, bool, int, int)` (PendingDisplay.cs:39): the rule in milliseconds.
pub fn visible_ms(started_at: f64, ended_at: Option<f64>, now: f64, failed: bool, show_after: i64, min_visible: i64) -> bool {
    let shown_at = started_at + show_after as f64;
    if failed {
        return now >= ended_at.unwrap_or(now) || now >= shown_at;
    }
    if now < shown_at {
        return false;
    }
    let Some(ended) = ended_at else { return true };
    if ended <= shown_at {
        return false;
    }
    now < ended || now <= shown_at + min_visible as f64
}

/// [`visible_ms`] with the design tokens' numbers.
pub fn visible(started_at: f64, ended_at: Option<f64>, now: f64, failed: bool) -> bool {
    visible_ms(started_at, ended_at, now, failed, PENDING_SHOW_AFTER, PENDING_MIN_VISIBLE)
}

/// C# `PendingDisplay.Milliseconds`: whole 100 ns ticks, so a boundary named in milliseconds is exact.
fn milliseconds(date: DateTimeOffset, since: DateTimeOffset) -> f64 {
    (date.utc_ticks() - since.utc_ticks()) as f64 / 10_000.0
}

/// C# `PendingDisplay.Indicator` (PendingDisplay.cs:56).
pub fn indicator(state: &PendingState, now: DateTimeOffset) -> PendingIndicator {
    let shows =
        |ended: Option<f64>, elapsed: f64, failed: bool| visible_ms(0.0, ended, elapsed, failed, PENDING_SHOW_AFTER, PENDING_MIN_VISIBLE);
    match state {
        PendingState::Pending { since } => {
            if shows(None, milliseconds(now, *since), false) {
                PendingIndicator::Loading
            } else {
                PendingIndicator::Hidden
            }
        }
        PendingState::Done { since, at } => {
            if shows(Some(milliseconds(*at, *since)), milliseconds(now, *since), false) {
                PendingIndicator::Loading
            } else {
                PendingIndicator::Hidden
            }
        }
        PendingState::Failed { message, since, at } => {
            let elapsed = milliseconds(now, *since);
            let ended = at.map_or(elapsed, |at| milliseconds(at, *since));
            if elapsed >= ended {
                return PendingIndicator::Failed(message.clone());
            }
            if shows(Some(ended), elapsed, true) { PendingIndicator::Loading } else { PendingIndicator::Hidden }
        }
        PendingState::Idle => PendingIndicator::Hidden,
    }
}

/// C# `PendingDisplay.Visible(PendingState, DateTimeOffset)`.
pub fn state_visible(state: &PendingState, now: DateTimeOffset) -> bool {
    indicator(state, now) != PendingIndicator::Hidden
}

/// C# `SubmitOutcome` (PendingDisplay.cs:80).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SubmitOutcome {
    Started,
    Queued,
    Steered,
}

impl SubmitOutcome {
    pub const ALL: [SubmitOutcome; 3] = [SubmitOutcome::Started, SubmitOutcome::Queued, SubmitOutcome::Steered];

    /// C# `SubmitResponse.Word(SubmitOutcome)`.
    pub fn word(self) -> &'static str {
        match self {
            SubmitOutcome::Queued => "queued",
            SubmitOutcome::Steered => "steered",
            SubmitOutcome::Started => "started",
        }
    }

    fn parse(value: Option<&Value>) -> Option<SubmitOutcome> {
        match value?.as_str()? {
            "started" => Some(SubmitOutcome::Started),
            "queued" => Some(SubmitOutcome::Queued),
            "steered" => Some(SubmitOutcome::Steered),
            _ => None,
        }
    }
}

/// C# `SubmitResponse(Accepted, EntryId?, RunId?, Outcome?)` (PendingDisplay.cs:91): a submit answer as a page or a phone reads it.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SubmitResponse {
    pub accepted: bool,
    pub entry_id: Option<String>,
    pub run_id: Option<String>,
    pub outcome: Option<SubmitOutcome>,
}

impl SubmitResponse {
    pub fn not_accepted() -> Self {
        SubmitResponse::default()
    }

    /// C# `SubmitResponse.Parse(JsonElement?)` (PendingDisplay.cs:99).
    pub fn parse(json: Option<&Value>) -> SubmitResponse {
        let Some(Value::Object(body)) = json else { return SubmitResponse::not_accepted() };
        let outcome = SubmitOutcome::parse(body.get("outcome")).or_else(|| SubmitOutcome::parse(body.get("accepted")));
        let accepted_true = body.get("accepted") == Some(&Value::Bool(true));
        let id = |name: &str| body.get(name).and_then(Value::as_str).filter(|s| !s.is_empty()).map(str::to_string);
        SubmitResponse { accepted: outcome.is_some() || accepted_true, entry_id: id("entryId"), run_id: id("runId"), outcome }
    }

    /// C# `SubmitResponse.Parse(string)`: a body that is not JSON reads as not accepted.
    pub fn parse_text(text: &str) -> SubmitResponse {
        match serde_json::from_str::<Value>(text) {
            Ok(value) => SubmitResponse::parse(Some(&value)),
            Err(_) => SubmitResponse::not_accepted(),
        }
    }
}

/// C# `SubmitReceipt(Outcome, EntryId?, RunId?)` (PendingDisplay.cs:145): what a Windows submit did, as the host answers it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SubmitReceipt {
    pub outcome: String,
    pub entry_id: Option<String>,
    pub run_id: Option<String>,
}

/// C# `MobileSubmitReply` (PendingDisplay.cs:166).
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MobileSubmitReply {
    pub protocol: i32,
    pub accepted: String,
    pub outcome: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub entry_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub run_id: Option<String>,
}

/// C# `ComposerSendAnswer` (PendingDisplay.cs:169): the page's `send` answer.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ComposerSendAnswer<S: Serialize> {
    pub accepted: bool,
    pub outcome: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub entry_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub run_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub client_id: Option<String>,
    pub draft: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub state: Option<S>,
}

impl SubmitReceipt {
    pub fn new(outcome: &str) -> Self {
        SubmitReceipt { outcome: outcome.into(), entry_id: None, run_id: None }
    }

    pub fn with_ids(outcome: &str, entry_id: Option<&str>, run_id: Option<&str>) -> Self {
        SubmitReceipt { outcome: outcome.into(), entry_id: entry_id.map(str::to_string), run_id: run_id.map(str::to_string) }
    }

    /// C# `SubmitReceipt.Accepted`.
    pub fn accepted(&self) -> bool {
        matches!(self.outcome.as_str(), "started" | "queued" | "steered" | "worktree" | "answered" | "builtin")
    }

    /// C# `SubmitReceipt.MobileReply`.
    pub fn mobile_reply(&self) -> MobileSubmitReply {
        MobileSubmitReply {
            protocol: 1,
            accepted: self.outcome.clone(),
            outcome: self.outcome.clone(),
            entry_id: self.entry_id.clone(),
            run_id: self.run_id.clone(),
        }
    }

    /// C# `SubmitReceipt.SendAnswer`: the page's own client id is echoed only when it is an identifier.
    pub fn send_answer<S: Serialize>(&self, client_id: Option<&str>, draft: &str, state: Option<S>) -> ComposerSendAnswer<S> {
        ComposerSendAnswer {
            accepted: self.accepted(),
            outcome: self.outcome.clone(),
            entry_id: self.entry_id.clone(),
            run_id: self.run_id.clone(),
            client_id: client_id.filter(|c| mighty_core::wire::identifier(c)).map(str::to_string),
            draft: draft.into(),
            state,
        }
    }
}

/// C# `SubmitScroll` (PendingDisplay.cs:176): where a view moves when a request block appears.
pub mod scroll {
    #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
    pub enum View {
        Basic,
        Diagram,
        Timeline,
        Pipeline,
    }

    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub enum Target {
        ToRequest,
        Follow,
        None,
    }

    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub struct Decision {
        pub scroll: Target,
        pub reset_board: bool,
    }

    /// C# `SubmitScroll.ViewFor`.
    pub fn view_for(shows_mighty: bool, mighty_view_mode: Option<&str>) -> View {
        if !shows_mighty {
            return View::Basic;
        }
        match mighty_core::model::graph_view_mode(mighty_view_mode).as_deref() {
            Some("timeline") => View::Timeline,
            Some("structure") => View::Pipeline,
            _ => View::Diagram,
        }
    }

    /// C# `SubmitScroll.Decide`.
    pub fn decide(view: View, own: bool, chosen_board_is_newest: bool, near_bottom: bool) -> Decision {
        let older_board = view == View::Pipeline && !chosen_board_is_newest;
        if own {
            return Decision { scroll: Target::ToRequest, reset_board: older_board };
        }
        if older_board {
            return Decision { scroll: Target::None, reset_board: false };
        }
        Decision { scroll: if near_bottom { Target::Follow } else { Target::None }, reset_board: false }
    }
}
