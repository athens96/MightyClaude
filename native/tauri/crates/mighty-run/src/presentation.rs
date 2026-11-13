//! How the approval card shows a request: C# `ToolPermissionPresentation` (ToolPermissionPresentation.cs) — a title, a headline and
//! named fields for each tool — and `ToolPermissionStrings` (ToolPermissionStrings.cs), the card's and the channel's copy.

use serde_json::Value;

use crate::meta;

/// C# `ToolPermissionStrings`: every value read from the shared locale files, the keys macOS reads.
pub mod strings {
    use crate::strings::get;

    pub fn bar_title_template() -> String {
        get("permission.bar.title")
    }
    pub fn bar_waiting_count_template() -> String {
        get("phone.questionnaire.waiting")
    }
    pub fn bar_path_template() -> String {
        get("permission.bar.blockedPath")
    }
    pub fn bar_raw_json() -> String {
        get("styles.approval.raw")
    }
    pub fn bar_once_only_note() -> String {
        get("permission.bar.thisRequestOnly")
    }
    pub fn bar_cannot_allow_here() -> String {
        get("permission.bar.cannotAllow")
    }
    pub fn button_deny() -> String {
        get("permission.deny")
    }
    pub fn button_allow_once() -> String {
        get("permission.allowOnce")
    }
    pub fn initialize_timed_out() -> String {
        get("claude.channel.initTimeout")
    }
    pub fn initialize_failed() -> String {
        get("claude.channel.initFailed")
    }
    pub fn malformed_control_request() -> String {
        get("claude.channel.badRequest")
    }
    pub fn too_many_requests_in_run() -> String {
        get("claude.channel.tooManyRequests")
    }
    pub fn unsupported_dialog() -> String {
        get("claude.channel.dialogUnsupported")
    }
    pub fn declined_elicitation() -> String {
        get("claude.channel.elicitationDeclined")
    }
    pub fn denied_malformed_request() -> String {
        get("claude.channel.badToolRequest")
    }
    pub fn denied_too_many_pending() -> String {
        get("claude.channel.tooManyPending")
    }
    pub fn denied_oversized_input() -> String {
        get("claude.channel.inputTooLarge")
    }
    pub fn needs_separate_input_screen() -> String {
        get("claude.channel.needsInteraction")
    }
    pub fn metadata_too_large() -> String {
        get("claude.channel.metadataTooLarge")
    }
    pub fn already_settled() -> String {
        get("permission.error.requestGone")
    }
    pub fn cannot_allow() -> String {
        get("claude.channel.cannotAllow")
    }
    pub fn title_bash() -> String {
        get("permission.tool.bash")
    }
    pub fn title_read() -> String {
        get("permission.tool.read")
    }
    pub fn title_edit() -> String {
        get("permission.tool.edit")
    }
    pub fn title_write() -> String {
        get("permission.tool.write")
    }
    pub fn title_notebook_edit() -> String {
        get("permission.tool.notebook")
    }
    pub fn title_glob() -> String {
        get("permission.tool.glob")
    }
    pub fn title_grep() -> String {
        get("permission.tool.grep")
    }
    pub fn title_web_fetch() -> String {
        get("permission.tool.webFetch")
    }
    pub fn title_web_search() -> String {
        get("permission.tool.webSearch")
    }
    pub fn title_agent() -> String {
        get("permission.tool.agent")
    }
    pub fn title_tool() -> String {
        get("permission.tool.tool")
    }
    pub fn title_mcp_template() -> String {
        get("permission.tool.mcp")
    }
    pub fn field_command() -> String {
        get("permission.field.command")
    }
    pub fn field_timeout_ms() -> String {
        get("permission.field.timeout")
    }
    pub fn field_background() -> String {
        get("permission.field.runInBackground")
    }
    pub fn field_file() -> String {
        get("permission.field.file")
    }
    pub fn field_offset() -> String {
        get("permission.field.offset")
    }
    pub fn field_limit() -> String {
        get("permission.field.limit")
    }
    pub fn field_old_string() -> String {
        get("permission.field.oldString")
    }
    pub fn field_new_string() -> String {
        get("permission.field.newString")
    }
    pub fn field_replace_all() -> String {
        get("permission.field.replaceAll")
    }
    pub fn field_edits() -> String {
        get("permission.field.edits")
    }
    pub fn field_content() -> String {
        get("permission.field.content")
    }
    pub fn field_notebook() -> String {
        get("permission.field.notebook")
    }
    pub fn field_cell() -> String {
        get("permission.field.cell")
    }
    pub fn field_edit_mode() -> String {
        get("permission.field.editMode")
    }
    pub fn field_pattern() -> String {
        get("permission.field.pattern")
    }
    pub fn field_path() -> String {
        get("permission.field.path")
    }
    pub fn field_glob() -> String {
        get("permission.field.glob")
    }
    pub fn field_url() -> String {
        get("permission.field.url")
    }
    pub fn field_question() -> String {
        get("permission.field.prompt")
    }
    pub fn field_query() -> String {
        get("permission.field.query")
    }
    pub fn field_subagent_type() -> String {
        get("permission.field.subagentType")
    }
    pub fn field_model() -> String {
        get("permission.field.model")
    }
    pub fn field_instruction() -> String {
        get("permission.field.instructions")
    }
    pub fn boolean_yes() -> String {
        get("permission.value.yes")
    }
    pub fn boolean_no() -> String {
        get("permission.value.no")
    }
}

/// C# `ToolPermissionField(Label, Value, Code)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolPermissionField {
    pub label: String,
    pub value: String,
    pub code: bool,
}

/// C# `ToolPermissionPresentation`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolPermissionPresentation {
    pub title: String,
    pub headline: Option<String>,
    pub fields: Vec<ToolPermissionField>,
}

pub const MAXIMUM_FIELDS: usize = 10;

fn field(label: String, value: &str, code: bool) -> ToolPermissionField {
    ToolPermissionField { label, value: value.into(), code }
}

fn yes_no(value: bool) -> String {
    if value { strings::boolean_yes() } else { strings::boolean_no() }
}

fn number_field(fields: &mut Vec<ToolPermissionField>, input: &Value, key: &str, label: String) {
    if let Some(value) = meta::prop(input, key).filter(|v| v.is_number()) {
        fields.push(field(label, &meta::raw_number(value), false));
    }
}

impl ToolPermissionPresentation {
    /// `ToolPermissionPresentation.Make`.
    pub fn make(tool_name: &str, input_json: &str) -> ToolPermissionPresentation {
        let Some(input) = serde_json::from_str::<Value>(input_json).ok().filter(Value::is_object) else {
            return ToolPermissionPresentation { title: strings::title_tool(), headline: None, fields: vec![] };
        };
        if tool_name.starts_with("mcp__") {
            let parts: Vec<&str> = tool_name.splitn(3, "__").collect();
            let server = if parts.len() >= 2 { parts[1] } else { tool_name };
            return ToolPermissionPresentation {
                title: strings::title_mcp_template().replace("{server}", server),
                headline: None,
                fields: generic(&input),
            };
        }
        let plain = |title: String, fields: Vec<ToolPermissionField>| ToolPermissionPresentation { title, headline: None, fields };
        match tool_name {
            "Bash" | "command_execution" => bash(&input),
            "file_change" => plain(strings::title_edit(), generic(&input)),
            "Read" => {
                let mut fields = Vec::new();
                if let Some(path) = meta::text(&input, "file_path") {
                    fields.push(field(strings::field_file(), path, false));
                }
                number_field(&mut fields, &input, "offset", strings::field_offset());
                number_field(&mut fields, &input, "limit", strings::field_limit());
                plain(strings::title_read(), fields)
            }
            "Edit" | "MultiEdit" => {
                let mut fields = Vec::new();
                if let Some(path) = meta::text(&input, "file_path") {
                    fields.push(field(strings::field_file(), path, false));
                }
                if let Some(old) = meta::text(&input, "old_string") {
                    fields.push(field(strings::field_old_string(), old, true));
                }
                if let Some(new) = meta::text(&input, "new_string") {
                    fields.push(field(strings::field_new_string(), new, true));
                }
                if let Some(Value::Bool(all)) = meta::prop(&input, "replace_all") {
                    fields.push(field(strings::field_replace_all(), &yes_no(*all), false));
                }
                if let Some(Value::Array(edits)) = meta::prop(&input, "edits") {
                    fields.push(field(strings::field_edits(), &edits.len().to_string(), false));
                }
                plain(strings::title_edit(), fields)
            }
            "Write" => {
                let mut fields = Vec::new();
                if let Some(path) = meta::text(&input, "file_path") {
                    fields.push(field(strings::field_file(), path, false));
                }
                if let Some(content) = meta::text(&input, "content") {
                    fields.push(field(strings::field_content(), content, true));
                }
                plain(strings::title_write(), fields)
            }
            "NotebookEdit" => {
                let mut fields = Vec::new();
                if let Some(notebook) = meta::text(&input, "notebook_path") {
                    fields.push(field(strings::field_notebook(), notebook, false));
                }
                number_field(&mut fields, &input, "cell_number", strings::field_cell());
                if let Some(mode) = meta::text(&input, "edit_mode") {
                    fields.push(field(strings::field_edit_mode(), mode, false));
                }
                if let Some(source) = meta::text(&input, "new_source") {
                    fields.push(field(strings::field_content(), source, true));
                }
                plain(strings::title_notebook_edit(), fields)
            }
            "Glob" | "Grep" => {
                let mut fields = Vec::new();
                if let Some(pattern) = meta::text(&input, "pattern") {
                    fields.push(field(strings::field_pattern(), pattern, true));
                }
                if let Some(path) = meta::text(&input, "path") {
                    fields.push(field(strings::field_path(), path, false));
                }
                if tool_name == "Grep"
                    && let Some(glob) = meta::text(&input, "glob")
                {
                    fields.push(field(strings::field_glob(), glob, false));
                }
                plain(if tool_name == "Glob" { strings::title_glob() } else { strings::title_grep() }, fields)
            }
            "WebFetch" => {
                let fields = meta::text(&input, "url").map(|url| vec![field(strings::field_url(), url, false)]).unwrap_or_default();
                plain(strings::title_web_fetch(), fields)
            }
            "WebSearch" => {
                let fields = meta::text(&input, "query").map(|q| vec![field(strings::field_query(), q, false)]).unwrap_or_default();
                plain(strings::title_web_search(), fields)
            }
            "Agent" | "Task" => {
                let mut fields = Vec::new();
                if let Some(kind) = meta::text(&input, "subagent_type") {
                    fields.push(field(strings::field_subagent_type(), kind, false));
                }
                if let Some(model) = meta::text(&input, "model") {
                    fields.push(field(strings::field_model(), model, false));
                }
                if let Some(prompt) = meta::text(&input, "prompt") {
                    fields.push(field(strings::field_instruction(), prompt, true));
                }
                plain(strings::title_agent(), fields)
            }
            _ => plain(strings::title_tool(), generic(&input)),
        }
    }
}

fn bash(input: &Value) -> ToolPermissionPresentation {
    let headline = meta::text(input, "description").filter(|h| !h.is_empty()).map(str::to_string);
    let mut fields = Vec::new();
    if let Some(command) = meta::text(input, "command") {
        fields.push(field(strings::field_command(), command, true));
    }
    number_field(&mut fields, input, "timeout", strings::field_timeout_ms());
    // Only a real yes or no is shown; a tool input may put anything under the name.
    if let Some(Value::Bool(background)) = meta::prop(input, "run_in_background") {
        fields.push(field(strings::field_background(), &yes_no(*background), false));
    }
    ToolPermissionPresentation { title: strings::title_bash(), headline, fields }
}

/// `GenericFields`: the input's first ten fields that say something, by their own names.
fn generic(input: &Value) -> Vec<ToolPermissionField> {
    let mut fields = Vec::new();
    for (name, value) in input.as_object().into_iter().flatten().take(MAXIMUM_FIELDS) {
        let text = match value {
            Value::String(s) => s.clone(),
            Value::Number(_) => meta::raw_number(value),
            Value::Bool(true) => strings::boolean_yes(),
            Value::Bool(false) => strings::boolean_no(),
            Value::Null => String::new(),
            _ => serde_json::to_string(value).unwrap_or_default(),
        };
        if !text.is_empty() {
            fields.push(field(name.clone(), &text, false));
        }
        if fields.len() >= MAXIMUM_FIELDS {
            break;
        }
    }
    fields
}
