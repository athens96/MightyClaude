//! C# `SessionHistoryState` (SessionHistory.cs:991-1155): what a pane loaded from its session record, kept in memory only — never saved
//! with the profile, dropped when the pane closes. The rules live here; the host only runs the file work off the page's thread.

use std::collections::HashSet;

use mighty_core::graph::MightyGraphRun;
use mighty_core::locale::Locale;
use mighty_core::model::RunSession;
use mighty_core::time;

use crate::history::{SessionHistoryAnchor, SessionHistoryChunk, SessionHistoryError, SessionHistoryFile, SessionHistoryRequest};

/// `SessionHistoryState.Phases`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Phase {
    /// More can be loaded.
    Idle,
    Loading,
    /// The record's first request is on screen.
    Start,
    /// No record, or nothing in it before what is already shown.
    Unavailable,
    Failed,
    /// The pane holds as many older requests as it keeps in memory.
    Limit,
}

impl Phase {
    /// `Phase.ToString().ToLowerInvariant()`, as the page's history block reads it.
    pub fn name(self) -> &'static str {
        match self {
            Phase::Idle => "idle",
            Phase::Loading => "loading",
            Phase::Start => "start",
            Phase::Unavailable => "unavailable",
            Phase::Failed => "failed",
            Phase::Limit => "limit",
        }
    }
}

/// Older requests kept per pane, and the bytes they may hold together; past either the oldest go and the pane stops loading.
pub const MAXIMUM_RUNS: usize = 100;
pub const MAXIMUM_BYTES: i64 = 8 * 1_048_576;

#[derive(Debug, Clone)]
pub struct SessionHistoryState {
    pub runs: Vec<MightyGraphRun>,
    pub phase: Phase,
    pub file: Option<SessionHistoryFile>,
    pub end: Option<i64>,
    /// The retained request and session the loaded runs attach above.
    pub anchor_run_id: Option<String>,
    pub resume_id: Option<String>,
    /// The run the diagram keeps where it first stood.
    pub pinned_run_id: Option<String>,
    /// Bumped on every reset so a load that started before it is discarded.
    pub generation: u64,
}

impl Default for SessionHistoryState {
    fn default() -> Self {
        SessionHistoryState {
            runs: vec![],
            phase: Phase::Idle,
            file: None,
            end: None,
            anchor_run_id: None,
            resume_id: None,
            pinned_run_id: None,
            generation: 0,
        }
    }
}

impl SessionHistoryState {
    pub fn new() -> SessionHistoryState {
        SessionHistoryState::default()
    }

    pub fn can_load(&self) -> bool {
        matches!(self.phase, Phase::Idle | Phase::Failed)
    }

    fn touched(&self) -> bool {
        !self.runs.is_empty() || self.end.is_some() || self.phase != Phase::Idle
    }

    /// `Connects`: whether what was loaded still attaches above this retained request.
    pub fn connects(&self, anchor_run_id: Option<&str>, resume_id: Option<&str>) -> bool {
        !self.touched()
            || (resume_id == self.resume_id.as_deref() && (anchor_run_id == self.anchor_run_id.as_deref() || self.anchor_run_id.is_none()))
    }

    /// `Reconcile`: history attaches above one retained request of one session; a pane without a retained request adopts its first one.
    pub fn reconcile(&mut self, anchor_run_id: Option<&str>, resume_id: Option<&str>) {
        if !self.touched() {
            return;
        }
        if !self.connects(anchor_run_id, resume_id) {
            self.reset();
            return;
        }
        self.adopt(anchor_run_id);
    }

    fn adopt(&mut self, id: Option<&str>) {
        let Some(id) = id.filter(|_| self.anchor_run_id.is_none()) else { return };
        self.anchor_run_id = Some(id.to_string());
        if self.pinned_run_id.is_none() {
            self.pinned_run_id = Some(id.to_string());
        }
    }

    pub fn reset(&mut self) {
        let next = self.generation + 1;
        *self = SessionHistoryState { generation: next, ..SessionHistoryState::default() };
    }

    /// `Follow`: the pane's retained list changed from `previous` to `current`. A trim that dropped the anchor moves the dropped runs here,
    /// newest last, and anchors on the new first one instead of throwing the history away.
    pub fn follow(&mut self, previous: &[MightyGraphRun], current: &[MightyGraphRun], resume_id: Option<&str>, provider: &str) {
        if !self.touched() || previous.first().map(|r| r.id()) == current.first().map(|r| r.id()) {
            return;
        }
        if resume_id != self.resume_id.as_deref() {
            self.reset();
            return;
        }
        let Some(anchor) = self.anchor_run_id.clone() else {
            self.adopt(current.first().map(|r| r.id()));
            return;
        };
        let (Some(start), Some(first)) = (previous.iter().position(|r| r.id() == anchor), current.first()) else {
            self.reset();
            return;
        };
        let stop = previous.iter().position(|r| r.id() == first.id()).unwrap_or(previous.len());
        if stop <= start {
            self.reset();
            return;
        }
        let known: HashSet<String> = self.runs.iter().map(|r| r.id().to_string()).collect();
        for original in previous[start..stop].iter().filter(|r| !known.contains(r.id())) {
            let mut run = original.clone();
            mighty_core::normalize::apply_provider(&mut run, provider);
            self.runs.push(run);
        }
        self.anchor_run_id = Some(first.id().to_string());
        if self.pinned_run_id.is_none() {
            self.pinned_run_id = Some(anchor);
        }
        self.bound();
    }

    /// `Bound`: drops the oldest runs past the count and byte limits; the pane then stops loading, since the record cursor lies above
    /// what was dropped.
    fn bound(&mut self) {
        let mut bytes = mighty_graph::support::live_history_bytes(&self.runs);
        let mut dropped = false;
        while !self.runs.is_empty() && (self.runs.len() > MAXIMUM_RUNS || bytes > MAXIMUM_BYTES) {
            bytes -= mighty_graph::support::live_history_bytes(&self.runs[..1]);
            self.runs.remove(0);
            dropped = true;
        }
        if dropped || self.runs.len() >= MAXIMUM_RUNS {
            self.phase = Phase::Limit;
        }
    }

    /// `Begin`: the request for the next chunk, or None when nothing more can be loaded. Marks the state as loading.
    pub fn begin(
        &mut self,
        anchor_run_id: Option<&str>,
        resume_id: Option<&str>,
        anchor: Option<SessionHistoryAnchor>,
        template: &SessionHistoryRequest,
    ) -> Option<SessionHistoryRequest> {
        self.reconcile(anchor_run_id, resume_id);
        if !self.can_load() {
            return None;
        }
        let Some(resume_id) = resume_id else {
            self.anchor_run_id = anchor_run_id.map(str::to_string);
            self.phase = Phase::Unavailable;
            return None;
        };
        if !self.touched() {
            self.anchor_run_id = anchor_run_id.map(str::to_string);
            self.pinned_run_id = anchor_run_id.map(str::to_string);
        }
        self.resume_id = Some(resume_id.to_string());
        self.phase = Phase::Loading;
        Some(SessionHistoryRequest {
            resume_id: resume_id.to_string(),
            file: self.file.clone(),
            end: self.end,
            anchor: if self.end.is_none() { anchor } else { None },
            ..template.clone()
        })
    }

    /// `Finish`: takes a finished load; a result from before a reset is ignored. True when the load should start again from scratch (the
    /// record was replaced under the cursor).
    pub fn finish(&mut self, result: Result<SessionHistoryChunk, SessionHistoryError>, generation: u64) -> bool {
        if generation != self.generation || self.phase != Phase::Loading {
            return false;
        }
        match result {
            Ok(chunk) => {
                let known: HashSet<String> = self.runs.iter().map(|r| r.id().to_string()).collect();
                let mut runs: Vec<MightyGraphRun> = chunk.runs.into_iter().filter(|r| !known.contains(r.id())).collect();
                runs.append(&mut self.runs);
                self.runs = runs;
                self.file = Some(chunk.file);
                self.end = Some(chunk.end);
                self.phase =
                    if chunk.reached_start { if self.runs.is_empty() { Phase::Unavailable } else { Phase::Start } } else { Phase::Idle };
                self.bound();
                false
            }
            Err(SessionHistoryError::Changed) => {
                self.reset();
                true
            }
            Err(SessionHistoryError::Missing | SessionHistoryError::Unsupported) => {
                self.phase = if self.runs.is_empty() { Phase::Unavailable } else { Phase::Start };
                false
            }
            Err(_) => {
                self.phase = Phase::Failed;
                false
            }
        }
    }

    /// `AnchorFor`: the anchor of the retained list, its first run's text and send time.
    pub fn anchor_for(session: &RunSession) -> Option<SessionHistoryAnchor> {
        let first = session.graph_runs().first()?;
        let sent = session
            .logs()
            .iter()
            .find(|l| l.id() == first.id())
            .and_then(|l| l.timestamp.clone())
            .or_else(|| first.root_entries().first().and_then(|e| e.timestamp.clone()));
        Some(SessionHistoryAnchor::new(first.input(), time::parse(sent.as_deref())))
    }

    /// `BlockText`: what the history block at the top of the diagram says — the phase line and, once requests were loaded, how many.
    pub fn block_text(&self, loaded: usize, locale: &Locale) -> String {
        let line = match self.phase {
            Phase::Loading => locale.get("graph.history.loading"),
            Phase::Start => locale.get("graph.history.start"),
            Phase::Unavailable => locale.get(if loaded > 0 { "graph.history.start" } else { "graph.history.none" }),
            Phase::Limit => locale.get("graph.history.limit"),
            Phase::Failed => locale.get("graph.history.failed"),
            Phase::Idle => locale.get("graph.history.load"),
        };
        if loaded > 0 { format!("{line} · {}", locale.get_with("graph.history.loaded", &[("count", &loaded.to_string())])) } else { line }
    }

    /// `BlockActs`: the idle and failed lines are buttons.
    pub fn block_acts(&self) -> bool {
        matches!(self.phase, Phase::Idle | Phase::Failed)
    }
}
