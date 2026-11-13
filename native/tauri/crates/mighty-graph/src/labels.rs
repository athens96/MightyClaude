//! Small labels the graph's blocks carry: the model a block names before its responses do (C# `GraphModelLabel`,
//! MightyCode.Core/GraphModelLabel.cs), a context compaction's block (`ContextCompaction`, ContextCompaction.cs) and the agent's own name
//! (`ProviderMark.Label`, ProviderMark.cs:36).

use mighty_core::locale::Locale;
use serde_json::Value;

/// `ProviderMark.Label`: the short name the provider's mark stands before (macOS `ProviderOptions.label`).
pub use mighty_core::model_label::provider_label;

/// `GraphModelLabel.NodeModelLabel`: the CLI's model, else a configured one marked "설정".
pub fn node_model_label(cli_reported: Option<&str>, configured: Option<&str>, locale: &Locale) -> Option<String> {
    if let Some(reported) = cli_reported.filter(|m| !m.is_empty()) {
        return Some(reported.to_string());
    }
    configured.filter(|m| !m.is_empty() && *m != "default").map(|m| format!("{m} {}", locale.get("graph.nodeModel.configuredSuffix")))
}

/// `GraphModelLabel.AgentModelLabel`: a sub-agent's model before its responses name the one that answered — the resolved model, else the
/// requested alias marked "요청"; "inherit" asks for none.
pub fn agent_model_label(resolved: Option<&str>, requested: Option<&str>, locale: &Locale) -> Option<String> {
    if let Some(resolved) = resolved.filter(|m| !m.is_empty()) {
        return Some(resolved.to_string());
    }
    requested.filter(|m| !m.is_empty() && *m != "inherit").map(|m| format!("{m} {}", locale.get("graph.nodeModel.requestedSuffix")))
}

/// `ContextCompaction.Title`.
pub fn compaction_title(locale: &Locale) -> String {
    locale.get("graph.block.compact")
}

/// `ContextCompaction.CodexSummary`.
pub fn codex_compaction_summary(locale: &Locale) -> String {
    locale.get("graph.compaction.codex")
}

/// `ContextCompaction.GetTokens`: a count of at least 0 that fits an int.
fn tokens(fields: Option<&serde_json::Map<String, Value>>, key: &str) -> Option<i32> {
    let value = fields?.get(key)?;
    let Value::Number(n) = value else { return None };
    // An integer past an int is read as a double below, as `TryGetInt32` fails and `TryGetDouble` holds.
    if let Some(integer) = n.as_i64()
        && (0..=i32::MAX as i64).contains(&integer)
    {
        return Some(integer as i32);
    }
    n.as_f64().filter(|d| d.is_finite() && *d >= 0.0 && *d <= i32::MAX as f64).map(|d| d as i32)
}

/// `ContextCompaction.Format`: "N0" with "," between each three digits.
fn grouped(value: i32) -> String {
    let digits = value.to_string();
    let mut out = String::new();
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            out.push(',');
        }
        out.push(c);
    }
    out
}

/// `ContextCompaction.Duration`: "850ms", "1.3s", "2s" (.NET's "0.0" format rounds half away from zero).
fn duration(milliseconds: i32, locale: &Locale) -> String {
    if milliseconds < 1000 {
        return locale.get_with("graph.compaction.durationMs", &[("n", &milliseconds.to_string())]);
    }
    let tenths = (milliseconds as i64 + 50) / 100;
    let mut seconds = format!("{}.{}", tenths / 10, tenths % 10);
    if seconds.ends_with(".0") {
        seconds.truncate(seconds.len() - 2);
    }
    locale.get_with("graph.compaction.durationSeconds", &[("n", &seconds)])
}

/// `ContextCompaction.ClaudeSummary`: what a `compact_boundary` frame's `compact_metadata` says.
pub fn claude_compaction_summary(metadata: Option<&Value>, locale: &Locale) -> String {
    let fields = metadata.and_then(Value::as_object);
    let manual = fields.and_then(|f| f.get("trigger")).and_then(Value::as_str) == Some("manual");
    let mut parts = vec![locale.get(if manual { "graph.compaction.manual" } else { "graph.compaction.auto" })];
    let pre = tokens(fields, "pre_tokens");
    let post = tokens(fields, "post_tokens");
    match (pre, post) {
        (Some(pre), Some(post)) => {
            parts.push(locale.get_with("graph.compaction.tokenRange", &[("before", &grouped(pre)), ("after", &grouped(post))]))
        }
        (Some(pre), None) => parts.push(locale.get_with("graph.compaction.tokenFrom", &[("before", &grouped(pre))])),
        _ => {}
    }
    if let Some(count) = tokens(fields, "messages_summarized").filter(|n| *n > 0) {
        parts.push(locale.get_with("graph.compaction.messageCount", &[("count", &grouped(count))]));
    }
    if let Some(ms) = tokens(fields, "duration_ms").filter(|n| *n > 0) {
        parts.push(duration(ms, locale));
    }
    parts.join(" · ")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn numbers_read_as_dotnet_writes_them() {
        assert_eq!(grouped(1_234_567), "1,234,567");
        assert_eq!(grouped(999), "999");
        let en = Locale::new("en");
        assert!(duration(1_250, &en).contains("1.3"));
        assert!(duration(2_000, &en).contains('2') && !duration(2_000, &en).contains("2.0"));
    }
}
