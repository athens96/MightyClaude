//! The cards over a pane's composer and its status line (`{kind:"cards", paneId, cards}`, page `src/cards/types.ts` `Card`): C#
//! `PaneView.WebCards` (MainWindow.WebShell.Cards.cs:391) and the `Web*Card` records it makes (:12-60), in the composer's order —
//! next actions, plan history, background work, the permission host's card (plan, question, permission or Antigravity), the
//! terminal prompt, web-open prompts, the sign-in card, the status line. Each record's fields are in the C# order with `kind`
//! last (a record's positional properties come before its body's), a null left out as `Wire.Json` leaves it.
//!
//! What later stages provide is typed in [`CardInputs`] and listed in [`Pending`].

use mighty_core::locale::Locale;
use mighty_core::model::RunSession;
use serde::Serialize;

use crate::next_actions;
use crate::presentation::{self, ToolPermissionPresentation, UserQuestionnaire, bar, plan};

/// The card inputs later stages provide; until then the host passes none.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Pending {
    /// The pane's tool permissions and questions (`toolPermissions`, the run engine's channel, W2).
    Requests,
    /// The background strip's rows (`PlanCardSupport.BackgroundStripRows`: the style engine's task list and the graph's blocks, W3/W9).
    BackgroundRows,
    /// A terminal prompt (`terminalPrompts`, W4).
    TerminalPrompt,
    /// `agentUrlPrompts.ForPane` (agent web prompts, W4).
    WebOpen,
    /// `loginRetries`, `loginJobs`, `loginFailures`, `loginNotes`, `accountLoginWaits` (login recovery, W6).
    Login,
    /// What `RenderStatusLine` last drew (status line, W6).
    StatusLine,
    /// `activeStyle` and the style's `GuidedQuestion` (style engine, W9).
    Style,
}

pub const PENDING: [Pending; 7] = [
    Pending::Requests,
    Pending::BackgroundRows,
    Pending::TerminalPrompt,
    Pending::WebOpen,
    Pending::Login,
    Pending::StatusLine,
    Pending::Style,
];

/// C# `ToolPermissionRequest` (ToolPermissions.cs:12): the run engine's request, one definition in mighty-core.
pub use mighty_core::permission::ToolPermissionRequest as PermissionRequest;

/// `ToolPermissionRequest.IsAnswerableQuestion`.
fn answerable_question(request: &PermissionRequest) -> bool {
    presentation::is_answerable_question(&request.state, request.can_answer_questions, &request.tool_name, &request.input_json)
}

/// The plan card's draft (`planDraft`): its answer being sent, or why it failed.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct PlanDraft {
    pub request_id: String,
    pub sending: bool,
    pub error: Option<String>,
}

/// One background strip row as `PlanCardSupport.BackgroundStripRows` gives it (W3/W9), already said: kind · status · elapsed.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BackgroundTask {
    pub text: String,
    pub status: String,
    pub detail: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tail: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub command: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tail_lines: Option<Vec<String>>,
}

/// One action of the Antigravity card, already in words (`AntigravityPermissionFlow.KindLabel`, `RuleLine`, `OnceOnlyText`; the host's
/// antigravity.rs words it from the run service's card).
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AntigravityAction {
    pub heading: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target: Option<String>,
    pub code: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rule: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub once_only: Option<String>,
}

/// The pane's Antigravity card and its state (`AntigravityCard`, `AntigravityError`, `AntigravityAnswering` of the run service).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AntigravityInput {
    pub card_id: String,
    pub run_id: String,
    pub actions: Vec<AntigravityAction>,
    pub reason: Option<String>,
    pub busy: bool,
    pub can_allow: bool,
    pub can_allow_always: bool,
}

/// A command in the pane's terminal waiting for input (MainWindow.TerminalPrompts.cs, W4).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TerminalPromptInput {
    pub id: String,
    pub text: String,
    pub options: Vec<String>,
    /// yesNo, secret, text or choice.
    pub kind: String,
    pub default_yes: Option<bool>,
}

/// A sign-in under way (`CliBackgroundLogin` as the card reads it, W6).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LoginJob {
    pub automatic: bool,
    /// "starting" or later.
    pub phase: String,
    /// A sign-in in a terminal of its own (Antigravity, pi, opencode, Hermes).
    pub terminal: bool,
    pub url: Option<String>,
    pub asks_for_code: bool,
}

/// The pane's sign-in card state (`loginRetries[id]` and the provider's job, failure, note and account wait; W6).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LoginInput {
    pub provider: String,
    pub job: Option<LoginJob>,
    pub failure: Option<String>,
    pub note: Option<String>,
    pub account_wait: bool,
}

/// A status line run, already coloured (`AnsiSegment` with the terminal colour `MainWindow.StatusLine.Terminal` gives, W6).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StatusRun {
    pub text: String,
    /// `#rrggbb`, or None.
    pub color: Option<String>,
    /// The standard bright black (`AnsiColorKind.Standard`, 8): the secondary ink when no colour was given.
    pub bright_black: bool,
    pub dim: bool,
    pub bold: bool,
    pub italic: bool,
    pub underline: bool,
}

/// What `RenderStatusLine` last drew (W6): the configuration's source and command, an untrusted one's, and the result.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct StatusLineInput {
    /// `(SourceLabel, Command)` of the configuration in use.
    pub config: Option<(String, String)>,
    /// `(SourceLabel, Command)` of a workspace's command not trusted yet.
    pub untrusted: Option<(String, String)>,
    /// The result's lines, None when there is no result.
    pub lines: Option<Vec<Vec<StatusRun>>>,
    pub error_text: Option<String>,
}

/// Everything `cards` reads.
pub struct CardInputs<'a> {
    pub locale: Locale,
    /// Minutes east of UTC, for the plan times (C# converts to the local zone).
    pub utc_offset_minutes: i32,
    pub pane: &'a RunSession,
    /// `Status == "running" || starting || queueStarting || BackgroundUpdateHolds` (the composer's [`crate::composer::busy`]).
    pub busy: bool,
    /// `activeStyle is not null`.
    pub styled: bool,
    /// `activeStyle?.Evaluator.DrawsTasks`.
    pub style_draws_tasks: bool,
    /// `PlanCardSupport.ComposerShowsPlanActions(request, InMightyDiagram, GraphRuns)`: the diagram draws the waiting plan.
    pub diagram_draws_plan: bool,
    pub requests: &'a [PermissionRequest],
    pub plan_draft: Option<&'a PlanDraft>,
    /// `Snapshot.BackgroundWorkOpen`.
    pub background_open: bool,
    pub background_rows: &'a [BackgroundTask],
    pub antigravity: Option<&'a AntigravityInput>,
    pub terminal_prompt: Option<&'a TerminalPromptInput>,
    pub web_open: &'a [(String, String)],
    pub login: Option<&'a LoginInput>,
    pub status_line: Option<&'a StatusLineInput>,
}

// ── the page's cards ────────────────────────────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlanRecordCard {
    pub id: String,
    pub outcome_title: String,
    pub tone: &'static str,
    pub time: String,
    pub received: String,
    pub headline: String,
    pub plan: String,
    pub truncated: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub feedback: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct PermissionField {
    pub label: String,
    pub value: String,
    pub code: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct QuestionOption {
    pub label: String,
    pub description: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Question {
    pub header: String,
    pub question: String,
    pub multi_select: bool,
    pub options: Vec<QuestionOption>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct LoginLine {
    pub text: String,
    pub tone: &'static str,
    pub busy: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StatusSegment {
    pub text: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub color: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ink: Option<&'static str>,
    pub dim: bool,
    pub bold: bool,
    pub italic: bool,
    pub underline: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct StatusTrust {
    pub prompt: String,
    pub command: String,
    pub allow: String,
    pub deny: String,
    pub note: String,
}

/// One card. Serialized as the C# record: its fields, then `kind`.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(untagged)]
pub enum Card {
    NextActions(NextActionsCard),
    PlanHistory(PlanHistoryCard),
    Background(BackgroundCard),
    Permission(Box<PermissionCard>),
    Questionnaire(QuestionnaireCard),
    Plan(PlanCard),
    Antigravity(AntigravityCard),
    TerminalPrompt(TerminalPromptCard),
    WebOpen(WebOpenCard),
    Login(LoginCard),
    StatusLine(StatusLineCard),
}

impl Card {
    /// The page's `kind`.
    pub fn kind(&self) -> &'static str {
        match self {
            Card::NextActions(c) => c.kind,
            Card::PlanHistory(c) => c.kind,
            Card::Background(c) => c.kind,
            Card::Permission(c) => c.kind,
            Card::Questionnaire(c) => c.kind,
            Card::Plan(c) => c.kind,
            Card::Antigravity(c) => c.kind,
            Card::TerminalPrompt(c) => c.kind,
            Card::WebOpen(c) => c.kind,
            Card::Login(c) => c.kind,
            Card::StatusLine(c) => c.kind,
        }
    }
}

/// `WebNextActionsCard(EntryId, Actions)`.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NextActionsCard {
    pub entry_id: String,
    pub actions: Vec<String>,
    pub kind: &'static str,
}

/// `WebPlanHistoryCard(Records)`.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct PlanHistoryCard {
    pub records: Vec<PlanRecordCard>,
    pub kind: &'static str,
}

/// `WebBackgroundCard(Counts, Open, Tasks)`.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct BackgroundCard {
    pub counts: String,
    pub open: bool,
    pub tasks: Vec<BackgroundTask>,
    pub kind: &'static str,
}

/// `WebPermissionCard(...)` (MainWindow.WebShell.Cards.cs:32).
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PermissionCard {
    pub request_id: String,
    pub title: String,
    pub tool: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub waiting: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub headline: Option<String>,
    pub fields: Vec<PermissionField>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub summary: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cannot_allow: Option<String>,
    pub can_allow: bool,
    pub input_json: String,
    pub raw_json: String,
    pub once_only: String,
    pub deny: String,
    pub allow_once: String,
    pub kind: &'static str,
}

/// `WebQuestionnaireCard(RequestId, Count, Questions)`.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct QuestionnaireCard {
    pub request_id: String,
    pub count: usize,
    pub questions: Vec<Question>,
    pub kind: &'static str,
}

/// `WebPlanCard(RequestId, Count, Received, Plan, ActionsOnly, Sending, Error?)`.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlanCard {
    pub request_id: String,
    pub count: usize,
    pub received: String,
    pub plan: String,
    pub actions_only: bool,
    pub sending: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
    pub kind: &'static str,
}

/// `WebAntigravityCard(CardId, RunId, Actions, Reason?, Busy, CanAllow, CanAllowAlways, AlwaysTip?)`.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AntigravityCard {
    pub card_id: String,
    pub run_id: String,
    pub actions: Vec<AntigravityAction>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
    pub busy: bool,
    pub can_allow: bool,
    pub can_allow_always: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub always_tip: Option<String>,
    pub kind: &'static str,
}

/// `WebTerminalPromptCard(PromptId, Text, Options, Input, DefaultYes?)`.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TerminalPromptCard {
    pub prompt_id: String,
    pub text: String,
    pub options: Vec<String>,
    pub input: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub default_yes: Option<bool>,
    pub kind: &'static str,
}

/// `WebOpenCard(RequestId, Url)`.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WebOpenCard {
    pub request_id: String,
    pub url: String,
    pub kind: &'static str,
}

/// `WebLoginCard(Title, Lines, Url?, AsksForCode, Buttons, LoginEnabled)`.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LoginCard {
    pub title: String,
    pub lines: Vec<LoginLine>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub url: Option<String>,
    pub asks_for_code: bool,
    pub buttons: Vec<&'static str>,
    pub login_enabled: bool,
    pub kind: &'static str,
}

/// `WebStatusLineCard(Lines, Error?, Trust?, Tip, Label)`.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct StatusLineCard {
    pub lines: Vec<Vec<StatusSegment>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub trust: Option<StatusTrust>,
    pub tip: String,
    pub label: String,
    pub kind: &'static str,
}

// ── the rules ───────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `WebNextActions` (MainWindow.WebShell.Cards.cs:406): an idle agent pane whose latest reply ends with options.
pub fn next_actions_card(i: &CardInputs) -> Option<NextActionsCard> {
    if i.pane.kind() != "claude" || i.busy {
        return None;
    }
    let reply = i.pane.logs().iter().rev().find(|e| matches!(e.kind(), "user" | "assistant"))?;
    if reply.kind() != "assistant" {
        return None;
    }
    let actions = next_actions::parse(reply.text());
    (!actions.is_empty()).then(|| NextActionsCard {
        entry_id: reply.id().into(),
        actions: actions.iter().map(|a| a.display_label()).collect(),
        kind: "nextActions",
    })
}

/// `WebNextActionFill` (MainWindow.WebShell.Cards.cs:417): the draft after a next action's words, or None when it may not.
pub fn next_action_fill(pane: &RunSession, may_fill: bool, entry_id: &str, index: i64, draft: &str) -> Option<String> {
    if !may_fill {
        return None;
    }
    let (latest, actions) = next_actions::latest(pane.logs())?;
    if latest != entry_id || index < 0 || index as usize >= actions.len() {
        return None;
    }
    let (replaces, text) = next_actions::insertion(draft, &actions[index as usize].fill);
    Some(if replaces { text } else { format!("{draft}{text}") })
}

/// `WebPlanHistory` (MainWindow.WebShell.Cards.cs:426): the pane's answered plans, newest first, outside the diagram.
pub fn plan_history_card(i: &CardInputs, in_mighty_diagram: bool) -> Option<PlanHistoryCard> {
    let l = &i.locale;
    if i.pane.kind() != "claude" || !plan::shows_history_strip(in_mighty_diagram) {
        return None;
    }
    let records = i.pane.plan_history.as_deref().unwrap_or(&[]);
    if records.is_empty() {
        return None;
    }
    Some(PlanHistoryCard {
        records: records
            .iter()
            .rev()
            .map(|r| {
                let outcome = r.outcome.as_deref().unwrap_or("");
                PlanRecordCard {
                    id: r.id.clone().unwrap_or_default(),
                    outcome_title: plan::outcome_title(outcome, l),
                    tone: match outcome {
                        plan::APPROVED_AUTO | plan::APPROVED_CONFIRM => "done",
                        plan::REVISED => "wait",
                        _ => "stop",
                    },
                    time: plan::time_text(r.decided_at.as_deref().unwrap_or(""), i.utc_offset_minutes),
                    received: plan::received_text(r.received_at.as_deref().unwrap_or(""), i.utc_offset_minutes, l),
                    headline: plan::headline(r.plan.as_deref().unwrap_or("")),
                    plan: r.plan.clone().unwrap_or_default(),
                    truncated: r.plan_truncated == Some(true),
                    feedback: r.feedback.clone().filter(|f| !f.is_empty()),
                }
            })
            .collect(),
        kind: "planHistory",
    })
}

/// `WebBackground` (MainWindow.WebShell.Cards.cs:436): the strip's line and, open, its rows. The second value says a running
/// task's elapsed time must be said again each second (`ticks`).
pub fn background_card(i: &CardInputs) -> Option<(BackgroundCard, bool)> {
    let pane = i.pane;
    let mighty = pane.kind() == "claude" && pane.provider() == "claude" && pane.agent_view_mode.as_deref() == Some("mighty");
    let draws_tasks = mighty && i.style_draws_tasks;
    let work = pane.background_work.as_ref();
    if pane.kind() != "claude" || !plan::shows_background_strip(work, mighty, draws_tasks) {
        return None;
    }
    let work = work?;
    let tasks = if i.background_open { i.background_rows.to_vec() } else { vec![] };
    let ticks = i.background_open && crate::queue::running_tasks(work) > 0;
    Some((BackgroundCard { counts: plan::background_counts(work, &i.locale), open: i.background_open, tasks, kind: "background" }, ticks))
}

/// `WebCardRequests` (MainWindow.WebShell.Cards.cs:460): the requests the page's cards answer, the style's waiting question first.
pub fn card_requests(requests: &[PermissionRequest], styled: bool) -> Vec<&PermissionRequest> {
    let pending: Vec<&PermissionRequest> = requests.iter().filter(|r| r.state == "pending").collect();
    if !styled {
        return pending;
    }
    // CardRequests under a style: every waiting request but the question the panel answers (GuidedQuestions.Visible); the page
    // draws that question first itself.
    match requests.iter().find(|r| answerable_question(r)) {
        Some(asked) => std::iter::once(asked).chain(pending.into_iter().filter(|r| r.id != asked.id)).collect(),
        None => pending,
    }
}

/// `WebRequestCard` (MainWindow.WebShell.Cards.cs:463): the first waiting request's card, else the Antigravity card.
pub fn request_card(i: &CardInputs) -> Option<Card> {
    let l = &i.locale;
    let pending = card_requests(i.requests, i.styled);
    let Some(current) = pending.first() else {
        let card = i.antigravity?;
        let always_tip = (card.can_allow && !card.can_allow_always)
            .then(|| card.actions.iter().find_map(|a| a.once_only.clone()).unwrap_or_else(|| l.get("antigravity.permission.onceOnly")));
        return Some(Card::Antigravity(AntigravityCard {
            card_id: card.card_id.clone(),
            run_id: card.run_id.clone(),
            actions: card.actions.clone(),
            reason: card.reason.clone(),
            busy: card.busy,
            can_allow: card.can_allow,
            can_allow_always: card.can_allow_always,
            always_tip,
            kind: "antigravity",
        }));
    };
    if current.can_answer_plan
        && let Some(plan_text) = current.plan()
    {
        let place = plan::composer_place(current.state == "pending", i.diagram_draws_plan, i.styled);
        let draft = i.plan_draft.filter(|d| d.request_id == current.id);
        return Some(Card::Plan(PlanCard {
            request_id: current.id.clone(),
            count: pending.len(),
            received: current.received_at.as_deref().map(|at| plan::received_text(at, i.utc_offset_minutes, l)).unwrap_or_default(),
            plan: plan_text,
            actions_only: matches!(place, Some(plan::ComposerPlace::BarActions | plan::ComposerPlace::PanelActions)),
            sending: draft.is_some_and(|d| d.sending),
            error: draft.and_then(|d| d.error.clone()),
            kind: "plan",
        }));
    }
    if current.can_answer_questions
        && current.tool_name == "AskUserQuestion"
        && let Some(questionnaire) = UserQuestionnaire::parse(&current.input_json)
    {
        return Some(Card::Questionnaire(QuestionnaireCard {
            request_id: current.id.clone(),
            count: pending.len(),
            questions: questionnaire
                .questions
                .into_iter()
                .map(|q| Question {
                    header: q.header,
                    question: q.question,
                    multi_select: q.multi_select,
                    options: q.options.into_iter().map(|o| QuestionOption { label: o.label, description: o.description }).collect(),
                })
                .collect(),
            kind: "questionnaire",
        }));
    }
    let pres = ToolPermissionPresentation::make(&current.tool_name, &current.input_json, l);
    let bare = pres.fields.is_empty() && pres.headline.is_none();
    Some(Card::Permission(Box::new(PermissionCard {
        request_id: current.id.clone(),
        title: bar::title_template(l).replace("{title}", &pres.title),
        tool: current.tool_name.clone(),
        waiting: (pending.len() > 1).then(|| bar::waiting_count_template(l).replace("{count}", &pending.len().to_string())),
        headline: pres.headline,
        fields: pres.fields.into_iter().map(|f| PermissionField { label: f.label, value: f.value, code: f.code }).collect(),
        summary: bare.then(|| current.summary.clone()),
        path: current.blocked_path.as_deref().filter(|p| !p.is_empty()).map(|p| bar::path_template(l).replace("{path}", p)),
        reason: current.reason.clone().filter(|r| !r.is_empty()),
        cannot_allow: (!current.can_allow).then(|| bar::cannot_allow_here(l)),
        can_allow: current.can_allow,
        input_json: current.input_json.clone(),
        raw_json: bar::raw_json(l),
        once_only: bar::once_only_note(l),
        deny: bar::button_deny(l),
        allow_once: bar::button_allow_once(l),
        kind: "permission",
    })))
}

/// C# `CliTerminalSignIn.Table` (CliLoginRecovery.cs:325): the providers that sign in in a terminal, with the words it waits with.
fn terminal_sign_in(provider: &str) -> Option<&'static str> {
    match provider {
        "gemini" => Some("loginRecovery.antigravityTerminal"),
        "pi" => Some("loginRecovery.piTerminal"),
        "opencode" => Some("loginRecovery.opencodeTerminal"),
        "hermes" => Some("loginRecovery.hermesTerminal"),
        _ => None,
    }
}

/// `WebLogin` (MainWindow.WebShell.Cards.cs:554): the sign-in card after an auth error.
pub fn login_card(login: &LoginInput, l: &Locale) -> LoginCard {
    let provider = login.provider.as_str();
    let pending = login.job.is_none() && login.failure.is_none() && login.account_wait;
    let mut lines = Vec::new();
    if let Some(note) = &login.note {
        lines.push(LoginLine { text: l.get_with("loginRecovery.resendBlockedTemplate", &[("reason", note)]), tone: "wait", busy: false });
    }
    if let Some(job) = &login.job {
        if job.automatic {
            lines.push(LoginLine { text: l.get("loginRecovery.autoStarted"), tone: "ink2", busy: false });
        }
        let text = if job.phase == "starting" {
            l.get("loginRecovery.starting")
        } else if job.terminal {
            l.get(terminal_sign_in(provider).unwrap_or("loginRecovery.waiting"))
        } else {
            l.get("loginRecovery.waiting")
        };
        lines.push(LoginLine { text, tone: "ink2", busy: true });
    } else if let Some(failure) = &login.failure {
        lines.push(LoginLine { text: failure.clone(), tone: "wait", busy: false });
    } else {
        lines.push(LoginLine {
            text: l.get(if pending { "loginRecovery.terminalPending" } else { "loginRecovery.body" }),
            tone: "ink2",
            busy: false,
        });
    }
    let in_terminal = terminal_sign_in(provider).is_some();
    let buttons: Vec<&'static str> = match (&login.job, &login.failure) {
        (Some(_), _) if !in_terminal => vec!["cancel", "terminal"],
        (Some(_), _) => vec!["cancel"],
        (None, Some(_)) if !in_terminal => vec!["login", "terminal", "resend"],
        _ => vec!["login", "resend"],
    };
    LoginCard {
        title: l.get_with("loginRecovery.title", &[("provider", mighty_core::normalize::provider_name(provider))]),
        lines,
        url: login.job.as_ref().and_then(|j| j.url.clone()),
        asks_for_code: login.job.as_ref().is_some_and(|j| j.asks_for_code),
        buttons,
        login_enabled: !pending,
        kind: "login",
    }
}

/// `StatusLineSupport.MaximumLines`.
pub const STATUS_LINE_MAXIMUM_LINES: usize = 6;

/// `WebStatusLine` (MainWindow.WebShell.Cards.cs:599): the trust question, then at most six coloured rows or the failure.
pub fn status_line_card(input: &StatusLineInput, l: &Locale) -> Option<StatusLineCard> {
    let trust = input.untrusted.as_ref().map(|(source, command)| StatusTrust {
        // StatusLineStrings (StatusLineStrings.cs:8-11).
        prompt: l.get("statusLine.untrustedPrompt").replace("{source}", source),
        command: command.clone(),
        allow: l.get("statusLine.trust"),
        deny: l.get("statusLine.notNow"),
        note: l.get("statusLine.reaskNote"),
    });
    let shown = input.config.is_some() && input.lines.is_some();
    let lines: Vec<Vec<StatusSegment>> = if shown {
        input
            .lines
            .as_deref()
            .unwrap_or(&[])
            .iter()
            .take(STATUS_LINE_MAXIMUM_LINES)
            .map(|line| {
                line.iter()
                    .map(|run| StatusSegment {
                        text: run.text.clone(),
                        ink: if run.color.is_some() {
                            None
                        } else if run.bright_black {
                            Some("ink2")
                        } else {
                            Some("ink")
                        },
                        color: run.color.clone(),
                        dim: run.dim,
                        bold: run.bold,
                        italic: run.italic,
                        underline: run.underline,
                    })
                    .collect()
            })
            .collect()
    } else {
        vec![]
    };
    let error =
        (shown && input.lines.as_ref().is_some_and(|l| l.is_empty())).then(|| input.error_text.clone().filter(|e| !e.is_empty())).flatten();
    if trust.is_none() && lines.is_empty() && error.is_none() {
        return None;
    }
    Some(StatusLineCard {
        lines,
        error,
        trust,
        tip: match &input.config {
            None => "statusLine".into(),
            Some((source, command)) => format!("statusLine · {source} · {command}"),
        },
        label: l.get("composer.statusLine.name"),
        kind: "statusLine",
    })
}

/// `InMightyDiagram` (MainWindow.PlanCard.cs:70): a Claude-kind pane of a graph provider in the Mighty view's diagram.
pub fn in_mighty_diagram(pane: &RunSession) -> bool {
    pane.kind() == "claude"
        && matches!(pane.provider(), "claude" | "codex" | "gemini" | "pi" | "opencode" | "hermes")
        && pane.agent_view_mode.as_deref() == Some("mighty")
        && mighty_core::model::graph_view_mode(pane.graph_view_mode.as_deref()).as_deref().unwrap_or("diagram") == "diagram"
}

/// C# `PaneView.WebCards` (MainWindow.WebShell.Cards.cs:391): the cards in the composer's order, and whether a running background
/// task's elapsed time must be said again each second.
pub fn cards(i: &CardInputs) -> (Vec<Card>, bool) {
    let mut cards = Vec::new();
    let mut ticks = false;
    if let Some(next) = next_actions_card(i) {
        cards.push(Card::NextActions(next));
    }
    if let Some(history) = plan_history_card(i, in_mighty_diagram(i.pane)) {
        cards.push(Card::PlanHistory(history));
    }
    if let Some((background, tick)) = background_card(i) {
        cards.push(Card::Background(background));
        ticks = tick;
    }
    if let Some(request) = request_card(i) {
        cards.push(request);
    }
    if let Some(prompt) = i.terminal_prompt {
        cards.push(Card::TerminalPrompt(TerminalPromptCard {
            prompt_id: prompt.id.clone(),
            text: prompt.text.clone(),
            options: prompt.options.clone(),
            input: prompt.kind.clone(),
            default_yes: prompt.default_yes,
            kind: "terminalPrompt",
        }));
    }
    for (id, url) in i.web_open {
        cards.push(Card::WebOpen(WebOpenCard { request_id: id.clone(), url: url.clone(), kind: "webOpen" }));
    }
    if let Some(login) = i.login {
        cards.push(Card::Login(login_card(login, &i.locale)));
    }
    if let Some(line) = i.status_line.and_then(|s| status_line_card(s, &i.locale)) {
        cards.push(Card::StatusLine(line));
    }
    (cards, ticks)
}
