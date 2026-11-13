//! C# `ToolPermissionRequest` (ToolPermissions.cs:12): one waiting (or settled) tool-permission request as the pane shows it. The run
//! engine (mighty-run's permission channel) makes it, the cards (mighty-composer) draw it and the host pushes it (`permission`), so it
//! has one definition here, in the lowest crate both read.

use serde_json::Value;

use crate::json::JsonOut;

/// `ClaudePlanMode.ToolName`: the tool whose request carries a plan.
pub const PLAN_TOOL: &str = "ExitPlanMode";

/// C# `ToolPermissionRequest(Id, RunId, ToolUseId, ToolName, InputJson, Summary, Reason?, BlockedPath?, State, CanAllow,
/// CanAnswerQuestions, CanAnswerPlan, ReceivedAt?)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolPermissionRequest {
    pub id: String,
    pub run_id: String,
    pub tool_use_id: String,
    pub tool_name: String,
    pub input_json: String,
    pub summary: String,
    pub reason: Option<String>,
    pub blocked_path: Option<String>,
    pub state: String,
    pub can_allow: bool,
    pub can_answer_questions: bool,
    pub can_answer_plan: bool,
    pub received_at: Option<String>,
}

impl ToolPermissionRequest {
    /// `new ToolPermissionRequest(id, runId, toolUseId, toolName, inputJson, summary)` with C#'s defaults.
    pub fn new(id: &str, run_id: &str, tool_use_id: &str, tool_name: &str, input_json: &str, summary: &str) -> ToolPermissionRequest {
        ToolPermissionRequest {
            id: id.into(),
            run_id: run_id.into(),
            tool_use_id: tool_use_id.into(),
            tool_name: tool_name.into(),
            input_json: input_json.into(),
            summary: summary.into(),
            reason: None,
            blocked_path: None,
            state: "pending".into(),
            can_allow: true,
            can_answer_questions: false,
            can_answer_plan: false,
            received_at: None,
        }
    }

    /// `ToolPermissionRequest.Plan` (`ClaudePlanMode.Plan(inputJson)`, ClaudePlanMode.cs:98): the Markdown plan of an ExitPlanMode
    /// request — its input an object whose `plan` is a string that is not blank.
    pub fn plan(&self) -> Option<String> {
        if self.tool_name != PLAN_TOOL || !self.input_json.trim_start().starts_with('{') {
            return None;
        }
        let value: Value = serde_json::from_str(&self.input_json).ok()?;
        value.as_object()?.get("plan")?.as_str().filter(|text| !text.trim().is_empty()).map(str::to_string)
    }

    /// As `Wire.Json` writes the record.
    pub fn write(&self, out: &mut JsonOut) {
        out.begin_object();
        out.prop_str("id", Some(&self.id));
        out.prop_str("runId", Some(&self.run_id));
        out.prop_str("toolUseId", Some(&self.tool_use_id));
        out.prop_str("toolName", Some(&self.tool_name));
        out.prop_str("inputJson", Some(&self.input_json));
        out.prop_str("summary", Some(&self.summary));
        out.prop_str("reason", self.reason.as_deref());
        out.prop_str("blockedPath", self.blocked_path.as_deref());
        out.prop_str("state", Some(&self.state));
        out.prop_bool("canAllow", self.can_allow);
        out.prop_bool("canAnswerQuestions", self.can_answer_questions);
        out.prop_bool("canAnswerPlan", self.can_answer_plan);
        out.prop_str("receivedAt", self.received_at.as_deref());
        out.end_object();
    }

    pub fn to_json(&self) -> String {
        let mut out = JsonOut::new();
        self.write(&mut out);
        out.finish()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_plan_is_an_exit_plan_mode_requests_text() {
        let mut request = ToolPermissionRequest::new("r", "p", "t", PLAN_TOOL, r##" {"plan":"# Plan"}"##, "s");
        assert_eq!(request.plan().as_deref(), Some("# Plan"));
        request.input_json = r#"{"plan":"  "}"#.into();
        assert_eq!(request.plan(), None);
        request.input_json = r#"["plan"]"#.into();
        assert_eq!(request.plan(), None);
        request.tool_name = "Bash".into();
        request.input_json = r#"{"plan":"x"}"#.into();
        assert_eq!(request.plan(), None);
        assert_eq!(
            ToolPermissionRequest::new("a", "b", "c", "Bash", "{}", "d").to_json(),
            r#"{"id":"a","runId":"b","toolUseId":"c","toolName":"Bash","inputJson":"{}","summary":"d","state":"pending","canAllow":true,"canAnswerQuestions":false,"canAnswerPlan":false}"#
        );
    }
}
