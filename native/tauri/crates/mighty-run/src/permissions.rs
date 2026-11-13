//! C# ToolPermissions.cs: Claude Code's stdio control protocol (`ClaudePermissionChannel`) and the launch arguments and first stdin
//! frames of each CLI (`ProviderInput`).
//!
//! Ephemeral, single-call consent: a request is never saved; its original input stays inside the channel. Only `can_use_tool` asks
//! reach the pane; no settings update or persistent rule is ever returned; the only mode change is the session-only `setMode` of an
//! approved plan.
//!
//! C# runs every callback under the channel's (re-entrant) lock, so a callback may answer the request it was just handed. Here the
//! lock is not re-entrant: writes to the CLI happen under it, in order, and the callbacks (`emit`, `activity`, `warning`, `fail`,
//! `plan`) run after it is released, in the order C# calls them — a callback may answer at once, as C#'s may.

use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex};

use indexmap::IndexMap;
use mighty_core::model::PlanRecord;
use mighty_core::time::DateTimeOffset;
use mighty_core::wire;
use serde_json::{Map, Value, json};

use crate::activity::{self, clean};
use crate::meta;
use crate::model::StartRunRequest;
use crate::plan_mode::{self, PlanDecision, outcome};
use crate::presentation::strings as copy;
use crate::providers;
use crate::questionnaire::{UserQuestionAnswer, UserQuestionnaire};
use crate::strings;
use crate::text_json;
use crate::{Result, RunError, lock};

/// C# `ToolPermissionRequest`: one waiting (or settled) request as the pane shows it (one definition, in mighty-core).
pub use mighty_core::permission::ToolPermissionRequest;

pub type Write = Arc<dyn Fn(&str) -> std::io::Result<()> + Send + Sync>;
pub type Emit = Arc<dyn Fn(ToolPermissionRequest) + Send + Sync>;
pub type ActivityHook = Arc<dyn Fn(&ToolPermissionRequest, &str) + Send + Sync>;
pub type Message = Arc<dyn Fn(&str) + Send + Sync>;
pub type PlanHook = Arc<dyn Fn(PlanRecord) + Send + Sync>;
pub type Clock = Arc<dyn Fn() -> DateTimeOffset + Send + Sync>;

/// What a channel tells its owner, after its lock is released.
enum Effect {
    Emit(ToolPermissionRequest),
    Activity(ToolPermissionRequest, String),
    Warning(String),
    Fail(String),
    Plan(PlanRecord),
}

struct Pending {
    display: ToolPermissionRequest,
    input: Value,
}

#[derive(Default)]
struct State {
    initialized: bool,
    failed: bool,
    closed: bool,
    pending: HashMap<String, Pending>,
    order: Vec<String>,
    seen: HashSet<String>,
}

/// The callbacks a channel reports through (C#'s constructor arguments).
#[derive(Clone)]
pub struct ChannelHooks {
    /// Writes one line to the CLI's stdin; an error fails the channel closed.
    pub write: Write,
    /// A request waiting, or settled.
    pub emit: Emit,
    /// The tool row's state for a request (the parser owns the row).
    pub activity: ActivityHook,
    pub warning: Message,
    /// The channel failed closed (the run stops).
    pub fail: Message,
    /// Every settled ExitPlanMode request: answered, or cancelled because the CLI withdrew it or the run ended.
    pub plan: PlanHook,
    pub clock: Clock,
}

impl ChannelHooks {
    /// Hooks that do nothing but write.
    pub fn writing(write: Write) -> ChannelHooks {
        ChannelHooks {
            write,
            emit: Arc::new(|_| {}),
            activity: Arc::new(|_, _| {}),
            warning: Arc::new(|_| {}),
            fail: Arc::new(|_| {}),
            plan: Arc::new(|_| {}),
            clock: Arc::new(DateTimeOffset::now),
        }
    }
}

/// C# `ClaudePermissionChannel` (ToolPermissions.cs:29-340).
pub struct ClaudePermissionChannel {
    pub run_id: String,
    pub initialization_id: String,
    prompt: String,
    pane_mode: String,
    hooks: ChannelHooks,
    state: Mutex<State>,
}

pub const MAXIMUM_INPUT_BYTES: usize = 65_536;
pub const MAXIMUM_PENDING: usize = 16;

impl ClaudePermissionChannel {
    /// `pane_mode`: the pane's stored permission mode, which an approved plan never goes below.
    pub fn new(run_id: &str, prompt: &str, hooks: ChannelHooks, pane_mode: &str) -> ClaudePermissionChannel {
        ClaudePermissionChannel {
            run_id: run_id.into(),
            initialization_id: wire::new_id(),
            prompt: prompt.into(),
            pane_mode: pane_mode.into(),
            hooks,
            state: Mutex::new(State::default()),
        }
    }

    pub fn initialized(&self) -> bool {
        lock(&self.state).initialized
    }

    pub fn failed(&self) -> bool {
        lock(&self.state).failed
    }

    /// The waiting requests, oldest first.
    pub fn waiting(&self) -> Vec<ToolPermissionRequest> {
        let state = lock(&self.state);
        state.order.iter().filter_map(|id| state.pending.get(id)).map(|p| p.display.clone()).collect()
    }

    fn run(&self, effects: Vec<Effect>) {
        for effect in effects {
            match effect {
                Effect::Emit(value) => (self.hooks.emit)(value),
                Effect::Activity(value, state) => (self.hooks.activity)(&value, &state),
                Effect::Warning(message) => (self.hooks.warning)(&message),
                Effect::Fail(message) => (self.hooks.fail)(&message),
                Effect::Plan(record) => (self.hooks.plan)(record),
            }
        }
    }

    fn timestamp(&self) -> String {
        plan_mode::timestamp((self.hooks.clock)())
    }

    /// `Start`: the `initialize` handshake. The prompt is written only after the CLI answers it.
    pub fn start(&self) -> Result<()> {
        let mut effects = Vec::new();
        let result = {
            let mut state = lock(&self.state);
            let frame = json!({
                "type": "control_request",
                "request_id": self.initialization_id,
                "request": {"subtype": "initialize", "hooks": {}, "sdkMcpServers": [], "supportedDialogKinds": []},
            });
            self.send(&mut state, &mut effects, &frame)
        };
        self.run(effects);
        result
    }

    /// `InitializationTimedOut`: a handshake the CLI never answered fails closed.
    pub fn initialization_timed_out(&self) {
        let mut effects = Vec::new();
        {
            let mut state = lock(&self.state);
            if !state.closed && !state.initialized {
                self.fail_closed(&mut state, &mut effects, &copy::initialize_timed_out());
            }
        }
        self.run(effects);
    }

    /// `Receive`: one stdout line of the CLI. An error is a write that failed (the prompt, or an answer).
    pub fn receive(&self, data: &str) -> Result<()> {
        let mut effects = Vec::new();
        let result = {
            let mut state = lock(&self.state);
            self.receive_locked(&mut state, &mut effects, data)
        };
        self.run(effects);
        result
    }

    fn receive_locked(&self, state: &mut State, effects: &mut Vec<Effect>, data: &str) -> Result<()> {
        if state.closed {
            return Ok(());
        }
        let Ok(envelope) = serde_json::from_str::<Value>(data) else { return Ok(()) };
        if !envelope.is_object() {
            return Ok(());
        }
        let Some(kind) = meta::text(&envelope, "type") else { return Ok(()) };
        if kind == "control_response"
            && let Some(response) = meta::prop(&envelope, "response").filter(|r| r.is_object())
            && meta::text(response, "request_id") == Some(self.initialization_id.as_str())
        {
            if state.initialized {
                return Ok(());
            }
            if meta::text(response, "subtype") != Some("success") {
                self.fail_closed(state, effects, &copy::initialize_failed());
                return Ok(());
            }
            state.initialized = true;
            if let Some(Value::Array(replay)) = meta::prop(response, "pending_permission_requests") {
                for item in replay {
                    self.receive_request(state, effects, item)?;
                    if state.closed {
                        break;
                    }
                }
            }
            if !state.closed {
                (self.hooks.write)(&self.prompt).map_err(|e| RunError::Io(e.to_string()))?;
            }
            return Ok(());
        }
        if kind == "control_cancel_request"
            && let Some(cancel) = meta::text(&envelope, "request_id")
        {
            self.settle(state, effects, cancel, "cancelled", "stopped");
            return Ok(());
        }
        if kind == "control_request" {
            self.receive_request(state, effects, &envelope)?;
        }
        Ok(())
    }

    fn receive_request(&self, state: &mut State, effects: &mut Vec<Effect>, envelope: &Value) -> Result<()> {
        if state.closed {
            return Ok(());
        }
        let id = meta::text(envelope, "request_id");
        let request = meta::prop(envelope, "request").filter(|r| r.is_object());
        let subtype = request.and_then(|r| meta::text(r, "subtype"));
        let (Some(id), Some(request), Some(subtype)) = (id.filter(|i| wire::identifier(i)), request, subtype) else {
            self.fail_closed(state, effects, &copy::malformed_control_request());
            return Ok(());
        };
        if !state.seen.insert(id.to_string()) {
            return Ok(());
        }
        if state.seen.len() > 2048 {
            self.fail_closed(state, effects, &copy::too_many_requests_in_run());
            return Ok(());
        }
        if subtype == "request_user_dialog" {
            effects.push(Effect::Warning(copy::unsupported_dialog()));
            return Ok(());
        }
        if subtype == "elicitation" {
            self.send_success(state, effects, id, json!({"action": "decline"}))?;
            effects.push(Effect::Warning(copy::declined_elicitation()));
            return Ok(());
        }
        if subtype != "can_use_tool" {
            return self.send_error(state, effects, id, "This host does not support this control request.");
        }
        let tool_name = meta::text(request, "tool_name").filter(|t| !t.is_empty() && t.len() <= 256);
        let tool_use_id = meta::text(request, "tool_use_id").filter(|t| wire::identifier(t));
        let input = meta::prop(request, "input").filter(|i| i.is_object());
        let (Some(tool_name), Some(tool_use_id), Some(input)) = (tool_name, tool_use_id, input) else {
            self.send_error(state, effects, id, "Invalid tool permission request.")?;
            effects.push(Effect::Warning(copy::denied_malformed_request()));
            return Ok(());
        };
        if state.pending.len() >= MAXIMUM_PENDING {
            self.deny(state, effects, id, tool_use_id, "Too many pending permission requests.")?;
            effects.push(Effect::Warning(copy::denied_too_many_pending()));
            return Ok(());
        }
        let display_json = input_display(input);
        if display_json.len() > MAXIMUM_INPUT_BYTES {
            // A plan the card cannot show whole is never approvable; Claude is asked for a shorter one.
            if tool_name == plan_mode::TOOL_NAME {
                self.deny(state, effects, id, tool_use_id, plan_mode::TOO_LONG_MESSAGE)?;
                effects.push(Effect::Warning(strings::get("plan.warning.tooLong")));
                if let Some(too_long) = plan_mode::plan(input) {
                    self.record_cancelled(effects, id, &too_long, None);
                }
                return Ok(());
            }
            self.deny(state, effects, id, tool_use_id, "Tool input exceeds the host's complete-display limit; permission denied.")?;
            effects.push(Effect::Warning(copy::denied_oversized_input()));
            return Ok(());
        }
        let interaction =
            matches!(meta::prop(request, "requires_user_interaction"), Some(Value::Bool(true))) || tool_name == "AskUserQuestion";
        let details: Vec<&str> = ["title", "description", "decision_reason"]
            .iter()
            .filter_map(|key| meta::text(request, key))
            .filter(|s| !s.is_empty())
            .collect();
        let detail_text = details.join("\n");
        let original_path = meta::text(request, "blocked_path");
        let complete_metadata = detail_text.len() <= 8192 && original_path.unwrap_or("").len() <= 8192;
        let can_answer_questions = complete_metadata && tool_name == "AskUserQuestion" && UserQuestionnaire::parse(&display_json).is_some();
        let can_answer_plan = complete_metadata && tool_name == plan_mode::TOOL_NAME && plan_mode::plan(input).is_some();
        let mut reason = clean(Some(&detail_text), 8192, false);
        if interaction && !can_answer_questions && !can_answer_plan {
            if !reason.is_empty() {
                reason.push_str("\n\n");
            }
            reason.push_str(&copy::needs_separate_input_screen());
        }
        if !complete_metadata {
            if !reason.is_empty() {
                reason.push_str("\n\n");
            }
            reason.push_str(&copy::metadata_too_large());
        }
        let value = ToolPermissionRequest {
            id: id.into(),
            run_id: self.run_id.clone(),
            tool_use_id: tool_use_id.into(),
            tool_name: clean(Some(tool_name), 256, true),
            input_json: display_json,
            summary: activity::summary(tool_name, Some(input)),
            reason: (!reason.is_empty()).then_some(reason),
            blocked_path: original_path.map(|p| clean(Some(p), 8192, false)),
            state: "pending".into(),
            can_allow: !interaction && complete_metadata,
            can_answer_questions,
            can_answer_plan,
            received_at: Some(self.timestamp()),
        };
        state.pending.insert(id.to_string(), Pending { display: value.clone(), input: input.clone() });
        state.order.push(id.to_string());
        effects.push(Effect::Activity(value.clone(), "waiting".into()));
        effects.push(Effect::Emit(value));
        Ok(())
    }

    fn take(&self, state: &mut State, id: &str) -> Option<Pending> {
        let pending = state.pending.remove(id)?;
        state.order.retain(|o| o != id);
        Some(pending)
    }

    /// `Respond`: 이번만 허용 (the original input, once) or 거부 for one waiting request. Nothing else is ever returned.
    pub fn respond(&self, request_id: &str, allow: bool) -> Result<()> {
        let mut effects = Vec::new();
        let result = {
            let mut state = lock(&self.state);
            self.respond_locked(&mut state, &mut effects, request_id, allow)
        };
        self.run(effects);
        result
    }

    fn respond_locked(&self, state: &mut State, effects: &mut Vec<Effect>, request_id: &str, allow: bool) -> Result<()> {
        let Some(request) = state.pending.get(request_id).filter(|_| !state.closed) else {
            return Err(RunError::State(copy::already_settled()));
        };
        if allow && !request.display.can_allow {
            return Err(RunError::State(copy::cannot_allow()));
        }
        let (tool_use_id, input) = (request.display.tool_use_id.clone(), request.input.clone());
        if allow {
            self.send_success(state, effects, request_id, json!({"behavior": "allow", "updatedInput": input, "toolUseID": tool_use_id}))?;
        } else {
            self.deny(state, effects, request_id, &tool_use_id, "The user denied this tool request in Mighty Code.")?;
        }
        let Some(request) = self.take(state, request_id) else { return Ok(()) };
        let display = ToolPermissionRequest { state: if allow { "allowed" } else { "denied" }.into(), ..request.display };
        effects.push(Effect::Activity(display.clone(), if allow { "running" } else { "error" }.into()));
        effects.push(Effect::Emit(display.clone()));
        // A plan denied from a card that cannot answer plans is still kept.
        if !allow
            && display.can_answer_plan
            && let Some(plan) = plan_mode::plan(&request.input)
        {
            self.record_cancelled(effects, request_id, &plan, display.received_at.as_deref());
        }
        Ok(())
    }

    /// `AnswerPlan`: one pending ExitPlanMode request answered. An approval switches the CLI session's mode; revise sends the
    /// feedback back as the denial; cancel interrupts.
    pub fn answer_plan(&self, request_id: &str, decision: &PlanDecision) -> Result<PlanRecord> {
        let mut effects = Vec::new();
        let result = {
            let mut state = lock(&self.state);
            self.answer_plan_locked(&mut state, &mut effects, request_id, decision)
        };
        self.run(effects);
        result
    }

    fn answer_plan_locked(
        &self,
        state: &mut State,
        effects: &mut Vec<Effect>,
        request_id: &str,
        decision: &PlanDecision,
    ) -> Result<PlanRecord> {
        let Some(request) = state.pending.get(request_id).filter(|_| !state.closed) else {
            return Err(RunError::State(strings::get("plan.error.settled")));
        };
        let plan = plan_mode::plan(&request.input).filter(|_| request.display.can_answer_plan);
        let Some(plan) = plan else { return Err(RunError::State(strings::get("plan.error.notPlan"))) };
        // Validation precedes settlement: an empty revise leaves this exact plan answerable.
        let response = plan_mode::response(decision, &request.display.tool_use_id, Some(&self.pane_mode))?;
        self.send_success(state, effects, request_id, response)?;
        let Some(request) = self.take(state, request_id) else { return Err(RunError::State(strings::get("plan.error.settled"))) };
        let approved = outcome::pane_mode(decision.outcome()).is_some();
        let display = ToolPermissionRequest { state: if approved { "allowed" } else { "denied" }.into(), ..request.display };
        effects.push(Effect::Activity(display.clone(), if approved { "running" } else { "error" }.into()));
        effects.push(Effect::Emit(display.clone()));
        let received = display.received_at.clone().unwrap_or_else(|| self.timestamp());
        let record = plan_mode::record(request_id, &self.run_id, &plan, &received, &self.timestamp(), decision);
        effects.push(Effect::Plan(record.clone()));
        Ok(record)
    }

    /// `TrySteer`: a follow-up joins only an initialized, still-open Claude stream.
    pub fn try_steer(&self, text: &str) -> bool {
        let state = lock(&self.state);
        if state.closed || !state.initialized || text.trim().is_empty() || text.encode_utf16().count() > 100_000 || text.contains('\0') {
            return false;
        }
        (self.hooks.write)(&ProviderInput::prompt_frame(text)).is_ok()
    }

    /// `CancelAll`: the run ended; every waiting request settles as cancelled.
    pub fn cancel_all(&self) {
        let mut effects = Vec::new();
        {
            let mut state = lock(&self.state);
            self.cancel_all_locked(&mut state, &mut effects);
        }
        self.run(effects);
    }

    fn cancel_all_locked(&self, state: &mut State, effects: &mut Vec<Effect>) {
        if state.closed {
            return;
        }
        state.closed = true;
        for id in state.order.clone() {
            self.settle(state, effects, &id, "cancelled", "stopped");
        }
    }

    /// `AnswerQuestions`: an AskUserQuestion answered with the questionnaire's checked answers in place of the model's own.
    pub fn answer_questions(&self, request_id: &str, answers: &IndexMap<String, UserQuestionAnswer>) -> Result<()> {
        let mut effects = Vec::new();
        let result = {
            let mut state = lock(&self.state);
            self.answer_questions_locked(&mut state, &mut effects, request_id, answers)
        };
        self.run(effects);
        result
    }

    fn answer_questions_locked(
        &self,
        state: &mut State,
        effects: &mut Vec<Effect>,
        request_id: &str,
        answers: &IndexMap<String, UserQuestionAnswer>,
    ) -> Result<()> {
        let Some(request) = state.pending.get(request_id).filter(|_| !state.closed) else {
            return Err(RunError::State(copy::already_settled()));
        };
        let questionnaire = UserQuestionnaire::parse(&request.display.input_json).filter(|_| request.display.can_answer_questions);
        let Some(questionnaire) = questionnaire else { return Err(RunError::State(copy::cannot_allow())) };
        // Validation precedes settlement: an invalid draft leaves this exact request answerable.
        let validated = questionnaire.validate_answers(answers)?;
        let mut input = Map::new();
        if let Some(object) = request.input.as_object() {
            for (name, value) in object {
                if name != "answers" && name != "response" {
                    input.insert(name.clone(), value.clone());
                }
            }
        }
        input.insert("answers".into(), Value::Object(validated.into_iter().map(|(k, v)| (k, Value::String(v))).collect()));
        let tool_use_id = request.display.tool_use_id.clone();
        self.send_success(
            state,
            effects,
            request_id,
            json!({"behavior": "allow", "updatedInput": Value::Object(input), "toolUseID": tool_use_id}),
        )?;
        let Some(request) = self.take(state, request_id) else { return Ok(()) };
        let display = ToolPermissionRequest { state: "answered".into(), ..request.display };
        effects.push(Effect::Activity(display.clone(), "running".into()));
        effects.push(Effect::Emit(display));
        Ok(())
    }

    fn settle(&self, state: &mut State, effects: &mut Vec<Effect>, id: &str, settled: &str, activity_state: &str) {
        let Some(pending) = self.take(state, id) else { return };
        let display = ToolPermissionRequest { state: settled.into(), ..pending.display };
        effects.push(Effect::Activity(display.clone(), activity_state.into()));
        effects.push(Effect::Emit(display.clone()));
        if display.can_answer_plan
            && let Some(plan) = plan_mode::plan(&pending.input)
        {
            self.record_cancelled(effects, id, &plan, display.received_at.as_deref());
        }
    }

    fn record_cancelled(&self, effects: &mut Vec<Effect>, id: &str, plan: &str, received_at: Option<&str>) {
        let now = self.timestamp();
        effects.push(Effect::Plan(plan_mode::record(id, &self.run_id, plan, received_at.unwrap_or(&now), &now, &PlanDecision::cancel())));
    }

    fn fail_closed(&self, state: &mut State, effects: &mut Vec<Effect>, message: &str) {
        state.failed = true;
        self.cancel_all_locked(state, effects);
        effects.push(Effect::Fail(message.to_string()));
    }

    fn send_success(&self, state: &mut State, effects: &mut Vec<Effect>, id: &str, result: Value) -> Result<()> {
        self.send(
            state,
            effects,
            &json!({"type": "control_response", "response": {"subtype": "success", "request_id": id, "response": result}}),
        )
    }

    fn send_error(&self, state: &mut State, effects: &mut Vec<Effect>, id: &str, message: &str) -> Result<()> {
        self.send(
            state,
            effects,
            &json!({"type": "control_response", "response": {"subtype": "error", "request_id": id, "error": message}}),
        )
    }

    fn deny(&self, state: &mut State, effects: &mut Vec<Effect>, id: &str, tool_use_id: &str, message: &str) -> Result<()> {
        self.send_success(state, effects, id, json!({"behavior": "deny", "message": message, "toolUseID": tool_use_id}))
    }

    /// `Send`: one line to the CLI; a failed write fails the channel closed (questionnaire.error.delivery) and is the caller's error.
    fn send(&self, state: &mut State, effects: &mut Vec<Effect>, frame: &Value) -> Result<()> {
        let line = format!("{}\n", text_json::wire(frame));
        match (self.hooks.write)(&line) {
            Ok(()) => Ok(()),
            Err(_) => {
                let delivery = strings::get("questionnaire.error.delivery");
                self.fail_closed(state, effects, &delivery);
                Err(RunError::State(delivery))
            }
        }
    }
}

/// `ClaudePermissionChannel.InputDisplay`: the input indented with the relaxed encoder, every Unicode format character (U+202E
/// among them) and other control character made visible as a lower-case `\uxxxx`.
pub fn input_display(input: &Value) -> String {
    let json = text_json::relaxed(input, true);
    let mut out = String::with_capacity(json.len());
    for c in json.chars() {
        let control = (c as u32) < 0x20 && !matches!(c, '\t' | '\n' | '\r');
        if text_json::is_format(c) && (c as u32) <= 0xFFFF || control {
            out.push_str(&format!("\\u{:04x}", c as u32));
        } else {
            out.push(c);
        }
    }
    out
}

/// C# `ProviderInput`: a run's launch arguments and first stdin.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProviderInput {
    pub arguments: Vec<String>,
    pub standard_input: Vec<u8>,
}

impl ProviderInput {
    /// `ProviderInput.Prepare`: the provider's arguments (Claude's with host prompts when the pane can show the approval card) and
    /// the prompt frame.
    pub fn prepare(value: &StartRunRequest, plugin_directory: &str, allow_permission_prompts: bool) -> Result<ProviderInput> {
        let request = value.validate()?;
        let mut args = providers::arguments(&request, plugin_directory, None)?;
        if allow_permission_prompts && request.provider == "claude" {
            Self::host_prompts(&mut args);
        }
        let input =
            if request.provider == "gemini" { Self::antigravity_user_message(&request.input) } else { Self::prompt_frame(&request.input) };
        Ok(ProviderInput { arguments: args, standard_input: input.into_bytes() })
    }

    /// `ProviderInput.HostPrompts`: Claude's arguments rewritten so permission prompts come to this host over stdio, stdin carries
    /// stream-json, and `--allowedTools` names the task tools alone.
    pub fn host_prompts(arguments: &mut Vec<String>) {
        if let Some(none) = arguments.iter().position(|a| a == "none")
            && none > 0
            && arguments[none - 1] == "--permission-prompts"
        {
            arguments[none] = "host".into();
            arguments.extend(["--permission-prompt-tool".to_string(), "stdio".to_string()]);
        }
        if !arguments.iter().any(|a| a == "--input-format") {
            arguments.extend(["--input-format".to_string(), "stream-json".to_string()]);
        }
        let tools = crate::flow_steps::TASK_TOOLS.join(",");
        match arguments.iter().position(|a| a == "--allowedTools") {
            Some(at) if at + 1 < arguments.len() => arguments[at + 1] = tools,
            Some(_) => {}
            None => arguments.extend(["--allowedTools".to_string(), tools]),
        }
    }

    /// `ProviderInput.PromptFrame`: the stream-json user frame (its message names its role, which Claude Code 2.1.289 requires).
    pub fn prompt_frame(input: &str) -> String {
        format!("{}\n", text_json::wire(&json!({"type": "user", "message": {"role": "user", "content": input}})))
    }

    /// `ProviderInput.AntigravityUserMessage`: the one stdin line of an Antigravity run.
    pub fn antigravity_user_message(text: &str) -> String {
        format!("{}\n", text_json::wire(&json!({"event": "user", "message": {"content": text}})))
    }

    /// `ProviderInput.PiStartup`: `get_state`, then the prompt with the pictures as `ImageContent`.
    pub fn pi_startup(text: &str, images: &[(String, String)]) -> String {
        let prompt = if images.is_empty() {
            json!({"type": "prompt", "message": text})
        } else {
            let pictures: Vec<Value> =
                images.iter().map(|(media, data)| json!({"type": "image", "data": data, "mimeType": media})).collect();
            json!({"type": "prompt", "message": text, "images": pictures})
        };
        format!("{{\"type\":\"get_state\"}}\n{}\n", text_json::wire(&prompt))
    }

    /// `ProviderInput.PiSteer`.
    pub fn pi_steer(text: &str) -> String {
        format!("{}\n", text_json::wire(&json!({"type": "steer", "message": text})))
    }
}
