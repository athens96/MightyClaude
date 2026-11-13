//! What a run's failure says: C# `CliAuthFailure` (CliLoginRecovery.cs:9-146, the signals a run reads; the recovery itself is W6) and
//! `BedrockSettings.RuntimeFailureGuidance` (BedrockSettings.cs:22-36).

use std::sync::OnceLock;

use regex::Regex;
use serde_json::Value;

use crate::meta;
use crate::strings;

fn bounded(text: &str) -> String {
    let mut units = 0;
    let mut cut = String::new();
    for c in text.chars() {
        units += c.len_utf16();
        if units > 16_384 {
            break;
        }
        cut.push(c);
    }
    cut.to_lowercase()
}

fn external_denial(text: &str) -> bool {
    text.contains("bedrock") || text.contains("service control policy") || text.contains("explicit deny")
}

/// `CliAuthFailure.Claude(text)`.
pub fn claude_text(text: &str) -> bool {
    let value = bounded(text);
    !external_denial(&value)
        && (["please run /login", "oauth token has expired", "oauth token revoked", "oauth session expired", "invalid api key"]
            .iter()
            .any(|phrase| value.contains(phrase))
            || value.contains("401") && value.contains("authentication_error"))
}

/// `CliAuthFailure.Codex`.
pub fn codex(text: &str) -> bool {
    let value = bounded(text);
    !external_denial(&value)
        && !value.trim_start().starts_with("reconnecting")
        && (value.contains("401") && value.contains("unauthorized")
            || ["not logged in", "please log in", "please login", "log in again", "sign in again", "refresh token"]
                .iter()
                .any(|phrase| value.contains(phrase)))
}

/// `CliAuthFailure.Antigravity`.
pub fn antigravity(text: &str) -> bool {
    let value = bounded(text);
    [
        "authentication required. run ",
        "print mode: auth error",
        "authentication failed or timed out",
        "please sign in",
        "not signed in",
        "sign in to view",
        "launch the cli without arguments to sign in",
    ]
    .iter()
    .any(|phrase| value.contains(phrase))
}

/// `CliAuthFailure.Pi`.
pub fn pi(text: &str) -> bool {
    let value = bounded(text);
    ["no api key", "no models available", "use /login", "run /login", "please /login"].iter().any(|phrase| value.contains(phrase))
}

/// `CliAuthFailure.AntigravitySucceeded`.
pub fn antigravity_succeeded(line: &str) -> bool {
    if !line.contains("\"result\"") || !line.trim_start().starts_with('{') {
        return false;
    }
    let Ok(root) = serde_json::from_str::<Value>(line) else { return false };
    root.is_object()
        && meta::text(&root, "event") == Some("result")
        && meta::text_of(meta::prop(&root, "result"), "status").map(str::to_uppercase).as_deref() == Some("SUCCESS")
}

/// `CliAuthFailure.Matches`: whether a run's failure text is its provider's lost sign-in.
pub fn matches(provider: &str, text: &str) -> bool {
    match provider {
        "claude" => claude_text(text),
        "codex" => codex(text),
        "gemini" => antigravity(text),
        _ => false,
    }
}

fn is_error_result(value: &Value) -> bool {
    meta::text(value, "type") == Some("result")
        && (meta::flag(value, "is_error") || meta::text(value, "subtype").is_some_and(|s| s.starts_with("error")))
}

fn assistant_texts(value: &Value) -> Vec<String> {
    match meta::prop(value, "message").filter(|m| m.is_object()).and_then(|m| meta::prop(m, "content")) {
        Some(Value::Array(blocks)) => blocks
            .iter()
            .filter(|block| meta::text(block, "type") == Some("text"))
            .map(|block| meta::text(block, "text").unwrap_or("").to_string())
            .collect(),
        _ => vec![],
    }
}

fn result_texts(value: &Value) -> Vec<String> {
    let mut texts: Vec<String> = match meta::prop(value, "errors") {
        Some(Value::Array(items)) => items.iter().filter_map(Value::as_str).map(str::to_string).collect(),
        _ => vec![],
    };
    if let Some(result) = meta::text(value, "result") {
        texts.push(result.to_string());
    }
    texts
}

/// `CliAuthFailure.Claude(JsonElement)`: a Claude frame that is a lost sign-in (an assistant message the CLI marked, or an error
/// result whose words say so), never an external policy's denial.
pub fn claude(value: &Value) -> bool {
    if meta::text(value, "type") == Some("assistant")
        && let Some(error) = meta::text(value, "error").filter(|e| !e.is_empty())
    {
        let texts = assistant_texts(value);
        return !texts.iter().any(|text| external_denial(&bounded(text)))
            && (error == "authentication_failed" || texts.iter().any(|t| claude_text(t)));
    }
    if !is_error_result(value) {
        return false;
    }
    let errors = result_texts(value);
    !errors.iter().any(|text| external_denial(&bounded(text))) && errors.iter().any(|t| claude_text(t))
}

/// `CliAuthFailure.ClaudeFailureText`: the text of an assistant frame the CLI marked as a lost sign-in.
pub fn claude_failure_text(value: &Value) -> Option<String> {
    (meta::text(value, "type") == Some("assistant") && meta::text(value, "error") == Some("authentication_failed") && claude(value))
        .then(|| assistant_texts(value).join("\n").trim().to_string())
}

/// `CliAuthFailure.Claude(value, repeating)`: an error result repeating the turn's message the CLI marked as a lost sign-in.
pub fn claude_repeating(value: &Value, repeating: Option<&str>) -> bool {
    let Some(repeating) = repeating.filter(|r| !r.is_empty()) else { return false };
    if !is_error_result(value) {
        return false;
    }
    let texts = result_texts(value);
    !texts.iter().any(|text| external_denial(&bounded(text))) && texts.iter().any(|text| text.trim() == repeating)
}

fn api_error_403() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| Regex::new(r"(?i)(?:^|\bAPI Error:\s*)403\s+").expect("pattern"))
}

fn scp_denial() -> &'static Regex {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    PATTERN.get_or_init(|| {
        Regex::new(r"(?i)\AUser:\s+\S+\s+is not authorized to perform:?\s+bedrock:(?:InvokeModel|InvokeModelWithResponseStream)(?:\s+on resource:\s+\S+)?\s+(?:with|because of) an explicit deny in a service control policy(?::\s+arn:aws:organizations::[0-9]{12}:policy/o-[a-z0-9]{10,32}/service_control_policy/p-[a-z0-9]{8,128})?\.?\z")
            .expect("pattern")
    })
}

/// `BedrockSettings.RuntimeFailureGuidance`: the guidance that replaces a Bedrock 403 an organization's service control policy
/// caused, or None for any other text.
pub fn bedrock_guidance(text: &str) -> Option<String> {
    if text.len() > 65_536 {
        return None;
    }
    // `403\s+(?=\{)`: the regex crate has no look-ahead, so each match is checked for the brace that must follow it.
    let marker = api_error_403().find_iter(text).find(|m| text[m.end()..].starts_with('{'))?;
    let body: Value = serde_json::from_str(&text[marker.end()..]).ok()?;
    let object = body.as_object()?;
    let message = object.get("Message").or_else(|| object.get("message"))?.as_str()?;
    for key in ["__type", "code", "Code"] {
        if let Some(code) = meta::text(&body, key) {
            let last = code.split('#').rfind(|s| !s.is_empty());
            let first = last.and_then(|l| l.split(':').find(|s| !s.is_empty()));
            if first != Some("AccessDeniedException") {
                return None;
            }
        }
    }
    scp_denial().is_match(message.trim()).then(|| strings::get("bedrock.scpDenied"))
}
