//! A question card: C# `UserQuestionnaire` (UserQuestionnaire.cs), the complete bounded AskUserQuestion input and its answers;
//! `QuestionnaireProgress` and `GuidedQuestions` (QuestionnaireProgress.cs), answering one question at a time; and `AgentQuestions`
//! (AgentQuestions.cs:12-69), the card other agents' questions are drawn in. ACP's `elicitation/create` form (`AcpElicitationForm`)
//! is the ACP channel's ([`crate::acp::elicitation`]).

use std::collections::HashSet;

use indexmap::IndexMap;
use serde_json::{Map, Value};

use crate::activity::clean;
use crate::permissions::ToolPermissionRequest;
use crate::strings;
use crate::text_json;
use crate::{Result, RunError};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UserQuestionOption {
    pub label: String,
    pub description: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UserQuestion {
    pub header: String,
    pub question: String,
    pub multi_select: bool,
    pub options: Vec<UserQuestionOption>,
}

/// C# `UserQuestionAnswer(SelectedOptions, CustomText?)`.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct UserQuestionAnswer {
    pub selected_options: Vec<String>,
    pub custom_text: Option<String>,
}

impl UserQuestionAnswer {
    pub fn new(selected: &[&str], custom: Option<&str>) -> UserQuestionAnswer {
        UserQuestionAnswer { selected_options: selected.iter().map(|s| s.to_string()).collect(), custom_text: custom.map(str::to_string) }
    }
}

/// C# `UserQuestionnaire`: one to eight questions of up to twelve options (Claude asks one to four of two to four).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UserQuestionnaire {
    pub questions: Vec<UserQuestion>,
}

pub const MAXIMUM_QUESTIONS: usize = 8;
pub const MAXIMUM_OPTIONS: usize = 12;

fn valid(value: &str, maximum: usize, empty: bool) -> bool {
    value.len() <= maximum
        && (empty || !value.trim().is_empty())
        && !value.chars().any(|c| c.is_control() && !matches!(c, '\t' | '\n' | '\r'))
}

impl UserQuestionnaire {
    /// `UserQuestionnaire.Parse`: the questionnaire, or None when the input is not one the card can show whole.
    pub fn parse(input: &str) -> Option<UserQuestionnaire> {
        if !input.trim_start().starts_with('{') || input.len() > 65_536 {
            return None;
        }
        let document: Value = serde_json::from_str(input).ok()?;
        let questions = document.as_object()?.get("questions")?.as_array()?;
        if !(1..=MAXIMUM_QUESTIONS).contains(&questions.len()) {
            return None;
        }
        let mut result = Vec::new();
        for q in questions {
            let q = q.as_object()?;
            let header = q.get("header")?.as_str().filter(|h| valid(h, 256, false))?;
            let question = q.get("question")?.as_str().filter(|h| valid(h, 8192, false))?;
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

    /// `ValidateAnswers`: every question answered as its card allows (a single choice once, picks it offers, a typed answer within
    /// its bound), as the text Claude Code reads — the picks in the offered order, then the typed text, joined by ", ".
    pub fn validate_answers(&self, answers: &IndexMap<String, UserQuestionAnswer>) -> Result<IndexMap<String, String>> {
        let asked: HashSet<&str> = self.questions.iter().map(|q| q.question.as_str()).collect();
        let given: HashSet<&str> = answers.keys().map(String::as_str).collect();
        if asked != given {
            return Err(RunError::Invalid(strings::get("questionnaire.error.incomplete")));
        }
        let selection = || RunError::Invalid(strings::get("questionnaire.error.selection"));
        let mut result = IndexMap::new();
        for question in &self.questions {
            let answer = &answers[&question.question];
            let selected: HashSet<&str> = answer.selected_options.iter().map(String::as_str).collect();
            let custom = answer.custom_text.as_deref().map(str::trim).unwrap_or("");
            let labels: HashSet<&str> = question.options.iter().map(|o| o.label.as_str()).collect();
            if selected.len() != answer.selected_options.len()
                || !selected.is_subset(&labels)
                || !valid(custom, 8192, true)
                || selected.is_empty() && custom.is_empty()
                || !question.multi_select && selected.len() + usize::from(!custom.is_empty()) != 1
            {
                return Err(selection());
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

/// C# `QuestionnaireStep`: moved to the next question (None), or completed the set with its answers.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QuestionnaireStep {
    pub answers: Option<IndexMap<String, UserQuestionAnswer>>,
}

impl QuestionnaireStep {
    pub fn complete(&self) -> bool {
        self.answers.is_some()
    }
}

/// C# `QuestionnaireProgress`: an AskUserQuestion answered one question at a time.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QuestionnaireProgress {
    pub request_key: String,
    pub index: usize,
    answers: IndexMap<String, UserQuestionAnswer>,
    selected: Vec<String>,
}

impl QuestionnaireProgress {
    pub fn new(request_key: &str) -> QuestionnaireProgress {
        QuestionnaireProgress { request_key: request_key.into(), index: 0, answers: IndexMap::new(), selected: vec![] }
    }

    pub fn answers(&self) -> &IndexMap<String, UserQuestionAnswer> {
        &self.answers
    }

    /// Picks made so far for the current multi-select question.
    pub fn selected(&self) -> &[String] {
        &self.selected
    }

    pub fn current<'a>(&self, questionnaire: &'a UserQuestionnaire) -> Option<&'a UserQuestion> {
        questionnaire.questions.get(self.index)
    }

    /// A single-select tile answers the question; a multi-select tile toggles and answers nothing yet.
    pub fn choose(&mut self, label: &str, questionnaire: &UserQuestionnaire) -> Option<QuestionnaireStep> {
        let question = self.current(questionnaire)?.clone();
        if !question.options.iter().any(|o| o.label == label) {
            return None;
        }
        if !question.multi_select {
            return Some(self.record(UserQuestionAnswer::new(&[label], None), &question, questionnaire));
        }
        match self.selected.iter().position(|s| s == label) {
            Some(at) => {
                self.selected.remove(at);
            }
            None => self.selected.push(label.to_string()),
        }
        None
    }

    /// Enter in the composer: the typed text (and any toggled tiles) answer the question.
    pub fn commit(&mut self, custom_text: Option<&str>, questionnaire: &UserQuestionnaire) -> Option<QuestionnaireStep> {
        let question = self.current(questionnaire)?.clone();
        let text = custom_text.unwrap_or("").trim().to_string();
        if text.is_empty() && self.selected.is_empty() {
            return None;
        }
        let picks: Vec<String> = if question.multi_select {
            question.options.iter().map(|o| o.label.clone()).filter(|l| self.selected.contains(l)).collect()
        } else {
            vec![]
        };
        let answer = UserQuestionAnswer { selected_options: picks, custom_text: (!text.is_empty()).then_some(text) };
        Some(self.record(answer, &question, questionnaire))
    }

    /// Returns to the previous question with its earlier picks shown again.
    pub fn back(&mut self, questionnaire: &UserQuestionnaire) {
        if self.index == 0 {
            return;
        }
        self.index -= 1;
        let previous = &questionnaire.questions[self.index];
        self.selected.clear();
        if previous.multi_select
            && let Some(answer) = self.answers.get(&previous.question)
        {
            self.selected.extend(answer.selected_options.iter().cloned());
        }
    }

    fn record(&mut self, answer: UserQuestionAnswer, question: &UserQuestion, questionnaire: &UserQuestionnaire) -> QuestionnaireStep {
        self.answers.insert(question.question.clone(), answer);
        // The last question stays on screen until the send succeeds, so its picks stay too.
        if self.index + 1 >= questionnaire.questions.len() {
            return QuestionnaireStep { answers: Some(self.answers.clone()) };
        }
        self.selected.clear();
        self.index += 1;
        QuestionnaireStep { answers: None }
    }
}

/// C# `GuidedQuestions`: the agent's question a pane in a style answers from its style panel.
pub mod guided {
    use super::*;

    /// The first answerable AskUserQuestion waiting in the pane, read whole.
    pub fn waiting(requests: &[ToolPermissionRequest]) -> Option<(ToolPermissionRequest, UserQuestionnaire)> {
        requests.iter().find_map(|request| {
            (request.state == "pending" && request.can_answer_questions && request.tool_name == "AskUserQuestion")
                .then(|| UserQuestionnaire::parse(&request.input_json).map(|q| (request.clone(), q)))
                .flatten()
        })
    }

    /// What the permission card shows beside a style panel: every waiting request but the question the panel answers.
    pub fn visible(requests: &[ToolPermissionRequest]) -> Vec<ToolPermissionRequest> {
        let mut pending: Vec<ToolPermissionRequest> = requests.iter().filter(|r| r.state == "pending").cloned().collect();
        if let Some((answering, _)) = waiting(&pending)
            && let Some(at) = pending.iter().position(|r| *r == answering)
        {
            pending.remove(at);
        }
        pending
    }
}

/// C# `AgentQuestions`: another agent's question in the shape the card reads (Claude's AskUserQuestion input).
pub mod agent {
    use super::*;

    pub const TOOL_NAME: &str = "AskUserQuestion";

    /// `AgentQuestions.Item`.
    #[derive(Debug, Clone, PartialEq, Eq)]
    pub struct Item {
        pub header: String,
        pub question: String,
        pub multi_select: bool,
        pub options: Vec<(String, String)>,
    }

    /// `AgentQuestions.Text`: no control characters, bounded, never blank.
    pub fn text(value: Option<&str>, limit: usize, single_line: bool) -> Option<String> {
        Some(clean(value, limit, single_line)).filter(|t| !t.is_empty())
    }

    /// `AgentQuestions.Request`: the card's request, or None when the card cannot show these questions whole.
    pub fn request(id: &str, run_id: &str, tool_use_id: &str, items: &[Item]) -> Option<ToolPermissionRequest> {
        let first = items.first()?;
        let questions: Vec<Value> = items
            .iter()
            .map(|item| {
                let options: Vec<Value> = item
                    .options
                    .iter()
                    .map(|(label, description)| {
                        let mut option = Map::new();
                        option.insert("description".into(), Value::String(description.clone()));
                        option.insert("label".into(), Value::String(label.clone()));
                        Value::Object(option)
                    })
                    .collect();
                let mut question = Map::new();
                question.insert("header".into(), Value::String(item.header.clone()));
                question.insert("multiSelect".into(), Value::Bool(item.multi_select));
                question.insert("options".into(), Value::Array(options));
                question.insert("question".into(), Value::String(item.question.clone()));
                Value::Object(question)
            })
            .collect();
        let mut root = Map::new();
        root.insert("questions".into(), Value::Array(questions));
        let json = text_json::relaxed(&Value::Object(root), true);
        UserQuestionnaire::parse(&json)?;
        let mut request = ToolPermissionRequest::new(id, run_id, tool_use_id, TOOL_NAME, &json, &clean(Some(&first.question), 1_000, true));
        request.can_allow = false;
        request.can_answer_questions = true;
        Some(request)
    }

    /// `AgentQuestions.Answers`: the answers checked against the card's own questions — what was picked, in the offered order, and
    /// what was typed.
    pub fn answers(
        request: &ToolPermissionRequest,
        answers: &IndexMap<String, UserQuestionAnswer>,
    ) -> Result<IndexMap<String, (Vec<String>, String)>> {
        let questionnaire = UserQuestionnaire::parse(&request.input_json)
            .filter(|_| request.can_answer_questions)
            .ok_or_else(|| RunError::State(strings::get("permission.error.noAnswers")))?;
        questionnaire.validate_answers(answers)?;
        let mut result = IndexMap::new();
        for question in &questionnaire.questions {
            let answer = answers.get(&question.question).cloned().unwrap_or_default();
            let selected: HashSet<&str> = answer.selected_options.iter().map(String::as_str).collect();
            let picked = question.options.iter().map(|o| o.label.clone()).filter(|l| selected.contains(l.as_str())).collect();
            result.insert(question.question.clone(), (picked, answer.custom_text.as_deref().unwrap_or("").trim().to_string()));
        }
        Ok(result)
    }
}
