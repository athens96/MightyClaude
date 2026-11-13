//! The card presenters the page's cards read (C# Core): a tool permission's title, headline and fields
//! (`ToolPermissionPresentation.cs`, with the bar's copy of `ToolPermissionStrings.cs`), an AskUserQuestion request
//! (`UserQuestionnaire.cs`), and what the plan card and the background strip say (`PlanCardSupport.cs`).

use std::collections::{HashMap, HashSet};

use mighty_core::locale::Locale;
use mighty_core::model::BackgroundWork;
use mighty_core::normalize;
use serde_json::Value;

/// C# `ToolPermissionField(Label, Value, Code)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolPermissionField {
    pub label: String,
    pub value: String,
    pub code: bool,
}

/// C# `ToolPermissionPresentation` (ToolPermissionPresentation.cs:7).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolPermissionPresentation {
    pub title: String,
    pub headline: Option<String>,
    pub fields: Vec<ToolPermissionField>,
}

/// C# `ToolPermissionPresentation.MaximumFields`.
pub const MAXIMUM_FIELDS: usize = 10;

/// A JSON object's properties in document order with each value's text as written (`JsonElement.EnumerateObject` and
/// `GetRawText`), duplicates kept.
struct RawObject(Vec<(String, Box<serde_json::value::RawValue>)>);

impl<'de> serde::Deserialize<'de> for RawObject {
    fn deserialize<D: serde::Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct Entries;
        impl<'de> serde::de::Visitor<'de> for Entries {
            type Value = RawObject;
            fn expecting(&self, f: &mut std::fmt::Formatter) -> std::fmt::Result {
                f.write_str("an object")
            }
            fn visit_map<A: serde::de::MapAccess<'de>>(self, mut map: A) -> Result<RawObject, A::Error> {
                let mut entries = Vec::new();
                while let Some(entry) = map.next_entry::<String, Box<serde_json::value::RawValue>>()? {
                    entries.push(entry);
                }
                Ok(RawObject(entries))
            }
        }
        deserializer.deserialize_map(Entries)
    }
}

impl RawObject {
    /// `JsonElement.TryGetProperty`: the last property of the name.
    fn raw(&self, name: &str) -> Option<&str> {
        self.0.iter().rev().find(|(key, _)| key == name).map(|(_, raw)| raw.get())
    }
    fn value(&self, name: &str) -> Option<Value> {
        self.raw(name).and_then(|raw| serde_json::from_str(raw).ok())
    }
    /// A number's literal as written (`GetRawText`).
    fn number(&self, name: &str) -> Option<String> {
        self.value(name).filter(Value::is_number).and(self.raw(name)).map(str::to_string)
    }
}

impl ToolPermissionPresentation {
    /// C# `ToolPermissionPresentation.Make` (ToolPermissionPresentation.cs:18).
    pub fn make(tool_name: &str, input_json: &str, l: &Locale) -> Self {
        let Ok(input) = serde_json::from_str::<RawObject>(input_json) else {
            return ToolPermissionPresentation { title: l.get("permission.tool.tool"), headline: None, fields: vec![] };
        };
        let strings: std::collections::HashMap<&str, String> = input
            .0
            .iter()
            .filter_map(|(key, _)| input.value(key).and_then(|v| v.as_str().map(|s| (key.as_str(), s.to_string()))))
            .collect();
        let text = |name: &str| strings.get(name).map(String::as_str);
        let raw_number = |name: &str| input.number(name);
        let flag = |name: &str| input.value(name).and_then(|v| v.as_bool());
        let yes_no = |b: bool| l.get(if b { "permission.value.yes" } else { "permission.value.no" });
        let mut fields: Vec<ToolPermissionField> = Vec::new();
        let push = |fields: &mut Vec<ToolPermissionField>, key: &str, value: String, code: bool| {
            fields.push(ToolPermissionField { label: l.get(key), value, code })
        };
        let simple =
            |title: &str, fields: Vec<ToolPermissionField>| ToolPermissionPresentation { title: l.get(title), headline: None, fields };
        if tool_name.starts_with("mcp__") {
            let parts: Vec<&str> = tool_name.splitn(3, "__").collect();
            let server = if parts.len() >= 2 { parts[1] } else { tool_name };
            return ToolPermissionPresentation {
                title: l.get("permission.tool.mcp").replace("{server}", server),
                headline: None,
                fields: generic_fields(&input, l),
            };
        }
        match tool_name {
            "Bash" | "command_execution" => {
                let headline = text("description").filter(|d| !d.is_empty()).map(str::to_string);
                if let Some(cmd) = text("command") {
                    push(&mut fields, "permission.field.command", cmd.into(), true);
                }
                if let Some(timeout) = raw_number("timeout") {
                    push(&mut fields, "permission.field.timeout", timeout, false);
                }
                if let Some(b) = flag("run_in_background") {
                    push(&mut fields, "permission.field.runInBackground", yes_no(b), false);
                }
                ToolPermissionPresentation { title: l.get("permission.tool.bash"), headline, fields }
            }
            "file_change" => simple("permission.tool.edit", generic_fields(&input, l)),
            "Read" => {
                if let Some(path) = text("file_path") {
                    push(&mut fields, "permission.field.file", path.into(), false);
                }
                if let Some(offset) = raw_number("offset") {
                    push(&mut fields, "permission.field.offset", offset, false);
                }
                if let Some(limit) = raw_number("limit") {
                    push(&mut fields, "permission.field.limit", limit, false);
                }
                simple("permission.tool.read", fields)
            }
            "Edit" | "MultiEdit" => {
                if let Some(path) = text("file_path") {
                    push(&mut fields, "permission.field.file", path.into(), false);
                }
                if let Some(old) = text("old_string") {
                    push(&mut fields, "permission.field.oldString", old.into(), true);
                }
                if let Some(new) = text("new_string") {
                    push(&mut fields, "permission.field.newString", new.into(), true);
                }
                if let Some(b) = flag("replace_all") {
                    push(&mut fields, "permission.field.replaceAll", yes_no(b), false);
                }
                if let Some(Value::Array(edits)) = input.value("edits") {
                    push(&mut fields, "permission.field.edits", edits.len().to_string(), false);
                }
                simple("permission.tool.edit", fields)
            }
            "Write" => {
                if let Some(path) = text("file_path") {
                    push(&mut fields, "permission.field.file", path.into(), false);
                }
                if let Some(content) = text("content") {
                    push(&mut fields, "permission.field.content", content.into(), true);
                }
                simple("permission.tool.write", fields)
            }
            "NotebookEdit" => {
                if let Some(nb) = text("notebook_path") {
                    push(&mut fields, "permission.field.notebook", nb.into(), false);
                }
                if let Some(cell) = raw_number("cell_number") {
                    push(&mut fields, "permission.field.cell", cell, false);
                }
                if let Some(mode) = text("edit_mode") {
                    push(&mut fields, "permission.field.editMode", mode.into(), false);
                }
                if let Some(src) = text("new_source") {
                    push(&mut fields, "permission.field.content", src.into(), true);
                }
                simple("permission.tool.notebook", fields)
            }
            "Glob" | "Grep" => {
                if let Some(p) = text("pattern") {
                    push(&mut fields, "permission.field.pattern", p.into(), true);
                }
                if let Some(path) = text("path") {
                    push(&mut fields, "permission.field.path", path.into(), false);
                }
                if tool_name == "Grep"
                    && let Some(g) = text("glob")
                {
                    push(&mut fields, "permission.field.glob", g.into(), false);
                }
                simple(if tool_name == "Glob" { "permission.tool.glob" } else { "permission.tool.grep" }, fields)
            }
            "WebFetch" => {
                if let Some(url) = text("url") {
                    push(&mut fields, "permission.field.url", url.into(), false);
                }
                simple("permission.tool.webFetch", fields)
            }
            "WebSearch" => {
                if let Some(q) = text("query") {
                    push(&mut fields, "permission.field.query", q.into(), false);
                }
                simple("permission.tool.webSearch", fields)
            }
            "Agent" | "Task" => {
                if let Some(t) = text("subagent_type") {
                    push(&mut fields, "permission.field.subagentType", t.into(), false);
                }
                if let Some(model) = text("model") {
                    push(&mut fields, "permission.field.model", model.into(), false);
                }
                if let Some(prompt) = text("prompt") {
                    push(&mut fields, "permission.field.instructions", prompt.into(), true);
                }
                simple("permission.tool.agent", fields)
            }
            _ => simple("permission.tool.tool", generic_fields(&input, l)),
        }
    }
}

/// C# `GenericFields` (ToolPermissionPresentation.cs:149): each property by its name, at most ten, empty ones left out.
fn generic_fields(input: &RawObject, l: &Locale) -> Vec<ToolPermissionField> {
    let mut fields = Vec::new();
    for (name, raw) in input.0.iter().take(MAXIMUM_FIELDS) {
        let text = match serde_json::from_str::<Value>(raw.get()) {
            Ok(Value::String(s)) => s,
            Ok(Value::Bool(true)) => l.get("permission.value.yes"),
            Ok(Value::Bool(false)) => l.get("permission.value.no"),
            Ok(Value::Null) => String::new(),
            // A number, an object or an array: its text as written.
            _ => raw.get().to_string(),
        };
        if !text.is_empty() {
            fields.push(ToolPermissionField { label: name.clone(), value: text, code: false });
        }
        if fields.len() >= MAXIMUM_FIELDS {
            break;
        }
    }
    fields
}

/// The approval bar's copy (C# `ToolPermissionStrings`, ToolPermissionStrings.cs:11-18).
pub mod bar {
    use mighty_core::locale::Locale;

    pub fn title_template(l: &Locale) -> String {
        l.get("permission.bar.title")
    }
    pub fn waiting_count_template(l: &Locale) -> String {
        l.get("phone.questionnaire.waiting")
    }
    pub fn path_template(l: &Locale) -> String {
        l.get("permission.bar.blockedPath")
    }
    pub fn raw_json(l: &Locale) -> String {
        l.get("styles.approval.raw")
    }
    pub fn once_only_note(l: &Locale) -> String {
        l.get("permission.bar.thisRequestOnly")
    }
    pub fn cannot_allow_here(l: &Locale) -> String {
        l.get("permission.bar.cannotAllow")
    }
    pub fn button_deny(l: &Locale) -> String {
        l.get("permission.deny")
    }
    pub fn button_allow_once(l: &Locale) -> String {
        l.get("permission.allowOnce")
    }
}

// ── UserQuestionnaire ───────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `UserQuestionOption(Label, Description)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UserQuestionOption {
    pub label: String,
    pub description: String,
}

/// C# `UserQuestion(Header, Question, MultiSelect, Options)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UserQuestion {
    pub header: String,
    pub question: String,
    pub multi_select: bool,
    pub options: Vec<UserQuestionOption>,
}

/// C# `UserQuestionAnswer(SelectedOptions, CustomText?)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UserQuestionAnswer {
    pub selected_options: Vec<String>,
    pub custom_text: Option<String>,
}

/// C# `UserQuestionnaire` (UserQuestionnaire.cs:16): the complete bounded AskUserQuestion input.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UserQuestionnaire {
    pub questions: Vec<UserQuestion>,
}

pub const MAXIMUM_QUESTIONS: usize = 8;
pub const MAXIMUM_OPTIONS: usize = 12;

/// C# `Valid`: at most `maximum` UTF-8 bytes, not blank unless allowed, no control character but tab, LF and CR.
fn valid(value: &str, maximum: usize, empty: bool) -> bool {
    value.len() <= maximum
        && (empty || !value.chars().all(char::is_whitespace))
        && !value.chars().any(|c| crate::attachments::is_control(c) && !matches!(c, '\t' | '\n' | '\r'))
}

impl UserQuestionnaire {
    /// C# `UserQuestionnaire.Parse` (UserQuestionnaire.cs:20).
    pub fn parse(input: &str) -> Option<UserQuestionnaire> {
        if !input.trim_start_matches(char::is_whitespace).starts_with('{') || input.len() > 65_536 {
            return None;
        }
        let root: Value = serde_json::from_str(input).ok()?;
        let questions = root.as_object()?.get("questions")?.as_array()?;
        if questions.is_empty() || questions.len() > MAXIMUM_QUESTIONS {
            return None;
        }
        let mut result = Vec::new();
        for q in questions {
            let q = q.as_object()?;
            let header = q.get("header")?.as_str().filter(|h| valid(h, 256, false))?;
            let question = q.get("question")?.as_str().filter(|t| valid(t, 8192, false))?;
            let multi = q.get("multiSelect")?.as_bool()?;
            let options = q.get("options")?.as_array().filter(|o| o.len() <= MAXIMUM_OPTIONS)?;
            let mut choices = Vec::new();
            for option in options {
                let option = option.as_object()?;
                let label = option.get("label")?.as_str().filter(|l| valid(l, 1024, false))?;
                let description = option.get("description")?.as_str().filter(|d| valid(d, 8192, true))?;
                choices.push(UserQuestionOption { label: label.into(), description: description.into() });
            }
            if choices.iter().map(|o| o.label.as_str()).collect::<HashSet<_>>().len() != choices.len() {
                return None;
            }
            result.push(UserQuestion { header: header.into(), question: question.into(), multi_select: multi, options: choices });
        }
        if result.iter().map(|q| q.question.as_str()).collect::<HashSet<_>>().len() != result.len() {
            return None;
        }
        Some(UserQuestionnaire { questions: result })
    }

    /// C# `UserQuestionnaire.ValidateAnswers` (UserQuestionnaire.cs:52): each question's answer as the CLI takes it, or the refusal.
    pub fn validate_answers(&self, answers: &HashMap<String, UserQuestionAnswer>, l: &Locale) -> Result<HashMap<String, String>, String> {
        let asked: HashSet<&str> = self.questions.iter().map(|q| q.question.as_str()).collect();
        if asked != answers.keys().map(String::as_str).collect() {
            return Err(l.get("questionnaire.error.incomplete"));
        }
        let mut result = HashMap::new();
        for question in &self.questions {
            let answer = &answers[&question.question];
            let selected: HashSet<&str> = answer.selected_options.iter().map(String::as_str).collect();
            let custom = answer.custom_text.as_deref().map(crate::slash::dotnet_trim).unwrap_or("");
            let labels: HashSet<&str> = question.options.iter().map(|o| o.label.as_str()).collect();
            if selected.len() != answer.selected_options.len()
                || !selected.is_subset(&labels)
                || !valid(custom, 8192, true)
                || selected.is_empty() && custom.is_empty()
                || !question.multi_select && selected.len() + usize::from(!custom.is_empty()) != 1
            {
                return Err(l.get("questionnaire.error.selection"));
            }
            let mut values: Vec<&str> =
                question.options.iter().filter(|o| selected.contains(o.label.as_str())).map(|o| o.label.as_str()).collect();
            if !custom.is_empty() {
                values.push(custom);
            }
            result.insert(question.question.clone(), values.join(", "));
        }
        Ok(result)
    }
}

/// C# `GuidedQuestions.Waiting` (QuestionnaireProgress.cs:69) over the requests' `(state, can answer questions, tool, input)`.
pub fn is_answerable_question(state: &str, can_answer_questions: bool, tool_name: &str, input_json: &str) -> bool {
    state == "pending" && can_answer_questions && tool_name == "AskUserQuestion" && UserQuestionnaire::parse(input_json).is_some()
}

/// C# `ClaudePlanMode.Plan(inputJson)` (ClaudePlanMode.cs:98): the Markdown plan of an ExitPlanMode request.
pub fn plan_of(tool_name: &str, input_json: &str) -> Option<String> {
    if tool_name != "ExitPlanMode" || !input_json.trim_start_matches(char::is_whitespace).starts_with('{') {
        return None;
    }
    let value: Value = serde_json::from_str(input_json).ok()?;
    value.get("plan")?.as_str().filter(|t| !t.chars().all(char::is_whitespace)).map(str::to_string)
}

/// C# `PlanCardSupport` (PlanCardSupport.cs) as the composer and the cards read it.
pub mod plan {
    use super::*;

    /// C# `PlanOutcome` words.
    pub const APPROVED_AUTO: &str = "approvedAuto";
    pub const APPROVED_CONFIRM: &str = "approvedConfirm";
    pub const REVISED: &str = "revised";

    /// C# `PlanCardSupport.ComposerPlaces`.
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub enum ComposerPlace {
        Card,
        PanelActions,
        BarActions,
    }

    /// C# `PlanCardSupport.ComposerPlace` (PlanCardSupport.cs:51). `diagram_draws_plan`: `ComposerShowsPlanActions` — the Mighty
    /// diagram shows and the newest request is not finished (`DiagramPlanRunID`, the graph's).
    pub fn composer_place(answerable_pending_plan: bool, diagram_draws_plan: bool, guided: bool) -> Option<ComposerPlace> {
        if !answerable_pending_plan {
            return None;
        }
        if !diagram_draws_plan {
            return Some(ComposerPlace::Card);
        }
        Some(if guided { ComposerPlace::PanelActions } else { ComposerPlace::BarActions })
    }

    /// C# `PlanCardSupport.ShowsHistoryStrip`.
    pub fn shows_history_strip(mighty_diagram: bool) -> bool {
        !mighty_diagram
    }

    /// C# `PlanCardSupport.OutcomeTitle`.
    pub fn outcome_title(outcome: &str, l: &Locale) -> String {
        l.get(match outcome {
            APPROVED_AUTO => "plan.outcome.approvedAuto",
            APPROVED_CONFIRM => "plan.outcome.approvedConfirm",
            REVISED => "plan.outcome.revised",
            _ => "plan.outcome.cancelled",
        })
    }

    /// C# `PlanCardSupport.BackgroundStatus`: the header's word while the turn is over but background work runs.
    pub fn background_status(work: Option<&BackgroundWork>, l: &Locale) -> Option<String> {
        let work = work.filter(|w| crate::queue::waiting_on_background(Some(w)))?;
        Some(l.get_with("plan.background.status", &[("count", &crate::queue::running_tasks(work).to_string())]))
    }

    /// C# `PlanCardSupport.NoticeDelivered`.
    pub fn notice_delivered(status: &str) -> bool {
        matches!(status, "completed" | "failed" | "stopped")
    }

    /// C# `PlanCardSupport.ShowsBackgroundStrip` (PlanCardSupport.cs:193).
    pub fn shows_background_strip(work: Option<&BackgroundWork>, mighty: bool, style_draws_tasks: bool) -> bool {
        let Some(work) = work.filter(|_| !style_draws_tasks) else { return false };
        (work.turn_ended || mighty)
            && work
                .tasks
                .as_deref()
                .unwrap_or(&[])
                .iter()
                .any(|t| t.status.as_deref().is_some_and(|s| s == "running" || notice_delivered(s)))
    }

    /// C# `PlanCardSupport.BackgroundFinished`.
    pub fn background_finished(work: &BackgroundWork) -> usize {
        work.tasks.as_deref().unwrap_or(&[]).len() - crate::queue::running_tasks(work)
    }

    /// C# `PlanCardSupport.BackgroundCounts`: "done · running".
    pub fn background_counts(work: &BackgroundWork, l: &Locale) -> String {
        format!(
            "{} · {}",
            l.get_with("plan.background.doneCount", &[("count", &background_finished(work).to_string())]),
            l.get_with("plan.background.runningCount", &[("count", &crate::queue::running_tasks(work).to_string())])
        )
    }

    /// C# `PlanCardSupport.TimeText`: "HH:mm" at the given UTC offset (C# reads the local zone), the input when unreadable.
    pub fn time_text(iso: &str, offset_minutes: i32) -> String {
        match mighty_core::time::parse(Some(iso)) {
            Some(time) => {
                let local = time.to_chrono() + chrono::Duration::minutes(offset_minutes as i64);
                local.format("%H:%M").to_string()
            }
            None => iso.to_string(),
        }
    }

    /// C# `PlanCardSupport.ReceivedText`.
    pub fn received_text(iso: &str, offset_minutes: i32, l: &Locale) -> String {
        l.get_with("plan.card.received", &[("time", &time_text(iso, offset_minutes))])
    }

    /// C# `PlanCardSupport.Headline`: the first non-empty line without heading marks.
    pub fn headline(plan: &str) -> String {
        let line = plan.split('\n').map(crate::slash::dotnet_trim).find(|l| !l.is_empty()).unwrap_or("");
        normalize::activity_clean(Some(crate::slash::dotnet_trim(line.trim_start_matches('#'))), 240, true)
    }
}
