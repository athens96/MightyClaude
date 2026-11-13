//! C# `ContextCompaction` (ContextCompaction.cs): the line a transcript shows when the CLI summarized its own context.

use serde_json::Value;

use crate::strings;

pub fn title() -> String {
    strings::get("graph.block.compact")
}

pub fn codex_summary() -> String {
    strings::get("graph.compaction.codex")
}

/// `GetTokens`: an integer of 0 or more, or a finite number from 0 to `int.MaxValue` cut to its whole part.
fn tokens(fields: Option<&Value>, key: &str) -> Option<i64> {
    let value = fields?.as_object()?.get(key)?;
    if let Some(n) = value.as_i64().filter(|n| i32::try_from(*n).is_ok() && *n >= 0) {
        return Some(n);
    }
    value.as_f64().filter(|d| value.is_number() && d.is_finite() && *d >= 0.0 && *d <= i32::MAX as f64).map(|d| d as i64)
}

/// `Format`: `value.ToString("N0")` with a comma every three digits whatever the culture (ContextCompaction.cs gives `N0` its own
/// `NumberFormatInfo`, `NumberGroupSeparator = ","`, `NumberGroupSizes = [3]`).
pub fn grouped(value: i64) -> String {
    grouped_with(value, ",", &[3])
}

/// `value.ToString("N0", format)` for a format's group separator and `NumberGroupSizes`: the groups counted from the right, the last
/// size repeated, a size of 0 leaving the digits left of it ungrouped (Windows' "3;2;0" is `[3, 2]`, India's 12,34,567).
pub fn grouped_with(value: i64, separator: &str, sizes: &[usize]) -> String {
    let digits: Vec<char> = value.unsigned_abs().to_string().chars().collect();
    let mut groups: Vec<String> = Vec::new();
    let mut end = digits.len();
    let mut size_at = 0;
    while end > 0 {
        let size = sizes.get(size_at).or(sizes.last()).copied().unwrap_or(0);
        if size_at + 1 < sizes.len() {
            size_at += 1;
        }
        let start = if size == 0 { 0 } else { end.saturating_sub(size) };
        groups.push(digits[start..end].iter().collect());
        end = start;
    }
    groups.reverse();
    let out = groups.join(separator);
    if value < 0 { format!("-{out}") } else { out }
}

/// `value.ToString("0.0", InvariantCulture)`: one decimal, a value exactly halfway rounded away from zero as .NET rounds it.
pub fn one_decimal(value: f64) -> String {
    let exact = format!("{:.40}", value.abs());
    if let Some((_, fraction)) = exact.split_once('.') {
        let rest = &fraction[1..];
        if rest.starts_with('5') && rest[1..].bytes().all(|b| b == b'0') {
            let up = (value.abs() * 10.0).floor() + 1.0;
            let text = format!("{:.1}", up / 10.0);
            return if value < 0.0 { format!("-{text}") } else { text };
        }
    }
    format!("{value:.1}")
}

fn duration(milliseconds: i64) -> String {
    if milliseconds < 1000 {
        return strings::get_with("graph.compaction.durationMs", &[("n", &milliseconds.to_string())]);
    }
    let mut seconds = one_decimal(milliseconds as f64 / 1000.0);
    if seconds.ends_with(".0") {
        seconds.truncate(seconds.len() - 2);
    }
    strings::get_with("graph.compaction.durationSeconds", &[("n", &seconds)])
}

/// `ContextCompaction.ClaudeSummary(compact_metadata)`: manual or automatic, the tokens before (and after), the messages summarized,
/// how long it took.
pub fn claude_summary(metadata: Option<&Value>) -> String {
    let fields = metadata.filter(|m| m.is_object());
    let manual = fields.and_then(|f| f.get("trigger")).and_then(Value::as_str) == Some("manual");
    let mut parts = vec![strings::get(if manual { "graph.compaction.manual" } else { "graph.compaction.auto" })];
    let (pre, post) = (tokens(fields, "pre_tokens"), tokens(fields, "post_tokens"));
    match (pre, post) {
        (Some(before), Some(after)) => {
            parts.push(strings::get_with("graph.compaction.tokenRange", &[("before", &grouped(before)), ("after", &grouped(after))]))
        }
        (Some(before), None) => parts.push(strings::get_with("graph.compaction.tokenFrom", &[("before", &grouped(before))])),
        _ => {}
    }
    if let Some(summarized) = tokens(fields, "messages_summarized").filter(|n| *n > 0) {
        parts.push(strings::get_with("graph.compaction.messageCount", &[("count", &grouped(summarized))]));
    }
    if let Some(ms) = tokens(fields, "duration_ms").filter(|n| *n > 0) {
        parts.push(duration(ms));
    }
    parts.join(" · ")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn numbers_read_as_dotnet_writes_them() {
        assert_eq!(grouped(1234567), "1,234,567");
        assert_eq!(grouped(12), "12");
        assert_eq!(grouped(0), "0");
        assert_eq!(grouped_with(1234567, ".", &[3]), "1.234.567");
        assert_eq!(grouped_with(1234567, "\u{a0}", &[3]), "1\u{a0}234\u{a0}567");
        assert_eq!(grouped_with(1234567, ",", &[3, 2]), "12,34,567", "India's 3;2;0");
        assert_eq!(grouped_with(1234567, ",", &[3, 0]), "1234,567", "Windows' 3 alone: one group, then none");
        assert_eq!(grouped_with(-1234, ",", &[3]), "-1,234");
        assert_eq!(grouped_with(1234, ",", &[]), "1234");
        assert_eq!(one_decimal(1.25), "1.3");
        assert_eq!(one_decimal(1.45), "1.4", "1.45 is a little below 1.45 as a double");
        assert_eq!(one_decimal(2.0), "2.0");
    }
}
